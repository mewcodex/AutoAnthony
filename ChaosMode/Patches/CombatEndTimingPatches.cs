using System.Diagnostics;
using ChaosCardGenerator;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace AutoAnthony.Patches;

internal static class CombatEndTiming
{
    internal static Stopwatch Start() => Stopwatch.StartNew();

    internal static void Finish(Stopwatch stopwatch, string stage, bool always = true)
    {
        stopwatch.Stop();
        if (ChaosRunDefinitions.IsRunActive && (always || stopwatch.ElapsedMilliseconds >= 25))
            Log.Info($"[AutoAnthony.Perf] {stage}: {stopwatch.ElapsedMilliseconds} ms");
    }

    internal static async Task Observe(Task task, Stopwatch stopwatch, string stage, bool always = true)
    {
        try
        {
            await task;
        }
        finally
        {
            Finish(stopwatch, stage, always);
        }
    }

    internal static async Task<T> Observe<T>(Task<T> task, Stopwatch stopwatch, string stage, bool always = true)
    {
        try
        {
            return await task;
        }
        finally
        {
            Finish(stopwatch, stage, always);
        }
    }
}

// The base game performs several unrelated operations between the final death and the reward screen. Keep these
// probes lightweight and log one duration per end-of-combat stage so a reported hitch can be assigned to the death
// hook chain, replay writing, run serialization, disk I/O, progress saving, or reward generation.
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDeath))]
internal static class AfterDeathTimingPatch
{
    private static void Prefix(out Stopwatch __state) => __state = CombatEndTiming.Start();

    private static void Postfix(ref Task __result, Stopwatch __state, Creature creature) =>
        __result = CombatEndTiming.Observe(__result, __state, $"AfterDeath hooks ({creature.ModelId})", always: false);
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCombatEnd))]
internal static class AfterCombatEndTimingPatch
{
    private static void Prefix(out Stopwatch __state) => __state = CombatEndTiming.Start();
    private static void Postfix(ref Task __result, Stopwatch __state) =>
        __result = CombatEndTiming.Observe(__result, __state, "AfterCombatEnd hooks");
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCombatVictory))]
internal static class AfterCombatVictoryTimingPatch
{
    private static void Prefix(out Stopwatch __state) => __state = CombatEndTiming.Start();
    private static void Postfix(ref Task __result, Stopwatch __state) =>
        __result = CombatEndTiming.Observe(__result, __state, "AfterCombatVictory hooks");
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.WriteReplay))]
internal static class WriteReplayTimingPatch
{
    private static void Prefix(out Stopwatch __state) => __state = CombatEndTiming.Start();
    private static void Postfix(Stopwatch __state) => CombatEndTiming.Finish(__state, "WriteReplay");
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveRun), typeof(AbstractRoom), typeof(bool))]
internal static class SaveRunTimingPatch
{
    private static void Prefix(out Stopwatch __state) => __state = CombatEndTiming.Start();
    private static void Postfix(ref Task __result, Stopwatch __state) =>
        __result = CombatEndTiming.Observe(__result, __state, "SaveRun");
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveProgressFile))]
internal static class SaveProgressTimingPatch
{
    private static void Prefix(out Stopwatch __state) => __state = CombatEndTiming.Start();
    private static void Postfix(Stopwatch __state) => CombatEndTiming.Finish(__state, "SaveProgressFile");
}

[HarmonyPatch(typeof(RewardsCmd), nameof(RewardsCmd.GenerateForRoomEnd))]
internal static class GenerateRoomRewardsTimingPatch
{
    private static void Prefix(out Stopwatch __state) => __state = CombatEndTiming.Start();
    private static void Postfix(ref Task<RewardsSet> __result, Stopwatch __state, Player player) =>
        __result = CombatEndTiming.Observe(__result, __state, $"GenerateForRoomEnd (player {player.NetId})");
}
