using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace AutoAnthony.Patches;

// Native Aim has a HeavyShot-specific multiplier. Component cards retain the same native hook and consumption
// lifecycle; only the returned additive amount changes, so preview, nested attacks and actual hits stay aligned.
[HarmonyPatch(typeof(AimPower), nameof(AimPower.ModifyDamageAdditive))]
internal static class FoolAimModifierPatch
{
    private static void Postfix(CardModel? cardSource, ref decimal __result)
    {
        if (__result == 0 || cardSource is not ChaosCardModel card) return;
        for (var index = 0; index < card.Generated.Operations.Count; index++)
            if (card.Generated.Operations[index].Template == "F:AimMultiplier")
                __result *= Math.Max(1, card.OperationAmount(index));
    }
}
