using System.Diagnostics;
using System.IO;

namespace Pinion.Cli;

/// <summary>Shared `--open` handling: open the written <c>--out</c> file, or a temp file when none was
/// given. Static artifact in the browser — no server, nothing listening.</summary>
internal static class ReportOutput
{
    public static async Task OpenAsync(string rendered, OutputFormat format, FileInfo? outFile, string stem, CancellationToken ct)
    {
        string file;
        if (outFile is not null)
        {
            file = outFile.FullName;
        }
        else
        {
            string ext = format switch
            {
                OutputFormat.Html => ".html",
                OutputFormat.Markdown => ".md",
                OutputFormat.Json => ".json",
                _ => ".txt",
            };
            file = Path.Combine(Path.GetTempPath(), $"{stem}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
            await File.WriteAllTextAsync(file, rendered, ct);
        }

        if (BrowserLauncher.TryOpen(file, out var err)) Console.Error.WriteLine($"Opened {file}");
        else Console.Error.WriteLine($"warning: could not open {file} ({err}).");
    }
}

/// <summary>
/// Opens a generated report file in the default browser — the posture-preserving convenience for the
/// `--open` flag. Pinion never binds a port or runs a server (that would be an attack surface a
/// security review flags); it just hands a static file to the OS's default handler. Best-effort: a
/// failure to launch (headless box, CI, no association) is reported, never fatal.
/// </summary>
internal static class BrowserLauncher
{
    public static bool TryOpen(string path, out string? error)
    {
        error = null;
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", path);
            else
                Process.Start("xdg-open", path);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
