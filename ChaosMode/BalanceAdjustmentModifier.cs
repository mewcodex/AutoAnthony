using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChaosCardGenerator;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using GeneratorCardTag = ChaosCardGenerator.CardTag;

namespace AutoAnthony;

/// <summary>
/// Optional run rule which applies a small, persistent rebalance pass after every second combat. The modifier is
/// stored in the run rather than consulting live settings, so saves and multiplayer peers keep the host's new-run
/// choice. All plans are derived from the run seed, combat ordinal and the ordered multiplayer deck multiset before
/// the local confirmation UI, so every peer applies the same run-wide pool mutation.
/// </summary>
public sealed class BalanceAdjustmentModifier : ModifierModel
{
    private const int GoldPerAdjustment = 8;
    private static readonly JsonSerializerOptions PoolStateJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private IReadOnlyList<BalanceAdjustmentPlan>? _pendingPlans;
    private bool _pendingPlansApplied;
    private int _pendingAppliedAdjustments;

    [SavedProperty] public int CompletedCombats { get; set; }
    [SavedProperty] public bool AdjustmentPending { get; set; }
    [SavedProperty] public string PendingCombatKey { get; set; } = string.Empty;
    [SavedProperty] public string ProcessedPlayerIds { get; set; } = string.Empty;
    [SavedProperty] public string AddedPoolCardsPayload { get; set; } = string.Empty;
    [SavedProperty] public string RemovedPoolCardIds { get; set; } = string.Empty;

    public override LocString Title => new("main_menu_ui", "AUTO_ANTHONY_BIWEEKLY_BALANCE_ADJUSTMENTS");
    public override LocString Description => new("main_menu_ui",
        "AUTO_ANTHONY_BALANCE_ADJUSTMENT_MODIFIER_DESCRIPTION");

    public override Task AfterCombatEnd(CombatRoom room)
    {
        AssertMutable();
        CompletedCombats++;
        if (CompletedCombats % 2 == 0)
        {
            AdjustmentPending = true;
            PendingCombatKey = CombatKey(room, CompletedCombats);
            ProcessedPlayerIds = string.Empty;
            _pendingPlans = null;
            _pendingPlansApplied = false;
            _pendingAppliedAdjustments = 0;
        }
        return Task.CompletedTask;
    }

    public override async Task BeforeCombatRewardOffered(RewardsSet rewards, CombatRoom room)
    {
        AssertMutable();
        if (!AdjustmentPending || AlreadyProcessed(rewards.Player.NetId)) return;

        IReadOnlyList<BalanceAdjustmentPlan> plans;
        try
        {
            var seed = StableSeed(RunState.Rng.StringSeed, PendingCombatKey);
            plans = _pendingPlans ??= BalanceAdjustmentPlanner.CreatePlans(this, RunState.Players, seed);
        }
        catch (Exception exception)
        {
            Log.Error($"[AutoAnthony] Balance-adjustment planning failed; rewards will continue normally: {exception}");
            MarkProcessed(rewards.Player.NetId);
            FinishPendingIfAllPlayersProcessed();
            return;
        }
        // Every peer derives one plan from the same host-authored pool and the same ordered multiplayer deck
        // multiset. Apply it once to the shared run state, but show the same previews on each local player's reward
        // pass and grant each player the corresponding rewards.
        if (!_pendingPlansApplied)
        {
            foreach (var (plan, index) in plans.Select((plan, index) => (plan, index)))
            {
                try
                {
                    if (LocalContext.IsMe(rewards.Player))
                        await BalanceAdjustmentOverlay.ShowAsync(plan.BeforePreview, plan.AfterPreview,
                            plan.LocalizationKey, index + 1, plans.Count);
                    await plan.Apply();
                    _pendingAppliedAdjustments++;
                }
                catch (Exception exception)
                {
                    // A broken preview or one invalid late hook must not strand CombatRoom before RewardsSet.Offer.
                    Log.Error($"[AutoAnthony] Skipped shared balance adjustment {index + 1}/{plans.Count} after an error: "
                              + exception);
                }
            }
            _pendingPlansApplied = true;
        }
        else if (LocalContext.IsMe(rewards.Player))
            foreach (var (plan, index) in plans.Select((plan, index) => (plan, index)))
                await BalanceAdjustmentOverlay.ShowAsync(plan.BeforePreview, plan.AfterPreview,
                    plan.LocalizationKey, index + 1, plans.Count);

        // GenerateForRoomEnd has already populated and sorted this RewardsSet before this hook runs. Exact-amount
        // GoldReward instances are populated by their constructor, but they still need to be inserted as individual
        // rewards and re-sorted so every confirmed adjustment is visible beside the normal Gold reward rather than
        // being appended below the card/relic rewards. (Auto-loot mods may of course claim these immediately.)
        for (var index = 0; index < _pendingAppliedAdjustments; index++)
            rewards.Rewards.Add(new GoldReward(GoldPerAdjustment, rewards.Player));
        rewards.Rewards.Sort((left, right) => left.RewardsSetIndex.CompareTo(right.RewardsSetIndex));

        MarkProcessed(rewards.Player.NetId);
        FinishPendingIfAllPlayersProcessed();
        Log.Info($"[AutoAnthony] Applied shared {_pendingAppliedAdjustments}/{plans.Count} balance adjustments for player "
                 + $"{rewards.Player.NetId} after combat {CompletedCombats}; queued {_pendingAppliedAdjustments} separate "
                 + $"{GoldPerAdjustment}-Gold rewards.");
    }

    private void FinishPendingIfAllPlayersProcessed()
    {
        if (!RunState.Players.All(player => AlreadyProcessed(player.NetId))) return;
        AdjustmentPending = false;
        ProcessedPlayerIds = string.Empty;
        _pendingPlans = null;
        _pendingPlansApplied = false;
        _pendingAppliedAdjustments = 0;
    }

    internal static IReadOnlyList<ModifierModel> Configure(IReadOnlyList<ModifierModel> modifiers, bool enabled)
    {
        var result = modifiers.Where(modifier => modifier is not BalanceAdjustmentModifier).ToList();
        if (enabled)
            result.Add(ModelDb.Modifier<BalanceAdjustmentModifier>().ToMutable());
        return result;
    }

    private static string CombatKey(CombatRoom room, int ordinal) =>
        $"{room.CombatState.RunState.CurrentActIndex}:{room.CombatState.RunState.TotalFloor}:"
        + $"{room.CombatState.RunState.CurrentRoomCount}:{room.RoomType}:{ordinal}";

