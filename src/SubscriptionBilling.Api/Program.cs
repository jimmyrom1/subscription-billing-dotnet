using Microsoft.EntityFrameworkCore;
using SubscriptionBilling.Api.Data;
using SubscriptionBilling.Api.Domain.Services;
using SubscriptionBilling.Api.Endpoints;
using SubscriptionBilling.Api.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Add services
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=billing.db";
builder.Services.AddDbContext<BillingDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddSingleton<IProrationEngine, ProrationEngine>();
builder.Services.AddScoped<IBillingService, BillingService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Subscription Billing Engine API",
        Version = "v1",
        Description = "Production-grade recurring billing engine in .NET 9 with exact proration, dunning state machine, and HTTP idempotency replay."
    });
});

var app = builder.Build();

// Auto-migrate database on start
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
    db.Database.EnsureCreated();
}

// Swagger enabled in all environments for API portfolio exploration
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Subscription Billing API v1");
    c.RoutePrefix = "swagger";
});

// Custom Idempotency Middleware
app.UseMiddleware<IdempotencyMiddleware>();

// Map endpoints
app.MapBillingEndpoints();

app.Run();

// Required for WebApplicationFactory<Program> in Integration Tests
public partial class Program { }
