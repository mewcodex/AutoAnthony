using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace AutoAnthony.Patches;

internal static class ChaosSettingsToggle
{
    internal enum Kind { None, Enabled, AddGeneratedCards, ReplaceStartingCards, PreserveOriginalCards, DecomposeOriginalCards, UltimateChaos, NumericBalanceOptimization, NumericRandomMode, AnytimeCardEditing, RandomCardArt, GenerationModeHoverTips, CardInternalIds, SurpriseMode, SurpriseModeLite, SurpriseModePro }

    internal const string GroupLineName = "AutoAnthonySettingsGroup";
    internal const string GroupButtonName = "AutoAnthonySettingsGroupButton";
    internal const string EnabledLineName = "AutoAnthonyEnabled";
    internal const string AddGeneratedCardsLineName = "AutoAnthonyAddGeneratedCards";
    internal const string ReplaceStartingCardsLineName = "AutoAnthonyReplaceStartingCards";
    internal const string UltimateChaosLineName = "AutoAnthonyUltimateChaos";
    internal const string NumericBalanceOptimizationLineName = "AutoAnthonyNumericBalanceOptimization";
    internal const string NumericRandomModeLineName = "AutoAnthonyNumericRandomMode";
    internal const string PreserveOriginalCardsLineName = "AutoAnthonyPreserveOriginalCards";
    internal const string DecomposeOriginalCardsLineName = "AutoAnthonyDecomposeOriginalCards";
    internal const string AnytimeCardEditingLineName = "AutoAnthonyAnytimeCardEditing";
    internal const string NumericCategoryLineName = "AutoAnthonyNumericCategory";
    internal const string PoolCategoryLineName = "AutoAnthonyPoolCategory";
    internal const string DisplayCategoryLineName = "AutoAnthonyDisplayCategory";
    internal const string RandomCardArtLineName = "AutoAnthonyRandomCardArt";
    internal const string GenerationModeHoverTipsLineName = "AutoAnthonyGenerationModeHoverTips";
    internal const string CardInternalIdsLineName = "AutoAnthonyCardInternalIds";
    internal const string SurpriseModeLineName = "AutoAnthonySurpriseMode";
    internal const string SurpriseModeLiteLineName = "AutoAnthonySurpriseModeLite";
    internal const string SurpriseModeProLineName = "AutoAnthonySurpriseModePro";
    internal const string OptimizeHistoryLineName = "AutoAnthonyOptimizeHistory";
    internal const string OptimizeHistoryButtonName = "AutoAnthonyOptimizeHistoryButton";
    internal static NFastModeTickbox? EnabledInstance { get; set; }
    internal static NFastModeTickbox? AddGeneratedCardsInstance { get; set; }
    internal static NFastModeTickbox? ReplaceStartingCardsInstance { get; set; }
    internal static NFastModeTickbox? UltimateChaosInstance { get; set; }
    internal static NFastModeTickbox? NumericBalanceOptimizationInstance { get; set; }
    internal static NFastModeTickbox? NumericRandomModeInstance { get; set; }
    internal static NFastModeTickbox? PreserveOriginalCardsInstance { get; set; }
    internal static NFastModeTickbox? DecomposeOriginalCardsInstance { get; set; }
    internal static NFastModeTickbox? AnytimeCardEditingInstance { get; set; }
    internal static NFastModeTickbox? RandomCardArtInstance { get; set; }
    internal static NFastModeTickbox? GenerationModeHoverTipsInstance { get; set; }
    internal static NFastModeTickbox? CardInternalIdsInstance { get; set; }
    internal static NFastModeTickbox? SurpriseModeInstance { get; set; }
    internal static NFastModeTickbox? SurpriseModeLiteInstance { get; set; }
    internal static NFastModeTickbox? SurpriseModeProInstance { get; set; }

    internal static bool IsCardTinkeringLoaded => ModManager.GetLoadedMods().Any(mod =>
        string.Equals(mod.manifest?.id, "AutoAnthonyCardTinkering", StringComparison.Ordinal));
    internal static NOpenModdingScreenButton? OptimizeHistoryButtonInstance { get; set; }

    internal static readonly string[] OptionLineNames =
    [EnabledLineName, NumericCategoryLineName, NumericBalanceOptimizationLineName, NumericRandomModeLineName,
        PoolCategoryLineName, UltimateChaosLineName, AddGeneratedCardsLineName, PreserveOriginalCardsLineName,
        DecomposeOriginalCardsLineName,
        ReplaceStartingCardsLineName,
        AnytimeCardEditingLineName,
        DisplayCategoryLineName, RandomCardArtLineName, GenerationModeHoverTipsLineName, CardInternalIdsLineName,
        SurpriseModeLineName, SurpriseModeLiteLineName, SurpriseModeProLineName,
        OptimizeHistoryLineName];

