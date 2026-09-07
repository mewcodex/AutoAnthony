using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ChaosCardGenerator;

/// <summary>
/// Offline reverse valuation of every authored v111 native-card recipe. This audit deliberately stays outside the
/// runtime mod: it compares the current component model with the balanced whole-card curve, and attributes each
/// card's residual through dependency-safe leave-one-unit-out marginals. Trigger + payoff chains, selector + payoff
/// chains and dependency prefixes are removed as one unit so an invalid half-card is never valued in isolation.
///
/// The balanced model is always the reference model. Aggressive/random-number modes are presentation variants and
/// must not be used to calibrate component values.
/// </summary>
internal static class NativeCardValuationAudit
{
    private const string MethodVersion = "native-card-generator-valuation-v4";

    internal static NativeValuationAuditResult Write(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException($"Refusing to overwrite non-empty native valuation audit directory: {output}");
        Directory.CreateDirectory(output);

        var cards = new List<CardRow>();
        var occurrences = new List<ComponentOccurrence>();
        var packageOccurrences = new List<PackageOccurrence>();
        var operationValuations = new List<OperationValuationRow>();
        var xCheckpoints = new List<XCheckpointRow>();
        foreach (var character in Enum.GetValues<GeneratedCharacter>())
        {
            foreach (var recipe in CharacterComponentCatalogs.Get(character).Recipes)
            {
                AuditCard(character, recipe, cards, occurrences, packageOccurrences, operationValuations);
                AuditXCheckpoints(character, recipe, xCheckpoints);
            }
        }

        if (cards.Count == 0)
            throw new InvalidOperationException("Native valuation audit found no authored recipes.");

        // First diagnose the configured rarity/cost curve itself. Component inference then uses the robust native
        // median inside the same rarity/effective-cost tier, otherwise a global Rare-center error would be falsely
        // attributed to every component that happens to occur mostly on Rare cards.
        var cohortFactors = cards.GroupBy(card => (card.Rarity, Tier: CostTier(card.EffectiveCost)))
            .ToDictionary(group => group.Key, group => Median(group.Select(card => card.Ratio)));
        var calibratedCards = cards.Select(card => card with
        {
            NativeCohortTarget = card.TargetCenter * cohortFactors[(card.Rarity, CostTier(card.EffectiveCost))]
        }).Select(card => card with
        {
            NativeCohortRatio = card.NormalizedValue / Math.Max(1d, card.NativeCohortTarget)
        }).ToArray();
        var calibratedOccurrences = occurrences.Select(item => Calibrate(item,
            cohortFactors[(item.Rarity, CostTier(item.EffectiveCost))])).ToArray();
        var calibratedPackages = packageOccurrences.Select(item => Calibrate(item,
            cohortFactors[(item.Rarity, CostTier(item.EffectiveCost))])).ToArray();

        // Generator-facing reports use the actual configured budget center. Native cohort normalization remains
        // available on card rows and cohort tables as a secondary balance diagnostic, but must not replace the
        // answer to “how would the generator itself price this card?”.
        var componentRows = BuildComponentRows(occurrences);
        var packageRows = BuildPackageRows(packageOccurrences);
        var cohortRows = BuildCohortRows(calibratedCards);
        var outliers = calibratedCards.OrderByDescending(card =>
                Math.Abs(Math.Log(Math.Max(0.01d, card.Ratio))))
            .ToArray();

        WriteCards(Path.Combine(output, "cards.tsv"), calibratedCards);
        WriteComponentOccurrences(Path.Combine(output, "component_occurrences.tsv"), occurrences);
        WriteOperationValuations(Path.Combine(output, "operation_valuations.tsv"), operationValuations);
        WritePackageOccurrences(Path.Combine(output, "trigger_package_occurrences.tsv"), packageOccurrences);
        WriteComponentOccurrences(Path.Combine(output, "native_cohort_component_occurrences.tsv"),
            calibratedOccurrences);
        WritePackageOccurrences(Path.Combine(output, "native_cohort_trigger_package_occurrences.tsv"),
            calibratedPackages);
        WriteComponents(Path.Combine(output, "components.tsv"), componentRows);
        WritePackages(Path.Combine(output, "trigger_packages.tsv"), packageRows);
        WriteCohorts(Path.Combine(output, "cohorts.tsv"), cohortRows);
        WriteCards(Path.Combine(output, "outliers.tsv"), outliers);
        WriteXCheckpoints(Path.Combine(output, "x_checkpoints.tsv"), xCheckpoints);
        WriteMetadata(Path.Combine(output, "metadata.json"), calibratedCards, calibratedOccurrences,
            calibratedPackages, xCheckpoints);
        WriteSummary(Path.Combine(output, "summary.md"), calibratedCards, componentRows, packageRows, cohortRows,
            xCheckpoints);

        return new NativeValuationAuditResult(output, cards.Count, occurrences.Count,
            componentRows.Count, packageRows.Count);
    }

