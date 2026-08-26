using System.Globalization;
using Pinion.Engine.Analysis;
using Pinion.Engine.Model;

namespace Pinion.Engine.Reporting;

/// <summary>Shared text helpers so the console and Markdown renderers stay consistent.</summary>
internal static class ReportText
{
    internal static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>
    /// A CLR metadata type name rendered for humans: <c>ClientBase`1</c> becomes <c>ClientBase&lt;T&gt;</c>
    /// and nested <c>Outer+Inner</c> becomes <c>Outer.Inner</c>. Metadata arity is an implementation
    /// detail of the reflection format and only distracts a reader scanning a migration report.
    /// </summary>
    internal static string FriendlyTypeName(string metadataName)
    {
        string s = metadataName.Replace('+', '.');
        int tick = s.IndexOf('`');
        if (tick < 0) return s;

        string head = s[..tick];
        string rest = s[(tick + 1)..];
        int digits = 0;
        while (digits < rest.Length && char.IsDigit(rest[digits])) digits++;
        if (digits == 0 || !int.TryParse(rest[..digits], out int arity) || arity < 1) return head;

        var names = arity == 1
            ? new[] { "T" }
            : Enumerable.Range(1, arity).Select(i => "T" + i).ToArray();
        return head + "<" + string.Join(", ", names) + ">" + rest[digits..];
    }

    /// <summary>"1,247" style thousands formatting, locale-independent.</summary>
    internal static string N(int value) => value.ToString("N0", Culture);

    internal static string Percent(double fraction) =>
        (fraction * 100).ToString("0", Culture) + "%";

    /// <summary>
    /// A compact "why it ranked" reason, e.g. "0 tests, complexity 14, money, 14 callers".
    /// Lists the components that actually contributed, highest first, and always
    /// states the test status (the product's central signal).
    /// </summary>
    internal static string Reason(RiskScore score)
    {
        var contributing = score.Components
            .Where(c => c.Contribution > 0)
            .OrderByDescending(c => c.Contribution)
            .Select(c => c.Detail)
            .ToList();

        var untested = score.Components.FirstOrDefault(c => c.Name == "untested");
        if (untested is { Contribution: 0 } && !contributing.Contains(untested.Detail))
            contributing.Add(untested.Detail);

        return contributing.Count == 0 ? "low risk" : string.Join(", ", contributing);
    }

    /// <summary>
    /// Rough effort estimate for the headline. Deliberately coarse and labeled as
    /// an estimate — assume a focused engineer can lock ~30 methods/day with the tool.
    /// </summary>
    internal static string Effort(int methodCount)
    {
        if (methodCount == 0) return "nothing to lock";
        const double perDay = 30.0;
        int days = (int)Math.Ceiling(methodCount / perDay);
        string dayWord = days == 1 ? "day" : "days";
        return $"{N(methodCount)} methods → ~{days} {dayWord}";
    }

    internal static string LandmineSummary(IReadOnlyDictionary<string, int> counts)
    {
        if (counts.Count == 0) return "none detected";
        return string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}"));
    }
}
