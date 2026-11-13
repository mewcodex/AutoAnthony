# Upgrade lifecycle regression

Run against a freshly built runtime:

```powershell
$env:STS2_GAME_DIR = 'F:\SteamLibrary\steamapps\common\Slay the Spire 2'
dotnet run --project tests/UpgradeLifecycleRegression -c Release
```

This harness exercises native `UpgradeInternal`, clone/cache restoration, the generated numeric variables and
the executor's effective RuntimeSpec without launching Godot or accessing user saves.

Coverage:

- Reject increasing an upgrade's printed energy cost, fixed 0 -> -1, and reductions bound to positive draw slots.
- Keep existing malformed resource upgrades from increasing cost or converting a fixed payment into X.
- Make descriptions, variables and execution share semantic upgrade guards and numeric caps.
- Keep a new cap from reversing the sign/magnitude of an existing numeric upgrade; cap curse compensation at
  generation time instead of shrinking an oversized reward when it is upgraded.
- Preserve ordinary draw/cost upgrades, signed native payment reductions, and exactly-once upgrade restoration.
- Reject Osty death before attacks and simultaneous Summon/death, including conditional/scheduled branches;
  retain attack-then-death, independent future Summons and native Sacrifice.
- Exercise 300 deterministic Machine Learning upgrade plans.

This is not a reproduction of the remote player's exact seed/save or an engine-rendering test.
