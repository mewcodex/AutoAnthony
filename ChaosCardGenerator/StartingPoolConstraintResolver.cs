namespace ChaosCardGenerator;

/// <summary>
/// Re-applies pool support constraints to the ten generated cards used as the starting deck. Local replacements
/// preserve X class and caller-specified combat coverage, while complete-pool constraints are handled separately.
/// </summary>
public static class StartingPoolConstraintResolver
{
    public const int MaximumHighResourceCards = 2;

    public static bool TryRepair(GeneratedCard[] cards, RandomCardGenerator generator, Random random,
        int minimumDamage, int minimumDefense, int replacementAttemptLimit, out string failure)
    {
        var maximumRepairs = cards.Length * 4;
        for (var repair = 0; repair <= maximumRepairs; repair++)
        {
            var ostyValid = true;
            var slyValid = true;
            var derivativeValid = true;
            try { OstyPoolConstraintResolver.Audit(cards); }
            catch { ostyValid = false; }
            try { SlyPoolConstraintResolver.Audit(cards); }
            catch { slyValid = false; }
            try
            {
                // Within the Basic subpool, Basic producer cards provide the local derivative supply. The complete
                // pool retains its stricter Common/Uncommon producer rule and is audited again by the caller.
                DerivativePoolConstraintResolver.Audit(cards,
                    restrictProducersToCommonAndUncommon: false);
            }
            catch
            {
                derivativeValid = false;
            }
            var currentDamage = cards.Count(CountsAsDamage);
            var currentDefense = cards.Count(CountsAsDefense);
            var currentHighResource = cards.Count(IsHighResourceCard);
            var damageValid = currentDamage >= minimumDamage;
            var defenseValid = currentDefense >= minimumDefense;
            var highResourceValid = currentHighResource <= MaximumHighResourceCards;
            if (ostyValid && slyValid && derivativeValid && damageValid && defenseValid && highResourceValid)
            {
                failure = string.Empty;
                return true;
            }
            if (repair == maximumRepairs) break;

            var currentOstyGap = cards.Count(OstyPoolConstraintResolver.HasOstyEffect)
                - cards.Count(OstyPoolConstraintResolver.HasSummonEffect);
            var currentSlyGap = cards.Count(SlyPoolConstraintResolver.HasSly)
                - cards.Count(SlyPoolConstraintResolver.HasDiscardEffect);
            var candidates = Enumerable.Range(0, cards.Length)
                .Where(index => !damageValid && !CountsAsDamage(cards[index])
                    || !defenseValid && !CountsAsDefense(cards[index])
                    || currentOstyGap > 0
                        && OstyPoolConstraintResolver.HasOstyEffect(cards[index])
                        && !OstyPoolConstraintResolver.HasSummonEffect(cards[index])
                    || currentSlyGap > 0
                        && SlyPoolConstraintResolver.HasSly(cards[index])
                        && !SlyPoolConstraintResolver.HasDiscardEffect(cards[index])
                    || !highResourceValid && IsHighResourceCard(cards[index])
                    || DerivativePoolConstraintResolver.HasProducedDerivativeReference(cards[index]))
                .OrderByDescending(index => currentOstyGap > 0
                    && OstyPoolConstraintResolver.HasOstyEffect(cards[index]) ? 1 : 0)
                .ThenByDescending(index => currentSlyGap > 0
                    && SlyPoolConstraintResolver.HasSly(cards[index]) ? 1 : 0)
                .ThenByDescending(index => !damageValid && !CountsAsDamage(cards[index]) ? 1 : 0)
                .ThenByDescending(index => !defenseValid && !CountsAsDefense(cards[index]) ? 1 : 0)
                .ThenByDescending(index => !highResourceValid && IsHighResourceCard(cards[index]) ? 1 : 0)
                .ThenBy(_ => random.Next())
                .ToArray();
            if (candidates.Length == 0)
            {
                failure = "Starting-deck support graph has no replaceable unsupported card.";
                return false;
            }

            var replaced = false;
            foreach (var index in candidates)
            {
                var oldOrdinaryX = SpecialXCardConverter.IsOrdinaryX(cards[index]);
                var oldCard = cards[index];
                var repairingHighResource = !highResourceValid && IsHighResourceCard(cards[index]);
                try
                {
                    var replacement = generator.GenerateReferenceFreeWithoutSpecialXMatching(
                        GeneratedRarity.Basic, candidate =>
                    {
                        // Ordinarily preserve the pool's X class. When the selected card itself violates the
                        // starting resource cap, that explicit rule takes precedence and permits a low-cost card.
                        if (!repairingHighResource
                            && SpecialXCardConverter.IsOrdinaryX(candidate) != oldOrdinaryX) return false;
                        if (currentOstyGap > 0 && OstyPoolConstraintResolver.HasOstyEffect(candidate)) return false;
                        if (currentSlyGap > 0 && SlyPoolConstraintResolver.HasSly(candidate)) return false;
                        var prospectiveDamage = currentDamage - (CountsAsDamage(oldCard) ? 1 : 0)
                            + (CountsAsDamage(candidate) ? 1 : 0);
                        var prospectiveDefense = currentDefense - (CountsAsDefense(oldCard) ? 1 : 0)
                            + (CountsAsDefense(candidate) ? 1 : 0);
                        var prospectiveHighResource = currentHighResource - (IsHighResourceCard(oldCard) ? 1 : 0)
                            + (IsHighResourceCard(candidate) ? 1 : 0);
                        if (damageValid ? prospectiveDamage < minimumDamage : prospectiveDamage < currentDamage)
                            return false;
                        if (defenseValid ? prospectiveDefense < minimumDefense : prospectiveDefense < currentDefense)
                            return false;
                        if (highResourceValid
                                ? prospectiveHighResource > MaximumHighResourceCards
                                : prospectiveHighResource >= currentHighResource)
                            return false;
                        var coverageImproved = !damageValid && prospectiveDamage > currentDamage
                            || !defenseValid && prospectiveDefense > currentDefense
                            || !highResourceValid && prospectiveHighResource < currentHighResource;
                        if (ostyValid && slyValid && derivativeValid && highResourceValid && !coverageImproved)
                            return false;
                        var prospectiveOstyGap = currentOstyGap
                            - (OstyPoolConstraintResolver.HasOstyEffect(oldCard) ? 1 : 0)
                            + (OstyPoolConstraintResolver.HasSummonEffect(oldCard) ? 1 : 0)
                            + (OstyPoolConstraintResolver.HasOstyEffect(candidate) ? 1 : 0)
                            - (OstyPoolConstraintResolver.HasSummonEffect(candidate) ? 1 : 0);
                        var prospectiveSlyGap = currentSlyGap
                            - (SlyPoolConstraintResolver.HasSly(oldCard) ? 1 : 0)
                            + (SlyPoolConstraintResolver.HasDiscardEffect(oldCard) ? 1 : 0)
                            + (SlyPoolConstraintResolver.HasSly(candidate) ? 1 : 0)
                            - (SlyPoolConstraintResolver.HasDiscardEffect(candidate) ? 1 : 0);
                        // Invalid ratios must strictly improve; already-valid ratios may not worsen.
                        if (currentOstyGap > 0 ? prospectiveOstyGap >= currentOstyGap
                                : prospectiveOstyGap > currentOstyGap)
                            return false;
                        return currentSlyGap > 0 ? prospectiveSlyGap < currentSlyGap
                            : prospectiveSlyGap <= currentSlyGap;
                    });
                    cards[index] = replacement;
                    replaced = true;
                }
                catch (InvalidOperationException)
                {
                    // Try another replaceable slot. The outer bounded pool attempt remains the final fallback.
                }
                if (replaced) break;
            }
            if (!replaced)
            {
                failure = "Could not repair starting-deck support/coverage/resource cap without breaking another constraint.";
                return false;
            }
        }

        failure = $"Starting-deck support repair exceeded {maximumRepairs} replacements.";
        return false;
    }

