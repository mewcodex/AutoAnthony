using System.Runtime.CompilerServices;
using ChaosCardGenerator;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;

namespace AutoAnthony;

internal static class ChaosDerivativeResolver
{
    private static readonly ConditionalWeakTable<CardModel, DetachedPreviewMarker> DetachedPreviews = new();
    private sealed class DetachedPreviewMarker { }

    internal static bool IsDetachedPreview(CardModel card) => DetachedPreviews.TryGetValue(card, out _);

    internal static void AuditEnchantmentCompatibility()
    {
        foreach (var curse in DerivativeSlotCatalog.All.Where(DerivativeSlotCatalog.IsCurse))
        {
            var canonical = MutableCanonicalCard(curse.Id);
            if (canonical.Type != CardType.Curse)
                throw new InvalidOperationException($"Easter-egg derivative {curse.Id} is not a curse card.");
            var operation = new GeneratorOperation("D:CreateDazedInDiscard", OperationScope.NonTargeted,
                string.Empty, new Dictionary<string, int>(), DerivativeId: curse.Id);
            if (HoverTips(operation, upgraded: false).FirstOrDefault() is not CardHoverTip preview
                || preview.Card.Type != CardType.Curse)
                throw new InvalidOperationException($"Curse derivative preview {curse.Id} is invalid.");
        }
        foreach (var derivative in DerivativeSlotCatalog.All)
        foreach (var enchantment in DerivativeEnchantmentCatalog.Candidates(derivative))
        {
            var card = MutableCanonicalCard(derivative.Id);
            var canonicalEnchantment = CanonicalEnchantment(enchantment.Id);
            if (!canonicalEnchantment.CanEnchant(card))
                throw new InvalidOperationException(
                    $"Offline derivative enchantment pair {derivative.Id}/{enchantment.Id} is rejected by ModelDb.");
            var operation = new GeneratorOperation("N:CreateShiv", OperationScope.NonTargeted, string.Empty,
                new Dictionary<string, int>(), DerivativeId: derivative.Id,
                DerivativeEnchantmentId: enchantment.Id);
            ApplyEnchantment(card, operation);
            if (card.Enchantment?.GetType() != canonicalEnchantment.GetType())
                throw new InvalidOperationException(
                    $"Derivative enchantment pair {derivative.Id}/{enchantment.Id} did not apply through the runtime resolver.");
            if (card.Enchantment.Amount != enchantment.Amount)
                throw new InvalidOperationException(
                    $"Derivative enchantment pair {derivative.Id}/{enchantment.Id} applied the wrong amount.");
            AuditNumericPreview(card, derivative.Id, enchantment.Id);
            AuditCardMutation(card, enchantment.Id);
            if (HoverTips(operation, upgraded: false).FirstOrDefault() is not CardHoverTip preview
                || preview.Card.Enchantment?.GetType() != canonicalEnchantment.GetType()
                || preview.Card.Enchantment.Amount != enchantment.Amount)
                throw new InvalidOperationException(
                    $"Derivative hover preview {derivative.Id}/{enchantment.Id} does not carry its enchantment.");
            AuditDetachedPreviewNumericValues(preview.Card, derivative.Id, enchantment.Id);
        }
        var fuelDefinition = DerivativeSlotCatalog.All.Single(definition => definition.Id == "fuel");
        foreach (var enchantment in DerivativeEnchantmentCatalog.Candidates(fuelDefinition))
        {
            var card = ModelDb.Card<AncientFuel>().ToMutable();
            var canonicalEnchantment = CanonicalEnchantment(enchantment.Id);
            if (!canonicalEnchantment.CanEnchant(card))
                throw new InvalidOperationException($"Ancient Fuel is rejected by {enchantment.Id}.");
            CardCmd.Enchant(canonicalEnchantment.ToMutable(), card, enchantment.Amount);
        }
        foreach (var enchantment in DerivativeEnchantmentCatalog.All
                     .Where(enchantment => DerivativeEnchantmentCatalog.AllowedAmounts(enchantment).Count > 1))
        {
            var derivative = DerivativeSlotCatalog.All.First(candidate =>
                DerivativeEnchantmentCatalog.CanUse(candidate, enchantment));
            foreach (var amount in new[]
                     {
                         DerivativeEnchantmentCatalog.AllowedAmounts(enchantment).Min(),
                         DerivativeEnchantmentCatalog.AllowedAmounts(enchantment).Max()
                     })
            {
                var operation = new GeneratorOperation("N:CreateShiv", OperationScope.NonTargeted, string.Empty,
                    new Dictionary<string, int>(), DerivativeId: derivative.Id,
                    DerivativeEnchantmentId: enchantment.Id, DerivativeEnchantmentAmount: amount);
                var card = MutableCanonicalCard(derivative.Id);
                ApplyEnchantment(card, operation);
                if (card.Enchantment?.Amount != amount
                    || HoverTips(operation, upgraded: false).FirstOrDefault() is not CardHoverTip preview
                    || preview.Card.Enchantment?.Amount != amount)
                    throw new InvalidOperationException(
                        $"Derivative enchantment amount {derivative.Id}/{enchantment.Id}/{amount} failed runtime or preview audit.");
            }
        }
    }

