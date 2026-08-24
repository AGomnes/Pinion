using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Pinion.Generate.Licensing;

/// <summary>
/// A best-effort, stable, machine-local fingerprint for node-locking a license. Computed
/// entirely offline from local OS/hardware facts — no network call, nothing leaves the
/// machine. The customer reads it (<c>pinion license machine-id</c>) and hands it to the
/// vendor out-of-band; the vendor binds the issued license to it.
/// </summary>
public static class MachineId
{
    /// <summary>A short, stable hex fingerprint of this machine.</summary>
    public static string Current()
    {
        string raw = string.Join("|",
            Environment.MachineName,
            RuntimeInformation.OSArchitecture.ToString(),
            PrimaryMac() ?? "no-mac");

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>Lowest stable physical (6-byte) MAC of an up, non-loopback interface, if any.</summary>
    private static string? PrimaryMac()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetPhysicalAddress().GetAddressBytes())
                .Where(b => b.Length == 6 && b.Any(x => x != 0))
                .Select(Convert.ToHexString)
                .OrderBy(s => s, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
