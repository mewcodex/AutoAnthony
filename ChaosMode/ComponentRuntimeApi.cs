using System.Collections.ObjectModel;
using ChaosCardGenerator;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace AutoAnthony;

/// <summary>
/// Public, localization-independent view of one generated operation execution. Runtime handlers receive the
/// resolved numeric amount and target after ordinary dependency/X scaling, plus controlled access to the small
/// amount of cross-operation state used by later effects.
/// </summary>
public sealed class ComponentRuntimeContext
{
    private readonly ChaosExecutionState _state;

    public CardModel Card { get; }
    public int OperationIndex { get; }
    public GeneratorOperation Operation { get; }
    public OperationRuntimeSpec RuntimeSpec { get; }
    public int Amount { get; }
    public PlayerChoiceContext ChoiceContext { get; }
    public CardPlay CardPlay { get; }
    public Creature? Target => _state.Target;
    public CardModel? EventCard => _state.EventCard;
    public decimal EventAmount => _state.EventAmount;
    public bool IsTriggered => _state.IsTriggered;
    public int LastDamageDealt => _state.LastDamageDealt;
    public IReadOnlyDictionary<string, CardModel> CardSlots { get; }

    internal ComponentRuntimeContext(ChaosCardModel card, int operationIndex, GeneratorOperation operation,
        OperationRuntimeSpec runtimeSpec, int amount, PlayerChoiceContext choiceContext, CardPlay cardPlay,
        ChaosExecutionState state)
    {
        Card = card;
        OperationIndex = operationIndex;
        Operation = operation;
        RuntimeSpec = runtimeSpec;
        Amount = amount;
        ChoiceContext = choiceContext;
        CardPlay = cardPlay;
        _state = state;
        CardSlots = new ReadOnlyDictionary<string, CardModel>(state.CardSlots);
    }

    public void RecordDamageDealt(int amount) => _state.LastDamageDealt = Math.Max(0, amount);

    public void ReplaceDrawnCards(IEnumerable<CardModel> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        _state.LastDrawnCards.Clear();
        _state.LastDrawnCards.AddRange(cards);
    }

    public void SetCardSlot(string slot, CardModel card)
    {
        if (string.IsNullOrWhiteSpace(slot) || slot.Any(value => value > 0x7f))
            throw new ArgumentException("Runtime card-slot IDs must be non-empty ASCII strings.", nameof(slot));
        ArgumentNullException.ThrowIfNull(card);
        _state.CardSlots[slot] = card;
    }

    /// <summary>
    /// Requests the same deferred, play-phase-safe end-turn path used by built-in operations. Calls outside the
    /// player's card-play phase resolve as a no-op after sibling effects, avoiding combat-state re-entry.
    /// </summary>
    public void RequestEndTurn() => _state.EndTurnRequested = true;
}

public interface IComponentRuntimeHandler
{
    /// <returns>True when the handler completed the operation; false to continue through compatibility fallback.</returns>
    Task<bool> ExecuteAsync(ComponentRuntimeContext context);
}

public sealed record ComponentRuntimeRoute(
    string Opcode,
    string Variant,
    IComponentRuntimeHandler Handler);

/// <summary>
/// Runtime implementation registry for new structured component routes. Exact opcode+variant handlers win over an
/// opcode-wide handler registered with an empty variant. Registration freezes on the first generated operation
/// execution so combat behavior cannot change halfway through a run or between multiplayer state syncs.
/// </summary>
public static class ComponentRuntimeApi
{
    public const int ApiVersion = 1;
    private static readonly object Sync = new();
    private static readonly Dictionary<(string Opcode, string Variant), IComponentRuntimeHandler> Handlers = new();
    private static readonly HashSet<string> Packages = new(StringComparer.Ordinal);
    private static bool _frozen;

    public static bool RegistrationsFrozen
    {
        get { lock (Sync) return _frozen; }
    }

