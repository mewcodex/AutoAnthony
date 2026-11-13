using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using AutoAnthony;
using ChaosCardGenerator;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using CardTag = ChaosCardGenerator.CardTag;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}
static void Field(object value, Type owner, string name, object? contents) =>
    AccessTools.Field(owner, name).SetValue(value, contents);
var assembly = typeof(ChaosCardModel).Assembly;
var executor = assembly.GetType("AutoAnthony.ChaosOperationExecutor", true)!;
var playMethod = AccessTools.Method(executor, "Play");
var harmony = new Harmony("autoanthony.tests.self-mutation");
var settings = assembly.GetType("AutoAnthony.ChaosModSettings", true)!;
harmony.Patch(settings.TypeInitializer!, prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.Skip)));
AccessTools.Field(settings, "_loaded").SetValue(null, true);
harmony.Patch(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.TriggerAnim)),
    prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.NoAnimation)));
harmony.Patch(AccessTools.Method(executor, "TryExecuteStructuredDamage"),
    prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.RecordDamage)));
harmony.Patch(AccessTools.Method(typeof(PowerCmd), nameof(PowerCmd.Apply),
        [typeof(PlayerChoiceContext), typeof(PowerModel), typeof(Creature), typeof(decimal), typeof(Creature), typeof(CardModel), typeof(bool)]),
    prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.CapturePower)));
harmony.Patch(AccessTools.Method(typeof(PowerCmd), nameof(PowerCmd.Remove), [typeof(PowerModel)]),
    prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.RemovePower)));
harmony.Patch(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.GainBlock),
        [typeof(Creature), typeof(decimal), typeof(MegaCrit.Sts2.Core.ValueProps.ValueProp), typeof(CardPlay), typeof(bool)]),
    prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.RecordBlock)));

var run = DispatchProxy.Create<IRunState, RunProxy>();
var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
Field(player, typeof(Player), "<NetId>k__BackingField", 1UL);
Field(player, typeof(Player), "<Character>k__BackingField", RuntimeHelpers.GetUninitializedObject(typeof(Defect)));
Field(player, typeof(Player), "_runState", run);
Field(player, typeof(Player), "_relics", new List<RelicModel>());
Field(player, typeof(Player), "<Deck>k__BackingField", new CardPile(PileType.Deck));
var combat = new CombatState(runState: run);
var creature = new Creature(player, 70, 70) { CombatState = combat };
Field(player, typeof(Player), "<Creature>k__BackingField", creature);
var state = (PlayerCombatState)RuntimeHelpers.GetUninitializedObject(typeof(PlayerCombatState));
Field(state, typeof(PlayerCombatState), "_player", player);
foreach (var (name, pile) in new[] { ("Hand", PileType.Hand), ("DrawPile", PileType.Draw),
             ("DiscardPile", PileType.Discard), ("ExhaustPile", PileType.Exhaust), ("PlayPile", PileType.Play) })
    Field(state, typeof(PlayerCombatState), $"<{name}>k__BackingField", new CardPile(pile));
Field(player, typeof(Player), "<PlayerCombatState>k__BackingField", state);
var definitions = (IDictionary)AccessTools.Field(typeof(ChaosRunDefinitions), "Definitions").GetValue(null)!;
((Dictionary<GeneratedCharacter, bool>)AccessTools.Field(typeof(ChaosRunDefinitions), "DefinitionBalancedValueModes")
    .GetValue(null)!)[GeneratedCharacter.Defect] = true;
var prototypes = Enum.GetValues<GeneratedCharacter>().SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms).ToArray();
GeneratorOperation Op(string template, int? value = null, int trigger = -1)
{
    var atom = prototypes.First(atom => atom.Template == template);
    var op = new GeneratorOperation(template, atom.Scope, atom.ChineseText,
        trigger < 0 ? [] : new Dictionary<string, int> { ["triggerIndex"] = trigger },
        RequiresSingleTarget: atom.RequiresSingleTarget, RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom),
        LocalizedText: atom.LocalizedText);
    if (value is { } number)
    {
        var slot = OperationRuntimeSpecCompiler.UpgradeValueSlot(op)!;
        if (OperationRuntimeSpecCompiler.StaticLiteralValue(op, slot) != number)
        {
            if (!OperationRuntimeSpecCompiler.TryReplaceFixedValue(op, slot, number, out var changed))
                throw new Exception("Cannot bind numeric fixture " + template);
            op = changed;
        }
    }
    return op;
}
GeneratedCard Definition(IReadOnlyList<GeneratorOperation> operations, GeneratedCardType type = GeneratedCardType.Attack,
    IReadOnlyList<CardTag>? tags = null) => new(2, type, TargetMode.Other, GeneratedRarity.Uncommon, "", tags ?? [], operations,
        Character: GeneratedCharacter.Defect);
