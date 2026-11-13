# Fool integration (v0.113.0)

Requested scope: add Fool to the same reviewed/offline component, generation, execution, persistence and editor
paths as the other built-in characters. Multiplayer-only native card recipes remain outside random generation.
Do not modify the separately developed CardTinkering project.

## Work sequence

1. Inventory every Fool card, derivative, power, melody tag and arrow hook against native v113 sources.
   Review conditions/repetitions manually; reuse shared components instead of whole-card proxies.
2. Author the Fool component catalog, structured runtime contracts, exact native bindings/upgrades and manual
   bilingual name parts. Melody and Shoot tags must be explicit gameplay metadata. Generated melody names
   must match their tags, including pure-melody names, without duplicating native or run-local names.
3. Add the pool/profile/host models, art sources, generation distributions, resource/trigger valuations and legal
   combinations. Append enum values to preserve existing save identities. Include Fool in Ultimate Chaos.
4. Execute arrow, melody, Aim, Stretch, Sadness/Joy, replay and Tarot effects through native commands and
   shared component execution. Split independent triggers/rewards; retain indivisible arrow-specific payloads.
5. Cover keywords, dynamic previews, hovertips, native decomposition, ancient relics, save/load, multiplayer
   preparation, settings, editor APIs and original-pool mode. Preserve old six-pool snapshots.
6. Run reconstruction/upgrades, generation legality/distributions, runtime interaction and actual isolated-engine
   loading tests. Recheck existing characters. Bump version, prepend notes, deploy DLL/JSON/PCK to game and
   uploader/content. Do not publish unless requested.

## Progress

- Native sources/localizations are available under `proj/113` and `export/113`.
- Initial 90-card source inventory: `audits/fool-native-inventory.json` (local audit artifact).
- Reviewed authoring now contains 84 single-player recipes, 60 Fool-specific component contracts and 194
  component occurrences. Shared operations are reused rather than copied under Fool IDs.
- Added the Fool pool/model scaffold, explicit melody/Shoot tags and reviewed bilingual naming fragments.
- Verified the common permanent-reward restrictions across all seven catalogs, including The Hunt and split
  permanent Aim growth. A recurring ancestor cannot bypass the restriction through a nested one-shot gate.
- Implemented shared generation, native arrow/melody/Aim/Stretch commands, derivative/status slots, actual
  exhaust-result dependencies, nested conditions, lifecycle returns, permanent Aim state and presentation.
- Added 91 generated Fool slots (10 Basic, 20 Common, 35 Uncommon, 24 Rare, 2 Ancient); seven pools total 606
  generated slots. Appended enum/tag values preserve older serialized identities.
- Reconstructed all 84 Fool native recipes including upgrades; the full reference catalog now contains 659
  records (566 executable recipes). Exclusive Shoot generation intentionally excludes multi-sentence native
  Shoot continuations, while native decomposition still represents them.
- Balanced-model occurrence audit covers 16 Fool pools; native valuation audit covers all 566 recipes. Pricing
  includes starting Aim for Arrow hits and uses native percentage/trigger/resource rules rather than fixed
  whole-card proxies. Source-card residuals are diagnostics, not a claim that every original card is equally strong.
- Engine evidence: `audits/fool-combat-engine-10.log` and `audits/fool-ultimate-combat-final.log` exercise native
  battle commands, normal Fool and Ultimate Regent, Aim, Arrow spending, returns, Exhaust returns, Sadness,
  Joy, The Shot, Melody uniqueness, repeated Block, Stretch refunds, Tune nesting, permanent Aim and X=0/3.
- `audits/fool-runload-final.log` covers all six characters plus Regent/Fool Ultimate. The subsequent growth
  save/load and fresh-process tests preserve pool/deck hashes, upgrades, formatted text and saved ExtraAim.
- Ancient relic binding/save/packet regressions include Fool. Multiplayer bootstrap, authoritative snapshot
  transfer, retry/cancellation and disconnect protocol regressions pass. No real second-device session was run.
- Release target: 0.3.161, DLL/JSON/PCK only, normal game and uploader/content. Workshop publication remains a
  separate user action. Test plugins must never be deployed to the user's game directory.

## Release verification

- Version 0.3.161 built with zero warnings/errors; structured catalog, RuntimeSpec, execution-route and public API
  contract checks passed. Final generator regression and complete generator self-test passed.
- Native rendering probe: 1,318 base/upgraded instances across both languages, 2,556 descriptions, zero failures.
- The final cross-role runtime probe (`audits/fool-ultimate-combat-release.log`) also verifies multiple random
  effective-pool Shoot cards and native Tarot cards. Shoot fallback uses the character's effective pool, preserving
  the generated/original card inclusion setting.
- Final runtime probes completed successfully; all disposable test processes exited and automatic test manifests
  were disabled. The normal game/user saves were not used by the fixtures.
- Deployed exactly `AutoAnthony.dll`, `AutoAnthony.json`, `AutoAnthony.pck` to the normal game mod directory and
  `ModUploader/AutoAnthony/content`. All three destinations match by SHA-256; see
  `audits/fool-03161-deployment.json`. No PDB was deployed.
- The previous deployed 0.3.160 package is backed up under
  `G:/sts2modding/backups/AutoAnthony-before-Fool-0.3.161-20261112`.
- The uploader's `workshop.json` contains only the new 0.3.161 change note. No Workshop upload was executed.

## Shared permanent-reward policy

- Any reward that survives the battle (healing, maximum HP, gold, potions, extra card rewards or permanent
  card growth) must set `ComponentSemanticFlags.Restricted` (`api_restricted`) and use the shared policy.
- A non-Power must have Exhaust; restricted components are valid Power foundations. Upgrade generation must
  not remove their required Exhaust keyword.
- One-shot gates are permitted; recurring triggers and count multipliers are not. Validate the entire trigger
  ancestry, not only the immediate owner. Restricted effects must not coexist with self-copy loops.
- The Fool's Show Off is Aim + a turn-limited, next-non-Minion-death gate + permanent Aim growth. The gate is
  one-shot with an estimated 0.10 success frequency; it must not become a persistent death-listening Power.
- Exact native recipes remain an audit source even when a generated-card combination is intentionally narrower
  (for example the exclusive Shoot modifier requested for generated cards).

## Name invariants

- Wah/Cho/Ki are tags, not inferred from localized title strings at runtime.
- Melody spelling and native title styling must agree with tags in both languages.
- Pure melody titles are allowed, but native titles and exact run-local duplicates are forbidden.
- Preserve existing Strike/Form/Hang naming rules and Ancient portrait boundaries.
