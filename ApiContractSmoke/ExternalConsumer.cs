using AutoAnthony;
using ChaosCardGenerator;
using Godot;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace ApiContractSmoke;

public static class ExternalConsumer
{
    public static void CompileRegistration(IComponentCatalog catalog,
        IComponentRuntimeHandler handler, IComponentHoverTipProvider tips,
        IReadOnlyList<ComponentLocalizationRegistration> localizations)
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
                new ComponentKeywordUpgrade("api_smoke:operation", [CardTag.Innate], [CardTag.Exhaust],
                    AddedCustomKeywords: ["api_smoke:charged"])
            ],
            Valuations: [new ComponentValuationRegistration("api_smoke", "effect", new SmokeValuation())],
            IncludeInUltimateChaos: true,
            Keywords: [new ComponentKeywordDefinition("api_smoke:charged")],
            Localizations: localizations));
        ComponentRuntimeApi.RegisterPackage("api_smoke:runtime",
            [new ComponentRuntimeRoute("api_smoke", "effect", handler)]);
        ComponentPresentationApi.RegisterPackage("api_smoke:presentation",
            [new ComponentPresentationRoute("api_smoke", "effect", tips)]);
        ComponentKeywordRuntimeApi.RegisterPackage("api_smoke:keywords",
            [new ComponentKeywordRuntimeRegistration("api_smoke:charged", new SmokeKeywordAdapter())]);
        ExternalComponentCharacterApi.Register(new ExternalComponentCharacterRegistration(
            profileId, GeneratedCharacter.Ironclad, "api_smoke", new SmokeAncientRelicAdapter()));
        ExternalComponentCharacterApi.RegisterRuntime(new ExternalComponentCharacterRuntimeRegistration(
            profileId, 1, _ => typeof(SmokeExternalCard000), () => false,
            () => ModelDb.CardPool<SmokeCardPool>()));
        CardNameGenerator.RegisterExternalParts("api_smoke:names",
        [
            new ComponentCardNameParts("ApiSmokeCard", ["测", "试"], "", "Test", " Card")
        ]);
        _ = ComponentPresentationApi.RegisteredRoutes;
        _ = ComponentPresentationApi.RegisteredPackages;
        _ = ComponentKeywordRuntimeApi.RegisteredKeywordIds;
        _ = ComponentKeywordRuntimeApi.RegisteredPackages;
        _ = new RandomCardGenerator(request, 12345, balancedValues: true);
        _ = new RandomCardGenerator(new ComponentProfileRequest(profileId, GeneratedCharacter.Ironclad, true),
            12345, balancedValues: true);
        _ = ComponentRunSettingsApi.Local;
        _ = ComponentRunSettingsApi.TryResolveMultiplayer([], out _);
        using var progress = ComponentGenerationProgressApi.Create(1);
        progress.Report(1);
        _ = ComponentSurpriseApi.IsGenerated(null);
    }

    public static async Task CompileTriggerBridge(ChaosCardModel card, Player player,
        PlayerChoiceContext choiceContext)
    {
        _ = ComponentTriggerApi.HasCardTrigger(card, "api_smoke:trigger");
        _ = ComponentTriggerApi.EffectiveOperationAmount(card, 0);
        await ComponentTriggerApi.FireCardAsync(card, choiceContext, "api_smoke:trigger");
        await ComponentTriggerApi.FirePlayerAsync(player, choiceContext, "api_smoke:trigger");
    }
}

public sealed class SmokeKeywordAdapter : IComponentKeywordRuntimeAdapter
{
    public IEnumerable<IHoverTip> BuildHoverTips(ChaosCardModel card) => [];
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
