using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChaosCardGenerator;

/// <summary>
/// Read-only, localization-independent balance data exposed to card-editor companion mods. Values use the
/// generator's native currency: one point of ordinary single-target damage is 100 units.
/// </summary>
public sealed record CardTinkeringValueBreakdown(
    int CurrentValue,
    int OrdinaryUpperBound,
    bool ExceedsOrdinaryUpperBound,
    IReadOnlyList<CardTinkeringComponentValue> Components);

public sealed record CardTinkeringComponentValue(
    int OperationIndex,
    int Value,
    double? Multiplier,
    bool IsTrigger,
    bool IsSingleUseCandidate);

/// <summary>
/// Exact whole-card terms used by the generator's final budget comparison. Linear costs live on the value side;
/// multiplier costs live on the capacity side when an editor presents the equivalent inequality.
/// </summary>
public sealed record CardTinkeringBudgetBreakdown(
    double PositiveValue,
    double LinearCompensation,
    double DownsideMultiplier,
    double NetValue,
    double OrdinaryUpperBound);

public sealed record CardTinkeringValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static CardTinkeringValidationResult Valid { get; } = new(true, Array.Empty<string>());
}

/// <summary>
/// Stable integration surface for editors which rearrange already-instantiated generated operations. It never
/// mutates generator registrations or card pools and deliberately evaluates base (pre-upgrade) values only.
/// </summary>
public static class CardTinkeringApi
{
    public const int ApiVersion = 3;
    private const int PayloadSchema = 1;
    private sealed record CardPayload(int Schema, GeneratedCard Card,
        IReadOnlyList<OperationRuntimeSpec> RuntimeSpecs,
        IReadOnlyList<OperationLocalizedText?> LocalizedTexts,
        IReadOnlyList<string?> UpgradeValueSlots);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string SerializeCard(GeneratedCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var attached = OperationRuntimeSpecCompiler.Attach(card);
        return JsonSerializer.Serialize(new CardPayload(PayloadSchema, attached,
            attached.Operations.Select(OperationRuntimeSpecCompiler.RequireStructured).ToArray(),
            attached.Operations.Select(operation => operation.LocalizedText).ToArray(),
            attached.Upgrade?.Effects.Select(effect => effect.ValueSlotId).ToArray() ?? []), JsonOptions);
    }

    public static GeneratedCard DeserializeCard(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("A generated-card payload must not be empty.", nameof(payload));
        var envelope = JsonSerializer.Deserialize<CardPayload>(payload, JsonOptions)
            ?? throw new InvalidDataException("The generated-card payload did not contain a card.");
        if (envelope.Schema != PayloadSchema)
            throw new InvalidDataException($"Unsupported card-tinkering payload schema {envelope.Schema}.");
        if (envelope.RuntimeSpecs.Count != envelope.Card.Operations.Count
            || envelope.LocalizedTexts.Count != envelope.Card.Operations.Count)
            throw new InvalidDataException("The card-tinkering operation metadata count is inconsistent.");
        var operations = envelope.Card.Operations.Select((operation, index) => operation with
        {
            RuntimeSpec = envelope.RuntimeSpecs[index],
            LocalizedText = envelope.LocalizedTexts[index]
        }).ToArray();
        var upgrade = envelope.Card.Upgrade;
        if (upgrade is not null)
        {
            if (envelope.UpgradeValueSlots.Count != upgrade.Effects.Count)
                throw new InvalidDataException("The card-tinkering upgrade metadata count is inconsistent.");
            upgrade = upgrade with
            {
                Effects = upgrade.Effects.Select((effect, index) => effect with
                    { ValueSlotId = envelope.UpgradeValueSlots[index] }).ToArray()
            };
        }
        return OperationRuntimeSpecCompiler.Attach(envelope.Card with { Operations = operations, Upgrade = upgrade });
    }

