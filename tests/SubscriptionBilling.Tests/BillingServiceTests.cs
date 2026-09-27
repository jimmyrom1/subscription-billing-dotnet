using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SubscriptionBilling.Api.Data;
using SubscriptionBilling.Api.Domain.Models;
using SubscriptionBilling.Api.Domain.Services;
using Xunit;

namespace SubscriptionBilling.Tests;

public class BillingServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BillingDbContext _db;
    private readonly BillingService _service;

    public BillingServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<BillingDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new BillingDbContext(options);
        _db.Database.EnsureCreated();

        var prorationEngine = new ProrationEngine();
        _service = new BillingService(_db, prorationEngine);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CreateSubscriptionAsync_ValidRequest_CreatesSubscriptionAndInitialInvoice()
    {
        var request = new CreateSubscriptionRequest(
            CustomerId: "cust_123",
            CustomerEmail: "finance@corp.com",
            PlanId: "plan_starter"
        );

        var sub = await _service.CreateSubscriptionAsync(request, "idem_sub_1");

        sub.Should().NotBeNull();
        sub.Status.Should().Be(SubscriptionStatus.Active);
        sub.CustomerId.Should().Be("cust_123");
        sub.PlanId.Should().Be("plan_starter");
        sub.FailedPaymentAttempts.Should().Be(0);
        sub.CancelAtPeriodEnd.Should().BeFalse();

        // Verify initial paid invoice
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.SubscriptionId == sub.Id);
        invoice.Should().NotBeNull();
        invoice!.AmountCents.Should().Be(1500); // Starter price
        invoice.Status.Should().Be(InvoiceStatus.Paid);
        invoice.IdempotencyKey.Should().Be("idem_sub_1");
    }

    [Fact]
    public async Task ChangePlanAsync_ValidUpgrade_CreatesProrationInvoiceAndUpdatesPlan()
    {
        var createReq = new CreateSubscriptionRequest("cust_upgrade", "upgrade@corp.com", "plan_starter");
        var sub = await _service.CreateSubscriptionAsync(createReq);

        var changeReq = new ChangePlanRequest("plan_pro");
        var result = await _service.ChangePlanAsync(sub.Id, changeReq, "idem_upgrade_1");

        result.Subscription.PlanId.Should().Be("plan_pro");
        result.Proration.NetAmountDueCents.Should().BeGreaterThan(0);
        result.ProrationInvoice.Should().NotBeNull();
        result.ProrationInvoice!.AmountCents.Should().Be(result.Proration.NetAmountDueCents);
        result.ProrationInvoice.Status.Should().Be(InvoiceStatus.Paid);
    }

    [Fact]
    public async Task ChangePlanAsync_SamePlan_ThrowsInvalidOperationException()
    {
        var sub = await _service.CreateSubscriptionAsync(new("cust_same", "same@corp.com", "plan_starter"));

        var act = async () => await _service.ChangePlanAsync(sub.Id, new ChangePlanRequest("plan_starter"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already on the requested plan*");
    }

    [Fact]
    public async Task ChangePlanAsync_CanceledSubscription_ThrowsInvalidOperationException()
    {
        var sub = await _service.CreateSubscriptionAsync(new("cust_canceled", "canc@corp.com", "plan_starter"));
        await _service.CancelSubscriptionAsync(sub.Id, immediate: true);

        var act = async () => await _service.ChangePlanAsync(sub.Id, new ChangePlanRequest("plan_pro"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cannot change plan for subscription with status 'Canceled'*");
    }

    [Fact]
    public async Task CancelSubscriptionAsync_CancelAtPeriodEnd_MarksFlagPreservingStatus()
    {
        var sub = await _service.CreateSubscriptionAsync(new("cust_cancel_end", "cancel@corp.com", "plan_starter"));

        var canceled = await _service.CancelSubscriptionAsync(sub.Id, immediate: false);

        canceled.Status.Should().Be(SubscriptionStatus.Active);
        canceled.CancelAtPeriodEnd.Should().BeTrue();
    }

    [Fact]
    public async Task CancelSubscriptionAsync_Immediate_TransitionsToCanceled()
    {
        var sub = await _service.CreateSubscriptionAsync(new("cust_cancel_imm", "imm@corp.com", "plan_starter"));

        var canceled = await _service.CancelSubscriptionAsync(sub.Id, immediate: true);

        canceled.Status.Should().Be(SubscriptionStatus.Canceled);
    }

    [Fact]
    public async Task ProcessDueSubscriptionsAsync_NormalRenewal_AdvancesPeriodAndCreatesInvoice()
    {
        var sub = await _service.CreateSubscriptionAsync(new("cust_renew", "renew@corp.com", "plan_starter"));

        var expectedNewStart = sub.CurrentPeriodEnd;
        var futureDate = expectedNewStart.AddMinutes(5);

        var result = await _service.ProcessDueSubscriptionsAsync(asOfDate: futureDate, simulatePaymentFailure: false);

        result.SubscriptionsEvaluated.Should().Be(1);
        result.RenewedCount.Should().Be(1);
        result.FailedDunningCount.Should().Be(0);
        result.GeneratedInvoices.Should().HaveCount(1);
        result.GeneratedInvoices[0].Status.Should().Be(InvoiceStatus.Paid);

        var updatedSub = await _db.Subscriptions.FindAsync(sub.Id);
        updatedSub!.CurrentPeriodStart.Should().Be(expectedNewStart);
        updatedSub.CurrentPeriodEnd.Should().BeAfter(futureDate);
    }

    [Fact]
    public async Task ProcessDueSubscriptionsAsync_WithCancelAtPeriodEnd_TransitionsToCanceledWithoutInvoice()
    {
        var sub = await _service.CreateSubscriptionAsync(new("cust_cancel_due", "due@corp.com", "plan_starter"));
        await _service.CancelSubscriptionAsync(sub.Id, immediate: false);

        var futureDate = sub.CurrentPeriodEnd.AddMinutes(5);
        var result = await _service.ProcessDueSubscriptionsAsync(asOfDate: futureDate, simulatePaymentFailure: false);

        result.SubscriptionsEvaluated.Should().Be(1);
        result.RenewedCount.Should().Be(0);
        result.GeneratedInvoices.Should().BeEmpty();

        var updatedSub = await _db.Subscriptions.FindAsync(sub.Id);
        updatedSub!.Status.Should().Be(SubscriptionStatus.Canceled);
    }

    [Fact]
    public async Task ProcessDueSubscriptionsAsync_DunningRetries_TransitionsToPastDueAfterThreeStrikes()
    {
        var sub = await _service.CreateSubscriptionAsync(new("cust_dunning", "dunning@corp.com", "plan_starter"));
        var futureDate = sub.CurrentPeriodEnd.AddMinutes(5);

        // Strike 1
        var res1 = await _service.ProcessDueSubscriptionsAsync(asOfDate: futureDate, simulatePaymentFailure: true);
        res1.FailedDunningCount.Should().Be(0);
        var subAfter1 = await _db.Subscriptions.FindAsync(sub.Id);
        subAfter1!.Status.Should().Be(SubscriptionStatus.Active);
        subAfter1.FailedPaymentAttempts.Should().Be(1);

        // Strike 2
        var res2 = await _service.ProcessDueSubscriptionsAsync(asOfDate: futureDate, simulatePaymentFailure: true);
        res2.FailedDunningCount.Should().Be(0);
        var subAfter2 = await _db.Subscriptions.FindAsync(sub.Id);
        subAfter2!.Status.Should().Be(SubscriptionStatus.Active);
        subAfter2.FailedPaymentAttempts.Should().Be(2);

        // Strike 3 (Final dunning failure)
        var res3 = await _service.ProcessDueSubscriptionsAsync(asOfDate: futureDate, simulatePaymentFailure: true);
        res3.FailedDunningCount.Should().Be(1);
        var subAfter3 = await _db.Subscriptions.FindAsync(sub.Id);
        subAfter3!.Status.Should().Be(SubscriptionStatus.PastDue);
        subAfter3.FailedPaymentAttempts.Should().Be(3);
    }
}
