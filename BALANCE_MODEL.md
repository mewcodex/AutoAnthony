# AutoAnthony component balance model (v111 draft)

This is a deliberately approximate model for random assembly, not a claim that the base game is exactly solved.
The target center is slightly above a comparable original card, while every component and original assembly keeps
a non-zero route and roughly 9% of numeric rolls receive a strong positive tail.

## Effect currency

One value point is approximately one point of printed single-target damage. These conversions price fixed mechanics,
whole-card acceptance, and the later numeric allocation; scalable families are no longer selected from the literal
number printed on their source card. Character-specific numeric curves determine the actual generated number.

| Effect | Approximate value |
|---|---:|
| 1 single-target damage | 1.00 |
| 1 damage to all enemies | 1.55 |
| 1 Block | 1.20 |
| Draw 1 | 4.50 |
| Gain 1 Energy | 6.50 |
| Gain 1 Strength/Dexterity | 5.25 |
| Gain 1 Focus | 6.50 |
| Gain 1 Intangible | 10.50 |
| Gain 1 Plating | 3.20 |
| Apply 1 Vulnerable | 3.10 |
| Apply 1 Weak | 2.70 |
| Apply 1 Poison | 2.00 |
| Apply 1 Doom | 0.80 |
| Heal 1 | 1.40 |
| Gain 1 Max HP | 2.30 |
| Forge 1 | 1.00 |
| Gain 1 Star | 2.60 |
| Channel 1 Lightning Orb (neutral reference) | 5.00 |
| Double the target's Vulnerable | 8.50 |
| Gain Strength equal to target Vulnerable | 24.00 |
| Add a same-cost copy of this card to the discard pile | contextual; see below |
| Add a 0-cost copy of this card to the discard pile | 15.00 |
| Exhaust the whole hand (downside magnitude) | 15.00 |
| Give the target 1 Strength (downside magnitude) | 9.00 |
| Play the top draw-pile card and Exhaust it | 9.00 |

An ordinary same-cost self-copy uses the final card context rather than one fixed value. On a reusable card with
printed Energy/Star/X cost it is a downside comparable to adding one Slime to the discard pile. On an Exhaust card
it exactly cancels the Exhaust keyword's budget premium. On a free reusable card it is worth 6.00, using Anger as
the native reference. On a Power it is a large 20.00-value engine and receives a sharply reduced, upper-rarity-
weighted occurrence chance. The separate operation that explicitly creates a 0-cost copy retains its own value.

Unique rules, modifiers, and proxies also inherit a native-strength prior from the original card's Energy cost,
Star cost, rarity, and number of benefit clauses. This is what makes a mechanic sourced from a 2-cost Uncommon
card substantially less likely on a 1-cost Common card without banning that result.

Family selection now has one policy per concern: native occurrence, trigger cadence, fixed-mechanic affordability,
core-combat occurrence, rarity/scarcity, character distribution, and repetition. The former cross-family source-
literal value pass was removed because numeric allocation already prices the generated value. Basic/immediate/
top-rarity/reference damage and Block adjustments are folded into one core-combat occurrence weight, while target
mix remains an independent policy.

## Native component frequency prior

Occurrence is calibrated independently for the six source pools: Ironclad, Silent, Defect, Necrobinder, Regent,
and Colorless. Ultimate Chaos uses a seventh profile built from the merged six-pool catalog, so its characters do
not retain hidden per-character component odds.

For each component family the generator records native occurrences by rarity, card type, target mode, and semantic
role. Roles are: unlinked operation, standalone Power foundation, Power-trigger payoff, and ordinary conditional
payoff. Pool -> rarity -> type -> target rates use empirical-Bayes smoothing, which preserves a non-zero route for
cross-rarity/type/role recombination without treating an effect native only behind a trigger as a common standalone
effect. Numeric variants use the same role-conditioned hierarchy inside their family.

One tracker shared by every assembler in a generated pool observes finalized cards only. It compares each
rarity/type/role family rate with the corresponding source rate and applies bounded squared feedback to counter
legality and whole-card-validation bias. This is selection feedback, not rejection: it cannot create an unbounded
retry loop, and every component retains a positive weight. Distribution audits must weight generated samples by
the source pool's actual rarity counts; giving every rarity the same sample mass badly overstates Rare/Ancient
Power foundations.

