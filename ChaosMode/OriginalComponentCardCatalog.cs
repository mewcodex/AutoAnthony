using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;

namespace AutoAnthony;

/// <summary>
/// Canonical source-card lookup shared by operation execution and generated-card hover tips. Models themselves
/// are intentionally not cached because ModelDb owns their lifecycle; only the immutable Colorless recipe IDs
/// are indexed once instead of repeatedly scanning every recipe for every card in ModelDb.All.
/// </summary>
internal static class OriginalComponentCardCatalog
{
    private static readonly IReadOnlySet<string> ColorlessTypeNames = CharacterComponentCatalogs
        .Get(GeneratedCharacter.Colorless).Recipes
        .Select(recipe => recipe.Id)
        .ToHashSet(StringComparer.Ordinal);

    internal static IEnumerable<CardModel> For(GeneratedCharacter character) => character switch
    {
        GeneratedCharacter.Ironclad => ModelDb.CardPool<IroncladCardPool>().AllCards,
        GeneratedCharacter.Silent => ModelDb.CardPool<SilentCardPool>().AllCards,
        GeneratedCharacter.Defect => ModelDb.CardPool<DefectCardPool>().AllCards,
        GeneratedCharacter.Necrobinder => ModelDb.CardPool<NecrobinderCardPool>().AllCards,
        GeneratedCharacter.Regent => ModelDb.CardPool<RegentCardPool>().AllCards,
        GeneratedCharacter.Colorless => ModelDb.All.OfType<CardModel>()
            .Where(card => ColorlessTypeNames.Contains(card.GetType().Name)),
        _ => throw new ArgumentOutOfRangeException(nameof(character))
    };
}
