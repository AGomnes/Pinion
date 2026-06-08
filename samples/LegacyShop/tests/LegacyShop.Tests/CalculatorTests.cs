using LegacyShop;
using Xunit;

namespace LegacyShop.Tests;

public class CalculatorTests
{
    [Fact]
    public void Add_sums_two_numbers()
    {
        var calc = new Calculator();
        Assert.Equal(5, calc.Add(2, 3));
    }

    [Fact]
    public void Subtract_returns_difference()
    {
        var calc = new Calculator();
        Assert.Equal(1, calc.Subtract(3, 2));
    }
}
