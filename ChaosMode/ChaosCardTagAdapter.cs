using GeneratorTag = ChaosCardGenerator.CardTag;
using GameCardTag = MegaCrit.Sts2.Core.Entities.Cards.CardTag;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace AutoAnthony;

/// <summary>Single boundary between the generator's stable wire tags and STS2 card enums.</summary>
internal static class ChaosCardTagAdapter
{
    internal static bool TryKeyword(GeneratorTag tag, out CardKeyword keyword)
    {
        keyword = tag switch
        {
            GeneratorTag.Exhaust => CardKeyword.Exhaust,
            GeneratorTag.Innate => CardKeyword.Innate,
            GeneratorTag.Retain => CardKeyword.Retain,
            GeneratorTag.Sly => CardKeyword.Sly,
            GeneratorTag.Ethereal => CardKeyword.Ethereal,
            GeneratorTag.Eternal => CardKeyword.Eternal,
            GeneratorTag.Unplayable => CardKeyword.Unplayable,
            _ => default
        };
        return ChaosCardGenerator.GeneratedCardTagPolicy.IsNativeKeyword(tag);
    }

    internal static bool TrySemanticTag(GeneratorTag tag, out GameCardTag gameTag)
    {
        gameTag = tag switch
        {
            GeneratorTag.Strike => GameCardTag.Strike,
            GeneratorTag.Defend => GameCardTag.Defend,
            GeneratorTag.OstyAttack => GameCardTag.OstyAttack,
            _ => default
        };
        return ChaosCardGenerator.GeneratedCardTagPolicy.IsSemanticTag(tag);
    }
}
