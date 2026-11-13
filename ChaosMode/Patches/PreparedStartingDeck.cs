using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace AutoAnthony.Patches;

internal static class PreparedStartingDeck
{
    // v113 constructs Player inventories before NGame's async run-start boundary. Rebind the untouched native
    // starter inventory after pool preparation, while the Player still has no RunState. Preserve additional
    // non-Basic cards and leave custom starter inventories and unsupported characters to their own providers.
    internal static void Refresh(Player player)
    {
        var character = ChaosCharacterMapping.From(player.Character);
        if (character is null || !ChaosRunDefinitions.IsCharacterRunActive(character.Value)
            || !ChaosRunDefinitions.ActiveReplaceStartingCards) return;
        // Character.CardPool is already replaced at this point, so old native cards may no longer resolve Pool.
        // Match their identities against the captured original pool instead of asking each card for its pool.
        var nativeIds = ChaosRunDefinitions.OriginalCardsForPreservedPool(character.Value)
            .Where(card => card.Rarity == CardRarity.Basic).Select(card => card.Id).ToHashSet();
        var starters = player.Deck.Cards.Where(card => card.Rarity == CardRarity.Basic
            && (card is ChaosCardModel generated ? generated.Generated.Character == character.Value
                : nativeIds.Contains(card.Id))).ToArray();
        var count = ChaosRunDefinitions.BasicCountFor(character.Value);
        if (starters.Length != count) return;
        var canonical = Enumerable.Range(0, count)
            .Select(slot => ChaosCardRegistry.Canonical(character.Value, slot)).ToArray();
        if (starters.Select(card => card.Id).SequenceEqual(canonical.Select(card => card.Id)))
        {
            foreach (var card in starters.Cast<ChaosCardModel>()) card.RefreshDefinitionCacheIfChanged();
            return;
        }
        var replacements = canonical.Select(card => card.ToMutable()).ToArray();
        var position = player.Deck.Cards.ToList().IndexOf(starters[0]);
        foreach (var card in starters) player.Deck.RemoveInternal(card, silent: true);
        foreach (var card in replacements)
        {
            card.FloorAddedToDeck = 1;
            player.Deck.AddInternal(card, position++, silent: true);
        }
    }
}
