using System.Diagnostics;

namespace ChaosCardGenerator;

/// <summary>Authoring-only assembly funnel. Calls are omitted entirely from the game assembly.</summary>
public static class AssemblySurvivalAudit
{
    public static bool Enabled { get; set; }
    private static readonly Dictionary<(string Family, string Stage), int> Counts = new();

    [Conditional("AUTHORING_CATALOGS")]
    internal static void Record(IReadOnlyList<GeneratorOperation> operations, string stage)
    {
        if (!Enabled) return;
        var totalKey = ("__assemblies", stage);
        Counts[totalKey] = Counts.GetValueOrDefault(totalKey) + 1;
        foreach (var family in operations.Select(o => o.Template == "A:rule"
                     ? o.Template + "|" + OperationRuntimeSpecCompiler.GetOrCompile(o).Variant : o.Template)
                 .Distinct(StringComparer.Ordinal))
        {
            var key = (family, stage);
            Counts[key] = Counts.GetValueOrDefault(key) + 1;
        }
    }

    public static void Reset() => Counts.Clear();
    [Conditional("AUTHORING_CATALOGS")]
    internal static void RecordFallback(GeneratedCard card, bool accepted)
    {
        if (Enabled) Record(card.Operations, "fallback:" + accepted + ":" + string.Join("|", card.Operations.Select(op =>
            op.Template + ":" + string.Join(",", OperationRuntimeSpecCompiler.GetOrCompile(op).Values.Select(v => v.Id + "=" + v.BaseValue)))));
    }
    [Conditional("AUTHORING_CATALOGS")]
    internal static void RecordFailure(IReadOnlyList<GeneratorOperation> operations, Exception error)
    {
        if (Enabled) Record(operations, "validation: " + error.Message);
    }
    public static string Report() => string.Join(Environment.NewLine, Counts
        .OrderBy(pair => pair.Key.Family, StringComparer.Ordinal)
        .ThenBy(pair => pair.Key.Stage, StringComparer.Ordinal)
        .Select(pair => $"{pair.Key.Family}\t{pair.Key.Stage}\t{pair.Value}"));
}
