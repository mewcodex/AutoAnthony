# 东尼算法（Auto-Anthonyology）

[English](README.md) | [简体中文](README.zh-CN.md)

![东尼算法模组图片](ChaosMode/assets/mod_image.png)

东尼算法是一个《杀戮尖塔 2》卡池随机模组，灵感来自《以撒的结合》的 DELETE THIS 挑战。每局开始时，
模组会把原版卡牌拆成可复用的 unit operation，再重新组装出包含名称、卡图、升级、目标和实际效果的新卡池。

模组目前处于开放测试阶段，适配《杀戮尖塔 2》`0.111.x`，支持战士、猎手、故障机器人、亡灵契约师、
储君以及两种无色卡池。

## 主要功能

- 按角色替换卡池，并保留原版稀有度数量和大体分布。
- 将完整结构化卡牌定义写入本局快照，保证读档和联机时效果稳定。
- 提供终极混乱、数值平衡/激进、数值随机、添加东尼算法/原版卡牌、替换初始卡、随机卡图和三种惊喜模式。
- “拆解原版卡”可让尚未重组的原版卡也使用已审阅的组件逻辑渲染描述，不改变其本体执行逻辑。
- 安装 Card Tinkering 衍生模组后，可在设置中开启“随时编辑”，从战斗外（包括战斗奖励页）的卡组页面进入工匠台。
- 使用带版本的 `OperationRuntimeSpec` 执行卡牌，不依赖中文或英文描述判断游戏逻辑。
- 提供组件 API v3，允许其他角色模组注册组件池、出率、数值与关键词策略、语义标记、稳定本地化 ID、
  运行时处理器、提示和随机卡槽位。
- 提供覆盖 v111 全部非多人卡牌的只读拆解目录；Card Tinkering 可选地编辑全部 481 张常规原版牌，且不会
  替换原版卡池或转换未修改的牌。

## 安装

可直接订阅 [Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3786611028)，或将构建后的
`AutoAnthony` 文件夹复制到游戏的 `mods` 目录。多人游戏的所有参与者应使用相同的模组版本，并保持所有
“在下一局生效”的设置一致。

## 构建

需要安装 .NET 10 SDK、Python 3、PowerShell，并准备《杀戮尖塔 2》`0.111.x`：

```powershell
$env:STS2_GAME_DIR = "C:\path\to\Slay the Spire 2"
.\build.ps1
```

成品输出到 `ChaosMode/build/AutoAnthony`。构建过程会验证结构化组件目录、运行规格、执行路由、本地化边界，
并编译一个模拟外部模组的 API 契约项目。

单独运行生成器自检：

```powershell
dotnet run --project .\ChaosCardGenerator\ChaosCardGenerator.csproj -c Release -- --self-test
```

## 组件 API

请从 [组件 API Wiki 中文首页](https://github.com/mewcodex/AutoAnthony/wiki/组件-API-概览) 开始阅读。
仓库内还提供简明契约 [COMPONENT_API.md](COMPONENT_API.md) 和不依赖角色框架的
[`WatcherComponentAdapter.cs.txt`](examples/WatcherComponentAdapter.cs.txt) 示例。

卡牌编辑类配套模组另请参阅
[卡牌编辑与设置 API](https://github.com/mewcodex/AutoAnthony/wiki/卡牌编辑与设置-API)。

API 只依赖稳定的 ASCII 标识符和结构化运行数据，不要求 BaseLib、RitsuLib 或某一种角色模组框架。

## 目录结构

- `ChaosMode/`：游戏运行时、解释器、存档、设置和 Harmony 补丁。
- `ChaosCardGenerator/`：独立生成器、平衡模型、组件目录、升级逻辑和审计逻辑。
- `ApiContractSmoke/`：以外部模组身份编译的 API 契约测试。
- `scripts/`：目录、执行、本地化边界和数值估值审计脚本。
- `*_unit_operations.md`：经人工审核并嵌入生成器的双语组件拆解数据。
- `audits/structured_operation_refactor/`：构建所需的结构化兼容审计基线。
- `tools/pack_godot_pck.py`：构建时使用的最小 Godot 4 PCK 打包工具。

## 参与开发

不要让游戏逻辑依赖本地化描述。新增 operation 时需要同时提供结构化运行规格、价值模型、执行路由、中英文
文本投影以及相应审计覆盖。提交前请运行完整构建和生成器自检。

## 作者

作者：Alriph。GitHub 仓库以 `mewcodex` 账号维护。

## 许可证

[MIT](LICENSE)
