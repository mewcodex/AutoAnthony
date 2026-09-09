using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace AutoAnthony;

/// <summary>
/// Optional exact decomposition adapter for native cards owned by an external component profile. The adapter
/// supplies semantics and instance-state copying; AutoAnthony supplies the generated host, upgrade projection,
/// persistence, and runtime interpreter.
/// </summary>
public interface IExternalNativeCardAdapter
{
    string ProfileId { get; }
    bool CanHandle(CardModel source);
    bool TryCreateDefinition(CardModel source, out GeneratedCard definition);
    void CopyInstanceState(CardModel source, ChaosCardModel destination) { }
}

/// <summary>
/// Game-side bridge for the read-only native-card decomposition catalog. Merely resolving or previewing a card is
/// side-effect free; no native card or card pool is replaced by this API.
/// </summary>
public static class AutoAnthonyNativeCardApi
{
    public const int ApiVersion = 3;
    private static readonly object AdapterSync = new();
    private static readonly Dictionary<string, IExternalNativeCardAdapter> ExternalAdapters =
        new(StringComparer.Ordinal);

    public static void RegisterExternalAdapter(IExternalNativeCardAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (string.IsNullOrWhiteSpace(adapter.ProfileId) || adapter.ProfileId.Any(character => character > 0x7f))
            throw new ArgumentException("External native-card profile IDs must be non-empty ASCII strings.",
                nameof(adapter));
        if (Enum.GetValues<GeneratedCharacter>().Any(character => string.Equals(adapter.ProfileId,
                ComponentProfileRequest.BuiltInId(character), StringComparison.Ordinal)))
            throw new ArgumentException("Built-in native-card adapters cannot be replaced.", nameof(adapter));
        lock (AdapterSync)
            if (!ExternalAdapters.TryAdd(adapter.ProfileId, adapter))
                throw new InvalidOperationException(
                    $"An external native-card adapter is already registered for '{adapter.ProfileId}'.");
        AutoAnthonyEditorApi.RegisterExternalCapabilities(adapter.ProfileId,
            ExternalEditorCapabilities.NativeCardDecomposition
            | ExternalEditorCapabilities.FreeformCardCreation);
    }

    /// <summary>Whether untouched native cards should display their reviewed component projection.</summary>
    public static bool ComponentDescriptionsEnabled =>
        ChaosModSettings.Enabled && ChaosModSettings.DecomposeOriginalCards;

