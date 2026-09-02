using AutoAnthony;
using ChaosCardGenerator;
using Godot;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace ApiContractSmoke;

public static class ExternalConsumer
{
    public static void CompileRegistration(IComponentCatalog catalog,
        IComponentRuntimeHandler handler, IComponentHoverTipProvider tips)
    {
        const string profileId = "api_smoke:character";
        var request = new ComponentProfileRequest(profileId, GeneratedCharacter.Ironclad, false);
        var profile = new ComponentGenerationProfile(profileId + ":normal", GeneratedCharacter.Ironclad, false,
            catalog, catalog, catalog, () => ComponentApi.CreateNativeOccurrencePolicy(catalog),
            ComponentApi.DefaultValuePolicy,
            new ComponentKeywordPolicy(
                AllowedBaseKeywords: new HashSet<CardTag> { CardTag.Exhaust, CardTag.Innate },
                AllowedUpgradeAdditions: new HashSet<CardTag> { CardTag.Innate },
                AllowedUpgradeRemovals: new HashSet<CardTag> { CardTag.Exhaust },
                GlobalUpgradeAdditions: new HashSet<CardTag> { CardTag.Innate },
                UseArchetypeUpgradeDefaults: false));
        ComponentPackageApi.Register(new ComponentPackageRegistration("api_smoke:components", request, profile,
            KeywordUpgrades:
            [
                new ComponentKeywordUpgrade("api_smoke:operation", [CardTag.Innate], [CardTag.Exhaust])
            ],
            Valuations: [new ComponentValuationRegistration("api_smoke", "effect", new SmokeValuation())],
            IncludeInUltimateChaos: true));
        ComponentRuntimeApi.RegisterPackage("api_smoke:runtime",
            [new ComponentRuntimeRoute("api_smoke", "effect", handler)]);
        ComponentPresentationApi.Register("api_smoke", "effect", tips);
        ExternalComponentCharacterApi.Register(new ExternalComponentCharacterRegistration(
            profileId, GeneratedCharacter.Ironclad, "api_smoke", new SmokeAncientRelicAdapter()));
        _ = new RandomCardGenerator(request, 12345, balancedValues: true);
        _ = new RandomCardGenerator(new ComponentProfileRequest(profileId, GeneratedCharacter.Ironclad, true),
            12345, balancedValues: true);
    }
}

public sealed class SmokeAncientRelicAdapter : IExternalAncientRelicAdapter
{
    public bool AppliesTo(Player player) => false;
    public bool ShouldOverrideArchaicTooth(Player player) => false;
    public bool ShouldOverrideDustyTome(Player player) => false;
    public CardModel AncientCard(Player player, int index) => throw new NotSupportedException();
}

public sealed class SmokeValuation : IComponentValuation
{
    public int Estimate(ComponentValuationContext context) =>
        context.FirstExplicitFixedValue() * 100;
}

public sealed class SmokeRuntimeHandler : IComponentRuntimeHandler
{
    public Task<bool> ExecuteAsync(ComponentRuntimeContext context) => Task.FromResult(true);
}

public sealed class SmokeHoverTipProvider : IComponentHoverTipProvider
{
    public IEnumerable<IHoverTip> BuildHoverTips(ComponentPresentationContext context) => [];
}

public sealed class SmokeCardPool : CardPoolModel
{
    public override string Title => "api_smoke";
    public override string EnergyColorName => "ironclad";
    public override string CardFrameMaterialPath => "card_frame_red";
    public override Color DeckEntryCardColor => Colors.Red;
    public override bool IsColorless => false;
    protected override CardModel[] GenerateAllCards() => [];
}

public abstract class SmokeExternalCard : ExternalChaosCardModel
{
    protected sealed override string ComponentProfileId => "api_smoke:character";
    public sealed override CardPoolModel Pool => ModelDb.CardPool<SmokeCardPool>();
}

public sealed class SmokeExternalCard000 : SmokeExternalCard
{
    protected override int Slot => 0;
}
