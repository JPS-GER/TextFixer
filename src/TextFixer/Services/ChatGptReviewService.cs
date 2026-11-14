using TextFixer.Contracts;

namespace TextFixer.Services;

/// <summary>
/// Text review through the signed-in user's ChatGPT plan (official "Sign in with ChatGPT" plan usage
/// for local personal apps). Thread-safe; create one instance per app.
/// Errors surface as <see cref="ChatGptServiceException"/> with user-facing messages.
/// </summary>
public sealed class ChatGptReviewService : IReviewService
{
    public const int MaxTextLength = 30_000;

    private readonly ChatGptAuthenticator _auth;
    private readonly ChatGptResponsesClient _responses = new();
    private readonly SemaphoreSlim _modelGate = new(1, 1);
    private IReadOnlyList<ChatGptModel>? _models;
    private volatile bool _structuredOutputUnsupported;

    public ChatGptReviewService()
        : this(new CredentialStore())
    {
    }

    internal ChatGptReviewService(CredentialStore store)
    {
        _auth = new ChatGptAuthenticator(store, new OpenIdProvider());
    }

    /// <summary>Link for "Manage usage" (required UI: next to "Using ChatGPT plan" and on usage-limit errors).</summary>
    public static string ManageUsageUrl => ChatGptProtocol.ManageUsageUrl;

    /// <summary>Model slug to use when the account lists it; otherwise the first listed model is used.</summary>
    public string? PreferredModel { get; set; }

    /// <summary>Model for translations and their revisions; null uses <see cref="PreferredModel"/>.</summary>
    public string? PreferredTranslationModel { get; set; }

    /// <summary>Model for fact checks; null uses <see cref="PreferredModel"/>.</summary>
    public string? PreferredFactCheckModel { get; set; }

    /// <summary>Punctuation style sent as prompt rules (<see cref="Punctuation"/>).</summary>
    public IReadOnlyDictionary<string, string> PunctuationStyle { get; set; } = Punctuation.Defaults();

    /// <summary>Spelling of "E-Mail" for all tasks and the form of address for translations (<see cref="WritingPreferences"/>).</summary>
    public string EmailSpelling { get; set; } = WritingPreferences.Keep;
    public string AddressForm { get; set; } = WritingPreferences.Auto;
    public string ReviewDu { get; set; } = WritingPreferences.Keep;

    /// <summary>Punctuation and E-Mail rules for review, fact check, rewrite, summary and explanation.</summary>
    private string Style => Punctuation.PromptRules(PunctuationStyle) + WritingPreferences.EmailRule(EmailSpelling);

    /// <summary>Review and fact-check corrections also get the du capitalization.</summary>
    private string ReviewStyle => Style + WritingPreferences.ReviewDuRule(ReviewDu);

    /// <summary>Translations also get the form of address.</summary>
    private string TranslationStyle => Style + WritingPreferences.TranslationAddressRule(AddressForm);

    /// <summary>Model slug used by the latest check.</summary>
    public string? ActiveModel { get; private set; }

    public async Task<ConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = await _auth.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (state.Unreadable)
        {
            return new ConnectionStatus(false, "The saved ChatGPT sign-in can't be read. Sign in again.");
        }

        if (state.ReauthRequired)
        {
            return new ConnectionStatus(false, "The ChatGPT sign-in has expired. Sign in again.");
        }

        if (state.Tokens is null && state.PendingRefresh is null)
        {
            return new ConnectionStatus(false, "Not signed in to ChatGPT.");
        }

