using System.Reflection;
using System.Text.Json;

namespace ChaosCardGenerator;

/// <summary>
/// Validates the read-only native-card reconstruction catalog. The same immutable resources are embedded in the
/// authoring executable and gameplay assembly; merely loading them never registers components for generation.
/// </summary>
internal static class NativeReferenceCatalog
{
    private static readonly IReadOnlyDictionary<string, int> ExpectedPoolCounts =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Colorless"] = 53,
            ["Curse"] = 18,
            ["Defect"] = 86,
            ["Deprecated"] = 1,
            ["Event"] = 36,
            ["Ironclad"] = 85,
            ["Necrobinder"] = 86,
            ["Quest"] = 4,
            ["Regent"] = 86,
            ["Silent"] = 86,
            ["Status"] = 12,
            ["Token"] = 14,
        };

    private static readonly string[] ExpectedTokens =
    [
        "Disintegration", "Fuel", "GiantRock", "Luminesce", "MindRot", "MinionDiveBomb",
        "MinionSacrifice", "MinionStrike", "Shiv", "Sloth", "Soul", "SovereignBlade",
        "SweepingGaze", "WasteAway",
    ];

    internal static void Validate()
    {
        using var cardsDocument = Load("native_reference_cards.json");
        using var componentsDocument = Load("native_reference_components.json");
        var cardsRoot = cardsDocument.RootElement;
        var componentsRoot = componentsDocument.RootElement;
        Require(cardsRoot.GetProperty("SchemaVersion").GetInt32() == 2,
            "card catalog schema is not version 2");
        Require(componentsRoot.GetProperty("SchemaVersion").GetInt32() == 2,
            "component catalog schema is not version 2");
        RequireReferenceOnly(cardsRoot, "card catalog");
        RequireReferenceOnly(componentsRoot, "component catalog");

        var definitions = componentsRoot.GetProperty("Components").EnumerateArray().ToArray();
        var definitionIds = UniqueIds(definitions, "component definition");
        foreach (var definition in definitions)
        {
            RequireReferenceOnly(definition, $"component {definition.GetProperty("Id").GetString()}");
            Require(definition.TryGetProperty("RuntimeContract", out _), "component lacks RuntimeContract");
            Require(definition.TryGetProperty("Parameters", out _), "component lacks parameter schema");
        }

        var keywords = componentsRoot.GetProperty("Keywords").EnumerateArray().ToArray();
        var keywordIds = UniqueIds(keywords, "keyword definition");
        Require(keywordIds.SetEquals(["Eternal", "Ethereal", "Exhaust", "Innate", "Retain", "Sly", "Unplayable"]),
            "keyword catalog differs from the complete native keyword set");
        var unplayable = keywords.Single(item => item.GetProperty("Id").GetString() == "Unplayable");
        Require(unplayable.GetProperty("Name").GetProperty("zhHans").GetString() == "不可被打出",
            "Unplayable is missing its official Chinese label");

        var cards = cardsRoot.GetProperty("Cards").EnumerateArray().ToArray();
        Require(cards.Length == 567, $"expected 567 card records, found {cards.Length}");
        _ = UniqueIds(cards, "card", "CatalogId");
        var poolCounts = cards.GroupBy(item => item.GetProperty("Pool").GetString()!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Require(poolCounts.Count == ExpectedPoolCounts.Count
                && ExpectedPoolCounts.All(pair => poolCounts.GetValueOrDefault(pair.Key) == pair.Value),
            "native reference pool counts changed");

        foreach (var card in cards)
            ValidateCard(card, definitionIds, keywordIds);

        RequireNativeBinding(cards, "SetupStrike", 1, "amount", "StrengthPower", 3, 1);
        RequireNativeBinding(cards, "FeelNoPain", 1, "block", "Power", 3, 1);
        RequireNativeBinding(cards, "HelixDrill", 1, "damage", "Damage", 3, 2);

        Require(cards.Count(item => item.GetProperty("ClassName").GetString() == "MadScience") == 9,
            "MadScience must be represented by nine independent variants");
        Require(cards.Any(item => item.GetProperty("ClassName").GetString() == "Fasten"),
            "Fasten is missing");
        Require(cards.Any(item => item.GetProperty("ClassName").GetString() == "DeprecatedCard"),
            "DeprecatedCard is missing");
        var tokens = cards.Where(item => item.GetProperty("Pool").GetString() == "Token")
            .Select(item => item.GetProperty("ClassName").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(tokens.SequenceEqual(ExpectedTokens.Order(StringComparer.Ordinal), StringComparer.Ordinal),
            "token/derivative pool is incomplete");

        ValidateRuntimeApi(cards);
    }

    private static void ValidateRuntimeApi(JsonElement[] sourceCards)
    {
        var exposed = NativeCardDecompositionApi.Cards;
        Require(NativeCardDecompositionApi.Components.Count == 451,
            "runtime API component-definition count differs from the source catalog");
        Require(NativeCardDecompositionApi.Keywords.Count == 7,
            "runtime API keyword-definition count differs from the source catalog");
        Require(exposed.Count == sourceCards.Length, "runtime API card count differs from the source catalog");
        Require(exposed.Count(card => card.ExecutionSupport == NativeCardExecutionSupport.ExecutableRecipe) == 481,
            "runtime API executable recipe count differs from the reviewed catalog");
        Require(exposed.Count(card => card.ExecutionSupport == NativeCardExecutionSupport.StructuredReference) == 86,
            "runtime API reference-extension count differs from the reviewed catalog");
        Require(exposed.Count(card => card.NativeId == "MAD_SCIENCE") == 9,
            "runtime API does not expose all Mad Science variants");
        Require(NativeCardDecompositionApi.Resolve("MAD_SCIENCE") is null,
            "ambiguous Mad Science lookup must require template bindings");
        var setup = NativeCardDecompositionApi.Get("native/ironclad/setup_strike");
        Require(NativeCardDecompositionApi.TryCreateBaseDefinition(setup.CatalogId, out var definition),
            "runtime API could not materialize Setup Strike");
        Require(definition.Name?.English == "Setup Strike" && definition.Operations.Count == 2
                && definition.Operations.All(operation => operation.RuntimeSpec is not null
                                                          && operation.LocalizedText is not null),
            "runtime API materialized an incomplete Setup Strike definition");
        Require(!NativeCardDecompositionApi.TryCreateBaseDefinition("native/event/brightest_flame", out _),
            "reference-extension card was exposed as executable before its lifecycle adapter exists");

        var exactDefinitions = 0;
        foreach (var card in exposed.Where(card =>
                     card.ExecutionSupport == NativeCardExecutionSupport.ExecutableRecipe))
        {
            Require(NativeCardDecompositionApi.TryCreateBaseDefinition(card.CatalogId, out var rebuilt),
                $"runtime API could not materialize {card.CatalogId}");
            Require(rebuilt.Operations.Count == card.Components.Count,
                $"runtime API changed the component count for {card.CatalogId}");
            Require(NativeCardDecompositionApi.TryCreateDefinition(card.CatalogId, out _, out var unsupported),
                $"runtime API could not materialize the exact upgrade for {card.CatalogId}: "
                + string.Join(',', unsupported));
            foreach (var upgraded in new[] { false, true })
            foreach (var chinese in new[] { false, true })
                Require(NativeCardDecompositionApi.TryCreateComponentDescriptionTemplate(
                            card.CatalogId, upgraded, chinese, out var template)
                        && !string.IsNullOrWhiteSpace(template)
                        && !template.Contains("[[", StringComparison.Ordinal),
                    $"runtime API could not render the component description for {card.CatalogId} "
                    + $"(upgraded={upgraded}, chinese={chinese})");
            exactDefinitions++;
        }
        Require(exactDefinitions == 481, "runtime API exact-upgrade coverage is incomplete");
        RequireStructuralUpgrade("native/defect/darkness", CardUpgradeKind.RepeatOperation);
        RequireStructuralUpgrade("native/defect/spinner", CardUpgradeKind.ExecuteOperationOnPlay);
        RequireStructuralUpgrade("native/defect/tesla_coil", CardUpgradeKind.RepeatOperation);
        RequireStructuralUpgrade("native/ironclad/armaments", CardUpgradeKind.SelectAllCards);
        RequireStructuralUpgrade("native/silent/knife_trap", CardUpgradeKind.UpgradeReferencedCards);
        RequireUpgradeText("native/defect/darkness", "两次", "twice");
        RequireUpgradeText("native/defect/spinner", "生成", "hannel", minimumOccurrences: 2);
        RequireUpgradeText("native/ironclad/armaments", "所有牌", "ALL cards");
        RequireUpgradeText("native/silent/knife_trap", "升级", "Upgrade");
        RequireComponentDescriptionTemplate("native/ironclad/setup_strike",
            "{Damage:diff()}", "{StrengthPower:diff()}");
        RequireComponentDescriptionTemplate("native/ironclad/barricade",
            "格挡", "Block");
        var eventRarityCards = exposed.Where(card => card.Base.Rarity == "Event").ToArray();
        Require(eventRarityCards.Length == 27,
            "native Event-rarity card count differs from the reviewed catalog");
        foreach (var card in eventRarityCards)
        {
            Require(card.Components.Count > 0 && card.Components.All(component => component.Text is not null),
                $"Event-rarity card {card.CatalogId} lacks printable component text");
            foreach (var chinese in new[] { false, true })
                Require(NativeCardDecompositionApi.TryCreateComponentDescriptionTemplate(
                            card.CatalogId, upgraded: false, chinese, out var template)
                        && !string.IsNullOrWhiteSpace(template),
                    $"Event-rarity card {card.CatalogId} did not render from components");
        }
        Require(NativeCardDecompositionApi.TryCreateComponentDescriptionTemplate(
                    "native/silent/prepared", upgraded: true, chinese: true, out var prepared)
                && Count(prepared, "{Cards:diff()}") == 2,
            "Prepared does not bind its upgraded draw and discard amounts to the same native variable");
    }

    private static void RequireStructuralUpgrade(string catalogId, CardUpgradeKind kind)
    {
        Require(NativeCardDecompositionApi.TryCreateDefinition(catalogId, out var definition, out _)
                && definition.Upgrade?.Effects.Any(effect => effect.Kind == kind) == true,
            $"native structural upgrade {catalogId}/{kind} is not executable");
    }

    private static void RequireUpgradeText(string catalogId, string chinese, string english,
        int minimumOccurrences = 1)
    {
        Require(NativeCardDecompositionApi.TryCreateDefinition(catalogId, out var definition, out _)
                && definition.Upgrade is { } upgrade
                && Count(upgrade.UpgradedChineseDescription, chinese) >= minimumOccurrences
                && Count(upgrade.UpgradedEnglishDescription, english) >= minimumOccurrences,
            $"native structural upgrade {catalogId} has an incomplete localized projection");
    }

    private static void RequireComponentDescriptionTemplate(string catalogId, string chinese, string english)
    {
        Require(NativeCardDecompositionApi.TryCreateComponentDescriptionTemplate(
                    catalogId, upgraded: false, chinese: true, out var chineseTemplate)
                && NativeCardDecompositionApi.TryCreateComponentDescriptionTemplate(
                    catalogId, upgraded: false, chinese: false, out var englishTemplate)
                && chineseTemplate.Contains(chinese, StringComparison.Ordinal)
                && englishTemplate.Contains(english, StringComparison.Ordinal),
            $"runtime API component-description projection {catalogId} is incomplete");
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        for (var index = 0; (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0;
             index += value.Length) count++;
        return count;
    }

    private static void ValidateCard(JsonElement card, HashSet<string> componentIds, HashSet<string> keywordIds)
    {
        var id = card.GetProperty("CatalogId").GetString()!;
        RequireReferenceOnly(card, $"card {id}");
        var variables = card.GetProperty("Base").GetProperty("Variables").EnumerateArray()
            .Select(item => item.GetProperty("Id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var keyword in card.GetProperty("Base").GetProperty("Keywords").EnumerateArray())
            Require(keywordIds.Contains(keyword.GetString()!), $"{id} references unknown keyword {keyword}");

        var components = card.GetProperty("Components").EnumerateArray().ToArray();
        var cardBoundVariables = new HashSet<string>(StringComparer.Ordinal);
        var upgradeDeltas = card.GetProperty("Upgrade").GetProperty("Actions").EnumerateArray()
            .Where(item => item.GetProperty("Kind").GetString() == "ChangeVariable")
            .GroupBy(item => item.GetProperty("Variable").GetString()!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Sum(item => item.GetProperty("Delta").GetDecimal()), StringComparer.Ordinal);
        var variableValues = card.GetProperty("Base").GetProperty("Variables").EnumerateArray()
            .Where(item => item.GetProperty("BaseValue").ValueKind == JsonValueKind.Number)
            .ToDictionary(item => item.GetProperty("Id").GetString()!,
                item => item.GetProperty("BaseValue").GetDecimal(), StringComparer.Ordinal);
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            var componentId = component.GetProperty("ComponentId").GetString()!;
            Require(componentIds.Contains(componentId), $"{id} references unknown component {componentId}");
            var owner = component.GetProperty("TriggerOwner").GetInt32();
            Require(owner >= -1 && owner < index, $"{id} has invalid TriggerOwner {owner} at {index}");
            foreach (var argument in component.GetProperty("Arguments").EnumerateObject())
            {
                if (argument.Value.GetProperty("Source").GetString() == "NativeVariable")
                {
                    var variable = argument.Value.GetProperty("Id").GetString()!;
                    Require(variables.Contains(variable), $"{id}/{componentId} references unknown variable {variable}");
                    cardBoundVariables.Add(variable);
                }
                if (!argument.Value.TryGetProperty("NativeBindings", out var bindings)) continue;
                Require(argument.Value.GetProperty("Source").GetString() == "RuntimeValue",
                    $"{id}/{componentId}/{argument.Name} has native bindings on a non-runtime argument");
                Require(bindings.ValueKind == JsonValueKind.Array && bindings.GetArrayLength() > 0,
                    $"{id}/{componentId}/{argument.Name} has an empty native binding list");
                var boundVariables = new HashSet<string>(StringComparer.Ordinal);
                foreach (var binding in bindings.EnumerateArray())
                {
                    var variable = binding.GetProperty("Variable").GetString()!;
                    cardBoundVariables.Add(variable);
                    Require(boundVariables.Add(variable),
                        $"{id}/{componentId}/{argument.Name} binds {variable} more than once");
                    Require(variableValues.TryGetValue(variable, out var nativeBase),
                        $"{id}/{componentId}/{argument.Name} binds unknown/non-numeric variable {variable}");
                    Require(binding.GetProperty("BaseValue").GetDecimal() == nativeBase,
                        $"{id}/{componentId}/{argument.Name} records the wrong base for {variable}");
                    Require(upgradeDeltas.TryGetValue(variable, out var nativeDelta)
                            && binding.GetProperty("UpgradeDelta").GetDecimal() == nativeDelta,
                        $"{id}/{componentId}/{argument.Name} records the wrong upgrade for {variable}");
                    var transform = binding.GetProperty("ValueTransform");
                    var scale = transform.GetProperty("Scale").GetDecimal();
                    var offset = transform.GetProperty("Offset").GetDecimal();
                    if (argument.Value.GetProperty("ValueSource").GetString() == "fixed")
                        Require(argument.Value.GetProperty("BaseValue").GetDecimal() == nativeBase * scale + offset,
                            $"{id}/{componentId}/{argument.Name} has an invalid native value transform");
                }
            }
        }

        var upgrade = card.GetProperty("Upgrade");
        var maxLevel = upgrade.GetProperty("MaxLevel").GetInt32();
        var actions = upgrade.GetProperty("Actions").EnumerateArray().ToArray();
        Require(maxLevel == 0 || actions.Length > 0, $"{id} has an upgrade without structured actions");
        Require(maxLevel > 0 || upgrade.GetProperty("Result").ValueKind == JsonValueKind.Null,
            $"{id} has an upgrade result despite MaxLevel=0");
        foreach (var action in actions.Where(item => item.GetProperty("Kind").GetString() == "ChangeVariable"))
        {
            var variable = action.GetProperty("Variable").GetString()!;
            Require(variables.Contains(variable), $"{id} upgrade references unknown variable {variable}");
            if (card.GetProperty("SourceKind").GetString() == "GeneratorCatalog")
                Require(cardBoundVariables.Contains(variable),
                    $"{id} upgrade variable {variable} is not explicitly bound to a component argument");
        }
    }

    private static void RequireNativeBinding(JsonElement[] cards, string className, int componentIndex,
        string argumentName, string variableName, decimal baseValue, decimal upgradeDelta)
    {
        var card = cards.Single(item => item.GetProperty("ClassName").GetString() == className);
        var component = card.GetProperty("Components")[componentIndex];
        var argument = component.GetProperty("Arguments").GetProperty(argumentName);
        var binding = argument.GetProperty("NativeBindings").EnumerateArray()
            .Single(item => item.GetProperty("Variable").GetString() == variableName);
        Require(binding.GetProperty("BaseValue").GetDecimal() == baseValue
                && binding.GetProperty("UpgradeDelta").GetDecimal() == upgradeDelta,
            $"{className}/{componentIndex}/{argumentName} lacks its reviewed {variableName} binding");
    }

    private static JsonDocument Load(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal));
        Require(resource is not null, $"missing embedded resource {suffix}");
        using var stream = assembly.GetManifestResourceStream(resource!)
            ?? throw new InvalidOperationException($"cannot open embedded resource {resource}");
        return JsonDocument.Parse(stream);
    }

    private static HashSet<string> UniqueIds(IEnumerable<JsonElement> items, string kind, string property = "Id")
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var id = item.GetProperty(property).GetString()!;
            Require(result.Add(id), $"duplicate {kind} ID: {id}");
        }
        return result;
    }

    private static void RequireReferenceOnly(JsonElement item, string name)
    {
        Require(item.GetProperty("ReferenceOnly").GetBoolean(), $"{name} is not ReferenceOnly");
        Require(!item.GetProperty("GenerationEligible").GetBoolean(), $"{name} is generation eligible");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Native reference catalog audit failed: {message}");
    }
}
