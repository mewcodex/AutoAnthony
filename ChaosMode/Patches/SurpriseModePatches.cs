using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Saves;
using System.Runtime.CompilerServices;

namespace AutoAnthony.Patches;

internal static class SurpriseModeUi
{
    internal const string InternalIdHoverTipPrefix = "AutoAnthony.CardInternalId.";

    private static readonly System.Reflection.FieldInfo SimpleRewardResultsField =
        AccessTools.Field(typeof(NSimpleCardSelectScreen), "_cardResults");
    private static readonly System.Reflection.FieldInfo MerchantCardNodeField =
        AccessTools.Field(typeof(NMerchantCard), "_cardNode");
    private static readonly System.Text.RegularExpressions.Regex ImageTagRegex = new(
        @"\[img(?:=[^\]]*)?\].*?\[/img\]",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.Singleline);
    private static readonly System.Text.RegularExpressions.Regex BbCodeTagRegex = new(
        @"\[[^\]\r\n]+\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly string[] ConcealedParts =
    [
        "%EnergyIcon", "%EnergyLabel", "%UnplayableEnergyIcon",
        "%StarIcon", "%StarLabel", "%UnplayableStarIcon"
    ];
    private sealed class CostConcealmentMarker;
    private static readonly ConditionalWeakTable<NCard, CostConcealmentMarker> CostConcealedCards = new();

    internal static bool ShouldConceal(CardModel? card) => card is not null
        && ChaosModSettings.AnySurpriseMode
        && ChaosRunDefinitions.IsRunActive
        && !SurpriseCardKnowledge.IsKnown(card);

    internal static bool IsGenerated(CardModel? card) => card?.Id is { } id
        && ChaosCardRegistry.IsGeneratedCardId(id);

    internal static bool HasBeenObtained(CardModel card) => SurpriseCardKnowledge.IsKnown(card)
        || !ChaosRunDefinitions.IsRunActive
        && SaveManager.Instance?.Progress.DiscoveredCards.Contains(card.Id) == true;

    internal static bool ShouldConcealDescription(CardModel? card, Node owner) =>
        ChaosModSettings.SurpriseModePro && IsGenerated(card)
        || ShouldConceal(card) && IsAcquisitionContext(owner);

    internal static bool ShouldConcealCost(CardModel? card, Node owner) =>
        ChaosModSettings.SurpriseModePro && IsGenerated(card) && !HasBeenObtained(card!)
        || ShouldConceal(card) && IsAcquisitionContext(owner);

    internal static bool ShouldConcealHoverTips(CardModel? card, Node owner) =>
        ChaosModSettings.SurpriseModePro && IsGenerated(card)
        || ShouldConceal(card) && IsAcquisitionContext(owner);

    internal static bool IsAcquisitionContext(Node node)
    {
        for (Node? current = node; current is not null; current = current.GetParent())
        {
            if (current is NCardRewardSelectionScreen
                or NMerchantCard
                or NChooseACardSelectionScreen
                or NChooseABundleSelectionScreen)
                return true;
            if (current is NSimpleCardSelectScreen simple && IsRewardGrid(simple))
                return true;
        }
        return false;
    }

    internal static bool IsCardLibraryContext(Node node)
    {
        for (Node? current = node; current is not null; current = current.GetParent())
            if (current is NCardLibraryGrid)
                return true;
        return false;
    }

    internal static bool IsRewardGrid(NSimpleCardSelectScreen screen) =>
        SimpleRewardResultsField.GetValue(screen) is not null;

    internal static bool MayInspect(CardModel? card) =>
        !(ChaosModSettings.SurpriseModePro && IsGenerated(card)) && !ShouldConceal(card);

    internal static CardModel? MerchantCard(NMerchantCard merchant) =>
        MerchantCardNodeField.GetValue(merchant) is NCard card ? card.Model : null;

    /// <summary>
    /// Replaces an acquisition card's normal hover-tip set while it is still unknown. Card references (including
    /// derivative previews) are ordinary CardHoverTips, so filtering to our opt-in internal-ID tip hides them too.
    /// Returns true when vanilla hover-tip creation must be skipped, including when ID display is disabled and the
    /// deliberately empty result should show no hover panel at all.
    /// </summary>
    internal static bool ShowConcealedHoverTips(Control owner, CardModel? card, NCardHolder? holder = null)
    {
        if (!ShouldConcealHoverTips(card, owner)) return false;
        var idTips = card!.HoverTips
            .Where(tip => tip.Id.StartsWith(InternalIdHoverTipPrefix, StringComparison.Ordinal))
            .ToArray();
        if (idTips.Length > 0)
        {
            var set = NHoverTipSet.CreateAndShow(owner, idTips);
            if (holder is not null) set?.SetAlignmentForCardHolder(holder);
        }
        return true;
    }

    /// <summary>
    /// Merchant cards use the slot hitbox as their hover-tip anchor rather than a card holder. Keeping this separate
    /// from the generic acquisition-card path mirrors vanilla's NMerchantCard.CreateHoverTip layout exactly.
    /// </summary>
    internal static bool ShowConcealedMerchantHoverTips(NMerchantCard merchant)
    {
        var card = MerchantCard(merchant);
        if (!ShouldConcealHoverTips(card, merchant)) return false;
        var idTips = card!.HoverTips
            .Where(tip => tip.Id.StartsWith(InternalIdHoverTipPrefix, StringComparison.Ordinal))
            .ToArray();
        if (idTips.Length > 0)
            NHoverTipSet.CreateAndShow(merchant, idTips)
                ?.SetAlignment(merchant.Hitbox, HoverTip.GetHoverTipAlignment(merchant));
        return true;
    }

    internal static void RestoreParts(NCard card)
    {
        if (!CostConcealedCards.TryGetValue(card, out _) || !card.IsNodeReady()) return;
        foreach (var path in ConcealedParts)
            if (card.GetNodeOrNull<CanvasItem>(path) is { } part)
                part.Visible = true;
        CostConcealedCards.Remove(card);
    }

    internal static void ConcealParts(NCard card)
    {
        var model = card.Model;
        if (!card.IsNodeReady()) return;
        var concealDescription = ShouldConcealDescription(model, card);
        var concealCost = ShouldConcealCost(model, card);
        if (!concealDescription && !concealCost) return;
        if (concealDescription
            && card.GetNodeOrNull<MegaRichTextLabel>("%DescriptionLabel") is { } description)
        {
            if (ChaosModSettings.SurpriseModeLite && !ChaosModSettings.SurpriseModePro)
                description.SetTextAutoSize(BuildLiteDescription(description.Text, model!.Id.Entry));
            else
                // The card scene's normal description size is 21, so 42 is exactly 200%.
                description.SetTextAutoSize("[center][font_size=42]？？？[/font_size][/center]");
        }
        if (concealCost)
        {
            var concealedAny = false;
            foreach (var path in ConcealedParts)
                if (card.GetNodeOrNull<CanvasItem>(path) is { } part)
                {
                    part.Visible = false;
                    concealedAny = true;
                }
            if (concealedAny)
                CostConcealedCards.GetValue(card, static _ => new CostConcealmentMarker());
        }
    }

    internal static string BuildLiteDescription(string formattedText, string stableCardId)
    {
        // Image tags have a path as their body. Replace the entire tag with one neutral text cell before removing
        // the remaining BBCode, otherwise the asset path itself would leak into the card description.
        var plainText = ImageTagRegex.Replace(formattedText, "◆");
        plainText = BbCodeTagRegex.Replace(plainText, string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        var characters = plainText.ToCharArray();
        var maskable = Enumerable.Range(0, characters.Length)
            .Where(index => characters[index] is not '\r' and not '\n' && !char.IsWhiteSpace(characters[index]))
            .ToArray();
        var maskCount = (int)Math.Round(maskable.Length * 0.8, MidpointRounding.AwayFromZero);
        var seed = StableHash(stableCardId + "\0" + plainText);
        foreach (var index in maskable.OrderBy(index => Mix(seed ^ (uint)index)).Take(maskCount))
            characters[index] = '■';
        // Centering controls layout only. No color, bold, dynamic-variable or keyword highlighting survives.
        return "[center]" + new string(characters) + "[/center]";
    }

    private static uint StableHash(string value)
    {
        var hash = 2166136261u;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 16777619u;
        }
        return hash;
    }

    private static uint Mix(uint value)
    {
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        return value ^ (value >> 16);
    }

    internal static void AuditTextMasking()
    {
        const string source = "[center][gold]Deal 12 damage.[/gold]\nGain [blue]8[/blue] Block. [img]res://energy.png[/img][/center]";
        var masked = BuildLiteDescription(source, "AutoAnthony.Audit");
        if (masked.Contains("gold", StringComparison.OrdinalIgnoreCase)
            || masked.Contains("blue", StringComparison.OrdinalIgnoreCase)
            || masked.Contains("res://", StringComparison.OrdinalIgnoreCase)
            || !masked.Contains('\n')
            || masked.Count(character => character == '■') != 20
            || masked != BuildLiteDescription(source, "AutoAnthony.Audit"))
            throw new InvalidOperationException("Surprise Mode Lite text masking audit failed.");
    }
}

/// <summary>
/// Vanilla marks any run-attached card as seen as soon as an NCard node receives it, which happens before our
/// reward/shop concealment is rendered. In Surprise Mode an unknown candidate must remain absent from the card
/// library. A card entering a real pile is recorded first by SurpriseCardKnowledge, so its subsequent discovery
/// call remains allowed.
/// </summary>
[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.MarkCardAsSeen))]
internal static class SurpriseModeCardDiscoveryPatch
{
    private static bool Prefix(CardModel card) => !ChaosModSettings.AnySurpriseMode
        || !ChaosRunDefinitions.IsRunActive
        || SurpriseCardKnowledge.IsKnown(card);
}

