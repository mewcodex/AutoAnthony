using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChaosCardGenerator;

/// <summary>Controls one read-only valuation pass. A resolved X is valid only for an Energy-X or Star-X card.</summary>
public sealed record CardTinkeringEvaluationOptions(bool BalancedValues = true, int? ResolvedX = null);

/// <summary>Allows an editor to validate an intentionally incomplete drag/drop projection.</summary>
public sealed record CardTinkeringValidationOptions(
    bool AllowIncompleteTriggers = false,
    bool AllowOrphanSelectors = false,
    bool AllowMissingSelectors = false);

/// <summary>Exact production-model contribution and cost data for one operation in its current card context.</summary>
public sealed record CardTinkeringComponentAnalysis(
    int OperationIndex,
    double AtomicValue,
    double ContextualValue,
    double TriggerMultiplier,
    bool IsTrigger,
    bool UsesContextualValue,
    bool IsSingleUseCandidate,
    bool IsNegative,
    double DownsideMultiplier,
    double LinearCompensation,
    double ExpectedResourceRefund);

/// <summary>Exact whole-card terms used by the generator's final budget comparison.</summary>
public sealed record CardTinkeringBudgetBreakdown(
    double PositiveValue,
    double LinearCompensation,
    double DownsideMultiplier,
    double NetValue,
    double OrdinaryUpperBound,
    double EffectiveCost,
    int PositiveRewardFields);

/// <summary>One complete, immutable analysis of a generated card.</summary>
public sealed record CardTinkeringCardAnalysis(
    CardTinkeringBudgetBreakdown Budget,
    IReadOnlyList<CardTinkeringComponentAnalysis> Components)
{
    public bool ExceedsOrdinaryUpperBound => Budget.NetValue > Budget.OrdinaryUpperBound + 0.0001d;
}

public sealed record CardTinkeringValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static CardTinkeringValidationResult Valid { get; } = new(true, Array.Empty<string>());
}

/// <summary>A stable palette entry and every built-in character catalog which owns it.</summary>
public sealed record CardTinkeringComponentPrototype(
    string Id,
    GeneratorOperation Operation,
    IReadOnlyList<GeneratedCharacter> Characters,
    CardReferenceRequirement CardReference);

/// <summary>A scalar which a freeform editor may safely replace without changing the operation's behavior kind.</summary>
public sealed record CardTinkeringEditableValue(
    string Id,
    int Value,
    bool IsXOffset,
    int Minimum,
    int Maximum);

/// <summary>
/// Localization-independent integration surface for card editors. All valuation methods delegate directly to the
/// production model; consumers never need reflection or a copied list of specially priced components.
/// </summary>
public static class CardTinkeringApi
{
    public const int ApiVersion = 5;
    private const int PayloadSchema = 2;
    private const double StarEnergyEquivalent = 0.5d;

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

    /// <summary>
    /// Enumerates the actual built-in component catalogs. Entries shared by multiple characters are returned once
    /// with all owning characters, so companion editors do not need to duplicate or scrape generator data.
    /// </summary>
    public static IReadOnlyList<CardTinkeringComponentPrototype> GetComponentPrototypes()
    {
        var entries = new Dictionary<string, (GeneratorOperation Operation, HashSet<GeneratedCharacter> Owners,
            CardReferenceRequirement CardReference)>(
            StringComparer.Ordinal);
        foreach (var character in Enum.GetValues<GeneratedCharacter>())
        foreach (var atom in CharacterComponentCatalogs.Get(character).Atoms)
        {
            var operation = CreatePrototypeOperation(atom);
            var id = atom.SemanticId ?? OperationRuntimeSpecCompiler.StructuralExactKey(operation);
            var key = id + "\u001f" + OperationRuntimeSpecCompiler.StructuralExactKey(operation);
            if (!entries.TryGetValue(key, out var entry))
                entries[key] = (operation, [character], atom.CardReference);
            else
                entry.Owners.Add(character);
        }
        return entries.OrderBy(pair => pair.Value.Operation.Scope)
            .ThenBy(pair => pair.Value.Operation.Template, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new CardTinkeringComponentPrototype(pair.Key, pair.Value.Operation,
                pair.Value.Owners.OrderBy(owner => owner).ToArray(), pair.Value.CardReference))
            .ToArray();
    }

    public static IReadOnlyList<CardTinkeringEditableValue> GetEditableValues(GeneratorOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return OperationRuntimeSpecCompiler.GetOrCompile(operation).Values
            .Where(slot => slot.Explicit && slot.Source is "fixed" or "energy_x" or "star_x" or "special_x")
            .Select(slot => slot.Source == "fixed"
                ? new CardTinkeringEditableValue(slot.Id, slot.BaseValue + slot.Offset, false, 0, 99999)
                : new CardTinkeringEditableValue(slot.Id, slot.Offset, true, -99, 999))
            .ToArray();
    }

