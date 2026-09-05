using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace AutoAnthony;

public enum ComponentSurpriseMode
{
    Disabled,
    Standard,
    Lite,
    Pro
}

/// <summary>Immutable settings used to generate one run.</summary>
public sealed record ComponentRunSettings(
    bool Enabled,
    bool UltimateChaos,
    bool NumericBalanceOptimization,
    bool NumericRandomMode,
    bool ReplaceStartingCards,
    bool PreserveOriginalCards,
    bool RandomCardArt,
    ComponentSurpriseMode SurpriseMode);

/// <summary>
/// Stable access to AutoAnthony's run-generation settings. External character adapters should resolve the
/// multiplayer carrier instead of reading private settings fields so every peer uses the host's gameplay options.
/// </summary>
public static class ComponentRunSettingsApi
{
    public const int ApiVersion = 1;

    public static ComponentRunSettings Local => new(
        ChaosModSettings.Enabled,
        ChaosModSettings.EffectiveUltimateChaos,
        ChaosModSettings.EffectiveNumericBalanceOptimization,
        ChaosModSettings.EffectiveNumericRandomMode,
        ChaosModSettings.ReplaceStartingCards,
        ChaosModSettings.PreserveOriginalCards,
        ChaosModSettings.RandomCardArt,
        CurrentSurpriseMode());

    public static bool TryResolveMultiplayer(IEnumerable<ModifierModel> modifiers,
        out ComponentRunSettings settings)
    {
        ArgumentNullException.ThrowIfNull(modifiers);
        var marker = modifiers.OfType<ChaosPoolSnapshotModifier>()
            .LastOrDefault(value => value.MultiplayerGenerationModeSpecified);
        if (marker is null)
        {
            settings = Local;
            return false;
        }

        var local = Local;
        settings = new ComponentRunSettings(
            marker.MultiplayerModEnabled,
            marker.MultiplayerUltimateChaos,
            marker.MultiplayerNumericBalanceOptimizationSpecified
                ? marker.MultiplayerNumericBalanceOptimization
                : local.NumericBalanceOptimization,
            marker.MultiplayerNumericRandomMode,
            marker.MultiplayerReplaceStartingCardsSpecified
                ? marker.MultiplayerReplaceStartingCards
                : local.ReplaceStartingCards,
            marker.MultiplayerPreserveOriginalCards,
            marker.MultiplayerRandomCardArtSpecified ? marker.MultiplayerRandomCardArt : local.RandomCardArt,
            local.SurpriseMode);
        return true;
    }

    private static ComponentSurpriseMode CurrentSurpriseMode() => ChaosModSettings.SurpriseModePro
        ? ComponentSurpriseMode.Pro
        : ChaosModSettings.SurpriseModeLite
            ? ComponentSurpriseMode.Lite
            : ChaosModSettings.SurpriseMode
                ? ComponentSurpriseMode.Standard
                : ComponentSurpriseMode.Disabled;
}

public interface IComponentGenerationProgress : IDisposable
{
    void Report(int current);
    void ShowEnteringRun();
    void ShowWaitingForPlayers();
}

/// <summary>Creates the same loading overlay used by AutoAnthony's built-in pools.</summary>
public static class ComponentGenerationProgressApi
{
    public const int ApiVersion = 1;

    public static IComponentGenerationProgress Create(int total) =>
        new ProgressAdapter(ChaosGenerationProgressOverlay.Create(total));

    private sealed class ProgressAdapter(ChaosGenerationProgressOverlay overlay) : IComponentGenerationProgress
    {
        public void Report(int current) => overlay.Report(current);
        public void ShowEnteringRun() => overlay.ShowEnteringRun();
        public void ShowWaitingForPlayers() => overlay.ShowWaitingForPlayers();
        public void Dispose() => overlay.Dispose();
    }
}

/// <summary>
/// Public trigger bridge for character-specific combat hooks. It preserves AutoAnthony's dependency resolution,
/// captured X values, recursion guards and selection cleanup instead of requiring reflection into the executor.
/// </summary>
public static class ComponentTriggerApi
{
    public const int ApiVersion = 1;

    public static async Task FirePlayerAsync(Player owner, PlayerChoiceContext choiceContext, string triggerKind,
        CardPlay? sourcePlay = null, CardModel? eventCard = null, Creature? eventCreature = null,
        decimal eventAmount = 0)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(choiceContext);
        ValidateTriggerKind(triggerKind);
        foreach (var power in owner.Creature.Powers.OfType<ChaosCompositePower>().ToArray())
            await power.FireExternalTriggerAsync(triggerKind, choiceContext, sourcePlay, eventCard, eventCreature,
                eventAmount);
    }

    public static bool HasCardTrigger(ChaosCardModel card, string triggerKind)
    {
        ArgumentNullException.ThrowIfNull(card);
        ValidateTriggerKind(triggerKind);
        return card.Generated.Operations.Any(operation =>
            operation.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger
            && OperationRuntimeSpecCompiler.RequireStructured(operation).Trigger?.Kind == triggerKind);
    }

    public static async Task FireCardAsync(ChaosCardModel card, PlayerChoiceContext choiceContext,
        string triggerKind, CardPlay? sourcePlay = null, CardModel? eventCard = null,
        Creature? eventCreature = null, decimal eventAmount = 0, Creature? sourceTarget = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(choiceContext);
        ValidateTriggerKind(triggerKind);
        var operations = card.Generated.Operations;
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Scope is not (OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger)
                || OperationRuntimeSpecCompiler.RequireStructured(operation).Trigger?.Kind != triggerKind)
                continue;
            await ChaosOperationExecutor.ExecuteTriggered(card, index, choiceContext, sourcePlay,
                eventCard, eventCreature, eventAmount, sourceTarget);
        }
    }

    public static int EffectiveOperationAmount(ChaosCardModel card, int operationIndex)
    {
        ArgumentNullException.ThrowIfNull(card);
        if ((uint)operationIndex >= (uint)card.Generated.Operations.Count)
            throw new ArgumentOutOfRangeException(nameof(operationIndex));
        return card.OperationAmount(operationIndex);
    }

    private static void ValidateTriggerKind(string triggerKind)
    {
        if (string.IsNullOrWhiteSpace(triggerKind) || triggerKind.Any(character => character > 0x7f))
            throw new ArgumentException("Trigger kinds must be non-empty ASCII strings.", nameof(triggerKind));
    }
}

/// <summary>Stable read-only integration points for Surprise modes.</summary>
public static class ComponentSurpriseApi
{
    public const int ApiVersion = 1;

    public static bool IsGenerated(CardModel? card) => AutoAnthony.Patches.SurpriseModeUi.IsGenerated(card);
    public static bool IsKnown(CardModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return SurpriseCardKnowledge.IsKnown(card);
    }

    public static bool ShouldConceal(CardModel? card) => AutoAnthony.Patches.SurpriseModeUi.ShouldConceal(card);
}
