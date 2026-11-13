using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.FoolArrows;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace AutoAnthony;

public abstract partial class ChaosCardModel
{
    private HashSet<int> _activeFoolLifecycleTriggers = [];
    private Dictionary<int, int> _foolLifecycleActivations = [];
    private long _foolLifecycleTimepoint = -1;

    protected override void AfterCloned()
    {
        base.AfterCloned();
        _activeFoolLifecycleTriggers = [];
        _foolLifecycleActivations = [];
        _foolLifecycleTimepoint = -1;
        _requestedPostPlayPile = null;
    }

    private async Task FireFoolLifecycle(string template, PlayerChoiceContext context,
        CardModel? eventCard = null, MegaCrit.Sts2.Core.Entities.Creatures.Creature? enemy = null)
    {
        for (var index = 0; index < Generated.Operations.Count; index++)
        {
            if (Generated.Operations[index].Template != template || !_activeFoolLifecycleTriggers.Add(index)) continue;
            try
            {
                if (ChaosAbilityTriggerLimiter.TryConsume(index, ChaosAbilityTriggerLimiter.CurrentTimepoint,
                        ref _foolLifecycleTimepoint, _foolLifecycleActivations))
                    await ChaosOperationExecutor.ExecuteTriggered(this, index, context, eventCard: eventCard, eventCreature: enemy);
            }
            finally { _activeFoolLifecycleTriggers.Remove(index); }
        }
    }

    public override async Task AfterArrowShot(Player owner, ArrowSlot? arrow, PlayerChoiceContext context, CardModel? source)
    {
        if (owner == Owner && arrow is not null && Pile?.Type == PileType.Discard)
            await FireFoolLifecycle("F:WhileDiscardArrowShot", context, source);
    }

    public override async Task AfterArrowDislodged(ImpaledPower power, ArrowSlot arrow,
        PlayerChoiceContext context, CardModel? source)
    {
        if (power.Applier == Owner.Creature && ReferenceEquals(source, this))
            await FireFoolLifecycle("F:WhenThisDislodgesArrow", context, source, power.Owner);
    }
}
