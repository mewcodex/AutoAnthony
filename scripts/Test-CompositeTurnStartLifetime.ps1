$ErrorActionPreference = 'Stop'
# Run the actual four production lifecycle methods with minimal engine/event stubs (PowerShell 7).
$source = Get-Content (Join-Path $PSScriptRoot '../ChaosMode/ChaosCompositePower.cs') -Raw
$start = $source.IndexOf('    public override async Task AfterSideTurnStart(')
$end = $source.IndexOf('    public override async Task AfterCardExhausted(', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Turn-start lifecycle methods not found' }
$methods = $source.Substring($start, $end - $start)
$expiryStart = $source.IndexOf('    private void ExpireOwnerTurnEffects()')
$expiry = $source.Substring($expiryStart).TrimEnd()
# The remaining source contains only these two helpers and the containing class's closing brace.
$expiry = $expiry.Substring(0, $expiry.LastIndexOf('}'))
$harness = @'
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using GeneratorOperation = CompositeLifetimeTests.Op;
namespace CompositeLifetimeTests {
public enum CombatSide { Player, Enemy }
public class Creature { public CombatSide Side; }
public class Player { public Creature Creature; }
public interface ICombatState { }
public class PlayerChoiceContext { }
public class ThrowingPlayerChoiceContext : PlayerChoiceContext { }
public record Op(string Template,string Lifetime="combat");
public record Trigger(string Kind,string Lifetime);
public record Spec(Trigger Trigger);
public static class OperationRuntimeSpecCompiler { public static Spec RequireStructured(Op op)=>new(new(op.Template,op.Lifetime)); }
public static class ChaosOperationExecutor {public static bool RequiresCompositePower(Op op)=>true;}
public static class CardEffectRules {public static bool IsNextAttackGrantTrigger(Op op)=>op.Template is "next_attack" or "next_attacks_this_turn";}
public record CardData(Op[] Operations);
public record DefinitionData(CardData Card);
public abstract class Base {
 public virtual Task AfterSideTurnStart(CombatSide side,IReadOnlyList<Creature> participants,ICombatState state)=>Task.CompletedTask;
 public virtual Task AfterPlayerTurnStart(PlayerChoiceContext context,Player player)=>Task.CompletedTask;
 public virtual Task AfterAutoPrePlayPhaseEntered(PlayerChoiceContext context,Player player)=>Task.CompletedTask;
 public virtual Task AfterSideTurnEnd(PlayerChoiceContext context,CombatSide side,IEnumerable<Creature> participants)=>Task.CompletedTask;
 public virtual Task BeforeSideTurnEnd(PlayerChoiceContext context,CombatSide side,IEnumerable<Creature> participants)=>Task.CompletedTask;
}
public static class PowerCmd {
 public static Task Remove(Power power) { power.Removed=true; power.Removals++; return Task.CompletedTask; }
}
public class Power : Base {
 public Creature Owner=new();
 public bool Permanent,Removed,_waitForNextTurn,_statusDrawnThisTurn,_firstAttackOrSkillAvailable,_firstCardReplayAvailable,_zeroCostAttackReturnAvailable;
 public bool OwnerTurnEffectsExpired,DefensiveTurnEffectsExpired,NextAttackTriggerAvailable,NextAttackReplayAvailable,NextAttackFreeAvailable,LegacyBomb;
 public int Removals,_remainingTurnTriggers,_attacksPlayedThisTurn,_cardsPlayedTowardTrigger,_delayedTurns,NextAttackTriggersRemaining;
 public List<Op> Extra=new();
 public DefinitionData Definition=>new(new(EffectivePowerOperations()));
 public HashSet<string> Choices=new();
 public Dictionary<string,int> Fired=new();
 public Op[] EffectiveDescriptionOperations()=>[];
 public Op[] EffectivePowerOperations()=>new Op[]{new("next_turn_start"),new("next_turns_start"),new("turns_elapsed")}
  .Concat(Permanent && !LegacyBomb?new[]{new Op("turn_start")}:Array.Empty<Op>()).Concat(Extra).ToArray();
 public bool HasTriggerWithLinkedEffect(Op[] ops,string trigger,string effect)=>false;
 public string TriggerKind(Op op)=>op.Template;
 public bool StartTriggerNeedsPlayerChoice(params string[] kinds)=>kinds.Any(Choices.Contains);
 public Task FireTriggers(string kind,PlayerChoiceContext context) {
  if(Removed)throw new Exception("Trigger after removal: "+kind);
  if(Choices.Contains(kind) && context is ThrowingPlayerChoiceContext)throw new Exception("Missing player-choice context");
  Fired[kind]=Fired.GetValueOrDefault(kind)+1; return Task.CompletedTask;
 }
 public async Task FireTriggersAny(string[] kinds,PlayerChoiceContext context, bool autoPrePlay = false)
 {
  foreach(var kind in kinds)
   if(EffectivePowerOperations().Any(operation=>operation.Template==kind))
    await FireTriggers(kind,context);
 }
'@
$tests = @'
}
public static class Regression {
 public static void Run()=>Test().GetAwaiter().GetResult();
 static void Check(bool ok,string label) {if(!ok)throw new Exception(label);}
 static async Task Tick(Power p,bool playerFirst) {
  var player=new Player {Creature=p.Owner};
  if(playerFirst)await p.AfterPlayerTurnStart(new(),player);
  await p.AfterSideTurnStart(CombatSide.Player,[p.Owner],null);
  if(!playerFirst)await p.AfterPlayerTurnStart(new(),player);
 }
 static async Task Test() {
  foreach(var order in new[]{false,true}) foreach(var permanent in new[]{false,true})
  for(int choices=0;choices<8;choices++) {
   var p=new Power {Permanent=permanent,_waitForNextTurn=true,_remainingTurnTriggers=2};
   var kinds=new[]{"next_turn_start","next_turns_start","turn_start"};
   for(int i=0;i<3;i++)if((choices&(1<<i))!=0)p.Choices.Add(kinds[i]);
   await p.AfterSideTurnStart(CombatSide.Enemy,[p.Owner],null);
   await p.AfterPlayerTurnStart(new(),new Player{Creature=new Creature()});
   Check(p.Fired.Count==0,"Ignore enemies and other players");
   for(int turn=1;turn<= (permanent?4:2);turn++) {
    await Tick(p,order);
    Check(p.Fired.GetValueOrDefault("next_turn_start")==1,"One-shot repeats or missing");
    Check(p.Fired.GetValueOrDefault("next_turns_start")==Math.Min(turn,2),"Limited effect cadence");
    Check(p.Fired.GetValueOrDefault("turn_start")== (permanent?turn:0),"Permanent engine skipped");
    Check(p.Removed==(!permanent && turn==2),"Wrong composite lifetime");
   }
   Check(p.Removals==(permanent?0:1),"Remove exactly once, only for expired temporary power");
  }
  var reported=new Power{Permanent=true,_waitForNextTurn=true};
  await Tick(reported,true); await Tick(reported,true);
  Check(!reported.Removed && reported.Fired["turn_start"]==2 && reported.Fired["next_turn_start"]==1,"Reported Focus/Energy combination");
  var oneShot=new Power{_waitForNextTurn=true}; await Tick(oneShot,true);
  Check(oneShot.Removed && oneShot.Removals==1,"Pure delayed skill still expires");
  var bomb=new Power{Permanent=true,LegacyBomb=true,_delayedTurns=1};
  await bomb.BeforeSideTurnEnd(new(),CombatSide.Player,[bomb.Owner]);
  Check(bomb.Removed,"Legacy Bomb-only container must expire despite Permanent flag");
  var combo=new Power{Permanent=true,_delayedTurns=1};
  await combo.BeforeSideTurnEnd(new(),CombatSide.Player,[combo.Owner]); await Tick(combo,true);
  Check(!combo.Removed && combo.Fired["turn_start"]==1,"Bomb must not remove permanent engine");
  var waiting=new Power{_waitForNextTurn=true,NextAttackTriggerAvailable=true};
  waiting.Extra.Add(new("next_attack"));
  await Tick(waiting,true);
  Check(!waiting.Removed,"Next-turn payout must retain pending next Attack");
  waiting.NextAttackTriggerAvailable=false;
  await waiting.AfterSideTurnEnd(new(),CombatSide.Player,[waiting.Owner]);
  Check(waiting.Removed,"Exhausted next Attack must not retain dead container");
  var temporary=new Power{_waitForNextTurn=true};
  temporary.Extra.Add(new("card_played","this_turn"));
  temporary.Extra.Add(new("attack_received","this_turn"));
  await temporary.AfterSideTurnEnd(new(),CombatSide.Player,[temporary.Owner]);
  Check(!temporary.Removed && temporary.OwnerTurnEffectsExpired && !temporary.DefensiveTurnEffectsExpired,"Expire owner rider while retaining defense and delay");
  await temporary.AfterSideTurnEnd(new(),CombatSide.Enemy,[]);
  Check(!temporary.Removed && temporary.DefensiveTurnEffectsExpired,"Expire defense while retaining delay");
  await Tick(temporary,true);
  Check(temporary.Removed,"Expired riders must not retain completed delay");
  var defense=new Power(); defense.Extra.Add(new("attack_received","this_turn"));
  await defense.AfterSideTurnEnd(new(),CombatSide.Player,[defense.Owner]);
  Check(!defense.Removed,"Retaliation must survive owner turn end");
  await defense.AfterSideTurnEnd(new(),CombatSide.Enemy,[]);
  Check(defense.Removed,"Retaliation expires after the opposing turn");
  var bombDelay=new Power{Permanent=true,LegacyBomb=true,_delayedTurns=1,_waitForNextTurn=true};
  await bombDelay.BeforeSideTurnEnd(new(),CombatSide.Player,[bombDelay.Owner]);
  await bombDelay.AfterSideTurnEnd(new(),CombatSide.Player,[bombDelay.Owner]);
  Check(!bombDelay.Removed,"Bomb expiry must preserve pending delayed reward");
  await Tick(bombDelay,true);
  Check(bombDelay.Removed,"Remove exhausted legacy Bomb/delay container");
 }
}
}
'@
Add-Type -TypeDefinition ($harness + $methods + $expiry + $tests)
[CompositeLifetimeTests.Regression]::Run()
Write-Output 'Composite turn-start lifetime regressions passed: mixed/permanent/temporary effects, choice routing, both hook orders, owner guards.'
