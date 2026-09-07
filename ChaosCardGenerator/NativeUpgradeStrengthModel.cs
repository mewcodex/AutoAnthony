namespace ChaosCardGenerator;

/// <summary>
/// Whole-card upgrade prior fitted from v111's single-player component-pool cards. A source audit of 464 directly
/// parseable OnUpgrade methods found an approximate median gain of 400 common value units (damage=100/point,
/// Block=120/point), with rarity medians of 360/360/450/500/1000. The generated center is deliberately a little
/// higher, while candidate-specific native numeric deltas remain owned by <see cref="NativeUpgradeValueModel"/>.
/// </summary>
internal static class NativeUpgradeStrengthModel
{
    // Native cards with at least two upgrade actions: Basic 10.5%, Common 25.5%, Uncommon 12.9%, Rare 6.6%,
    // Ancient 30.0% (small n=10). These remain the full-strength two-effect gates. A separate post-pass may add
    // one fixed-200-value numeric improvement (minimum printed delta 1) to an otherwise single, non-resource
    // upgrade; do not fold that modest rider back into these native-rate probabilities.
    internal static int MultipleEffectChance(GeneratedRarity rarity) => rarity switch
    {
        GeneratedRarity.Basic => 30,
        GeneratedRarity.Common => 48,
        GeneratedRarity.Uncommon => 25,
        GeneratedRarity.Rare => 12,
        GeneratedRarity.Ancient => 51,
        _ => 25
    };

    internal static double SampleTargetValue(GeneratedRarity rarity, Random random)
    {
        var center = CenterValue(rarity);
        var multiplier = random.Next(1000) switch
        {
            < 180 => 0.75d,
            < 680 => 1.00d,
            < 930 => 1.20d,
            _ => 1.45d
        };
        return center * multiplier;
    }

    internal static double CenterValue(GeneratedRarity rarity) => rarity switch
        {
            GeneratedRarity.Basic => 380d,
            GeneratedRarity.Common => 450d,
            GeneratedRarity.Uncommon => 550d,
            GeneratedRarity.Rare => 650d,
            GeneratedRarity.Ancient => 850d,
            _ => 450d
        };

    /// <summary>
    /// Final whole-upgrade ceiling. Candidate proximity alone is insufficient: when only one legal candidate
    /// remains, a minimum +1 on a high-frequency/multiplicative field can be far above the sampled native target.
    /// Bound both the sampled tail and the gain relative to the unupgraded card while retaining native-sized
    /// upgrades for low-value cards.
    /// </summary>
    internal static double MaximumPlanGain(GeneratedCard card, double sampledTarget)
    {
        var baseValue = EffectBalanceModel.EstimatedPositiveCardValue(card.Operations);
        // The low 0.75 target roll changes preference, not whether a card has any legal visible +1 upgrade.
        // Never push the hard ceiling below the rarity's native center.
        var targetCeiling = Math.Max(CenterValue(card.Rarity), sampledTarget * 1.50d);
        var relativeCeiling = Math.Max(CenterValue(card.Rarity), baseValue * 0.70d);
        return Math.Max(1d, Math.Min(targetCeiling, relativeCeiling));
    }

    /// <summary>Weights a legal candidate by how closely its whole-card marginal gain fits the remaining budget.</summary>
    internal static int CandidateWeight(double gain, double desiredGain)
    {
        gain = Math.Max(1d, gain);
        desiredGain = Math.Max(1d, desiredGain);
        var distance = Math.Abs(gain - desiredGain) / Math.Max(180d, desiredGain);
        return Math.Max(1, (int)Math.Round(10_000d / Math.Pow(1d + distance * 2.25d, 2d)));
    }

    internal static double NonOperationGain(CardUpgradeEffect effect, GeneratedCard card) => effect.Kind switch
    {
        CardUpgradeKind.ReduceSelfDamage => Math.Abs(effect.Delta ?? -1) * 250d,
        CardUpgradeKind.ReduceNegativeNumber => Math.Abs(effect.Delta ?? -1) * 260d,
        CardUpgradeKind.ReduceThreshold => 450d,
        CardUpgradeKind.UpgradeDerivative => 400d,
        CardUpgradeKind.UpgradeGeneratedCards => 500d,
        CardUpgradeKind.ChooseExhaust => 500d,
        CardUpgradeKind.RepeatOperation => 500d,
        CardUpgradeKind.ExecuteOperationOnPlay => 500d,
        CardUpgradeKind.UpgradeReferencedCards => 500d,
        CardUpgradeKind.SelectAllCards => 500d,
        CardUpgradeKind.GrantInnate => 200d,
        CardUpgradeKind.GrantRetain => 400d,
        CardUpgradeKind.RemoveEthereal => 300d,
        CardUpgradeKind.RemoveExhaust => Math.Max(500d,
            EffectBalanceModel.EstimatedPositiveCardValue(card.Operations) * 0.55d),
        CardUpgradeKind.ReduceCost => ResourceReductionGain(card, 1d),
        CardUpgradeKind.ReduceStarCost => ResourceReductionGain(card, 0.5d),
        _ => 200d
    };

    private static double ResourceReductionGain(GeneratedCard card, double reduction)
    {
        if (card.Cost < 0) return 500d;
        var oldCost = card.Cost + CardEffectRules.StarCostEnergyEquivalent(card.StarCost);
        var newCost = Math.Max(0d, oldCost - reduction);
        var rarityScale = 800 + card.Rarity switch
        {
            GeneratedRarity.Basic => -100,
            GeneratedRarity.Common => 0,
            GeneratedRarity.Uncommon => 120,
            GeneratedRarity.Rare => 280,
            GeneratedRarity.Ancient => 450,
            _ => 0
        };
        return Math.Max(250d, rarityScale
            * (ResourceEconomyModel.BudgetStrength(oldCost) - ResourceEconomyModel.BudgetStrength(newCost)));
    }

    internal static void Validate()
    {
        if (MultipleEffectChance(GeneratedRarity.Rare) >= MultipleEffectChance(GeneratedRarity.Common)
            || MultipleEffectChance(GeneratedRarity.Basic) >= MultipleEffectChance(GeneratedRarity.Common)
            || SampleTargetValue(GeneratedRarity.Ancient, new Random(7))
                <= SampleTargetValue(GeneratedRarity.Basic, new Random(7))
            || CandidateWeight(450d, 450d) <= CandidateWeight(1_200d, 450d))
            throw new InvalidOperationException("原版升级强度先验或候选接近度权重失效。");
    }
}