ChaosCardModel Card(GeneratedCard definition)
{
    definitions[GeneratedCharacter.Defect] = new[] { new ChaosCardDefinition(0, definition, "", "", "", "", "") };
    ModelDb.Inject(typeof(ChaosDefectCard000));
    var card = (ChaosCardModel)ModelDb.Card<ChaosDefectCard000>().ToMutable();
    AccessTools.Method(typeof(ChaosCardModel), "ApplyCapturedDefinition").Invoke(card, [definition]);
    combat.AddCard(card, player);
    return card;
}
void Move(CardModel card, CardPile target)
{
    card.Pile?.RemoveInternal(card, silent: true);
    target.AddInternal(card, silent: true);
}
async Task Play(ChaosCardModel card)
{
    Move(card, state.PlayPile);
    var play = new CardPlay { Card = card, Player = player, Target = null, IsAutoPlay = false, PlayIndex = 0,
        Resources = new ResourceInfo { EnergySpent = 2, EnergyValue = 2, StarsSpent = 0, StarValue = 0 },
        ResultPile = PileType.Exhaust, PlayCount = 1 };
    await ((Task)playMethod.Invoke(null, [card, new ThrowingPlayerChoiceContext(), play])!);
}

// Exercise the real direct-play loop. Only rendering, damage delivery and Power UI installation are stubbed.
var rolling = Card(Definition([Op("N:AllD", 9), Op("CL:IncreaseRollingDamage", 5),
    Op("C:NextTurnStart"), Op("CL:ReturnThisToHand", trigger: 2)], tags: [CardTag.Exhaust]));
ModelDb.Inject(typeof(ChaosCompositePower));
ModelDb.Inject(typeof(ChaosDefectCard000));
var limiterA = (ChaosCompositePower)ModelDb.Power<ChaosCompositePower>().ToMutable();
var limiterB = (ChaosCompositePower)ModelDb.Power<ChaosCompositePower>().ToMutable();
var consume = AccessTools.Method(typeof(ChaosCompositePower), "TryConsumeTimepointTrigger");
for (var activation = 0; activation < 20; activation++)
    Check((bool)consume.Invoke(limiterA, [7, 100L])!, "first Power consumes its own trigger allowance");
Check((bool)consume.Invoke(limiterB, [7, 100L])! && !(bool)consume.Invoke(limiterA, [7, 100L])!,
    "another Power cannot clear or consume the first Power's timepoint budget");
var limiterClone = (ChaosCompositePower)limiterA.ClonePreservingMutability();
Check(!(bool)consume.Invoke(limiterClone, [7, 100L])!, "cloning cannot reset a consumed timepoint budget");
await Play(rolling);
Check(Hooks.Damage.Last() == 9m, "first play uses the original area damage");
Move(rolling, state.ExhaustPile);
Move(rolling, state.Hand);
await Play(rolling);
Check(Hooks.Damage.Last() == 14m, "Exhaust and return preserve the increase on the second play");
Move(rolling, state.Hand);
rolling.DynamicVars["Damage0"].UpdateCardPreview(rolling, CardPreviewMode.None, null, runGlobalHooks: false);
Check(rolling.DynamicVars["Damage0"].PreviewValue == 19m, "area-damage preview includes accumulated growth exactly once");
var copy = (ChaosCardModel)rolling.ClonePreservingMutability();
await Play(copy);
Check(Hooks.Damage.Last() == 19m, "a copied card retains existing combat growth");
await Play(rolling);
Check(Hooks.Damage.Last() == 19m, "growing a copy cannot mutate its original");
var exposedCopy = (ChaosCardModel)rolling.ClonePreservingMutability();
var originalGrowth = rolling.OperationDamageGrowth[1];
exposedCopy.OperationDamageGrowth[1] += 100;
Check(rolling.OperationDamageGrowth[1] == originalGrowth, "native card cloning isolates mutable saved growth arrays");
var captured = AccessTools.Method(typeof(ChaosCardModel), "ApplyCapturedOperationValues");
var legacyBonus = AccessTools.Method(typeof(ChaosCardModel), "CapturedExternalDamageBonus");
captured.Invoke(rolling, [new[] { -2, 3 }]);
var bonusCopy = (ChaosCardModel)rolling.ClonePreservingMutability();
captured.Invoke(bonusCopy, [new[] { -2, 7 }]);
Check((int)legacyBonus.Invoke(rolling, [1])! == 3 && (int)legacyBonus.Invoke(bonusCopy, [1])! == 7,
    "restoring legacy bonuses on a clone cannot clear or change the original card");
captured.Invoke(rolling, [Array.Empty<int>()]);
var fresh = Card(rolling.Generated);
await Play(fresh);
Check(Hooks.Damage.Last() == 9m, "a fresh copy of the definition starts without combat growth");