    private static void AuditNumericPreview(CardModel card, string derivativeId, string enchantmentId)
    {
        var attached = card.Enchantment
            ?? throw new InvalidOperationException($"Derivative enchantment {derivativeId}/{enchantmentId} is missing.");
        foreach (var variable in card.DynamicVars.Values)
        {
            decimal expected;
            switch (variable)
            {
                case DamageVar damage:
                    expected = (damage.BaseValue + attached.EnchantDamageAdditive(damage.BaseValue, damage.Props))
                        * attached.EnchantDamageMultiplicative(damage.BaseValue, damage.Props);
                    break;
                case OstyDamageVar ostyDamage:
                    expected = (ostyDamage.BaseValue
                            + attached.EnchantDamageAdditive(ostyDamage.BaseValue, ostyDamage.Props))
                        * attached.EnchantDamageMultiplicative(ostyDamage.BaseValue, ostyDamage.Props);
                    break;
                case BlockVar block:
                    expected = (block.BaseValue + attached.EnchantBlockAdditive(block.BaseValue))
                        * attached.EnchantBlockMultiplicative(block.BaseValue);
                    break;
                default:
                    continue;
            }
            variable.UpdateCardPreview(card, CardPreviewMode.None, null, runGlobalHooks: false);
            if (variable.EnchantedValue != expected || variable.PreviewValue != expected)
                throw new InvalidOperationException(
                    $"Derivative enchantment preview {derivativeId}/{enchantmentId}/{variable.Name} is "
                    + $"{variable.EnchantedValue}/{variable.PreviewValue}, expected {expected}.");
        }
    }

    private static void AuditDetachedPreviewNumericValues(CardModel card, string derivativeId,
        string enchantmentId)
    {
        var attached = card.Enchantment
            ?? throw new InvalidOperationException($"Derivative preview {derivativeId}/{enchantmentId} is missing its enchantment.");
        card.DynamicVars.ClearPreview();
        card.UpdateDynamicVarPreview(CardPreviewMode.None, null, card.DynamicVars);
        foreach (var variable in card.DynamicVars.Values)
        {
            decimal? expected = variable switch
            {
                DamageVar damage => (damage.BaseValue
                        + attached.EnchantDamageAdditive(damage.BaseValue, damage.Props))
                    * attached.EnchantDamageMultiplicative(damage.BaseValue, damage.Props),
                OstyDamageVar ostyDamage => (ostyDamage.BaseValue
                        + attached.EnchantDamageAdditive(ostyDamage.BaseValue, ostyDamage.Props))
                    * attached.EnchantDamageMultiplicative(ostyDamage.BaseValue, ostyDamage.Props),
                BlockVar block => (block.BaseValue + attached.EnchantBlockAdditive(block.BaseValue))
                    * attached.EnchantBlockMultiplicative(block.BaseValue),
                _ => null
            };
            if (expected.HasValue && (variable.EnchantedValue != expected.Value
                                      || variable.PreviewValue != expected.Value))
                throw new InvalidOperationException(
                    $"Detached derivative preview {derivativeId}/{enchantmentId}/{variable.Name} is "
                    + $"{variable.EnchantedValue}/{variable.PreviewValue}, expected {expected.Value}.");
        }
    }

