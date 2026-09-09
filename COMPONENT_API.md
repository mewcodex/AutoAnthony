# AutoAnthony component API

Status: component API v3 / editor catalog API v6. Component catalogs, occurrence control, numeric parameter control, runtime execution, presentation
and external-character definition hosting are public, localization-independent interfaces. The public contract is
covered by an external-consumer compile test; built-in generation remains covered by the full generator self-test
and a historical full-pool drift corpus.

## Native-card decomposition catalog

`NativeCardDecompositionApi` is a separate, read-only API for Card Tinkering-style tools. It exposes all 567 v111
non-multiplayer reference records, 449 component contracts, and seven native keywords. The card set includes the
five character pools, both Colorless rarities, Event cards, Statuses, Curses, Quests, derivatives, Fasten,
DeprecatedCard, and nine explicit Mad Science type/rider variants.

The catalog never registers cards and every record remains `ReferenceOnly=true` and
`GenerationEligible=false`. `FindByNativeId` returns all variants for a ModelId; `Resolve` requires explicit
template bindings when the ID is ambiguous. `TryCreateBaseDefinition` materializes the 481 reviewed
character/Colorless recipes with executable structured operations. `TryCreateDefinition` materializes all 481 with
their exact native upgrade actions, including repeat, on-play addition, select-all, and upgrade-before-play changes.
The 86 lifecycle-sensitive reference extensions remain structured but non-executable until dedicated adapters exist.

The game-side `AutoAnthonyNativeCardApi.TryResolve(CardModel, ...)` handles native ModelIds and Mad Science's saved
type/rider state. Its preview method is side-effect free. Consumers must not replace an original card merely to
inspect it: only a user-confirmed edit should materialize a freeform `ChaosCardModel`, leaving every untouched
native card and native pool unchanged.

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
| `Category` | Optional localization-independent editor category; `Automatic` derives it from RuntimeSpec. |
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

### Trigger composition and atomic boundaries

Reviewed recipes use one shared authoring component for semantically identical actions. Character-prefixed legacy
aliases are accepted only while hydrating old snapshots. A build-time catalog invariant rejects those aliases if
they reappear in new reviewed recipes, and rejects a shared trigger that has no linked payoff.

The common delayed/event composition surface is:

| Trigger component | Runtime trigger | Pricing cadence |
| --- | --- | --- |
| `C:NextTurnStart` | `next_turn_start` | `0.50` resolution |
| `C:NextTurnsStart` | `next_turns_start` | `duration * 0.50` resolutions |
| `C:untilTurnEndCardPlayed` | `card_played`, armed for this turn | `3.20` resolutions |

The linked payoff is an ordinary component such as `N:B`, `N:Draw`, `N:E`, `R:GainStars`, `T:LoseHp`, Poison,
Doom, Summon, or an Orb action. It keeps exactly the same executor route and per-unit value as its immediate form;
only the trigger cadence changes its total value. Resource refunds use the same delayed cadence instead of a
separate Energy-only discount.

Card-bearing triggers form a second generic composition surface. A payoff whose RuntimeSpec requires
`requires_referenced_card_payload` may follow any trigger that structurally supplies a card (played, drawn,
generated, exhausted, iterated, or the host card observed in one of its own pile-state triggers). It may also consume
a card emitted by an earlier card-moving provider in the same trigger. For example, Juggling's
`create_copy/referenced_card` payoff is not Attack-specific: its native trigger supplies an Attack, while the same
payoff may legally follow `turn_end_if_self_in_exhaust` and copy the host card into Hand. The shared contract also
supports generic actions such as upgrading that referenced card. Damage modifiers, random-enemy autoplay and other
actions that actually require an Attack retain their filtered payload contracts.
Older `create_copy/referenced_attack` snapshots remain executable but are not emitted by the reviewed catalog.

Shared immediate authoring IDs now also cover permanent Strength (`N:Self`), permanent Dexterity (`N:Dex`),
temporary enemy Strength loss (`T:TempStrengthLoss`), retain-hand-this-turn (`N:RetainHandThisTurn`), a random
Colorless card (`N:AddRandomColorlessToHand`), selected discard-to-hand movement (`N:MoveDiscardCardToHand`),
self-copy-to-discard (`N:Create`), and static extra hits (`M:repeat`). Character-prefixed spellings remain readable
only as snapshot aliases. The balance self-test compares representative aliases against their shared replacement,
so an execution merge cannot silently leave two prices behind.

