# v0.113.0 native-card migration

Scope: the five previously supported characters, Colorless, and shared special cards. The new Fool character
and its dedicated derivatives remain outside the reviewed component catalog. Runtime compatibility was
published first as 0.3.158; the card-data corrections below belong to 0.3.159.

Sources: installed `sts2.dll` (v0.113.0, commit `16ac0fe3`), decompiled card implementations, and the English and
Simplified-Chinese localization JSON extracted from the installed PCK. No multiplayer card localization was
used as a wording reference. Untouched native cards still execute their native programs when component
description rendering is enabled; conversion APIs additionally use the updated executable recipes.

## Reviewed gameplay changes

| Card | Previous catalog | v0.113.0 projection |
| --- | --- | --- |
| Expect a Fight / 跃跃欲试 | 15 Block plus 5 Block per Strength | 15 (20) Block; current hand's Attacks cost 1 less Energy until played |
| Expertise / 独门技术 | Draw 2 (3); those cards Retain this turn | Draw 2 (3); Retain the entire hand this turn |
| Memento Mori / 铭记死亡 | 1 Energy; 9 damage plus damage per discard | 3 Energy, Sly; 19 (23) damage to the lowest-HP enemy |
| Forgotten Ritual / 被遗忘的仪式 | Unconditional Energy | Gain 3 (4) Energy only if a card was Exhausted this turn |
| Phantom Blades / 幻影之刃 | First-Shiv bonus 9 (12) | First-Shiv bonus 8 (11) |
| Salvo / 箭雨 | 12 (16) damage | 10 (14) damage |
| Minion Dive Bomb / 仆从俯冲 | 13 (16) damage | 11 (13) damage |
| Enlightenment / 启蒙 | Exhaust | No Exhaust keyword |
| Dowsing / 探寻 | Five unknown rooms | Four; no unknown-room combats when only one room remains |
| My Shinies / 我的小钱钱 | New shared card | For every 20 (10) Gold, deal 1 damage; separate reusable count prefix and damage |

Shared event additions: Dingy Dagger, Mighty Weapon, Mighty Shield, and God Byrd. Sadness is recorded as a
reference-only status. The reference catalog includes the Dagger's upgrade-level-dependent damage formula.
These special reference entries do not become generator components or replace event/quest lifecycle code.

## Variable and execution changes

- Bind native multi-hit upgrades to `HitCount`, preserving the separate `Repeat` variable used for other
  repetition (for example Refract's Glass generation and Bouncing Flask's Poison applications).
- Preserve `CalculatedHits` identities after their native type changes to `CalculatedHitCountVar`.
- Include native damage-source identity when generated effects kill Osty or enemies.
- Match Omnislice's capped damage propagation instead of applying damage modifiers again to copied damage.
- Native upgrade auditing now verifies applied numeric slots against their source variable bindings, including
  transformed values such as `HitCount - 1`, instead of checking only that an upgrade plan can be constructed.
- Explicit native bindings preserve legitimate marker upgrades such as FTL's play-count gate 3 to 4. Random
  generation retains its marker-upgrade restrictions.

## Catalog totals and persistence

- 482 generator recipes, 934 component occurrences.
- 573 native reference records: 482 executable recipes with exact upgrades, 91 special references.
- 455 reference component definitions and seven native keyword definitions.
- Colorless gains one Rare slot at the end of its existing slots. Existing slot identities and rarity positions
  remain unchanged. Older complete snapshots use selective recovery for the added slot; existing saved card
  definitions retain their persisted operation specifications.

## Verification

`--native-decomposition-audit` checks reconstruction, upgrade application and bilingual templates.
`tests/NativeCardVersionRegression` loads real native cards inside an isolated Godot process, checks base and
first-upgrade costs, variables and keywords, then formats both languages with component descriptions enabled.
All nine Mad Science variants receive their explicit native type/rider state.
Library activation tests generate the complete six pools with BaseLib/RitsuLib installed. These checks do not
replace a real two-computer multiplayer session or simulate every native combat program.

Observed results (2026-11-12): 1,146 language/card cases and 2,212 base/upgrade descriptions passed inside the
actual v0.113.0 engine. All 482 exact-upgrade reconstruction checks passed. Six-pool generation and the
52-to-53 Colorless snapshot recovery passed with BaseLib 3.4.8 and RitsuLib 0.6.11. Recovery retained all 514
prior definitions, including both removed-from-generation components, and supplied only the one new slot.
Upgrade lifecycle, multiplayer-preparation and delayed-trigger regressions also passed. Tests used isolated
profiles/fixtures; no existing user run was loaded or changed.

## Follow-up runtime audit (0.3.160)

The native 111/113 behavior comparison is retained locally as `audits/v113-core-api-behavior.diff` and
`audits/v113-cardmodel.diff`. No new-character behavior is implemented by this audit.

- v113 records keywords added/removed outside a card upgrade. Replaying a generated upgrade while rebuilding
  its caches previously classified upgrade-only Innate as an external addition. The replay now preserves the
  native upgrade scope and effective external keyword changes. The pre-fix regression failed on this exact case.
- Native downgrade clones the canonical slot's variables/cost. Generated per-instance definitions now rebuild
  their own base state in `AfterDowngraded`, before native enchantment/affliction reapplication. This prevents
  edited or rebalanced cards from inheriting unrelated canonical-slot values.
- Native combat selection can be cancelled by owner death or combat termination. Interpreter-owned choices
  now capture the owner combat and the new native `CombatId`, and discard stale asynchronous results before
  completing mandatory selections. Automated pile selectors follow native v113 card ordering and validate the
  live pile after waiting. Generated candidate choices use the same combat lifetime boundary.
- Direct portrait replacements refresh the native cached atlas UV/blur parameters through `SetPortraitTexture`,
  only when the final texture/presentation changes.

Verification: the real isolated v113 engine passed 1,146 bilingual native-card cases and 2,212 descriptions,
generated upgrade/downgrade/keyword checks, and both normal/Ancient hidden-portrait UV checks. Console
regressions passed for cancelled/live hand and discard choices, upgrades, delayed triggers, multiplayer pool
preparation, and 112 Tea of Discourtesy/companion-relic scenarios plus four entry restores. All AutoAnthony
Harmony patches installed with the actual local BaseLib/RitsuLib assemblies. These checks do not constitute
a two-computer multiplayer session. No existing user saves were loaded or modified.

Additional 0.3.160 loading smoke test: all five supported characters started, saved, returned to menu and resumed
through the native Continue action. One starter was upgraded before each save. All six pool-definition and deck
fingerprints remained identical, including names, art, effects, upgrades, costs, keywords and descriptions. Regent
covered Ultimate Chaos. A fresh process using the normal renderer then continued that Regent save from disk;
its pool/deck fingerprints also matched. Both test processes exited and the test manifest was disabled. The
production DLL hash matched the uploaded package. See `tests/RunLoadRegression` and local
`audits/v113-0160-load*` / `audits/v113-0160-fresh-load*` logs. Only the isolated test profile was used.