    internal static void ValidateCoverage()
    {
        var expectedCards = Enum.GetValues<GeneratedCharacter>()
            .Sum(character => CharacterComponentCatalogs.Get(character).Recipes.Count);
        var expectedOperations = Enum.GetValues<GeneratedCharacter>()
            .Sum(character => CharacterComponentCatalogs.Get(character).Recipes.Sum(recipe => recipe.Atoms.Count));
        var cards = new List<CardRow>();
        var components = new List<ComponentOccurrence>();
        var packages = new List<PackageOccurrence>();
        var xCheckpoints = new List<XCheckpointRow>();
        foreach (var character in Enum.GetValues<GeneratedCharacter>())
        foreach (var recipe in CharacterComponentCatalogs.Get(character).Recipes)
        {
            AuditCard(character, recipe, cards, components, packages);
            AuditXCheckpoints(character, recipe, xCheckpoints);
        }
        var operationComponents = components.Count(component =>
            !component.Template.StartsWith("KEYWORD:", StringComparison.Ordinal));
        if (cards.Count != expectedCards || operationComponents != expectedOperations)
            throw new InvalidOperationException("Native valuation audit did not cover every native recipe operation: "
                + $"cards={cards.Count}/{expectedCards}, operations={operationComponents}/{expectedOperations}.");
        if (cards.Any(card => !double.IsFinite(card.NormalizedValue) || !double.IsFinite(card.TargetCenter)
                              || card.TargetCenter <= 0d))
            throw new InvalidOperationException("Native valuation audit produced a non-finite card value.");
        if (components.Any(component => string.IsNullOrWhiteSpace(component.Template)
                                        || string.IsNullOrWhiteSpace(component.Family)))
            throw new InvalidOperationException("Native valuation audit produced an unidentified component.");
        var expectedXRows = Enum.GetValues<GeneratedCharacter>()
            .Sum(character => CharacterComponentCatalogs.Get(character).Recipes.Count(recipe =>
                recipe.Cost < 0 || recipe.HasStarCostX)) * VariableXCardBalance.GenerationCheckpoints.Count;
        if (xCheckpoints.Count != expectedXRows
            || xCheckpoints.Any(row => row.ResolvedX is not (1 or 3)
                                       || !double.IsFinite(row.NetValue)
                                       || !double.IsFinite(row.Minimum)
                                       || !double.IsFinite(row.Maximum)
                                       || row.Maximum <= 0d))
            throw new InvalidOperationException("Native X valuation audit did not produce finite X=1/X=3 rows: "
                + $"rows={xCheckpoints.Count}/{expectedXRows}.");
        var nonScalingXCards = xCheckpoints.GroupBy(row => (row.Character, row.CardId))
            .Where(group => group.Single(row => row.ResolvedX == 3).NetValue
                            <= group.Single(row => row.ResolvedX == 1).NetValue)
            .Select(group => $"{group.Key.Character}/{group.Key.CardId}").ToArray();
        if (nonScalingXCards.Length > 0)
            throw new InvalidOperationException("Native X valuation remained constant between X=1 and X=3: "
                + string.Join(", ", nonScalingXCards));
    }

    private static void AuditXCheckpoints(GeneratedCharacter character, IroncladCardRecipe recipe,
        ICollection<XCheckpointRow> rows)
    {
        if (recipe.Cost >= 0 && !recipe.HasStarCostX) return;
        var card = BuildGeneratedCard(character, recipe);
        foreach (var point in VariableXCardBalance.EvaluateGenerationCheckpoints(card, balancedValues: true))
            rows.Add(new XCheckpointRow(character, recipe.Id, recipe.OriginalRarity, recipe.Cost,
                recipe.StarCost, recipe.Cost < 0, recipe.HasStarCostX, point.ResolvedX, point.EffectiveCost,
                point.NetValue, point.Minimum, point.Maximum,
                point.NetValue / Math.Max(1d, ComponentAssemblyGenerator.CalibratedWholeCardCenter(
                    recipe.OriginalRarity, point.EffectiveCost)), point.IsWithinEnvelope()));
    }

    private static GeneratedCard BuildGeneratedCard(GeneratedCharacter character, IroncladCardRecipe recipe)
    {
        var operations = BuildOperations(recipe);
        return OperationRuntimeSpecCompiler.Attach(new GeneratedCard(recipe.Cost, recipe.Type, recipe.Target,
            recipe.OriginalRarity, string.Empty, recipe.Tags, operations, Character: character,
            StarCost: recipe.StarCost, HasStarCostX: recipe.HasStarCostX,
            CustomKeywords: recipe.CustomKeywords));
    }