    internal static Kind GetKind(NFastModeTickbox instance)
    {
        var lineName = instance.GetParent()?.Name.ToString();
        if (instance == EnabledInstance || instance.Name.ToString() == EnabledLineName
            || lineName == EnabledLineName)
            return Kind.Enabled;
        if (instance == AddGeneratedCardsInstance || instance.Name.ToString() == AddGeneratedCardsLineName
            || lineName == AddGeneratedCardsLineName)
            return Kind.AddGeneratedCards;
        if (instance == ReplaceStartingCardsInstance || instance.Name.ToString() == ReplaceStartingCardsLineName
            || lineName == ReplaceStartingCardsLineName)
            return Kind.ReplaceStartingCards;
        if (instance == UltimateChaosInstance || instance.Name.ToString() == UltimateChaosLineName
            || lineName == UltimateChaosLineName)
            return Kind.UltimateChaos;
        if (instance == NumericBalanceOptimizationInstance
            || instance.Name.ToString() == NumericBalanceOptimizationLineName
            || lineName == NumericBalanceOptimizationLineName)
            return Kind.NumericBalanceOptimization;
        if (instance == NumericRandomModeInstance || instance.Name.ToString() == NumericRandomModeLineName
            || lineName == NumericRandomModeLineName)
            return Kind.NumericRandomMode;
        if (instance == PreserveOriginalCardsInstance || instance.Name.ToString() == PreserveOriginalCardsLineName
            || lineName == PreserveOriginalCardsLineName)
            return Kind.PreserveOriginalCards;
        if (instance == DecomposeOriginalCardsInstance || instance.Name.ToString() == DecomposeOriginalCardsLineName
            || lineName == DecomposeOriginalCardsLineName)
            return Kind.DecomposeOriginalCards;
        if (instance == AnytimeCardEditingInstance || instance.Name.ToString() == AnytimeCardEditingLineName
            || lineName == AnytimeCardEditingLineName)
            return Kind.AnytimeCardEditing;
        if (instance == RandomCardArtInstance || instance.Name.ToString() == RandomCardArtLineName
            || lineName == RandomCardArtLineName)
            return Kind.RandomCardArt;
        if (instance == GenerationModeHoverTipsInstance
            || instance.Name.ToString() == GenerationModeHoverTipsLineName
            || lineName == GenerationModeHoverTipsLineName)
            return Kind.GenerationModeHoverTips;
        if (instance == CardInternalIdsInstance || instance.Name.ToString() == CardInternalIdsLineName
            || lineName == CardInternalIdsLineName)
            return Kind.CardInternalIds;
        if (instance == SurpriseModeInstance || instance.Name.ToString() == SurpriseModeLineName
            || lineName == SurpriseModeLineName)
            return Kind.SurpriseMode;
        if (instance == SurpriseModeLiteInstance || instance.Name.ToString() == SurpriseModeLiteLineName
            || lineName == SurpriseModeLiteLineName)
            return Kind.SurpriseModeLite;
        if (instance == SurpriseModeProInstance || instance.Name.ToString() == SurpriseModeProLineName
            || lineName == SurpriseModeProLineName)
            return Kind.SurpriseModePro;
        return Kind.None;
    }

    internal static bool GetValue(Kind kind) => kind switch
    {
        Kind.Enabled => ChaosModSettings.Enabled,
        Kind.AddGeneratedCards => ChaosModSettings.AddGeneratedCards,
        Kind.ReplaceStartingCards => ChaosModSettings.ReplaceStartingCards,
        Kind.UltimateChaos => ChaosModSettings.UltimateChaos,
        Kind.NumericBalanceOptimization => ChaosModSettings.NumericBalanceOptimization,
        Kind.NumericRandomMode => ChaosModSettings.NumericRandomMode,
        Kind.PreserveOriginalCards => ChaosModSettings.PreserveOriginalCards,
        Kind.DecomposeOriginalCards => ChaosModSettings.DecomposeOriginalCards,
        Kind.AnytimeCardEditing => ChaosModSettings.AnytimeCardEditing,
        Kind.RandomCardArt => ChaosModSettings.RandomCardArt,
        Kind.GenerationModeHoverTips => ChaosModSettings.ShowGenerationModeHoverTips,
        Kind.CardInternalIds => ChaosModSettings.ShowCardInternalIds,
        Kind.SurpriseMode => ChaosModSettings.SurpriseMode,
        Kind.SurpriseModeLite => ChaosModSettings.SurpriseModeLite,
        Kind.SurpriseModePro => ChaosModSettings.SurpriseModePro,
        _ => false
    };

    internal static bool AppliesNextRun(Kind kind) => kind is
        Kind.Enabled or Kind.AddGeneratedCards or Kind.ReplaceStartingCards or Kind.PreserveOriginalCards or Kind.UltimateChaos
        or Kind.NumericBalanceOptimization or Kind.NumericRandomMode or Kind.RandomCardArt;

