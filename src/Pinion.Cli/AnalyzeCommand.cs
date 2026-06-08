using System.CommandLine;
using Pinion.Adapters.CSharp;
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

        var verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Print Roslyn/MSBuild diagnostics to stderr.",
        };

        var cmd = new Command("analyze", "Scan a codebase and produce a Migration Readiness Report.")
        {
            pathArg, formatOption, outOption, topOption, thresholdOption, coverageOption, mutationReportOption, openOption, verboseOption,
        };

        cmd.SetAction(async (parse, ct) =>
        {
            string path = parse.GetValue(pathArg)!;
            var format = parse.GetValue(formatOption);
            var outFile = parse.GetValue(outOption);
            int top = parse.GetValue(topOption);
            double threshold = parse.GetValue(thresholdOption);
            bool coverage = parse.GetValue(coverageOption);
            var mutationReport = parse.GetValue(mutationReportOption);
            bool open = parse.GetValue(openOption);
            bool verbose = parse.GetValue(verboseOption);

            return await RunAsync(path, format, outFile, top, threshold, coverage, mutationReport, open, verbose, ct);
        });

        return cmd;
    }

    private static async Task<int> RunAsync(
        string path, OutputFormat format, FileInfo? outFile,
        int top, double threshold, bool collectCoverage, FileInfo? mutationReport, bool open, bool verbose, CancellationToken ct)
    {
        Action<string>? log = verbose ? msg => Console.Error.WriteLine(msg) : null;

        try
        {
            var adapter = new CSharpAdapter(log);

            Console.Error.WriteLine($"Analyzing {path} …");
            var units = await adapter.AnalyzeAsync(path, ct);

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

            string rendered = format switch
            {
                OutputFormat.Markdown => MarkdownReportRenderer.Render(report),
                OutputFormat.Json => JsonReportRenderer.Render(report),
                OutputFormat.Html => RenderHtml(report, mutationReport, log),
                _ => ConsoleReportRenderer.Render(report),
            };

            // The HTML dashboard is a file artifact — printing it to the console is noise; write it instead.
            if (format != OutputFormat.Html)
                Console.Out.WriteLine(rendered);
            else if (outFile is null && !open)
                Console.Error.WriteLine("note: pass --out report.html (or --open) to view the dashboard (HTML is a file artifact).");

            if (outFile is not null)
            {
                outFile.Directory?.Create();
                await File.WriteAllTextAsync(outFile.FullName, rendered, ct);
                Console.Error.WriteLine($"Wrote {format} report to {outFile.FullName}");
            }

            if (open) await ReportOutput.OpenAsync(rendered, format, outFile, "pinion-analyze", ct);

            // Opening a single production .csproj loads it and its dependencies, but NOT the test
            // project that references it — so coverage reads 0% even when the code is well-tested.
            // Nudge the user toward the solution so test references actually resolve. (To stderr, so
            // JSON/Markdown stdout stays clean.)
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

        // Per-file score map (file name → 0–100); last write wins if two paths share a name.
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in mutation.Files)
            map[Path.GetFileName(f.File)] = f.Score;

        // Pass the true mutant-weighted overall so the headline matches `prove` exactly.
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
