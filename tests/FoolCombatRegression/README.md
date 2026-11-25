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
The v113.1 checks include Stretch consumed by Adroit on a generated card without intrinsic Block,
and preservation of Stretch by an unenchanted non-Block card without a phantom Block preview.
Arrow-condition checks distinguish an available Arrow from Away/Destroyed slots, exercise Dislodge with two
payoffs under one condition, and preserve Ki Hop's pre-hit Impaled check when the hit retrieves the last Arrow.

The death-gate assertion invokes the real hook with a controlled eligible enemy, rather than ending the encounter.
Interactive selection and real second-device networking are outside this fixture. Remove the test manifest after use.

`AA_FATAL_ONLY=1` runs a focused two-enemy combat check: 12 selected damage, Fatal -> 23 area damage,
then +33 to that area damage. It checks that neither payoff runs without a kill and that both run after
the selected enemy dies, with another enemy still alive. The full fixture uses the same two-enemy encounter.

`AA_SHOOT_GENERATION_ONLY=1` checks every tagged Shoot candidate and its combat clone, then samples
the generated RandomShootFree operation and native BlotOutTheSky 12 times each. It checks the requested
counts, actual Shoot operations/tags and the rendered shot description of every card added to Hand.
