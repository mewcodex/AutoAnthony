namespace AutoAnthony;

/// <summary>
/// Read-only runtime view of the persisted Auto-Anthonyology master and cross-mod feature preferences.
/// Additive API: existing component-generation and runtime APIs are unchanged.
/// </summary>
public static class AutoAnthonySettingsApi
{
    public const int ApiVersion = 1;

    /// <summary>The user-facing top-level Auto-Anthonyology switch.</summary>
    public static bool Enabled => ChaosModSettings.Enabled;

    /// <summary>Whether Card Tinkering may offer its deck-screen editor outside combat.</summary>
    public static bool AnytimeCardEditing => ChaosModSettings.AnytimeCardEditing;
}
