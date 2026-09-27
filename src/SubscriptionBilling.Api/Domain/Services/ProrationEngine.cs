namespace SubscriptionBilling.Api.Domain.Services;

public record ProrationResult(
    long UnusedCreditCents,
    long NewPlanChargeCents,
    long NetAmountDueCents,
    double RemainingSeconds,
    double TotalPeriodSeconds,
    decimal FractionRemaining
);

public interface IProrationEngine
{
    ProrationResult Calculate(
        long currentPlanPriceCents,
        long newPlanPriceCents,
        DateTime currentPeriodStart,
        DateTime currentPeriodEnd,
        DateTime changeEffectiveAt);
}

public class ProrationEngine : IProrationEngine
{
    public ProrationResult Calculate(
        long currentPlanPriceCents,
        long newPlanPriceCents,
        DateTime currentPeriodStart,
        DateTime currentPeriodEnd,
        DateTime changeEffectiveAt)
    {
        if (currentPeriodEnd <= currentPeriodStart)
        {
            throw new ArgumentException("Current period end must be strictly after period start.", nameof(currentPeriodEnd));
        }

        var totalSeconds = (currentPeriodEnd - currentPeriodStart).TotalSeconds;
        var remainingSeconds = (currentPeriodEnd - changeEffectiveAt).TotalSeconds;

        if (remainingSeconds <= 0)
        {
            return new ProrationResult(
                UnusedCreditCents: 0,
                NewPlanChargeCents: 0,
                NetAmountDueCents: 0,
                RemainingSeconds: 0,
                TotalPeriodSeconds: totalSeconds,
                FractionRemaining: 0m
            );
        }

        if (remainingSeconds > totalSeconds)
        {
            remainingSeconds = totalSeconds;
        }

        var fractionRemaining = (decimal)remainingSeconds / (decimal)totalSeconds;

        // Exact sub-cent banker's rounding to nearest integer cent
        var unusedCredit = (long)Math.Round((decimal)currentPlanPriceCents * fractionRemaining, MidpointRounding.AwayFromZero);
        var newPlanCharge = (long)Math.Round((decimal)newPlanPriceCents * fractionRemaining, MidpointRounding.AwayFromZero);
        var netDue = newPlanCharge - unusedCredit;

        return new ProrationResult(
            UnusedCreditCents: unusedCredit,
            NewPlanChargeCents: newPlanCharge,
            NetAmountDueCents: netDue,
            RemainingSeconds: remainingSeconds,
            TotalPeriodSeconds: totalSeconds,
            FractionRemaining: Math.Round(fractionRemaining, 6)
        );
    }
}
