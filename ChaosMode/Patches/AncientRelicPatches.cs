using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace AutoAnthony.Patches;

internal static class ChaosAncientRelics
{
    private static readonly System.Reflection.FieldInfo ArchaicToothStarterCardField =
        AccessTools.Field(typeof(ArchaicTooth), "_serializableStarterCard");
    private static readonly System.Reflection.FieldInfo ArchaicToothAncientCardField =
        AccessTools.Field(typeof(ArchaicTooth), "_serializableAncientCard");
    internal static readonly System.Reflection.FieldInfo ArchaicToothExtraHoverTipsField =
        AccessTools.Field(typeof(ArchaicTooth), "_extraHoverTips");
    private static readonly System.Reflection.FieldInfo DustyTomeExtraHoverTipsField =
        AccessTools.Field(typeof(DustyTome), "_extraHoverTips");

    internal static bool IsSupportedCharacter(Player? player) => player is not null
        && TryCharacter(player, out var character) && ChaosRunDefinitions.IsCharacterRunActive(character);

    internal static bool ShouldOverrideArchaicTooth(Player? player) =>
        IsSupportedCharacter(player) && ChaosRunDefinitions.ActiveReplaceStartingCards;

    internal static bool ShouldOverrideDustyTome(Player? player) => IsSupportedCharacter(player);

    internal static bool TryCharacter(Player player, out ChaosCardGenerator.GeneratedCharacter character)
    {
        var mapped = ChaosCharacterMapping.From(player.Character);
        character = mapped.GetValueOrDefault();
        return mapped.HasValue;
    }

    internal static CardModel CreateAncient(Player player, int index)
    {
        return player.RunState.CreateCard(AncientCanonical(player, index), player);
    }

    internal static CardModel AncientCanonical(Player player, int index)
    {
        if (!TryCharacter(player, out var character))
            throw new InvalidOperationException($"Unsupported ancient-card owner {player.Character.Id}.");
        if (ChaosRunDefinitions.ActivePreserveOriginalCards)
            return OriginalAncientCanonical(character, index);
        var slot = ChaosRunDefinitions.CountFor(character) - ChaosRunDefinitions.AncientCount + index;
        return ChaosCardRegistry.Canonical(character, slot);
    }