The five explicitly scarce mechanics—doubling Vulnerable, converting target Vulnerable to Strength, copying this
card into the discard pile, exhausting the whole hand, and giving the target Strength—use 10% / 22% / 45% / 70%
of their ordinary Basic / Common / Uncommon / Rare-or-Ancient occurrence prior. This multiplier is part of the same
semantic/native-rarity calibration pipeline as every other low-frequency effect rather than an independent second
filter. The weights remain non-zero so native recipes are still reconstructible.

Plating uses the same centralized scarcity pipeline at the higher very-rare tier: 5% / 12% / 28% / 45% for
Basic / Common / Uncommon / Rare-or-Ancient. Its persistent value remains 3.20 damage-equivalents per stack.

## Trigger frequency estimates

For persistent triggers, the number is expected opportunities per ordinary turn. For one-shot gates, it is an
approximate success probability. Payoffs decrease as persistent frequency rises; difficult one-shot gates receive
a controlled premium rather than a literal inverse-probability multiplier.

| Trigger/condition | Relative frequency | Typical payoff scale |
|---|---:|---:|
| Every card played | 3.20/turn | 25% |
| Every card drawn | 4.80/turn | 25% |
| Every Attack played | 1.55/turn | 34% |
| Every Skill played | 1.45/turn | 52% |
| Every 1 Star spent | 2.00/turn | 34% |
| Every 2 Stars spent | 1.00/turn | 52% |
| Every 4 Energy spent (Orbit) | 0.78/turn | 60% |
| Start of turn | 1.00/turn | 52% |
| A card is Exhausted | 0.70/turn | 60-85% |
| Target has Poison | 55% | 120% |
| Target has Vulnerable | 62% | 120% |
| Enemy intends to attack | 62% | 120% |
| Fatal | 25% | 155% |
| No Attacks in hand | 30% | 155% |
| Hand is empty | 16% | 155% |
| Draw pile is empty / Grand Finale | 8% | 220% plus its special high-value variant bias |

## Whole-card allocation

Individual fields are initially sampled from their character and effect curves, then the completed non-native card
is reconciled against one shared whole-card budget envelope. Positive field count never expands that allowance:
Damage, Block, Draw and other rewards all spend the same budget, and repeating a semantic field remains legal but
does not create a second budget. Trigger conditions are not positive fields. Effective Energy (including Stars),
rarity, one-shot Power pricing, and the explicit prices of negative effects determine the final envelope.

The balanced-mode centers are fitted offline from the five native character pools. Native zero- and one-Energy
robust centers anchor each rarity; costs above one retain the shared nonlinear Energy curve because native high-cost
cells are sparse and contain many unique rule effects. Basic and Ancient zero-cost centers are extrapolated from the
mean zero/one ratio of Common, Uncommon and Rare, avoiding their sparse zero-cost samples. The resulting zero/one-
Energy centers are 4.1/6, 7.5/12, 10/14, 13.6/19 and 16.4/24 Damage-equivalents for
Basic/Common/Uncommon/Rare/Ancient. The one-Energy output bands are 6–7 / 10–14 / 12–17 / 16–23 / 20–30. Aggressive
mode keeps the same lower edge and raises the upper edge by 50%. Exact native reconstructions bypass this
reconciliation so every original card remains reachable.
Damage+Block flexibility, large Block totals, and other emergent synergies are charged after the ordinary envelope,
so their printed scalar sum can be lower while their synergy-adjusted card value remains within budget.

Weak and Vulnerable use one smooth duration curve rather than full linear value per stack: one layer anchors the
effect, while additional layers scale with the square root of duration because they mostly extend the debuff into
later turns. Persistent triggered Draw carries a shared 1.18 card-advantage multiplier (temporary repeated Draw uses
1.22); generation applies the exact reciprocal so the estimator and printed-value sampler cannot drift apart.

Every nonlinear negative effect owns a reviewed whole-card multiplier; there is no integer severity or shared lookup
table. Stackable payments that do not prevent this card from resolving instead own an additive value per stack. The
combined direct multiplier is applied to scalable printed values and to the legal budget envelope together: the first
step preserves the roll's percentile inside the shifted range, while the second validates the completed card, so these
are two stages of one compensation rather than two multiplicative rewards. Missing nonlinear prices fail the generator
self-test instead of inheriting a fallback bucket. Rarity and resource cost alone determine ordinary component count.
Very large payments that cannot be represented by scalable numbers may still use a limited cost adjustment;
resource-positive cards may still reserve a downside slot, and difficult conditions still reserve their required
payoff, because neither mechanism is a negative-effect line bonus.
