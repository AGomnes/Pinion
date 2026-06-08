using Pinion.Generate.Licensing;
using Pinion.LicenseAdmin;

// Tiny vendor CLI: `keygen` and `issue`. Deliberately dependency-light.
if (args.Length == 0)
{
    Usage();
    return 1;
}

switch (args[0])
{
    case "keygen":
    {
        var (pub, priv) = LicenseAuthority.GenerateKeyPair();
        Console.WriteLine("# Embed this in Pinion.Generate LicenseGate.TrustedPublicKeyB64:");
        Console.WriteLine(pub);
        Console.WriteLine();
        Console.WriteLine("# SECRET signing key — store offline, never ship it:");
        Console.WriteLine(priv);
        return 0;
    }

    case "issue":
    {
        string? signingKey = Environment.GetEnvironmentVariable("PINION_SIGNING_KEY");
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            Console.Error.WriteLine("error: set PINION_SIGNING_KEY to the private signing key first.");
            return 1;
        }

        string? subject = Arg(args, "--subject");
        if (string.IsNullOrWhiteSpace(subject))
        {
            Console.Error.WriteLine("error: --subject is required.");
            return 1;
        }
        string edition = Arg(args, "--edition") ?? "pro";
        int days = int.TryParse(Arg(args, "--days"), out var d) ? d : 365;
        string? machine = Arg(args, "--machine"); // optional: node-lock to this machine id

        try
        {
            Console.WriteLine(LicenseAuthority.Issue(subject!, edition, days, machine, signingKey!));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not issue license: {ex.Message}");
            return 1;
        }
    }

    default:
        Usage();
        return 1;
}

static string? Arg(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static void Usage()
{
    Console.Error.WriteLine("Pinion license admin (vendor-only):");
    Console.Error.WriteLine("  keygen");
    Console.Error.WriteLine("  issue --subject <who> [--edition pro] [--days 365] [--machine <id>]   (needs PINION_SIGNING_KEY)");
}
