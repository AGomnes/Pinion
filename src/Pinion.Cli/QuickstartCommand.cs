using System.CommandLine;
using Pinion.Adapters.CSharp;
using Pinion.Adapters.CSharp.Generate;
using Pinion.Engine.Scaffolding;
using Pinion.Generate;

namespace Pinion.Cli;

/// <summary>
/// `pinion quickstart &lt;path&gt;` — the one-command golden path / first-run "aha": analyze the code,
/// pick the riskiest characterizable methods, scaffold a host test project if there isn't one, and lock
/// those behaviors as golden masters — offline, no AI, no API key. Turns a multi-step setup
/// (init-tests → restore → generate) into a single command that ends on a green proof.
/// </summary>
internal static class QuickstartCommand
{
    public static Command Build()
    {
        var pathArg = new Argument<string>("path")
        {
            Description = "The code to characterize: a .csproj (or a directory containing exactly one), or a .sln/dir with --test-project.",
        };
        var topOption = new Option<int>("--top")
        {
            Description = "How many of the highest-risk untested methods to lock.",
            DefaultValueFactory = _ => 10,
        };
        var testProjectOption = new Option<FileInfo?>("--test-project", "-p")
        {
            Description = "An existing test project (.csproj) to host the tests. If omitted, one is scaffolded next to the code project.",
        };
        var tfmOption = new Option<string>("--tfm")
        {
            Description = "Target framework for a scaffolded test project.",
            DefaultValueFactory = _ => TestProjectScaffolder.DefaultTargetFramework,
        };
        var allowSideEffectsOption = new Option<bool>("--allow-side-effects")
        {
            Description = "Include methods tagged io/money. WARNING: locking these EXECUTES real side effects (files, DB, network, payments).",
        };
        var timeoutOption = new Option<int>("--timeout")
        {
            Description = "Per-method test-run timeout in seconds (kills a hung/infinite-loop target).",
            DefaultValueFactory = _ => 180,
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Show what would be locked and where the test project would go — write and run nothing.",
        };
        var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Print Roslyn/MSBuild diagnostics to stderr." };

        var cmd = new Command("quickstart",
            "Lock your riskiest behaviors in one step: analyze → scaffold a test project → characterize (offline, no AI).")
        {
            pathArg, topOption, testProjectOption, tfmOption, allowSideEffectsOption, timeoutOption, dryRunOption, verboseOption,
        };

        cmd.SetAction(async (parse, ct) => await RunAsync(
            parse.GetValue(pathArg)!,
            parse.GetValue(topOption),
            parse.GetValue(testProjectOption),
            parse.GetValue(tfmOption)!,
            parse.GetValue(allowSideEffectsOption),
            parse.GetValue(timeoutOption),
            parse.GetValue(dryRunOption),
            parse.GetValue(verboseOption),
            ct));

        return cmd;
    }

