# Trigger source and per-field growth regression

Target: v111 and AutoAnthony 0.3.153. Run after building the runtime:

```powershell
$env:STS2_GAME_DIR = 'F:\SteamLibrary\steamapps\common\Slay the Spire 2'
dotnet run --project tests/SelfMutationRegression -c Release
```

The harness executes the actual direct-play loop, draw hooks, detached-trigger reconstruction, local cost
modifiers, damage resolver, card cloning, numeric preview and native SavedProperties conversion. Rendering,
damage delivery and Power UI installation are stubbed; native combat piles are moved explicitly. It does not
launch Godot, access user saves or reproduce an anonymous player's exact card.

Coverage:

- Direct area damage followed by Rolling Boulder growth: 9, 14, 19; Exhaust/return retains the growth.
- Growth affects only its adjacent damage field; failed conditions do not grow damage.
- Copies retain existing growth and evolve independently; fresh cards start without it.
- Mutable saved arrays and legacy captured-bonus dictionaries are isolated by native card/Power cloning.
- Numeric preview and native saved properties retain the same field binding; empty growth is not serialized.
- Reordering components preserves an unambiguous surviving damage field; deleting it cannot transfer the growth
  to another field. Matching runs only during definition rebuild, not every frame.
- Persistent Rolling Boulder remains 5, 8, 11 without applying direct-card growth a second time.
- Multiple rolling fields remain independent, unrelated event amounts never replace damage, and captured X stays
  separate from additive growth. Legacy absolute totals migrate once and survive native Power serialization.
- Legacy Power descriptions display the saved total without mutating multiplayer checksums on hover.
- Draw-triggered self-cost reduction follows its exact applying combat copy, including copies without a deck
  identity; identical other copies remain unchanged and opening-hand draws are excluded.
- Removed sources cannot redirect mutations; Power clones preserve source identity. A restored recorded deck
  identity resolves only its unique combat copy, while missing identities never guess by matching definitions.
- Different Powers have independent timepoint guards; cloning an already limited Power does not reset its budget.
- Power generation rejects post-play self-cost changes, while passive cost reduction when drawing the Power
  itself remains available.

Runtime-only trigger-to-source bindings are weakly keyed and are not new save/network fields. Persistent
triggers retain their recorded deck-index fallback for older reconstructed instances, requiring an exact unique
combat clone. Definition equality alone is deliberately not treated as a card identity. Direct
damage growth uses compact operation-index/bonus pairs with copy-on-write updates, and does not write to the
permanent deck card or shared generated pool.

Triggered rolling growth uses the same compact additive representation on each Power, serialized separately
from the old absolute `RollingDamage` compatibility value. It is included in the Power multiplayer checksum.
The old generic EventAmount damage override has been removed: event payloads and damage growth have separate
semantics. Pre-fix failure output is retained in `audits/self-mutation-0153-before.txt`.
