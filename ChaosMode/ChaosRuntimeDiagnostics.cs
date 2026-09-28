using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Logging;

namespace AutoAnthony;

internal static class ChaosRuntimeDiagnostics
{
    // Observation only: never cancel/complete the command, move a card, or swallow its exception. A slow choice is
    // not necessarily a hang. Emit one breadcrumb to identify the nested card if a remote report has no exception.
    internal static async Task ObserveNestedCommand(Task command, string description)
    {
        using var completed = new CancellationTokenSource();
        var observation = ReportPending(completed.Token, description);
        try
        {
            await command;
        }
        catch (Exception exception)
        {
            Log.Error($"[AutoAnthony] Nested command failed ({description}): {exception}");
            throw;
        }
        finally
        {
            completed.Cancel();
            await observation;
        }
    }

    private static async Task ReportPending(CancellationToken completed, string description)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), completed);
            Log.Warn($"[AutoAnthony] Nested command still pending after 15s ({description}); "
                + "this may be an active player choice. No resolution was skipped.");
        }
        catch (OperationCanceledException) when (completed.IsCancellationRequested) { }
    }

    internal static void TriggerFired(string category, int slot, GeneratorOperation operation) =>
        Log.Info($"[AutoAnthony] Fired {category} trigger {operation.Template} for slot {slot}.");
}
