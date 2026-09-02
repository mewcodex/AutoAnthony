# 混沌模式：战士（Ironclad）基础卡的 unit-operation 拆分

数据来自 `proj/111/MegaCrit.Sts2.Core.Models.CardPools/IroncladCardPool.cs` 及每张卡的 `OnPlay` / 事件钩子实现；数值为**未升级**基准值。排除了明确为 `MultiplayerOnly` 的：`Blaze`、`DemonicShield`、`Midnight`、`Outrage`、`Tank`。本表共 85 张单人卡。

## 表示法

每张卡以 `费用 | 类型 | 选目标 | operations | keywords` 表示。

- 类型：`攻击`、`技能`、`能力`。
- 选目标：攻击和技能只有 `单敌`（玩家实际选择一个敌人）或 `其他`（自己、全体敌人、随机敌人）。能力固定为 `其他`。
- `T:` 是只能装到“单敌”牌的取目标 operation；`N:` 是不选目标的常规 operation；`M:` 是依赖同牌其它 effect 的 modifier；`C:` 是条件/延迟/重复触发器；`A:` 是能力牌的持续触发条件；`K:` 是 keyword；`I:` 是目前保留为不可拆分的独立 operation。
- `D(x)` 是受力量、易伤等正常伤害管线修正的单次伤害；`B(x)` 是从这张牌获得 x 格挡；`V(x)`、`W(x)` 是易伤/虚弱层数；`S(x)` 是力量；`HP-(x)` 是失去生命（不是伤害）；`E(x)` 是能量；`Draw(x)` 是抽牌。
- `=>` 表示 modifier/trigger 所依附的 operation。`card` 表示这张卡；`exhausted(card)` 表示这张卡由于任意原因进入消耗堆；`Attack*`、`Skill*` 是卡类别过滤器。
- `I:` 并不是忽略逻辑：其括号内是生成器必须整体实现的原始逻辑，作为后续继续细分前的原子槽位。这样每一行可完整复现原卡。

## 可采样的 operation 词表

### 普通取目标 / 非取目标 effect

| ID | operation（参数槽） | 目标限制 |
| --- | --- | --- |
| `T_DAMAGE` | `T:D(x)` 对所选单敌伤害 | 单敌 |
| `T_POWER` | `T:Apply(状态, x)`，如 `V`、`W`、敌方 `S`、临时力量下降 | 单敌 |
| `N_ALL_DAMAGE` | `N:AllD(x, hits=n)`，对每名敌人伤害 | 其他 |
| `N_RANDOM_DAMAGE` | `N:RandomD(x, hits=n)`，每段随机选存活敌人 | 其他 |
| `N_BLOCK` | `N:B(x)` | 其他 |
| `N_DRAW` | `N:Draw(x)` | 其他 |
| `N_ENERGY` | `N:E(x)` | 其他 |
| `N_HP_LOSS` / `N_HEAL` | `N:HP-(x)` / `N:Heal(x)` | 其他 |
| `N_SELF_POWER` | `N:Self(状态, x)` | 其他 |
| `N_EXHAUST` | `N:Exhaust(选择/随机/全部, 过滤器)` | 其他 |
| `N_CREATE` | `N:Create(对象, 位置, 数量, 属性)` | 其他 |
| `N_PILE_MOVE` | `N:Move(牌, 区域A→区域B, 规则)` | 其他 |

### modifier、条件和能力触发

| ID | operation（参数槽） | 依赖 / 说明 |
| --- | --- | --- |
| `M_REPEAT` | `M:repeat(n)` | 将同牌一个伤害 effect 重复 n 次 |
| `M_SCALE` | `M:base + per × count(条件)` | 依附伤害或格挡数值 effect |
| `M_REPLACE_VALUE` | `M:value = state(状态)` | 依附数值 effect，如“造成当前格挡值伤害” |
| `C_IF` | `C:if(条件) => effect` | 单次出牌条件 |
| `C_PER` | `C:forEach(事件/对象) => effect` | 一次结算内逐个触发 |
| `C_AFTER` | `C:after(事件) => effect` | 延后结算 |
| `A_TURN_START` / `A_TURN_END` | `A:turnStart/turnEnd => effect` | 能力牌持续触发 |
| `A_WHEN` | `A:when(事件, 过滤器) => effect` | 能力牌事件触发 |
| `A_RULE` | `A:rule(全局替换规则)` | 能力牌改变规则 |
| `K` | `K:Exhaust`、`K:Innate` 等 | 卡牌 keyword |