An effect remains atomic when splitting would discard runtime payload or change when state is captured. Current
intentional examples are Nightmare (the selected card must survive until next turn), Prolong (current Block is
snapshotted now), Wraith/Shadow-style next-turn rule powers, timed debuff-rule powers, and delayed player-choice
transactions. Nested delayed effects inside an already firing Power also remain atomic until the composite runtime
can arm child triggers dynamically. These are transactions/state objects, not hidden reusable trigger+payoff pairs.
Target-Vulnerable-to-Strength also remains atomic: high Vulnerable stacks have diminishing practical Strength
value, so treating it as a freely exchangeable linear `for each stack` trigger would misprice both halves.

Targeted Poison is the reusable `T:Poison` component in both ordinary and triggered contexts. On a targeted card it
uses the selected enemy; below a compatible enemy-event trigger it uses that event's enemy. Do not create a second
“apply Poison to that enemy” component. The same rule applies to an external payoff whose RuntimeSpec explicitly
declares event-target compatibility.

Built-in legacy Template aliases remain accepted only for supported run/history snapshots. New packages should
match components by structured opcode/variant/target/slot schema and use the current shared authoring Template rather
than copying a character-prefixed alias.

`Template` is a stable authoring/compatibility ID; semantic identity for sharing, execution and Card Tinkering is
the structured `RuntimeSpec` schema key. Two source occurrences with the same schema share the implementation while
retaining separate native occurrence and numeric evidence. Different targets, event-payload requirements, source
zones or lifetimes intentionally produce different schema keys even when their rendered verbs look alike.

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
5. optionally, an `ExternalComponentCharacterRuntimeRegistration` with its fixed slot types, active-run predicate,
   and generated pool.

The external mod declares fixed slot card classes derived from `ExternalChaosCardModel` and overrides only
`ComponentProfileId`, `Slot` and `Pool`. At new-run/restore time it generates or deserializes complete
`ChaosCardDefinition` records and calls `ExternalComponentCharacterApi.InstallDefinitions`. Definition slots must be
contiguous, every operation must have a validated RuntimeSpec, and every card must use the registered balance
archetype. `ClearDefinitions` is run-scoped cleanup.

The optional runtime registration lets the core reconstruct an external concrete card when a persistent Power
fires and adds active external pools to cross-pool events. `ComponentTriggerApi` is the supported entry point for
character-specific combat hooks; `ComponentRunSettingsApi`, `ComponentGenerationProgressApi`,
`ComponentSurpriseApi`, and `CardNameGenerator.RegisterExternalParts` replace the corresponding private-reflection
bridges used by early adapters. The original API-v2 character registration constructor and internal compatibility
targets remain intact for precompiled adapters.

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

## Card-editor and settings services

`CardTinkeringApi` is the localization-independent integration surface for companion editors. Its API version is
`6`. It serializes the complete structured card payload, evaluates the production whole-card budget, validates a
replacement operation list, and rebuilds descriptions/upgrades without parsing rendered prose. Values use the
generator's native currency: `100` units equal one point of ordinary single-target damage.

Profile-aware editors should call `GetComponentPrototypes(ComponentProfileRequest)` (or the stable-profile-ID
overload) and `GetKeywordPrototypes`. These enumerate the resolved external profile rather than only the six
built-in catalogs, preserve ownership as `ProfileId`, and expose profile-local base/add/remove keyword permissions.
`ComponentCategory` is explicit component metadata with a RuntimeSpec-only fallback; an editor never needs to
classify components by Chinese or English template text. `ComponentApi.TryGetProfileRequest` resolves built-in,
registered external, and derived Ultimate-Chaos requests without scraping internal registries.

Prefer `EvaluateBudget` when an editor needs the exact production inequality. It exposes positive value, linear
downside compensation, multiplicative downside capacity, net value, and the ordinary shell upper bound.
`EvaluateComponent` is intentionally context-free: moving a component does not preserve a discount inherited from
its old trigger. Call `Validate` before `Rebuild`; a failed result is a user-facing invalid composition, not an
operation that should be installed and repaired at runtime.

