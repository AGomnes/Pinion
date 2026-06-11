using Pinion.Engine.Analysis;
using Xunit;

namespace Pinion.Tests;

public class GeneratedCodeTests
{
    [Theory]
    [InlineData(@"C:\app\Form1.Designer.cs")]                 // WinForms designer
    [InlineData(@"C:\app\View.aspx.designer.vb")]             // WebForms designer
    [InlineData(@"obj\Debug\App.GlobalUsings.g.cs")]          // build-generated
    [InlineData(@"C:\app\My Project\Resources.Designer.vb")]  // VB My Project boilerplate
    [InlineData("/proj/My Project/Settings.Designer.vb")]
    public void Recognizes_generated_files(string path) => Assert.True(GeneratedCode.IsGenerated(path));

    [Theory]
    [InlineData(@"C:\app\DBHelper.vb")]
    [InlineData(@"C:\app\Controllers\HomeController.cs")]
    [InlineData("Details.aspx.vb")]   // code-behind is hand-written, not the generated designer
    [InlineData(null)]
    [InlineData("")]
    public void Leaves_hand_written_files(string? path) => Assert.False(GeneratedCode.IsGenerated(path));
}
