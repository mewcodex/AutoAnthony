using AutoAnthony;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using static AutoAnthonyCardTinkering.TinkeringText;

namespace AutoAnthonyCardTinkering;

internal static class FreeformSettings
{
    internal static bool Enabled
    {
        get => TinkeringSettings.FreeformEnabled;
        set => TinkeringSettings.FreeformEnabled = value;
    }

    internal static bool IgnoreCapacityLimit
    {
        get => TinkeringSettings.IgnoreCapacityLimit;
        set => TinkeringSettings.IgnoreCapacityLimit = value;
    }
}

[HarmonyPatch]
internal static class FreeformSettingsRowPatch
{
    internal enum SettingKind { None, Freeform, IgnoreCapacity }
    internal const string FreeformLineName = "AutoAnthonyCardTinkeringFreeform";
    internal const string IgnoreCapacityLineName = "AutoAnthonyCardTinkeringIgnoreCapacity";
    private static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("AutoAnthony.Patches.AutoAnthonySettingsSubmenu"), "_Ready")!;

    private static void Postfix(Node __instance)
    {
        try
        {
            InstallLocalization();
            var source = Find(__instance, "AutoAnthonyAnytimeCardEditing") as Control;
            if (source?.GetParent() is not Node parent) return;
            Control anchor = source;
            if (CardTinkeringFeatureGate.SupportsFreeformApi)
            {
                var freeform = Find(__instance, FreeformLineName) as Control;
                if (freeform is null)
                {
                    var created = CreateLine(source, FreeformLineName,
                        Localize("创造卡牌", "Card creation"));
                    parent.AddChild(created.Line);
                    parent.MoveChild(created.Line, anchor.GetIndex() + 1);
                    // The native tickbox binds textures during _Ready, so synchronize after entering the tree.
                    created.Toggle.IsTicked = FreeformSettings.Enabled;
                    freeform = created.Line;
                }
                anchor = freeform;
            }
            if (Find(__instance, IgnoreCapacityLineName) is null)
            {
                var created = CreateLine(source, IgnoreCapacityLineName,
                    Localize("忽略容量限制", "Ignore capacity limit"));
                parent.AddChild(created.Line);
                parent.MoveChild(created.Line, anchor.GetIndex() + 1);
                created.Toggle.IsTicked = FreeformSettings.IgnoreCapacityLimit;
            }
        }
        catch (Exception exception)
        {
            Log.Error($"[CardTinkering] Failed to add the Create Cards setting row: {exception}");
        }
    }

    private static (Control Line, NFastModeTickbox Toggle) CreateLine(Control source,
        string lineName, string label)
    {
        // Clone the adjacent native Auto Anthony option so the exact margins, auto-sizing label, focus reticle,
        // tickbox art, controller navigation and hover animation are inherited from the same scene.
        var line = source.Duplicate(6) as Control
                   ?? throw new InvalidOperationException("The native option row could not be duplicated.");
        line.Name = lineName;
        FixOwner(line, line);
        SetLabel(Find(line, "Label"), label);
        var toggle = FindToggle(line)
                     ?? throw new InvalidOperationException("The native option row has no tickbox.");
        return (line, toggle);
    }

    internal static SettingKind KindOf(Node node)
    {
        for (Node? current = node; current is not null; current = current.GetParent())
        {
            var name = current.Name.ToString();
            if (name == FreeformLineName) return SettingKind.Freeform;
            if (name == IgnoreCapacityLineName) return SettingKind.IgnoreCapacity;
        }
        return SettingKind.None;
    }

    internal static bool Value(SettingKind kind) => kind switch
    {
        SettingKind.Freeform => FreeformSettings.Enabled,
        SettingKind.IgnoreCapacity => FreeformSettings.IgnoreCapacityLimit,
        _ => false
    };

    internal static void SetValue(SettingKind kind, bool value)
    {
        if (kind == SettingKind.Freeform) FreeformSettings.Enabled = value;
        else if (kind == SettingKind.IgnoreCapacity) FreeformSettings.IgnoreCapacityLimit = value;
    }

    internal static string ToastKey(SettingKind kind, bool value)
    {
        var stem = kind switch
        {
            SettingKind.Freeform => "AUTO_ANTHONY_CARD_CREATION",
            SettingKind.IgnoreCapacity => "AUTO_ANTHONY_IGNORE_CAPACITY",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        return $"{stem}_{(value ? "ON" : "OFF")}";
    }

    internal static NSettingsScreen? FindSettingsScreen(Node node)
    {
        for (var current = node.GetParent(); current is not null; current = current.GetParent())
            if (current is NSettingsScreen screen) return screen;
        return null;
    }

    private static Node? Find(Node root, string name)
    {
        if (root.Name.ToString() == name) return root;
        foreach (var child in root.GetChildren())
            if (Find(child, name) is { } found) return found;
        return null;
    }

    private static NFastModeTickbox? FindToggle(Node root)
    {
        if (root is NFastModeTickbox toggle) return toggle;
        foreach (var child in root.GetChildren())
            if (FindToggle(child) is { } found) return found;
        return null;
    }

    private static void FixOwner(Node root, Node owner)
    {
        foreach (var child in root.GetChildren())
        {
            child.Owner = owner;
            FixOwner(child, owner);
        }
    }

    private static void SetLabel(Node? node, string text)
    {
        if (node is RichTextLabel rich) rich.Text = text;
        else if (node is Label label) label.Text = text;
    }

    private static void InstallLocalization()
    {
        LocManager.Instance.GetTable("main_menu_ui").MergeWith(new Dictionary<string, string>
        {
            ["AUTO_ANTHONY_CARD_CREATION"] = Localize("创造卡牌", "Card creation"),
            ["AUTO_ANTHONY_CARD_CREATION_DESCRIPTION"] = Localize(
                "在卡组页面显示创造卡牌入口，可从完整组件目录组装并生成卡牌。",
                "Show the Create Cards entry on the deck screen and assemble cards from the complete component catalog."),
            ["AUTO_ANTHONY_CARD_CREATION_ON"] = Localize(
                "已开启创造卡牌。", "Card creation enabled."),
            ["AUTO_ANTHONY_CARD_CREATION_OFF"] = Localize(
                "已关闭创造卡牌。", "Card creation disabled."),
            ["AUTO_ANTHONY_IGNORE_CAPACITY"] = Localize("忽略容量限制", "Ignore capacity limit"),
            ["AUTO_ANTHONY_IGNORE_CAPACITY_DESCRIPTION"] = Localize(
                "允许编辑后的卡牌超出显示容量；组件兼容性与卡牌结构规则仍然生效。",
                "Allow edited cards to exceed their displayed capacity; component compatibility and card-structure rules still apply."),
            ["AUTO_ANTHONY_IGNORE_CAPACITY_ON"] = Localize(
                "已忽略卡牌容量限制。", "Card capacity limits ignored."),
            ["AUTO_ANTHONY_IGNORE_CAPACITY_OFF"] = Localize(
                "已恢复卡牌容量限制。", "Card capacity limits restored.")
        });
    }
}

