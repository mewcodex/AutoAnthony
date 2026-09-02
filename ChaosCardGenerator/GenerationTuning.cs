using System.Text.RegularExpressions;

namespace ChaosCardGenerator;

/// <summary>
/// Central policy layer for percentage adjustments used while selecting effect atoms. The assembler supplies
/// native-frequency and value-fit weights; this type supplies the reusable gameplay biases. Keeping the two
/// separate prevents every balance pass from adding another independent multiplier to the assembly loop.
/// </summary>
internal static class EffectSelectionTuning
{
    private const int UltimateOstyFamilyWeightPercent = 60;
    private const int BasicDownsideWeightPercent = 30;

    /// <summary>
    /// Resource-positive feedback loops become degenerate much sooner than ordinary numeric payoffs. Keep them
    /// reachable for native reconstruction, but make Energy/Star refunds exceptional behind repeatable triggers.
    /// </summary>
    internal static int TriggeredResourceGainWeight(IEnumerable<ComponentAtom> family,
        IReadOnlyList<GeneratorOperation> previous)
    {
        if (!family.Any(atom => CardEffectRules.IsEnergyGainOperation(atom)
                || CardEffectRules.IsStarGainOperation(atom)))
            return 100;
        return EffectBalanceModel.HighFrequencyLinkedTrigger(previous) is { } trigger
            ? EffectBalanceModel.RelativeTriggerFrequency(trigger) switch
            {
                >= 2.5d => 2,
                >= 1.5d => 6,
                _ => 15
            }
            : 100;
    }

    /// <summary>
    /// Creating or transforming cards changes hand/deck size and can recursively feed other generated-card
    /// triggers. It therefore receives a separate, strong occurrence penalty in high-frequency payoff contexts.
    /// </summary>
    internal static int TriggeredCardCreationWeight(IEnumerable<ComponentAtom> family,
        IReadOnlyList<GeneratorOperation> previous)
    {
        if (!family.Any(CardEffectRules.IsCardCreationOrTransformation)) return 100;
        return EffectBalanceModel.HighFrequencyLinkedTrigger(previous) is { } trigger
            ? EffectBalanceModel.RelativeTriggerFrequency(trigger) switch
            {
                >= 2.5d => 4,
                >= 1.5d => 10,
                _ => 22
            }
            : 100;
    }

    /// <summary>
    /// Permanent stat losses stay legal after repeatable triggers, but repeated permanent penalties are unusually
    /// punishing. Keep a small assembly path rather than turning this into a hard incompatibility; ordinary
    /// downside pricing still compensates the cards that pass this weight gate.
    /// </summary>
    internal static int TriggeredPermanentDownsideWeight(IEnumerable<ComponentAtom> family,
        IReadOnlyList<GeneratorOperation> previous)
    {
        if (!family.Any(CardEffectRules.IsPermanentNegativeEffect)) return 100;
        return EffectBalanceModel.HighFrequencyLinkedTrigger(previous) is { } trigger
            ? EffectBalanceModel.RelativeTriggerFrequency(trigger) switch
            {
                >= 2.5d => 3,
                >= 1.5d => 10,
                _ => 20
            }
            : 100;
    }

    /// <summary>
    /// A replay count cannot be fractionally scaled like damage or Block. Keep every assembly reachable, but make
    /// replay payoffs behind frequent repeatable triggers proportionally rare instead of allowing an unscaled
    /// full-card replay after every ordinary card play.
    /// </summary>
    internal static int TriggeredReplayWeight(IEnumerable<ComponentAtom> family,
        IReadOnlyList<GeneratorOperation> previous)
    {
        if (!family.Any(EffectBalanceModel.IsReplayOrRepeatedPlayEffect)) return 100;
        var trigger = EffectBalanceModel.LinkedTrigger(previous);
        if (trigger is null) return 100;
        return EffectBalanceModel.RelativeTriggerFrequency(trigger) switch
        {
            >= 2.5d => 4,
            >= 1.5d => 12,
            >= 1d => 28,
            >= 0.7d => 50,
            _ => 75
        };
    }

    /// <summary>
    /// Power cards should spend most of their text budget on effects that justify remaining in play. A payoff
    /// linked to a persistent Power trigger is part of that foundation and remains unpenalized; unrelated immediate
    /// or turn-limited auxiliaries stay possible but occur substantially less often.
    /// </summary>
    internal static int PowerAuxiliaryWeight(IEnumerable<ComponentAtom> family, GeneratedCardType type,
        IReadOnlyList<GeneratorOperation> previous)
    {
        if (type != GeneratedCardType.Power) return 100;
        var atoms = family as ComponentAtom[] ?? family.ToArray();
        // Energy already uses the global EnergyGain prior plus the repeated-trigger resource prior. Applying the
        // generic Power auxiliary penalty here made an unlinked Energy line pay a third occurrence penalty.
        if (atoms.Any(CardEffectRules.IsEnergyGainOperation)) return 100;
        if (atoms.Any(CardEffectRules.IsPersistentPowerFoundation)) return 100;
        if (EffectBalanceModel.LinkedTrigger(previous) is { Scope: OperationScope.AbilityTrigger }) return 100;
        if (previous.LastOrDefault() is { } dependency
            && CardEffectRules.IsDependencyPrefix(dependency)
            && dependency.Parameters.TryGetValue("triggerIndex", out var owner)
            && owner >= 0 && owner < previous.Count
            && previous[owner].Scope == OperationScope.AbilityTrigger)
            return 100;
        // Any auxiliary that does not belong to the persistent Power foundation uses the same low weight. An
        // ordinary immediate Damage/Block/Draw line should not be more common than an explicitly turn-limited one.
        return 10;
    }

    /// <summary>
    /// Caltrops-style Thorns is a legal standalone Power foundation in far more generated shells than in the
    /// native Silent pool. The native-frequency controller corrects most acceptance bias; this residual inverse
    /// calibration keeps the finished rate near the source rate instead of four times above it.
    /// </summary>
    internal static int PowerFoundationAcceptanceWeight(IEnumerable<ComponentAtom> family,
        GeneratedCardType type, IReadOnlyList<GeneratorOperation> previous) =>
        type == GeneratedCardType.Power
        && EffectBalanceModel.LinkedTrigger(previous) is null
        && family.Any(atom => atom.Template == "N:Thorns")
            ? 20
            : 100;

    /// <summary>
    /// A Power may begin with Damage/Block solely to anchor a restricted run-persistent growth operation on the
    /// following slot. The growth operation is then effectively forced, so tuning its own candidate weight cannot
    /// control final frequency. Calibrate the responsible first-slot anchor instead.
    /// </summary>
    internal static int RestrictedRunGrowthAnchorWeight(IEnumerable<ComponentAtom> family, GeneratedCardType type,
        IReadOnlyList<GeneratorOperation> previous, GeneratedCharacter character, bool ultimateChaos)
    {
        if (ultimateChaos || type != GeneratedCardType.Power || previous.Count != 0) return 100;
        var atoms = family as ComponentAtom[] ?? family.ToArray();
        if (character == GeneratedCharacter.Defect && atoms.Any(atom => atom.Template == "N:B")) return 50;
        if (character == GeneratedCharacter.Necrobinder && atoms.Any(CardEffectRules.IsEnemyDamage)) return 27;
        return 100;
    }

    internal static long ApplyAtomAdjustments(long weight, ComponentAtom atom, GeneratedRarity rarity,
        bool forcedSpecialX, GeneratedCardType type)
    {
        var specialXDraw = forcedSpecialX && type != GeneratedCardType.Power
            && CardEffectRules.IsCardDrawEffect(atom)
                ? SpecialXCardConverter.NonPowerDrawWeightPercent
                : 100;
        weight = PercentWeight.Apply(weight, specialXDraw);
        return PercentWeight.Apply(weight, DownsideRarityWeight(atom, rarity));
    }

