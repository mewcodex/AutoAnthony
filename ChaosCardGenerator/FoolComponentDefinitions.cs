using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChaosCardGenerator;

/// <summary>
/// Reviewed v113 Fool contracts. Numeric token matching is confined to the offline authoring/migration boundary;
/// generated cards persist their named RuntimeSpec values and never interpret a melody from localized prose.
/// </summary>
public static class FoolComponentDefinitions
{
    // Nock: five Aim on a zero-cost Common; Deep Stretch: seven Stretch plus Draw 1 at one Energy.
    internal const int AimValue = 150;
    internal const int StretchValue = 100;
    // Nail / Recall are the offline anchors; the same frequency prices the double-damage modifier.
    internal const double ImpaledFrequency = 0.60d;
    private sealed record Definition(string Template, string ChineseTemplate, string EnglishTemplate,
        string Scope, string Opcode, string Variant, string Target, string[] Flags, RuntimeValueSlot[] Values,
        RuntimeConditionSpec? Condition, RuntimeTriggerSpec? Trigger,
        OperationTextSlot[]? TextSlots = null, string SourceZone = "none", string DestinationZone = "none",
        string CardFilter = "any");

    private static readonly Lazy<IReadOnlyDictionary<string, Definition>> Definitions = new(Load);
    private static readonly Regex Numeric = new(@"X(?:\+\d+)?|\d+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static IReadOnlyDictionary<string, Definition> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(name => name.EndsWith("fool_components.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        var entries = JsonSerializer.Deserialize<Definition[]>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Missing reviewed Fool component contracts.");
        return entries.ToDictionary(entry => entry.Template, StringComparer.Ordinal);
    }

    internal static bool TryCompile(GeneratorOperation operation, out OperationRuntimeSpec? result)
    {
        if (!operation.Template.StartsWith("F:", StringComparison.Ordinal)
            || !Definitions.Value.TryGetValue(operation.Template, out var definition)) { result = null; return false; }
        var numbers = Numeric.Matches(operation.ChineseText).Select(match => match.Value).ToArray();
        var numericIndex = 0;
        var values = definition.Values.Select(value =>
        {
            // Semantic selectors such as the melody bit mask are not printed numbers or upgrade candidates.
            if (!value.Explicit || numericIndex >= numbers.Length) return value;
            var literal = numbers[numericIndex++];
            return literal.StartsWith('X')
                ? value with { Source = "energy_x", BaseValue = 0,
                    Offset = literal.Length > 1 ? int.Parse(literal[2..]) : 0 }
                : value with { BaseValue = int.Parse(literal), Source = "fixed", Offset = 0 };
        }).ToArray();
        var flags = definition.Flags.ToHashSet(StringComparer.Ordinal);
        if (operation.Scope is OperationScope.NonTargeted or OperationScope.SingleEnemyOnly or OperationScope.AbilityRule
            && !flags.Contains(ComponentSemanticFlags.Negative)) flags.Add(ComponentSemanticFlags.Beneficial);
        result = new(OperationRuntimeSpec.CurrentSchemaVersion, definition.Opcode, definition.Variant,
            definition.Target, definition.SourceZone, definition.DestinationZone, definition.CardFilter,
            flags.Order(StringComparer.Ordinal).ToArray(), values,
            definition.Condition, definition.Trigger);
        result.Validate();
        return true;
    }

    internal static OperationLocalizedText? Localized(string template) =>
        template.StartsWith("F:", StringComparison.Ordinal) && Definitions.Value.TryGetValue(template, out var definition)
            ? new(definition.ChineseTemplate, definition.EnglishTemplate, definition.TextSlots) : null;

    internal static bool TryEstimate(string template, OperationRuntimeSpec spec, out int value)
    {
        value = 0;
        if (!template.StartsWith("F:", StringComparison.Ordinal)) return false;
        var amount = Math.Max(1, spec.Values.FirstOrDefault()?.BaseValue ?? 1);
        value = template switch
        {
            "F:ShootArrow" => 1, // Host-dependent arrow damage multiplier; never a standalone reward.
            "F:Aim" => amount * AimValue,
            "F:Stretch" => amount * StretchValue,
            "F:TemporaryThorns" => amount * 180,
            "F:CreateJoy" => amount * 1200,
            "F:CreateSadness" => amount * 450,
            "F:TransformSadnessToJoy" => amount * 1350,
            "F:ExhaustSadness" => 600,
            "F:LookChooseDiscard" => 350 + amount * 115,
            "F:RandomShootFree" => amount * 1500,
            "F:DiscardShootToHand" => amount * 550,
            "F:DrawShootToHand" => amount * 1000,
            "F:DiscardToDrawTop" => amount * 550,
            "F:Dislodge" => 1500,
            "F:OnlyCardsThisTurn" => 1600,
            "F:ArrowSlots" => amount * 2600,
            "F:MelodyReplay" => amount * 1400,
            "F:Quietus" => amount * 1200,
            "F:PlayMelodyCards" => MelodyPlayValue(spec.Values.FirstOrDefault(slot => slot.Id == "melody_mask")?.BaseValue ?? 5),
            "F:RandomTarot" => amount * 2400,
            "F:PlayCheapest" => amount * 1000,
            "F:NextCardFree" => amount * 1500,
            "F:TransformThisToSadness" => 500,
            "F:ExhaustShoot" => 1800,
            "F:CreateTheShot" => amount * 150,
            // Show Off: 1-Energy rare with Exhaust, immediate Aim 5, and permanent Aim +2 behind a
            // low-probability one-shot death gate. The gate's 0.10 frequency is applied by the shared pipeline.
            "F:IncreaseAimRun" => amount * 12_000,
            // These are host-dependent modifiers; the whole-card calculation replaces this sampling prior.
            "F:RefundStretch" or "F:StretchFromDamage" or "F:SplashDamage" => 600,
            "F:AimMultiplier" or "F:DoubleAgainstImpaled" or "F:ExtraHitsPerMelody"
                or "F:DamagePerSadnessCreated" or "F:DamagePerMelodyCard" => amount * 250,
            "F:CostPerMelody" or "F:CostIfWahKi" => amount * (int)SelfCostReductionValuation.ValuePerEnergy,
            _ => 0
        };
        return value > 0;
    }

    private static int MelodyPlayValue(int mask)
    {
        var recipes = CharacterComponentCatalogs.Get(GeneratedCharacter.Fool).Recipes;
        var selected = FoolMelodySubset.Tags(mask);
        var baseline = recipes.Count(recipe => recipe.Tags.Any(tag => tag is CardTag.Wah or CardTag.Ki));
        var coverage = recipes.Count(recipe => recipe.Tags.Any(selected.Contains));
        return (int)Math.Round(3500d * coverage / Math.Max(1, baseline));
    }
}
