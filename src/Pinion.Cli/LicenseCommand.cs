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
        cmd.Subcommands.Add(Verify());
        cmd.Subcommands.Add(MachineId());
        return cmd;
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
