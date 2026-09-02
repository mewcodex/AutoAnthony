using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using System.Text.RegularExpressions;
using GeneratorTag = ChaosCardGenerator.CardTag;
using GameTag = MegaCrit.Sts2.Core.Entities.Cards.CardTag;

namespace AutoAnthony;

/// <summary>
/// ModelDb-backed catalogs for the remaining v111 characters. Each original effect is kept as an executable
/// independent operation until its clauses are promoted into the shared unit-operation interpreter; this provides
/// a safe fallback for character-specific mechanics without allowing them into another character's pool.
/// </summary>
public sealed class RuntimeCharacterCatalog : IComponentCatalog
{
    public GeneratedCharacter Character { get; }
    public IReadOnlyList<IroncladCardRecipe> Recipes { get; }
    public IReadOnlyList<ComponentAtom> Atoms { get; }
    public IReadOnlyList<int> ComponentCounts { get; }
    public IReadOnlySet<string> AtomKeys { get; }
    public IReadOnlyDictionary<string, int> AtomCounts { get; }
    public IReadOnlyDictionary<int, int> ComponentCountCounts { get; }
    public IReadOnlyDictionary<GeneratorTag, int> TagCounts { get; }

    private RuntimeCharacterCatalog(GeneratedCharacter character, IReadOnlyList<IroncladCardRecipe> recipes, IReadOnlyList<int> printedEffectCounts)
    {
        Character = character;
        Recipes = recipes;
        var atoms = recipes.SelectMany(recipe => recipe.Atoms).ToArray();
        Atoms = atoms.DistinctBy(atom => atom.Key).ToArray();
        AtomKeys = Atoms.Select(atom => atom.Key).ToHashSet();
        AtomCounts = atoms.GroupBy(atom => atom.Key).ToDictionary(group => group.Key, group => group.Count());
        ComponentCounts = printedEffectCounts.Distinct().Order().ToArray();
        ComponentCountCounts = printedEffectCounts.GroupBy(count => count).ToDictionary(group => group.Key, group => group.Count());
        TagCounts = recipes.SelectMany(recipe => recipe.Tags).GroupBy(tag => tag).ToDictionary(group => group.Key, group => group.Count());
    }

