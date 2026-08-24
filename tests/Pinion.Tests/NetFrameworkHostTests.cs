using Pinion.Adapters.CSharp.Generate;
using Pinion.Engine.Model;
using Pinion.Engine.Scaffolding;
using Xunit;

namespace Pinion.Tests;

/// <summary>
/// Pinion's main use case is locking behavior BEFORE a migration, so the host test project is very
/// often still .NET Framework. Two defaults made that impossible until they were fixed, and both fail
/// as a compile error inside the generated project rather than anywhere visible, so they are pinned
/// here: `generate` reported "0 characterized" with the real reason buried in build output.
/// </summary>
public class NetFrameworkHostTests
{
    [Theory]
    [InlineData("net48")]
    [InlineData("net472")]
    [InlineData("net462")]
    public void Framework_targets_pin_the_language_version(string tfm)
    {
        // .NET Framework defaults to C# 7.3, which rejects Nullable and ImplicitUsings with CS8630.
        string csproj = TestProjectScaffolder.Csproj("../Code/Code.csproj", tfm);

        Assert.Contains("<LangVersion>latest</LangVersion>", csproj);
        Assert.Contains($"<TargetFramework>{tfm}</TargetFramework>", csproj);
    }

    [Theory]
    [InlineData("net8.0")]
    [InlineData("net10.0")]
    public void Modern_targets_do_not_need_the_pin(string tfm)
    {
        // Modern SDKs already default to a recent language version; adding LangVersion there would
        // silently opt projects into preview features they did not ask for.
        Assert.DoesNotContain("<LangVersion>", TestProjectScaffolder.Csproj("../Code/Code.csproj", tfm));
    }

    [Fact]
    public async Task Generated_setup_polyfills_ModuleInitializer_for_framework_targets()
    {
        // ModuleInitializerAttribute arrived in .NET 5. On .NET Framework the BCL has no such type and
        // the copies inside other packages are internal to their own assemblies (CS0122), so the
        // generated setup has to declare its own, guarded so modern targets use the real one.
        string dir = Path.Combine(Path.GetTempPath(), "pinion-fw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string proj = Path.Combine(dir, "T.csproj");
            File.WriteAllText(proj, "<Project/>");

            using var gen = new CSharpTestGenerator();
            gen.ConfigureGeneration(proj);
            var unit = new CodeUnit("S.M()", "S.M", "S.cs", 1, 2, "sig",
                Array.Empty<ParamInfo>(), "void", 1, 1, Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), HasTests: false, IsPublicEntryPoint: true, Array.Empty<string>());

            await gen.EmitTestAsync(unit, "public class S_M_CharacterizationTests {}", default);

            string setup = await File.ReadAllTextAsync(
                Path.Combine(dir, "PinionCharacterization", "__PinionGeneratedSetup.cs"));

            Assert.Contains("#if !NET5_0_OR_GREATER", setup);
            Assert.Contains("class ModuleInitializerAttribute", setup);
            Assert.Contains("#endif", setup);
            Assert.Contains("[ModuleInitializer]", setup);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