`SerializeCard`/`DeserializeCard` preserve RuntimeSpecs, named localization and upgrade value-slot identities. They
are suitable for an editor's own persistence and multiplayer payload, but AutoAnthony does not synchronize that
payload for the editor.

`AutoAnthonySettingsApi` v4 exposes one immutable snapshot of every user-facing setting, including the independent
`AddGeneratedCards` pool switch and the immediate `DecomposeOriginalCards` presentation switch. The latter keeps
native execution intact while projecting untouched original-card descriptions from the structured component catalog.
`ComponentRunSettingsApi` v2 carries the pool switch separately from the master
`Enabled` flag, so disabling random-pool insertion does not disable component execution or native-card editing. It deliberately does not
expose mutation; effective run and host-authoritative multiplayer settings remain the responsibility of
`ComponentRunSettingsApi`.

### Explicit per-card identity editing

`AutoAnthonyEditorApi` v2 is the only supported path for renaming an ordinary generated card or assigning a new
portrait to that individual card. `RerollEditorIdentity` is side-effect free and deterministic for a fixed
`GeneratedCard` and seed. It reuses production component relevance, cost/type/color matching, same-character name
parts, the Strike/Form suffix rules, enabled portrait replacements, and the Random Card Art setting. Ordinary cards
never receive Ancient-sized art; Ancient cards use only their own character's native Ancient sources and fall back
to the native portrait when no optional replacement is available.

```csharp
AutoAnthonyEditorIdentity identity =
    AutoAnthonyEditorApi.RerollEditorIdentity(card.Generated, seed);
GeneratedCard rebuilt = CardTinkeringApi.Rebuild(card.Generated, proposedOperations);
AutoAnthonyEditorApi.ApplyEditorDefinition(card, rebuilt, identity);
```

`ApplyEditorDefinition` treats `identity.Name` as authoritative, but still rejects changes to cost, Star cost, type,
target, rarity, character, tags, and shell-owned upgrade behavior. The edited definition and exact portrait source /
variant are saved on the card instance. Missing cosmetic providers on another machine render the stable native
fallback. Calling `ApplyTinkeredDefinition` directly continues to reject name changes and does not read a portrait
override, so merely installing AutoAnthony without an editor preserves its existing behavior.

External profiles register `IExternalEditorIdentityProvider` and use the ProfileId overloads. The provider owns
its name corpus, portrait sources, Ancient-size boundary and cosmetic fallback validation; AutoAnthony persists the
chosen identity and enforces the live card's profile and immutable shell. `AutoAnthonyFreeformCardApi` v2 likewise
accepts ProfileId for preview, deck and combat creation and obtains the concrete host from the external runtime
registration. `ExternalComponentCharacterApi.TryGetProfileId` and `TryGetActiveProfile` expose profile ownership
without relying on a borrowed `GeneratedCharacter` archetype.

External editor support is opt-in and independent from external-character generation. Registering
`ComponentPackageRegistration`, `ExternalComponentCharacterRegistration`, and its runtime host grants **no** editor
capabilities. An external character which only wants AutoAnthony random cards stops there. A character which also
wants editor support calls `AutoAnthonyEditorApi.RegisterExternalCapabilities`; editor consumers must check
`AutoAnthonyEditorApi.Supports` before presenting their UI. Registering an identity provider opts into identity and
generated-card editing, while registering a native-card adapter opts into native decomposition/freeform hosting.
Consequently neither AutoAnthony nor an editor silently treats every external Profile as editable.

For exact decomposition of an external character's native cards, register `IExternalNativeCardAdapter` through
`AutoAnthonyNativeCardApi` v3. It identifies the owning native cards, returns a complete structured `GeneratedCard`,
and copies character-specific per-instance state after materialization. `TryCreateDefinition`,
`TryCreateProfilePreview`, and the component-description method then work for built-in and registered external
cards. Untouched native cards remain native, and the adapter—not AutoAnthony—remains authoritative for mechanics
that cannot be represented in the common component state.
