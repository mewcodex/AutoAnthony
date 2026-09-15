using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;

namespace AutoAnthony.Patches;

/// <summary>
/// RitsuLib-backed character mods register FMOD banks during mod initialization and flush them after the game's
/// deferred initialization. AutoAnthony's sizeable model-preview initialization can expose a lifecycle race where
/// that first flush runs before FMOD is ready and no later registration occurs to retry the retained queue. In that
/// state character selection reports no GUID mappings and only the vanilla banks remain loaded.
///
/// Keep this bridge optional and deliberately narrow: when the character-select screen opens, ask an already-loaded
/// RitsuLib to retry only its own pending registrations. We neither discover nor load another mod's bank ourselves,
/// and installations without RitsuLib are untouched.
/// </summary>
[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.OnSubmenuOpened))]
internal static class ModCharacterAudioCompatibilityPatch
{
    private const string RegistrationTypeName =
        "STS2RitsuLib.Audio.FmodStudioDeferredBankRegistration";

    private static readonly object Gate = new();
    private static MethodInfo? _flushPending;
    private static bool _resolved;
    private static bool _disabled;
    private static bool _successLogged;

    private static void Prefix()
    {
        if (_disabled) return;
        try
        {
            var flush = ResolveFlushMethod();
            if (flush is null) return;
            flush.Invoke(null, null);
            if (_successLogged) return;
            _successLogged = true;
            Log.Info("[AutoAnthony] Retried pending optional RitsuLib FMOD registrations before character selection.");
        }
        catch (Exception exception)
        {
            _disabled = true;
            Log.Warn("[AutoAnthony] Disabled optional RitsuLib character-select audio compatibility after an error: "
                     + exception.GetBaseException().Message);
        }
    }

    private static MethodInfo? ResolveFlushMethod()
    {
        if (_resolved) return _flushPending;
        lock (Gate)
        {
            if (_resolved) return _flushPending;
            _resolved = true;
            var registrationType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(RegistrationTypeName, throwOnError: false))
                .FirstOrDefault(type => type is not null);
            _flushPending = registrationType?.GetMethod("FlushPending",
                BindingFlags.Static | BindingFlags.NonPublic);
            return _flushPending;
        }
    }
}
