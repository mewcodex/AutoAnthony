using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AutoAnthony;
using ChaosCardGenerator;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.TestSupport;

static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}
static void Field(object value, Type owner, string name, object? contents) =>
    AccessTools.Field(owner, name).SetValue(value, contents);

var aa = typeof(ChaosCardModel).Assembly;
var h = new Harmony("autoanthony.tests.tea-discourtesy");
// Engine names, timers and scene rendering are unavailable outside Godot. The native relic, card creation,
// pile insertion, hooks, generated-card responses, save properties and initial draw execute unchanged.
h.Patch(typeof(RelicModel).TypeInitializer!, prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.Skip)));
// v113's draw command also plays a temporary debug-audio cue. Audio/scene singletons require Godot even in
// TestMode, so suppress only that presentation boundary in this command-line fixture.
h.Patch(typeof(MegaCrit.Sts2.Core.Audio.Debug.NDebugAudioManager).TypeInitializer!,
    prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.Skip)));
h.Patch(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Audio.Debug.NDebugAudioManager), "Instance"),
    prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.NoDebugAudio)));
h.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Logging.Logger), "GetIsRunningFromGodotEditor"),
    prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.False)));
h.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Logging.ConsoleLogPrinter), "Print"),
    prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.Skip)));
var settings = aa.GetType("AutoAnthony.ChaosModSettings", true)!;
h.Patch(settings.TypeInitializer!, prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.Skip)));
AccessTools.Field(settings, "_loaded").SetValue(null, true);
h.Patch(AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.IsInProgress)),
    prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.True)));
h.Patch(AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.IsEnding)),
    prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.False)));
h.Patch(AccessTools.Method(typeof(Cmd), nameof(Cmd.Wait), [typeof(float), typeof(CancellationToken), typeof(bool)]),
    prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.NoWait)));
TestMode.IsOn = true;
var saveManager = (SaveManager)RuntimeHelpers.GetUninitializedObject(typeof(SaveManager));
var progressManager = (ProgressSaveManager)RuntimeHelpers.GetUninitializedObject(typeof(ProgressSaveManager));
progressManager.Progress = new ProgressState();
Field(saveManager, typeof(SaveManager), "_progressSaveManager", progressManager);
AccessTools.Field(typeof(SaveManager), "_mockInstance").SetValue(null, saveManager);
ModelDb.Inject(typeof(Dazed));
ModelDb.Inject(typeof(TeaOfDiscourtesy));
var companionTypes = new[] { typeof(Regalite), typeof(BlessedAntler), typeof(NinjaScroll), typeof(FuneraryMask),
    typeof(RadiantPearl), typeof(JeweledMask), typeof(BoneTea) };
foreach (var type in companionTypes.Concat(new[] { typeof(Shiv), typeof(Soul), typeof(Luminesce), typeof(Defragment) }))
    ModelDb.Inject(type);
AccessTools.Field(typeof(ModelIdSerializationCache), "_initialized").SetValue(null, true);
ModelIdSerializationCache.CacheSavedPropertiesForTypeDebug(typeof(TeaOfDiscourtesy));
ModelIdSerializationCache.CacheSavedPropertiesForTypeDebug(typeof(ChaosDefectCard000));
var scenarios = 0;