    public static IReadOnlyList<(string Opcode, string Variant)> RegisteredRoutes
    {
        get
        {
            lock (Sync)
                return Handlers.Keys.OrderBy(route => route.Opcode, StringComparer.Ordinal)
                    .ThenBy(route => route.Variant, StringComparer.Ordinal).ToArray();
        }
    }

    public static void Register(string opcode, string variant, IComponentRuntimeHandler handler)
    {
        ValidateId(opcode, nameof(opcode), required: true);
        ValidateId(variant, nameof(variant), required: false);
        ArgumentNullException.ThrowIfNull(handler);
        lock (Sync)
        {
            if (_frozen)
                throw new InvalidOperationException(
                    "Runtime handler registration must finish before the first generated card operation executes.");
            if (!Handlers.TryAdd((opcode, variant), handler))
                throw new InvalidOperationException(
                    $"A runtime handler is already registered for '{opcode}'/'{variant}'.");
        }
    }

    /// <summary>Atomically validates and registers every runtime route owned by one external package.</summary>
    public static void RegisterPackage(string packageId, IEnumerable<ComponentRuntimeRoute> routes)
    {
        ValidateId(packageId, nameof(packageId), required: true);
        ArgumentNullException.ThrowIfNull(routes);
        var materialized = routes.ToArray();
        if (materialized.Length == 0)
            throw new ArgumentException("A runtime package must contain at least one route.", nameof(routes));
        var keys = new HashSet<(string Opcode, string Variant)>();
        foreach (var route in materialized)
        {
            ArgumentNullException.ThrowIfNull(route);
            ValidateId(route.Opcode, nameof(route.Opcode), required: true);
            ValidateId(route.Variant, nameof(route.Variant), required: false);
            ArgumentNullException.ThrowIfNull(route.Handler);
            if (!keys.Add((route.Opcode, route.Variant)))
                throw new ArgumentException(
                    $"Runtime package '{packageId}' repeats '{route.Opcode}'/'{route.Variant}'.", nameof(routes));
        }

        lock (Sync)
        {
            if (_frozen)
                throw new InvalidOperationException(
                    "Runtime handler registration must finish before the first generated card operation executes.");
            if (Packages.Contains(packageId))
                throw new InvalidOperationException($"Runtime package '{packageId}' is already registered.");
            var conflict = materialized.FirstOrDefault(route => Handlers.ContainsKey((route.Opcode, route.Variant)));
            if (conflict is not null)
                throw new InvalidOperationException(
                    $"A runtime handler is already registered for '{conflict.Opcode}'/'{conflict.Variant}'.");
            Packages.Add(packageId);
            foreach (var route in materialized) Handlers.Add((route.Opcode, route.Variant), route.Handler);
        }
    }

    public static bool HasRoute(string opcode, string variant = "")
    {
        ValidateId(opcode, nameof(opcode), required: true);
        ValidateId(variant, nameof(variant), required: false);
        lock (Sync)
            return Handlers.ContainsKey((opcode, variant))
                || variant.Length > 0 && Handlers.ContainsKey((opcode, string.Empty));
    }

    internal static async Task<bool> TryExecute(ChaosCardModel card, int operationIndex,
        GeneratorOperation operation, OperationRuntimeSpec runtimeSpec, int amount,
        PlayerChoiceContext choiceContext, CardPlay cardPlay, ChaosExecutionState state)
    {
        IComponentRuntimeHandler? handler;
        lock (Sync)
        {
            _frozen = true;
            if (!Handlers.TryGetValue((runtimeSpec.Opcode, runtimeSpec.Variant), out handler))
                Handlers.TryGetValue((runtimeSpec.Opcode, string.Empty), out handler);
        }
        if (handler is null) return false;
        var context = new ComponentRuntimeContext(card, operationIndex, operation, runtimeSpec, amount,
            choiceContext, cardPlay, state);
        return await handler.ExecuteAsync(context);
    }

    private static void ValidateId(string value, string name, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Runtime route IDs must not be empty.", name);
        if (value.Any(character => character > 0x7f))
            throw new ArgumentException("Runtime route IDs must be ASCII.", name);
    }
}
