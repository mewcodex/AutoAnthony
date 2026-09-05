using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;

namespace AutoAnthony;

/// <summary>
/// Runtime identity supplied by an external character mod. AutoAnthony owns generated-card interpretation; the
/// character mod continues to own its CharacterModel, CardPoolModel, concrete card slot types and run lifecycle.
/// </summary>
public sealed record ExternalComponentCharacterRegistration(
    string ProfileId,
    GeneratedCharacter BalanceArchetype,
    string EnergyIconPrefix,
    IExternalAncientRelicAdapter? AncientRelics = null);

/// <summary>
/// Optional runtime host supplied by an external character adapter. The original four-argument character
/// registration deliberately remains unchanged for binary compatibility. Registering this companion object lets
/// AutoAnthony reconstruct the owning mod's concrete card type when a persistent Power fires later, and exposes
/// the active generated pool to vanilla events without either mod patching AutoAnthony's private methods.
/// </summary>
public sealed record ExternalComponentCharacterRuntimeRegistration(
    string ProfileId,
    int CardCount,
    Func<int, Type> CardTypeForSlot,
    Func<bool> IsRunActive,
    Func<CardPoolModel> CardPool);

/// <summary>
/// Optional bridge for Archaic Tooth and Dusty Tome. The character mod remains authoritative for deciding whether
/// the current run replaces its cards and for returning its two canonical Ancient cards.
/// </summary>
public interface IExternalAncientRelicAdapter
{
    bool AppliesTo(Player player);
    bool ShouldOverrideArchaicTooth(Player player);
    bool ShouldOverrideDustyTome(Player player);
    CardModel AncientCard(Player player, int index);
}

/// <summary>
/// Run-scoped definition host for external character adapters. InstallDefinitions is deterministic replacement,
/// making it suitable for authoritative multiplayer snapshots and save restoration performed by the owning mod.
/// </summary>
public static class ExternalComponentCharacterApi
{
    public const int ApiVersion = 3;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, ExternalComponentCharacterRegistration> Registrations =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IReadOnlyList<ChaosCardDefinition>> Definitions =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ExternalComponentCharacterRuntimeRegistration> RuntimeHosts =
        new(StringComparer.Ordinal);
    private static bool _registrationsFrozen;

    public static void Register(ExternalComponentCharacterRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ValidateId(registration.ProfileId, nameof(registration.ProfileId));
        ValidateId(registration.EnergyIconPrefix, nameof(registration.EnergyIconPrefix));
        lock (Sync)
        {
            if (_registrationsFrozen)
                throw new InvalidOperationException(
                    "External character registration must finish before its first definitions are installed or read.");
            if (!Registrations.TryAdd(registration.ProfileId, registration))
                throw new InvalidOperationException(
                    $"External component character '{registration.ProfileId}' is already registered.");
        }
    }

