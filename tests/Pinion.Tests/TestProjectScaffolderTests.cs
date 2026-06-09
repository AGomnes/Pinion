using Pinion.Engine.Scaffolding;
using Xunit;

namespace Pinion.Tests;

public class TestProjectScaffolderTests
{
    [Fact]
    public void Emits_a_verify_ready_xunit_project_referencing_the_code()
    {
        string csproj = TestProjectScaffolder.Csproj(@"..\MyApp\MyApp.csproj", "net9.0");

        Assert.Contains("<TargetFramework>net9.0</TargetFramework>", csproj);
        Assert.Contains("Verify.Xunit", csproj);                                   // the snapshot engine
        Assert.Contains("Microsoft.NET.Test.Sdk", csproj);
        Assert.Contains(@"<ProjectReference Include=""..\MyApp\MyApp.csproj"" />", csproj);
        Assert.Contains("<IsTestProject>true</IsTestProject>", csproj);
    }

    [Fact]
    public void Defaults_to_the_migration_audiences_target_framework()
    {
        Assert.Contains("<TargetFramework>net8.0</TargetFramework>", TestProjectScaffolder.Csproj("x.csproj"));
    }
}
