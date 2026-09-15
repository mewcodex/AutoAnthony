namespace ChaosCardGenerator;

/// <summary>
/// Classifies the legacy CardTag wire enum without changing its serialized numeric values. Native keywords affect
/// CardModel.Keywords; semantic tags participate in mechanics such as Perfected Strike and never render as a
/// keyword. New APIs should validate against this boundary rather than treating every CardTag as interchangeable.
/// </summary>
public static class GeneratedCardTagPolicy
{
    public static bool IsNativeKeyword(CardTag tag) => tag is CardTag.Exhaust or CardTag.Innate
        or CardTag.Retain or CardTag.Sly or CardTag.Ethereal or CardTag.Eternal or CardTag.Unplayable;

    public static bool IsSemanticTag(CardTag tag) => tag is CardTag.Strike or CardTag.Defend or CardTag.OstyAttack;

    /// <summary>
    /// Osty Attack is a semantic mechanic tag, not a randomly rolled keyword. Every native card whose direct
    /// card effect makes Osty deal damage carries CardTag.OstyAttack, including Protector/Unleash whose final
    /// damage is further modified by Osty's HP. Keep the rule tied to the dealer operation rather than to the
    /// localized description or to an HP modifier that may be attached to an ordinary player attack.
    /// </summary>
    public static bool IsOstyAttackCard(IReadOnlyList<GeneratorOperation> operations) =>
        operations.Any(operation => operation.Template is
            "NCR:OstyDamage" or "NCR:OstyAllDamage" or "T:ProxyDamage_Atomic_Poke");

    /// <summary>
    /// Rebuilds operation-derived semantic tags for newly assembled definitions. This prevents an unrelated card
    /// from randomly receiving OstyAttack and guarantees that every generated Osty damage card participates in
    /// native mechanics such as Squeeze. Runtime also derives this tag so old snapshots remain compatible.
    /// </summary>
    public static IReadOnlyList<CardTag> NormalizeOperationDerivedTags(IEnumerable<CardTag> tags,
        IReadOnlyList<GeneratorOperation> operations)
    {
        // Preserve an explicitly supplied semantic tag for an external component package. Built-in random
        // assembly never samples OstyAttack independently, so any newly generated built-in occurrence comes from
        // the operation rule below.
        var normalized = tags.Distinct().ToList();
        if (IsOstyAttackCard(operations) && !normalized.Contains(CardTag.OstyAttack))
            normalized.Add(CardTag.OstyAttack);
        return normalized;
    }

    public static CardTag? AddedBy(CardUpgradeKind kind) => kind switch
    {
        CardUpgradeKind.GrantInnate => CardTag.Innate,
        CardUpgradeKind.GrantRetain => CardTag.Retain,
        _ => null
    };

    public static CardTag? RemovedBy(CardUpgradeKind kind) => kind switch
    {
        CardUpgradeKind.RemoveExhaust => CardTag.Exhaust,
        CardUpgradeKind.RemoveEthereal => CardTag.Ethereal,
        _ => null
    };

    public static void ValidateKeywordSet(IEnumerable<CardTag>? tags, string field)
    {
        var invalid = tags?.Where(tag => !IsNativeKeyword(tag)).Select(tag => (CardTag?)tag).FirstOrDefault();
        if (invalid is null) return;
        throw new ArgumentException($"{field} contains semantic tag {invalid}; only native keywords are valid.",
            field);
    }

    /// <summary>
    /// Returns the effective native keyword additions represented by either snapshot layout. Schema 1-9 stored
    /// the projected arrays, while API v3 treats the structural upgrade effects as authoritative. Taking the union
    /// keeps both layouts readable without making runtime code understand two persistence formats.
    /// </summary>
    public static IReadOnlySet<CardTag> AddedKeywords(CardUpgradePlan? upgrade)
    {
        if (upgrade is null) return new HashSet<CardTag>();
        return upgrade.AddedKeywords
            .Concat(upgrade.Effects.Select(effect => AddedBy(effect.Kind)).OfType<CardTag>())
            .ToHashSet();
    }

    public static IReadOnlySet<CardTag> RemovedKeywords(CardUpgradePlan? upgrade)
    {
        if (upgrade is null) return new HashSet<CardTag>();
        return (upgrade.RemovedKeywords ?? [])
            .Concat(upgrade.Effects.Select(effect => RemovedBy(effect.Kind)).OfType<CardTag>())
            .ToHashSet();
    }

    public static IReadOnlySet<string> AddedCustomKeywords(CardUpgradePlan? upgrade)
    {
        if (upgrade is null) return new HashSet<string>(StringComparer.Ordinal);
        return (upgrade.AddedCustomKeywords ?? [])
            .Concat(upgrade.Effects.Where(effect => effect.Kind == CardUpgradeKind.AddCustomKeyword)
                .Select(effect => effect.KeywordId).OfType<string>())
            .ToHashSet(StringComparer.Ordinal);
    }

    public static IReadOnlySet<string> RemovedCustomKeywords(CardUpgradePlan? upgrade)
    {
        if (upgrade is null) return new HashSet<string>(StringComparer.Ordinal);
        return (upgrade.RemovedCustomKeywords ?? [])
            .Concat(upgrade.Effects.Where(effect => effect.Kind == CardUpgradeKind.RemoveCustomKeyword)
                .Select(effect => effect.KeywordId).OfType<string>())
            .ToHashSet(StringComparer.Ordinal);
    }
}
