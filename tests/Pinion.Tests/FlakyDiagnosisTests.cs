using Pinion.Engine.Model;
using Pinion.Generate;
using Xunit;

namespace Pinion.Tests;

public class FlakyDiagnosisTests
{
    private static CodeUnit Unit(string[]? obstacles) =>
        new("S.M()", "S.M", "S.cs", 1, 5, "sig", Array.Empty<ParamInfo>(), "long", 1, 5,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            HasTests: false, IsPublicEntryPoint: true, Array.Empty<string>(),
            Seams: null, SeamObstacles: obstacles);

    [Fact]
    public void Names_the_seam_obstacle_when_the_analysis_knows_it()
    {
        string msg = FlakyDiagnosis.Explain(Unit(new[] { "DateTime.Now" }));

        Assert.Contains("non-deterministic", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DateTime.Now", msg);   // points straight at the cause
        Assert.Contains("seam", msg);            // and the remedy
    }

    [Fact]
    public void Falls_back_to_the_common_culprits_when_no_obstacle_was_identified()
    {
        string msg = FlakyDiagnosis.Explain(Unit(obstacles: null));

        Assert.Contains("non-deterministic", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("randomness", msg, StringComparison.OrdinalIgnoreCase);
    }
}
