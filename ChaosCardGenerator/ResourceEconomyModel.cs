namespace ChaosCardGenerator;

/// <summary>
/// Converts every fixed card resource into one continuous Energy-equivalent axis.  Printed Energy and Stars are
/// payments; immediate, delayed and triggered Energy/Star gains are expected refunds.  The resulting effective
/// cost is allowed to be fractional or negative and feeds the same nonlinear reward curve everywhere.
/// </summary>
internal static class ResourceEconomyModel
{
    private const double StarEnergyEquivalent = 0.5d;
    // Delayed resource and non-resource payoffs share one cadence. A separate 0.72 refund factor previously made
    // the same NextTurnStart component worth different amounts depending only on the linked effect's opcode.
    private const double NextTurnRefundFactor = EffectBalanceModel.NextTurnTriggerFrequency;
    private const double PersistentTriggerTurns = 2.4d;
    // Double Energy has no fixed numeric slot: its live result still depends on the player's current Energy.
    // For generation budgets, use the requested two-Energy expectation without reclassifying the proxy as an
    // ordinary Gain Energy operation (which would incorrectly make it numeric-upgradeable and require an icon).
    private const double DoubleEnergyExpectedRefund = 2d;

    // A card still consumes one draw at zero Energy, so zero cost retains meaningful positive budget.  Above one
    // Energy, draw efficiency makes the curve deliberately superlinear: 2 costs a little more than twice the
    // one-cost budget and 3 costs materially more than three times it.
    private static readonly (double Cost, double Strength)[] DesiredCurve =
    [
        (-2d, 0.12d),
        (-1d, 0.28d),
        (0d, 0.55d),
        (1d, 1d),
        (2d, 2.15d),
        (3d, 3.40d),
        // Bury is the clean native four-Energy anchor: an Uncommon with one unconditional 52-damage line.
        // The 4.65 point keeps that result near 52 after the shared rarity sampler, while the monotone marginal
        // slope preserves one continuous curve for damage, Block, Forge and every other scalable reward.
        (4d, 4.65d),
        (5d, 6.00d)
    ];

    // Numeric families were historically instantiated against these integer ExpectedLineValue tiers.  Keeping the
    // old curve explicit lets the final continuous correction replace rather than stack on top of that pricing.
    private static readonly (double Cost, double Strength)[] LegacyCurve =
    [
        (0d, 0.625d),
        (1d, 1d),
        (2d, 1.625d),
        (3d, 2.3125d),
        (4d, 2.875d)
    ];

    internal static double PrintedCost(int energyCost, int starCost, bool hasEnergyX = false,
        bool hasStarX = false)
    {
        if (hasEnergyX || hasStarX || energyCost < 0) return double.NaN;
        return Math.Max(0, energyCost) + Math.Max(0, starCost) * StarEnergyEquivalent;
    }

    internal static double EffectiveCost(double printedCost, IReadOnlyList<GeneratorOperation> operations)
    {
        if (double.IsNaN(printedCost)) return double.NaN;
        return printedCost - ExpectedRefund(operations);
    }

    /// <summary>
    /// Converts the semantic payment into the cost coordinate used by numeric budgets. Energy follows the
    /// superlinear curve because its per-turn supply cannot be banked; Stars add linearly at two Stars per one
    /// strength point because they persist between turns. Refunds are deducted from their own resource before the
    /// two parts are combined. The returned coordinate lets existing budget consumers keep using one shared axis.
    /// </summary>
    internal static double BudgetEffectiveCost(int energyCost, int starCost, bool hasEnergyX, bool hasStarX,
        IReadOnlyList<GeneratorOperation> operations)
    {
        if (hasEnergyX || hasStarX || energyCost < 0)
            return double.NaN;

        var energyRefund = 0d;
        var starRefund = 0d;
        foreach (var operation in operations)
        {
            var refund = ExpectedRefund(operation, operations);
            if (IsExpectedEnergyRefundOperation(operation)) energyRefund += refund;
            else if (CardEffectRules.IsStarGainOperation(operation)) starRefund += refund;
        }

        var netEnergy = Math.Max(0, energyCost) - energyRefund;
        var netStars = Math.Max(0, starCost) * StarEnergyEquivalent - starRefund;
        var budgetStrength = BudgetStrength(netEnergy) + netStars;
        return CostForBudgetStrength(budgetStrength);
    }