    private static void AuditCardMutation(CardModel card, string enchantmentId)
    {
        if (enchantmentId == "souls_power" && card.Keywords.Contains(CardKeyword.Exhaust))
            throw new InvalidOperationException("Soul's Power did not remove Exhaust from its derivative card.");
        if (enchantmentId == "steady" && !card.Keywords.Contains(CardKeyword.Retain))
            throw new InvalidOperationException("Steady did not add Retain to its derivative card.");
        if (enchantmentId == "royally_approved"
            && (!card.Keywords.Contains(CardKeyword.Innate) || !card.Keywords.Contains(CardKeyword.Retain)))
            throw new InvalidOperationException("Royally Approved did not add Innate and Retain to its derivative card.");
        if (enchantmentId == "tezcatara_ember"
            && (card.EnergyCost.GetWithModifiers(CostModifiers.None) != 0
                || !card.Keywords.Contains(CardKeyword.Eternal)))
            throw new InvalidOperationException("Tezcatara's Ember did not set cost 0 and add Eternal.");
    }

    private static CardModel MutableCanonicalCard(string id) => id switch
    {
        "rock" => ModelDb.Card<GiantRock>().ToMutable(),
        "shiv" => ModelDb.Card<Shiv>().ToMutable(),
        "fuel" => ModelDb.Card<Fuel>().ToMutable(),
        "dazed" => ModelDb.Card<Dazed>().ToMutable(),
        "wound" => ModelDb.Card<Wound>().ToMutable(),
        "slimed" => ModelDb.Card<Slimed>().ToMutable(),
        "burn" => ModelDb.Card<Burn>().ToMutable(),
        "void" => ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.Void>().ToMutable(),
        "soul" => ModelDb.Card<Soul>().ToMutable(),
        "gaze" => ModelDb.Card<SweepingGaze>().ToMutable(),
        "debris" => ModelDb.Card<Debris>().ToMutable(),
        "sword" => ModelDb.Card<SovereignBlade>().ToMutable(),
        "minion_strike" => ModelDb.Card<MinionStrike>().ToMutable(),
        "minion_dive" => ModelDb.Card<MinionDiveBomb>().ToMutable(),
        "minion_sacrifice" => ModelDb.Card<MinionSacrifice>().ToMutable(),
        "curse_ascenders_bane" => ModelDb.Card<AscendersBane>().ToMutable(),
        "curse_bad_luck" => ModelDb.Card<BadLuck>().ToMutable(),
        "curse_clumsy" => ModelDb.Card<Clumsy>().ToMutable(),
        "curse_bell" => ModelDb.Card<CurseOfTheBell>().ToMutable(),
        "curse_debt" => ModelDb.Card<Debt>().ToMutable(),
        "curse_decay" => ModelDb.Card<Decay>().ToMutable(),
        "curse_doubt" => ModelDb.Card<Doubt>().ToMutable(),
        "curse_enthralled" => ModelDb.Card<Enthralled>().ToMutable(),
        "curse_folly" => ModelDb.Card<Folly>().ToMutable(),
        "curse_greed" => ModelDb.Card<Greed>().ToMutable(),
        "curse_guilty" => ModelDb.Card<Guilty>().ToMutable(),
        "curse_injury" => ModelDb.Card<Injury>().ToMutable(),
        "curse_normality" => ModelDb.Card<Normality>().ToMutable(),
        "curse_poor_sleep" => ModelDb.Card<PoorSleep>().ToMutable(),
        "curse_regret" => ModelDb.Card<Regret>().ToMutable(),
        "curse_shame" => ModelDb.Card<Shame>().ToMutable(),
        "curse_spore_mind" => ModelDb.Card<SporeMind>().ToMutable(),
        "curse_writhe" => ModelDb.Card<Writhe>().ToMutable(),
        _ => throw new InvalidOperationException($"Unknown derivative slot id '{id}'.")
    };

