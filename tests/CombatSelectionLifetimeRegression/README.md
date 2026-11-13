# Combat selection lifetime regression

Run `dotnet run --project tests/CombatSelectionLifetimeRegression -c Release -p:Sts2GameDir=<game>` after building
AutoAnthony. No Godot process, player settings or saves are used.

The fixture suspends the real interpreter hand/discard selection helpers at a native choice boundary. It then
simulates owner death, combat ending, owner combat replacement and a new native CombatId before the choice
resumes. None may turn a cancelled answer into forced mandatory targets. A valid card drawn while selection
is suspended must remain selectable, and no choice may start after combat has ended.

Native presentation/choice transport is replaced only inside this test process. Actual multiplayer transport and
the graphical hand-selection UI require separate engine/play tests.
