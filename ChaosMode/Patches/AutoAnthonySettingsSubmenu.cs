using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace AutoAnthony.Patches;

/// <summary>
/// A self-contained settings submenu modeled after BaseLib's NModConfigSubmenu architecture. BaseLib remains an
/// optional neighboring mod: AutoAnthony registers its own NSubmenu and only borrows complete vanilla setting-row
/// templates from the settings screen, so controller behavior and visual style stay native without a dependency.
/// </summary>
internal sealed class AutoAnthonySettingsSubmenu : NSubmenu
{
    private static WeakReference<Control>? _optionTemplate;
    private static WeakReference<Control>? _actionTemplate;
    private Control? _initialFocus;
    private Control? _historyOptimizationLine;

    protected override Control? InitialFocusedControl => _initialFocus;

    internal static void CaptureTemplates(Control optionTemplate, Control actionTemplate)
    {
        _optionTemplate = new WeakReference<Control>(optionTemplate);
        _actionTemplate = new WeakReference<Control>(actionTemplate);
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;

        var title = new Label
        {
            Text = ChaosSettingsScreenPatch.Text("AUTO_ANTHONY_SETTINGS_PAGE_TITLE"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        AddChild(title);
        title.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
        title.OffsetTop = 35;
        title.OffsetBottom = 105;
        title.AddThemeFontSizeOverride("font_size", 34);

        var scroll = new ScrollContainer
        {
            Name = "AutoAnthonySettingsScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            AnchorLeft = 0.19f,
            AnchorRight = 0.81f,
            AnchorTop = 0,
            AnchorBottom = 1,
            OffsetTop = 115,
            OffsetBottom = -105
        };
        AddChild(scroll);
        scroll.CustomMinimumSize = new Vector2(800, 0);
        var options = new VBoxContainer
        {
            Name = "AutoAnthonySettingsOptions",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(800, 0)
        };
        options.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(options);

        if (_optionTemplate?.TryGetTarget(out var optionTemplate) != true
            || _actionTemplate?.TryGetTarget(out var actionTemplate) != true
            || !GodotObject.IsInstanceValid(optionTemplate)
            || !GodotObject.IsInstanceValid(actionTemplate))
        {
            options.AddChild(new Label
            {
                Text = ChaosSettingsScreenPatch.Text("AUTO_ANTHONY_SETTINGS_PAGE_UNAVAILABLE"),
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }
        else
        {
            BuildOptions(options, optionTemplate, actionTemplate);
        }

        var backButton = PreloadManager.Cache.GetScene(SceneHelper.GetScenePath("ui/back_button"))
            .Instantiate<NBackButton>();
        backButton.Name = "BackButton";
        AddChild(backButton);
        ConnectSignals();
    }

    private void BuildOptions(VBoxContainer options, Control optionTemplate, Control actionTemplate)
    {
        // The master switch governs the whole mod, so keep it visible independently of every collapsible category.
        ChaosSettingsToggle.EnabledInstance = AddOption(options, optionTemplate,
            ChaosSettingsToggle.EnabledLineName, "AUTO_ANTHONY_ENABLED");

        var numeric = AddCategory(options, ChaosSettingsToggle.NumericCategoryLineName,
            "AUTO_ANTHONY_CATEGORY_NUMERIC");
        ChaosSettingsToggle.NumericBalanceOptimizationInstance = AddOption(numeric.Content, optionTemplate,
            ChaosSettingsToggle.NumericBalanceOptimizationLineName, "AUTO_ANTHONY_NUMERIC_BALANCE_OPTIMIZATION");
        ChaosSettingsToggle.NumericRandomModeInstance = AddOption(numeric.Content, optionTemplate,
            ChaosSettingsToggle.NumericRandomModeLineName, "AUTO_ANTHONY_NUMERIC_RANDOM_MODE");

        var pool = AddCategory(options, ChaosSettingsToggle.PoolCategoryLineName,
            "AUTO_ANTHONY_CATEGORY_POOL");
        ChaosSettingsToggle.UltimateChaosInstance = AddOption(pool.Content, optionTemplate,
            ChaosSettingsToggle.UltimateChaosLineName, "AUTO_ANTHONY_ULTIMATE_CHAOS");
        ChaosSettingsToggle.AddGeneratedCardsInstance = AddOption(pool.Content, optionTemplate,
            ChaosSettingsToggle.AddGeneratedCardsLineName, "AUTO_ANTHONY_ADD_GENERATED_CARDS");
        ChaosSettingsToggle.PreserveOriginalCardsInstance = AddOption(pool.Content, optionTemplate,
            ChaosSettingsToggle.PreserveOriginalCardsLineName, "AUTO_ANTHONY_PRESERVE_ORIGINAL_CARDS");
        ChaosSettingsToggle.DecomposeOriginalCardsInstance = AddOption(pool.Content, optionTemplate,
            ChaosSettingsToggle.DecomposeOriginalCardsLineName, "AUTO_ANTHONY_DECOMPOSE_ORIGINAL_CARDS");
        ChaosSettingsToggle.ReplaceStartingCardsInstance = AddOption(pool.Content, optionTemplate,
            ChaosSettingsToggle.ReplaceStartingCardsLineName, "AUTO_ANTHONY_REPLACE_STARTING_CARDS");
        ChaosSettingsToggle.AnytimeCardEditingInstance = ChaosSettingsToggle.IsCardTinkeringLoaded
            ? AddOption(pool.Content, optionTemplate, ChaosSettingsToggle.AnytimeCardEditingLineName,
                "AUTO_ANTHONY_ANYTIME_CARD_EDITING")
            : null;

        var display = AddCategory(options, ChaosSettingsToggle.DisplayCategoryLineName,
            "AUTO_ANTHONY_CATEGORY_DISPLAY");
        ChaosSettingsToggle.RandomCardArtInstance = AddOption(display.Content, optionTemplate,
            ChaosSettingsToggle.RandomCardArtLineName, "AUTO_ANTHONY_RANDOM_CARD_ART");
        ChaosSettingsToggle.GenerationModeHoverTipsInstance = AddOption(display.Content, optionTemplate,
            ChaosSettingsToggle.GenerationModeHoverTipsLineName, "AUTO_ANTHONY_GENERATION_MODE_HOVER_TIPS");
        ChaosSettingsToggle.CardInternalIdsInstance = AddOption(display.Content, optionTemplate,
            ChaosSettingsToggle.CardInternalIdsLineName, "AUTO_ANTHONY_CARD_INTERNAL_IDS");
        ChaosSettingsToggle.SurpriseModeInstance = AddOption(display.Content, optionTemplate,
            ChaosSettingsToggle.SurpriseModeLineName, "AUTO_ANTHONY_SURPRISE_MODE");
        ChaosSettingsToggle.SurpriseModeLiteInstance = AddOption(display.Content, optionTemplate,
            ChaosSettingsToggle.SurpriseModeLiteLineName, "AUTO_ANTHONY_SURPRISE_MODE_LITE");
        ChaosSettingsToggle.SurpriseModeProInstance = AddOption(display.Content, optionTemplate,
            ChaosSettingsToggle.SurpriseModeProLineName, "AUTO_ANTHONY_SURPRISE_MODE_PRO");

        _historyOptimizationLine = AddHistoryOptimizationAction(options, actionTemplate);
        RefreshHistoryOptimizationVisibility();
        SyncAllTickboxes();
        RefreshOptionAvailability();
    }

    private NFastModeTickbox AddOption(Node options, Control template, string lineName, string labelKey)
    {
        var toggle = ChaosSettingsScreenPatch.AddRow(options, template, lineName, labelKey,
            options.GetChildCount());
        toggle.IsTicked = ChaosSettingsToggle.GetValue(ChaosSettingsToggle.GetKind(toggle));
        _initialFocus ??= toggle;
        return toggle;
    }

    private static AutoAnthonySettingsSection AddCategory(Node options, string lineName, string labelKey)
    {
        var section = new AutoAnthonySettingsSection(lineName, ChaosSettingsScreenPatch.Text(labelKey));
        options.AddChild(section);
        return section;
    }

    private static void SyncAllTickboxes()
    {
        foreach (var toggle in new[]
                 {
                     ChaosSettingsToggle.EnabledInstance, ChaosSettingsToggle.NumericBalanceOptimizationInstance,
                     ChaosSettingsToggle.NumericRandomModeInstance, ChaosSettingsToggle.UltimateChaosInstance,
                     ChaosSettingsToggle.AddGeneratedCardsInstance, ChaosSettingsToggle.PreserveOriginalCardsInstance,
                     ChaosSettingsToggle.DecomposeOriginalCardsInstance,
                     ChaosSettingsToggle.ReplaceStartingCardsInstance,
                     ChaosSettingsToggle.AnytimeCardEditingInstance,
                     ChaosSettingsToggle.RandomCardArtInstance,
                     ChaosSettingsToggle.GenerationModeHoverTipsInstance,
                     ChaosSettingsToggle.CardInternalIdsInstance, ChaosSettingsToggle.SurpriseModeInstance,
                     ChaosSettingsToggle.SurpriseModeLiteInstance, ChaosSettingsToggle.SurpriseModeProInstance
                 })
            if (GodotObject.IsInstanceValid(toggle))
                toggle!.IsTicked = ChaosSettingsToggle.GetValue(ChaosSettingsToggle.GetKind(toggle));
    }

    private void RefreshOptionAvailability()
    {
        var inRun = false;
        for (Node? current = this; current is not null; current = current.GetParent())
            if (current is NRunSubmenuStack)
            {
                inRun = true;
                break;
            }

        foreach (var toggle in new[]
                 {
                     ChaosSettingsToggle.EnabledInstance, ChaosSettingsToggle.NumericBalanceOptimizationInstance,
                     ChaosSettingsToggle.NumericRandomModeInstance, ChaosSettingsToggle.UltimateChaosInstance,
                     ChaosSettingsToggle.AddGeneratedCardsInstance, ChaosSettingsToggle.PreserveOriginalCardsInstance,
                     ChaosSettingsToggle.DecomposeOriginalCardsInstance,
                     ChaosSettingsToggle.ReplaceStartingCardsInstance,
                     ChaosSettingsToggle.AnytimeCardEditingInstance,
                     ChaosSettingsToggle.RandomCardArtInstance,
                     ChaosSettingsToggle.GenerationModeHoverTipsInstance,
                     ChaosSettingsToggle.CardInternalIdsInstance, ChaosSettingsToggle.SurpriseModeInstance,
                     ChaosSettingsToggle.SurpriseModeLiteInstance, ChaosSettingsToggle.SurpriseModeProInstance
                 })
        {
            if (!GodotObject.IsInstanceValid(toggle)) continue;
            var locked = inRun && ChaosSettingsToggle.AppliesNextRun(ChaosSettingsToggle.GetKind(toggle!));
            if (toggle == ChaosSettingsToggle.ReplaceStartingCardsInstance
                && !ChaosModSettings.AddGeneratedCards)
                locked = true;
            if (locked) toggle!.Disable();
            else toggle!.Enable();
            if (toggle!.GetParent() is CanvasItem row)
                row.Modulate = locked ? new Color(1f, 1f, 1f, 0.45f) : Colors.White;
        }
    }

    private static Control AddHistoryOptimizationAction(Node options, Control template)
    {
        var line = (Control)template.Duplicate(6);
        line.Name = ChaosSettingsToggle.OptimizeHistoryLineName;
        ChaosSettingsScreenPatch.FixOwnerRecursive(line, line);
        options.AddChild(line);
        ChaosSettingsScreenPatch.SetLabel(line.GetNodeOrNull<Node>("Label"),
            ChaosSettingsScreenPatch.Text("AUTO_ANTHONY_OPTIMIZE_HISTORY_LABEL"));
        var button = line.GetNodeOrNull<NOpenModdingScreenButton>("ModdingButton")
            ?? throw new InvalidOperationException("The history optimization row has no action button.");
        button.Name = ChaosSettingsToggle.OptimizeHistoryButtonName;
        button.Enable();
        ChaosSettingsScreenPatch.SetLabel(button.GetNodeOrNull<Node>("Label"),
            ChaosSettingsScreenPatch.Text("AUTO_ANTHONY_OPTIMIZE_HISTORY_BUTTON"));
        button.Connect(NClickableControl.SignalName.Released,
            Callable.From<NButton>(ignored => { _ = OptimizeHistoryAsync(line, button); }));
        return line;
    }

    private static async Task OptimizeHistoryAsync(Control line, NOpenModdingScreenButton button)
    {
        if (HistorySnapshotOptimizer.IsRunning) return;
        button.Disable();
        try
        {
            var report = await HistorySnapshotOptimizer.OptimizeCurrentProfileAsync((current, total) =>
            {
                if (!GodotObject.IsInstanceValid(button)) return;
                var text = ChaosSettingsScreenPatch.Text("AUTO_ANTHONY_OPTIMIZE_HISTORY_PROGRESS")
                    .Replace("{current}", current.ToString(), StringComparison.Ordinal)
                    .Replace("{total}", total.ToString(), StringComparison.Ordinal);
                ChaosSettingsScreenPatch.SetLabel(button.GetNodeOrNull<Node>("Label"), text);
            });
            if (!GodotObject.IsInstanceValid(line)) return;
            if (report.FailedFiles == 0)
            {
                line.Visible = false;
                if (line.GetParent() is Container container) container.QueueSort();
            }
            else
            {
                button.Enable();
                ChaosSettingsScreenPatch.SetLabel(button.GetNodeOrNull<Node>("Label"),
                    ChaosSettingsScreenPatch.Text("AUTO_ANTHONY_OPTIMIZE_HISTORY_RETRY"));
            }
        }
        catch (Exception exception)
        {
            Log.Error($"[AutoAnthony] Dedicated-page history optimization failed: {exception}");
            if (!GodotObject.IsInstanceValid(button)) return;
            button.Enable();
            ChaosSettingsScreenPatch.SetLabel(button.GetNodeOrNull<Node>("Label"),
                ChaosSettingsScreenPatch.Text("AUTO_ANTHONY_OPTIMIZE_HISTORY_RETRY"));
        }
    }

    protected override void OnSubmenuShown()
    {
        SyncAllTickboxes();
        RefreshHistoryOptimizationVisibility();
        RefreshOptionAvailability();
    }

    public override void OnSubmenuOpened()
    {
        SyncAllTickboxes();
        RefreshHistoryOptimizationVisibility();
        RefreshOptionAvailability();
    }

    private void RefreshHistoryOptimizationVisibility()
    {
        if (GodotObject.IsInstanceValid(_historyOptimizationLine))
            _historyOptimizationLine!.Visible = HistorySnapshotOptimizer.ShouldShowForCurrentProfile();
    }
}

/// <summary>
/// BaseLib-style collapsible section header without taking a runtime dependency on BaseLib. Unlike the previous
/// disabled option-row clone, this is a real focusable category control with a dedicated content container.
/// </summary>
internal sealed class AutoAnthonySettingsSection : VBoxContainer
{
    private readonly Button _header;
    private bool _expanded = true;

    internal VBoxContainer Content { get; }

    internal AutoAnthonySettingsSection(string name, string title)
    {
        Name = name;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 0);

        var headerMargin = new MarginContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Pass
        };
        headerMargin.AddThemeConstantOverride("margin_top", 16);
        headerMargin.AddThemeConstantOverride("margin_bottom", 16);
        AddChild(headerMargin);

        _header = new Button
        {
            Name = $"Header_{name}",
            Text = $"▼  {title}",
            Alignment = HorizontalAlignment.Left,
            Flat = true,
            FocusMode = FocusModeEnum.All,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 64)
        };
        _header.AddThemeFontOverride("font", PreloadManager.Cache.GetAsset<Font>(
            "res://themes/kreon_bold_shared.tres"));
        _header.AddThemeFontSizeOverride("font_size", 40);
        _header.AddThemeColorOverride("font_color", Colors.White);
        _header.AddThemeColorOverride("font_hover_color", StsColors.gold);
        _header.AddThemeColorOverride("font_focus_color", StsColors.gold);
        _header.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        _header.Pressed += Toggle;
        headerMargin.AddChild(_header);

        Content = new VBoxContainer
        {
            Name = $"SectionContent_{name}",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        Content.AddThemeConstantOverride("separation", 4);
        AddChild(Content);
    }

    private void Toggle()
    {
        _expanded = !_expanded;
        Content.Visible = _expanded;
        _header.Text = $"{(_expanded ? "▼" : "▶")}  {_header.Text[3..]}";
    }
}

internal static class AutoAnthonySettingsSubmenuRegistry
{
    private sealed class PageHolder(AutoAnthonySettingsSubmenu page)
    {
        internal AutoAnthonySettingsSubmenu Page { get; } = page;
    }

    private static readonly ConditionalWeakTable<NSubmenuStack, PageHolder> Pages = new();

    internal static bool GetOrCreate(NSubmenuStack stack, Type type, ref NSubmenu result)
    {
        if (type != typeof(AutoAnthonySettingsSubmenu)) return true;
        if (!Pages.TryGetValue(stack, out var holder))
        {
            var page = new AutoAnthonySettingsSubmenu { Visible = false };
            // Programmatically-created controls do not receive the scene-authored full-rect layout that built-in
            // submenus have. Seed the parent size before _Ready so containers can calculate their first layout pass.
            page.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            page.Position = Vector2.Zero;
            page.Size = stack.Size;
            stack.AddChildSafely(page);
            page.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            holder = new PageHolder(page);
            Pages.Add(stack, holder);
        }
        result = holder.Page;
        return false;
    }
}

[HarmonyPatch(typeof(NMainMenuSubmenuStack), "GetSubmenuType", [typeof(Type)])]
internal static class AutoAnthonyMainMenuSettingsSubmenuRegistrationPatch
{
    private static bool Prefix(NMainMenuSubmenuStack __instance, Type type, ref NSubmenu __result) =>
        AutoAnthonySettingsSubmenuRegistry.GetOrCreate(__instance, type, ref __result);
}

[HarmonyPatch(typeof(NRunSubmenuStack), "GetSubmenuType", [typeof(Type)])]
internal static class AutoAnthonyRunSettingsSubmenuRegistrationPatch
{
    private static bool Prefix(NRunSubmenuStack __instance, Type type, ref NSubmenu __result) =>
        AutoAnthonySettingsSubmenuRegistry.GetOrCreate(__instance, type, ref __result);
}
