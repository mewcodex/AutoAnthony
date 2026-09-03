namespace ChaosCardGenerator;

[Flags]
public enum DerivativeSlotCapability
{
    Any = 0,
    Playable = 1,
    Attack = 2,
    Targeted = 4,
    Exhausting = 8,
    Status = 16
}

public enum DerivativeSlotUsage { Produce, Reference }

public sealed record DerivativeSlotDefinition(
    string Id,
    GeneratedCharacter Owner,
    string ChineseName,
    string EnglishSingular,
    string EnglishPlural,
    bool CanUpgrade,
    bool IsPlayable,
    bool IsAttack,
    bool CanTargetEnemy,
    bool HasExhaust,
    bool CanBeEnchanted,
    bool GainsBlock);

public enum DerivativeEnchantmentRequirement { Any, Attack, GainsBlock, Exhaust }

public sealed record DerivativeEnchantmentDefinition(
    string Id,
    string ChineseName,
    string EnglishName,
    int Amount,
    DerivativeEnchantmentRequirement Requirement);

/// <summary>
/// An intentionally conservative, offline whitelist.  Clone/Imbued never receive the hooks a short-lived
/// derivative needs, Goopy permanently mutates a deck card that does not exist, and Spiral only accepts Basic
/// Strike/Defend cards.  The remaining enchantments are filtered again against the concrete derivative shape.
/// </summary>
public static class DerivativeEnchantmentCatalog
{
    private static readonly IReadOnlyDictionary<string, DerivativeEnchantmentDefinition> Definitions =
        new[]
        {
            new DerivativeEnchantmentDefinition("adroit", "伶俐", "Adroit", 3, DerivativeEnchantmentRequirement.Any),
            new DerivativeEnchantmentDefinition("corrupted", "腐化", "Corrupted", 1, DerivativeEnchantmentRequirement.Attack),
            new DerivativeEnchantmentDefinition("glam", "华彩", "Glam", 1, DerivativeEnchantmentRequirement.Any),
            new DerivativeEnchantmentDefinition("inky", "墨影", "Inky", 1, DerivativeEnchantmentRequirement.Attack),
            new DerivativeEnchantmentDefinition("instinct", "本能", "Instinct", 1, DerivativeEnchantmentRequirement.Attack),
            new DerivativeEnchantmentDefinition("momentum", "动量", "Momentum", 5, DerivativeEnchantmentRequirement.Attack),
            new DerivativeEnchantmentDefinition("nimble", "灵巧", "Nimble", 2, DerivativeEnchantmentRequirement.GainsBlock),
            new DerivativeEnchantmentDefinition("perfect_fit", "完美契合", "Perfect Fit", 1, DerivativeEnchantmentRequirement.Any),
            new DerivativeEnchantmentDefinition("royally_approved", "王室认证", "Royally Approved", 1, DerivativeEnchantmentRequirement.Any),
            new DerivativeEnchantmentDefinition("sharp", "锋利", "Sharp", 2, DerivativeEnchantmentRequirement.Attack),
            new DerivativeEnchantmentDefinition("slither", "蛇行", "Slither", 1, DerivativeEnchantmentRequirement.Any),
            new DerivativeEnchantmentDefinition("slumbering_essence", "沉眠精华", "Slumbering Essence", 1, DerivativeEnchantmentRequirement.Any),
            new DerivativeEnchantmentDefinition("souls_power", "灵魂之力", "Soul's Power", 1, DerivativeEnchantmentRequirement.Exhaust),
            new DerivativeEnchantmentDefinition("sown", "播种", "Sown", 1, DerivativeEnchantmentRequirement.Any),
            new DerivativeEnchantmentDefinition("steady", "稳定", "Steady", 1, DerivativeEnchantmentRequirement.Any),
            new DerivativeEnchantmentDefinition("swift", "迅速", "Swift", 2, DerivativeEnchantmentRequirement.Any),
            new DerivativeEnchantmentDefinition("tezcatara_ember", "特兹卡塔拉的余烬", "Tezcatara's Ember", 1, DerivativeEnchantmentRequirement.Attack),
            new DerivativeEnchantmentDefinition("vigorous", "活力", "Vigorous", 8, DerivativeEnchantmentRequirement.Attack)
        }.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

    public static IEnumerable<DerivativeEnchantmentDefinition> All => Definitions.Values;
    public static bool IsKnownId(string id) => Definitions.ContainsKey(id);
    public static DerivativeEnchantmentDefinition? Resolve(string? id) =>
        id is not null && Definitions.TryGetValue(id, out var result) ? result : null;

