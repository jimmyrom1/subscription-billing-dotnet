using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubscriptionBilling.Api.Data;
using SubscriptionBilling.Api.Domain.Models;
using SubscriptionBilling.Api.Domain.Services;
using SubscriptionBilling.Api.Endpoints;
using Xunit;

namespace SubscriptionBilling.Tests;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private SqliteConnection? _connection;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<BillingDbContext>));
            if (descriptor != null)
            {
                services.Remove(descriptor);
            }

            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();

            services.AddDbContext<BillingDbContext>(options =>
            {
                options.UseSqlite(_connection);
            });

            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            db.Database.EnsureCreated();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection?.Dispose();
    }
}

public class IntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public IntegrationTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetHealth_ReturnsOkWithStatus()
    {
        var response = await _client.GetAsync("/healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("healthy");
        content.Should().Contain("subscription-billing-dotnet");
    }

    [Fact]
    public async Task GetPlans_ReturnsPreconfiguredTiers()
    {
        var response = await _client.GetAsync("/api/plans");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var plans = await response.Content.ReadFromJsonAsync<List<Plan>>(_jsonOptions);
        plans.Should().NotBeNull();
        plans!.Should().Contain(p => p.Id == "plan_starter" && p.PriceCents == 1500);
        plans.Should().Contain(p => p.Id == "plan_pro" && p.PriceCents == 4900);
        plans.Should().Contain(p => p.Id == "plan_enterprise" && p.PriceCents == 19900);
    }

    [Fact]
    public async Task CreateSubscription_WithInvalidInput_ReturnsBadRequest()
    {
        var request = new CreateSubscriptionRequest("", "invalid-email", "");
        var response = await _client.PostAsJsonAsync("/api/subscriptions", request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateSubscription_AndReplayWithSameIdempotencyKey_ReturnsCachedReplay()
    {
        var idempotencyKey = $"test-idem-{Guid.NewGuid():N}";
        var request = new CreateSubscriptionRequest("cust_corp_42", "treasury@corp.com", "plan_starter");

        var message1 = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions")
        {
            Content = JsonContent.Create(request)
        };
        message1.Headers.Add("Idempotency-Key", idempotencyKey);

        var response1 = await _client.SendAsync(message1);
        response1.StatusCode.Should().Be(HttpStatusCode.Created);
        response1.Headers.Contains("X-Idempotent-Replay").Should().BeFalse();
        var body1 = await response1.Content.ReadAsStringAsync();

        // Exact replay with same idempotency key
        var message2 = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions")
        {
            Content = JsonContent.Create(request)
        };
        message2.Headers.Add("Idempotency-Key", idempotencyKey);

        var response2 = await _client.SendAsync(message2);
        response2.StatusCode.Should().Be(HttpStatusCode.Created);
        response2.Headers.Contains("X-Idempotent-Replay").Should().BeTrue();
        var replayHeader = response2.Headers.GetValues("X-Idempotent-Replay").First();
        replayHeader.Should().Be("true");

        var body2 = await response2.Content.ReadAsStringAsync();
        body2.Should().Be(body1);
    }

    [Fact]
    public async Task SubscriptionLifecycle_Create_Upgrade_Get_Cancel()
    {
        // 1. Create subscription
        var createReq = new CreateSubscriptionRequest("cust_lifecycle", "owner@company.io", "plan_starter");
        var createRes = await _client.PostAsJsonAsync("/api/subscriptions", createReq);
        createRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var sub = await createRes.Content.ReadFromJsonAsync<Subscription>(_jsonOptions);
        sub.Should().NotBeNull();

        // 2. Change plan to Pro
        var changeReq = new ChangePlanRequest("plan_pro");
        var changeRes = await _client.PostAsJsonAsync($"/api/subscriptions/{sub!.Id}/change-plan", changeReq);
        changeRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var changeBody = await changeRes.Content.ReadFromJsonAsync<ChangePlanResponse>(_jsonOptions);
        changeBody.Should().NotBeNull();
        changeBody!.Subscription.PlanId.Should().Be("plan_pro");
        changeBody.Proration.NetAmountDueCents.Should().BeGreaterThan(0);

        // 3. Get subscription details with invoices
        var getRes = await _client.GetAsync($"/api/subscriptions/{sub.Id}");
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var getBody = await getRes.Content.ReadAsStringAsync();
        getBody.Should().Contain("plan_pro");
        getBody.Should().Contain("invoices");

        // 4. Cancel subscription
        var cancelRes = await _client.PostAsJsonAsync($"/api/subscriptions/{sub.Id}/cancel", new CancelRequest(Immediate: true));
        cancelRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var canceledSub = await cancelRes.Content.ReadFromJsonAsync<Subscription>(_jsonOptions);
        canceledSub!.Status.Should().Be(SubscriptionStatus.Canceled);
    }

    [Fact]
    public async Task PreviewProration_ValidParameters_ReturnsCalculatedPreview()
    {
        var start = DateTime.UtcNow.AddDays(-15);
        var end = DateTime.UtcNow.AddDays(15);
        var effective = DateTime.UtcNow;

        var req = new ProrationPreviewRequest(
            CurrentPlanPriceCents: 1500,
            NewPlanPriceCents: 4900,
            CurrentPeriodStart: start,
            CurrentPeriodEnd: end,
            ChangeEffectiveAt: effective
        );

        var res = await _client.PostAsJsonAsync("/api/proration/preview", req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var preview = await res.Content.ReadFromJsonAsync<ProrationResult>(_jsonOptions);
        preview.Should().NotBeNull();
        preview!.FractionRemaining.Should().BeApproximately(0.5m, 0.05m);
        preview.NetAmountDueCents.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ProcessDueBilling_ExecutesWithoutError()
    {
        var req = new ProcessDueRequest(
            AsOfDate: DateTime.UtcNow.AddDays(40),
            SimulatePaymentFailure: false
        );

        var res = await _client.PostAsJsonAsync("/api/billing/process-due", req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await res.Content.ReadFromJsonAsync<ProcessDueResult>(_jsonOptions);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task PreviewProration_InvalidDates_ReturnsBadRequest()
    {
        var now = DateTime.UtcNow;
        var req = new ProrationPreviewRequest(
            CurrentPlanPriceCents: 1500,
            NewPlanPriceCents: 4900,
            CurrentPeriodStart: now.AddDays(10),
            CurrentPeriodEnd: now, // Invalid: end before start
            ChangeEffectiveAt: now
        );

        var res = await _client.PostAsJsonAsync("/api/proration/preview", req);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetSubscription_NonExistent_ReturnsNotFound()
    {
        var res = await _client.GetAsync($"/api/subscriptions/non-existent-{Guid.NewGuid():N}");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ChangePlan_NonExistentSubscription_ReturnsNotFound()
    {
        var res = await _client.PostAsJsonAsync($"/api/subscriptions/non-existent-{Guid.NewGuid():N}/change-plan", new ChangePlanRequest("plan_pro"));
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetInvoices_CanFilterByCustomerId()
    {
        var customerId = $"cust_invoices_{Guid.NewGuid():N}";
        var createReq = new CreateSubscriptionRequest(customerId, "invoices@corp.com", "plan_starter");
        await _client.PostAsJsonAsync("/api/subscriptions", createReq);

        var res = await _client.GetAsync($"/api/invoices?customerId={customerId}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var invoices = await res.Content.ReadFromJsonAsync<List<Invoice>>(_jsonOptions);
        invoices.Should().NotBeNull();
        invoices!.Should().HaveCount(1);
        invoices[0].CustomerId.Should().Be(customerId);
    }
}
