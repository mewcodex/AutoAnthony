using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Hooks;

namespace AutoAnthony
{
    /// <summary>
    /// Bounds generated ability chains within one root combat timepoint. A timepoint begins with player input (including
    /// resuming an action after a choice), a side's turn start/end, or one monster action. Commands emitted by an ability
    /// do not advance the epoch, so recursive Orb/card/status chains share the same allowance and eventually terminate.
    /// </summary>
    internal static class ChaosAbilityTriggerLimiter
    {
        internal const int MaximumActivationsPerEffect = 20;
        private static long _currentTimepoint;

        internal static long CurrentTimepoint => Interlocked.Read(ref _currentTimepoint);

        internal static long BeginTimepoint() => Interlocked.Increment(ref _currentTimepoint);

        internal static bool TryConsume(int triggerIndex, long currentTimepoint, ref long observedTimepoint,
            IDictionary<int, int> activations, ISet<int>? reportedLimits = null)
        {
            if (observedTimepoint != currentTimepoint)
            {
                observedTimepoint = currentTimepoint;
                activations.Clear();
                reportedLimits?.Clear();
            }

            var current = activations.TryGetValue(triggerIndex, out var existing) ? existing : 0;
            if (current >= MaximumActivationsPerEffect) return false;

            activations[triggerIndex] = current + 1;
            return true;
        }
    }
}

namespace AutoAnthony.Patches
{
    // GameAction is the engine's input boundary: playing a card, drinking a potion, ending the turn and resuming after a
    // synchronized player choice all pass through Execute. Automatic cards/commands do not, and therefore remain inside
    // the timepoint which created them.
    [HarmonyPatch(typeof(GameAction), nameof(GameAction.Execute))]
    internal static class ChaosAbilityTriggerGameActionBoundaryPatch
    {
        private static void Prefix(GameAction __instance)
        {
            if (!CombatManager.Instance.IsInProgress
                || __instance.ActionType is not (GameActionType.Combat or GameActionType.CombatPlayPhaseOnly))
                return;
            ChaosAbilityTriggerLimiter.BeginTimepoint();
        }
    }

    // BeforeSideTurnStart covers the entire start-of-turn sequence, including the later AfterSideTurnStart hooks and Orb
    // passives. The end sequence receives its own epoch, so end of one turn and start of the next are distinct timepoints.
    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeSideTurnStart))]
    internal static class ChaosAbilityTriggerTurnStartBoundaryPatch
    {
        private static void Prefix() => ChaosAbilityTriggerLimiter.BeginTimepoint();
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeSideTurnEnd))]
    internal static class ChaosAbilityTriggerTurnEndBoundaryPatch
    {
        private static void Prefix() => ChaosAbilityTriggerLimiter.BeginTimepoint();
    }

    // Enemy moves are not GameActions. Treat each monster's move as one root timepoint while keeping all hits and effects
    // produced by that move in the same bounded chain.
    [HarmonyPatch(typeof(Creature), nameof(Creature.TakeTurn))]
    internal static class ChaosAbilityTriggerMonsterActionBoundaryPatch
    {
        private static void Prefix() => ChaosAbilityTriggerLimiter.BeginTimepoint();
    }
}
