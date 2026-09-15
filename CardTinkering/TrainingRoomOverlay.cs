using AutoAnthony;
using ChaosCardGenerator;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using static AutoAnthonyCardTinkering.TinkeringText;

namespace AutoAnthonyCardTinkering;

internal sealed partial class TrainingRoomOverlay : ColorRect
{
    private sealed record ChipPalette(string Name, Color Background, Color Border, Color Foreground);
    private sealed record ChipVisual(PanelContainer Panel, TrainingComponent Component, int CardIndex,
        ChipPalette Palette, bool Inactive, bool IsTrigger);
    private sealed record DropVisual(Button Target, Func<string, bool> Accepts, bool HiddenWhenIdle = false);

    private static readonly Color OverlayColor = new("14111c");
    private static readonly Color PanelColor = new("272331");
    private static readonly Color PanelBorder = new("625970");
    private static readonly Color TextMain = new("fff6e2");
    private static readonly Color TextMuted = new("c9becd");
    private static readonly Color Good = new("84e39a");
    private static readonly Color Warning = new("ffbd6a");
    private static readonly Color Danger = new("ff7272");
    private static readonly Color DropBlue = new("78c8ff");
    private static readonly Color FormulaYellow = new("ffd269");
    private static readonly Color SynergyPurple = new("c58aff");
    private const int SectionPadding = 14;
    private const float ComponentColumnWidth = 362f;
    private const float NativeTopBarHeight = 104f;
    private static readonly Color InvalidCardBackground = new("54262c");
    private static readonly Color DropPulseBackground = new("25465e");
    private const double PulseAngularSpeed = 5.2d;

    private readonly TrainingSession _session;
    private readonly CardTinkeringEvent? _event;
    private readonly Dictionary<int, ChaosCardModel> _previewModels = [];
    private int _selectedCard;
    private VBoxContainer? _root;
    private HBoxContainer? _columns;
    private Control? _workbenchPanel;
    private Control? _backpackPanel;
    private Label? _status;
    private RichTextLabel? _detail;
    private Button? _save;
    private Button? _leave;
    private bool _commitInProgress;
    private Control? _confirmation;
    private bool _activated;
    private bool _mapWasEnabled;
    private bool _deckWasEnabled;
    private ScrollContainer? _deckScroll;
    private int _deckScrollPosition;
    private bool _suspendDeckScrollCapture;
    private int _deckScrollRestoreRevision;
    private readonly Dictionary<int, PanelContainer> _deckCardPanels = [];
    private NCapstoneContainer? _capstoneContainer;
    private Callable _capstoneChangedCallback;
    private readonly List<ChipVisual> _chipVisuals = [];
    private readonly List<DropVisual> _dropVisuals = [];
    private readonly HashSet<string> _draggableComponentIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _legalBeforeTargets = new(StringComparer.Ordinal);
    private readonly HashSet<Button> _legalDropTargets = [];
    private string? _draggedComponentId;
    private string? _hoveredComponentId;
    private ulong _dragStartedFrame;
    private bool _showUpgradePreview;
    private bool _useLiveModelsForInitialBuild = true;
    private ulong _lastPulseStep = ulong.MaxValue;
    private ulong _pulseEpochMsec;
    private ulong _lastIdentityRerollFrame = ulong.MaxValue;
    private Godot.Timer? _pulseTimer;

    internal TrainingRoomOverlay(TrainingSession session, CardTinkeringEvent? trainingEvent = null)
    {
        _session = session;
        _event = trainingEvent;
        Name = "AutoAnthonyCardTinkeringRoom";
        Color = OverlayColor;
        MouseFilter = MouseFilterEnum.Stop;
        // Event rooms pause their inherited processing branch. Input signals still fire, which made the pulse appear
        // to update only when hovering; Always keeps this purely visual affordance animating while the room is paused.
        ProcessMode = ProcessModeEnum.Always;
        FitBelowNativeTopBar();
        ZIndex = 0;
        ConfigureBackpackDrop(this);
    }

    internal void Activate()
    {
        if (_activated) return;
        _activated = true;
        SetProcess(true);
        EnsurePulseTimer();
        FitBelowNativeTopBar();
        var topBar = NRun.Instance?.GlobalUi.TopBar;
        _mapWasEnabled = topBar?.Map.IsEnabled == true;
        _deckWasEnabled = topBar?.Deck.IsEnabled == true;
        // Keep the native run HUD above the workbench. In particular, the deck button must remain available so
        // players can inspect the real deck while arranging a draft. TrainingSession never mutates those live
        // models until Commit, so this view cannot leak unsaved component moves into the run.
        topBar?.AnimShow();
        topBar?.Map.Disable();
        topBar?.Deck.Enable();
        _capstoneContainer = NCapstoneContainer.Instance;
        if (GodotObject.IsInstanceValid(_capstoneContainer))
        {
            _capstoneChangedCallback = Callable.From(OnCapstoneChanged);
            _capstoneContainer!.Connect(NCapstoneContainer.SignalName.Changed, _capstoneChangedCallback);
        }
        TreeExiting += Deactivate;
        Rebuild();
        Log.Info($"[CardTinkering] Clean card workbench initialized (parent={GetParent()?.Name}).");
    }

    public override void _Ready() => Activate();

    public override void _Process(double delta)
    {
        _ = delta;
        ClearFinishedDragState();
        RefreshPulseVisuals();
    }

    private void EnsurePulseTimer()
    {
        if (GodotObject.IsInstanceValid(_pulseTimer))
        {
            _pulseTimer!.Start();
            return;
        }

        // NEventRoom can suppress a scripted Control's process callback while still dispatching input to it.
        // Drive the affordance from a native Timer as well, so it continues without requiring mouse movement.
        _pulseTimer = new Godot.Timer
        {
            Name = "ComponentPulseTimer",
            WaitTime = .05d,
            OneShot = false,
            Autostart = true,
            IgnoreTimeScale = true,
            ProcessCallback = Godot.Timer.TimerProcessCallback.Idle,
            ProcessMode = ProcessModeEnum.Always
        };
        _pulseTimer.Timeout += OnPulseTimerTimeout;
        AddChild(_pulseTimer);
        _pulseTimer.Start();
    }

    private void OnPulseTimerTimeout()
    {
        if (!_activated || !IsInsideTree()) return;
        // Some event-room states suppress _Process while still dispatching drag/drop callbacks. Clear the drag
        // affordance from the always-running timer too, otherwise an unsuccessful/no-op drop can leave every
        // "card end" and "this branch" target glowing until another interaction rebuilds the workbench.
        ClearFinishedDragState();
        // The timer is the authoritative pulse clock; do not let the _Process de-duplication cache suppress it.
        _lastPulseStep = ulong.MaxValue;
        RefreshPulseVisuals();
    }

    private void Deactivate()
    {
        if (!_activated) return;
        _activated = false;
        if (GodotObject.IsInstanceValid(_pulseTimer)) _pulseTimer!.Stop();
        if (GodotObject.IsInstanceValid(_capstoneContainer)
            && _capstoneContainer!.IsConnected(NCapstoneContainer.SignalName.Changed, _capstoneChangedCallback))
            _capstoneContainer.Disconnect(NCapstoneContainer.SignalName.Changed, _capstoneChangedCallback);
        _capstoneContainer = null;
        var topBar = NRun.Instance?.GlobalUi.TopBar;
        if (_mapWasEnabled) topBar?.Map.Enable();
        else topBar?.Map.Disable();
        if (_deckWasEnabled) topBar?.Deck.Enable();
        else topBar?.Deck.Disable();
    }

