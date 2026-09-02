using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace AutoAnthony;

/// <summary>
/// The base game's temporary Focus powers deliberately identify themselves as the source card (Focused Strike,
/// Synchronize, or Hyperbeam) and add that card as a hover tip. Generated cards need the same turn-end mechanics
/// without claiming to come from an unrelated original card.
/// </summary>
public sealed class ChaosTemporaryFocusPower : TemporaryFocusPower
{
    public override AbstractModel OriginModel => ModelDb.Power<FocusPower>();
    public override LocString Title => new("powers", "CHAOS_TEMPORARY_FOCUS_POWER.title");
    protected override IEnumerable<IHoverTip> ExtraHoverTips => [HoverTipFactory.FromPower<FocusPower>()];
}

public sealed class ChaosTemporaryFocusDownPower : TemporaryFocusPower
{
    public override AbstractModel OriginModel => ModelDb.Power<FocusPower>();
    public override LocString Title => new("powers", "CHAOS_TEMPORARY_FOCUS_DOWN_POWER.title");
    protected override bool IsPositive => false;
    protected override IEnumerable<IHoverTip> ExtraHoverTips => [HoverTipFactory.FromPower<FocusPower>()];
}
