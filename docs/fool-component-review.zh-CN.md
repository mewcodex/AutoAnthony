# 愚者组件拆解审阅清单（v0.114.0）

85 张非多人角色牌采用逐张离线拆解，复用既有伤害、格挡、抽牌、能量、状态与回合触发器。
下表列出 65 个愚者契约；数值均为命名槽位，并非把每个数值作为一个组件。完整原卡组合见 [拆解表](../fool_unit_operations.md)。

## 组合规则

- 射击是赋予 Shoot 标签的伤害 modifier。随机牌只允许射击加一条对单伤害句，关键字不计入句数；原卡精确重建保留原卡的额外效果。
- 旋律是显式 Wah/Cho/Ki 标签，名称与标签共同保存；打出手牌中的旋律牌支持三种旋律的七个非空子集。
- 炫技拆成瞄准、限本回合的下一次非爪牙死亡触发、永久瞄准成长。永久奖励沿用消耗／能力要求，禁止重复触发祖先和本牌复制循环。
- 新版怒放选择手牌变化为全力射击；旧版全力射击的消耗计数公式仍用于现有存档。
- 悲伤变化在出牌结束后执行，按一次性使用加悲伤代价计价，不额外叠加消耗补偿。
- 产生喜悦／悲伤及变化产物使用共用衍生物／状态槽位；旋律掩码不属于可升级的数值。
- 需要伤害、格挡、瞄准或消耗结果的后续效果必须具有相应主效果；嵌套条件在事件真正触发时求值。

## 契约