    internal static void SetValue(Kind kind, bool value)
    {
        switch (kind)
        {
            case Kind.Enabled: ChaosModSettings.Enabled = value; break;
            case Kind.AddGeneratedCards: ChaosModSettings.AddGeneratedCards = value; break;
            case Kind.ReplaceStartingCards: ChaosModSettings.ReplaceStartingCards = value; break;
            case Kind.UltimateChaos: ChaosModSettings.UltimateChaos = value; break;
            case Kind.NumericBalanceOptimization: ChaosModSettings.NumericBalanceOptimization = value; break;
            case Kind.NumericRandomMode: ChaosModSettings.NumericRandomMode = value; break;
            case Kind.PreserveOriginalCards: ChaosModSettings.PreserveOriginalCards = value; break;
            case Kind.DecomposeOriginalCards: ChaosModSettings.DecomposeOriginalCards = value; break;
            case Kind.AnytimeCardEditing: ChaosModSettings.AnytimeCardEditing = value; break;
            case Kind.RandomCardArt: ChaosModSettings.RandomCardArt = value; break;
            case Kind.GenerationModeHoverTips: ChaosModSettings.ShowGenerationModeHoverTips = value; break;
            case Kind.CardInternalIds: ChaosModSettings.ShowCardInternalIds = value; break;
            case Kind.SurpriseMode: ChaosModSettings.SurpriseMode = value; break;
            case Kind.SurpriseModeLite: ChaosModSettings.SurpriseModeLite = value; break;
            case Kind.SurpriseModePro: ChaosModSettings.SurpriseModePro = value; break;
        }

        // Enabling one surprise mode disables the other two in settings; mirror that change in the open UI.
        if (value && kind is Kind.SurpriseMode or Kind.SurpriseModeLite or Kind.SurpriseModePro)
            SyncSurpriseTickboxes();
        if (kind is Kind.AddGeneratedCards or Kind.PreserveOriginalCards or Kind.ReplaceStartingCards)
            SyncPoolTickboxes();
    }

