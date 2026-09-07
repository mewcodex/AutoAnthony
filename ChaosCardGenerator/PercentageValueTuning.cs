namespace ChaosCardGenerator;

/// <summary>
/// Shared policy for printed percentage-point values. Percentages are semantic rates, not ordinary scalar counts:
/// they use native-card anchors for valuation, a compact five-point generation grid, and meaningful upgrade steps.
/// Keeping all three decisions together prevents a 50% rule from being generated or upgraded like “50 stacks”.
/// </summary>
internal static class PercentageValueTuning
{
    internal const int UpgradeQuantum = 5;

    internal static bool IsPercentage(ComponentAtom atom) =>
        IsPercentage(OperationRuntimeSpecCompiler.GetOrCompile(atom));

    internal static bool IsPercentage(GeneratorOperation operation) =>
        IsPercentage(OperationRuntimeSpecCompiler.GetOrCompile(operation));

    private static bool IsPercentage(OperationRuntimeSpec spec) =>
        spec.Flags.Contains("percentage_value", StringComparer.Ordinal);

    /// <summary>
    /// Prices the four v111 percentage mechanics from their native complete-card shells. The result is expressed in
    /// hundredths of one ordinary single-target Damage, like the rest of <see cref="EffectBalanceModel"/>.
    /// </summary>
    internal static bool TryEstimate(OperationRuntimeSpec spec, out int value)
    {
        value = 0;
        if (!IsPercentage(spec)) return false;
        var slot = spec.Values.FirstOrDefault(candidate => candidate.Explicit && candidate.Source == "fixed");
        if (slot is null) return false;
        var percentage = Math.Max(0, slot.BaseValue + slot.Offset);
        var valuePerPoint = (spec.Opcode, spec.Variant, spec.Trigger?.Kind) switch
        {
            // Colossus: 4 Block (480) + this 50% rule (900) ~= the 1-Energy Uncommon center (1,400).
            (_, _, "vulnerable_enemy_damage_reduction") => 18,
            // Cruelty is a one-Energy Uncommon Power whose sole 25% rule occupies essentially the full shell.
            ("combat_rule", "vulnerable_enemy_damage_bonus", _) => 56,
            // Tracking is a two-Energy Rare Power whose sole 50% rule occupies its ~4,000-point shell.
            ("combat_rule", "weak_enemy_attack_damage_bonus", _) => 80,
            // Lethality: 50% of an ordinary 10-Damage first Attack, repeated for about three active turns.
            // The linked trigger supplies the 3x cadence, so this atomic modifier is 500 before linkage.
            ("modify_damage", "triggered_attack_percentage", _) => 10,
            _ => 0
        };
        if (valuePerPoint <= 0) return false;
        value = percentage * valuePerPoint;
        return true;
    }

    /// <summary>
    /// Native percentages are 25% or 50%. Recombined cards retain a continuous but readable neighborhood around
    /// that anchor rather than inheriting cost/rarity scaling intended for Damage and Block.
    /// </summary>
    internal static int SampleGeneratedValue(ComponentAtom atom, int original, Random random)
    {
        if (!IsPercentage(atom)) return original;
        var offsetSteps = random.Next(100) switch
        {
            < 10 => -2,
            < 30 => -1,
            < 70 => 0,
            < 90 => 1,
            _ => 2
        };
        var sampled = Math.Max(UpgradeQuantum, RoundToQuantum(original) + offsetSteps * UpgradeQuantum);
        return NumericGenerationTuning.ClampUniversalFixedValue(atom.Template,
            OperationRuntimeSpecCompiler.GetOrCompile(atom), PercentageSlotId(atom), sampled);
    }