    private bool AlreadyProcessed(ulong playerId) => ProcessedPlayerIds.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(value => ulong.TryParse(value, out var parsed) && parsed == playerId);

    private void MarkProcessed(ulong playerId)
    {
        if (AlreadyProcessed(playerId)) return;
        ProcessedPlayerIds = ProcessedPlayerIds.Length == 0
            ? playerId.ToString()
            : ProcessedPlayerIds + "," + playerId;
    }

    private static int StableSeed(string runSeed, string combatKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{runSeed}|balance|{combatKey}|shared"));
        return BitConverter.ToInt32(bytes, 0);
    }

    internal Task ApplyDefinitionChange(ChaosCardModel source, GeneratedCard adjusted, string portraitPath)
    {
        AssertMutable();
        var poolEntryId = source.BalancePoolEntryId;
        if (poolEntryId.Length > 0)
        {
            var additions = AddedPoolCards().ToList();
            var index = additions.FindIndex(entry => entry.EntryId == poolEntryId);
            if (index < 0)
                throw new InvalidOperationException($"Balance pool entry '{poolEntryId}' is not registered.");
            additions[index] = additions[index] with
            {
                CardPayload = CardTinkeringApi.SerializeCard(adjusted),
                PortraitPath = portraitPath
            };
            SaveAddedPoolCards(additions);
            foreach (var card in MatchingDeckCards(source).ToArray())
            {
                card.ApplyFreeformDefinition(adjusted);
                card.FreeformPortraitPath = portraitPath;
                card.BalancePoolEntryId = poolEntryId;
                AutoAnthonyBalanceAdjustmentApi.NotifyDefinitionChanged(card);
            }
            return Task.CompletedTask;
        }

        if (!ChaosCardRegistry.TryGetGeneratedCardSlot(source.Id, out var character, out var slot))
        {
            var replacedExternalPool = ExternalComponentCharacterApi.TryReplaceRunPoolCard(source, adjusted);
            foreach (var card in MatchingDeckCards(source).ToArray())
            {
                if (replacedExternalPool) card.RebindToSharedPoolDefinition();
                else
                {
                    card.ApplyFreeformDefinition(adjusted);
                    card.FreeformPortraitPath = portraitPath;
                }
                AutoAnthonyBalanceAdjustmentApi.NotifyDefinitionChanged(card);
            }
            return Task.CompletedTask;
        }

        ChaosRunDefinitions.ReplaceRunPoolCard(character, slot, adjusted);
        foreach (var card in MatchingDeckCards(source).ToArray())
        {
            card.RebindToSharedPoolDefinition();
            AutoAnthonyBalanceAdjustmentApi.NotifyDefinitionChanged(card);
        }
        return Task.CompletedTask;
    }

    internal async Task ApplyRemoval(ChaosCardModel source)
    {
        AssertMutable();
        var matches = MatchingDeckCards(source).ToArray();
        if (source.BalancePoolEntryId.Length > 0)
        {
            SaveAddedPoolCards(AddedPoolCards()
                .Where(entry => entry.EntryId != source.BalancePoolEntryId).ToArray());
        }
        else
        {
            var removed = RemovedPoolIds();
            removed.Add(source.Id.ToString());
            RemovedPoolCardIds = JsonSerializer.Serialize(removed.OrderBy(value => value).ToArray(),
                PoolStateJsonOptions);
        }

        foreach (var card in matches)
        {
            var placeholder = card.Owner.RunState.CreateCard(ModelDb.Card<DeprecatedCard>(), card.Owner);
            await CardPileCmd.RemoveFromDeck(card, showPreview: false);
            await CardPileCmd.Add(placeholder, PileType.Deck);
        }
    }

    internal string CreatePoolEntryId(GeneratedCard card, int nonce)
    {
        var ordinal = AddedPoolCards().Count;
        var payload = CardTinkeringApi.SerializeCard(card);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{RunState.Rng.StringSeed}|{PendingCombatKey}|addition|{ordinal}|{nonce}|{payload}"));
        return Convert.ToHexString(hash)[..24];
    }

    internal async Task ApplyAddition(ChaosCardModel card, string profileId, string poolEntryId)
    {
        AssertMutable();
        var additions = AddedPoolCards().ToList();
        if (additions.All(entry => entry.EntryId != poolEntryId))
        {
            additions.Add(new AddedPoolCard(poolEntryId, profileId, card.Pool.Id.ToString(),
                CardTinkeringApi.SerializeCard(card.Generated), card.EffectivePortraitDefinition.PortraitPath));
            SaveAddedPoolCards(additions);
        }
        card.BalancePoolEntryId = poolEntryId;
        await CardPileCmd.Add(card, PileType.Deck);
    }

    public override CardCreationOptions ModifyCardRewardCreationOptions(Player player, CardCreationOptions options)
    {
        var removed = RemovedPoolIds();
        if (removed.Count == 0) return options;
        var previous = options.CardPoolFilter;
        return options.WithFilter(card => (previous?.Invoke(card) ?? true) && !removed.Contains(card.Id.ToString()));
    }

    public override IEnumerable<CardModel> ModifyMerchantCardPool(Player player, IEnumerable<CardModel> options)
    {
        var removed = RemovedPoolIds();
        return removed.Count == 0 ? options : options.Where(card => !removed.Contains(card.Id.ToString()));
    }

