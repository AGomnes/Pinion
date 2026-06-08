using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Pinion.Engine.Reporting;

/// <summary>Renders the <see cref="BehaviorDiffReport"/> — console, Markdown (the shareable proof
/// asset), and JSON (for CI tooling).</summary>
public static class BehaviorDiffRenderer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static string Json(BehaviorDiffReport report) => JsonSerializer.Serialize(report, Options);

    public static string Console(BehaviorDiffReport report)
    {
        var sb = new StringBuilder();
        string project = Path.GetFileName(report.TestProject.TrimEnd('/', '\\'));
        sb.AppendLine($"BEHAVIOR VERIFICATION — {project}");

        if (report.BuildFailed)
        {
            sb.AppendLine();
            sb.AppendLine("✗ The locked characterization tests did NOT compile against the current code —");
            sb.AppendLine("  the migration broke the build, so behavior could not be verified:");
            foreach (var e in report.BuildErrors ?? Array.Empty<string>()) sb.AppendLine($"    {e}");
            return sb.ToString();
        }

        if (report.Total == 0)
        {
            sb.AppendLine();
            sb.AppendLine("No locked behavior found. Run `pinion generate` first to capture golden masters.");
            return sb.ToString();
        }

        sb.AppendLine($"Re-ran {report.Total} locked method(s) against the current code.");
        sb.AppendLine();
        if (report.BehaviorPreserved)
        {
            sb.AppendLine($"✓ Behavior preserved: all {report.Total} method(s) behave identically — safe to ship.");
            return sb.ToString();
        }

        sb.AppendLine($"✓ Identical: {report.Identical} of {report.Total}");
        sb.AppendLine($"⚠ CHANGED:   {report.Changed} of {report.Total}   (− was locked  ·  + current code)");
        sb.AppendLine();
        sb.AppendLine("CHANGED METHODS");
        sb.AppendLine(new string('─', 60));
        int i = 1;
        foreach (var e in report.Entries.Where(e => e.Status == BehaviorChange.Changed))
        {
            sb.AppendLine($"{i}. {e.Name}");
            foreach (var line in (e.Diff ?? "").Split('\n')) sb.AppendLine("   " + line);
            sb.AppendLine();
            i++;
        }
        return sb.ToString();
    }

    public static string Markdown(BehaviorDiffReport report)
    {
        var sb = new StringBuilder();
        string project = Path.GetFileName(report.TestProject.TrimEnd('/', '\\'));
        sb.AppendLine($"# Behavior Verification — {project}");
        sb.AppendLine();
        sb.AppendLine($"_Generated {report.GeneratedAt:yyyy-MM-dd HH:mm}_");
        sb.AppendLine();

        if (report.BuildFailed)
        {
            sb.AppendLine("> ✗ **The locked characterization tests did not compile against the current code.** " +
                          "The migration broke the build, so behavior could not be verified.");
            sb.AppendLine();
            sb.AppendLine("```");
            foreach (var e in report.BuildErrors ?? Array.Empty<string>()) sb.AppendLine(e);
            sb.AppendLine("```");
            return sb.ToString();
        }

        if (report.Total == 0)
        {
            sb.AppendLine("No locked behavior found. Run `pinion generate` first to capture golden masters.");
            return sb.ToString();
        }

        sb.AppendLine("| Result | Count |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Methods verified | {report.Total} |");
        sb.AppendLine($"| ✓ Identical | {report.Identical} |");
        sb.AppendLine($"| ⚠ Changed | {report.Changed} |");
        sb.AppendLine();
        sb.AppendLine(report.BehaviorPreserved
            ? $"**✓ Behavior preserved — all {report.Total} method(s) behave identically.**"
            : $"**⚠ {report.Changed} method(s) changed behavior** (`−` was locked, `+` current code):");
        sb.AppendLine();

        foreach (var e in report.Entries.Where(e => e.Status == BehaviorChange.Changed))
        {
            sb.AppendLine($"### {e.Name}");
            sb.AppendLine();
            sb.AppendLine("```diff");
            sb.AppendLine(e.Diff);
            sb.AppendLine("```");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
