using System.Text;

namespace ChaosCardGenerator;

/// <summary>Deterministic diagnostic for comparing generated upgrade strength with the v111 source audit.</summary>
internal static class GeneratorUpgradeAudit
{
    internal static string Run(GeneratedCharacter character, int samplesPerRarity)
    {
        var output = new StringBuilder();
        output.AppendLine($"character={character}; samplesPerRarity={samplesPerRarity}");
        output.AppendLine("rarity\ttwoEffects\tmeanGain\tmedianGain\tp10/p90\tmeanGainVsBase");
        foreach (var rarity in Enum.GetValues<GeneratedRarity>())
        {
            var generator = new RandomCardGenerator(character,
                seed: 0x2A7100 + (int)character * 997 + (int)rarity * 7919);
            var cards = Enumerable.Range(0, samplesPerRarity).Select(_ => generator.Generate(rarity)).ToArray();
            var gains = cards.Select(CardUpgradeGenerator.EstimatedUpgradeGain).Order().ToArray();
            var ratios = cards.Select(card => CardUpgradeGenerator.EstimatedUpgradeGain(card)
                    / Math.Max(1d, EffectBalanceModel.EstimatedPositiveCardValue(card.Operations)))
                .ToArray();
            output.AppendLine(string.Join('\t',
                rarity,
                Percent(cards.Count(card => card.Upgrade?.Effects.Count == 2), cards.Length),
                Math.Round(gains.Average()),
                Math.Round(Percentile(gains, 0.50d)),
                $"{Math.Round(Percentile(gains, 0.10d))}/{Math.Round(Percentile(gains, 0.90d))}",
                ratios.Average().ToString("P1")));
        }
        return output.ToString();
    }

    private static string Percent(int numerator, int denominator) =>
        (numerator / (double)Math.Max(1, denominator)).ToString("P1");

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0d;
        return sorted[Math.Clamp((int)Math.Floor((sorted.Count - 1) * percentile), 0, sorted.Count - 1)];
    }
}
