using Pinion.Engine.Analysis;
using Pinion.Engine.Model;
using Xunit;

namespace Pinion.Tests;

public class DomainTaggerTests
{
    private static IReadOnlyList<string> Tag(
        string method, string type = "", string[]? paramTypes = null,
        string ret = "void", string[]? refs = null) =>
        DomainTagger.Tag(method, type, paramTypes ?? Array.Empty<string>(), ret, refs ?? Array.Empty<string>());

    [Fact]
    public void Money_terms_are_tagged()
    {
        Assert.Contains(DomainTag.Money, Tag("CalculateVat", "InvoiceService", ret: "decimal"));
    }

    [Fact]
    public void Auth_terms_are_tagged()
    {
        Assert.Contains(DomainTag.Auth, Tag("ValidateToken", "AuthHandler", new[] { "string" }, "bool", new[] { "token" }));
    }

    [Fact]
    public void Validate_is_not_mistaken_for_date()
    {
        // "valiDATE" contains "date" as a substring — must not produce a date tag.
        Assert.DoesNotContain(DomainTag.Date, Tag("ValidateToken", "AuthHandler"));
    }

    [Fact]
    public void Data_transform_terms_are_tagged()
    {
        Assert.Contains(DomainTag.DataTransform, Tag("ParseCsv", "Importer"));
    }

    [Fact]
    public void Plain_names_get_no_tags()
    {
        Assert.Empty(Tag("Add", "Calculator", new[] { "int" }, "int"));
    }

    [Fact]
    public void Referenced_callee_names_can_drive_a_tag()
    {
        // A blandly named method that calls SqlCommand should still read as io.
        Assert.Contains(DomainTag.Io, Tag("Run", "Worker", refs: new[] { "SqlCommand" }));
    }
}
