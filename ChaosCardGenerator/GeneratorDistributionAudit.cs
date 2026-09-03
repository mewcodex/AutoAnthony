using System.Text;
using System.Text.RegularExpressions;

namespace ChaosCardGenerator;

public static class GeneratorDistributionAudit
{
    public static string RunNativeBudgetCenterAudit()
    {
        var rows = Enum.GetValues<GeneratedCharacter>()
            .Where(character => character != GeneratedCharacter.Colorless)
            .SelectMany(character => CharacterComponentCatalogs.Get(character).Recipes.Select(recipe =>
                NativeBudgetRow(character, recipe)))
            .Where(row => row is not null)
            .Select(row => row!.Value)
            .ToArray();
        var output = new StringBuilder();
        output.AppendLine("rarity\teffectiveCostTier\tcards\tmedianNormalizedValue\ttrimmedMeanNormalizedValue\tp25\tp75");
        foreach (var group in rows.GroupBy(row => (row.Rarity, row.CostTier))
                     .OrderBy(group => group.Key.Rarity).ThenBy(group => group.Key.CostTier))
        {
            var values = group.Select(row => row.Value).Order().ToArray();
            var trim = values.Length >= 10 ? Math.Max(1, values.Length / 10) : 0;
            var trimmed = values.Skip(trim).Take(values.Length - trim * 2).ToArray();
            output.AppendLine($"{group.Key.Rarity}\t{group.Key.CostTier}\t{values.Length}\t"
                + $"{Percentile(values, 0.50d):0.0}\t{trimmed.Average():0.0}\t"
                + $"{Percentile(values, 0.25d):0.0}\t{Percentile(values, 0.75d):0.0}");
        }
        output.AppendLine();
        output.AppendLine("character\tcard\ttype\trarity\teffectiveCostTier\tfields\tnormalizedValue");
        foreach (var row in rows.OrderBy(row => row.Rarity).ThenBy(row => row.CostTier)
                     .ThenBy(row => row.Value))
            output.AppendLine($"{row.Character}\t{row.CardId}\t{row.Type}\t{row.Rarity}\t{row.CostTier}\t"
                + $"{row.Fields}\t{row.Value:0.0}");
        return output.ToString();
    }

