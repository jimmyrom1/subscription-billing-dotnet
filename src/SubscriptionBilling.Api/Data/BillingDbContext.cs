using Microsoft.EntityFrameworkCore;
using SubscriptionBilling.Api.Domain.Models;

namespace SubscriptionBilling.Api.Data;

public class BillingDbContext : DbContext
{
    public BillingDbContext(DbContextOptions<BillingDbContext> options) : base(options) { }

    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Plan>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Name).IsRequired().HasMaxLength(100);
            entity.Property(p => p.Currency).IsRequired().HasMaxLength(3);

            // Seed default corporate tiers
            entity.HasData(
                new Plan { Id = "plan_starter", Name = "Starter Tier", PriceCents = 1500, Interval = BillingInterval.Monthly, Currency = "EUR" },
                new Plan { Id = "plan_pro", Name = "Professional Tier", PriceCents = 4900, Interval = BillingInterval.Monthly, Currency = "EUR" },
                new Plan { Id = "plan_enterprise", Name = "Enterprise Tier", PriceCents = 19900, Interval = BillingInterval.Monthly, Currency = "EUR" }
            );
        });

        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.CustomerId).IsRequired().HasMaxLength(100);
            entity.Property(s => s.CustomerEmail).IsRequired().HasMaxLength(200);
            entity.HasOne(s => s.Plan).WithMany().HasForeignKey(s => s.PlanId);
            entity.HasIndex(s => s.CustomerId);
            entity.HasIndex(s => s.Status);
        });

        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.HasKey(i => i.Id);
            entity.Property(i => i.SubscriptionId).IsRequired().HasMaxLength(100);
            entity.Property(i => i.CustomerId).IsRequired().HasMaxLength(100);
            entity.HasIndex(i => i.SubscriptionId);
            entity.HasIndex(i => i.CustomerId);
            entity.HasIndex(i => i.IdempotencyKey);
        });

        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.HasKey(r => r.Key);
            entity.Property(r => r.RequestPath).IsRequired().HasMaxLength(200);
        });
    }
}