[HarmonyPatch(typeof(NCardHolder), "CreateHoverTips")]
internal static class SurpriseModeCardHolderHoverTipsPatch
{
    private static bool Prefix(NCardHolder __instance) =>
        !SurpriseModeUi.ShowConcealedHoverTips(__instance, __instance.CardModel, __instance);
}

// Bundle-choice previews override NCardHolder.CreateHoverTips, so patch that implementation separately.
[HarmonyPatch(typeof(NPreviewCardHolder), "CreateHoverTips")]
internal static class SurpriseModePreviewCardHolderHoverTipsPatch
{
    private static bool Prefix(NPreviewCardHolder __instance) =>
        !SurpriseModeUi.ShowConcealedHoverTips(__instance, __instance.CardModel, __instance);
}

[HarmonyPatch(typeof(NMerchantCard), "CreateHoverTip")]
internal static class SurpriseModeMerchantHoverTipsPatch
{
    private static bool Prefix(NMerchantCard __instance) =>
        !SurpriseModeUi.ShowConcealedMerchantHoverTips(__instance);
}

[HarmonyPatch(typeof(NCard), nameof(NCard.UpdateVisuals))]
internal static class SurpriseModeCardVisualPatch
{
    // NCard nodes are pooled. Restore our previous concealment before vanilla recalculates which cost icon is valid.
    private static void Prefix(NCard __instance) => SurpriseModeUi.RestoreParts(__instance);