    internal static int RepeatedFamilyWeightBasisPoints(IEnumerable<ComponentAtom> family,
        IReadOnlyList<GeneratorOperation> previous)
    {
        var templates = family.Select(atom => atom.Template).ToHashSet(StringComparer.Ordinal);
        var repeats = previous.Count(operation => templates.Contains(operation.Template));
        // Each existing member of the same effect family independently multiplies the candidate weight by 20%.
        // Basis points preserve the fourth occurrence's 0.8% weight instead of rounding it to a whole percent.
        var basisPoints = 10_000;
        for (var i = 0; i < repeats; i++)
            basisPoints = Math.Max(1, (basisPoints * 20 + 50) / 100);
        return basisPoints;
    }

    /// <summary>
    /// Both native Exhaust-pile lifecycle clauses replay this card, while recombination may attach any compatible
    /// payoff. Give the native companion a modest conditional prior so the original pairing remains observable
    /// without turning either trigger into a hard-coded composite operation.
    /// </summary>
    internal static int NativeExhaustReplayCompanionWeight(IEnumerable<ComponentAtom> family,
        IReadOnlyList<GeneratorOperation> previous)
    {
        if (previous.LastOrDefault() is not { } trigger) return 100;
        var expectedReplay = CardEffectRules.IsExhaustPileTurnEndTrigger(trigger)
            ? "I:PlayThisCard"
            : CardEffectRules.IsExhaustPileTurnStartTrigger(trigger)
                ? "R:PlayThisCard"
                : null;
        return expectedReplay is not null && family.Any(atom => atom.Template == expectedReplay) ? 650 : 100;
    }

    internal static int RepeatedVariantWeight(ComponentAtom atom, IReadOnlyList<GeneratorOperation> previous)
    {
        // Shared templates can contain different effects (T:Apply includes Vulnerable, Weak and enemy Strength).
        // Only the same normalized numeric field is a repeated variant.
        var repeats = CardEffectRules.FieldOccurrenceCount(previous, atom);
        return repeats switch { 0 => 100, 1 => 15, 2 => 4, _ => 1 };
    }

    /// <summary>
    /// A Power that creates another copy of itself is a durable engine rather than ordinary card movement.
    /// Keep a non-zero reconstruction path at every rarity, but concentrate the already-small chance in the
    /// upper rarities where the whole-card budget can pay its large intrinsic value.
    /// </summary>
    internal static int CopyThisCardPowerWeight(ComponentAtom atom, GeneratedRarity rarity,
        GeneratedCardType cardType)
    {
        if (cardType != GeneratedCardType.Power || !CardEffectRules.IsCopyThisCardToDiscard(atom)) return 100;
        return rarity switch
        {
            GeneratedRarity.Basic => 1,
            GeneratedRarity.Common => 3,
            GeneratedRarity.Uncommon => 10,
            GeneratedRarity.Rare or GeneratedRarity.Ancient => 30,
            _ => 100
        };
    }

    internal static int DownsideRarityWeight(ComponentAtom atom, GeneratedRarity rarity)
    {
        if (!CardEffectRules.IsNegativeEffect(atom)) return 100;
        return rarity switch
        {
            // Starting cards should mostly teach the character's positive fundamentals. Downsides remain possible
            // for native reconstruction, but are deliberately concentrated above Basic rarity.
            GeneratedRarity.Basic => BasicDownsideWeightPercent,
            GeneratedRarity.Ancient => 16,
            _ => 100
        };
    }

    internal static int BasicDownsideVariantWeight(ComponentAtom atom, GeneratedRarity rarity) =>
        rarity == GeneratedRarity.Basic && CardEffectRules.IsNegativeEffect(atom)
            ? BasicDownsideWeightPercent
            : 100;

    internal static int NegativeKeywordRarityWeight(CardTag tag, GeneratedRarity rarity)
    {
        if (tag is not (CardTag.Exhaust or CardTag.Ethereal)) return 100;
        return rarity switch
        {
            GeneratedRarity.Basic => BasicDownsideWeightPercent,
            GeneratedRarity.Ancient => 16,
            _ => 100
        };
    }

    /// <summary>
    /// Ultimate Chaos deliberately shares one component pool across every character. Osty-dependent clauses are
    /// unusually self-reinforcing inside that larger pool, so temper their family prior without suppressing Summon:
    /// Summon remains the support operation that makes the surviving Osty clauses usable. Normal Necrobinder pools
    /// retain their native distribution, and the positive floor keeps every original assembly reachable.
    /// </summary>
    internal static int UltimateOstyFamilyWeight(IEnumerable<ComponentAtom> family, bool ultimateChaos)
    {
        if (!ultimateChaos) return 100;
        var atoms = family as ComponentAtom[] ?? family.ToArray();
        if (atoms.Length == 0) return 100;
        return Average(atoms, atom => IsOstyDependent(atom) ? UltimateOstyFamilyWeightPercent : 100);
    }

    internal static bool IsOstyDependent(ComponentAtom atom) =>
        atom.Template.Contains("Osty", StringComparison.Ordinal)
        || atom.Template == "A:ProxyAtomic_Calcify";

    internal static int PayoffBudgetBonus(IReadOnlyList<GeneratorOperation> operations)
    {
        if (operations.LastOrDefault() is not { } last) return 0;
        if (DifficultConditionTier(last) is var directTier and > 0
            && (CardEffectRules.TriggerNeedsLinkedEffect(last) || CardEffectRules.IsDependencyPrefix(last)))
            return directTier switch { >= 4 => 2, >= 3 => 1, _ => 0 };
        if (!last.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
            || triggerIndex < 0 || triggerIndex >= operations.Count)
            return 0;
        var linkedCount = operations.Count(operation =>
            operation.Parameters.GetValueOrDefault("triggerIndex", -1) == triggerIndex);
        if (linkedCount >= 2) return 0;
        return DifficultConditionTier(operations[triggerIndex]) switch { >= 4 => 2, >= 3 => 1, _ => 0 };
    }

    internal static bool DifficultConditionAwaitsPayoff(IReadOnlyList<GeneratorOperation> operations)
    {
        if (operations.LastOrDefault() is not { } last) return false;
        if (CardEffectRules.TriggerNeedsLinkedEffect(last) && DifficultConditionTier(last) >= 2)
            return true;
        if (!last.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
            || triggerIndex < 0 || triggerIndex >= operations.Count
            || DifficultConditionTier(operations[triggerIndex]) < 2)
            return false;
        return operations.Count(operation =>
            operation.Parameters.GetValueOrDefault("triggerIndex", -1) == triggerIndex) < 2;
    }

    internal static int DifficultConditionTier(ComponentAtom atom) =>
        DifficultConditionTier(atom.Template, OperationRuntimeSpecCompiler.GetOrCompile(atom));

    internal static int DifficultConditionTier(GeneratorOperation operation) =>
        DifficultConditionTier(operation.Template, OperationRuntimeSpecCompiler.GetOrCompile(operation));

    private static int DifficultConditionTier(string template, OperationRuntimeSpec spec)
    {
        if (template is "A:whenEnergyCostAtLeast" or "NCR:WheneverHighCostCardPlayed"
            && spec.Values.FirstOrDefault(value => value.Id == "threshold")?.BaseValue >= 3)
            return 2;
        if (template == "C:playableIfDrawPileEmpty") return 4;
        if (template == "CL:IfHandEmpty"
            || template == "R:IfCardsPlayedAtLeastThisTurn"
            || template == "R:AtTurnEndWhenTopOfDraw"
            || spec.Flags.Contains("difficult_condition_tier_3"))
            return 3;
        if (template is "C:ifTargetPoisoned" or "C:ifLastDrawnSkill" or "CL:IfNoAttacksInHand"
                or "D:IfHasFrost" or "R:IfEnergyXAtLeast"
            || spec.Flags.Contains("difficult_condition_tier_2"))
            return 2;
        return template is "D:IfEnemyIntendsAttack" or "D:IfCardsPlayedBelow" ? 1 : 0;
    }

