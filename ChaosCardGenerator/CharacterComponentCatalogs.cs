using System.Reflection;
using System.Text.RegularExpressions;

namespace ChaosCardGenerator;

internal sealed record BuiltInCatalogDefinition(string AuthoringFileName, int ExpectedRecipes);

/// <summary>Single manifest for every built-in character and colorless component source.</summary>
internal static class BuiltInCatalogManifest
{
    internal static readonly IReadOnlyDictionary<GeneratedCharacter, BuiltInCatalogDefinition> Definitions =
        new Dictionary<GeneratedCharacter, BuiltInCatalogDefinition>
        {
            [GeneratedCharacter.Ironclad] = new("ironclad_unit_operations.md", 85),
            [GeneratedCharacter.Silent] = new("silent_unit_operations.md", 86),
            [GeneratedCharacter.Defect] = new("defect_unit_operations.md", 86),
            [GeneratedCharacter.Necrobinder] = new("necrobinder_unit_operations.md", 86),
            [GeneratedCharacter.Regent] = new("regent_unit_operations.md", 86),
            [GeneratedCharacter.Colorless] = new("colorless_unit_operations.md", 53),
            [GeneratedCharacter.Fool] = new("fool_unit_operations.md", 84)
        };

    internal static BuiltInCatalogDefinition Get(GeneratedCharacter character) =>
        Definitions.TryGetValue(character, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(character), character,
                "No built-in component catalog exists.");
}

/// <summary>
/// Reviewed native keyword changes are production data. Keep them separate from the Markdown authoring parser so
/// AutoAnthony.dll does not need to ship the parser merely to register upgrade candidates.
/// </summary>
internal static class NativeKeywordUpgradeCatalog
{
    internal static void Register(GeneratedCharacter character, IEnumerable<IroncladCardRecipe> recipes)
    {
        foreach (var recipe in recipes)
        {
            var (added, removed) = (character, recipe.Id) switch
            {
                (GeneratedCharacter.Fool, "Lullaby") => ([CardTag.Innate], Array.Empty<CardTag>()),
                (GeneratedCharacter.Defect, "MachineLearning") => ([CardTag.Innate], Array.Empty<CardTag>()),
                (GeneratedCharacter.Defect, "Chill" or "Fusion" or "Hologram" or "Hotfix" or "Ignition" or "Rainbow" or "Voltaic")
                    => (Array.Empty<CardTag>(), [CardTag.Exhaust]),
                (GeneratedCharacter.Defect, "EchoForm") => (Array.Empty<CardTag>(), [CardTag.Ethereal]),

                (GeneratedCharacter.Necrobinder, "CallOfTheVoid" or "Soulbound") => ([CardTag.Innate], Array.Empty<CardTag>()),
                (GeneratedCharacter.Necrobinder, "Dredge" or "Misery" or "ReaperForm" or "TimesUp" or "Wisp")
                    => ([CardTag.Retain], Array.Empty<CardTag>()),
                (GeneratedCharacter.Necrobinder, "Graveblast" or "Transfigure" or "Underworld")
                    => (Array.Empty<CardTag>(), [CardTag.Exhaust]),

                (GeneratedCharacter.Regent, "Arsenal" or "BigBang" or "Tyranny") => ([CardTag.Innate], Array.Empty<CardTag>()),
                (GeneratedCharacter.Regent, "Monologue" or "RoyalGamble") => ([CardTag.Retain], Array.Empty<CardTag>()),
                (GeneratedCharacter.Regent, "KnowThyPlace") => (Array.Empty<CardTag>(), [CardTag.Exhaust]),
                (GeneratedCharacter.Regent, "VoidForm") => (Array.Empty<CardTag>(), [CardTag.Ethereal]),

                (GeneratedCharacter.Colorless, "Anointed" or "GoldAxe" or "Scrawl")
                    => ([CardTag.Retain], Array.Empty<CardTag>()),
                (GeneratedCharacter.Colorless, "Entropy") => ([CardTag.Innate], Array.Empty<CardTag>()),
                (GeneratedCharacter.Colorless, "Discovery" or "Prolong" or "SecretTechnique" or "SecretWeapon" or "ThinkingAhead")
                    => (Array.Empty<CardTag>(), [CardTag.Exhaust]),
                _ => (Array.Empty<CardTag>(), Array.Empty<CardTag>())
            };
            if (added.Length == 0 && removed.Length == 0) continue;
            foreach (var template in recipe.Atoms.Select(atom => atom.Template).Distinct(StringComparer.Ordinal))
                ExternalOperationUpgradeRegistry.Register(character, template, added, removed);
        }
    }
}

