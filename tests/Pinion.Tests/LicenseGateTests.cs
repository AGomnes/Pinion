using Pinion.Generate.Licensing;
using Pinion.LicenseAdmin;
using Xunit;

namespace Pinion.Tests;

public class LicenseGateTests
{
    // A throwaway issuer keypair, generated per test run — never the production key.
    private static readonly (string PublicKeyB64, string PrivateKeyB64) Issuer = LicenseAuthority.GenerateKeyPair();

    [Fact]
    public void Valid_unbound_license_verifies_against_the_matching_public_key()
    {
        string token = LicenseAuthority.Issue("Acme Corp", "pro", 365, machine: null, Issuer.PrivateKeyB64);

        var status = LicenseGate.VerifyWith(token, Issuer.PublicKeyB64);

        Assert.True(status.Valid, status.Reason);
        Assert.Equal("Acme Corp", status.Claims!.Subject);
        Assert.Null(status.Claims.Machine);
    }

    [Fact]
    public void License_bound_to_this_machine_verifies_here()
    {
        string token = LicenseAuthority.Issue("Acme Corp", "pro", 365, MachineId.Current(), Issuer.PrivateKeyB64);

        var status = LicenseGate.VerifyWith(token, Issuer.PublicKeyB64);

        Assert.True(status.Valid, status.Reason);
    }