    public override bool TryModifyCardRewardOptionsLate(Player player,
        List<CardCreationResult> cardRewardOptions, CardCreationOptions creationOptions)
    {
        var additions = AddedPoolCards();
        if (additions.Count == 0) return false;
        var availablePoolIds = creationOptions.CardPools.Select(pool => pool.Id.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var baseCards = creationOptions.GetPossibleCards(player).ToArray();
        var chosenAdditions = new HashSet<string>(StringComparer.Ordinal);
        var rng = creationOptions.RngOverride ?? player.PlayerRng.Rewards;
        var changed = false;

        foreach (var result in cardRewardOptions)
        {
            var candidates = additions.Where(entry => !chosenAdditions.Contains(entry.EntryId)
                                                       && availablePoolIds.Contains(entry.PoolId))
                .Select(entry => (Entry: entry, Card: DeserializeAddedCard(entry)))
                .Where(entry => ToGameRarity(entry.Card.Rarity) == result.Card.Rarity
                                && ToGameType(entry.Card.Type) == result.Card.Type)
                .ToList();
            if (candidates.Count == 0) continue;
            if (creationOptions.CardPoolFilter is { } filter)
                candidates.RemoveAll(candidate => !AdditionPassesFilter(player, candidate.Entry, filter));
            if (candidates.Count == 0) continue;

            var baseCount = baseCards.Count(card => card.Rarity == result.Card.Rarity
                                                    && card.Type == result.Card.Type);
            var roll = rng.NextInt(Math.Max(1, baseCount + candidates.Count));
            if (roll < baseCount) continue;
            var selected = candidates[roll - baseCount];
            var replacement = CreateAddedPoolCard(player, selected.Entry, selected.Card);
            CopyRewardCardState(result.Card, replacement);
            result.ModifyCard(replacement);
            chosenAdditions.Add(selected.Entry.EntryId);
            changed = true;
        }
        return changed;
    }

    private IEnumerable<ChaosCardModel> MatchingDeckCards(ChaosCardModel source)
    {
        // A slot ID is only the shared pool identity. Card Tinkering may give one physical copy a different
        // structured definition while retaining that ID. Match the full effective definition as well so a pool
        // rebalance never erases an independently edited copy.
        var sourcePayload = CardTinkeringApi.SerializeCard(source.Generated);
        if (source.BalancePoolEntryId.Length > 0)
            return RunState.Players.SelectMany(player => player.Deck.Cards.OfType<ChaosCardModel>())
                .Where(card => card.BalancePoolEntryId == source.BalancePoolEntryId
                               && CardTinkeringApi.SerializeCard(card.Generated) == sourcePayload);
        return RunState.Players.SelectMany(player => player.Deck.Cards.OfType<ChaosCardModel>())
            .Where(card => card.BalancePoolEntryId.Length == 0 && card.Id == source.Id
                           && CardTinkeringApi.SerializeCard(card.Generated) == sourcePayload);
    }

    internal bool IsAdjustablePoolRepresentative(ChaosCardModel card)
    {
        if (card.BalancePoolEntryId.Length == 0)
            return !card.HasTinkeredDefinition;
        var entry = AddedPoolCards().FirstOrDefault(candidate => candidate.EntryId == card.BalancePoolEntryId);
        return entry is not null
               && CardTinkeringApi.SerializeCard(card.Generated) == entry.CardPayload;
    }

    private IReadOnlyList<AddedPoolCard> AddedPoolCards()
    {
        if (string.IsNullOrWhiteSpace(AddedPoolCardsPayload)) return [];
        try
        {
            return JsonSerializer.Deserialize<AddedPoolCard[]>(AddedPoolCardsPayload, PoolStateJsonOptions) ?? [];
        }
        catch (Exception exception)
        {
            Log.Error($"[AutoAnthony] Ignoring invalid balance-adjustment added-pool state: {exception.Message}");
            return [];
        }
    }

    private void SaveAddedPoolCards(IEnumerable<AddedPoolCard> additions) =>
        AddedPoolCardsPayload = JsonSerializer.Serialize(additions.ToArray(), PoolStateJsonOptions);

    private HashSet<string> RemovedPoolIds()
    {
        if (string.IsNullOrWhiteSpace(RemovedPoolCardIds)) return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            return (JsonSerializer.Deserialize<string[]>(RemovedPoolCardIds, PoolStateJsonOptions) ?? [])
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception exception)
        {
            Log.Error($"[AutoAnthony] Ignoring invalid balance-adjustment removed-pool state: {exception.Message}");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static GeneratedCard DeserializeAddedCard(AddedPoolCard entry) =>
        OperationRuntimeSpecCompiler.Attach(CardTinkeringApi.DeserializeCard(entry.CardPayload));

    private static ChaosCardModel CreateAddedPoolCard(Player player, AddedPoolCard entry, GeneratedCard card)
    {
        var created = AutoAnthonyFreeformCardApi.CreateForDeck(player, entry.ProfileId, card, entry.PortraitPath);
        created.BalancePoolEntryId = entry.EntryId;
        return created;
    }

    private static bool AdditionPassesFilter(Player player, AddedPoolCard entry, Func<CardModel, bool> filter)
    {
        var preview = CreateAddedPoolCard(player, entry, DeserializeAddedCard(entry));
        try { return filter(preview); }
        finally { player.RunState.RemoveCard(preview); }
    }

    private static void CopyRewardCardState(CardModel source, CardModel target)
    {
        if (source.Enchantment is { } enchantment)
        {
            var copy = EnchantmentModel.FromSerializable(enchantment.ToSerializable());
            target.EnchantInternal(copy, enchantment.Amount);
            target.Enchantment!.ModifyCard();
            target.FinalizeUpgradeInternal();
        }
        for (var index = 0; index < source.CurrentUpgradeLevel; index++)
        {
            target.UpgradeInternal();
            target.FinalizeUpgradeInternal();
        }
    }

    private static CardRarity ToGameRarity(GeneratedRarity rarity) => rarity switch
    {
        GeneratedRarity.Basic => CardRarity.Basic,
        GeneratedRarity.Common => CardRarity.Common,
        GeneratedRarity.Uncommon => CardRarity.Uncommon,
        GeneratedRarity.Rare => CardRarity.Rare,
        GeneratedRarity.Ancient => CardRarity.Ancient,
        _ => CardRarity.Common
    };

    private static CardType ToGameType(GeneratedCardType type) => type switch
    {
        GeneratedCardType.Attack => CardType.Attack,
        GeneratedCardType.Skill => CardType.Skill,
        GeneratedCardType.Power => CardType.Power,
        _ => CardType.Skill
    };

    private sealed record AddedPoolCard(string EntryId, string ProfileId, string PoolId,
        string CardPayload, string PortraitPath);
}

internal sealed record BalanceAdjustmentPlan(
    CardModel? BeforePreview,
    CardModel AfterPreview,
    string LocalizationKey,
    Func<Task> Apply);

internal static class BalanceAdjustmentPlanner
{
    private const int MaximumPlans = 5;
    private const int MinimumPlans = 3;
    private const double ExpandedLowerFactor = 0.90d;
    private const double ExpandedUpperFactor = 1.10d;

    internal static IReadOnlyList<BalanceAdjustmentPlan> CreatePlans(BalanceAdjustmentModifier modifier,
        IReadOnlyList<Player> players, int seed)
    {
        var random = new Random(seed);
        var desired = random.Next(MinimumPlans, MaximumPlans + 1);
        // Preserve multiplicity: if three players jointly own four copies of a card, that identity receives four
        // tickets. Stable player/card ordering makes this selection identical on every multiplayer peer.
        var owned = players.OrderBy(player => player.NetId)
            .SelectMany(player => player.Deck.Cards.OfType<ChaosCardModel>()
                .Where(modifier.IsAdjustablePoolRepresentative)
                .Select((card, index) => (Card: card, Index: index)))
            .OrderBy(entry => entry.Card.Owner.NetId)
            .ThenBy(entry => entry.Card.Id.Entry, StringComparer.Ordinal)
            .ThenBy(entry => entry.Card.BalancePoolEntryId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Card)
            .ToList();
        Shuffle(owned, random);
        var plans = new List<BalanceAdjustmentPlan>(desired);

        while (owned.Count > 0 && plans.Count < desired)
        {
            var card = owned[0];
            owned.RemoveAt(0);
            if (!TryCreateOwnedCardPlan(modifier, card, random, out var plan)) continue;
            plans.Add(plan);
            // Multiplicity affects the chance of being the next selected identity, but one global pool entry must
            // not receive two conflicting adjustments in the same batch.
            owned.RemoveAll(candidate => SamePoolIdentity(card, candidate));
        }
        return plans;
    }

    private static bool SamePoolIdentity(ChaosCardModel left, ChaosCardModel right) =>
        left.BalancePoolEntryId.Length > 0 || right.BalancePoolEntryId.Length > 0
            ? left.BalancePoolEntryId.Length > 0 && left.BalancePoolEntryId == right.BalancePoolEntryId
            : left.Id == right.Id;

    private static bool TryCreateOwnedCardPlan(BalanceAdjustmentModifier modifier, ChaosCardModel source, Random random,
        out BalanceAdjustmentPlan plan)
    {
        plan = null!;
        var order = WeightedChangeOrder(source, random);
        foreach (var kind in order)
        {
            if (kind == ChangeKind.Rework)
            {
                if (TryCreateReworkPlan(modifier, source, random, out plan)) return true;
                continue;
            }
            if (kind == ChangeKind.Addition)
            {
                if (TryCreateAddedCardPlan(modifier, source.Owner, source, random, out plan)) return true;
                continue;
            }
            if (kind == ChangeKind.Removal)
            {
                if (TryCreateRemovalPlan(modifier, source, out plan)) return true;
                continue;
            }
            if (!TryCreateDefinition(source, random, kind, out var adjusted, out var localizationKey)) continue;
            var portraitPath = source.EffectivePortraitDefinition.PortraitPath;
            var before = (ChaosCardModel)source.ClonePreservingMutability();
            var after = (ChaosCardModel)source.ClonePreservingMutability();
            after.ApplyFreeformDefinition(adjusted);
            after.FreeformPortraitPath = portraitPath;
            plan = new BalanceAdjustmentPlan(before, after, localizationKey, () =>
            {
                return modifier.ApplyDefinitionChange(source, adjusted, portraitPath);
            });
            return true;
        }
        return false;
    }

    private static ChangeKind[] WeightedChangeOrder(ChaosCardModel source, Random random)
    {
        var firstRoll = random.Next(1000);
        var first = firstRoll < 10 ? ChangeKind.Cost
            : firstRoll < 70 ? source.IsTransformable ? ChangeKind.Rework : ChangeKind.Numeric
            : firstRoll < 130 ? ChangeKind.Addition
            : firstRoll < 190 ? ChangeKind.Removal
            : firstRoll < 285 ? ChangeKind.Exhaust
            : firstRoll < 375 ? ChangeKind.PromoteUpgrade
            : firstRoll < 445 ? ChangeKind.DemoteBase
            : ChangeKind.Numeric;
        // Cost and structural changes are genuinely rare outcomes, not generic fallbacks for a card whose scalar
        // fields were awkward to edit. Rework, addition and removal each occupy the same nominal 6% band.
        var remaining = new[]
        {
            ChangeKind.Numeric, ChangeKind.Exhaust, ChangeKind.PromoteUpgrade, ChangeKind.DemoteBase
        }.Where(kind => kind != first).ToList();
        Shuffle(remaining, random);
        return [first, .. remaining];
    }

    private static bool TryCreateDefinition(ChaosCardModel source, Random random, ChangeKind kind,
        out GeneratedCard adjusted, out string localizationKey)
    {
        adjusted = source.Generated;
        localizationKey = string.Empty;
        var success = kind switch
        {
            ChangeKind.Numeric => TryNumericChange(source.Generated, random, out adjusted),
            ChangeKind.PromoteUpgrade => TryPromoteUpgrade(source, random, out adjusted),
            ChangeKind.DemoteBase => TryDemoteBase(source, random, out adjusted),
            ChangeKind.Exhaust => TryToggleExhaust(source.Generated, out adjusted),
            ChangeKind.Cost => TryChangeCost(source.Generated, random, out adjusted),
            _ => false
        };
        if (!success || !WithinExpandedEnvelope(adjusted)) return false;
        try
        {
            var validation = CardTinkeringApi.Validate(adjusted, adjusted.Operations);
            if (!validation.IsValid) return false;
            if (string.IsNullOrEmpty(source.RuntimeProfileId))
                CardTemplateValidator.Validate(adjusted, allowRandomizedNumericValues: true);
        }
        catch
        {
            return false;
        }
        localizationKey = kind switch
        {
            ChangeKind.Numeric => "AUTO_ANTHONY_BALANCE_ADJUSTMENT_NUMERIC",
            ChangeKind.PromoteUpgrade => "AUTO_ANTHONY_BALANCE_ADJUSTMENT_UPGRADE_PROMOTED",
            ChangeKind.DemoteBase => "AUTO_ANTHONY_BALANCE_ADJUSTMENT_UPGRADE_DEMOTED",
            ChangeKind.Exhaust when adjusted.Tags.Contains(GeneratorCardTag.Exhaust) =>
                "AUTO_ANTHONY_BALANCE_ADJUSTMENT_EXHAUST_ADDED",
            ChangeKind.Exhaust => "AUTO_ANTHONY_BALANCE_ADJUSTMENT_EXHAUST_REMOVED",
            ChangeKind.Cost => "AUTO_ANTHONY_BALANCE_ADJUSTMENT_COST",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        return true;
    }

    private static bool TryNumericChange(GeneratedCard card, Random random, out GeneratedCard adjusted)
    {
        adjusted = card;
        var analysis = CardTinkeringApi.Analyze(card,
            new CardTinkeringEvaluationOptions(BalancedValues(), null));
        var candidates = card.Operations.SelectMany((operation, index) =>
                CardTinkeringApi.GetEditableValues(operation)
                    .Where(value => !value.IsXOffset && value.Value > 0 && value.Maximum > value.Minimum)
                    .Select(value => (Index: index, Value: value,
                        Negative: index < analysis.Components.Count && analysis.Components[index].IsNegative)))
            .ToList();
        Shuffle(candidates, random);
        foreach (var candidate in candidates)
        {
            var current = candidate.Value.Value;
            var step = NumericStep(current);
            // Increasing a reward is a buff, while decreasing a negative amount is a buff. Keep the bias modest:
            // replacement is neutral and Exhaust toggles are classified by their actual direction below, so the
            // combined adjustment population remains varied rather than becoming a stream of upgrades.
            var strengtheningDirection = candidate.Negative ? -1 : 1;
            var preferredDirection = PreferStrengthening(random)
                ? strengtheningDirection
                : -strengtheningDirection;
            var directions = new[] { preferredDirection, -preferredDirection };
            foreach (var direction in directions)
            {
                var next = Math.Clamp(current + direction * step,
                    Math.Max(1, candidate.Value.Minimum), candidate.Value.Maximum);
                if (next == current || !CardTinkeringApi.TrySetEditableValue(
                        card.Operations[candidate.Index], candidate.Value.Id, next, out var operation)) continue;
                var operations = card.Operations.ToArray();
                operations[candidate.Index] = operation;
                try
                {
                    adjusted = Rebuild(card, operations);
                    if (GeneratedCardEffectIdentity.Signature(adjusted)
                        != GeneratedCardEffectIdentity.Signature(card)) return true;
                }
                catch
                {
                    // Try another safe scalar slot.
                }
            }
        }
        return false;
    }

    private static int NumericStep(int value) => value switch
    {
        >= 80 => 10,
        >= 35 => 5,
        >= 18 => 3,
        >= 8 => 2,
        _ => 1
    };

    private static bool TryPromoteUpgrade(ChaosCardModel source, Random random, out GeneratedCard adjusted)
    {
        adjusted = source.Generated;
        var card = source.Generated;
        if (source.IsUpgraded || card.Upgrade is not { } upgrade) return false;
        try
        {
            var operations = CardUpgradeGenerator.ApplyEffectsToOperations(card.Operations, upgrade.Effects);
            var tags = card.Tags.Concat(upgrade.AddedKeywords)
                .Except(upgrade.RemovedKeywords ?? []).Distinct().ToArray();
            var custom = (card.CustomKeywords ?? []).Concat(upgrade.AddedCustomKeywords ?? [])
                .Except(upgrade.RemovedCustomKeywords ?? [], StringComparer.Ordinal).Distinct().ToArray();
            var promoted = card with
            {
                Cost = upgrade.UpgradedCost,
                StarCost = upgrade.UpgradedStarCost ?? card.StarCost,
                Tags = tags,
                CustomKeywords = custom,
                Operations = operations,
                Upgrade = null
            };
            promoted = Rebuild(promoted, operations);
            var replacementUpgrade = CardUpgradeGenerator.Generate(promoted, random, card.UnifiedChaos);
            adjusted = promoted with { Upgrade = replacementUpgrade };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDemoteBase(ChaosCardModel source, Random random, out GeneratedCard adjusted)
    {
        adjusted = source.Generated;
        if (source.IsUpgraded) return false;
        var card = source.Generated;
        var analysis = CardTinkeringApi.Analyze(card,
            new CardTinkeringEvaluationOptions(BalancedValues(), null));
        var candidates = card.Operations.SelectMany((operation, index) =>
                CardTinkeringApi.GetEditableValues(operation)
                    .Where(value => !value.IsXOffset && value.Value > 1)
                    .Select(value => (Index: index, Value: value,
                        Negative: analysis.Components[index].IsNegative)))
            .Where(candidate => !candidate.Negative).ToList();
        Shuffle(candidates, random);
        foreach (var candidate in candidates)
        {
            var delta = Math.Min(NumericStep(candidate.Value.Value), candidate.Value.Value - 1);
            if (delta <= 0 || !CardTinkeringApi.TrySetEditableValue(card.Operations[candidate.Index],
                    candidate.Value.Id, candidate.Value.Value - delta, out var lowered)) continue;
            var operations = card.Operations.ToArray();
            operations[candidate.Index] = lowered;
            try
            {
                var baseCard = Rebuild(card with { Upgrade = null }, operations);
                var effect = new CardUpgradeEffect(CardUpgradeKind.IncreaseNumber, candidate.Index, delta,
                    candidate.Value.Id);
                var upgradedOperations = CardUpgradeGenerator.ApplyEffectsToOperations(operations, [effect]);
                var plan = new CardUpgradePlan(baseCard.Cost, [effect],
                    CardDescriptionRenderer.Render(upgradedOperations), [],
                    EnglishCardDescriptionRenderer.Render(upgradedOperations), [], baseCard.StarCost);
                adjusted = baseCard with { Upgrade = plan };
                return true;
            }
            catch
            {
                // Try another numeric reward.
            }
        }
        return false;
    }

    private static bool TryToggleExhaust(GeneratedCard card, out GeneratedCard adjusted)
    {
        adjusted = card;
        if (card.Type == GeneratedCardType.Power) return false;
        var hasExhaust = card.Tags.Contains(GeneratorCardTag.Exhaust);
        var tags = hasExhaust
            ? card.Tags.Where(tag => tag != GeneratorCardTag.Exhaust).ToArray()
            : card.Tags.Append(GeneratorCardTag.Exhaust).Distinct().ToArray();
        var upgrade = card.Upgrade;
        if (hasExhaust && upgrade is not null)
            upgrade = upgrade with
            {
                Effects = upgrade.Effects.Where(effect => effect.Kind != CardUpgradeKind.RemoveExhaust).ToArray(),
                RemovedKeywords = (upgrade.RemovedKeywords ?? []).Where(tag => tag != GeneratorCardTag.Exhaust).ToArray()
            };
        adjusted = card with { Tags = tags, Upgrade = upgrade };
        return true;
    }

    private static bool TryChangeCost(GeneratedCard card, Random random, out GeneratedCard adjusted)
    {
        adjusted = card;
        if (card.Cost < 0 || card.HasStarCostX || card.Tags.Contains(GeneratorCardTag.Sly)) return false;
        var direction = card.Cost == 0 ? 1 : card.Cost == 4 ? -1 : PreferStrengthening(random) ? -1 : 1;
        var cost = Math.Clamp(card.Cost + direction, 0, 4);
        if (cost == card.Cost) return false;
        var upgrade = card.Upgrade;
        if (upgrade is not null && upgrade.UpgradedCost >= 0)
            upgrade = upgrade with { UpgradedCost = Math.Clamp(upgrade.UpgradedCost + direction, 0, 4) };
        adjusted = card with { Cost = cost, Upgrade = upgrade };
        return true;
    }

    private static bool TryCreateReworkPlan(BalanceAdjustmentModifier modifier, ChaosCardModel source, Random random,
        out BalanceAdjustmentPlan plan)
    {
        plan = null!;
        if (!source.IsTransformable) return false;
        var candidates = new List<(GeneratedCard Card, long Weight)>();
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                var generated = NewGenerator(source, random.Next()).Generate(source.Generated.Rarity);
                if (GeneratedCardEffectIdentity.Signature(generated)
                        == GeneratedCardEffectIdentity.Signature(source.Generated)
                    || generated.Operations.Any(operation =>
                        ComponentPolicy.Multiplicity(operation) == ComponentMultiplicity.UniquePerPool)
                    || source.IsUpgraded && generated.Upgrade is null) continue;
                candidates.Add((generated, ReworkIdentityWeight(source, generated, random)));
            }
            catch
            {
                // Try another complete generated shell.
            }
        }
        if (candidates.Count == 0) return false;

        foreach (var candidate in WeightedCandidateOrder(candidates, random))
        {
            try
            {
                // Name and portrait are the card's identity. Rework everything else while retaining the exact
                // current card instance so upgrade level, enchantment and other persistent per-card state survive.
                var generated = candidate with { Name = source.Generated.Name ?? candidate.Name };
                var portrait = source.PortraitPath;
                var before = (ChaosCardModel)source.ClonePreservingMutability();
                var after = (ChaosCardModel)source.ClonePreservingMutability();
                after.ApplyFreeformDefinition(generated);
                after.FreeformPortraitPath = portrait;
                plan = new BalanceAdjustmentPlan(before, after,
                    "AUTO_ANTHONY_BALANCE_ADJUSTMENT_REPLACED", () =>
                    {
                    return modifier.ApplyDefinitionChange(source, generated, portrait);
                    });
                return true;
            }
            catch (Exception exception)
            {
                Log.Warn($"[AutoAnthony] Could not build a rework balance adjustment: {exception.Message}");
            }
        }
        return false;
    }

    private static long ReworkIdentityWeight(ChaosCardModel source, GeneratedCard generated, Random random)
    {
        var score = 0;
        var sourceIds = source.Generated.Name?.SourceCardIds ?? [];
        var generatedIds = generated.Name?.SourceCardIds ?? [];
        score += sourceIds.Intersect(generatedIds, StringComparer.Ordinal).Count() * 8;
        if (source.Generated.Type == generated.Type) score += 2;
        if (source.Generated.Cost == generated.Cost && source.Generated.StarCost == generated.StarCost) score++;
        try
        {
            ExternalComponentCharacterApi.TryGetProfileId(source, out var profileId);
            var relatedIdentity = AutoAnthonyEditorApi.RerollEditorIdentity(profileId, generated, random.Next());
            var portrait = source.EffectivePortraitDefinition;
            if (!string.IsNullOrWhiteSpace(portrait.PortraitSourceId)
                && string.Equals(portrait.PortraitSourceId, relatedIdentity.PortraitSourceId,
                    StringComparison.OrdinalIgnoreCase)) score += 12;
            else if (string.Equals(portrait.PortraitPath, relatedIdentity.PortraitPath,
                         StringComparison.OrdinalIgnoreCase)) score += 8;
            score += sourceIds.Intersect(relatedIdentity.Name.SourceCardIds, StringComparer.Ordinal).Count() * 4;
        }
        catch
        {
            // Profiles may support generated cards without opting into editor identities. Name/type/cost relevance
            // still provides a useful inverse match in that case.
        }
        return 1L + (long)score * score;
    }

    private static IEnumerable<GeneratedCard> WeightedCandidateOrder(
        IReadOnlyList<(GeneratedCard Card, long Weight)> source, Random random)
    {
        var remaining = source.ToList();
        while (remaining.Count > 0)
        {
            var total = remaining.Sum(candidate => candidate.Weight);
            var roll = random.NextInt64(total);
            var selected = 0;
            for (; selected < remaining.Count - 1; selected++)
            {
                if (roll < remaining[selected].Weight) break;
                roll -= remaining[selected].Weight;
            }
            var candidate = remaining[selected];
            remaining.RemoveAt(selected);
            yield return candidate.Card;
        }
    }

    private static bool TryCreateRemovalPlan(BalanceAdjustmentModifier modifier, ChaosCardModel source,
        out BalanceAdjustmentPlan plan)
    {
        plan = null!;
        try
        {
            var placeholder = source.Owner.RunState.CreateCard(ModelDb.Card<DeprecatedCard>(), source.Owner);
            var before = (ChaosCardModel)source.ClonePreservingMutability();
            plan = new BalanceAdjustmentPlan(before, placeholder,
                "AUTO_ANTHONY_BALANCE_ADJUSTMENT_REMOVED",
                () => modifier.ApplyRemoval(source));
            return true;
        }
        catch (Exception exception)
        {
            Log.Warn($"[AutoAnthony] Could not build a removal balance adjustment: {exception.Message}");
            return false;
        }
    }

    private static bool TryCreateAddedCardPlan(BalanceAdjustmentModifier modifier, Player player,
        ChaosCardModel artSource, Random random,
        out BalanceAdjustmentPlan plan)
    {
        plan = null!;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                var rarity = attempt < 6 ? artSource.Generated.Rarity
                    : (GeneratedRarity)random.Next((int)GeneratedRarity.Common, (int)GeneratedRarity.Ancient + 1);
                var generated = NewGenerator(artSource, random.Next()).Generate(rarity);
                if (CardTinkeringApi.IsVariableX(generated)
                    || generated.Operations.Any(operation =>
                        ComponentPolicy.Multiplicity(operation) == ComponentMultiplicity.UniquePerPool)) continue;
                var outlier = CreateOutlier(generated, random);
                if (outlier is null) continue;
                ExternalComponentCharacterApi.TryGetProfileId(artSource, out var profileId);
                var card = AutoAnthonyFreeformCardApi.CreateForDeck(player, profileId, outlier,
                    artSource.EffectivePortraitDefinition.PortraitPath);
                var poolEntryId = modifier.CreatePoolEntryId(outlier, random.Next());
                card.BalancePoolEntryId = poolEntryId;
                plan = new BalanceAdjustmentPlan(null, card, "AUTO_ANTHONY_BALANCE_ADJUSTMENT_ADDED",
                    () => modifier.ApplyAddition(card, profileId, poolEntryId));
                return true;
            }
            catch
            {
                // Retry with another complete generated shell.
            }
        }
        return false;
    }

    private static GeneratedCard? CreateOutlier(GeneratedCard card, Random random)
    {
        var bounds = Bounds(card);
        if (bounds is null || bounds.Value.Minimum <= 0 || !double.IsFinite(bounds.Value.Maximum)) return null;
        var high = random.Next(2) == 0;
        var target = high
            ? bounds.Value.Maximum * (1.05d + random.NextDouble() * 0.95d)
            : bounds.Value.Minimum * (0.50d + random.NextDouble() * 0.45d);
        var current = card;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var analysis = CardTinkeringApi.Analyze(current,
                new CardTinkeringEvaluationOptions(BalancedValues(), null));
            if (high
                    ? analysis.Budget.NetValue > bounds.Value.Maximum
                      && analysis.Budget.NetValue <= bounds.Value.Maximum * 2d
                    : analysis.Budget.NetValue >= bounds.Value.Minimum * 0.5d
                      && analysis.Budget.NetValue < bounds.Value.Minimum)
                return current;
            var scale = Math.Clamp(target / Math.Max(1d, analysis.Budget.NetValue), 0.35d, 3d);
            if (!TryScalePositiveValues(current, analysis, scale, out current)) return null;
        }
        return null;
    }