var separate = Card(Definition([Op("N:AllD", 7), Op("CL:IncreaseRollingDamage", 3), Op("N:AllD", 11)]));
Hooks.Damage.Clear();
await Play(separate);
await Play(separate);
Check(Hooks.Damage.SequenceEqual(new[] { 7m, 11m, 10m, 11m }), "growth only affects its paired area-damage field");
AccessTools.Field(typeof(ModelIdSerializationCache), "_initialized").SetValue(null, true);
ModelIdSerializationCache.CacheSavedPropertiesForTypeDebug(typeof(ChaosDefectCard000));
var saved = SavedProperties.From(rolling)!;
Check(saved.intArrays!.Single(value => value.name == nameof(ChaosCardModel.OperationDamageGrowth))
    .value.SequenceEqual(rolling.OperationDamageGrowth), "native save properties retain field-specific growth");
var restored = Card(rolling.Generated);
saved.Fill(restored);
Check(restored.OperationDamageGrowth.SequenceEqual(rolling.OperationDamageGrowth), "restored growth keeps its operation binding");
var beforeEdit = Card(separate.Generated);
await Play(beforeEdit);
var oldEffects = beforeEdit.Generated.Operations;
beforeEdit.ApplyTinkeredDefinition(beforeEdit.Generated with { Operations =
    [oldEffects[2], oldEffects[0], oldEffects[1]] });
Hooks.Damage.Clear();
await Play(beforeEdit);
Check(Hooks.Damage.SequenceEqual(new[] { 11m, 10m }), "reordering components keeps growth on its original damage field");
var deleted = Card(separate.Generated);
await Play(deleted);
deleted.ApplyTinkeredDefinition(deleted.Generated with { Operations = [deleted.Generated.Operations[2]] });
Hooks.Damage.Clear();
await Play(deleted);
Check(Hooks.Damage.SequenceEqual(new[] { 11m }), "deleting the grown field cannot donate its bonus to another damage field");
var noGrowth = Card(rolling.Generated);
Check(SavedProperties.From(noGrowth)?.intArrays?.Any(value => value.name == nameof(ChaosCardModel.OperationDamageGrowth)) != true,
    "ordinary cards do not serialize an empty growth field");

var conditional = Card(Definition([Op("C:ifTargetPoisoned"), Op("N:AllD", 7, 0),
    Op("CL:IncreaseRollingDamage", 3, 0), Op("N:AllD", 11)]));
Hooks.Damage.Clear();
await Play(conditional);
Check(Hooks.Damage.SequenceEqual(new[] { 11m }) && conditional.OperationDamageGrowth.Length == 0,
    "a failed condition cannot apply its attached damage growth");

var ongoing = Card(Definition([Op("A:turnStart"), Op("N:AllD", 5, 0),
    Op("CL:IncreaseRollingDamage", 3, 0)], GeneratedCardType.Power));
await Play(ongoing);
var ongoingPower = Hooks.Powers.Last();
var fire = AccessTools.Method(typeof(ChaosCompositePower), "FireExternalTriggerAsync");
Hooks.Damage.Clear();
for (var turn = 0; turn < 3; turn++)
    await ((Task)fire.Invoke(ongoingPower, ["turn_start", new ThrowingPlayerChoiceContext(), null, null, null, 0m])!);
Check(Hooks.Damage.SequenceEqual(new[] { 5m, 8m, 11m }) && ongoing.OperationDamageGrowth.Length == 0,
    "persistent rolling growth remains owned by its Power and is not applied twice");
var multiRolling = Card(Definition([Op("A:turnStart"), Op("N:AllD", 5, 0),
    Op("CL:IncreaseRollingDamage", 3, 0), Op("N:AllD", 13, 0)], GeneratedCardType.Power));
await Play(multiRolling);
var multiPower = Hooks.Powers.Last();
Hooks.Damage.Clear();
for (var turn = 0; turn < 2; turn++)
    await ((Task)fire.Invoke(multiPower, ["turn_start", new ThrowingPlayerChoiceContext(), null, null, null, 0m])!);
Check(Hooks.Damage.SequenceEqual(new[] { 5m, 13m, 8m, 13m }), "triggered growth never overwrites a separate damage payoff");
var twoRolling = Card(Definition([Op("A:turnStart"), Op("N:AllD", 5, 0),
    Op("CL:IncreaseRollingDamage", 3, 0), Op("A:whenCardDrawnDuringTurn"), Op("N:AllD", 13, 3),
    Op("CL:IncreaseRollingDamage", 2, 3)], GeneratedCardType.Power));
await Play(twoRolling);
var twoPower = Hooks.Powers.Last();
Hooks.Damage.Clear();
for (var turn = 0; turn < 2; turn++)
{
    await ((Task)fire.Invoke(twoPower, ["turn_start", new ThrowingPlayerChoiceContext(), null, null, null, 0m])!);
    await ((Task)fire.Invoke(twoPower, ["card_drawn_during_turn", new ThrowingPlayerChoiceContext(), null, null, null, 77m])!);
}
Check(Hooks.Damage.SequenceEqual(new[] { 5m, 13m, 8m, 15m }),
    "independent rolling fields retain separate totals and ignore unrelated event amounts");
