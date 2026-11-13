using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TextFixer.Services;

internal sealed record OidcEndpoints(string AuthorizationEndpoint, string TokenEndpoint, string JwksUri, string? RevocationEndpoint);

internal sealed record IdentityClaims(string Subject, string? Email);

/// <summary>
/// OpenID discovery and ID token verification (RS256 against the issuer's JWKS).
/// Checks per the sign-in docs: signature, issuer, audience = issued client ID, expiration, nonce.
/// </summary>
internal sealed class OpenIdProvider
{
    private static readonly TimeSpan DiscoveryLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan KeyRefetchInterval = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private OidcEndpoints? _endpoints;
    private DateTimeOffset _endpointsFetchedAt;
    private Dictionary<string, RSAParameters>? _keys;
    private DateTimeOffset _keysFetchedAt;

    public async Task<OidcEndpoints> GetEndpointsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_endpoints is not null && DateTimeOffset.UtcNow - _endpointsFetchedAt < DiscoveryLifetime)
            {
                return _endpoints;
            }

            var result = await ChatGptHttp.GetJsonAsync(ChatGptProtocol.DiscoveryUrl, null, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess || result.Body is not { ValueKind: JsonValueKind.Object } body)
            {
                throw Unavailable();
            }

            if (JsonRead.String(body, "issuer") != ChatGptProtocol.Issuer)
            {
                throw Unavailable();
            }

            _endpoints = new OidcEndpoints(
                RequireIssuerUrl(body, "authorization_endpoint"),
                RequireIssuerUrl(body, "token_endpoint"),
                RequireIssuerUrl(body, "jwks_uri"),
                JsonRead.String(body, "revocation_endpoint") is null ? null : RequireIssuerUrl(body, "revocation_endpoint"));
            _endpointsFetchedAt = DateTimeOffset.UtcNow;
            return _endpoints;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <param name="expectedNonce">Nonce of the sign-in attempt; null for ID tokens from a refresh.</param>
    public async Task<IdentityClaims> VerifyIdTokenAsync(string idToken, string clientId, string? expectedNonce, CancellationToken cancellationToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3)
        {
            throw Invalid();
        }

        JsonElement header;
        JsonElement payload;
        byte[] signature;
        try
        {
            header = JsonRead.ParseUtf8(Base64Url.DecodeFromChars(parts[0]));
            payload = JsonRead.ParseUtf8(Base64Url.DecodeFromChars(parts[1]));
            signature = Base64Url.DecodeFromChars(parts[2]);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw Invalid();
        }

        if (JsonRead.String(header, "alg") != "RS256")
        {
            throw Invalid();
        }

        var key = await GetSigningKeyAsync(JsonRead.String(header, "kid"), cancellationToken).ConfigureAwait(false);
        using (var rsa = RSA.Create())
        {
            rsa.ImportParameters(key);
            var signedData = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (!rsa.VerifyData(signedData, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                throw Invalid();
            }
        }

        if (JsonRead.String(payload, "iss") != ChatGptProtocol.Issuer)
        {
            throw Invalid();
        }

        if (!AudienceMatches(payload, clientId))
        {
            throw Invalid();
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = JsonRead.UnixSeconds(payload, "exp") ?? throw Invalid();
        var issuedAt = JsonRead.UnixSeconds(payload, "iat") ?? throw Invalid();
        if (now > DateTimeOffset.FromUnixTimeSeconds(expiresAt) + ChatGptProtocol.ClockSkew
            || DateTimeOffset.FromUnixTimeSeconds(issuedAt) > now + ChatGptProtocol.ClockSkew)
        {
            throw Invalid();
        }

        var subject = JsonRead.String(payload, "sub");
        if (string.IsNullOrEmpty(subject))
        {
            throw Invalid();
        }

        if (expectedNonce is not null && JsonRead.String(payload, "nonce") != expectedNonce)
        {
            throw Invalid();
        }

        return new IdentityClaims(subject, JsonRead.String(payload, "email"));
    }

    private static bool AudienceMatches(JsonElement payload, string clientId)
    {
        if (!payload.TryGetProperty("aud", out var audience))
        {
            return false;
        }

        var azp = JsonRead.String(payload, "azp");
        if (azp is not null && azp != clientId)
        {
            return false;
        }

        if (audience.ValueKind == JsonValueKind.String)
        {
            return audience.GetString() == clientId;
        }

        if (audience.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = audience.EnumerateArray()
            .Where(a => a.ValueKind == JsonValueKind.String)
            .Select(a => a.GetString())
            .ToList();
        return values.Contains(clientId) && (values.Count == 1 || azp == clientId);
    }

    private async Task<RSAParameters> GetSigningKeyAsync(string? keyId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_keys is not null && TryFindKey(_keys, keyId, out var cached))
            {
                return cached;
            }

            if (_keys is not null && DateTimeOffset.UtcNow - _keysFetchedAt < KeyRefetchInterval)
            {
                throw Invalid();
            }
        }
        finally
        {
            _gate.Release();
        }

        var endpoints = await GetEndpointsAsync(cancellationToken).ConfigureAwait(false);
        var result = await ChatGptHttp.GetJsonAsync(endpoints.JwksUri, null, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess
            || result.Body is not { ValueKind: JsonValueKind.Object } body
            || !body.TryGetProperty("keys", out var keyArray)
            || keyArray.ValueKind != JsonValueKind.Array)
        {
            throw Unavailable();
        }

        var keys = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
        foreach (var jwk in keyArray.EnumerateArray())
        {
            if (JsonRead.String(jwk, "kty") != "RSA"
                || JsonRead.String(jwk, "use") is { } use && use != "sig"
                || JsonRead.String(jwk, "alg") is { } alg && alg != "RS256")
            {
                continue;
            }

            var modulus = JsonRead.String(jwk, "n");
            var exponent = JsonRead.String(jwk, "e");
            if (modulus is null || exponent is null)
            {
                continue;
            }

            try
            {
                keys[JsonRead.String(jwk, "kid") ?? string.Empty] = new RSAParameters
                {
                    Modulus = Base64Url.DecodeFromChars(modulus),
                    Exponent = Base64Url.DecodeFromChars(exponent),
                };
            }
            catch (FormatException)
            {
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _keys = keys;
            _keysFetchedAt = DateTimeOffset.UtcNow;
            return TryFindKey(keys, keyId, out var key) ? key : throw Invalid();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool TryFindKey(Dictionary<string, RSAParameters> keys, string? keyId, out RSAParameters key)
    {
        if (keyId is not null)
        {
            return keys.TryGetValue(keyId, out key);
        }

        if (keys.Count == 1)
        {
            key = keys.Values.First();
            return true;
        }

        key = default;
        return false;
    }

    private static string RequireIssuerUrl(JsonElement body, string name)
    {
        var value = JsonRead.String(body, name);
        if (value is null
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.GetLeftPart(UriPartial.Authority) != ChatGptProtocol.Issuer)
        {
            throw Unavailable();
        }

        return value;
    }

    private static ChatGptServiceException Invalid() =>
        new("invalid_id_token", "ChatGPT returned an identity that TextFixer can't verify. Sign in again.");

    private static ChatGptServiceException Unavailable() =>
        new("identity_verification_unavailable", "ChatGPT sign-in settings couldn't be loaded. Try again.");
}
