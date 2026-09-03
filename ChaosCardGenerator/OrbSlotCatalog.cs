namespace ChaosCardGenerator;

public sealed record OrbSlotDefinition(
    string Id,
    string ChineseName,
    string EnglishName,
    bool DealsDamage,
    int BudgetWeight,
    int NativeGenerationWeight);

/// <summary>
/// Compact, game-assembly-free Orb type slots. Source is the Orb type inspected by an effect; output is the Orb
/// type it Channels. Most Channel operations only use output, while Voltaic deliberately owns both slots.
/// </summary>
public static class OrbSlotCatalog
{
    private const int DamageOutputWeightPercent = 112;

    private sealed record SlotSource(string? SourceId = null, string? OutputId = null,
        bool DamageSourceOnly = false);

    private static readonly IReadOnlyDictionary<string, OrbSlotDefinition> Definitions =
        new[]
        {
            // Budget weights combine the Orb's unmodified passive/evoke values with the residual card budget of
            // the v111 cards that Channel it. Native weights are the number of fixed-type generation lines in the
            // original Defect pool (including Tempest and Voltaic for Lightning): 7/6/5/2/3.
            new OrbSlotDefinition("lightning", "闪电", "Lightning", true, 100, 7),
            new OrbSlotDefinition("frost", "冰霜", "Frost", false, 105, 6),
            new OrbSlotDefinition("dark", "黑暗", "Dark", true, 125, 5),
            new OrbSlotDefinition("plasma", "等离子", "Plasma", false, 384, 2),
            new OrbSlotDefinition("glass", "玻璃", "Glass", true, 150, 3),
            // "Random" is a legal generation-slot result, not an orb type that can be inspected or triggered in play.
            // Chaos supplies the native whole-card anchor; do not price its unpredictability again per orb or count.
            new OrbSlotDefinition("random", "随机", "random", false, 256, 2)
        }.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, SlotSource> Sources =
        new Dictionary<string, SlotSource>(StringComparer.Ordinal)
        {
            ["D:ChannelLightning"] = new(OutputId: "lightning"),
            ["D:ChannelFrost"] = new(OutputId: "frost"),
            ["D:ChannelDark"] = new(OutputId: "dark"),
            ["D:ChannelPlasma"] = new(OutputId: "plasma"),
            ["D:ChannelGlass"] = new(OutputId: "glass"),
            ["D:ChannelRandom"] = new(OutputId: "random"),
            ["I:ProxyAtomic_Tempest"] = new(OutputId: "lightning"),
            ["I:ProxyAtomic_Voltaic"] = new(SourceId: "lightning", OutputId: "lightning"),
            ["D:IfHasFrost"] = new(SourceId: "frost"),
            ["D:TriggerDarkPassives"] = new(SourceId: "dark"),
            ["A:whenLightningEvoked"] = new(SourceId: "lightning", DamageSourceOnly: true)
        };

    public static IEnumerable<OrbSlotDefinition> All => Definitions.Values;
    public static int OutputGenerationWeight(OrbSlotDefinition definition) =>
        WeightedGenerationCount(definition, boostDamageOutput: true);
    public static bool IsKnownId(string id) => Definitions.ContainsKey(id);
    public static bool IsSlotOperation(string template) => Sources.ContainsKey(template);
    public static bool UsesSource(string template) => Sources.TryGetValue(template, out var source)
        && source.SourceId is not null;
    public static bool UsesOutput(string template) => Sources.TryGetValue(template, out var source)
        && source.OutputId is not null;

    public static OrbSlotDefinition? Resolve(string? id) => id is not null
        && Definitions.TryGetValue(id, out var definition) ? definition : null;

    public static OrbSlotDefinition? ResolveSource(string? id, string template) => Resolve(id)
        ?? (Sources.TryGetValue(template, out var source) ? Resolve(source.SourceId) : null);

    public static OrbSlotDefinition? ResolveOutput(string? id, string template) => Resolve(id)
        ?? (Sources.TryGetValue(template, out var source) ? Resolve(source.OutputId) : null);

    /// <summary>
    /// Approximate delayed-damage value of Channeling one Orb, in hundredths of one immediate damage point.
    /// Lightning is the reference at 5 delayed damage. Ball Lightning versus Iron Wave is the native anchor:
    /// the mixed offense/defense utility already carries its own synergy premium elsewhere in the budget model.
    /// The other Orb types retain the relative budget ratios
    /// inferred from the native card pool, so replacing an Orb type cannot silently create free card value.
    /// </summary>
    public static int ChannelValue(OrbSlotDefinition definition) =>
        (int)Math.Round(500d * definition.BudgetWeight / Definitions["lightning"].BudgetWeight);

