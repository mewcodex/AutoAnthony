using ChaosCardGenerator;
using MegaCrit.Sts2.Core.HoverTips;

namespace AutoAnthony;

public sealed record ComponentPresentationContext(
    ChaosCardModel Card,
    int OperationIndex,
    GeneratorOperation Operation,
    OperationRuntimeSpec RuntimeSpec,
    bool IsUpgraded);

public interface IComponentHoverTipProvider
{
    IEnumerable<IHoverTip> BuildHoverTips(ComponentPresentationContext context);
}

public sealed record ComponentPresentationRoute(
    string Opcode,
    string Variant,
    IComponentHoverTipProvider Provider);

/// <summary>
/// Optional presentation routes for custom component references and named mechanics. Exact opcode/variant routes
/// override opcode-wide routes. Localized operation prose remains part of ComponentPackageRegistration.
/// </summary>
public static class ComponentPresentationApi
{
    public const int ApiVersion = 2;
    private static readonly object Sync = new();
    private static readonly Dictionary<(string Opcode, string Variant), IComponentHoverTipProvider> Providers = [];
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
                return Providers.Keys.OrderBy(route => route.Opcode, StringComparer.Ordinal)
                    .ThenBy(route => route.Variant, StringComparer.Ordinal).ToArray();
        }
    }

    public static IReadOnlyList<string> RegisteredPackages
    {
        get { lock (Sync) return Packages.OrderBy(value => value, StringComparer.Ordinal).ToArray(); }
    }

    public static void Register(string opcode, string variant, IComponentHoverTipProvider provider)
    {
        ValidateId(opcode, nameof(opcode), required: true);
        ValidateId(variant, nameof(variant), required: false);
        ArgumentNullException.ThrowIfNull(provider);
        lock (Sync)
        {
            if (_frozen)
                throw new InvalidOperationException(
                    "Component presentation registration must finish before generated hover tips are rendered.");
            if (!Providers.TryAdd((opcode, variant), provider))
                throw new InvalidOperationException(
                    $"A presentation provider is already registered for '{opcode}'/'{variant}'.");
        }
    }

    /// <summary>Atomically registers all presentation routes owned by one package.</summary>
    public static void RegisterPackage(string packageId, IEnumerable<ComponentPresentationRoute> routes)
    {
        ValidateId(packageId, nameof(packageId), required: true);
        ArgumentNullException.ThrowIfNull(routes);
        var values = routes.ToArray();
        if (values.Length == 0)
            throw new ArgumentException("A presentation package must contain at least one route.", nameof(routes));
        var keys = new HashSet<(string Opcode, string Variant)>();
        foreach (var route in values)
        {
            ArgumentNullException.ThrowIfNull(route);
            ValidateId(route.Opcode, nameof(route.Opcode), required: true);
            ValidateId(route.Variant, nameof(route.Variant), required: false);
            ArgumentNullException.ThrowIfNull(route.Provider);
            if (!keys.Add((route.Opcode, route.Variant)))
                throw new ArgumentException(
                    $"Presentation package '{packageId}' repeats '{route.Opcode}'/'{route.Variant}'.",
                    nameof(routes));
        }
        lock (Sync)
        {
            if (_frozen)
                throw new InvalidOperationException(
                    "Component presentation registration must finish before generated hover tips are rendered.");
            if (Packages.Contains(packageId))
                throw new InvalidOperationException($"Presentation package '{packageId}' is already registered.");
            var conflict = values.FirstOrDefault(route => Providers.ContainsKey((route.Opcode, route.Variant)));
            if (conflict is not null)
                throw new InvalidOperationException(
                    $"A presentation provider is already registered for '{conflict.Opcode}'/'{conflict.Variant}'.");
            Packages.Add(packageId);
            foreach (var route in values)
                Providers.Add((route.Opcode, route.Variant), route.Provider);
        }
    }

    internal static IEnumerable<IHoverTip> BuildHoverTips(ChaosCardModel card, int operationIndex,
        GeneratorOperation operation)
    {
        IComponentHoverTipProvider? provider;
        var spec = OperationRuntimeSpecCompiler.RequireStructured(operation);
        lock (Sync)
        {
            _frozen = true;
            if (!Providers.TryGetValue((spec.Opcode, spec.Variant), out provider))
                Providers.TryGetValue((spec.Opcode, string.Empty), out provider);
        }
        if (provider is null) return [];
        return provider.BuildHoverTips(new ComponentPresentationContext(
                   card, operationIndex, operation, spec, card.IsUpgraded))
            ?? [];
    }

    private static void ValidateId(string value, string name, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Presentation route IDs must not be empty.", name);
        if (value.Any(character => character > 0x7f))
            throw new ArgumentException("Presentation route IDs must be ASCII.", name);
    }
}
