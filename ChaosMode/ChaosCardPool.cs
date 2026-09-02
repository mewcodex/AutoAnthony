using Godot;
using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Models;

namespace AutoAnthony;

public sealed class ChaosIroncladCardPool : CardPoolModel
{
    public override string Title => "ironclad";
    public override string EnergyColorName => "ironclad";
    public override string CardFrameMaterialPath => "card_frame_red";
    public override Color DeckEntryCardColor => new("D62000");
    public override Color EnergyOutlineColor => new("802020");
    public override bool IsColorless => false;

    protected override CardModel[] GenerateAllCards() => ChaosCardRegistry.Types
        .Select(type => ModelDb.GetById<CardModel>(ModelDb.GetId(type)))
        .ToArray();
}

public sealed class ChaosSilentCardPool : CardPoolModel
{
    public override string Title => "silent";
    public override string EnergyColorName => "silent";
    public override string CardFrameMaterialPath => "card_frame_green";
    public override Color DeckEntryCardColor => new("5EBD00");
    public override Color EnergyOutlineColor => new("1A6625");
    public override bool IsColorless => false;

    protected override CardModel[] GenerateAllCards() => ChaosCardRegistry.SilentTypes
        .Select(type => ModelDb.GetById<CardModel>(ModelDb.GetId(type)))
        .ToArray();
}

public sealed class ChaosDefectCardPool : CardPoolModel
{
    public override string Title => "defect";
    public override string EnergyColorName => "defect";
    public override string CardFrameMaterialPath => "card_frame_blue";
    public override Color DeckEntryCardColor => new("3EB3ED");
    public override Color EnergyOutlineColor => new("1D5673");
    public override bool IsColorless => false;
    protected override CardModel[] GenerateAllCards() => ChaosCardRegistry.DefectTypes.Select(Model).ToArray();
    private static CardModel Model(Type type) => ModelDb.GetById<CardModel>(ModelDb.GetId(type));
}

public sealed class ChaosNecrobinderCardPool : CardPoolModel
{
    public override string Title => "necrobinder";
    public override string EnergyColorName => "necrobinder";
    public override string CardFrameMaterialPath => "card_frame_pink";
    public override Color DeckEntryCardColor => new("CD4EED");
    public override Color EnergyOutlineColor => new("803367");
    public override bool IsColorless => false;
    protected override CardModel[] GenerateAllCards() => ChaosCardRegistry.NecrobinderTypes.Select(Model).ToArray();
    private static CardModel Model(Type type) => ModelDb.GetById<CardModel>(ModelDb.GetId(type));
}

public sealed class ChaosRegentCardPool : CardPoolModel
{
    public override string Title => "regent";
    public override string EnergyColorName => "regent";
    public override string CardFrameMaterialPath => "card_frame_orange";
    public override Color DeckEntryCardColor => new("E36600");
    public override Color EnergyOutlineColor => new("803D0E");
    public override bool IsColorless => false;
    protected override CardModel[] GenerateAllCards() => ChaosCardRegistry.RegentTypes.Select(Model).ToArray();
    private static CardModel Model(Type type) => ModelDb.GetById<CardModel>(ModelDb.GetId(type));
}

public static class ChaosCardRegistry
{
    public const int Count = ChaosRunDefinitions.TotalCount * 4 + ChaosRunDefinitions.SilentTotalCount + ChaosRunDefinitions.ColorlessCount;
    public static IReadOnlyList<Type> Types { get; } = typeof(ChaosCard000).Assembly.GetTypes()
        .Where(type => !type.IsAbstract && type.BaseType == typeof(ChaosCardModel) && type.Name.StartsWith("ChaosCard", StringComparison.Ordinal))
        .OrderBy(type => type.Name, StringComparer.Ordinal)
        .ToArray();
    public static IReadOnlyList<Type> SilentTypes { get; } = typeof(ChaosCard000).Assembly.GetTypes()
        .Where(type => !type.IsAbstract && type.BaseType == typeof(ChaosSilentCardModel))
        .OrderBy(type => type.Name, StringComparer.Ordinal)
        .ToArray();
    public static IReadOnlyList<Type> DefectTypes { get; } = CharacterTypes(typeof(ChaosDefectCardModel));
    public static IReadOnlyList<Type> NecrobinderTypes { get; } = CharacterTypes(typeof(ChaosNecrobinderCardModel));
    public static IReadOnlyList<Type> RegentTypes { get; } = CharacterTypes(typeof(ChaosRegentCardModel));
    public static IReadOnlyList<Type> ColorlessTypes { get; } = CharacterTypes(typeof(ChaosColorlessCardModel));

    public static CardModel Canonical(int slot) => ModelDb.GetById<CardModel>(ModelDb.GetId(Types[slot]));
    public static CardModel Canonical(GeneratedCharacter character, int slot) => ModelDb.GetById<CardModel>(ModelDb.GetId(TypesFor(character)[slot]));
    public static bool TryGetGeneratedCardSlot(ModelId id, out GeneratedCharacter character, out int slot)
    {
        foreach (var candidate in ChaosRunDefinitions.SupportedPools)
        {
            var types = TypesFor(candidate);
            for (var index = 0; index < types.Count; index++)
            {
                if (ModelDb.GetId(types[index]) != id) continue;
                character = candidate;
                slot = index;
                return true;
            }
        }

        character = default;
        slot = -1;
        return false;
    }
    public static bool IsGeneratedCardId(ModelId id) =>
        TryGetGeneratedCardSlot(id, out _, out _);
    public static IReadOnlyList<Type> TypesFor(GeneratedCharacter character) => character switch
    {
        GeneratedCharacter.Ironclad => Types,
        GeneratedCharacter.Silent => SilentTypes,
        GeneratedCharacter.Defect => DefectTypes,
        GeneratedCharacter.Necrobinder => NecrobinderTypes,
        GeneratedCharacter.Regent => RegentTypes,
        GeneratedCharacter.Colorless => ColorlessTypes,
        _ => throw new ArgumentOutOfRangeException(nameof(character))
    };

    private static IReadOnlyList<Type> CharacterTypes(Type baseType) => typeof(ChaosCard000).Assembly.GetTypes()
        .Where(type => !type.IsAbstract && type.BaseType == baseType)
        .OrderBy(type => type.Name, StringComparer.Ordinal)
        .ToArray();
}
