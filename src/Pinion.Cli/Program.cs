using System.Globalization;
using System.Text;
using Microsoft.Build.Locator;
using Pinion.Cli;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
try { Console.OutputEncoding = Encoding.UTF8; } catch {  }

try
{
    if (!MSBuildLocator.IsRegistered)
        MSBuildLocator.RegisterDefaults();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"warning: MSBuild not available ({ex.Message.Split('\n')[0].Trim()}); analyzing source directly.");
}

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
