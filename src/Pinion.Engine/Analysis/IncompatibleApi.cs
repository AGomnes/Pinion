namespace Pinion.Engine.Analysis;

/// <summary>
/// A framework type the code uses that does not exist on the target framework — a migration blocker
/// found by resolving against the target's own reference assemblies rather than a curated list.
/// </summary>
/// <param name="TypeName">Fully-qualified metadata name, e.g. <c>System.Threading.Lock</c>.</param>
/// <param name="Assembly">Framework assembly the type came from in the current compilation.</param>
/// <param name="UsageCount">How many references appear across the codebase — a rough blast radius.</param>
/// <param name="FirstFilePath">Where to start looking.</param>
/// <param name="FirstLine">1-based line of the first usage.</param>
public sealed record IncompatibleApi(
    string TypeName,
    string Assembly,
    int UsageCount,
    string FirstFilePath,
    int FirstLine);
