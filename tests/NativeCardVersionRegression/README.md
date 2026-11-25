# Native-card version regression

This optional test plugin runs inside the real Godot game with AutoAnthony. It compares the embedded native
reference catalog to actual mutable native cards, their first upgrades and keyword sets, then formats their
component descriptions in English and Simplified Chinese. It checks DynamicVar identities, values, and explicit component bindings, including zero-delta numeric bindings on keyword-only upgrades.
It also verifies generated per-instance upgrade/downgrade state, external keyword preservation and the v113
hidden-portrait atlas UV refresh after a final texture replacement. The portrait check uses real Godot controls
and native setters; it substitutes only the art-mod lookup so it does not depend on a particular portrait pack.

Build with `dotnet build tests/NativeCardVersionRegression -c Release -p:Sts2GameDir=<game directory>`.
It must only be installed in a disposable game copy with Steam disabled and `override.cfg` setting a custom
user-data directory containing `AutoAnthonyV113Smoke-20261112`. The initializer refuses any other profile.
Supply the DLL and a test-only JSON manifest with `has_dll=true`, `has_pck=false` and
`id=NativeCardVersionRegression`. Remove other test initializers from that isolated copy before running it.

The plugin quits the isolated game after reporting `AA_NATIVE_COMPLETE`; it never ships in AutoAnthony's
package. It does not simulate every combat program or multiplayer transport. The current native catalog includes Fool as well as the five older characters and Colorless.

The Future of Potions check populates 54 native three-choice rewards across the six built-in generated
character pools, checks rarity/type/upgrade consistency, and reproduces a two-candidate failure using a
test-only sparse pool. It verifies that the event corrects its advertised type before reward creation;
the global card factory continues to reject insufficient candidates. It does not automate potion discard
or interaction with the reward-selection screen.

The live library filter is checked with generation disabled, enabled, and preserving original cards. Generated
Colorless models stay registered/readable for history without appearing in the native-only Colorless tab.
Shoot-tag checks restore an old payload with a stray source tag, keep native and actual generated Shoot cards
searchable, and verify that removing the Shoot operation also removes its runtime tag.
