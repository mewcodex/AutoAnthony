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
    // These spellings remain accepted by the runtime compiler for old run/history snapshots, but new reviewed
    // recipes must use the shared authoring component. Keeping this boundary explicit prevents a sixth pool from
    // accidentally cloning an existing operation under a character-prefixed name.
    private static readonly HashSet<string> LegacyAuthoringAliases = new(StringComparer.Ordinal)
    {
        "D:GainEnergy", "NCR:GainEnergy", "R:GainEnergy",
        "D:NextTurnEnergy", "NCR:NextTurnEnergy", "N:NextTurnEnergy", "N:NextTurnBlock",
        "D:IfFatal", "R:IfFatal", "CL:IfFatal",
        "CL:AtNextTurnStart", "NCR:NextTurn", "R:NextTurn", "D:NextTurnsStart",
        "D:ExhaustSelectedHandCard", "NCR:TargetHpLoss",
        "I:GainTemporaryStrength", "R:GainStrengthThisTurn",
        "R:GainVigor", "CL:GainVigor",
        "R:ApplyWeakAll", "NCR:ApplyWeakAll", "CL:ApplyWeakAll",
        "R:ApplyVulnerableAll", "NCR:ApplyVulnerableAll", "CL:ApplyVulnerableAll",
        "R:EnemiesLoseStrengthThisTurn", "R:TargetLoseStrengthThisTurn",
        "NCR:TargetLoseStrengthThisTurn", "CL:TargetLoseStrengthThisTurn",
        "D:RepeatDamage", "R:RepeatDamage",
        "CL:AddRandomColorlessToHand", "R:AddRandomColorlessToHand",
        "D:MoveDiscardCardToHand", "NCR:MoveDiscardCardToHand",
        "D:GainStrength", "D:GainDexterity", "R:GainStrength",
        "CL:RetainHandThisTurn", "R:RetainHandThisTurn", "NCR:CreateCopyInDiscard",
        "A:when", "A:whenAttackDealsDamage", "C:if", "C:after", "C:untilTurnEnd", "C:forEach",
        "NCR:WheneverCardPlayedThisTurn", "NCR:ApplyPower_OblivionPower", "T:Strangle"
    };

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
        if (entries.Length != 481 || semanticIds.Length != 938
            || semanticIds.Any(string.IsNullOrWhiteSpace)
            || semanticIds.Any(id => id.Any(value => value > 0x7f))
            || semanticIds.Distinct(StringComparer.Ordinal).Count() != semanticIds.Length)
            throw new InvalidDataException($"Structured catalog totals or semantic IDs drifted: "
                + $"recipes={entries.Length}, operations={semanticIds.Length}.");
        ValidateCanonicalAuthoring(entries);
        return entries;
    }

    private static void ValidateCanonicalAuthoring(IReadOnlyList<RecipeEntry> entries)
    {
        foreach (var entry in entries)
        {
            for (var triggerIndex = 0; triggerIndex < entry.Atoms.Count; triggerIndex++)
            {
                var atom = entry.Atoms[triggerIndex];
                if (LegacyAuthoringAliases.Contains(atom.Template))
                    throw new InvalidDataException($"Reviewed catalog {atom.SemanticId} uses legacy authoring "
                        + $"alias {atom.Template}; use its shared component instead.");

                var spec = CatalogRuntimeSpecRegistry.Get(atom.SemanticId);
                if (spec.Trigger?.Kind is not ("next_turn_start" or "next_turns_start" or "card_played"))
                    continue;
                if (atom.Template is not ("C:NextTurnStart" or "C:NextTurnsStart"
                        or "C:untilTurnEndCardPlayed"))
                    continue;

                var linkedCount = entry.Atoms.Count(candidate => candidate.TriggerOwner == triggerIndex);
                if (linkedCount == 0)
                    throw new InvalidDataException($"Shared trigger {atom.SemanticId} has no linked effect.");
            }
        }
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
                OperationLocalizedText? localizedText;
                try
                {
                    if (!OperationLocalizedText.TryCompile(atom.ChineseText, atom.EnglishText, spec,
                            out localizedText) || localizedText is null)
                        throw new InvalidDataException("A localized value could not be mapped to a named slot.");
                }
                catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
                {
                    throw new InvalidDataException($"Structured localization template could not compile for "
                        + $"{atom.SemanticId} ({atom.Template}): zh={atom.ChineseText}; en={atom.EnglishText}.",
                        exception);
                }
                ComponentLocalizationApi.RegisterBuiltIn(atom.SemanticId, localizedText, spec);
                return new ComponentAtom(atom.Template, atom.Scope, atom.ChineseText,
                    atom.RequiresSingleTarget, atom.CardReference)
                {
                    SemanticId = atom.SemanticId,
                    RuntimeSpec = spec,
                    LocalizedText = localizedText
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
