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

return await PinionCli.BuildRootCommand().Parse(args).InvokeAsync();
