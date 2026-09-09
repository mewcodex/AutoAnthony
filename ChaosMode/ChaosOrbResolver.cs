using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;

namespace AutoAnthony;

internal static class ChaosOrbResolver
{
    internal static bool MatchesSource(OrbModel orb, GeneratorOperation operation) =>
        Matches(orb, OrbSlotCatalog.ResolveSource(operation.OrbSourceId, operation.Template)?.Id);

    private static bool Matches(OrbModel orb, string? id) => id switch
    {
        "lightning" => orb is LightningOrb,
        "frost" => orb is FrostOrb,
        "dark" => orb is DarkOrb,
        "plasma" => orb is PlasmaOrb,
        "glass" => orb is GlassOrb,
        _ => false
    };

    internal static async Task Channel(PlayerChoiceContext choiceContext, Player owner,
        GeneratorOperation operation, int count)
    {
        var id = OrbSlotCatalog.ResolveOutput(operation.OrbOutputId, operation.Template)?.Id
            ?? throw new InvalidOperationException($"Operation {operation.Template} has no output Orb slot.");
        // If this effect was invoked by AfterOrbEvoked while a native Channel command is waiting to insert its own
        // Orb, generated Orbs may use the temporary vacancy but must leave one slot open before the hook returns.
        // This is different from an explicit Evoke/Dualcast: those have no pending outer Orb and should retain every
        // generated Orb normally.
        var preservePendingEnqueueVacancy = ChaosOrbChannelContext.HasPendingEnqueue;
        for (var index = 0; index < count; index++)
        {
            switch (id)
            {
                case "lightning": await OrbCmd.Channel<LightningOrb>(choiceContext, owner); break;
                case "frost": await OrbCmd.Channel<FrostOrb>(choiceContext, owner); break;
                case "dark": await OrbCmd.Channel<DarkOrb>(choiceContext, owner); break;
                case "plasma": await OrbCmd.Channel<PlasmaOrb>(choiceContext, owner); break;
                case "glass": await OrbCmd.Channel<GlassOrb>(choiceContext, owner); break;
                case "random":
                    await OrbCmd.Channel(choiceContext,
                        OrbModel.GetRandomOrb(owner.RunState.Rng.CombatOrbGeneration).ToMutable(), owner);
                    break;
                default: throw new InvalidOperationException($"Unknown Orb slot id '{id}'.");
            }
        }
        if (preservePendingEnqueueVacancy)
            await PreservePendingEnqueueVacancy(choiceContext, owner);
    }

    private static async Task PreservePendingEnqueueVacancy(PlayerChoiceContext choiceContext, Player owner)
    {
        var queue = owner.PlayerCombatState?.OrbQueue;
        if (queue is null || queue.Capacity <= 0 || queue.Orbs.Count < queue.Capacity) return;

        // The trigger which emitted these Orbs is still active, so its own AfterOrbEvoked callback is suppressed by
        // ChaosCompositePower's re-entry guard. Other generated Orb triggers use the same reservation protocol.
        await OrbCmd.EvokeNext(choiceContext, owner);
    }

    /// <summary>
    /// Materializes at most one hover tip for each concrete Orb type referenced by the card. A Power may legally
    /// Channel the same Orb both immediately and from a later trigger; deduplicating the semantic IDs before asking
    /// ModelDb for hover models keeps repeated card-hover refreshes bounded and avoids feeding duplicate canonical
    /// Orb tips into the UI's nested hover layout.
    /// </summary>
    internal static IEnumerable<IHoverTip> HoverTips(IEnumerable<GeneratorOperation> operations)
    {
        var ids = operations
            .Where(operation => OrbSlotCatalog.IsSlotOperation(operation.Template))
            .SelectMany(operation => new[]
            {
                OrbSlotCatalog.ResolveSource(operation.OrbSourceId, operation.Template)?.Id,
                OrbSlotCatalog.ResolveOutput(operation.OrbOutputId, operation.Template)?.Id
            })
            .Where(id => id is not null)
            .Where(id => id != "random")
            .Distinct(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            yield return id switch
            {
                "lightning" => HoverTipFactory.FromOrb<LightningOrb>(),
                "frost" => HoverTipFactory.FromOrb<FrostOrb>(),
                "dark" => HoverTipFactory.FromOrb<DarkOrb>(),
                "plasma" => HoverTipFactory.FromOrb<PlasmaOrb>(),
                "glass" => HoverTipFactory.FromOrb<GlassOrb>(),
                _ => throw new InvalidOperationException($"Unknown Orb slot id '{id}'.")
            };
        }
    }
}