    internal static CardModel OriginalAncientCanonical(
        ChaosCardGenerator.GeneratedCharacter character, int index)
    {
        if (index is < 0 or >= ChaosRunDefinitions.AncientCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        var originals = ChaosRunDefinitions.OriginalCardsForPool(character)
            .Where(card => card.Rarity == CardRarity.Ancient).ToArray();
        var transcendenceIds = ArchaicTooth.TranscendenceCards.Select(card => card.Id).ToHashSet();
        var ordered = originals.OrderByDescending(card => transcendenceIds.Contains(card.Id)).ToArray();
        if (ordered.Length != ChaosRunDefinitions.AncientCount
            || !transcendenceIds.Contains(ordered[0].Id)
            || transcendenceIds.Contains(ordered[1].Id))
            throw new InvalidOperationException(
                $"Could not resolve the vanilla Archaic Tooth/Dusty Tome Ancient pair for {character}.");
        return ordered[index];
    }

    internal static void BindArchaicToothCard(ArchaicTooth archaicTooth, CardModel card)
    {
        // Vanilla ArchaicTooth.UpdateHoverTips eagerly expands every tip on both referenced cards while Orobas is
        // still generating his relic options. Our replacement no longer transforms a starter card, so retain only
        // the generated Ancient reference and its stable card preview. Bypassing the private property setter here
        // also avoids creating/enumerating an unrelated run-owned card during event initialization.
        // SavedProperties logs and hashes every annotated value during each multiplayer checksum. A null
        // StarterCard therefore produced hundreds of warnings per combat and unnecessary network/checksum work.
        // The replacement flow never reads StarterCard, so use the Ancient card itself as a non-null sentinel and
        // keep the public hover-tip surface trimmed to the single actual reward below.
        ArchaicToothStarterCardField.SetValue(archaicTooth, new SerializableCard { Id = card.Id });
        ArchaicToothAncientCardField.SetValue(archaicTooth, new SerializableCard { Id = card.Id });
        ArchaicToothExtraHoverTipsField.SetValue(archaicTooth,
            new List<IHoverTip> { HoverTipFactory.FromCard(card) });
        ((StringVar)archaicTooth.DynamicVars["AncientCard"]).StringValue = card.Title;
    }

    internal static void BindDustyTomeCard(DustyTome dustyTome, CardModel card)
    {
        // DustyTome.AncientCard normally retains card.HoverTips as a lazy Concat sequence. NEventRoom places the
        // first Ancient dialogue line before it creates the option buttons; those buttons are where that sequence
        // is first enumerated. A generated card whose secondary hover-tip construction fails can therefore abort
        // SetOptions, leave OnSetupComplete uncalled, and make Darv look stuck on his first line.
        //
        // The referenced upgraded card is the useful part of this relic option. Store that preview eagerly as a
        // stable one-element array, so Darv setup never executes arbitrary generated-card tip construction.
        dustyTome.AncientCard = card.Id;
        DustyTomeExtraHoverTipsField.SetValue(dustyTome,
            new IHoverTip[] { HoverTipFactory.FromCard(card, upgrade: true) });
    }

    internal static bool IsStoredGeneratedCard(ModelId? id) =>
        id is { } value && ChaosCardRegistry.IsGeneratedCardId(value);

    internal static bool TryCreateStoredAncient(Player player, ModelId? id, int ancientIndex, out CardModel ancient)
    {
        if (!TryCharacter(player, out _)
            || id is not { } value
            || AncientCanonical(player, ancientIndex).Id != value)
        {
            ancient = null!;
            return false;
        }
        ancient = player.RunState.CreateCard(AncientCanonical(player, ancientIndex), player);
        return true;
    }

    internal static LocString Description(RelicModel relic, string entry)
    {
        var result = new LocString("relics", entry);
        relic.DynamicVars.AddTo(result);
        return result;
    }

    internal static bool TryDescription(RelicModel relic, string entry, out LocString description)
    {
        if (!LocString.Exists("relics", entry))
        {
            Log.Error($"[AutoAnthony] Missing relic localization entry {entry}; using the vanilla description.");
            description = null!;
            return false;
        }
        description = Description(relic, entry);
        return true;
    }
}

[HarmonyPatch(typeof(ArchaicTooth), "UpdateHoverTips")]
internal static class ArchaicToothSentinelHoverTipPatch
{
    private static void Postfix(ArchaicTooth __instance)
    {
        // On save/reconnect restore the vanilla property setters rebuild tips for both serialized cards. Equal ids
        // are our sentinel marker; collapse the duplicate back to the one Ancient reward preview without relying
        // on Owner or run state, which may not have been attached yet during deserialization.
        if (__instance.StarterCard?.Id is not { } starterId
            || __instance.AncientCard?.Id != starterId) return;
        var card = CardModel.FromSerializable(__instance.AncientCard);
        ChaosAncientRelics.ArchaicToothExtraHoverTipsField.SetValue(__instance,
            new List<IHoverTip> { HoverTipFactory.FromCard(card) });
    }
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.FromSerializable))]
internal static class LegacyArchaicToothSerializationRepairPatch
{
    private static void Postfix(ref RelicModel __result)
    {
        if (__result is not ArchaicTooth { StarterCard: null, AncientCard: { } ancient }
            || !ChaosAncientRelics.IsStoredGeneratedCard(ancient.Id)) return;
        try
        {
            // v0.2.215 and earlier intentionally serialized the replacement Tooth with a null StarterCard. Repair
            // that representation as it enters the model, before combat checksums can repeatedly serialize it.
            ChaosAncientRelics.BindArchaicToothCard((ArchaicTooth)__result, CardModel.FromSerializable(ancient));
        }
        catch (Exception exception)
        {
            Log.Warn($"[AutoAnthony] Could not normalize a legacy Archaic Tooth during load: {exception.Message}");
        }
    }
}

