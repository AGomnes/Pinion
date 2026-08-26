using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Pinion.Adapters.CSharp;
using Xunit;

namespace Pinion.Tests;

/// <summary>
/// Compatibility checking resolves the types a codebase uses against the TARGET framework's own
/// reference assemblies, so there is no curated catalog of removed APIs to maintain or go stale.
/// These tests drive the real mechanism against the reference packs installed on the machine.
/// </summary>
public class TargetFrameworkCompatibilityTests
{
    /// <summary>Compile <paramref name="source"/> against the reference assemblies of
    /// <paramref name="againstTfm"/>, which is how a project targeting that framework would resolve.</summary>
    private static Compilation CompilationFor(string source, string againstTfm)
    {
        string? refDir = ReferencePackLocator.Locate(againstTfm);
        Assert.True(refDir is not null, $"{againstTfm} reference assemblies are not installed on this machine");

        var refs = Directory.GetFiles(refDir!, "*.dll").Select(d => (MetadataReference)MetadataReference.CreateFromFile(d));
        return CSharpCompilation.Create("CompatTest",
            new[] { CSharpSyntaxTree.ParseText(source, path: "Test.cs") }, refs);
    }

    [Fact]
    public void Reference_packs_are_discoverable()
    {
        var installed = ReferencePackLocator.Installed();
        Assert.NotEmpty(installed);
        Assert.All(installed, tfm => Assert.StartsWith("net", tfm));
    }

    [Fact]
    public void Api_missing_from_the_target_is_reported()
    {
        // System.Threading.Lock arrived in .NET 9. Code that compiles on net10.0 therefore fails on
        // net8.0, which is exactly the class of breakage a readiness report must surface.
        const string src = """
            using System.Threading;
            public class C
            {
                private readonly Lock _gate = new();
                public void M() { lock (_gate) { } }
            }
            """;

        var compilation = CompilationFor(src, "net10.0");
        var found = TargetFrameworkCompatibility.Check(compilation, "net8.0", null, default);

        Assert.Contains(found, a => a.TypeName == "System.Threading.Lock");
        var hit = found.First(a => a.TypeName == "System.Threading.Lock");
        Assert.Equal("Test.cs", hit.FirstFilePath);
        Assert.True(hit.FirstLine > 0, "a finding must point at a real line");
    }

    [Fact]
    public void Api_present_on_the_target_is_not_reported()
    {
        // The same shape using a type that has existed for years must stay silent. A readiness report
        // that cries wolf is worse than one that says less.
        const string src = """
            using System.Collections.Generic;
            public class C
            {
                private readonly List<int> _items = new();
                public int M() => _items.Count;
            }
            """;

        var compilation = CompilationFor(src, "net10.0");
        var found = TargetFrameworkCompatibility.Check(compilation, "net8.0", null, default);

        Assert.DoesNotContain(found, a => a.TypeName.StartsWith("System.Collections.Generic.List"));
    }

    [Fact]
    public void Unknown_target_reports_nothing_rather_than_guessing()
    {
        // An uninstalled framework means "cannot tell", which must never be rendered as "incompatible".
        var compilation = CompilationFor("public class C { }", "net10.0");
        Assert.Empty(TargetFrameworkCompatibility.Check(compilation, "net99.0", null, default));
    }

    [Fact]
    public void Unresolvable_code_reports_nothing()
    {
        // Legacy projects frequently do not restore on a modern machine. Their symbols come back as
        // error types, and guessing from those would flag the whole codebase as a migration blocker.
        var compilation = CSharpCompilation.Create("NoRefs",
            new[] { CSharpSyntaxTree.ParseText("public class C { private SomeMissingType _x; }") });

        Assert.Empty(TargetFrameworkCompatibility.Check(compilation, "net8.0", null, default));
    }
}
