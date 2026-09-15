using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoAnthony;
using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace AutoAnthonyCardTinkering;

public sealed class CardTinkeringSaveCarrier : ModifierModel
{
    [SavedProperty] public string CardTinkeringRunPayload { get; set; } = string.Empty;
    // This property is written onto generated cards manually. Declaring it here registers its network property ID.
    [SavedProperty] public string CardTinkeringCardPayload { get; set; } = string.Empty;
    public override bool ShouldReceiveCombatHooks => false;
}

internal sealed record ComponentProgress(
    bool Used = false,
    int PermanentDamage = 0,
    int PermanentBlock = 0,
    bool Upgraded = false,
    GeneratorOperation? BaseOperation = null,
    IReadOnlyList<CardUpgradeEffect>? UpgradeEffects = null,
    OperationRuntimeSpec? BaseRuntimeSpec = null,
    OperationLocalizedText? BaseLocalizedText = null,
    IReadOnlyList<string?>? UpgradeValueSlots = null,
    bool? RequiresAttackCardSlot = null);
internal sealed record TinkeredCardState(int Capacity, bool Eternal,
    IReadOnlyList<ComponentProgress> Components, double? CleanCapacity = null,
    int CapacityModel = 0, bool Invalid = false,
    int? CapacityX0 = null, int? CapacityX3 = null);
internal sealed record StoredComponent(GeneratorOperation Operation,
    IReadOnlyList<CardUpgradeEffect> Upgrades, ComponentProgress Progress);
internal sealed record PlayerTinkeringRunState(ulong PlayerId,
    IReadOnlyList<StoredComponent> Backpack, IReadOnlyList<int> CompletedTrainingActs);
internal sealed record TinkeringRunState(IReadOnlyList<StoredComponent> Backpack,
    IReadOnlyList<int> CompletedTrainingActs,
    IReadOnlyList<PlayerTinkeringRunState>? Players = null);

internal static class TinkeringValue
{
    internal static CardTinkeringCardAnalysis Evaluate(GeneratedCard card) =>
        CardTinkeringApi.Analyze(card, new CardTinkeringEvaluationOptions(
            ChaosRunDefinitions.ActiveNumericBalanceOptimization));

    internal static ProductionCardPricing Pricing(GeneratedCard card)
    {
        var balancedValues = ChaosRunDefinitions.ActiveNumericBalanceOptimization;
        var budget = CardTinkeringApi.Analyze(card,
            new CardTinkeringEvaluationOptions(balancedValues)).Budget;
        return ProductionCardPricing.From(budget);
    }

    internal static bool TryXEndpointPricing(GeneratedCard card,
        out ProductionCardPricing x0, out ProductionCardPricing x3)
    {
        var balancedValues = ChaosRunDefinitions.ActiveNumericBalanceOptimization;
        if (!CardTinkeringApi.IsVariableX(card))
        {
            x0 = x3 = new ProductionCardPricing(0d, 0d, 1d, 0d, 0d);
            return false;
        }
        x0 = ProductionCardPricing.From(CardTinkeringApi.AnalyzeAtX(card, 0, balancedValues).Budget);
        x3 = ProductionCardPricing.From(CardTinkeringApi.AnalyzeAtX(card, 3, balancedValues).Budget);
        return true;
    }

    internal static int NetValueCeiling(ProductionCardPricing pricing) =>
        Math.Max(0, CeilingToInt(pricing.NetValue));

    private static int CeilingToInt(double value)
    {
        if (double.IsNaN(value)) return 0;
        if (value >= int.MaxValue) return int.MaxValue;
        if (value <= int.MinValue) return int.MinValue;
        return (int)Math.Ceiling(value);
    }
}

internal static class TinkeringStateStore
{
    private const int CardSchema = 2;
    private const int RunSchema = 3;
    internal const int NetValueCapacityModel = 1;
    internal const int XEndpointCapacityModel = 2;
    internal const int NumericRandomNetCapacityModel = 3;
    internal const int NumericRandomXEndpointCapacityModel = 4;
    private sealed record CardEnvelope(int Schema, TinkeredCardState State);
    private sealed record StoredComponentPayload(GeneratorOperation Operation,
        IReadOnlyList<CardUpgradeEffect> Upgrades, ComponentProgress Progress,
        OperationRuntimeSpec RuntimeSpec, OperationLocalizedText? LocalizedText,
        IReadOnlyList<string?> UpgradeValueSlots);
    private sealed record PlayerRunPayload(ulong PlayerId,
        IReadOnlyList<StoredComponentPayload> Backpack, IReadOnlyList<int> CompletedTrainingActs);
    private sealed record RunEnvelope(int Schema, IReadOnlyList<StoredComponentPayload> Backpack,
        IReadOnlyList<int> CompletedTrainingActs, IReadOnlyList<PlayerRunPayload>? Players = null);
    private sealed record PlayerRunEnvelope(int Schema, IReadOnlyList<StoredComponentPayload> Backpack,
        IReadOnlyList<int> CompletedTrainingActs);
    private static readonly ConditionalWeakTable<ChaosCardModel, Holder> CardStates = new();
    private static readonly ConditionalWeakTable<ChaosCardModel, PreviewMarker> EditorPreviews = new();
    private static readonly ConditionalWeakTable<RunState, RunHolder> RunStates = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class Holder(TinkeredCardState state, bool growthReconciled)
    {
        public TinkeredCardState State = state;
        public bool GrowthReconciled = growthReconciled;
    }
    private sealed class PreviewMarker { }
    private sealed class RunHolder(TinkeringRunState state) { public TinkeringRunState State = state; }

    /// <summary>
    /// Editor card faces are mutable card models so the native renderer can display their real cost, keywords and
    /// DynamicVars. They are not gameplay cards: no inspection/update path is allowed to consume a run-once effect
    /// from them, even if a base-game or third-party preview hook happens to enter the operation executor.
    /// </summary>
    internal static void MarkEditorPreview(ChaosCardModel card)
    {
        EditorPreviews.Remove(card);
        EditorPreviews.Add(card, new PreviewMarker());
    }

