using System.Diagnostics;

namespace Pinion.Adapters.CSharp;

internal readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut = false)
{
    public string Combined => StdOut + (string.IsNullOrEmpty(StdErr) ? "" : "\n" + StdErr);
}

/// <summary>Runs an external process, draining both pipes so it can't deadlock, with an optional
/// hard timeout that kills the whole process tree (so a hung build / infinite-loop target can't
/// wedge Pinion forever).</summary>
internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        ScrubSecretsFrom(psi.Environment);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        Task<string> stdout = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = proc.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = timeout is { } t ? new CancellationTokenSource(t) : null;
        using var linked = timeoutCts is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await proc.WaitForExitAsync(linked?.Token ?? ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
        {
            TryKill(proc);
            await SafeWhenAll(stdout, stderr).ConfigureAwait(false);
            string note = $"[pinion] process timed out after {timeout!.Value.TotalSeconds:0}s and was terminated.";
            return new ProcessResult(124, stdout.Result, AppendNote(stderr, note), TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw;
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return new ProcessResult(proc.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>
    /// Secrets that may sit in Pinion's own environment but that a spawned child must never inherit.
    /// Deliberately limited to LLM/provider API keys + the license-signing key — NOT cloud/feed
    /// credentials (AWS/GitHub/NuGet), which a legitimate `dotnet restore`/test can need, so stripping
    /// those could break the run. The dominant, always-safe entry is ANTHROPIC_API_KEY.
    /// </summary>
    internal static readonly string[] SecretEnvVars =
    {
        "ANTHROPIC_API_KEY",
        "OPENAI_API_KEY", "AZURE_OPENAI_API_KEY", "GOOGLE_API_KEY", "GEMINI_API_KEY", "MISTRAL_API_KEY",
        "PINION_SIGNING_KEY",
    };

    /// <summary>Remove every <see cref="SecretEnvVars"/> entry from a child process's environment.</summary>
    internal static void ScrubSecretsFrom(IDictionary<string, string?> environment)
    {
        foreach (var name in SecretEnvVars) environment.Remove(name);
    }

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch {  }
    }

    private static async Task SafeWhenAll(params Task[] tasks)
    {
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch {  }
    }

    private static string AppendNote(Task<string> stderr, string note) =>
        (stderr.IsCompletedSuccessfully ? stderr.Result : "") + "\n" + note;
}
