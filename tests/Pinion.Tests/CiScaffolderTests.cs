using Pinion.Engine.Ci;
using Xunit;
using YamlDotNet.Serialization;

namespace Pinion.Tests;

public class CiScaffolderTests
{
    private static object ParseYaml(string yaml) =>
        new Deserializer().Deserialize<object>(yaml)
            ?? throw new Xunit.Sdk.XunitException("YAML parsed to null");

    [Theory]
    [InlineData(CiProvider.GitHub, false)]
    [InlineData(CiProvider.GitHub, true)]
    [InlineData(CiProvider.Azure, false)]
    [InlineData(CiProvider.Azure, true)]
    public void Generated_workflow_is_valid_yaml(CiProvider provider, bool withProve)
    {
        string yaml = CiScaffolder.Generate(new CiOptions(provider, "tests/My.Tests.csproj", null, withProve));
        // Throws on bad indentation/structure — this is the guard against splice/indent regressions.
        ParseYaml(yaml);
    }

    [Fact]
    public void Behavior_gate_runs_the_named_test_project()
    {
        string yaml = CiScaffolder.Generate(new CiOptions(CiProvider.GitHub, "tests/My.Tests.csproj", null, false));
        // The core value: the committed characterization tests run as the gate.
        Assert.Contains("dotnet test tests/My.Tests.csproj", yaml);
    }

    [Fact]
    public void Prove_step_is_opt_in()
    {
        var opts = new CiOptions(CiProvider.GitHub, "tests/My.Tests.csproj", null, WithProve: false);
        Assert.DoesNotContain("pinion prove", CiScaffolder.Generate(opts));
        Assert.Contains("pinion prove -p tests/My.Tests.csproj", CiScaffolder.Generate(opts with { WithProve = true }));
    }

    [Fact]
    public void Windows_paths_are_normalised_to_forward_slashes()
    {
        string yaml = CiScaffolder.Generate(new CiOptions(CiProvider.Azure, @"tests\My.Tests.csproj", null, false));
        Assert.Contains("tests/My.Tests.csproj", yaml);
        Assert.DoesNotContain(@"tests\My.Tests.csproj", yaml);
    }

    [Fact]
    public void Missing_test_project_emits_an_editable_placeholder()
    {
        string yaml = CiScaffolder.Generate(new CiOptions(CiProvider.GitHub, "", null, false));
        Assert.Contains("path/to/YourProject.Tests.csproj", yaml);
        ParseYaml(yaml); // still valid YAML
    }
}
