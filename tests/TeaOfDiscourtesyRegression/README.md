# Tea of Discourtesy regression investigation

Investigation date: 2026-10-25. Target: the installed v111 game DLL and AutoAnthony 0.3.148.

Remote report: owning Tea of Discourtesy stalls combat before the opening draw; continuing the run subsequently fails.
This report has **not** been reproduced by the isolated tests below. No production fix is asserted.

## Run

Build AutoAnthony Release first, then from the repository root:

```powershell
$env:STS2_GAME_DIR = 'F:\SteamLibrary\steamapps\common\Slay the Spire 2'
dotnet run --project tests/TeaOfDiscourtesyRegression -c Release
```

The harness does not launch Godot or read/write user preferences, local saves or Steam Cloud.

## Combat-entry save boundary

Native `RunManager.EnterMapPointInternal` saves after leaving the prior room and before constructing/entering
the next room. `CombatManager.StartCombatInternal` invokes `BeforeCombatStart` before the first turn/draw.
Tea creates two Dazed in the combat draw pile, then decrements its remaining-use counter. Ordinary disk saves
serialize the permanent deck and relic properties; they do not store those two temporary Dazed as deck cards.
Consequently, restoring that entry save legitimately replays Tea. A repeated failure does not by itself establish
save corruption. Native save canonicalization constructs a temporary RunState, but does not enter combat or
execute Tea's `BeforeCombatStart` hook.

AutoAnthony's save postfix attaches a fresh modifier/property container containing a serialized pool string.
It does not replace `ModelDb.Card<Dazed>()`, move native status cards, or store combat pile references in the pool.

## Reference isolation and related cards

- Native `BoostAway` (高速脱离) directly generates Dazed. `Smokestack` (烟囱), `TrashToTreasure` (化废为宝)
  and `Iteration` (迭代) respond to status generation/draw rather than a specific Dazed reference.
- Tea and Blessed Antler create native Dazed through `CombatState.CreateCard<Dazed>`.
- AutoAnthony's derivative slot creates a native Dazed through the same API. Its detached hover preview uses
  `ModelDb.Card<Dazed>().ToMutable()`; keyword, dynamic-variable and energy-cost state belong to separate instances.
- AutoAnthony's `D:CostDownWhenStatusGenerated` observes Tea's two statuses and reduces its own cost twice.
  Generated/status-generated power triggers require an already active power; an unplayed power in the deck is
  not automatically active at combat start.

## Tested

112 combinations cover AA observation patches enabled/disabled, local/foreign ownership, relic order, and
Regalite, Blessed Antler, Ninja Scroll, Funerary Mask, Radiant Pearl, Jeweled Mask and Bone Tea, including
Regalite plus another companion. Native card insertion, generation hooks, initial draw, saved Tea properties
and the relevant companion hooks execute unchanged. Timers, native scene names and engine rendering are stubbed.

Additional checks assert separate preview/generated/clone instances, unchanged canonical keywords, no permanent
deck mutation and immutable captured Tea save records. Four fresh-scope restores use native JSON serializers
for deck cards, Tea properties and run RNG, then execute native deck cloning/shuffle, Tea + Regalite and a five-card
draw. All four reproduce the same pile order with exactly two new Dazed and no accumulation.

These are data-boundary and command/hook tests, **not** a full `RunManager` scene/save-store round trip. They do
not exercise Godot preview animation, Steam/network transport, Toolbox/Gambling Chip selection UI or other mods.
An affected remote save and the first exception remain necessary to attribute that specific failure.
