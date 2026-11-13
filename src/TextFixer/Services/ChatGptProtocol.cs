namespace TextFixer.Services;

/// <summary>
/// Protocol constants for ChatGPT plan usage ("Sign in with ChatGPT", open-source/local flow).
/// Source: https://developers.openai.com/siwc/token-sharing-open-source/ (sign-in, token-reference,
/// profiles-and-sessions, models-and-inference, preview-limitations, errors-and-recovery).
/// The program is a preview; keep every protocol value in this file.
/// </summary>
internal static class ChatGptProtocol
{
    public const string Issuer = "https://auth.openai.com";
    public const string DiscoveryUrl = Issuer + "/.well-known/openid-configuration";

    /// <summary>client_id used only for the first registration; the callback returns the issued client ID.</summary>
    public const string RegistrationClientId = "dynamic_agent_client";
    public const string AppName = "TextFixer";

    public const string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    public const string PlanUsageScope = "chatgpt.tokens.use.direct";
    public const string Resource = "https://api.openai.com/v1";
    public const string ApiBase = "https://api.openai.com/v1";

    public const string CallbackHost = "127.0.0.1";
    public const string CallbackPath = "/auth/callback";
    public const int PreferredCallbackPort = 1455;

    public const string ManageUsageUrl = "https://chatgpt.com/settings/usage";

    public const string UnsupportedCapabilityError = "subscription_sharing_unsupported_capability";
    public const string UsageLimitError = "subscription_sharing_usage_limit_exceeded";
    public const string UsageUnavailableError = "subscription_sharing_usage_unavailable";

    /// <summary>Refresh errors that require clearing tokens and signing in again (errors-and-recovery).</summary>
    public static readonly string[] ReauthenticationErrors =
    [
        "invalid_grant",
        "invalid_refresh_token",
        "token_expired",
        "refresh_token_expired",
        "refresh_token_invalidated",
        "refresh_token_reused",
    ];

    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan InferenceTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Fact checks search the web for every claim and take longer.</summary>
    public static readonly TimeSpan FactCheckTimeout = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan CallbackRequestTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
}