[HarmonyPatch(typeof(NFastModeTickbox), "OnTick")]
internal static class FreeformSettingOnPatch
{
    private static bool Prefix(NFastModeTickbox __instance)
    {
        var kind = FreeformSettingsRowPatch.KindOf(__instance);
        if (kind == FreeformSettingsRowPatch.SettingKind.None) return true;
        FreeformSettingsRowPatch.SetValue(kind, true);
        var actual = FreeformSettingsRowPatch.Value(kind);
        __instance.IsTicked = actual;
        FreeformSettingsRowPatch.FindSettingsScreen(__instance)?.ShowToast(
            new LocString("main_menu_ui", FreeformSettingsRowPatch.ToastKey(kind, actual)));
        return false;
    }
}

[HarmonyPatch(typeof(NFastModeTickbox), "OnUntick")]
internal static class FreeformSettingOffPatch
{
    private static bool Prefix(NFastModeTickbox __instance)
    {
        var kind = FreeformSettingsRowPatch.KindOf(__instance);
        if (kind == FreeformSettingsRowPatch.SettingKind.None) return true;
        FreeformSettingsRowPatch.SetValue(kind, false);
        var actual = FreeformSettingsRowPatch.Value(kind);
        __instance.IsTicked = actual;
        FreeformSettingsRowPatch.FindSettingsScreen(__instance)?.ShowToast(
            new LocString("main_menu_ui", FreeformSettingsRowPatch.ToastKey(kind, actual)));
        return false;
    }
}

[HarmonyPatch(typeof(NFastModeTickbox), nameof(NFastModeTickbox.SetFromSettings))]
internal static class FreeformSettingSyncPatch
{
    private static bool Prefix(NFastModeTickbox __instance)
    {
        var kind = FreeformSettingsRowPatch.KindOf(__instance);
        if (kind == FreeformSettingsRowPatch.SettingKind.None) return true;
        __instance.IsTicked = FreeformSettingsRowPatch.Value(kind);
        return false;
    }
}

[HarmonyPatch(typeof(NFastModeHoverTip), "OnHovered")]
internal static class FreeformSettingHoverPatch
{
    private static bool Prefix(NFastModeHoverTip __instance)
    {
        var kind = FreeformSettingsRowPatch.KindOf(__instance);
        if (kind == FreeformSettingsRowPatch.SettingKind.None) return true;
        var (titleKey, descriptionKey) = kind == FreeformSettingsRowPatch.SettingKind.Freeform
            ? ("AUTO_ANTHONY_CARD_CREATION", "AUTO_ANTHONY_CARD_CREATION_DESCRIPTION")
            : ("AUTO_ANTHONY_IGNORE_CAPACITY", "AUTO_ANTHONY_IGNORE_CAPACITY_DESCRIPTION");
        var tip = new HoverTip(new LocString("main_menu_ui", titleKey),
            new LocString("main_menu_ui", descriptionKey));
        NHoverTipSet.CreateAndShow(__instance, tip)?.SetGlobalPosition(
            __instance.GlobalPosition + NSettingsScreen.settingTipsOffset + new Vector2(72f, 0f));
        return false;
    }
}