var legacy = Card(ongoing.Generated);
await Play(legacy);
var legacyPower = Hooks.Powers.Last();
legacyPower.RollingDamage = 23;
legacyPower.RollingDamageGrowth = [];
var checksum = AccessTools.Method(typeof(ChaosCompositePower), "CaptureMultiplayerState");
var beforeHover = (int[])checksum.Invoke(legacyPower, null)!;
var shown = (IReadOnlyList<GeneratorOperation>)AccessTools.Method(typeof(ChaosCompositePower), "EffectiveDescriptionOperations")
    .Invoke(legacyPower, null)!;
Check(OperationRuntimeSpecCompiler.StaticLiteralValue(shown.First(op => op.Template == "N:AllD"), "damage") == 23
    && beforeHover.SequenceEqual((int[])checksum.Invoke(legacyPower, null)!),
    "legacy Power preview displays its saved total without mutating multiplayer state");
Hooks.Damage.Clear();
for (var turn = 0; turn < 2; turn++)
    await ((Task)fire.Invoke(legacyPower, ["turn_start", new ThrowingPlayerChoiceContext(), null, null, null, 0m])!);
Check(Hooks.Damage.SequenceEqual(new[] { 23m, 26m }), "legacy absolute rolling damage migrates without loss or duplication");
ModelIdSerializationCache.CacheSavedPropertiesForTypeDebug(typeof(ChaosCompositePower));
var powerSaved = SavedProperties.From(legacyPower)!;
var restoredPower = (ChaosCompositePower)ModelDb.Power<ChaosCompositePower>().ToMutable();
powerSaved.Fill(restoredPower);
Field(restoredPower, typeof(PowerModel), "_owner", creature);
Hooks.Damage.Clear();
await ((Task)fire.Invoke(restoredPower, ["turn_start", new ThrowingPlayerChoiceContext(), null, null, null, 0m])!);
Check(Hooks.Damage.SequenceEqual(new[] { 29m }), "restored Power growth is not migrated a second time");
var powerCopy = (ChaosCompositePower)legacyPower.ClonePreservingMutability();
var savedGrowth = legacyPower.RollingDamageGrowth[1];
var savedCapture = legacyPower.CapturedOperationValues[1];
powerCopy.RollingDamageGrowth[1] += 100;
powerCopy.CapturedOperationValues[1] += 100;
Check(legacyPower.RollingDamageGrowth[1] == savedGrowth && legacyPower.CapturedOperationValues[1] == savedCapture,
    "native Power cloning isolates mutable growth and captured-value arrays");
var xDamage = Op("N:AllD") with { RuntimeSpec = Op("N:AllD").RuntimeSpec! with
{
    Values = Op("N:AllD").RuntimeSpec!.Values.Select(value => value.Id == "damage"
        ? value with { Source = "energy_x", BaseValue = 0, Offset = 0 } : value).ToArray()
}};
var xPowerCard = Card(Definition([Op("A:turnStart"), xDamage with
    { Parameters = new Dictionary<string, int> { ["triggerIndex"] = 0 } },
    Op("CL:IncreaseRollingDamage", 2, 0)], GeneratedCardType.Power) with { Cost = -1 });
AccessTools.Method(typeof(ChaosCardModel), "SetResolvedXValues").Invoke(xPowerCard, [3, 0]);
await Play(xPowerCard);
var xPower = Hooks.Powers.Last();
Hooks.Damage.Clear();
for (var turn = 0; turn < 2; turn++)
    await ((Task)fire.Invoke(xPower, ["turn_start", new ThrowingPlayerChoiceContext(), null, null, null, 0m])!);
Check(Hooks.Damage.SequenceEqual(new[] { 3m, 5m }), "X-derived rolling damage keeps its captured X and additive growth separate");

// Two generated copies with identical definitions, neither with a permanent DeckVersion.
var drawCost = Definition([Op("A:whenCardDrawnDuringTurn"), Op("I:ReduceThisCardCostCombat", 1, 0)], GeneratedCardType.Skill);
var unrelated = Card(drawCost);
Move(unrelated, state.DiscardPile);
var costSource = Card(drawCost);
await Play(costSource);
Move(costSource, state.DiscardPile);
var costPower = Hooks.Powers.Last();
await costPower.AfterCardDrawn(new ThrowingPlayerChoiceContext(), unrelated, fromHandDraw: false);
Check(costSource.EnergyCost.GetWithModifiers(CostModifiers.Local) == 1,
    "draw-triggered combat reduction mutates its exact applying card");
Check(unrelated.EnergyCost.GetWithModifiers(CostModifiers.Local) == 2,
    "an identical unrelated combat copy keeps its own cost");
await costPower.AfterCardDrawn(new ThrowingPlayerChoiceContext(), unrelated, fromHandDraw: true);
Check(costSource.EnergyCost.GetWithModifiers(CostModifiers.Local) == 1,
    "normal opening-hand draw does not satisfy the during-turn trigger");
