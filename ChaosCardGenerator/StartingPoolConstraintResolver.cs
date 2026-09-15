using System.Diagnostics;

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
        var traceRepairs = Environment.GetEnvironmentVariable("AUTOANTHONY_TRACE_POOL_REPAIR") == "1";
        var maximumRepairs = cards.Length * 4;
        // Two additional passes allow one exact Attack and one exact Block fallback after ordinary constrained
        // generation has exhausted its bounded search.
        for (var repair = 0; repair <= maximumRepairs + 2; repair++)
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
            var enforceRegentStarBalance = cards.Any(card => card.Character == GeneratedCharacter.Regent);
            var currentStarConsumers = enforceRegentStarBalance ? cards.Count(ConsumesStars) : 0;
            var currentStarProducers = enforceRegentStarBalance ? cards.Count(ProducesStars) : 0;
            var currentStarGap = currentStarConsumers - currentStarProducers;
            var damageValid = currentDamage >= minimumDamage;
            var defenseValid = currentDefense >= minimumDefense;
            var highResourceValid = currentHighResource <= MaximumHighResourceCards;
            var starBalanceValid = !enforceRegentStarBalance || currentStarGap <= 0;
            var needsDamageHeadroom = !highResourceValid && currentDamage <= minimumDamage
                && cards.Any(card => IsHighResourceCard(card) && CountsAsDamage(card));
            var needsDefenseHeadroom = !highResourceValid && currentDefense <= minimumDefense
                && cards.Any(card => IsHighResourceCard(card) && CountsAsDefense(card));
            if (ostyValid && slyValid && derivativeValid && damageValid && defenseValid && highResourceValid
                && starBalanceValid)
            {
                failure = string.Empty;
                return true;
            }
            if (repair >= maximumRepairs)
            {
                if (TryApplyRegentCoverageFallback(cards, generator, random, minimumDamage, minimumDefense,
                        damageValid, defenseValid, highResourceValid, starBalanceValid,
                        currentDamage, currentDefense, currentHighResource, currentStarGap))
                    continue;
                break;
            }

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
                    || !highResourceValid && !IsHighResourceCard(cards[index])
                        && (needsDamageHeadroom && !CountsAsDamage(cards[index])
                            || needsDefenseHeadroom && !CountsAsDefense(cards[index]))
                    || !starBalanceValid && ConsumesStars(cards[index]) && !ProducesStars(cards[index])
                    || DerivativePoolConstraintResolver.HasProducedDerivativeReference(cards[index]))
                .OrderByDescending(index => currentOstyGap > 0
                    && OstyPoolConstraintResolver.HasOstyEffect(cards[index]) ? 1 : 0)
                .ThenByDescending(index => currentSlyGap > 0
                    && SlyPoolConstraintResolver.HasSly(cards[index]) ? 1 : 0)
                // Prefer slots whose removal preserves both current coverage counts. When defense is exactly at
                // its minimum, replacing a defensive card while repairing damage would require the rare combined
                // damage+Block template; trying those slots first caused seconds of guaranteed low-yield search.
                .ThenByDescending(index =>
                    (currentDamage - (CountsAsDamage(cards[index]) ? 1 : 0) >= minimumDamage ? 1 : 0)
                    + (currentDefense - (CountsAsDefense(cards[index]) ? 1 : 0) >= minimumDefense ? 1 : 0))
                .ThenByDescending(index => !damageValid && !CountsAsDamage(cards[index]) ? 1 : 0)
                .ThenByDescending(index => !defenseValid && !CountsAsDefense(cards[index]) ? 1 : 0)
                .ThenByDescending(index => !highResourceValid && IsHighResourceCard(cards[index]) ? 1 : 0)
                .ThenByDescending(index => !starBalanceValid
                    && ConsumesStars(cards[index]) && !ProducesStars(cards[index]) ? 1 : 0)
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
                var replacementStarted = traceRepairs ? Stopwatch.GetTimestamp() : 0;
                var oldOrdinaryX = SpecialXCardConverter.IsOrdinaryX(cards[index]);
                    var oldCard = cards[index];
                    var repairingHighResource = !highResourceValid && IsHighResourceCard(cards[index]);
                    var stagingCoverageForHighResource = !highResourceValid && !repairingHighResource;
                try
                {
                    var replacement = generator.GenerateReferenceFreeWithoutSpecialXMatching(
                        GeneratedRarity.Basic, candidate =>
                    {
                        // Ordinarily preserve the pool's X class. When the selected card itself violates the
                        // starting resource cap, that explicit rule takes precedence and permits a low-cost card.
                        if (!repairingHighResource
                            && SpecialXCardConverter.IsOrdinaryX(candidate) != oldOrdinaryX) return false;
                        if (!highResourceValid && IsHighResourceCard(candidate)) return false;
                        if (currentOstyGap > 0 && OstyPoolConstraintResolver.HasOstyEffect(candidate)) return false;
                        if (currentSlyGap > 0 && SlyPoolConstraintResolver.HasSly(candidate)) return false;
                        var prospectiveDamage = currentDamage - (CountsAsDamage(oldCard) ? 1 : 0)
                            + (CountsAsDamage(candidate) ? 1 : 0);
                        var prospectiveDefense = currentDefense - (CountsAsDefense(oldCard) ? 1 : 0)
                            + (CountsAsDefense(candidate) ? 1 : 0);
                        var prospectiveHighResource = currentHighResource - (IsHighResourceCard(oldCard) ? 1 : 0)
                            + (IsHighResourceCard(candidate) ? 1 : 0);
                        var prospectiveStarGap = currentStarGap
                            - (ConsumesStars(oldCard) ? 1 : 0) + (ProducesStars(oldCard) ? 1 : 0)
                            + (ConsumesStars(candidate) ? 1 : 0) - (ProducesStars(candidate) ? 1 : 0);
                        if (damageValid ? prospectiveDamage < minimumDamage : prospectiveDamage < currentDamage)
                            return false;
                        if (defenseValid ? prospectiveDefense < minimumDefense : prospectiveDefense < currentDefense)
                            return false;
                        var createsCoverageHeadroom = stagingCoverageForHighResource
                            && (needsDamageHeadroom && prospectiveDamage > currentDamage
                                || needsDefenseHeadroom && prospectiveDefense > currentDefense);
                        if (highResourceValid
                                ? prospectiveHighResource > MaximumHighResourceCards
                                : prospectiveHighResource > currentHighResource
                                  || prospectiveHighResource == currentHighResource && !createsCoverageHeadroom)
                            return false;
                        if (enforceRegentStarBalance && (starBalanceValid
                                ? prospectiveStarGap > 0
                                : prospectiveStarGap >= currentStarGap))
                            return false;
                        var coverageImproved = !damageValid && prospectiveDamage > currentDamage
                            || !defenseValid && prospectiveDefense > currentDefense
                            || !highResourceValid && prospectiveHighResource < currentHighResource
                            || !starBalanceValid && prospectiveStarGap < currentStarGap;
                        if (ostyValid && slyValid && derivativeValid && highResourceValid && starBalanceValid
                            && !coverageImproved && !createsCoverageHeadroom)
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
                    if (traceRepairs)
                        Console.Error.WriteLine($"starter-repair[{repair}] {Stopwatch.GetElapsedTime(replacementStarted).TotalMilliseconds:F0}ms "
                            + $"slot={index}; damage={currentDamage}/{minimumDamage}; defense={currentDefense}/{minimumDefense}; "
                            + $"high={currentHighResource}/{MaximumHighResourceCards}; ostyGap={currentOstyGap}; "
                            + $"stars={currentStarConsumers}/{currentStarProducers}; slyGap={currentSlyGap}; "
                            + $"derivativeValid={derivativeValid}");
                }
                catch (InvalidOperationException)
                {
                    // Try another replaceable slot. The outer bounded pool attempt remains the final fallback.
                    if (traceRepairs)
                        Console.Error.WriteLine($"starter-repair[{repair}] rejected after "
                            + $"{Stopwatch.GetElapsedTime(replacementStarted).TotalMilliseconds:F0}ms slot={index}; "
                            + $"oldDamage={CountsAsDamage(oldCard)}; oldDefense={CountsAsDefense(oldCard)}; "
                            + $"oldHigh={IsHighResourceCard(oldCard)}; oldX={oldOrdinaryX}");
                }
                if (replaced) break;
            }
            if (!replaced)
            {
                if (TryApplyRegentCoverageFallback(cards, generator, random, minimumDamage, minimumDefense,
                        damageValid, defenseValid, highResourceValid, starBalanceValid,
                        currentDamage, currentDefense, currentHighResource, currentStarGap))
                    continue;
                failure = "Could not repair starting-deck support/coverage/resource cap without breaking another constraint.";
                return false;
            }
        }

        failure = $"Starting-deck support/coverage/Star repair exceeded {maximumRepairs} ordinary replacements.";
        return false;
    }

    private static bool TryApplyRegentCoverageFallback(GeneratedCard[] cards, RandomCardGenerator generator,
        Random random, int minimumDamage, int minimumDefense, bool damageValid, bool defenseValid,
        bool highResourceValid, bool starBalanceValid, int currentDamage, int currentDefense,
        int currentHighResource, int currentStarGap)
    {
        if (!cards.Any(card => card.Character == GeneratedCharacter.Regent)
            || damageValid && defenseValid || !starBalanceValid)
            return false;

        var fallbackKinds = !damageValid && !defenseValid
            ? (currentDamage - minimumDamage <= currentDefense - minimumDefense
                ? new[] { true, false }
                : new[] { false, true })
            : new[] { !damageValid };
        foreach (var damage in fallbackKinds)
        {
            var prototype = RandomCardGenerator.RegentStartingCoverageFallbackPrototype(damage);
            var prototypeExact = GeneratedCardEffectIdentity.Signature(prototype);
            var prototypeTemplate = GeneratedCardEffectIdentity.TemplateSignature(prototype);
            if (cards.Any(card => GeneratedCardEffectIdentity.Signature(card) == prototypeExact
                    || GeneratedCardEffectIdentity.TemplateSignature(card) == prototypeTemplate))
                continue;

            var candidates = Enumerable.Range(0, cards.Length)
                // The exact fallback has no support/reference operation. Replacing a neutral slot therefore
                // cannot invalidate the already-repaired Osty, Sly, or derivative graphs.
                .Where(index => !TouchesSupportGraph(cards[index]))
                .Where(index =>
                {
                    var oldCard = cards[index];
                    var prospectiveDamage = currentDamage - (CountsAsDamage(oldCard) ? 1 : 0) + (damage ? 1 : 0);
                    var prospectiveDefense = currentDefense - (CountsAsDefense(oldCard) ? 1 : 0) + (damage ? 0 : 1);
                    var prospectiveHighResource = currentHighResource - (IsHighResourceCard(oldCard) ? 1 : 0) + 1;
                    var prospectiveStarGap = currentStarGap - (ConsumesStars(oldCard) ? 1 : 0)
                        + (ProducesStars(oldCard) ? 1 : 0) + 1;
                    return (damage ? prospectiveDamage > currentDamage : prospectiveDefense > currentDefense)
                        && (!damageValid || prospectiveDamage >= minimumDamage)
                        && (!defenseValid || prospectiveDefense >= minimumDefense)
                        && (highResourceValid
                            ? prospectiveHighResource <= MaximumHighResourceCards
                            : prospectiveHighResource < currentHighResource)
                        && prospectiveStarGap <= 0;
                })
                .OrderByDescending(index => IsHighResourceCard(cards[index]) ? 1 : 0)
                .ThenBy(_ => random.Next())
                .ToArray();
            if (candidates.Length == 0) continue;

            try
            {
                cards[candidates[0]] = generator.CreateRegentStartingCoverageFallback(damage);
                return true;
            }
            catch (InvalidOperationException)
            {
                // The bounded outer pool generator remains the final fallback if identity/upgrade generation fails.
            }
        }
        return false;
    }

    private static bool TouchesSupportGraph(GeneratedCard card) =>
        OstyPoolConstraintResolver.HasOstyEffect(card)
        || OstyPoolConstraintResolver.HasSummonEffect(card)
        || SlyPoolConstraintResolver.HasSly(card)
        || SlyPoolConstraintResolver.HasDiscardEffect(card)
        || DerivativePoolConstraintResolver.HasProducedDerivativeReference(card)
        || card.Operations.Any(operation => DerivativeSlotCatalog.IsProducer(operation.Template));

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

    public static bool ConsumesStars(GeneratedCard card) => card.StarCost > 0 || card.HasStarCostX;

    public static bool ProducesStars(GeneratedCard card) => card.Operations.Any(CardEffectRules.IsStarGainOperation);

    private static int RuntimeValueAtOne(OperationRuntimeSpec spec, string slotId, int fallback)
    {
        var slot = spec.Values.FirstOrDefault(value => value.Id == slotId);
        if (slot is null) return fallback;
        return Math.Max(0, slot.Source == "fixed" ? slot.BaseValue + slot.Offset : 1 + slot.Offset);
    }
}
