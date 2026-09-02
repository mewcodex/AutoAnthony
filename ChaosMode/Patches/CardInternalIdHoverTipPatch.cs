using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace AutoAnthony.Patches;

[HarmonyPatch(typeof(CardModel), nameof(CardModel.HoverTips), MethodType.Getter)]
internal static class CardInternalIdHoverTipPatch
{
    private static void Postfix(CardModel __instance, ref IEnumerable<IHoverTip> __result)
    {
        if (!ChaosModSettings.ShowCardInternalIds) return;
        var tip = new HoverTip(
            new LocString("main_menu_ui", "AUTO_ANTHONY_CARD_INTERNAL_ID_HOVER_TITLE"),
            __instance.Id.Entry)
        {
            Id = $"{SurpriseModeUi.InternalIdHoverTipPrefix}{__instance.Id.Entry}",
            ShouldOverrideTextOverflow = true
        };
        __result = __result.Append(tip);
    }
}
