using System.Text;

namespace ChaosCardGenerator;

/// <summary>
/// Generates complete character pools and compares every selectable family/schema with its native occurrence rate.
/// A full-pool stream is essential here: the production frequency tracker and duplicate guard span rarity slices,
/// so sampling each rarity with a fresh generator systematically overstates the first copy of native singletons.
/// </summary>
public static class ComponentOccurrenceAudit
{
    public static string Run(GeneratedCharacter character, int poolSamples, int seed = 20260902,
        bool ultimateChaos = false, bool balancedValues = true)
    {
        poolSamples = Math.Max(1, poolSamples);
        var catalog = CharacterComponentCatalogs.Get(character, ultimateChaos);
        var generated = new List<GeneratedCard>(poolSamples * GeneratedPoolSize(character));
        for (var poolIndex = 0; poolIndex < poolSamples; poolIndex++)
        {
            var generator = new RandomCardGenerator(character, seed + poolIndex * 104_729,
                ultimateChaos, balancedValues: balancedValues);
            foreach (var (rarity, count) in GeneratedRaritySlices(character))
                for (var slot = 0; slot < count; slot++)
                    generated.Add(generator.Generate(rarity));
        }

        var sourceOperations = catalog.Recipes.SelectMany(recipe => recipe.Atoms).ToArray();
        var generatedOperations = generated.SelectMany(card => card.Operations.Where(operation =>
            !operation.Template.StartsWith("N_SELECT_", StringComparison.Ordinal))).ToArray();
        var output = new StringBuilder();
        output.AppendLine($"character={character}; poolSamples={poolSamples}; sourceCards={catalog.Recipes.Count}; "
                          + $"generatedCards={generated.Count}; ultimateChaos={ultimateChaos}; "
                          + $"balancedValues={balancedValues}");

        WriteRows(output, "family", sourceOperations.Select(atom => atom.FamilyKey),
            generatedOperations.Select(operation => NumericTextSchema.Family(operation.Template)),
            catalog.Recipes.Count, generated.Count);

        var sourceSchemas = sourceOperations.GroupBy(atom => atom.SchemaKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var generatedSchemaKeys = generatedOperations.Select(OperationRuntimeSpecCompiler.StructuralFieldKey)
            .ToArray();
        output.AppendLine();
        output.AppendLine("schema\ttemplate\tsourceText\tsourceOccurrences\tsourcePer100Cards\t"
                          + "generatedPer100Cards\tratio");
        foreach (var schema in sourceSchemas.Keys.Concat(generatedSchemaKeys).Distinct(StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            var source = sourceSchemas.GetValueOrDefault(schema) ?? [];
            var sourceCount = source.Length;
            var generatedCount = generatedSchemaKeys.Count(candidate => candidate == schema);
            var sourceRate = 100d * sourceCount / Math.Max(1, catalog.Recipes.Count);
            var generatedRate = 100d * generatedCount / Math.Max(1, generated.Count);
            var ratio = sourceRate <= 0d ? double.NaN : generatedRate / sourceRate;
            var representative = source.FirstOrDefault();
            output.AppendLine(string.Join('\t',
                schema,
                representative?.Template ?? string.Empty,
                Sanitize(representative?.ChineseText ?? string.Empty),
                sourceCount,
                sourceRate.ToString("0.000"),
                generatedRate.ToString("0.000"),
                double.IsNaN(ratio) ? "n/a" : ratio.ToString("0.000")));
        }
        return output.ToString();
    }

    private static void WriteRows(StringBuilder output, string heading, IEnumerable<string> sourceKeys,
        IEnumerable<string> generatedKeys, int sourceCards, int generatedCards)
    {
        var source = sourceKeys.ToArray();
        var generated = generatedKeys.ToArray();
        output.AppendLine();
        output.AppendLine($"{heading}\tsourceOccurrences\tsourcePer100Cards\tgeneratedPer100Cards\tratio");
        foreach (var key in source.Concat(generated).Distinct(StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            var sourceCount = source.Count(candidate => candidate == key);
            var generatedCount = generated.Count(candidate => candidate == key);
            var sourceRate = 100d * sourceCount / Math.Max(1, sourceCards);
            var generatedRate = 100d * generatedCount / Math.Max(1, generatedCards);
            var ratio = sourceRate <= 0d ? double.NaN : generatedRate / sourceRate;
            output.AppendLine($"{key}\t{sourceCount}\t{sourceRate:0.000}\t{generatedRate:0.000}\t"
                              + $"{(double.IsNaN(ratio) ? "n/a" : ratio.ToString("0.000"))}");
        }
    }

    private static IReadOnlyList<(GeneratedRarity Rarity, int Count)> GeneratedRaritySlices(
        GeneratedCharacter character) => character switch
    {
        GeneratedCharacter.Colorless =>
        [
            (GeneratedRarity.Uncommon, 31),
            (GeneratedRarity.Rare, 21)
        ],
        GeneratedCharacter.Silent => StandardSlices(12),
        _ => StandardSlices(10)
    };

    private static IReadOnlyList<(GeneratedRarity Rarity, int Count)> StandardSlices(int basicCount) =>
    [
        (GeneratedRarity.Basic, basicCount),
        (GeneratedRarity.Common, 20),
        (GeneratedRarity.Uncommon, 35),
        (GeneratedRarity.Rare, 25),
        (GeneratedRarity.Ancient, 2)
    ];

    private static int GeneratedPoolSize(GeneratedCharacter character) =>
        GeneratedRaritySlices(character).Sum(item => item.Count);

    private static string Sanitize(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