    [Fact]
    public void License_bound_to_a_different_machine_is_rejected()
    {
        // A correctly-signed license for someone else's machine must not work here — this
        // is what stops a customer sharing their token with everyone.
        string token = LicenseAuthority.Issue("Acme Corp", "pro", 365, machine: "deadbeefdeadbeef", Issuer.PrivateKeyB64);

        var status = LicenseGate.VerifyWith(token, Issuer.PublicKeyB64);

        Assert.False(status.Valid);
        Assert.Contains("different machine", status.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expired_license_is_rejected()
    {
        string token = LicenseAuthority.Issue("Acme Corp", "pro", -1, machine: null, Issuer.PrivateKeyB64);

        var status = LicenseGate.VerifyWith(token, Issuer.PublicKeyB64);

        Assert.False(status.Valid);
        Assert.Contains("expired", status.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void License_signed_by_a_different_key_is_rejected()
    {
        var attacker = LicenseAuthority.GenerateKeyPair();
        string forged = LicenseAuthority.Issue("Acme Corp", "pro", 365, machine: null, attacker.PrivateKeyB64);

        var status = LicenseGate.VerifyWith(forged, Issuer.PublicKeyB64);

        Assert.False(status.Valid);
        Assert.Contains("signature", status.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        string token = LicenseAuthority.Issue("Acme Corp", "pro", 365, machine: null, Issuer.PrivateKeyB64);
        char[] chars = token.ToCharArray();
        chars[0] = chars[0] == 'A' ? 'B' : 'A';
        var status = LicenseGate.VerifyWith(new string(chars), Issuer.PublicKeyB64);

        Assert.False(status.Valid);
    }

    [Fact]
    public void Missing_or_malformed_license_reports_clearly_without_throwing()
    {
        Assert.False(LicenseGate.VerifyWith(null, Issuer.PublicKeyB64).Valid);
        Assert.False(LicenseGate.VerifyWith("", Issuer.PublicKeyB64).Valid);
        Assert.False(LicenseGate.VerifyWith("not-a-token", Issuer.PublicKeyB64).Valid);
    }

    [Fact]
    public void Machine_id_is_stable_across_calls()
    {
        Assert.Equal(MachineId.Current(), MachineId.Current());
        Assert.False(string.IsNullOrWhiteSpace(MachineId.Current()));
    }

    [Fact]
    public void Rotation_accepts_a_token_signed_by_any_trusted_key()
    {
        // Key rotation: while both keys are trusted, a license signed by EITHER must verify — so issuing
        // can move to a new key without invalidating licenses already minted under the old one.
        var keyA = LicenseAuthority.GenerateKeyPair();
        var keyB = LicenseAuthority.GenerateKeyPair();
        string tokenB = LicenseAuthority.Issue("Acme Corp", "pro", 365, machine: null, keyB.PrivateKeyB64);

        Assert.True(LicenseGate.VerifyAgainst(tokenB, new[] { keyA.PublicKeyB64, keyB.PublicKeyB64 }).Valid);
        Assert.False(LicenseGate.VerifyAgainst(tokenB, new[] { keyA.PublicKeyB64 }).Valid); // key B dropped → rejected
    }

    [Fact]
    public void Rotation_reports_expiry_not_signature_when_a_key_matches()
    {
        // The failure message should reflect that the token WAS ours (matched a key) but expired — not a
        // misleading "signature mismatch" from the other trusted key.
        var keyA = LicenseAuthority.GenerateKeyPair();
        var keyB = LicenseAuthority.GenerateKeyPair();
        string expiredB = LicenseAuthority.Issue("Acme Corp", "pro", -1, machine: null, keyB.PrivateKeyB64);

        var status = LicenseGate.VerifyAgainst(expiredB, new[] { keyA.PublicKeyB64, keyB.PublicKeyB64 });

        Assert.False(status.Valid);
        Assert.Contains("expired", status.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WebCrypto_minted_token_verifies_in_dotnet()
    {
        // Cross-runtime interop guard — the load-bearing assumption of the billing backend.
        // This token was minted by Web Crypto (Node/Deno crypto.subtle, ECDSA P-256, raw r‖s), EXACTLY as
        // the Supabase mint function (AlexNettside/supabase/functions/_shared/license.ts) produces. If it
        // verifies here, Deno-minted subscription licenses are accepted by the .NET product offline.
        // (exp is ~100 years out, so it never expires; the matching public key is inline.)
        const string webCryptoToken =
            "eyJzdWIiOiJJbnRlcm9wIFRlc3QiLCJlZCI6InBybyIsImV4cCI6IjIxMjYtMDUtMTdUMTE6MDY6MDMuMzE2WiIsImlhdCI6IjIwMjYtMDYtMTBUMTE6MDY6MDMuMzE2WiIsIm1pZCI6bnVsbH0" +
            ".Yca6sS0Cch7Q_Lj8PiEvz1kERUBDjuescit8LTRfkEf2SP9iyE8ulbdKM9hJRbesojpw55Cran7CzNnWMErDRA";
        const string publicKey =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEvrhzw54B1FOtQjkmDH2X9MpUmML5z+aHBSy8rMUwFNzH4NTtu7R5FSFqKHHISnsa5ysSDTEm6kWq3RZYvb7Vow==";

        var status = LicenseGate.VerifyWith(webCryptoToken, publicKey);

        Assert.True(status.Valid, status.Reason);
        Assert.Equal("Interop Test", status.Claims!.Subject);
        Assert.Equal("pro", status.Claims.Edition);
    }

    [Fact]
    public void DaysUntilExpiry_reflects_the_claim_window()
    {
        string token = LicenseAuthority.Issue("Acme Corp", "pro", 10, machine: null, Issuer.PrivateKeyB64);
        var status = LicenseGate.VerifyWith(token, Issuer.PublicKeyB64);

        Assert.True(status.Valid);
        Assert.InRange(status.DaysUntilExpiry!.Value, 9, 10); // ~10 days, allowing for sub-day rounding
    }

    [Fact]
    public void Install_writes_a_valid_key_and_refuses_an_invalid_one()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pinion-activate-" + System.Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "license");
        try
        {
            string good = LicenseAuthority.Issue("Acme Corp", "pro", 30, machine: null, Issuer.PrivateKeyB64);

            // Valid key → installed.
            var ok = LicenseGate.Install(good, path, Issuer.PublicKeyB64);
            Assert.True(ok.Valid, ok.Reason);
            Assert.True(File.Exists(path));
            Assert.Equal(good, File.ReadAllText(path).Trim());

            // Invalid key → never written (here, to a fresh path so we can assert nothing lands).
            string path2 = Path.Combine(dir, "license2");
            var bad = LicenseGate.Install("not-a-license", path2, Issuer.PublicKeyB64);
            Assert.False(bad.Valid);
            Assert.False(File.Exists(path2));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }
}
