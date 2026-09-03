using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChaosCardGenerator;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace AutoAnthony;

public sealed class ChaosPoolSnapshotModifier : ModifierModel
{
    // Registering this property with ModelIdSerializationCache makes the manually attached snapshot marker
    // legal in both JSON saves and multiplayer packets.
    [SavedProperty]
    public string PoolSnapshot { get; set; } = string.Empty;

    [SavedProperty]
    public string SurpriseKnowledge { get; set; } = string.Empty;

    // A transient copy of this modifier is also used as a versioned lobby configuration carrier. The host
    // appends it to the vanilla begin-run message and every peer removes it before RunState is constructed.
    // This keeps local settings from silently producing different generated pools in the same lobby.
    [SavedProperty]
    public bool MultiplayerGenerationModeSpecified { get; set; }

    [SavedProperty]
    public bool MultiplayerModEnabled { get; set; }

    [SavedProperty]
    public bool MultiplayerUltimateChaos { get; set; }

    [SavedProperty]
    public bool MultiplayerReplaceStartingCardsSpecified { get; set; }

    [SavedProperty]
    public bool MultiplayerReplaceStartingCards { get; set; }

    [SavedProperty]
    public bool MultiplayerNumericBalanceOptimizationSpecified { get; set; }

    [SavedProperty]
    public bool MultiplayerNumericBalanceOptimization { get; set; }

    [SavedProperty]
    public bool MultiplayerNumericRandomMode { get; set; }

    [SavedProperty]
    public bool MultiplayerPreserveOriginalCards { get; set; }

    [SavedProperty]
    public bool MultiplayerRandomCardArtSpecified { get; set; }

    [SavedProperty]
    public bool MultiplayerRandomCardArt { get; set; }

    // New-run lobbies carry only this compact gameplay checksum. Live saves still persist the complete pool
    // snapshot, but sending that snapshot through LobbyBeginRunMessage became unsafe once structured runtime
    // specifications increased it beyond 150 KB. Every peer deterministically builds the same six pools and
    // verifies this checksum before entering the run.
    [SavedProperty]
    public string MultiplayerGenerationFingerprint { get; set; } = string.Empty;

    // The property-name registry used by multiplayer SavedProperties is populated from [SavedProperty] members.
    // Ghost Seed writes this marker onto selected SerializableCards (rather than this modifier) so an otherwise
    // non-serializable local keyword survives autosaves, reloads, and peer card transfer.
    [SavedProperty]
    public bool GhostSeedEthereal { get; set; }

    public override bool ShouldReceiveCombatHooks => false;
}

public sealed record ChaosPoolRestoreReport(string SavedVersion, bool VersionChanged,
    int RestoredCards, int RegeneratedCards, string? Failure = null);

internal sealed record ChaosHistorySnapshotRestore(
    string SavedVersion,
    bool AncientFuel,
    bool UltimateChaos,
    bool ReplaceStartingCards,
    bool NumericBalanceOptimization,
    bool NumericRandomMode,
    bool PreserveOriginalCards,
    IReadOnlyDictionary<GeneratedCharacter, IReadOnlyDictionary<int, ChaosCardDefinition>> Cards,
    int RejectedCards);

public static class ChaosPoolSnapshot
{
    public const string ModVersion = "0.3.16";
    private const int SchemaVersion = 10;
    // Schema 1-4 predate the stable all-pool/run-mode layout. They remain readable for historical card display,
    // but resuming one as a live run now regenerates the pool instead of retaining increasingly fragile gameplay
    // migration branches. Schema 5 is the oldest live-save contract maintained by current releases.
    private const int MinimumLiveSchemaVersion = 5;
    private const string PayloadProperty = "PoolSnapshot";
    private const string SurpriseKnowledgeProperty = "SurpriseKnowledge";
    private const string Prefix = "AA1:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record PoolEnvelope(GeneratedCharacter Character, IReadOnlyList<ChaosCardDefinition> Cards);
    private sealed record Envelope(int Schema, string ModVersion, IReadOnlyList<GeneratedCharacter> ActiveCharacters,
        string Seed, bool AncientFuel, bool UltimateChaos, bool ReplaceStartingCards,
        bool NumericBalanceOptimization,
        bool NumericRandomMode,
        bool PreserveOriginalCards,
        IReadOnlyList<PoolEnvelope> Pools);
    private sealed record CachedRunPayload(IReadOnlyList<GeneratedCharacter> ActiveCharacters, string Seed,
        bool AncientFuel, bool UltimateChaos, bool ReplaceStartingCards,
        bool NumericBalanceOptimization,
        bool NumericRandomMode,
        bool PreserveOriginalCards,
        IReadOnlyList<IReadOnlyList<ChaosCardDefinition>> PoolReferences, string Payload);