#if AUTHORING_CATALOGS
/// <summary>
/// Loads every reviewed built-in catalog from the same offline, fully structured authoring format. Runtime reads the
/// materialized JSON catalog instead and never derives component boundaries from localized ModelDb card text.
/// </summary>
public static class ReviewedComponentCatalog
{
    private static readonly IReadOnlyDictionary<GeneratedCharacter, Lazy<IComponentCatalog>> Instances =
        BuiltInCatalogManifest.Definitions.ToDictionary(source => source.Key,
            source => new Lazy<IComponentCatalog>(() => Load(source.Key, attachSpecs: true)));

    public static IComponentCatalog Get(GeneratedCharacter character) =>
        Instances.TryGetValue(character, out var catalog)
            ? catalog.Value
            : throw new ArgumentOutOfRangeException(nameof(character), character, "No reviewed component catalog exists.");

    /// <summary>
    /// RuntimeSpec regeneration must be able to read a newly edited operation list before the checked-in registry
    /// contains the new semantic IDs. This authoring-only path deliberately skips registry hydration; production
    /// and ordinary generator reads continue to require complete structured specs.
    /// </summary>
    internal static IComponentCatalog GetUnstructuredAuthoring(GeneratedCharacter character) =>
        Load(character, attachSpecs: false);

    private static IComponentCatalog Load(GeneratedCharacter character, bool attachSpecs)
    {
        var source = BuiltInCatalogManifest.Get(character);
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith(source.AuthoringFileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The embedded {character} component catalog is missing.");
        using var reader = new StreamReader(stream);
        var recipes = reader.ReadToEnd().Split('\n')
            .Select(ParseRow)
            .Where(recipe => recipe is not null)
            .Cast<IroncladCardRecipe>()
            .ToArray();
        if (recipes.Length != source.ExpectedRecipes)
            throw new InvalidOperationException($"The {character} catalog must contain {source.ExpectedRecipes} "
                + $"recipes; found {recipes.Length}.");
        var structuredRecipes = attachSpecs ? CatalogRuntimeSpecRegistry.Attach(character, recipes) : recipes;
        NativeKeywordUpgradeCatalog.Register(character, structuredRecipes);
        return new ImmutableComponentCatalog(character, structuredRecipes);
    }

    private static IroncladCardRecipe? ParseRow(string line)
    {
        if (!line.StartsWith("| ", StringComparison.Ordinal)) return null;
        var cells = line.Split('|');
        if (cells.Length < 7) return null;
        var idMatch = Regex.Match(cells[1], "`(?<id>[A-Za-z0-9]+)`");
        if (!idMatch.Success) return null;
        var meta = cells[2].Trim().Split('/', StringSplitOptions.TrimEntries);
        if (meta.Length != 5) throw new InvalidOperationException($"Invalid offline card metadata: {cells[2]}");
        var cost = meta[0] == "X" ? -1 : int.Parse(meta[0]);
        var starCostX = meta[1] == "X";
        var starCost = meta[1] == "-" || starCostX ? -1 : int.Parse(meta[1]);
        var parsedAtoms = ParseAtoms(cells[3]);
        return new IroncladCardRecipe(
            idMatch.Groups["id"].Value,
            cells[1][..idMatch.Index].Trim(),
            cost,
            Enum.Parse<GeneratedCardType>(meta[2]),
            Enum.Parse<TargetMode>(meta[3]),
            Enum.Parse<GeneratedRarity>(meta[4]),
            ParseTags(cells[4]),
            parsedAtoms.Select(item => item.Atom).ToArray(),
            parsedAtoms.Select(item => item.TriggerOwner).ToArray(),
            starCost,
            starCostX,
            cells[5].Trim());
    }

    private static IReadOnlyList<(ComponentAtom Atom, int TriggerOwner)> ParseAtoms(string cell)
    {
        var result = new List<(ComponentAtom, int)>();
        foreach (Match match in Regex.Matches(cell, "⟦(?<fields>.*?)⟧"))
        {
            var fields = match.Groups["fields"].Value.Split('¦');
            if (fields.Length != 7)
                throw new InvalidOperationException($"An offline operation must contain seven fields: {match.Value}");
            var template = Unescape(fields[0]);
            var chinese = Unescape(fields[5]);
            var english = Unescape(fields[6]);
            var atom = new ComponentAtom(
                template,
                Enum.Parse<OperationScope>(fields[1]),
                chinese,
                bool.Parse(fields[2]),
                Enum.Parse<CardReferenceRequirement>(fields[3]));
            ExternalOperationTextRegistry.Register(template, chinese, english);
            result.Add((atom, int.Parse(fields[4])));
        }
        if (result.Count == 0) throw new InvalidOperationException($"The card row contains no operations: {cell}");
        return result;
    }

    private static IReadOnlyList<CardTag> ParseTags(string cell) => cell.Trim().Length == 0
        ? Array.Empty<CardTag>()
        : cell.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Enum.Parse<CardTag>(value))
            .ToArray();

