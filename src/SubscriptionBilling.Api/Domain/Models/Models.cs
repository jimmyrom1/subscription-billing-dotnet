namespace SubscriptionBilling.Api.Domain.Models;

public enum SubscriptionStatus
{
    Trialing = 1,
    Active = 2,
    PastDue = 3,
    Canceled = 4
}

public enum BillingInterval
{
    Monthly = 1,
    Yearly = 2
}

public enum InvoiceStatus
{
    Draft = 1,
    Paid = 2,
    Failed = 3
}

public class Plan
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long PriceCents { get; set; }
    public BillingInterval Interval { get; set; } = BillingInterval.Monthly;
    public string Currency { get; set; } = "EUR";
}

public class Subscription
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string CustomerId { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public Plan? Plan { get; set; }
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;
    public DateTime CurrentPeriodStart { get; set; }
    public DateTime CurrentPeriodEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public int FailedPaymentAttempts { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class Invoice
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SubscriptionId { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public long AmountCents { get; set; }
    public string Currency { get; set; } = "EUR";
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Paid;
    public DateTime DueDateUtc { get; set; }
    public DateTime? PaidAtUtc { get; set; }
    public string? Description { get; set; }
    public string? IdempotencyKey { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class IdempotencyRecord
{
    public string Key { get; set; } = string.Empty;
    public string RequestPath { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string ResponseBody { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
