namespace ChaosCardGenerator;

/// <summary>
/// Shared numeric-upgrade prior fitted from the 483 v111 single-player cards represented by the six component
/// catalogs. The source audit is kept in audits/upgrade_distribution_v111.md. Values are sampled around the native
/// conditional mean instead of being restricted to native integer sets. Components without a native numeric-upgrade
/// precedent use +1 as their fallback; +1 is not injected into families that already have a fitted distribution.
/// </summary>
internal static class NativeUpgradeValueModel
{
    internal enum Family
    {
        Damage,
        Block,
        Draw,
        Energy,
        PoisonOrDoom,
        OstyDamage,
        SummonOrForge,
        Debuff,
        StrengthOrFocus,
        ThornsOrPlating,
        TemporaryEnemyStrengthLoss,
        RepeatOrCardCount,
        HealingOrMaxHp,
        Gold,
        Other
    }

    internal static int SampleIncrease(GeneratorOperation operation, int value, Random random,
        Family? forcedFamily = null)
    {
        if (value <= 1) return 1;
        if (PercentageValueTuning.IsPercentage(operation))
            return PercentageValueTuning.SampleUpgradeIncrease(operation, value, random);
        var family = forcedFamily ?? Classify(operation);
        // Native draw upgrades overwhelmingly add one card. Keep this axis deterministic so large printed draw
        // values never turn a single upgrade into +2 or more cards.
        if (family == Family.Draw) return 1;
        // “Allow +1” means an unrepresented component is still a valid numeric-upgrade candidate. It does not mean
        // weakening every fitted native distribution with an artificial +1 probability branch.
        if (family == Family.Other) return 1;
        var center = NativeCenter(family, value);
        return SampleAroundCenter(center, value, random);
    }

    private static int SampleAroundCenter(double center, int value, Random random)
    {
        // The native distribution is the center of the sampler. This multiplier has a mean of 1.0475, preserving
        // native shape while shifting it slightly upward and retaining a modest upper tail.
        var roll = random.Next(1000);
        var multiplier = roll switch
        {
            < 200 => 0.85d,
            < 700 => 1.05d,
            < 950 => 1.15d,
            _ => 1.30d
        };
        var raw = center * multiplier;
        var lower = Math.Max(1, (int)Math.Floor(raw));
        var fraction = raw - lower;
        var sampled = lower + (fraction > 0d && random.NextDouble() < fraction ? 1 : 0);

        // No generated numeric upgrade may add more than the card's original printed value.
        return Math.Clamp(sampled, 1, value);
    }

