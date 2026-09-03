using System.Collections.Concurrent;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace AutoAnthony;

/// <summary>
/// Bridges a generated card's stable portrait assignment back to the original card model that supplied the art.
/// Common portrait mods either overlay the vanilla resource path or patch PortraitPath. Keeping the source model
/// lets both strategies compose without taking a compile-time dependency on a particular art mod. Deliberately do
/// not forward CardModel.Portrait itself: some mods package several textures and decide which one is active later in
/// their card-UI patches, so reading the raw source texture can expose an installed-but-inactive variant.
/// </summary>
internal static class ChaosPortraitCompatibility
{
    private const string VanillaPortraitRoot = "res://images/atlases/card_atlas.sprites/";
    private static readonly ConcurrentDictionary<string, CardModel> SourceCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, CardModel> StableSourceRegistry =
        new(StringComparer.OrdinalIgnoreCase);
    // PortraitPath is queried repeatedly while cards animate, fan out in a pile, or refresh previews. Calling the
    // original source model on every query re-enters every installed portrait mod's getter patch; several of those
    // also probe ResourceLoader.Exists. Portrait resources and enabled mods are immutable for the process lifetime,
    // so resolve the winning redirected path once per native source identity.
    private static readonly ConcurrentDictionary<string, string> ResolvedPathCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Texture2D> DirectTextureCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> DirectTextureMissCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, IReadOnlyList<PortraitVariant>> VariantCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object DirectAdapterLock = new();
    private delegate bool TryGetDirectTexture(string sourceType, out Texture2D texture);
    private sealed class DirectTextureAdapter(string name, TryGetDirectTexture resolver)
    {
        public string Name { get; } = name;
        public TryGetDirectTexture Resolver { get; } = resolver;
        public bool Disabled { get; set; }
        public bool FailureLogged { get; set; }
    }

    private static DirectTextureAdapter[] _directTextureAdapters = [];
    private static bool _directAdaptersResolved;

    internal const string OriginalVariantId = "$original";
    internal const string RedirectedVariantId = "$redirected";
    internal sealed record PortraitVariant(string Id, string? Path = null);

    internal static string ResolvePath(ChaosCardDefinition definition)
    {
        var key = VisualKey(definition);
        if (ResolvedPathCache.TryGetValue(key, out var cached)) return cached;

        var resolved = ResolvePathUncached(definition);
        return ResolvedPathCache.GetOrAdd(key, resolved);
    }

    private static string ResolvePathUncached(ChaosCardDefinition definition)
    {
        if (definition.PortraitVariantId == OriginalVariantId
            || definition.PortraitVariantId?.StartsWith("direct:", StringComparison.Ordinal) == true)
            return definition.PortraitPath;
        if (definition.PortraitVariantId == RedirectedVariantId)
            return !string.IsNullOrWhiteSpace(definition.PortraitVariantPath)
                   && ResourceLoader.Exists(definition.PortraitVariantPath, "")
                ? definition.PortraitVariantPath
                : definition.PortraitPath;
        if (!TryResolveSource(definition, out var source)) return definition.PortraitPath;

        // A disabled portrait mod can leave settings behind, but its PCK is not mounted. Never return that stale
        // patched path: the generated card falls back to the deterministic vanilla atlas identity saved with it.
        var redirected = source.PortraitPath;
        return !string.IsNullOrWhiteSpace(redirected) && ResourceLoader.Exists(redirected, "")
            ? redirected
            : definition.PortraitPath;
    }

    internal static bool TryResolveSource(ChaosCardDefinition definition, out CardModel source)
    {
        var key = SourceKey(definition);
        if (SourceCache.TryGetValue(key, out source!)) return true;

        source = ResolveUncached(definition)!;
        if (source is null) return false;
        SourceCache.TryAdd(key, source);
        return true;
    }

    internal static void RegisterStableSource(string stablePath, CardModel source)
    {
        if (!string.IsNullOrWhiteSpace(stablePath)) StableSourceRegistry.TryAdd(stablePath, source);
    }

    private static string SourceKey(ChaosCardDefinition definition) =>
        !string.IsNullOrWhiteSpace(definition.PortraitSourceId)
            ? "id:" + definition.PortraitSourceId
            : "path:" + definition.PortraitPath;

    private static string VisualKey(ChaosCardDefinition definition) => SourceKey(definition)
        + "|variant:" + (definition.PortraitVariantId ?? "$winning")
        + "|variantPath:" + (definition.PortraitVariantPath ?? string.Empty);

    /// <summary>Returns every currently available visual for one native source card in deterministic load order.</summary>
    internal static IReadOnlyList<PortraitVariant> GetAvailableVariants(CardModel source, string stablePath)
    {
        ResolveDirectAdapters();
        var sourceType = source.GetType().FullName;
        if (string.IsNullOrWhiteSpace(sourceType)) return [new PortraitVariant(OriginalVariantId)];
        return VariantCache.GetOrAdd(sourceType, _ => BuildAvailableVariants(source, stablePath, sourceType));
    }

