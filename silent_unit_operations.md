# 混沌模式：猎手（Silent）单人未升级卡牌的 unit-operation 拆分

数据来自 v111 `SilentCardPool` 及各卡牌实现。排除多人专属的 `BladeSymphony`、`Concoct`、`Fade`、`Flanking`、`Sneaky`，共 86 张。数值均为未升级基础值；独特规则保留为可执行的独立组件，但触发条件与其后效果仍分别拆开。

| 卡牌（ID） | 费用 / 类型 / 选目标 | operations（按结算顺序） | keyword |
| --- | --- | --- | --- |
| 磨蚀 `Abrasive` | 3 / 能力 / 其他 | `N:Dex(1)`；`N:Thorns(4)` | `K:Sly` |
| 触媒 `Accelerant` | 1 / 能力 / 其他 | `A:rulePoisonExtraTriggers(1)` | — |
| 精准 `Accuracy` | 1 / 能力 / 其他 | `A:ruleShivBonusDamage(4)` | — |
| 杂技 `Acrobatics` | 1 / 技能 / 其他 | `N:Draw(3)`；`N:Discard(1)` | — |
| 肾上腺素 `Adrenaline` | 0 / 技能 / 其他 | `N:E(1)`；`N:Draw(2)` | `K:Exhaust` |
| 余像 `Afterimage` | 1 / 能力 / 其他 | `A:whenCardPlayed => N:B(1)` | — |
| 预判 `Anticipate` | 0 / 技能 / 其他 | `N:TempDex(2)` | — |
| 刺杀 `Assassinate` | 0 / 攻击 / 单敌 | `T:D(10)`；`T:Apply(V,1)` | `K:Innate`、`K:Exhaust` |
| 后空翻 `Backflip` | 1 / 技能 / 其他 | `N:B(5)`；`N:Draw(2)` | — |
| 背刺 `Backstab` | 0 / 攻击 / 单敌 | `T:D(11)` | `K:Innate`、`K:Exhaust` |
| 墨之刃 `BladeOfInk` | 1 / 技能 / 其他 | `N:CreateInkShiv(2)` | — |
| 刀刃之舞 `BladeDance` | 1 / 技能 / 其他 | `N:CreateShiv(3)` | `K:Exhaust` |
| 残影 `Blur` | 1 / 技能 / 其他 | `N:B(5)`；`N:KeepBlockNextTurn(1)` | — |
| 弹跳药瓶 `BouncingFlask` | 2 / 技能 / 其他 | `N:RandomPoison(3, hits=3)` | — |
| 咕嘟冒泡 `BubbleBubble` | 1 / 技能 / 单敌 | `C:ifTargetPoisoned => T:Poison(9)` | — |
| 子弹时间 `BulletTime` | 3 / 技能 / 其他 | `I:PreventDrawThisTurn`；`I:FreeHandThisTurn` | — |
| 爆发 `Burst` | 1 / 技能 / 其他 | `I:ReplayNextSkills(1)` | — |
| 计算下注 `CalculatedGamble` | 0 / 技能 / 其他 | `I:DiscardHandDrawSame` | `K:Exhaust` |
| 斗篷与匕首 `CloakAndDagger` | 1 / 技能 / 其他 | `N:B(6)`；`N:CreateShiv(1)` | — |
| 腐蚀波 `CorrosiveWave` | 1 / 技能 / 其他 | `C:untilTurnEndCardDrawn => N:AllPoison(2)` | — |
| 匕首雨 `DaggerSpray` | 1 / 攻击 / 其他 | `N:AllD(4, hits=2)` | — |
| 投掷匕首 `DaggerThrow` | 1 / 攻击 / 单敌 | `T:D(9)`；`N:Draw(1)`；`N:Discard(1)` | — |
| 冲刺 `Dash` | 2 / 攻击 / 单敌 | `N:B(10)`；`T:D(10)` | — |
| 致命毒药 `DeadlyPoison` | 1 / 技能 / 单敌 | `T:Poison(5)` | — |
| 防御 `DefendSilent` | 1 / 技能 / 其他 | `N:B(5)` | `Tag:Defend` |
| 偏折 `Deflect` | 0 / 技能 / 其他 | `N:B(4)` | — |
| 闪躲翻滚 `DodgeAndRoll` | 1 / 技能 / 其他 | `N:B(4)`；`N:NextTurnBlock(4)` | — |
| 回响斩击 `EchoingSlash` | 1 / 攻击 / 其他 | `N:AllD(10, hits=1)`；`M:RepeatAreaOnKill` | — |
| 涂毒 `Envenom` | 2 / 能力 / 其他 | `A:ruleUnblockedAttackPoison(1)` | — |
| 逃脱计划 `EscapePlan` | 0 / 技能 / 其他 | `N:Draw(1)`；`C:ifLastDrawnSkill => N:B(3)` | — |
| 独门技术 `Expertise` | 1 / 技能 / 其他 | `I:DrawWithRetain(2)` | — |
| 暴露 `Expose` | 0 / 技能 / 单敌 | `T:RemoveBlockAndArtifact`；`T:Apply(V,2)` | `K:Exhaust` |
| 刀扇 `FanOfKnives` | 2 / 能力 / 其他 | `A:ruleShivsHitAll`；`N:CreateShiv(4)` | — |
| 终结技 `Finisher` | 1 / 攻击 / 单敌 | `T:D(6)`；`M:RepeatPerAttackThisTurn` | — |
| 飞镖 `Flechettes` | 1 / 攻击 / 单敌 | `T:D(5)`；`M:RepeatPerSkillInHand` | — |
| 翻越撑击 `FlickFlack` | 1 / 攻击 / 其他 | `N:AllD(7, hits=1)` | `K:Sly` |
| 侧步 `Sidestep` | 0 / 技能 / 其他 | `N:NextTurnEnergy(1)` | — |
| 灵动步法 `Footwork` | 1 / 能力 / 其他 | `N:Dex(2)` | — |
| 华丽收场 `GrandFinale` | 0 / 攻击 / 其他 | `C:playableIfDrawPileEmpty => N:AllD(60, hits=1)` | — |
| 手上技法 `HandTrick` | 1 / 技能 / 其他 | `N:B(7)`；`I:GrantSlyToHandSkillThisTurn` |  |
| 迷雾 `Haze` | 2 / 技能 / 其他 | `N:AllPoison(4)`；`N:AllWeak(1)` | — |
| 隐秘匕首 `HiddenDaggers` | 0 / 技能 / 其他 | `N:Discard(2)`；`N:CreateShiv(2)` | — |
| 无尽刀刃 `InfiniteBlades` | 1 / 能力 / 其他 | `A:turnStart => N:CreateShiv(1)` | — |
| 刀刃陷阱 `KnifeTrap` | 2 / 技能 / 单敌 | `I:PlayExhaustedShivsAtTarget` | — |
| 先制打击 `LeadingStrike` | 1 / 攻击 / 单敌 | `T:D(3)`；`N:CreateShiv(2)` | — |
| 扫腿 `LegSweep` | 2 / 技能 / 单敌 | `T:Apply(W,2)`；`N:B(11)` | — |
| 萎靡 `Malaise` | X / 技能 / 单敌 | `T:XStrengthLoss`；`T:XWeak` | `K:Exhaust` |
| 谋划专家 `MasterPlanner` | 2 / 能力 / 其他 | `A:rulePlayedSkillsGainSly` |  |
| 铭记死亡 `MementoMori` | 1 / 攻击 / 单敌 | `T:D(9)`；`M:DamagePerDiscardThisTurn(4)` | — |
| 蜃景 `Mirage` | 1 / 技能 / 其他 | `N:BlockEqualAllPoison` | `K:Exhaust` |
| 谋杀 `Murder` | 3 / 攻击 / 单敌 | `T:D(1)`；`M:DamagePerCardDrawnCombat(1)` | — |
| 中和 `Neutralize` | 0 / 攻击 / 单敌 | `T:D(3)`；`T:Apply(W,1)` | — |
| 夜魇 `Nightmare` | 3 / 技能 / 其他 | `I:CopySelectedCardNextTurn(3)` | `K:Exhaust` |
| 毒雾 `NoxiousFumes` | 1 / 能力 / 其他 | `A:turnStart => N:AllPoison(2)` | — |
| 毒性爆发 `Outbreak` | 3 / 技能 / 其他 | `N:AllPoison(9)`；`I:TriggerPoisonNow` | — |
| 幻影之刃 `PhantomBlades` | 1 / 能力 / 其他 | `A:ruleShivsRetain`；`A:ruleFirstShivBonusDamage(9)` | `K:Retain` |
| 尖啸 `PiercingWail` | 1 / 技能 / 其他 | `N:AllTempStrengthLoss(6)` | `K:Exhaust` |
| 精密瞄准 `Pinpoint` | 3 / 攻击 / 单敌 | `T:D(15)`；`C:whileInCombatSkillCostReduction` | — |
| 带毒刺击 `PoisonedStab` | 1 / 攻击 / 单敌 | `T:D(6)`；`T:Poison(3)` | — |
| 猛扑 `Pounce` | 2 / 攻击 / 单敌 | `T:D(14)`；`I:NextSkillCostsZero` | — |
| 精确切击 `PreciseCut` | 0 / 攻击 / 单敌 | `T:D(13)`；`M:DamageMinusPerCardInHand(2)` | — |
| 猎杀者 `Predator` | 2 / 攻击 / 单敌 | `T:D(15)`；`N:NextTurnDraw(2)` | — |
| 早有准备 `Prepared` | 0 / 技能 / 其他 | `N:Draw(1)`；`N:Discard(1)` | — |
| 本能反应 `Reflex` | 3 / 技能 / 其他 | `N:Draw(2)` | `K:Sly` |
| 连续反弹 `Ricochet` | 2 / 攻击 / 其他 | `N:RandomD(3, hits=4)` | `K:Sly` |
| 群蛇形态 `SerpentForm` | 3 / 能力 / 其他 | `A:whenCardPlayed => N:RandomD(4, hits=1)` | — |
| 暗影步 `ShadowStep` | 1 / 技能 / 其他 | `N:DiscardAll`；`I:DoubleAttackDamageNextTurn` | — |
| 融入暗影 `Shadowmeld` | 1 / 技能 / 其他 | `I:DoubleBlockThisTurn` | — |
| 串刺 `Skewer` | X / 攻击 / 单敌 | `T:DX(8)` | — |
| 切割 `Slice` | 0 / 攻击 / 单敌 | `T:D(6)` | — |
| 蛇咬 `Snakebite` | 2 / 技能 / 单敌 | `T:Poison(7)` | `K:Retain` |
| 速行者 `Speedster` | 2 / 能力 / 其他 | `A:whenCardDrawnDuringTurn => N:AllD(2, hits=1)` | — |
| 钢铁风暴 `StormOfSteel` | 1 / 技能 / 其他 | `N:DiscardAll`；`C:forEachDiscarded => N:CreateShiv(1)` | — |
| 紧勒 `Strangle` | 1 / 攻击 / 单敌 | `T:D(8)`；`T:Strangle(2)` | — |
| 打击 `StrikeSilent` | 1 / 攻击 / 单敌 | `T:D(6)` | `Tag:Strike` |
| 突然一拳 `SuckerPunch` | 1 / 攻击 / 单敌 | `T:D(8)`；`T:Apply(W,1)` | — |
| 压制 `Suppress` | 0 / 攻击 / 单敌 | `T:D(11)`；`T:Apply(W,3)` | `K:Innate` |
| 生存者 `Survivor` | 1 / 技能 / 其他 | `N:B(8)`；`N:Discard(1)` | — |
| 战术大师 `Tactician` | 3 / 技能 / 其他 | `N:E(1)` | `K:Sly` |
| 狩猎 `TheHunt` | 1 / 攻击 / 单敌 | `T:D(10)`；`C:ifFatal => I:AddCardReward` | `K:Exhaust` |
| 必备工具 `ToolsOfTheTrade` | 1 / 能力 / 其他 | `A:turnStart => N:Draw(1) + N:Discard(1)` | — |
| 跟踪 `Tracking` | 2 / 能力 / 其他 | `A:ruleWeakEnemiesTakeMoreAttackDamage(50)` | — |
| 触不可及 `Untouchable` | 2 / 技能 / 其他 | `N:B(6)` | `K:Sly` |
| 袖里乾坤 `UpMySleeve` | 2 / 技能 / 其他 | `N:CreateShiv(3)`；`I:ReduceThisCardCostCombat(1)` | — |
| 计划妥当 `WellLaidPlans` | 2 / 能力 / 其他 | `A:ruleRetainHand` | — |
| 幽魂形态 `WraithForm` | 3 / 能力 / 其他 | `N:Intangible(2)`；`A:turnStart => N:LoseDex(1)` | — |

## 升级类别

猎手沿用共享的数值增加与减费升级，并允许：能力牌添加固有、非能力牌添加保留、移除消耗，以及将生成或打出的“小刀”升级为“小刀+”。其中 `Malaise` 是 X+1；`HiddenDaggers`、`KnifeTrap`、`StormOfSteel` 属于衍生物升级。原卡中特定而不可泛化的升级仍由上述通用类别表达。
