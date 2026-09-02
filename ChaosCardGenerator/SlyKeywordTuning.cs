namespace ChaosCardGenerator;

/// <summary>
/// Prices Sly as a final printed-cost transformation rather than as effect budget.  A Sly card is assembled and
/// balanced as an ordinary 0- or 1-Energy card, then gains one or two printed Energy after every numeric pass.
/// This preserves native Tactician-style cards without letting the keyword buy additional reward scaling.
/// </summary>
internal static class SlyKeywordTuning
{
    // Native Silent has 6 actual Sly cards among 86 reviewed single-player cards (6.98%). Hand Trick and Master
    // Planner mention/grant Sly but do not carry the keyword themselves and must not inflate this prior. Cost
    // eligibility, whole-card rejection and both pool support repairs thin the initial roll; the retained lift
    // keeps the finished rate near or slightly above the native pool without raising Sly-granting operations.
    internal const int SilentTagWeightPercent = 120;

    internal static int AdjustTagNumerator(int numerator, GeneratedCharacter character, bool unifiedChaos) =>
        character == GeneratedCharacter.Silent && !unifiedChaos
            ? Math.Max(1, (numerator * SilentTagWeightPercent + 50) / 100)
            : numerator;

    internal static bool CanAttach(int compensatedEnergyCost,
        SpecialXGenerationMode specialXMode, bool hasExtremeLifecycleDownside) =>
        specialXMode != SpecialXGenerationMode.Forced
        && !hasExtremeLifecycleDownside
        // Explicit downsides are allowed to buy a lower template cost before Sly is considered. The remaining
        // whole-card passes re-price the complete payload against this compensated 0/1-cost coordinate, so a
        // formerly higher-cost shell cannot retain higher-cost numbers merely by acquiring Sly afterwards.
        && compensatedEnergyCost is 0 or 1;

    internal static int ApplyPrintedCostIncrease(int budgetedEnergyCost, bool hasSly, Random random) =>
        hasSly ? budgetedEnergyCost + random.Next(1, 3) : budgetedEnergyCost;

    internal static bool IsStrictPrintedCostIncrease(int templateEnergyCost, int printedEnergyCost) =>
        templateEnergyCost is 0 or 1
        && printedEnergyCost is >= 1 and <= 3
        && printedEnergyCost > templateEnergyCost
        && printedEnergyCost - templateEnergyCost is 1 or 2;

    /// <summary>
    /// A serialized Sly card retains only its printed cost. Printed 1 and 3 unambiguously came from a 0- and
    /// 1-Energy template respectively. Printed 2 can be either 0+2 or 1+1, so audits compare its actual positive
    /// payload with both native whole-card centers and use the closer template. This keeps Flick Flack anchored as
    /// a strong zero-cost template while Ricochet/Untouchable resolve to their more plausible 1/0-cost templates.
    /// </summary>
    internal static int ValidationTemplateCost(int printedEnergyCost, int starCost, bool hasStarCostX,
        GeneratedRarity rarity, GeneratedCardType cardType, IReadOnlyCollection<CardTag> tags,
        IReadOnlyList<GeneratorOperation> operations)
    {
        if (printedEnergyCost == 1) return 0;
        if (printedEnergyCost == 3) return 1;
        if (printedEnergyCost != 2)
            throw new InvalidOperationException("奇巧牌的印刷普通费用必须为1至3。 ");

        var zeroDistance = TemplateDistance(0, starCost, hasStarCostX, rarity, cardType, tags, operations);
        var oneDistance = TemplateDistance(1, starCost, hasStarCostX, rarity, cardType, tags, operations);
        return zeroDistance <= oneDistance ? 0 : 1;
    }

    private static double TemplateDistance(int templateEnergyCost, int starCost, bool hasStarCostX,
        GeneratedRarity rarity, GeneratedCardType cardType, IReadOnlyCollection<CardTag> tags,
        IReadOnlyList<GeneratorOperation> operations)
    {
        var effectiveCost = ResourceEconomyModel.BudgetEffectiveCost(templateEnergyCost, starCost,
            hasEnergyX: false, hasStarCostX, operations);
        var center = ComponentAssemblyGenerator.CalibratedWholeCardCenter(rarity, effectiveCost);
        var hasPrintedResourceCost = templateEnergyCost > 0 || starCost > 0 || hasStarCostX;
        var actual = EffectBalanceModel.EstimatedPositiveCardValue(operations, hasPrintedResourceCost,
            cardType, tags);
        if (actual <= 0d || center <= 0d) return Math.Abs(actual - center);
        // Relative (logarithmic) distance treats equally large high/low ratios symmetrically.
        return Math.Abs(Math.Log(actual / center));
    }