    private static EnchantmentModel CanonicalEnchantment(string id) => id switch
    {
        "adroit" => ModelDb.Enchantment<Adroit>(),
        "corrupted" => ModelDb.Enchantment<Corrupted>(),
        "glam" => ModelDb.Enchantment<Glam>(),
        "inky" => ModelDb.Enchantment<Inky>(),
        "instinct" => ModelDb.Enchantment<Instinct>(),
        "momentum" => ModelDb.Enchantment<Momentum>(),
        "nimble" => ModelDb.Enchantment<Nimble>(),
        "perfect_fit" => ModelDb.Enchantment<PerfectFit>(),
        "royally_approved" => ModelDb.Enchantment<RoyallyApproved>(),
        "sharp" => ModelDb.Enchantment<Sharp>(),
        "slither" => ModelDb.Enchantment<Slither>(),
        "slumbering_essence" => ModelDb.Enchantment<SlumberingEssence>(),
        "souls_power" => ModelDb.Enchantment<SoulsPower>(),
        "sown" => ModelDb.Enchantment<Sown>(),
        "steady" => ModelDb.Enchantment<Steady>(),
        "swift" => ModelDb.Enchantment<Swift>(),
        "tezcatara_ember" => ModelDb.Enchantment<TezcatarasEmber>(),
        "vigorous" => ModelDb.Enchantment<Vigorous>(),
        _ => throw new InvalidOperationException($"Unknown derivative enchantment '{id}'.")
    };

    public static DerivativeSlotDefinition? Definition(GeneratorOperation operation) =>
        DerivativeSlotCatalog.Resolve(operation.DerivativeId, operation.Template);

    public static CardModel Create(ICombatState cardScope, Player owner, GeneratorOperation operation, bool upgraded = false)
    {
        var id = Definition(operation)?.Id
            ?? throw new InvalidOperationException($"Operation {operation.Template} has no derivative slot.");
        CardModel card = id switch
        {
            "rock" => cardScope.CreateCard<GiantRock>(owner),
            "shiv" => cardScope.CreateCard<Shiv>(owner),
            "fuel" => ChaosRunDefinitions.AncientFuelActive
                ? cardScope.CreateCard<AncientFuel>(owner)
                : cardScope.CreateCard<Fuel>(owner),
            "dazed" => cardScope.CreateCard<Dazed>(owner),
            "wound" => cardScope.CreateCard<Wound>(owner),
            "slimed" => cardScope.CreateCard<Slimed>(owner),
            "burn" => cardScope.CreateCard<Burn>(owner),
            "void" => cardScope.CreateCard<MegaCrit.Sts2.Core.Models.Cards.Void>(owner),
            "soul" => cardScope.CreateCard<Soul>(owner),
            "gaze" => cardScope.CreateCard<SweepingGaze>(owner),
            "debris" => cardScope.CreateCard<Debris>(owner),
            "sword" => cardScope.CreateCard<SovereignBlade>(owner),
            "minion_strike" => cardScope.CreateCard<MinionStrike>(owner),
            "minion_dive" => cardScope.CreateCard<MinionDiveBomb>(owner),
            "minion_sacrifice" => cardScope.CreateCard<MinionSacrifice>(owner),
            "curse_ascenders_bane" => cardScope.CreateCard<AscendersBane>(owner),
            "curse_bad_luck" => cardScope.CreateCard<BadLuck>(owner),
            "curse_clumsy" => cardScope.CreateCard<Clumsy>(owner),
            "curse_bell" => cardScope.CreateCard<CurseOfTheBell>(owner),
            "curse_debt" => cardScope.CreateCard<Debt>(owner),
            "curse_decay" => cardScope.CreateCard<Decay>(owner),
            "curse_doubt" => cardScope.CreateCard<Doubt>(owner),
            "curse_enthralled" => cardScope.CreateCard<Enthralled>(owner),
            "curse_folly" => cardScope.CreateCard<Folly>(owner),
            "curse_greed" => cardScope.CreateCard<Greed>(owner),
            "curse_guilty" => cardScope.CreateCard<Guilty>(owner),
            "curse_injury" => cardScope.CreateCard<Injury>(owner),
            "curse_normality" => cardScope.CreateCard<Normality>(owner),
            "curse_poor_sleep" => cardScope.CreateCard<PoorSleep>(owner),
            "curse_regret" => cardScope.CreateCard<Regret>(owner),
            "curse_shame" => cardScope.CreateCard<Shame>(owner),
            "curse_spore_mind" => cardScope.CreateCard<SporeMind>(owner),
            "curse_writhe" => cardScope.CreateCard<Writhe>(owner),
            _ => throw new InvalidOperationException($"Unknown derivative slot id '{id}'.")
        };
        ApplyEnchantment(card, operation);
        if (upgraded && card.IsUpgradable) CardCmd.Upgrade(card);
        DetachedPreviews.Add(card, new DetachedPreviewMarker());
        return card;
    }