    internal static bool IsEditorPreview(ChaosCardModel card) => EditorPreviews.TryGetValue(card, out _);

    internal static TinkeredCardState Ensure(ChaosCardModel card)
    {
        if (CardStates.TryGetValue(card, out var existing))
        {
            existing.State = Normalize(card, existing.State);
            if (!existing.GrowthReconciled && card.CombatState is null && card.DeckVersion is null)
            {
                existing.State = ReconcilePermanentGrowth(card, existing.State);
                existing.GrowthReconciled = true;
            }
            return existing.State;
        }
        if (card.DeckVersion is ChaosCardModel deck && CardStates.TryGetValue(deck, out var deckHolder))
        {
            var clone = Normalize(card, deckHolder.State);
            Set(card, clone);
            return clone;
        }

        var value = TinkeringValue.Evaluate(card.Generated);
        var numericRandom = UsesNumericRandomCapacity(card.Generated);
        var operations = card.Generated.Operations;
        var progress = operations.Select((operation, index) =>
        {
            var componentEffects = card.Generated.Upgrade?.Effects
                .Where(effect => effect.OperationIndex == index)
                .Where(effect => !IsShellOwnedPowerInnate(card.Generated, effect))
                .Select(effect => effect with { OperationIndex = 0 }).ToArray() ?? [];
            return WithComponentMetadata(operation, componentEffects,
                new ComponentProgress(Upgraded: card.IsUpgraded && componentEffects.Length > 0));
        }).ToArray();
        var damageIndex = operations.ToList().FindIndex(TrainingSession.IsPermanentDamageGrowth);
        if (damageIndex >= 0 && card.ExtraDamage != 0)
            progress[damageIndex] = progress[damageIndex] with { PermanentDamage = card.ExtraDamage };
        var blockIndex = operations.ToList().FindIndex(TrainingSession.IsPermanentBlockGrowth);
        if (blockIndex >= 0 && card.ExtraBlock != 0)
            progress[blockIndex] = progress[blockIndex] with { PermanentBlock = card.ExtraBlock };

        TinkeredCardState created;
        if (TinkeringValue.TryXEndpointPricing(card.Generated, out var x0, out var x3))
        {
            var exceedsX = ExceedsUpper(x0) || ExceedsUpper(x3);
            var eternal = !numericRandom && IsIntentionalUpperBoundException(card.Generated) && exceedsX;
            var capacityX0 = TinkeringValue.NetValueCeiling(x0);
            var capacityX3 = TinkeringValue.NetValueCeiling(x3);
            if (numericRandom)
            {
                var multiplier = SampleNumericRandomCapacityMultiplier(CreateCapacityRng(card));
                capacityX0 = NumericRandomCapacity(x0, multiplier);
                capacityX3 = NumericRandomCapacity(x3, multiplier);
            }
            else if (!eternal && !exceedsX)
            {
                var rng = CreateCapacityRng(card);
                capacityX0 = SampleCapacity(rng, x0);
                capacityX3 = SampleCapacity(rng, x3);
            }
            created = new TinkeredCardState(capacityX3, eternal, progress,
                CapacityModel: numericRandom ? NumericRandomXEndpointCapacityModel : XEndpointCapacityModel,
                CapacityX0: capacityX0, CapacityX3: capacityX3);
        }
        else
        {
            var pricing = TinkeringValue.Pricing(card.Generated);
            var capacity = numericRandom
                ? SampleNumericRandomCapacity(CreateCapacityRng(card), pricing)
                : TinkeringValue.NetValueCeiling(pricing);
            if (!numericRandom && !value.ExceedsOrdinaryUpperBound)
                capacity = SampleCapacity(CreateCapacityRng(card), pricing);
            created = new TinkeredCardState(capacity,
                !numericRandom && IsIntentionalUpperBoundException(card.Generated)
                               && value.ExceedsOrdinaryUpperBound, progress,
                CapacityModel: numericRandom ? NumericRandomNetCapacityModel : NetValueCapacityModel);
        }
        Set(card, created);
        return created;
    }

    internal static bool TryGet(ChaosCardModel card, out TinkeredCardState state)
    {
        if (CardStates.TryGetValue(card, out var holder))
        {
            state = holder.State;
            return true;
        }
        state = null!;
        return false;
    }

    /// <summary>
    /// A pool-level balance adjustment changes the clean definition underneath a card. Cards with an actual
    /// per-instance editor definition are excluded by Auto Anthony; for an untouched card whose editor metadata was
    /// merely initialized, rebuild that metadata from the adjusted definition instead of retaining stale component
    /// indexes and capacity.
    /// </summary>
    internal static void RebaseAfterBalanceAdjustment(ChaosCardModel card)
    {
        if (!CardStates.Remove(card)) return;
        _ = Ensure(card);
    }

    internal static void Set(ChaosCardModel card, TinkeredCardState state)
    {
        CardStates.Remove(card);
        CardStates.Add(card, new Holder(Normalize(card, state), growthReconciled: true));
    }

    internal static void SetLoaded(ChaosCardModel card, TinkeredCardState state)
    {
        CardStates.Remove(card);
        CardStates.Add(card, new Holder(Normalize(card, state), growthReconciled: false));
    }

    internal static void SetRuntimeClone(ChaosCardModel card, TinkeredCardState state)
    {
        CardStates.Remove(card);
        CardStates.Add(card, new Holder(state.Invalid ? state : Normalize(card, state), growthReconciled: true));
    }

    internal static TinkeringRunState GetRun(RunState runState) =>
        RunStates.GetValue(runState, _ => new RunHolder(new TinkeringRunState([], []))).State;

    internal static void SetRun(RunState runState, TinkeringRunState state)
    {
        RunStates.Remove(runState);
        RunStates.Add(runState, new RunHolder(ClearSingleUseProgress(state)));
    }