    private static bool TryScalePositiveValues(GeneratedCard card, CardTinkeringCardAnalysis analysis,
        double scale, out GeneratedCard adjusted)
    {
        adjusted = card;
        var operations = card.Operations.ToArray();
        var changed = false;
        foreach (var component in analysis.Components.Where(component => !component.IsNegative
                                                                        && !component.IsTrigger
                                                                        && component.AtomicValue > 0d))
        foreach (var value in CardTinkeringApi.GetEditableValues(operations[component.OperationIndex])
                     .Where(value => !value.IsXOffset && value.Value > 0))
        {
            var scaled = Math.Clamp((int)Math.Round(value.Value * scale, MidpointRounding.AwayFromZero),
                Math.Max(1, value.Minimum), value.Maximum);
            if (scaled == value.Value || !CardTinkeringApi.TrySetEditableValue(
                    operations[component.OperationIndex], value.Id, scaled, out var operation)) continue;
            operations[component.OperationIndex] = operation;
            changed = true;
        }
        if (!changed) return false;
        adjusted = Rebuild(card, operations);
        return true;
    }

    private static GeneratedCard Rebuild(GeneratedCard card, IReadOnlyList<GeneratorOperation> operations) =>
        CardTinkeringApi.Rebuild(card, operations,
            card.Upgrade?.Effects.Where(effect => effect.OperationIndex is not null).ToArray());

