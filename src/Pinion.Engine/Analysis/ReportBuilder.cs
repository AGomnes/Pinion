using Pinion.Engine.Model;

namespace Pinion.Engine.Analysis;

/// <summary>Options that shape how a report is assembled.</summary>
public sealed record ReportOptions(
    RiskWeights Weights,
    RiskNormalization Normalization,
    // Tuned for the Milestone-1 signal set (complexity + untested + size, max ~5.5).
    // As Milestone 2 lights up domain/blast-radius/landmine, scores climb and this
    // should be revisited; it is overridable via `--threshold`.
    double HighRiskThreshold = 3.5,
    int TopHotspots = 0)
{
    public static ReportOptions Default { get; } = new(RiskWeights.Default, RiskNormalization.Default);
}

/// <summary>
/// Turns the raw IR from an adapter into a scored, ranked <see cref="AnalysisReport"/>.
/// Language-agnostic: it only ever touches <see cref="CodeUnit"/>.
/// </summary>
public static class ReportBuilder
{
    public static AnalysisReport Build(
        string projectPath,
        IReadOnlyList<CodeUnit> units,
        ReportOptions? options = null,
        DateTimeOffset? generatedAt = null,
        CoverageSummary? coverage = null)
    {
        options ??= ReportOptions.Default;

        var scored = units
            .Select(u => new ScoredUnit(u, RiskScorer.Score(u, options.Weights, options.Normalization)))
            .OrderByDescending(s => s.Score.Total)
            .ThenByDescending(s => s.Unit.CyclomaticComplexity)
            .ToList();

        int filesScanned = units
            .Select(u => u.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        int tested = units.Count(u => u.HasTests);

        // Compare on the unrounded total (RawTotal) so a unit sitting exactly on a rounding boundary
        // isn't included/excluded by display rounding alone.
        int highRiskUnprotected = scored.Count(s =>
            !s.Unit.HasTests && s.Score.RawTotal >= options.HighRiskThreshold);

        // Of the high-risk, unprotected units, how many hard-wire their dependencies and so need a
        // seam introduced before they can be locked — the testability friction in the migration.
        int seamsToIntroduce = scored.Count(s =>
            !s.Unit.HasTests && s.Score.RawTotal >= options.HighRiskThreshold
            && s.Unit.Seamability == Seamability.NeedsSeam);

        var landmineCounts = units
            .SelectMany(u => u.MigrationLandmines)
            .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<ScoredUnit> hotspots = options.TopHotspots > 0
            ? scored.Take(options.TopHotspots).ToList()
            : scored;

        return new AnalysisReport(
            ProjectPath: projectPath,
            GeneratedAt: generatedAt ?? DateTimeOffset.Now,
            ScannedMethods: units.Count,
            ScannedFiles: filesScanned,
            TestedMethods: tested,
            HighRiskUnprotected: highRiskUnprotected,
            HighRiskThreshold: options.HighRiskThreshold,
            LandmineCounts: landmineCounts,
            Hotspots: hotspots,
            Weights: options.Weights,
            Coverage: coverage,
            SeamsToIntroduce: seamsToIntroduce);
    }
}