[HarmonyPatch(typeof(ArchaicTooth), nameof(ArchaicTooth.SetupForPlayer))]
internal static class ArchaicToothSetupPatch
{
    private static bool Prefix(ArchaicTooth __instance, Player player, ref bool __result)
    {
        if (!ChaosAncientRelics.ShouldOverrideArchaicTooth(player)) return true;
        ChaosAncientRelics.BindArchaicToothCard(__instance, ChaosAncientRelics.AncientCanonical(player, 0));
        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(ArchaicTooth), nameof(ArchaicTooth.AfterObtained))]
internal static class ArchaicToothObtainedPatch
{
    private static bool Prefix(ArchaicTooth __instance, ref Task __result)
    {
        var player = __instance.Owner;
        // With the original starting deck, keep vanilla's exact Bash/Neutralize/etc. -> Ancient transformation,
        // including upgrade and enchantment transfer. The choose-one-removal replacement is only needed when that
        // designated starter card was itself replaced by a generated Basic card.
        if (!ChaosAncientRelics.ShouldOverrideArchaicTooth(player)) return true;
        if (!ChaosAncientRelics.TryCreateStoredAncient(player, __instance.AncientCard?.Id, 0, out var ancient))
        {
            Log.Warn($"[AutoAnthony] Archaic Tooth's cached card did not match its actual owner {player.NetId}; rebuilding the reward from that player's active Ancient pool.");
            ancient = ChaosAncientRelics.CreateAncient(player, 0);
        }
        __result = ObtainAndRemove(player, ancient);
        return false;
    }

    private static async Task ObtainAndRemove(Player player, CardModel ancient)
    {
        CardCmd.PreviewCardPileAdd(await CardPileCmd.Add(ancient, PileType.Deck), 2f);
        var selected = (await CardSelectCmd.FromDeckForRemoval(player,
            new CardSelectorPrefs(CardSelectorPrefs.RemoveSelectionPrompt, 1))).ToList();
        if (selected.Count > 0) await CardPileCmd.RemoveFromDeck(selected);
    }
}

[HarmonyPatch(typeof(DustyTome), nameof(DustyTome.SetupForPlayer))]
internal static class DustyTomeSetupPatch
{
    private static bool Prefix(DustyTome __instance, Player player)
    {
        if (!ChaosAncientRelics.ShouldOverrideDustyTome(player)) return true;
        // Darv calls SetupForPlayer while constructing his option list, before the event UI exists. Creating a
        // run-owned card here is unnecessary and can invoke generated-card initialization during that fragile
        // phase. Dusty Tome only persists a ModelId, so bind the canonical generated Ancient directly.
        ChaosAncientRelics.BindDustyTomeCard(__instance, ChaosAncientRelics.AncientCanonical(player, 1));
        return false;
    }
}

[HarmonyPatch(typeof(DustyTome), nameof(DustyTome.AfterObtained))]
internal static class DustyTomeObtainedPatch
{
    private static bool Prefix(DustyTome __instance, ref Task __result)
    {
        var player = __instance.Owner;
        if (!ChaosAncientRelics.ShouldOverrideDustyTome(player)) return true;
        if (!ChaosAncientRelics.TryCreateStoredAncient(player, __instance.AncientCard, 1, out var ancient))
        {
            Log.Warn($"[AutoAnthony] Dusty Tome's cached card did not match its actual owner {player.NetId}; rebuilding the reward from that player's active Ancient pool.");
            ancient = ChaosAncientRelics.CreateAncient(player, 1);
        }
        __result = Obtain(player, ancient);
        return false;
    }

    private static async Task Obtain(Player player, CardModel ancient)
    {
        // Dusty Tome's vanilla reward is the upgraded card. Its hover tip is also explicitly rendered with
        // upgrade:true, so omitting this made the option preview disagree with the card actually added to the deck.
        CardCmd.Upgrade(ancient);
        CardCmd.PreviewCardPileAdd(await CardPileCmd.Add(ancient, PileType.Deck), 2f);
    }
}

