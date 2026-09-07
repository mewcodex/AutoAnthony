using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Buffers.Binary;
using ChaosCardGenerator;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;

namespace AutoAnthony;

public sealed record ChaosCardDefinition(
    int Slot,
    GeneratedCard Card,
    string PortraitPath,
    string HitFx,
    string AttackAnimation,
    string PowerIconPath,
    string PowerBigIconPath,
    IReadOnlyList<OperationRuntimeSpec>? RuntimeSpecs = null,
    IReadOnlyList<string?>? UpgradeValueSlots = null,
    string? PortraitSourceId = null,
    string? PortraitVariantId = null,
    string? PortraitVariantPath = null);

public static class ChaosRunDefinitions
{
    private const int PoolGenerationAttemptLimit = 4;
    private sealed record ArtSource(
        GeneratedCharacter Owner,
        CardModel Card,
        IReadOnlySet<string> DescriptionSchemas,
        IReadOnlySet<string> Templates,
        ComponentAtom? PrimaryEffect);

    private sealed record ArtCandidate(
        ArtSource Source,
        string PortraitPath,
        string? VariantId,
        string? VariantPath);

    private sealed record ArtQuery(
        IReadOnlySet<string> DescriptionSchemas,
        IReadOnlySet<string> Templates,
        GeneratorOperation? PrimaryEffect);

    public const int BasicCount = 10;
    public const int SilentBasicCount = 12;
    public const int CommonCount = 20;
    public const int UncommonCount = 35;
    public const int RareCount = 25;
    public const int AncientCount = 2;
    public const int ColorlessUncommonCount = 31;
    public const int ColorlessRareCount = 21;
    public const int ColorlessCount = ColorlessUncommonCount + ColorlessRareCount;
    public const int TotalCount = BasicCount + CommonCount + UncommonCount + RareCount + AncientCount;
    public const int SilentTotalCount = SilentBasicCount + CommonCount + UncommonCount + RareCount + AncientCount;

