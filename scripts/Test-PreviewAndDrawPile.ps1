$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '../ChaosMode/ChaosOperationExecutor.cs') -Raw
$start = $source.IndexOf('    internal static int SelectionCountForEffect(')
$end = $source.IndexOf('    /// <summary>', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Selection-count methods not found' }
$methods = $source.Substring($start, $end - $start)
$header = @'
using System;
using System.Linq;
namespace DrawPileRegression {
public record Value(string Source);
public record Spec(Value[] Values);
public record GeneratorOperation(string Template, Spec Spec);
public static class OperationRuntimeSpecCompiler {
 public static Spec RequireStructured(GeneratorOperation operation) => operation.Spec;
}
public static class Test {
 static int TransformProxySelectionCount(int amount) => Math.Max(1, amount);
'@
$tests = @'
 public static void Run() {
  void Check(string template, int amount, string source, int expected) {
   var op = new GeneratorOperation(template, new Spec(source == "" ? [] : [new Value(source)]));
   if (SelectionCountForEffect(op, amount) != expected) throw new Exception($"{template}/{amount}/{source}");
  }
  Check("R:PutSelectedHandCardsOnDraw", 0, "", 1);
  Check("R:PutSelectedHandCardsOnDraw", 3, "fixed", 3);
  foreach(var source in new[]{"energy_x", "star_x", "special_x"}) {
   Check("R:PutSelectedHandCardsOnDraw", 0, source, 0);
   Check("R:PutSelectedHandCardsOnDraw", 2, source, 2);
  }
  Check("R:PutSelectedHandCardOnDraw", 0, "", 1);
 }
}}
'@
Add-Type -TypeDefinition ($header + $methods + $tests)
[DrawPileRegression.Test]::Run()
if ($source -notmatch 'public int\? PriorAttackHitsOnTargetAtPlayStart') {
 throw 'Uncaptured preview history must be null, not an explicit zero snapshot'
}
if ($source -notmatch '"R:ForEachPriorAttackHitOnTarget" => priorAttackHitsOverride\s*\?\? CountPriorAttackHits') {
 throw 'Live history fallback is missing'
}
$route = $source.Substring($source.IndexOf('case "CL:PlayTopDrawCard":'))
$route = $route.Substring(0, $route.IndexOf('case "CL:PutEventCardOnDrawTop":'))
if ($route -notmatch 'CardPileCmd.AutoPlayFromDrawPile' -or $route -notmatch 'forceExhaust: false') {
 throw 'Mayhem must use the native shuffle-aware path without forced Exhaust'
}
Write-Output 'Selection-count regressions and preview/Mayhem routing checks passed (no live game UI).'