    private static void ApplyEnchantment(CardModel card, GeneratorOperation operation)
    {
        var enchantment = DerivativeSlotCatalog.ResolveEnchantment(operation.DerivativeId,
            operation.DerivativeEnchantmentId, operation.Template);
        if (enchantment is null) return;
        var amount = (decimal)DerivativeEnchantmentCatalog.ResolveAmount(operation, enchantment);
        switch (enchantment.Id)
        {
            case "adroit": CardCmd.Enchant<Adroit>(card, amount); break;
            case "corrupted": CardCmd.Enchant<Corrupted>(card, amount); break;
            case "glam": CardCmd.Enchant<Glam>(card, amount); break;
            case "inky": CardCmd.Enchant<Inky>(card, amount); break;
            case "instinct": CardCmd.Enchant<Instinct>(card, amount); break;
            case "momentum": CardCmd.Enchant<Momentum>(card, amount); break;
            case "nimble": CardCmd.Enchant<Nimble>(card, amount); break;
            case "perfect_fit": CardCmd.Enchant<PerfectFit>(card, amount); break;
            case "royally_approved": CardCmd.Enchant<RoyallyApproved>(card, amount); break;
            case "sharp": CardCmd.Enchant<Sharp>(card, amount); break;
            case "slither": CardCmd.Enchant<Slither>(card, amount); break;
            case "slumbering_essence": CardCmd.Enchant<SlumberingEssence>(card, amount); break;
            case "souls_power": CardCmd.Enchant<SoulsPower>(card, amount); break;
            case "sown": CardCmd.Enchant<Sown>(card, amount); break;
            case "steady": CardCmd.Enchant<Steady>(card, amount); break;
            case "swift": CardCmd.Enchant<Swift>(card, amount); break;
            case "tezcatara_ember": CardCmd.Enchant<TezcatarasEmber>(card, amount); break;
            case "vigorous": CardCmd.Enchant<Vigorous>(card, amount); break;
            default: throw new InvalidOperationException($"Unknown derivative enchantment '{enchantment.Id}'.");
        }
    }

