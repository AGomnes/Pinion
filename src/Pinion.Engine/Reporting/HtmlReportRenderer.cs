using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Pinion.Engine.Analysis;

namespace Pinion.Engine.Reporting;

/// <summary>
/// Renders the report as a single self-contained "command center" HTML dashboard: headline metric
/// cards plus one sortable/filterable table of methods with inline risk bars and a click-to-expand
/// score breakdown. Everything (CSS, Alpine.js, data) is inlined — the file makes NO network request,
/// so it opens offline forever and never leaks the analyzed code's shape to a CDN.
/// </summary>
public static class HtmlReportRenderer
{
    /// <param name="fileScores">Optional per-file mutation score (0–100), keyed by file name, from `prove`.</param>
    /// <param name="overallMutation">The true overall mutation score (0–100) from `prove` — mutant-weighted,
    /// so the headline matches `prove` exactly rather than a per-file average.</param>
    public static string Render(AnalysisReport report, IReadOnlyDictionary<string, double>? fileScores = null, double? overallMutation = null)
    {
        string project = Path.GetFileName(report.ProjectPath.TrimEnd('/', '\\'));
        string rowsJson = BuildRowsJson(report, fileScores);

        double coverage = report.BehaviorCoverage * 100;
        int landmines = report.LandmineCounts.Values.Sum();
        double? mutation = overallMutation ?? AverageMutation(report, fileScores);

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append($"<title>Pinion — {Enc(project)}</title>\n");
        sb.Append("<style>\n").Append(Css).Append("\n</style>\n</head>\n<body>\n");

        sb.Append("<header class=\"app\">\n");
        sb.Append($"  <h1>Pinion <span class=\"muted\">behavior report</span></h1>\n");
        sb.Append($"  <p class=\"muted\">{Enc(project)} · {report.ScannedMethods:N0} methods across {report.ScannedFiles:N0} files · generated {Enc(report.GeneratedAt.ToString("yyyy-MM-dd HH:mm"))}</p>\n");
        sb.Append("</header>\n");

        sb.Append("<section class=\"cards\">\n");
        if (mutation is { } m)
            Card(sb, $"{m:0}%", "mutation score", Band(m, 50, 75, invert: true));
        Card(sb, $"{coverage:0}%", $"behavior coverage ({report.TestedMethods:N0}/{report.ScannedMethods:N0})", Band(coverage, 40, 70, invert: true));
        Card(sb, report.HighRiskUnprotected.ToString("N0"), $"high-risk & unprotected (risk ≥ {report.HighRiskThreshold:0.0})", report.HighRiskUnprotected > 0 ? "bad" : "good");
        Card(sb, landmines.ToString("N0"), "migration landmines", landmines > 0 ? "warn" : "good");
        Card(sb, Effort(report.HighRiskUnprotected), "estimated lock effort", "neutral");
        sb.Append("</section>\n");

        if (landmines > 0)
        {
            string summary = string.Join(", ", report.LandmineCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}"));
            sb.Append($"<section class=\"banner\"><strong>⚠ Migration landmines:</strong> {Enc(summary)} — these .NET Framework→Core hazards can block a clean upgrade. Lock the behavior around them before you touch the framework.</section>\n");
        }

        bool hasScores = fileScores is { Count: > 0 };
        int cols = hasScores ? 7 : 6;
        string thresholdJs = report.HighRiskThreshold.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);

        sb.Append("<main x-data=\"dashboard()\">\n");
        sb.Append("  <div class=\"controls\">\n");
        sb.Append("    <input type=\"search\" class=\"filter\" placeholder=\"Filter by name, file, or tag…\" x-model=\"q\" aria-label=\"Filter\">\n");
        sb.Append("    <label class=\"chk\"><input type=\"checkbox\" x-model=\"untestedOnly\"> untested only</label>\n");
        sb.Append("    <label class=\"chk\"><input type=\"checkbox\" x-model=\"grouped\"> group by file</label>\n");
        sb.Append("    <span class=\"count muted\" x-text=\"visible.length + ' / ' + rows.length + ' methods'\"></span>\n");
        sb.Append("  </div>\n");

        sb.Append("  <table>\n    <thead><tr>\n");
        Th(sb, "name", "Method");
        Th(sb, "cx", "Cx", "right", "Cyclomatic complexity");
        Th(sb, "blast", "Blast", "right", "Callers (blast radius)");
        sb.Append("      <th>Tags</th>\n");
        Th(sb, "tested", "Tests", "center");
        if (hasScores) Th(sb, "score", "Score", "right", "Per-file mutation score (prove)");
        Th(sb, "risk", "Risk", "right");
        sb.Append("    </tr></thead>\n");