    public static int ChannelValue(string? outputId, string template) =>
        ResolveOutput(outputId, template) is { } output ? ChannelValue(output) : 500;

    public static bool CanUseSource(string template, OrbSlotDefinition definition) =>
        Sources.TryGetValue(template, out var source) && source.SourceId is not null
        && (!source.DamageSourceOnly || definition.DealsDamage);

    public static bool CanUseOutput(string template, OrbSlotDefinition definition) =>
        Sources.TryGetValue(template, out var source) && source.OutputId is not null;

    public static (OrbSlotDefinition? Source, OrbSlotDefinition? Output) Roll(Random random, string template)
    {
        if (!Sources.TryGetValue(template, out var slot)) return (null, null);
        var sourceCandidates = slot.SourceId is null ? [] : Definitions.Values
            .Where(definition => definition.Id != "random")
            .Where(definition => CanUseSource(template, definition)).ToArray();
        var outputCandidates = slot.OutputId is null ? [] : Definitions.Values
            .Where(definition => CanUseOutput(template, definition)).ToArray();
        var source = sourceCandidates.Length == 0 ? null : PickNativeWeighted(random, sourceCandidates);
        OrbSlotDefinition? output;
        if (outputCandidates.Length == 0)
            output = null;
        else if (template == "I:ProxyAtomic_Voltaic" && source is not null && random.Next(100) < 75)
            output = source;
        else if (template == "I:ProxyAtomic_Voltaic" && source is not null)
        {
            var different = outputCandidates.Where(candidate => candidate.Id != source.Id).ToArray();
            output = PickNativeWeighted(random, different, boostDamageOutput: true);
        }
        else
            output = PickNativeWeighted(random, outputCandidates, boostDamageOutput: true);
        return (source, output);
    }

    private static OrbSlotDefinition PickNativeWeighted(Random random,
        IReadOnlyList<OrbSlotDefinition> candidates, bool boostDamageOutput = false)
    {
        var total = candidates.Sum(candidate => WeightedGenerationCount(candidate, boostDamageOutput));
        var roll = random.Next(total);
        foreach (var candidate in candidates)
        {
            roll -= WeightedGenerationCount(candidate, boostDamageOutput);
            if (roll < 0) return candidate;
        }
        return candidates[^1];
    }

    private static int WeightedGenerationCount(OrbSlotDefinition candidate, bool boostDamageOutput) =>
        !boostDamageOutput
            ? candidate.NativeGenerationWeight
            : candidate.NativeGenerationWeight * (candidate.DealsDamage ? DamageOutputWeightPercent : 100);

    /// <summary>
    /// Converts a fixed Channel count from the source Orb's original card budget to the rolled output Orb.
    /// Stochastic rounding retains a continuous expected value while all printed results remain integers.
    /// </summary>
    public static int BudgetedChannelCount(Random random, string template, OrbSlotDefinition output,
        int count, int effectiveCost, bool perEnemy)
    {
        var source = ResolveOutput(null, template);
        if (source is null) return Math.Max(1, count);
        var expected = Math.Max(1, count) * source.BudgetWeight / (double)output.BudgetWeight;
        var lower = (int)Math.Floor(expected);
        var adjusted = lower + (random.NextDouble() < expected - lower ? 1 : 0);
        return Math.Clamp(adjusted, 1, MaximumDirectChannelCount(output, effectiveCost, perEnemy));
    }

    public static int MaximumDirectChannelCount(OrbSlotDefinition output, int effectiveCost, bool perEnemy)
    {
        if (perEnemy) return 1;
        effectiveCost = Math.Max(0, effectiveCost);
        return output.Id switch
        {
            "plasma" => effectiveCost >= 5 ? 3 : effectiveCost >= 3 ? 2 : 1,
            "dark" or "glass" => effectiveCost >= 2 ? 2 : 1,
            "random" => effectiveCost >= 2 ? 3 : 2,
            _ => effectiveCost >= 2 ? 3 : 2
        };
    }

