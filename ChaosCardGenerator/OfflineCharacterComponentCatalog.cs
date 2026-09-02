using System.Reflection;
using System.Text.RegularExpressions;

namespace ChaosCardGenerator;

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
/// Offline component catalogs embedded for the final three characters. Runtime reads only reviewed project data and
/// never derives component boundaries from localized ModelDb card text.
/// </summary>
public sealed class OfflineCharacterComponentCatalog : IComponentCatalog
{
    private static readonly IReadOnlyDictionary<GeneratedCharacter, Lazy<OfflineCharacterComponentCatalog>> Instances =
        new Dictionary<GeneratedCharacter, Lazy<OfflineCharacterComponentCatalog>>
        {
            [GeneratedCharacter.Defect] = new(() => Load(GeneratedCharacter.Defect, "defect_unit_operations.md")),
            [GeneratedCharacter.Necrobinder] = new(() => Load(GeneratedCharacter.Necrobinder, "necrobinder_unit_operations.md")),
            [GeneratedCharacter.Regent] = new(() => Load(GeneratedCharacter.Regent, "regent_unit_operations.md")),
            [GeneratedCharacter.Colorless] = new(() => Load(GeneratedCharacter.Colorless, "colorless_unit_operations.md"))
        };

    public static OfflineCharacterComponentCatalog Get(GeneratedCharacter character) =>
        Instances.TryGetValue(character, out var catalog)
            ? catalog.Value
            : throw new ArgumentOutOfRangeException(nameof(character), character, "该角色没有离线组件目录。");

    /// <summary>
    /// RuntimeSpec regeneration must be able to read a newly edited operation list before the checked-in registry
    /// contains the new semantic IDs. This authoring-only path deliberately skips registry hydration; production
    /// and ordinary generator reads continue to require complete structured specs.
    /// </summary>
    internal static OfflineCharacterComponentCatalog GetUnstructuredAuthoring(GeneratedCharacter character) =>
        character switch
        {
            GeneratedCharacter.Defect => Load(character, "defect_unit_operations.md", attachSpecs: false),
            GeneratedCharacter.Necrobinder => Load(character, "necrobinder_unit_operations.md", attachSpecs: false),
            GeneratedCharacter.Regent => Load(character, "regent_unit_operations.md", attachSpecs: false),
            GeneratedCharacter.Colorless => Load(character, "colorless_unit_operations.md", attachSpecs: false),
            _ => throw new ArgumentOutOfRangeException(nameof(character), character,
                "该角色没有离线组件目录。")
        };

    public GeneratedCharacter Character { get; }
    public IReadOnlyList<IroncladCardRecipe> Recipes { get; }
    public IReadOnlyList<ComponentAtom> Atoms { get; }
    public IReadOnlyList<int> ComponentCounts { get; }
    public IReadOnlySet<string> AtomKeys { get; }
    public IReadOnlyDictionary<string, int> AtomCounts { get; }
    public IReadOnlyDictionary<int, int> ComponentCountCounts { get; }
    public IReadOnlyDictionary<CardTag, int> TagCounts { get; }

    private OfflineCharacterComponentCatalog(GeneratedCharacter character, IReadOnlyList<IroncladCardRecipe> recipes)
    {
        Character = character;
        Recipes = recipes;
        var allAtoms = recipes.SelectMany(recipe => recipe.Atoms).ToArray();
        Atoms = allAtoms.DistinctBy(atom => atom.SchemaKey).ToArray();
        ComponentCounts = recipes.Select(recipe => recipe.Atoms.Count).Distinct().Order().ToArray();
        AtomKeys = allAtoms.Select(atom => atom.Key).ToHashSet(StringComparer.Ordinal);
        AtomCounts = allAtoms.GroupBy(atom => atom.Key).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        ComponentCountCounts = recipes.GroupBy(recipe => recipe.Atoms.Count).ToDictionary(group => group.Key, group => group.Count());
        TagCounts = recipes.SelectMany(recipe => recipe.Tags).GroupBy(tag => tag).ToDictionary(group => group.Key, group => group.Count());
    }

