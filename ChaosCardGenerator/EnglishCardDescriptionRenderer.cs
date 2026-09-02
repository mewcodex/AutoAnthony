using System.Text.RegularExpressions;

namespace ChaosCardGenerator;

public static class EnglishCardDescriptionRenderer
{
    public static void ValidateRenderedEnglish(string text)
    {
        if (Regex.IsMatch(text, @"(?<![+\d])\b1 (?:random |Lightning |Frost |Dark |Plasma |Glass )?(?:cards|Attacks|Skills|Powers|Orbs|Stars|Souls|Wounds|Shivs|times)\b")
            || Regex.IsMatch(text, @"\b(?!1\b)(?:\d+|X(?:\+1)?) (?:random |Lightning |Frost |Dark |Plasma |Glass )?(?:card|Attack|Skill|Power|Orb|Star|Soul|Wound|Shiv|time)(?:\+)?(?!\w| [Ss]lots)")
            || Regex.IsMatch(text, @"\b1 additional times\b"))
            throw new InvalidOperationException($"Generated English card text has incorrect number agreement: {text}");
        if (text.Contains("The next Skill you play this turn are", StringComparison.Ordinal))
            throw new InvalidOperationException($"Generated English card text has incorrect subject agreement: {text}");
        if (Regex.IsMatch(text, @"\b(?:random random|\d+ s\b|Orbs slots|aLL enemies)"))
            throw new InvalidOperationException($"Generated English card text contains a localization artifact: {text}");
        if (Regex.IsMatch(text, @"\. [a-z]"))
            throw new InvalidOperationException($"Generated English card text starts a sentence with lowercase text: {text}");
    }

    private static string RenderEffects(IReadOnlyList<GeneratorOperation> effects)
    {
        var pieces = new List<string>();
        for (var index = 0; index < effects.Count; index++)
        {
            var operation = effects[index];
            if (CardEffectRules.IsCurrentBlockDamageAnchor(effects, index))
                continue;
            if (CardEffectRules.IsDependencyPrefix(operation) && index + 1 < effects.Count
                && CardEffectRules.IsLegalDependencyPayoff(operation, effects[index + 1]))
            {
                pieces.Add(OperationText(operation).TrimEnd('.') + " " + LowerFirst(OperationText(effects[++index])));
                continue;
            }
            pieces.Add(OperationText(operation));
        }
        return string.Join(" ", pieces);
    }

    private static string RenderTriggeredEffects(IReadOnlyList<GeneratorOperation> effects)
        => CardDescriptionRenderer.JoinTriggeredEffects(RenderEffects(effects), chinese: false);

