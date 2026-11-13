# Fool real-engine combat regression

Test-only plugin for a disposable v113 game copy. The initializer refuses any user-data directory that does not
contain `AutoAnthonyV113Smoke-20261112`. Never install it in the normal game or a player profile.

Build after AutoAnthony:

```powershell
dotnet build tests/FoolCombatRegression -c Release -p:Sts2GameDir=<game>
```

Install its DLL with an isolated manifest (`id=FoolCombatRegression`, `has_dll=true`, `has_pck=false`), disable
other automatic test plugins and launch with Steam disabled. It starts a native encounter and exercises actual
commands with normal card animations, then exits. `AA_FOOL_COMBAT_COMPLETE` marks success; each operation has a
timeout and assertions. `AA_FOOL_TEST_ULT_REGENT=1` repeats the checks as Regent in Ultimate Chaos, including its
initial Arrow slot. Coverage includes arrows, Aim, conditional/direct/Exhaust returns, transformation, tokens,
melody events, repeats, Stretch refunds, nested conditions, permanent growth and X=0/3.

The death-gate assertion invokes the real hook with a controlled eligible enemy, rather than ending the encounter.
Interactive selection and real second-device networking are outside this fixture. Remove the test manifest after use.
