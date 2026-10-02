using HarmonyLib;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace AutoAnthony.Patches;

// Include native cards: a native Defend can trigger generated block -> draw -> choose effects too.
// This does not manufacture a HookPlayerChoiceContext or enqueue a second action behind the action awaiting it.
[HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
internal static class ChaosCardPlayChoiceContextScopePatch
{
    private static void Prefix(CardModel __instance, ref PlayerChoiceContext choiceContext,
        out ChaosChoiceContext.Scope __state)
    {
        choiceContext = ChaosChoiceContext.Resolve(choiceContext, __instance.Owner.NetId);
        __state = ChaosChoiceContext.Enter(choiceContext);
    }

    private static void Postfix(ref Task __result, ChaosChoiceContext.Scope __state)
    {
        __state.RestoreCaller();
        __result = ChaosChoiceContext.Complete(__result, __state);
    }

    private static void Finalizer(Exception? __exception, ChaosChoiceContext.Scope? __state)
    {
        if (__exception is not null) __state?.Dispose();
    }
}
