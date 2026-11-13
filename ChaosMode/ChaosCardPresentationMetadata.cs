using System.Runtime.CompilerServices;
using ChaosCardGenerator;
using GameCardTag = MegaCrit.Sts2.Core.Entities.Cards.CardTag;

namespace AutoAnthony;

/// <summary>
/// Only immutable, definition-owned presentation facts. A new run, pool rework or edited definition has a new
/// weak key. Live counters, enchantments, keyword adapters and numerical combat previews are never cached here.
/// </summary>
internal sealed class ChaosCardPresentationMetadata
{
    private static readonly ConditionalWeakTable<GeneratedCard, ChaosCardPresentationMetadata> Entries = new();
    private readonly IReadOnlyList<GeneratorOperation> _operations;
    private IReadOnlySet<string>? _hoverTags;

    internal static ChaosCardPresentationMetadata For(GeneratedCard card) =>
        Entries.GetValue(card, static definition => new ChaosCardPresentationMetadata(definition));

    internal bool GainsBlock { get; }
    internal IReadOnlySet<string> HoverTags
    {
        get
        {
            var cached = Volatile.Read(ref _hoverTags);
            if (cached is not null) return cached;
            lock (this)
                return _hoverTags ??= _operations.SelectMany(CardEffectRules.HoverSemanticTags)
                    .ToHashSet(StringComparer.Ordinal);
        }
    }
    internal IReadOnlyList<GameCardTag> BuiltInTags { get; }
    internal IReadOnlyList<GameCardTag> ExternalTags { get; }

    private ChaosCardPresentationMetadata(GeneratedCard card)
    {
        _operations = card.Operations;
        GainsBlock = card.Operations.Any(operation => operation.Template is "N:B" or "N_BLOCK"
            or "N:BlockEqualAllPoison" or "CL:GainBlockEqualDamage" or "CL:GainBlockEqualCurrent"
            or "CL:GainNextTurnBlockEqualCurrent");
        var isOstyAttack = GeneratedCardTagPolicy.IsOstyAttackCard(card.Operations);
        var builtIn = new List<GameCardTag>();
        var external = new List<GameCardTag>();
        foreach (var tag in card.Tags)
        {
            if (!ChaosCardTagAdapter.TrySemanticTag(tag, out var nativeTag)) continue;
            external.Add(nativeTag);
            if (nativeTag != GameCardTag.OstyAttack || isOstyAttack) builtIn.Add(nativeTag);
        }
        if (isOstyAttack)
        {
            if (!builtIn.Contains(GameCardTag.OstyAttack)) builtIn.Add(GameCardTag.OstyAttack);
            if (!external.Contains(GameCardTag.OstyAttack)) external.Add(GameCardTag.OstyAttack);
        }
        if (card.Operations.Any(FoolComponentRules.IsShootModifier))
        {
            if (!builtIn.Contains(GameCardTag.Shoot)) builtIn.Add(GameCardTag.Shoot);
            if (!external.Contains(GameCardTag.Shoot)) external.Add(GameCardTag.Shoot);
        }
        BuiltInTags = builtIn.AsReadOnly();
        ExternalTags = external.AsReadOnly();
    }
}