    private static void Postfix(NCard __instance)
    {
        // The vanilla library reads profile discovery, while Surprise Mode deliberately uses a stricter run-scoped
        // set because merely constructing an acquisition candidate can pollute the profile set. Once the library
        // actually renders a card as fully Visible, however, the player has genuinely seen its complete rules. Merge
        // that one card into run knowledge so a later reward cannot remain concealed after the library revealed it.
        if (!ChaosModSettings.SurpriseModePro
            && __instance.Visibility == ModelVisibility.Visible
            && SurpriseModeUi.IsCardLibraryContext(__instance))
            SurpriseCardKnowledge.Record(__instance.Model);

        // Do not infer discovery from rendering here: NCard.Create assigns its model before the node is parented,
        // so an acquisition candidate would briefly have no acquisition-screen ancestor and be revealed early.
        // Successful CardPileCmd.Add calls are the authoritative run-scoped discovery event instead.
        SurpriseModeUi.ConcealParts(__instance);
    }
}

[HarmonyPatch(typeof(NCard), nameof(NCard.OnReturnedFromPool))]
internal static class SurpriseModePooledCardRestorePatch
{
    private static void Prefix(NCard __instance)
    {
        SurpriseModeUi.RestoreParts(__instance);
        ChaosUnseenLibraryCardCostPatch.RestoreParts(__instance);
    }
}

[HarmonyPatch(typeof(NCardRewardSelectionScreen), "InspectCard")]
internal static class SurpriseModeRewardInspectPatch
{
    private static bool Prefix(NCardHolder cardHolder) => SurpriseModeUi.MayInspect(cardHolder.CardModel);
}

[HarmonyPatch(typeof(NChooseACardSelectionScreen), "OpenPreviewScreen")]
internal static class SurpriseModeSingleChoiceInspectPatch
{
    private static bool Prefix(NCardHolder cardHolder) => SurpriseModeUi.MayInspect(cardHolder.CardModel);
}

[HarmonyPatch(typeof(NChooseABundleSelectionScreen), "OpenPreviewScreen")]
internal static class SurpriseModeBundleInspectPatch
{
    private static bool Prefix(NCardHolder cardHolder) => SurpriseModeUi.MayInspect(cardHolder.CardModel);
}

[HarmonyPatch(typeof(NCardGridSelectionScreen), "ShowCardDetail")]
internal static class SurpriseModeGridInspectPatch
{
    private static bool Prefix(NCardGridSelectionScreen __instance, CardModel card) =>
        __instance is not NSimpleCardSelectScreen simple
        || !SurpriseModeUi.IsRewardGrid(simple)
        || SurpriseModeUi.MayInspect(card);
}

[HarmonyPatch(typeof(NMerchantCard), "OnPreview")]
internal static class SurpriseModeMerchantInspectPatch
{
    private static bool Prefix(NMerchantCard __instance) =>
        SurpriseModeUi.MayInspect(SurpriseModeUi.MerchantCard(__instance));
}
