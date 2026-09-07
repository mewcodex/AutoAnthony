using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace AutoAnthony;

/// <summary>
/// Game-side bridge for the read-only native-card decomposition catalog. Merely resolving or previewing a card is
/// side-effect free; no native card or card pool is replaced by this API.
/// </summary>
public static class AutoAnthonyNativeCardApi
{
    public const int ApiVersion = 1;

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
        return decomposition is not null;
    }

    public static bool CanMaterialize(CardModel card) =>
        TryResolve(card, out var decomposition)
        && decomposition.ExecutionSupport == NativeCardExecutionSupport.ExecutableRecipe;

    public static bool CanMaterializeExact(CardModel card) =>
        TryResolve(card, out var decomposition)
        && NativeCardDecompositionApi.TryCreateDefinition(decomposition.CatalogId, out _, out _);

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
