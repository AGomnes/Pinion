using System.CommandLine;
using Pinion.Engine.Ci;

namespace Pinion.Cli;

/// <summary>
/// `pinion ci` — scaffold a CI workflow that runs the committed characterization tests as a behavior
/// gate (and, optionally, the risk report and mutation score). Free tier: it only emits text.
/// </summary>
internal static class CiCommand
{
    public static Command Build()
    {
        var providerOption = new Option<CiProvider>("--provider")
        {
            Description = "Which CI system to scaffold for.",
            DefaultValueFactory = _ => CiProvider.GitHub,
        };
        var testProjectOption = new Option<string?>("--test-project", "-p")
        {
            Description = "Characterization test project (.csproj) the gate runs. Omit to emit a placeholder to edit.",
        };
        var solutionOption = new Option<string?>("--solution", "-s")
        {
            Description = "Solution/project for the optional analyze step. Omit to emit a placeholder.",
        };
        var outOption = new Option<FileInfo?>("--out", "-o")
        {
            Description = "Where to write the workflow (default: the provider's conventional path).",
        };
        var withProveOption = new Option<bool>("--with-prove")
        {
            Description = "Include the mutation-score step (needs dotnet-stryker).",
        };
        var forceOption = new Option<bool>("--force") { Description = "Overwrite the workflow file if it already exists." };
        var stdoutOption = new Option<bool>("--stdout") { Description = "Print the workflow to stdout instead of writing a file." };

        var cmd = new Command("ci", "Scaffold a CI workflow that gates PRs on the characterization tests (GitHub Actions or Azure DevOps).")
        {
            providerOption, testProjectOption, solutionOption, outOption, withProveOption, forceOption, stdoutOption,
        };

        cmd.SetAction((parse, _) =>
        {
            var provider = parse.GetValue(providerOption);
            var options = new CiOptions(
                provider,
                parse.GetValue(testProjectOption) ?? "",
                parse.GetValue(solutionOption),
                parse.GetValue(withProveOption));

            string content = CiScaffolder.Generate(options);

            if (parse.GetValue(stdoutOption))
            {
                Console.Out.WriteLine(content);
                return Task.FromResult(0);
            }

            var target = parse.GetValue(outOption)?.FullName
                ?? Path.GetFullPath(CiScaffolder.DefaultPath(provider));

            if (File.Exists(target) && !parse.GetValue(forceOption))
            {
                Console.Error.WriteLine($"error: {target} already exists. Use --force to overwrite or --stdout to preview.");
                return Task.FromResult(1);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);

            Console.Out.WriteLine($"Wrote {provider} workflow to {target}");
            Console.Out.WriteLine("Next: set the test-project path (if placeholder), commit it, and open a PR to see the behavior gate run.");
            return Task.FromResult(0);
        });

        return cmd;
    }
}
