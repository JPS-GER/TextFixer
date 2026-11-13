namespace TextFixer.Contracts;

public sealed record ReviewFinding(string Kind, string Explanation, string[] Quotes, string? SuggestedRewrite);
public sealed record ReviewResult(string CorrectedText, ReviewFinding[] Findings);
public sealed record ConnectionStatus(bool Connected, string Message);

public interface IReviewService
{
    Task<ConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task SignInAsync(CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
    Task<ReviewResult> CheckAsync(string text, CancellationToken cancellationToken = default);
}