    public static bool CanUse(DerivativeSlotDefinition derivative, DerivativeEnchantmentDefinition enchantment)
    {
        if (!derivative.CanBeEnchanted) return false;
        // Steady would make Sovereign Blade persist in hand even though Forge is designed around replacing and
        // rebuilding that token. Slither only has a meaningful cost-randomization payoff on a derivative whose
        // native Energy cost is above zero; putting it on Shiv/Fuel/Soul/etc. is a mostly cosmetic enchantment.
        if (enchantment.Id == "steady" && derivative.Id == "sword") return false;
        if (enchantment.Id == "slither" && CanonicalEnergyCost(derivative) <= 0) return false;
        // The base-game Inky hook applies Weak to CardPlay.Target unless the card targets all enemies.
        // RandomEnemy cards (currently Sweeping Gaze) do not provide that explicit target, so although
        // ModelDb's broad Attack check accepts the pair, the enchantment cannot resolve its gameplay effect.
        if (enchantment.Id == "inky" && !derivative.CanTargetEnemy) return false;
        return enchantment.Requirement switch
        {
            DerivativeEnchantmentRequirement.Attack => derivative.IsAttack,
            DerivativeEnchantmentRequirement.GainsBlock => derivative.GainsBlock,
            DerivativeEnchantmentRequirement.Exhaust => derivative.HasExhaust,
            _ => true
        };
    }

    public static IReadOnlyList<DerivativeEnchantmentDefinition> Candidates(DerivativeSlotDefinition derivative) =>
        Definitions.Values.Where(enchantment => CanUse(derivative, enchantment)).ToArray();

    private static int CanonicalEnergyCost(DerivativeSlotDefinition derivative) => derivative.Id switch
    {
        "rock" => 1,
        "sword" => 2,
        // Debris also costs one, but it is deliberately not enchantable. Keeping the complete mapping here makes
        // the Slither rule explicit if that policy changes later.
        "debris" => 1,
        _ => 0
    };

    public static IReadOnlyList<int> AllowedAmounts(DerivativeEnchantmentDefinition enchantment) =>
        enchantment.Id switch
        {
            "adroit" => [2, 3, 4],
            "momentum" => [4, 5, 6, 7],
            "nimble" or "sharp" or "swift" => [1, 2, 3],
            "vigorous" => [6, 7, 8, 9, 10],
            _ => [enchantment.Amount]
        };

    public static bool IsAllowedAmount(DerivativeEnchantmentDefinition enchantment, int amount) =>
        AllowedAmounts(enchantment).Contains(amount);

    public static int ResolveAmount(GeneratorOperation operation,
        DerivativeEnchantmentDefinition enchantment) =>
        operation.DerivativeEnchantmentAmount ?? enchantment.Amount;

    public static int RollAmount(Random random, DerivativeEnchantmentDefinition enchantment)
    {
        var roll = random.NextDouble();
        return enchantment.Id switch
        {
            "adroit" => roll < 0.30 ? 2 : roll < 0.80 ? 3 : 4,
            "momentum" => roll < 0.30 ? 4 : roll < 0.75 ? 5 : roll < 0.95 ? 6 : 7,
            "nimble" or "sharp" => roll < 0.35 ? 1 : roll < 0.85 ? 2 : 3,
            // Swift should overwhelmingly remain at its native amount or one below it. Amount 3 is a jackpot.
            "swift" => roll < 0.45 ? 1 : roll < 0.98 ? 2 : 3,
            "vigorous" => roll < 0.10 ? 6 : roll < 0.30 ? 7 : roll < 0.70 ? 8 : roll < 0.90 ? 9 : 10,
            _ => enchantment.Amount
        };
    }

    public static DerivativeEnchantmentDefinition? Roll(Random random, string template,
        DerivativeSlotDefinition derivative)
    {
        // Blade of Ink's native pairing remains visibly more common. All other producer/enchantment pairs are
        // deliberately rare, including Inky on a Shiv created by another operation.
        if (template == "N:CreateInkShiv" && derivative.Id == "shiv" && random.NextDouble() < 0.45)
            return Definitions["inky"];
        if (random.NextDouble() >= 0.04) return null;
        var candidates = Candidates(derivative);
        return candidates.Count == 0 ? null : candidates[random.Next(candidates.Count)];
    }

    public static string ChineseCardName(DerivativeSlotDefinition derivative,
        DerivativeEnchantmentDefinition? enchantment) =>
        (enchantment?.ChineseName ?? string.Empty) + derivative.ChineseName;

    public static string EnglishSingular(DerivativeSlotDefinition derivative,
        DerivativeEnchantmentDefinition? enchantment) =>
        enchantment is null ? derivative.EnglishSingular : $"{enchantment.EnglishName} {derivative.EnglishSingular}";

    public static string EnglishPlural(DerivativeSlotDefinition derivative,
        DerivativeEnchantmentDefinition? enchantment) =>
        enchantment is null ? derivative.EnglishPlural : $"{enchantment.EnglishName} {derivative.EnglishPlural}";
}

/// <summary>
/// Cards created, transformed, counted, or replayed by an operation are parameters of that operation rather
/// than part of its identity.  This catalog is deliberately game-assembly-free so generated snapshots only
/// need to persist the compact derivative id.
/// </summary>
public static class DerivativeSlotCatalog
{
    public const double FillHandDerivativeChance = 0.05d;
    public const double StatusCurseEasterEggChance = 0.01d;
    private const int NormalNecrobinderSoulWeight = 75;
    private const int NormalNecrobinderGazeWeight = 25;