## 重建清单

| 卡牌（ID） | 费用 / 类型 / 选目标 | operations（按结算顺序） | keyword |
| --- | --- | --- | --- |
| 好勇斗狠 `Aggression` | 1 / 能力 / 其他 | `A:turnStart => N:Move(random Attack, 弃牌堆→手牌) + I:UpgradeThatCard` | — |
| 愤怒 `Anger` | 0 / 攻击 / 单敌 | `T:D(6)`；`N:Create(copy(card), 弃牌堆, 1)` | — |
| 武装 `Armaments` | 1 / 技能 / 其他 | `N:B(5)`；`I:Upgrade(select 1 card in hand)` | — |
| 灰烬打击 `AshenStrike` | 1 / 攻击 / 单敌 | `T:D(6) + M:base + 3 × count(消耗牌堆全部牌)` | `Tag:Strike` |
| 壁垒 `Barricade` | 3 / 能力 / 其他 | `A:rule(你的格挡在回合开始时不移除)` | — |
| 痛击 `Bash` | 2 / 攻击 / 单敌 | `T:D(8)`；`T:Apply(V,2)` | — |
| 战斗专注 `BattleTrance` | 0 / 技能 / 其他 | `N:Draw(3)`；`I:本回合不能再抽牌` | — |
| 血墙 `BloodWall` | 2 / 技能 / 其他 | `N:HP-(2)`；`N:B(16)` | — |
| 放血 `Bloodletting` | 0 / 技能 / 其他 | `N:HP-(3)`；`N:E(2)` | — |
| 重锤 `Bludgeon` | 3 / 攻击 / 单敌 | `T:D(32)` | — |
| 全身撞击 `BodySlam` | 1 / 攻击 / 单敌 | `T:D(0) + M:value = state(当前格挡)` | — |
| 烙印 `Brand` | 0 / 技能 / 其他 | `N:HP-(1)`；`N:Exhaust(选择1张, 手)`；`N:Self(S,1)` | — |
| 破击 `Break` | 1 / 攻击 / 单敌 | `T:D(20)`；`T:Apply(V,5)` | — |
| 突破 `Breakthrough` | 1 / 攻击 / 其他 | `N:HP-(1)`；`N:AllD(9, hits=1)` | — |
| 欺凌 `Bully` | 0 / 攻击 / 单敌 | `T:D(4) + M:base + 2 × count(target.V)` | — |
| 燃烧契约 `BurningPact` | 1 / 技能 / 其他 | `N:Exhaust(选择1张, 手)`；`N:Draw(2)` | — |
| 倾泻 `Cascade` | X / 技能 / 其他 | `I:打出抽牌堆顶部X张牌（正常打出；不强制消耗）` | — |
| 余烬 `Cinder` | 2 / 攻击 / 单敌 | `T:D(18)`；`N:Exhaust(随机1张, 手牌)` | — |
| 巨像 `Colossus` | 1 / 技能 / 其他 | `N:B(4)`；`C:untilTurnEnd(if attacker has V, 受到攻击伤害 × 50%)` | — |
| 焚烧 `Conflagration` | 1 / 攻击 / 其他 | `N:AllD(2, hits=4)` | — |
| 腐化 `Corruption` | 3 / 能力 / 其他 | `A:rule(你的技能费用=0)`；`A:when(打出 Skill) => N:Exhaust(该技能)` | — |
| 绯红披风 `CrimsonMantle` | 1 / 能力 / 其他 | `A:turnStart => N:HP-(1) + N:B(7)` | — |
| 残酷 `Cruelty` | 1 / 能力 / 其他 | `A:rule(有V的敌人额外受到25%伤害)` | — |
| 黑暗之拥 `DarkEmbrace` | 2 / 能力 / 其他 | `A:when(任意牌被消耗) => N:Draw(1)` | — |
| 防御 `DefendIronclad` | 1 / 技能 / 其他 | `N:B(5)` | `Tag:Defend` |
| 恶魔形态 `DemonForm` | 3 / 能力 / 其他 | `A:turnStart => N:Self(S,3)` | — |
| 拆卸 `Dismantle` | 1 / 攻击 / 单敌 | `T:D(8) + C:if(target has V) => M:repeat(2), else hits=1` | — |
| 主宰 `Dominate` | 1 / 技能 / 单敌 | `T:Apply(V,1)`；`N:Self(S, count(target.V) after applying)` | `K:Exhaust` |
| 战鼓 `DrumOfBattle` | 1 / 技能 / 其他 | `N:Draw(2)`；`C:after(exhausted(card)) => N:E(2)` | — |
| 邪眼 `EvilEye` | 1 / 技能 / 其他 | `N:B(8)`；`C:if(本回合曾消耗牌) => N:B(8)` | — |
| 跃跃欲试 `ExpectAFight` | 3 / 技能 / 其他 | `N:B(15) + M:base + 5 × max(0, self.S)` | — |
| 狂宴 `Feed` | 1 / 攻击 / 单敌 | `T:D(10)`；`C:if(此伤害斩杀) => I:永久最大生命+3` | `K:Exhaust` |
| 无惧疼痛 `FeelNoPain` | 1 / 能力 / 其他 | `A:when(任意牌被消耗) => N:B(3)` | — |
| 恶魔之焰 `FiendFire` | 2 / 攻击 / 单敌 | `N:Exhaust(全部, 手牌)`；`C:forEach(被此效果消耗的牌) => T:D(7)` | `K:Exhaust` |
| 与我一战！ `FightMe` | 2 / 攻击 / 单敌 | `T:D(5) + M:repeat(2)`；`N:Self(S,3)`；`T:Apply(enemy S,1)` | — |
| 火焰屏障 `FlameBarrier` | 2 / 技能 / 其他 | `N:B(12)`；`C:untilTurnEnd(forEach(你受到攻击)) => N:反伤攻击者D(4)` | — |
| 被遗忘的仪式 `ForgottenRitual` | 1 / 技能 / 其他 | `N:E(3)` | `K:Exhaust` |
| 破灭 `Havoc` | 1 / 技能 / 其他 | `I:打出抽牌堆顶牌，并强制消耗该牌` | — |
| 头槌 `Headbutt` | 1 / 攻击 / 单敌 | `T:D(9)`；`N:Move(选择1张, 弃牌堆→抽牌堆顶)` | — |
| 地狱狂徒 `Hellraiser` | 2 / 能力 / 其他 | `A:when(抽到名字中有“打击”的牌) => I:对一名随机敌人打出该牌` | — |
| 御血术 `Hemokinesis` | 1 / 攻击 / 单敌 | `N:HP-(2)`；`T:D(15)` | — |
| 彼岸咆哮 `HowlFromBeyond` | 3 / 攻击 / 其他 | `N:AllD(18, hits=1)`；`C:after(回合结束且card在消耗堆) => I:打出此牌` | — |
| 岿然不动 `Impervious` | 2 / 技能 / 其他 | `N:B(30)` | `K:Exhaust` |
| 地狱之刃 `InfernalBlade` | 1 / 技能 / 其他 | `I:Create(random Attack, 手牌, 1, 本回合费用0)` | `K:Exhaust` |
| 狱火 `Inferno` | 1 / 能力 / 其他 | `A:turnStart => N:HP-(1)`；`A:when(你的回合内失去生命) => N:AllD(6, hits=1)` | — |
| 燃烧 `Inflame` | 1 / 能力 / 其他 | `N:Self(S,2)` | — |
| 铁斩波 `IronWave` | 1 / 攻击 / 单敌 | `N:B(5)`；`T:D(5)` | — |
| 势不可当 `Juggernaut` | 2 / 能力 / 其他 | `A:when(你获得格挡) => N:RandomD(6, hits=1)` | — |
| 杂耍 `Juggling` | 1 / 能力 / 其他 | `A:when(每回合第3张 Attack 被打出) => N:Create(copy(该攻击), 手牌, 1)` | — |
| 凌虐 `Mangle` | 3 / 攻击 / 单敌 | `T:D(20)`；`T:Apply(本回合敌方S-10)` | — |
| 熔融之拳 `MoltenFist` | 1 / 攻击 / 单敌 | `T:D(10)`；`T:Apply(V, count(target.V after damage))`（即把易伤层数翻倍） | `K:Exhaust` |
| 时候未到 `NotYet` | 2 / 技能 / 其他 | `N:Heal(10)` | `K:Exhaust` |
| 祭品 `Offering` | 0 / 技能 / 其他 | `N:HP-(6)`；`N:E(2)`；`N:Draw(3)` | `K:Exhaust` |
| 连环拳 `OneTwoPunch` | 1 / 技能 / 其他 | `C:grantNextAttacksThisTurn(1) => I:额外打出一次该攻击` | — |
| 契约终结 `PactsEnd` | 0 / 攻击 / 其他 | `C:if(count(消耗牌堆) >= 3) => N:AllD(18, hits=1)` | — |
| 完美打击 `PerfectedStrike` | 2 / 攻击 / 单敌 | `T:D(6) + M:base + 2 × count(本场战斗所有区域中 Tag:Strike 卡)` | `Tag:Strike` |
| 劫掠 `Pillage` | 1 / 攻击 / 单敌 | `T:D(6)`；`I:连续抽牌，直到抽到非攻击牌（该非攻击牌也抽到）` | — |
| 剑柄打击 `PommelStrike` | 1 / 攻击 / 单敌 | `T:D(9)`；`N:Draw(1)` | `Tag:Strike` |
| 原始力量 `PrimalForce` | 0 / 技能 / 其他 | `I:Transform(手牌全部可变形 Attack → 巨石)` | — |
| 薪火之源 `Pyre` | 2 / 能力 / 其他 | `A:turnStart => N:E(1)` | — |
| 狂怒 `Rage` | 0 / 技能 / 其他 | `C:untilTurnEnd(每当你打出 Attack) => N:B(3)` | — |
| 暴走 `Rampage` | 1 / 攻击 / 单敌 | `T:D(10)`；`I:本场战斗中此卡的基础伤害+5` | — |
| 撕裂 `Rupture` | 1 / 能力 / 其他 | `A:when(你的回合内失去生命) => N:Self(S,1)` | — |
| 重振精神 `SecondWind` | 1 / 技能 / 其他 | `N:Exhaust(全部非Attack, 手牌)`；`C:forEach(手牌全部非Attack) => N:B(5)` | — |
| 预备打击 `SetupStrike` | 1 / 攻击 / 单敌 | `T:D(7)`；`I:本回合获得S(2)` | `Tag:Strike` |
| 耸肩无视 `ShrugItOff` | 1 / 技能 / 其他 | `N:B(8)`；`N:Draw(1)` | — |
| 怨恨 `Spite` | 0 / 攻击 / 单敌 | `T:D(5)`；`C:if(你本回合失去过生命) => M:repeat(2)` | — |
| 惊逃 `Stampede` | 2 / 能力 / 其他 | `A:turnEnd => I:从手牌随机自动打出1张Attack，目标随机敌人` | — |
| 添柴 `Stoke` | 1 / 技能 / 其他 | `N:Exhaust(全部, 手牌)`；`C:forEach(被此效果消耗的牌) => N:Create(random card from own pool, 手牌, 1)` | — |
| 踩踏 `Stomp` | 3 / 攻击 / 其他 | `N:AllD(12, hits=1)`；`C:whileInCombat(本回合每打出过1张Attack，此卡费用-1)` | — |
| 岩石铠甲 `StoneArmor` | 1 / 能力 / 其他 | `N:Self(覆甲,4)` | — |
| 打击 `StrikeIronclad` | 1 / 攻击 / 单敌 | `T:D(6)` | `Tag:Strike` |
| 飞剑回旋镖 `SwordBoomerang` | 1 / 攻击 / 其他 | `N:RandomD(3, hits=3)` | — |
| 挑衅 `Taunt` | 1 / 技能 / 单敌 | `N:B(6)`；`T:Apply(V,1)` | — |
| 扯碎 `TearAsunder` | 2 / 攻击 / 单敌 | `T:D(5) + M:repeat(1 + count(本场战斗你失去生命的次数))` | — |
| 痛殴 `Thrash` | 1 / 攻击 / 单敌 | `T:D(4) + M:repeat(2)`；`I:随机消耗手牌1张Attack`；`I:将该攻击牌的当前伤害永久加到此卡基础伤害` | — |
| 闪电霹雳 `Thunderclap` | 1 / 攻击 / 其他 | `N:AllD(4, hits=1)`；`I:对每名敌人Apply(V,1)` | — |
| 战栗 `Tremble` | 1 / 技能 / 单敌 | `T:Apply(V,3)` | `K:Exhaust` |
| 坚毅 `TrueGrit` | 1 / 技能 / 其他 | `N:B(7)`；`N:Exhaust(随机1张, 手牌)` | — |
| 双重打击 `TwinStrike` | 1 / 攻击 / 单敌 | `T:D(5) + M:repeat(2)` | `Tag:Strike` |
| 坚定不移 `Unmovable` | 2 / 能力 / 其他 | `A:rule(每回合第一次从卡牌获得的格挡翻倍)` | — |
| 无情猛攻 `Unrelenting` | 2 / 攻击 / 单敌 | `T:D(14)`；`C:grantNextAttack => I:费用变为0` | — |
| 上勾拳 `Uppercut` | 2 / 攻击 / 单敌 | `T:D(13)`；`T:Apply(W,1)`；`T:Apply(V,1)` | — |
| 凶恶 `Vicious` | 1 / 能力 / 其他 | `A:when(你施加V) => N:Draw(1)` | — |
| 旋风斩 `Whirlwind` | X / 攻击 / 其他 | `N:AllD(5, hits=本次支付的X)` | — |

