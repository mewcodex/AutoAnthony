using ChaosCardGenerator;

internal static class CompositionDiversityAudit
{
    internal static void Run(int pools)
    {
        foreach (var character in Enum.GetValues<GeneratedCharacter>().Where(c => c != GeneratedCharacter.Colorless))
        {
            var cards = new List<GeneratedCard>();
            var overlap = new List<double>();
            for (var seed = 0; seed < pools; seed++)
            {
                var generator = new RandomCardGenerator(character, 164_000 + seed, balancedValues: true);
                var pair = new[] { generator.Generate(GeneratedRarity.Ancient), generator.Generate(GeneratedRarity.Ancient) };
                cards.AddRange(pair);
                var first = pair[0].Operations.Select(op => op.Template).ToHashSet(StringComparer.Ordinal);
                var second = pair[1].Operations.Select(op => op.Template).ToHashSet(StringComparer.Ordinal);
                overlap.Add(first.Intersect(second).Count() / (double)Math.Max(1, first.Union(second).Count()));
                foreach (var card in pair) CardTemplateValidator.Validate(card);
            }
            Console.WriteLine($"{character}: cards={cards.Count}; templates={cards.Select(GeneratedCardEffectIdentity.TemplateSignature).Distinct().Count()}; "
                + $"pairJaccard={overlap.Average():F3}; types={string.Join(',', cards.GroupBy(c => c.Type).Select(g => g.Key + ":" + g.Count()))}");
        }
    }

    internal static void Verify()
    {
        var catalog = CharacterComponentCatalogs.Get(GeneratedCharacter.Fool);
        var tracker = new NativeComponentFrequencyTracker(catalog);
        foreach (var atom in catalog.Atoms)
        foreach (var type in Enum.GetValues<GeneratedCardType>())
        {
            if (tracker.SourcePriorWeight(GeneratedRarity.Rare, type, atom.FamilyKey, [])
                != tracker.SourcePriorWeight(GeneratedRarity.Ancient, type, atom.FamilyKey, [])
                || tracker.VariantWeight(atom, GeneratedRarity.Rare, type, TargetMode.Other, [])
                != tracker.VariantWeight(atom, GeneratedRarity.Ancient, type, TargetMode.Other, []))
                throw new InvalidOperationException("Ancient retained a separate component-source prior: " + atom.Template);
        }
        var compact = 0;
        foreach (var ultimate in new[] { false, true })
        foreach (var numericRandom in new[] { false, true })
        {
            var generator = new RandomCardGenerator(GeneratedCharacter.Fool, 164_001,
                unlockComponentRoles: ultimate, balancedValues: true, randomizeNumericValues: numericRandom);
            foreach (var rarity in Enum.GetValues<GeneratedRarity>())
            for (var sample = 0; sample < 12; sample++)
            {
                var card = generator.Generate(rarity);
                var upgraded = card.Upgrade is null ? card.Operations
                    : CardUpgradeGenerator.ApplyEffectsToOperations(card.Operations, card.Upgrade.Effects);
                foreach (var operation in card.Operations.Concat(upgraded))
                {
                    if (!operation.RuntimeSpec!.Flags.Contains("compact_hand_token_reference")) continue;
                    if (operation.Template is not ("F:CreateJoy" or "F:CreateSadness")
                        && operation.RuntimeSpec.DestinationZone != "hand") continue;
                    foreach (var value in operation.RuntimeSpec.Values.Where(v => v.Source == "fixed" && v.Id is "amount" or "count"))
                    {
                        if (value.BaseValue + value.Offset > 3) throw new InvalidOperationException("Crowded hand token count: " + operation.Template);
                        compact++;
                    }
                }
            }
        }
        if (compact == 0) throw new InvalidOperationException("No Joy/Sadness output was exercised.");
        Console.WriteLine($"PASS: Rare/Ancient share component priors; {compact} base/upgraded compact token counts <=3, including Numeric Random.");
    }
}