    public static string Render(IReadOnlyList<GeneratorOperation> operations)
    {
        var lines = new List<string>();
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (CardEffectRules.IsCurrentBlockDamageAnchor(operations, index))
                continue;
            // Slot selectors are interpreter metadata. Every operation that owns the slot already says
            // which card is chosen, so printing this marker produces duplicate instructions.
            if (operation.Template is "N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK")
                continue;
            if (operation.Template == "I:ExhaustRandomAttack"
                && index + 1 < operations.Count
                && operations[index + 1].Template == "I:AddExhaustedAttackDamage")
            {
                var chosen = OperationRuntimeSpecCompiler.GetOrCompile(operation).Variant
                    is "selected" or "i_exhaustselectedattack";
                lines.Add(chosen
                    ? "Choose an Attack in your Hand to Exhaust and add its damage to this card."
                    : "Exhaust a random Attack in your Hand and add its damage to this card.");
                index++;
                continue;
            }
            if (operation.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger)
            {
                var effects = operations.Skip(index + 1)
                    .Where(candidate => candidate.Scope is not OperationScope.AbilityTrigger and not OperationScope.ConditionalTrigger)
                    .Where(candidate => candidate.Parameters.TryGetValue("triggerIndex", out var trigger) && trigger == index)
                    .ToArray();
                if (CardEffectRules.IsNextAttackGrantTrigger(operation))
                {
                    lines.Add(CardDescriptionRenderer.RenderNextAttackGrant(operation, effects, OperationText, chinese: false));
                    continue;
                }
                var trigger = OperationText(operation).TrimEnd('.');
                if (effects.Length == 0)
                {
                    lines.Add(trigger + ".");
                }
                else if (operation.Template == "C:playableIfDrawPileEmpty")
                {
                    lines.Add($"{trigger}. {UpperFirst(RenderTriggeredEffects(effects))}");
                }
                else
                {
                    lines.Add($"{trigger}, {LowerFirst(RenderTriggeredEffects(effects))}");
                }
                continue;
            }
            if (!operation.Parameters.ContainsKey("triggerIndex"))
            {
                if (CardEffectRules.IsDependencyPrefix(operation) && index + 1 < operations.Count
                    && !operations[index + 1].Parameters.ContainsKey("triggerIndex")
                    && CardEffectRules.IsLegalDependencyPayoff(operation, operations[index + 1]))
                {
                    lines.Add(RenderEffects([operation, operations[++index]]));
                    continue;
                }
                lines.Add(OperationText(operation));
            }
        }
        return string.Join("\n", lines);
    }

    public static string OperationText(GeneratorOperation operation)
    {
        if ((operation.DerivativeId == "debris"
                || operation.Template is "R:AddDebrisToHand" or "R:FillHandWithDebris")
            && operation.ChineseText.Contains("残骸", StringComparison.Ordinal))
            operation = operation with { ChineseText = operation.ChineseText.Replace("残骸", "碎屑", StringComparison.Ordinal) };
        // Template-level compatibility for old snapshots whose pre-v0.2 Sic 'Em text either used "attacks"
        // or omitted the Summon amount entirely. Handle these before registry lookup can reject the old text.
        if (operation.Template == "NCR:WheneverOstyAttacksTargetThisTurn")
            return "Whenever Osty hits this enemy this turn,";
        if (operation.Template == "NCR:ApplyPower_SicEmPower")
            return $"Summon {(Regex.Match(operation.ChineseText, @"\d+") is { Success: true } summon ? summon.Value : "3")}.";
        string text;
        // Derivative identity is part of the operation, whereas the external text registry is shared process-wide
        // and keyed only by template/text. Resolve derivatives first so one Ultimate-Chaos character cannot
        // overwrite singular/plural wording later rendered for another character using the same source template.
        // Orb X variants still consult the registry first because SpecialXCardConverter registers their X wording.
        if (SpecialXCardConverter.IsSpecial(operation)
            && ExternalOperationTextRegistry.TryGet(operation.Template, operation.ChineseText,
                out var specialXRegistered))
        {
            // Special-X conversion registers the already derivative-aware English projection under the exact
            // converted Chinese face. Consult it before rebuilding derivative wording from the source template;
            // otherwise an X-valued Shiv/Soul/etc. is looked up as an unregistered source derivative.
            text = specialXRegistered;
        }
        else if (DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template) is { } derivative
            && DerivativeSlotCatalog.Source(operation.Template) is { } source)
        {
            var derivativeName = DerivativeSlotCatalog.ChineseCardName(operation);
            var sourceEnchantment = DerivativeSlotCatalog.ResolveEnchantment(null, null, operation.Template);
            var sourceName = DerivativeEnchantmentCatalog.ChineseCardName(source, sourceEnchantment);
            var upgradedDerivative = operation.ChineseText.Contains(derivativeName + "+", StringComparison.Ordinal);
            var sourceChinese = operation.ChineseText.Replace(derivativeName,
                sourceName, StringComparison.Ordinal);
            var lookupChinese = upgradedDerivative
                ? sourceChinese.Replace(sourceName + "+", sourceName, StringComparison.Ordinal)
                : sourceChinese;
            var sourceEnglish = ExternalOperationTextRegistry.TryGet(operation.Template, lookupChinese, out var mapped)
                ? mapped
                : ToEnglish(lookupChinese);
            var enchantment = DerivativeSlotCatalog.ResolveEnchantment(operation.DerivativeId,
                operation.DerivativeEnchantmentId, operation.Template);
            text = DerivativeSlotCatalog.ReplaceEnglish(sourceEnglish, operation.Template, derivative, enchantment);
            if (upgradedDerivative) text = DerivativeSlotCatalog.MarkEnglishUpgrade(text, derivative);
        }
        else if (ExternalOperationTextRegistry.TryGet(operation.Template, operation.ChineseText, out var registered))
            text = registered;
        else if (OrbSlotCatalog.IsSlotOperation(operation.Template))
            text = OrbSlotCatalog.EnglishText(operation);
        else
            text = ToEnglish(operation.ChineseText);
        text = operation.Template switch
        {
            "A:when" when Regex.Match(operation.ChineseText, @"第(\d+)张攻击牌") is { Success: true } attackOrdinal
                => $"Whenever you play your {OrdinalWord(int.Parse(attackOrdinal.Groups[1].Value))} Attack each turn.",
            "I:ReplayNextSkills" when Regex.Match(operation.ChineseText, @"下(\d+)张技能牌") is { Success: true } replayCount
                => int.Parse(replayCount.Groups[1].Value) == 1
                    ? "The next Skill you play this turn is played twice."
                    : $"The next {replayCount.Groups[1].Value} Skills you play this turn are played twice.",
            "I:AutoPlayRandomAttackFromHand" when Regex.Match(operation.ChineseText, @"(\d+)张攻击牌") is { Success: true } attackCount
                => int.Parse(attackCount.Groups[1].Value) == 1
                    ? "Play 1 random Attack from your Hand against a random enemy."
                    : $"Play {attackCount.Groups[1].Value} random Attacks from your Hand against random enemies.",
            "D:CreateZeroCostCopyInDiscard" =>
                "Add a 0{energyPrefix:energyIcons(1)} copy of this card into your Discard Pile.",
            "I:IncreaseDamageThisCombat" when Regex.Match(operation.ChineseText, @"(\d+)点") is { Success: true } increase
                => $"Increase this card's damage by {increase.Groups[1].Value} this combat.",
            "CL:ChooseFromRandomDrawCards" when Regex.Match(operation.ChineseText, @"随机(\d+)张牌") is { Success: true } options
                => $"Choose 1 of {options.Groups[1].Value} cards in your Draw Pile to add into your Hand.",
            "D:GainTemporaryFocus" when !text.Contains("this turn", StringComparison.OrdinalIgnoreCase)
                => text.TrimEnd('.') + " this turn.",
            "C:untilTurnEndCardDrawn" => "After you play this card, whenever you draw a card this turn.",
            "R:WheneverDrawn" => "Whenever you draw this card,",
            "R:CopySelectedColorlessCard" => "Choose a Colorless card in your Hand. Add a copy of that card into your Hand.",
            "R:KingsSwordDoubleDamageThisTurn" => "Sovereign Blade deals double damage to the enemy this turn.",
            "R:PutKingsSwordInHand" when operation.DerivativeId is null => "Put Sovereign Blade into your Hand from anywhere.",
            "R:KingsSwordHitsAllEnemies" => "Sovereign Blade now deals damage to ALL enemies.",
            "R:ReflectBlockedDamageThisTurn" => "Blocked attack damage is reflected to your attacker this turn.",
            "R:ReturnAfterSkillsPlayed" => Regex.Replace(text,
                @"^Whenever you play (\d+) Skills, return this from your discard pile to your hand\.?$",
                "Every $1 Skills you play in a turn, put this into your Hand."),
            // The structured catalog already stores the reviewed English projection. Keep it authoritative;
            // reinterpreting it here made output depend on whether another character happened to register the
            // same operation before this card was rendered.
            "R:CostDownWhenDrawn" => text,
            "R:DamageUpWhenDrawn" => text,
            _ => text
        };
        // Template-specific overrides run after the registry's ordinary grammar normalization. Numeric Random
        // can change an override's value (for example, “Choose 1 of 1 cards”), so normalize once more here.
        return UpperFirst(ExternalOperationTextRegistry.NormalizeNumberAgreement(text));
    }

    private static string OrdinalWord(int value) => value switch
    {
        1 => "first",
        2 => "second",
        3 => "third",
        4 => "fourth",
        5 => "fifth",
        _ => value % 100 is 11 or 12 or 13 ? $"{value}th" : (value % 10) switch
        {
            1 => $"{value}st",
            2 => $"{value}nd",
            3 => $"{value}rd",
            _ => $"{value}th"
        }
    };

    internal static string TranslateLiteral(string chineseText) => ToEnglish(chineseText);

    private static string LowerFirst(string text)
    {
        if (text.Length == 0 || text.StartsWith("Osty", StringComparison.Ordinal)
            || text.StartsWith("ALL", StringComparison.Ordinal)
            || text.StartsWith("King's Sword", StringComparison.Ordinal)
            || text.Length > 1 && char.IsUpper(text[0]) && char.IsUpper(text[1]))
            return text;
        return char.ToLowerInvariant(text[0]) + text[1..];
    }
    private static string UpperFirst(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string ToEnglish(string chinese)
    {
        var text = chinese.TrimEnd('。');
        string? Match(string pattern, string format)
        {
            var match = Regex.Match(text, pattern);
            return match.Success ? string.Format(format, match.Groups.Cast<Group>().Skip(1).Select(group => group.Value).ToArray()) : null;
        }

        var english =
            Match(@"^造成(\d+)点伤害$", "Deal {0} damage") ??
            Match(@"^对所有敌人造成(\d+)点伤害(?:(\d+|X(?:\+1)?)次)?$", "Deal {0} damage to ALL enemies{1}") ??
            Match(@"^随机对敌人造成(\d+)点伤害(?:(\d+|X(?:\+1)?)次)?$", "Deal {0} damage to a random enemy{1}") ??
            Match(@"^获得(\d+)点格挡$", "Gain {0} Block") ??
            (text == "抽1张牌" ? "Draw 1 card" : null) ??
            Match(@"^抽(\d+)张牌$", "Draw {0} cards") ??
            Match(@"^获得(\d+)点能量$", "Gain {0} Energy") ??
            Match(@"^失去(\d+)点生命$", "Lose {0} HP") ??
            Match(@"^回复(\d+)点生命$", "Heal {0} HP") ??
            Match(@"^给予(\d+)层易伤$", "Apply {0} Vulnerable") ??
            Match(@"^给予(\d+)层虚弱$", "Apply {0} Weak") ??
            Match(@"^给予(\d+)层中毒$", "Apply {0} Poison") ??
            Match(@"^获得(\d+)点敏捷$", "Gain {0} Dexterity") ??
            Match(@"^获得(\d+)点荆棘$", "Gain {0} Thorns") ??
            Match(@"^本回合获得(\d+)点敏捷$", "Gain {0} Dexterity this turn") ??
            Match(@"^获得(\d+)层无实体$", "Gain {0} Intangible") ??
            Match(@"^失去(\d+)点敏捷$", "Lose {0} Dexterity") ??
            Match(@"^丢弃(\d+)张牌$", "Discard {0} cards") ??
            Match(@"^将(\d+)张小刀\+加入手牌$", "Add {0} Shivs+ to your hand") ??
            Match(@"^将(\d+)张小刀加入手牌$", "Add {0} Shivs to your hand") ??
            Match(@"^将(\d+)张墨影小刀\+加入手牌$", "Add {0} Ink Shivs+ to your hand") ??
            Match(@"^将(\d+)张墨影小刀加入手牌$", "Add {0} Ink Shivs to your hand") ??
            Match(@"^在下个回合，获得(\d+)点格挡$", "Next turn, gain {0} Block") ??
            Match(@"^在下个回合，获得(\d+)点能量$", "Next turn, gain {0} Energy") ??
            Match(@"^在下个回合，抽(\d+)张牌$", "Next turn, draw {0} cards") ??
            Match(@"^给予所有敌人(\d+)层中毒$", "Apply {0} Poison to ALL enemies") ??
            Match(@"^给予所有敌人(\d+)层虚弱$", "Apply {0} Weak to ALL enemies") ??
            Match(@"^所有敌人在本回合失去(\d+)点力量$", "ALL enemies lose {0} Strength this turn") ??
            Match(@"^随机给予敌人(\d+)层中毒(\d+)次$", "Apply {0} Poison to random enemies {1} times") ??
            Match(@"^本回合你每打出一张牌，该敌人失去(\d+)点生命$", "Whenever you play a card this turn, that enemy loses {0} HP") ??
            (text == "敌人失去X点力量" ? "The enemy loses X Strength" : null) ??
            (text == "敌人失去X+1点力量" ? "The enemy loses X+1 Strength" : null) ??
            (text == "给予X层虚弱" ? "Apply X Weak" : null) ??
            (text == "给予X+1层虚弱" ? "Apply X+1 Weak" : null) ??
            (text == "召唤X" ? "Summon X" : null) ??
            (text == "召唤X+1" ? "Summon X+1" : null) ??
            (text == "将X张灵魂加入抽牌堆" ? "Add X Souls to your draw pile" : null) ??
            (text == "将X+1张灵魂加入抽牌堆" ? "Add X+1 Souls to your draw pile" : null) ??
            Match(@"^造成(\d+)点伤害X次$", "Deal {0} damage X times") ??
            Match(@"^造成(\d+)点伤害X\+1次$", "Deal {0} damage X+1 times") ??
            Match(@"^给予所有敌人(\d+)层易伤$", "Apply {0} Vulnerable to ALL enemies") ??
            Match(@"^获得(\d+)点力量$", "Gain {0} Strength") ??
            Match(@"^本回合获得(\d+)点力量$", "Gain {0} Strength this turn") ??
            Match(@"^获得(\d+)层覆甲$", "Gain {0} Plating") ??
            Match(@"^对攻击者造成(\d+)点伤害$", "Deal {0} damage to the attacker") ??
            (text == "消耗所有手牌" ? "Exhaust all cards in your hand" : null) ??
            (text == "消耗手牌中的所有非攻击牌" ? "Exhaust all non-Attack cards in your hand" : null) ??
            (text == "消耗手牌中的一张牌" ? "Exhaust a card in your hand" : null) ??
            (text == "随机消耗手牌中的一张牌" ? "Exhaust a random card in your hand" : null) ??
            (text == "消耗选中的牌" ? "Exhaust the selected card" : null) ??
            (text == "消耗那张非攻击牌" ? "Exhaust that non-Attack card" : null) ??
            (text == "消耗那张技能牌" ? "Exhaust that Skill" : null) ??
            (text == "选择手牌中的一张牌" ? "Choose a card in your hand" : null) ??
            (text == "选择手牌中的一张攻击牌" ? "Choose an Attack in your hand" : null) ??
            (text == "将此牌的一张复制加入弃牌堆" ? "Add a copy of this card to your discard pile" : null) ??
            (text == "将那张攻击牌的一张复制加入手牌" ? "Add a copy of that Attack to your hand" : null) ??
            (text == "升级那张攻击牌" ? "Upgrade that Attack" : null) ??
            (text == "升级手牌中的一张牌" ? "Upgrade a card in your hand" : null) ??
            (text == "打出抽牌堆顶部的牌并将其消耗" ? "Play the top card of your draw pile. Exhaust it" : null) ??
            (text == "打出抽牌堆顶部的X张牌" ? "Play the top X cards of your draw pile" : null) ??
            (text == "打出抽牌堆顶部的X+1张牌" ? "Play the top X+1 cards of your draw pile" : null) ??
            (text == "将手牌中的所有攻击牌变化为巨石" ? "Transform all Attacks in your hand into Boulders" : null) ??
            (text == "将手牌中的所有攻击牌变化为巨石+" ? "Transform all Attacks in your hand into Boulder+" : null) ??
            (text == "将一张随机攻击牌加入手牌。其本回合费用为0" ? "Add a random Attack to your hand. It can be played for free this turn" : null) ??
            (text == "抽牌，直到抽到一张非攻击牌" ? "Draw cards until you draw a non-Attack card" : null) ??
            (text == "对一名随机敌人打出这张牌" ? "It is played against a random enemy" : null) ??
            Match(@"^将该攻击牌额外打出(\d+)次$", "Play that Attack {0} additional times") ??
            (text == "随机消耗手牌中的一张攻击牌" ? "Exhaust a random Attack in your Hand" : null) ??
            (text == "丢弃所有手牌" ? "Discard your hand" : null) ??
            (text == "丢弃所有手牌，然后抽相同数量的牌" ? "Discard your hand, then draw that many cards" : null) ??
            (text == "你的下一回合开始时格挡不会消失" ? "Block is not removed at the start of your next turn" : null) ??
            (text == "获得等同于所有敌人中毒层数总和的格挡" ? "Gain Block equal to the total Poison on ALL enemies" : null) ??
            (text == "你手牌中的所有牌在本回合免费打出" ? "Cards in your hand cost 0 this turn" : null) ??
            Match(@"^在本回合，你打出的下(\d+)张技能牌会被额外打出一次$", "The next {0} Skills you play this turn are played twice") ??
            Match(@"^抽(\d+)张牌。如果抽到的是技能牌，则获得(\d+)点格挡$", "Draw {0} card. If it is a Skill, gain {1} Block") ??
            Match(@"^抽(\d+)张牌。这些牌在本回合获得保留$", "Draw {0} cards. Retain them this turn") ??
            (text == "在本回合给手牌中的一张技能牌添加奇巧" ? "Give a Skill in your hand Sly this turn" : null) ??
            (text == "将消耗牌堆中的所有小刀对该敌人打出" ? "Play all Shivs in your Exhaust Pile against that enemy" : null) ??
            (text == "将消耗牌堆中的所有小刀+对该敌人打出" ? "Upgrade and play all Shivs in your Exhaust Pile against that enemy" : null) ??
            Match(@"^选择一张手牌。在下个回合将它的(\d+)张复制加入手牌$", "Choose a card in your hand. Next turn, add {0} copies of it to your hand") ??
            (text is "立即触发中毒" or "立即触发所有敌人的中毒"
                ? "Trigger Poison on ALL enemies immediately" : null) ??
            (text == "你的下一张技能牌耗能变为0" ? "Your next Skill costs 0" : null) ??
            (text == "在下个回合，你所有的攻击伤害翻倍" ? "Next turn, your Attacks deal double damage" : null) ??
            (text == "本回合你获得的格挡值翻倍" ? "Double the Block you gain this turn" : null) ??
            (text == "额外获得一次卡牌奖励" ? "Gain an additional card reward" : null) ??
            Match(@"^本场战斗中，这张牌的耗能减少(\d+)$", "This card costs {0} less this combat") ??
            (text == "给予所有敌人1层易伤" ? "Apply 1 Vulnerable to ALL enemies" : null) ??
            (text == "本回合不能再抽牌" ? "You cannot draw cards this turn" : null) ??
            (text == "则将其打出" ? "Play it" : null) ??
            Match(@"^从手牌随机自动打出(\d+)张攻击牌，目标随机敌人$", "Play {0} random Attacks from your hand against random enemies") ??
            (text == "费用变为0" ? "Set its Cost to 0" : null) ??
            Match(@"^永久获得(\d+)点最大生命$", "Permanently gain {0} Max HP") ??
            Match(@"^在本场战斗中，此卡的基础伤害增加(\d+)点$", "Permanently increase this card's base damage by {0} this combat") ??
            (text == "将它的伤害添加给这张牌" ? "Add its damage to this card" : null) ??
            (text is "你的格挡不会在回合开始时移除" or "格挡不再在你的回合开始时消失" ? "Block is not removed at the start of your turn" : null) ??
            (text is "你的技能牌费用变为0" or "技能牌的耗能变为0" ? "Your Skills cost 0" : null) ??
            Match(@"^拥有易伤的敌人受到的伤害增加(\d+)%$", "Vulnerable enemies take {0}% more damage") ??
            (text == "每回合第一次通过卡牌获得的格挡翻倍" ? "The first Block you gain from a card each turn is doubled" : null) ??
            Match(@"^中毒会额外触发(\d+)次$", "Poison triggers {0} additional times") ??
            Match(@"^小刀额外造成(\d+)点伤害$", "Shivs deal {0} additional damage") ??
            Match(@"^每有一次攻击造成未被格挡的伤害，就给予(\d+)层中毒$", "Whenever an Attack deals unblocked damage, apply {0} Poison") ??
            (text == "小刀会攻击所有敌人" ? "Shivs hit ALL enemies" : null) ??
            (text == "当你打出技能牌时，该牌获得奇巧" ? "Whenever you play a Skill, it gains Sly" : null) ??
            Match(@"^小刀获得保留。每回合打出的第一张小刀额外造成(\d+)点伤害$", "Shivs have Retain. The first Shiv played each turn deals {0} additional damage") ??
            (text == "小刀获得保留" ? "Shivs have Retain" : null) ??
            Match(@"^每回合打出的第一张小刀额外造成(\d+)点伤害$", "The first Shiv played each turn deals {0} additional damage") ??
            Match(@"^处于虚弱状态的敌人受到的攻击伤害增加(\d+)%$", "Weak enemies take {0}% more Attack damage") ??
            (text == "在你的回合结束时，不再丢弃你的手牌" ? "At the end of your turn, do not discard your hand" : null) ??
            (text == "每当你打出一张牌时" ? "Whenever you play a card" : null) ??
            (text == "每当你在回合进行中抽到一张牌时" ? "Whenever you draw a card during your turn" : null) ??
            (text is "每回合开始时" or "在你的回合开始时" ? "At the start of your turn" : null) ??
            (text is "每回合结束时" or "在你的回合结束时" ? "At the end of your turn" : null) ??
            (text == "每当你打出一张技能牌时" ? "Whenever you play a Skill" : null) ??
            (text == "每当你在回合内失去生命时" ? "Whenever you lose HP during your turn" : null) ??
            (text == "每当有一张牌被消耗时" ? "Whenever a card is Exhausted" : null) ??
            (text is "每当你在本回合打出第3张攻击牌时" or "每回合中，当你打出第3张攻击牌时" ? "Whenever you play your third Attack each turn" : null) ??
            (text == "每当你施加易伤时" ? "Whenever you apply Vulnerable" : null) ??
            (text == "每当你抽到名字中有“打击”的牌时" ? "Whenever you draw a card containing “Strike”" : null) ??
            (text == "每当你获得格挡时" ? "Whenever you gain Block" : null) ??
            (text == "斩杀时" ? "If this kills" : null) ??
            (text == "若消耗牌堆中至少有3张牌" ? "If you have at least 3 cards in your Exhaust pile" : null) ??
            (text == "若本回合曾消耗过牌" ? "If you Exhausted a card this turn" : null) ??
            (text == "若你本回合失去过生命" ? "If you lost HP this turn" : null) ??
            (text == "若该敌人拥有易伤" ? "If the enemy is Vulnerable" : null) ??
            (text == "若该敌人拥有中毒" ? "If the enemy has Poison" : null) ??
            (text == "如果抽到的是技能牌" ? "If the card drawn is a Skill" : null) ??
            (text == "本回合每当你抽到一张牌时" ? "Whenever you draw a card this turn" : null) ??
            (text == "只有当抽牌堆中没有牌时" ? "Can only be played if your draw pile is empty" : null) ??
            (text == "你在本回合中每打出过一张技能牌，其耗能减少1" ? "Costs 1 less for each Skill played this turn" : null) ??
            (text == "每丢弃一张牌" ? "For each card discarded" : null) ??
            (text == "本回合每当你打出一张攻击牌时" ? "Whenever you play an Attack this turn" : null) ??
            (text == "本回合每当你受到一次攻击时" ? "Whenever you are attacked this turn" : null) ??
            (text == "在本回合中，有易伤状态的敌人对你造成的伤害降低50%" ? "You receive 50% less damage from Vulnerable enemies this turn" : null) ??
            (text == "每消耗一张牌" ? "For each card Exhausted" : null) ??
            (text == "每消耗一张手牌中的非攻击牌时" ? "For each non-Attack card Exhausted from your hand" : null) ??
            (text == "在你的回合结束时，如果这张牌在你的消耗牌堆中" ? "At the end of your turn, if this is in your Exhaust Pile" : null) ??
            (text is "当此牌被消耗时" or "这张牌被消耗时" ? "When this card is Exhausted" : null) ??
            Match(@"^在这个回合，你打出的下(\d+)张攻击牌获得效果：$", "This turn, your next {0} Attacks gain:") ??
            (text == "你打出的下一张攻击牌获得效果：" ? "Your next Attack gains:" : null) ??
            (text == "在本回合中，当你打出下一张攻击牌时" ? "This turn, when you play your next Attack" : null) ??
            (text == "当你打出下一张攻击牌时" ? "When you play your next Attack" : null) ??
            (text == "你在本回合中每打出过一张攻击牌，其耗能减少1" ? "Costs 1 less Energy for each Attack played this turn" : null) ??
            (text == "将弃牌堆中的一张牌放到抽牌堆顶部" ? "Put a card from your discard pile on top of your draw pile" : null) ??
            (text == "将弃牌堆中的一张随机攻击牌放入手牌" ? "Put a random Attack from your discard pile into your hand" : null) ??
            (text == "将一张当前角色的随机牌加入手牌" ? "Add a random card for your current character to your hand" : null) ??
            (text == "将一张随机战士牌加入手牌" ? "Add a random card for your current character to your hand" : null) ??
            Match(@"^使该敌人在本回合失去(\d+)点力量$", "The enemy loses {0} Strength this turn") ??
            Match(@"^使该敌人获得(\d+)点力量$", "The enemy gains {0} Strength") ??
            (text == "将该敌人身上的易伤层数翻倍" ? "Double the enemy's Vulnerable" : null) ??
            (text == "获得等同于目标易伤层数的力量" ? "Gain Strength equal to the enemy's Vulnerable" : null) ??
            Match(@"^(?:目标)?敌人身上每有一层易伤，就获得(\d+)点力量$", "Gain {0} Strength for each Vulnerable on the target enemy") ??
            (text == "去除该敌人的所有格挡和人工制品" ? "Remove all Block and Artifact from the enemy" : null) ??
            (text == "每有一名敌人被击杀，就重复此效果" ? "Repeat this effect for each enemy killed" : null) ??
            (text == "本回合每打出过一张攻击牌，就造成一次伤害" ? "Deal damage once for each Attack played this turn" : null) ??
            (text == "手牌中每有一张技能牌，就造成一次伤害" ? "Deal damage once for each Skill in your hand" : null) ??
            Match(@"^本回合每丢弃过一张牌，这张牌额外造成(\d+)点伤害$", "Deal {0} additional damage for each card discarded this turn") ??
            Match(@"^本场战斗每抽过一张牌，这张牌额外造成(\d+)点伤害$", "Deal {0} additional damage for each card drawn this combat") ??
            Match(@"^手牌中每有一张其他牌，这张牌的伤害降低(\d+)点$", "Deal {0} less damage for each other card in your hand") ??
            Match(@"^本回合每当你受到一次攻击，对攻击者造成(\d+)点伤害$", "Whenever you are attacked this turn, deal {0} damage to the attacker") ??
            Match(@"^消耗牌堆每有一张牌，这张牌额外造成(\d+)点伤害$", "This card deals {0} additional damage for each card in your Exhaust pile") ??
            Match(@"^该敌人每有一层易伤，这张牌额外造成(\d+)点伤害$", "This card deals {0} additional damage for each Vulnerable on the enemy") ??
            Match(@"^你每有(\d+)点力量，这张牌额外获得(\d+)点格挡$", "This card gains {1} additional Block for each {0} Strength you have") ??
            Match(@"^本场战斗中每有一张名称含“打击”的牌，这张牌额外造成(\d+)点伤害$", "This card deals {0} additional damage for each Strike card in combat") ??
            (text == "这张牌造成等同于当前格挡的伤害" ? "This card deals damage equal to your current Block" : null) ??
            Match(@"^本场战斗中你每失去过一次生命，这张牌额外造成(\d+)次伤害$", "This card deals damage {0} additional times for each time you lost HP this combat") ??
            Match(@"^这张牌额外造成(\d+)次伤害$", "This card deals damage {0} additional times") ??
            throw new InvalidOperationException($"缺少英文卡面映射：{chinese}");

        if (Regex.IsMatch(english, @"damage to (ALL enemies|a random enemy)(\d|X)"))
            english = Regex.Replace(english, @"(ALL enemies|a random enemy)(\d+|X(?:\+1)?)$", "$1 $2 times");
        english = english
            .Replace("Draw 1 cards", "Draw 1 card", StringComparison.Ordinal)
            .Replace("draw 1 cards", "draw 1 card", StringComparison.Ordinal)
            .Replace("Discard 1 cards", "Discard 1 card", StringComparison.Ordinal)
            .Replace("Add 1 Shivs", "Add 1 Shiv", StringComparison.Ordinal)
            .Replace("Add 1 Ink Shivs", "Add 1 Ink Shiv", StringComparison.Ordinal)
            .Replace("The next 1 Skills", "The next Skill", StringComparison.Ordinal);
        english = ExternalOperationTextRegistry.NormalizeNumberAgreement(english);
        return english.EndsWith('.') ? english : english + ".";
    }
}

