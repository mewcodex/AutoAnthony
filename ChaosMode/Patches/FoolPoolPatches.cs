using ChaosCardGenerator;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Entities.Players;

namespace AutoAnthony.Patches;

[HarmonyPatch(typeof(Fool), nameof(Fool.CardPool), MethodType.Getter)]
internal static class FoolPoolPatch
{
    private static bool Prefix(ref CardPoolModel __result) =>
        CharacterPoolPatchRouting.ReplacePool<ChaosFoolCardPool>(ref __result);
}

[HarmonyPatch(typeof(Fool), nameof(Fool.StartingDeck), MethodType.Getter)]
internal static class FoolStartingDeckPatch
{
    private static bool Prefix(ref IEnumerable<CardModel> __result) =>
        CharacterPoolPatchRouting.ReplaceStartingDeck(GeneratedCharacter.Fool, ref __result);
}

// Native non-Fool characters have no Arrow slots. Ultimate's shared component pool must be usable by every
// character; seed one slot at combat-state creation, without replenishing spent/impaled Arrows during play.
[HarmonyPatch(typeof(PlayerCombatState), MethodType.Constructor, [typeof(Player)])]
internal static class UltimateChaosInitialArrowPatch
{
    private static void Postfix(PlayerCombatState __instance)
    {
        if (ChaosRunDefinitions.IsRunActive && ChaosRunDefinitions.ActiveUltimateChaos && __instance.Arrows.Count == 0)
            __instance.GainArrow();
    }
}
