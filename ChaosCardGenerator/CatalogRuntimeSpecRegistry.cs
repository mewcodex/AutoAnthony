using System.Reflection;
using System.Text.Json;

namespace ChaosCardGenerator;

/// <summary>
/// Immutable, build-time projection of every reviewed catalog atom into its localization-independent contract.
/// The Markdown catalogs remain the authoring source for card names and rendered text, but gameplay generation
/// consumes this registry and never has to infer semantics from that text.
/// </summary>
internal static class CatalogRuntimeSpecRegistry
{
    private sealed record Entry(string Id, OperationRuntimeSpec Spec);

    private static readonly Lazy<IReadOnlyDictionary<string, OperationRuntimeSpec>> Specs = new(Load);

    internal static string SemanticId(GeneratedCharacter character, string recipeId, int operationIndex) =>
        $"{character.ToString().ToLowerInvariant()}/{recipeId.ToLowerInvariant()}/{operationIndex}";

    internal static OperationRuntimeSpec Get(string id) => Specs.Value.TryGetValue(id, out var spec)
        ? spec
        : throw new InvalidDataException($"Catalog RuntimeSpec registry is missing {id}.");

    internal static IReadOnlyList<IroncladCardRecipe> Attach(GeneratedCharacter character,
        IReadOnlyList<IroncladCardRecipe> recipes)
    {
        var result = new IroncladCardRecipe[recipes.Count];
        for (var recipeIndex = 0; recipeIndex < recipes.Count; recipeIndex++)
        {
            var recipe = recipes[recipeIndex];
            var atoms = new ComponentAtom[recipe.Atoms.Count];
            for (var atomIndex = 0; atomIndex < recipe.Atoms.Count; atomIndex++)
            {
                var id = SemanticId(character, recipe.Id, atomIndex);
                if (!Specs.Value.TryGetValue(id, out var spec))
                    throw new InvalidDataException($"Catalog RuntimeSpec registry is missing {id}.");
                spec.Validate();
                atoms[atomIndex] = recipe.Atoms[atomIndex] with { SemanticId = id, RuntimeSpec = spec };
            }
            result[recipeIndex] = recipe with { Atoms = atoms };
        }
        return result;
    }

    internal static void ValidateCoverage()
    {
        var expected = Enum.GetValues<GeneratedCharacter>()
            .SelectMany(character => CharacterComponentCatalogs.Get(character).Recipes.SelectMany(recipe =>
                recipe.Atoms.Select((_, index) => SemanticId(character, recipe.Id, index))))
            .ToHashSet(StringComparer.Ordinal);
        var actual = Specs.Value.Keys.ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(actual))
            throw new InvalidOperationException("Catalog RuntimeSpec registry coverage drifted. Missing: "
                + string.Join(", ", expected.Except(actual).Take(8)) + "; stale: "
                + string.Join(", ", actual.Except(expected).Take(8)));
        if (CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad).Recipes
            .SelectMany(recipe => recipe.Atoms).Any(atom => atom.RuntimeSpec is null || atom.SemanticId is null))
            throw new InvalidOperationException("Catalog atoms were not hydrated with structured semantics.");
        foreach (var atom in Enum.GetValues<GeneratedCharacter>()
                     .SelectMany(character => CharacterComponentCatalogs.Get(character).Recipes)
                     .SelectMany(recipe => recipe.Atoms))
            atom.RuntimeSpec!.Validate();
    }

#if AUTHORING_CATALOGS
    /// <summary>
    /// Build/self-test-only comparison between the reviewed Markdown authoring projection and the checked-in
    /// structured registry. AutoAnthony.dll does not embed those authoring resources and never calls this path.
    /// </summary>
    internal static void ValidateAuthoringProjection()
    {
        foreach (var character in Enum.GetValues<GeneratedCharacter>())
        {
            var structuredRecipes = CharacterComponentCatalogs.Get(character).Recipes
                .ToDictionary(recipe => recipe.Id, StringComparer.Ordinal);
            foreach (var recipe in LegacyCatalogAuthoringSource.Get(character).Recipes)
            {
                if (!structuredRecipes.TryGetValue(recipe.Id, out var structuredRecipe)
                    || recipe.Atoms.Count != structuredRecipe.Atoms.Count)
                    throw new InvalidOperationException($"Structured catalog recipe drifted for {character}/{recipe.Id}.");
                for (var index = 0; index < recipe.Atoms.Count; index++)
                {
                    var authoringAtom = recipe.Atoms[index];
                    var structuredAtom = structuredRecipe.Atoms[index];
                    var legacyCompiled = OperationRuntimeSpecCompiler.CompileRequired(new GeneratorOperation(
                        authoringAtom.Template, authoringAtom.Scope, authoringAtom.ChineseText,
                        new Dictionary<string, int>(), RequiresSingleTarget: authoringAtom.RequiresSingleTarget));
                    if (legacyCompiled.StableSignature() != structuredAtom.RuntimeSpec!.StableSignature())
                        throw new InvalidOperationException($"Catalog RuntimeSpec drifted for "
                            + $"{structuredAtom.SemanticId}.");
                }
            }
        }
    }
#endif

    private static IReadOnlyDictionary<string, OperationRuntimeSpec> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith("catalog_runtime_specs.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException("Embedded catalog RuntimeSpec registry is missing.");
        var entries = JsonSerializer.Deserialize<Entry[]>(stream)
            ?? throw new InvalidDataException("Embedded catalog RuntimeSpec registry is empty.");
        var result = new Dictionary<string, OperationRuntimeSpec>(entries.Length, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || entry.Id.Any(character => character > 0x7f))
                throw new InvalidDataException($"Catalog RuntimeSpec ID must be ASCII: {entry.Id}");
            entry.Spec.Validate();
            if (!result.TryAdd(entry.Id, entry.Spec))
                throw new InvalidDataException($"Duplicate catalog RuntimeSpec ID: {entry.Id}");
        }
        return result;
    }
}