await costPower.AfterCardDrawn(new ThrowingPlayerChoiceContext(), unrelated, fromHandDraw: false);
Check(costSource.EnergyCost.GetWithModifiers(CostModifiers.Local) == 0,
    "subsequent draw events continue reducing the real card");
var removedSource = Card(drawCost);
await Play(removedSource);
var removedPower = Hooks.Powers.Last();
removedSource.Pile?.RemoveInternal(removedSource, silent: true);
combat.RemoveCard(removedSource);
await removedPower.AfterCardDrawn(new ThrowingPlayerChoiceContext(), unrelated, fromHandDraw: false);
Check(unrelated.EnergyCost.GetWithModifiers(CostModifiers.Local) == 2,
    "removing the actual source never redirects its later mutations to another identical card");
var cloneSource = Card(drawCost);
await Play(cloneSource);
Move(cloneSource, state.DiscardPile);
var clonedPower = (ChaosCompositePower)Hooks.Powers.Last().ClonePreservingMutability();
Field(clonedPower, typeof(PowerModel), "_owner", creature);
await clonedPower.AfterApplied(creature, null);
await clonedPower.AfterCardDrawn(new ThrowingPlayerChoiceContext(), unrelated, fromHandDraw: false);
Check(cloneSource.EnergyCost.GetWithModifiers(CostModifiers.Local) == 1
    && unrelated.EnergyCost.GetWithModifiers(CostModifiers.Local) == 2,
    "cloning a Power retains its exact source rather than guessing another copy");
var orphaned = (ChaosCompositePower)ModelDb.Power<ChaosCompositePower>().ToMutable();
SavedProperties.From(costPower)!.Fill(orphaned);
Field(orphaned, typeof(PowerModel), "_owner", creature);
await orphaned.AfterCardDrawn(new ThrowingPlayerChoiceContext(), unrelated, fromHandDraw: false);
Check(unrelated.EnergyCost.GetWithModifiers(CostModifiers.Local) == 2,
    "a legacy Power without source identity cannot guess a card from its definition");
var deckSource = Card(drawCost);
Move(deckSource, player.Deck);
var combatSource = Card(drawCost);
combatSource.DeckVersion = deckSource;
await Play(combatSource);
Move(combatSource, state.DiscardPile);
var withDeckIdentity = (ChaosCompositePower)ModelDb.Power<ChaosCompositePower>().ToMutable();
SavedProperties.From(Hooks.Powers.Last())!.Fill(withDeckIdentity);
Field(withDeckIdentity, typeof(PowerModel), "_owner", creature);
await withDeckIdentity.AfterCardDrawn(new ThrowingPlayerChoiceContext(), unrelated, fromHandDraw: false);
Check(combatSource.EnergyCost.GetWithModifiers(CostModifiers.Local) == 1
    && deckSource.EnergyCost.GetWithModifiers(CostModifiers.Local) == 2,
    "a restored Power can resolve a recorded deck identity without changing the permanent deck");
combatSource.Pile?.RemoveInternal(combatSource, silent: true);
combat.RemoveCard(combatSource);
await withDeckIdentity.AfterCardDrawn(new ThrowingPlayerChoiceContext(), unrelated, fromHandDraw: false);
Check(unrelated.EnergyCost.GetWithModifiers(CostModifiers.Local) == 2,
    "a missing recorded combat clone never falls back to another same-definition card");

var compatibility = AccessTools.Method(typeof(ComponentAssemblyGenerator), "IsCompatible");
var generator = new ComponentAssemblyGenerator(new Random(152), GeneratedCharacter.Defect, balancedValues: true);
var powerOps = new[] { Op("A:whenCardDrawnDuringTurn") };
var atomCost = prototypes.First(atom => atom.Template == "I:ReduceThisCardCostCombat");
Check(!(bool)compatibility.Invoke(generator, [GeneratedCardType.Power, TargetMode.Other, 2, false, atomCost, powerOps])!,
    "new Power assemblies exclude meaningless post-play self-cost changes");
var passiveCost = prototypes.First(atom => atom.Template == "R:CostDownWhenDrawn");
Check((bool)compatibility.Invoke(generator, [GeneratedCardType.Power, TargetMode.Other, 2, false, passiveCost,
        new[] { Op("R:WheneverDrawn") }])!,
    "passive draw-time cost reduction remains available to Powers");
var lethalitySource = Card(Definition([Op("NCR:FirstAttackPlayedEachTurn"),
    Op("M:TriggeredAttackDamagePercent", 50, 0)], GeneratedCardType.Power));