    internal static double ExpectedRefund(IReadOnlyList<GeneratorOperation> operations)
    {
        var total = 0d;
        for (var index = 0; index < operations.Count; index++)
            total += ExpectedRefund(operations[index], operations);
        return total;
    }

    internal static double ExpectedRefund(GeneratorOperation operation,
        IReadOnlyList<GeneratorOperation> operations)
    {
        var isEnergyRefund = IsExpectedEnergyRefundOperation(operation);
        var amount = operation.Template == "I:ProxyAtomic_DoubleEnergy"
            ? DoubleEnergyExpectedRefund
            : OperationRuntimeSpecCompiler.StaticLiteralValue(operation,
                isEnergyRefund ? "energy" : "stars");
        if (amount <= 0) return 0d;
        var resource = isEnergyRefund
            ? amount
            : CardEffectRules.IsStarGainOperation(operation) ? amount * StarEnergyEquivalent : 0d;
        if (resource <= 0d) return 0d;

        if (!operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
            || triggerIndex < 0 || triggerIndex >= operations.Count)
            return CardEffectRules.IsDelayedEffect(operation) ? resource * NextTurnRefundFactor : resource;

        var trigger = operations[triggerIndex];
        // “For the next N turns” is delayed, but it still pays N separate refunds.  The generic delayed branch
        // used to return after pricing only one of them, which was the last remaining trigger-frequency omission
        // in the effective-cost path.
        if (CardEffectRules.IsNextTurnsStartTrigger(trigger))
            // ExpectedTriggerResolutions already includes the shared next-turn delay discount for every
            // resolution. Do not apply NextTurnRefundFactor a second time here.
            return resource * EffectBalanceModel.ExpectedTriggerResolutions(trigger);
        if (CardEffectRules.IsDelayedEffect(trigger)
            || CardEffectRules.IsNextTurnStartTrigger(trigger))
            return resource * NextTurnRefundFactor;

        if (EffectBalanceModel.HasRepeatedOrMultiplicativePayoff(trigger))
            return resource * EffectBalanceModel.ExpectedTriggerResolutions(trigger, PersistentTriggerTurns);

        return resource * EffectBalanceModel.ExpectedTriggerResolutions(trigger);
    }

    private static bool IsExpectedEnergyRefundOperation(GeneratorOperation operation) =>
        CardEffectRules.IsEnergyGainOperation(operation)
        || operation.Template == "I:ProxyAtomic_DoubleEnergy";

    internal static double BudgetStrength(double effectiveCost) => Interpolate(DesiredCurve, effectiveCost);

    internal static double LegacyBudgetStrength(double integerBudgetCost) =>
        Interpolate(LegacyCurve, Math.Max(0d, integerBudgetCost));

    internal static double NumericAdjustment(double effectiveCost, int legacyBudgetCost)
    {
        if (double.IsNaN(effectiveCost) || legacyBudgetCost < 0) return 1d;
        return BudgetStrength(effectiveCost) / Math.Max(0.01d, LegacyBudgetStrength(legacyBudgetCost));
    }

    internal static int NegativeFamilyWeight(IEnumerable<ComponentAtom> family, double effectiveCost)
    {
        if (double.IsNaN(effectiveCost) || effectiveCost >= 0d) return 100;
        var debt = Math.Min(3d, -effectiveCost);
        return family.Any(CardEffectRules.IsNegativeEffect)
            ? (int)Math.Round(100d + debt * 180d)
            : (int)Math.Round(Math.Max(35d, 100d - debt * 22d));
    }

    internal static int NegativeExtraLineChance(double effectiveCost)
    {
        if (double.IsNaN(effectiveCost) || effectiveCost >= 0d) return 0;
        return (int)Math.Round(Math.Min(90d, 42d + -effectiveCost * 24d));
    }

    private static double Interpolate(IReadOnlyList<(double Cost, double Strength)> curve, double cost)
    {
        if (cost <= curve[0].Cost) return curve[0].Strength;
        for (var index = 1; index < curve.Count; index++)
        {
            var right = curve[index];
            if (cost > right.Cost) continue;
            var left = curve[index - 1];
            var progress = (cost - left.Cost) / (right.Cost - left.Cost);
            return left.Strength + (right.Strength - left.Strength) * progress;
        }

        var last = curve[^1];
        var prior = curve[^2];
        var slope = (last.Strength - prior.Strength) / (last.Cost - prior.Cost);
        return last.Strength + (cost - last.Cost) * slope;
    }

