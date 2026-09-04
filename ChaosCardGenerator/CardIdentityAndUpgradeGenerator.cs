using System.Text.RegularExpressions;
using System.Text;
using System.Runtime.CompilerServices;

namespace ChaosCardGenerator;

public static class CardUpgradeGenerator
{
    private sealed record Candidate(CardUpgradeEffect Effect);
    // This is deliberately independent of rarity: the native-shaped primary upgrade already carries the card's
    // rarity budget. The optional second numeric line is a small, stable rider worth about two ordinary Damage.
    internal const double SupplementalNumericUpgradeBudget = 200d;

    public static GeneratorOperation[] ApplyEffectsToOperations(
        IReadOnlyList<GeneratorOperation> source,
        IReadOnlyList<CardUpgradeEffect> effects)
    {
        var operations = source.ToArray();
        foreach (var effect in effects.Where(effect => effect.OperationIndex is not null))
        {
            var index = effect.OperationIndex!.Value;
            if ((uint)index >= (uint)operations.Length) continue;
            var operation = operations[index];
            var text = operation.ChineseText;
            var runtimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
            var localizedText = operation.LocalizedText;
            if (operation.Template == "N:Discard" && effect.Kind == CardUpgradeKind.ReduceNegativeNumber)
            {
                // Legacy plans may have reduced mandatory discard. New discard upgrades are Silent-only +1 riders;
                // preserve the printed payment for every stale reduction plan.
                continue;
            }
            if (effect.Kind == CardUpgradeKind.UpgradeDerivative)
            {
                // Reference operations name a derivative category, not a produced card instance. For example,
                // “all Shivs in your Exhaust Pile” includes ordinary, upgraded, and enchanted Shivs, so the
                // category itself must never become “Shiv+”. This also neutralizes stale upgrade plans in old
                // snapshots now that such references no longer offer this upgrade candidate.
                if (!DerivativeSlotCatalog.SupportsUpgrade(operation.Template, operation.DerivativeId))
                    continue;
                var derivative = DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template);
                if (derivative is not null)
                {
                    var english = EnglishCardDescriptionRenderer.OperationText(operation);
                    localizedText ??= CompileLocalizedProjection(text, english, runtimeSpec);
                    if (localizedText is null)
                        throw new InvalidOperationException($"Cannot compile derivative upgrade text for "
                                                            + operation.Template + ".");
                    localizedText = DerivativeSlotCatalog.BindLocalizedText(operation, localizedText, english);
                    var enchantment = DerivativeSlotCatalog.ResolveEnchantment(operation.DerivativeId,
                        operation.DerivativeEnchantmentId, operation.Template);
                    localizedText = DerivativeSlotCatalog.SetDerivativeText(localizedText, derivative,
                        enchantment, DerivativeSlotCatalog.EnglishTextUsesPlural(english, operation.Template),
                        upgraded: true);
                    localizedText.Validate(runtimeSpec);
                    text = localizedText.RenderChinese(runtimeSpec);
                }
            }
            else if (effect.Kind == CardUpgradeKind.UpgradeGeneratedCards)
            {
                var english = UpgradeRandomGenerationEnglish(
                    EnglishCardDescriptionRenderer.OperationText(operation));
                text = UpgradeRandomGenerationChinese(text);
                ExternalOperationTextRegistry.Register(operation.Template, text, english);
                localizedText = CompileLocalizedProjection(text, english, runtimeSpec);
            }
            else if (effect.Kind == CardUpgradeKind.ChooseExhaust)
            {
                var attackOnly = runtimeSpec.CardFilter == "attack" || operation.Template == "I:ExhaustRandomAttack";
                text = attackOnly
                    ? "选择你手牌中的一张攻击牌，将其消耗。"
                    : "选择你手牌中的一张牌，将其消耗。";
                var english = attackOnly
                    ? "Choose an Attack in your Hand to Exhaust."
                    : "Choose a card in your Hand to Exhaust.";
                runtimeSpec = OperationRuntimeSpecCompiler.AsSelectedExhaust(runtimeSpec);
                ExternalOperationTextRegistry.Register(operation.Template, text, english);
                localizedText = CompileLocalizedProjection(text, english, runtimeSpec);
            }
            else if (effect.Kind == CardUpgradeKind.IncreaseNumber
                     && CardEffectRules.IsNonUpgradeableNumericMarker(operation))
            {
                // Compatibility guard for snapshots generated before marker values were excluded from upgrades.
                continue;
            }
            else if (effect.Kind == CardUpgradeKind.IncreaseNumber
                     && CardEffectRules.IsMandatoryDiscardOrExhaustNumber(operation)
                     && operation.Template != "N:Discard")
            {
                // Compatibility guard for existing snapshots whose upgrade plan incorrectly made a mandatory
                // discard/exhaust payment larger. Such a plan now leaves the payment unchanged.
                continue;
            }
            else if (effect.Kind == CardUpgradeKind.IncreaseNumber
                     && CardEffectRules.IsCurrentBlockDamageAnchor(source, index))
            {
                // The numeric anchor is interpreter scaffolding for current-Block damage, not a printed reward.
                // Ignore stale snapshot upgrades which attempted to improve this hidden zero value.
                continue;
            }
            else if (effect.Kind == CardUpgradeKind.ReduceThreshold
                     && operation.Template == "R:ReturnAfterSkillsPlayed"
                     && OperationRuntimeSpecCompiler.TryGetFixedUpgradeValue(operation, out _, out var skillThreshold)
                     && skillThreshold + (effect.Delta ?? 0) < 2)
            {
                // Compatibility guard for snapshots generated while Make It So's threshold could upgrade from 2 to
                // 1. A one-Skill threshold is a trivial self-return loop; keep the printed/runtime value at 2.
                continue;
            }
            else if (effect.Delta is { } semanticDelta
                && effect.Kind is CardUpgradeKind.IncreaseNumber or CardUpgradeKind.ReduceSelfDamage
                    or CardUpgradeKind.ReduceThreshold or CardUpgradeKind.ReduceNegativeNumber
                && (effect.ValueSlotId ?? OperationRuntimeSpecCompiler.UpgradeValueSlot(operation)) is { } slotId)
            {
                operation = ApplyNumericDelta(operation, slotId, semanticDelta);
                text = operation.ChineseText;
                runtimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
                localizedText = operation.LocalizedText;
            }
            operations[index] = operation with
            {
                ChineseText = text,
                RuntimeSpec = runtimeSpec,
                LocalizedText = localizedText
            };
        }
        return operations;
    }

    private static OperationLocalizedText? CompileLocalizedProjection(string chinese, string english,
        OperationRuntimeSpec spec) =>
        OperationLocalizedText.TryCompile(chinese, english, spec, out var localized)
            ? localized
            : null;

    /// <summary>
    /// Applies a numeric upgrade from its stable slot identity. Localized text is updated only as the final
    /// presentation projection; slot selection and the resulting runtime value never depend on that text.
    /// </summary>
    private static GeneratorOperation ApplyNumericDelta(GeneratorOperation operation, string slotId, int delta)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        var slot = spec.Values.FirstOrDefault(value => value.Id == slotId)
            ?? throw new InvalidOperationException($"RuntimeSpec has no upgrade slot {slotId} for {operation.Template}.");
        if (slot.Source == "fixed")
        {
            var currentValue = slot.BaseValue + slot.Offset;
            var updatedValue = NumericGenerationTuning.ClampUniversalFixedValue(operation, slotId,
                currentValue + delta);
            delta = updatedValue - currentValue;
            if (!OperationRuntimeSpecCompiler.TryRenderFixedValue(operation, slotId, updatedValue,
                    out var fixedProjection))
                throw new InvalidOperationException(
                    $"Cannot render fixed upgrade slot {slotId} for {operation.Template}: "
                    + $"text={operation.ChineseText}; spec={spec.StableSignature()}.");
            var fixedSpec = OperationRuntimeSpecCompiler.ApplyUpgradeDelta(spec, slotId, delta);
            return operation with { ChineseText = fixedProjection, RuntimeSpec = fixedSpec };
        }

        var updatedSpec = OperationRuntimeSpecCompiler.ApplyUpgradeDelta(spec, slotId, delta);
        var projectedText = operation.LocalizedText?.RenderChinese(updatedSpec)
            ?? OperationRuntimeSpecCompiler.IncreaseLegacyXValue(operation.ChineseText);
        return operation with { ChineseText = projectedText, RuntimeSpec = updatedSpec };
    }

    public static CardUpgradePlan Generate(GeneratedCard card, Random random, bool unifiedChaos = false,
        string? profileId = null, ComponentKeywordPolicy? keywordPolicy = null)
    {
        keywordPolicy ??= ComponentKeywordPolicy.Default;
        var candidates = new List<Candidate>();
        for (var index = 0; index < card.Operations.Count; index++)
        {
            var operation = card.Operations[index];
            var exhaustSpec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
            if (!operation.Parameters.ContainsKey("triggerIndex")
                && (exhaustSpec is { Opcode: "exhaust_card", Variant: "random" }
                    || operation.Template == "I:ExhaustRandomAttack"))
            {
                var attackOnly = exhaustSpec.CardFilter == "attack" || operation.Template == "I:ExhaustRandomAttack";
                candidates.Add(new(new(CardUpgradeKind.ChooseExhaust, index)));
            }
            if (operation.Template == "N:HP-"
                && OperationRuntimeSpecCompiler.TryGetFixedUpgradeValue(operation, out var selfDamageSlot,
                    out var selfDamageValue)
                && selfDamageSlot is not null && selfDamageValue > 1)
            {
                var delta = Math.Min(SelfDamageReduction(selfDamageValue), selfDamageValue - 1);
                candidates.Add(new(new(CardUpgradeKind.ReduceSelfDamage, index, -delta,
                    ValueSlotId: selfDamageSlot)));
                continue;
            }

            // Mandatory discard count is not an ordinary numeric upgrade axis. Silent may receive the explicit
            // rare +1 rider added after normal upgrade budgeting below; every other upgrade route skips it.
            if (operation.Template == "N:Discard")
                continue;

            // Numeric payments and negative durations are costs, not rewards. Their upgrades reduce the value
            // and never remove it; a value of 1 simply uses another upgrade axis. Keep this before the generic
            // numeric +value path.
            if (CardEffectRules.IsReducibleNegativeNumber(operation)
                && !(operation.Template == "N:Discard"
                    && (unifiedChaos || card.Character == GeneratedCharacter.Silent)))
            {
                if (OperationRuntimeSpecCompiler.TryGetFixedUpgradeValue(operation, out var paymentSlot,
                        out var paymentValue)
                    && paymentSlot is not null && paymentValue > 1)
                {
                    candidates.Add(new(new(CardUpgradeKind.ReduceNegativeNumber, index, -1,
                        ValueSlotId: paymentSlot)));
                }
                continue;
            }

            // These values are payment thresholds, not rewards. A better upgrade makes the trigger easier to
            // satisfy, so the first number must decrease. Keep this ahead of the generic AbilityRule upgrade.
            if (operation.Template is "A:ProxyAtomic_Orbit" or "A:whenEnergySpent" or "A:whenOneStarSpent"
                    or "R:ReturnAfterSkillsPlayed")
            {
                if (OperationRuntimeSpecCompiler.TryGetFixedUpgradeValue(operation, out var thresholdSlot,
                        out var thresholdValue)
                    && thresholdSlot is not null && thresholdValue > (operation.Template == "R:ReturnAfterSkillsPlayed" ? 2 : 1))
                {
                    candidates.Add(new(new(CardUpgradeKind.ReduceThreshold, index, -1,
                        ValueSlotId: thresholdSlot)));
                }
                continue;
            }

            var replayCount = operation.Template == "R:PlaySelectedSkillMultipleTimes"
                ? OperationRuntimeSpecCompiler.StaticLiteralValue(operation, "amount", 1)
                : 0;
            var replayUpgradeCost = card.Cost < 0
                ? double.NaN
                : card.Cost + Math.Max(0, card.StarCost) * 0.5d;
            var skipNumericUpgrade = CardEffectRules.IsNonUpgradeableNumericMarker(operation)
                || CardEffectRules.IsCurrentBlockDamageAnchor(card.Operations, index)
                || operation.Template == "R:PlaySelectedSkillMultipleTimes"
                && (replayCount >= 3 || double.IsNaN(replayUpgradeCost) || replayUpgradeCost < 2d
                    || operation.Parameters.ContainsKey("triggerIndex"));
            if (!skipNumericUpgrade && OperationRuntimeSpecCompiler.ValueUsesX(operation))
            {
                candidates.Add(new(new(CardUpgradeKind.IncreaseNumber, index, 1,
                    ValueSlotId: OperationRuntimeSpecCompiler.UpgradeValueSlot(operation))));
                continue;
            }

            if (!skipNumericUpgrade && operation.Template == "I:DrawAndBlockIfSkill")
            {
                if (OperationRuntimeSpecCompiler.TryGetFixedUpgradeValue(operation, out var slotId,
                        out var blockValue)
                    && slotId == "amount")
                {
                    var delta = NativeUpgradeValueModel.SampleIncrease(operation, blockValue, random,
                        NativeUpgradeValueModel.Family.Block);
                    candidates.Add(new(new(CardUpgradeKind.IncreaseNumber, index, delta,
                        ValueSlotId: slotId)));
                }
                continue;
            }

            // This condition is defined only for one just-drawn card; upgrading the draw count breaks its semantics.
            if (!skipNumericUpgrade && operation.Template == "N:Draw"
                && index + 1 < card.Operations.Count
                && card.Operations[index + 1].Template == "C:ifLastDrawnSkill")
                continue;

            // Upgrade resolved operation values only; cost rules, triggers, and X variables are not numeric upgrades.
            if (!skipNumericUpgrade
                && operation.Scope is OperationScope.SingleEnemyOnly or OperationScope.NonTargeted or OperationScope.Modifier or OperationScope.Independent or OperationScope.AbilityRule
                && !OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("cost_wording")
                && !CardEffectRules.IsEnemyStrengthGain(operation)
                && !CardEffectRules.IsMandatoryDiscardOrExhaustNumber(operation)
                && operation.Template is not ("N:HP-" or "N:Discard" or "N:DiscardAll" or "N:LoseDex"
                    or "D:LoseOrbSlots" or "M:DamageMinusPerCardInHand"))
            {
                var hasNumber = OperationRuntimeSpecCompiler.TryGetFixedUpgradeValue(operation,
                    out var valueSlotId, out var upgradeValue) && valueSlotId is not null;
                var value = upgradeValue;
                var directOrbMaximum = 3;
                if (CardEffectRules.IsDirectOrbChannel(operation)
                    && OrbSlotCatalog.ResolveOutput(operation.OrbOutputId, operation.Template) is { } outputOrb)
                {
                    var effectiveOrbCost = card.Cost < 0 ? 1 : Math.Max(0, card.Cost);
                    if (card.HasStarCostX) effectiveOrbCost += 2;
                    else effectiveOrbCost += CardEffectRules.StarCostEnergyEquivalent(card.StarCost);
                    directOrbMaximum = OrbSlotCatalog.MaximumDirectChannelCount(outputOrb, effectiveOrbCost,
                        index > 0 && card.Operations[index - 1].Template == "D:ForEachEnemy");
                }
                if (hasNumber
                    && (!CardEffectRules.IsDirectOrbChannel(operation)
                        || value < directOrbMaximum)
                    && (operation.Template != "NCR:BlockTripleOstyMaxHp" || value < 3))
                {
                    // Channel count scales multiplicatively through evoke/passive effects. Its ordinary upgrade
                    // is therefore always +1 and never raises a generated operation above three Orbs.
                    var delta = operation.Template == "R:PlaySelectedSkillMultipleTimes"
                        ? 1
                        : CardEffectRules.IsDirectOrbChannel(operation)
                        ? 1
                        : NativeUpgradeValueModel.SampleIncrease(operation, value, random);
                    if (NumericGenerationTuning.DurationOnlyStackCap(operation, card.Character, unifiedChaos)
                        is { } durationCap)
                        delta = Math.Min(delta, Math.Max(0, durationCap - value));
                    if (NumericGenerationTuning.UniversalFixedValueCap(operation, valueSlotId!) is { } valueCap)
                        delta = Math.Min(delta, Math.Max(0, valueCap - value));
                    if (operation.Template == "NCR:BlockTripleOstyMaxHp")
                        delta = Math.Min(delta, 3 - value);
                    if (delta <= 0) continue;
                    if (!card.Tags.Contains(CardTag.Sly) && CardEffectRules.IsEnergyGainOperation(operation))
                    {
                        var prospectiveOperations = card.Operations
                            .Select((candidate, candidateIndex) => candidateIndex == index
                                ? ApplyNumericDelta(candidate, valueSlotId!, delta)
                                : candidate)
                            .ToArray();
                        if (SlyKeywordTuning.IsPureImmediateSelfRefund(card.Cost, prospectiveOperations))
                            continue;
                    }
                    candidates.Add(new(new(CardUpgradeKind.IncreaseNumber, index, delta,
                        ValueSlotId: valueSlotId)));
                }
            }

            var derivative = DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template);
            if (derivative is not null && DerivativeSlotCatalog.SupportsUpgrade(operation.Template, operation.DerivativeId))
            {
                candidates.Add(new(new(CardUpgradeKind.UpgradeDerivative, index)));
            }

            if (CardEffectRules.IsRandomCardGeneration(operation))
            {
                var upgradedText = UpgradeRandomGenerationChinese(operation.ChineseText);
                ExternalOperationTextRegistry.Register(operation.Template, upgradedText,
                    UpgradeRandomGenerationEnglish(EnglishCardDescriptionRenderer.OperationText(operation)));
                candidates.Add(new(new(CardUpgradeKind.UpgradeGeneratedCards, index)));
            }

            (IReadOnlyList<CardTag> Added, IReadOnlyList<CardTag> Removed) keywordUpgrade = default;
            var hasKeywordUpgrade = profileId is { Length: > 0 }
                && ExternalOperationUpgradeRegistry.TryGet(profileId, operation.Template, out keywordUpgrade);
            if (!hasKeywordUpgrade)
                hasKeywordUpgrade = unifiedChaos
                    ? ExternalOperationUpgradeRegistry.TryGetUnified(operation.Template, out keywordUpgrade)
                    : ExternalOperationUpgradeRegistry.TryGet(card.Character, operation.Template,
                        out keywordUpgrade);
            if (hasKeywordUpgrade)
            {
                var addedKeywords = keywordUpgrade.Added ?? Array.Empty<CardTag>();
                var operationRemovedKeywords = keywordUpgrade.Removed ?? Array.Empty<CardTag>();
                if (addedKeywords.Contains(CardTag.Innate)
                    && keywordPolicy.AllowsAddition(CardTag.Innate)
                    && !card.Tags.Contains(CardTag.Innate))
                    candidates.Add(new(new(CardUpgradeKind.GrantInnate, index)));
                if (addedKeywords.Contains(CardTag.Retain)
                    && keywordPolicy.AllowsAddition(CardTag.Retain)
                    && !card.Tags.Contains(CardTag.Retain)
                    && !card.Tags.Contains(CardTag.Ethereal))
                    candidates.Add(new(new(CardUpgradeKind.GrantRetain, index)));
                if (operationRemovedKeywords.Contains(CardTag.Exhaust)
                    && keywordPolicy.AllowsRemoval(CardTag.Exhaust)
                    && card.Tags.Contains(CardTag.Exhaust)
                    && !card.Operations.Any(CardEffectRules.IsRestrictedEffect))
                    candidates.Add(new(new(CardUpgradeKind.RemoveExhaust, index)));
                if (operationRemovedKeywords.Contains(CardTag.Ethereal)
                    && keywordPolicy.AllowsRemoval(CardTag.Ethereal)
                    && card.Tags.Contains(CardTag.Ethereal))
                    candidates.Add(new(new(CardUpgradeKind.RemoveEthereal, index)));
            }

            (IReadOnlyList<string> Added, IReadOnlyList<string> Removed) customKeywordUpgrade = default;
            var hasCustomKeywordUpgrade = profileId is { Length: > 0 }
                && ExternalCustomKeywordUpgradeRegistry.TryGet(profileId, operation.Template,
                    out customKeywordUpgrade);
            if (!hasCustomKeywordUpgrade && unifiedChaos)
                hasCustomKeywordUpgrade = ExternalCustomKeywordUpgradeRegistry.TryGetUnified(operation.Template,
                    out customKeywordUpgrade);
            if (hasCustomKeywordUpgrade)
            {
                foreach (var keywordId in customKeywordUpgrade.Added ?? [])
                    if (keywordPolicy.AllowsCustomAddition(keywordId)
                        && !(card.CustomKeywords ?? []).Contains(keywordId, StringComparer.Ordinal)
                        && ComponentKeywordApi.CanUpgradeAdd(keywordId, card))
                        candidates.Add(new(new(CardUpgradeKind.AddCustomKeyword, index,
                            KeywordId: keywordId)));
                foreach (var keywordId in customKeywordUpgrade.Removed ?? [])
                    if (keywordPolicy.AllowsCustomRemoval(keywordId)
                        && (card.CustomKeywords ?? []).Contains(keywordId, StringComparer.Ordinal)
                        && ComponentKeywordApi.CanUpgradeRemove(keywordId, card))
                        candidates.Add(new(new(CardUpgradeKind.RemoveCustomKeyword, index,
                            KeywordId: keywordId)));
            }
        }

        if (keywordPolicy.UseArchetypeUpgradeDefaults
            && keywordPolicy.AllowsAddition(CardTag.Innate)
            && (unifiedChaos || card.Character is GeneratedCharacter.Ironclad or GeneratedCharacter.Silent)
            && card.Type == GeneratedCardType.Power)
            candidates.Add(new(new(CardUpgradeKind.GrantInnate)));
        if (keywordPolicy.UseArchetypeUpgradeDefaults
            && keywordPolicy.AllowsAddition(CardTag.Retain)
            && (unifiedChaos || card.Character == GeneratedCharacter.Silent)
            && !card.Tags.Contains(CardTag.Retain)
            && !card.Tags.Contains(CardTag.Ethereal))
            candidates.Add(new(new(CardUpgradeKind.GrantRetain)));
        if (keywordPolicy.UseArchetypeUpgradeDefaults
            && keywordPolicy.AllowsRemoval(CardTag.Exhaust)
            && (unifiedChaos || card.Character == GeneratedCharacter.Silent)
            && card.Tags.Contains(CardTag.Exhaust)
            && !card.Operations.Any(CardEffectRules.IsRestrictedEffect))
            candidates.Add(new(new(CardUpgradeKind.RemoveExhaust)));

        foreach (var tag in keywordPolicy.GlobalUpgradeAdditions ?? (IEnumerable<CardTag>)Array.Empty<CardTag>())
        {
            if (!keywordPolicy.AllowsAddition(tag) || card.Tags.Contains(tag)) continue;
            if (tag == CardTag.Innate)
                candidates.Add(new(new(CardUpgradeKind.GrantInnate)));
            else if (tag == CardTag.Retain && !card.Tags.Contains(CardTag.Ethereal))
                candidates.Add(new(new(CardUpgradeKind.GrantRetain)));
        }
        foreach (var tag in keywordPolicy.GlobalUpgradeRemovals ?? (IEnumerable<CardTag>)Array.Empty<CardTag>())
        {
            if (!keywordPolicy.AllowsRemoval(tag) || !card.Tags.Contains(tag)) continue;
            if (tag == CardTag.Exhaust && !card.Operations.Any(CardEffectRules.IsRestrictedEffect))
                candidates.Add(new(new(CardUpgradeKind.RemoveExhaust)));
            else if (tag == CardTag.Ethereal)
                candidates.Add(new(new(CardUpgradeKind.RemoveEthereal)));
        }
        foreach (var keywordId in keywordPolicy.GlobalCustomUpgradeAdditions
                 ?? (IEnumerable<string>)Array.Empty<string>())
            if (keywordPolicy.AllowsCustomAddition(keywordId)
                && !(card.CustomKeywords ?? []).Contains(keywordId, StringComparer.Ordinal)
                && ComponentKeywordApi.CanUpgradeAdd(keywordId, card))
                candidates.Add(new(new(CardUpgradeKind.AddCustomKeyword, KeywordId: keywordId)));
        foreach (var keywordId in keywordPolicy.GlobalCustomUpgradeRemovals
                 ?? (IEnumerable<string>)Array.Empty<string>())
            if (keywordPolicy.AllowsCustomRemoval(keywordId)
                && (card.CustomKeywords ?? []).Contains(keywordId, StringComparer.Ordinal)
                && ComponentKeywordApi.CanUpgradeRemove(keywordId, card))
                candidates.Add(new(new(CardUpgradeKind.RemoveCustomKeyword, KeywordId: keywordId)));

        // A 1-cost card whose own text already lowers its cost must not upgrade to 0: the resulting upgraded
        // card would carry a permanently dead self-cost-reduction clause. Higher costs may still upgrade by 1.
        var costCandidate = card.Cost > 0
            && !card.Tags.Contains(CardTag.Sly)
            && (card.Cost > 1 || !card.Operations.Any(CardEffectRules.IsSelfCostReduction))
            && !SlyKeywordTuning.IsPureImmediateSelfRefund(card.Cost - 1, card.Operations)
            && CardEffectRules.HasValidNumericSelfCostReductionAmounts(card.Cost - 1, card.Operations)
            && CardEffectRules.HasValidReturnThisToHandCost(card.Cost - 1, card.StarCost,
                card.HasStarCostX, card.Operations)
            ? new Candidate(new(CardUpgradeKind.ReduceCost, Delta: -1))
            : null;
        var starCostCandidate = (unifiedChaos || card.Character == GeneratedCharacter.Regent) && card.StarCost > 0
            && !card.Tags.Contains(CardTag.Sly)
            && CardEffectRules.HasValidReturnThisToHandCost(card.Cost, card.StarCost - 1,
                card.HasStarCostX, card.Operations)
            ? new Candidate(new(CardUpgradeKind.ReduceStarCost, Delta: -1))
            : null;
        // This should not be empty; pure X effects still support an X-to-X+1 numeric upgrade.
        if (candidates.Count == 0 && costCandidate is null && starCostCandidate is null)
            return new CardUpgradePlan(card.Cost, Array.Empty<CardUpgradeEffect>(), card.ChineseDescription, Array.Empty<CardTag>(), card.EnglishDescription);

        var availableCount = candidates.Count + (costCandidate is null ? 0 : 1) + (starCostCandidate is null ? 0 : 1);
        var count = availableCount >= 2
            && random.Next(100) < NativeUpgradeStrengthModel.MultipleEffectChance(card.Rarity) ? 2 : 1;
        var targetUpgradeValue = NativeUpgradeStrengthModel.SampleTargetValue(card.Rarity, random);
        var maximumUpgradeGain = NativeUpgradeStrengthModel.MaximumPlanGain(card, targetUpgradeValue);
        var selected = new List<Candidate>(count);
        // Cost reduction does not compete at equal weight with numeric upgrades; cheaper cards receive it less often.
        var chooseCost = costCandidate is not null
            && (card.Cost != 1 && candidates.Count == 0 && starCostCandidate is null
                || random.Next(100) < CostReductionChance(card.Character, card.Cost, unifiedChaos));
        if (chooseCost && FitsUpgradeCeiling(card, selected, costCandidate!, maximumUpgradeGain))
            selected.Add(costCandidate!);
        // Fixed-Star Regent cards have a separate upgrade axis. Higher Star costs are more likely to spend an
        // upgrade slot on reducing that cost; a 1-Star card can legitimately upgrade to 0 Stars.
        var chooseStarCost = starCostCandidate is not null && selected.Count < count
            && (candidates.Count == 0 && costCandidate is null
                || random.Next(100) < StarCostReductionChance(card.StarCost));
        if (chooseStarCost && FitsUpgradeCeiling(card, selected, starCostCandidate!, maximumUpgradeGain))
            selected.Add(starCostCandidate!);
        if (selected.Count == 0 && candidates.Count == 0)
        {
            // A 1-cost card with no other legal upgrade remains generatable, but only on its native-rate cost roll.
            // Rejecting this speculative upgrade plan lets the assembly generator reroll instead of inflating the
            // final 1->0 rate by forcing every such card to become free.
            if (card.Cost == 1 && starCostCandidate is null)
                return new CardUpgradePlan(card.Cost, Array.Empty<CardUpgradeEffect>(), card.ChineseDescription,
                    Array.Empty<CardTag>(), card.EnglishDescription);
            var resourceCandidates = new[] { costCandidate, starCostCandidate }
                .Where(candidate => candidate is not null).Cast<Candidate>()
                .Where(candidate => FitsUpgradeCeiling(card, selected, candidate, maximumUpgradeGain))
                .ToArray();
            if (resourceCandidates.Length == 0)
                return new CardUpgradePlan(card.Cost, Array.Empty<CardUpgradeEffect>(), card.ChineseDescription,
                    Array.Empty<CardTag>(), card.EnglishDescription);
            selected.Add(resourceCandidates[random.Next(resourceCandidates.Length)]);
        }

        // Keyword changes do not compete at equal weight with numeric/derivative upgrades. Each remaining slot makes
        // an independent rarity roll; failure never forces a keyword even when it is the only candidate. Basic and
        // Common cards therefore favor numeric upgrades while every legal keyword upgrade remains reachable.
        var ordinaryCandidates = candidates
            .Where(candidate => !IsKeywordChange(candidate.Effect.Kind))
            .ToList();
        var keywordCandidates = candidates
            .Where(candidate => IsKeywordChange(candidate.Effect.Kind))
            .DistinctBy(candidate => (candidate.Effect.Kind, candidate.Effect.KeywordId))
            .ToList();
        while (selected.Count < count)
        {
            var acceptedKeywordCandidates = keywordCandidates
                .Where(candidate => random.Next(10_000) < KeywordUpgradeChanceBasisPoints(card, candidate))
                .ToArray();
            if (acceptedKeywordCandidates.Length > 0)
            {
                var keyword = PickCandidateByMarginalValue(card, acceptedKeywordCandidates, selected,
                    count, targetUpgradeValue, maximumUpgradeGain, random);
                if (keyword is not null)
                {
                    selected.Add(keyword);
                    keywordCandidates.Remove(keyword);
                    continue;
                }
            }

            if (ordinaryCandidates.Count == 0)
                break;
            var ordinary = PickCandidateByMarginalValue(card, ordinaryCandidates, selected,
                count, targetUpgradeValue, maximumUpgradeGain, random);
            if (ordinary is null) break;
            selected.Add(ordinary);
            ordinaryCandidates.RemoveAll(candidate => candidate.Effect.Kind == ordinary.Effect.Kind
                && SameUpgradeAxis(candidate.Effect, ordinary.Effect));
        }
        // A one-line non-resource upgrade has a 50% chance to gain a deliberately small second numeric axis.
        // This is evaluated after the ordinary native-rate two-effect selection: it never turns a cost reduction
        // into a compound upgrade, never duplicates the same operation/value slot, and never grants a second
        // keyword/rule/derivative upgrade. Its magnitude is fitted against one fixed whole-card marginal budget;
        // this makes +Damage on a multi-hit line smaller than +Damage on a single hit while retaining a minimum
        // visible change of one.
        if (selected.Count == 1
            && ShouldAddLowNumericSecondary(selected[0].Effect.Kind, random.Next(100)))
        {
            var lowNumericCandidates = candidates
                .Where(candidate => IsNumericUpgrade(candidate.Effect.Kind))
                .Where(candidate => !SameUpgradeAxis(candidate.Effect, selected[0].Effect))
                .Select(candidate => ToBudgetedNumericCandidate(card, selected, candidate))
                .Where(candidate => candidate is not null)
                .Cast<Candidate>()
                .ToArray();
            if (lowNumericCandidates.Length > 0)
            {
                // CandidateWeight retains variation between legal fields, but strongly prefers the field whose
                // minimum-one integer delta lands closest to the fixed supplemental budget.
                var supplemental = PickCandidateByMarginalValue(card, lowNumericCandidates, selected,
                    selected.Count + 1, EstimatedPlanGain(card, selected) + SupplementalNumericUpgradeBudget,
                    maximumUpgradeGain, random);
                if (supplemental is not null) selected.Add(supplemental);
            }
        }
        // Silent alone retains a small native-flavored chance to turn mandatory discard into an additional synergy
        // payment. This fixed +1 is a bonus rider: it consumes neither an ordinary upgrade slot nor target upgrade
        // value, and no other character receives it merely because Ultimate Chaos unlocked Silent operations.
        var silentDiscardUpgradeRoll = random.Next(10_000);
        if (!unifiedChaos && card.Character == GeneratedCharacter.Silent && silentDiscardUpgradeRoll < 1_000)
        {
            var discardIndexes = card.Operations.Select((operation, index) => (operation, index))
                .Where(item => item.operation.Template == "N:Discard")
                .Select(item => item.index).ToArray();
            if (discardIndexes.Length > 0)
            {
                var discardIndex = discardIndexes[random.Next(discardIndexes.Length)];
                selected.Add(new Candidate(new CardUpgradeEffect(CardUpgradeKind.IncreaseNumber,
                    discardIndex, 1,
                    ValueSlotId: OperationRuntimeSpecCompiler.UpgradeValueSlot(card.Operations[discardIndex]))));
            }
        }
        // Apply every selected upgrade cumulatively. Two effects may legally target the same operation (for
        // example, increase the amount and upgrade the generated derivative); assigning each candidate's isolated
        // After text would make the later one silently erase the earlier one.
        var effects = selected.Select(candidate => candidate.Effect with
        {
            ValueSlotId = candidate.Effect.ValueSlotId ?? (candidate.Effect.OperationIndex is { } operationIndex
                           && (uint)operationIndex < (uint)card.Operations.Count
                ? OperationRuntimeSpecCompiler.UpgradeValueSlot(card.Operations[operationIndex])
                : null)
        }).ToArray();
        var upgradedOperations = ApplyEffectsToOperations(card.Operations, effects);
        var upgradedDescription = CardDescriptionRenderer.Render(upgradedOperations);

        var cost = selected.Any(candidate => candidate.Effect.Kind == CardUpgradeKind.ReduceCost)
            ? card.Cost - 1
            : card.Cost;
        var upgradedStarCost = selected.Any(candidate => candidate.Effect.Kind == CardUpgradeKind.ReduceStarCost)
            ? card.StarCost - 1
            : (int?)null;
        // Structural effects are authoritative in API v3. The four projected arrays remain read-only migration
        // inputs for schema 5-9 live snapshots, older history records, and API v2 callers; do not duplicate newly
        // generated keyword changes.
        return new CardUpgradePlan(cost, effects, upgradedDescription, [],
            EnglishCardDescriptionRenderer.Render(upgradedOperations), [], upgradedStarCost);
    }

    internal static bool ShouldAddLowNumericSecondary(CardUpgradeKind soleKind, int percentileRoll) =>
        soleKind is not (CardUpgradeKind.ReduceCost or CardUpgradeKind.ReduceStarCost)
        && percentileRoll is >= 0 and < 50;

    private static bool IsNumericUpgrade(CardUpgradeKind kind) => kind is
        CardUpgradeKind.IncreaseNumber or CardUpgradeKind.ReduceSelfDamage
            or CardUpgradeKind.ReduceThreshold or CardUpgradeKind.ReduceNegativeNumber;

    private static bool SameUpgradeAxis(CardUpgradeEffect left, CardUpgradeEffect right) =>
        left.OperationIndex == right.OperationIndex
        && string.Equals(left.ValueSlotId, right.ValueSlotId, StringComparison.Ordinal);

    private static Candidate? ToBudgetedNumericCandidate(GeneratedCard card, IReadOnlyList<Candidate> selected,
        Candidate candidate)
    {
        var effect = candidate.Effect;
        if (!IsNumericUpgrade(effect.Kind) || effect.OperationIndex is not { } operationIndex
            || (uint)operationIndex >= (uint)card.Operations.Count || effect.Delta is null)
            return null;
        var operation = card.Operations[operationIndex];
        var sign = effect.Delta > 0 ? 1 : -1;
        var maximumMagnitude = SupplementalMaximumMagnitude(card, operation, effect);
        Candidate? best = null;
        var bestDistance = double.MaxValue;
        for (var magnitude = 1; magnitude <= maximumMagnitude; magnitude++)
        {
            var trial = new Candidate(effect with { Delta = sign * magnitude });
            var distance = Math.Abs(EstimatedMarginalGain(card, selected, trial)
                - SupplementalNumericUpgradeBudget);
            if (distance + 0.001d >= bestDistance) continue;
            best = trial;
            bestDistance = distance;
        }
        return best;
    }

    private static int SupplementalMaximumMagnitude(GeneratedCard card, GeneratorOperation operation,
        CardUpgradeEffect effect)
    {
        if (OperationRuntimeSpecCompiler.ValueUsesX(operation)) return 1;
        var slotId = effect.ValueSlotId ?? OperationRuntimeSpecCompiler.UpgradeValueSlot(operation);
        var slot = slotId is null
            ? null
            : OperationRuntimeSpecCompiler.GetOrCompile(operation).Values
                .FirstOrDefault(value => value.Id == slotId);
        if (slot is null || slot.Source != "fixed") return 1;
        var currentValue = Math.Max(1, slot.BaseValue + slot.Offset);
        var maximum = effect.Kind == CardUpgradeKind.IncreaseNumber
            ? currentValue
            : Math.Max(1, currentValue - 1);

        // These fields have semantic caps beyond the universal <=100% upgrade cap. Their already-generated native
        // candidate has passed every relevant cap/refund check, so never search beyond its legal magnitude.
        if (CardEffectRules.IsDirectOrbChannel(operation)
            || NumericGenerationTuning.DurationOnlyStackCap(operation, card.Character, card.UnifiedChaos) is not null
            || operation.Template is "NCR:BlockTripleOstyMaxHp" or "R:PlaySelectedSkillMultipleTimes"
            || CardEffectRules.IsEnergyGainOperation(operation))
            maximum = Math.Min(maximum, Math.Abs(effect.Delta ?? 1));
        return Math.Clamp(maximum, 1, 100);
    }

    private static Candidate? PickCandidateByMarginalValue(GeneratedCard card,
        IReadOnlyList<Candidate> candidates, IReadOnlyList<Candidate> selected, int targetCount,
        double targetUpgradeValue, double maximumPlanGain, Random random)
    {
        var accumulated = EstimatedPlanGain(card, selected);
        var remainingSlots = Math.Max(1, targetCount - selected.Count);
        var desiredGain = Math.Max(1d, targetUpgradeValue - accumulated) / remainingSlots;
        // NativeUpgradeValueModel supplies the maximum native-shaped delta. Fit downward, never upward, against
        // the remaining whole-card budget. Because EstimatedMarginalGain revalues the complete operation list,
        // this automatically prices fixed multi-hit, random/AoE multi-hit, dynamic Flechettes/Finisher counts,
        // trigger frequency and dependent numeric modifiers rather than treating every printed +1 as one hit.
        var fittedCandidates = candidates
            .Select(candidate => FitNumericCandidateToBudget(card, selected, candidate, desiredGain))
            .Where(candidate => FitsUpgradeCeiling(card, selected, candidate, maximumPlanGain))
            .ToArray();
        if (fittedCandidates.Length == 0) return null;
        var weights = fittedCandidates.Select(candidate => NativeUpgradeStrengthModel.CandidateWeight(
            EstimatedMarginalGain(card, selected, candidate), desiredGain)).ToArray();
        var total = weights.Sum(weight => (long)weight);
        var roll = random.NextInt64(total);
        for (var index = 0; index < fittedCandidates.Length; index++)
        {
            roll -= weights[index];
            if (roll < 0) return fittedCandidates[index];
        }
        return fittedCandidates[^1];
    }

    private static bool FitsUpgradeCeiling(GeneratedCard card, IReadOnlyList<Candidate> selected,
        Candidate candidate, double maximumPlanGain) =>
        EstimatedPlanGain(card, selected.Append(candidate).ToArray()) <= maximumPlanGain + 0.001d;

    private static Candidate FitNumericCandidateToBudget(GeneratedCard card, IReadOnlyList<Candidate> selected,
        Candidate candidate, double desiredGain)
    {
        var effect = candidate.Effect;
        if (!IsNumericUpgrade(effect.Kind) || effect.Delta is null || effect.OperationIndex is not { } operationIndex
            || (uint)operationIndex >= (uint)card.Operations.Count)
            return candidate;
        var operation = card.Operations[operationIndex];
        // NativeUpgradeValueModel remains the ordinary single-resolution prior. Refit every positive numeric reward
        // against its complete-card marginal value so repeated/multi-hit fields can shrink and difficult, low-rate
        // trigger payoffs can grow. The latter used to inherit a tiny unconditional delta (for example 21 -> 23)
        // even though each added point was expected to resolve far less than once.
        if (effect.Kind != CardUpgradeKind.IncreaseNumber || !CardEffectRules.IsBeneficialEffect(operation))
            return candidate;
        var sign = effect.Delta > 0 ? 1 : -1;
        var unitEffect = effect with { Delta = sign };
        var unitGain = EstimatedMarginalGain(card, selected, new Candidate(unitEffect));
        if (unitGain <= 0.5d) return candidate;
        var triggerOwned = operation.Parameters.ContainsKey("triggerIndex");
        if (!triggerOwned)
        {
            // Preserve the cheap native path for ordinary effects. Only an unowned Damage field needs the existing
            // multi-hit correction; all cadence-aware payoffs carry trigger ownership in generated snapshots.
            if (!CardEffectRules.IsPrintedDamageReward(operation)
                || unitGain <= EffectBalanceModel.SingleResolutionDamagePointValue(operation) * 1.05d)
                return candidate;
        }
        var nativeMagnitude = Math.Max(1, Math.Abs(effect.Delta.Value));
        var maximumMagnitude = PrimaryNumericMaximumMagnitude(card, selected, candidate, unitGain,
            nativeMagnitude);
        Candidate? best = null;
        var bestDistance = double.MaxValue;
        for (var magnitude = 1; magnitude <= maximumMagnitude; magnitude++)
        {
            var trial = new Candidate(effect with { Delta = sign * magnitude });
            var distance = Math.Abs(EstimatedMarginalGain(card, selected, trial) - desiredGain);
            // Equal distance deliberately keeps the smaller integer change.
            if (distance + 0.001d >= bestDistance) continue;
            best = trial;
            bestDistance = distance;
        }
        return best ?? candidate;
    }

    private static int PrimaryNumericMaximumMagnitude(GeneratedCard card, IReadOnlyList<Candidate> selected,
        Candidate candidate, double linkedUnitGain, int nativeMagnitude)
    {
        var effect = candidate.Effect;
        if (effect.OperationIndex is not { } operationIndex
            || (uint)operationIndex >= (uint)card.Operations.Count)
            return nativeMagnitude;
        var operation = card.Operations[operationIndex];

        // These axes have semantic/native caps which are more important than cadence compensation. Draw upgrades
        // remain +1; Energy, direct Orb counts, durations, and repeated-play counts retain their dedicated limits.
        if (OperationRuntimeSpecCompiler.ValueUsesX(operation)
            || NativeUpgradeValueModel.Classify(operation) == NativeUpgradeValueModel.Family.Draw
            || CardEffectRules.IsDirectOrbChannel(operation)
            || NumericGenerationTuning.DurationOnlyStackCap(operation, card.Character, card.UnifiedChaos) is not null
            || operation.Template is "NCR:BlockTripleOstyMaxHp" or "R:PlaySelectedSkillMultipleTimes"
            || CardEffectRules.IsEnergyGainOperation(operation))
            return nativeMagnitude;

        var slotId = effect.ValueSlotId ?? OperationRuntimeSpecCompiler.UpgradeValueSlot(operation);
        var slot = slotId is null
            ? null
            : OperationRuntimeSpecCompiler.GetOrCompile(operation).Values
                .FirstOrDefault(value => value.Id == slotId);
        if (slot is null || slot.Source != "fixed") return nativeMagnitude;
        var currentValue = Math.Max(1, slot.BaseValue + slot.Offset);

        var unlinkedUnitGain = EstimatedUnlinkedUnitGain(card, selected, candidate);
        if (unlinkedUnitGain <= 0.5d || linkedUnitGain >= unlinkedUnitGain * 0.80d)
            return Math.Min(nativeMagnitude, currentValue);

        // Compensate low trigger cadence conservatively. The square root avoids turning an exceptionally rare
        // condition into a 100% numeric upgrade, while the 20%-of-field floor keeps large printed payoffs from
        // receiving visually negligible +1/+2 changes. Whole-plan ceilings still reject an excessive result.
        var cadenceScale = Math.Min(3d, Math.Sqrt(unlinkedUnitGain / Math.Max(0.5d, linkedUnitGain)));
        var cadenceMagnitude = (int)Math.Ceiling(nativeMagnitude * cadenceScale);
        var largeFieldFloor = (int)Math.Ceiling(currentValue * 0.20d);
        return Math.Clamp(Math.Max(nativeMagnitude, Math.Max(cadenceMagnitude, largeFieldFloor)),
            1, currentValue);
    }

    private static double EstimatedUnlinkedUnitGain(GeneratedCard card, IReadOnlyList<Candidate> selected,
        Candidate candidate)
    {
        if (candidate.Effect.OperationIndex is not { } operationIndex
            || (uint)operationIndex >= (uint)card.Operations.Count)
            return 0d;
        var beforeEffects = selected.Select(item => item.Effect).ToArray();
        var afterEffects = beforeEffects.Append(candidate.Effect with
        {
            Delta = candidate.Effect.Delta is > 0 ? 1 : -1
        }).ToArray();
        var beforeOperations = ApplyEffectsToOperations(card.Operations, beforeEffects);
        var afterOperations = ApplyEffectsToOperations(card.Operations, afterEffects);
        beforeOperations[operationIndex] = WithoutTriggerOwner(beforeOperations[operationIndex]);
        afterOperations[operationIndex] = WithoutTriggerOwner(afterOperations[operationIndex]);
        return Math.Max(0d, EffectBalanceModel.EstimatedPositiveCardValue(afterOperations)
            - EffectBalanceModel.EstimatedPositiveCardValue(beforeOperations));
    }

    private static GeneratorOperation WithoutTriggerOwner(GeneratorOperation operation)
    {
        if (!operation.Parameters.ContainsKey("triggerIndex")) return operation;
        var parameters = operation.Parameters
            .Where(pair => pair.Key != "triggerIndex")
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return operation with { Parameters = parameters };
    }

    private static double EstimatedPlanGain(GeneratedCard card, IReadOnlyList<Candidate> selected)
    {
        var applied = new List<Candidate>();
        var total = 0d;
        foreach (var candidate in selected)
        {
            total += EstimatedMarginalGain(card, applied, candidate);
            applied.Add(candidate);
        }
        return total;
    }

    internal static double EstimatedUpgradeGain(GeneratedCard card)
    {
        if (card.Upgrade is not { Effects.Count: > 0 } upgrade) return 0d;
        return EstimatedPlanGain(card, upgrade.Effects.Select(effect => new Candidate(effect)).ToArray());
    }

    internal static double EstimatedNumericUpgradeGainForAudit(GeneratedCard card, int operationIndex, int delta)
    {
        var operation = card.Operations[operationIndex];
        var effect = new CardUpgradeEffect(CardUpgradeKind.IncreaseNumber, operationIndex, delta,
            ValueSlotId: OperationRuntimeSpecCompiler.UpgradeValueSlot(operation));
        return EstimatedMarginalGain(card, Array.Empty<Candidate>(), new Candidate(effect));
    }

    internal static CardUpgradeEffect? SupplementalNumericEffectForAudit(GeneratedCard card, int operationIndex)
    {
        var operation = card.Operations[operationIndex];
        var effect = new CardUpgradeEffect(CardUpgradeKind.IncreaseNumber, operationIndex, 1,
            ValueSlotId: OperationRuntimeSpecCompiler.UpgradeValueSlot(operation));
        return ToBudgetedNumericCandidate(card, Array.Empty<Candidate>(), new Candidate(effect))?.Effect;
    }

    internal static CardUpgradeEffect FitNumericEffectForAudit(GeneratedCard card, int operationIndex,
        int nativeDelta, double desiredGain)
    {
        var operation = card.Operations[operationIndex];
        var effect = new CardUpgradeEffect(CardUpgradeKind.IncreaseNumber, operationIndex, nativeDelta,
            ValueSlotId: OperationRuntimeSpecCompiler.UpgradeValueSlot(operation));
        return FitNumericCandidateToBudget(card, Array.Empty<Candidate>(), new Candidate(effect), desiredGain).Effect;
    }

    private static double EstimatedMarginalGain(GeneratedCard card, IReadOnlyList<Candidate> selected,
        Candidate candidate)
    {
        if (candidate.Effect.Kind == CardUpgradeKind.IncreaseNumber
            && candidate.Effect.OperationIndex is { } downsideIndex
            && (uint)downsideIndex < (uint)card.Operations.Count
            && card.Operations[downsideIndex].Template == "N:Discard")
            // Silent's optional +1 mandatory-discard rider is a drawback, not 200 points of positive upgrade.
            return 0d;
        if (IsXValueUpgrade(card, candidate.Effect))
            return 500d;

        var beforeEffects = selected.Select(item => item.Effect).ToArray();
        var afterEffects = beforeEffects.Append(candidate.Effect).ToArray();
        var beforeOperations = ApplyEffectsToOperations(card.Operations, beforeEffects);
        var afterOperations = ApplyEffectsToOperations(card.Operations, afterEffects);
        var operationGain = EffectBalanceModel.EstimatedPositiveCardValue(afterOperations)
            - EffectBalanceModel.EstimatedPositiveCardValue(beforeOperations);
        return operationGain > 0.5d
            ? operationGain
            : NativeUpgradeStrengthModel.NonOperationGain(candidate.Effect, card);
    }

    private static bool IsXValueUpgrade(GeneratedCard card, CardUpgradeEffect effect)
    {
        if (effect.Kind != CardUpgradeKind.IncreaseNumber
            || effect.OperationIndex is not { } index
            || (uint)index >= (uint)card.Operations.Count)
            return false;
        var operation = card.Operations[index];
        var slotId = effect.ValueSlotId ?? OperationRuntimeSpecCompiler.UpgradeValueSlot(operation);
        return slotId is not null
            && OperationRuntimeSpecCompiler.GetOrCompile(operation).Values.Any(value =>
                value.Id == slotId && value.Source is "energy_x" or "star_x" or "special_x");
    }

    private static int CostReductionChance(GeneratedCharacter character, int cost, bool unifiedChaos) => cost switch
    {
        // Native v111 one-to-zero upgrade rates: Ironclad 6.4%, Silent 7.3%, Defect 17.0%, Necrobinder 7.5%,
        // Regent 2.0%, Colorless 23.8%. Candidate rates are slightly lower to offset retry acceptance bias; measured
        // finalized-card rates, rather than this internal roll, are calibrated around the native pools.
        1 => unifiedChaos ? 8 : character switch
        {
            GeneratedCharacter.Ironclad => 5,
            GeneratedCharacter.Silent => 6,
            GeneratedCharacter.Defect => 15,
            GeneratedCharacter.Necrobinder => 5,
            GeneratedCharacter.Regent => 2,
            GeneratedCharacter.Colorless => 20,
            _ => 8
        },
        2 => 30,
        >= 3 => 45,
        _ => 0
    };

    private static int StarCostReductionChance(int cost) => cost switch
    {
        1 => 18,
        2 => 28,
        3 => 36,
        >= 4 => 45,
        _ => 0
    };

    private static bool IsKeywordChange(CardUpgradeKind kind) => kind is
        CardUpgradeKind.GrantInnate or
        CardUpgradeKind.GrantRetain or
        CardUpgradeKind.RemoveExhaust or
        CardUpgradeKind.RemoveEthereal or
        CardUpgradeKind.AddCustomKeyword or
        CardUpgradeKind.RemoveCustomKeyword;

    private static int KeywordUpgradeChance(GeneratedRarity rarity) => rarity switch
    {
        GeneratedRarity.Basic => 1,
        GeneratedRarity.Common => 2,
        GeneratedRarity.Uncommon => 6,
        GeneratedRarity.Rare => 11,
        GeneratedRarity.Ancient => 15,
        _ => 2
    };

    private static int KeywordUpgradeChanceBasisPoints(GeneratedCard card, Candidate candidate)
    {
        if (candidate.Effect.Kind == CardUpgradeKind.RemoveExhaust)
            return RemoveExhaustUpgradeChanceBasisPoints(card);
        var chance = KeywordUpgradeChance(card.Rarity) * 100L;
        if (candidate.Effect.Kind == CardUpgradeKind.GrantRetain)
            chance = PercentWeight.Apply(chance, CardKeywordTuning.RetainWeightPercent(card.Cost,
                card.StarCost, card.HasStarCostX));
        else if (candidate.Effect.Kind == CardUpgradeKind.GrantInnate)
            chance = PercentWeight.Apply(chance, CardKeywordTuning.InnateWeightPercent(card.Cost,
                card.StarCost, card.HasStarCostX, card.Type, card.Tags.Contains(CardTag.Exhaust)));
        return (int)Math.Clamp(chance, 0, 10_000);
    }

    /// <summary>
    /// Removing Exhaust changes a one-shot card into a reusable engine, so it is not priced like an ordinary
    /// keyword edit. Start below the existing rarity-based keyword rate, then let genuinely under-budget cards
    /// recover most of that rate. Well-compensated and high-roll cards retain only a very small non-zero path.
    /// </summary>
    internal static int RemoveExhaustUpgradeChanceBasisPoints(GeneratedCard card)
    {
        var effectiveCost = card.Cost < 0
            ? card.Cost
            : card.Cost + CardEffectRules.StarCostEnergyEquivalent(card.StarCost);
        var relativeValue = EffectBalanceModel.RelativeCardRewardValue(card.Operations, card.Rarity, effectiveCost);
        var factorPercent = relativeValue switch
        {
            <= 0.55d => 90,
            <= 0.75d => 70,
            <= 0.95d => 45,
            <= 1.15d => 22,
            <= 1.40d => 10,
            _ => 4
        };
        return Math.Max(1, KeywordUpgradeChance(card.Rarity) * factorPercent);
    }

    private static int SelfDamageReduction(int value) => value switch
    {
        >= 17 => 4,
        >= 11 => 3,
        >= 7 => 2,
        _ => 1
    };

    internal static string UpgradeRandomGenerationChinese(string text) =>
        text.Contains("升级过的随机", StringComparison.Ordinal)
            ? text
            : text.Contains("其他角色的攻击牌", StringComparison.Ordinal)
                ? text.Replace("其他角色的攻击牌", "其他角色的升级过的攻击牌", StringComparison.Ordinal)
                : Regex.Replace(text, "随机", "升级过的随机", RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100));

    internal static string UpgradeRandomGenerationEnglish(string text) =>
        text.Contains("random upgraded", StringComparison.OrdinalIgnoreCase)
            ? text
            : Regex.Replace(text, @"\brandom\b", "random upgraded", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));

}

