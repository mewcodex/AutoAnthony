namespace ChaosCardGenerator;

/// <summary>
/// Resolves derivative references after a complete character pool has been generated. A reference is legal only
/// when a Common or Uncommon card in that same generated pool contains a matching creation/transformation effect.
/// Each producer effect supplies one reference; Forge supplies one plain Sovereign Blade reference. Enchantments
/// belong only to producer results and never change the base derivative identity used by supply accounting.
/// </summary>
public static class DerivativePoolConstraintResolver
{
    private static readonly IReadOnlyDictionary<string, string> AtomicReferences =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["A:ruleShivBonusDamage"] = "shiv",
            ["A:ruleShivsHitAll"] = "shiv",
            ["A:ruleShivsRetainAndFirstBonus"] = "shiv",
            ["A:ruleShivsRetain"] = "shiv",
            ["A:ruleFirstShivBonusDamage"] = "shiv",
            ["A:ProxyAtomic_Parry"] = "sword",
            ["A:ProxyAtomic_SwordSage"] = "sword",
            ["R:KingsSwordDoubleDamageThisTurn"] = "sword",
            ["R:KingsSwordHitsAllEnemies"] = "sword"
        };

    public static bool RequiresProducedDerivative(string template) =>
        DerivativeSlotCatalog.IsReference(template) || AtomicReferences.ContainsKey(template);

    public static bool HasProducedDerivativeReference(GeneratedCard card) =>
        card.Operations.Any(operation => RequiresProducedDerivative(operation.Template));

    public static bool TryResolve(GeneratedCard[] cards, Random random, out string failure,
        bool restrictProducersToCommonAndUncommon = true)
    {
        var remaining = CountProducers(cards, restrictProducersToCommonAndUncommon);
        var remainingExhausting = CountExhaustingProducers(cards, restrictProducersToCommonAndUncommon);

        var references = cards.SelectMany((card, cardIndex) => card.Operations
                .Select((operation, operationIndex) => (cardIndex, operationIndex, operation))
                .Where(item => DerivativeSlotCatalog.IsReference(item.operation.Template)))
            .OrderBy(_ => random.Next())
            .ToArray();

        bool BindReferences(IEnumerable<(int cardIndex, int operationIndex, GeneratorOperation operation)> items,
            bool requireExhausting, out string bindFailure)
        {
            foreach (var (cardIndex, operationIndex, operation) in items)
            {
                var requiresDerivativeUpgrade = cards[cardIndex].Upgrade?.Effects.Any(effect =>
                    effect.Kind == CardUpgradeKind.UpgradeDerivative && effect.OperationIndex == operationIndex) == true;
                var candidates = DerivativeSlotCatalog.All
                    .Where(definition => remaining.GetValueOrDefault(definition.Id) > 0)
                    .Where(definition => !requireExhausting
                        || remainingExhausting.GetValueOrDefault(definition.Id) > 0)
                    .Where(definition => DerivativeSlotCatalog.CanUse(operation.Template, definition))
                    .Where(definition => !requiresDerivativeUpgrade || definition.CanUpgrade)
                    .ToArray();
                if (candidates.Length == 0)
                {
                    bindFailure = $"No produced derivative can satisfy {operation.Template}.";
                    return false;
                }
                var totalWeight = candidates.Sum(candidate => requireExhausting
                    ? remainingExhausting[candidate.Id]
                    : remaining[candidate.Id]);
                var roll = random.Next(totalWeight);
                var selected = candidates[0];
                foreach (var candidate in candidates)
                {
                    var weight = requireExhausting
                        ? remainingExhausting[candidate.Id]
                        : remaining[candidate.Id];
                    if (roll < weight) { selected = candidate; break; }
                    roll -= weight;
                }
                try
                {
                    cards[cardIndex] = Rebind(cards[cardIndex], operationIndex, selected);
                }
                catch (Exception exception)
                {
                    // Pool binding is a speculative generation pass. A malformed candidate must reject this pass
                    // and enter the bounded repair/reroll path instead of escaping through the async run-start
                    // boundary and leaving the game on its transition screen.
                    bindFailure = $"Could not bind {operation.Template} to {selected.Id}: {exception.Message}";
                    return false;
                }
                remaining[selected.Id]--;
                if (requireExhausting) remainingExhausting[selected.Id]--;
            }
            bindFailure = string.Empty;
            return true;
        }

        // Soul's Power removes Exhaust from the produced card. Reserve genuinely exhausting outputs before
        // unrestricted fixed rules can consume their shared base-derivative capacity.
        var exhaustedPileReferences = references.Where(item =>
            DerivativeSlotCatalog.IsExhaustPileReference(item.operation.Template)).ToArray();
        if (!BindReferences(exhaustedPileReferences, requireExhausting: true, out failure)) return false;

        // Fixed derivative-specific rules cannot be rebound. Reserve their matching producer capacity first.
        foreach (var operation in cards.SelectMany(card => card.Operations))
        {
            if (!AtomicReferences.TryGetValue(operation.Template, out var derivativeId)) continue;
            if (remaining.GetValueOrDefault(derivativeId) <= 0)
            {
                failure = $"No Common/Uncommon producer remains for atomic {operation.Template}/{derivativeId}.";
                return false;
            }
            remaining[derivativeId]--;
        }

        if (!BindReferences(references.Except(exhaustedPileReferences), requireExhausting: false, out failure))
            return false;

        // Rebinding registers derivative-aware English forms globally. Refresh every card after all references
        // have settled so a numeric upgrade from 1 to 2 cannot retain an earlier singular derivative name.
        for (var cardIndex = 0; cardIndex < cards.Length; cardIndex++)
            cards[cardIndex] = RefreshDescriptions(cards[cardIndex]);

        try
        {
            Audit(cards, restrictProducersToCommonAndUncommon);
            failure = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Repairs a failed derivative supply graph by replacing only cards that consume derivative supply.  The
    /// replacement generator is expected to suppress derivative references; rarity and ordinary/special X class
    /// are preserved.  This avoids throwing away an otherwise valid 90-card pool because one late fixed Sword or
    /// Shiv rule had no matching Common/Uncommon producer.
    /// </summary>
    public static bool TryRepairAndResolve(GeneratedCard[] cards, IReadOnlyList<GeneratedRarity> rarities,
        RandomCardGenerator generator, Random random, int replacementAttemptLimit, out string failure)
    {
        if (cards.Length != rarities.Count)
        {
            failure = "Card and rarity counts differ while repairing derivative references.";
            return false;
        }

        failure = string.Empty;
        var maximumRepairs = Math.Max(8, cards.Length * 2);
        var perReplacementAttempts = Math.Min(Math.Max(32, replacementAttemptLimit), 1024);
        for (var repair = 0; repair <= maximumRepairs; repair++)
        {
            // Binding is speculative because a failed matching order may already have rebound some slots.  Commit
            // the array only when the complete graph succeeds.
            var trial = (GeneratedCard[])cards.Clone();
            if (TryResolve(trial, random, out failure))
            {
                Array.Copy(trial, cards, cards.Length);
                return true;
            }
            if (repair == maximumRepairs) break;

            var deficientAtomicIds = AtomicReferences.Values.Distinct(StringComparer.Ordinal)
                .Where(id => cards.SelectMany(card => card.Operations)
                    .Count(operation => AtomicReferences.GetValueOrDefault(operation.Template) == id)
                    > CountProducers(cards).GetValueOrDefault(id))
                .ToHashSet(StringComparer.Ordinal);
            var candidates = Enumerable.Range(0, cards.Length)
                // Starting-card coverage is a separate invariant.  It is cheaper and safer to retain Basic slots
                // and let the bounded outer pool retry handle the exceptionally rare all-Basic deadlock.
                .Where(index => rarities[index] != GeneratedRarity.Basic)
                .Where(index => HasProducedDerivativeReference(cards[index]))
                .OrderByDescending(index => cards[index].Operations.Count(operation =>
                    AtomicReferences.TryGetValue(operation.Template, out var id)
                    && deficientAtomicIds.Contains(id)))
                .ThenByDescending(index => ReferencePressure(cards[index]))
                // Prefer Rare/Ancient consumers over Common/Uncommon cards, which may also be producers.
                .ThenByDescending(index => rarities[index] is GeneratedRarity.Rare or GeneratedRarity.Ancient)
                .ThenBy(_ => random.Next())
                .ToArray();
            if (candidates.Length == 0)
            {
                failure += " No non-Basic derivative consumer is available for local repair.";
                return false;
            }

            var replaced = false;
            foreach (var index in candidates)
            {
                var requiredXClass = XClass(cards[index]);
                var ostyWithoutCandidate = cards.Count(OstyPoolConstraintResolver.HasOstyEffect)
                    - (OstyPoolConstraintResolver.HasOstyEffect(cards[index]) ? 1 : 0);
                var summonsWithoutCandidate = cards.Count(OstyPoolConstraintResolver.HasSummonEffect)
                    - (OstyPoolConstraintResolver.HasSummonEffect(cards[index]) ? 1 : 0);
                var slyWithoutCandidate = cards.Count(SlyPoolConstraintResolver.HasSly)
                    - (SlyPoolConstraintResolver.HasSly(cards[index]) ? 1 : 0);
                var discardWithoutCandidate = cards.Count(SlyPoolConstraintResolver.HasDiscardEffect)
                    - (SlyPoolConstraintResolver.HasDiscardEffect(cards[index]) ? 1 : 0);
                for (var attempt = 0; attempt < perReplacementAttempts; attempt++)
                {
                    var replacement = requiredXClass == 2
                        ? generator.GenerateReferenceFreeSpecialX(rarities[index])
                        : generator.GenerateReferenceFreeWithoutSpecialX(rarities[index]);
                    if (XClass(replacement) != requiredXClass || HasProducedDerivativeReference(replacement)) continue;
                    // Local derivative repair runs after the Osty/Summon support pass. Replacing its only Summon
                    // card would silently invalidate that earlier pool invariant, so validate both constraints
                    // before committing this candidate instead of forcing a later whole-pool reroll.
                    var prospectiveOsty = ostyWithoutCandidate
                        + (OstyPoolConstraintResolver.HasOstyEffect(replacement) ? 1 : 0);
                    var prospectiveSummons = summonsWithoutCandidate
                        + (OstyPoolConstraintResolver.HasSummonEffect(replacement) ? 1 : 0);
                    if (prospectiveOsty > prospectiveSummons) continue;
                    var prospectiveSly = slyWithoutCandidate
                        + (SlyPoolConstraintResolver.HasSly(replacement) ? 1 : 0);
                    var prospectiveDiscard = discardWithoutCandidate
                        + (SlyPoolConstraintResolver.HasDiscardEffect(replacement) ? 1 : 0);
                    if (prospectiveSly > prospectiveDiscard) continue;
                    cards[index] = replacement;
                    replaced = true;
                    break;
                }
                if (replaced) break;
            }
            if (!replaced)
            {
                failure += " Could not generate a same-rarity, same-X-class reference-free replacement.";
                return false;
            }
        }

        failure += $" Local derivative repair exceeded {maximumRepairs} replacements.";
        return false;
    }

    private static int ReferencePressure(GeneratedCard card)
    {
        var references = card.Operations.Count(operation => RequiresProducedDerivative(operation.Template));
        var producers = card.Rarity is GeneratedRarity.Common or GeneratedRarity.Uncommon
            ? card.Operations.Count(operation => DerivativeSlotCatalog.IsProducer(operation.Template)
                || operation.Template is "R:Forge" or "R:ForgePerPriorHit")
            : 0;
        return references - producers;
    }

    // 0 = fixed cost, 1 = ordinary X, 2 = special X.
    private static int XClass(GeneratedCard card) => SpecialXCardConverter.IsSpecial(card)
        ? 2
        : SpecialXCardConverter.IsOrdinaryX(card) ? 1 : 0;

    private static GeneratedCard RefreshDescriptions(GeneratedCard card)
    {
        var upgrade = card.Upgrade;
        if (upgrade is not null)
        {
            var upgradedOperations = CardUpgradeGenerator.ApplyEffectsToOperations(card.Operations, upgrade.Effects);
            upgrade = upgrade with
            {
                UpgradedChineseDescription = CardDescriptionRenderer.Render(upgradedOperations),
                UpgradedEnglishDescription = EnglishCardDescriptionRenderer.Render(upgradedOperations)
            };
        }
        return card with
        {
            ChineseDescription = CardDescriptionRenderer.Render(card.Operations),
            EnglishDescription = EnglishCardDescriptionRenderer.Render(card.Operations),
            Upgrade = upgrade
        };
    }

    public static void Audit(IReadOnlyList<GeneratedCard> cards,
        bool restrictProducersToCommonAndUncommon = true)
    {
        var producers = CountProducers(cards, restrictProducersToCommonAndUncommon);
        var exhaustingProducers = CountExhaustingProducers(cards, restrictProducersToCommonAndUncommon);
        var references = new Dictionary<string, int>(StringComparer.Ordinal);
        var exhaustingReferences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var operation in cards.SelectMany(card => card.Operations))
        {
            if (AtomicReferences.TryGetValue(operation.Template, out var atomicId))
                references[atomicId] = references.GetValueOrDefault(atomicId) + 1;
            if (!DerivativeSlotCatalog.IsReference(operation.Template)) continue;
            var derivative = DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template)
                ?? throw new InvalidOperationException($"Reference {operation.Template} has no derivative.");
            if (operation.DerivativeEnchantmentId is not null)
                throw new InvalidOperationException("A derivative reference cannot request an enchantment.");
            if (!DerivativeSlotCatalog.CanUse(operation.Template, derivative))
                throw new InvalidOperationException($"{derivative.Id} is incompatible with {operation.Template}.");
            references[derivative.Id] = references.GetValueOrDefault(derivative.Id) + 1;
            if (DerivativeSlotCatalog.IsExhaustPileReference(operation.Template))
                exhaustingReferences[derivative.Id] = exhaustingReferences.GetValueOrDefault(derivative.Id) + 1;
        }
        foreach (var (derivativeId, count) in references)
            if (count > producers.GetValueOrDefault(derivativeId))
                throw new InvalidOperationException($"Derivative {derivativeId} has {count} references but only "
                    + $"{producers.GetValueOrDefault(derivativeId)} Common/Uncommon producers.");
        foreach (var (derivativeId, count) in exhaustingReferences)
            if (count > exhaustingProducers.GetValueOrDefault(derivativeId))
                throw new InvalidOperationException($"Derivative {derivativeId} has {count} exhausted-pile references "
                    + $"but only {exhaustingProducers.GetValueOrDefault(derivativeId)} producers that retain Exhaust.");
    }

    private static Dictionary<string, int> CountProducers(IReadOnlyList<GeneratedCard> cards,
        bool restrictToCommonAndUncommon = true)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var card in cards.Where(card => !restrictToCommonAndUncommon
                     || card.Rarity is GeneratedRarity.Common or GeneratedRarity.Uncommon))
        {
            foreach (var operation in card.Operations)
            {
                if (DerivativeSlotCatalog.IsProducer(operation.Template))
                {
                    var derivative = DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template)
                        ?? throw new InvalidOperationException($"Producer {operation.Template} has no derivative.");
                    var id = DerivativeSlotCatalog.NormalizeProducedId(derivative.Id);
                    result[id] = result.GetValueOrDefault(id) + 1;
                }
                if (operation.Template is "R:Forge" or "R:ForgePerPriorHit")
                    result["sword"] = result.GetValueOrDefault("sword") + 1;
            }
        }
        return result;
    }

    private static Dictionary<string, int> CountExhaustingProducers(IReadOnlyList<GeneratedCard> cards,
        bool restrictToCommonAndUncommon = true)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var card in cards.Where(card => !restrictToCommonAndUncommon
                     || card.Rarity is GeneratedRarity.Common or GeneratedRarity.Uncommon))
        foreach (var operation in card.Operations.Where(operation => DerivativeSlotCatalog.IsProducer(operation.Template)))
        {
            var derivative = DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template)
                ?? throw new InvalidOperationException($"Producer {operation.Template} has no derivative.");
            var enchantment = DerivativeSlotCatalog.ResolveEnchantment(operation.DerivativeId,
                operation.DerivativeEnchantmentId, operation.Template);
            if (derivative.HasExhaust && enchantment?.Id != "souls_power")
                result[derivative.Id] = result.GetValueOrDefault(derivative.Id) + 1;
        }
        return result;
    }

    private static GeneratedCard Rebind(GeneratedCard card, int operationIndex,
        DerivativeSlotDefinition selected)
    {
        var operations = card.Operations.ToArray();
        var operation = operations[operationIndex];
        var previous = DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template)
            ?? throw new InvalidOperationException($"Cannot rebind {operation.Template} without a source derivative.");
        if (previous.Id == selected.Id) return card;

        var chinese = operation.ChineseText.Replace(previous.ChineseName, selected.ChineseName,
            StringComparison.Ordinal);
        var english = DerivativeSlotCatalog.ReplaceEnglishName(
            EnglishCardDescriptionRenderer.OperationText(operation), previous, selected,
            DerivativeSlotCatalog.ReferenceUsesPlural(operation.Template));
        ExternalOperationTextRegistry.Register(operation.Template, chinese, english);
        operations[operationIndex] = operation with
        {
            ChineseText = chinese,
            DerivativeId = selected.Id,
            DerivativeEnchantmentId = null,
            DerivativeEnchantmentAmount = null
        };

        CardUpgradePlan? upgrade = card.Upgrade;
        if (upgrade is not null)
        {
            var effects = upgrade.Effects.Select(effect => effect.OperationIndex == operationIndex
                ? effect with
                {
                    ChineseDescription = effect.ChineseDescription.Replace(previous.ChineseName,
                        selected.ChineseName, StringComparison.Ordinal),
                    EnglishDescription = DerivativeSlotCatalog.ReplaceEnglishName(effect.EnglishDescription,
                        previous, selected, plural: false)
                }
                : effect).ToArray();
            if (effects.Any(effect => effect.Kind == CardUpgradeKind.UpgradeDerivative
                && effect.OperationIndex == operationIndex))
            {
                var upgradedOperationText = chinese.Replace(selected.ChineseName,
                    selected.ChineseName + "+", StringComparison.Ordinal);
                ExternalOperationTextRegistry.Register(operation.Template, upgradedOperationText,
                    DerivativeSlotCatalog.MarkEnglishUpgrade(english, selected));
            }
            var upgradedOperations = CardUpgradeGenerator.ApplyEffectsToOperations(operations, effects);
            upgrade = upgrade with
            {
                Effects = effects,
                UpgradedChineseDescription = CardDescriptionRenderer.Render(upgradedOperations),
                UpgradedEnglishDescription = EnglishCardDescriptionRenderer.Render(upgradedOperations)
            };
        }

        // Rebinding changes only the derivative identity and its rendered name. The selected definition has already
        // passed template/upgrade compatibility checks above, so rerunning the complete generation-time validator
        // here adds no structural coverage. More importantly, numeric-random mode intentionally runs after that
        // validator and may raise values beyond ordinary generation caps (for example Draw > 4); rejecting those
        // legal post-processed values here made derivative binding abort an otherwise valid pool.
        return card with
        {
            Operations = operations,
            ChineseDescription = CardDescriptionRenderer.Render(operations),
            EnglishDescription = EnglishCardDescriptionRenderer.Render(operations),
            Upgrade = upgrade
        };
    }
}