    internal static string ToastKey(Kind kind, bool value)
    {
        var stem = kind switch
        {
            Kind.Enabled => "AUTO_ANTHONY_ENABLED",
            Kind.AddGeneratedCards => "AUTO_ANTHONY_ADD_GENERATED_CARDS",
            Kind.ReplaceStartingCards => "AUTO_ANTHONY_REPLACE_STARTING_CARDS",
            Kind.UltimateChaos => "AUTO_ANTHONY_ULTIMATE_CHAOS",
            Kind.NumericBalanceOptimization => "AUTO_ANTHONY_NUMERIC_BALANCE_OPTIMIZATION",
            Kind.NumericRandomMode => "AUTO_ANTHONY_NUMERIC_RANDOM_MODE",
            Kind.PreserveOriginalCards => "AUTO_ANTHONY_PRESERVE_ORIGINAL_CARDS",
            Kind.DecomposeOriginalCards => "AUTO_ANTHONY_DECOMPOSE_ORIGINAL_CARDS",
            Kind.AnytimeCardEditing => "AUTO_ANTHONY_ANYTIME_CARD_EDITING",
            Kind.RandomCardArt => "AUTO_ANTHONY_RANDOM_CARD_ART",
            Kind.GenerationModeHoverTips => "AUTO_ANTHONY_GENERATION_MODE_HOVER_TIPS",
            Kind.CardInternalIds => "AUTO_ANTHONY_CARD_INTERNAL_IDS",
            Kind.SurpriseMode => "AUTO_ANTHONY_SURPRISE_MODE",
            Kind.SurpriseModeLite => "AUTO_ANTHONY_SURPRISE_MODE_LITE",
            Kind.SurpriseModePro => "AUTO_ANTHONY_SURPRISE_MODE_PRO",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        return $"{stem}_{(value ? "ON" : "OFF")}";
    }

    internal static void SyncSurpriseTickboxes()
    {
        if (GodotObject.IsInstanceValid(SurpriseModeInstance))
            SurpriseModeInstance!.IsTicked = ChaosModSettings.SurpriseMode;
        if (GodotObject.IsInstanceValid(SurpriseModeLiteInstance))
            SurpriseModeLiteInstance!.IsTicked = ChaosModSettings.SurpriseModeLite;
        if (GodotObject.IsInstanceValid(SurpriseModeProInstance))
            SurpriseModeProInstance!.IsTicked = ChaosModSettings.SurpriseModePro;
    }

    internal static void SyncPoolTickboxes()
    {
        if (GodotObject.IsInstanceValid(AddGeneratedCardsInstance))
            AddGeneratedCardsInstance!.IsTicked = ChaosModSettings.AddGeneratedCards;
        if (GodotObject.IsInstanceValid(PreserveOriginalCardsInstance))
            PreserveOriginalCardsInstance!.IsTicked = ChaosModSettings.PreserveOriginalCards;
        if (GodotObject.IsInstanceValid(DecomposeOriginalCardsInstance))
            DecomposeOriginalCardsInstance!.IsTicked = ChaosModSettings.DecomposeOriginalCards;
        if (GodotObject.IsInstanceValid(ReplaceStartingCardsInstance))
        {
            ReplaceStartingCardsInstance!.IsTicked = ChaosModSettings.ReplaceStartingCards;
            var inRun = false;
            for (Node? current = ReplaceStartingCardsInstance; current is not null; current = current.GetParent())
                if (current is NRunSubmenuStack)
                {
                    inRun = true;
                    break;
                }
            var locked = inRun || !ChaosModSettings.AddGeneratedCards;
            if (locked) ReplaceStartingCardsInstance.Disable();
            else ReplaceStartingCardsInstance.Enable();
            if (ReplaceStartingCardsInstance.GetParent() is CanvasItem row)
                row.Modulate = locked ? new Color(1f, 1f, 1f, 0.45f) : Colors.White;
        }
    }

    internal static NSettingsScreen? FindSettingsScreen(Node node)
    {
        for (var current = node.GetParent(); current is not null; current = current.GetParent())
            if (current is NSettingsScreen screen) return screen;
        return null;
    }
}

[HarmonyPatch(typeof(NSettingsScreen), nameof(NSettingsScreen._Ready))]
internal static class ChaosSettingsScreenPatch
{
    private static void Postfix(NSettingsScreen __instance)
    {
        try
        {
            var panel = __instance.GetNode<NSettingsPanel>("%GeneralSettings");
            var content = panel.Content;
            var source = content.GetNodeOrNull<Control>("FastMode");
            var groupSource = content.GetNodeOrNull<Control>("Modding");
            if (source is null || groupSource is null) return;

            AutoAnthonySettingsSubmenu.CaptureTemplates(source, groupSource);

            var anchor = content.GetNodeOrNull<Node>("ModdingDivider");
            // Keep the original ModdingDivider below our block and insert a dedicated divider above it. This makes
            // both boundaries explicit instead of trying to reuse one divider for two neighboring rows.
            var insertionIndex = anchor?.GetIndex() ?? content.GetChildCount();
            insertionIndex = AddLeadingDivider(content, anchor
                ?? content.GetNodeOrNull<Node>("FastModeDivider"), insertionIndex);
            var addedRows = 0;
            Control? group = null;
            if (content.GetNodeOrNull<Control>(ChaosSettingsToggle.GroupLineName) is null)
            {
                group = AddGroupRow(panel, groupSource, insertionIndex + addedRows);
                addedRows++;
            }
            // As in BaseLib's configuration UI, the vanilla settings screen contains only an entry point. The
            // options themselves live in a dedicated NSubmenu, while the legacy inline builder below remains as
            // a defensive fallback for game builds where the main-menu submenu stack cannot be located.
            if (CanOpenDedicatedPage(__instance))
            {
                if (anchor is null) AddTrailingDivider(content, insertionIndex, addedRows);
                panel.Call(NSettingsPanel.MethodName.RefreshSize);
                panel.Call(NSettingsPanel.MethodName.UpdateNavigation);
                return;
            }
            if (content.GetNodeOrNull<Node>(ChaosSettingsToggle.EnabledLineName) is null)
            {
                ChaosSettingsToggle.EnabledInstance = AddRow(content, source,
                    ChaosSettingsToggle.EnabledLineName, "AUTO_ANTHONY_ENABLED",
                    insertionIndex + addedRows);
                addedRows++;
            }
            addedRows += AddCategoryIfMissing(content, source, ChaosSettingsToggle.NumericCategoryLineName,
                "AUTO_ANTHONY_CATEGORY_NUMERIC", insertionIndex + addedRows);
            if (content.GetNodeOrNull<Node>(ChaosSettingsToggle.NumericBalanceOptimizationLineName) is null)
            {
                ChaosSettingsToggle.NumericBalanceOptimizationInstance = AddRow(content, source,
                    ChaosSettingsToggle.NumericBalanceOptimizationLineName,
                    "AUTO_ANTHONY_NUMERIC_BALANCE_OPTIMIZATION", insertionIndex + addedRows);
                addedRows++;
            }
            if (content.GetNodeOrNull<Node>(ChaosSettingsToggle.NumericRandomModeLineName) is null)
            {
                ChaosSettingsToggle.NumericRandomModeInstance = AddRow(content, source,
                    ChaosSettingsToggle.NumericRandomModeLineName, "AUTO_ANTHONY_NUMERIC_RANDOM_MODE",
                    insertionIndex + addedRows);
                addedRows++;
            }
            addedRows += AddCategoryIfMissing(content, source, ChaosSettingsToggle.PoolCategoryLineName,
                "AUTO_ANTHONY_CATEGORY_POOL", insertionIndex + addedRows);
            if (content.GetNodeOrNull<Node>(ChaosSettingsToggle.UltimateChaosLineName) is null)
            {
                ChaosSettingsToggle.UltimateChaosInstance = AddRow(content, source,
                    ChaosSettingsToggle.UltimateChaosLineName, "AUTO_ANTHONY_ULTIMATE_CHAOS",
                    insertionIndex + addedRows);
                addedRows++;
            }
            if (content.GetNodeOrNull<Node>(ChaosSettingsToggle.AddGeneratedCardsLineName) is null)
            {
                ChaosSettingsToggle.AddGeneratedCardsInstance = AddRow(content, source,
                    ChaosSettingsToggle.AddGeneratedCardsLineName, "AUTO_ANTHONY_ADD_GENERATED_CARDS",
                    insertionIndex + addedRows);
                addedRows++;
            }
            if (content.GetNodeOrNull<Node>(ChaosSettingsToggle.PreserveOriginalCardsLineName) is null)
            {
                ChaosSettingsToggle.PreserveOriginalCardsInstance = AddRow(content, source,
                    ChaosSettingsToggle.PreserveOriginalCardsLineName, "AUTO_ANTHONY_PRESERVE_ORIGINAL_CARDS",
                    insertionIndex + addedRows);
                addedRows++;
            }
            if (content.GetNodeOrNull<Node>(ChaosSettingsToggle.DecomposeOriginalCardsLineName) is null)
            {
                ChaosSettingsToggle.DecomposeOriginalCardsInstance = AddRow(content, source,
                    ChaosSettingsToggle.DecomposeOriginalCardsLineName,
                    "AUTO_ANTHONY_DECOMPOSE_ORIGINAL_CARDS", insertionIndex + addedRows);
                addedRows++;
            }
            if (content.GetNodeOrNull<Node>(ChaosSettingsToggle.ReplaceStartingCardsLineName) is null)
            {
                ChaosSettingsToggle.ReplaceStartingCardsInstance = AddRow(content, source,
                    ChaosSettingsToggle.ReplaceStartingCardsLineName, "AUTO_ANTHONY_REPLACE_STARTING_CARDS",
                    insertionIndex + addedRows);
                addedRows++;
            }
            if (ChaosSettingsToggle.IsCardTinkeringLoaded
                && content.GetNodeOrNull<Node>(ChaosSettingsToggle.AnytimeCardEditingLineName) is null)
            {
                ChaosSettingsToggle.AnytimeCardEditingInstance = AddRow(content, source,
                    ChaosSettingsToggle.AnytimeCardEditingLineName, "AUTO_ANTHONY_ANYTIME_CARD_EDITING",
                    insertionIndex + addedRows);
                addedRows++;
            }
            addedRows += AddCategoryIfMissing(content, source, ChaosSettingsToggle.DisplayCategoryLineName,
                "AUTO_ANTHONY_CATEGORY_DISPLAY", insertionIndex + addedRows);
            if (content.GetNodeOrNull<Node>(ChaosSettingsToggle.RandomCardArtLineName) is null)
            {
                ChaosSettingsToggle.RandomCardArtInstance = AddRow(content, source,
                    ChaosSettingsToggle.RandomCardArtLineName, "AUTO_ANTHONY_RANDOM_CARD_ART",
                    insertionIndex + addedRows);
                addedRows++;
            }
            if (content.GetNodeOrNull<Node>(ChaosSettingsToggle.GenerationModeHoverTipsLineName) is null)
            {
                ChaosSettingsToggle.GenerationModeHoverTipsInstance = AddRow(content, source,
                    ChaosSettingsToggle.GenerationModeHoverTipsLineName,
                    "AUTO_ANTHONY_GENERATION_MODE_HOVER_TIPS", insertionIndex + addedRows);
                addedRows++;
            }
            if (content.GetNodeOrNull<Node>(ChaosSettingsToggle.CardInternalIdsLineName) is null)
            {
                ChaosSettingsToggle.CardInternalIdsInstance = AddRow(content, source,
                    ChaosSettingsToggle.CardInternalIdsLineName, "AUTO_ANTHONY_CARD_INTERNAL_IDS",
                    insertionIndex + addedRows);
                addedRows++;
            }
            if (content.GetNodeOrNull<Node>(ChaosSettingsToggle.SurpriseModeLineName) is null)
            {
                ChaosSettingsToggle.SurpriseModeInstance = AddRow(content, source,
                    ChaosSettingsToggle.SurpriseModeLineName, "AUTO_ANTHONY_SURPRISE_MODE",
                    insertionIndex + addedRows);
                addedRows++;
            }
            if (content.GetNodeOrNull<Node>(ChaosSettingsToggle.SurpriseModeLiteLineName) is null)
            {
                ChaosSettingsToggle.SurpriseModeLiteInstance = AddRow(content, source,
                    ChaosSettingsToggle.SurpriseModeLiteLineName, "AUTO_ANTHONY_SURPRISE_MODE_LITE",
                    insertionIndex + addedRows);
                addedRows++;
            }
            if (content.GetNodeOrNull<Node>(ChaosSettingsToggle.SurpriseModeProLineName) is null)
            {
                ChaosSettingsToggle.SurpriseModeProInstance = AddRow(content, source,
                    ChaosSettingsToggle.SurpriseModeProLineName, "AUTO_ANTHONY_SURPRISE_MODE_PRO",
                    insertionIndex + addedRows);
                addedRows++;
            }
            if (content.GetNodeOrNull<Node>(ChaosSettingsToggle.OptimizeHistoryLineName) is null)
            {
                ChaosSettingsToggle.OptimizeHistoryButtonInstance = AddHistoryOptimizationRow(panel, groupSource,
                    insertionIndex + addedRows);
                addedRows++;
            }

            // The group is collapsed whenever the settings screen is constructed. All option/action rows remain
            // siblings in the vanilla VBox so sizing, scrolling, focus navigation and hover tips keep working.
            SetGroupExpanded(panel, group
                ?? content.GetNode<Control>(ChaosSettingsToggle.GroupLineName), expanded: false);

            if (anchor is null) AddTrailingDivider(content, insertionIndex, addedRows);

            // The panel calculated its height and controller focus graph before this postfix inserted the row.
            panel.Call(NSettingsPanel.MethodName.RefreshSize);
            panel.Call(NSettingsPanel.MethodName.UpdateNavigation);
        }
        catch (Exception exception)
        {
            Log.Error($"[AutoAnthony] Failed to add the settings group: {exception}");
        }
    }

