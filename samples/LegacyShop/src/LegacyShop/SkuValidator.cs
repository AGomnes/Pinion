namespace LegacyShop;

/// <summary>
/// Regex-gated validation — the shape the constant-miner can't satisfy. The accept branch (and all of its
/// mutants: the substrings, the separator, the parsed fields) is reached ONLY by a string that MATCHES the
/// pattern. The old generator's "Ab1…" witness never matches, so it hits only the reject path; the regex
/// solver synthesizes a match (e.g. "AAA-5555") plus a verified non-match.
/// </summary>
public sealed class SkuValidator
{
    public string ClassifySku(string sku)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(sku, @"^[A-Z]{3}-\d{4}$"))
            return "INVALID";
        return sku.Substring(0, 3) + " / " + sku.Substring(4);
    }
}