foreach (var withAa in new[] { false, true })
{
    if (withAa)
    {
        foreach (var patch in new[] { "SurpriseModeTrackSeenCardsPatch", "DustyTomeBoundCardSavePatch",
                     "DustyTomeBoundCardLoadPatch", "LegacyArchaicToothSerializationRepairPatch" })
            new PatchClassProcessor(h, aa.GetType("AutoAnthony.Patches." + patch, true)!).Patch();
        AccessTools.Field(typeof(ChaosRunDefinitions), "_runActive").SetValue(null, true);
    }
    foreach (var local in new[] { false, true })
    foreach (var companionType in new Type?[] { null }.Concat(companionTypes))
    foreach (var teaFirst in new[] { false, true })
    foreach (var extraRegalite in new[] { false, true })
    {
        if (extraRegalite && (companionType is null || companionType == typeof(Regalite))) continue;
        scenarios++;
        var label = $"AA={withAa}, local={local}, companion={companionType?.Name ?? "none"}, teaFirst={teaFirst}, extraRegalite={extraRegalite}";
        var (run, proxy, player, combat, state) = CreateFixture(local);
        var creature = player.Creature;
        var relic = (TeaOfDiscourtesy)ModelDb.Relic<TeaOfDiscourtesy>().ToMutable();
        relic.Owner = player;
        proxy.Relics.Add(relic);
        RelicModel? companion = null;
        if (companionType is not null)
        {
            companion = ModelDb.GetById<RelicModel>(ModelDb.GetId(companionType)).ToMutable();
            companion.Owner = player;
            proxy.Relics.Insert(teaFirst ? 1 : 0, companion);
            ModelIdSerializationCache.CacheSavedPropertiesForTypeDebug(companionType);
        }
        if (extraRegalite)
        {
            var mineral = (Regalite)ModelDb.Relic<Regalite>().ToMutable();
            mineral.Owner = player;
            proxy.Relics.Add(mineral);
        }
        if (withAa)
        {
            var atom = CharacterComponentCatalogs.Get(GeneratedCharacter.Defect).Atoms
                .First(atom => atom.Template == "D:CostDownWhenStatusGenerated");
            var operation = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText, new Dictionary<string, int>(),
                RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom), LocalizedText: atom.LocalizedText);
            var definition = new GeneratedCard(3, GeneratedCardType.Attack, TargetMode.SingleEnemy,
                GeneratedRarity.Common, "", [], [operation], Character: GeneratedCharacter.Defect);
            var card = (ChaosCardModel)RuntimeHelpers.GetUninitializedObject(typeof(ChaosDefectCard000));
            Field(card, typeof(AbstractModel), "<Id>k__BackingField", ModelDb.GetId<ChaosDefectCard000>());
            Field(card, typeof(AbstractModel), "<IsMutable>k__BackingField", true);
            Field(card, typeof(CardModel), "_canonicalInstance", card);
            Field(card, typeof(ChaosCardModel), "_freeformDefinition", definition);
            combat.AddCard(card, player);
            state.DrawPile.AddInternal(card, silent: true);
        }
        for (var i = 0; i < 8; i++)
            state.DrawPile.AddInternal(combat.CreateCard<Dazed>(player), silent: true);
        if (companion is JeweledMask || companion is BoneTea)
            state.DrawPile.AddInternal(combat.CreateCard<Defragment>(player), 0, silent: true);
        var before = state.DrawPile.Cards.Count;
        var unusedSave = relic.ToSerializable();
        var savedJson = JsonSerializer.Serialize(unusedSave, JsonSerializationUtility.GetTypeInfo<SerializableRelic>());
        var existingCards = state.DrawPile.Cards.ToArray();
        var canonicalDazed = ModelDb.Card<Dazed>();
        var canonicalKeywords = canonicalDazed.Keywords.ToHashSet();
        var resolver = aa.GetType("AutoAnthony.ChaosDerivativeResolver", true)!;
        var statusOperation = new GeneratorOperation("D:CreateDazedInDiscard", OperationScope.NonTargeted,
            "", new Dictionary<string, int>(), DerivativeId: "dazed");
        var previews = Enumerable.Range(0, 2).Select(_ => ((IEnumerable<IHoverTip>)
                AccessTools.Method(resolver, "HoverTips").Invoke(null, [statusOperation, false])!)
            .OfType<CardHoverTip>().First().Card).ToArray();
        previews[0].RemoveKeyword(CardKeyword.Ethereal);
        Check(!ReferenceEquals(previews[0], previews[1]) && !ReferenceEquals(previews[0], canonicalDazed)
              && previews[1].Keywords.Contains(CardKeyword.Ethereal)
              && canonicalDazed.Keywords.ToHashSet().SetEquals(canonicalKeywords),
            $"{label}: AA Dazed previews have independent keyword sets and leave the canonical card unchanged");
        var resolvedDazed = (CardModel)AccessTools.Method(resolver, "Create")
            .Invoke(null, [combat, player, statusOperation, false])!;
        Check(resolvedDazed is Dazed && resolvedDazed.IsMutable && resolvedDazed.Owner == player
              && !ReferenceEquals(resolvedDazed, canonicalDazed),
            $"{label}: AA's status slot creates a native Dazed instance, not a replacement prototype");
        combat.RemoveCard(resolvedDazed);
        Check(!RelicModel.FromSerializable(unusedSave).IsUsedUp, $"{label}: unused Tea save/load preserves its pending use");
        await Hook.BeforeCombatStart(run, combat).WaitAsync(TimeSpan.FromSeconds(5));
        Check(state.DrawPile.Cards.Count == before + 2 && relic.IsUsedUp,
            $"{label}: native Tea inserts exactly two Dazed and completes before draw");
        if (withAa)
            Check(state.DrawPile.Cards.OfType<ChaosCardModel>().Single()
                    .EnergyCost.GetWithModifiers(CostModifiers.All) == 1,
                $"{label}: Tea's two statuses reduce the AA listener's cost twice without altering Dazed");
        var teaCards = state.DrawPile.Cards.Where(card => !existingCards.Contains(card)).ToArray();
        Check(teaCards.Length == 2 && teaCards.All(card => card is Dazed && card.Owner == player
                  && card.DeckVersion is null && !ReferenceEquals(card, canonicalDazed))
              && !ReferenceEquals(teaCards[0], teaCards[1])
              && !ReferenceEquals(teaCards[0].EnergyCost, teaCards[1].EnergyCost)
              && !ReferenceEquals(teaCards[0].DynamicVars, teaCards[1].DynamicVars)
              && player.Deck.Cards.Count == 0,
            $"{label}: Tea owns two separate combat-only Dazed, without modifying the permanent deck");
        var isolatedClone = teaCards[0].CreateClone();
        isolatedClone.RemoveKeyword(CardKeyword.Ethereal);
        Check(teaCards.All(card => card.Keywords.Contains(CardKeyword.Ethereal))
              && ReferenceEquals(ModelDb.Card<Dazed>(), canonicalDazed)
              && !canonicalDazed.IsMutable
              && canonicalDazed.Keywords.ToHashSet().SetEquals(canonicalKeywords),
            $"{label}: cloning or editing a Dazed cannot mutate Tea's other cards or ModelDb");
        Check(savedJson == JsonSerializer.Serialize(unusedSave, JsonSerializationUtility.GetTypeInfo<SerializableRelic>())
              && !RelicModel.FromSerializable(JsonSerializer.Deserialize(savedJson,
                  JsonSerializationUtility.GetTypeInfo<SerializableRelic>())!).IsUsedUp,
            $"{label}: a captured combat-entry Tea save remains unused after the live relic is consumed");
        if (companion is Regalite || extraRegalite)
            Check(creature.Block == 4, $"{label}: Tea genuinely triggers Regalite before opening draw");
        var save = relic.ToSerializable();
        var restored = (TeaOfDiscourtesy)RelicModel.FromSerializable(save);
        restored.Owner = player;
        Check(restored.IsUsedUp, $"{label}: used relic save/load succeeds");
        await restored.BeforeCombatStart().WaitAsync(TimeSpan.FromSeconds(5));
        Check(state.DrawPile.Cards.Count == before + 2,
            $"{label}: used relic does not insert more cards");
        var context = new ThrowingPlayerChoiceContext();
        await Hook.BeforeSideTurnStart(combat, CombatSide.Player, [creature]).WaitAsync(TimeSpan.FromSeconds(5));
        await Hook.BeforeHandDraw(combat, player, context).WaitAsync(TimeSpan.FromSeconds(5));
        if (extraRegalite && companion is BlessedAntler or NinjaScroll or FuneraryMask or RadiantPearl)
            Check(creature.Block == 8, $"{label}: Regalite resolves both combat-start and first-turn generation");
        var handBeforeDraw = state.Hand.Cards.Count;
        var expectedHand = companion switch { NinjaScroll => 3, RadiantPearl => 1, JeweledMask => 1, _ => 0 };
        Check(handBeforeDraw == expectedHand, $"{label}: companion's real pre-draw hook completes");
        var drawAdded = companion switch { BlessedAntler => 3, FuneraryMask => 3, _ => 0 };
        Check(state.DrawPile.Cards.Count == before + 2 + drawAdded - handBeforeDraw + (companion is NinjaScroll or RadiantPearl ? handBeforeDraw : 0),
            $"{label}: opening derivative/status counts are correct");
        await CardPileCmd.Draw(context, 5, player, fromHandDraw: true)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(state.Hand.Cards.Count == 5 + handBeforeDraw, $"{label}: initial five-card draw completes");
        await Hook.AfterPlayerTurnStart(combat, context, player).WaitAsync(TimeSpan.FromSeconds(5));
        if (companion is BoneTea bone)
            Check(bone.IsUsedUp && state.Hand.Cards.Where(card => card.MaxUpgradeLevel > 0).All(card => card.IsUpgraded),
                $"{label}: Bone Tea upgrades real opening hand without stalling");
    }
}
// Capture the actual native deck/relic/RNG save records before combat setup, then restore them into fresh
// scopes. This exercises the data boundary used by entry autosaves, not RunManager's scene or disk-save UI.
ModelIdSerializationCache.CacheSavedPropertiesForTypeDebug(typeof(Dazed));
ModelIdSerializationCache.CacheSavedPropertiesForTypeDebug(typeof(Defragment));
var entry = CreateFixture(local: true);
for (var i = 0; i < 10; i++)
{
    var card = ModelDb.Card<Defragment>().ToMutable();
    card.Owner = entry.Player;
    if (i % 2 == 1) CardCmd.Upgrade(card);
    entry.Player.Deck.AddInternal(card, silent: true);
}
var entryTea = (TeaOfDiscourtesy)ModelDb.Relic<TeaOfDiscourtesy>().ToMutable();
entryTea.Owner = entry.Player;
var deckJson = entry.Player.Deck.Cards.Select(card => JsonSerializer.Serialize(card.ToSerializable(),
    JsonSerializationUtility.GetTypeInfo<SerializableCard>())).ToArray();
