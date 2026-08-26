using System.IO;
using System.Text;
using Pinion.Engine.Analysis;
using Pinion.Engine.Model;

namespace Pinion.Engine.Reporting;

/// <summary>Renders the human-readable console form of the Migration Readiness Report.</summary>
public static class ConsoleReportRenderer
{
    private const int HotspotLimit = 15;

    public static string Render(AnalysisReport report)
    {
        var sb = new StringBuilder();
        string projectName = Path.GetFileName(report.ProjectPath.TrimEnd('/', '\\'));

        sb.AppendLine($"MIGRATION READINESS REPORT — {projectName}");
        sb.AppendLine($"Scanned: {ReportText.N(report.ScannedMethods)} methods across {ReportText.N(report.ScannedFiles)} files");
        sb.AppendLine();

        sb.AppendLine($"Behavior coverage:        {ReportText.Percent(report.BehaviorCoverage)} " +
                      $"({ReportText.N(report.TestedMethods)} of {ReportText.N(report.ScannedMethods)} methods tested)");
        if (report.Coverage is { } cov)
            sb.AppendLine($"Executed coverage:        {ReportText.Percent(cov.LineRate)} lines, " +
                          $"{ReportText.Percent(cov.BranchRate)} branches (Coverlet)");
        sb.AppendLine($"High-risk & UNPROTECTED:  {ReportText.N(report.HighRiskUnprotected)} methods");
        sb.AppendLine($"Seams to introduce:       {ReportText.N(report.SeamsToIntroduce)} (high-risk methods that hard-wire deps — no test seam)");
        sb.AppendLine($"Migration landmines:      {ReportText.LandmineSummary(report.LandmineCounts)}");
        if (report.TargetFramework is { Length: > 0 } tfm)
        {
            int n = report.IncompatibleApis?.Count ?? 0;
            // Same 26-column label gutter as the lines above; the target moniker varies in length, so
            // the label must be padded rather than spaced by hand.
            sb.AppendLine($"{$"Unavailable on {tfm}:",-26}{(n == 0 ? "none detected" : ReportText.N(n) + " framework type(s)")}");
        }
        sb.AppendLine();

        if (report.IncompatibleApis is { Count: > 0 } apis)
        {
            sb.AppendLine($"APIS UNAVAILABLE ON {report.TargetFramework}");
            sb.AppendLine(new string('─', 60));
            foreach (var a in apis.Take(15))
                sb.AppendLine($"  {ReportText.FriendlyTypeName(a.TypeName)}  ({a.UsageCount} use{(a.UsageCount == 1 ? "" : "s")})  first: {Path.GetFileName(a.FirstFilePath)}:{a.FirstLine}");
            if (apis.Count > 15) sb.AppendLine($"  … and {apis.Count - 15} more");
            sb.AppendLine(new string('─', 60));
            sb.AppendLine("These must be replaced before the migration compiles. Lock the behavior of the");
            sb.AppendLine("methods that use them FIRST, so the replacement can be proved equivalent.");
            sb.AppendLine();
        }

        sb.AppendLine("TOP RISK HOTSPOTS");
        string rule = new('─', 60);
        sb.AppendLine(rule);

        var top = report.Hotspots.Take(HotspotLimit).ToList();
        if (top.Count == 0)
        {
            sb.AppendLine("(no code units found)");
        }
        else
        {
            int i = 1;
            foreach (var s in top)
            {
                string name = s.Unit.DisplayName + "()";
                string left = $"{i,2}. {name}";
                string seam = s.Unit.Seamability == Seamability.NeedsSeam && s.Unit.SeamBlockers.Count > 0
                    ? "  ⚠ needs seam: " + string.Join(", ", s.Unit.SeamBlockers.Take(2))
                    : "";
                // Truncate rather than only pad: a name longer than the column pushes every following
                // field right, and a risk column that does not line up cannot be scanned.
                if (left.Length > 42) left = left[..41] + "…";
                sb.AppendLine($"{left,-42} risk {s.Score.Total,4:0.0}  ← {ReportText.Reason(s.Score)}{seam}");
                i++;
            }
        }

        sb.AppendLine(rule);
        sb.AppendLine($"Estimated behavior-lock effort: {ReportText.Effort(report.HighRiskUnprotected)}");

        // That figure is the whole backlog, and on a large codebase it reads as hopeless. Nobody locks
        // everything: the point is to cover the riskiest first, so the report ends on an action rather
        // than on a number.
        if (report.HighRiskUnprotected > 0)
        {
            int start = Math.Min(10, report.HighRiskUnprotected);
            sb.AppendLine($"You do not have to lock all of them. Start with the {start} riskiest:");
            sb.AppendLine($"  pinion quickstart <project> --top {start}     ({ReportText.Effort(start)})");
        }

        if (report.Hotspots.Count > top.Count)
            sb.AppendLine($"({ReportText.N(report.Hotspots.Count - top.Count)} more units in the JSON/Markdown report)");

        return sb.ToString();
    }
}
