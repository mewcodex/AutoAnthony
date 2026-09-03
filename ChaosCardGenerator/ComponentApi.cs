namespace ChaosCardGenerator;

/// <summary>
/// Stable flags that let external components participate in generator legality without adding Template-specific
/// branches to AutoAnthony. Flags describe composition behavior only; execution and value remain opcode routes.
/// </summary>
public static class ComponentSemanticFlags
{
    public const string Beneficial = "api_beneficial";
    public const string Negative = "api_negative";
    public const string Restricted = "api_restricted";
    public const string EnemyDamage = "api_enemy_damage";
    public const string PowerFoundation = "api_power_foundation";
    public const string ScalableReward = "api_scalable_reward";
    public const string SelfCardMovement = "api_self_card_movement";
}

/// <summary>
/// Immutable request used to resolve the component inventory and policies for one generated pool.
/// Ultimate Chaos is a profile choice, not a second set of value rules hidden in the assembler.
/// </summary>
public readonly record struct ComponentProfileRequest
{
    public string ProfileId { get; }
    public GeneratedCharacter Character { get; }
    public bool UnlockComponentRoles { get; }

    public ComponentProfileRequest(GeneratedCharacter character, bool UnlockComponentRoles)
        : this(BuiltInId(character), character, UnlockComponentRoles) { }

    public ComponentProfileRequest(string profileId, GeneratedCharacter character, bool unlockComponentRoles)
    {
        ValidateAsciiId(profileId, nameof(profileId));
        ProfileId = profileId;
        Character = character;
        UnlockComponentRoles = unlockComponentRoles;
    }

    public static string BuiltInId(GeneratedCharacter character) =>
        $"autoanthony:{character.ToString().ToLowerInvariant()}";

    private static void ValidateAsciiId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => character > 0x7f))
            throw new ArgumentException("Component profile IDs must be non-empty ASCII strings.", name);
    }
}

/// <summary>
/// Mutable, pool-scoped occurrence policy. It observes finalized cards only; speculative assembly attempts must
/// never be reported through <see cref="Observe"/>.
/// </summary>
public interface IComponentOccurrencePolicy
{
    int SourcePriorWeight(GeneratedRarity rarity, GeneratedCardType type, string family,
        IReadOnlyList<GeneratorOperation> currentCard);

    int SelectionWeight(GeneratedRarity rarity, GeneratedCardType type, string family,
        IReadOnlyList<GeneratorOperation> currentCard);

    int VariantWeight(ComponentAtom atom, GeneratedRarity rarity, GeneratedCardType type, TargetMode target,
        IReadOnlyList<GeneratorOperation> currentCard);

    void Observe(GeneratedCard card);
}

/// <summary>
/// Numeric parameter policy consumed by component assembly. Keeping this contract separate from occurrence
/// selection prevents a value calibration change from silently becoming a component-frequency change.
/// </summary>
public interface IComponentValuePolicy
{
    bool IsScalableReward(ComponentAtom atom);
    int OriginalValueChance(ComponentAtom atom, int benefitLines);
    int SampleAroundCenter(Random random, int center, int payoffScale);
    int AdjustSampledValue(Random random, ComponentAtom atom, int slot, int value);
    int ClampSampledValue(ComponentAtom atom, int slot, int value,
        IReadOnlyList<GeneratorOperation> previous, GeneratedCharacter? character, bool ultimateChaos);
    int ApplyValueBonuses(ComponentAtom atom, int value, GeneratedCharacter character, bool ultimateChaos);
    int PayoffScalePercent(IReadOnlyList<GeneratorOperation> previous);
    int ScaleRewardCenter(ComponentAtom atom, int center, int benefitLines, GeneratedRarity rarity,
        IReadOnlyList<GeneratorOperation> previous, Random random, bool balancedValues);
}