    private static bool WithinExpandedEnvelope(GeneratedCard card)
    {
        if (CardTinkeringApi.IsVariableX(card))
            return new[] { 1, 3 }.All(x => WithinExpandedEnvelopeAtX(card, x));
        var analysis = CardTinkeringApi.Analyze(card,
            new CardTinkeringEvaluationOptions(BalancedValues(), null));
        var bounds = Bounds(card);
        return bounds is not null
               && analysis.Budget.NetValue >= bounds.Value.Minimum * ExpandedLowerFactor
               && analysis.Budget.NetValue <= bounds.Value.Maximum * ExpandedUpperFactor;
    }

    private static bool WithinExpandedEnvelopeAtX(GeneratedCard card, int x)
    {
        var analysis = CardTinkeringApi.AnalyzeAtX(card, x, BalancedValues());
        var materialized = VariableXCardBalance.MaterializeOperations(card.Operations, x);
        var projected = card with
        {
            Cost = card.Cost < 0 ? x : card.Cost,
            StarCost = card.HasStarCostX ? x : card.StarCost,
            HasStarCostX = false,
            Operations = materialized
        };
        var bounds = Bounds(projected);
        return bounds is not null
               && analysis.Budget.NetValue >= bounds.Value.Minimum * ExpandedLowerFactor
               && analysis.Budget.NetValue <= bounds.Value.Maximum * ExpandedUpperFactor;
    }

