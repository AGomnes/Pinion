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

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Reads use the caller's token only, so on a timeout they still drain to EOF once we kill.
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
            TryKill(proc); // user cancelled
            throw;
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return new ProcessResult(proc.ExitCode, stdout.Result, stderr.Result);
    }

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    private static async Task SafeWhenAll(params Task[] tasks)
    {
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { /* drained or faulted — ignore */ }
    }

    private static string AppendNote(Task<string> stderr, string note) =>
        (stderr.IsCompletedSuccessfully ? stderr.Result : "") + "\n" + note;
}
