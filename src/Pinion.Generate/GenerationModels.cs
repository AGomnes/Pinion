using Pinion.Engine.Model;

namespace Pinion.Generate;

/// <summary>
/// The per-target context the engine sends to the model. Adapters extract it from
/// source; the engine treats it as opaque text. This is the ONLY code that leaves the
/// machine — keep it to the method plus the type signatures it needs, never config/secrets.
/// </summary>
public sealed record GenerationContext(
    CodeUnit Unit,
    string Namespace,
    string ContainingType,
    string SourceCode,
    IReadOnlyList<string> CalleeSignatures);

/// <summary>Knobs for the generate pipeline.</summary>
public sealed record GenerationOptions(
    string Model = GenerationOptions.DefaultModel,
    int MaxTokens = 4096,
    int MaxRepairAttempts = 3,
    bool DryRun = false)
{
    /// <summary>Spec §6.3: reserve Sonnet for test-body reasoning.</summary>
    public const string DefaultModel = "claude-sonnet-4-6";

    /// <summary>Spec §6.3: route cheap structural passes to Haiku.</summary>
    public const string CheapModel = "claude-haiku-4-5";

    public static GenerationOptions Default { get; } = new();
}

/// <summary>The outcome of generating a characterization test for one unit.</summary>
public sealed record GenerationResult(
    CodeUnit Unit,
    bool Success,
    int Attempts,
    string? TestFilePath,
    string? SnapshotPath,
    IReadOnlyList<string> Diagnostics);
