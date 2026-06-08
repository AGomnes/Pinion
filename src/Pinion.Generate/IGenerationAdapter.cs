using Pinion.Engine.Model;

namespace Pinion.Generate;

/// <summary>
/// The paid-tier, language-specific surface for the `generate` pipeline. Kept separate
/// from the free <c>ILanguageAdapter</c> (analyze) so the free engine and adapters have
/// NO compile-time dependency on the paid generation code — the dependency only ever
/// flows paid → free.
/// </summary>
public interface IGenerationAdapter
{
    /// <summary>Extract the per-target context (source + signatures) the model needs.</summary>
    Task<GenerationContext> ExtractContextAsync(CodeUnit unit, CancellationToken ct);

    /// <summary>Write a model-generated test body into a compilable test artifact.</summary>
    Task<GeneratedTest> EmitTestAsync(CodeUnit unit, string generatedBody, CancellationToken ct);

    /// <summary>Compile + run a generated test against the real code and capture the golden master.</summary>
    Task<ExecutionResult> RunAndSnapshotAsync(GeneratedTest test, CancellationToken ct);
}
