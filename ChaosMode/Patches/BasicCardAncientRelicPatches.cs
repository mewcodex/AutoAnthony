using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Saves.Runs;
using System.Runtime.CompilerServices;

namespace AutoAnthony.Patches;

internal static class ChaosBasicCardAncientRelics
{
    // Every override in this file exists because generated starting cards no longer provide the vanilla
    // Strike/Defend identities that these relics edit. If the player keeps the original starting deck, let the
    // base game execute and describe the relic unchanged.
    internal static bool IsActive => ChaosRunDefinitions.IsRunActive
        && ChaosRunDefinitions.ActiveReplaceStartingCards;

    internal static LocString Description(RelicModel relic, string entry)
    {
        var result = new LocString("relics", entry);
        relic.DynamicVars.AddTo(result);
        return result;
    }
}

internal static class MassiveScrollChaosAvailability
{
    internal static bool ShouldSuppress(bool runActive, bool preserveOriginalCards) =>
        runActive && !preserveOriginalCards;
}

// Massive Scroll can only create vanilla Multiplayer cards. Generated-pool runs deliberately omit those cards
// unless Preserve Original Card Pool is enabled, so use the relic's native Neow availability filter instead of
// leaving a reward option whose candidate pool is empty.
[HarmonyPatch(typeof(MassiveScroll), nameof(MassiveScroll.IsAllowed))]
internal static class MassiveScrollChaosAvailabilityPatch
{
    private static void Postfix(ref bool __result)
    {
        if (MassiveScrollChaosAvailability.ShouldSuppress(ChaosRunDefinitions.IsRunActive,
                ChaosRunDefinitions.ActivePreserveOriginalCards))
            __result = false;
    }
}

internal static class GhostSeedEtherealPersistence
{
    internal const string PropertyName = nameof(ChaosPoolSnapshotModifier.GhostSeedEthereal);
    private sealed class Marker { }
    private static readonly ConditionalWeakTable<CardModel, Marker> MarkedCards = new();

    internal static void Mark(CardModel card)
    {
        MarkedCards.GetValue(card, static _ => new Marker());
        if (!card.GetKeywordsWithSources(KeywordSources.Local).Contains(CardKeyword.Ethereal))
            CardCmd.ApplyKeyword(card, CardKeyword.Ethereal);
    }

    internal static bool IsMarked(CardModel? card) => card is not null && MarkedCards.TryGetValue(card, out _);

    internal static bool HasMarker(SerializableCard card) =>
        card.Props?.bools?.Any(property => property.name == PropertyName && property.value) == true;

    internal static void WriteMarker(SerializableCard card)
    {
        card.Props ??= new SavedProperties();
        card.Props.bools ??= [];
        card.Props.bools.RemoveAll(property => property.name == PropertyName);
        card.Props.bools.Add(new SavedProperties.SavedProperty<bool>(PropertyName, true));
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.ToSerializable))]
internal static class GhostSeedEtherealSavePatch
{
    private static void Postfix(CardModel __instance, SerializableCard __result)
    {
        if (GhostSeedEtherealPersistence.IsMarked(__instance))
            GhostSeedEtherealPersistence.WriteMarker(__result);
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.FromSerializable))]
internal static class GhostSeedEtherealLoadPatch
{
    private static void Postfix(SerializableCard save, CardModel __result)
    {
        if (GhostSeedEtherealPersistence.HasMarker(save))
            GhostSeedEtherealPersistence.Mark(__result);
    }
}

// Ghost Seed is a one-shot deck edit in generated-card runs. Its vanilla room/combat hooks are disabled below so
// it cannot silently add Ethereal to newly-created Basic Strikes or Defends after the pickup selection is complete.
[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.HasUponPickupEffect), MethodType.Getter)]
internal static class GhostSeedChaosUponPickupPatch
{
    private static void Postfix(RelicModel __instance, ref bool __result)
    {
        if (ChaosBasicCardAncientRelics.IsActive && __instance is GhostSeed) __result = true;
    }
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.AfterObtained))]
internal static class GhostSeedChaosAfterObtainedPatch
{
    private static bool Prefix(RelicModel __instance, ref Task __result)
    {
        if (!ChaosBasicCardAncientRelics.IsActive || __instance is not GhostSeed ghostSeed) return true;
        __result = AddEtherealToChosenCards(ghostSeed);
        return false;
    }

