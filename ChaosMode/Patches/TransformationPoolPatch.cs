using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Unlocks;

namespace AutoAnthony.Patches;

[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.GetDefaultTransformationOptions))]
internal static class ChaosDefaultTransformationPoolPatch
{
    // Native transformations start from the source card's Pool, not Character.CardPool. A retained vanilla
    // starter therefore bypasses the character-pool patches. Rebind only this default pool lookup: keep the
    // native rarity, unlock, multiplayer and combat filters, preview candidates and RNG unchanged. Explicit
    // replacements/options (including derivative transformations) never enter this method.
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var lookup = AccessTools.Method(typeof(CardPoolModel), nameof(CardPoolModel.GetUnlockedCards));
        var replacement = AccessTools.Method(typeof(ChaosDefaultTransformationPoolPatch), nameof(GetActivePoolCards));
        var replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(lookup))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1)
            throw new InvalidOperationException($"Expected one default transformation pool lookup, found {replaced}.");
    }

    internal static IEnumerable<CardModel> GetActivePoolCards(CardPoolModel pool, UnlockState unlockState,
        CardMultiplayerConstraint multiplayerConstraint) =>
        CharacterPoolPatchRouting.ActivePoolFor(pool).GetUnlockedCards(unlockState, multiplayerConstraint);
}