var teaJson = JsonSerializer.Serialize(entryTea.ToSerializable(), JsonSerializationUtility.GetTypeInfo<SerializableRelic>());
var rngJson = JsonSerializer.Serialize(entry.Run.Rng.ToSerializable(), JsonSerializationUtility.GetTypeInfo<SerializableRunRngSet>());
string? expectedOrder = null;
var previousCombatCards = new List<CardModel>();
for (var reload = 0; reload < 4; reload++)
{
    var replay = CreateFixture(local: true);
    replay.Run.Rng.LoadFromSerializable(JsonSerializer.Deserialize(rngJson,
        JsonSerializationUtility.GetTypeInfo<SerializableRunRngSet>())!);
    foreach (var json in deckJson)
    {
        var card = CardModel.FromSerializable(JsonSerializer.Deserialize(json,
            JsonSerializationUtility.GetTypeInfo<SerializableCard>())!);
        card.Owner = replay.Player;
        replay.Player.Deck.AddInternal(card, silent: true);
    }
    var tea = (TeaOfDiscourtesy)RelicModel.FromSerializable(JsonSerializer.Deserialize(teaJson,
        JsonSerializationUtility.GetTypeInfo<SerializableRelic>())!);
    tea.Owner = replay.Player;
    replay.Proxy.Relics.Add(tea);
    var mineral = (Regalite)ModelDb.Relic<Regalite>().ToMutable();
    mineral.Owner = replay.Player;
    replay.Proxy.Relics.Add(mineral);
    replay.Player.PopulateCombatState(replay.Run.Rng.Shuffle, replay.Combat);
    await Hook.BeforeCombatStart(replay.Run, replay.Combat).WaitAsync(TimeSpan.FromSeconds(5));
    var cards = replay.State.DrawPile.Cards.ToArray();
    var order = string.Join("|", cards.Select(card => $"{card.Id}:{card.CurrentUpgradeLevel}"));
    expectedOrder ??= order;
    Check(order == expectedOrder && cards.Length == 12 && cards.Count(card => card is Dazed) == 2
          && replay.Player.Deck.Cards.Count == 10 && !replay.Player.Deck.Cards.Any(card => card is Dazed)
          && tea.IsUsedUp && replay.Player.Creature.Block == 4
          && !cards.Intersect(previousCombatCards).Any()
          && cards.Where(card => card is Defragment).All(card => card.DeckVersion is not null
              && !ReferenceEquals(card, card.DeckVersion)),
        $"entry restore {reload}: same native JSON deck/relic/RNG reproduces two fresh Dazed and identical shuffle without accumulation");
    previousCombatCards.AddRange(cards);
    await CardPileCmd.Draw(new ThrowingPlayerChoiceContext(), 5, replay.Player, fromHandDraw: true)
        .WaitAsync(TimeSpan.FromSeconds(5));
    Check(replay.State.Hand.Cards.Count == 5, $"entry restore {reload}: initial draw succeeds after restore");
}
Console.WriteLine($"Tea of Discourtesy isolated regression complete: {scenarios} relic scenarios and 4 entry restores. No game, user preferences or saves were modified.");

