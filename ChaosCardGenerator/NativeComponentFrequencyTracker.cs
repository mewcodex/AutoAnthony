namespace ChaosCardGenerator;

/// <summary>
/// Soft closed-loop correction for component occurrence rates. The static source prior selects a family from the
/// matching rarity/type/target context; this tracker then corrects the acceptance bias introduced by legality,
/// dependency completion and whole-card validation. It observes finalized cards only, never speculative attempts.
/// Normal generation owns one tracker per character/colorless pool. Ultimate Chaos owns one tracker over its
/// merged catalog, giving every generated character the same combined frequency profile.
/// </summary>
public sealed class NativeComponentFrequencyTracker : IComponentOccurrencePolicy
{
    private const double PriorCards = 0.5d;
    private readonly GeneratedCharacter _catalogCharacter;
    private int _observedPoolCards;
    private readonly Dictionary<string, int> _observedPoolFamilies = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dependencyBoundFamilies;
    private readonly HashSet<string> _dependencyParentFamilies;
    private readonly HashSet<string> _ordinaryPoolFeedbackFamilies;
    private readonly IReadOnlyDictionary<string, double> _poolTargetRates;
    private readonly IReadOnlyDictionary<(string Parent, string Child), double> _dependencyPayoffRates;
    private readonly IReadOnlyDictionary<string, ComponentAtom[]> _nativeModifierChildren;
    private readonly HashSet<string> _nativeSelfReplayParents;
    private readonly bool _unifiedChaos;
    private readonly IReadOnlyDictionary<GeneratedCardType, int> _sourceCardsByType;
    private readonly IReadOnlyDictionary<(GeneratedRarity Rarity, GeneratedCardType Type), int>
        _sourceCardsByRarityType;
    private readonly IReadOnlyDictionary<GeneratedRarity, int> _sourceCardsByRarity;
    private readonly IReadOnlyDictionary<(GeneratedRarity Rarity, GeneratedCardType Type,
        NativeComponentRole Role, string Family), int> _sourceOccurrencesByRarityTypeRole;
    private readonly IReadOnlyDictionary<(GeneratedRarity Rarity, NativeComponentRole Role, string Family), int>
        _sourceOccurrencesByRarityRole;
    private readonly IReadOnlyDictionary<(NativeComponentRole Role, string Family), int> _sourceOccurrencesByRole;
    private readonly IReadOnlyDictionary<string, int> _sourceOccurrences;
    private readonly IReadOnlyDictionary<string, ComponentAtom[]> _atomsByFamily;
    private readonly IReadOnlyDictionary<string, double> _targetMultipliers;
    private readonly IReadOnlyDictionary<(GeneratedRarity Rarity, string Family), double> _rarityTargetMultipliers;
    private readonly IReadOnlyDictionary<string, int> _recoveryWeights;
    private readonly HashSet<string> _recoveredIndependentDownsides;
    private sealed record RecoveryCompanion(string RootTemplate, string RootVariant,
        string ParentTemplate, string ParentVariant, string ChildFamily, string ChildSchema);
    private readonly RecoveryCompanion[] _recoveryCompanions;
    private readonly Dictionary<(GeneratedRarity Rarity, GeneratedCardType Type, NativeComponentRole Role,
        string Family), double> _targetRates = new();
    private readonly IReadOnlyDictionary<string, int> _schemaCounts;
    private readonly IReadOnlyDictionary<(GeneratedRarity Rarity, string Schema), int> _raritySchemaCounts;
    private readonly IReadOnlyDictionary<(GeneratedRarity Rarity, GeneratedCardType Type, TargetMode Target,
        string Schema), int> _shellSchemaCounts;
    private readonly IReadOnlyDictionary<(NativeComponentRole Role, string Schema), int> _roleSchemaCounts;
    private readonly IReadOnlyDictionary<(NativeComponentRole Role, GeneratedRarity Rarity, string Schema), int>
        _roleRaritySchemaCounts;
    private readonly IReadOnlyDictionary<(NativeComponentRole Role, GeneratedRarity Rarity, GeneratedCardType Type,
        TargetMode Target, string Schema), int> _roleShellSchemaCounts;
    private readonly Dictionary<(GeneratedRarity Rarity, GeneratedCardType Type), int>
        _observedCardsByRarityType = new();
    private readonly Dictionary<(GeneratedRarity Rarity, GeneratedCardType Type, NativeComponentRole Role,
        string Family), int> _observedOccurrencesByRarityTypeRole = new();

