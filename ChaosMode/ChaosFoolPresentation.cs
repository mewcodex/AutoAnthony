using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using CardTag = MegaCrit.Sts2.Core.Entities.Cards.CardTag;

namespace AutoAnthony;

public abstract partial class ChaosCardModel
{
    private IEnumerable<IHoverTip> FoolHoverTips()
    {
        var templates = Generated.Operations.Select(operation => operation.Template).ToHashSet(StringComparer.Ordinal);
        if (templates.Contains("F:Aim") || templates.Contains("F:AimMultiplier") || templates.Contains("F:IncreaseAimRun"))
            yield return HoverTipFactory.FromPower<AimPower>();
        if (templates.Contains("F:Stretch") || templates.Contains("F:RefundStretch") || templates.Contains("F:StretchFromDamage"))
            yield return HoverTipFactory.FromPower<StretchPower>();
        if (templates.Contains("F:IfImpaled") || templates.Contains("F:IfOwnImpaled") || templates.Contains("F:Dislodge")
            || templates.Contains("F:DoubleAgainstImpaled") || templates.Contains("F:Serrated"))
            yield return HoverTipFactory.FromPower<ImpaledPower>();
        if (templates.Contains("F:ShootArrow") || templates.Contains("F:IfArrow") || templates.Contains("F:ArrowSlots")
            || templates.Contains("F:OnArrowImpaled") || templates.Contains("F:OnFirstArrowDislodged")
            || templates.Contains("F:WhenThisDislodgesArrow") || templates.Contains("F:Quietus")) yield return ArrowHoverTip;
        var melodies = Tags.Where(tag => tag is CardTag.Wah or CardTag.Cho or CardTag.Ki).ToHashSet();
        if (templates.Overlaps(["F:ForEachMelody", "F:OnMelodyGained", "F:OnAllMelodies", "F:MelodyReplay",
                "F:CostPerMelody", "F:DamagePerMelodyCard", "F:ExtraHitsPerMelody"]))
            melodies.UnionWith([CardTag.Wah, CardTag.Cho, CardTag.Ki]);
        if (templates.Overlaps(["F:IfWahKi", "F:IfWahCho", "F:CostIfWahKi", "F:OnWahGained"])) melodies.Add(CardTag.Wah);
        if (templates.Overlaps(["F:IfWahCho", "F:IfChoKi", "F:OnChoGained"])) melodies.Add(CardTag.Cho);
        if (templates.Overlaps(["F:IfWahKi", "F:IfChoKi", "F:IfKi", "F:CostIfWahKi"])) melodies.Add(CardTag.Ki);
        foreach (var operation in Generated.Operations.Where(operation => operation.Template == "F:PlayMelodyCards"))
        {
            var mask = OperationRuntimeSpecCompiler.RequireStructured(operation).Values.Single(value => value.Id == "melody_mask").BaseValue;
            foreach (var tag in FoolMelodySubset.Tags(mask))
                melodies.Add(tag switch { ChaosCardGenerator.CardTag.Wah => CardTag.Wah,
                    ChaosCardGenerator.CardTag.Cho => CardTag.Cho, _ => CardTag.Ki });
        }
        if (melodies.Contains(CardTag.Wah)) yield return HoverTipFactory.Static(StaticHoverTip.WahMelody);
        if (melodies.Contains(CardTag.Cho)) yield return HoverTipFactory.Static(StaticHoverTip.ChoMelody);
        if (melodies.Contains(CardTag.Ki)) yield return HoverTipFactory.Static(StaticHoverTip.KiMelody);
        if (templates.Contains("F:RandomTarot")) yield return HoverTipFactory.Static(StaticHoverTip.Tarot);
        if (templates.Contains("F:TransformThisToSadness")
            || templates.Contains("F:ExhaustSadness") || templates.Contains("F:OnSadnessGenerated"))
            yield return HoverTipFactory.FromCard<Sadness>();
        if (templates.Contains("F:CreateTheShot"))
            yield return HoverTipFactory.FromCard<TheShot>(FoolReferenceIsUpgraded("F:CreateTheShot"));
    }

    private bool FoolReferenceIsUpgraded(params string[] templates) => IsUpgraded && Generated.Upgrade?.Effects.Any(effect =>
        effect.Kind is CardUpgradeKind.UpgradeDerivative or CardUpgradeKind.UpgradeReferencedCards
        && effect.OperationIndex is { } index && (uint)index < (uint)Generated.Operations.Count
        && templates.Contains(Generated.Operations[index].Template)) == true;
}
