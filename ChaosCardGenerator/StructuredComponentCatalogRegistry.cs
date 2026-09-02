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
            foreach (var recipe in ReviewedAuthoringCatalogSource.Get(character).Recipes)
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
        var expectedCount = BuiltInCatalogManifest.Get(character).ExpectedRecipes;
        var source = Entries.Value.Where(entry => entry.Character == character).ToArray();
        if (source.Length != expectedCount)
            throw new InvalidDataException($"Structured {character} catalog expected "
                + $"{expectedCount} recipes, found {source.Length}.");
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
        // Component-count order participates in deterministic sampling. Preserve the existing Ironclad order while
        // all other reviewed catalogs retain their established sorted order.
        return new ImmutableComponentCatalog(character, recipes,
            preserveComponentCountOrder: character == GeneratedCharacter.Ironclad);
    }
}

#if AUTHORING_CATALOGS
/// <summary>Authoring-only access to the reviewed Markdown catalogs.</summary>
internal static class ReviewedAuthoringCatalogSource
{
    internal static IComponentCatalog Get(GeneratedCharacter character) => ReviewedComponentCatalog.Get(character);

    internal static IComponentCatalog GetForRuntimeSpecExport(GeneratedCharacter character) =>
        ReviewedComponentCatalog.GetUnstructuredAuthoring(character);
}
#endif
