using FluentAssertions;
using SubscriptionBilling.Api.Domain.Services;
using Xunit;

namespace SubscriptionBilling.Tests;

public class ProrationEngineTests
{
    private readonly ProrationEngine _engine = new();

    [Fact]
    public void Calculate_MidCycleUpgrade_CalculatesAccurateNetDue()
    {
        // 30 days total period (June 1 to July 1)
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        // Change effective exactly at midpoint: June 16, 00:00 (15 days remaining out of 30)
        var effective = new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc);

        // Starter: 15.00 EUR (1500 cents) -> Pro: 49.00 EUR (4900 cents)
        var result = _engine.Calculate(
            currentPlanPriceCents: 1500,
            newPlanPriceCents: 4900,
            currentPeriodStart: start,
            currentPeriodEnd: end,
            changeEffectiveAt: effective
        );

        // 15 days out of 30 days = 0.50 fraction
        result.FractionRemaining.Should().Be(0.5m);
        // Unused credit: 1500 * 0.5 = 750 cents
        result.UnusedCreditCents.Should().Be(750);
        // New plan charge: 4900 * 0.5 = 2450 cents
        result.NewPlanChargeCents.Should().Be(2450);
        // Net due: 2450 - 750 = 1700 cents (17.00 EUR)
        result.NetAmountDueCents.Should().Be(1700);
    }

    [Fact]
    public void Calculate_MidCycleDowngrade_GeneratesCredit()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var effective = new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc);

        // Enterprise: 199.00 EUR (19900 cents) -> Pro: 49.00 EUR (4900 cents)
        var result = _engine.Calculate(
            currentPlanPriceCents: 19900,
            newPlanPriceCents: 4900,
            currentPeriodStart: start,
            currentPeriodEnd: end,
            changeEffectiveAt: effective
        );

        result.FractionRemaining.Should().Be(0.5m);
        result.UnusedCreditCents.Should().Be(9950);
        result.NewPlanChargeCents.Should().Be(2450);
        // Net due is negative: 2450 - 9950 = -7500 cents (75.00 EUR credit)
        result.NetAmountDueCents.Should().Be(-7500);
    }

    [Fact]
    public void Calculate_ChangeAtStartOfPeriod_FullCreditAndFullCharge()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var effective = start;

        var result = _engine.Calculate(1500, 4900, start, end, effective);

        result.FractionRemaining.Should().Be(1.0m);
        result.UnusedCreditCents.Should().Be(1500);
        result.NewPlanChargeCents.Should().Be(4900);
        result.NetAmountDueCents.Should().Be(3400);
    }

    [Fact]
    public void Calculate_ChangeAtEndOfPeriod_ZeroCreditAndZeroCharge()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var effective = end;

        var result = _engine.Calculate(1500, 4900, start, end, effective);

        result.FractionRemaining.Should().Be(0m);
        result.UnusedCreditCents.Should().Be(0);
        result.NewPlanChargeCents.Should().Be(0);
        result.NetAmountDueCents.Should().Be(0);
    }

    [Fact]
    public void Calculate_EffectiveDatePastEnd_ClampsToZero()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var effective = end.AddDays(5);

        var result = _engine.Calculate(1500, 4900, start, end, effective);

        result.FractionRemaining.Should().Be(0m);
        result.NetAmountDueCents.Should().Be(0);
    }

    [Fact]
    public void Calculate_InvalidPeriodRange_ThrowsArgumentException()
    {
        var start = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var act = () => _engine.Calculate(1500, 4900, start, end, DateTime.UtcNow);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*must be strictly after period start*");
    }

    [Fact]
    public void Calculate_SubCentFractionRounding_UsesAwayFromZero()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddDays(3); // 3 days
        var effective = start.AddDays(1); // 2 days remaining (2/3 = 0.666667)

        // 100 cents * 2/3 = 66.6666... cents -> rounds to 67 cents
        var result = _engine.Calculate(100, 200, start, end, effective);

        result.UnusedCreditCents.Should().Be(67);
        // 200 * 2/3 = 133.3333... cents -> rounds to 133 cents
        result.NewPlanChargeCents.Should().Be(133);
        // Net due: 133 - 67 = 66 cents
        result.NetAmountDueCents.Should().Be(66);
    }
}
