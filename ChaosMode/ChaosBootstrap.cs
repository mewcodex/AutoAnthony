using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models.CardPools;

namespace AutoAnthony;

[ModInitializer(nameof(Init))]
public static class ChaosBootstrap
{
    private static bool _initialized;

    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;
        // Ancient Fuel is a real token model, not merely a run-time replacement. Registering it with the
        // vanilla token pool makes the card library treat it as unlocked in the same way as Fuel and the other
        // derivatives. This must happen before ModelDb freezes the pool contents.
        ModHelper.AddModelToPool<TokenCardPool, AncientFuel>();
        ChaosModSettings.EnsureLoaded();
        try
        {
            ChaosModSettings.AuditCompatibility();
            AutoAnthony.Patches.SurpriseModeUi.AuditTextMasking();
            PoolGenerationPolicy.Validate();
            ChaosOperationExecutor.AuditDerivativeProducerCoverage();
        }
        catch (Exception exception)
        {
            Log.Error($"[AutoAnthony] Startup configuration self-audit failed without blocking startup: {exception}");
        }

        var harmony = new Harmony("autoanthony.v111");
        var applied = 0;
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
        {
            if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;
            try
            {
                new PatchClassProcessor(harmony, type).Patch();
                applied++;
            }
            catch (Exception ex)
            {
                Log.Error($"[AutoAnthony] Patch failed for {type.FullName}: {ex}");
            }
        }
        Log.Info($"[AutoAnthony] Initialized with {ChaosCardRegistry.Count} fixed card slots and {applied} Harmony patch classes; Enabled={ChaosModSettings.Enabled}, ReplaceStartingCards={ChaosModSettings.ReplaceStartingCards}, Ultimate Chaos={ChaosModSettings.UltimateChaos}.");
    }
}
