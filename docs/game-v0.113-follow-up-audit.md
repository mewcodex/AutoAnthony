# v113 follow-up: existing characters and Colorless (0.3.165)

Sources: `proj/111`, `proj/113`, and the matching English localization exports. This is a source audit, not runtime parsing of descriptions. Fool-specific changes are covered separately.

## Scope and findings

- Reviewed all five older character pools and Colorless: 482 exact executable recipes, plus shared native reference cards.
- 45 pool-card implementations changed. Most changes expose hit counts as native variables rather than changing mechanics.
- My Shinies is the only added pool card; its Gold count and damage were already split in 0.3.159. No additional whole-card components are introduced here.
- Corrected Speedster's stale component damage (2 -> 3) and its explicit binding to `SpeedsterPower`, including the keyword-only upgrade.
- Existing reworks use generic conditions/payoffs, reusable cost reduction and lowest-HP targeting; no missing effect requires a new duplicate component.

## Card-by-card review

| Pool | Card | Finding |
| --- | --- | --- |
| Ironclad | Conflagration | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Ironclad | Dismantle | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Ironclad | ExpectAFight | Reworked: Block + hand Attack discount until played; two independently reusable effects. Discount price recalibrated. |
| Ironclad | FiendFire | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Ironclad | FightMe | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Ironclad | ForgottenRitual | Reworked: Exhausted-this-turn gate + Energy; existing reusable condition and resource effect. |
| Ironclad | Spite | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Ironclad | SwordBoomerang | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Ironclad | TearAsunder | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Ironclad | Thrash | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Ironclad | TwinStrike | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Silent | DaggerSpray | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Silent | EchoingSlash | Native attack animation addition; component mechanics unchanged. |
| Silent | Expertise | Reworked: Draw + Retain the entire hand this turn; separate effects. |
| Silent | Finisher | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Silent | Flechettes | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Silent | GrandFinale | Native VFX wait constant only; zero-cost special gate policy unchanged. |
| Silent | MementoMori | Reworked: Sly, 3 Energy, lowest-HP target; former discard-scaled modifier has no current native source. |
| Silent | PhantomBlades | 8 (11) first-Shiv bonus; Retain and first-Shiv bonus remain separate rules. Live keyword contribution now follows native v113. |
| Silent | Ricochet | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Silent | Speedster | Corrected missed 2 -> 3 AoE damage. Added explicit fixed Power-variable binding and recalibrated its exclusive draw cadence. |
| Defect | Barrage | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Defect | FlakCannon | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Defect | GunkUp | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Defect | HelixDrill | Native excludes actual Energy spent on this play; interpreter already uses CardPlay resources and the shared generic count prefix. |
| Defect | Refract | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Defect | Uproar | Native random selection now uses CombatCardSelection; interpreter already selects through this stream. |
| Necrobinder | BoneShards | Native kill command now includes source card; interpreter already carries source identity. |
| Necrobinder | Melancholy | Death hook gains source parameter; current interpreter compiled against v113 signature. |
| Necrobinder | PullFromBelow | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Necrobinder | Rattle | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Necrobinder | Sacrifice | Native kill command now includes source card; interpreter already carries source identity. |
| Regent | AstralPulse | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Regent | CelestialMight | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Regent | Glitterstream | Native out-variable cleanup only; delayed Block remains a generic delayed payoff. |
| Regent | LunarBlast | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Regent | Radiate | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Regent | Royalties | Native Blitz eligibility flag only; effect and shared Gold valuation unchanged. |
| Regent | SevenStars | Hit-count variable/type migration; damage, target and effect decomposition unchanged. |
| Colorless | BeatDown | Random discard Attack selection migrated from Shuffle to CombatCardSelection. |
| Colorless | Catastrophe | Random draw-pile play selection migrated from Shuffle to CombatCardSelection. |
| Colorless | HandOfGreed | Native Blitz eligibility flag only; effect and shared Gold valuation unchanged. |
| Colorless | MyShinies | New: Gold-threshold count prefix + generic damage. Calibrated to 200 carried Gold; threshold upgrade remains 20 -> 10. |
| Colorless | Omnislice | Capped copied-damage propagation; current interpreter already uses native Cap semantics. |
| Colorless | Salvo | 10 (14) damage; shared damage pricing unchanged. |

## Retired-source effects

| Former source | Historical component | New-pool policy |
| --- | --- | --- |
| Expect a Fight | Strength-scaled Block modifier | Already absent from native v113 generation recipes; keep that zero native prior. Persisted operations still execute. |
| Memento Mori | Damage per discarded card this turn | Already absent from native v113 generation recipes; keep that zero native prior. Persisted operations still execute. |
| Expertise | Draw plus Retain only those drawn cards | Replaced by separate Draw and whole-hand Retain. Historical combined operation remains readable/executable, without a current generation source. |

Do not lower the probability of a shared damage/Block/Exhaust component merely because one source card changed. No synthetic retired recipes are counted as present-day native occurrences. Existing saves are not rewritten to the new native effects.

## Exclusive-effect calibration

| Component | Previous | Current | Basis |
| --- | --- | --- |
| Hand Attack discount until played | 1,200 / Energy reduction | 2,800 / Energy reduction | Expect a Fight: 15 Block = 1,800; complete card 4,600 vs 4,760 3-Energy Uncommon center. |
| First Shiv bonus each turn | 180 / damage | 200 / damage | Native bonus 9 -> 8; preserve approximately the old complete package value. |
| Mid-turn draw Power | 4.8 per turn, lifetime cap 10 | 3.2 per turn, lifetime cap 20/3 | Speedster 2 -> 3 damage, same 2-Energy shell; native total remains 3,100. Ordinary/temporary draw triggers retain their own cadence. |
| Gold-threshold count | 100 expected carried Gold | 200 expected carried Gold | My Shinies: ten one-damage hits = 1,900 including multi-hit adaptability, matching 1-Energy Rare center. |

Shared damage prices and the user-specified derivative exchange values are unchanged. Salvo and Minion Dive Bomb use their updated native numbers rather than modifying every damage component to compensate.

## Runtime alignment

- Phantom Blades-style Retain is contributed by the live Power keyword hook. No permanent Retain is stamped on existing/new Shivs, and independent Retain survives Power removal.
- Beat Down and Catastrophe use `CombatCardSelection`, matching the native v113 RNG domain.
- Cold snapshot hydration restores the named count template for the two originally implicit one-card Transform producers. This avoids depending on process-local numeric English variants and unnecessarily rebuilding all pools on Continue. Counts and derivative identities remain exactly as saved.
- Fixed native bindings with zero numeric upgrade delta are supported explicitly; keyword-only upgrades can no longer conceal a stale component base.

## Verification

Release results are recorded in `audits/v113-03165-*`. Native engine tests use an isolated profile; no user run is modified. A local test does not replace a two-computer multiplayer session.

The initial cold-load test preserved all fingerprints but exposed unnecessary compatibility generation. The follow-up test additionally rejects any pool generation during Continue; this closes the gap that a hash-only round trip would miss.

Results: generator self-test and exact native decomposition passed (659 records, 566 exact executable upgrades). The isolated engine passed eight start/save/continue cases, each checking all seven pools / 606 cards. The final cold Continue matched both fingerprints with zero pool generation. Pure tests covered both implicit-count Transform components, counts 1/2/10 and all playable derivative identities.

Native localization IDs are also checked against their current operation opcode/variant before a registry template is reused. Old Expertise retains its Draw+Retain text and executable specification after the native card was reworked to Draw + whole-hand Retain.
