using System.Security.Cryptography;

namespace Pinion.Generate.Licensing;

/// <summary>
/// Verifies a Pinion paid-tier license entirely offline against an embedded public key.
/// No network call — verification on a fully air-gapped machine works, which is the whole
/// point: the license check must not contradict the product's "code stays local" promise.
/// </summary>
public static class LicenseGate
{
    /// <summary>
    /// The trusted issuer public key (ECDSA P-256, SubjectPublicKeyInfo, base64). Only the
    /// matching private key (held by the vendor) can mint licenses this build will accept.
    /// </summary>
    public const string TrustedPublicKeyB64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE6zb/pD0QdKJNaJjR/llOsHr5PMyFrw8PXvtkFbd33/DOsessrdDLcguLH372UzQ+m9TUuHv+pVo/AZZl4Bo9Zg==";

    /// <summary>Verify a token against the embedded trusted key.</summary>
    public static LicenseStatus Verify(string? token) => VerifyWith(token, TrustedPublicKeyB64);

    internal static LicenseStatus VerifyWith(string? token, string publicKeyB64)
    {
        if (string.IsNullOrWhiteSpace(token)) return LicenseStatus.Invalid("no license provided");

        ECDsa key;
        try
        {
            key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyB64), out _);
        }
        catch
        {
            return LicenseStatus.Invalid("this build has no valid trusted license key configured");
        }

        using (key)
            return LicenseToken.Decode(token!, key);
    }

    /// <summary>
    /// Find a license key from (in order): the explicit value, the <c>PINION_LICENSE</c> env
    /// var, <c>./pinion.license</c>, or <c>~/.pinion/license</c>.
    /// </summary>
    public static (string? Key, string Source) ResolveKey(string? explicitKey)
    {
        if (!string.IsNullOrWhiteSpace(explicitKey)) return (explicitKey!.Trim(), "--license");

        string? env = Environment.GetEnvironmentVariable("PINION_LICENSE");
        if (!string.IsNullOrWhiteSpace(env)) return (env.Trim(), "PINION_LICENSE");

        foreach (var (path, label) in CandidateFiles())
        {
            if (File.Exists(path))
            {
                try { return (File.ReadAllText(path).Trim(), label); }
                catch { /* unreadable — keep looking */ }
            }
        }

        return (null, "none");
    }

    public static (LicenseStatus Status, string Source) Resolve(string? explicitKey)
    {
        var (key, source) = ResolveKey(explicitKey);
        return (Verify(key), source);
    }

    /// <summary>Subscription licenses are short-lived; warn this many days before expiry so the user can
    /// refresh/renew before the paid tier locks.</summary>
    public const int RenewalWarningDays = 7;

    /// <summary>The user-level license location (<c>~/.pinion/license</c>) — applies to every project.</summary>
    public static string GlobalLicensePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pinion", "license");

    /// <summary>The project-level license location (<c>./pinion.license</c>).</summary>
    public static string LocalLicensePath() =>
        Path.Combine(Directory.GetCurrentDirectory(), "pinion.license");

    /// <summary>
    /// Validate <paramref name="key"/> and, if valid, install it to <paramref name="path"/> (creating the
    /// directory). An invalid or expired key is NEVER written — so a customer can't accidentally save a
    /// bad key and then wonder why nothing works. Returns the validation outcome either way.
    /// </summary>
    public static LicenseStatus Install(string? key, string path) => Install(key, path, TrustedPublicKeyB64);

    internal static LicenseStatus Install(string? key, string path, string publicKeyB64)
    {
        var status = VerifyWith(key, publicKeyB64);
        if (!status.Valid) return status;
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, key!.Trim());
        return status;
    }

    private static IEnumerable<(string Path, string Label)> CandidateFiles()
    {
        yield return (LocalLicensePath(), "./pinion.license");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            yield return (GlobalLicensePath(), "~/.pinion/license");
    }
}
