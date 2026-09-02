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

/// <summary>
/// Optional presentation routes for custom component references and named mechanics. Exact opcode/variant routes
/// override opcode-wide routes. Localized operation prose remains part of ComponentPackageRegistration.
/// </summary>
public static class ComponentPresentationApi
{
    public const int ApiVersion = 1;
    private static readonly object Sync = new();
    private static readonly Dictionary<(string Opcode, string Variant), IComponentHoverTipProvider> Providers = [];
    private static bool _frozen;

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

    internal static IEnumerable<IHoverTip> BuildHoverTips(ChaosCardModel card, int operationIndex,
        GeneratorOperation operation)
    {
        IComponentHoverTipProvider? provider;
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
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