    public static CardTinkeringValueBreakdown Evaluate(GeneratedCard card) =>
        Evaluate(card, balancedValues: true);

    /// <summary>
    /// Evaluates a card against the same balanced/aggressive whole-card envelope used to generate its pool.
    /// The one-argument overload remains the stable balanced-mode default for existing consumers.
    /// </summary>
    public static CardTinkeringValueBreakdown Evaluate(GeneratedCard card, bool balancedValues)
    {
        ArgumentNullException.ThrowIfNull(card);
        var operations = OperationRuntimeSpecCompiler.Attach(card.Operations);
        var budget = EvaluateBudget(card with { Operations = operations }, balancedValues);
        var components = operations.Select((operation, index) => EvaluateComponent(
            operation, index)).ToArray();

        var current = Math.Max(0, (int)Math.Min(int.MaxValue,
            Math.Ceiling(budget.PositiveValue - budget.LinearCompensation)));
        return new CardTinkeringValueBreakdown(
            current,
            Math.Max(0, (int)Math.Min(int.MaxValue, Math.Floor(budget.OrdinaryUpperBound))),
            budget.NetValue > budget.OrdinaryUpperBound + 0.0001d,
            components);
    }

    public static CardTinkeringBudgetBreakdown EvaluateBudget(GeneratedCard card) =>
        EvaluateBudget(card, balancedValues: true);

    /// <summary>
    /// Returns the production scorer's exact terms. The equivalent editor comparison is
    /// positive minus linear compensation &lt;= shell capacity times downside multiplier.
    /// </summary>
    public static CardTinkeringBudgetBreakdown EvaluateBudget(GeneratedCard card, bool balancedValues)
    {
        ArgumentNullException.ThrowIfNull(card);
        var operations = OperationRuntimeSpecCompiler.Attach(card.Operations);
        var templateEnergyCost = BudgetTemplateEnergyCost(card, operations);
        var hasPrintedResourceCost = templateEnergyCost != 0 || card.StarCost > 0 || card.HasStarCostX;
        var positive = EffectBalanceModel.EstimatedPositiveCardValue(
            operations, hasPrintedResourceCost, card.Type, card.Tags);
        var linear = NegativeEffectTuning.TotalLinearCompensationValue(operations, card.Rarity);
        var multiplier = CardEffectRules.NegativeEffectCompensationPercent(
            operations, card.Tags, hasPrintedResourceCost, card.Type,
            card.UnifiedChaos ? null : card.Character) / 100d;
        multiplier = Math.Max(1d, multiplier);
        var upper = OrdinaryUpperBound(card, operations, balancedValues);
        return new CardTinkeringBudgetBreakdown(
            positive, linear, multiplier, (positive - linear) / multiplier, upper);
    }

    public static CardTinkeringComponentValue EvaluateComponent(GeneratorOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return EvaluateComponent(operation, 0);
    }