    private static IReadOnlyList<PortraitVariant> BuildAvailableVariants(
        CardModel source, string stablePath, string sourceType)
    {
        var variants = new List<PortraitVariant> { new(OriginalVariantId) };
        // Variant discovery must not eagerly load every vanilla portrait: random-art selection only needs the
        // stable original identity, while loading all 481 textures at the first pool's final progress tick caused
        // a visible main-thread hitch. Direct registries are queried lazily only for semantically top-ranked cards.
        var textureKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "path:" + stablePath };

        var redirected = source.PortraitPath;
        if (!string.IsNullOrWhiteSpace(redirected)
            && !string.Equals(redirected, stablePath, StringComparison.OrdinalIgnoreCase)
            && ResourceLoader.Exists(redirected, "")
            && textureKeys.Add("path:" + redirected))
            variants.Add(new PortraitVariant(RedirectedVariantId, redirected));

        foreach (var adapter in _directTextureAdapters)
        {
            if (adapter.Disabled) continue;
            try
            {
                if (!adapter.Resolver(sourceType, out var texture) || !GodotObject.IsInstanceValid(texture))
                    continue;
                var textureKey = !string.IsNullOrWhiteSpace(texture.ResourcePath)
                    ? "path:" + texture.ResourcePath
                    : "instance:" + texture.GetInstanceId();
                if (!textureKeys.Add(textureKey)) continue;
                variants.Add(new PortraitVariant("direct:" + adapter.Name));
            }
            catch (Exception exception)
            {
                DisableAdapter(adapter, exception);
            }
        }
        // Random-card-art mode treats an enabled replacement as the visual pool for this source card. Keeping the
        // vanilla entry beside it made a replaced card randomly fall back to its original art even though at least
        // one enabled pack supplied an alternative. Preserve vanilla only as the true no-replacement fallback.
        return variants.Count > 1
            ? variants.Where(variant => variant.Id != OriginalVariantId).ToArray()
            : variants;
    }

    /// <summary>
    /// Some portrait mods do not change <see cref="CardModel.PortraitPath"/>. Instead they replace the texture on
    /// <c>NCard</c> after vanilla has rendered it, using the original model's runtime type as the lookup key. A
    /// generated card has its own runtime type, so replay that final lookup with the saved original source type.
    /// The adapter is discovered exclusively from assemblies loaded in the current process: an installed but
    /// disabled mod cannot contribute a texture merely because its PCK or settings remain on disk.
    /// </summary>
    internal static bool TryResolveDirectTexture(ChaosCardDefinition definition, out Texture2D texture)
    {
        texture = null!;
        ResolveDirectAdapters();
        if (_directTextureAdapters.Length == 0) return false;

        if (definition.PortraitVariantId is OriginalVariantId or RedirectedVariantId) return false;

        if (!TryResolveSource(definition, out var source)) return false;
        var sourceType = source.GetType().FullName;
        if (string.IsNullOrWhiteSpace(sourceType)) return false;
        var cacheKey = sourceType + "|" + (definition.PortraitVariantId ?? "$winning");
        if (DirectTextureCache.TryGetValue(cacheKey, out texture!))
        {
            if (GodotObject.IsInstanceValid(texture)) return true;
            DirectTextureCache.TryRemove(cacheKey, out _);
        }
        if (DirectTextureMissCache.ContainsKey(cacheKey)) return false;
        // Harmony postfixes with equal priority resolve according to mod load order. Walk adapters in reverse so
        // the last enabled portrait pack wins the same source-card conflict; disabled mods have no loaded assembly
        // and therefore never enter this list.
        for (var index = _directTextureAdapters.Length - 1; index >= 0; index--)
        {
            var adapter = _directTextureAdapters[index];
            if (adapter.Disabled) continue;
            if (definition.PortraitVariantId?.StartsWith("direct:", StringComparison.Ordinal) == true
                && !string.Equals(definition.PortraitVariantId, "direct:" + adapter.Name,
                    StringComparison.Ordinal))
                continue;
            try
            {
                if (adapter.Resolver(sourceType, out var resolved)
                    && GodotObject.IsInstanceValid(resolved))
                {
                    DirectTextureCache.TryAdd(cacheKey, resolved);
                    texture = resolved;
                    return true;
                }
            }
            catch (Exception exception)
            {
                DisableAdapter(adapter, exception);
            }
        }
        // Most source cards are not replaced by any enabled direct-texture mod. Caching that ordinary miss avoids
        // invoking every external registry again on every visual refresh.
        DirectTextureMissCache.TryAdd(cacheKey, 0);
        return false;
    }

    private static void DisableAdapter(DirectTextureAdapter adapter, Exception exception)
    {
        adapter.Disabled = true;
        if (adapter.FailureLogged) return;
        adapter.FailureLogged = true;
        Log.Warn($"[AutoAnthony] Disabled direct card-art adapter {adapter.Name} after an error: "
                 + exception.GetBaseException().Message);
    }

    private static void ResolveDirectAdapters()
    {
        if (_directAdaptersResolved) return;
        lock (DirectAdapterLock)
        {
            if (_directAdaptersResolved) return;
            _directAdaptersResolved = true;

            // Direct-texture packs commonly perform their winning replacement in NCard.UpdateVisuals and expose a
            // registry keyed by the source card's runtime type. Discover that convention structurally rather than
            // maintaining a mod-name allowlist. Only already-loaded assemblies are inspected, and the narrow class
            // name + exact method signature boundary avoids invoking unrelated arbitrary mod APIs.
            var adapters = new List<DirectTextureAdapter>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!ShouldInspectAssembly(assembly))
                    continue;
                try
                {
                    foreach (var registry in LoadableTypes(assembly).Where(IsDirectCardTextureRegistry))
                    {
                        var resolver = registry.GetMethod("TryGetTexture",
                            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                            binder: null, types: [typeof(string), typeof(Texture2D).MakeByRefType()], modifiers: null)
                            ?.CreateDelegate<TryGetDirectTexture>();
                        if (resolver is not null)
                            adapters.Add(new DirectTextureAdapter(
                                (assembly.GetName().Name ?? "unknown") + ":" + registry.FullName, resolver));
                    }
                }
                catch (Exception exception)
                {
                    Log.Warn("[AutoAnthony] Could not inspect one optional card-art assembly: "
                        + exception.GetBaseException().Message);
                }
            }
            _directTextureAdapters = adapters.ToArray();
            if (_directTextureAdapters.Length > 0)
                Log.Info($"[AutoAnthony] Enabled {_directTextureAdapters.Length} cached wildcard card-art "
                    + "adapter(s) from loaded mods.");
        }
    }

    private static bool ShouldInspectAssembly(Assembly assembly)
    {
        if (assembly == typeof(ChaosPortraitCompatibility).Assembly
            || assembly == typeof(CardModel).Assembly
            || assembly.IsDynamic)
            return false;

        // Avoid reflecting over the runtime/framework surface. Card-art providers are ordinary third-party mod
        // assemblies, while this list contains only stable platform prefixes and does not name any supported mod.
        var name = assembly.GetName().Name ?? string.Empty;
        return !name.Equals("System", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("GodotSharp", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("0Harmony", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    private static bool IsDirectCardTextureRegistry(Type type)
    {
        var name = type.Name;
        return name.Equals("CardReplacementRegistry", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Registry", StringComparison.OrdinalIgnoreCase)
            && name.Contains("Card", StringComparison.OrdinalIgnoreCase)
            && (name.Contains("Portrait", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Replacement", StringComparison.OrdinalIgnoreCase));
    }

    private static CardModel? ResolveUncached(ChaosCardDefinition definition)
    {
        if (StableSourceRegistry.TryGetValue(definition.PortraitPath, out var registered)) return registered;
        // The generated art catalog is made exclusively from cards compiled into sts2.dll. Restricting recovery to
        // that same set prevents an old path such as .../strike.tres from accidentally binding to an enabled mod's
        // unrelated card whose ModelId happens to reuse the entry "Strike".
        var cards = ModelDb.AllCards.Where(card => card is not ChaosCardModel
            && card.GetType().Assembly == typeof(CardModel).Assembly).ToArray();
        if (!string.IsNullOrWhiteSpace(definition.PortraitSourceId))
        {
            var byId = cards.FirstOrDefault(card => string.Equals(card.Id.ToString(),
                definition.PortraitSourceId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null) return byId;
        }

        // v0.2.140 and older snapshots contain only the deterministic vanilla atlas path. Recover both the pool and
        // ModelId entry; matching only the filename is ambiguous across colors and mod card namespaces.
        if (!TryParseStableIdentity(definition.PortraitPath, out var pool, out var entry)) return null;
        return cards.FirstOrDefault(card => string.Equals(card.Id.Entry, entry,
                StringComparison.OrdinalIgnoreCase)
            && HasPoolTitle(card, pool));
    }

    private static bool HasPoolTitle(CardModel card, string expected)
    {
        // Base-game special/event cards and some loaded external cards are valid ModelDb entries without a card
        // pool. CardModel.Pool deliberately throws for them, so wildcard portrait recovery must treat pool lookup as
        // optional external metadata rather than letting one unrelated card abort all generated-card initialization.
        try
        {
            if (string.Equals(card.Pool?.Title, expected, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (InvalidProgramException)
        {
            // Not a pooled reward card.
        }
        try
        {
            return string.Equals(card.VisualCardPool?.Title, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidProgramException)
        {
            return false;
        }
    }

    internal static bool IsStableOriginalPath(string path) =>
        TryParseStableIdentity(path, out _, out _);

    private static bool TryParseStableIdentity(string? path, out string pool, out string entry)
    {
        pool = string.Empty;
        entry = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith(VanillaPortraitRoot, StringComparison.OrdinalIgnoreCase)
            || !normalized.EndsWith(".tres", StringComparison.OrdinalIgnoreCase))
            return false;
        var relative = normalized[VanillaPortraitRoot.Length..];
        var slash = relative.IndexOf('/');
        if (slash <= 0 || slash == relative.Length - 1 || relative.IndexOf('/', slash + 1) >= 0) return false;
        pool = relative[..slash];
        entry = relative[(slash + 1)..^".tres".Length];
        return pool.Length > 0 && entry.Length > 0;
    }
}