/// <summary>
/// Profile-local control over native and custom card keywords. Null allow-lists mean all registered keywords.
/// Custom keyword identity is a stable ASCII ID; its gameplay value remains an ordinary structured component.
/// </summary>
public sealed record ComponentKeywordPolicy(
    IReadOnlySet<CardTag>? AllowedBaseKeywords = null,
    IReadOnlySet<CardTag>? AllowedUpgradeAdditions = null,
    IReadOnlySet<CardTag>? AllowedUpgradeRemovals = null,
    IReadOnlySet<CardTag>? GlobalUpgradeAdditions = null,
    IReadOnlySet<CardTag>? GlobalUpgradeRemovals = null,
    bool UseArchetypeUpgradeDefaults = true,
    IReadOnlySet<string>? AllowedCustomBaseKeywords = null,
    IReadOnlySet<string>? AllowedCustomUpgradeAdditions = null,
    IReadOnlySet<string>? AllowedCustomUpgradeRemovals = null,
    IReadOnlySet<string>? GlobalCustomUpgradeAdditions = null,
    IReadOnlySet<string>? GlobalCustomUpgradeRemovals = null)
{
    public static ComponentKeywordPolicy Default { get; } = new();

    // Semantic mechanic tags are not governed by a keyword policy and remain reachable from their source shells.
    public bool AllowsBase(CardTag tag) => GeneratedCardTagPolicy.IsSemanticTag(tag)
        || GeneratedCardTagPolicy.IsNativeKeyword(tag) && AllowedBaseKeywords?.Contains(tag) != false;
    public bool AllowsAddition(CardTag tag) => GeneratedCardTagPolicy.IsNativeKeyword(tag)
        && AllowedUpgradeAdditions?.Contains(tag) != false;
    public bool AllowsRemoval(CardTag tag) => GeneratedCardTagPolicy.IsNativeKeyword(tag)
        && AllowedUpgradeRemovals?.Contains(tag) != false;
    public bool AllowsCustomBase(string keywordId) => AllowedCustomBaseKeywords?.Contains(keywordId) != false;
    public bool AllowsCustomAddition(string keywordId) => AllowedCustomUpgradeAdditions?.Contains(keywordId) != false;
    public bool AllowsCustomRemoval(string keywordId) => AllowedCustomUpgradeRemovals?.Contains(keywordId) != false;

    public void Validate()
    {
        GeneratedCardTagPolicy.ValidateKeywordSet(AllowedBaseKeywords, nameof(AllowedBaseKeywords));
        GeneratedCardTagPolicy.ValidateKeywordSet(AllowedUpgradeAdditions, nameof(AllowedUpgradeAdditions));
        GeneratedCardTagPolicy.ValidateKeywordSet(AllowedUpgradeRemovals, nameof(AllowedUpgradeRemovals));
        GeneratedCardTagPolicy.ValidateKeywordSet(GlobalUpgradeAdditions, nameof(GlobalUpgradeAdditions));
        GeneratedCardTagPolicy.ValidateKeywordSet(GlobalUpgradeRemovals, nameof(GlobalUpgradeRemovals));
        ComponentKeywordApi.ValidateIds(AllowedCustomBaseKeywords, nameof(AllowedCustomBaseKeywords));
        ComponentKeywordApi.ValidateIds(AllowedCustomUpgradeAdditions, nameof(AllowedCustomUpgradeAdditions));
        ComponentKeywordApi.ValidateIds(AllowedCustomUpgradeRemovals, nameof(AllowedCustomUpgradeRemovals));
        ComponentKeywordApi.ValidateIds(GlobalCustomUpgradeAdditions, nameof(GlobalCustomUpgradeAdditions));
        ComponentKeywordApi.ValidateIds(GlobalCustomUpgradeRemovals, nameof(GlobalCustomUpgradeRemovals));
    }
}

