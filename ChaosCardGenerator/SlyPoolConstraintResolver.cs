namespace ChaosCardGenerator;

/// <summary>
/// Keeps every Sly card supported by at least one card that can actually discard cards. Counts are per card,
/// matching the Osty/Summon pool constraint: several discard operations on one card still supply one slot.
/// </summary>
public static class SlyPoolConstraintResolver
{
    public static bool HasSly(GeneratedCard card) => card.Tags.Contains(CardTag.Sly);

    public static bool HasDiscardEffect(GeneratedCard card) =>
        card.Operations.Any(CardEffectRules.IsDiscardEffect);

    public static void Audit(IReadOnlyList<GeneratedCard> cards)
    {
        var slyCards = cards.Count(HasSly);
        var discardCards = cards.Count(HasDiscardEffect);
        if (slyCards > discardCards)
            throw new InvalidOperationException(
                $"Pool contains {slyCards} Sly cards but only {discardCards} cards with discard effects.");
    }

    /// <summary>
    /// Repairs the complete pool without touching Basic slots. Starting-card support is audited separately after
    /// all whole-pool repairs; an unsupported Basic subset causes the bounded outer generator to reroll the pool.
    /// </summary>
    public static bool TryResolve(GeneratedCard[] cards, IReadOnlyList<GeneratedRarity> rarities,
        RandomCardGenerator generator, Random random, int replacementAttemptLimit, out string failure)
    {
        if (cards.Length != rarities.Count)
        {
            failure = "Card and rarity counts differ while resolving the Sly/discard support constraint.";
            return false;
        }

        while (cards.Count(HasSly) > cards.Count(HasDiscardEffect))
        {
            var candidates = Enumerable.Range(0, cards.Length)
                .Where(index => rarities[index] != GeneratedRarity.Basic)
                .Where(index => HasSly(cards[index]) && !HasDiscardEffect(cards[index]))
                .OrderBy(_ => random.Next())
                .ToArray();
            if (candidates.Length == 0)
            {
                failure = "No non-Basic Sly-only card is available to repair the discard support ratio.";
                return false;
            }

            var replaced = false;
            foreach (var index in candidates)
            {
                var ostyWithoutCandidate = cards.Count(OstyPoolConstraintResolver.HasOstyEffect)
                    - (OstyPoolConstraintResolver.HasOstyEffect(cards[index]) ? 1 : 0);
                var summonsWithoutCandidate = cards.Count(OstyPoolConstraintResolver.HasSummonEffect)
                    - (OstyPoolConstraintResolver.HasSummonEffect(cards[index]) ? 1 : 0);
                try
                {
                    // Sly cards are fixed-cost by construction. Suppressing derivative references makes this
                    // repair incapable of adding new demand to an already-resolved derivative supply graph.
                    var replacement = generator.GenerateReferenceFreeWithoutSpecialXMatching(rarities[index],
                        card => !HasSly(card)
                            && ostyWithoutCandidate + (OstyPoolConstraintResolver.HasOstyEffect(card) ? 1 : 0)
                            <= summonsWithoutCandidate
                               + (OstyPoolConstraintResolver.HasSummonEffect(card) ? 1 : 0));
                    cards[index] = replacement;
                    replaced = true;
                }
                catch (InvalidOperationException) { }
                if (replaced) break;
            }

            if (!replaced)
            {
                failure = "Could not generate a same-rarity non-Sly replacement card.";
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

    internal static void Validate()
    {
        var discard = new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Common, "丢弃1张牌。", Array.Empty<CardTag>(),
            [new GeneratorOperation("N:Discard", OperationScope.NonTargeted, "丢弃1张牌。",
                new Dictionary<string, int>())]);
        var sly = discard with { Tags = [CardTag.Sly], Operations = [], ChineseDescription = "获得1点格挡。" };
        Audit([sly, discard]);
        try
        {
            Audit([sly]);
            throw new InvalidOperationException("奇巧/弃牌卡池约束没有拒绝无支持的奇巧牌。 ");
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("Pool contains",
                   StringComparison.Ordinal))
        {
            // Expected.
        }
    }
}
