using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace AutoAnthony.Patches;

/// <summary>
/// Identifies the exceptional generation modes used by a generated card. Mode state comes from the installed
/// definition set rather than current preferences so restored saves and run-history cards retain their provenance.
/// </summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.HoverTips), MethodType.Getter)]
internal static class CardGenerationModeHoverTipPatch
{
    internal const string HoverTipPrefix = "AutoAnthony.GenerationMode.";

    private static void Postfix(CardModel __instance, ref IEnumerable<IHoverTip> __result)
    {
        if (!ChaosModSettings.ShowGenerationModeHoverTips || __instance is not ChaosCardModel chaosCard) return;

        var unified = chaosCard.Generated.UnifiedChaos;
        var numericRandom = ChaosRunDefinitions.UsesNumericRandomValues(chaosCard.Generated.Character);
        if (!unified && !numericRandom) return;

        var key = (unified, numericRandom) switch
        {
            (true, true) => "CHAOS_AND_NUMERIC_RANDOM",
            (true, false) => "CHAOS",
            _ => "NUMERIC_RANDOM"
        };
        var tip = new HoverTip(
            new LocString("main_menu_ui", $"AUTO_ANTHONY_{key}_MODE_HOVER_TITLE"),
            new LocString("main_menu_ui", $"AUTO_ANTHONY_{key}_MODE_HOVER_DESCRIPTION"))
        {
            Id = $"{HoverTipPrefix}{key}",
            ShouldOverrideTextOverflow = true
        };

        __result = __result.Prepend(tip);
    }
}