    private static int Average(IEnumerable<ComponentAtom> atoms, Func<ComponentAtom, int> selector)
    {
        var total = 0;
        var count = 0;
        foreach (var atom in atoms)
        {
            total += selector(atom);
            count++;
        }
        return count == 0 ? 100 : Math.Max(1, total / count);
    }

}

/// <summary>Shared numeric sampling policy. Mechanism-specific bounds live here instead of in the assembly loop.</summary>
internal static class NumericGenerationTuning
{
    internal static int OriginalValueChance(ComponentAtom atom, int benefitLines) => atom.Template switch
    {
        "NCR:OstyDamage" or "NCR:OstyAllDamage" => 1,
        "NCR:ApplyDoom" or "NCR:ApplyDoomAll" => 5,
        // Terraforming's native 7 Vigor remains reconstructible, but it is an outlier rather than the center of
        // the recombined scalar distribution. Most Vigor instances use the temporary-Strength-sized budget.
        "R:GainVigor" or "CL:GainVigor" => 2,
        "A:whenEnergyCostAtLeast" or "NCR:WheneverHighCostCardPlayed"
            or "NCR:IncreaseAllCardCostsThisTurn" or "D:IncreaseThisCardCost" => 0,
        _ when CardEffectRules.IsRandomCardGeneration(atom) => 2,
        _ when CardEffectRules.IsEnergyGainOperation(atom) => 3,
        _ => benefitLines switch { 1 => 18, 2 => 12, 3 => 7, _ => 4 }
    };

    internal static int SampleAroundCenter(Random random, int center, int payoffScale)
    {
        var span = center <= 2 && payoffScale < 80 ? 0 : center switch
        {
            >= 30 => 6,
            >= 20 => 4,
            >= 14 => 3,
            >= 10 => 2,
            >= 5 => 1,
            >= 3 => 1,
            _ => 1
        };
        var minimum = Math.Max(1, center - span);
        var maximum = Math.Max(minimum, center + span);
        var roll = random.Next(100);
        return roll switch
        {
            < 8 => minimum,
            < 20 => maximum,
            < 60 => random.Next(minimum, maximum + 1),
            _ => (random.Next(minimum, maximum + 1) + random.Next(minimum, maximum + 1) + 1) / 2
        };
    }

    internal static int AdjustSampledValue(Random random, ComponentAtom atom, int slot, int value)
    {
        if (slot == 0 && atom.Template is "N:CreateShiv" or "N:CreateInkShiv")
            value = SampleShivProducerCount(random, atom.Template, value);
        if (slot == 0 && CardEffectRules.IsStarGainOperation(atom) && value > 1 && random.Next(100) < 28)
            value--;
        if (slot == 0
            && atom.Template is "N:Draw" or "I:DrawWithRetain" or "I:DrawAndBlockIfSkill"
            && value >= 4)
            // Four-card draw remains reachable, especially for pure two-cost cards whose final floor restores it,
            // but ordinary multi-effect cards should overwhelmingly stop at three.
            value = random.Next(100) < 82 ? 3 : 4;
        return value;
    }

    internal static int SampleMandatoryHandExhaustCount(Random random)
    {
        // Every native player-selected mandatory Exhaust removes exactly one card. Unlike "up to", requiring two
        // or more targets can make the card unusable and is no longer a legal generated component.
        _ = random;
        return 1;
    }

    internal static int SampleMandatoryDiscardCount(Random random)
    {
        // Ordinary discard is still a downside/requirement, but no longer buys positive card budget. Keep one
        // overwhelmingly dominant, two uncommon, and preserve only a very small higher-count tail.
        var roll = random.Next(10_000);
        return roll switch
        {
            < 8_400 => 1,
            < 9_800 => 2,
            < 9_960 => 3,
            < 9_995 => 4,
            _ => 5
        };
    }

    /// <summary>
    /// Ironclad's eight native self-HP payments are 1/1/1/1/2/2/3/6. Preserve their roughly two-HP mean without
    /// copying the sparse sample's artificial jump from 5 to 6: one remains dominant, while 3/4/5/6 form a
    /// smoothly decaying 13%/6%/3.5%/1.5% tail. Six HP is uncommon but remains a real high-roll payment.
    /// Repeated trigger payments are fixed at one, matching Crimson Mantle and Inferno and preventing a harmless
    /// scalar randomizer from turning a per-turn cost into an accidental death sentence.
    /// </summary>
    internal static int SampleSelfHpLoss(Random random, IReadOnlyList<GeneratorOperation> previous)
    {
        if (EffectBalanceModel.LinkedTrigger(previous) is { } trigger
            && EffectBalanceModel.HasRepeatedOrMultiplicativePayoff(trigger))
            return 1;

        var roll = random.Next(10_000);
        return roll switch
        {
            < 4_700 => 1,
            < 7_600 => 2,
            < 8_900 => 3,
            < 9_500 => 4,
            < 9_850 => 5,
            _ => 6
        };
    }

    internal static int ClampSampledValue(ComponentAtom atom, int slot, int value,
        IReadOnlyList<GeneratorOperation> previous, GeneratedCharacter? character = null,
        bool ultimateChaos = false)
    {
        if (slot == 0 && DurationOnlyStackCap(atom.Template,
                OperationRuntimeSpecCompiler.GetOrCompile(atom), character, ultimateChaos) is { } cap)
            return Math.Clamp(value, 1, cap);
        if (slot == 0 && atom.Template is "N:Draw" or "I:DrawAndBlockIfSkill" or "I:DrawWithRetain")
            return Math.Clamp(value, 1, 4);
        // The native compact generated-card picker supports at most three candidates. Four candidates remain a
        // useful upper roll, but the runtime deliberately presents that case through the multiplayer-safe grid.
        // Keep the authored value at four or below so generated snapshots cannot request an unbounded choice list.
        if (slot == 0 && atom.Template is "CL:ProxyAtomic_Discovery" or "I:ProxyAtomic_Quasar"
                or "CL:ProxyAtomic_Splash" or "CL:ChooseFromRandomDrawCards")
            return Math.Clamp(value, 1, 4);
        if (slot == 0 && atom.Template == "R:PlaySelectedSkillMultipleTimes")
            return Math.Clamp(value, 1, 3);
        if (slot == 0 && atom.Template == "A:VoidFormFirstCardsFree")
            return Math.Clamp(value, 1, 3);
        if (slot > 0 && atom.Template is "N:AllD" or "N:RandomD" or "N:RandomPoison")
            return Math.Clamp(value, 1, 5);
        if (slot == 0 && CardEffectRules.IsEnergyGainOperation(atom))
            return Math.Clamp(value, 1, 4);
        if (slot == 0 && atom.Template is "R:GainVigor" or "CL:GainVigor")
            // Recombined values stay close to temporary Strength. Terraforming's exact native 7 remains reachable
            // through the deliberately small preserve-original branch above, which returns before this clamp.
            return Math.Clamp(value, 1, 4);
        if (slot == 0 && OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("plating_reference"))
            // Plating is a decaying persistent Block source. One or two stacks are both text-heavy and too small to
            // support a generated card line; every native direct grant starts at four or more. Keep a slightly wider
            // reconstruction space than native while rejecting non-functional low rolls.
            return Math.Max(3, value);
        if (slot == 0 && CardEffectRules.IsDirectOrbChannel(atom))
        {
            var maximum = previous.LastOrDefault()?.Template == "D:ForEachEnemy" ? 1 : 3;
            return Math.Clamp(value, 1, maximum);
        }
        if (slot == 0 && atom.Template == "D:LoseOrbSlots")
            return Math.Clamp(value, 1, 2);
        if (slot == 0 && atom.Template == "D:GainOrbSlots")
            return 1;
        if (slot == 0 && CardEffectRules.IsPositivePermanentStatGain(atom))
            return Math.Clamp(value, 1, 2);
        if (slot == 0 && CardEffectRules.IsPermanentStrengthOrDexterityChange(atom))
            return Math.Clamp(value, 1, 3);
        if (slot == 0 && CardEffectRules.IsEnemyStrengthReduction(atom)
            && OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("this_turn_reference"))
            return Math.Clamp(value, 1,
                OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("all_enemies_reference") ? 6 : 9);
        if (slot == 0 && atom.Template == "NCR:BlockTripleOstyMaxHp")
            return Math.Clamp(value, 1, 3);
        if (slot == 0 && atom.Template is "A:whenEnergyCostAtLeast" or "NCR:WheneverHighCostCardPlayed"
                or "NCR:IncreaseAllCardCostsThisTurn")
            return Math.Clamp(value, 1, 3);
        return value;
    }

