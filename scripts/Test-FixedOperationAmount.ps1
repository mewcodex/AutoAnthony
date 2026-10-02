$ErrorActionPreference = 'Stop'
# Exercise the production amount reader; engine state is stubbed, not the method under test.
$source = Get-Content (Join-Path $PSScriptRoot '../ChaosMode/ChaosCardModel.cs') -Raw
$start = $source.IndexOf('    internal int OperationAmount(int operationIndex)')
$end = $source.IndexOf('    internal static class ChaosXValueMultiplier', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'OperationAmount method not found.' }
$method = $source.Substring($start, $end - $start)
$harness = @'
#nullable enable
using System;
using System.Linq;
using System.Collections.Generic;
namespace FixedAmountTests {
public record Slot(string Id, int BaseValue, string Source = "fixed", int Offset = 0, bool Upgradable = true, bool Explicit = true);
public record Spec(Slot[] Values);
public record Operation(Spec Spec);
public record Definition(Operation[] Operations);
public record Variable(int IntValue);
public static class ChaosOperationVariables { public static string Name(Operation op, int index) => $"Amount{index}"; }
public static class OperationRuntimeSpecCompiler {
    public static string? UpgradeValueSlot(Operation op) => op.Spec.Values.FirstOrDefault(v => v.Upgradable)?.Id;
}
public static class ChaosOperationExecutor {
    public static Spec EffectiveRuntimeSpec(Card card, int index) => card.Generated.Operations[index].Spec;
}
public class Card {
    public Definition Generated = new(Array.Empty<Operation>());
    public Dictionary<string, Variable> DynamicVars = new();
    public int ResolveEffectSpecialXValue() => 2;
    public int ResolveEffectEnergyXValue() => 3;
    public int ResolveEffectStarXValue() => 4;
'@
$tests = @'
}
public static class Regression {
    public static void Run() {
        Card Make(params Slot[] values) => new() { Generated = new([new(new(values))]) };
        void Check(Card card, int expected, string label) {
            if (card.OperationAmount(0) != expected) throw new Exception(label);
        }
        Check(Make(new Slot("amount", 99, Upgradable:false)), 99, "Non-upgradable status must apply 99");
        Check(Make(new Slot("amount", 2)), 2, "Ordinary status unchanged");
        Check(Make(), 0, "No numeric slot remains zero");
        Check(Make(new Slot("metadata", 7, Upgradable:false, Explicit:false)), 0, "Implicit metadata is not an amount");
        Check(Make(new Slot("count", 0, "star_x", 1, false)), 5, "Non-upgradable X+1");
        Check(Make(new Slot("count", 0, "energy_x", 0, false)), 3, "Non-upgradable Energy X");
        Check(Make(new Slot("count", 0, "special_x", 0, false)), 2, "Non-upgradable special X");
        Check(Make(new("threshold", 3, Upgradable:false), new("amount", 6)), 6, "Existing primary slot priority");
        var live = Make(new Slot("amount", 2));
        live.DynamicVars["Amount0"] = new(5);
        Check(live, 5, "Live upgraded/enchantment value priority");
        var combo = new Card { Generated = new([
            new(new([new("amount", 99, Upgradable:false)])),
            new(new([new("damage", 3)]))]) };
        if (combo.OperationAmount(0) != 99 || combo.OperationAmount(1) != 3)
            throw new Exception("Status followed by random damage must keep independent amounts");
    }
}
}
'@
Add-Type -TypeDefinition ($harness + $method + $tests)
[FixedAmountTests.Regression]::Run()
Write-Output 'Fixed/non-upgradable operation amount regressions passed (no live game UI).'
