using System.Globalization;
using System.Text;
using Microsoft.Build.Locator;
using Pinion.Cli;

// Stable, locale-independent output: invariant number formatting everywhere and
// UTF-8 so box-drawing/arrows in the report render correctly.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected stream — ignore */ }

// MSBuild must be located before any Roslyn MSBuild type loads. Do it first, once,
// before touching the adapter (which lives in another assembly so nothing MSBuild
// resolves until we call into it). This can fail when the target repo's global.json
// pins an SDK that isn't installed — don't crash; the adapter falls back to a direct
// source scan when MSBuild is unavailable.
try
{
    if (!MSBuildLocator.IsRegistered)
        MSBuildLocator.RegisterDefaults();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"warning: MSBuild not available ({ex.Message.Split('\n')[0].Trim()}); analyzing source directly.");
}

// Top-level safety net: a throw that escapes a command handler (binding, MSBuild edge, I/O) must not
// surface as a raw stack trace with an undefined exit code — `verify` uses the exit code as a CI gate,
// so an unexpected crash must be a clean, distinct non-zero (1), never confused with "behavior changed".
try
{
    return await PinionCli.BuildRootCommand().Parse(args).InvokeAsync();
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
