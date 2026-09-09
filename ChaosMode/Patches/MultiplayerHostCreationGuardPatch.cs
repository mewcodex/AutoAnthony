using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace AutoAnthony.Patches;

/// <summary>
/// The vanilla host screen does not debounce its async Steam-lobby creation call. A second release while the
/// first call is awaiting Steam creates another lobby and another SteamHost callback. Both hosts then process
/// the same incoming connection; the host whose lobby the remote player did not join rejects that connection
/// and tears down the otherwise valid one. Keep the protection at the narrow host-creation boundary so normal
/// reconnects and later host attempts remain unaffected.
/// </summary>
[HarmonyPatch(typeof(NMultiplayerHostSubmenu), "StartHostAsync")]
internal static class MultiplayerHostCreationGuardPatch
{
    private static int _hostCreationInFlight;

    private static bool Prefix(ref Task __result, out bool __state)
    {
        __state = Interlocked.CompareExchange(ref _hostCreationInFlight, 1, 0) == 0;
        if (__state)
            return true;

        Log.Warn("[AutoAnthony] Ignored a duplicate multiplayer host request while Steam lobby creation was already in progress.");
        __result = Task.CompletedTask;
        return false;
    }

    private static void Postfix(ref Task __result, bool __state)
    {
        if (__state)
            __result = AwaitAndReleaseAsync(__result);
    }

    private static Exception? Finalizer(Exception? __exception, bool __state)
    {
        if (__state && __exception is not null)
            Volatile.Write(ref _hostCreationInFlight, 0);

        return __exception;
    }

    private static async Task AwaitAndReleaseAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _hostCreationInFlight, 0);
        }
    }
}