        sb.Append("    <template x-for=\"item in display\" :key=\"item.key || item.id\">\n");
        sb.Append("    <tbody class=\"row-group\">\n");
        sb.Append("      <tr class=\"file-head\" x-show=\"item.isFile\" @click=\"toggleGroup(item.file)\">\n");
        sb.Append($"        <td colspan=\"{cols}\"><span class=\"twisty\" x-text=\"groupOpen[item.file] === false ? '▸' : '▾'\"></span> <code x-text=\"item.file\"></code><span class=\"muted\" x-text=\"' · ' + item.count + (item.count === 1 ? ' method' : ' methods')\"></span>");
        if (hasScores) sb.Append("<template x-if=\"item.score !== null\"><span :class=\"'score fhscore ' + scoreBand(item.score)\" x-text=\"item.score + '%'\"></span></template>");
        sb.Append("</td>\n      </tr>\n");
        sb.Append("      <tr class=\"row\" x-show=\"!item.isFile\" @click=\"item.open = !item.open\" :class=\"{ open: item.open, nested: grouped }\">\n");
        sb.Append("        <td class=\"name\"><span class=\"twisty\" x-text=\"item.open ? '▾' : '▸'\"></span> <code x-text=\"item.name\"></code><span class=\"loc muted\" x-text=\"' ' + item.file + ':' + item.line\"></span></td>\n");
        sb.Append("        <td class=\"right\" x-text=\"item.cx\"></td>\n");
        sb.Append("        <td class=\"right\" x-text=\"item.blast\"></td>\n");
        sb.Append("        <td><template x-for=\"t in item.tags\" :key=\"t\"><span class=\"chip\" x-text=\"t\"></span></template><template x-for=\"l in item.landmines\" :key=\"l\"><span class=\"chip mine\" x-text=\"l\"></span></template></td>\n");
        sb.Append("        <td class=\"center\"><span :class=\"item.tested ? 'pill ok' : 'pill no'\" x-text=\"item.tested ? '✓ tested' : '✗ none'\"></span></td>\n");
        if (hasScores)
            sb.Append("        <td class=\"right\"><span x-show=\"item.score !== null\" :class=\"'score ' + scoreBand(item.score)\" x-text=\"item.score === null ? '' : item.score + '%'\"></span><span x-show=\"item.score === null\" class=\"muted\">—</span></td>\n");
        sb.Append("        <td class=\"right risk\"><span class=\"bar\"><span class=\"fill\" :class=\"riskBand(item.risk)\" :style=\"'width:' + (item.risk*10) + '%'\"></span></span><span class=\"riskval\" x-text=\"item.risk.toFixed(1)\"></span></td>\n");
        sb.Append("      </tr>\n");
        sb.Append($"      <tr class=\"detail\" x-show=\"!item.isFile && item.open\" x-cloak><td colspan=\"{cols}\">\n");
        sb.Append("        <div class=\"breakdown\">\n");
        sb.Append("          <div class=\"sig\"><code x-text=\"item.signature\"></code></div>\n");
        sb.Append("          <template x-for=\"c in item.components\" :key=\"c.name\">\n");
        sb.Append($"            <div class=\"comp\" x-show=\"c.contribution > 0\"><span class=\"cname\" x-text=\"c.name\"></span><span class=\"cbar\"><span class=\"cfill\" :style=\"'width:' + Math.min(100, c.contribution/{thresholdJs}*100) + '%'\"></span></span><span class=\"cdetail muted\" x-text=\"c.detail\"></span></div>\n");
        sb.Append("          </template>\n");
        sb.Append("          <div class=\"action\" x-show=\"!item.tested\"><span class=\"muted\">Lock it:</span> <code class=\"cmd\" x-text=\"item.gencmd\"></code> <button class=\"copy\" @click.stop=\"copy(item.gencmd, $event)\">Copy</button></div>\n");
        sb.Append("          <div class=\"path muted\" x-text=\"item.path\"></div>\n");
        sb.Append("        </div>\n");
        sb.Append("      </td></tr>\n");
        sb.Append("    </tbody>\n");
        sb.Append("    </template>\n");
        sb.Append("  </table>\n");
        sb.Append("</main>\n");

        sb.Append("<footer class=\"muted\">\n");
        sb.Append($"  Risk = weighted sum (complexity {report.Weights.Complexity}, untested {report.Weights.NoTests}, domain {report.Weights.Domain}, blast {report.Weights.Callers}, size {report.Weights.Size}, landmine {report.Weights.Landmine}) — fully auditable.<br>\n");
        sb.Append("  This report is self-contained and offline — no data left your machine.\n");
        sb.Append("</footer>\n");