    public static CardTinkeringValidationResult Validate(GeneratedCard shell,
        IReadOnlyList<GeneratorOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(operations);
        var attached = OperationRuntimeSpecCompiler.Attach(operations);
        var errors = new List<string>();

        for (var index = 0; index < attached.Count; index++)
        {
            var operation = attached[index];
            if (operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex))
            {
                if (triggerIndex < 0 || triggerIndex >= index)
                {
                    errors.Add($"component {index} references a trigger which does not precede it");
                    continue;
                }
                var parent = attached[triggerIndex];
                if (!CardEffectRules.TriggerNeedsLinkedEffect(parent)
                    && !CardEffectRules.IsDependencyPrefix(parent))
                    errors.Add($"component {index} is nested below a component which cannot own children");
            }

            var requirement = CardEffectRules.XRequirement(operation);
            var xMatches = requirement switch
            {
                XResourceRequirement.None => true,
                XResourceRequirement.Energy => shell.Cost < 0 && !shell.HasStarCostX,
                XResourceRequirement.Star => shell.Cost >= 0 && shell.HasStarCostX,
                XResourceRequirement.Either => shell.Cost < 0 || shell.HasStarCostX,
                _ => false
            };
            if (!xMatches)
                errors.Add($"component {index} requires a different X resource from the card shell");

            if (shell.Target == TargetMode.Other && CardEffectRules.RequiresSingleEnemyTarget(operation)
                && !CardEffectRules.UsesExplicitRandomEnemyTarget(operation)
                && !HasCompatibleTargetParent(attached, index))
                errors.Add($"component {index} requires a selected enemy but the card shell has no target");

            if (shell.Type == GeneratedCardType.Power && CardEffectRules.IsSelfCardMovementOrReplay(operation))
                errors.Add($"component {index} cannot be installed on a Power shell");
            if (shell.Type != GeneratedCardType.Power
                && operation.Scope is OperationScope.AbilityTrigger or OperationScope.AbilityRule
                && operation.Template is not ("CL:AfterTurns" or "CL:DieOnUnblockedAttack"))
                errors.Add($"component {index} requires a Power shell");
        }

        if (!ComponentAssemblyGenerator.HasValidTinkeringAssembly(attached))
            errors.Add("the component order violates a generated-card assembly invariant");
        if (!CardEffectRules.HasValidGrandFinaleCost(shell.Cost, shell.StarCost, shell.HasStarCostX, attached))
            errors.Add("the shell cost is incompatible with the installed Grand Finale component");
        if (!CardEffectRules.HasValidReturnThisToHandCost(shell.Cost, shell.StarCost,
                shell.HasStarCostX, attached))
            errors.Add("the shell cost is incompatible with returning this card to hand");
        if (!CardEffectRules.HasValidNumericSelfCostReductionAmounts(shell.Cost, attached))
            errors.Add("a self cost-reduction component exceeds the shell's printed cost");

