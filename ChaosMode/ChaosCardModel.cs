using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using System.Runtime.CompilerServices;
using System.Reflection;
using HarmonyLib;

namespace AutoAnthony;

public abstract class ChaosCardModel : CardModel
{
    private static readonly FieldInfo CardDynamicVarsField = AccessTools.Field(typeof(CardModel), "_dynamicVars");
    private static readonly FieldInfo CardEnergyCostField = AccessTools.Field(typeof(CardModel), "_energyCost");
    private static readonly FieldInfo CardKeywordsField = AccessTools.Field(typeof(CardModel), "_keywords");
    private static readonly FieldInfo CardBaseStarCostField = AccessTools.Field(typeof(CardModel), "_baseStarCost");
    private static readonly FieldInfo CardStarCostSetField = AccessTools.Field(typeof(CardModel), "_starCostSet");
    private int _extraDamage;
    private int _extraBlock;
    private int _resolvedSpecialXValue;
    private int _resolvedEnergyXValue;
    private int _resolvedStarXValue;
    private PileType? _postPlayExhaustMovePile;
    private readonly Dictionary<int, int> _capturedExternalDamageBonuses = [];
    private string _tinkeredDefinitionPayload = string.Empty;
    private GeneratedCard? _tinkeredDefinition;
    private string _freeformDefinitionPayload = string.Empty;
    private GeneratedCard? _freeformDefinition;
    private string _freeformPortraitPath = string.Empty;
    private string _editorDefinitionPayload = string.Empty;
    private GeneratedCard? _editorDefinition;
    private string _editorPortraitPath = string.Empty;
    private string _editorPortraitSourceId = string.Empty;
    private string _editorPortraitVariantId = string.Empty;
    private string _editorPortraitVariantPath = string.Empty;

    protected abstract int Slot { get; }
    protected virtual GeneratedCharacter Character => GeneratedCharacter.Ironclad;
    protected virtual string? DefinitionProfileId => null;
    internal string RuntimeProfileId => DefinitionProfileId ?? string.Empty;
    protected virtual ChaosCardDefinition ResolveDefinition() => DefinitionProfileId is { Length: > 0 } profileId
        ? ExternalComponentCharacterApi.ForSlot(profileId, Slot)
        : ChaosRunDefinitions.ForSlot(Character, Slot);
    public ChaosCardDefinition Definition => ResolveDefinition();
    public GeneratedCard Generated => _editorDefinition ?? _freeformDefinition ?? _tinkeredDefinition ?? Definition.Card;
    /// <summary>
    /// Optional per-instance definition supplied by a card-editor companion. The base mod does not create these
    /// payloads, but retaining and executing one here keeps edited cards save-safe and multiplayer-serializable.
    /// </summary>
    [SavedProperty]
    public string TinkeredDefinitionPayload
    {
        get => _tinkeredDefinitionPayload;
        set
        {
            AssertMutable();
            _tinkeredDefinitionPayload = value ?? string.Empty;
            if (_tinkeredDefinitionPayload.Length == 0)
            {
                _tinkeredDefinition = null;
                return;
            }
            try
            {
                var decoded = CardTinkeringApi.DeserializeCard(_tinkeredDefinitionPayload);
                EnsureSameShell(Definition.Card, decoded);
                _tinkeredDefinition = decoded;
            }
            catch (Exception exception)
            {
                _tinkeredDefinitionPayload = string.Empty;
                _tinkeredDefinition = null;
                Log.Warn($"[AutoAnthony] Ignoring an invalid per-card tinkering payload on {Id}: {exception.Message}");
            }
        }
    }
    /// <summary>
    /// Optional full-shell definition created through <see cref="AutoAnthonyFreeformCardApi"/>. This is deliberately
    /// separate from ordinary tinkering: loading a normal edited card still verifies every shell-owned property.
    /// </summary>
    [SavedProperty]
    public string FreeformDefinitionPayload
    {
        get => _freeformDefinitionPayload;
        set
        {
            AssertMutable();
            _freeformDefinitionPayload = value ?? string.Empty;
            if (_freeformDefinitionPayload.Length == 0)
            {
                _freeformDefinition = null;
                return;
            }
            try
            {
                var decoded = OperationRuntimeSpecCompiler.Attach(
                    CardTinkeringApi.DeserializeCard(_freeformDefinitionPayload));
                EnsureFreeformCharacter(decoded);
                _freeformDefinition = decoded;
                _tinkeredDefinition = null;
                _tinkeredDefinitionPayload = string.Empty;
                ClearEditorDefinition();
            }
            catch (Exception exception)
            {
                _freeformDefinitionPayload = string.Empty;
                _freeformDefinition = null;
                Log.Warn($"[AutoAnthony] Ignoring an invalid freeform definition on {Id}: {exception.Message}");
            }
        }
    }
    public bool HasTinkeredDefinition => _tinkeredDefinition is not null || _freeformDefinition is not null
        || _editorDefinition is not null;
    public bool HasFreeformDefinition => _freeformDefinition is not null;
    public bool HasEditorIdentity => _editorDefinition is not null;
    public AutoAnthonyEditorIdentity? EditorIdentity => _editorDefinition?.Name is { } name
        ? new AutoAnthonyEditorIdentity(name, _editorPortraitPath,
            NullIfEmpty(_editorPortraitSourceId), NullIfEmpty(_editorPortraitVariantId),
            NullIfEmpty(_editorPortraitVariantPath))
        : null;
    [SavedProperty]
    public string FreeformPortraitPath
    {
        get => _freeformPortraitPath;
        set
        {
            AssertMutable();
            _freeformPortraitPath = value ?? string.Empty;
        }
    }
    /// <summary>
    /// A definition whose identity was explicitly changed through <see cref="AutoAnthonyEditorApi"/>. Keeping it
    /// separate prevents the ordinary tinkering boundary from silently acquiring permission to rename cards.
    /// </summary>
    [SavedProperty]
    public string EditorDefinitionPayload
    {
        get => _editorDefinitionPayload;
        set
        {
            AssertMutable();
            _editorDefinitionPayload = value ?? string.Empty;
            if (_editorDefinitionPayload.Length == 0)
            {
                _editorDefinition = null;
                return;
            }
            try
            {
                var decoded = OperationRuntimeSpecCompiler.Attach(
                    CardTinkeringApi.DeserializeCard(_editorDefinitionPayload));
                EnsureSameShell(Definition.Card, decoded, allowIdentityChange: true);
                _editorDefinition = decoded;
                _tinkeredDefinition = null;
                _tinkeredDefinitionPayload = string.Empty;
            }
            catch (Exception exception)
            {
                _editorDefinitionPayload = string.Empty;
                _editorDefinition = null;
                Log.Warn($"[AutoAnthony] Ignoring an invalid editor identity payload on {Id}: {exception.Message}");
            }
        }
    }
    [SavedProperty]
    public string EditorPortraitPath
    {
        get => _editorPortraitPath;
        set { AssertMutable(); _editorPortraitPath = value ?? string.Empty; }
    }
    [SavedProperty]
    public string EditorPortraitSourceId
    {
        get => _editorPortraitSourceId;
        set { AssertMutable(); _editorPortraitSourceId = value ?? string.Empty; }
    }
    [SavedProperty]
    public string EditorPortraitVariantId
    {
        get => _editorPortraitVariantId;
        set { AssertMutable(); _editorPortraitVariantId = value ?? string.Empty; }
    }
    [SavedProperty]
    public string EditorPortraitVariantPath
    {
        get => _editorPortraitVariantPath;
        set { AssertMutable(); _editorPortraitVariantPath = value ?? string.Empty; }
    }
    [SavedProperty] public int ExtraDamage { get => _extraDamage; set { AssertMutable(); _extraDamage = value; } }
    [SavedProperty] public int ExtraBlock { get => _extraBlock; set { AssertMutable(); _extraBlock = value; } }
    [SavedProperty] public int ResolvedSpecialXValue { get => _resolvedSpecialXValue; set { AssertMutable(); _resolvedSpecialXValue = value; } }
    internal int ResolvedEnergyXValue => _resolvedEnergyXValue;
    internal int ResolvedStarXValue => _resolvedStarXValue;

    protected ChaosCardModel() : base(0, CardType.Skill, CardRarity.Common, TargetType.Self) { }

    /// <summary>
    /// Saved properties are filled after the canonical slot has been cloned. The clone can therefore still carry
    /// DynamicVars, cost and keyword caches materialized for the slot's pool definition when a per-card definition
    /// payload is assigned. Rebuild only after every saved property has been restored so freeform, ordinary
    /// tinkering and editor-identity payloads all expose variables from their restored operation list.
    /// </summary>
    protected override void AfterDeserialized()
    {
        base.AfterDeserialized();
        RebuildCachedCardState();
    }

    /// <summary>
    /// Applies or clears an editor-owned definition without changing the shared generated pool slot. Printed cost,
    /// type, target, rarity, keywords, identity and shell-owned upgrades are immutable at this boundary.
    /// </summary>
    public void ApplyTinkeredDefinition(GeneratedCard? definition)
    {
        AssertMutable();
        if (DefinitionProfileId is { Length: > 0 } externalProfileId)
            AutoAnthonyEditorApi.RequireSupport(externalProfileId,
                ExternalEditorCapabilities.GeneratedCardEditing);
        if (_editorDefinition is not null)
        {
            definition ??= Definition.Card with { Name = _editorDefinition.Name };
            definition = OperationRuntimeSpecCompiler.Attach(definition);
            EnsureSameShell(_editorDefinition, definition);
            _editorDefinition = definition;
            _editorDefinitionPayload = CardTinkeringApi.SerializeCard(definition);
            RebuildCachedCardState();
            return;
        }
        if (_freeformDefinition is not null)
        {
            if (definition is null)
                definition = _freeformDefinition with { Operations = [] };
            definition = OperationRuntimeSpecCompiler.Attach(definition);
            EnsureSameShell(_freeformDefinition, definition);
            _freeformDefinition = definition;
            _freeformDefinitionPayload = CardTinkeringApi.SerializeCard(definition);
            RebuildCachedCardState();
            return;
        }
        if (definition is null)
        {
            _tinkeredDefinition = null;
            _tinkeredDefinitionPayload = string.Empty;
        }
        else
        {
            definition = OperationRuntimeSpecCompiler.Attach(definition);
            EnsureSameShell(Definition.Card, definition);
            _tinkeredDefinition = definition;
            _tinkeredDefinitionPayload = CardTinkeringApi.SerializeCard(definition);
        }
        RebuildCachedCardState();
    }

    /// <summary>
    /// Applies a complete editor-owned definition. Callers should validate it with CardTinkeringApi before making
    /// the card available to combat. Intermediate invalid definitions are accepted so a preview can be assembled.
    /// </summary>
    public void ApplyFreeformDefinition(GeneratedCard definition)
    {
        AssertMutable();
        ArgumentNullException.ThrowIfNull(definition);
        definition = OperationRuntimeSpecCompiler.Attach(definition);
        EnsureFreeformCharacter(definition);
        _freeformDefinition = definition;
        _freeformDefinitionPayload = CardTinkeringApi.SerializeCard(definition);
        _tinkeredDefinition = null;
        _tinkeredDefinitionPayload = string.Empty;
        ClearEditorDefinition();
        RebuildCachedCardState();
    }

    internal void ApplyEditorDefinitionInternal(GeneratedCard definition, AutoAnthonyEditorIdentity identity)
    {
        AssertMutable();
        if (_freeformDefinition is not null)
            throw new InvalidOperationException(
                "Editor identity rerolls are supported for generated pool cards, not freeform card hosts.");
        definition = OperationRuntimeSpecCompiler.Attach(definition);
        EnsureSameShell(Definition.Card, definition, allowIdentityChange: true);
        _editorDefinition = definition;
        _editorDefinitionPayload = CardTinkeringApi.SerializeCard(definition);
        _tinkeredDefinition = null;
        _tinkeredDefinitionPayload = string.Empty;
        _editorPortraitPath = identity.PortraitPath;
        _editorPortraitSourceId = identity.PortraitSourceId ?? string.Empty;
        _editorPortraitVariantId = identity.PortraitVariantId ?? string.Empty;
        _editorPortraitVariantPath = identity.PortraitVariantPath ?? string.Empty;
        RebuildCachedCardState();
    }

    /// <summary>
    /// Restores the exact definition captured by a trigger Power onto its detached execution proxy. Unlike ordinary
    /// per-card tinkering, a captured definition can legitimately have a different shell from the proxy's shared slot:
    /// freeform/native decompositions and editor identity changes retain their own cost, type, target and rarity. The
    /// Power payload was validated when the source card armed it, so only the runtime-host character boundary applies
    /// here. Treating this as ordinary tinkering makes the first delayed trigger throw before recursion limiting runs.
    /// </summary>
    internal void ApplyCapturedDefinition(GeneratedCard definition)
    {
        AssertMutable();
        definition = OperationRuntimeSpecCompiler.Attach(definition);
        EnsureFreeformCharacter(definition);
        _freeformDefinition = definition;
        _freeformDefinitionPayload = CardTinkeringApi.SerializeCard(definition);
        _tinkeredDefinition = null;
        _tinkeredDefinitionPayload = string.Empty;
        ClearEditorDefinition();
        RebuildCachedCardState();
    }

    private void EnsureFreeformCharacter(GeneratedCard candidate)
    {
        if (candidate.Character != Character)
            throw new ArgumentException("A freeform definition must use the runtime card host's character.",
                nameof(candidate));
    }

