# AutoAnthony component API

Status: API v3. Component catalogs, occurrence control, numeric parameter control, runtime execution, presentation
and external-character definition hosting are public, localization-independent interfaces. The public contract is
covered by an external-consumer compile test; built-in generation remains covered by the full generator self-test
and a historical full-pool drift corpus.

## Goals

- Components describe semantics with ASCII IDs and `OperationRuntimeSpec`; localized prose is output only.
- Occurrence probability and effect value are separate systems.
- A source card pool is both the component inventory and the default occurrence-frequency dataset.
- Normal pools use their own native dataset. Ultimate Chaos uses the combined built-in and registered external
  catalogs, producing a pool-size-weighted average without a hand-authored extra table.
- External characters can provide the same inputs without editing the built-in generator. Their own mod remains
  responsible for character/card-pool registration and for deciding when a run creates/restores its definitions.

## Current component contract

`ComponentAtom` is the authoring-time component. Its stable fields are:

| Field | Meaning |
| --- | --- |
| `Template` | Non-empty ASCII semantic identifier. It is not localized text. |
| `Scope` | Targeting/trigger/modifier role used by assembly legality. |
| `ChineseText` | Authoring projection and legacy migration aid; never the intended execution source. |
| `LocalizedText` | Chinese/English templates whose placeholders name `RuntimeSpec` value slots. |
| `RequiresSingleTarget` | Whether the completed card must select one enemy. |
| `CardReference` | Required card-selection slot, if any. |
| `RuntimeSpec` | Opcode, variant, target, zones, flags, named value slots, condition and trigger. |
| `Multiplicity` | Computed component occurrence scope. |

External Template names are never inspected to infer gameplay. Use the public `ComponentSemanticFlags` constants
when a custom opcode needs generator-level composition behavior:

| Flag | Meaning to the assembler |
| --- | --- |
| `Beneficial` | The operation can satisfy the card's required positive payoff. A registered non-negative valuation also implies this. |
| `Negative` | The operation is a downside and participates in compensation. A valuation with downside pricing also implies this. |
| `Restricted` | The operation must be on a Power or force Exhaust on a non-Power. |
| `EnemyDamage` | The operation is enemy damage and may classify an ungated non-Power card as an Attack. |
| `PowerFoundation` | The operation is sufficient persistent state for a Power card. |
| `ScalableReward` | Its numeric value may be scaled to the whole-card budget. |
| `SelfCardMovement` | It moves/replays the generated card itself and is rejected on Powers. |

Use flags only for these cross-cutting legality facts. Targets, card zones, values, conditions and triggers belong in
their dedicated `RuntimeSpec` fields, while actual execution and valuation belong in their registered routes.

Every executable component must also provide English rendering, named numeric slots, upgrade semantics and an
executor route for its `Opcode`/`Variant`. Any new ID, flag, slot or variant must remain ASCII.

### Ownership and reuse

Character ownership belongs to reviewed **source occurrences**, not to executor copies. Every operation occurrence
on a native card has its own role-prefixed `SemanticId` (for example `ironclad/anger/0`). Catalog indexes then group
numeric-value-independent equivalents by `SchemaKey`; equivalent atoms share one selectable structural schema and
one opcode/variant execution route. Their separate source occurrences remain in `AtomCounts`, so each normal profile
retains its own native frequency and numeric evidence. Ultimate Chaos retains occurrences from every contributed
catalog for weighted fitting, then deduplicates the structural inventory.

All five built-in characters and Colorless use this same path: reviewed structured Markdown -> materialized recipe
and RuntimeSpec JSON -> `StructuredComponentCatalogRegistry` -> `ImmutableComponentCatalog`. No built-in catalog is
decomposed from localized card prose at runtime. Run `--catalog-ownership-audit` in the standalone generator to
inspect recipe, occurrence, schema, and cross-role sharing counts.

## Occurrence model

For a requested rarity, card type and semantic role, `NativeComponentFrequencyTracker` computes a direct source
prior:

```text
native occurrences of family / relevant native cards
```

When an exact rarity/type/role cell is empty, the generator uses a small cross-type/cross-rarity fallback so every
legal original component remains reachable. Components with a replaceable derivative/status/orb slot receive a
single 1.10 occurrence multiplier. Numeric value fitting is not part of this prior.

A closed-loop tracker observes finalized cards and corrects legality/acceptance bias. Rejected speculative cards do
not affect frequency. Same-family effects on one card retain the separate 20% multiplicative repeat penalty.

## Generation profile API

`ComponentAssemblyGenerator` no longer selects global character catalogs or constructs a hard-coded frequency
tracker. `ComponentApi.Resolve(ComponentProfileRequest)` supplies one immutable `ComponentGenerationProfile`:

