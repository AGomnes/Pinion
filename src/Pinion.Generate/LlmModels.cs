namespace Pinion.Generate;

/// <summary>One conversational turn sent to the model.</summary>
public sealed record LlmMessage(string Role, string Content)
{
    public static LlmMessage User(string content) => new("user", content);
    public static LlmMessage Assistant(string content) => new("assistant", content);
}

/// <summary>
/// A provider-neutral completion request. <paramref name="System"/> is the stable,
/// cacheable project-context prefix; <paramref name="Messages"/> carries the per-target
/// (and repair) turns.
/// </summary>
public sealed record LlmRequest(
    string Model,
    string System,
    IReadOnlyList<LlmMessage> Messages,
    int MaxTokens,
    bool CacheSystem = true);

/// <summary>Token accounting, used to surface cost + cache effectiveness.</summary>
public sealed record LlmUsage(
    int InputTokens = 0,
    int OutputTokens = 0,
    int CacheReadInputTokens = 0,
    int CacheCreationInputTokens = 0);

/// <summary>A model's reply: the text it produced plus usage.</summary>
public sealed record LlmResponse(string Text, LlmUsage Usage);

/// <summary>
/// The one outbound boundary for the AI layer. Implementations: HTTP clients for Anthropic and for
/// OpenAI-compatible endpoints (including Azure OpenAI and local servers), plus an offline heuristic
/// generator used to exercise the pipeline without a key.
///
/// Every implementation is opt-in: none is selected unless the user passes an explicit --provider.
/// </summary>
public interface ILlmClient
{
    /// <summary>Human-readable provider name, e.g. "anthropic", "openai", "heuristic".</summary>
    string Name { get; }

    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);

    /// <summary>
    /// The exact request body this client would send, for `--dry-run`. Each provider must render its
    /// OWN wire format: printing another provider's shape would make the audit preview a lie, which is
    /// the one thing --dry-run exists to prevent. Offline providers say that nothing is sent.
    /// </summary>
    string PreviewRequest(LlmRequest request);
}
