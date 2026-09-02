namespace AutoAnthony;

/// <summary>
/// Per-effect logs are useful while diagnosing routing, but are too expensive for normal play. High-level
/// lifecycle, warning, error, and performance messages remain enabled. Set AUTOANTHONY_VERBOSE_RUNTIME=1 before
/// launch to restore individual trigger/effect traces.
/// </summary>
internal static class ChaosDiagnostics
{
    internal static bool VerboseRuntime { get; } = string.Equals(
        Environment.GetEnvironmentVariable("AUTOANTHONY_VERBOSE_RUNTIME"), "1", StringComparison.Ordinal);
}