    private static (GeneratedCharacter Character, string CardId, GeneratedCardType Type,
        GeneratedRarity Rarity, int CostTier, int Fields, double Value)? NativeBudgetRow(
        GeneratedCharacter character, IroncladCardRecipe recipe)
    {
        if (recipe.Cost < 0 || recipe.HasStarCostX) return null;
        var operations = recipe.Atoms.Select((atom, index) => new GeneratorOperation(
            atom.Template, atom.Scope, string.Empty,
            recipe.TriggerOwners[index] < 0
                ? new Dictionary<string, int>()
                : new Dictionary<string, int> { ["triggerIndex"] = recipe.TriggerOwners[index] },
            RequiresSingleTarget: atom.RequiresSingleTarget,
            RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom))).ToArray();
        var energyCost = Math.Min(4, recipe.Cost);
        var effectiveCost = ResourceEconomyModel.BudgetEffectiveCost(energyCost, recipe.StarCost,
            hasEnergyX: false, hasStarX: false, operations);
        if (double.IsNaN(effectiveCost)) return null;
        var positive = EffectBalanceModel.EstimatedPositiveCardValue(operations,
            energyCost != 0 || recipe.StarCost > 0, recipe.Type, recipe.Tags);
        if (positive <= 0d) return null;
        var downside = CardEffectRules.NegativeEffectCompensationPercent(operations, recipe.Tags,
            energyCost != 0 || recipe.StarCost > 0, recipe.Type, character);
        var linearDownside = CardEffectRules.NegativeEffectLinearCompensationValue(operations);
        var power = ComponentAssemblyGenerator.PowerOneShotBudgetFactor(operations, recipe.Type);
        var normalized = (positive - linearDownside)
            / (Math.Max(100, downside) / 100d) / Math.Max(1d, power);
        return (character, recipe.Id, recipe.Type, recipe.OriginalRarity,
            Math.Clamp((int)Math.Round(effectiveCost, MidpointRounding.AwayFromZero), 0, 4),
            EffectBalanceModel.PositiveRewardFieldCount(operations, energyCost != 0 || recipe.StarCost > 0,
                recipe.Type, recipe.Tags), normalized);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0d;
        var position = Math.Clamp(percentile, 0d, 1d) * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    public static string Run(GeneratedCharacter character, int samplesPerRarity, int seed = 20260820,
        bool ultimateChaos = false, bool balancedValues = true)
    {
        var catalog = CharacterComponentCatalogs.Get(character, ultimateChaos);
        var output = new StringBuilder();
        output.AppendLine($"character={character}; samplesPerRarity={samplesPerRarity}; ultimateChaos={ultimateChaos}; balancedValues={balancedValues}");
        output.AppendLine("rarity\tsourceTypes(A/S/P)\tgeneratedTypes(A/S/P)\tsourceSingleTarget\tgeneratedSingleTarget\tsourceAvgCost\tgeneratedAvgCost\tsourceX\tgeneratedX\tsourceAvgEffects\tgeneratedAvgEffects\tgeneratedSingleEffect\tgeneratedOperationDownside\tgeneratedKeywordDownside\tgeneratedAnyDownside");

        var generatedByRarity = new Dictionary<GeneratedRarity, GeneratedCard[]>();
        var rarities = Enum.GetValues<GeneratedRarity>()
            .Where(rarity => catalog.Recipes.Any(recipe => recipe.OriginalRarity == rarity))
            .ToArray();
        foreach (var rarity in rarities)
        {
            var source = catalog.Recipes.Where(recipe => recipe.OriginalRarity == rarity).ToArray();
            RandomCardGenerator? generator = null;
            // Each tracker/uniqueness set models one real rarity slice, not an artificial 90-card same-rarity pool.
            // Reset at the native slice size so occurrence feedback and emergency fallback pressure match gameplay.
            var poolBatchSize = Math.Max(1, source.Length);
            var generated = Enumerable.Range(0, samplesPerRarity).Select(index =>
            {
                if (index % poolBatchSize == 0)
                    generator = new RandomCardGenerator(character, seed + (int)rarity * 10_000 + index,
                        ultimateChaos, balancedValues: balancedValues);
                return generator!.Generate(rarity);
            }).ToArray();
            generatedByRarity[rarity] = generated;
            output.AppendLine(string.Join('\t',
                rarity,
                TypeMix(source.Select(recipe => recipe.Type)),
                TypeMix(generated.Select(card => card.Type)),
                Percent(source.Count(recipe => recipe.Target == TargetMode.SingleEnemy), source.Length),
                Percent(generated.Count(card => card.Target == TargetMode.SingleEnemy), generated.Length),
                AverageCost(source.Select(recipe => recipe.Cost)),
                AverageCost(generated.Select(card => card.Cost)),
                Percent(source.Count(recipe => recipe.Cost == -1), source.Length),
                Percent(generated.Count(card => card.Cost == -1), generated.Length),
                source.Average(recipe => recipe.Atoms.Count).ToString("0.00"),
                generated.Average(card => EffectCount(card.Operations)).ToString("0.00"),
                Percent(generated.Count(card => EffectCount(card.Operations) == 1), generated.Length),
                Percent(generated.Count(card => card.Operations.Any(CardEffectRules.IsNegativeEffect)),
                    generated.Length),
                Percent(generated.Count(card => CardEffectRules.HasNegativeKeyword(card.Tags)), generated.Length),
                Percent(generated.Count(card => card.Operations.Any(CardEffectRules.IsNegativeEffect)
                    || CardEffectRules.HasNegativeKeyword(card.Tags)), generated.Length)));
        }

        var allGenerated = generatedByRarity.Values.SelectMany(cards => cards).ToArray();
        var generatedWeightByRarity = rarities.ToDictionary(rarity => rarity,
            rarity => catalog.Recipes.Count(recipe => recipe.OriginalRarity == rarity)
                      / (double)Math.Max(1, generatedByRarity[rarity].Length));
        double GeneratedWeight(GeneratedCard card) => generatedWeightByRarity.GetValueOrDefault(card.Rarity);
        var totalGeneratedWeight = allGenerated.Sum(GeneratedWeight);
        output.AppendLine();
        output.AppendLine("tag\tsourceCardsPer100\tgeneratedCardsPer100\tratio");
        foreach (var tag in Enum.GetValues<CardTag>())
        {
            var sourceRate = 100d * catalog.Recipes.Count(recipe => recipe.Tags.Contains(tag))
                / catalog.Recipes.Count;
            var generatedRate = 100d * allGenerated.Where(card => card.Tags.Contains(tag)).Sum(GeneratedWeight)
                / totalGeneratedWeight;
            var ratio = sourceRate == 0 ? double.NaN : generatedRate / sourceRate;
            output.AppendLine($"{tag}\t{sourceRate:0.00}\t{generatedRate:0.00}\t"
                + $"{(double.IsNaN(ratio) ? "n/a" : ratio.ToString("0.00"))}");
        }
        output.AppendLine();
        output.AppendLine("rarity\tsourceCost(0/1/2/3/4+)\tgeneratedCost(0/1/2/3/4+)\tgeneratedZeroResource\tgeneratedEffectiveZero\tsourceStarPayment\tgeneratedStarPayment\tsourceHighStarPayment\tgeneratedHighStarPayment\tgeneratedInnateUpgrade\tnonZeroWithoutBenefit\tcommonOneCostPureDraw2\ttinyImmediateReward\ttinyStandaloneCombatReward");
        foreach (var rarity in rarities)
        {
            var source = catalog.Recipes.Where(recipe => recipe.OriginalRarity == rarity && recipe.Cost >= 0).ToArray();
            var generated = generatedByRarity[rarity].Where(card => card.Cost >= 0).ToArray();
            output.AppendLine(string.Join('\t', rarity, CostMix(source.Select(recipe => recipe.Cost)),
                CostMix(generated.Select(card => card.Cost)),
                Percent(generated.Count(IsZeroResourceCard), generated.Length),
                Percent(generated.Count(IsEffectiveZeroCard), generated.Length),
                Percent(source.Count(recipe => recipe.StarCost > 0 || recipe.HasStarCostX), source.Length),
                Percent(generated.Count(card => card.StarCost > 0 || card.HasStarCostX), generated.Length),
                Percent(source.Count(recipe => recipe.StarCost >= 4), source.Length),
                Percent(generated.Count(card => card.StarCost >= 4), generated.Length),
                Percent(generated.Count(card => GeneratedCardTagPolicy.AddedKeywords(card.Upgrade)
                    .Contains(CardTag.Innate)), generated.Length),
                generated.Count(card => card.Cost > 0 && !card.Operations.Any(CardEffectRules.IsBeneficialEffect)),
                generated.Count(IsCommonOneCostPureDrawTwo),
                generated.Count(card => card.Operations.Any(CardAcceptanceTuning.IsTinyImmediateReward)),
                generated.Count(card => card.Operations.Select((_, index) => index)
                    .Any(index => CardAcceptanceTuning.IsTinyStandaloneCombatReward(card.Operations, index)))));
        }
        var sourceFamilies = catalog.Recipes.SelectMany(recipe => recipe.Atoms.Select(atom => atom.FamilyKey)).ToArray();
        var generatedFamilies = allGenerated.SelectMany(card => card.Operations
            .Where(operation => !operation.Template.StartsWith("N_SELECT_", StringComparison.Ordinal))
            .Select(operation => (Card: card, Family: NumericTextSchema.Family(operation.Template)))).ToArray();
        output.AppendLine();
        var sourceFamilyCounts = sourceFamilies.GroupBy(family => family, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        output.AppendLine("family\tsourceOccurrences\tsourcePer100Cards\tgeneratedPer100Cards\tratio");
        foreach (var family in sourceFamilies.Concat(generatedFamilies.Select(item => item.Family)).Distinct()
                     .OrderBy(family => family, StringComparer.Ordinal))
        {
            var sourceRate = 100d * sourceFamilies.Count(candidate => candidate == family) / catalog.Recipes.Count;
            var generatedRate = 100d * generatedFamilies.Where(candidate => candidate.Family == family)
                .Sum(candidate => GeneratedWeight(candidate.Card)) / totalGeneratedWeight;
            var ratio = sourceRate == 0 ? double.NaN : generatedRate / sourceRate;
            output.AppendLine($"{family}\t{sourceFamilyCounts.GetValueOrDefault(family)}\t{sourceRate:0.00}\t"
                              + $"{generatedRate:0.00}\t{(double.IsNaN(ratio) ? "n/a" : ratio.ToString("0.00"))}");
        }
        var sourcePowers = catalog.Recipes.Where(recipe => recipe.Type == GeneratedCardType.Power).ToArray();
        var generatedPowers = allGenerated.Where(card => card.Type == GeneratedCardType.Power).ToArray();
        var sourcePowerFamilies = sourcePowers.SelectMany(recipe => recipe.Atoms.Select(atom => atom.FamilyKey))
            .ToArray();
        var generatedPowerWeight = generatedPowers.Sum(GeneratedWeight);
        var generatedPowerFamilies = generatedPowers.SelectMany(card => card.Operations
                .Where(operation => !operation.Template.StartsWith("N_SELECT_", StringComparison.Ordinal))
                .Select(operation => (Card: card, Family: NumericTextSchema.Family(operation.Template))))
            .ToArray();
        output.AppendLine();
        output.AppendLine("powerFamily\tsourcePer100PowerCards\tgeneratedPer100PowerCards\tratio");
        foreach (var family in sourcePowerFamilies.Concat(generatedPowerFamilies.Select(item => item.Family))
                     .Distinct().OrderBy(family => family, StringComparer.Ordinal))
        {
            var sourceRate = sourcePowers.Length == 0 ? 0d
                : 100d * sourcePowerFamilies.Count(candidate => candidate == family) / sourcePowers.Length;
            var generatedRate = generatedPowerWeight <= 0 ? 0d
                : 100d * generatedPowerFamilies.Where(candidate => candidate.Family == family)
                    .Sum(candidate => GeneratedWeight(candidate.Card)) / generatedPowerWeight;
            var ratio = sourceRate == 0 ? double.NaN : generatedRate / sourceRate;
            output.AppendLine($"{family}\t{sourceRate:0.00}\t{generatedRate:0.00}\t"
                + $"{(double.IsNaN(ratio) ? "n/a" : ratio.ToString("0.00"))}");
        }
        var sourcePowerRoleFamilies = sourcePowers.SelectMany(recipe => recipe.Atoms.Select((atom, index) =>
            (Role: AuditSourceRole(recipe, index), Family: atom.FamilyKey))).ToArray();
        var generatedPowerRoleFamilies = generatedPowers.SelectMany(card => card.Operations
            .Select((operation, index) =>
                (Card: card, Role: AuditGeneratedRole(card, index),
                    Family: NumericTextSchema.Family(operation.Template))))
            .ToArray();
        output.AppendLine();
        output.AppendLine("powerRoleFamily\trole\tsourcePer100PowerCards\tgeneratedPer100PowerCards\tratio");
        foreach (var item in sourcePowerRoleFamilies.Concat(generatedPowerRoleFamilies.Select(candidate =>
                         (candidate.Role, candidate.Family))).Distinct()
                     .OrderBy(item => item.Role).ThenBy(item => item.Family, StringComparer.Ordinal))
        {
            var sourceRate = sourcePowers.Length == 0 ? 0d : 100d * sourcePowerRoleFamilies.Count(candidate =>
                candidate == item) / sourcePowers.Length;
            var generatedRate = generatedPowerWeight <= 0 ? 0d : 100d * generatedPowerRoleFamilies.Where(candidate =>
                candidate.Role == item.Role && candidate.Family == item.Family)
                .Sum(candidate => GeneratedWeight(candidate.Card)) / generatedPowerWeight;
            var ratio = sourceRate == 0 ? double.NaN : generatedRate / sourceRate;
            output.AppendLine($"{item.Family}\t{item.Role}\t{sourceRate:0.00}\t{generatedRate:0.00}\t"
                              + $"{(double.IsNaN(ratio) ? "n/a" : ratio.ToString("0.00"))}");
        }
        output.AppendLine();
        output.AppendLine("powerScope\tsourcePer100PowerCards\tgeneratedPer100PowerCards\tratio");
        foreach (var scope in Enum.GetValues<OperationScope>())
        {
            var sourceRate = sourcePowers.Length == 0 ? 0d
                : 100d * sourcePowers.Sum(recipe => recipe.Atoms.Count(atom => atom.Scope == scope))
                    / sourcePowers.Length;
            var generatedRate = generatedPowerWeight <= 0 ? 0d
                : 100d * generatedPowers.Sum(card => GeneratedWeight(card) * card.Operations.Count(operation =>
                    !operation.Template.StartsWith("N_SELECT_", StringComparison.Ordinal)
                    && operation.Scope == scope))
                    / generatedPowerWeight;
            if (sourceRate == 0d && generatedRate == 0d) continue;
            output.AppendLine($"{scope}\t{sourceRate:0.00}\t{generatedRate:0.00}\t"
                + $"{(sourceRate == 0d ? "n/a" : (generatedRate / sourceRate).ToString("0.00"))}");
        }
        output.AppendLine();
        output.AppendLine("sourceFamilyOccurrences\tfamilies\tsourcePer100Cards\tgeneratedPer100Cards\tratio");
        var frequencyBands = new (string Name, Func<int, bool> Includes)[]
        {
            ("1", count => count == 1),
            ("2", count => count == 2),
            ("3-4", count => count is 3 or 4),
            ("5+", count => count >= 5)
        };
        foreach (var (name, includes) in frequencyBands)
        {
            var familyIds = sourceFamilyCounts.Where(entry => includes(entry.Value))
                .Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
            var sourceCount = sourceFamilies.Count(familyIds.Contains);
            var generatedCount = generatedFamilies.Where(item => familyIds.Contains(item.Family))
                .Sum(item => GeneratedWeight(item.Card));
            var sourceRate = 100d * sourceCount / catalog.Recipes.Count;
            var generatedRate = 100d * generatedCount / totalGeneratedWeight;
            output.AppendLine($"{name}\t{familyIds.Count}\t{sourceRate:0.00}\t{generatedRate:0.00}\t"
                + $"{(sourceRate == 0 ? "n/a" : (generatedRate / sourceRate).ToString("0.00"))}");
        }
        if (character == GeneratedCharacter.Necrobinder)
        {
            output.AppendLine();
            output.AppendLine("doomRole\tsourceCardsPer100\tgeneratedCardsPer100\tratio");
            var doomRoles = new (string Name, Func<string, bool> Match)[]
            {
                ("AnyEnemyDoomProducer", template => template is "NCR:ApplyDoom" or "NCR:ApplyDoomAll"
                    or "NCR:ApplyDoomEqualDamage" or "NCR:ApplyEventDamageAsDoom"),
                ("TargetedDoom", template => template == "NCR:ApplyDoom"),
                ("AllEnemyDoom", template => template == "NCR:ApplyDoomAll"),
                ("OtherEnemyDoomProducer", template => template is "NCR:ApplyDoomEqualDamage" or "NCR:ApplyEventDamageAsDoom"),
                ("DoomConsumer", IsDoomConsumer)
            };
            foreach (var (name, match) in doomRoles)
            {
                var sourceCount = catalog.Recipes.Count(recipe => recipe.Atoms.Any(atom => match(atom.Template)));
                var generatedCount = allGenerated.Count(card => card.Operations.Any(operation => match(operation.Template)));
                var sourceRate = 100d * sourceCount / catalog.Recipes.Count;
                var generatedRate = 100d * generatedCount / allGenerated.Length;
                output.AppendLine($"{name}\t{sourceRate:0.00}\t{generatedRate:0.00}\t{(sourceRate == 0 ? "n/a" : (generatedRate / sourceRate).ToString("0.00"))}");
            }

            output.AppendLine();
            output.AppendLine("doomAmountOrigin\ttemplate\trarity\tcost\tcount\taverage\tminimum\tmaximum");
            var sourceDoomRows = catalog.Recipes.SelectMany(recipe => recipe.Atoms
                .Where(atom => atom.Template is "NCR:ApplyDoom" or "NCR:ApplyDoomAll")
                .Select(atom => new { Origin = "Source", atom.Template, recipe.OriginalRarity, recipe.Cost, Value = FirstNumber(atom.ChineseText) }));
            var generatedDoomRows = allGenerated.SelectMany(card => card.Operations
                .Where(operation => operation.Template is "NCR:ApplyDoom" or "NCR:ApplyDoomAll")
                .Select(operation => new { Origin = "Generated", operation.Template, OriginalRarity = card.Rarity, card.Cost, Value = FirstNumber(operation.ChineseText) }));
            foreach (var group in sourceDoomRows.Concat(generatedDoomRows)
                         .Where(row => row.Value > 0)
                         .GroupBy(row => (row.Origin, row.Template, row.OriginalRarity, row.Cost))
                         .OrderBy(group => group.Key.Origin, StringComparer.Ordinal)
                         .ThenBy(group => group.Key.OriginalRarity)
                         .ThenBy(group => group.Key.Cost)
                         .ThenBy(group => group.Key.Template, StringComparer.Ordinal))
                output.AppendLine($"{group.Key.Origin}\t{group.Key.Template}\t{group.Key.OriginalRarity}\t{group.Key.Cost}\t{group.Count()}\t"
                    + $"{group.Average(row => row.Value):0.00}\t{group.Min(row => row.Value)}\t{group.Max(row => row.Value)}");

            output.AppendLine();
            output.AppendLine("damageTemplate\trarity\tcost\tcount\taverage\tminimum\tmaximum");
            var damageRows = allGenerated.SelectMany(card => card.Operations
                    .Where(operation => operation.Template is "T:D" or "N:AllD" or "NCR:OstyDamage" or "NCR:OstyAllDamage")
                    .Select(operation => new
                    {
                        operation.Template,
                        card.Rarity,
                        card.Cost,
                        Value = FirstNumber(operation.ChineseText)
                    }))
                .Where(row => row.Value > 0)
                .GroupBy(row => (row.Template, row.Rarity, row.Cost))
                .OrderBy(group => group.Key.Rarity)
                .ThenBy(group => group.Key.Cost)
                .ThenBy(group => group.Key.Template, StringComparer.Ordinal);
            foreach (var group in damageRows)
                output.AppendLine($"{group.Key.Template}\t{group.Key.Rarity}\t{group.Key.Cost}\t{group.Count()}\t"
                    + $"{group.Average(row => row.Value):0.00}\t{group.Min(row => row.Value)}\t{group.Max(row => row.Value)}");
        }
        return output.ToString();
    }

    private static NativeComponentRole AuditSourceRole(IroncladCardRecipe recipe, int atomIndex)
    {
        var owner = atomIndex < recipe.TriggerOwners.Count ? recipe.TriggerOwners[atomIndex] : -1;
        if (owner >= 0 && owner < recipe.Atoms.Count)
            return recipe.Atoms[owner].Scope == OperationScope.AbilityTrigger
                ? NativeComponentRole.AbilityPayoff
                : NativeComponentRole.ConditionalPayoff;
        var atom = recipe.Atoms[atomIndex];
        return recipe.Type == GeneratedCardType.Power
               && atom.Scope is not (OperationScope.AbilityTrigger or OperationScope.AbilityRule)
            ? NativeComponentRole.AbilityFoundation
            : NativeComponentRole.Unlinked;
    }

    private static NativeComponentRole AuditGeneratedRole(GeneratedCard card, int operationIndex)
    {
        var operation = card.Operations[operationIndex];
        if (operation.Parameters.TryGetValue("triggerIndex", out var owner)
            && owner >= 0 && owner < card.Operations.Count)
            return card.Operations[owner].Scope == OperationScope.AbilityTrigger
                ? NativeComponentRole.AbilityPayoff
                : NativeComponentRole.ConditionalPayoff;
        return card.Type == GeneratedCardType.Power
               && operation.Scope is not (OperationScope.AbilityTrigger or OperationScope.AbilityRule)
            ? NativeComponentRole.AbilityFoundation
            : NativeComponentRole.Unlinked;
    }

    private static bool IsDoomConsumer(string template) => template is
        "NCR:IfDoomAppliedThisTurn" or "NCR:DoomScaledDamage" or "NCR:DoomPerDoomThreshold"
        or "NCR:ForEachDoomThreshold" or "NCR:KillEnemiesAtDoomThreshold" or "A:whenDoomApplied";

    private static int FirstNumber(string text)
    {
        var match = Regex.Match(text, @"\d+");
        return match.Success ? int.Parse(match.Value) : 0;
    }

    private static int EffectCount(IEnumerable<GeneratorOperation> operations) =>
        operations.Count(operation => !operation.Template.StartsWith("N_SELECT_", StringComparison.Ordinal));

    private static bool IsZeroResourceCard(GeneratedCard card) =>
        card.Cost == 0 && card.StarCost <= 0 && !card.HasStarCostX;

    private static bool IsEffectiveZeroCard(GeneratedCard card)
    {
        var effective = ResourceEconomyModel.BudgetEffectiveCost(card.Cost, card.StarCost,
            card.Cost < 0, card.HasStarCostX, card.Operations);
        return !double.IsNaN(effective) && effective <= 0.25d;
    }

    private static bool IsCommonOneCostPureDrawTwo(GeneratedCard card) =>
        card.Rarity == GeneratedRarity.Common
        && CardAcceptanceTuning.MarginalPureDrawAcceptancePercent(card.Operations, card.Rarity,
            ResourceEconomyModel.BudgetEffectiveCost(card.Cost, card.StarCost,
                card.Cost < 0, card.HasStarCostX, card.Operations)) == 18;

    private static string TypeMix(IEnumerable<GeneratedCardType> values)
    {
        var array = values.ToArray();
        return string.Join('/',
            Percent(array.Count(value => value == GeneratedCardType.Attack), array.Length),
            Percent(array.Count(value => value == GeneratedCardType.Skill), array.Length),
            Percent(array.Count(value => value == GeneratedCardType.Power), array.Length));
    }

    private static string AverageCost(IEnumerable<int> values)
    {
        var fixedCosts = values.Where(value => value >= 0).ToArray();
        return fixedCosts.Length == 0 ? "X" : fixedCosts.Average().ToString("0.00");
    }

    private static string CostMix(IEnumerable<int> values)
    {
        var array = values.ToArray();
        return string.Join('/', Enumerable.Range(0, 5).Select(band =>
            Percent(array.Count(value => band < 4 ? value == band : value >= 4), array.Length)));
    }

    private static string Percent(int numerator, int denominator) =>
        denominator == 0 ? "0.0" : (100d * numerator / denominator).ToString("0.0");
}
