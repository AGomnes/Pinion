using System.CommandLine;
using Pinion.Generate.Licensing;

namespace Pinion.Cli;

/// <summary>
/// `pinion license` — inspect the current license and report this machine's id. The
/// shipped product can ONLY verify; minting lives in a separate vendor-only tool so no
/// issuance code reaches customer machines. All operations are fully offline.
/// </summary>
internal static class LicenseCommand
{
    public static Command Build()
    {
        var cmd = new Command("license", "Inspect the current license / show this machine's id.");
        cmd.Subcommands.Add(Activate());
        cmd.Subcommands.Add(Verify());
        cmd.Subcommands.Add(MachineId());
        return cmd;
    }

    private static Command Activate()
    {
        var keyArg = new Argument<string>("key") { Description = "Your license key (paste it from your account page or your purchase/renewal email)." };
        var localOption = new Option<bool>("--local")
        {
            Description = "Install to ./pinion.license (this project only) instead of ~/.pinion/license (all projects).",
        };
        var cmd = new Command("activate", "Install a license key on this machine (offline — nothing is sent anywhere).")
        {
            keyArg, localOption,
        };
        cmd.SetAction(parse =>
        {
            string key = parse.GetValue(keyArg)!;
            string path = parse.GetValue(localOption) ? LicenseGate.LocalLicensePath() : LicenseGate.GlobalLicensePath();

            LicenseStatus status;
            try { status = LicenseGate.Install(key, path); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: could not write the license to {path}: {ex.Message}");
                return 1;
            }

            if (!status.Valid)
            {
                Console.Error.WriteLine($"✗ not a valid license: {status.Reason}. Nothing was installed.");
                return 1;
            }

            var c = status.Claims!;
            Console.Out.WriteLine($"✓ license activated for {c.Subject} ({c.Edition}), expires {c.Expires:yyyy-MM-dd} → {path}");
            WarnIfNearExpiry(status);
            return 0;
        });
        return cmd;
    }

    /// <summary>Print a renewal nudge when a valid license is within <see cref="LicenseGate.RenewalWarningDays"/>
    /// of expiry — subscription licenses are short-lived, so this prevents a surprise lock-out mid-work.</summary>
    internal static void WarnIfNearExpiry(LicenseStatus status)
    {
        if (status.Valid && status.DaysUntilExpiry is int d && d <= LicenseGate.RenewalWarningDays)
            Console.Error.WriteLine(
                $"⚠ your license expires in {Math.Max(0, d)} day(s) — install a fresh key with " +
                "`pinion license activate <key>` to avoid interruption.");
    }

    private static Command Verify()
    {
        var licenseOption = new Option<string?>("--license") { Description = "License key (else PINION_LICENSE / pinion.license file is used)." };
        var cmd = new Command("verify", "Check whether a valid license is present.") { licenseOption };
        cmd.SetAction(parse =>
        {
            var (status, source) = LicenseGate.Resolve(parse.GetValue(licenseOption));
            if (status.Valid)
            {
                var c = status.Claims!;
                string bound = string.IsNullOrEmpty(c.Machine) ? "unbound" : $"bound to {c.Machine}";
                Console.Out.WriteLine($"✓ licensed to {c.Subject} ({c.Edition}, {bound}), expires {c.Expires:yyyy-MM-dd} [source: {source}]");
                WarnIfNearExpiry(status);
                return 0;
            }
            Console.Out.WriteLine($"✗ no valid license: {status.Reason} [source: {source}]");
            return 1;
        });
        return cmd;
    }

    private static Command MachineId()
    {
        var cmd = new Command("machine-id", "Print this machine's fingerprint (give it to the vendor for a node-locked license).");
        cmd.SetAction(_ =>
        {
            Console.Out.WriteLine(Pinion.Generate.Licensing.MachineId.Current());
            return 0;
        });
        return cmd;
    }
}