    internal static string Text(string key) => new LocString("main_menu_ui", key).GetFormattedText();

    internal static NFastModeTickbox AddRow(Node content, Control source, string lineName, string labelKey,
        int insertionIndex)
    {
        var line = (Control)source.Duplicate(6);
        line.Name = lineName;
        FixOwnerRecursive(line, line);
        var toggle = FindToggle(line)
            ?? throw new InvalidOperationException($"The duplicated settings row '{lineName}' has no tickbox.");
        if (line.GetNodeOrNull<Node>("Label") is MegaRichTextLabel label)
            label.Text = Text(labelKey);
        else if (line.GetNodeOrNull<Node>("Label") is RichTextLabel richLabel)
            richLabel.Text = Text(labelKey);
        content.AddChild(line);
        content.MoveChild(line, insertionIndex);
        return toggle;
    }

    internal static int AddCategoryIfMissing(Node content, Control source, string lineName, string labelKey,
        int insertionIndex)
    {
        if (content.GetNodeOrNull<Node>(lineName) is not null) return 0;
        var line = (Control)source.Duplicate(6);
        line.Name = lineName;
        FixOwnerRecursive(line, line);
        if (FindToggle(line) is { } toggle) toggle.Visible = false;
        SetLabel(line.GetNodeOrNull<Node>("Label"), Text(labelKey));
        content.AddChild(line);
        content.MoveChild(line, insertionIndex);
        return 1;
    }