    public static bool TryResolve(CardModel card, out NativeCardDecomposition decomposition)
    {
        ArgumentNullException.ThrowIfNull(card);
        var bindings = card is MadScience madScience
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CardType"] = madScience.TinkerTimeType.ToString(),
                ["Rider"] = madScience.TinkerTimeRider.ToString()
            }
            : null;
        decomposition = NativeCardDecompositionApi.Resolve(card.Id.Entry, bindings)!;
        if (decomposition is null
            || !string.Equals(decomposition.ClassName, card.GetType().Name, StringComparison.Ordinal))
        {
            decomposition = null!;
            return false;
        }
        return true;
    }

    public static bool CanMaterialize(CardModel card) =>
        TryResolve(card, out var decomposition)
        && decomposition.ExecutionSupport == NativeCardExecutionSupport.ExecutableRecipe;

    public static bool CanMaterializeExact(CardModel card) =>
        TryResolve(card, out var decomposition)
        && NativeCardDecompositionApi.TryCreateDefinition(decomposition.CatalogId, out _, out _);

    public static bool TryGetComponentDescriptionTemplate(CardModel card, bool upgraded, bool chinese,
        out string template)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!TryResolve(card, out var decomposition))
            return TryGetExternalComponentDescriptionTemplate(card, upgraded, chinese, out template);
        return NativeCardDecompositionApi.TryCreateComponentDescriptionTemplate(
            decomposition.CatalogId, upgraded, chinese, out template);
    }

    /// <summary>Resolves either a built-in or external native card to an executable component definition.</summary>
    public static bool TryCreateDefinition(CardModel card, out string profileId, out GeneratedCard definition)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (TryResolve(card, out var decomposition)
            && NativeCardDecompositionApi.TryCreateDefinition(decomposition.CatalogId, out definition, out _))
        {
            profileId = ComponentProfileRequest.BuiltInId(definition.Character);
            return true;
        }
        if (TryExternalAdapter(card, out var adapter)
            && adapter.TryCreateDefinition(card, out definition))
        {
            if (!ComponentApi.TryGetProfileRequest(adapter.ProfileId, false, out var request)
                || definition.Character != request.Character)
                throw new InvalidDataException(
                    $"External native-card adapter '{adapter.ProfileId}' returned a definition for another profile.");
            profileId = adapter.ProfileId;
            definition = OperationRuntimeSpecCompiler.Attach(definition);
            return true;
        }
        profileId = string.Empty;
        definition = null!;
        return false;
    }

    public static bool TryGetExternalComponentDescriptionTemplate(CardModel card, bool upgraded, bool chinese,
        out string template)
    {
        if (!TryExternalAdapter(card, out var adapter)
            || !adapter.TryCreateDefinition(card, out var definition))
        {
            template = string.Empty;
            return false;
        }
        var operations = OperationRuntimeSpecCompiler.Attach(definition.Operations);
        if (upgraded && definition.Upgrade is { } upgrade)
            operations = CardUpgradeGenerator.ApplyEffectsToOperations(operations, upgrade.Effects);
        template = chinese
            ? CardDescriptionRenderer.Render(operations)
            : EnglishCardDescriptionRenderer.Render(operations);
        return true;
    }

    /// <summary>
    /// Creates a detached base-definition preview for an executable native recipe. It does not copy upgrade,
    /// enchantment, affliction, or pile state and therefore must not be inserted into a run directly.
    /// </summary>
    public static bool TryCreateBasePreview(Player owner, CardModel source, out ChaosCardModel preview)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(source);
        if (!TryResolve(source, out var decomposition)
            || !NativeCardDecompositionApi.TryCreateDefinition(
                decomposition.CatalogId, out var definition, out _))
        {
            preview = null!;
            return false;
        }
        preview = AutoAnthonyFreeformCardApi.CreatePreview(owner, definition, source.PortraitPath);
        for (var level = 0; level < source.CurrentUpgradeLevel && preview.IsUpgradable; level++)
        {
            preview.UpgradeInternal();
            preview.FinalizeUpgradeInternal();
        }
        CopyPermanentGrowth(source, preview);
        CopyLocalKeywordDelta(source, preview);
        return true;
    }

    /// <summary>Creates a detached preview from either the built-in catalog or a registered external adapter.</summary>
    public static bool TryCreateProfilePreview(Player owner, CardModel source, out ChaosCardModel preview)
    {
        if (TryCreateBasePreview(owner, source, out preview)) return true;
        if (!TryExternalAdapter(source, out var adapter)
            || !adapter.TryCreateDefinition(source, out var definition))
        {
            preview = null!;
            return false;
        }
        preview = AutoAnthonyFreeformCardApi.CreatePreview(owner, adapter.ProfileId, definition,
            source.PortraitPath);
        for (var level = 0; level < source.CurrentUpgradeLevel && preview.IsUpgradable; level++)
        {
            preview.UpgradeInternal();
            preview.FinalizeUpgradeInternal();
        }
        adapter.CopyInstanceState(source, preview);
        CopyLocalKeywordDelta(source, preview);
        return true;
    }

    private static bool TryExternalAdapter(CardModel card, out IExternalNativeCardAdapter adapter)
    {
        IExternalNativeCardAdapter[] adapters;
        lock (AdapterSync)
            adapters = ExternalAdapters.Values.OrderBy(candidate => candidate.ProfileId, StringComparer.Ordinal)
                .ToArray();
        var matches = adapters.Where(candidate => candidate.CanHandle(card)).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException(
                $"Multiple external native-card adapters claimed {card.Id}: "
                + string.Join(", ", matches.Select(match => match.ProfileId)));
        adapter = matches.SingleOrDefault()!;
        return adapter is not null;
    }

    private static void CopyPermanentGrowth(CardModel source, ChaosCardModel preview)
    {
        var operations = preview.Generated.Operations;
        if (operations.Any(operation => operation.Template == "D:IncreaseThisCardBlockRun"))
        {
            var block = operations.Select((operation, index) => (operation, index))
                .FirstOrDefault(item => item.operation.Template == "N:B");
            if (block.operation is not null
                && TryGrowthDelta(source, preview, "Block", block.index, out var extraBlock))
                preview.ExtraBlock = extraBlock;
        }

        if (operations.Any(operation => operation.Template == "NCR:IncreaseThisCardDamageRun"))
        {
            var damage = operations.Select((operation, index) => (operation, index))
                .FirstOrDefault(item => CardEffectRules.IsEnemyDamage(item.operation));
            if (damage.operation is not null
                && TryGrowthDelta(source, preview, "Damage", damage.index, out var extraDamage))
                preview.ExtraDamage = extraDamage;
        }
    }

    private static bool TryGrowthDelta(CardModel source, ChaosCardModel preview, string nativeVariable,
        int operationIndex, out int growth)
    {
        growth = 0;
        if ((uint)operationIndex >= (uint)preview.Generated.Operations.Count
            || !source.DynamicVars.TryGetValue(nativeVariable, out var current)) return false;
        var generatedName = ChaosOperationVariables.Name(preview.Generated.Operations[operationIndex], operationIndex);
        if (!preview.DynamicVars.TryGetValue(generatedName, out var baseline)) return false;
        growth = Math.Max(0, decimal.ToInt32(current.BaseValue - baseline.BaseValue));
        return growth > 0;
    }

    /// <summary>
    /// Replays only instance-owned keyword differences. Combat-global keywords and enchantment behavior must never
    /// be baked into the serialized component shell; the real enchantment is copied when a changed card commits.
    /// </summary>
    public static void CopyLocalKeywordDelta(CardModel source, CardModel destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var expected = source.GetKeywordsWithSources(KeywordSources.Local);
        var actual = destination.GetKeywordsWithSources(KeywordSources.Local).ToArray();
        foreach (var keyword in actual)
            if (!expected.Contains(keyword)) destination.RemoveKeyword(keyword);
        foreach (var keyword in expected)
            if (!destination.GetKeywordsWithSources(KeywordSources.Local).Contains(keyword))
                destination.AddKeyword(keyword);
    }
}