    private sealed record SlotSource(string DerivativeId, DerivativeSlotCapability Capability,
        bool CanUpgradeOutput, DerivativeSlotUsage Usage, string? EnchantmentId = null);

    private static readonly IReadOnlyDictionary<string, DerivativeSlotDefinition> Definitions =
        new[]
        {
            new DerivativeSlotDefinition("rock", GeneratedCharacter.Ironclad, "巨石", "Boulder", "Boulders", true, true, true, true, false, true, false),
            new DerivativeSlotDefinition("shiv", GeneratedCharacter.Silent, "小刀", "Shiv", "Shivs", true, true, true, true, true, true, false),
            new DerivativeSlotDefinition("fuel", GeneratedCharacter.Defect, "燃料", "Fuel", "Fuel", true, true, false, false, true, true, false),
            new DerivativeSlotDefinition("dazed", GeneratedCharacter.Defect, "晕眩", "Dazed", "Dazed", false, false, false, false, false, false, false),
            new DerivativeSlotDefinition("wound", GeneratedCharacter.Defect, "伤口", "Wound", "Wounds", false, false, false, false, false, false, false),
            new DerivativeSlotDefinition("slimed", GeneratedCharacter.Defect, "黏液", "Slimed", "Slimed", false, true, false, false, true, false, false),
            new DerivativeSlotDefinition("burn", GeneratedCharacter.Defect, "灼伤", "Burn", "Burns", false, false, false, false, false, false, false),
            new DerivativeSlotDefinition("void", GeneratedCharacter.Defect, "虚空", "Void", "Voids", false, false, false, false, false, false, false),
            new DerivativeSlotDefinition("soul", GeneratedCharacter.Necrobinder, "灵魂", "Soul", "Souls", true, true, false, false, true, true, false),
            new DerivativeSlotDefinition("gaze", GeneratedCharacter.Necrobinder, "扫荡凝视", "Sweeping Gaze", "Sweeping Gazes", true, true, true, false, true, true, false),
            new DerivativeSlotDefinition("debris", GeneratedCharacter.Regent, "碎屑", "Debris", "Debris", false, true, false, false, true, false, false),
            new DerivativeSlotDefinition("sword", GeneratedCharacter.Regent, "君王之剑", "Sovereign Blade", "Sovereign Blades", true, true, true, true, false, true, false),
            new DerivativeSlotDefinition("minion_strike", GeneratedCharacter.Regent, "仆从打击", "Minion Strike", "Minion Strikes", true, true, true, true, true, true, false),
            new DerivativeSlotDefinition("minion_dive", GeneratedCharacter.Regent, "仆从俯冲", "Minion Dive Bomb", "Minion Dive Bombs", true, true, true, true, true, true, false),
            new DerivativeSlotDefinition("minion_sacrifice", GeneratedCharacter.Regent, "仆从捐躯", "Minion Sacrifice", "Minion Sacrifices", true, true, false, false, true, true, true),
            new DerivativeSlotDefinition("curse_clumsy", GeneratedCharacter.Colorless, "笨拙", "Clumsy", "Clumsy", false, false, false, false, false, false, false),
            new DerivativeSlotDefinition("curse_debt", GeneratedCharacter.Colorless, "债务", "Debt", "Debt", false, false, false, false, false, false, false),
            new DerivativeSlotDefinition("curse_decay", GeneratedCharacter.Colorless, "腐朽", "Decay", "Decay", false, false, false, false, false, false, false),
            new DerivativeSlotDefinition("curse_doubt", GeneratedCharacter.Colorless, "疑虑", "Doubt", "Doubt", false, false, false, false, false, false, false),
            new DerivativeSlotDefinition("curse_guilty", GeneratedCharacter.Colorless, "愧疚", "Guilty", "Guilty", false, false, false, false, false, false, false),
            new DerivativeSlotDefinition("curse_injury", GeneratedCharacter.Colorless, "受伤", "Injury", "Injury", false, false, false, false, false, false, false),
            new DerivativeSlotDefinition("curse_normality", GeneratedCharacter.Colorless, "凡庸", "Normality", "Normality", false, false, false, false, false, false, false),
            new DerivativeSlotDefinition("curse_regret", GeneratedCharacter.Colorless, "悔恨", "Regret", "Regret", false, false, false, false, false, false, false),
            new DerivativeSlotDefinition("curse_shame", GeneratedCharacter.Colorless, "羞耻", "Shame", "Shame", false, false, false, false, false, false, false),
            new DerivativeSlotDefinition("curse_writhe", GeneratedCharacter.Colorless, "苦恼", "Writhe", "Writhe", false, false, false, false, false, false, false)
        }.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

    private static readonly DerivativeSlotDefinition[] CurseEasterEggs = Definitions.Values
        .Where(IsCurse).ToArray();