    public static bool CountsAsDamage(GeneratedCard card) => card.Operations.Any(operation =>
    {
        if (CardEffectRules.IsEnemyDamage(operation) || operation.Template == "N:RetaliateDamage")
        {
            var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
            var damage = RuntimeValueAtOne(spec, "damage", 0);
            var hits = RuntimeValueAtOne(spec, "hits", 1);
            return damage * hits >= 4;
        }

        return OrbSlotCatalog.ResolveOutput(operation.OrbOutputId, operation.Template)?.DealsDamage == true
            && EffectBalanceModel.EstimatedEffectValue(operation) >= 400;
    });

    public static bool CountsAsDefense(GeneratedCard card) => card.Operations.Any(operation =>
    {
        if (CardEffectRules.PrintedBlockValueSlot(operation) is { } blockSlot)
            return RuntimeValueAtOne(OperationRuntimeSpecCompiler.GetOrCompile(operation), blockSlot, 0) >= 4;

        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        var defensiveUtility = spec.Opcode == "gain_block"
            || spec.Opcode == "apply_power" && spec.Variant == "plating"
            || operation.Template == "I:DrawAndBlockIfSkill"
            || OrbSlotCatalog.ResolveOutput(operation.OrbOutputId, operation.Template)?.Id == "frost"
            || operation.Template is "NCR:Summon" or "NCR:SummonX";
        return defensiveUtility && EffectBalanceModel.EstimatedEffectValue(operation) >= 400;
    });

    public static bool IsHighResourceCard(GeneratedCard card) =>
        card.Cost > 1 || card.StarCost > 0 || card.HasStarCostX;

    private static int RuntimeValueAtOne(OperationRuntimeSpec spec, string slotId, int fallback)
    {
        var slot = spec.Values.FirstOrDefault(value => value.Id == slotId);
        if (slot is null) return fallback;
        return Math.Max(0, slot.Source == "fixed" ? slot.BaseValue + slot.Offset : 1 + slot.Offset);
    }
}
