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

    /// <param name="focus">Null for the full review in one request, otherwise one review tab (<see cref="ReviewFocus.All"/>).</param>
    public Task<ReviewResult> CheckAsync(string text, CancellationToken cancellationToken = default) => CheckAsync(text, null, cancellationToken);

    public async Task<ReviewResult> CheckAsync(string text, string? focus, CancellationToken cancellationToken)
    {
        if (focus is not null && !ReviewFocus.All.Contains(focus)) throw new ArgumentOutOfRangeException(nameof(focus));
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
        // A focused answer contributes only its own tab: corrections of that category, or findings for Worth checking.
        return focus == ReviewFocus.Worth
            ? result with { CorrectedText = text, Corrections = [] }
            : result with { Findings = [], Corrections = [.. result.Corrections.Select(c => c with { Category = focus })] };
    }

    /// <summary>Translates German into English and other text into German. Keeps the leading and trailing whitespace of the selection.</summary>
    public async Task<string> TranslateAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text)) return text;
        if (text.Length > MaxTextLength)
        {
            throw new ChatGptServiceException(
                "text_too_long", $"The selected text is too long to translate (maximum {MaxTextLength:N0} characters).");
        }

        var output = await WithAccessTokenAsync(async token =>
        {
            var model = await ResolveModelAsync(token, cancellationToken).ConfigureAwait(false);
            return await _responses.StreamTextAsync(token, ReviewPrompt.BuildTranslationRequest(model, text), cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        var translation = output.Trim();
        if (translation.Length == 0) throw new ChatGptServiceException("empty_translation", "ChatGPT returned no translation. Try again.");
        var lead = text[..(text.Length - text.TrimStart().Length)];
        var trail = text[text.TrimEnd().Length..];
        return lead + translation + trail;
    }

    private async Task<string> RequestReviewAsync(string token, string model, string text, string? focus, CancellationToken cancellationToken)
    {
        var structured = !_structuredOutputUnsupported;
        try
        {
            return await _responses.StreamTextAsync(token, ReviewPrompt.BuildRequest(model, text, structured, focus), cancellationToken).ConfigureAwait(false);
        }
        catch (ChatGptServiceException ex) when (
            structured
            && ex.Code == ChatGptProtocol.UnsupportedCapabilityError
            && (ex.ApiParam is null || ex.ApiParam.StartsWith("text", StringComparison.Ordinal)))
        {
            // text.format isn't documented for plan usage. Without it, the parser validates the JSON alone.
            _structuredOutputUnsupported = true;
            return await _responses.StreamTextAsync(token, ReviewPrompt.BuildRequest(model, text, structured: false, focus), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> ResolveModelAsync(string token, CancellationToken cancellationToken)
    {
        await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _models ??= await _responses.ListModelsAsync(token, cancellationToken).ConfigureAwait(false);
            var preferred = PreferredModel;
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
