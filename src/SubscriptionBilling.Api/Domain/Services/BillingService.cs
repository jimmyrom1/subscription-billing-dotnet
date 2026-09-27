using Microsoft.EntityFrameworkCore;
using SubscriptionBilling.Api.Data;
using SubscriptionBilling.Api.Domain.Models;

namespace SubscriptionBilling.Api.Domain.Services;

public record CreateSubscriptionRequest(
    string CustomerId,
    string CustomerEmail,
    string PlanId
);

public record ChangePlanRequest(
    string NewPlanId,
    bool Immediate = true
);

public record ChangePlanResponse(
    Subscription Subscription,
    ProrationResult Proration,
    Invoice? ProrationInvoice
);

public record ProcessDueResult(
    int SubscriptionsEvaluated,
    int RenewedCount,
    int FailedDunningCount,
    List<Invoice> GeneratedInvoices
);

public interface IBillingService
{
    Task<Subscription> CreateSubscriptionAsync(CreateSubscriptionRequest request, string? idempotencyKey = null, CancellationToken ct = default);
    Task<ChangePlanResponse> ChangePlanAsync(string subscriptionId, ChangePlanRequest request, string? idempotencyKey = null, CancellationToken ct = default);
    Task<Subscription> CancelSubscriptionAsync(string subscriptionId, bool immediate = false, CancellationToken ct = default);
    Task<ProcessDueResult> ProcessDueSubscriptionsAsync(DateTime? asOfDate = null, bool simulatePaymentFailure = false, CancellationToken ct = default);
}

public class BillingService : IBillingService
{
    private readonly BillingDbContext _db;
    private readonly IProrationEngine _prorationEngine;

    public BillingService(BillingDbContext db, IProrationEngine prorationEngine)
    {
        _db = db;
        _prorationEngine = prorationEngine;
    }

    public async Task<Subscription> CreateSubscriptionAsync(CreateSubscriptionRequest request, string? idempotencyKey = null, CancellationToken ct = default)
    {
        var plan = await _db.Plans.FindAsync([request.PlanId], ct)
            ?? throw new InvalidOperationException($"Plan '{request.PlanId}' not found.");

        var now = DateTime.UtcNow;
        var periodEnd = plan.Interval == BillingInterval.Yearly ? now.AddYears(1) : now.AddMonths(1);

        var subscription = new Subscription
        {
            CustomerId = request.CustomerId,
            CustomerEmail = request.CustomerEmail,
            PlanId = plan.Id,
            Status = SubscriptionStatus.Active,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = periodEnd,
            CancelAtPeriodEnd = false,
            FailedPaymentAttempts = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        var initialInvoice = new Invoice
        {
            SubscriptionId = subscription.Id,
            CustomerId = subscription.CustomerId,
            AmountCents = plan.PriceCents,
            Currency = plan.Currency,
            Status = InvoiceStatus.Paid,
            DueDateUtc = now,
            PaidAtUtc = now,
            Description = $"Initial subscription charge for {plan.Name}",
            IdempotencyKey = idempotencyKey,
            CreatedAtUtc = now
        };

        _db.Subscriptions.Add(subscription);
        _db.Invoices.Add(initialInvoice);
        await _db.SaveChangesAsync(ct);

        subscription.Plan = plan;
        return subscription;
    }

    public async Task<ChangePlanResponse> ChangePlanAsync(string subscriptionId, ChangePlanRequest request, string? idempotencyKey = null, CancellationToken ct = default)
    {
        var subscription = await _db.Subscriptions
            .Include(s => s.Plan)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, ct)
            ?? throw new KeyNotFoundException($"Subscription '{subscriptionId}' not found.");

        if (subscription.Status != SubscriptionStatus.Active)
        {
            throw new InvalidOperationException($"Cannot change plan for subscription with status '{subscription.Status}'.");
        }

        var newPlan = await _db.Plans.FindAsync([request.NewPlanId], ct)
            ?? throw new InvalidOperationException($"New plan '{request.NewPlanId}' not found.");

        if (subscription.PlanId == newPlan.Id)
        {
            throw new InvalidOperationException("Subscription is already on the requested plan.");
        }

        var now = DateTime.UtcNow;
        var currentPlan = subscription.Plan!;

        var proration = _prorationEngine.Calculate(
            currentPlan.PriceCents,
            newPlan.PriceCents,
            subscription.CurrentPeriodStart,
            subscription.CurrentPeriodEnd,
            now
        );

        Invoice? invoice = null;

        if (proration.NetAmountDueCents > 0)
        {
            invoice = new Invoice
            {
                SubscriptionId = subscription.Id,
                CustomerId = subscription.CustomerId,
                AmountCents = proration.NetAmountDueCents,
                Currency = newPlan.Currency,
                Status = InvoiceStatus.Paid,
                DueDateUtc = now,
                PaidAtUtc = now,
                Description = $"Proration upgrade from {currentPlan.Name} to {newPlan.Name} ({proration.RemainingSeconds:F0}s remaining)",
                IdempotencyKey = idempotencyKey,
                CreatedAtUtc = now
            };
            _db.Invoices.Add(invoice);
        }

        subscription.PlanId = newPlan.Id;
        subscription.Plan = newPlan;
        subscription.UpdatedAtUtc = now;

        await _db.SaveChangesAsync(ct);

        return new ChangePlanResponse(subscription, proration, invoice);
    }

