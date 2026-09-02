using System.Text.RegularExpressions;
using ChaosCardGenerator;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using AutoAnthony.Patches;

namespace AutoAnthony;

internal sealed class ChaosExecutionState
{
    public Dictionary<string, CardModel> CardSlots { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, IReadOnlyList<CardModel>> CardSelections { get; } = new(StringComparer.Ordinal);
    public List<CardModel> ExhaustedByCard { get; } = [];
    public List<CardModel> DiscardedByCard { get; } = [];
    public Creature? Target { get; set; }
    public CardModel? EventCard { get; init; }
    public CardModel? IterationCard { get; set; }
    public CardModel? LastMovedCard { get; set; }
    public int LastExhaustedAttackDamage { get; set; }
    public bool LastAttackKilled { get; set; }
    public int LastDamageDealt { get; set; }
    public List<CardModel> LastDrawnCards { get; } = [];
    public Dictionary<PowerModel, int>? TargetDebuffSnapshot { get; set; }
    public decimal EventAmount { get; init; }
    public bool IsTriggered { get; init; }
    public int CurrentCardEnergySpent { get; init; }
    public int PriorAttackHitsOnTargetAtPlayStart { get; init; }
    public bool EndTurnRequested { get; set; }
}

internal static class ChaosOperationExecutor
{
    private static readonly HashSet<string> GeneratedValueProxyTemplates = new(StringComparer.Ordinal)
    {
        "A:ProxyAtomic_Buffer", "A:ProxyAtomic_Calcify", "A:ProxyAtomic_Lethality",
        "A:ProxyAtomic_Parry", "A:ProxyAtomic_Royalties", "A:ProxyAtomic_SwordSage",
        "A:ProxyAtomic_VoidForm", "A:VoidFormFirstCardsFree",
        "CL:ProxyAtomic_BeatDown", "CL:ProxyAtomic_Catastrophe",
        "CL:ProxyAtomic_Discovery", "CL:ProxyAtomic_HiddenGem", "CL:ProxyAtomic_Splash",
        "I:ProxyAtomic_Charge", "I:ProxyAtomic_Dredge", "I:ProxyAtomic_ForegoneConclusion",
        "I:ProxyAtomic_MultiCast", "I:ProxyAtomic_Quasar", "I:ProxyAtomic_Tempest",
        "I:ProxyAtomic_Transfigure", "N:ProxyDamage_Atomic_StarX_Stardust",
        "T:ProxyDamage_Atomic_EnergyX_Eradicate",
        // Schema 1-7 snapshots can still contain Poke under its former proxy-damage identity.
        "T:ProxyDamage_Atomic_Poke"
    };

    internal static bool InterpretsGeneratedProxyValues(string template) =>
        GeneratedValueProxyTemplates.Contains(template);

    private static readonly HashSet<string> SimpleHandDerivativeProducerTemplates = new(StringComparer.Ordinal)
    {
        "N:CreateShiv",
        "N:CreateInkShiv",
        "NCR:CreateSoulInHand",
        "NCR:AddSweepingGazeToHand",
        "R:AddDebrisToHand"
    };

    private static readonly HashSet<string> RuntimeDerivativeProducerTemplates = new(StringComparer.Ordinal)
    {
        "I:Transform",
        "N:CreateShiv",
        "N:CreateInkShiv",
        "D:CreateDazedInDiscard",
        "D:CreateTwoWoundsInDiscard",
        "D:CreateSlimeInDiscard",
        "D:CreateBurnInDiscard",
        "D:CreateVoidInDiscard",
        "D:TransformStatusesToFuel",
        "I:ProxyAtomic_Seance",
        "NCR:CreateSoulInDraw",
        "NCR:CreateSoulInDrawX",
        "NCR:CreateSoulInHand",
        "NCR:CreateSoulInDiscard",
        "NCR:AddSweepingGazeToHand",
        "R:AddDebrisToHand",
        "R:FillHandWithDebris",
        "I:ProxyAtomic_Begone",
        "I:ProxyAtomic_Charge",
        "I:ProxyAtomic_Guards"
    };

    internal static void AuditDerivativeProducerCoverage()
    {
        var catalog = DerivativeSlotCatalog.SlotTemplates.Where(DerivativeSlotCatalog.IsProducer)
            .ToHashSet(StringComparer.Ordinal);
        var missing = catalog.Except(RuntimeDerivativeProducerTemplates).OrderBy(template => template).ToArray();
        var stale = RuntimeDerivativeProducerTemplates.Except(catalog).OrderBy(template => template).ToArray();
        if (missing.Length > 0 || stale.Length > 0)
            throw new InvalidOperationException("Derivative producer runtime coverage mismatch. Missing: ["
                + string.Join(", ", missing) + "]; stale: [" + string.Join(", ", stale) + "].");
    }

    public static async Task Play(ChaosCardModel card, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        var state = new ChaosExecutionState
        {
            Target = cardPlay.Target,
            CurrentCardEnergySpent = Math.Max(0, cardPlay.Resources.EnergySpent),
            PriorAttackHitsOnTargetAtPlayStart = CountPriorAttackHits(card, cardPlay.Target)
        };
        await CreatureCmd.TriggerAnim(card.Owner.Creature,
            card.Type == CardType.Power ? "PowerUp" : card.Definition.AttackAnimation,
            card.Type == CardType.Attack ? card.Owner.Character.AttackAnimDelay : card.Owner.Character.CastAnimDelay);

        var operations = card.Generated.Operations;
        if (state.Target is not null
            && operations.Any(operation => operation.Template == "NCR:CopyTargetDebuffsToOthers"))
            state.TargetDebuffSnapshot = CaptureTargetDebuffs(state.Target);
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Template is "N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK")
            {
                // Selectors are metadata for the operation that owns their card slot. Resolve them immediately
                // before that operation executes, after its condition has matched. Resolving here used to prompt
                // while a Power was armed, then discard the choice before its later trigger fired.
                continue;
            }

            if (operation.Scope is OperationScope.Modifier or OperationScope.AbilityTrigger)
                continue;
            if (operation.Scope == OperationScope.ConditionalTrigger)
            {
                if (operation.Template is "C:forEach" or "D:ForEachExhaustedStatus")
                    await ExecuteForEach(card, index, choiceContext, cardPlay, state);
                else if (operation.Template == "C:forEachDiscarded")
                    await ExecuteForEachDiscarded(card, index, choiceContext, cardPlay, state);
                else if (operation.Template == "D:ForEachEnergySpentThisTurn")
                    await ExecuteForEnergySpentThisTurn(card, index, choiceContext, cardPlay, state);
                continue;
            }
            if (operation.Parameters.TryGetValue("triggerIndex", out var triggerIndex))
            {
                var trigger = operations[triggerIndex];
                if (trigger.Template is "C:forEach" or "C:forEachDiscarded" or "D:ForEachExhaustedStatus"
                    or "D:ForEachEnergySpentThisTurn") continue;
                if (trigger.Scope == OperationScope.AbilityTrigger || IsLingeringTrigger(trigger)) continue;
                if (!ConditionMatches(card, trigger, state)) continue;
            }
            await ExecuteWithResolvedTarget(card, index, choiceContext, cardPlay, state);
        }

        if (operations.Any(RequiresCompositePower))
        {
            var power = (ChaosCompositePower)ModelDb.Power<ChaosCompositePower>().ToMutable();
            var permanent = card.Type == CardType.Power
                || operations.Any(operation => operation.Template is "CL:AfterTurns" or "CL:DieOnUnblockedAttack");
            power.Configure(card.Generated.Character, card.Definition.Slot, card.IsUpgraded, permanent,
                card.ResolvedSpecialXValue, card.ResolvedEnergyXValue, card.ResolvedStarXValue,
                card.CaptureOperationValuesForPower(), cardPlay.Target?.CombatId, card.RuntimeProfileId);
            await PowerCmd.Apply(choiceContext, power, card.Owner.Creature, 1m, card.Owner.Creature, card);
            if (ChaosDiagnostics.VerboseRuntime)
                Log.Info($"[AutoAnthony] Armed trigger effect for slot {card.Definition.Slot}: {card.DynamicTitle}");
        }

