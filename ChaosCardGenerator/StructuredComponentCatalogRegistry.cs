using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChaosCardGenerator;

/// <summary>
/// Build-time materialization of all reviewed card recipes. Production generation reads this registry directly;
/// the Markdown/legacy DSL parsers remain authoring and build-audit tools only.
/// Localized strings in this file are rendering payloads and are never inspected to infer gameplay semantics.
/// </summary>
internal static class StructuredComponentCatalogRegistry
{
    internal sealed record AtomEntry(
        string SemanticId,
        string Template,
        OperationScope Scope,
        string ChineseText,
        string EnglishText,
        bool RequiresSingleTarget,
        CardReferenceRequirement CardReference,
        int TriggerOwner);

    internal sealed record RecipeEntry(
        GeneratedCharacter Character,
        string Id,
        string ChineseTitle,
        string EnglishTitle,
        int Cost,
        int StarCost,
        bool HasStarCostX,
        GeneratedCardType Type,
        TargetMode Target,
        GeneratedRarity OriginalRarity,
        IReadOnlyList<CardTag> Tags,
        IReadOnlyList<AtomEntry> Atoms);

    private static readonly Lazy<RecipeEntry[]> Entries = new(ReadEntries);
    private static readonly IReadOnlyDictionary<GeneratedCharacter, Lazy<IComponentCatalog>> Catalogs =
        Enum.GetValues<GeneratedCharacter>().ToDictionary(character => character,
            character => new Lazy<IComponentCatalog>(() => LoadCharacter(character)));

    internal static IComponentCatalog Get(GeneratedCharacter character) =>
        Catalogs.TryGetValue(character, out var catalog)
            ? catalog.Value
            : throw new InvalidDataException($"Structured component catalog is missing {character}.");

#if AUTHORING_CATALOGS
    internal static IReadOnlyList<RecipeEntry> ExportAuthoringSource()
    {
        var entries = new List<RecipeEntry>();
        foreach (var character in Enum.GetValues<GeneratedCharacter>())
        {
            foreach (var recipe in LegacyCatalogAuthoringSource.Get(character).Recipes)
            {
                if (recipe.Atoms.Count != recipe.TriggerOwners.Count)
                    throw new InvalidDataException($"{character}/{recipe.Id} atom/trigger-owner counts differ.");
                var atoms = recipe.Atoms.Select((atom, index) =>
                {
                    var semanticId = CatalogRuntimeSpecRegistry.SemanticId(character, recipe.Id, index);
                    var runtimeSpec = atom.RuntimeSpec ?? CatalogRuntimeSpecRegistry.Get(semanticId);
                    var operation = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
                        new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget,
                        RuntimeSpec: runtimeSpec);
                    return new AtomEntry(semanticId, atom.Template, atom.Scope, atom.ChineseText,
                        EnglishCardDescriptionRenderer.OperationText(operation), atom.RequiresSingleTarget,
                        atom.CardReference, recipe.TriggerOwners[index]);
                }).ToArray();
                entries.Add(new RecipeEntry(character, recipe.Id, recipe.ChineseTitle, recipe.EnglishTitle,
                    recipe.Cost, recipe.StarCost, recipe.HasStarCostX, recipe.Type, recipe.Target,
                    recipe.OriginalRarity, recipe.Tags, atoms));
            }
        }
        return entries;
    }
