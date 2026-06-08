namespace Pinion.Engine.Ci;

/// <summary>Which CI system to emit a workflow for.</summary>
public enum CiProvider
{
    GitHub,
    Azure,
}

/// <summary>Inputs for scaffolding a CI workflow.</summary>
public sealed record CiOptions(
    CiProvider Provider,
    string TestProject,
    string? Solution,
    bool WithProve);

/// <summary>
/// Emits a ready-to-use CI workflow that hangs the behavior net in the doorway: the generated
/// characterization tests run as a gate that FAILS a PR when a migration (framework upgrade,
/// refactor) silently changes observable behavior. Free-tier adoption tooling — pure string
/// generation (no analysis, no MSBuild), so it's trivially unit-testable.
/// </summary>
public static class CiScaffolder
{
    private const string TestProjectPlaceholder = "path/to/YourProject.Tests.csproj";
    private const string SolutionPlaceholder = "path/to/YourSolution.sln";

    /// <summary>The conventional file path for the chosen provider (relative to repo root).</summary>
    public static string DefaultPath(CiProvider provider) => provider switch
    {
        CiProvider.GitHub => ".github/workflows/pinion.yml",
        CiProvider.Azure => "azure-pipelines-pinion.yml",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public static string Generate(CiOptions o)
    {
        string test = string.IsNullOrWhiteSpace(o.TestProject) ? TestProjectPlaceholder : Normalize(o.TestProject);
        string solution = string.IsNullOrWhiteSpace(o.Solution) ? SolutionPlaceholder : Normalize(o.Solution!);
        return o.Provider switch
        {
            CiProvider.GitHub => GitHub(test, solution, o.WithProve),
            CiProvider.Azure => Azure(test, solution, o.WithProve),
            _ => throw new ArgumentOutOfRangeException(nameof(o)),
        };
    }

    // Forward slashes so the path is valid in YAML on any CI runner (Linux agents).
    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string GitHub(string test, string solution, bool withProve)
    {
        // Explicit 6-space step indentation (raw-string dedent won't align when spliced at column 0).
        string prove = withProve
            ? "\n" +
              "      # Mutation score (paid `prove` tier) — how many regressions the tests actually catch.\n" +
              "      # Needs the dotnet-stryker tool and a PINION_LICENSE repo secret.\n" +
              "      - name: Pinion mutation score\n" +
              "        run: |\n" +
              "          dotnet tool install --global dotnet-stryker\n" +
              $"          pinion prove -p {test}\n" +
              "        env:\n" +
              "          PINION_LICENSE: ${{ secrets.PINION_LICENSE }}\n"
            : "";

        return $$"""
            # Pinion — behavior safety net for legacy .NET migration.
            # The characterization tests are golden masters of CURRENT behavior; this workflow fails a
            # PR when a change (framework upgrade, refactor, dependency bump) alters that behavior.
            name: Pinion behavior gate

            on:
              pull_request:
              push:
                branches: [ main, master ]

            jobs:
              behavior-gate:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4

                  - name: Set up .NET
                    uses: actions/setup-dotnet@v4
                    with:
                      dotnet-version: 8.0.x

                  # THE GATE: characterization tests fail if observable behavior changed.
                  - name: Characterization tests
                    run: dotnet test {{test}} --configuration Release

                  # Optional — risk report (free `analyze` tier). Requires the pinion CLI on PATH.
                  # - name: Pinion risk report
                  #   run: pinion analyze {{solution}} --format markdown --out pinion-analysis.md
                  # - uses: actions/upload-artifact@v4
                  #   with:
                  #     name: pinion-analysis
                  #     path: pinion-analysis.md
            {{prove}}
            """;
    }

    private static string Azure(string test, string solution, bool withProve)
    {
        // Explicit 2-space step indentation (raw-string dedent won't align when spliced at column 0).
        string prove = withProve
            ? "\n" +
              "  # Mutation score (paid `prove` tier). Needs dotnet-stryker and a PINION_LICENSE pipeline secret.\n" +
              "  - script: |\n" +
              "      dotnet tool install --global dotnet-stryker\n" +
              $"      pinion prove -p {test}\n" +
              "    displayName: 'Pinion mutation score'\n" +
              "    env:\n" +
              "      PINION_LICENSE: $(PINION_LICENSE)\n"
            : "";

        return $$"""
            # Pinion — behavior safety net for legacy .NET migration.
            # The characterization tests are golden masters of CURRENT behavior; this pipeline fails a
            # PR when a change (framework upgrade, refactor, dependency bump) alters that behavior.
            trigger:
              branches:
                include: [ main, master ]
            pr:
              branches:
                include: [ main, master ]

            pool:
              vmImage: ubuntu-latest

            steps:
              - task: UseDotNet@2
                displayName: 'Set up .NET'
                inputs:
                  version: 8.0.x

              # THE GATE: characterization tests fail if observable behavior changed.
              - script: dotnet test {{test}} --configuration Release
                displayName: 'Characterization tests'

              # Optional — risk report (free `analyze` tier). Requires the pinion CLI on PATH.
              # - script: pinion analyze {{solution}} --format markdown --out pinion-analysis.md
              #   displayName: 'Pinion risk report'
              # - publish: pinion-analysis.md
              #   artifact: pinion-analysis
            {{prove}}
            """;
    }
}
