# BaseLib / RitsuLib activation boundary checks

This optional headless test consumes the actual locally installed libraries. It is not a runtime mod dependency
and does not ship their binaries. No game initializer, native scene, Steam session, user settings or save file is used.

Example (Windows, v0.113.0):

```powershell
dotnet run --project tests/LibraryActivationCompatibility -c Release `
  -p:Sts2GameDir='F:\SteamLibrary\steamapps\common\Slay the Spire 2' -- `
  'F:\SteamLibrary\steamapps\workshop\content\2868840' `
  'F:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64' both --generate
```

Modes: `none`, `base`, `ritsu`, `both`. `--aa-first` reverses the selected patch installation order.
`--patch-all` checks installation of every AutoAnthony Harmony patch and exits before pool tests.
`--generate` runs the real constrained balanced generator and portrait-source assignment for all six pools,
then emits a SHA-256 digest of the complete definitions. Without it, fixed generated definitions isolate routing.

Checked boundaries:

- AutoAnthony's singleplayer, multiplayer, load, save, character-pool and starting-deck patches install alongside
  the selected real library patches.
- RitsuLib startup pool invalidation before and after registration freeze preserves native Colorless contents.
- Five native characters still resolve generated starting decks and complete generated reward pools.
- Inventories created before run startup receive generated basic cards, preserve extra cards and remain
  unchanged on a second refresh; the unsupported Fool character retains its native pool and starter deck.
- A previous 52-card Colorless snapshot keeps every old definition, cost and name and recovers only the newly
  added Rare slot. The fixture includes the retired Draw-with-Retain and per-discard damage components.
- BaseLib and RitsuLib multiplayer-save canonicalization extension callbacks retain native modifier carriers.
- RitsuLib singleplayer payload preparation does not clear active generated pools when no tracked lobby exists.
- RitsuLib JSON extension injection preserves native modifier payloads.

Engine-dependent logging, settings static initialization, native scenes and complete framework startup are
outside the test. The isolated database uses native card constructors and registered native card/pool/character
identities; generated hosts and non-card models are detached canonical fixtures. It does not validate visual UI,
all framework lifecycle subscribers, actual multiplayer transport, or every historical library version.

Verified locally on 2026-11-12 with BaseLib 3.4.8, RitsuLib 0.6.11 (v0.113.0 runtime variant),
AutoAnthony 0.3.159. RitsuLib's runtime variant is selected from the installed game's release metadata.
