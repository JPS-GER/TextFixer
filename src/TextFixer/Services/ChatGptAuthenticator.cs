using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TextFixer.Services;

/// <summary>
/// ChatGPT plan sign-in for a local app: public client, PKCE S256, loopback redirect on 127.0.0.1,
/// dynamic registration on first sign-in, serialized refresh with rotated refresh tokens, revocation.
/// Tokens stay in this class and the encrypted store; they never go to logs or the UI.
/// </summary>
internal sealed class ChatGptAuthenticator
{
    private static readonly Regex ClientIdPattern = new("^[A-Za-z0-9_-]{1,200}$", RegexOptions.CultureInvariant);

    private readonly CredentialStore _store;
    private readonly OpenIdProvider _openId;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _signInGate = new(1, 1);

    public ChatGptAuthenticator(CredentialStore store, OpenIdProvider openId)
    {
        _store = store;
        _openId = openId;
    }

    public async Task<StoredCredentials> ReadStateAsync(CancellationToken cancellationToken)
    {
        using (await LockAsync(cancellationToken).ConfigureAwait(false))
        {
            return _store.Load();
        }
    }

    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        if (!await _signInGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new ChatGptServiceException("sign_in_in_progress", "A ChatGPT sign-in is already open in the browser.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ChatGptProtocol.SignInTimeout);
            try
            {
                await RunSignInAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ChatGptServiceException("sign_in_timeout", "The ChatGPT sign-in timed out. Start it again from TextFixer.");
            }
        }
        finally
        {
            _signInGate.Release();
        }
    }

    private async Task RunSignInAsync(CancellationToken cancellationToken)
    {
        var endpoints = await _openId.GetEndpointsAsync(cancellationToken).ConfigureAwait(false);

        StoredCredentials snapshot;
        string hostId;
        using (await LockAsync(cancellationToken).ConfigureAwait(false))
        {
            snapshot = _store.Load();
            hostId = _store.GetOrCreateHostId();
        }

        var savedClientId = snapshot.Unreadable ? null : snapshot.ClientId;
        var verifier = NewRandomToken();
        var state = NewRandomToken();
        var nonce = NewRandomToken();

        using var listener = LoopbackCallbackListener.Start();
        var redirectUri = listener.RedirectUri;

        var query = new List<KeyValuePair<string, string>>
        {
            new("client_id", savedClientId ?? ChatGptProtocol.RegistrationClientId),
        };
        if (savedClientId is null)
        {
            query.Add(new("agent_name_hint", ChatGptProtocol.AppName));
        }

        query.Add(new("ext_agent_host_id", hostId));
        query.Add(new("response_type", "code"));
        query.Add(new("redirect_uri", redirectUri));
        query.Add(new("scope", ChatGptProtocol.Scope));
        query.Add(new("resource", ChatGptProtocol.Resource));
        query.Add(new("state", state));
        query.Add(new("nonce", nonce));
        query.Add(new("code_challenge_method", "S256"));
        query.Add(new("code_challenge", CodeChallenge(verifier)));
        if (savedClientId is not null && !string.IsNullOrEmpty(snapshot.Email))
        {
            query.Add(new("login_hint", snapshot.Email));
        }

        var authorizeUrl = endpoints.AuthorizationEndpoint + "?" + string.Join(
            "&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        OpenSystemBrowser(authorizeUrl);

        var callback = await listener.WaitForCallbackAsync(state, cancellationToken).ConfigureAwait(false);
        if (callback.Error is not null)
        {
            throw callback.Error == "access_denied"
                ? new ChatGptServiceException("sign_in_cancelled", "The ChatGPT sign-in was cancelled.")
                : new ChatGptServiceException(callback.Error, $"The ChatGPT sign-in failed ({callback.Error}).");
        }

        if (callback.Code is null)
        {
            throw RegistrationIncomplete();
        }

        string clientId;
        if (savedClientId is null)
        {
            if (callback.ClientId is null
                || callback.ClientId == ChatGptProtocol.RegistrationClientId
                || !ClientIdPattern.IsMatch(callback.ClientId))
            {
                throw RegistrationIncomplete();
            }

            clientId = callback.ClientId;

            // The docs require saving the issued client ID before the code exchange.
            using (await LockAsync(cancellationToken).ConfigureAwait(false))
            {
                var current = _store.Load();
                if (current.Unreadable)
                {
                    current = new StoredCredentials();
                }

                current.ClientId = clientId;
                _store.Save(current);
            }
        }
        else
        {
            if (callback.ClientId is not null && callback.ClientId != savedClientId)
            {
                throw RegistrationIncomplete();
            }

            clientId = savedClientId;
        }

        var result = await ChatGptHttp.PostFormAsync(endpoints.TokenEndpoint,
        [
            new("grant_type", "authorization_code"),
            new("client_id", clientId),
            new("code", callback.Code),
            new("code_verifier", verifier),
            new("redirect_uri", redirectUri),
            new("resource", ChatGptProtocol.Resource),
        ], cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            var error = ChatGptHttp.ToException(result);
            if (error.Code == "invalid_client")
            {
                await ForgetClientAsync(cancellationToken).ConfigureAwait(false);
            }

            throw error;
        }

        var tokens = ParseTokenResponse(result.Body, previous: null);
        if (tokens.IdToken is null)
        {
            throw InvalidTokenResponse();
        }

        var identity = await _openId.VerifyIdTokenAsync(tokens.IdToken, clientId, nonce, cancellationToken).ConfigureAwait(false);
        EnsurePlanUsageScope(tokens.Scope);

        using (await LockAsync(cancellationToken).ConfigureAwait(false))
        {
            var current = _store.Load();
            if (current.Unreadable)
            {
                current = new StoredCredentials();
            }

            current.ClientId = clientId;
            current.Subject = identity.Subject;
            current.Email = identity.Email;
            current.Tokens = tokens with { IdToken = null };
            current.PendingRefresh = null;
            current.ReauthRequired = false;
            _store.Save(current);
        }
    }

    /// <summary>Returns a usable access token, refreshing it when it is about to expire.</summary>
    /// <param name="forceRefresh">Refresh even if the token looks valid (after a 401 from the API).</param>
    public async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        using (await LockAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = _store.Load();
            if (state.Unreadable)
            {
                throw new ChatGptServiceException("reauth_required", "The saved ChatGPT sign-in can't be read. Sign in again.");
            }

            if (state.PendingRefresh is not null)
            {
                await CompletePendingRefreshAsync(state, cancellationToken).ConfigureAwait(false);
            }

            if (state.ReauthRequired)
            {
                throw new ChatGptServiceException("reauth_required", "The ChatGPT sign-in has expired. Sign in again.");
            }

            if (state.Tokens is null || state.ClientId is null)
            {
                throw new ChatGptServiceException("not_signed_in", "Sign in to ChatGPT to check text.");
            }

            var now = DateTimeOffset.UtcNow;
            var tokens = state.Tokens;
            var expiringSoon = tokens.AccessExpiresAt - now <= ChatGptProtocol.RefreshMargin;
            var refreshAllowed = tokens.EarliestRefreshAt is null || now >= tokens.EarliestRefreshAt;
            var expired = tokens.AccessExpiresAt <= now;

            if (!forceRefresh && (!expiringSoon || (!refreshAllowed && !expired)))
            {
                return tokens.AccessToken;
            }

            await RefreshAsync(state, cancellationToken).ConfigureAwait(false);
            return state.Tokens!.AccessToken;
        }
    }

