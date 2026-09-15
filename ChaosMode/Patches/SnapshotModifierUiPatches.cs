using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace AutoAnthony.Patches;

/// <summary>
/// The generated-pool snapshot is a transport record, not a visible run modifier. Daily-load UI has exactly three
/// modifier rows, so treating the snapshot as a fourth daily modifier throws after the Steam lobby has already been
/// created and is reported to the player as a room-creation failure. Temporarily hide only our marker while vanilla
/// builds modifier UI; restore it immediately so joining peers still receive the authoritative pool snapshot.
/// </summary>
internal static class SnapshotModifierUiFilter
{
    internal sealed class State(List<SerializableModifier> modifiers,
        IReadOnlyList<(int Index, SerializableModifier Modifier)> removed)
    {
        internal List<SerializableModifier> Modifiers { get; } = modifiers;
        internal IReadOnlyList<(int Index, SerializableModifier Modifier)> Removed { get; } = removed;
        internal bool Restored { get; set; }
    }

    internal static State? Hide(SerializableRun? run)
    {
        if (run is null) return null;
        var snapshotId = ModelDb.Modifier<ChaosPoolSnapshotModifier>().Id;
        var balanceId = ModelDb.Modifier<BalanceAdjustmentModifier>().Id;
        var removed = run.Modifiers.Select((modifier, index) => (Index: index, Modifier: modifier))
            .Where(item => item.Modifier.Id == snapshotId || item.Modifier.Id == balanceId).ToArray();
        if (removed.Length == 0) return null;
        for (var index = removed.Length - 1; index >= 0; index--)
            run.Modifiers.RemoveAt(removed[index].Index);
        return new State(run.Modifiers, removed);
    }

    internal static void Restore(State? state)
    {
        if (state is null || state.Restored) return;
        foreach (var item in state.Removed)
            state.Modifiers.Insert(Math.Min(item.Index, state.Modifiers.Count), item.Modifier);
        state.Restored = true;
    }

    internal static void Audit()
    {
        var snapshotId = ModelDb.Modifier<ChaosPoolSnapshotModifier>().Id;
        var balanceId = ModelDb.Modifier<BalanceAdjustmentModifier>().Id;
        var visible = new SerializableModifier();
        var marker = new SerializableModifier { Id = snapshotId };
        var balance = new SerializableModifier { Id = balanceId };
        var run = new SerializableRun { Modifiers = [visible, marker, balance] };
        var state = Hide(run);
        if (state is null || run.Modifiers.Count != 1 || !ReferenceEquals(run.Modifiers[0], visible))
            throw new InvalidOperationException("Internal modifier UI filter did not hide only internal markers.");
        Restore(state);
        Restore(state);
        if (run.Modifiers.Count != 3 || !ReferenceEquals(run.Modifiers[0], visible)
                                     || !ReferenceEquals(run.Modifiers[1], marker)
                                     || !ReferenceEquals(run.Modifiers[2], balance))
            throw new InvalidOperationException("Internal modifier UI filter did not restore marker order exactly once.");
    }
}

/// <summary>
/// Balance adjustments need run-lifetime hooks and saved fields, but they are a user setting rather than a custom
/// run modifier. Hide the internal model while vanilla builds the top bar and Neow's opening options; keeping it in
/// the live model set outside those synchronous calls preserves hook dispatch and save serialization.
/// </summary>
internal static class LiveInternalModifierUiFilter
{
    private static readonly System.Reflection.PropertyInfo ModifiersProperty =
        AccessTools.Property(typeof(RunState), nameof(RunState.Modifiers));

    internal sealed class State(RunState run, IReadOnlyList<ModifierModel> modifiers)
    {
        internal RunState Run { get; } = run;
        internal IReadOnlyList<ModifierModel> Modifiers { get; } = modifiers;
        internal bool Restored { get; set; }
    }

