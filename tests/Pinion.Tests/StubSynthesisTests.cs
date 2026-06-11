using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Pinion.Adapters.CSharp.Generate;
using Xunit;

namespace Pinion.Tests;

public class StubSynthesisTests
{
    private static INamedTypeSymbol Interface(string source, string metadataName)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var comp = CSharpCompilation.Create("StubTest", new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        return (INamedTypeSymbol)comp.GetTypeByMetadataName(metadataName)!;
    }

    [Fact]
    public void Interface_inheriting_an_unresolved_interface_is_not_stubbed()
    {
        // IRepo : Ext.IRepoBase where the base lives in a package that didn't restore on a source scan →
        // its members are invisible, so a generated stub would be incomplete and fail to compile (CS0535).
        // CanStub must decline, so the synthesizer falls back to default! rather than a broken stub.
        var iface = Interface(
            "namespace N { public interface IRepo : Ext.IRepoBase { int Compute(); } }", "N.IRepo");

        Assert.Contains(iface.AllInterfaces, i => i.TypeKind == TypeKind.Error); // base unresolved (sanity)
        Assert.False(CSharpDeterministicSynthesizer.CanStub(iface));
    }

    [Fact]
    public void Fully_resolved_service_interface_is_stubbable()
    {
        var iface = Interface(
            "namespace N { public interface IClock { System.DateTime Now { get; } int Tick(); } }", "N.IClock");

        Assert.DoesNotContain(iface.AllInterfaces, i => i.TypeKind == TypeKind.Error);
        Assert.True(CSharpDeterministicSynthesizer.CanStub(iface));
    }
}