    public static bool Matches(CardModel card, GeneratorOperation operation)
    {
        var id = Definition(operation)?.Id;
        return id switch
        {
            "rock" => card is GiantRock,
            // The base game defines Knife Trap in terms of CardTag.Shiv, not the concrete Shiv class. Preserve
            // that category semantics so upgraded, enchanted, and compatible mod-provided Shivs all replay.
            "shiv" => card.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shiv),
            "fuel" => card is Fuel or AncientFuel,
            "dazed" => card is Dazed,
            "wound" => card is Wound,
            "slimed" => card is Slimed,
            "burn" => card is Burn,
            "void" => card is MegaCrit.Sts2.Core.Models.Cards.Void,
            "soul" => card is Soul,
            "gaze" => card is SweepingGaze,
            "debris" => card is Debris,
            "sword" => card is SovereignBlade,
            "minion_strike" => card is MinionStrike,
            "minion_dive" => card is MinionDiveBomb,
            "minion_sacrifice" => card is MinionSacrifice,
            "curse_ascenders_bane" => card is AscendersBane,
            "curse_bad_luck" => card is BadLuck,
            "curse_clumsy" => card is Clumsy,
            "curse_bell" => card is CurseOfTheBell,
            "curse_debt" => card is Debt,
            "curse_decay" => card is Decay,
            "curse_doubt" => card is Doubt,
            "curse_enthralled" => card is Enthralled,
            "curse_folly" => card is Folly,
            "curse_greed" => card is Greed,
            "curse_guilty" => card is Guilty,
            "curse_injury" => card is Injury,
            "curse_normality" => card is Normality,
            "curse_poor_sleep" => card is PoorSleep,
            "curse_regret" => card is Regret,
            "curse_shame" => card is Shame,
            "curse_spore_mind" => card is SporeMind,
            "curse_writhe" => card is Writhe,
            _ => false
        };
    }

    public static IEnumerable<IHoverTip> HoverTips(GeneratorOperation operation, bool upgraded)
    {
        if (Definition(operation) is not null)
            yield return HoverTipFactory.FromCard(CreatePreview(operation, upgraded));
        var enchantment = DerivativeSlotCatalog.ResolveEnchantment(operation.DerivativeId,
            operation.DerivativeEnchantmentId, operation.Template);
        if (enchantment is null) yield break;
        foreach (var tip in EnchantmentHoverTips(enchantment,
                     DerivativeEnchantmentCatalog.ResolveAmount(operation, enchantment)))
            yield return tip;
    }

    private static CardModel CreatePreview(GeneratorOperation operation, bool upgraded)
    {
        var id = Definition(operation)?.Id
            ?? throw new InvalidOperationException($"Operation {operation.Template} has no derivative preview slot.");
        var card = id == "fuel" && ChaosRunDefinitions.AncientFuelActive
            ? ModelDb.Card<AncientFuel>().ToMutable()
            : MutableCanonicalCard(id);
        ApplyEnchantment(card, operation);
        if (upgraded && card.IsUpgradable) CardCmd.Upgrade(card);
        DetachedPreviews.Add(card, new DetachedPreviewMarker());
        return card;
    }

    private static IEnumerable<IHoverTip> EnchantmentHoverTips(DerivativeEnchantmentDefinition enchantment,
        int amount)
    {
        return enchantment.Id switch
        {
            "adroit" => HoverTipFactory.FromEnchantment<Adroit>(amount),
            "corrupted" => HoverTipFactory.FromEnchantment<Corrupted>(amount),
            "glam" => HoverTipFactory.FromEnchantment<Glam>(amount),
            "inky" => HoverTipFactory.FromEnchantment<Inky>(amount),
            "instinct" => HoverTipFactory.FromEnchantment<Instinct>(amount),
            "momentum" => HoverTipFactory.FromEnchantment<Momentum>(amount),
            "nimble" => HoverTipFactory.FromEnchantment<Nimble>(amount),
            "perfect_fit" => HoverTipFactory.FromEnchantment<PerfectFit>(amount),
            "royally_approved" => HoverTipFactory.FromEnchantment<RoyallyApproved>(amount),
            "sharp" => HoverTipFactory.FromEnchantment<Sharp>(amount),
            "slither" => HoverTipFactory.FromEnchantment<Slither>(amount),
            "slumbering_essence" => HoverTipFactory.FromEnchantment<SlumberingEssence>(amount),
            "souls_power" => HoverTipFactory.FromEnchantment<SoulsPower>(amount),
            "sown" => HoverTipFactory.FromEnchantment<Sown>(amount),
            "steady" => HoverTipFactory.FromEnchantment<Steady>(amount),
            "swift" => HoverTipFactory.FromEnchantment<Swift>(amount),
            "tezcatara_ember" => HoverTipFactory.FromEnchantment<TezcatarasEmber>(amount),
            "vigorous" => HoverTipFactory.FromEnchantment<Vigorous>(amount),
            _ => []
        };
    }
}

/// <summary>
/// Card hovertip previews are detached mutable canonical cards without a RunState or CombatState. The base game
/// clears their DynamicVars in NCard.UpdateVisuals, then CardModel.UpdateDynamicVarPreview returns before restoring
/// enchantment-adjusted values. Refresh only AutoAnthony's marked derivative previews without global combat hooks,
/// so Instinct Giant Rocks and every other numeric enchantment display their actual card-face values.
/// </summary>
[HarmonyPatch(typeof(CardModel), nameof(CardModel.UpdateDynamicVarPreview))]
internal static class ChaosDetachedDerivativePreviewPatch
{
    private static void Postfix(CardModel __instance, CardPreviewMode previewMode, Creature? target,
        DynamicVarSet dynamicVarSet)
    {
        if (!ChaosDerivativeResolver.IsDetachedPreview(__instance)
            || __instance.RunState is not null || __instance.CombatState is not null)
            return;
        foreach (var variable in dynamicVarSet.Values)
            variable.UpdateCardPreview(__instance, previewMode, target, runGlobalHooks: false);
    }
}
