using HarmonyLib;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace AutoAnthony.Patches;

/// <summary>
/// Self-exhaust effects must follow the global exhaust dispatch. Depending on the pile transition,
/// the exhausted card is not a reliable member of IterateHookListeners, so listening only through
/// CardModel.AfterCardExhausted can silently miss the card that caused the event.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardExhausted))]
internal static class SelfExhaustTriggerPatch
{
    private static void Postfix(PlayerChoiceContext choiceContext, CardModel card, ref Task __result)
    {
        if (card is ChaosCardModel chaosCard)
            __result = AfterOriginal(__result, chaosCard, choiceContext);
    }

    private static async Task AfterOriginal(Task original, ChaosCardModel card, PlayerChoiceContext choiceContext)
    {
        await original;
        await card.FireSelfExhaustTriggers(choiceContext);
    }
}
