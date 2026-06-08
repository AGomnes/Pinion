using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Pinion.Adapters.CSharp;
using Pinion.Engine.Model;
using Pinion.Generate;

namespace Pinion.Adapters.CSharp.Generate;

/// <summary>The C# implementation of the paid <see cref="IGenerationAdapter"/> surface.</summary>
public sealed class CSharpTestGenerator : IGenerationAdapter
{
    private string? _testProjectPath;
    private string _generatedSubdir = "PinionCharacterization";
    private CSharpDeterministicSynthesizer? _synth;

    /// <summary>Hard timeout for each `dotnet test` invocation (kills a hung/infinite-loop run).</summary>
    public int RunTimeoutSeconds { get; set; } = 180;

    private TimeSpan RunTimeout => TimeSpan.FromSeconds(Math.Max(5, RunTimeoutSeconds));

    /// <summary>When true, synthesis loads via MSBuild for full type resolution (needs MSBuild registered by the host).</summary>
    public bool ResolveReferences { get; set; }

    /// <summary>Optional diagnostic sink (verbose).</summary>
    public Action<string>? Log { get; set; }

    private CSharpDeterministicSynthesizer Synth => _synth ??= new CSharpDeterministicSynthesizer(Log, ResolveReferences);

    /// <summary>
    /// Point the generate pipeline at the test project that will host the generated
    /// characterization tests (it must reference the code under test + xunit + VerifyXunit).
    /// </summary>
    public void ConfigureGeneration(string testProjectPath, string? generatedSubdir = null)
    {
        _testProjectPath = Path.GetFullPath(testProjectPath);
        if (!string.IsNullOrWhiteSpace(generatedSubdir)) _generatedSubdir = generatedSubdir!;
    }

    /// <summary>The default, AI-free path: synthesize a test from the semantic model, then compile + run + snapshot.</summary>
    public async Task<GenerationResult> GenerateDeterministicAsync(CodeUnit unit, string sourceRoot, CancellationToken ct)
    {
        string source = Synth.Synthesize(unit, sourceRoot, ct);
        var emitted = await EmitTestAsync(unit, source, ct).ConfigureAwait(false);
        var exec = await RunAndSnapshotAsync(emitted, ct).ConfigureAwait(false);
        return new GenerationResult(unit, exec.Compiled && exec.Passed, Attempts: 1,
            emitted.FilePath, exec.SnapshotPath, exec.Diagnostics);
    }

    /// <summary>Synthesize the deterministic test source without running it (for --dry-run).</summary>
    public string SynthesizeDeterministic(CodeUnit unit, string sourceRoot, CancellationToken ct) =>
        Synth.Synthesize(unit, sourceRoot, ct);

