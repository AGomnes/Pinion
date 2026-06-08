using Pinion.Engine.Model;

namespace Pinion.Engine.Analysis;

/// <summary>A <see cref="CodeUnit"/> paired with its computed risk.</summary>
public sealed record ScoredUnit(CodeUnit Unit, RiskScore Score);

/// <summary>
/// The Migration Readiness Report — the headline `analyze` artifact and the sales
/// asset. Pure data: renderers turn it into console / Markdown / JSON.
/// </summary>
public sealed record AnalysisReport(
    string ProjectPath,
    DateTimeOffset GeneratedAt,
    int ScannedMethods,
    int ScannedFiles,
    int TestedMethods,
    int HighRiskUnprotected,
    double HighRiskThreshold,
    IReadOnlyDictionary<string, int> LandmineCounts,
    IReadOnlyList<ScoredUnit> Hotspots,
    RiskWeights Weights,
    CoverageSummary? Coverage = null,
    // High-risk, unprotected units that hard-wire their dependencies and so need a seam introduced
    // before they can be characterized (Feathers). The friction count for the migration estimate.
    int SeamsToIntroduce = 0)
{
    /// <summary>Fraction of methods that are referenced by a test (0..1).</summary>
    public double BehaviorCoverage => ScannedMethods == 0 ? 0 : (double)TestedMethods / ScannedMethods;
}