    /// <summary>
    /// Vulnerable, Weak and Intangible mostly convert their stack count into duration. Very large generated values
    /// consume text/budget without producing a proportionate tactical difference. Ironclad and Ultimate Chaos get
    /// a wider Vulnerable cap because several Ironclad mechanics explicitly profit from accumulated Vulnerable.
    /// </summary>
    private static int? DurationOnlyStackCap(string template, OperationRuntimeSpec spec,
        GeneratedCharacter? character,
        bool ultimateChaos)
    {
        if (spec.Flags.Any(flag => flag is "uses_energy_x" or "uses_star_x")
            || spec.Values.Any(value => value.Source is "energy_x" or "star_x" or "special_x")) return null;
        if (spec.Flags.Contains("vulnerable_reference")
            && (spec.Flags.Contains("apply_status_reference")
                || template is "R:ApplyVulnerableAll" or "NCR:ApplyVulnerableAll" or "CL:ApplyVulnerableAll"))
            return ultimateChaos || character == GeneratedCharacter.Ironclad ? 6 : 4;
        if (spec.Flags.Contains("weak_reference")
            && (spec.Flags.Contains("apply_status_reference")
                || template is "R:ApplyWeakAll" or "NCR:ApplyWeakAll" or "CL:ApplyWeakAll" or "N:AllWeak"))
            return 4;
        if (template == "N:Intangible" || spec.Flags.Contains("intangible_reference"))
            return 3;
        if (template == "CL:NoBlockFromCards")
            return 3;
        if (template == "NCR:DoubleVulnerableWeak")
            return 3;
        return null;
    }

    internal static int? DurationOnlyStackCap(GeneratorOperation operation, GeneratedCharacter character,
        bool ultimateChaos) => DurationOnlyStackCap(operation.Template,
        OperationRuntimeSpecCompiler.GetOrCompile(operation), character, ultimateChaos);

    internal static int SampleEnergyGainValue(Random random, int target)
    {
        var roll = random.Next(1000);
        return target switch
        {
            <= 1 => roll < 925 ? 1 : roll < 995 ? 2 : roll < 999 ? 3 : 4,
            2 => roll < 280 ? 1 : roll < 970 ? 2 : roll < 997 ? 3 : 4,
            _ => roll < 150 ? 1 : roll < 970 ? 2 : roll < 998 ? 3 : 4
        };
    }

    internal static int SampleRandomGeneratedCardCount(Random random)
    {
        var roll = random.Next(1000);
        return roll < 700 ? 1 : roll < 920 ? 2 : roll < 990 ? 3 : 4;
    }

    internal static int SampleShivProducerCount(Random random, string template, int sampled)
    {
        sampled = Math.Max(1, sampled);
        var roll = random.Next(100);
        if (template == "N:CreateInkShiv")
        {
            if (sampled <= 2) return sampled;
            if (sampled == 3) return roll < 70 ? 2 : 3;
            return roll switch { < 45 => 2, < 90 => 3, < 99 => 4, _ => 5 };
        }
        if (sampled <= 3) return sampled;
        if (sampled == 4) return roll < 25 ? 3 : 4;
        if (sampled == 5) return roll switch { < 30 => 3, < 90 => 4, _ => 5 };
        return roll switch { < 10 => 3, < 75 => 4, < 98 => 5, _ => 6 };
    }

    internal static int SampleHighCostTriggerThreshold(Random random)
    {
        var roll = random.Next(1000);
        return roll < 200 ? 1 : roll < 850 ? 2 : 3;
    }

    internal static int SampleSkillsPerReturnThreshold(Random random)
    {
        // Center tightly on Make It So's native threshold of three: 2/3/4 remain possible, while extreme values do
        // not turn a self-return clause into either a trivial loop or dead text.
        var roll = random.Next(1000);
        return roll < 150 ? 2 : roll < 900 ? 3 : 4;
    }

    internal static int SampleAllCardsCostIncrease(Random random)
    {
        var roll = random.Next(10_000);
        return roll < 8_950 ? 1 : roll < 9_970 ? 2 : 3;
    }

    internal static int SampleSelfCardCostIncrease(Random random)
    {
        // Unlike a temporary all-card surcharge, this payment only matters when the same card is seen again.
        // Keep +1 dominant, retain a meaningful +2 tail, and make the highly punishing +3 exceptional.
        var roll = random.Next(10_000);
        return roll < 8_200 ? 1 : roll < 9_850 ? 2 : 3;
    }

    internal static int ApplyUltimateComponentBonus(Random random, int count, bool ultimateChaos)
    {
        if (!ultimateChaos) return count;
        if (random.Next(100) < 65) count++;
        if (random.Next(100) < 20) count++;
        return Math.Min(5, count);
    }

    internal static int ApplyUltimateEnergyCostDiscount(Random random, int cost, bool ultimateChaos)
    {
        if (!ultimateChaos || cost < 0) return cost;
        // Ultimate Chaos keeps X and low-cost shells intact. Only cards that actually cost more than one
        // ordinary Energy may receive the mode's discount, and no discount may cross the one-Energy floor.
        if (cost <= 1) return cost;
        var chance = cost switch { 2 => 42, 3 => 58, _ => 70 };
        if (random.Next(100) < chance) cost--;
        if (cost > 1 && random.Next(100) < 12) cost--;
        return Math.Max(1, cost);
    }

    internal static int ApplyStarCostAdjustment(Random random, int cost, bool ultimateChaos)
    {
        if (cost <= 0) return cost;

        // Fixed Star payments remain a Regent identity, but the native shell histogram is slightly too dense once
        // it is recombined independently from Energy and effects. Keep every source outcome reachable while making
        // a no-Star result a modest (rather than dominant) branch. Ultimate Chaos uses the same shared sixth-column
        // resource policy with a slightly stronger normalization because every character can inherit Regent shells.
        if (random.Next(10_000) < (ultimateChaos ? 1_200 : 800)) return -1;
        if (cost == 1) return 1;

        // Four-to-six Stars are bankable but still strategically restrictive. A single generic -1 roll left most
        // five/six-Star shells in the extreme band, so sample that tail explicitly. All original values retain a
        // non-zero path, while >=4 is now exceptional rather than a common consequence of the source histogram.
        var roll = random.Next(10_000);
        return cost switch
        {
            2 => roll < (ultimateChaos ? 3_200 : 1_800) ? 1 : 2,
            3 => roll < (ultimateChaos ? 4_000 : 2_400) ? 2 : 3,
            4 => roll < (ultimateChaos ? 9_200 : 8_800) ? 3 : 4,
            5 => roll < (ultimateChaos ? 7_800 : 7_000) ? 3
                : roll < (ultimateChaos ? 9_500 : 9_200) ? 4 : 5,
            _ => roll < (ultimateChaos ? 7_500 : 6_500) ? 3
                : roll < (ultimateChaos ? 9_100 : 8_700) ? 4
                : roll < (ultimateChaos ? 9_800 : 9_700) ? 5 : cost
        };
    }

