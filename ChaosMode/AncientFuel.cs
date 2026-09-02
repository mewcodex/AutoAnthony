using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;

namespace AutoAnthony;

/// <summary>
/// A registered Token card so it is always visible in the card library's Miscellaneous category.  The run-level
/// easter egg only controls whether derivative fuel creation resolves to this model.
/// </summary>
public sealed class AncientFuel : CardModel
{
    protected override IEnumerable<IHoverTip> ExtraHoverTips => [EnergyHoverTip];
    public override IEnumerable<CardKeyword> CanonicalKeywords => [CardKeyword.Exhaust];
    protected override IEnumerable<DynamicVar> CanonicalVars =>
        [new EnergyVar(1), new CardsVar(1)];

    // Match vanilla Fuel: Token rarity + Token pool places this card in the library's Miscellaneous category.
    public override CardPoolModel Pool => ModelDb.CardPool<TokenCardPool>();
    public override string PortraitPath => ModelDb.Card<Fuel>().PortraitPath;
    public override string BetaPortraitPath => ModelDb.Card<Fuel>().BetaPortraitPath;
    public override IEnumerable<string> AllPortraitPaths => [ModelDb.Card<Fuel>().PortraitPath];

    public AncientFuel() : base(0, CardType.Skill, CardRarity.Token, TargetType.Self) { }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PlayerCmd.GainEnergy(DynamicVars.Energy.BaseValue, Owner);
        await CardPileCmd.Draw(choiceContext, DynamicVars.Cards.BaseValue, Owner);
    }

    protected override void OnUpgrade() => DynamicVars.Cards.UpgradeValueBy(1m);
}
