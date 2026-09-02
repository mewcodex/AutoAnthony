namespace ChaosCardGenerator;

/// <summary>
/// Re-applies pool support constraints to the ten generated cards used as the starting deck. Local replacements
/// preserve X class and caller-specified combat coverage, while complete-pool constraints are handled separately.
/// </summary>
public static class StartingPoolConstraintResolver
{
    public static bool TryRepair(GeneratedCard[] cards, RandomCardGenerator generator, Random random,
        int minimumDamage, int minimumDefense, int replacementAttemptLimit, out string failure)
    {
        var maximumRepairs = cards.Length * 4;
        var perReplacementAttempts = Math.Min(1024, replacementAttemptLimit);
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
            if (ostyValid && slyValid && derivativeValid)
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
                .Where(index => currentOstyGap > 0
                        && OstyPoolConstraintResolver.HasOstyEffect(cards[index])
                        && !OstyPoolConstraintResolver.HasSummonEffect(cards[index])
                    || currentSlyGap > 0
                        && SlyPoolConstraintResolver.HasSly(cards[index])
                        && !SlyPoolConstraintResolver.HasDiscardEffect(cards[index])
                    || DerivativePoolConstraintResolver.HasProducedDerivativeReference(cards[index]))
                .OrderByDescending(index => currentOstyGap > 0
                    && OstyPoolConstraintResolver.HasOstyEffect(cards[index]) ? 1 : 0)
                .ThenByDescending(index => currentSlyGap > 0
                    && SlyPoolConstraintResolver.HasSly(cards[index]) ? 1 : 0)
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
                for (var attempt = 0; attempt < perReplacementAttempts; attempt++)
                {
                    var replacement = generator.GenerateReferenceFreeWithoutSpecialX(GeneratedRarity.Basic);
                    if (SpecialXCardConverter.IsOrdinaryX(replacement) != oldOrdinaryX) continue;
                    if (currentOstyGap > 0 && OstyPoolConstraintResolver.HasOstyEffect(replacement)) continue;
                    if (currentSlyGap > 0 && SlyPoolConstraintResolver.HasSly(replacement)) continue;
                    var prospective = (GeneratedCard[])cards.Clone();
                    prospective[index] = replacement;
                    if (prospective.Count(CountsAsDamage) < minimumDamage
                        || prospective.Count(CountsAsDefense) < minimumDefense)
                        continue;
                    var prospectiveOstyGap = prospective.Count(OstyPoolConstraintResolver.HasOstyEffect)
                        - prospective.Count(OstyPoolConstraintResolver.HasSummonEffect);
                    var prospectiveSlyGap = prospective.Count(SlyPoolConstraintResolver.HasSly)
                        - prospective.Count(SlyPoolConstraintResolver.HasDiscardEffect);
                    // Invalid ratios must strictly improve; already-valid ratios may not worsen. This also proves
                    // that repairing the ten-card subpool cannot invalidate the corresponding complete-pool ratio.
                    if (currentOstyGap > 0 ? prospectiveOstyGap >= currentOstyGap
                            : prospectiveOstyGap > currentOstyGap)
                        continue;
                    if (currentSlyGap > 0 ? prospectiveSlyGap >= currentSlyGap
                            : prospectiveSlyGap > currentSlyGap)
                        continue;
                    cards[index] = replacement;
                    replaced = true;
                    break;
                }
                if (replaced) break;
            }
            if (!replaced)
            {
                failure = "Could not repair starting-deck support without breaking combat coverage or X class.";
                return false;
            }
        }

        failure = $"Starting-deck support repair exceeded {maximumRepairs} replacements.";
        return false;
    }

    public static bool CountsAsDamage(GeneratedCard card) => card.Operations.Any(operation =>
        CardEffectRules.IsEnemyDamage(operation)
        || operation.Template == "N:RetaliateDamage"
        || OrbSlotCatalog.ResolveOutput(operation.OrbOutputId, operation.Template)?.DealsDamage == true);

    public static bool CountsAsDefense(GeneratedCard card) => card.Operations.Any(operation =>
        operation.Template is "N:B" or "N:BlockEqualAllPoison" or "N:NextTurnBlock"
            or "I:DrawAndBlockIfSkill" or "NCR:BlockTripleOstyMaxHp"
            or "CL:GainBlockEqualDamage" or "CL:GainBlockEqualCurrent"
            or "CL:GainNextTurnBlockEqualCurrent"
        || OrbSlotCatalog.ResolveOutput(operation.OrbOutputId, operation.Template)?.Id == "frost"
        || operation.Template is "NCR:Summon" or "NCR:SummonX");
}
