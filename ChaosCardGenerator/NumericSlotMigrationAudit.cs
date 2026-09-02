using System.Text;
using System.Text.RegularExpressions;

namespace ChaosCardGenerator;

/// <summary>Offline preparation report for replacing positional localized-text numbers with named runtime slots.</summary>
internal static class NumericSlotMigrationAudit
{
    private static readonly Regex Number = new(@"\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string Report()
    {
        var occurrences = new List<(GeneratedCharacter Character, bool Ultimate, string Recipe,
            ComponentAtom Atom)>();
        foreach (var character in Enum.GetValues<GeneratedCharacter>())
        foreach (var recipe in CharacterComponentCatalogs.Get(character, unlockComponentRoles: false).Recipes)
        foreach (var atom in recipe.Atoms)
            occurrences.Add((character, false, recipe.Id, atom));
        foreach (var recipe in CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad,
                     unlockComponentRoles: true).Recipes)
        foreach (var atom in recipe.Atoms)
            occurrences.Add((GeneratedCharacter.Ironclad, true, recipe.Id, atom));

        var output = new StringBuilder(
            "template\tscope\ttext\tnumbers\tlegacyDynamicIndex\tlegacyDynamicSlot\tsuggestedSlots\thasX\toccurrences\tcharacters\trecipes\n");
        foreach (var group in occurrences.GroupBy(item =>
                     (item.Atom.Template, item.Atom.Scope, item.Atom.ChineseText))
                 .OrderBy(group => group.Key.Template, StringComparer.Ordinal)
                 .ThenBy(group => group.Key.ChineseText, StringComparer.Ordinal))
        {
            var matches = Number.Matches(group.Key.ChineseText).Cast<Match>().ToArray();
            var dynamicIndex = LegacyDynamicIndex(group.Key.Template, group.Key.Scope, group.Key.ChineseText,
                matches.Length);
            var suggestions = matches.Select((match, index) =>
                $"{index}:{SuggestedSlot(group.Key.Template, group.Key.ChineseText, match, index, dynamicIndex)}")
                .ToArray();
            output.Append(group.Key.Template).Append('\t').Append(group.Key.Scope).Append('\t')
                .Append(group.Key.ChineseText.Replace("\r", "\\r", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal).Replace('\t', ' ')).Append('\t')
                .Append(string.Join(',', matches.Select(match => match.Value))).Append('\t')
                .Append(dynamicIndex).Append('\t')
                .Append(dynamicIndex >= 0 ? DynamicSlot(group.Key.Template) : string.Empty).Append('\t')
                .Append(string.Join(',', suggestions)).Append('\t')
                .Append(group.Key.ChineseText.Contains('X')).Append('\t').Append(group.Count()).Append('\t')
                .Append(string.Join(',', group.Select(item => item.Character).Distinct().Order())).Append('\t')
                .Append(string.Join(',', group.Select(item => item.Recipe).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal))).Append('\n');
        }
        return output.ToString();
    }

    private static int LegacyDynamicIndex(string template, OperationScope scope, string text, int count)
    {
        if (count == 0 || scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger
            or OperationScope.AbilityRule)
            return -1;
        if (template == "I:DrawAndBlockIfSkill" && count >= 2) return 1;
        if (scope == OperationScope.Modifier && text.Contains("消耗牌堆", StringComparison.Ordinal) && count >= 2)
            return count - 1;
        return 0;
    }

    private static string DynamicSlot(string template)
    {
        if (template.StartsWith("T:ProxyDamage_", StringComparison.Ordinal)
            || template.StartsWith("N:ProxyDamage_", StringComparison.Ordinal)) return "damage";
        return template switch
        {
            "T:D" or "T:DX" or "T:D_EnergyX" or "N:AllD" or "N:RandomD" or "N:RetaliateDamage"
                or "NCR:OstyDamage" or "NCR:OstyAllDamage" or "CL:RollingAllDamage" => "damage",
            "N:B" or "N_BLOCK" => "block",
            "N:E" or "N:NextTurnEnergy" or "D:GainEnergy" or "D:NextTurnEnergy" or "NCR:GainEnergy"
                or "NCR:NextTurnEnergy" or "R:GainEnergy" => "energy",
            "N:HP-" => "hp_loss",
            _ => "amount"
        };
    }

    private static string SuggestedSlot(string template, string text, Match match, int index, int dynamicIndex)
    {
        if (index == dynamicIndex) return DynamicSlot(template);
        var before = text[..match.Index];
        var after = text[(match.Index + match.Length)..];
        if (after.StartsWith("次", StringComparison.Ordinal))
            return text.Contains("伤害", StringComparison.Ordinal) ? "hits" : "count";
        if (after.StartsWith("个回合", StringComparison.Ordinal)
            || after.StartsWith("回合", StringComparison.Ordinal)) return "duration";
        if (after.StartsWith("张", StringComparison.Ordinal) || after.StartsWith("个", StringComparison.Ordinal))
            return before.EndsWith("每", StringComparison.Ordinal) ? "threshold" : "count";
        if (after.Contains("蓝星", StringComparison.Ordinal)) return "stars";
        if (after.Contains("费用", StringComparison.Ordinal) || after.Contains("耗能", StringComparison.Ordinal))
            return "cost";
        return $"value_{index}";
    }
}
