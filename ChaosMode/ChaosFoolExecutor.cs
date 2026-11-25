using ChaosCardGenerator;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace AutoAnthony;

internal static partial class ChaosOperationExecutor
{
    private static bool HasDeferredTriggerAncestor(IReadOnlyList<GeneratorOperation> operations, int index)
    {
        while (operations[index].Parameters.TryGetValue("triggerIndex", out var owner))
        {
            if (owner < 0 || owner >= index) return true;
            var trigger = operations[owner];
            if (trigger.Scope == OperationScope.AbilityTrigger || IsLingeringTrigger(trigger)
                || IsFoolImmediateRepeat(trigger)) return true;
            index = owner;
        }
        return false;
    }

    private static bool ImmediateTriggerAncestorsMatch(ChaosCardModel card, int index, ChaosExecutionState state)
    {
        var operations = card.Generated.Operations;
        while (operations[index].Parameters.TryGetValue("triggerIndex", out var owner))
        {
            if (owner < 0 || owner >= index) return false;
            if (!EvaluateConditionOnce(state.ConditionResults, owner, () => ConditionMatches(card, operations[owner], state)))
                return false;
            index = owner;
        }
        return true;
    }

    private static async Task ExecuteImmediateConditionChildren(ChaosCardModel card, int index,
        PlayerChoiceContext context, CardPlay play, ChaosExecutionState state)
    {
        var trigger = card.Generated.Operations[index];
        if (IsFoolImmediateRepeat(trigger))
        {
            await ExecuteFoolRepeat(card, index, context, play, state);
            return;
        }
        if (!ConditionMatches(card, trigger, state)) return;
        for (var child = index + 1; child < card.Generated.Operations.Count; child++)
        {
            var operation = card.Generated.Operations[child];
            if (operation.Parameters.GetValueOrDefault("triggerIndex", -1) != index) continue;
            if (RequiresCompositePower(operation))
            {
                TriggerPowers.TryGetValue(card, out var parent);
                await ArmCompositePower(card, context, state.Target, child, parent);
            }
            else if (operation.Scope == OperationScope.ConditionalTrigger)
                await ExecuteImmediateConditionChildren(card, child, context, play, state);
            else if (operation.Scope != OperationScope.Modifier || operation.Template == "F:RefundStretch")
                await ExecuteWithResolvedTarget(card, child, context, play, state);
        }
    }
    private static async Task<bool> ExecuteFoolOperation(ChaosCardModel card, int index,
        PlayerChoiceContext context, CardPlay play, ChaosExecutionState state, OperationRuntimeSpec spec, int amount)
    {
        var combat = card.CombatState ?? card.Owner.Creature.CombatState;
        if (combat is null) return true;
        var operation = card.Generated.Operations[index];
        var owner = card.Owner;
        switch (operation.Template)
        {
            case "F:Aim":
                await PowerCmd.Apply<AimPower>(context, owner.Creature, amount, owner.Creature, card); return true;
            case "F:Stretch":
                await PowerCmd.Apply<StretchPower>(context, owner.Creature, amount, owner.Creature, card); return true;
            case "F:TemporaryThorns":
                await PowerCmd.Apply<ChoBarbsPower>(context, owner.Creature, amount, owner.Creature, card); return true;
            case "F:OnlyCardsThisTurn":
                await PowerCmd.Apply<EncorePower>(context, owner.Creature, amount, owner.Creature, card); return true;
            case "F:MelodyReplay":
                await PowerCmd.Apply<KiVersePower>(context, owner.Creature, amount, owner.Creature, card); return true;
            case "F:Quietus":
                await PowerCmd.Apply<QuietusPower>(context, owner.Creature, amount, owner.Creature, card); return true;
            case "F:Honing":
                await PowerCmd.Apply<HoningPower>(context, owner.Creature, amount, owner.Creature, card); return true;
            case "F:Serrated":
                await PowerCmd.Apply<SerratedArrowsPower>(context, owner.Creature, amount, owner.Creature, card); return true;
            case "F:ShotVulnerable":
                if (state.Target is { IsDead: false } enemy)
                    await PowerCmd.Apply<DislikeYouPower>(context, enemy, amount, owner.Creature, card);
                return true;
            case "F:NextCardFree":
            {
                var free = await PowerCmd.Apply<ChaosNextCardsFreePower>(context, owner.Creature, amount,
                    owner.Creature, card);
                if (free is not null) Patches.ChaosPowerVisuals.Bind(free, card.Definition);
                return true;
            }
            case "F:ArrowSlots":
                for (var i = 0; i < amount; i++) ArrowCmd.GainArrowSlot(owner);
                return true;
            case "F:CreateSadness":
            case "F:CreateJoy":
                await CreateDerivatives(card, index, operation, PileType.Hand, amount);
                return true;
            case "F:TransformSadnessToJoy":
            case "F:TransformSadnessToCopy":
            {
                var sources = PileType.Hand.GetPile(owner).Cards.OfType<Sadness>().Cast<CardModel>().ToList();
                var selected = new List<CardModel>();
                while (selected.Count < amount && sources.Count > 0)
                {
                    var pick = owner.RunState.Rng.CombatCardSelection.NextInt(sources.Count);
                    selected.Add(sources[pick]); sources.RemoveAt(pick);
                }
                if (selected.Count == 0) return true;
                var transforms = selected.Select(source =>
                {
                    var joy = operation.Template == "F:TransformSadnessToCopy"
                        ? combat.CloneCard(LiveCombatSourceCard(card) ?? card)
                        : ChaosDerivativeResolver.Create(combat, owner, operation, DerivativeIsUpgraded(card, index));
                    return new CardTransformation(source, joy);
                }).ToArray();
                await CardCmd.Transform(transforms, null, TransformPreviewStyle(context));
                return true;
            }
            case "F:TransformHandToTheShot":
            {
                if (amount <= 0) return true;
                var selected = await SelectFromHandIfAny(context, owner,
                    new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt, amount),
                    candidate => candidate.IsTransformable, card);
                var transforms = selected.Select(source =>
                {
                    var shot = combat.CreateCard<TheShot>(owner);
                    if (DerivativeIsUpgraded(card, index) && shot.IsUpgradable) CardCmd.Upgrade(shot);
                    return new CardTransformation(source, shot);
                }).ToArray();
                if (transforms.Length > 0) await CardCmd.Transform(transforms, null, TransformPreviewStyle(context));
                return true;
            }
            case "F:UpgradeRandomHand":
                foreach (var selected in PileType.Hand.GetPile(owner).Cards.Where(candidate => candidate.IsUpgradable)
                             .TakeRandom(Math.Max(0, amount), owner.RunState.Rng.CombatCardSelection).ToArray())
                    UpgradeExistingCombatCard(selected, operation.Template);
                return true;
            case "F:DislodgeAll":
                if (state.Target is { IsDead: false } impaled)
                    while (ArrowCmd.CreatureIsImpaledByPlayersArrow(impaled, owner))
                        await ArrowCmd.DislodgeArrow(impaled, owner, context, card);
                return true;
            case "F:ExhaustSadness":
            case "F:ExhaustShoot":
            {
                var sources = operation.Template == "F:ExhaustSadness"
                    ? PileType.Hand.GetPile(owner).Cards.Where(candidate => candidate is Sadness).ToList()
                    : owner.PlayerCombatState!.AllCards.Where(candidate => candidate.Pile?.Type != PileType.Exhaust
                        && candidate.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shoot)).ToList();
                var exhausted = new List<CardModel>();
                foreach (var source in sources)
                {
                    if (source.Pile is null || source.Pile.Type == PileType.Exhaust) continue;
                    await CardCmd.Exhaust(context, source);
                    if (source.Pile?.Type != PileType.Exhaust) continue;
                    exhausted.Add(source); state.ExhaustedByCard.Add(source);
                }
                state.ExhaustedByOperation[index] = exhausted;
                return true;
            }
            case "F:CreateTheShot":
            {
                var sourceIndex = FoolComponentRules.FindTheShotExhaustSource(card.Generated.Operations, index, true);
                var count = state.ExhaustedByOperation.GetValueOrDefault(sourceIndex)?.Count ?? 0;
                var shot = combat.CreateCard<TheShot>(owner);
                // v114 replaced Florescence/The Shot. Saved generated cards still own the older count-scaled
                // contract; materialize that damage on the new native Damage slot rather than calling removed APIs.
                var upgraded = DerivativeIsUpgraded(card, index);
                if (upgraded && shot.IsUpgradable) CardCmd.Upgrade(shot);
                shot.DynamicVars.Damage.BaseValue = (amount + (upgraded ? 3 : 0)) * count;
                await CardPileCmd.AddGeneratedCardToCombat(shot, PileType.Hand, owner);
                return true;
            }
            case "F:TransformThisToSadness":
                if (play.IsLastInSeries && !state.IsTriggered)
                {
                    card.SetPostPlayTransformation(combat.CreateCard<Sadness>(owner));
                    card.RequestPostPlayMove(PileType.Hand);
                }
                return true;
            case "F:RefundStretch":
                if (state.StretchAtPlayStart > 0)
                    await PowerCmd.Apply<StretchPower>(context, owner.Creature, state.StretchAtPlayStart, owner.Creature, card);
                return true;
            case "F:StretchFromDamage":
                if (state.LastDamageDealt > 0)
                    await PowerCmd.Apply<StretchPower>(context, owner.Creature, state.LastDamageDealt, owner.Creature, card);
                return true;
            case "F:SplashDamage":
                foreach (var splashTarget in combat.HittableEnemies.Where(enemy => enemy != state.Target).ToList())
                    await CreatureCmd.Damage(context, splashTarget, state.LastDamageDealt, ValueProp.Unpowered,
                        owner.Creature, card, play);
                return true;
            case "F:Dislodge":
                if (state.Target is { IsDead: false } target)
                    // Each native command removes one Arrow. Snapshot the count to avoid a trigger which adds an
                    // Arrow creating an unbounded dislodge loop in Ultimate Chaos.
                    for (var i = target.GetPowerInstances<ImpaledPower>().Count(power => power.Applier == owner.Creature); i > 0; i--)
                        await ArrowCmd.DislodgeArrow(target, owner, context, card);
                return true;
            case "F:LookChooseDiscard":
            {
                if (amount <= 0) return true;
                var draw = PileType.Draw.GetPile(owner);
                if (draw.Cards.Count == 0) await CardPileCmd.ShuffleIfNecessary(context, owner);
                else if (draw.Cards.Count < amount && PileType.Discard.GetPile(owner).Cards.Count > 0)
                {
                    var original = draw.Cards.ToArray();
                    await CardPileCmd.Shuffle(context, owner);
                    await CardPileCmd.Add(original.Where(candidate => candidate.Pile == draw).Reverse(), draw,
                        CardPilePosition.Top, null, skipVisuals: true);
                }
                var top = draw.Cards.Take(amount).ToList();
                var picked = (await SelectFromCombatPileIfAny(context, draw, owner,
                    new CardSelectorPrefs(SelectionPrompt("SELECT_HAND_CARD"), 1), top.Contains)).ToArray();
                foreach (var selected in picked.Where(candidate => candidate.Pile == draw))
                    await CardPileCmd.Add(selected, PileType.Hand);
                await CardCmd.Discard(context, top.Except(picked).Where(candidate => candidate.Pile == draw));
                return true;
            }
            case "F:DiscardShootToHand":
            case "F:DrawShootToHand":
            case "F:DiscardToDrawTop":
            {
                if (amount <= 0) return true;
                var pile = (operation.Template == "F:DrawShootToHand" ? PileType.Draw : PileType.Discard).GetPile(owner);
                var selected = await SelectFromCombatPileIfAny(context, pile, owner,
                    new CardSelectorPrefs(SelectionPrompt("SELECT_HAND_CARD"), amount),
                    candidate => operation.Template == "F:DiscardToDrawTop"
                        || candidate.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shoot));
                foreach (var source in selected.Where(candidate => candidate.Pile == pile))
                    await CardPileCmd.Add(source,
                        operation.Template == "F:DiscardToDrawTop" ? PileType.Draw : PileType.Hand, CardPilePosition.Top);
                return true;
            }
            case "F:PlayMelodyCards":
            case "F:PlayCheapest":
            {
                var hand = PileType.Hand.GetPile(owner);
                var nativeTags = operation.Template == "F:PlayMelodyCards"
                    ? FoolMelodySubset.Tags(spec.Values.Single(value => value.Id == "melody_mask").BaseValue)
                        .Select(tag => tag switch { ChaosCardGenerator.CardTag.Wah => MegaCrit.Sts2.Core.Entities.Cards.CardTag.Wah,
                            ChaosCardGenerator.CardTag.Cho => MegaCrit.Sts2.Core.Entities.Cards.CardTag.Cho,
                            _ => MegaCrit.Sts2.Core.Entities.Cards.CardTag.Ki }).ToArray() : [];
                var selected = operation.Template == "F:PlayMelodyCards"
                    ? hand.Cards.Where(candidate => candidate.Tags.Any(nativeTags.Contains)).ToList()
                    : hand.Cards.OrderBy(candidate => candidate.EnergyCost.GetWithModifiers(CostModifiers.All)).Take(Math.Max(0, amount)).ToList();
                foreach (var source in selected.Where(candidate => !ReferenceEquals(candidate, card)))
                    if (!CombatManager.Instance.IsOverOrEnding && source.Pile == hand)
                        await CardCmd.AutoPlay(context, source, null);
                return true;
            }
            case "F:RandomShootFree":
            case "F:RandomTarot":
            {
                if (amount <= 0) return true;
                IEnumerable<CardModel> candidates = operation.Template == "F:RandomTarot"
                    ? TarotCards.All.Where(candidate => candidate.IsAllowedInRun(owner.RunState))
                    : CurrentCharacterCards(card).Where(candidate =>
                        candidate.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shoot));
                if (!candidates.Any() && operation.Template == "F:RandomShootFree")
                    candidates = ModelDb.Character<MegaCrit.Sts2.Core.Models.Characters.Fool>().CardPool.GetUnlockedCards(owner.UnlockState,
                        owner.RunState.CardMultiplayerConstraint).Where(candidate =>
                        candidate.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shoot));
                candidates = RandomCombatGenerationCandidates(candidates).ToArray();
                if (!candidates.Any()) return true;
                if (operation.Template == "F:RandomTarot")
                {
                    // Mythic Form deliberately creates from the Tarot list directly. Ordinary combat factories
                    // filter out special cards, and are not the native route for this effect.
                    var tarot = candidates.ToArray();
                    for (var i = 0; i < amount; i++)
                    {
                        var created = combat.CreateCard(tarot[owner.RunState.Rng.CombatCardSelection.NextInt(tarot.Length)], owner);
                        if (GeneratedCardsAreUpgraded(card, index) && created.IsUpgradable) CardCmd.Upgrade(created);
                        await CardPileCmd.AddGeneratedCardToCombat(created, PileType.Hand, owner);
                    }
                    return true;
                }
                candidates = CardFactory.FilterForCombat(candidates).ToArray();
                if (!candidates.Any()) return true;
                var generated = CardFactory.GetForCombat(owner, candidates, amount, owner.RunState.Rng.CombatCardGeneration);
                foreach (var created in generated)
                {
                    if (GeneratedCardsAreUpgraded(card, index) && created.IsUpgradable) CardCmd.Upgrade(created);
                    if (operation.Template == "F:RandomShootFree") created.SetToFreeThisTurn();
                    await CardPileCmd.AddGeneratedCardToCombat(created, PileType.Hand, owner);
                }
                return true;
            }
            case "F:IncreaseAimRun":
            {
                var source = LiveCombatSourceCard(card);
                if (source is not null)
                {
                    source.ExtraAim += amount;
                    if (source.DeckVersion is ChaosCardModel deck && !ReferenceEquals(source, deck)) deck.ExtraAim += amount;
                }
                return true;
            }
            case "F:CostPerMelody": case "F:CostIfWahKi": return true; // Live cost hooks, not OnPlay effects.
        }
        return false;
    }

    private static bool IsFoolImmediateRepeat(GeneratorOperation operation) =>
        operation.Template is "F:ForEachMelody" or "F:ForEachEnergyX" or "F:ForEachSadnessExhausted";

    internal static int FoolRepeatPreviewCount(ChaosCardModel card, int index) =>
        card.Generated.Operations[index].Template switch
        {
            "F:ForEachMelody" => MelodyCmd.MelodiesThisTurn(card.Owner),
            "F:ForEachEnergyX" => RuntimeSpecValue(card, index, "count", 0),
            "F:ForEachSadnessExhausted" => PileType.Hand.GetPile(card.Owner).Cards.Count(candidate => candidate is Sadness),
            _ => 0
        };

    private static int FoolImmediateRepeatCount(ChaosCardModel card, int index, ChaosExecutionState state) =>
        card.Generated.Operations[index].Template switch
        {
            "F:ForEachMelody" => MelodyCmd.MelodiesThisTurn(card.Owner),
            "F:ForEachEnergyX" => RuntimeSpecValue(card, index, "count", 0),
            "F:ForEachSadnessExhausted" when state.ExhaustedByOperation.Count == 0 =>
                FoolRepeatPreviewCount(card, index),
            "F:ForEachSadnessExhausted" => state.ExhaustedByOperation
                .Where(pair => pair.Key < index && card.Generated.Operations[pair.Key].Parameters.GetValueOrDefault("triggerIndex", -1)
                    == card.Generated.Operations[index].Parameters.GetValueOrDefault("triggerIndex", -1))
                .OrderByDescending(pair => pair.Key).Select(pair => pair.Value.Count(candidate => candidate is Sadness))
                .FirstOrDefault(),
            _ => 0
        };

    private static async Task ExecuteFoolRepeat(ChaosCardModel card, int index, PlayerChoiceContext context,
        CardPlay play, ChaosExecutionState state)
    {
        var count = FoolImmediateRepeatCount(card, index, state);
        var children = card.Generated.Operations.Select((operation, childIndex) => (operation, childIndex))
            .Where(item => item.operation.Parameters.GetValueOrDefault("triggerIndex", -1) == index
                && item.operation.Scope is not (OperationScope.Modifier or OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger))
            .Select(item => item.childIndex).ToArray();
        for (var repeat = 0; repeat < count && !CombatManager.Instance.IsOverOrEnding; repeat++)
            foreach (var child in children) await ExecuteWithResolvedTarget(card, child, context, play, state);
    }
}