// Orobas builds one mutable relic option per player. In multiplayer those event instances are reconstructed and
// executed on every peer. Never trust the starter-relic IDs cached while the option was built: a stale/rebound
// option can otherwise ask the recipient to replace another player's starter relic, faulting the event task after
// the Touch itself has already been inserted. Resolve both sides of the replacement from the relic's actual owner.
[HarmonyPatch(typeof(TouchOfOrobas), nameof(TouchOfOrobas.AfterObtained))]
internal static class TouchOfOrobasMultiplayerOwnerPatch
{
    private static bool Prefix(TouchOfOrobas __instance, ref Task __result)
    {
        if (!ChaosRunDefinitions.IsRunActive) return true;
        __result = ReplaceActualOwnersStarter(__instance);
        return false;
    }

    private static async Task ReplaceActualOwnersStarter(TouchOfOrobas touch)
    {
        var owner = touch.Owner;
        var starter = owner.Relics.FirstOrDefault(relic => relic.Rarity == RelicRarity.Starter);
        if (starter is null)
        {
            // SetupForPlayer normally prevents this option from appearing. Treat an absent starter as a recovered
            // stale multiplayer option instead of throwing inside EventSynchronizer and leaving the room disabled.
            Log.Warn($"[AutoAnthony] Touch of Orobas was obtained by player {owner.NetId}, but that player no longer owns a starter relic; skipped the replacement.");
            return;
        }

        if (touch.StarterRelic is { } cachedStarter && cachedStarter != starter.Id)
            Log.Warn($"[AutoAnthony] Touch of Orobas was cached for {cachedStarter} but obtained by player {owner.NetId} with {starter.Id}; using the actual owner's starter relic.");

        var upgradedId = touch.GetUpgradedStarterRelic(starter).Id;
        var upgraded = ModelDb.GetById<RelicModel>(upgradedId).ToMutable();
        await RelicCmd.Replace(starter, upgraded);
    }
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.DynamicDescription), MethodType.Getter)]
internal static class ChaosAncientRelicDescriptionPatch
{
    private static bool Prefix(RelicModel __instance, ref LocString __result)
    {
        var entry = __instance switch
        {
            ArchaicTooth => "ARCHAIC_TOOTH_CHAOS.description",
            DustyTome => "DUSTY_TOME_CHAOS.description",
            _ => null
        };
        // The compendium and Ancient-event previews can query the canonical relic before a mutable, player-owned
        // option exists. RelicModel.Owner deliberately throws on canonical models, so reject those instances before
        // consulting owner-dependent run settings. This is especially visible when starter replacement is disabled
        // and Archaic Tooth should use its untouched vanilla description and behavior.
        if (entry is null || !__instance.IsMutable) return true;
        var overridden = __instance switch
        {
            ArchaicTooth => ChaosAncientRelics.ShouldOverrideArchaicTooth(__instance.Owner),
            DustyTome => ChaosAncientRelics.ShouldOverrideDustyTome(__instance.Owner),
            _ => false
        };
        if (!overridden) return true;
        return !ChaosAncientRelics.TryDescription(__instance, entry, out __result);
    }
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.DynamicEventDescription), MethodType.Getter)]
internal static class ChaosAncientRelicEventDescriptionPatch
{
    private static bool Prefix(RelicModel __instance, ref LocString __result)
    {
        var entry = __instance switch
        {
            ArchaicTooth => "ARCHAIC_TOOTH_CHAOS.eventDescription",
            DustyTome => "DUSTY_TOME_CHAOS.eventDescription",
            _ => null
        };
        // See DynamicDescription above: canonical preview models have no Owner and must stay entirely on the
        // vanilla getter path.
        if (entry is null || !__instance.IsMutable) return true;
        var overridden = __instance switch
        {
            ArchaicTooth => ChaosAncientRelics.ShouldOverrideArchaicTooth(__instance.Owner),
            DustyTome => ChaosAncientRelics.ShouldOverrideDustyTome(__instance.Owner),
            _ => false
        };
        if (!overridden) return true;
        return !ChaosAncientRelics.TryDescription(__instance, entry, out __result);
    }
}
