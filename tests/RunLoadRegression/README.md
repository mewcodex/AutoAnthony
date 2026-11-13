# Real-engine save/load smoke test

This test-only plugin must run in a disposable game copy with Steam disabled and a custom user-data directory
containing `AutoAnthonyV113Smoke-20261112`. Its initializer refuses other directories. Never install it in the
normal game or use an existing player profile. It creates and overwrites test saves inside that isolated profile.

Build with `dotnet build tests/RunLoadRegression -c Release -p:Sts2GameDir=<game>` after building AutoAnthony.
Install `EngineSmoke.dll` with a test-only manifest (`id=EngineSmoke`, `has_dll=true`, `has_pck=false`).
Disable other automatic test plugins, copy the three production AutoAnthony files into the isolated mod folder,
and launch the isolated game with `--force-steam=off`.

First stage, with `AA_SMOKE_LOAD_ONLY` unset:

- Start each of the six supported characters, generating all seven pools. Regent and Fool also test Ultimate Chaos.
- Upgrade one starter, save through the native SaveManager, return to menu, and execute the native Continue
  action including its loading/fade lifecycle.
- Compare SHA-256 fingerprints of all pool definitions, names, portraits and effects; compare the deck,
  upgrade levels, costs, keywords and formatted descriptions.
- Leave the final Fool save and its expected fingerprints in the isolated profile, then exit after
  `AA_SMOKE_PASS`.

Second stage: start a fresh isolated process with environment variable `AA_SMOKE_LOAD_ONLY=1`. It invokes
native Continue on the saved Fool Ultimate Chaos run, repeats fingerprint checks, returns to menu and exits
after `AA_SMOKE_FRESH_LOAD_PASS`. The second stage can use the normal renderer to exercise actual scene loading.

`AA_SMOKE_FOOL_ONLY=1` restricts the first stage to normal/Ultimate Fool. Saved `ExtraAim` is also fingerprinted.

The plugin never ships in AutoAnthony. It does not simulate combat completion, arbitrary older saves or a real
multiplayer session. Remove/disable its manifest when testing finishes.
