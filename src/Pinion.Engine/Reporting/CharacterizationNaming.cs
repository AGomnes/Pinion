using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Pinion.Engine.Reporting;

/// <summary>
/// The single source of truth for a characterization test's class name. The deterministic generator emits
/// it; <c>verify --since</c> recomputes it to map changed source back to the tests that cover it. Keeping
/// the scheme here means the two can't drift (a drift would silently make <c>--since</c> match nothing).
/// </summary>
public static class CharacterizationNaming
{
    /// <summary>e.g. ("InvoiceService.CalculateVat", "Ns.InvoiceService.CalculateVat(decimal,string)")
    /// → "InvoiceService_CalculateVat_1a2b3c_CharacterizationTests". The id hash disambiguates overloads
    /// that share a display name.</summary>
    public static string TestClassName(string displayName, string id) =>
        $"{SafeId(displayName)}_{ShortHash(id)}_CharacterizationTests";

    /// <summary>Identifier-safe form of a token: every non-word character becomes '_'.</summary>
    public static string SafeId(string s) => Regex.Replace(s, @"\W", "_");

    /// <summary>First 3 bytes of SHA-256(<paramref name="s"/>) as lowercase hex — a short, stable suffix.</summary>
    public static string ShortHash(string s)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(h, 0, 3).ToLowerInvariant();
    }
}
