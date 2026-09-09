using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace AutoAnthony;

/// <summary>
/// Keeps compatibility with optional act-selection frameworks deliberately soft. AutoAnthony owns generated card
/// pools for the lifetime of a run and never patches the act-transition method, so replacing an entry in
/// RunState.Acts must not be mistaken for starting a new run. The generation overlay is synchronously released
/// before vanilla run entry begins, allowing an act framework to await an interactive route selection without a
/// higher CanvasLayer intercepting its input. The remaining ordering requirement is that optional act registries
/// finish their ModelDb initialization before the generated-card preview is assembled.
/// </summary>
internal static class OptionalActSelectionFrameworkCompatibility
{
    private const string ActLikeItRegistryType = "ActLikeIt2.ActRegistry";
    private const string ActLikeItHarmonyOwner = "ActLikeIt2";

    internal static void LogStatus()
    {
        // Do not load an optional assembly or bind to one of its public APIs. Type lookup only observes assemblies
        // the game already enabled, which keeps AutoAnthony usable across framework updates and when it is absent.
        if (AccessTools.TypeByName(ActLikeItRegistryType) is null) return;

        var enterAct = AccessTools.Method(typeof(RunManager), nameof(RunManager.EnterAct));
        var patchInfo = enterAct is null ? null : Harmony.GetPatchInfo(enterAct);
        var selectorInstalled = patchInfo?.Prefixes.Any(patch =>
            string.Equals(patch.owner, ActLikeItHarmonyOwner, StringComparison.OrdinalIgnoreCase)) == true;

        if (!selectorInstalled)
        {
            Log.Warn("[AutoAnthony] An optional act-selection framework was detected, but its EnterAct patch is "
                     + "not active. AutoAnthony will preserve the run card-pool snapshot normally.");
            return;
        }

        Log.Info("[AutoAnthony] Optional act-selection framework detected. Generated card pools remain bound to "
                 + "the run snapshot across act replacement; the generation overlay releases input before "
                 + "interactive run entry, and AutoAnthony does not intercept EnterAct.");
    }
}
