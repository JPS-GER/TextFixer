namespace TextFixer.Contracts;

public sealed record ReviewFinding(string Kind, string Explanation, string[] Quotes, string? SuggestedRewrite);
/// <summary>A change made in <see cref="ReviewResult.CorrectedText"/>. Start/End are UTF-16 offsets into the original text.</summary>
public sealed record ReviewCorrection(int Start, int End, string After, string Category);
/// <summary>Corrections are sorted, non-overlapping and may be incomplete; categorize unmatched changes locally.</summary>
public sealed record ReviewResult(string CorrectedText, ReviewFinding[] Findings, ReviewCorrection[] Corrections);
public sealed record ConnectionStatus(bool Connected, string Message);

/// <summary>A cited web source. Url is an exact absolute https URL as cited by the search.</summary>
public sealed record FactSource(string Url, string Title, string Domain);
/// <summary>
/// A checkable claim. Quote occurs in the checked text. Verdict: supported, wrong, disputed, unverifiable or needs_context.
/// Correction replaces Quote and exists only for wrong claims. NoSource: the model gave a verdict without a citation, so it became unverifiable.
/// Question exists only for needs_context: what the user should tell so the claim can be checked.
/// </summary>
public sealed record FactClaim(string Quote, string Verdict, string Explanation, string? Correction, FactSource[] Sources, bool NoSource, string? Question = null);
/// <summary>The user's answer to the question of a needs_context claim.</summary>
public sealed record FactClarification(string Quote, string Question, string Answer);
/// <summary>Empty Claims means no checkable claims. Searches counts completed web searches.</summary>
public sealed record FactCheckResult(FactClaim[] Claims, string Model, long ElapsedMs, int Searches);
/// <summary>One message in a Summary or Explain dialog. Role: user or assistant.</summary>
public sealed record DialogTurn(string Role, string Text);
/// <summary>An answer in a Summary or Explain dialog. Sources come only from url_citation annotations; Searches counts completed web searches.</summary>
public sealed record DialogAnswer(string Text, FactSource[] Sources, int Searches, string Model);

public interface IReviewService
{
    Task<ConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task SignInAsync(CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
    Task<ReviewResult> CheckAsync(string text, CancellationToken cancellationToken = default);
}
