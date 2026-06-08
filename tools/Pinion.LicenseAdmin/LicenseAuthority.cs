using System.Security.Cryptography;
using Pinion.Generate.Licensing;

namespace Pinion.LicenseAdmin;

/// <summary>
/// Vendor-side license minting. Lives only in this admin tool — never in the shipped
/// product — so customer binaries contain no issuance code. Minting still fundamentally
/// requires the private signing key, which is the real control.
/// </summary>
public static class LicenseAuthority
{
    /// <summary>Generate a fresh issuer keypair. Embed the public key in the product; keep the private key secret.</summary>
    public static (string PublicKeyB64, string PrivateKeyB64) GenerateKeyPair()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (
            Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(ec.ExportPkcs8PrivateKey()));
    }

    /// <summary>Mint a signed license key for the given claims using the PKCS#8 private key (base64).</summary>
    public static string Issue(LicenseClaims claims, string privateKeyPkcs8B64)
    {
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8B64), out _);
        return LicenseToken.Encode(claims, ec);
    }

    /// <summary>Convenience: mint for a subject/edition, optionally node-locked to a machine id.</summary>
    public static string Issue(string subject, string edition, int days, string? machine, string privateKeyPkcs8B64)
    {
        var now = DateTimeOffset.UtcNow;
        var claims = new LicenseClaims(subject, edition, now.AddDays(days), now, machine);
        return Issue(claims, privateKeyPkcs8B64);
    }
}
