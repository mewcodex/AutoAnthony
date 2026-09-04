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
        _sourceCardsByRarityType = catalog.Recipes.GroupBy(recipe => (recipe.OriginalRarity, recipe.Type))
            .ToDictionary(group => group.Key, group => group.Count());
        _sourceCardsByRarity = catalog.Recipes.GroupBy(recipe => recipe.OriginalRarity)
            .ToDictionary(group => group.Key, group => group.Count());
        _sourceOccurrencesByRarityTypeRole = catalog.Recipes
            .SelectMany(recipe => recipe.Atoms.Select((atom, atomIndex) =>
                (recipe.OriginalRarity, recipe.Type, Role: SourceRole(recipe, atomIndex), atom.FamilyKey)))
            .GroupBy(item => item)
            .ToDictionary(group => group.Key, group => group.Count());
        _sourceOccurrencesByRarityRole = catalog.Recipes
            .SelectMany(recipe => recipe.Atoms.Select((atom, atomIndex) =>
                (recipe.OriginalRarity, Role: SourceRole(recipe, atomIndex), atom.FamilyKey)))
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
        var occurrences = catalog.Recipes.SelectMany(recipe => recipe.Atoms.Select((atom, atomIndex) =>
            (Recipe: recipe, Atom: atom, Role: SourceRole(recipe, atomIndex)))).ToArray();
        _schemaCounts = occurrences.GroupBy(item => item.Atom.SchemaKey)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        _raritySchemaCounts = occurrences.GroupBy(item => (item.Recipe.OriginalRarity, item.Atom.SchemaKey))
            .ToDictionary(group => group.Key, group => group.Count());
        _shellSchemaCounts = occurrences.GroupBy(item =>
                (item.Recipe.OriginalRarity, item.Recipe.Type, item.Recipe.Target, item.Atom.SchemaKey))
            .ToDictionary(group => group.Key, group => group.Count());
        _roleSchemaCounts = occurrences.GroupBy(item => (item.Role, item.Atom.SchemaKey))
            .ToDictionary(group => group.Key, group => group.Count());
        _roleRaritySchemaCounts = occurrences.GroupBy(item =>
                (item.Role, item.Recipe.OriginalRarity, item.Atom.SchemaKey))
            .ToDictionary(group => group.Key, group => group.Count());
        _roleShellSchemaCounts = occurrences.GroupBy(item =>
                (item.Role, item.Recipe.OriginalRarity, item.Recipe.Type, item.Recipe.Target,
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
        var role = ComponentAssemblyGenerator.SelectionRole(currentCard, type,
            _atomsByFamily.GetValueOrDefault(family) ?? []);
        var targetRate = TargetRate(rarity, type, role, family);
        var observedCards = _observedCardsByRarityType.GetValueOrDefault((rarity, type));
        var observedOccurrences = _observedOccurrencesByRarityTypeRole
            .GetValueOrDefault((rarity, type, role, family));
        var currentOccurrences = EnumerateRoleFamilies(type, currentCard)
            .Count(item => item.Role == role && item.Family == family);
        return AuditSelectionWeight(targetRate, observedCards, observedOccurrences, currentOccurrences);
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
        var role = ComponentAssemblyGenerator.SelectionRole(currentCard, type,
            _atomsByFamily.GetValueOrDefault(family) ?? []);
        return Math.Max(1, (int)Math.Round(TargetRate(rarity, type, role, family) * 10_000d,
            MidpointRounding.AwayFromZero));
    }

    public int VariantWeight(ComponentAtom atom, GeneratedRarity rarity, GeneratedCardType type,
        TargetMode target, IReadOnlyList<GeneratorOperation> currentCard)
    {
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
        return hasNativeRole
            ? weight
            : Math.Max(1, (int)Math.Min(int.MaxValue, PercentWeight.Apply(weight, 12)));
    }

    public void Observe(GeneratedCard card)
    {
        var cardKey = (card.Rarity, card.Type);
        _observedCardsByRarityType[cardKey] = _observedCardsByRarityType.GetValueOrDefault(cardKey) + 1;
        foreach (var item in EnumerateRoleFamilies(card.Type, card.Operations))
        {
            if (!_sourceOccurrences.ContainsKey(item.Family)) continue;
            var key = (card.Rarity, card.Type, item.Role, item.Family);
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
            if (operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
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

    private double TargetMultiplier(string family)
    {
        if (!_atomsByFamily.TryGetValue(family, out var atoms)) return 1d;
        var multiplier = atoms.Any(ComponentPolicy.HasReplaceableSlot) ? 1.10d : 1d;
        multiplier *= EffectSelectionTuning.NecrobinderBlockAndSummonWeight(atoms, _catalogCharacter,
            _unifiedChaos) / 100d;
        return multiplier;
    }

    private double TargetRate(GeneratedRarity rarity, GeneratedCardType type, NativeComponentRole role,
        string family)
    {
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
                    : _sourceOccurrences.GetValueOrDefault(family) * 0.12d) / poolCards * 0.02d;
            }
        }
        return Math.Max(0.001d, targetRate * TargetMultiplier(family));
    }

    internal static int AuditSelectionWeight(double targetRate, int observedCards, int observedOccurrences,
        int currentOccurrences = 0)
    {
        targetRate = Math.Max(0.001d, targetRate);
        var expected = targetRate * (observedCards + PriorCards);
        var actual = observedOccurrences + currentOccurrences + targetRate * PriorCards;
        var ratio = expected / Math.Max(0.001d, actual);
        return Math.Clamp((int)Math.Round(ratio * ratio * 100d,
            MidpointRounding.AwayFromZero), 1, 400);
    }
}
