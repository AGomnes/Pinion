using System.ServiceModel; // WCF — a top migration landmine (no .NET Core equivalent)

namespace LegacyWeb;

[ServiceContract]
public interface IBillingService
{
    [OperationContract]
    decimal GetOutstandingBalance(int customerId);
}

public class BillingService : IBillingService
{
    public decimal GetOutstandingBalance(int customerId)
    {
        if (customerId <= 0) return 0m;
        decimal balance = customerId * 12.5m;
        return balance > 0 ? balance : 0m;
    }
}
