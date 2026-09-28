using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Events;

namespace AutoAnthony.Patches;

[HarmonyPatch(typeof(Spiral), nameof(Spiral.CanEnchant))]
internal static class SpiralChaosEligibilityPatch
{
    // Replace only Spiral's identity-tag test. The native base eligibility call and Basic-rarity
    // check remain intact, including existing enchantments, unplayable cards and card-type restrictions.
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getter = AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Tags));
        var replacement = AccessTools.Method(typeof(SpiralChaosEligibilityPatch), nameof(EligibilityTags));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(getter))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
            }
            yield return instruction;
        }
    }

    internal static IEnumerable<CardTag> EligibilityTags(CardModel card) =>
        ChaosBasicCardAncientRelics.IsActive && card is ChaosCardModel && card.Rarity == CardRarity.Basic
            ? new[] { CardTag.Strike }
            : card.Tags;
}

[HarmonyPatch(typeof(SpiralingWhirlpool), "GenerateInitialOptions")]
internal static class SpiralingWhirlpoolChaosDescriptionPatch
{
    private static void Postfix(IReadOnlyList<EventOption> __result)
    {
        if (!ChaosBasicCardAncientRelics.IsActive) return;
        foreach (var option in __result)
            if (option.TextKey == "SPIRALING_WHIRLPOOL.pages.INITIAL.options.OBSERVE")
                AccessTools.PropertySetter(typeof(EventOption), nameof(EventOption.Description)).Invoke(option,
                    [new LocString("main_menu_ui", "AUTO_ANTHONY_WHIRLPOOL_DESCRIPTION")]);
    }
}