/// <summary>Immutable context supplied to custom keyword legality rules during one card assembly.</summary>
public sealed record ComponentKeywordGenerationContext(
    string ProfileId,
    GeneratedCharacter Character,
    GeneratedRarity Rarity,
    GeneratedCardType Type,
    int EnergyCost,
    int StarCost,
    bool HasStarCostX,
    IReadOnlyList<GeneratorOperation> Operations,
    IReadOnlyList<CardTag> NativeTags,
    IReadOnlyList<string> SelectedCustomKeywords);

/// <summary>
/// Optional pure-generator legality for an external keyword. Runtime behavior and hover tips belong to the game
/// assembly's ComponentKeywordRuntimeApi; this interface must remain deterministic and side-effect free.
/// </summary>
public interface IComponentKeywordRule
{
    bool CanAttach(ComponentKeywordGenerationContext context) => true;
    bool CanUpgradeAdd(GeneratedCard card) => true;
    bool CanUpgradeRemove(GeneratedCard card) => true;
}

public sealed record ComponentKeywordDefinition(string KeywordId, IComponentKeywordRule? Rule = null);

/// <summary>Stable ASCII keyword registry shared by source recipes, upgrade generation and snapshots.</summary>
public static class ComponentKeywordApi
{
    public const int ApiVersion = 1;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, ComponentKeywordDefinition> Definitions =
        new(StringComparer.Ordinal);

    public static IReadOnlyList<string> RegisteredKeywordIds
    {
        get { lock (Sync) return Definitions.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray(); }
    }

    public static void Register(ComponentKeywordDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateId(definition.KeywordId, nameof(definition.KeywordId));
        lock (Sync)
        {
            if (ComponentApi.RegistrationsFrozen)
                throw new InvalidOperationException(
                    "Keyword registration must finish before the first generation profile resolves.");
            if (!Definitions.TryAdd(definition.KeywordId, definition))
                throw new InvalidOperationException($"Keyword '{definition.KeywordId}' is already registered.");
        }
    }

    public static bool IsRegistered(string keywordId)
    {
        ValidateId(keywordId, nameof(keywordId));
        lock (Sync) return Definitions.ContainsKey(keywordId);
    }

    internal static void EnsureCanRegister(IEnumerable<ComponentKeywordDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var values = definitions.ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in values)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ValidateId(definition.KeywordId, nameof(definition.KeywordId));
            if (!ids.Add(definition.KeywordId))
                throw new ArgumentException($"Keyword registration repeats '{definition.KeywordId}'.",
                    nameof(definitions));
        }
        lock (Sync)
        {
            if (ComponentApi.RegistrationsFrozen)
                throw new InvalidOperationException(
                    "Keyword registration must finish before the first generation profile resolves.");
            var conflict = values.FirstOrDefault(value => Definitions.ContainsKey(value.KeywordId));
            if (conflict is not null)
                throw new InvalidOperationException($"Keyword '{conflict.KeywordId}' is already registered.");
        }
    }

    internal static bool CanAttach(string keywordId, ComponentKeywordGenerationContext context)
    {
        lock (Sync)
            return Definitions.TryGetValue(keywordId, out var definition)
                && (definition.Rule?.CanAttach(context) ?? true);
    }

    internal static bool CanUpgradeAdd(string keywordId, GeneratedCard card)
    {
        lock (Sync)
            return Definitions.TryGetValue(keywordId, out var definition)
                && (definition.Rule?.CanUpgradeAdd(card) ?? true);
    }

    internal static bool CanUpgradeRemove(string keywordId, GeneratedCard card)
    {
        lock (Sync)
            return Definitions.TryGetValue(keywordId, out var definition)
                && (definition.Rule?.CanUpgradeRemove(card) ?? true);
    }

    internal static void ValidateIds(IEnumerable<string>? keywordIds, string field)
    {
        if (keywordIds is null) return;
        foreach (var keywordId in keywordIds) ValidateId(keywordId, field);
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => character > 0x7f))
            throw new ArgumentException("Keyword IDs must be non-empty ASCII strings.", name);
    }
}