    public static void ValidateBudgetModel()
    {
        if (Definitions["lightning"].BudgetWeight != 100
            || Definitions["frost"].BudgetWeight <= Definitions["lightning"].BudgetWeight
            || Definitions["dark"].BudgetWeight <= Definitions["frost"].BudgetWeight
            || Definitions["glass"].BudgetWeight <= Definitions["dark"].BudgetWeight
            || Definitions["plasma"].BudgetWeight <= Definitions["glass"].BudgetWeight
            || Definitions.Values.Any(definition => definition.NativeGenerationWeight <= 0))
            throw new InvalidOperationException("充能球预算层级或原版生成权重无效。");

        if (ChannelValue(Definitions["lightning"]) != 500
            || ChannelValue(Definitions["frost"]) != 525
            || ChannelValue(Definitions["dark"]) != 625
            || ChannelValue(Definitions["glass"]) != 750
            || ChannelValue(Definitions["plasma"]) != 1_920
            || ChannelValue(Definitions["random"]) != 1_280)
            throw new InvalidOperationException("充能球的延迟伤害等价价值发生了意外变化。");

        var concrete = Definitions.Values.Where(definition => definition.Id != "random").ToArray();
        var nativeDamageShare = concrete.Where(definition => definition.DealsDamage)
            .Sum(definition => definition.NativeGenerationWeight) / (double)Definitions.Values
            .Where(definition => definition.Id != "random").Sum(definition => definition.NativeGenerationWeight);
        var adjustedDamageShare = concrete.Where(definition => definition.DealsDamage)
            .Sum(definition => WeightedGenerationCount(definition, boostDamageOutput: true)) / (double)concrete
            .Sum(definition => WeightedGenerationCount(definition, boostDamageOutput: true));
        if (adjustedDamageShare <= nativeDamageShare || adjustedDamageShare - nativeDamageShare > 0.08d)
            throw new InvalidOperationException("伤害类充能球的生成权重没有得到预期的小幅提升。 ");
    }

    public static string ApplyChinese(string text, string template, OrbSlotDefinition? source,
        OrbSlotDefinition? output)
    {
        if (template == "I:ProxyAtomic_Voltaic" && source is not null && output is not null)
            return $"生成等量于你在这场战斗中生成过的{source.ChineseName}充能球数量的{output.ChineseName}充能球。";
        if (!Sources.TryGetValue(template, out var slot)) return text;
        if (template == "D:ChannelRandom")
            text = text.Replace("随机生成", "生成", StringComparison.Ordinal)
                .Replace("个充能球", "个随机充能球", StringComparison.Ordinal);
        if (slot.SourceId is not null && source is not null)
            text = text.Replace(Definitions[slot.SourceId].ChineseName + "充能球",
                source.ChineseName + "充能球", StringComparison.Ordinal);
        if (slot.OutputId is not null && output is not null)
            text = text.Replace(Definitions[slot.OutputId].ChineseName + "充能球",
                output.ChineseName + "充能球", StringComparison.Ordinal);
        return text;
    }

    public static string ApplyEnglish(string text, string template, OrbSlotDefinition? source,
        OrbSlotDefinition? output)
    {
        if (template == "I:ProxyAtomic_Voltaic" && source is not null && output is not null)
            return $"Channel {output.EnglishName} equal to the {source.EnglishName} already Channeled this combat.";
        if (!Sources.TryGetValue(template, out var slot)) return text;
        if (slot.SourceId is not null && source is not null)
            text = text.Replace(Definitions[slot.SourceId].EnglishName, source.EnglishName,
                StringComparison.OrdinalIgnoreCase);
        if (slot.OutputId is not null && output is not null)
            text = text.Replace(Definitions[slot.OutputId].EnglishName, output.EnglishName,
                StringComparison.OrdinalIgnoreCase);
        return text;
    }

    public static string EnglishText(GeneratorOperation operation)
    {
        var source = ResolveSource(operation.OrbSourceId, operation.Template);
        var output = ResolveOutput(operation.OrbOutputId, operation.Template);
        if (operation.Template == "I:ProxyAtomic_Voltaic" && source is not null && output is not null)
            return ApplyEnglish(string.Empty, operation.Template, source, output);
        var slot = Sources[operation.Template];
        var sourceChinese = operation.ChineseText;
        if (slot.SourceId is not null && source is not null)
            sourceChinese = sourceChinese.Replace(source.ChineseName + "充能球",
                Definitions[slot.SourceId].ChineseName + "充能球", StringComparison.Ordinal);
        if (slot.OutputId is not null && output is not null)
            sourceChinese = sourceChinese.Replace(output.ChineseName + "充能球",
                Definitions[slot.OutputId].ChineseName + "充能球", StringComparison.Ordinal);
        var originalEnglish = ExternalOperationTextRegistry.TryGet(operation.Template, sourceChinese, out var mapped)
            ? mapped
            : EnglishCardDescriptionRenderer.TranslateLegacyLiteral(sourceChinese);
        return ApplyEnglish(originalEnglish, operation.Template, source, output);
    }

