using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;

namespace AutoAnthony;

/// <summary>
/// Opt-in integration surface for companion editors that create a complete generated-card shell. The base mod
/// never calls this API, so ordinary generated cards and ordinary component tinkering retain their strict rules.
/// </summary>
public static class AutoAnthonyFreeformCardApi
{
    public const int ApiVersion = 2;

    public static ChaosCardModel CreateForDeck(Player owner, GeneratedCard definition)
        => CreateForDeck(owner, definition, null);

    public static ChaosCardModel CreateForDeck(Player owner, GeneratedCard definition, string? portraitPath)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ValidateHost(definition);
        var card = owner.RunState.CreateCard(ChaosCardRegistry.Canonical(definition.Character, 0), owner)
                   as ChaosCardModel
                   ?? throw new InvalidOperationException("The selected Auto Anthony card host is unavailable.");
        card.ApplyFreeformDefinition(definition);
        card.FreeformPortraitPath = portraitPath ?? string.Empty;
        return card;
    }

    public static ChaosCardModel CreateForDeck(Player owner, string profileId, GeneratedCard definition) =>
        CreateForDeck(owner, profileId, definition, null);

    public static ChaosCardModel CreateForDeck(Player owner, string profileId, GeneratedCard definition,
        string? portraitPath)
    {
        var card = CreateProfileHost(owner, profileId, definition, forCombat: false);
        card.ApplyFreeformDefinition(definition);
        card.FreeformPortraitPath = portraitPath ?? string.Empty;
        return card;
    }

    public static ChaosCardModel CreateForCombat(Player owner, GeneratedCard definition)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ValidateHost(definition);
        var state = owner.Creature?.CombatState
                    ?? throw new InvalidOperationException("The player is not in an active combat.");
        var card = state.CreateCard(ChaosCardRegistry.Canonical(definition.Character, 0), owner)
                   as ChaosCardModel
                   ?? throw new InvalidOperationException("The selected Auto Anthony card host is unavailable.");
        card.ApplyFreeformDefinition(definition);
        return card;
    }

    public static ChaosCardModel CreateForCombat(Player owner, string profileId, GeneratedCard definition) =>
        CreateForCombat(owner, profileId, definition, null);

    public static ChaosCardModel CreateForCombat(Player owner, string profileId, GeneratedCard definition,
        string? portraitPath)
    {
        var card = CreateProfileHost(owner, profileId, definition, forCombat: true);
        card.ApplyFreeformDefinition(definition);
        card.FreeformPortraitPath = portraitPath ?? string.Empty;
        return card;
    }

    public static ChaosCardModel CreatePreview(Player owner, GeneratedCard definition) =>
        CreateForDeck(owner, definition);

    public static ChaosCardModel CreatePreview(Player owner, GeneratedCard definition, string? portraitPath) =>
        CreateForDeck(owner, definition, portraitPath);

    public static ChaosCardModel CreatePreview(Player owner, string profileId, GeneratedCard definition) =>
        CreateForDeck(owner, profileId, definition);

    public static ChaosCardModel CreatePreview(Player owner, string profileId, GeneratedCard definition,
        string? portraitPath) => CreateForDeck(owner, profileId, definition, portraitPath);

    public static CardTinkeringValidationResult Validate(GeneratedCard definition) =>
        CardTinkeringApi.Validate(definition, definition.Operations);

    private static void ValidateHost(GeneratedCard definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!ChaosRunDefinitions.SupportedPools.Contains(definition.Character))
            throw new ArgumentOutOfRangeException(nameof(definition), "Unsupported generated-card character.");
        if (ChaosCardRegistry.TypesFor(definition.Character).Count == 0)
            throw new InvalidOperationException("The selected generated-card pool has no runtime host.");
    }

    private static ChaosCardModel CreateProfileHost(Player owner, string profileId, GeneratedCard definition,
        bool forCombat)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(profileId))
            throw new ArgumentException("A component profile ID is required.", nameof(profileId));
        if (!ComponentApi.TryGetProfileRequest(profileId, false, out var request))
            throw new InvalidOperationException($"No component generation profile is registered for '{profileId}'.");
        if (definition.Character != request.Character)
            throw new ArgumentException(
                $"Definition uses {definition.Character}, but profile '{profileId}' uses {request.Character}.",
                nameof(definition));

        if (string.Equals(profileId, ComponentProfileRequest.BuiltInId(request.Character),
                StringComparison.Ordinal))
        {
            ValidateHost(definition);
            var canonical = ChaosCardRegistry.Canonical(definition.Character, 0);
            return forCombat
                ? (owner.Creature?.CombatState
                   ?? throw new InvalidOperationException("The player is not in an active combat."))
                    .CreateCard(canonical, owner) as ChaosCardModel
                    ?? throw new InvalidOperationException("The selected Auto Anthony card host is unavailable.")
                : owner.RunState.CreateCard(canonical, owner) as ChaosCardModel
                  ?? throw new InvalidOperationException("The selected Auto Anthony card host is unavailable.");
        }
        AutoAnthonyEditorApi.RequireSupport(profileId, ExternalEditorCapabilities.FreeformCardCreation);
        return ExternalComponentCharacterApi.CreateCardHost(profileId, owner, forCombat);
    }
}