public static class ExternalOperationTextRegistry
{
    private static readonly Dictionary<string, string> TextByTemplate = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> TextByExactOperation = new(StringComparer.Ordinal);
    public static void Register(string template, string englishText) => TextByTemplate[template] = englishText;
    public static void Register(string template, string chineseText, string englishText) =>
        TextByExactOperation[$"{template}|{chineseText}"] = englishText;
    public static void RegisterNumericVariant(string template, string sourceChinese, string newChinese)
    {
        // Prefer the canonical literal renderer whenever it understands the resulting text. Mapping by numeric
        // position is only a fallback for externally registered operations: some modifier sentences place their
        // two values in a different English order ("gain B for each A"), which made generation order affect the
        // final English description and broke Ultimate Chaos peer determinism.
        try
        {
            Register(template, newChinese, EnglishCardDescriptionRenderer.TranslateLiteral(newChinese));
            return;
        }
        catch (InvalidOperationException)
        {
            // Dynamic derivatives/orbs may only have an externally registered source translation.
        }
        if (!TryGet(template, sourceChinese, out var english))
            english = EnglishCardDescriptionRenderer.TranslateLiteral(sourceChinese);
        var sourceValues = Regex.Matches(sourceChinese, @"\d+").Cast<Match>().Select(match => match.Value).ToArray();
        var newValues = Regex.Matches(newChinese, @"\d+").Cast<Match>().Select(match => match.Value).ToArray();
        if (sourceValues.Length == 0 && newValues.Length > 0 && sourceChinese.Contains("一张", StringComparison.Ordinal))
        {
            var amount = newValues[0];
            english = Regex.Replace(english, @"\b(a|one) card\b", match =>
                $"{amount} {(match.Value.EndsWith("Card", StringComparison.Ordinal) ? "Cards" : "cards")}",
                RegexOptions.IgnoreCase);
        }
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var canMapByValue = sourceValues.Length == newValues.Length;
        for (var index = 0; canMapByValue && index < sourceValues.Length; index++)
        {
            if (replacements.TryGetValue(sourceValues[index], out var existing) && existing != newValues[index])
                canMapByValue = false;
            else
                replacements[sourceValues[index]] = newValues[index];
        }
        var valueIndex = 0;
        var translated = Regex.Replace(english, @"\d+", match => canMapByValue && replacements.TryGetValue(match.Value, out var replacement)
            ? replacement
            : valueIndex < newValues.Length ? newValues[valueIndex++] : match.Value);
        Register(template, newChinese, translated);
    }
    public static bool TryGet(string template, string chineseText, out string text)
    {
        if (!TryGetExactOrNumericSchema(template, chineseText, out text!)
            && !TextByTemplate.TryGetValue(template, out text!))
            return false;
        // The offline recipes occasionally preserve an upgraded English value beside a base Chinese
        // value (and multiple source cards can register the same template/text pair).  ChineseText is
        // the executor-facing canonical operation, so keep every printed numeric slot aligned with it.
        text = AlignNumericSlots(chineseText, text);
        text = NormalizeNumberAgreement(text);
        return true;
    }
    public static bool TryGet(string template, out string text) => TextByTemplate.TryGetValue(template, out text!);