    private static async Task AddEtherealToChosenCards(GhostSeed relic)
    {
        var eligible = PileType.Deck.GetPile(relic.Owner).Cards.Count(CanAffect);
        if (eligible == 0) return;
        var prompt = new LocString("relics", "GHOST_SEED_CHAOS.selectionScreenPrompt");
        var selected = await CardSelectCmd.FromDeckGeneric(relic.Owner,
            new CardSelectorPrefs(prompt, 0, eligible), CanAffect);
        foreach (var card in selected)
            GhostSeedEtherealPersistence.Mark(card);
    }

    internal static bool CanAffect(CardModel card) =>
        card.Type is not CardType.Curse and not CardType.Quest
        && !card.GetKeywordsWithSources(KeywordSources.Local).Contains(CardKeyword.Ethereal);
}

[HarmonyPatch(typeof(GhostSeed), nameof(GhostSeed.AfterCardEnteredCombat))]
internal static class GhostSeedChaosCardEnteredCombatPatch
{
    private static bool Prefix(CardModel card, ref Task __result)
    {
        if (!ChaosBasicCardAncientRelics.IsActive) return true;
        // Combat cards are clones of the run-deck card. Reapply from the persistent marker even if a save/load or
        // another modifier rebuilt the clone without carrying the local keyword set.
        if (GhostSeedEtherealPersistence.IsMarked(card.DeckVersion))
            GhostSeedEtherealPersistence.Mark(card);
        __result = Task.CompletedTask;
        return false;
    }
}

[HarmonyPatch(typeof(GhostSeed), nameof(GhostSeed.AfterRoomEntered))]
internal static class GhostSeedChaosRoomEnteredPatch
{
    private static bool Prefix(ref Task __result)
    {
        if (!ChaosBasicCardAncientRelics.IsActive) return true;
        __result = Task.CompletedTask;
        return false;
    }
}

[HarmonyPatch(typeof(PandorasBox), nameof(PandorasBox.AfterObtained))]
internal static class PandorasBoxChaosPatch
{
    private static bool Prefix(PandorasBox __instance, ref Task __result)
    {
        if (!ChaosBasicCardAncientRelics.IsActive) return true;
        __result = TransformAll(__instance);
        return false;
    }

    private static async Task TransformAll(PandorasBox relic)
    {
        // IsTransformable rejects Eternal cards while they are in the deck. Keep the explicit Quest check as a
        // separate invariant so this behavior does not depend on Quest cards also happening to be unremovable.
        var cards = PileType.Deck.GetPile(relic.Owner).Cards
            .Where(card => card.Type != CardType.Quest && card.IsTransformable)
            .ToList();
        var transformations = cards.Select(card => new CardTransformation(card,
            CardFactory.CreateRandomCardForTransform(card, isInCombat: false,
                relic.Owner.RunState.Rng.Niche)));
        var results = (await CardCmd.Transform(transformations, null, CardPreviewStyle.None)).ToList();
        if (results.Count > 0 && LocalContext.IsMe(relic.Owner))
            NSimpleCardsViewScreen.ShowScreen(results, new LocString("relics", "PANDORAS_BOX.infoText"));
    }
}

[HarmonyPatch(typeof(NutritiousSoup), nameof(NutritiousSoup.AfterObtained))]
internal static class NutritiousSoupChaosPatch
{
    private static bool Prefix(NutritiousSoup __instance, ref Task __result)
    {
        if (!ChaosBasicCardAncientRelics.IsActive) return true;
        __result = EnchantBasicAttacks(__instance);
        return false;
    }

    private static Task EnchantBasicAttacks(NutritiousSoup relic)
    {
        var ember = ModelDb.Enchantment<TezcatarasEmber>();
        foreach (var card in PileType.Deck.GetPile(relic.Owner).Cards.ToList())
        {
            if (card.Rarity != CardRarity.Basic || card.Type != CardType.Attack || !ember.CanEnchant(card)) continue;
            CardCmd.Enchant<TezcatarasEmber>(card, 1m);
            var vfx = NCardEnchantVfx.Create(card);
            if (vfx is not null)
                NRun.Instance?.GlobalUi.CardPreviewContainer.AddChildSafely(vfx);
        }
        return Task.CompletedTask;
    }
}

