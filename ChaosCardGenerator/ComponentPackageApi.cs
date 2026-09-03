namespace ChaosCardGenerator;

public sealed record ComponentLocalizedText(
    string Template,
    string ChineseText,
    string EnglishText);

public sealed record ComponentLocalizationRegistration(
    string SemanticId,
    string ChineseTemplate,
    string EnglishTemplate,
    IReadOnlyList<OperationTextSlot>? TextSlots = null);

public sealed record ComponentKeywordUpgrade(
    string Template,
    IReadOnlyList<CardTag> Added,
    IReadOnlyList<CardTag> Removed,
    IReadOnlyList<string>? AddedCustomKeywords = null,
    IReadOnlyList<string>? RemovedCustomKeywords = null);

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
    bool IncludeInUltimateChaos = true,
    IReadOnlyList<ComponentKeywordDefinition>? Keywords = null,
    IReadOnlyList<ComponentLocalizationRegistration>? Localizations = null);

public static class ComponentPackageApi
{
    public const int ApiVersion = 3;
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
        var pendingLocalizationIds = (package.Localizations ?? [])
            .Select(localization => localization.SemanticId).ToHashSet(StringComparer.Ordinal);
        var pendingKeywordIds = (package.Keywords ?? [])
            .Select(keyword => keyword.KeywordId).ToHashSet(StringComparer.Ordinal);
        ComponentProfileValidator.Validate(package.Profile, pendingLocalizationIds, pendingKeywordIds);
        if (package.Profile.Character != package.Request.Character
            || package.Profile.UnlockComponentRoles != package.Request.UnlockComponentRoles)
            throw new ArgumentException("The package profile does not match its request.", nameof(package));

