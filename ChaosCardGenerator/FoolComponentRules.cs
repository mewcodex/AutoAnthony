namespace ChaosCardGenerator;

/// <summary>Composition contracts shared by generation, validation and runtime dependency resolution.</summary>
public static class FoolComponentRules
{
    public static bool IsShootModifier(ComponentAtom atom) =>
        OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("grants_shoot_tag");

    public static bool IsShootModifier(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("grants_shoot_tag");

    public static bool HasValidShootAssembly(IReadOnlyList<GeneratorOperation> operations,
        bool allowIncomplete = false)
    {
        var arrows = operations.Count(IsShootModifier);
        if (arrows == 0) return true;
        if (arrows != 1) return false;
        var payloads = operations.Where(operation => !IsShootModifier(operation)).ToArray();
        if (allowIncomplete && payloads.Length == 0) return true;
        // One arrow-tagged damage sentence, including its intrinsic hit count. Other modifiers and conditions
        // would introduce additional sentences or ambiguous arrow ownership; native recipes are preserved as
        // reconstruction data, but newly assembled cards deliberately use the narrower requested contract.
        return payloads.Length == 1 && payloads[0].Scope != OperationScope.Modifier
            && !payloads[0].Parameters.ContainsKey("triggerIndex")
            && CardEffectRules.IsEnemyDamage(payloads[0])
            && OperationRuntimeSpecCompiler.GetOrCompile(payloads[0]).Target == "selected_enemy"
            && !GeneratedCardTagPolicy.IsOstyAttackCard(payloads);
    }

    public static bool HasValidAnchors(IReadOnlyList<GeneratorOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (!operation.Template.StartsWith("F:", StringComparison.Ordinal)) continue;
            var flags = OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags;
            if (operation.Template == "F:IfShotSucceeded" && !operations.Any(IsShootModifier)) return false;
            if (flags.Contains("requires_last_damage") && !HasPriorDamageInResolution(operations, index)) return false;
            if (flags.Contains("requires_aim_anchor") && !operations.Any(candidate => candidate.Template == "F:Aim"))
                return false;
            if (flags.Contains("requires_damage_anchor") && !operations.Any(CardEffectRules.IsEnemyDamage))
                return false;
            if (flags.Contains("requires_block_anchor") && !operations.Any(candidate =>
                    OperationRuntimeSpecCompiler.GetOrCompile(candidate).Opcode == "gain_block")) return false;
            if (flags.Contains("requires_arrow_damage") && !operations.Any(IsShootModifier)) return false;
            if (operation.Template == "F:WhenThisDislodgesArrow" && !operations.Any(CardEffectRules.IsEnemyDamage)
                && !operations.Any(candidate => candidate.Template == "F:Dislodge")) return false;
        }
        return true;
    }

    private static bool HasPriorDamageInResolution(IReadOnlyList<GeneratorOperation> operations, int index)
    {
        var before = index;
        var owner = operations[index].Parameters.GetValueOrDefault("triggerIndex", -1);
        while (true)
        {
            if (operations.Take(before).Any(candidate => CardEffectRules.IsEnemyDamage(candidate)
                && candidate.Parameters.GetValueOrDefault("triggerIndex", -1) == owner)) return true;
            if (owner < 0 || owner >= before
                || OperationRuntimeSpecCompiler.GetOrCompile(operations[owner]).Opcode != "condition") return false;
            before = owner;
            owner = operations[owner].Parameters.GetValueOrDefault("triggerIndex", -1);
        }
    }

    public static bool IsTheShotExhaustSource(GeneratorOperation operation, bool ultimateChaos)
    {
        if (operation.Template == "F:ExhaustShoot") return true;
        if (!ultimateChaos) return false;
        if (operation.Template == "F:ExhaustSadness") return true;
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        return spec.Opcode == "exhaust_card" && (spec.Variant == "all"
            || spec.Values.Any(value => value.Id is "amount" or "count" && value.BaseValue >= 2));
    }

    public static int FindTheShotExhaustSource(IReadOnlyList<GeneratorOperation> operations, int beforeIndex,
        bool ultimateChaos)
    {
        var owner = beforeIndex < operations.Count
            ? operations[beforeIndex].Parameters.GetValueOrDefault("triggerIndex", -1) : (int?)null;
        for (var index = beforeIndex - 1; index >= 0; index--)
            if (IsTheShotExhaustSource(operations[index], ultimateChaos)
                && (owner is null || operations[index].Parameters.GetValueOrDefault("triggerIndex", -1) == owner)) return index;
        return -1;
    }