        return errors.Count == 0
            ? CardTinkeringValidationResult.Valid
            : new CardTinkeringValidationResult(false, errors.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static GeneratedCard Rebuild(GeneratedCard shell, IReadOnlyList<GeneratorOperation> operations,
        IReadOnlyList<CardUpgradeEffect>? operationUpgrades = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(operations);
        var attached = OperationRuntimeSpecCompiler.Attach(operations);
        var validation = Validate(shell, attached);
        if (!validation.IsValid)
            throw new ArgumentException(string.Join("; ", validation.Errors), nameof(operations));

        var retainedShellEffects = shell.Upgrade?.Effects
            .Where(effect => effect.OperationIndex is null).ToArray() ?? [];
        var effects = retainedShellEffects.Concat(operationUpgrades ?? []).ToArray();
        CardUpgradePlan? upgrade = null;
        if (shell.Upgrade is { } originalUpgrade)
        {
            var upgradedOperations = CardUpgradeGenerator.ApplyEffectsToOperations(attached, effects);
            upgrade = originalUpgrade with
            {
                Effects = effects,
                UpgradedChineseDescription = CardDescriptionRenderer.Render(upgradedOperations),
                UpgradedEnglishDescription = EnglishCardDescriptionRenderer.Render(upgradedOperations)
            };
        }

        return OperationRuntimeSpecCompiler.Attach(shell with
        {
            Operations = attached,
            ChineseDescription = CardDescriptionRenderer.Render(attached),
            EnglishDescription = EnglishCardDescriptionRenderer.Render(attached),
            Upgrade = upgrade
        });
    }

    private static CardTinkeringComponentValue EvaluateComponent(GeneratorOperation operation, int index)
    {
        var isTrigger = operation.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger
            || CardEffectRules.IsDependencyPrefix(operation);
        double? multiplier = null;
        if (isTrigger)
        {
            multiplier = operation.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger
                ? EffectBalanceModel.RelativeTriggerFrequency(operation)
                : 1d;
        }

        var atomic = CardEffectRules.IsBeneficialEffect(operation)
            ? EffectBalanceModel.EstimatedEffectValue(operation)
            : 0d;
        // A chip keeps the same price wherever it is moved. Atomic valuation is deliberately independent of the
        // source card, its old trigger discount, and its destination; this also charges the larger unconditional
        // value for effects which were only conditionally valuable on their generated card.
        var value = isTrigger ? 0 : Math.Max(0, (int)Math.Ceiling(atomic));
        return new CardTinkeringComponentValue(index, value, multiplier, isTrigger,
            CardEffectRules.IsHealingOrMaxHp(operation)
            || CardEffectRules.IsCombatBaseDamageIncrease(operation)
            || operation.Template == "D:IncreaseThisCardBlockRun");
    }

    private static double CurrentComponentValue(IReadOnlyList<GeneratorOperation> operations,
        IReadOnlyList<CardTinkeringComponentValue> components)
    {
        double total = 0d;
        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];
            if (component.IsTrigger || component.Value <= 0) continue;

            var multiplier = 1d;
            var cursor = index;
            var visited = new HashSet<int>();
            while (operations[cursor].Parameters.TryGetValue("triggerIndex", out var parentIndex)
                   && parentIndex >= 0 && parentIndex < cursor && visited.Add(parentIndex))
            {
                multiplier *= components[parentIndex].Multiplier ?? 1d;
                cursor = parentIndex;
            }
            total += component.Value * Math.Max(0d, multiplier);
        }
        return total;
    }

    private static double OrdinaryUpperBound(GeneratedCard card, IReadOnlyList<GeneratorOperation> operations,
        bool balancedValues)
    {
        var templateEnergyCost = BudgetTemplateEnergyCost(card, operations);
        var effectiveCost = ResourceEconomyModel.BudgetEffectiveCost(templateEnergyCost, card.StarCost,
            templateEnergyCost < 0, card.HasStarCostX, operations);
        if (double.IsNaN(effectiveCost)) effectiveCost = 1d;
        var hasPrintedResourceCost = templateEnergyCost != 0 || card.StarCost > 0 || card.HasStarCostX;
        var fields = Math.Max(1, EffectBalanceModel.PositiveRewardFieldCount(
            operations, hasPrintedResourceCost, card.Type, card.Tags));
        var powerFactor = ComponentAssemblyGenerator.PowerOneShotBudgetFactorForTinkering(operations, card.Type);
        var bounds = ComponentAssemblyGenerator.WholeCardBudgetBounds(card.Rarity, effectiveCost, fields,
            powerFactor: powerFactor, balancedValues: balancedValues, character: card.Character);
        var upper = bounds.Maximum;
        if (operations.Any(CardEffectRules.IsZeroCostCopyThisCardToDiscard))
        {
            var zero = ComponentAssemblyGenerator.WholeCardBudgetBounds(card.Rarity, 0d, fields,
                powerFactor: powerFactor, balancedValues: balancedValues, character: card.Character);
            upper = CopyThisCardValuation.BlendWithZeroCostEnvelope(upper, zero.Maximum);
        }
        return upper;
    }

    private static int BudgetTemplateEnergyCost(GeneratedCard card,
        IReadOnlyList<GeneratorOperation> operations) => card.Tags.Contains(CardTag.Sly)
        ? SlyKeywordTuning.ValidationTemplateCost(card.Cost, card.StarCost, card.HasStarCostX,
            card.Rarity, card.Type, card.Tags, operations)
        : card.Cost;

    private static bool HasCompatibleTargetParent(IReadOnlyList<GeneratorOperation> operations, int index)
    {
        if (!operations[index].Parameters.TryGetValue("triggerIndex", out var triggerIndex)
            || triggerIndex < 0 || triggerIndex >= index)
            return false;
        return CardEffectRules.CanResolveTriggeredEnemyTarget(operations[triggerIndex], operations[index]);
    }
}
