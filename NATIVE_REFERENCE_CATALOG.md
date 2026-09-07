# Native card reconstruction catalog

The native reference catalog is a complete, read-only description of the v111 card set. It is intended for decomposition review, reverse valuation, and reconstruction tests. It is not an input to AutoAnthony's random-card generator.

## Files

- `ChaosCardGenerator/Data/native_reference_cards.json`: card metadata, bilingual localization templates, dynamic variables, ordered component occurrences, and structured upgrades.
- `ChaosCardGenerator/Data/native_reference_components.json`: parameterized component contracts and the complete native keyword dictionary.
- `tools/build_native_reference_catalog.py`: deterministic authoring tool that rebuilds both files from the decompiled v111 card sources and the reviewed generator catalog.

The two JSON resources are embedded only in `ChaosCardGenerator`, the offline authoring executable. They are neither embedded in `ChaosMode` nor copied into the deployed mod, so every entry and definition is marked `ReferenceOnly: true` and `GenerationEligible: false`.

## Coverage

The catalog contains 567 records derived from 559 non-multiplayer source classes. `MadScience` is expanded into its nine legal type/rider combinations, producing eight additional records. Multiplayer-only and mock test cards are excluded.

| Pool | Records |
| --- | ---: |
| Ironclad | 85 |
| Silent | 86 |
| Defect | 86 |
| Necrobinder | 86 |
| Regent | 86 |
| Colorless | 53 |
| Event | 36 |
| Curse | 18 |
| Status | 12 |
| Quest | 4 |
| Token / derivatives | 14 |
| Deprecated | 1 |

The token/derivative pool contains `Disintegration`, `Fuel`, `GiantRock`, `Luminesce`, `MindRot`, `MinionDiveBomb`, `MinionSacrifice`, `MinionStrike`, `Shiv`, `Sloth`, `Soul`, `SovereignBlade`, `SweepingGaze`, and `WasteAway`.

`Fasten` and `DeprecatedCard` are included explicitly. Event-rarity cards retain their actual in-run behavior. `Unplayable` is represented as a first-class keyword (`不可被打出`) and is assigned according to the native source rather than inferred from rarity.

## Reconstruction model

Each card record provides:

- canonical pool, type, rarity, target, costs, X-cost flags, tags, and keywords;
- named native variables with base values or source expressions;
- ordered unit-operation occurrences;
- `TriggerOwner`, which links a result to its preceding trigger without merging the two operations;
- arguments sourced from a native variable, a reviewed runtime value, or a literal;
- for generator-backed runtime arguments, optional `NativeBindings` records that explicitly map the argument to
  its native `DynamicVar`, native base value, upgrade delta, and affine value transform;
- the full bilingual localization templates;
- upgrade actions and their resulting costs, values, and keywords;
- explicit rules for upgrades whose native implementation branches on `IsUpgraded` rather than changing a numeric variable.

For the nine `MadScience` records, `TemplateBindings` fixes the selected card type and rider. Cards with intentionally empty native description text are reconstructed from their keywords and metadata alone.

Given implementations for every component and keyword, reconstruction consists of instantiating the base metadata and variables, executing components in listed order while honoring `TriggerOwner`, and applying the structured upgrade actions. No natural-language parsing is required.

`Upgradable` belongs to the random generator contract: it says that AutoAnthony may offer a numeric upgrade for
that slot. It does **not** describe how the native card upgrades. Native upgrades are represented by
`NativeBindings[].UpgradeDelta`; schema version 2 audits that every `ChangeVariable` action on a
generator-backed native card is bound to at least one component argument. `ValueTransform` records cases such as
an “additional hits” argument whose displayed value is the native repeat count minus one.

## 中文说明

这是一份独立、只读的 v111 原版卡牌复现目录，不参与东尼算法的随机组卡。它覆盖所有非多人卡，包括状态、诅咒、任务、事件稀有度、弃用卡、勒紧，以及 14 张衍生物；《疯狂科学》按 9 种实际组合分别记录。

卡牌、组件和关键词均带有 `ReferenceOnly: true`、`GenerationEligible: false`。组件参数、触发归属、变量、升级动作和双语模板均为结构化字段；已知每个组件的行为后，不需要解析描述文本即可复现卡牌及其升级效果。

从 schema v2 开始，生成器组件的数值参数可带 `NativeBindings`，显式记录它对应的原版
`DynamicVar`、基础值、升级增量和数值变换。`Upgradable` 只表示东尼算法是否允许给该随机组件生成
数值升级，不能再被导入器当作原版升级信息；原版升级必须读取 `NativeBindings[].UpgradeDelta`。
