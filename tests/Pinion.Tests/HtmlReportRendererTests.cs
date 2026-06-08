using Pinion.Engine.Analysis;
using Pinion.Engine.Model;
using Pinion.Engine.Reporting;
using Xunit;

namespace Pinion.Tests;

public class HtmlReportRendererTests
{
    private static CodeUnit Unit(string name, string file, int cx, bool tested, params string[] tags) =>
        new($"Ns.{name}", name, file, 1, 1 + cx, "sig", Array.Empty<ParamInfo>(), "void",
            cx, cx, Array.Empty<string>(), Array.Empty<string>(), tags, tested, true, Array.Empty<string>());

    private static AnalysisReport SampleReport() => ReportBuilder.Build(
        "C:/proj/Shop",
        new[]
        {
            Unit("InvoiceService.CalculateVat", "InvoiceService.cs", 10, tested: false, "money"),
            Unit("Calculator.Add", "Calculator.cs", 1, tested: true),
        });

    [Fact]
    public void Renders_a_self_contained_html_document()
    {
        string html = HtmlReportRenderer.Render(SampleReport());

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("</html>", html);
        Assert.Contains("__PINION_ROWS__", html);           // data inlined
        Assert.Contains("x-data=\"dashboard()\"", html);    // Alpine wired
        Assert.Contains("InvoiceService.CalculateVat", html);
    }

    [Fact]
    public void Is_fully_offline_no_external_requests()
    {
        string html = HtmlReportRenderer.Render(SampleReport());

        // The whole point: nothing is fetched at view time (offline + no leaking the code's shape).
        Assert.DoesNotContain("src=\"http", html);
        Assert.DoesNotContain("href=\"http", html);
        Assert.DoesNotContain("//cdn", html);
        Assert.DoesNotContain("jsdelivr", html);
        Assert.DoesNotContain("unpkg", html);
        // Alpine is actually inlined (not referenced): its runtime token is present.
        Assert.Contains("x-data", html);
        Assert.True(html.Length > 30_000, "expected Alpine to be inlined, making the file sizeable");
    }

    [Fact]
    public void Mutation_scores_overlay_when_supplied()
    {
        var scores = new Dictionary<string, double> { ["InvoiceService.cs"] = 77.0 };
        string html = HtmlReportRenderer.Render(SampleReport(), scores);

        Assert.Contains("sort('score')", html);   // Score column shown
        Assert.Contains("\"score\":77", html);     // value in the row data
        Assert.Contains("mutation score", html);   // headline card
    }

    [Fact]
    public void Score_column_is_omitted_without_mutation_data()
    {
        string html = HtmlReportRenderer.Render(SampleReport());
        Assert.DoesNotContain("sort('score')", html);
    }

    [Fact]
    public void Overall_mutation_score_is_used_for_the_headline_when_supplied()
    {
        // A per-file average would differ from the true mutant-weighted prove score; the headline
        // must show what was passed (the real overall), not a recomputed average.
        var scores = new Dictionary<string, double> { ["InvoiceService.cs"] = 90, ["Calculator.cs"] = 100 };
        string html = HtmlReportRenderer.Render(SampleReport(), scores, overallMutation: 74);
        Assert.Contains("<div class=\"v\">74%</div><div class=\"l\">mutation score", html);
    }

    [Fact]
    public void Each_untested_method_carries_its_lock_command()
    {
        string html = HtmlReportRenderer.Render(SampleReport());
        Assert.Contains("group by file", html);                                  // grouping control
        Assert.Contains("pinion generate \\u0022C:/proj/Shop\\u0022 --target CalculateVat", html); // copy-able command (JSON-escaped)
    }

    [Fact]
    public void Landmine_banner_appears_only_when_landmines_detected()
    {
        Assert.DoesNotContain("Migration landmines:", HtmlReportRenderer.Render(SampleReport()));

        var mined = new CodeUnit("Ns.Legacy.Page_Load", "Legacy.Page_Load", "Legacy.aspx.cs", 1, 9,
            "sig", Array.Empty<ParamInfo>(), "void", 3, 9, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), false, true, new[] { "WebForms" });
        string html = HtmlReportRenderer.Render(ReportBuilder.Build("C:/proj/Legacy", new[] { mined }));
        Assert.Contains("Migration landmines:", html);
        Assert.Contains("WebForms", html);
    }
}