    /// <summary>Caller holds the lock. Updates <paramref name="state"/> in place and saves it.</summary>
    private async Task RefreshAsync(StoredCredentials state, CancellationToken cancellationToken)
    {
        var endpoints = await _openId.GetEndpointsAsync(cancellationToken).ConfigureAwait(false);
        var result = await ChatGptHttp.PostFormAsync(endpoints.TokenEndpoint,
        [
            new("grant_type", "refresh_token"),
            new("client_id", state.ClientId!),
            new("refresh_token", state.Tokens!.RefreshToken),
            new("resource", ChatGptProtocol.Resource),
        ], cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            var error = ChatGptHttp.ToException(result);
            if (ChatGptProtocol.ReauthenticationErrors.Contains(error.Code) || error.Code == "invalid_client")
            {
                if (error.Code == "invalid_client")
                {
                    state.ClientId = null;
                }

                state.ClearSession();
                state.ReauthRequired = true;
                _store.Save(state);
                throw new ChatGptServiceException("reauth_required", ChatGptHttp.UserMessage(error.Code, null, error.HttpStatus))
                {
                    HttpStatus = error.HttpStatus,
                    RequestId = error.RequestId,
                };
            }

            throw error;
        }

        // Checkpoint first: the old refresh token is consumed now.
        state.PendingRefresh = ParseTokenResponse(result.Body, state.Tokens);
        _store.Save(state);
        await CompletePendingRefreshAsync(state, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Caller holds the lock. Verifies a saved refresh result and promotes it.</summary>
    private async Task CompletePendingRefreshAsync(StoredCredentials state, CancellationToken cancellationToken)
    {
        var pending = state.PendingRefresh!;
        if (state.ClientId is null || state.Subject is null)
        {
            state.ClearSession();
            state.ReauthRequired = true;
            _store.Save(state);
            return;
        }

        if (pending.IdToken is not null)
        {
            IdentityClaims identity;
            try
            {
                identity = await _openId.VerifyIdTokenAsync(pending.IdToken, state.ClientId, null, cancellationToken).ConfigureAwait(false);
            }
            catch (ChatGptServiceException ex) when (ex.Code == "invalid_id_token")
            {
                state.ClearSession();
                state.ReauthRequired = true;
                _store.Save(state);
                throw new ChatGptServiceException("reauth_required", "The ChatGPT sign-in has expired. Sign in again.");
            }

            if (identity.Subject != state.Subject)
            {
                state.ClearSession();
                state.ReauthRequired = true;
                _store.Save(state);
                throw new ChatGptServiceException("reauth_required", "ChatGPT returned a different account. Sign in again.");
            }

            state.Email = identity.Email ?? state.Email;
        }

        if (!HasPlanUsageScope(pending.Scope))
        {
            state.ClearSession();
            state.ReauthRequired = true;
            _store.Save(state);
            throw PlanUsageNotGranted();
        }

        state.Tokens = pending with { IdToken = null };
        state.PendingRefresh = null;
        _store.Save(state);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        string? refreshToken;
        string? clientId;
        using (await LockAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = _store.Load();
            if (state.Unreadable)
            {
                state = new StoredCredentials();
            }

            refreshToken = state.PendingRefresh?.RefreshToken ?? state.Tokens?.RefreshToken;
            clientId = state.ClientId;
            state.ClearSession();
            state.ReauthRequired = false;
            _store.Save(state);
        }

        if (refreshToken is null || clientId is null)
        {
            return;
        }

        // Best effort: local sign-out already happened; revocation failures don't block it.
        try
        {
            var endpoints = await _openId.GetEndpointsAsync(cancellationToken).ConfigureAwait(false);
            if (endpoints.RevocationEndpoint is null)
            {
                return;
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var result = await ChatGptHttp.PostFormAsync(endpoints.RevocationEndpoint,
                    [
                        new("token", refreshToken),
                        new("token_type_hint", "refresh_token"),
                        new("client_id", clientId),
                    ], cancellationToken).ConfigureAwait(false);
                    if ((int)result.Status < 500)
                    {
                        return;
                    }
                }
                catch (ChatGptServiceException ex) when (ex.Code is "network_error" or "network_timeout")
                {
                }

                await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ChatGptServiceException)
        {
        }
    }

    private async Task ForgetClientAsync(CancellationToken cancellationToken)
    {
        using (await LockAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = _store.Load();
            if (state.Unreadable)
            {
                state = new StoredCredentials();
            }

            state.ClientId = null;
            state.ClearSession();
            _store.Save(state);
        }
    }

    private async Task<IDisposable> LockAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fileLock = await _store.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
            return new Releaser(_stateGate, fileLock);
        }
        catch
        {
            _stateGate.Release();
            throw;
        }
    }