    /// <summary>Replaces one editor-visible fixed value or X offset and rerenders both localized descriptions.</summary>
    public static bool TrySetEditableValue(GeneratorOperation operation, string slotId, int value,
        out GeneratorOperation updated)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        var values = spec.Values.ToArray();
        var index = Array.FindIndex(values, slot => slot.Id == slotId && slot.Explicit
            && slot.Source is "fixed" or "energy_x" or "star_x" or "special_x");
        if (index < 0)
        {
            updated = operation;
            return false;
        }
        var slot = values[index];
        if (slot.Source == "fixed")
        {
            value = Math.Clamp(value, 0, 99999);
            if (slot.BaseValue + slot.Offset == value) { updated = operation; return true; }
            return OperationRuntimeSpecCompiler.TryReplaceFixedValue(operation, slotId, value, out updated);
        }
        else
        {
            value = Math.Clamp(value, -99, 999);
            if (slot.Offset == value)
            {
                updated = operation;
                return true;
            }
            values[index] = slot with { BaseValue = 0, Offset = value };
        }
        var updatedSpec = spec with { Values = values };
        updatedSpec.Validate();
        var chinese = operation.LocalizedText?.RenderChinese(updatedSpec) ?? operation.ChineseText;
        updated = operation with { ChineseText = chinese, RuntimeSpec = updatedSpec };
        return true;
    }

    public static CardTinkeringCardAnalysis Analyze(GeneratedCard card,
        CardTinkeringEvaluationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        options ??= new CardTinkeringEvaluationOptions();
        var isVariableX = IsVariableX(card);
        if (options.ResolvedX is not null && !isVariableX)
            throw new ArgumentException("ResolvedX can only be supplied for an Energy-X or Star-X card.",
                nameof(options));

        var resolvedX = options.ResolvedX is { } value ? Math.Max(0, value) : (int?)null;
        var operations = OperationRuntimeSpecCompiler.Attach(card.Operations);
        if (resolvedX is { } x) operations = VariableXCardBalance.MaterializeOperations(operations, x);

        var templateEnergyCost = BudgetTemplateEnergyCost(card, operations);
        var energyCost = resolvedX is { } resolvedEnergy && card.Cost < 0
            ? resolvedEnergy
            : templateEnergyCost;
        var starCost = resolvedX is { } resolvedStars && card.HasStarCostX
            ? resolvedStars
            : card.StarCost;
        var hasEnergyX = resolvedX is null && templateEnergyCost < 0;
        var hasStarX = resolvedX is null && card.HasStarCostX;
        var hasPrintedResourceCost = resolvedX is not null || templateEnergyCost != 0
            || card.StarCost > 0 || card.HasStarCostX;

        var positive = EffectBalanceModel.EstimatedPositiveCardValue(
            operations, hasPrintedResourceCost, card.Type, card.Tags);
        // This operation's atomic value represents one Energy. The complete card analysis owns the shell cost and
        // therefore expands the later-play saving to the actual fixed printed Energy cost.
        if (energyCost != 1)
        {
            foreach (var (operation, index) in operations.Select((operation, index) => (operation, index))
                         .Where(item => item.operation.Template == "D:SetThisCardCostZero"))
                positive += EffectBalanceModel.EstimatedContextualOperationValueForAudit(operation, index,
                    operations, hasPrintedResourceCost, card.Type, card.Tags) * (Math.Max(0, energyCost) - 1d);
        }

        var linear = NegativeEffectTuning.TotalLinearCompensationValue(operations, card.Rarity);
        var multiplier = Math.Max(1d, CardEffectRules.NegativeEffectCompensationPercent(
            operations, card.Tags, hasPrintedResourceCost, card.Type,
            card.UnifiedChaos ? null : card.Character) / 100d);
        var effectiveCost = TinkeringBudgetEffectiveCost(energyCost, starCost,
            hasEnergyX, hasStarX, operations);
        if (double.IsNaN(effectiveCost)) effectiveCost = 1d;
        var fields = Math.Max(1, EffectBalanceModel.PositiveRewardFieldCount(
            operations, hasPrintedResourceCost, card.Type, card.Tags));
        var upper = OrdinaryUpperBound(card, operations, effectiveCost, fields, options.BalancedValues);
        var budget = new CardTinkeringBudgetBreakdown(positive, linear, multiplier,
            (positive - linear) / multiplier, upper, effectiveCost, fields);
        var components = operations.Select((operation, index) => AnalyzeComponent(
            card, operations, index, hasPrintedResourceCost)).ToArray();
        return new CardTinkeringCardAnalysis(budget, components);
    }

    public static CardTinkeringCardAnalysis AnalyzeAtX(GeneratedCard card, int resolvedX,
        bool balancedValues = true) => Analyze(card,
        new CardTinkeringEvaluationOptions(balancedValues, Math.Max(0, resolvedX)));

    public static CardTinkeringComponentAnalysis AnalyzeStandaloneComponent(GeneratorOperation operation,
        GeneratedRarity rarity = GeneratedRarity.Common)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var operations = OperationRuntimeSpecCompiler.Attach([operation]);
        return AnalyzeComponent(null, operations, 0, hasPrintedResourceCost: true, rarity);
    }

    public static bool IsVariableX(GeneratedCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return card.Cost < 0 || card.HasStarCostX;
    }

    public static double GetEffectiveCost(GeneratedCard card, int? resolvedX = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (IsVariableX(card) && resolvedX is null) return double.NaN;
        if (!IsVariableX(card) && resolvedX is not null)
            throw new ArgumentException("Resolved X is only valid for an X-cost card.", nameof(resolvedX));
        return Analyze(card, new CardTinkeringEvaluationOptions(true, resolvedX)).Budget.EffectiveCost;
    }

    public static double GetKeywordValue(CardTag tag) =>
        Math.Max(0d, EffectBalanceModel.EstimatedPositiveKeywordValue([tag]));

    public static CardTinkeringValidationResult Validate(GeneratedCard shell,
        IReadOnlyList<GeneratorOperation> operations, CardTinkeringValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(operations);
        options ??= new CardTinkeringValidationOptions();
        var attached = OperationRuntimeSpecCompiler.Attach(operations);
        var errors = new List<string>();

        errors.AddRange(SelectorErrors(attached, options));
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
                && !HasCompatibleTargetAncestor(attached, index))
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
        errors.AddRange(ContextualAssemblyErrors(shell, attached, options));
        if (shell.Type == GeneratedCardType.Attack && !attached.Any(CardEffectRules.IsEnemyDamage))
            errors.Add("an Attack shell requires at least one Damage component");
        if (!CardEffectRules.HasValidHitEnemyDamageAssembly(attached))
            errors.Add("a hit-enemy damage component requires a lightning-evoked trigger");
        if (!CardEffectRules.HasValidEventAmountAssembly(attached))
            errors.Add("an event-amount component requires its matching event trigger");
        if (!options.AllowIncompleteTriggers)
        for (var index = 0; index < attached.Count; index++)
        {
            var owner = attached[index];
            if (!CardEffectRules.TriggerNeedsLinkedEffect(owner) && !CardEffectRules.IsDependencyPrefix(owner))
                continue;
            var hasDirectChild = CardEffectRules.IsDependencyPrefix(owner)
                ? index + 1 < attached.Count
                  && CardEffectRules.IsLegalDependencyPayoff(owner, attached[index + 1])
                  && (owner.Scope == OperationScope.ConditionalTrigger
                      ? attached[index + 1].Parameters.GetValueOrDefault("triggerIndex", -1) == index
                      : owner.Parameters.GetValueOrDefault("triggerIndex", -1)
                        == attached[index + 1].Parameters.GetValueOrDefault("triggerIndex", -1))
                : attached.Any(operation => operation.Parameters.GetValueOrDefault("triggerIndex", -1) == index);
            if (!hasDirectChild)
                errors.Add($"trigger component {index} has no linked effect");
        }

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

    private static GeneratorOperation CreatePrototypeOperation(ComponentAtom atom)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(atom);
        if (atom.Template == "N:RetaliateDamage")
        {
            var standalone = OperationRuntimeSpecCompiler.StandaloneRetaliateDamage(spec);
            var damage = standalone.Values.FirstOrDefault(slot => slot.Id == "damage") is { } slot
                ? Math.Max(0, slot.BaseValue + slot.Offset)
                : 0;
            return new GeneratorOperation(atom.Template, atom.Scope, $"对攻击者造成{damage}点伤害。",
                new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget,
                RuntimeSpec: standalone);
        }
        var localized = atom.LocalizedText
                        ?? (ComponentLocalizationApi.TryGet(atom.SemanticId, out var registered)
                            ? registered
                            : null);
        var cardTargetSlot = atom.CardReference switch
        {
            CardReferenceRequirement.ThisCard => "thisCard",
            CardReferenceRequirement.HandCard or CardReferenceRequirement.HandAttack => "card1",
            _ => null
        };
        return new GeneratorOperation(atom.Template, atom.Scope,
            localized?.RenderChinese(spec) ?? atom.ChineseText,
            new Dictionary<string, int>(), CardTargetSlot: cardTargetSlot,
            RequiresSingleTarget: atom.RequiresSingleTarget,
            RuntimeSpec: spec, LocalizedText: localized,
            LocalizationId: ComponentLocalizationApi.TryGet(atom.SemanticId, out _) ? atom.SemanticId : null);
    }

    private static CardTinkeringComponentAnalysis AnalyzeComponent(GeneratedCard? shell,
        IReadOnlyList<GeneratorOperation> operations, int index, bool hasPrintedResourceCost,
        GeneratedRarity? standaloneRarity = null)
    {
        var operation = operations[index];
        var isTrigger = operation.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger
            || CardEffectRules.IsDependencyPrefix(operation);
        var triggerMultiplier = isTrigger ? ResolveTriggerMultiplier(operation, index, operations) : 1d;
        var beneficial = CardEffectRules.IsBeneficialEffect(operation);
        var atomic = beneficial ? Math.Max(0d, EffectBalanceModel.EstimatedEffectValue(operation)) : 0d;
        var contextual = 0d;
        if (!isTrigger && beneficial)
        {
            contextual = shell is null
                ? EffectBalanceModel.EstimatedCardLevelRewardValueForAudit(operation, index, operations)
                : EffectBalanceModel.EstimatedContextualOperationValueForAudit(operation, index, operations,
                    hasPrintedResourceCost, shell.Type, shell.Tags);
            if (Math.Abs(contextual) < 0.0001d && CardEffectRules.IsSelfManagedStateEffect(operation))
                contextual = EffectBalanceModel.EstimatedCardLevelRewardValueForAudit(operation, index, operations);
            if (operation.Template == "D:SetThisCardCostZero" && shell is not null)
                contextual *= Math.Max(0, shell.Cost);
        }

        var isNegative = CardEffectRules.IsNegativeEffect(operation);
        var rarity = shell?.Rarity ?? standaloneRarity ?? GeneratedRarity.Common;
        var downsideMultiplier = isNegative
            ? Math.Max(1d, NegativeEffectTuning.EffectiveMultiplier(operation, operations, index))
            : 1d;
        var linear = isNegative
            ? Math.Max(0d, NegativeEffectTuning.LinearCompensationValue(operation, rarity, operations, index))
            : 0d;
        var refund = ExpectedRefundWithAncestors(operation, index, operations);
        var usesContextual = !isTrigger && beneficial && (Math.Abs(contextual - atomic) > 0.0001d
            || EffectBalanceModel.HasContextualModifierValuationForAudit(operation)
            || CardEffectRules.IsCopyThisCardToDiscard(operation)
            || CardEffectRules.IsEnergyGainOperation(operation)
            || CardEffectRules.IsStarGainOperation(operation));

        return new CardTinkeringComponentAnalysis(index, atomic, contextual, triggerMultiplier,
            isTrigger, usesContextual,
            CardEffectRules.IsHealingOrMaxHp(operation)
            || CardEffectRules.IsCombatBaseDamageIncrease(operation)
            || operation.Template == "D:IncreaseThisCardBlockRun",
            isNegative, downsideMultiplier, linear, refund);
    }

    private static double ResolveTriggerMultiplier(GeneratorOperation trigger, int triggerIndex,
        IReadOnlyList<GeneratorOperation> operations)
    {
        var childIndex = Enumerable.Range(triggerIndex + 1, operations.Count - triggerIndex - 1)
            .FirstOrDefault(index => operations[index].Parameters.GetValueOrDefault("triggerIndex", -1)
                == triggerIndex, -1);
        if (childIndex < 0 && CardEffectRules.IsDependencyPrefix(trigger)
            && triggerIndex + 1 < operations.Count
            && CardEffectRules.IsLegalDependencyPayoff(trigger, operations[triggerIndex + 1]))
            childIndex = triggerIndex + 1;
        return childIndex >= 0
            ? EffectBalanceModel.LinkedResolutionMultiplierForAudit(
                operations[childIndex], childIndex, operations)
            : EffectBalanceModel.RelativeTriggerFrequency(trigger);
    }

    private static double OrdinaryUpperBound(GeneratedCard card, IReadOnlyList<GeneratorOperation> operations,
        double effectiveCost, int fields, bool balancedValues)
    {
        var powerFactor = ComponentAssemblyGenerator.PowerOneShotBudgetFactorForTinkering(operations, card.Type);
        var bounds = ComponentAssemblyGenerator.WholeCardBudgetBounds(card.Rarity, effectiveCost, fields,
            powerFactor: powerFactor, balancedValues: balancedValues, character: card.Character);
        var upper = bounds.Maximum;
        if (!operations.Any(CardEffectRules.IsZeroCostCopyThisCardToDiscard)) return upper;
        var zero = ComponentAssemblyGenerator.WholeCardBudgetBounds(card.Rarity, 0d, fields,
            powerFactor: powerFactor, balancedValues: balancedValues, character: card.Character);
        return CopyThisCardValuation.BlendWithZeroCostEnvelope(upper, zero.Maximum);
    }

    private static int BudgetTemplateEnergyCost(GeneratedCard card,
        IReadOnlyList<GeneratorOperation> operations) => card.Tags.Contains(CardTag.Sly)
        ? SlyKeywordTuning.ValidationTemplateCost(card.Cost, card.StarCost, card.HasStarCostX,
            card.Rarity, card.Type, card.Tags, operations)
        : card.Cost;

    private static double TinkeringBudgetEffectiveCost(int energyCost, int starCost, bool hasEnergyX,
        bool hasStarX, IReadOnlyList<GeneratorOperation> operations)
    {
        if (hasEnergyX || hasStarX || energyCost < 0) return double.NaN;
        var strippedParents = operations.Select(operation => operation with
        {
            Parameters = operation.Parameters.Where(pair => pair.Key != "triggerIndex")
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        }).ToArray();
        var energyRefund = 0d;
        var starRefund = 0d;
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            var refund = ExpectedRefundWithAncestors(operation, index, operations, strippedParents);
            if (CardEffectRules.IsEnergyGainOperation(operation)
                || operation.Template == "I:ProxyAtomic_DoubleEnergy")
                energyRefund += refund;
            else if (CardEffectRules.IsStarGainOperation(operation))
                starRefund += refund;
        }

        var strength = ResourceEconomyModel.BudgetStrength(Math.Max(0, energyCost) - energyRefund)
            + Math.Max(0, starCost) * StarEnergyEquivalent - starRefund;
        return ResourceEconomyModel.CostForBudgetStrength(strength);
    }

    private static double ExpectedRefundWithAncestors(GeneratorOperation operation, int operationIndex,
        IReadOnlyList<GeneratorOperation> operations, IReadOnlyList<GeneratorOperation>? strippedParents = null)
    {
        strippedParents ??= operations.Select(candidate => candidate with
        {
            Parameters = candidate.Parameters.Where(pair => pair.Key != "triggerIndex")
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        }).ToArray();
        var refund = ResourceEconomyModel.ExpectedRefund(operation, strippedParents);
        if (refund <= 0d) return 0d;

        var cursor = operation.Parameters.GetValueOrDefault("triggerIndex", -1);
        var visited = new HashSet<int>();
        while (cursor >= 0 && cursor < operationIndex && visited.Add(cursor)
               && operations[cursor].Parameters.TryGetValue("triggerIndex", out var parentIndex)
               && parentIndex >= 0 && parentIndex < cursor)
        {
            refund *= StandaloneTriggerRefundFactor(parentIndex, strippedParents, operation);
            cursor = parentIndex;
        }

        var immediateOwner = operation.Parameters.GetValueOrDefault("triggerIndex", -1);
        if (operationIndex > 0 && operationIndex - 1 != immediateOwner
            && CardEffectRules.IsDependencyPrefix(operations[operationIndex - 1])
            && CardEffectRules.IsLegalDependencyPayoff(operations[operationIndex - 1], operation))
        {
            var prefixIndex = operationIndex - 1;
            var prefix = operations[prefixIndex];
            var frequency = EffectBalanceModel.RelativeTriggerFrequency(prefix);
            refund *= EffectBalanceModel.DependencyResolutionCountForAudit(
                prefix, prefixIndex, operations, Math.Max(0d, frequency));
        }
        return refund;
    }

    private static double StandaloneTriggerRefundFactor(int triggerIndex,
        IReadOnlyList<GeneratorOperation> strippedParents, GeneratorOperation refundOperation)
    {
        var parameters = refundOperation.Parameters.Where(pair => pair.Key != "triggerIndex")
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        parameters["triggerIndex"] = triggerIndex;
        var probe = refundOperation with { Parameters = parameters };
        var rawResource = RawResourceUnits(refundOperation);
        return rawResource <= 0d ? 1d
            : ResourceEconomyModel.ExpectedRefund(probe, strippedParents) / rawResource;
    }

    private static double RawResourceUnits(GeneratorOperation operation)
    {
        if (operation.Template == "I:ProxyAtomic_DoubleEnergy") return 2d;
        var isEnergy = CardEffectRules.IsEnergyGainOperation(operation);
        var isStars = CardEffectRules.IsStarGainOperation(operation);
        if (!isEnergy && !isStars) return 0d;
        var slot = OperationRuntimeSpecCompiler.GetOrCompile(operation).Values
            .FirstOrDefault(candidate => candidate.Id == (isEnergy ? "energy" : "stars"));
        if (slot is null || slot.Source != "fixed") return 0d;
        return Math.Max(0d, slot.BaseValue + slot.Offset) * (isStars ? StarEnergyEquivalent : 1d);
    }

    private static bool HasCompatibleTargetAncestor(IReadOnlyList<GeneratorOperation> operations, int index)
    {
        var cursor = index;
        var visited = new HashSet<int>();
        while (operations[cursor].Parameters.TryGetValue("triggerIndex", out var triggerIndex)
               && triggerIndex >= 0 && triggerIndex < cursor && visited.Add(triggerIndex))
        {
            if (CardEffectRules.CanResolveTriggeredEnemyTarget(operations[triggerIndex], operations[index]))
                return true;
            cursor = triggerIndex;
        }
        return false;
    }

    private static IReadOnlyList<string> ContextualAssemblyErrors(GeneratedCard shell,
        IReadOnlyList<GeneratorOperation> operations, CardTinkeringValidationOptions options)
    {
        var errors = new List<string>();
        if (!CardEffectRules.HasValidPlayThisCardAssembly(operations))
            errors.Add("play-this-card components require their matching exhaust-pile trigger and an earlier play effect");
        var shellUsesX = shell.Cost < 0 || shell.HasStarCostX;
        if (operations.Any(CardEffectRules.UsesX) && !shellUsesX)
            errors.Add("X components require an X-cost shell");
        if (shellUsesX && operations.Any(CardEffectRules.IsSelfCostChange))
            errors.Add("X-cost shells cannot contain self-cost-change components");
        if (shell.Cost == 0 && !shell.HasStarCostX
            && operations.Any(CardEffectRules.IsSelfCostReduction))
            errors.Add("zero-cost shells cannot contain self-cost-reduction components");
        if ((shell.Type == GeneratedCardType.Power || shell.Tags.Contains(CardTag.Exhaust))
            && operations.Any(IsOnPlaySelfCostChange))
            errors.Add("on-play self-cost-change components require a reusable non-Power shell");
        if (shell.Cost == 0 && shell.StarCost <= 0 && !shell.HasStarCostX
            && operations.Any(CardEffectRules.IsZeroCostCopyThisCardToDiscard))
            errors.Add("a free shell cannot create a zero-cost copy of itself");
        if (shell.Type == GeneratedCardType.Power && operations.Any(CardEffectRules.IsNextAttackGrantTrigger))
            errors.Add("next-Attack grant components cannot be installed on a Power shell");

        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            var previous = operations.Take(index).ToArray();
            var previousImmediate = index > 0 ? operations[index - 1] : null;
            var triggerIndex = operation.Parameters.GetValueOrDefault("triggerIndex", -1);
            var trigger = triggerIndex >= 0 && triggerIndex < index ? operations[triggerIndex] : null;

            if (CardEffectRules.IsDependencyPrefix(operation))
            {
                var hasPayoff = index + 1 < operations.Count
                                && CardEffectRules.IsLegalDependencyPayoff(operation, operations[index + 1]);
                var ownerMatches = hasPayoff && (operation.Scope == OperationScope.ConditionalTrigger
                    ? operations[index + 1].Parameters.GetValueOrDefault("triggerIndex", -1) == index
                    : operation.Parameters.GetValueOrDefault("triggerIndex", -1)
                      == operations[index + 1].Parameters.GetValueOrDefault("triggerIndex", -1));
                if ((!hasPayoff || !ownerMatches) && !options.AllowIncompleteTriggers)
                    errors.Add("a dependency prefix must immediately contain its legal payoff");
            }
            if (CardEffectRules.RequiresDependencyPrefix(operation)
                && (index == 0 || !CardEffectRules.IsDependencyPrefix(operations[index - 1])
                    || !CardEffectRules.IsLegalDependencyPayoff(operations[index - 1], operation)))
                errors.Add($"component {operation.Template} requires its adjacent dependency prefix");

            if (operation.Template is "CL:IfHandEmpty" or "CL:IfNoAttacksInHand" or "C:ifTargetPoisoned"
                && index != 0)
                errors.Add("a play-start hand/target gate must be the first component");
            if (operation.Template == "D:IncreaseThisCardCost"
                && (shell.Type == GeneratedCardType.Power || shell.Tags.Contains(CardTag.Exhaust)
                    || trigger is not null))
                errors.Add("increase-this-card-cost must be an immediate component on a reusable non-Power shell");
            if (operation.Template == "D:IncreaseAllClaws"
                && (shell.Type == GeneratedCardType.Power || shell.Tags.Contains(CardTag.Exhaust)
                    || trigger is not null))
                errors.Add("same-name combat growth must be immediate and cannot use a Power or Exhaust shell");
            if (CardEffectRules.IsCombatBaseDamageIncrease(operation)
                && (shell.Type == GeneratedCardType.Power || shell.Tags.Contains(CardTag.Exhaust)
                    || !previous.Any(CardEffectRules.IsEnemyDamage)))
                errors.Add("combat damage growth requires earlier Damage and a reusable non-Power shell");
            if (operation.Template == "NCR:IncreaseThisCardDamageRun"
                && !previous.Any(CardEffectRules.IsEnemyDamage))
                errors.Add("permanent damage growth requires an earlier Damage component");
            if (operation.Template == "D:IncreaseThisCardBlockRun"
                && !previous.Any(candidate => candidate.Template == "N:B"))
                errors.Add("permanent Block growth requires an earlier Block component");
            if (operation.Template is "NCR:ApplyDoomEqualDamage" or "NCR:DoubleHangDamage"
                    or "CL:GainBlockEqualDamage" or "CL:DamageOtherEnemiesEqual"
                && !previous.Any(CardEffectRules.IsEnemyDamage))
                errors.Add($"component {operation.Template} requires an earlier Damage component");
            if (CardEffectRules.IsFatalCondition(operation)
                && !previous.Any(CardEffectRules.IsEnemyDamage))
                errors.Add("Fatal requires an earlier Damage component");
            if (CardEffectRules.IsForEachExhaustTrigger(operation)
                && !previous.Any(candidate => CardEffectRules.IsCompatiblePriorExhaust(candidate, operation)))
                errors.Add("per-exhaust components require an earlier compatible Exhaust effect");
            if (operation.Template == "C:forEachDiscarded"
                && !previous.Any(candidate => candidate.Template is
                    "N:Discard" or "N:DiscardAll" or "I:DiscardHandDrawSame"))
                errors.Add("per-discard components require an earlier discard effect");
            if (operation.Template == "C:ifLastDrawnSkill"
                && (shell.Type == GeneratedCardType.Power
                    || previousImmediate is not { Template: "N:Draw" }
                    || previousImmediate.Parameters.ContainsKey("triggerIndex")
                    || OperationRuntimeSpecCompiler.FixedValue(previousImmediate, "draw") != 1))
                errors.Add("last-drawn-Skill requires an immediately preceding root Draw 1 component");
            if (operation.Template == "N:RetaliateDamage"
                && (trigger is null || OperationRuntimeSpecCompiler.GetOrCompile(trigger).Trigger
                    is not { Kind: "attack_received", Lifetime: "this_turn" }))
                errors.Add("retaliation Damage requires the this-turn attack-received trigger");
            if (operation.Template == "I:AddExhaustedAttackDamage"
                && previousImmediate?.Template != "I:ExhaustRandomAttack")
                errors.Add("add-exhausted-Attack-Damage requires the adjacent attack-exhaust step");
            if (operation.Template == "I:UpgradeThatCard" && previousImmediate?.Template != "N:Move")
                errors.Add("upgrade-that-card requires the adjacent card-move step");
            if (operation.Template == "CL:ReturnThisToHand"
                && (trigger?.Template != "CL:AtNextTurnStart"
                    || !operations.Take(triggerIndex).Any(CardEffectRules.IsOrdinaryOnPlayEffect)))
                errors.Add("delayed return-to-hand requires its next-turn trigger and an earlier play effect");
            if (operation.Template == "CL:IncreaseRollingDamage"
                && previousImmediate?.Template is not ("N:AllD" or "CL:RollingAllDamage"))
                errors.Add("rolling-Damage growth requires its adjacent area-Damage component");
            if (operation.Template == "D:ForEachOrb" && !previous.Any(CardEffectRules.IsEnemyDamage))
                errors.Add("per-Orb Damage repetition requires an earlier Damage component");
            if (CardEffectRules.IsStandaloneEventDependencyPrefix(operation) && trigger is not null)
                errors.Add("standalone event dependency components cannot be nested under another trigger");
            if (CardEffectRules.IsExtremeLifecycleDownside(operation) && trigger is not null)
                errors.Add("extreme lifecycle downsides cannot be nested below a trigger");
            if (operation.Scope == OperationScope.AbilityRule && trigger is not null)
                errors.Add("Power rule components cannot be nested below a trigger");

            if (operation.Scope != OperationScope.Modifier || CardEffectRules.IsDependencyPrefix(operation))
                continue;
            if (trigger is not null && CardEffectRules.IsSelfZoneStateCondition(trigger))
            {
                errors.Add("card-zone state conditions cannot own passive modifier components");
                continue;
            }
            if (trigger is not null && CardEffectRules.IsNextAttackGrantTrigger(trigger)
                && CardEffectRules.IsNextAttackGrantPayoff(operation))
                continue;
            if (operation.Template is "R:DoubleEnergyX" or "R:DoubleEitherXAtThreshold")
                continue;
            var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
            if (operation.Template == "M:TriggeredAttackDamagePercent")
            {
                var kind = trigger is null ? null : OperationRuntimeSpecCompiler.GetOrCompile(trigger).Trigger?.Kind;
                if (kind is not ("attack_played" or "first_attack_played_each_turn"
                    or "first_zero_cost_attack_played_each_turn" or "nth_attack_played_this_turn"
                    or "next_attack" or "next_attacks_this_turn"))
                    errors.Add("triggered Attack-Damage modifiers require an Attack event trigger");
                continue;
            }
            var hasAnchor = operation.Template == "NCR:DoomPerDoomThreshold"
                ? operations.Any(candidate => candidate.Template == "NCR:ApplyDoom")
                : spec.Flags.Contains("requires_block_anchor", StringComparer.Ordinal)
                    ? operations.Any(candidate => candidate.Template.StartsWith("N:B", StringComparison.Ordinal))
                    : operations.Any(CardEffectRules.IsEnemyDamage);
            if (!hasAnchor)
                errors.Add($"modifier {operation.Template} is missing its earlier numeric effect");
        }

        return errors;
    }

    private static bool IsOnPlaySelfCostChange(GeneratorOperation operation) => operation.Template is
        "D:IncreaseThisCardCost" or "D:SetThisCardCostZero" or "I:ReduceThisCardCostCombat";

    private static IReadOnlyList<string> SelectorErrors(IReadOnlyList<GeneratorOperation> operations,
        CardTinkeringValidationOptions options)
    {
        var errors = new List<string>();
        if (!CardEffectRules.HasNoRestrictedSelfCopyAssembly(operations))
            errors.Add("single-use components cannot be combined with copy-this-card components");
        var selectors = operations.Select((operation, index) => (operation, index, Slot: SelectorSlot(operation)))
            .Where(item => item.operation.Template is "N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK")
            .ToArray();
        foreach (var selector in selectors)
        {
            if (selector.Slot is null) errors.Add("a card selector has an invalid slot index");
            if (selector.operation.Parameters.ContainsKey("triggerIndex"))
                errors.Add("a card selector must be a root component");
        }
        foreach (var duplicate in selectors.Where(item => item.Slot is not null)
                     .GroupBy(item => item.Slot!, StringComparer.Ordinal).Where(group => group.Count() > 1))
            errors.Add($"card selector slot {duplicate.Key} is duplicated");

        var externalSlots = operations.Select(operation => operation.CardTargetSlot)
            .Where(slot => !string.IsNullOrWhiteSpace(slot) && slot != "thisCard")
            .Distinct(StringComparer.Ordinal).ToArray();
        if (externalSlots.Length > 1) errors.Add("the card uses more than one explicit card slot");
        foreach (var operation in operations)
        {
            if (CardEffectRules.NeedsExternalCardSlot(operation))
            {
                var matches = string.IsNullOrWhiteSpace(operation.CardTargetSlot)
                              || operation.CardTargetSlot == "thisCard"
                    ? 0
                    : selectors.Count(selector => selector.Slot == operation.CardTargetSlot);
                if (!options.AllowMissingSelectors && matches != 1)
                    errors.Add($"component {operation.Template} requires one matching card selector");
            }
            if (CardEffectRules.OperationNeedsChoiceContext(operation)
                && operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                && (triggerIndex < 0 || triggerIndex >= operations.Count
                    || !CardEffectRules.TriggerSupportsChoiceContext(operations[triggerIndex])))
                errors.Add($"component {operation.Template} requires a trigger with player-choice context");
        }
        if (!options.AllowOrphanSelectors)
            foreach (var selector in selectors.Where(selector => selector.Slot is not null))
                if (!operations.Any(operation => CardEffectRules.NeedsExternalCardSlot(operation)
                                                 && operation.CardTargetSlot == selector.Slot))
                    errors.Add($"card selector {selector.Slot} has no component to receive its selection");
        return errors;
    }

    private static string? SelectorSlot(GeneratorOperation operation) =>
        operation.Parameters.TryGetValue("slotIndex", out var slotIndex) && slotIndex > 0
            ? $"card{slotIndex}"
            : null;
}
