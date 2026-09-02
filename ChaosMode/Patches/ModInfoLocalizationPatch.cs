using System.Text;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace AutoAnthony.Patches;

[HarmonyPatch(typeof(NModInfoContainer), nameof(NModInfoContainer.Fill))]
internal static class ModInfoLocalizationPatch
{
    private static bool Prefix(NModInfoContainer __instance, Mod mod)
    {
        ModInfoLocalization.Apply(mod);
        if (mod.manifest?.id != "AutoAnthony") return true;

        // Exported games do not import a raw mod PNG into a Texture2D automatically.
        // Fill this mod's panel ourselves and decode the mounted PCK file directly.
        __instance.GetNode<MegaRichTextLabel>("ModTitle").Text = mod.manifest.name ?? "<No Name>";
        __instance.GetNode<TextureRect>("ModImage").Texture = LoadModImage();

        var description = new StringBuilder()
            .AppendLine($"[gold]Author[/gold]: {mod.manifest.author ?? "unknown"}")
            .AppendLine($"[gold]Version[/gold]: {mod.manifest.version ?? "unknown"}")
            .AppendLine()
            .AppendLine(mod.manifest.description ?? "No description");
        if (mod.errors is { Count: > 0 })
        {
            description.AppendLine();
            foreach (var error in mod.errors)
                description.AppendLine($"[red]{error.GetFormattedText()}[/red]");
        }
        __instance.GetNode<MegaRichTextLabel>("ModDescription").Text = description.ToString();
        return false;
    }

    private static Texture2D? LoadModImage()
    {
        const string path = "res://AutoAnthony/mod_image.png";
        if (!Godot.FileAccess.FileExists(path)) return null;
        var image = new Image();
        if (image.LoadPngFromBuffer(Godot.FileAccess.GetFileAsBytes(path)) == Error.Ok)
            return ImageTexture.CreateFromImage(image);
        return null;
    }
}

[HarmonyPatch(typeof(NModMenuRow), nameof(NModMenuRow._Ready))]
internal static class ModMenuNameLocalizationPatch
{
    private static void Prefix(NModMenuRow __instance)
    {
        if (__instance.Mod is { } mod) ModInfoLocalization.Apply(mod);
    }
}

internal static class ModInfoLocalization
{
    public static void Apply(Mod mod)
    {
        if (mod.manifest?.id != "AutoAnthony") return;
        mod.manifest.name = new LocString("main_menu_ui", "AUTO_ANTHONY_MOD_NAME").GetFormattedText();
        mod.manifest.description = new LocString("main_menu_ui", "AUTO_ANTHONY_MOD_DESCRIPTION").GetFormattedText();
    }
}