    internal static State? Hide(IRunState? run)
    {
        if (run is not RunState concrete) return null;
        var filtered = concrete.Modifiers.Where(modifier => modifier is not BalanceAdjustmentModifier
                                                             and not ChaosPoolSnapshotModifier).ToArray();
        if (filtered.Length == concrete.Modifiers.Count) return null;
        var state = new State(concrete, concrete.Modifiers);
        ModifiersProperty.SetValue(concrete, filtered);
        return state;
    }

    internal static void Restore(State? state)
    {
        if (state is null || state.Restored) return;
        ModifiersProperty.SetValue(state.Run, state.Modifiers);
        state.Restored = true;
    }
}

[HarmonyPatch(typeof(Neow), "get_InitialDescription")]
internal static class NeowInitialDescriptionInternalModifierPatch
{
    private static void Prefix(Neow __instance, out LiveInternalModifierUiFilter.State? __state) =>
        __state = LiveInternalModifierUiFilter.Hide(__instance.Owner?.RunState);

    private static void Postfix(LiveInternalModifierUiFilter.State? __state) =>
        LiveInternalModifierUiFilter.Restore(__state);

    private static Exception? Finalizer(Exception? __exception, LiveInternalModifierUiFilter.State? __state)
    {
        LiveInternalModifierUiFilter.Restore(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(Neow), "GenerateInitialOptions")]
internal static class NeowOptionsInternalModifierPatch
{
    private static void Prefix(Neow __instance, out LiveInternalModifierUiFilter.State? __state) =>
        __state = LiveInternalModifierUiFilter.Hide(__instance.Owner?.RunState);

    private static void Postfix(LiveInternalModifierUiFilter.State? __state) =>
        LiveInternalModifierUiFilter.Restore(__state);

    private static Exception? Finalizer(Exception? __exception, LiveInternalModifierUiFilter.State? __state)
    {
        LiveInternalModifierUiFilter.Restore(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(NTopBar), nameof(NTopBar.Initialize))]
internal static class TopBarInternalModifierPatch
{
    private static void Prefix(IRunState runState, out LiveInternalModifierUiFilter.State? __state) =>
        __state = LiveInternalModifierUiFilter.Hide(runState);

    private static void Postfix(LiveInternalModifierUiFilter.State? __state) =>
        LiveInternalModifierUiFilter.Restore(__state);

    private static Exception? Finalizer(Exception? __exception, LiveInternalModifierUiFilter.State? __state)
    {
        LiveInternalModifierUiFilter.Restore(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(NDailyRunLoadScreen), "InitializeDisplay")]
internal static class DailyRunSnapshotModifierUiPatch
{
    private static readonly System.Reflection.FieldInfo LobbyField =
        AccessTools.Field(typeof(NDailyRunLoadScreen), "_lobby");

    private static void Prefix(NDailyRunLoadScreen __instance, out SnapshotModifierUiFilter.State? __state) =>
        __state = SnapshotModifierUiFilter.Hide((LobbyField.GetValue(__instance) as LoadRunLobby)?.Run);

    private static void Postfix(SnapshotModifierUiFilter.State? __state) =>
        SnapshotModifierUiFilter.Restore(__state);

    private static Exception? Finalizer(Exception? __exception, SnapshotModifierUiFilter.State? __state)
    {
        SnapshotModifierUiFilter.Restore(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(NCustomRunLoadScreen), nameof(NCustomRunLoadScreen.OnSubmenuOpened))]
internal static class CustomRunSnapshotModifierUiPatch
{
    private static readonly System.Reflection.FieldInfo LobbyField =
        AccessTools.Field(typeof(NCustomRunLoadScreen), "_lobby");

    private static void Prefix(NCustomRunLoadScreen __instance, out SnapshotModifierUiFilter.State? __state) =>
        __state = SnapshotModifierUiFilter.Hide((LobbyField.GetValue(__instance) as LoadRunLobby)?.Run);

    private static void Postfix(SnapshotModifierUiFilter.State? __state) =>
        SnapshotModifierUiFilter.Restore(__state);

    private static Exception? Finalizer(Exception? __exception, SnapshotModifierUiFilter.State? __state)
    {
        SnapshotModifierUiFilter.Restore(__state);
        return __exception;
    }
}
