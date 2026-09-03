namespace ChaosCardGenerator;

public sealed record ComponentValuationContext(
    string Template,
    OperationScope Scope,
    OperationRuntimeSpec RuntimeSpec)
{
    public int Value(string slotId, int fallback = 1) =>
        RuntimeSpec.Values.FirstOrDefault(value => value.Id == slotId)?.BaseValue ?? fallback;

    public int FirstExplicitFixedValue(int fallback = 1) => RuntimeSpec.Values
        .FirstOrDefault(value => value.Explicit && value.Source == "fixed")?.BaseValue ?? fallback;
}

public interface IComponentValuation
{
    /// <summary>Returns effect value in hundredths of one point of ordinary single-target damage.</summary>
    int Estimate(ComponentValuationContext context);
}

public sealed record ComponentValuationRegistration(
    string Opcode,
    string Variant,
    IComponentValuation Valuation,
    double NegativeLinearValuePerUnit = 0d,
    double NegativeMultiplier = 1d);

/// <summary>
/// Structured valuation routes for custom opcodes. This is deliberately independent of occurrence probability and
/// numeric sampling. Exact opcode/variant routes win over opcode-wide routes.
/// </summary>
public static class ComponentValuationApi
{
    public const int ApiVersion = 1;
    private static readonly object Sync = new();
    private static readonly Dictionary<(string Opcode, string Variant), ComponentValuationRegistration> Routes = [];
    private static bool _frozen;

    public static IReadOnlyList<(string Opcode, string Variant)> RegisteredRoutes
    {
        get
        {
            lock (Sync)
                return Routes.Keys.OrderBy(route => route.Opcode, StringComparer.Ordinal)
                    .ThenBy(route => route.Variant, StringComparer.Ordinal).ToArray();
        }
    }

    public static void Register(ComponentValuationRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ValidateId(registration.Opcode, nameof(registration.Opcode), required: true);
        ValidateId(registration.Variant, nameof(registration.Variant), required: false);
        ArgumentNullException.ThrowIfNull(registration.Valuation);
        if (registration.NegativeLinearValuePerUnit < 0d || registration.NegativeMultiplier < 1d
            || registration.NegativeLinearValuePerUnit > 0d && registration.NegativeMultiplier > 1d)
            throw new ArgumentException(
                "Custom downside pricing must choose either a non-negative linear value or one multiplier >= 1.",
                nameof(registration));
        lock (Sync)
        {
            if (_frozen)
                throw new InvalidOperationException(
                    "Component valuation registration must finish before the first generation profile resolves.");
            if (!Routes.TryAdd((registration.Opcode, registration.Variant), registration))
                throw new InvalidOperationException(
                    $"A valuation is already registered for '{registration.Opcode}'/'{registration.Variant}'.");
        }
    }

    internal static void EnsureCanRegister(IEnumerable<ComponentValuationRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var values = registrations.ToArray();
        lock (Sync)
        {
            if (_frozen)
                throw new InvalidOperationException(
                    "Component valuation registration must finish before the first generation profile resolves.");
            var conflict = values.FirstOrDefault(value => Routes.ContainsKey((value.Opcode, value.Variant)));
            if (conflict is not null)
                throw new InvalidOperationException(
                    $"A valuation is already registered for '{conflict.Opcode}'/'{conflict.Variant}'.");
        }
    }

    internal static void FreezeRegistrations()
    {
        lock (Sync) _frozen = true;
    }

    internal static bool TryEstimate(string template, OperationScope scope, OperationRuntimeSpec spec,
        out int value)
    {
        ComponentValuationRegistration? registration;
        lock (Sync)
        {
            if (!Routes.TryGetValue((spec.Opcode, spec.Variant), out registration))
                Routes.TryGetValue((spec.Opcode, string.Empty), out registration);
        }
        if (registration is null)
        {
            value = 0;
            return false;
        }
        value = registration.Valuation.Estimate(new ComponentValuationContext(template, scope, spec));
        if (value <= 0)
            throw new InvalidOperationException(
                $"Custom valuation for '{spec.Opcode}'/'{spec.Variant}' returned non-positive value {value}.");
        return true;
    }

    internal static bool IsNegative(OperationRuntimeSpec spec) =>
        TryGetRegistration(spec, out var registration)
        && (registration.NegativeLinearValuePerUnit > 0d || registration.NegativeMultiplier > 1d);

    internal static bool IsRegisteredBenefit(OperationRuntimeSpec spec) =>
        TryGetRegistration(spec, out var registration)
        && registration.NegativeLinearValuePerUnit == 0d && registration.NegativeMultiplier <= 1d;

    internal static bool TryGetNegativePricing(OperationRuntimeSpec spec, out double linearValuePerUnit,
        out double multiplier)
    {
        if (TryGetRegistration(spec, out var registration))
        {
            linearValuePerUnit = registration.NegativeLinearValuePerUnit;
            multiplier = registration.NegativeMultiplier;
            return linearValuePerUnit > 0d || multiplier > 1d;
        }
        linearValuePerUnit = 0d;
        multiplier = 1d;
        return false;
    }

    private static bool TryGetRegistration(OperationRuntimeSpec spec,
        out ComponentValuationRegistration registration)
    {
        lock (Sync)
        {
            if (Routes.TryGetValue((spec.Opcode, spec.Variant), out registration!)) return true;
            return Routes.TryGetValue((spec.Opcode, string.Empty), out registration!);
        }
    }

    private static void ValidateId(string value, string name, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Valuation route IDs must not be empty.", name);
        if (value.Any(character => character > 0x7f))
            throw new ArgumentException("Valuation route IDs must be ASCII.", name);
    }
}
