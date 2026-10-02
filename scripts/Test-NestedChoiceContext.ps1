$ErrorActionPreference = 'Stop'
# Compile the production async scope without loading Godot. Mock only the native context identity contract.
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../ChaosMode/ChaosChoiceContext.cs') -Raw
$source = $source.Replace('namespace AutoAnthony;', '').Replace('using MegaCrit.Sts2.Core.GameActions.Multiplayer;', '')
$tests = @'
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
namespace NestedChoiceTests {
public abstract class PlayerChoiceContext { public abstract ulong? OwnerId { get; } }
public sealed class ThrowingPlayerChoiceContext : PlayerChoiceContext { public override ulong? OwnerId => null; }
public sealed class TestChoiceContext(ulong? id) : PlayerChoiceContext { public override ulong? OwnerId => id; }
public static class NestedChoiceRegression
{
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
    public static void Run() => RunAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    static async Task RunAsync()
    {
        var missing = new ThrowingPlayerChoiceContext();
        var owner = new TestChoiceContext(1);
        var other = new TestChoiceContext(2);
        Check(ReferenceEquals(ChaosChoiceContext.Resolve(missing, 1), missing), "No ambient context");
        using (ChaosChoiceContext.Enter(owner))
        {
            await Task.Yield();
            Check(ReferenceEquals(ChaosChoiceContext.Resolve(missing, 1), owner), "Across await");
            Check(ReferenceEquals(ChaosChoiceContext.Resolve(missing, 2), missing), "Cross-player isolation");
            Check(ReferenceEquals(ChaosChoiceContext.Resolve(other, 2), other), "Explicit context takes priority");
            using (ChaosChoiceContext.Enter(missing))
                Check(ReferenceEquals(ChaosChoiceContext.Resolve(missing, 1), owner), "Indirect missing hook");
            using (ChaosChoiceContext.Enter(other))
                Check(ReferenceEquals(ChaosChoiceContext.Resolve(missing, 1), missing), "Do not skip another owner");
            using (ChaosChoiceContext.Enter(new TestChoiceContext(null)))
                Check(ChaosChoiceContext.Resolve(missing, 2) is TestChoiceContext { OwnerId: null }, "Native blocking context");
        }
        Check(ReferenceEquals(ChaosChoiceContext.Resolve(missing, 1), missing), "Disposed context");

        // Emulate Harmony prefix -> async native method -> postfix. The caller must be restored immediately,
        // but the suspended method must retain its own frame until its Task completes.
        var gate = new TaskCompletionSource();
        var scope = ChaosChoiceContext.Enter(owner);
        async Task SuspendedPlay()
        {
            await gate.Task;
            Check(ReferenceEquals(ChaosChoiceContext.Resolve(missing, 1), owner), "Native async captured context");
        }
        var task = SuspendedPlay();
        scope.RestoreCaller();
        var completion = ChaosChoiceContext.Complete(task, scope);
        Check(ReferenceEquals(ChaosChoiceContext.Resolve(missing, 1), missing), "Postfix restores caller");
        gate.SetResult();
        await completion;
        Check(!scope.Active, "Task completion closes scope");

        var staleGate = new TaskCompletionSource();
        Task stale;
        using (ChaosChoiceContext.Enter(owner))
        {
            var child = ChaosChoiceContext.Enter(owner);
            async Task Detached()
            {
                await staleGate.Task;
                Check(ReferenceEquals(ChaosChoiceContext.Resolve(missing, 1), missing), "Closed child is a lifetime barrier");
            }
            stale = Detached();
            child.Dispose();
            staleGate.SetResult();
            await stale;
        }
        async Task ParallelOwner(ulong id)
        {
            var context = new TestChoiceContext(id);
            using (ChaosChoiceContext.Enter(context))
            {
                await Task.Yield();
                Check(ReferenceEquals(ChaosChoiceContext.Resolve(missing, id), context), "Concurrent owner context");
                Check(ReferenceEquals(ChaosChoiceContext.Resolve(missing, 3-id), missing), "Concurrent owner isolation");
            }
        }
        await Task.WhenAll(ParallelOwner(1), ParallelOwner(2));
        var failed = ChaosChoiceContext.Enter(owner);
        failed.RestoreCaller();
        try { await ChaosChoiceContext.Complete(Task.FromException(new InvalidOperationException("expected")), failed); }
        catch (InvalidOperationException) { }
        Check(!failed.Active, "Exception closes scope");
    }
}
'@
Add-Type -TypeDefinition ($tests + $source + '}')
[NestedChoiceTests.NestedChoiceRegression]::Run()
Write-Output 'Nested choice context lifecycle regressions passed (no live game UI).'
