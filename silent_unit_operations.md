# Silent v111 reviewed unit-operation catalog

This is the reviewed offline authoring source. Each `⟦...⟧` stores: operation ID, scope, single-target requirement, card-reference slot, trigger-owner index, Chinese rendering text, and English rendering text. Numeric values are variable-slot samples, not separate component identities.

| Source card | Cost/Stars/Type/Target/Rarity | Unit operations | Keywords | English name |
|---|---|---|---|---|
| 磨蚀 `Abrasive` | 3/-/Power/Other/Rare | ⟦N:Dex¦NonTargeted¦false¦None¦-1¦获得1点敏捷。¦Gain 1 Dexterity.⟧；⟦N:Thorns¦NonTargeted¦false¦None¦-1¦获得4点荆棘。¦Gain 4 Thorns.⟧ | Sly |  |
| 触媒 `Accelerant` | 1/-/Power/Other/Uncommon | ⟦A:rulePoisonExtraTriggers¦AbilityRule¦false¦None¦-1¦中毒会额外触发1次。¦Poison triggers 1 additional time.⟧ |  |  |
| 精准 `Accuracy` | 1/-/Power/Other/Uncommon | ⟦A:ruleShivBonusDamage¦AbilityRule¦false¦None¦-1¦小刀额外造成4点伤害。¦Shivs deal 4 additional damage.⟧ |  |  |
| 杂技 `Acrobatics` | 1/-/Skill/Other/Uncommon | ⟦N:Draw¦NonTargeted¦false¦None¦-1¦抽3张牌。¦Draw 3 cards.⟧；⟦N:Discard¦NonTargeted¦false¦None¦-1¦丢弃1张牌。¦Discard 1 card.⟧ |  |  |
| 肾上腺素 `Adrenaline` | 0/-/Skill/Other/Rare | ⟦N:E¦NonTargeted¦false¦None¦-1¦获得1点能量。¦Gain 1 Energy.⟧；⟦N:Draw¦NonTargeted¦false¦None¦-1¦抽2张牌。¦Draw 2 cards.⟧ | Exhaust |  |
| 余像 `Afterimage` | 1/-/Power/Other/Rare | ⟦A:whenCardPlayed¦AbilityTrigger¦false¦None¦-1¦每当你打出一张牌时。¦Whenever you play a card.⟧；⟦N:B¦NonTargeted¦false¦None¦0¦获得1点格挡。¦Gain 1 Block.⟧ |  |  |
| 预判 `Anticipate` | 0/-/Skill/Other/Common | ⟦N:TempDex¦NonTargeted¦false¦None¦-1¦本回合获得2点敏捷。¦Gain 2 Dexterity this turn.⟧ |  |  |
| 刺杀 `Assassinate` | 0/-/Attack/SingleEnemy/Rare | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成10点伤害。¦Deal 10 damage.⟧；⟦T:Apply¦SingleEnemyOnly¦false¦None¦-1¦给予1层易伤。¦Apply 1 Vulnerable.⟧ | Exhaust,Innate |  |
| 后空翻 `Backflip` | 1/-/Skill/Other/Common | ⟦N:B¦NonTargeted¦false¦None¦-1¦获得5点格挡。¦Gain 5 Block.⟧；⟦N:Draw¦NonTargeted¦false¦None¦-1¦抽2张牌。¦Draw 2 cards.⟧ |  |  |
| 背刺 `Backstab` | 0/-/Attack/SingleEnemy/Uncommon | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成11点伤害。¦Deal 11 damage.⟧ | Exhaust,Innate |  |
| 墨之刃 `BladeOfInk` | 1/-/Skill/Other/Rare | ⟦N:CreateInkShiv¦NonTargeted¦false¦None¦-1¦将2张墨影小刀加入手牌。¦Add 2 Ink Shivs to your hand.⟧ |  |  |
| 刀刃之舞 `BladeDance` | 1/-/Skill/Other/Common | ⟦N:CreateShiv¦NonTargeted¦false¦None¦-1¦将3张小刀加入手牌。¦Add 3 Shivs to your hand.⟧ | Exhaust |  |
| 残影 `Blur` | 1/-/Skill/Other/Uncommon | ⟦N:B¦NonTargeted¦false¦None¦-1¦获得5点格挡。¦Gain 5 Block.⟧；⟦N:KeepBlockNextTurn¦NonTargeted¦false¦None¦-1¦你的下一回合开始时格挡不会消失。¦Block is not removed at the start of your next turn.⟧ |  |  |
| 弹跳药瓶 `BouncingFlask` | 2/-/Skill/Other/Uncommon | ⟦N:RandomPoison¦NonTargeted¦false¦None¦-1¦随机给予敌人3层中毒3次。¦Apply 3 Poison to random enemies 3 times.⟧ |  |  |
| 咕嘟冒泡 `BubbleBubble` | 1/-/Skill/SingleEnemy/Uncommon | ⟦C:ifTargetPoisoned¦ConditionalTrigger¦false¦None¦-1¦若该敌人拥有中毒。¦If the enemy has Poison.⟧；⟦T:Poison¦SingleEnemyOnly¦false¦None¦0¦给予9层中毒。¦Apply 9 Poison.⟧ |  |  |
| 子弹时间 `BulletTime` | 3/-/Skill/Other/Rare | ⟦I:PreventDrawThisTurn¦Independent¦false¦None¦-1¦本回合不能再抽牌。¦You cannot draw cards this turn.⟧；⟦I:FreeHandThisTurn¦Independent¦false¦None¦-1¦你手牌中的所有牌在本回合免费打出。¦Cards in your hand cost 0 this turn.⟧ |  |  |
| 爆发 `Burst` | 1/-/Skill/Other/Rare | ⟦I:ReplayNextSkills¦Independent¦false¦None¦-1¦在本回合，你打出的下1张技能牌会被额外打出一次。¦The next Skill you play this turn is played twice.⟧ |  |  |
| 计算下注 `CalculatedGamble` | 0/-/Skill/Other/Uncommon | ⟦I:DiscardHandDrawSame¦Independent¦false¦None¦-1¦丢弃所有手牌，然后抽相同数量的牌。¦Discard your hand, then draw that many cards.⟧ | Exhaust |  |
| 斗篷与匕首 `CloakAndDagger` | 1/-/Skill/Other/Common | ⟦N:B¦NonTargeted¦false¦None¦-1¦获得6点格挡。¦Gain 6 Block.⟧；⟦N:CreateShiv¦NonTargeted¦false¦None¦-1¦将1张小刀加入手牌。¦Add 1 Shiv to your hand.⟧ |  |  |
| 腐蚀波 `CorrosiveWave` | 1/-/Skill/Other/Rare | ⟦C:untilTurnEndCardDrawn¦ConditionalTrigger¦false¦None¦-1¦本回合每当你抽到一张牌时。¦After you play this card, whenever you draw a card this turn.⟧；⟦N:AllPoison¦NonTargeted¦false¦None¦0¦给予所有敌人2层中毒。¦Apply 2 Poison to ALL enemies.⟧ |  |  |
| 匕首雨 `DaggerSpray` | 1/-/Attack/Other/Common | ⟦N:AllD¦NonTargeted¦false¦None¦-1¦对所有敌人造成4点伤害2次。¦Deal 4 damage to ALL enemies 2 times.⟧ |  |  |
| 投掷匕首 `DaggerThrow` | 1/-/Attack/SingleEnemy/Common | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成9点伤害。¦Deal 9 damage.⟧；⟦N:Draw¦NonTargeted¦false¦None¦-1¦抽1张牌。¦Draw 1 card.⟧；⟦N:Discard¦NonTargeted¦false¦None¦-1¦丢弃1张牌。¦Discard 1 card.⟧ |  |  |
| 冲刺 `Dash` | 2/-/Attack/SingleEnemy/Uncommon | ⟦N:B¦NonTargeted¦false¦None¦-1¦获得10点格挡。¦Gain 10 Block.⟧；⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成10点伤害。¦Deal 10 damage.⟧ |  |  |
| 致命毒药 `DeadlyPoison` | 1/-/Skill/SingleEnemy/Common | ⟦T:Poison¦SingleEnemyOnly¦false¦None¦-1¦给予5层中毒。¦Apply 5 Poison.⟧ |  |  |
| 防御 `DefendSilent` | 1/-/Skill/Other/Basic | ⟦N:B¦NonTargeted¦false¦None¦-1¦获得5点格挡。¦Gain 5 Block.⟧ | Defend |  |
| 偏折 `Deflect` | 0/-/Skill/Other/Common | ⟦N:B¦NonTargeted¦false¦None¦-1¦获得4点格挡。¦Gain 4 Block.⟧ |  |  |
| 闪躲翻滚 `DodgeAndRoll` | 1/-/Skill/Other/Common | ⟦N:B¦NonTargeted¦false¦None¦-1¦获得4点格挡。¦Gain 4 Block.⟧；⟦N:NextTurnBlock¦NonTargeted¦false¦None¦-1¦在下个回合，获得4点格挡。¦Next turn, gain 4 Block.⟧ |  |  |
| 回响斩击 `EchoingSlash` | 1/-/Attack/Other/Uncommon | ⟦N:AllD¦NonTargeted¦false¦None¦-1¦对所有敌人造成10点伤害。¦Deal 10 damage to ALL enemies.⟧；⟦M:RepeatAreaOnKill¦Modifier¦false¦None¦-1¦每有一名敌人被击杀，就重复此效果。¦Repeat this effect for each enemy killed.⟧ |  |  |
| 涂毒 `Envenom` | 2/-/Power/Other/Rare | ⟦A:ruleUnblockedAttackPoison¦AbilityRule¦false¦None¦-1¦每有一次攻击造成未被格挡的伤害，就给予1层中毒。¦Whenever an Attack deals unblocked damage, apply 1 Poison.⟧ |  |  |
| 逃脱计划 `EscapePlan` | 0/-/Skill/Other/Uncommon | ⟦N:Draw¦NonTargeted¦false¦None¦-1¦抽1张牌。¦Draw 1 card.⟧；⟦C:ifLastDrawnSkill¦ConditionalTrigger¦false¦None¦-1¦如果抽到的是技能牌。¦If the card drawn is a Skill.⟧；⟦N:B¦NonTargeted¦false¦None¦1¦获得3点格挡。¦Gain 3 Block.⟧ |  |  |
| 独门技术 `Expertise` | 1/-/Skill/Other/Uncommon | ⟦I:DrawWithRetain¦Independent¦false¦None¦-1¦抽2张牌。这些牌在本回合获得保留。¦Draw 2 cards. Retain them this turn.⟧ |  |  |
| 暴露 `Expose` | 0/-/Skill/SingleEnemy/Uncommon | ⟦T:RemoveBlockAndArtifact¦SingleEnemyOnly¦false¦None¦-1¦去除该敌人的所有格挡和人工制品。¦Remove all Block and Artifact from the enemy.⟧；⟦T:Apply¦SingleEnemyOnly¦false¦None¦-1¦给予2层易伤。¦Apply 2 Vulnerable.⟧ | Exhaust |  |
| 刀扇 `FanOfKnives` | 2/-/Power/Other/Rare | ⟦A:ruleShivsHitAll¦AbilityRule¦false¦None¦-1¦小刀会攻击所有敌人。¦Shivs hit ALL enemies.⟧；⟦N:CreateShiv¦NonTargeted¦false¦None¦-1¦将4张小刀加入手牌。¦Add 4 Shivs to your hand.⟧ |  |  |
| 终结技 `Finisher` | 1/-/Attack/SingleEnemy/Uncommon | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成6点伤害。¦Deal 6 damage.⟧；⟦M:RepeatPerAttackThisTurn¦Modifier¦false¦None¦-1¦本回合每打出过一张攻击牌，就造成一次伤害。¦Deal damage once for each Attack played this turn.⟧ |  |  |
| 飞镖 `Flechettes` | 1/-/Attack/SingleEnemy/Uncommon | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成5点伤害。¦Deal 5 damage.⟧；⟦M:RepeatPerSkillInHand¦Modifier¦false¦None¦-1¦手牌中每有一张技能牌，就造成一次伤害。¦Deal damage once for each Skill in your hand.⟧ |  |  |
| 翻越撑击 `FlickFlack` | 1/-/Attack/Other/Common | ⟦N:AllD¦NonTargeted¦false¦None¦-1¦对所有敌人造成7点伤害。¦Deal 7 damage to ALL enemies.⟧ | Sly |  |
| 侧步 `Sidestep` | 0/-/Skill/Other/Uncommon | ⟦N:NextTurnEnergy¦NonTargeted¦false¦None¦-1¦在下个回合，获得1点能量。¦Next turn, gain 1 Energy.⟧ |  |  |
| 灵动步法 `Footwork` | 1/-/Power/Other/Uncommon | ⟦N:Dex¦NonTargeted¦false¦None¦-1¦获得2点敏捷。¦Gain 2 Dexterity.⟧ |  |  |
| 华丽收场 `GrandFinale` | 0/-/Attack/Other/Rare | ⟦C:playableIfDrawPileEmpty¦ConditionalTrigger¦false¦None¦-1¦只有当抽牌堆中没有牌时。¦Can only be played if your draw pile is empty.⟧；⟦N:AllD¦NonTargeted¦false¦None¦0¦对所有敌人造成60点伤害。¦Deal 60 damage to ALL enemies.⟧ |  |  |
| 手上技法 `HandTrick` | 1/-/Skill/Other/Uncommon | ⟦N:B¦NonTargeted¦false¦None¦-1¦获得7点格挡。¦Gain 7 Block.⟧；⟦I:GrantSlyToHandSkillThisTurn¦Independent¦false¦None¦-1¦在本回合给手牌中的一张技能牌添加奇巧。¦Give a Skill in your hand Sly this turn.⟧ |  |  |
| 迷雾 `Haze` | 2/-/Skill/Other/Uncommon | ⟦N:AllPoison¦NonTargeted¦false¦None¦-1¦给予所有敌人4层中毒。¦Apply 4 Poison to ALL enemies.⟧；⟦N:AllWeak¦NonTargeted¦false¦None¦-1¦给予所有敌人1层虚弱。¦Apply 1 Weak to ALL enemies.⟧ |  |  |
| 隐秘匕首 `HiddenDaggers` | 0/-/Skill/Other/Uncommon | ⟦N:Discard¦NonTargeted¦false¦None¦-1¦丢弃2张牌。¦Discard 2 cards.⟧；⟦N:CreateShiv¦NonTargeted¦false¦None¦-1¦将2张小刀加入手牌。¦Add 2 Shivs to your hand.⟧ |  |  |
| 无尽刀刃 `InfiniteBlades` | 1/-/Power/Other/Uncommon | ⟦A:turnStart¦AbilityTrigger¦false¦None¦-1¦在你的回合开始时。¦At the start of your turn.⟧；⟦N:CreateShiv¦NonTargeted¦false¦None¦0¦将1张小刀加入手牌。¦Add 1 Shiv to your hand.⟧ |  |  |
| 刀刃陷阱 `KnifeTrap` | 2/-/Skill/SingleEnemy/Rare | ⟦I:PlayExhaustedShivsAtTarget¦Independent¦true¦None¦-1¦将消耗牌堆中的所有小刀对该敌人打出。¦Play all Shivs in your Exhaust Pile against that enemy.⟧ |  |  |
| 先制打击 `LeadingStrike` | 1/-/Attack/SingleEnemy/Common | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成3点伤害。¦Deal 3 damage.⟧；⟦N:CreateShiv¦NonTargeted¦false¦None¦-1¦将2张小刀加入手牌。¦Add 2 Shivs to your hand.⟧ |  |  |
| 扫腿 `LegSweep` | 2/-/Skill/SingleEnemy/Uncommon | ⟦T:Apply¦SingleEnemyOnly¦false¦None¦-1¦给予2层虚弱。¦Apply 2 Weak.⟧；⟦N:B¦NonTargeted¦false¦None¦-1¦获得11点格挡。¦Gain 11 Block.⟧ |  |  |
| 萎靡 `Malaise` | X/-/Skill/SingleEnemy/Rare | ⟦T:XStrengthLoss¦SingleEnemyOnly¦false¦None¦-1¦敌人失去X点力量。¦The enemy loses X Strength.⟧；⟦T:XWeak¦SingleEnemyOnly¦false¦None¦-1¦给予X层虚弱。¦Apply X Weak.⟧ | Exhaust |  |
| 谋划专家 `MasterPlanner` | 2/-/Power/Other/Rare | ⟦A:rulePlayedSkillsGainSly¦AbilityRule¦false¦None¦-1¦当你打出技能牌时，该牌获得奇巧。¦Whenever you play a Skill, it gains Sly.⟧ |  |  |
| 铭记死亡 `MementoMori` | 1/-/Attack/SingleEnemy/Uncommon | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成9点伤害。¦Deal 9 damage.⟧；⟦M:DamagePerDiscardThisTurn¦Modifier¦false¦None¦-1¦本回合每丢弃过一张牌，这张牌额外造成4点伤害。¦Deal 4 additional damage for each card discarded this turn.⟧ |  |  |
| 蜃景 `Mirage` | 1/-/Skill/Other/Uncommon | ⟦N:BlockEqualAllPoison¦NonTargeted¦false¦None¦-1¦获得等同于所有敌人中毒层数总和的格挡。¦Gain Block equal to the total Poison on ALL enemies.⟧ | Exhaust |  |
| 谋杀 `Murder` | 3/-/Attack/SingleEnemy/Rare | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成1点伤害。¦Deal 1 damage.⟧；⟦M:DamagePerCardDrawnCombat¦Modifier¦false¦None¦-1¦本场战斗每抽过一张牌，这张牌额外造成1点伤害。¦Deal 1 additional damage for each card drawn this combat.⟧ |  |  |
| 中和 `Neutralize` | 0/-/Attack/SingleEnemy/Basic | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成3点伤害。¦Deal 3 damage.⟧；⟦T:Apply¦SingleEnemyOnly¦false¦None¦-1¦给予1层虚弱。¦Apply 1 Weak.⟧ |  |  |
| 夜魇 `Nightmare` | 3/-/Skill/Other/Rare | ⟦I:CopySelectedCardNextTurn¦Independent¦false¦None¦-1¦选择一张手牌。在下个回合将它的3张复制加入手牌。¦Choose a card in your hand. Next turn, add 3 copies of it to your hand.⟧ | Exhaust |  |
| 毒雾 `NoxiousFumes` | 1/-/Power/Other/Uncommon | ⟦A:turnStart¦AbilityTrigger¦false¦None¦-1¦在你的回合开始时。¦At the start of your turn.⟧；⟦N:AllPoison¦NonTargeted¦false¦None¦0¦给予所有敌人2层中毒。¦Apply 2 Poison to ALL enemies.⟧ |  |  |
| 毒性爆发 `Outbreak` | 3/-/Skill/Other/Rare | ⟦N:AllPoison¦NonTargeted¦false¦None¦-1¦给予所有敌人9层中毒。¦Apply 9 Poison to ALL enemies.⟧；⟦I:TriggerPoisonNow¦Independent¦false¦None¦-1¦立即触发所有敌人的中毒。¦Trigger Poison on ALL enemies immediately.⟧ |  |  |
| 幻影之刃 `PhantomBlades` | 1/-/Power/Other/Uncommon | ⟦A:ruleShivsRetain¦AbilityRule¦false¦None¦-1¦小刀获得保留。¦Shivs have Retain.⟧；⟦A:ruleFirstShivBonusDamage¦AbilityRule¦false¦None¦-1¦每回合打出的第一张小刀额外造成9点伤害。¦The first Shiv played each turn deals 9 additional damage.⟧ | Retain |  |
| 尖啸 `PiercingWail` | 1/-/Skill/Other/Common | ⟦N:AllTempStrengthLoss¦NonTargeted¦false¦None¦-1¦所有敌人在本回合失去6点力量。¦ALL enemies lose 6 Strength this turn.⟧ | Exhaust |  |
| 精密瞄准 `Pinpoint` | 3/-/Attack/SingleEnemy/Uncommon | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成15点伤害。¦Deal 15 damage.⟧；⟦C:whileInCombatSkillCostReduction¦ConditionalTrigger¦false¦None¦-1¦你在本回合中每打出过一张技能牌，其耗能减少1。¦Costs 1 less for each Skill played this turn.⟧ |  |  |
| 带毒刺击 `PoisonedStab` | 1/-/Attack/SingleEnemy/Common | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成6点伤害。¦Deal 6 damage.⟧；⟦T:Poison¦SingleEnemyOnly¦false¦None¦-1¦给予3层中毒。¦Apply 3 Poison.⟧ |  |  |
| 猛扑 `Pounce` | 2/-/Attack/SingleEnemy/Uncommon | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成14点伤害。¦Deal 14 damage.⟧；⟦I:NextSkillCostsZero¦Independent¦false¦None¦-1¦你的下一张技能牌耗能变为0。¦Your next Skill costs 0.⟧ |  |  |
| 精确切击 `PreciseCut` | 0/-/Attack/SingleEnemy/Uncommon | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成13点伤害。¦Deal 13 damage.⟧；⟦M:DamageMinusPerCardInHand¦Modifier¦false¦None¦-1¦手牌中每有一张其他牌，这张牌的伤害降低2点。¦Deal 2 less damage for each other card in your hand.⟧ |  |  |
| 猎杀者 `Predator` | 2/-/Attack/SingleEnemy/Common | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成15点伤害。¦Deal 15 damage.⟧；⟦N:NextTurnDraw¦NonTargeted¦false¦None¦-1¦在下个回合，抽2张牌。¦Next turn, draw 2 cards.⟧ |  |  |
| 早有准备 `Prepared` | 0/-/Skill/Other/Common | ⟦N:Draw¦NonTargeted¦false¦None¦-1¦抽1张牌。¦Draw 1 card.⟧；⟦N:Discard¦NonTargeted¦false¦None¦-1¦丢弃1张牌。¦Discard 1 card.⟧ |  |  |
| 本能反应 `Reflex` | 3/-/Skill/Other/Uncommon | ⟦N:Draw¦NonTargeted¦false¦None¦-1¦抽2张牌。¦Draw 2 cards.⟧ | Sly |  |
| 连续反弹 `Ricochet` | 2/-/Attack/Other/Common | ⟦N:RandomD¦NonTargeted¦false¦None¦-1¦随机对敌人造成3点伤害4次。¦Deal 3 damage to a random enemy 4 times.⟧ | Sly |  |
| 群蛇形态 `SerpentForm` | 3/-/Power/Other/Rare | ⟦A:whenCardPlayed¦AbilityTrigger¦false¦None¦-1¦每当你打出一张牌时。¦Whenever you play a card.⟧；⟦N:RandomD¦NonTargeted¦false¦None¦0¦随机对敌人造成4点伤害。¦Deal 4 damage to a random enemy.⟧ |  |  |
| 暗影步 `ShadowStep` | 1/-/Skill/Other/Rare | ⟦N:DiscardAll¦NonTargeted¦false¦None¦-1¦丢弃所有手牌。¦Discard your hand.⟧；⟦I:DoubleAttackDamageNextTurn¦Independent¦false¦None¦-1¦在下个回合，你所有的攻击伤害翻倍。¦Next turn, your Attacks deal double damage.⟧ |  |  |
| 融入暗影 `Shadowmeld` | 1/-/Skill/Other/Rare | ⟦I:DoubleBlockThisTurn¦Independent¦false¦None¦-1¦本回合你获得的格挡值翻倍。¦Double the Block you gain this turn.⟧ |  |  |
| 串刺 `Skewer` | X/-/Attack/SingleEnemy/Uncommon | ⟦T:DX¦SingleEnemyOnly¦false¦None¦-1¦造成8点伤害X次。¦Deal 8 damage X times.⟧ |  |  |
| 切割 `Slice` | 0/-/Attack/SingleEnemy/Common | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成6点伤害。¦Deal 6 damage.⟧ |  |  |
| 蛇咬 `Snakebite` | 2/-/Skill/SingleEnemy/Common | ⟦T:Poison¦SingleEnemyOnly¦false¦None¦-1¦给予7层中毒。¦Apply 7 Poison.⟧ | Retain |  |
| 速行者 `Speedster` | 2/-/Power/Other/Uncommon | ⟦A:whenCardDrawnDuringTurn¦AbilityTrigger¦false¦None¦-1¦每当你在回合进行中抽到一张牌时。¦Whenever you draw a card during your turn.⟧；⟦N:AllD¦NonTargeted¦false¦None¦0¦对所有敌人造成2点伤害。¦Deal 2 damage to ALL enemies.⟧ |  |  |
| 钢铁风暴 `StormOfSteel` | 1/-/Skill/Other/Rare | ⟦N:DiscardAll¦NonTargeted¦false¦None¦-1¦丢弃所有手牌。¦Discard your hand.⟧；⟦C:forEachDiscarded¦ConditionalTrigger¦false¦None¦-1¦每丢弃一张牌。¦For each card discarded.⟧；⟦N:CreateShiv¦NonTargeted¦false¦None¦1¦将1张小刀加入手牌。¦Add 1 Shiv to your hand.⟧ |  |  |
| 紧勒 `Strangle` | 1/-/Attack/SingleEnemy/Uncommon | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成8点伤害。¦Deal 8 damage.⟧；⟦T:Strangle¦SingleEnemyOnly¦false¦None¦-1¦本回合你每打出一张牌，该敌人失去2点生命。¦Whenever you play a card this turn, that enemy loses 2 HP.⟧ |  |  |
| 打击 `StrikeSilent` | 1/-/Attack/SingleEnemy/Basic | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成6点伤害。¦Deal 6 damage.⟧ | Strike |  |
| 突然一拳 `SuckerPunch` | 1/-/Attack/SingleEnemy/Common | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成8点伤害。¦Deal 8 damage.⟧；⟦T:Apply¦SingleEnemyOnly¦false¦None¦-1¦给予1层虚弱。¦Apply 1 Weak.⟧ |  |  |
| 压制 `Suppress` | 0/-/Attack/SingleEnemy/Ancient | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成11点伤害。¦Deal 11 damage.⟧；⟦T:Apply¦SingleEnemyOnly¦false¦None¦-1¦给予3层虚弱。¦Apply 3 Weak.⟧ | Innate |  |
| 生存者 `Survivor` | 1/-/Skill/Other/Basic | ⟦N:B¦NonTargeted¦false¦None¦-1¦获得8点格挡。¦Gain 8 Block.⟧；⟦N:Discard¦NonTargeted¦false¦None¦-1¦丢弃1张牌。¦Discard 1 card.⟧ |  |  |
| 战术大师 `Tactician` | 3/-/Skill/Other/Uncommon | ⟦N:E¦NonTargeted¦false¦None¦-1¦获得1点能量。¦Gain 1 Energy.⟧ | Sly |  |
| 狩猎 `TheHunt` | 1/-/Attack/SingleEnemy/Rare | ⟦T:D¦SingleEnemyOnly¦false¦None¦-1¦造成10点伤害。¦Deal 10 damage.⟧；⟦C:ifFatal¦ConditionalTrigger¦false¦None¦-1¦斩杀时。¦If this kills.⟧；⟦I:AddCardReward¦Independent¦false¦None¦1¦额外获得一次卡牌奖励。¦Gain an additional card reward.⟧ | Exhaust |  |
| 必备工具 `ToolsOfTheTrade` | 1/-/Power/Other/Rare | ⟦A:turnStart¦AbilityTrigger¦false¦None¦-1¦在你的回合开始时。¦At the start of your turn.⟧；⟦N:Draw¦NonTargeted¦false¦None¦0¦抽1张牌。¦Draw 1 card.⟧；⟦N:Discard¦NonTargeted¦false¦None¦0¦丢弃1张牌。¦Discard 1 card.⟧ |  |  |
| 跟踪 `Tracking` | 2/-/Power/Other/Rare | ⟦A:ruleWeakEnemiesTakeMoreAttackDamage¦AbilityRule¦false¦None¦-1¦处于虚弱状态的敌人受到的攻击伤害增加50%。¦Weak enemies take 50% more Attack damage.⟧ |  |  |
| 触不可及 `Untouchable` | 2/-/Skill/Other/Common | ⟦N:B¦NonTargeted¦false¦None¦-1¦获得6点格挡。¦Gain 6 Block.⟧ | Sly |  |
| 袖里乾坤 `UpMySleeve` | 2/-/Skill/Other/Uncommon | ⟦N:CreateShiv¦NonTargeted¦false¦None¦-1¦将3张小刀加入手牌。¦Add 3 Shivs to your hand.⟧；⟦I:ReduceThisCardCostCombat¦Independent¦false¦None¦-1¦本场战斗中，这张牌的耗能减少1。¦This card costs 1 less this combat.⟧ |  |  |
| 计划妥当 `WellLaidPlans` | 2/-/Power/Other/Rare | ⟦A:ruleRetainHand¦AbilityRule¦false¦None¦-1¦在你的回合结束时，不再丢弃你的手牌。¦At the end of your turn, do not discard your hand.⟧ |  |  |
| 幽魂形态 `WraithForm` | 3/-/Power/Other/Ancient | ⟦N:Intangible¦NonTargeted¦false¦None¦-1¦获得2层无实体。¦Gain 2 Intangible.⟧；⟦A:turnStart¦AbilityTrigger¦false¦None¦-1¦在你的回合开始时。¦At the start of your turn.⟧；⟦N:LoseDex¦NonTargeted¦false¦None¦1¦失去1点敏捷。¦Lose 1 Dexterity.⟧ |  |  |