    private static void AuditCard(GeneratedCharacter character, IroncladCardRecipe recipe,
        ICollection<CardRow> cards, ICollection<ComponentOccurrence> occurrences,
        ICollection<PackageOccurrence> packages, ICollection<OperationValuationRow>? operationValuations = null)
    {
        var operations = BuildOperations(recipe);
        var resolvedOperations = VariableXCardBalance.MaterializeOperations(operations, 1);
        var valuationEnergyCost = ValuationEnergyCost(recipe, resolvedOperations);
        var effectiveCost = EffectiveCost(recipe, resolvedOperations);
        var hasPrintedResourceCost = valuationEnergyCost != 0 || recipe.StarCost > 0
            || recipe.Cost < 0 || recipe.HasStarCostX;
        var full = Evaluate(resolvedOperations, recipe.Tags, hasPrintedResourceCost, recipe.Type,
            recipe.OriginalRarity, character);
        var target = GeneratorTargetCenter(recipe, resolvedOperations, effectiveCost);
        var ratio = full.Normalized / Math.Max(1d, target);
        var cardKey = $"{character}:{recipe.Id}";
        var groups = BuildValuationUnits(recipe, resolvedOperations);

        if (operationValuations is not null)
        {
            for (var index = 0; index < resolvedOperations.Length; index++)
            {
                var operation = resolvedOperations[index];
                var atomic = EffectBalanceModel.EstimatedEffectValue(operation);
                var contextual = EffectBalanceModel.EstimatedContextualOperationValueForAudit(operation, index,
                    resolvedOperations, hasPrintedResourceCost, recipe.Type, recipe.Tags);
                var linked = EffectBalanceModel.LinkedResolutionMultiplierForAudit(operation, index,
                    resolvedOperations);
                var contextualMultiplier = atomic == 0 ? (double?)null : contextual / atomic;
                var remainingMultiplier = atomic == 0 || linked == 0
                    ? (double?)null
                    : contextual / atomic / linked;
                operationValuations.Add(new OperationValuationRow(character, cardKey, index,
                    recipe.TriggerOwners[index], operation.Template, operation.Scope, atomic, linked,
                    remainingMultiplier, contextualMultiplier, contextual,
                    operation.Template == "CL:IncreaseRollingDamage"
                        ? EffectBalanceModel.RollingGrowthQuadraticCoefficient
                        : null));
            }
            foreach (var tag in recipe.Tags.Distinct())
            {
                var keywordValue = tag switch
                {
                    CardTag.Retain => 400d,
                    CardTag.Innate => 200d,
                    _ => 0d
                };
                operationValuations.Add(new OperationValuationRow(character, cardKey, -1, -1,
                    $"KEYWORD:{tag}", OperationScope.Independent, keywordValue, 1d, 1d,
                    keywordValue == 0d ? null : 1d, keywordValue, null));
            }
        }

        foreach (var group in groups)
        {
            var retained = RemoveOperations(resolvedOperations, group.Indices);
            var without = Evaluate(retained, recipe.Tags, hasPrintedResourceCost, recipe.Type,
                recipe.OriginalRarity, character);
            var currentMarginal = full.Normalized - without.Normalized;
            var impliedMarginal = target - without.Normalized;
            var marginalRatio = ComparableRatio(currentMarginal, impliedMarginal);
            var packageKey = PackageKey(group.Indices.Select(index => resolvedOperations[index]));
            packages.Add(new PackageOccurrence(character, cardKey, recipe.OriginalRarity, effectiveCost,
                packageKey, group.Indices.Count, full.Normalized, without.Normalized, target,
                currentMarginal, impliedMarginal, marginalRatio));
            foreach (var index in group.Indices)
            {
                var operation = resolvedOperations[index];
                occurrences.Add(new ComponentOccurrence(character, cardKey, recipe.OriginalRarity,
                    effectiveCost, operation.Template,
                    NumericTextSchema.Family(operation.Template), operation.Scope,
                    EffectBalanceModel.EstimatedEffectValue(operation),
                    operation.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger
                        ? EffectBalanceModel.RelativeTriggerFrequency(operation)
                        : 1d,
                    group.Indices.Count == 1, packageKey, full.Normalized, without.Normalized, target,
                    currentMarginal, impliedMarginal, marginalRatio,
                    CardEffectRules.IsNegativeEffect(operation)));
            }
        }

        foreach (var tag in recipe.Tags.Distinct())
        {
            var reducedTags = recipe.Tags.Where(candidate => candidate != tag).ToArray();
            var without = Evaluate(resolvedOperations, reducedTags, hasPrintedResourceCost, recipe.Type,
                recipe.OriginalRarity, character);
            var currentMarginal = full.Normalized - without.Normalized;
            var impliedMarginal = target - without.Normalized;
            var marginalRatio = ComparableRatio(currentMarginal, impliedMarginal);
            var key = $"KEYWORD:{tag}";
            packages.Add(new PackageOccurrence(character, cardKey, recipe.OriginalRarity, effectiveCost,
                key, 1, full.Normalized, without.Normalized, target, currentMarginal,
                impliedMarginal, marginalRatio));
            occurrences.Add(new ComponentOccurrence(character, cardKey, recipe.OriginalRarity, effectiveCost,
                key, key, OperationScope.Independent,
                tag switch
                {
                    CardTag.Retain => 400d,
                    CardTag.Innate => 200d,
                    _ => 0d
                }, 1d, true, key, full.Normalized, without.Normalized, target,
                currentMarginal, impliedMarginal, marginalRatio,
                tag is CardTag.Exhaust or CardTag.Ethereal));
        }

        cards.Add(new CardRow(character, recipe.Id, recipe.ChineseTitle, recipe.EnglishTitle,
            recipe.Type, recipe.OriginalRarity, recipe.Cost, recipe.StarCost, recipe.Cost < 0,
            recipe.HasStarCostX, effectiveCost, resolvedOperations.Length, recipe.Tags.Count,
            EffectBalanceModel.PositiveRewardFieldCount(resolvedOperations, hasPrintedResourceCost,
                recipe.Type, recipe.Tags), full.Positive, full.DownsidePercent, full.PowerFactor,
            full.LinearDownsideValue, full.Normalized, target, ratio, full.Normalized - target, target, ratio));
    }

    private static ComponentOccurrence Calibrate(ComponentOccurrence item, double cohortFactor)
    {
        var implied = item.ConfiguredTarget * cohortFactor - item.WithoutNormalized;
        return item with { ImpliedMarginal = implied, Ratio = ComparableRatio(item.CurrentMarginal, implied) };
    }

    private static PackageOccurrence Calibrate(PackageOccurrence item, double cohortFactor)
    {
        var implied = item.ConfiguredTarget * cohortFactor - item.WithoutNormalized;
        return item with { ImpliedMarginal = implied, Ratio = ComparableRatio(item.CurrentMarginal, implied) };
    }

