namespace TextFixer.Services;

/// <summary>
/// Error from the ChatGPT service layer. <see cref="Exception.Message"/> is short, user-facing text
/// and never contains tokens. <see cref="Code"/> is the protocol or local error code.
/// </summary>
public sealed class ChatGptServiceException : Exception
{
    public ChatGptServiceException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }

    /// <summary>Offending request field reported by the API (error.param), if any.</summary>
    public string? ApiParam { get; init; }

    public int? HttpStatus { get; init; }

    /// <summary>x-request-id of the failed API call, for diagnostics.</summary>
    public string? RequestId { get; init; }

    /// <summary>The user has to sign in (first time or after the sign-in became unusable).</summary>
    public bool RequiresSignIn => Code is "not_signed_in" or "reauth_required";

    /// <summary>Plan usage is exhausted or unavailable; show "Manage usage" (<see cref="ChatGptReviewService.ManageUsageUrl"/>) as the primary action.</summary>
    public bool IsUsageLimit => Code is ChatGptProtocol.UsageLimitError or ChatGptProtocol.UsageUnavailableError;
}
