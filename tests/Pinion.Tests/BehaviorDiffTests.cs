using Pinion.Engine.Reporting;
using Xunit;

namespace Pinion.Tests;

public class BehaviorDiffTests
{
    [Fact]
    public void Identical_text_produces_no_diff()
    {
        Assert.Equal("", BehaviorDiff.Unified("a\nb\nc", "a\nb\nc"));
    }

    [Fact]
    public void Changed_line_shows_minus_was_and_plus_now()
    {
        string diff = BehaviorDiff.Unified("Outcome: 100", "Outcome: 120");
        Assert.Contains("- Outcome: 100", diff);
        Assert.Contains("+ Outcome: 120", diff);
    }

    [Fact]
    public void Unchanged_lines_within_context_are_kept_as_context()
    {
        string locked = "Input: 5\nOutcome: 100\nRegion: NO";
        string current = "Input: 5\nOutcome: 120\nRegion: NO";
        string diff = BehaviorDiff.Unified(locked, current);
        Assert.Contains("  Input: 5", diff);
        Assert.Contains("- Outcome: 100", diff);
        Assert.Contains("+ Outcome: 120", diff);
    }
}

public class BehaviorDiffReportTests
{
    private static BehaviorDiffEntry Same(string n) => new(n, BehaviorChange.Identical, null, n + ".verified.txt", null);
    private static BehaviorDiffEntry Diff(string n) => new(n, BehaviorChange.Changed, "- a\n+ b", n + ".verified.txt", n + ".received.txt");

    [Fact]
    public void Counts_and_preserved_flag_reflect_entries()
    {
        var ok = new BehaviorDiffReport("T.csproj", System.DateTimeOffset.Now, new[] { Same("A"), Same("B") });
        Assert.Equal(2, ok.Identical);
        Assert.Equal(0, ok.Changed);
        Assert.True(ok.BehaviorPreserved);

        var changed = new BehaviorDiffReport("T.csproj", System.DateTimeOffset.Now, new[] { Same("A"), Diff("B") });
        Assert.Equal(1, changed.Changed);
        Assert.False(changed.BehaviorPreserved);
    }

    [Fact]
    public void Build_failure_is_not_preserved()
    {
        var report = new BehaviorDiffReport("T.csproj", System.DateTimeOffset.Now, System.Array.Empty<BehaviorDiffEntry>(),
            BuildFailed: true, BuildErrors: new[] { "error CS0103: undefined" });
        Assert.False(report.BehaviorPreserved);
    }

    [Fact]
    public void Console_render_states_preserved_when_all_identical()
    {
        var report = new BehaviorDiffReport("T.csproj", System.DateTimeOffset.Now, new[] { Same("A") });
        string txt = BehaviorDiffRenderer.Console(report);
        Assert.Contains("Behavior preserved", txt);
    }

    [Fact]
    public void Markdown_render_shows_changed_methods_with_a_diff_block()
    {
        var report = new BehaviorDiffReport("T.csproj", System.DateTimeOffset.Now, new[] { Same("A"), Diff("PriceEngine.ApplyDiscounts") });
        string md = BehaviorDiffRenderer.Markdown(report);
        Assert.Contains("⚠ 1 method(s) changed behavior", md);
        Assert.Contains("### PriceEngine.ApplyDiscounts", md);
        Assert.Contains("```diff", md);
    }

    [Fact]
    public void Html_is_self_contained_and_colors_the_diff()
    {
        var report = new BehaviorDiffReport("T.csproj", System.DateTimeOffset.Now, new[] { Same("A"), Diff("PriceEngine.ApplyDiscounts") });
        string html = BehaviorDiffRenderer.Html(report);

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.DoesNotContain("src=\"http", html);
        Assert.Contains("<details", html);
        Assert.Contains("PriceEngine.ApplyDiscounts", html);
        Assert.Contains("class=\"del\"", html);
        Assert.Contains("class=\"ins\"", html);
    }

    [Fact]
    public void Html_shows_a_green_preserved_banner_when_nothing_changed()
    {
        var report = new BehaviorDiffReport("T.csproj", System.DateTimeOffset.Now, new[] { Same("A"), Same("B") });
        string html = BehaviorDiffRenderer.Html(report);
        Assert.Contains("banner good", html);
        Assert.Contains("Behavior preserved", html);
    }
}