    /// <summary>
    /// Binds Orb identities as named presentation slots. Runtime and balance continue to read OrbSourceId and
    /// OrbOutputId; changing either name can no longer replace an unrelated word elsewhere in the description.
    /// </summary>
    public static OperationLocalizedText BindLocalizedText(OperationLocalizedText localized, string sourceEnglish,
        string template, OrbSlotDefinition? source, OrbSlotDefinition? output, OperationRuntimeSpec spec)
    {
        if (template == "I:ProxyAtomic_Voltaic" && source is not null && output is not null)
        {
            var result = new OperationLocalizedText(
                "生成等量于你在这场战斗中生成过的[[orb_source]]充能球数量的[[orb_output]]充能球。",
                "Channel [[orb_output]] equal to the [[orb_source]] already Channeled this combat.",
                [new OperationTextSlot("orb_source", source.ChineseName, source.EnglishName),
                    new OperationTextSlot("orb_output", output.ChineseName, output.EnglishName)]);
            result.Validate(spec);
            return result;
        }

        if (!Sources.TryGetValue(template, out var slots)) return localized;
        var resultLocalized = localized;
        if (slots.SourceId is not null && source is not null)
        {
            var original = Definitions[slots.SourceId];
            resultLocalized = resultLocalized
                .BindTextSlot("orb_source", original.ChineseName + "充能球", original.EnglishName)
                .WithTextSlotValue("orb_source", source.ChineseName + "充能球", source.EnglishName);
        }
        if (slots.OutputId is not null && output is not null && template != "D:ChannelRandom")
        {
            var original = Definitions[slots.OutputId];
            resultLocalized = resultLocalized
                .BindTextSlot("orb_output", original.ChineseName + "充能球", original.EnglishName)
                .WithTextSlotValue("orb_output", output.ChineseName + "充能球", output.EnglishName);
        }
        if (template == "D:ChannelRandom")
        {
            var chinese = ApplyChinese(localized.RenderChinese(spec), template, source, output);
            var english = ApplyEnglish(sourceEnglish, template, source, output);
            if (!OperationLocalizedText.TryCompile(chinese, english, spec, out resultLocalized)
                || resultLocalized is null)
                throw new InvalidOperationException($"Cannot compile random-Orb localization for {template}.");
        }
        resultLocalized.Validate(spec);
        return resultLocalized;
    }

    public static OperationLocalizedText BindResolvedLocalizedText(GeneratorOperation operation,
        OperationLocalizedText localized, string renderedEnglish, OperationRuntimeSpec spec)
    {
        var source = ResolveSource(operation.OrbSourceId, operation.Template);
        var output = ResolveOutput(operation.OrbOutputId, operation.Template);
        if (operation.Template == "I:ProxyAtomic_Voltaic" && source is not null && output is not null)
            return BindLocalizedText(localized, renderedEnglish, operation.Template, source, output, spec);
        if (!Sources.TryGetValue(operation.Template, out var slots)) return localized;
        var result = localized;
        if (slots.SourceId is not null && source is not null)
        {
            var original = Definitions[slots.SourceId];
            result = (result.TextSlots ?? []).Any(slot => slot.Id == "orb_source")
                ? result.WithTextSlotValue("orb_source", source.ChineseName + "充能球", source.EnglishName)
                : result.BindTextSlot("orb_source", original.ChineseName + "充能球", original.EnglishName)
                    .WithTextSlotValue("orb_source", source.ChineseName + "充能球", source.EnglishName);
        }
        if (slots.OutputId is not null && output is not null && operation.Template != "D:ChannelRandom")
        {
            var original = Definitions[slots.OutputId];
            result = (result.TextSlots ?? []).Any(slot => slot.Id == "orb_output")
                ? result.WithTextSlotValue("orb_output", output.ChineseName + "充能球", output.EnglishName)
                : result.BindTextSlot("orb_output", original.ChineseName + "充能球", original.EnglishName)
                    .WithTextSlotValue("orb_output", output.ChineseName + "充能球", output.EnglishName);
        }
        result.Validate(spec);
        return result;
    }
}