    /// <summary>
    /// Generate deterministic tests for many targets with a single build/run cycle, instead of one
    /// `dotnet test` per method. A target whose generated test doesn't compile is isolated (removed)
    /// and reported, so it never fails the whole batch.
    /// </summary>
    public async Task<IReadOnlyList<GenerationResult>> GenerateDeterministicBatchAsync(
        IReadOnlyList<CodeUnit> units, string sourceRoot, CancellationToken ct)
    {
        string testProject = _testProjectPath
            ?? throw new InvalidOperationException("Call ConfigureGeneration(testProjectPath) before generating tests.");
        string outDir = Path.Combine(Path.GetDirectoryName(testProject)!, _generatedSubdir);
        Directory.CreateDirectory(outDir);
        foreach (var stale in Directory.GetFiles(outDir, "*.received.*")) { try { File.Delete(stale); } catch { } }

        var ordered = units.ToList();
        var results = new Dictionary<CodeUnit, GenerationResult>();
        var active = new List<(CodeUnit Unit, GeneratedTest Test)>();

        // 1. Synthesize + emit every target (no build yet).
        foreach (var unit in ordered)
        {
            try
            {
                string src = Synth.Synthesize(unit, sourceRoot, ct);
                active.Add((unit, await EmitTestAsync(unit, src, ct).ConfigureAwait(false)));
            }
            catch (Exception ex)
            {
                results[unit] = new GenerationResult(unit, false, 1, null, null, new[] { ex.Message });
            }
        }

        var env = new Dictionary<string, string> { ["DiffEngine_Disabled"] = "true" };
        string[] args = { "test", testProject, "--nologo", "--verbosity", "quiet", "--filter", "FullyQualifiedName~Pinion.Generated" };
        var timeout = TimeSpan.FromSeconds(Math.Max(600, RunTimeoutSeconds));

        // 2. One build/run; on a compile failure, drop the offending generated files and retry.
        for (int attempt = 0; active.Count > 0; attempt++)
        {
            var run = await ProcessRunner.RunAsync("dotnet", args, env: env, timeout: timeout, ct: ct).ConfigureAwait(false);

            if (run.TimedOut)
            {
                foreach (var (u, t) in active) results[u] = new GenerationResult(u, false, 1, t.FilePath, null, new[] { $"batch run timed out after {timeout.TotalSeconds:0}s" });
                active.Clear();
                break;
            }
            if (!HasBuildError(run.Combined)) break;

            var brokenFiles = ExtractErrorFiles(run.Combined);
            var toRemove = active.Where(a => brokenFiles.Contains(Path.GetFullPath(a.Test.FilePath))).ToList();

            if (toRemove.Count == 0 || attempt >= 3)
            {
                var diag = ExtractCompilerErrors(run.Combined);
                foreach (var (u, t) in active) results[u] = new GenerationResult(u, false, 1, t.FilePath, null, diag);
                active.Clear();
                break;
            }

            foreach (var item in toRemove)
            {
                results[item.Unit] = new GenerationResult(item.Unit, false, 1, item.Test.FilePath, null, ErrorsForFile(run.Combined, item.Test.FilePath));
                try { File.Delete(item.Test.FilePath); } catch { }
                active.Remove(item);
            }
        }

        if (active.Count == 0) return InOrder(ordered, results);

        // 3. Promote every surviving target's Verify *.received → scrubbed *.verified.
        foreach (var (_, t) in active)
        {
            foreach (var rec in Directory.GetFiles(outDir, t.TestClassName + ".*.received.*"))
            {
                string verified = rec.Replace(".received.", ".verified.");
                File.WriteAllText(verified, SecretScrubber.Scrub(File.ReadAllText(rec)).Text);
                File.Delete(rec);
            }
        }

        // 4. One confirming run.
        var confirm = await ProcessRunner.RunAsync("dotnet", args, env: env, timeout: timeout, ct: ct).ConfigureAwait(false);
        bool buildOk = !confirm.TimedOut && !HasBuildError(confirm.Combined);

        foreach (var (u, t) in active)
        {
            string? snapshot = Directory.GetFiles(outDir, t.TestClassName + ".*.verified.*").FirstOrDefault();
            bool ok = snapshot is not null && buildOk;
            results[u] = new GenerationResult(u, ok, 1, t.FilePath, snapshot,
                ok ? Array.Empty<string>() : new[] { confirm.TimedOut ? "confirm run timed out" : "snapshot not captured" });
        }

        return InOrder(ordered, results);
    }

    private static IReadOnlyList<GenerationResult> InOrder(List<CodeUnit> ordered, Dictionary<CodeUnit, GenerationResult> map) =>
        ordered.Select(u => map.TryGetValue(u, out var r) ? r : new GenerationResult(u, false, 0, null, null, new[] { "not processed" })).ToList();

