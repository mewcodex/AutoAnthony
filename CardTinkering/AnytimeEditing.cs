using AutoAnthony;
#if FREEFORM_API
using ChaosCardGenerator;
#endif
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AutoAnthonyCardTinkering;

internal static class CardTinkeringFeatureGate
{
    internal static bool BaseEnabled
    {
        get
        {
            if (ChimeraCompatibility.IsSuppressed()) return false;
            var manager = RunManager.Instance;
            // AutoAnthonySettingsApi.Current is explicitly the local preference. In multiplayer the host-authored
            // generated-pool activation is the shared run setting; using each client's checkbox here would generate
            // different maps before the players even reach the Training Room.
            if (manager.IsInProgress && manager.NetService.Type.IsMultiplayer())
                return ChaosRunDefinitions.IsRunActive;
            return AutoAnthonySettingsApi.Current.Enabled;
        }
    }
#if FREEFORM_API
    internal static bool SupportsFreeformApi
    {
        get
        {
            var assembly = typeof(ChaosCardModel).Assembly;
            if (assembly.GetType("AutoAnthony.AutoAnthonyFreeformCardApi", throwOnError: false) is null) return false;
            return typeof(CardTinkeringApi).GetField("ApiVersion")?.GetRawConstantValue() is int version
                   && version >= 5;
        }
    }

    internal static bool SupportsNativeCardApi
    {
        get
        {
            var type = typeof(ChaosCardModel).Assembly.GetType(
                "AutoAnthony.AutoAnthonyNativeCardApi", throwOnError: false);
            return type?.GetField("ApiVersion")?.GetRawConstantValue() is int version && version >= 2;
        }
    }
#endif

    internal static bool CanEditAnytime(RunState? run)
    {
        if (ChimeraCompatibility.IsSuppressed(run)) return false;
        var settings = AutoAnthonySettingsApi.Current;
        // Training Rooms commit through EventSynchronizer. The deck-screen entry has no deterministic event/action
        // boundary, so exposing it in multiplayer would mutate only the local peer's deck.
        if (!settings.Enabled || !settings.AnytimeCardEditing || run is not { Players.Count: 1 })
            return false;
        if (CombatManager.Instance.IsInProgress)
            return false;
        // A CombatRoom is also active during encounter setup. It only becomes pre-finished before the
        // reward flow starts, so this admits rewards without exposing the editor during combat loading.
        if (run.CurrentRoom is CombatRoom { IsPreFinished: false })
            return false;
        return run.Players.Single().Deck.Cards.Any(card => card is ChaosCardModel
#if FREEFORM_API
            || SupportsNativeCardApi && AutoAnthonyNativeCardApi.CanMaterializeExact(card)
#endif
        );
    }

#if FREEFORM_API
    // Creating cards can also target live combat piles; keep this explicitly single-player until it has its own
    // action-queue protocol instead of pretending that local pile mutation is multiplayer-safe.
    internal static bool CanFreeformEdit(RunState? run) => !ChimeraCompatibility.IsSuppressed(run)
        && SupportsFreeformApi && AutoAnthonySettingsApi.Current.Enabled
        && FreeformSettings.Enabled && run is { Players.Count: 1 };
#endif
}

[HarmonyPatch(typeof(NDeckViewScreen), nameof(NDeckViewScreen._Ready))]
internal static class AnytimeDeckEditorPatch
{
    private const string ButtonName = "AutoAnthonyAnytimeEditButton";
#if FREEFORM_API
    private const string FreeformButtonName = "AutoAnthonyFreeformEditButton";
#endif
    private const string SortButtonScene =
        "res://scenes/screens/deck_view_screen/deck_view_sort_button.tscn";

    private static void Postfix(NDeckViewScreen __instance)
        => RefreshButton(__instance);

    internal static void RefreshButton(NDeckViewScreen screen)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree()) return;
            var existing = screen.GetNodeOrNull<NCardViewSortButton>(ButtonName);
#if FREEFORM_API
            var freeform = screen.GetNodeOrNull<NCardViewSortButton>(FreeformButtonName);
#endif
            var run = RunManager.Instance.DebugOnlyGetState();
            if (!CardTinkeringFeatureGate.CanEditAnytime(run))
            {
                existing?.QueueFree();
            }
            else if (existing is null)
                CreateButton(screen, ButtonName, -324, -24,
                    new LocString("main_menu_ui", "AUTO_ANTHONY_ANYTIME_CARD_EDITING").GetRawText(),
                    new LocString("main_menu_ui", "AUTO_ANTHONY_ANYTIME_CARD_EDITING_DESCRIPTION").GetRawText(),
                    () => OpenEditor(screen));

