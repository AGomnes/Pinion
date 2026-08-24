using Pinion.Adapters.CSharp.Generate;
using Xunit;

namespace Pinion.Tests;

public class MutationReportTests
{
    [Fact]
    public void Parses_stryker_report_into_scores()
    {
        const string json = """
        {
          "files": {
            "src/A.cs": { "mutants": [ {"status":"Killed"}, {"status":"Survived"}, {"status":"NoCoverage"} ] },
            "src/B.cs": { "mutants": [ {"status":"Killed"}, {"status":"Killed"}, {"status":"Timeout"} ] }
          }
        }
        """;
        string path = Path.Combine(Path.GetTempPath(), "pinion-mut-" + System.Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        try
        {
            var report = MutationTester.Parse(path);

            Assert.Equal(3, report.Killed);
            Assert.Equal(1, report.Survived);
            Assert.Equal(1, report.NoCoverage);
            Assert.Equal(1, report.Timeout);
            Assert.Equal(6, report.Tested);
            Assert.Equal(66.7, report.Score, 1);

            var a = report.Files.Single(f => f.File == "A.cs");
            Assert.Equal(33.3, a.Score, 1);
            var b = report.Files.Single(f => f.File == "B.cs");
            Assert.Equal(100.0, b.Score, 1);
        }
        finally { File.Delete(path); }
    }
}
