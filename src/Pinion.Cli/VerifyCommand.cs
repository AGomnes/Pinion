using System.CommandLine;
using System.Diagnostics;
using System.Xml.Linq;
using Pinion.Adapters.CSharp;
using Pinion.Engine.Analysis;
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
        var openOption = new Option<bool>("--open")
        {
            Description = "Open the rendered report in the default browser (a static file — no server is started).",
        };
        var forceOption = new Option<bool>("--force") { Description = "Overwrite the --out file if it already exists." };
        var sinceOption = new Option<string?>("--since")
        {
            Description = "Verify only behaviors a change could affect: methods in files changed since this git ref (e.g. main, HEAD~1) plus their callers. Scopes a PR check to what it touched (C#).",
        };
        var sourceOption = new Option<string?>("--source")
        {
            Description = "The code under test (.sln/.csproj/dir) for --since blast-radius analysis. Defaults to the test project's ProjectReference.",
        };
        var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Print diagnostics to stderr." };

        var cmd = new Command("verify", "Re-run the locked behavior against current code and report what changed (CI gate).")
        {
            projectArg, formatOption, outOption, subdirOption, timeoutOption, openOption, forceOption, sinceOption, sourceOption, verboseOption,
        };

        cmd.SetAction(async (parse, ct) => await RunAsync(
            parse.GetValue(projectArg)!,
            parse.GetValue(formatOption),
            parse.GetValue(outOption),
            parse.GetValue(subdirOption)!,
            parse.GetValue(timeoutOption),
            parse.GetValue(openOption),
            parse.GetValue(forceOption),
            parse.GetValue(sinceOption),
            parse.GetValue(sourceOption),
            parse.GetValue(verboseOption),
            ct));

        return cmd;
    }

    private static async Task<int> RunAsync(
        string testProject, OutputFormat format, FileInfo? outFile, string subdir, int timeout, bool open, bool force,
        string? since, string? source, bool verbose, CancellationToken ct)
    {
        Action<string>? vlog = verbose ? msg => Console.Error.WriteLine(msg) : null;

        try
        {
            if (!File.Exists(testProject))
            {
                Console.Error.WriteLine($"error: test project not found: {testProject}");
                return 2;
            }

            // --since: scope the run to the locked behaviors a change since <ref> could affect (changed
            // methods + their callers). Returns null when nothing is affected → nothing to verify.
            IReadOnlyCollection<string>? onlyClasses = null;
            if (!string.IsNullOrWhiteSpace(since))
            {
                var scope = await ResolveSinceScopeAsync(testProject, since!, source, vlog, ct);
                if (scope is null) return 2; // a setup problem (bad ref / no source) — already reported
                if (scope.Count == 0)
                {
                    Console.Error.WriteLine($"No locked behavior is affected by changes since {since}. Nothing to verify.");
                    return 0;
                }
                onlyClasses = scope;
                Console.Error.WriteLine($"Scoped to {scope.Count} characterization class(es) affected by changes since {since}.");
            }

            Console.Error.WriteLine($"Verifying behavior against {testProject} …");
            var report = await new BehaviorVerifier(vlog) { RunTimeoutSeconds = timeout }
                .VerifyAsync(testProject, subdir, ct, onlyClasses);

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
            else if (outFile is null && !open)
                Console.Error.WriteLine("note: pass --out report.html (or --open) to view the behavior-verification page (HTML is a file artifact).");

            if (outFile is not null)
            {
                if (outFile.Exists && !force)
                {
                    Console.Error.WriteLine($"error: {outFile.FullName} already exists. Pass --force to overwrite.");
                    return 1;
                }
                outFile.Directory?.Create();
                await File.WriteAllTextAsync(outFile.FullName, rendered, ct);
                Console.Error.WriteLine($"Wrote {format} report to {outFile.FullName}");
            }

            if (open) await ReportOutput.OpenAsync(rendered, format, outFile, "pinion-verify", ct);

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

    /// <summary>Characterization test-class names whose covered method (or one it calls) changed since
    /// <paramref name="since"/>. Null on a setup error (already reported); an empty set means "nothing
    /// affected".</summary>
    private static async Task<IReadOnlyCollection<string>?> ResolveSinceScopeAsync(
        string testProject, string since, string? source, Action<string>? vlog, CancellationToken ct)
    {
        string? srcPath = source ?? DeriveSourceFromTestProject(testProject);
        if (srcPath is null)
        {
            Console.Error.WriteLine("error: --since needs the code under test. Pass --source <.sln/.csproj/dir> " +
                "(couldn't derive a single ProjectReference from the test project).");
            return null;
        }
        if (!File.Exists(srcPath) && !Directory.Exists(srcPath))
        {
            Console.Error.WriteLine($"error: --source path not found: {srcPath}");
            return null;
        }

        string srcDir = Directory.Exists(srcPath) ? Path.GetFullPath(srcPath) : Path.GetDirectoryName(Path.GetFullPath(srcPath))!;
        var changed = GitChangedFiles(srcDir, since, vlog);
        if (changed is null)
        {
            Console.Error.WriteLine($"error: couldn't list changes since '{since}'. Is this a git repository and is the ref valid?");
            return null;
        }

        var changedCode = changed
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (changedCode.Count == 0) return Array.Empty<string>();

        vlog?.Invoke($"[verify] {changedCode.Count} changed source file(s) since {since}; analyzing {srcPath} for blast radius …");
        var units = await new CSharpAdapter(vlog).AnalyzeAsync(srcPath, ct);
        return ChangeScope.Affected(units, changedCode)
            .Select(u => CharacterizationNaming.TestClassName(u.DisplayName, u.Id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The single code project a test project references (its <c>ProjectReference</c>), full path —
    /// or null when there isn't exactly one to pick.</summary>
    private static string? DeriveSourceFromTestProject(string testProjectPath)
    {
        try
        {
            string full = Path.GetFullPath(testProjectPath);
            string dir = Path.GetDirectoryName(full)!;
            var refs = XDocument.Load(full).Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => Path.GetFullPath(Path.Combine(dir, v!.Replace('\\', Path.DirectorySeparatorChar))))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return refs.Count == 1 ? refs[0] : null;
        }
        catch { return null; }
    }

    /// <summary>Files changed between <paramref name="since"/> and the working tree, as absolute paths.
    /// Null if git is unavailable or the ref is invalid.</summary>
    private static IReadOnlyList<string>? GitChangedFiles(string workingDir, string since, Action<string>? vlog)
    {
        string? root = RunGit(workingDir, "rev-parse", "--show-toplevel")?.Trim();
        if (string.IsNullOrEmpty(root)) return null;

        // `diff <ref> --name-only` = committed + working-tree changes relative to the ref.
        string? listed = RunGit(workingDir, "diff", "--name-only", since, "--");
        if (listed is null) return null;

        var files = new List<string>();
        foreach (var line in listed.Split('\n'))
        {
            string rel = line.Trim();
            if (rel.Length > 0) files.Add(Path.GetFullPath(Path.Combine(root, rel)));
        }
        return files;
    }

    private static string? RunGit(string workingDir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            string outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? outp : null;
        }
        catch { return null; }
    }
}