    private static bool TryGetExactOrNumericSchema(string template, string chineseText, out string text)
    {
        if (TextByExactOperation.TryGetValue($"{template}|{chineseText}", out text!)) return true;
        var schema = NumericSchema(chineseText);
        var prefix = template + "|";
        var source = TextByExactOperation.FirstOrDefault(entry =>
            entry.Key.StartsWith(prefix, StringComparison.Ordinal)
            && NumericSchema(entry.Key[prefix.Length..]) == schema);
        if (source.Key is null)
        {
            text = string.Empty;
            return false;
        }
        var values = Regex.Matches(chineseText, @"\d+").Cast<Match>().Select(match => match.Value).ToArray();
        var index = 0;
        text = Regex.Replace(source.Value, @"\d+", match => index < values.Length ? values[index++] : match.Value);
        text = text
            .Replace("Draw 1 cards", "Draw 1 card", StringComparison.Ordinal)
            .Replace("draw 1 cards", "draw 1 card", StringComparison.Ordinal)
            .Replace("Discard 1 cards", "Discard 1 card", StringComparison.Ordinal)
            .Replace("Add 1 Shivs", "Add 1 Shiv", StringComparison.Ordinal)
            .Replace("Add 1 Ink Shivs", "Add 1 Ink Shiv", StringComparison.Ordinal);
        if (chineseText.Contains("X+1", StringComparison.Ordinal)
            && !source.Key[prefix.Length..].Contains("X+1", StringComparison.Ordinal))
            text = text.Replace("X", "X+1", StringComparison.Ordinal);
        return true;
    }

