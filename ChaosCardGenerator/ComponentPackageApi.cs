namespace ChaosCardGenerator;

public sealed record ComponentLocalizedText(
    string Template,
    string ChineseText,
    string EnglishText);

public sealed record ComponentKeywordUpgrade(
    string Template,
    IReadOnlyList<CardTag> Added,
    IReadOnlyList<CardTag> Removed);

public sealed record ComponentMultiplicityRegistration(
    string Template,
    ComponentMultiplicity Multiplicity,
    string Variant = "");

/// <summary>
/// One initialization-time registration unit for an external component catalog. Runtime handlers are registered
/// separately in the game assembly because the pure generator intentionally has no dependency on sts2.dll.
/// </summary>
public sealed record ComponentPackageRegistration(
    string PackageId,
    ComponentProfileRequest Request,
    ComponentGenerationProfile Profile,
    IReadOnlyList<ComponentLocalizedText>? LocalizedTexts = null,
    IReadOnlyList<ComponentKeywordUpgrade>? KeywordUpgrades = null,
    IReadOnlyList<ComponentMultiplicityRegistration>? Multiplicities = null,
    IReadOnlyList<ComponentValuationRegistration>? Valuations = null,
    bool IncludeInUltimateChaos = true);

public static class ComponentPackageApi
{
    public const int ApiVersion = 2;
    private static readonly object Sync = new();
    private static readonly HashSet<string> Packages = new(StringComparer.Ordinal);

    public static IReadOnlyList<string> RegisteredPackages
    {
        get { lock (Sync) return Packages.OrderBy(value => value, StringComparer.Ordinal).ToArray(); }
    }

    public static void Register(ComponentPackageRegistration package)
    {
        ArgumentNullException.ThrowIfNull(package);
        ValidateId(package.PackageId, nameof(package.PackageId));
        ComponentProfileValidator.Validate(package.Profile);
        if (package.Profile.Character != package.Request.Character
            || package.Profile.UnlockComponentRoles != package.Request.UnlockComponentRoles)
            throw new ArgumentException("The package profile does not match its request.", nameof(package));

        var texts = package.LocalizedTexts?.ToArray() ?? [];
        var upgrades = package.KeywordUpgrades?.ToArray() ?? [];
        var multiplicities = package.Multiplicities?.ToArray() ?? [];
        var valuations = package.Valuations?.ToArray() ?? [];
        foreach (var text in texts)
        {
            ValidateId(text.Template, nameof(text.Template));
            if (string.IsNullOrWhiteSpace(text.ChineseText) || string.IsNullOrWhiteSpace(text.EnglishText))
                throw new ArgumentException("Localized component projections must provide Chinese and English text.",
                    nameof(package));
        }
        foreach (var upgrade in upgrades) ValidateId(upgrade.Template, nameof(upgrade.Template));
        foreach (var multiplicity in multiplicities)
        {
            ValidateId(multiplicity.Template, nameof(multiplicity.Template));
            ValidateOptionalId(multiplicity.Variant, nameof(multiplicity.Variant));
        }
        foreach (var valuation in valuations)
        {
            ArgumentNullException.ThrowIfNull(valuation);
            ValidateId(valuation.Opcode, nameof(valuation.Opcode));
            ValidateOptionalId(valuation.Variant, nameof(valuation.Variant));
            ArgumentNullException.ThrowIfNull(valuation.Valuation);
        }

        lock (Sync)
        {
            if (ComponentApi.RegistrationsFrozen)
                throw new InvalidOperationException(
                    "Component packages must be registered before the first generation profile resolves.");
            if (!Packages.Add(package.PackageId))
                throw new InvalidOperationException($"Component package '{package.PackageId}' is already registered.");

            foreach (var multiplicity in multiplicities)
                ComponentPolicy.RegisterMultiplicity(multiplicity.Template, multiplicity.Multiplicity,
                    multiplicity.Variant);
            foreach (var valuation in valuations) ComponentValuationApi.Register(valuation);
            foreach (var text in texts)
                ExternalOperationTextRegistry.Register(text.Template, text.ChineseText, text.EnglishText);
            foreach (var upgrade in upgrades)
                ExternalOperationUpgradeRegistry.Register(package.Request.ProfileId, upgrade.Template,
                    upgrade.Added, upgrade.Removed);
            ComponentApi.RegisterProfile(package.Request, package.Profile);
            if (package.IncludeInUltimateChaos && !package.Request.UnlockComponentRoles)
                ComponentApi.RegisterUltimateChaosContribution(package.PackageId, package.Profile.ComponentCatalog);
        }
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => character > 0x7f))
            throw new ArgumentException("Component package IDs must be non-empty ASCII strings.", name);
    }

    private static void ValidateOptionalId(string value, string name)
    {
        if (value.Any(character => character > 0x7f))
            throw new ArgumentException("Component package variant IDs must be ASCII strings.", name);
    }
}