/// <summary>
/// Complete read-only input package for card assembly. Catalogs own component implementation data; the two policy
/// interfaces own occurrence and numeric control. Naming is deliberately scoped separately so Ultimate Chaos can
/// share mechanics while retaining the current character's name corpus.
/// </summary>
public sealed class ComponentGenerationProfile
{
    private readonly Func<IComponentOccurrencePolicy> _occurrenceFactory;

    public string Id { get; }
    public GeneratedCharacter Character { get; }
    public bool UnlockComponentRoles { get; }
    public IComponentCatalog ShellCatalog { get; }
    public IComponentCatalog ComponentCatalog { get; }
    public IComponentCatalog NameCatalog { get; }
    public IComponentValuePolicy ValuePolicy { get; }
    public ComponentKeywordPolicy KeywordPolicy { get; }

    public ComponentGenerationProfile(string id, GeneratedCharacter character, bool unlockComponentRoles,
        IComponentCatalog shellCatalog, IComponentCatalog componentCatalog, IComponentCatalog nameCatalog,
        Func<IComponentOccurrencePolicy> occurrenceFactory, IComponentValuePolicy valuePolicy,
        ComponentKeywordPolicy? keywordPolicy = null)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(value => value > 0x7f))
            throw new ArgumentException("Component profile IDs must be non-empty ASCII strings.", nameof(id));
        Id = id;
        Character = character;
        UnlockComponentRoles = unlockComponentRoles;
        ShellCatalog = shellCatalog ?? throw new ArgumentNullException(nameof(shellCatalog));
        ComponentCatalog = componentCatalog ?? throw new ArgumentNullException(nameof(componentCatalog));
        NameCatalog = nameCatalog ?? throw new ArgumentNullException(nameof(nameCatalog));
        _occurrenceFactory = occurrenceFactory ?? throw new ArgumentNullException(nameof(occurrenceFactory));
        ValuePolicy = valuePolicy ?? throw new ArgumentNullException(nameof(valuePolicy));
        KeywordPolicy = keywordPolicy ?? ComponentKeywordPolicy.Default;
        KeywordPolicy.Validate();
        if (ShellCatalog.Character != character || NameCatalog.Character != character)
            throw new ArgumentException("Shell and name catalogs must belong to the requested character.");
    }

    public IComponentOccurrencePolicy CreateOccurrencePolicy() =>
        _occurrenceFactory() ?? throw new InvalidOperationException($"Profile {Id} returned no occurrence policy.");
}

/// <summary>Providers are queried in reverse registration order; the most recently registered match wins.</summary>
public interface IComponentProfileProvider
{
    bool TryCreate(ComponentProfileRequest request, out ComponentGenerationProfile profile);
}

/// <summary>
/// Registration boundary for built-in and future external component packages. Registration is intentionally frozen
/// on first resolution so a run cannot change catalogs or balance policy halfway through pool generation.
/// </summary>
public static class ComponentApi
{
    public const int ApiVersion = 3;
    private static readonly object Sync = new();
    private static readonly List<IComponentProfileProvider> Providers = [new BuiltInComponentProfileProvider()];
    private static readonly Dictionary<ComponentProfileRequest, ComponentGenerationProfile> Registered = new();
    private static readonly Dictionary<ComponentProfileRequest, ComponentGenerationProfile> Resolved = new();
    private static readonly Dictionary<string, IComponentCatalog> UltimateChaosContributions =
        new(StringComparer.Ordinal);
    private static bool _frozen;

    /// <summary>The built-in numeric model, exposed for external profiles that only customize component content.</summary>
    public static IComponentValuePolicy DefaultValuePolicy => BuiltInComponentValuePolicy.Instance;

    /// <summary>
    /// Creates the same pool-scoped native-frequency policy used by built-in profiles. Call this from a profile's
    /// occurrence factory so every generated pool receives independent feedback state.
    /// </summary>
    public static IComponentOccurrencePolicy CreateNativeOccurrencePolicy(IComponentCatalog catalog,
        bool unlockComponentRoles = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new NativeComponentFrequencyTracker(catalog, unlockComponentRoles);
    }

