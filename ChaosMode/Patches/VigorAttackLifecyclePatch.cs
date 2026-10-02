using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace AutoAnthony.Patches;

// On-hit Vigor can keep the native instance alive after its starting stacks are consumed.
// v111 leaves that instance bound to the completed command, preventing future attacks from using it.
[HarmonyPatch(typeof(VigorPower), nameof(VigorPower.AfterAttack))]
internal static class VigorAttackLifecyclePatch
{
    private static readonly FieldInfo InternalData = AccessTools.Field(typeof(PowerModel), "_internalData");
    private static readonly FieldInfo BoundCommand = AccessTools.Field(
        typeof(VigorPower).GetNestedType("Data", BindingFlags.NonPublic), "commandToModify");

    private static void Postfix(VigorPower __instance, AttackCommand command, ref Task __result)
    {
        __result = Finish(__result, __instance, command);
    }

    internal static async Task Finish(Task original, VigorPower power, AttackCommand command)
    {
        // Preserve native consumption and its async ordering. Never release a different/nested attack.
        await original;
        var data = InternalData.GetValue(power);
        if (data is not null && ReferenceEquals(BoundCommand.GetValue(data), command))
            BoundCommand.SetValue(data, null);
    }
}
