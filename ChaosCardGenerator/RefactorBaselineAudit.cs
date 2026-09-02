using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChaosCardGenerator;

/// <summary>
/// Produces a deterministic, display-independent projection of the current generator and its semantic classifiers.
/// The projection intentionally excludes future runtime metadata so that adding structured operation specs does not
/// itself invalidate the legacy-behaviour baseline.
/// </summary>
internal static class RefactorBaselineAudit
{
    internal const int SchemaVersion = 1;
    private const int CorpusSeed = 20260829;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static RefactorBaselineResult Write(string path, string modVersion)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
            throw new IOException($"Refusing to overwrite frozen refactor baseline: {fullPath}");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"Baseline path has no parent directory: {fullPath}"));
        var bytes = Serialize(Build(modVersion));
        WriteBytes(fullPath, bytes);
        return Result(fullPath, bytes);
    }

    internal static RefactorBaselineComparison Compare(string path, string modVersion)
    {
        var fullPath = Path.GetFullPath(path);
        var expected = ReadBytes(fullPath);
        var actual = Serialize(Build(modVersion));
        if (expected.AsSpan().SequenceEqual(actual))
            return new(true, Result(fullPath, actual), "exact match");

        var expectedLines = Encoding.UTF8.GetString(expected).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var actualLines = Encoding.UTF8.GetString(actual).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var common = Math.Min(expectedLines.Length, actualLines.Length);
        var line = 0;
        while (line < common && expectedLines[line] == actualLines[line]) line++;
        var expectedLine = line < expectedLines.Length ? expectedLines[line] : "<end of baseline>";
        var actualLine = line < actualLines.Length ? actualLines[line] : "<end of current output>";
        return new(false, Result(fullPath, actual),
            $"first difference at line {line + 1}\nbaseline: {expectedLine}\ncurrent:  {actualLine}");
    }

    internal static TextDependencyAuditResult WriteTextDependencyInventory(string sourceRoot, string outputPath)
    {
        var root = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var output = Path.GetFullPath(outputPath);
        if (File.Exists(output))
            throw new IOException($"Refusing to overwrite frozen text-dependency inventory: {output}");
        Directory.CreateDirectory(Path.GetDirectoryName(output)
            ?? throw new InvalidOperationException($"Inventory path has no parent directory: {output}"));

        var entries = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !HasBuildDirectory(path, root))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => new
            {
                Path = Path.GetRelativePath(root, path).Replace('\\', '/'),
                Line = index + 1,
                Code = line.Trim()
            }))
            .Where(entry => LooksLikeSemanticTextDependency(entry.Code))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ThenBy(entry => entry.Line)
            .ToArray();
        var builder = new StringBuilder("path\tline\tcode\n");
        foreach (var entry in entries)
            builder.Append(entry.Path).Append('\t').Append(entry.Line).Append('\t')
                .Append(entry.Code.Replace('\t', ' ')).Append('\n');
        File.WriteAllText(output, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new(output, entries.Length);
    }

    private static RefactorBaselineDocument Build(string modVersion)
    {
        OperationRuntimeSpecCompiler.EnableLegacyEquivalenceAssertions = true;
        try
        {
            var catalogs = Enum.GetValues<GeneratedCharacter>()
                .Select(character => BuildCatalog(character, ultimateChaos: false))
                .Append(BuildCatalog(GeneratedCharacter.Ironclad, ultimateChaos: true))
                .ToArray();
            var corpora = new List<BaselineCorpus>();
            foreach (var character in Enum.GetValues<GeneratedCharacter>())
            {
                corpora.Add(BuildFullPool(character, ultimateChaos: false, balancedValues: true,
                    CorpusSeed + (int)character * 1009));
                corpora.Add(BuildSpotCorpus(character, ultimateChaos: false, balancedValues: false,
                    CorpusSeed + 100_000 + (int)character * 1009));
                corpora.Add(BuildSpotCorpus(character, ultimateChaos: true, balancedValues: true,
                    CorpusSeed + 200_000 + (int)character * 1009));
                corpora.Add(BuildSpotCorpus(character, ultimateChaos: true, balancedValues: false,
                    CorpusSeed + 300_000 + (int)character * 1009));
            }
            return new(SchemaVersion, modVersion, CorpusSeed, catalogs, corpora);
        }
        finally
        {
            OperationRuntimeSpecCompiler.EnableLegacyEquivalenceAssertions = false;
        }
    }

    private static BaselineCatalog BuildCatalog(GeneratedCharacter character, bool ultimateChaos)
    {
        var catalog = CharacterComponentCatalogs.Get(character, ultimateChaos);
        var recipes = catalog.Recipes.OrderBy(recipe => recipe.Id, StringComparer.Ordinal).Select(recipe =>
        {
            var sourceOperations = recipe.Atoms.Select((atom, index) =>
                new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
                    index < recipe.TriggerOwners.Count && recipe.TriggerOwners[index] >= 0
                        ? new Dictionary<string, int> { ["triggerIndex"] = recipe.TriggerOwners[index] }
                        : new Dictionary<string, int>(),
                    RequiresSingleTarget: atom.RequiresSingleTarget)).ToArray();
            var operations = sourceOperations.Select((operation, index) =>
                ProjectOperation(operation, index, sourceOperations)).ToArray();
            return new BaselineRecipe(recipe.Id, recipe.ChineseTitle, recipe.EnglishTitle, recipe.Cost,
                recipe.StarCost, recipe.HasStarCostX, recipe.Type, recipe.Target, recipe.OriginalRarity,
                recipe.Tags.Order().ToArray(), recipe.TriggerOwners.ToArray(), operations);
        }).ToArray();
        return new(character, ultimateChaos, recipes);
    }

    private static BaselineCorpus BuildFullPool(GeneratedCharacter character, bool ultimateChaos,
        bool balancedValues, int seed)
    {
        var rarities = PoolRarities(character);
        var generator = new RandomCardGenerator(character, seed, ultimateChaos, balancedValues: balancedValues);
        var cards = rarities.Select(generator.Generate).ToArray();
        var repairRandom = new Random(seed ^ 0x5A17C0DE);
        var failure = string.Empty;
        if (!OstyPoolConstraintResolver.TryResolve(cards, rarities, generator, repairRandom, 20_000, out failure)
            || !SlyPoolConstraintResolver.TryResolve(cards, rarities, generator, repairRandom, 20_000, out failure)
            || !DerivativePoolConstraintResolver.TryRepairAndResolve(cards, rarities, generator, repairRandom,
                20_000, out failure))
            throw new InvalidOperationException($"Baseline pool repair failed for {character}: {failure}");
        if (character != GeneratedCharacter.Colorless)
        {
            var starting = cards.Take(character == GeneratedCharacter.Silent ? 12 : 10).ToArray();
            if (!StartingPoolConstraintResolver.TryRepair(starting, generator, repairRandom, 0, 0, 20_000,
                    out failure))
                throw new InvalidOperationException($"Baseline starter repair failed for {character}: {failure}");
            for (var index = 0; index < starting.Length; index++) cards[index] = starting[index];
        }
        return new($"full-balanced-normal-{character}", character, ultimateChaos, balancedValues, seed,
            cards.Select((card, index) => ProjectCard(index, card)).ToArray());
    }

    private static BaselineCorpus BuildSpotCorpus(GeneratedCharacter character, bool ultimateChaos,
        bool balancedValues, int seed)
    {
        var generator = new RandomCardGenerator(character, seed, ultimateChaos, balancedValues: balancedValues);
        var rarities = Enum.GetValues<GeneratedRarity>()
            .Where(rarity => CharacterComponentCatalogs.Get(character, ultimateChaos).Recipes
                .Any(recipe => recipe.OriginalRarity == rarity))
            .SelectMany(rarity => Enumerable.Repeat(rarity, 4))
            .ToArray();
        var cards = rarities.Select(generator.Generate).ToArray();
        return new($"spot-{(balancedValues ? "balanced" : "aggressive")}-{(ultimateChaos ? "ultimate" : "normal")}-{character}",
            character, ultimateChaos, balancedValues, seed,
            cards.Select((card, index) => ProjectCard(index, card)).ToArray());
    }

    private static GeneratedRarity[] PoolRarities(GeneratedCharacter character) =>
        character == GeneratedCharacter.Colorless
            ? Enumerable.Repeat(GeneratedRarity.Uncommon, 31)
                .Concat(Enumerable.Repeat(GeneratedRarity.Rare, 21)).ToArray()
            : Enumerable.Repeat(GeneratedRarity.Basic, character == GeneratedCharacter.Silent ? 12 : 10)
                .Concat(Enumerable.Repeat(GeneratedRarity.Common, 20))
                .Concat(Enumerable.Repeat(GeneratedRarity.Uncommon, 35))
                .Concat(Enumerable.Repeat(GeneratedRarity.Rare, 25))
                .Concat(Enumerable.Repeat(GeneratedRarity.Ancient, 2)).ToArray();

    private static BaselineCard ProjectCard(int index, GeneratedCard card)
    {
        CardNameGenerator.ValidateLegacyRelationEquivalence(
            CharacterComponentCatalogs.Get(card.Character, card.UnifiedChaos), card.Operations);
        var budgetCost = card.Tags.Contains(CardTag.Sly)
            ? SlyKeywordTuning.ValidationTemplateCost(card.Cost, card.StarCost, card.HasStarCostX,
                card.Rarity, card.Type, card.Tags, card.Operations)
            : card.Cost;
        var paid = budgetCost != 0 || card.StarCost > 0 || card.HasStarCostX;
        var effectiveCost = ResourceEconomyModel.BudgetEffectiveCost(budgetCost, card.StarCost,
            budgetCost < 0, card.HasStarCostX, card.Operations);
        var operations = card.Operations.Select((operation, operationIndex) =>
            ProjectOperation(operation, operationIndex, card.Operations)).ToArray();
        var upgradedSourceOperations = card.Upgrade is null
            ? Array.Empty<GeneratorOperation>()
            : CardUpgradeGenerator.ApplyEffectsToOperations(card.Operations, card.Upgrade.Effects);
        var upgradedOperations = upgradedSourceOperations.Select((operation, operationIndex) =>
            ProjectOperation(operation, operationIndex, upgradedSourceOperations)).ToArray();
        var positiveValue = EffectBalanceModel.EstimatedPositiveCardValue(card.Operations, paid, card.Type, card.Tags);
        var rewardFields = EffectBalanceModel.PositiveRewardFieldCount(card.Operations, paid, card.Type, card.Tags);
        var downsideMultiplier = NegativeEffectTuning.TotalMultiplier(card.Operations, card.Tags, paid, card.Type,
            card.UnifiedChaos ? null : card.Character);
        return new(index, card.Cost, card.StarCost, card.HasStarCostX, card.Type, card.Target, card.Rarity,
            card.Tags.Order().ToArray(), card.Name, card.ChineseDescription, card.EnglishDescription,
            card.Upgrade, operations, upgradedOperations, effectiveCost, positiveValue, rewardFields,
            downsideMultiplier, NegativeEffectTuning.CompensationPercent(downsideMultiplier));
    }

    private static BaselineOperation ProjectOperation(GeneratorOperation operation, int index,
        IReadOnlyList<GeneratorOperation> operations)
    {
        var atom = new ComponentAtom(operation.Template, operation.Scope, operation.ChineseText,
            operation.RequiresSingleTarget, CardReferenceRequirement.None);
        var condition = EffectBalanceModel.IsCondition(atom);
        // The frozen 0.2.118 artifact intentionally records its historical text-shaped field key. Production
        // generation now uses RuntimeSpec structural keys; retaining this projection keeps the comparison file
        // byte-for-byte comparable instead of turning an audit-format migration into a gameplay difference.
        var baselineFieldKey = $"{NumericTextSchema.Family(operation.Template)}|"
            + NumericTextSchema.Fields(operation.ChineseText);
        return new(index, operation.Template, NumericTextSchema.Family(operation.Template),
            baselineFieldKey, operation.Scope, operation.ChineseText,
            EnglishCardDescriptionRenderer.OperationText(operation),
            operation.Parameters.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new BaselineParameter(entry.Key, entry.Value)).ToArray(),
            operation.CardTargetSlot, operation.RequiresSingleTarget, operation.DerivativeId,
            operation.DerivativeEnchantmentId, operation.OrbSourceId, operation.OrbOutputId,
            operation.DerivativeEnchantmentAmount,
            new BaselineClassifications(
                condition,
                CardEffectRules.IsNegativeEffect(operation),
                CardEffectRules.IsBeneficialEffect(operation),
                CardEffectRules.IsRestrictedEffect(operation),
                CardEffectRules.IsExtremeLifecycleDownside(operation),
                CardEffectRules.IsEnemyDamage(operation),
                CardEffectRules.IsDamageBudgetEffect(operation),
                CardEffectRules.IsCardCreationOrTransformation(operation),
                CardEffectRules.IsRandomCardGeneration(operation),
                CardEffectRules.IsSelfCostChange(operation),
                CardEffectRules.IsDelayedEffect(operation),
                CardEffectRules.IsPersistentPowerFoundation(operation),
                CardEffectRules.IsDependencyPrefix(operation),
                CardEffectRules.IsRepeatedTriggerOrCondition(operation)),
            EffectBalanceModel.EstimatedEffectValue(operation),
            condition ? EffectBalanceModel.RelativeTriggerFrequency(operation) : null,
            condition ? EffectBalanceModel.ExpectedTriggerResolutions(operation) : null,
            operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex) ? triggerIndex : null,
            LinkedTriggerIndex(index, operation, operations));
    }

    private static int? LinkedTriggerIndex(int index, GeneratorOperation operation,
        IReadOnlyList<GeneratorOperation> operations)
    {
        if (operation.Parameters.TryGetValue("triggerIndex", out var explicitIndex)) return explicitIndex;
        if (index <= 0) return null;
        var previous = operations.Take(index).ToArray();
        var linked = EffectBalanceModel.LinkedTrigger(previous);
        if (linked is null) return null;
        for (var candidate = index - 1; candidate >= 0; candidate--)
            if (ReferenceEquals(operations[candidate], linked) || operations[candidate] == linked)
                return candidate;
        return null;
    }

    private static byte[] Serialize(RefactorBaselineDocument document) =>
        JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);

    private static void WriteBytes(string path, byte[] bytes)
    {
        if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllBytes(path, bytes);
            return;
        }
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        gzip.Write(bytes);
    }

    private static byte[] ReadBytes(string path)
    {
        if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) return File.ReadAllBytes(path);
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var memory = new MemoryStream();
        gzip.CopyTo(memory);
        return memory.ToArray();
    }

    private static RefactorBaselineResult Result(string path, byte[] uncompressed) => new(path,
        Convert.ToHexString(SHA256.HashData(uncompressed)), uncompressed.Length);

    private static bool HasBuildDirectory(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative.Split('/').Any(segment => segment is "bin" or "obj" or "build" or "retired_installs");
    }

    private static bool LooksLikeSemanticTextDependency(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.StartsWith("//", StringComparison.Ordinal)) return false;
        var semanticCall = code.Contains(".Contains(", StringComparison.Ordinal)
            || code.Contains(".StartsWith(", StringComparison.Ordinal)
            || code.Contains(".EndsWith(", StringComparison.Ordinal)
            || code.Contains("Regex.", StringComparison.Ordinal)
            || code.Contains(".Replace(", StringComparison.Ordinal);
        if (!semanticCall) return false;
        return code.Contains("ChineseText", StringComparison.Ordinal)
            || code.Contains("ChineseDescription", StringComparison.Ordinal)
            || code.Contains("text.", StringComparison.Ordinal)
            || code.Contains("text,", StringComparison.Ordinal)
            || code.Contains("Text)", StringComparison.Ordinal);
    }
}

