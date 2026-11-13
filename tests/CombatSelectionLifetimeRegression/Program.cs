using System.Reflection;
using System.Runtime.CompilerServices;
using AutoAnthony;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Runs;

static void Field(object value, Type owner, string name, object? contents) =>
    AccessTools.Field(owner, name).SetValue(value, contents);
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}
var h = new Harmony("autoanthony.test-combat-selection-lifetime");
h.Patch(AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.IsOverOrEnding)),
    prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.CombatEnded)));
h.Patch(AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.CurrentCombatId)),
    prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.CurrentCombat)));
foreach (var name in new[] { "FromHand", "FromHandForDiscard" })
    h.Patch(AccessTools.Method(typeof(CardSelectCmd), name),
        prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.Select)));
var executor = typeof(ChaosCardModel).Assembly.GetType("AutoAnthony.ChaosOperationExecutor", true)!;
var run = DispatchProxy.Create<IRunState, RunProxy>();
var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
Field(player, typeof(Player), "_runState", run);
var combat = new CombatState(runState: run);
var creature = new Creature(player, 70, 70) { CombatState = combat };
Field(player, typeof(Player), "<Creature>k__BackingField", creature);
var state = (PlayerCombatState)RuntimeHelpers.GetUninitializedObject(typeof(PlayerCombatState));
var hand = new CardPile(PileType.Hand);
Field(state, typeof(PlayerCombatState), "<Hand>k__BackingField", hand);
Field(state, typeof(PlayerCombatState), "_player", player);
Field(player, typeof(Player), "<PlayerCombatState>k__BackingField", state);
CardModel Card()
{
    var card = (CardModel)RuntimeHelpers.GetUninitializedObject(typeof(DefendDefect));
    Field(card, typeof(AbstractModel), "<IsMutable>k__BackingField", true);
    card.Owner = player;
    ((List<CardModel>)AccessTools.Field(typeof(CardPile), "_cards").GetValue(hand)!).Add(card);
    return card;
}
var original = Card();
var prefs = new CardSelectorPrefs(CardSelectorPrefs.ExhaustSelectionPrompt, 1);
foreach (var name in new[] { "SelectFromHandIfAny", "SelectFromHandForDiscardIfAny" })
{
    Task<IEnumerable<CardModel>> Select()
    {
        var args = new List<object?> { new BlockingPlayerChoiceContext(), player, prefs, null, original };
        if (name == "SelectFromHandIfAny") args.Add(false);
        return (Task<IEnumerable<CardModel>>)AccessTools.Method(executor, name).Invoke(null, args.ToArray())!;
    }
    foreach (var reason in new[] { "death", "ending", "new owner combat", "new manager combat" })
    {
        Hooks.Ended = false;
        Hooks.Id = new(1);
        creature.CombatState = combat;
        Field(creature, typeof(Creature), "_currentHp", 70);
        Hooks.Answer = new();
        var pending = Select();
        Check(!pending.IsCompleted, name + " suspends on a real asynchronous choice");
        switch (reason)
        {
            case "death": Field(creature, typeof(Creature), "_currentHp", 0); break;
            case "ending": Hooks.Ended = true; break;
            case "new owner combat": creature.CombatState = new CombatState(runState: run); break;
            case "new manager combat": Hooks.Id = new(2); break;
        }
        Hooks.Answer.SetResult([]);
        Check(!(await pending).Any(), name + " does not force a card after " + reason);
    }
    Hooks.Ended = false;
    Hooks.Id = new(1);
    creature.CombatState = combat;
    Field(creature, typeof(Creature), "_currentHp", 70);
    Hooks.Answer = new();
    var valid = Select();
    var drawnWhilePaused = Card();
    Hooks.Answer.SetResult([drawnWhilePaused]);
    Check((await valid).Single() == drawnWhilePaused, name + " preserves live choices drawn while paused");
    Hooks.Answer = new();
    var count = Hooks.Choices;
    Hooks.Ended = true;
    Check(!(await Select()).Any() && Hooks.Choices == count,
        name + " does not start a new choice after combat has ended");
}
Console.WriteLine("Combat selection lifetime regression passed. No game or user saves were accessed.");

public class RunProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) =>
        method!.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
}
static class Hooks
{
    public static bool Ended;
    public static CombatId? Id = new(1);
    public static int Choices;
    public static TaskCompletionSource<IEnumerable<CardModel>> Answer = new();
    public static bool CombatEnded(ref bool __result) { __result = Ended; return false; }
    public static bool CurrentCombat(ref CombatId? __result) { __result = Id; return false; }
    public static bool Select(ref Task<IEnumerable<CardModel>> __result)
    {
        Choices++;
        __result = Answer.Task;
        return false;
    }
}