| Profile member | Owner |
| --- | --- |
| `ShellCatalog` | source shells, rarity/type/target distributions and tag/effect-count priors |
| `ComponentCatalog` | selectable component implementation inventory |
| `NameCatalog` | same-character name corpus; separate so Ultimate Chaos does not mix names |
| `IComponentOccurrencePolicy` | family/variant source priors plus finalized-pool feedback |
| `IComponentValuePolicy` | scalar sampling, trigger payoff scaling and semantic bounds |

Custom opcodes also register `IComponentValuation` by opcode/variant. It prices the finalized structured value slots
in hundredths of one point of single-target damage; optional linear downside value or a direct whole-card downside
multiplier enters the same negative-compensation pipeline as built-in effects. This is independent of both occurrence
weight and numeric sampling. RuntimeSpec flags `api_scalable_reward` and `api_power_foundation` expose the two
remaining assembly capabilities without matching localized prose.

An occurrence policy instance is pool-scoped and mutable; the profile itself and all catalogs are read-only. The
factory is invoked once per generated pool, not in the component-selection hot loop. Failed card attempts are not
observed. Numeric policy calls never contribute component-selection weight.

The assembler asks the occurrence policy separately for a family prior and a within-family variant prior. The
built-in policy owns all rarity/type/target/trigger-role occurrence indexes; the assembler no longer reconstructs
native component tables or knows their smoothing formula. Card-shell/tag distributions remain properties of
`ShellCatalog`, since they choose the card frame rather than an effect component.

Providers can be registered during mod initialization. A direct immutable registration is preferred:

```csharp
ComponentPackageApi.Register(new ComponentPackageRegistration(
    "my_mod:watcher_components",
    new ComponentProfileRequest("my_mod:watcher", GeneratedCharacter.Regent, false),
    profile,
    localizedTexts,
    keywordUpgrades,
    multiplicities));
```

Providers are queried in reverse registration order, allowing a reviewed extension to override a built-in
character/profile. Registration freezes on the first `Resolve`, so catalogs cannot change halfway through a run or
between multiplayer peers. Provider IDs and all semantic/runtime identifiers must be ASCII and stable across save
versions. `ComponentProfileRequest.ProfileId` is the external identity. `Character` is explicitly only the closest
built-in balance/legality archetype, so any number of external profiles may reuse it without colliding. The older
two-argument constructor still resolves the six built-in profiles.

`ImmutableComponentCatalog` builds all structural indexes from a reviewed native recipe list. External profiles can
reuse `ComponentApi.CreateNativeOccurrencePolicy` and `ComponentApi.DefaultValuePolicy`, or provide either policy
themselves. This keeps component probability independent from value fitting while avoiding copies of internal
index-building code.

`ComponentApi.ComposeCatalog` exposes the same recipe-preserving union used by Ultimate Chaos. An external profile
can select any subset of public built-in catalogs, add its own reviewed recipes, and use the result as its component
inventory. `ComponentPackageRegistration.IncludeInUltimateChaos` defaults to true. Each normal external package
then contributes its complete native recipe dataset to Ultimate Chaos; repeated source occurrences remain present
and supply the weighting, while structural atom indexes are deduplicated. Requesting the external profile ID with
`UnlockComponentRoles=true` derives its Ultimate profile automatically when only a normal profile was registered.

Native keyword permissions are profile-local through `ComponentKeywordPolicy`. The stable legacy `CardTag` wire
enum is explicitly partitioned: Exhaust/Innate/Retain/Sly/Ethereal/Eternal/Unplayable are native keywords, while
Strike/Defend/OstyAttack are semantic mechanic tags. Keyword policies reject the latter instead of silently treating
them as displayable keywords. A profile can independently limit
base keyword availability, legal upgrade additions/removals, add global upgrade candidates, and disable built-in
archetype defaults. Component-specific keyword upgrades are keyed by stable profile ID rather than by the borrowed
balance archetype, so two mod characters that both reuse Regent cannot leak upgrade rules into each other.

API v3 adds stable ASCII custom keyword IDs without extending either closed base-game enum. Register generator-side
metadata in `ComponentPackageRegistration.Keywords`, put the ID on the relevant source recipes through
`IroncladCardRecipe.CustomKeywords`, and register the game-side projection through
`ComponentKeywordRuntimeApi.RegisterPackage`. `IComponentKeywordRuntimeAdapter` can expose existing base-game
`CardKeyword` values, semantic `CardTag` values, hover tips, and an upgrade callback. The active ID set is derived
from the base card plus `AddedCustomKeywords`/`RemovedCustomKeywords`, so upgrades, saves and reconnects use the
same stable identity.

