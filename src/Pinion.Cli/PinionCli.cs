using System.CommandLine;

namespace Pinion.Cli;

/// <summary>The format written to stdout (and, with --out, to a file).</summary>
public enum OutputFormat
{
    Console,
    Markdown,
    Json,
    Html,
}

/// <summary>Wires up the `pinion` command tree.</summary>
public static class PinionCli
{
    public static RootCommand BuildRootCommand()
    {
        var root = new RootCommand(
            "Pinion — lock the current behavior of legacy .NET so you can migrate without breaking the base.");

        root.Subcommands.Add(AnalyzeCommand.Build());
        root.Subcommands.Add(GenerateCommand.Build());
        root.Subcommands.Add(VerifyCommand.Build());
        root.Subcommands.Add(ProveCommand.Build());
        root.Subcommands.Add(CiCommand.Build());
        root.Subcommands.Add(LicenseCommand.Build());
        return root;
    }
}
