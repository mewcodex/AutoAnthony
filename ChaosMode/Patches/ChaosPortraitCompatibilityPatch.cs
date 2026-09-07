using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace AutoAnthony;

/// <summary>
/// Load a generated card from the already-resolved source portrait path. Besides avoiding repeated path probes,
/// this prevents generic CardModel.Portrait patches from treating ChaosCard* as a new vanilla filename and testing
/// several nonexistent resources on every visual refresh. Source-model PortraitPath patches have already been
/// composed once by <see cref="ChaosPortraitCompatibility.ResolvePath"/>.
/// </summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.Portrait), MethodType.Getter)]
[HarmonyPriority(Priority.First)]
internal static class ChaosPortraitTextureCachePatch
{
    private static bool Prefix(CardModel __instance, ref Texture2D __result)
    {
        if (__instance is not ChaosCardModel card) return true;
        __result = ResourceLoader.Load<Texture2D>(ChaosPortraitCompatibility.ResolvePath(card.EffectivePortraitDefinition), null,
            ResourceLoader.CacheMode.Reuse);
        return false;
    }
}

/// <summary>
/// Mirrors direct NCard texture replacements from the generated card's saved original-art source. Run at the
/// lowest Harmony priority so this represents the same final layer that wins on the original source card.
/// </summary>
[HarmonyPatch(typeof(NCard), nameof(NCard.UpdateVisuals))]
[HarmonyPriority(Priority.Last)]
internal static class ChaosDirectPortraitCompatibilityPatch
{
    private static readonly AccessTools.FieldRef<NCard, TextureRect> PortraitRef =
        AccessTools.FieldRefAccess<NCard, TextureRect>("_portrait");
    private static readonly AccessTools.FieldRef<NCard, TextureRect> AncientPortraitRef =
        AccessTools.FieldRefAccess<NCard, TextureRect>("_ancientPortrait");

    private static void Postfix(NCard __instance)
    {
        if (__instance.Model is not ChaosCardModel card
            || !ChaosPortraitCompatibility.TryResolveDirectTexture(card.EffectivePortraitDefinition, out var texture))
            return;

        Apply(PortraitRef(__instance), texture);
        Apply(AncientPortraitRef(__instance), texture);
    }

    private static void Apply(TextureRect? portrait, Texture2D texture)
    {
        if (portrait is null) return;
        if (ReferenceEquals(portrait.Texture, texture)
            && portrait.StretchMode == (TextureRect.StretchModeEnum)6
            && portrait.TextureFilter == CanvasItem.TextureFilterEnum.Linear)
            return;
        portrait.Texture = texture;
        // Match the usual original-card replacement presentation (KeepAspectCentered and linear filtering).
        portrait.StretchMode = (TextureRect.StretchModeEnum)6;
        portrait.TextureFilter = CanvasItem.TextureFilterEnum.Linear;
    }
}