    internal static PlayerTinkeringRunState GetPlayerRun(RunState runState, Player player)
    {
        var state = GetRun(runState);
        var playerState = state.Players?.FirstOrDefault(item => item.PlayerId == player.NetId);
        if (playerState is not null) return playerState;
        // Schema 1/2 saves only existed while Training Rooms were single-player-only. Preserve that state for a
        // single-player run, but do not leak one player's backpack into every player of a multiplayer run.
        return runState.Players.Count == 1
            ? new PlayerTinkeringRunState(player.NetId, state.Backpack, state.CompletedTrainingActs)
            : new PlayerTinkeringRunState(player.NetId, [], []);
    }

    internal static void SetPlayerRun(RunState runState, Player player, PlayerTinkeringRunState playerState)
    {
        playerState = ClearSingleUseProgress(playerState with { PlayerId = player.NetId });
        var current = GetRun(runState);
        if (runState.Players.Count == 1)
        {
            SetRun(runState, new TinkeringRunState(playerState.Backpack,
                playerState.CompletedTrainingActs));
            return;
        }

        var players = current.Players?.Where(item => item.PlayerId != player.NetId).ToList() ?? [];
        players.Add(playerState);
        SetRun(runState, current with
        {
            Backpack = [],
            CompletedTrainingActs = [],
            Players = players.OrderBy(item => item.PlayerId).ToArray()
        });
    }

    internal static bool HasCompleted(RunState runState, int actIndex) =>
        runState.Players.Count > 0 && runState.Players.All(player =>
            GetPlayerRun(runState, player).CompletedTrainingActs.Contains(actIndex));

    internal static void Complete(RunState runState, Player player, int actIndex,
        IReadOnlyList<StoredComponent> backpack)
    {
        var current = GetPlayerRun(runState, player);
        SetPlayerRun(runState, player, current with
        {
            Backpack = backpack,
            CompletedTrainingActs = current.CompletedTrainingActs.Append(actIndex).Distinct().Order().ToArray()
        });
    }

    internal static void UpdateBackpack(RunState runState, Player player,
        IReadOnlyList<StoredComponent> backpack)
    {
        var current = GetPlayerRun(runState, player);
        SetPlayerRun(runState, player, current with { Backpack = backpack });
    }

    internal static bool CanExecuteSingleUse(ChaosCardModel card, int operationIndex)
    {
        if (IsEditorPreview(card)) return true;
        if (!IsLiveCombatCard(card)) return true;
        var state = Ensure(card);
        if ((uint)operationIndex >= (uint)state.Components.Count) return true;
        if (!TrainingSession.IsSingleUse(card.Generated.Operations[operationIndex])) return true;
        return !state.Components[operationIndex].Used;
    }

    internal static void MarkSingleUseExecuted(ChaosCardModel card, int operationIndex)
    {
        if (IsEditorPreview(card)
            || !IsLiveCombatCard(card)
            || (uint)operationIndex >= (uint)card.Generated.Operations.Count
            || !TrainingSession.IsSingleUse(card.Generated.Operations[operationIndex])) return;
        var state = Ensure(card);
        if ((uint)operationIndex >= (uint)state.Components.Count) return;
        // Once-per-combat state belongs exclusively to the live combat card. A combat clone inherits the state in
        // RunCloneStatePatch.Copy, but the immutable DeckVersion must never be mutated: it is the clean source for
        // the next combat and for every out-of-combat editor/save path.
        UpdateProgress(card, operationIndex, progress => progress with { Used = true });
    }

    internal static bool HasUnavailableSingleUse(ChaosCardModel card)
    {
        if (IsEditorPreview(card) || !IsLiveCombatCard(card) || !TryGet(card, out var state)) return false;
        for (var index = 0; index < state.Components.Count && index < card.Generated.Operations.Count; index++)
            if (state.Components[index].Used && TrainingSession.IsSingleUse(card.Generated.Operations[index]))
                return true;
        return false;
    }

    internal static bool IsLiveCombatCard(ChaosCardModel card)
    {
        if (!card.IsMutable) return false;
        var owner = card.Owner;
        if (owner is null || card.CombatState is null && owner.Creature.CombatState is null) return false;
        if (card.DeckVersion is not null || card.CombatState is not null) return true;
        // Trigger proxies and combat-only created cards can be detached from a pile while an operation resolves.
        // The actual deck model has neither DeckVersion nor CombatState and is the one object that must stay clean.
        return !owner.Deck.Cards.Any(candidate => ReferenceEquals(candidate, card));
    }

    internal static void RecordPermanentGrowth(ChaosCardModel card, int operationIndex,
        int damage, int block)
    {
        if ((uint)operationIndex >= (uint)card.Generated.Operations.Count) return;
        var operation = card.Generated.Operations[operationIndex];
        if (!TrainingSession.IsPermanentGrowth(operation)) return;
        if (!TrainingSession.IsPermanentDamageGrowth(operation)) damage = 0;
        if (!TrainingSession.IsPermanentBlockGrowth(operation)) block = 0;
        if (damage <= 0 && block <= 0) return;
        UpdateProgress(card, operationIndex, progress => progress with
        {
            PermanentDamage = progress.PermanentDamage + Math.Max(0, damage),
            PermanentBlock = progress.PermanentBlock + Math.Max(0, block)
        });
        if (card.DeckVersion is ChaosCardModel deck && !ReferenceEquals(deck, card))
            UpdateProgress(deck, operationIndex, progress => progress with
            {
                PermanentDamage = progress.PermanentDamage + Math.Max(0, damage),
                PermanentBlock = progress.PermanentBlock + Math.Max(0, block)
            });
    }

    /// <summary>
    /// Reconcile a loaded deck card before the core operation mutates its aggregate ExtraDamage/ExtraBlock cache.
    /// Otherwise the first permanent gain after loading can be mistaken for legacy, unassigned growth and then be
    /// recorded a second time on the owning component. The deck copy is authoritative and must be prepared first.
    /// </summary>
    internal static void PreparePermanentGrowth(ChaosCardModel card)
    {
        if (card.DeckVersion is ChaosCardModel deck && !ReferenceEquals(deck, card))
            _ = Ensure(deck);
        _ = Ensure(card);
    }

