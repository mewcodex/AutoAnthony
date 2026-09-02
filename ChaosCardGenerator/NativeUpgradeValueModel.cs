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
        RepeatOrCardCount,
        HealingOrMaxHp,
        Gold,
        Other
    }

    internal static int SampleIncrease(GeneratorOperation operation, int value, Random random,
        Family? forcedFamily = null)
    {
        if (value <= 1) return 1;
        if (CardEffectRules.IsEnemyDamageAmplificationRule(operation))
        {
            // Cruelty upgrades 25% -> 50% in v111, while Tracking upgrades its cost rather than its percentage.
            // Preserve that distinction: the Vulnerable rule gets a large native-shaped increase, while the newly
            // variable Weak rule receives a smaller but still meaningful percentage-point increase.
            var amplificationCenter = OperationRuntimeSpecCompiler.GetOrCompile(operation).Variant
                == "vulnerable_enemy_damage_bonus"
                ? value * 0.9d
                : value * 0.3d;
            return SampleAroundCenter(amplificationCenter, value, random);
        }
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
        if (CardEffectRules.IsEnemyDamage(operation)
            || operation.Template is "N:RetaliateDamage" or "T_DAMAGE" or "N_RANDOM_DAMAGE")
            return operation.Template is "NCR:OstyDamage" or "NCR:OstyAllDamage"
                ? Family.OstyDamage : Family.Damage;
        if (operation.Template is "N:B" or "N_BLOCK" || text.Contains("格挡", StringComparison.Ordinal))
            return Family.Block;
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
        Family.RepeatOrCardCount => value <= 3 ? 1d : Math.Max(1d, value * 0.30d),
        Family.HealingOrMaxHp => Math.Max(1d, value * 0.35d),
        // Hand of Greed upgrades 20 -> 25, while Royalties upgrades 30 -> 40. Preserve both v111 anchors and
        // retain the shared slightly-above-native sampling tail instead of falling through to the +1 fallback.
        Family.Gold => value <= 20 ? Math.Max(5d, value * 0.25d) : Math.Max(5d, value / 3d),
        _ => 1d
    };

    internal static void Validate()
    {
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

        var weakRule = new GeneratorOperation("A:ruleWeakEnemiesTakeMoreAttackDamage",
            OperationScope.AbilityRule, "处于虚弱状态的敌人受到的攻击伤害增加50%。",
            new Dictionary<string, int>());
        var vulnerableRule = new GeneratorOperation("A:rule", OperationScope.AbilityRule,
            "拥有易伤的敌人受到的伤害增加25%。", new Dictionary<string, int>());
        if (Enumerable.Range(0, 200)
                .Select(seed => SampleIncrease(weakRule, 50, new Random(seed))).Any(delta => delta <= 1)
            || Enumerable.Range(0, 200)
                .Select(seed => SampleIncrease(vulnerableRule, 25, new Random(seed))).Any(delta => delta <= 1))
            throw new InvalidOperationException("敌人受伤增幅规则仍使用了+1升级兜底。");

        var royalties = new GeneratorOperation("A:ProxyAtomic_Royalties", OperationScope.AbilityRule,
            "在战斗结束时，获得30金币。", new Dictionary<string, int>());
        var royaltiesDeltas = Enumerable.Range(0, 300)
            .Select(seed => SampleIncrease(royalties, 30, new Random(seed))).ToArray();
        if (Classify(royalties) != Family.Gold
            || royaltiesDeltas.Any(delta => delta < 8 || delta > 13))
            throw new InvalidOperationException("金币效果仍使用普通小数值升级，或偏离王国资产的30→40锚点。");
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
