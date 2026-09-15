using System.Globalization;
using ChaosCardGenerator;
using static AutoAnthonyCardTinkering.TinkeringText;

namespace AutoAnthonyCardTinkering;

internal enum FormulaBadgeRole
{
    Positive,
    Trigger,
    Downside
}

internal sealed record FormulaBadge(string Expression, FormulaBadgeRole Role, string Explanation);

internal sealed record ProductionCardPricing(
    double PositiveValue,
    double LinearCompensation,
    double DownsideMultiplier,
    double NetValue,
    double OrdinaryUpperBound)
{
    internal static ProductionCardPricing From(CardTinkeringBudgetBreakdown budget) => new(
        budget.PositiveValue, budget.LinearCompensation, budget.DownsideMultiplier,
        budget.NetValue, budget.OrdinaryUpperBound);
}

/// <summary>
/// Formats valuation facts returned by CardTinkeringApi. It intentionally contains no reflection and no copied
/// balance calculations; changing the production model automatically changes every number displayed here.
/// </summary>
internal static class SpecialValuePricing
{
    internal static double DisplayValue(CardTinkeringComponentAnalysis analysis, bool installed = false) =>
        analysis.UsesContextualValue
            ? installed ? analysis.ContextualValue
                : analysis.ContextualValue > 0.0001d || analysis.AtomicValue <= 0.0001d
                    ? analysis.ContextualValue
                    : analysis.AtomicValue
            : analysis.AtomicValue;

    internal static IReadOnlyList<FormulaBadge> Resolve(GeneratedCard? shell,
        IReadOnlyList<GeneratorOperation> operations, int operationIndex,
        CardTinkeringComponentAnalysis analysis, ComponentDownsidePricing downside,
        IReadOnlyList<ComponentAnalysisLine>? currentValues = null)
    {
        if ((uint)operationIndex >= (uint)operations.Count) return [];
        var operation = operations[operationIndex];
        var badges = new List<FormulaBadge>();
        if (!analysis.IsTrigger && analysis.UsesContextualValue)
        {
            var expression = currentValues is { Count: > 0 }
                ? CurrentExpression(currentValues)
                : "+" + Number(DisplayValue(analysis));
            if (ResourceCostPenalty(operation) is { } resourcePenalty)
                expression += "/-" + resourcePenalty + "C";
            badges.Add(new FormulaBadge(expression, FormulaBadgeRole.Positive,
                PositiveExplanation(shell, operation, operations, operationIndex, analysis)));
        }
        if (currentValues is null && downside.GlobalMultiplier > 1.0001d)
            badges.Add(new FormulaBadge("1/x" + Number(downside.GlobalMultiplier),
                FormulaBadgeRole.Downside, string.Empty));
        if (currentValues is null && downside.FlatValue > 0.0001d)
            badges.Add(new FormulaBadge(Localize("负代价 -", "Downside -")
                                        + Number(Math.Ceiling(downside.FlatValue)),
                FormulaBadgeRole.Downside, string.Empty));
        return badges;
    }

    internal static string AppendExplanation(string baseText, IReadOnlyList<FormulaBadge> badges)
    {
        var special = badges.Where(badge => badge.Role == FormulaBadgeRole.Positive).ToArray();
        if (special.Length == 0) return baseText;
        return baseText + Localize("\n\n估值变化：\n", "\n\nValue adjustment:\n") + string.Join("\n", special.Select(badge =>
            $"{FormulaName(badge.Expression)}={FormulaExpression(badge.Expression)}　{badge.Explanation}"));
    }