    internal static bool KeepStarXCost(Random random, bool hasStarCostX, bool ultimateChaos) =>
        !hasStarCostX || ultimateChaos || random.Next(10_000) >= 800;

    internal static int ApplyBasicStarCostFloor(int cost, GeneratedRarity rarity) =>
        // Falling Star is the native fixed-Star Basic reference. Starting-pool cards use its stable two-Star
        // payment instead of inheriting the much wider 1-6 Star shell range from higher rarities.
        rarity == GeneratedRarity.Basic && cost > 0 ? 2 : cost;

    internal static int ApplyUnconditionalStarGainSoftCap(Random random, int amount)
    {
        if (amount <= 3) return amount;
        // Unconditional reusable Star gain above three can erase most of the resource system. Keep a small native-
        // style high roll instead of a hard cap; Exhaust cards and triggered gains are handled by the caller.
        return random.Next(100) < 90 ? 3 : amount;
    }

    internal static int ApplyUltimateValueBonus(ComponentAtom atom, int value, bool ultimateChaos)
    {
        if (!ultimateChaos || !EffectBalanceModel.IsScalableReward(atom)) return value;
        return Math.Max(1, (int)Math.Round(value * 1.2d, MidpointRounding.AwayFromZero));
    }

    internal static int ApplyValueBonuses(ComponentAtom atom, int value, GeneratedCharacter character,
        bool ultimateChaos) => ApplyUltimateValueBonus(atom, value, ultimateChaos);
}

/// <summary>Shared pricing rules for explicit card downsides.</summary>
internal static class NegativeEffectTuning
{
    internal const int ExactReconstructionChance = 3;

    /// <summary>
    /// Stackable payments which do not prevent this card itself from resolving buy a fixed amount of positive
    /// budget per stack.  Keeping this value additive is important: losing two Strength is exactly two copies of
    /// losing one Strength, and the same payment does not become proportionally larger merely because the card is
    /// Ancient or already expensive.  Exhaust/Ethereal, no-more-draw, killing Osty and the two extreme lifecycle
    /// downsides intentionally stay in the older nonlinear/special paths.
    /// </summary>
    internal static double LinearCompensationValue(GeneratorOperation operation,
        IReadOnlyList<GeneratorOperation>? operations = null, int operationIndex = -1)
    {
        var amount = Math.Max(1, OperationRuntimeSpecCompiler.PrimaryStaticLiteralValue(operation, 1));
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        ComponentValuationApi.TryGetNegativePricing(spec, out var customLinearValue, out _);
        var unitValue = customLinearValue > 0d ? customLinearValue : operation.Template switch
        {
            // Native 1-3 HP anchors imply a modest, nearly additive premium. Repeated HP loss is multiplied by
            // its expected resolutions below rather than sent through the old nonlinear severity curve.
            "N:HP-" => 100d,
            // Temporary Focus can usually be sequenced after Orb use; permanent losses are substantially larger.
            "D:LoseTemporaryFocus" => 85d,
            "D:LoseFocus" => 1_900d,
            "N:LoseDex" => 800d,
            "D:LoseOrbSlots" => 1_800d,
            "NCR:LoseStrength" => 735d,
            "NCR:ApplySelfDoom" => 650d,
            "NCR:IncreaseAllCardCostsThisTurn" => 300d,
            "R:DiscardTopOfDraw" => 100d,
            // Random or automatic consumption is an additive payment. Player-selected and "up to" Exhaust is
            // controlled deck-thinning and is priced as a positive effect instead.
            "N:Exhaust" when spec is { Variant: "referenced", CardFilter: "skill" } =>
                EffectBalanceModel.ReferencedSkillExhaustValuePerCard,
            "N:Exhaust" when spec.Variant is "random" or "referenced" or "top" => 100d,
            "I:ExhaustRandomAttack" => 100d,
            "T:Apply" when CardEffectRules.IsEnemyStrengthGain(operation) => 1_500d,
            "R:AddDebrisToHand" => StatusUnitValue(operation),
            _ when DerivativeSlotCatalog.ProducesStatus(operation)
                && operation.Template != "R:FillHandWithDebris" => StatusUnitValue(operation),
            _ => 0d
        };
        if (unitValue <= 0d) return 0d;

        var frequency = 1d;
        if (operations is not null && operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
            && triggerIndex >= 0 && triggerIndex < operations.Count
            && (operationIndex < 0 || triggerIndex < operationIndex))
        {
            var trigger = operations[triggerIndex];
            if (EffectBalanceModel.HasRepeatedOrMultiplicativePayoff(trigger))
                frequency = Math.Max(1d, EffectBalanceModel.ExpectedTriggerResolutions(trigger, 2.4d));
        }
        return unitValue * amount * frequency;
    }

    internal static double TotalLinearCompensationValue(IReadOnlyList<GeneratorOperation> operations)
    {
        var total = 0d;
        for (var index = 0; index < operations.Count; index++)
            total += LinearCompensationValue(operations[index], operations, index);
        return total;
    }

