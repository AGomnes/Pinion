using Pinion.Engine.Reporting;
using Xunit;

namespace Pinion.Tests;

public class CharacterizationFormatTests
{
    [Fact]
    public void Stamp_round_trips_to_the_current_version()
    {
        Assert.Equal(CharacterizationFormat.Version, CharacterizationFormat.ReadStamp(CharacterizationFormat.Stamp));
    }

    [Fact]
    public void Reads_the_stamp_from_a_generated_test_header()
    {
        string source =
            "// pinion-format: 3\n" +
            "#nullable enable annotations\n" +
            "using Xunit;\n" +
            "public class Foo_CharacterizationTests { }\n";

        Assert.Equal(3, CharacterizationFormat.ReadStamp(source));
    }

    [Fact]
    public void Unstamped_source_reads_as_null()
    {
        Assert.Null(CharacterizationFormat.ReadStamp("#nullable enable annotations\nusing Xunit;\n"));
    }
}
