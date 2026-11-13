using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TextFixer.Services;

internal sealed record TokenSet
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }

    /// <summary>Only kept while a refresh checkpoint waits for identity verification.</summary>
    public string? IdToken { get; init; }
    public string? Scope { get; init; }
    public DateTimeOffset AccessExpiresAt { get; init; }
    public DateTimeOffset? EarliestRefreshAt { get; init; }
}

internal sealed class StoredCredentials
{
    public int Version { get; set; } = 1;

    /// <summary>Issued client ID (oaiapp_...). Kept across sign-outs and reused for later sign-ins.</summary>
    public string? ClientId { get; set; }
    public string? Subject { get; set; }
    public string? Email { get; set; }
    public TokenSet? Tokens { get; set; }

    /// <summary>Rotated tokens saved before verification, so a consumed refresh token is never lost.</summary>
    public TokenSet? PendingRefresh { get; set; }
    public bool ReauthRequired { get; set; }

    [JsonIgnore]
    public bool Unreadable { get; set; }

    public void ClearSession()
    {
        Subject = null;
        Email = null;
        Tokens = null;
        PendingRefresh = null;
    }
}

/// <summary>
/// DPAPI-encrypted credential file plus an unencrypted host ID file under %LOCALAPPDATA%\TextFixer\ChatGPT.
/// Writes are atomic (temp file + replace). A lock file serializes access across processes.
/// </summary>
internal sealed class CredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Regex UuidV4HostId = new(
        "^urn:uuid:[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
        RegexOptions.CultureInvariant);

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    private readonly string _directory;

    public CredentialStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TextFixer", "ChatGPT");
    }

    private string CredentialsPath => Path.Combine(_directory, "credentials.bin");
    private string HostPath => Path.Combine(_directory, "host.json");
    private string LockPath => Path.Combine(_directory, "credentials.lock");

    public StoredCredentials Load()
    {
        if (!File.Exists(CredentialsPath))
        {
            return new StoredCredentials();
        }

        byte[]? plaintext = null;
        try
        {
            plaintext = Dpapi.Unprotect(File.ReadAllBytes(CredentialsPath));
            return JsonSerializer.Deserialize<StoredCredentials>(plaintext, JsonOptions)
                ?? new StoredCredentials { Unreadable = true };
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return new StoredCredentials { Unreadable = true };
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public void Save(StoredCredentials credentials)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials, JsonOptions);
        try
        {
            WriteAtomic(CredentialsPath, Dpapi.Protect(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Stable per-installation host identifier (urn:uuid form accepted by the docs).</summary>
    public string GetOrCreateHostId()
    {
        if (File.Exists(HostPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(HostPath));
                var id = JsonRead.String(document.RootElement, "id");
                if (id is not null && UuidV4HostId.IsMatch(id))
                {
                    return id;
                }
            }
            catch (JsonException)
            {
            }

            throw new ChatGptServiceException(
                "host_identity_invalid",
                $"The TextFixer host ID file is damaged: {HostPath}");
        }

        var hostId = "urn:uuid:" + Guid.NewGuid().ToString("D");
        WriteAtomic(HostPath, JsonSerializer.SerializeToUtf8Bytes(new { version = 1, id = hostId }));
        return hostId;
    }

    public async Task<IDisposable> AcquireLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                // A file pending deletion (DeleteOnClose) can briefly deny access.
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void WriteAtomic(string path, byte[] data)
    {
        Directory.CreateDirectory(_directory);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }
}
