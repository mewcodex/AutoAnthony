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
    public const int ApiVersion = 1;

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

    public static ChaosCardModel CreatePreview(Player owner, GeneratedCard definition) =>
        CreateForDeck(owner, definition);

    public static ChaosCardModel CreatePreview(Player owner, GeneratedCard definition, string? portraitPath) =>
        CreateForDeck(owner, definition, portraitPath);

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
}
