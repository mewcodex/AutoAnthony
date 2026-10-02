using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace AutoAnthony;

/// <summary>
/// Keeps the real choice transaction inside its async execution chain. Context-less native hooks may inherit it,
/// but never from another player, a concurrent task, or a task whose owning card play has already finished.
/// </summary>
internal static class ChaosChoiceContext
{
    private static readonly AsyncLocal<Scope?> Current = new();

    internal sealed class Scope(PlayerChoiceContext context, Scope? parent) : IDisposable
    {
        internal PlayerChoiceContext Context { get; } = context;
        internal Scope? Parent { get; } = parent;
        internal bool Active { get; private set; } = true;

        // Harmony's postfix runs as soon as the async method returns its Task, not when that Task finishes.
        internal void RestoreCaller() => Current.Value = Parent;
        internal void Close() => Active = false;
        public void Dispose()
        {
            Close();
            RestoreCaller();
        }
    }

    internal static Scope Enter(PlayerChoiceContext context)
    {
        var scope = new Scope(context, Current.Value);
        Current.Value = scope;
        return scope;
    }

    internal static PlayerChoiceContext Resolve(PlayerChoiceContext context, ulong ownerId)
    {
        if (context is not ThrowingPlayerChoiceContext) return context;
        for (var scope = Current.Value; scope is not null; scope = scope.Parent)
        {
            // A closed scope is a lifetime barrier: a detached continuation must not fall back to its grandparent.
            if (!scope.Active) break;
            if (scope.Context is ThrowingPlayerChoiceContext) continue;
            return scope.Context.OwnerId is null || scope.Context.OwnerId == ownerId ? scope.Context : context;
        }
        return context;
    }

    internal static async Task Complete(Task task, Scope scope)
    {
        try { await task; }
        finally { scope.Close(); }
    }
}