    private static (double Minimum, double Maximum)? Bounds(GeneratedCard card)
    {
        var analysis = CardTinkeringApi.Analyze(card,
            new CardTinkeringEvaluationOptions(BalancedValues(), null));
        if (double.IsNaN(analysis.Budget.EffectiveCost)) return null;
        var powerFactor = ComponentAssemblyGenerator.PowerOneShotBudgetFactorForTinkering(
            card.Operations, card.Type);
        var bounds = ComponentAssemblyGenerator.WholeCardBudgetBounds(card.Rarity,
            analysis.Budget.EffectiveCost, Math.Max(1, analysis.Budget.PositiveRewardFields),
            powerFactor: powerFactor, balancedValues: BalancedValues(), character: card.Character);
        return (bounds.Minimum, analysis.Budget.OrdinaryUpperBound);
    }

    private static RandomCardGenerator NewGenerator(GeneratedCharacter character, int seed) =>
        new(character, seed, unlockComponentRoles: ChaosRunDefinitions.ActiveUltimateChaos,
            balancedValues: BalancedValues(), randomizeNumericValues: false);

    private static RandomCardGenerator NewGenerator(ChaosCardModel source, int seed)
    {
        if (!ExternalComponentCharacterApi.TryGetProfileId(source, out var profileId)
            || !ComponentApi.TryGetProfileRequest(profileId,
                ChaosRunDefinitions.ActiveUltimateChaos, out var request))
            return NewGenerator(source.Generated.Character, seed);
        return new RandomCardGenerator(request, seed, balancedValues: BalancedValues(),
            randomizeNumericValues: false);
    }

