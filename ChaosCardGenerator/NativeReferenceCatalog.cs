using System.Reflection;
using System.Text.Json;

namespace ChaosCardGenerator;

/// <summary>
/// Validates the read-only native-card reconstruction catalog.  These resources
/// are embedded only in the authoring executable, never in the gameplay mod.
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
        }
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
