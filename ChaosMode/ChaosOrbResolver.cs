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
    }

    internal static IEnumerable<IHoverTip> HoverTips(GeneratorOperation operation)
    {
        var ids = new[]
            {
                OrbSlotCatalog.ResolveSource(operation.OrbSourceId, operation.Template)?.Id,
                OrbSlotCatalog.ResolveOutput(operation.OrbOutputId, operation.Template)?.Id
            }
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
