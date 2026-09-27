using SubscriptionBilling.Api.Data;
using SubscriptionBilling.Api.Domain.Models;

namespace SubscriptionBilling.Api.Middleware;

public class IdempotencyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IServiceScopeFactory _scopeFactory;

    public IdempotencyMiddleware(RequestDelegate next, IServiceScopeFactory scopeFactory)
    {
        _next = next;
        _scopeFactory = scopeFactory;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only inspect requests with Idempotency-Key
        if (!context.Request.Headers.TryGetValue("Idempotency-Key", out var rawKey) ||
            string.IsNullOrWhiteSpace(rawKey))
        {
            await _next(context);
            return;
        }

        var key = rawKey.ToString().Trim();

        using (var readScope = _scopeFactory.CreateScope())
        {
            var db = readScope.ServiceProvider.GetRequiredService<BillingDbContext>();
            var existing = await db.IdempotencyRecords.FindAsync([key]);

            if (existing != null)
            {
                context.Response.StatusCode = existing.StatusCode;
                context.Response.ContentType = "application/json";
                context.Response.Headers["X-Idempotent-Replay"] = "true";
                await context.Response.WriteAsync(existing.ResponseBody);
                return;
            }
        }

        var originalBodyStream = context.Response.Body;
        using var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        try
        {
            await _next(context);

            memoryStream.Seek(0, SeekOrigin.Begin);
            var responseText = await new StreamReader(memoryStream).ReadToEndAsync();
            memoryStream.Seek(0, SeekOrigin.Begin);

            // Cache 2xx responses
            if (context.Response.StatusCode >= 200 && context.Response.StatusCode < 300)
            {
                try
                {
                    using var writeScope = _scopeFactory.CreateScope();
                    var writeDb = writeScope.ServiceProvider.GetRequiredService<BillingDbContext>();
                    var record = new IdempotencyRecord
                    {
                        Key = key,
                        RequestPath = context.Request.Path.Value ?? string.Empty,
                        StatusCode = context.Response.StatusCode,
                        ResponseBody = responseText,
                        CreatedAtUtc = DateTime.UtcNow
                    };
                    writeDb.IdempotencyRecords.Add(record);
                    await writeDb.SaveChangesAsync();
                }
                catch
                {
                    // Concurrency / duplicate key race condition handled gracefully
                }
            }

            await memoryStream.CopyToAsync(originalBodyStream);
        }
        finally
        {
            context.Response.Body = originalBodyStream;
        }
    }
}
