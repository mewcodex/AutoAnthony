$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '../ChaosMode/ChaosPoolSnapshot.cs') -Raw
function Extract-Method($startText, $endText) {
    $start = $source.IndexOf($startText)
    $end = $source.IndexOf($endText, $start)
    if ($start -lt 0 -or $end -lt 0) { throw 'Cache method not found' }
    return $source.Substring($start, $end - $start)
}
$prime = Extract-Method '    public static void PrimeRunPayload(' '    public static SerializableModifier ToCachedSerializableModifier('
$match = Extract-Method '    internal static bool IsCachedRunPayload(' '    private static string GetOrCreateRunPayload('
$support = @'
#nullable enable annotations
using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
public enum GeneratedCharacter { Ironclad }
public record ChaosCardDefinition(int Slot);
public static class Log { public static void Info(string message) {} }
public static class ChaosRunDefinitions {
    public static GeneratedCharacter[] SupportedPools = [GeneratedCharacter.Ironclad];
    public static bool AncientFuelActive, ActiveUltimateChaos, ActiveReplaceStartingCards,
        ActiveNumericBalanceOptimization, ActiveNumericRandomMode, ActivePreserveOriginalCards;
}
public static class CacheProbe {
    private record CachedRunPayload(GeneratedCharacter[] ActiveCharacters, string Seed,
        bool AncientFuel, bool UltimateChaos, bool ReplaceStartingCards, bool NumericBalanceOptimization,
        bool NumericRandomMode, bool PreserveOriginalCards,
        IReadOnlyList<IReadOnlyList<ChaosCardDefinition>> PoolReferences, string Payload, string RestoredPayload = null);
    static readonly object PayloadCacheGate = new();
    static CachedRunPayload _cachedRunPayload;
    static GeneratedCharacter[] NormalizeCharacters(IEnumerable<GeneratedCharacter> chars) => chars.Distinct().OrderBy(c=>c).ToArray();
    static string GetOrCreateRunPayload(IReadOnlyCollection<GeneratedCharacter> chars, string seed,
        IReadOnlyDictionary<GeneratedCharacter,IReadOnlyList<ChaosCardDefinition>> pools, out bool rebuilt) {
        rebuilt = true;
        _cachedRunPayload = new(NormalizeCharacters(chars),seed,false,false,false,false,false,false,
            ChaosRunDefinitions.SupportedPools.Select(c=>pools[c]).ToArray(),"current");
        return "current";
    }
    public static void Run() {
        var chars = ChaosRunDefinitions.SupportedPools;
        var pools = new Dictionary<GeneratedCharacter,IReadOnlyList<ChaosCardDefinition>> { [chars[0]] = new[] { new ChaosCardDefinition(0) } };
        PrimeRunPayload(chars,"seed",pools,"validated-older-encoding");
        if (!IsCachedRunPayload(chars,"seed",pools,"validated-older-encoding")
            || !IsCachedRunPayload(chars,"seed",pools,"current")
            || IsCachedRunPayload(chars,"different",pools,"validated-older-encoding")
            || IsCachedRunPayload(chars,"seed",pools,"unvalidated")) throw new Exception("Cache identity");
        pools[chars[0]] = pools[chars[0]].ToArray();
        if (IsCachedRunPayload(chars,"seed",pools,"validated-older-encoding")) throw new Exception("Stale pool accepted");
        PrimeRunPayload(chars,"seed",pools);
        if (IsCachedRunPayload(chars,"seed",pools,"validated-older-encoding")) throw new Exception("Alias survived replacement");
    }
'@
Add-Type -TypeDefinition ($support + $prime + $match + '}')
[CacheProbe]::Run()
Write-Output 'Restored snapshot cache identity/invalidation regressions passed (no network session).'
