using AutoAnthony;
using ChaosCardGenerator;
using Godot;
using MegaCrit.Sts2.Core.HoverTips;
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
            ComponentApi.DefaultValuePolicy);
        ComponentPackageApi.Register(new ComponentPackageRegistration("api_smoke:components", request, profile,
            Valuations: [new ComponentValuationRegistration("api_smoke", "effect", new SmokeValuation())]));
        ComponentRuntimeApi.RegisterPackage("api_smoke:runtime",
            [new ComponentRuntimeRoute("api_smoke", "effect", handler)]);
        ComponentPresentationApi.Register("api_smoke", "effect", tips);
        ExternalComponentCharacterApi.Register(new ExternalComponentCharacterRegistration(
            profileId, GeneratedCharacter.Ironclad, "api_smoke"));
        _ = new RandomCardGenerator(request, 12345, balancedValues: true);
    }
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