    private static bool BalancedValues() => !ChaosRunDefinitions.IsRunActive
                                           || ChaosRunDefinitions.ActiveNumericBalanceOptimization;

    private static bool PreferStrengthening(Random random) => random.NextDouble() < 0.56d;

    private static void Shuffle<T>(IList<T> list, Random random)
    {
        for (var index = list.Count - 1; index > 0; index--)
        {
            var other = random.Next(index + 1);
            (list[index], list[other]) = (list[other], list[index]);
        }
    }

    private enum ChangeKind
    {
        Numeric, PromoteUpgrade, DemoteBase, Exhaust, Cost, Rework, Addition, Removal
    }
}

/// <summary>A non-cancelable, upgrade-shaped before/after confirmation shown before the reward screen.</summary>
internal sealed class BalanceAdjustmentOverlay : Control, IOverlayScreen
{
    private readonly CardModel? _before;
    private readonly CardModel _after;
    private readonly string _localizationKey;
    private readonly int _current;
    private readonly int _total;
    // Vanilla selection screens complete their task on the Godot thread. Confirm() removes the overlay before
    // completing this source, so an inline continuation cannot race the old backstop and all subsequent deck/reward
    // mutations stay on the scene thread.
    private readonly TaskCompletionSource _completion = new();
    private Button? _confirm;
    private Control? _beforeHost;
    private Control? _afterHost;
    private bool _closing;