    private static void EnsureSameShell(GeneratedCard original, GeneratedCard candidate,
        bool allowIdentityChange = false)
    {
        var sameTags = original.Tags.SequenceEqual(candidate.Tags)
            && (original.CustomKeywords ?? []).SequenceEqual(candidate.CustomKeywords ?? [], StringComparer.Ordinal);
        var originalUpgrade = original.Upgrade;
        var candidateUpgrade = candidate.Upgrade;
        var sameShellUpgrade = originalUpgrade is null && candidateUpgrade is null
            || originalUpgrade is not null && candidateUpgrade is not null
            && originalUpgrade.UpgradedCost == candidateUpgrade.UpgradedCost
            && originalUpgrade.UpgradedStarCost == candidateUpgrade.UpgradedStarCost
            && originalUpgrade.AddedKeywords.SequenceEqual(candidateUpgrade.AddedKeywords)
            && (originalUpgrade.RemovedKeywords ?? []).SequenceEqual(candidateUpgrade.RemovedKeywords ?? [])
            && (originalUpgrade.AddedCustomKeywords ?? []).SequenceEqual(
                candidateUpgrade.AddedCustomKeywords ?? [], StringComparer.Ordinal)
            && (originalUpgrade.RemovedCustomKeywords ?? []).SequenceEqual(
                candidateUpgrade.RemovedCustomKeywords ?? [], StringComparer.Ordinal);
        var sameName = original.Name?.Chinese == candidate.Name?.Chinese
            && original.Name?.English == candidate.Name?.English;
        var sameSources = (original.Name?.SourceCardIds ?? []).SequenceEqual(
            candidate.Name?.SourceCardIds ?? [], StringComparer.Ordinal);
        if (original.Cost != candidate.Cost || original.StarCost != candidate.StarCost
            || original.HasStarCostX != candidate.HasStarCostX || original.Type != candidate.Type
            || original.Target != candidate.Target || original.Rarity != candidate.Rarity
            || original.Character != candidate.Character || original.UnifiedChaos != candidate.UnifiedChaos
            || !sameTags || (!allowIdentityChange && (!sameName || !sameSources))
            || !sameShellUpgrade)
            throw new ArgumentException("A tinkered definition may only replace effect components, not card-shell properties.",
                nameof(candidate));
    }

    private void ClearEditorDefinition()
    {
        _editorDefinition = null;
        _editorDefinitionPayload = string.Empty;
        _editorPortraitPath = string.Empty;
        _editorPortraitSourceId = string.Empty;
        _editorPortraitVariantId = string.Empty;
        _editorPortraitVariantPath = string.Empty;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private void RebuildCachedCardState()
    {
        CardDynamicVarsField.SetValue(this, null);
        CardEnergyCostField.SetValue(this, null);
        CardKeywordsField.SetValue(this, null);
        CardBaseStarCostField.SetValue(this, 0);
        CardStarCostSetField.SetValue(this, false);
        _ = DynamicVars;
        _ = EnergyCost;
        _ = Keywords;
        _ = BaseStarCost;
        if (!IsUpgraded) return;
        OnUpgrade();
        FinalizeUpgradeInternal();
    }

    protected override int CanonicalEnergyCost => Math.Max(0, Generated.Cost);
    protected override bool HasEnergyCostX => Generated.Cost < 0;
    public override int CanonicalStarCost => Generated.StarCost;
    public override bool HasStarCostX => Generated.HasStarCostX;
    // Generated cards live in dedicated pools that are swapped in only for chaos runs. CardModel's default Pool
    // lookup scans ModelDb.AllCardPools, whose vanilla title-screen cache intentionally does not include those
    // pools; relying on it therefore makes library filters fail (and can make rendering query an invalid pool).
    public override CardPoolModel Pool => Character switch
    {
        GeneratedCharacter.Ironclad => ModelDb.CardPool<ChaosIroncladCardPool>(),
        GeneratedCharacter.Silent => ModelDb.CardPool<ChaosSilentCardPool>(),
        GeneratedCharacter.Defect => ModelDb.CardPool<ChaosDefectCardPool>(),
        GeneratedCharacter.Necrobinder => ModelDb.CardPool<ChaosNecrobinderCardPool>(),
        GeneratedCharacter.Regent => ModelDb.CardPool<ChaosRegentCardPool>(),
        GeneratedCharacter.Colorless => ModelDb.CardPool<ColorlessCardPool>(),
        _ => throw new ArgumentOutOfRangeException(nameof(Character))
    };
    public override CardType Type => Generated.Type switch
    {
        GeneratedCardType.Attack => CardType.Attack,
        GeneratedCardType.Skill => CardType.Skill,
        GeneratedCardType.Power => CardType.Power,
        _ => CardType.Skill
    };
    public override CardRarity Rarity => Generated.Rarity switch
    {
        GeneratedRarity.Basic => CardRarity.Basic,
        GeneratedRarity.Common => CardRarity.Common,
        GeneratedRarity.Uncommon => CardRarity.Uncommon,
        GeneratedRarity.Rare => CardRarity.Rare,
        GeneratedRarity.Ancient => CardRarity.Ancient,
        _ => CardRarity.Common
    };
    public override TargetType TargetType => Generated.Target == ChaosCardGenerator.TargetMode.SingleEnemy
        ? TargetType.AnyEnemy
        : Generated.Type == GeneratedCardType.Attack ? TargetType.AllEnemies : TargetType.Self;
    protected override bool IsPlayable => base.IsPlayable
        && (!Generated.Operations.Any(operation => operation.Template == "C:playableIfDrawPileEmpty")
            || PileType.Draw.GetPile(Owner).Cards.Count == 0);
    // Match vanilla Osty cards: warn with a red card glow when playing the card now would lose an immediate
    // Osty-dependent clause. The shared operation rule accounts for cards that first Summon and then use Osty.
    protected override bool ShouldGlowRedInternal => Owner.IsOstyMissing
        && CardEffectRules.RequiresLivingOstyForCurrentPlay(Generated.Operations);

    public string DynamicTitle
    {
        get
        {
            var chinese = LocManager.Instance?.Language is "zhs" or "zht";
            var value = chinese ? Generated.Name?.Chinese : Generated.Name?.English;
            value ??= chinese ? $"混沌牌{Slot + 1}" : $"Chaos Card {Slot + 1}";
            return IsUpgraded ? value + "+" : value;
        }
    }

    // Definition.PortraitPath is the stable vanilla identity used for deterministic selection, duplicate
    // prevention and snapshots. Resolve through the associated original CardModel at display time so resource
    // replacement and PortraitPath-based card-art mods can affect generated cards without exposing inactive
    // textures that some mods keep packaged behind their own UI-level selection logic.
    internal ChaosCardDefinition EffectivePortraitDefinition => _editorDefinition is not null
                                                                  && _editorPortraitPath.Length > 0
        ? Definition with
        {
            Card = Generated,
            PortraitPath = _editorPortraitPath,
            PortraitSourceId = NullIfEmpty(_editorPortraitSourceId),
            PortraitVariantId = NullIfEmpty(_editorPortraitVariantId),
            PortraitVariantPath = NullIfEmpty(_editorPortraitVariantPath)
        }
        : HasFreeformDefinition && _freeformPortraitPath.Length > 0
            ? Definition with
            {
                Card = Generated,
                PortraitPath = _freeformPortraitPath,
                PortraitSourceId = null,
                PortraitVariantId = null,
                PortraitVariantPath = null
            }
            : Definition;
    internal string EffectiveDefinitionPayload => _editorDefinitionPayload.Length > 0
        ? _editorDefinitionPayload
        : _freeformDefinitionPayload.Length > 0 ? _freeformDefinitionPayload : _tinkeredDefinitionPayload;
    public override string PortraitPath => ChaosPortraitCompatibility.ResolvePath(EffectivePortraitDefinition);
    public override string BetaPortraitPath => PortraitPath;
    public override IEnumerable<string> AllPortraitPaths => [PortraitPath];
    public override bool GainsBlock => Generated.Operations.Any(operation => operation.Template is "N:B" or "N_BLOCK" or "N:BlockEqualAllPoison"
        or "CL:GainBlockEqualDamage" or "CL:GainBlockEqualCurrent" or "CL:GainNextTurnBlockEqualCurrent");
    protected override IEnumerable<IHoverTip> ExtraHoverTips => IHoverTip.RemoveDupes(BuildExtraHoverTips());

    private IEnumerable<IHoverTip> BuildExtraHoverTips()
    {
        var operations = Generated.Operations;
        var semanticTags = operations.SelectMany(CardEffectRules.HoverSemanticTags)
            .ToHashSet(StringComparer.Ordinal);
        bool HasTag(string value) => semanticTags.Contains(value);
        bool Template(string value) => operations.Any(operation => operation.Template == value);
        bool TemplateStarts(string value) => operations.Any(operation => operation.Template.StartsWith(value, StringComparison.Ordinal));
        bool HasAtomicReference(string flag) => operations
            .Where(operation => !DerivativeSlotCatalog.IsSlotOperation(operation.Template))
            .Any(operation => OperationRuntimeSpecCompiler.RequireStructured(operation).Flags.Contains(flag));

        // These words can be introduced by an operation without becoming a keyword on the generated card.
        if (HasTag("ethereal")) yield return HoverTipFactory.FromKeyword(CardKeyword.Ethereal);
        if (HasTag("exhaust")) yield return HoverTipFactory.FromKeyword(CardKeyword.Exhaust);
        if (HasTag("retain")) yield return HoverTipFactory.FromKeyword(CardKeyword.Retain);
        if (HasTag("sly")) yield return HoverTipFactory.FromKeyword(CardKeyword.Sly);

        // Standard named effects follow the same model-backed tips used by the base-game cards. Deliberately do
        // not expose the generated card's backing ChaosCompositePower (or an ApplyPower_* card-named ability):
        // an ability with the same name as its card is self-explanatory and should not add a redundant tip.
        if (HasTag("vulnerable")) yield return HoverTipFactory.FromPower<VulnerablePower>();
        if (HasTag("weak")) yield return HoverTipFactory.FromPower<WeakPower>();
        if (HasTag("poison")) yield return HoverTipFactory.FromPower<PoisonPower>();
        if (HasTag("strength")) yield return HoverTipFactory.FromPower<StrengthPower>();
        if (HasTag("dexterity")) yield return HoverTipFactory.FromPower<DexterityPower>();
        if (HasTag("thorns")) yield return HoverTipFactory.FromPower<ThornsPower>();
        if (operations.Any(operation => OperationRuntimeSpecCompiler.RequireStructured(operation).Flags
                .Contains("block_reference")))
            yield return HoverTipFactory.Static(StaticHoverTip.Block);
        if (HasTag("intangible")) yield return HoverTipFactory.FromPower<IntangiblePower>();
        if (HasTag("plating")) yield return HoverTipFactory.FromPower<PlatingPower>();
        if (HasTag("focus")) yield return HoverTipFactory.FromPower<FocusPower>();
        if (HasTag("doom")) yield return HoverTipFactory.FromPower<DoomPower>();
        if (HasTag("vigor")) yield return HoverTipFactory.FromPower<VigorPower>();

        if (operations.Any(operation => operation.Template is "N:E" or "N:NextTurnEnergy" or "D:GainEnergy" or "D:NextTurnEnergy"
                or "NCR:GainEnergy" or "NCR:NextTurnEnergy" or "R:GainEnergy"
                || OperationRuntimeSpecCompiler.RequireStructured(operation).Flags.Contains("cost_wording")))
            // ChaosCardModel.Pool resolves directly to the character's dedicated replacement pool. Do not borrow
            // an arbitrary component-source card here: some valid source cards are intentionally outside reward
            // pools, and EnergyIconHelper rejects those cards while building hover tips.
            yield return HoverTipFactory.ForEnergy(this);
        if (HasTag("fatal")) yield return HoverTipFactory.Static(StaticHoverTip.Fatal);
        if (HasTag("replay")) yield return HoverTipFactory.Static(StaticHoverTip.ReplayStatic);
        if (HasTag("transform")) yield return HoverTipFactory.Static(StaticHoverTip.Transform);
        if (HasTag("summon")) yield return HoverTipFactory.Static(StaticHoverTip.SummonStatic);
        if (HasTag("card_reward")) yield return HoverTipFactory.Static(StaticHoverTip.CardReward);

        if (HasTag("forge"))
            foreach (var tip in HoverTipFactory.FromForge()) yield return tip;

        if (HasTag("channel")) yield return HoverTipFactory.Static(StaticHoverTip.Channeling);
        if (HasTag("evoke"))
            yield return HoverTipFactory.Static(StaticHoverTip.Evoke);
        foreach (var tip in ChaosOrbResolver.HoverTips(operations)) yield return tip;
        if (Template("D:TriggerLightningPassivesAtTarget")) yield return HoverTipFactory.FromOrb<LightningOrb>();

        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (!DerivativeSlotCatalog.IsSlotOperation(operation.Template)) continue;
            var upgraded = IsUpgraded && Generated.Upgrade?.Effects.Any(effect =>
                effect.OperationIndex == index
                && (effect.Kind == CardUpgradeKind.UpgradeReferencedCards
                    || effect.Kind == CardUpgradeKind.UpgradeDerivative
                    && DerivativeSlotCatalog.SupportsUpgrade(operation.Template, operation.DerivativeId))) == true;
            foreach (var tip in ChaosDerivativeResolver.HoverTips(operation, upgraded)) yield return tip;
        }

        // Rules that alter a particular derivative remain indivisible and therefore retain their native tip.
        if (HasAtomicReference("shiv_reference"))
            yield return HoverTipFactory.FromCard<Shiv>(AtomicDerivativeIsUpgraded("shiv_reference"));
        if (HasAtomicReference("giant_rock_reference"))
            yield return HoverTipFactory.FromCard<GiantRock>(AtomicDerivativeIsUpgraded("giant_rock_reference"));
        if (HasAtomicReference("soul_reference")) yield return HoverTipFactory.FromCard<Soul>();
        if (HasAtomicReference("fuel_reference"))
            yield return ChaosRunDefinitions.AncientFuelActive
                ? HoverTipFactory.FromCard<AncientFuel>()
                : HoverTipFactory.FromCard<Fuel>();
        if (HasAtomicReference("debris_reference")) yield return HoverTipFactory.FromCard<Debris>();
        if (HasAtomicReference("sovereign_blade_reference") || TemplateStarts("R:KingsSword"))
            yield return HoverTipFactory.FromCard<SovereignBlade>();

        for (var index = 0; index < operations.Count; index++)
            foreach (var tip in ComponentPresentationApi.BuildHoverTips(this, index, operations[index]))
                yield return tip;
        foreach (var tip in ComponentKeywordRuntimeApi.HoverTips(this)) yield return tip;

        // Atomic proxy tips are projected above from RuntimeSpec flags and typed derivative/orb slots. Do not
        // enumerate the source card's HoverTips here: during a chaos run its vanilla reward pool may be replaced,
        // and the base-game EnergyHoverTip rejects a source model that is temporarily outside an active pool.
    }

