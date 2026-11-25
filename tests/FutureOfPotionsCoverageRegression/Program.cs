using System.Reflection;
using AutoAnthony;
using MegaCrit.Sts2.Core.Entities.Cards;

var selector = typeof(ChaosCardModel).Assembly
    .GetType("AutoAnthony.Patches.FutureOfPotionsChaosRewardCoveragePatch", true)!
    .GetMethod("SelectAvailableType", BindingFlags.Static | BindingFlags.NonPublic)!;
var types = new[] { CardType.Attack, CardType.Skill, CardType.Power };
var cases = 0;
for (var attacks = 0; attacks <= 12; attacks++)
for (var skills = 0; skills <= 12; skills++)
for (var powers = 0; powers <= 12; powers++)
foreach (var allowPower in new[] { false, true })
foreach (var requested in types)
{
    var counts = new Dictionary<CardType, int>
        { [CardType.Attack] = attacks, [CardType.Skill] = skills, [CardType.Power] = powers };
    var selected = (CardType?)selector.Invoke(null, [counts, requested, allowPower, 3]);
    var allowed = allowPower ? types : types[..2];
    var existingValid = allowed.Contains(requested) && counts[requested] >= 3;
    if (existingValid && selected != requested)
        throw new InvalidOperationException("An already valid native choice changed.");
    if (selected is { } choice && (!allowed.Contains(choice) || counts[choice] < 3))
        throw new InvalidOperationException("The event selected an insufficient or forbidden type.");
    if (selected is null && allowed.Any(type => counts[type] >= 3))
        throw new InvalidOperationException("A valid three-card type was missed.");
    var legacy = (CardType?)selector.Invoke(null, [counts, requested, allowPower, 1]);
    if (allowed.Contains(requested) && counts[requested] > 0 && legacy != requested)
        throw new InvalidOperationException("The historical external-pool threshold changed.");
    cases++;
}
Console.WriteLine($"PASS: {cases} rarity/type coverage cases; valid choices unchanged, common/token never Power, insufficient one/two-card types rejected.");
