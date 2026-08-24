using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pinion.Engine.Analysis;

namespace Pinion.Engine.Reporting;

/// <summary>
/// Renders the machine-readable report. This is the contract that drives the
/// `generate` step and any future web UI, so it includes the full per-unit IR and
/// the itemized score breakdown — nothing is hidden.
/// </summary>
public static class JsonReportRenderer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Render(AnalysisReport report) => JsonSerializer.Serialize(report, Options);
}