        // EndTurn is queued by Execute so all other effects and persistent Powers on this resolution finish first.
        // The same path handles direct, conditional, for-each and Power-triggered copies without firing an
        // unfulfilled linked EndTurn merely because its operation exists on the card.
        if (state.EndTurnRequested)
            RequestEndTurnDuringPlayPhase(card.Owner);
    }

    internal static async Task ExecuteTriggered(ChaosCompositePower power, int triggerIndex, PlayerChoiceContext choiceContext,
        CardPlay? sourcePlay = null, CardModel? eventCard = null, Creature? eventCreature = null, decimal eventAmount = 0)
    {
        var canonical = ChaosCardRegistry.Canonical(power.Character, power.Slot);
        var owner = power.Owner.Player;
        if (owner is null) return;
        var source = power.CombatState.CreateCard(canonical, owner) as ChaosCardModel;
        if (source is null) return;
        source.ResolvedSpecialXValue = power.SpecialXValue;
        source.SetResolvedXValues(power.ResolvedEnergyXValue, power.ResolvedStarXValue);
        if (power.SourceUpgraded && source.IsUpgradable) CardCmd.Upgrade(source);
        source.ApplyCapturedOperationValues(power.CapturedOperationValues);
        try
        {
            var sourceTarget = power.SourceTargetCombatId < 0
                ? null
                : power.CombatState.GetCreature((uint)power.SourceTargetCombatId);
            await ExecuteTriggered(source, triggerIndex, choiceContext, sourcePlay, eventCard, eventCreature,
                eventAmount, sourceTarget);
        }
        finally
        {
            // Hand selection keeps selected holders in the centre container until the model passed as its
            // selection source reports ExecutionFinished. Triggered effects execute through a detached card model
            // reconstructed from the power snapshot, while HookPlayerChoiceContext only completes the power itself.
            // Without completing this detached source, transform/upgrade effects can leave the selected card in the
            // centre and a later hand interaction can never finish. Mirror CardModel/Hook execution ownership here,
            // including the exceptional path, so every triggered hand-selection transaction is closed exactly once.
            source.InvokeExecutionFinished();
        }
    }

    internal static async Task ExecuteTriggered(ChaosCardModel source, int triggerIndex, PlayerChoiceContext choiceContext,
        CardPlay? sourcePlay = null, CardModel? eventCard = null, Creature? eventCreature = null, decimal eventAmount = 0,
        Creature? sourceTarget = null)
    {
        var owner = source.Owner;
        var operations = source.Generated.Operations;
        var linkedOperations = Enumerable.Range(triggerIndex + 1, operations.Count - triggerIndex - 1)
            .Where(index => operations[index].Parameters.TryGetValue("triggerIndex", out var linked) && linked == triggerIndex)
            .ToArray();
        var unresolvedEventTargetOperations = linkedOperations
            .Where(index => CardEffectRules.RequiresSingleEnemyTarget(operations[index]))
            .Where(index => !CardEffectRules.UsesExplicitRandomEnemyTarget(operations[index]))
            .ToArray();
        if (eventCreature is null && unresolvedEventTargetOperations.Length > 0)
        {
            // Same-turn triggered effects such as Knife Trap retain the enemy selected by their source card.
            // The detached power used to discard this identity, so an otherwise valid "against that enemy"
            // payload either selected an unrelated random enemy or failed to play cards from the Exhaust pile.
            if (sourceTarget?.IsAlive == true)
                eventCreature = sourceTarget;
        }
        if (eventCreature is null && unresolvedEventTargetOperations.Length > 0)
        {
            // Current generation rejects this structure, but older run snapshots can still contain a delayed
            // trigger linked to a single-target payoff. Those snapshots cannot reconstruct the original selected
            // enemy, so resolve one live enemy deterministically from the combat RNG instead of dropping the whole
            // trigger (the former behavior behind condition-only cards doing nothing).
            var activeCombat = source.CombatState ?? owner.Creature.CombatState;
            var enemies = activeCombat?.HittableEnemies.ToArray() ?? [];
            if (enemies.Length == 0)
            {
                Log.Warn($"[AutoAnthony] Trigger {triggerIndex} on slot {source.Definition.Slot} has no live enemy for its legacy linked target effect.");
                return;
            }
            eventCreature = owner.RunState.Rng.CombatTargets.NextItem(enemies);
            Log.Warn($"[AutoAnthony] Trigger {triggerIndex} on slot {source.Definition.Slot} used a random live enemy for a legacy unresolved linked target effect.");
        }
        var play = sourcePlay ?? new CardPlay
        {
            Card = source,
            Player = owner,
            Target = eventCreature,
            ResultPile = PileType.Discard,
            Resources = new ResourceInfo { EnergySpent = 0, EnergyValue = 0, StarsSpent = 0, StarValue = 0 },
            IsAutoPlay = true,
            PlayIndex = 0,
            PlayCount = 1
        };
        var state = new ChaosExecutionState { Target = eventCreature, EventCard = eventCard, EventAmount = eventAmount, IsTriggered = true };
        if (eventCreature is not null
            && linkedOperations.Any(index => operations[index].Template == "NCR:CopyTargetDebuffsToOthers"))
            state.TargetDebuffSnapshot = CaptureTargetDebuffs(eventCreature);
        if (eventCard is not null) state.CardSlots["eventCard"] = eventCard;
        var executed = 0;
        foreach (var index in linkedOperations)
        {
            var operation = operations[index];
            if (operation.Scope == OperationScope.Modifier) continue;
            // Corruption-style exhaustion is implemented by ModifyCardPlayResultLocation so it happens once,
            // after all OnPlay effects have finished.
            if (operation.Template == "N:Exhaust"
                && OperationRuntimeSpecCompiler.GetOrCompile(operation).Variant == "referenced") continue;
            await ExecuteWithResolvedTarget(source, index, choiceContext, play, state);
            executed++;
        }
        if (state.EndTurnRequested)
            RequestEndTurnDuringPlayPhase(owner);
        var passiveModifiers = linkedOperations.Count(index => operations[index].Scope == OperationScope.Modifier);
        if (executed == 0 && passiveModifiers == 0)
            Log.Warn($"[AutoAnthony] Trigger {triggerIndex} on slot {source.Definition.Slot} had no executable linked effects.");
        else if (ChaosDiagnostics.VerboseRuntime)
            Log.Info($"[AutoAnthony] Trigger {triggerIndex} on slot {source.Definition.Slot} executed {executed} linked effect(s).");
    }

    private static async Task ExecuteForEach(ChaosCardModel card, int triggerIndex, PlayerChoiceContext choiceContext, CardPlay cardPlay, ChaosExecutionState state)
    {
        var trigger = card.Generated.Operations[triggerIndex];
        var triggerKind = OperationRuntimeSpecCompiler.GetOrCompile(trigger).Trigger?.Kind;
        IReadOnlyList<CardModel> items = state.ExhaustedByCard
            .Where(candidate => ExhaustedCardMatchesTrigger(triggerKind, candidate.Type)).ToList();
        foreach (var item in items)
        {
            state.IterationCard = item;
            for (var index = triggerIndex + 1; index < card.Generated.Operations.Count; index++)
            {
                var effect = card.Generated.Operations[index];
                if (!effect.Parameters.TryGetValue("triggerIndex", out var linked) || linked != triggerIndex) continue;
                if (effect.CardTargetSlot is { } slot) state.CardSlots[slot] = item;
                await ExecuteWithResolvedTarget(card, index, choiceContext, cardPlay, state);
            }
        }
        state.IterationCard = null;
    }

    internal static bool ExhaustedCardMatchesTrigger(string? triggerKind, CardType cardType) => triggerKind switch
    {
        "for_each_exhausted_non_attack" => cardType != CardType.Attack,
        "for_each_exhausted_status" => cardType == CardType.Status,
        _ => true
    };

    private static void RequestEndTurnDuringPlayPhase(Player owner)
    {
        // Damage received, deaths and several other generic event hooks can also fire during the enemy turn or
        // while the player's start/end lifecycle is still being processed. Enqueuing EndTurn there re-enters the
        // combat state machine (and can desync multiplayer). The payoff is meaningful only while cards can be
        // played, so outside PlayerTurnPhase.Play it resolves as a safe no-op after its sibling effects finish.
        if (owner.PlayerCombatState?.Phase != PlayerTurnPhase.Play)
        {
            if (ChaosDiagnostics.VerboseRuntime)
                Log.Info($"[AutoAnthony] Ignored triggered EndTurn outside the play phase for player {owner.NetId}.");
            return;
        }
        PlayerCmd.EndTurn(owner, canBackOut: false);
    }

    private static async Task ExecuteForEachDiscarded(ChaosCardModel card, int triggerIndex, PlayerChoiceContext choiceContext, CardPlay cardPlay, ChaosExecutionState state)
    {
        foreach (var item in state.DiscardedByCard.ToList())
        {
            state.IterationCard = item;
            for (var index = triggerIndex + 1; index < card.Generated.Operations.Count; index++)
            {
                var effect = card.Generated.Operations[index];
                if (!effect.Parameters.TryGetValue("triggerIndex", out var linked) || linked != triggerIndex) continue;
                await ExecuteWithResolvedTarget(card, index, choiceContext, cardPlay, state);
            }
        }
        state.IterationCard = null;
    }

    private static async Task ExecuteForEnergySpentThisTurn(ChaosCardModel card, int triggerIndex,
        PlayerChoiceContext choiceContext, CardPlay cardPlay, ChaosExecutionState state)
    {
        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        if (combatState is null) return;
        var threshold = Math.Max(1, RuntimeSpecValue(card, triggerIndex, "threshold", 1));
        // SpendResources records the current play before OnPlay resolves. Subtract that exact payment so the
        // implementation matches the printed "except on this card" clause, including modifiers and X costs.
        var spentThisTurn = CombatManager.Instance.History.Entries.OfType<EnergySpentEntry>()
            .Where(entry => entry.Actor == card.Owner.Creature && entry.HappenedThisTurn(combatState))
            .Sum(entry => entry.Amount);
        var otherEnergySpent = Math.Max(0, spentThisTurn - Math.Max(0, cardPlay.Resources.EnergySpent));
        var resolutions = otherEnergySpent / threshold;
        for (var resolution = 0; resolution < resolutions; resolution++)
        {
            for (var index = triggerIndex + 1; index < card.Generated.Operations.Count; index++)
            {
                var effect = card.Generated.Operations[index];
                if (!effect.Parameters.TryGetValue("triggerIndex", out var linked) || linked != triggerIndex
                    || effect.Scope == OperationScope.Modifier)
                    continue;
                await ExecuteWithResolvedTarget(card, index, choiceContext, cardPlay, state);
            }
        }
    }

    private static async Task ExecuteWithResolvedTarget(ChaosCardModel card, int index,
        PlayerChoiceContext choiceContext, CardPlay cardPlay, ChaosExecutionState state)
    {
        var operation = card.Generated.Operations[index];
        await ResolveCardSelectionForOperation(card, index, choiceContext, state);
        var originalTarget = state.Target;
        if (CardEffectRules.UsesExplicitRandomEnemyTarget(operation))
        {
            var activeCombat = card.CombatState ?? card.Owner.Creature.CombatState;
            if (activeCombat is null) return;
            state.Target = card.Owner.RunState.Rng.CombatTargets.NextItem(activeCombat.HittableEnemies);
            if (state.Target is not null && ChaosDiagnostics.VerboseRuntime)
                Log.Info($"[AutoAnthony] Resolved random enemy {state.Target.LogName} for operation {operation.Template} on slot {card.Definition.Slot}.");
        }
        try
        {
            await Execute(card, index, choiceContext, cardPlay, state);
        }
        finally
        {
            state.Target = originalTarget;
        }
    }

    private static async Task ResolveCardSelectionForOperation(ChaosCardModel card, int operationIndex,
        PlayerChoiceContext choiceContext, ChaosExecutionState state)
    {
        var operations = card.Generated.Operations;
        var selectedEffect = operations[operationIndex];
        if (selectedEffect.CardTargetSlot is not { } slot || state.CardSlots.ContainsKey(slot)) return;

        var selector = CardSelectorForSlot(operations, slot);
        if (selector is null)
        {
            // Operations referring to the card that caused a trigger do not have a selector marker. Keep that
            // event-card route separate from explicit player-choice slots so the triggering card can never replace
            // a requested hand selection.
            if (state.EventCard is not null) state.CardSlots[slot] = state.EventCard;
            return;
        }

        Func<CardModel, bool>? filter = selector.Template == "N_SELECT_HAND_ATTACK"
            ? candidate => candidate.Type == CardType.Attack
            : selectedEffect.Template == "R:PlaySelectedSkillMultipleTimes"
                ? candidate => candidate.Type == CardType.Skill && !candidate.Keywords.Contains(CardKeyword.Unplayable)
            : selectedEffect.Template == "R:CopySelectedColorlessCard"
                ? candidate => candidate.VisualCardPool.IsColorless
            : selectedEffect.Template == "NCR:AddVoidToSelectedHandCard"
                ? candidate => !candidate.Keywords.Contains(CardKeyword.Ethereal)
                : selectedEffect.Template == "NCR:AddRetainToSelectedHandCard"
                    ? candidate => !candidate.Keywords.Contains(CardKeyword.Retain)
                    : null;
        var prompt = selectedEffect.Template == "N:Exhaust"
            ? CardSelectorPrefs.ExhaustSelectionPrompt
            : selectedEffect.Template is "I:Upgrade" or "I:UpgradeThatCard"
                ? CardSelectorPrefs.UpgradeSelectionPrompt
            : selectedEffect.Template == "R:PlaySelectedSkillMultipleTimes"
                ? SelectionPrompt("SELECT_HAND_SKILL")
            : selectedEffect.Template == "R:CopySelectedColorlessCard"
                ? SelectionPrompt("SELECT_COLORLESS")
            : selectedEffect.Template is "R:PutSelectedHandCardsOnDraw" or "R:PutSelectedHandCardOnDraw"
                ? SelectionPrompt("HAND_TO_DRAW")
                : selectedEffect.Template == "NCR:AddVoidToSelectedHandCard"
                    ? SelectionPrompt("ADD_ETHEREAL")
                    : selectedEffect.Template == "NCR:AddRetainToSelectedHandCard"
                        ? SelectionPrompt("ADD_RETAIN")
                : SelectionPrompt(selector.Template == "N_SELECT_HAND_ATTACK"
                    ? "SELECT_HAND_ATTACK" : "SELECT_HAND_CARD");
        var prefs = new CardSelectorPrefs(prompt, 1)
        {
            // Decisions Decisions explicitly permits selecting a Skill regardless of whether its ordinary cost or
            // target would make it playable from hand; AutoPlay supplies those semantics after selection.
            PretendCardsCanBePlayed = selectedEffect.Template == "R:PlaySelectedSkillMultipleTimes"
        };
        var selected = (await SelectFromHandIfAny(choiceContext, card.Owner, prefs, filter, card)).ToArray();
        if (selected.Length == 0) return;
        state.CardSlots[slot] = selected[0];
        state.CardSelections[slot] = selected;
    }

    internal static GeneratorOperation? CardSelectorForSlot(IReadOnlyList<GeneratorOperation> operations,
        string slot) => operations.FirstOrDefault(candidate =>
            candidate.Template is "N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK"
            && candidate.Parameters.TryGetValue("slotIndex", out var slotIndex)
            && slot == $"card{slotIndex}");

    private static async Task Execute(ChaosCardModel card, int index, PlayerChoiceContext choiceContext, CardPlay cardPlay, ChaosExecutionState state)
    {
        // Triggered effects use a temporary source card which deliberately is not in a combat pile.
        // In v111 CardModel.CombatState is null for such cards, even though their owner is in combat.
        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        if (combatState is null) return;
        var operation = card.Generated.Operations[index];
        if (operation.Template == "R:EndTurn")
        {
            // Defer the lifecycle command until the enclosing direct/triggered resolution has installed every
            // other effect. This preserves Void Form and makes triggered EndTurn payoffs deterministic.
            state.EndTurnRequested = true;
            return;
        }
        var amount = card.OperationAmount(index);
        var runtimeSpec = EffectiveRuntimeSpec(card, index);
        // v0.1.99 and earlier snapshots lost Sic 'Em's printed numeric slot. Native Sic 'Em applies 3;
        // retain that value for old cards while new generated cards carry and execute their explicit number.
        if (operation.Template == "NCR:ApplyPower_SicEmPower" && amount <= 0)
            amount = 3;
        if (!DependencyConditionMatches(card, index)) return;
        var dependencyMultiplier = DependencyMultiplier(card, index, state.Target,
            state.PriorAttackHitsOnTargetAtPlayStart);
        // A multiplicative prefix with a live count of zero means its payoff does not resolve. Passing amount=0
        // into producers that defensively clamp their count to one would otherwise create a card/orb despite the
        // printed “for each” clause having no matches.
        if (dependencyMultiplier <= 0) return;
        amount *= dependencyMultiplier;
        // Persistent Powers snapshot powered Damage when played. For Mind Blast/Gold Axe dependencies the flat
        // Strength/Vigor contribution is stored separately so it is added once after the live-count multiplier,
        // matching CalculatedDamageVar rather than becoming part of the per-card coefficient.
        amount += card.CapturedExternalDamageBonus(index);

        // All of these operations mean “create the slotted derivative in Hand”. Dispatch by the stable producer
        // operation rather than by the derivative's localized name/native class, so Ultimate Chaos substitutions
        // such as Minion Strike and Minion Dive Bomb use exactly the same execution path as Shivs and Souls.
        if (SimpleHandDerivativeProducerTemplates.Contains(operation.Template))
        {
            await CreateDerivatives(card, index, operation, PileType.Hand, ExecutableGeneratedCardCount(amount));
            return;
        }

        if (operation.Template.Contains(":Proxy", StringComparison.Ordinal))
        {
            await ExecuteOriginalOperation(card, index, choiceContext, cardPlay, state, operation);
            return;
        }

        if (await TryExecuteStructuredCommon(card, choiceContext, cardPlay, state, runtimeSpec, amount))
            return;
        if (await TryExecuteStructuredDamage(card, index, choiceContext, cardPlay, state,
                runtimeSpec, amount, combatState))
            return;

        // Extension handlers consume the same structured opcode/variant contract as built-in routes. Built-in
        // common/damage implementations deliberately run first; an unhandled extension may still return false to
        // preserve old character/template fallback for cross-version snapshots.
        if (await ComponentRuntimeApi.TryExecute(card, index, operation, runtimeSpec, amount,
                choiceContext, cardPlay, state))
            return;

        if (operation.Template.StartsWith("CL:", StringComparison.Ordinal))
        {
            await ExecuteColorlessOperation(card, index, choiceContext, cardPlay, state, operation, amount);
            return;
        }

        if (operation.Template.StartsWith("D:", StringComparison.Ordinal))
        {
            await ExecuteDefectOperation(card, index, choiceContext, cardPlay, state, operation, amount);
            return;
        }
        if (operation.Template.StartsWith("NCR:", StringComparison.Ordinal))
        {
            await ExecuteNecrobinderOperation(card, index, choiceContext, cardPlay, state, operation, amount);
            return;
        }
        if (operation.Template.StartsWith("R:", StringComparison.Ordinal))
        {
            await ExecuteRegentOperation(card, index, choiceContext, cardPlay, state, operation, amount);
            return;
        }

        if (operation.Template == "N:Dex") { await PowerCmd.Apply<DexterityPower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card); return; }
        if (operation.Template == "N:Thorns") { await PowerCmd.Apply<ThornsPower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card); return; }
        if (operation.Template == "N:TempDex") { await PowerCmd.Apply<AnticipatePower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card); return; }
        if (operation.Template == "N:Intangible") { await PowerCmd.Apply<IntangiblePower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card); return; }
        if (operation.Template == "N:LoseDex") { await PowerCmd.Apply<DexterityPower>(choiceContext, card.Owner.Creature, -amount, card.Owner.Creature, card); return; }
        if (operation.Template == "N:KeepBlockNextTurn") { await PowerCmd.Apply<BlurPower>(choiceContext, card.Owner.Creature, 1m, card.Owner.Creature, card); return; }
        if (operation.Template == "N:NextTurnBlock") { await PowerCmd.Apply<BlockNextTurnPower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card); return; }
        if (operation.Template == "N:NextTurnEnergy") { await PowerCmd.Apply<EnergyNextTurnPower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card); return; }
        if (operation.Template == "N:NextTurnDraw") { await PowerCmd.Apply<DrawCardsNextTurnPower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card); return; }
        // Simple slotted Hand producers are handled by the shared dispatch above.
        if (operation.Template == "N:AllPoison") { await PowerCmd.Apply<PoisonPower>(choiceContext, combatState.HittableEnemies, amount, card.Owner.Creature, card); return; }
        if (operation.Template == "N:AllWeak") { await PowerCmd.Apply<WeakPower>(choiceContext, combatState.HittableEnemies, amount, card.Owner.Creature, card); return; }
        if (operation.Template == "N:AllTempStrengthLoss")
        {
            foreach (var enemy in combatState.HittableEnemies)
                await PowerCmd.Apply<PiercingWailPower>(choiceContext, enemy, amount, card.Owner.Creature, card);
            return;
        }
        if (operation.Template == "N:RandomPoison")
        {
            var hits = RuntimeSpecValue(card, index, "hits", 1);
            for (var hit = 0; hit < hits; hit++)
            {
                var enemy = card.Owner.RunState.Rng.CombatTargets.NextItem(combatState.HittableEnemies);
                if (enemy is not null) await PowerCmd.Apply<PoisonPower>(choiceContext, enemy, amount, card.Owner.Creature, card);
            }
            return;
        }
        if (operation.Template == "N:BlockEqualAllPoison")
        {
            var block = combatState.HittableEnemies.Sum(enemy => enemy.GetPower<PoisonPower>()?.Amount ?? 0);
            await CreatureCmd.GainBlock(card.Owner.Creature, block, ValueProp.Move, cardPlay);
            return;
        }
        if (operation.Template is "N:Self" or "N:StrengthPerTargetVulnerable")
        {
            if (runtimeSpec!.Variant == "plating")
                await PowerCmd.Apply<PlatingPower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card);
            else
            {
                var targetScaled = runtimeSpec.Variant == "strength_per_target_vulnerable";
                // A normal targeted play always carries CardPlay.Target. Keep it as a fallback as well as the
                // execution state's temporary target so nested execution cannot silently turn this into a no-op.
                var strengthTarget = state.Target ?? cardPlay.Target;
                if (targetScaled && strengthTarget is null)
                {
                    Log.Warn($"[AutoAnthony] Slot {card.Definition.Slot} could not gain target-scaled Strength because no enemy target was supplied.");
                    return;
                }
                var strength = targetScaled
                    ? (strengthTarget!.GetPower<VulnerablePower>()?.Amount ?? 0) * Math.Max(1, amount)
                    : amount;
                await PowerCmd.Apply<StrengthPower>(choiceContext, card.Owner.Creature, strength, card.Owner.Creature, card);
                if (targetScaled)
                    if (ChaosDiagnostics.VerboseRuntime)
                        Log.Info($"[AutoAnthony] Slot {card.Definition.Slot} gained {strength} Strength from {strengthTarget!.LogName}'s Vulnerable.");
            }
            return;
        }
        if (operation.Template == "T:Apply")
        {
            if (state.Target is null) return;
            if (runtimeSpec!.Variant == "vulnerable_double")
            {
                var current = state.Target.GetPower<VulnerablePower>()?.Amount ?? 0;
                if (current > 0) await PowerCmd.Apply<VulnerablePower>(choiceContext, state.Target, current, card.Owner.Creature, card);
            }
            else if (runtimeSpec.Variant == "vulnerable")
                await PowerCmd.Apply<VulnerablePower>(choiceContext, state.Target, amount, card.Owner.Creature, card);
            else if (runtimeSpec.Variant == "weak")
                await PowerCmd.Apply<WeakPower>(choiceContext, state.Target, amount, card.Owner.Creature, card);
            else if (runtimeSpec.Variant is "strength_loss" or "strength_loss_this_turn")
                await PowerCmd.Apply<ManglePower>(choiceContext, state.Target, amount, card.Owner.Creature, card);
            else if (runtimeSpec.Variant == "strength_gain")
                await PowerCmd.Apply<StrengthPower>(choiceContext, state.Target, amount, card.Owner.Creature, card);
            return;
        }
        if (operation.Template == "T:Poison")
        {
            if (state.Target is not null) await PowerCmd.Apply<PoisonPower>(choiceContext, state.Target, amount, card.Owner.Creature, card);
            return;
        }
        if (operation.Template is "T:XStrengthLoss" or "T:XWeak")
        {
            if (state.Target is null) return;
            var x = RuntimeSpecValue(card, index, "amount", card.ResolveEffectEnergyXValue());
            if (operation.Template == "T:XStrengthLoss") await PowerCmd.Apply<StrengthPower>(choiceContext, state.Target, -x, card.Owner.Creature, card);
            else await PowerCmd.Apply<WeakPower>(choiceContext, state.Target, x, card.Owner.Creature, card);
            return;
        }
        if (operation.Template == "T:Strangle")
        {
            if (state.Target is not null) await PowerCmd.Apply<StranglePower>(choiceContext, state.Target, amount, card.Owner.Creature, card);
            return;
        }
        if (operation.Template == "T:RemoveBlockAndArtifact")
        {
            if (state.Target is null) return;
            await CreatureCmd.LoseBlock(choiceContext, state.Target, state.Target.Block, card.Owner.Creature);
            if (state.Target.HasPower<ArtifactPower>()) await PowerCmd.Remove<ArtifactPower>(state.Target);
            return;
        }
        if (operation.Template == "N:Exhaust") { await Exhaust(card, operation, runtimeSpec!, amount, choiceContext, state); return; }
        if (operation.Template is "N:Create" or "N:CreateCurrentCharacterCardInHand")
        {
            await CreateCard(card, index, operation, runtimeSpec!, amount, state);
            return;
        }
        if (operation.Template == "N:Move") { await MoveCard(card, operation, runtimeSpec!, choiceContext, state); return; }
        if (operation.Template == "N:RetaliateDamage")
        {
            if (state.Target is not null)
            {
                await CreatureCmd.Damage(choiceContext, state.Target, amount, ValueProp.Unpowered, card.Owner.Creature, card, cardPlay);
                if (ChaosDiagnostics.VerboseRuntime)
                    Log.Info($"[AutoAnthony] Slot {card.Definition.Slot} dealt {amount} retaliatory damage to {state.Target.LogName}.");
            }
            else
                Log.Warn($"[AutoAnthony] Slot {card.Definition.Slot} had retaliatory damage without an attacker target.");
            return;
        }

        await ExecuteIndependent(card, index, operation, amount, choiceContext, cardPlay, state);
    }

    /// <summary>
    /// Localization-independent execution for shared immediate actions. Character-prefixed templates deliberately
    /// enter this path before their legacy character switch, so identical actions cannot drift between pools.
    /// Proxy operations remain native and are filtered by the caller before reaching this method.
    /// </summary>
    private static async Task<bool> TryExecuteStructuredCommon(ChaosCardModel card,
        PlayerChoiceContext choiceContext, CardPlay cardPlay, ChaosExecutionState state,
        OperationRuntimeSpec spec, int amount)
    {
        // Structured Target is authoritative for the recipient. Route every self Power before variants shared
        // with enemy effects: otherwise a targetless trigger can turn a valid self effect into a successful no-op,
        // or a future self temporary-Strength route can accidentally use permanent StrengthPower.
        if (await TryExecuteStructuredSelfPower(card, choiceContext, cardPlay, state, spec, amount)) return true;

        switch (spec.Opcode, spec.Variant)
        {
            case ("gain_block", "immediate"):
            {
                var block = ApplyBlockModifiers(card, amount);
                var blockProps = state.IsTriggered ? ValueProp.Unpowered : ValueProp.Move;
                var blockCardPlay = state.IsTriggered ? null : cardPlay;
                await CreatureCmd.GainBlock(card.Owner.Creature, block, blockProps, blockCardPlay);
                if (ChaosDiagnostics.VerboseRuntime)
                    Log.Info($"[AutoAnthony] Slot {card.Definition.Slot} gained {block} triggered Block.");
                return true;
            }
            case ("gain_block", "next_turn"):
                await PowerCmd.Apply<BlockNextTurnPower>(choiceContext, card.Owner.Creature, amount,
                    card.Owner.Creature, card);
                return true;
            case ("draw_cards", "immediate"):
                state.LastDrawnCards.Clear();
                state.LastDrawnCards.AddRange(await CardPileCmd.Draw(choiceContext, amount, card.Owner));
                return true;
            case ("draw_cards", "next_turn"):
                await PowerCmd.Apply<DrawCardsNextTurnPower>(choiceContext, card.Owner.Creature, amount,
                    card.Owner.Creature, card);
                return true;
            case ("gain_energy", "immediate"):
                await PlayerCmd.GainEnergy(amount, card.Owner);
                return true;
            case ("gain_energy", "next_turn"):
                await PowerCmd.Apply<EnergyNextTurnPower>(choiceContext, card.Owner.Creature, amount,
                    card.Owner.Creature, card);
                return true;
            case ("gain_stars", "immediate"):
                await PlayerCmd.GainStars(amount, card.Owner);
                return true;
            case ("lose_hp", "immediate"):
                await CreatureCmd.Damage(choiceContext, card.Owner.Creature, amount,
                    ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move, card, cardPlay);
                return true;
            case ("heal", "immediate"):
                await CreatureCmd.Heal(card.Owner.Creature, amount);
                return true;
            case ("discard_card", "selected") when spec.SourceZone == "hand":
                await Discard(card, amount, choiceContext, state);
                return true;
            case ("discard_card", "all") when spec.SourceZone == "hand":
                await Discard(card, int.MaxValue, choiceContext, state);
                return true;
            case ("apply_power", "vulnerable_double"):
            {
                var target = state.Target ?? cardPlay.Target;
                if (target is null) return true;
                var current = target.GetPower<VulnerablePower>()?.Amount ?? 0;
                if (current > 0)
                    await PowerCmd.Apply<VulnerablePower>(choiceContext, target, current,
                        card.Owner.Creature, card);
                return true;
            }
            case ("apply_power", "vulnerable"):
            case ("apply_power", "weak"):
            case ("apply_power", "strength_loss"):
            case ("apply_power", "strength_loss_this_turn"):
            case ("apply_power", "strength_gain"):
            {
                var target = state.Target ?? cardPlay.Target;
                if (target is null) return true;
                if (spec.Variant == "vulnerable")
                    await PowerCmd.Apply<VulnerablePower>(choiceContext, target, amount,
                        card.Owner.Creature, card);
                else if (spec.Variant == "weak")
                    await PowerCmd.Apply<WeakPower>(choiceContext, target, amount,
                        card.Owner.Creature, card);
                else if (spec.Variant == "strength_loss_this_turn")
                    await PowerCmd.Apply<ManglePower>(choiceContext, target, amount,
                        card.Owner.Creature, card);
                else if (spec.Variant == "strength_loss")
                    await PowerCmd.Apply<StrengthPower>(choiceContext, target, -amount,
                        card.Owner.Creature, card);
                else
                    await PowerCmd.Apply<StrengthPower>(choiceContext, target, amount,
                        card.Owner.Creature, card);
                return true;
            }
            default:
                return false;
        }
    }

    private static async Task<bool> TryExecuteStructuredSelfPower(ChaosCardModel card,
        PlayerChoiceContext choiceContext, CardPlay cardPlay, ChaosExecutionState state,
        OperationRuntimeSpec spec, int amount)
    {
        var route = StructuredSelfPowerRoute(spec);
        if (route is null) return false;
        switch (route)
        {
            case "dexterity_gain":
                await PowerCmd.Apply<DexterityPower>(choiceContext, card.Owner.Creature, amount,
                    card.Owner.Creature, card);
                return true;
            case "dexterity_loss":
                await PowerCmd.Apply<DexterityPower>(choiceContext, card.Owner.Creature, -amount,
                    card.Owner.Creature, card);
                return true;
            case "dexterity_gain_this_turn":
                await PowerCmd.Apply<AnticipatePower>(choiceContext, card.Owner.Creature, amount,
                    card.Owner.Creature, card);
                return true;
            case "doom":
                await PowerCmd.Apply<DoomPower>(choiceContext, card.Owner.Creature, amount,
                    card.Owner.Creature, card);
                return true;
            case "focus_loss":
                await PowerCmd.Apply<FocusPower>(choiceContext, card.Owner.Creature, -amount,
                    card.Owner.Creature, card);
                return true;
            case "focus_loss_this_turn":
                await ApplyBoundPower<ChaosTemporaryFocusDownPower>(card, choiceContext, amount);
                return true;
            case "thorns":
                await PowerCmd.Apply<ThornsPower>(choiceContext, card.Owner.Creature, amount,
                    card.Owner.Creature, card);
                return true;
            case "intangible":
                await PowerCmd.Apply<IntangiblePower>(choiceContext, card.Owner.Creature, amount,
                    card.Owner.Creature, card);
                return true;
            case "blur":
                await PowerCmd.Apply<BlurPower>(choiceContext, card.Owner.Creature, 1m,
                    card.Owner.Creature, card);
                return true;
            case "plating":
                await PowerCmd.Apply<PlatingPower>(choiceContext, card.Owner.Creature, amount,
                    card.Owner.Creature, card);
                return true;
            case "strength":
                await PowerCmd.Apply<StrengthPower>(choiceContext, card.Owner.Creature, amount,
                    card.Owner.Creature, card);
                return true;
            case "strength_this_turn":
                await PowerCmd.Apply<SetupStrikePower>(choiceContext, card.Owner.Creature, amount,
                    card.Owner.Creature, card);
                return true;
            case "strength_loss":
                await PowerCmd.Apply<StrengthPower>(choiceContext, card.Owner.Creature, -amount,
                    card.Owner.Creature, card);
                return true;
            case "strength_loss_this_turn":
                await PowerCmd.Apply<ManglePower>(choiceContext, card.Owner.Creature, amount,
                    card.Owner.Creature, card);
                return true;
            case "strength_per_target_vulnerable":
            {
                // The recipient is self, while the selected/event enemy supplies the Vulnerable count.
                var target = state.Target ?? cardPlay.Target;
                if (target is null)
                {
                    Log.Warn($"[AutoAnthony] Slot {card.Definition.Slot} could not gain target-scaled Strength because no enemy target was supplied.");
                    return true;
                }
                var strength = (target.GetPower<VulnerablePower>()?.Amount ?? 0) * Math.Max(1, amount);
                await PowerCmd.Apply<StrengthPower>(choiceContext, card.Owner.Creature, strength,
                    card.Owner.Creature, card);
                if (ChaosDiagnostics.VerboseRuntime)
                    Log.Info($"[AutoAnthony] Slot {card.Definition.Slot} gained {strength} Strength from {target.LogName}'s Vulnerable.");
                return true;
            }
            default:
                return false;
        }
    }

    internal static string? StructuredSelfPowerRoute(OperationRuntimeSpec spec)
    {
        if (spec is not { Opcode: "apply_power", Target: "self" }) return null;
        return spec.Variant is "dexterity_gain" or "dexterity_loss" or "dexterity_gain_this_turn"
            or "doom" or "focus_loss" or "focus_loss_this_turn" or "thorns" or "intangible" or "blur"
            or "plating" or "strength" or "strength_this_turn" or "strength_loss"
            or "strength_loss_this_turn" or "strength_per_target_vulnerable"
                ? spec.Variant
                : null;
    }

    private static async Task<bool> TryExecuteStructuredDamage(ChaosCardModel card, int operationIndex,
        PlayerChoiceContext choiceContext, CardPlay cardPlay, ChaosExecutionState state,
        OperationRuntimeSpec spec, int amount, ICombatState combatState)
    {
        if (spec.Opcode != "deal_damage") return false;

        var baseHits = Math.Max(0, RuntimeSpecValue(card, operationIndex, "hits", 1));
        if (spec.Variant == "selected_per_energy_spent_this_turn"
            && !(card.Generated.Operations[operationIndex].Parameters
                .TryGetValue("triggerIndex", out var linkedTriggerIndex)
                && linkedTriggerIndex >= 0 && linkedTriggerIndex < operationIndex
                && card.Generated.Operations[linkedTriggerIndex].Template == "D:ForEachEnergySpentThisTurn"
                && card.Generated.Operations[linkedTriggerIndex].Scope == OperationScope.ConditionalTrigger))
        {
            var thresholdIndex = card.Generated.Operations[operationIndex].Parameters
                .TryGetValue("triggerIndex", out var triggerIndex)
                && triggerIndex >= 0 && triggerIndex < operationIndex
                    ? triggerIndex
                    : operationIndex > 0
                      && card.Generated.Operations[operationIndex - 1].Template == "D:ForEachEnergySpentThisTurn"
                        ? operationIndex - 1
                        : -1;
            var threshold = thresholdIndex >= 0
                ? RuntimeSpecValue(card, thresholdIndex, "threshold", 2)
                : 2;
            var energySpent = CombatManager.Instance.History.Entries.OfType<EnergySpentEntry>()
                .Where(entry => entry.Actor == card.Owner.Creature && entry.HappenedThisTurn(combatState))
                .Sum(entry => entry.Amount) - Math.Max(0, cardPlay.Resources.EnergySpent);
            energySpent = Math.Max(0, energySpent);
            baseHits = energySpent / Math.Max(1, threshold);
        }
        if (spec.Variant == "selected_energy_x_threshold")
        {
            var thresholdIndex = card.Generated.Operations
                .Select((candidate, index) => (candidate, index))
                .Where(item => EffectiveRuntimeSpec(card, item.index).Condition?.Kind == "energy_x_at_least")
                .Select(item => (int?)item.index).FirstOrDefault();
            var threshold = thresholdIndex is { } linkedThresholdIndex
                ? RuntimeSpecValue(card, linkedThresholdIndex, "threshold", 0)
                : RuntimeSpecValue(card, operationIndex, "threshold", 0);
            if (threshold > 0 && baseHits >= threshold
                && (spec.Flags.Contains("legacy_inline_double_x")
                    || card.Generated.Operations.Select((candidate, index) => (candidate, index))
                        .Any(item => EffectiveRuntimeSpec(card, item.index).Variant == "r_doubleenergyx")))
                baseHits *= 2;
        }

        var (damage, hits) = DamageAndHits(card, amount, state, baseHits, operationIndex);
        if (hits == 0) return true;
        switch (spec.Target)
        {
            case "selected_enemy":
            case "event_enemy":
            {
                var target = state.Target ?? cardPlay.Target;
                if (target is null) return true;
                if (state.IsTriggered)
                {
                    var results = new List<DamageResult>();
                    for (var hit = 0; hit < hits; hit++)
                        results.AddRange(await CreatureCmd.Damage(choiceContext, target, damage,
                            ValueProp.Unpowered | ValueProp.Move, card.Owner.Creature, card, cardPlay));
                    CaptureDamageResults(state, results);
                    return true;
                }
                var command = await DamageCmd.Attack(damage).WithHitCount(hits).FromCard(card, cardPlay)
                    .Targeting(target).WithHitFx(card.Definition.HitFx).Execute(choiceContext);
                CaptureDamageResults(state, command.Results.SelectMany(result => result));
                return true;
            }
            case "all_enemies":
            {
                if (state.IsTriggered)
                {
                    var results = new List<DamageResult>();
                    for (var hit = 0; hit < hits; hit++)
                        results.AddRange(await CreatureCmd.Damage(choiceContext, combatState.HittableEnemies, damage,
                            ValueProp.Unpowered | ValueProp.Move, card.Owner.Creature, card, cardPlay));
                    CaptureDamageResults(state, results);
                    return true;
                }
                var repeatOnKill = card.Generated.Operations.Select((candidate, index) =>
                        EffectiveRuntimeSpec(card, index))
                    .Any(candidate => candidate.Opcode == "template_modifier"
                                      && candidate.Variant == "m_repeatareaonkill");
                if (repeatOnKill)
                {
                    var repeats = hits;
                    var totalDamage = 0m;
                    while (repeats-- > 0)
                    {
                        var echo = await DamageCmd.Attack(damage).FromCard(card, cardPlay)
                            .TargetingAllOpponents(combatState).WithHitFx(card.Definition.HitFx)
                            .Execute(choiceContext);
                        var results = echo.Results.SelectMany(result => result).ToArray();
                        totalDamage += results.Sum(result => result.TotalDamage + result.OverkillDamage);
                        repeats += results.Count(result => result.WasTargetKilled);
                        state.LastAttackKilled |= results.Any(result => result.WasTargetKilled);
                    }
                    state.LastDamageDealt = decimal.ToInt32(totalDamage);
                    return true;
                }
                var command = await DamageCmd.Attack(damage).WithHitCount(hits).FromCard(card, cardPlay)
                    .TargetingAllOpponents(combatState).WithHitFx(card.Definition.HitFx).Execute(choiceContext);
                CaptureDamageResults(state, command.Results.SelectMany(result => result));
                return true;
            }
            case "random_enemy":
            {
                if (state.IsTriggered)
                {
                    var results = new List<DamageResult>();
                    for (var hit = 0; hit < hits; hit++)
                    {
                        var target = card.Owner.RunState.Rng.CombatTargets.NextItem(combatState.HittableEnemies);
                        if (target is not null)
                            results.AddRange(await CreatureCmd.Damage(choiceContext, target, damage,
                                ValueProp.Unpowered | ValueProp.Move, card.Owner.Creature, card, cardPlay));
                    }
                    CaptureDamageResults(state, results);
                    return true;
                }
                var command = await DamageCmd.Attack(damage).WithHitCount(hits).FromCard(card, cardPlay)
                    .TargetingRandomOpponents(combatState).WithHitFx(card.Definition.HitFx).Execute(choiceContext);
                CaptureDamageResults(state, command.Results.SelectMany(result => result));
                return true;
            }
            default:
                throw new InvalidOperationException($"Unsupported structured damage target: {spec.Target}");
        }
    }

    private static void CaptureDamageResults(ChaosExecutionState state, IEnumerable<DamageResult> results)
    {
        var materialized = results as DamageResult[] ?? results.ToArray();
        state.LastAttackKilled = materialized.Any(result => result.WasTargetKilled);
        state.LastDamageDealt = decimal.ToInt32(materialized.Sum(result =>
            result.TotalDamage + result.OverkillDamage));
    }

    private static async Task ExecuteColorlessOperation(ChaosCardModel card, int operationIndex,
        PlayerChoiceContext choiceContext, CardPlay cardPlay, ChaosExecutionState state,
        GeneratorOperation operation, int amount)
    {
        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        if (combatState is null) return;
        switch (operation.Template)
        {
            case "CL:TargetLoseStrengthThisTurn":
                if (state.Target is not null)
                    await PowerCmd.Apply<DarkShacklesPower>(choiceContext, state.Target, amount,
                        card.Owner.Creature, card);
                return;
            case "CL:TransformSelectedHandCards":
            {
                var selected = (await SelectFromHandIfAny(choiceContext, card.Owner,
                    new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt, amount),
                    candidate => candidate.IsTransformable, card)).ToList();
                if (selected.Count == 0) return;
                // CardCmd's multi-card path snapshots every source pile/index before removing anything, then
                // restores all replacements and hand nodes as one transaction. Running TransformToRandom once per
                // selected card exits hand-select mode after the first replacement and leaves the remaining
                // selected holders detached/out of sync (visible as transformed cards failing to return to hand).
                var results = (await CardCmd.Transform(
                    selected.Select(selectedCard => new CardTransformation(selectedCard)),
                    card.Owner.RunState.Rng.CombatCardSelection,
                    TransformPreviewStyle(choiceContext))).ToList();
                if (results.Count != selected.Count || results.Any(result => result.cardAdded.Pile?.Type != PileType.Hand))
                    Log.Error($"[AutoAnthony] Batch hand transform expected {selected.Count} replacement(s) in Hand, "
                              + $"but received {results.Count}: "
                              + string.Join(", ", results.Select(result =>
                                  $"{result.cardAdded.Id}@{result.cardAdded.Pile?.Type.ToString() ?? "no pile"}")));
                return;
            }
            case "CL:RetainHandThisTurn":
                await PowerCmd.Apply<RetainHandPower>(choiceContext, card.Owner.Creature, 1,
                    card.Owner.Creature, card);
                return;
            case "CL:GainBlockEqualDamage":
                await CreatureCmd.GainBlock(card.Owner.Creature, state.LastDamageDealt, ValueProp.Move, cardPlay);
                return;
            case "CL:AddRandomAttackToHand":
            {
                var count = ExecutableGeneratedCardCount(amount);
                if (count == 0) return;
                var candidates = card.Owner.Character.CardPool.GetUnlockedCards(card.Owner.UnlockState,
                    card.Owner.RunState.CardMultiplayerConstraint).Where(candidate => candidate.Type == CardType.Attack);
                var generated = CardFactory.GetForCombat(card.Owner, candidates, count,
                    card.Owner.RunState.Rng.CombatCardGeneration).ToArray();
                UpgradeGeneratedCards(card, operationIndex, generated);
                foreach (var created in generated)
                    await CardPileCmd.AddGeneratedCardToCombat(created, PileType.Hand, card.Owner);
                return;
            }
            case "CL:AddRandomColorlessToHand":
            {
                var count = ExecutableGeneratedCardCount(amount);
                if (count == 0) return;
                var candidates = ModelDb.CardPool<ColorlessCardPool>().GetUnlockedCards(card.Owner.UnlockState,
                    card.Owner.RunState.CardMultiplayerConstraint).Where(candidate => candidate.Id != card.Id);
                var generated = CardFactory.GetDistinctForCombat(card.Owner, candidates, count,
                    card.Owner.RunState.Rng.CombatCardGeneration).ToArray();
                UpgradeGeneratedCards(card, operationIndex, generated);
                foreach (var created in generated)
                    await CardPileCmd.AddGeneratedCardToCombat(created, PileType.Hand, card.Owner);
                return;
            }
            case "CL:AddRandomZeroCostCardsToHand":
            {
                var count = ExecutableGeneratedCardCount(amount);
                if (count == 0) return;
                var candidates = card.Owner.Character.CardPool.GetUnlockedCards(card.Owner.UnlockState,
                    card.Owner.RunState.CardMultiplayerConstraint).Where(candidate =>
                    candidate.EnergyCost is { Canonical: 0, CostsX: false });
                var generated = CardFactory.GetForCombat(card.Owner, candidates, count,
                    card.Owner.RunState.Rng.CombatCardGeneration).ToArray();
                UpgradeGeneratedCards(card, operationIndex, generated);
                foreach (var created in generated)
                    await CardPileCmd.AddGeneratedCardToCombat(created, PileType.Hand, card.Owner);
                return;
            }
            case "CL:GainGold":
                await PlayerCmd.GainGold(amount, card.Owner);
                return;
            case "CL:PlayTopDrawCard":
            {
                var top = PileType.Draw.GetPile(card.Owner).Cards.FirstOrDefault();
                if (top is not null) await CardCmd.AutoPlay(choiceContext, top, null);
                return;
            }
            case "CL:PutEventCardOnDrawTop":
                if (state.EventCard?.Pile?.Type == PileType.Discard)
                    await CardPileCmd.Add(state.EventCard, PileType.Draw, CardPilePosition.Top);
                return;
            case "CL:DamageOtherEnemiesEqual":
            {
                var others = combatState.HittableEnemies.Where(enemy => enemy != state.Target).ToArray();
                if (others.Length > 0 && state.LastDamageDealt > 0)
                    await CreatureCmd.Damage(choiceContext, others, state.LastDamageDealt,
                        ValueProp.Unpowered | ValueProp.Move, card.Owner.Creature, card, cardPlay);
                return;
            }
            case "CL:NoBlockFromCards":
                await PowerCmd.Apply<NoBlockPower>(choiceContext, card.Owner.Creature, amount,
                    card.Owner.Creature, card);
                return;
            case "CL:GainVigor":
                await PowerCmd.Apply<VigorPower>(choiceContext, card.Owner.Creature, amount,
                    card.Owner.Creature, card);
                return;
            case "CL:GainBlockEqualCurrent":
                await CreatureCmd.GainBlock(card.Owner.Creature, card.Owner.Creature.Block, ValueProp.Move, cardPlay);
                return;
            case "CL:GainNextTurnBlockEqualCurrent":
                await PowerCmd.Apply<BlockNextTurnPower>(choiceContext, card.Owner.Creature,
                    card.Owner.Creature.Block, card.Owner.Creature, card);
                return;
            case "CL:ExhaustUpToHandCards":
            {
                var selected = await SelectFromHandIfAny(choiceContext, card.Owner,
                    new CardSelectorPrefs(CardSelectorPrefs.ExhaustSelectionPrompt, 0, amount), null, card);
                foreach (var selectedCard in selected) await CardCmd.Exhaust(choiceContext, selectedCard);
                return;
            }
            case "CL:RollingAllDamage":
            {
                var baseDamage = state.EventAmount > 0 ? state.EventAmount : amount;
                var (damage, hits) = DamageAndHits(card, baseDamage, state);
                for (var hit = 0; hit < hits; hit++)
                    await CreatureCmd.Damage(choiceContext, combatState.HittableEnemies, damage,
                        ValueProp.Unpowered, card.Owner.Creature, card, cardPlay);
                return;
            }
            case "CL:DrawToFullHand":
                await CardPileCmd.Draw(choiceContext,
                    Math.Max(0, CardPile.MaxCardsInHand - PileType.Hand.GetPile(card.Owner).Cards.Count), card.Owner);
                return;
            case "CL:MoveSelectedSkillDrawToHand":
            case "CL:MoveSelectedAttackDrawToHand":
            {
                var type = operation.Template.EndsWith("SkillDrawToHand", StringComparison.Ordinal)
                    ? CardType.Skill : CardType.Attack;
                var selected = (await SelectFromCombatPileIfAny(choiceContext, PileType.Draw.GetPile(card.Owner),
                    card.Owner, new CardSelectorPrefs(SelectionPrompt("DISCARD_TO_HAND"), 1), candidate => candidate.Type == type)).FirstOrDefault();
                if (selected is not null) await CardPileCmd.Add(selected, PileType.Hand);
                return;
            }
            case "CL:ChooseFromRandomDrawCards":
            {
                var optionCount = ExecutableGeneratedCardCount(amount);
                if (optionCount == 0) return;
                var options = PileType.Draw.GetPile(card.Owner).Cards.ToList().StableShuffle(
                    card.Owner.RunState.Rng.CombatCardSelection).Take(Math.Min(optionCount, 4)).ToHashSet();
                var selected = (await SelectFromCombatPileIfAny(choiceContext, PileType.Draw.GetPile(card.Owner),
                    card.Owner, new CardSelectorPrefs(SelectionPrompt("DISCARD_TO_HAND"), 1), options.Contains)).FirstOrDefault();
                if (selected is not null) await CardPileCmd.Add(selected, PileType.Hand);
                return;
            }
            case "CL:ApplyWeakAll":
                await PowerCmd.Apply<WeakPower>(choiceContext, combatState.HittableEnemies, amount,
                    card.Owner.Creature, card);
                return;
            case "CL:ApplyVulnerableAll":
                await PowerCmd.Apply<VulnerablePower>(choiceContext, combatState.HittableEnemies, amount,
                    card.Owner.Creature, card);
                return;
            case "CL:ChooseDrawCardToHand":
            {
                var count = ExecutableGeneratedCardCount(amount);
                if (count == 0) return;
                var selected = await SelectFromCombatPileIfAny(choiceContext, PileType.Draw.GetPile(card.Owner),
                    card.Owner, new CardSelectorPrefs(SelectionPrompt("DISCARD_TO_HAND"), count));
                foreach (var selectedCard in selected) await CardPileCmd.Add(selectedCard, PileType.Hand);
                return;
            }
            case "CL:PutSelectedHandCardOnDrawTop":
            {
                var selected = SelectedCard(operation, state);
                if (selected is not null) await CardPileCmd.Add(selected, PileType.Draw, CardPilePosition.Top);
                return;
            }
            case "CL:ReturnThisToHand":
            case "CL:IncreaseRollingDamage":
            case "CL:AtNextTurnStart":
            case "CL:IfFatal":
            case "CL:IfNoAttacksInHand":
            case "CL:IfHandEmpty":
            case "CL:EveryCardsDrawn":
            case "CL:EveryCardsPlayedThisTurn":
            case "CL:WheneverAttackPlayed":
            case "CL:FirstAttackOrSkillEachTurn":
            case "CL:WheneverDrawPileShuffled":
            case "CL:AfterTurns":
            case "CL:DieOnUnblockedAttack":
            case "CL:ForEachCardPlayedCombat":
            case "CL:ForEachDrawPileCard":
            case "CL:BonusPerUniqueDebuff":
                return;
            default:
                throw new InvalidOperationException($"Unimplemented Colorless operation: {operation.Template}");
        }
    }

    private static async Task ExecuteRegentOperation(ChaosCardModel card, int operationIndex,
        PlayerChoiceContext choiceContext, CardPlay cardPlay, ChaosExecutionState state,
        GeneratorOperation operation, int amount)
    {
        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        if (combatState is null) return;
        var playerCombatState = card.Owner.PlayerCombatState;
        switch (operation.Template)
        {
            case "R:Forge": await ForgeCmd.Forge(amount, card.Owner, card); return;
            case "R:ForgePerPriorHit":
            {
                if (state.Target is null) return;
                var hits = CombatManager.Instance.History.Entries.OfType<DamageReceivedEntry>().Count(entry =>
                    entry.Receiver == state.Target && entry.Dealer == card.Owner.Creature
                    && entry.Result.Props.IsPoweredAttack() && entry.HappenedThisTurn(combatState));
                // The just-completed damage operation is already present in history.
                await ForgeCmd.Forge(Math.Max(0, hits - 1) * amount, card.Owner, card);
                return;
            }
            case "R:AddDebrisToHand":
                await CreateDerivatives(card, operationIndex, operation, PileType.Hand,
                    ExecutableGeneratedCardCount(amount));
                return;
            case "R:FillHandWithDebris":
            {
                var hand = PileType.Hand.GetPile(card.Owner);
                var returnsThisToHand = !card.Keywords.Contains(CardKeyword.Exhaust)
                    && card.Generated.Operations.Any(candidate => candidate.Template == "R:ReturnThisToHand");
                var targetCount = FillHandTargetCount(returnsThisToHand);
                while (hand.Cards.Count < targetCount)
                {
                    var before = hand.Cards.Count;
                    await CreateDerivatives(card, operationIndex, operation, PileType.Hand, 1);
                    // A missing/invalid derivative must not turn “fill your hand” into an infinite async loop.
                    // The runtime producer audit normally makes this impossible, but old snapshots can still
                    // contain a derivative that a newer version no longer understands.
                    if (hand.Cards.Count <= before)
                    {
                        Log.Warn($"[AutoAnthony] Fill-hand operation {operation.Template} on slot {card.Definition.Slot} produced no card; stopped at {before}/{targetCount}.");
                        break;
                    }
                }
                return;
            }
            case "R:RetainHandThisTurn":
                await PowerCmd.Apply<RetainHandPower>(choiceContext, card.Owner.Creature, 1, card.Owner.Creature, card);
                return;
            case "R:DiscardTopOfDraw":
                foreach (var candidate in PileType.Draw.GetPile(card.Owner).Cards.Take(Math.Max(1, amount)).ToList())
                    await CardPileCmd.Add(candidate, PileType.Discard);
                return;
            case "R:MoveDiscardCardToDrawTop":
            {
                var selected = (await SelectFromCombatPileIfAny(choiceContext, PileType.Discard.GetPile(card.Owner),
                    card.Owner, new CardSelectorPrefs(SelectionPrompt("DISCARD_TO_DRAW"), 1))).FirstOrDefault();
                if (selected is not null) await CardPileCmd.Add(selected, PileType.Draw, CardPilePosition.Top);
                return;
            }
            case "R:EnemiesLoseStrengthThisTurn":
                foreach (var enemy in combatState.HittableEnemies)
                    await PowerCmd.Apply<PiercingWailPower>(choiceContext, enemy, amount, card.Owner.Creature, card);
                return;
            case "R:PlaySelectedSkillMultipleTimes":
            {
                var repeats = Math.Max(1, amount);
                var selected = SelectedCard(operation, state);
                if (selected is null && string.IsNullOrWhiteSpace(operation.CardTargetSlot))
                {
                    var prefs = new CardSelectorPrefs(SelectionPrompt("SELECT_HAND_SKILL"), 1)
                    {
                        PretendCardsCanBePlayed = true
                    };
                    selected = (await SelectFromHandIfAny(choiceContext, card.Owner,
                        prefs,
                        candidate => candidate.Type == CardType.Skill && !candidate.Keywords.Contains(CardKeyword.Unplayable), card)).FirstOrDefault();
                }
                if (selected is not null)
                    for (var i = 0; i < repeats; i++) await CardCmd.AutoPlay(choiceContext, selected, null);
                return;
            }
            case "R:PutSelectedHandCardsOnDraw":
            case "R:PutSelectedHandCardOnDraw":
            {
                var count = operation.Template.EndsWith("CardsOnDraw", StringComparison.Ordinal) ? Math.Max(1, amount) : 1;
                var selected = SelectedCards(operation, state);
                if (selected.Count == 0 && string.IsNullOrWhiteSpace(operation.CardTargetSlot))
                    selected = (await SelectFromHandIfAny(choiceContext, card.Owner,
                        new CardSelectorPrefs(SelectionPrompt("HAND_TO_DRAW"), count), null, card)).ToArray();
                await CardPileCmd.Add(selected, PileType.Draw, CardPilePosition.Top);
                return;
            }
            case "R:CopySelectedColorlessCard":
            {
                var selected = SelectedCard(operation, state);
                if (selected is null && string.IsNullOrWhiteSpace(operation.CardTargetSlot))
                    selected = (await SelectFromHandIfAny(choiceContext, card.Owner,
                        new CardSelectorPrefs(SelectionPrompt("SELECT_COLORLESS"), 1),
                        candidate => candidate.VisualCardPool.IsColorless, card)).FirstOrDefault();
                if (selected is not null)
                    await CardPileCmd.AddGeneratedCardToCombat(selected.CreateClone(), PileType.Hand, card.Owner);
                return;
            }
            case "R:AddRandomColorlessToHand":
            {
                var count = ExecutableGeneratedCardCount(amount);
                if (count == 0) return;
                var generated = CardFactory.GetDistinctForCombat(card.Owner,
                    ModelDb.CardPool<ColorlessCardPool>().GetUnlockedCards(card.Owner.UnlockState,
                        card.Owner.RunState.CardMultiplayerConstraint), count,
                    card.Owner.RunState.Rng.CombatCardGeneration).ToArray();
                UpgradeGeneratedCards(card, operationIndex, generated);
                await CardPileCmd.AddGeneratedCardsToCombat(generated, PileType.Hand, card.Owner);
                return;
            }
            case "R:ApplyWeakAll":
                await PowerCmd.Apply<WeakPower>(choiceContext, combatState.HittableEnemies, amount, card.Owner.Creature, card);
                return;
            case "R:ApplyVulnerableAll":
                await PowerCmd.Apply<VulnerablePower>(choiceContext, combatState.HittableEnemies, amount, card.Owner.Creature, card);
                return;
            case "R:GainStrengthThisTurn":
                await PowerCmd.Apply<FlexPotionPower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card);
                return;
            case "R:GainVigor":
                await PowerCmd.Apply<VigorPower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card);
                return;
            case "R:ReflectBlockedDamageThisTurn":
                await PowerCmd.Apply<ReflectPower>(choiceContext, card.Owner.Creature, 1, card.Owner.Creature, card);
                return;
            case "R:GainStrength":
                await PowerCmd.Apply<StrengthPower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card);
                return;
            case "R:TargetLoseStrengthThisTurn":
                if (state.Target is not null)
                    await PowerCmd.Apply<PiercingWailPower>(choiceContext, state.Target, amount, card.Owner.Creature, card);
                return;
            case "R:EnemiesLoseStrength":
                await PowerCmd.Apply<StrengthPower>(choiceContext, combatState.HittableEnemies, -amount, card.Owner.Creature, card);
                return;
            case "R:KingsSwordDoubleDamageThisTurn":
                if (state.Target is not null)
                    await PowerCmd.Apply<ConquerorPower>(choiceContext, state.Target, 1, card.Owner.Creature, card);
                return;
            case "R:KingsSwordHitsAllEnemies":
                await PowerCmd.Apply<SeekingEdgePower>(choiceContext, card.Owner.Creature, 1, card.Owner.Creature, card);
                return;
            case "R:PutKingsSwordInHand":
            {
                if (playerCombatState is null) return;
                var matching = playerCombatState.AllCards
                    .Where(candidate => ChaosDerivativeResolver.Matches(candidate, operation)).ToList();
                var recoverable = matching.Where(candidate => candidate.Pile?.Type != PileType.Hand).ToList();
                if (recoverable.Count > 0)
                {
                    await CardPileCmd.Add(recoverable, PileType.Hand);
                }
                else if (RecoveryRequiresCreation(matching.Count, recoverable.Count))
                {
                    // Summon Forth can assume that the Regent's unique Sovereign Blade already exists. A slotted
                    // derivative cannot: its supporting producer may not have been drawn or played yet. Treat the
                    // empty-source case as creation so replacements such as Minion Strike/Dive Bomb never become
                    // a silent no-op, while retaining the native "recover it from anywhere" behavior once a copy
                    // exists in combat.
                    await CreateDerivatives(card, operationIndex, operation, PileType.Hand, 1);
                }
                return;
            }
            // These operations are evaluated by modifiers, conditions, composite powers, or card lifecycle hooks.
            case "R:RepeatDamage": case "R:BonusPerStarCostCardInHand":
            case "R:RepeatPerSkillPlayedThisTurn": case "R:RepeatPerStarGainedThisTurn":
            case "R:BonusPerGeneratedCardThisCombat": case "R:NextTurn": case "R:IfFatal":
            case "R:AtTurnStartIfInExhaust":
            case "R:CostDownWhenDrawn": case "R:DamageUpWhenDrawn":
            case "R:ReturnAfterSkillsPlayed": case "R:ReturnThisToHand": case "R:PutThisOnDraw":
            case "R:PlayAtTurnEndIfTopOfDraw":
            case "R:DoubleEnergyX": case "R:DoubleEitherXAtThreshold":
                return;
            // The trigger above is deliberately inert here. Only its explicitly linked replay operation may
            // autoplay the source card; arbitrary linked effects must never inherit native Bombardment behaviour.
            case "R:PlayThisCard": await CardCmd.AutoPlay(choiceContext, card, null); return;
            default: throw new InvalidOperationException($"Unimplemented Regent operation: {operation.Template}");
        }
    }

    private static async Task ExecuteNecrobinderOperation(ChaosCardModel card, int operationIndex,
        PlayerChoiceContext choiceContext, CardPlay cardPlay, ChaosExecutionState state,
        GeneratorOperation operation, int amount)
    {
        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        if (combatState is null) return;
        var template = operation.Template;
        if (template.StartsWith("NCR:ApplyPower_", StringComparison.Ordinal))
        {
            var typeName = template["NCR:ApplyPower_".Length..];
            var type = typeof(DoomPower).Assembly.GetType($"MegaCrit.Sts2.Core.Models.Powers.{typeName}");
            if (type is null || !typeof(PowerModel).IsAssignableFrom(type))
                throw new InvalidOperationException($"Unknown Necrobinder power operation: {typeName}");
            var power = ModelDb.DebugPower(type).ToMutable();
            ChaosPowerVisuals.Bind(power, card.Definition);
            var target = template is "NCR:ApplyPower_SicEmPower" or "NCR:ApplyPower_OblivionPower"
                ? state.Target
                : card.Owner.Creature;
            if (target is not null)
                await PowerCmd.Apply(choiceContext, power, target, Math.Max(1, amount), card.Owner.Creature, card);
            return;
        }

        switch (template)
        {
            case "NCR:Summon": await OstyCmd.Summon(choiceContext, card.Owner, amount, card); return;
            case "NCR:SummonX":
            {
                var x = RuntimeSpecValue(card, operationIndex, "amount", card.ResolveEffectEnergyXValue());
                for (var i = 0; i < x; i++) await OstyCmd.Summon(choiceContext, card.Owner, 1, card);
                return;
            }
            case "NCR:OstyDamage":
            {
                if (card.Owner.Osty is not { } targetedOsty || state.Target is null) return;
                var (value, hits) = DamageAndHits(card, amount, state);
                var command = await DamageCmd.Attack(value).WithHitCount(hits).FromOsty(targetedOsty, card, cardPlay)
                    .Targeting(state.Target).WithHitFx(card.Definition.HitFx).Execute(choiceContext);
                var results = command.Results.SelectMany(result => result).ToArray();
                state.LastAttackKilled = results.Any(result => result.WasTargetKilled);
                state.LastDamageDealt = decimal.ToInt32(results.Sum(result => result.TotalDamage));
                return;
            }
            case "NCR:OstyAllDamage":
                if (card.Owner.Osty is { } areaOsty)
                {
                    // Osty's area attack is still this card's damage. Route it through the same calculation as
                    // targeted Osty attacks so permanent ExtraDamage and compatible damage/repeat modifiers apply.
                    var (value, hits) = DamageAndHits(card, amount, state);
                    var command = await DamageCmd.Attack(value).WithHitCount(hits)
                        .FromOsty(areaOsty, card, cardPlay)
                        .TargetingAllOpponents(combatState).WithHitFx(card.Definition.HitFx).Execute(choiceContext);
                    var results = command.Results.SelectMany(result => result).ToArray();
                    state.LastAttackKilled = results.Any(result => result.WasTargetKilled);
                    state.LastDamageDealt = decimal.ToInt32(results.Sum(result => result.TotalDamage));
                }
                return;
            case "NCR:ApplyDoomEqualDamage":
                if (state.Target is not null && state.LastDamageDealt > 0)
                    await PowerCmd.Apply<DoomPower>(choiceContext, state.Target, state.LastDamageDealt, card.Owner.Creature, card);
                return;
            case "NCR:NextTurnEnergy":
                await PowerCmd.Apply<EnergyNextTurnPower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card);
                return;
            case "NCR:IncreaseAllCardCostsThisTurn":
                foreach (var handCard in PileType.Hand.GetPile(card.Owner).Cards)
                    handCard.EnergyCost.AddThisTurn(amount);
                return;
            case "NCR:TargetHpLoss":
                if (state.Target is not null)
                    await CreatureCmd.Damage(choiceContext, state.Target, amount,
                        ValueProp.Unblockable | ValueProp.Unpowered, card.Owner.Creature, card, cardPlay);
                return;
            case "NCR:UnpoweredDamage":
                if (state.Target is not null)
                {
                    var (damage, hits) = DamageAndHits(card, amount, state);
                    for (var hit = 0; hit < hits; hit++)
                        await CreatureCmd.Damage(choiceContext, state.Target, damage,
                            ValueProp.Unpowered, card.Owner.Creature, card, cardPlay);
                }
                return;
            case "NCR:AddRandomEtherealCardToHand":
            {
                var count = ExecutableGeneratedCardCount(amount);
                if (count == 0) return;
                var pool = card.Owner.Character.CardPool.GetUnlockedCards(card.Owner.UnlockState,
                        card.Owner.RunState.CardMultiplayerConstraint)
                    .Where(candidate => candidate.Rarity is not (CardRarity.Basic or CardRarity.Ancient))
                    .ToArray();
                var generated = CardFactory.GetDistinctForCombat(card.Owner, pool, count,
                    card.Owner.RunState.Rng.CombatCardGeneration).ToArray();
                UpgradeGeneratedCards(card, operationIndex, generated);
                foreach (var candidate in generated) CardCmd.ApplyKeyword(candidate, CardKeyword.Ethereal);
                await CardPileCmd.AddGeneratedCardsToCombat(generated, PileType.Hand, card.Owner);
                return;
            }
            case "NCR:AddSweepingGazeToHand":
                await CreateDerivatives(card, operationIndex, operation, PileType.Hand,
                    ExecutableGeneratedCardCount(amount));
                return;
            case "NCR:CreateSoulInDraw":
                await CreateDerivatives(card, operationIndex, operation, PileType.Draw, amount, CardPilePosition.Random);
                return;
            case "NCR:CreateSoulInHand":
                await CreateDerivatives(card, operationIndex, operation, PileType.Hand, amount);
                return;
            case "NCR:CreateSoulInDiscard":
                await CreateDerivatives(card, operationIndex, operation, PileType.Discard, amount);
                return;
            case "NCR:CreateSoulInDrawX":
                await CreateDerivatives(card, operationIndex, operation, PileType.Draw,
                    RuntimeSpecValue(card, operationIndex, "amount", card.ResolveEffectEnergyXValue()),
                    CardPilePosition.Random);
                return;
            case "NCR:ExhaustSelectedDrawCard":
            {
                var selected = (await SelectFromCombatPileIfAny(choiceContext, PileType.Draw.GetPile(card.Owner), card.Owner,
                    new CardSelectorPrefs(CardSelectorPrefs.ExhaustSelectionPrompt, 1))).FirstOrDefault();
                if (selected is not null) await CardCmd.Exhaust(choiceContext, selected);
                return;
            }
            case "NCR:ApplyDoomAll":
                await PowerCmd.Apply<DoomPower>(choiceContext, combatState.HittableEnemies, amount, card.Owner.Creature, card);
                return;
            case "NCR:ApplySelfDoom":
                await PowerCmd.Apply<DoomPower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card);
                return;
            case "NCR:ApplyWeakAll":
                await PowerCmd.Apply<WeakPower>(choiceContext, combatState.HittableEnemies, amount, card.Owner.Creature, card);
                return;
            case "NCR:ApplyVulnerableAll":
                await PowerCmd.Apply<VulnerablePower>(choiceContext, combatState.HittableEnemies, amount, card.Owner.Creature, card);
                return;
            case "NCR:DoubleVulnerableWeak":
                if (state.Target is not null)
                {
                    var power = (PowerModel)ModelDb.Power<DebilitatePower>().ToMutable();
                    await PowerCmd.Apply(choiceContext, power, state.Target, Math.Max(1, amount),
                        card.Owner.Creature, card);
                }
                return;
            case "NCR:UpgradeRandomDiscardCards":
            {
                // Mirror Drain Power: take an exact random subset from the live discard pile, then preview every
                // upgraded card. The previous OrderBy(random) path mutated the cards without refreshing their
                // discard-pile presentation, which made the component appear to do nothing.
                var candidates = PileType.Discard.GetPile(card.Owner).Cards.Where(candidate => candidate.IsUpgradable)
                    .TakeRandom(Math.Max(0, amount), card.Owner.RunState.Rng.CombatCardSelection).ToList();
                foreach (var candidate in candidates)
                {
                    UpgradeExistingCombatCard(candidate, operation.Template);
                    CardCmd.Preview(candidate);
                }
                return;
            }
            case "NCR:KillEnemiesAtDoomThreshold":
                foreach (var enemy in combatState.HittableEnemies.ToList())
                    if ((enemy.GetPower<DoomPower>()?.Amount ?? 0) >= enemy.CurrentHp)
                        await CreatureCmd.Kill(enemy);
                return;
            // This is a live card rule, not an OnPlay effect. ChaosCardModel mirrors Flatten's
            // AfterCardEnteredCombat/AfterAttack hooks and applies SetThisTurn(0) as soon as it becomes true.
            case "NCR:SetCostZeroIfOstyAttacked": return;
            case "NCR:LoseStrength":
                await PowerCmd.Apply<StrengthPower>(choiceContext, card.Owner.Creature, -amount, card.Owner.Creature, card);
                return;
            case "NCR:MoveDiscardCardToHand":
            {
                var selected = (await SelectFromCombatPileIfAny(choiceContext, PileType.Discard.GetPile(card.Owner),
                    card.Owner, new CardSelectorPrefs(SelectionPrompt("DISCARD_TO_HAND"), 1))).FirstOrDefault();
                if (selected is not null) await CardPileCmd.Add(selected, PileType.Hand);
                return;
            }
            case "NCR:CopyTargetDebuffsToOthers":
                if (state.Target is not null)
                    await CopyTargetDebuffsToOtherEnemies(card, choiceContext, combatState, state.Target,
                        state.TargetDebuffSnapshot ?? CaptureTargetDebuffs(state.Target));
                return;
            case "NCR:ApplyDoom":
                if (state.Target is not null)
                {
                    var doom = amount;
                    var modifier = card.Generated.Operations.FirstOrDefault(op => op.Template == "NCR:DoomPerDoomThreshold");
                    if (modifier is not null)
                    {
                        var modifierIndex = card.Generated.Operations.ToList().IndexOf(modifier);
                        var prefix = modifierIndex > 0 && card.Generated.Operations[modifierIndex - 1].Template == "NCR:ForEachDoomThreshold"
                            ? card.Generated.Operations[modifierIndex - 1]
                            : null;
                        var threshold = prefix is null
                            ? RuntimeSpecValue(card, modifierIndex, "threshold", 0)
                            : RuntimeSpecValue(card, modifierIndex - 1, "threshold", 0);
                        var bonus = prefix is null
                            ? RuntimeSpecValue(card, modifierIndex, "bonus", 0)
                            : RuntimeSpecValue(card, modifierIndex, "bonus", 0);
                        if (threshold > 0 && bonus > 0)
                            doom += ((int)(state.Target.GetPower<DoomPower>()?.Amount ?? 0) / threshold) * bonus;
                    }
                    await PowerCmd.Apply<DoomPower>(choiceContext, state.Target, doom, card.Owner.Creature, card);
                }
                return;
            case "NCR:ApplyEventDamageAsDoom":
                if (state.Target is not null && state.EventAmount > 0)
                    await PowerCmd.Apply<DoomPower>(choiceContext, state.Target, state.EventAmount,
                        card.Owner.Creature, card);
                return;
            case "NCR:AllEnemiesLoseEventHp":
                if (state.EventAmount > 0)
                    foreach (var enemy in combatState.HittableEnemies.ToList())
                        await CreatureCmd.Damage(choiceContext, enemy, state.EventAmount,
                            ValueProp.Unblockable | ValueProp.Unpowered, card.Owner.Creature, card, cardPlay);
                return;
            case "NCR:KillOsty": if (card.Owner.Osty is { } killedOsty) await CreatureCmd.Kill(killedOsty); return;
            case "NCR:BlockTripleOstyMaxHp":
                if (card.Owner.Osty is { } blockingOsty)
                    await CreatureCmd.GainBlock(card.Owner.Creature, blockingOsty.MaxHp * Math.Max(1, amount),
                        ValueProp.Move, cardPlay);
                return;
            case "NCR:AddVoidToSelectedHandCard":
                ApplyKeywordToSelected(operation, state, CardKeyword.Ethereal);
                return;
            case "NCR:AddRetainToSelectedHandCard":
                ApplyKeywordToSelected(operation, state, CardKeyword.Retain);
                return;
            case "NCR:TargetLoseStrength":
                if (state.Target is not null)
                    await PowerCmd.Apply<StrengthPower>(choiceContext, state.Target, -amount, card.Owner.Creature, card);
                return;
            case "NCR:TargetLoseStrengthThisTurn":
                if (state.Target is not null)
                    await PowerCmd.Apply<PiercingWailPower>(choiceContext, state.Target, amount, card.Owner.Creature, card);
                return;
            case "NCR:HealOsty": if (card.Owner.Osty is { } healedOsty) await CreatureCmd.Heal(healedOsty, amount); return;
            case "NCR:IncreaseThisCardDamageRun": IncreaseCardDamageForRun(card, amount); return;
            case "NCR:DoomScaledDamage":
                if (state.Target is not null)
                {
                    var doom = state.Target.GetPower<DoomPower>()?.Amount ?? 0;
                    var (damage, hits) = DamageAndHits(card, doom, state);
                    await DamageCmd.Attack(damage).WithHitCount(hits).FromCard(card, cardPlay).Targeting(state.Target)
                        .WithHitFx(card.Definition.HitFx).Execute(choiceContext);
                }
                return;
            case "NCR:CreateCopyInDiscard":
                await CardPileCmd.AddGeneratedCardToCombat(card.CreateClone(), PileType.Discard, card.Owner);
                return;
            case "NCR:NextVoidCostsZero":
                await PowerCmd.Apply<VeilpiercerPower>(choiceContext, card.Owner.Creature, 1, card.Owner.Creature, card);
                return;
            // Evaluated as modifiers, triggers, or card hooks.
            case "NCR:CostDownPerVoidPlayed": case "NCR:DamagePerCardDrawnThisTurn":
            case "NCR:IfOstyAlive": case "NCR:IfDoomAppliedThisTurn": case "NCR:IfFirstPlayThisTurn":
            case "NCR:DoubleHangDamage":
                if (state.Target is not null)
                {
                    var currentMultiplier = state.Target.GetPowerAmount<HangPower>();
                    var increase = Math.Max(2, currentMultiplier);
                    if (currentMultiplier + increase > 999_999_999)
                        increase = Math.Max(0, 999_999_999 - currentMultiplier);
                    if (increase > 0)
                        await PowerCmd.Apply<HangPower>(choiceContext, state.Target, increase,
                            card.Owner.Creature, card);
                }
                return;
            case "NCR:NextTurn": case "NCR:CostDownWhenCreatureDies":
            case "NCR:DoomPerDoomThreshold": case "NCR:OstyMaxHpBonusDamage":
            case "NCR:RepeatPerVoidPlayedCombat": case "NCR:RepeatPerOstyAttackThisTurn":
            case "NCR:ReturnFromDiscardOnHighCostPlay": case "NCR:DamagePerExhaustedSoul":
            case "NCR:DamagePerOstyAttackCard": case "NCR:OstyCurrentHpBonusDamage":
                return;
            default: throw new InvalidOperationException($"Unimplemented Necrobinder operation: {template}");
        }
    }

    private static void ApplyKeywordToSelected(GeneratorOperation operation, ChaosExecutionState state, CardKeyword keyword)
    {
        if (operation.CardTargetSlot is { } slot && state.CardSlots.TryGetValue(slot, out var selected)
            && !selected.Keywords.Contains(keyword))
            CardCmd.ApplyKeyword(selected, keyword);
    }

    private static Dictionary<PowerModel, int> CaptureTargetDebuffs(Creature target)
    {
        var debuffs = target.Powers
            .Where(power => power.TypeForCurrentAmount == PowerType.Debuff)
            .Select(power => ((PowerModel)power.ClonePreservingMutability(), power.Amount))
            .ToDictionary(pair => pair.Item1, pair => pair.Amount);

        // Match Misery: temporary wrappers also expose an internally applied Power. Fold their current amount
        // into that persistent Power before copying, while retaining the wrapper entry itself exactly as vanilla.
        foreach (var pair in debuffs.ToArray())
        {
            if (pair.Key is not ITemporaryPower temporaryPower) continue;
            var internalPower = debuffs.FirstOrDefault(candidate =>
                candidate.Key.Id == temporaryPower.InternallyAppliedPower.Id);
            if (internalPower.Key is not null)
                debuffs[internalPower.Key] += pair.Value;
        }
        return debuffs;
    }

    private static async Task CopyTargetDebuffsToOtherEnemies(ChaosCardModel card,
        PlayerChoiceContext choiceContext, ICombatState combatState, Creature target,
        IReadOnlyDictionary<PowerModel, int> debuffs)
    {
        foreach (var enemy in combatState.HittableEnemies.ToList())
        {
            if (enemy == target) continue;
            foreach (var pair in debuffs)
            {
                if (pair.Value == 0) continue;
                var existing = PowerCmd.FindExistingInstanceForStacking(pair.Key, enemy, pair.Key.Applier);
                if (existing is not null)
                {
                    await PowerCmd.ModifyAmount(choiceContext, existing, pair.Value, pair.Key.Applier, card);
                    continue;
                }
                var clone = (PowerModel)pair.Key.ClonePreservingMutability();
                await PowerCmd.Apply(choiceContext, clone, enemy, pair.Value, pair.Key.Applier, card);
            }
        }
    }

    private static async Task CreateCard<T>(ChaosCardModel card, PileType pile, int count) where T : CardModel
    {
        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        if (combatState is null) return;
        for (var i = 0; i < count; i++)
            await CardPileCmd.AddGeneratedCardToCombat(combatState.CreateCard<T>(card.Owner), pile, card.Owner);
    }

    private static bool DerivativeIsUpgraded(ChaosCardModel card, int operationIndex)
    {
        var operation = card.Generated.Operations[operationIndex];
        return DerivativeSlotCatalog.SupportsUpgrade(operation.Template, operation.DerivativeId)
            && card.IsUpgraded && card.Generated.Upgrade?.Effects.Any(effect =>
                effect.Kind == CardUpgradeKind.UpgradeDerivative && effect.OperationIndex == operationIndex) == true;
    }

    private static bool GeneratedCardsAreUpgraded(ChaosCardModel card, int operationIndex) =>
        card.IsUpgraded && card.Generated.Upgrade?.Effects.Any(effect =>
            effect.Kind == CardUpgradeKind.UpgradeGeneratedCards && effect.OperationIndex == operationIndex) == true;

    private static void UpgradeGeneratedCards(ChaosCardModel card, int operationIndex,
        IEnumerable<CardModel> generated)
    {
        if (!GeneratedCardsAreUpgraded(card, operationIndex)) return;
        foreach (var generatedCard in generated)
            if (generatedCard.IsUpgradable)
                CardCmd.Upgrade(generatedCard);
    }

    private static async Task CreateDerivatives(ChaosCardModel card, int operationIndex,
        GeneratorOperation operation, PileType pile, int count, CardPilePosition? position = null)
    {
        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        if (combatState is null || count <= 0) return;
        var cards = Enumerable.Range(0, count)
            .Select(_ => ChaosDerivativeResolver.Create(combatState, card.Owner, operation,
                DerivativeIsUpgraded(card, operationIndex)))
            .ToArray();
        IReadOnlyList<CardPileAddResult> results;
        if (position is { } pilePosition)
        {
            results = await CardPileCmd.AddGeneratedCardsToCombat(cards, pile, card.Owner, pilePosition);
            CardCmd.PreviewCardPileAdd(results);
        }
        else
        {
            results = await CardPileCmd.AddGeneratedCardsToCombat(cards, pile, card.Owner);
            // Native status producers such as Gunk Up preview the newly added card. Besides matching that visual
            // contract, the preview path refreshes pile counters immediately instead of making a successful
            // discard-pile insertion look like a no-op until the next pile animation.
            if (pile != PileType.Hand) CardCmd.PreviewCardPileAdd(results);
        }

        // Card generation can legitimately fail once combat is ending. At every other time, report a shortfall
        // with the resolved derivative and final pile so vague "the token was not generated" reports can be
        // distinguished from the normal full-Hand redirect to the Discard Pile.
        if (!CombatManager.Instance.IsOverOrEnding
            && (results.Count != cards.Length || results.Any(result => !result.success)))
            Log.Error($"[AutoAnthony] Derivative producer {operation.Template}/{operation.DerivativeId ?? "default"} "
                      + $"expected {cards.Length} add(s) to {pile}, received {results.Count}: "
                      + string.Join(", ", results.Select(result =>
                          $"{result.cardAdded.Id}@{result.cardAdded.Pile?.Type.ToString() ?? "no pile"}/success={result.success}")));
        else if (ChaosDiagnostics.VerboseRuntime)
            Log.Info($"[AutoAnthony] Derivative producer {operation.Template}/{operation.DerivativeId ?? "default"} "
                     + $"created {results.Count} card(s): "
                     + string.Join(", ", results.Select(result =>
                         $"{result.cardAdded.Id}@{result.cardAdded.Pile?.Type.ToString() ?? "no pile"}")));
    }

    /// <summary>
    /// Transform every selected source as one CardCmd transaction. Card selection owns UI holders for the whole
    /// selection; transforming one source at a time can finish selection mode after the first card and leave later
    /// holders detached. It also permits a partial transformation when a later replacement fails. The base command's
    /// batch path snapshots every source pile/index first and restores all replacements atomically.
    /// </summary>
    private static async Task<int> TransformToDerivatives(ChaosCardModel card, int operationIndex,
        GeneratorOperation operation, IEnumerable<CardModel> sources, CardPreviewStyle style)
    {
        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        if (combatState is null) return 0;
        var selected = sources.Where(source => source.IsTransformable && source.Pile is not null)
            .Distinct().ToList();
        if (selected.Count == 0) return 0;
        var upgraded = DerivativeIsUpgraded(card, operationIndex);
        var transformations = selected.Select(source => new CardTransformation(source,
            ChaosDerivativeResolver.Create(combatState, card.Owner, operation, upgraded))).ToArray();
        var results = (await CardCmd.Transform(transformations, null, style)).ToList();
        if (!CombatManager.Instance.IsOverOrEnding
            && (results.Count != selected.Count || results.Any(result => !result.success)))
            Log.Error($"[AutoAnthony] Derivative transform {operation.Template}/{operation.DerivativeId ?? "default"} "
                      + $"expected {selected.Count} replacement(s), received {results.Count}: "
                      + string.Join(", ", results.Select(result =>
                          $"{result.cardAdded.Id}@{result.cardAdded.Pile?.Type.ToString() ?? "no pile"}/success={result.success}")));
        else if (ChaosDiagnostics.VerboseRuntime)
            Log.Info($"[AutoAnthony] Derivative transform {operation.Template}/{operation.DerivativeId ?? "default"} "
                     + $"replaced {results.Count} card(s): "
                     + string.Join(", ", results.Select(result =>
                         $"{result.cardAdded.Id}@{result.cardAdded.Pile?.Type.ToString() ?? "no pile"}")));
        return results.Count(result => result.success);
    }

    private static async Task ExecuteDefectOperation(ChaosCardModel card, int operationIndex,
        PlayerChoiceContext choiceContext, CardPlay cardPlay, ChaosExecutionState state,
        GeneratorOperation operation, int amount)
    {
        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        if (combatState is null) return;
        var playerCombatState = card.Owner.PlayerCombatState;
        var template = operation.Template;

        if (template.StartsWith("D:ApplyPower_", StringComparison.Ordinal))
        {
            var typeName = template["D:ApplyPower_".Length..];
            var type = typeof(FocusPower).Assembly.GetType($"MegaCrit.Sts2.Core.Models.Powers.{typeName}");
            if (type is null || !typeof(PowerModel).IsAssignableFrom(type))
                throw new InvalidOperationException($"Unknown Defect power operation: {typeName}");
            var power = ModelDb.DebugPower(type).ToMutable();
            ChaosPowerVisuals.Bind(power, card.Definition);
            await PowerCmd.Apply(choiceContext, power, card.Owner.Creature, Math.Max(1, amount), card.Owner.Creature, card);
            return;
        }

        switch (template)
        {
            case "D:CreateZeroCostCopyInDiscard":
            {
                var copy = card.CreateClone();
                copy.EnergyCost.SetThisCombat(0);
                await CardPileCmd.AddGeneratedCardToCombat(copy, PileType.Discard, card.Owner);
                return;
            }
            case "D:ReturnZeroCostDiscardToHand":
                foreach (var candidate in PileType.Discard.GetPile(card.Owner).Cards
                             .Where(candidate => !candidate.EnergyCost.CostsX
                                 && candidate.EnergyCost.GetWithModifiers(CostModifiers.All) == 0
                                 && candidate.Type is CardType.Attack or CardType.Skill or CardType.Power).ToList())
                    await CardPileCmd.Add(candidate, PileType.Hand);
                return;
            case "D:ChannelLightning": case "D:ChannelFrost": case "D:ChannelDark":
            case "D:ChannelPlasma": case "D:ChannelGlass": case "D:ChannelRandom":
                if (ExecutableOrbRepeatCount(amount) == 0) return;
                await ChaosOrbResolver.Channel(choiceContext, card.Owner, operation, amount); return;
            case "D:AddRandomPowerToHand":
            {
                var count = ExecutableGeneratedCardCount(amount);
                if (count == 0) return;
                var powers = card.Owner.Character.CardPool.GetUnlockedCards(card.Owner.UnlockState,
                    card.Owner.RunState.CardMultiplayerConstraint).Where(candidate => candidate.Type == CardType.Power);
                var generated = CardFactory.GetDistinctForCombat(card.Owner, powers, count,
                    card.Owner.RunState.Rng.CombatCardGeneration).ToArray();
                UpgradeGeneratedCards(card, operationIndex, generated);
                await CardPileCmd.AddGeneratedCardsToCombat(generated, PileType.Hand, card.Owner);
                return;
            }
            case "D:TriggerRightmostOrbPassive":
                if (playerCombatState?.OrbQueue.Orbs.FirstOrDefault() is { } rightmost)
                    for (var i = 0; i < Math.Max(1, amount); i++) await OrbCmd.Passive(choiceContext, rightmost, null);
                return;
            case "D:GainFocus":
                await PowerCmd.Apply<FocusPower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card);
                return;
            case "D:LoseFocus":
                await PowerCmd.Apply<FocusPower>(choiceContext, card.Owner.Creature, -amount, card.Owner.Creature, card);
                return;
            case "D:LoseOrbSlots": OrbCmd.RemoveSlots(card.Owner, amount); return;
            case "D:GainOrbSlots": await OrbCmd.AddSlots(card.Owner, amount); return;
            case "D:GainStrength":
                await PowerCmd.Apply<StrengthPower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card);
                return;
            case "D:GainDexterity":
                await PowerCmd.Apply<DexterityPower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card);
                return;
            case "D:NextTurnEnergy":
                await PowerCmd.Apply<EnergyNextTurnPower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card);
                return;
            case "D:IncreaseAllClaws":
                foreach (var chaos in playerCombatState?.AllCards.OfType<ChaosCardModel>()
                             .Where(candidate => candidate.Generated.Operations.Any(op => op.Template == template))
                         ?? Enumerable.Empty<ChaosCardModel>())
                    chaos.ExtraDamage += amount;
                return;
            case "D:CreateDazedInDiscard":
                await CreateDerivatives(card, operationIndex, operation, PileType.Discard,
                    ExecutableDerivativeDiscardCount(operation, amount)); return;
            case "D:CreateTwoWoundsInDiscard":
                await CreateDerivatives(card, operationIndex, operation, PileType.Discard,
                    ExecutableDerivativeDiscardCount(operation, amount)); return;
            case "D:CreateSlimeInDiscard":
                await CreateDerivatives(card, operationIndex, operation, PileType.Discard,
                    ExecutableDerivativeDiscardCount(operation, amount)); return;
            case "D:CreateBurnInDiscard":
                await CreateDerivatives(card, operationIndex, operation, PileType.Discard,
                    ExecutableDerivativeDiscardCount(operation, amount)); return;
            case "D:CreateVoidInDiscard":
                await CreateDerivatives(card, operationIndex, operation, PileType.Discard,
                    ExecutableDerivativeDiscardCount(operation, amount)); return;
            case "D:TransformStatusesToFuel":
            {
                var transforms = PileType.Hand.GetPile(card.Owner).Cards
                    .Where(candidate => candidate.IsTransformable && candidate.Type == CardType.Status)
                    .Select(candidate => new CardTransformation(candidate,
                        ChaosDerivativeResolver.Create(combatState, card.Owner, operation,
                            DerivativeIsUpgraded(card, operationIndex))))
                    .ToList();
                if (transforms.Count > 0) await CardCmd.Transform(transforms, null);
                return;
            }
            case "D:DrawPerUniqueOrb":
                await CardPileCmd.Draw(choiceContext,
                    playerCombatState?.OrbQueue.Orbs.Select(orb => orb.Id).Distinct().Count() ?? 0, card.Owner);
                return;
            case "D:TriggerDarkPassives":
                foreach (var orb in playerCombatState?.OrbQueue.Orbs
                             .Where(candidate => ChaosOrbResolver.MatchesSource(candidate, operation)).ToList() ?? [])
                    await OrbCmd.Passive(choiceContext, orb, null);
                return;
            case "D:ExhaustAllStatuses":
                foreach (var status in playerCombatState?.AllCards
                             .Where(candidate => candidate.Type == CardType.Status).ToList() ?? [])
                {
                    if (status.Pile?.Type == PileType.Exhaust) continue;
                    await CardCmd.Exhaust(choiceContext, status);
                    state.ExhaustedByCard.Add(status);
                }
                return;
            case "D:GainTemporaryFocus":
                await ApplyBoundPower<ChaosTemporaryFocusPower>(card, choiceContext, amount);
                return;
            case "D:IncreaseThisCardBlockRun": IncreaseCardBlockForRun(card, amount); return;
            case "D:MoveDiscardCardToHand":
            {
                var discard = PileType.Discard.GetPile(card.Owner);
                var selected = (await SelectFromCombatPileIfAny(choiceContext, discard, card.Owner,
                    new CardSelectorPrefs(SelectionPrompt("DISCARD_TO_HAND"), 1))).FirstOrDefault();
                if (selected is not null) await CardPileCmd.Add(selected, PileType.Hand);
                return;
            }
            case "D:LoseTemporaryFocus":
                await ApplyBoundPower<ChaosTemporaryFocusDownPower>(card, choiceContext, amount);
                return;
            case "D:IncreaseThisCardCost": card.EnergyCost.AddThisCombat(Math.Max(1, amount)); return;
            case "D:SetThisCardCostZero": card.EnergyCost.SetThisCombat(0); return;
            case "D:ShuffleAllUnexhaustedIntoDraw":
                foreach (var handCard in PileType.Hand.GetPile(card.Owner).Cards.ToList())
                    await CardPileCmd.Add(handCard, PileType.Draw);
                await CardPileCmd.Shuffle(choiceContext, card.Owner);
                return;
            case "D:DrawAndDiscardNonZero":
            {
                var drawn = await CardPileCmd.Draw(choiceContext, amount, card.Owner);
                var discard = drawn.Where(candidate => candidate.EnergyCost.CostsX
                    || candidate.EnergyCost.GetWithModifiers(CostModifiers.All) != 0).ToList();
                if (discard.Count > 0) await CardCmd.Discard(choiceContext, discard);
                return;
            }
            case "D:EvokeAllTwice":
            {
                var count = playerCombatState?.OrbQueue.Orbs.Count ?? 0;
                var repeats = Math.Max(1, amount);
                for (var i = 0; i < count; i++)
                {
                    for (var repeat = 0; repeat < repeats; repeat++)
                        await OrbCmd.EvokeNext(choiceContext, card.Owner,
                            dequeue: repeat == repeats - 1);
                }
                return;
            }
            case "D:EvokeRightmostOrb":
                var evokeCount = ExecutableOrbRepeatCount(amount);
                if (evokeCount == 0 || playerCombatState?.OrbQueue.Orbs.Count is null or 0) return;
                for (var i = 0; i < evokeCount; i++)
                    await OrbCmd.EvokeNext(choiceContext, card.Owner, dequeue: i == evokeCount - 1);
                return;
            case "D:EvokeLeftmostOrb":
                if (ExecutableOrbRepeatCount(amount) == 0 || playerCombatState?.OrbQueue.Orbs.Count is null or 0) return;
                for (var i = 0; i < amount; i++)
                    await OrbCmd.EvokeNext(choiceContext, card.Owner);
                return;
            case "D:GainTemporaryFocusPerUniqueOrb":
            {
                var unique = playerCombatState?.OrbQueue.Orbs.Select(orb => orb.Id).Distinct().Count() ?? 0;
                await ApplyBoundPower<ChaosTemporaryFocusPower>(card, choiceContext, amount * unique);
                return;
            }
            case "D:NextPowerCostsZero":
                await PowerCmd.Apply<FreePowerPower>(choiceContext, card.Owner.Creature, 1, card.Owner.Creature, card);
                return;
            case "D:TriggerLightningPassivesAtTarget":
                if (state.Target is not null)
                    foreach (var orb in playerCombatState?.OrbQueue.Orbs.OfType<LightningOrb>().ToList() ?? [])
                        await OrbCmd.Passive(choiceContext, orb, state.Target);
                return;
            case "D:AutoPlayRandomAttackFromDraw":
            {
                var played = new HashSet<CardModel>();
                for (var playIndex = 0; playIndex < RandomDrawAutoplayLimit(amount); playIndex++)
                {
                    // Re-read the pile after every resolved play: autoplay can move, exhaust or shuffle cards,
                    // and the next random choice must use the resulting live draw pile.
                    var attacks = PileType.Draw.GetPile(card.Owner).Cards
                        .Where(candidate => candidate.Type == CardType.Attack
                            && !candidate.Keywords.Contains(CardKeyword.Unplayable)
                            && !played.Contains(candidate))
                        .ToList();
                    var selected = card.Owner.RunState.Rng.CombatCardSelection.NextItem(attacks);
                    if (selected is null) break;
                    played.Add(selected);
                    await CardCmd.AutoPlay(choiceContext, selected, null);
                }
                return;
            }
            case "D:ReturnEventCardToHand":
                if (state.EventCard is not null) await CardPileCmd.Add(state.EventCard, PileType.Hand);
                return;
            case "D:ExhaustSelectedHandCard":
                if (operation.CardTargetSlot is { } slot && state.CardSlots.TryGetValue(slot, out var selectedCard))
                {
                    await CardCmd.Exhaust(choiceContext, selectedCard);
                    state.ExhaustedByCard.Add(selectedCard);
                }
                return;
            // Modifiers, conditions, and card hooks are evaluated elsewhere.
            case "D:RepeatPerOrb":
            case "D:ForEachOrb":
            case "D:ForEachEnemy":
            case "D:ForEachUniqueOrb":
            case "D:IfHasFrost":
            case "D:RepeatDamage":
            case "D:RepeatPerEnergySpentThisTurn":
            case "D:ForEachExhaustedStatus":
            case "D:IfCardsPlayedBelow":
            case "D:IfEnemyIntendsAttack":
            case "D:IfFatal":
            case "D:ReplayEventCard":
            case "D:CostDownWhenStatusGenerated":
                return;
            default:
                throw new InvalidOperationException($"Unimplemented Defect operation: {template}");
        }
    }

    private static async Task CreateStatus<T>(ChaosCardModel card, PileType pile, int count)
        where T : CardModel
    {
        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        if (combatState is null) return;
        for (var i = 0; i < count; i++)
            await CardPileCmd.AddGeneratedCardToCombat(combatState.CreateCard<T>(card.Owner), pile, card.Owner);
    }

    private static async Task ExecuteOriginalOperation(ChaosCardModel card, int operationIndex,
        PlayerChoiceContext choiceContext, CardPlay cardPlay, ChaosExecutionState state, GeneratorOperation operation)
    {
        if (operation.Template == "A:ProxyAtomic_Buffer")
        {
            await ApplyGeneratedProxyPower<BufferPower>(card, operationIndex, choiceContext);
            return;
        }
        if (operation.Template == "A:ProxyAtomic_Calcify")
        {
            // The generator is allowed to vary Calcify's printed amount. Invoking the detached original card would
            // always apply its hard-coded 4 (or native upgraded 6), so apply the generated slot directly.
            var amount = Math.Max(1, card.OperationAmount(operationIndex));
            await CreatureCmd.TriggerAnim(card.Owner.Creature, "PowerUp", card.Owner.Character.PowerUpAnimDelay);
            await PowerCmd.Apply<CalcifyPower>(choiceContext, card.Owner.Creature, amount,
                card.Owner.Creature, card);
            return;
        }
        if (operation.Template == "A:ProxyAtomic_Lethality")
        {
            await ApplyGeneratedProxyPower<LethalityPower>(card, operationIndex, choiceContext);
            return;
        }
        if (operation.Template == "A:ProxyAtomic_Parry")
        {
            await ApplyGeneratedProxyPower<ParryPower>(card, operationIndex, choiceContext);
            return;
        }
        if (operation.Template == "A:ProxyAtomic_Royalties")
        {
            await ApplyGeneratedProxyPower<RoyaltiesPower>(card, operationIndex, choiceContext);
            return;
        }
        if (operation.Template == "A:ProxyAtomic_SwordSage")
        {
            await ApplyGeneratedProxyPower<SwordSagePower>(card, operationIndex, choiceContext);
            return;
        }
        if (operation.Template == "A:ProxyAtomic_VoidForm")
        {
            await ApplyGeneratedProxyPower<VoidFormPower>(card, operationIndex, choiceContext);
            PlayerCmd.EndTurn(card.Owner, canBackOut: false);
            return;
        }
        if (operation.Template == "A:VoidFormFirstCardsFree")
        {
            await ApplyGeneratedProxyPower<VoidFormPower>(card, operationIndex, choiceContext);
            return;
        }
        if (operation.Template == "I:ProxyAtomic_WhiteNoise")
        {
            var powers = card.Owner.Character.CardPool.GetUnlockedCards(card.Owner.UnlockState,
                card.Owner.RunState.CardMultiplayerConstraint).Where(candidate => candidate.Type == CardType.Power);
            var generated = CardFactory.GetDistinctForCombat(card.Owner, powers, 1,
                card.Owner.RunState.Rng.CombatCardGeneration).ToArray();
            UpgradeGeneratedCards(card, operationIndex, generated);
            if (generated.FirstOrDefault() is { } selected)
            {
                selected.SetToFreeThisTurn();
                await CardPileCmd.AddGeneratedCardToCombat(selected, PileType.Hand, card.Owner);
            }
            return;
        }
        if (operation.Template is "CL:ProxyAtomic_Discovery" or "I:ProxyAtomic_Quasar"
            or "CL:ProxyAtomic_Splash")
        {
            IEnumerable<CardModel> pool;
            if (operation.Template == "I:ProxyAtomic_Quasar")
                pool = ModelDb.CardPool<ColorlessCardPool>().GetUnlockedCards(card.Owner.UnlockState,
                    card.Owner.RunState.CardMultiplayerConstraint);
            else if (operation.Template == "CL:ProxyAtomic_Splash")
            {
                var pools = card.Owner.UnlockState.CharacterCardPools.ToList();
                if (pools.Count > 1) pools.Remove(card.Owner.Character.CardPool);
                pool = pools.SelectMany(candidate => candidate.GetUnlockedCards(card.Owner.UnlockState,
                        card.Owner.RunState.CardMultiplayerConstraint))
                    .Where(candidate => candidate.Type == CardType.Attack);
            }
            else
                pool = card.Owner.Character.CardPool.GetUnlockedCards(card.Owner.UnlockState,
                    card.Owner.RunState.CardMultiplayerConstraint);

            // FromChooseACardScreen throws for four or more cards. New definitions are capped during generation,
            // and this runtime clamp keeps pre-cap snapshots safe without regenerating the saved card.
            var candidateCount = GeneratedCardChoiceCandidateCount(card.OperationAmount(operationIndex));
            if (candidateCount == 0) return;
            var generated = CardFactory.GetDistinctForCombat(card.Owner, pool, candidateCount,
                card.Owner.RunState.Rng.CombatCardGeneration).ToList();
            // A requested nonzero choice can still produce no legal combat cards after unlock/multiplayer filters.
            // Never open a choice holder with an empty list: the base UI waits for a selection that cannot exist.
            if (!HasGeneratedCardChoiceCandidates(candidateCount, generated.Count)) return;
            UpgradeGeneratedCards(card, operationIndex, generated);
            CardModel? selected;
            if (generated.Count <= 3)
                selected = await CardSelectCmd.FromChooseACardScreen(choiceContext, generated, card.Owner,
                    canSkip: true);
            else
                selected = (await CardSelectCmd.FromSimpleGrid(choiceContext, generated, card.Owner,
                    new CardSelectorPrefs(SelectionPrompt("DISCARD_TO_HAND"), 0, 1)
                    {
                        Cancelable = true
                    })).FirstOrDefault();
            if (selected is not null)
            {
                if (operation.Template is "CL:ProxyAtomic_Discovery" or "CL:ProxyAtomic_Splash")
                    selected.SetToFreeThisTurn();
                await CardPileCmd.AddGeneratedCardToCombat(selected, PileType.Hand, card.Owner);
            }
            return;
        }
        if (operation.Template == "CL:ProxyAtomic_Catastrophe")
        {
            var activeCombat = card.CombatState ?? card.Owner.Creature.CombatState;
            if (activeCombat is null) return;
            var count = Math.Max(1, card.OperationAmount(operationIndex));
            for (var playIndex = 0; playIndex < count; playIndex++)
            {
                var drawPile = PileType.Draw.GetPile(card.Owner).Cards.ToList();
                if (drawPile.Count == 0) break;
                var playable = drawPile.Where(candidate => !candidate.Keywords.Contains(CardKeyword.Unplayable))
                    .ToList();
                var selected = (playable.Count > 0 ? playable : drawPile)
                    .StableShuffle(card.Owner.RunState.Rng.Shuffle).FirstOrDefault();
                if (selected is null) break;
                Creature? target = null;
                if (selected.TargetType == TargetType.AnyEnemy)
                    target = card.Owner.RunState.Rng.CombatTargets.NextItem(activeCombat.HittableEnemies);
                if (selected.TargetType == TargetType.AnyEnemy && target is null) break;
                await CardCmd.AutoPlay(choiceContext, selected, target);
            }
            return;
        }
        if (operation.Template == "CL:ProxyAtomic_HiddenGem")
        {
            var drawPile = PileType.Draw.GetPile(card.Owner).Cards.ToList();
            var candidates = drawPile.Where(candidate =>
                    !candidate.Keywords.Contains(CardKeyword.Unplayable)
                    && candidate.Type is not (CardType.Status or CardType.Curse)
                    && candidate.GetEnchantedReplayCount() < 1)
                .ToList();
            var ordinaryCards = candidates.Where(candidate => candidate.Type is
                CardType.Attack or CardType.Skill or CardType.Power).ToList();
            var selected = card.Owner.RunState.Rng.CombatCardSelection.NextItem(
                ordinaryCards.Count > 0 ? ordinaryCards : candidates);
            if (selected is not null)
            {
                selected.BaseReplayCount += Math.Max(1, card.OperationAmount(operationIndex));
                CardCmd.Preview(selected);
            }
            return;
        }
        // These original cards read combat-only state from `this`. A proxy created only to invoke OnPlay is
        // detached from every combat pile, so its CombatState is null. Execute the reusable effects against the
        // generated card itself instead; this also preserves its generated damage value and X-star payment.
        if (IsDirectDiscardAttackAutoplay(operation))
        {
            var activeCombat = card.CombatState ?? card.Owner.Creature.CombatState;
            if (activeCombat is null) return;
            var count = Math.Max(1, card.OperationAmount(operationIndex));
            var attacks = PileType.Discard.GetPile(card.Owner).Cards
                .Where(candidate => candidate.Type == CardType.Attack
                    && !candidate.Keywords.Contains(CardKeyword.Unplayable))
                .ToList()
                .StableShuffle(card.Owner.RunState.Rng.Shuffle)
                .Take(count)
                .ToList();
            var played = 0;
            foreach (var attack in attacks)
            {
                if (CombatManager.Instance.IsOverOrEnding) break;
                Creature? target = null;
                if (attack.TargetType == TargetType.AnyEnemy)
                    target = card.Owner.RunState.Rng.CombatTargets.NextItem(activeCombat.HittableEnemies);
                if (attack.TargetType == TargetType.AnyEnemy && target is null) break;
                await CardCmd.AutoPlay(choiceContext, attack, target);
                played++;
            }
            if (ChaosDiagnostics.VerboseRuntime)
                Log.Info($"[AutoAnthony] Resolved Beat Down component on slot {card.Definition.Slot}: auto-played {played}/{count} discard-pile Attack(s).");
            return;
        }
        if (operation.Template == "N:ProxyDamage_Atomic_StarX_Stardust")
        {
            var activeCombat = card.CombatState ?? card.Owner.Creature.CombatState;
            if (activeCombat is null) return;
            var baseDamage = card.OperationAmount(operationIndex);
            var (damage, hits) = DamageAndHits(card, baseDamage, state, Math.Max(0, card.ResolveEffectStarXValue()));
            if (hits <= 0) return;
            await DamageCmd.Attack(damage).WithHitCount(hits).FromCard(card, cardPlay)
                .TargetingRandomOpponents(activeCombat)
                .WithHitFx(card.Definition.HitFx)
                .Execute(choiceContext);
            if (ChaosDiagnostics.VerboseRuntime)
                Log.Info($"[AutoAnthony] Resolved Stardust component on slot {card.Definition.Slot}: {damage} damage x {hits} Stars.");
            return;
        }
        if (operation.Template == "T:ProxyDamage_Atomic_EnergyX_Eradicate")
        {
            if (state.Target is null) return;
            var baseDamage = card.OperationAmount(operationIndex);
            var (damage, hits) = DamageAndHits(card, baseDamage, state, Math.Max(0, card.ResolveEffectEnergyXValue()));
            if (hits <= 0) return;
            await DamageCmd.Attack(damage).WithHitCount(hits).FromCard(card, cardPlay)
                .Targeting(state.Target).WithHitFx(card.Definition.HitFx).Execute(choiceContext);
            return;
        }
        if (operation.Template == "T:ProxyDamage_Atomic_Poke")
        {
            if (card.Owner.Osty is not { } osty || state.Target is null) return;
            var baseDamage = card.OperationAmount(operationIndex);
            var (damage, hits) = DamageAndHits(card, baseDamage, state);
            if (hits <= 0) return;
            await DamageCmd.Attack(damage).WithHitCount(hits).FromOsty(osty, card, cardPlay)
                .Targeting(state.Target).WithHitFx(card.Definition.HitFx).Execute(choiceContext);
            return;
        }
        if (operation.Template == "I:ProxyAtomic_Tempest")
        {
            var channels = RuntimeSpecValue(card, operationIndex, "amount",
                Math.Max(0, card.ResolveEffectEnergyXValue()));
            await ChaosOrbResolver.Channel(choiceContext, card.Owner, operation, channels);
            return;
        }
        // MultiCast's original OnPlay calls ResolveEnergyXValue() on the card instance. A generated operation is
        // invoked through a detached proxy, whose CombatState is null; the vanilla X-value hook walker then throws
        // before the proxy can evoke anything, leaving the source card's PlayCardAction incomplete. Reproduce the
        // small original loop using the X payment already captured by ChaosCardModel.OnPlay instead.
        if (operation.Template == "I:ProxyAtomic_MultiCast")
        {
            await CreatureCmd.TriggerAnim(card.Owner.Creature, "Cast", card.Owner.Character.CastAnimDelay);
            var evokeCount = RuntimeSpecValue(card, operationIndex, "amount",
                Math.Max(0, card.ResolveEffectEnergyXValue()));
            for (var i = 0; i < evokeCount; i++)
            {
                if (card.Owner.PlayerCombatState?.OrbQueue.Orbs.Count is null or 0)
                    break;
                await OrbCmd.EvokeNext(choiceContext, card.Owner, dequeue: i == evokeCount - 1);
                await Cmd.Wait(0.25f);
            }
            return;
        }
        if (operation.Template == "I:ProxyAtomic_Voltaic")
        {
            var sourceCount = CombatManager.Instance.History.Entries.OfType<OrbChanneledEntry>()
                .Count(entry => entry.Actor == card.Owner.Creature
                    && ChaosOrbResolver.MatchesSource(entry.Orb, operation));
            await ChaosOrbResolver.Channel(choiceContext, card.Owner, operation, sourceCount);
            if (ChaosDiagnostics.VerboseRuntime)
                Log.Info($"[AutoAnthony] Resolved Voltaic component on slot {card.Definition.Slot}: channeled {sourceCount} selected Orb(s).");
            return;
        }
        // Orbit is an atomic original card, but its payment threshold is a generated numeric slot and can be
        // reduced by an upgrade. The original OrbitPower hard-codes 4, so use the composite interpreter here.
        if (operation.Template == "A:ProxyAtomic_Orbit")
        {
            var power = (ChaosCompositePower)ModelDb.Power<ChaosCompositePower>().ToMutable();
            power.Configure(card.Generated.Character, card.Definition.Slot, card.IsUpgraded, permanent: true,
                card.ResolvedSpecialXValue, card.ResolvedEnergyXValue, card.ResolvedStarXValue,
                card.CaptureOperationValuesForPower(), profileId: card.RuntimeProfileId);
            await PowerCmd.Apply(choiceContext, power, card.Owner.Creature, 1m, card.Owner.Creature, card);
            return;
        }
        // Dredge and Transfigure normally delegate to an original card instance. Both original OnPlay methods
        // open a player-choice transaction before discovering that their source pile has no legal card. In a
        // generated multi-effect play that needless transaction can cancel the active card-play action and leave
        // the card node in Play. Interpret them directly so absence of an object is a true no-op, not a UI choice.
        if (operation.Template == "I:ProxyAtomic_Dredge")
        {
            var handSpace = Math.Max(0, CardPile.MaxCardsInHand - PileType.Hand.GetPile(card.Owner).Cards.Count);
            var count = Math.Min(handSpace,
                Math.Max(1, card.OperationAmount(operationIndex)));
            if (count == 0) return;
            var selected = await SelectFromCombatPileIfAny(choiceContext, PileType.Discard.GetPile(card.Owner),
                card.Owner, new CardSelectorPrefs(SelectionPrompt("DISCARD_TO_HAND"), count));
            await CardPileCmd.Add(selected, PileType.Hand);
            return;
        }
        if (operation.Template == "I:ProxyAtomic_Transfigure")
        {
            var selected = (await SelectFromHandIfAny(choiceContext, card.Owner,
                new CardSelectorPrefs(SelectionPrompt("SELECT_HAND_CARD"), 1), null, card)).FirstOrDefault();
            if (selected is null) return;
            if (!selected.EnergyCost.CostsX && selected.EnergyCost.GetWithModifiers(CostModifiers.None) >= 0)
                selected.EnergyCost.AddThisCombat(Math.Max(1, card.OperationAmount(operationIndex)));
            selected.BaseReplayCount++;
            return;
        }
        if (operation.Template == "I:ProxyAtomic_ForegoneConclusion")
        {
            await ApplyGeneratedProxyPower<ForegoneConclusionPower>(card, operationIndex, choiceContext,
                animation: "Cast");
            return;
        }
        var typeName = operation.Template[(operation.Template.LastIndexOf('_') + 1)..];
        // Combat transformations cannot safely run through a detached proxy card. CardModel.CombatState is derived
        // from the card's current pile, so even combatState.CreateCard(...) leaves a proxy without CombatState until
        // it is added to a pile. Execute the four atomic in-combat transform cards directly, preserving the original
        // selection source and CardCmd.Transform semantics (these replacements remain combat-only).
        if (await TryExecuteCombatTransformProxy(card, operationIndex, choiceContext, operation, typeName))
            return;
        var canonical = OriginalComponentCardCatalog.For(card.Generated.Character)
            .FirstOrDefault(candidate => candidate.GetType().Name == typeName)
            ?? Enum.GetValues<GeneratedCharacter>()
                .SelectMany(OriginalComponentCardCatalog.For)
                .FirstOrDefault(candidate => candidate.GetType().Name == typeName);
        if (canonical is null)
        {
            Log.Error($"[AutoAnthony] Missing original operation source {card.Generated.Character}/{typeName}.");
            return;
        }
        // A number of original atomic effects (notably Begone's transform choice) access CardModel.CombatState
        // after their selection screen closes. A RunState-created proxy is deliberately detached from combat and
        // therefore crashes after the choice. Create proxies through the active combat whenever one exists.
        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        var proxy = combatState?.CreateCard(canonical, card.Owner) ?? card.Owner.RunState.CreateCard(canonical, card.Owner);
        if (card.IsUpgraded && card.Generated.Upgrade?.Effects.Any(effect => effect.OperationIndex == operationIndex) == true
            && proxy.IsUpgradable)
            CardCmd.Upgrade(proxy);
        var proxyPlay = new CardPlay
        {
            Card = proxy,
            Player = cardPlay.Player,
            Target = cardPlay.Target,
            ResultPile = cardPlay.ResultPile,
            Resources = cardPlay.Resources,
            IsAutoPlay = cardPlay.IsAutoPlay,
            PlayIndex = cardPlay.PlayIndex,
            PlayCount = cardPlay.PlayCount
        };
        var onPlay = HarmonyLib.AccessTools.Method(proxy.GetType(), "OnPlay");
        if (onPlay?.Invoke(proxy, [choiceContext, proxyPlay]) is Task task)
            await task;
        else
            Log.Error($"[AutoAnthony] Original operation {typeName} did not expose an awaitable OnPlay method.");
    }

    private static async Task ApplyGeneratedProxyPower<T>(ChaosCardModel card, int operationIndex,
        PlayerChoiceContext choiceContext, string animation = "PowerUp") where T : PowerModel
    {
        await CreatureCmd.TriggerAnim(card.Owner.Creature, animation,
            animation == "Cast" ? card.Owner.Character.CastAnimDelay : card.Owner.Character.PowerUpAnimDelay);
        await PowerCmd.Apply<T>(choiceContext, card.Owner.Creature,
            Math.Max(1, card.OperationAmount(operationIndex)), card.Owner.Creature, card);
    }

    private static async Task<bool> TryExecuteCombatTransformProxy(ChaosCardModel card, int operationIndex,
        PlayerChoiceContext choiceContext, GeneratorOperation operation, string typeName)
    {
        if (typeName is not ("Begone" or "Charge" or "Guards" or "Seance")) return false;
        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        if (combatState is null) return true;

        switch (typeName)
        {
            case "Begone":
            {
                var selected = (await SelectFromHandIfAny(choiceContext, card.Owner,
                    new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt, 1),
                    candidate => candidate.IsTransformable, card)).FirstOrDefault();
                if (selected is null) return true;
                await TransformToDerivatives(card, operationIndex, operation, [selected],
                    TransformPreviewStyle(choiceContext));
                return true;
            }
            case "Charge":
            {
                var count = Math.Max(1, card.OperationAmount(operationIndex));
                var selected = (await SelectFromCombatPileIfAny(choiceContext, PileType.Draw.GetPile(card.Owner),
                    card.Owner, new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt, count),
                    candidate => candidate.IsTransformable)).ToList();
                await TransformToDerivatives(card, operationIndex, operation, selected,
                    TransformPreviewStyle(choiceContext));
                return true;
            }
            case "Guards":
            {
                var selected = (await SelectFromHandIfAny(choiceContext, card.Owner,
                    new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt, 0, int.MaxValue),
                    candidate => candidate.IsTransformable, card)).ToList();
                await TransformToDerivatives(card, operationIndex, operation, selected,
                    TransformPreviewStyle(choiceContext));
                return true;
            }
            case "Seance":
            {
                var count = Math.Max(1, card.OperationAmount(operationIndex));
                var selected = (await SelectFromCombatPileIfAny(choiceContext, PileType.Draw.GetPile(card.Owner),
                    card.Owner, new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt, count),
                    candidate => candidate.IsTransformable)).ToList();
                await TransformToDerivatives(card, operationIndex, operation, selected,
                    TransformPreviewStyle(choiceContext));
                return true;
            }
        }
        return false;
    }

    private static CardModel? SelectedCard(GeneratorOperation operation, ChaosExecutionState state) =>
        operation.CardTargetSlot is { } slot && state.CardSlots.TryGetValue(slot, out var selected) ? selected : null;

    private static IReadOnlyList<CardModel> SelectedCards(GeneratorOperation operation, ChaosExecutionState state)
    {
        if (operation.CardTargetSlot is not { } slot) return [];
        if (state.CardSelections.TryGetValue(slot, out var selected)) return selected;
        return state.CardSlots.TryGetValue(slot, out var card) ? [card] : [];
    }

    internal static int HandExhaustSelectionCount(int printedAmount, int availableCards) =>
        Math.Min(Math.Max(1, printedAmount), Math.Max(0, availableCards));

    private static async Task Exhaust(ChaosCardModel card, GeneratorOperation operation,
        OperationRuntimeSpec spec, int amount,
        PlayerChoiceContext choiceContext, ChaosExecutionState state)
    {
        var hand = PileType.Hand.GetPile(card.Owner).Cards.ToList();
        IEnumerable<CardModel> targets;
        if (spec.Variant == "all")
            targets = spec.CardFilter == "non_attack"
                ? hand.Where(candidate => candidate.Type != CardType.Attack)
                : hand;
        else if (spec.Variant == "referenced" && operation.CardTargetSlot is { } slot
                 && state.CardSlots.TryGetValue(slot, out var selectedCard)) targets = [selectedCard];
        else if (spec.Variant == "referenced"
                 && (state.IterationCard ?? state.EventCard) is { } referencedCard) targets = [referencedCard];
        else if (spec.Variant == "random")
        {
            var pool = spec.CardFilter == "attack"
                ? hand.Where(candidate => candidate.Type == CardType.Attack).ToList()
                : hand;
            var count = HandExhaustSelectionCount(amount, pool.Count);
            var selected = new List<CardModel>(count);
            while (selected.Count < count)
            {
                var randomCard = card.Owner.RunState.Rng.CombatCardSelection.NextItem(pool);
                if (randomCard is null) break;
                selected.Add(randomCard);
                pool.Remove(randomCard);
            }
            targets = selected;
        }
        else
        {
            var count = HandExhaustSelectionCount(amount, hand.Count);
            targets = count == 0
                ? []
                : await SelectFromHandIfAny(choiceContext, card.Owner,
                    new CardSelectorPrefs(CardSelectorPrefs.ExhaustSelectionPrompt, count), null, card);
        }
        foreach (var target in targets.ToList())
        {
            await CardCmd.Exhaust(choiceContext, target);
            state.ExhaustedByCard.Add(target);
        }
    }

    private static async Task Discard(ChaosCardModel card, int count, PlayerChoiceContext choiceContext, ChaosExecutionState state)
    {
        var hand = PileType.Hand.GetPile(card.Owner).Cards.ToList();
        var targets = count >= hand.Count
            ? hand
            : (await SelectFromHandForDiscardIfAny(choiceContext, card.Owner,
                new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, count), null, card)).ToList();
        if (targets.Count == 0) return;
        await CardCmd.Discard(choiceContext, targets);
        state.DiscardedByCard.AddRange(targets);
    }

    private static async Task CreateCard(ChaosCardModel card, int operationIndex, GeneratorOperation operation,
        OperationRuntimeSpec spec, int amount, ChaosExecutionState state)
    {
        var count = ExecutableGeneratedCardCount(amount);
        if (count == 0) return;
        IEnumerable<CardModel> created = [];
        if (spec.Opcode == "create_copy" && spec.Variant == "this_card")
            created = Enumerable.Range(0, count).Select(_ => card.CreateClone());
        else if (spec.Opcode == "create_copy" && spec.Variant == "referenced_attack"
                 && operation.CardTargetSlot is { } slot && state.CardSlots.TryGetValue(slot, out var selected))
            created = Enumerable.Range(0, count).Select(_ => selected.CreateClone());
        else if (spec.Opcode == "create_copy" && spec.Variant == "referenced_attack"
                 && (state.IterationCard ?? state.EventCard) is { } referencedCard)
            created = Enumerable.Range(0, count).Select(_ => referencedCard.CreateClone());
        else if (spec.Opcode == "create_card" && spec.Variant == "current_character_random")
            created = CardFactory.GetDistinctForCombat(card.Owner, CurrentCharacterCards(card), count,
                card.Owner.RunState.Rng.CombatCardGeneration);
        var materialized = created.ToArray();
        UpgradeGeneratedCards(card, operationIndex, materialized);
        await CardPileCmd.AddGeneratedCardsToCombat(materialized,
            spec.DestinationZone == "discard" ? PileType.Discard : PileType.Hand, card.Owner);
    }

    internal static bool IsCurrentCharacterRandomCardGeneration(GeneratorOperation operation) =>
        CardEffectRules.IsRandomCurrentCharacterCardToHand(operation);

    private static async Task MoveCard(ChaosCardModel card, GeneratorOperation operation,
        OperationRuntimeSpec spec, PlayerChoiceContext choiceContext, ChaosExecutionState state)
    {
        var discard = PileType.Discard.GetPile(card.Owner);
        if (spec.Variant == "random" && spec.CardFilter == "attack")
        {
            var attacks = discard.Cards.Where(candidate => candidate.Type == CardType.Attack).ToList();
            if (attacks.Count > 0)
            {
                var moved = card.Owner.RunState.Rng.CombatCardSelection.NextItem(attacks);
                if (moved is not null)
                {
                    await CardPileCmd.Add(moved, PileType.Hand);
                    state.CardSlots[operation.CardTargetSlot ?? "movedCard"] = moved;
                    state.LastMovedCard = moved;
                }
            }
            return;
        }
        var selected = (await SelectFromCombatPileIfAny(choiceContext, discard, card.Owner,
            new CardSelectorPrefs(SelectionPrompt("DISCARD_TO_DRAW_PILE"), 1))).FirstOrDefault();
        if (selected is not null) await CardPileCmd.Add(selected, PileType.Draw, CardPilePosition.Top);
    }

    private static async Task ExecuteIndependent(ChaosCardModel card, int operationIndex,
        GeneratorOperation operation, int amount,
        PlayerChoiceContext choiceContext, CardPlay cardPlay, ChaosExecutionState state)
    {
        if (operation.Template == "A:rulePoisonExtraTriggers")
            await ApplyBoundPower<AccelerantPower>(card, choiceContext, amount);
        else if (operation.Template == "A:ruleShivBonusDamage")
            await ApplyBoundPower<AccuracyPower>(card, choiceContext, amount);
        else if (operation.Template == "A:ruleUnblockedAttackPoison")
            await ApplyBoundPower<EnvenomPower>(card, choiceContext, amount);
        else if (operation.Template == "A:ruleShivsHitAll")
            await ApplyBoundPower<FanOfKnivesPower>(card, choiceContext, 1m);
        else if (operation.Template == "A:rulePlayedSkillsGainSly")
            await ApplyBoundPower<MasterPlannerPower>(card, choiceContext, 1m);
        else if (operation.Template == "A:ruleShivsRetainAndFirstBonus")
            await ApplyBoundPower<PhantomBladesPower>(card, choiceContext, amount);
        // Tracking-style percentage rules live in ChaosCompositePower. Applying the native TrackingPower here
        // exposes its raw percentage as a Counter amount (visually resembling "50x") and separates it from the
        // generated card's own Power description.
        else if (operation.Template == "A:ruleWeakEnemiesTakeMoreAttackDamage") { }
        else if (operation.Template == "A:ruleRetainHand")
            await ApplyBoundPower<WellLaidPlansPower>(card, choiceContext, 1m);
        else if (operation.Template == "I:FreeHandThisTurn")
        {
            foreach (var handCard in PileType.Hand.GetPile(card.Owner).Cards)
                if (!handCard.EnergyCost.CostsX) handCard.SetToFreeThisTurn();
        }
        else if (operation.Template == "I:ReplayNextSkills")
            await PowerCmd.Apply<BurstPower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card);
        else if (operation.Template == "I:DiscardHandDrawSame")
        {
            var discarded = PileType.Hand.GetPile(card.Owner).Cards.ToList();
            await CardCmd.Discard(choiceContext, discarded);
            state.DiscardedByCard.AddRange(discarded);
            await CardPileCmd.Draw(choiceContext, discarded.Count, card.Owner);
        }
        else if (operation.Template == "I:DrawAndBlockIfSkill")
        {
            var drawCount = RuntimeSpecValue(card, operationIndex, "draw", 0);
            var drawn = (await CardPileCmd.Draw(choiceContext, drawCount, card.Owner)).FirstOrDefault();
            if (drawn is not null && drawn.Type == CardType.Skill)
                await CreatureCmd.GainBlock(card.Owner.Creature, amount, ValueProp.Move, cardPlay);
        }
        else if (operation.Template == "I:DrawWithRetain")
        {
            foreach (var drawn in await CardPileCmd.Draw(choiceContext, amount, card.Owner))
                CardCmd.ApplySingleTurnRetain(drawn);
        }
        else if (operation.Template == "I:GrantSlyToHandSkillThisTurn")
        {
            var selected = (await SelectFromHandIfAny(choiceContext, card.Owner,
                new CardSelectorPrefs(SelectionPrompt("SELECT_HAND_SKILL"), 1),
                candidate => candidate.Type == CardType.Skill && !candidate.IsSlyThisTurn, card)).FirstOrDefault();
            if (selected is not null) CardCmd.ApplySingleTurnSly(selected);
        }
        else if (operation.Template == "I:PlayExhaustedShivsAtTarget")
        {
            if (state.Target is null) return;
            var first = true;
            foreach (var shiv in PileType.Exhaust.GetPile(card.Owner).Cards
                         .Where(candidate => ChaosDerivativeResolver.Matches(candidate, operation)).ToList())
            {
                if (DerivativeIsUpgraded(card, operationIndex) && shiv.IsUpgradable) CardCmd.Upgrade(shiv);
                await CardCmd.AutoPlay(choiceContext, shiv, state.Target, AutoPlayType.Default, false, !first);
                first = false;
            }
        }
        else if (operation.Template == "I:CopySelectedCardNextTurn")
        {
            var selected = (await SelectFromHandIfAny(choiceContext, card.Owner,
                new CardSelectorPrefs(SelectionPrompt("SELECT_HAND_CARD"), 1), null, card)).FirstOrDefault();
            if (selected is not null)
            {
                var nightmare = await PowerCmd.Apply<NightmarePower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card);
                nightmare?.SetSelectedCard(selected);
            }
        }
        else if (operation.Template == "I:TriggerPoisonNow")
        {
            foreach (var enemy in (card.CombatState ?? card.Owner.Creature.CombatState)!.HittableEnemies)
                if (enemy.GetPower<PoisonPower>() is { } poison) await poison.Trigger();
        }
        else if (operation.Template == "I:NextSkillCostsZero")
            await PowerCmd.Apply<FreeSkillPower>(choiceContext, card.Owner.Creature, 1m, card.Owner.Creature, card);
        else if (operation.Template == "I:DoubleAttackDamageNextTurn")
            await PowerCmd.Apply<ShadowStepPower>(choiceContext, card.Owner.Creature, 1m, card.Owner.Creature, card);
        else if (operation.Template == "I:DoubleBlockThisTurn")
            await PowerCmd.Apply<ShadowmeldPower>(choiceContext, card.Owner.Creature, 1m, card.Owner.Creature, card);
        else if (operation.Template == "I:AddCardReward")
        {
            if ((card.CombatState ?? card.Owner.Creature.CombatState)?.RunState.CurrentRoom is CombatRoom room)
                room.AddExtraReward(card.Owner, new CardReward(CardCreationOptions.ForRoom(card.Owner, room.RoomType), 3, card.Owner));
        }
        else if (operation.Template == "I:ReduceThisCardCostCombat")
            card.EnergyCost.AddThisCombat(-amount);
        else if (operation.Template == "I:PlayThisCard")
        {
            await CardCmd.AutoPlay(choiceContext, card, null);
        }
        else if (operation.Template == "I:Upgrade")
        {
            var selected = await SelectFromHandForUpgradeIfAny(choiceContext, card.Owner, card);
            if (selected is not null) UpgradeExistingCombatCard(selected, operation.Template);
        }
        else if (operation.Template == "I:UpgradeThatCard" && operation.CardTargetSlot is { } slot && state.CardSlots.TryGetValue(slot, out var selected) && selected.IsUpgradable) UpgradeExistingCombatCard(selected, operation.Template);
        else if (operation.Template == "I:UpgradeThatCard" && state.LastMovedCard is { IsUpgradable: true } moved) UpgradeExistingCombatCard(moved, operation.Template);
        else if (operation.Template == "I:PlayTopCardAndExhaust")
            await AutoPlayFromDrawPileSequentially(choiceContext, card.Owner, 1, CardPilePosition.Top, forceExhaust: true);
        else if (operation.Template == "I:PlayTopXCards")
            await AutoPlayFromDrawPileSequentially(choiceContext, card.Owner,
                RuntimeSpecValue(card, operationIndex, "amount", card.ResolveEffectEnergyXValue()),
                CardPilePosition.Top, forceExhaust: false);
        else if (operation.Template == "I:Create")
        {
            var count = ExecutableGeneratedCardCount(amount);
            if (count == 0) return;
            var attackPool = CurrentCharacterCards(card).Where(candidate => candidate.Type == CardType.Attack).ToArray();
            if (ChaosDiagnostics.VerboseRuntime)
                Log.Info($"[AutoAnthony] Random Attack pool contains {attackPool.Length} card(s) before combat-generation filtering.");
            var created = CardFactory.GetDistinctForCombat(card.Owner, attackPool, count,
                card.Owner.RunState.Rng.CombatCardGeneration).ToArray();
            if (created.Length > 0)
            {
                UpgradeGeneratedCards(card, operationIndex, created);
                foreach (var generated in created) generated.SetToFreeThisTurn();
                await CardPileCmd.AddGeneratedCardsToCombat(created, PileType.Hand, card.Owner);
                if (ChaosDiagnostics.VerboseRuntime)
                    Log.Info("[AutoAnthony] Generated free Attack(s) in hand: "
                             + string.Join(", ", created.Select(generated => generated.Id)));
            }
            else Log.Warn("[AutoAnthony] Could not find a combat-generatable Attack for the random Attack trigger.");
        }
        else if (operation.Template == "I:AutoPlayRandomAttackFromHand")
        {
            var played = new HashSet<CardModel>();
            for (var playIndex = 0; playIndex < RandomHandAutoplayLimit(amount); playIndex++)
            {
                if (CombatManager.Instance.IsOverOrEnding) break;
                // Re-read the live hand after each autoplay. The played Attack can move cards between piles,
                // transform cards or end combat, so a single precomputed selection is not valid for later plays.
                var attacks = PileType.Hand.GetPile(card.Owner).Cards
                    .Where(candidate => candidate.Type == CardType.Attack
                        && !candidate.Keywords.Contains(CardKeyword.Unplayable)
                        && !played.Contains(candidate))
                    .ToList();
                var selectedAttack = card.Owner.RunState.Rng.Shuffle.NextItem(attacks);
                if (selectedAttack is null) break;
                played.Add(selectedAttack);
                await CardCmd.AutoPlay(choiceContext, selectedAttack, null);
            }
        }
        else if (operation.Template == "I:PlayAtRandomEnemy")
        {
            var selectedCard = state.EventCard ?? state.IterationCard ?? state.CardSlots.Values.FirstOrDefault();
            if (selectedCard is not null) await CardCmd.AutoPlay(choiceContext, selectedCard, null);
        }
        else if (operation.Template == "I:ExhaustRandomAttack")
        {
            var attacks = PileType.Hand.GetPile(card.Owner).Cards
                .Where(candidate => candidate.Type == CardType.Attack).ToList();
            var exhaustedAttack = EffectiveRuntimeSpec(card, operationIndex).Variant == "i_exhaustselectedattack"
                ? (await SelectFromHandIfAny(choiceContext, card.Owner,
                    new CardSelectorPrefs(CardSelectorPrefs.ExhaustSelectionPrompt, 1),
                    candidate => candidate.Type == CardType.Attack, card)).FirstOrDefault()
                : card.Owner.RunState.Rng.CombatCardSelection.NextItem(attacks);
            if (exhaustedAttack is not null)
            {
                state.LastExhaustedAttackDamage = decimal.ToInt32(CurrentDamage(exhaustedAttack));
                await CardCmd.Exhaust(choiceContext, exhaustedAttack);
                state.ExhaustedByCard.Add(exhaustedAttack);
            }
        }
        else if (operation.Template == "I:AddExhaustedAttackDamage")
            card.ExtraDamage += state.LastExhaustedAttackDamage;
        else if (operation.Template == "I:IncreaseDamageThisCombat") card.ExtraDamage += amount;
        else if (operation.Template == "I:Transform")
        {
            var attacks = PileType.Hand.GetPile(card.Owner).Cards
                .Where(candidate => candidate.Type == CardType.Attack && candidate.IsTransformable).ToList();
            await TransformToDerivatives(card, operationIndex, operation, attacks, CardPreviewStyle.HorizontalLayout);
        }
        else if (operation.Template == "I:PreventDrawThisTurn") await PowerCmd.Apply<NoDrawPower>(choiceContext, card.Owner.Creature, 1, card.Owner.Creature, card);
        else if (operation.Template == "I:GainTemporaryStrength") await PowerCmd.Apply<SetupStrikePower>(choiceContext, card.Owner.Creature, amount, card.Owner.Creature, card);
        else if (operation.Template == "I:GainMaxHp") await CreatureCmd.GainMaxHp(card.Owner.Creature, amount);
        else if (operation.Template == "I:DrawUntilNonAttack")
        {
            CardModel? drawn;
            do { drawn = await CardPileCmd.Draw(choiceContext, card.Owner); } while (drawn is not null && drawn.Type == CardType.Attack);
        }
        else if (operation.Template == "I:ApplyToAllEnemies") await PowerCmd.Apply<VulnerablePower>(choiceContext, card.Owner.Creature.CombatState!.HittableEnemies, amount, card.Owner.Creature, card);
    }

    private static async Task ApplyBoundPower<T>(ChaosCardModel card, PlayerChoiceContext choiceContext, decimal amount)
        where T : PowerModel
    {
        var power = (T)ModelDb.Power<T>().ToMutable();
        ChaosPowerVisuals.Bind(power, card.Definition);
        await PowerCmd.Apply(choiceContext, power, card.Owner.Creature, amount, card.Owner.Creature, card);
    }

    internal static (decimal Damage, int Hits) DamageAndHits(ChaosCardModel card, decimal baseDamage,
        ChaosExecutionState state, int baseHits = 1, int damageOperationIndex = -1)
    {
        decimal damage = baseDamage + card.ExtraDamage;
        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        var playerCombatState = card.Owner.PlayerCombatState;
        var hasDynamicHitTotal = false;
        var dynamicHitTotal = 0;
        var additionalHits = 0;
        for (var index = 0; index < card.Generated.Operations.Count; index++)
        {
            var modifier = card.Generated.Operations[index];
            if (modifier.Scope != OperationScope.Modifier) continue;
            if (!ModifierConditionMatches(card, modifier, state)) continue;
            var modifierAmount = card.OperationAmount(index);
            var modifierSpec = modifier.Template is "M:base" or "M:repeat"
                ? EffectiveRuntimeSpec(card, index)
                : null;
            if (CardEffectRules.IsCurrentBlockDamageModifier(modifier))
            {
                // M:value replaces only its own hidden T:D anchor. Older builds applied it to every damage line,
                // making unrelated printed damage disappear whenever the same card also dealt current-Block damage.
                if (damageOperationIndex >= 0
                    && CardEffectRules.CurrentBlockDamageAnchorIndex(card.Generated.Operations, index)
                        == damageOperationIndex)
                    damage = card.Owner.Creature.Block;
            }
            else if (modifier.Template == "M:DamagePerExhaustCard"
                     || modifierSpec?.Variant == "exhaust_pile_scaled")
                damage += modifierAmount * PileType.Exhaust.GetPile(card.Owner).Cards.Count;
            else if (modifierSpec?.Variant == "vulnerable_scaled")
                damage += modifierAmount * (state.Target?.GetPower<VulnerablePower>()?.Amount ?? 0);
            else if (modifierSpec?.Variant == "strike_count_scaled")
                damage += modifierAmount * (card.Owner.PlayerCombatState?.AllCards.Count(candidate =>
                    candidate.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Strike)) ?? 0);
            else if (modifier.Template == "M:RepeatPerAttackThisTurn")
            {
                hasDynamicHitTotal = true;
                dynamicHitTotal += combatState is null ? 0 : CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
                    entry.CardPlay.Player == card.Owner && entry.CardPlay.Card.Type == CardType.Attack
                    && entry.HappenedThisTurn(combatState));
            }
            else if (modifier.Template == "M:RepeatPerSkillInHand")
            {
                hasDynamicHitTotal = true;
                dynamicHitTotal += PileType.Hand.GetPile(card.Owner).Cards.Count(candidate => candidate.Type == CardType.Skill);
            }
            else if (modifier.Template == "M:DamagePerDiscardThisTurn")
                damage += modifierAmount * (combatState is null ? 0 : CombatManager.Instance.History.Entries
                    .OfType<CardDiscardedEntry>().Count(entry => entry.Actor == card.Owner.Creature
                        && entry.HappenedThisTurn(combatState)));
            else if (modifier.Template == "M:DamagePerCardDrawnCombat")
                damage += modifierAmount * CombatManager.Instance.History.Entries.OfType<CardDrawnEntry>().Count(entry => entry.Actor == card.Owner.Creature);
            else if (modifier.Template == "M:DamageMinusPerCardInHand")
            {
                var handCount = PileType.Hand.GetPile(card.Owner).Cards.Count;
                if (card.Pile?.Type == PileType.Hand) handCount--;
                damage = Math.Max(0, damage - modifierAmount * handCount);
            }
            else if (modifier.Template == "D:RepeatPerOrb")
            {
                hasDynamicHitTotal = true;
                dynamicHitTotal += playerCombatState?.OrbQueue.Orbs.Count ?? 0;
            }
            else if (modifier.Template == "D:RepeatDamage")
                additionalHits += Math.Max(1, modifierAmount);
            else if (modifier.Template == "D:RepeatPerEnergySpentThisTurn"
                     && modifier.Scope == OperationScope.Modifier)
            {
                hasDynamicHitTotal = true;
                var spent = CombatManager.Instance.History.Entries.OfType<EnergySpentEntry>()
                    .Where(entry => combatState is not null && entry.Actor == card.Owner.Creature
                        && entry.HappenedThisTurn(combatState))
                    .Sum(entry => entry.Amount) - state.CurrentCardEnergySpent;
                var threshold = index > 0
                    && card.Generated.Operations[index - 1].Template == "D:ForEachEnergySpentThisTurn"
                        ? RuntimeSpecValue(card, index - 1, "threshold", 1)
                        : 1;
                dynamicHitTotal += Math.Max(0, spent) / Math.Max(1, threshold);
            }
            else if (modifier.Template == "NCR:DamagePerCardDrawnThisTurn")
                damage += modifierAmount * CombatManager.Instance.History.Entries.OfType<CardDrawnEntry>()
                    .Count(entry => combatState is not null && entry.Actor == card.Owner.Creature
                        && entry.HappenedThisTurn(combatState));
            else if (modifier.Template == "NCR:OstyMaxHpBonusDamage")
                damage += card.Owner.Osty?.MaxHp ?? 0;
            else if (modifier.Template == "NCR:OstyCurrentHpBonusDamage")
                damage += card.Owner.Osty?.CurrentHp ?? 0;
            else if (modifier.Template == "NCR:RepeatPerVoidPlayedCombat")
            {
                hasDynamicHitTotal = true;
                dynamicHitTotal += CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
                    entry.CardPlay.Player == card.Owner && entry.CardPlay.Card.Keywords.Contains(CardKeyword.Ethereal));
            }
            else if (modifier.Template == "NCR:RepeatPerOstyAttackThisTurn")
                additionalHits += CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
                    entry.CardPlay.Player == card.Owner
                    && entry.CardPlay.Card.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.OstyAttack)
                    && combatState is not null && entry.HappenedThisTurn(combatState));
            else if (modifier.Template == "NCR:DamagePerExhaustedSoul")
            {
                var prefix = index > 0 && card.Generated.Operations[index - 1].Template == "NCR:ForEachExhaustedSoul"
                    ? card.Generated.Operations[index - 1]
                    : card.Generated.Operations.FirstOrDefault(candidate => candidate.Template == "NCR:ForEachExhaustedSoul");
                damage += modifierAmount * (prefix is null ? 0 : PileType.Exhaust.GetPile(card.Owner).Cards
                    .Count(candidate => ChaosDerivativeResolver.Matches(candidate, prefix)));
            }
            else if (modifier.Template == "NCR:DamagePerOstyAttackCard")
                damage += modifierAmount * (playerCombatState?.AllCards.Count(candidate =>
                    candidate.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.OstyAttack)) ?? 0);
            else if (modifier.Template == "R:RepeatDamage")
                additionalHits += Math.Max(1, modifierAmount);
            else if (modifier.Template == "R:BonusPerStarCostCardInHand")
                damage += modifierAmount * (playerCombatState?.AllCards.Count(candidate =>
                    candidate.CanonicalStarCost >= 0 || candidate.HasStarCostX) ?? 0);
            else if (modifier.Template == "R:RepeatPerSkillPlayedThisTurn")
            {
                hasDynamicHitTotal = true;
                dynamicHitTotal += CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
                    entry.CardPlay.Player == card.Owner && entry.CardPlay.Card.Type == CardType.Skill
                    && combatState is not null && entry.HappenedThisTurn(combatState));
            }
            else if (modifier.Template == "R:RepeatPerStarGainedThisTurn")
            {
                hasDynamicHitTotal = true;
                dynamicHitTotal += CombatManager.Instance.History.Entries.OfType<StarsModifiedEntry>()
                    .Where(entry => entry.Actor == card.Owner.Creature && entry.Amount > 0
                        && combatState is not null && entry.HappenedThisTurn(combatState)).Sum(entry => entry.Amount);
            }
            else if (modifier.Template == "R:BonusPerGeneratedCardThisCombat")
                damage += modifierAmount * CombatManager.Instance.History.Entries.OfType<CardGeneratedEntry>()
                    .Count(entry => entry.Creator == card.Owner);
            else if (modifier.Template == "CL:BonusPerUniqueDebuff")
                damage += modifierAmount * (state.Target?.Powers.Count(power =>
                    power.Type == PowerType.Debuff && power is not ITemporaryPower) ?? 0);
            else if (IsExternallyScaledDamageDependency(modifier))
            {
                // The multiplier is already applied to the following damage operation before this method is
                // called. Reading a numeric value from the prefix itself yields 0 because the printed amount
                // belongs to the payoff.
            }
            else if (modifierSpec?.Variant == "hp_loss_scaled")
                additionalHits += modifierAmount * HpLossCount(card);
            else if (modifierSpec?.Variant == "flat_extra") additionalHits += modifierAmount;
        }
        var hits = (hasDynamicHitTotal ? dynamicHitTotal : Math.Max(0, baseHits)) + additionalHits;
        return (damage, Math.Max(0, hits));
    }

    internal static void IncreaseCardDamageForRun(ChaosCardModel card, int amount)
    {
        card.ExtraDamage += amount;
        // Combat uses a clone of the card in the run deck. The Scythe's original implementation updates both
        // copies; changing only the combat copy is lost when combat ends (and is especially easy to notice on a
        // Power, because the played combat copy leaves the piles immediately).
        if (card.DeckVersion is ChaosCardModel deckVersion && !ReferenceEquals(deckVersion, card))
            deckVersion.ExtraDamage += amount;
    }

    internal static void IncreaseCardBlockForRun(ChaosCardModel card, int amount)
    {
        card.ExtraBlock += amount;
        // Powers leave combat piles as soon as they are played. Persist growth on the run-deck copy as well as the
        // combat copy, matching Scythe/Rampage-style damage growth and preserving it across later combats and saves.
        if (card.DeckVersion is ChaosCardModel deckVersion && !ReferenceEquals(deckVersion, card))
            deckVersion.ExtraBlock += amount;
        if (ChaosDiagnostics.VerboseRuntime)
            Log.Info($"[AutoAnthony] Slot {card.Definition.Slot} permanently gained {amount} base Block "
                + $"(combat={card.ExtraBlock}, deck={((card.DeckVersion as ChaosCardModel)?.ExtraBlock ?? card.ExtraBlock)})."
            );
    }

    internal static bool IsTargetVulnerableStrength(GeneratorOperation operation) =>
        operation.Template is "N:Self" or "N:StrengthPerTargetVulnerable"
        && OperationRuntimeSpecCompiler.GetOrCompile(operation).Variant == "strength_per_target_vulnerable";

    internal static bool IsExhaustPileDamageModifier(GeneratorOperation operation) =>
        operation.Template == "M:DamagePerExhaustCard"
        || operation.Template == "M:base"
        && OperationRuntimeSpecCompiler.GetOrCompile(operation).Variant == "exhaust_pile_scaled";

    internal static bool IsExternallyScaledDamageDependency(GeneratorOperation operation) =>
        operation.Template is "CL:ForEachCardPlayedCombat" or "CL:ForEachDrawPileCard";

    internal static bool IsDirectDiscardAttackAutoplay(GeneratorOperation operation) =>
        operation.Template == "CL:ProxyAtomic_BeatDown";

    private static decimal CurrentDamage(CardModel card)
    {
        decimal damage = 0;
        if (card is ChaosCardModel chaos)
        {
            var damageIndex = chaos.Generated.Operations.ToList().FindIndex(CardEffectRules.IsEnemyDamage);
            if (damageIndex >= 0)
                damage = DamageAndHits(chaos,
                    chaos.OperationAmount(damageIndex),
                    new ChaosExecutionState(), damageOperationIndex: damageIndex).Damage;
        }
        else if (card.DynamicVars.ContainsKey("CalculatedDamage")) damage = card.DynamicVars.CalculatedDamage.Calculate(null);
        else if (card.DynamicVars.ContainsKey("Damage")) damage = card.DynamicVars.Damage.BaseValue;
        else if (card.DynamicVars.ContainsKey("OstyDamage")) damage = card.DynamicVars.OstyDamage.BaseValue;
        return Hook.ModifyDamage(card.Owner.RunState, card.Owner.Creature.CombatState, null, card.Owner.Creature,
            damage, ValueProp.Move, card, null, ModifyDamageHookType.All, CardPreviewMode.None, out _);
    }

    internal static decimal ApplyBlockModifiers(ChaosCardModel card, int baseBlock)
    {
        decimal block = baseBlock + card.ExtraBlock;
        for (var index = 0; index < card.Generated.Operations.Count; index++)
        {
            var modifier = card.Generated.Operations[index];
            if (modifier.Scope != OperationScope.Modifier || modifier.Template != "M:base") continue;
            var spec = EffectiveRuntimeSpec(card, index);
            if (spec.Variant == "strength_scaled")
            {
                var strength = Math.Max(0, card.Owner.Creature.GetPower<StrengthPower>()?.Amount ?? 0);
                var interval = RuntimeSpecValue(card, index, "strength_interval", 1);
                var bonus = RuntimeSpecValue(card, index, "block_per_interval", 0);
                block += strength / Math.Max(1, interval) * bonus;
            }
        }
        return block;
    }

    private static bool ConditionMatches(ChaosCardModel card, GeneratorOperation trigger, ChaosExecutionState state)
    {
        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(trigger);
        var kind = spec.Condition?.Kind;
        if (kind is null) return true;
        var threshold = spec.Values.FirstOrDefault(value => value.Id == "threshold")?.BaseValue ?? 1;
        return kind switch
        {
            "fatal" => state.LastAttackKilled,
            "exhaust_pile_minimum" => PileType.Exhaust.GetPile(card.Owner).Cards.Count >= threshold,
            "card_exhausted_this_turn" => combatState is not null
                && CombatManager.Instance.History.Entries.OfType<CardExhaustedEntry>().Any(entry =>
                    entry.Actor == card.Owner.Creature && entry.HappenedThisTurn(combatState)),
            "owner_lost_hp_this_turn" => combatState is not null
                && CombatManager.Instance.History.Entries.OfType<DamageReceivedEntry>().Any(entry =>
                    entry.Receiver == card.Owner.Creature && entry.Result.UnblockedDamage > 0
                    && entry.HappenedThisTurn(combatState)),
            "target_has_vulnerable" => (state.Target?.GetPower<VulnerablePower>()?.Amount ?? 0) > 0,
            "target_has_poison" => (state.Target?.GetPower<PoisonPower>()?.Amount ?? 0) > 0,
            "draw_pile_empty" => PileType.Draw.GetPile(card.Owner).Cards.Count == 0,
            "last_drawn_card_is_skill" => state.LastDrawnCards.Count == 1
                && state.LastDrawnCards[0].Type == CardType.Skill,
            "cards_played_this_turn_below" => combatState is not null
                && CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
                    entry.CardPlay.Player == card.Owner && entry.HappenedThisTurn(combatState)) < threshold,
            "enemy_intends_attack" => state.Target?.Monster?.IntendsToAttack == true,
            "osty_alive" => card.Owner.IsOstyAlive,
            "doom_applied_this_turn" => combatState is not null
                && CombatManager.Instance.History.Entries.OfType<PowerReceivedEntry>().Any(entry =>
                    entry.HappenedThisTurn(combatState) && entry.Power is DoomPower
                    && entry.Applier == card.Owner.Creature),
            "first_play_of_this_card_this_turn" => combatState is not null
                && !CombatManager.Instance.History.CardPlaysFinished.Any(entry =>
                    entry.CardPlay.Card == card && entry.HappenedThisTurn(combatState)),
            "osty_attacked_this_turn" => combatState is not null
                && CombatManager.Instance.History.Entries.OfType<CreatureAttackedEntry>().Any(entry =>
                    entry.Actor == card.Owner.Osty && entry.HappenedThisTurn(combatState)),
            "no_attacks_in_hand" => PileType.Hand.GetPile(card.Owner).Cards
                .All(candidate => candidate.Type != CardType.Attack),
            "hand_empty" => PileType.Hand.GetPile(card.Owner).Cards.Count == 0,
            _ => true
        };
    }

    private static bool ModifierConditionMatches(ChaosCardModel card, GeneratorOperation modifier, ChaosExecutionState state)
    {
        if (!modifier.Parameters.TryGetValue("triggerIndex", out var triggerIndex)) return true;
        return ConditionMatches(card, card.Generated.Operations[triggerIndex], state);
    }

    private static GeneratorOperation? DependencyPrefix(ChaosCardModel card, int effectIndex)
    {
        if (effectIndex <= 0) return null;
        var prefix = card.Generated.Operations[effectIndex - 1];
        var effect = card.Generated.Operations[effectIndex];
        if (!CardEffectRules.IsDependencyPrefix(prefix)
            || !CardEffectRules.IsLegalDependencyPayoff(prefix, effect)) return null;
        return prefix.Parameters.GetValueOrDefault("triggerIndex", -1)
            == effect.Parameters.GetValueOrDefault("triggerIndex", -1) ? prefix : null;
    }

    private static bool DependencyConditionMatches(ChaosCardModel card, int effectIndex)
    {
        var prefix = DependencyPrefix(card, effectIndex);
        if (prefix is null) return true;
        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        return prefix.Template switch
        {
            "D:IfHasFrost" => card.Owner.PlayerCombatState?.OrbQueue.Orbs.Any(orb =>
                ChaosOrbResolver.MatchesSource(orb, prefix)) ?? false,
            "R:IfEnergyXAtLeast" => card.ResolvedEnergyXValue >= Math.Max(1,
                card.OperationAmount(card.Generated.Operations.ToList().IndexOf(prefix))),
            "R:IfCardsPlayedAtLeastThisTurn" => combatState is not null
                && CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
                    entry.CardPlay.Player == card.Owner && entry.HappenedThisTurn(combatState))
                    >= card.OperationAmount(card.Generated.Operations.ToList().IndexOf(prefix)),
            _ => true
        };
    }

    internal static int DependencyMultiplier(ChaosCardModel card, int effectIndex, Creature? target = null,
        int? priorAttackHitsOverride = null)
    {
        var prefix = DependencyPrefix(card, effectIndex);
        if (prefix is null) return 1;
        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        var playerCombatState = card.Owner.PlayerCombatState;
        var prefixIndex = card.Generated.Operations.ToList().IndexOf(prefix);
        return prefix.Template switch
        {
            "D:ForEachOrb" => playerCombatState?.OrbQueue.Orbs.Count ?? 0,
            "D:ForEachEnemy" => combatState?.HittableEnemies.Count ?? 0,
            "D:ForEachUniqueOrb" => playerCombatState?.OrbQueue.Orbs
                .Select(orb => orb.Id).Distinct().Count() ?? 0,
            "NCR:ForEachEtherealPlayedCombat" => CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
                entry.CardPlay.Player == card.Owner && entry.CardPlay.Card.Keywords.Contains(CardKeyword.Ethereal)),
            "NCR:ForEachCardDrawnThisTurn" => CombatManager.Instance.History.Entries.OfType<CardDrawnEntry>()
                .Count(entry => combatState is not null && entry.Actor == card.Owner.Creature
                    && entry.HappenedThisTurn(combatState)),
            "NCR:ForEachDoomThreshold" => Math.Max(0,
                (int)(target?.GetPower<DoomPower>()?.Amount ?? 0))
                / Math.Max(1, prefixIndex < 0 ? 10 : card.OperationAmount(prefixIndex)),
            "NCR:ForEachOstyAttackThisTurn" => CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
                entry.CardPlay.Player == card.Owner
                && entry.CardPlay.Card.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.OstyAttack)
                && combatState is not null && entry.HappenedThisTurn(combatState)),
            "NCR:ForEachExhaustedSoul" => PileType.Exhaust.GetPile(card.Owner).Cards.Count(candidate =>
                ChaosDerivativeResolver.Matches(candidate, prefix)),
            "NCR:ForEachOstyAttackCard" => playerCombatState?.AllCards.Count(candidate =>
                candidate.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.OstyAttack)) ?? 0,
            "R:ForEachPriorAttackHitOnTarget" => priorAttackHitsOverride
                ?? CountPriorAttackHits(card, target),
            "R:ForEachStarCostCard" => playerCombatState?.AllCards.Count(candidate =>
                candidate.CanonicalStarCost >= 0 || candidate.HasStarCostX) ?? 0,
            "R:ForEachSkillPlayedThisTurn" => CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
                entry.CardPlay.Player == card.Owner && entry.CardPlay.Card.Type == CardType.Skill
                && combatState is not null && entry.HappenedThisTurn(combatState)),
            "R:ForEachStarGainedThisTurn" => CombatManager.Instance.History.Entries.OfType<StarsModifiedEntry>()
                .Where(entry => entry.Actor == card.Owner.Creature && entry.Amount > 0
                    && combatState is not null && entry.HappenedThisTurn(combatState)).Sum(entry => entry.Amount),
            "R:ForEachGeneratedCardCombat" => CombatManager.Instance.History.Entries.OfType<CardGeneratedEntry>()
                .Count(entry => entry.Creator == card.Owner),
            // Gold Axe counts every completed card play in the combat, matching the original implementation.
            "CL:ForEachCardPlayedCombat" => CombatManager.Instance.History.CardPlaysFinished.Count(),
            "CL:ForEachDrawPileCard" => PileType.Draw.GetPile(card.Owner).Cards.Count,
            _ => 1
        };
    }

    private static int CountPriorAttackHits(ChaosCardModel card, Creature? target)
    {
        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        if (combatState is null || target is null) return 0;
        return CombatManager.Instance.History.Entries.OfType<DamageReceivedEntry>().Count(entry =>
            entry.Receiver == target && entry.Dealer == card.Owner.Creature
            && entry.Result.Props.IsPoweredAttack() && entry.HappenedThisTurn(combatState));
    }

    private static int HpLossCount(ChaosCardModel card) => CombatManager.Instance.History.Entries.OfType<DamageReceivedEntry>()
        .Count(entry => entry.Receiver == card.Owner.Creature && entry.Result.UnblockedDamage > 0);

    private static IEnumerable<CardModel> CurrentCharacterCards(ChaosCardModel card)
    {
        // A card obtained from Splash or another cross-character effect keeps the class/pool it came from, while
        // "current character" means the character who is actually playing it. Using Generated.Character here made
        // such cards generate from their source character instead of their owner's current chaos pool.
        var pool = card.Owner.Character.CardPool;
        return pool.GetUnlockedCards(card.Owner.UnlockState, card.Owner.RunState.CardMultiplayerConstraint);
    }

    /// <summary>
    /// CardSelectCmd reserves and begins a multiplayer choice before it checks whether a hand/pile has any legal
    /// candidates. Generated cards can combine a selector with effects that empty or transform that source first,
    /// so every interpreter-owned selection must preflight the live source and avoid creating an empty choice.
    /// </summary>
    private static async Task<IEnumerable<CardModel>> SelectFromHandIfAny(PlayerChoiceContext context, Player player,
        CardSelectorPrefs prefs, Func<CardModel, bool>? filter, AbstractModel source)
    {
        var candidates = PileType.Hand.GetPile(player).Cards.Where(filter ?? (_ => true)).ToList();
        if (candidates.Count == 0)
            return Enumerable.Empty<CardModel>();
        prefs = ClampSelectionPrefs(prefs, candidates.Count);
        if (context is ThrowingPlayerChoiceContext)
            return SelectAutomatically(player, candidates, prefs);
        // NPlayerHand normally keeps every selected holder in its centre container until the supplied source emits
        // ExecutionFinished. That visual ownership contract is fragile for generated effects: persistent triggers
        // execute through reconstructed proxy cards, and multiplayer may branch/resume the choice on a different
        // action queue from the source model. If either lifetime ends first, the selected model remains in the Hand
        // pile while its holder is permanently stranded in the centre (an empty hand slot plus an unresponsive card).
        //
        // The interpreter already owns the selected CardModel result and every operation below performs its actual
        // pile mutation explicitly. It therefore does not need source-lifetime visual retention. Passing no source
        // makes NPlayerHand return the holder as part of completing the same selection transaction, on both peers,
        // before the interpreter applies Exhaust/Discard/Transform/Move/Upgrade.
        var selected = (await CardSelectCmd.FromHand(context, player, prefs, filter, null!)).ToArray();
        var completed = CompleteMandatorySelection(candidates, selected, prefs.MinSelect, prefs.MaxSelect);
        if (completed.Count > selected.Distinct().Count())
            Log.Warn($"[AutoAnthony] Hand selector returned {selected.Length} card(s) for mandatory "
                     + $"{prefs.MinSelect}-{prefs.MaxSelect}; completed deterministically to {completed.Count}.");
        return completed;
    }

    private static async Task<IEnumerable<CardModel>> SelectFromHandForDiscardIfAny(PlayerChoiceContext context,
        Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter, AbstractModel source)
    {
        var candidates = PileType.Hand.GetPile(player).Cards.Where(filter ?? (_ => true)).ToList();
        if (candidates.Count == 0)
            return Enumerable.Empty<CardModel>();
        prefs = ClampSelectionPrefs(prefs, candidates.Count);
        if (context is ThrowingPlayerChoiceContext)
            return SelectAutomatically(player, candidates, prefs);
        // See SelectFromHandIfAny: generated selections must close their holder transaction immediately instead of
        // coupling it to a proxy/source model's ExecutionFinished event across multiplayer action queues.
        var selected = (await CardSelectCmd.FromHandForDiscard(context, player, prefs, filter, null!)).ToArray();
        return CompleteMandatorySelection(candidates, selected, prefs.MinSelect, prefs.MaxSelect);
    }

    private static async Task<IEnumerable<CardModel>> SelectFromCombatPileIfAny(PlayerChoiceContext context,
        CardPile pile, Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter = null)
    {
        var candidates = pile.Cards.Where(filter ?? (_ => true)).ToList();
        if (candidates.Count == 0)
            return Enumerable.Empty<CardModel>();
        prefs = ClampSelectionPrefs(prefs, candidates.Count);
        if (context is ThrowingPlayerChoiceContext)
            return SelectAutomatically(player, candidates, prefs);
        var selected = (await CardSelectCmd.FromCombatPile(context, pile, player, prefs, filter)).ToArray();
        var completed = CompleteMandatorySelection(candidates, selected, prefs.MinSelect, prefs.MaxSelect);
        if (completed.Count > selected.Distinct().Count())
            Log.Warn($"[AutoAnthony] {pile.Type} selector returned {selected.Length} card(s) for mandatory "
                     + $"{prefs.MinSelect}-{prefs.MaxSelect}; completed deterministically to {completed.Count}.");
        return completed;
    }

    /// <summary>
    /// Exact-count selectors are an execution contract, not merely a UI hint. A stale multiplayer response or a
    /// selector implementation returning a partial result must not silently turn "2 cards" into one. Preserve all
    /// valid player choices, remove duplicates/out-of-pool entries, then fill only the mandatory shortfall in stable
    /// source order. Optional selections (minimum zero) remain optional.
    /// </summary>
    internal static IReadOnlyList<T> CompleteMandatorySelection<T>(IReadOnlyList<T> candidates,
        IEnumerable<T> selected, int minSelect, int maxSelect) where T : notnull
    {
        var allowed = candidates.ToHashSet();
        var maximum = Math.Min(candidates.Count, Math.Max(0, maxSelect));
        var required = Math.Min(maximum, Math.Max(0, minSelect));
        var result = selected.Where(allowed.Contains).Distinct().Take(maximum).ToList();
        if (result.Count >= required) return result;
        var alreadySelected = result.ToHashSet();
        foreach (var candidate in candidates)
        {
            if (!alreadySelected.Add(candidate)) continue;
            result.Add(candidate);
            if (result.Count >= required) break;
        }
        return result;
    }

    internal static bool RecoveryRequiresCreation(int matchingCount, int recoverableCount) =>
        matchingCount == 0 && recoverableCount == 0;

    internal static (int Min, int Max) ClampedSelectionBounds(int minSelect, int maxSelect, int availableCards)
    {
        var available = Math.Max(0, availableCards);
        var max = Math.Min(Math.Max(0, maxSelect), available);
        var min = Math.Min(Math.Max(0, minSelect), max);
        return (min, max);
    }

    private static CardSelectorPrefs ClampSelectionPrefs(CardSelectorPrefs prefs, int availableCards)
    {
        var (min, max) = ClampedSelectionBounds(prefs.MinSelect, prefs.MaxSelect, availableCards);
        if (min == prefs.MinSelect && max == prefs.MaxSelect) return prefs;
        return new CardSelectorPrefs(prefs.Prompt, min, max)
        {
            RequireManualConfirmation = prefs.RequireManualConfirmation && min != max,
            Cancelable = prefs.Cancelable,
            Comparison = prefs.Comparison,
            UnpoweredPreviews = prefs.UnpoweredPreviews,
            PretendCardsCanBePlayed = prefs.PretendCardsCanBePlayed,
            ShouldGlowGold = prefs.ShouldGlowGold
        };
    }

    private static IEnumerable<CardModel> SelectAutomatically(Player player, IReadOnlyList<CardModel> candidates,
        CardSelectorPrefs prefs)
    {
        // A ThrowingPlayerChoiceContext is an explicit engine promise that this hook cannot open UI. Old snapshots
        // may still contain combinations generated before that invariant was validated. Resolve their mandatory
        // choice deterministically from combat RNG, while optional “any number” selections choose zero cards.
        var count = Math.Min(candidates.Count, Math.Max(0, prefs.MinSelect));
        if (count == 0) return Enumerable.Empty<CardModel>();
        return candidates.ToList().StableShuffle(player.RunState.Rng.CombatCardSelection).Take(count).ToArray();
    }

    private static CardPreviewStyle TransformPreviewStyle(PlayerChoiceContext context) =>
        context is ThrowingPlayerChoiceContext ? CardPreviewStyle.None : CardPreviewStyle.HorizontalLayout;

    private static Task<CardModel?> SelectFromHandForUpgradeIfAny(PlayerChoiceContext context, Player player,
        AbstractModel source)
    {
        var candidates = PileType.Hand.GetPile(player).Cards.Where(candidate => candidate.IsUpgradable).ToList();
        if (candidates.Count == 0)
            return Task.FromResult<CardModel?>(null);
        // Some engine hooks explicitly prohibit opening a UI choice. Current generation does not attach Armaments-
        // style selection to those hooks, but an older saved snapshot can. Resolve it deterministically instead of
        // passing ThrowingPlayerChoiceContext into the vanilla selector and aborting the trigger/card-play task.
        if (context is ThrowingPlayerChoiceContext)
        {
            var selected = candidates.Count == 1
                ? candidates[0]
                : candidates.StableShuffle(player.RunState.Rng.CombatCardSelection).First();
            if (ChaosDiagnostics.VerboseRuntime)
                Log.Warn($"[AutoAnthony] Auto-selected {selected.Id} for an upgrade effect running without a choice context.");
            return Task.FromResult<CardModel?>(selected);
        }
        // The returned model is upgraded in-place after this task completes; retaining its holder until an external
        // source event is unnecessary and can strand it when a multiplayer choice branches to another queue.
        return CardSelectCmd.FromHandForUpgrade(context, player, null!);
    }

    private static void UpgradeExistingCombatCard(CardModel card, string operationTemplate)
    {
        var originalPile = card.Pile;
        CardCmd.Upgrade(card);
        // Vanilla CardCmd.Upgrade mutates the model in place and never changes its pile. If another patch violates
        // that contract, identify the responsible timing in the log instead of leaving a misleading “upgrade made
        // my card disappear” report with no operation/card identity.
        if (originalPile is not null && !ReferenceEquals(originalPile, card.Pile))
            Log.Error($"[AutoAnthony] Combat upgrade {operationTemplate} moved {card.Id} from "
                      + $"{originalPile.Type} to {card.Pile?.Type.ToString() ?? "no pile"}; CardCmd.Upgrade must be in-place.");
    }

    internal static async Task AutoPlayFromDrawPileSequentially(PlayerChoiceContext context, Player player,
        int count, CardPilePosition position, bool forceExhaust)
    {
        var drawPile = PileType.Draw.GetPile(player);
        for (var index = 0; index < Math.Max(0, count); index++)
        {
            if (CombatManager.Instance.IsOverOrEnding || player.Creature.IsDead) break;
            await CardPileCmd.ShuffleIfNecessary(context, player);
            if (CombatManager.Instance.IsOverOrEnding || player.Creature.IsDead) break;
            var next = position switch
            {
                CardPilePosition.Top => drawPile.Cards.FirstOrDefault(),
                CardPilePosition.Bottom => drawPile.Cards.LastOrDefault(),
                CardPilePosition.Random => player.RunState.Rng.CombatCardSelection.NextItem(drawPile.Cards),
                _ => null
            };
            if (next is null) break;
            next.ExhaustOnNextPlay = forceExhaust;
            // Do not stage later cards in Play. OnPlayWrapper moves this one card into Play and then to its final
            // pile before we inspect the live draw pile for the next iteration.
            await CardCmd.AutoPlay(context, next, null);
        }
    }

    private static LocString SelectionPrompt(string key) => new("cards", $"AUTO_ANTHONY.{key}");

    private static bool IsLingeringTrigger(GeneratorOperation operation) => operation.Scope == OperationScope.ConditionalTrigger
        && (operation.Template is "C:untilTurnEnd" or "C:untilTurnEndCardDrawn" or "C:after" or "C:for"
            or "C:grantNextAttacksThisTurn" or "C:grantNextAttack"
            or "NCR:NextTurn" or "R:NextTurn" or "R:AtTurnStartIfInExhaust" or "D:NextTurnsStart"
            or "CL:AtNextTurnStart");

    private static bool IsCompositePowerTrigger(GeneratorOperation operation) => operation.Scope == OperationScope.ConditionalTrigger
        && (operation.Template is "C:untilTurnEnd" or "C:untilTurnEndCardDrawn" or "C:for"
            or "C:grantNextAttacksThisTurn" or "C:grantNextAttack" or "NCR:NextTurn" or "R:NextTurn" or "D:NextTurnsStart"
            or "CL:AtNextTurnStart");

    internal static bool RequiresCompositePower(GeneratorOperation operation) =>
        operation.Scope == OperationScope.AbilityTrigger
        || operation.Scope == OperationScope.AbilityRule && operation.Template is
            "A:rule" or "A:ruleShivsRetain" or "A:ruleFirstShivBonusDamage"
                or "A:ruleWeakEnemiesTakeMoreAttackDamage" or "CL:DieOnUnblockedAttack"
        || IsCompositePowerTrigger(operation);

    internal static string EffectiveText(ChaosCardModel card, int operationIndex)
    {
        var text = card.Generated.Operations[operationIndex].ChineseText;
        if (!card.IsUpgraded || card.Generated.Upgrade is null) return text;
        foreach (var effect in card.Generated.Upgrade.Effects.Where(effect => effect.OperationIndex == operationIndex))
        {
            if (effect.Kind == CardUpgradeKind.UpgradeDerivative)
            {
                var operation = card.Generated.Operations[operationIndex];
                if (!DerivativeSlotCatalog.SupportsUpgrade(operation.Template, operation.DerivativeId))
                    continue;
                var derivative = DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template);
                if (derivative is not null)
                    text = text.Replace(derivative.ChineseName, derivative.ChineseName + "+", StringComparison.Ordinal);
                continue;
            }
            if (effect.Kind == CardUpgradeKind.UpgradeGeneratedCards)
            {
                text = CardUpgradeGenerator.UpgradeRandomGenerationChinese(text);
                continue;
            }
            if (effect.Kind == CardUpgradeKind.IncreaseNumber
                && CardEffectRules.IsNonUpgradeableNumericMarker(card.Generated.Operations[operationIndex]))
                continue;
            if (effect.Kind == CardUpgradeKind.IncreaseNumber
                && OperationRuntimeSpecCompiler.ValueUsesX(card.Generated.Operations[operationIndex]))
            {
                text = OperationRuntimeSpecCompiler.IncreaseLegacyXValue(text);
                continue;
            }
            if (effect.Delta is null) continue;
            if (OperationRuntimeSpecCompiler.TryProjectLegacyExecutionUpgradeValue(
                    card.Generated.Operations[operationIndex], text, out var projection) && projection is not null)
                text = OperationRuntimeSpecCompiler.ReplaceLegacyProjectedValue(text, projection,
                    projection.BaseValue + effect.Delta.Value);
        }
        return text;
    }

    internal static OperationRuntimeSpec EffectiveRuntimeSpec(ChaosCardModel card, int operationIndex)
    {
        var operation = card.Generated.Operations[operationIndex];
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        if (!card.IsUpgraded || card.Generated.Upgrade is null) return spec;
        if (card.Generated.Upgrade.Effects.Any(effect => effect.OperationIndex == operationIndex
                && effect.Kind == CardUpgradeKind.ChooseExhaust))
            spec = OperationRuntimeSpecCompiler.AsSelectedExhaust(spec);
        foreach (var effect in card.Generated.Upgrade.Effects.Where(effect =>
                     effect.OperationIndex == operationIndex
                     && effect.Delta is not null
                     && effect.Kind is CardUpgradeKind.IncreaseNumber or CardUpgradeKind.ReduceSelfDamage
                         or CardUpgradeKind.ReduceThreshold or CardUpgradeKind.ReduceNegativeNumber))
        {
            var slotId = effect.ValueSlotId;
            // Schema 1-7 did not persist the slot. Preserve their first-number execution projection rather than
            // guessing from current localized wording outside the single legacy compiler.
            if (slotId is null
                && OperationRuntimeSpecCompiler.TryProjectLegacyExecutionUpgradeValue(operation,
                    operation.ChineseText, out var legacyProjection))
                slotId = legacyProjection?.SlotId;
            if (slotId is not null)
                spec = OperationRuntimeSpecCompiler.ApplyUpgradeDelta(spec, slotId, effect.Delta!.Value);
        }
        return spec;
    }

    internal static int FillHandTargetCount(bool returnsThisToHand) =>
        Math.Max(0, CardPile.MaxCardsInHand - (returnsThisToHand ? 1 : 0));
    internal static int ExecutableOrbRepeatCount(int amount) => Math.Max(0, amount);
    internal static int ExecutableGeneratedCardCount(int amount) => Math.Max(0, amount);
    /// <summary>
    /// The five original Defect status-to-discard clauses were authored as prose-only fixed counts: four print
    /// “a” card and Overload prints two Wounds. They consequently have no RuntimeSpec amount slot, and the generic
    /// operation amount reader returns zero. Preserve an explicit numeric slot when a future/custom component has
    /// one, but supply the native fixed count for these legacy templates so old snapshots execute as printed.
    /// </summary>
    internal static int ExecutableDerivativeDiscardCount(GeneratorOperation operation, int amount)
    {
        if (OperationRuntimeSpecCompiler.GetOrCompile(operation).Values.Any(value =>
                value.Source is "fixed" or "energy_x" or "star_x" or "special_x"))
            return ExecutableGeneratedCardCount(amount);
        return operation.Template == "D:CreateTwoWoundsInDiscard" ? 2 : 1;
    }
    internal static int GeneratedCardChoiceCandidateCount(int amount) => Math.Clamp(amount, 0, 4);
    internal static bool HasGeneratedCardChoiceCandidates(int requestedCount, int generatedCount) =>
        GeneratedCardChoiceCandidateCount(requestedCount) > 0 && generatedCount > 0;
    internal static int RandomDrawAutoplayLimit(int amount) => Math.Max(1, amount);
    internal static int RandomHandAutoplayLimit(int amount) => Math.Max(1, amount);
    /// <summary>
    /// Structured-value reader addressed by a stable ASCII slot ID. The fallback is an explicit gameplay default
    /// for operations whose contract omits the requested optional slot; it is never parsed from localized text.
    /// </summary>
    private static int RuntimeSpecValue(ChaosCardModel card, int operationIndex, string slotId, int fallback)
    {
        var spec = EffectiveRuntimeSpec(card, operationIndex);
        var slot = spec.Values.FirstOrDefault(candidate => candidate.Id == slotId);
        if (slot is null) return fallback;
        return slot.Source switch
        {
            "special_x" => Math.Max(0, card.ResolveEffectSpecialXValue() + slot.Offset),
            "energy_x" => Math.Max(0, card.ResolveEffectEnergyXValue() + slot.Offset),
            "star_x" => Math.Max(0, card.ResolveEffectStarXValue() + slot.Offset),
            _ => slot.BaseValue + slot.Offset
        };
    }

}
