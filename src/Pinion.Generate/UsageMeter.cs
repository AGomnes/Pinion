namespace Pinion.Generate;

/// <summary>Per-million-token rates (USD) for a model.</summary>
public sealed record ModelRate(decimal InputPerMTok, decimal OutputPerMTok);

/// <summary>Coarse, transparent pricing so the tool can estimate and cap spend locally.</summary>
public static class ModelPricing
{
    public static ModelRate For(string model)
    {
        string m = model.ToLowerInvariant();
        if (m.Contains("haiku")) return new ModelRate(1m, 5m);
        if (m.Contains("opus")) return new ModelRate(5m, 25m);
        return new ModelRate(3m, 15m); // sonnet / default
    }

    /// <summary>
    /// Estimated USD for one call. Cache writes bill ~1.25× input, cache reads ~0.1×; this is an
    /// estimate for budgeting, not an invoice.
    /// </summary>
    public static decimal CostUsd(string model, LlmUsage u)
    {
        ModelRate r = For(model);
        decimal billableInput = u.InputTokens
            + (u.CacheCreationInputTokens * 1.25m)
            + (u.CacheReadInputTokens * 0.1m);
        return (billableInput * r.InputPerMTok + u.OutputTokens * r.OutputPerMTok) / 1_000_000m;
    }
}

/// <summary>
/// Accumulates token usage + estimated cost across a whole run so the tool can show what was
/// spent and stop before a runaway. Shared across every target in a run.
/// </summary>
public sealed class UsageMeter
{
    private readonly object _lock = new();

    public int Calls { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long CacheReadTokens { get; private set; }
    public decimal EstimatedCostUsd { get; private set; }

    public void Add(string model, LlmUsage u)
    {
        lock (_lock)
        {
            Calls++;
            InputTokens += u.InputTokens;
            OutputTokens += u.OutputTokens;
            CacheReadTokens += u.CacheReadInputTokens;
            EstimatedCostUsd += ModelPricing.CostUsd(model, u);
        }
    }

    public string Summary() =>
        $"{Calls} API call(s), {InputTokens:N0} in / {OutputTokens:N0} out tokens " +
        $"({CacheReadTokens:N0} cache-read), estimated cost ~${EstimatedCostUsd:0.0000}";
}