    public static IReadOnlyList<ExternalComponentCharacterRegistration> RegisteredCharacters
    {
        get
        {
            lock (Sync)
                return Registrations.Values.OrderBy(value => value.ProfileId, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>
    /// Registers the optional game-facing half of an external character integration. This is separate from
    /// <see cref="Register(ExternalComponentCharacterRegistration)"/> so adapters compiled against API v2 keep
    /// their original constructor and behavior.
    /// </summary>
    public static void RegisterRuntime(ExternalComponentCharacterRuntimeRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ValidateId(registration.ProfileId, nameof(registration.ProfileId));
        ArgumentNullException.ThrowIfNull(registration.CardTypeForSlot);
        ArgumentNullException.ThrowIfNull(registration.IsRunActive);
        ArgumentNullException.ThrowIfNull(registration.CardPool);
        if (registration.CardCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(registration.CardCount),
                "An external character runtime host must expose at least one card slot.");

        // Resolve the types during initialization, before mutating the registry. This catches incomplete slot
        // tables atomically while avoiding ModelDb/card construction before the database is ready.
        for (var slot = 0; slot < registration.CardCount; slot++)
        {
            var cardType = registration.CardTypeForSlot(slot)
                           ?? throw new ArgumentException($"External slot {slot} returned no card type.",
                               nameof(registration));
            if (!typeof(ExternalChaosCardModel).IsAssignableFrom(cardType) || cardType.IsAbstract)
                throw new ArgumentException(
                    $"External slot {slot} type '{cardType.FullName}' must be a concrete ExternalChaosCardModel.",
                    nameof(registration));
        }

        lock (Sync)
        {
            _ = GetRegistrationLocked(registration.ProfileId);
            if (_registrationsFrozen)
                throw new InvalidOperationException(
                    "External runtime registration must finish before definitions are installed or read.");
            if (!RuntimeHosts.TryAdd(registration.ProfileId, registration))
                throw new InvalidOperationException(
                    $"External component runtime host '{registration.ProfileId}' is already registered.");
        }
    }

    public static IReadOnlyList<ExternalComponentCharacterRuntimeRegistration> RegisteredRuntimeHosts
    {
        get
        {
            lock (Sync)
                return RuntimeHosts.Values.OrderBy(value => value.ProfileId, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>Returns active external generated pools in stable profile-ID order.</summary>
    public static IReadOnlyList<CardPoolModel> GetActiveCardPools()
    {
        ExternalComponentCharacterRuntimeRegistration[] hosts;
        lock (Sync) hosts = RuntimeHosts.Values.OrderBy(value => value.ProfileId, StringComparer.Ordinal).ToArray();
        var pools = new List<CardPoolModel>(hosts.Length);
        foreach (var host in hosts)
        {
            // Do not execute another mod's delegates while holding the registry lock.
            try
            {
                if (host.IsRunActive()) pools.Add(host.CardPool());
            }
            catch (Exception exception)
            {
                Log.Error($"[AutoAnthony] External runtime host '{host.ProfileId}' failed to expose its card pool: {exception}");
            }
        }
        return pools;
    }

    public static void InstallDefinitions(string profileId, IEnumerable<ChaosCardDefinition> definitions)
    {
        ValidateId(profileId, nameof(profileId));
        ArgumentNullException.ThrowIfNull(definitions);
        var materialized = definitions.OrderBy(definition => definition.Slot).ToArray();
        lock (Sync)
        {
            var registration = GetRegistrationLocked(profileId);
            if (RuntimeHosts.TryGetValue(profileId, out var runtimeHost)
                && materialized.Length != runtimeHost.CardCount)
                throw new ArgumentException(
                    $"External definitions for '{profileId}' contain {materialized.Length} slots; "
                    + $"the runtime host declared {runtimeHost.CardCount}.", nameof(definitions));
            if (materialized.Length == 0)
                throw new ArgumentException("At least one external generated-card definition is required.",
                    nameof(definitions));
            for (var slot = 0; slot < materialized.Length; slot++)
            {
                var definition = materialized[slot];
                if (definition.Slot != slot)
                    throw new ArgumentException(
                        $"External definitions for '{profileId}' must use contiguous zero-based slots; expected {slot}, got {definition.Slot}.",
                        nameof(definitions));
                if (definition.Card.Character != registration.BalanceArchetype)
                    throw new ArgumentException(
                        $"External definition {profileId}/{slot} uses {definition.Card.Character}, expected balance archetype {registration.BalanceArchetype}.",
                        nameof(definitions));
                if (definition.RuntimeSpecs is null
                    || definition.RuntimeSpecs.Count != definition.Card.Operations.Count)
                    throw new ArgumentException(
                        $"External definition {profileId}/{slot} has incomplete RuntimeSpecs.", nameof(definitions));
                foreach (var spec in definition.RuntimeSpecs) spec.Validate();
            }
            _registrationsFrozen = true;
            Definitions[profileId] = materialized;
        }
    }

    public static void ClearDefinitions(string profileId)
    {
        ValidateId(profileId, nameof(profileId));
        lock (Sync) Definitions.Remove(profileId);
    }

    public static IReadOnlyList<ChaosCardDefinition> GetDefinitions(string profileId)
    {
        ValidateId(profileId, nameof(profileId));
        lock (Sync)
        {
            _ = GetRegistrationLocked(profileId);
            _registrationsFrozen = true;
            return Definitions.TryGetValue(profileId, out var definitions) ? definitions : [];
        }
    }

    internal static ChaosCardDefinition ForSlot(string profileId, int slot)
    {
        lock (Sync)
        {
            _ = GetRegistrationLocked(profileId);
            _registrationsFrozen = true;
            if (!Definitions.TryGetValue(profileId, out var definitions)
                || (uint)slot >= (uint)definitions.Count)
                throw new InvalidOperationException(
                    $"External component definition '{profileId}' slot {slot} is not installed.");
            return definitions[slot];
        }
    }

    internal static GeneratedCharacter BalanceArchetype(string profileId)
    {
        lock (Sync)
        {
            var registration = GetRegistrationLocked(profileId);
            _registrationsFrozen = true;
            return registration.BalanceArchetype;
        }
    }

    internal static string EnergyIconPrefix(string profileId)
    {
        lock (Sync) return GetRegistrationLocked(profileId).EnergyIconPrefix;
    }

    internal static bool TryCreateTriggeredCard(string profileId, int slot, ICombatState combatState, Player owner,
        out ChaosCardModel? card)
    {
        ExternalComponentCharacterRuntimeRegistration? host;
        lock (Sync) RuntimeHosts.TryGetValue(profileId, out host);
        if (host is null || (uint)slot >= (uint)host.CardCount)
        {
            card = null;
            return false;
        }

        try
        {
            var cardType = host.CardTypeForSlot(slot);
            var canonical = ModelDb.GetById<CardModel>(ModelDb.GetId(cardType));
            card = combatState.CreateCard(canonical, owner) as ChaosCardModel;
            return card is not null;
        }
        catch (Exception exception)
        {
            Log.Error($"[AutoAnthony] External runtime host '{profileId}' could not create slot {slot}: {exception}");
            card = null;
            return false;
        }
    }

    internal static bool IsExternalRunActive(CardModel? card)
    {
        if (card is not ExternalChaosCardModel external) return false;
        ExternalComponentCharacterRuntimeRegistration? host;
        lock (Sync) RuntimeHosts.TryGetValue(external.ExternalProfileId, out host);
        if (host is null) return false;
        try
        {
            return host.IsRunActive();
        }
        catch (Exception exception)
        {
            Log.Error($"[AutoAnthony] External runtime host '{host.ProfileId}' failed its run-active query: {exception}");
            return false;
        }
    }

    public static bool TryGetAncientRelicAdapter(Player player, out IExternalAncientRelicAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(player);
        IExternalAncientRelicAdapter[] candidates;
        lock (Sync)
            candidates = Registrations.Values.Select(registration => registration.AncientRelics)
                .Where(candidate => candidate is not null).Cast<IExternalAncientRelicAdapter>().ToArray();
        // Never invoke another mod while holding the registry lock. Character predicates may query their own
        // model registries or call back into this API during startup diagnostics.
        var matches = candidates.Where(candidate => candidate.AppliesTo(player)).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException(
                $"Multiple external component profiles claimed Ancient relic handling for {player.Character.Id}.");
        adapter = matches.SingleOrDefault()!;
        return adapter is not null;
    }

    private static ExternalComponentCharacterRegistration GetRegistrationLocked(string profileId) =>
        Registrations.TryGetValue(profileId, out var registration)
            ? registration
            : throw new InvalidOperationException(
                $"External component character '{profileId}' was not registered during mod initialization.");

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => character > 0x7f))
            throw new ArgumentException("External component IDs must be non-empty ASCII strings.", name);
    }
}

/// <summary>
/// Base class for concrete slot models declared by an external character mod. The owning mod only supplies its
/// stable profile ID, slot number and CardPoolModel; all generated description, upgrade, trigger and operation
/// execution behavior is inherited from AutoAnthony.
/// </summary>
public abstract class ExternalChaosCardModel : ChaosCardModel
{
    protected abstract string ComponentProfileId { get; }
    internal string ExternalProfileId => ComponentProfileId;
    protected sealed override string? DefinitionProfileId => ComponentProfileId;
    protected sealed override GeneratedCharacter Character =>
        ExternalComponentCharacterApi.BalanceArchetype(ComponentProfileId);
    public abstract override CardPoolModel Pool { get; }
}
