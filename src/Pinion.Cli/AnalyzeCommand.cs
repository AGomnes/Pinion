using System.CommandLine;
using Pinion.Adapters.CSharp;
using Pinion.Adapters.VisualBasic;
using Pinion.Engine.Abstractions;
using Pinion.Engine.Analysis;
using Pinion.Engine.Reporting;
using Pinion.Generate;

namespace Pinion.Cli;

/// <summary>
/// `pinion analyze &lt;path&gt;` — the free, AI-free Migration Readiness Audit.
/// Scans a project/solution and reports where the team is exposed.
/// </summary>
internal static class AnalyzeCommand
{
    public static Command Build()
    {
        var pathArg = new Argument<string>("path")
        {
            Description = "Path to a .sln, .csproj, or a directory containing one.",
        };

        var formatOption = new Option<OutputFormat>("--format", "-f")
        {
            Description = "Output format for stdout.",
            DefaultValueFactory = _ => OutputFormat.Console,
        };

        var outOption = new Option<FileInfo?>("--out", "-o")
        {
            Description = "Also write the rendered report to this file.",
        };

        var topOption = new Option<int>("--top")
        {
            Description = "Show only the top N hotspots (0 = all).",
            DefaultValueFactory = _ => 0,
        };

        var thresholdOption = new Option<double>("--threshold")
        {
            Description = "Risk score (0–10) at/above which an untested method is counted high-risk.",
            DefaultValueFactory = _ => ReportOptions.Default.HighRiskThreshold,
        };

        var targetFrameworkOption = new Option<string?>("--target-framework")
        {
            Description = "Also check which framework APIs the code uses that do NOT exist on this target (e.g. net10.0). Resolved against that framework's reference assemblies — no catalog, no network.",
        };
        var coverageOption = new Option<bool>("--coverage")
        {
            Description = "Run the target's tests under Coverlet and include executed coverage % (slower).",
        };

        var mutationReportOption = new Option<FileInfo?>("--mutation-report")
        {
            Description = "Overlay per-file mutation scores in the HTML report (JSON from `prove --report-json`).",
        };

        var openOption = new Option<bool>("--open")
        {
            Description = "Open the rendered report in the default browser (a static file — no server is started).",
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Overwrite the --out file if it already exists.",
        };

        var includeRefsOption = new Option<bool>("--include-refs")
        {
            Description = "When the target is a single .csproj, also analyze its referenced projects (default: just the target project).",
        };

        var verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Print Roslyn/MSBuild diagnostics to stderr.",
        };

        var cmd = new Command("analyze", "Scan a codebase and produce a Migration Readiness Report.")
        {
            pathArg, formatOption, outOption, topOption, thresholdOption, coverageOption, mutationReportOption, openOption, forceOption, includeRefsOption, verboseOption, targetFrameworkOption,
        };

        cmd.SetAction(async (parse, ct) =>
        {
            string path = parse.GetValue(pathArg)!;
            var format = parse.GetValue(formatOption);
            var outFile = parse.GetValue(outOption);
            int top = parse.GetValue(topOption);
            double threshold = parse.GetValue(thresholdOption);
            bool coverage = parse.GetValue(coverageOption);
            string? targetFramework = parse.GetValue(targetFrameworkOption);
            var mutationReport = parse.GetValue(mutationReportOption);
            bool open = parse.GetValue(openOption);
            bool force = parse.GetValue(forceOption);
            bool includeRefs = parse.GetValue(includeRefsOption);
            bool verbose = parse.GetValue(verboseOption);

            return await RunAsync(path, format, outFile, top, threshold, coverage, mutationReport, open, force, includeRefs, verbose, targetFramework, ct);
        });

