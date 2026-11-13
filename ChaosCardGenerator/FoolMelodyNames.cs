namespace ChaosCardGenerator;

/// <summary>Typed name-fragment provenance; native gameplay never guesses tags from a displayed title.</summary>
public static class FoolMelodyNames
{
    private static readonly Lazy<HashSet<string>> NativeNames = new(() => CharacterComponentCatalogs
        .Get(GeneratedCharacter.Fool).Recipes.SelectMany(recipe => new[] { recipe.ChineseTitle, recipe.EnglishTitle })
        .Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase));
    private static readonly Lazy<HashSet<string>> NativeIds = new(() => CharacterComponentCatalogs
        .Get(GeneratedCharacter.Fool).Recipes.Select(recipe => recipe.Id).ToHashSet(StringComparer.Ordinal));

    private static string Normalize(string name) => string.Concat(name.Where(character => !char.IsWhiteSpace(character)));
    private static CardTag? PrefixTag(string id) => id switch
    {
        "WahBlast" or "WahCho" or "WahChoKi" or "WahHammer" or "WahKi" or "WahSymphony" or "WahSweep" or "WahTouch" => CardTag.Wah,
        "ChoBarbs" or "ChoChorus" or "ChoFirm" or "ChoKi" or "ChoLimbs" or "ChoReady" => CardTag.Cho,
        "KiChant" or "KiHop" or "KiRest" or "KiVerse" or "KiWhip" => CardTag.Ki,
        _ => null
    };
    private static CardTag[] StemTags(string id) => id switch
    {
        "WahCho" => [CardTag.Cho],
        "WahChoKi" => [CardTag.Cho, CardTag.Ki],
        "WahKi" or "ChoKi" => [CardTag.Ki],
        _ => []
    };

    internal static GeneratedCardName? Compose(GeneratedCardName candidate, GeneratedCard card, NameParts first,
        NameParts second, string chineseSegment)
    {
        var prefix = PrefixTag(first.Id);
        var stemTags = StemTags(second.Id);
        var melody = (prefix is { } tag ? new[] { tag } : []).Concat(stemTags).Distinct().ToArray();
        var relevant = NativeIds.Value.Contains(first.Id) || NativeIds.Value.Contains(second.Id) || melody.Length > 0
            || card.Operations.Any(operation => operation.Template == "F:ShootArrow");
        if (!relevant) return candidate;
        var chinese = candidate.Chinese;
        var english = candidate.English;
        if (melody.Length > 0)
        {
            // Melody syllables are a typed prefix. Keep one reviewed body fragment rather than retaining the
            // entire ordinary two-source name and then prepending a third fragment.
            var body = string.Concat(chineseSegment.Where(character => character is not ('哇' or '啾' or '叽'))).Trim();
            var englishBody = stemTags.Length > 0 ? string.Empty : second.EnglishMiddle;
            // A pure-melody second source contributes its syllables, not a body. Reuse one body fragment from
            // the first source so Wah + Cho does not always collapse to the forbidden original title "Wah Cho".
            if (body.Length == 0)
            {
                body = first.ChineseParts.LastOrDefault(part => part.Any(character => character is not ('哇' or '啾' or '叽')))
                    ?? string.Empty;
                englishBody = StemTags(first.Id).Length > 0 ? string.Empty : first.EnglishMiddle;
            }
            if (card.Tags.Contains(CardTag.Strike)) { body = "打击"; englishBody = "Strike"; }
            else if (CardEffectRules.IsHangDamageFamily(card.Operations)) { body = "吊杀"; englishBody = "Hang"; }
            else if (card.Operations.Any(IsShoot)) { body = "射击"; englishBody = "Shot"; }
            if (body.Length > 0 && !body.Any(char.IsLetterOrDigit)) return null;
            var syllables = string.Join(" ", melody.Select(tag => tag switch
                { CardTag.Wah => "哇", CardTag.Cho => "啾", _ => "叽" }));
            chinese = body.Length == 0 ? syllables : syllables + " " + body;
            englishBody = englishBody.Length == 0 ? englishBody : char.ToUpperInvariant(englishBody[0]) + englishBody[1..];
            english = string.Join(" ", melody.Select(tag => tag.ToString()).Concat(
                englishBody.Length == 0 ? [] : new[] { englishBody }));
        }
        var shoots = card.Operations.Any(operation => operation.Template == "F:ShootArrow");
        if (shoots && !card.Tags.Contains(CardTag.Strike))
        {
            if (!chinese.EndsWith("射击", StringComparison.Ordinal)) chinese += "射击";
            if (!english.EndsWith(" Shot", StringComparison.Ordinal) && english != "Shot") english += " Shot";
        }
        else if (chinese.EndsWith("射击", StringComparison.Ordinal) || english.EndsWith(" Shot", StringComparison.Ordinal))
            return null;
        if (NativeNames.Value.Contains(Normalize(chinese)) || NativeNames.Value.Contains(Normalize(english))) return null;
        return candidate with { Chinese = chinese, English = english, MelodyTags = melody.Distinct().ToArray() };
    }

    private static bool IsShoot(GeneratorOperation operation) => operation.Template == "F:ShootArrow";

    public static GeneratedCard ApplyNameTags(GeneratedCard card)
    {
        if (card.Name?.MelodyTags is not { } melody) return card;
        return card with { Tags = card.Tags.Where(tag => tag is not (CardTag.Wah or CardTag.Cho or CardTag.Ki))
            .Concat(melody).Distinct().ToArray() };
    }
}