#if FREEFORM_API
            if (!CardTinkeringFeatureGate.CanFreeformEdit(run))
            {
                freeform?.QueueFree();
                return;
            }
            if (freeform is null)
                CreateButton(screen, FreeformButtonName,
                    CardTinkeringFeatureGate.CanEditAnytime(run) ? -638 : -324,
                    CardTinkeringFeatureGate.CanEditAnytime(run) ? -338 : -24,
                    TinkeringText.Localize("创造卡牌", "Create Cards"),
                    TinkeringText.Localize("从完整组件目录组装并创造卡牌",
                        "Assemble and create cards from the complete component catalog."),
                    () => OpenFreeformEditor(screen));
#endif
        }
        catch (Exception exception)
        {
            Log.Error($"[CardTinkering] Failed to add the deck-screen editor entries: {exception}");
        }
    }

    private static void CreateButton(NDeckViewScreen screen, string name, float left, float right,
        string label, string tooltip, Action action)
    {
            var alphabetical = screen.GetNodeOrNull<NCardViewSortButton>("%AlphabeticalSorter")
                               ?? throw new InvalidOperationException(
                                   "The native deck screen has no AlphabeticalSorter control.");

            // Duplicating a nested scene instance loses the local unique-name owner used by NCardViewSortButton's
            // %ButtonImage/%Label lookups. Instantiate the same native scene so its ownership graph remains intact.
            var button = PreloadManager.Cache.GetScene(SortButtonScene)
                .Instantiate<NCardViewSortButton>(PackedScene.GenEditState.Disabled);
            button.Name = name;
            button.CustomMinimumSize = new Vector2(300, 48);
            screen.AddChild(button);
            // A fifth 250px sort tab can be clipped completely at narrower aspect ratios. The bottom-right corner is
            // deliberately reserved by the native deck screen (upgrade toggle left, info text center), so keep the
            // editor entry there as a large, stable action instead of extending the sorting row off-screen.
            button.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
            button.OffsetLeft = left;
            button.OffsetTop = -76;
            button.OffsetRight = right;
            button.OffsetBottom = -28;
            button.SetLabel(label);
            button.TooltipText = tooltip;
            button.GetNodeOrNull<TextureRect>("%Image")?.Hide();
            button.Connect(NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ => action()));

            button.FocusNeighborTop = screen.DefaultFocusedControl?.GetPath() ?? alphabetical.GetPath();
            button.FocusNeighborBottom = button.GetPath();
            button.FocusNeighborLeft = alphabetical.GetPath();
            button.FocusNeighborRight = button.GetPath();
            alphabetical.FocusNeighborRight = button.GetPath();
            Log.Info($"[CardTinkering] Added deck-screen action {name}.");
    }

    private static void OpenEditor(NDeckViewScreen screen)
    {
        var run = RunManager.Instance.DebugOnlyGetState();
        if (!GodotObject.IsInstanceValid(screen) || !CardTinkeringFeatureGate.CanEditAnytime(run)) return;
        NCapstoneContainer.Instance?.Close();
        Callable.From(() =>
        {
            if (CardTinkeringFeatureGate.CanEditAnytime(run)) TrainingEventUi.AttachAnytime(run!);
        }).CallDeferred();
    }

#if FREEFORM_API
    private static void OpenFreeformEditor(NDeckViewScreen screen)
    {
        var run = RunManager.Instance.DebugOnlyGetState();
        if (!GodotObject.IsInstanceValid(screen) || !CardTinkeringFeatureGate.CanFreeformEdit(run)) return;
        NCapstoneContainer.Instance?.Close();
        Callable.From(() =>
        {
            if (CardTinkeringFeatureGate.CanFreeformEdit(run)) TrainingEventUi.AttachFreeform(run!);
        }).CallDeferred();
    }
#endif
}

// NDeckViewScreen is normally instantiated for every opening, but other mods may retain and reopen one. Refresh once
// more after the native ShowScreen flow has attached it to the capstone so both lifecycles get the same visible entry.
[HarmonyPatch(typeof(NDeckViewScreen), nameof(NDeckViewScreen.ShowScreen))]
internal static class AnytimeDeckEditorShowScreenPatch
{
    private static void Postfix(NDeckViewScreen? __result)
    {
        if (__result is null || !GodotObject.IsInstanceValid(__result)) return;
        var screen = __result;
        Callable.From(() => AnytimeDeckEditorPatch.RefreshButton(screen)).CallDeferred();
    }
}
