using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pinion.Generate.Licensing;

/// <summary>The claims carried by a license token.</summary>
public sealed record LicenseClaims(
    [property: JsonPropertyName("sub")] string Subject,
    [property: JsonPropertyName("ed")] string Edition,
    [property: JsonPropertyName("exp")] DateTimeOffset Expires,
    [property: JsonPropertyName("iat")] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("mid")] string? Machine = null);

/// <summary>The outcome of verifying a license — never throws, always explains itself.</summary>
public sealed record LicenseStatus(bool Valid, string Reason, LicenseClaims? Claims = null)
{
    public static LicenseStatus Invalid(string reason) => new(false, reason);

    /// <summary>Whole days until the license expires (negative once expired); null when there are no claims.
    /// Subscription licenses are short-lived, so the CLI uses this to nudge a refresh before lock-out.</summary>
    public int? DaysUntilExpiry =>
        Claims is null ? null : (int)Math.Floor((Claims.Expires - DateTimeOffset.UtcNow).TotalDays);
}

/// <summary>Shared token format + crypto for offline (no phone-home) license keys.</summary>
/// <remarks>
/// A key is <c>base64url(payloadJson) "." base64url(signature)</c>. The signature is
/// ECDSA P-256 / SHA-256 over the payload bytes. Verification needs only the public key
/// (embedded in the product); issuance needs the private key (held by the vendor).
/// </remarks>
internal static class LicenseToken
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Encode(LicenseClaims claims, ECDsa signingKey)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(claims, Json);
        byte[] signature = signingKey.SignData(payload, HashAlgorithmName.SHA256);
        return Base64Url(payload) + "." + Base64Url(signature);
    }

    public static LicenseStatus Decode(string token, ECDsa publicKey)
    {
        if (string.IsNullOrWhiteSpace(token))
            return LicenseStatus.Invalid("no license provided");

        string[] parts = token.Trim().Split('.');
        if (parts.Length != 2)
            return LicenseStatus.Invalid("malformed license (expected payload.signature)");

        byte[] payload, signature;
        try
        {
            payload = FromBase64Url(parts[0]);
            signature = FromBase64Url(parts[1]);
        }
        catch
        {
            return LicenseStatus.Invalid("malformed license (bad base64)");
        }

        if (!publicKey.VerifyData(payload, signature, HashAlgorithmName.SHA256))
            return LicenseStatus.Invalid("signature does not match the trusted key (forged or wrong issuer)");

        LicenseClaims? claims;
        try
        {
            claims = JsonSerializer.Deserialize<LicenseClaims>(payload, Json);
        }
        catch
        {
            return LicenseStatus.Invalid("malformed license payload");
        }

        if (claims is null) return LicenseStatus.Invalid("empty license payload");
        if (claims.Expires < DateTimeOffset.UtcNow)
            return new LicenseStatus(false, $"license expired {claims.Expires:yyyy-MM-dd}", claims);

        // Sanity-check the claim window: a non-positive validity span or a future issue date can only be a
        // malformed or forged token. (After the expired check, so a normally-issued, now-expired token
        // still reads "expired" rather than this.)
        if (claims.Expires <= claims.IssuedAt)
            return new LicenseStatus(false, "license validity window is non-positive (exp ≤ iat) — malformed or forged", claims);
        if (claims.IssuedAt > DateTimeOffset.UtcNow.AddDays(1))
            return new LicenseStatus(false, "license issue date is in the future — clock skew or forged", claims);

        // Node-locking: a bound license only verifies on its machine. Unbound (mid == null)
        // licenses work anywhere — that's a deliberate vendor choice (site/floating license).
        if (!string.IsNullOrEmpty(claims.Machine)
            && !string.Equals(claims.Machine, MachineId.Current(), StringComparison.OrdinalIgnoreCase))
        {
            return new LicenseStatus(false, "license is bound to a different machine", claims);
        }

        return new LicenseStatus(true, "ok", claims);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        string b = s.Replace('-', '+').Replace('_', '/');
        b = (b.Length % 4) switch { 2 => b + "==", 3 => b + "=", _ => b };
        return Convert.FromBase64String(b);
    }

    public static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);
}