static (IRunState Run, RunProxy Proxy, Player Player, CombatState Combat, PlayerCombatState State) CreateFixture(bool local)
{
    var run = DispatchProxy.Create<IRunState, RunProxy>();
    var proxy = (RunProxy)(object)run;
    var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
    Field(player, typeof(Player), "<NetId>k__BackingField", 1UL);
    Field(player, typeof(Player), "<Character>k__BackingField", RuntimeHelpers.GetUninitializedObject(typeof(Defect)));
    Field(player, typeof(Player), "_runState", run);
    Field(player, typeof(Player), "_relics", proxy.Relics);
    Field(player, typeof(Player), "_potionSlots", new List<PotionModel?>());
    player.DiscoveredCards = [];
    Field(player, typeof(Player), "<Deck>k__BackingField", new CardPile(PileType.Deck));
    Field(player, typeof(Player), "<IsActiveForHooks>k__BackingField", true);
    var combat = new CombatState(runState: run);
    var creature = new Creature(player, 70, 70) { CombatState = combat };
    Field(player, typeof(Player), "<Creature>k__BackingField", creature);
    ((List<Creature>)AccessTools.Field(typeof(CombatState), "_allies").GetValue(combat)!).Add(creature);
    var state = (PlayerCombatState)RuntimeHelpers.GetUninitializedObject(typeof(PlayerCombatState));
    foreach (var (name, pile) in new[] { ("Hand", PileType.Hand), ("DrawPile", PileType.Draw),
                 ("DiscardPile", PileType.Discard), ("ExhaustPile", PileType.Exhaust), ("PlayPile", PileType.Play) })
        Field(state, typeof(PlayerCombatState), $"<{name}>k__BackingField", new CardPile(pile));
    Field(state, typeof(PlayerCombatState), "_player", player);
    AccessTools.PropertySetter(typeof(PlayerCombatState), nameof(PlayerCombatState.TurnNumber)).Invoke(state, [1]);
    Field(state, typeof(PlayerCombatState), "<OrbQueue>k__BackingField", new OrbQueue(player));
    Field(player, typeof(Player), "<PlayerCombatState>k__BackingField", state);
    LocalContext.NetId = local ? player.NetId : 2UL;
    proxy.Player = player;
    proxy.Combat = combat;
    return (run, proxy, player, combat, state);
}

public class RunProxy : DispatchProxy
{
    public Player Player = null!;
    public CombatState Combat = null!;
    public List<RelicModel> Relics = [];
    private readonly RunRngSet _rng = new("aa-tea-discourtesy-regression");
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "get_Rng" => _rng,
        "get_Players" => new[] { Player },
        "get_DebugOnlyGetState" => null,
        "IterateHookListeners" => Player.Deck.Cards.Cast<AbstractModel>().Concat(Relics)
            .Concat(Player.PlayerCombatState!.AllCards).ToArray(),
        "Contains" => true,
        "GetPlayer" => Player,
        _ => method.ReturnType == typeof(void) ? null
            : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null
    };
}
static class Hooks
{
    public static bool NoDebugAudio(ref MegaCrit.Sts2.Core.Audio.Debug.NDebugAudioManager? __result)
    { __result = null; return false; }
    public static bool Skip() => false;
    public static bool True(ref bool __result) { __result = true; return false; }
    public static bool False(ref bool __result) { __result = false; return false; }
    public static bool NoWait(ref Task __result) { __result = Task.CompletedTask; return false; }
}
