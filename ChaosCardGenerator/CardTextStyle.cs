using System.Text.RegularExpressions;

namespace ChaosCardGenerator;

/// <summary>
/// Normalizes generated Chinese operation text to the terminology and sentence shapes used by the
/// v111 Simplified-Chinese card localization. Executor-facing source text stays unchanged.
/// </summary>
public static class CardTextStyle
{
    public static string Chinese(GeneratorOperation operation) => Chinese(operation, operation.ChineseText);

    public static string Chinese(GeneratorOperation operation, string effectiveText)
    {
        var runtimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        var text = operation.Template switch
        {
            "C:playableIfDrawPileEmpty" => Regex.Replace(effectiveText,
                @"^只有当(?:你的)?抽牌堆中没有牌时。?$", "只有当你的抽牌堆中没有牌时才能打出。"),
            "A:ruleRetainHand" => effectiveText.Replace("在你的回合结束时，不再丢弃你的手牌",
                "在你的回合结束时，你不再丢弃你的手牌", StringComparison.Ordinal),
            "C:untilTurnEnd" when runtimeSpec.Trigger?.Kind == "next_attack_played"
                || operation.RuntimeSpec is null
                && effectiveText.Contains("当你打出下一张攻击牌时", StringComparison.Ordinal) => effectiveText
                .Replace("当你打出下一张攻击牌时", "在本回合中，当你打出下一张攻击牌时", StringComparison.Ordinal),
            "C:untilTurnEnd" when runtimeSpec.Trigger?.Kind == "attack_played"
                || operation.RuntimeSpec is null
                && effectiveText.Contains("打出一张攻击牌", StringComparison.Ordinal) => effectiveText
                .Replace("本回合每当你打出一张攻击牌时", "打出此牌后，你在这个回合内每打出一张攻击牌", StringComparison.Ordinal),
            "C:untilTurnEnd" when runtimeSpec.Trigger?.Kind == "attack_received"
                || operation.RuntimeSpec is null
                && effectiveText.Contains("受到一次攻击", StringComparison.Ordinal) => effectiveText
                .Replace("本回合每当你受到一次攻击时", "你在这个回合每受到一次攻击", StringComparison.Ordinal),
            "C:untilTurnEnd" when runtimeSpec.Trigger?.Kind == "card_played"
                || operation.RuntimeSpec is null
                && effectiveText.Contains("打出一张牌", StringComparison.Ordinal) => effectiveText
                .Replace("本回合每当你打出一张牌时", "打出此牌后，你在本回合内每打出一张牌", StringComparison.Ordinal),
            "C:untilTurnEndCardDrawn" => effectiveText.Replace("本回合每当你抽到一张牌时",
                "打出此牌后，你在本回合每抽到一张牌", StringComparison.Ordinal),
            "I:Create" => Regex.Replace(effectiveText,
                @"^将一张随机攻击牌加入手牌。其本回合费用为0。?$",
                "将一张随机攻击牌加入你的手牌。那张牌在本回合内可以免费打出。"),
            "I:DrawUntilNonAttack" => Regex.Replace(effectiveText,
                @"^抽牌，直到抽到一张非攻击牌。?$", "抽牌直到你抽到一张非攻击牌。"),
            "I:AutoPlayRandomAttackFromHand" => Regex.Replace(effectiveText,
                @"^从手牌随机自动打出(\d+)张攻击牌，目标随机敌人。?$",
                "随机打出你手牌中的$1张攻击牌攻击随机敌人。"),
            "I:GainMaxHp" => Regex.Replace(effectiveText,
                @"^永久获得(\d+)点最大生命。?$", "永久获得$1点最大生命值。"),
            "I:IncreaseDamageThisCombat" => Regex.Replace(effectiveText,
                @"^在本场战斗中，此卡的基础伤害增加(\d+)点。?$",
                "将这张牌在本场战斗中的伤害增加$1点。"),
            "I:SetCostZero" => Regex.Replace(effectiveText,
                @"^费用变为0。?$", "这张牌的耗能变为0。"),
            "I:PreventDrawThisTurn" => Regex.Replace(effectiveText,
                @"^本回合不能再抽牌。?$", "你在本回合内不能再抽牌。"),
            "I:CopySelectedCardNextTurn" => Regex.Replace(effectiveText,
                @"^选择一张手牌。在下个回合将它的(\d+)张复制加入手牌。?$",
                "选择一张牌。在下个回合将这张牌的$1张复制品加入你的手牌。"),
            "I:ReplayNextSkills" => effectiveText.Replace("在本回合，你打出的下", "在这个回合，你打出的下", StringComparison.Ordinal),
            "I:Upgrade" => effectiveText.Replace("升级手牌中的一张牌", "升级你手牌中的一张牌", StringComparison.Ordinal),
            "M:value" => effectiveText.Replace("这张牌造成等同于当前格挡的伤害", "造成你当前格挡值的伤害", StringComparison.Ordinal),
            "M:repeat" when effectiveText.Contains("失去过一次生命", StringComparison.Ordinal) => effectiveText
                .Replace("本场战斗中你每失去过一次生命", "在本场战斗中，你每失去过一次生命值", StringComparison.Ordinal)
                .Replace("这张牌额外造成", "这张牌就额外造成", StringComparison.Ordinal),
            "N:BlockEqualAllPoison" => effectiveText.Replace("获得等同于所有敌人中毒层数总和的格挡",
                "获得等量于所有敌人中毒层数总和的格挡", StringComparison.Ordinal),
            "N:CreateShiv" => effectiveText.Replace("加入手牌", "加入你的手牌", StringComparison.Ordinal),
            "N:CreateInkShiv" => effectiveText.Replace("加入手牌", "加入你的手牌", StringComparison.Ordinal),
            "N:NextTurnEnergy" => effectiveText.Replace("在下个回合，获得", "在下个回合获得", StringComparison.Ordinal),
            "N:NextTurnDraw" => effectiveText.Replace("在下个回合，抽", "在下个回合抽", StringComparison.Ordinal),
            "T:RemoveBlockAndArtifact" => effectiveText.Replace("去除该敌人的所有格挡和人工制品",
                "去除敌人身上的所有格挡值和人工制品", StringComparison.Ordinal),
            "D:AutoPlayRandomAttackFromDraw" => effectiveText.Replace("随机打出抽牌堆中的", "随机打出你的抽牌堆中的", StringComparison.Ordinal),
            "D:AddRandomPowerToHand" => effectiveText.Replace("加入手牌", "加入你的手牌", StringComparison.Ordinal),
            "D:CreateBurnInDiscard" or "D:CreateDazedInDiscard" or "D:CreateSlimeInDiscard"
                or "D:CreateTwoWoundsInDiscard" or "D:CreateVoidInDiscard" => effectiveText.Replace("加入弃牌堆",
                    "加入你的弃牌堆", StringComparison.Ordinal),
            "D:CreateZeroCostCopyInDiscard" => effectiveText.Replace("将此牌的一张0费复制品加入弃牌堆",
                "将这张牌的一张0{energyPrefix:energyIcons(1)}复制品添加到你的弃牌堆", StringComparison.Ordinal),
            "D:DrawAndDiscardNonZero" => effectiveText.Replace("丢弃其中耗能不为0的牌",
                "丢弃抽到的牌中耗能不为0的牌", StringComparison.Ordinal),
            "D:EvokeAllTwice" => effectiveText.Replace("激发所有充能球", "激发你的所有充能球", StringComparison.Ordinal),
            "D:EvokeRightmostOrb" => effectiveText.Replace("激发最右侧的充能球", "激发你最右侧的充能球", StringComparison.Ordinal),
            "D:IncreaseThisCardBlockRun" => Regex.Replace(effectiveText,
                @"^本局游戏中，此牌的基础格挡增加(\d+)点。?$",
                "这张牌在本局游戏中的格挡值永久增加$1点。"),
            "D:CostDownWhenStatusGenerated" => effectiveText.Replace("此牌在下一次打出前耗能减少",
                "此牌的耗能将在下一次打出前减少", StringComparison.Ordinal),
            "D:MoveDiscardCardToHand" => effectiveText.Replace("将弃牌堆中的一张牌放入手牌",
                "将弃牌堆中的一张牌放入你的手牌", StringComparison.Ordinal),
            // Synchronize's old detached payoff kept the duration only on its modifier prefix. Preserve the
            // temporary operation's meaning when it is assembled alone or loaded from an older snapshot.
            "D:GainTemporaryFocus" when !effectiveText.Contains("本回合", StringComparison.Ordinal) =>
                "本回合" + effectiveText,
            "D:ReturnZeroCostDiscardToHand" => effectiveText.Replace("将弃牌堆中所有0费牌放入手牌",
                "将你弃牌堆中的所有0费牌放入你的手牌", StringComparison.Ordinal),
            "D:ShuffleAllUnexhaustedIntoDraw" => effectiveText.Replace("将所有未消耗的牌洗回抽牌堆",
                "将你的所有未消耗的卡牌重新洗牌放入抽牌堆", StringComparison.Ordinal),
            "D:TriggerRightmostOrbPassive" => effectiveText.Replace("触发最右侧充能球的被动能力",
                "触发你最右侧的一个充能球的被动能力", StringComparison.Ordinal),
            "NCR:BlockTripleOstyMaxHp" => Regex.Replace(effectiveText,
                @"获得等同于奥斯提最大生命值(\d+)倍的格挡", "获得等量于奥斯提最大生命值$1倍的格挡"),
            "NCR:KillEnemiesAtDoomThreshold" => effectiveText.Replace("杀死灾厄不低于当前生命值的敌人",
                "杀死所有灾厄大于等于当前生命值的敌人", StringComparison.Ordinal),
            "NCR:AddRandomEtherealCardToHand" => Regex.Replace(effectiveText,
                @"^将(\d+)张随机牌加入手牌。它获得虚无。?$",
                "将$1张随机牌添加到你的手牌中。添加的牌会获得虚无。"),
            "NCR:CreateCopyInDiscard" => effectiveText.Replace("将此牌的一张复制加入弃牌堆",
                "在弃牌堆放入一张此牌的复制品", StringComparison.Ordinal),
            "NCR:MoveDiscardCardToHand" => effectiveText.Replace("将弃牌堆中的一张牌放入手牌",
                "将弃牌堆中的一张牌放入你的手牌", StringComparison.Ordinal),
            "NCR:CostDownWhenCreatureDies" => effectiveText.Replace("此牌耗能减少", "这张牌的耗能减少", StringComparison.Ordinal),
            "NCR:IncreaseThisCardDamageRun" => Regex.Replace(effectiveText,
                @"^本局游戏中，此牌的基础伤害增加(\d+)点。?$",
                "这张牌在本局游戏中的伤害永久性增加$1点。"),
            "NCR:ForEachCardDrawnThisTurn" => effectiveText.Replace("本回合每抽一张牌", "本回合每抽到一张牌", StringComparison.Ordinal),
            "NCR:ForEachExhaustedSoul" => effectiveText.Replace("消耗牌堆每有一张灵魂", "你的消耗牌堆中每有一张灵魂", StringComparison.Ordinal),
            "NCR:WheneverCreatureDies" => effectiveText.Replace("每当有生物死亡时", "每当有任何生物死亡时", StringComparison.Ordinal),
            "NCR:CreateSoulInDiscard" => effectiveText.Replace("加入弃牌堆", "加入你的弃牌堆", StringComparison.Ordinal),
            "NCR:CreateSoulInDraw" or "NCR:CreateSoulInDrawX" => effectiveText.Replace("加入抽牌堆", "加入你的抽牌堆", StringComparison.Ordinal),
            "NCR:CreateSoulInHand" or "NCR:AddSweepingGazeToHand" => effectiveText.Replace("加入手牌", "加入你的手牌", StringComparison.Ordinal),
            "NCR:DoomScaledDamage" => effectiveText.Replace("造成等同于目标灾厄层数的伤害",
                "造成等量于该敌人身上的灾厄层数的伤害", StringComparison.Ordinal),
            "NCR:ApplyDoomEqualDamage" => effectiveText.Replace("给予等同于所造成伤害的灾厄",
                "给予等量于所造成伤害的灾厄", StringComparison.Ordinal),
            "NCR:IfDoomAppliedThisTurn" => Regex.Replace(effectiveText,
                @"^(?:若|如果)本回合曾给予灾厄", "如果你在本回合中曾给予过灾厄"),
            "NCR:IfFirstPlayThisTurn" => effectiveText.Replace("如果这是本回合第一次打出此牌",
                "如果这是这张牌第一次在本回合被打出", StringComparison.Ordinal),
            "NCR:WheneverCardPlayedThisTurn" => effectiveText.Replace("本回合每当你打出一张牌时，",
                "打出此牌后，你在本回合内每打出一张牌，就", StringComparison.Ordinal),
            "NCR:WheneverOstyAttacksTargetThisTurn" => effectiveText
                .Replace("本回合每当奥斯提攻击该敌人时", "本回合中，奥斯提每次命中这名敌人时", StringComparison.Ordinal)
                .Replace("在本回合内，每当奥斯提攻击这名敌人时", "本回合中，奥斯提每次命中这名敌人时", StringComparison.Ordinal),
            "NCR:ApplyPower_SicEmPower" when !Regex.IsMatch(effectiveText, @"\d+") =>
                effectiveText.Replace("进行召唤", "召唤3", StringComparison.Ordinal)
                    .Replace("召唤。", "召唤3。", StringComparison.Ordinal),
            "NCR:ReturnFromDiscardOnHighCostPlay" => effectiveText.Replace("将此牌从弃牌堆放回手牌",
                "将此牌从弃牌堆放回你的手牌", StringComparison.Ordinal),
            "NCR:NextVoidCostsZero" => effectiveText.Replace("你打出的你打出的", "你打出的", StringComparison.Ordinal),
            "R:ForEachStarCostCard" => effectiveText.Replace("你的所有牌中每有一张有蓝星耗费的牌",
                "你每有一张拥有蓝星耗能的卡牌", StringComparison.Ordinal),
            "R:ForEachPriorAttackHitOnTarget" => effectiveText.Replace("本回合此前每对该敌人造成过一次攻击伤害",
                "本回合内，你此前每击中过该敌人一次", StringComparison.Ordinal),
            "R:ForEachGeneratedCardCombat" => effectiveText.Replace("本场战斗每生成过一张牌",
                "你在本场战斗中每生成过一张牌", StringComparison.Ordinal),
            "R:ForEachSkillPlayedThisTurn" => effectiveText.Replace("本回合每打出过一张技能牌",
                "本回合中每打出过一张技能牌", StringComparison.Ordinal),
            "R:ForEachStarGainedThisTurn" => effectiveText.Replace("本回合每获得过一颗蓝星",
                "你在本回合每获得1颗蓝星", StringComparison.Ordinal),
            "R:WheneverDrawn" => effectiveText.Replace("每当抽到此牌时", "每当你抽到这张牌时", StringComparison.Ordinal),
            "R:CostDownWhenDrawn" => Regex.Replace(effectiveText, @"^本场战斗此牌耗能减少(\d+)。?$",
                "这张牌的耗能减少$1。"),
            "R:DamageUpWhenDrawn" => Regex.Replace(effectiveText, @"^本场战斗此牌基础伤害增加(\d+)。?$",
                "在这场战斗中其伤害增加$1点。"),
            "R:KingsSwordDoubleDamageThisTurn" => effectiveText.Replace("本回合君王之剑对该敌人造成双倍伤害",
                "君王之剑在本回合对敌人造成双倍伤害", StringComparison.Ordinal),
            "R:MoveDiscardCardToDrawTop" => effectiveText.Replace("将弃牌堆中的一张牌放到抽牌堆顶",
                "将你弃牌堆中的一张牌放到抽牌堆顶部", StringComparison.Ordinal),
            "R:PlayAtTurnEndIfTopOfDraw" or "R:PlayThisCard" => effectiveText.Replace("打出此牌", "则将其打出", StringComparison.Ordinal),
            "R:NextTurn" => effectiveText.Replace("下个回合开始时", "在你的下个回合开始时", StringComparison.Ordinal),
            "R:AtTurnStartIfInExhaust" => effectiveText.Replace("若此牌", "如果这张牌", StringComparison.Ordinal)
                .Replace("如果此牌", "如果这张牌", StringComparison.Ordinal),
            "R:AddDebrisToHand" => effectiveText.Replace("残骸", "碎屑", StringComparison.Ordinal)
                .Replace("加入手牌", "加入你的手牌", StringComparison.Ordinal),
            "R:AddRandomColorlessToHand" => effectiveText.Replace("加入手牌", "加入你的手牌", StringComparison.Ordinal),
            "R:CopySelectedColorlessCard" => effectiveText.Replace("选择手牌中的一张无色牌，将它的一张复制加入手牌",
                "选择你手牌中的一张无色牌。将这张牌的一张复制品放入你的手牌", StringComparison.Ordinal),
            "R:FillHandWithDebris" => effectiveText.Replace("残骸", "碎屑", StringComparison.Ordinal)
                .Replace("加入手牌", "加入你的手牌", StringComparison.Ordinal),
            "R:PutKingsSwordInHand" => effectiveText.Replace("放入手牌", "放入你的手牌", StringComparison.Ordinal),
            "R:PutSelectedHandCardOnDraw" => effectiveText.Replace("将手牌中的一张牌放到抽牌堆顶",
                "将手牌中的一张牌放到你的抽牌堆顶部", StringComparison.Ordinal),
            "R:PutSelectedHandCardsOnDraw" => effectiveText.Replace("将手牌中的牌放到抽牌堆顶",
                "将手牌中的一张牌放到你的抽牌堆顶部", StringComparison.Ordinal),
            "R:ReflectBlockedDamageThisTurn" => effectiveText.Replace("本回合将被格挡的攻击伤害反弹给攻击者",
                "在本回合将你格挡掉的攻击伤害反弹给攻击者", StringComparison.Ordinal),
            "R:RetainHandThisTurn" => effectiveText.Replace("本回合保留你的手牌", "在本回合保留你的手牌", StringComparison.Ordinal),
            "R:ReturnThisToHand" => effectiveText.Replace("将此牌放回手牌", "将这张牌放回你的手牌", StringComparison.Ordinal),
            "R:PutThisOnDraw" => effectiveText.Replace("将此牌放到抽牌堆顶", "将这张牌放置于你的抽牌堆顶部", StringComparison.Ordinal),
            "R:AtTurnEndWhenTopOfDraw" => effectiveText.Replace("如果此牌位于抽牌堆顶",
                "如果这张牌位于你的抽牌堆顶部", StringComparison.Ordinal),
            "R:ReturnAfterSkillsPlayed" => Regex.Replace(effectiveText,
                @"^每打出(\d+)张技能牌，将此牌从弃牌堆放入手牌。?$",
                "你每在一回合内打出$1张技能牌，就将这张牌放入你的手牌。"),
            _ => effectiveText
        };

        if (operation.Template is "N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK")
            text = text.Replace("选择手牌中的", "选择你手牌中的", StringComparison.Ordinal);

        // Older snapshots used a literal Chinese translation; v111 uses the official localized name for Debris.
        if (operation.DerivativeId == "debris")
            text = text.Replace("残骸", "碎屑", StringComparison.Ordinal);

        if (operation.Template is "N:Create" or "N:CreateCurrentCharacterCardInHand")
        {
            text = text
                .Replace("将此牌的一张复制加入弃牌堆", "将一张此牌的复制品加入你的弃牌堆", StringComparison.Ordinal)
                .Replace("将那张攻击牌的一张复制加入手牌", "将该攻击牌的一张复制品加入你的手牌", StringComparison.Ordinal)
                .Replace("将一张当前角色的升级过的随机牌加入手牌", "将一张当前角色的升级过的随机牌加入你的手牌", StringComparison.Ordinal)
                .Replace("将一张当前角色的随机牌加入手牌", "将一张当前角色的随机牌加入你的手牌", StringComparison.Ordinal)
                // Legacy snapshots can still carry the pre-v0.1.71 wording.
                .Replace("将一张随机战士牌加入手牌", "将一张当前角色的随机牌加入你的手牌", StringComparison.Ordinal);
        }
        if (operation.Template is "M:base" or "M:DamagePerExhaustCard")
        {
            text = Regex.Replace(text,
                @"^本场战斗中每有一张名称含“打击”的牌，这张牌额外造成(\d+)点伤害。?$",
                "你每有一张名字中含有“打击”的牌，伤害+$1。");
            text = Regex.Replace(text,
                @"^消耗牌堆每有一张牌，这张牌额外造成(\d+)点伤害。?$",
                "你的消耗牌堆中每有一张牌，伤害增加$1点。");
            text = Regex.Replace(text,
                @"^该敌人每有一层易伤，这张牌额外造成(\d+)点伤害。?$",
                "该敌人每有一层易伤，伤害增加$1点。");
        }

        if (operation.Template == "N:Move")
        {
            text = text
                .Replace("将弃牌堆中的一张牌放到抽牌堆顶部",
                    "将你弃牌堆中的一张牌放到抽牌堆顶部", StringComparison.Ordinal)
                .Replace("将弃牌堆中的一张随机攻击牌放入手牌",
                    "将你弃牌堆的一张随机攻击牌放入你的手牌", StringComparison.Ordinal);
        }

        return NormalizeCommon(text);
    }

