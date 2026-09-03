using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ChaosCardGenerator;

internal enum NativeComponentRole { Unlinked, AbilityFoundation, AbilityPayoff, ConditionalPayoff }

/// <summary>
/// Assembles cards from a character's complete component catalog. Native card rows provide component and legality
/// samples only; this class never selects or returns a native-card preset.
/// </summary>
public sealed class ComponentAssemblyGenerator
{
    private const int AssemblyAttemptsPerShell = 64;
    private const int ShellRerollLimit = 4;
    private const int DuplicateFailuresPerDensityStep = 3;
    private const int MaximumAdaptiveEffectCount = 8;
    internal const double BalancedUpperBoundMultiplier = 1.15d;
    private sealed record ShellFrequencyIndex(
        IReadOnlyDictionary<GeneratedRarity, int> RecipeCountsByRarity,
        IReadOnlyDictionary<(GeneratedRarity Rarity, CardTag Tag), int> TagCountsByRarity,
        IReadOnlyDictionary<(GeneratedRarity Rarity, string KeywordId), int> CustomKeywordCountsByRarity);
    private readonly record struct ResolvedSlot(
        string? DerivativeId,
        string? DerivativeEnchantmentId,
        int? DerivativeEnchantmentAmount,
        string? OrbSourceId,
        string? OrbOutputId,
        bool IsCurse);
    private static readonly Dictionary<string, ShellFrequencyIndex> ShellFrequencyIndexes = new(StringComparer.Ordinal);
    private static readonly object ShellFrequencyIndexLock = new();
    private readonly Random _random;
    private readonly IComponentCatalog _catalog;
    private readonly IComponentCatalog _componentCatalog;
    private readonly IComponentCatalog _nameCatalog;
    private readonly GeneratedCharacter _character;
    private readonly bool _unlockComponentRoles;
    private readonly bool _ancientFuelActive;
    private readonly bool _suppressDerivativeReferences;
    // All future value calibration and regression baselines target balanced mode. Aggressive mode may map that
    // distribution upward, but must not become the source of component values or native occurrence priors.
    private readonly bool _balancedValues;
    private readonly bool _randomizeNumericValues;
    private readonly IReadOnlyDictionary<GeneratedRarity, int> _recipeCountsByRarity;
    private readonly IReadOnlyDictionary<(GeneratedRarity Rarity, CardTag Tag), int> _tagCountsByRarity;
    private readonly IReadOnlyDictionary<(GeneratedRarity Rarity, string KeywordId), int> _customKeywordCountsByRarity;
    private readonly IComponentOccurrencePolicy _frequencyTracker;
    private readonly IComponentValuePolicy _valuePolicy;
    private readonly ComponentKeywordPolicy _keywordPolicy;
    private readonly string _profileId;
    private readonly ISet<string>? _usedChineseNames;
    private readonly ISet<string>? _usedEnglishNames;
    private readonly ISet<string>? _usedEffectSignatures;
    private readonly ISet<string>? _usedPoolUniqueComponents;
    private readonly SpecialXGenerationMode _specialXMode;
    private GeneratedCharacter? BalanceCharacter => _unlockComponentRoles ? null : _character;

    public ComponentAssemblyGenerator(Random random, ISet<string>? usedChineseNames = null, ISet<string>? usedEnglishNames = null)
        : this(random, GeneratedCharacter.Ironclad, usedChineseNames, usedEnglishNames) { }

    public ComponentAssemblyGenerator(Random random, GeneratedCharacter character, ISet<string>? usedChineseNames = null,
        ISet<string>? usedEnglishNames = null, bool unlockComponentRoles = false,
        SpecialXGenerationMode specialXMode = SpecialXGenerationMode.Normal, bool ancientFuelActive = false,
        bool suppressDerivativeReferences = false, bool balancedValues = true, bool randomizeNumericValues = false,
        ISet<string>? usedEffectSignatures = null, IComponentOccurrencePolicy? frequencyTracker = null,
        ISet<string>? usedPoolUniqueComponents = null, ComponentGenerationProfile? profile = null,
        string? profileRegistrationId = null)
    {
        _random = random;
        _character = character;
        _unlockComponentRoles = unlockComponentRoles;
        _ancientFuelActive = ancientFuelActive;
        _suppressDerivativeReferences = suppressDerivativeReferences;
        _balancedValues = balancedValues;
        _randomizeNumericValues = randomizeNumericValues;
        // Catalog ownership, occurrence control and numeric parameter policy are resolved outside the assembly
        // algorithm. The built-in provider preserves the historical native/Ultimate catalogs exactly; future
        // providers can supply reviewed packages without adding another character switch to this class.
        profile ??= ComponentApi.Resolve(new ComponentProfileRequest(character, unlockComponentRoles));
        if (profile.Character != character || profile.UnlockComponentRoles != unlockComponentRoles)
            throw new ArgumentException($"Profile {profile.Id} does not match {character}/{unlockComponentRoles}.",
                nameof(profile));
        _catalog = profile.ShellCatalog;
        _componentCatalog = profile.ComponentCatalog;
        _nameCatalog = profile.NameCatalog;
        _valuePolicy = profile.ValuePolicy;
        _keywordPolicy = profile.KeywordPolicy;
        _profileId = profileRegistrationId ?? profile.Id;
        var index = GetShellFrequencyIndex(profile.Id, _catalog);
        _recipeCountsByRarity = index.RecipeCountsByRarity;
        _tagCountsByRarity = index.TagCountsByRarity;
        _customKeywordCountsByRarity = index.CustomKeywordCountsByRarity;
        _frequencyTracker = frequencyTracker
            ?? profile.CreateOccurrencePolicy();
        _usedChineseNames = usedChineseNames;
        _usedEnglishNames = usedEnglishNames;
        _usedEffectSignatures = usedEffectSignatures;
        _usedPoolUniqueComponents = usedPoolUniqueComponents;
        _specialXMode = specialXMode;
    }

    private static ShellFrequencyIndex GetShellFrequencyIndex(string profileId, IComponentCatalog catalog)
    {
        lock (ShellFrequencyIndexLock)
        {
            if (ShellFrequencyIndexes.TryGetValue(profileId, out var existing)) return existing;
            var created = new ShellFrequencyIndex(
                catalog.Recipes.GroupBy(recipe => recipe.OriginalRarity)
                    .ToDictionary(group => group.Key, group => group.Count()),
                catalog.Recipes.SelectMany(recipe => recipe.Tags.Select(tag =>
                        (recipe.OriginalRarity, Tag: tag)))
                    .GroupBy(item => item)
                    .ToDictionary(group => group.Key, group => group.Count()),
                catalog.Recipes.SelectMany(recipe => (recipe.CustomKeywords ?? []).Select(keywordId =>
                        (recipe.OriginalRarity, KeywordId: keywordId)))
                    .GroupBy(item => item)
                    .ToDictionary(group => group.Key, group => group.Count()));
            ShellFrequencyIndexes[profileId] = created;
            return created;
        }
    }

    public GeneratedCard Generate()
    {
        var rarity = _unlockComponentRoles
            ? Pick((GeneratedRarity.Basic, 4), (GeneratedRarity.Common, 20), (GeneratedRarity.Uncommon, 35), (GeneratedRarity.Rare, 25), (GeneratedRarity.Ancient, 2))
            : _character == GeneratedCharacter.Colorless
            ? Pick((GeneratedRarity.Uncommon, 31), (GeneratedRarity.Rare, 21))
            : _character != GeneratedCharacter.Ironclad
            ? Pick((GeneratedRarity.Basic, 4), (GeneratedRarity.Common, 20), (GeneratedRarity.Uncommon, 35), (GeneratedRarity.Rare, 25), (GeneratedRarity.Ancient, 2))
            : Pick((GeneratedRarity.Basic, 3), (GeneratedRarity.Common, 20), (GeneratedRarity.Uncommon, 35), (GeneratedRarity.Rare, 25), (GeneratedRarity.Ancient, 2));
        return Generate(rarity);
    }

    /// <summary>
    /// Generates one production card at the requested rarity. Rarity changes component weights but never removes a
    /// component, so every complete native component assembly remains reachable with nonzero probability.
    /// </summary>
    public GeneratedCard Generate(GeneratedRarity rarity) => GenerateMatching(rarity, null);

    /// <summary>
    /// Generates and commits the first card accepted by <paramref name="accept"/>. Rejected matches never reserve
    /// a name, effect signature, pool-unique component, or occurrence observation. Pool repair uses this boundary
    /// instead of repeatedly committing throwaway cards and progressively exhausting its own candidate space.
    /// </summary>
    internal GeneratedCard GenerateMatching(GeneratedRarity rarity, Func<GeneratedCard, bool>? accept)
    {
        // These are card-level mode rolls, not assembly-attempt rolls. A difficult shell therefore cannot bias
        // either advertised probability merely by consuming more retries than an ordinary shell.
        var raiseAggressiveEffectFloor = !_balancedValues
            && AggressiveModeTuning.ShouldRaiseEffectCountFloor(false, _random.Next(100));
        var softenAggressiveNegative = !_balancedValues
            && AggressiveModeTuning.ShouldOptimizeNegatives(false, _random.Next(100));
        var wantsNonBasicStarPayment = NumericGenerationTuning.SampleNonBasicStarPayment(
            _random, _unlockComponentRoles || _character == GeneratedCharacter.Regent, rarity);
        IroncladCardRecipe? lastShell = null;
        var duplicateFailures = 0;
        // A pool-repair predicate may require a different card type/resource shell (for example starter damage).
        // Give that bounded search enough independent shells instead of failing and regenerating the entire pool.
        var shellRerollLimit = accept is null ? ShellRerollLimit : ShellRerollLimit * 4;
        for (var shellAttempt = 0; shellAttempt < shellRerollLimit; shellAttempt++)
        {
            lastShell = PickShell(rarity);
            if (TryGenerate(rarity, lastShell, ref duplicateFailures, raiseAggressiveEffectFloor,
                    softenAggressiveNegative, wantsNonBasicStarPayment, accept) is { } generated)
                return generated;
        }
        // A legal shell should normally succeed in a handful of attempts. This path exists to guarantee that an
        // unforeseen cross-character interaction in Ultimate Chaos can degrade to a simple valid card instead of
        // pinning the pool-generation worker forever.
        for (var fallbackAttempt = 0; fallbackAttempt < 256; fallbackAttempt++)
        {
            var fallback = GenerateEmergencyFallback(rarity, lastShell!, fallbackAttempt,
                raiseAggressiveEffectFloor, wantsNonBasicStarPayment);
            if (TryFinalizeUniqueCard(fallback, ref duplicateFailures, accept, out var finalized)) return finalized;
        }
        throw new InvalidOperationException($"Could not produce a unique emergency fallback for {_character}/{rarity}.");
    }

    private GeneratedCard? TryGenerate(GeneratedRarity rarity, IroncladCardRecipe shell,
        ref int duplicateFailures, bool raiseAggressiveEffectFloor, bool softenAggressiveNegative,
        bool wantsNonBasicStarPayment, Func<GeneratedCard, bool>? accept)
    {
        // Rejected assemblies are expected. Keep them in an iterative loop: rare shells with a very low legal
        // acceptance rate must not accumulate one stack frame per retry or keep the worker alive forever.
        for (var assemblyAttempt = 0; assemblyAttempt < AssemblyAttemptsPerShell; assemblyAttempt++)
        {
        // Type and target still come from the native shell distribution. On an assembly failure, keep that shell and
        // redraw components so type-specific acceptance rates cannot distort the sampled distribution. Sample cost
        // first so it can constrain both effect count and numeric tier on cheap cards.
        var usesSharedResourceShell = _unlockComponentRoles || _character == GeneratedCharacter.Regent;
        var nativeStarCost = usesSharedResourceShell ? shell.StarCost : -1;
        var hasStarCostX = wantsNonBasicStarPayment && usesSharedResourceShell && shell.HasStarCostX
            && NumericGenerationTuning.KeepStarXCost(_random, hasStarCostX: true,
                ultimateChaos: _unlockComponentRoles);
        var starCost = hasStarCostX
            ? -1
            : rarity == GeneratedRarity.Basic
                ? NumericGenerationTuning.ApplyBasicStarCostTuning(_random, nativeStarCost, rarity)
                : wantsNonBasicStarPayment
                    ? NumericGenerationTuning.SampleNonBasicFixedStarCost(_random, nativeStarCost, rarity)
                    : -1;
        var budgetCost = SampleCost(shell.Type, rarity, shell.Cost == -1, starCost > 0 || hasStarCostX);
        var plannedCost = NumericGenerationTuning.ApplyUltimateEnergyCostDiscount(
            _random, budgetCost, _unlockComponentRoles);
        // A printed 0-Energy card with no Star payment has already reached the floor: any effect that lowers
        // this card's own cost is dead text. Fixed/X Star costs still represent a real resource payment and are
        // intentionally excluded from this rule.
        var isZeroResourceCard = plannedCost == 0 && starCost <= 0 && !hasStarCostX;
        // Two fixed Stars buy one ordinary Energy tier. Keep the conversion linear at high Star costs too:
        // for example, 1 Energy + 2 Stars is budgeted exactly like a 2-Energy card.
        var starBudget = hasStarCostX ? 2 : CardEffectRules.StarCostEnergyEquivalent(starCost);
        var effectiveBudget = budgetCost < 0 ? budgetCost : budgetCost + starBudget;
        var printedResourceCost = ResourceEconomyModel.PrintedCost(plannedCost, starCost,
            hasEnergyX: plannedCost < 0, hasStarX: hasStarCostX);
        // Start from the native effect-count distribution, then apply mild cost and rarity adjustments.
        var componentCount = NumericGenerationTuning.ApplyUltimateComponentBonus(
            _random, PickComponentCount(rarity, effectiveBudget, shell.Type), _unlockComponentRoles);
        var (temporaryMinimum, temporaryMaximum) = AdaptiveEffectCountWindow(duplicateFailures);
        var rarityEffectCountMinimum = RarityEffectCountMinimum(rarity);
        if (raiseAggressiveEffectFloor)
        {
            temporaryMinimum = Math.Max(temporaryMinimum, rarityEffectCountMinimum + 1);
            temporaryMaximum = Math.Max(temporaryMaximum, temporaryMinimum);
        }
        componentCount = Math.Clamp(componentCount, temporaryMinimum, temporaryMaximum);
        var operations = new List<GeneratorOperation>();
        var slots = new CardSlotContext();
        var difficultConditionBonusGranted = false;
        var resourceDebtBonusLineGranted = false;
        var curseStatusEasterEgg = false;

        for (var index = 0; index < componentCount; index++)
        {
            var candidates = _componentCatalog.Atoms
                .Where(atom => ComponentPolicy.PoolUniqueKey(atom) is not { } uniqueKey
                    || _usedPoolUniqueComponents?.Contains(uniqueKey) != true)
                .Where(atom => IsCompatible(shell.Type, shell.Target, shell.Cost, shell.HasStarCostX, atom, operations) && slots.CanResolve(atom.CardReference))
                .Where(atom => !isZeroResourceCard || !CardEffectRules.IsSelfCostReduction(atom))
                .Where(atom => !isZeroResourceCard || atom.Template != "D:CreateZeroCostCopyInDiscard")
                .Where(atom => !_suppressDerivativeReferences
                    || !DerivativePoolConstraintResolver.RequiresProducedDerivative(atom.Template))
                .Where(atom => !EffectSelectionTuning.DifficultConditionAwaitsPayoff(operations)
                    || atom.Scope is not (OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger or OperationScope.AbilityRule)
                    && !CardEffectRules.IsDependencyPrefix(atom))
                // A dependency prefix is half of one semantic clause. Never leave it in the final slot;
                // once selected, the following slot is reserved for one of its legal payoffs.
                .Where(atom => index < componentCount - 1 || !CardEffectRules.IsDependencyPrefix(atom))
                // “Grant an effect to the next Attack(s)” is a scoped modifier block, not a free-form trigger.
                // Its single compatible payload and the block itself must occupy the final two operation slots.
                .Where(atom => !CardEffectRules.IsNextAttackGrantTrigger(atom)
                    || shell.Type != GeneratedCardType.Power && index == componentCount - 2)
                // Reboot-style reshuffling is only meaningful when this card subsequently draws. It remains a
                // separate operation (and sentence), but reserves the next component slot for an immediate draw.
                .Where(atom => index < componentCount - 1 || atom.Template != "D:ShuffleAllUnexhaustedIntoDraw")
                // Do not spend the final planned slot on an operation that cannot possibly complete the sampled
                // shell.  This is only a look-ahead over hard end-state requirements (Attack damage, a real target,
                // and a positive payoff on paid cards); triggers/downside lines that can reserve another slot remain
                // eligible.  Previously these doomed partial cards ran through numeric rolling, slot binding and all
                // whole-card validators before being rejected near the end of the attempt.
                .Where(atom => index < componentCount - 1
                    || CanCompleteFinalPlannedSlot(shell, plannedCost, starCost, hasStarCostX, operations, atom))
                // The first Power component establishes its persistent purpose. Instant and this-turn components may
                // follow, but cannot make a card a Power on their own.
                .Where(atom => shell.Type != GeneratedCardType.Power || index != 0
                    || CardEffectRules.IsPersistentPowerFoundation(atom)
                    || CanAnchorRestrictedPowerFoundation(atom))
                // If a Power starts with the damage/block anchor required by a restricted effect, force that
                // restricted effect next. This keeps a theoretically legal route reachable in a large candidate pool.
                .Where(atom => shell.Type != GeneratedCardType.Power || index == 0
                    || operations.Any(CardEffectRules.IsPersistentPowerFoundation)
                    || CardEffectRules.IsRestrictedEffect(atom))
                .ToArray();
            if (candidates.Length == 0)
                break;

            // A conditional payoff is worth more than an unconditional line. Difficult conditions move the
            // following numeric effect up several budget tiers while retaining the card shell's actual cost.
            var currentEffectiveCost = ResourceEconomyModel.EffectiveCost(printedResourceCost, operations);
            var payoffBudget = effectiveBudget
                + EffectSelectionTuning.PayoffBudgetBonus(operations)
                + (curseStatusEasterEgg ? 3 : 0);
            var atom = InstantiateNumericSlotsStructured(
                PickForRarity(candidates, rarity, payoffBudget, shell.Type, shell.Target, operations,
                    currentEffectiveCost),
                rarity,
                payoffBudget,
                componentCount,
                operations,
                plannedCost == 0 && starBudget == 0,
                shell.Type);
            atom = ResolveSlots(atom, effectiveBudget,
                operations.LastOrDefault()?.Template == "D:ForEachEnemy", out var resolvedSlot);
            if (resolvedSlot.IsCurse)
            {
                curseStatusEasterEgg = true;
                // The curse replaces an already-negative status payload. Reserve two more semantic lines where
                // the global card-text cap permits; later compensation also boosts every scalable payoff.
                componentCount = Math.Min(5, componentCount + 2);
            }
            var parameters = new Dictionary<string, int>();
            var triggerIndex = LinkedTriggerIndex(operations, atom);
            var cardTargetSlot = slots.Resolve(atom.CardReference, operations);
            if (triggerIndex >= 0)
                parameters["triggerIndex"] = triggerIndex;
            if (atom.Template == "N:RetaliateDamage")
            {
                // Flame Barrier's printed clause is two executable components: the attacked trigger and damage.
                if (triggerIndex < 0)
                {
                    var attackedTriggerIndex = operations.Count;
                    operations.Add(new GeneratorOperation(
                        "C:untilTurnEnd",
                        OperationScope.ConditionalTrigger,
                        "本回合每当你受到一次攻击时。",
                        new Dictionary<string, int>(),
                        RuntimeSpec: CatalogRuntimeSpecRegistry.Get("ironclad/flamebarrier/1")));
                    parameters["triggerIndex"] = attackedTriggerIndex;
                }
                var damage = OperationRuntimeSpecCompiler.FixedValue(atom, "damage",
                    OperationRuntimeSpecCompiler.ExplicitFixedValueSlots(atom).FirstOrDefault()?.BaseValue ?? 0);
                operations.Add(new GeneratorOperation(
                    atom.Template,
                    atom.Scope,
                    $"对攻击者造成{damage}点伤害。",
                    parameters,
                    cardTargetSlot,
                    atom.RequiresSingleTarget,
                    RuntimeSpec: OperationRuntimeSpecCompiler.StandaloneRetaliateDamage(
                        OperationRuntimeSpecCompiler.GetOrCompile(atom))));
                continue;
            }
            var nextAttackPayload = triggerIndex >= 0
                && CardEffectRules.IsNextAttackGrantTrigger(operations[triggerIndex]);
            var generatedOperation = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
                parameters, cardTargetSlot, atom.RequiresSingleTarget && !nextAttackPayload,
                resolvedSlot.DerivativeId, resolvedSlot.DerivativeEnchantmentId,
                resolvedSlot.OrbSourceId, resolvedSlot.OrbOutputId, resolvedSlot.DerivativeEnchantmentAmount,
                RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom),
                LocalizedText: LocalizedText(atom),
                LocalizationId: ComponentLocalizationApi.TryGet(atom.SemanticId, out _) ? atom.SemanticId : null);
            operations.Add(generatedOperation);
            if (CardEffectRules.IsCurrentBlockDamageModifier(generatedOperation))
                NormalizeCurrentBlockDamageAnchor(operations, operations.Count - 1);
            var updatedEffectiveCost = ResourceEconomyModel.EffectiveCost(printedResourceCost, operations);
            if (!resourceDebtBonusLineGranted && componentCount < 5
                && ResourceEconomyModel.NegativeExtraLineChance(updatedEffectiveCost) is var debtLineChance
                && debtLineChance > 0 && _random.Next(100) < debtLineChance)
            {
                // Net-resource-positive cards remain legal, including free refund cards.  They merely reserve one
                // more opportunity for a downside, whose family weight is raised by the same effective-cost debt.
                componentCount = Math.Min(5, Math.Max(componentCount + 1, operations.Count + 1));
                resourceDebtBonusLineGranted = true;
            }
            // Explicit downsides never buy extra printed components. Their direct multiplier or linear price is
            // converted later into printed numeric scaling and—only for extreme/non-scalable
            // cases—a cost adjustment. Rarity/cost therefore remain the sole source of ordinary effect density.
            // Hard one-shot conditions do not consume the ordinary payoff count. Reserve room for two payoffs,
            // but keep the global five-operation ceiling.
            if (!difficultConditionBonusGranted
                && EffectSelectionTuning.DifficultConditionTier(generatedOperation) >= 2
                && CardEffectRules.TriggerNeedsLinkedEffect(generatedOperation)
                && componentCount < 5)
            {
                componentCount = Math.Min(5, Math.Max(componentCount + 1, operations.Count + 2));
                difficultConditionBonusGranted = true;
            }
            if (CardEffectRules.TriggerNeedsLinkedEffect(generatedOperation)
                && operations.Count >= componentCount && componentCount < 5)
            {
                // Reserve exactly one locally sampled payoff for an ordinary trigger selected in the final planned
                // slot. This replaces the old hard-coded Block 5 completion without rejecting the whole assembly.
                componentCount = operations.Count + 1;
            }
        }

        // Always emit at least one operation, and always give a trigger an executable payoff.
        if (operations.LastOrDefault() is { } unfinishedDependency
            && CardEffectRules.IsDependencyPrefix(unfinishedDependency))
            continue;
        if (operations.Count == 0)
            operations.Add(new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得5点格挡。",
                new Dictionary<string, int> { ["block"] = 5 },
                RuntimeSpec: CatalogRuntimeSpecRegistry.Get("ironclad/defendironclad/0")));
        // A trigger selected as the final component has no semantic payoff. The old emergency completion appended
        // a hard-coded Block 5, bypassing rarity/cost/trigger-frequency budgets and producing cards such as a
        // one-cost “next turn: gain 5 Block”. Reject and reassemble so the linked effect is sampled normally.
        if (CardEffectRules.TriggerNeedsLinkedEffect(operations[^1]))
            continue;
        if (!HasValidOperationAssembly(operations))
            continue;

        // A Channel line is not always one ordinary effect line: two Orbs consume roughly two line budgets, and
        // Glass/Plasma consume more per Orb than Lightning/Frost. Rebalance the other scalable rewards against the
        // actual rolled Orb payload. Keep a small unadjusted branch for exact native reconstruction and naturally
        // occurring high-roll combinations.
        if (_random.Next(100) >= 8)
            ApplyOrbBudgetCompensation(operations, rarity, effectiveBudget);

        var hasExtremeLifecycleDownside = operations.Any(CardEffectRules.IsExtremeLifecycleDownside);
        if (!_unlockComponentRoles && hasExtremeLifecycleDownside && shell.Type == GeneratedCardType.Power
            && _character is GeneratedCharacter.Ironclad or GeneratedCharacter.Silent)
            continue;
        if (shell.Type == GeneratedCardType.Power
            && !operations.Any(CardEffectRules.IsPersistentPowerFoundation))
            continue;

        var finalType = shell.Type == GeneratedCardType.Power
            ? GeneratedCardType.Power
            : CardEffectRules.HasAttackClassifyingDamage(operations) ? GeneratedCardType.Attack : GeneratedCardType.Skill;
        // A single-enemy target must be consumed by at least one operation; otherwise present the card as untargeted.
        var finalTarget = finalType == GeneratedCardType.Power
            ? TargetMode.Other
            : operations.Any(CardEffectRules.RequiresSingleEnemyTarget)
                ? TargetMode.SingleEnemy
                : TargetMode.Other;
        // A shell specifies only the resulting type and target. Components remain independently assembled. Reject a
        // result that does not support its shell (for example an Attack without damage, or a targeted shell with no
        // target consumer). This preserves native shell distributions without creating fake target selection.
        if (finalType != shell.Type || finalTarget != shell.Target)
            continue;
        // Extreme lifecycle payments now own explicit direct multipliers. They enter the same cost-discount and
        // whole-card scaling pipeline as every other multiplier downside instead of receiving a second hidden
        // damage/block boost and an unconditional zero-cost shell.
        var finalCost = curseStatusEasterEgg && plannedCost > 0 ? plannedCost - 1
            : plannedCost;
        // Every playable shell needs an actual benefit. Zero Energy is not compensation for a card which only
        // harms its owner, and Star/X shells must obey the same invariant. This is checked before downside-driven
        // cost discounts and again by the completed-card validator so no generation route can reintroduce a trap.
        if (!operations.Any(CardEffectRules.IsBeneficialEffect))
            continue;
        var hasVitalityEffect = operations.Any(CardEffectRules.IsHealingOrMaxHp);
        var hasRestrictedEffect = operations.Any(CardEffectRules.IsRestrictedEffect);
        var movesThisCardAfterPlay = operations.Any(operation => operation.Template is
            "R:PutThisOnDraw" or "R:ReturnThisToHand");
        if (movesThisCardAfterPlay && (finalType == GeneratedCardType.Power || hasRestrictedEffect))
            continue;
        if (hasVitalityEffect && finalType != GeneratedCardType.Power
            && operations.Any(CardEffectRules.IsCombatBaseDamageIncrease))
            continue;
        // Sly is deliberately sampled later. It must not participate in downside compensation, numeric scaling,
        // whole-card envelopes or any other budget multiplier.
        var tags = SampleTags(finalType, operations, rarity, finalCost, starCost, hasStarCostX);
        var customKeywords = SampleCustomKeywords(finalType, operations, rarity, finalCost, starCost,
            hasStarCostX, tags);
        if (hasRestrictedEffect && finalType != GeneratedCardType.Power && !tags.Contains(CardTag.Exhaust))
            tags = tags.Append(CardTag.Exhaust).ToArray();
        // Some useful effects (for example exhausting Statuses) have no scalable printed number. If an explicit
        // payment is attached to such a card, numeric multiplication cannot compensate it at all; grant one cost
        // tier instead. Keyword-only payments retain their native pricing.
        var hasScalablePositive = operations.Any(operation => !CardEffectRules.IsNegativeEffect(operation)
                && _valuePolicy.IsScalableReward(new ComponentAtom(operation.Template, operation.Scope,
                    operation.ChineseText, operation.RequiresSingleTarget, CardReferenceRequirement.None)
                    { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(operation) }));
        var hasSelfCostIncrease = operations.Any(operation => operation.Template == "D:IncreaseThisCardCost");
        var positiveEffectCount = operations.Count(CardEffectRules.IsBeneficialEffect);
        var downsideMultiplier = NegativeEffectTuning.TotalMultiplier(operations, tags,
            character: BalanceCharacter);
        var preserveExactDownsidePricing = downsideMultiplier > 1.0001d
            && !NegativeEffectTuning.HasRepeatedTriggeredDownside(operations)
            && _random.Next(100) < NegativeEffectTuning.ExactReconstructionChance;
        if (!preserveExactDownsidePricing)
        {
            var discount = NegativeEffectTuning.CostDiscount(downsideMultiplier, hasScalablePositive,
                hasSelfCostIncrease, positiveEffectCount);
            finalCost = ApplyDownsideCostDiscount(finalCost, discount);
            var hasPrintedResourceCost = finalCost != 0 || starCost > 0 || hasStarCostX;
            ApplyNegativeEffectCompensation(operations, tags, hasPrintedResourceCost, finalType);
        }
        if (curseStatusEasterEgg)
            ApplyCurseStatusEasterEggCompensation(operations);
        var finalStarCost = curseStatusEasterEgg && !hasStarCostX && starCost > 0
            ? starCost - 1
            : starCost;
        var hasSly = SlyKeywordTuning.CanAttach(finalCost, _specialXMode, hasExtremeLifecycleDownside)
            && _keywordPolicy.AllowsBase(CardTag.Sly)
            && SampleTag(CardTag.Sly, rarity, finalCost, finalStarCost, hasStarCostX, finalType,
                tags.Contains(CardTag.Exhaust));
        // Sly is decided after explicit-downside compensation. Its ordinary template is therefore the compensated
        // 0/1 cost, and every whole-card balance pass below validates the payload against that same coordinate
        // before the keyword adds one or two printed Energy.
        var templateFinalCost = finalCost;
        var printedFinalCost = SlyKeywordTuning.ApplyPrintedCostIncrease(templateFinalCost, hasSly, _random);
        if (hasSly && !SlyKeywordTuning.IsStrictPrintedCostIncrease(templateFinalCost, printedFinalCost))
            continue;
        // These clauses reduce this card's ordinary Energy cost by a printed amount. Clamp them against the cost
        // the player actually sees; Star payment cannot make a 0-Energy reduction meaningful.
        if (!CardEffectRules.ClampNumericSelfCostReductionAmounts(printedFinalCost, operations))
            continue;
        if (!CardEffectRules.HasValidGrandFinaleCost(printedFinalCost, finalStarCost, hasStarCostX, operations))
            continue;
        if (!CardEffectRules.HasValidReturnThisToHandCost(printedFinalCost, finalStarCost, hasStarCostX, operations))
            continue;
        var exactNativeEffectAssembly = IsExactNativeEffectAssembly(rarity, printedFinalCost, finalStarCost, hasStarCostX,
            finalType, finalTarget, operations);
        var exactNativeAssembly = rarity is GeneratedRarity.Rare or GeneratedRarity.Ancient
            && exactNativeEffectAssembly;
        if (!exactNativeAssembly)
            ApplyLostHpRepeatDamagePricing(operations);
        var finalBudgetEffectiveCost = ResourceEconomyModel.BudgetEffectiveCost(templateFinalCost, finalStarCost,
            templateFinalCost < 0, hasStarCostX, operations);
        if (!exactNativeAssembly)
            ApplyEffectiveCostAdjustment(operations, finalBudgetEffectiveCost, effectiveBudget);
        if (!exactNativeAssembly)
            NormalizeDependentNumericIncreases(operations);
        var finalEffectiveCostTier = double.IsNaN(finalBudgetEffectiveCost)
            ? templateFinalCost
            : Math.Max(0, (int)Math.Round(finalBudgetEffectiveCost, MidpointRounding.AwayFromZero));
        var finalEffectiveCostForFloor = double.IsNaN(finalBudgetEffectiveCost) ? -1d : finalBudgetEffectiveCost;
        ClampReusablePersistentCombatGains(operations, finalType, tags, finalEffectiveCostTier);
        ClampDurationOnlyStacks(operations);
        ClampSelectedSkillReplayCount(operations, finalEffectiveCostForFloor, tags);
        EnforceBasicDelayedPayoffFloor(operations, rarity, finalEffectiveCostTier);
        if (!RaisePurePositiveCostDrawFloor(operations, finalEffectiveCostForFloor))
            continue;
        if (!exactNativeAssembly
            && !RaiseNumericRewardsToTopRarityFloor(operations, rarity, finalEffectiveCostForFloor,
                _character, _unlockComponentRoles, finalType, tags))
            continue;
        if (!exactNativeAssembly && rarity == GeneratedRarity.Ancient
            && operations.Count(operation => operation.Template is not
                ("N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK")) == 1
            && !ApplySingleEffectAncientBonus(operations))
            continue;
        var hasFinalPrintedResourceCost = templateFinalCost != 0 || finalStarCost > 0 || hasStarCostX;
        if (!exactNativeEffectAssembly
            && !ApplyWholeCardBudgetEnvelope(operations, rarity, finalEffectiveCostForFloor,
                finalType, tags, hasFinalPrintedResourceCost, _balancedValues, _character,
                _unlockComponentRoles, skipUpperBound: curseStatusEasterEgg))
            continue;
        var preSynergyOperations = operations.ToArray();
        if (!exactNativeAssembly && !ApplyWholeCardSynergyPenalties(operations))
            continue;
        if (!TryPruneTinyStandaloneCombatRewards(operations, preSynergyOperations, rarity,
                finalEffectiveCostForFloor, finalType, finalTarget, tags, hasFinalPrintedResourceCost,
                applySynergyPenalties: !exactNativeAssembly, skipUpperBound: curseStatusEasterEgg))
            continue;
        // Synergy surcharges deliberately reduce printed numbers after the ordinary rarity envelope. Do not scale
        // the card back up (which would cancel the surcharge), but reject a result that fell below its rarity-aware
        // lower bound. This keeps flexible combinations expensive while preventing a Common 1-Energy card such as
        // Block 2 + Block 3 from surviving merely because its sum touches the Basic Defend floor.
        if (!exactNativeEffectAssembly
            && !HasPostSynergyWholeCardBudgetFloor(operations, rarity, finalEffectiveCostForFloor, finalType,
                tags, hasFinalPrintedResourceCost, _balancedValues, _character, _unlockComponentRoles))
            continue;
        if (!exactNativeEffectAssembly && (templateFinalCost < 0 || hasStarCostX)
            && !HasAdequateOrdinaryXCardValue(operations, finalType, tags, hasFinalPrintedResourceCost))
            continue;
        if (!exactNativeEffectAssembly)
        {
            var effectiveZeroAcceptance = CardAcceptanceTuning.EffectiveZeroAcceptancePercent(
                _character, _unlockComponentRoles, templateFinalCost, finalStarCost, templateFinalCost < 0, hasStarCostX,
                finalEffectiveCostForFloor);
            if (_random.Next(100) >= effectiveZeroAcceptance)
                continue;
            var pureDrawAcceptance = CardAcceptanceTuning.MarginalPureDrawAcceptancePercent(
                operations, rarity, finalEffectiveCostForFloor);
            if (_random.Next(100) >= pureDrawAcceptance)
                continue;
            var tinyRewardAcceptance = CardAcceptanceTuning.TinyImmediateRewardAcceptancePercent(
                operations, rarity);
            if (_random.Next(100) >= tinyRewardAcceptance)
                continue;
        }
        // Preserve balanced-mode seed output: the retired post-budget bonus branch consumed one percentile roll
        // here even though it could never append in balanced mode. Aggressive mode uses its card-level rolls above.
        if (_balancedValues)
            _ = _random.Next(100);
        if (softenAggressiveNegative && operations.Any(CardEffectRules.IsNegativeEffect))
            TrySoftenOneAggressiveNegative(operations);
        if (!CardEffectRules.ClampNumericSelfCostReductionAmounts(printedFinalCost, operations)
            || !CardEffectRules.HasValidGrandFinaleCost(printedFinalCost, finalStarCost, hasStarCostX, operations)
            || !CardEffectRules.HasValidEnergyXDoubleThreshold(operations))
            continue;
        ApplyUnconditionalStarGainSoftCap(operations, tags);
        // Whole-card envelopes and synergy passes may rescale every numeric reward after the current-Block
        // modifier initially normalized its hidden T:D anchor. Reassert the interpreter invariant at the very end;
        // the anchor is scaffolding, never a separately budgeted/printed damage value.
        var invalidCurrentBlockAnchor = false;
        for (var operationIndex = 0; operationIndex < operations.Count; operationIndex++)
        {
            if (!CardEffectRules.IsCurrentBlockDamageModifier(operations[operationIndex])) continue;
            if (!NormalizeCurrentBlockDamageAnchor(operations, operationIndex))
            {
                invalidCurrentBlockAnchor = true;
                break;
            }
        }
        if (invalidCurrentBlockAnchor) continue;
        // Aggressive negative softening and the unconditional-Star soft cap intentionally run after the scaling
        // pass. They can change either the downside allowance or the effective-cost coordinate, so the completed
        // card must satisfy the same envelope once more. This is validation only: reroll instead of scaling a
        // softened payment or restoring a Star amount that the soft cap just reduced.
        var finalValidatedEffectiveCost = ResourceEconomyModel.BudgetEffectiveCost(templateFinalCost,
            finalStarCost, templateFinalCost < 0, hasStarCostX, operations);
        if (!exactNativeEffectAssembly
            && !IsWithinWholeCardBudgetEnvelope(operations, rarity, finalValidatedEffectiveCost,
                finalType, tags, hasFinalPrintedResourceCost, _balancedValues, _character,
                _unlockComponentRoles, skipUpperBound: curseStatusEasterEgg))
            continue;
        if (SlyKeywordTuning.IsPureImmediateSelfRefund(templateFinalCost, operations) && !hasSly)
            continue;
        if (hasSly)
            tags = tags.Append(CardTag.Sly).ToArray();
        var renderedChineseDescription = CardDescriptionRenderer.Render(operations);
        // Planned component weights alone cannot fully control visible density: a trigger selected in the last
        // slot must gain its payoff, while resource debt and meaningful downsides may reserve compensation lines.
        // Apply a soft final-distribution correction to Basic cards only.  Dense legal cards retain a clear 70%
        // acceptance chance, so every native structure remains reachable and no semantic clause is truncated.
        var printedEffectLines = renderedChineseDescription.Count(character => character == '\n') + 1;
        if (raiseAggressiveEffectFloor && printedEffectLines < rarityEffectCountMinimum + 1)
            continue;
        if (rarity == GeneratedRarity.Basic && printedEffectLines >= 3 && _random.Next(100) < 30)
            continue;
        var baseCard = new GeneratedCard(
            printedFinalCost,
            finalType,
            finalTarget,
            rarity,
            renderedChineseDescription,
            tags,
            operations,
            Name: null,
            EnglishDescription: EnglishCardDescriptionRenderer.Render(operations),
            Character: _character,
            StarCost: finalStarCost,
            HasStarCostX: hasStarCostX,
            UnifiedChaos: _unlockComponentRoles,
            CustomKeywords: customKeywords.Count == 0 ? null : customKeywords);
        baseCard = SpecialXCardConverter.Convert(baseCard, _random, _specialXMode);
        if (_specialXMode == SpecialXGenerationMode.Forced && !SpecialXCardConverter.IsSpecial(baseCard))
            continue;
        // Pool-repair predicates depend only on the base card. In ordinary numeric modes, reject a mismatch before
        // the comparatively expensive upgrade search and final validation. Numeric-random mode must wait until its
        // deterministic post-roll projection in TryFinalizeUniqueCard.
        if (!_randomizeNumericValues && accept is not null && !accept(baseCard)) continue;
        var upgrade = CardUpgradeGenerator.Generate(baseCard, _random, _unlockComponentRoles, _profileId,
            _keywordPolicy);
        // Reject artificial assemblies for which none of the four supported upgrade families is legal.
        if (upgrade.Effects.Count == 0) continue;
        var card = baseCard with { Upgrade = upgrade };
        // Recheck the completed non-native card after special-X conversion and upgrade planning. This is a final
        // invariant, not another balancing pass: no fixed-cost Rare/Ancient card may escape with less positive
        // value than the floor merely because an earlier mechanic was irreducible or a speculative path changed.
        if (!exactNativeAssembly
            && !EffectBalanceModel.HasAdequateTopRarityCardValue(card.Operations, card.Rarity,
                finalEffectiveCostForFloor, card.Tags))
            continue;
        // Random assembly is speculative: a late interaction (including an upgrade plan) may make an otherwise
        // legal partial assembly fail a whole-card invariant. Treat that as a rejected roll, while keeping the
        // validator strict for snapshots and externally supplied definitions.
        try
        {
            CardTemplateValidator.Validate(card);
        }
        catch (InvalidOperationException)
        {
            continue;
        }
        if (TryFinalizeUniqueCard(card, ref duplicateFailures, accept, out var finalized)) return finalized;
        }
        return null;
    }

    /// <summary>
    /// Ordinary X is encoded as -1. A downside may discount a fixed cost to zero, but it must never erase the X
    /// resource marker. Previously every X+Exhaust roll entering the normal compensation branch was converted to
    /// zero cost; consequently the only surviving X+Exhaust cards came from the 3% exact-reconstruction branch,
    /// which deliberately receives no downside compensation.
    /// </summary>
    internal static int ApplyDownsideCostDiscount(int cost, int discount) =>
        cost < 0 ? cost : Math.Max(0, cost - discount);

    internal (int Minimum, int Maximum) AdaptiveEffectCountWindow(int duplicateFailures)
    {
        var minimum = _catalog.ComponentCounts.Min();
        // Five is the ordinary post-shell ceiling after Ultimate Chaos/condition completion bonuses. Do not
        // narrow that existing tail merely because the source recipe catalog itself tops out at three or four.
        var maximum = Math.Max(5, _catalog.ComponentCounts.Max());
        var steps = Math.Max(0, duplicateFailures) / DuplicateFailuresPerDensityStep;
        for (var step = 0; step < steps && minimum < MaximumAdaptiveEffectCount; step++)
        {
            if (minimum < maximum) minimum++;
            else
            {
                minimum++;
                maximum++;
            }
        }
        return (minimum, Math.Max(minimum, Math.Min(maximum, MaximumAdaptiveEffectCount)));
    }

    private int RarityEffectCountMinimum(GeneratedRarity rarity) =>
        _catalog.Recipes.Where(recipe => recipe.OriginalRarity == rarity)
            .Select(recipe => recipe.Atoms.Count)
            .DefaultIfEmpty(_catalog.ComponentCounts.Min())
            .Min();

    private bool TryFinalizeUniqueCard(GeneratedCard card, ref int duplicateFailures,
        Func<GeneratedCard, bool>? accept,
        out GeneratedCard finalized)
    {
        var completed = _randomizeNumericValues ? ApplyNumericRandomization(card) : card;
        if (accept is not null && !accept(completed))
        {
            finalized = null!;
            return false;
        }
        var signature = GeneratedCardEffectIdentity.Signature(completed);
        var exactPoolKey = "exact::" + signature;
        var templatePoolKey = "template::" + GeneratedCardEffectIdentity.TemplateSignature(completed);
        if (_usedEffectSignatures?.Contains(exactPoolKey) == true
            || _usedEffectSignatures?.Contains(templatePoolKey) == true)
        {
            duplicateFailures++;
            finalized = null!;
            return false;
        }

        // Naming reserves a unique pair only after every speculative assembly, upgrade, numeric-random and pool
        // effect-identity check has passed. Consume exactly one gameplay-RNG value, then isolate the role-specific
        // naming search in its own stream: different native name-catalog sizes must not desynchronize Ultimate
        // Chaos card effects between characters.
        var nameNonce = _random.Next();
        var nameHash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"AutoAnthony/CardName/v2|{_character}|{nameNonce}|{signature}"));
        var nameRandom = new Random(BitConverter.ToInt32(nameHash, 0) & int.MaxValue);
        completed = completed with
        {
            Name = CardNameGenerator.Generate(_nameCatalog, completed, nameRandom,
                _usedChineseNames, _usedEnglishNames)
        };
        if (_usedEffectSignatures is not null
            && (!_usedEffectSignatures.Add(exactPoolKey)
                || !_usedEffectSignatures.Add(templatePoolKey)))
        {
            duplicateFailures++;
            finalized = null!;
            return false;
        }
        _frequencyTracker.Observe(completed);
        if (_usedPoolUniqueComponents is not null)
            foreach (var uniqueKey in completed.Operations.Select(ComponentPolicy.PoolUniqueKey)
                         .Where(key => key is not null).Cast<string>().Distinct(StringComparer.Ordinal))
                _usedPoolUniqueComponents.Add(uniqueKey);
        finalized = completed;
        return true;
    }

    private GeneratedCard ApplyNumericRandomization(GeneratedCard card)
    {
        var operations = card.Operations.ToArray();
        var cardSignature = string.Join('|', operations.Select(operation =>
            OperationRuntimeSpecCompiler.GetOrCompile(operation).StableSignature()));
        for (var operationIndex = 0; operationIndex < operations.Length; operationIndex++)
        {
            var operation = operations[operationIndex];
            foreach (var slot in OperationRuntimeSpecCompiler.ExplicitFixedValueSlots(operation))
            {
                // Non-upgradable fixed slots are semantic markers (for example the native X>=4 doubling gate),
                // not scalable card output. Numeric Random mode varies gameplay magnitudes but must preserve those
                // routing thresholds just as ordinary upgrades do.
                if (!slot.Upgradable) continue;
                var original = slot.BaseValue + slot.Offset;
                if (original <= 0) continue;
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"AutoAnthony/NumericRandom/v2|{cardSignature}|{operationIndex}|{slot.Id}"));
                var bucketRoll = BitConverter.ToUInt32(hash, 0) % 10_000;
                var rangeRoll = BitConverter.ToUInt32(hash, sizeof(uint));
                var percent = bucketRoll switch
                {
                    < 5_000 => (int)(rangeRoll % 101),          // 50%: 0%-100%
                    < 9_000 => 100 + (int)(rangeRoll % 201),   // 40%: 100%-300%
                    _ => 300 + (int)(rangeRoll % 201)          // 10%: 300%-500%
                };
                var randomized = Math.Max(1,
                    (int)Math.Round(original * percent / 100d, MidpointRounding.AwayFromZero));
                if (slot.Id == "amount"
                    && OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("plating_reference"))
                    randomized = Math.Max(3, randomized);
                if (CardEffectRules.IsEnemyStrengthGain(operation))
                    randomized = Math.Min(2, randomized);
                if (operation.Template == "R:ReturnAfterSkillsPlayed")
                {
                    var upgradeDelta = card.Upgrade?.Effects.Where(effect =>
                            effect.OperationIndex == operationIndex
                            && effect.Kind == CardUpgradeKind.ReduceThreshold)
                        .Sum(effect => effect.Delta ?? 0) ?? 0;
                    randomized = Math.Max(2 - Math.Min(0, upgradeDelta), randomized);
                }
                if (operation.Template == "R:IfEnergyXAtLeast" && slot.Id == "threshold")
                    randomized = Math.Clamp(randomized, 1, 4);
                if (slot.Id == "duration"
                    && NumericGenerationTuning.DurationOnlyStackCap(operation, _character,
                        _unlockComponentRoles) is { } durationCap)
                    randomized = Math.Min(randomized, durationCap);
                if (CardEffectRules.IsNumericSelfCostReduction(operation) && slot.Id == "amount")
                {
                    var minimumCost = Math.Min(card.Cost, card.Upgrade?.UpgradedCost ?? card.Cost);
                    randomized = Math.Clamp(randomized, 1, Math.Max(1, minimumCost));
                }
                if (OperationRuntimeSpecCompiler.TryReplaceFixedValue(operation, slot.Id, randomized,
                        out var updated))
                    operation = updated;
            }
            operations[operationIndex] = operation;
        }

        var randomizedOperations = operations.ToArray();
        var randomizedUpgrade = card.Upgrade;
        if (randomizedUpgrade is not null)
        {
            var upgradedOperations = CardUpgradeGenerator.ApplyEffectsToOperations(randomizedOperations,
                randomizedUpgrade.Effects);
            randomizedUpgrade = randomizedUpgrade with
            {
                UpgradedChineseDescription = CardDescriptionRenderer.Render(upgradedOperations),
                UpgradedEnglishDescription = EnglishCardDescriptionRenderer.Render(upgradedOperations)
            };
        }
        var randomizedCard = card with
        {
            Operations = randomizedOperations,
            ChineseDescription = CardDescriptionRenderer.Render(randomizedOperations),
            EnglishDescription = EnglishCardDescriptionRenderer.Render(randomizedOperations),
            Upgrade = randomizedUpgrade
        };
        // Numeric Random deliberately runs after ordinary balance validation and may exceed generation-time
        // caps. Revalidate its structure with the same relaxed numeric profile used by save/multiplayer restore,
        // so every card we serialize is guaranteed to be accepted by an authoritative peer.
        CardTemplateValidator.Validate(randomizedCard, allowRandomizedNumericValues: true);
        return randomizedCard;
    }

    private bool IsExactNativeEffectAssembly(GeneratedRarity rarity, int cost, int starCost, bool hasStarCostX,
        GeneratedCardType type, TargetMode target, IReadOnlyList<GeneratorOperation> operations)
    {
        var semantic = operations.Where(operation => operation.Template is not
            ("N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK")).ToArray();
        bool MatchesShell(IroncladCardRecipe recipe)
        {
            var nativeCost = recipe.Cost >= 5 ? 4 : recipe.Cost;
            return recipe.OriginalRarity == rarity
                && nativeCost == cost
                && recipe.StarCost == starCost
                && recipe.HasStarCostX == hasStarCostX
                && recipe.Type == type
                && recipe.Target == target
                && recipe.Atoms.Count == semantic.Length
                && recipe.Atoms.Select(OperationRuntimeSpecCompiler.StructuralExactKey)
                    .SequenceEqual(semantic.Select(OperationRuntimeSpecCompiler.StructuralExactKey));
        }

        var structured = _catalog.Recipes.Any(MatchesShell);
        if (OperationRuntimeSpecCompiler.EnableLegacyEquivalenceAssertions)
        {
            var legacy = _catalog.Recipes.Any(recipe =>
            {
                var nativeCost = recipe.Cost >= 5 ? 4 : recipe.Cost;
                return recipe.OriginalRarity == rarity
                    && nativeCost == cost
                    && recipe.StarCost == starCost
                    && recipe.HasStarCostX == hasStarCostX
                    && recipe.Type == type
                    && recipe.Target == target
                    && recipe.Atoms.Count == semantic.Length
                    && recipe.Atoms.Select(atom => (atom.Template, atom.ChineseText))
                        .SequenceEqual(semantic.Select(operation => (operation.Template, operation.ChineseText)));
            });
            if (legacy != structured)
                throw new InvalidOperationException("Exact native assembly classification drift: "
                    + string.Join(" || ", semantic.Select(operation =>
                        $"{operation.Template}:{operation.ChineseText}:"
                        + OperationRuntimeSpecCompiler.StructuralExactKey(operation)))
                    + "; candidates=" + string.Join(" || ", _catalog.Recipes
                        .Where(recipe => recipe.Atoms.Count == semantic.Length
                            && recipe.Type == type && recipe.Target == target)
                        .Select(recipe => recipe.Id + ":" + string.Join(" / ", recipe.Atoms.Select(atom =>
                            $"{atom.Template}:{atom.ChineseText}:"
                            + OperationRuntimeSpecCompiler.StructuralExactKey(atom))))));
        }
        return structured;
    }

    internal static bool RaiseNumericRewardsToTopRarityFloor(IList<GeneratorOperation> operations,
        GeneratedRarity rarity, double effectiveCost, GeneratedCharacter? character = null,
        bool ultimateChaos = false, GeneratedCardType cardType = GeneratedCardType.Skill,
        IReadOnlyCollection<CardTag>? tags = null) => RaiseNumericRewardsToTopRarityFloorCore(
        operations, rarity, effectiveCost, character, ultimateChaos, cardType, tags);

    private static bool RaiseNumericRewardsToTopRarityFloorCore(IList<GeneratorOperation> operations,
        GeneratedRarity rarity, double effectiveCost, GeneratedCharacter? character,
        bool ultimateChaos, GeneratedCardType cardType, IReadOnlyCollection<CardTag>? tags)
    {
        // Preserve the selected component assembly. Rejecting a low roll and starting over makes easy conditions
        // and naturally high-valued effect families overrepresented in the final pool. Instead, proportionally
        // raise an existing positive numeric field; only genuinely nonnumeric assemblies still need to be rerolled.
        for (var attempt = 0; attempt < 32; attempt++)
        {
            if (!EffectBalanceModel.TryMeasureTopRarityCardValue(operations.ToArray(), rarity, effectiveCost,
                    out var actual, out var required, tags)
                || actual >= required)
                return true;

            var fixedKeywordValue = EffectBalanceModel.EstimatedPositiveKeywordValue(tags);
            var scalableValue = Math.Max(1d, actual - fixedKeywordValue);
            var remainingRequired = Math.Max(0d, required - fixedKeywordValue);
            var scale = Math.Clamp(remainingRequired / scalableValue * 1.01d, 1.05d, 4d);
            var changed = false;
            for (var operationIndex = 0; operationIndex < operations.Count; operationIndex++)
            {
                var operation = operations[operationIndex];
                if (CardEffectRules.IsNegativeEffect(operation)) continue;
                var atom = new ComponentAtom(operation.Template, operation.Scope, operation.ChineseText,
                    operation.RequiresSingleTarget, CardReferenceRequirement.None)
                    { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(operation) };
                if (!EffectBalanceModel.IsScalableReward(atom)) continue;

                var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
                if (spec.Flags.Contains("percentage_value")) continue;
                var numericSlots = OperationRuntimeSpecCompiler.ExplicitFixedValueSlots(operation);
                for (var slot = 0; slot < numericSlots.Count; slot++)
                {
                    var numericSlot = numericSlots[slot];
                    var current = numericSlot.BaseValue + numericSlot.Offset;
                    if (current <= 0) continue;
                    var proposed = Math.Max(current + 1,
                        (int)Math.Ceiling(current * scale));
                    if (cardType != GeneratedCardType.Power
                        && tags?.Contains(CardTag.Exhaust) != true
                        && CardEffectRules.IsStackablePersistentCombatGain(operation))
                        proposed = Math.Min(proposed, ReusablePersistentGainMaximum(
                            Math.Max(0, (int)Math.Round(effectiveCost, MidpointRounding.AwayFromZero))));
                    var adjusted = NumericGenerationTuning.ClampSampledValue(atom, slot, proposed,
                        operations.Take(operationIndex).ToArray(), character, ultimateChaos);
                    if (adjusted <= current) continue;
                    if (!OperationRuntimeSpecCompiler.TryReplaceFixedValue(operation, numericSlot.Id,
                            adjusted, out var updated))
                        continue;
                    operations[operationIndex] = updated;
                    changed = true;
                    // Scale at most one field of each effect per pass. If that field reaches a semantic cap, a later
                    // pass can move to its next adjustable field (for example the Block half of Draw-and-Block).
                    break;
                }
            }

            if (!changed) return false;
        }

        return EffectBalanceModel.HasAdequateTopRarityCardValue(operations.ToArray(), rarity, effectiveCost, tags);
    }

    private static void ClampReusablePersistentCombatGains(IList<GeneratorOperation> operations,
        GeneratedCardType cardType, IReadOnlyCollection<CardTag> tags, int effectiveCost)
    {
        if (cardType == GeneratedCardType.Power || tags.Contains(CardTag.Exhaust)) return;
        var maximum = ReusablePersistentGainMaximum(effectiveCost);
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (!CardEffectRules.IsStackablePersistentCombatGain(operation)
                || operation.Parameters.ContainsKey("triggerIndex"))
                continue;
            if (!OperationRuntimeSpecCompiler.TryGetPrimaryExplicitFixedValue(operation, out _, out var current)
                || current <= maximum) continue;
            ReplaceFirstNumeric(operations, index, maximum);
        }
    }

    private static int ReusablePersistentGainMaximum(int effectiveCost) => effectiveCost >= 2 ? 2 : 1;

    private static void ClampSelectedSkillReplayCount(IList<GeneratorOperation> operations,
        double effectiveCost, IReadOnlyCollection<CardTag> tags)
    {
        var maximum = double.IsNaN(effectiveCost)
            ? tags.Contains(CardTag.Exhaust) ? 2 : 1
            : effectiveCost >= 3d && tags.Contains(CardTag.Exhaust) ? 3
            : effectiveCost >= 2d ? 2
            : 1;
        for (var index = 0; index < operations.Count; index++)
        {
            if (operations[index].Template != "R:PlaySelectedSkillMultipleTimes") continue;
            var operationMaximum = operations[index].Parameters.ContainsKey("triggerIndex")
                ? Math.Min(1, maximum)
                : maximum;
            var current = OperationRuntimeSpecCompiler.StaticLiteralValue(operations[index], "amount", 1);
            if (current > operationMaximum) ReplaceFirstNumeric(operations, index, operationMaximum);
        }
    }

    private void ClampDurationOnlyStacks(IList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (NumericGenerationTuning.DurationOnlyStackCap(operation, _character, _unlockComponentRoles)
                is not { } cap)
                continue;
            if (!OperationRuntimeSpecCompiler.TryGetPrimaryExplicitFixedValue(operation, out _, out var current)
                || current <= cap) continue;
            ReplaceFirstNumeric(operations, index, cap);
        }
    }

    private static bool RaisePurePositiveCostDrawFloor(IList<GeneratorOperation> operations, double effectiveCost)
    {
        if (effectiveCost <= 0d) return true;
        var rewards = operations.Where(operation => CardEffectRules.IsBeneficialEffect(operation)
                && operation.Scope is not (OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger)
                && !CardEffectRules.IsDependencyPrefix(operation))
            .ToArray();
        if (rewards.Length != 1 || rewards[0].Template is not ("N:Draw" or "N_DRAW" or "N:NextTurnDraw"))
            return true;

        var delayed = CardEffectRules.IsDelayedEffect(rewards[0])
            || rewards[0].Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                && triggerIndex >= 0 && triggerIndex < operations.Count
                && CardEffectRules.IsDelayedEffect(operations[triggerIndex]);
        if (rewards[0].Parameters.ContainsKey("triggerIndex") && !delayed) return true;

        // The ordinary Draw operation is intentionally capped at four to avoid hand-size explosions. A pure
        // three-plus-cost draw card therefore cannot clear an acceptable efficiency floor and should be rerolled.
        if (effectiveCost >= 2.5d) return false;

        var index = operations.IndexOf(rewards[0]);
        if (index < 0 || !OperationRuntimeSpecCompiler.TryGetPrimaryExplicitFixedValue(rewards[0], out _,
                out var current)) return true;
        var minimum = delayed
            ? effectiveCost <= 1.25d ? 3 : 4
            : effectiveCost <= 1.25d ? 2 : 4;
        if (current >= minimum) return true;
        ReplaceFirstNumeric(operations, index, minimum);
        return true;
    }

    private void EnforceBasicDelayedPayoffFloor(IList<GeneratorOperation> operations,
        GeneratedRarity rarity, int effectiveCost)
    {
        if (rarity != GeneratedRarity.Basic || effectiveCost < 0) return;
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            var delayed = CardEffectRules.IsDelayedEffect(operation)
                || operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                    && triggerIndex >= 0 && triggerIndex < index
                    && CardEffectRules.IsDelayedEffect(operations[triggerIndex]);
            if (!delayed) continue;

            int minimum;
            if (operation.Template is "N:B" or "N:NextTurnBlock")
                minimum = Math.Max(2, BlockBudget(rarity, effectiveCost, BalanceCharacter) + 2);
            else if (operation.Template is "N:Draw" or "N_DRAW" or "N:NextTurnDraw")
                minimum = Math.Max(2, DrawBudget(rarity, effectiveCost) + 1);
            else
                continue;

            if (!OperationRuntimeSpecCompiler.TryGetPrimaryExplicitFixedValue(operation, out _, out var current)
                || current >= minimum) continue;
            ReplaceFirstNumeric(operations, index, minimum);
        }
    }

    private bool ApplySingleEffectAncientBonus(IList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Template is "N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK"
                || CardEffectRules.IsNegativeEffect(operation))
                continue;
            var atom = new ComponentAtom(operation.Template, operation.Scope, operation.ChineseText,
                operation.RequiresSingleTarget, CardReferenceRequirement.None)
                { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(operation) };
            if (!EffectBalanceModel.IsScalableReward(atom)) continue;
            if (!OperationRuntimeSpecCompiler.TryGetPrimaryExplicitFixedValue(operation, out _, out var current))
                continue;
            var proposed = Math.Max(current + 1,
                (int)Math.Round(current * 1.10d, MidpointRounding.AwayFromZero));
            var adjusted = _valuePolicy.ClampSampledValue(atom, 0, proposed,
                operations.Take(index).ToArray(), _character, _unlockComponentRoles);
            if (adjusted <= current) return false;
            ReplaceFirstNumeric(operations, index, adjusted);
            return true;
        }
        return false;
    }

    internal GeneratedCard GenerateEmergencyFallback(GeneratedRarity rarity, IroncladCardRecipe shell,
        int uniquenessBoost = 0, bool raiseAggressiveEffectFloor = false,
        bool wantsNonBasicStarPayment = false)
    {
        var type = shell.Type;
        var target = shell.Target;
        var cost = _specialXMode == SpecialXGenerationMode.Forced ? 2 : 1;
        var starCost = wantsNonBasicStarPayment
            ? NumericGenerationTuning.SampleNonBasicFixedStarCost(_random, shell.StarCost, rarity)
            : -1;
        var operation = CreateCatalogEmergencyOperation(ref type, ref target, cost,
            requireSpecialXConvertible: _specialXMode == SpecialXGenerationMode.Forced);

        if (_specialXMode != SpecialXGenerationMode.Forced)
            operation = RaiseEmergencyFallbackToRarityFloor(operation, rarity,
                cost + CardEffectRules.StarCostEnergyEquivalent(starCost));
        if (_specialXMode != SpecialXGenerationMode.Forced && rarity == GeneratedRarity.Ancient)
        {
            var boosted = new List<GeneratorOperation> { operation };
            if (ApplySingleEffectAncientBonus(boosted)) operation = boosted[0];
        }
        if (_specialXMode != SpecialXGenerationMode.Forced && uniquenessBoost > 0
            && OperationRuntimeSpecCompiler.TryGetPrimaryExplicitFixedValue(operation, out _, out var current)
            && OperationRuntimeSpecCompiler.TryReplacePrimaryExplicitFixedValue(operation,
                current + uniquenessBoost, out var uniqueOperation))
            operation = uniqueOperation;

        var fallbackOperations = new List<GeneratorOperation> { operation };
        if (_specialXMode != SpecialXGenerationMode.Forced && uniquenessBoost > 0)
        {
            // Exact/template duplicate rejection ignores numeric differences. Once a one-line emergency card has
            // already been used, increasing that line's number cannot make the template unique; rotate through
            // legal profile-owned complements so a dense pool still terminates without weakening uniqueness.
            if (TryCreateCatalogComplement(type, target, cost, fallbackOperations,
                    2, uniquenessBoost - 1) is { } complement)
                fallbackOperations.Add(complement);
        }
        if (raiseAggressiveEffectFloor)
        {
            // Preserve the card-level lower-bound roll on the emergency path, while keeping external profiles
            // independent from built-in component IDs.
            if (TryCreateCatalogComplement(type, target, cost, fallbackOperations, 3) is { } complement)
                fallbackOperations.Add(complement);
        }
        if (_specialXMode == SpecialXGenerationMode.Forced && uniquenessBoost > 0)
        {
            if (TryCreateCatalogComplement(type, target, cost, fallbackOperations,
                    2 + uniquenessBoost) is { } complement)
                fallbackOperations.Add(complement);
        }
        GeneratedCard card = new(cost, type, target, rarity,
            CardDescriptionRenderer.Render(fallbackOperations), Array.Empty<CardTag>(), fallbackOperations,
            EnglishDescription: EnglishCardDescriptionRenderer.Render(fallbackOperations), Character: _character,
            StarCost: starCost, UnifiedChaos: _unlockComponentRoles);
        card = SpecialXCardConverter.Convert(card, _random, _specialXMode);
        if (_specialXMode == SpecialXGenerationMode.Forced && !SpecialXCardConverter.IsSpecial(card))
            throw new InvalidOperationException("Emergency fallback could not produce the required special-X card.");
        // A two-line aggressive fallback exposes more legal upgrade candidates than the historical one-line
        // fallback. Reroll only its upgrade plan when a candidate violates a whole-card upgrade invariant; never
        // discard the already selected card-level density branch.
        string? lastUpgradeError = null;
        for (var upgradeAttempt = 0; upgradeAttempt < 64; upgradeAttempt++)
        {
            var upgrade = CardUpgradeGenerator.Generate(card, _random, _unlockComponentRoles, _profileId,
                _keywordPolicy);
            var upgradedCard = card with { Upgrade = upgrade };
            try
            {
                CardTemplateValidator.Validate(upgradedCard);
                return upgradedCard;
            }
            catch (InvalidOperationException exception)
            {
                // Try another independently legal upgrade candidate for this emergency-only card.
                lastUpgradeError = exception.Message;
            }
        }
        for (var operationIndex = 0; operationIndex < card.Operations.Count; operationIndex++)
        {
            var fallbackOperation = card.Operations[operationIndex];
            if (CardEffectRules.IsNegativeEffect(fallbackOperation)
                || CardEffectRules.IsNonUpgradeableNumericMarker(fallbackOperation)
                || !OperationRuntimeSpecCompiler.TryGetFixedUpgradeValue(fallbackOperation, out var slotId, out _)
                || slotId is null)
                continue;
            var effect = new CardUpgradeEffect(CardUpgradeKind.IncreaseNumber,
                operationIndex, 1, slotId);
            var upgradedOperations = CardUpgradeGenerator.ApplyEffectsToOperations(card.Operations, [effect]);
            var minimalUpgrade = new CardUpgradePlan(card.Cost, [effect],
                CardDescriptionRenderer.Render(upgradedOperations), Array.Empty<CardTag>(),
                EnglishCardDescriptionRenderer.Render(upgradedOperations), Array.Empty<CardTag>());
            var minimallyUpgradedCard = card with { Upgrade = minimalUpgrade };
            try
            {
                CardTemplateValidator.Validate(minimallyUpgradedCard);
                return minimallyUpgradedCard;
            }
            catch (InvalidOperationException exception)
            {
                lastUpgradeError = exception.Message;
            }
        }
        throw new InvalidOperationException("Emergency fallback could not produce a legal upgrade plan: "
                                            + CardDescriptionRenderer.Render(fallbackOperations) + " / "
                                            + lastUpgradeError);
    }

    /// <summary>
    /// Builds the bounded last-resort card from the active profile itself. Older code used Ironclad's Strike,
    /// Defend and Inflame RuntimeSpecs here, which made an otherwise complete external character package depend on
    /// another character's catalog when ordinary assembly exhausted its retry budget.
    /// </summary>
    private GeneratorOperation CreateCatalogEmergencyOperation(ref GeneratedCardType type, ref TargetMode target,
        int cost, bool requireSpecialXConvertible)
    {
        IEnumerable<ComponentAtom> candidates = EmergencyCandidates()
            // Emergency cards currently carry no generated keyword set. Do not select a pure immediate refund
            // whose only legal shell would require Sly; choose another profile-owned positive component instead.
            .Where(atom => !SlyKeywordTuning.IsPureImmediateSelfRefund(cost,
                [CreateCatalogOperation(atom)]));

        bool FitsType(ComponentAtom atom, GeneratedCardType candidateType) => candidateType switch
        {
            GeneratedCardType.Attack => CardEffectRules.HasAttackClassifyingDamage(
                [CreateCatalogOperation(atom)]),
            GeneratedCardType.Skill => !CardEffectRules.HasAttackClassifyingDamage(
                    [CreateCatalogOperation(atom)])
                && atom.Scope is not (OperationScope.AbilityTrigger or OperationScope.AbilityRule),
            GeneratedCardType.Power => CardEffectRules.IsPersistentPowerFoundation(atom)
                && atom.Scope is not (OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger),
            _ => false
        };

        var requestedType = type;
        var selected = candidates
            .Where(atom => FitsType(atom, requestedType))
            .Where(atom => HasValidOperationAssembly([CreateCatalogOperation(atom)]))
            .OrderBy(atom => atom.SemanticId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (selected is null && requireSpecialXConvertible)
        {
            // Special-X is a quota mechanism, not part of an external profile's required authoring surface. If the
            // sampled shell cannot expose a convertible field, use the profile's simplest legal Skill/Attack field
            // and derive the printed type and target from that operation.
            selected = candidates
                .Where(atom => FitsType(atom, GeneratedCardType.Skill)
                    || FitsType(atom, GeneratedCardType.Attack))
                .Where(atom => HasValidOperationAssembly([CreateCatalogOperation(atom)]))
                .OrderBy(atom => atom.SemanticId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (selected is not null)
                type = FitsType(selected, GeneratedCardType.Attack)
                    ? GeneratedCardType.Attack : GeneratedCardType.Skill;
        }
        if (selected is null)
            throw new InvalidOperationException($"Profile {_profileId} has no standalone positive {type} "
                                                + "component suitable for emergency generation.");

        target = type == GeneratedCardType.Power || !CardEffectRules.RequiresSingleEnemyTarget(selected)
            ? TargetMode.Other : TargetMode.SingleEnemy;
        var operation = CreateCatalogOperation(selected);
        if (requireSpecialXConvertible)
        {
            if (!OperationRuntimeSpecCompiler.TryGetPrimaryExplicitFixedValue(operation, out var slotId,
                    out _)
                || slotId is null
                || !OperationRuntimeSpecCompiler.TryReplaceFixedValue(operation, slotId, cost,
                    out operation))
                throw new InvalidOperationException($"Profile {_profileId} has no Special-X convertible fallback.");
        }
        return operation;
    }

    private IEnumerable<ComponentAtom> EmergencyCandidates() => _componentCatalog.Atoms
            .Where(atom => atom.CardReference == CardReferenceRequirement.None
                && atom.Scope is OperationScope.SingleEnemyOnly or OperationScope.NonTargeted
                && (atom.LocalizedText is not null || ComponentLocalizationApi.TryGet(atom.SemanticId, out _))
                && !DerivativeSlotCatalog.IsSlotOperation(atom.Template)
                && !OrbSlotCatalog.IsSlotOperation(atom.Template)
                && !CardEffectRules.IsNegativeEffect(atom)
                && !CardEffectRules.IsRestrictedEffect(atom)
                && !CardEffectRules.IsSelfCardMovementOrReplay(atom)
                && !CardEffectRules.TriggerNeedsLinkedEffect(atom)
                && !CardEffectRules.IsDependencyPrefix(atom)
                && !CardEffectRules.RequiresDependencyPrefix(atom)
                && !CardEffectRules.RequiresSpecificTriggerPayload(atom)
                && !CardEffectRules.IsHitEnemyDamageVariant(atom)
                && CardEffectRules.XRequirement(atom) == XResourceRequirement.None
                && CardEffectRules.IsBeneficialEffect(atom)
                && OperationRuntimeSpecCompiler.ExplicitFixedValueSlots(atom).Any(slot => slot.Upgradable));

    private GeneratorOperation? TryCreateCatalogComplement(GeneratedCardType type, TargetMode target, int cost,
        IReadOnlyList<GeneratorOperation> existing, int preferredValue, int candidateOrdinal = 0)
    {
        var candidates = EmergencyCandidates()
            .Where(atom => ComponentPolicy.PoolUniqueKey(atom) is not { } uniqueKey
                || _usedPoolUniqueComponents?.Contains(uniqueKey) != true)
            .Where(atom => type != GeneratedCardType.Skill
                || !CardEffectRules.HasAttackClassifyingDamage([CreateCatalogOperation(atom)]))
            .Where(atom => type != GeneratedCardType.Power
                || atom.Scope is not (OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger))
            .Where(atom => target == TargetMode.SingleEnemy
                || !CardEffectRules.RequiresSingleEnemyTarget(atom))
            .Where(atom => !existing.Any(operation => OperationRuntimeSpecCompiler.StructuralFieldKey(operation)
                == OperationRuntimeSpecCompiler.StructuralFieldKey(atom)))
            .Select(CreateCatalogOperation)
            .Where(operation => HasValidOperationAssembly(existing.Append(operation).ToArray()))
            .OrderBy(operation => OperationRuntimeSpecCompiler.StructuralFieldKey(operation),
                StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0) return null;
        var candidate = candidates[Math.Abs(candidateOrdinal % candidates.Length)];
        return OperationRuntimeSpecCompiler.TryGetPrimaryExplicitFixedValue(candidate, out var slotId, out _)
               && slotId is not null
               && OperationRuntimeSpecCompiler.TryReplaceFixedValue(candidate, slotId, preferredValue,
                   out var adjusted)
            ? adjusted
            : candidate;
    }

    /// <summary>
    /// Removes low-information one-shot combat lines after the first whole-card budget reconciliation. If removal
    /// drops the card below its rarity floor or changes its required Attack/target shape, first try to refill the
    /// released slot with another legal component from the active profile and reconcile the shared budget again.
    /// Failure to find a fitting replacement rejects this assembly so the outer generator can reroll it. This is a
    /// 90% attempt, not a hard ban; exact native-shaped assemblies remain reachable through the surviving 10% path.
    /// </summary>
    private bool TryPruneTinyStandaloneCombatRewards(IList<GeneratorOperation> operations,
        IReadOnlyList<GeneratorOperation> preSynergyOperations, GeneratedRarity rarity, double effectiveCost,
        GeneratedCardType cardType, TargetMode target, IReadOnlyCollection<CardTag> tags,
        bool hasPrintedResourceCost, bool applySynergyPenalties, bool skipUpperBound)
    {
        var original = operations.ToArray();
        var removedIndices = new List<int>();
        for (var index = operations.Count - 1; index >= 0; index--)
        {
            if (!CardAcceptanceTuning.IsTinyStandaloneCombatReward(
                    operations as IReadOnlyList<GeneratorOperation> ?? operations.ToArray(), index)
                || !OperationRuntimeSpecCompiler.TryGetPrimaryExplicitFixedValue(operations[index], out _,
                    out var amount)
                || !CardAcceptanceTuning.ShouldAttemptTinyStandaloneRemoval(amount, _random.Next(100)))
                continue;
            if (!TryRemoveOperationAndReindexTriggers(operations, index)) continue;
            removedIndices.Add(index);
        }
        if (removedIndices.Count == 0) return true;

        bool NeedsReplacement(IReadOnlyList<GeneratorOperation> candidate) =>
            !HasGeneratedCardShape(candidate, cardType, target)
            || !candidate.Any(CardEffectRules.IsBeneficialEffect)
            || !HasPostSynergyWholeCardBudgetFloor(candidate, rarity, effectiveCost, cardType, tags,
                hasPrintedResourceCost, _balancedValues, _character, _unlockComponentRoles);

        if (!NeedsReplacement(operations as IReadOnlyList<GeneratorOperation> ?? operations.ToArray()))
            return true;

        // Existing fields have already received their synergy surcharge. Rebuilding from that state and applying
        // the surcharge again would double-penalize them. Recreate the reduced assembly from the pre-synergy
        // snapshot, add one replacement, then run the ordinary envelope and synergy pass exactly once.
        var reduced = preSynergyOperations.ToList();
        foreach (var removedIndex in removedIndices.OrderDescending())
            if (!TryRemoveOperationAndReindexTriggers(reduced, removedIndex))
                return false;
        for (var ordinal = 0; ordinal < 32; ordinal++)
        {
            var complement = TryCreateCatalogComplement(cardType, target,
                Math.Max(0, (int)Math.Round(effectiveCost, MidpointRounding.AwayFromZero)), reduced,
                preferredValue: 3, candidateOrdinal: ordinal);
            if (complement is null) break;
            var candidate = reduced.Append(complement).ToList();
            if (!HasGeneratedCardShape(candidate, cardType, target)
                || !candidate.Any(CardEffectRules.IsBeneficialEffect)
                || !HasValidOperationAssembly(candidate))
                continue;
            if (!ApplyWholeCardBudgetEnvelope(candidate, rarity, effectiveCost, cardType, tags,
                    hasPrintedResourceCost, _balancedValues, _character, _unlockComponentRoles,
                    skipUpperBound)
                || applySynergyPenalties && !ApplyWholeCardSynergyPenalties(candidate)
                || !HasGeneratedCardShape(candidate, cardType, target)
                || !HasValidOperationAssembly(candidate)
                || !HasPostSynergyWholeCardBudgetFloor(candidate, rarity, effectiveCost, cardType, tags,
                    hasPrintedResourceCost, _balancedValues, _character, _unlockComponentRoles))
                continue;
            // Do not replace one tiny combat line with another, or let the replacement's synergy surcharge push a
            // different ordinary one-shot field down into the same presentation problem.
            if (candidate.Select((_, index) => index)
                .Any(index => CardAcceptanceTuning.IsTinyStandaloneCombatReward(candidate, index)))
                continue;
            operations.Clear();
            foreach (var operation in candidate) operations.Add(operation);
            return true;
        }

        operations.Clear();
        foreach (var operation in original) operations.Add(operation);
        return false;
    }

    private static bool TryRemoveOperationAndReindexTriggers(IList<GeneratorOperation> operations,
        int removedIndex)
    {
        if ((uint)removedIndex >= (uint)operations.Count
            || operations.Any(operation => operation.Parameters.GetValueOrDefault("triggerIndex", -1)
                == removedIndex))
            return false;
        var candidate = operations.Where((_, index) => index != removedIndex).Select(operation =>
        {
            if (!operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                || triggerIndex < removedIndex)
                return operation;
            var parameters = new Dictionary<string, int>(operation.Parameters)
            {
                ["triggerIndex"] = triggerIndex - 1
            };
            return operation with { Parameters = parameters };
        }).ToArray();
        if (!HasValidOperationAssembly(candidate)) return false;
        operations.Clear();
        foreach (var operation in candidate) operations.Add(operation);
        return true;
    }

    private static bool HasGeneratedCardShape(IReadOnlyList<GeneratorOperation> operations,
        GeneratedCardType expectedType, TargetMode expectedTarget)
    {
        var actualType = expectedType == GeneratedCardType.Power
            ? GeneratedCardType.Power
            : CardEffectRules.HasAttackClassifyingDamage(operations)
                ? GeneratedCardType.Attack : GeneratedCardType.Skill;
        if (actualType != expectedType) return false;
        if (expectedType == GeneratedCardType.Power
            && !operations.Any(CardEffectRules.IsPersistentPowerFoundation)) return false;
        var actualTarget = expectedType == GeneratedCardType.Power
            ? TargetMode.Other
            : operations.Any(CardEffectRules.RequiresSingleEnemyTarget)
                ? TargetMode.SingleEnemy : TargetMode.Other;
        return actualTarget == expectedTarget;
    }

    private static GeneratorOperation CreateCatalogOperation(ComponentAtom atom)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(atom);
        var localized = LocalizedText(atom);
        return new GeneratorOperation(
            atom.Template,
            atom.Scope,
            localized.RenderChinese(spec),
            new Dictionary<string, int>(),
            RequiresSingleTarget: atom.RequiresSingleTarget,
            RuntimeSpec: spec,
            LocalizedText: localized,
            LocalizationId: ComponentLocalizationApi.TryGet(atom.SemanticId, out _) ? atom.SemanticId : null);
    }

    private static OperationLocalizedText LocalizedText(ComponentAtom atom) =>
        atom.LocalizedText
        ?? (ComponentLocalizationApi.TryGet(atom.SemanticId, out var registered)
            ? registered
            : throw new InvalidOperationException($"Component {atom.SemanticId} has no localization template."));

    private static GeneratorOperation RaiseEmergencyFallbackToRarityFloor(GeneratorOperation operation,
        GeneratedRarity rarity, int effectiveCost)
    {
        // Emergency cards are intentionally simple, but they must not bypass the same top-rarity quality floor as
        // normal assemblies. Increment the one measurable reward until it clears the floor; this path is extremely
        // rare and exists only after all ordinary assembly retries fail.
        for (var attempt = 0; attempt < 100
             && !EffectBalanceModel.HasAdequateTopRarityCardValue([operation], rarity, effectiveCost); attempt++)
        {
            if (!OperationRuntimeSpecCompiler.TryGetPrimaryExplicitFixedValue(operation, out _, out var current)
                || !OperationRuntimeSpecCompiler.TryReplacePrimaryExplicitFixedValue(operation, current + 1,
                    out operation)) break;
        }
        return operation;
    }

    private bool CanAnchorRestrictedPowerFoundation(ComponentAtom atom)
    {
        if (atom.Template == "N:B"
            && _componentCatalog.Atoms.Any(candidate => candidate.Template == "D:IncreaseThisCardBlockRun"))
            return true;
        return CardEffectRules.IsEnemyDamage(atom)
            && _componentCatalog.Atoms.Any(candidate => candidate.Template == "NCR:IncreaseThisCardDamageRun");
    }

    private static void ApplyOrbBudgetCompensation(IList<GeneratorOperation> operations,
        GeneratedRarity rarity, int effectiveCost)
    {
        var orbIndices = operations.Select((operation, index) => (operation, index))
            .Where(item => CardEffectRules.IsDirectOrbChannel(item.operation)
                && !item.operation.Parameters.ContainsKey("triggerIndex"))
            .Select(item => item.index).ToArray();
        if (orbIndices.Length == 0) return;

        var scalableIndices = operations.Select((operation, index) => (operation, index))
            .Where(item => !orbIndices.Contains(item.index)
                && !item.operation.Parameters.ContainsKey("triggerIndex")
                && !CardEffectRules.IsNegativeEffect(item.operation)
                && EffectBalanceModel.IsScalableReward(new ComponentAtom(item.operation.Template,
                    item.operation.Scope, item.operation.ChineseText, item.operation.RequiresSingleTarget,
                    CardReferenceRequirement.None)
                    { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(item.operation) }))
            .Select(item => item.index).ToArray();
        var rewardLineCount = orbIndices.Length + scalableIndices.Length;
        if (rewardLineCount == 0) return;

        // Channel and any companion rewards share the same whole-card allowance. The former sqrt(line count)
        // grant made mixed cards systematically stronger than a native card of the same rarity and cost.
        var target = CalibratedWholeCardCenter(rarity, effectiveCost);

        // Discrete Channel counts cannot be scaled smoothly. If the Orb payload alone consumes most of the card,
        // lower counts above one before squeezing an unrelated Damage/Block line down to a token value.
        while (orbIndices.Sum(index => EffectBalanceModel.EstimatedEffectValue(operations[index]))
               > target * 0.72d)
        {
            var reducible = orbIndices
                .Select(index => (Index: index, Count: OperationRuntimeSpecCompiler.StaticLiteralValue(
                        operations[index], "amount", 1),
                    UnitValue: OrbSlotCatalog.ChannelValue(operations[index].OrbOutputId,
                        operations[index].Template)))
                .Where(item => item.Count > 1)
                .OrderByDescending(item => item.UnitValue).FirstOrDefault();
            if (reducible.Count <= 1) break;
            ReplaceFirstNumeric(operations, reducible.Index, reducible.Count - 1);
        }

        var orbValue = orbIndices.Sum(index => EffectBalanceModel.EstimatedEffectValue(operations[index]));
        var scalableValue = scalableIndices.Sum(index => EffectBalanceModel.EstimatedEffectValue(operations[index]));
        if (scalableValue <= 0d || orbValue + scalableValue <= target) return;
        var scale = Math.Clamp((target - orbValue) / scalableValue, 0.08d, 1d);
        foreach (var index in scalableIndices)
        {
            var operation = operations[index];
            if (!OperationRuntimeSpecCompiler.TryGetPrimaryExplicitFixedValue(operation, out _, out var current))
                continue;
            var adjusted = Math.Max(1, (int)Math.Round(current * scale, MidpointRounding.AwayFromZero));
            ReplaceFirstNumeric(operations, index, adjusted);
        }
    }

    /// <summary>
    /// Tear Asunder's lifetime repeat counter is neither a one-shot extra hit nor an independent reward line.
    /// Price it at three HP-loss events: a printed +N hits per event therefore contributes 3N expected extra
    /// hits. Reducing the host's per-hit amount preserves the ordinary damage budget while still leaving the
    /// card's substantial combat-scaling upside intact. Exact native reconstruction bypasses this pass.
    /// </summary>
    private static void ApplyLostHpRepeatDamagePricing(IList<GeneratorOperation> operations)
    {
        var modifier = operations.FirstOrDefault(operation =>
            operation.Template == "M:repeat"
            && OperationRuntimeSpecCompiler.GetOrCompile(operation).Variant == "hp_loss_scaled");
        if (modifier is null) return;

        var damageIndex = operations.Select((operation, index) => (operation, index))
            .Where(item => CardEffectRules.IsEnemyDamage(item.operation))
            .Select(item => item.index)
            .SingleOrDefault(-1);
        if (damageIndex < 0) return;

        var expectedExtraHits = EffectBalanceModel.ExpectedExtraDamageHits(modifier);
        if (expectedExtraHits <= 0) return;
        ScaleStructuredAmount(operations, damageIndex, "damage", 1d / (1d + expectedExtraHits));
    }

    private static void ReplaceFirstNumeric(IList<GeneratorOperation> operations, int index, int adjusted)
    {
        var operation = operations[index];
        if (OperationRuntimeSpecCompiler.TryReplacePrimaryExplicitFixedValue(operation, adjusted,
                out var updated))
            operations[index] = updated;
    }

    private void ApplyUnconditionalStarGainSoftCap(IList<GeneratorOperation> operations,
        IReadOnlyCollection<CardTag> tags)
    {
        if (tags.Contains(CardTag.Exhaust)) return;
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (!CardEffectRules.IsStarGainOperation(operation)
                || operation.Parameters.ContainsKey("triggerIndex")
                || !OperationRuntimeSpecCompiler.TryGetPrimaryExplicitFixedValue(operation, out _, out var amount))
                continue;
            var adjusted = NumericGenerationTuning.ApplyUnconditionalStarGainSoftCap(_random, amount);
            if (adjusted != amount
                && OperationRuntimeSpecCompiler.TryReplacePrimaryExplicitFixedValue(operation, adjusted,
                    out var updated))
                operations[index] = updated;
        }
    }

    /// <summary>
    /// Reconciles independently sampled component numbers against one card-level budget. The reference is the
    /// native-card rarity/cost center, including fractional effective cost. Every positive field shares that single
    /// allowance: adding Damage, Block or another reward no longer creates budget. Explicit downsides and one-shot
    /// Power text retain their established payment premiums. Balanced mode is the canonical range; aggressive mode
    /// keeps the same floor and raises only its upper edge by 50%.
    /// </summary>
    internal static bool ApplyWholeCardBudgetEnvelope(IList<GeneratorOperation> operations,
        GeneratedRarity rarity, double effectiveCost, GeneratedCardType cardType,
        IReadOnlyCollection<CardTag> tags, bool hasPrintedResourceCost, bool balancedValues,
        GeneratedCharacter character,
        bool ultimateChaos, bool skipUpperBound = false)
    {
        // NaN denotes an ordinary X-cost shell. A fixed card whose refunds push effective cost below zero is not
        // unbounded: it still consumes a draw and must fit the zero-cost envelope (and normally needs downsides).
        if (double.IsNaN(effectiveCost)) return true;
        var distinctRewards = EffectBalanceModel.PositiveRewardFieldCount(operations.ToArray(),
            hasPrintedResourceCost, cardType, tags);
        var fixedKeywordValue = EffectBalanceModel.EstimatedPositiveKeywordValue(tags);
        if (distinctRewards == 0 && fixedKeywordValue <= 0d) return true;
        // Field count is retained for diagnostics/API compatibility only; it never expands the card's allowance.
        // Retain and every other fixed reward consume the same shared whole-card budget.
        var budgetRewardFields = Math.Max(1, distinctRewards);

        var downsidePercent = CardEffectRules.NegativeEffectCompensationPercent(operations.ToArray(), tags,
            hasPrintedResourceCost, cardType, ultimateChaos ? null : character);
        var downsideFlatValue = CardEffectRules.NegativeEffectLinearCompensationValue(operations.ToArray());
        var powerFactor = PowerOneShotBudgetFactor(operations.ToArray(), cardType);
        var (minimum, maximum) = WholeCardBudgetBounds(rarity, effectiveCost, budgetRewardFields,
            downsidePercent, powerFactor, balancedValues, character, downsideFlatValue);
        // Adaptive Strike's free discard copy is not an independent fixed-value rider: it schedules a later free
        // use of the card's complete payload. Price the payload itself halfway between its printed-cost curve and
        // the equivalent zero-cost curve, matching the paid copy and its future free copy without double-counting.
        if (operations.Any(CardEffectRules.IsZeroCostCopyThisCardToDiscard))
        {
            var zeroCostBounds = WholeCardBudgetBounds(rarity, 0d, budgetRewardFields,
                downsidePercent, powerFactor, balancedValues, character, downsideFlatValue);
            minimum = (minimum + zeroCostBounds.Minimum) / 2d;
            maximum = (maximum + zeroCostBounds.Maximum) / 2d;
        }

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var actual = EffectBalanceModel.EstimatedPositiveCardValue(operations.ToArray(),
                hasPrintedResourceCost, cardType, tags);
            // The curse-in-status-slot Easter egg deliberately creates exceptional outliers. It still has to clear
            // the ordinary rarity/cost floor, but its authored extra lines, cost discount and 1.75x numeric boost
            // must not be normalized back down by the ordinary whole-card ceiling.
            if (actual >= minimum && (skipUpperBound || actual <= maximum)) return true;
            var target = actual < minimum ? minimum * 1.01d : maximum * 0.99d;
            var scalableValue = Math.Max(1d, actual - fixedKeywordValue);
            var remainingTarget = Math.Max(0d, target - fixedKeywordValue);
            var scale = Math.Clamp(remainingTarget / scalableValue, 0.20d, 4d);
            var changed = ScaleWholeCardNumericRewards(operations, scale, actual < minimum,
                character, ultimateChaos);
            if (!changed)
            {
                // Fixed rule/proxy effects cannot be squeezed without changing semantics. Exact native assemblies
                // bypass this envelope before reaching here; a non-native irreducible card outside its budget must
                // therefore be rerolled instead of silently treating “too strong to scale” as a successful result.
                return false;
            }
        }

        var finalValue = EffectBalanceModel.EstimatedPositiveCardValue(operations.ToArray(),
            hasPrintedResourceCost, cardType, tags);
        return finalValue >= minimum && (skipUpperBound || finalValue <= maximum);
    }

    /// <summary>Non-mutating final invariant for post-envelope adjustments.</summary>
    internal static bool IsWithinWholeCardBudgetEnvelope(IReadOnlyList<GeneratorOperation> operations,
        GeneratedRarity rarity, double effectiveCost, GeneratedCardType cardType,
        IReadOnlyCollection<CardTag> tags, bool hasPrintedResourceCost, bool balancedValues,
        GeneratedCharacter character, bool ultimateChaos, bool skipUpperBound = false)
    {
        if (double.IsNaN(effectiveCost)) return true;
        var distinctRewards = EffectBalanceModel.PositiveRewardFieldCount(operations,
            hasPrintedResourceCost, cardType, tags);
        var fixedKeywordValue = EffectBalanceModel.EstimatedPositiveKeywordValue(tags);
        if (distinctRewards == 0 && fixedKeywordValue <= 0d) return true;
        var downsidePercent = CardEffectRules.NegativeEffectCompensationPercent(operations, tags,
            hasPrintedResourceCost, cardType, ultimateChaos ? null : character);
        var downsideFlatValue = CardEffectRules.NegativeEffectLinearCompensationValue(operations);
        var powerFactor = PowerOneShotBudgetFactor(operations, cardType);
        var (minimum, maximum) = WholeCardBudgetBounds(rarity, effectiveCost,
            Math.Max(1, distinctRewards), downsidePercent, powerFactor, balancedValues, character,
            downsideFlatValue);
        if (operations.Any(CardEffectRules.IsZeroCostCopyThisCardToDiscard))
        {
            var zeroCostBounds = WholeCardBudgetBounds(rarity, 0d, Math.Max(1, distinctRewards),
                downsidePercent, powerFactor, balancedValues, character, downsideFlatValue);
            minimum = (minimum + zeroCostBounds.Minimum) / 2d;
            maximum = (maximum + zeroCostBounds.Maximum) / 2d;
        }
        var actual = EffectBalanceModel.EstimatedPositiveCardValue(operations,
            hasPrintedResourceCost, cardType, tags);
        return actual >= minimum && (skipUpperBound || actual <= maximum);
    }

    internal static (double Minimum, double Maximum) WholeCardBudgetBounds(GeneratedRarity rarity,
        double effectiveCost, int distinctRewardFields, int downsidePercent = 100,
        double powerFactor = 1d, bool balancedValues = true,
        GeneratedCharacter character = GeneratedCharacter.Ironclad, double downsideFlatValue = 0d)
    {
        if (double.IsNaN(effectiveCost))
            return (0d, double.PositiveInfinity);
        var singleRewardCenter = CalibratedWholeCardCenter(rarity, effectiveCost);
        var center = singleRewardCenter
            * Math.Max(100, downsidePercent) / 100d * Math.Max(1d, powerFactor)
            + Math.Max(0d, downsideFlatValue);
        var (lowerPercent, upperPercent) = rarity switch
        {
            // One-Energy bands stay tightly centered on the shared rarity curve. Rare deliberately uses a narrower
            // upper tail than the earlier 129% envelope; aggressive mode applies its global multiplier afterwards.
            GeneratedRarity.Basic => (0.80d, 1.13d),
            GeneratedRarity.Common => (0.84d, 1.15d),
            GeneratedRarity.Uncommon => (0.88d, 1.18d),
            GeneratedRarity.Rare => (0.85d, 1.20d),
            GeneratedRarity.Ancient => (0.84d, 1.26d),
            _ => (0.80d, 1.20d)
        };
        // Balanced mode is the canonical envelope. Aggressive mode remains a linear 50% expansion of that same
        // envelope, so global balance passes do not need to maintain a second set of rarity-specific bounds.
        upperPercent *= BalancedUpperBoundMultiplier;
        if (!balancedValues) upperPercent *= 1.50d;
        // Every fixed-cost card must clear the same minimum efficiency as Strike 6 / Defend 5. Apply that anchor
        // through the shared nonlinear resource curve instead of a one-Energy-only constant: two and three Energy
        // therefore demand slightly more than two and three times the one-Energy output, while fractional Star
        // costs keep their exact interpolated quality floor.
        var playableFloor = MinimumPlayablePositiveValue(effectiveCost);
        // A net-refund card is already providing value through its resource line. It needs an upper ceiling, but
        // forcing it up to the ordinary zero-cost lower bound can make fixed refund utilities impossible to fit.
        var minimum = effectiveCost < 0d ? 0d : Math.Max(playableFloor, center * lowerPercent);
        return (minimum, center * upperPercent);
    }

    /// <summary>
    /// Whole-card centers fitted offline from the five native character pools. Zero- and one-cost anchors use the
    /// robust center of native cards after trigger/downside/Power normalization. Rare uses its native upper-middle
    /// 1-cost cluster because many Rare proxy/rule effects deliberately have conservative printed-number estimates;
    /// using their raw median would incorrectly put Rare below Uncommon. Above one Energy the already validated
    /// nonlinear resource curve is retained, because native 3/4-cost cells are sparse and dominated by unique rules.
    /// Stars and refunds first convert to fractional effective cost, so the same interpolation applies everywhere.
    /// </summary>
    internal static double CalibratedWholeCardCenter(GeneratedRarity rarity, double effectiveCost)
    {
        var (zeroCost, oneCost) = rarity switch
        {
            // Basic and Ancient zero-cost anchors use the mean zero/one ratio of Common, Uncommon and Rare
            // (about 68.45%) instead of their sparse native zero-cost cells.
            GeneratedRarity.Basic => (410d, 600d),
            GeneratedRarity.Common => (750d, 1_200d),
            GeneratedRarity.Uncommon => (1_000d, 1_400d),
            GeneratedRarity.Rare => (1_360d, 1_900d),
            // Native Ancient has few one-cost samples and a wide rules-heavy cluster. 2,400 keeps its established
            // 20-30 one-cost capstone band while remaining inside that native cluster.
            GeneratedRarity.Ancient => (1_640d, 2_400d),
            _ => (750d, 1_200d)
        };
        if (double.IsNaN(effectiveCost)) return oneCost;
        if (effectiveCost <= 0d) return zeroCost;
        if (effectiveCost <= 1d)
            return zeroCost + (oneCost - zeroCost) * Math.Max(0d, effectiveCost);
        return oneCost * ResourceEconomyModel.BudgetStrength(effectiveCost);
    }

    internal static double MinimumPlayablePositiveValue(double effectiveCost) =>
        double.IsNaN(effectiveCost)
            ? 0d
            : effectiveCost < 0d ? 0d
            : 600d * ResourceEconomyModel.BudgetStrength(effectiveCost);

    internal static bool HasMinimumPlayableWholeCardValue(IReadOnlyList<GeneratorOperation> operations,
        double effectiveCost, GeneratedCardType cardType, IReadOnlyCollection<CardTag> tags,
        bool hasPrintedResourceCost)
    {
        if (double.IsNaN(effectiveCost)) return true;
        return EffectBalanceModel.EstimatedPositiveCardValue(operations, hasPrintedResourceCost, cardType, tags)
            >= MinimumPlayablePositiveValue(effectiveCost);
    }

    internal static bool HasPostSynergyWholeCardBudgetFloor(IReadOnlyList<GeneratorOperation> operations,
        GeneratedRarity rarity, double effectiveCost, GeneratedCardType cardType,
        IReadOnlyCollection<CardTag> tags, bool hasPrintedResourceCost, bool balancedValues,
        GeneratedCharacter character, bool ultimateChaos)
    {
        if (double.IsNaN(effectiveCost)) return true;
        var distinctRewards = EffectBalanceModel.PositiveRewardFieldCount(operations.ToArray(),
            hasPrintedResourceCost, cardType, tags);
        var fixedKeywordValue = EffectBalanceModel.EstimatedPositiveKeywordValue(tags);
        if (distinctRewards == 0 && fixedKeywordValue <= 0d) return true;

        var downsidePercent = CardEffectRules.NegativeEffectCompensationPercent(operations.ToArray(), tags,
            hasPrintedResourceCost, cardType, ultimateChaos ? null : character);
        var downsideFlatValue = CardEffectRules.NegativeEffectLinearCompensationValue(operations.ToArray());
        var powerFactor = PowerOneShotBudgetFactor(operations, cardType);
        var minimum = WholeCardBudgetBounds(rarity, effectiveCost, Math.Max(1, distinctRewards),
            downsidePercent, powerFactor, balancedValues, character, downsideFlatValue).Minimum;
        if (operations.Any(CardEffectRules.IsZeroCostCopyThisCardToDiscard))
        {
            var zeroCostMinimum = WholeCardBudgetBounds(rarity, 0d, Math.Max(1, distinctRewards),
                downsidePercent, powerFactor, balancedValues, character, downsideFlatValue).Minimum;
            minimum = (minimum + zeroCostMinimum) / 2d;
        }

        return EffectBalanceModel.EstimatedPositiveCardValue(operations, hasPrintedResourceCost, cardType, tags)
            >= minimum;
    }

    /// <summary>
    /// X cards bypass the fixed-cost budget envelope because their effective cost is dynamic. Validate the smallest
    /// meaningful payment explicitly so a lone low-value X rider (for example Summon X) cannot pass merely because
    /// its cost coordinate is undefined. Exact native recipes bypass this in the caller and remain reconstructible.
    /// </summary>
    internal static bool HasAdequateOrdinaryXCardValue(IReadOnlyList<GeneratorOperation> operations,
        GeneratedCardType cardType, IReadOnlyCollection<CardTag> tags, bool hasPrintedResourceCost) =>
        EffectBalanceModel.EstimatedPositiveCardValueAtOrdinaryX(operations, resolvedX: 1,
            hasPrintedResourceCost, cardType, tags) >= MinimumPlayablePositiveValue(1d);

    internal static double PowerOneShotBudgetFactor(IReadOnlyList<GeneratorOperation> operations,
        GeneratedCardType cardType)
    {
        if (cardType != GeneratedCardType.Power) return 1d;
        var rewardGroups = operations.Where(IsPositiveCardBudgetOperation)
            .GroupBy(CardEffectRules.FieldKey, StringComparer.Ordinal).ToArray();
        if (rewardGroups.Length == 0) return 1d;
        var immediateGroups = rewardGroups.Count(group => group.Any(operation =>
            !operation.Parameters.ContainsKey("triggerIndex")
            && operation.Scope is not (OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger
                or OperationScope.AbilityRule or OperationScope.Modifier)
            && !CardEffectRules.IsStackablePersistentCombatGain(operation)));
        return 1d + 0.5d * immediateGroups / rewardGroups.Length;
    }

    private static bool ScaleWholeCardNumericRewards(IList<GeneratorOperation> operations, double scale,
        bool increasing, GeneratedCharacter character, bool ultimateChaos) =>
        ScaleWholeCardNumericRewardsCore(operations, scale, increasing, character, ultimateChaos);

    private static bool ScaleWholeCardNumericRewardsCore(IList<GeneratorOperation> operations, double scale,
        bool increasing, GeneratedCharacter character, bool ultimateChaos)
    {
        var changed = false;
        for (var operationIndex = 0; operationIndex < operations.Count; operationIndex++)
        {
            var operation = operations[operationIndex];
            if (!IsPositiveCardBudgetOperation(operation)) continue;
            var atom = new ComponentAtom(operation.Template, operation.Scope, operation.ChineseText,
                operation.RequiresSingleTarget, CardReferenceRequirement.None)
                { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(operation) };
            if (!EffectBalanceModel.IsScalableReward(atom)) continue;
            var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
            if (spec.Flags.Contains("percentage_value")) continue;
            var numericSlots = OperationRuntimeSpecCompiler.ExplicitFixedValueSlots(operation);
            if (numericSlots.Count == 0) continue;
            var operationChanged = false;
            var updatedOperation = operation;
            for (var slot = 0; slot < numericSlots.Count; slot++)
            {
                var numericSlot = numericSlots[slot];
                var current = numericSlot.BaseValue + numericSlot.Offset;
                if (current <= 0) continue;
                var proposed = Math.Max(1, (int)Math.Round(current * scale,
                    MidpointRounding.AwayFromZero));
                if (increasing && proposed <= current) proposed = current + 1;
                if (!increasing && proposed >= current) proposed = Math.Max(1, current - 1);
                var adjusted = NumericGenerationTuning.ClampSampledValue(atom, slot, proposed,
                    operations.Take(operationIndex).ToArray(), character, ultimateChaos);
                if (adjusted == current
                    || !OperationRuntimeSpecCompiler.TryReplaceFixedValue(updatedOperation, numericSlot.Id,
                        adjusted, out updatedOperation))
                    continue;
                operationChanged = true;
            }
            if (!operationChanged) continue;
            operations[operationIndex] = updatedOperation;
            changed = true;
        }
        return changed;
    }

    private static bool IsPositiveCardBudgetOperation(GeneratorOperation operation) =>
        operation.Template is not ("N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK")
        && CardEffectRules.IsBeneficialEffect(operation)
        && !EffectBalanceModel.IsCondition(new ComponentAtom(operation.Template, operation.Scope,
            operation.ChineseText, operation.RequiresSingleTarget, CardReferenceRequirement.None)
            { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(operation) });

    /// <summary>
    /// Prices the two remaining broad numeric synergies after every positive field has already shared one whole-card
    /// allowance. Mixed Damage/Block receives only a mild flexibility surcharge; very large total Block retains its
    /// rising surcharge. More specific Poison, selected-Skill replay and permanent-stat pairing penalties were
    /// intentionally removed: their individual effect values and the shared allowance are now their complete price.
    /// </summary>
    private static bool ApplyWholeCardSynergyPenalties(IList<GeneratorOperation> operations)
    {
        var damageIndices = operations.Select((operation, index) => (operation, index))
            .Where(item => CardEffectRules.PrintedDamageValueSlot(item.operation) is not null)
            .Select(item => item.index).ToArray();
        var blockIndices = operations.Select((operation, index) => (operation, index))
            .Where(item => CardEffectRules.PrintedBlockValueSlot(item.operation) is not null)
            .Select(item => item.index).ToArray();

        // Static extra-hit modifiers multiply every host damage point, so the flexibility surcharge must price
        // the complete damage package rather than only the printed base-damage lines.
        var damageValue = EffectBalanceModel.EstimatedDamagePackageValue(operations.ToArray());
        var blockValue = blockIndices.Sum(index =>
            (double)EffectBalanceModel.EstimatedEffectValue(operations[index]));
        if (damageValue > 0d && blockValue > 0d)
        {
            // Damage and Block already share one allowance. Charge only 20% of the smaller package as an extra
            // flexibility premium; equal halves therefore retain 90% rather than the former 67.5%.
            var scale = EffectBalanceModel.MixedDamageBlockScale(damageValue, blockValue);
            foreach (var index in damageIndices)
                ScaleStructuredAmount(operations, index,
                    CardEffectRules.PrintedDamageValueSlot(operations[index])!, scale);
            foreach (var index in blockIndices)
                ScaleStructuredAmount(operations, index,
                    CardEffectRules.PrintedBlockValueSlot(operations[index])!, scale);
        }

        var totalBlock = blockIndices.Sum(index =>
            OperationRuntimeSpecCompiler.FixedValue(operations[index],
                CardEffectRules.PrintedBlockValueSlot(operations[index])!));
        if (totalBlock >= 16)
        {
            // Sixteen printed Block is a meaningful breakpoint. Every point above fifteen consumes an additional
            // half point of Block budget, yielding a smooth tax while retaining occasional large results.
            var highBlockScale = 16d / (16d + (totalBlock - 15) * 0.50d);
            var targetTotalBlock = Math.Max(1, (int)Math.Floor(totalBlock * highBlockScale));
            foreach (var index in blockIndices)
                ScaleStructuredAmount(operations, index,
                    CardEffectRules.PrintedBlockValueSlot(operations[index])!, highBlockScale);
            // Independent rounding can otherwise leave 8+8 unchanged at the exact 16-Block breakpoint. Remove
            // the remaining whole points from the largest line so every qualifying card pays a visible surcharge.
            while (blockIndices.Sum(index => OperationRuntimeSpecCompiler.FixedValue(operations[index],
                       CardEffectRules.PrintedBlockValueSlot(operations[index])!)) > targetTotalBlock)
            {
                var largest = blockIndices.Select(index =>
                        (Index: index, Amount: OperationRuntimeSpecCompiler.FixedValue(
                            operations[index], CardEffectRules.PrintedBlockValueSlot(operations[index])!)))
                    .Where(item => item.Amount > 1).OrderByDescending(item => item.Amount).FirstOrDefault();
                if (largest.Amount <= 1) break;
                ReplaceStructuredAmount(operations, largest.Index,
                    CardEffectRules.PrintedBlockValueSlot(operations[largest.Index])!, largest.Amount - 1);
            }
        }

        return true;
    }

    /// <summary>
    /// Numeric growth is initially sampled on the native one-host curve. Once the complete card is known, divide
    /// that amount by the summed marginal value of every affected host. Thus +4 on two ordinary target-Damage
    /// lines becomes +2, while a growth field attached to frequent/multi-hit/area Damage is reduced further.
    /// Whole-card floors and envelopes run afterwards and therefore retain the normalized relationship.
    /// </summary>
    internal static void NormalizeDependentNumericIncreases(IList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (!EffectBalanceModel.TryDependentNumericIncrease(operation, out var valueSlot, out _)) continue;
            var multiplier = EffectBalanceModel.DependentNumericValueMultiplier(operation, index,
                operations.ToArray());
            if (Math.Abs(multiplier - 1d) < 0.01d) continue;
            var current = OperationRuntimeSpecCompiler.FixedValue(operation, valueSlot);
            if (current <= 0) continue;
            var normalized = Math.Max(1, (int)Math.Round(current / multiplier,
                MidpointRounding.AwayFromZero));
            if (normalized == current
                || !OperationRuntimeSpecCompiler.TryReplaceFixedValue(operation, valueSlot, normalized,
                    out var replacement))
                continue;
            operations[index] = replacement;
        }
    }

    private static void ScaleStructuredAmount(IList<GeneratorOperation> operations, int index,
        string slotId, double scale)
    {
        var current = OperationRuntimeSpecCompiler.FixedValue(operations[index], slotId, -1);
        if (current < 0) return;
        var adjusted = Math.Max(1, (int)Math.Round(current * scale, MidpointRounding.AwayFromZero));
        ReplaceStructuredAmount(operations, index, slotId, adjusted);
    }

    private static void ScalePrimaryStructuredAmount(IList<GeneratorOperation> operations, int index,
        double scale)
    {
        if (!OperationRuntimeSpecCompiler.TryGetPrimaryExplicitFixedValue(operations[index], out var slotId,
                out var current) || slotId is null)
            return;
        var adjusted = Math.Max(1, (int)Math.Round(current * scale, MidpointRounding.AwayFromZero));
        ReplaceStructuredAmount(operations, index, slotId, adjusted);
    }

    private static void ReplaceStructuredAmount(IList<GeneratorOperation> operations, int index,
        string slotId, int adjusted)
    {
        if (OperationRuntimeSpecCompiler.TryReplaceFixedValue(operations[index], slotId, adjusted,
                out var updated))
            operations[index] = updated;
    }

    private void ApplyNegativeEffectCompensation(IList<GeneratorOperation> operations,
        IReadOnlyCollection<CardTag> tags, bool hasPrintedResourceCost, GeneratedCardType cardType)
    {
        var percent = CardEffectRules.NegativeEffectCompensationPercent(operations.ToArray(), tags,
            hasPrintedResourceCost, cardType, BalanceCharacter);
        if (percent == 100) return;
        var forceVisibleIncrease = percent >= 118 && operations.Any(CardEffectRules.IsNegativeEffect);
        var appliedVisibleIncrease = false;

        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (CardEffectRules.IsNegativeEffect(operation)
                || CardEffectRules.CopyThisCardBudgetRole(operation, hasPrintedResourceCost, cardType, tags)
                    is CopyThisCardBudgetRole.PaidReusableDownside or CopyThisCardBudgetRole.ExhaustOffset)
                continue;
            var atom = new ComponentAtom(operation.Template, operation.Scope, operation.ChineseText,
                operation.RequiresSingleTarget, CardReferenceRequirement.None)
                { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(operation) };
            if (!EffectBalanceModel.IsScalableReward(atom)) continue;
            if (!OperationRuntimeSpecCompiler.TryGetPrimaryExplicitFixedValue(operation, out var slotId,
                    out var current) || slotId is null) continue;
            var adjusted = Math.Max(1, (int)Math.Round(current * percent / 100d,
                MidpointRounding.AwayFromZero));
            if (forceVisibleIncrease && !appliedVisibleIncrease && adjusted <= current)
                adjusted = current + 1;
            if (adjusted == current
                || !OperationRuntimeSpecCompiler.TryReplaceFixedValue(operation, slotId, adjusted,
                    out var updated)) continue;
            operations[index] = updated;
            appliedVisibleIncrease = true;
        }
    }

    private void ApplyEffectiveCostAdjustment(IList<GeneratorOperation> operations, double effectiveCost,
        int legacyBudgetCost)
    {
        var scale = ResourceEconomyModel.NumericAdjustment(effectiveCost, legacyBudgetCost);
        if (double.IsNaN(scale) || Math.Abs(scale - 1d) < 0.015d) return;

        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (CardEffectRules.IsNegativeEffect(operation)
                || CardEffectRules.IsEnergyGainOperation(operation)
                || CardEffectRules.IsStarGainOperation(operation)
                || CardEffectRules.IsNonUpgradeableNumericMarker(operation))
                continue;
            var atom = new ComponentAtom(operation.Template, operation.Scope, operation.ChineseText,
                operation.RequiresSingleTarget, CardReferenceRequirement.None)
                { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(operation) };
            if (!_valuePolicy.IsScalableReward(atom)) continue;
            if (!OperationRuntimeSpecCompiler.TryGetPrimaryExplicitFixedValue(operation, out var slotId,
                    out var current) || slotId is null || current <= 0) continue;
            var adjusted = Math.Max(1, (int)Math.Round(current * scale, MidpointRounding.AwayFromZero));
            adjusted = _valuePolicy.ClampSampledValue(atom, 0, adjusted,
                operations.Take(index).ToArray(), _character, _unlockComponentRoles);
            if (CardEffectRules.IsRandomCardGeneration(operation)
                || DerivativeSlotCatalog.IsProducer(operation.Template))
                adjusted = Math.Clamp(adjusted, 1, 5);
            if (adjusted == current
                || !OperationRuntimeSpecCompiler.TryReplaceFixedValue(operation, slotId, adjusted,
                    out var updated)) continue;
            operations[index] = updated;
        }
    }

    private static void ApplyCurseStatusEasterEggCompensation(IList<GeneratorOperation> operations)
    {
        const double scale = 1.75d;
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (CardEffectRules.IsNegativeEffect(operation)
                || operation.DerivativeId is { } derivativeId
                    && DerivativeSlotCatalog.Resolve(derivativeId, operation.Template) is { } derivative
                    && DerivativeSlotCatalog.IsCurse(derivative))
                continue;
            var atom = new ComponentAtom(operation.Template, operation.Scope, operation.ChineseText,
                operation.RequiresSingleTarget, CardReferenceRequirement.None)
                { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(operation) };
            if (!EffectBalanceModel.IsScalableReward(atom)) continue;
            if (!OperationRuntimeSpecCompiler.TryGetPrimaryExplicitFixedValue(operation, out var slotId,
                    out var current) || slotId is null) continue;
            var adjusted = Math.Max(current + 1,
                (int)Math.Round(current * scale, MidpointRounding.AwayFromZero));
            if (OperationRuntimeSpecCompiler.TryReplaceFixedValue(operation, slotId, adjusted,
                    out var updated))
                operations[index] = updated;
        }
    }

    private int LinkedTriggerIndex(IReadOnlyList<GeneratorOperation> operations, ComponentAtom atom)
    {
        if (operations.Count == 0) return -1;
        // Raising this card's own combat cost is paid immediately when the card is played. Attaching it to a
        // delayed/ability trigger would mutate a card that has already left the hand and cease to be a real cost.
        if (atom.Template == "D:IncreaseThisCardCost") return -1;
        if (CardEffectRules.IsRestrictedEffect(atom)
            && EffectBalanceModel.LinkedTrigger(operations) is { } restrictedTrigger
            && CardEffectRules.IsRepeatedTriggerOrCondition(restrictedTrigger))
            return -1;
        if (CardEffectRules.IsStandaloneEventDependencyPrefix(atom)) return -1;
        if (operations[^1].Template == "D:ShuffleAllUnexhaustedIntoDraw")
            return operations[^1].Parameters.GetValueOrDefault("triggerIndex", -1);
        if (CardEffectRules.TriggerNeedsLinkedEffect(operations[^1]))
            return RequiresPlayerChoice(atom) && !TriggerSupportsPlayerChoice(operations[^1])
                ? -1 : operations.Count - 1;
        if (CardEffectRules.IsDependencyPrefix(operations[^1]))
            return operations[^1].Parameters.GetValueOrDefault("triggerIndex", -1);
        if (atom.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger or OperationScope.AbilityRule)
            return -1;
        if (!operations[^1].Parameters.TryGetValue("triggerIndex", out var triggerIndex)) return -1;
        var linkedCount = operations.Count(operation =>
            operation.Parameters.TryGetValue("triggerIndex", out var linked) && linked == triggerIndex);
        if (linkedCount >= 2) return -1;
        var trigger = operations[triggerIndex];
        if (CardEffectRules.IsDoubleTargetVulnerable(atom) && CardEffectRules.IsFatalCondition(trigger))
            return -1;
        if (atom.Template == "N:RetaliateDamage"
            && OperationRuntimeSpecCompiler.GetOrCompile(trigger).Trigger?.Kind != "attack_received")
            return -1;
        if (RequiresPlayerChoice(atom) && !TriggerSupportsPlayerChoice(trigger))
            return -1;
        if (EffectSelectionTuning.DifficultConditionTier(trigger) >= 2)
            return triggerIndex;
        // A trigger may own one or two effects. Dependent follow-ups (Aggression-style upgrade) must stay
        // in the same trigger; otherwise use a coin flip so both one-effect and two-effect assemblies remain possible.
        return atom.Template == "I:UpgradeThatCard" || _random.Next(2) == 0 ? triggerIndex : -1;
    }

    /// <summary>
    /// Strict reachability entry point. Verifies that a native card's rarity, cost, type, target, keywords, component
    /// order, and trigger ownership are all reachable through the production random path with nonzero probability.
    /// </summary>
    public bool CanAssemble(IroncladCardRecipe recipe)
    {
        // Native 5/9-cost recipes keep their operation decomposition reachable, but the generated shell used for
        // that reconstruction is capped at the highest legal fixed Energy cost.
        var legalCost = recipe.Cost >= 5 ? 4 : recipe.Cost;
        if (!_catalog.ComponentCounts.Contains(recipe.Atoms.Count) || recipe.Atoms.Count == 0)
            return false;
        if (recipe.TriggerOwners.Count != recipe.Atoms.Count)
            return false;
        if (legalCost >= 0 && CostRarityWeight(legalCost, recipe.OriginalRarity,
                recipe.StarCost > 0 || recipe.HasStarCostX) <= 0)
            return false;
        if (!_unlockComponentRoles && _character is GeneratedCharacter.Ironclad or GeneratedCharacter.Silent
            && recipe.Type == GeneratedCardType.Power && recipe.Cost <= 0)
            return false;
        if (recipe.Cost == -1 && recipe.OriginalRarity is not (GeneratedRarity.Uncommon or GeneratedRarity.Rare or GeneratedRarity.Ancient))
            return false;

        var assembled = new List<GeneratorOperation>();
        var operationIndices = new int[recipe.Atoms.Count];
        var slots = new CardSlotContext();
        for (var atomIndex = 0; atomIndex < recipe.Atoms.Count; atomIndex++)
        {
            var atom = recipe.Atoms[atomIndex];
            if (!_componentCatalog.AtomKeys.Contains(atom.Key) || !IsCompatible(recipe.Type, recipe.Target, legalCost, recipe.HasStarCostX, atom, assembled))
                return false;
            if (!slots.CanResolve(atom.CardReference))
                return false;

            var desiredOwner = recipe.TriggerOwners[atomIndex] < 0
                ? -1
                : operationIndices[recipe.TriggerOwners[atomIndex]];
            if (!CanChooseTriggerOwner(assembled, atom, desiredOwner))
                return false;

            var parameters = desiredOwner < 0
                ? new Dictionary<string, int>()
                : new Dictionary<string, int> { ["triggerIndex"] = desiredOwner };
            var cardTargetSlot = slots.Resolve(atom.CardReference, assembled);
            operationIndices[atomIndex] = assembled.Count;
            var nextAttackPayload = desiredOwner >= 0
                && CardEffectRules.IsNextAttackGrantTrigger(assembled[desiredOwner]);
            assembled.Add(new GeneratorOperation(
                atom.Template,
                atom.Scope,
                atom.ChineseText,
                parameters,
                cardTargetSlot,
                atom.RequiresSingleTarget && !nextAttackPayload,
                RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom), LocalizedText: LocalizedText(atom)));
        }

        var finalType = recipe.Type == GeneratedCardType.Power
            ? GeneratedCardType.Power
            : CardEffectRules.HasAttackClassifyingDamage(assembled) ? GeneratedCardType.Attack : GeneratedCardType.Skill;
        var finalTarget = finalType == GeneratedCardType.Power
            ? TargetMode.Other
            : assembled.Any(CardEffectRules.RequiresSingleEnemyTarget) ? TargetMode.SingleEnemy : TargetMode.Other;
        // The generator intentionally reclassifies native Attacks whose only damage is behind an explicit conditional
        // gate as Skills. Their complete operation assembly remains reachable; only the shell follows the safer rule.
        var intentionalConditionalDamageReclassification = recipe.Type == GeneratedCardType.Attack
            && finalType == GeneratedCardType.Skill
            && assembled.Any(CardEffectRules.IsEnemyDamage)
            && !CardEffectRules.HasAttackClassifyingDamage(assembled);
        if (finalType != recipe.Type && !intentionalConditionalDamageReclassification
            || finalTarget != recipe.Target)
            return false;
        if (!assembled.Any(CardEffectRules.IsBeneficialEffect))
            return false;
        if (!CardEffectRules.HasValidCopyThisCardAssembly(assembled)
            || !CardEffectRules.HasValidRandomCardGenerationCount(assembled)
            || !CardEffectRules.HasValidAttackCostReductionAssembly(assembled)
            || !CardEffectRules.HasValidOstyAttackedCostAssembly(assembled)
            || !CardEffectRules.HasValidSelfCostChangeAssembly(assembled)
            || !CardEffectRules.HasValidDeferredEnemyTargetAssembly(assembled)
            || !CardEffectRules.HasValidGrandFinaleAssembly(assembled)
            || !CardEffectRules.HasValidGrandFinaleCost(recipe.Cost, recipe.StarCost,
                recipe.HasStarCostX, assembled)
            || !CardEffectRules.HasValidNumericSelfCostReductionAmounts(recipe.Cost, assembled)
            || !CardEffectRules.HasAtMostTwoOfEachField(assembled)
            || !CardEffectRules.HasNoDuplicateCardUniqueEffects(assembled)
            || !CardEffectRules.HasNoDuplicateXEffectKinds(assembled)
            || !CardEffectRules.HasValidShuffleThenDrawAssembly(assembled)
            || !CardEffectRules.HasValidPreventDrawOrdering(assembled)
            || !CardEffectRules.HasValidExhaustAllHandOrdering(assembled)
            || !CardEffectRules.HasValidPlayerSelectedExhaustCounts(assembled)
            || !CardEffectRules.HasValidAllCardsCostIncreaseAssembly(assembled)
            || !CardEffectRules.HasValidHighCostThresholds(assembled)
            || !CardEffectRules.HasValidEnergyXDoubleThreshold(assembled)
            || !CardEffectRules.HasNoRepeatedTriggeredRestrictedEffects(assembled)
            || !CardEffectRules.HasNoRepeatedTriggeredCombatDamageGrowth(assembled)
            || !CardEffectRules.HasNoRepeatedTriggeredNextTurnAttackDouble(assembled)
            || !CardEffectRules.HasNoFatalDoubleVulnerablePayoff(assembled)
            || !CardEffectRules.HasNoNegativeSelfExhaustPayoffs(assembled)
            || !CardEffectRules.HasValidTriggerPayloadAssembly(assembled)
            || !CardEffectRules.HasNoSelfTriggeringDraw(assembled)
            || !CardEffectRules.HasNoSelfTriggeringBlock(assembled)
            || !CardEffectRules.HasValidReturnThisToHandCost(recipe.Cost, recipe.StarCost,
                recipe.HasStarCostX, assembled)
            || !CardEffectRules.HasValidRepeatDamageAssembly(assembled)
            || !CardEffectRules.HasValidNextAttackGrantAssembly(assembled))
            return false;
        if (!TagsAreReachable(recipe, assembled))
            return false;
        return true;
    }

    private bool CanChooseTriggerOwner(IReadOnlyList<GeneratorOperation> operations, ComponentAtom atom, int desiredOwner)
    {
        if (operations.Count == 0) return desiredOwner < 0;
        if (CardEffectRules.TriggerNeedsLinkedEffect(operations[^1]))
            return (!RequiresPlayerChoice(atom) || TriggerSupportsPlayerChoice(operations[^1]))
                && desiredOwner == operations.Count - 1;
        if (CardEffectRules.IsDependencyPrefix(operations[^1]))
            return desiredOwner == operations[^1].Parameters.GetValueOrDefault("triggerIndex", -1);
        if (!operations[^1].Parameters.TryGetValue("triggerIndex", out var inheritedOwner))
            return desiredOwner < 0;
        var linkedCount = operations.Count(operation =>
            operation.Parameters.TryGetValue("triggerIndex", out var linked) && linked == inheritedOwner);
        if (linkedCount >= 2) return desiredOwner < 0;
        var trigger = operations[inheritedOwner];
        if (atom.Template == "N:RetaliateDamage"
            && OperationRuntimeSpecCompiler.GetOrCompile(trigger).Trigger?.Kind != "attack_received")
            return desiredOwner < 0;
        if (RequiresPlayerChoice(atom) && !TriggerSupportsPlayerChoice(trigger))
            return desiredOwner < 0;
        return atom.Template == "I:UpgradeThatCard"
            ? desiredOwner == inheritedOwner
            : desiredOwner is -1 || desiredOwner == inheritedOwner;
    }

    private bool TagsAreReachable(IroncladCardRecipe recipe, IReadOnlyList<GeneratorOperation> operations)
    {
        // Restricted effects force Exhaust on generated non-Powers. A few native cards omit it; reachability still
        // validates their operation assembly while accepting this deliberate safety keyword.
        var forcedExhaust = operations.Any(CardEffectRules.IsRestrictedEffect) && recipe.Type != GeneratedCardType.Power;
        foreach (var tag in Enum.GetValues<CardTag>())
        {
            if (!_keywordPolicy.AllowsBase(tag)) continue;
            var allowed = tag switch
            {
                CardTag.Strike => recipe.Type == GeneratedCardType.Attack,
                CardTag.Defend => recipe.Type == GeneratedCardType.Skill,
                CardTag.Innate => true,
                CardTag.Exhaust => recipe.Type != GeneratedCardType.Power && !operations.Any(CardEffectRules.IsCombatBaseDamageIncrease),
                _ => true
            };
            if (recipe.Tags.Contains(tag) && !allowed) return false;

            var rarityRecipes = _catalog.Recipes.Where(candidate => candidate.OriginalRarity == recipe.OriginalRarity).ToArray();
            var globalCount = _catalog.TagCounts.GetValueOrDefault(tag);
            var rarityCount = rarityRecipes.Count(candidate => candidate.Tags.Contains(tag));
            var numerator = rarityCount * 4 + globalCount;
            var denominator = rarityRecipes.Length * 4 + _catalog.Recipes.Count;
            if (recipe.Tags.Contains(tag))
            {
                if (!forcedExhaust && numerator <= 0) return false;
            }
            else if (allowed && numerator >= denominator)
            {
                return false;
            }
        }
        return true;
    }

    private static bool HasValidOperationAssembly(IReadOnlyList<GeneratorOperation> operations) =>
        CardEffectRules.HasValidCopyThisCardAssembly(operations)
        && CardEffectRules.HasValidCurrentBlockDamageAssembly(operations)
        && CardEffectRules.HasValidRandomCardGenerationCount(operations)
        && CardEffectRules.HasValidAttackCostReductionAssembly(operations)
        && CardEffectRules.HasValidOstyAttackedCostAssembly(operations)
        && CardEffectRules.HasValidSelfCostChangeAssembly(operations)
        && CardEffectRules.HasValidDeferredEnemyTargetAssembly(operations)
        && CardEffectRules.HasValidGrandFinaleAssembly(operations)
        && CardEffectRules.HasAtMostTwoOfEachField(operations)
        && CardEffectRules.HasNoDuplicateCardUniqueEffects(operations)
        && CardEffectRules.HasNoDuplicateXEffectKinds(operations)
        && CardEffectRules.HasValidShuffleThenDrawAssembly(operations)
        && CardEffectRules.HasValidPreventDrawOrdering(operations)
        && CardEffectRules.HasValidExhaustAllHandOrdering(operations)
        && CardEffectRules.HasValidPlayerSelectedExhaustCounts(operations)
        && CardEffectRules.HasValidAllCardsCostIncreaseAssembly(operations)
        && CardEffectRules.HasValidHighCostThresholds(operations)
        && CardEffectRules.HasValidEnergyXDoubleThreshold(operations)
        && CardEffectRules.HasNoRepeatedTriggeredRestrictedEffects(operations)
        && CardEffectRules.HasNoRepeatedTriggeredCombatDamageGrowth(operations)
        && CardEffectRules.HasNoRepeatedTriggeredNextTurnAttackDouble(operations)
        && CardEffectRules.HasNoFatalDoubleVulnerablePayoff(operations)
        && CardEffectRules.HasNoNegativeSelfExhaustPayoffs(operations)
        && CardEffectRules.HasNoInvalidTriggeredStateEffects(operations)
        && CardEffectRules.HasNoStateConditionModifiers(operations)
        && CardEffectRules.HasValidTriggeredEndTurnAssembly(operations)
        && CardEffectRules.HasNoSelfTriggeringHpLoss(operations)
        && CardEffectRules.HasNoSelfTriggeringDraw(operations)
        && CardEffectRules.HasNoSelfTriggeringBlock(operations)
        && CardEffectRules.HasValidTriggerPayloadAssembly(operations)
        && CardEffectRules.HasValidRepeatDamageAssembly(operations)
        && CardEffectRules.HasValidNextAttackGrantAssembly(operations)
        && CardEffectRules.HasValidFailableConditionAssembly(operations);

    private static bool NormalizeCurrentBlockDamageAnchor(IList<GeneratorOperation> operations,
        int modifierIndex)
    {
        var anchorIndex = CardEffectRules.CurrentBlockDamageAnchorIndex(
            operations as IReadOnlyList<GeneratorOperation> ?? operations.ToArray(), modifierIndex);
        if (anchorIndex < 0) return false;
        var anchor = operations[anchorIndex];
        if (!OperationRuntimeSpecCompiler.ExplicitFixedValueSlots(anchor).Any(slot => slot.Id == "damage"))
            return false;
        if (OperationRuntimeSpecCompiler.FixedValue(anchor, "damage") == 0) return true;
        if (!OperationRuntimeSpecCompiler.TryReplaceFixedValue(anchor, "damage", 0, out var normalized))
            return false;
        operations[anchorIndex] = normalized;
        return true;
    }

    /// <summary>
    /// Resolves every replaceable payload attached to one component. Ordinary assembly and the independently
    /// budgeted aggressive bonus must use this exact pipeline; keeping separate copies previously allowed
    /// derivative enchantments, Orb count pricing and localized-text registration to drift apart.
    /// </summary>
    private ComponentAtom ResolveSlots(ComponentAtom atom, int effectiveCost, bool perEnemy,
        out ResolvedSlot resolved)
    {
        string? derivativeId = null;
        string? derivativeEnchantmentId = null;
        int? derivativeEnchantmentAmount = null;
        string? orbSourceId = null;
        string? orbOutputId = null;
        var isCurse = false;

        if (DerivativeSlotCatalog.IsSlotOperation(atom.Template))
        {
            var derivative = DerivativeSlotCatalog.Roll(_random, _character, _unlockComponentRoles,
                atom.Template);
            derivativeId = derivative.Id;
            isCurse = DerivativeSlotCatalog.IsCurse(derivative);
            var enchantment = DerivativeSlotCatalog.IsProducer(atom.Template)
                ? DerivativeEnchantmentCatalog.Roll(_random, atom.Template, derivative)
                : null;
            derivativeEnchantmentId = enchantment?.Id;
            derivativeEnchantmentAmount = enchantment is null
                ? null
                : DerivativeEnchantmentCatalog.RollAmount(_random, enchantment);
            var sourceChinese = atom.ChineseText;
            var legacyBudgetedSourceChinese = DerivativeSlotCatalog.AdjustFixedProducerCount(
                atom.Template, sourceChinese, derivative, _ancientFuelActive);
            var countSlot = OperationRuntimeSpecCompiler.GetOrCompile(atom).Values.FirstOrDefault(value =>
                value.Source == "fixed" && value.Id is "amount" or "count");
            if (countSlot is not null)
            {
                var sourceCount = countSlot.BaseValue + countSlot.Offset;
                var adjustedCount = DerivativeSlotCatalog.AdjustFixedProducerCount(atom.Template,
                    sourceCount, derivative, _ancientFuelActive);
                if (adjustedCount != sourceCount)
                    atom = ReplaceAtomFixedValue(atom, countSlot.Id, adjustedCount);
            }
            else if (DerivativeSlotCatalog.ImplicitFixedProducerCount(atom.Template) is { } implicitCount)
            {
                var adjustedCount = DerivativeSlotCatalog.AdjustFixedProducerCount(atom.Template,
                    implicitCount, derivative, _ancientFuelActive);
                if (adjustedCount != implicitCount)
                    atom = atom with
                    {
                        ChineseText = legacyBudgetedSourceChinese,
                        RuntimeSpec = OperationRuntimeSpecCompiler.AddExplicitFixedValue(
                            OperationRuntimeSpecCompiler.GetOrCompile(atom), "amount", adjustedCount)
                    };
            }
            if (OperationRuntimeSpecCompiler.EnableLegacyEquivalenceAssertions
                && legacyBudgetedSourceChinese != atom.ChineseText)
                throw new InvalidOperationException($"Structured derivative count drifted for {atom.Template}: "
                    + $"legacy={legacyBudgetedSourceChinese}; structured={atom.ChineseText}");
            if (atom.ChineseText != sourceChinese)
                ExternalOperationTextRegistry.RegisterNumericVariant(atom.Template, sourceChinese,
                    atom.ChineseText);
            sourceChinese = atom.ChineseText;
            var sourceSpec = OperationRuntimeSpecCompiler.GetOrCompile(atom);
            var sourceLocalized = LocalizedText(atom);
            try
            {
                sourceLocalized.Validate(sourceSpec);
                if (!string.Equals(sourceLocalized.RenderChinese(sourceSpec), sourceChinese,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException("The cached localization projects an earlier value shape.");
            }
            catch (InvalidOperationException)
            {
                var migratedEnglish = ExternalOperationTextRegistry.TryGet(atom.Template, sourceChinese,
                    out var registeredEnglish)
                    ? registeredEnglish
                    : EnglishCardDescriptionRenderer.TranslateLegacyLiteral(sourceChinese);
                if (!OperationLocalizedText.TryCompile(sourceChinese, migratedEnglish, sourceSpec,
                        out sourceLocalized) || sourceLocalized is null)
                    throw new InvalidOperationException($"Cannot refresh derivative localization for "
                                                        + $"{atom.SemanticId}/{atom.Template}.");
            }
            var sourceOperation = new GeneratorOperation(atom.Template, atom.Scope, sourceChinese,
                new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget,
                RuntimeSpec: sourceSpec, LocalizedText: sourceLocalized);
            var sourceEnglish = EnglishCardDescriptionRenderer.OperationText(sourceOperation);
            var derivativeSpec = OperationRuntimeSpecCompiler.WithDerivativeReference(
                OperationRuntimeSpecCompiler.GetOrCompile(atom), derivative.Id);
            var sourceDerivative = DerivativeSlotCatalog.Source(atom.Template)
                ?? throw new InvalidOperationException($"Derivative slot {atom.Template} has no source definition.");
            var sourceEnchantment = DerivativeSlotCatalog.ResolveEnchantment(null, null, atom.Template);
            var sourceChineseName = DerivativeEnchantmentCatalog.ChineseCardName(sourceDerivative,
                sourceEnchantment);
            var selectedChineseName = DerivativeEnchantmentCatalog.ChineseCardName(derivative, enchantment);
            var usePlural = DerivativeSlotCatalog.EnglishTextUsesPlural(sourceEnglish, atom.Template);
            var sourceEnglishSingular = DerivativeSlotCatalog.SourceEnglishName(atom.Template, plural: false);
            var sourceEnglishPlural = DerivativeSlotCatalog.SourceEnglishName(atom.Template, plural: true);
            var sourceEnglishName = sourceEnglishPlural != sourceEnglishSingular
                && sourceLocalized.EnglishTemplate?.Contains(sourceEnglishPlural,
                    StringComparison.OrdinalIgnoreCase) == true
                    ? sourceEnglishPlural
                    : sourceEnglishSingular;
            var selectedEnglishName = usePlural
                ? DerivativeEnchantmentCatalog.EnglishPlural(derivative, enchantment)
                : DerivativeEnchantmentCatalog.EnglishSingular(derivative, enchantment);
            OperationLocalizedText derivativeLocalized;
            try
            {
                derivativeLocalized = sourceLocalized
                    .BindTextSlot("derivative", sourceChineseName, sourceEnglishName)
                    .WithTextSlotValue("derivative", selectedChineseName, selectedEnglishName);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException($"Cannot bind derivative presentation slot for "
                    + $"{atom.SemanticId}/{atom.Template}: zh={sourceChineseName}; en={sourceEnglishName}; "
                    + $"rendered={sourceEnglish}; template={sourceLocalized.EnglishTemplate}.", exception);
            }
            try
            {
                derivativeLocalized.Validate(derivativeSpec);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException($"Invalid derivative presentation slots for "
                    + $"{atom.SemanticId}/{atom.Template}: zh={derivativeLocalized.ChineseTemplate}; "
                    + $"en={derivativeLocalized.EnglishTemplate}; spec={derivativeSpec.StableSignature()}.", exception);
            }
            var derivativeChinese = derivativeLocalized.RenderChinese(derivativeSpec);
            var derivativeEnglish = derivativeLocalized.RenderEnglish(derivativeSpec)
                ?? throw new InvalidOperationException($"Derivative slot {atom.Template} has no English template.");
            ExternalOperationTextRegistry.Register(atom.Template, derivativeChinese, derivativeEnglish);
            atom = atom with
            {
                ChineseText = derivativeChinese,
                RuntimeSpec = derivativeSpec,
                LocalizedText = derivativeLocalized
            };
        }

        if (OrbSlotCatalog.IsSlotOperation(atom.Template))
        {
            var (orbSource, orbOutput) = OrbSlotCatalog.Roll(_random, atom.Template);
            orbSourceId = orbSource?.Id;
            orbOutputId = orbOutput?.Id;
            if (orbOutput is not null && CardEffectRules.IsDirectOrbChannel(atom))
            {
                var countSlot = OperationRuntimeSpecCompiler.ExplicitFixedValueSlots(atom).FirstOrDefault();
                if (countSlot is not null)
                {
                    var adjustedCount = OrbSlotCatalog.BudgetedChannelCount(_random, atom.Template, orbOutput,
                        countSlot.BaseValue + countSlot.Offset, effectiveCost, perEnemy);
                    atom = ReplaceAtomFixedValue(atom, countSlot.Id, adjustedCount);
                }
            }
            var sourceLocalized = LocalizedText(atom);
            var sourceOperation = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
                new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget,
                RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom), LocalizedText: sourceLocalized);
            var sourceEnglish = EnglishCardDescriptionRenderer.OperationText(sourceOperation);
            var orbLocalized = OrbSlotCatalog.BindLocalizedText(sourceLocalized, sourceEnglish, atom.Template,
                orbSource, orbOutput, OperationRuntimeSpecCompiler.GetOrCompile(atom));
            var orbChinese = orbLocalized.RenderChinese(OperationRuntimeSpecCompiler.GetOrCompile(atom));
            var orbEnglish = orbLocalized.RenderEnglish(OperationRuntimeSpecCompiler.GetOrCompile(atom))
                ?? throw new InvalidOperationException($"Orb slot {atom.Template} has no English template.");
            ExternalOperationTextRegistry.Register(atom.Template, orbChinese, orbEnglish);
            atom = atom with { ChineseText = orbChinese, LocalizedText = orbLocalized };
        }

        resolved = new ResolvedSlot(derivativeId, derivativeEnchantmentId, derivativeEnchantmentAmount,
            orbSourceId, orbOutputId, isCurse);
        return atom;
    }

    /// <summary>
    /// Numeric-aggressive cards with a downside receive one card-level 50% optimization roll. The roll no longer
    /// deletes a downside or keyword: it picks at most one explicitly numeric negative whose value exceeds one and
    /// reduces its payment by one. This preserves the card's generated structure and downside identity while still
    /// giving aggressive mode an occasional cleaner numerical result.
    /// </summary>
    private void TrySoftenOneAggressiveNegative(List<GeneratorOperation> operations)
    {
        var candidates = operations.Select((operation, index) => (operation, index))
            .Where(item => CardEffectRules.IsNegativeEffect(item.operation)
                && CardEffectRules.IsReducibleNegativeNumber(item.operation)
                && OperationRuntimeSpecCompiler.TryGetFixedUpgradeValue(item.operation, out _, out var value)
                && value > 1)
            .ToList();
        while (candidates.Count > 0)
        {
            var candidateIndex = _random.Next(candidates.Count);
            var (operation, operationIndex) = candidates[candidateIndex];
            candidates.RemoveAt(candidateIndex);
            if (!OperationRuntimeSpecCompiler.TryGetFixedUpgradeValue(operation, out var slotId, out var value)
                || slotId is null || value <= 1
                || !OperationRuntimeSpecCompiler.TryReplaceFixedValue(operation, slotId, value - 1,
                    out var softened))
                continue;
            var candidateOperations = operations.ToArray();
            candidateOperations[operationIndex] = softened;
            if (!HasValidOperationAssembly(candidateOperations)) continue;
            operations[operationIndex] = softened;
            return;
        }
    }

    private bool IsCompatible(GeneratedCardType type, TargetMode target, int cost, bool hasStarCostX,
        ComponentAtom atom, IReadOnlyList<GeneratorOperation> previous)
    {
        if (OperationRuntimeSpecCompiler.EnableLegacyEquivalenceAssertions)
            AssertLegacyCompatibilityEquivalence(atom, previous);
        // Idempotent state/rule effects are card-level singletons. Rejecting the candidate here, before numeric
        // instantiation and whole-card budget evaluation, prevents a duplicate downside from buying compensation
        // even temporarily. The final assembly validator repeats this check only as an invariant for imported or
        // manually reconstructed operation lists.
        if (CardEffectRules.WouldDuplicateCardUniqueEffect(previous, atom))
            return false;
        if (CardEffectRules.FieldOccurrenceCount(previous, atom) >= 2)
            return false;
        if (CardEffectRules.XRequirement(atom) != XResourceRequirement.None
            && previous.Where(operation => CardEffectRules.XRequirement(operation) != XResourceRequirement.None)
                .Any(operation => CardEffectRules.HasSameXEffectKind(operation, atom)))
            return false;
        if ((cost == -1 || hasStarCostX) && CardEffectRules.IsSelfCostChange(atom))
            return false;
        if (atom.Template == "R:DoubleEitherXAtThreshold"
            && !previous.Any(operation => operation.Template != "R:DoubleEitherXAtThreshold"
                && OperationRuntimeSpecCompiler.GetOrCompile(operation).Values.Any(value =>
                    value.Source is "energy_x" or "star_x" or "special_x")))
            return false;
        if (CardEffectRules.IsNextAttackGrantTrigger(atom) && type == GeneratedCardType.Power)
            return false;
        if (CardEffectRules.IsCurrentBlockDamageModifier(atom)
            && (previous.Any(CardEffectRules.IsCurrentBlockDamageModifier)
                || !previous.Any(operation => operation.Template == "T:D"
                    && !CardEffectRules.IsIntrinsicMultiHitDamage(operation))))
            return false;
        var pendingNextAttackGrant = previous.LastOrDefault() is { } nextAttackTrigger
            && CardEffectRules.IsNextAttackGrantTrigger(nextAttackTrigger);
        if (pendingNextAttackGrant && !CardEffectRules.IsNextAttackGrantPayoff(atom))
            return false;
        if (atom.Template is "D:IncreaseThisCardCost" or "D:IncreaseAllClaws"
            && (type == GeneratedCardType.Power
                || previous.LastOrDefault() is { } costPrior
                && (CardEffectRules.TriggerNeedsLinkedEffect(costPrior)
                    || CardEffectRules.IsDependencyPrefix(costPrior))))
            return false;
        if (CardEffectRules.IsRestrictedEffect(atom)
            && previous.LastOrDefault() is { } restrictedPrior
            && (CardEffectRules.TriggerNeedsLinkedEffect(restrictedPrior)
                || CardEffectRules.IsDependencyPrefix(restrictedPrior))
            && CardEffectRules.IsRepeatedTriggerOrCondition(restrictedPrior))
            return false;
        // Doubling Vulnerable after Fatal is semantically backwards: the target is already dead when the payoff
        // resolves. Reject it before numeric instantiation when Fatal still needs its first linked payoff.
        if (CardEffectRules.IsDoubleTargetVulnerable(atom)
            && previous.LastOrDefault() is { } fatalPrior
            && CardEffectRules.IsFatalCondition(fatalPrior)
            && CardEffectRules.TriggerNeedsLinkedEffect(fatalPrior))
            return false;
        // These operations are resolved by card lifecycle hooks rather than the ordinary operation runner.
        // Nesting one under an unrelated trigger prints a condition that runtime cannot honor.
        if (CardEffectRules.IsSelfManagedStateEffect(atom)
            && previous.LastOrDefault() is { } statePrior
            && (CardEffectRules.TriggerNeedsLinkedEffect(statePrior)
                || CardEffectRules.IsDependencyPrefix(statePrior))
            && !(atom.Template == "CL:ReturnThisToHand" && statePrior.Template == "CL:AtNextTurnStart"))
            return false;
        // EndTurn is an executable payoff rather than a card-destination hook. It may follow ordinary conditions
        // and event triggers, but never a turn-start/turn-end boundary: that combination either immediately skips
        // the player's turn or recursively requests the lifecycle transition that is already being processed.
        if (atom.Template == "R:EndTurn"
            && previous.LastOrDefault() is { } endTurnOwner
            && (CardEffectRules.TriggerNeedsLinkedEffect(endTurnOwner)
                || CardEffectRules.IsDependencyPrefix(endTurnOwner))
            && CardEffectRules.IsTurnBoundaryTrigger(endTurnOwner))
            return false;
        if (CardEffectRules.IsCombatBaseDamageIncrease(atom)
            && EffectBalanceModel.LinkedTrigger(previous) is { } damageGrowthTrigger
            && CardEffectRules.IsRepeatedTriggerOrCondition(damageGrowthTrigger))
            return false;
        // A per-item extra-hit clause defines the whole hit count. It cannot share a card with an X/multi-hit
        // damage line, which already defines a separate hit count. Static “additional times” modifiers remain
        // legal and are added on top of any fixed, random, area, or X hit count by the runtime interpreter.
        if (CardEffectRules.IsIntrinsicMultiHitDamage(atom)
            && previous.Any(CardEffectRules.IsDynamicTotalHitModifier))
            return false;
        if (CardEffectRules.IsEnemyDamage(atom)
            && previous.Any(operation => operation.Template == "M:RepeatAreaOnKill")
            && atom.Template != "N:AllD")
            return false;
        if (previous.LastOrDefault()?.Template == "D:ShuffleAllUnexhaustedIntoDraw"
            && !CardEffectRules.IsImmediateDrawEffect(atom))
            return false;
        if (CardEffectRules.IsImmediateDrawEffect(atom)
            && previous.LastOrDefault() is { } drawTrigger
            && CardEffectRules.IsEveryCardDrawnTrigger(drawTrigger))
            return false;
        if (type == GeneratedCardType.Power && CardEffectRules.IsSelfCardMovementOrReplay(atom))
            return false;
        // These gates inspect the hand/target at the moment the card starts resolving. Keeping them first prevents
        // an earlier generated effect from satisfying or invalidating the supposedly difficult condition itself.
        if (atom.Template is "CL:IfHandEmpty" or "CL:IfNoAttacksInHand" or "C:ifTargetPoisoned"
            && previous.Count > 0)
            return false;
        // The just-drawn-card-is-a-Skill condition reads local state saved by the immediately preceding draw. It may
        // not stand alone, cross another effect, or run inside a Power/delayed trigger. It currently applies to one draw.
        if (atom.Template == "C:ifLastDrawnSkill"
            && (type == GeneratedCardType.Power
                || previous.LastOrDefault() is not { Template: "N:Draw" } draw
                || draw.Parameters.ContainsKey("triggerIndex")
                || OperationRuntimeSpecCompiler.FixedValue(draw, "draw") != 1))
            return false;
        if (CardEffectRules.RequiresSpecificTriggerPayload(atom)
            && (previous.LastOrDefault() is not { } payloadTrigger
                || !CardEffectRules.CanSupplySpecificTriggerPayload(payloadTrigger, atom)))
            return false;
        // The native Helix Drill Damage line retains its stable ID for old snapshots, but new generation treats
        // the prefix as a generic immediate trigger. Keep this line dependent while allowing other legal payoffs.
        if (atom.Template == "D:RepeatPerEnergySpentThisTurn"
            && previous.LastOrDefault()?.Template != "D:ForEachEnergySpentThisTurn")
            return false;
        if (previous.LastOrDefault() is { } pendingDependency
            && CardEffectRules.IsDependencyPrefix(pendingDependency)
            && !CardEffectRules.IsLegalDependencyPayoff(pendingDependency, atom))
            return false;
        if (previous.LastOrDefault() is { } pendingOuterTrigger
            && CardEffectRules.TriggerNeedsLinkedEffect(pendingOuterTrigger)
            && CardEffectRules.IsStandaloneEventDependencyPrefix(atom))
            return false;
        if (CardEffectRules.RequiresDependencyPrefix(atom)
            && (previous.LastOrDefault() is not { } dependency
                || !CardEffectRules.IsLegalDependencyPayoff(dependency, atom)))
            return false;
        if (CardEffectRules.IsHitEnemyDamageVariant(atom)
            && !CardEffectRules.HasLightningEvokeTriggerContext(previous))
            return false;
        if (CardEffectRules.IsExtremeLifecycleDownside(atom)
            && previous.LastOrDefault() is { } severePrior
            && (CardEffectRules.TriggerNeedsLinkedEffect(severePrior) || severePrior.Parameters.ContainsKey("triggerIndex")))
            return false;
        // Energy-X and Star-X are distinct resources. An X operation must match the shell's X resource,
        // and an X-cost shell cannot be filled by a fixed-value operation.
        var xRequirement = CardEffectRules.XRequirement(atom);
        var xMatches = xRequirement switch
        {
            XResourceRequirement.None => cost != -1 && !hasStarCostX,
            XResourceRequirement.Energy => cost == -1 && !hasStarCostX,
            XResourceRequirement.Star => cost != -1 && hasStarCostX,
            XResourceRequirement.Either => cost == -1 || hasStarCostX,
            _ => false
        };
        if (!xMatches)
            return false;
        // A non-targeting card may only own a target-dependent triggered effect when its wording says where the
        // target comes from: either a random enemy, or the enemy supplied by a compatible event trigger. Generic
        // text such as “deal 10 damage” must never appear on a card that cannot be aimed.
        if (target == TargetMode.Other && CardEffectRules.RequiresSingleEnemyTarget(atom)
            && !pendingNextAttackGrant
            && !CardEffectRules.UsesExplicitRandomEnemyTarget(atom)
            && (previous.LastOrDefault() is not { } targetProvider
                || !CardEffectRules.CanResolveTriggeredEnemyTarget(targetProvider, atom)))
            return false;
        // These triggers are resolved by a temporary power on a later turn. The power deliberately does not retain
        // the enemy selected when the card was played, so even a SingleEnemy card cannot use that stale target in
        // the linked effect. Random-enemy/all-enemy effects remain legal because they resolve their own target.
        if (previous.LastOrDefault() is { } deferredTrigger
            && CardEffectRules.TriggerLosesOriginalEnemyTarget(deferredTrigger)
            && CardEffectRules.RequiresSingleEnemyTarget(atom)
            && !CardEffectRules.CanResolveTriggeredEnemyTarget(deferredTrigger, atom))
            return false;
        // A keyword by itself belongs in the card's keyword collection, never in the operation text.
        if (IsStandaloneKeywordOperation(atom))
            return false;
        if (type != GeneratedCardType.Power && atom.Scope is OperationScope.AbilityTrigger or OperationScope.AbilityRule
            && atom.Template is not ("CL:AfterTurns" or "CL:DieOnUnblockedAttack"))
            return false;
        var pendingTrigger = previous.LastOrDefault();
        // Conditions that observe this card's pile state are lifecycle gates, not numeric repeat/count prefixes.
        // Their payoff must perform a real action when the state is observed (play this card, gain Block, etc.).
        // Attaching a passive modifier leaves no operation for the trigger runner to execute and can also make an
        // earlier damage line appear conditionally modified even though that damage already resolved on play.
        if (atom.Scope == OperationScope.Modifier
            && pendingTrigger is not null
            && CardEffectRules.IsSelfZoneStateCondition(pendingTrigger))
            return false;
        if (atom.Template == "I:DoubleAttackDamageNextTurn"
            && pendingTrigger is not null
            && CardEffectRules.TriggerNeedsLinkedEffect(pendingTrigger)
            && EffectBalanceModel.HasRepeatedOrMultiplicativePayoff(pendingTrigger))
            return false;
        if (atom.Template == "N:RetaliateDamage"
            && pendingTrigger is not null
            && CardEffectRules.TriggerNeedsLinkedEffect(pendingTrigger)
            && OperationRuntimeSpecCompiler.GetOrCompile(pendingTrigger).Trigger?.Kind != "attack_received")
            return false;
        if (pendingTrigger is not null
            && CardEffectRules.TriggerNeedsLinkedEffect(pendingTrigger)
            && RequiresPlayerChoice(atom) && !TriggerSupportsPlayerChoice(pendingTrigger))
            return false;
        // An on-exhaust trigger carries benefits only; a negative payoff would turn voluntary Exhaust into a trap.
        if (pendingTrigger is not null
            && CardEffectRules.IsSelfExhaustEventTrigger(pendingTrigger)
            && CardEffectRules.IsNegativeEffect(atom))
            return false;
        // A condition must be followed by an effect, not another condition that creates an empty unrenderable clause.
        if (atom.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger
            && previous.LastOrDefault()?.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger)
            return false;
        // A rule-style Power is persistent state, not the one-shot payoff of another condition.
        if (atom.Scope == OperationScope.AbilityRule
            && previous.LastOrDefault()?.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger)
            return false;
        // A modifier requires a matching numeric effect. Strength scaling modifies Block; the remaining native
        // Ironclad modifiers modify damage.
        if (atom.Scope == OperationScope.Modifier)
        {
            if (atom.Template == "M:TriggeredAttackDamagePercent"
                && previous.LastOrDefault() is { } eventAttackTrigger
                && CardEffectRules.SuppliesEventAttackForDamageModifier(eventAttackTrigger))
                return true;
            if (CardEffectRules.IsDependencyPrefix(atom))
            {
                if (previous.LastOrDefault() is { } dependencyPrior && CardEffectRules.IsDependencyPrefix(dependencyPrior)) return false;
                if (atom.Template == "D:ForEachOrb" && !previous.Any(CardEffectRules.IsEnemyDamage)) return false;
                return true;
            }
            var requiresBlock = OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags
                .Contains("requires_block_anchor");
            var hasAnchor = pendingNextAttackGrant && CardEffectRules.IsNextAttackGrantPayoff(atom)
                ? true
                : atom.Template == "NCR:DoomPerDoomThreshold"
                ? previous.Any(operation => operation.Template == "NCR:ApplyDoom")
                : requiresBlock
                ? previous.Any(operation => operation.Template.StartsWith("N:B", StringComparison.Ordinal))
                : previous.Any(CardEffectRules.IsEnemyDamage);
            if (!hasAnchor) return false;
            if (atom.Template == "M:RepeatAreaOnKill"
                && (previous.All(operation => operation.Template != "N:AllD")
                    || previous.Where(CardEffectRules.IsEnemyDamage)
                        .Any(operation => operation.Template != "N:AllD")))
                return false;
            if (CardEffectRules.IsDynamicTotalHitModifier(atom)
                && previous.Where(CardEffectRules.IsEnemyDamage).Any(CardEffectRules.IsIntrinsicMultiHitDamage))
                return false;
        }
        if (atom.Template == "NCR:ApplyDoomEqualDamage" && !previous.Any(CardEffectRules.IsEnemyDamage))
            return false;
        if (atom.Template == "NCR:DoubleHangDamage" && !previous.Any(CardEffectRules.IsEnemyDamage))
            return false;
        if (atom.Template is "CL:GainBlockEqualDamage" or "CL:DamageOtherEnemiesEqual"
            && !previous.Any(CardEffectRules.IsEnemyDamage))
            return false;
        if (atom.Template is "D:IncreaseAllClaws" or "NCR:IncreaseThisCardDamageRun"
            && !previous.Any(CardEffectRules.IsEnemyDamage))
            return false;
        if (atom.Template == "D:IncreaseThisCardBlockRun"
            && !previous.Any(operation => operation.Template == "N:B"))
            return false;
        if (atom.Template == "CL:ReturnThisToHand"
            && (previous.LastOrDefault()?.Template != "CL:AtNextTurnStart"
                || !previous.Take(previous.Count - 1).Any(CardEffectRules.IsOrdinaryOnPlayEffect)))
            return false;
        if (atom.Template == "CL:PutEventCardOnDrawTop"
            && previous.LastOrDefault()?.Template != "CL:FirstAttackOrSkillEachTurn")
            return false;
        if (atom.Template == "CL:IncreaseRollingDamage"
            && previous.LastOrDefault()?.Template is not ("N:AllD" or "CL:RollingAllDamage"))
            return false;
        if (CardEffectRules.IsFatalCondition(atom) && !previous.Any(CardEffectRules.IsEnemyDamage))
            return false;
        // A per-card-exhausted payoff counts only cards actually exhausted earlier by this card.
        if (CardEffectRules.IsForEachExhaustTrigger(atom)
            && !previous.Any(operation => CardEffectRules.IsCompatiblePriorExhaust(operation, atom)))
            return false;
        // Rampage-style combat damage growth requires damage on this card and cannot appear on a Power.
        if (CardEffectRules.IsCombatBaseDamageIncrease(atom)
            && (type == GeneratedCardType.Power || !previous.Any(CardEffectRules.IsEnemyDamage)))
            return false;
        // Pummel-style two-step effects share one local resolution state and cannot be nested under delayed triggers.
        if (atom.Template == "I:ExhaustRandomAttack"
            && previous.LastOrDefault() is { } prior
            && CardEffectRules.TriggerNeedsLinkedEffect(prior))
            return false;
        if (atom.Template == "I:PlayThisCard")
        {
            if (type == GeneratedCardType.Power
                || previous.LastOrDefault() is not { } trigger
                || !CardEffectRules.IsExhaustPileTurnEndTrigger(trigger))
                return false;
            // Auto-playing a card whose only effect is the auto-play trigger has no useful OnPlay payload.
            // Require an ordinary, unlinked play effect before this trigger is assembled.
            if (!previous.Take(previous.Count - 1).Any(CardEffectRules.IsOrdinaryOnPlayEffect))
                return false;
        }
        if (atom.Template == "R:PlayThisCard")
        {
            if (type == GeneratedCardType.Power
                || previous.LastOrDefault()?.Template != "R:AtTurnStartIfInExhaust"
                || !previous.Take(previous.Count - 1).Any(CardEffectRules.IsOrdinaryOnPlayEffect))
                return false;
        }
        if (atom.Template == "C:forEachDiscarded"
            && !previous.Any(operation => operation.Template is "N:Discard" or "N:DiscardAll" or "I:DiscardHandDrawSame"))
            return false;
        // The second Pummel-style step reads damage from the Attack saved by the adjacent random-exhaust step only.
        if (atom.Template == "I:AddExhaustedAttackDamage"
            && previous.LastOrDefault()?.Template != "I:ExhaustRandomAttack")
            return false;
        if (atom.Template is "I:ReplayAttack" or "I:SetCostZero"
            && (previous.LastOrDefault() is not { } grantTrigger
                || !CardEffectRules.IsNextAttackGrantTrigger(grantTrigger)))
            return false;
        if (atom.Template == "I:UpgradeThatCard" && previous.LastOrDefault()?.Template != "N:Move")
            return false;
        // Grand Finale's extreme 60-damage variant is the payoff for its severe play restriction, not a
        // generally available AllD number. Other AllD values remain freely composable.
        if (IsGrandFinalePayoff(atom) && !previous.Any(IsDrawPileEmptyCondition))
            return false;
        return true;
    }

    private static void AssertLegacyCompatibilityEquivalence(ComponentAtom atom,
        IReadOnlyList<GeneratorOperation> previous)
    {
        static void Check(string label, bool legacy, bool structured, ComponentAtom atom)
        {
            if (legacy != structured)
                throw new InvalidOperationException($"Compatibility {label} drift for {atom.Template}:"
                    + $" {atom.ChineseText}; legacy={legacy}; structured={structured}");
        }

        var spec = OperationRuntimeSpecCompiler.GetOrCompile(atom);
        Check("block_anchor", atom.Scope == OperationScope.Modifier
                && atom.ChineseText.Contains("额外获得", StringComparison.Ordinal),
            spec.Flags.Contains("requires_block_anchor"), atom);
        Check("standalone_keyword", atom.ChineseText.Trim().TrimEnd('。') is "虚无",
            spec.Flags.Contains("standalone_keyword"), atom);
        var legacyChoice = atom.CardReference is CardReferenceRequirement.HandCard
                or CardReferenceRequirement.HandAttack
            || CardEffectRules.IsAtomicChoiceProxy(atom.Template)
            || atom.Template is "N:Discard" or "I:GrantSlyToHandSkillThisTurn" or "I:CopySelectedCardNextTurn"
                or "CL:TransformSelectedHandCards" or "CL:ExhaustUpToHandCards"
                or "CL:MoveSelectedSkillDrawToHand" or "CL:MoveSelectedAttackDrawToHand"
                or "CL:ChooseFromRandomDrawCards" or "CL:ChooseDrawCardToHand"
                or "R:MoveDiscardCardToDrawTop" or "R:PlaySelectedSkillMultipleTimes"
                or "R:PutSelectedHandCardsOnDraw" or "R:PutSelectedHandCardOnDraw"
                or "R:CopySelectedColorlessCard" or "NCR:ExhaustSelectedDrawCard"
                or "NCR:MoveDiscardCardToHand" or "D:MoveDiscardCardToHand"
                or "I:PlayTopCardAndExhaust" or "I:PlayTopXCards" or "CL:PlayTopDrawCard"
                or "D:AutoPlayRandomAttackFromDraw" or "I:AutoPlayRandomAttackFromHand"
                or "I:PlayAtRandomEnemy"
            || atom.Template == "N:Exhaust"
                && spec is { Opcode: "exhaust_card", Variant: "selected", SourceZone: "hand" }
            || atom.ChineseText is "将弃牌堆中的一张牌放到抽牌堆顶部。"
                or "升级手牌中的一张牌。"
            || atom.ChineseText.Contains("选择", StringComparison.Ordinal);
        var structuredChoice = atom.CardReference is CardReferenceRequirement.HandCard
                or CardReferenceRequirement.HandAttack
            || CardEffectRules.IsAtomicChoiceProxy(atom.Template)
            || spec.Flags.Contains("requires_player_choice");
        Check("player_choice", legacyChoice, structuredChoice, atom);

        if (previous.LastOrDefault() is not { } prior) return;
        var priorSpec = OperationRuntimeSpecCompiler.GetOrCompile(prior);
        if (atom.Template == "N:Exhaust")
            Check("referenced_skill_exhaust", atom.ChineseText == "消耗那张技能牌。"
                    && (prior.Scope != OperationScope.AbilityTrigger
                        || !prior.ChineseText.Contains("技能牌", StringComparison.Ordinal)),
                spec is { Opcode: "exhaust_card", Variant: "referenced", CardFilter: "skill" }
                    && (prior.Scope != OperationScope.AbilityTrigger
                        || !priorSpec.Flags.Contains("skill_card_reference")),
                atom);
        if (atom.Template == "I:PlayAtRandomEnemy")
            Check("strike_draw_replay", prior.Scope != OperationScope.AbilityTrigger
                    || !prior.ChineseText.StartsWith("每当你抽到名字中有", StringComparison.Ordinal),
                prior.Scope != OperationScope.AbilityTrigger || priorSpec.Trigger?.Kind != "strike_card_drawn", atom);
        if (atom.Template == "I:AddCardReward")
            Check("fatal_card_reward", prior.Scope != OperationScope.ConditionalTrigger
                    || !prior.ChineseText.StartsWith("斩杀时", StringComparison.Ordinal),
                prior.Scope != OperationScope.ConditionalTrigger || priorSpec.Condition?.Kind != "fatal", atom);
        if (atom.Template is "I:ReplayAttack" or "I:SetCostZero")
            Check("next_attack_payoff", atom.ChineseText == "将该攻击牌额外打出1次。"
                    || atom.Template == "I:SetCostZero",
                true, atom);
        if (spec.Flags.Contains("referenced_non_attack_exhaust")
            || atom.ChineseText == "消耗那张非攻击牌。")
            Check("non_attack_exhaust", atom.ChineseText == "消耗那张非攻击牌。"
                    && (prior.Scope != OperationScope.ConditionalTrigger
                        || !prior.ChineseText.StartsWith("每消耗一张手牌中的非攻击牌", StringComparison.Ordinal)),
                spec.Flags.Contains("referenced_non_attack_exhaust")
                    && (prior.Scope != OperationScope.ConditionalTrigger
                        || priorSpec.Trigger?.Kind != "for_each_exhausted_non_attack"), atom);
        if (spec is { Opcode: "create_copy", Variant: "referenced_attack" }
            || atom.ChineseText == "将那张攻击牌的一张复制加入手牌。")
            Check("third_attack_copy", atom.ChineseText == "将那张攻击牌的一张复制加入手牌。"
                    && (prior.Scope != OperationScope.AbilityTrigger
                        || !prior.ChineseText.Contains("第3张攻击牌", StringComparison.Ordinal)),
                spec is { Opcode: "create_copy", Variant: "referenced_attack" }
                    && (prior.Scope != OperationScope.AbilityTrigger
                        || priorSpec.Trigger?.Kind != "nth_attack_played_this_turn"
                        || OperationRuntimeSpecCompiler.StaticLiteralValue(prior, "threshold", 1) != 3), atom);
    }

    private bool CanCompleteFinalPlannedSlot(IroncladCardRecipe shell, int cost, int starCost,
        bool hasStarCostX, IReadOnlyList<GeneratorOperation> previous, ComponentAtom atom)
    {
        var parameters = previous.LastOrDefault() is { } pending
            && CardEffectRules.TriggerNeedsLinkedEffect(pending)
            && (!RequiresPlayerChoice(atom) || TriggerSupportsPlayerChoice(pending))
                ? new Dictionary<string, int> { ["triggerIndex"] = previous.Count - 1 }
                : new Dictionary<string, int>();
        var preview = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
            parameters, RequiresSingleTarget: atom.RequiresSingleTarget,
            RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom), LocalizedText: LocalizedText(atom));
        // These operations can grow the planned component count after selection.  Leave them to the existing
        // dependency/downside completion rules rather than treating the current slot as truly final.
        if (CardEffectRules.TriggerNeedsLinkedEffect(preview)
            || CardEffectRules.IsNegativeEffect(preview))
            return true;

        var prospective = previous.Append(preview).ToArray();
        if (shell.Type == GeneratedCardType.Attack
            && !CardEffectRules.HasAttackClassifyingDamage(prospective))
            return false;
        if (shell.Type == GeneratedCardType.Skill
            && CardEffectRules.HasAttackClassifyingDamage(prospective))
            return false;
        if (shell.Target == TargetMode.SingleEnemy
            && !previous.Any(CardEffectRules.RequiresSingleEnemyTarget)
            && !atom.RequiresSingleTarget)
            return false;
        var hasPrintedPayment = cost != 0 || starCost > 0 || hasStarCostX;
        if (hasPrintedPayment
            && !previous.Any(CardEffectRules.IsBeneficialEffect)
            && !CardEffectRules.IsBeneficialEffect(preview))
            return false;
        return true;
    }

    private static bool IsStandaloneKeywordOperation(ComponentAtom atom) =>
        OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("standalone_keyword");

    private static bool RequiresPlayerChoice(ComponentAtom atom) =>
        atom.CardReference is CardReferenceRequirement.HandCard or CardReferenceRequirement.HandAttack
        || CardEffectRules.IsAtomicChoiceProxy(atom.Template)
        || atom.Template is "N:Discard" or "I:GrantSlyToHandSkillThisTurn" or "I:CopySelectedCardNextTurn"
            or "CL:TransformSelectedHandCards" or "CL:ExhaustUpToHandCards"
            or "CL:MoveSelectedSkillDrawToHand" or "CL:MoveSelectedAttackDrawToHand"
            or "CL:ChooseFromRandomDrawCards" or "CL:ChooseDrawCardToHand"
            or "R:MoveDiscardCardToDrawTop" or "R:PlaySelectedSkillMultipleTimes"
            or "R:PutSelectedHandCardsOnDraw" or "R:PutSelectedHandCardOnDraw" or "R:CopySelectedColorlessCard"
            or "NCR:ExhaustSelectedDrawCard" or "NCR:MoveDiscardCardToHand"
            or "D:MoveDiscardCardToHand"
            or "I:PlayTopCardAndExhaust" or "I:PlayTopXCards"
            or "CL:PlayTopDrawCard" or "D:AutoPlayRandomAttackFromDraw"
            or "I:AutoPlayRandomAttackFromHand" or "I:PlayAtRandomEnemy"
        || OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("requires_player_choice");

    private static bool TriggerSupportsPlayerChoice(GeneratorOperation trigger)
        => CardEffectRules.TriggerSupportsChoiceContext(trigger);

    private IReadOnlyList<CardTag> SampleTags(GeneratedCardType type, IReadOnlyList<GeneratorOperation> operations,
        GeneratedRarity operationsRarity, int energyCost, int starCost, bool hasStarCostX)
    {
        var tags = new List<CardTag>();
        foreach (var tag in Enum.GetValues<CardTag>())
        {
            if (!_keywordPolicy.AllowsBase(tag)) continue;
            if (tag == CardTag.Sly) continue;
            if (tag == CardTag.Strike && type != GeneratedCardType.Attack) continue;
            if (tag == CardTag.Defend && type != GeneratedCardType.Skill) continue;
            if (tag == CardTag.Exhaust
                && (type == GeneratedCardType.Power
                    || operations.Any(CardEffectRules.IsCombatBaseDamageIncrease)
                    || operations.Any(operation => operation.Template is
                        "D:IncreaseThisCardCost" or "D:IncreaseAllClaws"))) continue;
            if (tag is CardTag.Retain or CardTag.Ethereal
                && tags.Any(existing => existing is CardTag.Retain or CardTag.Ethereal)) continue;
            var willHaveExhaust = tags.Contains(CardTag.Exhaust)
                || type != GeneratedCardType.Power && operations.Any(CardEffectRules.IsRestrictedEffect);
            if (SampleTag(tag, operationsRarity, energyCost, starCost, hasStarCostX, type, willHaveExhaust))
                tags.Add(tag);
        }
        return tags;
    }

    private bool SampleTag(CardTag tag, GeneratedRarity rarity, int energyCost, int starCost,
        bool hasStarCostX, GeneratedCardType type, bool hasExhaust)
    {
        if (!_catalog.TagCounts.TryGetValue(tag, out var count)) return false;
        var rarityRecipeCount = _recipeCountsByRarity.GetValueOrDefault(rarity);
        var rarityCount = _tagCountsByRarity.GetValueOrDefault((rarity, tag));
        var numerator = rarityCount * 4 + count;
        var denominator = rarityRecipeCount * 4 + _catalog.Recipes.Count;
        if (tag == CardTag.Sly)
            numerator = SlyKeywordTuning.AdjustTagNumerator(numerator, _character, _unlockComponentRoles);
        if (tag == CardTag.Strike)
            numerator = AdjustStrikeTagNumerator(numerator, _character, _unlockComponentRoles);
        if (tag == CardTag.Ethereal)
            numerator = AdjustEtherealTagNumerator(numerator, _character, _unlockComponentRoles);
        if (tag == CardTag.Retain)
            numerator = (int)PercentWeight.Apply(numerator,
                CardKeywordTuning.RetainWeightPercent(energyCost, starCost, hasStarCostX));
        if (tag == CardTag.Innate)
            numerator = (int)PercentWeight.Apply(numerator,
                CardKeywordTuning.InnateWeightPercent(energyCost, starCost, hasStarCostX, type, hasExhaust));
        if (numerator > 0)
            numerator = (int)Math.Max(1, PercentWeight.Apply(numerator,
                EffectSelectionTuning.NegativeKeywordRarityWeight(tag, rarity)));
        return denominator > 0 && _random.Next(denominator) < numerator;
    }

    private IReadOnlyList<string> SampleCustomKeywords(GeneratedCardType type,
        IReadOnlyList<GeneratorOperation> operations, GeneratedRarity rarity, int energyCost, int starCost,
        bool hasStarCostX, IReadOnlyList<CardTag> nativeTags)
    {
        if (_catalog.CustomKeywordCounts.Count == 0) return [];
        var selected = new List<string>();
        var rarityRecipeCount = _recipeCountsByRarity.GetValueOrDefault(rarity);
        foreach (var keywordId in _catalog.CustomKeywordCounts.Keys.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!_keywordPolicy.AllowsCustomBase(keywordId)) continue;
            var context = new ComponentKeywordGenerationContext(_profileId, _character, rarity, type,
                energyCost, starCost, hasStarCostX, operations, nativeTags, selected);
            if (!ComponentKeywordApi.CanAttach(keywordId, context)) continue;
            var numerator = _customKeywordCountsByRarity.GetValueOrDefault((rarity, keywordId)) * 4
                + _catalog.CustomKeywordCounts.GetValueOrDefault(keywordId);
            var denominator = rarityRecipeCount * 4 + _catalog.Recipes.Count;
            if (numerator > 0 && denominator > 0 && _random.Next(denominator) < numerator)
                selected.Add(keywordId);
        }
        return selected;
    }

    internal static int AdjustStrikeTagNumerator(int numerator, GeneratedCharacter character,
        bool unifiedChaos) => character == GeneratedCharacter.Ironclad && !unifiedChaos
        ? Math.Max(1, (numerator * 150 + 50) / 100)
        : numerator;

    /// <summary>
    /// Retain is sampled before Ethereal and the two printed keywords are mutually exclusive. That collision plus
    /// the shared Basic/Ancient downside prior lowers Necrobinder's finished-pool Ethereal rate. Restore the tag
    /// numerator before rarity weighting; the calibrated 5-rarity audit lands near its native 9.30% card rate.
    /// Ultimate Chaos keeps its one shared sixth profile and deliberately receives no character correction.
    /// </summary>
    internal static int AdjustEtherealTagNumerator(int numerator, GeneratedCharacter character,
        bool unifiedChaos) => character == GeneratedCharacter.Necrobinder && !unifiedChaos
        ? Math.Max(1, (numerator * 150 + 50) / 100)
        : numerator;

    private T Pick<T>(IReadOnlyList<T> values) => values[_random.Next(values.Count)];

    /// <summary>
    /// Samples a component family by native-pool occurrence count, then samples a semantic variant within that family.
    /// Common families such as damage therefore outrank one-card mechanics. Family selection uses native per-card
    /// frequency; variant selection uses native rarity/type/target/character context. Whole-card budgeting assigns
    /// numeric magnitude later. Every weight remains positive, preserving reachability of every native assembly.
    /// </summary>
    private ComponentAtom PickForRarity(
        IReadOnlyList<ComponentAtom> atoms,
        GeneratedRarity rarity,
        int cost,
        GeneratedCardType type,
        TargetMode target,
        IReadOnlyList<GeneratorOperation> previous,
        double currentEffectiveCost)
    {
        var families = atoms.GroupBy(atom => atom.FamilyKey).ToArray();
        var family = PickWeighted(
            families,
            group => FamilySelectionWeight(group, rarity, cost, type, target, previous,
                currentEffectiveCost));
        var variants = family.ToArray();
        var finaleCondition = previous.Any(IsDrawPileEmptyCondition);
        return PickWeighted(
            variants,
            atom => VariantSelectionWeight(atom, variants, rarity, cost, type, target, previous, finaleCondition));
    }

    private long FamilySelectionWeight(IGrouping<string, ComponentAtom> family, GeneratedRarity rarity, int cost,
        GeneratedCardType type, TargetMode target, IReadOnlyList<GeneratorOperation> previous,
        double currentEffectiveCost)
    {
        var familyAtoms = family.ToArray();
        // The base probability is one direct native-pool prior and never reads a numeric value-fit score. Ultimate
        // Chaos supplies the combined catalog, so this same call yields the pool-size-weighted six-pool average.
        // The remaining multipliers are relationship/safety policies (Power foundation, repeat loops, downsides),
        // not a second hand-authored per-character occurrence table.
        var sourcePriorWeight = _frequencyTracker.SourcePriorWeight(rarity, type, family.Key, previous);
        var atomAdjustmentWeight = Math.Max(1, (int)Math.Round(familyAtoms.Average(atom =>
            EffectSelectionTuning.ApplyAtomAdjustments(100, atom, rarity,
                _specialXMode == SpecialXGenerationMode.Forced, type))));
        var adjusted = sourcePriorWeight * (long)atomAdjustmentWeight;
        adjusted = PercentWeight.Apply(adjusted,
            EffectSelectionTuning.PowerAuxiliaryWeight(family, type, previous));
        adjusted = PercentWeight.Apply(adjusted,
            EffectSelectionTuning.PowerFoundationAcceptanceWeight(family, type, previous));
        adjusted = PercentWeight.Apply(adjusted,
            EffectSelectionTuning.RestrictedRunGrowthAnchorWeight(family, type, previous, _character,
                _unlockComponentRoles));
        adjusted = PercentWeight.Apply(adjusted,
            EffectSelectionTuning.NativeExhaustReplayCompanionWeight(family, previous));
        adjusted = PercentWeight.Apply(adjusted,
            EffectSelectionTuning.UltimateOstyFamilyWeight(family, _unlockComponentRoles));
        adjusted = PercentWeight.Apply(adjusted,
            EffectSelectionTuning.TriggeredResourceGainWeight(family, previous));
        adjusted = PercentWeight.Apply(adjusted,
            EffectSelectionTuning.TriggeredCardCreationWeight(family, previous));
        adjusted = PercentWeight.Apply(adjusted,
            EffectSelectionTuning.TriggeredPermanentDownsideWeight(family, previous));
        adjusted = PercentWeight.Apply(adjusted,
            EffectSelectionTuning.TriggeredReplayWeight(family, previous));
        adjusted = PercentWeight.Apply(adjusted,
            AggressiveModeTuning.EnergyGainSelectionWeight(_balancedValues, family));
        adjusted = BasisPointWeight.Apply(adjusted,
            EffectSelectionTuning.RepeatedFamilyWeightBasisPoints(family, previous));
        adjusted = PercentWeight.Apply(adjusted,
            _frequencyTracker.SelectionWeight(rarity, type, family.Key, previous));
        return Math.Max(1, PercentWeight.Apply(adjusted,
            ResourceEconomyModel.NegativeFamilyWeight(family, currentEffectiveCost)));
    }

    private long VariantSelectionWeight(ComponentAtom atom, IReadOnlyList<ComponentAtom> variants,
        GeneratedRarity rarity, int cost, GeneratedCardType type, TargetMode target,
        IReadOnlyList<GeneratorOperation> previous, bool finaleCondition)
    {
        // Variant identity follows its native occurrence count. The sampled value is fitted to the whole-card
        // budget later; multiplying this choice by a damage/Block value-fit score coupled occurrence back to the
        // balance model and made changing one coefficient silently alter which mechanics appeared.
        var weight = (long)OriginalOccurrenceWeight(atom, rarity, type, target, previous);
        // The family pass prices the total mass of negative Basic variants; this conditional variant pass keeps
        // mixed families (for example beneficial and harmful T:Apply variants) from restoring that mass.
        weight = PercentWeight.Apply(weight,
            EffectSelectionTuning.BasicDownsideVariantWeight(atom, rarity));
        weight = PercentWeight.Apply(weight, EffectSelectionTuning.RepeatedVariantWeight(atom, previous));
        weight = PercentWeight.Apply(weight,
            EffectSelectionTuning.CopyThisCardPowerWeight(atom, rarity, type));
        return Math.Max(1, weight * (finaleCondition ? FinaleVariantWeight(atom, variants) : 1));
    }

    private static bool IsDrawPileEmptyCondition(GeneratorOperation operation) =>
        operation.Template == "C:playableIfDrawPileEmpty";

    internal static NativeComponentRole SelectionRole(IReadOnlyList<GeneratorOperation> previous,
        GeneratedCardType type, IEnumerable<ComponentAtom> family) =>
        EffectBalanceModel.LinkedTrigger(previous)?.Scope switch
        {
            OperationScope.AbilityTrigger => NativeComponentRole.AbilityPayoff,
            OperationScope.ConditionalTrigger => NativeComponentRole.ConditionalPayoff,
            _ when type == GeneratedCardType.Power && family.Any(atom =>
                atom.Scope is not (OperationScope.AbilityTrigger or OperationScope.AbilityRule)) =>
                NativeComponentRole.AbilityFoundation,
            _ => NativeComponentRole.Unlinked
        };


    private static bool IsGrandFinalePayoff(ComponentAtom atom) =>
        atom.Template == "N:AllD" && OperationRuntimeSpecCompiler.FixedValue(atom, "damage") == 60;

    private static long FinaleVariantWeight(ComponentAtom atom, IReadOnlyList<ComponentAtom> variants)
    {
        if (!TryBeneficialMagnitude(atom, out var magnitude)) return 1;
        var maximum = variants.Select(candidate => TryBeneficialMagnitude(candidate, out var value) ? value : -1).Max();
        // The play restriction is exceptionally severe. Within whatever ordinary effect family was selected,
        // put virtually all mass on its largest original value without making lesser values unreachable.
        return magnitude == maximum ? 10_000L : 1L;
    }

    private static bool TryBeneficialMagnitude(ComponentAtom atom, out int magnitude)
    {
        magnitude = 0;
        var operation = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
            new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget,
            RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom), LocalizedText: LocalizedText(atom));
        if (!CardEffectRules.IsBeneficialEffect(operation)) return false;
        var numbers = OperationRuntimeSpecCompiler.ExplicitFixedValueSlots(atom)
            .Select(slot => slot.BaseValue + slot.Offset).ToArray();
        if (numbers.Length == 0) return false;
        magnitude = CardEffectRules.IsEnemyDamage(atom)
            ? EffectiveDamageValue(atom, numbers[0])
            : numbers.Max();
        return true;
    }

    private int OriginalOccurrenceWeight(ComponentAtom atom, GeneratedRarity rarity, GeneratedCardType type,
        TargetMode target, IReadOnlyList<GeneratorOperation> previous)
        => _frequencyTracker.VariantWeight(atom, rarity, type, target, previous);

    /// <summary>
    /// Numeric literals in the authoring Markdown are native calibration samples, not component identity. Instantiate
    /// structured integer slots here: retain a nonzero native-value branch for exact reconstruction, and otherwise
    /// sample a continuous integer range from cost, rarity, effect count, and trigger frequency.
    /// </summary>
    private ComponentAtom InstantiateNumericSlotsStructured(ComponentAtom atom, GeneratedRarity rarity, int cost,
        int componentCount, IReadOnlyList<GeneratorOperation> previous, bool zeroResourceCost,
        GeneratedCardType cardType, bool allowOriginalValueBranch = true)
    {
        // Helix Drill's divisor is a semantic threshold rather than a reward. Sample only the supported 1/2-Energy
        // interval; the balance model prices those outcomes as two/one resolutions from two Energy spent elsewhere.
        if (atom.Template == "D:ForEachEnergySpentThisTurn")
            return ReplaceAtomFixedValue(atom, "threshold", _random.Next(2) + 1);
        if (atom.Template == "C:grantNextAttacksThisTurn")
        {
            // One-Two Punch is overwhelmingly “the next 1 Attack”. A small tail broadens the slot without
            // turning the already-multiplicative grant into a routine multi-card engine.
            var count = _random.Next(100) switch { < 90 => 1, < 99 => 2, _ => 3 };
            return ReplaceAtomFixedValue(atom, "threshold", count);
        }
        if (atom.Template == "N:Discard")
        {
            // Mandatory discard has a dedicated distribution and grants no positive budget. Do not let rarity,
            // cost, trigger cadence or the number of reward lines inflate its count.
            return ReplaceAtomFixedValue(atom, "count",
                NumericGenerationTuning.SampleMandatoryDiscardCount(_random));
        }
        var atomSpec = OperationRuntimeSpecCompiler.GetOrCompile(atom);
        if (atomSpec is { Opcode: "exhaust_card", Variant: "selected" }
            && !atomSpec.Flags.Contains("up_to"))
        {
            // Native mandatory selection always chooses exactly one card, from either Hand or draw pile.
            var count = NumericGenerationTuning.SampleMandatoryHandExhaustCount(_random);
            return ReplaceAtomFixedValue(atom, "count", count);
        }
        var conditionLines = previous.Count(operation =>
            operation.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger
            || CardEffectRules.IsDependencyPrefix(operation));
        if (EffectBalanceModel.IsCondition(atom)) conditionLines++;
        var benefitLines = Math.Max(1, componentCount - conditionLines);
        // A draw line is disproportionately valuable on a genuinely free card. Count it as one extra benefit
        // line so its own number and every following reward split a smaller share of the card's value budget.
        // Fixed-Star cards are excluded: their printed Energy cost may be 0, but they are not resource-free.
        if (zeroResourceCost && (IsDrawEffect(atom) || previous.Any(IsDrawEffect)))
            benefitLines++;
        // Basic cards should communicate their primary combat job clearly. When direct Damage or Block is chosen,
        // let that line retain a larger share of the existing whole-card budget instead of splitting every reward
        // evenly. The final envelope and mixed offense/defense surcharge still cap the complete card, so this shifts
        // allocation rather than creating extra value.
        if (rarity == GeneratedRarity.Basic && IsBasicCombatFoundation(atom))
            benefitLines = Math.Max(1, benefitLines - 1);
        // Original Osty damage cards often combine their low printed number with other strong clauses. Once that
        // operation is recombined independently, preserving those samples too often makes it systematically
        // under-budget. Exact original reconstruction remains possible, but most instances follow the shared curve.
        var preserveOriginalPercent = allowOriginalValueBranch
            ? _valuePolicy.OriginalValueChance(atom, benefitLines)
            : 0;
        if (rarity == GeneratedRarity.Basic)
            preserveOriginalPercent = Math.Min(3, preserveOriginalPercent);
        if (CardEffectRules.IsDelayedEffect(atom)
            || EffectBalanceModel.LinkedTrigger(previous) is { } linkedTrigger
                && CardEffectRules.IsDelayedEffect(linkedTrigger))
            preserveOriginalPercent = 0;
        else if (EffectBalanceModel.LinkedTrigger(previous) is { } repeatedTrigger
                 && EffectBalanceModel.HasRepeatedOrMultiplicativePayoff(repeatedTrigger))
            // A native payoff number is balanced together with its native Power cost and complete rules text.
            // Arbitrary recombinations must always use trigger-frequency pricing. Exact source recipes already have
            // their own reconstruction route, so no fixed native number needs to bypass the scalable budget here.
            preserveOriginalPercent = 0;
        if (rarity == GeneratedRarity.Basic && cost > 0 && CardEffectRules.IsStarGainOperation(atom))
            preserveOriginalPercent = 0;
        if (atom.Template.Contains(":Proxy", StringComparison.Ordinal) || _random.Next(100) < preserveOriginalPercent)
            return atom;
        var sourceOperation = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
            new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget,
            RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom), LocalizedText: LocalizedText(atom));
        var numericSlots = OperationRuntimeSpecCompiler.ExplicitFixedValueSlots(sourceOperation);
        if (numericSlots.Count == 0) return atom;
        var updatedOperation = sourceOperation;
        for (var index = 0; index < numericSlots.Count; index++)
        {
            var numericSlot = numericSlots[index];
            var original = numericSlot.BaseValue + numericSlot.Offset;
            if (original == 0 || OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("percentage_value")
                && !CardEffectRules.IsEnemyDamageAmplificationRule(atom)) continue;
            int replacement;
            if (index == 0 && atom.Template is "A:whenEnergyCostAtLeast" or "NCR:WheneverHighCostCardPlayed")
            {
                replacement = NumericGenerationTuning.SampleHighCostTriggerThreshold(_random);
            }
            else if (index == 0 && atom.Template == "R:ReturnAfterSkillsPlayed")
            {
                replacement = NumericGenerationTuning.SampleSkillsPerReturnThreshold(_random);
            }
            else if (index == 0 && atom.Template == "NCR:IncreaseAllCardCostsThisTurn")
            {
                replacement = NumericGenerationTuning.SampleAllCardsCostIncrease(_random);
            }
            else if (index == 0 && atom.Template == "D:IncreaseThisCardCost")
            {
                replacement = NumericGenerationTuning.SampleSelfCardCostIncrease(_random);
            }
            else if (index == 0 && atom.Template == "N:HP-")
            {
                replacement = NumericGenerationTuning.SampleSelfHpLoss(_random, previous);
            }
            else if (index == 0 && CardEffectRules.IsEnergyGainOperation(atom))
            {
                var energy = NumericGenerationTuning.SampleEnergyGainValue(_random, EnergyBudget(rarity, cost));
                var scaledEnergy = _valuePolicy.ScaleRewardCenter(atom, energy, benefitLines,
                    rarity, previous, _random, _balancedValues);
                replacement = _valuePolicy.ClampSampledValue(atom, index,
                    _valuePolicy.ApplyValueBonuses(atom, scaledEnergy, _character,
                        _unlockComponentRoles),
                    previous, _character, _unlockComponentRoles);
            }
            else if (index == 0 && CardEffectRules.IsRandomCardGeneration(atom))
            {
                replacement = NumericGenerationTuning.SampleRandomGeneratedCardCount(_random);
            }
            else
            {
                var center = NumericSlotCenter(atom, rarity, cost, index, original, benefitLines, previous,
                    cardType);
                var payoffScale = _valuePolicy.PayoffScalePercent(previous);
                var compactBasicReward = rarity == GeneratedRarity.Basic
                    && _valuePolicy.IsScalableReward(atom)
                    && !CardEffectRules.IsNegativeEffect(atom);
                var sampled = compactBasicReward
                    ? center
                    : _valuePolicy.SampleAroundCenter(_random, center, payoffScale);
                sampled = _valuePolicy.AdjustSampledValue(_random, atom, index, sampled);
                replacement = _valuePolicy.ClampSampledValue(atom, index, sampled, previous,
                    _character, _unlockComponentRoles);
                if (index == 0 && rarity == GeneratedRarity.Basic && cost > 0
                    && CardEffectRules.IsStarGainOperation(atom))
                    replacement = Math.Max(2, replacement);
            }
            if (index == 0 && atom.Template == "R:IfEnergyXAtLeast")
                replacement = Math.Clamp(replacement, 1, 4);
            if (index == 0 && atom.Template == "R:DoubleEitherXAtThreshold")
                replacement = 4;
            if (replacement != original)
                OperationRuntimeSpecCompiler.TryReplaceFixedValue(updatedOperation, numericSlot.Id,
                    replacement, out updatedOperation);
        }
        return updatedOperation.ChineseText == atom.ChineseText
            ? atom
            : atom with
            {
                ChineseText = updatedOperation.ChineseText,
                RuntimeSpec = updatedOperation.RuntimeSpec,
                LocalizedText = updatedOperation.LocalizedText
            };
    }

    private static ComponentAtom ReplaceAtomFixedValue(ComponentAtom atom, string slotId, int value)
    {
        var operation = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
            new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget,
            RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom), LocalizedText: LocalizedText(atom));
        return OperationRuntimeSpecCompiler.TryReplaceFixedValue(operation, slotId, value, out var updated)
            ? atom with
            {
                ChineseText = updated.ChineseText,
                RuntimeSpec = updated.RuntimeSpec,
                LocalizedText = updated.LocalizedText
            }
            : atom;
    }

    private static bool IsDrawEffect(ComponentAtom atom) => atom.Template is
        "N:Draw" or "N_DRAW" or "I:DrawAndBlockIfSkill" or "I:DrawWithRetain";

    private static bool IsDrawEffect(GeneratorOperation operation) => operation.Template is
        "N:Draw" or "N_DRAW" or "I:DrawAndBlockIfSkill" or "I:DrawWithRetain";

    private static bool IsBasicCombatFoundation(ComponentAtom atom)
    {
        if (CardEffectRules.IsEnemyDamage(atom)) return true;
        return OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("printed_block_value");
    }

    private int NumericSlotCenter(ComponentAtom atom, GeneratedRarity rarity, int cost, int slot, int original,
        int benefitLines, IReadOnlyList<GeneratorOperation> previous, GeneratedCardType cardType)
    {
        var center = NumericSlotCenterUnscaled(atom, rarity, cost, slot, original, cardType);
        if (slot > 0 && atom.Template is "N:AllD" or "N:RandomD" or "N:RandomPoison")
            return center;
        // Only Debilitate's own multi-turn payload bypasses benefit-line scaling here. Generic next-turn
        // rewards (for example N:NextTurnDraw) still share the assembled card budget exactly as in 0.2.118.
        if (slot == 0 && atom.Template == "NCR:DoubleVulnerableWeak")
            return center;
        var scaled = _valuePolicy.ScaleRewardCenter(atom, center, benefitLines, rarity, previous, _random,
            _balancedValues);
        scaled = ApplyCardTypeOneShotPricing(atom, scaled, cardType, previous);
        return _valuePolicy.ApplyValueBonuses(atom, scaled, _character, _unlockComponentRoles);
    }

    private int NumericSlotCenterUnscaled(ComponentAtom atom, GeneratedRarity rarity, int cost, int slot, int original,
        GeneratedCardType cardType)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(atom);
        // Numbers printed in triggers/rules are thresholds or durations, not rewards. Rarity and card cost must
        // never make these conditions harder as a side effect of ordinary numeric scaling.
        if (atom.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger or OperationScope.AbilityRule)
            return original;
        if (slot == 0 && CardEffectRules.IsDirectOrbChannel(atom))
        {
            // Orb count is much more multiplicative than ordinary scalar rewards. One is the low-cost mode,
            // two is reserved for cards that pay at least two total resource tiers, and three remains a tail.
            return cost >= 2 ? 2 : 1;
        }
        if (slot == 0 && rarity == GeneratedRarity.Basic && cost > 0
            && CardEffectRules.IsStarGainOperation(atom))
            // Venerate is the native starting-card reference: one Energy buys two Stars. A one-Star version loses
            // half the payoff while still consuming the same draw and Energy, so do not use it for random Basics.
            return 2;
        // Losing an Orb Slot is a downside/payment, not a reward. Never make the payment harsher because the
        // shell is rarer or more expensive.
        if (slot == 0 && atom.Template == "D:LoseOrbSlots")
            return 1;
        if (slot == 0 && atom.Template == "D:GainOrbSlots")
            return 1;
        if (slot == 0 && atom.Template == "D:IncreaseAllClaws")
            return rarity == GeneratedRarity.Ancient && cost >= 3 ? 3 : 2;
        if (slot == 0 && atom.Template is "NCR:IncreaseAllCardCostsThisTurn" or "D:IncreaseThisCardCost")
            return 1;
        if (atom.Template == "I:DrawAndBlockIfSkill")
            return slot == 0 ? DrawBudget(rarity, cost) : BlockBudget(rarity, cost, BalanceCharacter);
        if (slot == 0 && atom.Template is "NCR:ApplyDoom" or "NCR:ApplyDoomAll")
            return DoomBudget(atom, rarity, cost);
        if (slot == 0 && atom.Template is "T:Poison" or "N:AllPoison" or "N:RandomPoison")
            return PoisonBudget(rarity, cost, atom.Template != "T:Poison");
        if (slot == 0 && atom.Template == "N:Thorns")
            return ThornsBudget(rarity, cost);
        if (slot == 0 && CardEffectRules.IsPositivePermanentStatGain(atom))
            return atom.Template == "D:GainFocus"
                ? PermanentFocusBudget(rarity, cost)
                : cardType == GeneratedCardType.Power
                    ? PowerStrengthOrDexterityBudget(rarity, cost)
                : StrengthBudget(rarity, cost);
        if (slot == 0 && CardEffectRules.IsPermanentStrengthOrDexterityChange(atom))
            return StrengthBudget(rarity, cost);
        if (slot == 0 && CardEffectRules.IsEnemyStrengthReduction(atom))
            return EnemyStrengthReductionBudget(atom, rarity, cost);
        if (slot == 0 && CardEffectRules.IsEnemyDamage(atom))
        {
            var budget = atom.Template == "T:D"
                ? TargetDamageBudget(rarity, cost, BalanceCharacter)
                : atom.Template == "N:RandomD"
                    ? RandomDamageBudget(rarity, cost, BalanceCharacter,
                        CardEffectRules.IsIntrinsicMultiHitDamage(atom))
                : atom.Template == "N:AllD"
                    ? AllDamageBudget(rarity, cost, BalanceCharacter,
                        CardEffectRules.IsIntrinsicMultiHitDamage(atom))
                    : atom.Template == "NCR:OstyDamage"
                        ? OstyDamageBudget(rarity, cost)
                    : atom.Template == "NCR:OstyAllDamage"
                        ? OstyAllDamageBudget(rarity, cost)
                    : Math.Max(1, original + RarityRank(rarity) - 2 + Math.Max(0, cost - 1));
            // Multi-hit damage budgets describe the whole effect. Random/area reliability and hit synergy are
            // already represented by their shared damage-value multiplier, so split the total directly by hits.
            var printedHits = spec.Values.FirstOrDefault(value => value.Id == "hits" && value.Source == "fixed")
                ?.BaseValue ?? 1;
            return printedHits > 1
                ? Math.Max(1, (budget + Math.Min(5, printedHits) - 1) / Math.Min(5, printedHits))
                : budget;
        }
        if (slot == 0 && atom.Template == "R:Forge")
            // Wrought in War establishes the cleanest native exchange rate: 7 damage and Forge 7 on the same
            // one-cost Common. Treat direct Forge as an equal scalar reward, not the former 2.3x premium.
            return TargetDamageBudget(rarity, cost, BalanceCharacter);
        if (slot > 0 && atom.Template is "N:AllD" or "N:RandomD" or "N:RandomPoison")
            return RepeatCountBudget(original);
        if (atom.Template == "M:base" && spec.Variant == "strength_scaled")
            return slot == 0 ? 1 : BlockBudget(rarity, cost, BalanceCharacter);
        if (CardEffectRules.EffectFamily(atom) == "block"
            && !spec.Flags.Contains("repeated_or_multiplicative"))
            return BlockBudget(rarity, cost, BalanceCharacter);
        if (OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("legacy_direct_draw_center"))
            return DrawBudget(rarity, cost);
        if (atom.Template is "N:E" or "D:GainEnergy" or "NCR:GainEnergy" or "R:GainEnergy")
            return EnergyBudget(rarity, cost);
        if (atom.Template is "R:GainVigor" or "CL:GainVigor")
            return StrengthBudget(rarity, cost);
        if (CardEffectRules.EffectFamily(atom) == "strength"
            && !CardEffectRules.IsEnemyStrengthReduction(atom))
            return StrengthBudget(rarity, cost);
        if (atom.Template == "T:Apply" && spec.Variant is "vulnerable" or "vulnerable_double")
            return VulnerableBudget(rarity, cost);
        if (atom.Template == "N:HP-") return Math.Max(1, Math.Min(6, original));

        // Character-specific values also use continuous integer space with mild cost/rarity shifts around the sample.
        var shift = Math.Clamp(RarityRank(rarity) - 2 + Math.Max(0, cost - 1), -2, 4);
        return Math.Max(1, original + shift);
    }

    /// <summary>
    /// A Power leaves the draw/discard cycle when played, so its immediate line is priced like an Exhausted
    /// effect. Permanent Strength/Dexterity/Focus already use native Power baselines (Inflame/Footwork/
    /// Defragment), while the same untriggered effect on a reusable Skill is worth only about two thirds as much.
    /// Other immediate scalable lines on a Power receive the ordinary Exhaust compensation. Triggered effects are
    /// excluded because their frequency is priced by the trigger model instead of by the one-shot card shell.
    /// </summary>
    internal static int ApplyCardTypeOneShotPricing(ComponentAtom atom, int value, GeneratedCardType cardType,
        IReadOnlyList<GeneratorOperation> previous)
    {
        if (value <= 0
            || atom.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger
                or OperationScope.AbilityRule or OperationScope.Modifier
            || CardEffectRules.IsDependencyPrefix(atom)
            || CardEffectRules.IsNegativeEffect(atom)
            || EffectBalanceModel.LinkedTrigger(previous) is not null)
            return value;

        if (CardEffectRules.IsStackablePersistentCombatGain(atom))
            return cardType == GeneratedCardType.Power
                ? value
                : Math.Max(1, (int)Math.Round(value * 2d / 3d, MidpointRounding.AwayFromZero));

        return cardType == GeneratedCardType.Power && EffectBalanceModel.IsScalableReward(atom)
            ? Math.Max(value, (int)Math.Round(value * 1.5d, MidpointRounding.AwayFromZero))
            : value;
    }

    private static int EnemyStrengthReductionBudget(ComponentAtom atom, GeneratedRarity rarity, int cost)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(atom);
        // Generic legacy templates do not all route through CompileTargetPower yet. The compiler-owned
        // this_turn_reference flag is the canonical duration fact for both explicit and fallback specs.
        var temporary = spec.Flags.Contains("this_turn_reference");
        if (!temporary)
            return rarity == GeneratedRarity.Ancient && cost >= 2 ? 2 : 1;

        var center = cost switch { <= 0 => 1, 1 => 2, 2 => 3, 3 => 4, _ => 5 };
        if (spec.Target == "all_enemies"
            || atom.Template is "N:AllTempStrengthLoss" or "R:EnemiesLoseStrengthThisTurn"
                or "R:EnemiesLoseStrength")
            center--;
        if (rarity == GeneratedRarity.Ancient) center++;
        return Math.Max(1, center);
    }

    // Rarity already raises the whole effect budget and must not also multiply its repeat count. Preserve continuous
    // space by sampling one step around the native one-to-four-repeat anchor.
    private static int RepeatCountBudget(int original) => Math.Clamp(original, 1, 4);

    /// <summary>
    /// Explicitly scarce effects share the existing semantic-calibration path instead of adding an independent
    /// occurrence multiplier. Rare and Ancient keep the former upper-rarity rate; lower rarities fall away
    /// progressively, and every operation remains reachable.
    /// </summary>
    internal static int ExplicitRareOperationRarityWeight(ComponentAtom atom, GeneratedRarity rarity)
    {
        return ExplicitRareOperationTier(atom) switch
        {
            RareOperationTier.None => 100,
            // Very-rare persistent rules stay available everywhere for reconstruction, but are materially
            // scarcer than the broad rare tier. This includes Plating's decaying Block engine and the Regent's
            // pool-wide Sovereign Blade all-enemy conversion.
            RareOperationTier.VeryRare => rarity switch
            {
                GeneratedRarity.Basic => 5,
                GeneratedRarity.Common => 12,
                GeneratedRarity.Uncommon => 28,
                GeneratedRarity.Rare or GeneratedRarity.Ancient => 45,
                _ => 100
            },
            _ => rarity switch
            {
                GeneratedRarity.Basic => 10,
                GeneratedRarity.Common => 22,
                GeneratedRarity.Uncommon => 45,
                GeneratedRarity.Rare or GeneratedRarity.Ancient => 70,
                _ => 100
            }
        };
    }

    private enum RareOperationTier { None, Rare, VeryRare }

    private static RareOperationTier ExplicitRareOperationTier(ComponentAtom atom)
    {
        if (CardEffectRules.IsPlating(atom) || atom.Template == "R:KingsSwordHitsAllEnemies"
            || CardEffectRules.IsGoldGainOperation(atom)
            || IsVeryRareUtilityOperation(atom))
            return RareOperationTier.VeryRare;
        return IsOrdinaryExplicitRareOperation(atom) ? RareOperationTier.Rare : RareOperationTier.None;
    }

    private static bool IsVeryRareUtilityOperation(ComponentAtom atom)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(atom);
        return atom.Template is "N:DiscardAll" or "I:DrawWithRetain" or "R:FillHandWithDebris"
                or "A:ProxyAtomic_ForbiddenGrimoire" or "I:ProxyAtomic_Transfigure"
                or "A:ruleShivsRetain"
            || atom.Template == "A:when" && spec.Trigger?.Kind == "owner_hp_lost_during_turn";
    }

    internal static bool IsExplicitRareOperation(ComponentAtom atom) =>
        ExplicitRareOperationTier(atom) != RareOperationTier.None;

    internal static bool IsVeryRareOperation(ComponentAtom atom) =>
        ExplicitRareOperationTier(atom) == RareOperationTier.VeryRare;

    private static bool IsOrdinaryExplicitRareOperation(ComponentAtom atom) =>
        CardEffectRules.IsDoubleTargetVulnerable(atom)
        || atom.Template == "N:StrengthPerTargetVulnerable"
        || CardEffectRules.IsCopyThisCardToDiscard(atom)
        || CardEffectRules.IsExhaustAllHand(atom)
        || CardEffectRules.IsEnemyStrengthGain(atom)
        || CardEffectRules.IsRandomCurrentCharacterCardToHand(atom)
        || CardEffectRules.IsReplayGrant(atom)
        || atom.Template == "I:Transform"
            && OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("giant_rock_reference")
        || atom.Template == "A:ProxyAtomic_Parry"
        || atom.Template == "N:RetaliateDamage"
        || atom.Template == "NCR:CopyTargetDebuffsToOthers"
        || IsExplicitRareTemplate(atom.Template)
        || atom.Template == "I:ProxyAtomic_Guards"
        || atom.Template == "A:rule"
            && OperationRuntimeSpecCompiler.GetOrCompile(atom).Variant == "skills_cost_zero"
        || atom.Template is "D:IncreaseThisCardBlockRun" or "NCR:IncreaseThisCardDamageRun"
            or "I:PlayExhaustedShivsAtTarget" or "I:FreeHandThisTurn" or "N:DiscardAll"
            or "I:TriggerPoisonNow" or "A:rulePoisonExtraTriggers" or "R:DoubleEitherXAtThreshold";

    internal static bool IsExplicitRareTemplate(string template) =>
        template is "D:LoseFocus" or "D:LoseTemporaryFocus" or "D:LoseOrbSlots"
            or "N:Heal" or "N_HEAL" or "I:GainMaxHp";

    private int PickComponentCount(GeneratedRarity rarity, int cost, GeneratedCardType type)
    {
        var minimum = _catalog.ComponentCounts.Min();
        return PickWeighted(_catalog.ComponentCounts, count =>
        {
            // Keep five distinct density bands.  The former four-band clamp gave native four- and five-line
            // recipes the same multiplier, leaving an unnecessarily heavy extreme tail even after low-rarity
            // weighting.  Rank four now means five-or-more lines and remains possible, but separately tunable.
            var rank = Math.Clamp(count - minimum, 0, 4);
            var rarityCount = _catalog.Recipes.Count(recipe => recipe.OriginalRarity == rarity && recipe.Atoms.Count == count);
            var occurrenceWeight = rarityCount * 16 + _catalog.ComponentCountCounts[count];
            if (_unlockComponentRoles || _character != GeneratedCharacter.Ironclad)
            {
                // The later characters have more trigger/modifier atoms per printed card.  Sampling their raw
                // atom-count histogram directly makes cheap/basic generated cards much denser than Ironclad's.
                // Use Ironclad as the common calibration prior while retaining each character's own histogram
                // as positive smoothing, so every original assembly remains reachable.
                var reference = CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad);
                var referenceRarityCount = reference.Recipes.Count(recipe =>
                    recipe.OriginalRarity == rarity && recipe.Atoms.Count == count);
                var referenceGlobalCount = reference.ComponentCountCounts.GetValueOrDefault(count);
                occurrenceWeight = referenceRarityCount * 32 + referenceGlobalCount * 2
                    + rarityCount * 2 + _catalog.ComponentCountCounts[count] + 1;
            }
            return (long)occurrenceWeight
                * ComponentCountRarityWeight(rarity, rank)
                * ComponentCountCostWeight(cost, rank)
                * ComponentCountTopRarityNonPowerWeight(rarity, type, rank)
                * ComponentCountPowerWeight(type, rank);
        });
    }

    internal static int ComponentCountRarityWeight(GeneratedRarity rarity, int rank) => rarity switch
    {
        // Basic/Common primarily occupy one or two printed operations.  Three remains a real outcome while the
        // four/five-line tails are deliberately rare rather than forbidden.
        GeneratedRarity.Basic => new[] { 70, 125, 5, 1, 1 }[rank],
        GeneratedRarity.Common => new[] { 110, 100, 30, 8, 2 }[rank],
        // Higher rarities shift smoothly toward two and three operations, retaining both concise one-line cards
        // and uncommon high-density rolls.  Ancient shares Rare component rarity elsewhere, but may be denser.
        GeneratedRarity.Uncommon => new[] { 90, 105, 85, 25, 7 }[rank],
        GeneratedRarity.Rare => new[] { 70, 95, 120, 45, 14 }[rank],
        GeneratedRarity.Ancient => new[] { 55, 85, 130, 65, 24 }[rank],
        _ => 100
    };

    private static int ComponentCountCostWeight(int cost, int rank) => cost switch
    {
        -1 => new[] { 100, 45, 18, 6, 2 }[rank],
        0 => new[] { 100, 58, 28, 9, 3 }[rank],
        1 => new[] { 100, 88, 72, 42, 17 }[rank],
        2 => new[] { 100, 110, 123, 100, 52 }[rank],
        3 => new[] { 100, 123, 148, 135, 82 }[rank],
        >= 4 => new[] { 100, 133, 168, 160, 105 }[rank],
        _ => 100
    };

    internal static int ComponentCountTopRarityNonPowerWeight(GeneratedRarity rarity,
        GeneratedCardType type, int rank)
    {
        if (type == GeneratedCardType.Power) return 100;
        return rarity switch
        {
            // A smooth right shift: one-effect weight is unchanged, while each denser tier gains a little more
            // than the previous one. Ancient receives the larger but still controlled tail.
            GeneratedRarity.Rare => new[] { 100, 104, 110, 116, 120 }[rank],
            GeneratedRarity.Ancient => new[] { 100, 106, 114, 122, 130 }[rank],
            _ => 100
        };
    }

    internal static int ComponentCountPowerWeight(GeneratedCardType type, int rank) =>
        type == GeneratedCardType.Power
            // A Power already converts its persistent foundation and trigger block into multiple operations.
            // Apply only a mild left shift so dense Powers remain possible without matching Attack/Skill density.
            ? new[] { 110, 100, 88, 72, 58 }[rank]
            : 100;

    private IroncladCardRecipe PickShell(GeneratedRarity rarity)
    {
        // X-energy is character-local. Derive its chance from that character's original cards so the
        // Regent's Star-X card cannot accidentally create an Energy-X shell, and no X mechanic leaks roles.
        var rarityRecipes = _catalog.Recipes.Where(recipe => recipe.OriginalRarity == rarity).ToArray();
        var xNumerator = rarityRecipes.Count(recipe => recipe.Cost == -1);
        var denominator = Math.Max(1, rarityRecipes.Length);
        var useX = xNumerator > 0 && _random.Next(denominator) < xNumerator;
        var shells = _catalog.Recipes.Where(recipe => (recipe.Cost == -1) == useX).ToArray();
        var sameRarityCount = shells.Count(recipe => recipe.OriginalRarity == rarity);
        var otherRarityCount = shells.Length - sameRarityCount;
        // About 90% of type/target shells come from the same native rarity; smoothing keeps every cross-rarity shell reachable.
        var sameRarityWeight = sameRarityCount == 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(9d * otherRarityCount / sameRarityCount));
        return PickWeighted(shells, recipe =>
            (recipe.OriginalRarity == rarity ? sameRarityWeight : 1)
            * (rarity == GeneratedRarity.Basic && recipe.Type == GeneratedCardType.Power ? 25 : 100));
    }

    /// <summary>
    /// Samples non-X cost from the v111 native rarity distribution. Basic cards are narrowed around one Energy;
    /// Common and Uncommon use their own pools, while Rare and Ancient share their combined pool. Zero-cost cards use
    /// an explicit weight and cannot be amplified indirectly by shell type.
    /// </summary>
    private int SampleCost(GeneratedCardType type, GeneratedRarity rarity, bool isXCost,
        bool hasStarPayment)
    {
        if (isXCost) return -1;
        // Fixed Energy costs are capped at four. Regent Star cost is a separate resource and is deliberately
        // excluded from this cap.
        var costs = Enumerable.Range(0, 5)
            .Where(cost => CostRarityWeight(cost, rarity, hasStarPayment) > 0)
            .Where(cost => type != GeneratedCardType.Power || cost > 0)
            .ToArray();
        return PickWeighted(costs, cost => CostRarityWeight(cost, rarity, hasStarPayment));
    }

    private int CostRarityWeight(int cost, GeneratedRarity rarity, bool hasStarPayment)
        => EnergyCostTuning.Weight(cost, rarity, _character, _unlockComponentRoles, hasStarPayment);

    private static int BlockBudget(GeneratedRarity rarity, int cost, GeneratedCharacter? character)
    {
        var rank = RarityRank(rarity);
        var rarityBonus = rarity == GeneratedRarity.Basic ? -1 : rank - 1;
        var budget = cost switch
        {
            -1 => 6,
            <= 0 => 4 + Math.Min(2, rank / 2),
            1 => 6 + rarityBonus,
            2 => 11 + rarityBonus,
            3 => 16 + rarityBonus,
            _ => 21 + rarityBonus
        };
        // Ironclad and Regent's original pools sit about one point above the shared one-cost block line.
        if (rarity != GeneratedRarity.Basic && cost > 0
            && character is GeneratedCharacter.Ironclad or GeneratedCharacter.Regent)
            budget++;
        return budget;
    }

    private static int TargetDamageBudget(GeneratedRarity rarity, int cost, GeneratedCharacter? character)
    {
        var rank = RarityRank(rarity);
        if (cost <= 0)
            return cost == -1 ? 6 + rank : 5 + Math.Min(1, rank / 2);
        // One-Energy single-target damage is the calibration anchor for the shared numeric model:
        // Basic 5-7, Common 9-13, Uncommon 11-15, Rare 13-19 and Ancient 20-30 after whole-card floors and the
        // single-effect Ancient lift. Reuse each rarity offset at higher costs so cost growth stays monotonic
        // instead of introducing a one-cost-only exception.
        var rarityBonus = rarity switch
        {
            GeneratedRarity.Basic => -1,
            GeneratedRarity.Common => 3,
            GeneratedRarity.Uncommon => 5,
            // Rare sits between Uncommon (+5) and Ancient (+10). Its additional whole-card calibration then
            // places the actual center almost exactly halfway between those two rarity centers.
            GeneratedRarity.Rare => 8,
            GeneratedRarity.Ancient => 10,
            _ => 0
        };
        return cost switch { 1 => 7, 2 => 12, 3 => 19, 4 => 25, _ => 30 } + rarityBonus;
    }

    private static int AllDamageBudget(GeneratedRarity rarity, int cost, GeneratedCharacter? character,
        bool multiHit = false) => Math.Max(1, (int)Math.Round(
        TargetDamageBudget(rarity, cost, character) / (multiHit ? 1.75d : 1.55d),
        MidpointRounding.AwayFromZero));

    // Osty damage is an ordinary damage operation performed by the companion. A flat one-point discount is too
    // visible on low-cost cards, so low values share the exact curve and only double-digit values lose one point.
    private static int OstyDamageBudget(GeneratedRarity rarity, int cost)
    {
        var ordinaryDamage = TargetDamageBudget(rarity, cost, GeneratedCharacter.Necrobinder);
        return ordinaryDamage >= 10 ? ordinaryDamage - 1 : ordinaryDamage;
    }

    private static int OstyAllDamageBudget(GeneratedRarity rarity, int cost)
    {
        var ordinaryDamage = AllDamageBudget(rarity, cost, GeneratedCharacter.Necrobinder);
        return ordinaryDamage >= 10 ? ordinaryDamage - 1 : ordinaryDamage;
    }

    private static int DoomBudget(GeneratedRarity rarity, int cost)
    {
        var rarityBonus = rarity switch
        {
            GeneratedRarity.Basic => -1,
            GeneratedRarity.Common => 0,
            GeneratedRarity.Uncommon => 2,
            GeneratedRarity.Rare => 7,
            GeneratedRarity.Ancient => 8,
            _ => 0
        };
        // Native reference points: Scourge/No Escape are 10-13 Doom at 1 Energy, Deathbringer is 21 at 2,
        // and End of Days is 29 at 3. Doom is delayed damage, but its printed amount is deliberately well above
        // an ordinary damage operation at the same rarity and cost.
        var budget = cost switch
        {
            -1 => 9,
            0 => 10,
            1 => 13,
            2 => 22,
            3 => 30,
            4 => 38,
            _ => 43
        };
        return Math.Max(1, budget + rarityBonus);
    }

    private static int DoomBudget(ComponentAtom atom, GeneratedRarity rarity, int cost) =>
        Math.Max(1, (int)Math.Round(DoomBudget(rarity, cost)
            / EffectBalanceModel.DamageValueMultiplier(atom), MidpointRounding.AwayFromZero));

    private static int PoisonBudget(GeneratedRarity rarity, int cost, bool allEnemies)
    {
        // Native anchors: Deadly Poison is 1c Common/5, Snakebite is 2c Common/7 with Retain, Haze is 2c
        // Uncommon/4 to all plus Weak, and Outbreak is 3c Rare/9 to all plus an immediate Poison trigger.
        var rarityBonus = rarity switch
        {
            GeneratedRarity.Basic => -1,
            GeneratedRarity.Common => 0,
            GeneratedRarity.Uncommon => 1,
            GeneratedRarity.Rare => 2,
            GeneratedRarity.Ancient => 3,
            _ => 0
        };
        var targeted = cost switch { <= 0 => 3, 1 => 5, 2 => 7, 3 => 9, _ => 11 } + rarityBonus;
        return Math.Max(1, allEnemies ? targeted - 3 : targeted);
    }

    private static int ThornsBudget(GeneratedRarity rarity, int cost)
    {
        var rarityBonus = rarity switch
        {
            GeneratedRarity.Basic => -1,
            GeneratedRarity.Common => 0,
            GeneratedRarity.Uncommon => 0,
            GeneratedRarity.Rare => 0,
            GeneratedRarity.Ancient => 1,
            _ => 0
        };
        // The continuous effective-cost pass increases 2+ cost rewards superlinearly. These pre-correction anchors
        // therefore step earlier so the final distribution stays around Caltrops 3 and Abrasive 4 without a cap.
        var budget = cost switch { <= 0 => 1, 1 => 2, 2 => 2, 3 => 3, _ => 3 } + rarityBonus;
        return Math.Max(1, budget);
    }

    private static int VulnerableBudget(GeneratedRarity rarity, int cost) => rarity switch
    {
        GeneratedRarity.Basic => cost >= 2 ? 2 : 1,
        GeneratedRarity.Common => cost switch { <= 1 => 1, _ => 2 },
        GeneratedRarity.Uncommon => cost switch { <= 1 => 2, _ => 3 },
        GeneratedRarity.Rare => cost switch { <= 1 => 2, _ => 3 },
        GeneratedRarity.Ancient => 3,
        _ => 1
    };

    private static int RandomDamageBudget(GeneratedRarity rarity, int cost,
        GeneratedCharacter? character, bool multiHit = false) => Math.Max(1, (int)Math.Ceiling(
        TargetDamageBudget(rarity, cost, character) / (multiHit ? 0.95d : 0.90d)));

    private static int DrawBudget(GeneratedRarity rarity, int cost) => rarity switch
    {
        GeneratedRarity.Basic => 1,
        GeneratedRarity.Common => cost >= 2 ? 2 : 1,
        GeneratedRarity.Uncommon => cost <= 0 ? 1 : 2,
        GeneratedRarity.Rare => cost <= 1 ? 2 : 3,
        _ => cost <= 0 ? 2 : 3
    };

    private static int EnergyBudget(GeneratedRarity rarity, int cost) => rarity switch
    {
        GeneratedRarity.Basic => cost >= 3 ? 2 : 1,
        GeneratedRarity.Common => cost >= 2 ? 2 : 1,
        GeneratedRarity.Uncommon => cost switch { <= 0 => 1, <= 2 => 2, _ => 3 },
        GeneratedRarity.Rare => cost <= 1 ? 2 : 3,
        _ => 3
    };

    private static int StrengthBudget(GeneratedRarity rarity, int cost) => rarity switch
    {
        GeneratedRarity.Basic => 1,
        GeneratedRarity.Common => cost <= 1 ? 1 : 2,
        GeneratedRarity.Uncommon => cost <= 1 ? 1 : 2,
        GeneratedRarity.Rare => cost <= 0 ? 1 : 2,
        _ => 2
    };

    private static int PowerStrengthOrDexterityBudget(GeneratedRarity rarity, int cost) => rarity switch
    {
        // Inflame and Footwork establish the one-Energy Uncommon baseline at two. Lower-rarity smoothing keeps
        // one, while Rare/Ancient do not automatically exceed the native amount merely for changing border color.
        GeneratedRarity.Basic or GeneratedRarity.Common => 1,
        _ => cost <= 0 ? 1 : 2
    };

    private static int PermanentFocusBudget(GeneratedRarity rarity, int cost) => rarity switch
    {
        GeneratedRarity.Basic or GeneratedRarity.Common => 1,
        GeneratedRarity.Uncommon => cost >= 3 ? 2 : 1,
        _ => cost >= 2 ? 2 : 1
    };

    private static int RarityRank(GeneratedRarity rarity) => rarity switch
    {
        GeneratedRarity.Basic => 0,
        GeneratedRarity.Common => 1,
        GeneratedRarity.Uncommon => 2,
        GeneratedRarity.Rare => 3,
        GeneratedRarity.Ancient => 4,
        _ => 0
    };

    internal static void ValidateNumericBudgetMonotonicity()
    {
        var rarities = new[]
        {
            GeneratedRarity.Basic,
            GeneratedRarity.Common,
            GeneratedRarity.Uncommon,
            GeneratedRarity.Rare,
            GeneratedRarity.Ancient
        };
        var budgets = new (string Name, Func<GeneratedRarity, int, int> Value)[]
        {
            ("TargetDamage", (rarity, cost) => TargetDamageBudget(rarity, cost, GeneratedCharacter.Ironclad)),
            ("AllDamage", (rarity, cost) => AllDamageBudget(rarity, cost, GeneratedCharacter.Ironclad)),
            ("OstyDamage", OstyDamageBudget),
            ("OstyAllDamage", OstyAllDamageBudget),
            ("Doom", DoomBudget),
            ("RandomDamage", (rarity, cost) => RandomDamageBudget(rarity, cost,
                GeneratedCharacter.Ironclad)),
            ("Block", (rarity, cost) => BlockBudget(rarity, cost, GeneratedCharacter.Ironclad)),
            ("Draw", DrawBudget),
            ("Energy", EnergyBudget),
            ("Strength", StrengthBudget),
            ("Vulnerable", VulnerableBudget),
            ("DamageScale", (rarity, cost) => RarityRank(rarity) + cost >= 3 ? 3 : 2)
        };
        foreach (var (name, value) in budgets)
        {
            foreach (var rarity in rarities)
            {
                for (var cost = 1; cost <= 4; cost++)
                    if (value(rarity, cost) < value(rarity, cost - 1))
                        throw new InvalidOperationException($"{name} numeric budget decreases from cost {cost - 1} to {cost} at {rarity}.");
            }
            for (var cost = 0; cost <= 4; cost++)
            {
                for (var rarityIndex = 1; rarityIndex < rarities.Length; rarityIndex++)
                    if (value(rarities[rarityIndex], cost) < value(rarities[rarityIndex - 1], cost))
                        throw new InvalidOperationException($"{name} numeric budget decreases with rarity at cost {cost}.");
            }
        }
        foreach (var rarity in rarities)
        {
            for (var cost = 0; cost <= 4; cost++)
            {
                var target = TargetDamageBudget(rarity, cost, GeneratedCharacter.Necrobinder);
                var all = AllDamageBudget(rarity, cost, GeneratedCharacter.Necrobinder);
                var random = RandomDamageBudget(rarity, cost, GeneratedCharacter.Necrobinder);
                if (!(random > target && target > all))
                    throw new InvalidOperationException(
                        $"Printed damage hierarchy must be random > targeted > area at {rarity}/{cost}: "
                        + $"{random}/{target}/{all}.");
                if (OstyDamageBudget(rarity, cost) < target - 1 || OstyDamageBudget(rarity, cost) > target)
                    throw new InvalidOperationException($"Osty damage diverges from ordinary target damage at {rarity}/{cost}.");
                if (OstyAllDamageBudget(rarity, cost) < all - 1 || OstyAllDamageBudget(rarity, cost) > all)
                    throw new InvalidOperationException($"Osty area damage diverges from ordinary area damage at {rarity}/{cost}.");
                if (DoomBudget(rarity, cost) < Math.Max(target, all) + 3)
                    throw new InvalidOperationException($"Doom budget is not meaningfully above ordinary damage at {rarity}/{cost}.");
            }
        }

        var doomAtoms = CharacterComponentCatalogs.Get(GeneratedCharacter.Necrobinder).Atoms
            .Where(atom => atom.Template is "NCR:ApplyDoom" or "NCR:ApplyDoomAll")
            .ToArray();
        var selectedDoom = doomAtoms.First(atom =>
            !OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("random_enemy_reference")
            && !OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("all_enemies_reference"));
        var randomDoom = doomAtoms.First(atom =>
            OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("random_enemy_reference"));
        var allDoom = doomAtoms.First(atom =>
            OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("all_enemies_reference"));
        foreach (var rarity in rarities)
        for (var cost = 0; cost <= 4; cost++)
        {
            var selected = DoomBudget(selectedDoom, rarity, cost);
            var random = DoomBudget(randomDoom, rarity, cost);
            var allEnemies = DoomBudget(allDoom, rarity, cost);
            if (!(random > selected && selected > allEnemies))
                throw new InvalidOperationException(
                    $"Printed Doom hierarchy must be random > targeted > area at {rarity}/{cost}: "
                    + $"{random}/{selected}/{allEnemies}.");
        }
    }

    private static int EffectiveDamageValue(ComponentAtom atom, int damage)
    {
        var hits = OperationRuntimeSpecCompiler.GetOrCompile(atom).Values
            .FirstOrDefault(value => value.Id == "hits" && value.Source == "fixed")?.BaseValue ?? 1;
        return damage * Math.Max(1, hits);
    }

    private T Pick<T>(params (T Value, int Weight)[] choices)
    {
        var roll = _random.Next(choices.Sum(choice => choice.Weight));
        foreach (var (value, weight) in choices)
        {
            if (roll < weight) return value;
            roll -= weight;
        }
        throw new InvalidOperationException();
    }

    private T PickWeighted<T>(IReadOnlyList<T> values, Func<T, long> weightSelector)
    {
        var weights = values.Select(weightSelector).ToArray();
        var total = weights.Sum();
        if (total <= 0) throw new InvalidOperationException("随机权重总和必须为正数。");
        var roll = _random.NextInt64(total);
        for (var index = 0; index < values.Count; index++)
        {
            if (roll < weights[index]) return values[index];
            roll -= weights[index];
        }
        throw new InvalidOperationException();
    }
}

public static class CardEffectRules
{
    private static readonly IReadOnlyDictionary<string, int> EmptyParameters =
        new Dictionary<string, int>();
    private static OperationRuntimeSpec RuntimeSpec(ComponentAtom atom) =>
        OperationRuntimeSpecCompiler.GetOrCompile(atom);

    public static bool IsEnemyDamageAmplificationRule(ComponentAtom atom) =>
        atom.Scope == OperationScope.AbilityRule
        && RuntimeSpec(atom).Variant is "weak_enemy_attack_damage_bonus" or "vulnerable_enemy_damage_bonus";

    public static bool IsEnemyDamageAmplificationRule(GeneratorOperation operation) =>
        operation.Scope == OperationScope.AbilityRule
        && OperationRuntimeSpecCompiler.GetOrCompile(operation).Variant is
            "weak_enemy_attack_damage_bonus" or "vulnerable_enemy_damage_bonus";

    private static readonly HashSet<string> DynamicTotalHitModifiers = new(StringComparer.Ordinal)
    {
        "M:RepeatPerAttackThisTurn", "M:RepeatPerSkillInHand", "D:RepeatPerOrb",
        "NCR:RepeatPerVoidPlayedCombat",
        "R:RepeatPerSkillPlayedThisTurn", "R:RepeatPerStarGainedThisTurn"
    };

    public static bool IsDynamicTotalHitModifier(ComponentAtom atom) =>
        DynamicTotalHitModifiers.Contains(atom.Template)
        || atom.Template == "M:repeat"
            && RuntimeSpec(atom).Variant == "hp_loss_scaled";

    public static bool IsDynamicTotalHitModifier(GeneratorOperation operation) =>
        DynamicTotalHitModifiers.Contains(operation.Template)
        || operation.Template == "M:repeat"
            && OperationRuntimeSpecCompiler.GetOrCompile(operation).Variant == "hp_loss_scaled";

    public static bool IsIntrinsicMultiHitDamage(ComponentAtom atom) =>
        IsEnemyDamage(atom) && IsIntrinsicMultiHitDamage(RuntimeSpec(atom));

    public static bool IsIntrinsicMultiHitDamage(GeneratorOperation operation) =>
        IsEnemyDamage(operation) && IsIntrinsicMultiHitDamage(OperationRuntimeSpecCompiler.GetOrCompile(operation));

    private static bool IsIntrinsicMultiHitDamage(OperationRuntimeSpec spec) =>
        spec.Values.FirstOrDefault(value => value.Id == "hits") is { } hits
        && (hits.Source != "fixed" || hits.BaseValue + hits.Offset > 1);

    public static bool HasValidRepeatDamageAssembly(IReadOnlyList<GeneratorOperation> operations)
    {
        var damage = operations.Where(IsEnemyDamage).ToArray();
        if (operations.Any(operation => operation.Template == "M:RepeatAreaOnKill")
            && (damage.Length == 0 || damage.Any(operation => operation.Template != "N:AllD")))
            return false;
        var repeatModifiers = operations.Where(operation => operation.Scope == OperationScope.Modifier)
            .Where(operation => IsDynamicTotalHitModifier(operation)
                || operation.Template is "D:RepeatDamage" or "R:RepeatDamage"
                || OperationRuntimeSpecCompiler.GetOrCompile(operation).Opcode == "modify_hits")
            .ToArray();
        if (repeatModifiers.Length == 0) return true;
        // Static/dynamic hit modifiers are implemented at card level and therefore amplify every damage line.
        // Pairing one with two independent damage operations silently multiplies far more value than the text and
        // budget model imply (for example 8 + 10 damage, both repeated twice). Give it exactly one host effect.
        if (damage.Length != 1) return false;
        return !repeatModifiers.Any(IsDynamicTotalHitModifier)
            || damage.All(operation => !IsIntrinsicMultiHitDamage(operation));
    }

    /// <summary>
    /// Operations that move or replay the generated card itself. A Power leaves the ordinary card piles when
    /// played, so these effects are either meaningless or can conflict with Power-zone handling. Creating a copy
    /// is deliberately not part of this set and remains legal on Powers.
    /// </summary>
    public static bool IsSelfCardMovementOrReplay(ComponentAtom atom) =>
        RuntimeSpec(atom).Flags.Contains(ComponentSemanticFlags.SelfCardMovement)
        || IsSelfCardMovementOrReplay(atom.Template);

    public static bool IsSelfCardMovementOrReplay(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags
            .Contains(ComponentSemanticFlags.SelfCardMovement)
        || IsSelfCardMovementOrReplay(operation.Template);

    private static bool IsSelfCardMovementOrReplay(string template) => template is
        "CL:ReturnThisToHand"
        or "I:PlayThisCard"
        or "R:PlayThisCard"
        or "R:ReturnThisToHand"
        or "R:PutThisOnDraw"
        or "R:ReturnAfterSkillsPlayed"
        or "NCR:ReturnFromDiscardOnHighCostPlay"
        or "R:AtTurnEndWhenTopOfDraw"
        or "R:PlayAtTurnEndIfTopOfDraw";

    public static bool TriggerSupportsChoiceContext(GeneratorOperation trigger) =>
        TriggerSupportsChoiceContextBySpec(trigger);

    internal static bool TriggerSupportsChoiceContextBySpec(GeneratorOperation trigger)
    {
        if (trigger.Template == "A:turnStart") return true;
        if (trigger.Template is "NCR:NextTurn" or "R:NextTurn" or "D:NextTurnsStart"
            or "A:whenEnergyCostAtLeast" or "A:whenEnergySpent" or "A:whenOneStarSpent"
            or "A:whenOstyLosesHp" or "D:ForEachEnergySpentThisTurn") return false;
        var kind = OperationRuntimeSpecCompiler.GetOrCompile(trigger).Trigger?.Kind;
        return kind is not ("block_gained" or "owner_hp_lost_during_turn" or "card_generated"
            or "stars_spent_or_gained" or "status_generated");
    }

    public static bool OperationNeedsChoiceContext(GeneratorOperation operation) =>
        OperationNeedsChoiceContextBySpec(operation);

    internal static bool OperationNeedsChoiceContextBySpec(GeneratorOperation operation)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        if (IsAtomicChoiceProxy(operation.Template) || operation.Template == "I:ProxyAtomic_ForegoneConclusion"
            || operation.Template is "N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK" or "I:Upgrade"
            or "CL:TransformSelectedHandCards" or "CL:ExhaustUpToHandCards"
            or "CL:MoveSelectedSkillDrawToHand" or "CL:MoveSelectedAttackDrawToHand"
            or "CL:ChooseFromRandomDrawCards" or "CL:ChooseDrawCardToHand"
            or "R:MoveDiscardCardToDrawTop" or "R:PlaySelectedSkillMultipleTimes"
            or "R:PutSelectedHandCardsOnDraw" or "R:PutSelectedHandCardOnDraw"
            or "R:CopySelectedColorlessCard" or "NCR:MoveDiscardCardToHand" or "D:MoveDiscardCardToHand"
            or "I:GrantSlyToHandSkillThisTurn" or "I:CopySelectedCardNextTurn"
            or "I:PlayTopCardAndExhaust" or "I:PlayTopXCards" or "CL:PlayTopDrawCard"
            or "D:AutoPlayRandomAttackFromDraw" or "I:AutoPlayRandomAttackFromHand"
            or "I:PlayAtRandomEnemy")
            return true;
        return spec.Target == "selected_card"
            || spec.Opcode is "choose_generated_card" or "select_card";
    }

    /// <summary>
    /// Operations that upgrade an existing card in a live combat pile. This deliberately excludes upgrading a
    /// detached preview/proxy or creating an already-upgraded derivative, neither of which can open a pile choice.
    /// Keep this list centralized so new combat-upgrade components cannot silently bypass routing audits.
    /// </summary>
    public static bool IsExistingCombatCardUpgrade(GeneratorOperation operation) => operation.Template is
        "I:Upgrade" or "I:UpgradeThatCard" or "NCR:UpgradeRandomDiscardCards";

    /// <summary>
    /// Atomic proxies whose original OnPlay contract opens a card-selection transaction. Their printed text does
    /// do not always contain the localized word for "choose", so treating them as ordinary independent effects allows them to be linked
    /// to hooks that deliberately provide ThrowingPlayerChoiceContext (for example Osty losing HP).
    /// </summary>
    public static bool IsAtomicChoiceProxy(string template) => template is
        "I:ProxyAtomic_Begone"
        or "I:ProxyAtomic_Charge"
        or "I:ProxyAtomic_Guards"
        or "I:ProxyAtomic_Seance"
        or "I:ProxyAtomic_Dredge"
        or "I:ProxyAtomic_Transfigure";

    public static bool HasAttackClassifyingDamage(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            if (!IsEnemyDamage(operations[index])) continue;
            // A damage line gated by an explicit "if" clause is not a reliable Attack payload and therefore
            // classifies a non-Power card as a Skill. Count modifiers such as Fiend Fire's "for each exhausted
            // card" still resolve as the card's immediate attack and must retain Attack classification.
            if (!operations[index].Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                || triggerIndex < 0 || triggerIndex >= operations.Count)
                return true;
            if (!IsDamageTypeSuppressingCondition(operations[triggerIndex]))
                return true;
        }
        return false;
    }

    private static bool IsDamageTypeSuppressingCondition(GeneratorOperation trigger) =>
        IsDamageTypeSuppressingConditionBySpec(trigger);

    internal static bool IsDamageTypeSuppressingConditionBySpec(GeneratorOperation trigger) =>
        IsDelayedEffect(trigger)
        || trigger.Template != "C:playableIfDrawPileEmpty"
            && OperationRuntimeSpecCompiler.GetOrCompile(trigger).Condition is not null
        || OperationRuntimeSpecCompiler.GetOrCompile(trigger).Variant == "die_on_unblocked_attack";

    public static bool IsEnergyGainOperation(GeneratorOperation operation) =>
        operation.Template is "N:E" or "N:NextTurnEnergy"
            or "D:GainEnergy" or "D:NextTurnEnergy"
            or "NCR:GainEnergy" or "NCR:NextTurnEnergy"
            or "R:GainEnergy";

    public static bool IsEnergyGainOperation(ComponentAtom atom) =>
        atom.Template is "N:E" or "N:NextTurnEnergy"
            or "D:GainEnergy" or "D:NextTurnEnergy"
            or "NCR:GainEnergy" or "NCR:NextTurnEnergy"
            or "R:GainEnergy";

    public static bool IsStarGainOperation(GeneratorOperation operation) =>
        operation.Template == "R:GainStars";

    public static bool IsStarGainOperation(ComponentAtom atom) =>
        atom.Template == "R:GainStars";

    public static bool IsDiscardEffect(ComponentAtom atom) => atom.Template is
        "N:Discard" or "N:DiscardAll" or "I:DiscardHandDrawSame" or "D:DrawAndDiscardNonZero"
            or "R:DiscardTopOfDraw";

    public static bool IsDiscardEffect(GeneratorOperation operation) => operation.Template is
        "N:Discard" or "N:DiscardAll" or "I:DiscardHandDrawSame" or "D:DrawAndDiscardNonZero"
            or "R:DiscardTopOfDraw";

    public static bool GrantsSlyToAnotherCard(ComponentAtom atom) => atom.Template is
        "I:GrantSlyToHandSkillThisTurn" or "A:rulePlayedSkillsGainSly";

    public static bool IsCardCreationOrTransformation(ComponentAtom atom) =>
        DerivativeSlotCatalog.IsProducer(atom.Template)
        || IsRandomCardGeneration(atom)
        || atom.Template is "N:Create" or "D:CreateZeroCostCopyInDiscard" or "NCR:CreateCopyInDiscard"
            or "CL:TransformSelectedHandCards" or "CL:ProxyAtomic_Discovery" or "CL:ProxyAtomic_Splash"
            or "I:ProxyAtomic_Quasar" or "I:ProxyAtomic_WhiteNoise" or "I:CopySelectedCardNextTurn"
            or "R:CopySelectedColorlessCard";

    public static bool IsCardCreationOrTransformation(GeneratorOperation operation) =>
        DerivativeSlotCatalog.IsProducer(operation.Template)
        || IsRandomCardGeneration(operation)
        || operation.Template is "N:Create" or "D:CreateZeroCostCopyInDiscard" or "NCR:CreateCopyInDiscard"
            or "CL:TransformSelectedHandCards" or "CL:ProxyAtomic_Discovery" or "CL:ProxyAtomic_Splash"
            or "I:ProxyAtomic_Quasar" or "I:ProxyAtomic_WhiteNoise" or "I:CopySelectedCardNextTurn"
            or "R:CopySelectedColorlessCard";

    /// <summary>Permanent payments may resolve once per turn, but never once per card/hit/draw feedback loop.</summary>
    public static bool IsPermanentNegativeEffect(ComponentAtom atom) => atom.Template is
        "N:LoseDex" or "D:LoseFocus" or "D:LoseOrbSlots" or "NCR:LoseStrength"
        || atom.Template == "T:Apply"
            && RuntimeSpec(atom).Variant == "strength_gain";

    public static bool IsPermanentNegativeEffect(GeneratorOperation operation) => operation.Template is
        "N:LoseDex" or "D:LoseFocus" or "D:LoseOrbSlots" or "NCR:LoseStrength"
        || operation.Template == "T:Apply"
            && OperationRuntimeSpecCompiler.GetOrCompile(operation).Variant == "strength_gain";

    public static bool HasValidReturnThisToHandCost(int cost, int starCost, bool hasStarCostX,
        IReadOnlyList<GeneratorOperation> operations)
    {
        if (!operations.Any(operation => operation.Template == "R:ReturnThisToHand")) return true;
        // X may legally resolve as zero. Fixed printed payment is no longer sufficient either: an unconditional
        // refund can cancel it and create a free self-return loop. Use the same effective-cost model as numeric
        // budgets and reject every fixed card whose reliable net payment is zero or negative.
        var effectiveCost = ResourceEconomyModel.BudgetEffectiveCost(cost, starCost, cost < 0, hasStarCostX,
            operations);
        return !double.IsNaN(effectiveCost) && effectiveCost > 0d;
    }

    public static XResourceRequirement XRequirement(ComponentAtom atom)
    {
        if (atom.Template.Contains("StarX", StringComparison.Ordinal)) return XResourceRequirement.Star;
        if (atom.Template.Contains("EnergyX", StringComparison.Ordinal)) return XResourceRequirement.Energy;
        if (atom.Template.Contains("EitherX", StringComparison.Ordinal)) return XResourceRequirement.Either;
        return UsesX(atom) ? XResourceRequirement.Energy : XResourceRequirement.None;
    }

    public static XResourceRequirement XRequirement(GeneratorOperation operation)
    {
        if (SpecialXCardConverter.IsSpecial(operation))
            return SpecialXCardConverter.Resource(operation) == SpecialXCardConverter.StarResource
                ? XResourceRequirement.Star
                : XResourceRequirement.Energy;
        if (operation.Template.Contains("StarX", StringComparison.Ordinal)) return XResourceRequirement.Star;
        if (operation.Template.Contains("EnergyX", StringComparison.Ordinal)) return XResourceRequirement.Energy;
        if (operation.Template.Contains("EitherX", StringComparison.Ordinal)) return XResourceRequirement.Either;
        return UsesX(operation) ? XResourceRequirement.Energy : XResourceRequirement.None;
    }

    public static bool IsHealingOrMaxHp(GeneratorOperation operation) =>
        operation.Template is "N:Heal" or "N_HEAL" or "I:GainMaxHp";

    public static bool IsGoldGainOperation(ComponentAtom atom) =>
        atom.Template is "CL:GainGold" or "A:ProxyAtomic_Royalties";

    public static bool IsGoldGainOperation(GeneratorOperation operation) =>
        operation.Template is "CL:GainGold" or "A:ProxyAtomic_Royalties";

    /// <summary>
    /// Restricted effects require a Power or force Exhaust on a non-Power. Besides healing and maximum HP, this class
    /// includes permanent potion/gold rewards and run-persistent increases to this card's base damage or Block.
    /// </summary>
    public static bool IsRestrictedEffect(ComponentAtom atom) =>
        RuntimeSpec(atom).Flags.Contains(ComponentSemanticFlags.Restricted)
        || atom.Template is "N:Heal" or "N_HEAL" or "I:GainMaxHp" or "CL:ProxyAtomic_Alchemize" or "CL:GainGold"
            or "A:ProxyAtomic_Royalties" or "I:AddCardReward"
            or "D:IncreaseThisCardBlockRun" or "NCR:IncreaseThisCardDamageRun";

    public static bool IsRestrictedEffect(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains(ComponentSemanticFlags.Restricted)
        || IsHealingOrMaxHp(operation)
        || operation.Template is "CL:ProxyAtomic_Alchemize" or "CL:GainGold"
            or "A:ProxyAtomic_Royalties" or "I:AddCardReward"
            or "D:IncreaseThisCardBlockRun" or "NCR:IncreaseThisCardDamageRun";

    public static bool IsRepeatedTriggerOrCondition(GeneratorOperation operation) =>
        (operation.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger
            || IsDependencyPrefix(operation))
        && EffectBalanceModel.HasRepeatedOrMultiplicativePayoff(operation);

    /// <summary>
    /// Run-persistent rewards may sit behind a one-shot gate such as If Fatal, but never behind a trigger that can
    /// resolve repeatedly. Exhaust/Power restrictions are checked separately because they govern the card itself.
    /// </summary>
    public static bool HasNoRepeatedTriggeredRestrictedEffects(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (!IsRestrictedEffect(operation)) continue;
            if (operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                && triggerIndex >= 0 && triggerIndex < index
                && IsRepeatedTriggerOrCondition(operations[triggerIndex]))
                return false;
            if (index > 0 && IsDependencyPrefix(operations[index - 1])
                && IsRepeatedTriggerOrCondition(operations[index - 1]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Effects resolved by card lifecycle hooks must not borrow an unrelated trigger owner. ReturnNextTurn is a
    /// native two-part lifecycle pair and remains legal; dependency-only state payoffs are not classified here.
    /// </summary>
    public static bool HasNoInvalidTriggeredStateEffects(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (!IsSelfManagedStateEffect(operation)
                || !operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex))
                continue;
            if (triggerIndex < 0 || triggerIndex >= index) return false;
            var trigger = operations[triggerIndex];
            if (operation.Template == "CL:ReturnThisToHand" && trigger.Template == "CL:AtNextTurnStart")
                continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Pile-state conditions need an executable linked payoff. A Modifier has no independently executable body and
    /// must never borrow one of these lifecycle conditions, including in migrated/imported operation lists.
    /// </summary>
    public static bool HasNoStateConditionModifiers(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Scope != OperationScope.Modifier
                || !operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex))
                continue;
            if (triggerIndex < 0 || triggerIndex >= index
                || IsSelfZoneStateCondition(operations[triggerIndex]))
                return false;
        }
        return true;
    }

    public static bool IsSelfZoneStateCondition(GeneratorOperation operation)
    {
        if (operation.Template == "R:AtTurnEndWhenTopOfDraw") return true;
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        var kind = spec.Trigger?.Kind ?? spec.Condition?.Kind;
        return kind is "turn_start_if_self_in_exhaust" or "turn_end_if_self_in_exhaust"
            or "turn_end_if_self_on_draw_top";
    }

    public static bool IsSelfManagedStateEffect(ComponentAtom atom) =>
        IsSelfManagedStateEffect(atom.Template);

    public static bool IsSelfManagedStateEffect(GeneratorOperation operation) =>
        IsSelfManagedStateEffect(operation.Template);

    private static bool IsSelfManagedStateEffect(string template) => template is
        "CL:ReturnThisToHand"
        or "R:ReturnThisToHand"
        or "R:PutThisOnDraw"
        or "R:ReturnAfterSkillsPlayed"
        or "C:whileInCombat"
        or "C:whileInCombatSkillCostReduction";

    /// <summary>
    /// EndTurn may be a direct payment or a payoff owned by an ordinary event/condition. Turn-boundary triggers
    /// are deliberately excluded; all other trigger frequencies are priced by NegativeEffectTuning.
    /// </summary>
    public static bool HasValidTriggeredEndTurnAssembly(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Template != "R:EndTurn"
                || !operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex))
                continue;
            if (triggerIndex < 0 || triggerIndex >= index || IsTurnBoundaryTrigger(operations[triggerIndex]))
                return false;
        }
        return true;
    }

    public static bool IsTurnBoundaryTrigger(GeneratorOperation operation)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        var kind = spec.Trigger?.Kind ?? spec.Condition?.Kind;
        return kind is "turn_start" or "turn_end" or "next_turn_start" or "next_turns_start"
            or "turn_start_if_self_in_exhaust" or "turn_end_if_self_in_exhaust" or "turns_elapsed";
    }

    /// <summary>
    /// Increasing this card's Damage for the combat is itself a compounding modifier. Repeating it behind an
    /// Attack/card/draw trigger both stacks the modifier and raises every later Damage payoff on the same card,
    /// which cannot be represented by an ordinary linear trigger multiplier. Keep the native Rampage-style
    /// one-shot form and one-shot conditional forms, but reject repeatable owners.
    /// </summary>
    public static bool HasNoRepeatedTriggeredCombatDamageGrowth(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (!IsCombatBaseDamageIncrease(operation)
                || !operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                || triggerIndex < 0 || triggerIndex >= index)
                continue;
            if (IsRepeatedTriggerOrCondition(operations[triggerIndex])) return false;
        }
        return true;
    }

    /// <summary>
    /// Shadow Step's next-turn global Attack multiplier is a one-shot card payoff. Placing it behind a repeatable
    /// trigger can enqueue several independent global multipliers from one card and is not a meaningful assembly.
    /// One-shot conditions remain legal.
    /// </summary>
    public static bool HasNoRepeatedTriggeredNextTurnAttackDouble(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Template != "I:DoubleAttackDamageNextTurn"
                || !operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                || triggerIndex < 0 || triggerIndex >= index)
                continue;
            if (IsRepeatedTriggerOrCondition(operations[triggerIndex])) return false;
        }
        return true;
    }

    public static bool IsConditionalDamageVariant(ComponentAtom atom) =>
        RuntimeSpec(atom).Flags.Contains("conditional_damage_payoff");

    public static bool IsConditionalDamageVariant(GeneratorOperation operation) =>
        IsConditionalDamageVariantBySpec(operation);

    internal static bool IsConditionalDamageVariantBySpec(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("conditional_damage_payoff");

    public static bool IsHitEnemyDamageVariant(ComponentAtom atom) =>
        RuntimeSpec(atom).Flags.Contains("hit_enemy_reference");

    public static bool IsHitEnemyDamageVariant(GeneratorOperation operation) =>
        IsHitEnemyDamageVariantBySpec(operation);

    internal static bool IsHitEnemyDamageVariantBySpec(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("hit_enemy_reference");

    public static bool HasLightningEvokeTriggerContext(IReadOnlyList<GeneratorOperation> previous)
    {
        if (previous.LastOrDefault() is not { } prior) return false;
        if (prior.Template == "A:whenLightningEvoked") return true;
        return prior.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
            && triggerIndex >= 0 && triggerIndex < previous.Count
            && previous[triggerIndex].Template == "A:whenLightningEvoked";
    }

    public static bool HasValidHitEnemyDamageAssembly(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (!IsHitEnemyDamageVariant(operation)) continue;
            if (!operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                || triggerIndex < 0 || triggerIndex >= index
                || operations[triggerIndex].Template != "A:whenLightningEvoked")
                return false;
        }
        return true;
    }

    /// <summary>
    /// Event-amount effects do not own a printed value. They consume the amount supplied by one specific trigger
    /// (currently Osty losing HP), so assembling them as an immediate line produces a card that can never resolve.
    /// </summary>
    public static bool HasValidEventAmountAssembly(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Template != "NCR:AllEnemiesLoseEventHp") continue;
            if (!operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                || triggerIndex < 0 || triggerIndex >= index
                || operations[triggerIndex].Template != "A:whenOstyLosesHp")
                return false;
        }
        return true;
    }

    /// <summary>
    /// Operations that consume the card/amount carried by an event are not ordinary standalone effects. Their
    /// trigger ownership is an execution contract, so imported snapshots and future catalog edits are rejected if
    /// they connect the payload to a merely similar-looking trigger.
    /// </summary>
    public static bool HasValidTriggerPayloadAssembly(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var effect = operations[index];
            if (!RequiresSpecificTriggerPayload(effect)) continue;
            if (!effect.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                || triggerIndex < 0 || triggerIndex >= index)
                return false;
            if (!CanSupplySpecificTriggerPayload(operations[triggerIndex], effect)) return false;
        }
        return true;
    }

    /// <summary>
    /// True only when an effect reads transient state owned by a particular event: the event card, event amount,
    /// or a referenced Attack/Skill. Ordinary numeric effects deliberately return false even if an original card
    /// happened to print them after a trigger.
    /// </summary>
    public static bool RequiresSpecificTriggerPayload(ComponentAtom effect) =>
        RequiresSpecificTriggerPayload(RuntimeSpec(effect));

    public static bool RequiresSpecificTriggerPayload(GeneratorOperation effect) =>
        RequiresSpecificTriggerPayload(OperationRuntimeSpecCompiler.GetOrCompile(effect));

    private static bool RequiresSpecificTriggerPayload(OperationRuntimeSpec spec) =>
        spec.Flags.Contains("requires_event_card_payload")
        || spec.Flags.Contains("requires_event_amount_payload")
        || spec.Flags.Contains("referenced_non_attack_exhaust")
        || spec is { Opcode: "exhaust_card", Variant: "referenced" }
        || spec is { Opcode: "create_copy", Variant: "referenced_attack" };

    public static bool CanSupplySpecificTriggerPayload(GeneratorOperation trigger, ComponentAtom effect) =>
        CanSupplySpecificTriggerPayload(trigger, effect.Template, RuntimeSpec(effect));

    public static bool CanSupplySpecificTriggerPayload(GeneratorOperation trigger, GeneratorOperation effect) =>
        CanSupplySpecificTriggerPayload(trigger, effect.Template,
            OperationRuntimeSpecCompiler.GetOrCompile(effect));

    private static bool CanSupplySpecificTriggerPayload(GeneratorOperation trigger, string effectTemplate,
        OperationRuntimeSpec spec)
    {
        var triggerSpec = OperationRuntimeSpecCompiler.GetOrCompile(trigger);
        var kind = triggerSpec.Trigger?.Kind;
        return effectTemplate switch
        {
            "D:ReplayEventCard" => kind == "first_card_played_each_turn",
            "D:ReturnEventCardToHand" => kind == "first_zero_cost_attack_played_each_turn",
            "CL:PutEventCardOnDrawTop" => kind == "first_attack_or_skill_each_turn",
            "I:PlayAtRandomEnemy" => kind == "strike_card_drawn",
            "NCR:ApplyEventDamageAsDoom" => kind is "attack_damaged_enemy" or "attack_dealt_damage",
            "NCR:AllEnemiesLoseEventHp" => kind == "osty_hp_lost",
            _ when spec is { Opcode: "exhaust_card", Variant: "referenced" } => kind == "skill_played",
            _ when spec.Flags.Contains("referenced_non_attack_exhaust") =>
                kind == "for_each_exhausted_non_attack",
            _ when spec is { Opcode: "create_copy", Variant: "referenced_attack" } =>
                kind == "nth_attack_played_this_turn"
                && OperationRuntimeSpecCompiler.StaticLiteralValue(trigger, "threshold", 1) == 3,
            _ => false
        };
    }

    /// <summary>
    /// A negative duration is a payment just like lost Strength or increased cost: a better upgrade shortens it.
    /// The duration must be the operation's first numeric slot because upgrades intentionally address that slot.
    /// </summary>
    public static bool IsReducibleNegativeDuration(GeneratorOperation operation)
    {
        if (!IsNegativeEffect(operation)) return false;
        return OperationRuntimeSpecCompiler.GetOrCompile(operation).Values
            .FirstOrDefault(value => value.Id == "duration") is { Upgradable: true };
    }

    /// <summary>
    /// A fixed-count discard/exhaust instruction must be carried out for exactly the printed amount whenever
    /// enough cards exist. Raising that amount is therefore a worse upgrade. "Up to" effects are deliberately
    /// excluded because the player controls how many cards to consume and a larger ceiling is beneficial.
    /// </summary>
    public static bool IsMandatoryDiscardOrExhaustNumber(ComponentAtom atom) =>
        RuntimeSpec(atom) is { } spec
        && spec.Opcode is "discard_card" or "exhaust_card"
        && spec.Variant is "selected" or "random" or "top"
        && spec.Values.Count > 0 && spec.Flags.Contains("explicit_numeric")
        && !spec.Flags.Contains("up_to");

    public static bool IsMandatoryDiscardOrExhaustNumber(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation) is { } spec
        && spec.Opcode is "discard_card" or "exhaust_card"
        && spec.Variant is "selected" or "random" or "top"
        && spec.Values.Count > 0 && spec.Flags.Contains("explicit_numeric")
        && !spec.Flags.Contains("up_to");

    /// <summary>Numeric payments get smaller when upgraded; they must never enter the ordinary +value path.</summary>
    public static bool IsReducibleNegativeNumber(GeneratorOperation operation) =>
        IsReducibleNegativeDuration(operation)
        || IsMandatoryDiscardOrExhaustNumber(operation)
        || DerivativeSlotCatalog.ProducesStatus(operation)
        || operation.Template is
            "N:LoseDex"
            or "D:LoseFocus"
            or "D:LoseTemporaryFocus"
            or "D:LoseOrbSlots"
            or "D:IncreaseThisCardCost"
            or "NCR:ApplySelfDoom"
            or "NCR:LoseStrength"
            or "NCR:IncreaseAllCardCostsThisTurn"
        || operation.Template == "T:Apply"
        && OperationRuntimeSpecCompiler.GetOrCompile(operation).Variant == "strength_gain";

    public static bool IsExtremeLifecycleDownside(ComponentAtom atom) =>
        atom.Template is "CL:DieOnUnblockedAttack" or "CL:NoBlockFromCards" or "R:FillHandWithDebris";

    public static bool IsExtremeLifecycleDownside(GeneratorOperation operation) =>
        operation.Template is "CL:DieOnUnblockedAttack" or "CL:NoBlockFromCards" or "R:FillHandWithDebris";

    public static bool IsCostIncreaseDownside(ComponentAtom atom) =>
        OperationRuntimeSpecCompiler.IsCostIncrease(atom);

    public static bool IsCostIncreaseDownside(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.IsCostIncrease(operation);

    private static bool IsMixedBenefitAndDownside(string template) =>
        template == "D:DrawAndDiscardNonZero";

    public static bool IsPlayerSelectedExhaust(ComponentAtom atom) =>
        IsPlayerSelectedExhaust(RuntimeSpec(atom));

    public static bool IsPlayerSelectedExhaust(GeneratorOperation operation) =>
        IsPlayerSelectedExhaust(OperationRuntimeSpecCompiler.GetOrCompile(operation));

    private static bool IsPlayerSelectedExhaust(OperationRuntimeSpec spec) =>
        spec is { Opcode: "exhaust_card", Variant: "selected", Target: "selected_card" }
        && spec.Flags.Contains("requires_player_choice");

    public static bool HasValidPlayerSelectedExhaustCounts(IReadOnlyList<GeneratorOperation> operations) =>
        operations.All(operation =>
        {
            var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
            return spec is not { Opcode: "exhaust_card", Variant: "selected" }
                || spec.Flags.Contains("up_to")
                || OperationRuntimeSpecCompiler.StaticLiteralValue(operation, "count", 1) == 1;
        });

    /// <summary>
    /// Costs and downsides applied directly to the player. A mixed operation may be both beneficial and negative; for
    /// example, Transfigure grants Replay while increasing the selected card's cost.
    /// </summary>
    public static bool IsNegativeEffect(ComponentAtom atom) =>
        ComponentValuationApi.IsNegative(OperationRuntimeSpecCompiler.GetOrCompile(atom))
        || OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains(ComponentSemanticFlags.Negative)
        || OperationRuntimeSpecCompiler.IsIntrinsicNegative(atom)
        || DerivativeSlotCatalog.ProducesStatus(atom);

    public static bool IsNegativeEffect(GeneratorOperation operation) =>
        ComponentValuationApi.IsNegative(OperationRuntimeSpecCompiler.GetOrCompile(operation))
        || OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains(ComponentSemanticFlags.Negative)
        || OperationRuntimeSpecCompiler.IsIntrinsicNegative(operation)
        || DerivativeSlotCatalog.ProducesStatus(operation);

    public static bool HasNegativeKeyword(IEnumerable<CardTag> tags) =>
        tags.Any(tag => tag is CardTag.Exhaust or CardTag.Ethereal);

    internal static int NegativeEffectCompensationPercent(IReadOnlyList<GeneratorOperation> operations,
        IReadOnlyCollection<CardTag>? tags = null, bool? hasPrintedResourceCost = null,
        GeneratedCardType? cardType = null, GeneratedCharacter? character = null)
    {
        return NegativeEffectTuning.CompensationPercent(
            NegativeEffectTuning.TotalMultiplier(operations, tags, hasPrintedResourceCost, cardType, character));
    }

    internal static double NegativeEffectLinearCompensationValue(
        IReadOnlyList<GeneratorOperation> operations) =>
        NegativeEffectTuning.TotalLinearCompensationValue(operations);

    public static bool IsDoubleTargetVulnerable(ComponentAtom atom) =>
        atom.Template == "T:Apply"
        && RuntimeSpec(atom).Variant == "vulnerable_double";

    public static bool IsDoubleTargetVulnerable(GeneratorOperation operation) =>
        operation.Template == "T:Apply"
        && OperationRuntimeSpecCompiler.GetOrCompile(operation).Variant == "vulnerable_double";

    public static bool HasNoFatalDoubleVulnerablePayoff(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (!IsDoubleTargetVulnerable(operation)
                || !operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex))
                continue;
            if (triggerIndex >= 0 && triggerIndex < index && IsFatalCondition(operations[triggerIndex]))
                return false;
        }
        return true;
    }

    public static bool IsRandomCurrentCharacterCardToHand(ComponentAtom atom)
    {
        var spec = RuntimeSpec(atom);
        return spec.Opcode == "create_card" && spec.Variant == "current_character_random";
    }

    public static bool IsRandomCurrentCharacterCardToHand(GeneratorOperation operation) =>
        IsRandomCurrentCharacterCardToHand(new ComponentAtom(operation.Template, operation.Scope,
            operation.ChineseText, operation.RequiresSingleTarget, CardReferenceRequirement.None)
            { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(operation) });

    public static bool IsEnemyStrengthGain(ComponentAtom atom) =>
        atom.Template == "T:Apply"
        && RuntimeSpec(atom).Variant == "strength_gain";

    public static bool IsEnemyStrengthGain(GeneratorOperation operation) =>
        operation.Template == "T:Apply"
        && OperationRuntimeSpecCompiler.GetOrCompile(operation).Variant == "strength_gain";

    public static bool IsExhaustAllHand(ComponentAtom atom) =>
        atom.Template == "N:Exhaust"
        && RuntimeSpec(atom) is
            { Opcode: "exhaust_card", Variant: "all", SourceZone: "hand", CardFilter: "any" };

    public static bool IsExhaustAllHand(GeneratorOperation operation) =>
        operation.Template == "N:Exhaust"
        && OperationRuntimeSpecCompiler.GetOrCompile(operation) is
            { Opcode: "exhaust_card", Variant: "all", SourceZone: "hand", CardFilter: "any" };

    public static bool HasValidAllCardsCostIncreaseAssembly(IReadOnlyList<GeneratorOperation> operations)
    {
        foreach (var operation in operations.Where(operation =>
                     operation.Template == "NCR:IncreaseAllCardCostsThisTurn"))
        {
            if (OperationRuntimeSpecCompiler.StaticLiteralValue(operation, "amount") is not (>= 1 and <= 3))
                return false;
            if (!operations.Any(IsBeneficialEffect))
                return false;
        }
        return true;
    }

    public static bool HasValidHighCostThresholds(IReadOnlyList<GeneratorOperation> operations) =>
        operations.Where(operation => operation.Template is
                "A:whenEnergyCostAtLeast" or "NCR:WheneverHighCostCardPlayed")
            .All(operation => OperationRuntimeSpecCompiler.StaticLiteralValue(operation, "threshold")
                is >= 1 and <= 3);

    public static bool HasValidEnergyXDoubleThreshold(IReadOnlyList<GeneratorOperation> operations)
    {
        if (!operations.Where(operation => operation.Template == "R:IfEnergyXAtLeast")
                .All(operation => OperationRuntimeSpecCompiler.StaticLiteralValue(operation, "threshold")
                    is >= 1 and <= 4))
            return false;
        foreach (var modifier in operations.Where(operation =>
                     operation.Template == "R:DoubleEitherXAtThreshold"))
        {
            if (OperationRuntimeSpecCompiler.StaticLiteralValue(modifier, "threshold") != 4)
                return false;
            if (!operations.Any(operation => !ReferenceEquals(operation, modifier)
                    && OperationRuntimeSpecCompiler.GetOrCompile(operation).Values.Any(value =>
                        value.Source is "energy_x" or "star_x" or "special_x")))
                return false;
        }
        return true;
    }

    public static bool RequiresSingleEnemyTarget(ComponentAtom atom) =>
        atom.Scope == OperationScope.SingleEnemyOnly || atom.RequiresSingleTarget
        || RuntimeSpec(atom).Flags.Contains("requires_selected_enemy");

    public static bool RequiresSingleEnemyTarget(GeneratorOperation operation) =>
        RequiresSingleEnemyTargetBySpec(operation);

    internal static bool RequiresSingleEnemyTargetBySpec(GeneratorOperation operation)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        return operation.Scope == OperationScope.SingleEnemyOnly || operation.RequiresSingleTarget
            || spec.Flags.Contains("requires_selected_enemy");
    }

    public static bool CanResolveTriggeredEnemyTarget(GeneratorOperation trigger, ComponentAtom effect) =>
        RuntimeSpec(effect).Flags.Contains("random_enemy_reference")
        || TriggerSuppliesEnemyTarget(trigger) && RuntimeSpec(effect).Flags.Contains("event_enemy_reference");

    public static bool CanResolveTriggeredEnemyTarget(GeneratorOperation trigger, GeneratorOperation effect) =>
        UsesExplicitRandomEnemyTargetBySpec(effect)
        || TriggerSuppliesEnemyTarget(trigger) && UsesExplicitEventEnemyTargetBySpec(effect);

    /// <summary>
    /// Triggers whose linked effects are executed by a detached temporary power after the original card resolution.
    /// That power has no stable enemy identity to serialize, so it cannot reuse the card's original selected target.
    /// </summary>
    public static bool TriggerLosesOriginalEnemyTarget(GeneratorOperation trigger) => trigger.Template is
        "D:NextTurnsStart" or "NCR:NextTurn" or "R:NextTurn" or "CL:AtNextTurnStart" or "CL:AfterTurns";

    public static bool HasValidDeferredEnemyTargetAssembly(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var effectIndex = 0; effectIndex < operations.Count; effectIndex++)
        {
            var effect = operations[effectIndex];
            if (!CardEffectRules.RequiresSingleEnemyTarget(effect)
                || !effect.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                || triggerIndex < 0 || triggerIndex >= effectIndex)
                continue;
            var trigger = operations[triggerIndex];
            if (TriggerLosesOriginalEnemyTarget(trigger)
                && !CanResolveTriggeredEnemyTarget(trigger, effect))
                return false;
        }
        return true;
    }

    public static bool HasNoSelfTriggeringHpLoss(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var effect = operations[index];
            if (effect.Template != "N:HP-"
                || !effect.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                || triggerIndex < 0 || triggerIndex >= index)
                continue;
            // Paying HP from inside a "whenever you lose HP" payload can recursively emit the event that owns
            // the same payload. Reject the assembly instead of relying on engine event-queue reentrancy guards.
            if (OperationRuntimeSpecCompiler.GetOrCompile(operations[triggerIndex])
                .Flags.Contains("hp_loss_reference"))
                return false;
        }
        return true;
    }

    public static bool HasNoSelfTriggeringDraw(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var effect = operations[index];
            if (!IsImmediateDrawEffect(effect)
                || !effect.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                || triggerIndex < 0 || triggerIndex >= index)
                continue;
            var trigger = operations[triggerIndex];
            // Drawing is the event that owns this payload. An immediate Draw payoff can emit the same event before
            // resolution finishes, producing recursive text and, depending on pile state, an unbounded hook chain.
            if (IsEveryCardDrawnTrigger(trigger)) return false;
        }
        return true;
    }

    public static bool HasNoSelfTriggeringBlock(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var effect = operations[index];
            if (!IsImmediateBlockGain(effect)
                || !effect.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                || triggerIndex < 0 || triggerIndex >= index)
                continue;
            // A Block-gained hook whose own payload immediately grants Block can recursively emit the event that
            // owns it. Reject the semantic loop at generation/validation time rather than relying on event guards.
            if (IsBlockGainedTrigger(operations[triggerIndex])) return false;
        }
        return true;
    }

    public static bool IsBlockGainedTrigger(GeneratorOperation trigger) =>
        trigger.Template == "A_WHEN_GAIN_BLOCK"
        || OperationRuntimeSpecCompiler.GetOrCompile(trigger).Trigger?.Kind == "block_gained";

    private static bool IsImmediateBlockGain(GeneratorOperation operation)
    {
        if (operation.Template is "N:B" or "N_BLOCK" or "N:BlockEqualAllPoison"
            or "CL:GainBlockEqualDamage" or "CL:GainBlockEqualCurrent"
            or "I:DrawAndBlockIfSkill" or "NCR:BlockTripleOstyMaxHp")
            return true;
        // Keep this semantic rather than catalog-only: new computed-Block operations must not silently reopen
        // "whenever you gain Block -> gain Block" recursion. Delayed Block and permanent card-stat growth do not
        // synchronously emit the event and are therefore legal.
        return OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("immediate_block_gain");
    }

    public static bool IsEveryCardDrawnTrigger(GeneratorOperation trigger) => trigger.Template is
            "A:whenCardDrawnDuringTurn" or "C:untilTurnEndCardDrawn"
        || OperationRuntimeSpecCompiler.GetOrCompile(trigger).Trigger?.Kind is
            "card_drawn" or "card_drawn_during_turn";

    public static bool NeedsExternalCardSlot(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("needs_external_card_slot");

    public static string EffectFamily(GeneratorOperation operation)
    {
        if (IsEnemyDamage(operation)) return "damage";
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        if (spec.Flags.Contains("block_reference")) return "block";
        if (spec.Flags.Contains("effect_family_draw")) return "draw";
        if (operation.Template is "N:E" or "N:NextTurnEnergy" or "D:GainEnergy" or "D:NextTurnEnergy"
            or "NCR:GainEnergy" or "NCR:NextTurnEnergy" or "R:GainEnergy") return "energy";
        if (spec.Flags.Contains("effect_family_stars")) return "stars";
        if (spec.Flags.Contains("poison_reference")) return "poison";
        if (spec.Flags.Contains("doom_reference")) return "doom";
        if (spec.Flags.Contains("vulnerable_reference")) return "vulnerable";
        if (spec.Flags.Contains("weak_reference")) return "weak";
        if (spec.Flags.Contains("summon_reference")) return "summon";
        if (spec.Flags.Contains("orb_reference")) return "orb";
        if (spec.Flags.Contains("forge_reference")) return "forge";
        if (spec.Flags.Contains("strength_reference")) return "strength";
        if (spec.Flags.Contains("dexterity_reference")) return "dexterity";
        if (spec.Flags.Contains("focus_reference")) return "focus";
        if (spec.Flags.Contains("plating_reference")) return "plating";
        if (spec.Flags.Contains("thorns_reference")) return "thorns";
        if (DerivativeSlotCatalog.IsSlotOperation(operation.Template)) return "derivative";
        return string.Empty;
    }

    public static string EffectFamily(ComponentAtom atom) => EffectFamily(new GeneratorOperation(atom.Template,
        atom.Scope, atom.ChineseText, new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget,
        RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom), LocalizedText: atom.LocalizedText));

    public static string? PrintedDamageValueSlot(GeneratorOperation operation)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        if (!IsEnemyDamage(operation) || !spec.Flags.Contains("printed_damage_value")) return null;
        var slots = spec.Values.Where(slot => slot.Explicit && slot.Source == "fixed").ToArray();
        return slots.FirstOrDefault(slot => slot.Id == "damage")?.Id
            ?? slots.FirstOrDefault(slot => slot.Id.Contains("damage", StringComparison.Ordinal))?.Id
            ?? slots.FirstOrDefault()?.Id;
    }

    public static string? PrintedBlockValueSlot(GeneratorOperation operation)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        if (!IsBeneficialEffect(operation) || !spec.Flags.Contains("printed_block_value")) return null;
        var slots = spec.Values.Where(slot => slot.Explicit && slot.Source == "fixed").ToArray();
        return slots.FirstOrDefault(slot => slot.Id == "block")?.Id
            ?? slots.FirstOrDefault(slot => slot.Id.Contains("block", StringComparison.Ordinal))?.Id
            ?? slots.FirstOrDefault()?.Id;
    }

    public static bool HasValidGrandFinaleAssembly(IReadOnlyList<GeneratorOperation> operations) =>
        !operations.Any(operation => operation.Template == "N:AllD"
            && OperationRuntimeSpecCompiler.FixedValue(operation, "damage") == 60)
        || operations.Any(operation => operation.Template == "C:playableIfDrawPileEmpty");

    public static bool HasValidGrandFinaleCost(int energyCost, int starCost, bool hasStarCostX,
        IReadOnlyList<GeneratorOperation> operations) =>
        !operations.Any(operation => operation.Template == "C:playableIfDrawPileEmpty")
        || energyCost == 0 && starCost <= 0 && !hasStarCostX;

    public static string FieldKey(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.StructuralFieldKey(operation);

    public static int FieldOccurrenceCount(IReadOnlyList<GeneratorOperation> operations, ComponentAtom atom)
    {
        var structured = operations.Count(operation =>
            string.Equals(FieldKey(operation), atom.SchemaKey, StringComparison.Ordinal));
        if (OperationRuntimeSpecCompiler.EnableLegacyEquivalenceAssertions)
        {
            var legacyAtom = $"{NumericTextSchema.Family(atom.Template)}|{NumericTextSchema.Fields(atom.ChineseText)}";
            var legacy = operations.Count(operation => string.Equals(
                $"{NumericTextSchema.Family(operation.Template)}|{NumericTextSchema.Fields(operation.ChineseText)}",
                legacyAtom, StringComparison.Ordinal));
            if (legacy != structured)
                throw new InvalidOperationException($"Field occurrence schema drift for {atom.Template}:"
                    + $" legacy={legacy}, structured={structured}, atom={atom.ChineseText}, operations="
                    + string.Join(" || ", operations.Select(operation =>
                        $"{operation.Template}:{operation.ChineseText}:d={operation.DerivativeId}:"
                        + $"e={operation.DerivativeEnchantmentId}:os={operation.OrbSourceId}:oo={operation.OrbOutputId}")));
        }
        return structured;
    }

    public static bool HasAtMostTwoOfEachField(IReadOnlyList<GeneratorOperation> operations)
    {
        var structured = operations.GroupBy(FieldKey, StringComparer.Ordinal).All(group => group.Count() <= 2);
        if (OperationRuntimeSpecCompiler.EnableLegacyEquivalenceAssertions)
        {
            var legacy = operations.GroupBy(operation =>
                    $"{NumericTextSchema.Family(operation.Template)}|{NumericTextSchema.Fields(operation.ChineseText)}",
                    StringComparer.Ordinal)
                .All(group => group.Count() <= 2);
            if (legacy != structured)
                throw new InvalidOperationException("Maximum field occurrence schema drift: "
                    + string.Join(" || ", operations.Select(operation =>
                        $"{operation.Template}:{operation.ChineseText}:d={operation.DerivativeId}:"
                        + $"e={operation.DerivativeEnchantmentId}:os={operation.OrbSourceId}:oo={operation.OrbOutputId}")));
        }
        return structured;
    }

    /// <summary>
    /// Identifies the semantic shape of an X-scaled effect. Numeric values are ignored while the position of X
    /// is retained, so equivalent cross-character templates such as “deal 3 damage X times” collide, but
    /// “deal X damage 3 times” remains a different and therefore compatible shape.
    /// </summary>
    public static string XEffectKindKey(ComponentAtom atom) =>
        OperationRuntimeSpecCompiler.StructuralXEffectKindKey(atom);

    public static string XEffectKindKey(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.StructuralXEffectKindKey(operation);

    public static bool HasSameXEffectKind(GeneratorOperation operation, ComponentAtom atom)
    {
        var structured = string.Equals(XEffectKindKey(operation), XEffectKindKey(atom), StringComparison.Ordinal);
        if (OperationRuntimeSpecCompiler.EnableLegacyEquivalenceAssertions)
        {
            var legacy = string.Equals(NumericTextSchema.Fields(operation.ChineseText).Trim(),
                NumericTextSchema.Fields(atom.ChineseText).Trim(), StringComparison.Ordinal);
            if (legacy != structured)
                throw new InvalidOperationException("X effect schema drift: operation="
                    + $"{operation.Template}:{operation.ChineseText}:{XEffectKindKey(operation)}; atom="
                    + $"{atom.Template}:{atom.ChineseText}:{XEffectKindKey(atom)}");
        }
        return structured;
    }

    public static bool HasNoDuplicateXEffectKinds(IReadOnlyList<GeneratorOperation> operations)
    {
        var xOperations = operations.Where(operation => XRequirement(operation) != XResourceRequirement.None)
            .ToArray();
        var structured = xOperations.GroupBy(XEffectKindKey, StringComparer.Ordinal)
            .All(group => group.Count() == 1);
        if (OperationRuntimeSpecCompiler.EnableLegacyEquivalenceAssertions)
        {
            var legacy = xOperations.GroupBy(operation => NumericTextSchema.Fields(operation.ChineseText).Trim(),
                    StringComparer.Ordinal)
                .All(group => group.Count() == 1);
            if (legacy != structured)
                throw new InvalidOperationException("Duplicate X effect schema drift: "
                    + string.Join(" || ", xOperations.Select(operation =>
                        $"{operation.Template}:{operation.ChineseText}:{XEffectKindKey(operation)}")));
        }
        return structured;
    }

    /// <summary>
    /// Effects whose second application on the same card is semantically redundant are card-level singletons.
    /// This is intentionally narrower than the ordinary repeated-field limiter: numeric effects that really stack
    /// remain legal, while idempotent switches and whole-zone replacement/clearing effects cannot be selected twice
    /// and therefore cannot have their positive or negative budget counted twice.
    /// </summary>
    public static bool WouldDuplicateCardUniqueEffect(IReadOnlyList<GeneratorOperation> previous,
        ComponentAtom candidate)
    {
        var key = CardUniqueEffectKey(candidate);
        return key is not null && previous.Any(operation =>
            string.Equals(CardUniqueEffectKey(operation), key, StringComparison.Ordinal));
    }

    public static bool HasNoDuplicateCardUniqueEffects(IReadOnlyList<GeneratorOperation> operations)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            var key = CardUniqueEffectKey(operation);
            if (key is not null && !seen.Add(key)) return false;
        }
        return true;
    }

    internal static string? CardUniqueEffectKey(ComponentAtom atom)
    {
        if (IsExhaustAllHand(atom)) return "clear:all_hand_exhaust";
        return CardUniqueEffectKey(atom.Template, RuntimeSpec(atom));
    }

    internal static string? CardUniqueEffectKey(GeneratorOperation operation)
    {
        if (IsExhaustAllHand(operation)) return "clear:all_hand_exhaust";
        return CardUniqueEffectKey(operation.Template, OperationRuntimeSpecCompiler.GetOrCompile(operation));
    }

    private static string? CardUniqueEffectKey(string template, OperationRuntimeSpec spec)
    {
        if (spec.Opcode == "combat_rule" && spec.Variant is
            "retain_block_between_turns" or "skills_cost_zero" or "derivative_hits_all"
            or "derivative_retain" or "played_skills_gain_sly" or "retain_hand_at_turn_end" or "die_on_unblocked_attack"
            or "kings_sword_hits_all")
            return "rule:" + spec.Variant;

        return template switch
        {
            // All effects that change this generated card's post-resolution destination share one card-level
            // singleton. Two return-to-Hand routes are redundant, while returning to Hand and putting this card on
            // top of the Draw Pile compete for the same card instance and cannot both be honored deterministically.
            "CL:ReturnThisToHand" or "R:ReturnThisToHand" or "R:ReturnAfterSkillsPlayed"
                or "NCR:ReturnFromDiscardOnHighCostPlay" or "R:PutThisOnDraw" => "self:destination",
            "I:PreventDrawThisTurn" => "turn:no_draw",
            "I:FreeHandThisTurn" => "turn:free_hand",
            "CL:RetainHandThisTurn" or "R:RetainHandThisTurn" => "turn:retain_hand",
            "N:DiscardAll" => "clear:all_hand_discard",
            "D:ExhaustAllStatuses" => "clear:all_statuses_exhaust",
            "R:FillHandWithDebris" => "fill:hand",
            "R:EndTurn" => "turn:end",
            "R:DoubleEitherXAtThreshold" => "x:double_at_threshold",
            "I:Transform" => "transform:all_hand_attacks",
            "D:TransformStatusesToFuel" => "transform:all_hand_statuses",
            "T:RemoveBlockAndArtifact" => "target:remove_all_block_and_artifact",
            "CL:DieOnUnblockedAttack" => "rule:die_on_unblocked_attack",
            "CL:NoBlockFromCards" => "rule:no_block_from_cards",
            "R:KingsSwordHitsAllEnemies" => "rule:kings_sword_hits_all",
            _ => null
        };
    }

    public static bool IsImmediateDrawEffect(ComponentAtom atom) => IsImmediateDrawTemplate(atom.Template);

    public static bool IsImmediateDrawEffect(GeneratorOperation operation) =>
        IsImmediateDrawTemplate(operation.Template);

    private static bool IsImmediateDrawTemplate(string template) => template is
        "N:Draw" or "N_DRAW" or "I:DrawAndBlockIfSkill" or "I:DrawWithRetain"
        or "I:DrawUntilNonAttack" or "I:DiscardHandDrawSame" or "D:DrawAndDiscardNonZero";

    public static bool IsCardDrawEffect(ComponentAtom atom) => IsCardDrawTemplate(atom.Template);

    public static bool IsCardDrawEffect(GeneratorOperation operation) =>
        IsCardDrawTemplate(operation.Template);

    private static bool IsCardDrawTemplate(string template) => template is
        "N:Draw" or "N_DRAW" or "N:NextTurnDraw"
        or "I:DrawAndBlockIfSkill" or "I:DrawWithRetain" or "I:DrawUntilNonAttack"
        or "I:DiscardHandDrawSame" or "D:DrawAndDiscardNonZero";

    public static bool IsDelayedEffect(ComponentAtom atom) => IsDelayedEffect(atom.Template, RuntimeSpec(atom));

    public static bool IsDelayedEffect(GeneratorOperation operation) =>
        IsDelayedEffect(operation.Template, OperationRuntimeSpecCompiler.GetOrCompile(operation));

    private static bool IsDelayedEffect(string template, OperationRuntimeSpec spec) => template is
        "R:NextTurn" or "NCR:NextTurn" or "D:NextTurnsStart" or "CL:AtNextTurnStart" or "CL:AfterTurns"
        or "N:NextTurnBlock" or "N:NextTurnEnergy" or "N:NextTurnDraw" or "N:KeepBlockNextTurn"
        or "D:NextTurnEnergy" or "NCR:NextTurnEnergy" or "I:CopySelectedCardNextTurn"
        or "I:DoubleAttackDamageNextTurn"
        || spec.Flags.Contains("delayed_effect");

    public static bool HasValidShuffleThenDrawAssembly(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            if (operations[index].Template != "D:ShuffleAllUnexhaustedIntoDraw") continue;
            if (index + 1 >= operations.Count || !IsImmediateDrawEffect(operations[index + 1]))
                return false;
            if (operations[index].Parameters.GetValueOrDefault("triggerIndex", -1)
                != operations[index + 1].Parameters.GetValueOrDefault("triggerIndex", -1))
                return false;
        }
        return true;
    }

    /// <summary>
    /// “Cannot draw more cards this turn” only blocks later draw lines. Drawing first and then applying the lock
    /// remains legal (Battle Trance). A later draw is also legal when it belongs to a Power/condition that can
    /// continue for more than one turn, because that payoff is not an attempt to draw after the lock resolves in
    /// the current OnPlay sequence.
    /// </summary>
    public static bool HasValidPreventDrawOrdering(IReadOnlyList<GeneratorOperation> operations)
    {
        var preventIndex = -1;
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Template == "I:PreventDrawThisTurn")
            {
                preventIndex = index;
                continue;
            }
            if (preventIndex >= 0 && IsCardDrawEffect(operation)
                && !HasMultiTurnTriggerOwner(operations, index))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Exhausting the whole hand empties it for the remainder of that same immediate/triggered resolution group.
    /// A later operation may use hand cards only after an intervening draw/generation line repopulates the hand;
    /// effects owned by a different trigger are independent and are checked in their own group.
    /// </summary>
    public static bool HasValidExhaustAllHandOrdering(IReadOnlyList<GeneratorOperation> operations)
    {
        var clearedGroups = new HashSet<int>();
        var immediateHandCleared = false;
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            var group = ResolutionGroup(operations, index);
            var independentLaterResolution = HasMultiTurnTriggerOwner(operations, index);
            if ((clearedGroups.Contains(group) || immediateHandCleared && !independentLaterResolution)
                && RequiresExistingHandCards(operation))
                return false;
            if (clearedGroups.Contains(group) && ReplenishesHand(operation))
                clearedGroups.Remove(group);
            if (immediateHandCleared && !independentLaterResolution && ReplenishesHand(operation))
                immediateHandCleared = false;
            if (IsExhaustAllHand(operation))
            {
                clearedGroups.Add(group);
                if (!independentLaterResolution)
                    immediateHandCleared = true;
            }
        }
        return true;
    }

    private static int ResolutionGroup(IReadOnlyList<GeneratorOperation> operations, int index)
    {
        var current = index;
        var visited = new HashSet<int>();
        while (current >= 0 && current < operations.Count
               && operations[current].Parameters.TryGetValue("triggerIndex", out var triggerIndex)
               && triggerIndex >= 0 && triggerIndex < current && visited.Add(triggerIndex))
            current = triggerIndex;
        return current == index ? -1 : current;
    }

    private static bool RequiresExistingHandCards(GeneratorOperation operation) =>
        operation.Template is "N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK"
            or "I:AutoPlayRandomAttackFromHand" or "I:ExhaustRandomAttack"
            or "N:Discard" or "N:DiscardAll" or "N_EXHAUST_SELECTED"
            or "D:ExhaustSelectedHandCard" or "CL:ExhaustUpToHandCards"
            or "CL:TransformSelectedHandCards" or "R:PutSelectedHandCardsOnDraw"
            or "R:PutSelectedHandCardOnDraw" or "I:GrantSlyToHandSkillThisTurn"
            or "I:CopySelectedCardNextTurn" or "I:FreeHandThisTurn"
        || operation.Template == "N:Exhaust" && !IsExhaustAllHand(operation)
        || OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("requires_hand_cards");

    private static bool ReplenishesHand(GeneratorOperation operation) =>
        IsImmediateDrawEffect(operation)
        || OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("replenishes_hand");

    private static bool HasMultiTurnTriggerOwner(IReadOnlyList<GeneratorOperation> operations, int effectIndex)
    {
        var visited = new HashSet<int>();
        var current = effectIndex;
        while (current >= 0 && current < operations.Count
               && operations[current].Parameters.TryGetValue("triggerIndex", out var triggerIndex)
               && triggerIndex >= 0 && triggerIndex < current && visited.Add(triggerIndex))
        {
            var trigger = operations[triggerIndex];
            if (trigger.Scope == OperationScope.AbilityTrigger)
                return true;
            if (trigger.Template is "D:NextTurnsStart" or "CL:AfterTurns"
                && OperationRuntimeSpecCompiler.StaticLiteralValue(trigger,
                    trigger.Template == "D:NextTurnsStart" ? "duration" : "threshold") is > 1)
                return true;
            current = triggerIndex;
        }
        return false;
    }

    public static bool UsesExplicitRandomEnemyTarget(GeneratorOperation operation) =>
        UsesExplicitRandomEnemyTargetBySpec(operation);

    public static bool UsesExplicitRandomEnemyTarget(ComponentAtom atom) =>
        RuntimeSpec(atom).Flags.Contains("random_enemy_reference");

    internal static bool UsesExplicitRandomEnemyTargetBySpec(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("random_enemy_reference");

    internal static bool UsesExplicitEventEnemyTargetBySpec(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("event_enemy_reference");

    private static bool TriggerSuppliesEnemyTarget(GeneratorOperation trigger) => trigger.Template is
        "A:whenLightningEvoked" or "A:whenAttackDealsDamage" or "A:whenDebuffApplied" or "A:whenDoomApplied"
        or "NCR:FirstAttackPlayedEachTurn";

    internal static bool SuppliesEventAttackForDamageModifier(ComponentAtom trigger) =>
        SuppliesEventAttackForDamageModifier(OperationRuntimeSpecCompiler.GetOrCompile(trigger));

    internal static bool SuppliesEventAttackForDamageModifier(GeneratorOperation trigger) =>
        SuppliesEventAttackForDamageModifier(OperationRuntimeSpecCompiler.GetOrCompile(trigger));

    private static bool SuppliesEventAttackForDamageModifier(OperationRuntimeSpec spec) => spec.Trigger?.Kind is
        "attack_played" or "first_attack_played_each_turn" or "first_zero_cost_attack_played_each_turn"
        or "nth_attack_played_this_turn" or "next_attack" or "next_attacks_this_turn";

    public static bool IsExhaustPileTurnEndTrigger(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation).Trigger?.Kind == "turn_end_if_self_in_exhaust";

    public static bool IsExhaustPileTurnStartTrigger(GeneratorOperation operation) =>
        operation.Template == "R:AtTurnStartIfInExhaust";

    public static bool IsSelfExhaustEventTrigger(GeneratorOperation operation) =>
        operation.CardTargetSlot == "thisCard"
        && OperationRuntimeSpecCompiler.GetOrCompile(operation).Trigger?.Kind == "self_exhausted";

    public static bool HasNoNegativeSelfExhaustPayoffs(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var effect = operations[index];
            if (!effect.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                || triggerIndex < 0 || triggerIndex >= index
                || !IsSelfExhaustEventTrigger(operations[triggerIndex]))
                continue;
            if (IsNegativeEffect(effect)) return false;
        }
        return true;
    }

    public static bool IsOrdinaryOnPlayEffect(GeneratorOperation operation) =>
        !operation.Parameters.ContainsKey("triggerIndex")
        && operation.Template != "I:PlayThisCard"
        && operation.Scope is OperationScope.SingleEnemyOnly or OperationScope.NonTargeted or OperationScope.Independent;

    public static bool HasValidPlayThisCardAssembly(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Template is not ("I:PlayThisCard" or "R:PlayThisCard")) continue;
            if (!operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                || triggerIndex < 0
                || triggerIndex >= operations.Count
                || (operation.Template == "I:PlayThisCard"
                    ? !IsExhaustPileTurnEndTrigger(operations[triggerIndex])
                    : !IsExhaustPileTurnStartTrigger(operations[triggerIndex])))
                return false;
            // The payload must already exist before the trigger.  A later or trigger-linked operation cannot make
            // auto-playing the card useful, because it is not part of the card's normal OnPlay payload.
            if (!operations.Take(triggerIndex).Any(IsOrdinaryOnPlayEffect))
                return false;
        }
        return true;
    }

    public static bool IsCopyThisCardToDiscard(ComponentAtom atom) =>
        RuntimeSpec(atom).Flags.Contains("copy_this_to_discard");

    public static bool IsCopyThisCardToDiscard(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("copy_this_to_discard");

    public static bool IsZeroCostCopyThisCardToDiscard(GeneratorOperation operation) =>
        operation.Template == "D:CreateZeroCostCopyInDiscard";

    public static CopyThisCardBudgetRole CopyThisCardBudgetRole(GeneratorOperation operation,
        bool hasPrintedResourceCost, GeneratedCardType cardType, IReadOnlyCollection<CardTag>? tags = null)
    {
        // The explicit 0-cost-copy operation is a separate, always-positive mechanic. This contextual rule is
        // for Anger's ordinary same-cost self-copy only.
        if (!IsCopyThisCardToDiscard(operation)
            || operation.Template == "D:CreateZeroCostCopyInDiscard")
            return global::ChaosCardGenerator.CopyThisCardBudgetRole.None;
        if (cardType == GeneratedCardType.Power)
            return global::ChaosCardGenerator.CopyThisCardBudgetRole.PowerBenefit;
        if (tags?.Contains(CardTag.Exhaust) == true)
            return global::ChaosCardGenerator.CopyThisCardBudgetRole.ExhaustOffset;
        return hasPrintedResourceCost
            ? global::ChaosCardGenerator.CopyThisCardBudgetRole.PaidReusableDownside
            : global::ChaosCardGenerator.CopyThisCardBudgetRole.FreeReusableBenefit;
    }

    public static bool HasValidCopyThisCardAssembly(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            if (!IsCopyThisCardToDiscard(operations[index])) continue;
            if (!operations.Where((_, candidateIndex) => candidateIndex != index)
                    .Any(operation => IsBeneficialEffect(operation)
                        && !IsCopyThisCardToDiscard(operation)
                        && !IsSelfCostChange(operation)))
                return false;
        }
        return true;
    }

    public static bool IsRandomCardGeneration(ComponentAtom atom) => IsRandomCardGenerationBySpec(atom.Template,
        RuntimeSpec(atom));

    public static bool IsRandomCardGeneration(GeneratorOperation operation) => IsRandomCardGenerationBySpec(
        operation.Template, OperationRuntimeSpecCompiler.GetOrCompile(operation));

    internal static bool IsRandomCardGenerationBySpec(string template, OperationRuntimeSpec spec) => template is
        "CL:AddRandomAttackToHand" or "CL:AddRandomColorlessToHand" or "CL:AddRandomZeroCostCardsToHand"
        or "NCR:AddRandomEtherealCardToHand" or "D:AddRandomPowerToHand" or "R:AddRandomColorlessToHand"
        or "I_CREATE_RANDOM_ATTACK" or "I:ProxyAtomic_WhiteNoise" or "CL:ProxyAtomic_Discovery"
        or "I:ProxyAtomic_Quasar" or "CL:ProxyAtomic_Splash" or "N:CreateCurrentCharacterCardInHand"
        or "I:Create"
        || spec.Opcode == "create_card" && spec.Variant == "current_character_random";

    public static bool IsDirectOrbChannel(ComponentAtom atom) => IsDirectOrbChannelTemplate(atom.Template);

    public static bool IsDirectOrbChannel(GeneratorOperation operation) =>
        IsDirectOrbChannelTemplate(operation.Template);

    private static bool IsDirectOrbChannelTemplate(string template) => template is
        "D:ChannelLightning" or "D:ChannelFrost" or "D:ChannelDark"
        or "D:ChannelPlasma" or "D:ChannelGlass" or "D:ChannelRandom";

    public static bool HasValidRandomCardGenerationCount(IReadOnlyList<GeneratorOperation> operations) =>
        operations.Where(IsRandomCardGeneration).All(operation =>
        {
            var value = OperationRuntimeSpecCompiler.PrimaryFixedValue(operation);
            return value is null or <= 5;
        });

    public static bool IsAttackCostReductionRule(GeneratorOperation operation) =>
        IsAttackCostReductionRuleBySpec(operation);

    internal static bool IsAttackCostReductionRuleBySpec(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation).Trigger?.Kind is
            "attack_played_cost_reduction" or "skill_played_cost_reduction";

    public static bool HasValidAttackCostReductionAssembly(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            if (!IsAttackCostReductionRule(operations[index])) continue;
            if (!operations.Where((_, candidateIndex) => candidateIndex != index).Any(IsOrdinaryOnPlayEffect))
                return false;
        }
        return true;
    }

    public static bool HasValidOstyAttackedCostAssembly(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            if (operations[index].Template != "NCR:SetCostZeroIfOstyAttacked") continue;
            if (!operations[index].Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                || triggerIndex < 0 || triggerIndex >= index
                || operations[triggerIndex].Template != "NCR:IfOstyAttackedThisTurn")
                return false;
            if (!operations.Where((_, candidateIndex) => candidateIndex != triggerIndex && candidateIndex != index)
                    .Any(IsOrdinaryOnPlayEffect))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether a card loses an immediate printed effect when played while Osty is missing. This mirrors the
    /// base-game red glow used by Osty Attack cards and Sacrifice. Passive powers such as Calcify and Necro
    /// Mastery are intentionally excluded: they resolve successfully now and may benefit a later Summon.
    /// An unconditional fixed Summon earlier on the same card satisfies later Osty-dependent clauses, matching
    /// Spur's native Summon-then-heal ordering. Triggered and X Summons cannot guarantee that prerequisite.
    /// </summary>
    public static bool RequiresLivingOstyForCurrentPlay(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (!RequiresPreexistingLivingOsty(operation)) continue;
            var hasTrigger = operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex);
            var summonsBeforeUse = operations.Take(index).Any(candidate =>
                candidate.Template == "NCR:Summon"
                && (!candidate.Parameters.TryGetValue("triggerIndex", out var summonTriggerIndex)
                    || hasTrigger && summonTriggerIndex == triggerIndex));
            if (!summonsBeforeUse) return true;
        }
        return false;
    }

    private static bool RequiresPreexistingLivingOsty(GeneratorOperation operation) => operation.Template is
        "T:ProxyDamage_Atomic_Poke"
        or "NCR:OstyDamage" or "NCR:OstyAllDamage"
        or "NCR:OstyMaxHpBonusDamage" or "NCR:OstyCurrentHpBonusDamage"
        or "NCR:IfOstyAlive" or "NCR:KillOsty" or "NCR:BlockTripleOstyMaxHp" or "NCR:HealOsty";

    internal static bool IsReviewedOstyLifecycleOperation(GeneratorOperation operation) =>
        RequiresPreexistingLivingOsty(operation)
        || operation.Template is "A:whenOstyLosesHp" or "A:ProxyAtomic_Calcify"
            or "NCR:DamagePerOstyAttackCard" or "NCR:ForEachOstyAttackCard"
            or "NCR:ForEachOstyAttackThisTurn" or "NCR:IfOstyAttackedThisTurn"
            or "NCR:RepeatPerOstyAttackThisTurn" or "NCR:SetCostZeroIfOstyAttacked"
            or "NCR:WheneverOstyAttacksTargetThisTurn" or "NCR:ApplyPower_SicEmPower";

    public static bool IsSelfCostChange(GeneratorOperation operation) => operation.Template is
        "D:IncreaseThisCardCost"
        || IsSelfCostReduction(operation);

    public static bool IsSelfCostReduction(GeneratorOperation operation) => operation.Template is
        "I:ReduceThisCardCostCombat"
        or "D:SetThisCardCostZero"
        or "D:CostDownWhenStatusGenerated"
        or "NCR:CostDownPerVoidPlayed"
        or "NCR:CostDownWhenCreatureDies"
        or "NCR:SetCostZeroIfOstyAttacked"
        or "R:CostDownWhenDrawn"
        || IsAttackCostReductionRule(operation);

    public static bool IsNumericSelfCostReduction(GeneratorOperation operation) => operation.Template is
        "I:ReduceThisCardCostCombat"
        or "D:CostDownWhenStatusGenerated"
        or "NCR:CostDownPerVoidPlayed"
        or "NCR:CostDownWhenCreatureDies"
        or "R:CostDownWhenDrawn"
        or "C:whileInCombat"
        or "C:whileInCombatSkillCostReduction";

    public static bool HasValidNumericSelfCostReductionAmounts(int energyCost,
        IReadOnlyList<GeneratorOperation> operations) =>
        operations.Where(IsNumericSelfCostReduction).All(operation => energyCost > 0
            && OperationRuntimeSpecCompiler.StaticLiteralValue(operation, "amount") is var amount
            && amount >= 1 && amount <= energyCost);

    public static bool ClampNumericSelfCostReductionAmounts(int energyCost, IList<GeneratorOperation> operations)
    {
        if (energyCost <= 0 && operations.Any(IsNumericSelfCostReduction)) return false;
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (!IsNumericSelfCostReduction(operation)) continue;
            var amount = OperationRuntimeSpecCompiler.StaticLiteralValue(operation, "amount", 1);
            var replacement = Math.Clamp(amount, 1, energyCost);
            if (replacement != amount
                && OperationRuntimeSpecCompiler.TryReplaceFixedValue(operation, "amount", replacement,
                    out var updated))
                operations[index] = updated;
        }
        return HasValidNumericSelfCostReductionAmounts(energyCost,
            operations as IReadOnlyList<GeneratorOperation> ?? operations.ToArray());
    }

    public static bool IsSelfCostChange(ComponentAtom atom) => atom.Template is
        "D:IncreaseThisCardCost"
        || IsSelfCostReduction(atom);

    public static bool IsSelfCostReduction(ComponentAtom atom) => atom.Template is
        "I:ReduceThisCardCostCombat"
        or "D:SetThisCardCostZero"
        or "D:CostDownWhenStatusGenerated"
        or "NCR:CostDownPerVoidPlayed"
        or "NCR:CostDownWhenCreatureDies"
        or "NCR:SetCostZeroIfOstyAttacked"
        or "R:CostDownWhenDrawn"
        || RuntimeSpec(atom).Trigger?.Kind is
            "attack_played_cost_reduction" or "skill_played_cost_reduction";

    public static bool HasValidSelfCostChangeAssembly(IReadOnlyList<GeneratorOperation> operations) =>
        !operations.Any(IsSelfCostChange)
        || operations.Any(operation => !IsSelfCostChange(operation) && IsBeneficialEffect(operation));

    /// <summary>Direct enemy damage from this card, used for Attack/Skill classification and Fatal legality.</summary>
    public static bool IsEnemyDamage(ComponentAtom atom) =>
        RuntimeSpec(atom).Flags.Contains(ComponentSemanticFlags.EnemyDamage)
        || IsEnemyDamage(atom.Template);

    /// <summary>Direct enemy damage from this card, used for Attack/Skill classification and Fatal legality.</summary>
    public static bool IsEnemyDamage(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains(ComponentSemanticFlags.EnemyDamage)
        || IsEnemyDamage(operation.Template);

    private static bool IsEnemyDamage(string template) =>
        template.StartsWith("T:D", StringComparison.Ordinal)
        || template.StartsWith("N:AllD", StringComparison.Ordinal)
        || template.StartsWith("N:RandomD", StringComparison.Ordinal)
        || template == "CL:RollingAllDamage"
        || template == "D:RepeatPerEnergySpentThisTurn"
        || template is "NCR:OstyDamage" or "NCR:OstyAllDamage" or "NCR:DoomScaledDamage" or "NCR:UnpoweredDamage"
        || template.StartsWith("T:ProxyDamage_", StringComparison.Ordinal)
        || template.StartsWith("N:ProxyDamage_", StringComparison.Ordinal);

    public static bool IsCurrentBlockDamageModifier(ComponentAtom atom) =>
        RuntimeSpec(atom) is { Opcode: "modify_damage", Variant: "current_block" };

    public static bool IsCurrentBlockDamageModifier(GeneratorOperation operation) =>
        IsCurrentBlockDamageModifierBySpec(operation);

    internal static bool IsCurrentBlockDamageModifierBySpec(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation) is
            { Opcode: "modify_damage", Variant: "current_block" };

    /// <summary>
    /// Current-Block damage is represented by a zero-valued T:D anchor followed by M:value. Bind the modifier to
    /// the nearest preceding ordinary single-target damage operation owned by the same trigger; other damage lines
    /// on the card remain independent and must not be overwritten.
    /// </summary>
    public static int CurrentBlockDamageAnchorIndex(IReadOnlyList<GeneratorOperation> operations,
        int modifierIndex)
    {
        if ((uint)modifierIndex >= (uint)operations.Count
            || !IsCurrentBlockDamageModifier(operations[modifierIndex]))
            return -1;
        var owner = operations[modifierIndex].Parameters.GetValueOrDefault("triggerIndex", -1);
        for (var index = modifierIndex - 1; index >= 0; index--)
        {
            var candidate = operations[index];
            if (candidate.Parameters.GetValueOrDefault("triggerIndex", -1) != owner) continue;
            if (candidate.Template == "T:D" && !IsIntrinsicMultiHitDamage(candidate)) return index;
        }
        return -1;
    }

    public static bool IsCurrentBlockDamageAnchor(IReadOnlyList<GeneratorOperation> operations, int index)
    {
        if ((uint)index >= (uint)operations.Count) return false;
        for (var modifierIndex = index + 1; modifierIndex < operations.Count; modifierIndex++)
            if (CurrentBlockDamageAnchorIndex(operations, modifierIndex) == index)
                return true;
        return false;
    }

    public static bool HasValidCurrentBlockDamageAssembly(IReadOnlyList<GeneratorOperation> operations)
    {
        var claimed = new HashSet<int>();
        for (var index = 0; index < operations.Count; index++)
        {
            if (!IsCurrentBlockDamageModifier(operations[index])) continue;
            var anchor = CurrentBlockDamageAnchorIndex(operations, index);
            if (anchor < 0 || !claimed.Add(anchor)) return false;
        }
        return true;
    }

    /// <summary>
    /// Determines whether an operation gives the card real positive value. Triggers, Exhaust, self-damage, draw lock,
    /// and enemy Strength do not count; an ordinary payoff following a cost is recognized independently.
    /// </summary>
    public static bool IsBeneficialEffect(ComponentAtom atom) => IsBeneficialEffect(new GeneratorOperation(
        atom.Template, atom.Scope, atom.ChineseText, EmptyParameters,
        RequiresSingleTarget: atom.RequiresSingleTarget,
        RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom), LocalizedText: atom.LocalizedText));

    public static bool IsBeneficialEffect(GeneratorOperation operation)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        if (IsNegativeEffect(operation) && !IsMixedBenefitAndDownside(operation.Template)) return false;
        if (spec.Flags.Contains(ComponentSemanticFlags.Beneficial)
            || ComponentValuationApi.IsRegisteredBenefit(spec)) return true;
        if (IsPlayerSelectedExhaust(operation)) return true;
        if (operation.Template.Contains(":Proxy", StringComparison.Ordinal)) return true;
        if (IsEnemyDamage(operation)
            || operation.Template.StartsWith("N:RetaliateDamage", StringComparison.Ordinal)
            || operation.Template is "T_DAMAGE" or "N_RANDOM_DAMAGE")
            return !OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("zero_damage");
        if (operation.Scope is OperationScope.Modifier or OperationScope.AbilityRule
            || operation.Template.StartsWith("M_", StringComparison.Ordinal)
            || operation.Template.StartsWith("A_RULE", StringComparison.Ordinal))
            return true;
        if (operation.Template.StartsWith("D:", StringComparison.Ordinal))
            return operation.Template is not ("D:CreateDazedInDiscard" or "D:CreateTwoWoundsInDiscard"
                or "D:CreateSlimeInDiscard" or "D:CreateBurnInDiscard" or "D:CreateVoidInDiscard"
                or "D:LoseFocus" or "D:LoseOrbSlots" or "D:LoseTemporaryFocus" or "D:IncreaseThisCardCost"
                or "D:ForEachExhaustedStatus" or "D:IfCardsPlayedBelow" or "D:IfEnemyIntendsAttack" or "D:IfFatal");
        if (operation.Template.StartsWith("NCR:", StringComparison.Ordinal))
            return operation.Template is not ("NCR:LoseStrength" or "NCR:IncreaseAllCardCostsThisTurn"
                or "NCR:KillOsty"
                or "NCR:IfOstyAlive" or "NCR:IfDoomAppliedThisTurn" or "NCR:IfFirstPlayThisTurn" or "NCR:NextTurn");
        if (operation.Template.StartsWith("R:", StringComparison.Ordinal))
            return operation.Template is not ("R:NextTurn" or "R:IfFatal" or "R:AtTurnStartIfInExhaust"
                or "R:EndTurn");
        if (operation.Template.StartsWith("CL:", StringComparison.Ordinal))
            return operation.Template is not ("CL:AtNextTurnStart" or "CL:IfFatal" or "CL:IfNoAttacksInHand"
                or "CL:IfHandEmpty" or "CL:EveryCardsDrawn" or "CL:EveryCardsPlayedThisTurn"
                or "CL:WheneverAttackPlayed" or "CL:FirstAttackOrSkillEachTurn"
                or "CL:WheneverDrawPileShuffled" or "CL:AfterTurns" or "CL:NoBlockFromCards");
        if (operation.Template is "T:Apply" or "T_APPLY_VULNERABLE" or "T:Poison" or "T:XWeak" or "T:XStrengthLoss" or "T:Strangle"
            or "T:RemoveBlockAndArtifact")
            return !OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("legacy_enemy_strength_one");
        if (operation.Template is "N:B" or "N:Create" or "N:CreateCurrentCharacterCardInHand" or "N:Draw" or "N:E" or "N:Heal" or "N:Move" or "N:Self" or "N:StrengthPerTargetVulnerable"
            or "N:Dex" or "N:Thorns" or "N:TempDex" or "N:Intangible" or "N:CreateShiv" or "N:CreateInkShiv"
            or "N:KeepBlockNextTurn" or "N:NextTurnBlock" or "N:NextTurnEnergy" or "N:NextTurnDraw"
            or "N:AllPoison" or "N:AllWeak" or "N:AllTempStrengthLoss" or "N:RandomPoison" or "N:BlockEqualAllPoison"
            or "N_BLOCK" or "N_DRAW" or "N_ENERGY" or "N_GAIN_STRENGTH" or "N_HEAL" or "N_APPLY_VULNERABLE_ALL")
            return true;
        if (operation.Template is "C:whileInCombat" or "C:whileInCombatSkillCostReduction")
            return true;
        if (operation.Template == "C:untilTurnEnd"
            && OperationRuntimeSpecCompiler.GetOrCompile(operation).Trigger?.Kind
                == "vulnerable_enemy_damage_reduction")
            return true;
        if (operation.Template.StartsWith("I:", StringComparison.Ordinal))
            return operation.Template is not "I:PreventDrawThisTurn";
        return operation.Template is
            "I:IncreaseDamageThisCombat"
            or "I:GainTemporaryStrength"
            or "I:AutoPlayRandomAttackFromHand"
            or "I:PlayTopXCards"
            or "I:PlayTopCardAndExhaust"
            or "I:PlayThisCard"
            or "I:ApplyToAllEnemies"
            or "I:PlayAtRandomEnemy"
            or "I:ReplayAttack"
            or "I:SetCostZero"
            or "I:AddExhaustedAttackDamage"
            or "I:DrawUntilNonAttack"
            or "I:GainMaxHp"
            or "I:Create"
            or "I:Transform"
            or "I:Upgrade"
            or "I:UpgradeThatCard"
            or "I_AUTOPLAY_DRAW_PILE_X"
            or "I_CREATE_RANDOM_ATTACK";
    }

    public static bool IsFatalCondition(ComponentAtom atom) =>
        RuntimeSpec(atom).Condition?.Kind == "fatal";

    public static bool IsFatalCondition(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation).Condition?.Kind == "fatal";

    public static bool UsesX(ComponentAtom atom) => UsesX(RuntimeSpec(atom));

    public static bool UsesX(GeneratorOperation operation) =>
        SpecialXCardConverter.IsSpecial(operation) || UsesX(OperationRuntimeSpecCompiler.GetOrCompile(operation));

    private static bool UsesX(OperationRuntimeSpec spec) =>
        spec.Flags.Any(flag => flag is "uses_energy_x" or "uses_star_x")
        || spec.Values.Any(value => value.Source is "energy_x" or "star_x" or "special_x");

    public static bool IsForEachExhaustTrigger(ComponentAtom atom) =>
        RuntimeSpec(atom).Trigger?.Kind is "for_each_exhausted_status" or "for_each_exhausted_card"
            or "for_each_exhausted_non_attack";

    public static bool IsForEachExhaustTrigger(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation).Trigger?.Kind is "for_each_exhausted_status"
            or "for_each_exhausted_card" or "for_each_exhausted_non_attack";

    public static bool IsCompatiblePriorExhaust(GeneratorOperation operation, ComponentAtom trigger)
    {
        if (operation.Parameters.ContainsKey("triggerIndex")) return false;
        if (trigger.Template == "D:ForEachExhaustedStatus") return operation.Template == "D:ExhaustAllStatuses";
        if (operation.Template == "N:Exhaust") return true;
        return RuntimeSpec(trigger).Trigger?.Kind == "for_each_exhausted_card"
            && operation.Template == "I:ExhaustRandomAttack";
    }

    public static bool IsCompatiblePriorExhaust(GeneratorOperation operation, GeneratorOperation trigger)
    {
        if (operation.Parameters.ContainsKey("triggerIndex")) return false;
        if (trigger.Template == "D:ForEachExhaustedStatus") return operation.Template == "D:ExhaustAllStatuses";
        if (operation.Template == "N:Exhaust") return true;
        return OperationRuntimeSpecCompiler.GetOrCompile(trigger).Trigger?.Kind == "for_each_exhausted_card"
            && operation.Template == "I:ExhaustRandomAttack";
    }

    public static bool IsCombatBaseDamageIncrease(ComponentAtom atom) =>
        atom.Template == "I:IncreaseDamageThisCombat";

    public static bool IsCombatBaseDamageIncrease(GeneratorOperation operation) =>
        operation.Template == "I:IncreaseDamageThisCombat";

    public static bool IsPrintedDamageReward(GeneratorOperation operation) =>
        IsEnemyDamage(operation)
        || operation.Template.StartsWith("N:RetaliateDamage", StringComparison.Ordinal)
        || operation.Template is "T_DAMAGE" or "N_RANDOM_DAMAGE";

    public static bool IsDamageBudgetEffect(GeneratorOperation operation) =>
        IsPrintedDamageReward(operation)
        || IsCombatBaseDamageIncrease(operation)
        || operation.Scope == OperationScope.Modifier
            && OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("damage_budget_effect");

    public static bool IsOrbEvokeReward(GeneratorOperation operation) =>
        operation.Template is "D:EvokeRightmostOrb" or "D:EvokeAllTwice";

    /// <summary>
    /// A scoped “next Attack” grant owns a modifier for that future Attack card. It is deliberately narrower
    /// than an ordinary trigger: arbitrary on-play rewards cannot be attached to it.
    /// </summary>
    public static bool IsNextAttackGrantTrigger(ComponentAtom atom) =>
        RuntimeSpec(atom).Trigger?.Kind is "next_attack" or "next_attacks_this_turn";

    public static bool IsNextAttackGrantTrigger(GeneratorOperation operation) =>
        IsNextAttackGrantTriggerBySpec(operation);

    internal static bool IsNextAttackGrantTriggerBySpec(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation).Trigger?.Kind is
            "next_attack" or "next_attacks_this_turn";

    public static bool IsNextAttackGrantPayoff(ComponentAtom atom) =>
        IsNextAttackGrantPayoff(atom.Template, atom.Scope, RuntimeSpec(atom));

    public static bool IsNextAttackGrantPayoff(GeneratorOperation operation) =>
        IsNextAttackGrantPayoff(operation.Template, operation.Scope,
            OperationRuntimeSpecCompiler.GetOrCompile(operation));

    private static bool IsNextAttackGrantPayoff(string template, OperationScope scope, OperationRuntimeSpec spec) =>
        template is "I:ReplayAttack" or "I:SetCostZero"
        || scope == OperationScope.Modifier
            && (template == "M:DamagePerExhaustCard"
                || spec.Opcode == "modify_damage"
                    && spec.Variant is "strike_count_scaled" or "vulnerable_scaled");

    public static int NextAttackGrantCount(GeneratorOperation trigger) =>
        trigger.Template == "C:grantNextAttacksThisTurn"
            ? Math.Clamp(OperationRuntimeSpecCompiler.StaticLiteralValue(trigger, "threshold", 1), 1, 3)
            : 1;

    public static bool HasValidNextAttackGrantAssembly(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var triggerIndex = 0; triggerIndex < operations.Count; triggerIndex++)
        {
            if (!IsNextAttackGrantTrigger(operations[triggerIndex])) continue;
            var linked = operations.Select((operation, index) => (operation, index))
                .Where(item => item.operation.Parameters.GetValueOrDefault("triggerIndex", -1) == triggerIndex)
                .ToArray();
            if (triggerIndex != operations.Count - 2 || linked.Length != 1
                || linked[0].index != operations.Count - 1
                || !IsNextAttackGrantPayoff(linked[0].operation))
                return false;
            if (operations[triggerIndex].Template == "C:grantNextAttacksThisTurn"
                && OperationRuntimeSpecCompiler.StaticLiteralValue(operations[triggerIndex], "threshold")
                    is not (>= 1 and <= 3))
                return false;
        }

        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Template is not ("I:ReplayAttack" or "I:SetCostZero")) continue;
            var owner = operation.Parameters.GetValueOrDefault("triggerIndex", -1);
            if (owner < 0 || owner >= index || !IsNextAttackGrantTrigger(operations[owner]))
                return false;
        }
        return true;
    }

    public static bool TriggerNeedsLinkedEffect(ComponentAtom atom) =>
        TriggerNeedsLinkedEffect(atom.Scope, atom.Template, RuntimeSpec(atom));

    public static bool TriggerNeedsLinkedEffect(GeneratorOperation operation) =>
        TriggerNeedsLinkedEffect(operation.Scope, operation.Template,
            OperationRuntimeSpecCompiler.GetOrCompile(operation));

    private static bool TriggerNeedsLinkedEffect(OperationScope scope, string template, OperationRuntimeSpec spec) =>
        scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger
        && template is not ("C:whileInCombat" or "C:whileInCombatSkillCostReduction")
        && spec.Trigger?.Kind != "vulnerable_enemy_damage_reduction";

    public static bool IsFailableOneShotCondition(GeneratorOperation operation) => operation.Template is
        "C:if" or "C:ifFatal" or "C:ifLastDrawnSkill" or "C:ifTargetPoisoned"
        or "CL:IfFatal" or "CL:IfHandEmpty" or "CL:IfNoAttacksInHand"
        or "D:IfCardsPlayedBelow" or "D:IfEnemyIntendsAttack" or "D:IfFatal"
        or "NCR:IfDoomAppliedThisTurn" or "NCR:IfFirstPlayThisTurn" or "NCR:IfOstyAlive"
        or "NCR:IfOstyAttackedThisTurn" or "R:IfFatal";

    /// <summary>
    /// A genuinely failable one-shot condition may enhance a card, but it may not own all of the card's value.
    /// Count-based modifiers (Poison/Doom stacks, pile size, etc.), repeat triggers and guaranteed delayed clauses
    /// are deliberately outside this set. Fatal clauses naturally pass because their host Damage is unconditional.
    /// </summary>
    public static bool HasValidFailableConditionAssembly(IReadOnlyList<GeneratorOperation> operations)
    {
        var conditionIndices = operations.Select((operation, index) => (operation, index))
            .Where(item => IsFailableOneShotCondition(item.operation))
            .Select(item => item.index).ToHashSet();
        if (conditionIndices.Count == 0) return true;
        if (operations.Any(IsPersistentPowerFoundation)) return true;
        return operations.Any(operation =>
            !operation.Template.StartsWith("N_SELECT_", StringComparison.Ordinal)
            && !IsFailableOneShotCondition(operation)
            && !IsDependencyPrefix(operation)
            && IsBeneficialEffect(operation)
            && (!operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                || !conditionIndices.Contains(triggerIndex)));
    }

    /// <summary>
    /// Payoffs whose implementation consumes state owned by one particular prefix. These remain linked even when
    /// the prefix also accepts ordinary effects. The table is intentionally about runtime dependency, not native
    /// card provenance: a normal Draw/Block/Forge/Channel operation never belongs here merely because one original
    /// card placed it after a count clause.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> DependencyPayoffs =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // Barrage's latter half repeats the host card's existing Damage rather than dealing an independent
            // amount. The count prefix is generic; this particular modifier is not.
            ["D:ForEachOrb"] = ["D:RepeatPerOrb"],
            // Retained only for pre-v0.2.157 Modifier-shaped snapshots. New cards use a generic trigger.
            ["D:ForEachEnergySpentThisTurn"] = ["D:RepeatPerEnergySpentThisTurn"],
            ["D:WheneverStatusGenerated"] = ["D:CostDownWhenStatusGenerated"],
            ["NCR:ForEachEtherealPlayedCombat"] = ["NCR:CostDownPerVoidPlayed", "NCR:RepeatPerVoidPlayedCombat"],
            ["NCR:ForEachCardDrawnThisTurn"] = ["NCR:DamagePerCardDrawnThisTurn"],
            ["NCR:ForEachDoomThreshold"] = ["NCR:DoomPerDoomThreshold"],
            ["NCR:ForEachOstyAttackThisTurn"] = ["NCR:RepeatPerOstyAttackThisTurn"],
            ["NCR:ForEachExhaustedSoul"] = ["NCR:DamagePerExhaustedSoul"],
            ["NCR:ForEachOstyAttackCard"] = ["NCR:DamagePerOstyAttackCard"],
            ["NCR:WheneverCreatureDies"] = ["NCR:CostDownWhenCreatureDies"],
            ["NCR:WheneverHighCostCardPlayed"] = ["NCR:ReturnFromDiscardOnHighCostPlay"],
            ["NCR:IfOstyAttackedThisTurn"] = ["NCR:SetCostZeroIfOstyAttacked"],
            ["NCR:WheneverCardPlayedThisTurn"] = ["NCR:ApplyPower_OblivionPower"],
            ["NCR:WheneverOstyAttacksTargetThisTurn"] = ["NCR:ApplyPower_SicEmPower"],
            ["R:ForEachPriorAttackHitOnTarget"] = ["R:ForgePerPriorHit"],
            ["R:ForEachStarCostCard"] = ["R:BonusPerStarCostCardInHand"],
            ["R:ForEachSkillPlayedThisTurn"] = ["R:RepeatPerSkillPlayedThisTurn"],
            ["R:ForEachStarGainedThisTurn"] = ["R:RepeatPerStarGainedThisTurn"],
            ["R:ForEachGeneratedCardCombat"] = ["R:BonusPerGeneratedCardThisCombat"],
            ["R:WheneverDrawn"] = ["R:CostDownWhenDrawn", "R:DamageUpWhenDrawn"],
            ["R:IfEnergyXAtLeast"] = ["R:DoubleEnergyX"],
            ["R:AtTurnEndWhenTopOfDraw"] = ["R:PlayAtTurnEndIfTopOfDraw"],
            ["CL:ForEachCardPlayedCombat"] = ["T:D"],
            ["CL:ForEachDrawPileCard"] = ["T:D"]
        };

    /// <summary>
    /// Count/condition prefixes whose following clause may be any independently executable numeric operation.
    /// Their native payoff stays reachable, but is no longer an exclusive whitelist. Prefixes driven by a live
    /// card lifecycle (draw this card, creature dies, return this from discard, and so on) are deliberately absent:
    /// those payoffs mutate or move the host card and cannot be replaced by a generic OnPlay operation.
    /// </summary>
    private static readonly ISet<string> GenericDependencyPrefixes = new HashSet<string>(StringComparer.Ordinal)
    {
        "D:ForEachOrb", "D:ForEachEnemy", "D:ForEachUniqueOrb", "D:IfHasFrost",
        "NCR:ForEachEtherealPlayedCombat", "NCR:ForEachCardDrawnThisTurn",
        "NCR:ForEachDoomThreshold", "NCR:ForEachOstyAttackThisTurn",
        "NCR:ForEachExhaustedSoul", "NCR:ForEachOstyAttackCard",
        "R:ForEachPriorAttackHitOnTarget", "R:ForEachStarCostCard",
        "R:ForEachSkillPlayedThisTurn", "R:ForEachStarGainedThisTurn",
        "R:ForEachGeneratedCardCombat", "R:IfEnergyXAtLeast",
        "R:IfCardsPlayedAtLeastThisTurn",
        "CL:ForEachCardPlayedCombat", "CL:ForEachDrawPileCard"
    };

    private static readonly ISet<string> MultiplicativeDependencyPrefixes = new HashSet<string>(
        GenericDependencyPrefixes.Where(template => template is not
            ("D:IfHasFrost" or "R:IfEnergyXAtLeast" or "R:IfCardsPlayedAtLeastThisTurn")),
        StringComparer.Ordinal);

    private static readonly ISet<string> DependencyOnlyPayoffs = new HashSet<string>(StringComparer.Ordinal)
    {
        "D:RepeatPerOrb", "D:CostDownWhenStatusGenerated",
        "NCR:CostDownPerVoidPlayed", "NCR:RepeatPerVoidPlayedCombat", "NCR:DamagePerCardDrawnThisTurn",
        "NCR:DoomPerDoomThreshold", "NCR:RepeatPerOstyAttackThisTurn", "NCR:DamagePerExhaustedSoul",
        "NCR:DamagePerOstyAttackCard", "NCR:CostDownWhenCreatureDies", "NCR:ReturnFromDiscardOnHighCostPlay",
        "NCR:SetCostZeroIfOstyAttacked",
        "NCR:ApplyPower_OblivionPower", "NCR:ApplyPower_SicEmPower",
        "R:ForgePerPriorHit", "R:BonusPerStarCostCardInHand", "R:RepeatPerSkillPlayedThisTurn",
        "R:RepeatPerStarGainedThisTurn", "R:BonusPerGeneratedCardThisCombat", "R:CostDownWhenDrawn",
        "R:DamageUpWhenDrawn", "R:DoubleEnergyX", "R:PlayAtTurnEndIfTopOfDraw"
    };

    public static bool IsDependencyPrefix(ComponentAtom atom) =>
        (DependencyPayoffs.ContainsKey(atom.Template) || GenericDependencyPrefixes.Contains(atom.Template))
        && (atom.Template != "D:ForEachEnergySpentThisTurn" || atom.Scope == OperationScope.Modifier);
    public static bool IsDependencyPrefix(GeneratorOperation operation) =>
        (DependencyPayoffs.ContainsKey(operation.Template) || GenericDependencyPrefixes.Contains(operation.Template))
        && (operation.Template != "D:ForEachEnergySpentThisTurn" || operation.Scope == OperationScope.Modifier);

    public static bool IsMultiplicativeDependencyPrefix(ComponentAtom atom) =>
        MultiplicativeDependencyPrefixes.Contains(atom.Template);

    public static bool IsMultiplicativeDependencyPrefix(GeneratorOperation operation) =>
        MultiplicativeDependencyPrefixes.Contains(operation.Template);

    private static readonly ISet<string> StandaloneEventDependencyPrefixes = new HashSet<string>(StringComparer.Ordinal)
    {
        "D:WheneverStatusGenerated",
        "NCR:WheneverCreatureDies",
        "NCR:WheneverHighCostCardPlayed",
        "NCR:WheneverCardPlayedThisTurn",
        "NCR:WheneverOstyAttacksTargetThisTurn",
        "R:WheneverDrawn",
        "R:AtTurnEndWhenTopOfDraw"
    };

    /// <summary>
    /// A complete event clause that owns its own payoff. Unlike a per-count modifier such as “for each Orb”,
    /// it cannot itself be the payoff of another trigger.
    /// </summary>
    public static bool IsStandaloneEventDependencyPrefix(ComponentAtom atom) =>
        StandaloneEventDependencyPrefixes.Contains(atom.Template);

    public static bool IsStandaloneEventDependencyPrefix(GeneratorOperation operation) =>
        StandaloneEventDependencyPrefixes.Contains(operation.Template);

    /// <summary>Fixed Regent Stars use a strict two-Stars-to-one-Energy budget conversion.</summary>
    public static int StarCostEnergyEquivalent(int starCost) => Math.Max(0, starCost) / 2;

    public static bool IsEnemyStrengthReduction(ComponentAtom atom) =>
        IsEnemyStrengthReduction(atom.Template, RuntimeSpec(atom));

    public static bool IsEnemyStrengthReduction(GeneratorOperation operation) =>
        IsEnemyStrengthReduction(operation.Template, OperationRuntimeSpecCompiler.GetOrCompile(operation));

    public static bool IsPermanentStrengthOrDexterityChange(ComponentAtom atom) =>
        IsPermanentStrengthOrDexterityChange(atom.Template, RuntimeSpec(atom));

    public static bool IsPermanentStrengthOrDexterityChange(GeneratorOperation operation) =>
        IsPermanentStrengthOrDexterityChange(operation.Template,
            OperationRuntimeSpecCompiler.GetOrCompile(operation));

    private static bool IsPermanentStrengthOrDexterityChange(string template, OperationRuntimeSpec spec) =>
        template is "N:Dex" or "N:LoseDex" or "D:GainStrength" or "D:GainDexterity"
            or "NCR:LoseStrength" or "R:GainStrength" or "R:EnemiesLoseStrength"
            or "NCR:TargetLoseStrength" or "N:StrengthPerTargetVulnerable" or "T:XStrengthLoss"
        || spec.Opcode == "modify_block" && spec.Variant == "strength_scaled"
        || spec.Opcode == "apply_power" && spec.Variant is "strength" or "strength_gain" or "strength_loss";

    public static bool IsPositivePermanentStatGain(ComponentAtom atom) =>
        IsPositivePermanentStatGain(atom.Template, RuntimeSpec(atom));

    public static bool IsPositivePermanentStatGain(GeneratorOperation operation) =>
        IsPositivePermanentStatGain(operation.Template, OperationRuntimeSpecCompiler.GetOrCompile(operation));

    /// <summary>Effects that permanently add the Replay keyword to another card.</summary>
    public static bool IsReplayGrant(ComponentAtom atom) => IsReplayGrant(atom.Template);

    public static bool IsReplayGrant(GeneratorOperation operation) => IsReplayGrant(operation.Template);

    private static bool IsReplayGrant(string template) => template is
        "I:ProxyAtomic_Transfigure" or "A:ProxyAtomic_SwordSage" or "CL:ProxyAtomic_HiddenGem";

    private static bool IsPositivePermanentStatGain(string template, OperationRuntimeSpec spec) =>
        template is "N:Dex" or "N:StrengthPerTargetVulnerable"
            or "D:GainStrength" or "D:GainDexterity" or "D:GainFocus" or "R:GainStrength"
        || spec.Opcode == "apply_power" && spec.Variant == "strength";

    /// <summary>
    /// Immediate combat state that stacks for the remainder of combat. Replaying a Skill with one of these lines
    /// is materially stronger than playing the equivalent Power once, so numeric generation must know the shell.
    /// </summary>
    public static bool IsStackablePersistentCombatGain(ComponentAtom atom) =>
        IsStackablePersistentCombatGain(atom.Template, RuntimeSpec(atom));

    public static bool IsStackablePersistentCombatGain(GeneratorOperation operation) =>
        IsStackablePersistentCombatGain(operation.Template, OperationRuntimeSpecCompiler.GetOrCompile(operation));

    public static bool IsPlating(ComponentAtom atom) =>
        RuntimeSpec(atom) is { Opcode: "apply_power", Variant: "plating" };

    public static bool IsPlating(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation) is { Opcode: "apply_power", Variant: "plating" };

    private static bool IsStackablePersistentCombatGain(string template, OperationRuntimeSpec spec) =>
        IsPositivePermanentStatGain(template, spec)
        || template is "D:GainOrbSlots" or "N:Thorns"
        || spec.Opcode == "apply_power" && spec.Variant == "plating";

    private static bool IsEnemyStrengthReduction(string template, OperationRuntimeSpec spec) =>
        template is "N:AllTempStrengthLoss" or "CL:TargetLoseStrengthThisTurn"
            or "R:EnemiesLoseStrengthThisTurn" or "R:TargetLoseStrengthThisTurn" or "R:EnemiesLoseStrength"
            or "NCR:TargetLoseStrength" or "NCR:TargetLoseStrengthThisTurn" or "T:XStrengthLoss"
        || spec.Opcode == "apply_power" && spec.Target != "self"
            && spec.Variant is "strength_loss" or "strength_loss_this_turn";

    /// <summary>
    /// Stable semantic terms used by card hover tips. UI code consumes these ASCII IDs instead of searching the
    /// localized description, so changing Chinese or English wording cannot add or remove gameplay explanations.
    /// The template sets deliberately preserve a few historical broad matches (for example cost-to-zero text also
    /// exposed the Transform tip) until a separately approved UI behavior cleanup.
    /// </summary>
    public static IReadOnlySet<string> HoverSemanticTags(GeneratorOperation operation)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var template = operation.Template;
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        if (template is "A:whenEtherealDrawn" or "A:whenEtherealPlayed" or "I:ProxyAtomic_Eidolon"
            or "NCR:AddRandomEtherealCardToHand" or "NCR:AddVoidToSelectedHandCard"
            or "NCR:ForEachEtherealPlayedCombat" or "NCR:NextVoidCostsZero") result.Add("ethereal");
        if (spec.Opcode == "exhaust_card"
            || spec.Trigger?.Kind is "card_exhausted" or "for_each_exhausted_card"
                or "for_each_exhausted_non_attack" or "self_exhausted" or "turn_end_if_self_in_exhaust"
            || spec.Condition?.Kind is "card_exhausted_this_turn" or "exhaust_pile_minimum"
            || template is "CL:ExhaustUpToHandCards" or "D:ExhaustAllStatuses" or "D:ExhaustSelectedHandCard"
            or "D:ForEachExhaustedStatus" or "D:ShuffleAllUnexhaustedIntoDraw" or "I:ExhaustRandomAttack"
            or "I:PlayExhaustedShivsAtTarget" or "I:PlayTopCardAndExhaust" or "I:ProxyAtomic_Eidolon"
            or "M:DamagePerExhaustCard" or "NCR:ExhaustSelectedDrawCard" or "NCR:ForEachExhaustedSoul"
            or "R:AtTurnStartIfInExhaust") result.Add("exhaust");
        if (template is "A:ruleShivsRetainAndFirstBonus" or "A:ruleShivsRetain"
            or "CL:RetainHandThisTurn" or "I:DrawWithRetain"
            or "NCR:AddRetainToSelectedHandCard" or "R:RetainHandThisTurn") result.Add("retain");
        if (template is "A:rulePlayedSkillsGainSly" or "I:GrantSlyToHandSkillThisTurn") result.Add("sly");
        if (spec.Variant is "vulnerable" or "vulnerable_double" or "strength_per_target_vulnerable"
            or "vulnerable_scaled" or "vulnerable_applied" or "vulnerable_enemy_damage_bonus"
            or "vulnerable_enemy_damage_reduction"
            || spec.Trigger?.Kind is "vulnerable_applied" or "vulnerable_enemy_damage_reduction"
            || spec.Condition?.Kind == "target_has_vulnerable"
            || template is "CL:ApplyVulnerableAll" or "I:ApplyToAllEnemies" or "NCR:ApplyVulnerableAll"
                or "NCR:DoubleVulnerableWeak" or "R:ApplyVulnerableAll") result.Add("vulnerable");
        if (spec.Variant == "weak" || template is "A:ruleWeakEnemiesTakeMoreAttackDamage" or "CL:ApplyWeakAll"
            or "N:AllWeak" or "NCR:ApplyWeakAll" or "NCR:DoubleVulnerableWeak" or "R:ApplyWeakAll"
            or "T:XWeak") result.Add("weak");
        if (spec.Variant == "poison" || template is "A:rulePoisonExtraTriggers"
            or "A:ruleUnblockedAttackPoison" or "C:ifTargetPoisoned" or "I:TriggerPoisonNow"
            or "N:AllPoison" or "N:BlockEqualAllPoison" or "N:RandomPoison" or "T:Poison") result.Add("poison");
        if (spec.Variant.Contains("strength", StringComparison.Ordinal)
            || template is "CL:TargetLoseStrengthThisTurn" or "D:GainStrength" or "I:GainTemporaryStrength"
                or "N:AllTempStrengthLoss" or "NCR:LoseStrength" or "NCR:TargetLoseStrength"
                or "NCR:TargetLoseStrengthThisTurn" or "R:EnemiesLoseStrength" or "R:EnemiesLoseStrengthThisTurn"
                or "R:GainStrength" or "R:GainStrengthThisTurn" or "R:TargetLoseStrengthThisTurn"
                or "T:XStrengthLoss") result.Add("strength");
        if (template is "D:GainDexterity" or "N:Dex" or "N:LoseDex" or "N:TempDex") result.Add("dexterity");
        if (template == "N:Thorns") result.Add("thorns");
        if (template == "N:Intangible") result.Add("intangible");
        if (spec.Opcode == "apply_power" && spec.Variant == "plating") result.Add("plating");
        if (template is "D:GainFocus" or "D:GainTemporaryFocus" or "D:LoseFocus" or "D:LoseTemporaryFocus")
            result.Add("focus");
        if (template is "CL:GainVigor" or "R:GainVigor") result.Add("vigor");
        if (spec.Condition?.Kind == "fatal") result.Add("fatal");
        if (template is "A:ProxyAtomic_SwordSage" or "CL:ProxyAtomic_HiddenGem"
            or "I:ProxyAtomic_Transfigure" or "D:ReplayEventCard") result.Add("replay");
        if (template is "CL:TransformSelectedHandCards" or "D:TransformStatusesToFuel" or "I:ProxyAtomic_Begone"
            or "I:ProxyAtomic_Charge" or "I:ProxyAtomic_Guards" or "I:ProxyAtomic_Seance" or "I:Transform"
            or "D:NextPowerCostsZero" or "I:NextSkillCostsZero" or "I:SetCostZero"
            or "NCR:NextVoidCostsZero" or "NCR:SetCostZeroIfOstyAttacked") result.Add("transform");
        if (template == "A:rule" && spec.Variant == "skills_cost_zero") result.Add("transform");
        if (template is "NCR:ApplyPower_SicEmPower" or "NCR:Summon" or "NCR:SummonX") result.Add("summon");
        if (template == "I:AddCardReward") result.Add("card_reward");
        if (template is "R:Forge" or "R:ForgePerPriorHit") result.Add("forge");
        if (template.StartsWith("D:Channel", StringComparison.Ordinal)
            || template is "I:ProxyAtomic_Tempest" or "I:ProxyAtomic_Voltaic") result.Add("channel");
        if (template.Contains("Evoke", StringComparison.Ordinal) || template == "I:ProxyAtomic_MultiCast")
            result.Add("evoke");
        return result;
    }

    /// <summary>
    /// These numbers classify cards or define trigger thresholds; they are not scalable reward values.
    /// Explicit Energy/Star payment-threshold reductions are handled separately by the upgrade generator.
    /// </summary>
    public static bool IsNonUpgradeableNumericMarker(GeneratorOperation operation) =>
        operation.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger
        || IsDependencyPrefix(operation)
        || operation.Template is "D:CreateZeroCostCopyInDiscard"
            or "D:ReturnZeroCostDiscardToHand"
            or "R:ReturnAfterSkillsPlayed"
            or "R:DoubleEnergyX"
            or "R:DoubleEitherXAtThreshold"
            or "I:ReplayAttack"
            or "NCR:IncreaseAllCardCostsThisTurn";

    public static bool RequiresDependencyPrefix(ComponentAtom atom) =>
        DependencyOnlyPayoffs.Contains(atom.Template) || IsConditionalDamageVariant(atom);
    public static bool IsLegalDependencyPayoff(GeneratorOperation prefix, ComponentAtom payoff) =>
        IsExplicitDependencyPayoff(prefix.Template, payoff.Template)
        || GenericDependencyPrefixes.Contains(prefix.Template) && IsGenericDependencyPayoff(prefix, payoff);

    public static bool IsLegalDependencyPayoff(GeneratorOperation prefix, GeneratorOperation payoff) =>
        IsExplicitDependencyPayoff(prefix.Template, payoff.Template)
        || GenericDependencyPrefixes.Contains(prefix.Template) && IsGenericDependencyPayoff(prefix, payoff);

    private static bool IsExplicitDependencyPayoff(string prefixTemplate, string payoffTemplate) =>
        DependencyPayoffs.TryGetValue(prefixTemplate, out var templates)
        && templates.Contains(payoffTemplate, StringComparer.Ordinal);

    private static bool IsGenericDependencyPayoff(GeneratorOperation prefix, ComponentAtom payoff)
    {
        if (payoff.Scope is not (OperationScope.SingleEnemyOnly or OperationScope.NonTargeted
            or OperationScope.Independent or OperationScope.Modifier)) return false;
        if (DependencyOnlyPayoffs.Contains(payoff.Template) || IsConditionalDamageVariant(payoff)
            || IsSelfManagedStateEffect(payoff)) return false;
        if (payoff.Scope == OperationScope.Modifier && !IsRepeatableDependencyModifier(payoff)) return false;
        if (!IsMultiplicativeDependencyPrefix(prefix)) return true;
        var spec = RuntimeSpec(payoff);
        return UsesX(payoff) || spec.Values.Any(value => value.Source is "fixed" or "energy_x" or "star_x" or "special_x");
    }

    private static bool IsGenericDependencyPayoff(GeneratorOperation prefix, GeneratorOperation payoff)
    {
        if (payoff.Scope is not (OperationScope.SingleEnemyOnly or OperationScope.NonTargeted
            or OperationScope.Independent or OperationScope.Modifier)) return false;
        if (DependencyOnlyPayoffs.Contains(payoff.Template) || IsConditionalDamageVariant(payoff)
            || IsSelfManagedStateEffect(payoff)) return false;
        if (payoff.Scope == OperationScope.Modifier && !IsRepeatableDependencyModifier(payoff)) return false;
        if (!IsMultiplicativeDependencyPrefix(prefix)) return true;
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(payoff);
        return UsesX(payoff) || spec.Values.Any(value => value.Source is "fixed" or "energy_x" or "star_x" or "special_x");
    }

    /// <summary>
    /// A count prefix may repeat an additive modifier when a compatible Damage/Block anchor already exists.
    /// Replacement modifiers, one-off state transitions and percentage rules have no stable additive meaning.
    /// </summary>
    public static bool IsRepeatableDependencyModifier(ComponentAtom atom) =>
        atom.Scope == OperationScope.Modifier
        && IsRepeatableDependencyModifier(atom.Template, RuntimeSpec(atom));

    public static bool IsRepeatableDependencyModifier(GeneratorOperation operation) =>
        operation.Scope == OperationScope.Modifier
        && IsRepeatableDependencyModifier(operation.Template,
            OperationRuntimeSpecCompiler.GetOrCompile(operation));

    private static bool IsRepeatableDependencyModifier(string template, OperationRuntimeSpec spec)
    {
        if (template is "M:RepeatAreaOnKill" or "CL:IncreaseRollingDamage"
            or "R:DoubleEnergyX" or "R:DoubleEitherXAtThreshold"
            or "M:TriggeredAttackDamagePercent") return false;
        if (spec.Opcode == "modify_hits") return true;
        if (spec.Opcode == "modify_block") return spec.Variant == "strength_scaled";
        if (spec.Opcode == "modify_damage") return spec.Variant != "current_block"
            && spec.Variant != "triggered_attack_percentage";
        return template is "M:DamagePerExhaustCard" or "M:DamagePerDiscardThisTurn"
            or "M:DamagePerCardDrawnCombat" or "NCR:DamagePerCardDrawnThisTurn"
            or "NCR:DamagePerExhaustedSoul" or "NCR:DamagePerOstyAttackCard"
            or "R:BonusPerStarCostCardInHand" or "R:BonusPerGeneratedCardThisCombat"
            or "CL:BonusPerUniqueDebuff";
    }

    /// <summary>
    /// A Power needs at least one trigger/rule that persists into later turns, direct Plating/permanent Strength, or a
    /// restricted Power-or-Exhaust effect. Turn-local conditional triggers do not establish a Power foundation.
    /// </summary>
    public static bool IsPersistentPowerFoundation(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags
            .Contains(ComponentSemanticFlags.PowerFoundation)
        || operation.Scope is OperationScope.AbilityTrigger or OperationScope.AbilityRule
        || IsPersistentStat(operation.Template, OperationRuntimeSpecCompiler.GetOrCompile(operation))
        || IsRestrictedEffect(operation)
        || IsCopyThisCardToDiscard(operation);

    public static bool IsPersistentPowerFoundation(ComponentAtom atom) =>
        RuntimeSpec(atom).Flags.Contains(ComponentSemanticFlags.PowerFoundation)
        || atom.Scope is OperationScope.AbilityTrigger or OperationScope.AbilityRule
        || IsPersistentStat(atom.Template, RuntimeSpec(atom))
        || IsRestrictedEffect(atom)
        || IsCopyThisCardToDiscard(atom);

    private static bool IsPersistentStat(string template, OperationRuntimeSpec spec) =>
        template is "N:Dex" or "N:Thorns" or "N:Intangible"
        || template is "D:GainStrength" or "D:GainDexterity" or "D:GainFocus" or "D:GainOrbSlots"
        || template == "R:KingsSwordHitsAllEnemies"
        || template == "N:Self" && spec.Variant is "plating" or "strength";
}

public enum XResourceRequirement { None, Energy, Star, Either }

public enum CopyThisCardBudgetRole
{
    None,
    PaidReusableDownside,
    ExhaustOffset,
    FreeReusableBenefit,
    PowerBenefit
}

public sealed record IroncladCardRecipe(
    string Id,
    string ChineseTitle,
    int Cost,
    GeneratedCardType Type,
    TargetMode Target,
    GeneratedRarity OriginalRarity,
    IReadOnlyList<CardTag> Tags,
    IReadOnlyList<ComponentAtom> Atoms,
    IReadOnlyList<int> TriggerOwners,
    int StarCost = -1,
    bool HasStarCostX = false,
    string EnglishTitle = "",
    IReadOnlyList<string>? CustomKeywords = null);

public sealed record ComponentAtom(
    string Template,
    OperationScope Scope,
    string ChineseText,
    bool RequiresSingleTarget,
    CardReferenceRequirement CardReference)
{
    public string? SemanticId { get; init; }
    public OperationRuntimeSpec? RuntimeSpec { get; init; }
    public OperationLocalizedText? LocalizedText { get; init; }
    public string Key => OperationRuntimeSpecCompiler.StructuralExactKey(this);
    public string FamilyKey => NumericTextSchema.Family(Template);
    public string SchemaKey => OperationRuntimeSpecCompiler.StructuralFieldKey(this);
    public ComponentMultiplicity Multiplicity => ComponentPolicy.Multiplicity(this);
}

public interface IComponentCatalog
{
    GeneratedCharacter Character { get; }
    IReadOnlyList<IroncladCardRecipe> Recipes { get; }
    IReadOnlyList<ComponentAtom> Atoms { get; }
    IReadOnlyList<int> ComponentCounts { get; }
    IReadOnlySet<string> AtomKeys { get; }
    IReadOnlyDictionary<string, int> AtomCounts { get; }
    IReadOnlyDictionary<int, int> ComponentCountCounts { get; }
    IReadOnlyDictionary<CardTag, int> TagCounts { get; }
    IReadOnlyDictionary<string, int> CustomKeywordCounts => new Dictionary<string, int>();
}

/// <summary>Registers reviewed character catalogs after ModelDb becomes available in the game layer.</summary>
public static class ExternalOperationUpgradeRegistry
{
    private static readonly Dictionary<(GeneratedCharacter Character, string Template),
        (IReadOnlyList<CardTag> Added, IReadOnlyList<CardTag> Removed)> Values = new();
    private static readonly Dictionary<(string ProfileId, string Template),
        (IReadOnlyList<CardTag> Added, IReadOnlyList<CardTag> Removed)> ProfileValues = new();

    public static void Register(GeneratedCharacter character, string template,
        IReadOnlyList<CardTag> added, IReadOnlyList<CardTag> removed)
    {
        var key = (character, template);
        if (Values.TryGetValue(key, out var current))
        {
            added = current.Added.Concat(added).Distinct().ToArray();
            removed = current.Removed.Concat(removed).Distinct().ToArray();
        }
        Values[key] = (added, removed);
    }

    public static void Register(string profileId, string template,
        IReadOnlyList<CardTag> added, IReadOnlyList<CardTag> removed)
    {
        if (string.IsNullOrWhiteSpace(profileId) || profileId.Any(character => character > 0x7f))
            throw new ArgumentException("Profile IDs must be non-empty ASCII strings.", nameof(profileId));
        var key = (profileId, template);
        if (ProfileValues.TryGetValue(key, out var current))
        {
            added = current.Added.Concat(added).Distinct().ToArray();
            removed = current.Removed.Concat(removed).Distinct().ToArray();
        }
        ProfileValues[key] = (added, removed);
    }

    public static bool TryGet(string profileId, string template,
        out (IReadOnlyList<CardTag> Added, IReadOnlyList<CardTag> Removed) value) =>
        ProfileValues.TryGetValue((profileId, template), out value);

    public static bool TryGet(GeneratedCharacter character, string template,
        out (IReadOnlyList<CardTag> Added, IReadOnlyList<CardTag> Removed) value) =>
        Values.TryGetValue((character, template), out value);

    public static bool TryGetUnified(string template,
        out (IReadOnlyList<CardTag> Added, IReadOnlyList<CardTag> Removed) value)
    {
        var matches = Values.Where(entry => entry.Key.Template == template).Select(entry => entry.Value)
            .Concat(ProfileValues.Where(entry => entry.Key.Template == template).Select(entry => entry.Value))
            .ToArray();
        if (matches.Length == 0)
        {
            value = default;
            return false;
        }
        value = (
            matches.SelectMany(match => match.Added).Distinct().ToArray(),
            matches.SelectMany(match => match.Removed).Distinct().ToArray());
        return true;
    }
}

/// <summary>Upgrade routes for external keyword IDs; kept separate from the legacy CardTag wire enum.</summary>
public static class ExternalCustomKeywordUpgradeRegistry
{
    private sealed record Route(IReadOnlyList<string> Added, IReadOnlyList<string> Removed);
    private static readonly Dictionary<(string ProfileId, string Template), Route> Values = new();

    public static void Register(string profileId, string template, IReadOnlyList<string> added,
        IReadOnlyList<string> removed)
    {
        if (string.IsNullOrWhiteSpace(profileId) || profileId.Any(character => character > 0x7f))
            throw new ArgumentException("Profile IDs must be non-empty ASCII strings.", nameof(profileId));
        ComponentKeywordApi.ValidateIds(added, nameof(added));
        ComponentKeywordApi.ValidateIds(removed, nameof(removed));
        var key = (profileId, template);
        if (Values.TryGetValue(key, out var current))
        {
            added = current.Added.Concat(added).Distinct(StringComparer.Ordinal).ToArray();
            removed = current.Removed.Concat(removed).Distinct(StringComparer.Ordinal).ToArray();
        }
        Values[key] = new Route(added, removed);
    }

    public static bool TryGet(string profileId, string template,
        out (IReadOnlyList<string> Added, IReadOnlyList<string> Removed) value)
    {
        if (Values.TryGetValue((profileId, template), out var route))
        {
            value = (route.Added, route.Removed);
            return true;
        }
        value = default;
        return false;
    }

    public static bool TryGetUnified(string template,
        out (IReadOnlyList<string> Added, IReadOnlyList<string> Removed) value)
    {
        var matches = Values.Where(entry => entry.Key.Template == template).Select(entry => entry.Value).ToArray();
        if (matches.Length == 0)
        {
            value = default;
            return false;
        }
        value = (matches.SelectMany(match => match.Added).Distinct(StringComparer.Ordinal).ToArray(),
            matches.SelectMany(match => match.Removed).Distinct(StringComparer.Ordinal).ToArray());
        return true;
    }
}

public enum CardReferenceRequirement { None, ThisCard, HandCard, HandAttack }

/// <summary>Builds executable named card slots for operations that reference another card.</summary>
internal sealed class CardSlotContext
{
    private CardReferenceRequirement? _selectedKind;
    private string? _selectedSlot;
    private int _nextSlot;

    public bool CanResolve(CardReferenceRequirement requirement)
    {
        if (requirement is CardReferenceRequirement.None or CardReferenceRequirement.ThisCard) return true;
        if (_selectedKind is null) return true;
        // An Attack can satisfy an any-card hand slot; the inverse is not true.
        return _selectedKind == requirement || _selectedKind == CardReferenceRequirement.HandAttack && requirement == CardReferenceRequirement.HandCard;
    }

    public string? Resolve(CardReferenceRequirement requirement, List<GeneratorOperation> operations)
    {
        if (requirement == CardReferenceRequirement.None) return null;
        if (requirement == CardReferenceRequirement.ThisCard) return "thisCard";
        if (!CanResolve(requirement))
            throw new InvalidOperationException("一张生成卡不能要求多个不兼容的卡牌槽。");
        if (_selectedSlot is not null) return _selectedSlot;

        var slot = $"card{++_nextSlot}";
        var attackOnly = requirement == CardReferenceRequirement.HandAttack;
        operations.Add(new GeneratorOperation(
            attackOnly ? "N_SELECT_HAND_ATTACK" : "N_SELECT_HAND_CARD",
            OperationScope.NonTargeted,
            attackOnly ? "选择手牌中的一张攻击牌。" : "选择手牌中的一张牌。",
            new Dictionary<string, int> { ["slotIndex"] = _nextSlot },
            RuntimeSpec: OperationRuntimeSpecCompiler.GeneratedHandSelection(attackOnly)));
        _selectedKind = requirement;
        _selectedSlot = slot;
        return slot;
    }
}
