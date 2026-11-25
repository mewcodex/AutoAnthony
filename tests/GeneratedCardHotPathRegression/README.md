# Generated-card hot-path regression

Run against a freshly built AutoAnthony runtime:

```powershell
$env:STS2_GAME_DIR = 'F:\SteamLibrary\steamapps\common\Slay the Spire 2'
dotnet run --project tests/GeneratedCardHotPathRegression -c Release -- audits/hotpath-after.json
dotnet run --project tests/GeneratedCardHotPathRegression -c Release -- --extensions
```

`--baseline` omits allocation assertions but keeps correctness assertions. The historical 0.3.149 measurement used
the earlier probe revision; this revision intentionally fails its safety checks on 0.3.150 and uses private cache
helpers introduced in 0.3.151. Use the matching historical probe for older-runtime comparisons. No game/user saves
are accessed.

The probe measures warmed property/description/tooltip/portrait-identity calls using thread-local allocated bytes
and elapsed time. Only native IHoverTip's static Summon localization-ID initialization is stubbed; none of the
probe's tips are Summon tips. Native Dazed creation, tooltip deduplication and the runtime implementation execute
unchanged. These are microbenchmarks, not rendered-frame measurements or a reproduction of the remote hitch.

Checks cover:

- Strike and derived Osty tags; definition replacement invalidating tags and the Block flag.
- Stray Shoot tags from old payloads do not enter retrieval filters; actual Shoot operations derive their tag,
  including the external profile projection.
- Changed description templates and replacement localization tables.
- Different portrait variant assignments producing different keys.
- Separate mutable derivative previews on every query, upgrade invalidation and edited references.
- Same-language locale reload invalidation; stale entries not retaining old localization tables.
- Enchantment ownership and detached-preview markers, including Instinct Giant Rock damage after preview reset.
- Run-level Ancient Fuel changes and live local-character energy hints.
- Live external tooltip/keyword callbacks with `--extensions`, including upgrade removal and registration freeze.
- A fixed-seed digest of 170 generated cards across all six pools, normal/Ultimate mode and legal regular rarities.

The 0.3.149, 0.3.150 and audited 0.3.151 runtimes produced the same digest:
`952C035827E0B6AD035A53BA2B166FBA5FE079EFBA2F2161BE86A8C89B3D4EA1`.

Representative allocation results without extension providers:

| Repeated operation | 0.3.149 (bytes/call) | 0.3.151 (bytes/call) |
| --- | ---: | ---: |
| Semantic tags | 200 | 40 |
| Block flag | 32 | 0 |
| Unchanged description installation | 504 | 0 |
| Portrait identity key | 224 | 0 |
| Empty extra tooltips | 2,320 | 0 |
| Derivative extra tooltips | 4,094 | 1,077 |

Small timer/instrumentation allocations are excluded by rounding. Timing varies with runtime warmup and machine
load; the byte reductions are the more stable comparison. Presentation caches use weak definition/instance keys;
art keys follow the existing process-lifetime asset cache and use only art identity fields. These caches do not
store live damage/block totals, battle counters, RNG results or extension callback output. The 0.3.150 prototype
cache exposed mutable derivative cards; 0.3.151 keeps those prototypes private and clones only card references
on retrieval. Energy hints are materialized live because their icon depends on the current local character.

Additional probes stub the native energy-hint factory (to vary its context without GPU assets) and initialize
LocManager through test-only field assignment. The production detached-preview postfix runs unchanged; no
localization files, game settings or saves are read/written. Pre-fix failures are recorded in
`audits/hotpath-0151-before-safety.txt`.
