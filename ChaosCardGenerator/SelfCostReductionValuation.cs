namespace ChaosCardGenerator;

/// <summary>Prices future self-cost savings once, against printed Energy rather than effective cost or Stars.</summary>
internal static class SelfCostReductionValuation
{
    // Streamline / Kingly Kick: one future Energy saved is a smaller benefit than receiving Energy now.
    internal const double ValuePerEnergy = 700d;

    internal static double Value(GeneratorOperation operation, int index,
        IReadOnlyList<GeneratorOperation> operations, int? baseEnergyCost)
    {
        var ownSavings = ExpectedSavings(operation, index, operations, baseEnergyCost);
        if (baseEnergyCost is not { } cost) return ownSavings * ValuePerEnergy;
        if (cost <= 0) return 0d;
        var totalSavings = operations.Select((candidate, candidateIndex) => (candidate, candidateIndex))
            .Where(item => CardEffectRules.IsSelfCostReduction(item.candidate))
            .Sum(item => ExpectedSavings(item.candidate, item.candidateIndex, operations, cost));
        // Share the cap proportionally so per-component traces and upgrade deltas sum to the same card value.
        return ownSavings * Math.Min(1d, cost / Math.Max(1e-9d, totalSavings)) * ValuePerEnergy;
    }

    private static double ExpectedSavings(GeneratorOperation operation, int index,
        IReadOnlyList<GeneratorOperation> operations, int? baseEnergyCost)
    {
        var amount = CardEffectRules.IsNumericSelfCostReduction(operation)
            ? Math.Max(0, OperationRuntimeSpecCompiler.StaticLiteralValue(operation, "amount", 1))
            : Math.Max(0, baseEnergyCost ?? 1);
        var intrinsicCount = operation.Template switch
        {
            "F:CostPerMelody" => 1.5d,
            "F:CostIfWahKi" => 0.6d,
            "NCR:SetCostZeroIfOstyAttacked" => 0.55d,
            "C:whileInCombat" or "C:whileInCombatSkillCostReduction" => 2d,
            _ => 1d
        };
        return amount * intrinsicCount
            * EffectBalanceModel.LinkedResolutionMultiplierForAudit(operation, index, operations);
    }
}