        return cmd;
    }

    private static async Task<int> RunAsync(
        string path, OutputFormat format, FileInfo? outFile,
        int top, double threshold, bool collectCoverage, FileInfo? mutationReport, bool open, bool force, bool includeRefs, bool verbose, string? targetFramework, CancellationToken ct)
    {
        Action<string>? log = verbose ? msg => Console.Error.WriteLine(msg) : null;

        try
        {
            ILanguageAdapter adapter = IsVisualBasic(path)
                ? new VisualBasicAdapter(log)
                : new CSharpAdapter(log, includeReferencedProjects: includeRefs);

            Console.Error.WriteLine($"Analyzing {path} …");
            var units = await adapter.AnalyzeAsync(path, ct);

            if (ContainsBothDotNetLanguages(path))
            {
                string analyzed = IsVisualBasic(path) ? "VB.NET" : "C#";
                string other = IsVisualBasic(path) ? "C#" : "VB.NET";
                Console.Error.WriteLine($"note: this input also contains {other} project(s), which were NOT analyzed — " +
                    $"Pinion reports one language per run ({analyzed} here). Run `pinion analyze` on the {other} project(s) separately.");
            }

            CoverageSummary? coverage = null;
            if (collectCoverage)
            {
                Console.Error.WriteLine("Collecting executed coverage (running tests) …");
                coverage = await new CoverageCollector(log).CollectAsync(path, ct);
                if (coverage is null)
                    Console.Error.WriteLine("warning: coverage unavailable; continuing without it.");
            }

            var options = ReportOptions.Default with
            {
                HighRiskThreshold = threshold,
                TopHotspots = top,
            };
            var report = ReportBuilder.Build(path, units, options, coverage: coverage);

            // Opt-in: resolving against the target framework needs its reference assemblies, and the
            // answer is only meaningful when the code actually resolved, so it is never implied.
            if (targetFramework is { Length: > 0 } tfm && adapter is CSharpAdapter csharpAdapter)
            {
                var incompatible = csharpAdapter.CheckTargetCompatibility(tfm, ct);
                report = report with { TargetFramework = tfm, IncompatibleApis = incompatible };
            }

            string rendered = format switch
            {
                OutputFormat.Markdown => MarkdownReportRenderer.Render(report),
                OutputFormat.Json => JsonReportRenderer.Render(report),
                OutputFormat.Html => RenderHtml(report, mutationReport, log),
                _ => ConsoleReportRenderer.Render(report),
            };

            if (format != OutputFormat.Html)
                Console.Out.WriteLine(rendered);
            else if (outFile is null && !open)
                Console.Error.WriteLine("note: pass --out report.html (or --open) to view the dashboard (HTML is a file artifact).");

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

            if (open) await ReportOutput.OpenAsync(rendered, format, outFile, "pinion-analyze", ct);

            if (report.TestedMethods == 0 && report.ScannedMethods > 0 && !ResolvedToSolution(path))
                Console.Error.WriteLine(
                    "note: 0 methods are referenced by tests, so Behavior coverage reads 0%. If this " +
                    "project's tests live in a separate test project, point Pinion at the .sln/.slnx — " +
                    "opening a single .csproj loads the project but not its test project.");

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

    /// <summary>True if the input (a solution or a directory) holds BOTH C# and VB.NET projects — only
    /// one language is analyzed per run, so the other would be silently skipped without a note.</summary>
    private static bool ContainsBothDotNetLanguages(string input)
    {
        bool cs, vb;
        if (File.Exists(input) &&
            (input.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || input.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
        {
            string text;
            try { text = File.ReadAllText(input); } catch { return false; }
            cs = text.Contains(".csproj", StringComparison.OrdinalIgnoreCase);
            vb = text.Contains(".vbproj", StringComparison.OrdinalIgnoreCase);
        }
        else if (Directory.Exists(input))
        {
            cs = HasProject(input, "*.csproj");
            vb = HasProject(input, "*.vbproj");
        }
        else return false;

        return cs && vb;
    }

    private static bool HasProject(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern,
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Any();
        }
        catch { return false; }
    }

    /// <summary>VB.NET input: a <c>.vbproj</c> file, or a directory whose only project is a <c>.vbproj</c>.
    /// Internal — GenerateCommand routes with the same rule so analyze and generate always agree.</summary>
    internal static bool IsVisualBasic(string input)
    {
        if (File.Exists(input)) return input.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(input))
            return Directory.EnumerateFiles(input, "*.vbproj").Any()
                && !Directory.EnumerateFiles(input, "*.csproj").Any()
                && !Directory.EnumerateFiles(input, "*.sln").Concat(Directory.EnumerateFiles(input, "*.slnx")).Any();
        return false;
    }

    /// <summary>
    /// Did the analyzed input resolve to a solution (vs a single project)? Mirrors the adapter's
    /// input resolution: a directory containing a .sln/.slnx loads the solution; otherwise it's a
    /// single project and test projects that reference it won't be in the graph.
    /// </summary>
    private static bool ResolvedToSolution(string input)
    {
        if (File.Exists(input))
            return input.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || input.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(input))
            return Directory.EnumerateFiles(input, "*.sln")
                .Concat(Directory.EnumerateFiles(input, "*.slnx")).Any();
        return false;
    }

    /// <summary>Render the HTML dashboard, overlaying per-file + overall mutation scores when available.</summary>
    private static string RenderHtml(AnalysisReport report, FileInfo? mutationReport, Action<string>? log)
    {
        var mutation = LoadMutation(mutationReport, log);
        if (mutation is null)
            return HtmlReportRenderer.Render(report);

        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in mutation.Files)
            map[Path.GetFileName(f.File)] = f.Score;

        return HtmlReportRenderer.Render(report, map, mutation.Score);
    }

    private static MutationReport? LoadMutation(FileInfo? mutationReport, Action<string>? log)
    {
        if (mutationReport is null) return null;
        try
        {
            var report = System.Text.Json.JsonSerializer.Deserialize<MutationReport>(File.ReadAllText(mutationReport.FullName));
            return report?.Files is null ? null : report;
        }
        catch (Exception ex)
        {
            log?.Invoke($"warning: could not read mutation report ({ex.Message}); rendering without scores.");
            Console.Error.WriteLine("warning: could not read --mutation-report; rendering the dashboard without scores.");
            return null;
        }
    }
}