Base native-keyword allow-lists are enforced for both ordinary keywords and Sly's delayed final-cost branch. For
schema 5-9 live-save compatibility, runtime projection takes the union of structural upgrade effects and the legacy
`AddedKeywords`/`RemovedKeywords` arrays; new and old save layouts therefore produce the same upgraded keyword set.

A custom keyword is deliberately not a hidden effect container. Its executable behavior and balance value must be
represented by a structured operation in the same recipe, with an `IComponentRuntimeHandler` and
`IComponentValuation`. The keyword ID supplies classification/presentation and lets the owning mod attach native
hooks. This keeps card strength and behavior auditable even when the word shown to the player changes.

```csharp
var keyword = new ComponentKeywordDefinition("my_mod:stance_locked", new StanceKeywordRule());
var upgrade = new ComponentKeywordUpgrade(
    "my_mod:enter_stance", [], [], AddedCustomKeywords: [keyword.KeywordId]);

ComponentPackageApi.Register(new ComponentPackageRegistration(
    "my_mod:components", request, profile,
    KeywordUpgrades: [upgrade],
    Keywords: [keyword]));

ComponentKeywordRuntimeApi.RegisterPackage("my_mod:keyword_runtime",
[
    new ComponentKeywordRuntimeRegistration(keyword.KeywordId, new StanceKeywordAdapter())
]);
```

`ComponentKeywordPolicy` has separate allow-lists for base custom keywords, upgrade additions/removals and global
upgrade candidates. `IComponentKeywordRule` can reject a base attachment or an add/remove upgrade from the complete
structured card context. These checks are deterministic generator policy; runtime adapters must not make random
generation decisions.

Upgrade candidates are behavioral data only: `Kind`, `OperationIndex`, `ValueSlotId`, and `Delta`. New snapshots do
not write the old bilingual candidate labels. Those legacy JSON properties remain read-only migration sinks, and
the player-facing upgraded description is always rendered from the upgraded operation list.

New upgrade plans also express keyword changes only through `CardUpgradeEffect.Kind` and `KeywordId`. The old
`AddedKeywords`, `RemovedKeywords`, `AddedCustomKeywords` and `RemovedCustomKeywords` arrays remain constructor and
save-migration inputs for API v2/schema 5-9; schema 10 uses structural effects as the authoritative form, while
projection helpers merge both forms and new generation does not write
the fact twice.

`OperationLocalizedText` is a runtime presentation cache and is not serialized into every operation. Live and
history snapshots already preserve card text plus the authoritative RuntimeSpec; the load boundary reconstructs
named templates from those fields and falls back to the legacy renderer only when an older phrase is not
unambiguously compilable. This avoids multiplying snapshot size while template storage moves toward stable IDs.

New external packages can register stable templates with `ComponentLocalizationRegistration`. Its `SemanticId`
must match a component atom and both templates use named RuntimeSpec slots such as `[[damage]]`, `[[hits]]`,
`[[amount:cardinal]]`, `[[amount:ordinal]]`, `[[energy:energy]]` or `[[stars:stars]]`. The registration is validated
against the component RuntimeSpec and must reproduce the component's base Chinese projection. Generated operations
persist only the small localization ID; the compiled template itself remains a runtime cache. The older
`ComponentLocalizedText` exact-sentence registry remains available for API v2 source compatibility and legacy
snapshot rendering, but new packages should not use it.

Non-numeric names are explicit too. Add `OperationTextSlot` values to the registration and reference them with the
same placeholder syntax, for example `Create [[token]].` / `生成[[token]]。`. Derivative, status, enchantment and
Orb identities use this path internally; rebinding changes the slot value rather than replacing arbitrary words in
the rendered sentence.

Live game code calls `OperationRuntimeSpecCompiler.RequireStructured`; it cannot silently recompile a translated
sentence. `CompileLegacy` is intentionally limited to old-save hydration and startup/offline probes. A package with
a missing RuntimeSpec or named localization is rejected before its profile can generate cards.

Every resolved package passes `ComponentProfileValidator` before it is cached. The validator rejects empty or
cross-character catalogs, missing/non-ASCII semantic IDs, invalid RuntimeSpecs, duplicate semantic IDs, broken
or forward trigger-owner indices, missing named localization templates, unstructured shell atoms, unknown custom
keywords and shell components that the selectable catalog cannot supply. `ComponentPackageApi.Register` preflights
every generator-side registry before committing anything, so a conflict cannot leave a partially installed package.
Multiplicity overrides freeze
at the same boundary; register them before the first profile is resolved.

