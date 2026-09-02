using System.Text.RegularExpressions;

namespace ChaosCardGenerator;

public enum SpecialXGenerationMode { Normal, Disabled, Forced }

/// <summary>
/// Rarely turns a fixed-cost card whose scalable payoff equals that cost into a distinct X-cost card.
/// The compact operation marker is persisted in pool snapshots, so runtime execution can distinguish
/// "X damage" from the base game's ordinary "deal fixed damage X times" operations.
/// </summary>
public static class SpecialXCardConverter
{
    public const double NaturalChance = 0.025d;
    public const double GeneralXDoubleAttemptChance = 0.20d;
    public const int NonPowerDrawWeightPercent = 15;
    public const string Parameter = "specialX";
    public const string ValueMaskParameter = "specialXMask";
    public const int EnergyResource = 1;
    public const int StarResource = 2;

    public static bool IsSpecial(GeneratorOperation operation) =>
        operation.Parameters.TryGetValue(Parameter, out var resource)
        && resource is EnergyResource or StarResource;

    public static bool IsSpecial(GeneratedCard card) => card.Operations.Any(IsSpecial);

    public static int Resource(GeneratorOperation operation) =>
        operation.Parameters.GetValueOrDefault(Parameter);

    public static bool ValueUsesSpecialX(GeneratorOperation operation, int numericIndex) =>
        IsSpecial(operation)
        && ((operation.Parameters.TryGetValue(ValueMaskParameter, out var mask)
                ? mask
                : 1 << PrimaryValueIndex(operation)) & (1 << numericIndex)) != 0;

    public static int PrimaryValueIndex(GeneratorOperation operation) =>
        operation.Template == "I:DrawAndBlockIfSkill" ? 1 : 0;

    public static bool IsOrdinaryX(GeneratedCard card) =>
        (card.Cost < 0 || card.HasStarCostX) && !IsSpecial(card);

    public static GeneratedCard Convert(GeneratedCard card, Random random, SpecialXGenerationMode mode)
    {
        if (mode == SpecialXGenerationMode.Disabled || IsSpecial(card)
            || card.Cost < 0 || card.HasStarCostX || card.Rarity == GeneratedRarity.Basic
            || card.Tags.Contains(CardTag.Sly))
            return card;
        if (card.Operations.Any(CardEffectRules.IsSelfCostChange)) return card;

        var resources = new List<(int Marker, int Value)>();
        if (card.Cost > 0 && card.Operations.Any(operation => IsConvertibleValue(operation, card.Cost)))
            resources.Add((EnergyResource, card.Cost));
        if ((card.Character == GeneratedCharacter.Regent || card.UnifiedChaos)
            && card.StarCost > 0
            && card.Operations.Any(operation => IsConvertibleValue(operation, card.StarCost)))
            resources.Add((StarResource, card.StarCost));
        if (resources.Count == 0 && mode == SpecialXGenerationMode.Forced)
        {
            // Pool quotas need one or two special X cards. Repeatedly rolling both a fixed cost and an equal
            // payoff made that quota path needlessly expensive. In forced mode, pick a normal scalable value
            // first (the common 1-3 cost band), treat it as the pre-conversion energy cost, then convert it.
            // Natural rolls remain strict: they still require the independently sampled printed cost to match.
            var forcedValues = card.Operations
                .Where(IsConvertibleOperation)
                .SelectMany(ConvertibleSlots)
                .Select(item => item.Slot.BaseValue)
                .Where(value => value is >= 1 and <= 3)
                .Distinct()
                .ToArray();
            if (forcedValues.Length > 0)
                resources.Add((EnergyResource, forcedValues[random.Next(forcedValues.Length)]));
        }
        var naturalChance = NaturalChance;
        if (card.Type != GeneratedCardType.Power && card.Operations.Any(CardEffectRules.IsCardDrawEffect))
            naturalChance *= NonPowerDrawWeightPercent / 100d;
        if (resources.Count == 0
            || mode == SpecialXGenerationMode.Normal && random.NextDouble() >= naturalChance)
            return card;

        var selected = resources[random.Next(resources.Count)];
        var converted = card.Operations.Select(operation =>
            ConvertOperation(operation, selected.Value, selected.Marker)).ToArray();
        var convertedCard = card with
        {
            Cost = selected.Marker == EnergyResource ? -1 : card.Cost,
            StarCost = selected.Marker == StarResource ? -1 : card.StarCost,
            HasStarCostX = selected.Marker == StarResource,
            Operations = converted,
            ChineseDescription = CardDescriptionRenderer.Render(converted),
            EnglishDescription = EnglishCardDescriptionRenderer.Render(converted),
            Upgrade = null
        };
        if ((card.Character == GeneratedCharacter.Regent || card.UnifiedChaos)
            && random.NextDouble() < GeneralXDoubleAttemptChance)
            convertedCard = TryTradeOtherValuesForGeneralXDouble(convertedCard);
        return convertedCard;
    }