    /// <summary>
    /// Builds a catalog from complete source-pool recipes. Repeated recipes are intentionally retained: Ultimate
    /// Chaos uses those occurrences as its pool-size-weighted prior, while structural atom indexes remain deduped.
    /// </summary>
    public static IComponentCatalog ComposeCatalog(GeneratedCharacter balanceArchetype,
        params IComponentCatalog[] catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        if (catalogs.Length == 0 || catalogs.Any(catalog => catalog is null))
            throw new ArgumentException("At least one non-null component catalog is required.", nameof(catalogs));
        return new ImmutableComponentCatalog(balanceArchetype,
            catalogs.SelectMany(catalog => catalog.Recipes));
    }

    /// <summary>
    /// Adds one external character's reviewed native pool to the shared Ultimate Chaos prior. Package IDs prevent
    /// accidental double registration. Registration freezes with profiles so every peer sees one immutable set.
    /// </summary>
    public static void RegisterUltimateChaosContribution(string packageId, IComponentCatalog catalog)
    {
        ValidateApiId(packageId, nameof(packageId));
        ArgumentNullException.ThrowIfNull(catalog);
        lock (Sync)
        {
            if (_frozen)
                throw new InvalidOperationException(
                    "Ultimate Chaos contributions must be registered before the first profile resolves.");
            if (!UltimateChaosContributions.TryAdd(packageId, catalog))
                throw new InvalidOperationException(
                    $"An Ultimate Chaos contribution is already registered for '{packageId}'.");
        }
    }

    public static bool RegistrationsFrozen
    {
        get { lock (Sync) return _frozen; }
    }

    public static IReadOnlyList<ComponentProfileRequest> RegisteredProfiles
    {
        get { lock (Sync) return Registered.Keys.OrderBy(key => key.ProfileId, StringComparer.Ordinal).ToArray(); }
    }

