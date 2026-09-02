using System.Text.Json;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Logging;

namespace AutoAnthony;

/// <summary>
/// Run-scoped discovery used by Surprise Mode. Merely displaying an acquisition candidate does not reveal it,
/// but a successful entry into any run/combat pile does. This covers permanent acquisitions, combat-generated
/// cards, transformations, copies, and ordinary pile movement. The compact set is saved because profile discovery
/// alone cannot distinguish a hidden candidate that
/// vanilla UI eagerly marked as seen from a card the player actually encountered this run.
/// </summary>
internal static class SurpriseCardKnowledge
{
    private const int PayloadSchema = 2;
    private sealed record KnowledgeEnvelope(int Schema, ulong? OwnerNetId, string[] Cards);

    private static readonly object Gate = new();
    private static HashSet<string> _knownCardIds = new(StringComparer.Ordinal);

    internal static void BeginNewRun()
    {
        lock (Gate) _knownCardIds = new HashSet<string>(StringComparer.Ordinal);
    }

    internal static bool IsKnown(CardModel card)
    {
        lock (Gate) return _knownCardIds.Contains(card.Id.Entry);
    }

    internal static void Record(CardModel? card)
    {
        if (card is null || !ChaosRunDefinitions.IsRunActive) return;
        // The card library renders canonical models. They have a stable ID but deliberately have no Owner,
        // RunState, or CombatState; reading any of those properties throws and aborts NCardGrid.InitGrid midway,
        // leaving the remaining library cards at stale pooled positions. A visible library card is local UI state,
        // so record it directly by ID and never enter the mutable-card ownership path below.
        if (card.IsCanonical)
        {
            bool newlySeenCanonical;
            lock (Gate) newlySeenCanonical = _knownCardIds.Add(card.Id.Entry);
            if (newlySeenCanonical
                && SaveManager.Instance is { } canonicalSaveManager
                && !canonicalSaveManager.Progress.DiscoveredCards.Contains(card.Id))
                canonicalSaveManager.MarkCardAsSeen(card);
            return;
        }

        // A multiplayer process observes pile commands for every participant. Surprise knowledge is a local UI
        // concept: another player's deck or temporary cards must not reveal the corresponding card for me.
        if (LocalContext.NetId.HasValue && card.Owner is not null && !LocalContext.IsMe(card.Owner)) return;
        bool newlySeen;
        lock (Gate) newlySeen = _knownCardIds.Add(card.Id.Entry);
        if (!newlySeen || card.RunState is null && card.CombatState is null) return;

        // Keep the game's card-library discovery state aligned with Surprise Mode. Acquisition candidates never
        // reach this method merely by being displayed; temporary combat cards reach it through CardPileCmd.Add.
        if (SaveManager.Instance is { } saveManager
            && !saveManager.Progress.DiscoveredCards.Contains(card.Id))
            saveManager.MarkCardAsSeen(card);
    }

    internal static void RecordOwnedCards(RunState runState)
    {
        var localPlayer = LocalContext.GetMe(runState);
        var players = localPlayer is null ? runState.Players : [localPlayer];
        foreach (var player in players)
            foreach (var card in player.Deck.Cards)
                Record(card);
    }

    internal static string ToSavePayload()
    {
        lock (Gate)
            return JsonSerializer.Serialize(new KnowledgeEnvelope(PayloadSchema, LocalContext.NetId,
                _knownCardIds.OrderBy(id => id, StringComparer.Ordinal).ToArray()));
    }

    internal static void RestoreFrom(SerializableRun save, string? payload)
    {
        HashSet<string> restored;
        try
        {
            restored = ParsePayload(payload);
        }
        catch
        {
            // A malformed optional knowledge payload must never prevent an otherwise valid run from loading.
            restored = new HashSet<string>(StringComparer.Ordinal);
        }

        // This also gives pre-Surprise-Mode saves the intuitive baseline: every card already in the deck is known.
        var localPlayer = LocalContext.GetMe(save);
        var players = localPlayer is null ? save.Players : [localPlayer];
        foreach (var player in players)
            foreach (var card in player.Deck)
                if (card.Id is { } id)
                    restored.Add(id.Entry);

        lock (Gate) _knownCardIds = restored;
        ReconcileGeneratedCardDiscovery(restored);
    }

    private static HashSet<string> ParsePayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return new HashSet<string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            // Schema 1 was a local array in singleplayer but was shared indiscriminately in multiplayer. Retain it
            // for ordinary old saves; the locally owned deck below remains the baseline in every case.
            return new HashSet<string>(document.RootElement.Deserialize<string[]>() ?? [], StringComparer.Ordinal);
        }

        var envelope = document.RootElement.Deserialize<KnowledgeEnvelope>();
        if (envelope is null || envelope.Schema != PayloadSchema) return new HashSet<string>(StringComparer.Ordinal);
        if (envelope.OwnerNetId.HasValue && LocalContext.NetId.HasValue
                                             && envelope.OwnerNetId != LocalContext.NetId)
        {
            Log.Info("[AutoAnthony] Ignored another multiplayer participant's Surprise knowledge payload.");
            return new HashSet<string>(StringComparer.Ordinal);
        }
        return new HashSet<string>(envelope.Cards ?? [], StringComparer.Ordinal);
    }

    private static void ReconcileGeneratedCardDiscovery(IReadOnlySet<string> known)
    {
        if (!ChaosModSettings.AnySurpriseMode
            || SaveManager.Instance?.Progress.DiscoveredCards is not ISet<ModelId> discovered)
            return;

        var removed = 0;
        foreach (var type in ChaosRunDefinitions.SupportedPools
                     .SelectMany(ChaosCardRegistry.TypesFor)
                     .Distinct())
        {
            var id = ModelDb.GetId(type);
            if (!known.Contains(id.Entry) && discovered.Remove(id)) removed++;
        }
        if (removed > 0)
            Log.Info($"[AutoAnthony] Removed {removed} generated card-library discoveries that came only from concealed acquisition candidates.");
    }

    internal static void RecordSuccessfulAdds(IReadOnlyList<CardPileAddResult> results)
    {
        foreach (var result in results)
            if (result.success)
                Record(result.cardAdded);
    }
}