await Play(lethalitySource);
var lethality = Hooks.Powers.Last();
var attack = Card(Definition([Op("NCR:OstyDamage", 6)]));
var ostyDealer = new Creature(player, 10, 10) { CombatState = combat };
Check(lethality.ModifyDamageMultiplicative(null, 6m, MegaCrit.Sts2.Core.ValueProps.ValueProp.Move,
    ostyDealer, attack, null) == 1.5m, "Lethality applies its printed percentage to an owned Osty Attack");
Check(lethality.ModifyDamageMultiplicative(null, 6m, MegaCrit.Sts2.Core.ValueProps.ValueProp.Move,
    creature, attack, null) == 1.5m, "Lethality still applies to an owned player Attack");
Check(lethality.ModifyDamageMultiplicative(null, 6m, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered,
    ostyDealer, attack, null) == 1m, "unpowered pet damage cannot gain Lethality's Attack multiplier");
var skill = Card(Definition([Op("N:B", 4)], GeneratedCardType.Skill));
Check(lethality.ModifyDamageMultiplicative(null, 6m, MegaCrit.Sts2.Core.ValueProps.ValueProp.Move,
    ostyDealer, skill, null) == 1m, "a Skill does not receive the Attack-only modifier");

var nativeLethality = (PowerModel)RuntimeHelpers.GetUninitializedObject(
    typeof(MegaCrit.Sts2.Core.Models.Powers.LethalityPower));
Field(nativeLethality, typeof(AbstractModel), "<IsMutable>k__BackingField", true);
Field(nativeLethality, typeof(PowerModel), "_owner", creature);
Field(nativeLethality, typeof(PowerModel), "_amount", 50);
var history = CombatManager.Instance.History;
var entries = (IList)AccessTools.Field(history.GetType(), "_entries").GetValue(history)!;
void Started(CardModel startedCard) => entries.Add(
    new MegaCrit.Sts2.Core.Combat.History.Entries.CardPlayStartedEntry(new CardPlay
    {
        Card = startedCard, Player = player, Target = null, ResultPile = PileType.Discard,
        Resources = new ResourceInfo { EnergySpent = 0, EnergyValue = 0, StarsSpent = 0, StarValue = 0 },
        IsAutoPlay = false, PlayIndex = 0, PlayCount = 1
    }, combat.RoundNumber, combat.CurrentSide, history, []));
void MatchesNative(CardModel source, decimal expected, string scenario, bool duringPlay = false)
{
    var play = duringPlay ? new CardPlay
    {
        Card = source, Player = player, Target = null, ResultPile = PileType.Discard,
        Resources = new ResourceInfo { EnergySpent = 0, EnergyValue = 0, StarsSpent = 0, StarValue = 0 },
        IsAutoPlay = false, PlayIndex = source.CurrentPlayIndex, PlayCount = 2
    } : null;
    var actual = lethality.ModifyDamageMultiplicative(null, 6m,
        MegaCrit.Sts2.Core.ValueProps.ValueProp.Move, ostyDealer, source, play);
    var native = nativeLethality.ModifyDamageMultiplicative(null, 6m,
        MegaCrit.Sts2.Core.ValueProps.ValueProp.Move, ostyDealer, source, play);
    Check(actual == expected && actual == native, scenario);
}
history.Clear();
Move(attack, state.Hand);
MatchesNative(attack, 1.5m, "first-Attack preview matches native Lethality");
Move(attack, state.PlayPile);
Started(attack);
MatchesNative(attack, 1.5m, "the first Attack retains its multiplier after its play has started", duringPlay: true);
MatchesNative(attack, 1m, "an in-play preview without a current CardPlay matches native Lethality");
Field(attack, typeof(CardModel), "_currentPlayIndex", 1);
MatchesNative(attack, 1m, "Replay cannot reuse the first-Attack multiplier", duringPlay: true);
Field(attack, typeof(CardModel), "_currentPlayIndex", 0);
var nestedAttack = Card(Definition([Op("NCR:OstyDamage", 6)]));
Move(nestedAttack, state.PlayPile);
Started(nestedAttack);
MatchesNative(nestedAttack, 1m, "a nested second Attack cannot qualify while the first is unfinished", duringPlay: true);
Move(attack, state.Hand);
MatchesNative(attack, 1m, "later hand previews no longer show the first-Attack multiplier");
history.Clear();

var nextAttackSource = Card(Definition([Op("C:grantNextAttack"), Op("M:DamagePerExhaustCard", 3, 0)],
    GeneratedCardType.Skill));
await Play(nextAttackSource);
var nextAttackPower = Hooks.Powers.Last();
nextAttackPower.IgnoreArmingCardPlay = false;
Move(unrelated, state.ExhaustPile);
var expectedBonus = state.ExhaustPile.Cards.Count * 3m;
Check(expectedBonus > 0 && nextAttackPower.ModifyDamageAdditive(null, 6m,
    MegaCrit.Sts2.Core.ValueProps.ValueProp.Move, ostyDealer, attack, null) == expectedBonus,
    "next-Attack additive modifiers also apply to an owned Osty Attack");
