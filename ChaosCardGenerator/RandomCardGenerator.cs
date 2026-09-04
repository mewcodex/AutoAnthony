using System.Text.RegularExpressions;

namespace ChaosCardGenerator;

/// <summary>
/// Standalone generator for unupgraded single-player random card pools. It emits structured RuntimeSpecs and their
/// bilingual text projections without depending on sts2.dll or Godot; the game layer maps GeneratorOperation values
/// to CardModel, DynamicVar, OnPlay, and Power implementations.
/// </summary>
public sealed class RandomCardGenerator
{
    private readonly GeneratedCharacter _character;
    private readonly bool _unlockComponentRoles;
    private readonly HashSet<string> _usedChineseNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedEnglishNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _usedEffectSignatures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedPoolUniqueComponents = new(StringComparer.Ordinal);
    private readonly ComponentAssemblyGenerator _normalAssembler;
    private readonly ComponentAssemblyGenerator _nonSpecialXAssembler;
    private readonly ComponentAssemblyGenerator _forcedSpecialXAssembler;
    private readonly ComponentAssemblyGenerator _referenceFreeNonSpecialXAssembler;
    private readonly ComponentAssemblyGenerator _referenceFreeForcedSpecialXAssembler;

    public RandomCardGenerator(int? seed = null) : this(GeneratedCharacter.Ironclad, seed) { }

    public RandomCardGenerator(GeneratedCharacter character, int? seed = null, bool unlockComponentRoles = false,
        bool ancientFuelActive = false, bool suppressDerivativeReferences = false, bool balancedValues = true,
        bool randomizeNumericValues = false)
        : this(new ComponentProfileRequest(character, unlockComponentRoles), seed, ancientFuelActive,
            suppressDerivativeReferences, balancedValues, randomizeNumericValues) { }

    /// <summary>
    /// Creates a generator for a registered external profile. The request's Character remains the balance and
    /// legality archetype while ProfileId selects the external catalog package.
    /// </summary>
    public RandomCardGenerator(ComponentProfileRequest request, int? seed = null,
        bool ancientFuelActive = false, bool suppressDerivativeReferences = false, bool balancedValues = true,
        bool randomizeNumericValues = false)
    {
        _character = request.Character;
        _unlockComponentRoles = request.UnlockComponentRoles;
        var random = seed is { } value ? new Random(value) : Random.Shared;
        var profile = ComponentApi.Resolve(request);
        // Ultimate Chaos unlocks effect catalogs, not identity catalogs. Names and their reserved official
        // collisions always remain scoped to the generated card's own character/colorless pool.
        var catalog = profile.NameCatalog;
        // Official names are reserved before the run begins. Generated names therefore avoid both
        // duplicates inside this pool and accidental collisions with the source character's real cards.
        foreach (var recipe in catalog.Recipes)
        {
            _usedChineseNames.Add(recipe.ChineseTitle);
            if (!string.IsNullOrWhiteSpace(recipe.EnglishTitle))
                _usedEnglishNames.Add(recipe.EnglishTitle);
        }
        var frequencyTracker = profile.CreateOccurrencePolicy();
        // A run builds hundreds of cards. These assemblers only keep immutable catalog indexes plus the
        // generator-owned Random/name sets, so constructing one per card was pure repeated setup work.
        _normalAssembler = new ComponentAssemblyGenerator(random, _character, _usedChineseNames,
            _usedEnglishNames, _unlockComponentRoles, ancientFuelActive: ancientFuelActive,
            suppressDerivativeReferences: suppressDerivativeReferences, balancedValues: balancedValues,
            randomizeNumericValues: randomizeNumericValues, usedEffectSignatures: _usedEffectSignatures,
            frequencyTracker: frequencyTracker, usedPoolUniqueComponents: _usedPoolUniqueComponents,
            profile: profile, profileRegistrationId: request.ProfileId);
        _nonSpecialXAssembler = new ComponentAssemblyGenerator(random, _character, _usedChineseNames,
            _usedEnglishNames, _unlockComponentRoles, SpecialXGenerationMode.Disabled, ancientFuelActive,
            suppressDerivativeReferences, balancedValues, randomizeNumericValues, _usedEffectSignatures,
            frequencyTracker, _usedPoolUniqueComponents, profile, request.ProfileId);
        _forcedSpecialXAssembler = new ComponentAssemblyGenerator(random, _character, _usedChineseNames,
            _usedEnglishNames, _unlockComponentRoles, SpecialXGenerationMode.Forced, ancientFuelActive,
            suppressDerivativeReferences, balancedValues, randomizeNumericValues, _usedEffectSignatures,
            frequencyTracker, _usedPoolUniqueComponents, profile, request.ProfileId);
        _referenceFreeNonSpecialXAssembler = suppressDerivativeReferences
            ? _nonSpecialXAssembler
            : new ComponentAssemblyGenerator(random, _character, _usedChineseNames, _usedEnglishNames,
                _unlockComponentRoles, SpecialXGenerationMode.Disabled, ancientFuelActive,
                suppressDerivativeReferences: true, balancedValues: balancedValues,
                randomizeNumericValues: randomizeNumericValues, usedEffectSignatures: _usedEffectSignatures,
                frequencyTracker: frequencyTracker, usedPoolUniqueComponents: _usedPoolUniqueComponents,
                profile: profile, profileRegistrationId: request.ProfileId);
        _referenceFreeForcedSpecialXAssembler = suppressDerivativeReferences
            ? _forcedSpecialXAssembler
            : new ComponentAssemblyGenerator(random, _character, _usedChineseNames, _usedEnglishNames,
                _unlockComponentRoles, SpecialXGenerationMode.Forced, ancientFuelActive,
                suppressDerivativeReferences: true, balancedValues: balancedValues,
                randomizeNumericValues: randomizeNumericValues, usedEffectSignatures: _usedEffectSignatures,
                frequencyTracker: frequencyTracker, usedPoolUniqueComponents: _usedPoolUniqueComponents,
                profile: profile, profileRegistrationId: request.ProfileId);
    }

    public GeneratedCard Generate()
    {
        return OperationRuntimeSpecCompiler.Attach(_normalAssembler.Generate());
    }

    public GeneratedCard Generate(GeneratedRarity rarity)
    {
        return OperationRuntimeSpecCompiler.Attach(_normalAssembler.Generate(rarity));
    }

    internal GeneratedCard GenerateMatching(GeneratedRarity rarity, Func<GeneratedCard, bool> accept) =>
        OperationRuntimeSpecCompiler.Attach(_normalAssembler.GenerateMatching(rarity, accept));

    public GeneratedCard GenerateWithoutSpecialX(GeneratedRarity rarity)
    {
        return OperationRuntimeSpecCompiler.Attach(_nonSpecialXAssembler.Generate(rarity));
    }

    public GeneratedCard GenerateSpecialX(GeneratedRarity rarity)
    {
        // Forced mode aligns a legal scalable value with the hidden pre-conversion cost, so the assembler
        // itself only returns completed special-X cards. Keep one defensive assertion instead of a second
        // 20,000-attempt rejection loop around its own retry loop.
        var card = OperationRuntimeSpecCompiler.Attach(_forcedSpecialXAssembler.Generate(rarity));
        return SpecialXCardConverter.IsSpecial(card)
            ? card
            : throw new InvalidOperationException($"无法为 {_character}/{rarity} 生成特殊X费牌。");
    }

    public GeneratedCard GenerateReferenceFreeWithoutSpecialX(GeneratedRarity rarity) =>
        OperationRuntimeSpecCompiler.Attach(_referenceFreeNonSpecialXAssembler.Generate(rarity));

    internal GeneratedCard GenerateWithoutSpecialXMatching(GeneratedRarity rarity,
        Func<GeneratedCard, bool> accept) =>
        OperationRuntimeSpecCompiler.Attach(_nonSpecialXAssembler.GenerateMatching(rarity, accept));

    internal GeneratedCard GenerateReferenceFreeWithoutSpecialXMatching(GeneratedRarity rarity,
        Func<GeneratedCard, bool> accept) =>
        OperationRuntimeSpecCompiler.Attach(_referenceFreeNonSpecialXAssembler.GenerateMatching(rarity, accept));

    public GeneratedCard GenerateReferenceFreeSpecialX(GeneratedRarity rarity)
    {
        var card = OperationRuntimeSpecCompiler.Attach(_referenceFreeForcedSpecialXAssembler.Generate(rarity));
        return SpecialXCardConverter.IsSpecial(card)
            ? card
            : throw new InvalidOperationException($"无法为 {_character}/{rarity} 生成无衍生物引用的特殊X费牌。");
    }

    internal GeneratedCard GenerateReferenceFreeSpecialXMatching(GeneratedRarity rarity,
        Func<GeneratedCard, bool> accept)
    {
        var card = OperationRuntimeSpecCompiler.Attach(
            _referenceFreeForcedSpecialXAssembler.GenerateMatching(rarity, accept));
        return SpecialXCardConverter.IsSpecial(card)
            ? card
            : throw new InvalidOperationException($"无法为 {_character}/{rarity} 生成符合约束的无衍生物引用特殊X费牌。");
    }

}

public enum GeneratedCharacter { Ironclad, Silent, Defect, Necrobinder, Regent, Colorless }
public enum GeneratedCardType { Attack, Skill, Power }
public enum TargetMode { SingleEnemy, Other }
public enum GeneratedRarity { Basic, Common, Uncommon, Rare, Ancient }
public enum CardTag { Strike, Defend, Exhaust, Innate, Retain, Sly, Ethereal, Eternal, Unplayable, OstyAttack }
public enum OperationScope { SingleEnemyOnly, NonTargeted, Modifier, AbilityTrigger, ConditionalTrigger, AbilityRule, Independent }
public enum ComponentMultiplicity { Repeatable, SinglePerCard, UniquePerPool }
// New values must be appended: pool snapshots serialize enums numerically, so reordering older members would
// silently reinterpret upgrades when loading a save made by an earlier mod version.
public enum CardUpgradeKind { IncreaseNumber, ReduceSelfDamage, ReduceCost, GrantInnate, GrantRetain, RemoveExhaust, RemoveEthereal, UpgradeDerivative, ReduceStarCost, ReduceThreshold, ReduceNegativeNumber, UpgradeGeneratedCards, ChooseExhaust, AddCustomKeyword, RemoveCustomKeyword }

public sealed record GeneratorOperation(
    string Template,
    OperationScope Scope,
    string ChineseText,
    IReadOnlyDictionary<string, int> Parameters,
    string? CardTargetSlot = null,
    bool RequiresSingleTarget = false,
    string? DerivativeId = null,
    string? DerivativeEnchantmentId = null,
    string? OrbSourceId = null,
    string? OrbOutputId = null,
    int? DerivativeEnchantmentAmount = null,
    [property: System.Text.Json.Serialization.JsonIgnore]
    OperationRuntimeSpec? RuntimeSpec = null,
    [property: System.Text.Json.Serialization.JsonIgnore]
    OperationLocalizedText? LocalizedText = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? LocalizationId = null);

public sealed record GeneratedCardName(
    string Chinese,
    string English,
    IReadOnlyList<string> SourceCardIds);

/// <summary>
/// Localization-independent change applied when a generated card upgrades. The structural fields are the complete
/// upgrade contract; user-facing upgraded text is rendered from the resulting operation list.
/// </summary>
[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record CardUpgradeEffect(
    CardUpgradeKind Kind,
    int? OperationIndex = null,
    int? Delta = null,
    [property: System.Text.Json.Serialization.JsonIgnore]
    string? ValueSlotId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? KeywordId = null)
{
    // Schema 5-9 live snapshots and older history records persisted developer-only bilingual candidate labels.
    // Keep migration sinks for those JSON
    // properties, but never write or inspect them for newly generated cards.
    [System.Text.Json.Serialization.JsonPropertyName("chineseDescription")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyChineseDescription { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("englishDescription")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyEnglishDescription { get; init; }

    [Obsolete("Upgrade labels are not behavioral or player-facing data.")]
    [System.Text.Json.Serialization.JsonIgnore]
    public string ChineseDescription => LegacyChineseDescription ?? string.Empty;

    [Obsolete("Upgrade labels are not behavioral or player-facing data.")]
    [System.Text.Json.Serialization.JsonIgnore]
    public string EnglishDescription => LegacyEnglishDescription ?? string.Empty;

    /// <summary>Source-compatibility constructor for API v2 consumers. Labels never affect behavior.</summary>
    [Obsolete("Upgrade descriptions are diagnostic-only. Use Kind, OperationIndex, Delta and ValueSlotId.")]
    public CardUpgradeEffect(CardUpgradeKind kind, string chineseDescription, int? operationIndex = null,
        int? delta = null, string englishDescription = "", string? valueSlotId = null)
        : this(kind, operationIndex, delta, valueSlotId)
    {
        LegacyChineseDescription = chineseDescription;
        LegacyEnglishDescription = englishDescription;
    }
}

public sealed record CardUpgradePlan(
    int UpgradedCost,
    IReadOnlyList<CardUpgradeEffect> Effects,
    string UpgradedChineseDescription,
    IReadOnlyList<CardTag> AddedKeywords,
    string UpgradedEnglishDescription,
    IReadOnlyList<CardTag>? RemovedKeywords = null,
    int? UpgradedStarCost = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? AddedCustomKeywords = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? RemovedCustomKeywords = null);

public sealed record GeneratedCard(
    int Cost,
    GeneratedCardType Type,
    TargetMode Target,
    GeneratedRarity Rarity,
    string ChineseDescription,
    IReadOnlyList<CardTag> Tags,
    IReadOnlyList<GeneratorOperation> Operations,
    GeneratedCardName? Name = null,
    CardUpgradePlan? Upgrade = null,
    string EnglishDescription = "",
    GeneratedCharacter Character = GeneratedCharacter.Ironclad,
    int StarCost = -1,
    bool HasStarCostX = false,
    bool UnifiedChaos = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? CustomKeywords = null);

public static class GeneratedCardEffectIdentity
{
    /// <summary>
    /// Base effects are unique within one generated character/colorless pool. Printed resource cost, rarity,
    /// name, portrait and upgrade plan are deliberately excluded: two cards with the same keywords and the same
    /// executable base operations are still effect duplicates even if their shells differ.
    /// </summary>
    public static string Signature(GeneratedCard card)
    {
        var tags = string.Join(',', card.Tags.OrderBy(tag => tag)) + ";custom="
            + string.Join(',', (card.CustomKeywords ?? []).OrderBy(id => id, StringComparer.Ordinal));
        var operations = string.Join("||", card.Operations.Select(operation =>
            string.Join('|',
                operation.Template,
                operation.Scope,
                OperationRuntimeSpecCompiler.GetOrCompile(operation).StableSignature(),
                string.Join(',', operation.Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}")),
                operation.CardTargetSlot ?? string.Empty,
                operation.RequiresSingleTarget ? "target" : string.Empty,
                operation.DerivativeId ?? string.Empty,
                operation.DerivativeEnchantmentId ?? string.Empty,
                operation.DerivativeEnchantmentAmount?.ToString() ?? string.Empty,
                operation.OrbSourceId ?? string.Empty,
                operation.OrbOutputId ?? string.Empty)));
        return tags + "::" + operations;
    }

    /// <summary>
    /// Pool-level template identity ignores every printed numeric amount while retaining color, resources, card
    /// type, keywords, target/slot semantics, operation order and trigger ownership. Two same-color cards may
    /// therefore share a broad family such as Damage, but not the complete same-cost/same-type rules template
    /// merely because one rolled 9 Damage and the other rolled 12.
    /// </summary>
    public static string TemplateSignature(GeneratedCard card)
    {
        var tags = string.Join(',', card.Tags.OrderBy(tag => tag)) + ";custom="
            + string.Join(',', (card.CustomKeywords ?? []).OrderBy(id => id, StringComparer.Ordinal));
        var operations = string.Join("||", card.Operations.Select(operation =>
            string.Join('|',
                operation.Scope,
                OperationRuntimeSpecCompiler.StructuralRelationKey(operation),
                operation.Parameters.GetValueOrDefault("triggerIndex", -1),
                operation.CardTargetSlot ?? string.Empty,
                operation.RequiresSingleTarget ? "target" : string.Empty)));
        return string.Join("::",
            card.Character,
            card.Cost,
            card.StarCost,
            card.HasStarCostX ? "starX" : string.Empty,
            card.Type,
            card.Target,
            tags,
            operations);
    }

    public static bool TryAudit(IReadOnlyList<GeneratedCard> cards, out string failure)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < cards.Count; index++)
        {
            var signature = Signature(cards[index]);
            if (seen.TryGetValue(signature, out var first))
            {
                failure = $"Cards {first} and {index} have identical base effects.";
                return false;
            }
            seen[signature] = index;
        }
        failure = string.Empty;
        return true;
    }
}

public static class CardTemplateValidator
{
    public static void Validate(GeneratedCard card, bool allowRandomizedNumericValues = false)
    {
        foreach (var keywordId in (card.CustomKeywords ?? [])
                     .Concat(GeneratedCardTagPolicy.AddedCustomKeywords(card.Upgrade))
                     .Concat(GeneratedCardTagPolicy.RemovedCustomKeywords(card.Upgrade)))
        {
            if (!ComponentKeywordApi.IsRegistered(keywordId))
                throw new InvalidOperationException($"Generated card references unknown keyword '{keywordId}'.");
        }
        if (card.Upgrade?.Effects.Any(effect =>
                effect.Kind is (CardUpgradeKind.AddCustomKeyword or CardUpgradeKind.RemoveCustomKeyword)
                && (effect.KeywordId is null || !ComponentKeywordApi.IsRegistered(effect.KeywordId))) == true)
            throw new InvalidOperationException("Generated card contains an invalid custom-keyword upgrade.");
        if (card.Cost >= 5)
            throw new InvalidOperationException("生成卡的固定普通费用不能大于等于5；蓝星费用不受此限制。 ");
        if (card.Tags.Contains(CardTag.Sly)
            && (card.Cost is < 1 or > 3 || card.Cost < 0 || SpecialXCardConverter.IsSpecial(card)))
            throw new InvalidOperationException("奇巧牌必须由0至1费候选在最终结算时增加1至2点普通费用得到。 ");
        if (card.Tags.Contains(CardTag.Sly)
            && SlyKeywordTuning.ValidationTemplateCost(card.Cost, card.StarCost, card.HasStarCostX,
                card.Rarity, card.Type, card.Tags, card.Operations) is not (0 or 1))
            throw new InvalidOperationException("奇巧牌无法归入合法的0费或1费数值模板。 ");
        if (!allowRandomizedNumericValues && !card.Tags.Contains(CardTag.Sly)
            && SlyKeywordTuning.IsPureImmediateSelfRefund(card.Cost, card.Operations))
            throw new InvalidOperationException("仅即时返还自身全部普通费用的牌必须具有奇巧。 ");
        if (card.Tags.Contains(CardTag.Retain) && card.Tags.Contains(CardTag.Ethereal))
            throw new InvalidOperationException("生成卡的卡面不能同时具有保留与虚无。 ");
        if (card.Tags.Contains(CardTag.Retain)
            && CardKeywordTuning.RetainWeightPercent(card.Cost, card.StarCost, card.HasStarCostX) == 0)
            throw new InvalidOperationException("折合0费的卡不能具有保留。 ");
        if ((card.Type == GeneratedCardType.Power || card.Tags.Contains(CardTag.Exhaust))
            && card.Operations.Any(operation => operation.Template is
                "D:IncreaseThisCardCost" or "D:IncreaseAllClaws"))
            throw new InvalidOperationException("自身耗能增加与同词条牌战斗内增伤不能用于消耗牌或能力牌。 ");
        CardTextStyle.ValidateRenderedChinese(card.ChineseDescription);
        EnglishCardDescriptionRenderer.ValidateRenderedEnglish(card.EnglishDescription);
        if (card.Upgrade is { } styledUpgrade)
        {
            CardTextStyle.ValidateRenderedChinese(styledUpgrade.UpgradedChineseDescription);
            EnglishCardDescriptionRenderer.ValidateRenderedEnglish(styledUpgrade.UpgradedEnglishDescription);
        }
        if (card.Operations.Any(operation => OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags
                .Contains("standalone_keyword", StringComparer.Ordinal)))
            throw new InvalidOperationException("虚无必须作为关键词，不能作为独立operation文本。");
        foreach (var operation in card.Operations)
        {
            if (operation.LocalizationId is { } localizationId
                && (localizationId.Any(character => character > 0x7f)
                    || !ComponentLocalizationApi.TryGet(localizationId, out _)))
                throw new InvalidOperationException(
                    $"Operation {operation.Template} references unknown localization '{localizationId}'.");
            if (operation.Template == "R:ReturnAfterSkillsPlayed"
                && OperationRuntimeSpecCompiler.FixedValue(operation, "amount") < 2)
                throw new InvalidOperationException("每打出若干张技能牌后回手的门槛不能低于2。 ");
            if (operation.Template == "CL:NoBlockFromCards"
                && OperationRuntimeSpecCompiler.FixedValue(operation, "duration") > 3)
                throw new InvalidOperationException("接下来不能从卡牌获得格挡的持续时间不能超过3回合。 ");
            if (operation.Template == "D:IncreaseThisCardCost"
                && (card.Type == GeneratedCardType.Power
                    || !allowRandomizedNumericValues
                    && OperationRuntimeSpecCompiler.StaticLiteralValue(operation, "amount") is not (>= 1 and <= 3)
                    || operation.Parameters.ContainsKey("triggerIndex")))
                throw new InvalidOperationException("此牌自身耗能增加值只能为1至3，且只能作为非能力牌的即时效果。 ");
            if (!allowRandomizedNumericValues && operation.Template == "NCR:BlockTripleOstyMaxHp"
                && OperationRuntimeSpecCompiler.FixedValue(operation, "amount") > 3)
                throw new InvalidOperationException("按奥斯提最大生命值获得格挡的倍率不能超过3。 ");
            if (operation.DerivativeId is { } derivativeId
                && (!DerivativeSlotCatalog.IsSlotOperation(operation.Template)
                    || !DerivativeSlotCatalog.IsKnownId(derivativeId)
                    || DerivativeSlotCatalog.Resolve(derivativeId, operation.Template) is not { } slottedDerivative
                    || !DerivativeSlotCatalog.CanUseAssigned(operation.Template, slottedDerivative)))
                throw new InvalidOperationException($"{operation.Template} 使用了未知衍生物槽 {derivativeId}。");
            if (operation.DerivativeId is not null
                && DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template) is { } derivative
                && !operation.ChineseText.Contains(DerivativeSlotCatalog.ChineseCardName(operation),
                    StringComparison.Ordinal))
                throw new InvalidOperationException($"{operation.Template} 的描述与衍生物槽 {operation.DerivativeId} 不一致。");
            if (operation.DerivativeEnchantmentId is { } enchantmentId)
            {
                var enchantment = DerivativeEnchantmentCatalog.Resolve(enchantmentId);
                var enchantedDerivative = DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template);
                if (!DerivativeSlotCatalog.IsProducer(operation.Template) || enchantment is null || enchantedDerivative is null
                    || !DerivativeEnchantmentCatalog.CanUse(enchantedDerivative, enchantment))
                    throw new InvalidOperationException(
                        $"{operation.Template} 使用了不合法的衍生物附魔 {enchantmentId}。");
            }
            if (operation.DerivativeEnchantmentAmount is { } enchantmentAmount
                && (operation.DerivativeEnchantmentId is not { } amountEnchantmentId
                    || DerivativeEnchantmentCatalog.Resolve(amountEnchantmentId) is not { } amountEnchantment
                    || !DerivativeEnchantmentCatalog.IsAllowedAmount(amountEnchantment, enchantmentAmount)))
                throw new InvalidOperationException(
                    $"{operation.Template} 使用了不合法的衍生物附魔数值 {enchantmentAmount}。");
            if (operation.DerivativeId == "ink" && !DerivativeSlotCatalog.IsProducer(operation.Template))
                throw new InvalidOperationException("旧版墨影小刀槽只能用于生成或变化 operation。");
            if (DerivativeSlotCatalog.IsExhaustPileReference(operation.Template)
                && DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template)?.HasExhaust != true)
                throw new InvalidOperationException("读取某种衍生牌的消耗牌堆组件只能引用默认具有消耗的衍生牌。");
            if (operation.OrbSourceId is { } orbSourceId
                && (!OrbSlotCatalog.IsKnownId(orbSourceId)
                    || !OrbSlotCatalog.UsesSource(operation.Template)
                    || !OrbSlotCatalog.CanUseSource(operation.Template, OrbSlotCatalog.Resolve(orbSourceId)!)))
                throw new InvalidOperationException($"{operation.Template} 使用了不合法的充能球来源槽 {orbSourceId}。");
            if (operation.OrbOutputId is { } orbOutputId
                && (!OrbSlotCatalog.IsKnownId(orbOutputId)
                    || !OrbSlotCatalog.UsesOutput(operation.Template)
                    || !OrbSlotCatalog.CanUseOutput(operation.Template, OrbSlotCatalog.Resolve(orbOutputId)!)))
                throw new InvalidOperationException($"{operation.Template} 使用了不合法的充能球生成槽 {orbOutputId}。");
            if (!OrbSlotCatalog.IsSlotOperation(operation.Template)
                && (operation.OrbSourceId is not null || operation.OrbOutputId is not null))
                throw new InvalidOperationException($"{operation.Template} 不能携带充能球槽。");
        }
        if (!allowRandomizedNumericValues && card.Upgrade?.Effects.Any(effect => effect.OperationIndex is { } index
                && index >= 0 && index < card.Operations.Count
                && card.Operations[index].Template == "NCR:BlockTripleOstyMaxHp"
                && effect.Delta is > 0
                && OperationRuntimeSpecCompiler.FixedValue(card.Operations[index], "amount")
                    + effect.Delta > 3) == true)
            throw new InvalidOperationException("升级后的奥斯提最大生命值格挡倍率不能超过3。 ");
        if (card.Upgrade is { } durationUpgrade
            && CardUpgradeGenerator.ApplyEffectsToOperations(card.Operations, durationUpgrade.Effects)
                .Any(operation => operation.Template == "CL:NoBlockFromCards"
                    && OperationRuntimeSpecCompiler.FixedValue(operation, "duration") > 3))
            throw new InvalidOperationException("升级后不能从卡牌获得格挡的持续时间不能超过3回合。 ");
        if (!allowRandomizedNumericValues && !CardEffectRules.HasValidGrandFinaleAssembly(card.Operations))
            throw new InvalidOperationException("华丽收场的极端伤害值不能脱离抽牌堆为空的打出条件。");
        if (!CardEffectRules.HasValidGrandFinaleCost(card.Cost, card.StarCost, card.HasStarCostX,
                card.Operations))
            throw new InvalidOperationException("华丽收场的打出条件只能用于最终耗能为0的牌。");
        if (!CardEffectRules.HasAtMostTwoOfEachField(card.Operations))
            throw new InvalidOperationException("同一效果字段在一张卡牌上最多只能出现两次。");
        if (!CardEffectRules.HasNoDuplicateCardUniqueEffects(card.Operations))
            throw new InvalidOperationException("不可叠加的状态或规则效果在一张卡牌上只能出现一次。");
        if (!CardEffectRules.HasNoDuplicateXEffectKinds(card.Operations))
            throw new InvalidOperationException("X费牌不能包含两个相同形态的X费效果。");
        if (!CardEffectRules.HasValidShuffleThenDrawAssembly(card.Operations))
            throw new InvalidOperationException("将消耗牌堆以外的牌洗回抽牌堆后，必须紧接一项即时抽牌效果。");
        if (!CardEffectRules.HasValidPreventDrawOrdering(card.Operations))
            throw new InvalidOperationException("本回合不能再抽牌之后不能连接即时或当回合抽牌效果；跨多个回合持续触发的抽牌除外。");
        if (!CardEffectRules.HasValidExhaustAllHandOrdering(card.Operations))
            throw new InvalidOperationException("消耗所有手牌后，同一次结算中不能继续使用手牌，除非先补充手牌。");
        if (!CardEffectRules.HasValidPlayerSelectedExhaustCounts(card.Operations))
            throw new InvalidOperationException("必须选择消耗的效果只能选择1张牌；多张牌必须使用“至多”效果。");
        if (!allowRandomizedNumericValues && !CardEffectRules.HasValidHighCostThresholds(card.Operations))
            throw new InvalidOperationException("耗能大于等于门槛只能取1、2或3。 ");
        if (!CardEffectRules.HasValidEnergyXDoubleThreshold(card.Operations))
            throw new InvalidOperationException("X翻倍的触发门槛必须在1至4之间。");
        if (!CardEffectRules.HasValidNumericSelfCostReductionAmounts(card.Cost, card.Operations))
            throw new InvalidOperationException("本牌耗能减少量必须至少为1，且不能超过卡牌当前普通耗能。");
        if (card.Upgrade is { } boundedUpgrade
            && !CardEffectRules.HasValidNumericSelfCostReductionAmounts(
                boundedUpgrade.UpgradedCost, CardUpgradeGenerator.ApplyEffectsToOperations(
                    card.Operations, boundedUpgrade.Effects)))
            throw new InvalidOperationException("升级后的本牌耗能减少量不能超过升级后的普通耗能。");
        if ((!allowRandomizedNumericValues && !CardEffectRules.HasValidAllCardsCostIncreaseAssembly(card.Operations))
            || allowRandomizedNumericValues
            && card.Operations.Any(operation => operation.Template == "NCR:IncreaseAllCardCostsThisTurn")
            && !card.Operations.Any(CardEffectRules.IsBeneficialEffect))
            throw new InvalidOperationException("所有牌本回合耗能增加只能取1至3，且必须搭配其他正面效果。 ");
        if (!CardEffectRules.HasValidFailableConditionAssembly(card.Operations))
            throw new InvalidOperationException("可能失败的一次性条件不能承包整张牌的全部正面效果。 ");
        if (card.Type == GeneratedCardType.Power && card.Target != TargetMode.Other)
            throw new InvalidOperationException("能力牌不能选择单个敌人目标。");
        if (card.Type == GeneratedCardType.Power && card.Tags.Contains(CardTag.Exhaust))
            throw new InvalidOperationException("能力牌不能拥有消耗 keyword。");
        if (card.Type == GeneratedCardType.Power && card.Operations.Any(CardEffectRules.IsSelfCardMovementOrReplay))
            throw new InvalidOperationException("能力牌不能移动或重新打出自身；生成自身复制品除外。");
        if (card.Operations.Any(operation => operation.Template == "R:PutThisOnDraw")
            && card.Type == GeneratedCardType.Power)
            throw new InvalidOperationException("将这张牌放置于抽牌堆顶部不能用于能力牌。");
        if (card.Operations.Any(operation => operation.Template is "R:PutThisOnDraw" or "R:ReturnThisToHand")
            && card.Operations.Any(CardEffectRules.IsRestrictedEffect))
            throw new InvalidOperationException("打出后移动本牌的效果不能绕过战斗外限制效果的消耗约束。");
        if (!CardEffectRules.HasValidReturnThisToHandCost(card.Cost, card.StarCost, card.HasStarCostX,
                card.Operations))
            throw new InvalidOperationException("将这张牌放回手牌必须用于折合费用大于0的牌。");
        if (card.Type != GeneratedCardType.Power
            && card.Operations.Any(CardEffectRules.IsRestrictedEffect)
            && !card.Tags.Contains(CardTag.Exhaust))
            throw new InvalidOperationException("限制效果只能用于能力牌，或用于具有消耗的非能力牌。");
        if (!CardEffectRules.HasNoRepeatedTriggeredRestrictedEffects(card.Operations))
            throw new InvalidOperationException("会影响战斗外状态的限制效果不能由可重复触发条件反复结算。");
        if (!CardEffectRules.HasNoRepeatedTriggeredCombatDamageGrowth(card.Operations))
            throw new InvalidOperationException("本场战斗提高此牌伤害的效果不能由可重复触发条件反复结算。");
        if (!CardEffectRules.HasNoRepeatedTriggeredNextTurnAttackDouble(card.Operations))
            throw new InvalidOperationException("下个回合攻击伤害翻倍不能由可重复触发条件反复结算。");
        if (!CardEffectRules.HasNoFatalDoubleVulnerablePayoff(card.Operations))
            throw new InvalidOperationException("斩杀时不能将已被击杀目标的易伤翻倍。");
        if (!CardEffectRules.HasNoNegativeSelfExhaustPayoffs(card.Operations))
            throw new InvalidOperationException("“这张牌被消耗时”后面不能接负面效果。");
        if (!CardEffectRules.HasNoInvalidTriggeredStateEffects(card.Operations))
            throw new InvalidOperationException("由卡牌状态钩子执行的效果不能嵌套在其他条件或重复触发条件之后。");
        if (!CardEffectRules.HasNoStateConditionModifiers(card.Operations))
            throw new InvalidOperationException("消耗牌堆或抽牌堆顶部状态条件后必须连接可实际结算的效果，不能连接 modifier。");
        if (!CardEffectRules.HasValidTriggeredEndTurnAssembly(card.Operations))
            throw new InvalidOperationException("结束回合不能接在回合开始或回合结束触发器之后。");
        if (card.Type == GeneratedCardType.Power && !card.Operations.Any(CardEffectRules.IsPersistentPowerFoundation))
            throw new InvalidOperationException("能力牌必须包含跨回合触发、持续规则、覆甲、永久力量或限制效果组件。");
        if (card.Type != GeneratedCardType.Power)
        {
            var shouldBeAttack = CardEffectRules.HasAttackClassifyingDamage(card.Operations);
            if (shouldBeAttack != (card.Type == GeneratedCardType.Attack))
                throw new InvalidOperationException("非能力牌必须按是否含直接敌方伤害 operation 分类为攻击或技能。");
        }
        if (!card.UnifiedChaos && card.Character == GeneratedCharacter.Ironclad && card.Cost == -1
            && card.Target != TargetMode.Other && !SpecialXCardConverter.IsSpecial(card))
            throw new InvalidOperationException("战士样本中的 X 费牌不选择单个敌人目标。");
        var xRequirements = card.Operations.Select(CardEffectRules.XRequirement)
            .Where(requirement => requirement != XResourceRequirement.None).ToArray();
        var validXResources = card.Cost == -1
            ? !card.HasStarCostX && xRequirements.Length > 0
                && xRequirements.All(requirement => requirement is XResourceRequirement.Energy or XResourceRequirement.Either)
            : card.HasStarCostX
                ? xRequirements.Length > 0
                    && xRequirements.All(requirement => requirement is XResourceRequirement.Star or XResourceRequirement.Either)
                : xRequirements.Length == 0;
        if (!validXResources)
            throw new InvalidOperationException("X 结算必须与能量X或蓝星X费用的资源种类一致。");
        if ((card.Cost < 0 || card.HasStarCostX) && card.Operations.Any(CardEffectRules.IsSelfCostChange))
            throw new InvalidOperationException("X费牌不能包含调整本牌费用的效果。");
        if (card.Cost == 0 && card.StarCost < 0 && !card.HasStarCostX
            && card.Operations.Any(CardEffectRules.IsSelfCostReduction))
            throw new InvalidOperationException("0费牌不能包含降低本牌自身耗能的效果。");
        if (card.Cost == 0 && card.StarCost <= 0 && !card.HasStarCostX
            && card.Operations.Any(CardEffectRules.IsZeroCostCopyThisCardToDiscard))
            throw new InvalidOperationException("将这张牌的一张0费复制品加入弃牌堆只能用于具有资源费用的牌。");
        var specialXOperations = card.Operations.Where(SpecialXCardConverter.IsSpecial).ToArray();
        if (specialXOperations.Length > 0)
        {
            var resource = SpecialXCardConverter.Resource(specialXOperations[0]);
            var paysMatchingX = resource == SpecialXCardConverter.EnergyResource
                ? card.Cost < 0 && !card.HasStarCostX
                : card.Cost >= 0 && card.HasStarCostX;
            if (specialXOperations.Any(operation => OperationRuntimeSpecCompiler.GetOrCompile(operation).Values
                    .All(value => value.Source != "special_x"))
                || specialXOperations.Select(SpecialXCardConverter.Resource).Distinct().Count() != 1
                || !paysMatchingX)
                throw new InvalidOperationException("特殊X数值标记与卡牌支付资源不一致。");
        }
        if (!card.UnifiedChaos && card.Character != GeneratedCharacter.Regent && (card.StarCost >= 0 || card.HasStarCostX))
            throw new InvalidOperationException("只有储君牌可以具有蓝星费用。");
        if ((card.UnifiedChaos || card.Character == GeneratedCharacter.Regent)
            && (card.StarCost == 0 || card.StarCost < -1 || card.StarCost >= 0 && card.HasStarCostX))
            throw new InvalidOperationException("蓝星费用必须为无、正整数或X，且不能同时为固定值与X。");
        if (card.Target == TargetMode.Other && card.Operations.Where(CardEffectRules.RequiresSingleEnemyTarget)
            .Any(operation => !CardEffectRules.UsesExplicitRandomEnemyTarget(operation)
                && (!operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                    || triggerIndex < 0 || triggerIndex >= card.Operations.Count
                    || !CardEffectRules.CanResolveTriggeredEnemyTarget(card.Operations[triggerIndex], operation))))
            throw new InvalidOperationException("非单敌牌的取目标 operation 必须明确指向随机敌人，或明确引用触发事件提供的敌人："
                + string.Join(" / ", card.Operations.Select(operation =>
                    $"{operation.Template}[{operation.Parameters.GetValueOrDefault("triggerIndex", -1)}]:{operation.ChineseText}")));
        if (card.Type != GeneratedCardType.Power && card.Target == TargetMode.SingleEnemy
            && !card.Operations.Any(CardEffectRules.RequiresSingleEnemyTarget))
            throw new InvalidOperationException("单敌牌必须包含实际使用单敌目标的 operation。");
        if (card.Operations.Count == 0 || string.IsNullOrWhiteSpace(card.ChineseDescription))
            throw new InvalidOperationException("生成卡必须有 operation 和描述。");
        if (card.Type != GeneratedCardType.Power
            && card.Name?.Chinese.EndsWith("形态", StringComparison.Ordinal) == true)
            throw new InvalidOperationException("以“形态”结尾的卡名只能用于能力牌。");
        if (card.Type == GeneratedCardType.Power
            && card.Operations.Any(operation => operation.Template is "I:PlayThisCard" or "R:PlayThisCard"))
            throw new InvalidOperationException("能力牌不能包含“打出此牌”operation。");
        if (!CardEffectRules.HasValidPlayThisCardAssembly(card.Operations))
            throw new InvalidOperationException("“打出此牌”只能连接匹配的消耗牌堆触发器，且此牌必须在该触发器之前另有正常打出效果。");
        if (!CardEffectRules.HasValidCopyThisCardAssembly(card.Operations))
            throw new InvalidOperationException("将这张牌的复制品加入弃牌堆（包括0费复制品）必须搭配另一项实际正面效果。");
        if (!allowRandomizedNumericValues && !CardEffectRules.HasValidRandomCardGenerationCount(card.Operations))
            throw new InvalidOperationException("随机生成牌效果一次最多生成5张牌。");
        if (!CardEffectRules.HasValidAttackCostReductionAssembly(card.Operations))
            throw new InvalidOperationException("按本回合已打出的攻击牌或技能牌减少耗能不能是此牌唯一的效果。");
        if (!CardEffectRules.HasValidOstyAttackedCostAssembly(card.Operations))
            throw new InvalidOperationException("奥斯提攻击后的当回合0费规则必须具有条件前件，且此牌必须另有正常打出效果。");
        if (!CardEffectRules.HasValidSelfCostChangeAssembly(card.Operations))
            throw new InvalidOperationException("仅改变此牌自身耗能的组件必须搭配至少一项实际正面效果，不能只搭配负面效果。");
        if (!CardEffectRules.HasValidDeferredEnemyTargetAssembly(card.Operations))
            throw new InvalidOperationException("跨回合延迟触发器无法保留此牌最初选择的敌人，后续效果必须改用随机敌人或所有敌人。");
        if (!CardEffectRules.HasValidHitEnemyDamageAssembly(card.Operations))
            throw new InvalidOperationException("“对被命中的敌人造成伤害”只能作为激发充能球触发器的后续效果。");
        if (!CardEffectRules.HasValidEventAmountAssembly(card.Operations))
            throw new InvalidOperationException("“所有敌人失去等量生命”只能作为“奥斯提失去生命”触发器的后续效果。");
        if (!CardEffectRules.HasValidRepeatDamageAssembly(card.Operations))
            throw new InvalidOperationException("伤害次数 modifier 必须依附伤害；动态总段数不能与X段或固有多段伤害并存。");
        if (!CardEffectRules.HasValidNextAttackGrantAssembly(card.Operations))
            throw new InvalidOperationException("下一张攻击牌的效果块必须位于卡面末尾，并且只能连接一个可作用于该攻击牌的效果。");
        if (card.Type == GeneratedCardType.Power && card.Operations.Any(CardEffectRules.IsNextAttackGrantTrigger))
            throw new InvalidOperationException("限定下一张攻击牌的临时效果不能用于能力牌。");
        if (!card.Operations.Any(CardEffectRules.IsBeneficialEffect))
            throw new InvalidOperationException("任何费用的卡牌都必须至少包含一项实际正面效果。");
        if (card.Tags.Distinct().Count() != card.Tags.Count)
            throw new InvalidOperationException("卡牌 tag 不能重复。");
        if ((card.CustomKeywords ?? []).Distinct(StringComparer.Ordinal).Count()
            != (card.CustomKeywords?.Count ?? 0))
            throw new InvalidOperationException("Custom keyword IDs cannot repeat on one card.");
        foreach (var operation in card.Operations)
        {
            if (CardEffectRules.NeedsExternalCardSlot(operation) && string.IsNullOrWhiteSpace(operation.CardTargetSlot))
                throw new InvalidOperationException($"{operation.Template}（{operation.ChineseText}）引用了卡牌对象却没有 cardTargetSlot。");
        }
        var explicitCardSlots = card.Operations
            .Select(operation => operation.CardTargetSlot)
            .Where(slot => !string.IsNullOrWhiteSpace(slot) && slot != "thisCard")
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (explicitCardSlots > 1)
            throw new InvalidOperationException("一张生成卡最多只能有一个显式 card slot。");
        for (var i = 0; i < card.Operations.Count; i++)
        {
            if (CardEffectRules.OperationNeedsChoiceContext(card.Operations[i])
                && card.Operations[i].Parameters.TryGetValue("triggerIndex", out var choiceTriggerIndex)
                && (choiceTriggerIndex < 0 || choiceTriggerIndex >= i
                    || !CardEffectRules.TriggerSupportsChoiceContext(card.Operations[choiceTriggerIndex])))
                throw new InvalidOperationException("需要玩家选择的触发效果必须连接提供 PlayerChoiceContext 的触发钩子："
                    + string.Join(" / ", card.Operations.Select((operation, index) =>
                        $"{index}:{operation.Template}[{operation.Parameters.GetValueOrDefault("triggerIndex", -1)}]:{operation.ChineseText}")));
            if (CardEffectRules.IsConditionalDamageVariant(card.Operations[i])
                && (i == 0
                    || !CardEffectRules.IsDependencyPrefix(card.Operations[i - 1])
                    || !CardEffectRules.IsLegalDependencyPayoff(card.Operations[i - 1], card.Operations[i])))
                throw new InvalidOperationException("“这张牌就造成……”或“对该目标造成……”必须紧跟与其匹配的条件/计数前件。");
            if (CardEffectRules.IsDependencyPrefix(card.Operations[i]))
            {
                if (CardEffectRules.IsStandaloneEventDependencyPrefix(card.Operations[i])
                    && card.Operations[i].Parameters.ContainsKey("triggerIndex"))
                    throw new InvalidOperationException("独立事件前件不能嵌套在另一项触发条件之下。 ");
                var hasPayoff = i + 1 < card.Operations.Count
                    && CardEffectRules.IsLegalDependencyPayoff(card.Operations[i], card.Operations[i + 1]);
                var correctOwner = hasPayoff && (card.Operations[i].Scope == OperationScope.ConditionalTrigger
                    ? card.Operations[i + 1].Parameters.GetValueOrDefault("triggerIndex", -1) == i
                    : card.Operations[i].Parameters.GetValueOrDefault("triggerIndex", -1)
                        == card.Operations[i + 1].Parameters.GetValueOrDefault("triggerIndex", -1));
                if (!hasPayoff || !correctOwner)
                    throw new InvalidOperationException("条件/计数前件必须紧邻合法后件，且二者必须属于同一触发器："
                        + string.Join(" / ", card.Operations.Select((candidate, candidateIndex) =>
                            $"{candidateIndex}:{candidate.Template}[{candidate.Parameters.GetValueOrDefault("triggerIndex", -1)}]")));
            }
            if (card.Operations[i].Template == "C:ifLastDrawnSkill"
                && (i == 0
                    || card.Type == GeneratedCardType.Power
                    || card.Operations[i - 1].Template != "N:Draw"
                    || card.Operations[i - 1].Parameters.ContainsKey("triggerIndex")
                    || !allowRandomizedNumericValues
                    && OperationRuntimeSpecCompiler.FixedValue(card.Operations[i - 1], "draw") != 1))
                throw new InvalidOperationException("“如果抽到的是技能牌”必须紧跟一条未嵌套的“抽1张牌”effect。 ");
            if (!allowRandomizedNumericValues && card.Operations[i].Template == "N:Draw"
                && OperationRuntimeSpecCompiler.FixedValue(card.Operations[i], "draw") > 4)
                throw new InvalidOperationException("普通抽牌 operation 的基础抽牌数不能超过4。 ");
            var primaryValue = OperationRuntimeSpecCompiler.PrimaryFixedValue(card.Operations[i]);
            if (!allowRandomizedNumericValues
                && CardEffectRules.IsDirectOrbChannel(card.Operations[i]) && primaryValue is { } channelCount
                && OrbSlotCatalog.ResolveOutput(card.Operations[i].OrbOutputId, card.Operations[i].Template) is { } outputOrb)
            {
                var effectiveOrbCost = card.Cost < 0 ? 1 : Math.Max(0, card.Cost);
                if (card.HasStarCostX) effectiveOrbCost += 2;
                else effectiveOrbCost += CardEffectRules.StarCostEnergyEquivalent(card.StarCost);
                var perEnemy = i > 0 && card.Operations[i - 1].Template == "D:ForEachEnemy";
                var maximum = OrbSlotCatalog.MaximumDirectChannelCount(outputOrb, effectiveOrbCost, perEnemy);
                if (channelCount > maximum)
                    throw new InvalidOperationException(
                        $"{outputOrb.ChineseName}充能球在当前费用与触发频率下最多生成{maximum}个。 ");
            }
            if (!allowRandomizedNumericValues && card.Operations[i].Template == "D:LoseOrbSlots"
                && OperationRuntimeSpecCompiler.FixedValue(card.Operations[i], "amount") > 2)
                throw new InvalidOperationException("单次失去的充能球栏位不能超过2个。 ");
            if (!allowRandomizedNumericValues
                && (card.Operations[i].Template is "N:AllD" or "N:RandomD" or "N:RandomPoison")
                && (OperationRuntimeSpecCompiler.FixedValue(card.Operations[i], "hits") is > 5
                    || OperationRuntimeSpecCompiler.FixedValue(card.Operations[i], "repeat_count") is > 5))
                throw new InvalidOperationException("多段伤害或多次中毒 operation 的基础重复次数不能超过5。 ");
            if (card.Operations[i].Template == "N:RetaliateDamage"
                && (!card.Operations[i].Parameters.TryGetValue("triggerIndex", out var retaliationTrigger)
                    || retaliationTrigger < 0
                    || retaliationTrigger >= i
                    || OperationRuntimeSpecCompiler.GetOrCompile(card.Operations[retaliationTrigger]).Trigger
                        is not { Kind: "attack_received", Lifetime: "this_turn" }))
                throw new InvalidOperationException("对攻击者造成伤害必须连接“本回合每当你受到一次攻击时”触发器。");
            if (CardEffectRules.IsForEachExhaustTrigger(card.Operations[i])
                && !card.Operations.Take(i).Any(operation => CardEffectRules.IsCompatiblePriorExhaust(operation, card.Operations[i])))
                throw new InvalidOperationException("“每消耗一张……”触发器前必须已有可实际消耗牌的 effect。");
            if (CardEffectRules.IsCombatBaseDamageIncrease(card.Operations[i])
                && (card.Type == GeneratedCardType.Power
                    || card.Tags.Contains(CardTag.Exhaust)
                    || !card.Operations.Take(i).Any(CardEffectRules.IsEnemyDamage)))
                throw new InvalidOperationException("战斗内基础伤害增长必须依附于先前的伤害 effect，且不能用于消耗牌或能力牌。");
            if (card.Operations[i].Template == "NCR:ApplyDoomEqualDamage"
                && !card.Operations.Take(i).Any(CardEffectRules.IsEnemyDamage))
                throw new InvalidOperationException("给予等量于所造成伤害的灾厄必须位于直接伤害 effect 之后。");
            if (card.Operations[i].Template == "NCR:DoubleHangDamage"
                && !card.Operations.Take(i).Any(CardEffectRules.IsEnemyDamage))
                throw new InvalidOperationException("“吊杀”规则必须位于直接伤害 effect 之后。");
            if (card.Operations[i].Template is "D:IncreaseAllClaws" or "NCR:IncreaseThisCardDamageRun"
                && !card.Operations.Take(i).Any(CardEffectRules.IsEnemyDamage))
                throw new InvalidOperationException("牌自身或同名效果的伤害成长必须依附于先前的直接伤害 effect。");
            if (card.Operations[i].Template == "D:IncreaseThisCardBlockRun"
                && (!card.Operations.Take(i).Any(operation => operation.Template == "N:B")
                    || card.Type != GeneratedCardType.Power && !card.Tags.Contains(CardTag.Exhaust)))
                throw new InvalidOperationException("永久格挡成长必须依附于先前的格挡 effect，且技能牌必须具有消耗。");
            if (CardEffectRules.TriggerNeedsLinkedEffect(card.Operations[i]) &&
                !card.Operations.Skip(i + 1).Any(operation =>
                    operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex) && triggerIndex == i))
            {
                throw new InvalidOperationException("能力触发条件必须至少连接一个被触发的 effect operation。");
            }
            if (CardEffectRules.IsFatalCondition(card.Operations[i]) && !card.Operations.Take(i).Any(CardEffectRules.IsEnemyDamage))
                throw new InvalidOperationException("斩杀条件前必须已有直接敌方伤害 operation。");
        }
        if (!IsAllowedCost(card.Character, card.Type, card.Cost, card.UnifiedChaos,
                SpecialXCardConverter.IsSpecial(card)))
            throw new InvalidOperationException("费用不属于战士样本分布支持集。");
        if (card.Name is not null && (string.IsNullOrWhiteSpace(card.Name.Chinese) || string.IsNullOrWhiteSpace(card.Name.English)))
            throw new InvalidOperationException("生成卡必须有完整的中英文名称。");
        if (card.Tags.Contains(CardTag.Strike) && card.Name is not null
            && (!card.Name.Chinese.EndsWith("打击", StringComparison.Ordinal) || !card.Name.English.EndsWith(" Strike", StringComparison.Ordinal)))
            throw new InvalidOperationException("打击牌名称必须锁定“打击 / Strike”后缀。");
        if (card.Upgrade is { } upgrade)
        {
            var addedKeywords = GeneratedCardTagPolicy.AddedKeywords(upgrade);
            var removedKeywords = GeneratedCardTagPolicy.RemovedKeywords(upgrade);
            var addedCustomKeywords = GeneratedCardTagPolicy.AddedCustomKeywords(upgrade);
            var removedCustomKeywords = GeneratedCardTagPolicy.RemovedCustomKeywords(upgrade);
            if (upgrade.Effects.Count is < 1 or > 2)
                throw new InvalidOperationException("一张卡必须有一至两个升级效果。");
            if (upgrade.UpgradedCost != card.Cost && upgrade.UpgradedCost != card.Cost - 1)
                throw new InvalidOperationException("升级只能将费用减少1。");
            if (card.Tags.Contains(CardTag.Sly)
                && (upgrade.UpgradedCost != card.Cost
                    || upgrade.UpgradedStarCost is not null
                    || upgrade.Effects.Any(effect => effect.Kind is
                        CardUpgradeKind.ReduceCost or CardUpgradeKind.ReduceStarCost)))
                throw new InvalidOperationException("奇巧牌的升级不能降低普通费用或蓝星费用。 ");
            if (upgrade.UpgradedCost == 0 && card.StarCost < 0 && !card.HasStarCostX
                && card.Operations.Any(CardEffectRules.IsSelfCostReduction))
                throw new InvalidOperationException("升级后为0费的牌不能保留降低本牌自身耗能的效果。");
            if (upgrade.UpgradedStarCost is { } upgradedStarCost
                && (!card.UnifiedChaos && card.Character != GeneratedCharacter.Regent || card.StarCost <= 0
                    || upgradedStarCost != card.StarCost - 1 || upgradedStarCost < 0
                    || !upgrade.Effects.Any(effect => effect.Kind == CardUpgradeKind.ReduceStarCost)))
                throw new InvalidOperationException("蓝星费用升级只能将固定蓝星费用减少1。");
            if (addedKeywords.Contains(CardTag.Innate)
                && !card.UnifiedChaos
                && card.Character is GeneratedCharacter.Ironclad or GeneratedCharacter.Silent
                && card.Type != GeneratedCardType.Power)
                throw new InvalidOperationException("固有 keyword 只能由能力牌升级获得。");
            if (addedKeywords.Contains(CardTag.Retain)
                && !card.UnifiedChaos
                && card.Character is GeneratedCharacter.Ironclad or GeneratedCharacter.Defect)
                throw new InvalidOperationException("该角色不能通过升级获得保留。");
            if (addedKeywords.Contains(CardTag.Retain)
                && CardKeywordTuning.RetainWeightPercent(card.Cost, card.StarCost, card.HasStarCostX) == 0)
                throw new InvalidOperationException("折合0费的卡不能通过升级获得保留。 ");
            if (removedKeywords.Contains(CardTag.Exhaust)
                && !card.UnifiedChaos
                && (card.Character == GeneratedCharacter.Ironclad || !card.Tags.Contains(CardTag.Exhaust)))
                throw new InvalidOperationException("去除消耗升级只能用于拥有相应原版升级路径的消耗牌。");
            if (removedKeywords.Contains(CardTag.Exhaust)
                && card.Operations.Any(CardEffectRules.IsRestrictedEffect))
                throw new InvalidOperationException("限制效果不能通过升级去除消耗。");
            if (removedKeywords.Contains(CardTag.Ethereal)
                && !card.Tags.Contains(CardTag.Ethereal))
                throw new InvalidOperationException("去除虚无升级只能用于原本带虚无的牌。");
            var upgradedHasRetain = card.Tags.Contains(CardTag.Retain)
                || addedKeywords.Contains(CardTag.Retain);
            var upgradedHasEthereal = card.Tags.Contains(CardTag.Ethereal)
                && !removedKeywords.Contains(CardTag.Ethereal);
            if (upgradedHasRetain && upgradedHasEthereal)
                throw new InvalidOperationException("升级后的卡面不能同时具有保留与虚无。 ");
            if (addedCustomKeywords.Overlaps(removedCustomKeywords))
                throw new InvalidOperationException("同一次升级不能同时添加和移除同一个自定义关键词。 ");
            if (upgrade.Effects.Any(effect => effect.Kind == CardUpgradeKind.ReduceSelfDamage && effect.Delta is not (>= -4 and <= -1)))
                throw new InvalidOperationException("自伤升级只能减少1至4点生命。");
            if (upgrade.Effects.Any(effect => effect.Kind == CardUpgradeKind.IncreaseNumber
                    && !IsValidIncreaseNumberUpgrade(card, effect, allowRandomizedNumericValues)))
                throw new InvalidOperationException("普通数值升级只能增加1至4点；金币升级可按原版比例增加，但不得超过原数值。");
            if (upgrade.Effects.Any(effect => effect.Kind == CardUpgradeKind.IncreaseNumber
                    && effect.OperationIndex is { } index
                    && index >= 0 && index < card.Operations.Count
                    && CardEffectRules.IsReducibleNegativeNumber(card.Operations[index])
                    && !(card.Character == GeneratedCharacter.Silent
                        && card.Operations[index].Template == "N:Discard" && effect.Delta == 1)))
                throw new InvalidOperationException("负面效果的数值不能在升级后增加。");
            if (upgrade.Effects.Any(effect => effect.Kind == CardUpgradeKind.ReduceThreshold && effect.Delta != -1))
                throw new InvalidOperationException("费用门槛升级只能将门槛减少1。");
            if (upgrade.Effects.Any(effect => effect.Kind == CardUpgradeKind.ReduceThreshold
                    && effect.OperationIndex is { } index
                    && index >= 0 && index < card.Operations.Count
                    && card.Operations[index].Template == "R:ReturnAfterSkillsPlayed"
                    && OperationRuntimeSpecCompiler.FixedValue(card.Operations[index], "amount")
                        + (effect.Delta ?? 0) < 2))
                throw new InvalidOperationException("升级后的技能牌回手门槛不能低于2。 ");
            if (upgrade.Effects.Any(effect => effect.Kind == CardUpgradeKind.ReduceNegativeNumber
                    && (effect.Delta != -1
                        || effect.OperationIndex is not { } index
                        || index < 0 || index >= card.Operations.Count
                        || !CardEffectRules.IsReducibleNegativeNumber(card.Operations[index])
                        || card.Operations[index].Template == "N:Discard")))
                throw new InvalidOperationException("负面数值升级只能将合法的负面数值减少1。");
        }
    }

    private static bool IsValidIncreaseNumberUpgrade(GeneratedCard card, CardUpgradeEffect effect,
        bool allowRandomizedNumericValues)
    {
        if (effect.OperationIndex is not { } operationIndex
            || operationIndex < 0 || operationIndex >= card.Operations.Count
            || effect.Delta is not { } delta || delta < 1)
            return false;
        var operation = card.Operations[operationIndex];
        if (allowRandomizedNumericValues) return true;
        var maximum = CardEffectRules.IsGoldGainOperation(operation)
            ? OperationRuntimeSpecCompiler.StaticLiteralValue(operation, "amount")
            : 4;
        return delta <= maximum;
    }

    private static bool IsAllowedCost(GeneratedCharacter character, GeneratedCardType type, int cost,
        bool unifiedChaos, bool specialX)
    {
        if (unifiedChaos) return cost is >= -1 and <= 9;
        var maximum = character switch
        {
            GeneratedCharacter.Defect => 5,
            GeneratedCharacter.Necrobinder => 9,
            GeneratedCharacter.Regent => 4,
            GeneratedCharacter.Colorless => 3,
            _ => 3
        };
        // Native Ironclad/Silent pools do not contain ordinary X-cost Powers. A special-X card is different:
        // it starts as a legal fixed-cost Power and the shared runtime records its paid X in ChaosCompositePower.
        var minimum = type == GeneratedCardType.Power
            && character is GeneratedCharacter.Ironclad or GeneratedCharacter.Silent
            && !specialX ? 1 : -1;
        return cost >= minimum && cost <= maximum;
    }

}

/// <summary>
/// The data layer separates triggers from effects; the renderer rejoins them by triggerIndex.
/// </summary>
public static class CardDescriptionRenderer
{
    private sealed record RenderedLine(string Text, bool Last);

    private static bool MustRenderLast(GeneratorOperation operation) =>
        operation.Template == "CL:NoBlockFromCards";

    internal static string RenderNextAttackGrant(GeneratorOperation trigger,
        IReadOnlyList<GeneratorOperation> effects, Func<GeneratorOperation, string> text, bool chinese)
    {
        var count = CardEffectRules.NextAttackGrantCount(trigger);
        if (effects.Count == 1 && effects[0].Template == "I:ReplayAttack"
            && trigger.Template is "C:grantNextAttacksThisTurn" or "C:untilTurnEnd")
            return chinese
                ? $"在这个回合，你打出的下{count}张攻击牌会被额外打出一次。"
                : count == 1
                    ? "This turn, your next Attack is played an extra time."
                    : $"This turn, your next {count} Attacks are played an extra time.";
        if (effects.Count == 1 && effects[0].Template == "I:SetCostZero"
            && trigger.Template is "C:grantNextAttack" or "C:for")
            return chinese
                ? "你打出的下一张攻击牌耗能变为0。"
                : "The next Attack you play costs 0 Energy.";

        var header = chinese
            ? trigger.Template is "C:grantNextAttacksThisTurn" or "C:untilTurnEnd"
                ? $"在这个回合，你打出的下{count}张攻击牌获得效果："
                : "你打出的下一张攻击牌获得效果："
            : trigger.Template is "C:grantNextAttacksThisTurn" or "C:untilTurnEnd"
                ? count == 1 ? "This turn, your next Attack gains:" : $"This turn, your next {count} Attacks gain:"
                : "Your next Attack gains:";
        var payload = effects.Select(effect => AdaptNextAttackPayoff(effect, text(effect), chinese));
        return header + "\n" + string.Join(chinese ? "" : " ", payload);
    }

    private static string AdaptNextAttackPayoff(GeneratorOperation effect, string rendered, bool chinese)
    {
        if (chinese)
        {
            if (effect.Template == "I:ReplayAttack") return "额外打出一次。";
            if (effect.Template == "I:SetCostZero") return "耗能变为0。";
            var text = rendered.Trim().TrimEnd('。');
            var strike = Regex.Match(text, @"名称含“打击”.*?额外造成(\d+)点伤害");
            if (strike.Success) return $"你每有一张名字中含有“打击”的牌，伤害+{strike.Groups[1].Value}。";
            var exhaust = Regex.Match(text, @"消耗牌堆每有一张牌.*?额外造成(\d+)点伤害");
            if (exhaust.Success) return $"你的消耗牌堆每有一张牌，伤害+{exhaust.Groups[1].Value}。";
            var vulnerable = Regex.Match(text, @"该敌人每有一层易伤.*?额外造成(\d+)点伤害");
            if (vulnerable.Success) return $"目标敌人每有一层易伤，伤害+{vulnerable.Groups[1].Value}。";
            return text.Replace("这张牌就额外造成", "伤害+", StringComparison.Ordinal)
                .Replace("这张牌额外造成", "伤害+", StringComparison.Ordinal)
                .Replace("这张牌", "该攻击牌", StringComparison.Ordinal) + "。";
        }

        if (effect.Template == "I:ReplayAttack") return "Play it an extra time.";
        if (effect.Template == "I:SetCostZero") return "Costs 0 Energy.";
        var english = rendered.Trim().TrimEnd('.');
        var amount = Regex.Match(english, @"(\d+) additional damage");
        if (amount.Success && english.Contains("Strike", StringComparison.Ordinal))
            return $"+{amount.Groups[1].Value} damage for each card you have with “Strike” in its name.";
        if (amount.Success && english.Contains("Exhaust", StringComparison.Ordinal))
            return $"+{amount.Groups[1].Value} damage for each card in your Exhaust Pile.";
        if (amount.Success && english.Contains("Vulnerable", StringComparison.Ordinal))
            return $"+{amount.Groups[1].Value} damage for each Vulnerable on the target enemy.";
        return english.Replace("This card", "It", StringComparison.Ordinal) + ".";
    }

    private static string RenderEffects(IReadOnlyList<GeneratorOperation> effects)
    {
        var pieces = new List<string>();
        for (var index = 0; index < effects.Count; index++)
        {
            var operation = effects[index];
            if (CardEffectRules.IsCurrentBlockDamageAnchor(effects, index))
                continue;
            if (CardEffectRules.IsDependencyPrefix(operation) && index + 1 < effects.Count
                && CardEffectRules.IsLegalDependencyPayoff(operation, effects[index + 1]))
            {
                pieces.Add(CardTextStyle.Chinese(operation).TrimEnd('。')
                    + CardTextStyle.Chinese(effects[++index]));
                continue;
            }
            pieces.Add(CardTextStyle.Chinese(operation));
        }
        return string.Join(string.Empty, pieces);
    }

    internal static string JoinTriggeredEffects(string rendered, bool chinese)
    {
        // Every listed effect is owned by the same trigger. Joining its internal sentences with “and” keeps the
        // condition's scope explicit; a full stop here previously made the final effect look unconditional.
        if (chinese)
            return Regex.Replace(rendered, @"。(?!\{InCombat:)(?=.)", "，并且")
                .Replace("，并且然后", "，并且", StringComparison.Ordinal);
        var clauses = rendered.Split(". ", StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" and ", clauses.Select((clause, index) => index == 0
            ? clause
            : LowerFirst(clause)));
    }

    private static string LowerFirst(string text)
    {
        if (text.Length == 0 || text.StartsWith("Osty", StringComparison.Ordinal)
            || text.StartsWith("ALL", StringComparison.Ordinal)
            || text.StartsWith("King's Sword", StringComparison.Ordinal)
            || text.StartsWith("Sovereign Blade", StringComparison.Ordinal)
            || text.Length > 1 && char.IsUpper(text[0]) && char.IsUpper(text[1]))
            return text;
        return char.ToLowerInvariant(text[0]) + text[1..];
    }

    public static string Render(IReadOnlyList<GeneratorOperation> operations)
    {
        var lines = new List<RenderedLine>();
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (CardEffectRules.IsCurrentBlockDamageAnchor(operations, index))
                continue;
            if (operation.Template is "N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK")
                continue;
            if (operation.Template == "I:ExhaustRandomAttack"
                && index + 1 < operations.Count
                && operations[index + 1].Template == "I:AddExhaustedAttackDamage")
            {
                var chosen = OperationRuntimeSpecCompiler.GetOrCompile(operation).Variant
                    is "selected" or "i_exhaustselectedattack";
                lines.Add(new RenderedLine(chosen
                    ? "选择你手牌中的一张攻击牌，将其消耗，并将它的伤害添加给这张牌。"
                    : "消耗你的手牌中随机一张攻击牌，并将它的伤害添加给这张牌。", false));
                index++;
                continue;
            }
            if (operation.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger)
            {
                var effects = operations
                    .Skip(index + 1)
                    .Where(candidate => candidate.Scope is not OperationScope.AbilityTrigger and not OperationScope.ConditionalTrigger)
                    .Where(candidate => candidate.Parameters.TryGetValue("triggerIndex", out var triggerIndex) && triggerIndex == index)
                    .ToArray();
                if (CardEffectRules.IsNextAttackGrantTrigger(operation))
                {
                    lines.Add(new RenderedLine(
                        RenderNextAttackGrant(operation, effects, CardTextStyle.Chinese, chinese: true),
                        effects.Any(MustRenderLast)));
                    continue;
                }
                var triggerText = CardTextStyle.Chinese(operation).TrimEnd('。', '，', '；');
                var separator = operation.Template == "C:playableIfDrawPileEmpty" ? "。"
                    : operation.Template == "C:untilTurnEndCardDrawn" ? "，"
                    : operation.Template == "C:untilTurnEnd" && triggerText.Contains("受到一次攻击", StringComparison.Ordinal) ? "，都会"
                    : operation.Template == "C:ifLastDrawnSkill" || triggerText.StartsWith("如果", StringComparison.Ordinal) ? "，则"
                    : "，";
                var renderedEffects = JoinTriggeredEffects(RenderEffects(effects), chinese: true);
                if (operation.Scope == OperationScope.AbilityTrigger
                    && renderedEffects.StartsWith("在本回合", StringComparison.Ordinal))
                    renderedEffects = renderedEffects[1..];
                lines.Add(new RenderedLine(
                    effects.Length == 0 ? CardTextStyle.Chinese(operation) : $"{triggerText}{separator}{renderedEffects}",
                    MustRenderLast(operation) || effects.Any(MustRenderLast)));
                continue;
            }

            if (!operation.Parameters.ContainsKey("triggerIndex"))
            {
                if (CardEffectRules.IsDependencyPrefix(operation) && index + 1 < operations.Count
                    && !operations[index + 1].Parameters.ContainsKey("triggerIndex")
                    && CardEffectRules.IsLegalDependencyPayoff(operation, operations[index + 1]))
                {
                    lines.Add(new RenderedLine(RenderEffects([operation, operations[++index]]),
                        MustRenderLast(operation) || MustRenderLast(operations[index])));
                    continue;
                }
                lines.Add(new RenderedLine(CardTextStyle.Chinese(operation), MustRenderLast(operation)));
            }
        }

        return string.Join("\n", lines.OrderBy(line => line.Last ? 1 : 0).Select(line => line.Text));
    }
}

#if AUTHORING_CATALOGS
public static class GeneratorSelfTest
{
    public static void Run()
    {
        // Cheap static catalog audits run before the randomized stress loops so decomposition regressions fail fast.
        EffectBalanceModel.Validate();
        SlyKeywordTuning.Validate();
        SlyPoolConstraintResolver.Validate();
        CardKeywordTuning.Validate();
        var regentStarAtom = CharacterComponentCatalogs.Get(GeneratedCharacter.Regent).Atoms
            .First(atom => atom.Template == "R:GainStars");
        var twoStarSpec = OperationRuntimeSpecCompiler.GetOrCompile(regentStarAtom) with
        {
            Values = OperationRuntimeSpecCompiler.GetOrCompile(regentStarAtom).Values
                .Select(value => value.Id == "stars" ? value with { BaseValue = 2 } : value).ToArray()
        };
        var twoStars = new GeneratorOperation(regentStarAtom.Template, regentStarAtom.Scope,
            string.Empty, new Dictionary<string, int>(), RuntimeSpec: twoStarSpec,
            LocalizedText: regentStarAtom.LocalizedText);
        if (EffectBalanceModel.EstimatedEffectValue(twoStars) != 2 * EffectBalanceModel.StarValuePerPoint)
            throw new InvalidOperationException("蓝星收益没有使用统一的略降价值换算。 ");
        var necrobinderAtoms = CharacterComponentCatalogs.Get(GeneratedCharacter.Necrobinder).Atoms;
        var necrobinderBlock = necrobinderAtoms.First(atom => atom.Template == "N:B");
        var necrobinderSummon = necrobinderAtoms.First(atom => atom.Template == "NCR:Summon");
        var necrobinderDamage = necrobinderAtoms.First(CardEffectRules.IsEnemyDamage);
        if (EffectSelectionTuning.NecrobinderBlockAndSummonWeight([necrobinderBlock],
                GeneratedCharacter.Necrobinder, ultimateChaos: false)
                != EffectSelectionTuning.NecrobinderBlockAndSummonWeightPercent
            || EffectSelectionTuning.NecrobinderBlockAndSummonWeight([necrobinderSummon],
                GeneratedCharacter.Necrobinder, ultimateChaos: false)
                != EffectSelectionTuning.NecrobinderBlockAndSummonWeightPercent
            || EffectSelectionTuning.NecrobinderBlockAndSummonWeight([necrobinderDamage],
                GeneratedCharacter.Necrobinder, ultimateChaos: false) != 100
            || EffectSelectionTuning.NecrobinderBlockAndSummonWeight([necrobinderBlock],
                GeneratedCharacter.Necrobinder, ultimateChaos: true) != 100
            || EffectSelectionTuning.NecrobinderBlockAndSummonWeight([necrobinderBlock],
                GeneratedCharacter.Ironclad, ultimateChaos: false) != 100)
            throw new InvalidOperationException("死灵契约师的格挡/召唤共享降权越过了角色或究极混沌边界。 ");
        var packageSourceCatalog = CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad);
        var packageCatalog = new ImmutableComponentCatalog(GeneratedCharacter.Ironclad,
            packageSourceCatalog.Recipes);
        var packageRequest = new ComponentProfileRequest("selftest:ironclad", GeneratedCharacter.Ironclad,
            unlockComponentRoles: false);
        var packageProfile = new ComponentGenerationProfile("selftest:ironclad:normal",
            GeneratedCharacter.Ironclad, false, packageCatalog, packageCatalog, packageCatalog,
            () => ComponentApi.CreateNativeOccurrencePolicy(packageCatalog), ComponentApi.DefaultValuePolicy);
        var forwardOwnerSource = packageSourceCatalog.Recipes.First(recipe => recipe.Atoms.Count >= 2);
        var forwardOwners = forwardOwnerSource.TriggerOwners.ToArray();
        forwardOwners[0] = 1;
        var forwardOwnerCatalog = new ImmutableComponentCatalog(GeneratedCharacter.Ironclad,
            [forwardOwnerSource with { TriggerOwners = forwardOwners }]);
        ExpectInvalidProfile(new ComponentGenerationProfile("selftest:forward_trigger",
            GeneratedCharacter.Ironclad, false, forwardOwnerCatalog, packageCatalog, packageCatalog,
            () => ComponentApi.CreateNativeOccurrencePolicy(packageCatalog), ComponentApi.DefaultValuePolicy),
            "invalid trigger owner");
        var unstructuredSource = packageSourceCatalog.Recipes.First();
        var unstructuredAtoms = unstructuredSource.Atoms.ToArray();
        unstructuredAtoms[0] = unstructuredAtoms[0] with { RuntimeSpec = null };
        var unstructuredCatalog = new ImmutableComponentCatalog(GeneratedCharacter.Ironclad,
            [unstructuredSource with { Atoms = unstructuredAtoms }]);
        ExpectInvalidProfile(new ComponentGenerationProfile("selftest:unstructured_shell",
            GeneratedCharacter.Ironclad, false, unstructuredCatalog, packageCatalog, packageCatalog,
            () => ComponentApi.CreateNativeOccurrencePolicy(packageCatalog), ComponentApi.DefaultValuePolicy),
            "unstructured shell component");
        var unknownKeywordCatalog = new ImmutableComponentCatalog(GeneratedCharacter.Ironclad,
            [unstructuredSource with { CustomKeywords = ["selftest:missing_keyword"] }]);
        ExpectInvalidProfile(new ComponentGenerationProfile("selftest:unknown_keyword",
            GeneratedCharacter.Ironclad, false, unknownKeywordCatalog, unknownKeywordCatalog,
            unknownKeywordCatalog, () => ComponentApi.CreateNativeOccurrencePolicy(unknownKeywordCatalog),
            ComponentApi.DefaultValuePolicy), "unregistered custom keyword");
        var packageLocalizationAtom = packageCatalog.Atoms.First(atom => atom.LocalizedText?.EnglishTemplate is not null);
        ComponentPackageApi.Register(new ComponentPackageRegistration("selftest:package", packageRequest,
            packageProfile, Valuations:
            [new ComponentValuationRegistration("api_test", "fixed", new SelfTestComponentValuation(),
                NegativeLinearValuePerUnit: 250d)],
            KeywordUpgrades:
            [new ComponentKeywordUpgrade("API:Test", [], [], ["selftest:charged"], [])],
            Keywords: [new ComponentKeywordDefinition("selftest:charged")],
            Localizations:
            [new ComponentLocalizationRegistration(packageLocalizationAtom.SemanticId!,
                packageLocalizationAtom.LocalizedText!.ChineseTemplate,
                packageLocalizationAtom.LocalizedText.EnglishTemplate!)]));
        if (!ReferenceEquals(ComponentApi.Resolve(packageRequest), packageProfile)
            || !ComponentApi.RegisteredProfiles.Contains(packageRequest)
            || !ComponentPackageApi.RegisteredPackages.Contains("selftest:package", StringComparer.Ordinal)
            || !ComponentKeywordApi.IsRegistered("selftest:charged")
            || !ComponentLocalizationApi.TryGet(packageLocalizationAtom.SemanticId,
                out var registeredLocalization)
            || registeredLocalization != packageLocalizationAtom.LocalizedText
            || !ExternalCustomKeywordUpgradeRegistry.TryGet(packageRequest.ProfileId, "API:Test",
                out var registeredCustomUpgrade)
            || !registeredCustomUpgrade.Added.Contains("selftest:charged", StringComparer.Ordinal))
            throw new InvalidOperationException("外部组件包的一次性注册、解析或不可变目录索引失败。");
        var customKeywordPlan = new CardUpgradePlan(1,
            [new CardUpgradeEffect(CardUpgradeKind.AddCustomKeyword, KeywordId: "selftest:charged")],
            "测试。", [], "Test.", AddedCustomKeywords: ["selftest:charged"]);
        var customKeywordCard = new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Common, "测试。", [], [], Upgrade: customKeywordPlan,
            EnglishDescription: "Test.", CustomKeywords: ["selftest:charged"]);
        var customKeywordRoundTrip = System.Text.Json.JsonSerializer.Deserialize<GeneratedCard>(
            System.Text.Json.JsonSerializer.Serialize(customKeywordCard));
        if (customKeywordRoundTrip?.CustomKeywords?.Single() != "selftest:charged"
            || customKeywordRoundTrip.Upgrade?.AddedCustomKeywords?.Single() != "selftest:charged"
            || customKeywordRoundTrip.Upgrade.Effects.Single().KeywordId != "selftest:charged")
            throw new InvalidOperationException("自定义关键词ID没有稳定通过卡牌/升级快照往返。");
        if (ComponentApi.ApiVersion != 3 || ComponentPackageApi.ApiVersion != 3
            || !GeneratedCardTagPolicy.AddedKeywords(new CardUpgradePlan(1,
                    [new CardUpgradeEffect(CardUpgradeKind.GrantInnate)], "测试。", [], "Test."))
                .Contains(CardTag.Innate)
            || !GeneratedCardTagPolicy.RemovedCustomKeywords(new CardUpgradePlan(1,
                    [new CardUpgradeEffect(CardUpgradeKind.RemoveCustomKeyword,
                        KeywordId: "selftest:charged")], "测试。", [], "Test."))
                .Contains("selftest:charged"))
            throw new InvalidOperationException("API v3关键词升级迁移投影不完整。");

        // Emergency generation must consume the active package's own structured component, never an Ironclad
        // Strike/Defend/Inflame fallback hidden in the shared assembler.
        var sourceFallbackAtom = packageSourceCatalog.Recipes
            .Single(recipe => recipe.Id == "StrikeIronclad").Atoms.Single();
        var apiFallbackAtom = sourceFallbackAtom with
        {
            Template = "API:FallbackDamage",
            SemanticId = "selftest/fallback_damage",
            RuntimeSpec = sourceFallbackAtom.RuntimeSpec! with
            {
                Flags = sourceFallbackAtom.RuntimeSpec.Flags
                    .Append(ComponentSemanticFlags.EnemyDamage).ToArray()
            }
        };
        var apiFallbackRecipe = new IroncladCardRecipe("ApiFallback", "接口打击", 1,
            GeneratedCardType.Attack, TargetMode.SingleEnemy, GeneratedRarity.Common, [],
            [apiFallbackAtom], [-1], EnglishTitle: "API Strike");
        var apiFallbackCatalog = new ImmutableComponentCatalog(GeneratedCharacter.Ironclad,
            [apiFallbackRecipe]);
        var apiFallbackProfile = new ComponentGenerationProfile("selftest:fallback",
            GeneratedCharacter.Ironclad, false, apiFallbackCatalog, apiFallbackCatalog,
            apiFallbackCatalog, () => ComponentApi.CreateNativeOccurrencePolicy(apiFallbackCatalog),
            ComponentApi.DefaultValuePolicy);
        ComponentProfileValidator.Validate(apiFallbackProfile);
        var apiFallbackCard = new ComponentAssemblyGenerator(new Random(9341),
            GeneratedCharacter.Ironclad, profile: apiFallbackProfile,
            profileRegistrationId: "selftest:fallback").GenerateEmergencyFallback(
            GeneratedRarity.Common, apiFallbackRecipe);
        if (apiFallbackCard.Operations.Single().Template != "API:FallbackDamage")
            throw new InvalidOperationException("外部角色紧急生成错误地使用了内置角色组件。");
        var valuationSpec = new OperationRuntimeSpec(OperationRuntimeSpec.CurrentSchemaVersion,
            "api_test", "fixed", "self", "none", "none", "any", [],
            [new RuntimeValueSlot("amount", 2)]);
        var valuationProbe = new GeneratorOperation("API:Test", OperationScope.NonTargeted,
            "测试2。", new Dictionary<string, int>(), RuntimeSpec: valuationSpec);
        if (EffectBalanceModel.EstimatedEffectValue(valuationProbe) != 777
            || !CardEffectRules.IsNegativeEffect(valuationProbe)
            || NegativeEffectTuning.LinearCompensationValue(valuationProbe) != 500d)
            throw new InvalidOperationException("外部组件价值与线性负面补偿没有进入统一预算管线。");
        var nativeProfile = ComponentApi.Resolve(new ComponentProfileRequest(
            GeneratedCharacter.Ironclad, UnlockComponentRoles: false));
        var ultimateProfile = ComponentApi.Resolve(new ComponentProfileRequest(
            GeneratedCharacter.Ironclad, UnlockComponentRoles: true));
        var builtInUltimateRecipeCount = CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad,
            unlockComponentRoles: true).Recipes.Count;
        if (nativeProfile.ShellCatalog != nativeProfile.ComponentCatalog
            || nativeProfile.NameCatalog.Character != GeneratedCharacter.Ironclad
            || ultimateProfile.ComponentCatalog.AtomKeys.Count <= nativeProfile.ComponentCatalog.AtomKeys.Count
            || ultimateProfile.ComponentCatalog.Recipes.Count
                != builtInUltimateRecipeCount + packageCatalog.Recipes.Count
            || ultimateProfile.NameCatalog != nativeProfile.NameCatalog
            || ReferenceEquals(ultimateProfile.CreateOccurrencePolicy(), ultimateProfile.CreateOccurrencePolicy()))
            throw new InvalidOperationException("组件 API 未保持原生/究极混沌 Profile 边界或池级出率状态隔离。");
        var wraithRecipe = CharacterComponentCatalogs.Get(GeneratedCharacter.Silent).Recipes
            .Single(recipe => recipe.Id == "WraithForm");
        var wraithOperations = wraithRecipe.Atoms.Select((atom, index) => new GeneratorOperation(
            atom.Template, atom.Scope, "test.",
            wraithRecipe.TriggerOwners[index] >= 0
                ? new Dictionary<string, int> { ["triggerIndex"] = wraithRecipe.TriggerOwners[index] }
                : new Dictionary<string, int>(),
            RequiresSingleTarget: atom.RequiresSingleTarget,
            RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom))).ToArray();
        var wraithNormalized = (EffectBalanceModel.EstimatedPositiveCardValue(wraithOperations)
                                - CardEffectRules.NegativeEffectLinearCompensationValue(wraithOperations))
                               / ComponentAssemblyGenerator.PowerOneShotBudgetFactor(
                                   wraithOperations, GeneratedCardType.Power);
        if (Math.Abs(wraithNormalized - 8_160d) > 0.001d)
            throw new InvalidOperationException($"幽魂形态联合估值应为8160，实际为{wraithNormalized:0.###}。");
        var componentApiDamage = nativeProfile.ComponentCatalog.Atoms.First(CardEffectRules.IsEnemyDamage);
        if (nativeProfile.ValuePolicy.IsScalableReward(componentApiDamage)
                != EffectBalanceModel.IsScalableReward(componentApiDamage)
            || nativeProfile.ValuePolicy.OriginalValueChance(componentApiDamage, 1)
                != NumericGenerationTuning.OriginalValueChance(componentApiDamage, 1))
            throw new InvalidOperationException("内置组件数值策略适配器偏离现有平衡模型。");
        if (ComponentAssemblyGenerator.AdjustStrikeTagNumerator(10, GeneratedCharacter.Ironclad, false) != 15
            || ComponentAssemblyGenerator.AdjustStrikeTagNumerator(10, GeneratedCharacter.Silent, false) != 10
            || ComponentAssemblyGenerator.AdjustStrikeTagNumerator(10, GeneratedCharacter.Ironclad, true) != 10)
            throw new InvalidOperationException("战士打击标签倍率只能作用于非究极混沌的战士卡池。");
        var ostyDamageProbe = new GeneratorOperation("NCR:OstyDamage", OperationScope.SingleEnemyOnly,
            "奥斯提造成6点伤害。", new Dictionary<string, int>(), RequiresSingleTarget: true);
        var summonProbe = new GeneratorOperation("NCR:Summon", OperationScope.NonTargeted,
            "召唤3。", new Dictionary<string, int>());
        var triggeredSummonProbe = summonProbe with
        {
            Parameters = new Dictionary<string, int> { ["triggerIndex"] = 0 }
        };
        var sameTriggerDamageProbe = ostyDamageProbe with
        {
            Parameters = new Dictionary<string, int> { ["triggerIndex"] = 0 }
        };
        var ostyHistoryProbe = new GeneratorOperation("NCR:ForEachOstyAttackThisTurn",
            OperationScope.ConditionalTrigger, "本回合每打出一张奥斯提攻击牌。", new Dictionary<string, int>());
        var calcifyProbe = new GeneratorOperation("A:ProxyAtomic_Calcify", OperationScope.AbilityRule,
            "奥斯提的攻击额外造成4点伤害。", new Dictionary<string, int>());
        if (!CardEffectRules.RequiresLivingOstyForCurrentPlay([ostyDamageProbe])
            || CardEffectRules.RequiresLivingOstyForCurrentPlay([summonProbe, ostyDamageProbe])
            || !CardEffectRules.RequiresLivingOstyForCurrentPlay([ostyDamageProbe, summonProbe])
            || !CardEffectRules.RequiresLivingOstyForCurrentPlay([triggeredSummonProbe, ostyDamageProbe])
            || CardEffectRules.RequiresLivingOstyForCurrentPlay([triggeredSummonProbe, sameTriggerDamageProbe])
            || CardEffectRules.RequiresLivingOstyForCurrentPlay([ostyHistoryProbe])
            || CardEffectRules.RequiresLivingOstyForCurrentPlay([calcifyProbe]))
            throw new InvalidOperationException("奥斯提缺失红色高亮没有遵循原版即时效果与先召唤后使用规则。");
        var unreviewedOstyOperations = Enum.GetValues<GeneratedCharacter>()
            .SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms)
            .Select(atom => new GeneratorOperation(atom.Template, atom.Scope, string.Empty,
                new Dictionary<string, int>()))
            .Where(operation => operation.Template.Contains("Osty", StringComparison.Ordinal)
                || operation.Template is "A:ProxyAtomic_Calcify" or "T:ProxyDamage_Atomic_Poke"
                    or "NCR:ApplyPower_SicEmPower")
            .Where(operation => !CardEffectRules.IsReviewedOstyLifecycleOperation(operation))
            .Select(operation => operation.Template)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(template => template, StringComparer.Ordinal)
            .ToArray();
        if (unreviewedOstyOperations.Length > 0)
            throw new InvalidOperationException("存在尚未审查存活要求的奥斯提组件："
                                                + string.Join(", ", unreviewedOstyOperations));
        // Ground the generalized operation rule in the actual v111 card implementations. These are every
        // Necrobinder pool card whose vanilla model overrides ShouldGlowRedInternal with Owner.IsOstyMissing.
        // Sweeping Gaze has the same override but is a derivative CardModel rather than a pool recipe, so it keeps
        // its native implementation and is intentionally absent from this catalog comparison.
        var expectedNativeOstyRedGlow = new HashSet<string>(StringComparer.Ordinal)
        {
            "BoneShards", "Fetch", "Flatten", "HighFive", "Poke", "Protector", "Rattle",
            "RightHandHand", "Sacrifice", "SicEm", "Snap", "Squeeze", "Unleash"
        };
        var actualNativeOstyRedGlow = CharacterComponentCatalogs.Get(GeneratedCharacter.Necrobinder).Recipes
            .Where(recipe => CardEffectRules.RequiresLivingOstyForCurrentPlay(recipe.Atoms.Select(atom =>
                new GeneratorOperation(atom.Template, atom.Scope, string.Empty,
                    new Dictionary<string, int>())).ToArray()))
            .Select(recipe => recipe.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (!actualNativeOstyRedGlow.SetEquals(expectedNativeOstyRedGlow))
            throw new InvalidOperationException("奥斯提红色高亮分类与原版v111卡牌不一致。缺少："
                                                + string.Join(", ", expectedNativeOstyRedGlow.Except(actualNativeOstyRedGlow))
                                                + "；误报："
                                                + string.Join(", ", actualNativeOstyRedGlow.Except(expectedNativeOstyRedGlow)));
        var densityProbe = new ComponentAssemblyGenerator(new Random(2026083001), GeneratedCharacter.Ironclad);
        if (densityProbe.AdaptiveEffectCountWindow(0) != (1, 5)
            || densityProbe.AdaptiveEffectCountWindow(3) != (2, 5)
            || densityProbe.AdaptiveEffectCountWindow(12) != (5, 5)
            || densityProbe.AdaptiveEffectCountWindow(15) != (6, 6))
            throw new InvalidOperationException("重复效果碰撞后的动态词条数窗口不符合递增规则。");
        var templateIdentityA = new GeneratedCard(1, GeneratedCardType.Attack, TargetMode.SingleEnemy,
            GeneratedRarity.Common, "造成9点伤害。", Array.Empty<CardTag>(),
            [new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly, "造成9点伤害。",
                new Dictionary<string, int>(), RequiresSingleTarget: true)],
            Character: GeneratedCharacter.Ironclad);
        var templateIdentityB = templateIdentityA with
        {
            Operations = [new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly, "造成12点伤害。",
                new Dictionary<string, int>(), RequiresSingleTarget: true)]
        };
        if (GeneratedCardEffectIdentity.Signature(templateIdentityA)
                == GeneratedCardEffectIdentity.Signature(templateIdentityB)
            || GeneratedCardEffectIdentity.TemplateSignature(templateIdentityA)
                != GeneratedCardEffectIdentity.TemplateSignature(templateIdentityB)
            || GeneratedCardEffectIdentity.TemplateSignature(templateIdentityA)
                == GeneratedCardEffectIdentity.TemplateSignature(templateIdentityB with { Cost = 2 })
            || GeneratedCardEffectIdentity.TemplateSignature(templateIdentityA)
                == GeneratedCardEffectIdentity.TemplateSignature(templateIdentityB with
                {
                    Character = GeneratedCharacter.Silent
                }))
            throw new InvalidOperationException("同色同费用同类型的整卡模板去重未正确忽略数值或错误跨费用/颜色互斥。 ");
        NativeUpgradeValueModel.Validate();
        NativeUpgradeStrengthModel.Validate();
        OperationRuntimeSpecCompiler.ValidateBatchOneCoverage();
        OperationRuntimeSpecCompiler.ValidateNumericBatchCoverage();
        OperationRuntimeSpecCompiler.ValidateLegacyDynamicProjection();
        OperationRuntimeSpecCompiler.ValidateTriggerCoverage();
        OperationRuntimeSpecCompiler.ValidateFullCatalogCoverage();
        CatalogRuntimeSpecRegistry.ValidateCoverage();
#if AUTHORING_CATALOGS
        CatalogRuntimeSpecRegistry.ValidateAuthoringProjection();
#endif
        OperationRuntimeSpecCompiler.ValidateLocalizationIndependence();
        OperationRuntimeSpecCompiler.ValidateSemanticProjectionEquivalence();
        foreach (var character in Enum.GetValues<GeneratedCharacter>())
        {
            foreach (var atom in CharacterComponentCatalogs.Get(character).Atoms)
            {
                var operation = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
                    new Dictionary<string, int>());
                if (atom.Template == "D:GainTemporaryFocus"
                    && (!atom.ChineseText.Contains("本回合", StringComparison.Ordinal)
                        || !CardTextStyle.Chinese(operation).Contains("本回合", StringComparison.Ordinal)))
                    throw new InvalidOperationException("临时集中组件必须在自身描述中明确标注“本回合”。");
                if (atom.Template == "D:GainFocus"
                    && atom.ChineseText.Contains("本回合", StringComparison.Ordinal))
                    throw new InvalidOperationException("永久集中组件不得被标记为本回合效果。");
            }
        }
        var currentBlockDamageProbe = new GeneratorOperation("M:value", OperationScope.Modifier,
            "这张牌造成等同于当前格挡的伤害。", new Dictionary<string, int>());
        var oldCurrentBlockAssembly = new[]
        {
            new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly, "造成6点伤害。",
                new Dictionary<string, int>(), RequiresSingleTarget: true),
            currentBlockDamageProbe
        };
        var currentBlockChinese = CardDescriptionRenderer.Render(oldCurrentBlockAssembly);
        var currentBlockEnglish = EnglishCardDescriptionRenderer.Render(oldCurrentBlockAssembly);
        if (!CardEffectRules.HasValidCurrentBlockDamageAssembly(oldCurrentBlockAssembly)
            || CardEffectRules.CurrentBlockDamageAnchorIndex(oldCurrentBlockAssembly, 1) != 0
            || currentBlockChinese != "造成你当前格挡值的伤害。"
            || currentBlockEnglish != "This card deals damage equal to your current Block.")
            throw new InvalidOperationException("当前格挡伤害仍会把内部替换锚点显示为独立伤害："
                                                + currentBlockChinese + " | " + currentBlockEnglish);
        var multipleDamageCurrentBlockAssembly = new[]
        {
            oldCurrentBlockAssembly[0],
            oldCurrentBlockAssembly[0] with { ChineseText = "造成7点伤害。" },
            currentBlockDamageProbe
        };
        var multipleDamageDescription = CardDescriptionRenderer.Render(multipleDamageCurrentBlockAssembly);
        if (CardEffectRules.CurrentBlockDamageAnchorIndex(multipleDamageCurrentBlockAssembly, 2) != 1
            || !multipleDamageDescription.Contains("造成6点伤害。", StringComparison.Ordinal)
            || multipleDamageDescription.Contains("造成7点伤害。", StringComparison.Ordinal)
            || !multipleDamageDescription.Contains("造成你当前格挡值的伤害。", StringComparison.Ordinal)
            || CardEffectRules.HasValidCurrentBlockDamageAssembly([currentBlockDamageProbe]))
            throw new InvalidOperationException("当前格挡伤害没有只绑定最近的同触发单体伤害锚点。 ");
        var staleCurrentBlockUpgrade = CardUpgradeGenerator.ApplyEffectsToOperations(oldCurrentBlockAssembly,
            [new CardUpgradeEffect(CardUpgradeKind.IncreaseNumber, 0, 3)]);
        if (staleCurrentBlockUpgrade[0].ChineseText != oldCurrentBlockAssembly[0].ChineseText)
            throw new InvalidOperationException("旧快照仍能升级当前格挡伤害的隐藏数值锚点。 ");
        var lowZeroCostConditional = new List<GeneratorOperation>
        {
            new("C:untilTurnEnd", OperationScope.ConditionalTrigger,
                "当你打出下一张攻击牌时。", new Dictionary<string, int>()),
            new("N:B", OperationScope.NonTargeted, "获得5点格挡。",
                new Dictionary<string, int> { ["triggerIndex"] = 0 })
        };
        if (!ComponentAssemblyGenerator.RaiseNumericRewardsToTopRarityFloor(lowZeroCostConditional,
                GeneratedRarity.Rare, 0)
            || !EffectBalanceModel.HasAdequateTopRarityCardValue(lowZeroCostConditional,
                GeneratedRarity.Rare, 0)
            || lowZeroCostConditional[0].Template != "C:untilTurnEnd"
            || OperationRuntimeSpecCompiler.StaticLiteralValue(lowZeroCostConditional[1], "block") <= 5)
            throw new InvalidOperationException("0费高稀有度牌没有在保留条件组件的同时用数值补足整卡门槛。");
        var lowZeroCostBlock = new List<GeneratorOperation>
        {
            new("N:B", OperationScope.NonTargeted, "获得6点格挡。", new Dictionary<string, int>())
        };
        if (!ComponentAssemblyGenerator.RaiseNumericRewardsToTopRarityFloor(lowZeroCostBlock,
                GeneratedRarity.Rare, 0, GeneratedCharacter.Defect)
            || !EffectBalanceModel.HasAdequateTopRarityCardValue(lowZeroCostBlock,
                GeneratedRarity.Rare, 0)
            || OperationRuntimeSpecCompiler.StaticLiteralValue(lowZeroCostBlock[0], "block") < 8)
            throw new InvalidOperationException("0费稀有单格挡牌没有达到高稀有度整卡下限。");
        var frequentPermanentStrength = new List<GeneratorOperation>
        {
            new("A:whenStarsChanged", OperationScope.AbilityTrigger, "每当你花费或获得蓝星时。",
                new Dictionary<string, int>()),
            new("R:GainStrength", OperationScope.NonTargeted, "获得1点力量。",
                new Dictionary<string, int> { ["triggerIndex"] = 0 })
        };
        if (EffectBalanceModel.EstimatedPositiveCardValue(frequentPermanentStrength) < 5_000d
            || ComponentAssemblyGenerator.ApplyWholeCardBudgetEnvelope(frequentPermanentStrength,
                GeneratedRarity.Uncommon, 1d, GeneratedCardType.Power, Array.Empty<CardTag>(), true,
                balancedValues: true, character: GeneratedCharacter.Regent, ultimateChaos: false))
            throw new InvalidOperationException("高频能力触发的永久属性仍能作为不可缩放效果绕过整卡预算。");
        var ultimateCatalog = CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad, true);
        var fallbackAssembler = new ComponentAssemblyGenerator(new Random(2026082301),
            GeneratedCharacter.Ironclad, unlockComponentRoles: true);
        foreach (var shell in ultimateCatalog.Recipes.DistinctBy(recipe => (recipe.Type, recipe.Target)))
        {
            CardTemplateValidator.Validate(fallbackAssembler.GenerateEmergencyFallback(
                GeneratedRarity.Uncommon, shell));
            var rareFallback = fallbackAssembler.GenerateEmergencyFallback(GeneratedRarity.Rare, shell);
            CardTemplateValidator.Validate(rareFallback);
            if (!EffectBalanceModel.HasAdequateTopRarityCardValue(rareFallback.Operations, rareFallback.Rarity,
                    rareFallback.Cost + CardEffectRules.StarCostEnergyEquivalent(rareFallback.StarCost)))
                throw new InvalidOperationException("高稀有度紧急回退牌绕过了成品最低价值线："
                                                    + rareFallback.ChineseDescription);
        }
        var forcedFallbackAssembler = new ComponentAssemblyGenerator(new Random(2026082302),
            GeneratedCharacter.Ironclad, unlockComponentRoles: true,
            specialXMode: SpecialXGenerationMode.Forced);
        var forcedFallback = forcedFallbackAssembler.GenerateEmergencyFallback(GeneratedRarity.Rare,
            ultimateCatalog.Recipes.First(recipe => recipe.Type == GeneratedCardType.Skill));
        if (!SpecialXCardConverter.IsSpecial(forcedFallback))
            throw new InvalidOperationException("终极混乱的有界兜底流程未生成所需的特殊X费牌。 ");
        var shivCountRandom = new Random(20260823);
        var shivCounts = Enumerable.Range(0, 20_000)
            .Select(index => NumericGenerationTuning.SampleShivProducerCount(shivCountRandom,
                index % 2 == 0 ? "N:CreateShiv" : "N:CreateInkShiv", 7)).ToArray();
        if (shivCounts.Where((_, index) => index % 2 == 0).Any(value => value is < 3 or > 6)
            || shivCounts.Where((_, index) => index % 2 == 1).Any(value => value is < 2 or > 5))
            throw new InvalidOperationException("小刀系衍生物数量专用采样越界。");
        var shiv = DerivativeSlotCatalog.All.Single(definition => definition.Id == "shiv");
        var gazeValueProbe = DerivativeSlotCatalog.All.Single(definition => definition.Id == "gaze");
        var swordValueProbe = DerivativeSlotCatalog.All.Single(definition => definition.Id == "sword");
        if (DerivativeSlotCatalog.AdjustFixedProducerCount("N:CreateShiv", "将3张小刀加入手牌。", gazeValueProbe)
                != "将2张小刀加入手牌。"
            || DerivativeSlotCatalog.AdjustFixedProducerCount("N:CreateInkShiv", "将2张墨影小刀加入手牌。", gazeValueProbe)
                != "将2张墨影小刀加入手牌。"
            || DerivativeSlotCatalog.AdjustFixedProducerCount("NCR:AddSweepingGazeToHand", "将1张扫荡凝视加入手牌。", shiv)
                != "将2张扫荡凝视加入手牌。"
            || DerivativeSlotCatalog.AdjustFixedProducerCount("N:CreateShiv", "将4张小刀加入手牌。",
                DerivativeSlotCatalog.All.Single(definition => definition.Id == "fuel"), ancientFuelActive: false)
                != "将3张小刀加入手牌。"
            || DerivativeSlotCatalog.AdjustFixedProducerCount("N:CreateShiv", "将4张小刀加入手牌。",
                DerivativeSlotCatalog.All.Single(definition => definition.Id == "fuel"), ancientFuelActive: true)
                != "将2张小刀加入手牌。"
            || DerivativeSlotCatalog.AdjustFixedProducerCount("I:ProxyAtomic_Charge",
                "选择你抽牌堆中的2张牌，将其变化为仆从俯冲。", shiv)
                != "选择你抽牌堆中的4张牌，将其变化为仆从俯冲。"
            || DerivativeSlotCatalog.AdjustFixedProducerCount("I:ProxyAtomic_Seance",
                "将你抽牌堆中的一张牌变化为灵魂。", shiv)
                != "将你抽牌堆中的2张牌变化为灵魂。"
            || DerivativeSlotCatalog.AdjustFixedProducerCount("D:TransformStatusesToFuel",
                "将手牌中的所有状态牌变化为燃料。", shiv)
                != "将手牌中的所有状态牌变化为燃料。"
            || DerivativeSlotCatalog.AdjustFixedProducerCount("NCR:CreateSoulInHand", "将10张灵魂加入手牌。", swordValueProbe)
                != "将2张灵魂加入手牌。")
            throw new InvalidOperationException("衍生物固定生成数量的相对价值换算失败。");
        // This seed previously found a low-acceptance Necrobinder shell and exhausted the process stack because
        // every rejected assembly recursed. Keep it as a deterministic regression probe for iterative retries.
        var retryProbe = new RandomCardGenerator(GeneratedCharacter.Necrobinder, 28965);
        for (var index = 0; index < 75; index++)
            CardTemplateValidator.Validate(retryProbe.Generate());
        CardNameGenerator.ValidateCompositionRules();
        CardNameGenerator.ValidateCatalog(CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad));
        CardNameGenerator.ValidateCatalog(CharacterComponentCatalogs.Get(GeneratedCharacter.Silent));
        var numericSource = CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad).Recipes
            .SelectMany(recipe => recipe.Atoms.Select(atom => (Recipe: recipe, Atom: atom)))
            .First(item => System.Text.RegularExpressions.Regex.IsMatch(item.Atom.ChineseText, @"\d"));
        var randomizedNumeric = new GeneratorOperation(numericSource.Atom.Template, numericSource.Atom.Scope,
            System.Text.RegularExpressions.Regex.Replace(numericSource.Atom.ChineseText, @"\d+", "997"),
            new Dictionary<string, int>());
        if (!CardNameGenerator.IsRelatedToOperations(numericSource.Recipe, [randomizedNumeric]))
            throw new InvalidOperationException("随机数值破坏了卡名与卡图的原始组件来源识别。");
        var derivativeSource = Enum.GetValues<GeneratedCharacter>()
            .SelectMany(character => CharacterComponentCatalogs.Get(character).Recipes)
            .SelectMany(recipe => recipe.Atoms.Select(atom => (Recipe: recipe, Atom: atom)))
            .First(item => DerivativeSlotCatalog.IsSlotOperation(item.Atom.Template));
        var randomizedDerivative = new GeneratorOperation(derivativeSource.Atom.Template,
            derivativeSource.Atom.Scope, "衍生物槽已被随机替换。", new Dictionary<string, int>(),
            DerivativeId: "portrait-audit-probe");
        if (!CardNameGenerator.IsRelatedToOperations(derivativeSource.Recipe, [randomizedDerivative]))
            throw new InvalidOperationException("衍生物替换破坏了卡名与卡图的原始组件来源识别。");
        foreach (var derivative in DerivativeSlotCatalog.All)
        foreach (var enchantment in DerivativeEnchantmentCatalog.All)
        {
            var compatible = DerivativeEnchantmentCatalog.CanUse(derivative, enchantment);
            if (compatible && !derivative.CanBeEnchanted)
                throw new InvalidOperationException($"不可附魔的衍生牌 {derivative.Id} 接受了 {enchantment.Id}。");
            if (compatible && enchantment.Requirement == DerivativeEnchantmentRequirement.Attack
                && !derivative.IsAttack)
                throw new InvalidOperationException($"非攻击衍生牌 {derivative.Id} 接受了攻击附魔 {enchantment.Id}。");
            if (compatible && enchantment.Requirement == DerivativeEnchantmentRequirement.GainsBlock
                && !derivative.GainsBlock)
                throw new InvalidOperationException($"不获得格挡的衍生牌 {derivative.Id} 接受了灵巧附魔。");
            if (compatible && enchantment.Requirement == DerivativeEnchantmentRequirement.Exhaust
                && !derivative.HasExhaust)
                throw new InvalidOperationException($"不消耗的衍生牌 {derivative.Id} 接受了灵魂之力附魔。");
        }
        var shivDefinition = DerivativeSlotCatalog.All.Single(definition => definition.Id == "shiv");
        var gazeDefinition = DerivativeSlotCatalog.All.Single(definition => definition.Id == "gaze");
        var inkyDefinition = DerivativeEnchantmentCatalog.Resolve("inky")!;
        var slitherDefinition = DerivativeEnchantmentCatalog.Resolve("slither")!;
        var steadyDefinition = DerivativeEnchantmentCatalog.Resolve("steady")!;
        var rockDefinition = DerivativeSlotCatalog.All.Single(definition => definition.Id == "rock");
        var swordDefinition = DerivativeSlotCatalog.All.Single(definition => definition.Id == "sword");
        if (!DerivativeEnchantmentCatalog.CanUse(shivDefinition, inkyDefinition)
            || DerivativeEnchantmentCatalog.CanUse(gazeDefinition, inkyDefinition))
            throw new InvalidOperationException("墨影附魔必须要求衍生攻击具有可解析的敌人目标。");
        if (DerivativeEnchantmentCatalog.CanUse(swordDefinition, steadyDefinition)
            || DerivativeEnchantmentCatalog.CanUse(shivDefinition, slitherDefinition)
            || !DerivativeEnchantmentCatalog.CanUse(rockDefinition, slitherDefinition)
            || !DerivativeEnchantmentCatalog.CanUse(swordDefinition, slitherDefinition))
            throw new InvalidOperationException("君王之剑/稳定或蛇行/衍生牌费用的附魔约束失效。");
        var necrobinderDerivativeRandom = new Random(20260905);
        var normalNecrobinderDerivatives = Enumerable.Range(0, 100_000)
            .Select(_ => DerivativeSlotCatalog.Roll(necrobinderDerivativeRandom,
                GeneratedCharacter.Necrobinder, false, "NCR:CreateSoulInHand").Id).ToArray();
        var soulRate = normalNecrobinderDerivatives.Count(id => id == "soul")
            / (double)normalNecrobinderDerivatives.Length;
        if (Math.Abs(soulRate - 0.75d) > 0.01d
            || DerivativeSlotCatalog.SelectionWeight(shivDefinition, GeneratedCharacter.Silent, false) != 100
            || DerivativeSlotCatalog.SelectionWeight(gazeDefinition, GeneratedCharacter.Necrobinder, true) != 100)
            throw new InvalidOperationException("死灵契约师普通模式的灵魂/扫荡凝视槽位权重失效。 ");
        var selfCostOnly = new GeneratorOperation("I:ReduceThisCardCostCombat", OperationScope.Independent,
            "本场战斗中，这张牌的耗能减少1。", new Dictionary<string, int>());
        var ordinaryBlock = new GeneratorOperation("N:B", OperationScope.NonTargeted,
            "获得5点格挡。", new Dictionary<string, int> { ["block"] = 5 });
        var selfHpLoss = new GeneratorOperation("N:HP-", OperationScope.NonTargeted,
            "失去2点生命。", new Dictionary<string, int> { ["hpLoss"] = 2 });
        var generatedStatus = new GeneratorOperation("D:CreateDazedInDiscard", OperationScope.NonTargeted,
            "将一张晕眩加入弃牌堆。", new Dictionary<string, int>(), DerivativeId: "dazed");
        if (CardEffectRules.HasValidSelfCostChangeAssembly([selfCostOnly])
            || CardEffectRules.HasValidSelfCostChangeAssembly([selfCostOnly, selfHpLoss])
            || CardEffectRules.HasValidSelfCostChangeAssembly([selfCostOnly, generatedStatus])
            || !CardEffectRules.HasValidSelfCostChangeAssembly([ordinaryBlock, selfCostOnly]))
            throw new InvalidOperationException("自身耗能变化组件必须搭配实际正面效果，负面效果不能满足约束。");
        AssertInvalid(new GeneratedCard(0, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common,
            "获得5点格挡。本场战斗中，这张牌的耗能减少1。", Array.Empty<CardTag>(),
            [ordinaryBlock, selfCostOnly]));
        AssertInvalid(new GeneratedCard(0, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Common, "获得5点格挡。本场战斗中，这张牌的耗能减少1。",
            Array.Empty<CardTag>(), [ordinaryBlock, selfCostOnly], Character: GeneratedCharacter.Regent,
            StarCost: 1));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common,
            "获得5点格挡。本场战斗中，这张牌的耗能减少1。", Array.Empty<CardTag>(),
            [ordinaryBlock, selfCostOnly], Upgrade: new CardUpgradePlan(0,
                [new CardUpgradeEffect(CardUpgradeKind.ReduceCost, Delta: -1)],
                "获得5点格挡。本场战斗中，这张牌的耗能减少1。", Array.Empty<CardTag>(),
                "Gain 5 Block. This card costs 1 less this combat.")));
        var delayedTurnTrigger = new GeneratorOperation("D:NextTurnsStart", OperationScope.ConditionalTrigger,
            "在接下来的3个回合开始时。", new Dictionary<string, int>());
        var delayedTargetDamage = new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly,
            "对目标敌人造成13点伤害。", new Dictionary<string, int> { ["damage"] = 13, ["triggerIndex"] = 0 },
            RequiresSingleTarget: true);
        var delayedRandomDamage = new GeneratorOperation("N:RandomD", OperationScope.NonTargeted,
            "对一名随机敌人造成13点伤害。", new Dictionary<string, int> { ["damage"] = 13, ["triggerIndex"] = 0 });
        if (CardEffectRules.HasValidDeferredEnemyTargetAssembly([delayedTurnTrigger, delayedTargetDamage])
            || !CardEffectRules.HasValidDeferredEnemyTargetAssembly([delayedTurnTrigger, delayedRandomDamage]))
            throw new InvalidOperationException("跨回合延迟触发器错误地接受了原始单敌目标，或拒绝了可自行解析的随机敌人目标。");
        var repeatedCardTrigger = new GeneratorOperation("NCR:WheneverCardPlayedThisTurn",
            OperationScope.ConditionalTrigger, "本回合每当你打出一张牌时。", new Dictionary<string, int>());
        var oneShotCondition = new GeneratorOperation("C:ifTargetPoisoned", OperationScope.ConditionalTrigger,
            "如果目标敌人拥有中毒。", new Dictionary<string, int>());
        var linkedNextTurnDouble = new GeneratorOperation("I:DoubleAttackDamageNextTurn",
            OperationScope.Independent, "在下个回合，你所有的攻击伤害翻倍。",
            new Dictionary<string, int> { ["triggerIndex"] = 0 });
        if (CardEffectRules.HasNoRepeatedTriggeredNextTurnAttackDouble(
                [repeatedCardTrigger, linkedNextTurnDouble])
            || !CardEffectRules.HasNoRepeatedTriggeredNextTurnAttackDouble(
                [oneShotCondition, linkedNextTurnDouble]))
            throw new InvalidOperationException("下个回合攻击伤害翻倍必须拒绝可重复触发器，但保留一次性条件组合。");
        var ostyHpTrigger = new GeneratorOperation("A:whenOstyLosesHp", OperationScope.AbilityTrigger,
            "每当奥斯提失去生命时。", new Dictionary<string, int>());
        var equalHpLoss = new GeneratorOperation("NCR:AllEnemiesLoseEventHp", OperationScope.NonTargeted,
            "所有敌人失去等量生命。", new Dictionary<string, int>());
        var linkedEqualHpLoss = equalHpLoss with
        {
            Parameters = new Dictionary<string, int> { ["triggerIndex"] = 0 }
        };
        if (CardEffectRules.HasValidEventAmountAssembly([equalHpLoss])
            || !CardEffectRules.HasValidEventAmountAssembly([ostyHpTrigger, linkedEqualHpLoss]))
            throw new InvalidOperationException("等量生命效果没有被限制在奥斯提失去生命的事件数值上下文中。");
        var staticExtraHits = new GeneratorOperation("D:RepeatDamage", OperationScope.Modifier,
            "这张牌额外造成2次伤害。", new Dictionary<string, int> { ["hits"] = 2 });
        var dynamicTotalHits = new GeneratorOperation("D:RepeatPerOrb", OperationScope.Modifier,
            "当前每有一个充能球，这张牌就造成一次伤害。", new Dictionary<string, int>());
        var fixedRandomDamage = new GeneratorOperation("N:RandomD", OperationScope.NonTargeted,
            "随机对敌人造成5点伤害。", new Dictionary<string, int> { ["damage"] = 5 });
        var multiRandomDamage = new GeneratorOperation("N:RandomD", OperationScope.NonTargeted,
            "随机对敌人造成5点伤害3次。", new Dictionary<string, int> { ["damage"] = 5, ["hits"] = 3 });
        var areaDamage = new GeneratorOperation("N:AllD", OperationScope.NonTargeted,
            "对所有敌人造成5点伤害。", new Dictionary<string, int> { ["damage"] = 5 });
        var secondDamage = new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly,
            "造成8点伤害。", new Dictionary<string, int> { ["damage"] = 8 }, RequiresSingleTarget: true);
        var repeatAreaOnKill = new GeneratorOperation("M:RepeatAreaOnKill", OperationScope.Modifier,
            "每有一名敌人被击杀，就重复此效果。", new Dictionary<string, int>());
        if (!CardEffectRules.HasValidRepeatDamageAssembly([fixedRandomDamage, staticExtraHits])
            || !CardEffectRules.HasValidRepeatDamageAssembly([multiRandomDamage, staticExtraHits])
            || !CardEffectRules.HasValidRepeatDamageAssembly([fixedRandomDamage, dynamicTotalHits])
            || CardEffectRules.HasValidRepeatDamageAssembly([multiRandomDamage, dynamicTotalHits])
            || CardEffectRules.HasValidRepeatDamageAssembly([staticExtraHits])
            || CardEffectRules.HasValidRepeatDamageAssembly([fixedRandomDamage, secondDamage, staticExtraHits])
            || !CardEffectRules.HasValidRepeatDamageAssembly([areaDamage, repeatAreaOnKill])
            || CardEffectRules.HasValidRepeatDamageAssembly([fixedRandomDamage, repeatAreaOnKill]))
            throw new InvalidOperationException("静态额外段数与动态总段数的伤害兼容规则失效。");
        var selfDoom = new GeneratorOperation("NCR:ApplySelfDoom", OperationScope.NonTargeted,
            "给予自身1层灾厄。", new Dictionary<string, int>());
        if (!CardEffectRules.IsNegativeEffect(selfDoom) || CardEffectRules.IsBeneficialEffect(selfDoom)
            || new[] { selfDoom }.Any(CardEffectRules.IsBeneficialEffect)
            || !new[] { selfDoom, ordinaryBlock }.Any(CardEffectRules.IsBeneficialEffect)
            || NegativeEffectTuning.BaseMultiplier(selfDoom) != 1d
            || NegativeEffectTuning.LinearCompensationValue(selfDoom) != 650d)
            throw new InvalidOperationException("给予自身灾厄必须是负面效果，不能单独支撑非0费牌。");

        _ = CharacterComponentCatalogs.Get(GeneratedCharacter.Defect);
        var zeroCostFilter = new GeneratorOperation("D:ReturnZeroCostDiscardToHand", OperationScope.NonTargeted,
            "将弃牌堆中所有0费牌放入手牌。", new Dictionary<string, int>());
        var markerCard = new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            CardDescriptionRenderer.Render([zeroCostFilter, ordinaryBlock]), Array.Empty<CardTag>(),
            [zeroCostFilter, ordinaryBlock], Character: GeneratedCharacter.Defect);
        var markerUpgrades = Enumerable.Range(0, 500)
            .Select(seed => CardUpgradeGenerator.Generate(markerCard, new Random(seed))).ToArray();
        if (markerUpgrades.Any(upgrade => upgrade.Effects.Any(effect =>
                effect.Kind == CardUpgradeKind.IncreaseNumber && effect.OperationIndex == 0))
            || markerUpgrades.Any(upgrade => upgrade.UpgradedChineseDescription.Contains("所有1费牌", StringComparison.Ordinal)))
            throw new InvalidOperationException("0费牌筛选标记被错误地作为普通数值升级。");
        var legacyMarkerUpgrade = CardUpgradeGenerator.ApplyEffectsToOperations([zeroCostFilter],
            [new CardUpgradeEffect(CardUpgradeKind.IncreaseNumber, 0, 1)]);
        if (legacyMarkerUpgrade[0].ChineseText != zeroCostFilter.ChineseText)
            throw new InvalidOperationException("旧版0费筛选升级没有被兼容层忽略。");

        _ = CharacterComponentCatalogs.Get(GeneratedCharacter.Regent);
        var xThreshold = new GeneratorOperation("R:IfEnergyXAtLeast", OperationScope.Modifier,
            "如果X至少为4，", new Dictionary<string, int>());
        var xPayoff = new GeneratorOperation("R:DoubleEnergyX", OperationScope.Independent,
            "X翻倍。", new Dictionary<string, int>());
        var xThresholdSpec = OperationRuntimeSpecCompiler.GetOrCompile(xThreshold);
        var excessiveXThreshold = xThreshold with
        {
            RuntimeSpec = xThresholdSpec with
            {
                Values = xThresholdSpec.Values.Select(value => value.Id == "threshold"
                    ? value with { BaseValue = 5 }
                    : value).ToArray()
            }
        };
        var xThresholdCard = new GeneratedCard(-1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            CardDescriptionRenderer.Render([xThreshold, xPayoff]), Array.Empty<CardTag>(), [xThreshold, xPayoff],
            Character: GeneratedCharacter.Regent);
        if (Enumerable.Range(0, 250).Select(seed => CardUpgradeGenerator.Generate(xThresholdCard, new Random(seed)))
                .Any(upgrade => upgrade.Effects.Any(effect => effect.OperationIndex == 0))
            || !CardEffectRules.HasValidEnergyXDoubleThreshold([xThreshold, xPayoff])
            || CardEffectRules.HasValidEnergyXDoubleThreshold([excessiveXThreshold, xPayoff]))
            throw new InvalidOperationException("依赖条件中的X或数值门槛被错误地作为普通数值升级。");

        var generalizedXDamage = new GeneratorOperation("T:D_EnergyX", OperationScope.SingleEnemyOnly,
            "造成8点伤害X次。", new Dictionary<string, int>(), RequiresSingleTarget: true);
        var generalizedXDouble = new GeneratorOperation("R:DoubleEitherXAtThreshold", OperationScope.Modifier,
            "如果X至少为4，则X翻倍。", new Dictionary<string, int>());
        var generalizedXDoubleSpec = OperationRuntimeSpecCompiler.GetOrCompile(generalizedXDouble);
        var invalidGeneralizedXDouble = generalizedXDouble with
        {
            RuntimeSpec = generalizedXDoubleSpec with
            {
                Values = generalizedXDoubleSpec.Values.Select(value => value.Id == "threshold"
                    ? value with { BaseValue = 5 }
                    : value).ToArray()
            }
        };
        var generalizedXOperations = new[] { generalizedXDamage, generalizedXDouble };
        var generalizedXDoubleValue = EffectBalanceModel.EstimatedContextualOperationValueForAudit(
            generalizedXDouble, 1, generalizedXOperations, true, GeneratedCardType.Attack, []);
        if (!CardEffectRules.HasValidEnergyXDoubleThreshold(generalizedXOperations)
            || CardEffectRules.HasValidEnergyXDoubleThreshold([generalizedXDouble])
            || CardEffectRules.HasValidEnergyXDoubleThreshold([generalizedXDamage, invalidGeneralizedXDouble])
            || Math.Abs(generalizedXDoubleValue - 540d) > 0.001d)
            throw new InvalidOperationException(
                $"通用X翻倍组件的依赖、固定门槛或天钻反推价值失效：{generalizedXDoubleValue:0.###}。");

        var finaleCondition = new GeneratorOperation("C:playableIfDrawPileEmpty", OperationScope.ConditionalTrigger,
            "只有当你的抽牌堆中没有牌时。", new Dictionary<string, int>());
        if (!CardEffectRules.HasValidGrandFinaleCost(0, -1, false, [finaleCondition])
            || CardEffectRules.HasValidGrandFinaleCost(1, -1, false, [finaleCondition])
            || CardEffectRules.HasValidGrandFinaleCost(0, 1, false, [finaleCondition])
            || CardEffectRules.HasValidGrandFinaleCost(0, -1, true, [finaleCondition]))
            throw new InvalidOperationException("华丽收场条件没有被限制在最终0费卡上。");

        var costDownThree = new GeneratorOperation("R:CostDownWhenDrawn", OperationScope.Independent,
            "本场战斗此牌耗能减少3。", new Dictionary<string, int>());
        var boundedCostReductions = new List<GeneratorOperation> { costDownThree };
        if (!CardEffectRules.ClampNumericSelfCostReductionAmounts(2, boundedCostReductions)
            || OperationRuntimeSpecCompiler.StaticLiteralValue(boundedCostReductions[0], "amount") != 2
            || CardEffectRules.HasValidNumericSelfCostReductionAmounts(1, [costDownThree])
            || CardEffectRules.ClampNumericSelfCostReductionAmounts(0,
                new List<GeneratorOperation> { costDownThree }))
            throw new InvalidOperationException("条件型本牌减费没有按最终普通费用限制在1至卡牌耗能之间。");

        var specialXSourceOperations = new[]
        {
            new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly, "造成2点伤害。",
                new Dictionary<string, int>()),
            new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得2点格挡。",
                new Dictionary<string, int>())
        };
        var specialXSource = new GeneratedCard(2, GeneratedCardType.Attack, TargetMode.SingleEnemy,
            GeneratedRarity.Rare, CardDescriptionRenderer.Render(specialXSourceOperations), Array.Empty<CardTag>(),
            specialXSourceOperations, EnglishDescription: EnglishCardDescriptionRenderer.Render(specialXSourceOperations));
        AssertInvalid(specialXSource with { Cost = 5 });
        var specialX = SpecialXCardConverter.Convert(specialXSource, new Random(20260830),
            SpecialXGenerationMode.Forced);
        if (specialX.Cost != -1 || specialX.HasStarCostX
            || specialX.Operations.Count(SpecialXCardConverter.IsSpecial) != 2
            || specialX.Operations.Any(operation => !operation.ChineseText.Contains('X'))
            || !specialX.EnglishDescription.Contains("X damage", StringComparison.Ordinal)
            || CardEffectRules.IsIntrinsicMultiHitDamage(specialX.Operations[0]))
            throw new InvalidOperationException("固定费用与多个效果数值联动的特殊X转换失效。");
        CardTemplateValidator.Validate(specialX);
        var specialXPowerSourceOperations = new[]
        {
            new GeneratorOperation("A:turnStart", OperationScope.AbilityTrigger,
                "在你的回合开始时。", new Dictionary<string, int>()),
            new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得2点格挡。",
                new Dictionary<string, int> { ["triggerIndex"] = 0 })
        };
        var specialXPowerSource = new GeneratedCard(2, GeneratedCardType.Power, TargetMode.Other,
            GeneratedRarity.Rare, CardDescriptionRenderer.Render(specialXPowerSourceOperations),
            Array.Empty<CardTag>(), specialXPowerSourceOperations,
            EnglishDescription: EnglishCardDescriptionRenderer.Render(specialXPowerSourceOperations),
            Character: GeneratedCharacter.Ironclad);
        var specialXPower = SpecialXCardConverter.Convert(specialXPowerSource, new Random(20260834),
            SpecialXGenerationMode.Forced);
        if (specialXPower.Cost != -1 || specialXPower.Type != GeneratedCardType.Power
            || !SpecialXCardConverter.IsSpecial(specialXPower.Operations[1])
            || specialXPower.Operations[1].ChineseText != "获得X点格挡。")
            throw new InvalidOperationException("能力牌未能转换为特殊X费牌。 ");
        CardTemplateValidator.Validate(specialXPower);
        var repeatedDamageOperation = new GeneratorOperation("N:RandomD", OperationScope.NonTargeted,
            "随机对敌人造成2点伤害2次。", new Dictionary<string, int>());
        var repeatedDamageSource = new GeneratedCard(2, GeneratedCardType.Attack, TargetMode.Other,
            GeneratedRarity.Rare, CardDescriptionRenderer.Render([repeatedDamageOperation]), Array.Empty<CardTag>(),
            [repeatedDamageOperation], EnglishDescription: EnglishCardDescriptionRenderer.Render([repeatedDamageOperation]));
        var repeatedSpecialX = SpecialXCardConverter.Convert(repeatedDamageSource, new Random(20260833),
            SpecialXGenerationMode.Forced);
        if (repeatedSpecialX.Operations[0].ChineseText != "随机对敌人造成X点伤害X次。"
            || !SpecialXCardConverter.ValueUsesSpecialX(repeatedSpecialX.Operations[0], 0)
            || !SpecialXCardConverter.ValueUsesSpecialX(repeatedSpecialX.Operations[0], 1)
            || !CardEffectRules.IsIntrinsicMultiHitDamage(repeatedSpecialX.Operations[0]))
            throw new InvalidOperationException("同一组件中的多个相等强度槽没有全部转换为特殊X。");
        CardTemplateValidator.Validate(repeatedSpecialX);
        var specialXRolls = Enumerable.Range(0, 100_000)
            .Count(seed => SpecialXCardConverter.IsSpecial(SpecialXCardConverter.Convert(specialXSource,
                new Random(seed), SpecialXGenerationMode.Normal)));
        if (specialXRolls is < 2_250 or > 2_750)
            throw new InvalidOperationException($"特殊X自然生成概率偏离2.5%：{specialXRolls}/100000。");
        var specialXDrawSourceOperation = new GeneratorOperation("N:Draw", OperationScope.NonTargeted,
            "抽2张牌。", new Dictionary<string, int>());
        var specialXDrawSource = new GeneratedCard(2, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Rare, CardDescriptionRenderer.Render([specialXDrawSourceOperation]),
            Array.Empty<CardTag>(), [specialXDrawSourceOperation],
            EnglishDescription: EnglishCardDescriptionRenderer.Render([specialXDrawSourceOperation]));
        var specialXDrawRolls = Enumerable.Range(0, 100_000)
            .Count(seed => SpecialXCardConverter.IsSpecial(SpecialXCardConverter.Convert(specialXDrawSource,
                new Random(seed), SpecialXGenerationMode.Normal)));
        if (specialXDrawRolls is < 250 or > 500 || specialXDrawRolls * 4 >= specialXRolls)
            throw new InvalidOperationException(
                $"非能力牌的特殊X抽牌抑制失效：draw={specialXDrawRolls}, baseline={specialXRolls}。");
        var starXSourceOperation = new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得2点格挡。",
            new Dictionary<string, int>());
        var starXSource = new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            CardDescriptionRenderer.Render([starXSourceOperation]), Array.Empty<CardTag>(), [starXSourceOperation],
            EnglishDescription: EnglishCardDescriptionRenderer.Render([starXSourceOperation]),
            Character: GeneratedCharacter.Regent, StarCost: 2);
        var specialStarX = SpecialXCardConverter.Convert(starXSource, new Random(20260831),
            SpecialXGenerationMode.Forced);
        if (specialStarX.Cost != 1 || !specialStarX.HasStarCostX || specialStarX.StarCost != -1
            || SpecialXCardConverter.Resource(specialStarX.Operations[0]) != SpecialXCardConverter.StarResource)
            throw new InvalidOperationException("固定蓝星费用与效果数值联动的特殊X转换失效。");
        CardTemplateValidator.Validate(specialStarX);
        var xCostWithSelfCostChange = specialX with
        {
            Operations = specialX.Operations.Append(new GeneratorOperation("I:ReduceThisCardCostCombat",
                OperationScope.Independent, "本场战斗中，这张牌的耗能减少1。",
                new Dictionary<string, int>())).ToArray()
        };
        AssertInvalid(xCostWithSelfCostChange);
        var forcedSpecialGenerator = new RandomCardGenerator(GeneratedCharacter.Ironclad, 20260832);
        foreach (var rarity in new[] { GeneratedRarity.Common, GeneratedRarity.Uncommon, GeneratedRarity.Rare })
        {
            var generatedSpecial = forcedSpecialGenerator.GenerateSpecialX(rarity);
            if (!SpecialXCardConverter.IsSpecial(generatedSpecial)
                || generatedSpecial.Operations.Any(CardEffectRules.IsSelfCostChange))
                throw new InvalidOperationException($"{rarity} 特殊X生成路径未命中，或混入了本牌费用调整效果。");
            CardTemplateValidator.Validate(generatedSpecial);
        }

        var statusTemplates = DerivativeSlotCatalog.SlotTemplates
            .Where(DerivativeSlotCatalog.IsStatusProducer).ToArray();
        if (statusTemplates.Length != 7
            || statusTemplates.Any(template => !CardEffectRules.IsNegativeEffect(
                new GeneratorOperation(template, OperationScope.NonTargeted, "生成状态牌。",
                    new Dictionary<string, int>())))
            || DerivativeSlotCatalog.Candidates(GeneratedCharacter.Defect, false, "D:CreateDazedInDiscard") is not { Count: 5 } defectStatuses
            || defectStatuses.Any(definition => !DerivativeSlotCatalog.IsStatus(definition)
                || definition.Owner != GeneratedCharacter.Defect)
            || DerivativeSlotCatalog.Candidates(GeneratedCharacter.Regent, false, "R:AddDebrisToHand")
                .Any(definition => definition.Id != "debris"))
            throw new InvalidOperationException("状态牌槽位没有保持同角色状态牌候选及纯负面分类。");
        var ultimateStatuses = DerivativeSlotCatalog.Candidates(GeneratedCharacter.Defect, true,
            "D:CreateDazedInDiscard");
        if (ultimateStatuses.Count != 6 || ultimateStatuses.Any(definition => !DerivativeSlotCatalog.IsStatus(definition)))
            throw new InvalidOperationException("终极混乱的状态牌槽位没有解锁全部六种状态牌。");
        foreach (var template in DerivativeSlotCatalog.SlotTemplates.Where(template =>
                     template != "R:FillHandWithDebris"))
        {
            var expectsStatus = DerivativeSlotCatalog.IsStatusProducer(template);
            foreach (var character in Enum.GetValues<GeneratedCharacter>())
            {
                foreach (var ultimateChaos in new[] { false, true })
                {
                    var candidates = DerivativeSlotCatalog.Candidates(character, ultimateChaos, template);
                    if (candidates.Any(definition => DerivativeSlotCatalog.IsStatus(definition) != expectsStatus))
                        throw new InvalidOperationException(
                            $"{template} 在 {character}/Ultimate={ultimateChaos} 下混淆了状态牌与衍生牌槽位。");
                }
            }
        }
        var statusTransformOutputs = DerivativeSlotCatalog.Candidates(GeneratedCharacter.Defect, false,
            "D:TransformStatusesToFuel");
        if (statusTransformOutputs.Count != 1 || statusTransformOutputs[0].Id != "fuel")
            throw new InvalidOperationException("将手牌中所有状态牌变化为衍生牌的输出槽必须只包含鸡煲衍生牌。");
        if (!CardEffectRules.IsExtremeLifecycleDownside(new GeneratorOperation("R:FillHandWithDebris",
                OperationScope.NonTargeted, "将碎屑加入手牌，直到手牌已满。", new Dictionary<string, int>())))
            throw new InvalidOperationException("迫降的填满手牌状态效果必须属于重负面。");
        var legacyDebris = new GeneratorOperation("R:FillHandWithDebris", OperationScope.NonTargeted,
            "将残骸加入手牌，直到手牌已满。", new Dictionary<string, int>(), DerivativeId: "debris");
        if (DerivativeSlotCatalog.All.Single(definition => definition.Id == "debris").ChineseName != "碎屑"
            || CardTextStyle.Chinese(legacyDebris).Contains("残骸", StringComparison.Ordinal)
            || !CardTextStyle.Chinese(legacyDebris).Contains("碎屑", StringComparison.Ordinal)
            || !EnglishCardDescriptionRenderer.OperationText(legacyDebris).Contains("Debris", StringComparison.Ordinal))
            throw new InvalidOperationException("Debris 必须使用原版简中名称“碎屑”，并兼容旧快照中的“残骸”。");
        var crashLandingRolls = 100_000;
        var crashLandingDerivatives = 0;
        var crashLandingRandom = new Random(20260826);
        for (var index = 0; index < crashLandingRolls; index++)
        {
            var derivative = DerivativeSlotCatalog.Roll(crashLandingRandom, GeneratedCharacter.Regent, false,
                "R:FillHandWithDebris");
            if (DerivativeSlotCatalog.IsStatus(derivative) || DerivativeSlotCatalog.IsCurse(derivative)) continue;
            if (derivative.Owner != GeneratedCharacter.Regent)
                throw new InvalidOperationException("迫降的稀有衍生物分支越过了角色限制。");
            crashLandingDerivatives++;
        }
        if (Math.Abs(crashLandingDerivatives / (double)crashLandingRolls
                - DerivativeSlotCatalog.FillHandDerivativeChance) > 0.005)
            throw new InvalidOperationException("迫降的按卡衍生物替换概率没有保持在5%。");
        var orbAuditRandom = new Random(20260825);
        OrbSlotCatalog.ValidateBudgetModel();
        var voltaicSame = 0;
        const int orbRolls = 20_000;
        for (var index = 0; index < orbRolls; index++)
        {
            var (source, output) = OrbSlotCatalog.Roll(orbAuditRandom, "I:ProxyAtomic_Voltaic");
            if (source?.Id == output?.Id) voltaicSame++;
        }
        if (Math.Abs(voltaicSame / (double)orbRolls - 0.75) > 0.025
            || OrbSlotCatalog.Roll(orbAuditRandom, "A:whenLightningEvoked").Source?.DealsDamage != true
            || OrbSlotCatalog.Roll(orbAuditRandom, "D:TriggerDarkPassives").Source?.Id == "random"
            || new[] { "D:ChannelLightning", "D:ChannelFrost", "D:ChannelDark", "D:ChannelPlasma",
                    "D:ChannelGlass", "D:ChannelRandom" }
                .Any(template => !OrbSlotCatalog.UsesOutput(template)))
            throw new InvalidOperationException("充能球槽位、雷霆伤害类型限制或电流相生75%同类约束失效。");
        var adjustedOrbWeights = OrbSlotCatalog.All.Sum(OrbSlotCatalog.OutputGenerationWeight);
        var rolledOrbCounts = OrbSlotCatalog.All.ToDictionary(definition => definition.Id, _ => 0,
            StringComparer.Ordinal);
        for (var index = 0; index < 100_000; index++)
        {
            var output = OrbSlotCatalog.Roll(orbAuditRandom, "D:ChannelLightning").Output
                ?? throw new InvalidOperationException("固定生成充能球组件未能解析输出槽。");
            rolledOrbCounts[output.Id]++;
        }
        foreach (var definition in OrbSlotCatalog.All)
        {
            var actual = rolledOrbCounts[definition.Id] / 100_000d;
            var expected = OrbSlotCatalog.OutputGenerationWeight(definition) / (double)adjustedOrbWeights;
            if (Math.Abs(actual - expected) > 0.01)
                throw new InvalidOperationException(
                    $"{definition.ChineseName}充能球槽权重偏离调整后的卡池：{actual:P2} / {expected:P2}。 ");
        }
        var plasma = OrbSlotCatalog.Resolve("plasma")!;
        var glass = OrbSlotCatalog.Resolve("glass")!;
        var lightning = OrbSlotCatalog.Resolve("lightning")!;
        var channelBudgetRandom = new Random(20260827);
        var frostToPlasma = Enumerable.Range(0, 100_000)
            .Select(_ => OrbSlotCatalog.BudgetedChannelCount(channelBudgetRandom, "D:ChannelFrost", plasma,
                3, 3, perEnemy: false)).Average();
        if (Math.Abs(frostToPlasma - 1.0) > 0.02
            || OrbSlotCatalog.BudgetedChannelCount(channelBudgetRandom, "D:ChannelPlasma", plasma,
                3, 1, perEnemy: false) != 1
            || OrbSlotCatalog.BudgetedChannelCount(channelBudgetRandom, "D:ChannelGlass", glass,
                3, 3, perEnemy: false) != 2
            || OrbSlotCatalog.BudgetedChannelCount(channelBudgetRandom, "D:ChannelLightning", lightning,
                3, 3, perEnemy: true) != 1)
            throw new InvalidOperationException("不同充能球的数量预算换算或费用/触发频率上限失效。 ");
        var inkySpecial = 0;
        var inkyOrdinary = 0;
        var enchantAuditRandom = new Random(20260824);
        const int enchantRolls = 20_000;
        for (var index = 0; index < enchantRolls; index++)
        {
            if (DerivativeEnchantmentCatalog.Roll(enchantAuditRandom, "N:CreateInkShiv", shivDefinition)?.Id == "inky")
                inkySpecial++;
            if (DerivativeEnchantmentCatalog.Roll(enchantAuditRandom, "N:CreateShiv", shivDefinition)?.Id == "inky")
                inkyOrdinary++;
        }
        if (inkySpecial < enchantRolls / 3 || inkySpecial <= inkyOrdinary * 20)
            throw new InvalidOperationException("墨影×小刀没有保持显著高于普通附魔组合的生成概率。");
        var enchantAmountRandom = new Random(20260828);
        foreach (var enchantment in DerivativeEnchantmentCatalog.All.Where(enchantment => enchantment.Amount > 1))
        {
            var rolled = Enumerable.Range(0, 10_000)
                .Select(_ => DerivativeEnchantmentCatalog.RollAmount(enchantAmountRandom, enchantment))
                .ToArray();
            if (rolled.Distinct().Count() < 2
                || rolled.Any(amount => !DerivativeEnchantmentCatalog.IsAllowedAmount(enchantment, amount)))
                throw new InvalidOperationException($"衍生物附魔 {enchantment.Id} 的随机数值分布失效。");
        }
        var swift = DerivativeEnchantmentCatalog.Resolve("swift")!;
        var swiftRolls = Enumerable.Range(0, 100_000)
            .Select(_ => DerivativeEnchantmentCatalog.RollAmount(enchantAmountRandom, swift)).ToArray();
        var swiftThree = swiftRolls.Count(amount => amount == 3);
        if (swiftRolls.Count(amount => amount is 1 or 2) < 97_000
            || swiftThree is < 1_000 or > 3_000)
            throw new InvalidOperationException("迅速附魔没有保持1/2为主、3极少出现的分布。");
        foreach (var character in Enum.GetValues<GeneratedCharacter>().Where(character => character != GeneratedCharacter.Colorless))
        {
            foreach (var template in DerivativeSlotCatalog.SlotTemplates)
            {
                var source = DerivativeSlotCatalog.Source(template)!;
                if (source.Owner != character) continue;
                var normalCandidates = DerivativeSlotCatalog.Candidates(character, false, template);
                if (normalCandidates.Any(candidate => candidate.Owner != character))
                    throw new InvalidOperationException($"{character}/{template} 的普通衍生物槽越过了角色锁。");
            }
        }
        var ordinaryStatusCandidates = DerivativeSlotCatalog.Candidates(
            GeneratedCharacter.Defect, false, "D:CreateDazedInDiscard");
        if (ordinaryStatusCandidates.Any(DerivativeSlotCatalog.IsCurse))
            throw new InvalidOperationException("诅咒进入了普通状态牌槽候选池。");
        var curseDefinitions = DerivativeSlotCatalog.All.Where(DerivativeSlotCatalog.IsCurse).ToArray();
        if (curseDefinitions.Length != 10
            || curseDefinitions.Any(definition =>
                !DerivativeSlotCatalog.CanUseAssigned("D:CreateDazedInDiscard", definition)
                || DerivativeSlotCatalog.CanUse("D:CreateDazedInDiscard", definition)))
            throw new InvalidOperationException("状态牌诅咒彩蛋的候选或快照约束失效。");
        var curseAuditRandom = new Random(20260829);
        var rolledCurses = Enumerable.Range(0, 100_000)
            .Select(_ => DerivativeSlotCatalog.Roll(curseAuditRandom, GeneratedCharacter.Defect,
                false, "D:CreateDazedInDiscard"))
            .Where(DerivativeSlotCatalog.IsCurse)
            .ToArray();
        if (rolledCurses.Length is < 800 or > 1_200
            || rolledCurses.Select(definition => definition.Id).Distinct().Count() != curseDefinitions.Length)
            throw new InvalidOperationException(
                $"状态牌诅咒彩蛋没有保持约1%概率或未覆盖全部普通诅咒：{rolledCurses.Length}/100000。");
        var allDerivatives = DerivativeSlotCatalog.All.Count(definition =>
            !DerivativeSlotCatalog.IsStatus(definition) && !DerivativeSlotCatalog.IsCurse(definition));
        if (DerivativeSlotCatalog.Candidates(GeneratedCharacter.Ironclad, true, "I:Transform").Count != allDerivatives)
            throw new InvalidOperationException("终极混乱没有向通用变化槽开放全部非状态衍生牌。");
        if (DerivativeSlotCatalog.Candidates(GeneratedCharacter.Ironclad, true, "I:PlayExhaustedShivsAtTarget")
            .Any(candidate => !candidate.IsPlayable || !candidate.IsAttack || !candidate.CanTargetEnemy || !candidate.HasExhaust))
            throw new InvalidOperationException("指定敌人打出衍生牌的槽包含无法合理取目标的衍生牌。");
        if (DerivativeSlotCatalog.SupportsUpgrade("I:PlayExhaustedShivsAtTarget", "shiv"))
            throw new InvalidOperationException("消耗牌堆中的小刀是类别引用，不得升级为小刀+。");
        var exhaustedShivReference = new GeneratorOperation("I:PlayExhaustedShivsAtTarget",
            OperationScope.SingleEnemyOnly, "将消耗牌堆中的所有小刀对该敌人打出。",
            new Dictionary<string, int>(), RequiresSingleTarget: true, DerivativeId: "shiv");
        var legacyShivReferenceUpgrade = CardUpgradeGenerator.ApplyEffectsToOperations([exhaustedShivReference],
            [new CardUpgradeEffect(CardUpgradeKind.UpgradeDerivative, 0)]);
        if (legacyShivReferenceUpgrade[0].ChineseText != exhaustedShivReference.ChineseText)
            throw new InvalidOperationException("旧版消耗牌堆小刀类别引用仍被错误地显示为小刀+。");
        foreach (var template in DerivativeSlotCatalog.SlotTemplates.Where(DerivativeSlotCatalog.IsExhaustPileReference))
            if (DerivativeSlotCatalog.Candidates(GeneratedCharacter.Ironclad, true, template)
                .Any(candidate => !candidate.HasExhaust))
                throw new InvalidOperationException($"消耗牌堆衍生物槽 {template} 接受了默认不消耗的衍生牌。");
        var legacyOperation = System.Text.Json.JsonSerializer.Deserialize<GeneratorOperation>(
            "{\"Template\":\"I:Transform\",\"Scope\":6,\"ChineseText\":\"将手牌中的所有攻击牌变化为巨石。\",\"Parameters\":{}}")!;
        if (legacyOperation.DerivativeId is not null
            || DerivativeSlotCatalog.Resolve(legacyOperation.DerivativeId, legacyOperation.Template)?.Id != "rock")
            throw new InvalidOperationException("旧快照缺少衍生物槽时必须回退至该 operation 的原衍生牌。");
        _ = CharacterComponentCatalogs.Get(GeneratedCharacter.Necrobinder);
        var restoredUpgradeText = EnglishCardDescriptionRenderer.OperationText(new GeneratorOperation(
            "NCR:CreateSoulInDiscard", OperationScope.NonTargeted, "将997张小刀+加入弃牌堆。",
            new Dictionary<string, int>(), DerivativeId: "shiv"));
        if (!restoredUpgradeText.Contains("997 Shivs+", StringComparison.Ordinal)
            || restoredUpgradeText.Contains("Soul", StringComparison.Ordinal))
            throw new InvalidOperationException("跨版本恢复后的衍生物升级无法从槽值重建英文动态描述：" + restoredUpgradeText);
        var enchantedDerivativeText = EnglishCardDescriptionRenderer.OperationText(new GeneratorOperation(
            "N:CreateShiv", OperationScope.NonTargeted, "将2张本能小刀加入手牌。",
            new Dictionary<string, int>(), DerivativeId: "shiv", DerivativeEnchantmentId: "instinct"));
        if (enchantedDerivativeText != "Add 2 Instinct Shivs to your hand.")
            throw new InvalidOperationException("附魔衍生牌英文名称被重复拼接：" + enchantedDerivativeText);
        var allComponentKeys = Enum.GetValues<GeneratedCharacter>()
            .SelectMany(character => CharacterComponentCatalogs.Get(character).AtomKeys)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var character in Enum.GetValues<GeneratedCharacter>())
        {
            var unlockedCatalog = CharacterComponentCatalogs.Get(character, unlockComponentRoles: true);
            if (!allComponentKeys.IsSubsetOf(unlockedCatalog.AtomKeys))
                throw new InvalidOperationException($"终极混乱未解除 {character} 的全部组件角色锁。");
            var ultimateGenerator = new RandomCardGenerator(character, 20260822 + (int)character * 997,
                unlockComponentRoles: true);
            var nativeNameIds = CharacterComponentCatalogs.Get(character).Recipes
                .Select(recipe => recipe.Id).ToHashSet(StringComparer.Ordinal);
            // Catalog coverage is deterministic above; a small randomized pass is enough to
            // exercise construction without multiplying the already-large 10,000-card stress test.
            for (var index = 0; index < 16; index++)
            {
                var generated = ultimateGenerator.Generate();
                CardTemplateValidator.Validate(generated);
                if (generated.Name?.SourceCardIds.Any(source => !nativeNameIds.Contains(source)) != false)
                    throw new InvalidOperationException($"终极混沌的 {character} 卡名引用了异色名称词块。");
            }
        }

        // Ultimate Chaos is a genuine sixth statistical profile, not merely a union of
        // atom allow-lists. With the same random stream, every character must therefore
        // receive the same shell, resource costs, values, tags and upgrade. Names intentionally use the current
        // character's own catalog, but their isolated RNG must not perturb any later gameplay generation.
        var unifiedRarities = Enum.GetValues<GeneratedRarity>();
        var unifiedSeed = 2026082107;
        var unifiedBaselineGenerator = new RandomCardGenerator(GeneratedCharacter.Ironclad, unifiedSeed, true);
        var unifiedBaseline = unifiedRarities.SelectMany(rarity => Enumerable.Range(0, 4)
                .Select(_ => unifiedBaselineGenerator.Generate(rarity)))
            .Select(ComparableUltimateCard).ToArray();
        foreach (var character in Enum.GetValues<GeneratedCharacter>().Where(character => character != GeneratedCharacter.Ironclad))
        {
            var candidateGenerator = new RandomCardGenerator(character, unifiedSeed, true);
            var candidate = unifiedRarities.SelectMany(rarity => Enumerable.Range(0, 4)
                    .Select(_ => candidateGenerator.Generate(rarity)))
                .Select(ComparableUltimateCard).ToArray();
            if (!candidate.SequenceEqual(unifiedBaseline, StringComparer.Ordinal))
            {
                var mismatch = Enumerable.Range(0, unifiedBaseline.Length)
                    .First(index => unifiedBaseline[index] != candidate[index]);
                throw new InvalidOperationException(
                    $"终极混乱第六配置仍受角色影响：{character}，样本索引{mismatch}。\n"
                    + $"baseline={unifiedBaseline[mismatch]}\nactual={candidate[mismatch]}");
            }
        }
        foreach (var character in Enum.GetValues<GeneratedCharacter>())
        foreach (var ultimate in new[] { false, true })
        {
            var basicCount = character == GeneratedCharacter.Colorless ? 0
                : character == GeneratedCharacter.Silent ? 12 : 10;
            var rarities = character == GeneratedCharacter.Colorless
                ? Enumerable.Repeat(GeneratedRarity.Uncommon, 31)
                    .Concat(Enumerable.Repeat(GeneratedRarity.Rare, 21)).ToArray()
                : Enumerable.Repeat(GeneratedRarity.Basic, basicCount)
                    .Concat(Enumerable.Repeat(GeneratedRarity.Common, 20))
                    .Concat(Enumerable.Repeat(GeneratedRarity.Uncommon, 35))
                    .Concat(Enumerable.Repeat(GeneratedRarity.Rare, 25))
                    .Concat(Enumerable.Repeat(GeneratedRarity.Ancient, 2)).ToArray();
            var poolRandom = new Random(20260823 + (int)character * 101 + (ultimate ? 1009 : 0));
            var resolved = false;
            for (var attempt = 0; attempt < 256 && !resolved; attempt++)
            {
                var poolGenerator = new RandomCardGenerator(character, poolRandom.Next(), ultimate);
                var cards = rarities.Select(poolGenerator.Generate).ToArray();
                resolved = OstyPoolConstraintResolver.TryResolve(cards, rarities, poolGenerator, poolRandom,
                    replacementAttemptLimit: 20_000, out _)
                    && SlyPoolConstraintResolver.TryResolve(cards, rarities, poolGenerator, poolRandom,
                        replacementAttemptLimit: 20_000, out _)
                    && DerivativePoolConstraintResolver.TryRepairAndResolve(cards, rarities, poolGenerator,
                        poolRandom, replacementAttemptLimit: 20_000, out _);
                if (resolved)
                {
                    OstyPoolConstraintResolver.Audit(cards);
                    SlyPoolConstraintResolver.Audit(cards);
                    DerivativePoolConstraintResolver.Audit(cards);
                    if (character != GeneratedCharacter.Colorless)
                    {
                        var startingCards = cards.Take(10).ToArray();
                        resolved = StartingPoolConstraintResolver.TryRepair(startingCards, poolGenerator,
                            poolRandom, minimumDamage: 0, minimumDefense: 0,
                            replacementAttemptLimit: 20_000, out _);
                        if (!resolved) continue;
                        if (startingCards.Count(StartingPoolConstraintResolver.IsHighResourceCard)
                            > StartingPoolConstraintResolver.MaximumHighResourceCards)
                            throw new InvalidOperationException("初始牌堆中高于1费或带蓝星耗费的牌超过2张。");
                    }
                    foreach (var operation in cards.SelectMany(card => card.Operations))
                    {
                        if (DerivativeSlotCatalog.IsReference(operation.Template)
                            && operation.DerivativeEnchantmentId is not null)
                            throw new InvalidOperationException("引用衍生牌的 operation 不得携带附魔槽。");
                        if (operation.DerivativeEnchantmentId is { } enchantmentId)
                        {
                            var derivative = DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template)!;
                            var enchantment = DerivativeEnchantmentCatalog.Resolve(enchantmentId)!;
                            if (!DerivativeSlotCatalog.IsProducer(operation.Template)
                                || !DerivativeEnchantmentCatalog.CanUse(derivative, enchantment))
                                throw new InvalidOperationException($"生成池含非法附魔组合 {derivative.Id}/{enchantmentId}。");
                            if (operation.DerivativeEnchantmentAmount is not { } enchantmentAmount
                                || !DerivativeEnchantmentCatalog.IsAllowedAmount(enchantment, enchantmentAmount))
                                throw new InvalidOperationException(
                                    $"生成池含非法附魔数值 {derivative.Id}/{enchantmentId}/{operation.DerivativeEnchantmentAmount}。");
                        }
                    }
                    foreach (var card in cards.Where(card => card.Upgrade is not null))
                    {
                        var upgraded = CardUpgradeGenerator.ApplyEffectsToOperations(card.Operations, card.Upgrade!.Effects);
                        if (CardDescriptionRenderer.Render(upgraded) != card.Upgrade.UpgradedChineseDescription
                            || EnglishCardDescriptionRenderer.Render(upgraded) != card.Upgrade.UpgradedEnglishDescription)
                            throw new InvalidOperationException($"{character} 的升级描述无法由 operation 与升级效果重建："
                                + $"{card.ChineseDescription} | {string.Join(';', card.Upgrade.Effects.Select(effect => $"{effect.Kind}/{effect.OperationIndex}/{effect.Delta}"))} "
                                + $"| expected={card.Upgrade.UpgradedChineseDescription} | actual={CardDescriptionRenderer.Render(upgraded)} "
                                + $"| expectedEn={card.Upgrade.UpgradedEnglishDescription} | actualEn={EnglishCardDescriptionRenderer.Render(upgraded)}");
                    }
                }
            }
            if (!resolved)
                throw new InvalidOperationException($"{character}/Ultimate={ultimate} 无法生成满足衍生物供需约束的完整卡池。");
        }
        RandomCardGenerator? generator = null;
        var ironcladNames = new HashSet<string>(StringComparer.Ordinal);
        var ironcladEffects = new HashSet<string>(StringComparer.Ordinal);
        var ironcladTemplates = new HashSet<string>(StringComparer.Ordinal);
        var ironcladSourceIds = CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad).Recipes
            .Select(recipe => recipe.Id).ToHashSet(StringComparer.Ordinal);
        var ironcladOriginalNames = CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad).Recipes.Select(recipe => recipe.ChineseTitle).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < 10_000; i++)
        {
            if (i % 92 == 0)
            {
                generator = new RandomCardGenerator(20260819 + i);
                ironcladNames.Clear();
                ironcladEffects.Clear();
                ironcladTemplates.Clear();
            }
            var generated = generator!.Generate();
            CardTemplateValidator.Validate(generated);
            if (!ironcladNames.Add(generated.Name!.Chinese))
                throw new InvalidOperationException("同一战士卡池中出现了重复中文卡名。");
            if (!ironcladEffects.Add(GeneratedCardEffectIdentity.Signature(generated)))
                throw new InvalidOperationException("同一战士卡池中出现了基础效果完全相同的卡牌。");
            if (!ironcladTemplates.Add(GeneratedCardEffectIdentity.TemplateSignature(generated)))
                throw new InvalidOperationException("同一战士卡池中出现了同费用、同类型且仅数值不同的重复效果模板。 ");
            if (generated.Name.SourceCardIds.Any(source => !ironcladSourceIds.Contains(source)))
                throw new InvalidOperationException("战士卡名引用了其他角色或无色卡的名称词块。");
            if (ironcladOriginalNames.Contains(generated.Name.Chinese))
                throw new InvalidOperationException($"随机战士卡名与原卡重名：{generated.Name.Chinese}。");
            if (generated.Upgrade?.Effects.Count is not (>= 1 and <= 2))
                throw new InvalidOperationException("生成卡必须具有1至2个有效升级效果。");
            if (System.Text.RegularExpressions.Regex.IsMatch(generated.ChineseDescription, @"\bcard\d+\b|\b[A-Z]:"))
                throw new InvalidOperationException("中文描述不能泄漏内部 slot 或 operation DSL 标记。");
            if (System.Text.RegularExpressions.Regex.IsMatch(generated.ChineseDescription, @"(?m)^(?:虚无(?:。|$)|若|每回合开始时|每回合结束时)")
                || generated.ChineseDescription.Contains("你的格挡不会在回合开始时移除", StringComparison.Ordinal)
                || generated.ChineseDescription.Contains("获得等同于目标易伤层数的力量", StringComparison.Ordinal)
                || generated.ChineseDescription.Contains("当此牌被消耗时", StringComparison.Ordinal))
                throw new InvalidOperationException($"中文描述未通过原版文风检查：{generated.ChineseDescription}");
            for (var operationIndex = 0; operationIndex < generated.Operations.Count; operationIndex++)
            {
                if (!CardEffectRules.IsCurrentBlockDamageModifier(generated.Operations[operationIndex])) continue;
                var anchorIndex = CardEffectRules.CurrentBlockDamageAnchorIndex(generated.Operations,
                    operationIndex);
                if (anchorIndex < 0
                    || !generated.Operations[anchorIndex].ChineseText.Contains("造成0点伤害", StringComparison.Ordinal)
                    || generated.ChineseDescription.Contains("造成0点伤害", StringComparison.Ordinal))
                    throw new InvalidOperationException("当前格挡伤害没有使用隐藏的零值伤害锚点："
                                                        + generated.ChineseDescription);
            }
        }

        var catalog = CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad);
        var assembler = new ComponentAssemblyGenerator(new Random(20260819));
        ResourceEconomyModel.Validate();
        EnergyCostTuning.Validate();
        CardAcceptanceTuning.Validate();
        AggressiveModeTuning.Validate();
        var goldAxeCount = new GeneratorOperation("CL:ForEachCardPlayedCombat", OperationScope.Modifier,
            "本场战斗每打出一张牌，", new Dictionary<string, int>());
        var randomZeroCost = new GeneratorOperation("CL:AddRandomZeroCostCardsToHand",
            OperationScope.NonTargeted, "将1张随机0费牌加入手牌。", new Dictionary<string, int>());
        var damageThirteen = new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly,
            "造成13点伤害。", new Dictionary<string, int>(), RequiresSingleTarget: true);
        var runawayRandomZeroCost = new List<GeneratorOperation>
            { damageThirteen, goldAxeCount, randomZeroCost };
        var runawayRandomZeroCostValue = EffectBalanceModel.EstimatedPositiveCardValue(
            runawayRandomZeroCost, true, GeneratedCardType.Attack, []);
        var aggressiveRareTwoCostMaximum = ComponentAssemblyGenerator.WholeCardBudgetBounds(
            GeneratedRarity.Rare, 2d, 2, balancedValues: false,
            character: GeneratedCharacter.Colorless).Maximum;
        if (Math.Abs(EffectBalanceModel.RelativeTriggerFrequency(goldAxeCount) - 20d) > 0.001d
            || EffectBalanceModel.EstimatedEffectValue(randomZeroCost) != EffectBalanceModel.RandomZeroCostCardValue
            || runawayRandomZeroCostValue <= aggressiveRareTwoCostMaximum
            || ComponentAssemblyGenerator.ApplyWholeCardBudgetEnvelope(runawayRandomZeroCost,
                GeneratedRarity.Rare, 2d, GeneratedCardType.Attack, [], true,
                balancedValues: false, GeneratedCharacter.Colorless, ultimateChaos: false))
            throw new InvalidOperationException("累计出牌生成随机0费牌没有按金斧/大奖的频率与价值拒绝超模组合。");

        var nextTurn = new GeneratorOperation("R:NextTurn", OperationScope.ConditionalTrigger,
            "下个回合开始时。", new Dictionary<string, int>());
        var starCardCount = new GeneratorOperation("R:ForEachStarCostCard", OperationScope.Modifier,
            "你的所有牌中每有一张有蓝星耗费的牌，",
            new Dictionary<string, int> { ["triggerIndex"] = 0 });
        var enemiesLoseStrength = new GeneratorOperation("R:EnemiesLoseStrengthThisTurn",
            OperationScope.NonTargeted, "本回合所有敌人失去1点力量。",
            new Dictionary<string, int> { ["triggerIndex"] = 0 });
        var replaySkill = new GeneratorOperation("R:PlaySelectedSkillMultipleTimes",
            OperationScope.NonTargeted, "选择手牌中的一张技能牌，将其打出1次。",
            new Dictionary<string, int>());
        var gainThreeStars = new GeneratorOperation("R:GainStars", OperationScope.NonTargeted,
            "获得3颗蓝星。", new Dictionary<string, int>());
        var blockOne = new GeneratorOperation("N:B", OperationScope.NonTargeted,
            "获得1点格挡。", new Dictionary<string, int>());
        var runawayRegentBasic = new List<GeneratorOperation>
            { replaySkill, gainThreeStars, blockOne, nextTurn, starCardCount, enemiesLoseStrength };
        var runawayRegentEffectiveCost = ResourceEconomyModel.BudgetEffectiveCost(1, 0, false, false,
            runawayRegentBasic);
        if (Math.Abs(EffectBalanceModel.RelativeTriggerFrequency(starCardCount) - 5d) > 0.001d
            || ComponentAssemblyGenerator.ApplyWholeCardBudgetEnvelope(runawayRegentBasic,
                GeneratedRarity.Basic, runawayRegentEffectiveCost, GeneratedCardType.Skill, [], true,
                balancedValues: false, GeneratedCharacter.Regent, ultimateChaos: false))
            throw new InvalidOperationException("蓝星耗能牌计数没有按至少5张折算，或超模储君基础牌通过了激进上界。");
        var balancedExtraProbe = new RandomCardGenerator(GeneratedCharacter.Ironclad, 20261001,
            balancedValues: true);
        if (Enumerable.Range(0, 80).Select(_ => balancedExtraProbe.Generate(GeneratedRarity.Common))
            .Any(card => card.Operations.Any(operation =>
                operation.Parameters.GetValueOrDefault("aggressiveBonus") == 1)))
            throw new InvalidOperationException("数值平衡模式出现了已废弃的激进追加效果标记。");
        // The old post-budget appended line has been removed entirely. Aggressive density now comes from the
        // pre-assembly rarity floor, so no generated operation may retain the legacy marker.
        var aggressiveCards = Enumerable.Range(0, 4).SelectMany(poolIndex =>
        {
            var aggressiveExtraProbe = new RandomCardGenerator(GeneratedCharacter.Ironclad,
                20261002 + poolIndex, balancedValues: false);
            var poolRarities = Enumerable.Repeat(GeneratedRarity.Basic, 10)
                .Concat(Enumerable.Repeat(GeneratedRarity.Common, 20))
                .Concat(Enumerable.Repeat(GeneratedRarity.Uncommon, 35))
                .Concat(Enumerable.Repeat(GeneratedRarity.Rare, 25))
                .Concat(Enumerable.Repeat(GeneratedRarity.Ancient, 2));
            return poolRarities.Select(aggressiveExtraProbe.Generate);
        }).ToArray();
        if (aggressiveCards.Any(card => card.Operations.Any(operation =>
                operation.Parameters.ContainsKey("aggressiveBonus")
                || operation.Parameters.ContainsKey("aggressiveBonusBaseEffectCount"))))
            throw new InvalidOperationException("数值激进模式仍生成了已废弃的事后追加效果。");
        var multiplicityProbe = new RandomCardGenerator(GeneratedCharacter.Regent, 20261006,
            balancedValues: false);
        var multiplicityPool = Enumerable.Repeat(GeneratedRarity.Basic, 10)
            .Concat(Enumerable.Repeat(GeneratedRarity.Common, 20))
            .Concat(Enumerable.Repeat(GeneratedRarity.Uncommon, 35))
            .Concat(Enumerable.Repeat(GeneratedRarity.Rare, 25))
            .Concat(Enumerable.Repeat(GeneratedRarity.Ancient, 2))
            .Select(multiplicityProbe.Generate).ToArray();
        if (!ComponentPolicy.TryAuditPool(multiplicityPool, out var multiplicityFailure))
            throw new InvalidOperationException("生成卡池违反组件作用域：" + multiplicityFailure);
        var repeatedPoolUnique = new GeneratorOperation("R:KingsSwordHitsAllEnemies",
            OperationScope.AbilityRule, "君王之剑对所有敌人造成伤害。", new Dictionary<string, int>());
        var repeatedPoolUniqueCards = new[]
        {
            multiplicityPool[0] with { Operations = [repeatedPoolUnique] },
            multiplicityPool[1] with { Operations = [repeatedPoolUnique] }
        };
        if (ComponentPolicy.TryAuditPool(repeatedPoolUniqueCards, out _))
            throw new InvalidOperationException("卡池唯一组件的完成卡池审计未拒绝第二次出现。");
        var declaredPoolUniqueKeys = Enum.GetValues<GeneratedCharacter>()
            .SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms)
            .Select(ComponentPolicy.PoolUniqueKey)
            .Where(key => key is not null).ToHashSet(StringComparer.Ordinal);
        var requiredPoolUniqueKeys = new[]
        {
            "transform:all_hand_attacks", "clear:all_non_attack_hand_exhaust", "clear:all_hand_exhaust",
            "rule:played_skills_gain_sly", "rule:derivative_hits_all",
            "target:remove_all_block_and_artifact", "value:block_equal_all_poison",
            "rule:kings_sword_hits_all", "action:play_selected_skill_multiple_times",
            "target:vulnerable_double", "target:vulnerable_weak_double", "target:copy_debuffs_to_others",
            "action:draw_discard_nonzero", "action:shuffle_unexhausted", "rule:skills_cost_zero",
            "rule:skills_exhaust", "rule:buffer", "rule:no_block_from_cards",
            "rule:die_on_unblocked_attack", "reward:gain_gold", "turn:free_hand",
            "damage:cards_played_combat"
        };
        if (requiredPoolUniqueKeys.Any(key => !declaredPoolUniqueKeys.Contains(key)))
            throw new InvalidOperationException("请求的卡池唯一组件缺少稳定语义键："
                + string.Join(", ", requiredPoolUniqueKeys.Where(key => !declaredPoolUniqueKeys.Contains(key))));
        var freeHandAtom = Enum.GetValues<GeneratedCharacter>()
            .SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms)
            .First(atom => atom.Template == "I:FreeHandThisTurn");
        if (freeHandAtom.Multiplicity != ComponentMultiplicity.UniquePerPool
            || ComponentPolicy.PoolUniqueKey(freeHandAtom) != "turn:free_hand")
            throw new InvalidOperationException("手牌中所有牌免费打出必须保持为卡池唯一效果。");
        var multiplicityNecrobinderCatalog = CharacterComponentCatalogs.Get(GeneratedCharacter.Necrobinder);
        var debilitateRecipe = multiplicityNecrobinderCatalog.Recipes.Single(recipe => recipe.Id == "Debilitate");
        if (debilitateRecipe.Atoms.Count != 2
            || debilitateRecipe.Atoms[1].Template != "NCR:DoubleVulnerableWeak"
            || debilitateRecipe.Atoms[1].Multiplicity != ComponentMultiplicity.UniquePerPool)
            throw new InvalidOperationException("摧残的持续易伤/虚弱翻倍必须保持为一个卡池唯一的完整效果。");
        var lethalityRecipe = multiplicityNecrobinderCatalog.Recipes.Single(recipe => recipe.Id == "Lethality");
        if (lethalityRecipe.Atoms.Count != 2
            || lethalityRecipe.Atoms[0].Template != "NCR:FirstAttackPlayedEachTurn"
            || lethalityRecipe.Atoms[1].Template != "M:TriggeredAttackDamagePercent"
            || lethalityRecipe.TriggerOwners.Count != 2 || lethalityRecipe.TriggerOwners[1] != 0
            || EffectBalanceModel.EstimatedEffectValue(lethalityRecipe.Atoms[1]) != 450)
            throw new InvalidOperationException("致死性的首张攻击触发器与50%事件攻击增伤 modifier 拆分或定价失效。");
        var allCatalogAtoms = Enum.GetValues<GeneratedCharacter>()
            .SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms).ToArray();
        var everyAttackTrigger = allCatalogAtoms.First(atom => atom.Template == "CL:WheneverAttackPlayed");
        var afterAttackDamageTrigger = allCatalogAtoms.First(atom => atom.Template == "A:whenAttackDealsDamage");
        if (!CardEffectRules.SuppliesEventAttackForDamageModifier(lethalityRecipe.Atoms[0])
            || !CardEffectRules.SuppliesEventAttackForDamageModifier(everyAttackTrigger)
            || CardEffectRules.SuppliesEventAttackForDamageModifier(afterAttackDamageTrigger))
            throw new InvalidOperationException("事件攻击增伤 modifier 未能交换到合法攻击触发器，或错误接到了伤害结算后的触发器。");
        var shivInFormerStatusSlot = new GeneratorOperation("R:FillHandWithDebris", OperationScope.NonTargeted,
            "将小刀加入你的手牌，直至手牌已满。", new Dictionary<string, int>(), DerivativeId: "shiv");
        var slimeInStatusSlot = new GeneratorOperation("D:CreateSlimeInDiscard", OperationScope.NonTargeted,
            "将一张黏液加入弃牌堆。", new Dictionary<string, int>(), DerivativeId: "slimed");
        if (DerivativeSlotCatalog.ProducesStatus(shivInFormerStatusSlot)
            || CardEffectRules.IsNegativeEffect(shivInFormerStatusSlot)
            || !DerivativeSlotCatalog.ProducesStatus(slimeInStatusSlot)
            || !CardEffectRules.IsNegativeEffect(slimeInStatusSlot))
            throw new InvalidOperationException("衍生物产出类型仍错误沿用了源插槽的状态牌分类。");
        ComponentAssemblyGenerator.ValidateNumericBudgetMonotonicity();
        for (var rank = 0; rank <= 4; rank++)
        {
            var rareWeight = ComponentAssemblyGenerator.ComponentCountTopRarityNonPowerWeight(
                GeneratedRarity.Rare, GeneratedCardType.Attack, rank);
            var ancientWeight = ComponentAssemblyGenerator.ComponentCountTopRarityNonPowerWeight(
                GeneratedRarity.Ancient, GeneratedCardType.Skill, rank);
            if ((rank == 0 && (rareWeight != 100 || ancientWeight != 100))
                || (rank > 0 && (rareWeight <= 100 || ancientWeight < rareWeight))
                || ComponentAssemblyGenerator.ComponentCountTopRarityNonPowerWeight(
                    GeneratedRarity.Rare, GeneratedCardType.Power, rank) != 100)
                throw new InvalidOperationException("稀有/先古攻击与技能的效果数必须平滑右移，能力牌不得受此加成。");
        }
        if (ComponentAssemblyGenerator.ComponentCountRarityWeight(GeneratedRarity.Basic, 0) != 70
            || ComponentAssemblyGenerator.ComponentCountRarityWeight(GeneratedRarity.Basic, 1) != 125
            || ComponentAssemblyGenerator.ComponentCountRarityWeight(GeneratedRarity.Basic, 2)
                >= ComponentAssemblyGenerator.ComponentCountRarityWeight(GeneratedRarity.Basic, 1)
            || ComponentAssemblyGenerator.ComponentCountRarityWeight(GeneratedRarity.Common, 2)
                >= ComponentAssemblyGenerator.ComponentCountRarityWeight(GeneratedRarity.Common, 1)
            || ComponentAssemblyGenerator.ComponentCountRarityWeight(GeneratedRarity.Uncommon, 3)
                >= ComponentAssemblyGenerator.ComponentCountRarityWeight(GeneratedRarity.Uncommon, 2)
            || ComponentAssemblyGenerator.ComponentCountRarityWeight(GeneratedRarity.Rare, 2)
                <= ComponentAssemblyGenerator.ComponentCountRarityWeight(GeneratedRarity.Rare, 1)
            || ComponentAssemblyGenerator.ComponentCountRarityWeight(GeneratedRarity.Ancient, 2)
                <= ComponentAssemblyGenerator.ComponentCountRarityWeight(GeneratedRarity.Rare, 2)
            || ComponentAssemblyGenerator.ComponentCountPowerWeight(GeneratedCardType.Power, 3) >= 100
            || ComponentAssemblyGenerator.ComponentCountPowerWeight(GeneratedCardType.Skill, 3) != 100)
            throw new InvalidOperationException("效果条数没有满足低稀有度以1-2条、高稀有度以1-3条为主且能力牌略少的平滑分布。 ");
        var repeatedFieldProbe = new[]
        {
            new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得5点格挡。", new Dictionary<string, int>()),
            new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得8点格挡。", new Dictionary<string, int>()),
            new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得11点格挡。", new Dictionary<string, int>())
        };
        if (CardEffectRules.HasAtMostTwoOfEachField(repeatedFieldProbe)
            || !CardEffectRules.HasAtMostTwoOfEachField(repeatedFieldProbe.Take(2).ToArray()))
            throw new InvalidOperationException("同一数值字段的单卡出现次数上限必须为2。 ");
        var runawayDamageGrowthProbe = new[]
        {
            new GeneratorOperation("C:untilTurnEnd", OperationScope.ConditionalTrigger,
                "本回合每当你受到一次攻击时。", new Dictionary<string, int>()),
            new GeneratorOperation("N:RetaliateDamage", OperationScope.NonTargeted,
                "对攻击者造成16点伤害。", new Dictionary<string, int> { ["triggerIndex"] = 0 }),
            new GeneratorOperation("N:HP-", OperationScope.NonTargeted,
                "失去1点生命。", new Dictionary<string, int>()),
            new GeneratorOperation("C:untilTurnEnd", OperationScope.ConditionalTrigger,
                "本回合每当你打出一张攻击牌时。", new Dictionary<string, int>()),
            new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly,
                "造成15点伤害。", new Dictionary<string, int> { ["triggerIndex"] = 3 },
                RequiresSingleTarget: true),
            new GeneratorOperation("I:IncreaseDamageThisCombat", OperationScope.Independent,
                "在本场战斗中，此卡的基础伤害增加7点。",
                new Dictionary<string, int> { ["triggerIndex"] = 3 })
        };
        if (CardEffectRules.HasNoRepeatedTriggeredCombatDamageGrowth(runawayDamageGrowthProbe)
            || EffectBalanceModel.PositiveRewardFieldCount(runawayDamageGrowthProbe) != 1
            || EffectBalanceModel.EstimatedPositiveCardValue(runawayDamageGrowthProbe) < 10_000d)
            throw new InvalidOperationException("重复触发的本场伤害成长没有被拒绝，或多套伤害仍在重复领取整卡预算。 ");
        var evokeDamageProbe = new List<GeneratorOperation>
        {
            new("D:EvokeRightmostOrb", OperationScope.NonTargeted,
                "激发最右侧的充能球2次。", new Dictionary<string, int>()),
            new("T:D", OperationScope.SingleEnemyOnly, "造成11点伤害。", new Dictionary<string, int>(),
                RequiresSingleTarget: true)
        };
        var evokeDamageBounds = ComponentAssemblyGenerator.WholeCardBudgetBounds(
            GeneratedRarity.Common, 1d, 1, balancedValues: true, character: GeneratedCharacter.Defect);
        if (EffectBalanceModel.PositiveRewardFieldCount(evokeDamageProbe) != 1
            || EffectBalanceModel.EstimatedPositiveCardValue(evokeDamageProbe) <= evokeDamageBounds.Maximum
            || !ComponentAssemblyGenerator.ApplyWholeCardBudgetEnvelope(evokeDamageProbe,
                GeneratedRarity.Common, 1d, GeneratedCardType.Attack, Array.Empty<CardTag>(), true, true,
                GeneratedCharacter.Defect, false)
            || EffectBalanceModel.EstimatedPositiveCardValue(evokeDamageProbe) > evokeDamageBounds.Maximum)
            throw new InvalidOperationException("激发与直接伤害没有共享主要输出预算，或超越释放模板未被压回普通牌范围。 ");
        var curseEasterEggUpperBoundProbe = new List<GeneratorOperation>
        {
            new("D:CreateDazedInDiscard", OperationScope.NonTargeted, "将1张笨拙放入弃牌堆。",
                new Dictionary<string, int>(), DerivativeId: "curse_clumsy"),
            new("T:D", OperationScope.SingleEnemyOnly, "造成100点伤害。", new Dictionary<string, int>(),
                RequiresSingleTarget: true)
        };
        var curseEasterEggValueBeforeEnvelope = EffectBalanceModel.EstimatedPositiveCardValue(
            curseEasterEggUpperBoundProbe, true, GeneratedCardType.Attack, Array.Empty<CardTag>());
        if (!ComponentAssemblyGenerator.ApplyWholeCardBudgetEnvelope(curseEasterEggUpperBoundProbe,
                GeneratedRarity.Common, 1d, GeneratedCardType.Attack, Array.Empty<CardTag>(), true, true,
                GeneratedCharacter.Ironclad, false, skipUpperBound: true)
            || EffectBalanceModel.EstimatedPositiveCardValue(curseEasterEggUpperBoundProbe,
                true, GeneratedCardType.Attack, Array.Empty<CardTag>()) != curseEasterEggValueBeforeEnvelope)
            throw new InvalidOperationException("状态牌诅咒彩蛋仍被整卡数值上界压缩。 ");
        var repeatedDamageBudgetProbe = new List<GeneratorOperation>
        {
            new("T:D", OperationScope.SingleEnemyOnly, "造成11点伤害。", new Dictionary<string, int>(),
                RequiresSingleTarget: true),
            new("N:B", OperationScope.NonTargeted, "获得10点格挡。", new Dictionary<string, int>()),
            new("T:D", OperationScope.SingleEnemyOnly, "造成11点伤害。", new Dictionary<string, int>(),
                RequiresSingleTarget: true)
        };
        var repeatedDamageFields = EffectBalanceModel.PositiveRewardFieldCount(repeatedDamageBudgetProbe);
        var repeatedDamageBounds = ComponentAssemblyGenerator.WholeCardBudgetBounds(
            GeneratedRarity.Uncommon, 1d, repeatedDamageFields, 150, balancedValues: true);
        var repeatedDamageBefore = EffectBalanceModel.EstimatedPositiveCardValue(repeatedDamageBudgetProbe);
        if (repeatedDamageFields != 2
            || repeatedDamageBefore <= repeatedDamageBounds.Maximum
            || !ComponentAssemblyGenerator.ApplyWholeCardBudgetEnvelope(repeatedDamageBudgetProbe,
                GeneratedRarity.Uncommon, 1d, GeneratedCardType.Attack, [CardTag.Exhaust], true, true,
                GeneratedCharacter.Ironclad, false)
            || EffectBalanceModel.EstimatedPositiveCardValue(repeatedDamageBudgetProbe)
                > repeatedDamageBounds.Maximum)
            throw new InvalidOperationException("重复伤害字段没有共享整卡预算，或整卡预算包络未压回目标范围。 ");
        var expectedOneCostBands = new Dictionary<GeneratedRarity, (int Minimum, int Maximum)>
        {
            [GeneratedRarity.Basic] = (6, 8),
            [GeneratedRarity.Common] = (10, 16),
            [GeneratedRarity.Uncommon] = (12, 19),
            [GeneratedRarity.Rare] = (16, 26),
            [GeneratedRarity.Ancient] = (20, 35)
        };
        foreach (var (bandRarity, expectedBand) in expectedOneCostBands)
        {
            var bounds = ComponentAssemblyGenerator.WholeCardBudgetBounds(
                bandRarity, 1d, 1, balancedValues: true);
            if ((int)Math.Round(bounds.Minimum / 100d) != expectedBand.Minimum
                || (int)Math.Round(bounds.Maximum / 100d) != expectedBand.Maximum)
                throw new InvalidOperationException($"{bandRarity}的一费单收益整卡预算带偏离设定范围。 ");
        }
        if (Math.Abs(ComponentAssemblyGenerator.BalancedUpperBoundMultiplier - 1.15d) > 0.0001d)
            throw new InvalidOperationException("数值平衡模式的整卡上界没有保持15%统一提升。");
        foreach (var bandRarity in Enum.GetValues<GeneratedRarity>())
        {
            var oneField = ComponentAssemblyGenerator.WholeCardBudgetBounds(
                bandRarity, 1d, 1, balancedValues: true);
            var threeFields = ComponentAssemblyGenerator.WholeCardBudgetBounds(
                bandRarity, 1d, 3, balancedValues: true);
            if (oneField != threeFields)
                throw new InvalidOperationException($"{bandRarity}仍会因收益字段数量获得额外整卡预算。 ");
        }
        if (ComponentAssemblyGenerator.CalibratedWholeCardCenter(GeneratedRarity.Basic, 0d) != 410d
            || ComponentAssemblyGenerator.CalibratedWholeCardCenter(GeneratedRarity.Common, 1d) != 1_200d
            || ComponentAssemblyGenerator.CalibratedWholeCardCenter(GeneratedRarity.Uncommon, 2d) != 3_010d
            || ComponentAssemblyGenerator.CalibratedWholeCardCenter(GeneratedRarity.Rare, 0d) != 1_360d
            || ComponentAssemblyGenerator.CalibratedWholeCardCenter(GeneratedRarity.Rare, 3d) != 6_460d
            || ComponentAssemblyGenerator.CalibratedWholeCardCenter(GeneratedRarity.Ancient, 4d) != 11_160d)
            throw new InvalidOperationException("原版卡池校准的费用/稀有度整卡中心发生了意外变化。 ");
        if (Math.Abs(ComponentAssemblyGenerator.MinimumPlayablePositiveValue(0d) - 330d) > 0.001d
            || Math.Abs(ComponentAssemblyGenerator.MinimumPlayablePositiveValue(1d) - 600d) > 0.001d
            || Math.Abs(ComponentAssemblyGenerator.MinimumPlayablePositiveValue(2d) - 1_290d) > 0.001d
            || Math.Abs(ComponentAssemblyGenerator.MinimumPlayablePositiveValue(3d) - 2_040d) > 0.001d
            || Math.Abs(ComponentAssemblyGenerator.MinimumPlayablePositiveValue(4d) - 2_790d) > 0.001d)
            throw new InvalidOperationException("整卡最低效率没有随统一有效费用曲线增长。 ");
        var weakDamageProbe = new List<GeneratorOperation>
        {
            new("T:D", OperationScope.SingleEnemyOnly, "造成1点伤害。", new Dictionary<string, int>(),
                RequiresSingleTarget: true)
        };
        var weakBlockProbe = new List<GeneratorOperation>
        {
            new("N:B", OperationScope.NonTargeted, "获得1点格挡。", new Dictionary<string, int>())
        };
        if (!ComponentAssemblyGenerator.ApplyWholeCardBudgetEnvelope(weakDamageProbe,
                GeneratedRarity.Basic, 1d, GeneratedCardType.Attack, Array.Empty<CardTag>(), true, true,
                GeneratedCharacter.Ironclad, false)
            || OperationRuntimeSpecCompiler.PrimaryStaticLiteralValue(weakDamageProbe[0], 0) < 6
            || !ComponentAssemblyGenerator.ApplyWholeCardBudgetEnvelope(weakBlockProbe,
                GeneratedRarity.Basic, 1d, GeneratedCardType.Skill, Array.Empty<CardTag>(), true, true,
                GeneratedCharacter.Ironclad, false)
            || OperationRuntimeSpecCompiler.PrimaryStaticLiteralValue(weakBlockProbe[0], 0) < 5)
            throw new InvalidOperationException("一费卡整卡强度下限低于打6或防5。 ");
        var postSynergyWeakProbe = new List<GeneratorOperation>
        {
            new("T:D", OperationScope.SingleEnemyOnly, "造成2点伤害。", new Dictionary<string, int>(),
                RequiresSingleTarget: true),
            new("N:B", OperationScope.NonTargeted, "获得1点格挡。", new Dictionary<string, int>())
        };
        var postSynergyBaselineProbe = new List<GeneratorOperation>
        {
            new("T:D", OperationScope.SingleEnemyOnly, "造成3点伤害。", new Dictionary<string, int>(),
                RequiresSingleTarget: true),
            new("N:B", OperationScope.NonTargeted, "获得3点格挡。", new Dictionary<string, int>())
        };
        if (ComponentAssemblyGenerator.HasMinimumPlayableWholeCardValue(postSynergyWeakProbe, 1d,
                GeneratedCardType.Attack, Array.Empty<CardTag>(), true)
            || !ComponentAssemblyGenerator.HasMinimumPlayableWholeCardValue(postSynergyBaselineProbe, 1d,
                GeneratedCardType.Attack, Array.Empty<CardTag>(), true))
            throw new InvalidOperationException("协同惩罚后的整卡价值没有遵守打6/防5通用下限。 ");
        var commonBlockFiveProbe = new List<GeneratorOperation>
        {
            new("N:B", OperationScope.NonTargeted, "获得2点格挡。", new Dictionary<string, int>()),
            new("N:B", OperationScope.NonTargeted, "获得3点格挡。", new Dictionary<string, int>())
        };
        var commonBlockNineProbe = new List<GeneratorOperation>
        {
            new("N:B", OperationScope.NonTargeted, "获得4点格挡。", new Dictionary<string, int>()),
            new("N:B", OperationScope.NonTargeted, "获得5点格挡。", new Dictionary<string, int>())
        };
        if (ComponentAssemblyGenerator.HasPostSynergyWholeCardBudgetFloor(commonBlockFiveProbe,
                GeneratedRarity.Common, 1d, GeneratedCardType.Skill, Array.Empty<CardTag>(), true, true,
                GeneratedCharacter.Ironclad, false)
            || !ComponentAssemblyGenerator.HasPostSynergyWholeCardBudgetFloor(commonBlockNineProbe,
                GeneratedRarity.Common, 1d, GeneratedCardType.Skill, Array.Empty<CardTag>(), true, true,
                GeneratedCharacter.Ironclad, false))
            throw new InvalidOperationException("一费普通牌未在协同结算后遵守自身稀有度下限。 ");
        var guardsAtom = CharacterComponentCatalogs.Get(GeneratedCharacter.Regent).Recipes
            .Single(recipe => recipe.Id == "Guards").Atoms.Single();
        var corruptionRecipe = CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad).Recipes
            .Single(recipe => recipe.Id == "Corruption");
        var corruptionRule = corruptionRecipe.Atoms
            .Single(atom => OperationRuntimeSpecCompiler.GetOrCompile(atom).Variant == "skills_cost_zero");
        var corruptionOperations = corruptionRecipe.Atoms.Select((atom, index) => new GeneratorOperation(
            atom.Template, atom.Scope, string.Empty,
            corruptionRecipe.TriggerOwners[index] < 0
                ? new Dictionary<string, int>()
                : new Dictionary<string, int> { ["triggerIndex"] = corruptionRecipe.TriggerOwners[index] },
            RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom))).ToArray();
        var copyTargetDebuffs = CharacterComponentCatalogs.Get(GeneratedCharacter.Necrobinder).Recipes
            .Single(recipe => recipe.Id == "Misery").Atoms
            .Single(atom => atom.Template == "NCR:CopyTargetDebuffsToOthers");
        var kingsSwordHitsAll = CharacterComponentCatalogs.Get(GeneratedCharacter.Regent).Recipes
            .Single(recipe => recipe.Id == "SeekingEdge").Atoms
            .Single(atom => atom.Template == "R:KingsSwordHitsAllEnemies");
        var attacksToBoulders = CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad).Recipes
            .Single(recipe => recipe.Id == "PrimalForce").Atoms.Single();
        var sovereignBladeBlock = CharacterComponentCatalogs.Get(GeneratedCharacter.Regent).Recipes
            .Single(recipe => recipe.Id == "Parry").Atoms.Single();
        var gainGold = CharacterComponentCatalogs.Get(GeneratedCharacter.Colorless).Recipes
            .Single(recipe => recipe.Id == "HandOfGreed").Atoms
            .Single(CardEffectRules.IsGoldGainOperation);
        var retaliateDamage = new ComponentAtom("N:RetaliateDamage", OperationScope.NonTargeted,
            "对攻击者造成6点伤害。", false, CardReferenceRequirement.None);
        if (EffectBalanceModel.EstimatedEffectValue(guardsAtom) != 3_200
            || EffectBalanceModel.EstimatedEffectValue(corruptionRule)
                != EffectBalanceModel.SkillsCostZeroRuleValue
            || EffectBalanceModel.EstimatedEffectValue(corruptionRule)
                <= 5 * EffectBalanceModel.OrdinaryEnergyValuePerPoint * 3
            || Math.Abs(CardEffectRules.NegativeEffectLinearCompensationValue(corruptionOperations) - 2_262d)
                > 0.001d
            || EffectBalanceModel.EstimatedEffectValue(kingsSwordHitsAll) != 1_700
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(guardsAtom, GeneratedRarity.Rare) != 70
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(guardsAtom, GeneratedRarity.Common) != 22
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(corruptionRule, GeneratedRarity.Rare) != 70
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(corruptionRule, GeneratedRarity.Common) != 22
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(copyTargetDebuffs,
                GeneratedRarity.Rare) != 70
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(copyTargetDebuffs,
                GeneratedRarity.Common) != 22
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(kingsSwordHitsAll,
                GeneratedRarity.Common) != 12
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(kingsSwordHitsAll,
                GeneratedRarity.Rare) != 45
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(attacksToBoulders,
                GeneratedRarity.Common) != 22
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(attacksToBoulders,
                GeneratedRarity.Rare) != 70
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(sovereignBladeBlock,
                GeneratedRarity.Common) != 22
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(sovereignBladeBlock,
                GeneratedRarity.Rare) != 70
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(gainGold,
                GeneratedRarity.Common) != 12
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(gainGold,
                GeneratedRarity.Rare) != 45
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(retaliateDamage,
                GeneratedRarity.Basic) != 10
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(retaliateDamage,
                GeneratedRarity.Common) != 22
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(retaliateDamage,
                GeneratedRarity.Uncommon) != 45
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(retaliateDamage,
                GeneratedRarity.Rare) != 70)
            throw new InvalidOperationException("变化衍生物、所有技能牌0费、君王之剑群攻、获得金币、复制目标负面状态或受击反伤没有使用稀有效果管线。");
        var overrepresentedUtilities = new ComponentAtom[]
        {
            new("A:when", OperationScope.AbilityTrigger, "每当你在回合内失去生命时。", false,
                CardReferenceRequirement.None),
            new("N:DiscardAll", OperationScope.NonTargeted, "丢弃所有手牌。", false,
                CardReferenceRequirement.None),
            new("I:DrawWithRetain", OperationScope.Independent, "抽2张牌。这些牌在本回合获得保留。", false,
                CardReferenceRequirement.None),
            new("R:FillHandWithDebris", OperationScope.NonTargeted, "将碎屑加入手牌，直到手牌已满。", false,
                CardReferenceRequirement.None),
            new("A:ProxyAtomic_ForbiddenGrimoire", OperationScope.AbilityRule,
                "在战斗结束时，你可以从你的牌组中选一张牌移除。", false, CardReferenceRequirement.None),
            new("I:ProxyAtomic_Transfigure", OperationScope.Independent,
                "给一张手牌添加重放。其耗能增加1。", false, CardReferenceRequirement.HandCard),
            new("A:ruleShivsRetain", OperationScope.AbilityRule, "小刀获得保留。", false,
                CardReferenceRequirement.None)
        };
        if (overrepresentedUtilities.Any(atom => !ComponentAssemblyGenerator.IsVeryRareOperation(atom)
                || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                    atom, GeneratedRarity.Uncommon) != 28
                || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                    atom, GeneratedRarity.Rare) != 45))
            throw new InvalidOperationException("高重复感的整手/保留/移牌/重放组件没有进入统一极稀有效果权重。");
        var loseFocusProbe = new GeneratorOperation("D:LoseFocus", OperationScope.NonTargeted,
            "失去2点集中。", new Dictionary<string, int>());
        var loseOrbSlotProbe = new GeneratorOperation("D:LoseOrbSlots", OperationScope.NonTargeted,
            "失去1个充能球栏位。", new Dictionary<string, int>());
        if (!CardEffectRules.IsNegativeEffect(loseFocusProbe)
            || !CardEffectRules.IsPermanentNegativeEffect(loseFocusProbe)
            || !CardEffectRules.IsNegativeEffect(loseOrbSlotProbe)
            || !CardEffectRules.IsPermanentNegativeEffect(loseOrbSlotProbe)
            || !ComponentAssemblyGenerator.IsExplicitRareTemplate(loseFocusProbe.Template)
            || !ComponentAssemblyGenerator.IsExplicitRareTemplate(loseOrbSlotProbe.Template))
            throw new InvalidOperationException("永久失去集中或充能球栏位没有被同时认定为稀有效果、负面和永久负面效果。");
        var conditionalDamageProbe = new[]
        {
            new GeneratorOperation("C:ifTargetVulnerable", OperationScope.ConditionalTrigger,
                "如果目标敌人拥有易伤。", new Dictionary<string, int>()),
            new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly, "造成9点伤害。",
                new Dictionary<string, int> { ["triggerIndex"] = 0 }, RequiresSingleTarget: true)
        };
        if (CardEffectRules.HasAttackClassifyingDamage(conditionalDamageProbe)
            || !CardEffectRules.HasAttackClassifyingDamage(
                [.. conditionalDamageProbe, conditionalDamageProbe[1] with
                {
                    ChineseText = "造成6点伤害。", Parameters = new Dictionary<string, int>()
                }]))
            throw new InvalidOperationException("仅由条件触发的伤害应归类为技能，另有直接伤害时仍应归类为攻击。");
        var randomGeneration = new GeneratorOperation("CL:AddRandomAttackToHand", OperationScope.NonTargeted,
            "将一张随机攻击牌加入手牌。", new Dictionary<string, int>());
        ExternalOperationTextRegistry.Register(randomGeneration.Template, randomGeneration.ChineseText,
            "Add a random Attack to your hand.");
        var randomGenerationUpgrade = new CardUpgradeEffect(CardUpgradeKind.UpgradeGeneratedCards, 0);
        var upgradedRandomGeneration = CardUpgradeGenerator.ApplyEffectsToOperations(
            [randomGeneration], [randomGenerationUpgrade])[0];
        if (!upgradedRandomGeneration.ChineseText.Contains("升级过的随机攻击牌", StringComparison.Ordinal)
            || !EnglishCardDescriptionRenderer.OperationText(upgradedRandomGeneration)
                .Contains("random upgraded Attack", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("随机生成牌的升级选项没有同步更新中英文操作文本。");
        var currentCharacterGeneration = new GeneratorOperation("N:CreateCurrentCharacterCardInHand",
            OperationScope.NonTargeted, "将一张当前角色的随机牌加入手牌。", new Dictionary<string, int>());
        var upgradedCurrentCharacterGeneration = CardUpgradeGenerator.ApplyEffectsToOperations(
            [currentCharacterGeneration], [randomGenerationUpgrade])[0];
        if (!CardEffectRules.IsRandomCurrentCharacterCardToHand(currentCharacterGeneration)
            || !CardEffectRules.IsRandomCurrentCharacterCardToHand(upgradedCurrentCharacterGeneration)
            || !upgradedCurrentCharacterGeneration.ChineseText.Contains("升级过的随机牌", StringComparison.Ordinal))
            throw new InvalidOperationException("当前角色随机牌的升级文本破坏了稳定的执行路由。");
        var repeatedXKindProbe = new[]
        {
            new GeneratorOperation("T:DX", OperationScope.SingleEnemyOnly, "造成3点伤害X次。",
                new Dictionary<string, int>()),
            // Deliberately use another internal template: the rule is based on player-visible effect shape.
            new GeneratorOperation("T:D_EnergyX", OperationScope.SingleEnemyOnly, "造成8点伤害X次。",
                new Dictionary<string, int>())
        };
        var swappedXSlotProbe = new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly,
            "造成X点伤害3次。", new Dictionary<string, int>
            {
                [SpecialXCardConverter.Parameter] = SpecialXCardConverter.EnergyResource,
                [SpecialXCardConverter.ValueMaskParameter] = 1
            });
        if (CardEffectRules.HasNoDuplicateXEffectKinds(repeatedXKindProbe)
            || !CardEffectRules.HasNoDuplicateXEffectKinds([repeatedXKindProbe[0], swappedXSlotProbe]))
            throw new InvalidOperationException("相同形态的X费效果必须互斥，但X位于不同参数槽时应当兼容。 ");
        var duplicateNoDrawProbe = new[]
        {
            new GeneratorOperation("I:PreventDrawThisTurn", OperationScope.Independent,
                "你在本回合内不能再抽牌。", new Dictionary<string, int>()),
            new GeneratorOperation("I:PreventDrawThisTurn", OperationScope.Independent,
                "你在本回合内不能再抽牌。", new Dictionary<string, int> { ["triggerIndex"] = 0 })
        };
        var distinctCardUniqueProbe = new[]
        {
            duplicateNoDrawProbe[0],
            new GeneratorOperation("N:DiscardAll", OperationScope.NonTargeted,
                "丢弃所有手牌。", new Dictionary<string, int>())
        };
        if (CardEffectRules.HasNoDuplicateCardUniqueEffects(duplicateNoDrawProbe)
            || !CardEffectRules.HasNoDuplicateCardUniqueEffects(distinctCardUniqueProbe))
            throw new InvalidOperationException("整卡唯一效果必须在不同触发归属下仍拒绝重复，同时不应互斥不同效果。 ");
        var noDrawCandidate = new ComponentAtom("I:PreventDrawThisTurn", OperationScope.Independent,
            "本回合不能再抽牌。", false, CardReferenceRequirement.None);
        if (!CardEffectRules.WouldDuplicateCardUniqueEffect([duplicateNoDrawProbe[0]], noDrawCandidate)
            || CardEffectRules.WouldDuplicateCardUniqueEffect([distinctCardUniqueProbe[1]], noDrawCandidate))
            throw new InvalidOperationException("整卡唯一效果没有在候选组件进入数值与预算流程之前正确拒绝重复。 ");
        var retainHandAtom = new ComponentAtom("A:ruleRetainHand", OperationScope.AbilityRule,
            "在你的回合结束时，不再丢弃你的手牌。", false, CardReferenceRequirement.None);
        var kingsSwordHitsAllAtom = new ComponentAtom("R:KingsSwordHitsAllEnemies", OperationScope.AbilityRule,
            "君王之剑对所有敌人造成伤害。", false, CardReferenceRequirement.None);
        if (retainHandAtom.Multiplicity != ComponentMultiplicity.UniquePerPool
            || kingsSwordHitsAllAtom.Multiplicity != ComponentMultiplicity.UniquePerPool
            || noDrawCandidate.Multiplicity != ComponentMultiplicity.SinglePerCard
            || new ComponentAtom("T:D", OperationScope.SingleEnemyOnly, "造成7点伤害。", true,
                CardReferenceRequirement.None).Multiplicity != ComponentMultiplicity.Repeatable)
            throw new InvalidOperationException("组件的卡内不可叠加与卡池唯一作用域没有被明确区分。");
        var dazedSlotAtom = CharacterComponentCatalogs.Get(GeneratedCharacter.Defect).Atoms
            .First(atom => atom.Template == "D:CreateDazedInDiscard");
        if (ComponentPolicy.ReplaceableSlotOccurrenceWeight(dazedSlotAtom) != 110
            || ComponentPolicy.ReplaceableSlotOccurrenceWeight(new ComponentAtom("T:D",
                OperationScope.SingleEnemyOnly, "造成7点伤害。", true,
                CardReferenceRequirement.None)) != 100)
            throw new InvalidOperationException("带可替换衍生物/充能球槽位的组件没有获得统一的小幅出率增益。");
        var returnThisToHandProbe = new GeneratorOperation("R:ReturnThisToHand", OperationScope.Independent,
            "将这张牌放回你的手牌。", new Dictionary<string, int>());
        var delayedReturnThisToHandProbe = new GeneratorOperation("R:ReturnAfterSkillsPlayed",
            OperationScope.Independent, "将这张牌从弃牌堆放回你的手牌。", new Dictionary<string, int>());
        var putThisOnDrawProbe = new GeneratorOperation("R:PutThisOnDraw", OperationScope.Independent,
            "将这张牌放置于你的抽牌堆顶部。", new Dictionary<string, int>());
        var putThisOnDrawAtom = new ComponentAtom("R:PutThisOnDraw", OperationScope.Independent,
            "将这张牌放置于你的抽牌堆顶部。", false, CardReferenceRequirement.None);
        if (CardEffectRules.HasNoDuplicateCardUniqueEffects(
                [returnThisToHandProbe, delayedReturnThisToHandProbe])
            || CardEffectRules.HasNoDuplicateCardUniqueEffects([returnThisToHandProbe, putThisOnDrawProbe])
            || !CardEffectRules.WouldDuplicateCardUniqueEffect([returnThisToHandProbe], putThisOnDrawAtom))
            throw new InvalidOperationException("本卡回手与本卡置于抽牌堆顶部必须整卡唯一且彼此互斥。 ");
        var blockAtom = new ComponentAtom("N:B", OperationScope.NonTargeted, "获得5点格挡。", false,
            CardReferenceRequirement.None);
        var drawAtom = new ComponentAtom("N:Draw", OperationScope.NonTargeted, "抽1张牌。", false,
            CardReferenceRequirement.None);
        var damageAtom = new ComponentAtom("T:D", OperationScope.SingleEnemyOnly, "造成7点伤害。", true,
            CardReferenceRequirement.None);
        var permanentStrengthAtom = new ComponentAtom("N:Self", OperationScope.NonTargeted, "获得2点力量。", false,
            CardReferenceRequirement.None);
        var temporaryStrengthAtom = permanentStrengthAtom with { ChineseText = "本回合获得2点力量。" };
        var starGainAtom = new ComponentAtom("R:GainStars", OperationScope.NonTargeted, "获得2蓝星。", false,
            CardReferenceRequirement.None);
        var permanentFocusAtom = new ComponentAtom("D:GainFocus", OperationScope.NonTargeted, "获得3点集中。", false,
            CardReferenceRequirement.None);
        var permanentFocusLossAtom = new ComponentAtom("D:LoseFocus", OperationScope.NonTargeted,
            "失去1点集中。", false, CardReferenceRequirement.None);
        var healAtom = new ComponentAtom("N:Heal", OperationScope.NonTargeted, "回复8点生命。", false,
            CardReferenceRequirement.None);
        var orbSlotGainAtom = new ComponentAtom("D:GainOrbSlots", OperationScope.NonTargeted,
            "获得3个充能球栏位。", false, CardReferenceRequirement.None);
        var conditionalTrigger = new GeneratorOperation("C:ifTargetVulnerable", OperationScope.ConditionalTrigger,
            "如果该敌人拥有易伤。", new Dictionary<string, int>());
        var highFrequencyTrigger = new GeneratorOperation("NCR:WheneverCardPlayedThisTurn",
            OperationScope.ConditionalTrigger, "本回合每当你打出一张牌时。", new Dictionary<string, int>());
        var turnStartTrigger = new GeneratorOperation("A:turnStart", OperationScope.AbilityTrigger,
            "在你的回合开始时。", new Dictionary<string, int>());
        var energyGainAtom = new ComponentAtom("N:E", OperationScope.NonTargeted, "获得1点能量。", false,
            CardReferenceRequirement.None);
        var delayedEnergyAtom = new ComponentAtom("N:NextTurnEnergy", OperationScope.NonTargeted,
            "在下个回合，获得1点能量。", false, CardReferenceRequirement.None);
        var nativeFourEnergyAtom = new ComponentAtom("NCR:GainEnergy", OperationScope.NonTargeted,
            "获得4点能量。", false, CardReferenceRequirement.None);
        var createCardAtom = new ComponentAtom("N:CreateShiv", OperationScope.NonTargeted,
            "将1张小刀加入手牌。", false, CardReferenceRequirement.None);
        var discardAtom = new ComponentAtom("N:Discard", OperationScope.NonTargeted, "丢弃1张牌。", false,
            CardReferenceRequirement.None);
        var grantSlyAtom = new ComponentAtom("I:GrantSlyToHandSkillThisTurn", OperationScope.Independent,
            "在本回合给手牌中的一张技能牌添加奇巧。", false, CardReferenceRequirement.None);
        var ostyDamageAtom = new ComponentAtom("NCR:OstyDamage", OperationScope.SingleEnemyOnly,
            "奥斯提造成9点伤害。", true, CardReferenceRequirement.None);
        var delayedBlockAtom = new ComponentAtom("N:NextTurnBlock", OperationScope.NonTargeted,
            "在下个回合，获得5点格挡。", false, CardReferenceRequirement.None);
        var firstPlayTrigger = new GeneratorOperation("NCR:IfFirstPlayThisTurn",
            OperationScope.ConditionalTrigger, "如果这是本回合第一次打出此牌。",
            new Dictionary<string, int>());
        var firstPlayDamage = new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly,
            "造成8点伤害。", new Dictionary<string, int> { ["triggerIndex"] = 0 },
            RequiresSingleTarget: true);
        var unconditionalBlock = new GeneratorOperation("N:B", OperationScope.NonTargeted,
            "获得4点格挡。", new Dictionary<string, int>());
        var fatalTrigger = new GeneratorOperation("C:ifFatal", OperationScope.ConditionalTrigger,
            "斩杀时。", new Dictionary<string, int>());
        var doubleVulnerableAtom = CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad).Recipes
            .Single(recipe => recipe.Id == "MoltenFist").Atoms
            .Single(CardEffectRules.IsDoubleTargetVulnerable);
        var linkedFatalDoubleVulnerable = new GeneratorOperation(doubleVulnerableAtom.Template,
            doubleVulnerableAtom.Scope, "Double Vulnerable.",
            new Dictionary<string, int> { ["triggerIndex"] = 1 }, RequiresSingleTarget: true,
            RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(doubleVulnerableAtom));
        if (CardEffectRules.HasValidFailableConditionAssembly([firstPlayTrigger, firstPlayDamage])
            || !CardEffectRules.HasValidFailableConditionAssembly(
                [unconditionalBlock, firstPlayTrigger, firstPlayDamage with
                {
                    Parameters = new Dictionary<string, int> { ["triggerIndex"] = 1 }
                }])
            || Math.Abs(EffectBalanceModel.RelativeTriggerFrequency(firstPlayTrigger) - 0.82d) > 0.0001d)
            throw new InvalidOperationException("第一次打出条件必须另有无条件收益，并按低于一次计价。 ");
        if (Math.Abs(EffectBalanceModel.RelativeTriggerFrequency(fatalTrigger)
                     - EffectBalanceModel.FatalTriggerFrequency) > 0.0001d
            || Math.Abs(EffectBalanceModel.FatalTriggerFrequency - 0.18d) > 0.0001d)
            throw new InvalidOperationException("斩杀条件没有使用统一的低频触发折算。 ");
        if (CardEffectRules.HasNoFatalDoubleVulnerablePayoff(
                [firstPlayDamage with { Parameters = new Dictionary<string, int>() }, fatalTrigger,
                    linkedFatalDoubleVulnerable])
            || !CardEffectRules.HasNoFatalDoubleVulnerablePayoff(
                [firstPlayDamage with { Parameters = new Dictionary<string, int>() }, fatalTrigger,
                    linkedFatalDoubleVulnerable with { Parameters = new Dictionary<string, int>() }]))
            throw new InvalidOperationException("斩杀时不能连接将目标易伤翻倍的后续效果。");
        if (EffectSelectionTuning.RepeatedFamilyWeightBasisPoints([blockAtom], []) != 10_000
            || EffectSelectionTuning.RepeatedFamilyWeightBasisPoints([blockAtom],
                repeatedFieldProbe.Take(1).ToArray()) != 2_000
            || EffectSelectionTuning.RepeatedFamilyWeightBasisPoints([blockAtom],
                repeatedFieldProbe.Take(2).ToArray()) != 400
            || EffectSelectionTuning.RepeatedFamilyWeightBasisPoints([blockAtom],
                repeatedFieldProbe.Take(3).ToArray()) != 80
            || EffectSelectionTuning.RepeatedVariantWeight(blockAtom, repeatedFieldProbe.Take(1).ToArray()) != 15
            || EffectSelectionTuning.RepeatedVariantWeight(drawAtom, repeatedFieldProbe) != 100
            || EffectSelectionTuning.PowerAuxiliaryWeight([damageAtom], GeneratedCardType.Power, []) != 10
            || EffectSelectionTuning.PowerAuxiliaryWeight([temporaryStrengthAtom], GeneratedCardType.Power, []) != 10
            || EffectSelectionTuning.PowerAuxiliaryWeight([energyGainAtom], GeneratedCardType.Power, []) != 100
            || EffectSelectionTuning.PowerAuxiliaryWeight([damageAtom], GeneratedCardType.Power,
                [turnStartTrigger]) != 100
            || EffectSelectionTuning.RestrictedRunGrowthAnchorWeight([blockAtom], GeneratedCardType.Power, [],
                GeneratedCharacter.Defect, false) != 50
            || EffectSelectionTuning.RestrictedRunGrowthAnchorWeight([damageAtom], GeneratedCardType.Power, [],
                GeneratedCharacter.Necrobinder, false) != 27
            || EffectSelectionTuning.RestrictedRunGrowthAnchorWeight([damageAtom], GeneratedCardType.Power, [],
                GeneratedCharacter.Necrobinder, true) != 100)
            throw new InvalidOperationException("同一张牌已经拥有的效果没有按家族与精确模板递减抽取权重。 ");
        if (EffectBalanceModel.EstimatedEffectValue(healAtom) != 4_000
            || !ComponentAssemblyGenerator.IsExplicitRareOperation(healAtom)
            || EffectBalanceModel.TriggerOccurrenceWeight([nativeFourEnergyAtom]) != 100
            || NumericGenerationTuning.ClampSampledValue(permanentFocusAtom, 0, 3, []) != 2
            || NumericGenerationTuning.ClampSampledValue(orbSlotGainAtom, 0, 3, []) != 1
            || NumericGenerationTuning.ClampSampledValue(
                new ComponentAtom("N:Self", OperationScope.NonTargeted,
                    "获得1层覆甲。", false, CardReferenceRequirement.None), 0, 1, []) != 3
            || NumericGenerationTuning.ClampSampledValue(
                new ComponentAtom("I:ProxyAtomic_Quasar", OperationScope.NonTargeted,
                    "从9张随机无色牌中选择1张加入你的手牌。", false, CardReferenceRequirement.None),
                0, 9, []) != 4)
            throw new InvalidOperationException("直接伤害、永久属性或生命效果的全局选择权重/价值失效。 ");
        var basicDamageRandom = new Random(20260914);
        var basicDamageValues = Enumerable.Range(0, 20_000)
            .Select(_ => EffectBalanceModel.ScaleRewardCenter(damageAtom, 6, 1,
                GeneratedRarity.Basic, [], basicDamageRandom)).ToArray();
        var basicBlockRandom = new Random(20260915);
        var basicBlockValues = Enumerable.Range(0, 20_000)
            .Select(_ => EffectBalanceModel.ScaleRewardCenter(blockAtom, 5, 1,
                GeneratedRarity.Basic, [], basicBlockRandom)).ToArray();
        var damageAboveRate = basicDamageValues.Count(value => value > 6) / (double)basicDamageValues.Length;
        var blockAboveRate = basicBlockValues.Count(value => value > 5) / (double)basicBlockValues.Length;
        if (damageAboveRate is < 0.28 or > 0.32 || blockAboveRate is < 0.28 or > 0.32
            || basicDamageValues.Min() != 5 || basicDamageValues.Max() != 7
            || basicBlockValues.Min() != 4 || basicBlockValues.Max() != 6)
            throw new InvalidOperationException("基础牌数值没有保持在打击5-7、格挡4-6的紧凑预算带。 ");
        if (EffectSelectionTuning.TriggeredResourceGainWeight([energyGainAtom], [highFrequencyTrigger]) != 2
            || EffectSelectionTuning.TriggeredCardCreationWeight([createCardAtom], [highFrequencyTrigger]) != 4
            || EffectSelectionTuning.TriggeredPermanentDownsideWeight([permanentFocusLossAtom],
                [highFrequencyTrigger]) != 3
            || EffectSelectionTuning.TriggeredPermanentDownsideWeight([permanentFocusLossAtom],
                [turnStartTrigger]) != 100)
            throw new InvalidOperationException("高频资源、卡牌产出或永久负面效果的组合限制失效。 ");
        var vulnerableAtom = new ComponentAtom("T:Apply", OperationScope.SingleEnemyOnly,
            "给予9层易伤。", true, CardReferenceRequirement.None);
        var weakAtom = vulnerableAtom with { ChineseText = "给予9层虚弱。" };
        var temporaryEnemyStrengthAtom = new ComponentAtom("T:Apply", OperationScope.SingleEnemyOnly,
            "使该敌人本回合失去12点力量。", true, CardReferenceRequirement.None);
        var temporaryAllEnemyStrengthAtom = new ComponentAtom("N:AllTempStrengthLoss", OperationScope.NonTargeted,
            "使所有敌人本回合失去12点力量。", false, CardReferenceRequirement.None);
        var percentageDamageReductionAtom = new ComponentAtom("C:untilTurnEnd",
            OperationScope.ConditionalTrigger,
            "在本回合中，有易伤状态的敌人对你造成的伤害降低150%。", false,
            CardReferenceRequirement.None);
        var excessiveShivCountAtom = new ComponentAtom("N:CreateShiv", OperationScope.NonTargeted,
            "将20张小刀加入手牌。", false, CardReferenceRequirement.None);
        var excessiveTransformCountAtom = new ComponentAtom("CL:TransformSelectedHandCards",
            OperationScope.NonTargeted, "变化手牌中的20张牌。", false, CardReferenceRequirement.HandCard);
        var excessiveNextTurnDrawAtom = new ComponentAtom("N:NextTurnDraw", OperationScope.NonTargeted,
            "在下个回合抽20张牌。", false, CardReferenceRequirement.None);
        var unrelatedCardThresholdAtom = new ComponentAtom("CL:EveryCardsDrawn",
            OperationScope.AbilityTrigger, "你每抽20张牌。", false, CardReferenceRequirement.None);
        var tenShivs = new GeneratorOperation(excessiveShivCountAtom.Template,
            excessiveShivCountAtom.Scope, "将10张小刀加入手牌。", new Dictionary<string, int>(),
            RuntimeSpec: OperationRuntimeSpecCompiler.CompileLegacy(new GeneratorOperation(
                excessiveShivCountAtom.Template, excessiveShivCountAtom.Scope, "将10张小刀加入手牌。",
                new Dictionary<string, int>())));
        var upgradedTenShivs = CardUpgradeGenerator.ApplyEffectsToOperations([tenShivs],
            [new CardUpgradeEffect(CardUpgradeKind.IncreaseNumber, 0, 4, ValueSlotId: "amount")]);
        if (NumericGenerationTuning.ClampSampledValue(vulnerableAtom, 0, 9, [],
                GeneratedCharacter.Silent) != 4
            || NumericGenerationTuning.ClampSampledValue(vulnerableAtom, 0, 9, [],
                GeneratedCharacter.Ironclad) != 6
            || NumericGenerationTuning.ClampSampledValue(vulnerableAtom, 0, 9, [],
                GeneratedCharacter.Silent, ultimateChaos: true) != 6
            || NumericGenerationTuning.ClampSampledValue(weakAtom, 0, 9, [],
                GeneratedCharacter.Ironclad) != 4
            || NumericGenerationTuning.ClampSampledValue(temporaryEnemyStrengthAtom, 0, 12, []) != 9
            || NumericGenerationTuning.ClampSampledValue(temporaryAllEnemyStrengthAtom, 0, 12, []) != 6
            || NumericGenerationTuning.ClampSampledValue(percentageDamageReductionAtom, 0, 150, []) != 95
            || NumericGenerationTuning.ClampSampledValue(excessiveShivCountAtom, 0, 20, []) != 10
            || NumericGenerationTuning.ClampSampledValue(excessiveTransformCountAtom, 0, 20, []) != 10
            || NumericGenerationTuning.ClampSampledValue(excessiveNextTurnDrawAtom, 0, 20, []) != 10
            || NumericGenerationTuning.ClampSampledValue(unrelatedCardThresholdAtom, 0, 20, []) != 20
            || OperationRuntimeSpecCompiler.FixedValue(upgradedTenShivs[0], "amount") != 10)
            throw new InvalidOperationException("持续层数、百分比减伤或牌张数的全局数值上限失效。 ");
        var returnToHand = new GeneratorOperation("R:ReturnThisToHand", OperationScope.Independent,
            "将此牌放回手牌。", new Dictionary<string, int>());
        var gainOneEnergy = new GeneratorOperation("N:E", OperationScope.NonTargeted,
            "获得1点能量。", new Dictionary<string, int>());
        if (CardEffectRules.HasValidReturnThisToHandCost(0, -1, false, [returnToHand])
            || !CardEffectRules.HasValidReturnThisToHandCost(1, -1, false, [returnToHand])
            || !CardEffectRules.HasValidReturnThisToHandCost(0, 2, false, [returnToHand])
            || CardEffectRules.HasValidReturnThisToHandCost(-1, -1, false, [returnToHand])
            || CardEffectRules.HasValidReturnThisToHandCost(0, -1, true, [returnToHand])
            || CardEffectRules.HasValidReturnThisToHandCost(-1, -1, true, [returnToHand])
            || CardEffectRules.HasValidReturnThisToHandCost(-1, 1, false, [returnToHand])
            || CardEffectRules.HasValidReturnThisToHandCost(1, -1, true, [returnToHand])
            || CardEffectRules.HasValidReturnThisToHandCost(1, -1, false,
                [gainOneEnergy, returnToHand])
            || !CardEffectRules.HasValidReturnThisToHandCost(2, -1, false,
                [gainOneEnergy, returnToHand]))
            throw new InvalidOperationException("返回手牌组件没有正确要求折合费用大于0。 ");
        var paidReturnOperations = new[]
        {
            new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得7点格挡。",
                new Dictionary<string, int>()),
            returnToHand
        };
        var oneCostReturnCard = new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Uncommon, CardDescriptionRenderer.Render(paidReturnOperations),
            Array.Empty<CardTag>(), paidReturnOperations, Character: GeneratedCharacter.Regent);
        var oneStarReturnCard = oneCostReturnCard with { Cost = 0, StarCost = 1 };
        var energyXOneStarReturnCard = oneCostReturnCard with { Cost = -1, StarCost = 1 };
        var oneCostStarXReturnCard = oneCostReturnCard with { HasStarCostX = true };
        if (Enumerable.Range(0, 500).Any(seed => CardUpgradeGenerator.Generate(oneCostReturnCard,
                    new Random(seed)).Effects.Any(effect => effect.Kind == CardUpgradeKind.ReduceCost))
            || Enumerable.Range(0, 500).Any(seed => CardUpgradeGenerator.Generate(oneStarReturnCard,
                    new Random(seed)).Effects.Any(effect => effect.Kind == CardUpgradeKind.ReduceStarCost))
            || Enumerable.Range(0, 500).Any(seed => CardUpgradeGenerator.Generate(energyXOneStarReturnCard,
                    new Random(seed)).Effects.Any(effect => effect.Kind == CardUpgradeKind.ReduceStarCost))
            || Enumerable.Range(0, 500).Any(seed => CardUpgradeGenerator.Generate(oneCostStarXReturnCard,
                    new Random(seed), unifiedChaos: true).Effects.Any(effect => effect.Kind == CardUpgradeKind.ReduceCost)))
            throw new InvalidOperationException("返回手牌组件仍能通过升级失去最后一项固定普通费用或蓝星支付。 ");
        if (!CardUpgradeGenerator.ShouldAddLowNumericSecondary(CardUpgradeKind.IncreaseNumber, 0)
            || !CardUpgradeGenerator.ShouldAddLowNumericSecondary(CardUpgradeKind.GrantRetain, 49)
            || CardUpgradeGenerator.ShouldAddLowNumericSecondary(CardUpgradeKind.IncreaseNumber, 50)
            || CardUpgradeGenerator.ShouldAddLowNumericSecondary(CardUpgradeKind.ReduceCost, 0)
            || CardUpgradeGenerator.ShouldAddLowNumericSecondary(CardUpgradeKind.ReduceStarCost, 0))
            throw new InvalidOperationException("唯一非费用升级的50%低数值追加规则失效。 ");
        var retainPowerOperations = new[]
        {
            new GeneratorOperation("N:Str", OperationScope.NonTargeted, "获得2点力量。",
                new Dictionary<string, int>())
        };
        var paidSilentPower = new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other,
            GeneratedRarity.Rare, CardDescriptionRenderer.Render(retainPowerOperations), [],
            retainPowerOperations, Character: GeneratedCharacter.Silent);
        var freeSilentPower = paidSilentPower with { Cost = 0 };
        if (!Enumerable.Range(0, 500).Any(seed => GeneratedCardTagPolicy.AddedKeywords(
                    CardUpgradeGenerator.Generate(paidSilentPower, new Random(seed))).Contains(CardTag.Retain))
            || Enumerable.Range(0, 500).Any(seed => GeneratedCardTagPolicy.AddedKeywords(
                    CardUpgradeGenerator.Generate(freeSilentPower, new Random(seed))).Contains(CardTag.Retain)))
            throw new InvalidOperationException("能力牌获得保留的路径未开放，或折合0费牌仍能升级获得保留。 ");
        var upgradeDamage = new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly,
            "造成8点伤害。", new Dictionary<string, int>());
        var staticRepeat = new GeneratorOperation("M:repeat", OperationScope.Modifier,
            "这张牌额外造成2次伤害。", new Dictionary<string, int>());
        var handSkillRepeat = new GeneratorOperation("M:RepeatPerSkillInHand", OperationScope.Modifier,
            "手牌中每有一张技能牌，就造成一次伤害。", new Dictionary<string, int>());
        GeneratedCard UpgradeProbe(IReadOnlyList<GeneratorOperation> operations) => new(1,
            GeneratedCardType.Attack, TargetMode.SingleEnemy, GeneratedRarity.Uncommon,
            CardDescriptionRenderer.Render(operations), Array.Empty<CardTag>(), operations,
            Character: GeneratedCharacter.Silent);
        var singleUpgradeProbe = UpgradeProbe([upgradeDamage]);
        var tripleUpgradeProbe = UpgradeProbe([upgradeDamage, staticRepeat]);
        var flechettesUpgradeProbe = UpgradeProbe([upgradeDamage, handSkillRepeat]);
        var singlePointGain = CardUpgradeGenerator.EstimatedNumericUpgradeGainForAudit(singleUpgradeProbe, 0, 1);
        var triplePointGain = CardUpgradeGenerator.EstimatedNumericUpgradeGainForAudit(tripleUpgradeProbe, 0, 1);
        var flechettesPointGain = CardUpgradeGenerator.EstimatedNumericUpgradeGainForAudit(
            flechettesUpgradeProbe, 0, 1);
        var singleRider = CardUpgradeGenerator.SupplementalNumericEffectForAudit(singleUpgradeProbe, 0);
        var tripleRider = CardUpgradeGenerator.SupplementalNumericEffectForAudit(tripleUpgradeProbe, 0);
        var fittedSingle = CardUpgradeGenerator.FitNumericEffectForAudit(singleUpgradeProbe, 0, 3, 450d);
        var fittedTriple = CardUpgradeGenerator.FitNumericEffectForAudit(tripleUpgradeProbe, 0, 3, 450d);
        var fittedFlechettes = CardUpgradeGenerator.FitNumericEffectForAudit(
            flechettesUpgradeProbe, 0, 3, 450d);
        var fatalUpgradeTrigger = new GeneratorOperation("C:if", OperationScope.ConditionalTrigger,
            "斩杀时。", new Dictionary<string, int>());
        var lowFrequencyDamage = new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly,
            "造成21点伤害。", new Dictionary<string, int> { ["triggerIndex"] = 0 });
        var lowFrequencyUpgradeProbe = UpgradeProbe([fatalUpgradeTrigger, lowFrequencyDamage]);
        var fittedLowFrequency = CardUpgradeGenerator.FitNumericEffectForAudit(
            lowFrequencyUpgradeProbe, 1, 2, 450d);
        if (triplePointGain < singlePointGain * 2.9d || flechettesPointGain < singlePointGain * 2d
            || singleRider?.Delta != 2 || tripleRider?.Delta != 1
            || fittedSingle.Delta != 3 || fittedTriple.Delta != 1 || fittedFlechettes.Delta != 1
            || fittedLowFrequency.Delta != 5
            || CardUpgradeGenerator.SupplementalNumericUpgradeBudget != 200d)
            throw new InvalidOperationException(
                "升级边际价值没有计入静态/动态多段、低频触发，或追加数值升级未遵守固定预算。 ");
        var skillThresholdRandom = new Random(20260911);
        var skillThresholds = Enumerable.Range(0, 20_000)
            .Select(_ => NumericGenerationTuning.SampleSkillsPerReturnThreshold(skillThresholdRandom)).ToArray();
        if (skillThresholds.Any(value => value is < 2 or > 4)
            || skillThresholds.Count(value => value == 3) < skillThresholds.Length * 7 / 10)
            throw new InvalidOperationException("每回合技能牌数量门槛没有以3为中心保持低方差。 ");
        var discardUpgradeOperations = new[]
        {
            new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得7点格挡。",
                new Dictionary<string, int>()),
            new GeneratorOperation("N:Discard", OperationScope.NonTargeted, "丢弃2张牌。",
                new Dictionary<string, int>())
        };
        var silentDiscardCard = new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Uncommon, CardDescriptionRenderer.Render(discardUpgradeOperations),
            Array.Empty<CardTag>(), discardUpgradeOperations,
            EnglishDescription: EnglishCardDescriptionRenderer.Render(discardUpgradeOperations),
            Character: GeneratedCharacter.Silent);
        var silentDiscardPlans = Enumerable.Range(0, 10_000)
            .Select(seed => CardUpgradeGenerator.Generate(silentDiscardCard, new Random(seed))).ToArray();
        if (silentDiscardPlans.Any(plan => plan.Effects.Any(effect => effect.OperationIndex == 1
                && effect.Kind == CardUpgradeKind.ReduceNegativeNumber)))
            throw new InvalidOperationException("猎手牌的升级仍会减少弃牌数量。 ");
        var discardIncreasePlans = silentDiscardPlans.Where(plan => plan.Effects.Any(effect =>
            effect.OperationIndex == 1 && effect.Kind == CardUpgradeKind.IncreaseNumber)).ToArray();
        if (discardIncreasePlans.Length is < 850 or > 1_150
            || discardIncreasePlans.SelectMany(plan => plan.Effects).Where(effect => effect.OperationIndex == 1)
                .Any(effect => effect.Kind != CardUpgradeKind.IncreaseNumber || effect.Delta != 1)
            || !discardIncreasePlans.Any(plan => OperationRuntimeSpecCompiler.StaticLiteralValue(
                CardUpgradeGenerator.ApplyEffectsToOperations(discardUpgradeOperations, plan.Effects)[1],
                "count") == 3))
            throw new InvalidOperationException("猎手强制弃牌没有保持约10%的固定+1额外升级，或错误占用了普通升级逻辑。 ");
        var nonSilentDiscardCard = silentDiscardCard with { Character = GeneratedCharacter.Regent };
        if (Enumerable.Range(0, 2_000).Select(seed => CardUpgradeGenerator.Generate(nonSilentDiscardCard,
                new Random(seed))).Any(plan => plan.Effects.Any(effect => effect.OperationIndex == 1)))
            throw new InvalidOperationException("非猎手角色错误获得了强制弃牌数量升级。 ");

        var drawFourDiscardTwoOperations = new[]
        {
            new GeneratorOperation("N:Draw", OperationScope.NonTargeted, "抽4张牌。",
                new Dictionary<string, int>()),
            new GeneratorOperation("N:Discard", OperationScope.NonTargeted, "丢弃2张牌。",
                new Dictionary<string, int>())
        };
        var drawFourDiscardTwoValue = EffectBalanceModel.EstimatedPositiveCardValue(
            drawFourDiscardTwoOperations);
        var drawFourDiscardTwoMaximum = ComponentAssemblyGenerator.WholeCardBudgetBounds(
            GeneratedRarity.Uncommon, 1d, 1, balancedValues: true,
            character: GeneratedCharacter.Silent).Maximum;
        var drawFourDiscardTwoBudgetProbe = drawFourDiscardTwoOperations.ToArray();
        var drawFourDiscardTwoBudgetAccepted = ComponentAssemblyGenerator.ApplyWholeCardBudgetEnvelope(
                drawFourDiscardTwoBudgetProbe,
                GeneratedRarity.Uncommon, 1d, GeneratedCardType.Skill, [], hasPrintedResourceCost: true,
                balancedValues: true, GeneratedCharacter.Silent, ultimateChaos: false);
        if (drawFourDiscardTwoValue <= drawFourDiscardTwoMaximum
            || drawFourDiscardTwoBudgetAccepted
                && OperationRuntimeSpecCompiler.PrimaryStaticLiteralValue(drawFourDiscardTwoBudgetProbe[0]) == 4
            || OperationRuntimeSpecCompiler.StaticLiteralValue(drawFourDiscardTwoBudgetProbe[1], "count") != 2)
            throw new InvalidOperationException("猎手1费罕见抽4弃2模板仍可原样通过数值平衡模式的整卡预算："
                + $"accepted={drawFourDiscardTwoBudgetAccepted}, value="
                + drawFourDiscardTwoValue + $", maximum={drawFourDiscardTwoMaximum}"
                + $", draw={OperationRuntimeSpecCompiler.PrimaryStaticLiteralValue(drawFourDiscardTwoBudgetProbe[0])}, "
                + $"discard={OperationRuntimeSpecCompiler.StaticLiteralValue(drawFourDiscardTwoBudgetProbe[1], "count")}。 ");
        var drawFourDiscardTwoCard = new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Uncommon, CardDescriptionRenderer.Render(drawFourDiscardTwoOperations), [],
            drawFourDiscardTwoOperations, Character: GeneratedCharacter.Silent);
        var drawUpgradePlans = Enumerable.Range(0, 5_000)
            .Select(seed => CardUpgradeGenerator.Generate(drawFourDiscardTwoCard, new Random(seed))).ToArray();
        var drawFiveDiscardTwoPlan = drawUpgradePlans.FirstOrDefault(plan => plan.Effects.Any(effect =>
                effect.OperationIndex == 0 && effect.Kind == CardUpgradeKind.IncreaseNumber)
            && plan.Effects.All(effect => effect.OperationIndex != 1));
        var drawFiveDiscardTwoUpgraded = drawFiveDiscardTwoPlan is null ? []
            : CardUpgradeGenerator.ApplyEffectsToOperations(drawFourDiscardTwoOperations,
                drawFiveDiscardTwoPlan.Effects);
        if (drawUpgradePlans.SelectMany(plan => plan.Effects).Any(effect => effect.OperationIndex == 0
                && effect.Kind == CardUpgradeKind.IncreaseNumber && effect.Delta != 1)
            || drawFiveDiscardTwoPlan is null || drawFiveDiscardTwoUpgraded.Length != 2
            || OperationRuntimeSpecCompiler.PrimaryStaticLiteralValue(drawFiveDiscardTwoUpgraded[0]) != 5
            || OperationRuntimeSpecCompiler.StaticLiteralValue(drawFiveDiscardTwoUpgraded[1], "count") != 2)
            throw new InvalidOperationException("抽牌升级没有固定为+1，或抽4弃2模板无法生成抽5弃2升级。 ");
        var mandatoryExhaustOperations = new[]
        {
            new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得9点格挡。",
                new Dictionary<string, int>()),
            new GeneratorOperation("N:Exhaust", OperationScope.NonTargeted, "消耗手牌中的2张牌。",
                new Dictionary<string, int>())
        };
        var mandatoryExhaustCard = new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Uncommon, CardDescriptionRenderer.Render(mandatoryExhaustOperations),
            Array.Empty<CardTag>(), mandatoryExhaustOperations,
            EnglishDescription: EnglishCardDescriptionRenderer.Render(mandatoryExhaustOperations),
            Character: GeneratedCharacter.Regent);
        var mandatoryExhaustPlans = Enumerable.Range(0, 500)
            .Select(seed => CardUpgradeGenerator.Generate(mandatoryExhaustCard, new Random(seed))).ToArray();
        if (mandatoryExhaustPlans.Any(plan => plan.Effects.Any(effect => effect.OperationIndex == 1
                && effect.Kind == CardUpgradeKind.IncreaseNumber))
            || !mandatoryExhaustPlans.Any(plan => plan.Effects.Any(effect => effect.OperationIndex == 1
                && effect.Kind == CardUpgradeKind.ReduceNegativeNumber)))
            throw new InvalidOperationException("固定数量的强制消耗没有保持或减少其升级数值。 ");
        var legacyBadExhaustUpgrade = CardUpgradeGenerator.ApplyEffectsToOperations(mandatoryExhaustOperations,
            [new CardUpgradeEffect(CardUpgradeKind.IncreaseNumber, 1, 1)]);
        if (legacyBadExhaustUpgrade[1].ChineseText != mandatoryExhaustOperations[1].ChineseText)
            throw new InvalidOperationException("旧快照仍会把强制消耗数量升级得更高。 ");
        var randomExhaustOperations = new[]
        {
            new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得7点格挡。",
                new Dictionary<string, int>()),
            new GeneratorOperation("N:Exhaust", OperationScope.NonTargeted, "随机消耗手牌中的一张牌。",
                new Dictionary<string, int>())
        };
        var randomExhaustCard = new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Uncommon, CardDescriptionRenderer.Render(randomExhaustOperations), [],
            randomExhaustOperations, EnglishDescription: EnglishCardDescriptionRenderer.Render(randomExhaustOperations),
            Character: GeneratedCharacter.Ironclad);
        var chooseExhaustPlans = Enumerable.Range(0, 500)
            .Select(seed => CardUpgradeGenerator.Generate(randomExhaustCard, new Random(seed)))
            .Where(plan => plan.Effects.Any(effect => effect.Kind == CardUpgradeKind.ChooseExhaust)).ToArray();
        if (chooseExhaustPlans.Length == 0)
            throw new InvalidOperationException("随机消耗手牌效果没有生成“改为选择消耗”的升级选项。");
        var chooseExhaustOperations = CardUpgradeGenerator.ApplyEffectsToOperations(randomExhaustOperations,
            chooseExhaustPlans[0].Effects);
        var chooseExhaustSpec = OperationRuntimeSpecCompiler.GetOrCompile(chooseExhaustOperations[1]);
        if (chooseExhaustSpec is not { Opcode: "exhaust_card", Variant: "selected", Target: "selected_card" }
            || !chooseExhaustSpec.Flags.Contains("requires_player_choice")
            || !CardDescriptionRenderer.Render(chooseExhaustOperations).Contains("选择你手牌中的一张牌，将其消耗",
                StringComparison.Ordinal)
            || !EnglishCardDescriptionRenderer.Render(chooseExhaustOperations).Contains("Choose a card in your Hand to Exhaust",
                StringComparison.Ordinal))
            throw new InvalidOperationException("随机消耗升级没有同步修改结构化执行规格或双语描述。");
        var randomAttackExhaust = new GeneratorOperation("I:ExhaustRandomAttack", OperationScope.Independent,
            "随机消耗手牌中的一张攻击牌。", new Dictionary<string, int>());
        var addExhaustedDamage = new GeneratorOperation("I:AddExhaustedAttackDamage", OperationScope.Independent,
            "将它的伤害添加给这张牌。", new Dictionary<string, int>());
        var chosenAttackAssembly = CardUpgradeGenerator.ApplyEffectsToOperations(
            [randomAttackExhaust, addExhaustedDamage],
            [new CardUpgradeEffect(CardUpgradeKind.ChooseExhaust, 0)]);
        if (OperationRuntimeSpecCompiler.GetOrCompile(chosenAttackAssembly[0]).Variant != "i_exhaustselectedattack"
            || CardDescriptionRenderer.Render(chosenAttackAssembly)
                != "选择你手牌中的一张攻击牌，将其消耗，并将它的伤害添加给这张牌。"
            || EnglishCardDescriptionRenderer.Render(chosenAttackAssembly)
                != "Choose an Attack in your Hand to Exhaust and add its damage to this card.")
            throw new InvalidOperationException("随机消耗攻击牌升级没有同步痛殴组合的执行规格或双语描述。");
        var mandatoryExhaustCountRandom = new Random(20260913);
        var mandatoryExhaustCounts = Enumerable.Range(0, 100_000)
            .Select(_ => NumericGenerationTuning.SampleMandatoryHandExhaustCount(mandatoryExhaustCountRandom))
            .ToArray();
        if (mandatoryExhaustCounts.Any(value => value != 1))
            throw new InvalidOperationException("必须选择消耗手牌的数量没有固定为1。 ");
        var mandatoryDiscardCountRandom = new Random(20260914);
        var mandatoryDiscardCounts = Enumerable.Range(0, 100_000)
            .Select(_ => NumericGenerationTuning.SampleMandatoryDiscardCount(mandatoryDiscardCountRandom))
            .ToArray();
        if (mandatoryDiscardCounts.Any(value => value is < 1 or > 5)
            || mandatoryDiscardCounts.Count(value => value == 1) is < 82_500 or > 85_500
            || mandatoryDiscardCounts.Count(value => value == 2) is < 12_500 or > 15_500
            || mandatoryDiscardCounts.Count(value => value >= 3) is < 1_200 or > 2_800)
            throw new InvalidOperationException("强制弃牌数量没有保持以1为主、2为小概率、高值为极小尾部的分布。 ");
        var slyUpgradeOperations = new[]
        {
            new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly, "造成10点伤害。",
                new Dictionary<string, int>(), RequiresSingleTarget: true)
        };
        var slyUpgradeCard = new GeneratedCard(1, GeneratedCardType.Attack, TargetMode.SingleEnemy,
            GeneratedRarity.Uncommon, CardDescriptionRenderer.Render(slyUpgradeOperations),
            [CardTag.Sly], slyUpgradeOperations,
            EnglishDescription: EnglishCardDescriptionRenderer.Render(slyUpgradeOperations),
            Character: GeneratedCharacter.Silent);
        var slyUpgradeChinese = CardDescriptionRenderer.Render(slyUpgradeOperations);
        var slyUpgradeEnglish = EnglishCardDescriptionRenderer.Render(slyUpgradeOperations);
        var slyStarUpgradeCard = slyUpgradeCard with
        {
            Character = GeneratedCharacter.Regent,
            StarCost = 2,
            UnifiedChaos = true
        };
        if (Enumerable.Range(0, 500).Any(seed => CardUpgradeGenerator.Generate(slyUpgradeCard,
                    new Random(seed)).Effects.Any(effect => effect.Kind == CardUpgradeKind.ReduceCost))
            || Enumerable.Range(0, 500).Any(seed => CardUpgradeGenerator.Generate(slyStarUpgradeCard,
                    new Random(seed), unifiedChaos: true).Effects.Any(effect => effect.Kind
                    is CardUpgradeKind.ReduceCost or CardUpgradeKind.ReduceStarCost)))
            throw new InvalidOperationException("带有奇巧的牌仍会选择普通费用或蓝星费用减少升级。 ");
        var illegalSlyCostUpgrade = new CardUpgradePlan(0,
            [new CardUpgradeEffect(CardUpgradeKind.ReduceCost, Delta: -1)],
            slyUpgradeChinese, Array.Empty<CardTag>(), slyUpgradeEnglish);
        AssertInvalid(slyUpgradeCard with { Upgrade = illegalSlyCostUpgrade });
        var illegalSlyStarUpgrade = new CardUpgradePlan(slyStarUpgradeCard.Cost,
            [new CardUpgradeEffect(CardUpgradeKind.ReduceStarCost, Delta: -1)],
            slyUpgradeChinese, Array.Empty<CardTag>(), slyUpgradeEnglish,
            UpgradedStarCost: 1);
        AssertInvalid(slyStarUpgradeCard with { Upgrade = illegalSlyStarUpgrade });
        var almostRefundOperations = new[]
        {
            new GeneratorOperation("N:E", OperationScope.NonTargeted, "获得1点能量。",
                new Dictionary<string, int>())
        };
        var almostRefundCard = new GeneratedCard(2, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Common, CardDescriptionRenderer.Render(almostRefundOperations),
            Array.Empty<CardTag>(), almostRefundOperations,
            EnglishDescription: EnglishCardDescriptionRenderer.Render(almostRefundOperations),
            Character: GeneratedCharacter.Silent);
        if (Enumerable.Range(0, 500).Select(seed => CardUpgradeGenerator.Generate(almostRefundCard,
                    new Random(seed)))
            .Any(plan => plan.Effects.Any(effect => effect.Kind is CardUpgradeKind.ReduceCost
                or CardUpgradeKind.IncreaseNumber)))
            throw new InvalidOperationException("非奇巧牌仍能通过升级变成纯即时自回费模板。 ");
        var returnThresholdOperations = new[]
        {
            new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得7点格挡。",
                new Dictionary<string, int>()),
            new GeneratorOperation("R:ReturnAfterSkillsPlayed", OperationScope.Independent,
                "每打出3张技能牌，将此牌从弃牌堆放入手牌。", new Dictionary<string, int>())
        };
        var returnThresholdCard = new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Rare, CardDescriptionRenderer.Render(returnThresholdOperations),
            Array.Empty<CardTag>(), returnThresholdOperations,
            EnglishDescription: EnglishCardDescriptionRenderer.Render(returnThresholdOperations),
            Character: GeneratedCharacter.Regent);
        var thresholdUpgrade = Enumerable.Range(0, 500)
            .Select(seed => CardUpgradeGenerator.Generate(returnThresholdCard, new Random(seed)))
            .FirstOrDefault(plan => plan.Effects.Any(effect => effect.OperationIndex == 1
                && effect.Kind == CardUpgradeKind.ReduceThreshold));
        if (thresholdUpgrade is null
            || !thresholdUpgrade.UpgradedChineseDescription.Contains("2张技能牌", StringComparison.Ordinal))
            throw new InvalidOperationException("每回合打出技能牌门槛没有在升级时下降。 ");
        if (!OperationRuntimeSpecCompiler.TryReplacePrimaryExplicitFixedValue(returnThresholdOperations[1], 2,
                out var twoSkillReturn))
            throw new InvalidOperationException("无法构造两张技能牌回手门槛测试。 ");
        var twoSkillReturnOperations = new[] { returnThresholdOperations[0], twoSkillReturn };
        var twoSkillReturnCard = returnThresholdCard with
        {
            Operations = twoSkillReturnOperations,
            Upgrade = null
        };
        if (Enumerable.Range(0, 500).Select(seed => CardUpgradeGenerator.Generate(twoSkillReturnCard,
                    new Random(seed)))
            .Any(plan => plan.Effects.Any(effect => effect.OperationIndex == 1
                && effect.Kind == CardUpgradeKind.ReduceThreshold)))
            throw new InvalidOperationException("两张技能牌回手门槛仍能升级为一张。 ");
        var staleOneSkillUpgrade = thresholdUpgrade with
        {
            UpgradedChineseDescription = "获得7点格挡。每打出1张技能牌，将此牌从弃牌堆放入手牌。",
            UpgradedEnglishDescription = "Gain 7 Block. Every 1 Skill you play in a turn, put this into your Hand."
        };
        AssertInvalid(twoSkillReturnCard with { Upgrade = staleOneSkillUpgrade });
        var compatibilityOperations = CardUpgradeGenerator.ApplyEffectsToOperations(twoSkillReturnOperations,
            staleOneSkillUpgrade.Effects);
        if (OperationRuntimeSpecCompiler.FixedValue(compatibilityOperations[1], "amount") != 2)
            throw new InvalidOperationException("旧快照中的一张技能牌回手升级没有被兼容钳制到2。 ");
        if (EffectSelectionTuning.UltimateOstyFamilyWeight([ostyDamageAtom], false) != 100
            || EffectSelectionTuning.UltimateOstyFamilyWeight([ostyDamageAtom], true) != 60
            || EffectSelectionTuning.UltimateOstyFamilyWeight([damageAtom], true) != 100
            || NumericGenerationTuning.ApplyValueBonuses(damageAtom, 10,
                GeneratedCharacter.Necrobinder, false) != 10
            || NumericGenerationTuning.ApplyValueBonuses(damageAtom, 10,
                GeneratedCharacter.Ironclad, false) != 10)
            throw new InvalidOperationException("终极混乱的奥斯提权重或共享数值倍率失效。 ");
        var commonStarCostRandom = new Random(20260902);
        var commonStarCosts = Enumerable.Range(0, 100_000)
            .Select(_ => NumericGenerationTuning.SampleNonBasicFixedStarCost(
                commonStarCostRandom, 7, GeneratedRarity.Common)).ToArray();
        var uncommonStarCostRandom = new Random(20260903);
        var uncommonStarCosts = Enumerable.Range(0, 100_000)
            .Select(_ => NumericGenerationTuning.SampleNonBasicFixedStarCost(
                uncommonStarCostRandom, 5, GeneratedRarity.Uncommon)).ToArray();
        var rareStarCostRandom = new Random(20260905);
        var rareStarCosts = Enumerable.Range(0, 100_000)
            .Select(_ => NumericGenerationTuning.SampleNonBasicFixedStarCost(
                rareStarCostRandom, 7, GeneratedRarity.Rare)).ToArray();
        var commonPositiveStarCosts = commonStarCosts.Where(value => value > 0).Order().ToArray();
        var uncommonPositiveStarCosts = uncommonStarCosts.Where(value => value > 0).Order().ToArray();
        var rarePositiveStarCosts = rareStarCosts.Where(value => value > 0).Order().ToArray();
        var nonBasicStarPaymentRandom = new Random(20260906);
        var sampledNonBasicStarPayments = Enumerable.Range(0, 100_000)
            .Count(_ => NumericGenerationTuning.SampleNonBasicStarPayment(
                nonBasicStarPaymentRandom, usesSharedResourceShell: true, rarity: GeneratedRarity.Uncommon));
        var starXRandom = new Random(20260907);
        var retainedStarX = Enumerable.Range(0, 100_000)
            .Count(_ => NumericGenerationTuning.KeepStarXCost(starXRandom, true, false));
        var ultimateStarXRandom = new Random(20260908);
        var retainedUltimateStarX = Enumerable.Range(0, 100_000)
            .Count(_ => NumericGenerationTuning.KeepStarXCost(ultimateStarXRandom, true, true));
        var basicStarCostRandom = new Random(20260909);
        if (sampledNonBasicStarPayments is < 24_000 or > 26_000
            || NumericGenerationTuning.SampleNonBasicStarPayment(
                new Random(1), usesSharedResourceShell: false, rarity: GeneratedRarity.Uncommon)
            || NumericGenerationTuning.SampleNonBasicStarPayment(
                new Random(1), usesSharedResourceShell: true, rarity: GeneratedRarity.Basic)
            || commonPositiveStarCosts[commonPositiveStarCosts.Length / 2] != 1
            || uncommonPositiveStarCosts[uncommonPositiveStarCosts.Length / 2] != 2
            || rarePositiveStarCosts[rarePositiveStarCosts.Length / 2] != 3
            || commonPositiveStarCosts.Count(value => value == 7) is < 24_000 or > 26_000
            || uncommonPositiveStarCosts.Count(value => value == 5) is < 24_000 or > 26_000
            || rarePositiveStarCosts.Count(value => value == 7) is < 24_000 or > 26_000
            || NumericGenerationTuning.SampleNonBasicFixedStarCost(
                new Random(1), 3, GeneratedRarity.Basic) != 3
            || !Enumerable.Range(0, 10_000).Select(seed =>
                    NumericGenerationTuning.SampleNonBasicFixedStarCost(
                        new Random(seed), 6, GeneratedRarity.Ancient))
                .Any(value => value == 3)
            || retainedStarX is < 91_000 or > 93_000
            || retainedUltimateStarX != 100_000
            || NumericGenerationTuning.ApplyUltimateEnergyCostDiscount(new Random(1), -1, true) != -1
            || NumericGenerationTuning.ApplyUltimateEnergyCostDiscount(new Random(1), 0, true) != 0
            || NumericGenerationTuning.ApplyUltimateEnergyCostDiscount(new Random(1), 1, true) != 1
            || Enumerable.Range(0, 10_000).Select(seed =>
                    NumericGenerationTuning.ApplyUltimateEnergyCostDiscount(new Random(seed), 4, true))
                .Any(cost => cost < 1)
            || Enumerable.Range(0, 100_000)
                .Select(_ => NumericGenerationTuning.ApplyBasicStarCostTuning(
                    basicStarCostRandom, 6, GeneratedRarity.Basic))
                .Count(value => value == 2) is < 26_500 or > 28_700
            || NumericGenerationTuning.ApplyBasicStarCostTuning(
                new Random(1), 1, GeneratedRarity.Common) != 1
            || NumericGenerationTuning.ApplyBasicStarCostTuning(
                new Random(1), -1, GeneratedRarity.Basic) != -1)
            throw new InvalidOperationException("蓝星费用的总体出率或极高费用尾部没有按预期降低。 ");
        var starGainRandom = new Random(20260904);
        var starGainSamples = Enumerable.Range(0, 100_000)
            .Select(_ => NumericGenerationTuning.AdjustSampledValue(starGainRandom, starGainAtom, 0, 3)).ToArray();
        if (starGainSamples.Any(value => value is not (2 or 3))
            || Math.Abs(starGainSamples.Average() - 2.72d) > 0.02d
            || NumericGenerationTuning.AdjustSampledValue(new Random(1), starGainAtom, 0, 1) != 1)
            throw new InvalidOperationException("获得蓝星数值没有按预期小幅下移，或错误降低到了0。 ");
        var starGainCapRandom = new Random(20260908);
        var starGainCapSamples = Enumerable.Range(0, 100_000)
            .Select(_ => NumericGenerationTuning.ApplyUnconditionalStarGainSoftCap(starGainCapRandom, 6))
            .ToArray();
        if (starGainCapSamples.Any(value => value is not (3 or 6))
            || starGainCapSamples.Count(value => value == 3) is < 89_000 or > 91_000
            || NumericGenerationTuning.ApplyUnconditionalStarGainSoftCap(new Random(1), 3) != 3)
            throw new InvalidOperationException("非消耗牌的无条件回蓝没有软限制在3。 ");
        var shuffle = new GeneratorOperation("D:ShuffleAllUnexhaustedIntoDraw", OperationScope.NonTargeted,
            "将所有未消耗的牌洗回抽牌堆。", new Dictionary<string, int>());
        var immediateDraw = new GeneratorOperation("N:Draw", OperationScope.NonTargeted,
            "抽4张牌。", new Dictionary<string, int>());
        if (!CardEffectRules.HasValidShuffleThenDrawAssembly(new[] { shuffle, immediateDraw })
            || CardEffectRules.HasValidShuffleThenDrawAssembly(new[] { shuffle, repeatedFieldProbe[0] })
            || CardEffectRules.HasValidShuffleThenDrawAssembly(new[] { shuffle }))
            throw new InvalidOperationException("洗回未消耗牌的组件必须紧接一项即时抽牌效果。 ");
        var ironcladUnifiedFrequency = new NativeComponentFrequencyTracker(
            CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad, true), true);
        var silentUnifiedFrequency = new NativeComponentFrequencyTracker(
            CharacterComponentCatalogs.Get(GeneratedCharacter.Silent, true), true);
        var ironcladUnifiedBlockPrior = ironcladUnifiedFrequency.SourcePriorWeight(
            GeneratedRarity.Common, GeneratedCardType.Skill, "N:B", []);
        var silentUnifiedBlockPrior = silentUnifiedFrequency.SourcePriorWeight(
            GeneratedRarity.Common, GeneratedCardType.Skill, "N:B", []);
        if (ironcladUnifiedBlockPrior <= 0 || ironcladUnifiedBlockPrior != silentUnifiedBlockPrior
            || NativeComponentFrequencyTracker.AuditSelectionWeight(0.10d, 20, 2) != 100
            || NativeComponentFrequencyTracker.AuditSelectionWeight(0.10d, 20, 6) != 11
            || NativeComponentFrequencyTracker.AuditSelectionWeight(0.10d, 20, 0) != 400
            || NativeComponentFrequencyTracker.AuditSelectionWeight(0.10d, 20, 2, 1) != 45)
            throw new InvalidOperationException("原版组件直接频率先验、混沌加权池或成品分布反馈发生了意外变化。 ");
        var thresholdRandom = new Random(20260831);
        var thresholdSamples = Enumerable.Range(0, 100_000)
            .Select(_ => NumericGenerationTuning.SampleHighCostTriggerThreshold(thresholdRandom)).ToArray();
        if (thresholdSamples.Any(value => value is < 1 or > 3)
            || Math.Abs(thresholdSamples.Count(value => value == 2) / 100_000d - 0.65d) > 0.01)
            throw new InvalidOperationException("高耗能牌触发门槛必须以2为中心且只能取1至3。 ");
        var costIncreaseRandom = new Random(20260901);
        var costIncreaseSamples = Enumerable.Range(0, 1_000_000)
            .Select(_ => NumericGenerationTuning.SampleAllCardsCostIncrease(costIncreaseRandom)).ToArray();
        if (costIncreaseSamples.Any(value => value is < 1 or > 3)
            || costIncreaseSamples.Count(value => value == 1) < 890_000
            || costIncreaseSamples.Count(value => value == 3) is < 1_500 or > 4_500)
            throw new InvalidOperationException("全体牌涨费必须以1为绝对主体，且3只能是极小概率尾部。 ");
        var thresholdOne = new GeneratorOperation("A:whenEnergyCostAtLeast", OperationScope.AbilityTrigger,
            "每当你打出一张耗能大于等于1的牌时。", new Dictionary<string, int>());
        var thresholdTwo = thresholdOne with { ChineseText = "每当你打出一张耗能大于等于2的牌时。" };
        var thresholdThree = thresholdOne with { ChineseText = "每当你打出一张耗能大于等于3的牌时。" };
        if (EffectBalanceModel.PayoffScalePercent([thresholdOne]) != 36
            || EffectBalanceModel.PayoffScalePercent([thresholdTwo]) != 60
            || EffectBalanceModel.PayoffScalePercent([thresholdThree]) != 125)
            throw new InvalidOperationException("高耗能牌触发门槛没有按1弱、2基准、3大幅增强分配预算。 ");
        var necrobinderThresholdThree = thresholdThree with { Template = "NCR:WheneverHighCostCardPlayed" };
        if (EffectBalanceModel.PayoffScalePercent([necrobinderThresholdThree]) != 125)
            throw new InvalidOperationException("同义的高耗能牌触发模板没有共享相同预算。 ");
        var sicEmOperations = new[]
        {
            new GeneratorOperation("NCR:WheneverOstyAttacksTargetThisTurn", OperationScope.Modifier,
                "本回合中，奥斯提每次命中这名敌人时，", new Dictionary<string, int>(), RequiresSingleTarget: true),
            new GeneratorOperation("NCR:ApplyPower_SicEmPower", OperationScope.SingleEnemyOnly,
                "召唤3。", new Dictionary<string, int>(), RequiresSingleTarget: true)
        };
        var sicEmChinese = CardDescriptionRenderer.Render(sicEmOperations);
        var sicEmEnglish = EnglishCardDescriptionRenderer.Render(sicEmOperations);
        if (!sicEmChinese.Contains("奥斯提每次命中这名敌人时，召唤3", StringComparison.Ordinal)
            || !sicEmEnglish.Contains("Osty hits this enemy this turn, summon 3", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"紧追不放触发器必须显示召唤数值且使用命中措辞：{sicEmChinese} | {sicEmEnglish}");
        var legacySicEmOperations = new[]
        {
            sicEmOperations[0] with { ChineseText = "本回合每当奥斯提攻击该敌人时，" },
            sicEmOperations[1] with { ChineseText = "进行召唤。" }
        };
        if (!CardDescriptionRenderer.Render(legacySicEmOperations)
                .Contains("奥斯提每次命中这名敌人时，召唤3", StringComparison.Ordinal)
            || !EnglishCardDescriptionRenderer.Render(legacySicEmOperations)
                .Contains("Osty hits this enemy this turn, summon 3", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("旧快照中的紧追不放组件没有迁移到带数值的新描述。 ");
        var cardCostPenalty = new GeneratorOperation("NCR:IncreaseAllCardCostsThisTurn",
            OperationScope.NonTargeted, "所有牌在本回合耗能增加2。", new Dictionary<string, int>());
        var selfCostPenalty = new GeneratorOperation("D:IncreaseThisCardCost",
            OperationScope.Independent, "这张牌的耗能增加1。", new Dictionary<string, int>());
        var selfCostPenaltyTwo = selfCostPenalty with { ChineseText = "这张牌的耗能增加2。" };
        var selfCostPenaltyThree = selfCostPenalty with { ChineseText = "这张牌的耗能增加3。" };
        var transfigureTradeoff = new GeneratorOperation("I:ProxyAtomic_Transfigure",
            OperationScope.Independent, "给一张手牌添加重放。其耗能增加1。", new Dictionary<string, int>());
        var exhaustOtherCard = new GeneratorOperation("N:Exhaust", OperationScope.NonTargeted,
            "消耗手牌中的一张牌。", new Dictionary<string, int>());
        var exhaustSelectedDrawCard = new GeneratorOperation("NCR:ExhaustSelectedDrawCard",
            OperationScope.NonTargeted, "从抽牌堆中选择一张牌消耗。", new Dictionary<string, int>());
        var exhaustUpToThree = new GeneratorOperation("CL:ExhaustUpToHandCards",
            OperationScope.Independent, "从手牌中选择至多3张牌消耗。", new Dictionary<string, int>());
        var exhaustSelectedTwo = new GeneratorOperation("D:ExhaustSelectedHandCard",
            OperationScope.NonTargeted, "选择手牌中的2张牌消耗。", new Dictionary<string, int>());
        var exhaustSelectedDrawTwo = new GeneratorOperation("NCR:ExhaustSelectedDrawCard",
            OperationScope.NonTargeted, "从抽牌堆中选择2张牌消耗。", new Dictionary<string, int>());
        var triggeredExhaustTwo = new GeneratorOperation("N:Exhaust", OperationScope.NonTargeted,
            "随机消耗手牌中的2张牌。", new Dictionary<string, int> { ["triggerIndex"] = 0 });
        var preventFurtherDraw = new GeneratorOperation("I:PreventDrawThisTurn", OperationScope.Independent,
            "你在本回合内不能再抽牌。", new Dictionary<string, int>());
        var preventTestDraw = new GeneratorOperation("N:Draw", OperationScope.NonTargeted,
            "抽2张牌。", new Dictionary<string, int>());
        var persistentDrawTrigger = new GeneratorOperation("A:turnStart", OperationScope.AbilityTrigger,
            "在你的回合开始时。", new Dictionary<string, int>());
        var persistentTriggeredDraw = preventTestDraw with
        {
            Parameters = new Dictionary<string, int> { ["triggerIndex"] = 1 }
        };
        var twoTurnDrawTrigger = new GeneratorOperation("D:NextTurnsStart", OperationScope.ConditionalTrigger,
            "在接下来的2个回合开始时。", new Dictionary<string, int>());
        var twoTurnTriggeredDraw = preventTestDraw with
        {
            Parameters = new Dictionary<string, int> { ["triggerIndex"] = 1 }
        };
        var oneTurnDrawTrigger = twoTurnDrawTrigger with { ChineseText = "在接下来的1个回合开始时。" };
        if (CardEffectRules.HasValidPreventDrawOrdering([preventFurtherDraw, preventTestDraw])
            || !CardEffectRules.HasValidPreventDrawOrdering([preventTestDraw, preventFurtherDraw])
            || !CardEffectRules.HasValidPreventDrawOrdering(
                [preventFurtherDraw, persistentDrawTrigger, persistentTriggeredDraw])
            || !CardEffectRules.HasValidPreventDrawOrdering(
                [preventFurtherDraw, twoTurnDrawTrigger, twoTurnTriggeredDraw])
            || CardEffectRules.HasValidPreventDrawOrdering(
                [preventFurtherDraw, oneTurnDrawTrigger, twoTurnTriggeredDraw]))
            throw new InvalidOperationException("禁抽组件之后错误地接受了即时抽牌，或拒绝了真正跨多回合的抽牌触发。 ");

        var exhaustAllHand = new GeneratorOperation("N:Exhaust", OperationScope.NonTargeted,
            "消耗所有手牌。", new Dictionary<string, int>());
        var playRandomHandAttack = new GeneratorOperation("I:AutoPlayRandomAttackFromHand",
            OperationScope.Independent, "随机打出手牌中的2张攻击牌，攻击随机敌人。",
            new Dictionary<string, int>());
        var forEachExhausted = new GeneratorOperation("C:forEach", OperationScope.ConditionalTrigger,
            "每消耗一张牌。", new Dictionary<string, int>());
        var linkedPlayRandomHandAttack = playRandomHandAttack with
        {
            Parameters = new Dictionary<string, int> { ["triggerIndex"] = 1 }
        };
        if (CardEffectRules.HasValidExhaustAllHandOrdering([exhaustAllHand, playRandomHandAttack])
            || CardEffectRules.HasValidExhaustAllHandOrdering(
                [exhaustAllHand, forEachExhausted, linkedPlayRandomHandAttack])
            || !CardEffectRules.HasValidExhaustAllHandOrdering(
                [exhaustAllHand, preventTestDraw, playRandomHandAttack])
            || !CardEffectRules.HasValidExhaustAllHandOrdering([playRandomHandAttack, exhaustAllHand]))
            throw new InvalidOperationException("消耗所有手牌后的手牌依赖效果没有按同一结算顺序正确限制。 ");

        var permanentStrengthPricingAtom = new ComponentAtom("N:Self", OperationScope.NonTargeted,
            "获得2点力量。", false, CardReferenceRequirement.None);
        var oneShotBlockPricingAtom = new ComponentAtom("N:B", OperationScope.NonTargeted,
            "获得6点格挡。", false, CardReferenceRequirement.None);
        if (ComponentAssemblyGenerator.ApplyCardTypeOneShotPricing(permanentStrengthPricingAtom, 2,
                GeneratedCardType.Power, []) != 2
            || ComponentAssemblyGenerator.ApplyCardTypeOneShotPricing(permanentStrengthPricingAtom, 2,
                GeneratedCardType.Skill, []) != 1
            || ComponentAssemblyGenerator.ApplyCardTypeOneShotPricing(oneShotBlockPricingAtom, 6,
                GeneratedCardType.Power, []) != 9
            || ComponentAssemblyGenerator.ApplyCardTypeOneShotPricing(oneShotBlockPricingAtom, 6,
                GeneratedCardType.Skill, []) != 6)
            throw new InvalidOperationException("能力牌的一次性收益没有按消耗牌定价，或可重复技能的永久属性收益未折价。 ");

        var selfHpRandom = new Random(20260827);
        var sampledSelfHp = Enumerable.Range(0, 100_000)
            .Select(_ => NumericGenerationTuning.SampleSelfHpLoss(selfHpRandom, [])).ToArray();
        var meanSelfHp = sampledSelfHp.Average();
        var selfHpBuckets = Enumerable.Range(1, 6)
            .ToDictionary(value => value, value => sampledSelfHp.Count(sample => sample == value));
        var repeatSelfHpTrigger = new GeneratorOperation("A:turnStart", OperationScope.AbilityTrigger,
            "在你的回合开始时。", new Dictionary<string, int>());
        if (Math.Abs(meanSelfHp - 1.945d) > 0.04d
            || sampledSelfHp.Min() != 1 || sampledSelfHp.Max() != 6
            || selfHpBuckets[3] <= selfHpBuckets[4]
            || selfHpBuckets[4] <= selfHpBuckets[5]
            || selfHpBuckets[5] <= selfHpBuckets[6]
            || selfHpBuckets[6] is < 1_200 or > 1_800
            || Enumerable.Range(0, 1_000).Any(_ => NumericGenerationTuning.SampleSelfHpLoss(
                selfHpRandom, [repeatSelfHpTrigger]) != 1))
            throw new InvalidOperationException("自伤组件的1至6点平滑尾部分布失效。 ");
        var loseFocus = new GeneratorOperation("D:LoseFocus", OperationScope.NonTargeted,
            "失去1点集中。", new Dictionary<string, int>());
        var exhaustStatuses = new GeneratorOperation("D:ExhaustAllStatuses", OperationScope.NonTargeted,
            "消耗所有状态牌。", new Dictionary<string, int>());
        var forEachExhaustedStatus = new GeneratorOperation("D:ForEachExhaustedStatus",
            OperationScope.ConditionalTrigger, "每消耗一张状态牌。", new Dictionary<string, int>());
        var flakRandomDamage = new GeneratorOperation("N:RandomD", OperationScope.NonTargeted,
            "随机对敌人造成8点伤害。", new Dictionary<string, int> { ["triggerIndex"] = 1 });
        var buffEnemy = new GeneratorOperation("T:Apply", OperationScope.SingleEnemyOnly,
            "使该敌人获得1点力量。", new Dictionary<string, int>(), RequiresSingleTarget: true);
        var addDebris = new GeneratorOperation("R:AddDebrisToHand", OperationScope.NonTargeted,
            "将一张碎屑加入手牌。", new Dictionary<string, int>(), DerivativeId: "debris");
        var endTurn = new GeneratorOperation("R:EndTurn", OperationScope.Independent,
            "结束你的回合。", new Dictionary<string, int>());
        var poisonGate = new GeneratorOperation("C:ifTargetPoisoned", OperationScope.ConditionalTrigger,
            "如果目标敌人拥有中毒。", new Dictionary<string, int>());
        var gatedEndTurn = endTurn with
        {
            Parameters = new Dictionary<string, int> { ["triggerIndex"] = 0 }
        };
        var attackPlayedTrigger = new GeneratorOperation("CL:WheneverAttackPlayed", OperationScope.AbilityTrigger,
            "每当你打出一张攻击牌时。", new Dictionary<string, int>());
        var endTurnStartTrigger = new GeneratorOperation("A:turnStart", OperationScope.AbilityTrigger,
            "在你的回合开始时。", new Dictionary<string, int>());
        if (!CardEffectRules.IsNegativeEffect(cardCostPenalty)
            || !CardEffectRules.IsCostIncreaseDownside(cardCostPenalty)
            || !CardEffectRules.IsNegativeEffect(selfCostPenalty)
            || !CardEffectRules.IsCostIncreaseDownside(selfCostPenalty)
            || CardEffectRules.IsNegativeEffect(transfigureTradeoff)
            || CardEffectRules.IsCostIncreaseDownside(transfigureTradeoff)
            || !CardEffectRules.IsBeneficialEffect(transfigureTradeoff)
            || !CardEffectRules.IsNegativeEffect(generatedStatus)
            || CardEffectRules.IsNegativeEffect(exhaustOtherCard)
            || !CardEffectRules.IsBeneficialEffect(exhaustOtherCard)
            || !CardEffectRules.IsPlayerSelectedExhaust(exhaustOtherCard)
            || EffectBalanceModel.EstimatedEffectValue(exhaustOtherCard) != 350
            || NegativeEffectTuning.LinearCompensationValue(exhaustOtherCard) != 0d
            || CardEffectRules.IsNegativeEffect(exhaustSelectedDrawCard)
            || !CardEffectRules.IsBeneficialEffect(exhaustSelectedDrawCard)
            || EffectBalanceModel.EstimatedEffectValue(exhaustSelectedDrawCard) != 850
            || NegativeEffectTuning.LinearCompensationValue(exhaustSelectedDrawCard) != 0d
            || CardEffectRules.IsNegativeEffect(exhaustUpToThree)
            || !CardEffectRules.IsBeneficialEffect(exhaustUpToThree)
            || EffectBalanceModel.EstimatedEffectValue(exhaustUpToThree) != 950
            || EffectBalanceModel.PlayerSelectedExhaustValue(1, "hand", upTo: true)
                <= EffectBalanceModel.PlayerSelectedExhaustValue(1, "hand", upTo: false)
            || CardEffectRules.HasValidPlayerSelectedExhaustCounts([exhaustSelectedTwo])
            || CardEffectRules.HasValidPlayerSelectedExhaustCounts([exhaustSelectedDrawTwo])
            || !CardEffectRules.HasValidPlayerSelectedExhaustCounts([exhaustOtherCard])
            || !CardEffectRules.HasValidPlayerSelectedExhaustCounts([exhaustUpToThree])
            || NegativeEffectTuning.LinearCompensationValue(exhaustUpToThree) != 0d
            || !CardEffectRules.IsNegativeEffect(preventFurtherDraw)
            || !CardEffectRules.IsNegativeEffect(selfHpLoss)
            || !CardEffectRules.IsNegativeEffect(buffEnemy)
            || !CardEffectRules.IsNegativeEffect(loseFocus)
            || !CardEffectRules.IsNegativeEffect(endTurn)
            || CardEffectRules.IsBeneficialEffect(endTurn)
            || NegativeEffectTuning.BaseMultiplier(endTurn) != 2d
            || CardEffectRules.NegativeEffectCompensationPercent([ordinaryBlock, endTurn]) != 200
            || Math.Abs(NegativeEffectTuning.TotalMultiplier([endTurn], [CardTag.Ethereal]) - 2.14d) > 0.0001d
            || Math.Abs(NegativeEffectTuning.EffectiveMultiplier(gatedEndTurn,
                [poisonGate, gatedEndTurn], 1) - 1.10d) > 0.0001d
            || Math.Abs(NegativeEffectTuning.EffectiveMultiplier(gatedEndTurn,
                [attackPlayedTrigger, gatedEndTurn], 1) - 7.44d) > 0.0001d
            || !CardEffectRules.HasNoInvalidTriggeredStateEffects([poisonGate, gatedEndTurn])
            || !CardEffectRules.HasValidTriggeredEndTurnAssembly([poisonGate, gatedEndTurn])
            || CardEffectRules.HasValidTriggeredEndTurnAssembly([endTurnStartTrigger, gatedEndTurn])
            || CardEffectRules.IsNegativeEffect(exhaustStatuses)
            || !CardEffectRules.IsBeneficialEffect(exhaustStatuses)
            || EffectBalanceModel.EstimatedEffectValue(exhaustStatuses) != 780
            || EffectBalanceModel.RelativeTriggerFrequency(forEachExhaustedStatus) != 2.6d
            || Math.Abs(EffectBalanceModel.EstimatedEffectValue(exhaustStatuses)
                + EffectBalanceModel.EstimatedEffectValue(flakRandomDamage)
                    * EffectBalanceModel.RelativeTriggerFrequency(forEachExhaustedStatus)
                - 2_652d) > 0.01d
            || EffectBalanceModel.ExpectedLineValue(GeneratedRarity.Rare, 2d) != 2_322
            || !CardEffectRules.HasNegativeKeyword([CardTag.Exhaust])
            || !CardEffectRules.HasNegativeKeyword([CardTag.Ethereal])
            || CardEffectRules.HasNegativeKeyword([CardTag.Retain])
            || CardEffectRules.IsCostIncreaseDownside(thresholdTwo)
            || CardEffectRules.IsBeneficialEffect(cardCostPenalty)
            || CardEffectRules.HasValidAllCardsCostIncreaseAssembly([cardCostPenalty])
            || !CardEffectRules.HasValidAllCardsCostIncreaseAssembly([cardCostPenalty, repeatedFieldProbe[0]]))
            throw new InvalidOperationException("独立涨费必须被识别为负面；高费触发门槛及重放附加涨费不能被误判。 ");
        if (CardEffectRules.NegativeEffectCompensationPercent([ordinaryBlock]) != 100
            || CardEffectRules.NegativeEffectCompensationPercent([ordinaryBlock, selfHpLoss]) != 100
            || CardEffectRules.NegativeEffectLinearCompensationValue([ordinaryBlock, selfHpLoss]) != 200d
            || CardEffectRules.NegativeEffectCompensationPercent([ordinaryBlock, generatedStatus]) != 100
            || CardEffectRules.NegativeEffectLinearCompensationValue([ordinaryBlock, generatedStatus]) != 50d
            || CardEffectRules.NegativeEffectCompensationPercent([ordinaryBlock, exhaustStatuses]) != 100
            || CardEffectRules.NegativeEffectCompensationPercent([ordinaryBlock], [CardTag.Exhaust]) != 145
            || CardEffectRules.NegativeEffectCompensationPercent([ordinaryBlock], [CardTag.Ethereal]) != 107
            || CardEffectRules.NegativeEffectCompensationPercent(
                [ordinaryBlock], [CardTag.Exhaust, CardTag.Ethereal]) != 226
            || EffectBalanceModel.EstimatedPositiveKeywordValue([CardTag.Retain]) != 400d
            || EffectBalanceModel.EstimatedPositiveKeywordValue([CardTag.Innate]) != 200d
            || EffectBalanceModel.EstimatedPositiveKeywordValue([CardTag.Retain, CardTag.Innate]) != 600d
            || CardEffectRules.NegativeEffectCompensationPercent(
                [ordinaryBlock, selfHpLoss], [CardTag.Exhaust]) != 145
            || CardEffectRules.NegativeEffectCompensationPercent([ordinaryBlock, selfCostPenalty]) != 124
            || CardEffectRules.NegativeEffectCompensationPercent([ordinaryBlock, selfCostPenaltyTwo]) != 166
            || CardEffectRules.NegativeEffectCompensationPercent([ordinaryBlock, selfCostPenaltyThree]) != 226
            || CardEffectRules.NegativeEffectCompensationPercent([ordinaryBlock, cardCostPenalty]) != 100
            || CardEffectRules.NegativeEffectLinearCompensationValue([ordinaryBlock, cardCostPenalty]) != 600d
            || NegativeEffectTuning.BaseMultiplier(addDebris) != 1d
            || CardEffectRules.NegativeEffectCompensationPercent([ordinaryBlock, addDebris]) != 100
            || CardEffectRules.NegativeEffectLinearCompensationValue([ordinaryBlock, addDebris]) != 250d
            || NegativeEffectTuning.BaseMultiplier(triggeredExhaustTwo) != 1d
            || NegativeEffectTuning.EffectiveMultiplier(triggeredExhaustTwo,
                [highFrequencyTrigger, triggeredExhaustTwo], 1) != 1d
            || !NegativeEffectTuning.HasRepeatedTriggeredDownside(
                [highFrequencyTrigger, triggeredExhaustTwo])
            || CardEffectRules.NegativeEffectCompensationPercent(
                [highFrequencyTrigger, triggeredExhaustTwo, ordinaryBlock]) != 100
            || CardEffectRules.NegativeEffectLinearCompensationValue(
                [highFrequencyTrigger, triggeredExhaustTwo, ordinaryBlock]) != 640d)
            throw new InvalidOperationException("普通负面、碎屑、涨费与高额全体涨费没有获得对应的整卡收益预算补偿。 ");
        if (ComponentAssemblyGenerator.ApplyDownsideCostDiscount(-1, 0) != -1
            || ComponentAssemblyGenerator.ApplyDownsideCostDiscount(-1, 2) != -1
            || ComponentAssemblyGenerator.ApplyDownsideCostDiscount(2, 1) != 1
            || ComponentAssemblyGenerator.ApplyDownsideCostDiscount(1, 2) != 0)
            throw new InvalidOperationException("负面补偿的减费步骤不得把普通X费牌错误转换成0费牌。 ");
        var temporaryFocusLoss = new GeneratorOperation("D:LoseTemporaryFocus", OperationScope.NonTargeted,
            "本回合失去4点集中。", new Dictionary<string, int>());
        var temporaryFocusCard = new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Uncommon, CardDescriptionRenderer.Render([ordinaryBlock, temporaryFocusLoss]),
            Array.Empty<CardTag>(), [ordinaryBlock, temporaryFocusLoss], Character: GeneratedCharacter.Defect);
        var temporaryFocusUpgrades = Enumerable.Range(0, 300)
            .Select(seed => CardUpgradeGenerator.Generate(temporaryFocusCard, new Random(seed))).ToArray();
        if (!CardEffectRules.IsNegativeEffect(temporaryFocusLoss)
            || CardEffectRules.NegativeEffectCompensationPercent([ordinaryBlock, temporaryFocusLoss]) != 100
            || CardEffectRules.NegativeEffectLinearCompensationValue(
                [ordinaryBlock, temporaryFocusLoss]) != 340d
            || temporaryFocusUpgrades.Any(upgrade => upgrade.Effects.Any(effect =>
                effect.OperationIndex == 1 && effect.Kind == CardUpgradeKind.IncreaseNumber))
            || !temporaryFocusUpgrades.Any(upgrade => upgrade.Effects.Any(effect =>
                effect.OperationIndex == 1 && effect.Kind == CardUpgradeKind.ReduceNegativeNumber
                && effect.Delta == -1)
                && upgrade.UpgradedChineseDescription.Contains("本回合失去3点集中", StringComparison.Ordinal)))
            throw new InvalidOperationException("本回合失去集中必须是负面效果，且升级只能将其数值减少1。 ");
        var strengthLoss = new GeneratorOperation("NCR:LoseStrength", OperationScope.NonTargeted,
            "失去2点力量。", new Dictionary<string, int>());
        var dexterityLoss = new GeneratorOperation("N:LoseDex", OperationScope.NonTargeted,
            "失去1点敏捷。", new Dictionary<string, int>());
        var orbSlotLoss = new GeneratorOperation("D:LoseOrbSlots", OperationScope.NonTargeted,
            "失去1个充能球栏位。", new Dictionary<string, int>());
        var strengthLossCard = new GeneratedCard(0, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Uncommon, CardDescriptionRenderer.Render([strengthLoss, ordinaryBlock]),
            [CardTag.Ethereal], [strengthLoss, ordinaryBlock], Character: GeneratedCharacter.Necrobinder);
        var strengthLossUpgrades = Enumerable.Range(0, 300)
            .Select(seed => CardUpgradeGenerator.Generate(strengthLossCard, new Random(seed))).ToArray();
        if (NegativeEffectTuning.BaseMultiplier(loseFocus) != 1d
            || NegativeEffectTuning.BaseMultiplier(dexterityLoss) != 1d
            || NegativeEffectTuning.BaseMultiplier(strengthLoss) != 1d
            || NegativeEffectTuning.BaseMultiplier(orbSlotLoss) != 1d
            || CardEffectRules.NegativeEffectCompensationPercent([ordinaryBlock, loseFocus]) != 100
            || CardEffectRules.NegativeEffectCompensationPercent([ordinaryBlock, dexterityLoss]) != 100
            || CardEffectRules.NegativeEffectCompensationPercent([ordinaryBlock, strengthLoss]) != 100
            || CardEffectRules.NegativeEffectCompensationPercent([ordinaryBlock, orbSlotLoss]) != 100
            || CardEffectRules.NegativeEffectLinearCompensationValue([ordinaryBlock, loseFocus]) != 1_900d
            || CardEffectRules.NegativeEffectLinearCompensationValue([ordinaryBlock, dexterityLoss]) != 1_150d
            || CardEffectRules.NegativeEffectLinearCompensationValue([ordinaryBlock, strengthLoss]) != 1_470d
            || CardEffectRules.NegativeEffectLinearCompensationValue([ordinaryBlock, orbSlotLoss]) != 1_800d
            || strengthLossUpgrades.Any(upgrade => upgrade.Effects.Any(effect =>
                effect.OperationIndex == 0 && effect.Kind == CardUpgradeKind.IncreaseNumber))
            || !strengthLossUpgrades.Any(upgrade => upgrade.Effects.Any(effect =>
                effect.OperationIndex == 0 && effect.Kind == CardUpgradeKind.ReduceNegativeNumber
                && effect.Delta == -1)))
            throw new InvalidOperationException("永久失去集中、敏捷、力量或充能球栏位必须获得较高预算补偿，且升级不能增加损失。 ");
        var noBlockDuration = new GeneratorOperation("CL:NoBlockFromCards", OperationScope.Independent,
            "你在接下来的2回合内无法再从卡牌中获得格挡。", new Dictionary<string, int>());
        var excessiveNoBlockDuration = new GeneratorOperation("CL:NoBlockFromCards", OperationScope.Independent,
            "你在接下来的4回合内无法再从卡牌中获得格挡。", new Dictionary<string, int>());
        var positiveDuration = new GeneratorOperation("NCR:DoubleVulnerableWeak", OperationScope.SingleEnemyOnly,
            "在接下来的2回合内，该敌人的易伤与虚弱效果翻倍。", new Dictionary<string, int>(),
            RequiresSingleTarget: true);
        var noBlockDurationCard = new GeneratedCard(0, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Uncommon, CardDescriptionRenderer.Render([ordinaryBlock, noBlockDuration]),
            [CardTag.Exhaust], [ordinaryBlock, noBlockDuration], Character: GeneratedCharacter.Colorless);
        var noBlockDurationUpgrades = Enumerable.Range(0, 300)
            .Select(seed => CardUpgradeGenerator.Generate(noBlockDurationCard, new Random(seed))).ToArray();
        if (NumericGenerationTuning.DurationOnlyStackCap(noBlockDuration,
                GeneratedCharacter.Colorless, false) != 3
            || !CardEffectRules.IsReducibleNegativeDuration(noBlockDuration)
            || CardEffectRules.IsReducibleNegativeDuration(positiveDuration)
            || noBlockDurationUpgrades.Any(upgrade => upgrade.Effects.Any(effect =>
                effect.OperationIndex == 1 && effect.Kind == CardUpgradeKind.IncreaseNumber))
            || !noBlockDurationUpgrades.Any(upgrade => upgrade.Effects.Any(effect =>
                effect.OperationIndex == 1 && effect.Kind == CardUpgradeKind.ReduceNegativeNumber
                && effect.Delta == -1)
                && upgrade.UpgradedChineseDescription.Contains("接下来的1回合内", StringComparison.Ordinal)))
            throw new InvalidOperationException("负面效果的持续回合数升级后必须缩短，不能延长。 ");
        AssertInvalid(new GeneratedCard(0, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Uncommon, CardDescriptionRenderer.Render([ordinaryBlock, excessiveNoBlockDuration]),
            [CardTag.Exhaust], [ordinaryBlock, excessiveNoBlockDuration], Character: GeneratedCharacter.Colorless));
        var selfCostIncreaseRandom = new Random(20260905);
        var selfCostIncreaseSamples = Enumerable.Range(0, 1_000_000)
            .Select(_ => NumericGenerationTuning.SampleSelfCardCostIncrease(selfCostIncreaseRandom)).ToArray();
        if (selfCostIncreaseSamples.Any(value => value is < 1 or > 3)
            || selfCostIncreaseSamples.Count(value => value == 1) is < 815_000 or > 825_000
            || selfCostIncreaseSamples.Count(value => value == 3) is < 13_000 or > 17_000
            )
            throw new InvalidOperationException("此牌自身涨费的数值分布失控。 ");
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            "获得5点格挡。这张牌的耗能增加4。", Array.Empty<CardTag>(),
            [ordinaryBlock, selfCostPenalty with { ChineseText = "这张牌的耗能增加4。" }],
            EnglishDescription: "Gain 5 Block. Increase this card's cost by 4.",
            Character: GeneratedCharacter.Defect));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Rare,
            "在你的回合开始时，这张牌的耗能增加1。获得5点格挡。", Array.Empty<CardTag>(),
            [new GeneratorOperation("A:turnStart", OperationScope.AbilityTrigger, "在你的回合开始时。",
                    new Dictionary<string, int>()),
                selfCostPenalty with { Parameters = new Dictionary<string, int> { ["triggerIndex"] = 0 } },
                ordinaryBlock], EnglishDescription: "At the start of your turn, increase this card's cost by 1. Gain 5 Block.",
            Character: GeneratedCharacter.Defect));
        var catalogNegativeOperations = Enum.GetValues<GeneratedCharacter>()
            .SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms)
            .Where(CardEffectRules.IsNegativeEffect)
            .Select(atom => new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
                new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget))
            .ToArray();
        var fiveHpLoss = selfHpLoss with { ChineseText = "失去5点生命。" };
        var sixHpLoss = selfHpLoss with { ChineseText = "失去6点生命。" };
        var discardOne = new GeneratorOperation("N:Discard", OperationScope.NonTargeted,
            "丢弃1张牌。", new Dictionary<string, int>());
        var discardAll = new GeneratorOperation("N:DiscardAll", OperationScope.NonTargeted,
            "丢弃所有手牌。", new Dictionary<string, int>());
        var handPenaltyDamage = new GeneratorOperation("N:D", OperationScope.SingleEnemyOnly,
            "造成8点伤害。", new Dictionary<string, int>(), RequiresSingleTarget: true);
        var handPenaltyModifier = new GeneratorOperation("M:DamageMinusPerCardInHand", OperationScope.Modifier,
            "手牌中每有一张其他牌，这张牌的伤害降低2点。", new Dictionary<string, int>());
        if (catalogNegativeOperations.Length == 0
            || catalogNegativeOperations.Any(operation => operation.Template is not
                    ("N:Discard" or "M:DamageMinusPerCardInHand")
                && NegativeEffectTuning.BaseMultiplier(operation) <= 1d
                && NegativeEffectTuning.LinearCompensationValue(operation) <= 0d)
            || NegativeEffectTuning.BaseMultiplier(handPenaltyModifier) != 1d
            || NegativeEffectTuning.EffectiveMultiplier(handPenaltyModifier,
                    [handPenaltyDamage, handPenaltyModifier], 1) <= 1d
            || NegativeEffectTuning.LinearCompensationValue(selfHpLoss)
                >= NegativeEffectTuning.LinearCompensationValue(fiveHpLoss)
            || NegativeEffectTuning.LinearCompensationValue(fiveHpLoss)
                >= NegativeEffectTuning.LinearCompensationValue(sixHpLoss)
            || NegativeEffectTuning.BaseMultiplier(discardOne) >= NegativeEffectTuning.BaseMultiplier(discardAll)
            || !CardEffectRules.IsNegativeEffect(discardOne)
            || NegativeEffectTuning.TotalMultiplier([discardOne]) != 1d
            || NegativeEffectTuning.CompensationPercent(NegativeEffectTuning.TotalMultiplier([discardOne])) != 100
            || NegativeEffectTuning.BaseMultiplier(selfCostPenalty)
                >= NegativeEffectTuning.BaseMultiplier(selfCostPenaltyTwo)
            || NegativeEffectTuning.BaseMultiplier(selfCostPenaltyTwo)
                >= NegativeEffectTuning.BaseMultiplier(selfCostPenaltyThree)
            || NegativeEffectTuning.TotalMultiplier([selfHpLoss], [CardTag.Exhaust])
                <= NegativeEffectTuning.TotalMultiplier([selfHpLoss])
            || NegativeEffectTuning.CostDiscount(NegativeEffectTuning.TotalMultiplier([selfHpLoss]),
                true, false, 1) != 0
            || NegativeEffectTuning.CostDiscount(1.84d, true, false, 2) != 1
            || NegativeEffectTuning.CostDiscount(2.50d, true, false, 2) != 2
            || NegativeEffectTuning.CostDiscount(1.14d, false, false, 1) != 1)
            throw new InvalidOperationException("直接负面倍率没有覆盖所有倍率型负面，或没有随负面强度递增。 ");
        foreach (var target in new[] { 1, 2, 3 })
        {
            var energyRandom = new Random(20260821 + target);
            var energySamples = Enumerable.Range(0, 100_000)
                .Select(_ => NumericGenerationTuning.SampleEnergyGainValue(energyRandom, target)).ToArray();
            var highRate = energySamples.Count(value => value >= 3) / (double)energySamples.Length;
            var maximumRate = target switch { 1 => 0.007, 2 => 0.04, _ => 0.04 };
            if (highRate > maximumRate || energySamples.Any(value => value is < 1 or > 4))
                throw new InvalidOperationException($"获得能量的高数值尾部失控：target={target}, rate={highRate:P2}。");
        }
        var generatedCardCountRandom = new Random(20260822);
        var generatedCardCountSamples = Enumerable.Range(0, 100_000)
            .Select(_ => NumericGenerationTuning.SampleRandomGeneratedCardCount(generatedCardCountRandom)).ToArray();
        if (generatedCardCountSamples.Count(value => value == 1) < 65_000
            || generatedCardCountSamples.Count(value => value <= 3) < 98_500
            || generatedCardCountSamples.Any(value => value is < 1 or > 4))
            throw new InvalidOperationException("随机生成牌的数量分布必须以1张为主，且至少98.5%的结果不超过3张。");
        if (catalog.Recipes.Count != 85)
            throw new InvalidOperationException("完整组件目录必须包含 85 张单人战士卡。");
        CardNameGenerator.ValidateCatalog(catalog);
        foreach (var atom in catalog.Atoms)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(atom.Template, @"[^\x00-\x7F]"))
                throw new InvalidOperationException($"operation ID 必须仅包含 ASCII 字符：{atom.Template}");
            _ = EnglishCardDescriptionRenderer.OperationText(new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText, new Dictionary<string, int>()));
        }
        foreach (var recipe in catalog.Recipes)
        {
            if (!assembler.CanAssemble(recipe))
                throw new InvalidOperationException($"组件语法无法组装原卡 {recipe.Id}。");
        }

        var silentCatalog = CharacterComponentCatalogs.Get(GeneratedCharacter.Silent);
        var silentAssembler = new ComponentAssemblyGenerator(new Random(20260820), GeneratedCharacter.Silent);
        if (silentCatalog.Recipes.Count != 86)
            throw new InvalidOperationException("完整猎手组件目录必须包含 86 张单人卡。");
        CardNameGenerator.ValidateCatalog(silentCatalog);
        foreach (var atom in silentCatalog.Atoms)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(atom.Template, @"[^\x00-\x7F]"))
                throw new InvalidOperationException($"猎手 operation ID 必须仅包含 ASCII 字符：{atom.Template}");
            _ = EnglishCardDescriptionRenderer.OperationText(new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText, new Dictionary<string, int>()));
        }
        foreach (var recipe in silentCatalog.Recipes)
        {
            if (!silentAssembler.CanAssemble(recipe))
                throw new InvalidOperationException($"共享组件语法无法组装猎手原卡 {recipe.Id}。");
        }
        foreach (var externalCharacter in new[]
                 {
                     GeneratedCharacter.Defect, GeneratedCharacter.Necrobinder,
                     GeneratedCharacter.Regent, GeneratedCharacter.Colorless
                 })
        {
            var externalCatalog = CharacterComponentCatalogs.Get(externalCharacter);
            var externalAssembler = new ComponentAssemblyGenerator(new Random(20260820), externalCharacter);
            var unreachable = externalCatalog.Recipes
                .Where(recipe => !externalAssembler.CanAssemble(recipe)).Select(recipe => recipe.Id).ToArray();
            if (unreachable.Length > 0)
                throw new InvalidOperationException(
                    $"{externalCharacter} 原卡组件无法在最高4点固定普通费用的壳上重建：{string.Join(", ", unreachable)}");
        }
        var defectCatalog = CharacterComponentCatalogs.Get(GeneratedCharacter.Defect);
        var helix = defectCatalog.Recipes.Single(recipe => recipe.Id == "HelixDrill");
        var helixPrefix = new GeneratorOperation(helix.Atoms[0].Template, helix.Atoms[0].Scope,
            helix.Atoms[0].ChineseText, new Dictionary<string, int>(),
            RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(helix.Atoms[0]));
        if (!helix.Atoms.Select(atom => atom.Template).SequenceEqual(
                ["D:ForEachEnergySpentThisTurn", "T:D"])
            || !helix.TriggerOwners.SequenceEqual([-1, 0])
            || helix.Atoms[0].Scope != OperationScope.ConditionalTrigger
            || helix.Atoms[1].Scope != OperationScope.SingleEnemyOnly
            || OperationRuntimeSpecCompiler.GetOrCompile(helix.Atoms[0]).Trigger?.Kind
                != "energy_spent_this_turn_excluding_self"
            || OperationRuntimeSpecCompiler.GetOrCompile(helix.Atoms[1]) is not
                { Opcode: "deal_damage", Variant: "selected" }
            || OperationRuntimeSpecCompiler.FixedValue(helix.Atoms[0], "threshold") != 1
            || OperationRuntimeSpecCompiler.FixedValue(helix.Atoms[1], "damage") != 5
            || Math.Abs(EffectBalanceModel.ExpectedTriggerResolutions(helixPrefix) - 2d) > 0.0001d
            || CardEffectRules.IsDependencyPrefix(helix.Atoms[0])
            || !CardEffectRules.TriggerNeedsLinkedEffect(helix.Atoms[0])
            || EffectBalanceModel.EstimatedEffectValue(helix.Atoms[1]) != 500)
            throw new InvalidOperationException("螺旋钻击必须拆为可组装的此牌以外耗能触发器与5点伤害，原牌1能量门槛按2次触发估值。");
        var genericHelixBlock = defectCatalog.Atoms.First(atom => atom.Template == "N:B");
        var genericHelix = helix with
        {
            Id = "AuditHelixBlockPayoff",
            Type = GeneratedCardType.Skill,
            Target = TargetMode.Other,
            Tags = [],
            Atoms = [helix.Atoms[0], genericHelixBlock],
            TriggerOwners = [-1, 0]
        };
        if (!new ComponentAssemblyGenerator(new Random(20260830), GeneratedCharacter.Defect)
                .CanAssemble(genericHelix))
            throw new InvalidOperationException("螺旋钻击耗能触发器必须能组装专属伤害以外的常规后续效果。");

        // Every payoff that consumes transient event state must retain one exact, character-owned trigger
        // contract. Ordinary payoffs are intentionally absent from this audit: their native TriggerOwner keeps
        // the original card reconstructible, but the component remains independently selectable.
        foreach (var characterValue in Enum.GetValues<GeneratedCharacter>())
        {
            var characterCatalog = CharacterComponentCatalogs.Get(characterValue);
            foreach (var recipe in characterCatalog.Recipes)
            {
                for (var atomIndex = 0; atomIndex < recipe.Atoms.Count; atomIndex++)
                {
                    var atom = recipe.Atoms[atomIndex];
                    if (!CardEffectRules.RequiresSpecificTriggerPayload(atom)) continue;
                    var owner = recipe.TriggerOwners[atomIndex];
                    if (owner < 0 || owner >= atomIndex)
                        throw new InvalidOperationException(
                            $"{characterValue}/{recipe.Id}/{atom.Template} 缺少事件载荷触发器。");
                    var triggerAtom = recipe.Atoms[owner];
                    var trigger = new GeneratorOperation(triggerAtom.Template, triggerAtom.Scope,
                        string.Empty, new Dictionary<string, int>(),
                        RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(triggerAtom));
                    if (!CardEffectRules.CanSupplySpecificTriggerPayload(trigger, atom))
                        throw new InvalidOperationException(
                            $"{characterValue}/{recipe.Id}/{atom.Template} 错接到 {triggerAtom.Template}。");
                }
            }
        }

        var coolants = defectCatalog.Recipes.Single(recipe => recipe.Id == "Coolant");
        var coolantDraw = coolants with
        {
            Id = "AuditUniqueOrbDrawPayoff",
            Atoms = [coolants.Atoms[0], coolants.Atoms[1], defectCatalog.Atoms.First(atom => atom.Template == "N:Draw")],
            TriggerOwners = [-1, 0, 0]
        };
        if (!new ComponentAssemblyGenerator(new Random(20260830), GeneratedCharacter.Defect)
                .CanAssemble(coolantDraw))
            throw new InvalidOperationException("每有一种不同充能球必须能连接抽牌等普通后续，而非仅限原版格挡后续。");
        if (defectCatalog.Atoms.Count(atom => atom.Template == "D:ForEachUniqueOrb") != 1
            || coolants.Atoms[1].SchemaKey != defectCatalog.Recipes.Single(recipe => recipe.Id == "CompileDriver")
                .Atoms.Single(atom => atom.Template == "D:ForEachUniqueOrb").SchemaKey)
            throw new InvalidOperationException("作为能力后续的不同充能球计数器必须复用普通计数器组件。");

        var colorlessCatalog = CharacterComponentCatalogs.Get(GeneratedCharacter.Colorless);
        var ordinaryColorlessDamage = colorlessCatalog.Atoms.Single(atom => atom.Template == "T:D");
        foreach (var recipeId in new[] { "MindBlast" })
        {
            var payoff = colorlessCatalog.Recipes.Single(recipe => recipe.Id == recipeId)
                .Atoms.Single(atom => atom.Template == "T:D");
            if (payoff.SchemaKey != ordinaryColorlessDamage.SchemaKey
                || CardEffectRules.IsConditionalDamageVariant(payoff))
                throw new InvalidOperationException($"{recipeId} 的计数后续必须复用普通单体伤害，而非建立专属伤害词条。");
        }
        if (colorlessCatalog.Atoms.Count(atom => atom.Template == "T:D") != 1)
            throw new InvalidOperationException("无色普通单体伤害不应因处于计数后续而产生第二个字段。");

        // These clauses change an existing Damage field or its hit count; none deals an independent packet.
        // Their wording can resemble a triggered Damage payoff, so protect the semantic boundary explicitly.
        var dependentDamageModifiers = new[]
        {
            defectCatalog.Recipes.Single(recipe => recipe.Id == "Barrage").Atoms[^1],
            CharacterComponentCatalogs.Get(GeneratedCharacter.Regent).Recipes
                .Single(recipe => recipe.Id == "LunarBlast").Atoms[^1],
            CharacterComponentCatalogs.Get(GeneratedCharacter.Regent).Recipes
                .Single(recipe => recipe.Id == "Radiate").Atoms[^1]
        };
        foreach (var modifier in dependentDamageModifiers)
            if (modifier.Scope != OperationScope.Modifier
                || !CardEffectRules.RequiresDependencyPrefix(modifier)
                || OperationRuntimeSpecCompiler.GetOrCompile(modifier).Opcode == "deal_damage"
                || modifier.SchemaKey == ordinaryColorlessDamage.SchemaKey)
                throw new InvalidOperationException($"{modifier.Template} 是伤害次数 modifier，不能并入普通造成伤害词条。");

        var thunder = defectCatalog.Recipes.Single(recipe => recipe.Id == "Thunder");
        var thunderDamage = thunder.Atoms.Single(atom => atom.Template == "T:D");
        if (!CardEffectRules.IsHitEnemyDamageVariant(thunderDamage)
            || thunderDamage.SchemaKey == defectCatalog.Atoms.First(atom => atom.Template == "T:D"
                && !CardEffectRules.IsHitEnemyDamageVariant(atom)).SchemaKey)
            throw new InvalidOperationException("雷霆读取触发事件命中的敌人，不能误合并为需要卡牌选中目标的普通伤害。");

        var rollingBoulder = colorlessCatalog.Recipes
            .Single(recipe => recipe.Id == "RollingBoulder");
        if (!rollingBoulder.Atoms.Select(atom => atom.Template)
                .SequenceEqual(["A:turnStart", "N:AllD", "CL:IncreaseRollingDamage"])
            || rollingBoulder.TriggerOwners.Count != 3
            || !rollingBoulder.TriggerOwners.SequenceEqual([-1, 0, 0])
            || CharacterComponentCatalogs.Get(GeneratedCharacter.Colorless).Atoms
                .Any(atom => atom.Template == "CL:RollingAllDamage"))
            throw new InvalidOperationException("滚石必须复用普通全体伤害组件，仅保留依赖该伤害的成长 modifier。");

        var escapePlanAuditSource = silentCatalog.Recipes.Single(recipe => recipe.Id == "EscapePlan");
        var escapePlanPayoff = escapePlanAuditSource with
        {
            Id = "AuditLastDrawnSkillAlternatePayoff",
            Atoms = [escapePlanAuditSource.Atoms[0], escapePlanAuditSource.Atoms[1],
                silentCatalog.Atoms.First(atom => atom.Template == "N:CreateShiv")],
            TriggerOwners = [-1, -1, 1]
        };
        if (!new ComponentAssemblyGenerator(new Random(20260830), GeneratedCharacter.Silent)
                .CanAssemble(escapePlanPayoff))
            throw new InvalidOperationException("刚抽到技能牌条件必须保留抽牌依赖，但可连接格挡以外的普通后续。");

        var hunt = silentCatalog.Recipes.Single(recipe => recipe.Id == "TheHunt");
        var standaloneCardReward = hunt with
        {
            Id = "AuditStandaloneCardReward",
            Cost = 1,
            Type = GeneratedCardType.Skill,
            Target = TargetMode.Other,
            Tags = [CardTag.Exhaust],
            Atoms = [hunt.Atoms.Single(atom => atom.Template == "I:AddCardReward")],
            TriggerOwners = [-1]
        };
        if (!new ComponentAssemblyGenerator(new Random(20260830), GeneratedCharacter.Silent)
                .CanAssemble(standaloneCardReward))
            throw new InvalidOperationException("额外卡牌奖励可独立结算，应作为限制效果而非锁死在斩杀触发器后。");

        var roleOwnedTriggerTemplates = new Dictionary<GeneratedCharacter, string[]>
        {
            [GeneratedCharacter.Defect] = ["D:ForEachOrb", "D:ForEachUniqueOrb", "D:ForEachEnergySpentThisTurn"],
            [GeneratedCharacter.Necrobinder] = ["NCR:ForEachDoomThreshold", "NCR:ForEachOstyAttackThisTurn"],
            [GeneratedCharacter.Regent] = ["R:ForEachStarCostCard", "R:ForEachStarGainedThisTurn"],
            [GeneratedCharacter.Colorless] = ["CL:ForEachDrawPileCard"]
        };
        foreach (var (owner, templates) in roleOwnedTriggerTemplates)
        foreach (var template in templates)
        {
            if (!CharacterComponentCatalogs.Get(owner).Atoms.Any(atom => atom.Template == template))
                throw new InvalidOperationException($"{owner} 缺少角色专属触发器 {template}。");
            foreach (var other in Enum.GetValues<GeneratedCharacter>().Where(characterValue => characterValue != owner))
                if (CharacterComponentCatalogs.Get(other).Atoms.Any(atom => atom.Template == template))
                    throw new InvalidOperationException($"角色专属触发器 {template} 从 {owner} 污染到 {other}。");
        }
        var impervious = catalog.Recipes.Single(recipe => recipe.Id == "Impervious");
        if (!impervious.Tags.Contains(CardTag.Exhaust))
            throw new InvalidOperationException("岿然不动的原卡配方必须包含消耗关键词。");
        var necrobinderCatalog = CharacterComponentCatalogs.Get(GeneratedCharacter.Necrobinder);
        var hang = necrobinderCatalog.Recipes.Single(recipe => recipe.Id == "Hang");
        if (!hang.Atoms.Select(atom => atom.Template)
                .SequenceEqual(new[] { "T:D", "NCR:DoubleHangDamage" })
            || hang.Atoms[1].ChineseText != "这张牌的伤害受“吊杀”影响。让所有“吊杀”牌对这名敌人造成的伤害翻倍。")
            throw new InvalidOperationException("吊杀的原卡配方或描述未保留吊杀伤害家族规则。");
        var sleightOfFlesh = necrobinderCatalog.Recipes.Single(recipe => recipe.Id == "SleightOfFlesh");
        if (!sleightOfFlesh.Atoms.Select(atom => atom.Template)
                .SequenceEqual(new[] { "A:whenDebuffApplied", "NCR:UnpoweredDamage" })
            || sleightOfFlesh.Atoms[0].ChineseText != "每当你给予一个敌人负面状态时。"
            || EnglishCardDescriptionRenderer.OperationText(new GeneratorOperation(
                sleightOfFlesh.Atoms[0].Template, sleightOfFlesh.Atoms[0].Scope,
                sleightOfFlesh.Atoms[0].ChineseText, new Dictionary<string, int>()))
                != "Whenever you apply a debuff to an enemy.")
            throw new InvalidOperationException("血肉戏法必须使用原版卡面文案，并造成可被格挡的非力量伤害。");
        var escapePlan = silentCatalog.Recipes.Single(recipe => recipe.Id == "EscapePlan");
        if (!escapePlan.Atoms.Select(atom => atom.Template)
                .SequenceEqual(new[] { "N:Draw", "C:ifLastDrawnSkill", "N:B" })
            || !escapePlan.TriggerOwners.SequenceEqual(new[] { -1, -1, 1 }))
            throw new InvalidOperationException("逃脱计划必须拆为抽1张牌、检查刚抽到的技能牌、获得格挡三个组件。");
        var escapeOperations = new GeneratorOperation[]
        {
            new("N:Draw", OperationScope.NonTargeted, "抽1张牌。", new Dictionary<string, int>()),
            new("C:ifLastDrawnSkill", OperationScope.ConditionalTrigger, "如果抽到的是技能牌。", new Dictionary<string, int>()),
            new("N:B", OperationScope.NonTargeted, "获得3点格挡。", new Dictionary<string, int> { ["triggerIndex"] = 1 })
        };
        if (CardDescriptionRenderer.Render(escapeOperations) != "抽1张牌。\n如果抽到的是技能牌，则获得3点格挡。"
            || EnglishCardDescriptionRenderer.Render(escapeOperations) != "Draw 1 card.\nIf the card drawn is a Skill, gain 3 Block.")
            throw new InvalidOperationException("逃脱计划的中英文重组描述不符合原版文风："
                + CardDescriptionRenderer.Render(escapeOperations).Replace("\n", " / ", StringComparison.Ordinal)
                + " | " + EnglishCardDescriptionRenderer.Render(escapeOperations).Replace("\n", " / ", StringComparison.Ordinal));

        ExternalOperationTextRegistry.Register("CL:NoBlockFromCards",
            "你在接下来的2回合内无法再从卡牌中获得格挡。",
            "You cannot gain Block from cards for the next 2 turns.");
        var noBlockLastOperations = new GeneratorOperation[]
        {
            new("CL:NoBlockFromCards", OperationScope.Independent,
                "你在接下来的2回合内无法再从卡牌中获得格挡。", new Dictionary<string, int>()),
            new("N:B", OperationScope.NonTargeted, "获得5点格挡。", new Dictionary<string, int>())
        };
        var renderedNoBlockLastChinese = CardDescriptionRenderer.Render(noBlockLastOperations);
        var renderedNoBlockLastEnglish = EnglishCardDescriptionRenderer.Render(noBlockLastOperations);
        if (renderedNoBlockLastChinese
                != "获得5点格挡。\n你在接下来的2回合内无法再从卡牌中获得格挡。"
            || renderedNoBlockLastEnglish
                != "Gain 5 Block.\nYou cannot gain Block from cards for the next 2 turns.")
            throw new InvalidOperationException("无法从卡牌中获得格挡的持续负面效果必须显示在卡面最后："
                + renderedNoBlockLastChinese.Replace("\n", " / ", StringComparison.Ordinal) + " | "
                + renderedNoBlockLastEnglish.Replace("\n", " / ", StringComparison.Ordinal));

        var doomConditionalOperations = new GeneratorOperation[]
        {
            new("NCR:IfDoomAppliedThisTurn", OperationScope.ConditionalTrigger,
                "若本回合曾给予灾厄。", new Dictionary<string, int>()),
            new("N:Draw", OperationScope.NonTargeted, "抽2张牌。",
                new Dictionary<string, int> { ["triggerIndex"] = 0 }),
            new("NCR:ApplyDoomAll", OperationScope.NonTargeted, "给予所有敌人23层灾厄。",
                new Dictionary<string, int> { ["triggerIndex"] = 0 })
        };
        ExternalOperationTextRegistry.Register("NCR:IfDoomAppliedThisTurn", "若本回合曾给予灾厄。",
            "If you applied Doom this turn.");
        ExternalOperationTextRegistry.Register("NCR:ApplyDoomAll", "给予所有敌人23层灾厄。",
            "Apply 23 Doom to ALL enemies.");
        var renderedDoomConditional = CardDescriptionRenderer.Render(doomConditionalOperations);
        var renderedEnglishDoomConditional = EnglishCardDescriptionRenderer.Render(doomConditionalOperations);
        if (renderedDoomConditional != "如果你在本回合中曾给予过灾厄，则抽2张牌，并且给予所有敌人23层灾厄。"
            || renderedEnglishDoomConditional.Contains(". Apply 23 Doom", StringComparison.Ordinal))
            throw new InvalidOperationException("同一条件拥有多个效果时必须明确共享作用域："
                + renderedDoomConditional + " | " + renderedEnglishDoomConditional);
        var twoEffectPower = new GeneratorOperation[]
        {
            new("A:turnStart", OperationScope.AbilityTrigger, "在你的回合开始时。", new Dictionary<string, int>()),
            new("D:ChannelLightning", OperationScope.NonTargeted, "生成6个闪电充能球。",
                new Dictionary<string, int> { ["triggerIndex"] = 0 }),
            new("D:LoseFocus", OperationScope.NonTargeted, "失去5点集中。",
                new Dictionary<string, int> { ["triggerIndex"] = 0 })
        };
        if (CardDescriptionRenderer.Render(twoEffectPower) != "在你的回合开始时，生成6个闪电充能球，并且失去5点集中。")
            throw new InvalidOperationException("同一能力触发器的两个效果之间不能使用句号。");
        var vulnerableConditional = new GeneratorOperation[]
        {
            new("C:ifTargetVulnerable", OperationScope.ConditionalTrigger, "若该敌人拥有易伤。",
                new Dictionary<string, int>()),
            new("N:HP-", OperationScope.NonTargeted, "失去2点生命。",
                new Dictionary<string, int> { ["triggerIndex"] = 0 }),
            new("T:D", OperationScope.SingleEnemyOnly, "造成9点伤害。",
                new Dictionary<string, int> { ["triggerIndex"] = 0 }, RequiresSingleTarget: true)
        };
        var renderedVulnerableConditional = CardDescriptionRenderer.Render(vulnerableConditional);
        if (renderedVulnerableConditional != "如果该敌人拥有易伤，则失去2点生命，并且造成9点伤害。"
            || CardDescriptionRenderer.JoinTriggeredEffects("失去2点生命。造成9点伤害。", chinese: true)
                != "失去2点生命，并且造成9点伤害。"
            || CardDescriptionRenderer.JoinTriggeredEffects(
                "对所有敌人造成1点伤害。然后将该伤害增加1点。", chinese: true)
                != "对所有敌人造成1点伤害，并且将该伤害增加1点。"
            || CardDescriptionRenderer.JoinTriggeredEffects("Lose 2 HP. Deal 9 damage.", chinese: false)
                != "Lose 2 HP and deal 9 damage.")
            throw new InvalidOperationException("条件绑定的中英文多效果必须合并为同一个句法作用域："
                + renderedVulnerableConditional);
        var previewJoinProbe = CardDescriptionRenderer.JoinTriggeredEffects(
            "当前每有一名敌人，就生成1个冰霜充能球。{InCombat:\n（生成3个冰霜充能球）|}", chinese: true);
        if (previewJoinProbe.Contains("并{InCombat:", StringComparison.Ordinal))
            throw new InvalidOperationException("战斗数值预览不能被当作触发器的第二项效果并插入‘并’。");
        var postPlayDrawTrigger = new GeneratorOperation[]
        {
            new("C:untilTurnEndCardDrawn", OperationScope.ConditionalTrigger,
                "本回合每当你抽到一张牌时。", new Dictionary<string, int>()),
            new("N:AllPoison", OperationScope.NonTargeted, "给予所有敌人2层中毒。",
                new Dictionary<string, int> { ["triggerIndex"] = 0 })
        };
        var renderedPostPlayDrawChinese = CardDescriptionRenderer.Render(postPlayDrawTrigger);
        var renderedPostPlayDrawEnglish = EnglishCardDescriptionRenderer.Render(postPlayDrawTrigger);
        if (renderedPostPlayDrawChinese != "打出此牌后，你在本回合每抽到一张牌，给予所有敌人2层中毒。"
            || renderedPostPlayDrawEnglish
                != "After you play this card, whenever you draw a card this turn, apply 2 Poison to ALL enemies.")
            throw new InvalidOperationException("本回合抽牌触发器没有明确写出从打出来源牌后开始计数："
                + renderedPostPlayDrawChinese + " | " + renderedPostPlayDrawEnglish);

        ExternalOperationTextRegistry.Register("TEST:NumericStyle", "抽2张牌。", "Draw 1 cards.");
        var numericStyle = EnglishCardDescriptionRenderer.OperationText(new GeneratorOperation(
            "TEST:NumericStyle", OperationScope.NonTargeted, "抽2张牌。", new Dictionary<string, int>()));
        if (numericStyle != "Draw 2 cards.")
            throw new InvalidOperationException($"英文随机数值与单复数校正失败：{numericStyle}");
        var agreementSamples = new Dictionary<string, string>
        {
            ["1 random Attacks"] = "1 random Attack",
            ["3 random Attack"] = "3 random Attacks",
            ["1 Stars"] = "1 Star",
            ["3 Star"] = "3 Stars",
            ["1 additional times"] = "1 additional time",
            ["3 additional time"] = "3 additional times",
            ["2 Orb Slots"] = "2 Orb Slots"
        };
        foreach (var (source, expected) in agreementSamples)
        {
            var actual = ExternalOperationTextRegistry.NormalizeNumberAgreement(source);
            if (actual != expected)
                throw new InvalidOperationException($"英文单复数校正失败：{source} -> {actual}（应为 {expected}）");
        }
        var nextVoid = new GeneratorOperation("NCR:NextVoidCostsZero", OperationScope.Independent,
            "你打出的下一张虚无牌耗能变为0。", new Dictionary<string, int>());
        if (CardTextStyle.Chinese(nextVoid) != CardTextStyle.Chinese(nextVoid, CardTextStyle.Chinese(nextVoid)))
            throw new InvalidOperationException("中文卡面用语校正必须可重复执行，不能叠加主语。 ");
        var legacyNthAttack = new GeneratorOperation("A:when", OperationScope.AbilityTrigger,
            "每当你在本回合打出第3张攻击牌时。", new Dictionary<string, int>());
        var legacyVulnerableStrength = new GeneratorOperation("N:StrengthPerTargetVulnerable",
            OperationScope.NonTargeted, "敌人身上每有一层易伤，就获得1点力量。",
            new Dictionary<string, int>(), RequiresSingleTarget: true);
        var turnLimitedNextAttack = new GeneratorOperation("C:untilTurnEnd", OperationScope.ConditionalTrigger,
            "当你打出下一张攻击牌时。", new Dictionary<string, int>());
        var persistentNextAttack = turnLimitedNextAttack with { Template = "C:for" };
        if (CardTextStyle.Chinese(legacyNthAttack) != "每回合中，当你打出第3张攻击牌时。"
            || CardTextStyle.Chinese(legacyVulnerableStrength)
                != "目标敌人身上每有一层易伤，就获得1点力量。"
            || CardTextStyle.Chinese(legacyVulnerableStrength,
                CardTextStyle.Chinese(legacyVulnerableStrength))
                != "目标敌人身上每有一层易伤，就获得1点力量。"
            || CardTextStyle.Chinese(turnLimitedNextAttack)
                != "在本回合中，当你打出下一张攻击牌时。"
            || CardTextStyle.Chinese(persistentNextAttack) != "当你打出下一张攻击牌时。")
            throw new InvalidOperationException("每回合第N张攻击牌或按目标易伤获得力量的规范用语失效。 ");
        var nextTwoAttacks = new GeneratorOperation("C:grantNextAttacksThisTurn",
            OperationScope.ConditionalTrigger, "在这个回合，你打出的下2张攻击牌获得效果：",
            new Dictionary<string, int>());
        var perfectedNextAttack = new GeneratorOperation("M:base", OperationScope.Modifier,
            "本场战斗中每有一张名称含“打击”的牌，这张牌额外造成2点伤害。",
            new Dictionary<string, int> { ["triggerIndex"] = 0 });
        var nextAttackReplay = new GeneratorOperation("I:ReplayAttack", OperationScope.Independent,
            "将该攻击牌额外打出1次。", new Dictionary<string, int> { ["triggerIndex"] = 0 });
        var persistentNextAttackGrant = new GeneratorOperation("C:grantNextAttack",
            OperationScope.ConditionalTrigger, "你打出的下一张攻击牌获得效果：",
            new Dictionary<string, int>());
        var nextAttackFree = new GeneratorOperation("I:SetCostZero", OperationScope.Independent,
            "费用变为0。", new Dictionary<string, int> { ["triggerIndex"] = 0 });
        var unsupportedExtraHit = new GeneratorOperation("M:base", OperationScope.Modifier,
            "这张牌额外造成1次伤害。", new Dictionary<string, int> { ["triggerIndex"] = 0 });
        var grantedPerfectedChinese = CardDescriptionRenderer.Render([nextTwoAttacks, perfectedNextAttack]);
        var grantedPerfectedEnglish = EnglishCardDescriptionRenderer.Render([nextTwoAttacks, perfectedNextAttack]);
        if (grantedPerfectedChinese != "在这个回合，你打出的下2张攻击牌获得效果：\n你每有一张名字中含有“打击”的牌，伤害+2。"
            || grantedPerfectedEnglish != "This turn, your next 2 Attacks gain:\n+2 damage for each card you have with “Strike” in its name."
            || CardDescriptionRenderer.Render([nextTwoAttacks with
                { ChineseText = "在这个回合，你打出的下1张攻击牌获得效果：" }, nextAttackReplay])
                != "在这个回合，你打出的下1张攻击牌会被额外打出一次。"
            || CardDescriptionRenderer.Render([persistentNextAttackGrant, nextAttackFree])
                != "你打出的下一张攻击牌耗能变为0。"
            || !CardEffectRules.HasValidNextAttackGrantAssembly([nextTwoAttacks, perfectedNextAttack])
            || CardEffectRules.HasValidNextAttackGrantAssembly([nextTwoAttacks, unsupportedExtraHit])
            || !CardEffectRules.IsNonUpgradeableNumericMarker(nextAttackReplay)
            || CardEffectRules.HasValidNextAttackGrantAssembly([nextTwoAttacks,
                new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得5点格挡。",
                    new Dictionary<string, int> { ["triggerIndex"] = 0 })])
            || CardEffectRules.HasValidNextAttackGrantAssembly([nextTwoAttacks, perfectedNextAttack,
                new GeneratorOperation("N:Draw", OperationScope.NonTargeted, "抽1张牌。", new Dictionary<string, int>())]))
            throw new InvalidOperationException("限定下一张攻击牌的作用域、末尾位置或专用描述渲染失效。 ");
        CardTemplateValidator.Validate(new GeneratedCard(0, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Common, CardDescriptionRenderer.Render(escapeOperations), Array.Empty<CardTag>(), escapeOperations,
            EnglishDescription: EnglishCardDescriptionRenderer.Render(escapeOperations), Character: GeneratedCharacter.Silent));
        RandomCardGenerator? silentGenerator = null;
        var silentNames = new HashSet<string>(StringComparer.Ordinal);
        var silentOriginalNames = CharacterComponentCatalogs.Get(GeneratedCharacter.Silent).Recipes.Select(recipe => recipe.ChineseTitle).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < 10_000; i++)
        {
            if (i % 94 == 0)
            {
                silentGenerator = new RandomCardGenerator(GeneratedCharacter.Silent, 20260820 + i);
                silentNames.Clear();
            }
            var generated = silentGenerator!.Generate();
            CardTemplateValidator.Validate(generated);
            if (!silentNames.Add(generated.Name!.Chinese))
                throw new InvalidOperationException("同一猎手卡池中出现了重复中文卡名。");
            if (silentOriginalNames.Contains(generated.Name.Chinese))
                throw new InvalidOperationException($"随机猎手卡名与原卡重名：{generated.Name.Chinese}。");
            if (generated.Character != GeneratedCharacter.Silent)
                throw new InvalidOperationException("猎手生成器返回了错误角色的卡牌。");
            if (generated.Operations.Any(operation => operation.Template is "N:HP-" or "I:PlayTopCardAndExhaust"))
                throw new InvalidOperationException("猎手抽到了战士专属组件。");
            if (System.Text.RegularExpressions.Regex.IsMatch(generated.ChineseDescription, @"(?m)^(?:虚无(?:。|$)|若|每回合开始时|每回合结束时)"))
                throw new InvalidOperationException($"猎手中文描述未通过原版文风检查：{generated.ChineseDescription}");
        }

        foreach (var rarity in Enum.GetValues<GeneratedRarity>())
        {
            RandomCardGenerator? rarityGenerator = null;
            for (var i = 0; i < 250; i++)
            {
                if (i % 90 == 0)
                    rarityGenerator = new RandomCardGenerator(20260819 + (int)rarity * 1000 + i);
                var generated = rarityGenerator!.Generate(rarity);
                if (generated.Rarity != rarity)
                    throw new InvalidOperationException($"指定生成的稀有度 {rarity} 被重试流程改成了 {generated.Rarity}。");
            }
        }

        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.SingleEnemy, GeneratedRarity.Common, "造成6点伤害。", Array.Empty<CardTag>(),
            new[] { new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly, "造成6点伤害。", new Dictionary<string, int>()) }));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Attack, TargetMode.Other, GeneratedRarity.Common, "获得5点格挡。", Array.Empty<CardTag>(),
            new[] { new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得5点格挡。", new Dictionary<string, int>()) }));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            "获得等量于奥斯提最大生命值4倍的格挡。", Array.Empty<CardTag>(),
            [new GeneratorOperation("NCR:BlockTripleOstyMaxHp", OperationScope.NonTargeted,
                "获得等同于奥斯提最大生命值4倍的格挡。", new Dictionary<string, int>())],
            Character: GeneratedCharacter.Necrobinder));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Attack, TargetMode.Other, GeneratedRarity.Common, "对所有敌人造成5点伤害X次。", Array.Empty<CardTag>(),
            new[] { new GeneratorOperation("N:AllD", OperationScope.NonTargeted, "对所有敌人造成5点伤害X次。", new Dictionary<string, int>()) }));
        AssertInvalid(new GeneratedCard(-1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common, "获得5点格挡。", Array.Empty<CardTag>(),
            new[] { new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得5点格挡。", new Dictionary<string, int>()) }));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common, "斩杀时，获得5点格挡。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("C:if", OperationScope.ConditionalTrigger, "斩杀时。", new Dictionary<string, int>()),
                new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得5点格挡。", new Dictionary<string, int> { ["triggerIndex"] = 0 })
            }));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Common, "获得5点格挡。", Array.Empty<CardTag>(),
            new[] { new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得5点格挡。", new Dictionary<string, int>()) }));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Uncommon, "获得5点格挡。\n将这张牌放置于你的抽牌堆顶部。",
            [CardTag.Exhaust],
            [
                new GeneratorOperation("N:B", OperationScope.NonTargeted,
                    "获得5点格挡。", new Dictionary<string, int>()),
                new GeneratorOperation("R:PutThisOnDraw", OperationScope.Independent,
                    "将此牌放到抽牌堆顶。", new Dictionary<string, int>())
            ], Character: GeneratedCharacter.Regent));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Uncommon,
            "获得1点力量。\n将这张牌放置于你的抽牌堆顶部。", Array.Empty<CardTag>(),
            [
                new GeneratorOperation("N:Self", OperationScope.NonTargeted,
                    "获得1点力量。", new Dictionary<string, int>()),
                new GeneratorOperation("R:PutThisOnDraw", OperationScope.Independent,
                    "将此牌放到抽牌堆顶。", new Dictionary<string, int>())
            ], Character: GeneratedCharacter.Regent));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Uncommon, "获得5点格挡。\n将这张牌放回你的手牌。",
            [CardTag.Exhaust],
            [
                new GeneratorOperation("N:B", OperationScope.NonTargeted,
                    "获得5点格挡。", new Dictionary<string, int>()),
                new GeneratorOperation("R:ReturnThisToHand", OperationScope.Independent,
                    "将此牌放回手牌。", new Dictionary<string, int>())
            ], Character: GeneratedCharacter.Regent));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Uncommon,
            "获得1点力量。\n将这张牌放回你的手牌。", Array.Empty<CardTag>(),
            [
                new GeneratorOperation("N:Self", OperationScope.NonTargeted,
                    "获得1点力量。", new Dictionary<string, int>()),
                new GeneratorOperation("R:ReturnThisToHand", OperationScope.Independent,
                    "将此牌放回手牌。", new Dictionary<string, int>())
            ], Character: GeneratedCharacter.Regent));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Common, "本回合每当你打出一张攻击牌时，获得5点格挡。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("C:untilTurnEnd", OperationScope.ConditionalTrigger, "本回合每当你打出一张攻击牌时。", new Dictionary<string, int>()),
                new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得5点格挡。", new Dictionary<string, int> { ["triggerIndex"] = 0 })
            }));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Common, "获得3层覆甲。", Array.Empty<CardTag>(),
            new[] { new GeneratorOperation("N:Self", OperationScope.NonTargeted, "获得3层覆甲。", new Dictionary<string, int>()) }));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Common, "获得2点力量。", Array.Empty<CardTag>(),
            new[] { new GeneratorOperation("N:Self", OperationScope.NonTargeted, "获得2点力量。", new Dictionary<string, int>()) }));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Common, "在你的回合开始时，获得1点力量。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("A:turnStart", OperationScope.AbilityTrigger, "在你的回合开始时。", new Dictionary<string, int>()),
                new GeneratorOperation("N:Self", OperationScope.NonTargeted, "获得1点力量。", new Dictionary<string, int> { ["triggerIndex"] = 0 })
            }));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            "将你抽牌堆中的一张牌变化为灵魂。", [CardTag.Ethereal],
            [new GeneratorOperation("I:ProxyAtomic_Seance", OperationScope.Independent,
                "将你抽牌堆中的一张牌变化为灵魂。", new Dictionary<string, int>())],
            Character: GeneratedCharacter.Necrobinder));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Rare,
            "每当奥斯提失去生命时，将你抽牌堆中的一张牌变化为灵魂。", Array.Empty<CardTag>(),
            [
                new GeneratorOperation("A:whenOstyLosesHp", OperationScope.AbilityTrigger,
                    "每当奥斯提失去生命时。", new Dictionary<string, int>()),
                new GeneratorOperation("I:ProxyAtomic_Seance", OperationScope.Independent,
                    "将你抽牌堆中的一张牌变化为灵魂。", new Dictionary<string, int> { ["triggerIndex"] = 0 })
            ], Character: GeneratedCharacter.Necrobinder));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Common,
            "在你的回合开始时，造成10点伤害。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("A:turnStart", OperationScope.AbilityTrigger, "在你的回合开始时。", new Dictionary<string, int>()),
                new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly, "造成10点伤害。", new Dictionary<string, int> { ["triggerIndex"] = 0 })
            }));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Common,
            "在你的回合开始时，给予一名随机敌人6层灾厄。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("A:turnStart", OperationScope.AbilityTrigger, "在你的回合开始时。", new Dictionary<string, int>()),
                new GeneratorOperation("NCR:ApplyDoom", OperationScope.SingleEnemyOnly, "给予一名随机敌人6层灾厄。", new Dictionary<string, int> { ["triggerIndex"] = 0 })
            }, Character: GeneratedCharacter.Necrobinder));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common,
            "随机一名敌人失去7点生命。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("NCR:TargetHpLoss", OperationScope.SingleEnemyOnly, "随机一名敌人失去7点生命。", new Dictionary<string, int>())
            }, Character: GeneratedCharacter.Necrobinder));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Common,
            "每当你激发闪电充能球时，对被命中的敌人造成8点伤害。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("A:whenLightningEvoked", OperationScope.AbilityTrigger, "每当你激发闪电充能球时。", new Dictionary<string, int>()),
                new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly, "对被命中的敌人造成8点伤害。", new Dictionary<string, int> { ["triggerIndex"] = 0 })
            }, Character: GeneratedCharacter.Defect));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Attack, TargetMode.SingleEnemy, GeneratedRarity.Common,
            "对被命中的敌人造成8点伤害。", Array.Empty<CardTag>(),
            [new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly,
                "对被命中的敌人造成8点伤害。", new Dictionary<string, int>(), RequiresSingleTarget: true)],
            Character: GeneratedCharacter.Defect));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Attack, TargetMode.SingleEnemy, GeneratedRarity.Common, "造成6点伤害。斩杀时，获得5点格挡。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly, "造成6点伤害。", new Dictionary<string, int>()),
                new GeneratorOperation("C:if", OperationScope.ConditionalTrigger, "斩杀时。", new Dictionary<string, int>()),
                new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得5点格挡。", new Dictionary<string, int> { ["triggerIndex"] = 1 })
            }));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common,
            "在你的回合结束时，如果这张牌在你的消耗牌堆中，则将其打出。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("C:after", OperationScope.ConditionalTrigger, "在你的回合结束时，如果这张牌在你的消耗牌堆中。", new Dictionary<string, int>()),
                new GeneratorOperation("I:PlayThisCard", OperationScope.Independent, "则将其打出。", new Dictionary<string, int> { ["triggerIndex"] = 0 })
            }));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common,
            "获得5点格挡。本回合每当你打出一张攻击牌时，则将其打出。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得5点格挡。", new Dictionary<string, int>()),
                new GeneratorOperation("C:untilTurnEnd", OperationScope.ConditionalTrigger, "本回合每当你打出一张攻击牌时。", new Dictionary<string, int>()),
                new GeneratorOperation("I:PlayThisCard", OperationScope.Independent, "则将其打出。", new Dictionary<string, int> { ["triggerIndex"] = 1 })
            }));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common,
            "获得5点格挡。在你的回合结束时，如果这张牌在你的消耗牌堆中，则将其打出。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得5点格挡。", new Dictionary<string, int>()),
                new GeneratorOperation("C:after", OperationScope.ConditionalTrigger, "在你的回合结束时，如果这张牌在你的消耗牌堆中。", new Dictionary<string, int>()),
                new GeneratorOperation("I:PlayThisCard", OperationScope.Independent, "则将其打出。", new Dictionary<string, int> { ["triggerIndex"] = 1 })
            }));
        var selfExhaustTrigger = new GeneratorOperation("C:after", OperationScope.ConditionalTrigger,
            "这张牌被消耗时。", new Dictionary<string, int>(), CardTargetSlot: "thisCard");
        var exhaustPileTurnEndTrigger = new GeneratorOperation("C:after", OperationScope.ConditionalTrigger,
            "在你的回合结束时，如果这张牌在你的消耗牌堆中。", new Dictionary<string, int>(),
            CardTargetSlot: "thisCard");
        if (!CardEffectRules.IsSelfExhaustEventTrigger(selfExhaustTrigger)
            || CardEffectRules.IsSelfExhaustEventTrigger(exhaustPileTurnEndTrigger)
            || !CardEffectRules.IsExhaustPileTurnEndTrigger(exhaustPileTurnEndTrigger))
            throw new InvalidOperationException("被消耗瞬间与消耗牌堆回合结束触发器未被严格区分。");
        var selfExhaustBlock = new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得7点格挡。",
            new Dictionary<string, int> { ["triggerIndex"] = 0 });
        var selfExhaustHpLoss = new GeneratorOperation("N:HP-", OperationScope.NonTargeted, "失去2点生命。",
            new Dictionary<string, int> { ["triggerIndex"] = 0 });
        if (!CardEffectRules.HasNoNegativeSelfExhaustPayoffs([selfExhaustTrigger, selfExhaustBlock])
            || CardEffectRules.HasNoNegativeSelfExhaustPayoffs([selfExhaustTrigger, selfExhaustHpLoss]))
            throw new InvalidOperationException("被消耗时触发器没有拒绝负面后续，或错误拒绝了正面后续。");
        var exhaustPileTurnStartTrigger = new GeneratorOperation("R:AtTurnStartIfInExhaust",
            OperationScope.ConditionalTrigger, "在你的回合开始时，如果这张牌在你的消耗牌堆中。",
            new Dictionary<string, int>());
        var conditionalExtraHits = new GeneratorOperation("M:repeat", OperationScope.Modifier,
            "这张牌额外造成2次伤害。", new Dictionary<string, int> { ["triggerIndex"] = 0 });
        if (CardEffectRules.HasNoStateConditionModifiers(
                [exhaustPileTurnStartTrigger, conditionalExtraHits])
            || !CardEffectRules.HasNoStateConditionModifiers(
                [exhaustPileTurnStartTrigger, selfExhaustBlock]))
            throw new InvalidOperationException("卡牌所在牌堆的状态条件仍允许连接 modifier，或错误拒绝实际触发效果。");
        var drawTopTurnEndCondition = new GeneratorOperation("R:AtTurnEndWhenTopOfDraw",
            OperationScope.Modifier, "在你的回合结束时，如果这张牌位于你的抽牌堆顶部，",
            new Dictionary<string, int>());
        if (CardEffectRules.HasNoStateConditionModifiers(
                [drawTopTurnEndCondition, conditionalExtraHits]))
            throw new InvalidOperationException("抽牌堆顶部状态条件仍允许连接 modifier。");
        var playFromExhaustAtTurnStart = new GeneratorOperation("R:PlayThisCard", OperationScope.Independent,
            "则将其打出。", new Dictionary<string, int> { ["triggerIndex"] = 1 });
        var ironcladReplayAtom = new ComponentAtom("I:PlayThisCard", OperationScope.Independent,
            "则将其打出。", false, CardReferenceRequirement.ThisCard);
        var regentReplayAtom = new ComponentAtom("R:PlayThisCard", OperationScope.Independent,
            "打出此牌。", false, CardReferenceRequirement.None);
        var unrelatedPayoffAtom = new ComponentAtom("N:B", OperationScope.NonTargeted,
            "获得5点格挡。", false, CardReferenceRequirement.None);
        if (EffectSelectionTuning.NativeExhaustReplayCompanionWeight([ironcladReplayAtom],
                [exhaustPileTurnEndTrigger]) <= 100
            || EffectSelectionTuning.NativeExhaustReplayCompanionWeight([regentReplayAtom],
                [exhaustPileTurnStartTrigger]) <= 100
            || EffectSelectionTuning.NativeExhaustReplayCompanionWeight([unrelatedPayoffAtom],
                [exhaustPileTurnEndTrigger]) != 100
            || EffectSelectionTuning.NativeExhaustReplayCompanionWeight([regentReplayAtom],
                [selfExhaustTrigger]) != 100)
            throw new InvalidOperationException("消耗牌堆触发器的原版重放搭配权重或隔离规则失效。");
        CardTemplateValidator.Validate(new GeneratedCard(2, GeneratedCardType.Attack, TargetMode.SingleEnemy,
            GeneratedRarity.Rare,
            "造成18点伤害。在你的回合开始时，如果这张牌在你的消耗牌堆中，则将其打出。",
            [CardTag.Exhaust],
            [
                new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly, "造成18点伤害。",
                    new Dictionary<string, int>(), RequiresSingleTarget: true),
                exhaustPileTurnStartTrigger,
                playFromExhaustAtTurnStart
            ], Character: GeneratedCharacter.Regent));
        AssertInvalid(new GeneratedCard(2, GeneratedCardType.Attack, TargetMode.SingleEnemy, GeneratedRarity.Rare,
            "在你的回合开始时，如果这张牌在你的消耗牌堆中，则将其打出。造成18点伤害。",
            [CardTag.Exhaust],
            [
                exhaustPileTurnStartTrigger,
                playFromExhaustAtTurnStart with { Parameters = new Dictionary<string, int> { ["triggerIndex"] = 0 } },
                new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly, "造成18点伤害。",
                    new Dictionary<string, int>(), RequiresSingleTarget: true)
            ], Character: GeneratedCharacter.Regent));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common,
            "将此牌的一张复制加入弃牌堆。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("N:Create", OperationScope.NonTargeted, "将此牌的一张复制加入弃牌堆。", new Dictionary<string, int>())
            }));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common,
            "获得5点格挡。将此牌的一张复制加入弃牌堆。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得5点格挡。", new Dictionary<string, int>()),
                new GeneratorOperation("N:Create", OperationScope.NonTargeted, "将此牌的一张复制加入弃牌堆。", new Dictionary<string, int>())
            }));
        var zeroCostCopy = new GeneratorOperation("D:CreateZeroCostCopyInDiscard", OperationScope.NonTargeted,
            "将此牌的一张0费复制品加入弃牌堆。", new Dictionary<string, int>());
        var ordinaryCopy = new GeneratorOperation("N:Create", OperationScope.NonTargeted,
            "将一张此牌的复制品加入你的弃牌堆。", new Dictionary<string, int>());
        var necrobinderCopy = new GeneratorOperation("NCR:CreateCopyInDiscard", OperationScope.NonTargeted,
            "将此牌的一张复制加入弃牌堆。", new Dictionary<string, int>());
        if (!CardEffectRules.IsCopyThisCardToDiscard(zeroCostCopy)
            || !CardEffectRules.IsCopyThisCardToDiscard(necrobinderCopy)
            || CardEffectRules.HasValidCopyThisCardAssembly([zeroCostCopy])
            || CardEffectRules.HasValidCopyThisCardAssembly([zeroCostCopy, selfHpLoss])
            || !CardEffectRules.HasValidCopyThisCardAssembly([zeroCostCopy, ordinaryBlock]))
            throw new InvalidOperationException("普通复制与0费复制都必须搭配另一项实际正面效果；负面效果不能满足约束。");
        AssertInvalid(new GeneratedCard(0, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Rare, "获得6点格挡。将这张牌的一张0费复制品加入弃牌堆。", [],
            [ordinaryBlock, zeroCostCopy]));
        var ordinaryCopyAtom = new ComponentAtom(ordinaryCopy.Template, ordinaryCopy.Scope,
            ordinaryCopy.ChineseText, false, CardReferenceRequirement.None);
        if (CardEffectRules.CopyThisCardBudgetRole(ordinaryCopy, true, GeneratedCardType.Skill, [])
                != CopyThisCardBudgetRole.PaidReusableDownside
            || CardEffectRules.CopyThisCardBudgetRole(ordinaryCopy, false, GeneratedCardType.Skill, [])
                != CopyThisCardBudgetRole.FreeReusableBenefit
            || CardEffectRules.CopyThisCardBudgetRole(ordinaryCopy, true, GeneratedCardType.Skill,
                [CardTag.Exhaust]) != CopyThisCardBudgetRole.ExhaustOffset
            || CardEffectRules.CopyThisCardBudgetRole(ordinaryCopy, true, GeneratedCardType.Power, [])
                != CopyThisCardBudgetRole.PowerBenefit
            || CardEffectRules.CopyThisCardBudgetRole(zeroCostCopy, true, GeneratedCardType.Skill, [])
                != CopyThisCardBudgetRole.None
            || EffectBalanceModel.EstimatedEffectValue(ordinaryCopyAtom)
                != CopyThisCardValuation.FreeReusableRewardValue
            || EffectBalanceModel.EstimatedPositiveCardValue([ordinaryCopy], false,
                GeneratedCardType.Skill, []) != CopyThisCardValuation.FreeReusableRewardValue
            || EffectBalanceModel.EstimatedPositiveCardValue([ordinaryCopy], true,
                GeneratedCardType.Skill, []) != 0d
            || EffectBalanceModel.EstimatedPositiveCardValue([ordinaryCopy], true,
                GeneratedCardType.Power, []) != CopyThisCardValuation.PowerRewardValue
            || EffectBalanceModel.EstimatedPositiveCardValue([ordinaryBlock, zeroCostCopy], true,
                GeneratedCardType.Skill, []) != EffectBalanceModel.EstimatedPositiveCardValue(
                    [ordinaryBlock], true, GeneratedCardType.Skill, [])
            || Math.Abs(NegativeEffectTuning.TotalMultiplier([ordinaryCopy], [], true,
                GeneratedCardType.Skill) - CopyThisCardValuation.PaidReusableDownsideMultiplier) > 0.0001d
            || NegativeEffectTuning.TotalMultiplier([ordinaryCopy], [CardTag.Exhaust], true,
                GeneratedCardType.Skill) != 1d
            || NegativeEffectTuning.TotalMultiplier([], [CardTag.Exhaust], true,
                GeneratedCardType.Skill) != 1.45d
            || !CardEffectRules.IsPersistentPowerFoundation(ordinaryCopy)
            || EffectSelectionTuning.CopyThisCardPowerWeight(ordinaryCopyAtom,
                GeneratedRarity.Common, GeneratedCardType.Power) != 3
            || EffectSelectionTuning.CopyThisCardPowerWeight(ordinaryCopyAtom,
                GeneratedRarity.Rare, GeneratedCardType.Power) != 30
            || EffectSelectionTuning.CopyThisCardPowerWeight(ordinaryCopyAtom,
                GeneratedRarity.Common, GeneratedCardType.Skill) != 100)
            throw new InvalidOperationException("弃牌堆自身复制没有按费用、消耗与能力牌上下文正确计价。 ");

        var rareZeroCostBlockTwentySix = new GeneratorOperation("N:B", OperationScope.NonTargeted,
            "获得26点格挡。", new Dictionary<string, int> { ["block"] = 26 });
        var rareZeroCostExhaustCopyOperations = new[] { rareZeroCostBlockTwentySix, ordinaryCopy };
        var rareZeroCostBounds = ComponentAssemblyGenerator.WholeCardBudgetBounds(GeneratedRarity.Rare, 0d, 1,
            balancedValues: true, character: GeneratedCharacter.Ironclad);
        var rareZeroCostExhaustCopyValue = EffectBalanceModel.EstimatedPositiveCardValue(
            rareZeroCostExhaustCopyOperations, false, GeneratedCardType.Skill, [CardTag.Exhaust]);
        if (Math.Abs(rareZeroCostBounds.Maximum - 1_876.8d) > 0.001d
            || rareZeroCostExhaustCopyValue != 3_120d
            || ComponentAssemblyGenerator.IsWithinWholeCardBudgetEnvelope(rareZeroCostExhaustCopyOperations,
                GeneratedRarity.Rare, 0d, GeneratedCardType.Skill, [CardTag.Exhaust], false,
                true, GeneratedCharacter.Ironclad, false))
            throw new InvalidOperationException(
                "0费稀有的26格挡/弃牌堆复制/消耗组合错误地通过了数值平衡上界。 ");

        var exhaustPileCondition = new GeneratorOperation("R:AtTurnStartIfInExhaust",
            OperationScope.ConditionalTrigger, "在你的回合开始时，如果这张牌在你的消耗牌堆中。",
            new Dictionary<string, int>());
        var incorrectlyNestedReturn = new GeneratorOperation("R:ReturnAfterSkillsPlayed",
            OperationScope.Independent, "每打出2张技能牌，将此牌从弃牌堆放入手牌。",
            new Dictionary<string, int> { ["triggerIndex"] = 0 });
        if (CardEffectRules.HasNoInvalidTriggeredStateEffects(
                [exhaustPileCondition, incorrectlyNestedReturn])
            || !CardEffectRules.HasNoInvalidTriggeredStateEffects([incorrectlyNestedReturn with
                { Parameters = new Dictionary<string, int>() }]))
            throw new InvalidOperationException("卡牌状态型效果仍可被错误嵌套在其他条件触发器之后。 ");
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other,
            GeneratedRarity.Rare, "获得5点格挡。每当你打出一张攻击牌时，结束你的回合。",
            Array.Empty<CardTag>(),
            [
                ordinaryBlock,
                attackPlayedTrigger,
                gatedEndTurn with { Parameters = new Dictionary<string, int> { ["triggerIndex"] = 1 } }
            ]));
        var playTopAndExhaust = new GeneratorOperation("I:PlayTopCardAndExhaust",
            OperationScope.Independent, "打出抽牌堆顶部的牌并消耗该牌。", new Dictionary<string, int>());
        var enemyGainStrength = new GeneratorOperation("T:Apply", OperationScope.SingleEnemyOnly,
            "使该敌人获得1点力量。", new Dictionary<string, int>(), RequiresSingleTarget: true);
        var enemyGainTwoStrength = new GeneratorOperation("T:Apply", OperationScope.SingleEnemyOnly,
            "使该敌人获得2点力量。", new Dictionary<string, int>(), RequiresSingleTarget: true);
        if (CardEffectRules.IsNegativeEffect(playTopAndExhaust)
            || !CardEffectRules.IsBeneficialEffect(playTopAndExhaust)
            || NegativeEffectTuning.BaseMultiplier(playTopAndExhaust) != 1d
            || NegativeEffectTuning.BaseMultiplier(enemyGainStrength) != 1d
            || NegativeEffectTuning.LinearCompensationValue(enemyGainStrength) != 1_000d
            || NegativeEffectTuning.LinearCompensationValue(enemyGainTwoStrength) != 2_000d
            || NumericGenerationTuning.ThinEnemyStrengthGainTail(1, 99) != 1
            || NumericGenerationTuning.ThinEnemyStrengthGainTail(3, 84) != 1
            || NumericGenerationTuning.ThinEnemyStrengthGainTail(3, 85) != 2
            || NumericGenerationTuning.ThinEnemyStrengthGainTail(3, 97) != 2
            || NumericGenerationTuning.ThinEnemyStrengthGainTail(3, 98) != 2)
            throw new InvalidOperationException("牌堆顶打出并消耗必须是纯正面效果；给予敌人力量必须保留足额负面补偿。 ");
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common,
            "你在本回合中每打出过一张攻击牌，其耗能减少1。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("C:whileInCombat", OperationScope.ConditionalTrigger, "你在本回合中每打出过一张攻击牌，其耗能减少1。", new Dictionary<string, int>())
            }));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common,
            "获得5点格挡。你在本回合中每打出过一张攻击牌，其耗能减少1。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得5点格挡。", new Dictionary<string, int>()),
                new GeneratorOperation("C:whileInCombat", OperationScope.ConditionalTrigger, "你在本回合中每打出过一张攻击牌，其耗能减少1。", new Dictionary<string, int>())
            }));

        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common, "每消耗一张牌，获得5点格挡。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("C:forEach", OperationScope.ConditionalTrigger, "每消耗一张牌。", new Dictionary<string, int>()),
                new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得5点格挡。", new Dictionary<string, int> { ["triggerIndex"] = 0 })
            }));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common, "消耗所有手牌。每消耗一张牌，获得5点格挡。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("N:Exhaust", OperationScope.NonTargeted, "消耗所有手牌。", new Dictionary<string, int>()),
                new GeneratorOperation("C:forEach", OperationScope.ConditionalTrigger, "每消耗一张牌。", new Dictionary<string, int>()),
                new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得5点格挡。", new Dictionary<string, int> { ["triggerIndex"] = 1 })
            }));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Attack, TargetMode.SingleEnemy, GeneratedRarity.Common, "造成10点伤害。在本场战斗中，此卡的基础伤害增加5点。", new[] { CardTag.Exhaust },
            new[]
            {
                new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly, "造成10点伤害。", new Dictionary<string, int>()),
                new GeneratorOperation("I:IncreaseDamageThisCombat", OperationScope.Independent, "在本场战斗中，此卡的基础伤害增加5点。", new Dictionary<string, int>())
            }));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Common, "获得2点力量。", new[] { CardTag.Exhaust },
            new[] { new GeneratorOperation("N:Self", OperationScope.NonTargeted, "获得2点力量。", new Dictionary<string, int>()) }));

        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Common, "消耗手牌中的一张牌。", Array.Empty<CardTag>(),
            new[] { new GeneratorOperation("N:Exhaust", OperationScope.NonTargeted,
                "消耗手牌中的一张牌。", new Dictionary<string, int>()) }));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common, "失去3点生命。", Array.Empty<CardTag>(),
            new[] { new GeneratorOperation("N:HP-", OperationScope.NonTargeted, "失去3点生命。", new Dictionary<string, int>()) }));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.SingleEnemy, GeneratedRarity.Common, "使该敌人获得1点力量。", Array.Empty<CardTag>(),
            new[] { new GeneratorOperation("T:Apply", OperationScope.SingleEnemyOnly, "使该敌人获得1点力量。", new Dictionary<string, int>()) }));
        CardTemplateValidator.Validate(new GeneratedCard(0, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common, "消耗手牌中的一张牌。", Array.Empty<CardTag>(),
            new[] { new GeneratorOperation("N:Exhaust", OperationScope.NonTargeted, "消耗手牌中的一张牌。", new Dictionary<string, int>()) }));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common, "失去3点生命。\n获得5点格挡。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("N:HP-", OperationScope.NonTargeted, "失去3点生命。", new Dictionary<string, int>()),
                new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得5点格挡。", new Dictionary<string, int>())
            }));

        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common,
            "获得等同于目标易伤层数的力量。", Array.Empty<CardTag>(),
            new[] { new GeneratorOperation("N:Self", OperationScope.NonTargeted, "获得等同于目标易伤层数的力量。", new Dictionary<string, int>(), RequiresSingleTarget: true) }));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.SingleEnemy, GeneratedRarity.Common,
            "获得等同于目标易伤层数的力量。", Array.Empty<CardTag>(),
            new[] { new GeneratorOperation("N:Self", OperationScope.NonTargeted, "获得等同于目标易伤层数的力量。", new Dictionary<string, int>(), RequiresSingleTarget: true) }));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Attack, TargetMode.Other, GeneratedRarity.Common,
            "对攻击者造成4点伤害。", Array.Empty<CardTag>(),
            new[] { new GeneratorOperation("N:RetaliateDamage", OperationScope.NonTargeted, "对攻击者造成4点伤害。", new Dictionary<string, int>()) }));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            "回复10点生命。", Array.Empty<CardTag>(),
            new[] { new GeneratorOperation("N:Heal", OperationScope.NonTargeted, "回复10点生命。", new Dictionary<string, int>()) }));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            "回复10点生命。", new[] { CardTag.Exhaust },
            new[] { new GeneratorOperation("N:Heal", OperationScope.NonTargeted, "回复10点生命。", new Dictionary<string, int>()) }));
        var repeatedRestrictedTrigger = new GeneratorOperation("A:whenSkillPlayed", OperationScope.AbilityTrigger,
            "每当你打出一张技能牌时。", new Dictionary<string, int>());
        var triggeredHeal = new GeneratorOperation("N:Heal", OperationScope.NonTargeted, "回复2点生命。",
            new Dictionary<string, int> { ["triggerIndex"] = 0 });
        var oneShotFatal = new GeneratorOperation("C:ifFatal", OperationScope.ConditionalTrigger,
            "斩杀时。", new Dictionary<string, int>());
        var triggeredMaxHp = new GeneratorOperation("I:GainMaxHp", OperationScope.Independent,
            "永久获得3点最大生命。", new Dictionary<string, int> { ["triggerIndex"] = 0 });
        if (CardEffectRules.HasNoRepeatedTriggeredRestrictedEffects([repeatedRestrictedTrigger, triggeredHeal])
            || !CardEffectRules.HasNoRepeatedTriggeredRestrictedEffects([oneShotFatal, triggeredMaxHp]))
            throw new InvalidOperationException("战斗外限制效果必须拒绝重复触发，但允许斩杀等一次性条件。 ");
        var directHealTen = new GeneratorOperation("N:Heal", OperationScope.NonTargeted,
            "回复10点生命。", new Dictionary<string, int>());
        if (EffectBalanceModel.EstimatedEffectValue(triggeredMaxHp) != 2_700
            || EffectBalanceModel.EstimatedEffectValue(directHealTen) != 5_000
            || !ComponentAssemblyGenerator.IsExplicitRareTemplate(triggeredMaxHp.Template))
            throw new InvalidOperationException("回血与最大生命没有使用提高后的价值及稀有效果管线。 ");
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Rare,
            "每当你打出一张技能牌时，回复2点生命。", Array.Empty<CardTag>(),
            [repeatedRestrictedTrigger, triggeredHeal], Character: GeneratedCharacter.Necrobinder));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            "永久获得3点最大生命。", Array.Empty<CardTag>(),
            new[] { new GeneratorOperation("I:GainMaxHp", OperationScope.Independent, "永久获得3点最大生命。", new Dictionary<string, int>()) }));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Rare,
            "获得1点力量。\n回复10点生命。", Array.Empty<CardTag>(),
            new[]
            {
                new GeneratorOperation("N:Self", OperationScope.NonTargeted, "获得1点力量。", new Dictionary<string, int>()),
                new GeneratorOperation("N:Heal", OperationScope.NonTargeted, "回复10点生命。", new Dictionary<string, int>())
            }));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Rare,
            "回复10点生命。", Array.Empty<CardTag>(),
            [new GeneratorOperation("N:Heal", OperationScope.NonTargeted,
                "回复10点生命。", new Dictionary<string, int>())]));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            "获得一瓶随机药水。", Array.Empty<CardTag>(),
            [new GeneratorOperation("CL:ProxyAtomic_Alchemize", OperationScope.Independent,
                "获得一瓶随机药水。", new Dictionary<string, int>())], Character: GeneratedCharacter.Colorless));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            "获得一瓶随机药水。", [CardTag.Exhaust],
            [new GeneratorOperation("CL:ProxyAtomic_Alchemize", OperationScope.Independent,
                "获得一瓶随机药水。", new Dictionary<string, int>())], Character: GeneratedCharacter.Colorless));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Rare,
            "获得一瓶随机药水。", Array.Empty<CardTag>(),
            [new GeneratorOperation("CL:ProxyAtomic_Alchemize", OperationScope.Independent,
                "获得一瓶随机药水。", new Dictionary<string, int>())], Character: GeneratedCharacter.Colorless));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            "获得20金币。", Array.Empty<CardTag>(),
            [new GeneratorOperation("CL:GainGold", OperationScope.NonTargeted,
                "获得20金币。", new Dictionary<string, int>())], Character: GeneratedCharacter.Colorless));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            "获得20金币。", [CardTag.Exhaust],
            [new GeneratorOperation("CL:GainGold", OperationScope.NonTargeted,
                "获得20金币。", new Dictionary<string, int>())], Character: GeneratedCharacter.Colorless));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Rare,
            "获得20金币。", Array.Empty<CardTag>(),
            [new GeneratorOperation("CL:GainGold", OperationScope.NonTargeted,
                "获得20金币。", new Dictionary<string, int>())], Character: GeneratedCharacter.Colorless));

        var royaltiesOperation = new GeneratorOperation("A:ProxyAtomic_Royalties", OperationScope.AbilityRule,
            "在战斗结束时，获得30金币。", new Dictionary<string, int>());
        var royaltiesCard = new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other,
            GeneratedRarity.Rare, CardDescriptionRenderer.Render([royaltiesOperation]), [],
            [royaltiesOperation], EnglishDescription: EnglishCardDescriptionRenderer.Render([royaltiesOperation]),
            Character: GeneratedCharacter.Regent);
        var royaltiesNumericPlans = Enumerable.Range(0, 500)
            .Select(seed => CardUpgradeGenerator.Generate(royaltiesCard, new Random(seed)))
            .Where(plan => plan.Effects.Any(effect => effect.Kind == CardUpgradeKind.IncreaseNumber))
            .ToArray();
        if (royaltiesNumericPlans.Length == 0
            || royaltiesNumericPlans.SelectMany(plan => plan.Effects)
                .Where(effect => effect.Kind == CardUpgradeKind.IncreaseNumber)
                .Any(effect => effect.Delta is < 8 or > 13)
            || royaltiesNumericPlans.Any(plan => !Regex.IsMatch(plan.UpgradedChineseDescription,
                @"获得(?:3[8-9]|4[0-3])金币")))
            throw new InvalidOperationException("王国资产式30金币效果仍生成过弱的+1升级，或升级文本没有应用原版比例。 ");
        foreach (var plan in royaltiesNumericPlans.Take(8))
            CardTemplateValidator.Validate(royaltiesCard with { Upgrade = plan });

        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Rare,
            "永久获得3点最大生命值。", Array.Empty<CardTag>(),
            [new GeneratorOperation("I:GainMaxHp", OperationScope.Independent,
                "永久获得3点最大生命。", new Dictionary<string, int>())]));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Rare,
            "获得1点格挡。\n本局游戏中，此牌的基础格挡增加3点。", Array.Empty<CardTag>(),
            [
                new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得1点格挡。", new Dictionary<string, int>()),
                new GeneratorOperation("D:IncreaseThisCardBlockRun", OperationScope.Independent,
                    "本局游戏中，此牌的基础格挡增加3点。", new Dictionary<string, int>())
            ], Character: GeneratedCharacter.Defect));
        AssertInvalid(new GeneratedCard(2, GeneratedCardType.Attack, TargetMode.SingleEnemy, GeneratedRarity.Rare,
            "造成13点伤害。\n本局游戏中，此牌的基础伤害增加5点。", Array.Empty<CardTag>(),
            [
                new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly, "造成13点伤害。", new Dictionary<string, int>(), RequiresSingleTarget: true),
                new GeneratorOperation("NCR:IncreaseThisCardDamageRun", OperationScope.Independent,
                    "本局游戏中，此牌的基础伤害增加5点。", new Dictionary<string, int>())
            ], Character: GeneratedCharacter.Necrobinder));
        CardTemplateValidator.Validate(new GeneratedCard(2, GeneratedCardType.Attack, TargetMode.SingleEnemy, GeneratedRarity.Rare,
            "造成13点伤害。\n本局游戏中，此牌的基础伤害增加5点。", [CardTag.Exhaust],
            [
                new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly, "造成13点伤害。", new Dictionary<string, int>(), RequiresSingleTarget: true),
                new GeneratorOperation("NCR:IncreaseThisCardDamageRun", OperationScope.Independent,
                    "本局游戏中，此牌的基础伤害增加5点。", new Dictionary<string, int>())
            ], Character: GeneratedCharacter.Necrobinder));

        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Attack, TargetMode.SingleEnemy, GeneratedRarity.Rare,
            "这张牌就造成10点伤害。", Array.Empty<CardTag>(),
            [new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly,
                "这张牌就造成10点伤害。", new Dictionary<string, int>(), RequiresSingleTarget: true)],
            Character: GeneratedCharacter.Colorless));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Attack, TargetMode.SingleEnemy, GeneratedRarity.Rare,
            "本场战斗每打出一张牌，这张牌就造成10点伤害。", Array.Empty<CardTag>(),
            [
                new GeneratorOperation("CL:ForEachCardPlayedCombat", OperationScope.Modifier,
                    "本场战斗每打出一张牌，", new Dictionary<string, int>()),
                new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly,
                    "这张牌就造成10点伤害。", new Dictionary<string, int>(), RequiresSingleTarget: true)
            ], Character: GeneratedCharacter.Colorless));

        var dieOnUnblockedAttack = new GeneratorOperation("CL:DieOnUnblockedAttack", OperationScope.AbilityRule,
            "如果你在本场战斗中受到未被格挡的攻击伤害，则立刻死亡。", new Dictionary<string, int>());
        var cannotGainBlock = new GeneratorOperation("CL:NoBlockFromCards", OperationScope.Independent,
            "你在接下来的2回合内无法再从卡牌中获得格挡。", new Dictionary<string, int>());
        if (!CardEffectRules.IsNegativeEffect(dieOnUnblockedAttack)
            || CardEffectRules.IsBeneficialEffect(dieOnUnblockedAttack)
            || !CardEffectRules.IsNegativeEffect(cannotGainBlock)
            || CardEffectRules.IsBeneficialEffect(cannotGainBlock))
            throw new InvalidOperationException("未格挡即死与接下来若干回合不能获得格挡必须保持为纯负面效果。");
        AssertInvalid(new GeneratedCard(0, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            "如果你在本场战斗中受到未被格挡的攻击伤害，则立刻死亡。", Array.Empty<CardTag>(), [dieOnUnblockedAttack],
            Character: GeneratedCharacter.Colorless));
        AssertInvalid(new GeneratedCard(0, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            "你在接下来的2回合内无法再从卡牌中获得格挡。", Array.Empty<CardTag>(), [cannotGainBlock],
            Character: GeneratedCharacter.Colorless));
        CardTemplateValidator.Validate(new GeneratedCard(0, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            "获得20点格挡。\n如果你在本场战斗中受到未被格挡的攻击伤害，则立刻死亡。", Array.Empty<CardTag>(),
            [
                new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得20点格挡。", new Dictionary<string, int>()),
                dieOnUnblockedAttack
            ], Character: GeneratedCharacter.Colorless));
        CardTemplateValidator.Validate(new GeneratedCard(0, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            "获得50点格挡。\n如果你在本场战斗中受到未被格挡的攻击伤害，则立刻死亡。", Array.Empty<CardTag>(),
            [
                new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得50点格挡。", new Dictionary<string, int>()),
                dieOnUnblockedAttack
            ], Character: GeneratedCharacter.Colorless));
        CardTemplateValidator.Validate(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Uncommon,
            "获得30点格挡。\n你在接下来的2回合内无法再从卡牌中获得格挡。", [CardTag.Exhaust],
            [
                new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得30点格挡。", new Dictionary<string, int>()),
                cannotGainBlock
            ], Character: GeneratedCharacter.Colorless));
        CardTemplateValidator.Validate(new GeneratedCard(0, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Uncommon,
            "获得30点格挡。\n你在接下来的2回合内无法再从卡牌中获得格挡。", [CardTag.Exhaust],
            [
                new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得30点格挡。", new Dictionary<string, int>()),
                cannotGainBlock
            ], Character: GeneratedCharacter.Colorless));

        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.SingleEnemy, GeneratedRarity.Common,
            "给予等量于所造成伤害的灾厄。", Array.Empty<CardTag>(),
            [new GeneratorOperation("NCR:ApplyDoomEqualDamage", OperationScope.SingleEnemyOnly,
                "给予等量于所造成伤害的灾厄。", new Dictionary<string, int>(), RequiresSingleTarget: true)],
            Character: GeneratedCharacter.Necrobinder));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common,
            "本场战斗中，所有拥有此效果的牌基础伤害增加2点。", Array.Empty<CardTag>(),
            [new GeneratorOperation("D:IncreaseAllClaws", OperationScope.Independent,
                "本场战斗中，所有拥有此效果的牌基础伤害增加2点。", new Dictionary<string, int>())],
            Character: GeneratedCharacter.Defect));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Attack, TargetMode.SingleEnemy,
            GeneratedRarity.Uncommon, "造成8点伤害。\n本场战斗中，所有拥有此效果的牌基础伤害增加2点。",
            [CardTag.Exhaust],
            [
                new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly, "造成8点伤害。",
                    new Dictionary<string, int>(), RequiresSingleTarget: true),
                new GeneratorOperation("D:IncreaseAllClaws", OperationScope.Independent,
                    "本场战斗中，所有拥有此效果的牌基础伤害增加2点。", new Dictionary<string, int>())
            ], Character: GeneratedCharacter.Defect));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Uncommon, "获得8点格挡。\n这张牌的耗能增加1。", [CardTag.Exhaust],
            [
                new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得8点格挡。",
                    new Dictionary<string, int>()),
                new GeneratorOperation("D:IncreaseThisCardCost", OperationScope.Independent,
                    "这张牌的耗能增加1。", new Dictionary<string, int>())
            ], Character: GeneratedCharacter.Defect));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            "获得1点格挡。\n本局游戏中，此牌的基础格挡增加3点。", Array.Empty<CardTag>(),
            [
                new GeneratorOperation("N:B", OperationScope.NonTargeted, "获得1点格挡。", new Dictionary<string, int>()),
                new GeneratorOperation("D:IncreaseThisCardBlockRun", OperationScope.Independent,
                    "本局游戏中，此牌的基础格挡增加3点。", new Dictionary<string, int>())
            ], Character: GeneratedCharacter.Defect));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common,
            "本回合失去3点集中。", Array.Empty<CardTag>(),
            [new GeneratorOperation("D:LoseTemporaryFocus", OperationScope.NonTargeted,
                "本回合失去3点集中。", new Dictionary<string, int>())], Character: GeneratedCharacter.Defect));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Uncommon,
            "在你的回合开始时，本回合失去2点集中。", Array.Empty<CardTag>(),
            [
                new GeneratorOperation("A:turnStart", OperationScope.AbilityTrigger,
                    "在你的回合开始时。", new Dictionary<string, int>()),
                new GeneratorOperation("D:LoseTemporaryFocus", OperationScope.NonTargeted,
                    "本回合失去2点集中。", new Dictionary<string, int> { ["triggerIndex"] = 0 })
            ], Character: GeneratedCharacter.Defect));
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common,
            "如果奥斯提在本回合攻击过，则这张牌的耗能变为0。", Array.Empty<CardTag>(),
            [
                new GeneratorOperation("NCR:IfOstyAttackedThisTurn", OperationScope.ConditionalTrigger,
                    "如果奥斯提在本回合攻击过。", new Dictionary<string, int>()),
                new GeneratorOperation("NCR:SetCostZeroIfOstyAttacked", OperationScope.Independent,
                    "这张牌的耗能变为0。", new Dictionary<string, int> { ["triggerIndex"] = 0 })
            ], Character: GeneratedCharacter.Necrobinder));

        foreach (var (character, cardId) in new[]
                 {
                     (GeneratedCharacter.Defect, "MachineLearning"),
                     (GeneratedCharacter.Necrobinder, "CallOfTheVoid"),
                     (GeneratedCharacter.Regent, "BigBang")
                 })
        {
            var recipe = CharacterComponentCatalogs.Get(character).Recipes.Single(candidate => candidate.Id == cardId);
            var atom = recipe.Atoms[0];
            var operation = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
                new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget);
            var upgradeProbe = new GeneratedCard(recipe.Cost, recipe.Type, recipe.Target, recipe.OriginalRarity,
                atom.ChineseText, recipe.Tags, [operation], Character: character);
            if (!Enumerable.Range(0, 500).Select(seed => CardUpgradeGenerator.Generate(upgradeProbe, new Random(seed)))
                    .Any(upgrade => GeneratedCardTagPolicy.AddedKeywords(upgrade).Contains(CardTag.Innate)))
                throw new InvalidOperationException($"{character} 的原版固有升级路径未进入共享升级生成器：{cardId}。");
        }

        var poorExhaustUpgradeProbe = new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Rare, "抽1张牌。", [CardTag.Exhaust],
            [new GeneratorOperation("N:Draw", OperationScope.NonTargeted, "抽1张牌。",
                new Dictionary<string, int>())], Character: GeneratedCharacter.Silent);
        var fairExhaustUpgradeProbe = poorExhaustUpgradeProbe with
        {
            ChineseDescription = "造成12点伤害。",
            Type = GeneratedCardType.Attack,
            Target = TargetMode.SingleEnemy,
            Operations = [new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly,
                "造成12点伤害。", new Dictionary<string, int>(), RequiresSingleTarget: true)]
        };
        var strongExhaustUpgradeProbe = fairExhaustUpgradeProbe with
        {
            ChineseDescription = "造成20点伤害。",
            Operations = [new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly,
                "造成20点伤害。", new Dictionary<string, int>(), RequiresSingleTarget: true)]
        };
        var poorRemoveExhaustChance = CardUpgradeGenerator.RemoveExhaustUpgradeChanceBasisPoints(
            poorExhaustUpgradeProbe);
        var fairRemoveExhaustChance = CardUpgradeGenerator.RemoveExhaustUpgradeChanceBasisPoints(
            fairExhaustUpgradeProbe);
        var strongRemoveExhaustChance = CardUpgradeGenerator.RemoveExhaustUpgradeChanceBasisPoints(
            strongExhaustUpgradeProbe);
        if (poorRemoveExhaustChance != 990 || fairRemoveExhaustChance != 770
            || strongRemoveExhaustChance != 242
            || !(poorRemoveExhaustChance > fairRemoveExhaustChance
                && fairRemoveExhaustChance > strongRemoveExhaustChance)
            || poorRemoveExhaustChance >= 1_100)
            throw new InvalidOperationException("去除消耗升级没有随整卡亏模程度单调增加，或总体概率未降低。 ");

        var regentCatalog = CharacterComponentCatalogs.Get(GeneratedCharacter.Regent);
        if (CardEffectRules.StarCostEnergyEquivalent(1) != 0
            || CardEffectRules.StarCostEnergyEquivalent(2) != 1
            || CardEffectRules.StarCostEnergyEquivalent(5) != 2)
            throw new InvalidOperationException("蓝星费用没有按每2蓝星折算1点普通耗能预算。 ");
        var ancientDownsideProbe = new ComponentAtom("N:HP-", OperationScope.NonTargeted,
            "失去3点生命。", false, CardReferenceRequirement.None);
        if (EffectSelectionTuning.DownsideRarityWeight(ancientDownsideProbe, GeneratedRarity.Ancient) != 16
            || EffectSelectionTuning.DownsideRarityWeight(ancientDownsideProbe, GeneratedRarity.Basic) != 30
            || EffectSelectionTuning.DownsideRarityWeight(ancientDownsideProbe, GeneratedRarity.Rare) != 100
            || EffectSelectionTuning.BasicDownsideVariantWeight(ancientDownsideProbe,
                GeneratedRarity.Basic) != 30
            || EffectSelectionTuning.NegativeKeywordRarityWeight(CardTag.Exhaust,
                GeneratedRarity.Basic) != 30
            || EffectSelectionTuning.NegativeKeywordRarityWeight(CardTag.Ethereal,
                GeneratedRarity.Ancient) != 16
            || EffectSelectionTuning.NegativeKeywordRarityWeight(CardTag.Retain,
                GeneratedRarity.Basic) != 100
            || ComponentAssemblyGenerator.AdjustEtherealTagNumerator(100,
                GeneratedCharacter.Necrobinder, false) != 150
            || ComponentAssemblyGenerator.AdjustEtherealTagNumerator(100,
                GeneratedCharacter.Necrobinder, true) != 100
            || ComponentAssemblyGenerator.AdjustEtherealTagNumerator(100,
                GeneratedCharacter.Defect, false) != 100)
            throw new InvalidOperationException("基础/先古卡负面组件或负面关键词权重没有受到独立抑制。 ");
        AssertInvalid(new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Uncommon,
            "每当你花费2颗蓝星时，每当你抽到这张牌时，耗能减少1。", Array.Empty<CardTag>(),
            [
                new GeneratorOperation("A:whenOneStarSpent", OperationScope.AbilityTrigger,
                    "每当你花费2颗蓝星时。", new Dictionary<string, int>()),
                new GeneratorOperation("R:WheneverDrawn", OperationScope.Modifier,
                    "每当你抽到这张牌时，", new Dictionary<string, int> { ["triggerIndex"] = 0 }),
                new GeneratorOperation("R:CostDownWhenDrawn", OperationScope.Independent,
                    "本场战斗此牌耗能减少1。", new Dictionary<string, int> { ["triggerIndex"] = 0 })
            ], Character: GeneratedCharacter.Regent));
        var summonXRecipe = CharacterComponentCatalogs.Get(GeneratedCharacter.Necrobinder).Recipes
            .Single(recipe => recipe.Id == "Dirge");
        var summonXAtom = summonXRecipe.Atoms.Single(atom => atom.Template == "NCR:SummonX");
        var summonXOperation = new GeneratorOperation(summonXAtom.Template, summonXAtom.Scope,
            summonXAtom.ChineseText, new Dictionary<string, int>());
        if (summonXAtom.ChineseText != "召唤X。"
            || EnglishCardDescriptionRenderer.OperationText(summonXOperation) != "Summon X.")
            throw new InvalidOperationException("X召唤的数值后不应附加次数量词。 ");
        var orbitRecipe = regentCatalog.Recipes.Single(recipe => recipe.Id == "Orbit");
        if (orbitRecipe.Atoms.Count != 2 || orbitRecipe.Atoms[0].Template != "A:whenEnergySpent"
            || orbitRecipe.TriggerOwners.Count != 2 || orbitRecipe.TriggerOwners[1] != 0)
            throw new InvalidOperationException("环绕轨道必须拆成能量花费触发条件与独立收益组件。");
        var orbitOperations = orbitRecipe.Atoms.Select((atom, index) => new GeneratorOperation(
            atom.Template, atom.Scope, atom.ChineseText,
            orbitRecipe.TriggerOwners[index] >= 0
                ? new Dictionary<string, int> { ["triggerIndex"] = orbitRecipe.TriggerOwners[index] }
                : new Dictionary<string, int>())).ToArray();
        var orbitCard = new GeneratedCard(2, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Uncommon,
            CardDescriptionRenderer.Render(orbitOperations), Array.Empty<CardTag>(), orbitOperations,
            EnglishDescription: EnglishCardDescriptionRenderer.Render(orbitOperations), Character: GeneratedCharacter.Regent);
        var orbitUpgrades = Enumerable.Range(0, 200).Select(seed => CardUpgradeGenerator.Generate(orbitCard, new Random(seed))).ToArray();
        if (!orbitUpgrades.Any(upgrade => upgrade.Effects.Any(effect => effect.Kind == CardUpgradeKind.ReduceThreshold))
            || orbitUpgrades.Any(upgrade => upgrade.Effects.Any(effect => effect.Kind == CardUpgradeKind.ReduceThreshold && effect.Delta != -1))
            || orbitUpgrades.Where(upgrade => upgrade.Effects.Any(effect => effect.Kind == CardUpgradeKind.ReduceThreshold))
                .Any(upgrade => !upgrade.UpgradedChineseDescription.Contains("花费3点能量", StringComparison.Ordinal)))
            throw new InvalidOperationException("普通能量返还门槛升级必须把4降低为3。");

        var starTriggerAtom = regentCatalog.Recipes.Single(recipe => recipe.Id == "ChildOfTheStars").Atoms[0];
        var starTriggerText = starTriggerAtom.ChineseText.Replace("1颗蓝星", "3颗蓝星", StringComparison.Ordinal);
        ExternalOperationTextRegistry.RegisterNumericVariant(starTriggerAtom.Template, starTriggerAtom.ChineseText, starTriggerText);
        var starTriggerOperations = new GeneratorOperation[]
        {
            new("A:whenOneStarSpent", OperationScope.AbilityTrigger, starTriggerText, new Dictionary<string, int>()),
            new("R:GainEnergy", OperationScope.NonTargeted, "获得1点能量。", new Dictionary<string, int> { ["triggerIndex"] = 0 })
        };
        var starTriggerCard = new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Uncommon,
            CardDescriptionRenderer.Render(starTriggerOperations), Array.Empty<CardTag>(), starTriggerOperations,
            EnglishDescription: EnglishCardDescriptionRenderer.Render(starTriggerOperations), Character: GeneratedCharacter.Regent);
        var starTriggerUpgrades = Enumerable.Range(0, 200).Select(seed => CardUpgradeGenerator.Generate(starTriggerCard, new Random(seed))).ToArray();
        if (!starTriggerUpgrades.Any(upgrade => upgrade.Effects.Any(effect => effect.Kind == CardUpgradeKind.ReduceThreshold))
            || starTriggerUpgrades.Where(upgrade => upgrade.Effects.Any(effect => effect.Kind == CardUpgradeKind.ReduceThreshold))
                .Any(upgrade => !upgrade.UpgradedChineseDescription.Contains("花费2颗蓝星", StringComparison.Ordinal)))
            throw new InvalidOperationException("蓝星返还门槛升级必须把3降低为2。");

        var fixedStarOperation = new GeneratorOperation("N:B", OperationScope.NonTargeted,
            "获得12点格挡。", new Dictionary<string, int>());
        var fixedStarCard = new GeneratedCard(0, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Uncommon,
            fixedStarOperation.ChineseText, Array.Empty<CardTag>(), [fixedStarOperation],
            EnglishDescription: "Gain 12 Block.", Character: GeneratedCharacter.Regent, StarCost: 3);
        var fixedStarUpgrades = Enumerable.Range(0, 200).Select(seed => CardUpgradeGenerator.Generate(fixedStarCard, new Random(seed))).ToArray();
        if (!fixedStarUpgrades.Any(upgrade => upgrade.UpgradedStarCost == 2
                && upgrade.Effects.Any(effect => effect.Kind == CardUpgradeKind.ReduceStarCost))
            || fixedStarUpgrades.Any(upgrade => upgrade.UpgradedStarCost is { } starCost && starCost != 2))
            throw new InvalidOperationException("固定蓝星费用牌必须具有将蓝星费用减少1的升级路径。");

        // Equal seeds must produce equal templates for snapshotting, bug reproduction, and tests.
        var first = System.Text.Json.JsonSerializer.Serialize(new RandomCardGenerator(7).Generate());
        var second = System.Text.Json.JsonSerializer.Serialize(new RandomCardGenerator(7).Generate());
        if (first != second)
            throw new InvalidOperationException("相同 seed 的生成结果不稳定。");

        var upgradeJsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        var structuredUpgradeJson = System.Text.Json.JsonSerializer.Serialize(
            new CardUpgradeEffect(CardUpgradeKind.IncreaseNumber, 2, 3, "damage"), upgradeJsonOptions);
        if (structuredUpgradeJson.Contains("Description", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("新升级效果仍在持久化开发者调试文本。");
        var legacyUpgrade = System.Text.Json.JsonSerializer.Deserialize<CardUpgradeEffect>(
            "{\"kind\":0,\"chineseDescription\":\"旧升级标签\",\"englishDescription\":\"legacy label\","
            + "\"operationIndex\":2,\"delta\":3}", upgradeJsonOptions);
        if (legacyUpgrade is not { Kind: CardUpgradeKind.IncreaseNumber, OperationIndex: 2, Delta: 3 }
            || legacyUpgrade.LegacyChineseDescription != "旧升级标签"
            || legacyUpgrade.LegacyEnglishDescription != "legacy label")
            throw new InvalidOperationException("旧快照升级调试文本兼容读取失败。");

        foreach (var tag in Enum.GetValues<CardTag>())
        {
            if (GeneratedCardTagPolicy.IsNativeKeyword(tag) == GeneratedCardTagPolicy.IsSemanticTag(tag))
                throw new InvalidOperationException($"卡牌标签没有被唯一分类：{tag}。");
        }
        if (GeneratedCardTagPolicy.AddedBy(CardUpgradeKind.GrantInnate) != CardTag.Innate
            || GeneratedCardTagPolicy.AddedBy(CardUpgradeKind.GrantRetain) != CardTag.Retain
            || GeneratedCardTagPolicy.RemovedBy(CardUpgradeKind.RemoveExhaust) != CardTag.Exhaust
            || GeneratedCardTagPolicy.RemovedBy(CardUpgradeKind.RemoveEthereal) != CardTag.Ethereal)
            throw new InvalidOperationException("结构化关键词升级映射不完整。");
        try
        {
            new ComponentKeywordPolicy(AllowedBaseKeywords: new HashSet<CardTag> { CardTag.Strike }).Validate();
            throw new InvalidOperationException("关键词策略错误地接受了机制标签。");
        }
        catch (ArgumentException)
        {
            // Expected: Strike is a semantic tag, not a native card keyword.
        }
        var exhaustOnlyKeywordPolicy = new ComponentKeywordPolicy(
            AllowedBaseKeywords: new HashSet<CardTag> { CardTag.Exhaust });
        if (!exhaustOnlyKeywordPolicy.AllowsBase(CardTag.Strike)
            || !exhaustOnlyKeywordPolicy.AllowsBase(CardTag.Exhaust)
            || exhaustOnlyKeywordPolicy.AllowsBase(CardTag.Innate))
            throw new InvalidOperationException("关键词许可错误地改变了机制标签可达性。");

        var quasarAtom = CharacterComponentCatalogs.Get(GeneratedCharacter.Regent).Recipes
            .Single(recipe => recipe.Id == "Quasar").Atoms.Single();
        var quasarOperation = new GeneratorOperation(quasarAtom.Template, quasarAtom.Scope,
            quasarAtom.ChineseText, new Dictionary<string, int>(), RuntimeSpec: quasarAtom.RuntimeSpec,
            LocalizedText: quasarAtom.LocalizedText);
        if (System.Text.Json.JsonSerializer.Serialize(quasarOperation)
            .Contains("localizedText", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("运行时本地化模板被重复写入卡池快照。");
        if (!OperationRuntimeSpecCompiler.TryReplaceFixedValue(quasarOperation, "choices", 4,
                out var upgradedQuasar)
            || upgradedQuasar.ChineseText != "从4张随机无色牌中选择1张加入你的手牌。"
            || EnglishCardDescriptionRenderer.OperationText(upgradedQuasar)
                != "Choose 1 of 4 random Colorless cards to add into your Hand.")
            throw new InvalidOperationException("具名本地化槽没有保持中英文不同的数值顺序。");

        var perturbedLocalization = new OperationLocalizedText(
            "测试：候选[[choices]]，选择[[picks]]。", "Test: [[choices]] candidates, pick [[picks]].");
        perturbedLocalization.Validate(OperationRuntimeSpecCompiler.GetOrCompile(quasarOperation));
        var perturbedQuasar = quasarOperation with
        {
            ChineseText = perturbedLocalization.RenderChinese(OperationRuntimeSpecCompiler.GetOrCompile(quasarOperation)),
            LocalizedText = perturbedLocalization
        };
        if (OperationRuntimeSpecCompiler.GetOrCompile(perturbedQuasar).StableSignature()
                != OperationRuntimeSpecCompiler.GetOrCompile(quasarOperation).StableSignature()
            || EffectBalanceModel.EstimatedPositiveCardValue([perturbedQuasar], false,
                    GeneratedCardType.Skill, [])
                != EffectBalanceModel.EstimatedPositiveCardValue([quasarOperation], false,
                    GeneratedCardType.Skill, []))
            throw new InvalidOperationException("修改本地化模板意外改变了结构语义或估值。");

        var energyLocalizationSpec = new OperationRuntimeSpec(OperationRuntimeSpec.CurrentSchemaVersion,
            "gain_resource", "energy", "self", "none", "none", "any", [],
            [new RuntimeValueSlot("amount", 2)]);
        if (!OperationLocalizedText.TryCompile("获得2点能量。", "Gain 2 Energy.", energyLocalizationSpec,
                out var energyLocalization)
            || energyLocalization is null
            || energyLocalization.RenderChinese(energyLocalizationSpec) != "获得2点能量。"
            || energyLocalization.RenderEnglish(energyLocalizationSpec) != "Gain 2 Energy."
            || !energyLocalization.TryReplaceRenderedSlot("获得2点能量。", energyLocalizationSpec, "amount",
                "{Energy0:energyIcons()}", chinese: true, out var dynamicEnergy)
            || dynamicEnergy != "获得{Energy0:energyIcons()}。")
            throw new InvalidOperationException("具名资源槽没有稳定渲染能量文本或动态图标。");

        var entityLocalization = new OperationLocalizedText(
            "生成[[amount]]个[[orb_output]]。", "Channel [[amount]] [[orb_output]].",
            [new OperationTextSlot("orb_output", "闪电充能球", "Lightning Orbs")]);
        entityLocalization.Validate(energyLocalizationSpec);
        var reboundEntityLocalization = entityLocalization.WithTextSlotValue(
            "orb_output", "冰霜充能球", "Frost Orbs");
        if (entityLocalization.RenderChinese(energyLocalizationSpec) != "生成2个闪电充能球。"
            || reboundEntityLocalization.RenderChinese(energyLocalizationSpec) != "生成2个冰霜充能球。"
            || reboundEntityLocalization.RenderEnglish(energyLocalizationSpec) != "Channel 2 Frost Orbs.")
            throw new InvalidOperationException("具名实体槽没有与数值槽独立渲染或重绑定。");
    }

    private static void ExpectInvalidProfile(ComponentGenerationProfile profile, string expectedMessage)
    {
        try
        {
            ComponentProfileValidator.Validate(profile);
        }
        catch (InvalidDataException exception) when (exception.Message.Contains(expectedMessage,
                   StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException(
            $"Expected profile validation failure containing '{expectedMessage}'.");
    }

    private static void AssertInvalid(GeneratedCard card)
    {
        try
        {
            CardTemplateValidator.Validate(card);
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException("预期非法的 card template 被错误接受。");
    }

    private static string ComparableUltimateCard(GeneratedCard card) =>
        System.Text.Json.JsonSerializer.Serialize(card with
        {
            Character = GeneratedCharacter.Ironclad,
            Name = null
        });
}

internal sealed class SelfTestComponentValuation : IComponentValuation
{
    public int Estimate(ComponentValuationContext context) => 777;
}
#endif
