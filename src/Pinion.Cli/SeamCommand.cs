using System.CommandLine;
using Pinion.Adapters.CSharp;

namespace Pinion.Cli;

/// <summary>
/// `pinion seam &lt;path&gt;` — turn analyze's diagnosis into treatment. For methods that hard-wire ambient
/// values (DateTime.Now, Guid.NewGuid, …) it introduces a Feathers seam automatically: the original
/// signature becomes a thin delegating wrapper (existing callers unchanged), and the real body moves to an
/// overload whose ambient values are parameters — deterministic, so `generate` can lock it.
/// Preview-by-default: it prints diffs and writes nothing until --apply; applied edits are compile-gated
/// and reverted if the build breaks. Analyze layer — it feeds `generate`.
/// </summary>
internal static class SeamCommand
{
    public static Command Build()
    {
        var pathArg = new Argument<string>("path")
        {
            Description = "Path to the .sln, .csproj, or source directory to seam.",
        };
        var targetOption = new Option<string?>("--target", "-t")
        {
            Description = "Only seam methods whose name (or containing type) contains this substring.",
        };
        var applyOption = new Option<bool>("--apply")
        {
            Description = "Write the rewrites (default is preview only). Applied edits are compile-checked and reverted if the build breaks.",
        };
        var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Print diagnostics to stderr." };

        var cmd = new Command("seam",
            "Introduce test seams for ambient values (DateTime.Now, Guid.NewGuid, …) so untestable methods become lockable.")
        {
            pathArg, targetOption, applyOption, verboseOption,
        };

        cmd.SetAction(async (parse, ct) => await RunAsync(
            parse.GetValue(pathArg)!,
            parse.GetValue(targetOption),
            parse.GetValue(applyOption),
            parse.GetValue(verboseOption),
            ct));

        return cmd;
    }

    private static async Task<int> RunAsync(string path, string? target, bool apply, bool verbose, CancellationToken ct)
    {
        Action<string>? vlog = verbose ? msg => Console.Error.WriteLine(msg) : null;
        try
        {
            Console.Error.WriteLine($"Analyzing {path} for seamable ambient reads …");
            using var applier = new SeamApplier(vlog, tryMsBuild: Microsoft.Build.Locator.MSBuildLocator.IsRegistered);
            var result = await applier.PlanAsync(path, target, ct);

            foreach (var skip in result.Skipped)
                Console.Error.WriteLine($"  manual: {skip}");

            if (result.Plans.Count == 0)
            {
                Console.Out.WriteLine(result.Skipped.Count > 0
                    ? "No auto-seamable methods (the ones found need a manual seam — see above)."
                    : "No ambient reads found — nothing to seam.");
                return 0;
            }

            int methodCount = result.Plans.Sum(p => p.Methods.Count);
            foreach (var plan in result.Plans)
            {
                Console.Out.WriteLine($"— {plan.FilePath}");
                Console.Out.WriteLine($"  seams: {string.Join(", ", plan.Methods)}  (ambient: {string.Join(", ", plan.Reads)})");
                foreach (var line in plan.Diff.Split('\n')) Console.Out.WriteLine("  " + line.TrimEnd());
                Console.Out.WriteLine();
            }

            if (!apply)
            {
                Console.Out.WriteLine($"Preview only — {methodCount} method(s) in {result.Plans.Count} file(s) can be seamed.");
                Console.Out.WriteLine("Re-run with --apply to write (edits are compile-checked and reverted if the build breaks).");
                return 0;
            }

            var (ok, errors) = await applier.ApplyAsync(path, result.Plans, ct);
            if (!ok)
            {
                Console.Error.WriteLine("error: the build failed after applying seams — ALL edits were reverted:");
                foreach (var e in errors) Console.Error.WriteLine($"    {e}");
                return 1;
            }

            foreach (var e in errors) Console.Error.WriteLine(e);
            Console.Out.WriteLine($"✓ Seamed {methodCount} method(s) in {result.Plans.Count} file(s); build OK.");
            Console.Out.WriteLine("Next: lock the deterministic overloads as golden masters:");
            Console.Out.WriteLine($"  pinion generate \"{path}\" -p <test-project> --target <method>");
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
