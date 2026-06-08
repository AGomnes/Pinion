namespace Pinion.Engine.Model;

/// <summary>
/// The language-neutral intermediate representation the whole engine speaks.
/// Adapters (Roslyn, ts-morph, …) produce these; the engine consumes them and
/// never sees a compiler-specific type. Get this boundary right and a new
/// language is a new adapter, not a rewrite.
/// </summary>
/// <param name="Id">Stable identifier, e.g. "Ns.Class.Method(int,string)".</param>
/// <param name="DisplayName">Human-friendly name for reports, e.g. "InvoiceService.CalculateVat".</param>
/// <param name="FilePath">Absolute path to the source file.</param>
/// <param name="StartLine">1-based first line of the unit.</param>
/// <param name="EndLine">1-based last line of the unit.</param>
/// <param name="Signature">Full signature as written in source.</param>
/// <param name="Parameters">Ordered parameter list.</param>
/// <param name="ReturnType">Return type as written in source ("void" if none).</param>
/// <param name="CyclomaticComplexity">Decision-point count + 1.</param>
/// <param name="LineCount">EndLine - StartLine + 1.</param>
/// <param name="CallerIds">Ids of units that call this one (blast radius). Empty until call-graph (Milestone 2).</param>
/// <param name="CalleeIds">Ids of units this one calls. Empty until call-graph (Milestone 2).</param>
/// <param name="DomainTags">Sensitivity tags, e.g. "money", "auth". Empty until tagging (Milestone 2).</param>
/// <param name="HasTests">True if referenced by any test project.</param>
/// <param name="IsPublicEntryPoint">True if part of a public contract (public/protected member of a public type).</param>
/// <param name="MigrationLandmines">.NET Framework→Core hazards, e.g. "WebForms", "WCF", "EF6". Empty until detection (Milestone 2).</param>
public sealed record CodeUnit(
    string Id,
    string DisplayName,
    string FilePath,
    int StartLine,
    int EndLine,
    string Signature,
    IReadOnlyList<ParamInfo> Parameters,
    string ReturnType,
    int CyclomaticComplexity,
    int LineCount,
    IReadOnlyList<string> CallerIds,
    IReadOnlyList<string> CalleeIds,
    IReadOnlyList<string> DomainTags,
    bool HasTests,
    bool IsPublicEntryPoint,
    IReadOnlyList<string> MigrationLandmines)
{
    /// <summary>The bare member name — <see cref="DisplayName"/> after the last '.'.</summary>
    public string SimpleName
    {
        get
        {
            int dot = DisplayName.LastIndexOf('.');
            return dot >= 0 ? DisplayName[(dot + 1)..] : DisplayName;
        }
    }
}

/// <summary>A single parameter of a <see cref="CodeUnit"/>.</summary>
public sealed record ParamInfo(string Name, string Type);
