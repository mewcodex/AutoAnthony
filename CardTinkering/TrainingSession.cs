using AutoAnthony;
using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using NativeCardPile = MegaCrit.Sts2.Core.Entities.Cards.CardPile;
using NativePileType = MegaCrit.Sts2.Core.Entities.Cards.PileType;
using static AutoAnthonyCardTinkering.TinkeringText;

namespace AutoAnthonyCardTinkering;

internal sealed class TrainingComponent
{
    internal required string Id { get; init; }
    internal required GeneratorOperation Operation { get; set; }
    internal required List<CardUpgradeEffect> Upgrades { get; init; }
    internal required ComponentProgress Progress { get; set; }
    internal int CardIndex { get; set; }
    internal string? ParentId { get; set; }
    internal int Order { get; set; }
}

internal sealed class TrainingCard
{
    internal required CardModel SourceCard { get; init; }
    internal required ChaosCardModel LiveCard { get; init; }
    internal required GeneratedCard Shell { get; set; }
    internal required string OriginalPortraitPath { get; init; }
    internal required AutoAnthonyEditorIdentity? OriginalIdentity { get; init; }
    internal AutoAnthonyEditorIdentity? DraftIdentity { get; set; }
    internal bool IdentityChanged { get; set; }
    internal required string OriginalDefinitionPayload { get; init; }
    internal required int Capacity { get; init; }
    internal required double CapacityUpperBound { get; init; }
    internal required bool Eternal { get; init; }
    internal required bool NumericRandom { get; init; }
    internal required IReadOnlySet<string> EntryValidationErrors { get; init; }
    internal int? CapacityX0 { get; init; }
    internal int? CapacityX3 { get; init; }
    internal double? CapacityUpperBoundX0 { get; init; }
    internal double? CapacityUpperBoundX3 { get; init; }
    internal bool IsNativeSource => SourceCard is not ChaosCardModel;
    internal bool UsesFreeformHost => IsNativeSource || LiveCard.HasFreeformDefinition;
}

internal sealed record CardBudgetLine(string Label, double OccupiedValue, double Capacity, int ShellCapacity);
internal sealed record CardEffectiveCost(double? Fixed, double? X0, double? X3);
internal sealed record CardAnalysisLine(string Label, CardTinkeringCardAnalysis Analysis, int ShellCapacity);
internal sealed record CardAnalysisSnapshot(CardTinkeringCardAnalysis Analysis,
    IReadOnlyList<CardAnalysisLine> Lines);
internal sealed record ComponentAnalysisLine(string Label, CardTinkeringComponentAnalysis Analysis);
internal sealed record KeywordAnalysisLine(string Label, double PositiveValue, double DownsideMultiplier);
internal sealed record TriggerAnalysisLine(string Label, double Own, double Nested, bool HasAncestor);

internal sealed class TrainingSession
{
    private sealed record ComponentSnapshot(string Id, GeneratorOperation Operation,
        IReadOnlyList<CardUpgradeEffect> Upgrades, ComponentProgress Progress,
        int CardIndex, string? ParentId, int Order);
    private sealed record CardIdentitySnapshot(int CardIndex, GeneratedCardName? Name,
        AutoAnthonyEditorIdentity? Identity, bool Changed);
    private sealed record SessionSnapshot(IReadOnlyList<ComponentSnapshot> Components,
        IReadOnlyList<CardIdentitySnapshot> Identities);

    private readonly RunState _run;
    private readonly Player _owner;
    private readonly int _actIndex;
    private readonly bool _completesTrainingAct;
    private readonly List<TrainingCard> _cards = [];
    private readonly Dictionary<string, TrainingComponent> _components = new(StringComparer.Ordinal);
    private readonly Dictionary<int, CardAnalysisSnapshot> _analysisCache = [];
    private SessionSnapshot _entrance = new([], []);
    private SessionSnapshot? _checkpoint;
    private int _nextId;

    internal TrainingSession(RunState run, Player owner, int actIndex, bool completesTrainingAct = true)
    {
        _run = run;
        _owner = owner;
        _actIndex = actIndex;
        _completesTrainingAct = completesTrainingAct;
        var nativeCandidates = 0;
        var nativeLoaded = 0;
        foreach (var sourceCard in owner.Deck.Cards)
        {
            if (sourceCard is not ChaosCardModel) nativeCandidates++;
            ChaosCardModel card;
            if (sourceCard is ChaosCardModel generated)
                card = generated;
#if FREEFORM_API
            else if (CardTinkeringFeatureGate.SupportsNativeCardApi
                     && AutoAnthonyNativeCardApi.TryCreateBasePreview(
                         owner, sourceCard, out var nativePreview))
            {
                card = nativePreview;
                nativeLoaded++;
            }
#endif
            else
                continue;

            var state = TinkeringStateStore.Ensure(card);
            var shell = TinkeringStateStore.NormalizeShellUpgradeOwnership(card.Generated,
                sourceCard is ChaosCardModel ? card.Definition.Card : card.Generated);
            var referencePricing = TinkeringValue.Pricing(shell);
            ProductionCardPricing? referenceX0 = null;
            ProductionCardPricing? referenceX3 = null;
            if (TinkeringValue.TryXEndpointPricing(shell, out var shellX0, out var shellX3))
            {
                referenceX0 = shellX0;
                referenceX3 = shellX3;
            }
            var cardIndex = _cards.Count;
            _cards.Add(new TrainingCard
            {
                SourceCard = sourceCard,
                LiveCard = card,
                Shell = shell,
                OriginalPortraitPath = sourceCard is ChaosCardModel sourceGenerated
                    ? sourceGenerated.HasFreeformDefinition
                        ? sourceGenerated.FreeformPortraitPath
                        : sourceGenerated.PortraitPath
                    : sourceCard.PortraitPath,
                OriginalIdentity = card.EditorIdentity,
                DraftIdentity = card.EditorIdentity,
                IdentityChanged = false,
                OriginalDefinitionPayload = CardTinkeringApi.SerializeCard(shell),
                Capacity = state.Capacity,
                CapacityUpperBound = referencePricing.OrdinaryUpperBound,
                Eternal = state.Eternal,
                NumericRandom = TinkeringStateStore.UsesNumericRandomCapacity(shell)
                                || TinkeringStateStore.IsNumericRandomCapacityModel(state.CapacityModel),
                // The base generator's final acceptance contract is authoritative for untouched cards. Preserve
                // only errors already present on that exact core-generated definition; new errors introduced by a
                // rearrangement are still rejected. Previously an API-only invariant could disable an unedited
                // base card as soon as Card Tinkering was installed.
                EntryValidationErrors = sourceCard is not ChaosCardModel || !card.HasTinkeredDefinition
                    ? ValidateAssembly(shell, shell.Operations).Errors.ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal),
                CapacityX0 = state.CapacityX0,
                CapacityX3 = state.CapacityX3,
                CapacityUpperBoundX0 = referenceX0?.OrdinaryUpperBound,
                CapacityUpperBoundX3 = referenceX3?.OrdinaryUpperBound
            });
            ImportCard(cardIndex, shell, state.Components);
        }

