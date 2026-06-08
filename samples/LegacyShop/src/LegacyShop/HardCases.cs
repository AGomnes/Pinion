namespace LegacyShop;

/// <summary>Real-world C# shapes that a naive generator would emit broken tests for.</summary>
public class HardCases
{
    public async Task<int> CountUpAsync(int seed)
    {
        await Task.Yield();
        int n = 0;
        for (int i = 0; i < seed; i++) n += i;
        return n;
    }

    public async Task RunAsync(int n)
    {
        await Task.Yield();
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
    }

    public bool TryDouble(string text, out int value)
    {
        if (int.TryParse(text, out int parsed)) { value = parsed * 2; return true; }
        value = 0;
        return false;
    }

    public void Bump(ref int counter, int by) => counter += by;

    public T Echo<T>(T input) => input; // generic — deterministic generator should skip cleanly

    public int Combine(int a, int b) => a + b;            // overload 1
    public int Combine(int a) => a;                        // overload 2 (same DisplayName)
}
