$ErrorActionPreference = 'Stop'
# Execute the actual v111 Vigor implementation and production patch, with lightweight engine stubs.
$native = Get-Content (Join-Path $PSScriptRoot '../../proj/111/MegaCrit.Sts2.Core.Models.Powers/VigorPower.cs') -Raw
$patch = Get-Content (Join-Path $PSScriptRoot '../ChaosMode/Patches/VigorAttackLifecyclePatch.cs') -Raw
$native = $native -replace '(?m)^using [^;]+;\r?\n', '' -replace 'namespace MegaCrit.Sts2.Core.Models.Powers;', ''
$patch = $patch -replace '(?m)^using [^;]+;\r?\n', '' -replace 'namespace AutoAnthony.Patches;', ''
$harness = @'
#nullable enable
using System;
using System.Reflection;
using System.Threading.Tasks;
namespace VigorLifecycleTests {
public sealed class HarmonyPatch(Type type, string name) : Attribute { public Type Type = type; public string Name = name; }
public static class AccessTools { public static FieldInfo Field(Type? type, string name) => type!.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!; }
public enum PowerType { Buff }
public enum PowerStackType { Counter }
public enum ValueProp { Powered, Unpowered }
public static class Props { public static bool IsPoweredAttack(this ValueProp prop) => prop == ValueProp.Powered; }
public sealed class Creature { }
public sealed class CardModel { }
public sealed class CardPlay { }
public sealed class PlayerChoiceContext { }
public sealed class AttackCommand {
    public Creature? Attacker;
    public ValueProp DamageProps;
    public object? ModelSource;
}
public abstract class PowerModel {
    private object? _internalData;
    public Creature Owner = new();
    public int Amount;
    public virtual PowerType Type => default;
    public virtual PowerStackType StackType => default;
    protected virtual object? InitInternalData() => null;
    protected T GetInternalData<T>() => (T)(_internalData ??= InitInternalData())!;
    public virtual Task BeforeAttack(AttackCommand command) => Task.CompletedTask;
    public virtual Task AfterAttack(PlayerChoiceContext context, AttackCommand command) => Task.CompletedTask;
    public virtual decimal ModifyDamageAdditive(Creature? target, decimal amount, ValueProp props, Creature? dealer, CardModel? cardSource, CardPlay? cardPlay) => 0;
}
public static class PowerCmd {
    public static Task ModifyAmount(PlayerChoiceContext context, PowerModel power, int delta, object? a, object? b) { power.Amount += delta; return Task.CompletedTask; }
}
public static class Regression {
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
    static AttackCommand Attack(VigorPower power) => new() { Attacker = power.Owner, DamageProps = ValueProp.Powered, ModelSource = new CardModel() };
    static decimal Bonus(VigorPower power, AttackCommand attack) => power.ModifyDamageAdditive(null, 6, attack.DamageProps, attack.Attacker, (CardModel)attack.ModelSource!, null);
    public static void Run() => RunAsync().GetAwaiter().GetResult();
    static async Task RunAsync() {
        var context = new PlayerChoiceContext();
        // Demonstrate the native bug first: on-hit stacks keep the instance and stale attack binding alive.
        var broken = new VigorPower { Amount = 2 };
        var first = Attack(broken);
        await broken.BeforeAttack(first);
        broken.Amount += 2;
        await broken.AfterAttack(context, first);
        var second = Attack(broken);
        await broken.BeforeAttack(second);
        Check(Bonus(broken, second) == 0 && broken.Amount == 2, "Native reproduction failed");

        // Both ordinary and Glam-replayed powers: +2 / +4 on each attack, different source cards.
        foreach (int grant in new[] { 2, 4 }) {
            var power = new VigorPower { Amount = grant };
            for (int i = 0; i < 5; i++) {
                var attack = Attack(power);
                await power.BeforeAttack(attack);
                Check(Bonus(power, attack) == grant, "Next attack must use retained Vigor");
                var nested = Attack(power);
                await power.BeforeAttack(nested);
                await VigorAttackLifecyclePatch.Finish(power.AfterAttack(context, nested), power, nested);
                Check(Bonus(power, nested) == 0 && Bonus(power, attack) == grant, "Nested attack must not clear outer binding");
                power.Amount += grant;
                await VigorAttackLifecyclePatch.Finish(power.AfterAttack(context, attack), power, attack);
                Check(power.Amount == grant, "Only starting stacks should be consumed");
            }
            var unpowered = Attack(power);
            unpowered.DamageProps = ValueProp.Unpowered;
            await power.BeforeAttack(unpowered);
            await VigorAttackLifecyclePatch.Finish(power.AfterAttack(context, unpowered), power, unpowered);
            Check(power.Amount == grant && Bonus(power, unpowered) == 0, "Unpowered damage must not use Vigor");
            var final = Attack(power);
            await power.BeforeAttack(final);
            await VigorAttackLifecyclePatch.Finish(power.AfterAttack(context, final), power, final);
            Check(power.Amount == 0, "Ordinary consumption unchanged");
        }
        var delayed = new VigorPower { Amount = 2 };
        var command = Attack(delayed);
        await delayed.BeforeAttack(command);
        var gate = new TaskCompletionSource();
        var completion = VigorAttackLifecyclePatch.Finish(gate.Task, delayed, command);
        Check(!completion.IsCompleted && Bonus(delayed, Attack(delayed)) == 0, "Must await native completion");
        gate.SetResult();
        await completion;
        Check(Bonus(delayed, Attack(delayed)) == 2, "Release after native completion");
    }
}
'@
Add-Type -TypeDefinition ($harness + $native + $patch + '}')
[VigorLifecycleTests.Regression]::Run()
Write-Output 'Vigor native reproduction and patched lifecycle regressions passed (no live game UI).'
