using Pinion.Engine.Analysis;
using Pinion.Engine.Model;
using Xunit;

namespace Pinion.Tests;

public class ReportBuilderTests
{
    private static CodeUnit Unit(string id, int complexity, bool hasTests, string file = "A.cs") =>
        new(id, id, file, 1, 10, "sig", Array.Empty<ParamInfo>(), "void",
            complexity, 10, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), hasTests, true, Array.Empty<string>());

    [Fact]
    public void Hotspots_are_ranked_by_descending_risk()
    {
        var units = new[]
        {
            Unit("low", complexity: 1, hasTests: true),
            Unit("high", complexity: 20, hasTests: false),
            Unit("mid", complexity: 5, hasTests: false),
        };

        var report = ReportBuilder.Build("proj", units);

        Assert.Equal(new[] { "high", "mid", "low" }, report.Hotspots.Select(h => h.Unit.Id).ToArray());
    }

    [Fact]
    public void Coverage_and_counts_reflect_the_units()
    {
        var units = new[]
        {
            Unit("a", 1, hasTests: true, file: "A.cs"),
            Unit("b", 1, hasTests: false, file: "A.cs"),
            Unit("c", 1, hasTests: false, file: "B.cs"),
            Unit("d", 1, hasTests: true, file: "B.cs"),
        };

        var report = ReportBuilder.Build("proj", units);

        Assert.Equal(4, report.ScannedMethods);
        Assert.Equal(2, report.ScannedFiles);
        Assert.Equal(2, report.TestedMethods);
        Assert.Equal(0.5, report.BehaviorCoverage, 3);
    }

    [Fact]
    public void High_risk_unprotected_counts_only_untested_units_over_threshold()
    {
        var units = new[]
        {
            Unit("risky-untested", complexity: 20, hasTests: false),
            Unit("risky-but-tested", complexity: 20, hasTests: true),
            Unit("safe-untested", complexity: 1, hasTests: false),
        };

        var report = ReportBuilder.Build("proj", units, ReportOptions.Default with { HighRiskThreshold = 3.5 });

        Assert.Equal(1, report.HighRiskUnprotected);
    }
}
