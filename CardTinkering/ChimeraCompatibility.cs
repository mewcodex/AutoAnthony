using System.Reflection;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace AutoAnthonyCardTinkering;

/// <summary>
/// Optional compatibility boundary for Chimera. Card Tinkering must not become a hard dependency of Chimera (or
/// vice versa), so this adapter resolves only Chimera's public run state after its assembly has actually loaded.
/// A failed early lookup is deliberately not cached because unrelated mods may initialize before Chimera.
/// </summary>
internal static class ChimeraCompatibility
{
    private const string AssemblyName = "Chimera";
    private const string CharacterTypeName = "ChimeraMod.ChimeraCharacter";
    private const string RunContentTypeName = "ChimeraMod.RunContent";

    private static readonly object ResolutionLock = new();
    private static Type? _characterType;
    private static PropertyInfo? _isActiveProperty;
    private static bool _initialScanComplete;
    private static bool _knownAssemblyIncompatible;
    private static int _startupSuppressionDepth;
    private static int _reportedSuppression;
    private static int _reportedResolutionFailure;

    static ChimeraCompatibility()
    {
        // Card Tinkering and Chimera do not depend on one another, so their initializer order is unspecified. Scan
        // once for an already-loaded assembly, then resolve a later Chimera load through the runtime event. This
        // keeps the ordinary no-Chimera hot path reflection-free after its first check.
        AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
        {
            if (string.Equals(args.LoadedAssembly.GetName().Name, AssemblyName, StringComparison.Ordinal))
                Resolve(args.LoadedAssembly);
        };
    }

    /// <summary>
    /// Returns true only when the optional Chimera assembly is loaded and the supplied/current run is a Chimera
    /// run. With no materialized RunState (notably during FromSerializable), Chimera's public IsActive property is
    /// the authoritative initialization signal.
    /// </summary>
    internal static bool IsSuppressed(IRunState? runState = null)
    {
        if (Volatile.Read(ref _startupSuppressionDepth) > 0) return ReportSuppressed();
        if (!TryResolve()) return false;

        bool suppressed;
        if (runState is not null)
        {
            // An explicit run is authoritative. Do not let a stale global from a just-finished Chimera run suppress
            // map generation or saving for a known ordinary run.
            suppressed = HasChimeraPlayer(runState);
        }
        else
        {
            var current = RunManager.Instance.DebugOnlyGetState();
            // During RunState.FromSerializable, Chimera restores its public content before the new RunState is
            // returned, while RunManager may still expose the previous run. The public active flag therefore has
            // to participate even when an old current state remains temporarily visible.
            suppressed = (current is not null && HasChimeraPlayer(current)) || ReadRunContentActive();
        }

        return suppressed && ReportSuppressed();
    }

    internal static bool IsSuppressed(CardModel? card)
    {
        if (Volatile.Read(ref _startupSuppressionDepth) > 0) return ReportSuppressed();
        if (!TryResolve()) return false;
        if (card?.Owner?.RunState is IRunState runState) return IsSuppressed(runState);
        if (card?.Owner?.Character is { } character && _characterType!.IsInstanceOfType(character))
            return ReportSuppressed();
        return IsSuppressed();
    }

    internal static bool BeginRunStartupIfChimera(object? character)
    {
        if (!TryResolve() || character is null || !_characterType!.IsInstanceOfType(character)) return false;
        Interlocked.Increment(ref _startupSuppressionDepth);
        ReportSuppressed();
        return true;
    }

    internal static void EndRunStartup(bool entered)
    {
        if (!entered) return;
        if (Interlocked.Decrement(ref _startupSuppressionDepth) < 0)
        {
            Interlocked.Exchange(ref _startupSuppressionDepth, 0);
            Log.Error("[CardTinkering] Chimera startup suppression scope became unbalanced; it was reset safely.");
        }
    }

    private static bool ReportSuppressed()
    {
        if (Interlocked.Exchange(ref _reportedSuppression, 1) == 0)
            Log.Info("[CardTinkering] Chimera run detected; all Card Tinkering features are disabled for this run.");
        return true;
    }

    private static bool HasChimeraPlayer(IRunState runState) =>
        runState.Players.Any(player => _characterType!.IsInstanceOfType(player.Character));

    private static bool ReadRunContentActive()
    {
        try { return _isActiveProperty?.GetValue(null) is true; }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _reportedResolutionFailure, 1) == 0)
                Log.Warn($"[CardTinkering] Could not read Chimera's public run state; compatibility fallback is inactive: {exception.Message}");
            return false;
        }
    }

    private static bool TryResolve()
    {
        if (_characterType is not null) return true;
        lock (ResolutionLock)
        {
            if (_characterType is not null) return true;
            if (_knownAssemblyIncompatible) return false;
            if (_initialScanComplete) return false;
            _initialScanComplete = true;
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
                string.Equals(candidate.GetName().Name, AssemblyName, StringComparison.Ordinal));
            if (assembly is null) return false;
            return ResolveLocked(assembly);
        }
    }

    private static void Resolve(Assembly assembly)
    {
        lock (ResolutionLock)
        {
            if (_characterType is not null || _knownAssemblyIncompatible) return;
            ResolveLocked(assembly);
        }
    }

    private static bool ResolveLocked(Assembly assembly)
    {
        var characterType = assembly.GetType(CharacterTypeName, throwOnError: false);
        var activeProperty = assembly.GetType(RunContentTypeName, throwOnError: false)?
            .GetProperty("IsActive", BindingFlags.Public | BindingFlags.Static);
        if (characterType is null || activeProperty?.PropertyType != typeof(bool))
        {
            _knownAssemblyIncompatible = true;
            if (Interlocked.Exchange(ref _reportedResolutionFailure, 1) == 0)
            {
                Log.Warn("[CardTinkering] Chimera is loaded but its public compatibility surface was not found; "
                         + "Card Tinkering will leave compatibility suppression inactive.");
            }
            return false;
        }

        _isActiveProperty = activeProperty;
        // Publish the character type last so other threads cannot observe a half-resolved adapter.
        _characterType = characterType;
        return true;
    }
}
