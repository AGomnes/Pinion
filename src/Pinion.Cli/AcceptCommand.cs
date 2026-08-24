using System.CommandLine;
using Pinion.Adapters.CSharp;
using Pinion.Engine.Reporting;
using Pinion.Generate;

namespace Pinion.Cli;

/// <summary>
/// `pinion accept &lt;test-project&gt;` — the triage step after <c>verify</c> shows changes. Re-baselines
/// the INTENDED ones (promotes the current output to the golden master) so the locked suite tracks the new
/// expected behavior, leaving only genuine regressions to investigate. Scoped by <c>--name</c> (or
/// <c>--all</c>) so you accept deliberately, never by accident.
/// </summary>
internal static class AcceptCommand
{
    public static Command Build()
    {
        var projectArg = new Argument<string>("test-project")
        {
            Description = "The test project (.csproj) hosting the characterization tests + golden masters.",
        };
        var nameOption = new Option<string?>("--name")
        {
            Description = "Re-baseline only changed behaviors whose name contains this substring (e.g. a class or method name).",
        };
        var allOption = new Option<bool>("--all")
        {
            Description = "Re-baseline EVERY changed behavior. Use only after reviewing the verify diff — this accepts all current output as correct.",
        };
        var subdirOption = new Option<string>("--subdir")
        {
            Description = "Folder under the test project holding the generated tests/snapshots.",
            DefaultValueFactory = _ => "PinionCharacterization",
        };
        var timeoutOption = new Option<int>("--timeout")
        {
            Description = "Hard timeout (seconds) for the test run.",
            DefaultValueFactory = _ => 600,
        };
        var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Print diagnostics to stderr." };

        var cmd = new Command("accept",
            "Re-baseline intended behavior changes: promote the current output to the golden master for matching methods.")
        {
            projectArg, nameOption, allOption, subdirOption, timeoutOption, verboseOption,
        };

        cmd.SetAction(async (parse, ct) => await RunAsync(
            parse.GetValue(projectArg)!,
            parse.GetValue(nameOption),
            parse.GetValue(allOption),
            parse.GetValue(subdirOption)!,
            parse.GetValue(timeoutOption),
            parse.GetValue(verboseOption),
            ct));

        return cmd;
    }

    private static async Task<int> RunAsync(
        string testProject, string? name, bool all, string subdir, int timeout, bool verbose, CancellationToken ct)
    {
        Action<string>? vlog = verbose ? msg => Console.Error.WriteLine(msg) : null;

        try
        {
            if (!File.Exists(testProject))
            {
                Console.Error.WriteLine($"error: test project not found: {testProject}");
                return 2;
            }
            if (!all && string.IsNullOrWhiteSpace(name))
            {
                Console.Error.WriteLine("error: specify which changes to accept — --name <substring> (recommended) or --all.");
                return 2;
            }

            Console.Error.WriteLine($"Re-running locked behavior against {testProject} …");
            var report = await new BehaviorVerifier(vlog) { RunTimeoutSeconds = timeout }
                .VerifyAsync(testProject, subdir, ct);

            if (report.BuildFailed)
            {
                Console.Error.WriteLine("error: the characterization tests didn't compile against the current code — fix the build before accepting.");
                foreach (var e in report.BuildErrors ?? Array.Empty<string>()) Console.Error.WriteLine($"    {e}");
                return 1;
            }
            if (report.Total == 0)
            {
                Console.Error.WriteLine("No locked behavior found. Run `pinion generate` first to capture golden masters.");
                return 1;
            }

            var changed = report.Entries.Where(e => e.Status == BehaviorChange.Changed).ToList();
            if (changed.Count == 0)
            {
                Console.Out.WriteLine("Nothing to accept — every locked behavior is already identical.");
                return 0;
            }

            var toAccept = all
                ? changed
                : changed.Where(e => e.Name.Contains(name!, StringComparison.OrdinalIgnoreCase)).ToList();
            if (toAccept.Count == 0)
            {
                Console.Error.WriteLine($"No changed behavior matches --name '{name}'. Changed: {string.Join(", ", changed.Select(e => e.Name))}.");
                return 1;
            }

            int accepted = BehaviorBaseline.Accept(toAccept);
            foreach (var e in toAccept) Console.Out.WriteLine($"✓ re-baselined {e.Name}");

            int remaining = changed.Count - accepted;
            Console.Error.WriteLine($"Accepted {accepted} change(s) as the new golden master." +
                (remaining > 0 ? $" {remaining} changed behavior(s) left — review them as potential regressions (`pinion verify`)." : ""));
            Console.Error.WriteLine("Commit the updated *.verified.* snapshots to record the new expected behavior.");
            return 0;
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