    private static double StatusUnitValue(GeneratorOperation operation)
    {
        var derivative = DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template);
        if (derivative is not null && DerivativeSlotCatalog.IsCurse(derivative))
            return 0d; // The curse easter egg already owns a separate large whole-card compensation path.
        return derivative?.Id switch
        {
            "dazed" or "burn" => 50d,
            "wound" => 80d,
            "slimed" => 200d,
            "void" => 550d,
            "debris" => 250d,
            _ => operation.Template switch
            {
                "D:CreateDazedInDiscard" or "D:CreateBurnInDiscard" => 50d,
                "D:CreateTwoWoundsInDiscard" => 80d,
                "D:CreateSlimeInDiscard" => 200d,
                "D:CreateVoidInDiscard" => 550d,
                "R:AddDebrisToHand" => 250d,
                _ => 100d
            }
        };
    }

    /// <summary>
    /// Direct whole-card multiplier owned by one non-linear downside. Additive payments return 1 here and are
    /// priced only by LinearCompensationValue. This deliberately replaces the former severity integer and lookup
    /// table: every multiplier-style downside now exposes its actual budget factor in one place.
    /// </summary>
    internal static double BaseMultiplier(GeneratorOperation operation)
    {
        if (!CardEffectRules.IsNegativeEffect(operation)) return 1d;
        if (LinearCompensationValue(operation) > 0d) return 1d;

        var amount = Math.Max(1, OperationRuntimeSpecCompiler.PrimaryStaticLiteralValue(operation, 1));
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        if (ComponentValuationApi.TryGetNegativePricing(spec, out _, out var customMultiplier)
            && customMultiplier > 1d)
            return customMultiplier;
        var derivative = DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template);
        return operation.Template switch
        {
            // Extreme lifecycle restrictions retain their native whole-card anchors instead of receiving a
            // separate hard-coded payoff amount outside the shared budget model.
            "CL:DieOnUnblockedAttack" => 4.00d,
            "CL:NoBlockFromCards" => 1d + 0.70d * amount,
            "R:FillHandWithDebris" => 3.00d,

            // Raising only this card's future cost is multiplicative: the card must buy proportionally more output
            // while remaining playable. The three printed values preserve the former calibrated standalone ratios.
            "D:IncreaseThisCardCost" => amount switch { <= 1 => 1.24d, 2 => 1.66d, _ => 2.26d },
            "N:DiscardAll" => 1.50d,
            // Ordinary discard has semantic costs and enables Sly, but deliberately grants no numeric budget.
            "N:Discard" => 1d,
            "N:Exhaust" when spec is { Opcode: "exhaust_card", Variant: "all", CardFilter: "any" } => 1.36d,
            "N:Exhaust" when spec is { Opcode: "exhaust_card", Variant: "all", CardFilter: "non_attack" } => 1.36d,
            "I:PreventDrawThisTurn" => 1.36d,
            "D:DrawAndDiscardNonZero" => amount >= 4 ? 1.24d : 1.14d,
            "NCR:KillOsty" => 1.84d,
            "R:EndTurn" => 2.00d,
            // This modifier owns a contextual multiplier in EffectiveMultiplier: its cost depends on both the
            // printed penalty and the number/type of damage hosts it weakens.
            "M:DamageMinusPerCardInHand" => 1d,
            // The curse-status Easter egg has additional authored generation bonuses. Retain the small ordinary
            // status multiplier it historically received, but make it explicit instead of falling through a
            // generic minimum bucket.
            _ when derivative is not null && DerivativeSlotCatalog.IsCurse(derivative) => 1.14d,
            // A new nonlinear downside must be deliberately priced. Silently inheriting a generic multiplier
            // recreates the old severity-table problem and makes reverse-fitting impossible.
            _ => throw new InvalidOperationException(
                $"Multiplier-style downside '{operation.Template}' has no explicit multiplier.")
        };
    }

    /// <summary>A repeated downside composes its own multiplier once per expected resolution.</summary>
    internal static double EffectiveMultiplier(GeneratorOperation operation,
        IReadOnlyList<GeneratorOperation> operations, int operationIndex = -1)
    {
        var multiplier = BaseMultiplier(operation);
        if (operation.Template == "M:DamageMinusPerCardInHand" && operationIndex >= 0)
        {
            var amount = Math.Max(1, OperationRuntimeSpecCompiler.PrimaryStaticLiteralValue(operation, 1));
            var lostValue = amount * 3d * 100d
                * EffectBalanceModel.DamageModifierHostValueMultiplier(operation, operationIndex, operations);
            multiplier = 1d + lostValue / 1_500d;
        }
        if (multiplier <= 1d
            || !operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
            || triggerIndex < 0 || triggerIndex >= operations.Count
            || operationIndex >= 0 && triggerIndex >= operationIndex)
            return multiplier;
        var trigger = operations[triggerIndex];
        // Ending the turn is a single, strong lifecycle payment. When it is gated or repeated, price the complete
        // 2.00x payment by the trigger's expected resolution count instead of compounding it exponentially. This
        // also lets one-shot conditions below 100% frequency reduce the payment naturally. Turn-boundary owners
        // are rejected by CardEffectRules because ending a turn from its own start/end hook is not a usable card.
        if (operation.Template == "R:EndTurn")
        {
            var endTurnFrequency = EffectBalanceModel.ExpectedTriggerResolutions(trigger, 2.4d);
            return Math.Max(1d, multiplier * endTurnFrequency);
        }
        if (!EffectBalanceModel.HasRepeatedOrMultiplicativePayoff(trigger)) return multiplier;
        var frequency = Math.Max(1d, EffectBalanceModel.ExpectedTriggerResolutions(trigger, 2.4d));
        return Math.Pow(multiplier, frequency);
    }

    internal static double TotalMultiplier(IReadOnlyList<GeneratorOperation> operations,
        IReadOnlyCollection<CardTag>? tags = null, bool? hasPrintedResourceCost = null,
        GeneratedCardType? cardType = null, GeneratedCharacter? character = null)
    {
        _ = character; // Character-specific downside values belong in explicit operation prices, not this combiner.
        var multiplier = 1d;
        for (var index = 0; index < operations.Count; index++)
            multiplier *= EffectiveMultiplier(operations[index], operations, index);

        var exhaustOffset = false;
        var paidReusableCopies = 0;
        if (hasPrintedResourceCost is { } paid && cardType is { } type)
        {
            var roles = operations
                .Select(operation => CardEffectRules.CopyThisCardBudgetRole(operation, paid, type, tags))
                .ToArray();
            exhaustOffset = roles.Contains(CopyThisCardBudgetRole.ExhaustOffset);
            paidReusableCopies = roles.Count(role => role == CopyThisCardBudgetRole.PaidReusableDownside);
        }

        if (tags is not null)
        {
            var hasExhaust = tags.Contains(CardTag.Exhaust) && !exhaustOffset;
            var hasEthereal = tags.Contains(CardTag.Ethereal);
            if (hasExhaust) multiplier *= 1.45d;
            if (hasEthereal) multiplier *= 1.07d;
            // Exhaust + Ethereal is worse than either lifecycle payment alone: whether played or retained, the
            // card disappears. Preserve the reviewed 2.26 combined factor as an explicit interaction multiplier.
            if (hasExhaust && hasEthereal) multiplier *= 2.26d / (1.45d * 1.07d);
        }
        if (paidReusableCopies > 0) multiplier *= Math.Pow(1.14d, paidReusableCopies);
        return Math.Max(1d, multiplier);
    }

    internal static int CompensationPercent(double multiplier) =>
        Math.Max(100, (int)Math.Round(multiplier * 100d, MidpointRounding.AwayFromZero));

    internal static bool HasRepeatedTriggeredDownside(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
            if (EffectiveMultiplier(operations[index], operations, index)
                    > BaseMultiplier(operations[index]) + 0.0001d
                || LinearCompensationValue(operations[index], operations, index)
                    > LinearCompensationValue(operations[index]))
                return true;
        return false;
    }

    internal static int CostDiscount(double multiplier, bool hasScalablePositive, bool hasSelfCostIncrease,
        int positiveEffectCount)
    {
        var discount = multiplier switch { >= 2.50d => 2, >= 1.84d => 1, _ => 0 };
        if (!hasScalablePositive && multiplier > 1.0001d) discount = Math.Max(discount, 1);
        if (hasSelfCostIncrease && positiveEffectCount <= 1) discount = Math.Max(discount, 1);
        return discount;
    }
}

internal static class PercentWeight
{
    internal static long Apply(long weight, int percent) => weight * percent / 100;
}

/// <summary>
/// Shared context weights for authored Retain/Innate keywords and their upgrade candidates. Two Stars equal one
/// Energy throughout the balance model. X is assigned one reference Energy here: it is not a free fixed-cost card,
/// but it also must not receive the high-cost Retain multiplier without a printed payment.
/// </summary>
internal static class CardKeywordTuning
{
    internal static double EffectiveCost(int energyCost, int starCost, bool hasStarCostX)
    {
        var energy = energyCost < 0 ? 1d : energyCost;
        var stars = hasStarCostX ? 1d : Math.Max(0, starCost) / 2d;
        return Math.Max(0d, energy + stars);
    }

    internal static int RetainWeightPercent(int energyCost, int starCost, bool hasStarCostX)
    {
        var effectiveCost = EffectiveCost(energyCost, starCost, hasStarCostX);
        if (effectiveCost <= 0d) return 0;
        return Math.Clamp((int)Math.Round(effectiveCost * 50d), 1, 200);
    }