// Goopy's vanilla restriction checks the Defend tag. Generated cards intentionally do not inherit that identity
// tag, so use Fresnel Lens' GainsBlock predicate while a generated-card run is active. This keeps the enchantment
// preview screen and CardCmd.Enchant's final validation on exactly the same rule.
[HarmonyPatch(typeof(Goopy), nameof(Goopy.CanEnchant))]
internal static class GoopyChaosEligibilityPatch
{
    private static bool Prefix(CardModel card, ref bool __result)
    {
        if (!ChaosBasicCardAncientRelics.IsActive) return true;
        __result = ModelDb.Enchantment<Nimble>().CanEnchant(card);
        return false;
    }
}

[HarmonyPatch(typeof(PaelsClaw), nameof(PaelsClaw.AfterObtained))]
internal static class PaelsClawChaosPatch
{
    private static bool Prefix(PaelsClaw __instance, ref Task __result)
    {
        if (!ChaosBasicCardAncientRelics.IsActive) return true;
        __result = EnchantChosenBlockCards(__instance);
        return false;
    }

    private static async Task EnchantChosenBlockCards(PaelsClaw relic)
    {
        var goopy = ModelDb.Enchantment<Goopy>();
        var eligible = PileType.Deck.GetPile(relic.Owner).Cards.Count(goopy.CanEnchant);
        if (eligible == 0) return;
        var selected = await CardSelectCmd.FromDeckForEnchantment(relic.Owner, goopy, 1,
            card => card?.GainsBlock == true,
            new CardSelectorPrefs(CardSelectorPrefs.EnchantSelectionPrompt, 0, eligible));
        foreach (var card in selected)
        {
            CardCmd.Enchant<Goopy>(card, 1m);
            var vfx = NCardEnchantVfx.Create(card);
            if (vfx is not null)
                NRun.Instance?.GlobalUi.CardPreviewContainer.AddChildSafely(vfx);
        }
    }
}

// Ghost Seed, Nutritious Soup, and Pael's Claw already expose their keyword/enchantment tips in vanilla. Pandora's
// Box is the exception, so add Transform to both inventory hovers and Ancient-event option hovers.
[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.HoverTips), MethodType.Getter)]
internal static class PandorasBoxChaosHoverTipsPatch
{
    private static void Postfix(RelicModel __instance, ref IEnumerable<IHoverTip> __result)
    {
        if (ChaosBasicCardAncientRelics.IsActive && __instance is PandorasBox)
            __result = __result.Append(HoverTipFactory.Static(StaticHoverTip.Transform));
    }
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.HoverTipsExcludingRelic), MethodType.Getter)]
internal static class PandorasBoxChaosEventHoverTipsPatch
{
    private static void Postfix(RelicModel __instance, ref IEnumerable<IHoverTip> __result)
    {
        if (ChaosBasicCardAncientRelics.IsActive && __instance is PandorasBox)
            __result = __result.Append(HoverTipFactory.Static(StaticHoverTip.Transform));
    }
}

[HarmonyPatch(typeof(NeowsTalisman), nameof(NeowsTalisman.AfterObtained))]
internal static class NeowsTalismanChaosPatch
{
    private static bool Prefix(NeowsTalisman __instance, ref Task __result)
    {
        if (!ChaosBasicCardAncientRelics.IsActive) return true;
        __result = UpgradeTwo(__instance);
        return false;
    }

    private static async Task UpgradeTwo(NeowsTalisman relic)
    {
        var selected = await CardSelectCmd.FromDeckForUpgrade(relic.Owner,
            new CardSelectorPrefs(CardSelectorPrefs.UpgradeSelectionPrompt, 2));
        foreach (var card in selected)
            CardCmd.Upgrade(card);
    }
}

[HarmonyPatch(typeof(LeafyPoultice), nameof(LeafyPoultice.AfterObtained))]
internal static class LeafyPoulticeChaosPatch
{
    private static bool Prefix(LeafyPoultice __instance, ref Task __result)
    {
        if (!ChaosBasicCardAncientRelics.IsActive) return true;
        __result = TransformTwoAndLoseMaxHp(__instance);
        return false;
    }