    private static GeneratorOperation[] BuildOperations(IroncladCardRecipe recipe) =>
        recipe.Atoms.Select((atom, index) => new GeneratorOperation(atom.Template, atom.Scope,
            string.Empty,
            recipe.TriggerOwners[index] < 0
                ? new Dictionary<string, int>()
                : new Dictionary<string, int> { ["triggerIndex"] = recipe.TriggerOwners[index] },
            atom.CardReference == CardReferenceRequirement.ThisCard ? "thisCard" : null,
            atom.RequiresSingleTarget, RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom))).ToArray();

    private static double EffectiveCost(IroncladCardRecipe recipe,
        IReadOnlyList<GeneratorOperation> resolvedOperations)
    {
        // X is audited at the smallest meaningful payment. Energy-X contributes one Energy; Star-X contributes
        // one Star through the shared 2 Stars = 1 Energy conversion. This convention is recorded in metadata.
        var energy = ValuationEnergyCost(recipe, resolvedOperations);
        var stars = recipe.HasStarCostX ? 1 : Math.Max(0, recipe.StarCost);
        var effective = ResourceEconomyModel.BudgetEffectiveCost(energy, stars, false, false,
            resolvedOperations);
        return double.IsFinite(effective) ? Math.Max(0d, effective) : 1d;
    }

    private static double GeneratorTargetCenter(IroncladCardRecipe recipe,
        IReadOnlyList<GeneratorOperation> operations, double effectiveCost)
    {
        var target = ComponentAssemblyGenerator.CalibratedWholeCardCenter(recipe.OriginalRarity, effectiveCost);
        // Production prices a free discard-copy against the mean of the paid and free envelopes because the
        // operation schedules one later zero-cost use of the complete payload. The audit must use that same target
        // instead of making Adaptive Strike look weak merely by omitting the generator's explicit copy rule.
        return operations.Any(CardEffectRules.IsZeroCostCopyThisCardToDiscard)
            ? CopyThisCardValuation.BlendWithZeroCostEnvelope(target,
                ComponentAssemblyGenerator.CalibratedWholeCardCenter(recipe.OriginalRarity, 0d))
            : target;
    }

    private static int ValuationEnergyCost(IroncladCardRecipe recipe,
        IReadOnlyList<GeneratorOperation> resolvedOperations)
    {
        if (recipe.Tags.Contains(CardTag.Sly))
            return SlyKeywordTuning.ValidationTemplateCost(recipe.Cost, recipe.StarCost,
                recipe.HasStarCostX, recipe.OriginalRarity, recipe.Type, recipe.Tags, resolvedOperations);
        return recipe.Cost < 0 ? 1 : Math.Clamp(recipe.Cost, 0, 4);
    }

    private static Evaluation Evaluate(IReadOnlyList<GeneratorOperation> operations,
        IReadOnlyCollection<CardTag> tags, bool hasPrintedResourceCost, GeneratedCardType type,
        GeneratedRarity rarity, GeneratedCharacter character)
    {
        var positive = EffectBalanceModel.EstimatedPositiveCardValue(operations,
            hasPrintedResourceCost, type, tags);
        var downside = CardEffectRules.NegativeEffectCompensationPercent(operations, tags,
            hasPrintedResourceCost, type, character);
        var linearDownside = NegativeEffectTuning.TotalLinearCompensationValue(operations, rarity);
        var power = ComponentAssemblyGenerator.PowerOneShotBudgetFactor(operations, type);
        var normalized = EffectBalanceModel.EstimatedNetCardValue(operations,
                hasPrintedResourceCost, type, tags, rarity, character)
            / Math.Max(1d, power);
        return new Evaluation(positive, downside, power, linearDownside, normalized);
    }

    private static IReadOnlyList<ValuationUnit> BuildValuationUnits(IroncladCardRecipe recipe,
        IReadOnlyList<GeneratorOperation> operations)
    {
        var union = new DisjointSet(operations.Count);
        for (var index = 0; index < operations.Count; index++)
        {
            var owner = recipe.TriggerOwners[index];
            if (owner >= 0 && owner < operations.Count) union.Join(index, owner);
            if (index + 1 < operations.Count && CardEffectRules.IsDependencyPrefix(operations[index])
                && CardEffectRules.IsLegalDependencyPayoff(operations[index], operations[index + 1]))
                union.Join(index, index + 1);
            if (recipe.Atoms[index].CardReference is CardReferenceRequirement.HandCard
                    or CardReferenceRequirement.HandAttack)
            {
                var selector = Enumerable.Range(0, index).Reverse().FirstOrDefault(candidate =>
                    operations[candidate].Template.StartsWith("N_SELECT_", StringComparison.Ordinal), -1);
                if (selector >= 0) union.Join(index, selector);
            }
        }
        return Enumerable.Range(0, operations.Count).GroupBy(union.Root)
            .Select(group => new ValuationUnit(group.Order().ToArray())).ToArray();
    }

    private static GeneratorOperation[] RemoveOperations(IReadOnlyList<GeneratorOperation> operations,
        IReadOnlyCollection<int> removed)
    {
        var removedSet = removed.ToHashSet();
        var retainedIndices = Enumerable.Range(0, operations.Count).Where(index => !removedSet.Contains(index))
            .ToArray();
        var remap = retainedIndices.Select((oldIndex, newIndex) => (oldIndex, newIndex))
            .ToDictionary(item => item.oldIndex, item => item.newIndex);
        return retainedIndices.Select(oldIndex =>
        {
            var operation = operations[oldIndex];
            if (!operation.Parameters.TryGetValue("triggerIndex", out var oldTrigger)) return operation;
            var parameters = new Dictionary<string, int>(operation.Parameters);
            if (remap.TryGetValue(oldTrigger, out var newTrigger)) parameters["triggerIndex"] = newTrigger;
            else parameters.Remove("triggerIndex");
            return operation with { Parameters = parameters };
        }).ToArray();
    }

    private static double? ComparableRatio(double current, double implied)
    {
        if (Math.Abs(current) < 25d || Math.Sign(current) != Math.Sign(implied)) return null;
        var ratio = implied / current;
        return double.IsFinite(ratio) && ratio > 0d ? ratio : null;
    }

    private static string PackageKey(IEnumerable<GeneratorOperation> operations) =>
        string.Join(" -> ", operations.Select(operation => operation.Template));

    private static IReadOnlyList<ComponentRow> BuildComponentRows(IEnumerable<ComponentOccurrence> source) =>
        source.GroupBy(item => (item.Template, item.Family, item.Scope))
            .Select(group =>
            {
                var rows = group.ToArray();
                var ratios = rows.Where(row => row.Ratio is > 0d).Select(row => row.Ratio!.Value).Order().ToArray();
                var standalone = rows.Where(row => row.Standalone && row.Ratio is > 0d).ToArray();
                var recommended = standalone.Length >= 2
                    ? standalone.Select(row => row.Ratio!.Value).Order().ToArray()
                    : ratios;
                return new ComponentRow(group.Key.Template, group.Key.Family, group.Key.Scope,
                    string.Join(',', rows.Select(row => row.Character).Distinct().Order()), rows.Length,
                    rows.Select(row => row.CardKey).Distinct().Count(), standalone.Length,
                    rows.Count(row => !row.Standalone), Median(rows.Select(row => row.RawValue)),
                    Median(rows.Select(row => row.TriggerFrequency)), Median(rows.Select(row => row.CurrentMarginal)),
                    Median(rows.Select(row => row.ImpliedMarginal)), MedianNullable(recommended),
                    PercentileNullable(recommended, 0.25d), PercentileNullable(recommended, 0.75d),
                    Verdict(recommended, standalone.Length), rows.Any(row => row.Negative));
            }).OrderBy(row => row.Family, StringComparer.Ordinal).ThenBy(row => row.Template, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<PackageRow> BuildPackageRows(IEnumerable<PackageOccurrence> source) =>
        source.GroupBy(item => item.PackageKey, StringComparer.Ordinal).Select(group =>
        {
            var rows = group.ToArray();
            var ratios = rows.Where(row => row.Ratio is > 0d).Select(row => row.Ratio!.Value).Order().ToArray();
            return new PackageRow(group.Key,
                string.Join(',', rows.Select(row => row.Character).Distinct().Order()), rows.Length,
                rows.Select(row => row.CardKey).Distinct().Count(), rows.Max(row => row.MemberCount),
                Median(rows.Select(row => row.CurrentMarginal)), Median(rows.Select(row => row.ImpliedMarginal)),
                MedianNullable(ratios), PercentileNullable(ratios, 0.25d),
                PercentileNullable(ratios, 0.75d), Verdict(ratios, rows.Length));
        }).OrderBy(row => row.PackageKey, StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<CohortRow> BuildCohortRows(IReadOnlyCollection<CardRow> cards)
    {
        var rows = new List<CohortRow>();
        rows.AddRange(Cohorts(cards.GroupBy(card => (Scope: "ALL", Character: "ALL",
            card.Rarity, CostTier: CostTier(card.EffectiveCost)))));
        rows.AddRange(Cohorts(cards.GroupBy(card => (Scope: "CHARACTER", Character: card.Character.ToString(),
            card.Rarity, CostTier: CostTier(card.EffectiveCost)))));
        return rows.OrderBy(row => row.Scope).ThenBy(row => row.Character)
            .ThenBy(row => row.Rarity).ThenBy(row => row.CostTier).ToArray();
    }

    private static IEnumerable<CohortRow> Cohorts(IEnumerable<IGrouping<(string Scope, string Character,
        GeneratedRarity Rarity, string CostTier), CardRow>> groups) => groups.Select(group =>
    {
        var ratios = group.Select(card => card.Ratio).Order().ToArray();
        return new CohortRow(group.Key.Scope, group.Key.Character, group.Key.Rarity, group.Key.CostTier,
            ratios.Length, Median(group.Select(card => card.NormalizedValue)),
            Median(group.Select(card => card.TargetCenter)), Median(ratios),
            Percentile(ratios, 0.25d), Percentile(ratios, 0.75d));
    });

    private static string CostTier(double cost) => cost switch
    {
        < 0.25d => "0",
        < 0.75d => "0.5",
        < 1.25d => "1",
        < 1.75d => "1.5",
        < 2.25d => "2",
        < 2.75d => "2.5",
        < 3.25d => "3",
        < 3.75d => "3.5",
        _ => "4+"
    };

    private static string Verdict(IReadOnlyList<double> ratios, int standaloneSamples)
    {
        if (ratios.Count < 2) return "insufficient";
        var median = Percentile(ratios, 0.5d);
        var confidence = standaloneSamples >= 3 || ratios.Count >= 6 ? "review" : "low-confidence";
        if (median < 0.67d) return $"{confidence}:current-high";
        if (median > 1.50d) return $"{confidence}:current-low";
        return "aligned";
    }

    private static double Median(IEnumerable<double> values) => Percentile(values.Order().ToArray(), 0.5d);
    private static double? MedianNullable(IReadOnlyList<double> values) =>
        values.Count == 0 ? null : Percentile(values, 0.5d);
    private static double? PercentileNullable(IReadOnlyList<double> values, double percentile) =>
        values.Count == 0 ? null : Percentile(values, percentile);
    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0d;
        var position = Math.Clamp(percentile, 0d, 1d) * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper ? sorted[lower]
            : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static void WriteCards(string path, IEnumerable<CardRow> rows)
    {
        var output = new StringBuilder("character\tcardId\tzhTitle\tenTitle\ttype\trarity\tenergyCost\tstarCost\tenergyX\tstarX\teffectiveCost\toperations\tkeywords\trewardFields\tpositiveValue\tdownsidePercent\tlinearDownsideValue\tpowerFactor\tnormalizedValue\tgeneratorTarget\tgeneratorRatio\tgeneratorResidual\tnativeCohortTarget\tnativeCohortRatio\n");
        foreach (var row in rows) output.AppendLine(string.Join('\t',
            row.Character, Tsv(row.CardId), Tsv(row.ChineseTitle), Tsv(row.EnglishTitle), row.Type, row.Rarity,
            row.EnergyCost, row.StarCost, row.HasEnergyX, row.HasStarX, F(row.EffectiveCost), row.Operations,
            row.Keywords, row.RewardFields, F(row.PositiveValue), row.DownsidePercent,
            F(row.LinearDownsideValue), F(row.PowerFactor),
            F(row.NormalizedValue), F(row.TargetCenter), F(row.Ratio), F(row.Residual),
            F(row.NativeCohortTarget), F(row.NativeCohortRatio)));
        File.WriteAllText(path, output.ToString(), Utf8());
    }

    private static void WriteComponents(string path, IEnumerable<ComponentRow> rows)
    {
        var output = new StringBuilder("template\tfamily\tscope\tcharacters\toccurrences\tcards\tstandaloneSamples\tpackageSamples\tcurrentRawMedian\ttriggerFrequencyMedian\tcurrentMarginalMedian\timpliedMarginalMedian\trecommendedScaleMedian\trecommendedScaleP25\trecommendedScaleP75\tverdict\tnegative\n");
        foreach (var row in rows) output.AppendLine(string.Join('\t', Tsv(row.Template), Tsv(row.Family),
            row.Scope, row.Characters, row.Occurrences, row.Cards, row.StandaloneSamples, row.PackageSamples,
            F(row.CurrentRawMedian), F(row.TriggerFrequencyMedian), F(row.CurrentMarginalMedian),
            F(row.ImpliedMarginalMedian), F(row.RecommendedScaleMedian), F(row.RecommendedScaleP25),
            F(row.RecommendedScaleP75), row.Verdict, row.Negative));
        File.WriteAllText(path, output.ToString(), Utf8());
    }

    private static void WriteComponentOccurrences(string path, IEnumerable<ComponentOccurrence> rows)
    {
        var output = new StringBuilder("character\tcard\trarity\teffectiveCost\ttemplate\tfamily\tscope\tcurrentRawValue\ttriggerFrequency\tstandalone\tpackage\tfullNormalized\twithoutNormalized\tgeneratorTarget\tcurrentMarginal\timpliedMarginal\trecommendedScale\tnegative\n");
        foreach (var row in rows.OrderBy(row => row.Character).ThenBy(row => row.CardKey, StringComparer.Ordinal)
                     .ThenBy(row => row.Template, StringComparer.Ordinal))
            output.AppendLine(string.Join('\t', row.Character, Tsv(row.CardKey), row.Rarity,
                F(row.EffectiveCost), Tsv(row.Template), Tsv(row.Family), row.Scope, F(row.RawValue),
                F(row.TriggerFrequency), row.Standalone, Tsv(row.PackageKey), F(row.FullNormalized),
                F(row.WithoutNormalized), F(row.ConfiguredTarget), F(row.CurrentMarginal),
                F(row.ImpliedMarginal), F(row.Ratio), row.Negative));
        File.WriteAllText(path, output.ToString(), Utf8());
    }

    private static void WriteOperationValuations(string path, IEnumerable<OperationValuationRow> rows)
    {
        var output = new StringBuilder("character\tcard\toperationIndex\ttriggerOwner\ttemplate\tscope\tatomicValue\tlinkedResolutionMultiplier\totherContextMultiplier\ttotalContextMultiplier\tcontextualValue\trollingQuadraticCoefficient\n");
        foreach (var row in rows.OrderBy(row => row.Character).ThenBy(row => row.CardKey, StringComparer.Ordinal)
                     .ThenBy(row => row.OperationIndex))
            output.AppendLine(string.Join('\t', row.Character, Tsv(row.CardKey), row.OperationIndex,
                row.TriggerOwner, Tsv(row.Template), row.Scope, F(row.AtomicValue),
                F(row.LinkedResolutionMultiplier), F(row.OtherContextMultiplier),
                F(row.TotalContextMultiplier), F(row.ContextualValue), F(row.RollingQuadraticCoefficient)));
        File.WriteAllText(path, output.ToString(), Utf8());
    }

    private static void WritePackageOccurrences(string path, IEnumerable<PackageOccurrence> rows)
    {
        var output = new StringBuilder("character\tcard\trarity\teffectiveCost\tpackage\tmembers\tfullNormalized\twithoutNormalized\tgeneratorTarget\tcurrentMarginal\timpliedMarginal\trecommendedScale\n");
        foreach (var row in rows.OrderBy(row => row.Character).ThenBy(row => row.CardKey, StringComparer.Ordinal)
                     .ThenBy(row => row.PackageKey, StringComparer.Ordinal))
            output.AppendLine(string.Join('\t', row.Character, Tsv(row.CardKey), row.Rarity,
                F(row.EffectiveCost), Tsv(row.PackageKey), row.MemberCount, F(row.FullNormalized),
                F(row.WithoutNormalized), F(row.ConfiguredTarget), F(row.CurrentMarginal),
                F(row.ImpliedMarginal), F(row.Ratio)));
        File.WriteAllText(path, output.ToString(), Utf8());
    }

    private static void WritePackages(string path, IEnumerable<PackageRow> rows)
    {
        var output = new StringBuilder("package\tcharacters\toccurrences\tcards\tmembers\tcurrentMarginalMedian\timpliedMarginalMedian\trecommendedScaleMedian\trecommendedScaleP25\trecommendedScaleP75\tverdict\n");
        foreach (var row in rows) output.AppendLine(string.Join('\t', Tsv(row.PackageKey), row.Characters,
            row.Occurrences, row.Cards, row.Members, F(row.CurrentMarginalMedian), F(row.ImpliedMarginalMedian),
            F(row.RecommendedScaleMedian), F(row.RecommendedScaleP25), F(row.RecommendedScaleP75), row.Verdict));
        File.WriteAllText(path, output.ToString(), Utf8());
    }

    private static void WriteCohorts(string path, IEnumerable<CohortRow> rows)
    {
        var output = new StringBuilder("scope\tcharacter\trarity\teffectiveCostTier\tcards\tnormalizedMedian\ttargetMedian\tratioMedian\tratioP25\tratioP75\n");
        foreach (var row in rows) output.AppendLine(string.Join('\t', row.Scope, row.Character, row.Rarity,
            row.CostTier, row.Cards, F(row.NormalizedMedian), F(row.TargetMedian), F(row.RatioMedian),
            F(row.RatioP25), F(row.RatioP75)));
        File.WriteAllText(path, output.ToString(), Utf8());
    }

    private static void WriteXCheckpoints(string path, IEnumerable<XCheckpointRow> rows)
    {
        var output = new StringBuilder("character\tcardId\trarity\tenergyCost\tstarCost\tenergyX\tstarX\tresolvedX\teffectiveCost\tnetValue\tgenerationMinimum\tgenerationMaximum\tordinaryCenterRatio\twithinGeneratedEnvelope\n");
        foreach (var row in rows.OrderBy(row => row.Character).ThenBy(row => row.CardId, StringComparer.Ordinal)
                     .ThenBy(row => row.ResolvedX))
            output.AppendLine(string.Join('\t', row.Character, Tsv(row.CardId), row.Rarity, row.EnergyCost,
                row.StarCost, row.HasEnergyX, row.HasStarX, row.ResolvedX, F(row.EffectiveCost), F(row.NetValue),
                F(row.Minimum), F(row.Maximum), F(row.OrdinaryCenterRatio), row.WithinGeneratedEnvelope));
        File.WriteAllText(path, output.ToString(), Utf8());
    }

    private static void WriteMetadata(string path, IReadOnlyCollection<CardRow> cards,
        IReadOnlyCollection<ComponentOccurrence> components, IReadOnlyCollection<PackageOccurrence> packages,
        IReadOnlyCollection<XCheckpointRow> xCheckpoints)
    {
        var metadata = new
        {
            method = MethodVersion,
            generatedUtc = DateTimeOffset.UtcNow,
            referenceMode = "balanced",
            xConvention = "Residual/component fitting resolves X at 1. A separate core-generator report evaluates every native Energy-X and Star-X card at X=1 and X=3.",
            generatorTarget = "ComponentAssemblyGenerator.CalibratedWholeCardCenter(rarity, effectiveCost)",
            componentInferenceTarget = "configured balanced-mode budget center; native-cohort-normalized occurrences are emitted separately as auxiliary diagnostics",
            normalizedValue = "(sum(operation contextual values) + keyword values - linearDownsideValue) / downsideMultiplier / powerOneShotMultiplier",
            operationValue = "atomicValue * linkedResolutionMultiplier * otherContextMultiplier = contextualValue; totalContextMultiplier is contextualValue / atomicValue",
            marginal = "full normalized value minus dependency-safe leave-one-unit-out normalized value",
            impliedMarginal = "native cohort target minus leave-one-unit-out normalized value",
            limitations = new[]
            {
                "The configured curve and native cohort normalization are diagnostics, not ground truth.",
                "A linked trigger/payoff or selector/payoff chain is identifiable only as a package.",
                "Sparse and unique components remain low-confidence and require manual review."
            },
            cards = cards.Count,
            componentOccurrences = components.Count,
            packages = packages.Count,
            xCheckpointRows = xCheckpoints.Count,
            templates = components.Select(component => component.Template).Distinct(StringComparer.Ordinal).Count(),
            characters = cards.GroupBy(card => card.Character).ToDictionary(group => group.Key.ToString(), group => group.Count())
        };
        File.WriteAllText(path, JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }), Utf8());
    }

    private static void WriteSummary(string path, IReadOnlyCollection<CardRow> cards,
        IReadOnlyCollection<ComponentRow> components, IReadOnlyCollection<PackageRow> packages,
        IReadOnlyCollection<CohortRow> cohorts, IReadOnlyCollection<XCheckpointRow> xCheckpoints)
    {
        var output = new StringBuilder();
        output.AppendLine("# 原版全卡反向估值审计");
        output.AppendLine();
        output.AppendLine($"- 方法版本：`{MethodVersion}`");
        output.AppendLine($"- 覆盖：{cards.Count} 张原版卡、{components.Count} 个组件模板、{packages.Count} 种估值单元。");
        output.AppendLine("- 基准：始终使用数值平衡模式的费用×稀有度中心；组件残差按 X=1 归一，所有原版 X/X蓝星牌另在 X=1 与 X=3 两端审计。");
        output.AppendLine("- 主指标：完整组件估值经负面倍率、线性补偿、能力一次性折算和有效费用曲线归一化后，直接除以生成器的数值平衡预算中心。");
        output.AppendLine("- 辅助指标：相同稀有度×有效费用档的原版中位数仍写入 native-cohort 字段，但不再用于整卡越界判定或组件调参建议。");
        output.AppendLine("- 边际：把触发器、选择器、依赖前缀及其后续合并成合法估值单元后逐项移除，再比较整卡归一化价值变化。");
        output.AppendLine("- 解释：建议倍率 >1 表示当前组件价值可能低估，<1 表示可能高估。低样本及整卡代理效果必须人工复核，不应自动回写参数。");
        output.AppendLine($"- X 端点：{xCheckpoints.Count / 2} 张原版 X 牌均写入 `x_checkpoints.tsv`；`withinGeneratedEnvelope` 仅表示随机组卡是否会被接受，原版精确重建不受该列限制。");
        output.AppendLine();
        output.AppendLine("## 全局稀有度概览");
        output.AppendLine();
        output.AppendLine("| 稀有度 | 卡数 | 原版归一化/目标中位数 | P25-P75 |");
        output.AppendLine("|---|---:|---:|---:|");
        foreach (var group in cards.GroupBy(card => card.Rarity).OrderBy(group => group.Key))
        {
            var ratios = group.Select(card => card.Ratio).Order().ToArray();
            output.AppendLine($"| {group.Key} | {ratios.Length} | {Percentile(ratios, 0.5d):0.00} | "
                + $"{Percentile(ratios, 0.25d):0.00}-{Percentile(ratios, 0.75d):0.00} |");
        }
        output.AppendLine();
        output.AppendLine("## 优先人工复核的组件");
        output.AppendLine();
        output.AppendLine("| 组件 | 样本 | 独立样本 | 建议倍率 | 区间 | 判定 |");
        output.AppendLine("|---|---:|---:|---:|---:|---|");
        foreach (var row in components.Where(row => row.RecommendedScaleMedian is not null
                                                     && row.Verdict.StartsWith("review:", StringComparison.Ordinal))
                     .OrderByDescending(row => Math.Abs(Math.Log(row.RecommendedScaleMedian!.Value)))
                     .Take(30))
            output.AppendLine($"| `{row.Template}` | {row.Occurrences} | {row.StandaloneSamples} | "
                + $"{row.RecommendedScaleMedian:0.00} | {row.RecommendedScaleP25:0.00}-{row.RecommendedScaleP75:0.00} | {row.Verdict} |");
        output.AppendLine();
        var lowConfidenceCount = components.Count(row => row.Verdict == "insufficient"
            || row.Verdict.StartsWith("low-confidence:", StringComparison.Ordinal));
        output.AppendLine($"另有 {lowConfidenceCount} 个低样本组件仅保留在 `components.tsv`，不进入优先调整清单。");
        output.AppendLine();
        output.AppendLine("## 整卡离群值");
        output.AppendLine();
        output.AppendLine("| 角色 | 卡 | 稀有度 | 有效费用 | 模型/目标 | 残差 |");
        output.AppendLine("|---|---|---|---:|---:|---:|");
        foreach (var card in cards.OrderByDescending(card =>
                         Math.Abs(Math.Log(Math.Max(0.01d, card.Ratio))))
                     .Take(30))
            output.AppendLine($"| {card.Character} | {card.ChineseTitle} (`{card.CardId}`) | {card.Rarity} | "
                + $"{card.EffectiveCost:0.00} | {card.Ratio:0.00} | "
                + $"{card.Residual:0} |");
        output.AppendLine();
        output.AppendLine("详细数据见 `cards.tsv`、`x_checkpoints.tsv`、`operation_valuations.tsv`、`component_occurrences.tsv`、`components.tsv`、`trigger_package_occurrences.tsv`、`trigger_packages.tsv`、`cohorts.tsv` 与 `outliers.tsv`。`operation_valuations.tsv` 展开原子值、触发结算倍率、其余上下文系数与最终贡献；重复触发链仍应优先查看 package 报告，避免把相互依赖的前后半句误判成两个独立组件。");
        File.WriteAllText(path, output.ToString(), Utf8());
    }

    private static string Tsv(string? value) => (value ?? string.Empty).Replace('\t', ' ')
        .Replace('\r', ' ').Replace('\n', ' ');
    private static string F(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    private static string F(double? value) => value?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    private static UTF8Encoding Utf8() => new(encoderShouldEmitUTF8Identifier: false);

    private sealed class DisjointSet(int count)
    {
        private readonly int[] _parents = Enumerable.Range(0, count).ToArray();
        internal int Root(int item) => _parents[item] == item ? item : _parents[item] = Root(_parents[item]);
        internal void Join(int left, int right)
        {
            left = Root(left);
            right = Root(right);
            if (left != right) _parents[right] = left;
        }
    }

    private sealed record ValuationUnit(IReadOnlyList<int> Indices);
    private sealed record Evaluation(double Positive, int DownsidePercent, double PowerFactor,
        double LinearDownsideValue, double Normalized);
    private sealed record CardRow(GeneratedCharacter Character, string CardId, string ChineseTitle,
        string EnglishTitle, GeneratedCardType Type, GeneratedRarity Rarity, int EnergyCost, int StarCost,
        bool HasEnergyX, bool HasStarX, double EffectiveCost, int Operations, int Keywords, int RewardFields,
        double PositiveValue, int DownsidePercent, double PowerFactor, double LinearDownsideValue,
        double NormalizedValue,
        double TargetCenter, double Ratio, double Residual, double NativeCohortTarget,
        double NativeCohortRatio);
    private sealed record ComponentOccurrence(GeneratedCharacter Character, string CardKey,
        GeneratedRarity Rarity, double EffectiveCost, string Template,
        string Family, OperationScope Scope, double RawValue, double TriggerFrequency, bool Standalone,
        string PackageKey, double FullNormalized, double WithoutNormalized, double ConfiguredTarget,
        double CurrentMarginal, double ImpliedMarginal, double? Ratio, bool Negative);
    private sealed record OperationValuationRow(GeneratedCharacter Character, string CardKey,
        int OperationIndex, int TriggerOwner, string Template, OperationScope Scope, double AtomicValue,
        double LinkedResolutionMultiplier, double? OtherContextMultiplier, double? TotalContextMultiplier,
        double ContextualValue, double? RollingQuadraticCoefficient);
    private sealed record PackageOccurrence(GeneratedCharacter Character, string CardKey,
        GeneratedRarity Rarity, double EffectiveCost, string PackageKey, int MemberCount,
        double FullNormalized, double WithoutNormalized, double ConfiguredTarget,
        double CurrentMarginal, double ImpliedMarginal, double? Ratio);
    private sealed record ComponentRow(string Template, string Family, OperationScope Scope, string Characters,
        int Occurrences, int Cards, int StandaloneSamples, int PackageSamples, double CurrentRawMedian,
        double TriggerFrequencyMedian, double CurrentMarginalMedian, double ImpliedMarginalMedian,
        double? RecommendedScaleMedian, double? RecommendedScaleP25, double? RecommendedScaleP75,
        string Verdict, bool Negative);
    private sealed record PackageRow(string PackageKey, string Characters, int Occurrences, int Cards, int Members,
        double CurrentMarginalMedian, double ImpliedMarginalMedian, double? RecommendedScaleMedian,
        double? RecommendedScaleP25, double? RecommendedScaleP75, string Verdict);
    private sealed record CohortRow(string Scope, string Character, GeneratedRarity Rarity, string CostTier,
        int Cards, double NormalizedMedian, double TargetMedian, double RatioMedian, double RatioP25,
        double RatioP75);
    private sealed record XCheckpointRow(GeneratedCharacter Character, string CardId, GeneratedRarity Rarity,
        int EnergyCost, int StarCost, bool HasEnergyX, bool HasStarX, int ResolvedX, double EffectiveCost,
        double NetValue, double Minimum, double Maximum, double OrdinaryCenterRatio,
        bool WithinGeneratedEnvelope);
}

internal sealed record NativeValuationAuditResult(string OutputDirectory, int Cards,
    int ComponentOccurrences, int ComponentTemplates, int PackageTemplates);
