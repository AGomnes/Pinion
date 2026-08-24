using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Pinion.Adapters.CSharp;
using Xunit;

namespace Pinion.Tests;

/// <summary>
/// The analyzer must see ALL behaviour-carrying members, not just methods: constructors, operators,
/// computed properties/indexers, and local functions are common migration-risk carriers. Auto-property
/// accessors carry no behaviour and must NOT add noise.
/// </summary>
public class MemberCoverageTests
{
    private static List<(string Id, string Display, int Complexity)> Members(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var refs = tpa.Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        var comp = CSharpCompilation.Create("MemberTest", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = comp.GetSemanticModel(tree);

        return CSharpAdapter.BehaviorMembers(tree.GetRoot(), model, default)
            .Select(m => (RoslynSymbols.MethodId(m.Symbol), RoslynSymbols.DisplayName(m.Symbol),
                CyclomaticComplexity.Compute(m.Body)))
            .ToList();
    }

    private const string Sample = """
        using System;
        public class Account
        {
            private decimal _balance;
            public Account(decimal opening)            // constructor
            {
                if (opening < 0) throw new ArgumentException(nameof(opening));
                _balance = opening;
            }

            public decimal Balance => _balance;        // expression-bodied (computed) property

            public string Status                        // property with a body-bearing getter
            {
                get { return _balance > 0 ? "ok" : "empty"; }
            }

            public int Plain { get; set; }              // auto-property — NO behaviour, must be skipped

            public decimal Withdraw(decimal amount)     // ordinary method
            {
                int Clamp(decimal a) { if (a < 0) return 0; return 1; }   // local function
                _balance -= amount;
                return _balance * Clamp(amount);
            }

            public static Account operator +(Account a, decimal d) => new Account(a._balance + d); // operator
        }
        """;

    [Fact]
    public void Constructor_property_accessor_local_function_and_operator_are_all_units()
    {
        var display = Members(Sample).Select(m => m.Display).ToList();

        Assert.Contains("Account.ctor", display);
        Assert.Contains("Account.Balance.get", display);
        Assert.Contains("Account.Status.get", display);
        Assert.Contains("Account.Withdraw", display);
        Assert.Contains("Account.Clamp (local)", display);
        Assert.Contains("Account.op_Addition", display);
    }

    [Fact]
    public void Auto_property_accessors_are_not_units()
    {
        var display = Members(Sample).Select(m => m.Display).ToList();
        Assert.DoesNotContain("Account.Plain.get", display);
        Assert.DoesNotContain("Account.Plain.set", display);
    }

    [Fact]
    public void Local_function_complexity_is_attributed_to_itself_not_the_parent()
    {
        var members = Members(Sample);

        var clamp = members.Single(m => m.Display == "Account.Clamp (local)");
        var withdraw = members.Single(m => m.Display == "Account.Withdraw");
        Assert.Equal(2, clamp.Complexity);
        Assert.Equal(1, withdraw.Complexity);

        var ctor = members.Single(m => m.Display == "Account.ctor");
        Assert.Equal(2, ctor.Complexity);
    }

    [Fact]
    public void Local_function_id_is_qualified_by_its_enclosing_method()
    {
        var clamp = Members(Sample).Single(m => m.Display == "Account.Clamp (local)");
        Assert.Contains("Withdraw", clamp.Id);
        Assert.Contains("/Clamp(", clamp.Id);
    }

    [Fact]
    public void Blast_radius_syntactic_fallback_matches_only_when_unambiguous()
    {
        var index = new Dictionary<(string, int), List<string>>
        {
            [("Twice", 1)] = new() { "H.Twice(int)" },
            [("Add", 1)] = new() { "A.Add(int)", "B.Add(int)" },
        };

        Assert.Equal("H.Twice(int)", CSharpAdapter.UniqueCalleeByName(index, "Twice", 1, "caller"));
        Assert.Null(CSharpAdapter.UniqueCalleeByName(index, "Add", 1, "caller"));
        Assert.Null(CSharpAdapter.UniqueCalleeByName(index, "Twice", 1, "H.Twice(int)"));
        Assert.Null(CSharpAdapter.UniqueCalleeByName(index, "Twice", 2, "caller"));
        Assert.Null(CSharpAdapter.UniqueCalleeByName(index, "Missing", 0, "caller"));
    }
}
