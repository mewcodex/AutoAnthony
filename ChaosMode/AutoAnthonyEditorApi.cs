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
/// Optional name/portrait authority for an external component profile. The provider owns its native/Ancient art
/// boundaries and enabled portrait-variant policy; AutoAnthony owns persistence and card-shell validation.
/// </summary>
public interface IExternalEditorIdentityProvider
{
    AutoAnthonyEditorIdentity RerollIdentity(GeneratedCard current, int seed);
    bool IsValidIdentity(GeneratedCard definition, AutoAnthonyEditorIdentity identity) => true;
}

[Flags]
public enum ExternalEditorCapabilities
{
    None = 0,
    GeneratedCardEditing = 1 << 0,
    FreeformCardCreation = 1 << 1,
    IdentityReroll = 1 << 2,
    NativeCardDecomposition = 1 << 3,
    All = GeneratedCardEditing | FreeformCardCreation | IdentityReroll | NativeCardDecomposition
}

/// <summary>
/// Explicit card-editor boundary for changing the name and portrait of one generated card instance. Ordinary
/// <see cref="ChaosCardModel.ApplyTinkeredDefinition"/> calls remain identity-preserving.
/// </summary>
public static class AutoAnthonyEditorApi
{
    public const int ApiVersion = 2;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, IExternalEditorIdentityProvider> IdentityProviders =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ExternalEditorCapabilities> ExternalCapabilities =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Explicit opt-in for editor features on an external profile. Ordinary external character registration grants
    /// no editor capability, so a character mod can support AutoAnthony generation without supporting an editor.
    /// </summary>
    public static void RegisterExternalCapabilities(string profileId, ExternalEditorCapabilities capabilities)
    {
        ValidateExternalProfileId(profileId);
        if (capabilities == ExternalEditorCapabilities.None
            || (capabilities & ~ExternalEditorCapabilities.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(capabilities));
        lock (Sync)
            ExternalCapabilities[profileId] = ExternalCapabilities.GetValueOrDefault(profileId) | capabilities;
    }

    public static bool Supports(string profileId, ExternalEditorCapabilities capabilities)
    {
        if (capabilities == ExternalEditorCapabilities.None) return true;
        if (!ComponentApi.TryGetProfileRequest(profileId, false, out var request)) return false;
        if (string.Equals(profileId, ComponentProfileRequest.BuiltInId(request.Character),
                StringComparison.Ordinal)) return true;
        lock (Sync)
            return (ExternalCapabilities.GetValueOrDefault(profileId) & capabilities) == capabilities;
    }

    public static void RegisterIdentityProvider(string profileId, IExternalEditorIdentityProvider provider)
    {
        ValidateExternalProfileId(profileId);
        ArgumentNullException.ThrowIfNull(provider);
        lock (Sync)
        {
            if (!IdentityProviders.TryAdd(profileId, provider))
                throw new InvalidOperationException(
                    $"An editor identity provider is already registered for '{profileId}'.");
            ExternalCapabilities[profileId] = ExternalCapabilities.GetValueOrDefault(profileId)
                                              | ExternalEditorCapabilities.GeneratedCardEditing
                                              | ExternalEditorCapabilities.IdentityReroll;
        }
    }

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

    public static AutoAnthonyEditorIdentity RerollEditorIdentity(string profileId, GeneratedCard current, int seed)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!ComponentApi.TryGetProfileRequest(profileId, false, out var request)
            || current.Character != request.Character)
            throw new ArgumentException("The card does not belong to the requested component profile.",
                nameof(current));
        if (string.Equals(profileId, ComponentProfileRequest.BuiltInId(request.Character),
                StringComparison.Ordinal))
            return RerollEditorIdentity(current, seed);
        RequireSupport(profileId, ExternalEditorCapabilities.IdentityReroll);
        IExternalEditorIdentityProvider provider;
        lock (Sync)
            provider = IdentityProviders.TryGetValue(profileId, out var registered)
                ? registered
                : throw new InvalidOperationException(
                    $"External profile '{profileId}' did not register an editor identity provider.");
        return provider.RerollIdentity(current, seed)
               ?? throw new InvalidOperationException(
                   $"External profile '{profileId}' returned no editor identity.");
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

    public static void ApplyEditorDefinition(ChaosCardModel card, string profileId, GeneratedCard definition,
        AutoAnthonyEditorIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(identity.Name);
        if (!ExternalComponentCharacterApi.TryGetProfileId(card, out var actualProfile)
            || !string.Equals(actualProfile, profileId, StringComparison.Ordinal))
            throw new ArgumentException("The live card does not belong to the requested component profile.",
                nameof(card));
        if (!ComponentApi.TryGetProfileRequest(profileId, false, out var request)
            || definition.Character != request.Character)
            throw new ArgumentException("The definition does not belong to the requested component profile.",
                nameof(definition));

        var identified = definition with { Name = identity.Name };
        if (string.Equals(profileId, ComponentProfileRequest.BuiltInId(request.Character),
                StringComparison.Ordinal))
            ChaosRunDefinitions.ValidateEditorIdentity(identified, identity);
        else
        {
            RequireSupport(profileId, ExternalEditorCapabilities.GeneratedCardEditing
                                      | ExternalEditorCapabilities.IdentityReroll);
            IExternalEditorIdentityProvider provider;
            lock (Sync)
                provider = IdentityProviders.TryGetValue(profileId, out var registered)
                    ? registered
                    : throw new InvalidOperationException(
                        $"External profile '{profileId}' did not register an editor identity provider.");
            if (!provider.IsValidIdentity(identified, identity))
                throw new ArgumentException(
                    $"External profile '{profileId}' rejected the selected card identity.", nameof(identity));
        }
        card.ApplyEditorDefinitionInternal(identified, identity);
    }

    internal static void RequireSupport(string profileId, ExternalEditorCapabilities capabilities)
    {
        if (!Supports(profileId, capabilities))
            throw new NotSupportedException(
                $"External profile '{profileId}' did not opt in to editor capability '{capabilities}'.");
    }

    private static void ValidateExternalProfileId(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (profileId.Any(character => character > 0x7f))
            throw new ArgumentException("Profile IDs must be ASCII.", nameof(profileId));
        if (Enum.GetValues<GeneratedCharacter>().Any(character => string.Equals(profileId,
                ComponentProfileRequest.BuiltInId(character), StringComparison.Ordinal)))
            throw new ArgumentException("Built-in editor capabilities are always available and cannot be registered.",
                nameof(profileId));
    }
}