    public static RuntimeCharacterCatalog Create(GeneratedCharacter character)
    {
        var cards = OriginalCards(character)
            .Where(card => card.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly)
            .ToArray();
        if (cards.Length != 86)
            throw new InvalidOperationException($"{character} 单人原卡目录应为86张，实际为{cards.Length}张。");

        var originalLanguage = LocManager.Instance.Language;
        IReadOnlyDictionary<Type, (string Title, string Description)> chinese;
        IReadOnlyDictionary<Type, (string Title, string Description)> english;
        try
        {
            LocManager.Instance.SetLanguage("zhs");
            chinese = Snapshot(cards);
            LocManager.Instance.SetLanguage("eng");
            english = Snapshot(cards);
        }
        finally
        {
            if (LocManager.Instance.Language != originalLanguage)
                LocManager.Instance.SetLanguage(originalLanguage);
        }

        var effectCounts = new List<int>(cards.Length);
        var recipes = new List<IroncladCardRecipe>(cards.Length);
        foreach (var canonical in cards)
        {
            var description = chinese[canonical.GetType()].Description;
            var printedEffectCount = Math.Clamp(description.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, 1, 3);
            var type = canonical.Type switch
            {
                CardType.Attack => GeneratedCardType.Attack,
                CardType.Power => GeneratedCardType.Power,
                _ => GeneratedCardType.Skill
            };
            var target = canonical.TargetType == TargetType.AnyEnemy
                ? TargetMode.SingleEnemy
                : TargetMode.Other;
            var costsX = canonical.EnergyCost.CostsX;
            var baseTags = Tags(canonical);
            IReadOnlyList<ComponentAtom> atoms;
            IReadOnlyList<int> triggerOwners;
            if (DetailedCharacterDecomposer.TryDecompose(character, canonical,
                    chinese[canonical.GetType()].Description,
                    english[canonical.GetType()].Description,
                    out var detailed))
            {
                atoms = detailed.Atoms;
                triggerOwners = detailed.TriggerOwners;
                effectCounts.Add(atoms.Count);
            }
            else if (!costsX && !canonical.HasStarCostX && TryDecomposeSimple(description, type, target, out var simpleAtoms))
            {
                atoms = simpleAtoms;
                triggerOwners = Enumerable.Repeat(-1, atoms.Count).ToArray();
                effectCounts.Add(printedEffectCount);
            }
            else
            {
                var scope = type == GeneratedCardType.Power ? OperationScope.AbilityRule
                    : target == TargetMode.SingleEnemy ? OperationScope.SingleEnemyOnly
                    : OperationScope.Independent;
                var damagePrefix = type == GeneratedCardType.Attack
                    ? target == TargetMode.SingleEnemy ? "T:ProxyDamage_" : "N:ProxyDamage_"
                    : type == GeneratedCardType.Power ? "A:Proxy_" : "I:Proxy_";
                var template = damagePrefix
                    + (costsX ? "X_" : string.Empty)
                    + (canonical.HasStarCostX ? "StarX_" : string.Empty)
                    + canonical.GetType().Name;
                ExternalOperationTextRegistry.Register(template, english[canonical.GetType()].Description);
                atoms = [new ComponentAtom(template, scope, description, target == TargetMode.SingleEnemy, CardReferenceRequirement.None)];
                triggerOwners = [-1];
                effectCounts.Add(printedEffectCount);
            }
            var upgraded = canonical.ToMutable();
            if (upgraded.IsUpgradable) CardCmd.Upgrade(upgraded);
            var upgradedTags = Tags(upgraded);
            var addedOnUpgrade = upgradedTags.Except(baseTags).ToArray();
            var removedOnUpgrade = baseTags.Except(upgradedTags).ToArray();
            foreach (var template in atoms.Select(atom => atom.Template).Distinct(StringComparer.Ordinal))
                ExternalOperationUpgradeRegistry.Register(character, template, addedOnUpgrade, removedOnUpgrade);
            var tags = baseTags;
            recipes.Add(new IroncladCardRecipe(
                canonical.GetType().Name,
                chinese[canonical.GetType()].Title,
                costsX ? -1 : canonical.EnergyCost.Canonical,
                type,
                target,
                Rarity(canonical.Rarity),
                tags,
                atoms,
                triggerOwners,
                character == GeneratedCharacter.Regent ? canonical.CanonicalStarCost : -1,
                character == GeneratedCharacter.Regent && canonical.HasStarCostX,
                english[canonical.GetType()].Title));
        }
        return new RuntimeCharacterCatalog(character, recipes, effectCounts);
    }

    private static bool TryDecomposeSimple(string description, GeneratedCardType type, TargetMode target,
        out IReadOnlyList<ComponentAtom> atoms)
    {
        var result = new List<ComponentAtom>();
        foreach (var raw in description.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = raw.Trim();
            ComponentAtom? atom = null;
            if (Regex.IsMatch(line, @"^造成\d+点伤害。?$"))
                atom = new("T:D", OperationScope.SingleEnemyOnly, line, true, CardReferenceRequirement.None);
            else if (Regex.IsMatch(line, @"^对所有敌人造成\d+点伤害(?:\d+次)?。?$"))
                atom = new("N:AllD", OperationScope.NonTargeted, line, false, CardReferenceRequirement.None);
            else if (Regex.IsMatch(line, @"^获得\d+点格挡。?$"))
                atom = new("N:B", OperationScope.NonTargeted, line, false, CardReferenceRequirement.None);
            else if (Regex.IsMatch(line, @"^抽\d+张牌。?$"))
                atom = new("N:Draw", OperationScope.NonTargeted, line, false, CardReferenceRequirement.None);
            else if (Regex.IsMatch(line, @"^给予\d+层(?:易伤|虚弱)。?$"))
                atom = new("T:Apply", OperationScope.SingleEnemyOnly, line, true, CardReferenceRequirement.None);
            else if (Regex.IsMatch(line, @"^获得\d+点力量。?$"))
                atom = new("N:Self", OperationScope.NonTargeted, line, false, CardReferenceRequirement.None);
            else if (Regex.IsMatch(line, @"^获得\d+点敏捷。?$"))
                atom = new("N:Dex", OperationScope.NonTargeted, line, false, CardReferenceRequirement.None);
            if (atom is null)
            {
                atoms = Array.Empty<ComponentAtom>();
                return false;
            }
            result.Add(atom);
        }
        var hasDamage = result.Any(atom => atom.Template is "T:D" or "N:AllD");
        var hasSingleTarget = result.Any(atom => atom.RequiresSingleTarget);
        if (result.Count == 0
            || (type == GeneratedCardType.Attack) != hasDamage
            || (target == TargetMode.SingleEnemy) != hasSingleTarget)
        {
            atoms = Array.Empty<ComponentAtom>();
            return false;
        }
        atoms = result;
        return true;
    }

