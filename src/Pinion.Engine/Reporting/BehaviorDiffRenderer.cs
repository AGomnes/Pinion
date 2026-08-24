using System.IO;
using System.Net;
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

        sb.AppendLine(new string('─', 60));
        sb.AppendLine("TRIAGE — review each diff above:");
        sb.AppendLine($"  • Intended?  re-baseline it:  pinion accept \"{report.TestProject}\" --name <method>");
        sb.AppendLine("  • Unexpected?  it's a regression — fix the code, not the snapshot.");
        return sb.ToString();
    }

    /// <summary>A single self-contained, offline HTML page — the shareable proof artifact. Green when
    /// behavior is preserved, red diffs per changed method. No network request, no JS (native
    /// &lt;details&gt; for collapse), nothing leaves the machine.</summary>
    public static string Html(BehaviorDiffReport report)
    {
        string project = Path.GetFileName(report.TestProject.TrimEnd('/', '\\'));
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append($"<title>Pinion — behavior verification — {Enc(project)}</title>\n");
        sb.Append("<style>\n").Append(HtmlCss).Append("\n</style>\n</head>\n<body>\n");

        sb.Append("<header><h1>Pinion <span class=\"muted\">behavior verification</span></h1>\n");
        sb.Append($"<p class=\"muted\">{Enc(project)} · generated {Enc(report.GeneratedAt.ToString("yyyy-MM-dd HH:mm"))}</p></header>\n");

        if (report.BuildFailed)
        {
            sb.Append("<section class=\"banner bad\"><strong>✗ The migration broke the build.</strong> The locked characterization tests no longer compile against the current code, so behavior could not be verified.</section>\n");
            sb.Append("<pre class=\"errors\">");
            foreach (var e in report.BuildErrors ?? Array.Empty<string>()) sb.Append(Enc(e)).Append('\n');
            sb.Append("</pre>\n");
            return Close(sb);
        }

        if (report.Total == 0)
        {
            sb.Append("<section class=\"banner warn\">No locked behavior found. Run <code>pinion generate</code> first to capture golden masters.</section>\n");
            return Close(sb);
        }

        sb.Append("<section class=\"cards\">\n");
        Card(sb, report.Total.ToString(), "methods verified", "neutral");
        Card(sb, report.Identical.ToString(), "behave identically", "good");
        Card(sb, report.Changed.ToString(), "changed behavior", report.Changed > 0 ? "bad" : "good");
        sb.Append("</section>\n");

        if (report.BehaviorPreserved)
            sb.Append($"<section class=\"banner good\"><strong>✓ Behavior preserved.</strong> All {report.Total} method(s) behave identically against the current code — safe to ship.</section>\n");
        else
            sb.Append($"<section class=\"banner bad\"><strong>⚠ {report.Changed} of {report.Total} method(s) changed behavior.</strong> <span class=\"muted\">− was locked · + current code</span></section>\n");

        foreach (var e in report.Entries.Where(e => e.Status == BehaviorChange.Changed))
        {
            sb.Append("<details open class=\"change\">\n");
            sb.Append($"  <summary><code>{Enc(e.Name)}</code></summary>\n");
            sb.Append("  <div class=\"diff\">\n");
            foreach (var line in (e.Diff ?? "").Split('\n'))
            {
                string cls = line.StartsWith("- ") ? "del" : line.StartsWith("+ ") ? "ins"
                    : line.TrimStart().StartsWith("…") ? "gap" : "ctx";
                sb.Append($"    <div class=\"{cls}\">{Enc(line)}</div>\n");
            }
            sb.Append("  </div>\n</details>\n");
        }

        return Close(sb);
    }

    private static string Close(StringBuilder sb)
    {
        sb.Append("<footer class=\"muted\">This report is self-contained and offline — no data left your machine.</footer>\n");
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    private static void Card(StringBuilder sb, string value, string label, string band) =>
        sb.Append($"  <div class=\"card {band}\"><div class=\"v\">{Enc(value)}</div><div class=\"l\">{Enc(label)}</div></div>\n");

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

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

    private const string HtmlCss = """
        :root {
          --bg: #f6f7f9; --panel: #fff; --ink: #1c2530; --muted: #6b7787; --line: #e4e8ee;
          --accent: #3b6ef5; --good: #1a8f4a; --good-bg: #e6f5ec; --warn: #b7791f; --warn-bg: #fbf2e0;
          --bad: #c0392b; --bad-bg: #fbe9e7;
          --del-bg: #fbe9e7; --del-ink: #b1342a; --ins-bg: #e6f5ec; --ins-ink: #1a7f43;
          --shadow: 0 1px 2px rgba(16,24,40,.06), 0 1px 3px rgba(16,24,40,.08);
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg: #0f141a; --panel: #161c24; --ink: #e7ecf2; --muted: #9aa6b4; --line: #28313c;
            --accent: #6c97ff; --good: #46c97e; --good-bg: #14301f; --warn: #e0a83a; --warn-bg: #30260f;
            --bad: #ef6f5e; --bad-bg: #341a17;
            --del-bg: #341a17; --del-ink: #ef8a7c; --ins-bg: #14301f; --ins-ink: #5fcf8f; --shadow: none;
          }
        }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--ink); line-height: 1.45; font-size: 14px;
          font-family: "Segoe UI", -apple-system, BlinkMacSystemFont, Roboto, Helvetica, Arial, sans-serif;
          padding: 28px clamp(16px, 4vw, 48px) 64px; }
        code, .diff { font-family: "Cascadia Code", Consolas, "SF Mono", Menlo, monospace; }
        h1 { font-size: 22px; margin: 0 0 4px; font-weight: 650; } h1 .muted { font-weight: 400; }
        .muted { color: var(--muted); } header p { margin: 0; font-size: 13px; }
        header { margin-bottom: 20px; }
        .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 12px; margin-bottom: 18px; }
        .card { background: var(--panel); border: 1px solid var(--line); border-left: 4px solid var(--line);
          border-radius: 12px; padding: 14px 16px; box-shadow: var(--shadow); }
        .card .v { font-size: 28px; font-weight: 680; letter-spacing: -.02em; }
        .card .l { font-size: 12px; color: var(--muted); margin-top: 2px; }
        .card.good { border-left-color: var(--good); } .card.bad { border-left-color: var(--bad); }
        .card.neutral { border-left-color: var(--accent); }
        .banner { border-radius: 10px; padding: 12px 15px; margin-bottom: 16px; font-size: 14px; border: 1px solid var(--line); }
        .banner.good { background: var(--good-bg); border-color: color-mix(in srgb, var(--good) 35%, transparent); }
        .banner.good strong { color: var(--good); }
        .banner.bad { background: var(--bad-bg); border-color: color-mix(in srgb, var(--bad) 35%, transparent); }
        .banner.bad strong { color: var(--bad); }
        .banner.warn { background: var(--warn-bg); border-color: color-mix(in srgb, var(--warn) 35%, transparent); }
        details.change { background: var(--panel); border: 1px solid var(--line); border-radius: 10px;
          margin-bottom: 12px; box-shadow: var(--shadow); overflow: hidden; }
        details.change summary { cursor: pointer; padding: 11px 15px; font-weight: 600; list-style: none; background: color-mix(in srgb, var(--accent) 5%, var(--panel)); }
        details.change summary::-webkit-details-marker { display: none; }
        details.change summary code { font-size: 13.5px; }
        .diff { font-size: 12.5px; padding: 6px 0; overflow-x: auto; }
        .diff > div { padding: 1px 15px; white-space: pre; }
        .diff .del { background: var(--del-bg); color: var(--del-ink); }
        .diff .ins { background: var(--ins-bg); color: var(--ins-ink); }
        .diff .gap { color: var(--muted); } .diff .ctx { color: var(--ink); }
        pre.errors { background: var(--panel); border: 1px solid var(--line); border-radius: 10px; padding: 12px 15px; overflow-x: auto; font-size: 12.5px; color: var(--bad); }
        footer { margin-top: 20px; font-size: 12px; }
        """;
}