    /// <summary>For legacy callers that do not have an operation template.</summary>
    public static string Chinese(string text) => NormalizeCommon(text);

    public static void ValidateRenderedChinese(string text)
    {
        var violations = new[]
        {
            "若", "你打出的你打出的", "其本回合费用", "随机自动打出", "目标随机敌人",
            "只有当你的抽牌堆中没有牌时。", "在本场战斗中，此卡的基础伤害",
            "抽牌，直到抽到", "永久获得3点最大生命。"
        };
        var violation = violations.FirstOrDefault(text.Contains);
        if (violation is not null)
            throw new InvalidOperationException($"生成卡描述含有不符合v111卡面用语的文本“{violation}”：{text}");
        if (text.Contains("每当你在本回合", StringComparison.Ordinal))
            throw new InvalidOperationException($"每回合触发条件使用了逻辑不通的“每当你在本回合”：{text}");
        if (Regex.IsMatch(text, @"(?<!目标)敌人身上每有一层易伤"))
            throw new InvalidOperationException($"按易伤层数结算的效果没有明确写出目标敌人：{text}");
        if (Regex.IsMatch(text, @"(?m)(?:^|[。！？])费用(?:变为|减少|增加)"))
            throw new InvalidOperationException($"卡牌耗能效果误用了“费用”：{text}");
        if (Regex.IsMatch(text, @"(?m)^每回合(?:开始|结束)时"))
            throw new InvalidOperationException($"回合时点缺少玩家归属：{text}");
    }