        sb.Append("<script>window.__PINION_ROWS__ = ").Append(rowsJson).Append(";\n");
        sb.Append(DashboardJs).Append("</script>\n");
        sb.Append("<!-- Alpine.js v3.14.8 (MIT) — vendored & inlined for offline use -->\n");
        sb.Append("<script>").Append(AlpineJs()).Append("</script>\n");
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    private static void Card(StringBuilder sb, string value, string label, string band) =>
        sb.Append($"  <div class=\"card {band}\"><div class=\"v\">{Enc(value)}</div><div class=\"l\">{Enc(label)}</div></div>\n");

    private static void Th(StringBuilder sb, string key, string label, string align = "left", string? title = null)
    {
        string t = title is null ? "" : $" title=\"{Enc(title)}\"";
        sb.Append($"      <th class=\"{align} sortable\" @click=\"sort('{key}')\"{t}>{Enc(label)}<span class=\"arrow\" x-show=\"sortKey === '{key}'\" x-text=\"sortDir === 1 ? ' ▲' : ' ▼'\"></span></th>\n");
    }

    private static string BuildRowsJson(AnalysisReport report, IReadOnlyDictionary<string, double>? fileScores)
    {
        var rows = report.Hotspots.Select(h =>
        {
            string file = Path.GetFileName(h.Unit.FilePath);
            double? score = fileScores is not null && fileScores.TryGetValue(file, out var s) ? Math.Round(s) : null;
            string method = h.Unit.SimpleName;
            return new
            {
                id = h.Unit.Id,
                open = false,
                name = h.Unit.DisplayName,
                file,
                path = h.Unit.FilePath,
                line = h.Unit.StartLine,
                signature = h.Unit.Signature,
                gencmd = $"pinion generate \"{report.ProjectPath}\" --target {method}",
                cx = h.Unit.CyclomaticComplexity,
                blast = h.Unit.CallerIds.Count,
                lines = h.Unit.LineCount,
                tags = h.Unit.DomainTags,
                landmines = h.Unit.MigrationLandmines,
                tested = h.Unit.HasTests,
                entry = h.Unit.IsPublicEntryPoint,
                risk = h.Score.Total,
                score,
                components = h.Score.Components.Select(c => new { c.Name, c.Detail, c.Contribution })
                    .Select(c => new { name = c.Name, detail = c.Detail, contribution = Math.Round(c.Contribution, 2) }),
            };
        });
        return JsonSerializer.Serialize(rows);
    }

    private static double? AverageMutation(AnalysisReport report, IReadOnlyDictionary<string, double>? fileScores)
    {
        if (fileScores is not { Count: > 0 }) return null;
        var weighted = report.Hotspots
            .Select(h => Path.GetFileName(h.Unit.FilePath))
            .Where(fileScores.ContainsKey)
            .Select(f => fileScores[f])
            .ToList();
        return weighted.Count == 0 ? null : Math.Round(weighted.Average());
    }

    private static string Band(double value, double bad, double ok, bool invert) =>
        invert
            ? value < bad ? "bad" : value < ok ? "warn" : "good"
            : value > ok ? "bad" : value > bad ? "warn" : "good";

