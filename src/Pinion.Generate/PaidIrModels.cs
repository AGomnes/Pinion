using Pinion.Engine.Model;

namespace Pinion.Generate;

// Paid-tier IR for the `generate` (AI) half of the product. Lives in the paid
// assembly so the free engine has no compile-time path to it.

/// <summary>A characterization test produced for a <see cref="CodeUnit"/>, ready to compile and run.</summary>
/// <param name="Unit">The unit under characterization.</param>
/// <param name="TestClassName">Name of the emitted test class.</param>
/// <param name="SourceCode">Full source of the generated test file.</param>
/// <param name="FilePath">Where the test file was (or will be) written.</param>
public sealed record GeneratedTest(
    CodeUnit Unit,
    string TestClassName,
    string SourceCode,
    string FilePath);

/// <summary>The outcome of compiling + running a <see cref="GeneratedTest"/> against the real current code.</summary>
/// <param name="Compiled">Whether the test compiled.</param>
/// <param name="Passed">Whether the test ran to a clean result (the golden master was captured).</param>
/// <param name="Diagnostics">Compiler/runtime messages, used to drive the repair loop.</param>
/// <param name="SnapshotPath">Path to the captured approval snapshot, if any.</param>
public sealed record ExecutionResult(
    bool Compiled,
    bool Passed,
    IReadOnlyList<string> Diagnostics,
    string? SnapshotPath);