    private static string NormalizeCommon(string text)
    {
        var styled = text
            .Replace("你的格挡不会在回合开始时移除", "格挡不再在你的回合开始时消失", StringComparison.Ordinal)
            .Replace("获得等同于目标易伤层数的力量", "目标敌人身上每有一层易伤，就获得1点力量", StringComparison.Ordinal)
            .Replace("每当你在本回合打出第", "每回合中，当你打出第", StringComparison.Ordinal)
            .Replace("当此牌被消耗时", "这张牌被消耗时", StringComparison.Ordinal)
            .Replace("你的技能牌费用变为0", "技能牌的耗能变为0", StringComparison.Ordinal)
            .Replace("给一张手牌添加虚无", "为一张手牌添加虚无", StringComparison.Ordinal)
            .Replace("下一张虚无牌耗能变为0", "你打出的下一张虚无牌耗能变为0", StringComparison.Ordinal)
            .Replace("下一张能力牌耗能变为0", "你打出的下一张能力牌耗能变为0", StringComparison.Ordinal)
            .Replace("若", "如果", StringComparison.Ordinal)
            .Replace("你打出的你打出的", "你打出的", StringComparison.Ordinal);
        styled = Regex.Replace(styled, @"(?<!目标)敌人身上每有一层易伤，就获得",
            "目标敌人身上每有一层易伤，就获得");
        const string turnStart = "每回合开始时";
        const string turnEnd = "每回合结束时";
        if (styled.StartsWith(turnStart, StringComparison.Ordinal))
            styled = "在你的回合开始时，" + styled[turnStart.Length..].TrimStart('，');
        if (styled.StartsWith(turnEnd, StringComparison.Ordinal))
            styled = "在你的回合结束时，" + styled[turnEnd.Length..].TrimStart('，');
        return styled;
    }
}