    private bool AtomicDerivativeIsUpgraded(string referenceFlag) => IsUpgraded
        && Generated.Upgrade?.Effects.Any(effect => effect.Kind is CardUpgradeKind.UpgradeDerivative
                or CardUpgradeKind.UpgradeReferencedCards
            && effect.OperationIndex is { } index && (uint)index < (uint)Generated.Operations.Count
            && OperationRuntimeSpecCompiler.RequireStructured(Generated.Operations[index]).Flags
                .Contains(referenceFlag)) == true;

    protected override IEnumerable<DynamicVar> CanonicalVars
    {
        get
        {
            for (var index = 0; index < Generated.Operations.Count; index++)
            {
                var operation = Generated.Operations[index];
                if (ChaosStatPreview.TryGetKind(Generated.Operations, index, out var previewKind))
                    yield return new ChaosCalculatedPreviewVar(ChaosStatPreview.VariableName(previewKind, index),
                        index, previewKind);
                if (!ChaosOperationVariables.TryGetInitialValue(operation, out var value)) continue;
                var name = ChaosOperationVariables.Name(operation, index);
                if (operation.Template == "T:ProxyDamage_Atomic_Poke")
                {
                    // Poke is executed by Osty even though it retains its legacy proxy template. Its preview must
                    // therefore use Osty as the damage dealer so Calcify, Osty's Strength and Weak are reflected.
                    yield return new ChaosOstyDamageVar(name, value, ValueProp.Move);
                    continue;
                }
                if (operation.Template.StartsWith("T:ProxyDamage_", StringComparison.Ordinal)
                    || operation.Template.StartsWith("N:ProxyDamage_", StringComparison.Ordinal))
                {
                    yield return new ChaosDamageVar(name, value, ValueProp.Move);
                    continue;
                }
                if (operation.Template is "T:D" or "T:DX" or "T:D_EnergyX"
                    && operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                    && triggerIndex >= 0 && triggerIndex < index
                    && CardEffectRules.TriggerImplicitlyTargetsAttacker(Generated.Operations[triggerIndex]))
                {
                    // Retaliation uses the ordinary damage component, but—as in the native Flame Barrier—its
                    // event damage is not modified by the card owner's Strength. Match runtime and preview values.
                    yield return new ChaosDamageVar(name, value, ValueProp.Unpowered);
                    continue;
                }
                yield return operation.Template switch
                {
                    "T:D" or "T:DX" or "T:D_EnergyX" or "N:AllD" or "N:RandomD"
                        or "NCR:DoomScaledDamage" =>
                        new ChaosDamageVar(name, value, ValueProp.Move),
                    "NCR:OstyDamage" or "NCR:OstyAllDamage" =>
                        new ChaosOstyDamageVar(name, value, ValueProp.Move),
                    "NCR:UnpoweredDamage" => new ChaosDamageVar(name, value, ValueProp.Unpowered),
                    "CL:RollingAllDamage" => new ChaosDamageVar(name, value, ValueProp.Unpowered),
                    "N:RetaliateDamage" => new ChaosDamageVar(name, value, ValueProp.Unpowered),
                    "N:B" or "N_BLOCK" or "I:DrawAndBlockIfSkill" => new ChaosBlockVar(name, value, ValueProp.Move),
                    "N:E" or "N:NextTurnEnergy" or "D:GainEnergy" or "D:NextTurnEnergy" or "NCR:GainEnergy" or "NCR:NextTurnEnergy"
                        or "R:GainEnergy" => new EnergyVar(name, value),
                    "N:HP-" => new HpLossVar(name, value),
                    _ => new DynamicVar(name, value)
                };
            }
        }
    }

    public override IEnumerable<CardKeyword> CanonicalKeywords
    {
        get
        {
            foreach (var tag in Generated.Tags)
            {
                if (ChaosCardTagAdapter.TryKeyword(tag, out var keyword)) yield return keyword;
            }
            foreach (var keyword in ComponentKeywordRuntimeApi.CanonicalKeywords(this)) yield return keyword;
        }
    }

    // CardModel caches CanonicalTags after the first read. Chaos slot definitions change between runs, so a model
    // whose tags were queried on the title screen could otherwise retain the previous placeholder/run's tags and
    // fail Perfected Strike's PlayerCombatState.AllCards semantic-tag count. Resolve the generated definition on
    // every query instead, exactly as the native card counts CardTag.Strike rather than localized title text.
    public override IEnumerable<MegaCrit.Sts2.Core.Entities.Cards.CardTag> Tags
    {
        get
        {
            foreach (var tag in Generated.Tags)
                if (ChaosCardTagAdapter.TrySemanticTag(tag, out var gameTag)) yield return gameTag;
            foreach (var gameTag in ComponentKeywordRuntimeApi.SemanticTags(this)) yield return gameTag;
        }
    }

    protected override void AddExtraArgsToDescription(LocString description)
    {
        var chinese = LocManager.Instance?.Language is "zhs" or "zht";
        var rawTemplate = ChaosRuntimeDescriptionCache.GetFormatted(this, chinese);
        // SmartFormat does not recursively evaluate replacement strings. Put the generated template directly into
        // the active localization table so {EffectN:diff()} and energyIcons() are evaluated in the normal card pass.
        ChaosRuntimeDescriptionCache.InstallIfChanged(
            LocManager.Instance!.GetTable("cards"), Id.Entry + ".description", rawTemplate);
        description.Add("singleStarIcon", "[img]res://images/packed/sprite_fonts/star_icon.png[/img]");
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // ResourceInfo is the synchronized payment attached to this CardPlay. Prefer it over mutable card fields on
        // remote peers; LastStarsSpent/CapturedXValue can lag one action while a multiplayer play is reconstructed.
        // Auto-played/replayed X cards intentionally spend zero, so retain the card's captured value in that case.
        var resolvedEnergyX = EnergyCost.CostsX
            ? Hook.ModifyXValue(CombatState!, this,
                cardPlay.Resources.EnergySpent > 0 ? cardPlay.Resources.EnergySpent : EnergyCost.CapturedXValue)
            : 0;
        var resolvedStarX = HasStarCostX
            ? Hook.ModifyXValue(CombatState!, this,
                cardPlay.Resources.StarsSpent > 0 ? cardPlay.Resources.StarsSpent : LastStarsSpent)
            : 0;
        SetResolvedXValues(
            resolvedEnergyX,
            resolvedStarX);
        var special = Generated.Operations.FirstOrDefault(SpecialXCardConverter.IsSpecial);
        ResolvedSpecialXValue = special is null ? 0
            : SpecialXCardConverter.Resource(special) == SpecialXCardConverter.StarResource
                ? ResolvedStarXValue
                : ResolvedEnergyXValue;
        await ChaosOperationExecutor.Play(this, choiceContext, cardPlay);
    }

    internal void SetResolvedXValues(int energy, int stars)
    {
        _resolvedEnergyXValue = Math.Max(0, energy);
        _resolvedStarXValue = Math.Max(0, stars);
    }

    // Triggered effects execute through a newly-created proxy card. Reading that proxy's captured payment would
    // always return zero, so effects must reuse the values captured when the source X-cost card was actually played.
    internal int ResolveEffectEnergyXValue() => ChaosXValueMultiplier.Apply(Generated,
        EnergyCost.CostsX ? ResolvedEnergyXValue : ResolveEnergyXValue());
    internal int ResolveEffectStarXValue() => ChaosXValueMultiplier.Apply(Generated,
        HasStarCostX ? ResolvedStarXValue : ResolveStarXValue());
    internal int ResolveEffectSpecialXValue() => ChaosXValueMultiplier.Apply(Generated, ResolvedSpecialXValue);

    internal async Task FireSelfExhaustTriggers(PlayerChoiceContext choiceContext)
    {
        for (var index = 0; index < Generated.Operations.Count; index++)
        {
            var operation = Generated.Operations[index];
            // C:after is shared by two distinct card lifecycle events.  A turn-end trigger also targets this card,
            // but must not fire at the moment the card enters the Exhaust pile.
            if (!CardEffectRules.IsSelfExhaustEventTrigger(operation)) continue;
            if (ChaosDiagnostics.VerboseRuntime)
                ChaosRuntimeDiagnostics.TriggerFired("self-exhaust", Definition.Slot, operation);
            await ChaosOperationExecutor.ExecuteTriggered(this, index, choiceContext, eventCard: this);
        }
    }

    // Bombardment uses the Early auto-pre-play hook for this mechanic.  Besides running at the correct point in
    // the turn, this supplies the real choice context to arbitrary generated payoffs and prevents another
    // auto-pre-play effect from causing the exhausted card to trigger twice.
    public override async Task AfterAutoPrePlayPhaseEnteredEarly(PlayerChoiceContext choiceContext, Player player)
    {
        if (player != Owner || Pile?.Type != PileType.Exhaust) return;
        for (var index = 0; index < Generated.Operations.Count; index++)
        {
            if (!CardEffectRules.IsExhaustPileTurnStartTrigger(Generated.Operations[index])) continue;
            if (ChaosDiagnostics.VerboseRuntime)
                MegaCrit.Sts2.Core.Logging.Log.Info($"[AutoAnthony] Fired Exhaust-pile turn-start trigger for slot {Definition.Slot}.");
            await ChaosOperationExecutor.ExecuteTriggered(this, index, choiceContext, eventCard: this);
        }
    }

    public override async Task AfterAutoPostPlayPhaseEntered(PlayerChoiceContext choiceContext, MegaCrit.Sts2.Core.Entities.Players.Player player)
    {
        if (player != Owner) return;
        if (Pile?.Type == PileType.Exhaust)
        {
            for (var index = 0; index < Generated.Operations.Count; index++)
            {
                var operation = Generated.Operations[index];
                if (!CardEffectRules.IsExhaustPileTurnEndTrigger(operation)) continue;
                if (ChaosDiagnostics.VerboseRuntime)
                    MegaCrit.Sts2.Core.Logging.Log.Info($"[AutoAnthony] Fired Exhaust-pile turn-end trigger for slot {Definition.Slot}.");
                await ChaosOperationExecutor.ExecuteTriggered(this, index, choiceContext, eventCard: this);
            }
        }
        if (Generated.Operations.Any(operation => operation.Template == "R:PlayAtTurnEndIfTopOfDraw")
            && Pile?.Type == PileType.Draw && PileType.Draw.GetPile(Owner).Cards.FirstOrDefault() == this)
            await ChaosOperationExecutor.AutoPlayFromDrawPileSequentially(choiceContext, Owner, 1,
                CardPilePosition.Top, forceExhaust: false);
    }

    public override Task AfterCardDrawn(PlayerChoiceContext choiceContext, CardModel drawnCard, bool fromHandDraw)
    {
        if (drawnCard != this) return Task.CompletedTask;
        // Values on both draw-trigger variants are generated and upgradeable. Resolve every occurrence (the same
        // field may legally appear twice) from the effective text instead of silently hard-coding the native 1.
        for (var index = 0; index < Generated.Operations.Count; index++)
        {
            var operation = Generated.Operations[index];
            if (operation.Template == "R:CostDownWhenDrawn")
                EnergyCost.AddThisCombat(-Math.Max(1,
                    OperationAmount(index)));
            else if (operation.Template == "R:DamageUpWhenDrawn")
                ExtraDamage += Math.Max(0,
                    OperationAmount(index));
        }
        return Task.CompletedTask;
    }

    public override async Task BeforeHandDraw(Player player, PlayerChoiceContext choiceContext, ICombatState combatState)
    {
        if (player != Owner || !Generated.Operations.Any(operation => operation.Template == "CL:ReturnThisToHand")) return;
        if (!CombatManager.Instance.History.CardPlaysFinished.Any(entry =>
                entry.HappenedLastPlayerTurn(Owner) && entry.CardPlay.Card == this)) return;
        if (Pile?.Type != PileType.Hand)
            await ChaosOperationExecutor.TryAddToHand(this);
    }

