using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace AutoAnthony.Patches;

[HarmonyPatch(typeof(CardModel), nameof(CardModel.ToMutable))]
internal static class GeneratedCardInitialStarCostPatch
{
    private static void Postfix(CardModel __result)
    {
        if (__result is not ChaosCardModel) return;
        // ToMutable creates a fresh card from a canonical model. The native shallow clone also copies the
        // lazy Star-cost cache, which can belong to a previous generated definition of this model slot.
        // Reinitialize only that base cache. Mutable combat clones, upgrades and legitimate temporary free
        // costs retain their native lifecycle and are deliberately not reset here.
        ChaosCardModel.ResetInitialStarCostCache(__result);
    }
}