    private static Control AddGroupRow(NSettingsPanel panel, Control source, int insertionIndex)
    {
        var line = (Control)source.Duplicate(6);
        line.Name = ChaosSettingsToggle.GroupLineName;
        FixOwnerRecursive(line, line);
        panel.Content.AddChild(line);
        panel.Content.MoveChild(line, insertionIndex);
        line.Visible = true;

        SetLabel(line.GetNodeOrNull<Node>("Label"), Text("AUTO_ANTHONY_SETTINGS_GROUP"));
        var button = line.GetNodeOrNull<NOpenModdingScreenButton>("ModdingButton")
            ?? throw new InvalidOperationException("The duplicated Auto-Anthony settings group has no button.");
        button.Name = ChaosSettingsToggle.GroupButtonName;
        button.Enable();
        button.Connect(NClickableControl.SignalName.Released,
            Callable.From<NButton>(_ => OpenDedicatedPage(panel)));
        SetLabel(button.GetNodeOrNull<Node>("Label"), Text("AUTO_ANTHONY_SETTINGS_OPEN"));
        return line;
    }

    private static bool CanOpenDedicatedPage(Node node)
    {
        for (Node? current = node; current is not null; current = current.GetParent())
            if (current is NSubmenuStack) return true;
        return false;
    }

    private static void OpenDedicatedPage(Node node)
    {
        for (Node? current = node; current is not null; current = current.GetParent())
            if (current is NSubmenuStack stack)
            {
                stack.PushSubmenuType<AutoAnthonySettingsSubmenu>();
                return;
            }
        Log.Error("[AutoAnthony] Could not locate a submenu stack for the dedicated settings page.");
    }

    private static int AddLeadingDivider(Node content, Node? source, int insertionIndex)
    {
        if (source is null) return insertionIndex;
        var divider = content.GetNodeOrNull<Node>("AutoAnthonySettingsLeadingDivider");
        if (divider is null)
        {
            divider = source.Duplicate(15);
            divider.Name = "AutoAnthonySettingsLeadingDivider";
            content.AddChild(divider);
        }
        content.MoveChild(divider, insertionIndex);
        return insertionIndex + 1;
    }

    private static void AddTrailingDivider(Node content, int insertionIndex, int addedRows)
    {
        var dividerSource = content.GetNodeOrNull<Node>("FastModeDivider");
        if (addedRows <= 0 || dividerSource is null
            || content.GetNodeOrNull<Node>("AutoAnthonySettingsTrailingDivider") is not null) return;
        var divider = dividerSource.Duplicate(15);
        divider.Name = "AutoAnthonySettingsTrailingDivider";
        content.AddChild(divider);
        content.MoveChild(divider, insertionIndex + addedRows);
    }

    private static NOpenModdingScreenButton AddHistoryOptimizationRow(
        NSettingsPanel panel, Control source, int insertionIndex)
    {
        var line = (Control)source.Duplicate(6);
        line.Name = ChaosSettingsToggle.OptimizeHistoryLineName;
        FixOwnerRecursive(line, line);
        panel.Content.AddChild(line);
        panel.Content.MoveChild(line, insertionIndex);
        SetLabel(line.GetNodeOrNull<Node>("Label"), Text("AUTO_ANTHONY_OPTIMIZE_HISTORY_LABEL"));
        var button = line.GetNodeOrNull<NOpenModdingScreenButton>("ModdingButton")
            ?? throw new InvalidOperationException("The duplicated history optimization row has no button.");
        button.Name = ChaosSettingsToggle.OptimizeHistoryButtonName;
        button.Enable();
        SetHistoryOptimizationButtonLabel(button, "AUTO_ANTHONY_OPTIMIZE_HISTORY_BUTTON");
        button.Connect(NClickableControl.SignalName.Released,
            Callable.From<NButton>(ignored => { _ = OptimizeHistoryAsync(panel, line, button); }));
        return button;
    }

