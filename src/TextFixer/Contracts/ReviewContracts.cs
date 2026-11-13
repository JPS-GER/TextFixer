namespace TextFixer.Contracts;

public sealed record ReviewFinding(string Kind, string Explanation, string[] Quotes, string? SuggestedRewrite);
/// <summary>A change made in <see cref="ReviewResult.CorrectedText"/>. Start/End are UTF-16 offsets into the original text.</summary>
public sealed record ReviewCorrection(int Start, int End, string After, string Category);
/// <summary>Corrections are sorted, non-overlapping and may be incomplete; categorize unmatched changes locally.</summary>
public sealed record ReviewResult(string CorrectedText, ReviewFinding[] Findings, ReviewCorrection[] Corrections);
public sealed record ConnectionStatus(bool Connected, string Message);

public interface IReviewService
{
    Task<ConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task SignInAsync(CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
    Task<ReviewResult> CheckAsync(string text, CancellationToken cancellationToken = default);
}
