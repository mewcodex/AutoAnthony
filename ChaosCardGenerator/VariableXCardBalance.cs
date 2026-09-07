namespace ChaosCardGenerator;

internal readonly record struct VariableXBudgetPoint(
    int ResolvedX,
    double EffectiveCost,
    double NetValue,
    double Minimum,
    double Maximum)
{
    internal bool IsWithinEnvelope(bool skipUpperBound = false) =>
        NetValue >= Minimum && (skipUpperBound || NetValue <= Maximum);
}

/// <summary>
/// Core AutoAnthony balance projection for variable-X cards. This deliberately lives below CardTinkeringApi so
/// ordinary pool generation enforces the same concrete X=1/X=3 checks even when no external card editor is loaded.
/// </summary>
internal static class VariableXCardBalance
{
    // X cards can spend only the resources currently available and cannot realize the fixed-cost curve's full
    // draw-efficiency premium at every checkpoint. Native X cards cluster near 0.5-1.0x of the corresponding
    // fixed-cost lower edge, so use half of that lower edge while retaining the ordinary ceiling as a hard guard.
    // Exact native recipes remain exempt from generation rejection, but are still reported by the audit below.
    private const double FlexiblePaymentLowerBoundFactor = 0.50d;
    internal static IReadOnlyList<int> GenerationCheckpoints { get; } = [1, 3];

    internal static bool IsVariableX(GeneratedCard card) => card.Cost < 0 || card.HasStarCostX;

    internal static IReadOnlyList<VariableXBudgetPoint> EvaluateGenerationCheckpoints(GeneratedCard card,
        bool balancedValues) => GenerationCheckpoints
        .Select(resolvedX => Evaluate(card, resolvedX, balancedValues))
        .ToArray();

    internal static bool IsWithinGenerationEnvelope(GeneratedCard card, bool balancedValues,
        bool skipUpperBound = false) =>
        !IsVariableX(card) || EvaluateGenerationCheckpoints(card, balancedValues)
            .All(point => point.IsWithinEnvelope(skipUpperBound));

    internal static VariableXBudgetPoint Evaluate(GeneratedCard card, int resolvedX, bool balancedValues)
    {
        if (!IsVariableX(card))
            throw new ArgumentException("Concrete X evaluation requires an Energy-X or Star-X card.", nameof(card));

        resolvedX = Math.Max(0, resolvedX);
        var operations = MaterializeOperations(card.Operations, resolvedX);
        var energyCost = card.Cost < 0 ? resolvedX : Math.Max(0, card.Cost);
        var starCost = card.HasStarCostX ? resolvedX : card.StarCost;
        var hasPrintedResourceCost = energyCost > 0 || starCost > 0;
        var effectiveCost = ResourceEconomyModel.BudgetEffectiveCost(
            energyCost, starCost, hasEnergyX: false, hasStarX: false, operations);
        var rewardFields = Math.Max(1, EffectBalanceModel.PositiveRewardFieldCount(
            operations, hasPrintedResourceCost, card.Type, card.Tags));
        var powerFactor = ComponentAssemblyGenerator.PowerOneShotBudgetFactorForTinkering(
            operations, card.Type);
        var bounds = ComponentAssemblyGenerator.WholeCardBudgetBounds(card.Rarity, effectiveCost,
            rewardFields, powerFactor: powerFactor, balancedValues: balancedValues,
            character: card.Character);
        if (operations.Any(CardEffectRules.IsZeroCostCopyThisCardToDiscard))
        {
            var zeroCostBounds = ComponentAssemblyGenerator.WholeCardBudgetBounds(card.Rarity, 0d,
                rewardFields, powerFactor: powerFactor, balancedValues: balancedValues,
                character: card.Character);
            bounds = (
                CopyThisCardValuation.BlendWithZeroCostEnvelope(bounds.Minimum, zeroCostBounds.Minimum),
                CopyThisCardValuation.BlendWithZeroCostEnvelope(bounds.Maximum, zeroCostBounds.Maximum));
        }

        var netValue = EffectBalanceModel.EstimatedNetCardValue(operations,
            hasPrintedResourceCost, card.Type, card.Tags, card.Rarity,
            card.UnifiedChaos ? null : card.Character);
        return new VariableXBudgetPoint(resolvedX, effectiveCost, netValue,
            bounds.Minimum * FlexiblePaymentLowerBoundFactor, bounds.Maximum);
    }

    internal static GeneratorOperation[] MaterializeOperations(IReadOnlyList<GeneratorOperation> operations,
        int resolvedX)
    {
        resolvedX = Math.Max(0, resolvedX);
        var effectiveX = resolvedX;
        var globalMultiplier = operations.FirstOrDefault(operation =>
            operation.Template == "R:DoubleEitherXAtThreshold");
        if (globalMultiplier is not null)
        {
            var threshold = Math.Max(1, OperationRuntimeSpecCompiler.StaticLiteralValue(
                globalMultiplier, "threshold", 4));
            if (resolvedX >= threshold) effectiveX *= 2;
        }

        return operations.Select(operation =>
        {
            var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
            if (!spec.Values.Any(value => value.Source is "energy_x" or "star_x" or "special_x"))
                return operation;
            var values = spec.Values.Select(value => value.Source is "energy_x" or "star_x" or "special_x"
                ? value with
                {
                    BaseValue = Math.Max(0, effectiveX + value.Offset),
                    Source = "fixed",
                    Offset = 0
                }
                : value).ToArray();
            return operation with { RuntimeSpec = spec with { Values = values } };
        }).ToArray();
    }
}
