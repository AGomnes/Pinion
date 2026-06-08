namespace LegacyShop;

/// <summary>Orchestrates the risky services — so they each pick up callers (blast radius).</summary>
public class CheckoutService
{
    private readonly InvoiceService _invoices = new();
    private readonly PriceEngine _pricing = new();
    private readonly AuthHandler _auth = new();

    public decimal Checkout(IReadOnlyList<CartLine> lines, string region, string? coupon, string token, bool isMember)
    {
        if (!_auth.ValidateToken(token))
            throw new InvalidOperationException("bad token");

        decimal subtotal = _pricing.ApplyDiscounts(lines, coupon, isMember);
        decimal vat = _invoices.CalculateVat(subtotal, region, isExempt: false);
        return subtotal + vat;
    }
}