    internal static int InnateWeightPercent(int energyCost, int starCost, bool hasStarCostX,
        GeneratedCardType type, bool hasExhaust)
    {
        // A 70% normalization keeps the pool-wide rate near its native-smoothed prior after the two stackable
        // 1.5x affinities redistribute Innate toward free cards and one-shot/Powers.
        var weight = 70d;
        if (energyCost == 0 && starCost <= 0 && !hasStarCostX) weight *= 1.5d;
        if (hasExhaust || type == GeneratedCardType.Power) weight *= 1.5d;
        return (int)Math.Round(weight);
    }

    internal static void Validate()
    {
        if (RetainWeightPercent(0, -1, false) != 0
            || RetainWeightPercent(1, -1, false) != 50
            || RetainWeightPercent(2, -1, false) != 100
            || RetainWeightPercent(3, -1, false) != 150
            || RetainWeightPercent(4, -1, false) != 200
            || RetainWeightPercent(0, 1, false) != 25
            || RetainWeightPercent(0, 2, false) != 50
            || RetainWeightPercent(-1, -1, false) != 50
            || InnateWeightPercent(1, -1, false, GeneratedCardType.Skill, false) != 70
            || InnateWeightPercent(0, -1, false, GeneratedCardType.Skill, false) != 105
            || InnateWeightPercent(1, -1, false, GeneratedCardType.Power, false) != 105
            || InnateWeightPercent(0, -1, false, GeneratedCardType.Skill, true) != 158)
            throw new InvalidOperationException("保留费用权重或固有条件权重发生了意外变化。");
    }
}

internal static class BasisPointWeight
{
    internal static long Apply(long weight, int basisPoints) => weight * basisPoints / 10_000;
}

/// <summary>
/// Fixed-Energy priors expressed as data rather than character-specific switch trees. Each row is indexed by
/// printed cost 0..4. Ultimate Chaos sums the five playable-character rows exactly as before.
/// </summary>
internal static class EnergyCostTuning
{
    private const int ZeroResourceCostWeightPercent = 65;
    private const int RareZeroResourceCostWeightPercent = 45;
    private const int RegentZeroResourceCostWeightPercent = 65;
    private static readonly int[] Basic = [5, 90, 5, 0, 0];
    private static readonly GeneratedCharacter[] PlayableCharacters =
    [
        GeneratedCharacter.Ironclad, GeneratedCharacter.Silent, GeneratedCharacter.Defect,
        GeneratedCharacter.Necrobinder, GeneratedCharacter.Regent
    ];
    private static readonly IReadOnlyDictionary<(GeneratedCharacter, GeneratedRarity), int[]> Rows =
        new Dictionary<(GeneratedCharacter, GeneratedRarity), int[]>
        {
            [(GeneratedCharacter.Ironclad, GeneratedRarity.Common)] = [1, 16, 3, 0, 0],
            [(GeneratedCharacter.Ironclad, GeneratedRarity.Uncommon)] = [9, 20, 5, 4, 0],
            [(GeneratedCharacter.Ironclad, GeneratedRarity.Rare)] = [6, 11, 9, 2, 0],
            [(GeneratedCharacter.Ironclad, GeneratedRarity.Ancient)] = [6, 11, 9, 2, 0],

            [(GeneratedCharacter.Silent, GeneratedRarity.Common)] = [4, 12, 4, 0, 0],
            [(GeneratedCharacter.Silent, GeneratedRarity.Uncommon)] = [7, 17, 7, 3, 0],
            [(GeneratedCharacter.Silent, GeneratedRarity.Rare)] = [4, 9, 6, 7, 0],
            [(GeneratedCharacter.Silent, GeneratedRarity.Ancient)] = [4, 9, 6, 7, 0],

            [(GeneratedCharacter.Defect, GeneratedRarity.Common)] = [6, 13, 1, 0, 0],
            [(GeneratedCharacter.Defect, GeneratedRarity.Uncommon)] = [6, 20, 7, 2, 0],
            [(GeneratedCharacter.Defect, GeneratedRarity.Rare)] = [5, 10, 7, 4, 1],
            [(GeneratedCharacter.Defect, GeneratedRarity.Ancient)] = [5, 10, 7, 4, 1],

            [(GeneratedCharacter.Necrobinder, GeneratedRarity.Common)] = [2, 15, 2, 1, 0],
            [(GeneratedCharacter.Necrobinder, GeneratedRarity.Uncommon)] = [3, 25, 5, 1, 1],
            [(GeneratedCharacter.Necrobinder, GeneratedRarity.Rare)] = [6, 9, 6, 5, 1],
            [(GeneratedCharacter.Necrobinder, GeneratedRarity.Ancient)] = [6, 9, 6, 5, 1],

            [(GeneratedCharacter.Regent, GeneratedRarity.Common)] = [4, 14, 2, 0, 0],
            [(GeneratedCharacter.Regent, GeneratedRarity.Uncommon)] = [9, 19, 5, 1, 1],
            [(GeneratedCharacter.Regent, GeneratedRarity.Rare)] = [6, 13, 6, 2, 0],
            [(GeneratedCharacter.Regent, GeneratedRarity.Ancient)] = [6, 13, 6, 2, 0],

            [(GeneratedCharacter.Colorless, GeneratedRarity.Uncommon)] = [14, 12, 4, 0, 0],
            [(GeneratedCharacter.Colorless, GeneratedRarity.Rare)] = [5, 9, 2, 5, 0]
        };

    internal static int Weight(int cost, GeneratedRarity rarity, GeneratedCharacter character,
        bool ultimateChaos, bool hasStarPayment = false)
    {
        var native = ultimateChaos
            ? PlayableCharacters.Sum(owner => NativeWeight(cost, rarity, owner))
            : NativeWeight(cost, rarity, character);
        if (native <= 0) return 0;
        // Keep all native outcomes reachable while making 3 Energy uncommon and 4 Energy exceptional. A true
        // 0-Energy/0-Star card receives a further global suppression; a 0-Energy card that still pays Stars is not
        // free and retains the ordinary low-cost prior.
        var weighted = cost switch { >= 4 => native, 3 => native, _ => native * 8 };
        if (cost != 0 || hasStarPayment) return weighted;
        var zeroCostPercent = rarity == GeneratedRarity.Rare
            ? RareZeroResourceCostWeightPercent
            : ZeroResourceCostWeightPercent;
        weighted = Math.Max(1, weighted * zeroCostPercent / 100);
        // Regent's native shell histogram contains many printed 0-Energy cards, but a substantial portion of
        // those pay Stars. Once Star payment is absent, inheriting the whole native zero-cost row produces far too
        // many genuinely free cards. Keep every native outcome reachable while separating 0/0 from 0+Stars.
        return !ultimateChaos && character == GeneratedCharacter.Regent
            ? Math.Max(1, weighted * RegentZeroResourceCostWeightPercent / 100)
            : weighted;
    }

    internal static int NativeWeight(int cost, GeneratedRarity rarity, GeneratedCharacter character)
    {
        if ((uint)cost >= Basic.Length) return 0;
        if (rarity == GeneratedRarity.Basic) return Basic[cost];
        if (Rows.TryGetValue((character, rarity), out var row)) return row[cost];
        // Preserve the old smoothing fallback for invalid Ironclad/Silent rarity requests.
        return character is GeneratedCharacter.Ironclad or GeneratedCharacter.Silent ? 1 : 0;
    }