    public override Task AfterCardEnteredCombat(CardModel card)
    {
        if (card != this || IsClone) return Task.CompletedTask;
        var voidCostReduction = TotalOperationAmount("NCR:CostDownPerVoidPlayed");
        if (voidCostReduction > 0)
        {
            var voids = CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
                entry.CardPlay.Player == Owner && entry.CardPlay.Card.Keywords.Contains(CardKeyword.Ethereal));
            EnergyCost.AddThisCombat(-voids * voidCostReduction);
        }
        if (Generated.Operations.Any(operation => operation.Template == "NCR:SetCostZeroIfOstyAttacked")
            && HasOstyAttackedThisTurn())
            EnergyCost.SetThisTurn(0);
        var countedType = CostReductionType();
        if (countedType is null) return Task.CompletedTask;
        var plays = CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
            entry.CardPlay.Card.Type == countedType && entry.CardPlay.Player == Owner && entry.HappenedThisTurn(CombatState));
        EnergyCost.AddThisTurn(-plays * CostReductionPerPlay(countedType.Value));
        return Task.CompletedTask;
    }

    public override Task AfterAttack(PlayerChoiceContext choiceContext, AttackCommand command)
    {
        if (Generated.Operations.Any(operation => operation.Template == "NCR:SetCostZeroIfOstyAttacked")
            && command.Attacker == Owner.Osty)
            EnergyCost.SetThisTurn(0);
        return Task.CompletedTask;
    }

    public override Task BeforeCardPlayed(CardPlay cardPlay)
    {
        var voidCostReduction = TotalOperationAmount("NCR:CostDownPerVoidPlayed");
        if (voidCostReduction > 0
            && cardPlay.Card.Owner == Owner && cardPlay.Card.Keywords.Contains(CardKeyword.Ethereal))
            EnergyCost.AddThisCombat(-voidCostReduction);
        var countedType = CostReductionType();
        if (countedType is not null && cardPlay.Card.Owner == Owner && cardPlay.Card.Type == countedType)
            EnergyCost.AddThisTurn(-CostReductionPerPlay(countedType.Value));
        return Task.CompletedTask;
    }

    public override Task AfterDeath(PlayerChoiceContext choiceContext,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature creature, bool wasRemovalPrevented, float deathAnimLength)
    {
        var deathCostReduction = TotalOperationAmount("NCR:CostDownWhenCreatureDies");
        if (!wasRemovalPrevented && Pile?.IsCombatPile == true && deathCostReduction > 0)
            EnergyCost.AddThisCombat(-deathCostReduction);
        return Task.CompletedTask;
    }

    public override async Task AfterCardPlayedLate(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        var operationIndex = Generated.Operations.ToList()
            .FindIndex(op => op.Template == "NCR:ReturnFromDiscardOnHighCostPlay");
        if (operationIndex >= 0 && cardPlay.Card.Owner == Owner)
        {
            var operation = Generated.Operations[operationIndex];
            var thresholdIndex = operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                                 && (uint)triggerIndex < (uint)operationIndex
                ? triggerIndex
                : -1;
            var threshold = thresholdIndex < 0 ? 2 : OperationAmount(thresholdIndex);
            if (cardPlay.Resources.EnergyValue >= threshold && Pile?.Type == PileType.Discard)
                await ChaosOperationExecutor.TryAddToHand(this);
        }
        var returnIndices = Generated.Operations.Select((operation, index) => (operation, index))
            .Where(item => item.operation.Template == "R:ReturnAfterSkillsPlayed")
            .Select(item => item.index).ToArray();
        if (returnIndices.Length > 0 && cardPlay.Card.Owner == Owner && cardPlay.Card.Type == CardType.Skill
            && Pile?.Type != PileType.Hand)
        {
            var skills = CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
                entry.HappenedThisTurn(CombatState) && entry.CardPlay.Card.Type == CardType.Skill
                && entry.CardPlay.Player == Owner);
            // A card may legally contain the same field twice. Evaluate each upgraded threshold rather than only
            // the first occurrence; otherwise upgrading the second visible “3 Skills” field to 2 would still run
            // the untouched first field at 3. OperationAmount reads the live upgraded DynamicVar/RuntimeSpec.
            // One Skill would turn the clause into a near-automatic self-return loop. Clamp at runtime as a final
            // compatibility boundary for already-instantiated cards from old saves; current snapshots are rejected
            // by CardTemplateValidator and selectively regenerated instead.
            if (skills > 0 && returnIndices.Any(index => skills % Math.Max(2, OperationAmount(index)) == 0))
                await ChaosOperationExecutor.TryAddToHand(this);
        }
    }

    public override async Task AfterCardExhausted(PlayerChoiceContext choiceContext, CardModel card,
        bool causedByEthereal)
    {
        if (card != this || causedByEthereal || _postPlayExhaustMovePile is not { } destination) return;
        _postPlayExhaustMovePile = null;
        if (Pile?.Type != PileType.Exhaust) return;
        await CardPileCmd.Add(this, destination,
            destination == PileType.Draw ? CardPilePosition.Top : CardPilePosition.Bottom);
    }

    public override Task AfterCardGeneratedForCombat(CardModel card, Player? creator)
    {
        var statusCostReduction = TotalOperationAmount("D:CostDownWhenStatusGenerated");
        if (statusCostReduction <= 0
            || creator != Owner || card.Owner != Owner || card.Type != CardType.Status)
            return Task.CompletedTask;
        EnergyCost.AddUntilPlayed(-statusCostReduction);
        return Task.CompletedTask;
    }

    private int TotalOperationAmount(string template) => Generated.Operations
        .Select((operation, index) => (operation, index))
        .Where(item => item.operation.Template == template)
        .Sum(item => Math.Max(1, OperationAmount(item.index)));

    private CardType? CostReductionType()
    {
        if (Generated.Operations.Any(operation => operation.Template == "C:whileInCombat")) return CardType.Attack;
        if (Generated.Operations.Any(operation => operation.Template == "C:whileInCombatSkillCostReduction")) return CardType.Skill;
        return null;
    }

    private int CostReductionPerPlay(CardType countedType)
    {
        var template = countedType == CardType.Skill
            ? "C:whileInCombatSkillCostReduction"
            : "C:whileInCombat";
        var index = Generated.Operations.ToList().FindIndex(operation => operation.Template == template);
        if (index < 0) return 1;
        return Math.Max(1, OperationAmount(index));
    }

    private bool HasOstyAttackedThisTurn()
    {
        var combatState = CombatState ?? Owner.Creature.CombatState;
        return combatState is not null && CombatManager.Instance.History.Entries.OfType<CreatureAttackedEntry>()
            .Any(entry => entry.Actor == Owner.Osty && entry.HappenedThisTurn(combatState));
    }

    protected override CardLocation GetResultLocationForCardPlay()
    {
        var location = base.GetResultLocationForCardPlay();
        _postPlayExhaustMovePile = null;
        if (location.pileType == PileType.Exhaust)
        {
            if (Generated.Operations.Any(operation => operation.Template == "R:ReturnThisToHand"))
                _postPlayExhaustMovePile = PileType.Hand;
            else if (Generated.Operations.Any(operation => operation.Template == "R:PutThisOnDraw"))
                _postPlayExhaustMovePile = PileType.Draw;
            // Preserve the real Exhaust event and all Exhaust synergies, then move this card from the Exhaust pile
            // in AfterCardExhausted. This makes the printed keyword and the post-play movement both functional.
            return location;
        }
        if (location.pileType != PileType.Discard) return location;
        if (Generated.Operations.Any(operation => operation.Template == "R:ReturnThisToHand"))
            location.pileType = PileType.Hand;
        else if (Generated.Operations.Any(operation => operation.Template == "R:PutThisOnDraw"))
        {
            location.pileType = PileType.Draw;
            location.position = CardPilePosition.Top;
        }
        return location;
    }

    protected override void OnUpgrade()
    {
        var upgrade = Generated.Upgrade;
        if (upgrade is null) return;
        if (!EnergyCost.CostsX && upgrade.UpgradedCost != Generated.Cost)
            EnergyCost.UpgradeBy(upgrade.UpgradedCost - Generated.Cost);
        var upgradedStarCost = upgrade.UpgradedStarCost ?? Generated.StarCost;
        if (!HasStarCostX && Generated.StarCost >= 0 && upgradedStarCost != Generated.StarCost)
            UpgradeStarCostBy(upgradedStarCost - Generated.StarCost);
        foreach (var keyword in GeneratedCardTagPolicy.AddedKeywords(upgrade))
        {
            if (ChaosCardTagAdapter.TryKeyword(keyword, out var gameKeyword)) AddKeyword(gameKeyword);
        }
        foreach (var keyword in GeneratedCardTagPolicy.RemovedKeywords(upgrade))
        {
            if (ChaosCardTagAdapter.TryKeyword(keyword, out var gameKeyword)) RemoveKeyword(gameKeyword);
        }
        foreach (var keywordId in GeneratedCardTagPolicy.AddedCustomKeywords(upgrade))
            ComponentKeywordRuntimeApi.ApplyUpgrade(this, keywordId, added: true);
        foreach (var keywordId in GeneratedCardTagPolicy.RemovedCustomKeywords(upgrade))
            ComponentKeywordRuntimeApi.ApplyUpgrade(this, keywordId, added: false);
        foreach (var effect in upgrade.Effects)
        {
            if (effect.OperationIndex is not { } index || effect.Delta is not { } delta) continue;
            if (effect.Kind == CardUpgradeKind.IncreaseNumber
                && CardEffectRules.IsNonUpgradeableNumericMarker(Generated.Operations[index])) continue;
            // X upgrades change the number of X repetitions, not the operation's ordinary numeric value.
            if (!ChaosOperationVariables.TryGetInitialSlot(Generated.Operations[index], out var dynamicSlot)
                || effect.ValueSlotId is { } upgradedSlot && upgradedSlot != dynamicSlot)
                continue;
            if (DynamicVars.TryGetValue(ChaosOperationVariables.Name(Generated.Operations[index], index), out var variable))
                variable.UpgradeValueBy(delta);
        }
    }

    internal int OperationAmount(int operationIndex)
    {
        var operation = Generated.Operations[operationIndex];
        if (DynamicVars.TryGetValue(ChaosOperationVariables.Name(operation, operationIndex), out var variable))
            return variable.IntValue;
        var spec = ChaosOperationExecutor.EffectiveRuntimeSpec(this, operationIndex);
        var slotId = OperationRuntimeSpecCompiler.UpgradeValueSlot(operation);
        var slot = slotId is null ? spec.Values.FirstOrDefault(value => value.Upgradable)
            : spec.Values.FirstOrDefault(value => value.Id == slotId);
        if (slot is null) return 0;
        return slot.Source switch
        {
            "special_x" => Math.Max(0, ResolveEffectSpecialXValue() + slot.Offset),
            "energy_x" => Math.Max(0, ResolveEffectEnergyXValue() + slot.Offset),
            "star_x" => Math.Max(0, ResolveEffectStarXValue() + slot.Offset),
            _ => Math.Max(0, slot.BaseValue + slot.Offset)
        };
    }

    internal static class ChaosXValueMultiplier
    {
        internal static int Apply(GeneratedCard card, int resolvedX)
        {
            resolvedX = Math.Max(0, resolvedX);
            var modifier = card.Operations.FirstOrDefault(operation =>
                operation.Template == "R:DoubleEitherXAtThreshold");
            if (modifier is null) return resolvedX;
            var threshold = Math.Max(1, OperationRuntimeSpecCompiler.StaticLiteralValue(
                modifier, "threshold", 4));
            return resolvedX >= threshold ? resolvedX * 2 : resolvedX;
        }
    }

    /// <summary>
    /// Persistent effects execute later through an unenchanted proxy card. Capture the source card's intrinsic
    /// (upgrade + enchantment + card-persistent growth) values now so the proxy represents the card that was
    /// actually played. Triggered damage and Block deliberately execute as unpowered Power effects, so a Power
    /// card must instead snapshot their final combat preview here; otherwise Strength/Dexterity changes the card
    /// panel but is lost as soon as the backing ChaosCompositePower is created. The compact array stores
    /// index/value pairs and is suitable for Power SavedProperties and multiplayer sync.
    /// </summary>
    internal int[] CaptureOperationValuesForPower()
    {
        var captured = new List<int>();
        for (var index = 0; index < Generated.Operations.Count; index++)
        {
            var operation = Generated.Operations[index];
            if (!ChaosOperationVariables.TryGetInitialValue(operation, out _)
                || !DynamicVars.TryGetValue(ChaosOperationVariables.Name(operation, index), out var variable))
                continue;
            var captureFinalPowerValue = Type == CardType.Power
                && UsesFinalCombatPreviewInPower(operation, variable);
            var externalDamageDependency = captureFinalPowerValue
                && variable is DamageVar
                && HasExternalDamageDependency(index);
            if (externalDamageDependency)
            {
                // Mind Blast/Gold Axe multiply their intrinsic coefficient by a live count, then Strength/Vigor
                // modifies the resulting attack once. Store the coefficient and the powered flat delta separately;
                // baking the delta into the coefficient would multiply Strength by the draw-pile/card-play count.
                variable.UpdateCardPreview(this, CardPreviewMode.None, null, runGlobalHooks: false);
                var intrinsic = (int)variable.EnchantedValue;
                variable.UpdateCardPreview(this, CardPreviewMode.None, null, runGlobalHooks: true);
                captured.Add(index);
                captured.Add(intrinsic);
                captured.Add(ExternalDamageBonusKey(index));
                captured.Add((int)variable.PreviewValue - intrinsic);
                continue;
            }
            variable.UpdateCardPreview(this, CardPreviewMode.None, null,
                runGlobalHooks: captureFinalPowerValue);
            captured.Add(index);
            captured.Add((int)(captureFinalPowerValue ? variable.PreviewValue : variable.EnchantedValue));
        }
        return captured.ToArray();
    }

    internal static bool UsesFinalCombatPreviewInPower(GeneratorOperation operation, DynamicVar variable)
    {
        // Original-card proxy operations run their own powered commands when triggered. Baking their preview into
        // the proxy as well would apply Strength/Dexterity twice. The native composite damage/Block paths use
        // ValueProp.Unpowered and therefore need the played card's already-modified value.
        if (operation.Template.Contains(":Proxy", StringComparison.Ordinal)) return false;
        return variable switch
        {
            DamageVar damage => damage.Props.IsPoweredAttack(),
            BlockVar block => block.Props.IsPoweredCardOrMonsterMoveBlock(),
            _ => false
        };
    }

    internal void ApplyCapturedOperationValues(IReadOnlyList<int> captured)
    {
        _capturedExternalDamageBonuses.Clear();
        for (var offset = 0; offset + 1 < captured.Count; offset += 2)
        {
            var index = captured[offset];
            if (index < 0)
            {
                _capturedExternalDamageBonuses[ExternalDamageBonusIndex(index)] = captured[offset + 1];
                continue;
            }
            if ((uint)index >= (uint)Generated.Operations.Count) continue;
            var operation = Generated.Operations[index];
            if (DynamicVars.TryGetValue(ChaosOperationVariables.Name(operation, index), out var variable))
                variable.BaseValue = captured[offset + 1];
        }
    }

    internal int CapturedExternalDamageBonus(int operationIndex) =>
        _capturedExternalDamageBonuses.GetValueOrDefault(operationIndex);

    private bool HasExternalDamageDependency(int operationIndex) => operationIndex > 0
        && ChaosOperationExecutor.IsExternallyScaledDamageDependency(Generated.Operations[operationIndex - 1])
        && CardEffectRules.IsEnemyDamage(Generated.Operations[operationIndex]);

    internal static int ExternalDamageBonusKey(int operationIndex) => -operationIndex - 1;
    internal static int ExternalDamageBonusIndex(int key) => -key - 1;
}

