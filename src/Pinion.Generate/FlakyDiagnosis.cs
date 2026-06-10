using Pinion.Engine.Model;

namespace Pinion.Generate;

/// <summary>
/// Explains why a characterization target couldn't be locked because its output is non-deterministic —
/// the snapshot differed between two runs of the same code. A flaky golden master is worse than none (it
/// fails every future <c>verify</c> for no real reason), so the pipeline quarantines these and reports a
/// cause + remedy instead of shipping them. When the static seam analysis already named the ambient
/// dependency (e.g. <c>DateTime.Now</c>), point straight at it; otherwise give the common culprits.
/// </summary>
public static class FlakyDiagnosis
{
    public static string Explain(CodeUnit unit)
    {
        var blockers = unit.SeamBlockers;
        if (blockers.Count > 0)
            return "non-deterministic: the captured result changed between two runs because this method depends on " +
                   $"{string.Join(", ", blockers.Take(4))} (ambient state that varies per run). Introduce a seam " +
                   "(inject a clock, wrap the dependency behind an interface) so the output is stable, then re-lock. " +
                   "Skipped to avoid a flaky golden master.";

        return "non-deterministic: the captured result changed between two runs of the same code — likely time, " +
               "randomness, a GUID/hash, or an unordered collection. Make the output stable (or inject the varying " +
               "dependency) before locking. Skipped to avoid a flaky golden master.";
    }
}