    public static void RegisterProfileProvider(IComponentProfileProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Sync)
        {
            if (_frozen)
                throw new InvalidOperationException(
                    "Component profile registration must finish before the first card pool is generated.");
            Providers.Add(provider);
        }
    }

    /// <summary>
    /// Registers one already-normalized profile without requiring an adapter provider. The request's Character is
    /// a balance/mechanics archetype; ProfileId is the external character/profile identity and keeps registrations
    /// distinct even when several mods reuse the same archetype.
    /// </summary>
    public static void RegisterProfile(ComponentProfileRequest request, ComponentGenerationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (Sync)
        {
            if (_frozen)
                throw new InvalidOperationException(
                    "Component profile registration must finish before the first card pool is generated.");
            if (profile.Character != request.Character
                || profile.UnlockComponentRoles != request.UnlockComponentRoles)
                throw new ArgumentException($"Profile {profile.Id} does not match {request}.", nameof(profile));
            ComponentProfileValidator.Validate(profile);
            if (!Registered.TryAdd(request, profile))
                throw new InvalidOperationException($"A component profile is already registered for {request}.");
        }
    }

    internal static void EnsureCanRegisterProfile(ComponentProfileRequest request,
        ComponentGenerationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (Sync)
        {
            if (_frozen)
                throw new InvalidOperationException(
                    "Component profile registration must finish before the first card pool is generated.");
            if (profile.Character != request.Character
                || profile.UnlockComponentRoles != request.UnlockComponentRoles)
                throw new ArgumentException($"Profile {profile.Id} does not match {request}.", nameof(profile));
            if (Registered.ContainsKey(request))
                throw new InvalidOperationException($"A component profile is already registered for {request}.");
        }
    }

    internal static void EnsureCanRegisterUltimateChaosContribution(string packageId)
    {
        ValidateApiId(packageId, nameof(packageId));
        lock (Sync)
        {
            if (_frozen)
                throw new InvalidOperationException(
                    "Ultimate Chaos contributions must be registered before the first profile resolves.");
            if (UltimateChaosContributions.ContainsKey(packageId))
                throw new InvalidOperationException(
                    $"An Ultimate Chaos contribution is already registered for '{packageId}'.");
        }
    }

    public static ComponentGenerationProfile Resolve(ComponentProfileRequest request)
    {
        lock (Sync)
        {
            _frozen = true;
            ComponentValuationApi.FreezeRegistrations();
            if (Resolved.TryGetValue(request, out var cached)) return cached;
            if (Registered.TryGetValue(request, out var registered))
            {
                registered = AddUltimateChaosContributions(request, registered);
                ComponentPolicy.FreezeRegistrations();
                Resolved.Add(request, registered);
                return registered;
            }
            // External characters normally register only their native profile. Derive the Ultimate profile from
            // that shell/name identity and the shared weighted component inventory.
            if (request.UnlockComponentRoles
                && Registered.TryGetValue(new ComponentProfileRequest(request.ProfileId, request.Character, false),
                    out var nativeExternal))
            {
                var builtInUltimate = CharacterComponentCatalogs.Get(request.Character,
                    unlockComponentRoles: true);
                var combined = ComposeCatalog(request.Character,
                    new[] { builtInUltimate }.Concat(OrderedUltimateChaosContributions()).ToArray());
                var derived = new ComponentGenerationProfile(
                    nativeExternal.Id + ":ultimate",
                    request.Character,
                    true,
                    combined,
                    combined,
                    nativeExternal.NameCatalog,
                    () => CreateNativeOccurrencePolicy(combined, unlockComponentRoles: true),
                    nativeExternal.ValuePolicy,
                    nativeExternal.KeywordPolicy);
                ComponentProfileValidator.Validate(derived);
                ComponentPolicy.FreezeRegistrations();
                Resolved.Add(request, derived);
                return derived;
            }
            for (var index = Providers.Count - 1; index >= 0; index--)
            {
                if (!Providers[index].TryCreate(request, out var profile)) continue;
                if (profile.Character != request.Character
                    || profile.UnlockComponentRoles != request.UnlockComponentRoles)
                    throw new InvalidOperationException(
                        $"Component provider returned mismatched profile {profile.Id} for {request}.");
                profile = AddUltimateChaosContributions(request, profile);
                ComponentProfileValidator.Validate(profile);
                ComponentPolicy.FreezeRegistrations();
                Resolved.Add(request, profile);
                return profile;
            }
            throw new InvalidOperationException($"No component generation profile is registered for {request}.");
        }
    }

    private static ComponentGenerationProfile AddUltimateChaosContributions(ComponentProfileRequest request,
        ComponentGenerationProfile profile)
    {
        if (!request.UnlockComponentRoles || UltimateChaosContributions.Count == 0) return profile;
        var combined = ComposeCatalog(request.Character,
            new[] { profile.ComponentCatalog }.Concat(OrderedUltimateChaosContributions()).ToArray());
        return new ComponentGenerationProfile(
            profile.Id + ":external-union",
            profile.Character,
            true,
            combined,
            combined,
            profile.NameCatalog,
            () => CreateNativeOccurrencePolicy(combined, unlockComponentRoles: true),
            profile.ValuePolicy,
            profile.KeywordPolicy);
    }

    private static IEnumerable<IComponentCatalog> OrderedUltimateChaosContributions() =>
        UltimateChaosContributions.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value);

    private static void ValidateApiId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => character > 0x7f))
            throw new ArgumentException("Component API IDs must be non-empty ASCII strings.", name);
    }
}

/// <summary>Shared pre-generation validation for built-in and externally supplied profiles.</summary>
public static class ComponentProfileValidator
{
    public static void Validate(ComponentGenerationProfile profile) => Validate(profile, null, null);

