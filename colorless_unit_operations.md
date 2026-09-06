# Colorless v111 离线 unit-operation 拆解表

此文件覆盖 v111 单人游戏可进入商店无色池的卡牌。多人专属牌不纳入；`Fasten`（勒紧）依赖不存在于随机初始牌组中的固定“防御”牌，因此按设计排除。每个 `⟦...⟧` 依次记录 operation ID、作用域、是否需要单敌目标、卡牌槽位、触发器索引、中文文本和英文文本；数值是可连续随机化的变量槽样本。

| 原卡 | 费用/蓝星/类型/目标/稀有度 | unit operations | Keywords | 英文名 |
|---|---|---|---|---|
| 炼制药水 `Alchemize` | 1/-/Skill/Other/Rare | ⟦CL:ProxyAtomic_Alchemize¦Independent¦false¦None¦-1¦获得一瓶随机药水。¦Procure a random potion.⟧ | Exhaust | Alchemize |
| 天选 `Anointed` | 1/-/Skill/Other/Rare | ⟦CL:ProxyAtomic_Anointed¦Independent¦false¦None¦-1¦将抽牌堆中的所有稀有牌放入手牌。¦Put every Rare card from your Draw Pile into your Hand.⟧ | Exhaust | Anointed |
| 自动化 `Automation` | 1/-/Power/Other/Uncommon | ⟦CL:EveryCardsDrawn¦AbilityTrigger¦false¦None¦-1¦你每抽10张牌。¦Every 10 cards you draw.⟧；⟦N:E¦NonTargeted¦false¦None¦0¦获得1点能量。¦Gain 1 Energy.⟧ |  | Automation |
| 狠揍 `BeatDown` | 3/-/Skill/Other/Rare | ⟦CL:ProxyAtomic_BeatDown¦Independent¦false¦None¦-1¦打出弃牌堆中的3张随机攻击牌。¦Play 3 random Attacks from your Discard Pile.⟧ |  | Beat Down |
| 流星锤 `Bolas` | 0/-/Attack/SingleEnemy/Rare | ⟦T:D¦SingleEnemyOnly¦true¦None¦-1¦造成3点伤害。¦Deal 3 damage.⟧；⟦C:NextTurnStart¦ConditionalTrigger¦false¦None¦-1¦在你的下个回合开始时。¦At the start of your next turn.⟧；⟦CL:ReturnThisToHand¦Independent¦false¦ThisCard¦1¦将此牌返回手牌。¦Return this card to your Hand.⟧ |  | Bolas |
| 劫难 `Calamity` | 3/-/Power/Other/Rare | ⟦CL:WheneverAttackPlayed¦AbilityTrigger¦false¦None¦-1¦每当你打出一张攻击牌时。¦Whenever you play an Attack.⟧；⟦CL:AddRandomAttackToHand¦NonTargeted¦false¦None¦0¦将一张随机攻击牌加入手牌。¦Add a random Attack into your Hand.⟧ |  | Calamity |
| 横祸 `Catastrophe` | 2/-/Skill/Other/Uncommon | ⟦CL:ProxyAtomic_Catastrophe¦Independent¦false¦None¦-1¦从抽牌堆中随机打出2张牌。¦Play 2 random cards from your Draw Pile.⟧ |  | Catastrophe |
| 黑暗镣铐 `DarkShackles` | 0/-/Skill/SingleEnemy/Uncommon | ⟦T:TempStrengthLoss¦SingleEnemyOnly¦true¦None¦-1¦该敌人在本回合失去9点力量。¦That enemy loses 9 Strength this turn.⟧ | Exhaust | Dark Shackles |
| 发现 `Discovery` | 1/-/Skill/Other/Uncommon | ⟦CL:ProxyAtomic_Discovery¦Independent¦false¦None¦-1¦从3张随机牌中选择1张加入手牌。其本回合耗能为0。¦Choose 1 of 3 random cards to add into your Hand. It costs 0 this turn.⟧ | Exhaust | Discovery |
| 闪亮登场 `DramaticEntrance` | 0/-/Attack/Other/Uncommon | ⟦N:AllD¦NonTargeted¦false¦None¦-1¦对所有敌人造成11点伤害。¦Deal 11 damage to ALL enemies.⟧ | Exhaust,Innate | Dramatic Entrance |
| 熵 `Entropy` | 1/-/Power/Other/Rare | ⟦A:turnStart¦AbilityTrigger¦false¦None¦-1¦在你的回合开始时。¦At the start of your turn.⟧；⟦CL:TransformSelectedHandCards¦NonTargeted¦false¦None¦0¦变化手牌中的1张牌。¦Transform 1 card in your Hand.⟧ |  | Entropy |
| 均衡 `Equilibrium` | 2/-/Skill/Other/Uncommon | ⟦N:B¦NonTargeted¦false¦None¦-1¦获得13点格挡。¦Gain 13 Block.⟧；⟦N:RetainHandThisTurn¦NonTargeted¦false¦None¦-1¦在本回合保留你的手牌。¦Retain your Hand this turn.⟧ | Retain | Equilibrium |
| 永恒铠甲 `EternalArmor` | 3/-/Power/Other/Rare | ⟦N:Self¦NonTargeted¦false¦None¦-1¦获得9层覆甲。¦Gain 9 Plating.⟧ |  | Eternal Armor |
| 妙计 `Finesse` | 0/-/Skill/Other/Uncommon | ⟦N:B¦NonTargeted¦false¦None¦-1¦获得4点格挡。¦Gain 4 Block.⟧；⟦N:Draw¦NonTargeted¦false¦None¦-1¦抽1张牌。¦Draw 1 card.⟧ |  | Finesse |
| 拳斗 `Fisticuffs` | 1/-/Attack/SingleEnemy/Uncommon | ⟦T:D¦SingleEnemyOnly¦true¦None¦-1¦造成7点伤害。¦Deal 7 damage.⟧；⟦CL:GainBlockEqualDamage¦NonTargeted¦false¦None¦-1¦获得等量于所造成伤害的格挡。¦Gain Block equal to damage dealt.⟧ |  | Fisticuffs |
| 亮剑 `FlashOfSteel` | 0/-/Attack/SingleEnemy/Uncommon | ⟦T:D¦SingleEnemyOnly¦true¦None¦-1¦造成5点伤害。¦Deal 5 damage.⟧；⟦N:Draw¦NonTargeted¦false¦None¦-1¦抽1张牌。¦Draw 1 card.⟧ |  | Flash of Steel |
| 金斧 `GoldAxe` | 1/-/Attack/SingleEnemy/Rare | ⟦CL:DamageEqualCardsPlayedCombat¦SingleEnemyOnly¦true¦None¦-1¦造成本场战斗中所打出牌数的伤害。¦Deal damage equal to the number of cards played this combat.⟧ |  | Gold Axe |
| 贪婪之手 `HandOfGreed` | 2/-/Attack/SingleEnemy/Rare | ⟦T:D¦SingleEnemyOnly¦true¦None¦-1¦造成20点伤害。¦Deal 20 damage.⟧；⟦C:ifFatal¦ConditionalTrigger¦true¦None¦-1¦斩杀时。¦If this kills.⟧；⟦CL:GainGold¦NonTargeted¦false¦None¦1¦获得20金币。¦Gain 20 Gold.⟧ |  | Hand of Greed |
| 未掘宝石 `HiddenGem` | 1/-/Skill/Other/Rare | ⟦CL:ProxyAtomic_HiddenGem¦Independent¦false¦None¦-1¦你抽牌堆中的一张没有重放的随机牌获得2层重放。¦A random card without Replay in your Draw Pile gains 2 Replay.⟧ |  | Hidden Gem |
| 急躁 `Impatience` | 0/-/Skill/Other/Uncommon | ⟦CL:IfNoAttacksInHand¦ConditionalTrigger¦false¦None¦-1¦如果你的手牌中没有攻击牌。¦If you have no Attacks in your Hand.⟧；⟦N:Draw¦NonTargeted¦false¦None¦0¦抽2张牌。¦Draw 2 cards.⟧ |  | Impatience |
| 花样百出 `JackOfAllTrades` | 0/-/Skill/Other/Uncommon | ⟦N:AddRandomColorlessToHand¦NonTargeted¦false¦None¦-1¦将1张随机无色牌加入手牌。¦Add 1 random Colorless card into your Hand.⟧ | Exhaust | Jack of All Trades |
| 大奖 `Jackpot` | 3/-/Attack/SingleEnemy/Rare | ⟦T:D¦SingleEnemyOnly¦true¦None¦-1¦造成25点伤害。¦Deal 25 damage.⟧；⟦CL:AddRandomZeroCostCardsToHand¦NonTargeted¦false¦None¦-1¦将3张随机0费牌加入手牌。¦Add 3 random 0-cost cards into your Hand.⟧ |  | Jackpot |
| 战略大师 `MasterOfStrategy` | 0/-/Skill/Other/Rare | ⟦N:Draw¦NonTargeted¦false¦None¦-1¦抽3张牌。¦Draw 3 cards.⟧ | Exhaust | Master of Strategy |
| 乱战 `Mayhem` | 2/-/Power/Other/Rare | ⟦A:turnStart¦AbilityTrigger¦false¦None¦-1¦在你的回合开始时。¦At the start of your turn.⟧；⟦CL:PlayTopDrawCard¦NonTargeted¦false¦None¦0¦打出抽牌堆顶部的牌。¦Play the top card of your Draw Pile.⟧ |  | Mayhem |
| 心灵震慑 `MindBlast` | 1/-/Attack/SingleEnemy/Uncommon | ⟦CL:ForEachDrawPileCard¦Modifier¦false¦None¦-1¦抽牌堆中每有一张牌，¦For each card in your Draw Pile,⟧；⟦T:D¦SingleEnemyOnly¦true¦None¦-1¦造成1点伤害。¦Deal 1 damage.⟧ | Innate | Mind Blast |
| 怀旧 `Nostalgia` | 1/-/Power/Other/Rare | ⟦CL:FirstAttackOrSkillEachTurn¦AbilityTrigger¦false¦None¦-1¦每回合首次打出攻击或技能牌时。¦The first time you play an Attack or Skill each turn.⟧；⟦CL:PutEventCardOnDrawTop¦Independent¦false¦None¦0¦将那张牌置于抽牌堆顶端。¦Put that card on top of your Draw Pile.⟧ |  | Nostalgia |
| 万向斩 `Omnislice` | 0/-/Attack/SingleEnemy/Uncommon | ⟦T:D¦SingleEnemyOnly¦true¦None¦-1¦造成8点伤害。¦Deal 8 damage.⟧；⟦CL:DamageOtherEnemiesEqual¦NonTargeted¦false¦None¦-1¦对所有其他敌人造成等量伤害。¦Deal that much damage to ALL other enemies.⟧ |  | Omnislice |
| 神气制胜 `Panache` | 0/-/Power/Other/Uncommon | ⟦CL:EveryCardsPlayedThisTurn¦AbilityTrigger¦false¦None¦-1¦每当你在一回合内打出5张牌时。¦Every time you play 5 cards in a single turn.⟧；⟦N:AllD¦NonTargeted¦false¦None¦0¦对所有敌人造成10点伤害。¦Deal 10 damage to ALL enemies.⟧ |  | Panache |
| 应急按钮 `PanicButton` | 0/-/Skill/Other/Uncommon | ⟦N:B¦NonTargeted¦false¦None¦-1¦获得30点格挡。¦Gain 30 Block.⟧；⟦CL:NoBlockFromCards¦Independent¦false¦None¦-1¦你在接下来的2回合内无法再从卡牌中获得格挡。¦You cannot gain Block from cards for the next 2 turns.⟧ | Exhaust | Panic Button |
| 准备时间 `PrepTime` | 1/-/Power/Other/Uncommon | ⟦A:turnStart¦AbilityTrigger¦false¦None¦-1¦在你的回合开始时。¦At the start of your turn.⟧；⟦N:Vigor¦NonTargeted¦false¦None¦0¦获得3点活力。¦Gain 3 Vigor.⟧ |  | Prep Time |
| 生产制造 `Production` | 0/-/Skill/Other/Uncommon | ⟦N:E¦NonTargeted¦false¦None¦-1¦获得2点能量。¦Gain 2 Energy.⟧ | Exhaust | Production |
| 延伸 `Prolong` | 0/-/Skill/Other/Uncommon | ⟦CL:GainNextTurnBlockEqualCurrent¦Independent¦false¦None¦-1¦下个回合，获得等量于你当前格挡值的格挡。¦Next turn, gain Block equal to your current Block.⟧ | Exhaust | Prolong |
| 非凡技艺 `Prowess` | 1/-/Power/Other/Uncommon | ⟦N:Self¦NonTargeted¦false¦None¦-1¦获得1点力量。¦Gain 1 Strength.⟧；⟦N:Dex¦NonTargeted¦false¦None¦-1¦获得1点敏捷。¦Gain 1 Dexterity.⟧ |  | Prowess |
| 净化 `Purity` | 0/-/Skill/Other/Uncommon | ⟦CL:ExhaustUpToHandCards¦Independent¦false¦None¦-1¦从手牌中选择至多3张牌消耗。¦Exhaust up to 3 cards in your Hand.⟧ | Retain,Exhaust | Purity |
| 撕碎 `Rend` | 1/-/Attack/SingleEnemy/Rare | ⟦T:D¦SingleEnemyOnly¦true¦None¦-1¦造成10点伤害。¦Deal 10 damage.⟧；⟦CL:BonusPerUniqueDebuff¦Modifier¦true¦None¦-1¦该敌人身上每有一种负面效果，就额外造成5点伤害。¦Deals 5 additional damage for each unique debuff on the enemy.⟧ |  | Rend |
| 心神不宁 `Restlessness` | 0/-/Skill/Other/Uncommon | ⟦CL:IfHandEmpty¦ConditionalTrigger¦false¦None¦-1¦如果你的手牌为空。¦If your Hand is empty.⟧；⟦N:Draw¦NonTargeted¦false¦None¦0¦抽2张牌。¦Draw 2 cards.⟧；⟦N:E¦NonTargeted¦false¦None¦0¦获得2点能量。¦Gain 2 Energy.⟧ | Retain | Restlessness |
| 滚石 `RollingBoulder` | 3/-/Power/Other/Rare | ⟦A:turnStart¦AbilityTrigger¦false¦None¦-1¦在你的回合开始时。¦At the start of your turn.⟧；⟦N:AllD¦NonTargeted¦false¦None¦0¦对所有敌人造成5点伤害。¦Deal 5 damage to ALL enemies.⟧；⟦CL:IncreaseRollingDamage¦Modifier¦false¦None¦0¦然后将该伤害增加5点。¦Then increase this damage by 5.⟧ |  | Rolling Boulder |
| 箭雨 `Salvo` | 1/-/Attack/SingleEnemy/Uncommon | ⟦T:D¦SingleEnemyOnly¦true¦None¦-1¦造成12点伤害。¦Deal 12 damage.⟧；⟦N:RetainHandThisTurn¦NonTargeted¦false¦None¦-1¦在本回合保留你的手牌。¦Retain your Hand this turn.⟧ | Retain | Salvo |
| 潦草急就 `Scrawl` | 1/-/Skill/Other/Rare | ⟦CL:DrawToFullHand¦Independent¦false¦None¦-1¦抽牌直到抽满手牌。¦Draw cards until your Hand is full.⟧ | Exhaust | Scrawl |
| 秘密技法 `SecretTechnique` | 0/-/Skill/Other/Rare | ⟦CL:MoveSelectedSkillDrawToHand¦Independent¦false¦None¦-1¦从抽牌堆中选择一张技能牌放入手牌。¦Put a Skill from your Draw Pile into your Hand.⟧ | Exhaust | Secret Technique |
| 秘密武器 `SecretWeapon` | 0/-/Skill/Other/Rare | ⟦CL:MoveSelectedAttackDrawToHand¦Independent¦false¦None¦-1¦从抽牌堆中选择一张攻击牌放入手牌。¦Put an Attack from your Draw Pile into your Hand.⟧ | Exhaust | Secret Weapon |
| 探寻打击 `SeekerStrike` | 1/-/Attack/SingleEnemy/Uncommon | ⟦T:D¦SingleEnemyOnly¦true¦None¦-1¦造成9点伤害。¦Deal 9 damage.⟧；⟦CL:ChooseFromRandomDrawCards¦NonTargeted¦false¦None¦-1¦从抽牌堆的随机3张牌中选择一张加入手牌。¦Choose 1 of 3 cards in your Draw Pile to add into your Hand.⟧ | Strike | Seeker Strike |
| 震荡波 `Shockwave` | 2/-/Skill/Other/Uncommon | ⟦N:AllWeak¦NonTargeted¦false¦None¦-1¦给予所有敌人3层虚弱。¦Apply 3 Weak to ALL enemies.⟧；⟦N:AllVulnerable¦NonTargeted¦false¦None¦-1¦给予所有敌人3层易伤。¦Apply 3 Vulnerable to ALL enemies.⟧ | Exhaust | Shockwave |
| 飞溅 `Splash` | 1/-/Skill/Other/Rare | ⟦CL:ProxyAtomic_Splash¦Independent¦false¦None¦-1¦从3张其他角色的攻击牌中选择1张加入手牌。其本回合耗能为0。¦Choose 1 of 3 random Attacks from another character to add into your Hand. It costs 0 this turn.⟧ |  | Splash |
| 计策 `Stratagem` | 1/-/Power/Other/Uncommon | ⟦CL:WheneverDrawPileShuffled¦AbilityTrigger¦false¦None¦-1¦每当你的抽牌堆洗牌时。¦Whenever you shuffle your Draw Pile.⟧；⟦CL:ChooseDrawCardToHand¦Independent¦false¦None¦0¦选择一张牌放入手牌。¦Choose a card from it to put into your Hand.⟧ |  | Stratagem |
| 炸弹 `TheBomb` | 2/-/Skill/Other/Uncommon | ⟦CL:AfterTurns¦AbilityTrigger¦false¦None¦-1¦在3回合结束后。¦At the end of 3 turns.⟧；⟦N:AllD¦NonTargeted¦false¦None¦0¦对所有敌人造成40点伤害。¦Deal 40 damage to ALL enemies.⟧ |  | The Bomb |
| 孤注一掷 `TheGambit` | 0/-/Skill/Other/Rare | ⟦N:B¦NonTargeted¦false¦None¦-1¦获得50点格挡。¦Gain 50 Block.⟧；⟦CL:DieOnUnblockedAttack¦AbilityRule¦false¦None¦-1¦如果你在本场战斗中受到未被格挡的攻击伤害，则立刻死亡。¦If you take unblocked attack damage this combat, die.⟧ |  | The Gambit |
| 深谋远虑 `ThinkingAhead` | 0/-/Skill/Other/Uncommon | ⟦N:Draw¦NonTargeted¦false¦None¦-1¦抽2张牌。¦Draw 2 cards.⟧；⟦CL:PutSelectedHandCardOnDrawTop¦Independent¦false¦HandCard¦-1¦将手牌中的一张牌放到抽牌堆顶端。¦Put 1 card from your Hand on top of your Draw Pile.⟧ | Exhaust | Thinking Ahead |
| 无休手斧 `ThrummingHatchet` | 1/-/Attack/SingleEnemy/Uncommon | ⟦T:D¦SingleEnemyOnly¦true¦None¦-1¦造成11点伤害。¦Deal 11 damage.⟧；⟦C:NextTurnStart¦ConditionalTrigger¦false¦None¦-1¦在你的下个回合开始时。¦At the start of your next turn.⟧；⟦CL:ReturnThisToHand¦Independent¦false¦ThisCard¦1¦将此牌返回手牌。¦Return this card to your Hand.⟧ |  | Thrumming Hatchet |
| 究极防御 `UltimateDefend` | 1/-/Skill/Other/Uncommon | ⟦N:B¦NonTargeted¦false¦None¦-1¦获得11点格挡。¦Gain 11 Block.⟧ | Defend | Ultimate Defend |
| 究极打击 `UltimateStrike` | 1/-/Attack/SingleEnemy/Uncommon | ⟦T:D¦SingleEnemyOnly¦true¦None¦-1¦造成14点伤害。¦Deal 14 damage.⟧ | Strike | Ultimate Strike |
| 连射 `Volley` | X/-/Attack/Other/Uncommon | ⟦N:RandomD¦NonTargeted¦false¦None¦-1¦随机对敌人造成10点伤害X次。¦Deal 10 damage to a random enemy X times.⟧ |  | Volley |
