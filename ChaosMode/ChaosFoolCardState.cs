using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace AutoAnthony;

public abstract partial class ChaosCardModel
{
    private int _extraAim;
    private PileType? _requestedPostPlayPile;

    [SavedProperty]
    public int ExtraAim
    {
        get => _extraAim;
        set
        {
            AssertMutable();
            var delta = value - _extraAim;
            _extraAim = value;
            if (_dynamicVariablesDefinition is null || delta == 0) return;
            for (var index = 0; index < Generated.Operations.Count; index++)
                if (Generated.Operations[index].Template == "F:Aim"
                    && DynamicVars.TryGetValue(ChaosOperationVariables.Name(Generated.Operations[index], index), out var variable))
                    variable.BaseValue += delta;
        }
    }

    internal void RequestPostPlayMove(PileType pile) => _requestedPostPlayPile = pile;
    internal void SetPostPlayTransformation(CardModel replacement) => TransformIntoAfterPlay = replacement;

    private async Task ApplyResolvedPostPlayMove(PlayerChoiceContext context, CardPlay play)
    {
        if (!ReferenceEquals(play.Card, this) || !play.IsLastInSeries || TransformIntoAfterPlay is not null
            || _requestedPostPlayPile is not { } destination || Pile?.Type != PileType.Play) return;
        if (play.ResultPile == PileType.Exhaust)
        {
            // Let native Exhaust finish and publish its real event before the existing exhaust callback moves us.
            _postPlayExhaustMovePile = destination;
            return;
        }
        if (play.ResultPile is PileType.Discard or PileType.Hand or PileType.Draw)
            await CardPileCmd.Add(this, destination,
                destination == PileType.Draw ? CardPilePosition.Top : CardPilePosition.Bottom);
    }

    public override bool TryModifyEnergyCostInCombat(CardModel card, decimal originalCost, out decimal modifiedCost)
    {
        modifiedCost = originalCost;
        if (!ReferenceEquals(card, this)) return false;
        var reduction = 0;
        for (var index = 0; index < Generated.Operations.Count; index++)
        {
            var operation = Generated.Operations[index];
            if (operation.Template == "F:CostPerMelody")
                reduction += OperationAmount(index) * MelodyCmd.MelodiesThisTurn(Owner);
            else if (operation.Template == "F:CostIfWahKi"
                     && (MelodyCmd.PlayedWahThisTurn(Owner) || MelodyCmd.PlayedKiThisTurn(Owner)))
                reduction += OperationAmount(index);
        }
        if (reduction <= 0) return false;
        modifiedCost = Math.Max(0m, originalCost - reduction);
        return true;
    }
}