    internal static void Validate(ComponentGenerationProfile profile,
        IReadOnlySet<string>? pendingLocalizationIds,
        IReadOnlySet<string>? pendingKeywordIds)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.ShellCatalog.Character != profile.Character
            || profile.ComponentCatalog.Character != profile.Character
            || profile.NameCatalog.Character != profile.Character)
            throw new InvalidDataException($"Profile {profile.Id} contains a catalog for another character.");
        if (profile.ShellCatalog.Recipes.Count == 0 || profile.ComponentCatalog.Atoms.Count == 0
            || profile.NameCatalog.Recipes.Count == 0)
            throw new InvalidDataException($"Profile {profile.Id} contains an empty required catalog.");

        var semanticIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var atom in profile.ComponentCatalog.Atoms)
        {
            ValidateAscii(atom.Template, $"{profile.Id} component template", required: true);
            ValidateAscii(atom.SemanticId, $"{profile.Id} component semantic ID", required: true);
            if (!semanticIds.Add(atom.SemanticId!))
                throw new InvalidDataException($"Profile {profile.Id} repeats semantic ID {atom.SemanticId}.");
            var spec = atom.RuntimeSpec ?? throw new InvalidDataException(
                $"Profile {profile.Id} component {atom.SemanticId} has no RuntimeSpec.");
            spec.Validate();
            if (atom.LocalizedText is null
                && !ComponentLocalizationApi.TryGet(atom.SemanticId, out _)
                && pendingLocalizationIds?.Contains(atom.SemanticId!) != true)
                throw new InvalidDataException(
                    $"Profile {profile.Id} component {atom.SemanticId} has no named localization template.");
            atom.LocalizedText?.Validate(spec);
            if (atom.LocalizedText is { } localized
                && !string.Equals(localized.RenderChinese(spec), atom.ChineseText, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Profile {profile.Id} component {atom.SemanticId} localization does not reproduce its Chinese projection.");
        }

        foreach (var recipe in profile.ShellCatalog.Recipes)
        {
            ValidateAscii(recipe.Id, $"{profile.Id} recipe ID", required: true);
            if (recipe.Atoms.Count == 0 || recipe.Atoms.Count != recipe.TriggerOwners.Count)
                throw new InvalidDataException(
                    $"Profile {profile.Id} recipe {recipe.Id} has invalid component/trigger-owner counts.");
            for (var index = 0; index < recipe.Atoms.Count; index++)
            {
                var owner = recipe.TriggerOwners[index];
                if (owner < -1 || owner >= index)
                    throw new InvalidDataException(
                        $"Profile {profile.Id} recipe {recipe.Id} has invalid trigger owner {owner} at {index}.");
                var shellAtom = recipe.Atoms[index];
                if (shellAtom.RuntimeSpec is null || string.IsNullOrWhiteSpace(shellAtom.SemanticId))
                    throw new InvalidDataException(
                        $"Profile {profile.Id} recipe {recipe.Id} contains an unstructured shell component at {index}.");
                if (!profile.ComponentCatalog.AtomKeys.Contains(shellAtom.Key))
                    throw new InvalidDataException(
                        $"Profile {profile.Id} recipe {recipe.Id} references an unavailable component at {index}.");
            }
        }

        var availableKeywordIds = ComponentKeywordApi.RegisteredKeywordIds
            .Concat(pendingKeywordIds is { } pending ? pending : Enumerable.Empty<string>())
            .ToHashSet(StringComparer.Ordinal);
        var referencedKeywordIds = profile.ShellCatalog.Recipes
            .Concat(profile.ComponentCatalog.Recipes)
            .Concat(profile.NameCatalog.Recipes)
            .SelectMany(recipe => recipe.CustomKeywords ?? Array.Empty<string>())
            .Concat(profile.KeywordPolicy.AllowedCustomBaseKeywords is { } allowedBase
                ? allowedBase : Enumerable.Empty<string>())
            .Concat(profile.KeywordPolicy.AllowedCustomUpgradeAdditions is { } allowedAdditions
                ? allowedAdditions : Enumerable.Empty<string>())
            .Concat(profile.KeywordPolicy.AllowedCustomUpgradeRemovals is { } allowedRemovals
                ? allowedRemovals : Enumerable.Empty<string>())
            .Concat(profile.KeywordPolicy.GlobalCustomUpgradeAdditions is { } globalAdditions
                ? globalAdditions : Enumerable.Empty<string>())
            .Concat(profile.KeywordPolicy.GlobalCustomUpgradeRemovals is { } globalRemovals
                ? globalRemovals : Enumerable.Empty<string>())
            .Distinct(StringComparer.Ordinal);
        var unknownKeyword = referencedKeywordIds.FirstOrDefault(id => !availableKeywordIds.Contains(id));
        if (unknownKeyword is not null)
            throw new InvalidDataException(
                $"Profile {profile.Id} references unregistered custom keyword '{unknownKeyword}'.");
    }

    private static void ValidateAscii(string? value, string label, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"{label} must not be empty.");
        if (value?.Any(character => character > 0x7f) == true)
            throw new InvalidDataException($"{label} must be ASCII.");
    }
}