    private static readonly object Gate = new();
    private static readonly SemaphoreSlim AsyncGenerationGate = new(1, 1);
    private static readonly Dictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> Definitions = new();
    private static readonly Dictionary<GeneratedCharacter, string> Seeds = new();
    private static readonly Dictionary<GeneratedCharacter, bool> DefinitionUltimateModes = new();
    private static readonly Dictionary<GeneratedCharacter, bool> DefinitionBalancedValueModes = new();
    private static readonly Dictionary<GeneratedCharacter, bool> DefinitionNumericRandomModes = new();
    private static readonly Dictionary<GeneratedCharacter, bool> DefinitionReplaceStartingCardsModes = new();
    private static readonly Dictionary<GeneratedCharacter, bool> DefinitionPreserveOriginalCardsModes = new();
    private static readonly HashSet<GeneratedCharacter> ActiveCharacterSet = [];
    private static volatile bool _ancientFuelActive;
    private static volatile bool _activeUltimateChaos;
    private static volatile bool _activeNumericBalanceOptimization = true;
    private static volatile bool _activeReplaceStartingCards = true;
    private static volatile bool _activeNumericRandomMode;
    private static volatile bool _activePreserveOriginalCards;
    private static volatile bool _activeRandomCardArt;
    private static volatile bool _runActive;
    private static CardModel[]? _originalColorlessCards;
    private static ArtSource[]? _originalArtSources;
    private static readonly object ArtSourceGate = new();
    private static readonly Lazy<IReadOnlyDictionary<string, Type>> NativeCardTypesByName = new(() =>
        typeof(CardModel).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(CardModel).IsAssignableFrom(type))
            .GroupBy(type => type.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));

    internal sealed record RuntimePoolState(
        IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> Definitions,
        IReadOnlyDictionary<GeneratedCharacter, string> Seeds,
        IReadOnlyDictionary<GeneratedCharacter, bool> UltimateModes,
        IReadOnlyDictionary<GeneratedCharacter, bool> BalancedValueModes,
        IReadOnlyDictionary<GeneratedCharacter, bool> NumericRandomModes,
        IReadOnlyDictionary<GeneratedCharacter, bool> ReplaceStartingCardsModes,
        IReadOnlyDictionary<GeneratedCharacter, bool> PreserveOriginalCardsModes,
        IReadOnlyList<GeneratedCharacter> ActiveCharacters,
        bool RunActive,
        bool AncientFuelActive,
        bool ActiveUltimateChaos,
        bool ActiveNumericBalanceOptimization,
        bool ActiveReplaceStartingCards,
        bool ActiveNumericRandomMode,
        bool ActivePreserveOriginalCards,
        bool ActiveRandomCardArt);
    internal sealed record HistoryActivationReport(string SavedVersion, int RestoredCards, int RejectedCards,
        IReadOnlyList<(GeneratedCharacter Character, int Slot)> RestoredSlots);
    internal static readonly GeneratedCharacter[] SupportedPools =
    [
        GeneratedCharacter.Ironclad, GeneratedCharacter.Silent, GeneratedCharacter.Defect,
        GeneratedCharacter.Necrobinder, GeneratedCharacter.Regent, GeneratedCharacter.Colorless
    ];

    public static string ActiveSeed => Seeds.TryGetValue(GeneratedCharacter.Ironclad, out var seed) ? seed : string.Empty;
    public static bool AncientFuelActive => _ancientFuelActive;
    public static bool ActiveUltimateChaos => _activeUltimateChaos;
    public static bool ActiveNumericBalanceOptimization => _activeNumericBalanceOptimization;
    public static bool ActiveReplaceStartingCards => _activeReplaceStartingCards;
    public static bool ActiveNumericRandomMode => _activeNumericRandomMode;
    public static bool ActivePreserveOriginalCards => _activePreserveOriginalCards;
    public static bool ActiveRandomCardArt => _activeRandomCardArt;
    public static IReadOnlyList<GeneratedCharacter> ActiveCharacters
    {
        get
        {
            lock (Gate) return ActiveCharacterSet.OrderBy(character => character).ToArray();
        }
    }
    // Kept for older call sites that genuinely require a single-player run. Multiplayer callers must use
    // ActiveCharacters or IsCharacterRunActive instead of silently choosing one player.
    public static GeneratedCharacter? ActiveCharacter => ActiveCharacters.Count == 1 ? ActiveCharacters[0] : null;
    public static bool IsRunActive => _runActive;
    public static bool IsCharacterRunActive(GeneratedCharacter character)
    {
        lock (Gate) return ActiveCharacterSet.Contains(character);
    }
    public static IReadOnlyList<ChaosCardDefinition> Cards => GetCards(GeneratedCharacter.Ironclad);

    internal static RuntimePoolState CaptureRuntimeState()
    {
        lock (Gate)
            return new RuntimePoolState(
                Definitions.ToDictionary(pair => pair.Key, pair => pair.Value),
                Seeds.ToDictionary(pair => pair.Key, pair => pair.Value),
                DefinitionUltimateModes.ToDictionary(pair => pair.Key, pair => pair.Value),
                DefinitionBalancedValueModes.ToDictionary(pair => pair.Key, pair => pair.Value),
                DefinitionNumericRandomModes.ToDictionary(pair => pair.Key, pair => pair.Value),
                DefinitionReplaceStartingCardsModes.ToDictionary(pair => pair.Key, pair => pair.Value),
                DefinitionPreserveOriginalCardsModes.ToDictionary(pair => pair.Key, pair => pair.Value),
                ActiveCharacterSet.OrderBy(character => character).ToArray(),
                _runActive, _ancientFuelActive, _activeUltimateChaos, _activeNumericBalanceOptimization,
                _activeReplaceStartingCards, _activeNumericRandomMode, _activePreserveOriginalCards,
                _activeRandomCardArt);
    }

    internal static void RestoreRuntimeState(RuntimePoolState state)
    {
        lock (Gate)
        {
            Definitions.Clear();
            foreach (var pair in state.Definitions) Definitions[pair.Key] = pair.Value;
            Seeds.Clear();
            foreach (var pair in state.Seeds) Seeds[pair.Key] = pair.Value;
            DefinitionUltimateModes.Clear();
            foreach (var pair in state.UltimateModes) DefinitionUltimateModes[pair.Key] = pair.Value;
            DefinitionBalancedValueModes.Clear();
            foreach (var pair in state.BalancedValueModes) DefinitionBalancedValueModes[pair.Key] = pair.Value;
            DefinitionNumericRandomModes.Clear();
            foreach (var pair in state.NumericRandomModes) DefinitionNumericRandomModes[pair.Key] = pair.Value;
            DefinitionReplaceStartingCardsModes.Clear();
            foreach (var pair in state.ReplaceStartingCardsModes)
                DefinitionReplaceStartingCardsModes[pair.Key] = pair.Value;
            DefinitionPreserveOriginalCardsModes.Clear();
            foreach (var pair in state.PreserveOriginalCardsModes)
                DefinitionPreserveOriginalCardsModes[pair.Key] = pair.Value;
            ActiveCharacterSet.Clear();
            ActiveCharacterSet.UnionWith(state.ActiveCharacters);
            _runActive = state.RunActive;
            _ancientFuelActive = state.AncientFuelActive;
            _activeUltimateChaos = state.ActiveUltimateChaos;
            _activeNumericBalanceOptimization = state.ActiveNumericBalanceOptimization;
            _activeReplaceStartingCards = state.ActiveReplaceStartingCards;
            _activeNumericRandomMode = state.ActiveNumericRandomMode;
            _activePreserveOriginalCards = state.ActivePreserveOriginalCards;
            _activeRandomCardArt = state.ActiveRandomCardArt;
            foreach (var character in SupportedPools) ResetCanonicalCardCaches(character);
        }
    }

    public static void ActivateLibraryPreview()
    {
        // Library placeholders are not a run and must stay cheap/stable regardless of the persisted option.
        // The selected mode is applied by Activate when an actual run is created.
        const bool ultimateChaos = false;
        _ancientFuelActive = false;
        _activeRandomCardArt = false;
        CaptureOriginalColorlessCards();
        foreach (var character in SupportedPools)
            BuildPreview(character, $"{character.ToString().ToUpperInvariant()}_CHAOS_LIBRARY_PREVIEW", ultimateChaos,
                balancedValues: true);
    }

    public static void Activate(string seed) => Activate(GeneratedCharacter.Ironclad, seed);

    public static void Activate(GeneratedCharacter character, string seed)
        => Activate([character], seed);

    public static void Activate(IEnumerable<GeneratedCharacter> characters, string seed)
        => ActivateWithMode(characters, seed, ChaosModSettings.EffectiveUltimateChaos,
            ChaosModSettings.ReplaceStartingCards, ChaosModSettings.EffectiveNumericBalanceOptimization,
            ChaosModSettings.EffectiveNumericRandomMode, ChaosModSettings.PreserveOriginalCards,
            ChaosModSettings.RandomCardArt, showProgress: true);

    internal static Task<ChaosGenerationProgressOverlay?> ActivateAsync(GeneratedCharacter character, string seed)
        => ActivateAsync([character], seed);

    /// <summary>
    /// Generates a new run's six pools off the Godot main thread while yielding process frames for the loading
    /// overlay. The generated definitions are installed back on the main thread before NGame creates RunState.
    /// </summary>
    internal static async Task<ChaosGenerationProgressOverlay?> ActivateAsync(
        IEnumerable<GeneratedCharacter> characters, string seed, bool? forcedUltimateChaos = null,
        bool? forcedReplaceStartingCards = null, bool? forcedNumericBalanceOptimization = null,
        bool? forcedNumericRandomMode = null, bool? forcedPreserveOriginalCards = null,
        bool? forcedRandomCardArt = null)
    {
        await AsyncGenerationGate.WaitAsync();
        ChaosGenerationProgressOverlay? progress = null;
        var previousState = CaptureRuntimeState();
        try
        {
            var activeCharacters = NormalizeCharacters(characters);
            if (activeCharacters.Length == 0)
            {
                DeactivateRun();
                return null;
            }

            seed = NormalizeSeed(activeCharacters, seed);
            // Multiplayer passes the host's setting explicitly. Do not use the thread-static settings override
            // here: this method yields frames and performs generation on worker threads.
            var ultimateChaos = forcedUltimateChaos ?? ChaosModSettings.EffectiveUltimateChaos;
            var replaceStartingCards = forcedReplaceStartingCards ?? ChaosModSettings.ReplaceStartingCards;
            var balancedValues = forcedNumericBalanceOptimization
                                 ?? ChaosModSettings.EffectiveNumericBalanceOptimization;
            var randomizeNumericValues = forcedNumericRandomMode ?? ChaosModSettings.EffectiveNumericRandomMode;
            var preserveOriginalCards = forcedPreserveOriginalCards ?? ChaosModSettings.PreserveOriginalCards;
            var randomCardArt = forcedRandomCardArt ?? ChaosModSettings.RandomCardArt;
            lock (Gate)
            {
                SetActiveCharacters(activeCharacters);
                _activeUltimateChaos = ultimateChaos;
                _activeNumericBalanceOptimization = balancedValues;
                _activeReplaceStartingCards = replaceStartingCards;
                _activeNumericRandomMode = randomizeNumericValues;
                _activePreserveOriginalCards = preserveOriginalCards;
                _activeRandomCardArt = randomCardArt;
                _ancientFuelActive = ShouldUseAncientFuel(seed);
                CaptureOriginalColorlessCards();
            }

            var pendingPools = SupportedPools.Where(pool => NeedsBuild(pool, seed, ultimateChaos, balancedValues,
                randomizeNumericValues)).ToArray();
            if (pendingPools.Length == 0) return null;

            progress = ChaosGenerationProgressOverlay.Create(pendingPools.Sum(GenerationCountFor));
            var completed = 0;
            foreach (var pool in pendingPools)
            {
                var definitions = await GenerateDefinitionsWithRecoveryAsync(pool, seed, ultimateChaos,
                    balancedValues, randomizeNumericValues, completed, progress, previousState);
                lock (Gate) SetDefinitions(pool, seed, definitions, ultimateChaos, balancedValues,
                    randomizeNumericValues,
                    replaceStartingCards, preserveOriginalCards);
                completed += GenerationCountFor(pool);
                progress.Report(completed);
            }
            // Show the completed counter for a frame, then retain this same overlay through vanilla character/act
            // preloading, initial map creation, first autosave, and first-room entry.
            await NextProcessFrame();
            progress.ShowEnteringRun();
            await NextProcessFrame();
            return progress;
        }
        catch
        {
            progress?.Dispose();
            // Pools are installed one at a time so the UI can remain responsive. Roll the complete runtime state
            // back if a later pool fails; otherwise a failed run-start leaves a mixture of the new seed and the
            // previous library/run definitions behind for the next attempt.
            RestoreRuntimeState(previousState);
            throw;
        }
        finally
        {
            AsyncGenerationGate.Release();
        }
    }

    /// <summary>
    /// A single pool must never silently turn an otherwise generated run into a vanilla run. First retry the
    /// constrained assembly with an independent deterministic stream, then use a reference-free relaxed pool.
    /// The immutable preview/previous pool is the last-resort playable database if the generator itself throws.
    /// Multiplayer remains deterministic because the host serializes the recovered definitions authoritatively.
    /// </summary>
    private static async Task<IReadOnlyList<ChaosCardDefinition>> GenerateDefinitionsWithRecoveryAsync(
        GeneratedCharacter character, string seed, bool ultimateChaos, bool balancedValues,
        bool randomizeNumericValues, int completed, ChaosGenerationProgressOverlay progress,
        RuntimePoolState previousState)
    {
        var stages = new[]
        {
            (Seed: seed, EnforceConstraints: true, SuppressDerivativeReferences: false, Name: "primary"),
            (Seed: seed + "/POOL_RECOVERY", EnforceConstraints: true,
                SuppressDerivativeReferences: true, Name: "constrained recovery"),
            (Seed: seed + "/POOL_RELAXED_RECOVERY", EnforceConstraints: false,
                SuppressDerivativeReferences: true, Name: "relaxed recovery")
        };
        Exception? lastFailure = null;
        foreach (var stage in stages)
        {
            var latestInPool = 1;
            progress.Report(completed + latestInPool);
            await NextProcessFrame();
            try
            {
                var generationTask = Task.Run(() => GenerateCardCandidates(character, stage.Seed, ultimateChaos,
                    balancedValues, randomizeNumericValues, stage.EnforceConstraints,
                    current => Interlocked.Exchange(ref latestInPool, current),
                    stage.SuppressDerivativeReferences));
                while (!generationTask.IsCompleted)
                {
                    progress.Report(completed + Math.Clamp(Volatile.Read(ref latestInPool), 1,
                        GenerationCountFor(character)));
                    await NextProcessFrame();
                }

                var candidate = await generationTask;
                var definitions = BuildDefinitions(character, ultimateChaos, stage.EnforceConstraints, candidate);
                if (stage.Name != "primary")
                    Log.Warn($"[AutoAnthony] Recovered {character} run-pool generation through {stage.Name}.");
                return definitions;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
                Log.Error($"[AutoAnthony] {character} {stage.Name} pool generation failed: {exception}");
            }
        }

        if (previousState.Definitions.TryGetValue(character, out var previous)
            && previous.Count == CountFor(character))
        {
            Log.Error($"[AutoAnthony] Reused the complete previous {character} pool after all deterministic "
                      + $"recovery stages failed. Last failure: {lastFailure}");
            return previous;
        }

        throw new InvalidOperationException(
            $"Could not generate or recover a complete {character} card pool.", lastFailure);
    }

    private static async Task NextProcessFrame()
    {
        if (Engine.GetMainLoop() is SceneTree tree)
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        else
            await Task.Delay(16);
    }

    internal static void ActivateForStartupAudit(IEnumerable<GeneratedCharacter> characters, string seed,
        bool randomizeNumericValues = false)
        => ActivateWithMode(characters, seed, ultimateChaos: false, replaceStartingCards: true,
            balancedValues: true, randomizeNumericValues, preserveOriginalCards: false,
            randomCardArt: false, showProgress: false);

    internal static void SelectActiveCharactersForStartupAudit(IEnumerable<GeneratedCharacter> characters,
        bool replaceStartingCards = true, bool preserveOriginalCards = false)
    {
        // ModelDb startup already built and audited the immutable library-preview definitions for all six pools.
        // Replacement audits only need to vary which characters are active; regenerating all pools for every
        // single-player case used to dominate startup time without testing any additional generation behavior.
        lock (Gate)
        {
            SetActiveCharacters(NormalizeCharacters(characters));
            _activeUltimateChaos = false;
            _activeNumericBalanceOptimization = true;
            _activeReplaceStartingCards = replaceStartingCards;
            _activeNumericRandomMode = false;
            _activePreserveOriginalCards = preserveOriginalCards;
            _activeRandomCardArt = false;
            _ancientFuelActive = false;
        }
    }

    private static void ActivateWithMode(IEnumerable<GeneratedCharacter> characters, string seed, bool ultimateChaos,
        bool replaceStartingCards, bool balancedValues, bool randomizeNumericValues, bool preserveOriginalCards,
        bool randomCardArt, bool showProgress)
    {
        var activeCharacters = NormalizeCharacters(characters);
        if (activeCharacters.Length == 0)
        {
            DeactivateRun();
            return;
        }
        seed = NormalizeSeed(activeCharacters, seed);
        lock (Gate)
        {
            SetActiveCharacters(activeCharacters);
            _activeUltimateChaos = ultimateChaos;
            _activeNumericBalanceOptimization = balancedValues;
            _activeReplaceStartingCards = replaceStartingCards;
            _activeNumericRandomMode = randomizeNumericValues;
            _activePreserveOriginalCards = preserveOriginalCards;
            _activeRandomCardArt = randomCardArt;
            _ancientFuelActive = ShouldUseAncientFuel(seed);
            CaptureOriginalColorlessCards();
            var pendingPools = SupportedPools.Where(pool => NeedsBuild(pool, seed, ultimateChaos, balancedValues,
                randomizeNumericValues)).ToArray();
            using var progress = showProgress && pendingPools.Length > 0
                ? ChaosGenerationProgressOverlay.Create(pendingPools.Sum(GenerationCountFor))
                : null;
            var completed = 0;
            foreach (var pool in pendingPools)
            {
                var completedBeforePool = completed;
                Build(pool, seed, ultimateChaos, balancedValues, randomizeNumericValues,
                    current => progress?.Report(completedBeforePool + current));
                completed += GenerationCountFor(pool);
            }
        }
    }

    public static ChaosPoolRestoreReport ActivateFromSave(GeneratedCharacter character, string seed, string? snapshot)
        => ActivateFromSave([character], seed, snapshot);

    public static ChaosPoolRestoreReport ActivateFromSave(IEnumerable<GeneratedCharacter> characters, string seed, string? snapshot)
    {
        var activeCharacters = NormalizeCharacters(characters);
        if (activeCharacters.Length == 0)
            throw new InvalidOperationException("A generated-card save must contain at least one supported player character.");
        seed = NormalizeSeed(activeCharacters, seed);
        lock (Gate)
        {
            var savedUltimateChaos = ChaosPoolSnapshot.RestoreUltimateChaosFlag(snapshot, seed);
            var savedReplaceStartingCards = ChaosPoolSnapshot.RestoreReplaceStartingCardsFlag(snapshot, seed);
            var savedBalancedValues = ChaosPoolSnapshot.RestoreNumericBalanceOptimizationFlag(snapshot, seed);
            var savedNumericRandomMode = ChaosPoolSnapshot.RestoreNumericRandomModeFlag(snapshot, seed);
            var savedPreserveOriginalCards = ChaosPoolSnapshot.RestorePreserveOriginalCardsFlag(snapshot, seed);
            using var generationMode = ChaosModSettings.OverrideGenerationMode(savedUltimateChaos,
                savedBalancedValues, savedNumericRandomMode);
            SetActiveCharacters(activeCharacters);
            _activeUltimateChaos = savedUltimateChaos;
            _activeNumericBalanceOptimization = savedBalancedValues;
            _activeReplaceStartingCards = savedReplaceStartingCards;
            _activeNumericRandomMode = savedNumericRandomMode;
            _activePreserveOriginalCards = savedPreserveOriginalCards;
            _activeRandomCardArt = ChaosPoolSnapshot.RestoreRandomCardArtFlag(snapshot, seed);
            _ancientFuelActive = ChaosPoolSnapshot.RestoreAncientFuelFlag(snapshot, seed);
            CaptureOriginalColorlessCards();
            // The overwhelmingly common load path has a complete, current snapshot. Validate and install it
            // directly; generating 514 fallback cards first was wasted work unless at least one saved card is
            // actually incompatible. The slower path below retains selective cross-version regeneration.
            // Pool-wide support quotas are generation-time design constraints, not runtime validity rules.
            // A complete saved pool already contains concrete, individually validated derivative/orb assignments;
            // requiring it to satisfy the current version's producer quotas made otherwise compatible older saves
            // regenerate all 514 fallback cards behind the continue-loading screen. Besides looking like a hang on
            // slower machines, the subsequent repair could silently rebind a saved card. Only an actually
            // unreadable card should enter the selective regeneration path below.
            if (ChaosPoolSnapshot.TryRestoreComplete(snapshot, activeCharacters, seed,
                    out var complete, out var completeReport))
            {
                foreach (var pool in SupportedPools)
                    SetDefinitions(pool, seed, complete[pool], savedUltimateChaos, savedBalancedValues,
                        savedNumericRandomMode,
                        savedReplaceStartingCards, savedPreserveOriginalCards);
                return completeReport;
            }
            if (!string.IsNullOrWhiteSpace(completeReport.Failure))
                Log.Warn($"[AutoAnthony] Complete run snapshot needs selective compatibility recovery: "
                         + completeReport.Failure);
            using var progress = ChaosGenerationProgressOverlay.Create(SupportedPools.Sum(GenerationCountFor));
            var completed = 0;
            var fallbacks = new Dictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>>();
            foreach (var pool in SupportedPools)
            {
                var completedBeforePool = completed;
                fallbacks[pool] = GenerateDefinitionsResponsive(pool, seed, savedUltimateChaos,
                    savedBalancedValues, savedNumericRandomMode,
                    current => progress.Report(completedBeforePool + current));
                completed += GenerationCountFor(pool);
            }
            var restored = ChaosPoolSnapshot.RestoreAll(snapshot, activeCharacters, seed, fallbacks, out var report);
            foreach (var pool in SupportedPools)
                SetDefinitions(pool, seed, EnsureDerivativeConstraints(restored[pool], fallbacks[pool], pool, seed),
                    savedUltimateChaos, savedBalancedValues, savedNumericRandomMode, savedReplaceStartingCards,
                    savedPreserveOriginalCards);
            return report;
        }
    }

    internal static HistoryActivationReport ActivateHistoryFromSave(IEnumerable<GeneratedCharacter> characters,
        string seed, string? snapshot)
    {
        var activeCharacters = NormalizeCharacters(characters);
        if (activeCharacters.Length == 0)
            throw new InvalidOperationException("A generated-card history entry must contain a supported player character.");
        seed = NormalizeSeed(activeCharacters, seed);
        lock (Gate)
        {
            if (!ChaosPoolSnapshot.TryRestoreForHistory(snapshot, activeCharacters, seed,
                    out var historical, out var failure))
                throw new InvalidDataException(failure ?? "The generated-card history snapshot could not be read.");

            SetActiveCharacters(activeCharacters);
            _activeUltimateChaos = historical.UltimateChaos;
            _activeNumericBalanceOptimization = historical.NumericBalanceOptimization;
            _activeReplaceStartingCards = historical.ReplaceStartingCards;
            _activeNumericRandomMode = historical.NumericRandomMode;
            _activePreserveOriginalCards = historical.PreserveOriginalCards;
            _activeRandomCardArt = historical.Cards.Values.SelectMany(cards => cards.Values)
                .Any(definition => definition.PortraitVariantId is not null);
            _ancientFuelActive = historical.AncientFuel;
            CaptureOriginalColorlessCards();
            var installed = new List<(GeneratedCharacter Character, int Slot)>();
            foreach (var (character, savedCards) in historical.Cards)
            {
                var expected = CountFor(character);
                ChaosCardDefinition[] merged;
                if (Definitions.TryGetValue(character, out var current) && current.Count == expected)
                    merged = current.ToArray();
                else if (savedCards.Count == expected)
                    merged = Enumerable.Range(0, expected).Select(slot => savedCards[slot]).ToArray();
                else
                    continue;

                foreach (var (slot, definition) in savedCards)
                {
                    merged[slot] = definition;
                    installed.Add((character, slot));
                }
                SetDefinitions(character, seed, merged, historical.UltimateChaos,
                    historical.NumericBalanceOptimization, historical.NumericRandomMode,
                    historical.ReplaceStartingCards, historical.PreserveOriginalCards);
            }

            if (installed.Count == 0)
                throw new InvalidDataException("The generated-card history snapshot contained no installable cards.");
            return new HistoryActivationReport(historical.SavedVersion, installed.Count,
                historical.RejectedCards, installed);
        }
    }

    private static IReadOnlyList<ChaosCardDefinition> EnsureDerivativeConstraints(
        IReadOnlyList<ChaosCardDefinition> restored,
        IReadOnlyList<ChaosCardDefinition> fallback,
        GeneratedCharacter character,
        string seed)
    {
        try
        {
            DerivativePoolConstraintResolver.Audit(restored.Select(definition => definition.Card).ToArray());
            return restored;
        }
        catch
        {
            var cards = restored.Select(definition => definition.Card).ToArray();
            var random = new Random(StableSeed(character, seed + "/DERIVATIVE_RESTORE"));
            if (!DerivativePoolConstraintResolver.TryResolve(cards, random, out _)) return fallback;
            return restored.Select((definition, index) => definition with { Card = cards[index] }).ToArray();
        }
    }

    private static bool NeedsBuild(GeneratedCharacter character, string seed, bool ultimateChaos,
        bool balancedValues, bool randomizeNumericValues)
    {
        var expected = CountFor(character);
        return !(Seeds.TryGetValue(character, out var activeSeed) && activeSeed == seed
            && DefinitionUltimateModes.GetValueOrDefault(character) == ultimateChaos
            && DefinitionBalancedValueModes.GetValueOrDefault(character) == balancedValues
            && DefinitionNumericRandomModes.GetValueOrDefault(character) == randomizeNumericValues
            && DefinitionReplaceStartingCardsModes.GetValueOrDefault(character) == _activeReplaceStartingCards
            && DefinitionPreserveOriginalCardsModes.GetValueOrDefault(character) == _activePreserveOriginalCards
            && Definitions.TryGetValue(character, out var cached) && cached.Count == expected
            && cached.All(definition => (definition.PortraitVariantId is not null) == _activeRandomCardArt));
    }

    private static void Build(GeneratedCharacter character, string seed, bool ultimateChaos, bool balancedValues,
        bool randomizeNumericValues, Action<int>? progress = null)
    {
        if (!NeedsBuild(character, seed, ultimateChaos, balancedValues, randomizeNumericValues)) return;

        SetDefinitions(character, seed,
            GenerateDefinitions(character, seed, ultimateChaos, balancedValues, randomizeNumericValues,
                progress: progress), ultimateChaos, balancedValues, randomizeNumericValues,
            _activeReplaceStartingCards, _activePreserveOriginalCards);
    }

    private static void BuildPreview(GeneratedCharacter character, string seed, bool ultimateChaos,
        bool balancedValues)
    {
        _activeRandomCardArt = false;
        var expected = CountFor(character);
        if (Seeds.TryGetValue(character, out var activeSeed) && activeSeed == seed
            && DefinitionUltimateModes.GetValueOrDefault(character) == ultimateChaos
            && DefinitionBalancedValueModes.GetValueOrDefault(character) == balancedValues
            && DefinitionNumericRandomModes.GetValueOrDefault(character) == false
            && DefinitionPreserveOriginalCardsModes.GetValueOrDefault(character) == false
            && Definitions.TryGetValue(character, out var cached) && cached.Count == expected)
            return;
        SetDefinitions(character, seed,
            GenerateDefinitions(character, seed, ultimateChaos, balancedValues, randomizeNumericValues: false,
                enforcePoolConstraints: false), ultimateChaos, balancedValues, randomizeNumericValues: false,
            _activeReplaceStartingCards, preserveOriginalCards: false);
    }

    private static IReadOnlyList<ChaosCardDefinition> GenerateDefinitions(GeneratedCharacter character, string seed,
        bool ultimateChaos, bool balancedValues, bool randomizeNumericValues,
        bool enforcePoolConstraints = true, Action<int>? progress = null)
    {
        var candidate = GenerateCardCandidates(character, seed, ultimateChaos, balancedValues,
            randomizeNumericValues,
            enforcePoolConstraints, progress);
        return BuildDefinitions(character, ultimateChaos, enforcePoolConstraints, candidate);
    }

    /// <summary>
    /// RunState.FromSerializable is synchronous in v111. Generate its rare incompatible-save fallbacks on a worker
    /// and service native window events while waiting, so Windows does not mark the game as hung and the overlay
    /// can still redraw even though this boundary cannot await Godot process frames.
    /// </summary>
    private static IReadOnlyList<ChaosCardDefinition> GenerateDefinitionsResponsive(
        GeneratedCharacter character,
        string seed,
        bool ultimateChaos,
        bool balancedValues,
        bool randomizeNumericValues,
        Action<int> progress)
    {
        var latest = 1;
        progress(latest);
        var task = Task.Run(() => GenerateCardCandidates(character, seed, ultimateChaos, balancedValues,
            randomizeNumericValues,
            enforcePoolConstraints: true, current => Interlocked.Exchange(ref latest, current)));
        while (!task.Wait(8))
        {
            progress(Math.Clamp(Volatile.Read(ref latest), 1, GenerationCountFor(character)));
            try
            {
                DisplayServer.ProcessEvents();
            }
            catch
            {
                // Headless audits have no active display server.
            }
        }
        var candidate = task.GetAwaiter().GetResult();
        progress(GenerationCountFor(character));
        return BuildDefinitions(character, ultimateChaos, enforcePoolConstraints: true, candidate);
    }

    private sealed record GeneratedPoolCandidate(
        GeneratedCard[] Cards,
        Random Random,
        GeneratedRarity[] Rarities,
        int AttemptsUsed,
        long GenerationMilliseconds,
        long CardAssemblyMilliseconds,
        long PoolRepairMilliseconds);

    private static GeneratedPoolCandidate GenerateCardCandidates(GeneratedCharacter character, string seed,
        bool ultimateChaos, bool balancedValues, bool randomizeNumericValues, bool enforcePoolConstraints,
        Action<int>? progress, bool suppressDerivativeReferences = false)
    {
        var stopwatch = Stopwatch.StartNew();
        var random = new Random(StableSeed(character, seed));
        var poolPolicy = PoolGenerationPolicy.For(character);
        var rarities = ExpectedRarities(character);
        var omitGeneratedAncients = enforcePoolConstraints && _activePreserveOriginalCards
            && character != GeneratedCharacter.Colorless;
        var generatedRarities = omitGeneratedAncients
            ? rarities.Take(rarities.Length - AncientCount).ToArray()
            : rarities;
        GeneratedCard[]? generatedCards;
        var poolConstraintFailure = string.Empty;
        var attemptsUsed = 0;
        long cardAssemblyMilliseconds = 0;
        long poolRepairMilliseconds = 0;
        GeneratedCard[] GenerateCandidate(RandomCardGenerator generator)
        {
            var candidateStopwatch = Stopwatch.StartNew();
            long localRepairMilliseconds = 0;
            var cards = new GeneratedCard[generatedRarities.Length];
            for (var index = 0; index < generatedRarities.Length; index++)
            {
                cards[index] = generator.Generate(generatedRarities[index]);
                progress?.Invoke(index + 1);
                // Repair the actual ten-card starting subset before the generator has reserved names and pool-unique
                // components for the other ~80 cards. Late repair could exhaust the uniqueness space and fail even
                // though plenty of legal Basic replacements existed when this subset was first assembled.
                if (index + 1 == poolPolicy.StartingDeckSize && enforcePoolConstraints
                    && poolPolicy.EnforceStartingCoverage && _activeReplaceStartingCards)
                {
                    var startingCards = cards.Take(poolPolicy.StartingDeckSize).ToArray();
                    var repairStopwatch = Stopwatch.StartNew();
                    EnsureStartingDeckCoverage(startingCards, generator, poolPolicy);
                    repairStopwatch.Stop();
                    localRepairMilliseconds += repairStopwatch.ElapsedMilliseconds;
                    Array.Copy(startingCards, cards, startingCards.Length);
                }
            }
            candidateStopwatch.Stop();
            cardAssemblyMilliseconds += Math.Max(0,
                candidateStopwatch.ElapsedMilliseconds - localRepairMilliseconds);
            poolRepairMilliseconds += localRepairMilliseconds;
            return cards;
        }
        if (!enforcePoolConstraints)
        {
            var previewGenerator = new RandomCardGenerator(character, random.Next(), ultimateChaos,
                AncientFuelActive, balancedValues: balancedValues,
                randomizeNumericValues: randomizeNumericValues,
                suppressDerivativeReferences: suppressDerivativeReferences);
            generatedCards = GenerateCandidate(previewGenerator);
            attemptsUsed = 1;
        }
        else
        {
            generatedCards = null;
            for (var attempt = 0; attempt < PoolGenerationAttemptLimit; attempt++)
            {
                attemptsUsed = attempt + 1;
                var generator = new RandomCardGenerator(character, random.Next(), ultimateChaos,
                    AncientFuelActive,
                    suppressDerivativeReferences: attempt == PoolGenerationAttemptLimit - 1,
                    balancedValues: balancedValues, randomizeNumericValues: randomizeNumericValues);
                GeneratedCard[] candidateCards;
                try
                {
                    candidateCards = GenerateCandidate(generator);
                    var repairStopwatch = Stopwatch.StartNew();
                    EnsureXCostDistribution(candidateCards, generatedRarities, generator, random, poolPolicy);
                    if (!TryResolvePoolSupportConstraints(candidateCards, generatedRarities, generator, random,
                            poolPolicy,
                            checkStartingDeck: _activeReplaceStartingCards
                                               && character != GeneratedCharacter.Colorless,
                            out poolConstraintFailure))
                    {
                        repairStopwatch.Stop();
                        poolRepairMilliseconds += repairStopwatch.ElapsedMilliseconds;
                        continue;
                    }
                    repairStopwatch.Stop();
                    poolRepairMilliseconds += repairStopwatch.ElapsedMilliseconds;
                }
                catch (Exception exception)
                {
                    // Coverage/X-quota repair is part of speculative whole-pool assembly. A failed candidate must
                    // consume one bounded attempt, not escape after every card slot was already reported complete.
                    poolConstraintFailure = exception.Message;
                    continue;
                }
                generatedCards = candidateCards;
                break;
            }
        }
        if (generatedCards is null)
            throw new InvalidOperationException($"Could not assemble a constraint-consistent {character} pool after "
                + $"{PoolGenerationAttemptLimit} bounded attempts: {poolConstraintFailure}");

        if (!ComponentPolicy.TryAuditPool(generatedCards, out var multiplicityFailure))
            throw new InvalidOperationException($"Generated {character} pool violates component multiplicity: "
                                                + multiplicityFailure);

        if (omitGeneratedAncients)
        {
            GeneratedCard[] placeholders;
            lock (Gate)
            {
                if (!Definitions.TryGetValue(character, out var existing)
                    || existing.Count != CountFor(character))
                    throw new InvalidOperationException(
                        $"The {character} library preview did not provide fixed Ancient slot placeholders.");
                placeholders = existing.TakeLast(AncientCount).Select(definition => definition.Card).ToArray();
            }
            generatedCards = generatedCards.Concat(placeholders).ToArray();
        }

        stopwatch.Stop();
        return new GeneratedPoolCandidate(generatedCards, random, rarities, attemptsUsed,
            stopwatch.ElapsedMilliseconds, cardAssemblyMilliseconds, poolRepairMilliseconds);
    }

    private static bool TryResolvePoolSupportConstraints(GeneratedCard[] cards,
        IReadOnlyList<GeneratedRarity> rarities, RandomCardGenerator generator, Random random,
        PoolGenerationPolicy policy, bool checkStartingDeck, out string failure)
    {
        // Resolve the complete character pool first. Sly repair is reference-free and preserves the Osty ratio;
        // derivative repair then preserves both ratios while binding or replacing any remaining consumers.
        if (!OstyPoolConstraintResolver.TryResolve(cards, rarities, generator, random,
                policy.ReplacementAttemptLimit, out failure)
            || !SlyPoolConstraintResolver.TryResolve(cards, rarities, generator, random,
                policy.ReplacementAttemptLimit, out failure)
            || !DerivativePoolConstraintResolver.TryRepairAndResolve(cards, rarities, generator, random,
                policy.ReplacementAttemptLimit, out failure))
            return false;

        try
        {
            OstyPoolConstraintResolver.Audit(cards);
            SlyPoolConstraintResolver.Audit(cards);
            DerivativePoolConstraintResolver.Audit(cards);
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return false;
        }

        if (!checkStartingDeck)
        {
            return GeneratedCardEffectIdentity.TryAudit(cards, out failure);
        }

        // The starting deck is always the first ten generated Basic slots, including Silent's twelve-card Basic
        // library. Re-run every support relationship over this ten-card subpool only after the whole pool passes.
        // Basic producers are counted inside this local graph; the whole-pool graph retains its Common/Uncommon
        // producer rule. Failure rerolls the bounded outer candidate rather than disturbing 4/4 combat coverage.
        if (cards.Length < policy.StartingDeckSize)
        {
            failure = "The generated pool does not contain a ten-card starting-deck subset.";
            return false;
        }
        var startingCards = cards.Take(policy.StartingDeckSize).ToArray();
        if (!StartingPoolConstraintResolver.TryRepair(startingCards, generator, random,
                policy.MinimumStartingDamage, policy.MinimumStartingDefense,
                policy.ReplacementAttemptLimit, out failure))
            return false;
        Array.Copy(startingCards, cards, startingCards.Length);
        try
        {
            OstyPoolConstraintResolver.Audit(cards);
            SlyPoolConstraintResolver.Audit(cards);
            DerivativePoolConstraintResolver.Audit(cards);
        }
        catch (Exception exception)
        {
            failure = "Starting-deck repair invalidated the complete pool: " + exception.Message;
            return false;
        }
        return GeneratedCardEffectIdentity.TryAudit(cards, out failure);
    }

    private static IReadOnlyList<ChaosCardDefinition> BuildDefinitions(GeneratedCharacter character,
        bool ultimateChaos, bool enforcePoolConstraints, GeneratedPoolCandidate candidate)
    {
        var assemblyStopwatch = Stopwatch.StartNew();
        var expected = CountFor(character);
        var random = candidate.Random;
        var rarities = candidate.Rarities;
        var generatedCards = candidate.Cards;
        // Portraits deliberately use a run-independent all-color source catalog. The generated card pool still
        // owns its own used-path set, so another character may reuse a portrait while this one never does.
        var artSources = OriginalArtSources();
        var ancientArt = artSources.Where(source => source.Owner == character
                && source.Card.Rarity == CardRarity.Ancient)
            .ToArray();
        var normalArt = artSources.Where(source => source.Card.Rarity != CardRarity.Ancient).ToArray();
        if (normalArt.Length == 0 || character != GeneratedCharacter.Colorless && ancientArt.Length != AncientCount)
            throw new InvalidOperationException($"The v111 portrait pools could not be resolved for {character}: "
                + $"normal={normalArt.Length}, ownAncient={ancientArt.Length}, total={artSources.Length}.");

        var hitFx = character == GeneratedCharacter.Silent
            ? new[] { "vfx/vfx_attack_slash", "vfx/vfx_dagger_spray", "vfx/vfx_flying_slash", "vfx/vfx_dramatic_stab", "vfx/vfx_dagger_throw" }
            : new[] { "vfx/vfx_attack_blunt", "vfx/vfx_heavy_blunt", "vfx/vfx_attack_slash", "vfx/vfx_bloody_impact", "vfx/vfx_rock_shatter" };
        var powerIcons = character == GeneratedCharacter.Silent
            ? new[] { "accelerant", "accuracy", "afterimage", "burst", "corrosive_wave", "envenom", "infinite_blades", "noxious_fumes", "phantom_blades", "serpent_form", "speedster", "tools_of_the_trade", "well_laid_plans", "wraith_form" }
            : new[] { "aggression", "barricade", "corruption", "crimson_mantle", "dark_embrace", "demon_form", "feel_no_pain", "inferno", "strength", "juggernaut", "rupture", "plating", "unmovable", "vicious" };
        var built = new List<ChaosCardDefinition>(expected);
        var usedPortraits = new HashSet<string>(StringComparer.Ordinal);
        var ancientAssignments = SelectAncientArt(character, generatedCards, rarities, ancientArt, random);
        for (var slot = 0; slot < rarities.Length; slot++)
        {
            var rarity = rarities[slot];
            var card = generatedCards[slot];
            var art = rarity == GeneratedRarity.Ancient
                ? SelectArtVariant(ancientAssignments[slot], _activeRandomCardArt, random)
                : SelectRelatedArt(character, card, normalArt, usedPortraits, _activeRandomCardArt, random);
            usedPortraits.Add(ArtCandidateKey(art));
            var powerIcon = powerIcons[random.Next(powerIcons.Length)] + "_power";
            built.Add(new ChaosCardDefinition(
                slot,
                card,
                art.PortraitPath,
                hitFx[random.Next(hitFx.Length)],
                card.Type == GeneratedCardType.Attack && random.Next(4) == 0 ? "heavyAttack" : card.Type == GeneratedCardType.Attack ? "Attack" : "Cast",
                $"res://images/atlases/power_atlas.sprites/{powerIcon}.tres",
                $"res://images/powers/{powerIcon}.png",
                card.Operations.Select(OperationRuntimeSpecCompiler.RequireStructured).ToArray(),
                card.Upgrade?.Effects.Select(effect => effect.ValueSlotId).ToArray() ?? [],
                art.Source.Card.Id.ToString(),
                art.VariantId,
                art.VariantPath));
        }
        assemblyStopwatch.Stop();
        Log.Info($"[AutoAnthony.Perf] Generated {character} {(enforcePoolConstraints ? "run" : "preview")} pool "
            + $"({built.Count} cards, attempts={candidate.AttemptsUsed}, ultimate={ultimateChaos}) in "
            + $"{candidate.GenerationMilliseconds} ms (card assembly={candidate.CardAssemblyMilliseconds} ms, "
            + $"pool repair={candidate.PoolRepairMilliseconds} ms); "
            + $"art/definition assembly={assemblyStopwatch.ElapsedMilliseconds} ms.");
        return built;
    }

    internal static AutoAnthonyEditorIdentity RerollEditorIdentity(GeneratedCard current, int seed)
    {
        if (!SupportedPools.Contains(current.Character))
            throw new ArgumentOutOfRangeException(nameof(current),
                $"No native name and portrait catalog is registered for {current.Character}.");

        var signature = GeneratedCardEffectIdentity.Signature(current);
        var nameRandom = EditorIdentityRandom(current.Character, seed, signature, "name");
        var artRandom = EditorIdentityRandom(current.Character, seed, signature, "portrait");
        var usedChinese = current.Name is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>([current.Name.Chinese], StringComparer.Ordinal);
        var usedEnglish = current.Name is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>([current.Name.English], StringComparer.Ordinal);
        GeneratedCardName name;
        try
        {
            name = CardNameGenerator.Generate(CharacterComponentCatalogs.Get(current.Character), current,
                nameRandom, usedChinese, usedEnglish);
        }
        catch (InvalidOperationException)
        {
            // A very small externally supplied name catalog may have no second legal combination. Identity reroll
            // must remain usable there, so fall back to the best legal name even when it equals the current one.
            name = CardNameGenerator.Generate(CharacterComponentCatalogs.Get(current.Character), current,
                EditorIdentityRandom(current.Character, seed, signature, "name-fallback"));
        }

        var sources = OriginalArtSources();
        var randomPortraits = _runActive ? _activeRandomCardArt : ChaosModSettings.RandomCardArt;
        ArtCandidate art;
        if (current.Rarity == GeneratedRarity.Ancient)
        {
            var ancient = sources.Where(source => source.Owner == current.Character
                    && source.Card.Rarity == CardRarity.Ancient)
                .ToArray();
            if (ancient.Length == 0)
                throw new InvalidOperationException(
                    $"No native Ancient portrait fallback is available for {current.Character}.");
            var query = CreateArtQuery(current);
            var best = ancient.GroupBy(source => ArtRank(current.Character, current, query, source))
                .OrderByDescending(group => group.Key).First().ToArray();
            art = SelectArtVariant(best[artRandom.Next(best.Length)], randomPortraits, artRandom);
        }
        else
        {
            // Ancient and ordinary card frames use different portrait dimensions. This filter is deliberately
            // applied before relevance ranking and before external variants are expanded.
            var ordinary = sources.Where(source => source.Card.Rarity != CardRarity.Ancient).ToArray();
            if (ordinary.Length == 0)
                throw new InvalidOperationException("No ordinary native portrait fallback is available.");
            art = SelectRelatedArt(current.Character, current, ordinary,
                new HashSet<string>(StringComparer.Ordinal), randomPortraits, artRandom);
        }

        return new AutoAnthonyEditorIdentity(name, art.PortraitPath, art.Source.Card.Id.ToString(),
            art.VariantId, art.VariantPath);
    }

    internal static void ValidateEditorIdentity(GeneratedCard card, AutoAnthonyEditorIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Name.Chinese)
            || string.IsNullOrWhiteSpace(identity.Name.English))
            throw new ArgumentException("An editor identity must contain both localized card names.",
                nameof(identity));
        if (!SupportedPools.Contains(card.Character))
            throw new ArgumentOutOfRangeException(nameof(card), "Unsupported generated-card character.");

        var sources = OriginalArtSources();
        var source = sources.FirstOrDefault(candidate =>
            string.Equals(PortraitPath(candidate), identity.PortraitPath, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(identity.PortraitSourceId)
                || string.Equals(candidate.Card.Id.ToString(), identity.PortraitSourceId,
                    StringComparison.OrdinalIgnoreCase)))
            ?? throw new ArgumentException("The editor portrait does not identify a native portrait source.",
                nameof(identity));
        if (card.Rarity == GeneratedRarity.Ancient)
        {
            if (source.Owner != card.Character || source.Card.Rarity != CardRarity.Ancient)
                throw new ArgumentException(
                    "An Ancient generated card must use an Ancient portrait from its own character.",
                    nameof(identity));
        }
        else if (source.Card.Rarity == CardRarity.Ancient)
        {
            throw new ArgumentException("An ordinary generated card cannot use an Ancient-sized portrait.",
                nameof(identity));
        }

        // Do not require the optional variant provider to exist on the applying machine. Save reloads and
        // multiplayer peers may legitimately lack the host's cosmetic pack; ResolvePath/TryResolveDirectTexture
        // retain the stable native source and fall back to its original portrait in that case.
    }

    private static Random EditorIdentityRandom(GeneratedCharacter character, int seed, string signature,
        string stream)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"AutoAnthony/EditorIdentity/v1|{character}|{seed}|{stream}|{signature}"));
        return new Random(BinaryPrimitives.ReadInt32LittleEndian(hash) & int.MaxValue);
    }

    internal static GeneratedRarity[] ExpectedRarities(GeneratedCharacter character)
    {
        if (character == GeneratedCharacter.Colorless)
            return Enumerable.Repeat(GeneratedRarity.Uncommon, ColorlessUncommonCount)
                .Concat(Enumerable.Repeat(GeneratedRarity.Rare, ColorlessRareCount)).ToArray();
        return Enumerable.Repeat(GeneratedRarity.Basic, BasicCountFor(character))
            .Concat(Enumerable.Repeat(GeneratedRarity.Common, CommonCount))
            .Concat(Enumerable.Repeat(GeneratedRarity.Uncommon, UncommonCount))
            .Concat(Enumerable.Repeat(GeneratedRarity.Rare, RareCount))
            .Concat(Enumerable.Repeat(GeneratedRarity.Ancient, AncientCount))
            .ToArray();
    }

    private static ArtSource[] OriginalArtSources()
    {
        lock (ArtSourceGate)
        {
            if (_originalArtSources is not null) return _originalArtSources;
            _originalArtSources = ResolveOriginalArtSources(owner => OriginalPool(owner));
            var ancientCounts = SupportedPools.Where(owner => owner != GeneratedCharacter.Colorless)
                .ToDictionary(owner => owner,
                    owner => _originalArtSources.Count(source => source.Owner == owner
                        && source.Card.Rarity == CardRarity.Ancient));
            Log.Info($"[AutoAnthony] Resolved immutable native portrait catalog: total={_originalArtSources.Length}, "
                     + $"ancients={string.Join(",", ancientCounts.Select(pair => $"{pair.Key}:{pair.Value}"))}.");
            return _originalArtSources;
        }
    }

    private static ArtSource[] ResolveOriginalArtSources(
        Func<GeneratedCharacter, IEnumerable<CardModel>> visiblePool)
    {
        var sources = new List<ArtSource>();
        foreach (var owner in SupportedPools)
        {
            var recipes = CharacterComponentCatalogs.Get(owner).Recipes;
            // Prefer the currently visible vanilla pool so normal model overrides remain observable. Card pools
            // are mutable extension points, however: another mod may replace/filter one before our ModelDbReady
            // postfix, and InitIds can be invoked more than once. Recover every missing source directly from
            // the registered v111 card type named by the offline component catalog.
            var visibleByType = visiblePool(owner)
                .Where(card => card is not ChaosCardModel
                    && card.GetType().Assembly == typeof(CardModel).Assembly)
                .GroupBy(card => card.GetType().Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var recipe in recipes)
            {
                if (!visibleByType.TryGetValue(recipe.Id, out var card))
                    card = ResolveNativeArtCard(recipe.Id);
                if (card is null)
                {
                    Log.Warn($"[AutoAnthony] Could not resolve native portrait source {owner}/{recipe.Id}.");
                    continue;
                }
                if (card.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly) continue;
                sources.Add(CreateArtSource(owner, card, recipe));
            }
        }
        return sources.ToArray();
    }

    private static CardModel? ResolveNativeArtCard(string recipeId)
    {
        if (!NativeCardTypesByName.Value.TryGetValue(recipeId, out var cardType)) return null;
        try
        {
            return ModelDb.GetById<CardModel>(ModelDb.GetId(cardType));
        }
        catch (Exception exception)
        {
            Log.Warn($"[AutoAnthony] Failed to recover native portrait model {recipeId}: {exception.Message}");
            return null;
        }
    }

    private static ArtSource CreateArtSource(GeneratedCharacter owner, CardModel card, IroncladCardRecipe? recipe)
    {
        var atoms = recipe?.Atoms ?? [];
        ChaosPortraitCompatibility.RegisterStableSource(
            $"res://images/atlases/card_atlas.sprites/{owner.ToString().ToLowerInvariant()}/{card.Id.Entry.ToLowerInvariant()}.tres",
            card);
        return new ArtSource(owner, card,
            atoms.Select(CardNameGenerator.RelationSchema).ToHashSet(StringComparer.Ordinal),
            atoms.Select(atom => atom.Template).ToHashSet(StringComparer.Ordinal),
            PrimaryEffect(atoms));
    }

    private static IReadOnlyDictionary<int, ArtSource> SelectAncientArt(GeneratedCharacter character,
        IReadOnlyList<GeneratedCard> cards, IReadOnlyList<GeneratedRarity> rarities,
        IReadOnlyList<ArtSource> ancientArt, Random random)
    {
        var slots = Enumerable.Range(0, rarities.Count)
            .Where(index => rarities[index] == GeneratedRarity.Ancient).ToArray();
        if (slots.Length == 0) return new Dictionary<int, ArtSource>();
        if (slots.Length != AncientCount || ancientArt.Count != AncientCount)
            throw new InvalidOperationException(
                $"{character} must match exactly two generated Ancient cards to its two original Ancient portraits.");

        var firstCard = cards[slots[0]];
        var secondCard = cards[slots[1]];
        var firstQuery = CreateArtQuery(firstCard);
        var secondQuery = CreateArtQuery(secondCard);
        var direct = AggregateArtRank(
            ArtRank(character, firstCard, firstQuery, ancientArt[0]),
            ArtRank(character, secondCard, secondQuery, ancientArt[1]));
        var crossed = AggregateArtRank(
            ArtRank(character, firstCard, firstQuery, ancientArt[1]),
            ArtRank(character, secondCard, secondQuery, ancientArt[0]));
        var useCrossed = crossed.CompareTo(direct) > 0
            || crossed == direct && random.Next(2) == 1;
        return new Dictionary<int, ArtSource>
        {
            [slots[0]] = ancientArt[useCrossed ? 1 : 0],
            [slots[1]] = ancientArt[useCrossed ? 0 : 1]
        };
    }

    private static ArtCandidate SelectRelatedArt(GeneratedCharacter character, GeneratedCard card,
        IReadOnlyList<ArtSource> eligible, IReadOnlySet<string> usedPortraits, bool randomPortraits, Random random)
    {
        var query = CreateArtQuery(card);
        foreach (var rankGroup in eligible
                     .GroupBy(source => ArtRank(character, card, query, source))
                     .OrderByDescending(group => group.Key))
        {
            // Variants do not alter semantic relevance. Expand only the highest-ranked group that still has an
            // unused visual; this preserves the exact priority model while avoiding a full 481-card texture scan.
            var available = ExpandArtCandidates(rankGroup, randomPortraits)
                .Where(candidate => !usedPortraits.Contains(ArtCandidateKey(candidate))).ToArray();
            if (available.Length > 0) return available[random.Next(available.Length)];
        }
        throw new InvalidOperationException(
            $"No unused portrait satisfies the Ancient-card boundary for {character}/{card.Rarity}.");
    }

    private static IEnumerable<ArtCandidate> ExpandArtCandidates(IEnumerable<ArtSource> sources, bool randomPortraits)
    {
        foreach (var source in sources)
        {
            var stablePath = PortraitPath(source);
            if (!randomPortraits)
            {
                yield return new ArtCandidate(source, stablePath, null, null);
                continue;
            }
            foreach (var variant in ChaosPortraitCompatibility.GetAvailableVariants(source.Card, stablePath))
                yield return new ArtCandidate(source, stablePath, variant.Id, variant.Path);
        }
    }

    private static ArtCandidate SelectArtVariant(ArtSource source, bool randomPortraits, Random random)
    {
        var variants = ExpandArtCandidates([source], randomPortraits).ToArray();
        return variants[random.Next(variants.Length)];
    }

    private static string ArtCandidateKey(ArtCandidate candidate) => candidate.PortraitPath
        + "|" + (candidate.VariantId ?? "$winning")
        + "|" + (candidate.VariantPath ?? string.Empty);

    private static ArtQuery CreateArtQuery(GeneratedCard card) => new(
        card.Operations.Select(CardNameGenerator.RelationSchema).ToHashSet(StringComparer.Ordinal),
        card.Operations.Select(operation => operation.Template).ToHashSet(StringComparer.Ordinal),
        PrimaryEffect(card.Operations));

    private static (int Description, int PrimaryEffect, int SameColorTypeCost, int SameColorType,
        int SameTypeCost, int SameType) ArtRank(GeneratedCharacter character, GeneratedCard card,
        ArtQuery query, ArtSource source)
    {
        var sameType = SourceType(source.Card) == card.Type;
        var sameCost = SamePrintedCost(card, source.Card);
        var sameColor = source.Owner == character;
        return (
            DescriptionSimilarity(query, source),
            PrimaryEffectSimilarity(query.PrimaryEffect, source.PrimaryEffect),
            sameColor && sameType && sameCost ? 1 : 0,
            sameColor && sameType ? 1 : 0,
            sameType && sameCost ? 1 : 0,
            sameType ? 1 : 0);
    }

    private static (int Description, int PrimaryEffect, int SameColorTypeCost, int SameColorType,
        int SameTypeCost, int SameType) AggregateArtRank(
        (int Description, int PrimaryEffect, int SameColorTypeCost, int SameColorType,
            int SameTypeCost, int SameType) first,
        (int Description, int PrimaryEffect, int SameColorTypeCost, int SameColorType,
            int SameTypeCost, int SameType) second) =>
        (first.Description + second.Description,
            first.PrimaryEffect + second.PrimaryEffect,
            first.SameColorTypeCost + second.SameColorTypeCost,
            first.SameColorType + second.SameColorType,
            first.SameTypeCost + second.SameTypeCost,
            first.SameType + second.SameType);

    private static int DescriptionSimilarity(ArtQuery query, ArtSource source)
    {
        // Exact normalized clauses dominate, while shared operation templates give a weaker whole-description
        // relationship when the wording or target shape differs. Dice similarity rewards matching the whole card
        // rather than a single incidental line on a much larger source card.
        return DiceSimilarity(query.DescriptionSchemas, source.DescriptionSchemas) * 1_000
            + DiceSimilarity(query.Templates, source.Templates);
    }

    private static int DiceSimilarity(IReadOnlySet<string> left, IReadOnlySet<string> right)
    {
        if (left.Count == 0 || right.Count == 0) return 0;
        var intersection = left.Count <= right.Count
            ? left.Count(right.Contains)
            : right.Count(left.Contains);
        return 200 * intersection / (left.Count + right.Count);
    }

    private static int PrimaryEffectSimilarity(GeneratorOperation? generated, ComponentAtom? source)
    {
        if (generated is null || source is null) return 0;
        if (CardNameGenerator.RelationSchema(generated) == CardNameGenerator.RelationSchema(source)) return 3;
        if (generated.Template == source.Template) return 2;
        var generatedFamily = CardEffectRules.EffectFamily(generated);
        var sourceFamily = CardEffectRules.EffectFamily(source);
        return generatedFamily.Length > 0 && generatedFamily == sourceFamily ? 1 : 0;
    }

    private static GeneratorOperation? PrimaryEffect(IEnumerable<GeneratorOperation> operations) => operations
        .Where(operation => CardEffectRules.IsBeneficialEffect(operation)
            && operation.Scope is not (OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger)
            && !CardEffectRules.IsDependencyPrefix(operation))
        .MaxBy(EffectBalanceModel.EstimatedEffectValue);

    private static ComponentAtom? PrimaryEffect(IEnumerable<ComponentAtom> atoms) => atoms
        .Where(atom => CardEffectRules.IsBeneficialEffect(atom)
            && atom.Scope is not (OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger)
            && !CardEffectRules.IsDependencyPrefix(atom))
        .MaxBy(EffectBalanceModel.EstimatedEffectValue);

    private static GeneratedCardType SourceType(CardModel card) => card.Type switch
    {
        CardType.Attack => GeneratedCardType.Attack,
        CardType.Power => GeneratedCardType.Power,
        _ => GeneratedCardType.Skill
    };

    private static bool SamePrintedCost(GeneratedCard generated, CardModel source)
    {
        if ((generated.Cost < 0) != source.EnergyCost.CostsX) return false;
        if (generated.Cost >= 0 && generated.Cost != source.EnergyCost.Canonical) return false;
        if (generated.HasStarCostX != source.HasStarCostX) return false;
        if (!generated.HasStarCostX && generated.StarCost != source.CanonicalStarCost) return false;
        return true;
    }

    private static string PortraitPath(ArtSource source) =>
        $"res://images/atlases/card_atlas.sprites/{source.Owner.ToString().ToLowerInvariant()}/{source.Card.Id.Entry.ToLowerInvariant()}.tres";

    internal static void AuditPortraitSelections(GeneratedCharacter character,
        IReadOnlyList<ChaosCardDefinition> definitions)
    {
        var sources = OriginalArtSources();
        var randomPortraits = definitions.Any(definition => definition.PortraitVariantId is not null);
        var normal = sources.Where(source => source.Card.Rarity != CardRarity.Ancient).ToArray();
        var ownAncient = sources.Where(source => source.Owner == character
                && source.Card.Rarity == CardRarity.Ancient)
            .ToArray();
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in definitions.Where(definition =>
                     definition.Card.Rarity != GeneratedRarity.Ancient).OrderBy(definition => definition.Slot))
        {
            // Multiplayer peers and reloaded saves need not have the host's optional portrait pack. Audit the
            // stable semantic source and saved variant identity; rendering falls back to the original when the
            // provider itself is absent.
            var source = normal.FirstOrDefault(source => PortraitPath(source) == definition.PortraitPath)
                ?? throw new InvalidOperationException(
                    $"{character} slot {definition.Slot} uses an unknown or Ancient-sized portrait source.");
            var selected = new ArtCandidate(source, definition.PortraitPath,
                definition.PortraitVariantId, definition.PortraitVariantPath);
            if (used.Contains(ArtCandidateKey(selected)))
                throw new InvalidOperationException($"{character} repeats an exact portrait variant.");
            var query = CreateArtQuery(definition.Card);
            var bestRank = normal.GroupBy(candidate => ArtRank(character, definition.Card, query, candidate))
                .OrderByDescending(group => group.Key)
                .First(group => ExpandArtCandidates(group, randomPortraits)
                    .Any(candidate => !used.Contains(ArtCandidateKey(candidate)))).Key;
            if (ArtRank(character, definition.Card, query, selected.Source) != bestRank)
                throw new InvalidOperationException(
                    $"{character} slot {definition.Slot} did not use its highest-priority available portrait.");
            AuditPortraitSource(character, definition, selected.Source);
            used.Add(ArtCandidateKey(selected));
        }

        var ancientDefinitions = definitions.Where(definition =>
                definition.Card.Rarity == GeneratedRarity.Ancient)
            .OrderBy(definition => definition.Slot).ToArray();
        if (ancientDefinitions.Length == 0) return;
        if (ancientDefinitions.Length != AncientCount || ownAncient.Length != AncientCount)
            throw new InvalidOperationException($"{character} Ancient portrait pair is incomplete.");
        var selectedAncient = ancientDefinitions.Select(definition => ownAncient.FirstOrDefault(source =>
                PortraitPath(source) == definition.PortraitPath)
            ?? throw new InvalidOperationException(
                $"{character} Ancient slot {definition.Slot} uses another color or a non-Ancient portrait."))
            .ToArray();
        if (selectedAncient[0] == selectedAncient[1]
            || !used.Add(DefinitionPortraitKey(ancientDefinitions[0]))
            || !used.Add(DefinitionPortraitKey(ancientDefinitions[1])))
            throw new InvalidOperationException($"{character} repeats an Ancient portrait.");
        AuditPortraitSource(character, ancientDefinitions[0], selectedAncient[0]);
        AuditPortraitSource(character, ancientDefinitions[1], selectedAncient[1]);

        var first = ancientDefinitions[0].Card;
        var second = ancientDefinitions[1].Card;
        var firstQuery = CreateArtQuery(first);
        var secondQuery = CreateArtQuery(second);
        var actual = AggregateArtRank(
            ArtRank(character, first, firstQuery, selectedAncient[0]),
            ArtRank(character, second, secondQuery, selectedAncient[1]));
        var swapped = AggregateArtRank(
            ArtRank(character, first, firstQuery, selectedAncient[1]),
            ArtRank(character, second, secondQuery, selectedAncient[0]));
        if (actual.CompareTo(swapped) < 0)
            throw new InvalidOperationException($"{character} Ancient portrait pair used the less similar mapping.");
    }

    private static string DefinitionPortraitKey(ChaosCardDefinition definition) => definition.PortraitPath
        + "|" + (definition.PortraitVariantId ?? "$winning")
        + "|" + (definition.PortraitVariantPath ?? string.Empty);

    internal static void AuditPortraitSourceCompatibility()
    {
        // Exercise the exact recovery route used when another mod has emptied/replaced every visible character
        // pool. This is intentionally independent of the cached normal catalog, so startup proves that all five
        // Ancient pairs and enough normal portraits can be rebuilt from native type IDs alone.
        var recovered = ResolveOriginalArtSources(_ => Array.Empty<CardModel>());
        var recoveredNormal = recovered.Count(source => source.Card.Rarity != CardRarity.Ancient);
        var incompleteAncients = SupportedPools.Where(owner => owner != GeneratedCharacter.Colorless)
            .Where(owner => recovered.Count(source => source.Owner == owner
                && source.Card.Rarity == CardRarity.Ancient) != AncientCount)
            .ToArray();
        if (recoveredNormal == 0 || incompleteAncients.Length > 0)
            throw new InvalidOperationException("Native portrait fallback audit failed: "
                + $"normal={recoveredNormal}, incompleteAncients={string.Join(',', incompleteAncients)}.");

        var definitions = SupportedPools.SelectMany(GetCards).ToArray();
        var redirectedPaths = 0;
        var directTextureProbe = false;
        foreach (var definition in definitions)
        {
            if (!ChaosPortraitCompatibility.TryResolveSource(definition, out var source)
                || !string.IsNullOrWhiteSpace(definition.PortraitSourceId)
                && !string.Equals(source.Id.ToString(), definition.PortraitSourceId,
                    StringComparison.OrdinalIgnoreCase)
                || source.GetType().Assembly != typeof(CardModel).Assembly)
                throw new InvalidOperationException(
                    $"{definition.Card.Character} slot {definition.Slot} has an unresolved portrait source.");
            if (!string.Equals(ChaosPortraitCompatibility.ResolvePath(definition), definition.PortraitPath,
                    StringComparison.Ordinal))
                redirectedPaths++;
            if (!directTextureProbe
                && ChaosPortraitCompatibility.TryResolveDirectTexture(definition, out _))
                directTextureProbe = true;

            var legacy = definition with { PortraitSourceId = null };
            if (!ChaosPortraitCompatibility.TryResolveSource(legacy, out var legacySource)
                || legacySource.Id != source.Id)
                throw new InvalidOperationException(
                    $"{definition.Card.Character} slot {definition.Slot} cannot recover its legacy portrait source.");
        }
        var unsafeLegacy = definitions[0] with
        {
            PortraitSourceId = null,
            PortraitPath = "res://InactivePortraitMod/unused_variant.png"
        };
        if (ChaosPortraitCompatibility.TryResolveSource(unsafeLegacy, out _))
            throw new InvalidOperationException("A non-vanilla legacy portrait path resolved to a card source.");
        Log.Info($"[AutoAnthony] Portrait-source compatibility and empty-pool recovery audit passed for "
            + $"{definitions.Length} cards; recoveredNative={recovered.Length}; "
            + $"{redirectedPaths} paths are currently redirected by loaded card-art mods; "
            + $"directTextureProbe={directTextureProbe}.");
    }

    private static void AuditPortraitSource(GeneratedCharacter character, ChaosCardDefinition definition,
        ArtSource selected)
    {
        if (!string.Equals(definition.PortraitSourceId, selected.Card.Id.ToString(),
                StringComparison.OrdinalIgnoreCase)
            || !ChaosPortraitCompatibility.TryResolveSource(definition, out var resolved)
            || resolved.Id != selected.Card.Id)
            throw new InvalidOperationException(
                $"{character} slot {definition.Slot} lost its original-card portrait source identity.");

        // Older snapshots have only PortraitPath. Their deterministic atlas filename must still recover the same
        // source card so existing runs also inherit installed portrait mods.
        var legacy = definition with { PortraitSourceId = null };
        if (!ChaosPortraitCompatibility.TryResolveSource(legacy, out var legacyResolved)
            || legacyResolved.Id != selected.Card.Id)
            throw new InvalidOperationException(
                $"{character} slot {definition.Slot} could not recover its portrait source from a legacy path.");
    }

    private static void SetDefinitions(
        GeneratedCharacter character,
        string seed,
        IReadOnlyList<ChaosCardDefinition> definitions,
        bool ultimateChaos,
        bool balancedValues,
        bool randomizeNumericValues,
        bool replaceStartingCards,
        bool preserveOriginalCards)
    {
        Definitions[character] = definitions;
        Seeds[character] = seed;
        DefinitionUltimateModes[character] = ultimateChaos;
        DefinitionBalancedValueModes[character] = balancedValues;
        DefinitionNumericRandomModes[character] = randomizeNumericValues;
        DefinitionReplaceStartingCardsModes[character] = replaceStartingCards;
        DefinitionPreserveOriginalCardsModes[character] = preserveOriginalCards;
        ResetCanonicalCardCaches(character);
        InstallLocalizedTitles(character, definitions);
    }

    private static void InstallLocalizedTitles(GeneratedCharacter character,
        IReadOnlyList<ChaosCardDefinition> definitions)
    {
        if (LocManager.Instance is not { } localization) return;
        var chinese = localization.Language is "zhs" or "zht";
        var cardTypes = ChaosCardRegistry.TypesFor(character);
        var titles = definitions.ToDictionary(
            definition => ModelDb.GetId(cardTypes[definition.Slot]).Entry + ".title",
            definition => chinese ? definition.Card.Name!.Chinese : definition.Card.Name!.English,
            StringComparer.Ordinal);
        ChaosRuntimeDescriptionCache.InstallAllIfChanged(localization.GetTable("cards"), titles);
    }

    private static GeneratedCharacter[] NormalizeCharacters(IEnumerable<GeneratedCharacter> characters) =>
        characters.Where(character => character != GeneratedCharacter.Colorless && SupportedPools.Contains(character))
            .Distinct().OrderBy(character => character).ToArray();

    private static string NormalizeSeed(IReadOnlyList<GeneratedCharacter> characters, string seed) =>
        string.IsNullOrWhiteSpace(seed)
            ? $"{string.Join('_', characters.Select(character => character.ToString().ToUpperInvariant()))}_CHAOS_EMPTY_SEED"
            : seed;

    private static void SetActiveCharacters(IEnumerable<GeneratedCharacter> characters)
    {
        ActiveCharacterSet.Clear();
        ActiveCharacterSet.UnionWith(characters);
        _runActive = ActiveCharacterSet.Count > 0;
    }

    private static void EnsureStartingDeckCoverage(GeneratedCard[] cards, RandomCardGenerator generator,
        PoolGenerationPolicy policy)
    {
        if (cards.Length < policy.StartingDeckSize)
            throw new InvalidOperationException("The generated pool does not contain a ten-card starting deck.");

        RepairCoverage(cards, generator, CountsAsStartingDamage, CountsAsStartingDefense,
            policy.MinimumStartingDamage, policy.MinimumStartingDefense, policy.StartingDeckSize,
            policy.ReplacementAttemptLimit, "damage");
        RepairCoverage(cards, generator, CountsAsStartingDefense, CountsAsStartingDamage,
            policy.MinimumStartingDefense, policy.MinimumStartingDamage, policy.StartingDeckSize,
            policy.ReplacementAttemptLimit, "defense");

        var damage = cards.Take(policy.StartingDeckSize).Count(CountsAsStartingDamage);
        var defense = cards.Take(policy.StartingDeckSize).Count(CountsAsStartingDefense);
        if (damage < policy.MinimumStartingDamage || defense < policy.MinimumStartingDefense)
            throw new InvalidOperationException($"Starting deck coverage repair failed: damage={damage}, defense={defense}.");
    }

    private static void RepairCoverage(
        GeneratedCard[] cards,
        RandomCardGenerator generator,
        Func<GeneratedCard, bool> required,
        Func<GeneratedCard, bool> protectedCoverage,
        int minimum,
        int protectedMinimum,
        int deckSize,
        int attemptLimit,
        string label)
    {
        while (cards.Take(deckSize).Count(required) < minimum)
        {
            _ = attemptLimit; // Matching generation owns one bounded, non-committing retry loop.
            GeneratedCard replacement;
            try
            {
                replacement = generator.GenerateMatching(GeneratedRarity.Basic, required);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    $"Could not generate a Basic {label} card for the starting deck.", exception);
            }

            var protectedCount = cards.Take(deckSize).Count(protectedCoverage);
            var slot = Enumerable.Range(0, deckSize).FirstOrDefault(index =>
                !required(cards[index])
                && (!protectedCoverage(cards[index]) || protectedCoverage(replacement)
                    || protectedCount > protectedMinimum), -1);
            if (slot < 0)
                throw new InvalidOperationException($"No safe starting-deck slot was available for {label} coverage repair.");
            cards[slot] = replacement;
        }
    }

    private static void EnsureXCostDistribution(GeneratedCard[] cards, IReadOnlyList<GeneratedRarity> rarities,
        RandomCardGenerator generator, Random random, PoolGenerationPolicy policy)
    {
        var basicCount = cards.TakeWhile(card => card.Rarity == GeneratedRarity.Basic).Count();

        // Ordinary native X cards keep their original generator path, but no run pool may be flooded by them.
        var ordinary = Enumerable.Range(0, cards.Length)
            .Where(index => SpecialXCardConverter.IsOrdinaryX(cards[index]))
            // Preserve the already-repaired ten-card combat coverage whenever a non-starting slot can satisfy the
            // ordinary-X cap. Basic X cards remain replaceable as the bounded fallback, so the quota is unchanged.
            .OrderBy(index => index < basicCount ? 1 : 0)
            .ThenBy(_ => random.Next()).ToList();
        foreach (var index in ordinary.Skip(policy.OrdinaryXMaximum))
            cards[index] = GenerateNonX(rarities[index], generator, policy.ReplacementAttemptLimit);

        if (!policy.EnforceSpecialXMinimum) return;
        var ordinaryCount = cards.Count(SpecialXCardConverter.IsOrdinaryX);
        var targetSpecial = policy.RollSpecialXTarget(random, ordinaryCount);

        var special = Enumerable.Range(0, cards.Length)
            .Where(index => SpecialXCardConverter.IsSpecial(cards[index]))
            .OrderBy(index => index < basicCount ? 0 : 1)
            .ThenBy(_ => random.Next())
            .ToList();
        foreach (var index in special.Skip(targetSpecial))
            cards[index] = GenerateNonX(rarities[index], generator, policy.ReplacementAttemptLimit);

        var missing = targetSpecial - cards.Count(SpecialXCardConverter.IsSpecial);
        if (missing > 0)
        {
            var replacementSlots = Enumerable.Range(basicCount, cards.Length - basicCount)
                .Where(index => !SpecialXCardConverter.IsSpecial(cards[index])
                    && !SpecialXCardConverter.IsOrdinaryX(cards[index])
                    && rarities[index] != GeneratedRarity.Ancient)
                .OrderBy(_ => random.Next())
                .Take(missing)
                .ToArray();
            if (replacementSlots.Length != missing)
                throw new InvalidOperationException("The generated pool has too few non-Basic slots for special X cards.");
            foreach (var index in replacementSlots)
                cards[index] = generator.GenerateSpecialX(rarities[index]);
        }

        ordinaryCount = cards.Count(SpecialXCardConverter.IsOrdinaryX);
        var specialCount = cards.Count(SpecialXCardConverter.IsSpecial);
        if (!policy.IsValidXDistribution(ordinaryCount, specialCount))
            throw new InvalidOperationException(
                $"X-cost pool quota failed: ordinary={ordinaryCount}, special={specialCount}.");
    }

    private static GeneratedCard GenerateNonX(GeneratedRarity rarity, RandomCardGenerator generator,
        int attemptLimit)
    {
        _ = attemptLimit; // Matching generation owns one bounded, non-committing retry loop.
        return generator.GenerateWithoutSpecialXMatching(rarity,
            replacement => !SpecialXCardConverter.IsOrdinaryX(replacement));
    }

    internal static bool CountsAsStartingDamage(GeneratedCard card) =>
        StartingPoolConstraintResolver.CountsAsDamage(card);

    internal static bool CountsAsStartingDefense(GeneratedCard card) =>
        StartingPoolConstraintResolver.CountsAsDefense(card);

    public static void DeactivateRun()
    {
        lock (Gate)
        {
            ActiveCharacterSet.Clear();
            _runActive = false;
            _ancientFuelActive = false;
            _activeUltimateChaos = false;
            _activeNumericBalanceOptimization = true;
            _activeReplaceStartingCards = true;
            _activeNumericRandomMode = false;
            _activePreserveOriginalCards = false;
            _activeRandomCardArt = false;
        }
        ChaosPoolSnapshot.ClearRunPayloadCache();
    }
    public static int CountFor(GeneratedCharacter character) => character switch
    {
        GeneratedCharacter.Silent => SilentTotalCount,
        GeneratedCharacter.Colorless => ColorlessCount,
        _ => TotalCount
    };
    internal static int GenerationCountFor(GeneratedCharacter character) =>
        CountFor(character) - (_activePreserveOriginalCards && character != GeneratedCharacter.Colorless
            ? AncientCount : 0);
    public static int BasicCountFor(GeneratedCharacter character) => character switch
    {
        GeneratedCharacter.Silent => SilentBasicCount,
        GeneratedCharacter.Colorless => 0,
        _ => BasicCount
    };

    public static IReadOnlyList<ChaosCardDefinition> GetCards(GeneratedCharacter character)
    {
        var desiredMode = IsRunActive ? ActiveUltimateChaos : false;
        var desiredBalancedValues = IsRunActive ? ActiveNumericBalanceOptimization : true;
        var desiredNumericRandomMode = IsRunActive && ActiveNumericRandomMode;
        if (!Definitions.TryGetValue(character, out var cards)
            || DefinitionUltimateModes.GetValueOrDefault(character) != desiredMode
            || DefinitionBalancedValueModes.GetValueOrDefault(character) != desiredBalancedValues
            || DefinitionNumericRandomModes.GetValueOrDefault(character) != desiredNumericRandomMode)
        {
            lock (Gate)
            {
                CaptureOriginalColorlessCards();
                var seed = IsRunActive
                    ? ActiveSeed
                    : $"{character.ToString().ToUpperInvariant()}_CHAOS_LIBRARY_PREVIEW";
                Build(character, seed, desiredMode, desiredBalancedValues, desiredNumericRandomMode);
            }
            cards = Definitions[character];
        }
        return cards;
    }

    public static IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> GetAllCards() =>
        SupportedPools.ToDictionary(character => character, GetCards);

    internal static bool UsesNumericRandomValues(GeneratedCharacter character)
    {
        lock (Gate)
            return DefinitionNumericRandomModes.TryGetValue(character, out var enabled)
                ? enabled
                : ActiveNumericRandomMode;
    }

    public static ChaosCardDefinition ForSlot(int slot) => ForSlot(GeneratedCharacter.Ironclad, slot);

    public static ChaosCardDefinition ForSlot(GeneratedCharacter character, int slot)
    {
        var cards = GetCards(character);
        if ((uint)slot >= (uint)cards.Count) throw new ArgumentOutOfRangeException(nameof(slot));
        return cards[slot];
    }

    public static IReadOnlyList<ChaosCardDefinition> AncientCards => GetCards(GeneratedCharacter.Ironclad).TakeLast(AncientCount).ToArray();

    internal static bool ShouldUseAncientFuel(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"AutoAnthony/v111/easter/ancient-fuel-v1/{seed}"));
        return BitConverter.ToUInt32(hash, 0) % 10u == 0u;
    }

    internal static string ReplaceFuelDisplay(string text, bool chinese)
    {
        if (!AncientFuelActive) return text;
        if (chinese)
            return text.Contains("先古燃料", StringComparison.Ordinal)
                ? text
                : text.Replace("燃料", "先古燃料", StringComparison.Ordinal);
        return text.Contains("Ancient Fuel", StringComparison.OrdinalIgnoreCase)
            ? text
            : text.Replace("Fuel", "Ancient Fuel", StringComparison.OrdinalIgnoreCase);
    }

    private static int StableSeed(GeneratedCharacter character, string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"AutoAnthony/v111/all-pools-v2/{character}/{seed}"));
        return BitConverter.ToInt32(hash, 0) & int.MaxValue;
    }

    private static void ResetCanonicalCardCaches(GeneratedCharacter character)
    {
        var energy = AccessTools.Field(typeof(CardModel), "_energyCost");
        var dynamicVars = AccessTools.Field(typeof(CardModel), "_dynamicVars");
        var keywords = AccessTools.Field(typeof(CardModel), "_keywords");
        var tags = AccessTools.Field(typeof(CardModel), "_tags");
        foreach (var type in ChaosCardRegistry.TypesFor(character))
        {
            var canonical = ModelDb.GetById<CardModel>(ModelDb.GetId(type));
            energy.SetValue(canonical, null);
            dynamicVars.SetValue(canonical, null);
            keywords.SetValue(canonical, null);
            tags.SetValue(canonical, null);
        }
    }

    private static IEnumerable<CardModel> OriginalPool(GeneratedCharacter character) => character switch
    {
        GeneratedCharacter.Ironclad => ModelDb.CardPool<IroncladCardPool>().AllCards,
        GeneratedCharacter.Silent => ModelDb.CardPool<SilentCardPool>().AllCards,
        GeneratedCharacter.Defect => ModelDb.CardPool<DefectCardPool>().AllCards,
        GeneratedCharacter.Necrobinder => ModelDb.CardPool<NecrobinderCardPool>().AllCards,
        GeneratedCharacter.Regent => ModelDb.CardPool<RegentCardPool>().AllCards,
        GeneratedCharacter.Colorless => _originalColorlessCards
            ?? throw new InvalidOperationException("The original v111 Colorless card pool was not captured."),
        _ => throw new ArgumentOutOfRangeException(nameof(character))
    };

    internal static IReadOnlyList<CardModel> OriginalCardsForPool(GeneratedCharacter character)
    {
        CaptureOriginalColorlessCards();
        return OriginalPool(character)
            .Where(card => card.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly)
            // Fasten is deliberately absent from the random Colorless catalog because its effect is hard-wired to
            // the vanilla Defend family. Keep that generator/decomposition contract separate from the complete
            // original pool used by the opt-in preserve-original setting below.
            .Where(card => character != GeneratedCharacter.Colorless || card is not Fasten)
            .ToArray();
    }

    /// <summary>
    /// Complete vanilla pool used by the additive "preserve original cards" policy. Unlike the component catalog,
    /// this must retain MultiplayerOnly cards (and Fasten): CardPoolModel.GetUnlockedCards applies the actual run's
    /// multiplayer constraint later, while the card library requests None so its multiplayer-card toggle can show
    /// those cards as normally unlocked.
    /// </summary>
    internal static IReadOnlyList<CardModel> OriginalCardsForPreservedPool(GeneratedCharacter character)
    {
        CaptureOriginalColorlessCards();
        return OriginalPool(character).ToArray();
    }

    private static void CaptureOriginalColorlessCards()
    {
        if (_originalColorlessCards is not null) return;
        _originalColorlessCards = ModelDb.CardPool<ColorlessCardPool>().AllCards
            .ToArray();
        const int vanillaColorlessCount = 65;
        if (_originalColorlessCards.Length != vanillaColorlessCount)
            throw new InvalidOperationException(
                $"Expected {vanillaColorlessCount} complete v111 Colorless cards, found {_originalColorlessCards.Length}.");
    }
}