        var runState = TinkeringStateStore.GetPlayerRun(run, owner);
        var backpackOperations = runState.Backpack.Select(component => component.Operation).ToArray();
        var backpackAttackSlots = backpackOperations.Where(operation => operation.Template == "N_SELECT_HAND_ATTACK")
            .Select(SelectorSlot).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var backpackOrdinarySlots = backpackOperations.Where(operation => operation.Template == "N_SELECT_HAND_CARD")
            .Select(SelectorSlot).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var backpackIds = new List<string>();
        foreach (var stored in runState.Backpack)
        {
            var parentId = stored.Operation.Parameters.TryGetValue("triggerIndex", out var parentIndex)
                           && parentIndex >= 0 && parentIndex < backpackIds.Count ? backpackIds[parentIndex] : null;
            var progress = stored.Progress;
            if (CardEffectRules.NeedsExternalCardSlot(stored.Operation)
                && stored.Operation.CardTargetSlot is { Length: > 0 } slot
                && slot != "thisCard" && progress.RequiresAttackCardSlot is null)
                progress = progress with
                {
                    RequiresAttackCardSlot = backpackAttackSlots.Contains(slot)
                        ? true
                        : backpackOrdinarySlots.Contains(slot) ? false : null
                };
            backpackIds.Add(AddComponent(stored.Operation, stored.Upgrades, progress, -1, parentId).Id);
        }
        ApplyImplicitDependencyContainment(backpackOperations, backpackIds);
        NormalizeImplicitSelectors();
        NormalizeOrders();
        _entrance = Capture();
        Log.Info($"[CardTinkering] Training session loaded {_cards.Count} editable card(s), "
                 + $"including {nativeLoaded}/{nativeCandidates} native deck card(s).");
    }

    internal IReadOnlyList<TrainingCard> Cards => _cards;
    internal IReadOnlyCollection<TrainingComponent> Components => _components.Values;
    internal IEnumerable<TrainingComponent> Roots(int cardIndex, string? parentId = null) =>
        _components.Values.Where(component => component.CardIndex == cardIndex && component.ParentId == parentId
                                      && !IsImplicitSelector(component.Operation))
            .OrderBy(component => component.Order);
    private IEnumerable<TrainingComponent> AllRoots(int cardIndex, string? parentId = null) =>
        _components.Values.Where(component => component.CardIndex == cardIndex && component.ParentId == parentId)
            .OrderBy(component => component.Order);
    internal IEnumerable<TrainingComponent> VisibleComponents(int cardIndex) =>
        _components.Values.Where(component => component.CardIndex == cardIndex
                                              && !IsImplicitSelector(component.Operation));
    internal TrainingComponent Get(string id) => _components[id];
    internal bool HasCheckpoint => _checkpoint is not null;
    internal static bool IsPermanentDamageGrowth(GeneratorOperation operation) =>
        operation.Template == "NCR:IncreaseThisCardDamageRun";
    internal static bool IsPermanentBlockGrowth(GeneratorOperation operation) =>
        operation.Template == "D:IncreaseThisCardBlockRun";
    internal static bool IsPermanentGrowth(GeneratorOperation operation) =>
        IsPermanentDamageGrowth(operation) || IsPermanentBlockGrowth(operation);
    internal static bool IsSingleUse(GeneratorOperation operation) =>
        CardEffectRules.IsRestrictedEffect(operation);
    internal static bool IsWholeCardUnique(GeneratorOperation operation) =>
        ComponentPolicy.Multiplicity(operation) != ComponentMultiplicity.Repeatable;
    internal static bool CanHaveChildren(GeneratorOperation operation) =>
        CardEffectRules.TriggerNeedsLinkedEffect(operation)
        || CardEffectRules.IsDependencyPrefix(operation);
    private static bool IsImplicitSelector(GeneratorOperation operation) => operation.Template is
        "N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK";

    internal GeneratedCard Preview(int cardIndex)
    {
        var flattened = Flatten(cardIndex);
        var shell = _cards[cardIndex].Shell;
        var operations = flattened.Select(item => item.Operation).ToArray();
        var retainedShellEffects = shell.Upgrade?.Effects
            .Where(effect => effect.OperationIndex is null).ToArray() ?? [];
        var componentEffects = flattened.SelectMany((item, index) => item.Component.Upgrades.Select(effect =>
            effect with { OperationIndex = index })).ToArray();
        var effects = retainedShellEffects.Concat(componentEffects).ToArray();
        CardUpgradePlan? upgrade = null;
        if (shell.Upgrade is { } originalUpgrade)
        {
            var upgradedOperations = CardUpgradeGenerator.ApplyEffectsToOperations(operations, effects);
            upgrade = originalUpgrade with
            {
                Effects = effects,
                UpgradedChineseDescription = CardDescriptionRenderer.Render(upgradedOperations),
                UpgradedEnglishDescription = EnglishCardDescriptionRenderer.Render(upgradedOperations)
            };
        }
        return OperationRuntimeSpecCompiler.Attach(shell with
        {
            Operations = operations,
            ChineseDescription = CardDescriptionRenderer.Render(operations),
            EnglishDescription = EnglishCardDescriptionRenderer.Render(operations),
            Upgrade = upgrade
        });
    }

    internal ChaosCardModel PreviewModel(int cardIndex, bool showUpgrade = false)
    {
        var preview = (ChaosCardModel)_cards[cardIndex].LiveCard.ClonePreservingMutability();
        TinkeringStateStore.MarkEditorPreview(preview);
        var flattened = Flatten(cardIndex);
        var progress = flattened.Select(item => TinkeringStateStore.WithComponentMetadata(
            item.Operation, item.Component.Upgrades, item.Component.Progress)).ToArray();
        var card = _cards[cardIndex];
        var definition = TinkeringStateStore.MaterializeComponentUpgradesForPreview(
            Preview(cardIndex), progress, preview.IsUpgraded);
        if (card.UsesFreeformHost && card.IdentityChanged)
        {
            preview.ApplyFreeformDefinition(definition);
            preview.FreeformPortraitPath = DraftPortraitPath(card);
        }
        else if (card.IdentityChanged && card.DraftIdentity is { } identity)
            AutoAnthonyEditorApi.ApplyEditorDefinition(preview, definition, identity);
        else
            preview.ApplyTinkeredDefinition(definition);
        preview.ExtraDamage = progress.Sum(item => item.PermanentDamage);
        preview.ExtraBlock = progress.Sum(item => item.PermanentBlock);
        var invalid = !CardIsLegal(cardIndex, Value(cardIndex), out _);
        var budgets = BudgetAmounts(cardIndex);
        var currentCapacity = budgets[^1].ShellCapacity;
        int? currentCapacityX0 = budgets.Count > 1 ? budgets[0].ShellCapacity : null;
        int? currentCapacityX3 = budgets.Count > 1 ? budgets[1].ShellCapacity : null;
        TinkeringStateStore.Set(preview, new TinkeredCardState(
            currentCapacity, card.Eternal, progress,
            CapacityModel: currentCapacityX0 is not null && currentCapacityX3 is not null
                ? card.NumericRandom
                    ? TinkeringStateStore.NumericRandomXEndpointCapacityModel
                    : TinkeringStateStore.XEndpointCapacityModel
                : card.NumericRandom
                    ? TinkeringStateStore.NumericRandomNetCapacityModel
                    : TinkeringStateStore.NetValueCapacityModel,
            Invalid: invalid, CapacityX0: currentCapacityX0, CapacityX3: currentCapacityX3));
        if (showUpgrade && !preview.IsUpgraded && preview.IsUpgradable)
        {
            preview.UpgradeInternal();
        }
        return preview;
    }

    internal bool TryRerollIdentity(int cardIndex, out string reason)
    {
        if ((uint)cardIndex >= (uint)_cards.Count)
        {
            reason = Localize("找不到当前卡牌。", "The current card could not be found.");
            return false;
        }
        var card = _cards[cardIndex];
        if (card.Eternal)
        {
            reason = Localize("永恒卡牌不能编辑。", "Eternal cards cannot be edited.");
            return false;
        }
        try
        {
            var identity = AutoAnthonyEditorApi.RerollEditorIdentity(Preview(cardIndex), Random.Shared.Next());
            if (identity.Name is not null)
                card.Shell = card.Shell with { Name = identity.Name };
            card.DraftIdentity = identity;
            card.IdentityChanged = true;
            _analysisCache.Remove(cardIndex);
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            Log.Warn($"[CardTinkering] Card identity reroll failed: {exception.Message}");
            reason = Localize("无法为当前效果找到合适的卡名与卡图。",
                "No suitable name and portrait could be found for these effects.");
            return false;
        }
    }

    private static string DraftPortraitPath(TrainingCard card) => card.DraftIdentity is { } identity
        ? identity.PortraitVariantPath ?? identity.PortraitPath
        : card.OriginalPortraitPath;

    internal CardTinkeringCardAnalysis Value(int cardIndex) => AnalysisSnapshot(cardIndex).Analysis;
    internal CardEffectiveCost EffectiveCost(int cardIndex)
    {
        var preview = Preview(cardIndex);
        if (CardTinkeringApi.IsVariableX(preview))
        {
            var x0 = CardTinkeringApi.GetEffectiveCost(preview, 0);
            var x3 = CardTinkeringApi.GetEffectiveCost(preview, 3);
            return new CardEffectiveCost(null,
                double.IsFinite(x0) ? x0 : null,
                double.IsFinite(x3) ? x3 : null);
        }
        var fixedCost = CardTinkeringApi.GetEffectiveCost(preview);
        return double.IsFinite(fixedCost)
            ? new CardEffectiveCost(fixedCost, null, null)
            : new CardEffectiveCost(null, null, null);
    }

    internal IReadOnlyList<CardBudgetLine> BudgetAmounts(int cardIndex) =>
        AnalysisSnapshot(cardIndex).Lines.Select(line => BudgetLine(line.Label, line.Analysis, line.ShellCapacity))
            .ToArray();

    private CardAnalysisSnapshot AnalysisSnapshot(int cardIndex)
    {
        if (_analysisCache.TryGetValue(cardIndex, out var cached)) return cached;
        var card = _cards[cardIndex];
        var preview = Preview(cardIndex);
        var analysis = TinkeringValue.Evaluate(preview);
        CardAnalysisLine[] lines;
        var balanced = ChaosRunDefinitions.ActiveNumericBalanceOptimization;
        if (CardTinkeringApi.IsVariableX(preview))
        {
            var x0 = CardTinkeringApi.AnalyzeAtX(preview, 0, balanced);
            var x3 = CardTinkeringApi.AnalyzeAtX(preview, 3, balanced);
            lines =
            [
                new CardAnalysisLine("f(0)", x0, ScaleCapacity(card.CapacityX0 ?? card.Capacity,
                    card.CapacityUpperBoundX0 ?? x0.Budget.OrdinaryUpperBound,
                    x0.Budget.OrdinaryUpperBound)),
                new CardAnalysisLine("f(3)", x3, ScaleCapacity(card.CapacityX3 ?? card.Capacity,
                    card.CapacityUpperBoundX3 ?? x3.Budget.OrdinaryUpperBound,
                    x3.Budget.OrdinaryUpperBound))
            ];
        }
        else
        {
            lines =
            [
                new CardAnalysisLine(string.Empty, analysis, ScaleCapacity(card.Capacity,
                    card.CapacityUpperBound, analysis.Budget.OrdinaryUpperBound))
            ];
        }
        var snapshot = new CardAnalysisSnapshot(analysis, lines);
        _analysisCache[cardIndex] = snapshot;
        return snapshot;
    }

    private static int ScaleCapacity(int referenceCapacity, double referenceUpperBound, double currentUpperBound)
    {
        if (referenceCapacity <= 0 || currentUpperBound <= 0d || !double.IsFinite(currentUpperBound)) return 0;
        if (referenceUpperBound <= 0d || !double.IsFinite(referenceUpperBound)) return referenceCapacity;
        var scaled = referenceCapacity * currentUpperBound / referenceUpperBound;
        return scaled >= int.MaxValue ? int.MaxValue : Math.Max(0, (int)Math.Floor(scaled + 0.0001d));
    }

    private static CardBudgetLine BudgetLine(string label, CardTinkeringCardAnalysis analysis,
        int shellCapacity) => new(label,
        analysis.Budget.PositiveValue - analysis.Budget.LinearCompensation,
        shellCapacity * Math.Max(1d, analysis.Budget.DownsideMultiplier), shellCapacity);

    internal IReadOnlyList<ComponentAnalysisLine> ComponentValues(int cardIndex, string componentId)
    {
        var flattened = Flatten(cardIndex);
        var index = flattened.FindIndex(item => item.Component.Id == componentId);
        if (index < 0) return [];
        var preview = Preview(cardIndex);
        return AnalysisSnapshot(cardIndex).Lines
            .Where(line => (uint)index < (uint)line.Analysis.Components.Count)
            .Select(line => new ComponentAnalysisLine(line.Label,
                DisplayComponents(preview, line)[index]))
            .ToArray();
    }

    internal static IReadOnlyList<CardTinkeringComponentAnalysis> DisplayComponents(GeneratedCard preview,
        CardAnalysisLine line)
    {
        var components = line.Analysis.Components.ToArray();
        // The production API's ContextualValue is the exact whole-card contribution, so a child effect includes
        // every trigger outside it. The chip already shows those frequencies on the owning trigger(s); repeat them
        // in the child's value and the same multiplier appears twice in the UI. Keep the production analysis intact
        // for the card budget, but divide the display-only contribution by the cumulative owner multiplier.
        for (var index = 0; index < components.Length && index < preview.Operations.Count; index++)
            components[index] = WithoutAncestorTriggerMultiplier(
                preview.Operations[index], components[index], components);
        for (var index = 0; index < components.Length && index < preview.Operations.Count; index++)
        {
            if (!CardEffectRules.IsCopyThisCardToDiscard(preview.Operations[index])) continue;
            var without = RemoveOperation(preview, index);
            var withoutMultiplier = AnalyzeLine(without, line.Label).Budget.DownsideMultiplier;
            if (withoutMultiplier <= .0001d) continue;
            var contextualFactor = line.Analysis.Budget.DownsideMultiplier / withoutMultiplier;
            if (contextualFactor <= components[index].DownsideMultiplier + .0001d) continue;
            components[index] = components[index] with
            {
                IsNegative = true,
                DownsideMultiplier = contextualFactor
            };
        }

        var componentProduct = components.Aggregate(1d,
            (current, component) => current * Math.Max(1d, component.DownsideMultiplier));
        var keywordProduct = KeywordMultiplier(preview, CardTag.Exhaust, line.Label)
                             * KeywordMultiplier(preview, CardTag.Ethereal, line.Label)
                             * KeywordMultiplier(preview, CardTag.Eternal, line.Label);
        var shownProduct = componentProduct * keywordProduct;
        if (shownProduct <= .0001d) return components;
        var correction = line.Analysis.Budget.DownsideMultiplier / shownProduct;
        if (Math.Abs(correction - 1d) < .0001d) return components;

        // The whole-card factor is rounded by the production model after combining its parts. Put that tiny exact
        // remainder on the last existing multiplier chip so the visible factors reproduce the top-line number.
        var carrier = Array.FindLastIndex(components, component => component.DownsideMultiplier > 1.0001d);
        if (carrier >= 0)
            components[carrier] = components[carrier] with
            {
                DownsideMultiplier = Math.Max(1d, components[carrier].DownsideMultiplier * correction)
            };
        return components;
    }

    private static CardTinkeringComponentAnalysis WithoutAncestorTriggerMultiplier(
        GeneratorOperation operation, CardTinkeringComponentAnalysis analysis,
        IReadOnlyList<CardTinkeringComponentAnalysis> components)
    {
        if (analysis.IsTrigger
            || !operation.Parameters.TryGetValue("triggerIndex", out var ownerIndex)
            || ownerIndex < 0 || ownerIndex >= analysis.OperationIndex
            || (uint)ownerIndex >= (uint)components.Count)
            return analysis;

        var ancestorMultiplier = components[ownerIndex].TriggerMultiplier;
        if (!double.IsFinite(ancestorMultiplier) || ancestorMultiplier <= 0.0001d
            || Math.Abs(ancestorMultiplier - 1d) < 0.0001d)
            return analysis;
        return analysis with
        {
            ContextualValue = analysis.ContextualValue / ancestorMultiplier,
            ExpectedResourceRefund = analysis.ExpectedResourceRefund / ancestorMultiplier
        };
    }

    private static GeneratedCard RemoveOperation(GeneratedCard card, int removedIndex)
    {
        var operations = card.Operations.Where((_, index) => index != removedIndex).Select(operation =>
        {
            if (!operation.Parameters.TryGetValue("triggerIndex", out var owner)) return operation;
            var parameters = operation.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.Ordinal);
            if (owner == removedIndex) parameters.Remove("triggerIndex");
            else if (owner > removedIndex) parameters["triggerIndex"] = owner - 1;
            return operation with { Parameters = parameters };
        }).ToArray();
        return OperationRuntimeSpecCompiler.Attach(card with { Operations = operations });
    }

    internal static CardTinkeringCardAnalysis AnalyzeLine(GeneratedCard card, string label)
    {
        var balanced = ChaosRunDefinitions.ActiveNumericBalanceOptimization;
        return label switch
        {
            "f(0)" => CardTinkeringApi.AnalyzeAtX(card, 0, balanced),
            "f(3)" => CardTinkeringApi.AnalyzeAtX(card, 3, balanced),
            _ => TinkeringValue.Evaluate(card)
        };
    }

    internal static double KeywordMultiplier(GeneratedCard preview, CardTag tag, string label)
    {
        if (!preview.Tags.Contains(tag)) return 1d;
        // Decompose the rounded whole-card percentage in one stable order. Computing every keyword as an
        // independent full-card ratio double-counts rounding whenever two lifecycle keywords coexist (for
        // example Exhaust + Eternal displayed 1.605 while production compares against 1.60).
        var ordinaryTags = preview.Tags.Where(item =>
            item is not (CardTag.Exhaust or CardTag.Ethereal or CardTag.Eternal)).ToArray();
        GeneratedCard WithLifecycle(params CardTag[] lifecycle) => preview with
            { Tags = ordinaryTags.Concat(lifecycle).ToArray() };
        GeneratedCard withoutCurrent;
        GeneratedCard withCurrent;
        switch (tag)
        {
            case CardTag.Exhaust:
                withoutCurrent = WithLifecycle();
                withCurrent = WithLifecycle(CardTag.Exhaust);
                break;
            case CardTag.Ethereal:
                withoutCurrent = preview.Tags.Contains(CardTag.Exhaust)
                    ? WithLifecycle(CardTag.Exhaust)
                    : WithLifecycle();
                withCurrent = preview.Tags.Contains(CardTag.Exhaust)
                    ? WithLifecycle(CardTag.Exhaust, CardTag.Ethereal)
                    : WithLifecycle(CardTag.Ethereal);
                break;
            case CardTag.Eternal:
                withoutCurrent = preview with
                    { Tags = preview.Tags.Where(item => item != CardTag.Eternal).ToArray() };
                withCurrent = preview;
                break;
            default:
                withoutCurrent = preview with { Tags = preview.Tags.Where(item => item != tag).ToArray() };
                withCurrent = preview;
                break;
        }
        var denominator = AnalyzeLine(withoutCurrent, label).Budget.DownsideMultiplier;
        return denominator > .0001d
            ? Math.Max(1d, AnalyzeLine(withCurrent, label).Budget.DownsideMultiplier / denominator)
            : 1d;
    }

    internal IReadOnlyList<KeywordAnalysisLine> KeywordValues(int cardIndex, CardTag tag)
    {
        if ((uint)cardIndex >= (uint)_cards.Count) return [];
        var preview = Preview(cardIndex);
        if (!preview.Tags.Contains(tag)) return [];

        return AnalysisSnapshot(cardIndex).Lines.Select(line =>
        {
            return new KeywordAnalysisLine(line.Label, CardTinkeringApi.GetKeywordValue(tag),
                KeywordMultiplier(preview, tag, line.Label));
        }).ToArray();
    }

    internal IReadOnlyList<TriggerAnalysisLine> TriggerMultipliers(int cardIndex, string componentId)
    {
        var flattened = Flatten(cardIndex);
        var index = flattened.FindIndex(item => item.Component.Id == componentId);
        if (index < 0) return [];
        var preview = Preview(cardIndex);
        var hasParent = flattened[index].Operation.Parameters.TryGetValue("triggerIndex", out var parentIndex)
                        && parentIndex >= 0 && parentIndex < index;
        var hasLinkedChild = flattened.Skip(index + 1).Any(item =>
                                 item.Operation.Parameters.GetValueOrDefault("triggerIndex", -1) == index)
                             || CardEffectRules.IsDependencyPrefix(flattened[index].Operation)
                             && index + 1 < flattened.Count
                             && CardEffectRules.IsLegalDependencyPayoff(
                                 flattened[index].Operation, flattened[index + 1].Operation);
        return AnalysisSnapshot(cardIndex).Lines
            .Where(line => (uint)index < (uint)line.Analysis.Components.Count
                           && line.Analysis.Components[index].IsTrigger)
            .Select(line =>
            {
                var nested = line.Analysis.Components[index].TriggerMultiplier;
                if (!hasLinkedChild)
                    nested = EmptyTriggerMultiplier(preview, index, line.Label, nested);
                if (!hasParent || (uint)parentIndex >= (uint)line.Analysis.Components.Count)
                    return new TriggerAnalysisLine(line.Label, nested, nested, false);

                // TriggerMultiplier is cumulative through the complete parent chain. For X cards, both the child
                // and parent must come from the same resolved endpoint; mixing f(0) with the unresolved analysis
                // produced a plausible-looking but numerically wrong local multiplier.
                var parentNested = line.Analysis.Components[parentIndex].TriggerMultiplier;
                return ComposeTriggerLine(line.Label, nested, parentNested);
            }).ToArray();
    }

    private static TriggerAnalysisLine ComposeTriggerLine(string label, double reportedMultiplier,
        double parentNestedMultiplier)
    {
        if (!double.IsFinite(parentNestedMultiplier) || Math.Abs(parentNestedMultiplier) <= 0.0001d)
            return new TriggerAnalysisLine(label, reportedMultiplier, reportedMultiplier, true);
        return new TriggerAnalysisLine(label, reportedMultiplier / parentNestedMultiplier,
            reportedMultiplier, true);
    }

    /// <summary>
    /// The core analyzer reports a bare trigger's per-turn/event frequency until it owns a payoff, then reports
    /// the complete linked-resolution estimate used for valuation (for example a persistent 0.75/turn Power
    /// trigger becomes 2.25 over its expected active lifetime). Preview the missing link with a harmless child so
    /// an empty slot and the same slot after installation use one stable, production-backed display contract.
    /// </summary>
    internal static double EmptyTriggerMultiplier(GeneratedCard preview, int triggerIndex, string label,
        double fallback)
    {
        if ((uint)triggerIndex >= (uint)preview.Operations.Count) return fallback;
        try
        {
            var operations = new List<GeneratorOperation>(preview.Operations.Count + 1);
            for (var oldIndex = 0; oldIndex < preview.Operations.Count; oldIndex++)
            {
                var operation = preview.Operations[oldIndex];
                if (oldIndex > triggerIndex
                    && operation.Parameters.TryGetValue("triggerIndex", out var owner)
                    && owner > triggerIndex)
                {
                    var shifted = operation.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value,
                        StringComparer.Ordinal);
                    shifted["triggerIndex"] = owner + 1;
                    operation = operation with { Parameters = shifted };
                }
                operations.Add(operation);
                if (oldIndex != triggerIndex) continue;
                var payoff = CardTinkeringApi.GetComponentPrototypes()
                    .FirstOrDefault(prototype => prototype.Operation.Template == "N:B"
                                                 && prototype.CardReference == CardReferenceRequirement.None)
                    ?.Operation;
                if (payoff is null) return fallback;
                var payoffParameters = payoff.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value,
                    StringComparer.Ordinal);
                payoffParameters["triggerIndex"] = triggerIndex;
                operations.Add(payoff with { Parameters = payoffParameters });
            }

            // Sly's printed-cost inversion is unrelated to trigger cadence and intentionally rejects malformed
            // freeform shells. Do not let such a shell prevent an individual trigger chip from rendering.
            var probe = preview with
            {
                Operations = operations,
                Tags = preview.Tags.Where(tag => tag != CardTag.Sly).ToArray()
            };
            var analysis = AnalyzeLine(probe, label);
            return (uint)triggerIndex < (uint)analysis.Components.Count
                ? analysis.Components[triggerIndex].TriggerMultiplier
                : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    internal IReadOnlyList<FormulaBadge> ComponentFormula(int cardIndex, string componentId,
        CardTinkeringComponentAnalysis atomic, ComponentDownsidePricing downside,
        IReadOnlyList<ComponentAnalysisLine>? currentValues = null)
    {
        var flattened = Flatten(cardIndex);
        var index = flattened.FindIndex(item => item.Component.Id == componentId);
        if (index < 0) return [];
        var operations = flattened.Select(item => item.Operation).ToArray();
        var shell = cardIndex >= 0 ? _cards[cardIndex].Shell : null;
        return SpecialValuePricing.Resolve(shell, operations, index, atomic, downside,
            cardIndex >= 0 ? currentValues ?? ComponentValues(cardIndex, componentId) : null);
    }

    internal CardTinkeringValidationResult Validate(int cardIndex)
    {
        if ((uint)cardIndex >= (uint)_cards.Count)
            return new CardTinkeringValidationResult(false, ["unknown target card"]);
        var flattened = Flatten(cardIndex);
        var operations = flattened.Select(item => item.Operation).ToArray();
        var validation = ValidateAssembly(_cards[cardIndex].Shell, operations);
        var errors = validation.Errors.Where(error =>
            !_cards[cardIndex].EntryValidationErrors.Contains(error)).ToArray();
        return errors.Length == 0
            ? CardTinkeringValidationResult.Valid
            : new CardTinkeringValidationResult(false, errors);
    }

    /// <summary>
    /// The current component assembly is the only editable state. Component upgrades retain their own state and
    /// are previewed independently, but a hypothetical "every component upgraded" card must not add another set
    /// of editor-only placement rules. Filter only API checks which the base card-template validator does not
    /// enforce: Exhaust self-cost changes and a correctly nested next-turn return with no earlier immediate payoff.
    /// </summary>
    internal static CardTinkeringValidationResult ValidateAssembly(GeneratedCard shell,
        IReadOnlyList<GeneratorOperation> operations, CardTinkeringValidationOptions? options = null)
    {
        var errors = CardTinkeringApi.Validate(shell, operations, options).Errors
            .Where(error => !IsEditorOnlyRestriction(shell, operations, error))
            .Distinct(StringComparer.Ordinal).ToArray();
        return errors.Length == 0
            ? CardTinkeringValidationResult.Valid
            : new CardTinkeringValidationResult(false, errors);
    }

    private static bool IsEditorOnlyRestriction(GeneratedCard shell,
        IReadOnlyList<GeneratorOperation> operations, string error)
    {
        // A null target slot is the structured distinction between an operation which performs its own native
        // random/choice flow and an editor component waiting for a separate selector. Recent native decomposition
        // data exposes both through the same needs_external_card_slot runtime flag; do not invent a selector for
        // the self-contained form. This covers every such recipe by contract rather than by native card identity.
        if (operations.Any(operation => string.IsNullOrWhiteSpace(operation.CardTargetSlot)
                                        && CardEffectRules.NeedsExternalCardSlot(operation)
                                        && error.Equals($"component {operation.Template} requires one matching card selector",
                                            StringComparison.Ordinal)))
            return true;
        if (error.Equals("delayed return-to-hand requires its next-turn trigger and an earlier play effect",
                StringComparison.Ordinal))
            return true;
        if (shell.Type == GeneratedCardType.Power || !shell.Tags.Contains(CardTag.Exhaust)) return false;
        if (error.Equals("on-play self-cost-change components require a reusable non-Power shell",
                StringComparison.Ordinal))
            return true;
        if (!error.Equals("increase-this-card-cost must be an immediate component on a reusable non-Power shell",
                StringComparison.Ordinal))
            return false;
        return operations.Where(operation => operation.Template == "D:IncreaseThisCardCost")
            .All(operation => !operation.Parameters.ContainsKey("triggerIndex"));
    }

    internal bool CardIsLegal(int cardIndex, out string reason)
        => CardIsLegal(cardIndex, Value(cardIndex), out reason);

    internal bool CardIsLegal(int cardIndex, CardTinkeringCardAnalysis value, out string reason)
    {
        var validation = Validate(cardIndex);
        if (!validation.IsValid)
        {
            reason = LocalizedReason(validation.Errors[0]);
            return false;
        }
        var card = _cards[cardIndex];
        foreach (var budget in BudgetAmounts(cardIndex))
        {
            var prefix = budget.Label.Length > 0 ? budget.Label + Localize("：", ": ") : string.Empty;
            if (!TinkeringSettings.IgnoreCapacityLimit && !card.Eternal
                && budget.OccupiedValue > budget.Capacity + 0.0001d)
            {
                reason = Localize(
                    $"{prefix}超出容量（{budget.OccupiedValue:0.##}/{budget.Capacity:0.##}）。",
                    $"{prefix}Over capacity ({budget.OccupiedValue:0.##}/{budget.Capacity:0.##}).");
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    internal bool CanMoveAnywhereOnCard(string componentId, int cardIndex)
    {
        if ((uint)cardIndex >= (uint)_cards.Count || _cards[cardIndex].Eternal) return false;
        if (CanMove(componentId, cardIndex, null, out _)) return true;
        foreach (var target in VisibleComponents(cardIndex).ToArray())
        {
            if (CanMoveBefore(componentId, target.Id, out _)) return true;
            if (CanHaveChildren(target.Operation)
                && CanMove(componentId, cardIndex, target.Id, out _)) return true;
        }
        return false;
    }

    internal bool CanMove(string componentId, int targetCard, string? parentId, out string reason) =>
        CanMoveCore(componentId, targetCard, parentId, beforeComponentId: null, out reason);

    private bool CanMoveCore(string componentId, int targetCard, string? parentId,
        string? beforeComponentId, out string reason)
    {
        if (!_components.TryGetValue(componentId, out var component))
        {
            reason = Localize("找不到该组件。", "Component not found.");
            return false;
        }
        if (component.CardIndex >= 0 && _cards[component.CardIndex].Eternal)
        {
            reason = Localize("永恒牌无法编辑。", "Eternal cards cannot be edited.");
            return false;
        }
        if (targetCard >= 0 && ((uint)targetCard >= (uint)_cards.Count || _cards[targetCard].Eternal))
        {
            reason = Localize("不能把组件放入永恒牌。", "Components cannot be placed in Eternal cards.");
            return false;
        }
        if (parentId == componentId || IsDescendant(parentId, componentId))
        {
            reason = Localize("组件不能放入自己的子树。", "A component cannot be placed inside its own branch.");
            return false;
        }
        if (parentId is not null)
        {
            if (!_components.TryGetValue(parentId, out var parent) || parent.CardIndex != targetCard)
            {
                reason = Localize("目标插槽已失效。", "The destination slot is no longer available.");
                return false;
            }
            if (!CanHaveChildren(parent.Operation))
            {
                reason = Localize("这个组件不能拥有下级效果。", "This component cannot contain child effects.");
                return false;
            }
        }

        // The backpack remains the escape hatch for an unfinished edit. Card destinations are preflighted using
        // the exact projected order, so shell/type/target/X and repeat restrictions fail before the UI advertises
        // the destination. Empty triggers are still permitted as an intermediate state and checked on save/leave.
        if (targetCard >= 0)
        {
            var projectedComponents = FlattenProjected(targetCard, componentId, parentId, beforeComponentId);
            var projectionOptions = DragProjectionOptions();
            var error = DragProjectionErrors(_cards[targetCard], projectedComponents, projectionOptions)
                .FirstOrDefault();
            if (error is not null)
            {
                reason = LocalizedReason(error);
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    internal bool Move(string componentId, int targetCard, string? parentId, out string reason)
    {
        if (!CanMove(componentId, targetCard, parentId, out reason)) return false;
        MoveUnchecked(_components[componentId], targetCard, parentId);
        NormalizeImplicitSelectors();
        NormalizeOrders();
        _analysisCache.Clear();
        return true;
    }

    internal bool CanMoveBefore(string componentId, string beforeComponentId, out string reason)
    {
        if (!_components.TryGetValue(beforeComponentId, out var before))
        {
            reason = Localize("目标组件已失效。", "The destination component is no longer available.");
            return false;
        }
        if (componentId == beforeComponentId)
        {
            reason = Localize("组件已经在这里。", "The component is already here.");
            return false;
        }
        if (IsDescendant(beforeComponentId, componentId))
        {
            reason = Localize("不能把组件插入自己的子树。", "A component cannot be inserted into its own branch.");
            return false;
        }
        return CanMoveCore(componentId, before.CardIndex, before.ParentId, beforeComponentId, out reason);
    }

    internal bool MoveBefore(string componentId, string beforeComponentId, out string reason)
    {
        if (!CanMoveBefore(componentId, beforeComponentId, out reason)) return false;
        var component = _components[componentId];
        var before = _components[beforeComponentId];
        var targetCard = before.CardIndex;
        var parentId = before.ParentId;
        var siblings = Roots(targetCard, parentId)
            .Where(item => item.Id != componentId)
            .ToList();
        var targetIndex = siblings.FindIndex(item => item.Id == beforeComponentId);
        if (targetIndex < 0)
        {
            reason = Localize("目标组件已失效。", "The destination component is no longer available.");
            return false;
        }
        MoveUnchecked(component, targetCard, parentId);

        // MoveUnchecked deliberately appends. Rebuild just this sibling list so dropping on a chip has the
        // unambiguous meaning "insert immediately before this chip", including across cards and the backpack.
        siblings.Insert(targetIndex, component);
        for (var index = 0; index < siblings.Count; index++) siblings[index].Order = index;
        NormalizeImplicitSelectors();
        NormalizeOrders();
        _analysisCache.Clear();
        reason = string.Empty;
        return true;
    }

    internal bool CanLeave(out string reason)
    {
        for (var index = 0; index < _cards.Count; index++)
        {
            var value = Value(index);
            if (!CardIsLegal(index, value, out var cardReason))
            {
                reason = $"{_cards[index].LiveCard.Title}: {cardReason}";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    internal void Reset() => Restore(_entrance);
    internal void SaveCheckpoint() => _checkpoint = Capture();
    internal bool LoadCheckpoint()
    {
        if (_checkpoint is null) return false;
        Restore(_checkpoint);
        return true;
    }

    internal bool Commit(out string reason)
    {
        var pending = new List<(TrainingCard Card, GeneratedCard Definition,
            ComponentProgress[] Progress, GeneratedCard OriginalDefinition,
            TinkeredCardState OriginalState, int OriginalDamage, int OriginalBlock, bool Invalid,
            int SavedCapacity, int? SavedCapacityX0, int? SavedCapacityX3)>();
        var nativePending = new List<(TrainingCard Card, GeneratedCard Definition,
            ComponentProgress[] Progress, bool Invalid, int SavedCapacity,
            int? SavedCapacityX0, int? SavedCapacityX3)>();
        for (var cardIndex = 0; cardIndex < _cards.Count; cardIndex++)
        {
            var card = _cards[cardIndex];
            if (card.Eternal) continue;
            var flattened = Flatten(cardIndex);
            var editableDefinition = Preview(cardIndex);
            var progress = flattened.Select(item => TinkeringStateStore.WithComponentMetadata(
                item.Operation, item.Component.Upgrades, item.Component.Progress)).ToArray();
            var invalid = !CardIsLegal(cardIndex, Value(cardIndex), out _);
            var budgets = BudgetAmounts(cardIndex);
            var savedCapacity = budgets[^1].ShellCapacity;
            int? savedCapacityX0 = budgets.Count > 1 ? budgets[0].ShellCapacity : null;
            int? savedCapacityX3 = budgets.Count > 1 ? budgets[1].ShellCapacity : null;
            var rebuilt = invalid
                ? TinkeringStateStore.MaterializeComponentUpgradesForPreview(
                    editableDefinition, progress, card.LiveCard.IsUpgraded)
                : TinkeringStateStore.MaterializeComponentUpgrades(
                    editableDefinition, progress, card.LiveCard.IsUpgraded);
            if (card.IsNativeSource)
            {
                if (!string.Equals(CardTinkeringApi.SerializeCard(rebuilt), card.OriginalDefinitionPayload,
                        StringComparison.Ordinal))
                    nativePending.Add((card, rebuilt, progress, invalid, savedCapacity,
                        savedCapacityX0, savedCapacityX3));
                continue;
            }
            pending.Add((card, rebuilt, progress, card.LiveCard.Generated,
                TinkeringStateStore.Ensure(card.LiveCard), card.LiveCard.ExtraDamage,
                card.LiveCard.ExtraBlock, invalid, savedCapacity, savedCapacityX0, savedCapacityX3));
        }

        var backpack = Flatten(-1).Select(item => new StoredComponent(
            item.Operation, item.Component.Upgrades, item.Component.Progress)).ToArray();
        var originalRunState = TinkeringStateStore.GetRun(_run);
        var nativeReplacements = new List<(CardModel Original, ChaosCardModel Replacement,
            NativeCardPile Pile, int Index)>();
        try
        {
            // Rebuild every draft first, then publish the batch. Native deck views therefore keep reading the
            // untouched run deck throughout editing; no component move or temporary checkpoint mutates a live card.
            foreach (var update in pending)
            {
                if (update.Card.UsesFreeformHost && update.Card.IdentityChanged)
                {
                    update.Card.LiveCard.ApplyFreeformDefinition(update.Definition);
                    update.Card.LiveCard.FreeformPortraitPath = DraftPortraitPath(update.Card);
                }
                else if (update.Card.IdentityChanged && update.Card.DraftIdentity is { } identity)
                    AutoAnthonyEditorApi.ApplyEditorDefinition(update.Card.LiveCard, update.Definition, identity);
                else
                    update.Card.LiveCard.ApplyTinkeredDefinition(update.Definition);
                TinkeringStateStore.Set(update.Card.LiveCard, new TinkeredCardState(
                    update.SavedCapacity, false, update.Progress,
                    CapacityModel: update.SavedCapacityX0 is not null && update.SavedCapacityX3 is not null
                        ? update.Card.NumericRandom
                            ? TinkeringStateStore.NumericRandomXEndpointCapacityModel
                            : TinkeringStateStore.XEndpointCapacityModel
                        : update.Card.NumericRandom
                            ? TinkeringStateStore.NumericRandomNetCapacityModel
                            : TinkeringStateStore.NetValueCapacityModel,
                    Invalid: update.Invalid, CapacityX0: update.SavedCapacityX0,
                    CapacityX3: update.SavedCapacityX3));
                update.Card.LiveCard.ExtraDamage = update.Progress.Sum(item => item.PermanentDamage);
                update.Card.LiveCard.ExtraBlock = update.Progress.Sum(item => item.PermanentBlock);
            }
            foreach (var update in nativePending)
            {
                var original = update.Card.SourceCard;
                var pile = original.Pile
                           ?? throw new InvalidOperationException($"Native card {original.Id} is no longer in a pile.");
                if (pile.Type != NativePileType.Deck)
                    throw new InvalidOperationException($"Native card {original.Id} is no longer in the run deck.");
                var index = pile.Cards.IndexOf(original);
                if (index < 0)
                    throw new InvalidOperationException($"Native card {original.Id} is missing from its run deck.");
                var replacement = AutoAnthonyFreeformCardApi.CreateForDeck(
                    original.Owner, update.Definition, DraftPortraitPath(update.Card));
                CopyNativeCardState(original, replacement);
                original.RemoveFromCurrentPile();
                pile.AddInternal(replacement, index);
                // Register the reversible pile swap immediately. Any later state/progress write must still be able
                // to put the untouched native instance back at its exact deck position.
                nativeReplacements.Add((original, replacement, pile, index));
                TinkeringStateStore.Set(replacement, new TinkeredCardState(
                    update.SavedCapacity, false, update.Progress,
                    CapacityModel: update.SavedCapacityX0 is not null && update.SavedCapacityX3 is not null
                        ? update.Card.NumericRandom
                            ? TinkeringStateStore.NumericRandomXEndpointCapacityModel
                            : TinkeringStateStore.XEndpointCapacityModel
                        : update.Card.NumericRandom
                            ? TinkeringStateStore.NumericRandomNetCapacityModel
                            : TinkeringStateStore.NetValueCapacityModel,
                    Invalid: update.Invalid, CapacityX0: update.SavedCapacityX0,
                    CapacityX3: update.SavedCapacityX3));
                replacement.ExtraDamage = update.Progress.Sum(item => item.PermanentDamage);
                replacement.ExtraBlock = update.Progress.Sum(item => item.PermanentBlock);
            }
            if (_completesTrainingAct)
                TinkeringStateStore.Complete(_run, _owner, _actIndex, backpack);
            else
                TinkeringStateStore.UpdateBackpack(_run, _owner, backpack);
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            foreach (var swap in nativeReplacements.AsEnumerable().Reverse())
            {
                if (ReferenceEquals(swap.Replacement.Pile, swap.Pile))
                    swap.Replacement.RemoveFromCurrentPile();
                if (swap.Original.Pile is null)
                    swap.Pile.AddInternal(swap.Original, Math.Min(swap.Index, swap.Pile.Cards.Count));
            }
            foreach (var update in pending)
            {
                if (update.Card.UsesFreeformHost && update.Card.IdentityChanged)
                {
                    update.Card.LiveCard.ApplyFreeformDefinition(update.OriginalDefinition);
                    update.Card.LiveCard.FreeformPortraitPath = update.Card.OriginalPortraitPath;
                }
                else if (update.Card.IdentityChanged)
                {
                    if (update.Card.OriginalIdentity is { } originalIdentity)
                        AutoAnthonyEditorApi.ApplyEditorDefinition(update.Card.LiveCard,
                            update.OriginalDefinition, originalIdentity);
                    else
                    {
                        update.Card.LiveCard.EditorDefinitionPayload = string.Empty;
                        update.Card.LiveCard.EditorPortraitPath = string.Empty;
                        update.Card.LiveCard.EditorPortraitSourceId = string.Empty;
                        update.Card.LiveCard.EditorPortraitVariantId = string.Empty;
                        update.Card.LiveCard.EditorPortraitVariantPath = string.Empty;
                        update.Card.LiveCard.ApplyTinkeredDefinition(update.OriginalDefinition);
                    }
                }
                else
                    update.Card.LiveCard.ApplyTinkeredDefinition(update.OriginalDefinition);
                TinkeringStateStore.Set(update.Card.LiveCard, update.OriginalState);
                update.Card.LiveCard.ExtraDamage = update.OriginalDamage;
                update.Card.LiveCard.ExtraBlock = update.OriginalBlock;
            }
            TinkeringStateStore.SetRun(_run, originalRunState);
            reason = Localize($"保存失败，牌组已恢复：{exception.Message}",
                $"Save failed; the deck was restored: {exception.Message}");
            return false;
        }
    }

    private static void CopyNativeCardState(CardModel source, ChaosCardModel destination)
    {
        destination.FloorAddedToDeck = source.FloorAddedToDeck;
        if (source.Enchantment is { } enchantment)
        {
            var copy = (EnchantmentModel)enchantment.ClonePreservingMutability();
            destination.EnchantInternal(copy, enchantment.Amount);
            copy.ModifyCard();
            destination.FinalizeUpgradeInternal();
        }
        for (var level = 0; level < source.CurrentUpgradeLevel && destination.IsUpgradable; level++)
        {
            destination.UpgradeInternal();
            destination.FinalizeUpgradeInternal();
        }
        if (source.Affliction is { } affliction)
        {
            var copy = (AfflictionModel)affliction.ClonePreservingMutability();
            destination.AfflictInternal(copy, affliction.Amount);
            copy.AfterApplied();
        }
        AutoAnthonyNativeCardApi.CopyLocalKeywordDelta(source, destination);
    }

    internal string ComponentText(TrainingComponent component, bool english = false)
    {
        var operation = StripParent(component.Operation);
        var upgraded = component.Upgrades.Count == 0
            ? operation
            : CardUpgradeGenerator.ApplyEffectsToOperations([operation], component.Upgrades.Select(effect =>
                effect with { OperationIndex = 0 }).ToArray())[0];
        var text = StandaloneComponentText(operation, english);
        var upgradedText = StandaloneComponentText(upgraded, english);
        if (!string.Equals(text, upgradedText, StringComparison.Ordinal))
            text = component.Progress.Upgraded
                ? Localize($"基础：{text}\n当前（已升级）：{upgradedText}",
                    $"Base: {text}\nCurrent (upgraded): {upgradedText}")
                : Localize($"{text}\n升级后：{upgradedText}", $"{text}\nUpgraded: {upgradedText}");
        if (component.Progress.PermanentDamage + component.Progress.PermanentBlock > 0)
        {
            var parts = new List<string>();
            if (component.Progress.PermanentDamage > 0)
                parts.Add(Localize($"永久伤害 +{component.Progress.PermanentDamage}",
                    $"permanent damage +{component.Progress.PermanentDamage}"));
            if (component.Progress.PermanentBlock > 0)
                parts.Add(Localize($"永久格挡 +{component.Progress.PermanentBlock}",
                    $"permanent Block +{component.Progress.PermanentBlock}"));
            text += Localize($"（{string.Join("，", parts)}）", $" ({string.Join(", ", parts)})");
        }
        return text;
    }

    internal string ComponentContentText(TrainingComponent component, bool english = false)
    {
        var operation = TinkeringStateStore.EffectiveOperation(StripParent(component.Operation),
            component.Upgrades, component.Progress);
        var text = StandaloneComponentText(operation, english);
        if (component.Progress.PermanentDamage + component.Progress.PermanentBlock <= 0) return text;
        var parts = new List<string>();
        if (component.Progress.PermanentDamage > 0)
            parts.Add(Localize($"永久伤害 +{component.Progress.PermanentDamage}",
                $"permanent damage +{component.Progress.PermanentDamage}"));
        if (component.Progress.PermanentBlock > 0)
            parts.Add(Localize($"永久格挡 +{component.Progress.PermanentBlock}",
                $"permanent Block +{component.Progress.PermanentBlock}"));
        return text + Localize($"（{string.Join("，", parts)}）", $" ({string.Join(", ", parts)})");
    }

    private static string StandaloneComponentText(GeneratorOperation operation, bool english)
    {
        var rendered = english ? EnglishCardDescriptionRenderer.Render([operation])
            : CardDescriptionRenderer.Render([operation]);
        if (!string.IsNullOrWhiteSpace(rendered)) return rendered;

        // Whole-card rendering intentionally suppresses selector metadata because the consuming effect repeats the
        // choice in normal card text. In the editor the selector is a real, movable syntax component, so it needs a
        // standalone label instead of an apparently empty first chip. The localized operation projection is also a
        // safe fallback for any future metadata component intentionally omitted by the whole-card renderer.
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        if (english)
        {
            var localizedEnglish = operation.LocalizedText?.RenderEnglish(spec);
            if (!string.IsNullOrWhiteSpace(localizedEnglish)) return localizedEnglish;
            return operation.Template switch
            {
                "N_SELECT_HAND_ATTACK" => "Choose an Attack in your Hand.",
                "N_SELECT_HAND_CARD" => "Choose a card in your Hand.",
                _ => EnglishCardDescriptionRenderer.OperationText(operation)
            };
        }
        var localizedChinese = operation.LocalizedText?.RenderChinese(spec);
        return CardTextStyle.Chinese(operation,
            string.IsNullOrWhiteSpace(localizedChinese) ? operation.ChineseText : localizedChinese);
    }

    private void ImportCard(int cardIndex, GeneratedCard card, IReadOnlyList<ComponentProgress> progress)
    {
        var ids = new string[card.Operations.Count];
        var attackSlots = card.Operations.Where(operation => operation.Template == "N_SELECT_HAND_ATTACK")
            .Select(SelectorSlot).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var ordinarySlots = card.Operations.Where(operation => operation.Template == "N_SELECT_HAND_CARD")
            .Select(SelectorSlot).OfType<string>().ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < card.Operations.Count; index++)
        {
            var existingProgress = index < progress.Count ? progress[index] : new ComponentProgress();
            var operation = TinkeringStateStore.BaseOperation(card, index, existingProgress);
            if (CardEffectRules.NeedsExternalCardSlot(operation)
                && operation.CardTargetSlot is { Length: > 0 } slot
                && slot != "thisCard" && existingProgress.RequiresAttackCardSlot is null)
                existingProgress = existingProgress with
                {
                    RequiresAttackCardSlot = attackSlots.Contains(slot)
                        ? true
                        : ordinarySlots.Contains(slot) ? false : null
                };
            var parentId = operation.Parameters.TryGetValue("triggerIndex", out var parentIndex)
                           && parentIndex >= 0 && parentIndex < index ? ids[parentIndex] : null;
            var upgrades = TinkeringStateStore.ComponentUpgrades(card, index, existingProgress);
            ids[index] = AddComponent(operation, upgrades,
                existingProgress, cardIndex, parentId).Id;
        }
        ApplyImplicitDependencyContainment(card.Operations, ids);
    }

    /// <summary>
    /// Count/condition prefixes are displayed as owners of their adjacent payoff even though non-conditional
    /// prefixes are encoded by the runtime as siblings with the same trigger owner. Keeping that distinction here
    /// gives the editor an honest tree without changing the operation contract when it is flattened again.
    /// </summary>
    private void ApplyImplicitDependencyContainment(IReadOnlyList<GeneratorOperation> operations,
        IReadOnlyList<string> ids)
    {
        for (var index = 1; index < operations.Count && index < ids.Count; index++)
        {
            var prefix = operations[index - 1];
            var payoff = operations[index];
            if (!CardEffectRules.IsDependencyPrefix(prefix)
                || !CardEffectRules.IsLegalDependencyPayoff(prefix, payoff)) continue;
            var prefixOwner = prefix.Parameters.GetValueOrDefault("triggerIndex", -1);
            var payoffOwner = payoff.Parameters.GetValueOrDefault("triggerIndex", -1);
            var correctlyLinked = prefix.Scope == OperationScope.ConditionalTrigger
                ? payoffOwner == index - 1
                : prefixOwner == payoffOwner;
            if (correctlyLinked && _components.TryGetValue(ids[index], out var component))
                component.ParentId = ids[index - 1];
        }
    }

    /// <summary>
    /// Hand-card selectors are interpreter plumbing, not detachable effects. Regenerate exactly one hidden selector
    /// wherever an editable effect needs a card slot, so moving that effect cannot strand an empty selector chip or
    /// require the player to move two implementation details for one printed effect.
    /// </summary>
    private void NormalizeImplicitSelectors()
    {
        foreach (var selectorId in _components.Values.Where(component => IsImplicitSelector(component.Operation))
                     .Select(component => component.Id).ToArray())
            _components.Remove(selectorId);

        var groups = _components.Values
            .Where(component => CardEffectRules.NeedsExternalCardSlot(component.Operation)
                                && component.Operation.CardTargetSlot is { Length: > 0 } slot
                                && slot != "thisCard")
            .GroupBy(component => (CardIndex: component.CardIndex,
                Slot: component.Operation.CardTargetSlot!))
            .ToArray();
        foreach (var group in groups)
        {
            var slotIndex = group.Key.Slot.StartsWith("card", StringComparison.Ordinal)
                            && int.TryParse(group.Key.Slot.AsSpan("card".Length), out var parsed)
                ? Math.Max(1, parsed) : 1;
            var attackOnly = group.Any(component => component.Progress.RequiresAttackCardSlot == true);
            var operation = new GeneratorOperation(
                attackOnly ? "N_SELECT_HAND_ATTACK" : "N_SELECT_HAND_CARD",
                OperationScope.NonTargeted,
                attackOnly ? "选择手牌中的一张攻击牌。" : "选择手牌中的一张牌。",
                new Dictionary<string, int> { ["slotIndex"] = slotIndex },
                RuntimeSpec: OperationRuntimeSpecCompiler.GeneratedHandSelection(attackOnly));
            var selector = AddComponent(operation, [], new ComponentProgress(), group.Key.CardIndex, null);
            selector.Order = int.MinValue;
        }
    }

    private TrainingComponent AddComponent(GeneratorOperation operation,
        IReadOnlyList<CardUpgradeEffect> upgrades, ComponentProgress progress, int cardIndex, string? parentId)
    {
        var component = new TrainingComponent
        {
            Id = $"c{++_nextId}", Operation = StripParent(operation), Upgrades = upgrades.ToList(),
            Progress = progress, CardIndex = cardIndex, ParentId = parentId,
            Order = _components.Count(item => item.Value.CardIndex == cardIndex && item.Value.ParentId == parentId)
        };
        _components.Add(component.Id, component);
        return component;
    }

    private List<(TrainingComponent Component, GeneratorOperation Operation)> Flatten(int cardIndex)
    {
        var output = new List<(TrainingComponent, GeneratorOperation)>();
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        void Visit(TrainingComponent component, int? parentIndex)
        {
            var parameters = component.Operation.Parameters
                .Where(pair => pair.Key != "triggerIndex")
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (parentIndex.HasValue) parameters["triggerIndex"] = parentIndex.Value;
            var operation = OperationRuntimeSpecCompiler.Attach(component.Operation with { Parameters = parameters });
            indexById[component.Id] = output.Count;
            output.Add((component, operation));
            var childOwner = CardEffectRules.IsDependencyPrefix(component.Operation)
                             && component.Operation.Scope != OperationScope.ConditionalTrigger
                ? parentIndex
                : indexById[component.Id];
            foreach (var child in AllRoots(cardIndex, component.Id)) Visit(child, childOwner);
        }
        foreach (var root in AllRoots(cardIndex)) Visit(root, null);
        return output;
    }

    private List<(TrainingComponent Component, GeneratorOperation Operation)> FlattenProjected(int cardIndex,
        string movingId, string? targetParentId, string? beforeComponentId)
    {
        var moving = _components[movingId];
        var movedIds = Descendants(movingId).Select(component => component.Id)
            .Append(movingId).ToHashSet(StringComparer.Ordinal);
        var output = new List<(TrainingComponent, GeneratorOperation)>();
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);

        int ProjectedCard(TrainingComponent component) =>
            movedIds.Contains(component.Id) ? cardIndex : component.CardIndex;
        string? ProjectedParent(TrainingComponent component) =>
            component.Id == movingId ? targetParentId : component.ParentId;

        IReadOnlyList<TrainingComponent> Children(string? parentId)
        {
            var siblings = _components.Values
                .Where(component => component.Id != movingId
                                    && !IsImplicitSelector(component.Operation)
                                    && ProjectedCard(component) == cardIndex
                                    && ProjectedParent(component) == parentId)
                .OrderBy(component => component.Order)
                .ThenBy(component => component.Id, StringComparer.Ordinal)
                .ToList();
            if (parentId != targetParentId) return siblings;
            var insertion = beforeComponentId is null
                ? siblings.Count
                : siblings.FindIndex(component => component.Id == beforeComponentId);
            if (insertion < 0) insertion = siblings.Count;
            siblings.Insert(insertion, moving);
            return siblings;
        }

        void Visit(TrainingComponent component, int? parentIndex)
        {
            var parameters = component.Operation.Parameters
                .Where(pair => pair.Key != "triggerIndex")
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (parentIndex.HasValue) parameters["triggerIndex"] = parentIndex.Value;
            var operation = OperationRuntimeSpecCompiler.Attach(component.Operation with { Parameters = parameters });
            indexById[component.Id] = output.Count;
            output.Add((component, operation));
            var childOwner = CardEffectRules.IsDependencyPrefix(component.Operation)
                             && component.Operation.Scope != OperationScope.ConditionalTrigger
                ? parentIndex
                : indexById[component.Id];
            foreach (var child in Children(component.Id)) Visit(child, childOwner);
        }

        foreach (var root in Children(null)) Visit(root, null);
        return output;
    }

    private static CardTinkeringValidationOptions DragProjectionOptions() => new(
        AllowIncompleteTriggers: true, AllowOrphanSelectors: true, AllowMissingSelectors: true);

    private static IReadOnlyList<string> DragProjectionErrors(TrainingCard card,
        IReadOnlyList<(TrainingComponent Component, GeneratorOperation Operation)> flattened,
        CardTinkeringValidationOptions options)
    {
        var operations = flattened.Select(item => item.Operation).ToArray();
        return ValidateAssembly(card.Shell, operations, options).Errors
            .Where(error => !card.EntryValidationErrors.Contains(error)).ToArray();
    }

    private static string? SelectorSlot(GeneratorOperation operation) =>
        operation.Parameters.TryGetValue("slotIndex", out var slotIndex) && slotIndex > 0
            ? $"card{slotIndex}"
            : null;

    private void MoveUnchecked(TrainingComponent component, int targetCard, string? parentId)
    {
        var oldCard = component.CardIndex;
        component.CardIndex = targetCard;
        component.ParentId = parentId;
        component.Order = int.MaxValue;
        // A chip carries its nested effects. Moving the root therefore keeps the component subtree intact.
        foreach (var child in Descendants(component.Id)) child.CardIndex = targetCard;
        NormalizeOrders(oldCard);
        NormalizeOrders(targetCard);
    }

    private IEnumerable<TrainingComponent> Descendants(string id)
    {
        foreach (var child in _components.Values.Where(item => item.ParentId == id).ToArray())
        {
            yield return child;
            foreach (var descendant in Descendants(child.Id)) yield return descendant;
        }
    }

    private bool IsDescendant(string? possibleDescendant, string ancestor) => possibleDescendant is not null
        && Descendants(ancestor).Any(component => component.Id == possibleDescendant);

    private static GeneratorOperation StripParent(GeneratorOperation operation) => operation with
    {
        Parameters = operation.Parameters.Where(pair => pair.Key != "triggerIndex")
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
    };

    private void NormalizeOrders(int? onlyCard = null)
    {
        var groups = _components.Values.Where(component => onlyCard is null || component.CardIndex == onlyCard)
            .GroupBy(component => (component.CardIndex, component.ParentId));
        foreach (var group in groups)
        {
            var order = 0;
            foreach (var component in group.OrderBy(component => component.Order).ThenBy(component => component.Id,
                         StringComparer.Ordinal)) component.Order = order++;
        }
    }

    private SessionSnapshot Capture() => new(_components.Values.Select(component => new ComponentSnapshot(
            component.Id, component.Operation, component.Upgrades.ToArray(), component.Progress,
            component.CardIndex, component.ParentId, component.Order)).ToArray(),
        _cards.Select((card, index) => new CardIdentitySnapshot(index, card.Shell.Name,
            card.DraftIdentity, card.IdentityChanged)).ToArray());

    private void Restore(SessionSnapshot snapshot)
    {
        foreach (var identity in snapshot.Identities)
        {
            if ((uint)identity.CardIndex >= (uint)_cards.Count) continue;
            var card = _cards[identity.CardIndex];
            card.Shell = card.Shell with { Name = identity.Name };
            card.DraftIdentity = identity.Identity;
            card.IdentityChanged = identity.Changed;
        }
        _components.Clear();
        foreach (var item in snapshot.Components)
            _components.Add(item.Id, new TrainingComponent
            {
                Id = item.Id, Operation = OperationRuntimeSpecCompiler.Attach(item.Operation),
                Upgrades = item.Upgrades.ToList(), Progress = item.Progress,
                CardIndex = item.CardIndex, ParentId = item.ParentId, Order = item.Order
            });
        NormalizeOrders();
        _analysisCache.Clear();
    }

    internal static string LocalizedReason(string? reason) => IsChinese
        ? ChineseReason(reason)
        : EnglishReason(reason);

    private static string EnglishReason(string? reason) => reason switch
    {
        null => "Invalid component assembly.",
        var value when value.Contains("trigger", StringComparison.OrdinalIgnoreCase) =>
            "A trigger component needs a valid child effect.",
        var value when value.Contains("X resource", StringComparison.OrdinalIgnoreCase) =>
            "This component requires a different X resource.",
        var value when value.Contains("X components", StringComparison.OrdinalIgnoreCase) =>
            "X components can only be installed in a shell with the corresponding X cost.",
        var value when value.Contains("Attack shell", StringComparison.OrdinalIgnoreCase) =>
            "An Attack must contain at least one Damage component.",
        var value when value.Contains("dependency prefix", StringComparison.OrdinalIgnoreCase) =>
            "A condition/count component must directly contain a matching payoff.",
        var value when value.Contains("selected enemy", StringComparison.OrdinalIgnoreCase) =>
            "This component requires a selected enemy.",
        var value when value.Contains("reusable non-Power", StringComparison.OrdinalIgnoreCase) =>
            "A post-play self-cost component cannot be installed in a Power.",
        var value when value.Contains("Power", StringComparison.OrdinalIgnoreCase) =>
            "This component is incompatible with a Power shell.",
        var value when value.Contains("lightning-evoked", StringComparison.OrdinalIgnoreCase) =>
            "This effect must be placed under ‘Whenever a Lightning Orb is Evoked’.",
        var value when value.Contains("event-amount", StringComparison.OrdinalIgnoreCase) =>
            "This effect must be placed under the event trigger that supplies its amount.",
        var value when value.Contains("assembly invariant", StringComparison.OrdinalIgnoreCase) =>
            "This component is incompatible with the effect order or repeat rules at that destination.",
        var value when value.Contains("single-use", StringComparison.OrdinalIgnoreCase) =>
            "This effect cannot share a card with copy-this-card effects.",
        var value when value.Contains("more than one explicit card slot", StringComparison.OrdinalIgnoreCase) =>
            "A card can use only one explicit card-selection slot group.",
        var value when value.Contains("Attack-only card selector", StringComparison.OrdinalIgnoreCase) =>
            "This component requires a ‘Choose an Attack’ card slot.",
        var value when value.Contains("card selector", StringComparison.OrdinalIgnoreCase) =>
            "This component requires a matching card selector and slot.",
        var value when value.Contains("player-choice context", StringComparison.OrdinalIgnoreCase) =>
            "This component's trigger cannot provide a card-selection prompt.",
        var value when value.Contains("permanent damage growth", StringComparison.OrdinalIgnoreCase) =>
            "Permanent damage growth requires an earlier Damage component.",
        var value when value.Contains("permanent Block growth", StringComparison.OrdinalIgnoreCase) =>
            "Permanent Block growth requires an earlier Block component.",
        var value when value.Contains("earlier Damage", StringComparison.OrdinalIgnoreCase) =>
            "This component requires an earlier Damage component to reference.",
        var value when value.Contains("earlier compatible Exhaust", StringComparison.OrdinalIgnoreCase) =>
            "This component requires an earlier component that actually Exhausts cards.",
        var value when value.Contains("earlier discard", StringComparison.OrdinalIgnoreCase) =>
            "This component requires an earlier discard component.",
        var value when value.Contains("Draw 1", StringComparison.OrdinalIgnoreCase) =>
            "This condition must immediately follow an unnested ‘Draw 1’ component.",
        var value when value.Contains("retaliation", StringComparison.OrdinalIgnoreCase) =>
            "Retaliation damage can only be placed under ‘Whenever you are Attacked this turn’.",
        var value when value.Contains("self-cost", StringComparison.OrdinalIgnoreCase) =>
            "The shell cost is incompatible with this self-cost component.",
        var value when value.Contains("numeric effect", StringComparison.OrdinalIgnoreCase) =>
            "This multiplier/modifier has no Damage or Block component to affect.",
        var value when value.Contains("play-this-card", StringComparison.OrdinalIgnoreCase)
                       || value.Contains("return-to-hand", StringComparison.OrdinalIgnoreCase) =>
            "This card-movement component is missing its matching trigger or earlier effect.",
        _ => "Invalid component order or assembly."
    };

    private static string ChineseReason(string? reason) => reason switch
    {
        null => "组件组合不合法。",
        var value when value.Contains("trigger", StringComparison.OrdinalIgnoreCase) => "触发组件需要合法的下级效果。",
        var value when value.Contains("X resource", StringComparison.OrdinalIgnoreCase) => "该组件需要不同的 X 资源。",
        var value when value.Contains("X components", StringComparison.OrdinalIgnoreCase) =>
            "X组件只能安装在对应的X费用牌壳中。",
        var value when value.Contains("Attack shell", StringComparison.OrdinalIgnoreCase) =>
            "攻击牌必须至少包含一个伤害组件。",
        var value when value.Contains("dependency prefix", StringComparison.OrdinalIgnoreCase) =>
            "条件/计数组件必须直接包含与之匹配的后续效果。",
        var value when value.Contains("selected enemy", StringComparison.OrdinalIgnoreCase) => "该组件需要选中的敌人。",
        var value when value.Contains("reusable non-Power", StringComparison.OrdinalIgnoreCase) =>
            "打出后调整本牌费用的组件不能安装在能力牌上。",
        var value when value.Contains("Power", StringComparison.OrdinalIgnoreCase) => "该组件与能力牌类型不兼容。",
        var value when value.Contains("lightning-evoked", StringComparison.OrdinalIgnoreCase) =>
            "该效果必须放在“闪电充能球被激发时”下面。",
        var value when value.Contains("event-amount", StringComparison.OrdinalIgnoreCase) =>
            "该效果必须放在提供对应数值的事件触发器下面。",
        var value when value.Contains("assembly invariant", StringComparison.OrdinalIgnoreCase) =>
            "该组件与目标位置的效果顺序或重复触发限制不兼容。",
        var value when value.Contains("single-use", StringComparison.OrdinalIgnoreCase) =>
            "该效果不能和复制本体效果位于同一张卡。",
        var value when value.Contains("more than one explicit card slot", StringComparison.OrdinalIgnoreCase) =>
            "一张卡只能使用一组明确的卡牌选择槽位。",
        var value when value.Contains("Attack-only card selector", StringComparison.OrdinalIgnoreCase) =>
            "该组件需要“选择一张攻击牌”的卡牌槽位。",
        var value when value.Contains("card selector", StringComparison.OrdinalIgnoreCase) =>
            "该组件需要与之匹配的卡牌选择组件和槽位。",
        var value when value.Contains("player-choice context", StringComparison.OrdinalIgnoreCase) =>
            "该组件所在的触发器不能提供玩家选牌界面。",
        var value when value.Contains("permanent damage growth", StringComparison.OrdinalIgnoreCase) =>
            "永久伤害成长前必须已有伤害组件。",
        var value when value.Contains("permanent Block growth", StringComparison.OrdinalIgnoreCase) =>
            "永久格挡成长前必须已有格挡组件。",
        var value when value.Contains("earlier Damage", StringComparison.OrdinalIgnoreCase) =>
            "该组件前必须已有可供其引用的伤害组件。",
        var value when value.Contains("earlier compatible Exhaust", StringComparison.OrdinalIgnoreCase) =>
            "该组件前必须已有能够实际消耗卡牌的组件。",
        var value when value.Contains("earlier discard", StringComparison.OrdinalIgnoreCase) =>
            "该组件前必须已有弃牌组件。",
        var value when value.Contains("Draw 1", StringComparison.OrdinalIgnoreCase) =>
            "该条件必须紧跟在未嵌套的“抽1张牌”组件之后。",
        var value when value.Contains("retaliation", StringComparison.OrdinalIgnoreCase) =>
            "反击伤害只能放在“本回合每当受到攻击时”下面。",
        var value when value.Contains("self-cost", StringComparison.OrdinalIgnoreCase) =>
            "该牌壳的费用与调整本牌费用的组件不兼容。",
        var value when value.Contains("numeric effect", StringComparison.OrdinalIgnoreCase) =>
            "这个倍率/修正组件缺少它所作用的伤害或格挡组件。",
        var value when value.Contains("play-this-card", StringComparison.OrdinalIgnoreCase)
                           || value.Contains("return-to-hand", StringComparison.OrdinalIgnoreCase) =>
            "该卡牌移动组件缺少匹配的触发器或先行效果。",
        _ => "组件顺序或组合不合法。"
    };
}
