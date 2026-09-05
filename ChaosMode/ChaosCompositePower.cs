using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.ValueProps;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace AutoAnthony;

public sealed class ChaosCompositePower : PowerModel
{
    // A component is allowed to emit the same event that owns it (for example, Channel an Orb whenever an Orb is
    // Channeled). The per-Power active set suppresses direct and cross-event re-entry while the originating hook is
    // still resolving. The async-flow depth limit is a final circuit breaker for cycles involving several Powers.
    private const int MaximumNestedTriggerDepth = 64;
    private static readonly AsyncLocal<int> TriggerChainDepth = new();
    private int _slot;
    private GeneratedCharacter _character;
    private string _profileId = string.Empty;
    private bool _sourceUpgraded;
    private bool _permanent;
    private int _attacksPlayedThisTurn;
    private bool _nextAttackReplayAvailable;
    private bool _nextAttackFreeAvailable;
    private bool _nextAttackTriggerAvailable;
    private int _nextAttackTriggersRemaining;
    private bool _ignoreArmingCardPlay;
    private bool _waitForNextTurn;
    private bool _statusDrawnThisTurn;
    private int _remainingTurnTriggers;
    private bool _firstCardReplayAvailable;
    private bool _zeroCostAttackReturnAvailable;
    private int _energySpentTowardRefund;
    private int _starsSpentTowardTrigger;
    private int _cardsDrawnTowardTrigger;
    private int _cardsPlayedTowardTrigger;
    private bool _firstAttackOrSkillAvailable;
    private int _delayedTurns;
    private int _rollingDamage;
    private bool _isDebuff;
    private int _specialXValue;
    private int _resolvedEnergyXValue;
    private int _resolvedStarXValue;
    private int _sourceTargetCombatId = -1;
    private bool _ownerTurnEffectsExpired;
    private bool _defensiveTurnEffectsExpired;
    private int[] _capturedOperationValues = [];
    private HashSet<int> _activeTriggers = [];
    private bool? _cachedDescriptionChinese;
    private bool _cachedDescriptionUpgraded;
    private string? _cachedDescriptionText;

    [SavedProperty] public int Slot { get => _slot; set { AssertMutable(); _slot = value; } }
    [SavedProperty] public GeneratedCharacter Character { get => _character; set { AssertMutable(); _character = value; } }
    [SavedProperty] public string ProfileId { get => _profileId; set { AssertMutable(); _profileId = value ?? string.Empty; } }
    [SavedProperty] public bool SourceUpgraded { get => _sourceUpgraded; set { AssertMutable(); _sourceUpgraded = value; } }
    [SavedProperty] public bool Permanent { get => _permanent; set { AssertMutable(); _permanent = value; } }
    [SavedProperty] public int AttacksPlayedThisTurn { get => _attacksPlayedThisTurn; set { AssertMutable(); _attacksPlayedThisTurn = value; } }
    [SavedProperty] public bool NextAttackReplayAvailable { get => _nextAttackReplayAvailable; set { AssertMutable(); _nextAttackReplayAvailable = value; } }
    [SavedProperty] public bool NextAttackFreeAvailable { get => _nextAttackFreeAvailable; set { AssertMutable(); _nextAttackFreeAvailable = value; } }
    [SavedProperty] public bool NextAttackTriggerAvailable { get => _nextAttackTriggerAvailable; set { AssertMutable(); _nextAttackTriggerAvailable = value; } }
    [SavedProperty] public int NextAttackTriggersRemaining { get => _nextAttackTriggersRemaining; set { AssertMutable(); _nextAttackTriggersRemaining = value; } }
    [SavedProperty] public bool IgnoreArmingCardPlay { get => _ignoreArmingCardPlay; set { AssertMutable(); _ignoreArmingCardPlay = value; } }
    [SavedProperty] public bool WaitForNextTurn { get => _waitForNextTurn; set { AssertMutable(); _waitForNextTurn = value; } }
    [SavedProperty] public bool StatusDrawnThisTurn { get => _statusDrawnThisTurn; set { AssertMutable(); _statusDrawnThisTurn = value; } }
    [SavedProperty] public int RemainingTurnTriggers { get => _remainingTurnTriggers; set { AssertMutable(); _remainingTurnTriggers = value; } }
    [SavedProperty] public bool FirstCardReplayAvailable { get => _firstCardReplayAvailable; set { AssertMutable(); _firstCardReplayAvailable = value; } }
    [SavedProperty] public bool ZeroCostAttackReturnAvailable { get => _zeroCostAttackReturnAvailable; set { AssertMutable(); _zeroCostAttackReturnAvailable = value; } }
    [SavedProperty] public int EnergySpentTowardRefund { get => _energySpentTowardRefund; set { AssertMutable(); _energySpentTowardRefund = value; } }
    [SavedProperty] public int StarsSpentTowardTrigger { get => _starsSpentTowardTrigger; set { AssertMutable(); _starsSpentTowardTrigger = value; } }
    [SavedProperty] public int CardsDrawnTowardTrigger { get => _cardsDrawnTowardTrigger; set { AssertMutable(); _cardsDrawnTowardTrigger = value; } }
    [SavedProperty] public int CardsPlayedTowardTrigger { get => _cardsPlayedTowardTrigger; set { AssertMutable(); _cardsPlayedTowardTrigger = value; } }
    [SavedProperty] public bool FirstAttackOrSkillAvailable { get => _firstAttackOrSkillAvailable; set { AssertMutable(); _firstAttackOrSkillAvailable = value; } }
    [SavedProperty] public int DelayedTurns { get => _delayedTurns; set { AssertMutable(); _delayedTurns = value; } }
    [SavedProperty] public int RollingDamage { get => _rollingDamage; set { AssertMutable(); _rollingDamage = value; } }
    [SavedProperty] public bool IsDebuffState { get => _isDebuff; set { AssertMutable(); _isDebuff = value; } }
    [SavedProperty] public int SpecialXValue { get => _specialXValue; set { AssertMutable(); _specialXValue = value; } }
    [SavedProperty] public int ResolvedEnergyXValue { get => _resolvedEnergyXValue; set { AssertMutable(); _resolvedEnergyXValue = value; } }
    [SavedProperty] public int ResolvedStarXValue { get => _resolvedStarXValue; set { AssertMutable(); _resolvedStarXValue = value; } }
    // SavedProperties and BaseLib both support Int32 but not UInt32. Combat ids are small monotonically assigned
    // values, so store them as a signed value with -1 as the no-target sentinel to keep reconnect/save payloads
    // portable across the vanilla and BaseLib serializers.
    [SavedProperty] public int SourceTargetCombatId { get => _sourceTargetCombatId; set { AssertMutable(); _sourceTargetCombatId = value; } }
    [SavedProperty] public bool OwnerTurnEffectsExpired { get => _ownerTurnEffectsExpired; set { AssertMutable(); _ownerTurnEffectsExpired = value; } }
    [SavedProperty] public bool DefensiveTurnEffectsExpired { get => _defensiveTurnEffectsExpired; set { AssertMutable(); _defensiveTurnEffectsExpired = value; } }
    [SavedProperty] public int[] CapturedOperationValues { get => _capturedOperationValues; set { AssertMutable(); _capturedOperationValues = value ?? []; } }