    internal static Family Classify(GeneratorOperation operation)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        if (CardEffectRules.IsEnemyDamage(operation)
            || operation.Template is "N:RetaliateDamage" or "T_DAMAGE" or "N_RANDOM_DAMAGE")
            return operation.Template is "NCR:OstyDamage" or "NCR:OstyAllDamage"
                ? Family.OstyDamage
                : Family.Damage;
        if (operation.Template is "N:B" or "N_BLOCK" || spec.Flags.Contains("block_reference"))
            return Family.Block;
        if (spec.Flags.Contains("plating_reference") || spec.Flags.Contains("thorns_reference"))
            return Family.ThornsOrPlating;
        // These slots are printed damage magnitudes even though they modify a host attack instead of resolving an
        // immediate hit. Static extra-hit counts remain in RepeatOrCardCount; treating both as Damage would make a
        // “+1 hit” upgrade scale like “+N damage per hit”.
        if (IsDamageMagnitude(operation, spec))
            return Family.Damage;
        if (operation.Template is "N:Draw" or "N_DRAW" or "I:DrawWithRetain"
            || spec.Flags.Contains("leading_draw_reference"))
            return Family.Draw;
        if (CardEffectRules.IsEnergyGainOperation(operation)) return Family.Energy;
        if (operation.Template.Contains("Doom", StringComparison.Ordinal)
            || operation.Template.Contains("Poison", StringComparison.Ordinal)
            || spec.Flags.Contains("doom_reference") || spec.Flags.Contains("poison_reference"))
            return Family.PoisonOrDoom;
        if (operation.Template.Contains("Summon", StringComparison.Ordinal)
            || operation.Template.Contains("Forge", StringComparison.Ordinal)
            || spec.Flags.Contains("summon_reference") || spec.Flags.Contains("forge_reference"))
            return Family.SummonOrForge;
        if (CardEffectRules.IsEnemyStrengthReduction(operation)
            && spec.Flags.Contains("this_turn_reference"))
            return Family.TemporaryEnemyStrengthLoss;
        if (spec.Flags.Contains("vulnerable_reference") || spec.Flags.Contains("weak_reference")
            || spec.Flags.Contains("strength_loss_wording"))
            return Family.Debuff;
        if (spec.Flags.Contains("strength_reference") || spec.Flags.Contains("dexterity_reference")
            || spec.Flags.Contains("focus_reference"))
            return Family.StrengthOrFocus;
        if (operation.Template is "N:Heal" or "N_HEAL" or "I:GainMaxHp"
            || spec.Flags.Contains("leading_heal_reference") || spec.Flags.Contains("max_hp_reference"))
            return Family.HealingOrMaxHp;
        if (CardEffectRules.IsGoldGainOperation(operation)) return Family.Gold;
        if (spec.Flags.Contains("count_unit_reference"))
            return Family.RepeatOrCardCount;
        return Family.Other;
    }

    internal static Family LegacyClassifyForAudit(GeneratorOperation operation)
    {
        var text = operation.ChineseText;
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        if (CardEffectRules.IsEnemyDamage(operation)
            || operation.Template is "N:RetaliateDamage" or "T_DAMAGE" or "N_RANDOM_DAMAGE")
            return operation.Template is "NCR:OstyDamage" or "NCR:OstyAllDamage"
                ? Family.OstyDamage : Family.Damage;
        if (operation.Template is "N:B" or "N_BLOCK" || text.Contains("格挡", StringComparison.Ordinal))
            return Family.Block;
        if (spec.Flags.Contains("plating_reference") || spec.Flags.Contains("thorns_reference"))
            return Family.ThornsOrPlating;
        if (IsDamageMagnitude(operation, spec)) return Family.Damage;
        if (operation.Template is "N:Draw" or "N_DRAW" or "I:DrawWithRetain"
            || text.TrimStart().StartsWith("抽", StringComparison.Ordinal)) return Family.Draw;
        if (CardEffectRules.IsEnergyGainOperation(operation)) return Family.Energy;
        if (operation.Template.Contains("Doom", StringComparison.Ordinal)
            || operation.Template.Contains("Poison", StringComparison.Ordinal)
            || text.Contains("灾厄", StringComparison.Ordinal) || text.Contains("中毒", StringComparison.Ordinal))
            return Family.PoisonOrDoom;
        if (operation.Template.Contains("Summon", StringComparison.Ordinal)
            || operation.Template.Contains("Forge", StringComparison.Ordinal)
            || text.Contains("召唤", StringComparison.Ordinal) || text.Contains("铸造", StringComparison.Ordinal))
            return Family.SummonOrForge;
        if (CardEffectRules.IsEnemyStrengthReduction(operation)
            && spec.Flags.Contains("this_turn_reference"))
            return Family.TemporaryEnemyStrengthLoss;
        if (text.Contains("易伤", StringComparison.Ordinal) || text.Contains("虚弱", StringComparison.Ordinal)
            || text.Contains("失去力量", StringComparison.Ordinal)) return Family.Debuff;
        if (text.Contains("力量", StringComparison.Ordinal) || text.Contains("敏捷", StringComparison.Ordinal)
            || text.Contains("集中", StringComparison.Ordinal)) return Family.StrengthOrFocus;
        if (operation.Template is "N:Heal" or "N_HEAL" or "I:GainMaxHp"
            || text.StartsWith("回复", StringComparison.Ordinal) || text.Contains("最大生命", StringComparison.Ordinal))
            return Family.HealingOrMaxHp;
        if (CardEffectRules.IsGoldGainOperation(operation)) return Family.Gold;
        if (text.Contains("张", StringComparison.Ordinal) || text.Contains("次", StringComparison.Ordinal)
            || text.Contains("颗", StringComparison.Ordinal) || text.Contains("个", StringComparison.Ordinal))
            return Family.RepeatOrCardCount;
        return Family.Other;
    }

    private static bool IsDamageMagnitude(GeneratorOperation operation, OperationRuntimeSpec spec)
    {
        if (PercentageValueTuning.IsPercentage(operation)
            || CardEffectRules.IsStaticExtraDamageHitModifier(operation))
            return false;
        if (operation.Template is "N:Vigor" or "R:GainVigor" or "CL:GainVigor"
                or "I:IncreaseDamageThisCombat" or "NCR:IncreaseThisCardDamageRun"
                or "D:IncreaseAllClaws" or "R:DamageUpWhenDrawn" or "CL:IncreaseRollingDamage")
            return true;
        return spec.Flags.Contains("damage_budget_effect")
               && spec.Values.Any(value => value.Explicit && value.Source == "fixed");
    }

    internal static double NativeCenter(Family family, int value) => family switch
    {
        // Source conditional means: 1-3 / 4-6 / 7-10 / 11-16 / 17+.
        Family.Damage => value switch
        {
            <= 3 => 1.30d,
            <= 6 => 2.27d,
            <= 10 => 2.69d,
            <= 16 => 4.32d,
            _ => Math.Max(5d, value * 0.34d)
        },
        Family.Block => value switch
        {
            <= 3 => 1.50d,
            <= 6 => 2.54d,
            <= 10 => 2.68d,
            <= 16 => 3.50d,
            _ => Math.Max(6d, value / 3d)
        },
        Family.Draw => 1d,
        Family.Energy => value <= 3 ? 1d : 2d,
        Family.PoisonOrDoom => Math.Max(1.15d, value * 0.42d),
        Family.OstyDamage => Math.Max(1.25d, value * 0.42d),
        Family.SummonOrForge => Math.Max(1.25d, value * 0.43d),
        Family.Debuff => 1.12d,
        Family.StrengthOrFocus => 1d,
        // Stone Armor / Neutron Aegis / Eternal Armor upgrade 4/8/9 Plating by 2/3/3; Abrasive upgrades
        // 4 Thorns by 2. A shared 40%-of-field curve reconstructs those anchors while avoiding a large-field +1.
        Family.ThornsOrPlating => Math.Max(2d, value * 0.40d),
        // The five native temporary enemy-Strength reductions upgrade by 2-6 from bases 6-10 (roughly 42%).
        // Vulnerable and Weak remain in the ordinary Debuff family because their stacks primarily mark duration.
        Family.TemporaryEnemyStrengthLoss => Math.Max(2d, value * 0.42d),
        Family.RepeatOrCardCount => value <= 3 ? 1d : Math.Max(1d, value * 0.30d),
        Family.HealingOrMaxHp => Math.Max(1d, value * 0.35d),
        // Hand of Greed upgrades 20 -> 25, while Royalties upgrades 30 -> 40. Preserve both v111 anchors and
        // retain the shared slightly-above-native sampling tail instead of falling through to the +1 fallback.
        Family.Gold => value <= 20 ? Math.Max(5d, value * 0.25d) : Math.Max(5d, value / 3d),
        _ => 1d
    };

    internal static void Validate()
    {
        PercentageValueTuning.Validate();
        foreach (var family in Enum.GetValues<Family>())
        {
            var operation = Probe(family);
            foreach (var value in Enumerable.Range(1, 60))
            {
                var samples = Enumerable.Range(0, 300)
                    .Select(seed => SampleIncrease(operation, value, new Random(seed * 7919 + value), family))
                    .ToArray();
                if (samples.Any(delta => delta < 1 || delta > value))
                    throw new InvalidOperationException($"{family}/{value} 的升级数值超过100%上限。");
                if (family == Family.Other && samples.Any(delta => delta != 1))
                    throw new InvalidOperationException($"无原版升级样本的{family}/{value}没有固定使用+1兜底。");
            }
        }

        var royalties = new GeneratorOperation("A:ProxyAtomic_Royalties", OperationScope.AbilityRule,
            "在战斗结束时，获得30金币。", new Dictionary<string, int>());
        var royaltiesDeltas = Enumerable.Range(0, 300)
            .Select(seed => SampleIncrease(royalties, 30, new Random(seed))).ToArray();
        if (Classify(royalties) != Family.Gold
            || royaltiesDeltas.Any(delta => delta < 8 || delta > 13))
            throw new InvalidOperationException("金币效果仍使用普通小数值升级，或偏离王国资产的30→40锚点。");

        var plating = new GeneratorOperation("N:Self", OperationScope.NonTargeted,
            "获得10层覆甲。", new Dictionary<string, int>());
        var thorns = new GeneratorOperation("N:Thorns", OperationScope.NonTargeted,
            "获得10点荆棘。", new Dictionary<string, int>());
        var temporaryStrengthLoss = new GeneratorOperation("T:TempStrengthLoss",
            OperationScope.SingleEnemyOnly, "该敌人在本回合失去10点力量。", new Dictionary<string, int>());
        var damageModifier = new GeneratorOperation("R:DamageUpWhenDrawn", OperationScope.Modifier,
            "本场战斗此牌基础伤害增加10。", new Dictionary<string, int>());
        foreach (var (operation, expectedFamily) in new[]
                 {
                     (plating, Family.ThornsOrPlating),
                     (thorns, Family.ThornsOrPlating),
                     (temporaryStrengthLoss, Family.TemporaryEnemyStrengthLoss),
                     (damageModifier, Family.Damage)
                 })
        {
            var deltas = Enumerable.Range(0, 300)
                .Select(seed => SampleIncrease(operation, 10, new Random(seed))).ToArray();
            if (Classify(operation) != expectedFamily || deltas.Any(delta => delta < 2))
                throw new InvalidOperationException($"{operation.Template} 仍会把大数值升级退化为通用+1。 ");
        }
    }

    private static GeneratorOperation Probe(Family family) => family switch
    {
        Family.Damage => new("T:D", OperationScope.SingleEnemyOnly, "造成10点伤害。", new Dictionary<string, int>()),
        Family.Block => new("N:B", OperationScope.NonTargeted, "获得10点格挡。", new Dictionary<string, int>()),
        Family.Draw => new("N:Draw", OperationScope.NonTargeted, "抽2张牌。", new Dictionary<string, int>()),
        Family.Energy => new("N:E", OperationScope.NonTargeted, "获得2点能量。", new Dictionary<string, int>()),
        Family.Gold => new("CL:GainGold", OperationScope.NonTargeted, "获得20金币。",
            new Dictionary<string, int>()),
        _ => new("AUDIT", OperationScope.NonTargeted, "获得10点数值。", new Dictionary<string, int>())
    };
}