    private static string PositiveExplanation(GeneratedCard? shell, GeneratorOperation operation,
        IReadOnlyList<GeneratorOperation> operations, int operationIndex,
        CardTinkeringComponentAnalysis analysis)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        if (CardEffectRules.IsCopyThisCardToDiscard(operation))
        {
            if (CardEffectRules.IsZeroCostCopyThisCardToDiscard(operation))
                return Localize("当前占用价值直接显示在组件上；0费牌壳约束实时反映在顶部当前容量。",
                    "Its current occupied value is shown on the component; the zero-cost shell constraint is reflected by the live capacity above.");
            if (shell?.Type == GeneratedCardType.Power)
                return Localize("复制能力牌按可持续引擎计价。",
                    "Copying a Power is priced as a persistent engine.");
            if (shell?.Tags.Contains(CardTag.Exhaust) == true)
                return Localize("复制品抵消消耗损失，复制组件不再重复计入正价值。",
                    "The copy offsets the Exhaust loss, so the copy component does not add positive value again.");
            if (shell is not null && (shell.Cost != 0 || shell.StarCost > 0 || shell.HasStarCostX))
                return Localize("有费用且可重复的自我复制作为循环代价计入整卡。",
                    "Paid, repeatable self-copying is priced as a cycling downside for the whole card.");
            return Localize("免费可重复的自我复制按额外卡牌收益计价。",
                "Free, repeatable self-copying is priced as an additional-card benefit.");
        }
        if (operation.Template is "NCR:ApplyDoomEqualDamage" or "CL:GainBlockEqualDamage"
            or "CL:DamageOtherEnemiesEqual")
            return Localize("读取整张卡的伤害宿主；宿主数值、目标方式或触发次数改变时会同步变化。",
                "Reads Damage hosts across the card and updates with their values, targeting, and trigger cadence.");
        if (operation.Template == "D:SetThisCardCostZero")
            return Localize($"按牌壳的{Math.Max(0, shell?.Cost ?? 0)}点普通能量费用和所属触发链计价。",
                $"Priced from the shell's {Math.Max(0, shell?.Cost ?? 0)} Energy cost and its trigger chain.");
        if (operation.Template == "CL:IncreaseRollingDamage")
            return Localize("读取同一分支的伤害宿主，并按宿主预期触发次数的平方计算成长。",
                "Reads Damage hosts in the same branch and prices growth by the square of their expected resolutions.");
        if (CardEffectRules.IsCurrentBlockDamageModifier(operation))
            return Localize("以预期当前格挡为基准，并继承伤害宿主的目标、命中次数和触发频率。",
                "Uses expected current Block and inherits the Damage host's targeting, hit count, and trigger cadence.");
        if (operation.Template is "NCR:OstyCurrentHpBonusDamage" or "NCR:OstyMaxHpBonusDamage")
            return Localize("以奥斯提的预期生命为基准，并继承全部伤害宿主的命中与目标倍率。",
                "Uses Osty's expected HP and inherits hit and target multipliers from every Damage host.");
        if (operation.Template == "M:RepeatAreaOnKill")
            return Localize("读取全部伤害宿主追加一段的价值，并乘预期斩杀率。",
                "Prices one additional hit from every Damage host, multiplied by the expected Fatal rate.");
        if (operation.Template is "R:DoubleEnergyX" or "R:DoubleEitherXAtThreshold")
            return Localize("读取所有兼容X效果在阈值前后的收益差，并乘高额支付概率。",
                "Reads the value difference of every compatible X effect across the threshold and applies the high-payment probability.");
        if (operation.Template == "NCR:DoomPerDoomThreshold")
            return Localize("灾厄数值乘阈值的预期次数，并继承完整触发链。",
                "Multiplies Doom by the expected threshold count and inherits the complete trigger chain.");
        if (CardEffectRules.IsDynamicTotalHitModifier(operation))
            return Localize("读取全部伤害宿主与动态命中组件，共享的第一击只计一次。",
                "Reads every Damage host and dynamic hit component, counting their shared first hit only once.");
        if (operation.Template is "M:repeat" or "D:RepeatDamage" or "R:RepeatDamage"
            or "NCR:RepeatPerOstyAttackThisTurn" || spec.Flags.Contains("static_extra_damage_hits"))
            return Localize("额外命中数乘全部兼容伤害宿主的单次命中价值。",
                "Extra hits multiply the per-hit value of every compatible Damage host.");
        if (IsDependentDamageModifier(operation, spec))
            return Localize("增量作用于所有兼容伤害宿主，并继承目标、段数和触发频率。",
                "The increase applies to every compatible Damage host and inherits targeting, hit count, and trigger cadence.");
        if (operation.Template == "D:IncreaseThisCardBlockRun"
            || spec is { Opcode: "modify_block", Variant: "strength_scaled" })
            return Localize("增量作用于所有兼容格挡宿主，并继承各宿主的触发频率。",
                "The increase applies to every compatible Block host and inherits each host's trigger cadence.");
        if (CardEffectRules.IsEnergyGainOperation(operation) || CardEffectRules.IsStarGainOperation(operation))
            return Localize($"资源返还会进入有效费用，当前上下文约返还{Number(analysis.ExpectedResourceRefund)}C。",
                $"Resource refunds change effective cost; this context refunds about {Number(analysis.ExpectedResourceRefund)}C.");
        if (CardEffectRules.IsDirectOrbChannel(operation))
            return Localize("充能球种类、数量及同卡的球种多样性共同决定价值。",
                "Orb type, count, and same-card Orb diversity jointly determine this value.");
        if (operation.Template == "M:TriggeredAttackDamagePercent")
            return Localize("按未来攻击的预期伤害与所属事件触发次数计价。",
                "Priced from expected future Attack damage and the owning event's trigger count.");
        if (operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
            && triggerIndex >= 0 && triggerIndex < operationIndex)
            return Localize($"当前显示组件自身贡献{Number(analysis.ContextualValue)}；外层次数由触发器倍率单独显示。",
                $"This component contributes {Number(analysis.ContextualValue)} on its own; outer repetitions are shown on the trigger.");
        return Localize($"该组件读取整张卡结构计算，当前上下文贡献为{Number(analysis.ContextualValue)}。",
            $"This component reads the whole card structure and contributes {Number(analysis.ContextualValue)} in the current context.");
    }

    private static string? ResourceCostPenalty(GeneratorOperation operation)
    {
        if (operation.Template == "I:ProxyAtomic_DoubleEnergy") return "2";
        var isEnergy = CardEffectRules.IsEnergyGainOperation(operation);
        var isStars = CardEffectRules.IsStarGainOperation(operation);
        if (!isEnergy && !isStars) return null;
        var slot = OperationRuntimeSpecCompiler.GetOrCompile(operation).Values
            .FirstOrDefault(candidate => candidate.Id == (isEnergy ? "energy" : "stars"));
        if (slot is null) return null;
        var scale = isStars ? 0.5d : 1d;
        if (slot.Source is "energy_x" or "star_x" or "special_x")
        {
            var coefficient = scale == 1d ? string.Empty : Number(scale);
            var offset = slot.Offset * scale;
            return coefficient + "X" + (Math.Abs(offset) < 0.0001d
                ? string.Empty
                : offset > 0d ? "+" + Number(offset) : Number(offset));
        }
        return slot.Source == "fixed"
            ? Number(Math.Max(0d, slot.BaseValue + slot.Offset) * scale)
            : null;
    }

    private static bool IsDependentDamageModifier(GeneratorOperation operation, OperationRuntimeSpec spec) =>
        operation.Template is "I:IncreaseDamageThisCombat" or "D:IncreaseAllClaws"
            or "NCR:IncreaseThisCardDamageRun" or "R:DamageUpWhenDrawn"
            or "M:DamagePerExhaustCard" or "M:DamagePerDiscardThisTurn" or "M:DamagePerCardDrawnCombat"
            or "NCR:DamagePerCardDrawnThisTurn" or "NCR:DamagePerExhaustedSoul"
            or "NCR:DamagePerOstyAttackCard" or "R:BonusPerStarCostCardInHand"
            or "R:BonusPerGeneratedCardThisCombat" or "CL:BonusPerUniqueDebuff"
        || spec is { Opcode: "modify_damage", Variant: "vulnerable_scaled" or "strike_count_scaled"
            or "exhaust_pile_scaled" };

    private static string Number(double value)
    {
        if (!double.IsFinite(value)) return "?";
        if (Math.Abs(value - Math.Round(value)) < 0.0001d)
            return Math.Round(value).ToString(CultureInfo.InvariantCulture);
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string CurrentExpression(IReadOnlyList<ComponentAnalysisLine> values)
    {
        if (values.Count <= 1)
            return Signed(DisplayValue(values.FirstOrDefault()?.Analysis
                                       ?? throw new InvalidOperationException(), installed: true));
        return "X:" + string.Join('/', values.Select(line => Signed(DisplayValue(line.Analysis, installed: true))));
    }

    private static string Signed(double value) => value >= -0.0001d
        ? "+" + Number(Math.Max(0d, value))
        : Number(value);

    internal static string FormulaName(string expression) =>
        expression.StartsWith("X:", StringComparison.Ordinal) ? "f(X)" : "f()";

    internal static string FormulaExpression(string expression) =>
        expression.StartsWith("X:", StringComparison.Ordinal) ? expression[2..] : expression;
}
