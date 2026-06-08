namespace Pinion.Engine.Analysis;

/// <summary>
/// Real executed-coverage numbers (from Coverlet), as opposed to the static
/// "is this symbol referenced by a test?" signal. This is what turns "we wrote
/// tests" into the verifiable claim "we cover N% of branches".
/// </summary>
public sealed record CoverageSummary(
    int CoveredLines,
    int TotalLines,
    int CoveredBranches,
    int TotalBranches)
{
    public double LineRate => TotalLines == 0 ? 0 : (double)CoveredLines / TotalLines;
    public double BranchRate => TotalBranches == 0 ? 0 : (double)CoveredBranches / TotalBranches;
}
