using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace AutoAnthony.Patches;

/// <summary>
/// Card afflictions commit their model state before notifying UI/listener code. A listener exception normally
/// escapes CardCmd.Afflict/ClearAffliction and faults the monster's awaited move, leaving combat between turns even
/// though the affliction itself was already applied or removed. Generated cards have substantially more dynamic
/// presentation state than native cards, so keep a post-commit presentation failure from aborting combat flow.
/// Exceptions raised before the state transition commits are deliberately not suppressed.
/// </summary>
internal static class ChaosAfflictionCompatibility
{
    internal static bool AuditProbe { get; set; }

    internal static Exception? RecoverCommittedTransition(CardModel card, AfflictionModel? expectedAffliction,
        bool clearing, Exception? exception)
    {
        if (exception is null || card is not ChaosCardModel) return exception;
        var committed = clearing ? card.Affliction is null : ReferenceEquals(card.Affliction, expectedAffliction);
        if (!committed) return exception;
        if (!AuditProbe)
        {
            var action = clearing ? "cleared" : "applied";
            Log.Warn($"[AutoAnthony] Recovered a generated-card affliction observer failure after the state was "
                     + $"already {action}: card={card.Id}, affliction={expectedAffliction?.Id.Entry ?? "none"}. "
                     + $"Combat will continue. Error: {exception}");
        }
        return null;
    }

    internal static void Audit(CardModel card)
    {
        AuditProbe = true;
        try
        {
            Action throwingObserver = static () => throw new InvalidOperationException("Affliction observer audit probe");
            card.AfflictionChanged += throwingObserver;
            var affliction = ModelDb.Affliction<MegaCrit.Sts2.Core.Models.Afflictions.Hexed>().ToMutable();
            card.AfflictInternal(affliction, 2);
            if (!ReferenceEquals(card.Affliction, affliction))
                throw new InvalidOperationException("Committed affliction application was not preserved.");
            card.AfflictionChanged -= throwingObserver;

            card.AfflictionChanged += throwingObserver;
            card.ClearAfflictionInternal();
            if (card.Affliction is not null)
                throw new InvalidOperationException("Committed affliction removal was not preserved.");
            card.AfflictionChanged -= throwingObserver;
        }
        finally
        {
            AuditProbe = false;
        }
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.AfflictInternal))]
internal static class ChaosAfflictionApplyCompatibilityPatch
{
    private static Exception? Finalizer(CardModel __instance, AfflictionModel affliction, Exception? __exception) =>
        ChaosAfflictionCompatibility.RecoverCommittedTransition(__instance, affliction, clearing: false, __exception);
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.ClearAfflictionInternal))]
internal static class ChaosAfflictionClearCompatibilityPatch
{
    private static Exception? Finalizer(CardModel __instance, AfflictionModel? __state, Exception? __exception) =>
        ChaosAfflictionCompatibility.RecoverCommittedTransition(__instance, __state, clearing: true, __exception);

    private static void Prefix(CardModel __instance, out AfflictionModel? __state) => __state = __instance.Affliction;
}