Check(nextAttackPower.ModifyDamageAdditive(null, 6m,
    MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered, ostyDealer, attack, null) == 0m,
    "next-Attack additive modifiers still exclude unpowered damage");

Hooks.Block.Clear();
var delayedDraw = Card(Definition([Op("C:NextTurnStart"), Op("C:untilTurnEndCardDrawn", trigger: 0),
    Op("N:B", 3, 1)], GeneratedCardType.Skill));
await Play(delayedDraw);
var pendingDraw = Hooks.Powers.Last();
await pendingDraw.AfterCardDrawn(new ThrowingPlayerChoiceContext(), unrelated, fromHandDraw: false);
Check(Hooks.Block.Count == 0, "a next-turn draw listener does not trigger on the turn it was played");
await pendingDraw.AfterSideTurnEnd(new ThrowingPlayerChoiceContext(), creature.Side, [creature]);
Check(!Hooks.Removed.Contains(pendingDraw), "the pending next-turn listener survives the source turn ending");
var installedBeforeStart = Hooks.Powers.Count;
await pendingDraw.AfterSideTurnStart(creature.Side, [creature], combat);
var drawListener = Hooks.Powers.Last();
Check(Hooks.Powers.Count == installedBeforeStart + 1 && drawListener.ActivationRootIndex == 1
      && Hooks.Removed.Contains(pendingDraw) && Hooks.Block.Count == 0,
    "next-turn start installs the draw listener without granting its Block immediately");
await drawListener.AfterCardDrawn(new ThrowingPlayerChoiceContext(), unrelated, fromHandDraw: true);
await drawListener.AfterCardDrawn(new ThrowingPlayerChoiceContext(), unrelated, fromHandDraw: false);
Check(Hooks.Block.SequenceEqual(new[] { 3m, 3m }), "both opening and later draws grant the printed 3 Block");
var listenerDescription = (IReadOnlyList<GeneratorOperation>)AccessTools.Method(typeof(ChaosCompositePower),
    "EffectiveDescriptionOperations").Invoke(drawListener, null)!;
Check(listenerDescription.Count == 2 && listenerDescription[0].Template == "C:untilTurnEndCardDrawn"
      && !listenerDescription[0].Parameters.ContainsKey("triggerIndex")
      && listenerDescription[1].Parameters["triggerIndex"] == 0,
    "the activated Power describes just its correctly linked active draw clause");
var savedListener = SavedProperties.From(drawListener)!;
var loadedListener = (ChaosCompositePower)ModelDb.Power<ChaosCompositePower>().ToMutable();
savedListener.Fill(loadedListener);
Field(loadedListener, typeof(PowerModel), "_owner", creature);
Hooks.Block.Clear();
await loadedListener.AfterCardDrawn(new ThrowingPlayerChoiceContext(), unrelated, fromHandDraw: false);
Check(loadedListener.ActivationRootIndex == 1 && Hooks.Block.SequenceEqual(new[] { 3m }),
    "saving and restoring an activated listener retains its scope and printed amount");
await drawListener.AfterSideTurnEnd(new ThrowingPlayerChoiceContext(), creature.Side, [creature]);
Hooks.Block.Clear();
await drawListener.AfterCardDrawn(new ThrowingPlayerChoiceContext(), unrelated, fromHandDraw: false);
Check(Hooks.Removed.Contains(drawListener) && Hooks.Block.Count == 0,
    "the activated draw listener expires at the end of its actual active turn");

var delayedPlay = Card(Definition([Op("C:NextTurnStart"), Op("C:untilTurnEndCardPlayed", trigger: 0),
    Op("N:B", 3, 1)], GeneratedCardType.Skill));
await Play(delayedPlay);
var pendingPlay = Hooks.Powers.Last();
await pendingPlay.AfterSideTurnEnd(new ThrowingPlayerChoiceContext(), creature.Side, [creature]);
await pendingPlay.AfterSideTurnStart(creature.Side, [creature], combat);
var playListener = Hooks.Powers.Last();
Hooks.Block.Clear();
await playListener.AfterCardPlayed(new ThrowingPlayerChoiceContext(), new CardPlay
{
    Card = unrelated, Player = player, Target = null, ResultPile = PileType.Discard,
    Resources = new ResourceInfo { EnergySpent = 1, EnergyValue = 1, StarsSpent = 0, StarValue = 0 },
    IsAutoPlay = false, PlayIndex = 0, PlayCount = 1
});
Check(Hooks.Block.SequenceEqual(new[] { 3m }),
    "a delayed card-play listener counts the first real card rather than dropping it as its arming play");

Hooks.Block.Clear();
var mixedDelay = Card(Definition([Op("C:NextTurnStart"), Op("C:untilTurnEndCardDrawn", trigger: 0),
    Op("N:B", 3, 1), Op("A:whenCardDrawnDuringTurn"), Op("N:B", 2, 3)], GeneratedCardType.Power));
