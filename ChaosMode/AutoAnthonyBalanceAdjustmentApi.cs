using MegaCrit.Sts2.Core.Logging;

namespace AutoAnthony;

/// <summary>
/// Optional compatibility notifications for companions that keep per-card metadata outside the generated
/// definition. Listeners are invoked only after a live deck card has been rebound to a balance-adjusted definition.
/// Auto Anthony itself does not require Card Tinkering (or any other listener) to be installed.
/// </summary>
public static class AutoAnthonyBalanceAdjustmentApi
{
    public const int ApiVersion = 1;
    private static readonly object Gate = new();
    private static readonly List<Action<ChaosCardModel>> DefinitionChangedListeners = [];

    public static void RegisterDefinitionChangedListener(Action<ChaosCardModel> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (Gate)
        {
            if (!DefinitionChangedListeners.Contains(listener))
                DefinitionChangedListeners.Add(listener);
        }
    }

    internal static void NotifyDefinitionChanged(ChaosCardModel card)
    {
        Action<ChaosCardModel>[] listeners;
        lock (Gate) listeners = DefinitionChangedListeners.ToArray();
        foreach (var listener in listeners)
        {
            try { listener(card); }
            catch (Exception exception)
            {
                Log.Error("[AutoAnthony] A balance-adjustment compatibility listener failed: " + exception);
            }
        }
    }
}
