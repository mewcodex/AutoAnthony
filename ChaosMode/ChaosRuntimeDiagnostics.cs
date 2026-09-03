using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Logging;

namespace AutoAnthony;

internal static class ChaosRuntimeDiagnostics
{
    internal static void TriggerFired(string category, int slot, GeneratorOperation operation) =>
        Log.Info($"[AutoAnthony] Fired {category} trigger {operation.Template} for slot {slot}.");
}
