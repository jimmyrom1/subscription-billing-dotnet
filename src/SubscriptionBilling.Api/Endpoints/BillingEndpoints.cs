using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SubscriptionBilling.Api.Data;
using SubscriptionBilling.Api.Domain.Models;
using SubscriptionBilling.Api.Domain.Services;

namespace SubscriptionBilling.Api.Endpoints;

public record ProcessDueRequest(
    DateTime? AsOfDate = null,
    bool SimulatePaymentFailure = false
);

public record CancelRequest(
    bool Immediate = false
);

public record ProrationPreviewRequest(
    long CurrentPlanPriceCents,
    long NewPlanPriceCents,
    DateTime CurrentPeriodStart,
    DateTime CurrentPeriodEnd,
    DateTime? ChangeEffectiveAt = null
);

public static class BillingEndpoints
{
    public static void MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        // Health check
        app.MapGet("/healthz", () => Results.Ok(new
        {
            status = "healthy",
            service = "subscription-billing-dotnet",
            timestamp = DateTimeOffset.UtcNow
        }))
        .WithName("HealthCheck")
        .WithTags("System");

        var api = app.MapGroup("/api");

        // Plans
        api.MapGet("/plans", async (BillingDbContext db, CancellationToken ct) =>
        {
            var plans = await db.Plans.AsNoTracking().ToListAsync(ct);
            return Results.Ok(plans);
        })
        .WithName("GetPlans")
        .WithTags("Plans");

        // Subscriptions
        api.MapPost("/subscriptions", async (
            [FromBody] CreateSubscriptionRequest request,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            IBillingService billingService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId))
            {
                return Results.BadRequest(new { error = "CustomerId is required." });
            }

            if (string.IsNullOrWhiteSpace(request.CustomerEmail) || !request.CustomerEmail.Contains('@'))
            {
                return Results.BadRequest(new { error = "A valid CustomerEmail is required." });
            }

            if (string.IsNullOrWhiteSpace(request.PlanId))
            {
                return Results.BadRequest(new { error = "PlanId is required." });
            }

            try
            {
                var sub = await billingService.CreateSubscriptionAsync(request, idempotencyKey, ct);
                return Results.Created($"/api/subscriptions/{sub.Id}", sub);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("CreateSubscription")
        .WithTags("Subscriptions");

        api.MapGet("/subscriptions/{id}", async (string id, BillingDbContext db, CancellationToken ct) =>
        {
            var sub = await db.Subscriptions
                .Include(s => s.Plan)
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id, ct);

            if (sub == null)
            {
                return Results.NotFound(new { error = $"Subscription '{id}' not found." });
            }

            var invoices = await db.Invoices
                .Where(i => i.SubscriptionId == id)
                .OrderByDescending(i => i.CreatedAtUtc)
                .AsNoTracking()
                .ToListAsync(ct);

            return Results.Ok(new
            {
                subscription = sub,
                invoices
            });
        })
        .WithName("GetSubscriptionById")
        .WithTags("Subscriptions");

        api.MapPost("/subscriptions/{id}/change-plan", async (
            string id,
            [FromBody] ChangePlanRequest request,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            IBillingService billingService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.NewPlanId))
            {
                return Results.BadRequest(new { error = "NewPlanId is required." });
            }

            try
            {
                var response = await billingService.ChangePlanAsync(id, request, idempotencyKey, ct);
                return Results.Ok(response);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("ChangeSubscriptionPlan")
        .WithTags("Subscriptions");

        api.MapPost("/subscriptions/{id}/cancel", async (
            string id,
            [FromBody] CancelRequest? request,
            IBillingService billingService,
            CancellationToken ct) =>
        {
            try
            {
                var immediate = request?.Immediate ?? false;
                var sub = await billingService.CancelSubscriptionAsync(id, immediate, ct);
                return Results.Ok(sub);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("CancelSubscription")
        .WithTags("Subscriptions");

        // Invoices
        api.MapGet("/invoices", async (
            [FromQuery] string? subscriptionId,
            [FromQuery] string? customerId,
            BillingDbContext db,
            CancellationToken ct) =>
        {
            var query = db.Invoices.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(subscriptionId))
            {
                query = query.Where(i => i.SubscriptionId == subscriptionId);
            }

            if (!string.IsNullOrWhiteSpace(customerId))
            {
                query = query.Where(i => i.CustomerId == customerId);
            }

            var invoices = await query.OrderByDescending(i => i.CreatedAtUtc).ToListAsync(ct);
            return Results.Ok(invoices);
        })
        .WithName("GetInvoices")
        .WithTags("Invoices");

        // Batch recurring billing & dunning
        api.MapPost("/billing/process-due", async (
            [FromBody] ProcessDueRequest? request,
            IBillingService billingService,
            CancellationToken ct) =>
        {
            var result = await billingService.ProcessDueSubscriptionsAsync(
                request?.AsOfDate,
                request?.SimulatePaymentFailure ?? false,
                ct
            );
            return Results.Ok(result);
        })
        .WithName("ProcessDueSubscriptions")
        .WithTags("Billing");

        // Proration preview calculator
        api.MapPost("/proration/preview", (
            [FromBody] ProrationPreviewRequest request,
            IProrationEngine prorationEngine) =>
        {
            if (request.CurrentPeriodEnd <= request.CurrentPeriodStart)
            {
                return Results.BadRequest(new { error = "CurrentPeriodEnd must be greater than CurrentPeriodStart." });
            }

            var effectiveDate = request.ChangeEffectiveAt ?? DateTime.UtcNow;

            var proration = prorationEngine.Calculate(
                request.CurrentPlanPriceCents,
                request.NewPlanPriceCents,
                request.CurrentPeriodStart,
                request.CurrentPeriodEnd,
                effectiveDate
            );

            return Results.Ok(proration);
        })
        .WithName("PreviewProration")
        .WithTags("Proration");
    }
}
