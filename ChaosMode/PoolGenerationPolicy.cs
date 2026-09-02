using ChaosCardGenerator;

namespace AutoAnthony;

/// <summary>
/// Declarative constraints applied after individual cards are generated. These are pool-shape guarantees rather
/// than card-value rules, so they stay outside the component generator and can be changed without touching its
/// sampling pipeline.
/// </summary>
internal sealed record PoolGenerationPolicy(
    bool EnforceStartingCoverage,
    bool EnforceSpecialXMinimum,
    int StartingDeckSize,
    int MinimumStartingDamage,
    int MinimumStartingDefense,
    int OrdinaryXMaximum,
    int SpecialXMinimum,
    int SpecialXMaximum,
    int MinimumTotalX,
    int ReplacementAttemptLimit)
{
    private static readonly PoolGenerationPolicy CharacterPool = new(
        EnforceStartingCoverage: true,
        EnforceSpecialXMinimum: true,
        StartingDeckSize: 10,
        MinimumStartingDamage: 4,
        MinimumStartingDefense: 4,
        OrdinaryXMaximum: 2,
        SpecialXMinimum: 1,
        SpecialXMaximum: 2,
        MinimumTotalX: 2,
        ReplacementAttemptLimit: 20_000);

    internal static PoolGenerationPolicy For(GeneratedCharacter character) =>
        character == GeneratedCharacter.Colorless
            ? CharacterPool with { EnforceStartingCoverage = false, EnforceSpecialXMinimum = false }
            : CharacterPool;

    internal int RollSpecialXTarget(Random random, int ordinaryXCount)
    {
        var target = SpecialXMinimum + random.Next(SpecialXMaximum - SpecialXMinimum + 1);
        return Math.Max(target, MinimumTotalX - ordinaryXCount);
    }

    internal bool IsValidXDistribution(int ordinaryCount, int specialCount) =>
        ordinaryCount <= OrdinaryXMaximum
        && (!EnforceSpecialXMinimum
            || specialCount >= SpecialXMinimum && specialCount <= SpecialXMaximum
            && ordinaryCount + specialCount >= MinimumTotalX);

    internal static void Validate()
    {
        var character = For(GeneratedCharacter.Ironclad);
        var colorless = For(GeneratedCharacter.Colorless);
        if (!character.EnforceStartingCoverage || !character.EnforceSpecialXMinimum
            || character.StartingDeckSize != 10
            || character.MinimumStartingDamage != 4 || character.MinimumStartingDefense != 4
            || !character.IsValidXDistribution(1, 1) || character.IsValidXDistribution(3, 1)
            || colorless.EnforceStartingCoverage || colorless.EnforceSpecialXMinimum)
            throw new InvalidOperationException("卡池覆盖或X费配额配置无效。");
    }
}
