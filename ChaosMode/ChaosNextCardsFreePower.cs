using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;

namespace AutoAnthony;

/// <summary>Preserves generated FreeCard effects after v114 removed the native FreeCardPower.</summary>
public sealed class ChaosNextCardsFreePower : PowerModel
{
    public override PowerType Type => PowerType.Buff;
    public override PowerStackType StackType => PowerStackType.Counter;

    private bool AppliesTo(CardModel card) => card.Owner.Creature == Owner
        && card.Pile?.Type is PileType.Hand or PileType.Play;

    public override bool TryModifyEnergyCostInCombatLate(CardModel card, decimal originalCost,
        out decimal modifiedCost)
    {
        var applies = AppliesTo(card);
        modifiedCost = applies ? 0 : originalCost;
        return applies;
    }

    public override async Task BeforeCardPlayed(CardPlay cardPlay)
    {
        if (AppliesTo(cardPlay.Card)) await PowerCmd.Decrement(this);
    }
}
