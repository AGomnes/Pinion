using System.CommandLine;
using System.Text.RegularExpressions;
using Pinion.Engine.Scaffolding;

namespace Pinion.Cli;

/// <summary>
/// `pinion init-tests &lt;code.csproj&gt;` — scaffold a Verify-ready xUnit project to host the
/// characterization tests `generate` writes. Removes the one setup step `generate` needs that `analyze`
/// doesn't. Free tier: it only emits a .csproj.
/// </summary>
internal static class InitTestsCommand
{
    public static Command Build()
    {
        var codeArg = new Argument<string>("code-project")
        {
            Description = "The .csproj (or a directory containing one) whose behavior you'll characterize — added as a ProjectReference.",
        };
        var outOption = new Option<DirectoryInfo?>("--out", "-o")
        {
            Description = "Directory to create the test project under (default: alongside the code project).",
        };
        var nameOption = new Option<string?>("--name")
        {
            Description = "Test project name (default: <CodeProject>.CharacterizationTests).",
        };
        var tfmOption = new Option<string>("--tfm")
        {
            Description = "Target framework for the test project.",
            DefaultValueFactory = _ => TestProjectScaffolder.DefaultTargetFramework,
        };
        var forceOption = new Option<bool>("--force") { Description = "Overwrite the test project if it already exists." };
        var stdoutOption = new Option<bool>("--stdout") { Description = "Print the .csproj to stdout instead of writing it." };

        var cmd = new Command("init-tests", "Scaffold a Verify-ready xUnit project to host generated characterization tests.")
        {
            codeArg, outOption, nameOption, tfmOption, forceOption, stdoutOption,
        };

        cmd.SetAction((parse, _) =>
        {
            string? codeCsproj = ResolveCsproj(parse.GetValue(codeArg)!);
            if (codeCsproj is null)
            {
                Console.Error.WriteLine($"error: no .csproj found at '{parse.GetValue(codeArg)}'.");
                return Task.FromResult(2);
            }

            string name = parse.GetValue(nameOption)
                ?? Path.GetFileNameWithoutExtension(codeCsproj) + ".CharacterizationTests";
            name = Regex.Replace(name, @"[^A-Za-z0-9_.]", "_");

            string outDir = parse.GetValue(outOption)?.FullName
                ?? Path.GetDirectoryName(codeCsproj)!;
            string projDir = Path.Combine(outDir, name);
            string projFile = Path.Combine(projDir, name + ".csproj");

            string relRef = Path.GetRelativePath(projDir, codeCsproj);
            string content = TestProjectScaffolder.Csproj(relRef, parse.GetValue(tfmOption)!);

            if (parse.GetValue(stdoutOption))
            {
                Console.Out.WriteLine(content);
                return Task.FromResult(0);
            }

            if (File.Exists(projFile) && !parse.GetValue(forceOption))
            {
                Console.Error.WriteLine($"error: {projFile} already exists. Use --force to overwrite or --stdout to preview.");
                return Task.FromResult(1);
            }

            Directory.CreateDirectory(projDir);
            File.WriteAllText(projFile, content);

            Console.Out.WriteLine($"Wrote test project to {projFile}");
            Console.Out.WriteLine("Next:");
            Console.Out.WriteLine($"  dotnet restore \"{projFile}\"");
            Console.Out.WriteLine($"  pinion generate \"{codeCsproj}\" -p \"{projFile}\" --target <method>");
            return Task.FromResult(0);
        });

        return cmd;
    }

    /// <summary>Resolve a .csproj path or a directory containing exactly one .csproj to a full path.</summary>
    private static string? ResolveCsproj(string input)
    {
        if (File.Exists(input) && input.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(input);

        if (Directory.Exists(input))
        {
            var found = Directory.GetFiles(Path.GetFullPath(input), "*.csproj");
            if (found.Length == 1) return found[0];
        }
        return null;
    }
}
