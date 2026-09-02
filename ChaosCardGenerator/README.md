# ChaosCardGenerator

`ChaosCardGenerator` is Auto-Anthonyology's standalone, deterministic card-pool generator. It has no dependency on
`sts2.dll` or Godot. The game assembly maps its `GeneratorOperation` and `OperationRuntimeSpec` records to concrete
`CardModel`, `DynamicVar`, `OnPlay`, and Power behavior.

The generator returns cost, optional Star cost, card type, target mode, rarity, bilingual descriptions, tags,
ordered operations, upgrades, and stable runtime specifications. Trigger and payoff operations remain separate in
data and are joined only by the renderer. Cross-operation card references use named `CardTargetSlot` values rather
than localized prose.

## Commands

```powershell
dotnet run --project .\ChaosCardGenerator\ChaosCardGenerator.csproj -- --seed 42 --count 3
dotnet run --project .\ChaosCardGenerator\ChaosCardGenerator.csproj -c Release -- --self-test
```

Run `--help` for the complete generator and audit command list.

## Catalog coverage

The six reviewed `*_unit_operations.md` files are embedded authoring inputs. Production assembly samples card shells,
component families, semantic variants, values, tags, and upgrades independently; it never selects a native-card
preset merely to satisfy coverage. Every native sequence is nevertheless checked for nonzero reachability through
the same production legality rules.

`--self-test` exercises deterministic generation, structured runtime coverage, upgrade behavior, pool constraints,
and exact native assembly reachability. The full mod build adds execution-route and localization-boundary audits.

For extension contracts, see the [Component API Wiki](https://github.com/mewcodex/AutoAnthony/wiki/Component-API-Overview)
and [`COMPONENT_API.md`](../COMPONENT_API.md).
