# 文本依赖迁移优先级

基线扫描共704处候选。该数字是宽松上界，不等同于704个bug或704个实际语义依赖；渲染器、日志和审计工具中的文本读取允许保留。

## P0：战斗执行与卡死风险

| 文件 | 候选数 | 主要职责 | 迁移批次 |
|---|---:|---|---|
| `ChaosMode/ChaosOperationExecutor.cs` | 68 | 即时效果、条件、自动打出、选择、伤害modifier | R1–R4 |
| `ChaosMode/ChaosCompositePower.cs` | 12 | 跨回合能力、事件触发、剩余次数 | R4 |
| `ChaosMode/ChaosCardModel.cs` | 21 | 动态数值、升级值、预览及部分触发 | R2–R4 |

这三处优先迁移。任何中文措辞变化都可能改变实际结算或令异步命令无法完成。

## P1：生成、预算和升级语义

| 文件 | 候选数 | 主要职责 | 迁移批次 |
|---|---:|---|---|
| `ComponentAssemblyGenerator.cs` | 305 | 合法性、组件依赖、目标、正负面、字段族 | R5 |
| `EffectBalanceModel.cs` | 92 | 价值、触发频率、整卡预算 | R5 |
| `CardIdentityAndUpgradeGenerator.cs` | 28 | 升级方向、文本数值替换 | R2/R5 |
| `GenerationTuning.cs` | 20 | 概率和补偿 | R5 |
| `NativeUpgradeValueModel.cs` | 12 | 原版升级拟合 | R2/R5 |

这些代码通常不会立即卡死，但可能静默改变概率、数值或升级方向，因此必须继续受Golden Master约束。

## P2：卡图、名称和展示相关分类

| 文件 | 候选数 | 处理方式 |
|---|---:|---|
| `ChaosMode/ChaosRunDefinitions.cs` | 19 | 卡图相似度改读结构化效果族 |
| `EnglishCardDescriptionRenderer.cs` | 34 | 最终保留文本输出，但改为从RuntimeSpec渲染 |
| `CardTextStyle.cs` | 9 | 最终保留纯展示修饰，不得反馈到语义 |

## P3：允许列表候选

日志、hover tip最终拼接、历史快照原始字符串识别、审计工具自身可以继续读取文本，但必须进入明确允许列表。R7完成后，静态扫描对允许列表之外的新增依赖直接报错。

## 当前结构化覆盖

`runtime_spec_batch1_v2.tsv`：首批高风险复用Template共110个目录出现点，编译失败0：

- `T:Apply`：62
- `N:Self`：18
- `N:StrengthPerTargetVulnerable`：2
- `N:Create`：4
- `N:CreateCurrentCharacterCardInHand`：2
- `N:Move`：4
- `N:Exhaust`：18

`numeric_slot_inventory_v2.tsv`：565种文本schema；354种含固定数字，325种当前会建立DynamicVar，13种含多个固定数字，15种包含X。带多个数字的13种是R2第一优先级，因为“第一个数字”规则最容易选错字段。

带 `superseded_` 前缀的两个TSV是首次输出时未转义多行文本的废弃审计，不得作为迁移输入；保留只是因为当前工具策略阻止直接删除生成文件。