internal sealed class ChaosDamageVar(string name, decimal damage, ValueProp props) : DamageVar(name, damage, props)
{
    public override void UpdateCardPreview(CardModel card, CardPreviewMode previewMode,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature? target, bool runGlobalHooks)
    {
        // Combat-only permanent growth (Rampage-style effects) is stored separately so save data remains stable,
        // but it still belongs in the number shown on the card.
        var growth = card is ChaosCardModel chaos ? chaos.ExtraDamage : 0;
        var original = BaseValue;
        BaseValue = original + growth;
        base.UpdateCardPreview(card, previewMode, target, runGlobalHooks);
        var preview = PreviewValue;
        var enchanted = EnchantedValue;
        BaseValue = original;
        EnchantedValue = enchanted;
        PreviewValue = preview;
    }
}

internal sealed class ChaosOstyDamageVar(string name, decimal damage, ValueProp props)
    : OstyDamageVar(name, damage, props)
{
    public override void UpdateCardPreview(CardModel card, CardPreviewMode previewMode,
        Creature? target, bool runGlobalHooks)
    {
        // Match ChaosDamageVar's persistent-growth projection, but retain OstyDamageVar's Osty dealer. A regular
        // DamageVar asks player hooks for the preview and consequently cannot see Calcify or Osty's own powers.
        var growth = card is ChaosCardModel chaos ? chaos.ExtraDamage : 0;
        var original = BaseValue;
        BaseValue = original + growth;
        base.UpdateCardPreview(card, previewMode, target, runGlobalHooks);
        var preview = PreviewValue;
        var enchanted = EnchantedValue;
        BaseValue = original;
        EnchantedValue = enchanted;
        PreviewValue = preview;
    }
}

internal sealed class ChaosBlockVar(string name, decimal block, ValueProp props) : BlockVar(name, block, props)
{
    public override void UpdateCardPreview(CardModel card, CardPreviewMode previewMode,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature? target, bool runGlobalHooks)
    {
        var growth = card is ChaosCardModel chaos ? chaos.ExtraBlock : 0;
        var original = BaseValue;
        BaseValue = original + growth;
        base.UpdateCardPreview(card, previewMode, target, runGlobalHooks);
        var preview = PreviewValue;
        var enchanted = EnchantedValue;
        BaseValue = original;
        EnchantedValue = enchanted;
        PreviewValue = preview;
    }
}

internal enum ChaosStatPreviewKind
{
    Block,
    Damage,
    Hits,
    Cards,
    Focus,
    Channels,
    ChannelRepeats,
    Strength
}

/// <summary>
/// Adds base-game-style, combat-only totals to operations whose printed value depends on the current combat
/// state. The ordinary variable remains the per-item value; this separate value is the result right now.
/// </summary>
internal static class ChaosStatPreview
{
    private static readonly HashSet<string> RepeatModifiers =
    [
        "M:RepeatPerAttackThisTurn", "M:RepeatPerSkillInHand", "D:RepeatPerOrb",
        "NCR:RepeatPerVoidPlayedCombat",
        "NCR:RepeatPerOstyAttackThisTurn", "R:RepeatPerSkillPlayedThisTurn",
        "R:RepeatPerStarGainedThisTurn"
    ];

    private static readonly HashSet<string> DamageModifiers =
    [
        "M:DamagePerExhaustCard", "M:DamagePerDiscardThisTurn", "M:DamagePerCardDrawnCombat",
        "M:DamageMinusPerCardInHand", "NCR:DamagePerCardDrawnThisTurn", "NCR:OstyMaxHpBonusDamage",
        "NCR:OstyCurrentHpBonusDamage", "NCR:DamagePerExhaustedSoul", "NCR:DamagePerOstyAttackCard",
        "R:BonusPerStarCostCardInHand", "R:BonusPerGeneratedCardThisCombat", "CL:BonusPerUniqueDebuff"
    ];

    internal static string VariableName(ChaosStatPreviewKind kind, int index) => $"Calculated{kind}{index}";