    private static TokenSet ParseTokenResponse(JsonElement? body, TokenSet? previous)
    {
        if (body is not { ValueKind: JsonValueKind.Object } json)
        {
            throw InvalidTokenResponse();
        }

        var accessToken = JsonRead.String(json, "access_token");
        var refreshToken = JsonRead.String(json, "refresh_token") ?? previous?.RefreshToken;
        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
        {
            throw InvalidTokenResponse();
        }

        var now = DateTimeOffset.UtcNow;
        var expiresIn = JsonRead.UnixSeconds(json, "expires_in") is long seconds && seconds > 0 ? seconds : 3600;
        return new TokenSet
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            IdToken = JsonRead.String(json, "id_token"),
            Scope = JsonRead.String(json, "scope") ?? previous?.Scope,
            AccessExpiresAt = now.AddSeconds(expiresIn),
            EarliestRefreshAt = ParseInstant(json, "earliest_refresh_at"),
        };
    }

    /// <summary>earliest_refresh_at: Unix seconds or an ISO 8601 string; unparseable means "now".</summary>
    private static DateTimeOffset? ParseInstant(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds)
            && seconds is > 0 and < 253402300799)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        }

        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
        {
            return instant;
        }

        return null;
    }

    private static bool HasPlanUsageScope(string? scope) =>
        scope is not null && scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(ChatGptProtocol.PlanUsageScope);

    private static void EnsurePlanUsageScope(string? scope)
    {
        if (!HasPlanUsageScope(scope))
        {
            throw PlanUsageNotGranted();
        }
    }

    private static ChatGptServiceException PlanUsageNotGranted() =>
        new("plan_usage_not_granted", "This ChatGPT account didn't grant plan usage to TextFixer. Plan usage needs an eligible ChatGPT Plus or Pro plan.");

    private static ChatGptServiceException RegistrationIncomplete() =>
        new("registration_incomplete", "ChatGPT didn't complete the TextFixer registration. Try signing in again.");

    private static ChatGptServiceException InvalidTokenResponse() =>
        new("invalid_token_response", "ChatGPT returned an unreadable sign-in response. Try signing in again.");

    private static string NewRandomToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static string CodeChallenge(string verifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static void OpenSystemBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ChatGptServiceException("browser_unavailable", "TextFixer can't open the web browser for the ChatGPT sign-in.", ex);
        }
    }

    private sealed class Releaser(SemaphoreSlim gate, IDisposable fileLock) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                fileLock.Dispose();
                gate.Release();
            }
        }
    }
}
