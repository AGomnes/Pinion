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
        sb.AppendLine();

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
                // Pad the name column for readability; let long names overflow gracefully.
                string left = $"{i,2}. {name}";
                // Flag the actionable case: a risky unit that needs a seam introduced before it can be locked.
                string seam = s.Unit.Seamability == Seamability.NeedsSeam && s.Unit.SeamBlockers.Count > 0
                    ? "  ⚠ needs seam: " + string.Join(", ", s.Unit.SeamBlockers.Take(2))
                    : "";
                sb.AppendLine($"{left,-42} risk {s.Score.Total,4:0.0}  ← {ReportText.Reason(s.Score)}{seam}");
                i++;
            }
        }

        sb.AppendLine(rule);
        sb.AppendLine($"Estimated behavior-lock effort: {ReportText.Effort(report.HighRiskUnprotected)}");

        if (report.Hotspots.Count > top.Count)
            sb.AppendLine($"({ReportText.N(report.Hotspots.Count - top.Count)} more units in the JSON/Markdown report)");

        return sb.ToString();
    }
}
