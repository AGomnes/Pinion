namespace Pinion.Generate;

/// <summary>
/// The model ids Pinion knows about, per provider. Used to catch a typo'd <c>--model</c> (e.g.
/// "claude-sonnet-4.6" with a dot, or "gpt4o" without the dash) before a billable run spends a call on
/// a 404. Advisory, not a hard gate: providers ship new models constantly, so an unknown id is
/// warned-about rather than rejected — a user can always pass a newer id deliberately.
/// </summary>
public static class ModelCatalog
{
    /// <summary>Current + recent Anthropic ids accepted without a warning.</summary>
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6", "claude-opus-4-5",
        "claude-sonnet-4-6", "claude-sonnet-4-5",
        "claude-haiku-4-5",
    };

    /// <summary>Current + recent OpenAI ids accepted without a warning.</summary>
    public static readonly IReadOnlySet<string> KnownOpenAi = new HashSet<string>(StringComparer.Ordinal)
    {
        "gpt-5", "gpt-5-mini", "gpt-5-nano",
        "gpt-4.1", "gpt-4.1-mini", "gpt-4.1-nano",
        "gpt-4o", "gpt-4o-mini",
        "o4-mini", "o3", "o3-mini",
    };

    public static bool IsKnown(string model) => !string.IsNullOrWhiteSpace(model) && Known.Contains(model);

    /// <summary>
    /// The model to use when the caller did not pass <c>--model</c>. Azure OpenAI deliberately has none:
    /// there the value is a customer-chosen deployment name, so no default could be correct.
    /// </summary>
    public static string? DefaultFor(string provider) => provider switch
    {
        "anthropic" => GenerationOptions.DefaultModel,
        "openai" => "gpt-4.1",
        _ => null,
    };

    public static bool IsKnownOpenAi(string model) =>
        !string.IsNullOrWhiteSpace(model) && KnownOpenAi.Contains(model);

    /// <summary>
    /// Whether a warning is warranted for this provider/model pair. Azure OpenAI is deliberately
    /// exempt: there <c>--model</c> is a customer-chosen *deployment name*, not a published model id,
    /// so any value is legitimate and a catalog check would only produce false alarms.
    /// </summary>
    public static bool ShouldWarn(string provider, string model) => provider switch
    {
        "anthropic" => !IsKnown(model),
        "openai" => !IsKnownOpenAi(model),
        _ => false,
    };

    /// <summary>The ids to suggest when warning about <paramref name="provider"/>.</summary>
    public static IReadOnlyCollection<string> KnownFor(string provider) => provider switch
    {
        "openai" => (IReadOnlyCollection<string>)KnownOpenAi,
        _ => (IReadOnlyCollection<string>)Known,
    };
}