    /// <summary>
    /// A card whose only immediate reward merely refunds its own Energy is dead without Sly, but is a useful
    /// discard payoff with it. Other rewards, delayed refunds and triggered refunds keep their ordinary rules.
    /// </summary>
    internal static bool IsPureImmediateSelfRefund(int budgetedEnergyCost,
        IReadOnlyList<GeneratorOperation> operations)
    {
        if (budgetedEnergyCost < 0) return false;
        var rewards = operations.Where(CardEffectRules.IsBeneficialEffect).ToArray();
        if (rewards.Length == 0 || rewards.Any(operation => !CardEffectRules.IsEnergyGainOperation(operation)))
            return false;
        var immediateRefund = rewards
            .Where(operation => !operation.Parameters.ContainsKey("triggerIndex")
                && !CardEffectRules.IsDelayedEffect(operation))
            .Sum(operation => OperationRuntimeSpecCompiler.StaticLiteralValue(operation,
                CardEffectRules.IsEnergyGainOperation(operation) ? "energy" : "stars"));
        return immediateRefund >= Math.Max(1, budgetedEnergyCost);
    }

    internal static void Validate()
    {
        if (AdjustTagNumerator(10, GeneratedCharacter.Silent, unifiedChaos: false) != 12
            || AdjustTagNumerator(10, GeneratedCharacter.Ironclad, unifiedChaos: false) != 10
            || AdjustTagNumerator(10, GeneratedCharacter.Silent, unifiedChaos: true) != 10
            || !CanAttach(0, SpecialXGenerationMode.Normal, false)
            || !CanAttach(1, SpecialXGenerationMode.Normal, false)
            || CanAttach(-1, SpecialXGenerationMode.Normal, false)
            || CanAttach(2, SpecialXGenerationMode.Normal, false)
            || CanAttach(1, SpecialXGenerationMode.Forced, false)
            || CanAttach(1, SpecialXGenerationMode.Normal, true))
            throw new InvalidOperationException("奇巧只能在负面补偿后、最终加费前的0至1费普通候选上生成。 ");

        var surcharges = Enumerable.Range(0, 1_000)
            .Select(seed => ApplyPrintedCostIncrease(1, true, new Random(seed)) - 1)
            .ToArray();
        if (surcharges.Any(value => value is not (1 or 2))
            || !surcharges.Contains(1) || !surcharges.Contains(2)
            || Enumerable.Range(0, 2).Any(template => Enumerable.Range(0, 1_000).Any(seed =>
            {
                var printed = ApplyPrintedCostIncrease(template, true, new Random(seed));
                return !IsStrictPrintedCostIncrease(template, printed);
            })))
            throw new InvalidOperationException("奇巧的最终普通费用增量必须为1或2。 ");

        var flickFlack = new GeneratorOperation("N:AllD", OperationScope.NonTargeted,
            "对所有敌人造成7点伤害。", new Dictionary<string, int>(),
            RuntimeSpec: CatalogRuntimeSpecRegistry.Get("silent/flickflack/0"));
        var ricochet = new GeneratorOperation("N:RandomD", OperationScope.NonTargeted,
            "随机对敌人造成3点伤害4次。", new Dictionary<string, int>(),
            RuntimeSpec: CatalogRuntimeSpecRegistry.Get("silent/ricochet/0"));
        var untouchable = new GeneratorOperation("N:B", OperationScope.NonTargeted,
            "获得6点格挡。", new Dictionary<string, int>(),
            RuntimeSpec: CatalogRuntimeSpecRegistry.Get("silent/untouchable/0"));
        if (ValidationTemplateCost(1, -1, false, GeneratedRarity.Common, GeneratedCardType.Attack,
                [CardTag.Sly], [flickFlack]) != 0
            || ValidationTemplateCost(2, -1, false, GeneratedRarity.Common, GeneratedCardType.Attack,
                [CardTag.Sly], [ricochet]) != 1
            || ValidationTemplateCost(2, -1, false, GeneratedRarity.Common, GeneratedCardType.Skill,
                [CardTag.Sly], [untouchable]) != 0)
            throw new InvalidOperationException("奇巧的0/1费数值模板推断未保持原版校准锚点。 ");

        var energy = new GeneratorOperation("N:E", OperationScope.NonTargeted, "获得1点能量。",
            new Dictionary<string, int>());
        var draw = new GeneratorOperation("N:Draw", OperationScope.NonTargeted, "抽1张牌。",
            new Dictionary<string, int>());
        var delayedEnergy = new GeneratorOperation("N:NextTurnEnergy", OperationScope.NonTargeted,
            "下一回合获得1点能量。", new Dictionary<string, int>());
        if (!IsPureImmediateSelfRefund(1, [energy])
            || IsPureImmediateSelfRefund(1, [energy, draw])
            || IsPureImmediateSelfRefund(1, [delayedEnergy]))
            throw new InvalidOperationException("奇巧专属的纯即时自回费模板识别失效。 ");
    }
}