#endif

    internal static JsonSerializerOptions JsonOptions(bool indented = false)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNamingPolicy = null
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static RecipeEntry[] ReadEntries()
    {
        var assembly = Assembly.GetExecutingAssembly();
        if (string.Equals(assembly.GetName().Name, "AutoAnthony", StringComparison.Ordinal)
            && assembly.GetManifestResourceNames().Any(name =>
                name.EndsWith("_unit_operations.md", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Production assembly must not embed legacy localized operation catalogs.");
        var resource = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith("catalog_recipes.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException("Embedded structured component catalog is missing.");
        var entries = JsonSerializer.Deserialize<RecipeEntry[]>(stream, JsonOptions())
            ?? throw new InvalidDataException("Embedded structured component catalog is empty.");

        var semanticIds = entries.SelectMany(entry => entry.Atoms).Select(atom => atom.SemanticId).ToArray();
        if (entries.Length != 481 || semanticIds.Length != 930
            || semanticIds.Any(string.IsNullOrWhiteSpace)
            || semanticIds.Any(id => id.Any(value => value > 0x7f))
            || semanticIds.Distinct(StringComparer.Ordinal).Count() != semanticIds.Length)
            throw new InvalidDataException($"Structured catalog totals or semantic IDs drifted: "
                + $"recipes={entries.Length}, operations={semanticIds.Length}.");
        return entries;
    }

    private static IComponentCatalog LoadCharacter(GeneratedCharacter character)
    {
        var expectedCounts = new Dictionary<GeneratedCharacter, int>
        {
            [GeneratedCharacter.Ironclad] = 85,
            [GeneratedCharacter.Silent] = 86,
            [GeneratedCharacter.Defect] = 86,
            [GeneratedCharacter.Necrobinder] = 86,
            [GeneratedCharacter.Regent] = 86,
            [GeneratedCharacter.Colorless] = 52
        };
        var source = Entries.Value.Where(entry => entry.Character == character).ToArray();
        if (source.Length != expectedCounts[character])
            throw new InvalidDataException($"Structured {character} catalog expected "
                + $"{expectedCounts[character]} recipes, found {source.Length}.");
        var recipes = source.Select(entry =>
        {
            var atoms = entry.Atoms.Select(atom =>
            {
                var spec = CatalogRuntimeSpecRegistry.Get(atom.SemanticId);
                ExternalOperationTextRegistry.Register(atom.Template, atom.ChineseText, atom.EnglishText);
                return new ComponentAtom(atom.Template, atom.Scope, atom.ChineseText,
                    atom.RequiresSingleTarget, atom.CardReference)
                {
                    SemanticId = atom.SemanticId,
                    RuntimeSpec = spec
                };
            }).ToArray();
            return new IroncladCardRecipe(entry.Id, entry.ChineseTitle, entry.Cost, entry.Type,
                entry.Target, entry.OriginalRarity, entry.Tags.ToArray(), atoms,
                entry.Atoms.Select(atom => atom.TriggerOwner).ToArray(), entry.StarCost,
                entry.HasStarCostX, entry.EnglishTitle);
        }).ToArray();
        NativeKeywordUpgradeCatalog.Register(character, recipes);
        return new MaterializedCatalog(character, recipes);
    }

    private sealed class MaterializedCatalog : IComponentCatalog
    {
        public GeneratedCharacter Character { get; }
        public IReadOnlyList<IroncladCardRecipe> Recipes { get; }
        public IReadOnlyList<ComponentAtom> Atoms { get; }
        public IReadOnlyList<int> ComponentCounts { get; }
        public IReadOnlySet<string> AtomKeys { get; }
        public IReadOnlyDictionary<string, int> AtomCounts { get; }
        public IReadOnlyDictionary<int, int> ComponentCountCounts { get; }
        public IReadOnlyDictionary<CardTag, int> TagCounts { get; }

        internal MaterializedCatalog(GeneratedCharacter character, IReadOnlyList<IroncladCardRecipe> recipes)
        {
            Character = character;
            Recipes = recipes;
            var allAtoms = recipes.SelectMany(recipe => recipe.Atoms).ToArray();
            Atoms = allAtoms.DistinctBy(atom => atom.SchemaKey).ToArray();
            // Preserve the reviewed catalogs' historical iteration order because this list is sampled by index.
            // Ironclad/Silent kept first-seen order; the four offline catalogs explicitly sorted it.
            var counts = recipes.Select(recipe => recipe.Atoms.Count).Distinct();
            ComponentCounts = character is GeneratedCharacter.Ironclad
                ? counts.ToArray()
                : counts.Order().ToArray();
            AtomKeys = allAtoms.Select(atom => atom.Key).ToHashSet(StringComparer.Ordinal);
            AtomCounts = allAtoms.GroupBy(atom => atom.Key)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            ComponentCountCounts = recipes.GroupBy(recipe => recipe.Atoms.Count)
                .ToDictionary(group => group.Key, group => group.Count());
            TagCounts = recipes.SelectMany(recipe => recipe.Tags).GroupBy(tag => tag)
                .ToDictionary(group => group.Key, group => group.Count());
        }
    }
}

#if AUTHORING_CATALOGS
/// <summary>Authoring-only access to the reviewed Markdown catalogs.</summary>
internal static class LegacyCatalogAuthoringSource
{
    internal static IComponentCatalog Get(GeneratedCharacter character) => character switch
    {
        GeneratedCharacter.Ironclad => IroncladComponentCatalog.Instance,
        GeneratedCharacter.Silent => SilentComponentCatalog.Instance,
        GeneratedCharacter.Defect or GeneratedCharacter.Necrobinder or GeneratedCharacter.Regent
            or GeneratedCharacter.Colorless => OfflineCharacterComponentCatalog.Get(character),
        _ => throw new ArgumentOutOfRangeException(nameof(character))
    };

    internal static IComponentCatalog GetForRuntimeSpecExport(GeneratedCharacter character) => character switch
    {
        GeneratedCharacter.Defect or GeneratedCharacter.Necrobinder or GeneratedCharacter.Regent
            or GeneratedCharacter.Colorless => OfflineCharacterComponentCatalog.GetUnstructuredAuthoring(character),
        _ => Get(character)
    };
}
#endif