    /// <summary>
    /// Every gameplay-relevant value not represented by vanilla PowerState.Amount. This compact representation
    /// is appended to multiplayer combat checksums; caches and the active-trigger recursion guard are deliberately
    /// excluded because they cannot alter a state once the current hook has completed.
    /// </summary>
    internal int[] CaptureMultiplayerState()
    {
        static int Flag(bool value) => value ? 1 : 0;
        var state = new List<int>(32 + _capturedOperationValues.Length)
        {
            4, _slot, (int)_character, StableProfileHash(_profileId), Flag(_sourceUpgraded), Flag(_permanent), _attacksPlayedThisTurn,
            Flag(_nextAttackReplayAvailable), Flag(_nextAttackFreeAvailable), Flag(_nextAttackTriggerAvailable),
            _nextAttackTriggersRemaining,
            Flag(_ignoreArmingCardPlay), Flag(_waitForNextTurn), Flag(_statusDrawnThisTurn),
            _remainingTurnTriggers, Flag(_firstCardReplayAvailable), Flag(_zeroCostAttackReturnAvailable),
            _energySpentTowardRefund, _starsSpentTowardTrigger, _cardsDrawnTowardTrigger,
            _cardsPlayedTowardTrigger, Flag(_firstAttackOrSkillAvailable), _delayedTurns, _rollingDamage,
            Flag(_isDebuff), _specialXValue, _resolvedEnergyXValue, _resolvedStarXValue,
            Flag(_ownerTurnEffectsExpired), Flag(_defensiveTurnEffectsExpired), _sourceTargetCombatId,
            _capturedOperationValues.Length
        };
        state.AddRange(_capturedOperationValues);
        return state.ToArray();
    }

