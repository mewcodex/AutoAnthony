using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace AutoAnthony.Patches;

internal static class ChaosPowerVisuals
{
    private sealed record Binding(ChaosCardDefinition Definition);
    private static readonly ConditionalWeakTable<PowerModel, Binding> Bindings = new();

    internal static void Bind(PowerModel power, ChaosCardDefinition definition)
    {
        Bindings.Remove(power);
        Bindings.Add(power, new Binding(definition));
    }

    internal static bool TryGetDefinition(PowerModel power, out ChaosCardDefinition definition)
    {
        if (power is ChaosCompositePower composite)
        {
            definition = composite.Definition;
            return true;
        }
        if (Bindings.TryGetValue(power, out var binding))
        {
            definition = binding.Definition;
            return true;
        }
        var template = power switch
        {
            AccelerantPower => "A:rulePoisonExtraTriggers",
            AccuracyPower => "A:ruleShivBonusDamage",
            EnvenomPower => "A:ruleUnblockedAttackPoison",
            FanOfKnivesPower => "A:ruleShivsHitAll",
            MasterPlannerPower => "A:rulePlayedSkillsGainSly",
            PhantomBladesPower => "A:ruleShivsRetainAndFirstBonus",
            TrackingPower => "A:ruleWeakEnemiesTakeMoreAttackDamage",
            WellLaidPlansPower => "A:ruleRetainHand",
            _ => null
        };
        if (template is not null)
        {
            var inferred = ChaosRunDefinitions.ActiveCharacters
                .SelectMany(ChaosRunDefinitions.GetCards)
                .FirstOrDefault(card => card.Card.Operations.Any(operation => operation.Template == template));
            if (inferred is not null)
            {
                definition = inferred;
                return true;
            }
        }
        definition = null!;
        return false;
    }
}

[HarmonyPatch(typeof(NPower), "Reload")]
internal static class ChaosPowerVisualPatch
{
    private static readonly FieldInfo ModelField = AccessTools.Field(typeof(NPower), "_model");
    private static readonly FieldInfo IconField = AccessTools.Field(typeof(NPower), "_icon");
    private static readonly FieldInfo FlashField = AccessTools.Field(typeof(NPower), "_powerFlash");

    private static void Postfix(NPower __instance)
    {
        // NPower._Ready calls Reload before a multiplayer creature's power nodes have necessarily received their
        // models. The public Model getter deliberately throws in that state. Vanilla Reload tolerates the null
        // backing field, so this visual-only postfix must do the same instead of emitting several exceptions for
        // every player/enemy whenever a combat scene is reconstructed.
        if (ModelField.GetValue(__instance) is not PowerModel power
            || !ChaosPowerVisuals.TryGetDefinition(power, out var definition)) return;
        if (IconField.GetValue(__instance) is not TextureRect icon) return;
        if (ResourceLoader.Exists(definition.PowerIconPath))
            icon.Texture = ResourceLoader.Load<Texture2D>(definition.PowerIconPath);
        if (FlashField.GetValue(__instance) is CpuParticles2D flash && ResourceLoader.Exists(definition.PowerBigIconPath))
            flash.Texture = ResourceLoader.Load<Texture2D>(definition.PowerBigIconPath);
    }
}

[HarmonyPatch(typeof(PowerModel), nameof(PowerModel.PackedIconPath), MethodType.Getter)]
internal static class ChaosPowerPackedIconPatch
{
    private static void Postfix(PowerModel __instance, ref string __result)
    {
        if (ChaosPowerVisuals.TryGetDefinition(__instance, out var definition)) __result = definition.PowerIconPath;
    }
}

[HarmonyPatch(typeof(PowerModel), nameof(PowerModel.IconPath), MethodType.Getter)]
internal static class ChaosPowerIconPathPatch
{
    private static void Postfix(PowerModel __instance, ref string __result)
    {
        if (ChaosPowerVisuals.TryGetDefinition(__instance, out var definition)) __result = definition.PowerIconPath;
    }
}

[HarmonyPatch(typeof(PowerModel), nameof(PowerModel.Icon), MethodType.Getter)]
internal static class ChaosPowerIconPatch
{
    private static bool Prefix(PowerModel __instance, ref Texture2D __result)
    {
        if (!ChaosPowerVisuals.TryGetDefinition(__instance, out var definition)) return true;
        __result = ResourceLoader.Load<Texture2D>(definition.PowerIconPath, null, ResourceLoader.CacheMode.Reuse);
        return false;
    }
}

[HarmonyPatch(typeof(PowerModel), nameof(PowerModel.ResolvedBigIconPath), MethodType.Getter)]
internal static class ChaosPowerBigIconPathPatch
{
    private static void Postfix(PowerModel __instance, ref string __result)
    {
        if (ChaosPowerVisuals.TryGetDefinition(__instance, out var definition)) __result = definition.PowerBigIconPath;
    }
}

[HarmonyPatch(typeof(PowerModel), nameof(PowerModel.BigIcon), MethodType.Getter)]
internal static class ChaosPowerBigIconPatch
{
    private static bool Prefix(PowerModel __instance, ref Texture2D __result)
    {
        if (!ChaosPowerVisuals.TryGetDefinition(__instance, out var definition)) return true;
        __result = ResourceLoader.Load<Texture2D>(definition.PowerBigIconPath, null, ResourceLoader.CacheMode.Reuse);
        return false;
    }
}