| 标识 | 类别 | 中文模板 | 数值槽 | 原卡来源 |
|---|---|---|---|---|
| `F:Aim` | NonTargeted | 获得[[amount]]点瞄准。 | amount | 箭术、箭矢戳击、绷紧、啾 就绪、搭箭、炫技 |
| `F:Stretch` | NonTargeted | 获得[[amount]]点拉伸。 | amount | 弹跳、深度拉伸、潜伏根系 |
| `F:TemporaryThorns` | NonTargeted | 在本回合获得[[amount]]点荆棘。 | amount | 啾 尖刺 |
| `F:ShootArrow` | Modifier | 发射一支箭矢， | 无 | 绽放射击、神力射击、愚蠢射击、沉重射击、蹒跚射击、旋律射击、快速射击、散雨射击、重复射击、天空射击、花样射击、双重射击 |
| `F:StretchFromDamage` | NonTargeted | 获得等同于所造成伤害量的拉伸。 | 无 | 绽放射击 |
| `F:IfArrow` | ConditionalTrigger | 如果你拥有箭矢， | 无 | 啾 坚定 |
| `F:IfImpaled` | ConditionalTrigger | 如果目标敌人处于扎穿状态， | 无 | 持弓猛击、叽 跳跃、钉住 |
| `F:IfOwnImpaled` | ConditionalTrigger | 如果目标敌人被你的箭矢扎穿， | 无 | 连根拔起、拧下 |
| `F:IfShotSucceeded` | ConditionalTrigger | 如果这张牌发射了箭矢， | 无 | 绽放射击、蹒跚射击、散雨射击、花样射击 |
| `F:IfWahKi` | ConditionalTrigger | 如果你拥有哇旋律或叽旋律， | 无 |  |
| `F:IfWahCho` | ConditionalTrigger | 如果你拥有哇旋律或啾旋律， | 无 | 叽 休止符 |
| `F:IfChoKi` | ConditionalTrigger | 如果你拥有啾旋律或叽旋律， | 无 | 哇 爆破 |
| `F:IfKi` | ConditionalTrigger | 如果你拥有叽旋律， | 无 |  |
| `F:ForEachMelody` | ConditionalTrigger | 你每拥有一种旋律， | 无 | 和声之盾、平稳韵律 |
| `F:ForEachEnergyX` | ConditionalTrigger | 重复[[count]]次， | count | 绷紧 |
| `F:ForEachSadnessExhausted` | ConditionalTrigger | 每消耗一张悲伤， | 无 | 乐观 |
| `F:OnSadnessGenerated` | AbilityTrigger | 每当你生成一张悲伤时， | 无 | 万事无忧 |
| `F:OnMelodyGained` | AbilityTrigger | 每当你获得一种旋律时， | 无 | 失落鲁特琴、曲中意 |
| `F:OnWahGained` | AbilityTrigger | 每当你获得哇旋律时， | 无 | 暴力和弦 |
| `F:OnChoGained` | AbilityTrigger | 每当你获得啾旋律时， | 无 | 歌之道 |
| `F:OnKiGained` | AbilityTrigger | 每当你获得叽旋律时， | 无 | 调音 |
| `F:OnAllMelodies` | AbilityTrigger | 每当你集齐三种旋律时， | 无 | 摇篮曲 |
| `F:OnArrowImpaled` | AbilityTrigger | 每当你的箭矢扎穿一名敌人时， | 无 | 爆炸箭矢 |
| `F:OnFirstArrowDislodged` | AbilityTrigger | 每回合第一次让你的箭矢松脱时， | 无 | 呀！ |
| `F:WhenThisDislodgesArrow` | ConditionalTrigger | 如果这张牌让你的箭矢松脱， | 无 | 取箭神技 |
| `F:WhileDiscardArrowShot` | ConditionalTrigger | 每当你发射一支箭矢时，如果这张牌在你的弃牌堆中， | 无 | 呼哨箭矢 |
| `F:CreateSadness` | NonTargeted | 将[[amount]]张悲伤加入你的手牌。 | amount | 活跃气氛、倦怠、说笑、翻找、哀伤打击、刻薄 |
| `F:CreateJoy` | NonTargeted | 将[[amount]]张喜悦加入你的手牌。 | amount | 活跃气氛 |
| `F:TransformSadnessToJoy` | NonTargeted | 将你手牌中的随机[[amount]]张悲伤变化为喜悦。 | amount | 狂喜、喜悦之拳 |
| `F:ExhaustSadness` | NonTargeted | 消耗你手牌中的所有悲伤。 | 无 | 乐观 |
| `F:RefundStretch` | Modifier | 重新获得被这张牌的格挡效果消耗的拉伸。 | 无 | 啾 叽 |
| `F:AimMultiplier` | Modifier | 瞄准对这张牌有[[amount]]倍效果。 | amount | 沉重射击 |
| `F:DoubleAgainstImpaled` | Modifier | 对处于扎穿状态的敌人造成双倍伤害。 | 无 | 哇 战锤 |
| `F:ExtraHitsPerMelody` | Modifier | 你每拥有一种旋律，这张牌就额外造成[[hits]]次伤害。 | hits | 旋律射击、节奏打击 |
| `F:DamagePerSadnessCreated` | Modifier | 本场战斗中，你每生成过一张悲伤，这张牌就额外造成[[amount]]点伤害。 | amount | 苦闷 |
| `F:DamagePerMelodyCard` | Modifier | 你每有一张名字中含有“哇”、“啾”或“叽”的牌，这张牌就额外造成[[amount]]点伤害。 | amount | 哇 交响乐 |
| `F:CostPerMelody` | Independent | 你每拥有一种旋律，这张牌的耗能减少[[amount]][E]。 | amount | 终曲 |
| `F:CostIfWahKi` | Independent | 如果你拥有哇旋律或叽旋律，这张牌的耗能减少[[amount]][E]。 | amount | 啾 四肢 |
| `F:TransformThisToSadness` | Independent | 将这张牌变化为悲伤并返回你的手牌。 | 无 | 悲痛一击 |
| `F:LookChooseDiscard` | NonTargeted | 查看你抽牌堆顶的[[amount]]张牌，选择1张放入你的手牌，丢弃其余牌。 | amount | 叽 唱诵 |
| `F:RandomShootFree` | NonTargeted | 将[[amount]]张随机发射牌加入你的手牌。该牌在本回合内可以免费打出。 | amount | 遮天蔽日 |
| `F:DiscardShootToHand` | NonTargeted | 将你弃牌堆中的[[amount]]张发射牌放入你的手牌。 | amount |  |
| `F:DrawShootToHand` | NonTargeted | 将你抽牌堆中的[[amount]]张发射牌放入你的手牌。 | amount | 凝神 |
| `F:DiscardToDrawTop` | NonTargeted | 选择你弃牌堆中的[[amount]]张牌，放到你的抽牌堆顶。 | amount |  |
| `F:Dislodge` | SingleEnemyOnly | 使你扎穿目标敌人的所有箭矢松脱。 | 无 | 连根拔起、拧下 |
| `F:OnlyCardsThisTurn` | NonTargeted | 你在本回合只能再打出[[amount]]张牌。 | amount | 安可 |
| `F:ArrowSlots` | NonTargeted | 额外获得[[amount]]支箭矢。 | amount | 箭袋 |
| `F:MelodyReplay` | NonTargeted | 本回合你打出的下[[amount]]张“哇”、“啾”或“叽”牌会被额外打出一次。 | amount | 叽 诗句 |
| `F:Quietus` | NonTargeted | 本回合，你的下一次射击造成[[amount]]倍伤害，但箭矢会被摧毁。 | amount | 寂灭 |
| `F:Honing` | NonTargeted | 箭矢造成[[amount]]%额外伤害。 | amount | 磨砺 |
| `F:Serrated` | NonTargeted | 扎穿的敌人从你的攻击中受到的伤害增加[[amount]]%。 | amount | 锯齿箭矢 |
| `F:ShotVulnerable` | SingleEnemyOnly | 这名敌人在本回合从射击中额外受到[[amount]]%伤害。 | amount | 不喜欢你 |
| `F:PlayMelodyCards` | NonTargeted | 打出你手牌中的所有[[melodies]]牌。 | melody_mask（结构选择） | 啾 副歌 |
| `F:RandomTarot` | NonTargeted | 将[[amount]]张随机塔罗牌加入你的手牌。 | amount | 神话形态 |
| `F:PlayCheapest` | NonTargeted | 打出你手牌中耗能最低的[[amount]]张牌。 | amount | 傻瓜 |
| `F:NextCardFree` | NonTargeted | 你接下来打出的[[amount]]张牌耗能为0[E]。 | amount |  |
| `F:UpgradeRandomHand` | NonTargeted | 升级手牌中的[[amount]]张随机牌。 | amount | 调音 |
| `F:TransformHandToTheShot` | NonTargeted | 将手牌中的[[amount]]张牌变化为[[shot]]。 | amount | 怒放 |
| `F:TransformSadnessToCopy` | NonTargeted | 将手牌中的[[amount]]张随机悲伤变化为这张牌的复制品。 | amount | 生长 |
| `F:DislodgeAll` | SingleEnemyOnly | 使目标敌人身上所有属于你的箭矢松脱。 | 无 | 召回 |
| `F:SplashDamage` | NonTargeted | 对所有其他敌人造成等量于上次造成伤害的伤害。 | 无 | 散雨射击 |
| `F:ExhaustShoot` | NonTargeted | 消耗本场战斗中所有尚未消耗的发射牌。 | 无 |  |
| `F:CreateTheShot` | NonTargeted | 将一张[[shot]]加入手牌。其基础伤害为刚才消耗的牌数量的[[amount]]倍。 | amount |  |
| `F:UntilNonMinionDies` | ConditionalTrigger | 在本回合中，下次有非爪牙敌人死亡时， | 无 | 炫技 |
| `F:IncreaseAimRun` | NonTargeted | 这张牌的瞄准永久增加[[amount]]点。 | amount | 炫技 |

## 实现位置

组件契约与原卡组合：`tools/author_fool_catalog.py`、`Data/fool_components.json`、`fool_unit_operations.md`。
生成合法性：`FoolComponentRules.cs`；价格：`FoolComponentDefinitions.cs`、`EffectBalanceModel.cs`、`PercentageValueTuning.cs`。
战斗执行与事件：`ChaosFoolExecutor.cs`、`ChaosFoolPowerHooks.cs`、`ChaosFoolCardHooks.cs`。
名字与旋律标签：`ManualFoolNameParts.cs`、`FoolMelodyNames.cs`；不依据本地化牌名解析战斗行为。
