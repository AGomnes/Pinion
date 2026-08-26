using System.Text.Json.Serialization;
using Pinion.Engine.Model;

namespace Pinion.Engine.Analysis;

/// <summary>
/// Weights for the risk formula. Exposed (and serializable) so clients can tune
/// them and, crucially, see them. Defaults sum to 10 so the score reads on a
/// familiar 0–10 scale. "Untested" carries the most weight: an unprotected unit
/// is the whole point of the product.
/// </summary>
public sealed record RiskWeights(
    double Complexity = 2.0,
    double NoTests = 2.5,
    double Domain = 1.5,
    double Callers = 1.5,
    double Size = 1.0,
    double Landmine = 1.5)
{
    public static RiskWeights Default { get; } = new();
}

/// <summary>
/// Caps used to normalize unbounded raw metrics into 0..1 before weighting.
/// A method at or above the cap contributes its component's full weight.
/// </summary>
public sealed record RiskNormalization(
    double ComplexityCap = 20,
    double LineCountCap = 200,
    double CallerCountCap = 20)
{
    public static RiskNormalization Default { get; } = new();
}

/// <summary>One line of a score's breakdown — what it was and why it counted.</summary>
/// <param name="Name">Component name, e.g. "complexity".</param>
/// <param name="Weight">Configured weight.</param>
/// <param name="Normalized">The 0..1 normalized signal.</param>
/// <param name="Detail">Human-readable raw value, e.g. "complexity 14" or "0 tests".</param>
public sealed record RiskComponent(string Name, double Weight, double Normalized, string Detail)
{
    /// <summary>Weight × Normalized — this component's contribution to the total.</summary>
    public double Contribution => Weight * Normalized;
}

/// <summary>A unit's total risk plus the fully itemized breakdown that produced it.</summary>
public sealed record RiskScore(double Total, IReadOnlyList<RiskComponent> Components)
{
    /// <summary>The unrounded total. Threshold comparisons use this so a unit near a boundary (e.g. 3.449
    /// vs 3.450) isn't moved in/out of "high-risk" purely by display rounding. Not serialized.</summary>
    [JsonIgnore]
    public double RawTotal { get; init; }
}

/// <summary>Computes the transparent, explainable risk score for a <see cref="CodeUnit"/>.</summary>
public static class RiskScorer
{
    public static RiskScore Score(CodeUnit unit, RiskWeights? weights = null, RiskNormalization? norm = null)
    {
        weights ??= RiskWeights.Default;
        norm ??= RiskNormalization.Default;

        double domainSensitivity = unit.DomainTags.Count == 0
            ? 0
            : Math.Min(1.0, 0.5 + 0.25 * unit.DomainTags.Count);

        bool hasLandmine = unit.MigrationLandmines.Count > 0;

        var components = new List<RiskComponent>
        {
            new("complexity", weights.Complexity, Norm(unit.CyclomaticComplexity, norm.ComplexityCap),
                $"complexity {unit.CyclomaticComplexity}"),
            new("untested", weights.NoTests, unit.HasTests ? 0 : 1,
                unit.HasTests ? "has tests" : "0 tests"),
            new("domain", weights.Domain, domainSensitivity,
                unit.DomainTags.Count == 0 ? "no domain tags" : string.Join("+", unit.DomainTags)),
            new("blast-radius", weights.Callers, Norm(unit.CallerIds.Count, norm.CallerCountCap),
                $"{unit.CallerIds.Count} caller{(unit.CallerIds.Count == 1 ? "" : "s")}"),
            new("size", weights.Size, Norm(unit.LineCount, norm.LineCountCap),
                $"{unit.LineCount} lines"),
            new("landmine", weights.Landmine, hasLandmine ? 1 : 0,
                hasLandmine ? string.Join("+", unit.MigrationLandmines) : "none"),
        };

        double total = components.Sum(c => c.Contribution);
        return new RiskScore(Math.Round(total, 1), components) { RawTotal = total };
    }

    private static double Norm(double value, double cap) => cap <= 0 ? 0 : Math.Min(1.0, value / cap);
}
