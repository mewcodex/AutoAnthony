using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChaosCardGenerator;

public enum NativeCardExecutionSupport
{
    /// <summary>The reviewed component recipe has a complete Auto Anthony runtime specification.</summary>
    ExecutableRecipe,

    /// <summary>
    /// The card is fully decomposed for authoring, but one or more event/quest/non-combat lifecycle components
    /// still require a dedicated runtime adapter before a native instance may be replaced safely.
    /// </summary>
    StructuredReference
}

public sealed record NativeCardLocalizedText(
    [property: JsonPropertyName("zhHans")] string Chinese,
    [property: JsonPropertyName("en")] string English);

public sealed record NativeCardVariableDescriptor(
    string Id,
    string Kind,
    decimal? BaseValue,
    string? BaseExpression,
    bool Calculated);

public sealed record NativeCardShellDescriptor(
    int EnergyCost,
    bool EnergyCostX,
    int StarCost,
    bool StarCostX,
    string Type,
    string Target,
    string Rarity,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Tags,
    IReadOnlyList<NativeCardVariableDescriptor> Variables);

public sealed record NativeCardUpgradeResultDescriptor(
    int EnergyCost,
    int StarCost,
    IReadOnlyDictionary<string, decimal?> Variables,
    IReadOnlyList<string> Keywords);

/// <summary>
/// Upgrade actions deliberately retain their structured payload. Some native upgrades add an operation, replace a
/// selection contract, or alter a produced card instead of changing one scalar value.
/// </summary>
public sealed record NativeCardUpgradeActionDescriptor(
    string Kind,
    string? Variable = null,
    decimal? Delta = null,
    string? Keyword = null,
    string? Component = null,
    string? Card = null,
    string? Pool = null,
    string? Timing = null,
    string? Action = null,
    JsonElement? Filter = null,
    JsonElement? From = null,
    JsonElement? To = null);

public sealed record NativeCardUpgradeDescriptor(
    int MaxLevel,
    IReadOnlyList<NativeCardUpgradeActionDescriptor> Actions,
    NativeCardUpgradeResultDescriptor? Result);

public sealed record NativeCardValueTransform(decimal Scale, decimal Offset);

public sealed record NativeCardVariableBinding(
    string Variable,
    decimal BaseValue,
    decimal UpgradeDelta,
    NativeCardValueTransform ValueTransform);

public sealed record NativeCardComponentArgument(
    string Source,
    string? Id = null,
    string? ValueSource = null,
    decimal? BaseValue = null,
    decimal? Offset = null,
    bool? Upgradable = null,
    JsonElement? Value = null,
    IReadOnlyList<NativeCardVariableBinding>? NativeBindings = null);

public sealed record NativeCardComponentDescriptor(
    string ComponentId,
    string? SemanticId,
    int TriggerOwner,
    string? CardReference,
    bool RequiresSingleTarget,
    IReadOnlyDictionary<string, NativeCardComponentArgument> Arguments,
    OperationRuntimeSpec? RuntimeSpec,
    NativeCardLocalizedText? Text);

public sealed record NativeComponentParameterDescriptor(
    string Name,
    IReadOnlyList<string> AcceptedSources,
    bool SupportsNativeBindings);

public sealed record NativeComponentDefinition(
    string Id,
    NativeCardLocalizedText Name,
    bool ReferenceOnly,
    bool GenerationEligible,
    JsonElement RuntimeContract,
    NativeCardLocalizedText ExampleText,
    IReadOnlyList<NativeComponentParameterDescriptor> Parameters);

public sealed record NativeKeywordDefinition(
    string Id,
    NativeCardLocalizedText Name,
    bool ReferenceOnly,
    bool GenerationEligible);

/// <summary>One exact, reference-only v111 native-card recipe.</summary>
public sealed record NativeCardDecomposition(
    string CatalogId,
    string NativeId,
    string ClassName,
    string Pool,
    string SourceKind,
    bool ReferenceOnly,
    bool GenerationEligible,
    bool ShouldShowInLibrary,
    NativeCardLocalizedText Title,
    NativeCardLocalizedText DescriptionTemplate,
    NativeCardShellDescriptor Base,
    NativeCardUpgradeDescriptor Upgrade,
    IReadOnlyList<NativeCardComponentDescriptor> Components,
    IReadOnlyDictionary<string, string>? TemplateBindings = null)
{
    public NativeCardExecutionSupport ExecutionSupport =>
        string.Equals(SourceKind, "GeneratorCatalog", StringComparison.Ordinal)
            ? NativeCardExecutionSupport.ExecutableRecipe
            : NativeCardExecutionSupport.StructuredReference;
}