    /// <summary>
    /// Samples percentage-point upgrades from native behavior. Cruelty doubles 25% to 50%, Lethality gains 25
    /// points from 50%, while Tracking and Colossus have cost/Block upgrades and therefore use conservative inferred
    /// percentage alternatives. Every newly generated numeric upgrade remains a multiple of five percentage points.
    /// </summary>
    internal static int SampleUpgradeIncrease(GeneratorOperation operation, int value, Random random)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        var center = (spec.Opcode, spec.Variant, spec.Trigger?.Kind) switch
        {
            ("combat_rule", "vulnerable_enemy_damage_bonus", _) => value * 0.90d,
            ("combat_rule", "weak_enemy_attack_damage_bonus", _) => value * 0.30d,
            ("modify_damage", "triggered_attack_percentage", _) => value * 0.50d,
            (_, _, "vulnerable_enemy_damage_reduction") => value * 0.20d,
            _ => value * 0.25d
        };
        var multiplier = random.Next(1000) switch
        {
            < 200 => 0.85d,
            < 700 => 1.05d,
            < 950 => 1.15d,
            _ => 1.30d
        };
        var sampled = RoundToQuantum(center * multiplier);
        return Math.Clamp(sampled, Math.Min(UpgradeQuantum, Math.Max(1, value)), Math.Max(1, value));
    }

    internal static int UpgradeStep(GeneratorOperation operation) => IsPercentage(operation) ? UpgradeQuantum : 1;

    /// <summary>
    /// Normalizes a percentage generated outside the ordinary component sampler (currently Numeric Random mode).
    /// Percentage-point fields stay on the same readable five-point grid as ordinary generation, even though that
    /// mode deliberately bypasses the normal balance range.
    /// </summary>
    internal static int NormalizeGeneratedValue(GeneratorOperation operation, string slotId, int value)
    {
        if (!IsPercentageSlot(operation, slotId)) return value;
        var normalized = RoundToQuantum(value);
        return NumericGenerationTuning.ClampUniversalFixedValue(operation, slotId, normalized);
    }

    /// <summary>
    /// Upgrade plans can arrive from old snapshots or editor integrations as well as the current candidate sampler.
    /// Enforce the semantic percentage-point quantum at the application boundary so a stale +1 plan cannot turn
    /// 50% into 51%. Returning zero deliberately preserves the card when no complete five-point step remains below
    /// an engine-safety cap.
    /// </summary>
    internal static int NormalizeAppliedIncrease(GeneratorOperation operation, string slotId, int current,
        int requestedDelta)
    {
        if (requestedDelta <= 0 || !IsPercentageSlot(operation, slotId)) return requestedDelta;
        var magnitude = RoundToQuantum(requestedDelta);
        magnitude = ClampUpgradeMagnitude(operation, slotId, current, magnitude);
        magnitude -= magnitude % UpgradeQuantum;
        return magnitude;
    }

    internal static int ClampUpgradeMagnitude(GeneratorOperation operation, string slotId, int current,
        int magnitude)
    {
        magnitude = Math.Min(Math.Max(0, magnitude), Math.Max(0, current));
        if (NumericGenerationTuning.UniversalFixedValueCap(operation, slotId) is { } cap)
            magnitude = Math.Min(magnitude, Math.Max(0, cap - current));
        return magnitude;
    }

    private static string PercentageSlotId(ComponentAtom atom) =>
        OperationRuntimeSpecCompiler.GetOrCompile(atom).Values
            .First(value => value.Explicit && value.Source == "fixed").Id;

    private static bool IsPercentageSlot(GeneratorOperation operation, string slotId)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        if (!IsPercentage(spec)) return false;
        return spec.Values.FirstOrDefault(value => value.Explicit && value.Source == "fixed")?.Id == slotId;
    }

    private static int RoundToQuantum(double value) => Math.Max(UpgradeQuantum,
        (int)Math.Round(value / UpgradeQuantum, MidpointRounding.AwayFromZero) * UpgradeQuantum);

    internal static void Validate()
    {
        var reduction = OperationRuntimeSpecCompiler.CompileRequired(new GeneratorOperation(
            "C:VulnerableEnemyDamageReductionThisTurn", OperationScope.ConditionalTrigger,
            "在本回合中，有易伤状态的敌人对你造成的伤害降低50%。", new Dictionary<string, int>()));
        var cruelty = OperationRuntimeSpecCompiler.CompileRequired(new GeneratorOperation(
            "A:rule", OperationScope.AbilityRule, "拥有易伤的敌人受到的伤害增加25%。",
            new Dictionary<string, int>()));
        var tracking = OperationRuntimeSpecCompiler.CompileRequired(new GeneratorOperation(
            "A:ruleWeakEnemiesTakeMoreAttackDamage", OperationScope.AbilityRule,
            "处于虚弱状态的敌人受到的攻击伤害增加50%。", new Dictionary<string, int>()));
        var lethality = OperationRuntimeSpecCompiler.CompileRequired(new GeneratorOperation(
            "M:TriggeredAttackDamagePercent", OperationScope.Modifier, "该攻击牌造成的伤害增加50%。",
            new Dictionary<string, int>()));
        if (!TryEstimate(reduction, out var reductionValue) || reductionValue != 900
            || !TryEstimate(cruelty, out var crueltyValue) || crueltyValue != 1_400
            || !TryEstimate(tracking, out var trackingValue) || trackingValue != 4_000
            || !TryEstimate(lethality, out var lethalityValue) || lethalityValue != 500)
            throw new InvalidOperationException("百分比组件没有按原版整卡锚点计价。");

        var reductionAtom = new ComponentAtom("C:VulnerableEnemyDamageReductionThisTurn",
            OperationScope.ConditionalTrigger,
            "在本回合中，有易伤状态的敌人对你造成的伤害降低50%。", false,
            CardReferenceRequirement.None) { RuntimeSpec = reduction };
        var generated = Enumerable.Range(0, 1_000)
            .Select(seed => SampleGeneratedValue(reductionAtom, 50, new Random(seed))).ToArray();
        if (generated.Any(percentage => percentage is < 40 or > 60 || percentage % UpgradeQuantum != 0)
            || generated.Distinct().Count() < 5)
            throw new InvalidOperationException("百分比生成值没有围绕原版值使用完整的五百分点网格。");

        foreach (var operation in new[]
                 {
                     new GeneratorOperation("C:VulnerableEnemyDamageReductionThisTurn",
                         OperationScope.ConditionalTrigger,
                         "在本回合中，有易伤状态的敌人对你造成的伤害降低50%。",
                         new Dictionary<string, int>()),
                     new GeneratorOperation("A:rule", OperationScope.AbilityRule,
                         "拥有易伤的敌人受到的伤害增加25%。", new Dictionary<string, int>()),
                     new GeneratorOperation("A:ruleWeakEnemiesTakeMoreAttackDamage", OperationScope.AbilityRule,
                         "处于虚弱状态的敌人受到的攻击伤害增加50%。", new Dictionary<string, int>()),
                     new GeneratorOperation("M:TriggeredAttackDamagePercent", OperationScope.Modifier,
                         "该攻击牌造成的伤害增加50%。", new Dictionary<string, int>())
                 })
        {
            var current = OperationRuntimeSpecCompiler.PrimaryFixedValue(operation) ?? 0;
            var samples = Enumerable.Range(0, 500)
                .Select(seed => SampleUpgradeIncrease(operation, current, new Random(seed))).ToArray();
            if (samples.Any(delta => delta < UpgradeQuantum || delta > current
                                     || delta % UpgradeQuantum != 0))
                throw new InvalidOperationException($"百分比组件 {operation.Template} 仍会生成±1式升级。");

            var slotId = OperationRuntimeSpecCompiler.UpgradeValueSlot(operation)
                         ?? throw new InvalidOperationException($"百分比组件 {operation.Template} 缺少升级槽位。");
            var applied = CardUpgradeGenerator.ApplyEffectsToOperations([operation],
                [new CardUpgradeEffect(CardUpgradeKind.IncreaseNumber, 0, 1, ValueSlotId: slotId)])[0];
            var appliedSlot = OperationRuntimeSpecCompiler.GetOrCompile(applied).Values
                .First(value => value.Id == slotId);
            var appliedValue = appliedSlot.BaseValue + appliedSlot.Offset;
            var expectedAppliedDelta = CardEffectRules.IsNonUpgradeableNumericMarker(operation)
                ? 0
                : UpgradeQuantum;
            if (appliedValue - current != expectedAppliedDelta)
                throw new InvalidOperationException($"百分比组件 {operation.Template} 的旧版/API +1升级未在应用边界规范化："
                                                    + $"slot={slotId}, current={current}, applied={appliedValue}, "
                                                    + $"normalized={NormalizeAppliedIncrease(operation, slotId, current, 1)}。");
            if (NormalizeGeneratedValue(operation, slotId, 51) != 50)
                throw new InvalidOperationException($"百分比组件 {operation.Template} 的随机数值未保持五百分点网格。");
        }


        var catalogAtoms = Enum.GetValues<GeneratedCharacter>()
            .SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms)
            .GroupBy(atom => atom.SemanticId ?? atom.Template, StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();
        foreach (var atom in catalogAtoms)
        {
            var isPercentage = IsPercentage(atom);
            if (!isPercentage) continue;
            var fixedSlots = OperationRuntimeSpecCompiler.GetOrCompile(atom).Values
                .Where(value => value.Explicit && value.Source == "fixed").ToArray();
            if (fixedSlots.Length != 1 || !fixedSlots[0].Upgradable
                                       || (fixedSlots[0].BaseValue + fixedSlots[0].Offset) % UpgradeQuantum != 0)
                throw new InvalidOperationException($"百分比组件 {atom.Template} 没有唯一、可升级且位于五百分点网格的数值槽位。");
        }

        static string PercentageKind(OperationRuntimeSpec spec) =>
            $"{spec.Opcode}|{spec.Variant}|{spec.Trigger?.Kind ?? string.Empty}";
        var expectedKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "trigger|event|vulnerable_enemy_damage_reduction",
            "combat_rule|vulnerable_enemy_damage_bonus|",
            "combat_rule|weak_enemy_attack_damage_bonus|",
            "modify_damage|triggered_attack_percentage|"
        };
        var actualKinds = catalogAtoms.Where(IsPercentage)
            .Select(atom => PercentageKind(OperationRuntimeSpecCompiler.GetOrCompile(atom)))
            .ToHashSet(StringComparer.Ordinal);
        if (!actualKinds.SetEquals(expectedKinds))
            throw new InvalidOperationException("v111百分比组件目录覆盖不完整或出现了未经校准的新百分比语义："
                                                + string.Join(", ", actualKinds));
    }
}