    private static double CostForBudgetStrength(double strength)
    {
        if (strength <= DesiredCurve[0].Strength) return DesiredCurve[0].Cost;
        for (var index = 1; index < DesiredCurve.Length; index++)
        {
            var right = DesiredCurve[index];
            if (strength > right.Strength) continue;
            var left = DesiredCurve[index - 1];
            var progress = (strength - left.Strength) / (right.Strength - left.Strength);
            return left.Cost + (right.Cost - left.Cost) * progress;
        }

        var last = DesiredCurve[^1];
        var prior = DesiredCurve[^2];
        var slope = (last.Strength - prior.Strength) / (last.Cost - prior.Cost);
        return last.Cost + (strength - last.Strength) / slope;
    }

    internal static void Validate()
    {
        if (Math.Abs(PrintedCost(1, 1) - 1.5d) > 0.0001d
            || Math.Abs(PrintedCost(1, 2) - 2d) > 0.0001d)
            throw new InvalidOperationException("蓝星没有按半费精度进入有效费用。 ");
        var mixedLow = BudgetEffectiveCost(1, 2, false, false, []);
        var pureHighStars = BudgetEffectiveCost(0, 5, false, false, []);
        var mixedHighStars = BudgetEffectiveCost(1, 5, false, false, []);
        if (Math.Abs(BudgetStrength(mixedLow) - 2d) > 0.0001d
            || Math.Abs(BudgetStrength(pureHighStars) - 3.05d) > 0.0001d
            || Math.Abs(BudgetStrength(mixedHighStars) - 3.5d) > 0.0001d)
            throw new InvalidOperationException("蓝星费用没有在线性预算轴上与普通费用曲线相加。 ");
        if (BudgetStrength(2d) <= BudgetStrength(1d) * 2d
            || BudgetStrength(3d) <= BudgetStrength(1d) * 3d
            || Math.Abs(BudgetStrength(4d) - 4.65d) > 0.0001d)
            throw new InvalidOperationException("有效费用收益曲线不再满足2费略高于两倍、3费高于三倍。 ");

        var immediate = new GeneratorOperation("N:E", OperationScope.NonTargeted, "获得2点能量。",
            new Dictionary<string, int>());
        var everyCard = new GeneratorOperation("A:whenCardPlayed", OperationScope.AbilityTrigger,
            "每当你打出一张牌时。", new Dictionary<string, int>());
        var triggered = immediate with { Parameters = new Dictionary<string, int> { ["triggerIndex"] = 0 } };
        var nextTwoTurns = new GeneratorOperation("C:NextTurnsStart", OperationScope.ConditionalTrigger,
            "在接下来的2个回合开始时。", new Dictionary<string, int>());
        var delayedTriggered = immediate with
        {
            Parameters = new Dictionary<string, int> { ["triggerIndex"] = 0 }
        };
        var doubleEnergy = new GeneratorOperation("I:ProxyAtomic_DoubleEnergy", OperationScope.Independent,
            "将你的能量翻倍。", new Dictionary<string, int>());
        var immediateCost = EffectiveCost(1d, [immediate]);
        var doubledRefund = ExpectedRefund([doubleEnergy]);
        var doubledCost = EffectiveCost(1d, [doubleEnergy]);
        var doubledBudgetCost = BudgetEffectiveCost(1, 0, false, false, [doubleEnergy]);
        var repeatedRefund = ExpectedRefund([everyCard, triggered]);
        var delayedRefund = ExpectedRefund([nextTwoTurns, delayedTriggered]);
        if (Math.Abs(immediateCost + 1d) > 0.0001d
            || Math.Abs(doubledRefund - DoubleEnergyExpectedRefund) > 0.0001d
            || Math.Abs(doubledCost + 1d) > 0.0001d
            || Math.Abs(doubledBudgetCost + 1d) > 0.0001d
            || repeatedRefund <= 2d
            || Math.Abs(delayedRefund - 4d * NextTurnRefundFactor) > 0.0001d)
            throw new InvalidOperationException("即时或高频触发回费没有进入有效费用："
                + $" immediateCost={immediateCost:F3}, doubledRefund={doubledRefund:F3}, "
                + $"doubledCost={doubledCost:F3}, doubledBudgetCost={doubledBudgetCost:F3}, "
                + $"repeatedRefund={repeatedRefund:F3}, delayedRefund={delayedRefund:F3}。");
    }
}
