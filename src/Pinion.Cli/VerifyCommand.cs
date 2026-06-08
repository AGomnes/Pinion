using System.CommandLine;
using Pinion.Adapters.CSharp;
using Pinion.Engine.Reporting;

namespace Pinion.Cli;

/// <summary>
/// `pinion verify` — the proof step. Re-runs the locked characterization suite against the current
/// (e.g. post-migration) code and reports which methods still behave identically and exactly how the
/// rest changed. Exit code is non-zero on any change or build break, so it doubles as a CI gate.
/// </summary>
internal static class VerifyCommand
{
    public static Command Build()
    {
        var projectArg = new Argument<string>("test-project")
        {
            Description = "The test project (.csproj) hosting the generated characterization tests + golden masters.",
        };
        var formatOption = new Option<OutputFormat>("--format", "-f")
        {
            Description = "Output format for stdout (Console, Markdown, or Json).",
            DefaultValueFactory = _ => OutputFormat.Console,
        };
        var outOption = new Option<FileInfo?>("--out", "-o")
        {
            Description = "Also write the rendered report to this file.",
        };
        var subdirOption = new Option<string>("--subdir")
        {
            Description = "Folder under the test project holding the generated tests/snapshots.",
            DefaultValueFactory = _ => "PinionCharacterization",
        };
        var timeoutOption = new Option<int>("--timeout")
        {
            Description = "Hard timeout (seconds) for the whole test run.",
            DefaultValueFactory = _ => 600,
        };
        var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Print diagnostics to stderr." };

        var cmd = new Command("verify", "Re-run the locked behavior against current code and report what changed (CI gate).")
        {
            projectArg, formatOption, outOption, subdirOption, timeoutOption, verboseOption,
        };

        cmd.SetAction(async (parse, ct) => await RunAsync(
            parse.GetValue(projectArg)!,
            parse.GetValue(formatOption),
            parse.GetValue(outOption),
            parse.GetValue(subdirOption)!,
            parse.GetValue(timeoutOption),
            parse.GetValue(verboseOption),
            ct));

        return cmd;
    }

    private static async Task<int> RunAsync(
        string testProject, OutputFormat format, FileInfo? outFile, string subdir, int timeout, bool verbose, CancellationToken ct)
    {
        Action<string>? vlog = verbose ? msg => Console.Error.WriteLine(msg) : null;

        try
        {
            if (!File.Exists(testProject))
            {
                Console.Error.WriteLine($"error: test project not found: {testProject}");
                return 2;
            }

            Console.Error.WriteLine($"Verifying behavior against {testProject} …");
            var report = await new BehaviorVerifier(vlog) { RunTimeoutSeconds = timeout }
                .VerifyAsync(testProject, subdir, ct);

            string rendered = format switch
            {
                OutputFormat.Markdown => BehaviorDiffRenderer.Markdown(report),
                OutputFormat.Json => BehaviorDiffRenderer.Json(report),
                OutputFormat.Html => BehaviorDiffRenderer.Html(report),
                _ => BehaviorDiffRenderer.Console(report),
            };

            // The HTML page is a file artifact — printing markup to the console is noise; write it instead.
            if (format != OutputFormat.Html)
                Console.Out.WriteLine(rendered);
            else if (outFile is null)
                Console.Error.WriteLine("note: pass --out report.html to write the behavior-verification page (HTML is a file artifact).");

            if (outFile is not null)
            {
                outFile.Directory?.Create();
                await File.WriteAllTextAsync(outFile.FullName, rendered, ct);
                Console.Error.WriteLine($"Wrote {format} report to {outFile.FullName}");
            }

            // Exit code = CI gate: 0 only when behavior is fully preserved.
            if (report.BuildFailed) return 1;
            if (report.Total == 0) return 1;          // nothing locked to verify
            return report.BehaviorPreserved ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            if (verbose) Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
