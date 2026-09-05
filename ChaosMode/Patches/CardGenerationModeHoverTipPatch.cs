using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace AutoAnthony.Patches;

/// <summary>
/// Identifies the generation profile on generated cards. The mode is read from the card definition rather than
/// current settings so restored saves and run-history cards retain their original provenance.
/// </summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.HoverTips), MethodType.Getter)]
internal static class CardGenerationModeHoverTipPatch
{
    internal const string HoverTipPrefix = "AutoAnthony.GenerationMode.";

    private static void Postfix(CardModel __instance, ref IEnumerable<IHoverTip> __result)
    {
        if (!ChaosModSettings.ShowGenerationModeHoverTips || __instance is not ChaosCardModel chaosCard) return;

        var unified = chaosCard.Generated.UnifiedChaos;
        var key = unified ? "CHAOS" : "RANDOM";
        var tip = new HoverTip(
            new LocString("main_menu_ui", $"AUTO_ANTHONY_{key}_MODE_HOVER_TITLE"),
            new LocString("main_menu_ui", $"AUTO_ANTHONY_{key}_MODE_HOVER_DESCRIPTION"))
        {
            Id = $"{HoverTipPrefix}{(unified ? "Chaos" : "Random")}",
            ShouldOverrideTextOverflow = true
        };

        __result = __result.Prepend(tip);
    }
}
