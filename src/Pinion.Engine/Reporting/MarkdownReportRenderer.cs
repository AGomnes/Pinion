using System.IO;
using System.Text;
using Pinion.Engine.Analysis;

namespace Pinion.Engine.Reporting;

/// <summary>Renders the Markdown form of the report — the shareable sales asset.</summary>
public static class MarkdownReportRenderer
{
    public static string Render(AnalysisReport report)
    {
        var sb = new StringBuilder();
        string projectName = Path.GetFileName(report.ProjectPath.TrimEnd('/', '\\'));

        sb.AppendLine($"# Migration Readiness Report — {projectName}");
        sb.AppendLine();
        sb.AppendLine($"_Generated {report.GeneratedAt:yyyy-MM-dd HH:mm} • {ReportText.N(report.ScannedMethods)} methods across {ReportText.N(report.ScannedFiles)} files_");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Behavior coverage | {ReportText.Percent(report.BehaviorCoverage)} ({ReportText.N(report.TestedMethods)} of {ReportText.N(report.ScannedMethods)} tested) |");
        if (report.Coverage is { } cov)
            sb.AppendLine($"| Executed coverage (Coverlet) | {ReportText.Percent(cov.LineRate)} lines, {ReportText.Percent(cov.BranchRate)} branches |");
        sb.AppendLine($"| High-risk & unprotected | {ReportText.N(report.HighRiskUnprotected)} methods (risk ≥ {report.HighRiskThreshold:0.0}) |");
        sb.AppendLine($"| Seams to introduce | {ReportText.N(report.SeamsToIntroduce)} high-risk methods hard-wire deps (no test seam) |");
        sb.AppendLine($"| Migration landmines | {ReportText.LandmineSummary(report.LandmineCounts)} |");
        sb.AppendLine($"| Estimated behavior-lock effort | {ReportText.Effort(report.HighRiskUnprotected)} |");
        sb.AppendLine();

        sb.AppendLine("## Top risk hotspots");
        sb.AppendLine();
        sb.AppendLine("| # | Unit | Risk | Why | Seam | File |");
        sb.AppendLine("|---:|---|---:|---|---|---|");

        int i = 1;
        foreach (var s in report.Hotspots)
        {
            string file = Path.GetFileName(s.Unit.FilePath);
            string location = string.IsNullOrEmpty(file) ? "" : $"{file}:{s.Unit.StartLine}";
            string seam = s.Unit.Seamability switch
            {
                Pinion.Engine.Model.Seamability.NeedsSeam => "⚠ needs seam: " + string.Join(", ", s.Unit.SeamBlockers.Take(2)),
                Pinion.Engine.Model.Seamability.SeamAvailable => "✓ " + string.Join(", ", s.Unit.SeamPoints.Take(2)),
                _ => "",
            };
            sb.AppendLine($"| {i} | `{s.Unit.DisplayName}` | {s.Score.Total:0.0} | {ReportText.Reason(s.Score)} | {seam} | {location} |");
            i++;
        }
        sb.AppendLine();

        sb.AppendLine($"_Risk weights: complexity {report.Weights.Complexity}, untested {report.Weights.NoTests}, " +
                      $"domain {report.Weights.Domain}, blast-radius {report.Weights.Callers}, size {report.Weights.Size}, " +
                      $"landmine {report.Weights.Landmine}. Every score above is the weighted sum of these — fully auditable._");

        return sb.ToString();
    }
}
