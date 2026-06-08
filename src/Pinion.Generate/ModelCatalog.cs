namespace Pinion.Generate;

/// <summary>
/// The Anthropic model ids Pinion knows about. Used to catch a typo'd <c>--model</c> (e.g.
/// "claude-sonnet-4.6" with a dot) before a billable run spends a call on a 404. Advisory, not a
/// hard gate: Anthropic ships new models over time, so an unknown id is warned-about, not rejected —
/// the user can always pass a newer id deliberately.
/// </summary>
public static class ModelCatalog
{
    /// <summary>Current + recent ids accepted without a warning.</summary>
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6", "claude-opus-4-5",
        "claude-sonnet-4-6", "claude-sonnet-4-5",
        "claude-haiku-4-5",
    };

    public static bool IsKnown(string model) => !string.IsNullOrWhiteSpace(model) && Known.Contains(model);
}