/// <summary>
/// Read-only native-card decomposition API. It never registers cards, changes native pools, or mutates a run.
/// Consumers opt in per card and may materialize only entries whose execution support is
/// <see cref="NativeCardExecutionSupport.ExecutableRecipe"/>.
/// </summary>
public static class NativeCardDecompositionApi
{
    public const int ApiVersion = 1;

    private sealed record CatalogEnvelope(int SchemaVersion, bool ReferenceOnly, bool GenerationEligible,
        IReadOnlyList<NativeCardDecomposition> Cards);
    private sealed record ComponentEnvelope(int SchemaVersion, bool ReferenceOnly, bool GenerationEligible,
        IReadOnlyList<NativeComponentDefinition> Components,
        IReadOnlyList<NativeKeywordDefinition> Keywords);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict
    };

    private static readonly Lazy<IReadOnlyList<NativeCardDecomposition>> Catalog = new(Load);
    private static readonly Lazy<ComponentEnvelope> ComponentCatalog = new(LoadComponents);
    private static readonly Lazy<IReadOnlyDictionary<string, NativeCardDecomposition>> ByCatalogId = new(() =>
        Catalog.Value.ToDictionary(card => card.CatalogId, StringComparer.Ordinal));
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<NativeCardDecomposition>>> ByNativeId =
        new(() => Catalog.Value.GroupBy(card => card.NativeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<NativeCardDecomposition>)group.ToArray(),
                StringComparer.Ordinal));

    public static IReadOnlyList<NativeCardDecomposition> Cards => Catalog.Value;
    public static IReadOnlyList<NativeComponentDefinition> Components => ComponentCatalog.Value.Components;
    public static IReadOnlyList<NativeKeywordDefinition> Keywords => ComponentCatalog.Value.Keywords;

    public static NativeCardDecomposition Get(string catalogId) =>
        ByCatalogId.Value.TryGetValue(catalogId, out var card)
            ? card
            : throw new KeyNotFoundException($"Unknown native-card catalog ID '{catalogId}'.");

    /// <summary>
    /// Returns every template for a native ModelId. Ordinary cards return one entry; Mad Science returns its nine
    /// explicit type/rider variants and must be disambiguated through <see cref="Resolve"/>.
    /// </summary>
    public static IReadOnlyList<NativeCardDecomposition> FindByNativeId(string nativeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeId);
        return ByNativeId.Value.TryGetValue(nativeId, out var cards) ? cards : [];
    }

    public static NativeCardDecomposition? Resolve(string nativeId,
        IReadOnlyDictionary<string, string>? templateBindings = null)
    {
        var candidates = FindByNativeId(nativeId);
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];
        if (templateBindings is null || templateBindings.Count == 0) return null;
        return candidates.SingleOrDefault(candidate => BindingsEqual(candidate.TemplateBindings, templateBindings));
    }

    /// <summary>
    /// Rebuilds the exact base component shell for one of the 481 reviewed character/colorless recipes. The native
    /// upgrade action list remains available on the descriptor and is intentionally not approximated here.
    /// </summary>
    public static bool TryCreateBaseDefinition(string catalogId, out GeneratedCard definition)
    {
        var card = Get(catalogId);
        if (card.ExecutionSupport != NativeCardExecutionSupport.ExecutableRecipe
            || !TryCharacter(card.Pool, out var character))
        {
            definition = null!;
            return false;
        }

        var recipe = StructuredComponentCatalogRegistry.Get(character).Recipes
            .SingleOrDefault(candidate => string.Equals(candidate.Id, card.ClassName, StringComparison.Ordinal));
        if (recipe is null)
            throw new InvalidDataException($"Executable native recipe {catalogId} has no structured source recipe.");
        var operations = recipe.Atoms.Select((atom, index) => new GeneratorOperation(
            atom.Template,
            atom.Scope,
            atom.ChineseText,
            recipe.TriggerOwners[index] < 0
                ? new Dictionary<string, int>()
                : new Dictionary<string, int> { ["triggerIndex"] = recipe.TriggerOwners[index] },
            atom.CardReference == CardReferenceRequirement.ThisCard ? "thisCard" : null,
            atom.RequiresSingleTarget,
            RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom),
            LocalizedText: atom.LocalizedText,
            LocalizationId: atom.SemanticId)).ToArray();

        definition = OperationRuntimeSpecCompiler.Attach(new GeneratedCard(
            recipe.Cost,
            recipe.Type,
            recipe.Target,
            recipe.OriginalRarity,
            CardDescriptionRenderer.Render(operations),
            recipe.Tags,
            operations,
            new GeneratedCardName(card.Title.Chinese, card.Title.English, [card.NativeId]),
            Upgrade: null,
            EnglishDescription: EnglishCardDescriptionRenderer.Render(operations),
            Character: character,
            StarCost: recipe.StarCost,
            HasStarCostX: recipe.HasStarCostX,
            CustomKeywords: recipe.CustomKeywords));
        return true;
    }

    /// <summary>
    /// Rebuilds the base shell and its exact native upgrade when the current generated-card upgrade contract can
    /// represent every action. Cards whose upgrade adds/removes an operation or changes an arbitrary selection
    /// program return false instead of receiving an approximation.
    /// </summary>
    public static bool TryCreateDefinition(string catalogId, out GeneratedCard definition,
        out IReadOnlyList<string> unsupportedUpgradeActions)
    {
        if (!TryCreateBaseDefinition(catalogId, out definition))
        {
            unsupportedUpgradeActions = ["runtime_adapter"];
            return false;
        }

        var source = Get(catalogId);
        var unsupported = new List<string>();
        var effects = new List<CardUpgradeEffect>();
        foreach (var action in source.Upgrade.Actions)
        {
            switch (action.Kind)
            {
                case "ChangeEnergyCost":
                    break;
                case "ChangeVariable":
                    if (!TryBindVariableUpgrade(source, action, effects)) unsupported.Add(action.Kind);
                    break;
                case "ChangeXOffset":
                    if (!TryBindXUpgrade(definition, action, effects)) unsupported.Add(action.Kind);
                    break;
                case "ChangeRepeatCount":
                    if (!TryBindRepeatUpgrade(definition, action, effects)) unsupported.Add(action.Kind);
                    break;
                case "AddComponent":
                    if (!TryBindAddedOnPlayComponent(definition, action, effects)) unsupported.Add(action.Kind);
                    break;
                case "AddKeyword":
                    if (!TryKeywordUpgrade(action.Keyword, added: true, out var add)) unsupported.Add(action.Kind);
                    else effects.Add(add);
                    break;
                case "RemoveKeyword":
                    if (!TryKeywordUpgrade(action.Keyword, added: false, out var remove)) unsupported.Add(action.Kind);
                    else effects.Add(remove);
                    break;
                case "UpgradeProducedCard":
                case "UpgradeProducedCards":
                case "UpgradeProducedChoices":
                case "UpgradeReferencedCardsBeforePlay":
                    if (!TryBindProducedCardUpgrade(definition, action, effects)) unsupported.Add(action.Kind);
                    break;
                case "ReplaceSelection" when string.Equals(action.Action, "Upgrade", StringComparison.Ordinal):
                    if (!TryBindUpgradeAllSelection(definition, effects)) unsupported.Add(action.Kind);
                    break;
                case "ReplaceSelection" when string.Equals(action.Action, "Exhaust", StringComparison.Ordinal):
                    if (!TryBindChosenExhaustUpgrade(definition, effects)) unsupported.Add(action.Kind);
                    break;
                default:
                    unsupported.Add(action.Kind);
                    break;
            }
        }

        unsupportedUpgradeActions = unsupported.Distinct(StringComparer.Ordinal).ToArray();
        if (unsupportedUpgradeActions.Count > 0) return false;
        if (source.Upgrade.MaxLevel <= 0) return true;

        GeneratorOperation[] upgradedOperations;
        try
        {
            upgradedOperations = CardUpgradeGenerator.ApplyEffectsToOperations(definition.Operations, effects);
        }
        catch (InvalidOperationException)
        {
            unsupportedUpgradeActions = ["localized_upgrade_projection"];
            return false;
        }
        var upgradedChinese = CardDescriptionRenderer.Render(upgradedOperations);
        var upgradedEnglish = EnglishCardDescriptionRenderer.Render(upgradedOperations);
        ProjectStructuralUpgradeText(upgradedOperations, effects,
            ref upgradedChinese, ref upgradedEnglish);
        definition = definition with
        {
            Upgrade = new CardUpgradePlan(
                source.Upgrade.Result?.EnergyCost ?? definition.Cost,
                effects,
                upgradedChinese,
                [],
                upgradedEnglish,
                [],
                source.Upgrade.Result?.StarCost)
        };
        return true;
    }

    /// <summary>
    /// Renders an executable native card through the same component composition pipeline used by generated cards.
    /// Numeric component slots that are explicitly bound to a native DynamicVar remain SmartFormat references, so
    /// the native card keeps its ordinary upgrade and combat-preview behavior without being replaced by a generated
    /// card instance. Printable reference records (including Event-rarity cards) are composed from their ordered
    /// component occurrences; records without a printable operation fall back to their reviewed structured template.
    /// </summary>
    public static bool TryCreateComponentDescriptionTemplate(string catalogId, bool upgraded, bool chinese,
        out string template)
    {
        var source = Get(catalogId);
        if (source.ExecutionSupport != NativeCardExecutionSupport.ExecutableRecipe)
        {
            if (TryRenderStructuredReferenceComponents(source, chinese, out template))
                return true;
            // Some non-combat lifecycle records intentionally have no printable operation (for example a bare
            // keyword-only Status). Their reviewed native template remains the only complete presentation.
            template = chinese ? source.DescriptionTemplate.Chinese : source.DescriptionTemplate.English;
            return true;
        }
        if (!TryCreateDefinition(catalogId, out var definition, out _))
        {
            template = string.Empty;
            return false;
        }

        var effects = upgraded ? definition.Upgrade?.Effects ?? [] : [];
        var operations = upgraded && definition.Upgrade is { } upgrade
            ? CardUpgradeGenerator.ApplyEffectsToOperations(definition.Operations, upgrade.Effects)
            : definition.Operations.ToArray();
        if (operations.Length != source.Components.Count)
            throw new InvalidDataException($"Native component rendering changed the operation count for {catalogId}.");

        var boundOperations = operations.Select((operation, index) =>
            BindNativeDynamicValues(operation, source.Components[index])).ToArray();
        var renderedChinese = CardDescriptionRenderer.Render(boundOperations);
        var renderedEnglish = EnglishCardDescriptionRenderer.Render(boundOperations);
        if (upgraded)
            ProjectStructuralUpgradeText(boundOperations, effects,
                ref renderedChinese, ref renderedEnglish);
        template = chinese ? renderedChinese : renderedEnglish;
        return true;
    }

    private static bool TryRenderStructuredReferenceComponents(NativeCardDecomposition source, bool chinese,
        out string template)
    {
        template = string.Empty;
        if (source.Components.Count == 0
            || source.Components.Any(component => component.Text is null
                                                  || string.IsNullOrWhiteSpace(chinese
                                                      ? component.Text.Chinese
                                                      : component.Text.English)))
            return false;

        var childIndices = source.Components
            .Select((component, index) => (component, index))
            .Where(item => item.component.TriggerOwner >= 0)
            .GroupBy(item => item.component.TriggerOwner)
            .ToDictionary(group => group.Key, group => group.Select(item => item.index).ToArray());
        var owned = childIndices.Values.SelectMany(indices => indices).ToHashSet();
        var visiting = new HashSet<int>();

        string RenderNode(int index)
        {
            if (!visiting.Add(index))
                throw new InvalidDataException($"Native structured component cycle in {source.CatalogId}.");
            var component = source.Components[index];
            var text = chinese ? component.Text!.Chinese : component.Text!.English;
            if (childIndices.TryGetValue(index, out var children))
                foreach (var child in children) text += RenderNode(child);
            visiting.Remove(index);
            return text;
        }

        var lines = Enumerable.Range(0, source.Components.Count)
            .Where(index => !owned.Contains(index))
            .Select(RenderNode)
            .ToArray();
        if (lines.Length == 0) return false;
        template = string.Join('\n', lines);
        return true;
    }

    private static GeneratorOperation BindNativeDynamicValues(GeneratorOperation operation,
        NativeCardComponentDescriptor component)
    {
        if (operation.LocalizedText is not { } localized) return operation;
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        var chinese = localized.RenderChinese(spec);
        var english = localized.RenderEnglish(spec) ?? EnglishCardDescriptionRenderer.OperationText(operation);
        var changed = false;
        foreach (var (slotId, argument) in component.Arguments)
        {
            var binding = argument.NativeBindings?.SingleOrDefault();
            if (binding is null
                || binding.ValueTransform.Scale != 1m
                || binding.ValueTransform.Offset != 0m)
                continue;
            var chineseToken = NativeDynamicToken(localized, slotId, binding.Variable, chinese: true);
            var englishToken = NativeDynamicToken(localized, slotId, binding.Variable, chinese: false);
            if (!localized.TryReplaceRenderedSlot(chinese, spec, slotId, chineseToken,
                    chinese: true, out var boundChinese)
                || !localized.TryReplaceRenderedSlot(english, spec, slotId, englishToken,
                    chinese: false, out var boundEnglish))
                continue;
            chinese = boundChinese;
            english = boundEnglish;
            changed = true;
        }
        if (!changed) return operation;
        // These templates intentionally contain native SmartFormat variable names rather than [[runtime slots]].
        // They are presentation-only and are installed on a native CardModel immediately before its own formatter
        // runs; execution and component parameter validation continue to use the original localized definition.
        return operation with
        {
            ChineseText = chinese,
            LocalizedText = new OperationLocalizedText(chinese, english)
        };
    }

    private static string NativeDynamicToken(OperationLocalizedText localized, string slotId,
        string variable, bool chinese)
    {
        var source = chinese ? localized.ChineseTemplate : localized.EnglishTemplate ?? string.Empty;
        if (source.Contains($"[[{slotId}:energy]]", StringComparison.Ordinal))
            return $"{{{variable}:energyIcons()}}";
        if (source.Contains($"[[{slotId}:stars]]", StringComparison.Ordinal))
            return $"{{{variable}:starIcons()}}";
        return $"{{{variable}:diff()}}";
    }

    private static IReadOnlyList<NativeCardDecomposition> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().SingleOrDefault(name =>
            name.EndsWith("native_reference_cards.json", StringComparison.Ordinal));
        if (resource is null)
            throw new InvalidDataException("The embedded native-card decomposition catalog is missing.");
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException($"Cannot read embedded resource {resource}.");
        var envelope = JsonSerializer.Deserialize<CatalogEnvelope>(stream, JsonOptions)
            ?? throw new InvalidDataException("The native-card decomposition catalog is empty.");
        if (envelope.SchemaVersion != 2 || !envelope.ReferenceOnly || envelope.GenerationEligible)
            throw new InvalidDataException("The native-card decomposition catalog has an unsupported schema/role.");
        if (envelope.Cards.Count != 567)
            throw new InvalidDataException($"Expected 567 native-card recipes, found {envelope.Cards.Count}.");
        if (envelope.Cards.Any(card => !card.ReferenceOnly || card.GenerationEligible))
            throw new InvalidDataException("A native reference recipe was accidentally enabled for generation.");
        if (envelope.Cards.Select(card => card.CatalogId).Distinct(StringComparer.Ordinal).Count()
            != envelope.Cards.Count)
            throw new InvalidDataException("The native-card decomposition catalog contains duplicate IDs.");
        return envelope.Cards.ToArray();
    }

    private static ComponentEnvelope LoadComponents()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().SingleOrDefault(name =>
            name.EndsWith("native_reference_components.json", StringComparison.Ordinal));
        if (resource is null)
            throw new InvalidDataException("The embedded native component-definition catalog is missing.");
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException($"Cannot read embedded resource {resource}.");
        var envelope = JsonSerializer.Deserialize<ComponentEnvelope>(stream, JsonOptions)
            ?? throw new InvalidDataException("The native component-definition catalog is empty.");
        if (envelope.SchemaVersion != 2 || !envelope.ReferenceOnly || envelope.GenerationEligible)
            throw new InvalidDataException("The native component-definition catalog has an unsupported schema/role.");
        if (envelope.Components.Count == 0 || envelope.Keywords.Count != 7
            || envelope.Components.Any(component => !component.ReferenceOnly || component.GenerationEligible)
            || envelope.Keywords.Any(keyword => !keyword.ReferenceOnly || keyword.GenerationEligible))
            throw new InvalidDataException("The native component/keyword catalog is incomplete or generation-enabled.");
        return envelope with
        {
            Components = envelope.Components.ToArray(),
            Keywords = envelope.Keywords.ToArray()
        };
    }

    private static bool BindingsEqual(IReadOnlyDictionary<string, string>? expected,
        IReadOnlyDictionary<string, string> actual)
    {
        if (expected is null || expected.Count != actual.Count) return false;
        return expected.All(pair => actual.TryGetValue(pair.Key, out var value)
                                    && string.Equals(value, pair.Value, StringComparison.Ordinal));
    }

    private static bool TryBindVariableUpgrade(NativeCardDecomposition card,
        NativeCardUpgradeActionDescriptor action, ICollection<CardUpgradeEffect> effects)
    {
        if (action.Variable is null || action.Delta is null) return false;
        var found = false;
        for (var operationIndex = 0; operationIndex < card.Components.Count; operationIndex++)
        {
            foreach (var (slotId, argument) in card.Components[operationIndex].Arguments)
            foreach (var binding in argument.NativeBindings ?? [])
            {
                if (!string.Equals(binding.Variable, action.Variable, StringComparison.Ordinal)) continue;
                var scaled = action.Delta.Value * binding.ValueTransform.Scale;
                if (scaled != decimal.Truncate(scaled)) return false;
                effects.Add(new CardUpgradeEffect(CardUpgradeKind.IncreaseNumber, operationIndex,
                    decimal.ToInt32(scaled), slotId));
                found = true;
            }
        }
        return found;
    }

    private static bool TryBindXUpgrade(GeneratedCard card, NativeCardUpgradeActionDescriptor action,
        ICollection<CardUpgradeEffect> effects)
    {
        if (action.Delta is null || action.Delta != decimal.Truncate(action.Delta.Value)) return false;
        var candidates = card.Operations.Select((operation, index) => (operation, index,
                slots: OperationRuntimeSpecCompiler.GetOrCompile(operation).Values
                    .Where(value => value.Upgradable && value.Source is "energy_x" or "star_x" or "special_x")
                    .ToArray()))
            .Where(item => item.slots.Length > 0).ToArray();
        if (candidates.Length == 0 || candidates.Any(candidate => candidate.slots.Length != 1)) return false;
        foreach (var candidate in candidates)
            effects.Add(new CardUpgradeEffect(CardUpgradeKind.IncreaseNumber, candidate.index,
                decimal.ToInt32(action.Delta.Value), candidate.slots[0].Id));
        return true;
    }

    private static bool TryKeywordUpgrade(string? keyword, bool added, out CardUpgradeEffect effect)
    {
        var kind = (added, keyword) switch
        {
            (true, "Innate") => CardUpgradeKind.GrantInnate,
            (true, "Retain") => CardUpgradeKind.GrantRetain,
            (false, "Exhaust") => CardUpgradeKind.RemoveExhaust,
            (false, "Ethereal") => CardUpgradeKind.RemoveEthereal,
            _ => (CardUpgradeKind?)null
        };
        effect = kind is { } value ? new CardUpgradeEffect(value) : null!;
        return kind is not null;
    }

    private static bool TryBindProducedCardUpgrade(GeneratedCard card,
        NativeCardUpgradeActionDescriptor action, ICollection<CardUpgradeEffect> effects)
    {
        if (action.Kind == "UpgradeReferencedCardsBeforePlay")
        {
            var referenced = card.Operations.Select((operation, index) => (operation, index))
                .Where(item => item.operation.Template == "I:PlayExhaustedShivsAtTarget")
                .ToArray();
            if (referenced.Length != 1) return false;
            effects.Add(new CardUpgradeEffect(CardUpgradeKind.UpgradeReferencedCards,
                referenced[0].index));
            return true;
        }
        var generatedCards = action.Pool is not null
                             || action.Kind is "UpgradeProducedCards" or "UpgradeProducedChoices";
        var candidates = card.Operations.Select((operation, index) => (operation, index))
            .Where(item => action.Kind == "UpgradeProducedCard" && !generatedCards
                    ? DerivativeSlotCatalog.SupportsUpgrade(item.operation.Template, item.operation.DerivativeId)
                    : CardEffectRules.IsRandomCardGeneration(item.operation))
            .ToArray();
        if (candidates.Length != 1) return false;
        effects.Add(new CardUpgradeEffect(
            generatedCards
                ? CardUpgradeKind.UpgradeGeneratedCards
                : CardUpgradeKind.UpgradeDerivative,
            candidates[0].index));
        return true;
    }

    private static bool TryBindChosenExhaustUpgrade(GeneratedCard card, ICollection<CardUpgradeEffect> effects)
    {
        var candidates = card.Operations.Select((operation, index) => (operation, index))
            .Where(item =>
            {
                var spec = OperationRuntimeSpecCompiler.GetOrCompile(item.operation);
                return spec.Opcode == "exhaust_card" && spec.SourceZone == "hand"
                       && spec.Variant is "random" or "selected";
            })
            .ToArray();
        if (candidates.Length != 1) return false;
        effects.Add(new CardUpgradeEffect(CardUpgradeKind.ChooseExhaust, candidates[0].index));
        return true;
    }

    private static bool TryBindRepeatUpgrade(GeneratedCard card, NativeCardUpgradeActionDescriptor action,
        ICollection<CardUpgradeEffect> effects)
    {
        var template = action.Component switch
        {
            "TriggerAllDarkOrbPassives" => "D:TriggerDarkPassives",
            "TriggerAllLightningOrbPassives" => "D:TriggerLightningPassivesAtTarget",
            _ => null
        };
        if (template is null || action.Delta is null || action.Delta != decimal.Truncate(action.Delta.Value))
            return false;
        var candidates = card.Operations.Select((operation, index) => (operation, index))
            .Where(item => item.operation.Template == template).ToArray();
        if (candidates.Length != 1) return false;
        effects.Add(new CardUpgradeEffect(CardUpgradeKind.RepeatOperation, candidates[0].index,
            decimal.ToInt32(action.Delta.Value)));
        return true;
    }

    private static bool TryBindAddedOnPlayComponent(GeneratedCard card,
        NativeCardUpgradeActionDescriptor action, ICollection<CardUpgradeEffect> effects)
    {
        if (!string.Equals(action.Timing, "OnPlay", StringComparison.Ordinal)) return false;
        var template = action.Component switch
        {
            "ChannelGlassOrb" => "D:ChannelGlass",
            _ => null
        };
        if (template is null) return false;
        var candidates = card.Operations.Select((operation, index) => (operation, index))
            .Where(item => item.operation.Template == template).ToArray();
        if (candidates.Length != 1) return false;
        effects.Add(new CardUpgradeEffect(CardUpgradeKind.ExecuteOperationOnPlay, candidates[0].index));
        return true;
    }

    private static bool TryBindUpgradeAllSelection(GeneratedCard card,
        ICollection<CardUpgradeEffect> effects)
    {
        var candidates = card.Operations.Select((operation, index) => (operation, index))
            .Where(item => item.operation.Template == "I:Upgrade").ToArray();
        if (candidates.Length != 1) return false;
        effects.Add(new CardUpgradeEffect(CardUpgradeKind.SelectAllCards, candidates[0].index));
        return true;
    }

    private static void ProjectStructuralUpgradeText(IReadOnlyList<GeneratorOperation> upgradedOperations,
        IReadOnlyList<CardUpgradeEffect> effects,
        ref string chinese, ref string english)
    {
        var added = effects.SingleOrDefault(effect => effect.Kind == CardUpgradeKind.ExecuteOperationOnPlay);
        if (added?.OperationIndex is not { } operationIndex
            || (uint)operationIndex >= (uint)upgradedOperations.Count) return;
        var immediate = upgradedOperations[operationIndex] with
        {
            Parameters = upgradedOperations[operationIndex].Parameters
                .Where(pair => pair.Key != "triggerIndex")
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
        chinese = CardDescriptionRenderer.Render([immediate]) + "\n" + chinese;
        english = EnglishCardDescriptionRenderer.Render([immediate]) + "\n" + english;
    }

    private static bool TryCharacter(string pool, out GeneratedCharacter character)
    {
        if (Enum.TryParse(pool, ignoreCase: false, out character)
            && Enum.IsDefined(character)) return true;
        character = default;
        return false;
    }
}
