using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;

namespace AutoAnthony;

internal static class ChaosCardDiscovery
{
    internal static void ResetGeneratedCardsForNewRun()
    {
        if (SaveManager.Instance is not { } saveManager
            || saveManager.Progress.DiscoveredCards is not ISet<ModelId> discovered)
            return;

        var removed = 0;
        foreach (var type in ChaosCardRegistry.Types
                     .Concat(ChaosCardRegistry.SilentTypes)
                     .Concat(ChaosCardRegistry.DefectTypes)
                     .Concat(ChaosCardRegistry.NecrobinderTypes)
                     .Concat(ChaosCardRegistry.RegentTypes)
                     .Concat(ChaosCardRegistry.ColorlessTypes)
                     .Distinct())
        {
            if (discovered.Remove(ModelDb.GetId(type))) removed++;
        }

        // Starting a run already persists progress as part of the normal run-save transaction. Writing the
        // profile here created a second synchronous cloud save immediately before that transaction and could
        // overlap the main-menu abandon/load path. The in-memory reset is sufficient for the new run, and the
        // ordinary save commits it atomically with the run.
        Log.Info($"[AutoAnthony] Reset discovery state for {ChaosCardRegistry.Count} generated card slots before the new run ({removed} previously seen). ");
    }
}