    private static GeneratedCard TryTradeOtherValuesForGeneralXDouble(GeneratedCard card)
    {
        if (card.Operations.Any(operation => operation.Template == "R:DoubleEitherXAtThreshold")) return card;
        var modifier = new GeneratorOperation("R:DoubleEitherXAtThreshold", OperationScope.Modifier,
            "如果X至少为4，则X翻倍。", new Dictionary<string, int>(),
            RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(new GeneratorOperation(
                "R:DoubleEitherXAtThreshold", OperationScope.Modifier, "如果X至少为4，则X翻倍。",
                new Dictionary<string, int>())));
        var originalValue = EffectBalanceModel.EstimatedPositiveCardValue(
            card.Operations, true, card.Type, card.Tags);
        if (originalValue <= 0d) return card;

        var operations = card.Operations.Append(modifier).ToList();
        var targetMaximum = originalValue * 1.03d;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var candidateValue = EffectBalanceModel.EstimatedPositiveCardValue(
                operations, true, card.Type, card.Tags);
            if (candidateValue <= targetMaximum)
            {
                var finalized = operations.ToArray();
                return card with
                {
                    Operations = finalized,
                    ChineseDescription = CardDescriptionRenderer.Render(finalized),
                    EnglishDescription = EnglishCardDescriptionRenderer.Render(finalized),
                    Upgrade = null
                };
            }

            var scale = Math.Clamp(targetMaximum / candidateValue, 0.20d, 0.95d);
            var changed = false;
            for (var operationIndex = 0; operationIndex < operations.Count - 1; operationIndex++)
            {
                var operation = operations[operationIndex];
                if (CardEffectRules.IsNegativeEffect(operation)
                    || CardEffectRules.IsNonUpgradeableNumericMarker(operation)
                    || !CardEffectRules.IsBeneficialEffect(operation))
                    continue;
                var atom = new ComponentAtom(operation.Template, operation.Scope, string.Empty,
                    operation.RequiresSingleTarget, CardReferenceRequirement.None)
                    { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(operation) };
                if (!EffectBalanceModel.IsScalableReward(atom)) continue;
                foreach (var slot in OperationRuntimeSpecCompiler.ExplicitFixedValueSlots(operation)
                             .Where(slot => slot.Source == "fixed" && slot.BaseValue + slot.Offset > 1))
                {
                    var current = slot.BaseValue + slot.Offset;
                    var reduced = Math.Max(1, (int)Math.Floor(current * scale));
                    if (reduced >= current
                        || !OperationRuntimeSpecCompiler.TryReplaceFixedValue(operation, slot.Id, reduced,
                            out var updated))
                        continue;
                    operation = updated;
                    changed = true;
                }
                operations[operationIndex] = operation;
            }
            if (!changed) break;
        }
        return card;
    }

    public static bool IsConvertibleValue(GeneratorOperation operation, int cost)
    {
        if (cost <= 0 || !IsConvertibleOperation(operation)) return false;

        return ConvertibleSlots(operation).Any(item => item.Slot.BaseValue == cost);
    }

    private static bool IsConvertibleOperation(GeneratorOperation operation)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        if (IsSpecial(operation) || CardEffectRules.UsesX(operation)
            || CardEffectRules.IsNegativeEffect(operation)
            || CardEffectRules.IsNonUpgradeableNumericMarker(operation)
            || CardEffectRules.IsSelfCostChange(operation)
            || operation.Template.Contains(":Proxy", StringComparison.Ordinal)
            || CardEffectRules.IsEnergyGainOperation(operation)
            || operation.Template == "R:GainStars"
            || spec.Opcode == "modify_cost"
            || spec.Flags.Any(flag => flag is "set_cost_zero" or "set_cost_zero_this_turn"))
            return false;
        if (operation.Scope is not (OperationScope.SingleEnemyOnly or OperationScope.NonTargeted
            or OperationScope.Modifier or OperationScope.Independent))
            return false;
        return ConvertibleSlots(operation).Count > 0;
    }

    private static GeneratorOperation ConvertOperation(GeneratorOperation operation, int cost, int resource)
    {
        if (!IsConvertibleValue(operation, cost)) return operation;
        var replacements = ConvertibleSlots(operation)
            .Where(item => item.Slot.BaseValue == cost).ToArray();
        if (replacements.Length == 0) return operation;
        // Text mutation is now a rendering projection only. Slot eligibility and the persisted mask above are
        // determined entirely by RuntimeSpec; the legacy compiler guarantees the same slot/numeric ordering.
        var chineseNumbers = Regex.Matches(operation.ChineseText, @"\d+");
        var chinese = operation.ChineseText;
        foreach (var item in replacements.OrderByDescending(item => item.NumericIndex))
        {
            if (item.NumericIndex >= chineseNumbers.Count) continue;
            var number = chineseNumbers[item.NumericIndex];
            chinese = chinese[..number.Index] + "X" + chinese[(number.Index + number.Length)..];
        }
        var english = EnglishCardDescriptionRenderer.OperationText(operation);
        var englishNumbers = Regex.Matches(english, @"\d+");
        foreach (var item in replacements.OrderByDescending(item => item.NumericIndex))
        {
            if (item.NumericIndex >= englishNumbers.Count) continue;
            var englishNumber = englishNumbers[item.NumericIndex];
            english = english[..englishNumber.Index] + "X"
                + english[(englishNumber.Index + englishNumber.Length)..];
        }
        ExternalOperationTextRegistry.Register(operation.Template, chinese, english);
        var parameters = operation.Parameters.ToDictionary(entry => entry.Key, entry => entry.Value,
            StringComparer.Ordinal);
        parameters[Parameter] = resource;
        parameters[ValueMaskParameter] = replacements.Sum(item => 1 << item.NumericIndex);
        var runtimeSpec = OperationRuntimeSpecCompiler.ConvertFixedValuesToSpecialX(
            OperationRuntimeSpecCompiler.GetOrCompile(operation),
            replacements.Select(item => item.Slot.Id).ToArray());
        return operation with { ChineseText = chinese, Parameters = parameters, RuntimeSpec = runtimeSpec };
    }

    private static IReadOnlyList<(RuntimeValueSlot Slot, int NumericIndex)> ConvertibleSlots(
        GeneratorOperation operation)
    {
        var values = OperationRuntimeSpecCompiler.GetOrCompile(operation).Values
            .Where(value => value.Source == "fixed" && value.Explicit).ToArray();
        if (values.Length == 0) return [];
        // Damage amount/hit count and Draw-and-Block's draw/block values are all genuine scalable slots.
        // Other multi-number clauses usually mix a threshold with a payoff and stay out of this Easter egg.
        if (values.Length > 1 && !CardEffectRules.IsEnemyDamage(operation)
            && operation.Template != "I:DrawAndBlockIfSkill")
            return [];
        return values.Select((slot, index) => (slot, index)).ToArray();
    }

    internal static void ValidateLegacyEligibilityEquivalence(IEnumerable<GeneratorOperation> operations)
    {
        var failures = new List<string>();
        foreach (var operation in operations)
        {
            var legacySlots = LegacyConvertibleMatchesForAudit(operation)
                .Select(item => int.Parse(item.Match.Value, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            var structuredSlots = IsConvertibleOperation(operation)
                ? ConvertibleSlots(operation).Select(item => item.Slot.BaseValue).ToArray()
                : [];
            if (!legacySlots.SequenceEqual(structuredSlots))
                failures.Add($"{operation.Template}:{operation.ChineseText}:"
                    + $"legacy=[{string.Join(',', legacySlots)}]:spec=[{string.Join(',', structuredSlots)}]");
        }
        if (failures.Count > 0)
            throw new InvalidOperationException("Special-X eligibility projection changed:\n"
                + string.Join("\n", failures));
    }

    // Audit-only copy of the frozen 0.2.118 text projection. Production selection never calls this.
    private static IReadOnlyList<(Match Match, int NumericIndex)> LegacyConvertibleMatchesForAudit(
        GeneratorOperation operation)
    {
        if (IsSpecial(operation) || CardEffectRules.UsesX(operation)
            || CardEffectRules.IsNegativeEffect(operation)
            || CardEffectRules.IsNonUpgradeableNumericMarker(operation)
            || operation.Template.Contains(":Proxy", StringComparison.Ordinal)
            || CardEffectRules.IsEnergyGainOperation(operation)
            || operation.Template == "R:GainStars"
            || operation.Scope is not (OperationScope.SingleEnemyOnly or OperationScope.NonTargeted
                or OperationScope.Modifier or OperationScope.Independent)
            || operation.ChineseText.Contains("费用", StringComparison.Ordinal)
            || operation.ChineseText.Contains("耗能", StringComparison.Ordinal)
            || operation.ChineseText.Contains("免费", StringComparison.Ordinal))
            return [];
        var values = Regex.Matches(operation.ChineseText, @"\d+");
        if (values.Count == 0
            || values.Count > 1 && !CardEffectRules.IsEnemyDamage(operation)
                && operation.Template != "I:DrawAndBlockIfSkill")
            return [];
        return values.Cast<Match>().Select((match, index) => (match, index)).ToArray();
    }
}