internal sealed record NameParts(string Id, IReadOnlyList<string> ChineseParts, string EnglishPrefix, string EnglishMiddle, string EnglishSuffix);

public static class CardNameGenerator
{
    // Chinese names are reviewed into meaningful chunks of at most two characters. Slashes exist only in the source
    // lexicon; recombining all chunks must reproduce the exact official v111 title.
    private static readonly IReadOnlyDictionary<string, string> ChineseSplitOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["StrikeIronclad"] = "打击",
        ["FightMe"] = "与我/一战/！", ["ForgottenRitual"] = "被/遗忘/的/仪式",
        ["Juggernaut"] = "势不/可当", ["SwordBoomerang"] = "飞剑/回旋/镖",
        ["Abrasive"] = "磨/蚀", ["Accelerant"] = "触/媒", ["Accuracy"] = "精/准", ["Acrobatics"] = "杂/技",
        ["Adrenaline"] = "肾上/腺素", ["Afterimage"] = "余/像", ["Anticipate"] = "预/判", ["Assassinate"] = "刺/杀",
        ["Backflip"] = "后/空翻", ["Backstab"] = "背/刺", ["BladeOfInk"] = "墨/之刃", ["BladeDance"] = "刀刃/之舞",
        ["Blur"] = "残/影", ["BouncingFlask"] = "弹跳/药瓶", ["BubbleBubble"] = "咕嘟/冒泡", ["BulletTime"] = "子弹/时间",
        ["Burst"] = "爆/发", ["CalculatedGamble"] = "计算/下注", ["CloakAndDagger"] = "斗篷/与/匕首", ["CorrosiveWave"] = "腐蚀/波",
        ["DaggerSpray"] = "匕首/雨", ["DaggerThrow"] = "投掷/匕首", ["Dash"] = "冲/刺", ["DeadlyPoison"] = "致命/毒药",
        ["DefendSilent"] = "防/御", ["Deflect"] = "偏/折", ["DodgeAndRoll"] = "闪躲/翻滚", ["EchoingSlash"] = "回响/斩击",
        ["Envenom"] = "涂/毒", ["EscapePlan"] = "逃脱/计划", ["Expertise"] = "独门/技术", ["Expose"] = "暴/露",
        ["FanOfKnives"] = "刀/扇", ["Finisher"] = "终结/技", ["Flechettes"] = "飞/镖", ["FlickFlack"] = "翻越/撑击",
        ["Sidestep"] = "侧/步", ["Footwork"] = "灵动/步法", ["GrandFinale"] = "华丽/收场", ["HandTrick"] = "手上/技法",
        ["Haze"] = "迷/雾", ["HiddenDaggers"] = "隐秘/匕首", ["InfiniteBlades"] = "无尽/刀刃", ["KnifeTrap"] = "刀刃/陷阱",
        ["LeadingStrike"] = "先制/打击", ["LegSweep"] = "扫/腿", ["Malaise"] = "萎/靡", ["MasterPlanner"] = "谋划/专家",
        ["MementoMori"] = "铭记/死亡", ["Mirage"] = "蜃/景", ["Murder"] = "谋/杀", ["Neutralize"] = "中/和",
        ["Nightmare"] = "夜/魇", ["NoxiousFumes"] = "毒/雾", ["Outbreak"] = "毒性/爆发", ["PhantomBlades"] = "幻影/之刃",
        ["PiercingWail"] = "尖/啸", ["Pinpoint"] = "精密/瞄准", ["PoisonedStab"] = "带毒/刺击", ["Pounce"] = "猛/扑",
        ["PreciseCut"] = "精确/切击", ["Predator"] = "猎杀/者", ["Prepared"] = "早有/准备", ["Reflex"] = "本能/反应",
        ["Ricochet"] = "连续/反弹", ["SerpentForm"] = "群蛇/形态", ["ShadowStep"] = "暗影/步", ["Shadowmeld"] = "融入/暗影",
        ["Skewer"] = "串/刺", ["Slice"] = "切/割", ["Snakebite"] = "蛇/咬", ["Speedster"] = "速行/者",
        ["StormOfSteel"] = "钢铁/风暴", ["Strangle"] = "紧/勒", ["StrikeSilent"] = "打击", ["SuckerPunch"] = "突然/一拳",
        ["Suppress"] = "压/制", ["Survivor"] = "生存/者", ["Tactician"] = "战术/大师", ["TheHunt"] = "狩/猎",
        ["ToolsOfTheTrade"] = "必备/工具", ["Tracking"] = "跟/踪", ["Untouchable"] = "触不/可及", ["UpMySleeve"] = "袖里/乾坤",
        ["WellLaidPlans"] = "计划/妥当", ["WraithForm"] = "幽魂/形态"
    };

    private static readonly IReadOnlyDictionary<string, NameParts> Parts = BuildParts().ToDictionary(part => part.Id);
    private static readonly ConditionalWeakTable<IComponentCatalog, RelationIndex> RelationIndexes = new();

    private sealed class RelationIndex
    {
        private readonly IReadOnlyDictionary<string, string[]> _bySchema;
        private readonly IReadOnlyDictionary<string, string[]> _byTemplate;

        public RelationIndex(IComponentCatalog catalog)
        {
            var entries = catalog.Recipes.SelectMany(recipe => recipe.Atoms
                .Select(atom => (RecipeId: recipe.Id, Atom: atom))).ToArray();
            _bySchema = entries.GroupBy(entry => RelationSchema(entry.Atom),
                    StringComparer.Ordinal)
                .ToDictionary(group => group.Key,
                    group => group.Select(entry => entry.RecipeId).Distinct(StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal);
            _byTemplate = entries.GroupBy(entry => entry.Atom.Template, StringComparer.Ordinal)
                .ToDictionary(group => group.Key,
                    group => group.Select(entry => entry.RecipeId).Distinct(StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal);
        }

        public IReadOnlySet<string> Find(IReadOnlyList<GeneratorOperation> operations)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var operation in operations)
            {
                var wildcard = operation.Template == "N:RetaliateDamage"
                    || DerivativeSlotCatalog.IsSlotOperation(operation.Template);
                var index = wildcard ? _byTemplate : _bySchema;
                var key = wildcard ? operation.Template : RelationSchema(operation);
                if (index.TryGetValue(key, out var recipeIds)) result.UnionWith(recipeIds);
            }
            return result;
        }
    }

    private readonly record struct WeightedNameSource(NameParts Parts, long Weight);

    private sealed class WeightedNameSourcePool
    {
        internal WeightedNameSource[] Sources { get; }
        private long[] CumulativeWeights { get; }
        private long TotalWeight => CumulativeWeights[^1];

        internal WeightedNameSourcePool(WeightedNameSource[] sources)
        {
            Sources = sources;
            CumulativeWeights = new long[sources.Length];
            var cumulative = 0L;
            for (var index = 0; index < sources.Length; index++)
            {
                cumulative += sources[index].Weight;
                CumulativeWeights[index] = cumulative;
            }
        }

        internal NameParts Sample(Random random)
        {
            var target = random.NextInt64(TotalWeight) + 1;
            var index = Array.BinarySearch(CumulativeWeights, target);
            if (index < 0) index = ~index;
            return Sources[index].Parts;
        }
    }

    public static GeneratedCardName Generate(
        IComponentCatalog catalog,
        GeneratedCard card,
        Random random,
        ISet<string>? usedChineseNames = null,
        ISet<string>? usedEnglishNames = null)
    {
        if (catalog.Character != card.Character)
            throw new InvalidOperationException(
                $"Card-name catalog {catalog.Character} does not own generated card {card.Character}.");
        var isStrike = card.Tags.Contains(CardTag.Strike);
        var sources = BuildSourcePool(catalog, card, excludeStrikeSources: false);
        var suffixSources = isStrike
            ? sources
            : BuildSourcePool(catalog, card, excludeStrikeSources: true);

        // Decide once per source for this generated card. Previously every rejected name candidate rerolled this
        // choice, and two independently sampled 20% split chances made roughly 36% of two-source names contain
        // at least one broken two-character word. Keep the deliberately reviewed split reachable, but rare.
        var preserveTwoCharacterWords = catalog.Recipes
            .Select(ResolveParts)
            .DistinctBy(part => part.Id)
            .Where(IsSplittableTwoCharacterName)
            .ToDictionary(part => part.Id, _ => random.Next(100) < 92, StringComparer.Ordinal);

        GeneratedCardName? fallback = null;
        var randomAttempts = Math.Max(512, sources.Sources.Length * 24);
        for (var attempt = 0; attempt < randomAttempts; attempt++)
        {
            // Draw both source names independently with replacement. This keeps every retry concentrated around
            // effects that actually resemble the generated card instead of exhausting the best source and drifting
            // toward an unrelated tail. Only the completed bilingual name remains unique within the generated pool.
            var first = sources.Sample(random);
            var second = isStrike ? first : suffixSources.Sample(random);
            if (TryCreateUniqueName(first, second, isStrike, card.Type, preserveTwoCharacterWords,
                    usedChineseNames, usedEnglishNames, ref fallback) is { } candidate)
                return candidate;
        }

        // The weighted retry space is ample for ordinary pools. Keep a deterministic exhaustive escape hatch so an
        // unusually saturated historic/custom catalog still fails only when its legal name space is truly exhausted.
        var fallbackSources = sources.Sources.OrderByDescending(source => source.Weight).ToArray();
        var fallbackSuffixSources = suffixSources.Sources.OrderByDescending(source => source.Weight).ToArray();
        foreach (var first in fallbackSources)
        foreach (var second in isStrike ? [first] : fallbackSuffixSources)
            if (TryCreateUniqueName(first.Parts, second.Parts, isStrike, card.Type, preserveTwoCharacterWords,
                    usedChineseNames, usedEnglishNames, ref fallback) is { } candidate)
                return candidate;

        throw new InvalidOperationException($"{catalog.Character} 的不重复卡名组合空间已耗尽。最后候选：{fallback?.Chinese ?? "无"}。");
    }

    private static GeneratedCardName? TryCreateUniqueName(NameParts first, NameParts second, bool isStrike,
        GeneratedCardType cardType, IReadOnlyDictionary<string, bool> preserveTwoCharacterWords,
        ISet<string>? usedChineseNames, ISet<string>? usedEnglishNames, ref GeneratedCardName? fallback)
    {
        var chinese = isStrike
            ? ComposeChineseStrike(first, second, preserveTwoCharacterWords)
            : ComposeChinese(first, second, preserveTwoCharacterWords);
        var english = ComposeEnglish(first, second, isStrike);
        var candidate = new GeneratedCardName(chinese, english, new[] { first.Id, second.Id }.Distinct().ToArray());
        if (!NameFitsCardType(candidate, cardType) || !NameRespectsStrikeRule(candidate, isStrike)) return null;
        fallback ??= candidate;
        if ((usedChineseNames?.Contains(chinese) ?? false) || (usedEnglishNames?.Contains(english) ?? false))
            return null;
        usedChineseNames?.Add(chinese);
        usedEnglishNames?.Add(english);
        return candidate;
    }

    private static WeightedNameSourcePool BuildSourcePool(IComponentCatalog catalog, GeneratedCard card,
        bool excludeStrikeSources)
    {
        var descriptionSchemas = card.Operations.Select(RelationSchema).ToHashSet(StringComparer.Ordinal);
        var templates = card.Operations.Select(operation => operation.Template).ToHashSet(StringComparer.Ordinal);
        var primary = PrimaryEffect(card.Operations);
        var candidates = catalog.Recipes
            // A localized title may end in the translated word for Strike without carrying the gameplay Strike tag
            // (Leading Strike is the native example). Such a source is still reserved for Strike-card names.
            .Where(recipe => !excludeStrikeSources || !IsStrikeNameSource(recipe))
            .Select(recipe =>
            {
                var recipeSchemas = recipe.Atoms.Select(RelationSchema).ToHashSet(StringComparer.Ordinal);
                var recipeTemplates = recipe.Atoms.Select(atom => atom.Template).ToHashSet(StringComparer.Ordinal);
                var description = DiceSimilarity(descriptionSchemas, recipeSchemas) * 1_000
                    + DiceSimilarity(templates, recipeTemplates);
                var rank = (
                    Description: description,
                    PrimaryEffect: PrimaryEffectSimilarity(primary, PrimaryEffect(recipe.Atoms)),
                    SameTypeAndCost: recipe.Type == card.Type && SamePrintedCost(card, recipe) ? 1 : 0,
                    SameType: recipe.Type == card.Type ? 1 : 0);
                // Preserve the established relevance priorities, but choose probabilistically among related source
                // names instead of deterministically consuming the same highest-ranked fragments on every card.
                // Exact operation/description matches remain overwhelmingly more likely than type-only fallbacks.
                var weight = 1L + rank.Description / 50L + rank.PrimaryEffect * 80L
                    + rank.SameTypeAndCost * 30L + rank.SameType * 10L;
                return new WeightedNameSource(ResolveParts(recipe), weight);
            })
            .DistinctBy(candidate => candidate.Parts.Id)
            .ToArray();
        return new WeightedNameSourcePool(candidates);
    }

    private static bool IsStrikeNameSource(IroncladCardRecipe recipe)
    {
        var name = ResolveParts(recipe);
        return name.ChineseParts[^1] == "打击" || name.EnglishSuffix == " Strike";
    }

    private static bool NameRespectsStrikeRule(GeneratedCardName name, bool isStrike)
    {
        var chinesePrefix = name.Chinese.EndsWith("打击", StringComparison.Ordinal)
            ? name.Chinese[..^2]
            : name.Chinese;
        var englishPrefix = name.English.EndsWith(" Strike", StringComparison.OrdinalIgnoreCase)
            ? name.English[..^7]
            : name.English;
        return isStrike
            ? name.Chinese.EndsWith("打击", StringComparison.Ordinal)
              && !chinesePrefix.Contains("打击", StringComparison.Ordinal)
              && name.English.EndsWith(" Strike", StringComparison.OrdinalIgnoreCase)
              && !englishPrefix.Contains("Strike", StringComparison.OrdinalIgnoreCase)
            : !name.Chinese.Contains("打击", StringComparison.Ordinal)
              && !name.English.Contains("Strike", StringComparison.OrdinalIgnoreCase);
    }

    private static int DiceSimilarity(IReadOnlySet<string> left, IReadOnlySet<string> right)
    {
        if (left.Count == 0 || right.Count == 0) return 0;
        var intersection = left.Count <= right.Count
            ? left.Count(right.Contains)
            : right.Count(left.Contains);
        return 200 * intersection / (left.Count + right.Count);
    }

    private static int PrimaryEffectSimilarity(GeneratorOperation? generated, ComponentAtom? source)
    {
        if (generated is null || source is null) return 0;
        if (RelationSchema(generated) == RelationSchema(source)) return 3;
        if (generated.Template == source.Template) return 2;
        var generatedFamily = CardEffectRules.EffectFamily(generated);
        var sourceFamily = CardEffectRules.EffectFamily(source);
        return generatedFamily.Length > 0 && generatedFamily == sourceFamily ? 1 : 0;
    }

    private static GeneratorOperation? PrimaryEffect(IEnumerable<GeneratorOperation> operations) => operations
        .Where(operation => CardEffectRules.IsBeneficialEffect(operation)
            && operation.Scope is not (OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger)
            && !CardEffectRules.IsDependencyPrefix(operation))
        .MaxBy(EffectBalanceModel.EstimatedEffectValue);

    private static ComponentAtom? PrimaryEffect(IEnumerable<ComponentAtom> atoms) => atoms
        .Where(atom => CardEffectRules.IsBeneficialEffect(atom)
            && atom.Scope is not (OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger)
            && !CardEffectRules.IsDependencyPrefix(atom))
        .MaxBy(EffectBalanceModel.EstimatedEffectValue);

    private static bool SamePrintedCost(GeneratedCard card, IroncladCardRecipe recipe)
    {
        if ((card.Cost < 0) != (recipe.Cost < 0)) return false;
        if (card.Cost >= 0 && card.Cost != recipe.Cost) return false;
        if (card.HasStarCostX != recipe.HasStarCostX) return false;
        return card.HasStarCostX || card.StarCost == recipe.StarCost;
    }

    private static bool NameFitsCardType(GeneratedCardName name, GeneratedCardType cardType) =>
        cardType == GeneratedCardType.Power
        || !name.Chinese.EndsWith("形态", StringComparison.Ordinal);

    public static void ValidateCatalog(IComponentCatalog catalog)
    {
        foreach (var recipe in catalog.Recipes)
        {
            if (!Parts.ContainsKey(recipe.Id))
                throw new InvalidOperationException($"缺少手工卡名拆分：{recipe.Id}。");
            var name = ResolveParts(recipe);
            if (name.ChineseParts.Count == 0 || name.ChineseParts.Any(string.IsNullOrWhiteSpace)
                || name.ChineseParts.Any(part => part.EnumerateRunes().Count() > 2)
                || string.IsNullOrWhiteSpace(name.EnglishMiddle) || name.EnglishMiddle.Contains(' '))
                throw new InvalidOperationException($"卡名拆分不符合词块规则：{recipe.Id}。");
            var reconstructedChinese = string.Concat(name.ChineseParts);
            if (reconstructedChinese != recipe.ChineseTitle)
                throw new InvalidOperationException($"卡名拆分无法还原官方中文标题：{recipe.Id}，{reconstructedChinese} != {recipe.ChineseTitle}。");
            var containsStrikeName = name.ChineseParts.Any(part => part.Contains("打击", StringComparison.Ordinal))
                || name.EnglishPrefix.Contains("Strike", StringComparison.OrdinalIgnoreCase)
                || name.EnglishMiddle.Contains("Strike", StringComparison.OrdinalIgnoreCase)
                || name.EnglishSuffix.Contains("Strike", StringComparison.OrdinalIgnoreCase);
            if (containsStrikeName && (name.EnglishSuffix != " Strike"
                    || name.ChineseParts[^1] != "打击"
                    || name.ChineseParts.Take(name.ChineseParts.Count - 1)
                        .Any(part => part.Contains("打击", StringComparison.Ordinal))
                    || name.EnglishPrefix.Contains("Strike", StringComparison.OrdinalIgnoreCase)
                    || name.EnglishMiddle.Contains("Strike", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"卡名中的“打击/Strike”必须是不可拆分的末尾词块：{recipe.Id}。");
            if (recipe.Tags.Contains(CardTag.Strike) && !containsStrikeName)
                throw new InvalidOperationException($"打击标签牌必须提供“打击/Strike”末尾词块：{recipe.Id}。");
            if (recipe.ChineseTitle.EnumerateRunes().Count() == 4
                && (name.ChineseParts.Count != 2
                    || name.ChineseParts.Any(part => part.EnumerateRunes().Count() != 2)))
                throw new InvalidOperationException(
                    $"四字卡名必须在中间按有意义的二字词块拆分：{recipe.Id}={string.Join('|', name.ChineseParts)}。");
            if (!recipe.Tags.Contains(CardTag.Strike) && !string.IsNullOrWhiteSpace(recipe.EnglishTitle))
            {
                var reconstructedEnglish = name.EnglishPrefix + name.EnglishMiddle + name.EnglishSuffix;
                if (reconstructedEnglish != recipe.EnglishTitle)
                    throw new InvalidOperationException($"卡名拆分无法还原官方英文标题：{recipe.Id}，{reconstructedEnglish} != {recipe.EnglishTitle}。");
            }
        }
    }

    internal static bool IsRelatedToOperations(IroncladCardRecipe recipe,
        IReadOnlyList<GeneratorOperation> operations)
    {
        var operationSchemas = operations
            .Where(operation => operation.Template != "N:RetaliateDamage"
                && !DerivativeSlotCatalog.IsSlotOperation(operation.Template))
            .Select(RelationSchema)
            .ToHashSet(StringComparer.Ordinal);
        var wildcardTemplates = operations
            .Where(operation => operation.Template == "N:RetaliateDamage"
                || DerivativeSlotCatalog.IsSlotOperation(operation.Template))
            .Select(operation => operation.Template).ToHashSet(StringComparer.Ordinal);
        return recipe.Atoms.Any(atom => wildcardTemplates.Contains(atom.Template)
            || operationSchemas.Contains(RelationSchema(atom)));
    }

    internal static IReadOnlySet<string> RelatedRecipeIds(IComponentCatalog catalog,
        IReadOnlyList<GeneratorOperation> operations) => RelationIndexes.GetValue(catalog,
        static source => new RelationIndex(source)).Find(operations);

    internal static void ValidateLegacyRelationEquivalence(IComponentCatalog catalog,
        IReadOnlyList<GeneratorOperation> operations)
    {
        static string Legacy(string template, string text) =>
            $"{template}|{NumericTextSchema.Fields(text)}";
        var operationSchemas = operations
            .Where(operation => operation.Template != "N:RetaliateDamage"
                && !DerivativeSlotCatalog.IsSlotOperation(operation.Template))
            .Select(operation => Legacy(operation.Template, operation.ChineseText))
            .ToHashSet(StringComparer.Ordinal);
        var wildcardTemplates = operations
            .Where(operation => operation.Template == "N:RetaliateDamage"
                || DerivativeSlotCatalog.IsSlotOperation(operation.Template))
            .Select(operation => operation.Template).ToHashSet(StringComparer.Ordinal);
        var legacy = catalog.Recipes.Where(recipe => recipe.Atoms.Any(atom =>
                wildcardTemplates.Contains(atom.Template)
                || operationSchemas.Contains(Legacy(atom.Template, atom.ChineseText))))
            .Select(recipe => recipe.Id).ToHashSet(StringComparer.Ordinal);
        var structured = RelatedRecipeIds(catalog, operations);
        if (!legacy.SetEquals(structured))
            throw new InvalidOperationException("Card-name relation schema drift: legacy="
                + string.Join(',', legacy.Order(StringComparer.Ordinal)) + "; structured="
                + string.Join(',', structured.Order(StringComparer.Ordinal)) + "; operations="
                + string.Join(" || ", operations.Select(operation =>
                    $"{operation.Template}:{operation.ChineseText}:d={operation.DerivativeId}:e={operation.DerivativeEnchantmentId}:"
                    + $"os={operation.OrbSourceId}:oo={operation.OrbOutputId}")));
    }

    internal static string RelationSchema(ComponentAtom atom) =>
        OperationRuntimeSpecCompiler.StructuralRelationKey(atom);

    internal static string RelationSchema(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.StructuralRelationKey(operation);

    internal static void ValidateCompositionRules()
    {
        var spaced = ComposeEnglish(
            new NameParts("Prefix", ["前"], "Thunder ", "unused", ""),
            new NameParts("Stem", ["后"], "", "clap", " strike"),
            isStrike: false);
        if (spaced != "Thunder Clap Strike")
            throw new InvalidOperationException($"空格分隔的英文卡名词块未正确大写：{spaced}。");

        var affixed = ComposeEnglish(
            new NameParts("Prefix", ["前"], "Un", "unused", ""),
            new NameParts("Stem", ["后"], "", "mov", "able"),
            isStrike: false);
        if (affixed != "Unmovable")
            throw new InvalidOperationException($"英文词内词缀不应被强制大写：{affixed}。");
        if (!Parts.TryGetValue("Hang", out var hang)
            || !hang.ChineseParts.SequenceEqual(["吊杀"], StringComparer.Ordinal))
            throw new InvalidOperationException("中文卡名“吊杀”必须作为不可拆分的完整词块。");

        var strikeFirst = new NameParts("AshenStrike", ["灰烬", "打击"], "", "Ashen", " Strike");
        var strikeSecond = new NameParts("PerfectedStrike", ["完美", "打击"], "", "Perfected", " Strike");
        var preserve = new Dictionary<string, bool>();
        var strikeName = new GeneratedCardName(
            ComposeChineseStrike(strikeFirst, strikeSecond, preserve),
            ComposeEnglish(strikeFirst, strikeSecond, isStrike: true),
            [strikeFirst.Id, strikeSecond.Id]);
        if (!NameRespectsStrikeRule(strikeName, isStrike: true))
            throw new InvalidOperationException($"打击卡名没有保持专用末尾后缀：{strikeName.Chinese}/{strikeName.English}。");

        var ordinaryName = new GeneratedCardName(
            ComposeChinese(strikeFirst, new NameParts("Wall", ["血", "墙"], "Blood ", "Wall", ""), preserve),
            ComposeEnglish(strikeFirst, new NameParts("Wall", ["血", "墙"], "Blood ", "Wall", ""), isStrike: false),
            [strikeFirst.Id, "Wall"]);
        if (!NameRespectsStrikeRule(ordinaryName, isStrike: false))
            throw new InvalidOperationException($"非打击卡名意外包含专用打击词块：{ordinaryName.Chinese}/{ordinaryName.English}。");
    }

    private static NameParts ResolveParts(IroncladCardRecipe recipe)
    {
        if (Parts.TryGetValue(recipe.Id, out var known)) return known;
        throw new InvalidOperationException($"缺少手工卡名拆分：{recipe.Id}。");
    }

    private static string ComposeEnglish(NameParts first, NameParts second, bool isStrike)
    {
        var prefix = first.EnglishPrefix;
        var middle = prefix.Length == 0 || char.IsWhiteSpace(prefix[^1])
            ? CapitalizeFirstLetter(second.EnglishMiddle)
            : second.EnglishMiddle;
        var suffix = isStrike ? " Strike" : second.EnglishSuffix;
        if (suffix.Length > 0 && char.IsWhiteSpace(suffix[0]))
            suffix = CapitalizeFirstLetter(suffix);
        var body = middle + suffix;
        var result = prefix + body;
        // Short stems such as "Me" may combine with affixes but cannot form a card name alone.
        if (result.Trim().Length < 4)
            result = first.EnglishPrefix + first.EnglishMiddle + first.EnglishSuffix;
        return result;
    }

    private static string CapitalizeFirstLetter(string segment)
    {
        for (var index = 0; index < segment.Length; index++)
        {
            if (!char.IsLetter(segment[index])) continue;
            return segment[..index] + char.ToUpperInvariant(segment[index]) + segment[(index + 1)..];
        }
        return segment;
    }

    private static string ComposeChinese(NameParts first, NameParts second,
        IReadOnlyDictionary<string, bool> preserveTwoCharacterWords)
    {
        if (first.Id == second.Id) return string.Concat(first.ChineseParts);
        return ChineseComponent(first, takeFirst: true, preserveTwoCharacterWords)
            + ChineseComponent(second, takeFirst: false, preserveTwoCharacterWords);
    }

    private static string ComposeChineseStrike(NameParts first, NameParts second,
        IReadOnlyDictionary<string, bool> preserveTwoCharacterWords)
    {
        var firstPart = StrikePrefix(first, preserveTwoCharacterWords);
        var secondPart = first.Id == second.Id
            ? string.Empty
            : StrikePrefix(second, preserveTwoCharacterWords);
        return firstPart + secondPart + "打击";
    }

    private static string StrikePrefix(NameParts source,
        IReadOnlyDictionary<string, bool> preserveTwoCharacterWords)
    {
        var part = ChineseComponent(source, takeFirst: true, preserveTwoCharacterWords);
        return part.Replace("打击", string.Empty, StringComparison.Ordinal);
    }

    private static string ChineseComponent(NameParts source, bool takeFirst,
        IReadOnlyDictionary<string, bool> preserveTwoCharacterWords)
    {
        var original = string.Concat(source.ChineseParts);
        // Two-character official names are often already a compact, readable morpheme. Preserve the whole
        // word 92% of the time, while retaining a small chance to use the manually reviewed split.
        if (IsSplittableTwoCharacterName(source)
            && preserveTwoCharacterWords.GetValueOrDefault(source.Id, true))
            return original;
        // Standalone punctuation is retained for exact official-name reconstruction, but never selected as a
        // generated-name component (for example, punctuation preserved from a source title).
        var semanticParts = source.ChineseParts.Where(part => part.Any(char.IsLetterOrDigit)).ToArray();
        return takeFirst ? semanticParts[0] : semanticParts[^1];
    }

    private static bool IsSplittableTwoCharacterName(NameParts source) =>
        string.Concat(source.ChineseParts).EnumerateRunes().Count() == 2 && source.ChineseParts.Count > 1;

    // Chinese names use manually reviewed composable chunks; English names use prefix/non-empty-stem/suffix splits.
    private static IReadOnlyList<NameParts> BuildParts() => new[]
    {
        N("Aggression","好勇","斗狠","","Aggress","ion"), N("Anger","愤","怒","","Anger",""),
        N("Armaments","武","装","","Arm","aments"), N("AshenStrike","灰烬","打击","","Ashen"," Strike"),
        N("Barricade","壁","垒","","Barric","ade"), N("Bash","痛","击","","Bash",""),
        N("BattleTrance","战斗","专注","Battle ","Trance",""), N("BloodWall","血","墙","Blood ","Wall",""),
        N("Bloodletting","放","血","Blood","lett","ing"), N("Bludgeon","重","锤","","Bludge","on"),
        N("BodySlam","全身","撞击","Body ","Slam",""), N("Brand","烙","印","","Brand",""),
        N("Break","破","击","","Break",""), N("Breakthrough","突","破","Break","through",""),
        N("Bully","欺","凌","","Bull","y"), N("BurningPact","燃烧","契约","Burning ","Pact",""),
        N("Cascade","倾","泻","","Casc","ade"), N("Cinder","余","烬","","Cinder",""),
        N("Colossus","巨","像","","Coloss","us"), N("Conflagration","焚","烧","Con","flagr","ation"),
        N("Corruption","腐","化","","Corrupt","ion"), N("CrimsonMantle","绯红","披风","Crimson ","Mantle",""),
        N("Cruelty","残","酷","","Cruel","ty"), N("DarkEmbrace","黑暗","之拥","Dark ","Embrace",""),
        N("DefendIronclad","防","御","","Defend",""), N("DemonForm","恶魔","形态","Demon ","Form",""),
        N("Dismantle","拆","卸","Dis","mant","le"), N("Dominate","主","宰","","Domin","ate"),
        N("DrumOfBattle","战","鼓","Drum of ","Battle",""), N("EvilEye","邪","眼","Evil ","Eye",""),
        N("ExpectAFight","跃跃","欲试","Expect a ","Fight",""), N("Feed","狂","宴","","Feed",""),
        N("FeelNoPain","无惧","疼痛","Feel No ","Pain",""), N("FiendFire","恶魔","之焰","Fiend ","Fire",""),
        N("FightMe","与我","一战！","Fight ","Me",""), N("FlameBarrier","火焰","屏障","Flame ","Barrier",""),
        N("ForgottenRitual","被遗忘的","仪式","Forgotten ","Ritual",""), N("Havoc","破","灭","","Havoc",""),
        N("Headbutt","头","槌","Head","butt",""), N("Hellraiser","地狱","狂徒","Hell","rais","er"),
        N("Hemokinesis","御血","术","Hemo","kines","is"), N("HowlFromBeyond","彼岸","咆哮","Howl from ","Beyond",""),
        N("Impervious","岿然","不动","Im","pervi","ous"), N("InfernalBlade","地狱","之刃","Infernal ","Blade",""),
        N("Inferno","狱","火","Infer","no",""), N("Inflame","燃","烧","In","flam","e"),
        N("IronWave","铁","斩波","Iron ","Wave",""), N("Juggernaut","势不可","当","","Juggernaut",""),
        N("Juggling","杂","耍","","Juggl","ing"), N("Mangle","凌","虐","","Mang","le"),
        N("MoltenFist","熔融","之拳","Molten ","Fist",""), N("NotYet","时候","未到","Not ","Yet",""),
        N("Offering","祭","品","","Offer","ing"), N("OneTwoPunch","连环","拳","One Two ","Punch",""),
        N("PactsEnd","契约","终结","Pact","s"," End"), N("PerfectedStrike","完美","打击","","Perfected"," Strike"),
        N("Pillage","劫","掠","","Pill","age"), N("PommelStrike","剑柄","打击","Pom","mel"," Strike"),
        N("PrimalForce","原始","力量","Primal ","Force",""), N("Pyre","薪火","之源","","Pyre",""),
        N("Rage","狂","怒","","Rage",""), N("Rampage","暴","走","","Ramp","age"),
        N("Rupture","撕","裂","","Rupt","ure"), N("SecondWind","重振","精神","Second ","Wind",""),
        N("SetupStrike","预备","打击","Set","up"," Strike"), N("ShrugItOff","耸肩","无视","Shrug It ","Off",""),
        N("Spite","怨","恨","","Spite",""), N("Stampede","惊","逃","","Stamp","ede"),
        N("Stoke","添","柴","","Stoke",""), N("Stomp","踩","踏","","Stomp",""),
        N("StoneArmor","岩石","铠甲","Stone ","Armor",""), N("StrikeIronclad","铁","打击","Iron","clad"," Strike"),
        N("SwordBoomerang","飞剑","回旋镖","Sword ","Boomerang",""), N("Taunt","挑","衅","","Taunt",""),
        N("TearAsunder","扯","碎","Tear ","Asunder",""), N("Thrash","痛","殴","","Thrash",""),
        N("Thunderclap","闪电","霹雳","Thunder ","clap",""), N("Tremble","战","栗","","Tremble",""),
        N("TrueGrit","坚","毅","True ","Grit",""), N("TwinStrike","双重","打击","Tw","in"," Strike"),
        N("Unmovable","坚定","不移","Un","mov","able"), N("Unrelenting","无情","猛攻","Un","relent","ing"),
        N("Uppercut","上勾","拳","Up","percut",""), N("Vicious","凶","恶","","Vici","ous"),
        N("Whirlwind","旋风","斩","Whirl","wind",""),

        N("Abrasive","磨","蚀","A","brasive",""), N("Accelerant","触","媒","Ac","celerant",""),
        N("Accuracy","精","准","Ac","curacy",""), N("Acrobatics","杂","技","Acro","batics",""),
        N("Adrenaline","肾上","腺素","Ad","renaline",""), N("Afterimage","余","像","After","image",""),
        N("Anticipate","预","判","Anti","cipate",""), N("Assassinate","刺","杀","Assass","inate",""),
        N("Backflip","后","空翻","Back","flip",""), N("Backstab","背","刺","Back","stab",""),
        N("BladeOfInk","墨","之刃","Blade of ","Ink",""), N("BladeDance","刀刃","之舞","Blade ","Dance",""),
        N("Blur","残","影","","Blur",""), N("BouncingFlask","弹跳","药瓶","Bouncing ","Flask",""),
        N("BubbleBubble","咕嘟","冒泡","Bubble ","Bubble",""), N("BulletTime","子弹","时间","Bullet ","Time",""),
        N("Burst","爆","发","","Burst",""), N("CalculatedGamble","计算","下注","Calculated ","Gamble",""),
        N("CloakAndDagger","斗篷","匕首","Cloak and ","Dagger",""), N("CorrosiveWave","腐蚀","波","Corrosive ","Wave",""),
        N("DaggerSpray","匕首","雨","Dagger ","Spray",""), N("DaggerThrow","投掷","匕首","Dagger ","Throw",""),
        N("Dash","冲","刺","","Dash",""), N("DeadlyPoison","致命","毒药","Deadly ","Poison",""),
        N("DefendSilent","防","御","","Defend",""), N("Deflect","偏","折","De","flect",""),
        N("DodgeAndRoll","闪躲","翻滚","Dodge and ","Roll",""), N("EchoingSlash","回响","斩击","Echoing ","Slash",""),
        N("Envenom","涂","毒","En","venom",""), N("EscapePlan","逃脱","计划","Escape ","Plan",""),
        N("Expertise","独门","技术","Exper","tise",""), N("Expose","暴","露","Ex","pose",""),
        N("FanOfKnives","刀","扇","Fan of ","Knives",""), N("Finisher","终结","技","Finish","er",""),
        N("Flechettes","飞","镖","Fle","chettes",""), N("FlickFlack","翻越","撑击","Flick ","Flack",""),
        N("Sidestep","侧","步","Side","step",""), N("Footwork","灵动","步法","Foot","work",""),
        N("GrandFinale","华丽","收场","Grand ","Finale",""), N("HandTrick","手上","技法","Hand ","Trick",""),
        N("Haze","迷","雾","","Haze",""), N("HiddenDaggers","隐秘","匕首","Hidden ","Daggers",""),
        N("InfiniteBlades","无尽","刀刃","Infinite ","Blades",""), N("KnifeTrap","刀刃","陷阱","Knife ","Trap",""),
        N("LeadingStrike","先制","打击","Lead","ing"," Strike"), N("LegSweep","扫","腿","Leg ","Sweep",""),
        N("Malaise","萎","靡","Mal","aise",""), N("MasterPlanner","谋划","专家","Master ","Planner",""),
        N("MementoMori","铭记","死亡","Memento ","Mori",""), N("Mirage","蜃","景","Mir","age",""),
        N("Murder","谋","杀","","Murder",""), N("Neutralize","中","和","Neutr","alize",""),
        N("Nightmare","夜","魇","Night","mare",""), N("NoxiousFumes","毒","雾","Noxious ","Fumes",""),
        N("Outbreak","毒性","爆发","Out","break",""), N("PhantomBlades","幻影","之刃","Phantom ","Blades",""),
        N("PiercingWail","尖","啸","Piercing ","Wail",""), N("Pinpoint","精密","瞄准","Pin","point",""),
        N("PoisonedStab","带毒","刺击","Poisoned ","Stab",""), N("Pounce","猛","扑","","Pounce",""),
        N("PreciseCut","精确","切击","Precise ","Cut",""), N("Predator","猎杀","者","Pred","ator",""),
        N("Prepared","早有","准备","Pre","pared",""), N("Reflex","本能","反应","Re","flex",""),
        N("Ricochet","连续","反弹","Rico","chet",""), N("SerpentForm","群蛇","形态","Serpent ","Form",""),
        N("ShadowStep","暗影","步","Shadow ","Step",""), N("Shadowmeld","融入","暗影","Shadow","meld",""),
        N("Skewer","串","刺","Ske","wer",""), N("Slice","切","割","","Slice",""),
        N("Snakebite","蛇","咬","Snake","bite",""), N("Speedster","速行","者","Speed","ster",""),
        N("StormOfSteel","钢铁","风暴","Storm of ","Steel",""), N("Strangle","紧","勒","Strang","le",""),
        N("StrikeSilent","静默","打击","","Silent"," Strike"), N("SuckerPunch","突然","一拳","Sucker ","Punch",""),
        N("Suppress","压","制","Sup","press",""), N("Survivor","生存","者","Sur","vivor",""),
        N("Tactician","战术","大师","Tacti","cian",""), N("TheHunt","狩","猎","The ","Hunt",""),
        N("ToolsOfTheTrade","必备","工具","Tools of the ","Trade",""), N("Tracking","跟","踪","Track","ing",""),
        N("Untouchable","触不可","及","Un","touchable",""), N("UpMySleeve","袖里","乾坤","Up My ","Sleeve",""),
        N("WellLaidPlans","计划","妥当","Well-Laid ","Plans",""), N("WraithForm","幽魂","形态","Wraith ","Form","")
    }.Concat(ManualExternalNameParts.All()).ToArray();

    private static NameParts N(string id, string chineseHead, string chineseTail, string englishPrefix, string englishMiddle, string englishSuffix) =>
        new(id,
            ChineseSplitOverrides.TryGetValue(id, out var split)
                ? split.Split('/', StringSplitOptions.RemoveEmptyEntries)
                : new[] { chineseHead, chineseTail },
            englishPrefix,
            englishMiddle,
            englishSuffix);
}
