using System.Text.RegularExpressions;

namespace Pinion.Generate;

/// <summary>The result of scrubbing: the cleaned text and how many secrets were removed.</summary>
public sealed record ScrubResult(string Text, int Redactions);

/// <summary>
/// Last line of defense before anything leaves the machine: strip obvious secrets
/// (keys, passwords, tokens, connection-string credentials) from outbound text.
/// Conservative and explainable — it redacts values, never whole files, so the model
/// still gets useful structure. Pair with a never-send allowlist at the file level.
/// </summary>
public static class SecretScrubber
{
    private const string Mask = "[REDACTED]";

    private static readonly RegexOptions Opts =
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // 1. Assignments to sensitively-named identifiers: pwd = "...", apiKey: '...', "token": "..."
    private static readonly Regex Assignment = new(
        @"(?<key>(?:password|passwd|pwd|secret|api[_-]?key|access[_-]?key|auth[_-]?token|token|client[_-]?secret|connection[_-]?string|conn[_-]?str|private[_-]?key)\s*[""']?\s*[:=]\s*)(?<q>[""'])(?<val>.*?)(\k<q>)",
        Opts);

    // 2. Connection-string credential fields: Password=...;  Pwd=...  (unquoted values only —
    //    the first char must not be a space/quote, so this never re-hits a redacted assignment).
    private static readonly Regex ConnStringPassword = new(
        @"(?<key>(?:password|pwd)\s*=\s*)(?<val>[^\s;""'][^;""'\r\n]*)",
        Opts);

    // 3. Well-known token shapes, redacted wherever they appear.
    private static readonly Regex[] TokenShapes =
    {
        new(@"sk-ant-[A-Za-z0-9_\-]{20,}", Opts),               // Anthropic
        new(@"sk-[A-Za-z0-9]{20,}", Opts),                       // OpenAI-style
        new(@"gh[pousr]_[A-Za-z0-9]{20,}", Opts),                // GitHub
        new(@"xox[baprs]-[A-Za-z0-9-]{10,}", Opts),              // Slack
        new(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled),         // AWS access key id
        new(@"eyJ[A-Za-z0-9_\-]+\.eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+", RegexOptions.Compiled), // JWT
        new(@"-----BEGIN[^-]+PRIVATE KEY-----[\s\S]*?-----END[^-]+PRIVATE KEY-----", Opts), // PEM
    };

    public static ScrubResult Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return new ScrubResult(text, 0);

        int count = 0;

        text = Assignment.Replace(text, m =>
        {
            if (string.IsNullOrEmpty(m.Groups["val"].Value)) return m.Value; // empty string, nothing to hide
            count++;
            return m.Groups["key"].Value + m.Groups["q"].Value + Mask + m.Groups["q"].Value;
        });

        text = ConnStringPassword.Replace(text, m =>
        {
            count++;
            return m.Groups["key"].Value + Mask;
        });

        foreach (var shape in TokenShapes)
        {
            text = shape.Replace(text, _ => { count++; return Mask; });
        }

        return new ScrubResult(text, count);
    }
}