    private BalanceAdjustmentOverlay(CardModel? before, CardModel after, string localizationKey,
        int current, int total)
    {
        _before = before;
        _after = after;
        _localizationKey = localizationKey;
        _current = current;
        _total = total;
        Name = "AutoAnthonyBalanceAdjustment";
        MouseFilter = MouseFilterEnum.Stop;
    }

    public NetScreenType ScreenType => NetScreenType.CardSelection;
    public bool UseSharedBackstop => true;
    public Control? DefaultFocusedControl => _confirm;

    internal static Task ShowAsync(CardModel? before, CardModel after, string localizationKey,
        int current, int total)
    {
        if (TestMode.IsOn || NOverlayStack.Instance is not { } stack) return Task.CompletedTask;
        var overlay = new BalanceAdjustmentOverlay(before, after, localizationKey, current, total);
        try
        {
            // The non-card shell is safe to build before entering the scene tree. NCard.UpdateVisuals deliberately
            // does nothing until its node is ready, however, so card previews must be attached only after Push has
            // added this overlay to the live tree. Building cards before Push leaves the shared backstop visible
            // with blank previews and can look like a permanent black screen.
            overlay.BuildShell();
            stack.Push(overlay);
            overlay.PopulateCardPreviews();
            Log.Info($"[AutoAnthony] Showing balance adjustment {current}/{total}.");
            return overlay._completion.Task;
        }
        catch (Exception exception)
        {
            Log.Error($"[AutoAnthony] Balance-adjustment overlay could not be shown; applying the deterministic "
                      + $"adjustment without blocking rewards: {exception}");
            if (stack.Peek() == overlay) stack.Remove(overlay);
            else if (overlay.GetParent() is { } parent) parent.RemoveChildSafely(overlay);
            overlay.QueueFreeSafely();
            overlay._completion.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private void BuildShell()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(1120, 760) };
        center.AddChild(panel);
        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 28);
        panel.AddChild(margin);
        var column = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        column.AddThemeConstantOverride("separation", 14);
        margin.AddChild(column);

        var titleText = new LocString("main_menu_ui", "AUTO_ANTHONY_BALANCE_ADJUSTMENT_TITLE");
        titleText.Add("current", _current);
        titleText.Add("total", _total);
        var title = new Label
        {
            Text = titleText.GetFormattedText(),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.AddThemeFontSizeOverride("font_size", 34);
        title.AddThemeColorOverride("font_color", Colors.White);
        title.AddThemeColorOverride("font_outline_color", Colors.Black);
        title.AddThemeConstantOverride("outline_size", 6);
        column.AddChild(title);
        var explanation = new Label
        {
            Text = Text(_localizationKey),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        explanation.AddThemeFontSizeOverride("font_size", 22);
        explanation.AddThemeColorOverride("font_color", Colors.White);
        explanation.AddThemeColorOverride("font_outline_color", Colors.Black);
        explanation.AddThemeConstantOverride("outline_size", 4);
        column.AddChild(explanation);

        var cards = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            CustomMinimumSize = new Vector2(1000, 540)
        };
        cards.AddThemeConstantOverride("separation", 80);
        column.AddChild(cards);
        if (_before is not null)
        {
            _beforeHost = CreateCardHost();
            cards.AddChild(_beforeHost);
        }
        var arrow = new Label
        {
            Text = _before is null ? "+" : "→",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(90, 500)
        };
        arrow.AddThemeFontSizeOverride("font_size", 64);
        arrow.AddThemeColorOverride("font_color", Colors.White);
        arrow.AddThemeColorOverride("font_outline_color", Colors.Black);
        arrow.AddThemeConstantOverride("outline_size", 6);
        cards.AddChild(arrow);
        _afterHost = CreateCardHost();
        cards.AddChild(_afterHost);

        _confirm = new Button
        {
            Text = Text("AUTO_ANTHONY_BALANCE_ADJUSTMENT_CONFIRM"),
            CustomMinimumSize = new Vector2(310, 60),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter
        };
        _confirm.AddThemeFontSizeOverride("font_size", 24);
        _confirm.Pressed += Confirm;
        column.AddChild(_confirm);
    }

    private void PopulateCardPreviews()
    {
        if (!IsInsideTree() || !IsNodeReady())
            throw new InvalidOperationException("The balance-adjustment overlay was not ready after being pushed.");
        if (_before is not null && _beforeHost is not null)
            AttachCardPreview(_beforeHost, _before);
        if (_afterHost is null)
            throw new InvalidOperationException("The balance-adjustment result preview host was not constructed.");
        AttachCardPreview(_afterHost, _after);
    }

    public override void _Ready()
    {
        _confirm?.CallDeferred(Control.MethodName.GrabFocus);
    }

    private static Control CreateCardHost() => new() { CustomMinimumSize = new Vector2(340, 500) };

    private static void AttachCardPreview(Control host, CardModel model)
    {
        var cardNode = NCard.Create(model);
        if (cardNode is null) return;
        var holder = NPreviewCardHolder.Create(cardNode, showHoverTips: true, scaleOnHover: false);
        if (holder is null) return;
        holder.SetCardScale(Vector2.One * 0.82f);
        holder.Position = new Vector2(170, 250);
        host.AddChild(holder);
        // Adding the holder to the live host makes both it and NCard ready synchronously. This explicit refresh is
        // required because NCard._Ready() reloads its art/frame but does not populate description and cost text.
        cardNode.UpdateVisuals(PileType.Deck, CardPreviewMode.Normal);
    }

    private void Confirm()
    {
        if (_closing || _completion.Task.IsCompleted) return;
        _closing = true;
        if (_confirm is not null) _confirm.Disabled = true;
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        var stack = NOverlayStack.Instance;
        if (stack is not null) stack.Remove(this);
        _completion.TrySetResult();
        this.QueueFreeSafely();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel")) GetViewport().SetInputAsHandled();
    }

    public void AfterOverlayOpened() => Visible = true;
    public void AfterOverlayClosed() { }
    public void AfterOverlayShown()
    {
        Visible = true;
        _confirm?.CallDeferred(Control.MethodName.GrabFocus);
    }
    public void AfterOverlayHidden() => Visible = false;

    public override void _ExitTree()
    {
        // Scene teardown must never leave the reward coroutine awaiting a vanished node.
        _completion.TrySetResult();
    }

    private static string Text(string key) => new LocString("main_menu_ui", key).GetFormattedText();
}
