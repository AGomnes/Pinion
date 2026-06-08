using System.Text.Json.Serialization;

namespace Pinion.Engine.Model;

/// <summary>
/// How readily a unit can be put under a characterization test, in Feathers' terms.
/// </summary>
public enum Seamability
{
    /// <summary>No collaborators worth substituting and no hard dependencies — test it directly.</summary>
    Pure,

    /// <summary>Has an object seam already — a substitutable collaborator (injected/parameter abstraction).</summary>
    SeamAvailable,

    /// <summary>Hard-wires its dependencies (news up concretes, calls statics/I/O) and offers no seam —
    /// a seam must be introduced (Extract Interface / Parameterize Constructor) before it can be locked.</summary>
    NeedsSeam,
}

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
/// <param name="Seams">Object seams already available — substitutable collaborators (injected/parameter abstractions) you can use to characterize this unit as-is.</param>
/// <param name="SeamObstacles">Hard dependencies with no seam (e.g. "DateTime.Now", "new SqlConnection", "File") — a seam must be introduced before this unit can be safely locked.</param>
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
    IReadOnlyList<string> MigrationLandmines,
    [property: JsonIgnore] IReadOnlyList<string>? Seams = null,
    [property: JsonIgnore] IReadOnlyList<string>? SeamObstacles = null)
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

    /// <summary>Substitutable collaborators available now (never null). See <see cref="Seams"/>.</summary>
    public IReadOnlyList<string> SeamPoints => Seams ?? Array.Empty<string>();

    /// <summary>Hard dependencies that block testing until a seam is introduced (never null). See <see cref="SeamObstacles"/>.</summary>
    public IReadOnlyList<string> SeamBlockers => SeamObstacles ?? Array.Empty<string>();

    /// <summary>Whether this unit can be characterized as-is, or needs a seam introduced first.</summary>
    public Seamability Seamability =>
        SeamBlockers.Count > 0 && SeamPoints.Count == 0 ? Seamability.NeedsSeam
        : SeamPoints.Count > 0 ? Seamability.SeamAvailable
        : Seamability.Pure;
}

/// <summary>A single parameter of a <see cref="CodeUnit"/>.</summary>
public sealed record ParamInfo(string Name, string Type);
