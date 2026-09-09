using ChaosCardGenerator;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace AutoAnthony.Patches;

[HarmonyPatch(typeof(HangPower), nameof(HangPower.ModifyDamageMultiplicative))]
internal static class HangPowerPatch
{
    private static void Postfix(HangPower __instance, Creature? target, CardModel? cardSource,
        ref decimal __result)
    {
        if (target != __instance.Owner || cardSource is not ChaosCardModel chaosCard)
            return;
        if (!CardEffectRules.IsHangDamageFamily(chaosCard.Generated.Operations))
            return;
        __result = __instance.Amount;
    }
}
