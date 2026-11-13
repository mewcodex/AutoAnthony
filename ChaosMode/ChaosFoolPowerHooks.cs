using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.FoolArrows;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace AutoAnthony;

public sealed partial class ChaosCompositePower
{
    private int[] _consumedFoolDeathTriggers = [];
    [SavedProperty] public int[] ConsumedFoolDeathTriggers
    {
        get => _consumedFoolDeathTriggers;
        set { AssertMutable(); _consumedFoolDeathTriggers = value ?? []; }
    }
    [SavedProperty] public bool FoolFirstArrowDislodged { get; set; }
    [SavedProperty] public bool FoolAllMelodiesTriggered { get; set; }

    public override Task BeforeSideTurnStart(PlayerChoiceContext context, CombatSide side,
        IReadOnlyList<Creature> participants, ICombatState combatState)
    {
        if (Owner.Side == side && participants.Contains(Owner))
        {
            FoolFirstArrowDislodged = false;
            FoolAllMelodiesTriggered = false;
        }
        return Task.CompletedTask;
    }

    private async Task FireFoolMelodyEvents(PlayerChoiceContext context, CardPlay play)
    {
        if (!OwnedOperations().Any(operation => operation.Template is "F:OnMelodyGained" or "F:OnWahGained"
                or "F:OnChoGained" or "F:OnAllMelodies")) return;
        var gained = 0;
        foreach (var tag in new[] { CardTag.Wah, CardTag.Cho, CardTag.Ki })
        {
            if (!play.Card.Tags.Contains(tag) || !MelodyCmd.IsFirstPlayThisTurn(play, tag)) continue;
            gained++;
            await FireTriggers("melody_gained", context, play, play.Card);
            await FireTriggers(tag switch { CardTag.Wah => "wah_melody_gained", CardTag.Cho => "cho_melody_gained",
                _ => "ki_melody_gained" }, context, play, play.Card);
        }
        if (gained > 0 && !FoolAllMelodiesTriggered && MelodyCmd.MelodiesThisTurn(Owner.Player!) == 3)
        {
            FoolAllMelodiesTriggered = true;
            await FireTriggers("all_melodies_gained", context, play, play.Card);
        }
    }

    public override async Task AfterArrowImpaled(ImpaledPower power, ArrowSlot arrow,
        PlayerChoiceContext context, CardModel? source)
    {
        if (power.Applier == Owner)
            await FireTriggers("arrow_impaled", context, eventCard: source, eventCreature: power.Owner);
    }

    public override async Task AfterArrowDislodged(ImpaledPower power, ArrowSlot arrow,
        PlayerChoiceContext context, CardModel? source)
    {
        if (arrow.owner.Creature != Owner || FoolFirstArrowDislodged) return;
        FoolFirstArrowDislodged = true;
        await FireTriggers("first_arrow_dislodged_each_turn", context, eventCard: source, eventCreature: power.Owner);
    }

    public override async Task AfterDeath(PlayerChoiceContext context, Creature target, CardModel? source,
        bool wasRemovalPrevented, float deathAnimLength)
    {
        if (wasRemovalPrevented || target.Side == Owner.Side
            || !target.Powers.All(power => power.ShouldOwnerDeathTriggerFatal())) return;
        var operations = Definition.Card.Operations;
        for (var index = 0; index < operations.Count; index++)
        {
            if (!OwnsOperation(index) || TriggerKind(operations[index]) != "next_non_minion_enemy_death_this_turn"
                || ConsumedFoolDeathTriggers.Contains(index) || OwnerTurnEffectsExpired) continue;
            ConsumedFoolDeathTriggers = [.. ConsumedFoolDeathTriggers, index];
            await FireTriggerAt(index, context, eventCard: source, eventCreature: target);
        }
        await RemoveIfNoLiveEffects();
    }
}