    private static IReadOnlyDictionary<Type, (string Title, string Description)> Snapshot(IEnumerable<CardModel> cards) =>
        cards.ToDictionary(card => card.GetType(), card =>
        {
            var mutable = card.ToMutable();
            var description = mutable.GetDescriptionForPile(PileType.None)
                .Replace("[gold]", string.Empty, StringComparison.Ordinal)
                .Replace("[/gold]", string.Empty, StringComparison.Ordinal)
                .Trim();
            var keywordTitles = mutable.Keywords.Select(KeywordTitle).ToHashSet(StringComparer.OrdinalIgnoreCase);
            description = string.Join('\n', description
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !keywordTitles.Contains(line.Trim().TrimEnd('。', '.'))));
            return (card.Title, description);
        });

    private static string KeywordTitle(CardKeyword keyword)
    {
        var chinese = LocManager.Instance.Language is "zhs" or "zht";
        return (keyword, chinese) switch
        {
            (CardKeyword.Exhaust, true) => "消耗",
            (CardKeyword.Innate, true) => "固有",
            (CardKeyword.Retain, true) => "保留",
            (CardKeyword.Sly, true) => "奇巧",
            (CardKeyword.Ethereal, true) => "虚无",
            (CardKeyword.Eternal, true) => "永恒",
            (CardKeyword.Unplayable, true) => "不能被打出",
            (CardKeyword.Exhaust, false) => "Exhaust",
            (CardKeyword.Innate, false) => "Innate",
            (CardKeyword.Retain, false) => "Retain",
            (CardKeyword.Sly, false) => "Sly",
            (CardKeyword.Ethereal, false) => "Ethereal",
            (CardKeyword.Eternal, false) => "Eternal",
            (CardKeyword.Unplayable, false) => "Unplayable",
            _ => string.Empty
        };
    }

    private static IEnumerable<CardModel> OriginalCards(GeneratedCharacter character) => character switch
    {
        GeneratedCharacter.Defect => ModelDb.CardPool<DefectCardPool>().AllCards,
        GeneratedCharacter.Necrobinder => ModelDb.CardPool<NecrobinderCardPool>().AllCards,
        GeneratedCharacter.Regent => ModelDb.CardPool<RegentCardPool>().AllCards,
        _ => throw new ArgumentOutOfRangeException(nameof(character))
    };

    private static GeneratedRarity Rarity(CardRarity rarity) => rarity switch
    {
        CardRarity.Basic => GeneratedRarity.Basic,
        CardRarity.Common => GeneratedRarity.Common,
        CardRarity.Uncommon => GeneratedRarity.Uncommon,
        CardRarity.Rare => GeneratedRarity.Rare,
        CardRarity.Ancient => GeneratedRarity.Ancient,
        _ => throw new ArgumentOutOfRangeException(nameof(rarity))
    };

    private static IReadOnlyList<GeneratorTag> Tags(CardModel card)
    {
        var tags = new List<GeneratorTag>();
        if (card.Tags.Contains(GameTag.Strike)) tags.Add(GeneratorTag.Strike);
        if (card.Tags.Contains(GameTag.Defend)) tags.Add(GeneratorTag.Defend);
        foreach (var keyword in card.Keywords)
        {
            var mapped = keyword switch
            {
                CardKeyword.Exhaust => GeneratorTag.Exhaust,
                CardKeyword.Innate => GeneratorTag.Innate,
                CardKeyword.Retain => GeneratorTag.Retain,
                CardKeyword.Sly => GeneratorTag.Sly,
                CardKeyword.Ethereal => GeneratorTag.Ethereal,
                CardKeyword.Eternal => GeneratorTag.Eternal,
                CardKeyword.Unplayable => GeneratorTag.Unplayable,
                _ => (GeneratorTag?)null
            };
            if (mapped is { } value) tags.Add(value);
        }
        if (card.Tags.Contains(GameTag.OstyAttack)) tags.Add(GeneratorTag.OstyAttack);
        return tags;
    }
}
