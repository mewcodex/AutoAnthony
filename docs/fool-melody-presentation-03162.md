# Fool presentation and Melody audit (0.3.162)

The generated title grammar is a nonempty typed Melody prefix followed by zero or one reviewed body fragment.
Chinese bodies contain at most two characters; an empty body permits pure-Melody names. Strike, Hang, Shot and
Form restrictions still apply. Both languages use the same Melody tags and one body source. Original titles and
run-local duplicate titles remain excluded. When the second source is entirely Melody syllables, one body fragment
from the first source keeps composite Melodies reachable without reproducing an original pure-Melody title.

The native Fool authoring export previously sliced the tag declaration at `CanonicalVars`, missing declarations
which followed that property. It now reads the actual tag property expression, including Ki Whip's direct `Tags`
override. Native single-player coverage is 19/84 Melody cards: eight each of Wah, Cho and Ki, with four composite
Melody cards. This is a metadata correction, not a runtime inference from displayed names.

Balanced-mode sampling uses 32 deterministic seeds and complete 91-card Fool pools (2,912 cards). The report
includes each Melody, their union, composites and rarity cohorts: `audits/fool-03162-melodies-final.txt`.
No separate Melody probability override was added. The shortened title grammar alone initially suppressed most
composite titles through the original-name exclusion; the first-source body continuation fixes that attrition.
Basic generated cards intentionally can carry Melody tags because their effects can also depend on Melodies.

Final sample: any Melody 29.43% (native 22.62%); Wah 10.92%, Cho 11.61%, Ki 11.26% (native 9.52% each);
composite Melodies 3.88% (native 4.76%). Generated Basics average 3.4 Melody cards per ten-card starting pool.
The total is moderately above native, individual Melodies are balanced, and composites remain close to native.

Runtime card descriptions now reuse the structured nested-clause renderer, join Shoot to its damage sentence in
either operation order, and resolve `[E]` presentation shorthand before formatting. Repeated Energy resource glyphs
remain distinct from the single glyph attached to a numeric cost. Fool terminology is highlighted consistently.

Tests cover both native and generated descriptions inside the game, including nested Tune, both cost-reduction
forms, next-card-free, both languages, upgrades and all native Fool tags. The title rule affects newly generated
identities; existing saved identities are preserved. Description rendering fixes apply to existing cards too.

Build and complete generator self-test passed. Actual-engine probe checked 1,318 native instances and 2,556
descriptions with zero failures, plus the generated-card cases above (`audits/fool-03162-native-final.log`).
Version 0.3.162 is deployed as DLL/JSON/PCK to the game and uploader/content; hashes match. The previous package
is backed up and the test plugin disabled. The Workshop change note is prepared; publication was not requested.
