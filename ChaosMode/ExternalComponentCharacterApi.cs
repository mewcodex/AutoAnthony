using ChaosCardGenerator;
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
    string EnergyIconPrefix);

/// <summary>
/// Run-scoped definition host for external character adapters. InstallDefinitions is deterministic replacement,
/// making it suitable for authoritative multiplayer snapshots and save restoration performed by the owning mod.
/// </summary>
public static class ExternalComponentCharacterApi
{
    public const int ApiVersion = 1;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, ExternalComponentCharacterRegistration> Registrations =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IReadOnlyList<ChaosCardDefinition>> Definitions =
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

    public static void InstallDefinitions(string profileId, IEnumerable<ChaosCardDefinition> definitions)
    {
        ValidateId(profileId, nameof(profileId));
        ArgumentNullException.ThrowIfNull(definitions);
        var materialized = definitions.OrderBy(definition => definition.Slot).ToArray();
        lock (Sync)
        {
            _registrationsFrozen = true;
            var registration = GetRegistrationLocked(profileId);
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
            _registrationsFrozen = true;
            _ = GetRegistrationLocked(profileId);
            return Definitions.TryGetValue(profileId, out var definitions) ? definitions : [];
        }
    }

    internal static ChaosCardDefinition ForSlot(string profileId, int slot)
    {
        lock (Sync)
        {
            _registrationsFrozen = true;
            _ = GetRegistrationLocked(profileId);
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
            _registrationsFrozen = true;
            return GetRegistrationLocked(profileId).BalanceArchetype;
        }
    }

    internal static string EnergyIconPrefix(string profileId)
    {
        lock (Sync) return GetRegistrationLocked(profileId).EnergyIconPrefix;
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
    protected sealed override string? DefinitionProfileId => ComponentProfileId;
    protected sealed override GeneratedCharacter Character =>
        ExternalComponentCharacterApi.BalanceArchetype(ComponentProfileId);
    public abstract override CardPoolModel Pool { get; }
}