    private static async Task OptimizeHistoryAsync(
        NSettingsPanel panel, Control line, NOpenModdingScreenButton button)
    {
        if (HistorySnapshotOptimizer.IsRunning) return;
        button.Disable();
        try
        {
            var report = await HistorySnapshotOptimizer.OptimizeCurrentProfileAsync((current, total) =>
            {
                if (!GodotObject.IsInstanceValid(button)) return;
                var format = Text("AUTO_ANTHONY_OPTIMIZE_HISTORY_PROGRESS");
                SetLabel(button.GetNodeOrNull<Node>("Label"), format
                    .Replace("{current}", current.ToString(), StringComparison.Ordinal)
                    .Replace("{total}", total.ToString(), StringComparison.Ordinal));
            });
            if (!GodotObject.IsInstanceValid(panel) || !GodotObject.IsInstanceValid(line)) return;
            var screen = ChaosSettingsToggle.FindSettingsScreen(panel);
            if (report.FailedFiles == 0)
            {
                line.Visible = false;
                screen?.ShowToast(new LocString("main_menu_ui", "AUTO_ANTHONY_OPTIMIZE_HISTORY_DONE"));
            }
            else
            {
                button.Enable();
                SetHistoryOptimizationButtonLabel(button, "AUTO_ANTHONY_OPTIMIZE_HISTORY_RETRY");
                screen?.ShowToast(new LocString("main_menu_ui", "AUTO_ANTHONY_OPTIMIZE_HISTORY_FAILED"));
            }
            panel.Call(NSettingsPanel.MethodName.RefreshSize);
            panel.Call(NSettingsPanel.MethodName.UpdateNavigation);
        }
        catch (Exception exception)
        {
            Log.Error($"[AutoAnthony] History optimization UI failed: {exception}");
            if (!GodotObject.IsInstanceValid(button)) return;
            button.Enable();
            SetHistoryOptimizationButtonLabel(button, "AUTO_ANTHONY_OPTIMIZE_HISTORY_RETRY");
            ChaosSettingsToggle.FindSettingsScreen(button)?.ShowToast(
                new LocString("main_menu_ui", "AUTO_ANTHONY_OPTIMIZE_HISTORY_FAILED"));
        }
    }

    private static void SetGroupExpanded(NSettingsPanel panel, Control group, bool expanded)
    {
        foreach (var lineName in ChaosSettingsToggle.OptionLineNames)
            if (panel.Content.GetNodeOrNull<Control>(lineName) is { } option)
                option.Visible = expanded && (lineName != ChaosSettingsToggle.OptimizeHistoryLineName
                                              || HistorySnapshotOptimizer.ShouldShowForCurrentProfile());
        SetGroupButtonLabel(group, expanded);
        panel.Call(NSettingsPanel.MethodName.RefreshSize);
        panel.Call(NSettingsPanel.MethodName.UpdateNavigation);
    }

    private static void SetGroupButtonLabel(Control group, bool expanded)
    {
        var button = group.GetNodeOrNull<Node>(ChaosSettingsToggle.GroupButtonName)
            ?? group.GetNodeOrNull<Node>("ModdingButton");
        SetLabel(button?.GetNodeOrNull<Node>("Label"), Text(expanded
            ? "AUTO_ANTHONY_SETTINGS_GROUP_COLLAPSE"
            : "AUTO_ANTHONY_SETTINGS_GROUP_EXPAND"));
    }

    private static void SetHistoryOptimizationButtonLabel(NOpenModdingScreenButton button, string key) =>
        SetLabel(button.GetNodeOrNull<Node>("Label"), Text(key));

    internal static void SetLabel(Node? node, string value)
    {
        switch (node)
        {
            case MegaRichTextLabel rich: rich.Text = value; break;
            case MegaLabel mega: mega.SetTextAutoSize(value); break;
            case RichTextLabel rich: rich.Text = value; break;
            case Label label: label.Text = value; break;
        }
    }

    internal static NFastModeTickbox? FindToggle(Node root)
    {
        if (root is NFastModeTickbox toggle) return toggle;
        foreach (var child in root.GetChildren())
            if (FindToggle(child) is { } found) return found;
        return null;
    }