    private static readonly object PayloadCacheGate = new();
    private static readonly Lazy<IReadOnlySet<string>> SupportedTemplates = new(BuildSupportedTemplates,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static CachedRunPayload? _cachedRunPayload;

    public static SerializableModifier ToSerializableModifier(GeneratedCharacter activeCharacter, string seed,
        IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> pools) =>
        ToSerializableModifier([activeCharacter], seed, pools);

    public static SerializableModifier ToSerializableModifier(IReadOnlyCollection<GeneratedCharacter> activeCharacters, string seed,
        IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> pools)
    {
        var normalizedCharacters = NormalizeCharacters(activeCharacters);
        var ordered = ChaosRunDefinitions.SupportedPools
            .Select(character => CreatePoolEnvelope(character, pools[character])).ToArray();
        var payload = Encode(new Envelope(SchemaVersion, ModVersion, normalizedCharacters, seed,
            ChaosRunDefinitions.AncientFuelActive, ChaosRunDefinitions.ActiveUltimateChaos,
            ChaosRunDefinitions.ActiveReplaceStartingCards,
            ChaosRunDefinitions.ActiveNumericBalanceOptimization, ChaosRunDefinitions.ActiveNumericRandomMode,
            ChaosRunDefinitions.ActivePreserveOriginalCards, ordered));
        return CreateSerializableModifier(payload);
    }

    /// <summary>
    /// New multiplayer runs use the host's immutable all-pool payload as their authoritative card database.
    /// The lobby player list can still contain RandomCharacter at this point, so recipients rebind only the
    /// active-character metadata after vanilla resolves those choices; card definitions and run-mode flags stay
    /// byte-for-byte host-authored.
    /// </summary>
    internal static string GetAuthoritativeMultiplayerPayload(
        IReadOnlyCollection<GeneratedCharacter> provisionalActiveCharacters,
        string seed,
        IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> pools) =>
        GetOrCreateRunPayload(provisionalActiveCharacters, seed, pools, out _);

    internal static string RebindMultiplayerActiveCharacters(string payload,
        IReadOnlyCollection<GeneratedCharacter> activeCharacters, string seed)
    {
        using var document = Decode(payload);
        var root = document.RootElement;
        if (root.GetProperty("schema").GetInt32() != SchemaVersion)
            throw new InvalidDataException("The host sent an unsupported multiplayer pool snapshot schema.");
        if (!string.Equals(root.GetProperty("seed").GetString(), seed, StringComparison.Ordinal))
            throw new InvalidDataException("The host multiplayer pool snapshot uses a different run seed.");
        var envelope = root.Deserialize<Envelope>(JsonOptions)
            ?? throw new JsonException("The host multiplayer pool snapshot was empty.");
        return Encode(envelope with { ActiveCharacters = NormalizeCharacters(activeCharacters) });
    }

    internal static string MultiplayerFingerprint(string payload)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash)[..16];
    }

    /// <summary>
    /// Stable gameplay-only identity used by the lightweight multiplayer start protocol. Presentation fields
    /// (localized text, names, portraits and VFX) are intentionally excluded so cosmetic card-art differences
    /// cannot prevent otherwise compatible peers from starting a run.
    /// </summary>
    internal static string MultiplayerGameplayFingerprint(
        IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> pools)
    {
        var builder = new StringBuilder(256 * 1024);
        foreach (var character in ChaosRunDefinitions.SupportedPools)
        {
            builder.Append((int)character).Append(':');
            foreach (var definition in pools[character].OrderBy(definition => definition.Slot))
            {
                var card = definition.Card;
                builder.Append(definition.Slot).Append('|')
                    .Append(card.Cost).Append('|').Append(card.StarCost).Append('|')
                    .Append(card.HasStarCostX ? '1' : '0').Append('|')
                    .Append((int)card.Type).Append('|').Append((int)card.Target).Append('|')
                    .Append((int)card.Rarity).Append('|').Append(card.UnifiedChaos ? '1' : '0').Append('|');
                foreach (var tag in card.Tags.OrderBy(tag => tag)) builder.Append((int)tag).Append(',');
                builder.Append('|');
                foreach (var keyword in (card.CustomKeywords ?? []).OrderBy(id => id, StringComparer.Ordinal))
                    AppendFingerprintToken(builder, keyword);
                builder.Append('|');
                for (var operationIndex = 0; operationIndex < card.Operations.Count; operationIndex++)
                {
                    var operation = card.Operations[operationIndex];
                    AppendFingerprintToken(builder, operation.Template);
                    builder.Append((int)operation.Scope).Append('|')
                        .Append(operation.RequiresSingleTarget ? '1' : '0').Append('|');
                    AppendFingerprintToken(builder, operation.CardTargetSlot);
                    AppendFingerprintToken(builder, operation.DerivativeId);
                    AppendFingerprintToken(builder, operation.DerivativeEnchantmentId);
                    AppendFingerprintToken(builder, operation.OrbSourceId);
                    AppendFingerprintToken(builder, operation.OrbOutputId);
                    builder.Append(operation.DerivativeEnchantmentAmount?.ToString() ?? "-").Append('|');
                    foreach (var parameter in operation.Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        AppendFingerprintToken(builder, parameter.Key);
                        builder.Append(parameter.Value).Append(',');
                    }
                    builder.Append('|');
                    var spec = definition.RuntimeSpecs is { Count: > 0 }
                               && operationIndex < definition.RuntimeSpecs.Count
                        ? definition.RuntimeSpecs[operationIndex]
                        : OperationRuntimeSpecCompiler.RequireStructured(operation);
                    AppendFingerprintToken(builder, spec.StableSignature());
                    builder.Append(';');
                }
                builder.Append('|');
                if (card.Upgrade is { } upgrade)
                {
                    builder.Append(upgrade.UpgradedCost).Append('|')
                        .Append(upgrade.UpgradedStarCost?.ToString() ?? "-").Append('|');
                    foreach (var tag in upgrade.AddedKeywords.OrderBy(tag => tag))
                        builder.Append('+').Append((int)tag).Append(',');
                    foreach (var tag in (upgrade.RemovedKeywords ?? []).OrderBy(tag => tag))
                        builder.Append('-').Append((int)tag).Append(',');
                    foreach (var keyword in (upgrade.AddedCustomKeywords ?? []).OrderBy(id => id, StringComparer.Ordinal))
                    {
                        builder.Append('+');
                        AppendFingerprintToken(builder, keyword);
                    }
                    foreach (var keyword in (upgrade.RemovedCustomKeywords ?? []).OrderBy(id => id, StringComparer.Ordinal))
                    {
                        builder.Append('-');
                        AppendFingerprintToken(builder, keyword);
                    }
                    builder.Append('|');
                    for (var effectIndex = 0; effectIndex < upgrade.Effects.Count; effectIndex++)
                    {
                        var effect = upgrade.Effects[effectIndex];
                        builder.Append((int)effect.Kind).Append(',')
                            .Append(effect.OperationIndex?.ToString() ?? "-").Append(',')
                            .Append(effect.Delta?.ToString() ?? "-").Append(',');
                        AppendFingerprintToken(builder,
                            definition.UpgradeValueSlots is { Count: > 0 }
                            && effectIndex < definition.UpgradeValueSlots.Count
                                ? definition.UpgradeValueSlots[effectIndex]
                                : effect.ValueSlotId);
                        AppendFingerprintToken(builder, effect.KeywordId);
                        builder.Append(';');
                    }
                }
                builder.AppendLine();
            }
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash)[..16];
    }

    private static void AppendFingerprintToken(StringBuilder builder, string? value)
    {
        value ??= string.Empty;
        builder.Append(value.Length).Append('#').Append(value).Append('|');
    }

    /// <summary>
    /// Completed-run history only needs definitions for generated cards that remain in a player's final deck.
    /// Live saves deliberately continue to use <see cref="ToCachedSerializableModifier"/> and retain all six
    /// pools, because events, rewards, other-character generation and multiplayer reconnects can still reference
    /// any slot while the run is in progress.
    /// </summary>
    public static SerializableModifier? ToHistorySerializableModifier(
        IReadOnlyCollection<GeneratedCharacter> activeCharacters,
        string seed,
        IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> pools,
        IEnumerable<ModelId> finalDeckCardIds,
        out int savedCardCount)
    {
        var slotsByCharacter = new Dictionary<GeneratedCharacter, HashSet<int>>();
        foreach (var id in finalDeckCardIds)
        {
            if (!ChaosCardRegistry.TryGetGeneratedCardSlot(id, out var character, out var slot)) continue;
            if (!slotsByCharacter.TryGetValue(character, out var slots))
                slotsByCharacter[character] = slots = [];
            slots.Add(slot);
        }

        var ordered = ChaosRunDefinitions.SupportedPools
            .Where(slotsByCharacter.ContainsKey)
            .Select(character => CreatePoolEnvelope(character, pools[character]
                .Where(definition => slotsByCharacter[character].Contains(definition.Slot))
                .OrderBy(definition => definition.Slot)
                .ToArray()))
            .Where(pool => pool.Cards.Count > 0)
            .ToArray();
        savedCardCount = ordered.Sum(pool => pool.Cards.Count);
        if (savedCardCount == 0) return null;

        var payload = Encode(new Envelope(SchemaVersion, ModVersion, NormalizeCharacters(activeCharacters), seed,
            ChaosRunDefinitions.AncientFuelActive, ChaosRunDefinitions.ActiveUltimateChaos,
            ChaosRunDefinitions.ActiveReplaceStartingCards,
            ChaosRunDefinitions.ActiveNumericBalanceOptimization, ChaosRunDefinitions.ActiveNumericRandomMode,
            ChaosRunDefinitions.ActivePreserveOriginalCards, ordered));
        return CreateSerializableModifier(payload);
    }

    /// <summary>
    /// Rewrites an existing historical payload without generating or validating gameplay content. Definitions not
    /// referenced by the recorded final decks are discarded; accepted legacy definitions retain their exact card
    /// data and original producer version. A null compact modifier means the final decks contain no restorable
    /// generated cards and the historical marker can be removed entirely.
    /// </summary>
    internal static bool TryCompactHistorySnapshot(
        string? payload,
        IReadOnlyCollection<GeneratedCharacter> activeCharacters,
        string seed,
        IEnumerable<ModelId> finalDeckCardIds,
        out SerializableModifier? compactModifier,
        out int sourceCardCount,
        out int keptCardCount,
        out string? failure)
    {
        compactModifier = null;
        sourceCardCount = 0;
        keptCardCount = 0;
        if (!TryRestoreForHistory(payload, activeCharacters, seed, out var historical, out failure))
            return false;

        var requested = new Dictionary<GeneratedCharacter, HashSet<int>>();
        foreach (var id in finalDeckCardIds.Distinct())
        {
            if (!ChaosCardRegistry.TryGetGeneratedCardSlot(id, out var character, out var slot)) continue;
            if (!requested.TryGetValue(character, out var slots)) requested[character] = slots = [];
            slots.Add(slot);
        }

        sourceCardCount = historical.Cards.Values.Sum(cards => cards.Count);
        var compactPools = historical.Cards
            .OrderBy(pair => pair.Key)
            .Select(pair => CreatePoolEnvelope(pair.Key, pair.Value
                .Where(card => requested.TryGetValue(pair.Key, out var slots) && slots.Contains(card.Key))
                .OrderBy(card => card.Key)
                .Select(card => card.Value)
                .ToArray()))
            .Where(pool => pool.Cards.Count > 0)
            .ToArray();
        keptCardCount = compactPools.Sum(pool => pool.Cards.Count);
        if (sourceCardCount == keptCardCount && historical.RejectedCards == 0)
        {
            failure = null;
            return false;
        }

        if (keptCardCount > 0)
        {
            var compactPayload = Encode(new Envelope(SchemaVersion, historical.SavedVersion,
                NormalizeCharacters(activeCharacters), seed, historical.AncientFuel, historical.UltimateChaos,
                historical.ReplaceStartingCards, historical.NumericBalanceOptimization,
                historical.NumericRandomMode, historical.PreserveOriginalCards, compactPools));
            compactModifier = CreateSerializableModifier(compactPayload);
        }
        failure = null;
        return true;
    }

    public static void PrimeRunPayload(IReadOnlyCollection<GeneratedCharacter> activeCharacters, string seed,
        IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> pools)
    {
        var stopwatch = Stopwatch.StartNew();
        _ = GetOrCreateRunPayload(activeCharacters, seed, pools, out var rebuilt);
        stopwatch.Stop();
        if (rebuilt)
            Log.Info($"[AutoAnthony] Prepared immutable run-pool snapshot cache in {stopwatch.ElapsedMilliseconds} ms.");
    }

    public static SerializableModifier ToCachedSerializableModifier(
        IReadOnlyCollection<GeneratedCharacter> activeCharacters,
        string seed,
        IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> pools,
        string? surpriseKnowledge = null)
    {
        var payload = GetOrCreateRunPayload(activeCharacters, seed, pools, out var rebuilt);
        if (rebuilt)
            Log.Warn("[AutoAnthony] The run-pool snapshot cache was unavailable or stale during save; rebuilt it once.");
        return CreateSerializableModifier(payload, surpriseKnowledge);
    }

    public static void ClearRunPayloadCache()
    {
        lock (PayloadCacheGate) _cachedRunPayload = null;
    }

    internal static bool IsCachedRunPayload(IReadOnlyCollection<GeneratedCharacter> activeCharacters, string seed,
        IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> pools, string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return false;
        var normalizedCharacters = NormalizeCharacters(activeCharacters);
        var poolReferences = ChaosRunDefinitions.SupportedPools.Select(character => pools[character]).ToArray();
        lock (PayloadCacheGate)
        {
            return _cachedRunPayload is { } cached
                && cached.Seed == seed
                && cached.AncientFuel == ChaosRunDefinitions.AncientFuelActive
                && cached.UltimateChaos == ChaosRunDefinitions.ActiveUltimateChaos
                && cached.ReplaceStartingCards == ChaosRunDefinitions.ActiveReplaceStartingCards
                && cached.NumericBalanceOptimization == ChaosRunDefinitions.ActiveNumericBalanceOptimization
                && cached.NumericRandomMode == ChaosRunDefinitions.ActiveNumericRandomMode
                && cached.PreserveOriginalCards == ChaosRunDefinitions.ActivePreserveOriginalCards
                && cached.ActiveCharacters.SequenceEqual(normalizedCharacters)
                && cached.PoolReferences.Count == poolReferences.Length
                && cached.PoolReferences.Zip(poolReferences).All(pair => ReferenceEquals(pair.First, pair.Second))
                && string.Equals(cached.Payload, payload, StringComparison.Ordinal);
        }
    }

    private static string GetOrCreateRunPayload(
        IReadOnlyCollection<GeneratedCharacter> activeCharacters,
        string seed,
        IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> pools,
        out bool rebuilt)
    {
        var normalizedCharacters = NormalizeCharacters(activeCharacters);
        var poolReferences = ChaosRunDefinitions.SupportedPools.Select(character => pools[character]).ToArray();
        var ancientFuel = ChaosRunDefinitions.AncientFuelActive;
        var ultimateChaos = ChaosRunDefinitions.ActiveUltimateChaos;
        var replaceStartingCards = ChaosRunDefinitions.ActiveReplaceStartingCards;
        var numericBalanceOptimization = ChaosRunDefinitions.ActiveNumericBalanceOptimization;
        var numericRandomMode = ChaosRunDefinitions.ActiveNumericRandomMode;
        var preserveOriginalCards = ChaosRunDefinitions.ActivePreserveOriginalCards;
        lock (PayloadCacheGate)
        {
            if (_cachedRunPayload is { } cached
                && cached.Seed == seed
                && cached.AncientFuel == ancientFuel
                && cached.UltimateChaos == ultimateChaos
                && cached.ReplaceStartingCards == replaceStartingCards
                && cached.NumericBalanceOptimization == numericBalanceOptimization
                && cached.NumericRandomMode == numericRandomMode
                && cached.PreserveOriginalCards == preserveOriginalCards
                && cached.ActiveCharacters.SequenceEqual(normalizedCharacters)
                && cached.PoolReferences.Count == poolReferences.Length
                && cached.PoolReferences.Zip(poolReferences).All(pair => ReferenceEquals(pair.First, pair.Second)))
            {
                rebuilt = false;
                return cached.Payload;
            }

            var ordered = ChaosRunDefinitions.SupportedPools
                .Select((character, index) => CreatePoolEnvelope(character, poolReferences[index])).ToArray();
            var payload = Encode(new Envelope(SchemaVersion, ModVersion, normalizedCharacters, seed,
                ancientFuel, ultimateChaos, replaceStartingCards, numericBalanceOptimization, numericRandomMode,
                preserveOriginalCards, ordered));
            _cachedRunPayload = new CachedRunPayload(normalizedCharacters, seed, ancientFuel, ultimateChaos,
                replaceStartingCards, numericBalanceOptimization, numericRandomMode, preserveOriginalCards,
                poolReferences, payload);
            rebuilt = true;
            return payload;
        }
    }

    private static SerializableModifier CreateSerializableModifier(string payload, string? surpriseKnowledge = null)
    {
        var strings = new List<SavedProperties.SavedProperty<string>>
        {
            new(PayloadProperty, payload)
        };
        if (!string.IsNullOrEmpty(surpriseKnowledge))
            strings.Add(new SavedProperties.SavedProperty<string>(SurpriseKnowledgeProperty, surpriseKnowledge));
        return new SerializableModifier
        {
            Id = ModelDb.Modifier<ChaosPoolSnapshotModifier>().Id,
            Props = new SavedProperties
            {
                strings = strings
            }
        };
    }

    private static PoolEnvelope CreatePoolEnvelope(GeneratedCharacter character,
        IEnumerable<ChaosCardDefinition> definitions) =>
        new(character, definitions.Select(definition =>
            HydrateRuntimeSpecs(definition, requirePersisted: false)).ToArray());

    public static string? ReadFrom(SerializableRun save) => FindStringProperty(save, PayloadProperty, remove: false);

    public static string? ReadFrom(RunHistory history)
    {
        var id = ModelDb.Modifier<ChaosPoolSnapshotModifier>().Id;
        for (var index = history.Modifiers.Count - 1; index >= 0; index--)
        {
            var modifier = history.Modifiers[index];
            if (modifier.Id != id) continue;
            return modifier.Props?.strings?
                .FirstOrDefault(property => property.name == PayloadProperty).value;
        }
        return null;
    }

    public static string? ReadSurpriseKnowledge(SerializableRun save) =>
        FindStringProperty(save, SurpriseKnowledgeProperty, remove: false);

    public static string? ExtractFrom(SerializableRun save) => FindStringProperty(save, PayloadProperty, remove: true);

    private static string? FindStringProperty(SerializableRun save, string propertyName, bool remove)
    {
        var id = ModelDb.Modifier<ChaosPoolSnapshotModifier>().Id;
        string? payload = null;
        for (var index = save.Modifiers.Count - 1; index >= 0; index--)
        {
            var modifier = save.Modifiers[index];
            if (modifier.Id != id) continue;
            payload ??= modifier.Props?.strings?
                .FirstOrDefault(property => property.name == propertyName).value;
            if (remove) save.Modifiers.RemoveAt(index);
        }
        return payload;
    }

    public static IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> RestoreAll(
        string? payload, GeneratedCharacter activeCharacter, string seed,
        IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> fallbacks,
        out ChaosPoolRestoreReport report, bool logFailures = true) =>
        RestoreAll(payload, [activeCharacter], seed, fallbacks, out report, logFailures);

    public static IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> RestoreAll(
        string? payload, IReadOnlyCollection<GeneratedCharacter> activeCharacters, string seed,
        IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> fallbacks,
        out ChaosPoolRestoreReport report, bool logFailures = true)
    {
        var normalizedCharacters = NormalizeCharacters(activeCharacters);
        var total = fallbacks.Values.Sum(cards => cards.Count);
        if (string.IsNullOrWhiteSpace(payload))
        {
            report = new ChaosPoolRestoreReport(string.Empty, true, 0, total,
                "This save predates all-pool snapshots.");
            return fallbacks;
        }

        try
        {
            using var document = Decode(payload);
            var root = document.RootElement;
            var schema = root.GetProperty("schema").GetInt32();
            var savedVersion = root.GetProperty("modVersion").GetString() ?? string.Empty;
            var savedSeed = root.GetProperty("seed").GetString() ?? string.Empty;
            var allowRandomizedNumericValues = SnapshotUsesNumericRandomValues(root);
            if (!string.Equals(savedSeed, seed, StringComparison.Ordinal))
                throw new InvalidDataException("Snapshot seed does not match the run seed.");

            var restoredPools = fallbacks.ToDictionary(pair => pair.Key, pair => pair.Value);
            var restored = 0;
            var rejected = 0;
            if (schema >= MinimumLiveSchemaVersion && schema <= SchemaVersion)
            {
                var savedActive = root.GetProperty("activeCharacters")
                    .Deserialize<GeneratedCharacter[]>(JsonOptions) ?? [];
                var normalizedSaved = NormalizeCharacters(savedActive);
                if (!normalizedSaved.SequenceEqual(normalizedCharacters))
                    throw new InvalidDataException($"Snapshot characters [{string.Join(", ", normalizedSaved)}] do not match [{string.Join(", ", normalizedCharacters)}].");
                RestorePools(root, fallbacks, restoredPools, ref restored, ref rejected, logFailures, schema,
                    allowRandomizedNumericValues);
            }
            else
                throw new InvalidDataException($"Unsupported pool snapshot schema {schema}.");

            var regenerated = total - restored;
            report = new ChaosPoolRestoreReport(savedVersion,
                !string.Equals(savedVersion, ModVersion, StringComparison.Ordinal), restored, regenerated,
                rejected == 0 && regenerated == 0 ? null : $"Rejected={rejected}, regenerated={regenerated}.");
            return restoredPools;
        }
        catch (Exception exception)
        {
            report = new ChaosPoolRestoreReport(string.Empty, true, 0, total, exception.Message);
            if (logFailures)
                Log.Warn($"[AutoAnthony] Could not read the saved generated-card pools; regenerated every pool: {exception}");
            return fallbacks;
        }
    }

    internal static bool TryRestoreComplete(string? payload,
        IReadOnlyCollection<GeneratedCharacter> activeCharacters, string seed,
        out IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> restoredPools,
        out ChaosPoolRestoreReport report)
    {
        restoredPools = new Dictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>>();
        report = new ChaosPoolRestoreReport(string.Empty, true, 0, 0, "Complete snapshot fast path was unavailable.");
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            using var document = Decode(payload);
            var root = document.RootElement;
            var schema = root.GetProperty("schema").GetInt32();
            if (schema is not (5 or 6 or 7 or 8 or 9 or SchemaVersion)
                || !string.Equals(root.GetProperty("seed").GetString(), seed, StringComparison.Ordinal))
                return false;
            var normalizedCharacters = NormalizeCharacters(activeCharacters);
            var savedActive = root.GetProperty("activeCharacters")
                .Deserialize<GeneratedCharacter[]>(JsonOptions) ?? [];
            if (!NormalizeCharacters(savedActive).SequenceEqual(normalizedCharacters)) return false;
            var allowRandomizedNumericValues = SnapshotUsesNumericRandomValues(root);

            var restored = new Dictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>>();
            foreach (var poolElement in root.GetProperty("pools").EnumerateArray())
            {
                var character = poolElement.GetProperty("character").Deserialize<GeneratedCharacter>(JsonOptions);
                if (!ChaosRunDefinitions.SupportedPools.Contains(character) || restored.ContainsKey(character))
                    return false;
                var expectedRarities = ChaosRunDefinitions.ExpectedRarities(character);
                var cards = new ChaosCardDefinition[expectedRarities.Length];
                var seen = new bool[expectedRarities.Length];
                foreach (var element in poolElement.GetProperty("cards").EnumerateArray())
                {
                    var definition = element.Deserialize<ChaosCardDefinition>(JsonOptions)
                        ?? throw new JsonException("Card definition was null.");
                    definition = HydrateRuntimeSpecs(definition, requirePersisted: schema >= 8);
                    if ((uint)definition.Slot >= (uint)cards.Length || seen[definition.Slot]) return false;
                    ValidateDefinition(definition, character, expectedRarities, allowRandomizedNumericValues);
                    cards[definition.Slot] = definition;
                    seen[definition.Slot] = true;
                }
                if (seen.Any(value => !value)) return false;
                restored[character] = cards;
            }
            if (ChaosRunDefinitions.SupportedPools.Any(character => !restored.ContainsKey(character))) return false;

            var savedVersion = root.GetProperty("modVersion").GetString() ?? string.Empty;
            var total = restored.Values.Sum(cards => cards.Count);
            restoredPools = restored;
            report = new ChaosPoolRestoreReport(savedVersion,
                !string.Equals(savedVersion, ModVersion, StringComparison.Ordinal), total, 0);
            return true;
        }
        catch (Exception exception)
        {
            report = report with { Failure = exception.Message };
            return false;
        }
    }

    /// <summary>
    /// Run history only needs the immutable identity and card text that were saved with the completed run. It must
    /// never generate a replacement 514-card pool merely because a newer version tightened gameplay validation.
    /// Deserialize each structurally safe historical definition and let unavailable slots use DeprecatedCard.
    /// Unlike the live-run restore path, this deliberately does not execute current balance/assembly validation.
    /// </summary>
    internal static bool TryRestoreForHistory(string? payload,
        IReadOnlyCollection<GeneratedCharacter> activeCharacters, string seed,
        out ChaosHistorySnapshotRestore restore, out string? failure)
    {
        restore = new ChaosHistorySnapshotRestore(string.Empty, false, false, true, true, false, false,
            new Dictionary<GeneratedCharacter, IReadOnlyDictionary<int, ChaosCardDefinition>>(), 0);
        failure = null;
        if (string.IsNullOrWhiteSpace(payload))
        {
            failure = "The history entry has no generated-card snapshot.";
            return false;
        }

        try
        {
            using var document = Decode(payload);
            var root = document.RootElement;
            var schema = root.GetProperty("schema").GetInt32();
            var savedSeed = root.GetProperty("seed").GetString() ?? string.Empty;
            if (!string.Equals(savedSeed, seed, StringComparison.Ordinal))
                throw new InvalidDataException("Snapshot seed does not match the run-history seed.");

            var normalizedCharacters = NormalizeCharacters(activeCharacters);
            if (schema == 1)
            {
                var savedCharacter = root.GetProperty("character").Deserialize<GeneratedCharacter>(JsonOptions);
                EnsureLegacyCharacterMatches(savedCharacter, normalizedCharacters);
            }
            else if (schema == 2)
            {
                var savedCharacter = root.GetProperty("activeCharacter").Deserialize<GeneratedCharacter>(JsonOptions);
                EnsureLegacyCharacterMatches(savedCharacter, normalizedCharacters);
            }
            else if (schema is 3 or 4 or 5 or 6 or 7 or 8 or 9 or SchemaVersion)
            {
                var savedActive = root.GetProperty("activeCharacters")
                    .Deserialize<GeneratedCharacter[]>(JsonOptions) ?? [];
                if (!NormalizeCharacters(savedActive).SequenceEqual(normalizedCharacters))
                    throw new InvalidDataException("Snapshot characters do not match the run-history players.");
            }
            else
                throw new InvalidDataException($"Unsupported pool snapshot schema {schema}.");

            var restored = new Dictionary<GeneratedCharacter, IReadOnlyDictionary<int, ChaosCardDefinition>>();
            var rejected = 0;
            if (schema == 1)
            {
                var character = root.GetProperty("character").Deserialize<GeneratedCharacter>(JsonOptions);
                restored[character] = RestoreHistoryPool(root.GetProperty("cards"), character, ref rejected,
                    requirePersistedSpecs: false);
            }
            else
            {
                foreach (var poolElement in root.GetProperty("pools").EnumerateArray())
                {
                    var character = poolElement.GetProperty("character").Deserialize<GeneratedCharacter>(JsonOptions);
                    if (!ChaosRunDefinitions.SupportedPools.Contains(character) || restored.ContainsKey(character))
                    {
                        rejected++;
                        continue;
                    }
                    restored[character] = RestoreHistoryPool(poolElement.GetProperty("cards"), character,
                        ref rejected, requirePersistedSpecs: schema >= 8);
                }
            }

            var ancientFuel = schema >= 4 && root.TryGetProperty("ancientFuel", out var ancientFuelElement)
                ? ancientFuelElement.GetBoolean()
                : ChaosRunDefinitions.ShouldUseAncientFuel(seed);
            var ultimateChaos = root.TryGetProperty("ultimateChaos", out var ultimateElement)
                                && ultimateElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? ultimateElement.GetBoolean()
                : schema == 4 && ContainsUltimateChaosCard(root);
            var replaceStartingCards = root.TryGetProperty("replaceStartingCards", out var replaceElement)
                                       && replaceElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? replaceElement.GetBoolean()
                : true;
            var numericBalanceOptimization = root.TryGetProperty("numericBalanceOptimization", out var balanceElement)
                                             && balanceElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? balanceElement.GetBoolean()
                // All generator versions before this option used what is now the balanced distribution.
                : true;
            var numericRandomMode = root.TryGetProperty("numericRandomMode", out var randomElement)
                                    && randomElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                && randomElement.GetBoolean();
            var preserveOriginalCards = root.TryGetProperty("preserveOriginalCards", out var preserveElement)
                                        && preserveElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                && preserveElement.GetBoolean();
            restore = new ChaosHistorySnapshotRestore(
                root.GetProperty("modVersion").GetString() ?? string.Empty,
                ancientFuel, ultimateChaos, replaceStartingCards, numericBalanceOptimization, numericRandomMode,
                preserveOriginalCards, restored, rejected);
            return restored.Values.Sum(cards => cards.Count) > 0;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return false;
        }
    }

    private static IReadOnlyDictionary<int, ChaosCardDefinition> RestoreHistoryPool(JsonElement elements,
        GeneratedCharacter character, ref int rejected, bool requirePersistedSpecs)
    {
        var expectedRarities = ChaosRunDefinitions.ExpectedRarities(character);
        var cards = new Dictionary<int, ChaosCardDefinition>();
        foreach (var element in elements.EnumerateArray())
        {
            try
            {
                var definition = element.Deserialize<ChaosCardDefinition>(JsonOptions)
                    ?? throw new JsonException("Card definition was null.");
                if (requirePersistedSpecs)
                    definition = HydrateRuntimeSpecs(definition, requirePersisted: true);
                ValidateHistoryDefinition(definition, character, expectedRarities);
                if (!cards.TryAdd(definition.Slot, definition))
                    throw new InvalidDataException($"Duplicate slot {definition.Slot}.");
            }
            catch
            {
                rejected++;
            }
        }
        return cards;
    }

    private static void ValidateHistoryDefinition(ChaosCardDefinition definition, GeneratedCharacter character,
        IReadOnlyList<GeneratedRarity> expectedRarities)
    {
        if ((uint)definition.Slot >= (uint)expectedRarities.Count)
            throw new InvalidDataException($"Slot {definition.Slot} is outside the historical pool.");
        if (definition.Card is null || definition.Card.Character != character
                                    || definition.Card.Rarity != expectedRarities[definition.Slot])
            throw new InvalidDataException($"Slot {definition.Slot} has an invalid historical identity.");
        if (definition.Card.Operations is null || definition.Card.Operations.Any(operation =>
                operation is null || operation.Parameters is null))
            throw new InvalidDataException($"Slot {definition.Slot} has malformed historical operations.");
        if (definition.Card.Upgrade?.Effects.Any(effect => !Enum.IsDefined(effect.Kind)
                || effect.OperationIndex is { } operationIndex
                && (operationIndex < 0 || operationIndex >= definition.Card.Operations.Count)) == true)
            throw new InvalidDataException($"Slot {definition.Slot} has malformed historical upgrades.");
        if (string.IsNullOrWhiteSpace(definition.Card.Name?.Chinese)
            || string.IsNullOrWhiteSpace(definition.Card.Name.English)
            || string.IsNullOrWhiteSpace(definition.PortraitPath))
            throw new InvalidDataException($"Slot {definition.Slot} has no historical name or portrait.");
    }

    internal static bool RestoreAncientFuelFlag(string? payload, string seed)
    {
        if (string.IsNullOrWhiteSpace(payload)) return ChaosRunDefinitions.ShouldUseAncientFuel(seed);
        try
        {
            using var document = Decode(payload);
            var root = document.RootElement;
            var schema = root.GetProperty("schema").GetInt32();
            if (schema < MinimumLiveSchemaVersion || schema > SchemaVersion
                || !string.Equals(root.GetProperty("seed").GetString(), seed, StringComparison.Ordinal))
                return ChaosRunDefinitions.ShouldUseAncientFuel(seed);
            return root.TryGetProperty("ancientFuel", out var flag)
                ? flag.GetBoolean()
                : ChaosRunDefinitions.ShouldUseAncientFuel(seed);
        }
        catch
        {
            return ChaosRunDefinitions.ShouldUseAncientFuel(seed);
        }
    }

    internal static bool RestoreUltimateChaosFlag(string? payload, string seed)
    {
        // Saves made before Ultimate Chaos was persisted were normal-mode saves. Never let the current
        // global option silently change the mode of an existing run.
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            using var document = Decode(payload);
            var root = document.RootElement;
            var schema = root.GetProperty("schema").GetInt32();
            if (schema < MinimumLiveSchemaVersion || schema > SchemaVersion
                || !string.Equals(root.GetProperty("seed").GetString(), seed, StringComparison.Ordinal)) return false;
            if (root.TryGetProperty("ultimateChaos", out var explicitFlag)
                && explicitFlag.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return explicitFlag.GetBoolean();
            return false;
        }
        catch
        {
            return false;
        }
    }

    internal static bool RestoreReplaceStartingCardsFlag(string? payload, string seed)
    {
        // Every save from before this option existed replaced its Basic cards and starting deck.
        if (string.IsNullOrWhiteSpace(payload)) return true;
        try
        {
            using var document = Decode(payload);
            var root = document.RootElement;
            if (!string.Equals(root.GetProperty("seed").GetString(), seed, StringComparison.Ordinal)) return true;
            return root.TryGetProperty("replaceStartingCards", out var explicitFlag)
                   && explicitFlag.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? explicitFlag.GetBoolean()
                : true;
        }
        catch
        {
            return true;
        }
    }

    internal static bool RestoreNumericBalanceOptimizationFlag(string? payload, string seed)
    {
        // Before this setting existed, every generated pool used today's balanced distribution. Preserve that
        // behavior for selective regeneration in old live saves instead of applying the new aggressive default.
        if (string.IsNullOrWhiteSpace(payload)) return true;
        try
        {
            using var document = Decode(payload);
            var root = document.RootElement;
            if (!string.Equals(root.GetProperty("seed").GetString(), seed, StringComparison.Ordinal)) return true;
            return root.TryGetProperty("numericBalanceOptimization", out var explicitFlag)
                   && explicitFlag.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? explicitFlag.GetBoolean()
                : true;
        }
        catch
        {
            return true;
        }
    }

    internal static bool RestoreNumericRandomModeFlag(string? payload, string seed) =>
        RestoreOptionalFalseFlag(payload, seed, "numericRandomMode");

    private static bool SnapshotUsesNumericRandomValues(JsonElement root) =>
        root.TryGetProperty("numericRandomMode", out var flag)
        && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
        && flag.GetBoolean();

    internal static bool RestorePreserveOriginalCardsFlag(string? payload, string seed) =>
        RestoreOptionalFalseFlag(payload, seed, "preserveOriginalCards");

    internal static bool RestoreRandomCardArtFlag(string? payload, string seed)
    {
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            using var document = Decode(payload);
            var root = document.RootElement;
            if (!string.Equals(root.GetProperty("seed").GetString(), seed, StringComparison.Ordinal)) return false;
            if (!root.TryGetProperty("pools", out var pools) || pools.ValueKind != JsonValueKind.Array) return false;
            foreach (var pool in pools.EnumerateArray())
            {
                if (!pool.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array) continue;
                foreach (var card in cards.EnumerateArray())
                    if (card.TryGetProperty("portraitVariantId", out var variant)
                        && variant.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(variant.GetString()))
                        return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static bool RestoreOptionalFalseFlag(string? payload, string seed, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            using var document = Decode(payload);
            var root = document.RootElement;
            if (!string.Equals(root.GetProperty("seed").GetString(), seed, StringComparison.Ordinal)) return false;
            return root.TryGetProperty(propertyName, out var flag)
                   && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                   && flag.GetBoolean();
        }
        catch { return false; }
    }

    private static bool ContainsUltimateChaosCard(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("unifiedChaos") && property.Value.ValueKind == JsonValueKind.True)
                    return true;
                if (ContainsUltimateChaosCard(property.Value)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                if (ContainsUltimateChaosCard(child)) return true;
        }
        return false;
    }

    private static void RestorePools(JsonElement root,
        IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> fallbacks,
        IDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> restoredPools,
        ref int restored, ref int rejected, bool logFailures, int schema, bool allowRandomizedNumericValues)
    {
        var seen = new HashSet<GeneratedCharacter>();
        foreach (var poolElement in root.GetProperty("pools").EnumerateArray())
        {
            var character = poolElement.GetProperty("character").Deserialize<GeneratedCharacter>(JsonOptions);
            if (!fallbacks.ContainsKey(character) || !seen.Add(character))
            {
                rejected++;
                continue;
            }
            var result = RestorePool(poolElement.GetProperty("cards"), character,
                fallbacks[character], logFailures, schema, allowRandomizedNumericValues);
            restoredPools[character] = result.Cards;
            restored += result.Accepted;
            rejected += result.Rejected;
        }
    }

    private static GeneratedCharacter[] NormalizeCharacters(IEnumerable<GeneratedCharacter> characters) =>
        characters.Where(character => character != GeneratedCharacter.Colorless)
            .Distinct().OrderBy(character => character).ToArray();

    private static void EnsureLegacyCharacterMatches(GeneratedCharacter savedCharacter,
        IReadOnlyList<GeneratedCharacter> activeCharacters)
    {
        if (activeCharacters.Count != 1 || activeCharacters[0] != savedCharacter)
            throw new InvalidDataException($"Legacy snapshot character {savedCharacter} does not match [{string.Join(", ", activeCharacters)}].");
    }

    private static (IReadOnlyList<ChaosCardDefinition> Cards, int Accepted, int Rejected) RestorePool(
        JsonElement elements, GeneratedCharacter character, IReadOnlyList<ChaosCardDefinition> fallbacks,
        bool logFailures, int schema, bool allowRandomizedNumericValues)
    {
        var merged = fallbacks.ToArray();
        var acceptedSlots = new HashSet<int>();
        var seenSlots = new HashSet<int>();
        var rejected = 0;
        foreach (var element in elements.EnumerateArray())
        {
            ChaosCardDefinition? definition = null;
            var claimedSlot = -1;
            try
            {
                definition = element.Deserialize<ChaosCardDefinition>(JsonOptions)
                    ?? throw new JsonException("Card definition was null.");
                definition = HydrateRuntimeSpecs(definition, requirePersisted: schema >= 8);
                claimedSlot = definition.Slot;
                if ((uint)claimedSlot >= (uint)fallbacks.Count)
                    throw new InvalidDataException($"Slot {claimedSlot} is outside the current pool.");
                if (!seenSlots.Add(claimedSlot))
                    throw new InvalidDataException($"Duplicate slot {claimedSlot}.");
                ValidateDefinition(definition, character, fallbacks, allowRandomizedNumericValues);
                acceptedSlots.Add(claimedSlot);
                merged[claimedSlot] = definition;
            }
            catch (Exception exception)
            {
                rejected++;
                if (definition is not null && claimedSlot >= 0 && claimedSlot < fallbacks.Count)
                    merged[claimedSlot] = PreserveSavedIdentity(fallbacks[claimedSlot], definition);
                if (logFailures)
                    Log.Warn($"[AutoAnthony] Rejected one saved {character} generated card: {exception.Message}");
            }
        }
        return (merged, acceptedSlots.Count, rejected);
    }

    private static ChaosCardDefinition PreserveSavedIdentity(ChaosCardDefinition fallback, ChaosCardDefinition saved)
    {
        var savedName = saved.Card?.Name;
        var name = !string.IsNullOrWhiteSpace(savedName?.Chinese) && !string.IsNullOrWhiteSpace(savedName.English)
            ? savedName : fallback.Card.Name;
        var hasSavedPortrait = !string.IsNullOrWhiteSpace(saved.PortraitPath)
            && ChaosPortraitCompatibility.IsStableOriginalPath(saved.PortraitPath);
        return fallback with
        {
            Card = fallback.Card with { Name = name },
            PortraitPath = hasSavedPortrait ? saved.PortraitPath : fallback.PortraitPath,
            // Old snapshots predate PortraitSourceId. If they have their own portrait path, leave the source null
            // so the compatibility resolver infers the original card from that saved path instead of incorrectly
            // borrowing the newly generated fallback slot's art identity.
            PortraitSourceId = hasSavedPortrait ? saved.PortraitSourceId : fallback.PortraitSourceId,
            HitFx = NonEmpty(saved.HitFx, fallback.HitFx),
            AttackAnimation = NonEmpty(saved.AttackAnimation, fallback.AttackAnimation),
            PowerIconPath = NonEmpty(saved.PowerIconPath, fallback.PowerIconPath),
            PowerBigIconPath = NonEmpty(saved.PowerBigIconPath, fallback.PowerBigIconPath)
        };
    }

    private static string NonEmpty(string? saved, string fallback) => string.IsNullOrWhiteSpace(saved) ? fallback : saved;

    private static ChaosCardDefinition HydrateRuntimeSpecs(ChaosCardDefinition definition, bool requirePersisted)
    {
        if (definition.Card?.Operations is null)
            throw new InvalidDataException($"Slot {definition.Slot} has no operations.");
        IReadOnlyList<OperationRuntimeSpec> specs;
        if (definition.RuntimeSpecs is { } persisted)
        {
            if (persisted.Count != definition.Card.Operations.Count)
                throw new InvalidDataException($"Slot {definition.Slot} has a mismatched RuntimeSpec count.");
            foreach (var spec in persisted) spec.Validate();
            specs = persisted;
        }
        else
        {
            if (requirePersisted)
                throw new InvalidDataException($"Slot {definition.Slot} is missing persisted RuntimeSpecs.");
            specs = definition.Card.Operations.Select(OperationRuntimeSpecCompiler.CompileLegacy).ToArray();
        }

        var operations = definition.Card.Operations.Select((operation, index) =>
        {
            var hydrated = operation with { RuntimeSpec = specs[index] };
            if (hydrated.LocalizedText is not null) return hydrated;
            if (ComponentLocalizationApi.TryGet(hydrated.LocalizationId, out var registeredLocalization))
            {
                var localized = registeredLocalization;
                var registeredEnglish = localized.RenderEnglish(specs[index])
                    ?? EnglishCardDescriptionRenderer.OperationText(hydrated);
                if (hydrated.DerivativeId is not null)
                    localized = DerivativeSlotCatalog.BindSourceLocalizedText(hydrated, localized,
                        registeredEnglish, specs[index]);
                if (hydrated.OrbSourceId is not null || hydrated.OrbOutputId is not null)
                    localized = OrbSlotCatalog.BindResolvedLocalizedText(hydrated, localized, registeredEnglish,
                        specs[index]);
                return hydrated with { LocalizedText = localized };
            }
            // Named localization templates are deliberately not persisted per operation: doing so would duplicate
            // the already stored card text throughout every live/history snapshot. Rebuild this presentation-only
            // cache only for schema 5-9 operations without a stable ID. Schema 10 takes the registry path above and
            // therefore does not recompile hundreds of localized sentences while a run is loading.
            try
            {
                var english = EnglishCardDescriptionRenderer.OperationText(hydrated);
                if (!OperationLocalizedText.TryCompile(hydrated.ChineseText, english, specs[index],
                        out var localized) || localized is null)
                    return hydrated;
                if (hydrated.DerivativeId is not null)
                    localized = DerivativeSlotCatalog.BindLocalizedText(hydrated, localized, english);
                if (hydrated.OrbSourceId is not null || hydrated.OrbOutputId is not null)
                    localized = OrbSlotCatalog.BindResolvedLocalizedText(hydrated, localized, english, specs[index]);
                return hydrated with { LocalizedText = localized };
            }
            catch (InvalidOperationException)
            {
                return hydrated;
            }
        }).ToArray();
        var effects = definition.Card.Upgrade?.Effects ?? [];
        IReadOnlyList<string?> upgradeSlots;
        if (definition.UpgradeValueSlots is { } persistedUpgradeSlots)
        {
            if (persistedUpgradeSlots.Count != effects.Count)
                throw new InvalidDataException($"Slot {definition.Slot} has a mismatched upgrade-slot count.");
            foreach (var slot in persistedUpgradeSlots.Where(slot => slot is not null))
                OperationRuntimeSpec.ValidateId(slot!, "upgrade_value_slot", required: true);
            upgradeSlots = persistedUpgradeSlots;
        }
        else
        {
            if (requirePersisted)
                throw new InvalidDataException($"Slot {definition.Slot} is missing persisted upgrade slots.");
            // Old schemas intentionally retain null here: their execution used the historical first-number
            // projection, including the documented DrawAndBlock anomaly.
            upgradeSlots = Enumerable.Repeat<string?>(null, effects.Count).ToArray();
        }
        var upgradedPlan = definition.Card.Upgrade is null ? null : definition.Card.Upgrade with
        {
            Effects = effects.Select((effect, index) => effect with
            {
                ValueSlotId = upgradeSlots[index]
            }).ToArray()
        };
        return definition with
        {
            Card = definition.Card with { Operations = operations, Upgrade = upgradedPlan },
            RuntimeSpecs = specs,
            UpgradeValueSlots = upgradeSlots
        };
    }

    private static void ValidateDefinition(ChaosCardDefinition definition, GeneratedCharacter character,
        IReadOnlyList<ChaosCardDefinition> fallbacks, bool allowRandomizedNumericValues)
    {
        if ((uint)definition.Slot >= (uint)fallbacks.Count)
            throw new InvalidDataException($"Slot {definition.Slot} is outside the current pool.");
        ValidateDefinitionCore(definition, character, fallbacks[definition.Slot].Card.Rarity,
            allowRandomizedNumericValues);
    }

    private static void ValidateDefinition(ChaosCardDefinition definition, GeneratedCharacter character,
        IReadOnlyList<GeneratedRarity> expectedRarities, bool allowRandomizedNumericValues)
    {
        if ((uint)definition.Slot >= (uint)expectedRarities.Count)
            throw new InvalidDataException($"Slot {definition.Slot} is outside the current pool.");
        ValidateDefinitionCore(definition, character, expectedRarities[definition.Slot],
            allowRandomizedNumericValues);
    }

    private static void ValidateDefinitionCore(ChaosCardDefinition definition, GeneratedCharacter character,
        GeneratedRarity expectedRarity, bool allowRandomizedNumericValues)
    {
        if (definition.Card is null || definition.Card.Character != character)
            throw new InvalidDataException($"Slot {definition.Slot} belongs to another pool.");
        if (definition.Card.Rarity != expectedRarity)
            throw new InvalidDataException($"Slot {definition.Slot} has the wrong rarity.");
        if (definition.Card.Operations is null || definition.Card.Operations.Any(operation =>
                operation is null || operation.Parameters is null || !IsSupported(operation.Template)))
            throw new InvalidDataException($"Slot {definition.Slot} contains an unknown operation.");
        if (definition.RuntimeSpecs is null
            || definition.RuntimeSpecs.Count != definition.Card.Operations.Count
            || definition.Card.Operations.Select((operation, index) =>
                    operation.RuntimeSpec is null
                    || operation.RuntimeSpec.StableSignature() != definition.RuntimeSpecs[index].StableSignature())
                .Any(invalid => invalid))
            throw new InvalidDataException($"Slot {definition.Slot} contains invalid RuntimeSpecs.");
        if (definition.UpgradeValueSlots is null
            || definition.UpgradeValueSlots.Count != (definition.Card.Upgrade?.Effects.Count ?? 0)
            || definition.Card.Upgrade?.Effects.Select((effect, index) =>
                    !string.Equals(effect.ValueSlotId, definition.UpgradeValueSlots[index],
                        StringComparison.Ordinal)).Any(mismatch => mismatch) == true)
            throw new InvalidDataException($"Slot {definition.Slot} contains invalid upgrade value slots.");
        if (definition.Card.Operations.Any(operation =>
                operation.DerivativeId is { } derivativeId
                && (!DerivativeSlotCatalog.IsSlotOperation(operation.Template)
                    || !DerivativeSlotCatalog.IsKnownId(derivativeId)
                    || DerivativeSlotCatalog.Resolve(derivativeId, operation.Template) is not { } slottedDerivative
                    || !DerivativeSlotCatalog.CanUseAssigned(operation.Template, slottedDerivative))))
            throw new InvalidDataException($"Slot {definition.Slot} contains an unknown derivative slot.");
        if (definition.Card.Operations.Any(operation => operation.DerivativeEnchantmentId is { } enchantmentId
                && (!DerivativeSlotCatalog.IsProducer(operation.Template)
                    || !DerivativeEnchantmentCatalog.IsKnownId(enchantmentId)
                    || DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template) is not { } derivative
                    || !DerivativeEnchantmentCatalog.CanUse(derivative,
                        DerivativeEnchantmentCatalog.Resolve(enchantmentId)!))))
            throw new InvalidDataException($"Slot {definition.Slot} contains an invalid derivative enchantment.");
        if (definition.Card.Operations.Any(operation => operation.DerivativeEnchantmentAmount is { } amount
                && (operation.DerivativeEnchantmentId is not { } enchantmentId
                    || DerivativeEnchantmentCatalog.Resolve(enchantmentId) is not { } enchantment
                    || !DerivativeEnchantmentCatalog.IsAllowedAmount(enchantment, amount))))
            throw new InvalidDataException($"Slot {definition.Slot} contains an invalid derivative enchantment amount.");
        if (definition.Card.Operations.Any(operation => operation.OrbSourceId is { } sourceId
                && (!OrbSlotCatalog.IsKnownId(sourceId)
                    || !OrbSlotCatalog.UsesSource(operation.Template)
                    || !OrbSlotCatalog.CanUseSource(operation.Template, OrbSlotCatalog.Resolve(sourceId)!)))
            || definition.Card.Operations.Any(operation => operation.OrbOutputId is { } outputId
                && (!OrbSlotCatalog.IsKnownId(outputId)
                    || !OrbSlotCatalog.UsesOutput(operation.Template)
                    || !OrbSlotCatalog.CanUseOutput(operation.Template, OrbSlotCatalog.Resolve(outputId)!))))
            throw new InvalidDataException($"Slot {definition.Slot} contains an invalid Orb slot.");
        if (definition.Card.Tags.Any(tag => !Enum.IsDefined(tag))
            || GeneratedCardTagPolicy.AddedKeywords(definition.Card.Upgrade).Any(tag => !Enum.IsDefined(tag))
            || GeneratedCardTagPolicy.RemovedKeywords(definition.Card.Upgrade).Any(tag => !Enum.IsDefined(tag)))
            throw new InvalidDataException($"Slot {definition.Slot} contains an unknown keyword.");
        if ((definition.Card.CustomKeywords ?? [])
                .Concat(GeneratedCardTagPolicy.AddedCustomKeywords(definition.Card.Upgrade))
                .Concat(GeneratedCardTagPolicy.RemovedCustomKeywords(definition.Card.Upgrade))
                .Any(keywordId => !ComponentKeywordApi.IsRegistered(keywordId)))
            throw new InvalidDataException($"Slot {definition.Slot} contains an unknown custom keyword.");
        if (definition.Card.Upgrade?.Effects.Any(effect => !Enum.IsDefined(effect.Kind)
                || effect.OperationIndex is { } operationIndex
                    && (operationIndex < 0 || operationIndex >= definition.Card.Operations.Count)) == true)
            throw new InvalidDataException($"Slot {definition.Slot} contains an invalid upgrade operation.");
        if (string.IsNullOrWhiteSpace(definition.PortraitPath)
            || !ChaosPortraitCompatibility.IsStableOriginalPath(definition.PortraitPath)
            || string.IsNullOrWhiteSpace(definition.HitFx)
            || string.IsNullOrWhiteSpace(definition.AttackAnimation)
            || string.IsNullOrWhiteSpace(definition.PowerIconPath)
            || string.IsNullOrWhiteSpace(definition.PowerBigIconPath)
            || string.IsNullOrWhiteSpace(definition.Card.Name?.Chinese)
            || string.IsNullOrWhiteSpace(definition.Card.Name?.English))
            throw new InvalidDataException($"Slot {definition.Slot} is missing its name or visual identity.");
        CardTemplateValidator.Validate(definition.Card, allowRandomizedNumericValues);
        // RuntimeSpecs are the authoritative execution representation and both localized descriptions are already
        // persisted in the snapshot. Re-rendering them here made resume success depend on process-local legacy text
        // mappings that are populated incidentally while generating cards. An older but fully structured card could
        // therefore fail the first validation, force all six fallback pools to generate, then pass the identical
        // validation after that generation happened to register its wording. Besides the long black screen, this
        // violated the snapshot's purpose. Text projection belongs to generation/migration, never ordinary load.
    }

    private static bool IsSupported(string template) => SupportedTemplates.Value.Contains(template);

    private static IReadOnlySet<string> BuildSupportedTemplates()
    {
        var templates = Enum.GetValues<GeneratedCharacter>()
            .SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms)
            .Select(atom => atom.Template)
            .ToHashSet(StringComparer.Ordinal);
        templates.UnionWith([
            "N_SELECT_HAND_CARD", "N_SELECT_HAND_ATTACK", "I:DrawAndBlockIfSkill",
            "A:ProxyAtomic_Orbit", "A:ProxyAtomic_ChildOfTheStars"
        ]);
        return templates;
    }

    private static string Encode<T>(T envelope)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(json);
        return Prefix + Convert.ToBase64String(output.ToArray());
    }

    private static JsonDocument Decode(string payload)
    {
        if (!payload.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidDataException("Unknown pool snapshot encoding.");
        var compressed = Convert.FromBase64String(payload[Prefix.Length..]);
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }

    internal static void AuditRoundTrip(IReadOnlyCollection<GeneratedCharacter> activeCharacters, string seed,
        IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> pools)
    {
        var marker = ToSerializableModifier(activeCharacters, seed, pools);
        var payload = marker.Props?.strings?.Single(property => property.name == PayloadProperty).value
            ?? throw new InvalidOperationException("Pool snapshot audit did not produce a payload.");
        ClearRunPayloadCache();
        var firstCachedPayload = GetOrCreateRunPayload(activeCharacters, seed, pools, out var firstCacheBuild);
        var reusedCachedPayload = GetOrCreateRunPayload(activeCharacters, seed, pools, out var repeatedCacheBuild);
        if (!firstCacheBuild || repeatedCacheBuild || !ReferenceEquals(firstCachedPayload, reusedCachedPayload))
            throw new InvalidOperationException("Run-pool snapshot cache did not reuse its immutable payload.");
        if (!IsCachedRunPayload(activeCharacters, seed, pools, firstCachedPayload)
            || IsCachedRunPayload(activeCharacters, seed + "_OTHER", pools, firstCachedPayload))
            throw new InvalidOperationException("Run-pool snapshot cache identity audit failed.");
        var replacedPools = pools.ToDictionary(pair => pair.Key, pair => pair.Value);
        var replacedCharacter = ChaosRunDefinitions.SupportedPools[0];
        replacedPools[replacedCharacter] = pools[replacedCharacter].ToArray();
        _ = GetOrCreateRunPayload(activeCharacters, seed, replacedPools, out var replacementCacheBuild);
        if (!replacementCacheBuild)
            throw new InvalidOperationException("Run-pool snapshot cache did not detect a replaced pool definition list.");
        ClearRunPayloadCache();
        if (payload.Length > 512_000)
            throw new InvalidOperationException($"Compressed all-pool snapshot is unexpectedly large: {payload.Length} characters.");
        var syntheticSave = new SerializableRun
        {
            Modifiers = [marker], SerializableOdds = new SerializableRunOddsSet(), Players = [],
            SerializableRng = new SerializableRunRngSet { Seed = string.Empty },
            SerializableSharedRelicGrabBag = new SerializableRelicGrabBag(), ExtraFields = new SerializableExtraRunFields()
        };
        var writer = new PacketWriter { WarnOnGrow = false };
        writer.Write(syntheticSave);
        var reader = new PacketReader();
        reader.Reset(writer.Buffer[..writer.BytePosition]);
        var packetSave = reader.Read<SerializableRun>();
        var markerCount = packetSave.Modifiers.Count;
        var readPayload = ReadFrom(packetSave);
        if (!string.Equals(readPayload, payload, StringComparison.Ordinal)
            || packetSave.Modifiers.Count != markerCount)
            throw new InvalidOperationException("Pool snapshot non-mutating load audit failed.");
        var packetPayload = ExtractFrom(packetSave);
        if (!string.Equals(packetPayload, payload, StringComparison.Ordinal)
            || packetSave.Modifiers.Any(modifier => modifier.Id == marker.Id))
            throw new InvalidOperationException("Pool snapshot multiplayer packet round-trip failed.");

        var restored = RestoreAll(payload, activeCharacters, seed, pools, out var report, logFailures: true);
        var roundTripMismatch = FirstDefinitionMismatch(pools, restored);
        if (report.RegeneratedCards != 0 || report.RestoredCards != pools.Values.Sum(cards => cards.Count)
            || roundTripMismatch is not null)
            throw new InvalidOperationException("All-pool snapshot round-trip audit failed: "
                                                + $"restored={report.RestoredCards}, regenerated={report.RegeneratedCards}, "
                                                + $"expected={pools.Values.Sum(cards => cards.Count)}"
                                                + (roundTripMismatch is null ? "." : $"; {roundTripMismatch}"));
        if (!TryRestoreComplete(payload, activeCharacters, seed, out var fastRestored, out var fastReport)
            || fastReport.RegeneratedCards != 0
            || fastReport.RestoredCards != pools.Values.Sum(cards => cards.Count)
            || ChaosRunDefinitions.SupportedPools.Any(character =>
                JsonSerializer.Serialize(fastRestored[character], JsonOptions)
                != JsonSerializer.Serialize(pools[character], JsonOptions)))
            throw new InvalidOperationException("Complete snapshot fast-path audit failed.");

        var reboundCharacters = activeCharacters.Count == 1
                                && activeCharacters.Contains(GeneratedCharacter.Regent)
            ? new[] { GeneratedCharacter.Ironclad }
            : new[] { GeneratedCharacter.Regent };
        var reboundPayload = RebindMultiplayerActiveCharacters(payload, reboundCharacters, seed);
        if (!TryRestoreComplete(reboundPayload, reboundCharacters, seed,
                out var reboundRestored, out var reboundReport)
            || reboundReport.RegeneratedCards != 0
            || reboundReport.RestoredCards != pools.Values.Sum(cards => cards.Count)
            || ChaosRunDefinitions.SupportedPools.Any(character =>
                JsonSerializer.Serialize(reboundRestored[character], JsonOptions)
                != JsonSerializer.Serialize(pools[character], JsonOptions))
            || string.IsNullOrWhiteSpace(MultiplayerFingerprint(payload)))
            throw new InvalidOperationException("Authoritative multiplayer snapshot rebinding audit failed.");

        var lobbyCarrier = (ChaosPoolSnapshotModifier)ModelDb.Modifier<ChaosPoolSnapshotModifier>().ToMutable();
        lobbyCarrier.MultiplayerGenerationModeSpecified = true;
        lobbyCarrier.MultiplayerModEnabled = true;
        lobbyCarrier.PoolSnapshot = payload;
        var serializedCarrier = lobbyCarrier.ToSerializable();
        if (serializedCarrier.Props?.strings?
                .SingleOrDefault(property => property.name == PayloadProperty).value != payload)
            throw new InvalidOperationException("Authoritative multiplayer snapshot carrier serialization failed.");
        if (!TryRestoreForHistory(payload, activeCharacters, seed, out var historyRestored,
                out var historyFailure)
            || historyFailure is not null
            || historyRestored.RejectedCards != 0
            || historyRestored.Cards.Values.Sum(cards => cards.Count) != pools.Values.Sum(cards => cards.Count))
            throw new InvalidOperationException("Run-history snapshot fast-path audit failed.");

        var sparseIds = new[]
        {
            ChaosCardRegistry.Canonical(GeneratedCharacter.Ironclad, 0).Id,
            ChaosCardRegistry.Canonical(GeneratedCharacter.Ironclad, 0).Id,
            ChaosCardRegistry.Canonical(GeneratedCharacter.Colorless, 1).Id
        };
        var sparseMarker = ToHistorySerializableModifier(activeCharacters, seed, pools, sparseIds,
            out var sparseSavedCount)
            ?? throw new InvalidOperationException("Sparse run-history snapshot was unexpectedly empty.");
        var sparsePayload = sparseMarker.Props?.strings?
            .Single(property => property.name == PayloadProperty).value;
        if (sparseSavedCount != 2
            || string.IsNullOrEmpty(sparsePayload)
            || !TryRestoreForHistory(sparsePayload, activeCharacters, seed,
                out var sparseHistoryRestored, out var sparseHistoryFailure)
            || sparseHistoryFailure is not null
            || sparseHistoryRestored.RejectedCards != 0
            || sparseHistoryRestored.Cards.Values.Sum(cards => cards.Count) != 2
            || sparseHistoryRestored.Cards[GeneratedCharacter.Ironclad].Keys.Single() != 0
            || sparseHistoryRestored.Cards[GeneratedCharacter.Colorless].Keys.Single() != 1)
            throw new InvalidOperationException("Sparse final-deck run-history snapshot audit failed.");
        if (!TryCompactHistorySnapshot(payload, activeCharacters, seed, sparseIds,
                out var migratedSparseMarker, out var migrationSourceCount, out var migrationKeptCount,
                out var migrationFailure)
            || migrationFailure is not null
            || migratedSparseMarker is null
            || migrationSourceCount != pools.Values.Sum(cards => cards.Count)
            || migrationKeptCount != 2)
            throw new InvalidOperationException("Legacy full run-history snapshot compaction audit failed.");
        var migratedSparsePayload = migratedSparseMarker.Props?.strings?
            .Single(property => property.name == PayloadProperty).value;
        if (!TryRestoreForHistory(migratedSparsePayload, activeCharacters, seed,
                out var migratedSparseRestore, out var migratedSparseFailure)
            || migratedSparseFailure is not null
            || migratedSparseRestore.Cards.Values.Sum(cards => cards.Count) != 2)
            throw new InvalidOperationException("Compacted legacy run-history snapshot round-trip audit failed.");
        if (RestoreAncientFuelFlag(payload, seed) != ChaosRunDefinitions.AncientFuelActive)
            throw new InvalidOperationException("Ancient Fuel snapshot round-trip audit failed.");
        if (RestoreUltimateChaosFlag(payload, seed) != ChaosRunDefinitions.ActiveUltimateChaos
            || RestoreUltimateChaosFlag(payload, seed + "_OTHER"))
            throw new InvalidOperationException("Ultimate Chaos snapshot round-trip audit failed.");
        if (RestoreReplaceStartingCardsFlag(payload, seed) != ChaosRunDefinitions.ActiveReplaceStartingCards
            || !RestoreReplaceStartingCardsFlag(payload, seed + "_OTHER"))
            throw new InvalidOperationException("Starting-card replacement snapshot round-trip audit failed.");
        if (RestoreNumericBalanceOptimizationFlag(payload, seed)
                != ChaosRunDefinitions.ActiveNumericBalanceOptimization
            || !RestoreNumericBalanceOptimizationFlag(payload, seed + "_OTHER"))
            throw new InvalidOperationException("Numeric balance snapshot round-trip audit failed.");
        if (RestoreNumericRandomModeFlag(payload, seed) != ChaosRunDefinitions.ActiveNumericRandomMode
            || RestorePreserveOriginalCardsFlag(payload, seed)
               != ChaosRunDefinitions.ActivePreserveOriginalCards)
            throw new InvalidOperationException("New generation flags snapshot round-trip audit failed.");

        var savedActive = NormalizeCharacters(activeCharacters);
        var orderedPools = ChaosRunDefinitions.SupportedPools
            .Select(character => new PoolEnvelope(character, pools[character])).ToArray();
        var forcedAncientPayload = Encode(new Envelope(SchemaVersion, ModVersion, savedActive, seed, true,
            ChaosRunDefinitions.ActiveUltimateChaos, ChaosRunDefinitions.ActiveReplaceStartingCards,
            ChaosRunDefinitions.ActiveNumericBalanceOptimization, ChaosRunDefinitions.ActiveNumericRandomMode,
            ChaosRunDefinitions.ActivePreserveOriginalCards, orderedPools));
        if (!RestoreAncientFuelFlag(forcedAncientPayload, seed))
            throw new InvalidOperationException("Ancient Fuel true snapshot flag was not restored.");
        var retiredLivePayload = Encode(new
        {
            Schema = 4,
            ModVersion = "0.1.0",
            ActiveCharacters = savedActive,
            Seed = seed,
            AncientFuel = false,
            Pools = orderedPools
        });
        _ = RestoreAll(retiredLivePayload, activeCharacters, seed, pools,
            out var retiredLiveReport, logFailures: false);
        if (retiredLiveReport.RestoredCards != 0
            || retiredLiveReport.RegeneratedCards != pools.Values.Sum(cards => cards.Count)
            || !TryRestoreForHistory(retiredLivePayload, activeCharacters, seed,
                out var retiredHistory, out var retiredHistoryFailure)
            || retiredHistoryFailure is not null
            || retiredHistory.Cards.Values.Sum(cards => cards.Count) != pools.Values.Sum(cards => cards.Count))
            throw new InvalidOperationException(
                "Retired schema live-regeneration/history-read compatibility audit failed.");
        var legacyV7Pools = orderedPools.Select(pool => new PoolEnvelope(pool.Character,
            pool.Cards.Select(definition => definition with
            {
                RuntimeSpecs = null,
                UpgradeValueSlots = null
            }).ToArray())).ToArray();
        var legacyV7Payload = Encode(new Envelope(7, "0.2.120", savedActive, seed,
            ChaosRunDefinitions.AncientFuelActive, ChaosRunDefinitions.ActiveUltimateChaos,
            ChaosRunDefinitions.ActiveReplaceStartingCards,
            ChaosRunDefinitions.ActiveNumericBalanceOptimization, false, false, legacyV7Pools));
        var legacyV7Restored = RestoreAll(legacyV7Payload, activeCharacters, seed, pools,
            out var legacyV7Report, logFailures: false);
        if (legacyV7Report.RegeneratedCards != 0
            || legacyV7Restored.Values.SelectMany(cards => cards).Any(definition =>
                definition.RuntimeSpecs is null || definition.UpgradeValueSlots is null
                || definition.Card.Operations.Any(operation => operation.RuntimeSpec is null)))
            throw new InvalidOperationException("Version 7 RuntimeSpec migration audit failed.");

        var legacyV9Payload = Encode(new Envelope(9, "0.3.8", savedActive, seed,
            ChaosRunDefinitions.AncientFuelActive, ChaosRunDefinitions.ActiveUltimateChaos,
            ChaosRunDefinitions.ActiveReplaceStartingCards,
            ChaosRunDefinitions.ActiveNumericBalanceOptimization, ChaosRunDefinitions.ActiveNumericRandomMode,
            ChaosRunDefinitions.ActivePreserveOriginalCards, orderedPools));
        var legacyV9Restored = RestoreAll(legacyV9Payload, activeCharacters, seed, pools,
            out var legacyV9Report, logFailures: false);
        if (legacyV9Report.RegeneratedCards != 0
            || legacyV9Restored.Values.SelectMany(cards => cards).Any(definition =>
                definition.RuntimeSpecs is null || definition.UpgradeValueSlots is null
                || definition.Card.Operations.Any(operation => operation.RuntimeSpec is null)))
            throw new InvalidOperationException("Version 9 structured snapshot migration audit failed.");

        var invalidPools = pools.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<ChaosCardDefinition>)pair.Value.ToArray());
        var invalidCharacter = ChaosRunDefinitions.SupportedPools[0];
        var invalidCards = invalidPools[invalidCharacter].ToArray();
        var invalidOperations = invalidCards[0].Card.Operations.ToArray();
        invalidOperations[0] = invalidOperations[0] with { Template = "REMOVED:Operation" };
        invalidCards[0] = invalidCards[0] with { Card = invalidCards[0].Card with { Operations = invalidOperations } };
        invalidPools[invalidCharacter] = invalidCards;
        var incompatiblePayload = Encode(new Envelope(SchemaVersion, "0.0.0", NormalizeCharacters(activeCharacters), seed,
            ChaosRunDefinitions.AncientFuelActive, ChaosRunDefinitions.ActiveUltimateChaos,
            ChaosRunDefinitions.ActiveReplaceStartingCards,
            ChaosRunDefinitions.ActiveNumericBalanceOptimization,
            ChaosRunDefinitions.ActiveNumericRandomMode,
            ChaosRunDefinitions.ActivePreserveOriginalCards,
            ChaosRunDefinitions.SupportedPools.Select(character => new PoolEnvelope(character, invalidPools[character])).ToArray()));
        var selectivelyRestored = RestoreAll(incompatiblePayload, activeCharacters, seed, pools,
            out var incompatibleReport, logFailures: false);
        if (!incompatibleReport.VersionChanged || incompatibleReport.RegeneratedCards != 1
            || JsonSerializer.Serialize(selectivelyRestored[invalidCharacter][0].Card.Operations, JsonOptions)
                != JsonSerializer.Serialize(pools[invalidCharacter][0].Card.Operations, JsonOptions)
            || selectivelyRestored[invalidCharacter][0].Card.Name?.Chinese != invalidCards[0].Card.Name?.Chinese
            || selectivelyRestored[invalidCharacter][0].PortraitPath != invalidCards[0].PortraitPath)
            throw new InvalidOperationException("All-pool snapshot selective-regeneration audit failed.");
        if (!TryRestoreForHistory(incompatiblePayload, activeCharacters, seed,
                out var incompatibleHistory, out var incompatibleHistoryFailure)
            || incompatibleHistoryFailure is not null
            || incompatibleHistory.RejectedCards != 0
            || incompatibleHistory.Cards[invalidCharacter][0].Card.Operations[0].Template != "REMOVED:Operation")
            throw new InvalidOperationException("Run history applied live gameplay validation or regenerated an old card.");
    }

    private static string? FirstDefinitionMismatch(
        IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> expected,
        IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> actual)
    {
        foreach (var character in ChaosRunDefinitions.SupportedPools)
        {
            var expectedCards = expected[character];
            var actualCards = actual[character];
            if (expectedCards.Count != actualCards.Count)
                return $"{character} count {actualCards.Count}, expected {expectedCards.Count}";
            for (var index = 0; index < expectedCards.Count; index++)
            {
                var expectedJson = JsonSerializer.Serialize(expectedCards[index], JsonOptions);
                var actualJson = JsonSerializer.Serialize(actualCards[index], JsonOptions);
                if (string.Equals(expectedJson, actualJson, StringComparison.Ordinal)) continue;
                var offset = 0;
                var shared = Math.Min(expectedJson.Length, actualJson.Length);
                while (offset < shared && expectedJson[offset] == actualJson[offset]) offset++;
                var expectedTail = expectedJson.Substring(Math.Max(0, offset - 48),
                    Math.Min(160, expectedJson.Length - Math.Max(0, offset - 48)));
                var actualTail = actualJson.Substring(Math.Max(0, offset - 48),
                    Math.Min(160, actualJson.Length - Math.Max(0, offset - 48)));
                return $"{character} slot {index}, offset {offset}; expected `{expectedTail}`; actual `{actualTail}`";
            }
        }
        return null;
    }

    internal static void AuditAuthoritativeMultiplayerRoundTrip(
        IReadOnlyCollection<GeneratedCharacter> activeCharacters, string seed,
        IReadOnlyDictionary<GeneratedCharacter, IReadOnlyList<ChaosCardDefinition>> pools)
    {
        var gameplayFingerprint = MultiplayerGameplayFingerprint(pools);
        if (gameplayFingerprint.Length != 16)
            throw new InvalidOperationException("Deterministic multiplayer gameplay fingerprint is malformed.");
        var presentationOnlyPools = pools.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<ChaosCardDefinition>)pair.Value.Select(definition => definition with
            {
                PortraitPath = definition.PortraitPath + "#cosmetic-audit",
                HitFx = definition.HitFx + "#cosmetic-audit"
            }).ToArray());
        if (MultiplayerGameplayFingerprint(presentationOnlyPools) != gameplayFingerprint)
            throw new InvalidOperationException(
                "Cosmetic card fields unexpectedly affect the deterministic multiplayer fingerprint.");
        var gameplayChangedPools = pools.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<ChaosCardDefinition>)pair.Value.ToArray());
        var changedCharacter = ChaosRunDefinitions.SupportedPools[0];
        var changedCards = gameplayChangedPools[changedCharacter].ToArray();
        changedCards[0] = changedCards[0] with
        {
            Card = changedCards[0].Card with { Cost = changedCards[0].Card.Cost + 1 }
        };
        gameplayChangedPools[changedCharacter] = changedCards;
        if (MultiplayerGameplayFingerprint(gameplayChangedPools) == gameplayFingerprint)
            throw new InvalidOperationException(
                "A gameplay card change did not affect the deterministic multiplayer fingerprint.");

        var carrier = (ChaosPoolSnapshotModifier)ModelDb.Modifier<ChaosPoolSnapshotModifier>().ToMutable();
        carrier.MultiplayerGenerationModeSpecified = true;
        carrier.MultiplayerModEnabled = true;
        carrier.MultiplayerUltimateChaos = true;
        carrier.MultiplayerReplaceStartingCardsSpecified = true;
        carrier.MultiplayerReplaceStartingCards = false;
        carrier.MultiplayerNumericBalanceOptimizationSpecified = true;
        carrier.MultiplayerNumericBalanceOptimization = true;
        carrier.MultiplayerNumericRandomMode = true;
        carrier.MultiplayerPreserveOriginalCards = true;
        carrier.MultiplayerRandomCardArtSpecified = true;
        carrier.MultiplayerRandomCardArt = true;
        carrier.MultiplayerGenerationFingerprint = gameplayFingerprint;
        var serializedCarrier = carrier.ToSerializable();
        var packetWriter = new PacketWriter { WarnOnGrow = false };
        packetWriter.Write(serializedCarrier);
        var restoredCarrier = (ChaosPoolSnapshotModifier)ModifierModel.FromSerializable(serializedCarrier);
        if (packetWriter.BytePosition > 2_048
            || restoredCarrier.PoolSnapshot.Length != 0
            || restoredCarrier.MultiplayerGenerationFingerprint != gameplayFingerprint
            || !restoredCarrier.MultiplayerRandomCardArtSpecified
            || !restoredCarrier.MultiplayerRandomCardArt)
            throw new InvalidOperationException(
                $"The lightweight multiplayer generation carrier failed round-trip validation ({packetWriter.BytePosition} bytes).");
        Log.Info($"[AutoAnthony] Lightweight multiplayer carrier audit: fingerprint={gameplayFingerprint}, bytes={packetWriter.BytePosition}.");

        var payload = GetAuthoritativeMultiplayerPayload(activeCharacters, seed, pools);
        var reboundCharacters = new[] { GeneratedCharacter.Ironclad, GeneratedCharacter.Regent };
        var reboundPayload = RebindMultiplayerActiveCharacters(payload, reboundCharacters, seed);
        if (!TryRestoreComplete(reboundPayload, reboundCharacters, seed,
                out var restored, out var report)
            || report.RegeneratedCards != 0
            || report.RestoredCards != pools.Values.Sum(cards => cards.Count)
            || ChaosRunDefinitions.SupportedPools.Any(character =>
                JsonSerializer.Serialize(restored[character], JsonOptions)
                != JsonSerializer.Serialize(pools[character], JsonOptions)))
            throw new InvalidOperationException(
                "Numeric-random authoritative multiplayer snapshot round-trip failed.");
    }
}
