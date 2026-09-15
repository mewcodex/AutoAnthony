using System.Text;
using AutoAnthony;
using ChaosCardGenerator;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using static AutoAnthonyCardTinkering.TinkeringText;
using GeneratorCardTag = ChaosCardGenerator.CardTag;

namespace AutoAnthonyCardTinkering;

internal sealed partial class FreeformEditorOverlay : ColorRect
{
    private const string ExportPrefix = "AACT-FREE-1:";
    private const float NativeTopBarHeight = 104f;
    private const int SectionPadding = 14;
    private const float CatalogColumnWidth = 561f;
    private const float WorkbenchColumnWidth = 624f;
    private const float ComponentColumnWidth = 362f;
    private static readonly Color Bg = new("14111c");
    private static readonly Color Panel = new("272331");
    private static readonly Color Border = new("625970");
    private static readonly Color Text = new("fff6e2");
    private static readonly Color Muted = new("c9becd");
    private static readonly Color Accent = new("78c8ff");
    private static readonly Color Good = new("84e39a");
    private static readonly Color Bad = new("ff7272");

    private readonly RunState _run;
    private readonly FreeformSession _session = new();
    private readonly IReadOnlyList<CardTinkeringComponentPrototype> _catalog;
    private VBoxContainer? _root;
    private Label? _status;
    private RichTextLabel? _detail;
    private TextEdit? _transferText;
    // An empty set means "All". Left-click replaces the selection; right-click toggles entries for multi-select.
    private readonly HashSet<GeneratedCharacter> _roleSelections = [];
    private readonly HashSet<string> _categorySelections = new(StringComparer.Ordinal) { "trigger" };
    private int _quantityValue = 1;
    private string _transferValue = string.Empty;
    private ChaosCardModel? _previewModel;
    private GeneratedCharacter? _previewCharacter;
    private Control? _modal;
    private bool _activated;
    private bool _mapWasEnabled;
    private bool _deckWasEnabled;
    private NCapstoneContainer? _capstone;
    private Callable _capstoneCallback;
    private ScrollContainer? _catalogScroll;
    private int _catalogScrollPosition;
    private bool _suspendCatalogScrollCapture;
    private int _catalogScrollRestoreRevision;
    private ulong _lastIdentityRerollFrame = ulong.MaxValue;

    internal FreeformEditorOverlay(RunState run)
    {
        _run = run;
        _catalog = CardTinkeringApi.GetComponentPrototypes()
            .Where(item => item.Operation.Template is not "N_SELECT_HAND_CARD" and not "N_SELECT_HAND_ATTACK")
            .ToArray();
        Name = "AutoAnthonyFreeformEditor";
        Color = Bg;
        MouseFilter = MouseFilterEnum.Stop;
        ProcessMode = ProcessModeEnum.Always;
        FitBelowNativeTopBar();
    }

    public override void _Ready() => Activate();

    internal void Activate()
    {
        if (_activated) return;
        _activated = true;
        var top = NRun.Instance?.GlobalUi.TopBar;
        _mapWasEnabled = top?.Map.IsEnabled == true;
        _deckWasEnabled = top?.Deck.IsEnabled == true;
        top?.AnimShow();
        top?.Map.Disable();
        top?.Deck.Enable();
        _capstone = NCapstoneContainer.Instance;
        if (GodotObject.IsInstanceValid(_capstone))
        {
            _capstoneCallback = Callable.From(OnCapstoneChanged);
            _capstone!.Connect(NCapstoneContainer.SignalName.Changed, _capstoneCallback);
        }
        TreeExiting += Deactivate;
        Rebuild();
    }

    private void Deactivate()
    {
        if (!_activated) return;
        _activated = false;
        if (GodotObject.IsInstanceValid(_capstone)
            && _capstone!.IsConnected(NCapstoneContainer.SignalName.Changed, _capstoneCallback))
            _capstone.Disconnect(NCapstoneContainer.SignalName.Changed, _capstoneCallback);
        var top = NRun.Instance?.GlobalUi.TopBar;
        if (_mapWasEnabled) top?.Map.Enable(); else top?.Map.Disable();
        if (_deckWasEnabled) top?.Deck.Enable(); else top?.Deck.Disable();
    }

    private void Rebuild()
    {
        if (!_suspendCatalogScrollCapture && GodotObject.IsInstanceValid(_catalogScroll))
            _catalogScrollPosition = _catalogScroll!.ScrollVertical;
        _catalogScroll = null;
        if (_root is not null)
        {
            RemoveChild(_root);
            _root.QueueFree();
        }
        _root = new VBoxContainer
        {
            Name = "Workbench",
            MouseFilter = MouseFilterEnum.Pass,
            ClipContents = true,
            AnchorRight = 1,
            AnchorBottom = 1,
            OffsetLeft = 24,
            OffsetTop = 12,
            OffsetRight = -24,
            OffsetBottom = -20
        };
        _root.AddThemeConstantOverride("separation", 14);
        AddChild(_root);
        _root.AddChild(Header());
        var columns = new HBoxContainer
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        columns.AddThemeConstantOverride("separation", 14);
        _root.AddChild(columns);
        columns.AddChild(CatalogPanel());
        columns.AddChild(WorkbenchPanel());
        columns.AddChild(BackpackPanel());

        var footer = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        footer.AddThemeConstantOverride("separation", 12);
        _status = LabelText(string.Empty, 20, Muted);
        _status.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        footer.AddChild(_status);
        footer.AddChild(Actions());
        _root.AddChild(footer);
        RefreshStatus();
    }

