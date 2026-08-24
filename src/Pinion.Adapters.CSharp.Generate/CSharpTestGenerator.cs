using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Pinion.Adapters.CSharp;
using Pinion.Engine.Model;
using Pinion.Generate;

namespace Pinion.Adapters.CSharp.Generate;

/// <summary>The C# implementation of the <see cref="IGenerationAdapter"/> surface.</summary>
public sealed class CSharpTestGenerator : IGenerationAdapter, IDisposable
{
    private string? _testProjectPath;
    private string _generatedSubdir = "PinionCharacterization";
    private CSharpDeterministicSynthesizer? _synth;

    /// <summary>Dispose the synthesizer (and its MSBuild workspaces).</summary>
    public void Dispose() => _synth?.Dispose();

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
        string source = await Synth.SynthesizeAsync(unit, sourceRoot, ct).ConfigureAwait(false);
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
        EnsureGeneratedSetup(outDir);
        foreach (var stale in Directory.GetFiles(outDir, "*.received.*")) { try { File.Delete(stale); } catch { } }

        var ordered = units.ToList();
        var results = new Dictionary<CodeUnit, GenerationResult>();
        var active = new List<(CodeUnit Unit, GeneratedTest Test)>();

        foreach (var unit in ordered)
        {
            try
            {
                string src = await Synth.SynthesizeAsync(unit, sourceRoot, ct).ConfigureAwait(false);
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

        foreach (var (_, t) in active)
        {
            foreach (var rec in Directory.GetFiles(outDir, t.TestClassName + ".*.received.*"))
            {
                string verified = rec.Replace(".received.", ".verified.");
                File.WriteAllText(verified, SecretScrubber.Scrub(File.ReadAllText(rec)).Text);
                File.Delete(rec);
            }
        }

        var confirm = await ProcessRunner.RunAsync("dotnet", args, env: env, timeout: timeout, ct: ct).ConfigureAwait(false);
        bool buildOk = !confirm.TimedOut && !HasBuildError(confirm.Combined);

        foreach (var (u, t) in active)
        {
            var verified = Directory.GetFiles(outDir, t.TestClassName + ".*.verified.*");
            string? snapshot = verified.FirstOrDefault();

            if (!buildOk)
            {
                results[u] = new GenerationResult(u, false, 1, t.FilePath, snapshot,
                    new[] { confirm.TimedOut ? "confirm run timed out" : "build failed on the confirm run" });
                continue;
            }

            var received = Directory.GetFiles(outDir, t.TestClassName + ".*.received.*");
            if (received.Length > 0)
            {
                foreach (var f in verified.Concat(received)) { try { File.Delete(f); } catch { } }
                try { File.Delete(t.FilePath); } catch { }
                results[u] = new GenerationResult(u, false, 1, null, null, new[] { FlakyDiagnosis.Explain(u) });
                continue;
            }

            results[u] = new GenerationResult(u, snapshot is not null, 1, t.FilePath, snapshot,
                snapshot is not null ? Array.Empty<string>() : new[] { "snapshot not captured" });
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
                    best = type;
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
        EnsureGeneratedSetup(outDir);

        string className = ClassNameFrom(generatedBody)
            ?? Regex.Replace(unit.DisplayName, @"\W", "_") + "_CharacterizationTests";

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

        var received = Directory.GetFiles(outDir, test.TestClassName + ".*.received.*");
        if (received.Length > 0)
        {
            string? snapshot = null;
            foreach (var rec in received)
            {
                string verified = rec.Replace(".received.", ".verified.");
                File.WriteAllText(verified, SecretScrubber.Scrub(File.ReadAllText(rec)).Text);
                File.Delete(rec);
                snapshot ??= verified;
            }

            var second = await ProcessRunner.RunAsync("dotnet", args, env: env, timeout: RunTimeout, ct: ct).ConfigureAwait(false);
            if (second.TimedOut)
                return new ExecutionResult(false, false, new[] { $"test run timed out after {RunTimeoutSeconds}s" }, snapshot);
            if (HasBuildError(second.Combined))
                return new ExecutionResult(false, false, ExtractCompilerErrors(second.Combined), null);

            if (second.ExitCode == 0)
                return new ExecutionResult(true, true, Array.Empty<string>(), snapshot);

            var reappeared = Directory.GetFiles(outDir, test.TestClassName + ".*.received.*");
            if (reappeared.Length > 0)
            {
                foreach (var f in Directory.GetFiles(outDir, test.TestClassName + ".*.verified.*").Concat(reappeared))
                    { try { File.Delete(f); } catch { } }
                try { File.Delete(test.FilePath); } catch { }
                return new ExecutionResult(true, false, new[] { FlakyDiagnosis.Explain(test.Unit) }, null);
            }

            return new ExecutionResult(true, false, Tail(second.Combined), snapshot);
        }

        if (first.ExitCode == 0)
        {
            string? snapshot = Directory.GetFiles(outDir, test.TestClassName + ".*.verified.*").FirstOrDefault();
            return new ExecutionResult(true, true, Array.Empty<string>(), snapshot);
        }

        return new ExecutionResult(true, false, Tail(first.Combined), null);
    }

    private static string? ClassNameFrom(string source)
    {
        var m = Regex.Match(source, @"\bclass\s+(?<name>[A-Za-z_]\w*)");
        return m.Success ? m.Groups["name"].Value : null;
    }

    /// <summary>The shared setup file written alongside the generated tests. A module initializer pins the
    /// ambient culture to invariant so the golden masters are reproducible across machines/CI.</summary>
    private const string GeneratedSetupFileName = "__PinionGeneratedSetup.cs";

    private const string GeneratedSetupSource =
        """
        // <auto-generated> Pinion characterization-test setup — do not edit.
        // Pins the ambient culture to invariant so generated golden masters are REPRODUCIBLE across
        // machines/CI. Without this, a method that uses CurrentCulture (e.g. value.ToString() with no
        // IFormatProvider, or DateTime formatting) captures machine/locale-specific output, and the
        // snapshot flakes on a different OS or locale. Sets the DEFAULT culture only — code that selects
        // an explicit CultureInfo still behaves normally, so real culture-specific behavior is preserved.
        using System.Globalization;
        using System.Runtime.CompilerServices;

        namespace Pinion.Generated;

        internal static class __PinionGeneratedSetup
        {
            [ModuleInitializer]
            internal static void PinCulture()
            {
                CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
                CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            }
        }
        """;

    /// <summary>Write (or refresh) the culture-pinning setup file into the generated-tests folder.</summary>
    private static void EnsureGeneratedSetup(string outDir)
    {
        string path = Path.Combine(outDir, GeneratedSetupFileName);
        try
        {
            if (!File.Exists(path) || File.ReadAllText(path) != GeneratedSetupSource)
                File.WriteAllText(path, GeneratedSetupSource);
        }
        catch {  }
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
