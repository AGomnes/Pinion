namespace Pinion.Engine.Scaffolding;

/// <summary>
/// Emits a Verify-ready xUnit test project to HOST Pinion's generated characterization tests. This is the
/// one piece of setup `generate` needs that `analyze` doesn't — a project that references the code under
/// test plus xunit + Verify — so scaffolding it removes the main friction of starting to lock behavior.
/// Free tier: it only emits text (no network, no build).
/// </summary>
public static class TestProjectScaffolder
{
    public const string DefaultTargetFramework = "net8.0";

    /// <summary>
    /// The .csproj text for a host test project that references the code under test at
    /// <paramref name="codeProjectRelativePath"/>. Package versions match the bundled sample, which is the
    /// set the deterministic generator's emitted tests are known to compile and run against.
    /// </summary>
    public static string Csproj(string codeProjectRelativePath, string targetFramework = DefaultTargetFramework) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <TargetFramework>{targetFramework}</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <IsPackable>false</IsPackable>
            <IsTestProject>true</IsTestProject>
          </PropertyGroup>

          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.8.0" />
            <PackageReference Include="xunit" Version="2.9.3" />
            <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
            <!-- Verify powers the golden-master snapshots Pinion generates (used via `using static VerifyXunit.Verifier;`). -->
            <PackageReference Include="Verify.Xunit" Version="31.12.5" />
          </ItemGroup>

          <ItemGroup>
            <!-- The code whose current behavior you'll lock as golden masters. -->
            <ProjectReference Include="{codeProjectRelativePath}" />
          </ItemGroup>

        </Project>
        """;
}
