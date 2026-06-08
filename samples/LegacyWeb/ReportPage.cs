using System.Web.UI; // WebForms — does not exist in .NET Core/8

namespace LegacyWeb;

public class ReportPage : Page
{
    protected void Page_Load(object sender, EventArgs e)
    {
        int rows = 0;
        for (int i = 0; i < 10; i++)
        {
            if (i % 2 == 0) rows++;
        }
        Response.Write($"rows: {rows}");
    }
}
