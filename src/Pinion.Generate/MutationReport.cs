namespace Pinion.Generate;

/// <summary>Mutation results for one source file.</summary>
public sealed record MutationFileResult(string File, int Killed, int Survived, int NoCoverage, int Timeout)
{
    /// <summary>Mutants that were actually testable (excludes ignored/compile-error).</summary>
    public int Tested => Killed + Survived + NoCoverage + Timeout;

    /// <summary>Percent of behaviour-changing mutations the tests caught.</summary>
    public double Score => Tested == 0 ? 0 : 100.0 * (Killed + Timeout) / Tested;
}

/// <summary>
/// The outcome of mutation testing — the empirical answer to "do these tests actually catch
/// regressions?". A killed/timed-out mutant means a behaviour change was detected; a survived or
/// uncovered mutant means a change slipped through.
/// </summary>
public sealed record MutationReport(
    int Killed,
    int Survived,
    int NoCoverage,
    int Timeout,
    IReadOnlyList<MutationFileResult> Files)
{
    public int Tested => Killed + Survived + NoCoverage + Timeout;
    public double Score => Tested == 0 ? 0 : 100.0 * (Killed + Timeout) / Tested;
}
