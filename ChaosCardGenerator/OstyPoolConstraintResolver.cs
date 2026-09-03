namespace ChaosCardGenerator;

/// <summary>
/// Keeps Osty-dependent cards supported by enough Summon cards in the same generated pool. Counts are per card,
/// not per operation: a card containing several Osty clauses still consumes one support slot, while a card that
/// both depends on Osty and Summons contributes once to each side.
/// </summary>
public static class OstyPoolConstraintResolver
{
    public static bool HasOstyEffect(GeneratedCard card) =>
        card.Tags.Contains(CardTag.OstyAttack)
        || card.Operations.Any(operation =>
            operation.Template.Contains("Osty", StringComparison.Ordinal)
            || operation.Template == "A:ProxyAtomic_Calcify");

    public static bool HasSummonEffect(GeneratedCard card) =>
        card.Operations.Any(operation =>
            operation.Template.Contains("Summon", StringComparison.Ordinal)
            // Sic 'Em is decomposed into an Osty-hit trigger plus this named power payoff.
            || operation.Template == "NCR:ApplyPower_SicEmPower");

    public static void Audit(IReadOnlyList<GeneratedCard> cards)
    {
        var ostyCards = cards.Count(HasOstyEffect);
        var summonCards = cards.Count(HasSummonEffect);
        if (ostyCards > summonCards)
            throw new InvalidOperationException(
                $"Pool contains {ostyCards} Osty-effect cards but only {summonCards} Summon-effect cards.");
    }

    /// <summary>
    /// Replaces only unsupported Osty-only cards. Rarity and X-cost class are preserved, so callers may run this
    /// after starting-deck and X-quota repair without invalidating either constraint. Basic slots are never
    /// replaced; if a pathological pool cannot be repaired from its non-Basic slots, the bounded outer pool
    /// generation loop can safely retry the candidate.
    /// </summary>
    public static bool TryResolve(GeneratedCard[] cards, IReadOnlyList<GeneratedRarity> rarities,
        RandomCardGenerator generator, Random random, int replacementAttemptLimit, out string failure)
    {
        if (cards.Length != rarities.Count)
        {
            failure = "Card and rarity counts differ while resolving the Osty support constraint.";
            return false;
        }

        while (cards.Count(HasOstyEffect) > cards.Count(HasSummonEffect))
        {
            var candidates = Enumerable.Range(0, cards.Length)
                .Where(index => rarities[index] != GeneratedRarity.Basic)
                .Where(index => HasOstyEffect(cards[index]) && !HasSummonEffect(cards[index]))
                // Fixed-cost cards are cheapest to replace. X cards remain eligible as a bounded fallback.
                .OrderBy(index => XClass(cards[index]))
                .ThenBy(_ => random.Next())
                .ToArray();
            if (candidates.Length == 0)
            {
                failure = "No non-Basic Osty-only card is available to repair the pool support ratio.";
                return false;
            }

            var replaced = false;
            foreach (var index in candidates)
            {
                var requiredXClass = XClass(cards[index]);
                try
                {
                    var replacement = requiredXClass switch
                    {
                        2 => generator.GenerateReferenceFreeSpecialXMatching(rarities[index],
                            card => XClass(card) == requiredXClass && !HasOstyEffect(card)),
                        _ => generator.GenerateWithoutSpecialXMatching(rarities[index],
                            card => XClass(card) == requiredXClass && !HasOstyEffect(card))
                    };
                    cards[index] = replacement;
                    replaced = true;
                }
                catch (InvalidOperationException) { }
                if (replaced) break;
            }

            if (!replaced)
            {
                failure = "Could not generate a same-rarity, same-X-class non-Osty replacement card.";
                return false;
            }
        }

        try
        {
            Audit(cards);
            failure = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return false;
        }
    }

    // 0 = fixed cost, 1 = ordinary X, 2 = special X.
    private static int XClass(GeneratedCard card) => SpecialXCardConverter.IsSpecial(card)
        ? 2
        : SpecialXCardConverter.IsOrdinaryX(card) ? 1 : 0;
}