    private static string Effort(int methodCount)
    {
        if (methodCount == 0) return "—";
        int days = (int)Math.Ceiling(methodCount / 30.0);
        return $"~{days} day{(days == 1 ? "" : "s")}";
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    private static readonly Lazy<string> AlpineSource = new(() =>
    {
        var asm = typeof(HtmlReportRenderer).Assembly;
        string name = asm.GetManifestResourceNames().First(n => n.EndsWith("alpine.min.js", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static string AlpineJs() => AlpineSource.Value;

    private const string DashboardJs = """
        function dashboard() {
          return {
            rows: window.__PINION_ROWS__,
            q: '',
            untestedOnly: false,
            grouped: false,
            groupOpen: {}, // file name -> false when collapsed (default open)
            sortKey: 'risk',
            sortDir: -1, // 1 asc, -1 desc
            get visible() {
              let r = this.rows;
              if (this.untestedOnly) r = r.filter(m => !m.tested);
              const q = this.q.trim().toLowerCase();
              if (q) r = r.filter(m => (m.name + ' ' + m.file + ' ' + m.tags.join(' ') + ' ' + m.landmines.join(' ')).toLowerCase().includes(q));
              const k = this.sortKey, d = this.sortDir;
              return [...r].sort((a, b) => {
                let x = a[k], y = b[k];
                if (x === null) x = -1; if (y === null) y = -1;
                return (x > y ? 1 : x < y ? -1 : 0) * d;
              });
            },
            // Flat list of methods, or — when grouped — file-header rows interleaved with their methods.
            get display() {
              const v = this.visible;
              if (!this.grouped) return v;
              const order = [], byFile = {};
              for (const m of v) { if (!byFile[m.file]) { byFile[m.file] = []; order.push(m.file); } byFile[m.file].push(m); }
              const out = [];
              for (const f of order) {
                out.push({ isFile: true, file: f, count: byFile[f].length, score: byFile[f][0].score, key: '__f__' + f });
                if (this.groupOpen[f] !== false) out.push(...byFile[f]);
              }
              return out;
            },
            toggleGroup(f) { this.groupOpen[f] = this.groupOpen[f] === false; },
            sort(k) {
              if (this.sortKey === k) { this.sortDir *= -1; }
              else { this.sortKey = k; this.sortDir = (k === 'name' || k === 'file') ? 1 : -1; }
            },
            riskBand(v) { return v >= 7 ? 'bad' : v >= 4 ? 'warn' : 'good'; },
            scoreBand(v) { return v === null ? '' : v >= 75 ? 'good' : v >= 50 ? 'warn' : 'bad'; },
            copy(text, ev) {
              const btn = ev.target, original = btn.textContent;
              const done = () => { btn.textContent = 'Copied ✓'; setTimeout(() => btn.textContent = original, 1200); };
              const fallback = () => {
                const ta = document.createElement('textarea');
                ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';
                document.body.appendChild(ta); ta.select();
                try { document.execCommand('copy'); } catch (e) {}
                document.body.removeChild(ta); done();
              };
              if (navigator.clipboard && window.isSecureContext)
                navigator.clipboard.writeText(text).then(done).catch(fallback);
              else fallback();
            },
          };
        }
        """;

    private const string Css = """
        :root {
          --bg: #f6f7f9; --panel: #ffffff; --ink: #1c2530; --muted: #6b7787; --line: #e4e8ee;
          --accent: #3b6ef5; --good: #1a8f4a; --good-bg: #e6f5ec; --warn: #b7791f; --warn-bg: #fbf2e0;
          --bad: #c0392b; --bad-bg: #fbe9e7; --chip: #eef1f6; --shadow: 0 1px 2px rgba(16,24,40,.06), 0 1px 3px rgba(16,24,40,.08);
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg: #0f141a; --panel: #161c24; --ink: #e7ecf2; --muted: #9aa6b4; --line: #28313c;
            --accent: #6c97ff; --good: #46c97e; --good-bg: #14301f; --warn: #e0a83a; --warn-bg: #30260f;
            --bad: #ef6f5e; --bad-bg: #341a17; --chip: #222b35; --shadow: none;
          }
        }
        * { box-sizing: border-box; }
        [x-cloak] { display: none !important; }
        body {
          margin: 0; background: var(--bg); color: var(--ink); line-height: 1.45;
          font-family: "Segoe UI", -apple-system, BlinkMacSystemFont, Roboto, Helvetica, Arial, sans-serif;
          font-size: 14px; padding: 28px clamp(16px, 4vw, 48px) 64px;
        }
        code, .loc, .path, .sig code { font-family: "Cascadia Code", Consolas, "SF Mono", Menlo, monospace; }
        h1 { font-size: 22px; margin: 0 0 4px; font-weight: 650; }
        h1 .muted { font-weight: 400; }
        .muted { color: var(--muted); }
        header.app { margin-bottom: 20px; }
        header.app p { margin: 0; font-size: 13px; }
        .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 12px; margin-bottom: 22px; }
        .card { background: var(--panel); border: 1px solid var(--line); border-radius: 12px; padding: 14px 16px; box-shadow: var(--shadow); border-left: 4px solid var(--line); }
        .card .v { font-size: 26px; font-weight: 680; letter-spacing: -.02em; }
        .card .l { font-size: 12px; color: var(--muted); margin-top: 2px; }
        .card.good { border-left-color: var(--good); } .card.warn { border-left-color: var(--warn); }
        .card.bad { border-left-color: var(--bad); } .card.neutral { border-left-color: var(--accent); }
        .controls { display: flex; gap: 14px; align-items: center; margin-bottom: 10px; flex-wrap: wrap; }
        .filter { flex: 1 1 280px; max-width: 420px; padding: 9px 12px; border: 1px solid var(--line); border-radius: 9px; background: var(--panel); color: var(--ink); font-size: 14px; }
        .filter:focus { outline: 2px solid var(--accent); outline-offset: 0; border-color: var(--accent); }
        .chk { display: inline-flex; gap: 6px; align-items: center; cursor: pointer; user-select: none; }
        .count { font-size: 12px; margin-left: auto; }
        table { width: 100%; border-collapse: collapse; background: var(--panel); border: 1px solid var(--line); border-radius: 12px; overflow: hidden; box-shadow: var(--shadow); }
        thead th { position: sticky; top: 0; background: var(--panel); text-align: left; font-size: 12px; font-weight: 600; color: var(--muted); padding: 11px 12px; border-bottom: 1px solid var(--line); white-space: nowrap; z-index: 1; }
        th.sortable { cursor: pointer; } th.sortable:hover { color: var(--ink); }
        .right { text-align: right; } .center { text-align: center; }
        tbody.row-group { border-top: 1px solid var(--line); }
        tbody.row-group:first-of-type { border-top: none; }
        tr.row { cursor: pointer; } tr.row:hover td { background: color-mix(in srgb, var(--accent) 6%, transparent); }
        tr.row.open td { background: color-mix(in srgb, var(--accent) 9%, transparent); }
        td { padding: 9px 12px; vertical-align: middle; }
        td.name code { font-weight: 600; } .twisty { color: var(--muted); display: inline-block; width: 12px; }
        .loc { font-size: 12px; }
        .chip { display: inline-block; background: var(--chip); color: var(--ink); border-radius: 999px; padding: 1px 8px; font-size: 11px; margin: 1px 3px 1px 0; }
        .chip.mine { background: var(--bad-bg); color: var(--bad); }
        .pill { font-size: 11px; font-weight: 600; padding: 2px 8px; border-radius: 999px; white-space: nowrap; }
        .pill.ok { background: var(--good-bg); color: var(--good); } .pill.no { background: var(--bad-bg); color: var(--bad); }
        .score { font-weight: 650; } .score.good { color: var(--good); } .score.warn { color: var(--warn); } .score.bad { color: var(--bad); }
        td.risk { white-space: nowrap; } .bar { display: inline-block; width: 64px; height: 7px; background: var(--line); border-radius: 4px; overflow: hidden; vertical-align: middle; margin-right: 7px; }
        .fill { display: block; height: 100%; border-radius: 4px; } .fill.good { background: var(--good); } .fill.warn { background: var(--warn); } .fill.bad { background: var(--bad); }
        .riskval { font-variant-numeric: tabular-nums; font-weight: 600; }
        tr.detail td { background: color-mix(in srgb, var(--accent) 3%, transparent); padding: 4px 16px 14px 30px; }
        .breakdown { display: flex; flex-direction: column; gap: 6px; padding-top: 8px; }
        .sig code { font-size: 12.5px; color: var(--ink); }
        .comp { display: grid; grid-template-columns: 96px 120px 1fr; gap: 10px; align-items: center; font-size: 12px; }
        .cname { color: var(--muted); } .cbar { height: 6px; background: var(--line); border-radius: 4px; overflow: hidden; }
        .cfill { display: block; height: 100%; background: var(--accent); border-radius: 4px; }
        .path { font-size: 11px; margin-top: 4px; }
        .banner { background: var(--warn-bg); color: var(--warn); border: 1px solid color-mix(in srgb, var(--warn) 35%, transparent); border-radius: 10px; padding: 11px 14px; margin-bottom: 18px; font-size: 13px; }
        .banner strong { color: var(--warn); }
        tr.file-head { cursor: pointer; background: color-mix(in srgb, var(--accent) 7%, var(--panel)); }
        tr.file-head:hover td { background: color-mix(in srgb, var(--accent) 12%, transparent); }
        tr.file-head code { font-weight: 650; font-size: 13px; }
        .fhscore { margin-left: 10px; font-size: 12px; }
        tr.row.nested td.name { padding-left: 30px; }
        .action { display: flex; align-items: center; gap: 8px; margin: 6px 0 2px; flex-wrap: wrap; }
        .cmd { background: var(--chip); border: 1px solid var(--line); border-radius: 6px; padding: 2px 8px; font-size: 12px; }
        .copy { background: var(--accent); color: #fff; border: none; border-radius: 6px; padding: 3px 11px; font-size: 12px; cursor: pointer; font-weight: 600; }
        .copy:hover { filter: brightness(1.08); } .copy:active { transform: translateY(1px); }
        footer { margin-top: 20px; font-size: 12px; }
        """;
}
