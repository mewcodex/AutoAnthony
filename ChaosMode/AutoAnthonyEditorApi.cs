using ChaosCardGenerator;

namespace AutoAnthony;

/// <summary>
/// A complete, saveable display identity selected for one editor-owned generated card. <see cref="PortraitPath"/>
/// is always the stable native portrait identity; the optional variant fields preserve the exact enabled
/// replacement selected by random-card-art mode while still allowing a vanilla fallback on another device.
/// </summary>
public sealed record AutoAnthonyEditorIdentity(
    GeneratedCardName Name,
    string PortraitPath,
    string? PortraitSourceId,
    string? PortraitVariantId,
    string? PortraitVariantPath);

/// <summary>
/// Explicit card-editor boundary for changing the name and portrait of one generated card instance. Ordinary
/// <see cref="ChaosCardModel.ApplyTinkeredDefinition"/> calls remain identity-preserving.
/// </summary>
public static class AutoAnthonyEditorApi
{
    public const int ApiVersion = 1;

    /// <summary>
    /// Selects a new name and portrait using the same component relevance, card-shell, Ancient-art and enabled
    /// portrait-mod rules as run-pool generation. This method is deterministic for a fixed card and seed and does
    /// not mutate <paramref name="current"/> or any live card.
    /// </summary>
    public static AutoAnthonyEditorIdentity RerollEditorIdentity(GeneratedCard current, int seed)
    {
        ArgumentNullException.ThrowIfNull(current);
        return ChaosRunDefinitions.RerollEditorIdentity(current, seed);
    }

    /// <summary>
    /// Applies an edited effect definition together with an explicitly selected identity to one live card. The
    /// identity name is authoritative; every other card-shell property remains immutable and is validated by the
    /// card model. Both the definition and portrait identity are stored as per-card saved properties.
    /// </summary>
    public static void ApplyEditorDefinition(ChaosCardModel card, GeneratedCard definition,
        AutoAnthonyEditorIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(identity.Name);
        var identified = definition with { Name = identity.Name };
        ChaosRunDefinitions.ValidateEditorIdentity(identified, identity);
        card.ApplyEditorDefinitionInternal(identified, identity);
    }
}