    internal static string Description(ChaosStatPreviewKind kind, int index, bool chinese,
        GeneratorOperation? operation = null)
    {
        var variable = VariableName(kind, index);
        var orb = operation is null ? null
            : OrbSlotCatalog.ResolveOutput(operation.OrbOutputId, operation.Template);
        if (chinese)
        {
            var phrase = kind switch
            {
                ChaosStatPreviewKind.Block => $"获得{{{variable}:diff()}}点格挡",
                ChaosStatPreviewKind.Damage => $"造成{{{variable}:diff()}}点伤害",
                ChaosStatPreviewKind.Hits => $"造成{{{variable}:diff()}}次伤害",
                ChaosStatPreviewKind.Cards => $"抽{{{variable}:diff()}}张牌",
                ChaosStatPreviewKind.Focus => $"获得{{{variable}:diff()}}点集中",
                ChaosStatPreviewKind.Channels => $"生成{{{variable}:diff()}}个{orb?.ChineseName ?? string.Empty}充能球",
                ChaosStatPreviewKind.ChannelRepeats => $"生成{{{variable}:diff()}}次",
                ChaosStatPreviewKind.Strength => $"获得{{{variable}:diff()}}点力量",
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            return $"{{InCombat:\n（{phrase}）|}}";
        }

        var english = kind switch
        {
            ChaosStatPreviewKind.Block => $"Gain {{{variable}:diff()}} Block",
            ChaosStatPreviewKind.Damage => $"Deal {{{variable}:diff()}} damage",
            ChaosStatPreviewKind.Hits => $"Deal damage {{{variable}:diff()}} times",
            ChaosStatPreviewKind.Cards => $"Draw {{{variable}:diff()}} cards",
            ChaosStatPreviewKind.Focus => $"Gain {{{variable}:diff()}} Focus",
            ChaosStatPreviewKind.Channels => $"Channel {{{variable}:diff()}} {orb?.EnglishName ?? "Orbs"}",
            ChaosStatPreviewKind.ChannelRepeats => $"Channel {{{variable}:diff()}} times",
            ChaosStatPreviewKind.Strength => $"Gain {{{variable}:diff()}} Strength",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return $"{{InCombat:\n({english})|}}";
    }

    internal static bool TryGetKind(IReadOnlyList<GeneratorOperation> operations, int index,
        out ChaosStatPreviewKind kind)
    {
        var operation = operations[index];
        kind = operation.Template switch
        {
            "N:BlockEqualAllPoison" or "CL:GainBlockEqualCurrent" or "CL:GainNextTurnBlockEqualCurrent"
                or "NCR:BlockTripleOstyMaxHp" => ChaosStatPreviewKind.Block,
            "NCR:DoomScaledDamage" or "CL:DamageEqualCardsPlayedCombat" => ChaosStatPreviewKind.Damage,
            "N:StrengthPerTargetVulnerable" => ChaosStatPreviewKind.Strength,
            "I:ProxyAtomic_Voltaic" => ChaosStatPreviewKind.Channels,
            _ => default
        };
        if (operation.Template is "N:BlockEqualAllPoison" or "CL:GainBlockEqualCurrent"
            or "CL:GainNextTurnBlockEqualCurrent" or "NCR:BlockTripleOstyMaxHp" or "NCR:DoomScaledDamage"
            or "CL:DamageEqualCardsPlayedCombat"
            or "N:StrengthPerTargetVulnerable" or "I:ProxyAtomic_Voltaic") return true;
        if (operation.Template == "N:Self"
            && OperationRuntimeSpecCompiler.RequireStructured(operation).Variant
                == "strength_per_target_vulnerable")
        {
            kind = ChaosStatPreviewKind.Strength;
            return true;
        }

        if (operation.Scope == OperationScope.Modifier)
        {
            OperationRuntimeSpec? modifierSpec = null;
            if (operation.Template is "M:base" or "M:repeat")
                modifierSpec = OperationRuntimeSpecCompiler.RequireStructured(operation);
            if (RepeatModifiers.Contains(operation.Template)
                || modifierSpec?.Opcode == "modify_hits")
            {
                kind = ChaosStatPreviewKind.Hits;
                return true;
            }
            if (modifierSpec?.Opcode == "modify_block")
            {
                kind = ChaosStatPreviewKind.Block;
                return true;
            }
            if (DamageModifiers.Contains(operation.Template)
                || ChaosOperationExecutor.IsExhaustPileDamageModifier(operation)
                || CardEffectRules.IsCurrentBlockDamageModifier(operation)
                || modifierSpec?.Opcode == "modify_damage")
            {
                kind = ChaosStatPreviewKind.Damage;
                return true;
            }
        }

        if (index == 0) return false;
        var prefix = operations[index - 1];
        if (!CardEffectRules.IsDependencyPrefix(prefix)
            || !CardEffectRules.IsLegalDependencyPayoff(prefix, operation)
            || prefix.Parameters.GetValueOrDefault("triggerIndex", -1)
                != operation.Parameters.GetValueOrDefault("triggerIndex", -1)) return false;

        if (CardEffectRules.IsMultiplicativeDependencyPrefix(prefix))
        {
            var payoffSpec = OperationRuntimeSpecCompiler.RequireStructured(operation);
            // Count prefixes repeat a Damage action as separate hits. Their combat preview therefore reports the
            // live hit count, while Block/Poison/other additive payoffs continue to report their summed amount.
            if (CardEffectRules.IsEnemyDamage(operation)) kind = ChaosStatPreviewKind.Hits;
            else if (payoffSpec.Opcode == "gain_block") kind = ChaosStatPreviewKind.Block;
            else if (payoffSpec.Opcode == "draw_cards") kind = ChaosStatPreviewKind.Cards;
            // A count prefix repeats the complete Channel action. Its parenthetical preview must show how many
            // times that action resolves, rather than multiplying the action's own Orb amount into a second
            // misleading "Channel N Orbs" value.
            else if (payoffSpec.Flags.Contains("orb_channel_reference"))
                kind = ChaosStatPreviewKind.ChannelRepeats;
            else if (payoffSpec.Variant is "focus" or "focus_this_turn") kind = ChaosStatPreviewKind.Focus;
            else if (payoffSpec.Variant is "strength" or "strength_this_turn") kind = ChaosStatPreviewKind.Strength;
            else return false;
            return true;
        }

        kind = (prefix.Template, operation.Template) switch
        {
            ("D:ForEachEnemy", "D:ChannelFrost") => ChaosStatPreviewKind.Channels,
            ("D:ForEachUniqueOrb", "N:Draw") => ChaosStatPreviewKind.Cards,
            ("D:ForEachUniqueOrb", "N:B") => ChaosStatPreviewKind.Block,
            ("D:ForEachUniqueOrb", "N_BLOCK") => ChaosStatPreviewKind.Block,
            ("D:ForEachUniqueOrb", "D:GainTemporaryFocus") => ChaosStatPreviewKind.Focus,
            ("CL:ForEachCardPlayedCombat", _) when CardEffectRules.IsEnemyDamage(operation) => ChaosStatPreviewKind.Damage,
            ("CL:ForEachDrawPileCard", _) when CardEffectRules.IsEnemyDamage(operation) => ChaosStatPreviewKind.Damage,
            _ => default
        };
        return (prefix.Template, operation.Template) switch
        {
            ("D:ForEachEnemy", "D:ChannelFrost") or ("D:ForEachUniqueOrb", "N:Draw")
                or ("D:ForEachUniqueOrb", "N:B") or ("D:ForEachUniqueOrb", "N_BLOCK")
                or ("D:ForEachUniqueOrb", "D:GainTemporaryFocus") => true,
            ("CL:ForEachCardPlayedCombat", _) or ("CL:ForEachDrawPileCard", _)
                => CardEffectRules.IsEnemyDamage(operation),
            _ => false
        };
    }

    internal static decimal Calculate(ChaosCardModel card, int index, ChaosStatPreviewKind kind, Creature? target)
    {
        var operation = card.Generated.Operations[index];
        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        switch (operation.Template)
        {
            case "N:BlockEqualAllPoison":
                return combatState?.HittableEnemies.Sum(enemy => enemy.GetPower<PoisonPower>()?.Amount ?? 0) ?? 0;
            case "CL:GainBlockEqualCurrent":
            case "CL:GainNextTurnBlockEqualCurrent":
                return card.Owner.Creature.Block;
            case "NCR:BlockTripleOstyMaxHp":
                return card.Owner.IsOstyAlive
                    ? (card.Owner.Osty?.MaxHp ?? 0) * Math.Max(1, card.OperationAmount(index))
                    : 0;
            case "NCR:DoomScaledDamage":
                return target?.GetPower<DoomPower>()?.Amount ?? 0;
            case "N:StrengthPerTargetVulnerable":
            case "N:Self" when OperationRuntimeSpecCompiler.RequireStructured(operation).Variant
                == "strength_per_target_vulnerable":
                return (target?.GetPower<VulnerablePower>()?.Amount ?? 0)
                    * Math.Max(1, card.OperationAmount(index));
            case "I:ProxyAtomic_Voltaic":
                return CombatManager.Instance.History.Entries.OfType<OrbChanneledEntry>()
                    .Count(entry => entry.Actor == card.Owner.Creature
                        && ChaosOrbResolver.MatchesSource(entry.Orb, operation));
        }

        if (kind == ChaosStatPreviewKind.ChannelRepeats)
            return ChaosOperationExecutor.DependencyMultiplier(card, index, target);

        if (kind is ChaosStatPreviewKind.Cards or ChaosStatPreviewKind.Focus or ChaosStatPreviewKind.Channels)
            return card.OperationAmount(index)
                * ChaosOperationExecutor.DependencyMultiplier(card, index, target);

        if (kind == ChaosStatPreviewKind.Block)
        {
            var blockIndex = operation.Scope == OperationScope.Modifier
                ? card.Generated.Operations.ToList().FindIndex(candidate => candidate.Template is "N:B" or "N_BLOCK")
                : index;
            if (blockIndex < 0) return 0;
            var block = card.OperationAmount(blockIndex)
                * ChaosOperationExecutor.DependencyMultiplier(card, blockIndex, target);
            return ChaosOperationExecutor.ApplyBlockModifiers(card, block, blockIndex, target);
        }

        var damageIndex = operation.Scope == OperationScope.Modifier
            ? card.Generated.Operations.ToList().FindIndex(CardEffectRules.IsEnemyDamage)
            : index;
        if (damageIndex < 0) return 0;
        var baseDamage = card.OperationAmount(damageIndex);
        if (OperationRuntimeSpecCompiler.RequireStructured(
                card.Generated.Operations[damageIndex]).Variant == "cards_played_combat")
            baseDamage = CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
                entry.CardPlay.Player == card.Owner);
        var result = ChaosOperationExecutor.DamageAndHits(card, baseDamage,
            new ChaosExecutionState { Target = target }, damageOperationIndex: damageIndex);
        return kind == ChaosStatPreviewKind.Hits ? result.Hits : result.Damage;
    }

    internal static void Audit()
    {
        static GeneratorOperation Op(string template, OperationScope scope, string text)
        {
            var operation = new GeneratorOperation(template, scope, text, new Dictionary<string, int>());
            return operation with { RuntimeSpec = OperationRuntimeSpecCompiler.CompileLegacy(operation) };
        }
        var poison = new[] { Op("N:BlockEqualAllPoison", OperationScope.NonTargeted, "获得等同于所有敌人中毒层数总和的格挡") };
        if (!TryGetKind(poison, 0, out var poisonKind) || poisonKind != ChaosStatPreviewKind.Block
            || !Description(poisonKind, 0, true).Contains("{InCombat:", StringComparison.Ordinal)
            || !Description(poisonKind, 0, true).Contains("CalculatedBlock0", StringComparison.Ordinal))
            throw new InvalidOperationException("AutoAnthony poison/block statistical preview audit failed.");
        var uniqueOrbDraw = new[]
        {
            Op("D:ForEachUniqueOrb", OperationScope.Modifier, "每有一种不同的充能球，"),
            Op("N:Draw", OperationScope.NonTargeted, "抽1张牌")
        };
        if (!TryGetKind(uniqueOrbDraw, 1, out var drawKind) || drawKind != ChaosStatPreviewKind.Cards)
            throw new InvalidOperationException("AutoAnthony dependency statistical preview audit failed.");
        var uniqueOrbChannel = new[]
        {
            Op("D:ForEachUniqueOrb", OperationScope.Modifier, "每有一种不同的充能球，"),
            Op("D:ChannelLightning", OperationScope.NonTargeted, "生成2个闪电充能球。")
        };
        if (!TryGetKind(uniqueOrbChannel, 1, out var channelKind)
            || channelKind != ChaosStatPreviewKind.ChannelRepeats
            || !Description(channelKind, 1, true).Contains("生成{CalculatedChannelRepeats1:diff()}次",
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "AutoAnthony repeated Channel dependency preview must display the live resolution count.");
        var staticRepeat = new[]
        {
            Op("D:RepeatDamage", OperationScope.Modifier, "这张牌额外造成2次伤害。")
        };
        if (TryGetKind(staticRepeat, 0, out _))
            throw new InvalidOperationException("AutoAnthony static repeat modifier must not add an in-combat preview.");
        var dynamicRepeat = new[]
        {
            Op("D:RepeatPerOrb", OperationScope.Modifier, "当前每有一个充能球，这张牌就造成一次伤害。")
        };
        if (!TryGetKind(dynamicRepeat, 0, out var repeatKind) || repeatKind != ChaosStatPreviewKind.Hits)
            throw new InvalidOperationException("AutoAnthony dynamic repeat modifier must retain its in-combat preview.");
        var starCardRepeatedDamage = new[]
        {
            Op("R:ForEachStarCostCard", OperationScope.Modifier, "你的所有牌中每有一张有蓝星耗费的牌，"),
            Op("T:D", OperationScope.SingleEnemyOnly, "造成4点伤害。")
        };
        if (!TryGetKind(starCardRepeatedDamage, 1, out var starDamageKind)
            || starDamageKind != ChaosStatPreviewKind.Hits
            || !Description(starDamageKind, 1, true).Contains("次伤害", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "AutoAnthony repeated Damage dependency preview must display the live number of hits.");
    }
}

internal sealed class ChaosCalculatedPreviewVar(string name, int operationIndex, ChaosStatPreviewKind kind)
    : DynamicVar(name, 0)
{
    public override void UpdateCardPreview(CardModel card, CardPreviewMode previewMode, Creature? target,
        bool runGlobalHooks)
    {
        if (card is not ChaosCardModel chaos || card.Owner is null)
        {
            ResetToBase();
            return;
        }

        decimal value;
        // Cards in a combat pile may expose the combat through their owner before CardModel.CombatState has been
        // populated. Runtime dependency resolution already uses this fallback; the preview must use it as well.
        if (!CombatManager.Instance.IsInProgress
            || chaos.CombatState is null && chaos.Owner.Creature.CombatState is null)
            value = 0;
        else try
        {
            value = ChaosStatPreview.Calculate(chaos, operationIndex, kind, target);
        }
        catch (InvalidOperationException)
        {
            value = 0;
        }

        var enchantment = card.Enchantment;
        if (kind == ChaosStatPreviewKind.Block)
        {
            var enchanted = value;
            if (enchantment is not null)
            {
                enchanted += enchantment.EnchantBlockAdditive(enchanted);
                enchanted *= enchantment.EnchantBlockMultiplicative(enchanted);
            }
            EnchantedValue = enchanted;
            PreviewValue = runGlobalHooks && chaos.Generated.Operations[operationIndex].Template != "CL:GainNextTurnBlockEqualCurrent"
                ? Hook.ModifyBlock(card.CombatState ?? card.Owner.Creature.CombatState!, card.Owner.Creature,
                    value, ValueProp.Move, card, null, out _)
                : enchanted;
        }
        else if (kind == ChaosStatPreviewKind.Damage)
        {
            var enchanted = value;
            if (enchantment is not null)
            {
                enchanted += enchantment.EnchantDamageAdditive(enchanted, ValueProp.Move);
                enchanted *= enchantment.EnchantDamageMultiplicative(enchanted, ValueProp.Move);
            }
            EnchantedValue = enchanted;
            PreviewValue = runGlobalHooks
                ? Hook.ModifyDamage(card.Owner.RunState, card.CombatState ?? card.Owner.Creature.CombatState,
                    target, card.Owner.Creature, value, ValueProp.Move, card, null, ModifyDamageHookType.All,
                    previewMode, out _)
                : enchanted;
        }
        else
        {
            EnchantedValue = value;
            PreviewValue = value;
        }
        PreviewValue = Math.Max(0, PreviewValue);
    }
}

internal static class ChaosOperationVariables
{
    private static readonly System.Text.RegularExpressions.Regex Number = new(@"\d+", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static string Name(GeneratorOperation operation, int operationIndex)
    {
        var prefix = operation.Template.StartsWith("T:ProxyDamage_", StringComparison.Ordinal)
            || operation.Template.StartsWith("N:ProxyDamage_", StringComparison.Ordinal)
            ? "Damage"
            : operation.Template switch
        {
            "T:D" or "T:DX" or "T:D_EnergyX" or "N:AllD" or "N:RandomD" or "N:RetaliateDamage"
                or "NCR:OstyDamage" or "NCR:OstyAllDamage" or "CL:RollingAllDamage"
                or "D:RepeatPerEnergySpentThisTurn" => "Damage",
            "N:B" or "N_BLOCK" => "Block",
            "N:E" or "N:NextTurnEnergy" or "D:GainEnergy" or "D:NextTurnEnergy" or "NCR:GainEnergy" or "NCR:NextTurnEnergy"
                or "R:GainEnergy" => "Energy",
            "N:HP-" => "HpLoss",
            _ => "Amount"
        };
        return $"{prefix}{operationIndex}";
    }

    internal static bool TryGetInitialValue(GeneratorOperation operation, out int value)
    {
        if (!TryGetInitialSlot(operation, out var slotId))
        {
            value = 0;
            return false;
        }
        var slot = OperationRuntimeSpecCompiler.RequireStructured(operation).Values
            .First(candidate => candidate.Id == slotId);
        value = slot.BaseValue + slot.Offset;
        return slot.Source == "fixed";
    }

    internal static bool TryGetInitialSlot(GeneratorOperation operation, out string slotId)
    {
        var spec = OperationRuntimeSpecCompiler.RequireStructured(operation);
        // Preserve the documented schema5-7 M:base display-variable anomaly until its dedicated behavior
        // migration: the first printed interval was historically the DynamicVar even though the payoff is the
        // upgradable block_per_interval slot.
        if (operation.Template == "M:base" && spec.Variant == "strength_scaled")
        {
            slotId = "strength_interval";
            return true;
        }
        // This operation prints Draw first and Block second, but only Block is a live preview variable. Older
        // display code found the second localized number; keep that behavior structurally by naming the slot.
        if (operation.Template == "I:DrawAndBlockIfSkill"
            && spec.Values.Any(value => value.Id == "block" && value.Source == "fixed"))
        {
            slotId = "block";
            return true;
        }
        var slot = spec.Values
            .FirstOrDefault(value => value.Upgradable && value.Source == "fixed");
        slotId = slot?.Id ?? string.Empty;
        return slot is not null;
    }

    internal static GeneratorOperation ReplaceInitialValue(GeneratorOperation operation, int value)
    {
        if (operation.LocalizedText is { } localized && TryGetInitialSlot(operation, out var slotId))
        {
            var spec = OperationRuntimeSpecCompiler.RequireStructured(operation);
            var updatedSpec = OperationRuntimeSpecCompiler.ReplaceFixedValueInSpec(spec, slotId,
                Math.Max(0, value));
            return operation with
            {
                ChineseText = localized.RenderChinese(updatedSpec),
                RuntimeSpec = updatedSpec
            };
        }
        var text = operation.ChineseText;
        if (!OperationRuntimeSpecCompiler.TryProjectLegacyDynamicValue(operation, text, out var projection)
            || projection is null)
            return operation;
        var projected = projection.Length == 0
            ? text
            : text[..projection.Start] + Math.Max(0, value).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + text[(projection.Start + projection.Length)..];
        return operation with { ChineseText = projected };
    }

    internal static string InsertToken(GeneratorOperation operation, int operationIndex, string text, bool chinese)
    {
        var spec = OperationRuntimeSpecCompiler.RequireStructured(operation);
        if (operation.Template == "A:whenOneStarSpent")
        {
            var threshold = spec.Values.FirstOrDefault(value => value.Id == "threshold")
                ?? spec.Values.FirstOrDefault(value => value.Source == "fixed" && value.Explicit);
            if (threshold is not null)
            {
                var starName = Name(operation, operationIndex);
                var replacement = threshold.BaseValue + threshold.Offset > 5
                    ? $"{{{starName}:diff()}}{{singleStarIcon}}"
                    : $"{{{starName}:starIcons()}}";
                if (operation.LocalizedText?.TryReplaceRenderedSlot(text, spec, threshold.Id, replacement,
                        chinese, out var structuredStars) == true)
                {
                    // starIcons() is the complete resource glyph, not a numeric prefix. Remove the localized
                    // resource noun left around the replaced numeric slot ("icons颗蓝星" / "icons Stars").
                    return chinese
                        ? structuredStars.Replace(replacement + "颗蓝星", replacement, StringComparison.Ordinal)
                        : System.Text.RegularExpressions.Regex.Replace(structuredStars,
                            System.Text.RegularExpressions.Regex.Escape(replacement) + @"\s+Stars?",
                            replacement, System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                            TimeSpan.FromMilliseconds(50));
                }
            }
        }
        if (operation.Template is "A:whenEnergySpent" or "D:ForEachEnergySpentThisTurn")
        {
            var threshold = spec.Values.FirstOrDefault(value => value.Id == "threshold")
                ?? spec.Values.FirstOrDefault(value => value.Source == "fixed" && value.Explicit);
            if (threshold is not null && operation.LocalizedText?.TryReplaceRenderedSlot(text, spec,
                    threshold.Id, $"{{energyPrefix:energyIcons({Math.Max(0, threshold.BaseValue + threshold.Offset)})}}",
                    chinese, out var structuredEnergy) == true)
                return structuredEnergy;
            // Schema1-9 compatibility only. New operations always take the named-slot path above.
            var match = Number.Match(text);
            if (!match.Success) return text;
            var icon = $"{{energyPrefix:energyIcons({match.Value})}}";
            return text[..match.Index] + icon + text[(match.Index + match.Length)..]
                .Replace(chinese ? "点能量" : " Energy", string.Empty, StringComparison.Ordinal);
        }
        if (!TryGetInitialValue(operation, out _)) return text;
        var name = Name(operation, operationIndex);
        if (operation.Template is "N:E" or "N:NextTurnEnergy" or "D:GainEnergy" or "D:NextTurnEnergy" or "NCR:GainEnergy"
            or "NCR:NextTurnEnergy" or "R:GainEnergy")
        {
            if (TryGetInitialSlot(operation, out var energySlot)
                && operation.LocalizedText?.TryReplaceRenderedSlot(text, spec, energySlot,
                    $"{{{name}:energyIcons()}}", chinese, out var structuredEnergy) == true)
                return structuredEnergy;
            var pattern = chinese ? @"获得\d+点能量" : @"(?:Gain|gain) \d+ Energy";
            return new System.Text.RegularExpressions.Regex(pattern).Replace(text, match => chinese
                ? $"获得{{{name}:energyIcons()}}"
                : $"{(match.Value.StartsWith("gain", StringComparison.Ordinal) ? "gain" : "Gain")} {{{name}:energyIcons()}}", 1);
        }
        if (operation.Template == "R:GainStars")
        {
            if (TryGetInitialSlot(operation, out var starSlot))
            {
                var slot = spec.Values.First(value => value.Id == starSlot);
                var useCompactStars = slot.BaseValue + slot.Offset > 5;
                var structuredReplacement = useCompactStars
                    ? $"{{{name}:diff()}}{{singleStarIcon}}"
                    : $"{{{name}:starIcons()}}";
                if (operation.LocalizedText?.TryReplaceRenderedSlot(text, spec, starSlot, structuredReplacement,
                        chinese, out var structuredStars) == true)
                    return structuredStars;
            }
            var pattern = chinese ? @"获得\d+颗蓝星" : @"Gain \d+ Stars?";
            var match = new System.Text.RegularExpressions.Regex(pattern).Match(text);
            if (!match.Success) return text;
            var amountMatch = Number.Match(match.Value);
            var compact = amountMatch.Success && int.Parse(amountMatch.Value) > 5;
            var replacement = compact
                ? chinese ? $"获得{{{name}:diff()}}{{singleStarIcon}}" : $"Gain {{{name}:diff()}} {{singleStarIcon}}"
                : chinese ? $"获得{{{name}:starIcons()}}" : $"Gain {{{name}:starIcons()}}";
            return new System.Text.RegularExpressions.Regex(pattern).Replace(text, replacement, 1);
        }
        if (TryGetInitialSlot(operation, out var initialSlot)
            && operation.LocalizedText?.TryReplaceRenderedSlot(text, spec, initialSlot,
                $"{{{name}:diff()}}", chinese, out var structured) == true)
            return AddEnglishPluralSelectors(operation, structured, name, chinese);
        if (operation.Template == "I:DrawAndBlockIfSkill")
        {
            var matches = Number.Matches(text);
            if (matches.Count < 2) return text;
            var match = matches[1];
            return text[..match.Index] + $"{{{name}:diff()}}" + text[(match.Index + match.Length)..];
        }
        var result = Number.Replace(text, $"{{{name}:diff()}}", 1);
        return AddEnglishPluralSelectors(operation, result, name, chinese);
    }

    private static string AddEnglishPluralSelectors(GeneratorOperation operation, string result, string name,
        bool chinese)
    {
        if (!chinese)
        {
            var token = $"{{{name}:diff()}}";
            if (operation.Template == "I:AutoPlayRandomAttackFromHand")
            {
                result = System.Text.RegularExpressions.Regex.Replace(result,
                    $@"{System.Text.RegularExpressions.Regex.Escape(token)} random Attacks? from your Hand against (?:a random enemy|random enemies)",
                    $"{token} random {{{name}:plural:Attack|Attacks}} from your Hand against {{{name}:plural:a random enemy|random enemies}}");
            }
            var nouns = new (string Singular, string Plural)[]
            {
                ("card", "cards"), ("Attack", "Attacks"), ("Skill", "Skills"), ("Power", "Powers"),
                ("Orb", "Orbs"), ("Star", "Stars"), ("Soul", "Souls"), ("Wound", "Wounds"),
                ("Shiv", "Shivs"), ("time", "times")
            };
            foreach (var (singular, plural) in nouns)
            {
                result = System.Text.RegularExpressions.Regex.Replace(result,
                    $@"{System.Text.RegularExpressions.Regex.Escape(token)}( random )?(?:{singular}|{plural})\b(?!\+| [Ss]lots)",
                    match => token + match.Groups[1].Value + $"{{{name}:plural:{singular}|{plural}}}");
            }
        }
        return result;
    }
}

internal static class ChaosRuntimeDescriptionRenderer
{
    private static string RenderEffects(ChaosCardModel card, IReadOnlyList<GeneratorOperation> effectiveOperations,
        IReadOnlyList<int> effectIndices, bool chinese, bool attackReceived = false)
    {
        var pieces = new List<string>();
        for (var cursor = 0; cursor < effectIndices.Count; cursor++)
        {
            var index = effectIndices[cursor];
            var operation = card.Generated.Operations[index];
            if (CardEffectRules.IsCurrentBlockDamageAnchor(card.Generated.Operations, index))
                continue;
            if (CardEffectRules.IsDependencyPrefix(operation) && cursor + 1 < effectIndices.Count
                && CardEffectRules.IsLegalDependencyPayoff(operation, card.Generated.Operations[effectIndices[cursor + 1]]))
            {
                var prefix = Text(card, effectiveOperations, index, chinese).TrimEnd('.', '。');
                var payoff = Text(card, effectiveOperations, effectIndices[++cursor], chinese);
                var combined = chinese
                    ? CardDescriptionRenderer.JoinChineseClause(prefix, payoff)
                    : prefix + " " + LowerFirst(payoff);
                pieces.Add(attackReceived
                    ? CardDescriptionRenderer.AdaptAttackReceivedPayoff(
                        card.Generated.Operations[effectIndices[cursor]], combined, chinese)
                    : combined);
                continue;
            }
            var rendered = Text(card, effectiveOperations, index, chinese);
            pieces.Add(attackReceived
                ? CardDescriptionRenderer.AdaptAttackReceivedPayoff(operation, rendered, chinese)
                : rendered);
        }
        return chinese ? string.Join(string.Empty, pieces) : string.Join(" ", pieces);
    }

    internal static string Render(ChaosCardModel card, bool chinese)
    {
        var operations = card.Generated.Operations;
        var effectiveOperations = card.IsUpgraded && card.Generated.Upgrade is { } upgrade
            ? CardUpgradeGenerator.ApplyEffectsToOperations(operations, upgrade.Effects)
            : operations;
        var lines = new List<string>();
        foreach (var effect in card.Generated.Upgrade?.Effects.Where(effect =>
                     effect.Kind == CardUpgradeKind.ExecuteOperationOnPlay) ?? [])
        {
            if (effect.OperationIndex is not { } operationIndex
                || (uint)operationIndex >= (uint)effectiveOperations.Count) continue;
            var immediate = effectiveOperations[operationIndex] with
            {
                Parameters = effectiveOperations[operationIndex].Parameters
                    .Where(pair => pair.Key != "triggerIndex")
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            };
            var addedText = RenderOperationVariant(card, operationIndex, chinese, immediate);
            lines.Add(UpgradeConditional(addedText, string.Empty, chinese));
        }
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (CardEffectRules.IsCurrentBlockDamageAnchor(operations, index))
                continue;
            // Card-slot selectors drive the selection screen; the owning operation already contains the
            // printed instruction, so exposing the selector would duplicate the card text.
            if (operation.Template is "N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK")
                continue;
            if (operation.Template == "I:ExhaustRandomAttack"
                && index + 1 < operations.Count
                && operations[index + 1].Template == "I:AddExhaustedAttackDamage")
            {
                lines.Add(chinese
                    ? "消耗你的手牌中随机一张攻击牌，并将消耗的牌的攻击力添加到这张牌。"
                    : "Exhaust a random Attack in your Hand and add the Exhausted card's Attack damage to this card.");
                index++;
                continue;
            }
            if (operation.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger)
            {
                var effects = Enumerable.Range(index + 1, operations.Count - index - 1)
                    .Where(effectIndex => operations[effectIndex].Scope is not OperationScope.AbilityTrigger and not OperationScope.ConditionalTrigger)
                    .Where(effectIndex => operations[effectIndex].Parameters.TryGetValue("triggerIndex", out var trigger) && trigger == index)
                    .ToArray();
                if (CardEffectRules.IsNextAttackGrantTrigger(operation))
                {
                    var effectiveTrigger = operation with
                    {
                        ChineseText = Text(card, effectiveOperations, index, chinese)
                    };
                    var effectiveEffects = effects.Select(effectIndex => operations[effectIndex] with
                    {
                        ChineseText = Text(card, effectiveOperations, effectIndex, chinese)
                    }).ToArray();
                    lines.Add(CardDescriptionRenderer.RenderNextAttackGrant(effectiveTrigger, effectiveEffects,
                        effect => effect.ChineseText, chinese));
                    continue;
                }
                var trigger = Text(card, effectiveOperations, index, chinese).TrimEnd('.', '。', '，', '；');
                if (effects.Length == 0)
                {
                    lines.Add(trigger + (chinese ? "。" : "."));
                    continue;
                }
                var attackReceived = CardEffectRules.TriggerImplicitlyTargetsAttacker(operation);
                // Use the same clause joiner as the offline Chinese/English descriptions. Dynamic variables make
                // runtime rendering necessary, but must not reintroduce full stops between effects owned by one
                // condition or trigger.
                var renderedEffects = CardDescriptionRenderer.JoinTriggeredEffects(
                    RenderEffects(card, effectiveOperations, effects, chinese, attackReceived), chinese);
                if (attackReceived)
                {
                    lines.Add(chinese
                        ? CardDescriptionRenderer.JoinChineseClause(
                            "你在这个回合每受到一次攻击，都会", renderedEffects)
                        : $"Whenever you are attacked this turn, {LowerFirst(renderedEffects)}");
                    continue;
                }
                if (chinese)
                {
                    if (operation.Scope == OperationScope.AbilityTrigger
                        && renderedEffects.StartsWith("在本回合", StringComparison.Ordinal))
                        renderedEffects = renderedEffects[1..];
                    var separator = operation.Template == "C:playableIfDrawPileEmpty" ? "。"
                        : operation.Template == "C:untilTurnEndCardDrawn" ? "，就"
                        : OperationRuntimeSpecCompiler.RequireStructured(operation).Trigger?.Kind == "attack_received" ? "，都会"
                        : operation.Template == "C:ifLastDrawnSkill" || trigger.StartsWith("如果", StringComparison.Ordinal) ? "，则"
                        : "，";
                    lines.Add(CardDescriptionRenderer.JoinChineseClause(trigger + separator, renderedEffects));
                }
                else if (operation.Template == "C:playableIfDrawPileEmpty")
                    lines.Add($"{trigger}. {UpperFirst(renderedEffects)}");
                else
                    lines.Add($"{trigger}, {LowerFirst(renderedEffects)}");
                continue;
            }
            if (!operation.Parameters.ContainsKey("triggerIndex"))
            {
                if (CardEffectRules.IsDependencyPrefix(operation) && index + 1 < operations.Count
                    && !operations[index + 1].Parameters.ContainsKey("triggerIndex")
                    && CardEffectRules.IsLegalDependencyPayoff(operation, operations[index + 1]))
                {
                    lines.Add(RenderEffects(card, effectiveOperations, new[] { index, ++index }, chinese));
                    continue;
                }
                lines.Add(Text(card, effectiveOperations, index, chinese));
            }
        }

        var rendered = string.Join('\n', lines);
        return chinese ? CardTextStyle.NormalizeRenderedChineseEnergyNotation(rendered) : rendered;
    }

    private static string Text(ChaosCardModel card, IReadOnlyList<GeneratorOperation> effectiveOperations,
        int index, bool chinese)
    {
        var operation = card.Generated.Operations[index];
        var text = RenderOperationVariant(card, index, chinese, effectiveOperations[index]);
        if (card.Generated.Upgrade?.Effects.Any(effect =>
                effect.Kind == CardUpgradeKind.RepeatOperation && effect.OperationIndex == index) == true)
        {
            var normalText = RenderOperationVariant(card, index, chinese, operation);
            var upgradedOperation = CardUpgradeGenerator.ApplyEffectsToOperations(card.Generated.Operations,
                card.Generated.Upgrade.Effects)[index];
            var upgradedText = RenderOperationVariant(card, index, chinese, upgradedOperation);
            text = UpgradeConditional(upgradedText, normalText, chinese);
        }
        else if (card.Generated.Upgrade?.Effects.Any(effect =>
                effect.Kind == CardUpgradeKind.UpgradeReferencedCards && effect.OperationIndex == index) == true)
        {
            var normalText = RenderOperationVariant(card, index, chinese, operation);
            var upgradedOperation = CardUpgradeGenerator.ApplyEffectsToOperations(card.Generated.Operations,
                card.Generated.Upgrade.Effects)[index];
            var upgradedText = RenderOperationVariant(card, index, chinese, upgradedOperation);
            text = UpgradeConditional(upgradedText, normalText, chinese);
        }
        else if (card.Generated.Upgrade?.Effects.Any(effect =>
                     effect.Kind == CardUpgradeKind.UpgradeDerivative && effect.OperationIndex == index) == true
                 && DerivativeSlotCatalog.SupportsUpgrade(operation.Template, operation.DerivativeId)
                 && DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template) is not null)
        {
            // Upgrade previews are formatted while the card itself is still unupgraded. Put both derivative names
            // in the localization template so the built-in IfUpgraded variable selects “Shiv+” for both previews
            // and upgraded instances, while ordinary copies continue to show “Shiv”.
            var normalText = RenderOperationVariant(card, index, chinese, operation);
            var upgradedOperation = CardUpgradeGenerator.ApplyEffectsToOperations(card.Generated.Operations,
                card.Generated.Upgrade.Effects)[index];
            var upgradedText = RenderOperationVariant(card, index, chinese, upgradedOperation);
            text = UpgradeConditional(upgradedText, normalText, chinese);
        }
        else if (card.Generated.Upgrade?.Effects.Any(effect =>
                     effect.Kind == CardUpgradeKind.UpgradeGeneratedCards && effect.OperationIndex == index) == true)
        {
            var normalText = RenderOperationVariant(card, index, chinese, operation);
            var upgradedOperation = CardUpgradeGenerator.ApplyEffectsToOperations(card.Generated.Operations,
                card.Generated.Upgrade.Effects)[index];
            var upgradedText = RenderOperationVariant(card, index, chinese, upgradedOperation);
            text = UpgradeConditional(upgradedText, normalText, chinese);
        }
        if (!ChaosStatPreview.TryGetKind(card.Generated.Operations, index, out var previewKind))
            return text;
        var preview = ChaosStatPreview.Description(previewKind, index, chinese, operation);
        if (chinese && text.EndsWith('。')) return text[..^1] + preview + "。";
        if (!chinese && text.EndsWith('.')) return text[..^1] + preview + ".";
        return text + preview;
    }

    private static string RenderOperationVariant(ChaosCardModel card, int index, bool chinese,
        GeneratorOperation effective)
    {
        var text = chinese
            ? CardTextStyle.Chinese(effective, effective.ChineseText)
            : EnglishCardDescriptionRenderer.OperationText(effective);
        if (DerivativeSlotCatalog.Resolve(effective.DerivativeId, effective.Template)?.Id == "fuel")
            text = ChaosRunDefinitions.ReplaceFuelDisplay(text, chinese);
        text = ChaosDerivativeTextStyle.Apply(text, [effective], chinese);
        return ChaosOperationVariables.InsertToken(effective, index, text, chinese);
    }

    private static string UpgradeConditional(string upgraded, string normal, bool chinese)
    {
        var punctuation = chinese ? '。' : '.';
        var upgradedHasPunctuation = upgraded.EndsWith(punctuation);
        var normalHasPunctuation = normal.EndsWith(punctuation);
        if (upgradedHasPunctuation) upgraded = upgraded[..^1];
        if (normalHasPunctuation) normal = normal[..^1];
        var conditional = $"{{IfUpgraded:show:{upgraded}|{normal}}}";
        return upgradedHasPunctuation && normalHasPunctuation ? conditional + punctuation : conditional;
    }

    private static string LowerFirst(string text)
    {
        if (text.Length == 0 || text.StartsWith("Osty", StringComparison.Ordinal)
            || text.StartsWith("ALL", StringComparison.Ordinal)
            || text.StartsWith("Sovereign Blade", StringComparison.Ordinal)
            || text.Length > 1 && char.IsUpper(text[0]) && char.IsUpper(text[1]))
            return text;
        return char.ToLowerInvariant(text[0]) + text[1..];
    }

    private static string UpperFirst(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>
/// Generated description templates are immutable for a card definition and upgrade state. Card UI asks for the
/// same template repeatedly while laying out, hovering, and previewing a card; cache the regex-heavy render by the
/// generated-card object shared by all of that slot's runtime copies. Weak keys let old run definitions disappear.
/// </summary>
internal static class ChaosRuntimeDescriptionCache
{
    private sealed class Entry
    {
        internal string? Chinese;
        internal string? ChineseUpgraded;
        internal string? English;
        internal string? EnglishUpgraded;
    }

    private static readonly ConditionalWeakTable<GeneratedCard, Entry> Entries = new();
    private static readonly ConditionalWeakTable<LocTable, InstalledTemplates> InstalledByTable = new();

    private sealed class InstalledTemplates
    {
        internal readonly Dictionary<string, string> ByKey = new(StringComparer.Ordinal);
    }

    internal static string GetFormatted(ChaosCardModel card, bool chinese)
    {
        var generated = card.Generated;
        var entry = Entries.GetValue(generated, static _ => new Entry());
        lock (entry)
        {
            var cached = (chinese, card.IsUpgraded) switch
            {
                (true, true) => entry.ChineseUpgraded,
                (true, false) => entry.Chinese,
                (false, true) => entry.EnglishUpgraded,
                _ => entry.English
            };
            if (cached is not null) return cached;
            cached = ChaosTextFormatter.Format(ChaosRuntimeDescriptionRenderer.Render(card, chinese), chinese);
            if (chinese)
            {
                if (card.IsUpgraded) entry.ChineseUpgraded = cached;
                else entry.Chinese = cached;
            }
            else if (card.IsUpgraded) entry.EnglishUpgraded = cached;
            else entry.English = cached;
            return cached;
        }
    }

    /// <summary>
    /// Card layout and compatibility patches can request the same description many times. LocTable.MergeWith mutates
    /// the global table and is substantially more expensive than the cached render, so only repeat it when this key's
    /// active template actually changes (normally when switching between normal and upgraded display). The table is a
    /// weak key so changing language or rebuilding localization naturally starts with an empty installation cache.
    /// </summary>
    internal static void InstallIfChanged(LocTable table, string key, string template)
    {
        InstallAllIfChanged(table, new Dictionary<string, string> { [key] = template });
    }

    /// <summary>Installs a group of generated localization values with one LocTable mutation.</summary>
    internal static void InstallAllIfChanged(LocTable table, IReadOnlyDictionary<string, string> templates)
    {
        var installed = InstalledByTable.GetValue(table, static _ => new InstalledTemplates());
        lock (installed)
        {
            var changed = templates
                .Where(pair => !installed.ByKey.TryGetValue(pair.Key, out var current)
                    || !string.Equals(current, pair.Value, StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (changed.Count == 0) return;

            table.MergeWith(changed);
            foreach (var pair in changed) installed.ByKey[pair.Key] = pair.Value;
        }
    }
}

internal static class ChaosTextFormatter
{
    public static string Format(string text, bool chinese)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        // Derivative/enchantment names are styled before smart upgrade tokens are assembled. Protect those spans
        // from the generic keyword pass (especially derivative names such as Shiv), otherwise markup becomes nested and renders with
        // the wrong color or literal tags.
        var styledTokens = new List<string>();
        var result = System.Text.RegularExpressions.Regex.Replace(text,
            @"\[(?<style>gold|purple)\].*?\[/\k<style>\]", match =>
            {
                styledTokens.Add(match.Value);
                return $"\uE010{(char)(0xE200 + styledTokens.Count - 1)}\uE011";
            });
        var smartTokens = new List<string>();
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\{[^{}\r\n]+\}", match =>
        {
            smartTokens.Add(match.Value);
            return $"\uE000{(char)(0xE100 + smartTokens.Count - 1)}\uE001";
        });
        result = result
            .Replace("Block", "[gold]Block[/gold]", StringComparison.Ordinal)
            .Replace("Vulnerable", "[gold]Vulnerable[/gold]", StringComparison.Ordinal)
            .Replace("Weak", "[gold]Weak[/gold]", StringComparison.Ordinal)
            .Replace("Strength", "[gold]Strength[/gold]", StringComparison.Ordinal)
            .Replace("Exhaust", "[gold]Exhaust[/gold]", StringComparison.Ordinal)
            .Replace("Poison", "[gold]Poison[/gold]", StringComparison.Ordinal)
            .Replace("Dexterity", "[gold]Dexterity[/gold]", StringComparison.Ordinal)
            .Replace("Retain", "[gold]Retain[/gold]", StringComparison.Ordinal)
            .Replace("Sly", "[gold]Sly[/gold]", StringComparison.Ordinal)
            .Replace("Intangible", "[gold]Intangible[/gold]", StringComparison.Ordinal)
            .Replace("Ethereal", "[gold]Ethereal[/gold]", StringComparison.Ordinal)
            .Replace("Shiv", "[gold]Shiv[/gold]", StringComparison.Ordinal)
            .Replace("格挡", "[gold]格挡[/gold]", StringComparison.Ordinal)
            .Replace("易伤", "[gold]易伤[/gold]", StringComparison.Ordinal)
            .Replace("虚弱", "[gold]虚弱[/gold]", StringComparison.Ordinal)
            .Replace("力量", "[gold]力量[/gold]", StringComparison.Ordinal)
            .Replace("消耗", "[gold]消耗[/gold]", StringComparison.Ordinal);
        result = result
            .Replace("中毒", "[gold]中毒[/gold]", StringComparison.Ordinal)
            .Replace("敏捷", "[gold]敏捷[/gold]", StringComparison.Ordinal)
            .Replace("保留", "[gold]保留[/gold]", StringComparison.Ordinal)
            .Replace("奇巧", "[gold]奇巧[/gold]", StringComparison.Ordinal)
            .Replace("无实体", "[gold]无实体[/gold]", StringComparison.Ordinal)
            .Replace("虚无", "[gold]虚无[/gold]", StringComparison.Ordinal)
            .Replace("小刀", "[gold]小刀[/gold]", StringComparison.Ordinal);
        result = System.Text.RegularExpressions.Regex.Replace(result, @"(?<![A-Za-z0-9])\d+(?![A-Za-z0-9])", match => $"[blue]{match.Value}[/blue]");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"(?<![A-Za-z])X(?![A-Za-z])", "[blue]X[/blue]");
        for (var index = 0; index < smartTokens.Count; index++)
            result = result.Replace($"\uE000{(char)(0xE100 + index)}\uE001", smartTokens[index], StringComparison.Ordinal);
        for (var index = 0; index < styledTokens.Count; index++)
            result = result.Replace($"\uE010{(char)(0xE200 + index)}\uE011", styledTokens[index], StringComparison.Ordinal);
        return result;
    }
}

/// <summary>
/// Native derivative references are gold; an enchantment prefix on that derivative is purple. This is applied
/// to the rendered operation rather than persisted localization so snapshots remain language-neutral.
/// </summary>
internal static class ChaosDerivativeTextStyle
{
    internal static string Apply(string text, IReadOnlyList<GeneratorOperation> operations, bool chinese)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var composites = new List<string>();
        foreach (var operation in operations)
        {
            var derivative = DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template);
            var enchantment = DerivativeSlotCatalog.ResolveEnchantment(operation.DerivativeId,
                operation.DerivativeEnchantmentId, operation.Template);
            if (derivative is null || enchantment is null) continue;
            if (chinese)
            {
                var plain = DerivativeEnchantmentCatalog.ChineseCardName(derivative, enchantment);
                text = ReplaceCardName(text, plain,
                    $"[purple]{enchantment.ChineseName}[/purple][gold]{derivative.ChineseName}[/gold]",
                    composites, chinese: true);
            }
            else
            {
                text = ReplaceCardName(text,
                    DerivativeEnchantmentCatalog.EnglishPlural(derivative, enchantment),
                    $"[purple]{enchantment.EnglishName}[/purple] [gold]{derivative.EnglishPlural}[/gold]",
                    composites, chinese: false);
                text = ReplaceCardName(text,
                    DerivativeEnchantmentCatalog.EnglishSingular(derivative, enchantment),
                    $"[purple]{enchantment.EnglishName}[/purple] [gold]{derivative.EnglishSingular}[/gold]",
                    composites, chinese: false);
            }
        }

        // Ancient Fuel is a run-level replacement rather than a separate compact derivative id.
        text = ReplaceCardName(text, chinese ? "先古燃料" : "Ancient Fuel",
            chinese ? "[gold]先古燃料[/gold]" : "[gold]Ancient Fuel[/gold]", composites, chinese);
        foreach (var derivative in DerivativeSlotCatalog.All
                     .OrderByDescending(definition => chinese
                         ? definition.ChineseName.Length
                         : Math.Max(definition.EnglishPlural.Length, definition.EnglishSingular.Length)))
        {
            if (chinese)
            {
                text = ReplaceCardName(text, derivative.ChineseName,
                    $"[gold]{derivative.ChineseName}[/gold]", composites, chinese: true);
                continue;
            }
            text = ReplaceCardName(text, derivative.EnglishPlural,
                $"[gold]{derivative.EnglishPlural}[/gold]", composites, chinese: false);
            if (!string.Equals(derivative.EnglishPlural, derivative.EnglishSingular, StringComparison.Ordinal))
                text = ReplaceCardName(text, derivative.EnglishSingular,
                    $"[gold]{derivative.EnglishSingular}[/gold]", composites, chinese: false);
        }

        for (var index = 0; index < composites.Count; index++)
            text = text.Replace($"\uE020{(char)(0xE300 + index)}\uE021", composites[index],
                StringComparison.Ordinal);
        return text;
    }

    private static string ReplaceCardName(string text, string plain, string styled, IList<string> protectedValues,
        bool chinese)
    {
        if (plain.Length == 0) return text;
        var pattern = chinese
            ? System.Text.RegularExpressions.Regex.Escape(plain) + @"(?<plus>\+)?"
            : @"(?<![A-Za-z])" + System.Text.RegularExpressions.Regex.Escape(plain)
                + @"(?<plus>\+)?(?![A-Za-z])";
        return System.Text.RegularExpressions.Regex.Replace(text, pattern, match =>
        {
            var value = styled;
            if (match.Groups["plus"].Success)
            {
                var closing = value.LastIndexOf("[/gold]", StringComparison.Ordinal);
                value = closing >= 0 ? value.Insert(closing, "+") : value + "+";
            }
            protectedValues.Add(value);
            return $"\uE020{(char)(0xE300 + protectedValues.Count - 1)}\uE021";
        });
    }
}
