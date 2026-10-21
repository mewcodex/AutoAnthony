using ChaosCardGenerator;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

// Reported v0.3.136 seed: simultaneous coverage, high-resource and Star-supply constraints
// exhausted every repair slot, then restarted the entire pool. Keep the same populated-pool context.
var hash = SHA256.HashData(Encoding.UTF8.GetBytes("AutoAnthony/v111/all-pools-v2/Regent/32CUR4R3KQPP"));
var random = new Random(BitConverter.ToInt32(hash, 0) & int.MaxValue);
var generator = new RandomCardGenerator(GeneratedCharacter.Regent, random.Next(), balancedValues: true);
var cards = new List<GeneratedCard>();
var matching = typeof(RandomCardGenerator).GetMethod("GenerateMatching", BindingFlags.Instance | BindingFlags.NonPublic)!;
var elapsed = System.Diagnostics.Stopwatch.StartNew();
foreach (var rarity in Enum.GetValues<GeneratedRarity>())
for (var slot = 0; slot < (rarity switch
     { GeneratedRarity.Basic => 10, GeneratedRarity.Common => 20, GeneratedRarity.Uncommon => 35,
         GeneratedRarity.Rare => 25, _ => 2 }); slot++)
{
    cards.Add(generator.Generate(rarity));
    if (cards.Count != 10) continue;
    Cover(StartingPoolConstraintResolver.CountsAsDamage, StartingPoolConstraintResolver.CountsAsDefense);
    Cover(StartingPoolConstraintResolver.CountsAsDefense, StartingPoolConstraintResolver.CountsAsDamage);
}
var starters = cards.Take(10).ToArray();
if (!StartingPoolConstraintResolver.TryRepair(starters, generator, random, 4, 4, 128, out var failure))
    throw new InvalidOperationException(failure);
if (starters.Count(StartingPoolConstraintResolver.CountsAsDamage) < 4
    || starters.Count(StartingPoolConstraintResolver.CountsAsDefense) < 4
    || starters.Count(StartingPoolConstraintResolver.IsHighResourceCard) > 2
    || starters.Count(StartingPoolConstraintResolver.ConsumesStars) > starters.Count(StartingPoolConstraintResolver.ProducesStars))
    throw new InvalidOperationException("Repair weakened starting-deck constraints.");
foreach (var card in starters) CardTemplateValidator.Validate(card);
Console.WriteLine($"Reported Regent seed repaired successfully in {elapsed.ElapsedMilliseconds} ms.");

void Cover(Func<GeneratedCard, bool> required, Func<GeneratedCard, bool> protect)
{
    while (cards.Count(required) < 4)
    {
        var replacement = (GeneratedCard)matching.Invoke(generator, [GeneratedRarity.Basic, required])!;
        var slot = cards.FindIndex(card => !required(card)
            && (!protect(card) || protect(replacement) || cards.Count(protect) > 4));
        if (slot < 0) throw new InvalidOperationException("No coverage repair slot.");
        cards[slot] = replacement;
    }
}