    internal static void Validate()
    {
        if (NativeWeight(1, GeneratedRarity.Basic, GeneratedCharacter.Regent) != 90
            || NativeWeight(3, GeneratedRarity.Rare, GeneratedCharacter.Silent) != 7
            || NativeWeight(4, GeneratedRarity.Rare, GeneratedCharacter.Defect) != 1
            || NativeWeight(0, GeneratedRarity.Uncommon, GeneratedCharacter.Colorless) != 14
            || Weight(0, GeneratedRarity.Basic, GeneratedCharacter.Ironclad, false) != 26
            || Weight(0, GeneratedRarity.Basic, GeneratedCharacter.Ironclad, false, hasStarPayment: true) != 40
            || Weight(0, GeneratedRarity.Common, GeneratedCharacter.Regent, false) != 13
            || Weight(0, GeneratedRarity.Rare, GeneratedCharacter.Ironclad, false) != 21
            || Weight(3, GeneratedRarity.Rare, GeneratedCharacter.Ironclad, false) != 2
            || Weight(1, GeneratedRarity.Common, GeneratedCharacter.Ironclad, true)
                != PlayableCharacters.Sum(character => NativeWeight(1, GeneratedRarity.Common, character)) * 8)
            throw new InvalidOperationException("固定费用配置表或终极混乱合并规则发生了意外变化。");
    }
}

/// <summary>
/// Soft final acceptance priors for legal but strategically uninteresting cards. These do not alter component
/// reachability or hard-code individual templates: exact native assemblies retain a bypass, while ordinary random
/// assemblies keep a nonzero tail for free cards, pure draw and tiny immediate rewards.
/// </summary>
internal static class CardAcceptanceTuning
{
    internal static int EffectiveZeroAcceptancePercent(GeneratedCharacter character, bool ultimateChaos,
        int energyCost, int starCost, bool hasEnergyX, bool hasStarX, double effectiveCost)
    {
        if (hasEnergyX || hasStarX || double.IsNaN(effectiveCost) || effectiveCost > 0.25d) return 100;
        if (energyCost == 0 && starCost <= 0)
            return !ultimateChaos && character == GeneratedCharacter.Regent ? 45 : 65;
        // A printed paid card whose reliable refund cancels its payment is also functionally free, but retains a
        // little more representation than Regent's literal 0/0 shells.
        return 55;
    }

    internal static int MarginalPureDrawAcceptancePercent(IReadOnlyList<GeneratorOperation> operations,
        GeneratedRarity rarity, double effectiveCost)
    {
        if (rarity != GeneratedRarity.Common || effectiveCost is < 0.75d or > 1.25d) return 100;
        var rewards = operations.Where(operation => CardEffectRules.IsBeneficialEffect(operation)
                && operation.Scope is not (OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger)
                && !CardEffectRules.IsDependencyPrefix(operation))
            .ToArray();
        if (rewards.Length != 1 || rewards[0].Template is not ("N:Draw" or "N_DRAW")
            || rewards[0].Parameters.ContainsKey("triggerIndex"))
            return 100;
        return OperationRuntimeSpecCompiler.StaticLiteralValue(rewards[0], "draw") <= 2 ? 18 : 100;
    }

    internal static int TinyImmediateRewardAcceptancePercent(IReadOnlyList<GeneratorOperation> operations,
        GeneratedRarity rarity)
    {
        var count = operations.Count(IsTinyImmediateReward);
        if (count == 0) return 100;
        var oneLine = rarity switch
        {
            GeneratedRarity.Basic => 48,
            GeneratedRarity.Common => 35,
            GeneratedRarity.Uncommon => 25,
            GeneratedRarity.Rare => 18,
            GeneratedRarity.Ancient => 15,
            _ => 35
        };
        return count == 1 ? oneLine : Math.Max(5, oneLine / 2);
    }

    internal static bool IsTinyImmediateReward(GeneratorOperation operation)
    {
        if (operation.Parameters.ContainsKey("triggerIndex")
            || operation.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger
                or OperationScope.AbilityRule or OperationScope.Modifier
            || !CardEffectRules.IsBeneficialEffect(operation)
            || CardEffectRules.IsEnergyGainOperation(operation)
            || CardEffectRules.IsStarGainOperation(operation)
            || CardEffectRules.IsPositivePermanentStatGain(operation))
            return false;
        var atom = new ComponentAtom(operation.Template, operation.Scope, operation.ChineseText,
            operation.RequiresSingleTarget, CardReferenceRequirement.None)
            { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(operation) };
        return EffectBalanceModel.IsScalableReward(atom)
            && EffectBalanceModel.EstimatedEffectValue(operation) < 250;
    }

    internal static void Validate()
    {
        if (EffectiveZeroAcceptancePercent(GeneratedCharacter.Regent, false,
                0, 0, false, false, 0d) != 45
            || EffectiveZeroAcceptancePercent(GeneratedCharacter.Ironclad, false,
                0, -1, false, false, 0d) != 65
            || EffectiveZeroAcceptancePercent(GeneratedCharacter.Ironclad, false,
                1, -1, false, false, 0d) != 55)
            throw new InvalidOperationException("折合零费牌的软限制发生了意外变化。");

        var drawTwo = new GeneratorOperation("N:Draw", OperationScope.NonTargeted, "抽2张牌。",
            new Dictionary<string, int> { ["draw"] = 2 });
        var drawThree = drawTwo with
        {
            ChineseText = "抽3张牌。",
            Parameters = new Dictionary<string, int> { ["draw"] = 3 }
        };
        if (MarginalPureDrawAcceptancePercent([drawTwo], GeneratedRarity.Common, 1d) != 18
            || MarginalPureDrawAcceptancePercent([drawThree], GeneratedRarity.Common, 1d) != 100
            || MarginalPureDrawAcceptancePercent([drawTwo], GeneratedRarity.Uncommon, 1d) != 100)
            throw new InvalidOperationException("普通1费纯抽牌软限制发生了意外变化。");

        var blockOne = new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得1点格挡。",
            new Dictionary<string, int> { ["block"] = 1 });
        var linkedBlockOne = blockOne with
        {
            Parameters = new Dictionary<string, int> { ["block"] = 1, ["triggerIndex"] = 0 }
        };
        var blockThree = blockOne with
        {
            ChineseText = "获得3点格挡。",
            Parameters = new Dictionary<string, int> { ["block"] = 3 }
        };
        if (!IsTinyImmediateReward(blockOne)
            || IsTinyImmediateReward(linkedBlockOne)
            || IsTinyImmediateReward(blockThree)
            || TinyImmediateRewardAcceptancePercent([blockOne], GeneratedRarity.Common) != 35)
            throw new InvalidOperationException("极低单项收益软限制发生了意外变化。");
    }
}

/// <summary>Mode-level branches used only by numeric-aggressive generation.</summary>
internal static class AggressiveModeTuning
{
    internal const int RaisedEffectFloorChancePercent = 30;
    internal const int NegativeOptimizationChancePercent = 50;

    internal static bool ShouldRaiseEffectCountFloor(bool balancedValues, int percentileRoll) =>
        !balancedValues && percentileRoll is >= 0 and < RaisedEffectFloorChancePercent;

    internal static bool ShouldOptimizeNegatives(bool balancedValues, int percentileRoll) =>
        !balancedValues && percentileRoll is >= 0 and < NegativeOptimizationChancePercent;

    internal static void Validate()
    {
        if (ShouldRaiseEffectCountFloor(true, 0)
            || !ShouldRaiseEffectCountFloor(false, 29)
            || ShouldRaiseEffectCountFloor(false, 30)
            || ShouldOptimizeNegatives(true, 0)
            || !ShouldOptimizeNegatives(false, 49)
            || ShouldOptimizeNegatives(false, 50)
            || Enumerable.Range(0, 100).Count(roll => ShouldRaiseEffectCountFloor(false, roll)) != 30
            || Enumerable.Range(0, 100).Count(roll => ShouldOptimizeNegatives(false, roll)) != 50)
            throw new InvalidOperationException("数值激进模式的效果数下界或负面优化概率发生了意外变化。");
    }
}