        var texts = package.LocalizedTexts?.ToArray() ?? [];
        var upgrades = package.KeywordUpgrades?.ToArray() ?? [];
        var multiplicities = package.Multiplicities?.ToArray() ?? [];
        var valuations = package.Valuations?.ToArray() ?? [];
        var keywords = package.Keywords?.ToArray() ?? [];
        var localizations = package.Localizations?.ToArray() ?? [];
        foreach (var text in texts)
        {
            ValidateId(text.Template, nameof(text.Template));
            if (string.IsNullOrWhiteSpace(text.ChineseText) || string.IsNullOrWhiteSpace(text.EnglishText))
                throw new ArgumentException("Localized component projections must provide Chinese and English text.",
                    nameof(package));
        }
        var atomsBySemanticId = package.Profile.ComponentCatalog.Atoms
            .Where(atom => atom.SemanticId is not null)
            .ToDictionary(atom => atom.SemanticId!, StringComparer.Ordinal);
        foreach (var localization in localizations)
        {
            ArgumentNullException.ThrowIfNull(localization);
            ValidateId(localization.SemanticId, nameof(localization.SemanticId));
            if (!atomsBySemanticId.TryGetValue(localization.SemanticId, out var atom))
                throw new ArgumentException(
                    $"Localization references unknown component '{localization.SemanticId}'.", nameof(package));
            var localized = new OperationLocalizedText(localization.ChineseTemplate,
                localization.EnglishTemplate, localization.TextSlots)
                .ValidateAndReturn(OperationRuntimeSpecCompiler.GetOrCompile(atom));
            if (!string.Equals(localized.RenderChinese(OperationRuntimeSpecCompiler.GetOrCompile(atom)),
                    atom.ChineseText, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Localization '{localization.SemanticId}' does not render its component's base Chinese text.",
                    nameof(package));
        }
        foreach (var upgrade in upgrades)
        {
            ValidateId(upgrade.Template, nameof(upgrade.Template));
            ComponentKeywordApi.ValidateIds(upgrade.AddedCustomKeywords, nameof(upgrade.AddedCustomKeywords));
            ComponentKeywordApi.ValidateIds(upgrade.RemovedCustomKeywords, nameof(upgrade.RemovedCustomKeywords));
        }
        foreach (var keyword in keywords)
        {
            ArgumentNullException.ThrowIfNull(keyword);
            ValidateId(keyword.KeywordId, nameof(keyword.KeywordId));
        }
        if (keywords.Select(keyword => keyword.KeywordId).Distinct(StringComparer.Ordinal).Count()
            != keywords.Length)
            throw new ArgumentException("A component package cannot repeat a keyword ID.", nameof(package));
        if (localizations.Select(localization => localization.SemanticId).Distinct(StringComparer.Ordinal).Count()
            != localizations.Length)
            throw new ArgumentException("A component package cannot repeat a localization ID.", nameof(package));
        var availableKeywordIds = ComponentKeywordApi.RegisteredKeywordIds
            .Concat(keywords.Select(keyword => keyword.KeywordId)).ToHashSet(StringComparer.Ordinal);
        var unknownKeyword = package.Profile.ShellCatalog.Recipes
            .SelectMany(recipe => recipe.CustomKeywords ?? [])
            .Concat(package.Profile.ComponentCatalog.Recipes.SelectMany(recipe => recipe.CustomKeywords ?? []))
            .Concat(package.Profile.NameCatalog.Recipes.SelectMany(recipe => recipe.CustomKeywords ?? []))
            .FirstOrDefault(keywordId => !availableKeywordIds.Contains(keywordId));
        if (unknownKeyword is not null)
            throw new ArgumentException($"Component profile references unregistered keyword '{unknownKeyword}'.",
                nameof(package));
        var unknownUpgradeKeyword = upgrades
            .SelectMany(upgrade => (upgrade.AddedCustomKeywords ?? [])
                .Concat(upgrade.RemovedCustomKeywords ?? []))
            .FirstOrDefault(keywordId => !availableKeywordIds.Contains(keywordId));
        if (unknownUpgradeKeyword is not null)
            throw new ArgumentException($"Keyword upgrade references unregistered keyword '{unknownUpgradeKeyword}'.",
                nameof(package));
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

        EnsureUnique(texts.Select(text => (text.Template, text.ChineseText)),
            "localized operation projection", nameof(package));
        EnsureUnique(multiplicities.Select(value => (value.Template, value.Variant)),
            "multiplicity route", nameof(package));
        EnsureUnique(valuations.Select(value => (value.Opcode, value.Variant)),
            "valuation route", nameof(package));

        lock (Sync)
        {
            if (ComponentApi.RegistrationsFrozen)
                throw new InvalidOperationException(
                    "Component packages must be registered before the first generation profile resolves.");
            if (Packages.Contains(package.PackageId))
                throw new InvalidOperationException($"Component package '{package.PackageId}' is already registered.");

            // Preflight every registry before mutating any of them. Package registration is an initialization
            // transaction: a late valuation/localization/profile collision must not leave a keyword or package ID
            // behind and make the next, corrected registration fail differently.
            ComponentKeywordApi.EnsureCanRegister(keywords);
            ComponentLocalizationApi.EnsureCanRegister(localizations.Select(localization =>
                (localization.SemanticId,
                    new OperationLocalizedText(localization.ChineseTemplate, localization.EnglishTemplate,
                        localization.TextSlots),
                    OperationRuntimeSpecCompiler.GetOrCompile(atomsBySemanticId[localization.SemanticId]))));
            ComponentPolicy.EnsureCanRegister(multiplicities);
            ComponentValuationApi.EnsureCanRegister(valuations);
            ExternalOperationTextRegistry.EnsureCanRegister(texts);
            ComponentApi.EnsureCanRegisterProfile(package.Request, package.Profile);
            if (package.IncludeInUltimateChaos && !package.Request.UnlockComponentRoles)
                ComponentApi.EnsureCanRegisterUltimateChaosContribution(package.PackageId);

            foreach (var keyword in keywords) ComponentKeywordApi.Register(keyword);
            foreach (var localization in localizations)
                ComponentLocalizationApi.Register(localization.SemanticId,
                    new OperationLocalizedText(localization.ChineseTemplate, localization.EnglishTemplate,
                        localization.TextSlots),
                    OperationRuntimeSpecCompiler.GetOrCompile(atomsBySemanticId[localization.SemanticId]));
            foreach (var multiplicity in multiplicities)
                ComponentPolicy.RegisterMultiplicity(multiplicity.Template, multiplicity.Multiplicity,
                    multiplicity.Variant);
            foreach (var valuation in valuations) ComponentValuationApi.Register(valuation);
            foreach (var text in texts)
                ExternalOperationTextRegistry.Register(text.Template, text.ChineseText, text.EnglishText);
            foreach (var upgrade in upgrades)
            {
                ExternalOperationUpgradeRegistry.Register(package.Request.ProfileId, upgrade.Template,
                    upgrade.Added, upgrade.Removed);
                ExternalCustomKeywordUpgradeRegistry.Register(package.Request.ProfileId, upgrade.Template,
                    upgrade.AddedCustomKeywords ?? [], upgrade.RemovedCustomKeywords ?? []);
            }
            ComponentApi.RegisterProfile(package.Request, package.Profile);
            if (package.IncludeInUltimateChaos && !package.Request.UnlockComponentRoles)
                ComponentApi.RegisterUltimateChaosContribution(package.PackageId, package.Profile.ComponentCatalog);
            Packages.Add(package.PackageId);
        }
    }

    private static void EnsureUnique(IEnumerable<(string First, string Second)> keys, string label,
        string parameterName)
    {
        var seen = new HashSet<(string First, string Second)>();
        foreach (var key in keys)
            if (!seen.Add(key))
                throw new ArgumentException($"A component package repeats {label} '{key.First}'/'{key.Second}'.",
                    parameterName);
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
