$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '../ChaosMode/ChaosCompositePower.cs') -Raw
$start = $source.IndexOf('    private async Task FireTriggersAny(')
$end = $source.IndexOf('    private async Task FireTriggerAt(', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Trigger dispatch not found' }
$dispatch = $source.Substring($start, $end - $start)
$header = @'
#nullable enable
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace MayhemPhaseTests {
public enum OperationScope { AbilityTrigger, ConditionalTrigger, NonTargeted }
public record Op(string Template, OperationScope Scope, string? Kind, Dictionary<string,int> Parameters);
public record CardData(IReadOnlyList<Op> Operations);
public record DefinitionData(CardData Card);
public class PlayerChoiceContext {}
public class CardPlay {}
public class CardModel {}
public class Creature {}
public class Test {
 public DefinitionData Definition = new(new([]));
 public List<int> Fired = new();
 static string? TriggerKind(Op operation) => operation.Kind;
 Task FireTriggerAt(int index, PlayerChoiceContext context, CardPlay? play, CardModel? card, Creature? creature, decimal amount) {
  Fired.Add(index); return Task.CompletedTask;
 }
'@
$tests = @'
 public static async Task Run() {
  Op Trigger(string kind) => new("trigger", OperationScope.AbilityTrigger, kind, new());
  Op Effect(string template, int parent) => new(template, OperationScope.NonTargeted, null, new(){{"triggerIndex",parent}});
  var test = new Test { Definition = new(new([
   Trigger("turn_start"), Effect("gain_focus",0),
   Trigger("turn_start"), Effect("CL:PlayTopDrawCard",2), Effect("gain_block",2),
   Trigger("turn_start_if_self_in_exhaust"), Effect("CL:PlayTopDrawCard",5)
  ])) };
  var kinds = new[]{"turn_start","turn_start_if_self_in_exhaust"};
  await test.FireTriggersAny(kinds,new());
  if(!test.Fired.SequenceEqual(new[]{0})) throw new Exception("Mayhem ran early or independent Focus was delayed");
  await test.FireTriggersAny(kinds,new(),autoPrePlay:true);
  if(!test.Fired.SequenceEqual(new[]{0,2,5})) throw new Exception("Wrong deferred clauses or duplicate permanent trigger");
 }
}}
'@
Add-Type -TypeDefinition ($header + $dispatch + $tests)
[MayhemPhaseTests.Test]::Run().GetAwaiter().GetResult()
Write-Output 'Mayhem phase dispatch passed: independent start effects stay early; linked Mayhem clauses run only in AutoPrePlay.'