await Play(mixedDelay);
var mixedParent = Hooks.Powers.Last();
await mixedParent.AfterCardDrawn(new ThrowingPlayerChoiceContext(), unrelated, fromHandDraw: false);
Check(Hooks.Block.SequenceEqual(new[] { 2m }),
    "an independent permanent clause remains active while its sibling waits for next turn");
await mixedParent.AfterSideTurnEnd(new ThrowingPlayerChoiceContext(), creature.Side, [creature]);
await mixedParent.AfterSideTurnStart(creature.Side, [creature], combat);
var mixedChild = Hooks.Powers.Last();
Hooks.Block.Clear();
await mixedParent.AfterCardDrawn(new ThrowingPlayerChoiceContext(), unrelated, fromHandDraw: false);
await mixedChild.AfterCardDrawn(new ThrowingPlayerChoiceContext(), unrelated, fromHandDraw: false);
Check(Hooks.Block.SequenceEqual(new[] { 2m, 3m }) && !Hooks.Removed.Contains(mixedParent),
    "parent and delayed child each execute their own clause once without duplicating siblings");
var childChecksum = (int[])checksum.Invoke(mixedChild, null)!;
var copiedListener = (ChaosCompositePower)mixedChild.ClonePreservingMutability();
Check(copiedListener.ActivationRootIndex == 1
      && childChecksum.SequenceEqual((int[])checksum.Invoke(copiedListener, null)!),
    "cloned delayed listeners preserve their deterministic multiplayer scope");
var installedBeforeRepeat = Hooks.Powers.Count;
await mixedParent.AfterSideTurnStart(creature.Side, [creature], combat);
Check(Hooks.Powers.Count == installedBeforeRepeat,
    "a one-shot next-turn parent cannot install its child again on a later turn");
var upgradedDelay = Card(Definition([Op("C:NextTurnStart"), Op("C:untilTurnEndCardDrawn", trigger: 0),
    Op("N:B", 3, 1)], GeneratedCardType.Skill) with
{
    Upgrade = new CardUpgradePlan(2,
        [new(CardUpgradeKind.IncreaseNumber, 2, 1, ValueSlotId: "block")], "", [], "")
});
upgradedDelay.UpgradeInternal();
await Play(upgradedDelay);
var upgradedPending = Hooks.Powers.Last();
await upgradedPending.AfterSideTurnEnd(new ThrowingPlayerChoiceContext(), creature.Side, [creature]);
await upgradedPending.AfterSideTurnStart(creature.Side, [creature], combat);
Hooks.Block.Clear();
await Hooks.Powers.Last().AfterCardDrawn(new ThrowingPlayerChoiceContext(), unrelated, fromHandDraw: false);
Check(Hooks.Block.SequenceEqual(new[] { 4m }),
    "delayed activation preserves the upgraded captured Block without losing or applying the upgrade twice");
Console.WriteLine("Self-mutation regression passed. No game or user saves were accessed.");

public class RunProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "IterateHookListeners" => Array.Empty<AbstractModel>(),
        "get_Players" => Array.Empty<Player>(),
        "Contains" => true,
        _ => method.ReturnType == typeof(void) ? null
            : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null
    };
}
static class Hooks
{
    public static List<decimal> Damage = [];
    public static List<ChaosCompositePower> Powers = [];
    public static List<decimal> Block = [];
    public static HashSet<PowerModel> Removed = [];
    public static bool Skip() => false;
    public static bool NoAnimation(ref Task __result) { __result = Task.CompletedTask; return false; }
    public static bool RemovePower(PowerModel power, ref Task __result)
    {
        Removed.Add(power);
        __result = Task.CompletedTask;
        return false;
    }
    public static bool RecordBlock(decimal amount, ref Task<decimal> __result)
    {
        Block.Add(amount);
        __result = Task.FromResult(amount);
        return false;
    }
    public static bool RecordDamage(ChaosCardModel card, int operationIndex, int amount,
        OperationRuntimeSpec spec, object state, ref Task<bool> __result)
    {
        if (spec.Opcode != "deal_damage") return true;
        var executor = typeof(ChaosCardModel).Assembly.GetType("AutoAnthony.ChaosOperationExecutor", true)!;
        amount = (int)AccessTools.Method(executor, "RuntimeSpecValue").Invoke(null, [card, operationIndex, "damage", amount])!;
        var resolved = ((decimal Damage, int Hits))AccessTools.Method(executor, "DamageAndHits")
            .Invoke(null, [card, (decimal)amount, state, 1, operationIndex])!;
        Damage.Add(resolved.Damage);
        __result = Task.FromResult(true);
        return false;
    }
    public static bool CapturePower(PowerModel power, Creature target, CardModel? cardSource, ref Task __result)
    {
        AccessTools.Field(typeof(PowerModel), "_owner").SetValue(power, target);
        Powers.Add((ChaosCompositePower)power);
        __result = power.AfterApplied(target, cardSource);
        return false;
    }
}