The built-in adapter maps normal pools to their native catalog and Ultimate Chaos to the same combined six-pool
catalog used before this boundary. A historical corpus covering every character, normal/Ultimate Chaos, balanced/
aggressive generation and complete pool repair is stored at
`audits/structured_operation_refactor/0.3.5_pre_component_api.json.gz` (SHA-256
`F722B24DC8C32108A6F6AC9378F059FBB10FE1E862DF2327A683E3D8C0C2E66A`). It is a diagnostic reference rather
than an assertion that later intentional balance changes preserve byte-identical output.

## Multiplicity

`ComponentMultiplicity` has three scopes:

- `Repeatable`: ordinary stackable effects; normal per-field limits still apply.
- `SinglePerCard`: existing “不可叠加” rule. The semantic key may appear at most once on one card.
- `UniquePerPool`: `unique`; after one finalized card consumes the key, it cannot appear on another card in that
  character/colorless pool.

Pool uniqueness is consumed only after the card passes validation, naming and duplicate-card checks. Failed
assemblies never consume it. `ComponentPolicy.TryAuditPool` validates both scopes on a completed pool.

Built-in pool-unique rules currently cover idempotent combat-long switches such as retaining the hand at turn end,
skills costing zero, derivative-wide retain/hit-all rules and Sovereign Blade hitting all enemies. Stateful counters
and numeric powers are deliberately not inferred as pool-unique.

External code may register metadata before generation:

```csharp
ComponentPolicy.RegisterMultiplicity(
    "my_mod:retain_hand_rule",
    ComponentMultiplicity.UniquePerPool,
    variant: "retain_hand_at_turn_end");
```

An exact template+variant registration wins over a template-wide registration.

## External character adapter

The owning character mod registers its normal models in the usual v111 way: concrete `CharacterModel`,
`CardPoolModel`, `CardModel` subclasses and any token-pool additions. AutoAnthony deliberately does not patch
`ModelDb.AllCharacters` or rewrite another mod's pool.

During that mod's initializer it registers:

1. a `ComponentPackageRegistration`, containing native recipes, component atoms, localized projections, keyword
   upgrades, multiplicity metadata and custom structured valuations;
2. custom opcode implementations through `ComponentRuntimeApi.RegisterPackage`;
3. optional custom referenced-card/named-mechanic tips through `ComponentPresentationApi.RegisterPackage`;
4. one `ExternalComponentCharacterRegistration`, whose profile ID, balance archetype and energy-icon prefix are
   stable across versions; it may also include an `IExternalAncientRelicAdapter` for Archaic Tooth and Dusty Tome.

The external mod declares fixed slot card classes derived from `ExternalChaosCardModel` and overrides only
`ComponentProfileId`, `Slot` and `Pool`. At new-run/restore time it generates or deserializes complete
`ChaosCardDefinition` records and calls `ExternalComponentCharacterApi.InstallDefinitions`. Definition slots must be
contiguous, every operation must have a validated RuntimeSpec, and every card must use the registered balance
archetype. `ClearDefinitions` is run-scoped cleanup.

AutoAnthony carries the external profile ID into `ChaosCompositePower` saved properties and multiplayer checksums,
so delayed/continuous effects resolve the same external definition after save/load or reconnect. The owning mod must
still serialize and authoritatively synchronize its definition list; localized text must never be re-parsed on a
client. See `examples/WatcherComponentAdapter.cs.txt` for the minimal shape.

The Ancient adapter identifies its own players, decides independently whether each relic is overridden, and returns
the two canonical Ancient card models. AutoAnthony then reuses its multiplayer-safe relic binding, hover-tip and
obtain flows. This avoids hard-coding an external `CharacterModel` ID in AutoAnthony.

## Runtime handler API

The game assembly exposes `ComponentRuntimeApi.Register(opcode, variant, handler)` for structured operations whose
runtime implementation is supplied by another mod. Exact opcode+variant routes win over an opcode-wide route whose
variant is empty. Duplicate registrations are rejected, IDs must be ASCII, and registration freezes when the first
generated operation executes.

`RegisterPackage` validates a package's complete route set and commits it atomically. `HasRoute`,
`RegisteredRoutes`, `RegistrationsFrozen` and the API version are available for startup compatibility audits.

Presentation and custom-keyword runtime adapters also have atomic package registration. Prefer the package methods
over registering routes individually so a duplicate route cannot leave half of an integration active.