    private static HashSet<string> ExtractErrorFiles(string output)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @"^(?<f>.+?\.cs)\(\d+,\d+\):\s*error CS");
            if (!m.Success) continue;
            try { set.Add(Path.GetFullPath(m.Groups["f"].Value.Trim())); } catch { }
        }
        return set;
    }

    private static IReadOnlyList<string> ErrorsForFile(string output, string file)
    {
        string name = Path.GetFileName(file);
        var errs = output.Split('\n').Select(l => l.Trim())
            .Where(l => l.Contains("error CS", StringComparison.Ordinal) && l.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Distinct().Take(10).ToList();
        return errs.Count > 0 ? errs : new[] { "did not compile" };
    }

    public Task<GenerationContext> ExtractContextAsync(CodeUnit unit, CancellationToken ct)
    {
        string ns = "", typeName = "", source = unit.Signature;

        if (File.Exists(unit.FilePath))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(unit.FilePath), cancellationToken: ct);
            var root = tree.GetRoot(ct);

            TypeDeclarationSyntax? best = null;
            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var span = type.GetLocation().GetLineSpan();
                int start = span.StartLinePosition.Line + 1;
                int end = span.EndLinePosition.Line + 1;
                if (unit.StartLine >= start && unit.EndLine <= end &&
                    (best is null || type.Span.Length < best.Span.Length))
                {
                    best = type; // innermost type that contains the method
                }
            }

            if (best is not null)
            {
                typeName = best.Identifier.Text;
                source = best.ToString();
                ns = best.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "";
            }
        }

        if (string.IsNullOrEmpty(typeName))
            typeName = unit.DisplayName.Contains('.') ? unit.DisplayName.Split('.')[0] : "Target";

        return Task.FromResult(new GenerationContext(unit, ns, typeName, source, unit.CalleeIds));
    }

    public Task<GeneratedTest> EmitTestAsync(CodeUnit unit, string generatedBody, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_testProjectPath))
            throw new InvalidOperationException("Call ConfigureGeneration(testProjectPath) before generating tests.");

        string testDir = Path.GetDirectoryName(_testProjectPath)!;
        string outDir = Path.Combine(testDir, _generatedSubdir);
        Directory.CreateDirectory(outDir);

        string className = ClassNameFrom(generatedBody)
            ?? Regex.Replace(unit.DisplayName, @"\W", "_") + "_CharacterizationTests";

        // Harden the filename: identifier chars only, and never let it escape outDir.
        className = Regex.Replace(Path.GetFileNameWithoutExtension(className), @"[^A-Za-z0-9_]", "_");
        string filePath = Path.GetFullPath(Path.Combine(outDir, className + ".cs"));
        if (Path.GetDirectoryName(filePath) != Path.GetFullPath(outDir))
            throw new InvalidOperationException($"Refusing to write generated test outside {outDir}.");

        File.WriteAllText(filePath, generatedBody);

        return Task.FromResult(new GeneratedTest(unit, className, generatedBody, filePath));
    }

    public async Task<ExecutionResult> RunAndSnapshotAsync(GeneratedTest test, CancellationToken ct)
    {
        string testProject = _testProjectPath
            ?? throw new InvalidOperationException("Call ConfigureGeneration(testProjectPath) before generating tests.");
        string outDir = Path.GetDirectoryName(test.FilePath)!;

        // Headless: never let Verify pop a diff tool.
        var env = new Dictionary<string, string> { ["DiffEngine_Disabled"] = "true" };
        string[] args =
        {
            "test", testProject, "--nologo", "--verbosity", "quiet",
            "--filter", $"FullyQualifiedName~{test.TestClassName}",
        };

        var first = await ProcessRunner.RunAsync("dotnet", args, env: env, timeout: RunTimeout, ct: ct).ConfigureAwait(false);
        if (first.TimedOut)
            return new ExecutionResult(false, false, new[] { $"test run timed out after {RunTimeoutSeconds}s (possible infinite loop or blocking call in the target)" }, null);
        if (HasBuildError(first.Combined))
            return new ExecutionResult(Compiled: false, Passed: false, ExtractCompilerErrors(first.Combined), null);

        // Verify's first run with no approved snapshot fails and writes *.received.*.
        var received = Directory.GetFiles(outDir, test.TestClassName + ".*.received.*");
        if (received.Length > 0)
        {
            string? snapshot = null;
            foreach (var rec in received)
            {
                string verified = rec.Replace(".received.", ".verified.");
                // Scrub the golden master — captured return values can contain real secrets/PII,
                // and this file gets committed to the repo.
                File.WriteAllText(verified, SecretScrubber.Scrub(File.ReadAllText(rec)).Text);
                File.Delete(rec);
                snapshot ??= verified;
            }

            // Re-run to confirm the captured snapshot now passes.
            var second = await ProcessRunner.RunAsync("dotnet", args, env: env, timeout: RunTimeout, ct: ct).ConfigureAwait(false);
            if (second.TimedOut)
                return new ExecutionResult(false, false, new[] { $"test run timed out after {RunTimeoutSeconds}s" }, snapshot);
            if (HasBuildError(second.Combined))
                return new ExecutionResult(false, false, ExtractCompilerErrors(second.Combined), null);

            bool passed = second.ExitCode == 0;
            return new ExecutionResult(true, passed, passed ? Array.Empty<string>() : Tail(second.Combined), snapshot);
        }

        if (first.ExitCode == 0)
        {
            string? snapshot = Directory.GetFiles(outDir, test.TestClassName + ".*.verified.*").FirstOrDefault();
            return new ExecutionResult(true, true, Array.Empty<string>(), snapshot);
        }

        // Compiled, ran, but failed for a non-snapshot reason.
        return new ExecutionResult(true, false, Tail(first.Combined), null);
    }

    private static string? ClassNameFrom(string source)
    {
        var m = Regex.Match(source, @"\bclass\s+(?<name>[A-Za-z_]\w*)");
        return m.Success ? m.Groups["name"].Value : null;
    }

    private static bool HasBuildError(string output) =>
        output.Contains("error CS", StringComparison.Ordinal) || output.Contains("Build FAILED", StringComparison.Ordinal);

    private static IReadOnlyList<string> ExtractCompilerErrors(string output)
    {
        var errors = output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains("error CS", StringComparison.Ordinal))
            .Distinct()
            .Take(25)
            .ToList();
        return errors.Count > 0 ? errors : Tail(output);
    }

    private static IReadOnlyList<string> Tail(string output, int lines = 30) =>
        output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(lines).ToList();
}