    private void FitBelowNativeTopBar()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        OffsetTop = NativeTopBarHeight;
    }

    private Control Header()
    {
        var row = new HBoxContainer();
        var title = LabelText(Localize("创造卡牌", "Create Cards"), 36, Text);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(title);
        var hint = LabelText(Localize("左键单选 · 右键多选 · 双击修改数值 · 拖回左栏删除",
            "Left-click single · Right-click multi · Double-click values · Drag left to delete"), 20, Muted);
        hint.CustomMinimumSize = new Vector2(660, 0);
        hint.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        row.AddChild(hint);
        return row;
    }

    private Control CatalogPanel()
    {
        var (frame, body) = Section(Localize("组件目录", "Component Catalog"), CatalogColumnWidth);
        body.AddChild(FilterTabs());

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        _catalogScroll = scroll;
        scroll.GetVScrollBar().ValueChanged += value =>
        {
            if (!_suspendCatalogScrollCapture && GodotObject.IsInstanceValid(scroll))
                _catalogScrollPosition = Math.Max(0, (int)Math.Round(value));
        };
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 6);
        ConfigureDeleteDrop(list);
        scroll.AddChild(list);
        body.AddChild(scroll);

        var matches = _catalog.Select((prototype, index) => (prototype, index))
            .Where(item => _roleSelections.Count == 0
                           || item.prototype.Characters.Any(_roleSelections.Contains))
            .Where(item => _categorySelections.Count == 0
                           || _categorySelections.Contains(CategoryKey(item.prototype.Operation)))
            .GroupBy(item => CategoryKey(item.prototype.Operation))
            .OrderBy(group => CategoryOrder(group.Key));
        foreach (var group in matches)
        {
            list.AddChild(LabelText(CategoryDisplay(group.Key), 22, Accent));
            foreach (var item in group) list.AddChild(CatalogChip(item.prototype, item.index));
        }
        if (_categorySelections.Count == 0 || _categorySelections.Contains("keywords"))
        {
            list.AddChild(LabelText(Localize("关键字", "Keywords"), 22, Accent));
            foreach (var tag in Enum.GetValues<GeneratorCardTag>())
                if (!_session.HasTag(tag)) list.AddChild(PaletteKeyword(TagName(tag), $"t|{tag}"));
            foreach (var id in ComponentKeywordApi.RegisteredKeywordIds)
                if (!_session.HasCustomKeyword(id)) list.AddChild(PaletteKeyword(id, $"u|{id}"));
        }

        ConfigureDeleteDrop(frame);
        RestoreCatalogScrollPosition(scroll, _catalogScrollPosition);
        return frame;
    }

    private Control FilterTabs()
    {
        var layers = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        layers.AddThemeConstantOverride("separation", 5);

        var colors = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        colors.AddThemeConstantOverride("h_separation", 5);
        colors.AddThemeConstantOverride("v_separation", 4);
        colors.AddChild(FilterLayerLabel(Localize("卡色", "Color")));
        colors.AddChild(FilterTab(Localize("全部", "All"), _roleSelections.Count == 0,
            () => { _roleSelections.Clear(); RebuildCatalogAtTop(); },
            () => { _roleSelections.Clear(); RebuildCatalogAtTop(); }));
        foreach (var value in Enum.GetValues<GeneratedCharacter>())
        {
            var selected = _roleSelections.Contains(value);
            colors.AddChild(FilterTab(CharacterTabName(value), selected,
                () =>
                {
                    _roleSelections.Clear();
                    _roleSelections.Add(value);
                    RebuildCatalogAtTop();
                }, () =>
                {
                    if (!_roleSelections.Add(value)) _roleSelections.Remove(value);
                    RebuildCatalogAtTop();
                }));
        }
        layers.AddChild(colors);

        var categories = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        categories.AddThemeConstantOverride("h_separation", 5);
        categories.AddThemeConstantOverride("v_separation", 4);
        categories.AddChild(FilterLayerLabel(Localize("类别", "Type")));
        foreach (var category in Categories)
        {
            var all = category.Key == "all";
            var selected = all ? _categorySelections.Count == 0 : _categorySelections.Contains(category.Key);
            categories.AddChild(FilterTab(CategoryTabName(category.Key), selected,
                () =>
                {
                    _categorySelections.Clear();
                    if (!all) _categorySelections.Add(category.Key);
                    RebuildCatalogAtTop();
                }, () =>
                {
                    if (all) _categorySelections.Clear();
                    else if (!_categorySelections.Add(category.Key)) _categorySelections.Remove(category.Key);
                    RebuildCatalogAtTop();
                }));
        }
        layers.AddChild(categories);
        return layers;
    }

    private static Control FilterLayerLabel(string text)
    {
        var label = LabelText(text, 16, Muted, HorizontalAlignment.Center);
        label.CustomMinimumSize = new Vector2(48, 35);
        return label;
    }

    private static Button FilterTab(string text, bool selected, Action leftClick, Action rightClick)
    {
        var background = selected ? new Color("315c75") : new Color("302b39");
        var border = selected ? Accent : new Color("5c5368");
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, 35),
            FocusMode = FocusModeEnum.None,
            TooltipText = Localize("左键仅选此项；右键切换多选。",
                "Left-click to select only this; right-click to toggle multi-select.")
        };
        button.AddThemeFontSizeOverride("font_size", 16);
        button.AddThemeColorOverride("font_color", selected ? Text : Muted);
        button.AddThemeStyleboxOverride("normal", Box(background, border, 2, 7));
        button.AddThemeStyleboxOverride("hover", Box(background.Lightened(.10f), Accent, 2, 7));
        button.AddThemeStyleboxOverride("pressed", Box(background.Lightened(.16f), Accent, 2, 7));
        button.Pressed += leftClick;
        button.GuiInput += input =>
        {
            if (input is not InputEventMouseButton
                { ButtonIndex: MouseButton.Right, Pressed: true }) return;
            rightClick();
            button.AcceptEvent();
        };
        return button;
    }

    private void RebuildCatalogAtTop()
    {
        _catalogScrollPosition = 0;
        _suspendCatalogScrollCapture = true;
        Rebuild();
    }

    private void RestoreCatalogScrollPosition(ScrollContainer scroll, int position)
    {
        position = Math.Max(0, position);
        _catalogScrollPosition = position;
        _suspendCatalogScrollCapture = true;
        var revision = ++_catalogScrollRestoreRevision;
        scroll.ScrollVertical = position;
        void Restore(int passes)
        {
            Callable.From(() =>
            {
                if (!GodotObject.IsInstanceValid(scroll)) return;
                scroll.ScrollVertical = position;
                if (passes > 1) Restore(passes - 1);
                else if (revision == _catalogScrollRestoreRevision) _suspendCatalogScrollCapture = false;
            }).CallDeferred();
        }
        Restore(3);
    }

    private Control CatalogChip(CardTinkeringComponentPrototype prototype, int index)
    {
        var roles = string.Join(" / ", prototype.Characters.Select(CharacterName));
        var (panel, hitbox) = ComponentChip(prototype.Operation,
            ComponentMarkers(prototype.Operation, component: null, installed: false, roles));
        hitbox.TooltipText = Localize("拖入组件区添加；双击添加并调整数值。",
            "Drag into Component Effects to add; double-click to add and edit values.");
        hitbox.GuiInput += input =>
        {
            if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, DoubleClick: true, Pressed: true })
            {
                if (!_session.CanAdd(prototype, null, null, out var reason))
                {
                    SetStatus(reason, Bad);
                    return;
                }
                var added = _session.Add(prototype);
                var values = CardTinkeringApi.GetEditableValues(added.Operation);
                if (values.Count > 0) ShowValueEditor(added.Id, removeOnCancel: true);
                else Rebuild();
            }
        };
        hitbox.SetDragForwarding(
            Callable.From<Vector2, Variant>(_ =>
            {
                var preview = Chip(ComponentText(prototype.Operation), CategoryColor(CategoryKey(prototype.Operation)));
                preview.CustomMinimumSize = new Vector2(ComponentColumnWidth, 54);
                hitbox.SetDragPreview(preview);
                return Variant.From($"p|{index}");
            }), Callable.From<Vector2, Variant, bool>((_, data) => CanDeleteData(data)),
            Callable.From<Vector2, Variant>((_, data) => DeleteData(data)));
        return panel;
    }

    private Control WorkbenchPanel()
    {
        var (frame, body) = Section(Localize("当前卡牌", "Current Card"), WorkbenchColumnWidth, true);
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
        previewColumn.AddThemeConstantOverride("separation", 10);
        previewColumn.AddChild(CapacityBlock());
        previewColumn.AddChild(CardFace());
        previewColumn.AddChild(ShellControls());
        work.AddChild(previewColumn);

        var editorColumn = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(ComponentColumnWidth, 0),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        var heading = LabelText(Localize("组件效果", "Component Effects"), 24, Text,
            HorizontalAlignment.Center);
        editorColumn.AddChild(heading);
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        var tree = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        tree.AddThemeConstantOverride("separation", 9);
        AddBranch(tree, null, 0);
        scroll.AddChild(tree);
        editorColumn.AddChild(scroll);
        _detail = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            Text = Localize("选择组件查看效果；双击可修改数值。",
                "Select a component to inspect it; double-click to edit values."),
            CustomMinimumSize = new Vector2(0, 78),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _detail.AddThemeFontSizeOverride("normal_font_size", 19);
        _detail.AddThemeColorOverride("default_color", Muted);
        editorColumn.AddChild(_detail);
        work.AddChild(editorColumn);
        return frame;
    }

    private Control CapacityBlock()
    {
        var budgets = _session.BudgetAmounts();
        var overflow = budgets.Any(line => line.OccupiedValue > line.Capacity + 0.0001d);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", Box(overflow ? new Color("4b3826") : new Color("20392e"),
            overflow ? new Color("ffbd6a") : Good, 2, 9));
        var value = string.Join("\n", budgets.Select(line =>
            $"{(line.Label.Length > 0 ? line.Label + "  " : string.Empty)}{BudgetNumber(line.OccupiedValue)} / {BudgetNumber(line.Capacity)}"));
        var label = LabelText(value, budgets.Count > 1 ? 22 : 27, overflow ? new Color("ffbd6a") : Good,
            HorizontalAlignment.Center);
        label.TooltipText = Localize("当前预算 / 当前容量。创造卡牌允许超出容量，此处仅用于核对数值。",
            "Current budget / current capacity. Card creation permits overflow; this is informational.");
        panel.AddChild(Padded(label, 16, 8));
        return panel;
    }

    private Control CardFace()
    {
        const float scale = .70f;
        var size = new Vector2(300f * scale, 422f * scale);
        var cardSurface = new Control
        {
            CustomMinimumSize = size,
            Size = size,
            MouseFilter = MouseFilterEnum.Pass,
            ClipContents = false
        };
        cardSurface.TooltipText = Localize("右键：根据当前效果重新随机卡名与卡图",
            "Right-click: reroll the name and portrait for the current effects");
        cardSurface.GuiInput += input =>
            HandleIdentityRerollInput(input);
        try
        {
            var definition = _session.Preview;
            if (_previewModel is null || _previewCharacter != definition.Character)
            {
                _previewModel = AutoAnthonyFreeformCardApi.CreatePreview(
                    _run.Players.Single(), definition, _session.PortraitPath);
                TinkeringStateStore.MarkEditorPreview(_previewModel);
                _previewCharacter = definition.Character;
            }
            else
            {
                _previewModel.ApplyFreeformDefinition(definition);
                _previewModel.FreeformPortraitPath = _session.PortraitPath ?? string.Empty;
            }
            var cardNode = NCard.Create(_previewModel)
                           ?? throw new InvalidOperationException("The native card node could not be created.");
            var holder = NPreviewCardHolder.Create(cardNode, showHoverTips: true, scaleOnHover: false)
                         ?? throw new InvalidOperationException("The native preview holder could not be created.");
            holder.SetCardScale(Vector2.One * scale);
            holder.SetClickable(false);
            holder.MouseFilter = MouseFilterEnum.Pass;
            if (holder.GetNodeOrNull<Control>("%Hitbox") is { } hitbox)
            {
                hitbox.TooltipText = cardSurface.TooltipText;
                hitbox.GuiInput += HandleIdentityRerollInput;
            }
            cardSurface.AddChild(holder);
            holder.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(cardNode) && cardNode.IsInsideTree())
                    cardNode.UpdateVisuals(PileType.Deck, CardPreviewMode.Normal);
            }).CallDeferred();
        }
        catch (Exception exception)
        {
            Log.Warn($"[CardTinkering] Freeform preview failed: {exception.Message}");
            var fallback = LabelText(Localize("预览暂不可用", "Preview unavailable"), 22, Bad,
                HorizontalAlignment.Center);
            fallback.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            cardSurface.AddChild(fallback);
        }
        return cardSurface;
    }

    private void HandleIdentityRerollInput(InputEvent input)
    {
        if (input is not InputEventMouseButton
            { ButtonIndex: MouseButton.Right, Pressed: true }) return;
        var frame = Engine.GetProcessFrames();
        if (_lastIdentityRerollFrame == frame) return;
        _lastIdentityRerollFrame = frame;
        GetViewport().SetInputAsHandled();
        if (_session.TryRerollIdentity(out var reason))
        {
            Log.Info("[CardTinkering] Rerolled the Create Cards editor identity.");
            _previewModel = null;
            _previewCharacter = null;
            Rebuild();
            SetStatus(Localize("已根据当前效果重新随机卡名与卡图。",
                "Rerolled the name and portrait for the current effects."), Good);
        }
        else SetStatus(reason, Bad);
    }

    private Control ShellControls()
    {
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChoice(grid, Localize("卡色", "Color"), Enum.GetValues<GeneratedCharacter>().Select(CharacterName).ToArray(),
            (int)_session.Shell.Character, index => { _session.SetCharacter((GeneratedCharacter)index); Rebuild(); });
        AddChoice(grid, Localize("稀有度", "Rarity"), Enum.GetValues<GeneratedRarity>().Select(RarityName).ToArray(),
            (int)_session.Shell.Rarity, index => { _session.SetRarity((GeneratedRarity)index); Rebuild(); });
        AddChoice(grid, Localize("类型", "Type"), Enum.GetValues<GeneratedCardType>().Select(TypeName).ToArray(),
            (int)_session.Shell.Type, index =>
            {
                var removed = _session.SetType((GeneratedCardType)index);
                Rebuild();
                if (removed.Count > 0) SetStatus(Localize($"{removed.Count} 个不兼容组件已移入暂存区。",
                    $"Moved {removed.Count} incompatible components to temporary storage."), new Color("ffbd6a"));
            });
        AddChoice(grid, Localize("能量", "Energy"), ["X", "0", "1", "2", "3", "4", "5", "6", "7", "8", "9"],
            _session.Shell.Cost < 0 ? 0 : _session.Shell.Cost + 1,
            index => { _session.SetEnergyCost(index == 0 ? -1 : index - 1); Rebuild(); });
        AddChoice(grid, Localize("蓝星", "Stars"), [Localize("无", "None"), "X", "0", "1", "2", "3", "4", "5", "6", "7", "8", "9"],
            _session.Shell.HasStarCostX ? 1 : _session.Shell.StarCost < 0 ? 0 : _session.Shell.StarCost + 2,
            index => { _session.SetStarCost(index <= 1 ? -1 : index - 2, index == 1); Rebuild(); });
        grid.AddChild(LabelText(Localize("目标", "Target"), 18, Muted));
        grid.AddChild(LabelText(_session.Preview.Target == TargetMode.SingleEnemy
            ? Localize("选择敌人", "Selected enemy") : Localize("自动", "Automatic"), 18, Text));
        return grid;
    }

    private Control BackpackPanel()
    {
        var (frame, body) = Section(Localize("组件背包", "Component Backpack"),
            ComponentColumnWidth + SectionPadding * 2);
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 7);
        var backpack = _session.Roots(backpack: true).ToArray();
        foreach (var component in backpack)
            list.AddChild(InstalledBranch(component, backpack: true, includeChildSlot: false));
        if (backpack.Length == 0)
        {
            var empty = LabelText(Localize("类型变更时移出的组件会暂存在这里。",
                    "Components removed by a shell-type change are kept here."), 19, Muted,
                HorizontalAlignment.Center);
            empty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            list.AddChild(empty);
        }
        scroll.AddChild(list);
        body.AddChild(scroll);
        return frame;
    }

    private void AddBranch(VBoxContainer container, string? parentId, int depth)
    {
        _ = depth;
        if (parentId is null) AddKeywordRows(container, beforeDescription: true);
        foreach (var component in _session.Roots(parentId))
            container.AddChild(InstalledBranch(component, backpack: false, includeChildSlot: true));
        container.AddChild(DropZone(parentId));
        if (parentId is null) AddKeywordRows(container, beforeDescription: false);
    }

    private Control InstalledBranch(FreeformComponent component, bool backpack, bool includeChildSlot)
    {
        var branch = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        branch.AddThemeConstantOverride("separation", 0);
        branch.AddChild(InstalledChip(component, backpack));
        if (!FreeformSession.CanHaveChildren(component.Operation)) return branch;

        var descendants = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        descendants.AddThemeConstantOverride("separation", 0);
        var palette = CategoryColor(CategoryKey(component.Operation));
        var stem = new PanelContainer
        {
            CustomMinimumSize = new Vector2(18, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        stem.AddThemeStyleboxOverride("panel", LStemBox(palette, palette.Lightened(.38f)));
        descendants.AddChild(stem);
        var margin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_top", 7);
        var children = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        children.AddThemeConstantOverride("separation", 6);
        foreach (var child in _session.Roots(component.Id, backpack))
            children.AddChild(InstalledBranch(child, backpack, includeChildSlot));
        if (includeChildSlot) children.AddChild(DropZone(component.Id));
        margin.AddChild(children);
        descendants.AddChild(margin);
        branch.AddChild(descendants);
        return branch;
    }

    private Control InstalledChip(FreeformComponent component, bool backpack)
    {
        var (panel, hitbox) = ComponentChip(component.Operation,
            ComponentMarkers(component.Operation, component, installed: !backpack,
                CardTinkeringApi.GetEditableValues(component.Operation).Count > 0
                    ? Localize("可调数值", "Editable") : null));
        hitbox.TooltipText = Localize("拖动排序；双击编辑数值", "Drag to arrange; double-click to edit values");
        hitbox.Pressed += () => ShowDetail(component);
        hitbox.MouseEntered += () => ShowDetail(component);
        hitbox.GuiInput += input =>
        {
            if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, DoubleClick: true, Pressed: true })
                ShowValueEditor(component.Id);
        };
        hitbox.SetDragForwarding(
            Callable.From<Vector2, Variant>(_ =>
            {
                var preview = Chip(ComponentText(component.Operation), CategoryColor(CategoryKey(component.Operation)));
                preview.CustomMinimumSize = new Vector2(ComponentColumnWidth, 54);
                hitbox.SetDragPreview(preview);
                return Variant.From($"i|{component.Id}");
            }),
            Callable.From<Vector2, Variant, bool>((_, data) => !backpack
                && CanDrop(data, component.ParentId, component.Id)),
            Callable.From<Vector2, Variant>((_, data) =>
            {
                if (!backpack) Drop(data, component.ParentId, component.Id);
            }));
        return panel;
    }

    private Control DropZone(string? parentId)
    {
        var drop = new Button
        {
            Text = parentId is null ? Localize("＋ 添加到卡牌末尾", "+ Add to end of card")
                : Localize("└ ＋ 添加到这一段", "└ + Add to this branch"),
            CustomMinimumSize = new Vector2(0, 46),
            FocusMode = FocusModeEnum.None
        };
        drop.AddThemeFontSizeOverride("font_size", 18);
        drop.AddThemeColorOverride("font_color", Muted);
        drop.AddThemeStyleboxOverride("normal", Box(new Color("211e29"), new Color("4b4558"), 2, 7));
        drop.AddThemeStyleboxOverride("hover", Box(new Color("25465e"), Accent, 2, 7));
        ConfigureAssemblyDrop(drop, parentId, null);
        return drop;
    }

    private void AddKeywordRows(VBoxContainer container, bool beforeDescription)
    {
        var nativeOrder = beforeDescription ? CardKeywordOrder.beforeDescription : CardKeywordOrder.afterDescription;
        foreach (var keyword in nativeOrder)
        {
            if (!Enum.TryParse<GeneratorCardTag>(keyword.ToString(), out var tag) || !_session.HasTag(tag)) continue;
            container.AddChild(KeywordChip(TagName(tag), $"t|{tag}", deleteTarget: false));
        }
        if (beforeDescription) return;
        var known = CardKeywordOrder.beforeDescription.Concat(CardKeywordOrder.afterDescription)
            .Select(keyword => keyword.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (var tag in _session.Shell.Tags.Where(tag => !known.Contains(tag.ToString())))
            container.AddChild(KeywordChip(TagName(tag), $"t|{tag}", deleteTarget: false));
        foreach (var id in _session.Shell.CustomKeywords ?? [])
            container.AddChild(KeywordChip(id, $"u|{id}", deleteTarget: false));
    }

    private void ShowDetail(FreeformComponent component)
    {
        if (_detail is null) return;
        var category = CategoryDisplay(CategoryKey(component.Operation));
        var editable = CardTinkeringApi.GetEditableValues(component.Operation).Count;
        var values = _session.ComponentValues(component.Id);
        var fallback = values.FirstOrDefault()?.Analysis
                       ?? CardTinkeringApi.AnalyzeStandaloneComponent(component.Operation);
        var formula = _session.ComponentFormula(component.Id, fallback, values);
        var summary = string.Join(Localize("　", " · "),
            ComponentMarkers(component.Operation, component, installed: values.Count > 0)
                .Where(marker => marker != Localize("可调数值", "Editable")));
        var detail = $"{category}{(editable > 0 ? Localize("　双击调整数值", " · Double-click to edit values") : string.Empty)}　{ComponentText(component.Operation)}";
        if (summary.Length > 0) detail += "\n" + summary;
        detail = SpecialValuePricing.AppendExplanation(detail, formula);
        _detail.Text = TrainingRoomOverlay.ComponentMarkupForPrefix(detail,
            RunManager.Instance.GetLocalCharacterEnergyIconPrefix() ?? "colorless");
        _detail.AddThemeColorOverride("default_color", Text);
    }

    private IReadOnlyList<string> ComponentMarkers(GeneratorOperation operation, FreeformComponent? component,
        bool installed, string? trailing = null)
    {
        var currentValues = installed && component is not null ? _session.ComponentValues(component.Id) : [];
        var fallback = currentValues.FirstOrDefault()?.Analysis
                       ?? CardTinkeringApi.AnalyzeStandaloneComponent(operation);
        IReadOnlyList<FormulaBadge> formula;
        if (installed && component is not null)
            formula = _session.ComponentFormula(component.Id, fallback, currentValues);
        else
        {
            var downside = new ComponentDownsidePricing(fallback.IsNegative,
                fallback.DownsideMultiplier, fallback.LinearCompensation);
            formula = SpecialValuePricing.Resolve(null, [operation], 0, fallback, downside);
        }

        var markers = new List<string>();
        var isTrigger = FreeformSession.CanHaveChildren(operation);
        if (isTrigger)
            markers.Add(TriggerValueText(component is not null && installed
                ? _session.TriggerMultipliers(component.Id) : [], fallback));
        else if (formula.FirstOrDefault(badge => badge.Role == FormulaBadgeRole.Positive) is { } special)
            markers.Add($"{SpecialValuePricing.FormulaName(special.Expression)}={SpecialValuePricing.FormulaExpression(special.Expression)}");
        else if (!fallback.IsNegative || SpecialValuePricing.DisplayValue(fallback, installed) > 0.0001d)
            markers.Add(ComponentValueText(currentValues, fallback, installed));

        if (currentValues.Count > 0)
        {
            var linear = currentValues.Select(line => line.Analysis.LinearCompensation).ToArray();
            if (linear.Any(value => Math.Abs(value) > 0.0001d))
                markers.Add(linear.Length == 1 || linear.All(value => Math.Abs(value - linear[0]) < 0.0001d)
                    ? Localize("负代价 ", "Downside ") + SignedNumber(-linear[0])
                    : Localize("负代价 f(X)=", "Downside f(X)=") + SignedNumber(-linear[0]) + "/"
                      + SignedNumber(-linear[1]));
            var multipliers = currentValues.Select(line => line.Analysis.DownsideMultiplier).ToArray();
            if (multipliers.Any(value => Math.Abs(value - 1d) > 0.0001d))
                markers.Add(Localize("全卡倍率 ", "Global multiplier ")
                            + (multipliers.Length == 1
                               || multipliers.All(value => Math.Abs(value - multipliers[0]) < 0.0001d)
                                ? "1/x" + MultiplierNumber(multipliers[0])
                                : "f(X)=1/x" + MultiplierNumber(multipliers[0]) + "/1/x"
                                  + MultiplierNumber(multipliers[1])));
        }
        else
        {
            foreach (var badge in formula.Where(badge => badge.Role == FormulaBadgeRole.Downside))
                markers.Add(badge.Expression.StartsWith("1/x", StringComparison.Ordinal)
                    ? Localize("全卡倍率 ", "Global multiplier ") + badge.Expression
                    : badge.Expression);
            if (fallback.IsNegative && formula.All(badge => badge.Role != FormulaBadgeRole.Downside))
                markers.Add(Localize("负代价 0", "Downside 0"));
        }
        if (TrainingSession.IsWholeCardUnique(operation))
            markers.Add(Localize("整卡唯一", "Card-unique"));
        if (!string.IsNullOrWhiteSpace(trailing)) markers.Add(trailing);
        return markers;
    }

    private static string TriggerValueText(IReadOnlyList<TriggerAnalysisLine> lines,
        CardTinkeringComponentAnalysis fallback)
    {
        if (lines.Count == 0)
            return Localize($"触发倍率 ×{MultiplierNumber(fallback.TriggerMultiplier)}",
                $"Trigger ×{MultiplierNumber(fallback.TriggerMultiplier)}");
        var values = lines.Select(line => line.Own).ToArray();
        var amount = values.Length == 1 || values.All(value => Math.Abs(value - values[0]) < 0.0001d)
            ? "×" + MultiplierNumber(values[0])
            : "f(X)=×" + MultiplierNumber(values[0]) + "/×" + MultiplierNumber(values[1]);
        return Localize("触发倍率 ", "Trigger ") + amount;
    }

    private static string ComponentValueText(IReadOnlyList<ComponentAnalysisLine> currentValues,
        CardTinkeringComponentAnalysis fallback, bool installed)
    {
        double Display(CardTinkeringComponentAnalysis analysis) =>
            SpecialValuePricing.DisplayValue(analysis, installed);
        if (currentValues.Count == 0)
            return Localize("价值 ", "Value ") + BudgetNumber(Display(fallback));
        var values = currentValues.Select(line => Display(line.Analysis)).ToArray();
        return values.Length == 1 || values.All(value => Math.Abs(value - values[0]) < 0.0001d)
            ? Localize("价值 ", "Value ") + BudgetNumber(values[0])
            : Localize("价值 f(X)=", "Value f(X)=") + BudgetNumber(values[0]) + "/"
              + BudgetNumber(values[1]);
    }

    private Control PaletteKeyword(string text, string data)
    {
        var button = KeywordChip(text, data, deleteTarget: true);
        button.Pressed += () => AddKeyword(data);
        return button;
    }

    private Button KeywordChip(string text, string data, bool deleteTarget)
    {
        var header = Localize("关键字　外壳", "Keyword · Shell");
        if (data.StartsWith("t|", StringComparison.Ordinal)
            && Enum.TryParse<GeneratorCardTag>(data[2..], out var tag))
        {
            var values = _session.KeywordValues(tag, includeWhenMissing: deleteTarget);
            if (values.FirstOrDefault()?.PositiveValue is > .0001d and var positive)
                header += Localize("　价值 ", " · Value ") + BudgetNumber(positive);
            var multipliers = values.Select(line => line.DownsideMultiplier).ToArray();
            if (multipliers.Any(value => Math.Abs(value - 1d) > .0001d))
                header += Localize("　全卡倍率 ", " · Global multiplier ")
                          + (multipliers.Length == 1
                             || multipliers.All(value => Math.Abs(value - multipliers[0]) < .0001d)
                              ? "1/x" + MultiplierNumber(multipliers[0])
                              : "f(X)=1/x" + MultiplierNumber(multipliers[0]) + "/1/x"
                                + MultiplierNumber(multipliers[1]));
        }
        var button = Chip($"{header}\n{text}", new Color("6d5424"));
        button.CustomMinimumSize = new Vector2(0, 72);
        button.SetDragForwarding(Callable.From<Vector2, Variant>(_ =>
            {
                var preview = Chip(text, new Color("6d5424"));
                button.SetDragPreview(preview);
                return Variant.From(data);
            }), Callable.From<Vector2, Variant, bool>((_, incoming) => deleteTarget && CanDeleteData(incoming)),
            Callable.From<Vector2, Variant>((_, incoming) =>
            {
                if (deleteTarget) DeleteData(incoming);
            }));
        return button;
    }

    private Control Actions()
    {
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        row.AddThemeConstantOverride("separation", 8);
        var transfer = ActionButton(Localize("导入 / 导出", "Import / Export"));
        transfer.Pressed += ShowTransferDialog;
        row.AddChild(transfer);
        var quantity = ActionButton(Localize($"数量 {_quantityValue}", $"Copies {_quantityValue}"));
        quantity.Pressed += ShowQuantityDialog;
        row.AddChild(quantity);
        var valid = _session.Validation.IsValid;
        if (CombatManager.Instance.IsInProgress)
        {
            var hand = ActionButton(Localize("加入手牌", "Add to Hand"), true);
            hand.Disabled = !valid;
            hand.Pressed += () => AddCards(PileType.Hand);
            row.AddChild(hand);
            var draw = ActionButton(Localize("加入抽牌堆", "Add to Draw Pile"), true);
            draw.Disabled = !valid;
            draw.Pressed += () => AddCards(PileType.Draw);
            row.AddChild(draw);
        }
        var deck = ActionButton(Localize("加入牌组", "Add to Deck"), true);
        deck.Disabled = !valid;
        deck.Pressed += () => AddCards(PileType.Deck);
        row.AddChild(deck);
        var close = ActionButton(Localize("关闭", "Close"));
        close.Pressed += () => { Deactivate(); QueueFree(); };
        row.AddChild(close);
        return row;
    }

    private void ShowQuantityDialog()
    {
        var (shade, body) = Modal(Localize("生成数量", "Number of Copies"), 460);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddChild(LabelText(Localize("数量", "Copies"), 21, Text));
        var quantity = new SpinBox
        {
            MinValue = 1,
            MaxValue = 99,
            Value = _quantityValue,
            CustomMinimumSize = new Vector2(150, 50),
            AllowGreater = false,
            AllowLesser = false
        };
        row.AddChild(quantity);
        body.AddChild(row);
        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        var cancel = ActionButton(Localize("取消", "Cancel"));
        cancel.Pressed += CloseModal;
        actions.AddChild(cancel);
        var apply = ActionButton(Localize("应用", "Apply"), true);
        apply.Pressed += () =>
        {
            _quantityValue = Math.Clamp((int)quantity.Value, 1, 99);
            CloseModal();
            Rebuild();
        };
        actions.AddChild(apply);
        body.AddChild(actions);
        _modal = shade;
        AddChild(shade);
    }

    private void ShowTransferDialog()
    {
        var (shade, body) = Modal(Localize("导入 / 导出", "Import / Export"), 880);
        _transferText = new TextEdit
        {
            CustomMinimumSize = new Vector2(0, 150),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            PlaceholderText = Localize("粘贴 AACT-FREE-1: 开头的字符串", "Paste a string beginning with AACT-FREE-1:"),
            Text = _transferValue
        };
        _transferText.TextChanged += () => _transferValue = _transferText.Text;
        body.AddChild(_transferText);
        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        var close = ActionButton(Localize("关闭", "Close"));
        close.Pressed += CloseModal;
        actions.AddChild(close);
        var export = ActionButton(Localize("导出并复制", "Export & Copy"));
        export.Pressed += Export;
        actions.AddChild(export);
        var import = ActionButton(Localize("导入", "Import"), true);
        import.Pressed += Import;
        actions.AddChild(import);
        body.AddChild(actions);
        _modal = shade;
        AddChild(shade);
    }

    private (ColorRect Shade, VBoxContainer Body) Modal(string title, float width)
    {
        CloseModal();
        var shade = new ColorRect
        {
            Color = new Color(0, 0, 0, .78f),
            ZIndex = 500,
            MouseFilter = MouseFilterEnum.Stop
        };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        shade.AddChild(center);
        var frame = new PanelContainer { CustomMinimumSize = new Vector2(width, 0) };
        frame.AddThemeStyleboxOverride("panel", Box(new Color("2b2735"), Accent, 2, 12));
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 16);
        body.AddChild(LabelText(title, 30, Text, HorizontalAlignment.Center));
        frame.AddChild(Padded(body, 26));
        center.AddChild(frame);
        return (shade, body);
    }

    private async void AddCards(PileType pile)
    {
        try
        {
            var definition = _session.Preview;
            var validation = _session.Validation;
            if (!validation.IsValid)
            {
                SetStatus(string.Join("; ", validation.Errors.Select(TrainingSession.LocalizedReason)), Bad);
                return;
            }
            var count = Math.Clamp(_quantityValue, 1, 99);
            var owner = _run.Players.Single();
            for (var index = 0; index < count; index++)
            {
                if (pile == PileType.Deck)
                    await CardPileCmd.Add(AutoAnthonyFreeformCardApi.CreateForDeck(
                        owner, definition, _session.PortraitPath), PileType.Deck);
                else
                {
                    var card = AutoAnthonyFreeformCardApi.CreateForCombat(owner, definition);
                    card.FreeformPortraitPath = _session.PortraitPath ?? string.Empty;
                    await CardPileCmd.AddGeneratedCardToCombat(
                        card, pile, owner);
                }
            }
            if (pile == PileType.Deck)
                _ = TaskHelper.RunSafely(SaveManager.Instance.SaveRun(_run.CurrentRoom));
            SetStatus(Localize($"已将 {count} 张牌加入{PileName(pile)}。",
                $"Added {count} card(s) to the {PileName(pile)}."), Good);
        }
        catch (Exception exception)
        {
            Log.Error($"[CardTinkering] Freeform card insertion failed: {exception}");
            SetStatus(Localize("加入失败，请查看日志。", "Could not add the card; see the log."), Bad);
        }
    }

    private void Export()
    {
        try
        {
            var json = CardTinkeringApi.SerializeCard(_session.Preview);
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var text = ExportPrefix + encoded;
            if (_transferText is not null) _transferText.Text = text;
            _transferValue = text;
            DisplayServer.ClipboardSet(text);
            SetStatus(Localize("已导出并复制到剪贴板。", "Exported and copied to the clipboard."), Good);
        }
        catch (Exception exception)
        {
            Log.Warn($"[CardTinkering] Freeform export failed: {exception.Message}");
            SetStatus(Localize("导出失败，请查看日志。", "Export failed; see the log."), Bad);
        }
    }

    private void Import()
    {
        try
        {
            var text = _transferText?.Text.Trim() ?? string.Empty;
            if (text.Length == 0) text = DisplayServer.ClipboardGet().Trim();
            if (!text.StartsWith(ExportPrefix, StringComparison.Ordinal))
                throw new InvalidDataException(Localize("不支持的创造卡牌版本。",
                    "Unsupported created-card version."));
            var encoded = text[ExportPrefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            _session.Import(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            _previewModel = null;
            _previewCharacter = null;
            CloseModal();
            Rebuild();
            SetStatus(Localize("导入成功。", "Import complete."), Good);
        }
        catch (Exception exception)
        {
            Log.Warn($"[CardTinkering] Freeform import failed: {exception.Message}");
            SetStatus(Localize("导入失败：字符串无效或版本不受支持。",
                "Import failed: the string is invalid or unsupported."), Bad);
        }
    }

    private void ShowValueEditor(string componentId, bool removeOnCancel = false)
    {
        var component = _session.Get(componentId);
        var values = CardTinkeringApi.GetEditableValues(component.Operation);
        if (values.Count == 0)
        {
            SetStatus(Localize("这个组件没有可手动调整的数值。", "This component has no editable values."), Muted);
            return;
        }
        if (_modal is not null && GodotObject.IsInstanceValid(_modal)) _modal.QueueFree();
        var shade = new ColorRect { Color = new Color(0, 0, 0, .78f), ZIndex = 500, MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        shade.AddChild(center);
        var frame = new PanelContainer { CustomMinimumSize = new Vector2(620, 0) };
        frame.AddThemeStyleboxOverride("panel", Box(new Color("2b2735"), Accent, 2, 12));
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 12);
        body.AddChild(LabelText(Localize("调整组件数值", "Edit Component Values"), 28, Text,
            HorizontalAlignment.Center));
        var description = LabelText(ComponentText(component.Operation), 20, Muted, HorizontalAlignment.Center);
        description.CustomMinimumSize = new Vector2(540, 0);
        description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(description);
        var controls = new List<(string Id, SpinBox Spin)>();
        foreach (var value in values)
        {
            var row = new HBoxContainer();
            var label = LabelText(value.IsXOffset
                ? Localize($"{ValueName(value.Id)} · X 偏移", $"{ValueName(value.Id)} · X offset")
                : ValueName(value.Id), 20, Text);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(label);
            var spin = new SpinBox
            {
                MinValue = value.Minimum, MaxValue = value.Maximum, Value = value.Value,
                CustomMinimumSize = new Vector2(190, 48), AllowGreater = false, AllowLesser = false
            };
            row.AddChild(spin);
            controls.Add((value.Id, spin));
            body.AddChild(row);
        }
        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        var cancel = ActionButton(Localize("取消", "Cancel"));
        cancel.Pressed += () =>
        {
            if (removeOnCancel) _session.Delete(componentId);
            CloseModal();
            if (removeOnCancel) Rebuild();
        };
        actions.AddChild(cancel);
        var apply = ActionButton(Localize("应用", "Apply"), true);
        apply.Pressed += () =>
        {
            foreach (var control in controls)
                _session.TrySetValue(componentId, control.Id, (int)control.Spin.Value);
            CloseModal();
            Rebuild();
        };
        actions.AddChild(apply);
        body.AddChild(actions);
        frame.AddChild(Padded(body, 24));
        center.AddChild(frame);
        _modal = shade;
        AddChild(shade);
    }

    private void CloseModal()
    {
        if (_modal is not null && GodotObject.IsInstanceValid(_modal)) _modal.QueueFree();
        _modal = null;
    }

    private bool CanDrop(Variant data, string? parentId, string? beforeId)
    {
        if (data.VariantType != Variant.Type.String) return false;
        var value = data.AsString();
        if (value.StartsWith("p|", StringComparison.Ordinal)
            && int.TryParse(value.AsSpan(2), out var index) && (uint)index < (uint)_catalog.Count)
            return _session.CanAdd(_catalog[index], parentId, beforeId, out _);
        if (value.StartsWith("i|", StringComparison.Ordinal)) return true;
        return value.StartsWith("t|", StringComparison.Ordinal) || value.StartsWith("u|", StringComparison.Ordinal);
    }

    private void Drop(Variant data, string? parentId, string? beforeId)
    {
        if (data.VariantType != Variant.Type.String) return;
        var value = data.AsString();
        if (value.StartsWith("p|", StringComparison.Ordinal)
            && int.TryParse(value.AsSpan(2), out var index) && (uint)index < (uint)_catalog.Count)
        {
            if (_session.CanAdd(_catalog[index], parentId, beforeId, out var reason))
            {
                _session.Add(_catalog[index], parentId, beforeId);
                Rebuild();
            }
            else SetStatus(reason, Bad);
            return;
        }
        if (value.StartsWith("i|", StringComparison.Ordinal))
        {
            if (_session.TryMove(value[2..], parentId, beforeId, false, out var reason)) Rebuild();
            else SetStatus(reason, Bad);
            return;
        }
        AddKeyword(value);
    }

    private void AddKeyword(string data)
    {
        if (data.StartsWith("t|", StringComparison.Ordinal)
            && Enum.TryParse<GeneratorCardTag>(data[2..], out var tag) && !_session.HasTag(tag))
            _session.ToggleTag(tag);
        else if (data.StartsWith("u|", StringComparison.Ordinal) && !_session.HasCustomKeyword(data[2..]))
            _session.ToggleCustomKeyword(data[2..]);
        Rebuild();
    }

    private void ConfigureAssemblyDrop(Control target, string? parentId, string? beforeId) =>
        target.SetDragForwarding(Callable.From<Vector2, Variant>(_ => default),
            Callable.From<Vector2, Variant, bool>((_, data) => CanDrop(data, parentId, beforeId)),
            Callable.From<Vector2, Variant>((_, data) => Drop(data, parentId, beforeId)));

    private static bool CanDeleteData(Variant data) => data.VariantType == Variant.Type.String
        && (data.AsString().StartsWith("i|", StringComparison.Ordinal)
            || data.AsString().StartsWith("t|", StringComparison.Ordinal)
            || data.AsString().StartsWith("u|", StringComparison.Ordinal));

    private void DeleteData(Variant data)
    {
        if (data.VariantType != Variant.Type.String) return;
        var value = data.AsString();
        if (value.StartsWith("i|", StringComparison.Ordinal)) _session.Delete(value[2..]);
        else if (value.StartsWith("t|", StringComparison.Ordinal)
                 && Enum.TryParse<GeneratorCardTag>(value[2..], out var tag) && _session.HasTag(tag))
            _session.ToggleTag(tag);
        else if (value.StartsWith("u|", StringComparison.Ordinal) && _session.HasCustomKeyword(value[2..]))
            _session.ToggleCustomKeyword(value[2..]);
        Rebuild();
    }

    private void ConfigureDeleteDrop(Control target) => target.SetDragForwarding(
        Callable.From<Vector2, Variant>(_ => default),
        Callable.From<Vector2, Variant, bool>((_, data) => CanDeleteData(data)),
        Callable.From<Vector2, Variant>((_, data) => DeleteData(data)));

    private void OnCapstoneChanged()
    {
        var global = NRun.Instance?.GlobalUi;
        if (global is null || !GodotObject.IsInstanceValid(this)) return;
        MoveBelow(global.CapstoneContainer.InUse ? global.CapstoneContainer : global.TargetManager);
    }

    private void MoveBelow(Node sibling)
    {
        if (GetParent() is not Node parent || sibling.GetParent() != parent) return;
        var index = sibling.GetIndex();
        if (GetIndex() < index) index--;
        parent.MoveChild(this, index);
    }

    private void RefreshStatus()
    {
        var result = _session.Validation;
        SetStatus(result.IsValid ? Localize("✓ 符合东尼算法组合规则", "✓ Valid Auto Anthony assembly")
            : string.Join("; ", result.Errors.Take(3).Select(TrainingSession.LocalizedReason)),
            result.IsValid ? Good : Bad);
    }

    private void SetStatus(string value, Color color)
    {
        if (_status is null) return;
        _status.Text = value;
        _status.AddThemeColorOverride("font_color", color);
    }

    private static string ComponentText(GeneratorOperation operation)
    {
        string text;
        if (!IsEnglish) text = operation.ChineseText;
        else if (operation.LocalizedText?.RenderEnglish(OperationRuntimeSpecCompiler.GetOrCompile(operation)) is { } english)
            text = english;
        else text = EnglishCardDescriptionRenderer.Render([operation]).Trim();
        return text;
    }

    private static readonly (string Key, string Display)[] Categories =
    [
        ("all", Localize("全部类别", "All Categories")),
        ("trigger", Localize("触发与条件", "Triggers & Conditions")),
        ("damage", Localize("伤害", "Damage")),
        ("block", Localize("格挡与防御", "Block & Defense")),
        ("resource", Localize("费用与资源", "Costs & Resources")),
        ("cards", Localize("抽牌与牌堆", "Cards & Piles")),
        ("growth", Localize("成长与恢复", "Growth & Recovery")),
        ("downside", Localize("代价", "Downsides")),
        ("keywords", Localize("关键字", "Keywords")),
        ("utility", Localize("其它效果", "Other Effects"))
    ];

    private static string CategoryKey(GeneratorOperation operation)
    {
        if (FreeformSession.CanHaveChildren(operation)) return "trigger";
        if (CardEffectRules.IsEnemyDamage(operation)) return "damage";
        if (operation.Template.Contains("Block", StringComparison.OrdinalIgnoreCase)
            || operation.Template is "N:B") return "block";
        if (operation.Template.Contains("Energy", StringComparison.OrdinalIgnoreCase)
            || operation.Template.Contains("Star", StringComparison.OrdinalIgnoreCase)
            || operation.Template.Contains("Cost", StringComparison.OrdinalIgnoreCase)) return "resource";
        if (CardEffectRules.IsCardDrawEffect(operation)
            || operation.Template.Contains("Discard", StringComparison.OrdinalIgnoreCase)
            || operation.Template.Contains("Exhaust", StringComparison.OrdinalIgnoreCase)
            || operation.CardTargetSlot is not null) return "cards";
        if (CardEffectRules.IsHealingOrMaxHp(operation)
            || CardEffectRules.IsPermanentStrengthOrDexterityChange(operation)) return "growth";
        if (CardEffectRules.IsNegativeEffect(operation)) return "downside";
        return "utility";
    }

    private static int CategoryOrder(string key) => Array.FindIndex(Categories, item => item.Key == key);
    private static string CategoryDisplay(string key) => Categories.First(item => item.Key == key).Display;
    private static string CategoryTabName(string key) => key switch
    {
        "all" => Localize("全部", "All"), "trigger" => Localize("触发", "Triggers"),
        "damage" => Localize("伤害", "Damage"), "block" => Localize("格挡", "Block"),
        "resource" => Localize("资源", "Resource"), "cards" => Localize("牌堆", "Cards"),
        "growth" => Localize("成长", "Growth"), "downside" => Localize("代价", "Downside"),
        "keywords" => Localize("关键字", "Keywords"), _ => Localize("其他", "Other")
    };
    private static Color CategoryColor(string key) => key switch
    {
        "trigger" => new Color("453561"), "damage" => new Color("572f37"),
        "block" => new Color("253f5a"), "resource" => new Color("55431f"),
        "cards" => new Color("214b48"), "growth" => new Color("334c31"),
        "downside" => new Color("4b3430"), _ => new Color("343944")
    };

    private static string CharacterName(GeneratedCharacter value) => value switch
    {
        GeneratedCharacter.Ironclad => Localize("铁甲战士", "Ironclad"),
        GeneratedCharacter.Silent => Localize("静默猎手", "Silent"),
        GeneratedCharacter.Defect => Localize("故障机器人", "Defect"),
        GeneratedCharacter.Necrobinder => Localize("亡灵契约师", "Necrobinder"),
        GeneratedCharacter.Regent => Localize("储君", "Regent"),
        _ => Localize("无色", "Colorless")
    };
    private static string CharacterTabName(GeneratedCharacter value) => value switch
    {
        GeneratedCharacter.Ironclad => Localize("铁甲", "Ironclad"),
        GeneratedCharacter.Silent => Localize("静默", "Silent"),
        GeneratedCharacter.Defect => Localize("故障", "Defect"),
        GeneratedCharacter.Necrobinder => Localize("亡灵", "Necro"),
        GeneratedCharacter.Regent => Localize("储君", "Regent"),
        _ => Localize("无色", "Colorless")
    };
    private static string TypeName(GeneratedCardType value) => value switch
    {
        GeneratedCardType.Attack => Localize("攻击", "Attack"),
        GeneratedCardType.Power => Localize("能力", "Power"),
        _ => Localize("技能", "Skill")
    };
    private static string RarityName(GeneratedRarity value) => value switch
    {
        GeneratedRarity.Basic => Localize("基础", "Basic"),
        GeneratedRarity.Common => Localize("普通", "Common"),
        GeneratedRarity.Uncommon => Localize("罕见", "Uncommon"),
        GeneratedRarity.Rare => Localize("稀有", "Rare"),
        _ => Localize("远古", "Ancient")
    };
    private static string TagName(GeneratorCardTag value) => value switch
    {
        GeneratorCardTag.Strike => Localize("打击", "Strike"), GeneratorCardTag.Defend => Localize("防御", "Defend"),
        GeneratorCardTag.Exhaust => Localize("消耗", "Exhaust"), GeneratorCardTag.Innate => Localize("固有", "Innate"),
        GeneratorCardTag.Retain => Localize("保留", "Retain"), GeneratorCardTag.Sly => Localize("奇巧", "Sly"),
        GeneratorCardTag.Ethereal => Localize("虚无", "Ethereal"), GeneratorCardTag.Eternal => Localize("永恒", "Eternal"),
        GeneratorCardTag.Unplayable => Localize("不可打出", "Unplayable"),
        GeneratorCardTag.OstyAttack => Localize("奥斯提攻击", "Osty Attack"), _ => value.ToString()
    };
    private static string PileName(PileType pile) => pile switch
    {
        PileType.Hand => Localize("手牌", "hand"), PileType.Draw => Localize("抽牌堆", "draw pile"),
        _ => Localize("牌组", "deck")
    };

    private static string ValueName(string id) => id switch
    {
        "damage" => Localize("伤害", "Damage"), "block" => Localize("格挡", "Block"),
        "amount" => Localize("数值", "Amount"), "count" => Localize("数量", "Count"),
        "hits" or "extra_hits" => Localize("次数", "Hits"),
        "duration" => Localize("持续回合", "Duration"), "threshold" => Localize("阈值", "Threshold"),
        "energy" => Localize("能量", "Energy"), "stars" => Localize("蓝星", "Stars"),
        "draw" => Localize("抽牌", "Cards drawn"), "percentage" => Localize("比例", "Percentage"),
        "choices" => Localize("候选数量", "Choices"), "picks" => Localize("选择数量", "Picks"),
        _ => id.Replace('_', ' ')
    };

    private static void AddChoice(GridContainer grid, string label, IReadOnlyList<string> items, int selected,
        Action<int> changed)
    {
        grid.AddChild(LabelText(label, 18, Muted));
        var choice = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var item in items) choice.AddItem(item);
        choice.Selected = Math.Clamp(selected, 0, Math.Max(0, items.Count - 1));
        choice.ItemSelected += index => changed((int)index);
        grid.AddChild(choice);
    }

    private static (PanelContainer Frame, VBoxContainer Body) Section(string title, float width,
        bool expand = false)
    {
        var frame = new PanelContainer
        {
            CustomMinimumSize = new Vector2(width, 0), SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = expand ? SizeFlags.ExpandFill : SizeFlags.ShrinkBegin
        };
        frame.AddThemeStyleboxOverride("panel", Box(Panel, Border, 2, 12));
        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 9);
        body.AddChild(LabelText(title, 26, Text, HorizontalAlignment.Center));
        frame.AddChild(Padded(body, 13));
        return (frame, body);
    }

    private static (PanelContainer Panel, Button Hitbox) ComponentChip(GeneratorOperation operation,
        IReadOnlyList<string> markers)
    {
        var key = CategoryKey(operation);
        var background = CategoryColor(key);
        var border = background.Lightened(.38f);
        var panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0, 76),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Stop
        };
        panel.AddThemeStyleboxOverride("panel", Box(background, border, 2, 8));
        var stack = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        stack.AddThemeConstantOverride("separation", 0);
        var tags = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        tags.AddThemeConstantOverride("h_separation", 5);
        tags.AddThemeConstantOverride("v_separation", 3);
        tags.AddChild(MiniTag(CategoryDisplay(key), background, border));
        foreach (var marker in markers.Where(marker => !string.IsNullOrWhiteSpace(marker)))
            tags.AddChild(MiniTag(marker, background, border));
        stack.AddChild(tags);
        var content = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore,
            Text = TrainingRoomOverlay.ComponentMarkupForPrefix(ComponentText(operation),
                RunManager.Instance.GetLocalCharacterEnergyIconPrefix() ?? "colorless"),
            CustomMinimumSize = new Vector2(0, 32)
        };
        content.AddThemeFontSizeOverride("normal_font_size", 20);
        content.AddThemeColorOverride("default_color", Text);
        stack.AddChild(Padded(content, 12, 8));
        panel.AddChild(stack);

        var hitbox = new Button { Flat = true, FocusMode = FocusModeEnum.None, MouseFilter = MouseFilterEnum.Stop };
        hitbox.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var empty = new StyleBoxEmpty();
        hitbox.AddThemeStyleboxOverride("normal", empty);
        hitbox.AddThemeStyleboxOverride("hover", empty);
        hitbox.AddThemeStyleboxOverride("pressed", empty);
        hitbox.AddThemeStyleboxOverride("focus", empty);
        hitbox.MouseEntered += () =>
            panel.AddThemeStyleboxOverride("panel", Box(background.Lightened(.08f), Accent, 2, 8));
        hitbox.MouseExited += () => panel.AddThemeStyleboxOverride("panel", Box(background, border, 2, 8));
        panel.AddChild(hitbox);
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(content) || !content.IsInsideTree() || content.GetLineCount() <= 2) return;
            content.AddThemeFontSizeOverride("normal_font_size", content.GetLineCount() > 3 ? 16 : 18);
        }).CallDeferred();
        return (panel, hitbox);
    }

    private static Control MiniTag(string text, Color background, Color border)
    {
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        var style = Box(background.Lightened(.08f), border, 1, 6);
        style.BorderWidthTop = 0;
        style.CornerRadiusTopLeft = 0;
        style.CornerRadiusTopRight = 0;
        panel.AddThemeStyleboxOverride("panel", style);
        var label = LabelText(text, 14, Text);
        panel.AddChild(Padded(label, 7, 3));
        return panel;
    }

    private static Button Chip(string text, Color background)
    {
        var button = new Button
        {
            Text = string.Empty,
            Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(0, 58),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        button.AddThemeStyleboxOverride("normal", Box(background, background.Lightened(.35f), 2, 8));
        button.AddThemeStyleboxOverride("hover", Box(background.Lightened(.12f), Accent, 2, 8));
        var prefix = RunManager.Instance.GetLocalCharacterEnergyIconPrefix() ?? "colorless";
        var label = new RichTextLabel
        {
            BbcodeEnabled = true, ScrollActive = false, FitContent = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = MouseFilterEnum.Ignore,
            Text = TrainingRoomOverlay.ComponentMarkupForPrefix(text, prefix)
        };
        label.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        label.OffsetLeft = 12;
        label.OffsetTop = 7;
        label.OffsetRight = -10;
        label.OffsetBottom = -5;
        label.AddThemeFontSizeOverride("normal_font_size", 18);
        label.AddThemeColorOverride("default_color", Text);
        button.AddChild(label);
        return button;
    }

    private static Button ActionButton(string text, bool primary = false)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(primary ? 142 : 112, 52) };
        var background = primary ? new Color("326d50") : new Color("353142");
        var border = primary ? Good : new Color("6d657f");
        button.AddThemeStyleboxOverride("normal", Box(background, border, 2, 8));
        button.AddThemeStyleboxOverride("hover", Box(background.Lightened(.12f), border.Lightened(.15f), 2, 8));
        button.AddThemeFontSizeOverride("font_size", 19);
        button.AddThemeColorOverride("font_color", Text);
        return button;
    }

    private static string SignedNumber(double value) => value >= -0.0001d
        ? "+" + BudgetNumber(Math.Max(0d, value))
        : BudgetNumber(value);

    private static string BudgetNumber(double value)
    {
        if (!double.IsFinite(value)) return "INF";
        return Math.Abs(value - Math.Round(value)) < .0001d
            ? Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string MultiplierNumber(double value) => double.IsFinite(value)
        ? value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
        : "?";

    private static Label LabelText(string text, int size, Color color,
        HorizontalAlignment alignment = HorizontalAlignment.Left)
    {
        var label = new Label
        {
            Text = text, AutowrapMode = TextServer.AutowrapMode.Off,
            HorizontalAlignment = alignment, VerticalAlignment = VerticalAlignment.Center
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private static MarginContainer Padded(Control child, int horizontal, int vertical = -1)
    {
        if (vertical < 0) vertical = horizontal;
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", horizontal);
        margin.AddThemeConstantOverride("margin_right", horizontal);
        margin.AddThemeConstantOverride("margin_top", vertical);
        margin.AddThemeConstantOverride("margin_bottom", vertical);
        margin.AddChild(child);
        return margin;
    }

    private static StyleBoxFlat Box(Color background, Color border, int width, int radius) => new()
    {
        BgColor = background, BorderColor = border,
        BorderWidthLeft = width, BorderWidthRight = width, BorderWidthTop = width, BorderWidthBottom = width,
        CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius,
        CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius
    };

    private static StyleBoxFlat LStemBox(Color background, Color border) => new()
    {
        BgColor = background,
        BorderColor = border,
        BorderWidthLeft = 2,
        BorderWidthRight = 2,
        BorderWidthBottom = 2,
        BorderWidthTop = 0,
        CornerRadiusBottomLeft = 8
    };
}