    // Relative values compare interchangeable outputs of the same producer operation only. They are deliberately
    // not part of the whole-card strength budget: replacing three Shivs with another derivative may change that
    // operation's count, but must not make unrelated damage, Block, cost, or component selection stronger.
    private static readonly IReadOnlyDictionary<string, decimal> RelativeValues =
        new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["shiv"] = 1m,
            ["rock"] = 3.5m,
            ["fuel"] = 1.5m,
            ["soul"] = 1.5m,
            ["gaze"] = 2m,
            ["minion_strike"] = 2m,
            ["minion_dive"] = 2m,
            ["minion_sacrifice"] = 1m,
            ["sword"] = 4m
        };

    // Highly derivative-specific rules (Accuracy/Fan of Knives/Sword Sage/etc.) intentionally stay atomic.
    // Only operations whose verb remains valid after substituting the card are slotted here.
    private static readonly IReadOnlyDictionary<string, SlotSource> Sources =
        new Dictionary<string, SlotSource>(StringComparer.Ordinal)
        {
            ["I:Transform"] = new("rock", DerivativeSlotCapability.Any, true, DerivativeSlotUsage.Produce),
            ["N:CreateShiv"] = new("shiv", DerivativeSlotCapability.Any, true, DerivativeSlotUsage.Produce),
            ["N:CreateInkShiv"] = new("shiv", DerivativeSlotCapability.Any, true, DerivativeSlotUsage.Produce, "inky"),
            ["I:PlayExhaustedShivsAtTarget"] = new("shiv",
                DerivativeSlotCapability.Playable | DerivativeSlotCapability.Attack | DerivativeSlotCapability.Targeted
                | DerivativeSlotCapability.Exhausting, false, DerivativeSlotUsage.Reference),
            ["D:CreateDazedInDiscard"] = new("dazed", DerivativeSlotCapability.Status, false, DerivativeSlotUsage.Produce),
            ["D:CreateTwoWoundsInDiscard"] = new("wound", DerivativeSlotCapability.Status, false, DerivativeSlotUsage.Produce),
            ["D:CreateSlimeInDiscard"] = new("slimed", DerivativeSlotCapability.Status, false, DerivativeSlotUsage.Produce),
            ["D:CreateBurnInDiscard"] = new("burn", DerivativeSlotCapability.Status, false, DerivativeSlotUsage.Produce),
            ["D:CreateVoidInDiscard"] = new("void", DerivativeSlotCapability.Status, false, DerivativeSlotUsage.Produce),
            ["D:TransformStatusesToFuel"] = new("fuel", DerivativeSlotCapability.Any, true, DerivativeSlotUsage.Produce),
            ["A:whenSoulPlayed"] = new("soul", DerivativeSlotCapability.Playable, false, DerivativeSlotUsage.Reference),
            ["I:ProxyAtomic_Seance"] = new("soul", DerivativeSlotCapability.Any, true, DerivativeSlotUsage.Produce),
            ["NCR:CreateSoulInDraw"] = new("soul", DerivativeSlotCapability.Any, true, DerivativeSlotUsage.Produce),
            ["NCR:CreateSoulInDrawX"] = new("soul", DerivativeSlotCapability.Any, true, DerivativeSlotUsage.Produce),
            ["NCR:CreateSoulInHand"] = new("soul", DerivativeSlotCapability.Any, true, DerivativeSlotUsage.Produce),
            ["NCR:CreateSoulInDiscard"] = new("soul", DerivativeSlotCapability.Any, true, DerivativeSlotUsage.Produce),
            ["NCR:ForEachExhaustedSoul"] = new("soul", DerivativeSlotCapability.Exhausting, false, DerivativeSlotUsage.Reference),
            ["NCR:AddSweepingGazeToHand"] = new("gaze", DerivativeSlotCapability.Any, true, DerivativeSlotUsage.Produce),
            ["R:AddDebrisToHand"] = new("debris", DerivativeSlotCapability.Status, false, DerivativeSlotUsage.Produce),
            ["R:FillHandWithDebris"] = new("debris", DerivativeSlotCapability.Status, false, DerivativeSlotUsage.Produce),
            ["R:PutKingsSwordInHand"] = new("sword", DerivativeSlotCapability.Any, false, DerivativeSlotUsage.Reference),
            ["I:ProxyAtomic_Begone"] = new("minion_strike", DerivativeSlotCapability.Any, true, DerivativeSlotUsage.Produce),
            ["I:ProxyAtomic_Charge"] = new("minion_dive", DerivativeSlotCapability.Any, true, DerivativeSlotUsage.Produce),
            ["I:ProxyAtomic_Guards"] = new("minion_sacrifice", DerivativeSlotCapability.Any, true, DerivativeSlotUsage.Produce)
        };

    public static IEnumerable<DerivativeSlotDefinition> All => Definitions.Values;
    public static IEnumerable<string> SlotTemplates => Sources.Keys;

    public static bool IsSlotOperation(string template) => Sources.ContainsKey(template);

    public static bool IsProducer(string template) => Sources.TryGetValue(template, out var source)
        && source.Usage == DerivativeSlotUsage.Produce;

    public static decimal? RelativeValue(string derivativeId) =>
        RelativeValues.TryGetValue(NormalizeProducedId(derivativeId), out var value) ? value : null;

    public static string AdjustFixedProducerCount(string template, string chineseText,
        DerivativeSlotDefinition selected, bool ancientFuelActive = false)
    {
        if (!IsProducer(template) || Source(template) is not { } source
            || NormalizeProducedId(source.Id) == NormalizeProducedId(selected.Id)
            || EffectiveRelativeValue(source.Id, ancientFuelActive) is not { } sourceValue
            || EffectiveRelativeValue(selected.Id, ancientFuelActive) is not { } selectedValue)
            return chineseText;

        // X quantities and non-numeric producers (all cards, fill the Hand, any number of cards, etc.) do not
        // have a safe fixed count to rewrite. Explicit fixed-count creation and Transform operations do.
        var match = System.Text.RegularExpressions.Regex.Match(chineseText,
            @"将(?<count>\d+)张|中的(?<count>\d+)张牌|中的(?<count>一)张牌");
        if (!match.Success) return chineseText;
        var countGroup = match.Groups["count"];
        var sourceCount = countGroup.Value == "一"
            ? 1
            : int.TryParse(countGroup.Value, out var parsed) ? parsed : 0;
        if (sourceCount <= 0) return chineseText;

        var adjusted = AdjustFixedProducerCount(template, sourceCount, selected, ancientFuelActive);
        if (adjusted == sourceCount) return chineseText;
        return chineseText[..countGroup.Index] + adjusted
            + chineseText[(countGroup.Index + countGroup.Length)..];
    }

    public static int AdjustFixedProducerCount(string template, int sourceCount,
        DerivativeSlotDefinition selected, bool ancientFuelActive = false)
    {
        if (sourceCount <= 0 || !IsProducer(template) || Source(template) is not { } source
            || NormalizeProducedId(source.Id) == NormalizeProducedId(selected.Id)
            || EffectiveRelativeValue(source.Id, ancientFuelActive) is not { } sourceValue
            || EffectiveRelativeValue(selected.Id, ancientFuelActive) is not { } selectedValue)
            return sourceCount;

        // Blade of Ink's native two-card payload is budgeted as three ordinary Shiv units. Enchantments themselves
        // remain outside this table; the 1.5 factor belongs only to this source operation's native quantity budget.
        if (template == "N:CreateInkShiv") sourceValue = 1.5m;
        var adjusted = Math.Max(1, decimal.ToInt32(decimal.Ceiling(sourceCount * sourceValue / selectedValue)));
        // Paid generated cards become cumbersome much faster than 0-cost derivatives. Numeric producers never
        // create more than two Boulders or Sovereign Blades; non-numeric transforms remain unchanged by design.
        return selected.Id is "rock" or "sword" ? Math.Min(2, adjusted) : adjusted;
    }

    public static int? ImplicitFixedProducerCount(string template) => template switch
    {
        "I:ProxyAtomic_Begone" or "I:ProxyAtomic_Seance" => 1,
        _ => null
    };

    private static decimal? EffectiveRelativeValue(string derivativeId, bool ancientFuelActive)
    {
        var normalized = NormalizeProducedId(derivativeId);
        if (normalized == "fuel" && ancientFuelActive) return 2.5m;
        return RelativeValue(normalized);
    }

    public static bool IsReference(string template) => Sources.TryGetValue(template, out var source)
        && source.Usage == DerivativeSlotUsage.Reference;

    public static bool IsExhaustPileReference(string template) => template is
        "I:PlayExhaustedShivsAtTarget" or "NCR:ForEachExhaustedSoul";

    public static bool IsStatus(DerivativeSlotDefinition definition) => definition.Id is
        "dazed" or "wound" or "slimed" or "burn" or "void" or "debris";

    public static bool IsCurse(DerivativeSlotDefinition definition) =>
        definition.Id.StartsWith("curse_", StringComparison.Ordinal);

    public static bool IsStatusProducer(string template) => Sources.TryGetValue(template, out var source)
        && source.Usage == DerivativeSlotUsage.Produce
        && source.Capability.HasFlag(DerivativeSlotCapability.Status);

    // Source capability controls which cards may be rolled into a slot. Once a concrete derivative has been
    // assigned, gameplay/budget classification must use that output instead. Crash Landing's rare replacement can
    // legally put a Shiv-like derivative in its former Status slot; classifying it from the source template made
    // the produced Shiv count as a Status/downside even though the actual card is an Attack.
    public static bool ProducesStatus(ComponentAtom atom) =>
        Resolve(null, atom.Template) is { } derivative && IsStatus(derivative);

    public static bool ProducesStatus(GeneratorOperation operation) =>
        Resolve(operation.DerivativeId, operation.Template) is { } derivative && IsStatus(derivative);

    // "ink" is accepted only to migrate v0.1.65 snapshots; new generation always stores shiv + inky.
    public static bool IsKnownId(string derivativeId) => Definitions.ContainsKey(derivativeId) || derivativeId == "ink";

    public static DerivativeSlotDefinition? Resolve(string? derivativeId, string template)
    {
        if (derivativeId == "ink") return Definitions["shiv"];
        if (derivativeId is not null && Definitions.TryGetValue(derivativeId, out var selected)) return selected;
        return Sources.TryGetValue(template, out var source) ? Definitions[source.DerivativeId] : null;
    }

    public static DerivativeSlotDefinition? Source(string template) =>
        Sources.TryGetValue(template, out var source) ? Definitions[source.DerivativeId] : null;

    public static DerivativeEnchantmentDefinition? ResolveEnchantment(string? derivativeId,
        string? enchantmentId, string template)
    {
        if (enchantmentId is not null) return DerivativeEnchantmentCatalog.Resolve(enchantmentId);
        if (derivativeId == "ink") return DerivativeEnchantmentCatalog.Resolve("inky");
        // A null derivative id denotes the original, unslotted operation text.
        return derivativeId is null && Sources.TryGetValue(template, out var source)
            ? DerivativeEnchantmentCatalog.Resolve(source.EnchantmentId)
            : null;
    }

    public static bool SupportsUpgrade(string template, string? derivativeId) =>
        Sources.TryGetValue(template, out var source)
        && source.CanUpgradeOutput
        && Resolve(derivativeId, template)?.CanUpgrade == true;

    public static string NormalizeProducedId(string derivativeId) => derivativeId == "ink" ? "shiv" : derivativeId;

    public static bool CanUse(string template, DerivativeSlotDefinition definition)
    {
        if (!Sources.TryGetValue(template, out var source)) return false;
        // Curses are never ordinary derivative candidates. They are admitted only by CanUseAssigned after the
        // one-time status-producer Easter-egg roll has explicitly selected one.
        if (IsCurse(definition)) return false;
        // Status cards and ordinary derivatives share the compact DerivativeId field, but never share an ordinary
        // slot. Crash Landing's explicit 5% whole-card replacement is validated separately by CanUseAssigned.
        if (!source.Capability.HasFlag(DerivativeSlotCapability.Status) && IsStatus(definition)) return false;
        if (source.Capability.HasFlag(DerivativeSlotCapability.Playable) && !definition.IsPlayable) return false;
        if (source.Capability.HasFlag(DerivativeSlotCapability.Attack) && !definition.IsAttack) return false;
        if (source.Capability.HasFlag(DerivativeSlotCapability.Targeted) && !definition.CanTargetEnemy) return false;
        if (source.Capability.HasFlag(DerivativeSlotCapability.Exhausting) && !definition.HasExhaust) return false;
        if (source.Capability.HasFlag(DerivativeSlotCapability.Status) && !IsStatus(definition)) return false;
        return true;
    }

    /// <summary>
    /// Snapshot validation accepts the ordinary slot rules plus Crash Landing's rare whole-card replacement.
    /// The replacement is deliberately encoded by the selected derivative id, so combat never rerolls per card.
    /// </summary>
    public static bool CanUseAssigned(string template, DerivativeSlotDefinition definition) =>
        CanUse(template, definition)
        || IsStatusProducer(template) && IsCurse(definition)
        || template == "R:FillHandWithDebris" && !IsStatus(definition) && !IsCurse(definition);

    public static IReadOnlyList<DerivativeSlotDefinition> Candidates(
        GeneratedCharacter currentCharacter, bool ultimateChaos, string template)
    {
        if (!Sources.TryGetValue(template, out var source)) return [];
        var values = Definitions.Values.Where(definition => ultimateChaos || definition.Owner == currentCharacter);
        values = values.Where(definition => CanUse(template, definition));
        var result = values.ToArray();
        // This is mainly relevant to the normal Colorless pool, which has no native derivative. Old snapshots and
        // unusual cross-pool test cards still retain the source derivative instead of becoming unparsable.
        return result.Length > 0 ? result : [Definitions[source.DerivativeId]];
    }

    public static DerivativeSlotDefinition Roll(Random random, GeneratedCharacter currentCharacter,
        bool ultimateChaos, string template)
    {
        if (IsStatusProducer(template) && random.NextDouble() < StatusCurseEasterEggChance)
            return CurseEasterEggs[random.Next(CurseEasterEggs.Length)];

        if (template == "R:FillHandWithDebris")
        {
            var replacements = Definitions.Values
                .Where(definition => (ultimateChaos || definition.Owner == currentCharacter)
                    && !IsStatus(definition) && !IsCurse(definition))
                .ToArray();
            if (replacements.Length > 0 && random.NextDouble() < FillHandDerivativeChance)
                return replacements[random.Next(replacements.Length)];
        }

        var candidates = Candidates(currentCharacter, ultimateChaos, template);
        var totalWeight = candidates.Sum(candidate => SelectionWeight(candidate, currentCharacter, ultimateChaos));
        var roll = random.Next(totalWeight);
        foreach (var candidate in candidates)
        {
            var weight = SelectionWeight(candidate, currentCharacter, ultimateChaos);
            if (roll < weight) return candidate;
            roll -= weight;
        }
        throw new InvalidOperationException("衍生物槽位权重采样失败。");
    }

    /// <summary>
    /// The normal Necrobinder pool should still resemble its native derivative mix: Souls are the common payload,
    /// while Sweeping Gaze is the rarer alternative. Ultimate Chaos keeps the shared uniform slot distribution.
    /// Capability filters run first, so an attack-only slot that can only accept Sweeping Gaze remains valid.
    /// </summary>
    internal static int SelectionWeight(DerivativeSlotDefinition candidate,
        GeneratedCharacter currentCharacter, bool ultimateChaos)
    {
        if (ultimateChaos || currentCharacter != GeneratedCharacter.Necrobinder) return 100;
        return candidate.Id switch
        {
            "soul" => NormalNecrobinderSoulWeight,
            "gaze" => NormalNecrobinderGazeWeight,
            _ => 100
        };
    }

    public static string ReplaceChinese(string text, string template, DerivativeSlotDefinition selected,
        DerivativeEnchantmentDefinition? enchantment = null)
    {
        var source = Source(template);
        if (source is null) return text;
        var sourceEnchantment = ResolveEnchantment(null, null, template);
        return text.Replace(DerivativeEnchantmentCatalog.ChineseCardName(source, sourceEnchantment),
            DerivativeEnchantmentCatalog.ChineseCardName(selected, enchantment), StringComparison.Ordinal);
    }

    public static string ReplaceEnglish(string text, string template, DerivativeSlotDefinition selected,
        DerivativeEnchantmentDefinition? enchantment = null)
    {
        var source = Source(template);
        if (source is null) return text;
        var sourceEnchantment = ResolveEnchantment(null, null, template);
        var sourceSingular = DerivativeEnchantmentCatalog.EnglishSingular(source, sourceEnchantment);
        var sourcePlural = DerivativeEnchantmentCatalog.EnglishPlural(source, sourceEnchantment);
        var selectedSingular = DerivativeEnchantmentCatalog.EnglishSingular(selected, enchantment);
        var selectedPlural = DerivativeEnchantmentCatalog.EnglishPlural(selected, enchantment);
        if (sourcePlural == sourceSingular)
            return ReplaceEnglishLiteral(text, sourceSingular,
                EnglishTextUsesPlural(text, template) ? selectedPlural : selectedSingular);
        // Replace the plural first: "Soul" is a prefix of "Souls".
        var marker = "\uE059DERIVATIVE_ENCHANTED\uE05A";
        return text.Replace(sourcePlural, marker, StringComparison.OrdinalIgnoreCase)
            .Replace(sourceSingular, selectedSingular, StringComparison.OrdinalIgnoreCase)
            .Replace(marker, selectedPlural, StringComparison.Ordinal);
    }

    public static string ChineseCardName(GeneratorOperation operation)
    {
        var derivative = Resolve(operation.DerivativeId, operation.Template);
        var enchantment = ResolveEnchantment(operation.DerivativeId, operation.DerivativeEnchantmentId,
            operation.Template);
        return derivative is null ? string.Empty
            : DerivativeEnchantmentCatalog.ChineseCardName(derivative, enchantment);
    }

    public static OperationLocalizedText BindLocalizedText(GeneratorOperation operation,
        OperationLocalizedText localized, string renderedEnglish)
    {
        var derivative = Resolve(operation.DerivativeId, operation.Template)
            ?? throw new InvalidOperationException($"Cannot bind derivative text for {operation.Template}.");
        var enchantment = ResolveEnchantment(operation.DerivativeId, operation.DerivativeEnchantmentId,
            operation.Template);
        var plural = EnglishTextUsesPlural(renderedEnglish, operation.Template);
        var chinese = DerivativeEnchantmentCatalog.ChineseCardName(derivative, enchantment);
        var english = plural
            ? DerivativeEnchantmentCatalog.EnglishPlural(derivative, enchantment)
            : DerivativeEnchantmentCatalog.EnglishSingular(derivative, enchantment);
        return (localized.TextSlots ?? []).Any(slot => slot.Id == "derivative")
            ? localized.WithTextSlotValue("derivative", chinese, english)
            : localized.BindTextSlot("derivative", chinese, english);
    }

    /// <summary>
    /// Rehydrates a registered component-localization template. Unlike <see cref="BindLocalizedText"/>, whose
    /// unbound input is expected to already mention the operation's current derivative, registry templates still
    /// contain the component's authored source derivative and must bind that source before applying the saved slot.
    /// </summary>
    public static OperationLocalizedText BindSourceLocalizedText(GeneratorOperation operation,
        OperationLocalizedText localized, string renderedSourceEnglish, OperationRuntimeSpec spec)
    {
        var selected = Resolve(operation.DerivativeId, operation.Template)
            ?? throw new InvalidOperationException($"Cannot bind derivative text for {operation.Template}.");
        var selectedEnchantment = ResolveEnchantment(operation.DerivativeId,
            operation.DerivativeEnchantmentId, operation.Template);
        var source = Source(operation.Template)
            ?? throw new InvalidOperationException($"Derivative slot {operation.Template} has no source definition.");
        var sourceEnchantment = ResolveEnchantment(null, null, operation.Template);
        var sourceChinese = DerivativeEnchantmentCatalog.ChineseCardName(source, sourceEnchantment);
        var sourceEnglishSingular = SourceEnglishName(operation.Template, plural: false);
        var sourceEnglishPlural = SourceEnglishName(operation.Template, plural: true);
        var sourceEnglish = sourceEnglishPlural != sourceEnglishSingular
                            && localized.EnglishTemplate?.Contains(sourceEnglishPlural,
                                StringComparison.OrdinalIgnoreCase) == true
            ? sourceEnglishPlural
            : sourceEnglishSingular;
        var plural = EnglishTextUsesPlural(renderedSourceEnglish, operation.Template);
        var selectedChinese = DerivativeEnchantmentCatalog.ChineseCardName(selected, selectedEnchantment);
        var selectedEnglish = plural
            ? DerivativeEnchantmentCatalog.EnglishPlural(selected, selectedEnchantment)
            : DerivativeEnchantmentCatalog.EnglishSingular(selected, selectedEnchantment);
        var result = (localized.TextSlots ?? []).Any(slot => slot.Id == "derivative")
            ? localized.WithTextSlotValue("derivative", selectedChinese, selectedEnglish)
            : localized.BindTextSlot("derivative", sourceChinese, sourceEnglish)
                .WithTextSlotValue("derivative", selectedChinese, selectedEnglish);
        result.Validate(spec);
        return result;
    }

    public static OperationLocalizedText SetDerivativeText(OperationLocalizedText localized,
        DerivativeSlotDefinition derivative, DerivativeEnchantmentDefinition? enchantment, bool plural,
        bool upgraded = false)
    {
        var suffix = upgraded ? "+" : string.Empty;
        var chinese = DerivativeEnchantmentCatalog.ChineseCardName(derivative, enchantment) + suffix;
        var english = (plural
            ? DerivativeEnchantmentCatalog.EnglishPlural(derivative, enchantment)
            : DerivativeEnchantmentCatalog.EnglishSingular(derivative, enchantment)) + suffix;
        return localized.WithTextSlotValue("derivative", chinese, english);
    }

    private static string ReplaceEnglishLiteral(string text, string from, string to) =>
        text.Replace(from, to, StringComparison.OrdinalIgnoreCase);

    public static string MarkEnglishUpgrade(string text, DerivativeSlotDefinition derivative)
    {
        var marker = "\uE055DERIVATIVE\uE056";
        return text.Replace(derivative.EnglishPlural, marker, StringComparison.OrdinalIgnoreCase)
            .Replace(derivative.EnglishSingular, derivative.EnglishSingular + "+", StringComparison.OrdinalIgnoreCase)
            .Replace(marker, derivative.EnglishPlural + "+", StringComparison.Ordinal);
    }

    public static string ReplaceEnglishName(string text, DerivativeSlotDefinition from, DerivativeSlotDefinition to)
    {
        var marker = "\uE057DERIVATIVE\uE058";
        return text.Replace(from.EnglishPlural, marker, StringComparison.OrdinalIgnoreCase)
            .Replace(from.EnglishSingular, to.EnglishSingular, StringComparison.OrdinalIgnoreCase)
            .Replace(marker, to.EnglishPlural, StringComparison.Ordinal);
    }

    public static string ReplaceEnglishName(string text, DerivativeSlotDefinition from,
        DerivativeSlotDefinition to, bool plural)
    {
        var replacement = plural ? to.EnglishPlural : to.EnglishSingular;
        return text.Replace(from.EnglishPlural, replacement, StringComparison.OrdinalIgnoreCase)
            .Replace(from.EnglishSingular, replacement, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ReferenceUsesPlural(string template) => template is
        "I:PlayExhaustedShivsAtTarget" or "NCR:ForEachExhaustedSoul";

    public static string SourceEnglishName(string template, bool plural)
    {
        if (template == "N:CreateInkShiv") return plural ? "Ink Shivs" : "Ink Shiv";
        var source = Source(template)
            ?? throw new InvalidOperationException($"Derivative slot {template} has no source definition.");
        var enchantment = ResolveEnchantment(null, null, template);
        return plural
            ? DerivativeEnchantmentCatalog.EnglishPlural(source, enchantment)
            : DerivativeEnchantmentCatalog.EnglishSingular(source, enchantment);
    }

    public static bool EnglishTextUsesPlural(string text, string template)
    {
        if (ReferenceUsesPlural(template) || template == "R:FillHandWithDebris") return true;
        var amount = System.Text.RegularExpressions.Regex.Match(text, @"\b(?:Add|Transform)\s+(\d+|X)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return amount.Success && (amount.Groups[1].Value == "X"
            || int.TryParse(amount.Groups[1].Value, out var value) && value != 1);
    }
}
