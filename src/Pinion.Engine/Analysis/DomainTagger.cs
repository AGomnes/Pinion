using Pinion.Engine.Model;

namespace Pinion.Engine.Analysis;

/// <summary>
/// Assigns domain-sensitivity tags from names alone — the method's own name and type,
/// its parameter/return types, and the names of the things it calls. Pure string
/// heuristics, so the logic is language-neutral and reusable across adapters; the
/// adapter's only job is to extract the strings.
/// </summary>
public static class DomainTagger
{
    private static readonly (string Tag, string[] Keywords)[] Rules =
    {
        (DomainTag.Money, new[]
        {
            "money", "price", "pricing", "cost", "amount", "vat", "tax", "invoice", "discount",
            "currency", "payment", "pay", "charge", "fee", "refund", "balance", "billing", "salary",
            "wage", "ledger", "account", "decimal",
        }),
        (DomainTag.Auth, new[]
        {
            "auth", "login", "logout", "token", "password", "passwd", "credential", "permission",
            "role", "claim", "principal", "identity", "authorize", "authenticate", "secret", "session",
        }),
        (DomainTag.Date, new[]
        {
            "date", "time", "expiry", "expire", "schedule", "deadline", "duration", "timeout",
            "timestamp", "calendar", "datetime",
        }),
        (DomainTag.Io, new[]
        {
            "file", "stream", "http", "url", "uri", "socket", "sql", "query", "database", "connection",
            "request", "response", "download", "upload", "ftp", "smtp", "email", "disk", "path",
        }),
        (DomainTag.DataTransform, new[]
        {
            "parse", "serialize", "deserialize", "convert", "transform", "map", "mapper", "encode",
            "decode", "format", "marshal", "json", "xml", "csv",
        }),
    };

    public static IReadOnlyList<string> Tag(
        string methodName,
        string typeName,
        IEnumerable<string> parameterTypes,
        string returnType,
        IEnumerable<string> referencedNames)
    {
        var signals = new List<string> { methodName, typeName, returnType };
        signals.AddRange(parameterTypes);
        signals.AddRange(referencedNames);

        var tokens = signals
            .Where(s => !string.IsNullOrEmpty(s))
            .SelectMany(Tokenize)
            .ToHashSet(StringComparer.Ordinal);

        var tags = new List<string>();
        foreach (var (tag, keywords) in Rules)
        {
            if (keywords.Any(kw => tokens.Any(t => Matches(t, kw))))
                tags.Add(tag);
        }
        return tags;
    }

    private static bool Matches(string token, string keyword) =>
        token.Equals(keyword, StringComparison.Ordinal)
        || (keyword.Length >= 4 && token.StartsWith(keyword, StringComparison.Ordinal));

    private static IEnumerable<string> Tokenize(string identifier)
    {
        var word = new System.Text.StringBuilder();
        char prev = '\0';
        foreach (char c in identifier)
        {
            bool boundary = !char.IsLetterOrDigit(c)
                || (char.IsUpper(c) && char.IsLower(prev))
                || (char.IsDigit(c) && char.IsLetter(prev));
            if (boundary && word.Length > 0)
            {
                yield return word.ToString().ToLowerInvariant();
                word.Clear();
            }
            if (char.IsLetterOrDigit(c)) word.Append(c);
            prev = c;
        }
        if (word.Length > 0) yield return word.ToString().ToLowerInvariant();
    }
}