    public NativeComponentFrequencyTracker(IComponentCatalog catalog, bool unifiedChaos = false)
    {
        _catalogCharacter = catalog.Character;
        _unifiedChaos = unifiedChaos;
        _sourceCardsByType = catalog.Recipes.GroupBy(recipe => recipe.Type)
            .ToDictionary(group => group.Key, group => group.Count());
        _sourceCardsByRarityType = catalog.Recipes.GroupBy(recipe => (CharacterComponentCatalogs.ComponentRarity(recipe.OriginalRarity), recipe.Type))
            .ToDictionary(group => group.Key, group => group.Count());
        _sourceCardsByRarity = catalog.Recipes.GroupBy(recipe => CharacterComponentCatalogs.ComponentRarity(recipe.OriginalRarity))
            .ToDictionary(group => group.Key, group => group.Count());
        _sourceOccurrencesByRarityTypeRole = catalog.Recipes
            .SelectMany(recipe => recipe.Atoms.Select((atom, atomIndex) =>
                (CharacterComponentCatalogs.ComponentRarity(recipe.OriginalRarity), recipe.Type, Role: SourceRole(recipe, atomIndex), atom.FamilyKey)))
            .GroupBy(item => item)
            .ToDictionary(group => group.Key, group => group.Count());
        _sourceOccurrencesByRarityRole = catalog.Recipes
            .SelectMany(recipe => recipe.Atoms.Select((atom, atomIndex) =>
                (CharacterComponentCatalogs.ComponentRarity(recipe.OriginalRarity), Role: SourceRole(recipe, atomIndex), atom.FamilyKey)))
            .GroupBy(item => item)
            .ToDictionary(group => group.Key, group => group.Count());
        _sourceOccurrencesByRole = catalog.Recipes
            .SelectMany(recipe => recipe.Atoms.Select((atom, atomIndex) =>
                (Role: SourceRole(recipe, atomIndex), atom.FamilyKey)))
            .GroupBy(item => item)
            .ToDictionary(group => group.Key, group => group.Count());
        _sourceOccurrences = catalog.Recipes.SelectMany(recipe => recipe.Atoms)
            .GroupBy(atom => atom.FamilyKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        _atomsByFamily = catalog.Atoms.GroupBy(atom => atom.FamilyKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        _recoveryWeights = catalog.Atoms.DistinctBy(atom => atom.SchemaKey).ToDictionary(
            atom => atom.SchemaKey,
            atom => EffectSelectionTuning.LowOccurrenceRecoveryWeight(atom, _catalogCharacter, _unifiedChaos),
            StringComparer.Ordinal);
        _recoveredIndependentDownsides = _atomsByFamily.Where(pair => pair.Value.Any(atom =>
                _recoveryWeights.GetValueOrDefault(atom.SchemaKey, 100) > 100
                && CardEffectRules.IsNegativeEffect(atom)))
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        // Expensive native rules can depend on a separately decomposed payment. Prefer their native
        // continuation while recovering occurrence, rather than discounting the rule or inserting a whole card.
        _recoveryCompanions = catalog.Recipes
            .Where(recipe => recipe.Atoms.Count > 1 && recipe.Atoms[0].Scope == OperationScope.AbilityRule
                && _recoveryWeights.GetValueOrDefault(recipe.Atoms[0].SchemaKey, 100) > 100)
            .SelectMany(recipe => recipe.Atoms.Skip(1).Select((child, index) => new RecoveryCompanion(
                recipe.Atoms[0].Template, OperationRuntimeSpecCompiler.GetOrCompile(recipe.Atoms[0]).Variant,
                recipe.Atoms[index].Template, OperationRuntimeSpecCompiler.GetOrCompile(recipe.Atoms[index]).Variant,
                child.FamilyKey, child.SchemaKey))).Distinct().ToArray();
        // Ordinary effects target final pool incidence. Explicit scarcity, downside and resource-loop policies
        // are deliberately not counteracted by this controller.
        _ordinaryPoolFeedbackFamilies = _atomsByFamily.Where(pair => pair.Value.All(atom =>
                !ComponentAssemblyGenerator.IsExplicitRareOperation(atom)
                && !CardEffectRules.IsNegativeEffect(atom)
                && !CardEffectRules.IsEnergyGainOperation(atom)
                && !CardEffectRules.IsStarGainOperation(atom)
                && atom.Scope != OperationScope.ConditionalTrigger))
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        // Target multipliers are immutable for the lifetime of a pool. Computing their RuntimeSpec/category scans
        // once per family avoids repeating the same work twice for every candidate-family draw (source prior plus
        // closed-loop correction) across every speculative card assembly.
        _targetMultipliers = _atomsByFamily.ToDictionary(pair => pair.Key,
            pair => ComputeTargetMultiplier(pair.Value), StringComparer.Ordinal);
        _rarityTargetMultipliers = Enum.GetValues<GeneratedRarity>()
            .SelectMany(rarity => _atomsByFamily.Select(pair =>
                (Key: (rarity, pair.Key), Value: _targetMultipliers[pair.Key]
                    * DefenseGenerationTuning.OccurrenceMultiplier(pair.Value, rarity,
                        _catalogCharacter, _unifiedChaos))))
            .ToDictionary(item => item.Key, item => item.Value);
        _dependencyBoundFamilies = _atomsByFamily.Where(pair => pair.Value.Any(atom =>
                atom.Scope == OperationScope.Modifier
                    && (!CardEffectRules.IsDependencyPrefix(atom) || atom.Template == "D:ForEachOrb")
                || CardEffectRules.RequiresSpecificTriggerPayload(atom)
                || CardEffectRules.IsForEachExhaustTrigger(atom)
                || atom.Template is "C:ifLastDrawnSkill" or "I:AddExhaustedAttackDamage"
                    or "CL:GainBlockEqualDamage" or "NCR:ApplyDoomEqualDamage"
                    or "D:IncreaseThisCardBlockRun"
                    or "I:PlayThisCard" or "R:PlayThisCard"
                    or "C:forEachDiscarded"))
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        _dependencyParentFamilies = _atomsByFamily.Where(pair => pair.Value.Any(CardEffectRules.IsDependencyPrefix))
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        // A prerequisite already selected on this card changes the relevant sampling population. Counting its
        // native payoff against every card diluted rare dependent modifiers twice: once when choosing the parent,
        // and again when choosing its child. Use native adjacency only for dependency-bound children; ordinary
        // interchangeable rewards keep their broad prior. Finalized-pool feedback still limits total incidence.
        var prerequisitePairs = catalog.Recipes.SelectMany(recipe => recipe.Atoms.Skip(1)
            .Select((atom, index) => (Parent: recipe.Atoms[index], Child: atom))).ToArray();
        _nativeSelfReplayParents = prerequisitePairs
            .Where(pair => pair.Child.Template is "I:PlayThisCard" or "R:PlayThisCard")
            .Select(pair => pair.Parent.FamilyKey).ToHashSet(StringComparer.Ordinal);
        _dependencyPayoffRates = prerequisitePairs
            .GroupBy(pair => (Parent: pair.Parent.FamilyKey, Child: pair.Child.FamilyKey))
            .ToDictionary(group => group.Key, group => group.Count() /
                (double)Math.Max(1, _sourceOccurrences.GetValueOrDefault(group.Key.Parent)));
        _nativeModifierChildren = prerequisitePairs
            .Where(pair => CardEffectRules.IsDependencyPrefix(pair.Parent)
                && pair.Child.Scope == OperationScope.Modifier
                && !CardEffectRules.IsDependencyPrefix(pair.Child))
            .GroupBy(pair => pair.Parent.FamilyKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Child).ToArray(),
                StringComparer.Ordinal);
        var defensePoolCorrections = catalog.Recipes.SelectMany(recipe => recipe.Atoms.Select(atom =>
                (Family: atom.FamilyKey, Multiplier: DefenseGenerationTuning.OccurrenceMultiplier(
                    _atomsByFamily[atom.FamilyKey], CharacterComponentCatalogs.ComponentRarity(recipe.OriginalRarity), _catalogCharacter, _unifiedChaos))))
            .GroupBy(item => item.Family, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Average(item => item.Multiplier),
                StringComparer.Ordinal);
        _poolTargetRates = _sourceOccurrences.ToDictionary(pair => pair.Key,
            pair => pair.Value / (double)Math.Max(1, catalog.Recipes.Count)
                * _targetMultipliers.GetValueOrDefault(pair.Key, 1d)
                // Pool-wide feedback must observe the same rarity-weighted target as source selection,
                // otherwise it silently counteracts a correction applied only to Common cards.
                * defensePoolCorrections.GetValueOrDefault(pair.Key, 1d),
            StringComparer.Ordinal);
        var occurrences = catalog.Recipes.SelectMany(recipe => recipe.Atoms.Select((atom, atomIndex) =>
            (Recipe: recipe, Atom: atom, Role: SourceRole(recipe, atomIndex)))).ToArray();
        _schemaCounts = occurrences.GroupBy(item => item.Atom.SchemaKey)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        _raritySchemaCounts = occurrences.GroupBy(item => (CharacterComponentCatalogs.ComponentRarity(item.Recipe.OriginalRarity), item.Atom.SchemaKey))
            .ToDictionary(group => group.Key, group => group.Count());
        _shellSchemaCounts = occurrences.GroupBy(item =>
                (CharacterComponentCatalogs.ComponentRarity(item.Recipe.OriginalRarity), item.Recipe.Type, item.Recipe.Target, item.Atom.SchemaKey))
            .ToDictionary(group => group.Key, group => group.Count());
        _roleSchemaCounts = occurrences.GroupBy(item => (item.Role, item.Atom.SchemaKey))
            .ToDictionary(group => group.Key, group => group.Count());
        _roleRaritySchemaCounts = occurrences.GroupBy(item =>
                (item.Role, CharacterComponentCatalogs.ComponentRarity(item.Recipe.OriginalRarity), item.Atom.SchemaKey))
            .ToDictionary(group => group.Key, group => group.Count());
        _roleShellSchemaCounts = occurrences.GroupBy(item =>
                (item.Role, CharacterComponentCatalogs.ComponentRarity(item.Recipe.OriginalRarity), item.Recipe.Type, item.Recipe.Target,
                    item.Atom.SchemaKey))
            .ToDictionary(group => group.Key, group => group.Count());
    }

    /// <summary>
    /// Returns a percentage multiplier. Each rarity/type stream follows its native per-card occurrence rate.
    /// Squared proportional feedback closes the legality bias of small Power candidate sets. A 1% non-zero floor
    /// keeps every original/cross-role combination reachable without letting an exhausted family remain common.
    /// </summary>
    public int SelectionWeight(GeneratedRarity rarity, GeneratedCardType type, string family,
        IReadOnlyList<GeneratorOperation> currentCard)
    {
        rarity = CharacterComponentCatalogs.ComponentRarity(rarity);
        var role = ComponentAssemblyGenerator.SelectionRole(currentCard, type,
            _atomsByFamily.GetValueOrDefault(family) ?? []);
        var targetRate = TargetRate(rarity, type, role, family);
        var observedCards = _observedCardsByRarityType.GetValueOrDefault((rarity, type));
        var observedOccurrences = _observedOccurrencesByRarityTypeRole
            .GetValueOrDefault((rarity, type, role, family));
        var currentOccurrences = EnumerateRoleFamilies(type, currentCard)
            .Count(item => item.Role == role && item.Family == family);
        // Dependent families have far fewer legal selection opportunities than standalone actions. The old
        // universal 4x ceiling prevented the feedback controller from ever closing those persistent deficits.
        // Keep the native target unchanged and stop boosting as soon as finalized cards contain the family.
        var dependencyBound = _dependencyBoundFamilies.Contains(family);
        var weight = AuditSelectionWeight(targetRate, observedCards, observedOccurrences, currentOccurrences,
            dependencyBound || _dependencyParentFamilies.Contains(family) ? 10_000 : 400);
        if (!dependencyBound && !_dependencyParentFamilies.Contains(family)
            && !_ordinaryPoolFeedbackFamilies.Contains(family)) return weight;
        // A family selected in a different shell/role still counts toward the same pool incidence. Otherwise
        // stronger deficit correction can fill every rarity stream separately with the same native singleton.
        var poolRate = _poolTargetRates.GetValueOrDefault(family);
        var poolWeight = AuditSelectionWeight(poolRate, _observedPoolCards,
            _observedPoolFamilies.GetValueOrDefault(family),
            currentCard.Count(operation => NumericTextSchema.Family(operation.Template) == family),
            dependencyBound || _dependencyParentFamilies.Contains(family) ? 10_000 : 1_600);
        // A generic payoff may already have filled the parent's quota without ever offering its native modifier.
        // When a real host is now present, let the missing child reserve another soft parent opportunity.
        // Bound this completion correction; it ends as soon as the child's own accepted-pool target is met.
        if (_nativeModifierChildren.TryGetValue(family, out var children))
            foreach (var child in children)
                if (CardEffectRules.HasNumericModifierAnchor(child, currentCard))
                    poolWeight = Math.Max(poolWeight, AuditSelectionWeight(
                        _poolTargetRates.GetValueOrDefault(child.FamilyKey), _observedPoolCards,
                        _observedPoolFamilies.GetValueOrDefault(child.FamilyKey),
                        currentCard.Count(operation => NumericTextSchema.Family(operation.Template) == child.FamilyKey),
                        1_600));
        // SourcePriorWeight already supplies the native shell/role preference. Applying the sparse role
        // deficit a second time starves legal cross-shell completions (and a child's one available parent).
        // Dependencies therefore close the accepted-frequency loop at pool level, not independently per shell.
        return poolWeight;
    }

    /// <summary>
    /// Direct source prior for the currently requested rarity/type/role. The value is proportional to the number
    /// of native cards carrying the family, normalized by the relevant native card count. Ultimate Chaos passes a
    /// combined six-pool catalog, so this naturally becomes a pool-size-weighted average rather than a seventh set
    /// of hand-maintained character exceptions.
    /// </summary>
    public int SourcePriorWeight(GeneratedRarity rarity, GeneratedCardType type, string family,
        IReadOnlyList<GeneratorOperation> currentCard)
    {
        rarity = CharacterComponentCatalogs.ComponentRarity(rarity);
        var role = ComponentAssemblyGenerator.SelectionRole(currentCard, type,
            _atomsByFamily.GetValueOrDefault(family) ?? []);
        var rate = TargetRate(rarity, type, role, family);
        // Prefer placing native modifier prefixes after their host effect. Without this context, a perfectly
        // healthy parent rate can consist almost entirely of generic payoffs because its native modifier is
        // illegal. Keep unanchored recombination reachable, but favor the scarce legal completion opportunities.
        if (_nativeModifierChildren.TryGetValue(family, out var modifiers))
            rate *= modifiers.Any(modifier => CardEffectRules.HasNumericModifierAnchor(modifier, currentCard))
                ? 24d : 0.02d;
        if (_nativeSelfReplayParents.Contains(family))
            rate *= currentCard.Any(CardEffectRules.IsOrdinaryOnPlayEffect) ? 24d : 0.02d;
        if (_dependencyBoundFamilies.Contains(family)
            && currentCard.LastOrDefault() is { } parent
            && _dependencyPayoffRates.TryGetValue((NumericTextSchema.Family(parent.Template), family),
                out var conditionalRate))
            // Native-only payoff paths otherwise compete with the entire generic reward menu despite having
            // just one eligible parent opportunity. A 12:1 completion prior preserves recombination while avoiding
            // dilution by that larger menu. Pool-level feedback is still the cap on accepted occurrences.
            rate = Math.Max(rate, conditionalRate * 12d * _rarityTargetMultipliers.GetValueOrDefault((rarity, family), 1d));
        if (_recoveryCompanions.Any(link => link.ChildFamily == family && MatchesRecoveryCompanion(link, currentCard)))
            rate = Math.Max(rate, 16d);
        return Math.Max(1, (int)Math.Round(rate * 10_000d,
            MidpointRounding.AwayFromZero));
    }

    public int VariantWeight(ComponentAtom atom, GeneratedRarity rarity, GeneratedCardType type,
        TargetMode target, IReadOnlyList<GeneratorOperation> currentCard)
    {
        rarity = CharacterComponentCatalogs.ComponentRarity(rarity);
        var role = ComponentAssemblyGenerator.SelectionRole(currentCard, type, [atom]);
        var roleCount = _roleSchemaCounts.GetValueOrDefault((role, atom.SchemaKey));
        var hasNativeRole = roleCount > 0;
        var rarityCount = hasNativeRole
            ? _roleRaritySchemaCounts.GetValueOrDefault((role, rarity, atom.SchemaKey))
            : _raritySchemaCounts.GetValueOrDefault((rarity, atom.SchemaKey));
        var shellCount = hasNativeRole
            ? _roleShellSchemaCounts.GetValueOrDefault((role, rarity, type, target, atom.SchemaKey))
            : _shellSchemaCounts.GetValueOrDefault((rarity, type, target, atom.SchemaKey));
        var allCount = hasNativeRole ? roleCount : _schemaCounts.GetValueOrDefault(atom.SchemaKey);
        var weight = shellCount * 16 + rarityCount * 4 + allCount;
        var roleWeight = hasNativeRole
            ? weight
            : Math.Max(1, (int)Math.Min(int.MaxValue, PercentWeight.Apply(weight, 12)));
        var recoveryWeight = _recoveryWeights.GetValueOrDefault(atom.SchemaKey, 100);
        if (_recoveryCompanions.Any(link => link.ChildSchema == atom.SchemaKey && MatchesRecoveryCompanion(link, currentCard)))
            recoveryWeight = Math.Max(recoveryWeight, 10_000);
        return Math.Max(1, (int)Math.Min(int.MaxValue, PercentWeight.Apply(roleWeight, recoveryWeight)));
    }

    private static bool MatchesRecoveryCompanion(RecoveryCompanion link, IReadOnlyList<GeneratorOperation> current)
        => current.LastOrDefault() is { } parent && parent.Template == link.ParentTemplate
            && OperationRuntimeSpecCompiler.GetOrCompile(parent).Variant == link.ParentVariant
            && current.Any(operation => operation.Template == link.RootTemplate
                && OperationRuntimeSpecCompiler.GetOrCompile(operation).Variant == link.RootVariant);

    public void Observe(GeneratedCard card)
    {
        _observedPoolCards++;
        foreach (var operation in card.Operations)
        {
            var family = NumericTextSchema.Family(operation.Template);
            _observedPoolFamilies[family] = _observedPoolFamilies.GetValueOrDefault(family) + 1;
        }
        var cardKey = (CharacterComponentCatalogs.ComponentRarity(card.Rarity), card.Type);
        _observedCardsByRarityType[cardKey] = _observedCardsByRarityType.GetValueOrDefault(cardKey) + 1;
        foreach (var item in EnumerateRoleFamilies(card.Type, card.Operations))
        {
            if (!_sourceOccurrences.ContainsKey(item.Family)) continue;
            var key = (CharacterComponentCatalogs.ComponentRarity(card.Rarity), card.Type, item.Role, item.Family);
            _observedOccurrencesByRarityTypeRole[key] =
                _observedOccurrencesByRarityTypeRole.GetValueOrDefault(key) + 1;
        }
    }

    private static IEnumerable<(NativeComponentRole Role, string Family)> EnumerateRoleFamilies(
        GeneratedCardType type, IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Template.StartsWith("N_SELECT_", StringComparison.Ordinal)) continue;
            var role = NativeComponentRole.Unlinked;
            if (index > 0 && CardEffectRules.IsDependencyPrefix(operations[index - 1]))
            {
                role = NativeComponentRole.ConditionalPayoff;
            }
            else if (operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                && triggerIndex >= 0 && triggerIndex < operations.Count)
            {
                role = operations[triggerIndex].Scope == OperationScope.AbilityTrigger
                    ? NativeComponentRole.AbilityPayoff
                    : NativeComponentRole.ConditionalPayoff;
            }
            else if (type == GeneratedCardType.Power
                     && operation.Scope is not (OperationScope.AbilityTrigger or OperationScope.AbilityRule))
            {
                role = NativeComponentRole.AbilityFoundation;
            }
            yield return (role, NumericTextSchema.Family(operation.Template));
        }
    }

    private static NativeComponentRole SourceRole(IroncladCardRecipe recipe, int atomIndex)
    {
        var owner = atomIndex < recipe.TriggerOwners.Count ? recipe.TriggerOwners[atomIndex] : -1;
        // Offline modifier-prefix recipes predate explicit TriggerOwner metadata. Selection and finalized
        // operations link these children, so the native prior must count the same conditional role.
        if (atomIndex > 0 && CardEffectRules.IsDependencyPrefix(recipe.Atoms[atomIndex - 1]))
            return NativeComponentRole.ConditionalPayoff;
        if (owner < 0 || owner >= recipe.Atoms.Count)
        {
            var atom = recipe.Atoms[atomIndex];
            return recipe.Type == GeneratedCardType.Power
                   && atom.Scope is not (OperationScope.AbilityTrigger or OperationScope.AbilityRule)
                ? NativeComponentRole.AbilityFoundation
                : NativeComponentRole.Unlinked;
        }
        return recipe.Atoms[owner].Scope == OperationScope.AbilityTrigger
            ? NativeComponentRole.AbilityPayoff
            : NativeComponentRole.ConditionalPayoff;
    }

    private double ComputeTargetMultiplier(IReadOnlyList<ComponentAtom> atoms)
    {
        var multiplier = atoms.Any(ComponentPolicy.HasReplaceableSlot) ? 1.10d : 1d;
        multiplier *= EffectSelectionTuning.NativeCategoryWeight(atoms, _catalogCharacter, _unifiedChaos) / 100d;
        multiplier *= EffectSelectionTuning.NativeFinalOccurrenceCalibrationWeight(atoms) / 100d;
        // Shared combat-rule family: recover its selected variants, not every rule's target count.
        multiplier *= atoms.All(atom => atom.Template == "A:rule") ? 2d
            : atoms.Average(atom => _recoveryWeights.GetValueOrDefault(atom.SchemaKey, 100)) / 100d;
        multiplier *= EffectSelectionTuning.NecrobinderBlockAndSummonWeight(atoms, _catalogCharacter,
            _unifiedChaos) / 100d;
        return multiplier;
    }

    private double TargetRate(GeneratedRarity rarity, GeneratedCardType type, NativeComponentRole role,
        string family)
    {
        var cacheKey = (rarity, type, role, family);
        if (_targetRates.TryGetValue(cacheKey, out var cached)) return cached;
        var exactCards = _sourceCardsByRarityType.GetValueOrDefault((rarity, type));
        var exactOccurrences = _sourceOccurrencesByRarityTypeRole.GetValueOrDefault((rarity, type, role, family));
        double targetRate;
        if (exactCards > 0 && exactOccurrences > 0)
            targetRate = exactOccurrences / (double)exactCards;
        else
        {
            var rarityCards = _sourceCardsByRarity.GetValueOrDefault(rarity);
            var rarityOccurrences = _sourceOccurrencesByRarityRole.GetValueOrDefault((rarity, role, family));
            if (rarityCards > 0 && rarityOccurrences > 0)
                targetRate = rarityOccurrences / (double)rarityCards * 0.12d;
            else
            {
                // Cross-rarity/role recombination remains possible but rare. This is deliberately much smaller
                // than the same-rarity cross-type allowance, so a native Rare mechanic does not flood Basics.
                var poolCards = Math.Max(1, _sourceCardsByType.Values.Sum());
                var roleOccurrences = _sourceOccurrencesByRole.GetValueOrDefault((role, family));
                targetRate = (roleOccurrences > 0
                    ? roleOccurrences
                    : _sourceOccurrences.GetValueOrDefault(family)
                        // A reviewed independent downside does not require its native card's Power-foundation
                        // role. Keep the cross-rarity allowance but do not additionally suppress that role transfer.
                        * (_recoveredIndependentDownsides.Contains(family) ? 1d : 0.12d)) / poolCards * 0.02d;
            }
        }
        var resolved = Math.Max(0.001d,
            targetRate * _rarityTargetMultipliers.GetValueOrDefault((rarity, family), 1d));
        _targetRates[cacheKey] = resolved;
        return resolved;
    }

    internal static int AuditSelectionWeight(double targetRate, int observedCards, int observedOccurrences,
        int currentOccurrences = 0, int maximumWeight = 400)
    {
        targetRate = Math.Max(0.001d, targetRate);
        var expected = targetRate * (observedCards + PriorCards);
        var actual = observedOccurrences + currentOccurrences + targetRate * PriorCards;
        var ratio = expected / Math.Max(0.001d, actual);
        return Math.Clamp((int)Math.Round(ratio * ratio * 100d,
            MidpointRounding.AwayFromZero), 1, maximumWeight);
    }
}