        return new ConnectionStatus(true, string.IsNullOrEmpty(state.Email)
            ? "Using ChatGPT plan"
            : $"Using ChatGPT plan ({state.Email})");
    }

    /// <summary>Opens the system browser and waits (up to 10 minutes) for the user to finish.</summary>
    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        await _auth.SignInAsync(cancellationToken).ConfigureAwait(false);
        _models = null;
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        _models = null;
        ActiveModel = null;
        await _auth.SignOutAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChatGptModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = await WithAccessTokenAsync(
            token => _responses.ListModelsAsync(token, cancellationToken), cancellationToken).ConfigureAwait(false);
        _models = models;
        return models;
    }

    /// <param name="focus">Null for the full review in one request, <see cref="ReviewFocus.Basics"/> for spelling, grammar and punctuation in one request, otherwise one review tab (<see cref="ReviewFocus.All"/>).</param>
    public Task<ReviewResult> CheckAsync(string text, CancellationToken cancellationToken = default) => CheckAsync(text, null, cancellationToken);

    public async Task<ReviewResult> CheckAsync(string text, string? focus, CancellationToken cancellationToken)
    {
        if (focus is not null && focus != ReviewFocus.Basics && !ReviewFocus.All.Contains(focus)) throw new ArgumentOutOfRangeException(nameof(focus));
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ReviewResult(text, [], []);
        }

        if (text.Length > MaxTextLength)
        {
            throw new ChatGptServiceException(
                "text_too_long", $"The selected text is too long to check (maximum {MaxTextLength:N0} characters).");
        }

        var output = await WithAccessTokenAsync(async token =>
        {
            var model = await ResolveModelAsync(token, cancellationToken).ConfigureAwait(false);
            return await RequestReviewAsync(token, model, text, focus, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        var result = ReviewResultParser.Parse(text, output);
        if (focus is null) return result;
        // Basics: no findings; a change outside the three categories counts as grammar, since nothing else was checked.
        if (focus == ReviewFocus.Basics)
            return result with { Findings = [], Corrections = [.. result.Corrections.Select(c => ReviewFocus.BasicsTabs.Contains(c.Category) ? c : c with { Category = ReviewCorrectionCategories.Grammar })] };
        // A focused answer contributes only its own tab: corrections of that category, or findings for Worth checking.
        return focus == ReviewFocus.Worth
            ? result with { CorrectedText = text, Corrections = [] }
            : result with { Findings = [], Corrections = [.. result.Corrections.Select(c => c with { Category = focus })] };
    }

    /// <summary>
    /// Translates into <paramref name="target"/>: auto turns German into English and other text into German. Keeps the
    /// leading and trailing whitespace of the selection.
    /// </summary>
    public async Task<string> TranslateAsync(string text, CancellationToken cancellationToken, string target = ReviewPrompt.TargetAuto)
    {
        if (target is not (ReviewPrompt.TargetAuto or ReviewPrompt.TargetGerman or ReviewPrompt.TargetEnglish)) target = ReviewPrompt.TargetAuto;
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text)) return text;
        if (text.Length > MaxTextLength)
        {
            throw new ChatGptServiceException(
                "text_too_long", $"The selected text is too long to translate (maximum {MaxTextLength:N0} characters).");
        }

        var output = await WithAccessTokenAsync(async token =>
        {
            var model = await ResolveModelAsync(token, cancellationToken, ModelTask.Translate).ConfigureAwait(false);
            return await _responses.StreamTextAsync(token, ReviewPrompt.BuildTranslationRequest(model, text, TranslationStyle, target), cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return KeepOuterWhitespace(text, output);
    }

    public const int MaxRequestLength = 500;

    /// <summary>
    /// Rewrite (as <paramref name="request"/> asks), summary or explanation of <paramref name="text"/>, answered as plain
    /// text. A rewrite keeps the leading and trailing whitespace of the selection.
    /// </summary>
    public async Task<string> WriteAsync(string kind, string text, string? request, CancellationToken cancellationToken, string variant = "")
    {
        variant ??= "";
        if (variant.Length > 0 && !(ReviewPrompt.Variants.TryGetValue(kind, out var variants) && variants.Contains(variant))) variant = "";
        ArgumentNullException.ThrowIfNull(text);
        if (kind is not (ReviewPrompt.Rewrite or ReviewPrompt.Summary or ReviewPrompt.Explain)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (string.IsNullOrWhiteSpace(text)) throw new ChatGptServiceException("empty_text", "Select some text first.");
        if (text.Length > MaxTextLength * 2)
            throw new ChatGptServiceException("text_too_long", $"The selected text is too long (maximum {MaxTextLength:N0} characters).");
        request = request?.Trim() ?? "";
        if (kind == ReviewPrompt.Rewrite && request.Length == 0) throw new ChatGptServiceException("empty_request", "Choose a preset or describe the change you want.");
        if (request.Length > MaxRequestLength) throw new ChatGptServiceException("request_too_long", $"Keep the change request under {MaxRequestLength} characters.");

        var output = await WithAccessTokenAsync(async token =>
        {
            var model = await ResolveModelAsync(token, cancellationToken).ConfigureAwait(false);
            return await _responses.StreamTextAsync(token, ReviewPrompt.BuildWritingRequest(model, kind, text, request, Style, variant), cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (kind == ReviewPrompt.Rewrite) return KeepOuterWhitespace(text, output);
        output = output.Trim();
        if (output.Length == 0) throw new ChatGptServiceException("empty_answer", "ChatGPT returned no answer. Try again.");
        return output;
    }

    public const int MaxQuestionLength = 1000, MaxDialogQuestions = 20;

    /// <summary>
    /// Answers a follow-up question or objection about a summary or explanation of <paramref name="text"/>, with web
    /// search and the fact-check model. <paramref name="turns"/> alternates assistant and user, starting with the first
    /// answer and ending with the new question. An answer without a completed search reports Searches = 0.
    /// </summary>
    public async Task<DialogAnswer> DiscussAsync(string kind, string text, IReadOnlyList<DialogTurn> turns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(turns);
        if (kind is not (ReviewPrompt.Summary or ReviewPrompt.Explain)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (string.IsNullOrWhiteSpace(text)) throw new ChatGptServiceException("empty_text", "Select some text first.");
        if (text.Length > MaxTextLength * 2)
            throw new ChatGptServiceException("text_too_long", $"The selected text is too long (maximum {MaxTextLength:N0} characters).");
        if (turns.Count < 2 || turns.Count % 2 != 0 || turns.Count > MaxDialogQuestions * 2)
            throw new ChatGptServiceException("dialog_too_long", $"A conversation holds up to {MaxDialogQuestions} questions. Capture the text again to start a new one.");
        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            if (turn.Role != (i % 2 == 0 ? "assistant" : "user") || string.IsNullOrWhiteSpace(turn.Text))
                throw new ChatGptServiceException("invalid_dialog", "The conversation could not be read. Capture the text again.");
            if (turn.Role == "user" && turn.Text.Length > MaxQuestionLength)
                throw new ChatGptServiceException("question_too_long", $"Keep the question under {MaxQuestionLength:N0} characters.");
            if (turn.Text.Length > MaxTextLength)
                throw new ChatGptServiceException("invalid_dialog", "The conversation could not be read. Capture the text again.");
        }

        var (response, model) = await WithAccessTokenAsync(async token =>
        {
            var model = await ResolveModelAsync(token, cancellationToken, ModelTask.FactCheck).ConfigureAwait(false);
            return (await _responses.StreamResponseAsync(token, ReviewPrompt.BuildDialogRequest(model, kind, text, turns, Style), ChatGptProtocol.FactCheckTimeout, cancellationToken).ConfigureAwait(false), model);
        }, cancellationToken).ConfigureAwait(false);

        var (answer, sources) = FactCheckParser.CitedAnswer(response);
        if (answer.Length == 0) throw new ChatGptServiceException("empty_answer", "ChatGPT returned no answer. Try again.");
        return new DialogAnswer(answer, sources, response.SearchCalls.Count(s => s == "completed"), model);
    }

    /// <summary>Revises <paramref name="translation"/> of <paramref name="source"/> as the user asks, for example a more formal tone.</summary>
    public async Task<string> ReviseTranslationAsync(string source, string translation, string request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translation);
        request = request?.Trim() ?? "";
        if (request.Length == 0) throw new ChatGptServiceException("empty_request", "Describe the change you want, for example \"more formal\".");
        if (request.Length > MaxRequestLength) throw new ChatGptServiceException("request_too_long", $"Keep the change request under {MaxRequestLength} characters.");
        if (source.Length > MaxTextLength || translation.Length > MaxTextLength * 2)
            throw new ChatGptServiceException("text_too_long", $"The selected text is too long to translate (maximum {MaxTextLength:N0} characters).");

        var output = await WithAccessTokenAsync(async token =>
        {
            var model = await ResolveModelAsync(token, cancellationToken, ModelTask.Translate).ConfigureAwait(false);
            return await _responses.StreamTextAsync(token, ReviewPrompt.BuildTranslationRevisionRequest(model, source, translation.Trim(), request, TranslationStyle), cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return KeepOuterWhitespace(source, output);
    }

    private static string KeepOuterWhitespace(string source, string output)
    {
        var translation = output.Trim();
        if (translation.Length == 0) throw new ChatGptServiceException("empty_translation", "ChatGPT returned no translation. Try again.");
        var lead = source[..(source.Length - source.TrimStart().Length)];
        var trail = source[source.TrimEnd().Length..];
        return lead + translation + trail;
    }

    /// <summary>
    /// Checks the factual claims in <paramref name="text"/> with web search. Throws search_unavailable when claims
    /// were returned but no web search completed, so an answer from memory never counts as a fact check.
    /// </summary>
    public const int MaxContextLength = 4000, MaxClarifications = 40;

    /// <param name="context">The user's notes about the text (topic, time, article, links); empty for none.</param>
    /// <param name="clarifications">The user's answers to questions of needs_context claims.</param>
    /// <param name="only">Quotes to check again; empty checks the whole text.</param>
    public async Task<FactCheckResult> FactCheckAsync(string text, CancellationToken cancellationToken, string context = "",
        IReadOnlyList<FactClarification>? clarifications = null, IReadOnlyList<string>? only = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        context = context?.Trim() ?? "";
        clarifications ??= [];
        only ??= [];
        if (context.Length > MaxContextLength) throw new ChatGptServiceException("context_too_long", $"Keep the context under {MaxContextLength:N0} characters.");
        if (clarifications.Count > MaxClarifications || clarifications.Any(c => c.Answer.Length > MaxQuestionLength || c.Question.Length > MaxQuestionLength || c.Quote.Length > MaxTextLength))
            throw new ChatGptServiceException("context_too_long", $"Keep each answer under {MaxQuestionLength:N0} characters.");
        if (only.Count > FactCheckParser.MaxClaims || only.Any(q => string.IsNullOrEmpty(q) || !text.Contains(q, StringComparison.Ordinal)))
            throw new ChatGptServiceException("invalid_recheck", "The claim no longer matches the captured text. Capture the text again.");
        if (string.IsNullOrWhiteSpace(text)) return new FactCheckResult([], ActiveModel ?? "", 0, 0);
        if (text.Length > MaxTextLength)
        {
            throw new ChatGptServiceException(
                "text_too_long", $"The selected text is too long to check (maximum {MaxTextLength:N0} characters).");
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (response, model) = await WithAccessTokenAsync(async token =>
        {
            var model = await ResolveModelAsync(token, cancellationToken, ModelTask.FactCheck).ConfigureAwait(false);
            return (await RequestFactCheckAsync(token, model, text, context, clarifications, only, cancellationToken).ConfigureAwait(false), model);
        }, cancellationToken).ConfigureAwait(false);

        var claims = FactCheckParser.Parse(text, response);
        var searches = response.SearchCalls.Count(s => s == "completed");
        // A claim waiting for the user's context needs no search; every other verdict does.
        if (claims.Any(c => c.Verdict != FactCheckParser.NeedsContext) && searches == 0)
        {
            throw new ChatGptServiceException(
                "search_unavailable", "Web search did not run, so the claims could not be checked. Try again, or choose another fact-check model in Settings.");
        }

        return new FactCheckResult(claims, model, watch.ElapsedMilliseconds, searches);
    }

    private async Task<ResponseResult> RequestFactCheckAsync(string token, string model, string text, string context,
        IReadOnlyList<FactClarification> clarifications, IReadOnlyList<string> only, CancellationToken cancellationToken)
    {
        var structured = !_structuredOutputUnsupported;
        try
        {
            return await _responses.StreamResponseAsync(token, ReviewPrompt.BuildFactCheckRequest(model, text, structured, ReviewStyle, context, clarifications, only), ChatGptProtocol.FactCheckTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (ChatGptServiceException ex) when (
            structured
            && ex.Code == ChatGptProtocol.UnsupportedCapabilityError
            && (ex.ApiParam is null || ex.ApiParam.StartsWith("text", StringComparison.Ordinal)))
        {
            _structuredOutputUnsupported = true;
            return await _responses.StreamResponseAsync(token, ReviewPrompt.BuildFactCheckRequest(model, text, structured: false, ReviewStyle, context, clarifications, only), ChatGptProtocol.FactCheckTimeout, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> RequestReviewAsync(string token, string model, string text, string? focus, CancellationToken cancellationToken)
    {
        var structured = !_structuredOutputUnsupported;
        try
        {
            return await _responses.StreamTextAsync(token, ReviewPrompt.BuildRequest(model, text, structured, focus, ReviewStyle), cancellationToken).ConfigureAwait(false);
        }
        catch (ChatGptServiceException ex) when (
            structured
            && ex.Code == ChatGptProtocol.UnsupportedCapabilityError
            && (ex.ApiParam is null || ex.ApiParam.StartsWith("text", StringComparison.Ordinal)))
        {
            // text.format isn't documented for plan usage. Without it, the parser validates the JSON alone.
            _structuredOutputUnsupported = true;
            return await _responses.StreamTextAsync(token, ReviewPrompt.BuildRequest(model, text, structured: false, focus, ReviewStyle), cancellationToken).ConfigureAwait(false);
        }
    }

    private enum ModelTask { Review, Translate, FactCheck }

    /// <param name="task">Translate and FactCheck use their own preferred model, falling back to the review model.</param>
    private async Task<string> ResolveModelAsync(string token, CancellationToken cancellationToken, ModelTask task = ModelTask.Review)
    {
        await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _models ??= await _responses.ListModelsAsync(token, cancellationToken).ConfigureAwait(false);
            var preferred = task switch
            {
                ModelTask.Translate => PreferredTranslationModel ?? PreferredModel,
                ModelTask.FactCheck => PreferredFactCheckModel ?? PreferredModel,
                _ => PreferredModel,
            };
            var model = _models.FirstOrDefault(m => m.Slug == preferred) ?? _models[0];
            ActiveModel = model.Slug;
            return model.Slug;
        }
        finally
        {
            _modelGate.Release();
        }
    }

    /// <summary>Runs an API call with a valid access token; after a 401 it refreshes once and retries.</summary>
    private async Task<T> WithAccessTokenAsync<T>(Func<string, Task<T>> action, CancellationToken cancellationToken)
    {
        var token = await _auth.GetAccessTokenAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(token).ConfigureAwait(false);
        }
        catch (ChatGptServiceException ex) when (ex.HttpStatus == 401)
        {
            token = await _auth.GetAccessTokenAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
            return await action(token).ConfigureAwait(false);
        }
    }
}
