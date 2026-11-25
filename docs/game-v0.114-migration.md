# STS2 v0.114.0 migration — AutoAnthony 0.3.170

Reviewed against the installed v0.114.0 assembly, commit `a7b9a9c0`.
SHA-256: `DDCEB87905B6096DEB7BE975922E24A53992453675DF26DF545B1C46B1826C5D`.
Card and power implementations were compared with v0.113.1; English and Simplified Chinese localization
was extracted from the installed PCK. The API versions and snapshot schema remain unchanged.

## Runtime migration

- The game removed `FreeCardPower`. `ChaosNextCardsFreePower` preserves the existing generated operation's
  owner/pile checks, zero-energy modifier and one-stack-per-card consumption. Its icon is bound to its source card.
- The native `TheShot` now has a regular Damage variable, costs zero, and deals 20 (25) damage. Saved count-scaled
  `F:CreateTheShot` operations materialize their older formula on that Damage variable, including their older
  derivative upgrade increment. They do not call the removed `BuffFromFlorescence` method.
- Six retired source operations remain accepted by run/history snapshot parsing. Their structured definitions
  remain available to the compiler. Source-card removal does not make their saved generated cards unknown.
- Native attack-context and Arrow-command changes are consumed through the public commands. Destroyed Arrow
  state was removed by the game; unavailable-arrow tests now use the remaining Away state.
- The established generated slot layout stays stable. Expanding a source catalog must not reassign existing
  saved generated card IDs or the two Ancient slots.

## Native card catalog

- Added Twin Shot and Optimism; Rapid Shot is no longer a Fool-pool source.
- Florescence selects hand cards and transforms them into the new The Shot, including upgraded references.
- Growth gains Block and transforms a random Sadness into a copy of its source card.
- Tune triggers on the first Ki Melody and upgrades random hand cards.
- Recall Dislodges all of the owner's Arrows from its selected enemy; its upgrade removes Exhaust.
- Harmonic Shield separates base Block from additional Block per Melody.
- Trick Shot's successful-shot draw, Punch of Joy's transformation count, Mythic Form's Ethereal removal,
  Quiver's Innate upgrade, the changed rarities/costs, and numeric/keyword upgrades follow v114 source data.
- Explicit native-variable bindings distinguish Harmonic Shield's two calculations and Vitriol's Weak/count
  values even when their printed numbers coincide.
- Read-only catalog: 660 native records, 515 component definitions, seven keyword definitions; 567 executable
  reviewed recipes and 93 structured reference extensions. There are 85 non-multiplayer Fool source cards.

## Verification

The v114 validation includes deterministic catalog exports, API contract compilation, every Fool native upgrade,
all native base/upgraded bilingual description rendering, upgrade lifecycle, unified X upgrades, multiplayer
preparation, balanced/Ultimate generation, and real combat covering both migrated and newly decomposed effects.
Tests use an isolated game profile and never modify player saves.

The local RitsuLib 0.6.15 package selects its v113 compatibility branch and fails its own AttackHitHook startup
patch on v114. AutoAnthony does not depend on RitsuLib. Combat validation therefore also runs with it disabled
in the isolated copy; no RitsuLib files in the player's installation are changed.
