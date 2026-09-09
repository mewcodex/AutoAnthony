using System.Reflection;
using System.Runtime.CompilerServices;
using ChaosCardGenerator;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace AutoAnthony.Patches;

/// <summary>
/// Installs a component-composed localization template immediately before a native card formats its description.
/// The native CardModel still owns execution, upgrades, enchantments and preview DynamicVars; only presentation is
/// projected from the reviewed decomposition. This makes opting in safe for existing saves and multiplayer runs.
/// </summary>
internal static class NativeCardComponentDescription
{
    private sealed class OriginalTableEntries
    {
        internal readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
    }

    private readonly record struct TemplateKey(string CatalogId, bool Upgraded, bool Chinese);

    private static readonly ConditionalWeakTable<LocTable, OriginalTableEntries> Originals = new();
    private static readonly Dictionary<TemplateKey, string> Templates = new();
    private static readonly HashSet<TemplateKey> FailedTemplates = [];
    private static readonly object Sync = new();
    [ThreadStatic] private static int _upgradePreviewDepth;

    internal static bool IsUpgradePreview => _upgradePreviewDepth > 0;

    internal static void EnterUpgradePreview() => _upgradePreviewDepth++;

    internal static void ExitUpgradePreview() => _upgradePreviewDepth = Math.Max(0, _upgradePreviewDepth - 1);

    internal static void Prepare(CardModel card)
    {
        if (card is ChaosCardModel || LocManager.Instance is null
            || !AutoAnthonyNativeCardApi.TryResolve(card, out var decomposition))
            return;

        var table = LocManager.Instance.GetTable("cards");
        var localizationKey = card.Id.Entry + ".description";
        var originals = Originals.GetValue(table, static _ => new OriginalTableEntries());
        lock (originals)
        {
            if (!originals.Values.ContainsKey(localizationKey))
                originals.Values[localizationKey] = table.GetRawText(localizationKey);
        }

        if (!AutoAnthonyNativeCardApi.ComponentDescriptionsEnabled)
        {
            lock (originals)
                ChaosRuntimeDescriptionCache.InstallIfChanged(
                    table, localizationKey, originals.Values[localizationKey]);
            return;
        }

        var chinese = LocManager.Instance.Language is "zhs" or "zht";
        var key = new TemplateKey(decomposition.CatalogId,
            IsUpgradePreview || card.IsUpgraded, chinese);
        string template;
        lock (Sync)
        {
            if (!Templates.TryGetValue(key, out template!))
            {
                try
                {
                    if (!NativeCardDecompositionApi.TryCreateComponentDescriptionTemplate(
                            key.CatalogId, key.Upgraded, key.Chinese, out template!))
                        return;
                    template = ChaosTextFormatter.Format(template, chinese);
                    Templates[key] = template;
                }
                catch (Exception exception)
                {
                    if (FailedTemplates.Add(key))
                        Log.Warn($"[AutoAnthony] Could not render native component description "
                                 + $"{key.CatalogId}: {exception.Message}");
                    return;
                }
            }
        }
        ChaosRuntimeDescriptionCache.InstallIfChanged(table, localizationKey, template);
    }
}

[HarmonyPatch]
internal static class NativeCardPrivateDescriptionPatch
{
    private static MethodBase TargetMethod() => AccessTools.GetDeclaredMethods(typeof(CardModel))
        .Single(method => method.Name == "GetDescriptionForPile"
                          && method.IsPrivate
                          && method.GetParameters().Length == 3);

    private static void Prefix(CardModel __instance) => NativeCardComponentDescription.Prepare(__instance);
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.GetDescriptionForUpgradePreview))]
internal static class NativeCardUpgradeDescriptionContextPatch
{
    private static void Prefix() => NativeCardComponentDescription.EnterUpgradePreview();

    private static Exception? Finalizer(Exception? __exception)
    {
        NativeCardComponentDescription.ExitUpgradePreview();
        return __exception;
    }
}