    private static string NumericSchema(string text) => Regex.Replace(
        text.Replace("X+1", "X", StringComparison.Ordinal), @"\d+", "{n}");

    private static string AlignNumericSlots(string chineseText, string englishText)
    {
        var values = Regex.Matches(chineseText, @"(?<!X\+)\d+")
            .Cast<Match>().Select(match => match.Value).ToArray();
        var englishValues = Regex.Matches(englishText, @"(?<!X\+)\d+");
        if (values.Length != 1 || englishValues.Count != 1)
            return englishText;

        var valueIndex = 0;
        return Regex.Replace(englishText, @"(?<!X\+)\d+", _ => values[valueIndex++]);
    }

    internal static string NormalizeNumberAgreement(string text)
    {
        text = Regex.Replace(text,
            @"(?<![+\d])\b1 (?<middle>random |Lightning |Frost |Dark |Plasma |Glass )?(?<noun>cards|Attacks|Skills|Powers|Orbs|Stars|Souls|Wounds|Shivs|times)\b",
            match => "1 " + match.Groups["middle"].Value + (match.Groups["noun"].Value switch
            {
                "cards" => "card", "Attacks" => "Attack", "Skills" => "Skill", "Powers" => "Power",
                "Orbs" => "Orb", "Stars" => "Star", "Souls" => "Soul", "Wounds" => "Wound",
                "Shivs" => "Shiv", "times" => "time", _ => match.Groups["noun"].Value
            }));
        text = Regex.Replace(text,
            @"\b(?!1\b)(?<count>\d+|X(?:\+1)?) (?<middle>random |Lightning |Frost |Dark |Plasma |Glass )?(?<noun>card|Attack|Skill|Power|Orb|Star|Soul|Wound|Shiv|time)(?<upgrade>\+)?(?!\w| [Ss]lots)",
            match => match.Groups["count"].Value + " " + match.Groups["middle"].Value
                + match.Groups["noun"].Value + "s" + match.Groups["upgrade"].Value);
        text = text.Replace("Orbs slots", "Orb Slots", StringComparison.Ordinal)
            .Replace("Orbs Slots", "Orb Slots", StringComparison.Ordinal)
            .Replace("King's Sword", "Sovereign Blade", StringComparison.Ordinal);
        text = Regex.Replace(text, @"\bOrb slots\b", "Orb Slots");
        text = Regex.Replace(text, @"\b1 additional times\b", "1 additional time");
        text = Regex.Replace(text, @"\b(?!1\b)(\d+|X(?:\+1)?) additional time\b", "$1 additional times");
        text = Regex.Replace(text, @"\. ([a-z])", match => ". " + char.ToUpperInvariant(match.Groups[1].Value[0]));
        text = Regex.Replace(text, @"\ball enemies\b", "ALL enemies", RegexOptions.IgnoreCase);
        return text
            .Replace("1 Ink Shivs", "1 Ink Shiv", StringComparison.Ordinal)
            .Replace("The next Skill you play this turn are played twice", "The next Skill you play this turn is played twice", StringComparison.Ordinal);
    }
}