    private static int StableProfileHash(string value)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var character in value)
                hash = (hash ^ character) * 16777619;
            return hash;
        }
    }

    public override PowerType Type => _isDebuff ? PowerType.Debuff : PowerType.Buff;
    public override PowerStackType StackType => PowerStackType.Single;
    public override PowerInstanceType InstanceType => PowerInstanceType.Instanced;
    public ChaosCardDefinition Definition => string.IsNullOrEmpty(ProfileId)
        ? ChaosRunDefinitions.ForSlot(Character, Slot)
        : ExternalComponentCharacterApi.ForSlot(ProfileId, Slot);

    public override LocString Description
    {
        get
        {
            var description = new LocString("powers", Id.Entry + ".description");
            LocManager.Instance.GetTable("powers").MergeWith(new Dictionary<string, string>
            {
                [Id.Entry + ".description"] = GetDescriptionText()
            });
            description.Add("energyPrefix", string.IsNullOrEmpty(ProfileId)
                ? Character.ToString().ToLowerInvariant()
                : ExternalComponentCharacterApi.EnergyIconPrefix(ProfileId));
            // PowerModel normally supplies this through its dumb-hover path. Our
            // generated description is also materialized directly by startup audits
            // and a few UI call sites, so keep the built-in Star token self-contained.
            description.Add("singleStarIcon", "[img]res://images/packed/sprite_fonts/star_icon.png[/img]");
            return description;
        }
    }

    protected override void AfterCloned()
    {
        base.AfterCloned();
        _activeTriggers = [];
        _cachedDescriptionChinese = null;
        _cachedDescriptionText = null;
    }

    public void Configure(int slot, bool upgraded, bool permanent) => Configure(GeneratedCharacter.Ironclad, slot, upgraded, permanent);

    public void Configure(GeneratedCharacter character, int slot, bool upgraded, bool permanent, int specialXValue = 0,
        int resolvedEnergyXValue = 0, int resolvedStarXValue = 0, int[]? capturedOperationValues = null,
        uint? sourceTargetCombatId = null, string profileId = "")
    {
        Character = character;
        ProfileId = profileId;
        Slot = slot;
        SourceUpgraded = upgraded;
        Permanent = permanent;
        SpecialXValue = specialXValue;
        ResolvedEnergyXValue = Math.Max(0, resolvedEnergyXValue);
        ResolvedStarXValue = Math.Max(0, resolvedStarXValue);
        SourceTargetCombatId = sourceTargetCombatId is { } targetId ? checked((int)targetId) : -1;
        OwnerTurnEffectsExpired = false;
        DefensiveTurnEffectsExpired = false;
        CapturedOperationValues = capturedOperationValues?.ToArray() ?? [];
        _cachedDescriptionChinese = null;
        _cachedDescriptionText = null;
        var powerOperations = DescriptionOperations(EffectivePowerOperations());
        NextAttackReplayAvailable = powerOperations.Any(operation => operation.Template == "I:ReplayAttack");
        NextAttackFreeAvailable = powerOperations.Any(operation => operation.Template == "I:SetCostZero");
        var nextAttackTrigger = powerOperations.FirstOrDefault(CardEffectRules.IsNextAttackGrantTrigger);
        NextAttackTriggersRemaining = nextAttackTrigger is null ? 0 : CardEffectRules.NextAttackGrantCount(nextAttackTrigger);
        NextAttackTriggerAvailable = NextAttackTriggersRemaining > 0;
        // The power is installed during its source card's OnPlay. Its first AfterCardPlayed callback therefore
        // belongs to the card that armed it and must not satisfy any of the newly installed play triggers.
        IgnoreArmingCardPlay = true;
        _waitForNextTurn = powerOperations.Any(operation => operation.Template is "NCR:NextTurn" or "R:NextTurn" or "CL:AtNextTurnStart");
        var limitedTurnIndex = Definition.Card.Operations.ToList()
            .FindIndex(operation => operation.Template == "D:NextTurnsStart");
        _remainingTurnTriggers = limitedTurnIndex < 0 ? 0 : EffectiveOperationAmount(limitedTurnIndex, 1);
        _firstCardReplayAvailable = HasTriggerWithLinkedEffect(powerOperations,
            "first_card_played_each_turn", "D:ReplayEventCard");
        // This trigger was originally introduced for ReturnEventCardToHand, but its payoff is now composable.
        // Arming it only for the native payoff made other legal combinations (for example status -> Fuel) inert.
        _zeroCostAttackReturnAvailable = powerOperations.Any(operation =>
            TriggerKind(operation) == "first_zero_cost_attack_played_each_turn");
        _firstAttackOrSkillAvailable = powerOperations.Any(operation => operation.Template == "CL:FirstAttackOrSkillEachTurn");
        var delayed = powerOperations.ToList().FindIndex(operation => operation.Template == "CL:AfterTurns");
        _delayedTurns = delayed < 0 ? 0 : Math.Max(1, EffectiveOperationAmount(delayed, 3));
        var rollingIncrease = powerOperations.ToList().FindIndex(operation =>
            operation.Template == "CL:IncreaseRollingDamage");
        var rollingOwner = rollingIncrease < 0
            ? -1
            : powerOperations[rollingIncrease].Parameters.GetValueOrDefault("triggerIndex", -1);
        var rolling = powerOperations.ToList().FindIndex(operation =>
            operation.Template is "N:AllD" or "CL:RollingAllDamage"
            && operation.Parameters.GetValueOrDefault("triggerIndex", -1) == rollingOwner);
        _rollingDamage = rolling < 0 ? 0 : Math.Max(0, EffectiveOperationAmount(rolling, 5));
        _isDebuff = powerOperations.Any(operation => operation.Template == "CL:DieOnUnblockedAttack");
    }

    private string GetDescriptionText()
    {
        var chinese = LocManager.Instance?.Language is "zhs" or "zht";
        if (_cachedDescriptionText is not null && _cachedDescriptionChinese == chinese
            && _cachedDescriptionUpgraded == SourceUpgraded)
            return _cachedDescriptionText;
        var card = Definition.Card;
        var operations = EffectivePowerOperations();
        var descriptionOperations = DescriptionOperations(operations);
        var raw = chinese
            ? CardDescriptionRenderer.Render(descriptionOperations)
            : EnglishCardDescriptionRenderer.Render(descriptionOperations);
        if (descriptionOperations.Any(operation =>
                DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template)?.Id == "fuel"))
            raw = ChaosRunDefinitions.ReplaceFuelDisplay(raw, chinese);
        raw = ChaosDerivativeTextStyle.Apply(raw, descriptionOperations, chinese);
        raw = ResolvePrintedX(raw, DescriptionXValue(card));
        raw = System.Text.RegularExpressions.Regex.Replace(raw,
            chinese ? @"获得(\d+)点能量" : @"Gain (\d+) Energy",
            match => chinese
                ? $"获得{{energyPrefix:energyIcons({match.Groups[1].Value})}}"
                : $"Gain {{energyPrefix:energyIcons({match.Groups[1].Value})}}");
        raw = System.Text.RegularExpressions.Regex.Replace(raw,
            chinese ? @"(花费)(\d+)点能量" : @"(Every )(\d+) Energy",
            match => chinese
                ? $"{match.Groups[1].Value}{{energyPrefix:energyIcons({match.Groups[2].Value})}}"
                : $"{match.Groups[1].Value}{{energyPrefix:energyIcons({match.Groups[2].Value})}}");
        raw = System.Text.RegularExpressions.Regex.Replace(raw,
            chinese ? @"获得(\d+)颗蓝星" : @"Gain (\d+) Stars?",
            match =>
            {
                var amount = int.Parse(match.Groups[1].Value);
                return amount > 5
                    ? (chinese ? $"获得{amount}{{singleStarIcon}}" : $"Gain {amount} {{singleStarIcon}}")
                    : (chinese ? "获得" : "Gain ")
                        + string.Concat(Enumerable.Repeat("{singleStarIcon}", amount));
            });
        _cachedDescriptionChinese = chinese;
        _cachedDescriptionUpgraded = SourceUpgraded;
        return _cachedDescriptionText = ChaosTextFormatter.Format(raw, chinese);
    }

    private IReadOnlyList<GeneratorOperation> EffectivePowerOperations()
    {
        var card = Definition.Card;
        IReadOnlyList<GeneratorOperation> operations = card.Operations;
        if (SourceUpgraded && card.Upgrade is { } upgrade)
        {
            // Rebuild instead of trusting the serialized upgraded text. This also migrates old snapshots whose
            // generic upgrader changed a selector marker such as “0-cost” into “1-cost”.
            operations = CardUpgradeGenerator.ApplyEffectsToOperations(card.Operations, upgrade.Effects);
        }
        if (CapturedOperationValues.Length < 2) return operations;

        var effective = operations.ToArray();
        for (var offset = 0; offset + 1 < CapturedOperationValues.Length; offset += 2)
        {
            var index = CapturedOperationValues[offset];
            if ((uint)index >= (uint)effective.Length) continue;
            var operation = effective[index];
            effective[index] = ChaosOperationVariables.ReplaceInitialValue(operation,
                CapturedOperationValues[offset + 1]);
        }
        return effective;
    }

    private int DescriptionXValue(GeneratedCard card)
    {
        var special = card.Operations.FirstOrDefault(SpecialXCardConverter.IsSpecial);
        if (special is not null)
            return ChaosCardModel.ChaosXValueMultiplier.Apply(card,
                SpecialXCardConverter.Resource(special) == SpecialXCardConverter.StarResource
                ? ResolvedStarXValue
                : ResolvedEnergyXValue);
        return ChaosCardModel.ChaosXValueMultiplier.Apply(card,
            card.HasStarCostX ? ResolvedStarXValue : ResolvedEnergyXValue);
    }

    internal static string ResolvePrintedX(string text, int resolvedX)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"(?<![A-Za-z0-9_])X\+(\d+)(?![A-Za-z0-9_])",
            match => (Math.Max(0, resolvedX) + int.Parse(match.Groups[1].Value)).ToString());
        return System.Text.RegularExpressions.Regex.Replace(text,
            @"(?<![A-Za-z0-9_])X(?![A-Za-z0-9_])", Math.Max(0, resolvedX).ToString());
    }

    internal static IReadOnlyList<GeneratorOperation> DescriptionOperations(IReadOnlyList<GeneratorOperation> operations)
    {
        var included = new HashSet<int>();
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Scope is OperationScope.AbilityTrigger or OperationScope.AbilityRule
                || IsPowerConditionalTrigger(operation))
                included.Add(index);
        }

        // Effects explicitly owned by a persistent trigger are the core of the generated Power. Follow links as
        // a closure so a future decomposed component can safely contain another piece of interpreter metadata.
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var index = 0; index < operations.Count; index++)
            {
                if (included.Contains(index)
                    || !operations[index].Parameters.TryGetValue("triggerIndex", out var owner)
                    || !included.Contains(owner)) continue;
                included.Add(index);
                changed = true;
            }
        }

        // Unowned modifiers are evaluated globally by the interpreter. Retain them only when the Power contains a
        // compatible payoff; this keeps a modifier that really changes triggered damage/Block without leaking an
        // unrelated modifier belonging solely to an instantaneous card effect into the Power description.
        var linkedEffects = included.Select(index => operations[index]).ToArray();
        var hasDamage = linkedEffects.Any(CardEffectRules.IsEnemyDamage);
        var hasBlock = linkedEffects.Any(IsBlockEffect);
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Scope != OperationScope.Modifier || operation.Parameters.ContainsKey("triggerIndex")) continue;
            if (hasDamage && !IsBlockOnlyModifier(operation) || hasBlock && IsBlockOnlyModifier(operation))
                included.Add(index);
        }

        // Dependency prefixes are executable parts of their payoff even when an old snapshot did not serialize an
        // explicit owner link. Include the immediately preceding prefix whenever its linked payoff is included.
        foreach (var payoffIndex in included.OrderBy(index => index).ToArray())
        {
            if (payoffIndex <= 0) continue;
            var prefix = operations[payoffIndex - 1];
            if (CardEffectRules.IsDependencyPrefix(prefix)
                && CardEffectRules.IsLegalDependencyPayoff(prefix, operations[payoffIndex]))
                included.Add(payoffIndex - 1);
        }

        var ordered = included.OrderBy(index => index).ToArray();
        var remap = ordered.Select((oldIndex, newIndex) => (oldIndex, newIndex))
            .ToDictionary(pair => pair.oldIndex, pair => pair.newIndex);
        var result = new List<GeneratorOperation>(ordered.Length);
        foreach (var oldIndex in ordered)
        {
            var operation = operations[oldIndex];
            if (operation.Parameters.TryGetValue("triggerIndex", out var oldTrigger)
                && remap.TryGetValue(oldTrigger, out var newTrigger))
            {
                var parameters = operation.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value,
                    StringComparer.Ordinal);
                parameters["triggerIndex"] = newTrigger;
                operation = operation with { Parameters = parameters };
            }
            result.Add(operation);
        }
        return result;
    }

    private static bool IsPowerConditionalTrigger(GeneratorOperation operation) =>
        operation.Scope == OperationScope.ConditionalTrigger
        && operation.Template is "C:untilTurnEnd" or "C:untilTurnEndCardDrawn" or "C:for"
            or "C:grantNextAttacksThisTurn" or "C:grantNextAttack"
            or "NCR:NextTurn" or "R:NextTurn" or "D:NextTurnsStart" or "CL:AtNextTurnStart";

    private static bool IsBlockEffect(GeneratorOperation operation) => operation.Template is
        "N:B" or "N_BLOCK" or "N:BlockEqualAllPoison" or "CL:GainBlockEqualDamage"
        or "CL:GainBlockEqualCurrent" or "CL:GainNextTurnBlockEqualCurrent";

    private static bool IsBlockOnlyModifier(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.RequireStructured(operation).Opcode == "modify_block";

    public override Task AfterCardEnteredCombat(CardModel card)
    {
        if (HasRule("derivative_retain") && card.Owner == Owner.Player
            && card.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shiv))
            CardCmd.ApplyKeyword(card, CardKeyword.Retain);
        return Task.CompletedTask;
    }

    public override Task AfterApplied(Creature? applier, CardModel? cardSource)
    {
        var playerCombatState = Owner.Player?.PlayerCombatState;
        if (HasRule("derivative_retain") && playerCombatState is not null)
            foreach (var card in playerCombatState.AllCards.Where(card =>
                         card.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shiv)))
                CardCmd.ApplyKeyword(card, CardKeyword.Retain);
        return Task.CompletedTask;
    }

    public override async Task AfterSideTurnStart(CombatSide side, IReadOnlyList<Creature> participants, ICombatState combatState)
    {
        if (Owner.Side != side || !participants.Contains(Owner)) return;
        _attacksPlayedThisTurn = 0;
        _statusDrawnThisTurn = false;
        _cardsPlayedTowardTrigger = 0;
        _firstAttackOrSkillAvailable = DescriptionOperations(Definition.Card.Operations)
            .Any(operation => operation.Template == "CL:FirstAttackOrSkillEachTurn");
        var effectiveOperations = EffectivePowerOperations();
        _firstCardReplayAvailable = HasTriggerWithLinkedEffect(effectiveOperations,
            "first_card_played_each_turn", "D:ReplayEventCard");
        _zeroCostAttackReturnAvailable = effectiveOperations.Any(operation =>
            TriggerKind(operation) == "first_zero_cost_attack_played_each_turn");
        if (!Permanent && _remainingTurnTriggers > 0)
        {
            // Player-choice hooks run earlier than AfterSideTurnStart in v111. Leave choice-capable delayed
            // triggers to AfterPlayerTurnStart; resolving one here with ThrowingPlayerChoiceContext can leave the
            // turn setup task incomplete and the player's phase permanently stuck at Start.
            if (StartTriggerNeedsPlayerChoice("next_turns_start")) return;
            await ResolveRemainingTurnStart(new ThrowingPlayerChoiceContext());
            return;
        }
        if (_waitForNextTurn)
        {
            if (StartTriggerNeedsPlayerChoice("next_turn_start")) return;
            await ResolveNextTurnStart(new ThrowingPlayerChoiceContext());
            return;
        }
        if (Permanent && !StartTriggerNeedsPlayerChoice("turn_start", "turn_start_if_self_in_exhaust"))
            await FireTriggersAny(["turn_start", "turn_start_if_self_in_exhaust"],
                new ThrowingPlayerChoiceContext());
    }

    public override async Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        if (player.Creature != Owner) return;
        if (!Permanent && _remainingTurnTriggers > 0 && StartTriggerNeedsPlayerChoice("next_turns_start"))
        {
            await ResolveRemainingTurnStart(choiceContext);
            return;
        }
        if (_waitForNextTurn && StartTriggerNeedsPlayerChoice("next_turn_start"))
        {
            await ResolveNextTurnStart(choiceContext);
            return;
        }
        if (Permanent && StartTriggerNeedsPlayerChoice("turn_start", "turn_start_if_self_in_exhaust"))
            await FireTriggersAny(["turn_start", "turn_start_if_self_in_exhaust"], choiceContext);
    }

    private async Task ResolveRemainingTurnStart(PlayerChoiceContext choiceContext)
    {
        await FireTriggers("next_turns_start", choiceContext);
        _remainingTurnTriggers--;
        if (_remainingTurnTriggers <= 0) await PowerCmd.Remove(this);
    }

    private async Task ResolveNextTurnStart(PlayerChoiceContext choiceContext)
    {
        _waitForNextTurn = false;
        await FireTriggers("next_turn_start", choiceContext);
        await PowerCmd.Remove(this);
    }

    public override async Task AfterSideTurnEnd(PlayerChoiceContext choiceContext, CombatSide side, IEnumerable<Creature> participants)
    {
        if (Permanent)
        {
            // A composite Power may contain both a combat-long engine and a rider that explicitly lasts only for
            // the turn in which the card was played. Expiration belongs to each trigger, not to the Power as a
            // whole; otherwise the permanent engine accidentally keeps the temporary rider alive every turn.
            if (Owner.Side == side && participants.Contains(Owner)) ExpireOwnerTurnEffects();
            else if (Owner.Side != side) DefensiveTurnEffectsExpired = true;
            return;
        }
        if (_waitForNextTurn) return;
        // Lightning Rod-style effects have a fixed number of future turn-start triggers. They must survive
        // the turn in which the card was played; AfterSideTurnStart removes them after the final trigger.
        if (_remainingTurnTriggers > 0) return;
        // Unrelenting waits across turns for the next Attack, while One-Two Punch explicitly expires at the
        // end of this turn. They share the same event hook but remain distinct operation templates.
        if (NextAttackTriggerAvailable && HasCrossTurnNextAttackTrigger()) return;
        var defensiveUntilOpponentTurnEnds = HasTrigger("attack_received")
            || HasTrigger("vulnerable_enemy_damage_reduction");
        if (defensiveUntilOpponentTurnEnds)
        {
            // Flame Barrier-style effects must survive the player's turn end so they can react to enemy attacks.
            if (Owner.Side == side) return;
            await PowerCmd.Remove(this);
            return;
        }
        if (Owner.Side == side && participants.Contains(Owner))
            await PowerCmd.Remove(this);
    }

    public override async Task BeforeSideTurnEnd(PlayerChoiceContext choiceContext, CombatSide side, IEnumerable<Creature> participants)
    {
        if (_delayedTurns <= 0 || Owner.Side != side || !participants.Contains(Owner)) return;
        _delayedTurns--;
        if (_delayedTurns > 0) return;
        await FireTriggers("turns_elapsed", choiceContext);
        await PowerCmd.Remove(this);
    }

    public override async Task AfterCardExhausted(PlayerChoiceContext choiceContext, CardModel card, bool causedByEthereal)
    {
        if (card.Owner != Owner.Player) return;
        await FireTriggers("card_exhausted", choiceContext, eventCard: card);
    }

    public override async Task AfterCardDrawn(PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw)
    {
        if (card.Owner != Owner.Player) return;
        var cardsTrigger = Definition.Card.Operations.ToList().FindIndex(operation => operation.Template == "CL:EveryCardsDrawn");
        if (cardsTrigger >= 0)
        {
            var threshold = Math.Max(1, EffectiveOperationAmount(cardsTrigger, 10));
            _cardsDrawnTowardTrigger++;
            while (_cardsDrawnTowardTrigger >= threshold)
            {
                _cardsDrawnTowardTrigger -= threshold;
                await FireTriggerAt(cardsTrigger, choiceContext, eventCard: card);
            }
        }
        // The base game exposes Strike as a semantic card tag. Title matching made this trigger language-dependent
        // and caused different clients to disagree when their localization differed.
        if (card.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Strike))
            await FireTriggers("strike_card_drawn", choiceContext, eventCard: card);
        if (!fromHandDraw && card.Owner.Creature.CombatState?.CurrentSide == card.Owner.Creature.Side)
            await FireTriggers("card_drawn_during_turn", choiceContext, eventCard: card);
        await FireTriggers("card_drawn", choiceContext, eventCard: card);
        if (card.Keywords.Contains(CardKeyword.Ethereal))
            await FireTriggers("ethereal_card_drawn", choiceContext, eventCard: card);
        if (card.Type == CardType.Status && !_statusDrawnThisTurn)
        {
            _statusDrawnThisTurn = true;
            await FireTriggers("first_status_drawn_each_turn", choiceContext, eventCard: card);
        }
    }

    public override async Task AfterCardPlayed(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        if (cardPlay.Card.Owner != Owner.Player) return;
        if (IgnoreArmingCardPlay)
        {
            IgnoreArmingCardPlay = false;
            if (cardPlay.Card.Id == ChaosCardRegistry.Canonical(Character, Slot).Id)
                return;
        }
        // Unlike Echo Form's linked replay payoff, the first-card trigger itself is composable.  It must be
        // dispatched for every legal linked effect (draw, Block, channel, and so on), not only when the trigger
        // happens to own D:ReplayEventCard.  Capture the state before firing any other play triggers because those
        // payoffs may autoplay additional cards and would otherwise change the combat history underneath us.
        var isFirstCardPlayedThisTurn = IsFirstCardPlayThisTurn(
            CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
                entry.CardPlay.Player.Creature == Owner && entry.HappenedThisTurn(CombatState)));
        var cardsTrigger = Definition.Card.Operations.ToList().FindIndex(operation => operation.Template == "CL:EveryCardsPlayedThisTurn");
        if (cardsTrigger >= 0 && cardPlay.Card.Id != ChaosCardRegistry.Canonical(Character, Slot).Id)
        {
            var threshold = Math.Max(1, EffectiveOperationAmount(cardsTrigger, 5));
            _cardsPlayedTowardTrigger++;
            while (_cardsPlayedTowardTrigger >= threshold)
            {
                _cardsPlayedTowardTrigger -= threshold;
                await FireTriggerAt(cardsTrigger, choiceContext, cardPlay, cardPlay.Card);
            }
        }
        await FireTriggers("card_played", choiceContext, sourcePlay: cardPlay, eventCard: cardPlay.Card);
        if (isFirstCardPlayedThisTurn)
            await FireTriggers("first_card_played_each_turn", choiceContext,
                sourcePlay: cardPlay, eventCard: cardPlay.Card);
        if (cardPlay.Card.Type == CardType.Power)
            await FireTriggers("power_played", choiceContext, sourcePlay: cardPlay, eventCard: cardPlay.Card);
        var derivativeTriggers = Definition.Card.Operations;
        for (var index = 0; index < derivativeTriggers.Count; index++)
            if (derivativeTriggers[index].Template == "A:whenSoulPlayed"
                && ChaosDerivativeResolver.Matches(cardPlay.Card, derivativeTriggers[index]))
                await FireTriggerAt(index, choiceContext, cardPlay, cardPlay.Card);
        if (cardPlay.Card.Keywords.Contains(CardKeyword.Ethereal))
            await FireTriggers("ethereal_card_played", choiceContext, sourcePlay: cardPlay, eventCard: cardPlay.Card);
        if (cardPlay.Resources.StarsSpent > 0)
            await FireTriggers("stars_spent_or_gained", choiceContext, sourcePlay: cardPlay, eventCard: cardPlay.Card);
        if (cardPlay.Card.Type == CardType.Attack)
        {
            _attacksPlayedThisTurn = CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
                entry.CardPlay.Card.Type == CardType.Attack && entry.CardPlay.Player.Creature == Owner && entry.HappenedThisTurn(CombatState));
            await FireTriggers("attack_played", choiceContext, sourcePlay: cardPlay, eventCard: cardPlay.Card);
            if (_attacksPlayedThisTurn == 1)
                await FireTriggers("first_attack_played_each_turn", choiceContext,
                    sourcePlay: cardPlay, eventCard: cardPlay.Card);
            var operations = Definition.Card.Operations;
            for (var triggerIndex = 0; triggerIndex < operations.Count; triggerIndex++)
            {
                var trigger = operations[triggerIndex];
                if (!IsNthAttackPlayedThisTurnTrigger(trigger)
                    || _attacksPlayedThisTurn != NthAttackPlayedThisTurnThreshold(trigger)) continue;
                await FireTriggerAt(triggerIndex, choiceContext, cardPlay, cardPlay.Card);
            }
            if (NextAttackTriggerAvailable && NextAttackTriggersRemaining > 0)
            {
                var nextAttackIndex = Definition.Card.Operations.ToList()
                    .FindIndex(CardEffectRules.IsNextAttackGrantTrigger);
                if (nextAttackIndex >= 0)
                    await FireTriggerAt(nextAttackIndex, choiceContext, cardPlay, cardPlay.Card);
                NextAttackTriggersRemaining = Math.Max(0, NextAttackTriggersRemaining - 1);
                NextAttackTriggerAvailable = NextAttackTriggersRemaining > 0;
                if (!NextAttackTriggerAvailable)
                {
                    NextAttackReplayAvailable = false;
                    NextAttackFreeAvailable = false;
                }
            }
        }
        if (cardPlay.Card.Type == CardType.Skill)
        {
            await FireTriggers("skill_played", choiceContext, cardPlay, cardPlay.Card);
        }
        if (_firstAttackOrSkillAvailable && cardPlay.Card.Type is CardType.Attack or CardType.Skill)
        {
            _firstAttackOrSkillAvailable = false;
            await FireTriggers("first_attack_or_skill_each_turn", choiceContext, cardPlay, cardPlay.Card);
        }
        if (_zeroCostAttackReturnAvailable && cardPlay.Card.Type == CardType.Attack
            && cardPlay.Card.EnergyCost.GetResolved() == 0)
        {
            _zeroCostAttackReturnAvailable = false;
            await FireTriggers("first_zero_cost_attack_played_each_turn", choiceContext,
                sourcePlay: cardPlay, eventCard: cardPlay.Card);
        }
    }

    public override async Task AfterAutoPostPlayPhaseEntered(PlayerChoiceContext choiceContext, MegaCrit.Sts2.Core.Entities.Players.Player player)
    {
        if (player == Owner.Player)
            await FireTriggersAny(["turn_end", "turn_end_if_self_in_exhaust"], choiceContext);
    }

    public override async Task AfterBlockGained(Creature creature, decimal amount, ValueProp props, CardModel? cardSource)
    {
        if (creature == Owner) await FireTriggers("block_gained", new ThrowingPlayerChoiceContext());
    }

    public override async Task AfterCurrentHpChanged(Creature creature, decimal delta)
    {
        if (creature == Owner.Player?.Osty && delta < 0)
            await FireTriggers("osty_hp_lost", new ThrowingPlayerChoiceContext(), eventAmount: -delta);
        // This trigger explicitly says "during your turn".  Damage received while enemies or another multiplayer
        // side is acting must not resolve it, even though the same Power remains subscribed to the HP hook.
        if (ShouldFireOwnerTurnHpLoss(creature == Owner, delta,
                Owner.CombatState?.CurrentSide == Owner.Side))
        {
            await FireTriggers("owner_hp_lost_during_turn", new ThrowingPlayerChoiceContext());
        }
    }

    public override async Task AfterPowerAmountChanged(PlayerChoiceContext choiceContext, PowerModel power, decimal amount, Creature? applier, CardModel? cardSource)
    {
        if (amount > 0 && applier == Owner && power is VulnerablePower)
            await FireTriggers("vulnerable_applied", choiceContext, eventCreature: power.Owner);
        if (amount != 0 && applier == Owner && power is DoomPower)
            await FireTriggers("doom_applied", choiceContext, eventCreature: power.Owner);
        if (amount != 0 && applier == Owner && power.Owner.IsEnemy
            && power.GetTypeForAmount(amount) == PowerType.Debuff && power is not ITemporaryPower)
            await FireTriggers("enemy_debuff_applied", choiceContext, eventCreature: power.Owner);
    }

    public override async Task AfterCardGeneratedForCombat(CardModel card, Player? creator)
    {
        if (creator?.Creature != Owner) return;
        await FireTriggers("card_generated", new ThrowingPlayerChoiceContext(), eventCard: card);
        if (card.Type == CardType.Status)
            await FireTriggers("status_generated", new ThrowingPlayerChoiceContext(), eventCard: card);
    }

    public override async Task AfterOrbChanneled(PlayerChoiceContext choiceContext, Player player, OrbModel orb)
    {
        if (player.Creature != Owner) return;
        await FireTriggers("orb_channeled", choiceContext);
    }

    public override async Task AfterOrbEvoked(PlayerChoiceContext choiceContext, OrbModel orb, IEnumerable<Creature> targets)
    {
        if (orb.Owner != Owner.Player) return;
        var operations = Definition.Card.Operations;
        for (var triggerIndex = 0; triggerIndex < operations.Count; triggerIndex++)
        {
            var trigger = operations[triggerIndex];
            if (trigger.Template != "A:whenLightningEvoked"
                || !ChaosOrbResolver.MatchesSource(orb, trigger)) continue;
            foreach (var target in targets.Where(target => target.IsAlive))
                await FireTriggerAt(triggerIndex, choiceContext, eventCreature: target);
        }
    }

    public override async Task AfterStarsSpent(int amount, Player spender)
    {
        if (amount <= 0 || spender.Creature != Owner) return;
        var operations = Definition.Card.Operations;
        var triggerIndex = operations.ToList().FindIndex(operation => operation.Template == "A:whenOneStarSpent");
        if (triggerIndex < 0) return;
        var threshold = Math.Max(1, EffectiveOperationAmount(triggerIndex, 1));
        StarsSpentTowardTrigger += amount;
        var triggerCount = StarsSpentTowardTrigger / threshold;
        StarsSpentTowardTrigger %= threshold;
        for (var i = 0; i < triggerCount; i++)
            await FireTriggerAt(triggerIndex, new ThrowingPlayerChoiceContext());
    }

    public override async Task AfterEnergySpent(CardModel card, int amount)
    {
        if (amount <= 0 || card.Owner.Creature != Owner) return;
        var operations = Definition.Card.Operations;
        var triggerIndex = operations.ToList().FindIndex(operation => operation.Template == "A:whenEnergySpent");
        if (triggerIndex >= 0)
        {
            var triggerThreshold = Math.Max(1, EffectiveOperationAmount(triggerIndex, 4));
            EnergySpentTowardRefund += amount;
            var triggerCount = EnergySpentTowardRefund / triggerThreshold;
            EnergySpentTowardRefund %= triggerThreshold;
            for (var i = 0; i < triggerCount; i++)
                await FireTriggerAt(triggerIndex, new ThrowingPlayerChoiceContext(), sourcePlay: null, eventCard: card);
            return;
        }
        // Retain the old indivisible operation for v0.1.68-and-earlier run snapshots.
        var orbitIndex = operations.ToList().FindIndex(operation => operation.Template == "A:ProxyAtomic_Orbit");
        if (orbitIndex < 0) return;
        var threshold = Math.Max(1, EffectiveOperationAmount(orbitIndex, 4));
        EnergySpentTowardRefund += amount;
        var refunds = EnergySpentTowardRefund / threshold;
        EnergySpentTowardRefund %= threshold;
        if (refunds <= 0) return;
        Flash();
        await PlayerCmd.GainEnergy(refunds, Owner.Player!);
    }

    public override async Task AfterStarsGained(int amount, Player gainer)
    {
        if (amount > 0 && gainer.Creature == Owner)
            await FireTriggers("stars_spent_or_gained", new ThrowingPlayerChoiceContext());
    }

    public override async Task AfterDamageGiven(PlayerChoiceContext choiceContext, Creature? dealer, DamageResult result,
        ValueProp props, Creature target, CardModel? cardSource)
    {
        if (dealer == Owner && target.IsEnemy && props.IsPoweredAttack() && cardSource?.Type == CardType.Attack)
        {
            await FireTriggers("attack_damaged_enemy", choiceContext, eventCreature: target);
            await FireTriggers("attack_dealt_damage", choiceContext, eventCreature: target,
                eventAmount: result.UnblockedDamage);
        }
    }

    public override bool ShouldClearBlock(Creature creature)
    {
        return creature != Owner || !HasRule("retain_block_between_turns");
    }

    public override bool ShouldFlush(Player player)
    {
        // Keep the generated rule self-contained instead of relying on WellLaidPlansPower's
        // version-specific implementation. Only this Power's owner keeps their entire hand.
        return player != Owner.Player || !HasRule("retain_hand_at_turn_end");
    }

    public override decimal ModifyBlockMultiplicative(Creature target, decimal block, ValueProp props, CardModel? cardSource, CardPlay? cardPlay)
    {
        if (target != Owner || cardSource is null || !HasRule("first_card_block_doubled_each_turn")) return 1m;
        var previous = CombatManager.Instance.History.Entries.OfType<BlockGainedEntry>().Count(entry =>
            entry.HappenedThisTurn(CombatState) && entry.CardPlay is not null && entry.CardPlay.Player.Creature == Owner
            && entry.Props.IsCardOrMonsterMove() && entry.CardPlay != cardPlay);
        if (previous > 0) return 1m;
        return 2m;
    }

    public override decimal ModifyDamageMultiplicative(Creature? target, decimal amount, ValueProp props, Creature? dealer, CardModel? cardSource, CardPlay? cardPlay)
    {
        decimal multiplier = 1m;
        if (dealer == Owner && props.IsPoweredAttack() && cardSource?.Type == CardType.Attack)
        {
            var operations = EffectivePowerOperations();
            for (var index = 0; index < operations.Count; index++)
            {
                var modifier = operations[index];
                if (modifier.Template != "M:TriggeredAttackDamagePercent"
                    || !modifier.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                    || triggerIndex < 0 || triggerIndex >= index
                    || !TriggeredAttackDamageModifierApplies(operations[triggerIndex], triggerIndex, cardSource))
                    continue;
                multiplier *= 1m + Math.Max(0, EffectiveOperationAmount(index, 50)) / 100m;
            }
        }
        if (HasRule("vulnerable_enemy_damage_bonus") && dealer == Owner
            && (target?.GetPower<VulnerablePower>()?.Amount ?? 0) > 0)
            multiplier *= 1m + RuleAmount("vulnerable_enemy_damage_bonus") / 100m;
        if (HasRule("weak_enemy_attack_damage_bonus") && props.IsPoweredAttack() && cardSource is not null
            && (dealer == Owner || dealer is not null && Owner.Pets.Contains<Creature>(dealer))
            && (target?.GetPower<WeakPower>()?.Amount ?? 0) > 0)
            multiplier *= 1m + RuleAmount("weak_enemy_attack_damage_bonus") / 100m;
        var reductionIndex = FindTrigger("vulnerable_enemy_damage_reduction");
        if (reductionIndex >= 0 && target == Owner && props.IsPoweredAttack()
            && !TurnLimitedTriggerExpired(Definition.Card.Operations[reductionIndex],
                OwnerTurnEffectsExpired, DefensiveTurnEffectsExpired)
            && (dealer?.GetPower<VulnerablePower>()?.Amount ?? 0) > 0)
            multiplier *= Math.Max(0m, 1m - EffectiveOperationAmount(reductionIndex, 50) / 100m);
        return multiplier;
    }

    private bool TriggeredAttackDamageModifierApplies(GeneratorOperation trigger, int triggerIndex,
        CardModel eventAttack)
    {
        if (!CardEffectRules.SuppliesEventAttackForDamageModifier(trigger)) return false;
        var earlierAttacks = CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
            entry.CardPlay.Card.Type == CardType.Attack && entry.CardPlay.Player.Creature == Owner
            && entry.HappenedThisTurn(CombatState));
        return TriggerKind(trigger) switch
        {
            "attack_played" => true,
            "first_attack_played_each_turn" => earlierAttacks == 0,
            "first_zero_cost_attack_played_each_turn" => eventAttack.EnergyCost.GetResolved() == 0
                && CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
                    entry.CardPlay.Card.Type == CardType.Attack
                    && entry.CardPlay.Card.EnergyCost.GetResolved() == 0
                    && entry.CardPlay.Player.Creature == Owner && entry.HappenedThisTurn(CombatState)) == 0,
            "nth_attack_played_this_turn" => earlierAttacks + 1 == EffectiveOperationAmount(triggerIndex, 3),
            "next_attack" or "next_attacks_this_turn" => NextAttackTriggerAvailable
                && NextAttackTriggersRemaining > 0,
            _ => false
        };
    }

    public override decimal ModifyDamageAdditive(Creature? target, decimal amount, ValueProp props,
        Creature? dealer, CardModel? cardSource, CardPlay? cardPlay)
    {
        decimal bonus = 0m;
        if (dealer == Owner && props.IsPoweredAttack() && cardSource is not null
            && cardSource.Owner.Creature == Owner
            && cardSource.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shiv)
            && HasRule("first_derivative_bonus_damage"))
        {
            var priorShivs = CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
                entry.HappenedThisTurn(CombatState) && entry.CardPlay.Player == Owner.Player
                && entry.CardPlay.Card.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shiv));
            if (priorShivs == 0) bonus += RuleAmount("first_derivative_bonus_damage");
        }

        if (!NextAttackTriggerAvailable || NextAttackTriggersRemaining <= 0
            || dealer != Owner || !props.IsPoweredAttack()
            || cardSource is null || cardSource.Owner.Creature != Owner || cardSource.Type != CardType.Attack)
            return bonus;

        var effectiveOperations = EffectivePowerOperations();
        for (var operationIndex = 0; operationIndex < effectiveOperations.Count; operationIndex++)
        {
            var operation = effectiveOperations[operationIndex];
            if (!operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex)
                || triggerIndex < 0 || triggerIndex >= Definition.Card.Operations.Count
                || !CardEffectRules.IsNextAttackGrantTrigger(Definition.Card.Operations[triggerIndex])
                || operation.Scope != OperationScope.Modifier)
                continue;
            var value = Math.Max(1, EffectiveOperationAmount(operationIndex, 1));
            var modifierSpec = operation.Template == "M:base"
                ? OperationRuntimeSpecCompiler.RequireStructured(operation)
                : null;
            if (operation.Template == "M:DamagePerExhaustCard"
                || modifierSpec?.Variant == "exhaust_pile_scaled")
                bonus += value * PileType.Exhaust.GetPile(Owner.Player!).Cards.Count;
            else if (modifierSpec?.Variant == "vulnerable_scaled")
                bonus += value * (target?.GetPower<VulnerablePower>()?.Amount ?? 0m);
            else if (modifierSpec?.Variant == "strike_count_scaled")
                bonus += value * (Owner.Player?.PlayerCombatState?.AllCards.Count(candidate =>
                    candidate.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Strike)) ?? 0);
        }
        return bonus;
    }

    public override bool TryModifyEnergyCostInCombatLate(CardModel card, decimal originalCost, out decimal modifiedCost)
    {
        modifiedCost = originalCost;
        if (card.Owner.Creature != Owner) return false;
        if (HasRule("skills_cost_zero") && card.Type == CardType.Skill) { modifiedCost = 0; return true; }
        if (NextAttackFreeAvailable && card.Type == CardType.Attack) { modifiedCost = 0; return true; }
        return false;
    }

    public override CardLocation ModifyCardPlayResultLocation(CardModel card, bool isAutoPlay, ResourceInfo resources, CardLocation location)
    {
        if (card.Owner.Creature == Owner && card.Type == CardType.Skill
            && DescriptionOperations(Definition.Card.Operations).Any(operation => operation.Template == "N:Exhaust"
                && OperationRuntimeSpecCompiler.RequireStructured(operation).Variant == "referenced"))
            location.pileType = PileType.Exhaust;
        return location;
    }

    public override int ModifyCardPlayCount(CardModel card, Creature? target, int playCount)
    {
        if (card.Owner.Creature == Owner && _firstCardReplayAvailable) return playCount + 1;
        if (card.Owner.Creature == Owner && card.Type == CardType.Attack && NextAttackReplayAvailable) return playCount + 1;
        return playCount;
    }

    public override Task AfterModifyingCardPlayCount(CardModel card)
    {
        if (card.Owner.Creature == Owner && _firstCardReplayAvailable)
        {
            _firstCardReplayAvailable = false;
            Flash();
        }
        if (card.Owner.Creature == Owner && card.Type == CardType.Attack && NextAttackReplayAvailable)
        {
            Flash();
        }
        return Task.CompletedTask;
    }

    public override async Task BeforeCardPlayed(CardPlay cardPlay)
    {
        if (cardPlay.Card.Owner.Creature == Owner)
        {
            var operations = Definition.Card.Operations;
            for (var triggerIndex = 0; triggerIndex < operations.Count; triggerIndex++)
            {
                var operation = operations[triggerIndex];
                if (TriggerKind(operation) != "energy_cost_at_least_card_played") continue;
                var threshold = EffectiveOperationAmount(triggerIndex, 2);
                if (cardPlay.Card.EnergyCost.GetResolved() >= threshold)
                    await FireTriggerAt(triggerIndex, new ThrowingPlayerChoiceContext(), cardPlay, cardPlay.Card);
            }
        }
        if (cardPlay.Card.Owner.Creature == Owner && cardPlay.Card.Type == CardType.Attack && NextAttackFreeAvailable)
        {
            Flash();
        }
    }

    public override async Task AfterDamageReceived(PlayerChoiceContext choiceContext, Creature target, DamageResult result, ValueProp props, Creature? dealer, CardModel? cardSource)
    {
        if (target == Owner && props.IsPoweredAttack() && result.UnblockedDamage > 0
            && DescriptionOperations(Definition.Card.Operations).Any(operation => operation.Template == "CL:DieOnUnblockedAttack"))
        {
            await PowerCmd.Remove(this);
            Flash();
            await CreatureCmd.Kill(Owner);
            return;
        }
        if (target != Owner || dealer is null || !props.IsPoweredAttack() || !HasTrigger("attack_received")) return;
        await FireTriggers("attack_received", choiceContext, eventCreature: dealer);
    }

    public override async Task AfterShuffle(PlayerChoiceContext choiceContext, Player player)
    {
        if (player == Owner.Player)
            await FireTriggers("draw_pile_shuffled", choiceContext);
    }

    private bool HasTrigger(string kind) => FindTrigger(kind) >= 0;

    internal static bool HasTriggerWithLinkedEffect(IReadOnlyList<GeneratorOperation> operations,
        string triggerKind, string effectTemplate)
    {
        for (var triggerIndex = 0; triggerIndex < operations.Count; triggerIndex++)
        {
            if (TriggerKind(operations[triggerIndex]) != triggerKind) continue;
            if (operations.Any(operation => operation.Template == effectTemplate
                    && operation.Parameters.GetValueOrDefault("triggerIndex", -1) == triggerIndex))
                return true;
        }
        return false;
    }

    private int FindTrigger(string kind) => Definition.Card.Operations.ToList()
        .FindIndex(operation => TriggerKind(operation) == kind);

    private bool HasRule(string variant) => DescriptionOperations(Definition.Card.Operations)
        .Any(operation => operation.Scope == OperationScope.AbilityRule
            && OperationRuntimeSpecCompiler.RequireStructured(operation).Variant == variant);

    private bool HasCrossTurnNextAttackTrigger() => DescriptionOperations(Definition.Card.Operations)
        .Any(operation => operation.Template is "C:grantNextAttack" or "C:for"
            && CardEffectRules.IsNextAttackGrantTrigger(operation));

    internal static bool IsNthAttackPlayedThisTurnTrigger(GeneratorOperation operation) =>
        TriggerKind(operation) == "nth_attack_played_this_turn";

    internal static int NthAttackPlayedThisTurnThreshold(GeneratorOperation operation) =>
        Math.Max(1, OperationRuntimeSpecCompiler.RequireStructured(operation).Values
            .FirstOrDefault(value => value.Id == "threshold")?.BaseValue ?? 3);

    private decimal RuleAmount(string variant)
    {
        var operations = Definition.Card.Operations;
        var index = operations.ToList().FindIndex(operation => operation.Scope == OperationScope.AbilityRule
            && OperationRuntimeSpecCompiler.RequireStructured(operation).Variant == variant);
        if (index < 0) return 0m;
        return EffectiveOperationAmount(index, 0);
    }

    private int EffectiveOperationAmount(int operationIndex, int fallback)
    {
        if (TryGetCapturedOperationValue(operationIndex, out var captured)) return captured;
        var operation = Definition.Card.Operations[operationIndex];
        var spec = OperationRuntimeSpecCompiler.RequireStructured(operation);
        if (SourceUpgraded && Definition.Card.Upgrade is { } upgrade)
        {
            foreach (var effect in upgrade.Effects.Where(effect => effect.OperationIndex == operationIndex
                         && effect.Delta is not null
                         && effect.Kind is CardUpgradeKind.IncreaseNumber or CardUpgradeKind.ReduceSelfDamage
                             or CardUpgradeKind.ReduceThreshold or CardUpgradeKind.ReduceNegativeNumber))
            {
                var slotId = effect.ValueSlotId;
                if (slotId is null
                    && OperationRuntimeSpecCompiler.TryProjectLegacyExecutionUpgradeValue(operation,
                        operation.ChineseText, out var legacyProjection))
                    slotId = legacyProjection?.SlotId;
                if (slotId is not null && spec.Values.Any(value => value.Id == slotId))
                    spec = OperationRuntimeSpecCompiler.ApplyUpgradeDelta(spec, slotId, effect.Delta!.Value);
            }
        }
        var preferredSlot = OperationRuntimeSpecCompiler.UpgradeValueSlot(operation);
        var slot = preferredSlot is null ? spec.Values.FirstOrDefault(value => value.Upgradable)
            : spec.Values.FirstOrDefault(value => value.Id == preferredSlot);
        if (slot is null) return fallback;
        return slot.Source switch
        {
            "special_x" => Math.Max(0, ChaosCardModel.ChaosXValueMultiplier.Apply(Definition.Card,
                SpecialXValue) + slot.Offset),
            "energy_x" => Math.Max(0, ChaosCardModel.ChaosXValueMultiplier.Apply(Definition.Card,
                ResolvedEnergyXValue) + slot.Offset),
            "star_x" => Math.Max(0, ChaosCardModel.ChaosXValueMultiplier.Apply(Definition.Card,
                ResolvedStarXValue) + slot.Offset),
            _ => Math.Max(0, slot.BaseValue + slot.Offset)
        };
    }

    private bool TryGetCapturedOperationValue(int operationIndex, out int value)
    {
        for (var offset = 0; offset + 1 < CapturedOperationValues.Length; offset += 2)
        {
            if (CapturedOperationValues[offset] != operationIndex) continue;
            value = CapturedOperationValues[offset + 1];
            return true;
        }
        value = 0;
        return false;
    }

    private bool StartTriggerNeedsPlayerChoice(params string[] kinds) =>
        StartTriggerNeedsPlayerChoice(Definition.Card.Operations, kinds);

    internal static bool StartTriggerNeedsPlayerChoice(IReadOnlyList<GeneratorOperation> operations,
        IReadOnlyCollection<string> kinds)
    {
        for (var triggerIndex = 0; triggerIndex < operations.Count; triggerIndex++)
        {
            var trigger = operations[triggerIndex];
            if (trigger.Scope is not (OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger)
                || TriggerKind(trigger) is not { } kind || !kinds.Contains(kind)) continue;
            if (operations.Any(operation => TurnStartOperationNeedsChoiceContext(operation)
                && operation.Parameters.TryGetValue("triggerIndex", out var linked) && linked == triggerIndex))
                return true;
        }
        return false;
    }

    // Autoplayed cards are not known until this trigger resolves. They may themselves contain a hand-card
    // selector, so every autoplay route needs the real PlayerChoiceContext even when the Power's own printed
    // effects do not visibly ask for a choice.
    internal static bool TurnStartOperationNeedsChoiceContext(GeneratorOperation operation) => operation.Template is
        "N:Discard" or "N:Exhaust" or "CL:TransformSelectedHandCards"
        or "I:PlayTopCardAndExhaust" or "I:PlayTopXCards"
        or "CL:PlayTopDrawCard" or "D:AutoPlayRandomAttackFromDraw"
        or "I:AutoPlayRandomAttackFromHand" or "I:PlayAtRandomEnemy"
        or "I:PlayThisCard" or "R:PlayThisCard" or "D:ReplayEventCard"
        or "CL:ProxyAtomic_Catastrophe" or "CL:ProxyAtomic_BeatDown"
        || CardEffectRules.OperationNeedsChoiceContext(operation);

    private static string? TriggerKind(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.RequireStructured(operation).Trigger?.Kind;

    private async Task FireTriggers(string kind, PlayerChoiceContext choiceContext, CardPlay? sourcePlay = null,
        CardModel? eventCard = null, Creature? eventCreature = null, decimal eventAmount = 0)
        => await FireTriggersAny([kind], choiceContext, sourcePlay, eventCard, eventCreature, eventAmount);

    internal Task FireExternalTriggerAsync(string kind, PlayerChoiceContext choiceContext,
        CardPlay? sourcePlay = null, CardModel? eventCard = null, Creature? eventCreature = null,
        decimal eventAmount = 0)
        => FireTriggers(kind, choiceContext, sourcePlay, eventCard, eventCreature, eventAmount);

    private async Task FireTriggersAny(IReadOnlyCollection<string> kinds,
        PlayerChoiceContext choiceContext, CardPlay? sourcePlay = null,
        CardModel? eventCard = null, Creature? eventCreature = null, decimal eventAmount = 0)
    {
        var operations = Definition.Card.Operations;
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Scope is not (OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger)) continue;
            var triggerKind = TriggerKind(operation);
            if (triggerKind is null || !kinds.Contains(triggerKind)) continue;
            await FireTriggerAt(index, choiceContext, sourcePlay, eventCard, eventCreature, eventAmount);
        }
    }

    private async Task FireTriggerAt(int index, PlayerChoiceContext choiceContext, CardPlay? sourcePlay = null,
        CardModel? eventCard = null, Creature? eventCreature = null, decimal eventAmount = 0)
    {
        var operation = Definition.Card.Operations[index];
        if (TurnLimitedTriggerExpired(operation, OwnerTurnEffectsExpired, DefensiveTurnEffectsExpired)) return;
        var previousDepth = TriggerChainDepth.Value;
        if (!TryEnterTrigger(_activeTriggers, index, previousDepth)) return;
        TriggerChainDepth.Value = previousDepth + 1;
        try
        {
            var rollingIndex = Definition.Card.Operations.ToList().FindIndex(candidate =>
                candidate.Template is "N:AllD" or "CL:RollingAllDamage"
                && candidate.Parameters.GetValueOrDefault("triggerIndex", -1) == index
                && Definition.Card.Operations.Any(increase =>
                    increase.Template == "CL:IncreaseRollingDamage"
                    && increase.Parameters.GetValueOrDefault("triggerIndex", -1) == index));
            if (rollingIndex >= 0 && eventAmount == 0) eventAmount = _rollingDamage;
            Flash();
            if (ChaosDiagnostics.VerboseRuntime)
                ChaosRuntimeDiagnostics.TriggerFired("composite", Slot, operation);
            await ChaosOperationExecutor.ExecuteTriggered(this, index, choiceContext, sourcePlay, eventCard, eventCreature, eventAmount);
            if (rollingIndex >= 0)
            {
                var increaseIndex = Definition.Card.Operations.ToList().FindIndex(candidate =>
                    candidate.Template == "CL:IncreaseRollingDamage"
                    && candidate.Parameters.GetValueOrDefault("triggerIndex", -1) == index);
                if (increaseIndex >= 0) _rollingDamage += Math.Max(0, EffectiveOperationAmount(increaseIndex, 5));
            }
        }
        finally
        {
            TriggerChainDepth.Value = previousDepth;
            _activeTriggers.Remove(index);
        }
    }

    internal static bool TryEnterTrigger(ISet<int> activeTriggers, int triggerIndex, int currentDepth) =>
        currentDepth < MaximumNestedTriggerDepth && activeTriggers.Add(triggerIndex);

    internal static bool IsFirstCardPlayThisTurn(int finishedOwnerCardPlays) => finishedOwnerCardPlays == 1;

    internal static bool ShouldFireOwnerTurnHpLoss(bool isOwner, decimal delta, bool isOwnerSide) =>
        isOwner && delta < 0 && isOwnerSide;

    private void ExpireOwnerTurnEffects()
    {
        OwnerTurnEffectsExpired = true;
        var nextAttack = Definition.Card.Operations.FirstOrDefault(CardEffectRules.IsNextAttackGrantTrigger);
        if (nextAttack is null
            || OperationRuntimeSpecCompiler.RequireStructured(nextAttack).Trigger?.Lifetime != "this_turn") return;
        NextAttackTriggerAvailable = false;
        NextAttackTriggersRemaining = 0;
        NextAttackReplayAvailable = false;
        NextAttackFreeAvailable = false;
    }

    internal static bool TurnLimitedTriggerExpired(GeneratorOperation operation, bool ownerTurnExpired,
        bool defensiveTurnExpired)
    {
        var trigger = OperationRuntimeSpecCompiler.RequireStructured(operation).Trigger;
        if (trigger?.Lifetime != "this_turn") return false;
        return trigger.Kind is "attack_received" or "vulnerable_enemy_damage_reduction"
            ? defensiveTurnExpired
            : ownerTurnExpired;
    }
}
