using System.Reflection;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;

namespace AutoAnthony
{
    /// <summary>
    /// Tracks the precise interval in which the native Orb channel command has evoked an Orb to make room but has not
    /// yet enqueued its own Orb. AfterOrbEvoked hooks run inside that interval. A generated channel effect must leave
    /// the reserved vacancy open; otherwise the suspended outer command resumes into OrbQueue.TryEnqueue while the
    /// queue is full and faults the entire card action.
    /// </summary>
    internal static class ChaosOrbChannelContext
    {
        internal sealed class Frame(Frame? parent, bool pendingEnqueue)
        {
            internal Frame? Parent { get; } = parent;
            internal bool PendingEnqueue { get; set; } = pendingEnqueue;
        }

        private static readonly AsyncLocal<Frame?> CurrentFrame = new();

        internal static bool HasPendingEnqueue => CurrentFrame.Value?.PendingEnqueue == true;

        internal static Frame Push(bool pendingEnqueue)
        {
            var frame = new Frame(CurrentFrame.Value, pendingEnqueue);
            CurrentFrame.Value = frame;
            return frame;
        }

        internal static void Pop(Frame frame) => CurrentFrame.Value = frame.Parent;

        internal static Frame? Capture() => CurrentFrame.Value;

        internal static void MarkEnqueued(Frame? frame)
        {
            if (frame is not null) frame.PendingEnqueue = false;
        }
    }
}

namespace AutoAnthony.Patches
{
    [HarmonyPatch]
    internal static class ChaosOrbChannelContextPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(OrbCmd), nameof(OrbCmd.Channel),
            [typeof(PlayerChoiceContext), typeof(OrbModel), typeof(Player)])
            ?? throw new MissingMethodException(typeof(OrbCmd).FullName, nameof(OrbCmd.Channel));

        private static void Prefix(Player player, out ChaosOrbChannelContext.Frame __state)
        {
            var queue = player.PlayerCombatState?.OrbQueue;
            __state = ChaosOrbChannelContext.Push(queue is { Capacity: > 0 }
                                                  && queue.Orbs.Count >= queue.Capacity);
        }

        // The async method has already captured the frame in its ExecutionContext before this synchronous postfix
        // restores the caller. Nested Channel calls therefore receive a child frame without leaking state afterward.
        private static void Postfix(ChaosOrbChannelContext.Frame __state) => ChaosOrbChannelContext.Pop(__state);
    }

    [HarmonyPatch(typeof(OrbQueue), nameof(OrbQueue.TryEnqueue))]
    internal static class ChaosOrbEnqueueCompletionPatch
    {
        private static void Prefix(out ChaosOrbChannelContext.Frame? __state) =>
            __state = ChaosOrbChannelContext.Capture();

        private static void Postfix(ref Task<bool> __result, ChaosOrbChannelContext.Frame? __state)
        {
            if (__state is not null) __result = Observe(__result, __state);
        }

        private static async Task<bool> Observe(Task<bool> original, ChaosOrbChannelContext.Frame frame)
        {
            var enqueued = await original;
            if (enqueued) ChaosOrbChannelContext.MarkEnqueued(frame);
            return enqueued;
        }
    }
}
