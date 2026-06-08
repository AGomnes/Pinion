using Pinion.Engine.Analysis;
using Pinion.Engine.Model;
using Xunit;

namespace Pinion.Tests;

public class RiskScorerTests
{
    private static CodeUnit Unit(
        int complexity = 1,
        int lineCount = 1,
        bool hasTests = false,
        string[]? domain = null,
        string[]? callers = null,
        string[]? landmines = null) =>
        new(
            Id: "N.C.M()",
            DisplayName: "C.M",
            FilePath: "C.cs",
            StartLine: 1,
            EndLine: lineCount,
            Signature: "void M()",
            Parameters: Array.Empty<ParamInfo>(),
            ReturnType: "void",
            CyclomaticComplexity: complexity,
            LineCount: lineCount,
            CallerIds: callers ?? Array.Empty<string>(),
            CalleeIds: Array.Empty<string>(),
            DomainTags: domain ?? Array.Empty<string>(),
            HasTests: hasTests,
            IsPublicEntryPoint: true,
            MigrationLandmines: landmines ?? Array.Empty<string>());

    [Fact]
    public void Untested_scores_higher_than_otherwise_identical_tested_unit()
    {
        var tested = RiskScorer.Score(Unit(hasTests: true));
        var untested = RiskScorer.Score(Unit(hasTests: false));

        Assert.True(untested.Total > tested.Total);
    }

    [Fact]
    public void Score_is_the_sum_of_its_component_contributions()
    {
        var score = RiskScorer.Score(Unit(complexity: 10, lineCount: 100, hasTests: false));

        double sum = score.Components.Sum(c => c.Contribution);
        Assert.Equal(Math.Round(sum, 1), score.Total, 1);
    }

    [Fact]
    public void Every_component_is_present_for_auditability()
    {
        var names = RiskScorer.Score(Unit()).Components.Select(c => c.Name).ToHashSet();

        Assert.Superset(
            new HashSet<string> { "complexity", "untested", "domain", "blast-radius", "size", "landmine" },
            names);
    }

    [Fact]
    public void Normalization_caps_each_signal_at_full_weight()
    {
        var weights = RiskWeights.Default;
        var norm = RiskNormalization.Default;

        // Way past every cap: each normalized signal should saturate at 1.0.
        var score = RiskScorer.Score(
            Unit(complexity: 999, lineCount: 9999, hasTests: false,
                 callers: Enumerable.Range(0, 999).Select(i => $"c{i}").ToArray(),
                 domain: new[] { DomainTag.Money, DomainTag.Auth },
                 landmines: new[] { MigrationLandmine.Wcf }),
            weights, norm);

        double maxPossible = weights.Complexity + weights.NoTests + weights.Domain
            + weights.Callers + weights.Size + weights.Landmine;

        Assert.Equal(maxPossible, score.Total, 1);
    }
}