    private static async Task TransformTwoAndLoseMaxHp(LeafyPoultice relic)
    {
        var selected = (await CardSelectCmd.FromDeckForTransformation(relic.Owner,
            new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt, 2))).ToList();
        var transformations = selected.Select(card => new CardTransformation(card)).ToList();
        await CardCmd.Transform(transformations, relic.Owner.PlayerRng.Transformations);
        await CreatureCmd.LoseMaxHp(new ThrowingPlayerChoiceContext(), relic.Owner.Creature,
            relic.DynamicVars.MaxHp.BaseValue, isFromCard: false);
    }
}

[HarmonyPatch(typeof(LargeCapsule), nameof(LargeCapsule.AfterObtained))]
internal static class LargeCapsuleChaosPatch
{
    private static bool Prefix(LargeCapsule __instance, ref Task __result)
    {
        if (!ChaosBasicCardAncientRelics.IsActive) return true;
        __result = ObtainRelicsAndCommons(__instance);
        return false;
    }

    private static async Task ObtainRelicsAndCommons(LargeCapsule relic)
    {
        for (var index = 0; index < relic.DynamicVars["Relics"].IntValue; index++)
            await RelicCmd.Obtain(RelicFactory.PullNextRelicFromFront(relic.Owner).ToMutable(), relic.Owner);

        var candidates = relic.Owner.Character.CardPool
            .GetUnlockedCards(relic.Owner.UnlockState, relic.Owner.RunState.CardMultiplayerConstraint)
            .Where(card => card.Rarity == CardRarity.Common)
            .ToList();
        var results = new List<CardPileAddResult>(2);
        for (var index = 0; index < 2 && candidates.Count > 0; index++)
        {
            var canonical = relic.Owner.PlayerRng.Rewards.NextItem(candidates);
            if (canonical is null) break;
            candidates.Remove(canonical);
            var card = relic.Owner.RunState.CreateCard(canonical, relic.Owner);
            results.Add(await CardPileCmd.Add(card, PileType.Deck));
        }
        CardCmd.PreviewCardPileAdd(results, 2f);
    }
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.DynamicDescription), MethodType.Getter)]
internal static class ChaosBasicCardAncientRelicDescriptionPatch
{
    private static bool Prefix(RelicModel __instance, ref LocString __result)
    {
        if (!ChaosBasicCardAncientRelics.IsActive) return true;
        var entry = __instance switch
        {
            NeowsTalisman => "NEOWS_TALISMAN_CHAOS.description",
            LeafyPoultice => "LEAFY_POULTICE_CHAOS.description",
            LargeCapsule => "LARGE_CAPSULE_CHAOS.description",
            GhostSeed => "GHOST_SEED_CHAOS.description",
            PandorasBox => "PANDORAS_BOX_CHAOS.description",
            NutritiousSoup => "NUTRITIOUS_SOUP_CHAOS.description",
            PaelsClaw => "PAELS_CLAW_CHAOS.description",
            _ => null
        };
        if (entry is null || !LocString.Exists("relics", entry)) return true;
        __result = ChaosBasicCardAncientRelics.Description(__instance, entry);
        return false;
    }
}

[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.DynamicEventDescription), MethodType.Getter)]
internal static class ChaosBasicCardAncientRelicEventDescriptionPatch
{
    private static bool Prefix(RelicModel __instance, ref LocString __result)
    {
        if (!ChaosBasicCardAncientRelics.IsActive) return true;
        var entry = __instance switch
        {
            NeowsTalisman => "NEOWS_TALISMAN_CHAOS.eventDescription",
            LeafyPoultice => "LEAFY_POULTICE_CHAOS.eventDescription",
            LargeCapsule => "LARGE_CAPSULE_CHAOS.eventDescription",
            GhostSeed => "GHOST_SEED_CHAOS.eventDescription",
            PandorasBox => "PANDORAS_BOX_CHAOS.eventDescription",
            NutritiousSoup => "NUTRITIOUS_SOUP_CHAOS.eventDescription",
            PaelsClaw => "PAELS_CLAW_CHAOS.eventDescription",
            _ => null
        };
        if (entry is null || !LocString.Exists("relics", entry)) return true;
        __result = ChaosBasicCardAncientRelics.Description(__instance, entry);
        return false;
    }
}
