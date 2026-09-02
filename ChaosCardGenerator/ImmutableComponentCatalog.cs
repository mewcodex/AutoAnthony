namespace ChaosCardGenerator;

/// <summary>
/// General-purpose immutable catalog for external component packages. It derives all indexes from native recipes
/// using the same structural identities as the built-in catalogs, so extension authors do not need to duplicate
/// AutoAnthony's indexing implementation.
/// </summary>
public sealed class ImmutableComponentCatalog : IComponentCatalog
{
    public GeneratedCharacter Character { get; }
    public IReadOnlyList<IroncladCardRecipe> Recipes { get; }
    public IReadOnlyList<ComponentAtom> Atoms { get; }
    public IReadOnlyList<int> ComponentCounts { get; }
    public IReadOnlySet<string> AtomKeys { get; }
    public IReadOnlyDictionary<string, int> AtomCounts { get; }
    public IReadOnlyDictionary<int, int> ComponentCountCounts { get; }
    public IReadOnlyDictionary<CardTag, int> TagCounts { get; }

    public ImmutableComponentCatalog(GeneratedCharacter character,
        IEnumerable<IroncladCardRecipe> recipes)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        Character = character;
        Recipes = recipes.ToArray();
        if (Recipes.Count == 0)
            throw new ArgumentException("A component catalog must contain at least one native recipe.",
                nameof(recipes));
        if (Recipes.Any(recipe => recipe is null))
            throw new ArgumentException("Component recipes must not contain null entries.", nameof(recipes));

        var allAtoms = Recipes.SelectMany(recipe => recipe.Atoms).ToArray();
        Atoms = allAtoms.DistinctBy(atom => atom.SchemaKey).ToArray();
        ComponentCounts = Recipes.Select(recipe => recipe.Atoms.Count).Distinct().Order().ToArray();
        AtomKeys = allAtoms.Select(atom => atom.Key).ToHashSet(StringComparer.Ordinal);
        AtomCounts = allAtoms.GroupBy(atom => atom.Key)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        ComponentCountCounts = Recipes.GroupBy(recipe => recipe.Atoms.Count)
            .ToDictionary(group => group.Key, group => group.Count());
        TagCounts = Recipes.SelectMany(recipe => recipe.Tags).GroupBy(tag => tag)
            .ToDictionary(group => group.Key, group => group.Count());
    }
}