    private void FitBelowNativeTopBar()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        OffsetTop = NativeTopBarHeight;
    }

    private void Rebuild()
    {
        if (!_suspendDeckScrollCapture && GodotObject.IsInstanceValid(_deckScroll))
            _deckScrollPosition = _deckScroll!.ScrollVertical;
        _deckScroll = null;
        _columns = null;
        _workbenchPanel = null;
        _backpackPanel = null;
        _deckCardPanels.Clear();
        _draggedComponentId = null;
        _hoveredComponentId = null;
        _chipVisuals.Clear();
        _dropVisuals.Clear();
        _draggableComponentIds.Clear();
        _legalBeforeTargets.Clear();
        _legalDropTargets.Clear();
        _lastPulseStep = ulong.MaxValue;
        if (_root is not null)
        {
            RemoveChild(_root);
            _root.QueueFree();
        }
        _previewModels.Clear();
        var useLiveModels = _useLiveModelsForInitialBuild;
        for (var index = 0; index < _session.Cards.Count; index++)
        {
            try
            {
                // A newly opened session exactly mirrors the live deck. Reusing those already-restored models for
                // the first frame avoids cloning and recompiling every card while the native room transition is
                // waiting. Subsequent edits still rebuild true draft previews.
                _previewModels[index] = useLiveModels
                    ? _session.Cards[index].LiveCard
                    : _session.PreviewModel(index);
            }
            catch (Exception exception)
            {
                Log.Warn($"[CardTinkering] Could not prepare card preview {index}: {exception.Message}");
            }
        }
        _useLiveModelsForInitialBuild = false;

        _root = new VBoxContainer
        {
            Name = "Workbench",
            MouseFilter = MouseFilterEnum.Pass,
            AnchorRight = 1,
            AnchorBottom = 1,
            OffsetLeft = 24,
            OffsetTop = 12,
            OffsetRight = -24,
            OffsetBottom = -20
        };
        _root.AddThemeConstantOverride("separation", 14);
        ConfigureBackpackDrop(_root);
        AddChild(_root);

        _root.AddChild(BuildHeader());

        _columns = new HBoxContainer
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        _columns.AddThemeConstantOverride("separation", 14);
        ConfigureBackpackDrop(_columns);
        _root.AddChild(_columns);
        _columns.AddChild(BuildDeckPanel());
        _workbenchPanel = BuildWorkbenchPanel();
        _backpackPanel = BuildBackpackPanel();
        _columns.AddChild(_workbenchPanel);
        _columns.AddChild(_backpackPanel);

        var footer = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        footer.AddThemeConstantOverride("separation", 12);
        ConfigureBackpackDrop(footer);
        _status = new Label
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            VerticalAlignment = VerticalAlignment.Center
        };
        _status.AddThemeFontSizeOverride("font_size", 20);
        footer.AddChild(_status);
        footer.AddChild(BuildActions());
        _root.AddChild(footer);
        RefreshLeaveState();
        RefreshIdleDraggables();
        // Make the affordance visible immediately after opening/rebuilding instead of entering at an arbitrary
        // point in a wall-clock sine wave and possibly spending its first second near the dark trough.
        RestartPulseAtPeak();
        RefreshPulseVisuals();
    }

    private Control BuildHeader()
    {
        var row = new HBoxContainer();
        var title = new Label
            { Text = Localize("卡牌工匠台", "Card Workbench"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 36);
        title.AddThemeColorOverride("font_color", TextMain);
        row.AddChild(title);
        var help = new Label
        {
            Text = Localize("拖到插槽重组　·　拖到编辑区外收回背包",
                "Drag to a slot to assemble · Drag outside the editor to return to the backpack"),
            VerticalAlignment = VerticalAlignment.Center
        };
        help.AddThemeFontSizeOverride("font_size", 22);
        help.AddThemeColorOverride("font_color", TextMuted);
        row.AddChild(help);
        ConfigureBackpackDrop(row);
        return row;
    }

    private Control BuildDeckPanel()
    {
        var (frame, body) = Section(Localize("卡组", "Deck"), 561);
        if (_session.Cards.Count == 0)
        {
            body.AddChild(MessageLabel(Localize("没有可编辑的卡牌。", "No editable cards."),
                HorizontalAlignment.Center));
            return frame;
        }

        var grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 14);
        for (var index = 0; index < _session.Cards.Count; index++)
        {
            var cardIndex = index;
            grid.AddChild(BuildDeckCard(cardIndex));
        }
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            FollowFocus = false
        };
        _deckScroll = scroll;
        scroll.GetVScrollBar().ValueChanged += value =>
        {
            if (!_suspendDeckScrollCapture && GodotObject.IsInstanceValid(scroll))
                _deckScrollPosition = Math.Max(0, (int)Math.Round(value));
        };
        scroll.AddChild(grid);
        ConfigureBackpackDrop(scroll);
        body.AddChild(scroll);
        RestoreDeckScrollPosition(scroll, _deckScrollPosition);
        return frame;
    }

    private void RestoreDeckScrollPosition(ScrollContainer scroll, int position)
    {
        position = Math.Max(0, position);
        _deckScrollPosition = position;
        _suspendDeckScrollCapture = true;
        var revision = ++_deckScrollRestoreRevision;
        scroll.ScrollVertical = position;
        void Restore(int passes)
        {
            Callable.From(() =>
            {
                if (!GodotObject.IsInstanceValid(scroll)) return;
                scroll.ScrollVertical = position;
                if (passes > 1) Restore(passes - 1);
                else if (revision == _deckScrollRestoreRevision) _suspendDeckScrollCapture = false;
            }).CallDeferred();
        }
        // Native card faces settle their minimum sizes over multiple deferred layout passes. Keep the same scroll
        // coordinate through that complete settle instead of assuming one frame is sufficient.
        Restore(3);
    }

    private Control BuildDeckCard(int cardIndex)
    {
        var selected = cardIndex == _selectedCard;
        var card = _session.Cards[cardIndex];
        var value = _session.Value(cardIndex);
        var legal = card.Eternal || _session.CardIsLegal(cardIndex, value, out _);
        var outer = new PanelContainer { CustomMinimumSize = new Vector2(151, 236) };
        ApplyDeckCardStyle(outer, selected, legal);
        _deckCardPanels[cardIndex] = outer;
        var stack = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        stack.AddThemeConstantOverride("separation", 4);
        outer.AddChild(Padded(stack, 8));
        stack.AddChild(BuildCardFace(cardIndex, .39f, true));
        // The outer card frame owns selection feedback. Keeping the compact budget style selection-neutral lets us
        // switch cards without rebuilding any child of the deck ScrollContainer.
        stack.AddChild(BuildBudgetBlock(cardIndex, card, value, selected: false, compact: true, legal: legal));

        var select = new Button
        {
            Flat = true,
            TooltipText = card.Eternal
                ? Localize($"{card.LiveCard.Title}（永恒，不可编辑）",
                    $"{card.LiveCard.Title} (Eternal; cannot be edited)")
                : card.LiveCard.Title,
            ZIndex = 0,
            MouseFilter = MouseFilterEnum.Stop
        };
        select.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        select.FocusMode = FocusModeEnum.None;
        select.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        select.Pressed += () =>
        {
            SelectCard(cardIndex);
        };
        ConfigureBackpackDrop(select);
        outer.AddChild(select);
        return outer;
    }

    private static void ApplyDeckCardStyle(PanelContainer outer, bool selected, bool legal)
    {
        outer.AddThemeStyleboxOverride("panel", Box(
            !legal ? InvalidCardBackground : selected ? new Color("39334c") : new Color("191722"),
            !legal ? Danger : selected ? DropBlue : new Color("393447"), selected ? 3 : 1, 10));
    }

    private void SelectCard(int cardIndex)
    {
        if (cardIndex == _selectedCard) return;
        // Use the continuously captured user position. Reading ScrollVertical from the button callback is too late:
        // Godot may already have clamped it while the neighbouring native preview changed its minimum size.
        var preservedScroll = _deckScrollPosition;
        _suspendDeckScrollCapture = true;
        _selectedCard = cardIndex;
        _showUpgradePreview = false;

        // Keep the deck panel and its ScrollContainer alive. Reconstructing that native card grid caused Godot to
        // reset its range after card faces completed layout, even when ScrollVertical was restored on deferred frames.
        if (!GodotObject.IsInstanceValid(_columns)
            || !GodotObject.IsInstanceValid(_workbenchPanel)
            || !GodotObject.IsInstanceValid(_backpackPanel))
        {
            Rebuild();
            return;
        }

        _draggedComponentId = null;
        _hoveredComponentId = null;
        _chipVisuals.Clear();
        _dropVisuals.Clear();
        _draggableComponentIds.Clear();
        _legalBeforeTargets.Clear();
        _legalDropTargets.Clear();
        _lastPulseStep = ulong.MaxValue;

        var oldWorkbench = _workbenchPanel!;
        var oldBackpack = _backpackPanel!;
        var workbenchIndex = oldWorkbench.GetIndex();
        var backpackIndex = oldBackpack.GetIndex();
        var newWorkbench = BuildWorkbenchPanel();
        var newBackpack = BuildBackpackPanel();
        // Attach replacements before removing their predecessors. This prevents the HBox from briefly collapsing
        // and clamping the still-live deck ScrollContainer to zero during a card switch.
        _columns!.AddChild(newWorkbench);
        _columns.MoveChild(newWorkbench, workbenchIndex);
        _columns.RemoveChild(oldWorkbench);
        oldWorkbench.QueueFree();
        _columns.AddChild(newBackpack);
        _columns.MoveChild(newBackpack, backpackIndex);
        _columns.RemoveChild(oldBackpack);
        oldBackpack.QueueFree();
        _workbenchPanel = newWorkbench;
        _backpackPanel = newBackpack;

        foreach (var (index, panel) in _deckCardPanels)
        {
            if (!GodotObject.IsInstanceValid(panel)) continue;
            var card = _session.Cards[index];
            var legal = card.Eternal || _session.CardIsLegal(index, out _);
            ApplyDeckCardStyle(panel, index == _selectedCard, legal);
        }
        if (GodotObject.IsInstanceValid(_deckScroll))
            RestoreDeckScrollPosition(_deckScroll!, preservedScroll);

        RefreshLeaveState();
        RefreshIdleDraggables();
        RestartPulseAtPeak();
        RefreshPulseVisuals();
    }

    private Control BuildWorkbenchPanel()
    {
        // The deck gained 50% width. Keep the workbench's minimum at its actual content width so the three
        // columns still fit the game's 1680-wide reference viewport instead of overflowing on narrower ratios.
        var (frame, body) = Section(Localize("当前卡牌", "Current Card"), 624, true);
        if (_session.Cards.Count == 0)
        {
            body.AddChild(MessageLabel(Localize("没有可编辑的卡牌。", "No editable cards."),
                HorizontalAlignment.Center));
            return frame;
        }

        _selectedCard = Math.Clamp(_selectedCard, 0, _session.Cards.Count - 1);
        var card = _session.Cards[_selectedCard];
        var value = _session.Value(_selectedCard);
        var legal = card.Eternal || _session.CardIsLegal(_selectedCard, value, out _);

        var work = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        work.AddThemeConstantOverride("separation", 16);
        body.AddChild(work);

        var previewColumn = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(218, 0),
            Alignment = BoxContainer.AlignmentMode.Begin,
            SizeFlagsVertical = SizeFlags.ShrinkBegin
        };
        previewColumn.AddThemeConstantOverride("separation", 12);
        previewColumn.AddChild(BuildBudgetBlock(_selectedCard, card, value,
            selected: true, compact: false, legal: legal));
        previewColumn.AddChild(BuildEffectiveCostBlock(_selectedCard));
        previewColumn.AddChild(BuildCardFace(_selectedCard, .70f, false, _showUpgradePreview));
        previewColumn.AddChild(BuildShellProperties(card.Shell));
        if (card.Shell.Upgrade is not null)
        {
            var upgrade = ActionButton(card.LiveCard.IsUpgraded
                ? Localize("已升级", "Upgraded")
                : _showUpgradePreview ? Localize("显示当前", "Show Current")
                    : Localize("显示升级", "Show Upgrade"), false);
            upgrade.CustomMinimumSize = new Vector2(176, 46);
            upgrade.Disabled = card.LiveCard.IsUpgraded;
            upgrade.TooltipText = card.LiveCard.IsUpgraded
                ? Localize("这张卡已经升级", "This card is already upgraded.")
                : Localize("切换当前卡牌的完整升级预览",
                    "Toggle the complete upgraded preview for the current card.");
            upgrade.Pressed += () =>
            {
                _showUpgradePreview = !_showUpgradePreview;
                Rebuild();
            };
            ConfigureBackpackDrop(upgrade);
            previewColumn.AddChild(upgrade);
        }
        ConfigureBackpackDrop(previewColumn);
        work.AddChild(previewColumn);

        var editorColumn = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(ComponentColumnWidth, 0),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        var editorHeading = new Label
        {
            Text = Localize("组件效果", "Component Effects"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        editorHeading.AddThemeFontSizeOverride("font_size", 24);
        editorHeading.AddThemeColorOverride("font_color", TextMain);
        editorColumn.AddChild(editorHeading);
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        var tree = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        tree.AddThemeConstantOverride("separation", 9);
        scroll.AddChild(tree);
        editorColumn.AddChild(scroll);
        if (card.Eternal)
        {
            AddKeywordChips(tree, _selectedCard, beforeDescription: true);
            var eternal = MessageLabel(Localize("永恒卡牌不能编辑。", "Eternal cards cannot be edited."),
                HorizontalAlignment.Center);
            eternal.AddThemeColorOverride("font_color", Warning);
            tree.AddChild(eternal);
            AddKeywordChips(tree, _selectedCard, beforeDescription: false);
        }
        else
        {
            AddTree(tree, _selectedCard, null);
        }

        _detail = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            Text = Localize("选择组件查看效果与升级。",
                "Select a component to view its effect and upgrade."),
            CustomMinimumSize = new Vector2(0, 78),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _detail.AddThemeFontSizeOverride("normal_font_size", 19);
        _detail.AddThemeColorOverride("default_color", TextMuted);
        editorColumn.AddChild(_detail);
        work.AddChild(editorColumn);
        return frame;
    }

    private Control BuildBackpackPanel()
    {
        var (frame, body) = Section(Localize("组件背包", "Component Backpack"),
            ComponentColumnWidth + SectionPadding * 2);
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 7);
        ConfigureBackpackDrop(scroll);
        ConfigureBackpackDrop(list);
        scroll.AddChild(list);
        body.AddChild(scroll);
        AddBackpackTree(list, null);
        ConfigureBackpackDrop(frame);
        return frame;
    }

    private PanelContainer BuildBudgetBlock(int cardIndex, TrainingCard card,
        CardTinkeringCardAnalysis value, bool selected, bool compact, bool legal)
    {
        var budgets = _session.BudgetAmounts(cardIndex);
        var invalidValue = !card.Eternal && !legal;
        var background = card.Eternal ? new Color("4b4950")
            : invalidValue ? new Color("7b3037")
            : new Color("286143");
        var border = card.Eternal ? new Color("77747d")
            : invalidValue ? Danger : Good;
        var block = new PanelContainer
        {
            CustomMinimumSize = new Vector2(compact ? 118 : 204,
                compact ? 32 : budgets.Count > 1 ? 76 : 52),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            TooltipText = card.Eternal
                ? Localize("永恒卡牌没有可编辑容量。", "Eternal cards have no editable capacity.")
                : Localize("当前预算 / 当前容量；两项数值与合法性检查完全相同。",
                    "Current budget / current capacity; these are the exact values used by legality checks.")
        };
        var style = Box(background, selected ? border.Lightened(.24f) : border,
            selected ? 3 : 2, compact ? 7 : 10);
        style.ContentMarginLeft = compact ? 10 : 18;
        style.ContentMarginRight = compact ? 10 : 18;
        style.ContentMarginTop = compact ? 3 : 7;
        style.ContentMarginBottom = compact ? 3 : 7;
        block.AddThemeStyleboxOverride("panel", style);

        if (card.Eternal)
        {
            var center = new CenterContainer { MouseFilter = MouseFilterEnum.Pass };
            var iconSize = compact ? 30f : 42f;
            var icon = new TextureRect
            {
                Texture = PreloadManager.Cache.GetTexture2D(CardTinkeringEvent.LockIconPath),
                CustomMinimumSize = new Vector2(iconSize, iconSize),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Modulate = new Color("d2cfd6"),
                MouseFilter = MouseFilterEnum.Ignore
            };
            center.AddChild(icon);
            block.AddChild(center);
        }
        else
        {
            var label = new Label
            {
                Text = compact && budgets.Count > 1
                    ? "f(X) / f(X)"
                    : compact
                        ? $"{budgets[0].OccupiedValue:0.##} / {budgets[0].Capacity:0.##}"
                        : budgets.Count > 1
                            ? string.Join('\n', budgets.Select(budget =>
                                  $"{budget.Label}　{budget.OccupiedValue:0.##} / {budget.Capacity:0.##}"))
                            : $"{budgets[0].OccupiedValue:0.##} / {budgets[0].Capacity:0.##}",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.AddThemeFontSizeOverride("font_size", compact ? 18 : budgets.Count > 1 ? 18 : 21);
            label.AddThemeColorOverride("font_color", TextMain);
            block.AddChild(label);
        }
        ConfigureBackpackDrop(block);
        return block;
    }

    private Control BuildEffectiveCostBlock(int cardIndex)
    {
        var cost = _session.EffectiveCost(cardIndex);
        string costText;
        string tooltip;
        if (cost.Fixed is { } fixedCost)
        {
            costText = Localize($"有效费用　{fixedCost:0.##}C", $"Effective Cost  {fixedCost:0.##}C");
            tooltip = Localize("从牌壳原始费用重新计算，并实时扣除当前组件的折算返费。",
                "Recalculated from the shell's printed cost, including live component refund adjustments.");
        }
        else if (cost.X0 is { } x0 && cost.X3 is { } x3)
        {
            costText = Localize("有效费用　f(X)C", "Effective Cost  f(X)C");
            tooltip = Localize($"基于原始 X 费用实时计算：f(0)={x0:0.##}C，f(3)={x3:0.##}C。",
                $"Calculated live from the printed X cost: f(0)={x0:0.##}C, f(3)={x3:0.##}C.");
        }
        else
        {
            costText = Localize("有效费用　—", "Effective Cost  —");
            tooltip = Localize("当前本体接口无法计算这张牌的有效费用。",
                "The current base-mod API cannot calculate this card's effective cost.");
        }

        var panel = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(204, 50),
            TooltipText = tooltip + Localize("\n牌壳容量会按有效费用对应的价值上限等比例实时变化。",
                "\nShell capacity scales live with the value limit for its effective cost.")
        };
        panel.AddThemeStyleboxOverride("panel", Box(new Color("45391f"), FormulaYellow, 2, 9));
        var costLabel = new Label
        {
            Text = costText,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        costLabel.AddThemeFontSizeOverride("font_size", 21);
        costLabel.AddThemeColorOverride("font_color", TextMain);
        panel.AddChild(Padded(costLabel, 11, 5));
        ConfigureBackpackDrop(panel);
        return panel;
    }

    private static Control BuildShellProperties(GeneratedCard shell)
    {
        var cost = shell.Cost < 0 ? Localize("X 能量", "X Energy")
            : Localize($"{shell.Cost} 能量", $"{shell.Cost} Energy");
        if (shell.HasStarCostX) cost += Localize(" · X 蓝星", " · X Stars");
        else if (shell.StarCost > 0)
            cost += Localize($" · {shell.StarCost} 蓝星", $" · {shell.StarCost} Stars");
        var type = shell.Type switch
        {
            GeneratedCardType.Attack => Localize("攻击", "Attack"),
            GeneratedCardType.Skill => Localize("技能", "Skill"),
            GeneratedCardType.Power => Localize("能力", "Power"),
            _ => shell.Type.ToString()
        };
        var rarity = shell.Rarity switch
        {
            GeneratedRarity.Basic => Localize("基础", "Basic"),
            GeneratedRarity.Common => Localize("普通", "Common"),
            GeneratedRarity.Uncommon => Localize("罕见", "Uncommon"),
            GeneratedRarity.Rare => Localize("稀有", "Rare"),
            GeneratedRarity.Ancient => Localize("远古", "Ancient"),
            _ => shell.Rarity.ToString()
        };
        var target = shell.Target == TargetMode.SingleEnemy
            ? Localize("选择敌人", "Targeted")
            : shell.Type == GeneratedCardType.Attack ? Localize("所有敌人", "All Enemies")
                : Localize("无需选择目标", "No Target");
        var tags = shell.Tags.Count == 0 ? Localize("无", "None")
            : string.Join(" · ", shell.Tags.Select(ShellTagName));
        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        panel.AddThemeStyleboxOverride("panel", Box(new Color("211e2a"), new Color("4f485d"), 1, 8));
        var label = new Label
        {
            Text = Localize($"{type} · {rarity}\n原始牌壳：{cost} · {target}\n标签：{tags}",
                $"{type} · {rarity}\nPrinted Shell: {cost} · {target}\nTags: {tags}"),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        label.AddThemeFontSizeOverride("font_size", 17);
        label.AddThemeColorOverride("font_color", TextMuted);
        panel.AddChild(Padded(label, 10, 7));
        return panel;
    }

    private static string ShellTagName(ChaosCardGenerator.CardTag tag) => tag switch
    {
        ChaosCardGenerator.CardTag.Strike => Localize("打击", "Strike"),
        ChaosCardGenerator.CardTag.Defend => Localize("防御", "Defend"),
        ChaosCardGenerator.CardTag.Exhaust => Localize("消耗", "Exhaust"),
        ChaosCardGenerator.CardTag.Innate => Localize("固有", "Innate"),
        ChaosCardGenerator.CardTag.Retain => Localize("保留", "Retain"),
        ChaosCardGenerator.CardTag.Sly => Localize("奇巧", "Sly"),
        ChaosCardGenerator.CardTag.Ethereal => Localize("虚无", "Ethereal"),
        ChaosCardGenerator.CardTag.Eternal => Localize("永恒", "Eternal"),
        ChaosCardGenerator.CardTag.Unplayable => Localize("不可打出", "Unplayable"),
        ChaosCardGenerator.CardTag.OstyAttack => Localize("奥斯提攻击", "Osty Attack"),
        _ => tag.ToString()
    };

    private Control BuildCardFace(int cardIndex, float scale, bool dimEternal, bool showUpgrade = false)
    {
        var size = new Vector2(300f * scale, 422f * scale);
        var surface = new Control
        {
            CustomMinimumSize = size,
            Size = size,
            MouseFilter = MouseFilterEnum.Pass,
            ClipContents = false
        };
        if (!dimEternal)
        {
            surface.TooltipText = _session.Cards[cardIndex].Eternal
                ? Localize("永恒卡牌不能编辑。", "Eternal cards cannot be edited.")
                : Localize("右键：根据当前效果重新随机卡名与卡图",
                    "Right-click: reroll the name and portrait for the current effects");
            surface.GuiInput += input =>
                HandleIdentityRerollInput(input, cardIndex);
        }
        ConfigureBackpackDrop(surface);
        try
        {
            var model = showUpgrade
                ? _session.PreviewModel(cardIndex, showUpgrade: true)
                : _previewModels.TryGetValue(cardIndex, out var cached)
                    ? cached : _session.PreviewModel(cardIndex);
            var cardNode = NCard.Create(model);
            if (cardNode is not null)
            {
                if (dimEternal && _session.Cards[cardIndex].Eternal)
                    cardNode.Modulate = new Color(.72f, .72f, .78f, .9f);
                var holder = NPreviewCardHolder.Create(cardNode, showHoverTips: !dimEternal, scaleOnHover: false);
                if (holder is not null)
                {
                    holder.SetCardScale(Vector2.One * scale);
                    holder.SetClickable(false);
                    holder.MouseFilter = MouseFilterEnum.Pass;
                    ConfigureBackpackDrop(holder);
                    if (!dimEternal && holder.GetNodeOrNull<Control>("%Hitbox") is { } hitbox)
                    {
                        hitbox.TooltipText = surface.TooltipText;
                        // NPreviewCardHolder's native hitbox consumes pointer events before they reach `surface`.
                        // Listen at the real target while retaining the surface callback for fallback previews.
                        hitbox.GuiInput += input => HandleIdentityRerollInput(input, cardIndex);
                    }
                    surface.AddChild(holder);
                    holder.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
                    Callable.From(() =>
                    {
                        if (GodotObject.IsInstanceValid(cardNode) && cardNode.IsInsideTree())
                            cardNode.UpdateVisuals(PileType.Deck,
                                showUpgrade ? CardPreviewMode.Upgrade : CardPreviewMode.Normal);
                    }).CallDeferred();
                    return surface;
                }
                cardNode.QueueFreeSafely();
            }
        }
        catch (Exception exception)
        {
            Log.Warn($"[CardTinkering] Card-face preview failed: {exception.Message}");
        }

        var fallback = new Label
        {
            Text = _session.Cards[cardIndex].LiveCard.Title,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        fallback.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        fallback.AddThemeColorOverride("font_color", TextMain);
        surface.AddChild(fallback);
        return surface;
    }

    private void HandleIdentityRerollInput(InputEvent input, int cardIndex)
    {
        if (input is not InputEventMouseButton
            { ButtonIndex: MouseButton.Right, Pressed: true }) return;
        var frame = Engine.GetProcessFrames();
        if (_lastIdentityRerollFrame == frame) return;
        _lastIdentityRerollFrame = frame;
        GetViewport().SetInputAsHandled();
        if (_session.TryRerollIdentity(cardIndex, out var reason))
        {
            Log.Info($"[CardTinkering] Rerolled editor identity for card {cardIndex}.");
            _showUpgradePreview = false;
            Rebuild();
            SetStatus(Localize("已根据当前效果重新随机卡名与卡图。",
                "Rerolled the name and portrait for the current effects."), Good);
        }
        else SetStatus(reason, Warning);
    }

    private void AddTree(VBoxContainer parent, int cardIndex, string? parentId)
    {
        if (parentId is null) AddKeywordChips(parent, cardIndex, beforeDescription: true);
        var roots = _session.Roots(cardIndex, parentId).ToArray();
        if (parentId is null && roots.Length > 0)
            parent.AddChild(CreateFirstDropZone(roots[0].Id));
        foreach (var component in roots)
            parent.AddChild(BuildComponentBranch(component, cardIndex));
        parent.AddChild(CreateDropZone(cardIndex, parentId));
        if (parentId is null) AddKeywordChips(parent, cardIndex, beforeDescription: false);
    }

    private void AddKeywordChips(VBoxContainer parent, int cardIndex, bool beforeDescription)
    {
        if (!_previewModels.TryGetValue(cardIndex, out var model)) return;
        var order = beforeDescription ? CardKeywordOrder.beforeDescription : CardKeywordOrder.afterDescription;
        foreach (var keyword in order.Where(keyword => model.Keywords.Contains(keyword)
                                                       || keyword == CardKeyword.Eternal
                                                       && _session.Cards[cardIndex].Eternal))
            parent.AddChild(BuildKeywordChip(keyword, cardIndex));
    }

    private Control BuildKeywordChip(CardKeyword keyword, int cardIndex)
    {
        var palette = new ChipPalette(Localize("关键字", "Keyword"), new Color("4a3d20"), new Color("e2bc58"),
            new Color("fff2bd"));
        var title = KeywordTitle(keyword);
        var hasExhaust = _previewModels.TryGetValue(cardIndex, out var model)
                         && model.Keywords.Contains(CardKeyword.Exhaust);
        var chip = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0, 72),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Stop
        };
        ApplyChipPanelStyle(chip, palette, inactive: false, isTrigger: false, highlighted: false);
        var stack = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        stack.AddThemeConstantOverride("separation", 0);
        var tags = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        tags.AddThemeConstantOverride("h_separation", 5);
        tags.AddThemeConstantOverride("v_separation", 3);
        tags.AddChild(BuildChipTag(Localize("关键字", "Keyword"), palette, inactive: false));
        tags.AddChild(BuildChipTag(Localize("外壳", "Shell"), palette, inactive: false));
        tags.AddChild(BuildChipTag(Localize("固定", "Fixed"), palette, inactive: false));
        if (Enum.TryParse<ChaosCardGenerator.CardTag>(keyword.ToString(), out var tag))
        {
            var values = _session.KeywordValues(cardIndex, tag);
            if (values.FirstOrDefault()?.PositiveValue is > .0001d and var positive)
                tags.AddChild(BuildChipTag(Localize($"价值 {BudgetNumber(positive)}",
                    $"Value {BudgetNumber(positive)}"), palette, inactive: false));
            AddCurrentMultiplierTags(tags, values.Select(line =>
                    (line.Label, line.DownsideMultiplier)).ToArray(), palette, inactive: false,
                splitEtherealSynergy: keyword == CardKeyword.Ethereal && hasExhaust);
        }
        stack.AddChild(tags);
        var label = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore,
            Text = $"[color=#ffd269]{title}[/color]",
            CustomMinimumSize = new Vector2(0, 28)
        };
        label.AddThemeFontSizeOverride("normal_font_size", 20);
        label.AddThemeColorOverride("default_color", palette.Foreground);
        chip.AddChild(stack);
        stack.AddChild(Padded(label, 12, 8));
        return chip;
    }

    private static string KeywordTitle(CardKeyword keyword)
    {
        try
        {
            return new LocString("card_keywords", $"{StringHelper.Slugify(keyword.ToString())}.title")
                .GetFormattedText() ?? keyword.ToString();
        }
        catch
        {
            return keyword.ToString();
        }
    }

    private void AddBackpackTree(VBoxContainer parent, string? parentId)
    {
        foreach (var component in _session.Roots(-1, parentId).ToArray())
            parent.AddChild(BuildComponentBranch(component, -1, includeChildSlot: false));
    }

    private Control BuildComponentBranch(TrainingComponent component, int cardIndex, bool includeChildSlot = true)
    {
        var palette = PaletteFor(component.Operation);
        var branch = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        branch.AddThemeConstantOverride("separation", 0);
        branch.AddChild(BuildChip(component, cardIndex, palette));

        if (!TrainingSession.CanHaveChildren(component.Operation)) return branch;

        var descendants = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        descendants.AddThemeConstantOverride("separation", 0);
        var stem = new PanelContainer
        {
            CustomMinimumSize = new Vector2(18, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        stem.AddThemeStyleboxOverride("panel", LStemBox(palette.Background, palette.Border,
            inactive: false));
        descendants.AddChild(stem);
        var childMargin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        childMargin.AddThemeConstantOverride("margin_left", 10);
        childMargin.AddThemeConstantOverride("margin_top", 7);
        var childList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        childList.AddThemeConstantOverride("separation", 6);
        childMargin.AddChild(childList);
        descendants.AddChild(childMargin);
        foreach (var child in _session.Roots(cardIndex, component.Id).ToArray())
            childList.AddChild(BuildComponentBranch(child, cardIndex, includeChildSlot));
        if (includeChildSlot) childList.AddChild(CreateDropZone(cardIndex, component.Id));
        branch.AddChild(descendants);
        return branch;
    }

    private Control BuildChip(TrainingComponent component, int cardIndex, ChipPalette palette)
    {
        var currentValues = cardIndex >= 0 ? _session.ComponentValues(cardIndex, component.Id) : [];
        var value = currentValues.FirstOrDefault()?.Analysis
                    ?? CardTinkeringApi.AnalyzeStandaloneComponent(component.Operation);
        var downside = new ComponentDownsidePricing(value.IsNegative,
            value.DownsideMultiplier, value.LinearCompensation);
        var formula = _session.ComponentFormula(cardIndex, component.Id, value, downside, currentValues);
        var isTrigger = TrainingSession.CanHaveChildren(component.Operation);
        var amount = isTrigger
            ? TriggerAmount(cardIndex, component.Id, value)
            : ComponentValueText(currentValues, value, cardIndex >= 0);
        var wholeCardUnique = TrainingSession.IsWholeCardUnique(component.Operation);
        const bool inactive = false;
        var chip = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0, 76),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Stop
        };
        ApplyChipPanelStyle(chip, palette, inactive, isTrigger, highlighted: false);

        var stack = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        stack.AddThemeConstantOverride("separation", 0);
        var tags = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        tags.AddThemeConstantOverride("h_separation", 5);
        tags.AddThemeConstantOverride("v_separation", 3);
        tags.AddChild(BuildChipTag(palette.Name, palette, inactive));
        // Pure downsides do not have a positive component value. Showing "价值 0" beside a real whole-card
        // payment was misleading, so their multiplier/flat compensation tags replace it. Mixed effects retain
        // both their positive value and their downside tags.
        var primaryFormula = formula.FirstOrDefault(badge => badge.Role != FormulaBadgeRole.Downside);
        if (cardIndex >= 0)
        {
            if (isTrigger) tags.AddChild(BuildChipTag(amount, palette, inactive));
            if (primaryFormula is not null)
                tags.AddChild(BuildFormulaTag(primaryFormula.Expression, palette, inactive));
            else if (!value.IsNegative && !isTrigger)
                tags.AddChild(BuildChipTag(amount, palette, inactive));
            AddCurrentDownsideTags(tags, currentValues, palette, inactive);
        }
        else
        {
            if (isTrigger)
                tags.AddChild(BuildChipTag(amount, palette, inactive));
            if (primaryFormula is not null)
                tags.AddChild(BuildFormulaTag(primaryFormula.Expression, palette, inactive));
            else if (!isTrigger && (!downside.IsNegative || SpecialValuePricing.DisplayValue(value) > 0))
                tags.AddChild(BuildChipTag(amount, palette, inactive));
            foreach (var badge in formula.Where(badge => badge.Role == FormulaBadgeRole.Downside))
                tags.AddChild(badge.Expression.StartsWith("1/x", StringComparison.Ordinal)
                    ? BuildGlobalMultiplierTag(badge.Expression, palette, inactive)
                    : BuildChipTag(badge.Expression, palette, inactive));
            if (downside.IsNegative && formula.All(badge => badge.Role != FormulaBadgeRole.Downside))
                tags.AddChild(BuildChipTag(Localize("负代价 0", "Downside 0"), palette, inactive));
        }
        if (wholeCardUnique)
            tags.AddChild(BuildChipTag(Localize("整卡唯一", "Card-unique"), palette, inactive));
        if (component.Upgrades.Count > 0)
            tags.AddChild(BuildChipTag(component.Progress.Upgraded
                ? Localize("已升级", "Upgraded") : Localize("可升级", "Upgradable"), palette, inactive));
        stack.AddChild(tags);

        var contentMargin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        contentMargin.AddThemeConstantOverride("margin_left", 12);
        contentMargin.AddThemeConstantOverride("margin_right", 12);
        contentMargin.AddThemeConstantOverride("margin_top", 8);
        contentMargin.AddThemeConstantOverride("margin_bottom", 10);
        var rich = BuildComponentContent(component, inactive);
        contentMargin.AddChild(rich);
        stack.AddChild(contentMargin);
        chip.AddChild(stack);

        var hitbox = new Button
        {
            Flat = true,
            MouseFilter = MouseFilterEnum.Stop
        };
        hitbox.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var empty = new StyleBoxEmpty();
        hitbox.AddThemeStyleboxOverride("normal", empty);
        hitbox.AddThemeStyleboxOverride("hover", empty);
        hitbox.AddThemeStyleboxOverride("pressed", empty);
        hitbox.AddThemeStyleboxOverride("focus", empty);
        hitbox.Pressed += () => ShowDetail(component, formula);
        hitbox.MouseEntered += () =>
        {
            ShowDetail(component, formula);
            _hoveredComponentId = CanStartDrag(component.Id, cardIndex) ? component.Id : null;
            _lastPulseStep = ulong.MaxValue;
            RefreshPulseVisuals();
        };
        hitbox.MouseExited += () =>
        {
            if (_hoveredComponentId == component.Id) _hoveredComponentId = null;
            _lastPulseStep = ulong.MaxValue;
            RefreshPulseVisuals();
        };
        chip.AddChild(hitbox);
        ConfigureChipDrag(hitbox, chip, component, cardIndex, palette);
        _chipVisuals.Add(new ChipVisual(chip, component, cardIndex, palette, inactive, isTrigger));
        return chip;
    }

    private string TriggerAmount(int cardIndex, string componentId, CardTinkeringComponentAnalysis value)
    {
        if (cardIndex < 0)
            return Localize($"触发倍率 ×{value.TriggerMultiplier:0.##}",
                $"Trigger ×{value.TriggerMultiplier:0.##}");
        var lines = _session.TriggerMultipliers(cardIndex, componentId);
        if (lines.Count == 0)
            return Localize($"触发倍率 ×{value.TriggerMultiplier:0.##}",
                $"Trigger ×{value.TriggerMultiplier:0.##}");
        static string Amount(IReadOnlyList<TriggerAnalysisLine> items, Func<TriggerAnalysisLine, double> select)
        {
            var values = items.Select(select).ToArray();
            return values.Length == 1 || values.All(item => Math.Abs(item - values[0]) < .0001d)
                ? $"×{values[0]:0.###}"
                : $"f(X)=×{values[0]:0.###}/×{values[1]:0.###}";
        }
        var own = Amount(lines, line => line.Own);
        return Localize($"触发倍率 {own}", $"Trigger {own}");
    }

    private static string ComponentValueText(IReadOnlyList<ComponentAnalysisLine> currentValues,
        CardTinkeringComponentAnalysis fallback, bool installed)
    {
        double Display(CardTinkeringComponentAnalysis analysis) =>
            SpecialValuePricing.DisplayValue(analysis, installed);
        if (currentValues.Count == 0)
            return Localize($"价值 {BudgetNumber(Display(fallback))}",
                $"Value {BudgetNumber(Display(fallback))}");
        var values = currentValues.Select(line => Display(line.Analysis)).ToArray();
        return values.Length == 1 || values.All(value => Math.Abs(value - values[0]) < .0001d)
            ? Localize($"价值 {BudgetNumber(values[0])}", $"Value {BudgetNumber(values[0])}")
            : Localize($"价值 f(X)={BudgetNumber(values[0])}/{BudgetNumber(values[1])}",
                $"Value f(X)={BudgetNumber(values[0])}/{BudgetNumber(values[1])}");
    }

    private static void AddCurrentDownsideTags(Container tags,
        IReadOnlyList<ComponentAnalysisLine> values, ChipPalette palette, bool inactive)
    {
        if (values.Count == 0) return;
        var linear = values.Select(line => (line.Label, line.Analysis.LinearCompensation)).ToArray();
        if (linear.Any(line => Math.Abs(line.LinearCompensation) > .0001d))
        {
            var first = linear[0].LinearCompensation;
            var expression = linear.Length == 1
                             || linear.All(line => Math.Abs(line.LinearCompensation - first) < .0001d)
                ? Localize("负代价 ", "Downside ") + SignedNumber(-first)
                : Localize("负代价 f(X)=", "Downside f(X)=") + SignedNumber(-linear[0].LinearCompensation) + "/"
                  + SignedNumber(-linear[1].LinearCompensation);
            tags.AddChild(BuildChipTag(expression, palette, inactive));
        }
        AddCurrentMultiplierTags(tags, values.Select(line =>
            (line.Label, line.Analysis.DownsideMultiplier)).ToArray(), palette, inactive);
    }

    private static void AddCurrentMultiplierTags(Container tags,
        IReadOnlyList<(string Label, double Multiplier)> values, ChipPalette palette, bool inactive,
        bool splitEtherealSynergy = false)
    {
        if (values.Count == 0 || values.All(line => Math.Abs(line.Multiplier - 1d) < .0001d)) return;
        var factors = values.Select(line => line.Multiplier).ToArray();
        if (splitEtherealSynergy && factors.Length == 1 && factors[0] > 1.0701d)
        {
            BuildAndAddGlobal(tags, "1/x1.07", palette, inactive,
                "×" + MultiplierNumber(factors[0] / 1.07d));
            return;
        }
        var expression = factors.Length == 1 || factors.All(value => Math.Abs(value - factors[0]) < .0001d)
            ? "1/x" + MultiplierNumber(factors[0])
            : "f(X)=1/x" + MultiplierNumber(factors[0]) + "/1/x" + MultiplierNumber(factors[1]);
        BuildAndAddGlobal(tags, expression, palette, inactive);
    }

    private static void BuildAndAddGlobal(Container tags, string expression, ChipPalette palette,
        bool inactive, string? purpleSuffix = null) =>
        tags.AddChild(BuildGlobalMultiplierTag(expression, palette, inactive, purpleSuffix));

    private static string SignedNumber(double value) => value >= -0.0001d
        ? "+" + BudgetNumber(Math.Max(0d, value))
        : BudgetNumber(value);

    private static string BudgetNumber(double value) => Math.Abs(value - Math.Round(value)) < .0001d
        ? Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture)
        : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string MultiplierNumber(double value) =>
        value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    private static Control BuildChipTag(string text, ChipPalette palette, bool inactive)
    {
        var block = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        var background = inactive ? palette.Background.Darkened(.50f) : palette.Border.Darkened(.35f);
        var border = inactive ? new Color(.34f, .34f, .38f) : palette.Border;
        block.AddThemeStyleboxOverride("panel", TagBox(background, border));
        var label = new Label
        {
            Text = text,
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.AddThemeFontSizeOverride("font_size", 15);
        label.AddThemeColorOverride("font_color", inactive ? new Color(.66f, .66f, .69f) : palette.Foreground);
        block.AddChild(Padded(label, 8, 3));
        return block;
    }

    private static Control BuildFormulaTag(string expression, ChipPalette palette, bool inactive)
    {
        var block = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        var background = inactive ? palette.Background.Darkened(.50f) : palette.Border.Darkened(.35f);
        var border = inactive ? new Color(.34f, .34f, .38f) : palette.Border;
        block.AddThemeStyleboxOverride("panel", TagBox(background, border));
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 0);
        row.AddChild(FormulaPart(SpecialValuePricing.FormulaName(expression),
            inactive ? new Color(.66f, .66f, .69f) : palette.Foreground));
        row.AddChild(FormulaPart("=" + SpecialValuePricing.FormulaExpression(expression),
            inactive ? new Color(.66f, .66f, .69f) : FormulaYellow));
        block.AddChild(Padded(row, 8, 3));
        return block;
    }

    private static Control BuildGlobalMultiplierTag(string expression, ChipPalette palette, bool inactive,
        string? purpleSuffix = null)
    {
        var block = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        var background = inactive ? palette.Background.Darkened(.50f) : palette.Border.Darkened(.35f);
        var border = inactive ? new Color(.34f, .34f, .38f) : palette.Border;
        block.AddThemeStyleboxOverride("panel", TagBox(background, border));
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 0);
        var muted = new Color(.66f, .66f, .69f);
        row.AddChild(FormulaPart(Localize("全卡倍率 ", "Whole-card multiplier "),
            inactive ? muted : palette.Foreground));
        row.AddChild(FormulaPart(expression, inactive ? muted : Danger));
        if (purpleSuffix is not null)
            row.AddChild(FormulaPart(purpleSuffix, inactive ? muted : SynergyPurple));
        block.AddChild(Padded(row, 8, 3));
        return block;
    }

    private static Label FormulaPart(string text, Color color)
    {
        var label = new Label
        {
            Text = text,
            MouseFilter = MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.AddThemeFontSizeOverride("font_size", 15);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private RichTextLabel BuildComponentContent(TrainingComponent component, bool inactive)
    {
        var text = ComponentMarkup(_session.ComponentContentText(component, english: IsEnglish));
        var label = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore,
            Text = text,
            CustomMinimumSize = new Vector2(0, 28)
        };
        label.AddThemeFontSizeOverride("normal_font_size", 20);
        label.AddThemeColorOverride("default_color", inactive ? new Color(.62f, .62f, .65f) : TextMain);
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(label) || !label.IsInsideTree() || label.GetLineCount() <= 2) return;
            label.AddThemeFontSizeOverride("normal_font_size", label.GetLineCount() > 3 ? 16 : 18);
        }).CallDeferred();
        return label;
    }

    private Control BuildActions()
    {
        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        actions.AddThemeConstantOverride("separation", 8);
        ConfigureBackpackDrop(actions);
        var reset = ActionButton(Localize("重置", "Reset"), false);
        reset.TooltipText = Localize("恢复进入本训练室时的整个卡组和背包",
            "Restore the entire deck and backpack to their state on entering this Training Room.");
        reset.Pressed += () => { _session.Reset(); Rebuild(); };
        ConfigureBackpackDrop(reset);
        actions.AddChild(reset);
        var settings = ActionButton(Localize("设置", "Settings"), false);
        settings.TooltipText = Localize("打开游戏设置；关闭后返回卡牌工匠台",
            "Open game settings; closing them returns to the Card Workbench.");
        settings.Pressed += OpenSettings;
        ConfigureBackpackDrop(settings);
        actions.AddChild(settings);
        _save = ActionButton(Localize("保存方案", "Save Draft"), false);
        _save.TooltipText = Localize("保存当前训练室内的临时检查点",
            "Save a temporary checkpoint for this Training Room.");
        _save.Pressed += () =>
        {
            _session.SaveCheckpoint();
            Rebuild();
            SetStatus(Localize("已保存当前方案；检查点仅在本次训练室有效。",
                "Draft saved. This checkpoint lasts only for the current Training Room."), Good);
        };
        ConfigureBackpackDrop(_save);
        actions.AddChild(_save);
        var load = ActionButton(Localize("加载方案", "Load Draft"), false);
        load.TooltipText = Localize("恢复当前训练室内保存的检查点",
            "Restore the checkpoint saved in this Training Room.");
        load.Disabled = !_session.HasCheckpoint;
        load.Pressed += () =>
        {
            if (_session.LoadCheckpoint())
            {
                Rebuild();
                SetStatus(Localize("已加载保存的方案。", "Saved draft loaded."), Good);
            }
        };
        ConfigureBackpackDrop(load);
        actions.AddChild(load);
        _leave = ActionButton(Localize("保存并完成", "Save & Finish"), true);
        _leave.TooltipText = Localize("一次性将所有合法草稿应用到牌组，然后离开训练室",
            "Apply all valid drafts to the deck at once, then leave the Training Room.");
        _leave.Pressed += Leave;
        ConfigureBackpackDrop(_leave);
        actions.AddChild(_leave);
        return actions;
    }

    private void OpenSettings()
    {
        var globalUi = NRun.Instance?.GlobalUi;
        if (globalUi is null) return;
        MoveBelow(globalUi.CapstoneContainer);
        globalUi.SubmenuStack.ShowScreen(CapstoneSubmenuType.Settings);
    }

    private void OnCapstoneChanged()
    {
        var globalUi = NRun.Instance?.GlobalUi;
        if (globalUi is null || !GodotObject.IsInstanceValid(this)) return;
        MoveBelow(globalUi.CapstoneContainer.InUse
            ? globalUi.CapstoneContainer
            : globalUi.TargetManager);
    }

    private void MoveBelow(Node sibling)
    {
        if (GetParent() is not Node parent || sibling.GetParent() != parent) return;
        var targetIndex = sibling.GetIndex();
        if (GetIndex() < targetIndex) targetIndex--;
        parent.MoveChild(this, targetIndex);
    }

    private void Leave()
    {
        if (_commitInProgress) return;
        if (!_session.CanLeave(out var invalidReason))
        {
            ShowInvalidConfirmation(invalidReason);
            return;
        }
        TaskHelper.RunSafely(CommitAndLeaveAsync());
    }

    private async Task CommitAndLeaveAsync()
    {
        if (_commitInProgress) return;
        _commitInProgress = true;
        if (_leave is not null) _leave.Disabled = true;
        if (!_session.Commit(out var reason))
        {
            _commitInProgress = false;
            if (_leave is not null) _leave.Disabled = false;
            SetStatus(reason, Warning);
            return;
        }
        var room = RunManager.Instance.DebugOnlyGetState()?.CurrentRoom;
        if (_event is not null)
        {
            SetStatus(Localize("正在同步并保存调整……", "Synchronizing and saving changes…"), Good);
            var synchronizationFailure = await _event.SubmitCommittedChanges();
            if (!string.IsNullOrWhiteSpace(synchronizationFailure))
            {
                _commitInProgress = false;
                if (_leave is not null) _leave.Disabled = false;
                SetStatus(Localize($"联机同步失败：{synchronizationFailure}",
                    $"Multiplayer synchronization failed: {synchronizationFailure}"), Warning);
                return;
            }
        }
        else
            await SaveManager.Instance.SaveRun(room);
        Deactivate();
        QueueFree();
    }

    private void ShowInvalidConfirmation(string reason)
    {
        if (_confirmation is not null && GodotObject.IsInstanceValid(_confirmation)) return;
        var shade = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, .78f),
            MouseFilter = MouseFilterEnum.Stop,
            ZIndex = 500
        };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        shade.AddChild(center);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(690, 0) };
        panel.AddThemeStyleboxOverride("panel", Box(new Color("2b2028"), Danger, 3, 14));
        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", 18);
        var title = MessageLabel(Localize("仍要保存并完成吗？", "Save and finish anyway?"),
            HorizontalAlignment.Center);
        title.AddThemeFontSizeOverride("font_size", 30);
        title.AddThemeColorOverride("font_color", Danger);
        stack.AddChild(title);
        var body = MessageLabel(Localize(
            $"牌组中存在不合法卡牌：\n{reason}\n\n这些卡仍会保存；战斗外会显示失效警告，进入战斗后则保留费用，但清除全部关键词与效果并显示“没有效果。”。",
            $"The deck contains invalid cards:\n{reason}\n\nThey will still be saved. Outside combat they show a disabled warning; in combat they keep their cost but lose all keywords and effects and read ‘No effect.’."),
            HorizontalAlignment.Center);
        body.AddThemeFontSizeOverride("font_size", 21);
        stack.AddChild(body);
        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        actions.AddThemeConstantOverride("separation", 16);
        var cancel = ActionButton(Localize("返回调整", "Keep Editing"), false);
        cancel.Pressed += CloseConfirmation;
        actions.AddChild(cancel);
        var confirm = ActionButton(Localize("仍然保存", "Save Anyway"), true);
        ApplyActionButtonStyle(confirm, new Color("7b3037"), Danger);
        confirm.Pressed += () =>
        {
            CloseConfirmation();
            TaskHelper.RunSafely(CommitAndLeaveAsync());
        };
        actions.AddChild(confirm);
        stack.AddChild(actions);
        panel.AddChild(Padded(stack, 28, 24));
        center.AddChild(panel);
        _confirmation = shade;
        AddChild(shade);
    }

    private void CloseConfirmation()
    {
        if (_confirmation is not null && GodotObject.IsInstanceValid(_confirmation))
            _confirmation.QueueFree();
        _confirmation = null;
    }

    internal void OnDrop(string componentId, int cardIndex, string? parentId)
    {
        EndComponentDrag();
        if (_session.Move(componentId, cardIndex, parentId, out var reason)) Rebuild();
        else SetStatus(reason, Warning);
    }

    internal bool CanDrop(string componentId, int cardIndex, string? parentId) =>
        _session.CanMove(componentId, cardIndex, parentId, out _);

    internal void OnDropBefore(string componentId, string beforeComponentId)
    {
        EndComponentDrag();
        if (_session.MoveBefore(componentId, beforeComponentId, out var reason)) Rebuild();
        else SetStatus(reason, Warning);
    }

    internal bool CanDropBefore(string componentId, string beforeComponentId) =>
        _session.CanMoveBefore(componentId, beforeComponentId, out _);

    private void ShowDetail(TrainingComponent component, IReadOnlyList<FormulaBadge>? formula = null)
    {
        if (_detail is null) return;
        var palette = PaletteFor(component.Operation);
        var wholeCardUnique = TrainingSession.IsWholeCardUnique(component.Operation)
            ? Localize("  整卡唯一", "  Card-unique")
            : string.Empty;
        var upgraded = component.Progress.Upgraded ? Localize("  已升级", "  Upgraded") : string.Empty;
        var text = formula is null
            ? _session.ComponentText(component, english: IsEnglish)
            : SpecialValuePricing.AppendExplanation(
                _session.ComponentText(component, english: IsEnglish), formula);
        _detail.Text = ComponentMarkup($"{palette.Name}{wholeCardUnique}{upgraded}　{text}");
        _detail.AddThemeColorOverride("default_color", palette.Foreground);
    }

    private void RefreshLeaveState()
    {
        var valid = _session.CanLeave(out var reason);
        if (_save is not null)
        {
            _save.Disabled = _commitInProgress;
            _save.TooltipText = Localize("保存当前训练室内的临时检查点；不合法草稿也可以保存",
                "Save a temporary checkpoint for this Training Room; invalid drafts may also be saved.");
        }
        if (_leave is not null)
        {
            _leave.Disabled = _commitInProgress;
            ApplyActionButtonStyle(_leave,
                valid ? new Color("326d50") : new Color("7b3037"),
                valid ? new Color("88e4a3") : Danger);
            _leave.TooltipText = valid
                ? Localize("一次性将所有草稿应用到牌组，然后离开训练室",
                    "Apply every draft to the deck at once, then leave the Training Room.")
                : Localize($"包含不合法卡牌；点击后确认：{reason}",
                    $"Contains invalid cards; click to confirm: {reason}");
        }
        SetStatus(valid ? Localize("✓ 所有卡牌均合法且未超过容量",
            "✓ Every card is valid and within capacity") : reason, valid ? Good : Danger);
    }

    private void SetStatus(string text, Color color)
    {
        if (_status is null) return;
        _status.Text = text;
        _status.AddThemeColorOverride("font_color", color);
    }

    private void ConfigureChipDrag(Button dragSurface, Control chip, TrainingComponent component,
        int cardIndex, ChipPalette palette)
    {
        dragSurface.SetDragForwarding(
            Callable.From<Vector2, Variant>(_ =>
            {
                if (!CanStartDrag(component.Id, cardIndex))
                {
                    SetStatus(Localize("该背包组件与当前卡牌完全不兼容。",
                        "This backpack component is incompatible with the current card."), Warning);
                    return default;
                }
                _draggedComponentId = component.Id;
                _dragStartedFrame = Engine.GetProcessFrames();
                RefreshLegalDragTargets(component.Id);
                RestartPulseAtPeak();
                RefreshPulseVisuals();
                var preview = new PanelContainer();
                preview.AddThemeStyleboxOverride("panel", Box(palette.Background, palette.Border, 2, 8));
                var label = new RichTextLabel
                {
                    BbcodeEnabled = true,
                    FitContent = true,
                    ScrollActive = false,
                    Text = ComponentMarkup(_session.ComponentContentText(component, english: IsEnglish)),
                    CustomMinimumSize = new Vector2(Math.Min(460, Math.Max(240, chip.Size.X)), 52),
                    MouseFilter = MouseFilterEnum.Ignore
                };
                label.AddThemeFontSizeOverride("normal_font_size", 18);
                label.AddThemeColorOverride("default_color", palette.Foreground);
                preview.AddChild(Padded(label, 10, 5));
                dragSurface.SetDragPreview(preview);
                return Variant.From(component.Id);
            }),
            Callable.From<Vector2, Variant, bool>((_, data) =>
                data.VariantType == Variant.Type.String
                && CanDropBefore(data.AsString(), component.Id)),
            Callable.From<Vector2, Variant>((_, data) =>
            {
                if (data.VariantType == Variant.Type.String)
                    OnDropBefore(data.AsString(), component.Id);
            }));
    }

    private Button CreateDropZone(int cardIndex, string? parentId)
    {
        var zone = new Button
        {
            Text = parentId is null
                ? Localize("＋ 添加到卡牌末尾", "+ Add to end of card")
                : Localize("└　＋ 添加到这一段", "└  + Add to this branch"),
            Flat = false,
            CustomMinimumSize = new Vector2(0, parentId is null ? 48 : 44),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Stop
        };
        zone.AddThemeFontSizeOverride("font_size", 18);
        ConfigureDropTarget(zone, cardIndex, parentId);
        return zone;
    }

    private Button CreateFirstDropZone(string beforeComponentId)
    {
        // A narrow transparent hit band above the first visible component makes inserting at index zero much less
        // fiddly. It remains visually absent while idle and joins the normal legal-target pulse during a drag.
        var zone = new Button
        {
            Text = string.Empty,
            Flat = false,
            CustomMinimumSize = new Vector2(0, 22),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Stop
        };
        ConfigureDropBeforeTarget(zone, beforeComponentId, hiddenWhenIdle: true);
        return zone;
    }

    private void ConfigureDropTarget(Button target, int cardIndex, string? parentId)
    {
        target.SetDragForwarding(
            Callable.From<Vector2, Variant>(_ => default),
            Callable.From<Vector2, Variant, bool>((_, data) =>
                data.VariantType == Variant.Type.String && CanDrop(data.AsString(), cardIndex, parentId)),
            Callable.From<Vector2, Variant>((_, data) =>
            {
                if (data.VariantType == Variant.Type.String) OnDrop(data.AsString(), cardIndex, parentId);
            }));
        _dropVisuals.Add(new DropVisual(target,
            componentId => CanDrop(componentId, cardIndex, parentId)));
    }

    private void ConfigureDropBeforeTarget(Button target, string beforeComponentId, bool hiddenWhenIdle)
    {
        target.SetDragForwarding(
            Callable.From<Vector2, Variant>(_ => default),
            Callable.From<Vector2, Variant, bool>((_, data) =>
                data.VariantType == Variant.Type.String
                && CanDropBefore(data.AsString(), beforeComponentId)),
            Callable.From<Vector2, Variant>((_, data) =>
            {
                if (data.VariantType == Variant.Type.String)
                    OnDropBefore(data.AsString(), beforeComponentId);
            }));
        _dropVisuals.Add(new DropVisual(target,
            componentId => CanDropBefore(componentId, beforeComponentId), hiddenWhenIdle));
    }

    private void ConfigureBackpackDrop(Control target)
    {
        target.SetDragForwarding(
            Callable.From<Vector2, Variant>(_ => default),
            Callable.From<Vector2, Variant, bool>((_, data) => data.VariantType == Variant.Type.String
                && CanDrop(data.AsString(), -1, null)),
            Callable.From<Vector2, Variant>((_, data) =>
            {
                if (data.VariantType == Variant.Type.String) OnDrop(data.AsString(), -1, null);
            }));
    }

    private static ChipPalette PaletteFor(GeneratorOperation operation)
    {
        if (TrainingSession.CanHaveChildren(operation))
            return new ChipPalette(Localize("触发", "Trigger"), new Color("453561"),
                new Color("bd91ef"), new Color("fff7ff"));
        if (CardEffectRules.IsEnemyDamage(operation))
            return new ChipPalette(Localize("伤害", "Damage"), new Color("572f37"),
                new Color("ff8a9b"), new Color("fff5f6"));
        if (operation.Template.Contains("Block", StringComparison.OrdinalIgnoreCase))
            return new ChipPalette(Localize("防御", "Block"), new Color("253f5a"),
                new Color("78bfff"), new Color("f5fbff"));
        if (operation.Template.Contains("Energy", StringComparison.OrdinalIgnoreCase)
            || operation.Template.Contains("Star", StringComparison.OrdinalIgnoreCase)
            || operation.Template.Contains("Cost", StringComparison.OrdinalIgnoreCase))
            return new ChipPalette(Localize("资源", "Resource"), new Color("55431f"),
                new Color("ffd269"), new Color("fffbed"));
        if (CardEffectRules.IsCardDrawEffect(operation)
            || operation.Template.Contains("Card", StringComparison.OrdinalIgnoreCase)
            || operation.Template.Contains("Discard", StringComparison.OrdinalIgnoreCase)
            || operation.Template.Contains("Exhaust", StringComparison.OrdinalIgnoreCase))
            return new ChipPalette(Localize("牌库", "Cards"), new Color("214b48"),
                new Color("70ded0"), new Color("effffd"));
        if (CardEffectRules.IsHealingOrMaxHp(operation)
            || CardEffectRules.IsPermanentStrengthOrDexterityChange(operation)
            || TrainingSession.IsSingleUse(operation))
            return new ChipPalette(Localize("成长", "Growth"), new Color("334c31"),
                new Color("8fd784"), new Color("f4fff1"));
        if (CardEffectRules.IsNegativeEffect(operation))
            return new ChipPalette(Localize("代价", "Downside"), new Color("4b3430"),
                new Color("d99a78"), new Color("fff7ef"));
        return new ChipPalette(Localize("效果", "Effect"), new Color("343944"),
            new Color("abb7c9"), new Color("f7f9ff"));
    }

    private static void ApplyChipPanelStyle(PanelContainer chip, ChipPalette palette, bool inactive,
        bool isTrigger, bool highlighted, float pulse = 0f, bool hovered = false)
    {
        var baseBackground = inactive ? palette.Background.Darkened(.38f) : palette.Background;
        var baseBorder = inactive ? new Color(.38f, .38f, .42f) : palette.Border;
        var glow = highlighted ? .06f + .18f * pulse : 0f;
        if (hovered) glow = Math.Max(glow, .40f);
        var background = glow > 0f ? baseBackground.Lerp(palette.Border, glow) : baseBackground;
        var border = hovered ? palette.Border.Lerp(Colors.White, .58f)
            : highlighted ? baseBorder.Lerp(Colors.White, .06f + .20f * pulse) : baseBorder;
        chip.AddThemeStyleboxOverride("panel", CornerBox(background,
            border, hovered ? 4 : highlighted ? 3 : 2,
            8, 8, 8, isTrigger ? 0 : 8));
    }

    private bool CanStartDrag(string componentId, int cardIndex) => cardIndex >= 0
        || _session.Cards.Count > 0 && _session.CanMoveAnywhereOnCard(componentId, _selectedCard);

    private void RefreshIdleDraggables()
    {
        _draggableComponentIds.Clear();
        foreach (var visual in _chipVisuals)
            if (CanStartDrag(visual.Component.Id, visual.CardIndex))
                _draggableComponentIds.Add(visual.Component.Id);
    }

    private void RefreshLegalDragTargets(string componentId)
    {
        _legalBeforeTargets.Clear();
        foreach (var visual in _chipVisuals)
            if (visual.CardIndex >= 0 && visual.Component.Id != componentId
                && CanDropBefore(componentId, visual.Component.Id))
                _legalBeforeTargets.Add(visual.Component.Id);
        _legalDropTargets.Clear();
        foreach (var visual in _dropVisuals)
            if (visual.Accepts(componentId)) _legalDropTargets.Add(visual.Target);
    }

    private void RefreshPulseVisuals()
    {
        var ticks = Time.GetTicksMsec();
        var step = ticks / 100;
        if (step == _lastPulseStep) return;
        _lastPulseStep = step;
        var elapsed = ticks >= _pulseEpochMsec ? ticks - _pulseEpochMsec : 0ul;
        var phase = (float)((Math.Cos(elapsed / 1000d * PulseAngularSpeed) + 1d) * .5d);
        foreach (var visual in _chipVisuals)
        {
            if (!GodotObject.IsInstanceValid(visual.Panel)) continue;
            var highlighted = _draggedComponentId is not null
                ? _legalBeforeTargets.Contains(visual.Component.Id)
                : _draggableComponentIds.Contains(visual.Component.Id);
            ApplyChipPanelStyle(visual.Panel, visual.Palette, visual.Inactive,
                visual.IsTrigger, highlighted, phase,
                hovered: _hoveredComponentId == visual.Component.Id);
            visual.Panel.QueueRedraw();
        }
        foreach (var visual in _dropVisuals)
        {
            if (!GodotObject.IsInstanceValid(visual.Target)) continue;
            var highlighted = _draggedComponentId is not null && _legalDropTargets.Contains(visual.Target);
            var hidden = visual.HiddenWhenIdle && !highlighted;
            var background = highlighted
                ? DropPulseBackground.Lerp(DropBlue, .10f + .25f * phase)
                : new Color(0f, 0f, 0f, 0f);
            var border = highlighted ? DropBlue.Lerp(Colors.White, .22f * phase)
                : hidden ? new Color(0f, 0f, 0f, 0f) : PanelBorder.Darkened(.25f);
            visual.Target.AddThemeStyleboxOverride("normal", Box(background, border,
                highlighted ? 3 : hidden ? 0 : 1, 8));
            var hoverBackground = highlighted
                ? DropBlue.Darkened(.52f).Lerp(DropBlue, .14f + .12f * phase)
                : background.Lightened(.07f);
            var hoverBorder = highlighted ? Colors.White.Lerp(DropBlue, .18f)
                : border.Lightened(.12f);
            visual.Target.AddThemeStyleboxOverride("hover", Box(hoverBackground,
                hoverBorder, highlighted ? 4 : hidden ? 0 : 2, 8));
            visual.Target.AddThemeColorOverride("font_color", highlighted ? TextMain : TextMuted);
            visual.Target.QueueRedraw();
        }
    }

    private void RestartPulseAtPeak()
    {
        _pulseEpochMsec = Time.GetTicksMsec();
        _lastPulseStep = ulong.MaxValue;
    }

    private void ClearFinishedDragState()
    {
        if (_draggedComponentId is null
            || Engine.GetProcessFrames() <= _dragStartedFrame + 1
            || GetViewport()?.GuiIsDragging() == true)
            return;
        ClearComponentDragState();
        RestartPulseAtPeak();
    }

    private void EndComponentDrag()
    {
        ClearComponentDragState();
        RestartPulseAtPeak();
        RefreshPulseVisuals();
    }

    private void ClearComponentDragState()
    {
        _draggedComponentId = null;
        _legalBeforeTargets.Clear();
        _legalDropTargets.Clear();
    }

    private static StyleBoxFlat LStemBox(Color background, Color border, bool inactive)
    {
        var bg = inactive ? background.Darkened(.38f) : background;
        var edge = inactive ? new Color(.38f, .38f, .42f) : border;
        var box = CornerBox(bg, edge, 2, 0, 0, 0, 8);
        box.BorderWidthTop = 0;
        return box;
    }

    private static StyleBoxFlat TagBox(Color background, Color border)
    {
        var box = CornerBox(background, border, 1, 0, 0, 6, 6);
        box.BorderWidthTop = 0;
        return box;
    }

    private static (PanelContainer Frame, VBoxContainer Body) Section(string title, float width,
        bool expandHorizontal = false)
    {
        var frame = new PanelContainer
        {
            CustomMinimumSize = new Vector2(width, 0),
            SizeFlagsHorizontal = expandHorizontal ? SizeFlags.ExpandFill : SizeFlags.ShrinkBegin,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Pass
        };
        frame.AddThemeStyleboxOverride("panel", Box(PanelColor, PanelBorder, 2, 12));
        var body = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Pass
        };
        body.AddThemeConstantOverride("separation", 11);
        var heading = new Label { Text = title, HorizontalAlignment = HorizontalAlignment.Center };
        heading.AddThemeFontSizeOverride("font_size", 26);
        heading.AddThemeColorOverride("font_color", TextMain);
        body.AddChild(heading);
        frame.AddChild(Padded(body, SectionPadding));
        return (frame, body);
    }

    private static MarginContainer Padded(Control content, int horizontal, int vertical = -1)
    {
        if (vertical < 0) vertical = horizontal;
        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Pass };
        margin.AddThemeConstantOverride("margin_left", horizontal);
        margin.AddThemeConstantOverride("margin_right", horizontal);
        margin.AddThemeConstantOverride("margin_top", vertical);
        margin.AddThemeConstantOverride("margin_bottom", vertical);
        margin.AddChild(content);
        return margin;
    }

    private static Label MessageLabel(string text, HorizontalAlignment alignment = HorizontalAlignment.Left)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = alignment,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        label.AddThemeFontSizeOverride("font_size", 20);
        label.AddThemeColorOverride("font_color", TextMuted);
        return label;
    }

    private static Button ActionButton(string text, bool primary)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(primary ? 132 : 104, 54) };
        var bg = primary ? new Color("326d50") : new Color("353142");
        var border = primary ? new Color("88e4a3") : new Color("6d657f");
        ApplyActionButtonStyle(button, bg, border);
        button.AddThemeColorOverride("font_color", TextMain);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeFontSizeOverride("font_size", 20);
        return button;
    }

    private static void ApplyActionButtonStyle(Button button, Color bg, Color border)
    {
        button.AddThemeStyleboxOverride("normal", Box(bg, border, 2, 8));
        button.AddThemeStyleboxOverride("hover", Box(bg.Lightened(.12f), border.Lightened(.10f), 3, 8));
        button.AddThemeStyleboxOverride("pressed", Box(bg.Lightened(.18f), Colors.White, 3, 8));
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
    }

    private static StyleBoxFlat Box(Color background, Color border, int borderWidth, int radius)
        => CornerBox(background, border, borderWidth, radius, radius, radius, radius);

    private static StyleBoxFlat CornerBox(Color background, Color border, int borderWidth,
        int topLeft, int topRight, int bottomRight, int bottomLeft)
    {
        return new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = border,
            BorderWidthLeft = borderWidth,
            BorderWidthTop = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthBottom = borderWidth,
            CornerRadiusTopLeft = topLeft,
            CornerRadiusTopRight = topRight,
            CornerRadiusBottomRight = bottomRight,
            CornerRadiusBottomLeft = bottomLeft
        };
    }

    private static string ComponentMarkup(string text)
    {
        var prefix = RunManager.Instance.GetLocalCharacterEnergyIconPrefix() ?? "colorless";
        return ComponentMarkupForPrefix(text, prefix);
    }

    internal static string ComponentMarkupForPrefix(string text, string prefix)
    {
        var energyIcon = $"[img]res://images/packed/sprite_fonts/{prefix}_energy_icon.png[/img]";
        const string starIcon = "[img]res://images/packed/sprite_fonts/star_icon.png[/img]";
        static string Icons(int amount, string icon) => amount is > 0 and < 4
            ? string.Concat(Enumerable.Repeat(icon, amount))
            : $"{amount}{icon}";

        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"\{(?:energyPrefix|[A-Za-z0-9_]+):energyIcons\((?<n>\d+)\)\}",
            match => Icons(int.Parse(match.Groups["n"].Value), energyIcon));
        text = text.Replace("{singleStarIcon}", starIcon, StringComparison.Ordinal);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<n>\d+)\s*点能量",
            match => Icons(int.Parse(match.Groups["n"].Value), energyIcon));
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<n>\d+)\s+Energy",
            match => Icons(int.Parse(match.Groups["n"].Value), energyIcon),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<n>\d+)\s*颗蓝星",
            match => Icons(int.Parse(match.Groups["n"].Value), starIcon));
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<n>\d+)\s+Stars?",
            match => Icons(int.Parse(match.Groups["n"].Value), starIcon),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"(?<![A-Za-z0-9_#])\d+(?![A-Za-z0-9_])", "[color=#78c8ff]$0[/color]");
        foreach (var keyword in new[]
                 {
                     "格挡", "易伤", "虚弱", "力量", "消耗", "中毒", "敏捷", "保留", "奇巧", "无实体",
                     "虚无", "小刀", "Block", "Vulnerable", "Weak", "Strength", "Exhaust", "Poison",
                     "Dexterity", "Retain", "Sly", "Intangible", "Ethereal", "Shiv"
                 })
            text = text.Replace(keyword, $"[color=#ffd269]{keyword}[/color]", StringComparison.Ordinal);
        return text
            .Replace("[gold]", "[color=#ffd269]", StringComparison.Ordinal)
            .Replace("[/gold]", "[/color]", StringComparison.Ordinal)
            .Replace("[blue]", "[color=#78c8ff]", StringComparison.Ordinal)
            .Replace("[/blue]", "[/color]", StringComparison.Ordinal);
    }

    private static string ShortText(string text, int maxLength)
    {
        text = text.Replace('\n', ' ').Trim();
        return text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }

}