internal sealed record RefactorBaselineDocument(int SchemaVersion, string ModVersion, int CorpusSeed,
    IReadOnlyList<BaselineCatalog> Catalogs, IReadOnlyList<BaselineCorpus> Corpora);
internal sealed record BaselineCatalog(GeneratedCharacter Character, bool UltimateChaos,
    IReadOnlyList<BaselineRecipe> Recipes);
internal sealed record BaselineRecipe(string Id, string ChineseTitle, string EnglishTitle, int Cost, int StarCost,
    bool HasStarCostX, GeneratedCardType Type, TargetMode Target, GeneratedRarity Rarity,
    IReadOnlyList<CardTag> Tags, IReadOnlyList<int> TriggerOwners, IReadOnlyList<BaselineOperation> Operations);
internal sealed record BaselineCorpus(string Id, GeneratedCharacter Character, bool UltimateChaos,
    bool BalancedValues, int Seed, IReadOnlyList<BaselineCard> Cards);
internal sealed record BaselineCard(int Index, int Cost, int StarCost, bool HasStarCostX, GeneratedCardType Type,
    TargetMode Target, GeneratedRarity Rarity, IReadOnlyList<CardTag> Tags, GeneratedCardName? Name,
    string ChineseDescription, string EnglishDescription, CardUpgradePlan? Upgrade,
    IReadOnlyList<BaselineOperation> Operations, IReadOnlyList<BaselineOperation> UpgradedOperations,
    double EffectiveCost, double PositiveValue, int PositiveRewardFields, double DownsideMultiplier,
    int DownsideCompensationPercent);