    public async Task<Subscription> CancelSubscriptionAsync(string subscriptionId, bool immediate = false, CancellationToken ct = default)
    {
        var subscription = await _db.Subscriptions.FindAsync([subscriptionId], ct)
            ?? throw new KeyNotFoundException($"Subscription '{subscriptionId}' not found.");

        var now = DateTime.UtcNow;
        if (immediate)
        {
            subscription.Status = SubscriptionStatus.Canceled;
        }
        else
        {
            subscription.CancelAtPeriodEnd = true;
        }

        subscription.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct);
        return subscription;
    }

    public async Task<ProcessDueResult> ProcessDueSubscriptionsAsync(DateTime? asOfDate = null, bool simulatePaymentFailure = false, CancellationToken ct = default)
    {
        var refTime = asOfDate ?? DateTime.UtcNow;

        var dueSubscriptions = await _db.Subscriptions
            .Include(s => s.Plan)
            .Where(s => s.Status == SubscriptionStatus.Active && s.CurrentPeriodEnd <= refTime)
            .ToListAsync(ct);

        int renewed = 0;
        int failedDunning = 0;
        var invoices = new List<Invoice>();

        foreach (var sub in dueSubscriptions)
        {
            if (sub.CancelAtPeriodEnd)
            {
                sub.Status = SubscriptionStatus.Canceled;
                sub.UpdatedAtUtc = refTime;
                continue;
            }

            if (simulatePaymentFailure)
            {
                sub.FailedPaymentAttempts++;
                sub.UpdatedAtUtc = refTime;

                var failedInvoice = new Invoice
                {
                    SubscriptionId = sub.Id,
                    CustomerId = sub.CustomerId,
                    AmountCents = sub.Plan!.PriceCents,
                    Currency = sub.Plan.Currency,
                    Status = InvoiceStatus.Failed,
                    DueDateUtc = refTime,
                    PaidAtUtc = null,
                    Description = $"Renewal charge attempt #{sub.FailedPaymentAttempts} failed for {sub.Plan.Name}",
                    CreatedAtUtc = refTime
                };

                if (sub.FailedPaymentAttempts >= 3)
                {
                    sub.Status = SubscriptionStatus.PastDue;
                    failedDunning++;
                }

                _db.Invoices.Add(failedInvoice);
                invoices.Add(failedInvoice);
            }
            else
            {
                // Advance billing period
                sub.CurrentPeriodStart = sub.CurrentPeriodEnd;
                sub.CurrentPeriodEnd = sub.Plan!.Interval == BillingInterval.Yearly
                    ? sub.CurrentPeriodStart.AddYears(1)
                    : sub.CurrentPeriodStart.AddMonths(1);

                sub.FailedPaymentAttempts = 0;
                sub.UpdatedAtUtc = refTime;

                var paidInvoice = new Invoice
                {
                    SubscriptionId = sub.Id,
                    CustomerId = sub.CustomerId,
                    AmountCents = sub.Plan.PriceCents,
                    Currency = sub.Plan.Currency,
                    Status = InvoiceStatus.Paid,
                    DueDateUtc = refTime,
                    PaidAtUtc = refTime,
                    Description = $"Recurring renewal for {sub.Plan.Name}",
                    CreatedAtUtc = refTime
                };

                _db.Invoices.Add(paidInvoice);
                invoices.Add(paidInvoice);
                renewed++;
            }
        }

        await _db.SaveChangesAsync(ct);

        return new ProcessDueResult(
            SubscriptionsEvaluated: dueSubscriptions.Count,
            RenewedCount: renewed,
            FailedDunningCount: failedDunning,
            GeneratedInvoices: invoices
        );
    }
}
