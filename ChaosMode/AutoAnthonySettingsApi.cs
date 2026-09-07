namespace AutoAnthony;

/// <summary>
/// Immutable snapshot of every user-facing Auto-Anthonyology preference. This deliberately excludes internal
/// migration bookkeeping and temporary generation overrides.
/// </summary>
public sealed record AutoAnthonySettingsSnapshot(
    bool Enabled,
    bool NumericBalanceOptimization,
    bool NumericRandomMode,
    bool UltimateChaos,
    bool ReplaceStartingCards,
    bool PreserveOriginalCards,
    bool RandomCardArt,
    bool AnytimeCardEditing,
    bool ShowGenerationModeHoverTips,
    bool ShowCardInternalIds,
    bool SurpriseMode,
    bool SurpriseModeLite,
    bool SurpriseModePro)
{
    public bool AnySurpriseMode => SurpriseMode || SurpriseModeLite || SurpriseModePro;

    public ComponentSurpriseMode ActiveSurpriseMode => SurpriseModePro
        ? ComponentSurpriseMode.Pro
        : SurpriseModeLite
            ? ComponentSurpriseMode.Lite
            : SurpriseMode
                ? ComponentSurpriseMode.Standard
                : ComponentSurpriseMode.Disabled;
}

/// <summary>
/// Read-only access to the settings currently selected by the local user. These are persisted preferences, not
/// temporary save-migration overrides or multiplayer host settings. Use <see cref="ComponentRunSettingsApi"/> when
/// generation must follow the effective settings for a run.
/// </summary>
public static class AutoAnthonySettingsApi
{
    public const int ApiVersion = 2;

    public static AutoAnthonySettingsSnapshot Current => new(
        ChaosModSettings.Enabled,
        ChaosModSettings.NumericBalanceOptimization,
        ChaosModSettings.NumericRandomMode,
        ChaosModSettings.UltimateChaos,
        ChaosModSettings.ReplaceStartingCards,
        ChaosModSettings.PreserveOriginalCards,
        ChaosModSettings.RandomCardArt,
        ChaosModSettings.AnytimeCardEditing,
        ChaosModSettings.ShowGenerationModeHoverTips,
        ChaosModSettings.ShowCardInternalIds,
        ChaosModSettings.SurpriseMode,
        ChaosModSettings.SurpriseModeLite,
        ChaosModSettings.SurpriseModePro);
}