    private static async Task<int> RunAsync(
        string path, int top, FileInfo? testProject, string tfm,
        bool allowSideEffects, int timeoutSeconds, bool dryRun, bool verbose, CancellationToken ct)
    {
        Action<string>? vlog = verbose ? msg => Console.Error.WriteLine(msg) : null;

        try
        {
            // 1. Analyze and rank.
            Console.Error.WriteLine($"Analyzing {path} …");
            var adapter = new CSharpAdapter(vlog);
            var units = await adapter.AnalyzeAsync(path, ct);

            // 2. Select the riskiest characterizable targets (public methods on public types, untested),
            //    then drop the side-effecting ones unless the user opted in.
            var ranked = QuickstartPlanner.SelectRiskiest(path, units, Math.Max(1, top));
            var targets = allowSideEffects ? ranked : ranked.Where(u => !TargetGuards.IsSideEffecting(u)).ToList();
            int skipped = ranked.Count - targets.Count;
            if (skipped > 0)
                Console.Error.WriteLine($"Skipped {skipped} method(s) tagged io (running them would touch the filesystem/DB/network) — pass --allow-side-effects to include them.");

            int sensitive = targets.Count(TargetGuards.IsSensitive);
            if (sensitive > 0)
                Console.Error.WriteLine($"note: {sensitive} money/auth-sensitive method(s) will be characterized — snapshots are secret-scrubbed, but review them before committing.");

            if (targets.Count == 0)
            {
                Console.Error.WriteLine(units.Count == 0
                    ? "No methods found to characterize. Point quickstart at a project with source."
                    : "No untested public methods to lock (everything risk-ranked is already tested or not externally callable). " +
                      "Run `pinion analyze` to see the full picture.");
                return 1;
            }

            // 3. Decide where the host test project lives (scaffold one when none is supplied).
            var plan = QuickstartPlanner.PlanTestProject(path, testProject?.FullName, tfm);
            if (plan.Error is not null) { Console.Error.WriteLine($"error: {plan.Error}"); return 2; }

            // 4. Dry run: show the plan, touch nothing.
            if (dryRun)
            {
                Console.Out.WriteLine($"Would lock {targets.Count} behavior(s):");
                foreach (var u in targets) Console.Out.WriteLine($"  • {u.DisplayName}  (risk-ranked, untested, public)");
                Console.Out.WriteLine();
                Console.Out.WriteLine(plan.Exists
                    ? $"Would use existing test project: {plan.ProjectFile}"
                    : $"Would scaffold a test project at: {plan.ProjectFile}");
                Console.Out.WriteLine("Re-run without --dry-run to lock them (offline, no AI, $0).");
                return 0;
            }

            // 5. Materialize the test project if needed.
            if (plan.Exists)
                Console.Error.WriteLine($"Using existing test project: {plan.ProjectFile}");
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(plan.ProjectFile)!);
                File.WriteAllText(plan.ProjectFile, TestProjectScaffolder.Csproj(plan.RelativeCodeRef!, tfm));
                Console.Error.WriteLine($"Scaffolded host test project: {plan.ProjectFile}");
            }

            // 6. Lock behavior — deterministic, offline, one build for the batch. The first run restores
            //    NuGet packages for the freshly scaffolded project, so it can take a minute.
            Console.Error.WriteLine($"Locking {targets.Count} behavior(s) — deterministic, offline (no AI, no API cost)…");
            using var genAdapter = new CSharpTestGenerator
            {
                RunTimeoutSeconds = timeoutSeconds,
                ResolveReferences = Microsoft.Build.Locator.MSBuildLocator.IsRegistered,
                Log = vlog,
            };
            genAdapter.ConfigureGeneration(plan.ProjectFile);

            var results = await genAdapter.GenerateDeterministicBatchAsync(targets, path, ct);

            int ok = 0;
            foreach (var r in results)
            {
                if (r.Success)
                {
                    ok++;
                    Console.Out.WriteLine($"✓ {r.Unit.DisplayName}");
                    Console.Out.WriteLine($"  golden master: {r.SnapshotPath}");
                }
                else
                {
                    Console.Out.WriteLine($"✗ {r.Unit.DisplayName} (couldn't be locked automatically)");
                    foreach (var d in r.Diagnostics.Take(2)) Console.Out.WriteLine($"    {d}");
                }
            }

            PrintNextSteps(ok, targets.Count, plan.ProjectFile, path);
            return ok > 0 ? 0 : 1;
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

    private static void PrintNextSteps(int ok, int total, string testProjectFile, string path)
    {
        Console.Error.WriteLine();
        if (ok == 0)
        {
            Console.Error.WriteLine("Nothing could be locked automatically. Run with --verbose to see why, or try " +
                "`pinion analyze` to pick targets manually.");
            return;
        }

        Console.Error.WriteLine($"Locked {ok}/{total} of your riskiest behaviors as golden masters. This is your safety net.");
        Console.Error.WriteLine("Next:");
        Console.Error.WriteLine($"  1. Commit the new tests + snapshots under the test project.");
        Console.Error.WriteLine($"  2. Do your migration (retarget the TFM, bump packages, refactor).");
        Console.Error.WriteLine($"  3. Prove nothing broke:  pinion verify \"{testProjectFile}\"");
        Console.Error.WriteLine($"     (See the full risk picture any time:  pinion analyze \"{path}\" --format html --open)");
    }
}
