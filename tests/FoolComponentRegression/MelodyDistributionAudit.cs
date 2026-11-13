using ChaosCardGenerator;

internal static class MelodyDistributionAudit
{
    internal static void Run(IComponentCatalog catalog, int pools)
    {
        var melodies = new[] { CardTag.Wah, CardTag.Cho, CardTag.Ki };
        bool HasMelody(IReadOnlyList<CardTag> tags) => tags.Any(melodies.Contains);
        var cards = new List<GeneratedCard>();
        for (var seed = 0; seed < pools; seed++)
        {
            var generator = new RandomCardGenerator(GeneratedCharacter.Fool, 113_162 + seed, balancedValues: true);
            foreach (var (rarity, count) in new[] {
                (GeneratedRarity.Basic, 10), (GeneratedRarity.Common, 20), (GeneratedRarity.Uncommon, 35),
                (GeneratedRarity.Rare, 24), (GeneratedRarity.Ancient, 2) })
                for (var index = 0; index < count; index++) cards.Add(generator.Generate(rarity));
        }
        var nativeCopies = catalog.Recipes.SelectMany(recipe => Enumerable.Repeat(recipe,
            CharacterComponentCatalogs.NativeOccurrenceWeight(recipe))).ToArray();
        Console.WriteLine($"balanced=true; pools={pools}; cards={cards.Count}; sourceCards={catalog.Recipes.Count}; "
            + $"weightedSourceCards={nativeCopies.Length}; Basic=StartingDeck copies");
        Console.WriteLine("metric\tnativeCards\tnativePercent\tgeneratedCards\tgeneratedPercent");
        void Row(string label, int original, int actual) => Console.WriteLine(
            $"{label}\t{original}\t{100d * original / nativeCopies.Length:F2}\t{actual}\t{100d * actual / cards.Count:F2}");
        Row("anyMelody", nativeCopies.Count(recipe => HasMelody(recipe.Tags)), cards.Count(card => HasMelody(card.Tags)));
        foreach (var tag in melodies)
            Row(tag.ToString(), nativeCopies.Count(recipe => recipe.Tags.Contains(tag)), cards.Count(card => card.Tags.Contains(tag)));
        Row("multipleMelodies", nativeCopies.Count(recipe => recipe.Tags.Count(melodies.Contains) > 1),
            cards.Count(card => card.Tags.Count(melodies.Contains) > 1));
        Row("shoot", nativeCopies.Count(recipe => recipe.Atoms.Any(FoolComponentRules.IsShootModifier)),
            cards.Count(card => card.Operations.Any(FoolComponentRules.IsShootModifier)));
        Console.WriteLine("rarity\tnativeMelodyPercent\tgeneratedMelodyPercent\tnativeShootPercent\tgeneratedShootPercent");
        foreach (var rarity in Enum.GetValues<GeneratedRarity>())
        {
            var original = nativeCopies.Where(recipe => recipe.OriginalRarity == rarity).ToArray();
            var generated = cards.Where(card => card.Rarity == rarity).ToArray();
            Console.WriteLine($"{rarity}\t{100d * original.Count(recipe => HasMelody(recipe.Tags)) / original.Length:F2}"
                + $"\t{100d * generated.Count(card => HasMelody(card.Tags)) / generated.Length:F2}"
                + $"\t{100d * original.Count(recipe => recipe.Atoms.Any(FoolComponentRules.IsShootModifier)) / original.Length:F2}"
                + $"\t{100d * generated.Count(card => card.Operations.Any(FoolComponentRules.IsShootModifier)) / generated.Length:F2}");
        }
    }
}