internal sealed class BuiltInComponentProfileProvider : IComponentProfileProvider
{
    public bool TryCreate(ComponentProfileRequest request, out ComponentGenerationProfile profile)
    {
        if (!string.Equals(request.ProfileId, ComponentProfileRequest.BuiltInId(request.Character),
                StringComparison.Ordinal))
        {
            profile = null!;
            return false;
        }
        var nameCatalog = CharacterComponentCatalogs.Get(request.Character);
        var componentCatalog = CharacterComponentCatalogs.Get(request.Character, request.UnlockComponentRoles);
        profile = new ComponentGenerationProfile(
            $"autoanthony:{request.Character}:{(request.UnlockComponentRoles ? "ultimate" : "native")}",
            request.Character,
            request.UnlockComponentRoles,
            componentCatalog,
            componentCatalog,
            nameCatalog,
            () => new NativeComponentFrequencyTracker(componentCatalog, request.UnlockComponentRoles),
            BuiltInComponentValuePolicy.Instance);
        return true;
    }
}

internal sealed class BuiltInComponentValuePolicy : IComponentValuePolicy
{
    internal static BuiltInComponentValuePolicy Instance { get; } = new();

    public bool IsScalableReward(ComponentAtom atom) => EffectBalanceModel.IsScalableReward(atom);
    public int OriginalValueChance(ComponentAtom atom, int benefitLines) =>
        NumericGenerationTuning.OriginalValueChance(atom, benefitLines);
    public int SampleAroundCenter(Random random, int center, int payoffScale) =>
        NumericGenerationTuning.SampleAroundCenter(random, center, payoffScale);
    public int AdjustSampledValue(Random random, ComponentAtom atom, int slot, int value) =>
        NumericGenerationTuning.AdjustSampledValue(random, atom, slot, value);
    public int ClampSampledValue(ComponentAtom atom, int slot, int value,
        IReadOnlyList<GeneratorOperation> previous, GeneratedCharacter? character, bool ultimateChaos) =>
        NumericGenerationTuning.ClampSampledValue(atom, slot, value, previous, character, ultimateChaos);
    public int ApplyValueBonuses(ComponentAtom atom, int value, GeneratedCharacter character,
        bool ultimateChaos) => NumericGenerationTuning.ApplyValueBonuses(atom, value, character, ultimateChaos);
    public int PayoffScalePercent(IReadOnlyList<GeneratorOperation> previous) =>
        EffectBalanceModel.PayoffScalePercent(previous);
    public int ScaleRewardCenter(ComponentAtom atom, int center, int benefitLines, GeneratedRarity rarity,
        IReadOnlyList<GeneratorOperation> previous, Random random, bool balancedValues) =>
        EffectBalanceModel.ScaleRewardCenter(atom, center, benefitLines, rarity, previous, random, balancedValues);
}