Handlers receive `ComponentRuntimeContext`, not localized text. It contains the `GeneratorOperation`, validated
`OperationRuntimeSpec`, resolved amount after X/dependency scaling, resolved target, card play/choice context,
trigger event information and read-only selected-card slots. Controlled methods record damage/drawn cards, add a
card slot, or request the same deferred play-phase-safe end-turn path used internally.

Execution order is deliberately migration-safe:

1. built-in common structured handlers;
2. built-in structured damage handler;
3. registered external handler;
4. legacy character/template compatibility dispatcher.

An external handler may return `false` to continue into step 4. This permits old snapshots to retain their existing
template route while a new structured implementation is introduced. Custom derivatives/orbs/statuses are expressed
as structured custom opcodes and executed by their owner; their localized projection and keyword upgrade metadata
live in `ComponentPackageRegistration`, while referenced-card and named-mechanic tips use
`ComponentPresentationApi`. Built-in interchangeable derivative/orb slot catalogs remain intentionally closed
because their budget conversion and concrete ModelDb resolution are AutoAnthony-owned semantics.

## Audit boundary: what is and is not generic

The external route is generic; the built-in implementation is not yet data-only. Native mechanics still have
template/variant-specific implementations in `ChaosOperationExecutor`, `ChaosCardModel` and
`ChaosCompositePower`. They are compatibility adapters for native hooks and existing snapshots. A new namespaced
opcode does not require another branch there: it executes through `ComponentRuntimeApi`. External routes do not,
however, override a built-in common opcode because built-in structured execution intentionally runs first.

If bounded ordinary generation exhausts its retries, the emergency path selects a simple positive component from
the resolved profile's own catalog. It does not reference Ironclad RuntimeSpecs. A profile therefore needs at least
one standalone, upgradable positive component for every card type present in its shell catalog; a forced Special-X
quota additionally needs a fixed numeric Attack or Skill component that can be converted.

The game's `CardKeyword` and `CardTag` types are closed enums. API v3 therefore stores genuinely new keyword
identity as an ASCII string and projects it through `IComponentKeywordRuntimeAdapter`; it never manufactures an
enum member. The keyword's operation supplies its custom opcode, localized projection, valuation and runtime
handler. The owning character mod supplies any card hook that cannot be represented by an existing structured
trigger.

External definition storage is run-scoped but does not replace the owning mod's save schema. The character mod must
serialize its complete definition list and synchronize the host's list. AutoAnthony preserves the external profile
ID for delayed powers after those definitions have been installed.

Under the “Regent is external” acceptance test, an adapter can provide a completely external recipe/component
catalog, reuse or exclude any built-in catalog, define custom opcodes and semantic keywords, choose native keyword
and upgrade permissions, participate in weighted Ultimate Chaos, install generated card slots, and service the two
Ancient relics. It must still own ordinary character/pool/model registration, portraits, starting-deck wiring, its
save field and authoritative multiplayer transport. Custom token/orb/status resolution also remains with the owning
mod unless it deliberately uses one of AutoAnthony's closed built-in slot catalogs.

An integration is complete when its generator package, runtime routes, optional presentation routes and custom
keyword adapters all register before the first profile/card query; its definitions round-trip through the owning
mod's save and host-authoritative multiplayer payload; and every shell has a structured, localized, executable
fallback component. AutoAnthony can verify these contracts, but it cannot supply the owning mod's character models,
art, concrete generated-card classes or network field.

## Compatibility reference: The Watcher 0.9.25

The subscribed Watcher character demonstrates the common framework-free v111 pattern: a direct `CharacterModel`, a
direct `CardPoolModel`, concrete `CardModel` subclasses discovered by ModelDb, `ModHelper.AddModelToPool` for token
cards, and targeted Harmony compatibility patches. It also extends multiplayer ModelId serialization itself. The API
therefore depends on no BaseLib/RitsuLib character abstraction and accepts stable IDs plus normal model types rather
than framework objects. A Watcher integration can retain its existing model and serialization architecture; it only
adds the component package, fixed generated slots and authoritative definition snapshot described above.

## Compatibility rules

Schema 5 is the oldest live run that current builds resume in place. Schema 1-4 records remain readable by the
history viewer, but a still-active run from those pre-all-pool layouts regenerates its generated pools. Schema 5-9
live saves are migrated card-by-card; schema 10 is the current structured format.

- Snapshot serialization stores runtime specs and operation IDs, not an interpretation of localized text.
- Removing or changing a component must preserve a migration route for supported current-run snapshots.
- Adding enum values must append them; serialized numeric enum values must not be reordered.
- API consumers must not assume a generated card is accepted merely because a component was sampled.
- Balance calibration uses numeric-balanced mode unless a test explicitly targets aggressive mode.
