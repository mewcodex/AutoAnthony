using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace AutoAnthony.Patches;

/// <summary>
/// Darv is the only vanilla Ancient that prepares Dusty Tome (and therefore its referenced card) while building
/// the initial relic choices. Keep diagnostics at that boundary: option generation finishes before the dialogue UI
/// is set up, so its result cleanly distinguishes model/setup faults from later layout or input faults.
/// </summary>
[HarmonyPatch(typeof(Darv), "GenerateInitialOptions")]
internal static class DarvOptionGenerationAuditPatch
{
    private static void Postfix(Darv __instance, IReadOnlyList<EventOption>? __result)
    {
        if (!ChaosRunDefinitions.IsRunActive) return;
        var owner = __instance.Owner;
        if (owner is null)
        {
            Log.Error("[AutoAnthony] Darv prepared relic options without an owning player.");
            return;
        }
        if (__result is null)
        {
            // A replacement prefix supplied by another mod can accidentally skip vanilla without assigning the
            // result. Do not turn that foreign fault into a second exception in this diagnostic postfix.
            Log.Error($"[AutoAnthony] Darv option generation returned null for {owner.Character.Id} "
                + $"(player {owner.NetId}); inspect other GenerateInitialOptions patches.");
            return;
        }
        var relics = string.Join(", ", __result.Select(option => option.Relic?.Id.ToString() ?? "<no relic>"));
        Log.Info($"[AutoAnthony] Darv prepared {__result.Count} relic options for "
            + $"{owner.Character.Id} (player {owner.NetId}): {relics}.");
        if (__result.Count == 0)
            Log.Error("[AutoAnthony] Darv produced no relic options; the Ancient dialogue cannot reveal a choice.");
    }

    private static Exception? Finalizer(Darv __instance, Exception? __exception)
    {
        if (__exception is not null && ChaosRunDefinitions.IsRunActive)
            Log.Error($"[AutoAnthony] Darv relic-option generation failed for "
                + $"{__instance.Owner?.Character.Id}: {__exception}");
        return __exception;
    }
}

[HarmonyPatch(typeof(Orobas), "GenerateInitialOptions")]
internal static class OrobasOptionGenerationAuditPatch
{
    private static void Postfix(Orobas __instance, IReadOnlyList<EventOption>? __result)
    {
        if (!ChaosRunDefinitions.IsRunActive) return;
        var owner = __instance.Owner;
        if (__result is null)
        {
            Log.Error($"[AutoAnthony] Orobas option generation returned null for {owner?.Character.Id}.");
            return;
        }
        var relics = string.Join(", ", __result.Select(option => option.Relic?.Id.ToString() ?? "<no relic>"));
        Log.Info($"[AutoAnthony] Orobas prepared {__result.Count} relic options for "
            + $"{owner?.Character.Id} (player {owner?.NetId}): {relics}.");
        if (__result.Count == 0)
            Log.Error("[AutoAnthony] Orobas produced no relic options; the Ancient dialogue cannot reveal a choice.");
    }

    private static Exception? Finalizer(Orobas __instance, Exception? __exception)
    {
        if (__exception is not null && ChaosRunDefinitions.IsRunActive)
            Log.Error($"[AutoAnthony] Orobas relic-option generation failed for "
                + $"{__instance.Owner?.Character.Id}: {__exception}");
        return __exception;
    }
}

/// <summary>
/// The Ancient dialogue is installed before SetOptions and enabled only by the following OnSetupComplete call.
/// Logging this synchronous boundary makes any future "first line cannot advance" report actionable without
/// changing vanilla input state or attempting to continue with a partially constructed option list.
/// </summary>
[HarmonyPatch(typeof(NEventRoom), "SetOptions")]
internal static class AncientRelicOptionUiAuditPatch
{
    private static Exception? Finalizer(EventModel eventModel, Exception? __exception)
    {
        var eventName = eventModel switch
        {
            Darv => "Darv",
            Orobas => "Orobas",
            _ => null
        };
        if (__exception is not null && eventName is not null && ChaosRunDefinitions.IsRunActive)
            Log.Error($"[AutoAnthony] {eventName} option UI construction failed before Ancient dialogue activation: {__exception}");
        return __exception;
    }
}
