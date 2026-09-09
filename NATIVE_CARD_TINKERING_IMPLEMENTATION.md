# Native-card decomposition for Card Tinkering

## Goal

Expose every v111 non-multiplayer native card as localization-independent component data, then let Card Tinkering
edit an individual native card without replacing the game's card pools or converting untouched cards.

## Invariants

- Native cards remain native until the player changes and commits that exact card.
- The optional `Decompose Original Cards` display setting renders untouched native cards from the same component
  projection without replacing their native execution model.
- Opening or cancelling the editor never mutates the deck.
- Catalog identity and behavior use ASCII IDs and structured runtime specifications, never rendered text.
- Event, Quest, Curse, Status, Token, deprecated, Fasten, and all nine Mad Science variants remain in the catalog.
- Reference-only entries never enter Auto Anthony's random-generation pools.
- Unsupported lifecycle behavior is reported explicitly; it must not be silently approximated.
- Conversion is save-safe: a committed card becomes a serialized freeform `ChaosCardModel`.

## Delivery phases

1. **Runtime catalog API**
   - Embed the complete native reference catalog in the gameplay assembly.
   - Publish immutable card/component descriptors and native-card lookup.
   - Rebuild the 481 reviewed generator-catalog cards into executable `GeneratedCard` definitions.
   - Audit all 567 records and all nine Mad Science variants at startup/tests.
2. **Opt-in Card Tinkering adapter**
   - Add a disabled-by-default native-card editing option.
   - Import supported native cards as drafts without changing the deck.
   - On commit, replace only modified native cards with freeform generated cards.
   - Preserve upgrade level, floor-added metadata, enchantment, and affliction where supported.
3. **Special lifecycle components**
   - Implement the 86 reference-extension recipes (event/quest/status/curse/token/deprecated/Fasten).
   - Keep native lifecycle hooks intact until each structured component has an executable adapter.
   - Add selection, trigger, map/quest, and unplayable-card regression tests.
4. **Release validation**
   - Exact base/upgrade reconstruction audit for all native records.
   - Save/load, editor cancel/commit, history, and multiplayer isolation checks.
   - Document the public API and Card Tinkering integration contract.

## Status

- The complete schema-v2 reference catalog already contains 567 records from 559 native classes.
- Phase 1 runtime API is implemented: all 567 cards, 449 component definitions, and seven keywords are embedded as
  read-only data; all 481 reviewed ordinary recipes rebuild with their exact base component layout and upgrade
  behavior. Structural upgrades on Darkness, Spinner, Tesla Coil, Armaments, and Knife Trap use explicit upgrade
  actions instead of being approximated as scalar changes.
- Phase 2 is implemented behind Card Tinkering's disabled-by-default `Edit native cards` option. Supported native
  cards are imported as detached drafts; only a changed card is replaced, at the same deck index, by a serialized
  freeform `ChaosCardModel` when the player commits. A failed batch restores both native and generated cards.
- Current executable editing coverage is all 481 ordinary character/colorless cards. The 86 lifecycle-sensitive
  reference extensions stay visible through the API but read-only until their dedicated adapters are complete.
