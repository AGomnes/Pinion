using System.CommandLine;
using Pinion.Adapters.CSharp.Generate;
using Pinion.Generate.Licensing;

namespace Pinion.Cli;

/// <summary>
/// `pinion prove` — mutation-test the code under the given test project (via Stryker.NET) and report
/// how many behaviour-changing mutations the tests catch. Turns "we generated tests" into the
/// measurable claim "the tests kill N% of regressions".
/// </summary>
internal static class ProveCommand
{
    public static Command Build()
    {
        var testProjectOption = new Option<FileInfo>("--test-project", "-p")
        {
            Description = "Test project (.csproj) whose tests should be mutation-tested.",
            Required = true,
        };
        var licenseOption = new Option<string?>("--license")
        {
            Description = "License key (else PINION_LICENSE / pinion.license file). Dormant: nothing is gated.",
        };
        var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Print diagnostics to stderr." };
        var reportJsonOption = new Option<FileInfo?>("--report-json")
        {
            Description = "Also write the score report as JSON (feed it to `analyze --format html --mutation-report`).",
        };

        var cmd = new Command("prove", "Mutation-test the generated tests (needs dotnet-stryker) and report the regression-catching score.")
        {
            testProjectOption, licenseOption, verboseOption, reportJsonOption,
        };

        cmd.SetAction(async (parse, ct) =>
        {
            var (status, _) = LicenseGate.Resolve(parse.GetValue(licenseOption));
            if (status.Valid) LicenseCommand.WarnIfNearExpiry(status);

            var testProject = parse.GetValue(testProjectOption)!;
            bool verbose = parse.GetValue(verboseOption);
            Action<string> log = msg => Console.Error.WriteLine(msg);

            var report = await new MutationTester(log).RunAsync(testProject.FullName, ct);
            if (report is null)
            {
                Console.Error.WriteLine("error: mutation testing did not produce a report (see messages above).");
                return 1;
            }

            Console.Out.WriteLine($"MUTATION SCORE: {report.Score:0.0}%  " +
                $"(killed {report.Killed + report.Timeout}/{report.Tested}; survived {report.Survived}, uncovered {report.NoCoverage})");
            Console.Out.WriteLine(new string('─', 60));
            foreach (var f in report.Files)
                Console.Out.WriteLine($"{f.File,-32} {f.Score,5:0}%  killed {f.Killed + f.Timeout,3}/{f.Tested,-3} survived {f.Survived,2}, uncovered {f.NoCoverage,2}");
            Console.Out.WriteLine(new string('─', 60));
            Console.Out.WriteLine("Survived/uncovered mutants mark behaviour the tests don't lock — raise inputs or use --provider anthropic for guard-heavy methods.");

            var reportJson = parse.GetValue(reportJsonOption);
            if (reportJson is not null)
            {
                reportJson.Directory?.Create();
                await File.WriteAllTextAsync(reportJson.FullName,
                    System.Text.Json.JsonSerializer.Serialize(report), ct);
                Console.Error.WriteLine($"Wrote score report to {reportJson.FullName}");
            }
            return 0;
        });

        return cmd;
    }
}