    private static string Unescape(string text) => text.Replace("<br>", "\n", StringComparison.Ordinal)
        .Replace('｜', '|');
}
#endif

public static class CharacterComponentCatalogs
{
    // v113 StartingDeck multiplicities, authored from CharacterModel.StartingDeck rather than unique pool rows.
    // Non-Basic recipes are distinct reward choices; external/custom Basic recipes keep a neutral one-copy prior.
    private static readonly IReadOnlyDictionary<string, int> StartingCopies = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["StrikeIronclad"] = 5, ["DefendIronclad"] = 4,
        ["StrikeSilent"] = 5, ["DefendSilent"] = 5,
        ["StrikeDefect"] = 4, ["DefendDefect"] = 4,
        ["StrikeNecrobinder"] = 4, ["DefendNecrobinder"] = 4,
        ["StrikeRegent"] = 4, ["DefendRegent"] = 4,
        ["StrikeFool"] = 5, ["DefendFool"] = 4
    };

    /// <summary>Native occurrence weight: actual starter copies for Basic recipes, one for other card choices.</summary>
    public static int NativeOccurrenceWeight(IroncladCardRecipe recipe) =>
        recipe.OriginalRarity == GeneratedRarity.Basic ? StartingCopies.GetValueOrDefault(recipe.Id, 1) : 1;

    /// <summary>Ancient is a stronger budget, not a separate two-card component vocabulary.</summary>
    public static GeneratedRarity ComponentRarity(GeneratedRarity rarity) =>
        rarity == GeneratedRarity.Ancient ? GeneratedRarity.Rare : rarity;

    private static readonly IReadOnlyDictionary<GeneratedCharacter, Lazy<IComponentCatalog>> UnlockedCatalogs =
        Enum.GetValues<GeneratedCharacter>().ToDictionary(
            character => character,
            character => new Lazy<IComponentCatalog>(() => BuildUnlockedCatalog(character)));

    public static IComponentCatalog Get(GeneratedCharacter character)
    {
        _ = BuiltInCatalogManifest.Get(character);
        return StructuredComponentCatalogRegistry.Get(character);
    }

    public static IComponentCatalog Get(GeneratedCharacter character, bool unlockComponentRoles) =>
        unlockComponentRoles ? UnlockedCatalogs[character].Value : Get(character);

    private static IComponentCatalog BuildUnlockedCatalog(GeneratedCharacter character)
    {
        var recipes = Enum.GetValues<GeneratedCharacter>()
            .Select(Get)
            .SelectMany(catalog => catalog.Recipes)
            .ToArray();
        return new ImmutableComponentCatalog(character, recipes, preserveComponentCountOrder: true);
    }
}