    internal static void FixOwnerRecursive(Node root, Node owner)
    {
        foreach (var child in root.GetChildren())
        {
            child.Owner = owner;
            FixOwnerRecursive(child, owner);
        }
    }
}

[HarmonyPatch(typeof(NFastModeTickbox), nameof(NFastModeTickbox.SetFromSettings))]
internal static class UltimateChaosSetFromSettingsPatch
{
    private static bool Prefix(NFastModeTickbox __instance)
    {
        var kind = ChaosSettingsToggle.GetKind(__instance);
        if (kind == ChaosSettingsToggle.Kind.None) return true;
        __instance.IsTicked = ChaosSettingsToggle.GetValue(kind);
        return false;
    }
}

[HarmonyPatch(typeof(NFastModeTickbox), "OnTick")]
internal static class UltimateChaosOnTickPatch
{
    private static bool Prefix(NFastModeTickbox __instance)
    {
        var kind = ChaosSettingsToggle.GetKind(__instance);
        if (kind == ChaosSettingsToggle.Kind.None) return true;
        ChaosSettingsToggle.SetValue(kind, true);
        var actual = ChaosSettingsToggle.GetValue(kind);
        __instance.IsTicked = actual;
        ChaosSettingsToggle.FindSettingsScreen(__instance)?.ShowToast(
            new LocString("main_menu_ui", ChaosSettingsToggle.ToastKey(kind, actual)));
        return false;
    }
}

[HarmonyPatch(typeof(NFastModeTickbox), "OnUntick")]
internal static class UltimateChaosOnUntickPatch
{
    private static bool Prefix(NFastModeTickbox __instance)
    {
        var kind = ChaosSettingsToggle.GetKind(__instance);
        if (kind == ChaosSettingsToggle.Kind.None) return true;
        ChaosSettingsToggle.SetValue(kind, false);
        var actual = ChaosSettingsToggle.GetValue(kind);
        __instance.IsTicked = actual;
        ChaosSettingsToggle.FindSettingsScreen(__instance)?.ShowToast(
            new LocString("main_menu_ui", ChaosSettingsToggle.ToastKey(kind, actual)));
        return false;
    }
}

[HarmonyPatch(typeof(NFastModeHoverTip), "OnHovered")]
internal static class UltimateChaosHoverTipPatch
{
    private static bool Prefix(NFastModeHoverTip __instance)
    {
        var name = __instance.Name.ToString();
        var (titleKey, descriptionKey) = name switch
        {
            ChaosSettingsToggle.EnabledLineName =>
                ("AUTO_ANTHONY_ENABLED", "AUTO_ANTHONY_ENABLED_DESCRIPTION"),
            ChaosSettingsToggle.AddGeneratedCardsLineName =>
                ("AUTO_ANTHONY_ADD_GENERATED_CARDS", "AUTO_ANTHONY_ADD_GENERATED_CARDS_DESCRIPTION"),
            ChaosSettingsToggle.ReplaceStartingCardsLineName =>
                ("AUTO_ANTHONY_REPLACE_STARTING_CARDS", "AUTO_ANTHONY_REPLACE_STARTING_CARDS_DESCRIPTION"),
            ChaosSettingsToggle.UltimateChaosLineName =>
                ("AUTO_ANTHONY_ULTIMATE_CHAOS", "AUTO_ANTHONY_ULTIMATE_CHAOS_DESCRIPTION"),
            ChaosSettingsToggle.NumericBalanceOptimizationLineName =>
                ("AUTO_ANTHONY_NUMERIC_BALANCE_OPTIMIZATION", "AUTO_ANTHONY_NUMERIC_BALANCE_OPTIMIZATION_DESCRIPTION"),
            ChaosSettingsToggle.NumericRandomModeLineName =>
                ("AUTO_ANTHONY_NUMERIC_RANDOM_MODE", "AUTO_ANTHONY_NUMERIC_RANDOM_MODE_DESCRIPTION"),
            ChaosSettingsToggle.PreserveOriginalCardsLineName =>
                ("AUTO_ANTHONY_PRESERVE_ORIGINAL_CARDS", "AUTO_ANTHONY_PRESERVE_ORIGINAL_CARDS_DESCRIPTION"),
            ChaosSettingsToggle.DecomposeOriginalCardsLineName =>
                ("AUTO_ANTHONY_DECOMPOSE_ORIGINAL_CARDS",
                    "AUTO_ANTHONY_DECOMPOSE_ORIGINAL_CARDS_DESCRIPTION"),
            ChaosSettingsToggle.AnytimeCardEditingLineName =>
                ("AUTO_ANTHONY_ANYTIME_CARD_EDITING", "AUTO_ANTHONY_ANYTIME_CARD_EDITING_DESCRIPTION"),
            ChaosSettingsToggle.RandomCardArtLineName =>
                ("AUTO_ANTHONY_RANDOM_CARD_ART", "AUTO_ANTHONY_RANDOM_CARD_ART_DESCRIPTION"),
            ChaosSettingsToggle.GenerationModeHoverTipsLineName =>
                ("AUTO_ANTHONY_GENERATION_MODE_HOVER_TIPS",
                    "AUTO_ANTHONY_GENERATION_MODE_HOVER_TIPS_DESCRIPTION"),
            ChaosSettingsToggle.NumericCategoryLineName or ChaosSettingsToggle.PoolCategoryLineName
                or ChaosSettingsToggle.DisplayCategoryLineName => (string.Empty, string.Empty),
            ChaosSettingsToggle.CardInternalIdsLineName =>
                ("AUTO_ANTHONY_CARD_INTERNAL_IDS", "AUTO_ANTHONY_CARD_INTERNAL_IDS_DESCRIPTION"),
            ChaosSettingsToggle.SurpriseModeLineName =>
                ("AUTO_ANTHONY_SURPRISE_MODE", "AUTO_ANTHONY_SURPRISE_MODE_DESCRIPTION"),
            ChaosSettingsToggle.SurpriseModeLiteLineName =>
                ("AUTO_ANTHONY_SURPRISE_MODE_LITE", "AUTO_ANTHONY_SURPRISE_MODE_LITE_DESCRIPTION"),
            ChaosSettingsToggle.SurpriseModeProLineName =>
                ("AUTO_ANTHONY_SURPRISE_MODE_PRO", "AUTO_ANTHONY_SURPRISE_MODE_PRO_DESCRIPTION"),
            _ => (null, null)
        };
        if (titleKey is null || descriptionKey is null) return true;
        if (titleKey.Length == 0) return false;
        var tip = new HoverTip(
            new LocString("main_menu_ui", titleKey),
            new LocString("main_menu_ui", descriptionKey));
        var offset = NSettingsScreen.settingTipsOffset;
        for (Node? current = __instance; current is not null; current = current.GetParent())
            if (current is AutoAnthonySettingsSubmenu)
            {
                // The dedicated page is narrower than the vanilla settings panel; leave a little more breathing
                // room between its option column and hover cards without changing the legacy inline fallback.
                offset += new Vector2(72f, 0f);
                break;
            }
        NHoverTipSet.CreateAndShow(__instance, tip)?
            .SetGlobalPosition(__instance.GlobalPosition + offset);
        return false;
    }
}