    internal static ComponentProgress WithComponentMetadata(GeneratorOperation operation,
        IReadOnlyList<CardUpgradeEffect> upgrades, ComponentProgress progress)
    {
        var attached = OperationRuntimeSpecCompiler.Attach(operation);
        return progress with
        {
            BaseOperation = attached,
            UpgradeEffects = upgrades.ToArray(),
            BaseRuntimeSpec = OperationRuntimeSpecCompiler.RequireStructured(attached),
            BaseLocalizedText = attached.LocalizedText,
            UpgradeValueSlots = upgrades.Select(effect => effect.ValueSlotId).ToArray()
        };
    }

    internal static GeneratorOperation BaseOperation(GeneratedCard card, int index, ComponentProgress progress)
    {
        var operation = progress.BaseOperation ?? card.Operations[index];
        return OperationRuntimeSpecCompiler.Attach(operation with
        {
            RuntimeSpec = progress.BaseRuntimeSpec ?? operation.RuntimeSpec,
            LocalizedText = progress.BaseLocalizedText ?? operation.LocalizedText
        });
    }

    internal static IReadOnlyList<CardUpgradeEffect> ComponentUpgrades(GeneratedCard card, int index,
        ComponentProgress progress)
    {
        var effects = progress.UpgradeEffects
            ?? card.Upgrade?.Effects.Where(effect => effect.OperationIndex == index)
                .Select(effect => effect with { OperationIndex = 0 }).ToArray()
            ?? [];
        var slots = progress.UpgradeValueSlots;
        if (slots is not null && slots.Count == effects.Count)
            effects = effects.Select((effect, effectIndex) =>
                effect with { ValueSlotId = slots[effectIndex] }).ToArray();
        return effects.Where(effect => !IsShellOwnedPowerInnate(card, effect)).ToArray();
    }

    /// <summary>
    /// Innate gained by upgrading a Power is a property of that card shell. Some native-shaped component profiles
    /// retain an operation index only to explain where the candidate came from; the editor must not interpret that
    /// provenance as component ownership. Recover the original plan as well so a definition saved by an older editor
    /// build cannot make the Power itself lose its Innate upgrade after its component layout changed.
    /// </summary>
    internal static GeneratedCard NormalizeShellUpgradeOwnership(GeneratedCard shell,
        GeneratedCard? originalShell = null)
    {
        if (shell.Type != GeneratedCardType.Power || shell.Upgrade is not { } upgrade) return shell;
        var hasInnate = upgrade.Effects.Any(effect => effect.Kind == CardUpgradeKind.GrantInnate)
                        || originalShell?.Upgrade?.Effects.Any(effect =>
                            effect.Kind == CardUpgradeKind.GrantInnate) == true;
        if (!hasInnate) return shell;
        var effects = upgrade.Effects.Where(effect => effect.Kind != CardUpgradeKind.GrantInnate)
            .Append(new CardUpgradeEffect(CardUpgradeKind.GrantInnate))
            .ToArray();
        if (upgrade.Effects.SequenceEqual(effects)) return shell;
        return shell with { Upgrade = upgrade with { Effects = effects } };
    }

    private static bool IsShellOwnedPowerInnate(GeneratedCard card, CardUpgradeEffect effect) =>
        card.Type == GeneratedCardType.Power && effect.Kind == CardUpgradeKind.GrantInnate;

    internal static GeneratorOperation EffectiveOperation(GeneratorOperation operation,
        IReadOnlyList<CardUpgradeEffect> upgrades, ComponentProgress progress) =>
        !progress.Upgraded || upgrades.Count == 0
            ? operation
            : CardUpgradeGenerator.ApplyEffectsToOperations([operation], upgrades.Select(effect =>
                effect with { OperationIndex = 0 }).ToArray())[0];

    /// <summary>
    /// Component upgrade state is projected independently from the shell. When component and shell states match,
    /// the normal upgrade plan remains available (including the native upgrade preview); when they differ, the
    /// component is either baked upgraded or held at base. The pre-upgrade operation and full plan always remain in
    /// ComponentProgress for valuation and later editing.
    /// </summary>
    internal static GeneratedCard MaterializeComponentUpgrades(GeneratedCard shell,
        IReadOnlyList<ComponentProgress> components, bool shellIsUpgraded)
    {
        shell = NormalizeShellUpgradeOwnership(shell);
        var (operations, runtimeEffects) = ProjectComponentUpgrades(shell, components, shellIsUpgraded);
        var coreValidation = CardTinkeringApi.Validate(shell, operations);
        if (coreValidation.IsValid)
            return CardTinkeringApi.Rebuild(shell, operations, runtimeEffects);
        // Native self-contained choice/random components and the editor's reviewed delayed-return form are valid
        // runtime recipes even though the stricter random-generation validator rejects their standalone shape.
        // Only bypass Rebuild when every remaining error is accepted by our operation-contract validator; all real
        // assembly errors continue down the throwing core path.
        if (TrainingSession.ValidateAssembly(shell, operations).IsValid)
            return RebuildWithoutValidation(shell, operations, runtimeEffects);
        return CardTinkeringApi.Rebuild(shell, operations, runtimeEffects);
    }

    /// <summary>
    /// Builds a visual-only card definition without applying final assembly validation. The editor deliberately
    /// permits incomplete intermediate states, so its live card face must not disappear merely because a trigger
    /// is awaiting a child, a target-dependent chip is being moved, or the draft currently exceeds capacity.
    /// Commit still uses MaterializeComponentUpgrades and CardTinkeringApi.Rebuild above.
    /// </summary>
    internal static GeneratedCard MaterializeComponentUpgradesForPreview(GeneratedCard shell,
        IReadOnlyList<ComponentProgress> components, bool shellIsUpgraded)
    {
        shell = NormalizeShellUpgradeOwnership(shell);
        var (operations, runtimeEffects) = ProjectComponentUpgrades(shell, components, shellIsUpgraded);
        return RebuildWithoutValidation(shell, operations, runtimeEffects);
    }

