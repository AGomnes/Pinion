using Pinion.Engine.Analysis;
using Xunit;

namespace Pinion.Tests;

public class GeneratedCodeTests
{
    [Theory]
    [InlineData(@"C:\app\Form1.Designer.cs")]
    [InlineData(@"C:\app\View.aspx.designer.vb")]
    [InlineData(@"obj\Debug\App.GlobalUsings.g.cs")]
    [InlineData(@"C:\app\My Project\Resources.Designer.vb")]
    [InlineData("/proj/My Project/Settings.Designer.vb")]
    public void Recognizes_generated_files(string path) => Assert.True(GeneratedCode.IsGenerated(path));

    [Theory]
    [InlineData(@"C:\app\DBHelper.vb")]
    [InlineData(@"C:\app\Controllers\HomeController.cs")]
    [InlineData("Details.aspx.vb")]
    [InlineData(null)]
    [InlineData("")]
    public void Leaves_hand_written_files(string? path) => Assert.False(GeneratedCode.IsGenerated(path));
}
