namespace Pinion.Generate;

/// <summary>
/// Wraps another provider and deliberately corrupts its first N responses with a guaranteed
/// compile error. Used to exercise the compile→run→repair loop end-to-end (the path a real
/// model hits whenever it emits code that doesn't build) without spending API tokens.
/// </summary>
public sealed class FaultInjectingLlmClient : ILlmClient
{
    private readonly ILlmClient _inner;
    private readonly int _failFirst;
    private int _calls;

    public FaultInjectingLlmClient(ILlmClient inner, int failFirst = 1)
    {
        _inner = inner;
        _failFirst = failFirst;
    }

    public string Name => $"{_inner.Name}+fault";

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        int call = Interlocked.Increment(ref _calls);
        var response = await _inner.CompleteAsync(request, ct).ConfigureAwait(false);

        if (call <= _failFirst)
        {
            string broken = response.Text + "\n\nclass __PinionInjectedFault { int broken = ; }\n";
            return response with { Text = broken };
        }

        return response;
    }
}