## 拟合时应保留的结构约束

1. `T:*` 只能和“攻击/技能 + 单敌”这个牌壳组合；`N_ALL_DAMAGE` / `N_RANDOM_DAMAGE` 会把牌壳固定为“其他”。
2. `M_REPEAT`、`M_SCALE`、`M_REPLACE_VALUE` 至少要绑定一个同卡、同结算域的数值 effect。`M_REPEAT` 不能独立抽到。
3. `C:*` 后接 1–2 个 effect；`A:*` 只能出现在能力牌上；`A_RULE` 通常单独占一张能力牌，以免生成不可读的全局规则叠加。
4. `K:Exhaust` 是**牌本身**的 keyword；诸如“消耗一张牌”只是 `N_EXHAUST`，不能误把本牌也标为消耗。
5. 产物槽位应显式建模为枚举，而不是文本：至少有 `巨石`、`复制自身`、`随机攻击`、`本角色卡池随机牌`。牌堆/区域、过滤器、是否免费、是否自动打出也都是 operation 参数。
6. `I:` 共有 20 个左右的稀有/复杂原子；先以独立组件进入样本可保持 100% 复现。等基础生成器稳定后，再优先拆 `I:自动打出`、`I:抽牌至条件`、`I:变形`、`I:费用规则` 这四组，它们在其他角色中复用率最高。
