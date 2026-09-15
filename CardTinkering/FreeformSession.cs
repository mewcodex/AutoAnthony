using AutoAnthony;
using ChaosCardGenerator;
using static AutoAnthonyCardTinkering.TinkeringText;

namespace AutoAnthonyCardTinkering;

internal sealed class FreeformComponent
{
    internal required string Id { get; init; }
    internal required GeneratorOperation Operation { get; set; }
    internal required bool RequiresAttackSlot { get; init; }
    internal string? ParentId { get; set; }
    internal int Order { get; set; }
    internal bool InBackpack { get; set; }
}

/// <summary>
/// Pure, game-node-free state for the freeform workbench. Legality follows CardTinkeringApi plus the same reviewed
/// self-contained-native-operation adaptations used by the normal editor.
/// </summary>
internal sealed class FreeformSession
{
    private readonly Dictionary<string, FreeformComponent> _components = new(StringComparer.Ordinal);
    private int _nextId;

    internal FreeformSession()
    {
        Shell = new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Common,
            string.Empty, [], [], new GeneratedCardName("新造卡牌", "New Creation", []),
            EnglishDescription: string.Empty, Character: GeneratedCharacter.Ironclad);
    }

    internal GeneratedCard Shell { get; private set; }
    internal AutoAnthonyEditorIdentity? Identity { get; private set; }
    internal string? PortraitPath => Identity is { } identity
        ? identity.PortraitVariantPath ?? identity.PortraitPath
        : null;
    internal IReadOnlyCollection<FreeformComponent> Components => _components.Values;
    internal IEnumerable<FreeformComponent> Roots(string? parentId = null, bool backpack = false) =>
        _components.Values.Where(component => component.InBackpack == backpack && component.ParentId == parentId)
            .OrderBy(component => component.Order).ThenBy(component => component.Id, StringComparer.Ordinal);
    internal FreeformComponent Get(string id) => _components[id];

    internal GeneratedCard Preview
    {
        get
        {
            var operations = Flatten();
            var target = Shell.Type == GeneratedCardType.Power
                ? TargetMode.Other
                : CardEffectRules.RequiresCardSelectedEnemyTarget(operations)
                    ? TargetMode.SingleEnemy : TargetMode.Other;
            return OperationRuntimeSpecCompiler.Attach(Shell with
            {
                Target = target,
                Operations = operations,
                ChineseDescription = CardDescriptionRenderer.Render(operations),
                EnglishDescription = EnglishCardDescriptionRenderer.Render(operations),
                Upgrade = null,
                Tags = Shell.Tags.Distinct().OrderBy(tag => tag).ToArray(),
                CustomKeywords = (Shell.CustomKeywords ?? []).Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal).ToArray()
            });
        }
    }

    internal CardTinkeringValidationResult Validation =>
        TrainingSession.ValidateAssembly(Preview, Preview.Operations);

    /// <summary>
    /// Freeform creation deliberately does not enforce the production capacity limit, but it still exposes the
    /// exact production valuation envelope so every chip and the top-line budget remain auditable. A freeform shell
    /// has no sampled per-card capacity, so its current ordinary upper bound is the capacity shown to the player.
    /// </summary>
    internal IReadOnlyList<CardAnalysisLine> AnalysisLines()
    {
        var preview = Preview;
        var balanced = ChaosRunDefinitions.ActiveNumericBalanceOptimization;
        if (CardTinkeringApi.IsVariableX(preview))
        {
            var x0 = CardTinkeringApi.AnalyzeAtX(preview, 0, balanced);
            var x3 = CardTinkeringApi.AnalyzeAtX(preview, 3, balanced);
            return
            [
                new CardAnalysisLine("f(0)", x0, Capacity(x0.Budget.OrdinaryUpperBound)),
                new CardAnalysisLine("f(3)", x3, Capacity(x3.Budget.OrdinaryUpperBound))
            ];
        }
        var analysis = TinkeringValue.Evaluate(preview);
        return [new CardAnalysisLine(string.Empty, analysis, Capacity(analysis.Budget.OrdinaryUpperBound))];
    }

    internal IReadOnlyList<CardBudgetLine> BudgetAmounts() => AnalysisLines().Select(line =>
        new CardBudgetLine(line.Label,
            line.Analysis.Budget.PositiveValue - line.Analysis.Budget.LinearCompensation,
            line.ShellCapacity * Math.Max(1d, line.Analysis.Budget.DownsideMultiplier),
            line.ShellCapacity)).ToArray();

    internal IReadOnlyList<ComponentAnalysisLine> ComponentValues(string componentId)
    {
        var flattened = FlattenWithComponents();
        var index = flattened.FindIndex(item => item.Component?.Id == componentId);
        if (index < 0) return [];
        var preview = Preview;
        return AnalysisLines().Where(line => (uint)index < (uint)line.Analysis.Components.Count)
            .Select(line => new ComponentAnalysisLine(line.Label,
                TrainingSession.DisplayComponents(preview, line)[index]))
            .ToArray();
    }

    internal IReadOnlyList<TriggerAnalysisLine> TriggerMultipliers(string componentId)
    {
        var flattened = FlattenWithComponents();
        var index = flattened.FindIndex(item => item.Component?.Id == componentId);
        if (index < 0) return [];
        var preview = Preview;
        var hasParent = flattened[index].Operation.Parameters.TryGetValue("triggerIndex", out var parentIndex)
                        && parentIndex >= 0 && parentIndex < index;
        var hasLinkedChild = flattened.Skip(index + 1).Any(item =>
                                 item.Operation.Parameters.GetValueOrDefault("triggerIndex", -1) == index)
                             || CardEffectRules.IsDependencyPrefix(flattened[index].Operation)
                             && index + 1 < flattened.Count
                             && CardEffectRules.IsLegalDependencyPayoff(
                                 flattened[index].Operation, flattened[index + 1].Operation);
        return AnalysisLines()
            .Where(line => (uint)index < (uint)line.Analysis.Components.Count
                           && line.Analysis.Components[index].IsTrigger)
            .Select(line =>
            {
                var nested = line.Analysis.Components[index].TriggerMultiplier;
                if (!hasLinkedChild)
                    nested = TrainingSession.EmptyTriggerMultiplier(preview, index, line.Label, nested);
                if (!hasParent || (uint)parentIndex >= (uint)line.Analysis.Components.Count)
                    return new TriggerAnalysisLine(line.Label, nested, nested, false);
                var parentNested = line.Analysis.Components[parentIndex].TriggerMultiplier;
                if (!double.IsFinite(parentNested) || Math.Abs(parentNested) <= 0.0001d)
                    return new TriggerAnalysisLine(line.Label, nested, nested, true);
                return new TriggerAnalysisLine(line.Label, nested / parentNested, nested, true);
            }).ToArray();
    }

    internal IReadOnlyList<FormulaBadge> ComponentFormula(string componentId,
        CardTinkeringComponentAnalysis fallback, IReadOnlyList<ComponentAnalysisLine>? currentValues = null)
    {
        var flattened = FlattenWithComponents();
        var index = flattened.FindIndex(item => item.Component?.Id == componentId);
        if (index < 0) return [];
        var downside = new ComponentDownsidePricing(fallback.IsNegative,
            fallback.DownsideMultiplier, fallback.LinearCompensation);
        return SpecialValuePricing.Resolve(Shell, flattened.Select(item => item.Operation).ToArray(), index,
            fallback, downside, currentValues ?? ComponentValues(componentId));
    }

    internal IReadOnlyList<KeywordAnalysisLine> KeywordValues(CardTag tag, bool includeWhenMissing)
    {
        var preview = Preview;
        if (!preview.Tags.Contains(tag))
        {
            if (!includeWhenMissing) return [];
            preview = preview with { Tags = preview.Tags.Append(tag).Distinct().ToArray() };
        }
        var labels = CardTinkeringApi.IsVariableX(preview) ? new[] { "f(0)", "f(3)" } : [string.Empty];
        return labels.Select(label =>
        {
            try
            {
                return new KeywordAnalysisLine(label, CardTinkeringApi.GetKeywordValue(tag),
                    TrainingSession.KeywordMultiplier(preview, tag, label));
            }
            catch (InvalidOperationException)
            {
                // Some keywords have shell prerequisites enforced by the analyzer (notably Sly requires a
                // printed ordinary cost from 1 to 3). A palette preview must remain renderable before the user
                // chooses or adjusts that shell; actual installation is normalized/validated separately.
                return new KeywordAnalysisLine(label, CardTinkeringApi.GetKeywordValue(tag), 1d);
            }
        }).ToArray();
    }

    private static int Capacity(double upperBound) => !double.IsFinite(upperBound) || upperBound <= 0d
        ? 0
        : upperBound >= int.MaxValue ? int.MaxValue : Math.Max(0, (int)Math.Floor(upperBound + 0.0001d));

    internal void SetCharacter(GeneratedCharacter value) => Shell = Shell with { Character = value };
    internal void SetRarity(GeneratedRarity value) => Shell = Shell with { Rarity = value };

    internal bool TryRerollIdentity(out string reason)
    {
        try
        {
            var identity = AutoAnthonyEditorApi.RerollEditorIdentity(Preview, Random.Shared.Next());
            Identity = identity;
            Shell = Shell with { Name = identity.Name };
            reason = string.Empty;
            return true;
        }
        catch
        {
            reason = Localize("无法为当前效果找到合适的卡名与卡图。",
                "No suitable name and portrait could be found for these effects.");
            return false;
        }
    }

    internal IReadOnlyList<FreeformComponent> SetType(GeneratedCardType value)
    {
        Shell = Shell with { Type = value };
        var removed = new List<FreeformComponent>();
        // A shell change may invalidate only a subset of an otherwise useful assembly. Move a root subtree only
        // when its removal strictly reduces the authoritative error set; an Attack merely lacking Damage remains
        // an editable invalid draft instead of discarding unrelated effects.
        while (true)
        {
            var current = ProjectedErrors().Count;
            if (current == 0) break;
            FreeformComponent? best = null;
            var bestErrors = current;
            foreach (var candidate in _components.Values.Where(item => !item.InBackpack)
                         .OrderByDescending(item => item.Order).ToArray())
            {
                var moved = Subtree(candidate.Id).ToArray();
                foreach (var item in moved) item.InBackpack = true;
                var count = ProjectedErrors().Count;
                foreach (var item in moved) item.InBackpack = false;
                if (count >= bestErrors) continue;
                best = candidate;
                bestErrors = count;
            }
            if (best is null) break;
            var subtree = Subtree(best.Id).ToArray();
            foreach (var item in subtree)
            {
                item.InBackpack = true;
                if (item.Id == best.Id) item.ParentId = null;
                removed.Add(item);
            }
            NormalizeOrders();
        }
        return removed;
    }

    internal void SetEnergyCost(int cost)
    {
        Shell = Shell with { Cost = Math.Clamp(cost, -1, 9) };
        if (cost < 0) Shell = Shell with { HasStarCostX = false };
        if (Shell.Tags.Contains(CardTag.Sly) && Shell.Cost is < 1 or > 3)
            Shell = Shell with { Tags = Shell.Tags.Where(tag => tag != CardTag.Sly).ToArray() };
    }

    internal void SetStarCost(int cost, bool x)
    {
        Shell = Shell with { StarCost = x ? 0 : Math.Clamp(cost, -1, 9), HasStarCostX = x };
        if (x) Shell = Shell with { Cost = Math.Max(0, Shell.Cost) };
    }

    internal void ToggleTag(CardTag tag)
    {
        var tags = Shell.Tags.ToHashSet();
        var added = tags.Add(tag);
        if (!added) tags.Remove(tag);
        Shell = Shell with { Tags = tags.OrderBy(value => value).ToArray() };
        if (added && tag == CardTag.Sly && Shell.Cost is < 1 or > 3)
            Shell = Shell with { Cost = Math.Clamp(Shell.Cost, 1, 3) };
    }

    internal void ToggleCustomKeyword(string id)
    {
        var values = (Shell.CustomKeywords ?? []).ToHashSet(StringComparer.Ordinal);
        if (!values.Add(id)) values.Remove(id);
        Shell = Shell with { CustomKeywords = values.OrderBy(value => value, StringComparer.Ordinal).ToArray() };
    }

    internal bool HasTag(CardTag tag) => Shell.Tags.Contains(tag);
    internal bool HasCustomKeyword(string id) => (Shell.CustomKeywords ?? []).Contains(id, StringComparer.Ordinal);

    internal FreeformComponent Add(CardTinkeringComponentPrototype prototype, string? parentId = null,
        string? beforeId = null, bool backpack = false)
    {
        var component = new FreeformComponent
        {
            Id = $"f{++_nextId}",
            Operation = StripParent(prototype.Operation),
            RequiresAttackSlot = prototype.CardReference == CardReferenceRequirement.HandAttack,
            ParentId = backpack ? null : parentId,
            Order = int.MaxValue,
            InBackpack = backpack
        };
        _components.Add(component.Id, component);
        PlaceUnchecked(component, parentId, beforeId, backpack);
        return component;
    }

    internal bool CanAdd(CardTinkeringComponentPrototype prototype, string? parentId, string? beforeId,
        out string reason)
    {
        if (parentId is not null && (!_components.TryGetValue(parentId, out var parent)
                                     || parent.InBackpack || !CanHaveChildren(parent.Operation)))
        {
            reason = Localize("目标不能包含后续组件。", "The target cannot own child components.");
            return false;
        }
        var temporary = Add(prototype, parentId, beforeId);
        var errors = ProjectedErrors();
        Delete(temporary.Id);
        var blocking = errors.FirstOrDefault(error => !IsIncompleteError(error));
        reason = blocking is null ? string.Empty : LocalizedError(blocking);
        return blocking is null;
    }

    internal bool TryMove(string id, string? parentId, string? beforeId, bool backpack, out string reason)
    {
        if (!_components.TryGetValue(id, out var component))
        {
            reason = Localize("找不到组件。", "Component not found.");
            return false;
        }
        if (parentId == id || parentId is not null && Subtree(id).Any(item => item.Id == parentId))
        {
            reason = Localize("组件不能放入自己的分支。", "A component cannot contain itself.");
            return false;
        }
        if (!backpack && parentId is not null
            && (!_components.TryGetValue(parentId, out var parent) || parent.InBackpack
                || !CanHaveChildren(parent.Operation)))
        {
            reason = Localize("目标不能包含后续组件。", "The target cannot own child components.");
            return false;
        }
        var snapshot = (component.ParentId, component.Order, component.InBackpack);
        PlaceUnchecked(component, parentId, beforeId, backpack);
        var blocking = ProjectedErrors().FirstOrDefault(error => !IsIncompleteError(error));
        if (blocking is null)
        {
            reason = string.Empty;
            return true;
        }
        PlaceUnchecked(component, snapshot.ParentId, null, snapshot.InBackpack);
        component.Order = snapshot.Order;
        NormalizeOrders();
        reason = LocalizedError(blocking);
        return false;
    }

    internal void Delete(string id)
    {
        foreach (var item in Subtree(id).ToArray()) _components.Remove(item.Id);
        NormalizeOrders();
    }

    internal bool TrySetValue(string id, string slot, int value)
    {
        if (!_components.TryGetValue(id, out var component)
            || !CardTinkeringApi.TrySetEditableValue(component.Operation, slot, value, out var updated))
            return false;
        component.Operation = updated;
        return true;
    }

    internal void Import(string payload)
    {
        var card = CardTinkeringApi.DeserializeCard(payload);
        _components.Clear();
        _nextId = 0;
        Identity = null;
        Shell = card with { Operations = [], ChineseDescription = string.Empty, EnglishDescription = string.Empty,
            Upgrade = null };
        var source = card.Operations;
        var selectorAttack = source.Any(operation => operation.Template == "N_SELECT_HAND_ATTACK");
        var byIndex = new Dictionary<int, FreeformComponent>();
        for (var index = 0; index < source.Count; index++)
        {
            var operation = source[index];
            if (operation.Template is "N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK") continue;
            string? parent = null;
            if (operation.Parameters.TryGetValue("triggerIndex", out var parentIndex)
                && byIndex.TryGetValue(parentIndex, out var owner)) parent = owner.Id;
            var component = new FreeformComponent
            {
                Id = $"f{++_nextId}", Operation = StripParent(operation), ParentId = parent,
                RequiresAttackSlot = selectorAttack && operation.CardTargetSlot == "card1",
                Order = _components.Count(item => !item.Value.InBackpack && item.Value.ParentId == parent),
                InBackpack = false
            };
            _components.Add(component.Id, component);
            byIndex[index] = component;
        }
        ApplyImplicitContainment(source, byIndex);
        NormalizeOrders();
    }

    internal static bool CanHaveChildren(GeneratorOperation operation) =>
        CardEffectRules.TriggerNeedsLinkedEffect(operation) || CardEffectRules.IsDependencyPrefix(operation);

    private IReadOnlyList<string> ProjectedErrors() => TrainingSession.ValidateAssembly(
        Preview, Preview.Operations, new CardTinkeringValidationOptions(true, true, true)).Errors;

    private static bool IsIncompleteError(string error) => error.Contains("no linked effect", StringComparison.Ordinal)
        || error.Contains("requires at least one Damage", StringComparison.Ordinal)
        || error.Contains("requires its adjacent dependency prefix", StringComparison.Ordinal)
        || error.Contains("dependency prefix must immediately", StringComparison.Ordinal)
        || error.Contains("requires an earlier", StringComparison.Ordinal);

    private IReadOnlyList<GeneratorOperation> Flatten() =>
        FlattenWithComponents().Select(item => item.Operation).ToArray();

    private List<(FreeformComponent? Component, GeneratorOperation Operation)> FlattenWithComponents()
    {
        var output = new List<(FreeformComponent? Component, GeneratorOperation Operation)>();
        var needsCard = _components.Values.Any(component => !component.InBackpack
            && component.Operation.CardTargetSlot == "card1");
        if (needsCard)
        {
            var attackOnly = _components.Values.Any(component => !component.InBackpack
                && component.Operation.CardTargetSlot == "card1" && component.RequiresAttackSlot);
            var selector = new GeneratorOperation(attackOnly ? "N_SELECT_HAND_ATTACK" : "N_SELECT_HAND_CARD",
                OperationScope.NonTargeted,
                attackOnly ? "选择手牌中的一张攻击牌。" : "选择手牌中的一张牌。",
                new Dictionary<string, int> { ["slotIndex"] = 1 },
                RuntimeSpec: OperationRuntimeSpecCompiler.GeneratedHandSelection(attackOnly));
            output.Add((null, selector));
        }

        void Visit(FreeformComponent component, int? parentIndex)
        {
            var parameters = component.Operation.Parameters.Where(pair => pair.Key != "triggerIndex")
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (parentIndex is not null) parameters["triggerIndex"] = parentIndex.Value;
            var ownIndex = output.Count;
            output.Add((component,
                OperationRuntimeSpecCompiler.Attach(component.Operation with { Parameters = parameters })));
            var childOwner = CardEffectRules.IsDependencyPrefix(component.Operation)
                             && component.Operation.Scope != OperationScope.ConditionalTrigger
                ? parentIndex : ownIndex;
            foreach (var child in Roots(component.Id)) Visit(child, childOwner);
        }
        foreach (var root in Roots()) Visit(root, null);
        return output;
    }

    private void PlaceUnchecked(FreeformComponent component, string? parentId, string? beforeId, bool backpack)
    {
        component.InBackpack = backpack;
        component.ParentId = backpack ? null : parentId;
        foreach (var child in Subtree(component.Id).Where(item => item.Id != component.Id))
            child.InBackpack = backpack;
        var siblings = _components.Values.Where(item => item.Id != component.Id && item.InBackpack == backpack
            && item.ParentId == component.ParentId).OrderBy(item => item.Order).ToList();
        var index = beforeId is null ? siblings.Count : siblings.FindIndex(item => item.Id == beforeId);
        if (index < 0) index = siblings.Count;
        siblings.Insert(index, component);
        for (var i = 0; i < siblings.Count; i++) siblings[i].Order = i;
        NormalizeOrders();
    }

    private IEnumerable<FreeformComponent> Subtree(string id)
    {
        if (!_components.TryGetValue(id, out var root)) yield break;
        yield return root;
        foreach (var child in _components.Values.Where(item => item.ParentId == id).ToArray())
        foreach (var descendant in Subtree(child.Id)) yield return descendant;
    }

    private void NormalizeOrders()
    {
        foreach (var group in _components.Values.GroupBy(component => (component.InBackpack, component.ParentId)))
        {
            var index = 0;
            foreach (var component in group.OrderBy(item => item.Order).ThenBy(item => item.Id,
                         StringComparer.Ordinal)) component.Order = index++;
        }
    }

    private void ApplyImplicitContainment(IReadOnlyList<GeneratorOperation> source,
        IReadOnlyDictionary<int, FreeformComponent> byIndex)
    {
        for (var index = 1; index < source.Count; index++)
        {
            if (!byIndex.TryGetValue(index - 1, out var prefix) || !byIndex.TryGetValue(index, out var payoff)
                || !CardEffectRules.IsDependencyPrefix(source[index - 1])
                || !CardEffectRules.IsLegalDependencyPayoff(source[index - 1], source[index])) continue;
            payoff.ParentId = prefix.Id;
        }
    }

    private static GeneratorOperation StripParent(GeneratorOperation operation) => operation with
    {
        Parameters = operation.Parameters.Where(pair => pair.Key != "triggerIndex")
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
    };

    private static string LocalizedError(string error) => TrainingSession.LocalizedReason(error);
}