    public static bool HasValidExhaustDependencies(IReadOnlyList<GeneratorOperation> operations, bool ultimateChaos)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Template == "F:CreateTheShot" && FindTheShotExhaustSource(operations, index, ultimateChaos) < 0)
                return false;
            if (operation.Template == "F:ForEachSadnessExhausted"
                && !operations.Take(index).Any(source =>
                    (source.Template == "F:ExhaustSadness" || ultimateChaos && IsTheShotExhaustSource(source, true))
                    && source.Parameters.GetValueOrDefault("triggerIndex", -1)
                        == operation.Parameters.GetValueOrDefault("triggerIndex", -1))) return false;
        }
        return true;
    }

    internal static bool CanAppend(IReadOnlyList<GeneratorOperation> previous, ComponentAtom atom, bool ultimateChaos)
    {
        if (!atom.Template.StartsWith("F:", StringComparison.Ordinal)
            && !previous.Any(operation => operation.Template.StartsWith("F:", StringComparison.Ordinal))) return true;
        var candidate = new GeneratorOperation(atom.Template, atom.Scope, string.Empty,
            new Dictionary<string, int>(), RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom));
        if (!HasValidShootAssembly([.. previous, candidate], allowIncomplete: true)) return false;
        var flags = OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags;
        if ((atom.Template == "F:IfShotSucceeded" || flags.Contains("requires_arrow_damage"))
            && !previous.Any(IsShootModifier)) return false;
        if (flags.Contains("requires_block_anchor") && !previous.Any(operation =>
                OperationRuntimeSpecCompiler.GetOrCompile(operation).Opcode == "gain_block")) return false;
        if (atom.Template == "F:ForEachSadnessExhausted" && !previous.Any(operation =>
                operation.Template == "F:ExhaustSadness" || ultimateChaos && IsTheShotExhaustSource(operation, true))) return false;
        if (atom.Template == "F:WhenThisDislodgesArrow" && !previous.Any(CardEffectRules.IsEnemyDamage)
            && !previous.Any(operation => operation.Template == "F:Dislodge")) return false;
        if (flags.Contains("requires_aim_anchor") && !previous.Any(operation => operation.Template == "F:Aim"))
            return false;
        if (flags.Contains("requires_last_damage") && !previous.Any(CardEffectRules.IsEnemyDamage)) return false;
        if (flags.Contains("requires_exhaust_result")
            && FindTheShotExhaustSource(previous, previous.Count, ultimateChaos) < 0) return false;
        return true;
    }
}

/// <summary>Wah=1, Cho=2, Ki=4. The selector is structural metadata, never an upgradeable number in prose.</summary>
public static class FoolMelodySubset
{
    public static IReadOnlyList<CardTag> Tags(int mask)
    {
        if (mask is < 1 or > 7) throw new ArgumentOutOfRangeException(nameof(mask));
        return new[] { CardTag.Wah, CardTag.Cho, CardTag.Ki }
            .Where((_, index) => (mask & (1 << index)) != 0).ToArray();
    }

    public static ComponentAtom WithMask(ComponentAtom atom, int mask)
    {
        var tags = Tags(mask);
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(atom);
        if (!spec.Flags.Contains("melody_subset_slot")) throw new ArgumentException("Not a melody subset slot.", nameof(atom));
        var selected = spec with { CardFilter = $"melody_{mask}", Values = spec.Values.Select(value =>
            value.Id == "melody_mask" ? value with { BaseValue = mask, Upgradable = false, Explicit = false } : value).ToArray() };
        var zh = string.Join("、", tags.Select(tag => tag switch { CardTag.Wah => "“哇”", CardTag.Cho => "“啾”", _ => "“叽”" }));
        var en = string.Join(" or ", tags.Select(tag => tag.ToString()));
        var text = (atom.LocalizedText ?? FoolComponentDefinitions.Localized(atom.Template)
            ?? throw new InvalidOperationException("Melody subset localization is missing."))
            .WithTextSlotValue("melodies", zh, en);
        text.Validate(selected);
        return atom with { RuntimeSpec = selected, LocalizedText = text, ChineseText = text.RenderChinese(selected) };
    }
}
