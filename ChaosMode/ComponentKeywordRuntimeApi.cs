using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.HoverTips;
using GameCardTag = MegaCrit.Sts2.Core.Entities.Cards.CardTag;

namespace AutoAnthony;

/// <summary>
/// Game-side projection for one generator keyword ID. A custom keyword normally also owns an operation component:
/// that operation supplies execution and valuation, while this adapter supplies CardModel keywords/tags, upgrade
/// side effects, and hover tips that cannot exist in the pure generator assembly.
/// </summary>
public interface IComponentKeywordRuntimeAdapter
{
    IEnumerable<CardKeyword> CanonicalKeywords(ChaosCardModel card) => [];
    IEnumerable<GameCardTag> SemanticTags(ChaosCardModel card) => [];
    IEnumerable<IHoverTip> BuildHoverTips(ChaosCardModel card) => [];
    void OnUpgrade(ChaosCardModel card, bool added) { }
}

public sealed record ComponentKeywordRuntimeRegistration(
    string KeywordId,
    IComponentKeywordRuntimeAdapter Adapter);

/// <summary>Runtime half of ComponentKeywordApi. Registration freezes on first card query.</summary>
public static class ComponentKeywordRuntimeApi
{
    public const int ApiVersion = 1;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, IComponentKeywordRuntimeAdapter> Adapters =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> Packages = new(StringComparer.Ordinal);
    private static bool _frozen;

    public static bool RegistrationsFrozen
    {
        get { lock (Sync) return _frozen; }
    }

    public static IReadOnlyList<string> RegisteredKeywordIds
    {
        get { lock (Sync) return Adapters.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray(); }
    }

    public static IReadOnlyList<string> RegisteredPackages
    {
        get { lock (Sync) return Packages.OrderBy(value => value, StringComparer.Ordinal).ToArray(); }
    }

    public static void RegisterPackage(string packageId,
        IEnumerable<ComponentKeywordRuntimeRegistration> registrations)
    {
        ValidateId(packageId, nameof(packageId));
        ArgumentNullException.ThrowIfNull(registrations);
        var values = registrations.ToArray();
        if (values.Length == 0)
            throw new ArgumentException("A keyword runtime package must contain at least one adapter.",
                nameof(registrations));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in values)
        {
            ArgumentNullException.ThrowIfNull(registration);
            ValidateId(registration.KeywordId, nameof(registration.KeywordId));
            ArgumentNullException.ThrowIfNull(registration.Adapter);
            if (!ComponentKeywordApi.IsRegistered(registration.KeywordId))
                throw new InvalidOperationException(
                    $"Generator keyword '{registration.KeywordId}' must be registered before its runtime adapter.");
            if (!ids.Add(registration.KeywordId))
                throw new ArgumentException($"Keyword runtime package repeats '{registration.KeywordId}'.",
                    nameof(registrations));
        }
        lock (Sync)
        {
            if (_frozen)
                throw new InvalidOperationException(
                    "Keyword runtime registration must finish before generated cards query keywords.");
            if (Packages.Contains(packageId))
                throw new InvalidOperationException($"Keyword runtime package '{packageId}' is already registered.");
            var conflict = values.FirstOrDefault(value => Adapters.ContainsKey(value.KeywordId));
            if (conflict is not null)
                throw new InvalidOperationException(
                    $"A runtime adapter is already registered for keyword '{conflict.KeywordId}'.");
            Packages.Add(packageId);
            foreach (var value in values) Adapters.Add(value.KeywordId, value.Adapter);
        }
    }

    internal static IEnumerable<CardKeyword> CanonicalKeywords(ChaosCardModel card) =>
        ActiveAdapters(card).SelectMany(item => item.Adapter.CanonicalKeywords(card) ?? []);

    internal static IEnumerable<GameCardTag> SemanticTags(ChaosCardModel card) =>
        ActiveAdapters(card).SelectMany(item => item.Adapter.SemanticTags(card) ?? []);

    internal static IEnumerable<IHoverTip> HoverTips(ChaosCardModel card) =>
        ActiveAdapters(card).SelectMany(item => item.Adapter.BuildHoverTips(card) ?? []);

    internal static void ApplyUpgrade(ChaosCardModel card, string keywordId, bool added)
    {
        IComponentKeywordRuntimeAdapter? adapter;
        lock (Sync)
        {
            _frozen = true;
            Adapters.TryGetValue(keywordId, out adapter);
        }
        adapter?.OnUpgrade(card, added);
    }

    private static IReadOnlyList<(string Id, IComponentKeywordRuntimeAdapter Adapter)> ActiveAdapters(
        ChaosCardModel card)
    {
        var ids = new HashSet<string>(card.Generated.CustomKeywords ?? [], StringComparer.Ordinal);
        if (card.IsUpgraded && card.Generated.Upgrade is { } upgrade)
        {
            ids.UnionWith(GeneratedCardTagPolicy.AddedCustomKeywords(upgrade));
            ids.ExceptWith(GeneratedCardTagPolicy.RemovedCustomKeywords(upgrade));
        }
        lock (Sync)
        {
            _frozen = true;
            return ids.OrderBy(value => value, StringComparer.Ordinal)
                .Where(Adapters.ContainsKey)
                .Select(id => (id, Adapters[id])).ToArray();
        }
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => character > 0x7f))
            throw new ArgumentException("Keyword runtime IDs must be non-empty ASCII strings.", name);
    }
}