    private static GeneratedCard RebuildWithoutValidation(GeneratedCard shell,
        IReadOnlyList<GeneratorOperation> operations, IReadOnlyList<CardUpgradeEffect> runtimeEffects)
    {
        var retainedShellEffects = shell.Upgrade?.Effects
            .Where(effect => effect.OperationIndex is null).ToArray() ?? [];
        var effects = retainedShellEffects.Concat(runtimeEffects).ToArray();
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
            Operations = operations.ToArray(),
            ChineseDescription = CardDescriptionRenderer.Render(operations),
            EnglishDescription = EnglishCardDescriptionRenderer.Render(operations),
            Upgrade = upgrade
        });
    }

    private static (GeneratorOperation[] Operations, CardUpgradeEffect[] RuntimeEffects)
        ProjectComponentUpgrades(GeneratedCard shell, IReadOnlyList<ComponentProgress> components,
            bool shellIsUpgraded)
    {
        var runtimeEffects = new List<CardUpgradeEffect>();
        var operations = shell.Operations.Select((operation, index) =>
        {
            if ((uint)index >= (uint)components.Count) return operation;
            var progress = components[index];
            var basis = BaseOperation(shell, index, progress);
            var upgrades = ComponentUpgrades(shell, index, progress);
            if (progress.Upgraded == shellIsUpgraded)
            {
                runtimeEffects.AddRange(upgrades.Select(effect => effect with { OperationIndex = index }));
                return basis;
            }
            return progress.Upgraded ? EffectiveOperation(basis, upgrades, progress) : basis;
        }).ToArray();
        return (operations, runtimeEffects.ToArray());
    }

    internal static void SetInstalledComponentsUpgraded(ChaosCardModel card, bool upgraded)
    {
        if (!CardStates.TryGetValue(card, out var holder)) return;
        var state = holder.State;
        if (state.Invalid && card.CombatState is not null)
        {
            var runtimeComponents = state.Components.Select(progress => progress with
            {
                Upgraded = upgraded && progress.UpgradeEffects is { Count: > 0 }
            }).ToArray();
            SetRuntimeClone(card, state with { Components = runtimeComponents });
            return;
        }
        var components = state.Components.Select((progress, index) =>
        {
            var effects = ComponentUpgrades(card.Generated, index, progress);
            return progress with { Upgraded = upgraded && effects.Count > 0 };
        }).ToArray();
        var updated = state with { Components = components };
        var shell = NormalizeShellUpgradeOwnership(card.Generated, card.Definition.Card);
        card.ApplyTinkeredDefinition(state.Invalid
            ? MaterializeComponentUpgradesForPreview(shell, components, card.IsUpgraded)
            : MaterializeComponentUpgrades(shell, components, card.IsUpgraded));
        Set(card, updated);
    }

    private static void UpdateProgress(ChaosCardModel card, int index,
        Func<ComponentProgress, ComponentProgress> update)
    {
        var state = Ensure(card);
        if ((uint)index >= (uint)state.Components.Count) return;
        var components = state.Components.ToArray();
        components[index] = update(components[index]);
        Set(card, state with { Components = components });
    }

    private static TinkeredCardState Normalize(ChaosCardModel card, TinkeredCardState state)
    {
        var normalized = Enumerable.Range(0, card.Generated.Operations.Count)
            .Select(index =>
            {
                var progress = index < state.Components.Count ? state.Components[index] : new ComponentProgress();
                var upgrades = ComponentUpgrades(card.Generated, index, progress);
                if (progress.BaseOperation is null && card.IsUpgraded && upgrades.Count > 0)
                    progress = progress with { Upgraded = true };
                else if (upgrades.Count == 0 && progress.Upgraded)
                    progress = progress with { Upgraded = false };
                return WithComponentMetadata(BaseOperation(card.Generated, index, progress), upgrades, progress);
            }).ToArray();
        // Saved cards may become invalid when the base mod adds a new assembly rule. Normalization runs during
        // load, so it must never call Rebuild (which rejects that card and drops the entire editor payload).
        // Materialize the same base, pre-upgrade projection without validation, then let the public API below be
        // the single authority for both valuation and structural validity.
        var valuationDefinition = MaterializeComponentUpgradesForPreview(card.Generated,
            normalized.Select(progress => progress with { Upgraded = false }).ToArray(),
            shellIsUpgraded: false);
        var evaluation = TinkeringValue.Evaluate(valuationDefinition);
        var pricing = TinkeringValue.Pricing(valuationDefinition);
        // Never let an API-only invariant disable a definition accepted and emitted by the base generator. Once a
        // per-card tinkering payload exists, the editor owns the assembly and the complete current API applies.
        var violatesCoreAssembly = card.HasTinkeredDefinition && !TrainingSession.ValidateAssembly(
            valuationDefinition, valuationDefinition.Operations).IsValid;
        var numericRandom = UsesNumericRandomCapacity(valuationDefinition)
                            || IsNumericRandomCapacityModel(state.CapacityModel);
        // Re-evaluate legacy/generated cards when balance settings change. An intentionally saved invalid draft is
        // different: it must remain editable next time instead of being irreversibly promoted to Eternal.
        if (TinkeringValue.TryXEndpointPricing(valuationDefinition, out var x0, out var x3))
        {
            if (numericRandom)
            {
                var hasNumericCapacity = state.CapacityModel == NumericRandomXEndpointCapacityModel
                                         && state.CapacityX0 is not null && state.CapacityX3 is not null;
                var multiplier = hasNumericCapacity ? 0 : SampleNumericRandomCapacityMultiplier(CreateCapacityRng(card));
                var numericCapacityX0 = hasNumericCapacity
                    ? Math.Max(0, state.CapacityX0!.Value)
                    : NumericRandomCapacity(x0, multiplier);
                var numericCapacityX3 = hasNumericCapacity
                    ? Math.Max(0, state.CapacityX3!.Value)
                    : NumericRandomCapacity(x3, multiplier);
                var numericInvalid = violatesCoreAssembly || !TinkeringSettings.IgnoreCapacityLimit
                    && (x0.NetValue > numericCapacityX0 + 0.0001d
                        || x3.NetValue > numericCapacityX3 + 0.0001d);
                if (state.Components.SequenceEqual(normalized) && state.Capacity == numericCapacityX3
                    && !state.Eternal && state.CapacityModel == NumericRandomXEndpointCapacityModel
                    && state.CapacityX0 == numericCapacityX0 && state.CapacityX3 == numericCapacityX3
                    && state.Invalid == numericInvalid && state.CleanCapacity is null) return state;
                var numericXResult = state with
                {
                    Capacity = numericCapacityX3,
                    Eternal = false,
                    Invalid = numericInvalid,
                    Components = normalized,
                    CleanCapacity = null,
                    CapacityModel = NumericRandomXEndpointCapacityModel,
                    CapacityX0 = numericCapacityX0,
                    CapacityX3 = numericCapacityX3
                };
                if (CardStates.TryGetValue(card, out var numericXHolder)) numericXHolder.State = numericXResult;
                return numericXResult;
            }

            var exceedsX = ExceedsUpper(x0) || ExceedsUpper(x3);
            // Eternal is assigned only when the card is first created. Moving a special component must not turn a
            // draft into an irreversible Eternal shell; this condition also clears legacy false-positive locks.
            var eternalX = state.Eternal && IsIntentionalUpperBoundException(valuationDefinition) && exceedsX;
            var hasEndpointCapacity = state.CapacityModel == XEndpointCapacityModel
                                      && state.CapacityX0 is not null && state.CapacityX3 is not null;
            var rng = hasEndpointCapacity || eternalX || state.Invalid || exceedsX
                ? null : CreateCapacityRng(card);
            var capacityX0 = state.Invalid
                ? Math.Max(0, state.CapacityX0 ?? state.Capacity)
                : NormalizeEndpointCapacity(state.CapacityX0, state.Capacity, x0,
                    hasEndpointCapacity, eternalX, rng);
            var capacityX3 = state.Invalid
                ? Math.Max(0, state.CapacityX3 ?? state.Capacity)
                : NormalizeEndpointCapacity(state.CapacityX3, state.Capacity, x3,
                    hasEndpointCapacity, eternalX, rng);
            var invalidX = !eternalX && (violatesCoreAssembly
                || !TinkeringSettings.IgnoreCapacityLimit
                && (x0.NetValue > capacityX0 + 0.0001d
                    || x3.NetValue > capacityX3 + 0.0001d));
            if (state.Components.SequenceEqual(normalized) && state.Capacity == capacityX3
                && state.Eternal == eternalX && state.CapacityModel == XEndpointCapacityModel
                && state.CapacityX0 == capacityX0 && state.CapacityX3 == capacityX3
                && state.Invalid == invalidX && state.CleanCapacity is null) return state;
            var xResult = state with
            {
                Capacity = capacityX3,
                Eternal = eternalX,
                Invalid = invalidX,
                Components = normalized,
                CleanCapacity = null,
                CapacityModel = XEndpointCapacityModel,
                CapacityX0 = capacityX0,
                CapacityX3 = capacityX3
            };
            if (CardStates.TryGetValue(card, out var xHolder)) xHolder.State = xResult;
            return xResult;
        }

        if (numericRandom)
        {
            var numericCapacity = state.CapacityModel == NumericRandomNetCapacityModel
                ? Math.Max(0, state.Capacity)
                : SampleNumericRandomCapacity(CreateCapacityRng(card), pricing);
            var numericInvalid = violatesCoreAssembly || !TinkeringSettings.IgnoreCapacityLimit
                && pricing.NetValue > numericCapacity + 0.0001d;
            if (state.Components.SequenceEqual(normalized) && numericCapacity == state.Capacity
                && !state.Eternal && state.CapacityModel == NumericRandomNetCapacityModel
                && state.Invalid == numericInvalid && state.CleanCapacity is null
                && state.CapacityX0 is null && state.CapacityX3 is null)
                return state;
            var numericResult = state with
            {
                Capacity = numericCapacity,
                Eternal = false,
                Invalid = numericInvalid,
                Components = normalized,
                CleanCapacity = null,
                CapacityModel = NumericRandomNetCapacityModel,
                CapacityX0 = null,
                CapacityX3 = null
            };
            if (CardStates.TryGetValue(card, out var numericHolder)) numericHolder.State = numericResult;
            return numericResult;
        }

        var eternal = state.Eternal && IsIntentionalUpperBoundException(valuationDefinition)
                                    && evaluation.ExceedsOrdinaryUpperBound;
        var capacity = state.CapacityModel == NetValueCapacityModel
            ? Math.Max(0, state.Capacity)
            : state.CleanCapacity is { } cleanCapacity
                ? Math.Max(0, (int)Math.Min(int.MaxValue, Math.Floor(cleanCapacity + 0.0001d)))
                : Math.Max(0, (int)Math.Min(int.MaxValue, Math.Floor(
                    (state.Capacity - pricing.LinearCompensation)
                    / Math.Max(1d, pricing.DownsideMultiplier) + 0.0001d)));
        // A capacity in the current net-value model is immutable here. In particular, a balance update or an
        // over-budget saved draft must become invalid instead of silently increasing its own capacity on load.
        var invalid = !eternal && (violatesCoreAssembly
            || !TinkeringSettings.IgnoreCapacityLimit
            && pricing.NetValue > capacity + 0.0001d);
        if (state.Components.SequenceEqual(normalized) && capacity == state.Capacity
            && eternal == state.Eternal && state.CapacityModel == NetValueCapacityModel
            && state.Invalid == invalid && state.CleanCapacity is null) return state;
        var result = state with
        {
            Capacity = capacity,
            Eternal = eternal,
            Invalid = invalid,
            Components = normalized,
            CleanCapacity = null,
            CapacityModel = NetValueCapacityModel,
            CapacityX0 = null,
            CapacityX3 = null
        };
        if (CardStates.TryGetValue(card, out var holder)) holder.State = result;
        return result;
    }

    private static bool ExceedsUpper(ProductionCardPricing pricing) =>
        pricing.NetValue > pricing.OrdinaryUpperBound + 0.0001d;

    // The generator's only deliberate random-card upper-envelope bypass is the curse-in-status-slot Easter egg.
    // Ordinary cards can move across an upper bound after balance/API updates and must remain editable.
    internal static bool IsIntentionalUpperBoundException(GeneratedCard card) =>
        card.Operations.Any(operation => DerivativeSlotCatalog.IsStatusProducer(operation.Template)
                                         && DerivativeSlotCatalog.ProducesCurse(operation));

    internal static bool UsesNumericRandomCapacity(GeneratedCard card) =>
        ChaosRunDefinitions.ActiveNumericRandomMode;

    internal static bool IsNumericRandomCapacityModel(int capacityModel) =>
        capacityModel is NumericRandomNetCapacityModel or NumericRandomXEndpointCapacityModel;

    private static Rng CreateCapacityRng(ChaosCardModel card)
    {
        var owner = card.Owner
                    ?? throw new InvalidOperationException("A generated card needs an owner before capacity is assigned.");
        var deckIndex = owner.Deck.Cards.IndexOf(card);
        var mixin = unchecked((ulong)((deckIndex + 1) * 1_000_003L
                                      + Math.Max(0, card.FloorAddedToDeck ?? 0) * 97L));
        return new Rng(owner, card.Id, mixin);
    }

    private static int SampleCapacity(Rng rng, ProductionCardPricing pricing)
    {
        var upper = Math.Max(0, (int)Math.Min(int.MaxValue, Math.Floor(pricing.OrdinaryUpperBound)));
        var lower = Math.Clamp(TinkeringValue.NetValueCeiling(pricing), 0, upper);
        return upper switch
        {
            0 => 0,
            _ when lower == upper => upper,
            int.MaxValue => rng.NextInt(lower, int.MaxValue),
            _ => rng.NextInt(lower, upper + 1)
        };
    }

    private static int SampleNumericRandomCapacity(Rng rng, ProductionCardPricing pricing) =>
        NumericRandomCapacity(pricing, SampleNumericRandomCapacityMultiplier(rng));

    private static int SampleNumericRandomCapacityMultiplier(Rng rng) => rng.NextInt(1_000, 1_501);

    private static int NumericRandomCapacity(ProductionCardPricing pricing, int multiplierPermille)
    {
        var minimum = TinkeringValue.NetValueCeiling(pricing);
        multiplierPermille = Math.Clamp(multiplierPermille, 1_000, 1_500);
        var scaled = (long)minimum * multiplierPermille;
        return (int)Math.Min(int.MaxValue, (scaled + 999L) / 1_000L);
    }

    private static int NormalizeEndpointCapacity(int? endpointCapacity, int legacyCapacity,
        ProductionCardPricing pricing, bool hasEndpointCapacity, bool eternal, Rng? rng)
    {
        var minimum = TinkeringValue.NetValueCeiling(pricing);
        if (eternal) return Math.Max(0, endpointCapacity ?? legacyCapacity);
        if (!hasEndpointCapacity)
            return ExceedsUpper(pricing) ? minimum : SampleCapacity(rng!, pricing);
        // Endpoint capacities already in the current model are fixed save data. Raising one to the current value
        // would let an over-budget edit manufacture capacity merely by being saved and reloaded.
        return Math.Max(0, endpointCapacity ?? legacyCapacity);
    }

    /// <summary>
    /// Older editor builds attached the deck's aggregate ExtraDamage to the combat-only Rampage component and did
    /// not recognize the true run-persistent damage component. Repair that ownership only when inspecting the deck
    /// outside combat; combat copies may legitimately contain temporary ExtraDamage and must never be migrated.
    /// When an old card contains more than one permanent-growth component the historical split is unknowable, so
    /// the legacy aggregate is assigned to the first matching component. All future gains are recorded by exact
    /// operation index and consequently travel with their own chip.
    /// </summary>
    private static TinkeredCardState ReconcilePermanentGrowth(ChaosCardModel card, TinkeredCardState state)
    {
        var components = state.Components.ToArray();
        var damageTotal = Math.Max(card.ExtraDamage,
            components.Sum(component => Math.Max(0, component.PermanentDamage)));
        var blockTotal = Math.Max(card.ExtraBlock,
            components.Sum(component => Math.Max(0, component.PermanentBlock)));
        var changed = false;

        for (var index = 0; index < components.Length && index < card.Generated.Operations.Count; index++)
        {
            var operation = card.Generated.Operations[index];
            var damage = TrainingSession.IsPermanentDamageGrowth(operation)
                ? Math.Max(0, components[index].PermanentDamage) : 0;
            var block = TrainingSession.IsPermanentBlockGrowth(operation)
                ? Math.Max(0, components[index].PermanentBlock) : 0;
            if (damage == components[index].PermanentDamage && block == components[index].PermanentBlock) continue;
            components[index] = components[index] with { PermanentDamage = damage, PermanentBlock = block };
            changed = true;
        }

        var assignedDamage = components.Sum(component => component.PermanentDamage);
        var damageIndex = card.Generated.Operations.ToList().FindIndex(TrainingSession.IsPermanentDamageGrowth);
        if (damageIndex >= 0 && damageTotal > assignedDamage)
        {
            components[damageIndex] = components[damageIndex] with
                { PermanentDamage = components[damageIndex].PermanentDamage + damageTotal - assignedDamage };
            changed = true;
        }
        var assignedBlock = components.Sum(component => component.PermanentBlock);
        var blockIndex = card.Generated.Operations.ToList().FindIndex(TrainingSession.IsPermanentBlockGrowth);
        if (blockIndex >= 0 && blockTotal > assignedBlock)
        {
            components[blockIndex] = components[blockIndex] with
                { PermanentBlock = components[blockIndex].PermanentBlock + blockTotal - assignedBlock };
            changed = true;
        }

        // ExtraDamage/ExtraBlock are only aggregate runtime caches. Once ownership has been reconstructed, make
        // those caches exactly equal to the surviving permanent-growth chips; otherwise removing the component in
        // an older editor build could leave an ownerless bonus active until the next Training Room commit.
        var reconciledDamage = components.Sum(component => component.PermanentDamage);
        var reconciledBlock = components.Sum(component => component.PermanentBlock);
        if (card.ExtraDamage != reconciledDamage) card.ExtraDamage = reconciledDamage;
        if (card.ExtraBlock != reconciledBlock) card.ExtraBlock = reconciledBlock;

        return changed ? state with { Components = components } : state;
    }

    internal static string EncodeCard(TinkeredCardState state, bool includeCombatUsage) =>
        JsonSerializer.Serialize(new CardEnvelope(CardSchema,
            includeCombatUsage ? state : ClearSingleUseProgress(state)), JsonOptions);

    internal static TinkeredCardState DecodeCard(string payload)
    {
        var envelope = JsonSerializer.Deserialize<CardEnvelope>(payload, JsonOptions)
            ?? throw new InvalidDataException("The card-tinkering card payload is empty.");
        if (envelope.Schema is not (1 or CardSchema))
            throw new InvalidDataException($"Unsupported card schema {envelope.Schema}.");
        // v0.8.12 could write run-once state back into the deck card. Schema 1 has no reliable runtime/deck marker,
        // so discard that obsolete state once during migration. Schema 2 serializes it only for combat cards.
        return envelope.Schema == 1 ? ClearSingleUseProgress(envelope.State) : envelope.State;
    }

    internal static string EncodeRun(TinkeringRunState state) =>
        JsonSerializer.Serialize(new RunEnvelope(RunSchema, EncodeComponents(state.Backpack),
            state.CompletedTrainingActs, state.Players?.Select(player => new PlayerRunPayload(
                player.PlayerId, EncodeComponents(player.Backpack),
                player.CompletedTrainingActs)).ToArray()), JsonOptions);

    internal static string EncodePlayerRun(PlayerTinkeringRunState state) =>
        JsonSerializer.Serialize(new PlayerRunEnvelope(RunSchema, EncodeComponents(state.Backpack),
            state.CompletedTrainingActs), JsonOptions);

    internal static TinkeringRunState DecodeRun(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return new TinkeringRunState([], []);
        var envelope = JsonSerializer.Deserialize<RunEnvelope>(payload, JsonOptions)
            ?? throw new InvalidDataException("The card-tinkering run payload is empty.");
        if (envelope.Schema is not (1 or 2 or RunSchema))
            throw new InvalidDataException($"Unsupported run schema {envelope.Schema}.");
        var backpack = DecodeComponents(envelope.Backpack);
        var players = envelope.Players?.Select(player => new PlayerTinkeringRunState(player.PlayerId,
            DecodeComponents(player.Backpack), player.CompletedTrainingActs)).ToArray();
        return new TinkeringRunState(backpack, envelope.CompletedTrainingActs, players);
    }

    internal static PlayerTinkeringRunState DecodePlayerRun(ulong playerId, string payload)
    {
        var envelope = JsonSerializer.Deserialize<PlayerRunEnvelope>(payload, JsonOptions)
            ?? throw new InvalidDataException("The player card-tinkering payload is empty.");
        if (envelope.Schema != RunSchema)
            throw new InvalidDataException($"Unsupported player run schema {envelope.Schema}.");
        return new PlayerTinkeringRunState(playerId, DecodeComponents(envelope.Backpack),
            envelope.CompletedTrainingActs);
    }

    private static StoredComponentPayload[] EncodeComponents(IEnumerable<StoredComponent> components) =>
        components.Select(component => new StoredComponentPayload(component.Operation, component.Upgrades,
            component.Progress with { Used = false },
            OperationRuntimeSpecCompiler.RequireStructured(component.Operation),
            component.Operation.LocalizedText,
            component.Upgrades.Select(effect => effect.ValueSlotId).ToArray())).ToArray();

    private static StoredComponent[] DecodeComponents(IEnumerable<StoredComponentPayload> components) =>
        components.Select(component =>
        {
            if (component.Upgrades.Count != component.UpgradeValueSlots.Count)
                throw new InvalidDataException("The backpack upgrade metadata count is inconsistent.");
            var operation = OperationRuntimeSpecCompiler.Attach(component.Operation with
            {
                RuntimeSpec = component.RuntimeSpec,
                LocalizedText = component.LocalizedText
            });
            var upgrades = component.Upgrades.Select((effect, index) => effect with
                { ValueSlotId = component.UpgradeValueSlots[index] }).ToArray();
            return new StoredComponent(operation, upgrades, component.Progress with { Used = false });
        }).ToArray();

    private static TinkeredCardState ClearSingleUseProgress(TinkeredCardState state)
    {
        if (!state.Components.Any(progress => progress.Used)) return state;
        return state with
        {
            Components = state.Components.Select(progress => progress.Used
                ? progress with { Used = false }
                : progress).ToArray()
        };
    }

    private static TinkeringRunState ClearSingleUseProgress(TinkeringRunState state)
    {
        var backpackChanged = state.Backpack.Any(component => component.Progress.Used);
        var playersChanged = state.Players?.Any(player =>
            player.Backpack.Any(component => component.Progress.Used)) == true;
        if (!backpackChanged && !playersChanged) return state;
        return state with
        {
            Backpack = state.Backpack.Select(component => component.Progress.Used
                ? component with { Progress = component.Progress with { Used = false } }
                : component).ToArray(),
            Players = state.Players?.Select(ClearSingleUseProgress).ToArray()
        };
    }

    private static PlayerTinkeringRunState ClearSingleUseProgress(PlayerTinkeringRunState state)
    {
        if (!state.Backpack.Any(component => component.Progress.Used)) return state;
        return state with
        {
            Backpack = state.Backpack.Select(component => component.Progress.Used
                ? component with { Progress = component.Progress with { Used = false } }
                : component).ToArray()
        };
    }
}

internal static class ReadOnlyListExtensions
{
    internal static int IndexOf<T>(this IReadOnlyList<T> values, T value)
    {
        for (var index = 0; index < values.Count; index++)
            if (EqualityComparer<T>.Default.Equals(values[index], value)) return index;
        return -1;
    }
}
