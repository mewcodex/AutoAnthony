# AutoAnthony component API

Status: API v1. Component catalogs, occurrence control, numeric parameter control, runtime execution, presentation
and external-character definition hosting are public, localization-independent interfaces. The public contract is
covered by an external-consumer compile test; built-in generation remains covered by the full generator self-test
and a historical full-pool drift corpus.

## Goals

- Components describe semantics with ASCII IDs and `OperationRuntimeSpec`; localized prose is output only.
- Occurrence probability and effect value are separate systems.
- A source card pool is both the component inventory and the default occurrence-frequency dataset.
- Normal pools use their own native dataset. Ultimate Chaos uses the combined six-pool catalog, which produces a
  pool-size-weighted average without a hand-authored seventh table.
- External characters can provide the same inputs without editing the built-in generator. Their own mod remains
  responsible for character/card-pool registration and for deciding when a run creates/restores its definitions.

## Current component contract

`ComponentAtom` is the authoring-time component. Its stable fields are:

| Field | Meaning |
| --- | --- |
| `Template` | Non-empty ASCII semantic identifier. It is not localized text. |
| `Scope` | Targeting/trigger/modifier role used by assembly legality. |
| `ChineseText` | Authoring projection and legacy migration aid; never the intended execution source. |
| `RequiresSingleTarget` | Whether the completed card must select one enemy. |
| `CardReference` | Required card-selection slot, if any. |
| `RuntimeSpec` | Opcode, variant, target, zones, flags, named value slots, condition and trigger. |
| `Multiplicity` | Computed component occurrence scope. |

Every executable component must also provide English rendering, named numeric slots, upgrade semantics and an
executor route for its `Opcode`/`Variant`. Any new ID, flag, slot or variant must remain ASCII.

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

Every resolved package passes `ComponentProfileValidator` before it is cached. The validator rejects empty or
cross-character catalogs, missing/non-ASCII semantic IDs, invalid RuntimeSpecs, duplicate semantic IDs, broken
trigger-owner indices and shell components that the selectable catalog cannot supply. Multiplicity overrides freeze
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
3. optional custom referenced-card/named-mechanic tips through `ComponentPresentationApi.Register`;
4. one `ExternalComponentCharacterRegistration`, whose profile ID, balance archetype and energy-icon prefix are
   stable across versions.

The external mod declares fixed slot card classes derived from `ExternalChaosCardModel` and overrides only
`ComponentProfileId`, `Slot` and `Pool`. At new-run/restore time it generates or deserializes complete
`ChaosCardDefinition` records and calls `ExternalComponentCharacterApi.InstallDefinitions`. Definition slots must be
contiguous, every operation must have a validated RuntimeSpec, and every card must use the registered balance
archetype. `ClearDefinitions` is run-scoped cleanup.

AutoAnthony carries the external profile ID into `ChaosCompositePower` saved properties and multiplayer checksums,
so delayed/continuous effects resolve the same external definition after save/load or reconnect. The owning mod must
still serialize and authoritatively synchronize its definition list; localized text must never be re-parsed on a
client. See `examples/WatcherComponentAdapter.cs.txt` for the minimal shape.

## Runtime handler API

The game assembly exposes `ComponentRuntimeApi.Register(opcode, variant, handler)` for structured operations whose
runtime implementation is supplied by another mod. Exact opcode+variant routes win over an opcode-wide route whose
variant is empty. Duplicate registrations are rejected, IDs must be ASCII, and registration freezes when the first
generated operation executes.

`RegisterPackage` validates a package's complete route set and commits it atomically. `HasRoute`,
`RegisteredRoutes`, `RegistrationsFrozen` and the API version are available for startup compatibility audits.

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

## Compatibility reference: The Watcher 0.9.25

The subscribed Watcher character demonstrates the common framework-free v111 pattern: a direct `CharacterModel`, a
direct `CardPoolModel`, concrete `CardModel` subclasses discovered by ModelDb, `ModHelper.AddModelToPool` for token
cards, and targeted Harmony compatibility patches. It also extends multiplayer ModelId serialization itself. The API
therefore depends on no BaseLib/RitsuLib character abstraction and accepts stable IDs plus normal model types rather
than framework objects. A Watcher integration can retain its existing model and serialization architecture; it only
adds the component package, fixed generated slots and authoritative definition snapshot described above.

## Compatibility rules

- Snapshot serialization stores runtime specs and operation IDs, not an interpretation of localized text.
- Removing or changing a component must preserve a migration route for supported current-run snapshots.
- Adding enum values must append them; serialized numeric enum values must not be reordered.
- API consumers must not assume a generated card is accepted merely because a component was sampled.
- Balance calibration uses numeric-balanced mode unless a test explicitly targets aggressive mode.