    private static OfflineCharacterComponentCatalog Load(GeneratedCharacter character, string fileName,
        bool attachSpecs = true)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"缺少嵌入的 {character} 组件目录。");
        using var reader = new StreamReader(stream);
        var recipes = reader.ReadToEnd().Split('\n')
            .Select(ParseRow)
            .Where(recipe => recipe is not null)
            .Cast<IroncladCardRecipe>()
            .ToArray();
        var expected = character == GeneratedCharacter.Colorless ? 52 : 86;
        if (recipes.Length != expected)
            throw new InvalidOperationException($"{character} 组件目录应为{expected}张卡，实际读取到{recipes.Length}张。");
        var structuredRecipes = attachSpecs ? CatalogRuntimeSpecRegistry.Attach(character, recipes) : recipes;
        NativeKeywordUpgradeCatalog.Register(character, structuredRecipes);
        return new OfflineCharacterComponentCatalog(character, structuredRecipes);
    }

    private static IroncladCardRecipe? ParseRow(string line)
    {
        if (!line.StartsWith("| ", StringComparison.Ordinal)) return null;
        var cells = line.Split('|');
        if (cells.Length < 7) return null;
        var idMatch = Regex.Match(cells[1], "`(?<id>[A-Za-z0-9]+)`");
        if (!idMatch.Success) return null;
        var meta = cells[2].Trim().Split('/', StringSplitOptions.TrimEntries);
        if (meta.Length != 5) throw new InvalidOperationException($"无效的离线卡牌元数据：{cells[2]}");
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
                throw new InvalidOperationException($"离线 operation 应有7个字段：{match.Value}");
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
        if (result.Count == 0) throw new InvalidOperationException($"卡牌行没有 operation：{cell}");
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
    private static readonly IReadOnlyDictionary<GeneratedCharacter, Lazy<IComponentCatalog>> UnlockedCatalogs =
        Enum.GetValues<GeneratedCharacter>().ToDictionary(
            character => character,
            character => new Lazy<IComponentCatalog>(() => BuildUnlockedCatalog(character)));

    public static IComponentCatalog Get(GeneratedCharacter character) => character switch
    {
        GeneratedCharacter.Ironclad or GeneratedCharacter.Silent or GeneratedCharacter.Defect
            or GeneratedCharacter.Necrobinder or GeneratedCharacter.Regent or GeneratedCharacter.Colorless =>
            StructuredComponentCatalogRegistry.Get(character),
        _ => throw new ArgumentOutOfRangeException(nameof(character))
    };

    public static IComponentCatalog Get(GeneratedCharacter character, bool unlockComponentRoles) =>
        unlockComponentRoles ? UnlockedCatalogs[character].Value : Get(character);

    private static IComponentCatalog BuildUnlockedCatalog(GeneratedCharacter character)
    {
        var recipes = Enum.GetValues<GeneratedCharacter>()
            .Select(Get)
            .SelectMany(catalog => catalog.Recipes)
            .ToArray();
        return new CombinedComponentCatalog(character, recipes);
    }

    private sealed class CombinedComponentCatalog : IComponentCatalog
    {
        public GeneratedCharacter Character { get; }
        public IReadOnlyList<IroncladCardRecipe> Recipes { get; }
        public IReadOnlyList<ComponentAtom> Atoms { get; }
        public IReadOnlyList<int> ComponentCounts { get; }
        public IReadOnlySet<string> AtomKeys { get; }
        public IReadOnlyDictionary<string, int> AtomCounts { get; }
        public IReadOnlyDictionary<int, int> ComponentCountCounts { get; }
        public IReadOnlyDictionary<CardTag, int> TagCounts { get; }

        public CombinedComponentCatalog(GeneratedCharacter character, IReadOnlyList<IroncladCardRecipe> recipes)
        {
            Character = character;
            Recipes = recipes;
            var allAtoms = recipes.SelectMany(recipe => recipe.Atoms).ToArray();
            Atoms = allAtoms.DistinctBy(atom => atom.SchemaKey).ToArray();
            ComponentCounts = recipes.Select(recipe => recipe.Atoms.Count).Distinct().ToArray();
            AtomKeys = allAtoms.Select(atom => atom.Key).ToHashSet(StringComparer.Ordinal);
            AtomCounts = allAtoms.GroupBy(atom => atom.Key)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            ComponentCountCounts = recipes.GroupBy(recipe => recipe.Atoms.Count)
                .ToDictionary(group => group.Key, group => group.Count());
            TagCounts = recipes.SelectMany(recipe => recipe.Tags)
                .GroupBy(tag => tag).ToDictionary(group => group.Key, group => group.Count());
        }
    }
}