internal sealed record BaselineOperation(int Index, string Template, string Family, string Field,
    OperationScope Scope, string ChineseText, string EnglishText, IReadOnlyList<BaselineParameter> Parameters,
    string? CardTargetSlot, bool RequiresSingleTarget, string? DerivativeId, string? DerivativeEnchantmentId,
    string? OrbSourceId, string? OrbOutputId, int? DerivativeEnchantmentAmount,
    BaselineClassifications Classifications, int EstimatedEffectValue, double? RelativeTriggerFrequency,
    double? ExpectedTriggerResolutions, int? ExplicitTriggerIndex, int? LinkedTriggerIndex);
internal sealed record BaselineParameter(string Name, int Value);
internal sealed record BaselineClassifications(bool Condition, bool Negative, bool Beneficial, bool Restricted,
    bool ExtremeLifecycleDownside, bool EnemyDamage, bool DamageBudgetEffect, bool CardCreationOrTransformation,
    bool RandomCardGeneration, bool SelfCostChange, bool Delayed, bool PersistentPowerFoundation,
    bool DependencyPrefix, bool RepeatedTriggerOrCondition);
internal sealed record RefactorBaselineResult(string Path, string Sha256, long UncompressedBytes);
internal sealed record RefactorBaselineComparison(bool Matches, RefactorBaselineResult Current, string Detail);
internal sealed record TextDependencyAuditResult(string Path, int Entries);
