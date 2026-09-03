using System.Text.RegularExpressions;

namespace ChaosCardGenerator;

/// <summary>
/// A deliberately approximate common currency for recombined effects.  One point is roughly one printed point
/// of single-target damage.  The model is not intended to solve every card exactly: it prevents reliable,
/// repeatable mechanics from receiving a full ordinary-card payoff while preserving a strong high-roll tail.
/// </summary>
internal static class EffectBalanceModel
{
    private static readonly Regex Number = new(@"\d+", RegexOptions.Compiled);
    // Fatal normally succeeds only on the finishing hit of one combat. Treat it as a rarer one-shot gate than
    // poison/vulnerable checks so its linked payoff receives a visibly larger budget without changing occurrence.
    internal const double FatalTriggerFrequency = 0.18d;
    // Stratagem is the sole native anchor. In actual fights the Power is often drawn after part of the first deck
    // cycle and combat/deck growth suppress later reshuffles, so one resolution per roughly four active turns is
    // more representative than estimating cadence from starting deck size alone.
    internal const double DrawPileShuffleTriggerFrequency = 0.25d;
    // Royalties (30 Gold on a one-Energy Rare Power) and Hand of Greed (20 Gold behind Fatal beside 20 Damage
    // on a two-Energy Rare) bracket the same result around 0.5 Damage-equivalent per Gold.
    internal const int GoldValuePerPoint = 50;
    private const int RandomGeneratedCardValue = 450;
    private const double RandomColorlessPoolValueMultiplier = 1.9d;
    private const int RandomPowerCardValue = 1_120;
    internal const int OrdinaryEnergyValuePerPoint = 650;
    internal const int SkillsCostZeroRuleValue = 10_500;
    internal const int ReferencedSkillExhaustValuePerCard = 650;
    // Gold Axe is itself a one-Energy Rare with no other text. Keep the complete dynamic-damage rule at the
    // one-Energy Rare center instead of estimating an arbitrary number of cards played this combat.
    internal const int GoldAxeDynamicDamageValue = 1_900;
    // Bullet Time is the only native source: a three-Energy Rare plus the 1.36x no-draw payment. The rule must
    // therefore buy the complete 3-Energy Rare envelope after that payment (1,900 * 3.40 * 1.36 ~= 8,786).
    // Rounding to 8,800 keeps the native card centered and prevents the rule from fitting on cheap filler cards.
    internal const int FreeHandThisTurnValue = 8_800;
    // A player-selected mandatory Hand Exhaust is worth 350. Status-only Exhaust has less targeting flexibility,
    // so value each removed Status slightly lower. Flak Cannon is intentionally a strong native 2-Energy Rare:
    // 2.6 * (300 Status utility + 720 random 8-Damage) = 2,652, about 114.2% of its 2,322 rarity/cost center,
    // without introducing a whole-card exception.
    internal const int StatusExhaustValuePerCard = 300;
    internal const double ExhaustedStatusTriggerFrequency = 2.6d;
    private const int NecrobinderEnergyValuePerPoint = 658;
    private const int SummonValuePerPoint = 239;
    private const int VigorValuePerPoint = 180;
    private const int VulnerableValuePerTurn = 550;
    private const int WeakValuePerTurn = 470;
    private const int OrbSlotValuePerSlot = 650;
    private const int OstyHealValuePerPoint = 180;
    // Kept internal so the offline native-card audit can expose the exact coefficient which participates in the
    // generator-facing valuation.  The audit must never replace this with a peer-card residual coefficient.
    internal const double RollingGrowthQuadraticCoefficient = 0.592831541218638d;
    private const int SoulInDrawValuePerCard = 810;
    private const double ExpectedCurrentBlock = 11d;
    private const double ExpectedOstyCurrentHp = 5d;
    private const double ExpectedOstyMaxHp = 12d;
    private const double EnergyXThresholdSuccessChance = 0.15d;
    // Recurring draw has extra engine value beyond its nominal draw count. Keep generation and valuation as exact
    // reciprocals of the same shared factors so changing the estimate cannot silently move generated card budgets.
    // Native Tyranny places the persistent premium near 1.18; one-turn repeatable draw retains the prior 1.22.
    private const double PersistentTriggeredDrawValueMultiplier = 1.18d;
    private const double TemporaryTriggeredDrawValueMultiplier = 1.22d;

    /// <summary>
    /// Occurrence prior for trigger/condition families only. Scalable numeric rewards are priced after their
    /// generated values exist; fixed mechanics use the assembler's single affordability gate.
    /// </summary>
    internal static int TriggerOccurrenceWeight(IEnumerable<ComponentAtom> family)
    {
        var atoms = family.ToArray();
        if (atoms.Length == 0) return 100;
        var triggerWeights = atoms.Select(TriggerSelectionWeight).ToArray();
        return Math.Max(1, triggerWeights.Sum() / triggerWeights.Length);
    }

    /// <summary>Estimated printed value in hundredths of one ordinary damage point.</summary>
    internal static int EstimatedEffectValue(ComponentAtom atom)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(atom);
        if (ComponentValuationApi.TryEstimate(atom.Template, atom.Scope, spec, out var customValue))
            return customValue;
        var explicitValues = spec.Values.Where(value => value.Explicit && value.Source == "fixed").ToArray();
        var first = explicitValues.FirstOrDefault()?.BaseValue ?? 1;
        var hits = Math.Clamp(spec.Values.FirstOrDefault(value => value.Id is "hits" or "repeat_count")
            ?.BaseValue ?? 1, 1, 8);

        if (TryEstimateReplayValue(atom, first, hits, out var replayValue)) return replayValue;
        // These scarce rule effects are complete card-sized mechanics, not generic one-point status lines.
        // Explicit prices keep them out of cheap filler slots while their separate occurrence prior preserves a
        // small reconstruction path at every rarity.
        // Molten Fist is the native anchor: 1 Energy Common, 10 Damage, Exhaust, then doubles the target's
        // Vulnerable. Against the ordinary Common damage curve and Exhaust compensation, the rider is worth
        // about six Damage points rather than the old 8.5-point near-card-sized price.
        if (CardEffectRules.IsDoubleTargetVulnerable(atom)) return 600;
        if (atom.Template == "N:StrengthPerTargetVulnerable") return 2_400;
        if (CardEffectRules.IsCopyThisCardToDiscard(atom))
            return atom.Template == "D:CreateZeroCostCopyInDiscard" ? 1_500 : 1_100;
        if (CardEffectRules.IsExhaustAllHand(atom)) return 1_500;
        if (CardEffectRules.IsEnemyStrengthGain(atom)) return 900;
        if (atom.Template == "I:PlayTopCardAndExhaust") return 900;
        // Guards transforms any number of hand cards into a chosen derivative. The open-ended count and the
        // ability to replace liabilities or spent cards make it a full high-value effect rather than a generic
        // proxy rider.
        if (atom.Template == "I:ProxyAtomic_Guards") return 3_200;
        // Corruption's persistent rule makes every Skill cost 0. At the shared three-active-turn valuation horizon,
        // merely gaining five Energy at each turn start is worth 5 * 650 * 3 = 9,750. Free Skills are more flexible,
        // can exceed five Energy in a developed hand, and retain unspent-card sequencing value, so this rule must
        // sit strictly above that benchmark. Its paired "Exhaust that Skill" payment is priced separately by
        // NegativeEffectTuning and scales with the actual persistent Skill-play cadence.
        if (atom.Template == "A:rule"
            && OperationRuntimeSpecCompiler.GetOrCompile(atom).Variant == "skills_cost_zero")
            return SkillsCostZeroRuleValue;
        // Stoke replaces each Exhausted hand card with one random card from the current character's pool. One
        // generated replacement is close to Draw 1, but does not advance the draw pile and is less controllable.
        if (CardEffectRules.IsRandomCurrentCharacterCardToHand(atom)) return RandomGeneratedCardValue;
        // Accelerant is the clean native anchor: a one-Energy Uncommon Power spends its entire card on one extra
        // Poison trigger, and its native upgrade raises that amount from one to two. Price every additional trigger
        // as a full 13.5-damage-equivalent persistent rule. The former generic AbilityRule fallback returned 1,350
        // regardless of N, severely underpricing the native +1 upgrade and any randomized higher amount.
        if (atom.Template == "A:rulePoisonExtraTriggers") return PoisonExtraTriggerValue(first);
        // Royalties (30 Gold) and Hand of Greed (20 Gold behind Fatal) establish Gold as a run-level reward, not
        // an ordinary one-point scalar. Fifty value units per Gold keeps Royalties near its former fixed AbilityRule
        // price while making its native +10 upgrade worth a meaningful ~500 upgrade units.
        if (CardEffectRules.IsGoldGainOperation(atom)) return first * GoldValuePerPoint;
        // Native random-card producers were previously falling through to amount*100. Jack of All Trades,
        // Bundle of Joy, Spectrum Shift and Creative AI show that the produced card is close to Draw 1, with a
        // small premium for restricting the result to Powers. Keep the count live so upgrades and generated
        // amounts consume the same value that their card face advertises.
        if (atom.Template is "CL:AddRandomColorlessToHand" or "R:AddRandomColorlessToHand")
            // Output pool, not source character, determines value. Jack of All Trades, Spectrum Shift and Bundle
            // of Joy jointly place a random Colorless card near 1.9 random role-pool cards; every character that
            // accesses the Colorless pool must therefore pay the same unit price.
            return first * RandomColorlessCardValue;
        if (atom.Template == "D:AddRandomPowerToHand") return first * RandomPowerCardValue;
        if (atom.Template == "N:Create"
            && spec is { Opcode: "create_copy", Variant: "referenced_attack" })
            // Juggling copies the actual third Attack, preserving its full effects and upgrade. It is much closer
            // to gaining another playable card than to the no-number fallback used by generic utility text.
            return 1_400;
        // Fit one linear Summon price from the clean native anchors instead of introducing an arbitrary breakpoint:
        // Bodyguard (5 alone), Afterlife (6 + Exhaust), Pull Aggro (4 + Block 7) and Reanimate (20 + Exhaust)
        // imply roughly 120/300/177/194 value per point. Their robust center is about 180. X variants use the same
        // resolved amount, and the whole-card envelope remains responsible for rarity/cost scaling.
        if (atom.Template is "NCR:Summon" or "NCR:SummonX") return first * SummonValuePerPoint;
        if (atom.Template is "NCR:CreateSoulInDraw" or "NCR:CreateSoulInDrawX")
            return first * SoulInDrawValuePerCard;
        // Dynamic-stat cards have no printed scalar to discover, so the generic no-number fallback valued them at
        // only 1 damage. These anchors reflect an ordinary live state (roughly 12-15 Block/Poison/Doom) and reserve
        // one real card-sized payoff without imposing any runtime cap on the dynamic result.
        if (atom.Template == "N:BlockEqualAllPoison") return 1_800;
        if (atom.Template == "CL:GainNextTurnBlockEqualCurrent") return 1_400;
        if (atom.Template == "NCR:DoomScaledDamage") return 2_050;
        if (atom.Template == "M:value"
            && spec is { Opcode: "modify_damage", Variant: "current_block" }) return 1_100;
        if (atom.Template == "I:DoubleBlockThisTurn") return 1_400;
        // These card-copy/play effects scale with their printed count. Nightmare's delayed copy is worth less per
        // card than an immediate free replay; Cascade's X=1 line is anchored near one full random card play.
        if (atom.Template == "I:CopySelectedCardNextTurn") return first * 1_700;
        if (atom.Template == "I:PlayTopXCards") return first * 1_100;
        // Returning the card which caused a trigger creates repeat access but not a fresh draw. Keep this shared
        // event-card operation slightly above the generic no-number utility fallback.
        if (atom.Template == "D:ReturnEventCardToHand") return 950;
        // Re-queuing this card on top of the draw pile is useful repeat access, but costs the next draw and does
        // not create card advantage. Shining Strike places the shared operation just below one ordinary draw.
        if (atom.Template == "R:PutThisOnDraw") return 580;
        // Stratagem spends an entire one-Energy Uncommon Power on recurring unrestricted draw-pile tutoring.
        // With the shuffle package valued at at least 1.25 resolutions, 1,000 * 1.25 = 1,250, almost exactly its
        // native Uncommon cohort center (1,275). The old generic 650 fallback made the native package worth 812.5.
        if (atom.Template == "CL:ChooseDrawCardToHand") return 1_000;
        // Foregone Conclusion cross-checks the tutor price from the other direction: two unrestricted tutors one
        // turn later occupy its complete 1,250 native Rare-Skill cohort center. Thus each delayed tutor is worth
        // 625 (= 1,000 immediate tutor value * 62.5% delay factor). Keep the count live so its native 2 -> 3
        // upgrade gains 625 value instead of being hidden behind the old generic fixed proxy fallback.
        if (atom.Template == "I:ProxyAtomic_ForegoneConclusion") return first * 625;
        // Native whole-card utilities need explicit prices. Falling through to the old no-number 650/Proxy 1250
        // defaults made their generated recombinations consume only a fraction of the card they replace.
        if (atom.Template == "CL:ProxyAtomic_BeatDown") return 3_400;
        if (atom.Template == "I:ProxyAtomic_Eidolon") return 3_200;
        // Legacy schema 1-8 snapshots retain the former atomic proxy and its historical estimate. New cards split
        // Void Form into an independently priced 2.00x EndTurn payment plus this recurring rule. Ethereal remains
        // an ordinary keyword payment; there is intentionally no EndTurn/Ethereal interaction in component value.
        if (atom.Template == "A:ProxyAtomic_VoidForm") return 2_650;
        if (atom.Template == "A:VoidFormFirstCardsFree") return first * 7_364;
        if (atom.Template == "I:ProxyAtomic_Voltaic") return 5_050;
        if (atom.Template == "I:DiscardHandDrawSame") return 1_400;
        if (atom.Template == "I:DoubleAttackDamageNextTurn") return 1_800;
        // Colossus' compact atom contains the complete one-turn mitigation rule, not a bare condition.
        if (atom.Template == "C:untilTurnEnd") return 800;
        // Expose removes two defensive resources before applying its debuff. This no-number utility is positive.
        if (atom.Template == "T:RemoveBlockAndArtifact") return 400;
        // At the ordinary ten-Max-HP Osty state, triple Max HP is approximately 30 Block.
        if (atom.Template == "NCR:BlockTripleOstyMaxHp") return 3_600;
        if (atom.Template == "R:EnemiesLoseStrengthThisTurn") return first * 1_160;
        if (atom.Template == "R:TargetLoseStrengthThisTurn") return first * 400;
        // This HP-loss line ignores Strength but still chooses a random enemy; retain the targeting discount used
        // by random damage instead of valuing every point as controllable single-target damage.
        if (atom.Template == "NCR:TargetHpLoss") return first * 75;
        if (atom.Template is "CL:MoveSelectedAttackDrawToHand" or "CL:MoveSelectedSkillDrawToHand")
            return 1_500;
        // "Up to" is strictly stronger than an otherwise identical mandatory selection because the player can
        // decline when no profitable target exists. Its first slot therefore carries a flexibility premium, while
        // later slots decay: a normal hand rarely contains three equally desirable cards to remove. Mandatory
        // multi-select also grows slowly because every extra target must be supplied.
        if (atom.Template == "CL:ExhaustUpToHandCards")
            return PlayerSelectedExhaustValue(first, sourceZone: "hand", upTo: true);
        if (CardEffectRules.IsPlayerSelectedExhaust(atom))
            return PlayerSelectedExhaustValue(first, spec.SourceZone, upTo: spec.Flags.Contains("up_to"));
        if (atom.Template == "CL:DrawToFullHand") return 2_200;
        if (atom.Template == "CL:TransformSelectedHandCards") return first * 450;
        if (atom.Template == "NCR:AddRandomEtherealCardToHand") return first * 450;
        if (atom.Template == "N:NextTurnDraw") return first * 625;
        // Healing Osty is combat-local pet sustain, not run-persistent player healing.
        if (atom.Template == "NCR:HealOsty") return first * OstyHealValuePerPoint;
        if (atom.Template == "I:FreeHandThisTurn") return FreeHandThisTurnValue;
        // Sentry Mode creates one fixed two-value derivative every turn. Keep the amount live and reuse the
        // established Sweeping Gaze ~= two Shivs exchange rate (2 * 425) before trigger cadence is applied.
        if (atom.Template == "NCR:AddSweepingGazeToHand") return first * 850;
        // Reaper Form copies the actual unblocked Attack damage into Doom. Eight ordinary event Damage at Doom's
        // 0.8 conversion gives roughly 600 value per trigger; the persistent trigger multiplier prices its life.
        if (atom.Template == "NCR:ApplyEventDamageAsDoom") return 625;
        if (atom.Template == "NCR:AllEnemiesLoseEventHp") return 750;
        if (atom.Template == "NCR:CostDownWhenCreatureDies") return 3_000;
        // Infernal Blade is a complete one-Energy Uncommon payoff: the generated Attack replaces this card and
        // can be played for free immediately.  The old generic no-number fallback (650) priced it like a small
        // rider and allowed too much unrelated output beside it.  Keep the randomness discount, but reserve a
        // little more than one ordinary Common card's line budget.
        if (IsFreeRandomAttackToHand(atom)) return 1_100;
        // Knife Trap is a complete two-Energy Rare payoff. It replays an unbounded combat-built pile of Shivs,
        // including their enchantments and on-play synergies, so the generic 650-point no-number fallback lets it
        // appear as cheap filler. Price a conservative three-to-four-Shiv turn while retaining its setup cost.
        if (atom.Template == "I:PlayExhaustedShivsAtTarget") return 2_400;
        // Run-persistent growth is paid once but improves every later combat copy of this card. The native anchors
        // (The Scythe +5 damage; Genetic Algorithm +3 Block) place one point far above an immediate point. Block
        // retains the shared 1.2x damage exchange rate.
        if (atom.Template == "NCR:IncreaseThisCardDamageRun") return first * 600;
        if (atom.Template == "D:IncreaseThisCardBlockRun") return first * 720;
        if (atom.Template == "A:whenEnergySpent") return 1_500;
        if (atom.Template == "A:whenOneStarSpent") return 1_300;
        // Lethality's split modifier inherits the event Attack rather than a Damage line printed on the Power.
        // An ordinary first Attack is about 9 damage; 50% therefore contributes 4.5 damage per active turn. The
        // linked persistent-trigger multiplier supplies the expected three turns, reconstructing the former
        // 1,350-value native rule while allowing the trigger to exchange its payoff.
        if (atom.Template == "M:TriggeredAttackDamagePercent") return first * 9;
        // Phantom Blades is decomposed into two independently reusable rules. Retain is idempotent, while the
        // first-Shiv rider scales every turn and therefore pays a persistent per-point price.
        // Retain on every Shiv is useful but idempotent and requires future Shiv generation. Keep it below a
        // small immediate card line; the separate occurrence prior prevents it from saturating Silent Powers.
        if (atom.Template == "A:ruleShivsRetain") return 300;
        // Accuracy affects every Shiv rather than only the first Shiv each turn. Price the printed amount instead
        // of falling through to the flat AbilityRule fallback, which ignored both its amount and repeat potential.
        if (atom.Template == "A:ruleShivBonusDamage") return first * 450;
        if (atom.Template == "A:ruleFirstShivBonusDamage") return first * 180;
        // Seeking Edge is a one-Energy Rare Power containing this persistent rule plus Forge 7. The Forge line is
        // worth about 700; pricing the whole native card against the current one-cost Rare Power envelope leaves
        // roughly 1,700 for turning every later 10-damage Sovereign Blade hit into an all-enemy hit. This is above
        // the generic AbilityRule fallback while still accounting for the need to draw and play the Blade itself.
        if (atom.Template == "R:KingsSwordHitsAllEnemies") return 1_700;
        if (atom.Template == "R:PlayThisCard") return 6_500;
        if (atom.Template == "D:ReturnZeroCostDiscardToHand") return 1_200;
        if (atom.Template is "D:NextPowerCostsZero" or "I:NextSkillCostsZero" or "I:SetCostZero"
            or "NCR:SetCostZeroIfOstyAttacked") return 250;
        if (atom.Template is "C:whileInCombat" or "C:whileInCombatSkillCostReduction") return 1_000;
        if (atom.Template == "D:ExhaustAllStatuses")
            return (int)Math.Round(StatusExhaustValuePerCard * ExhaustedStatusTriggerFrequency);
        if (atom.Template == "CL:AddRandomAttackToHand") return first * 750;
        if (atom.Template == "I:PlayAtRandomEnemy") return 1_100;
        if (atom.Template == "I:TriggerPoisonNow") return 570;
        if (atom.Template == "CL:PlayTopDrawCard") return 710;
        // This has no printed numeric parameter and receives neither numeric generation/upgrades nor multi-hit
        // adaptation; its fixed price is the complete native one-Energy Rare card.
        if (atom.Template == "CL:DamageEqualCardsPlayedCombat") return GoldAxeDynamicDamageValue;
        if (atom.Template == "I:AddCardReward") return 2_500;
        if (atom.Template == "CL:ReturnThisToHand") return 1_400;
        if (atom.Template == "CL:ApplyWeakAll")
            return (int)Math.Round(DiminishingDurationStatusValue(first, WeakValuePerTurn) * 1.33d);
        if (atom.Template == "CL:ApplyVulnerableAll")
            return (int)Math.Round(DiminishingDurationStatusValue(first, VulnerableValuePerTurn) * 1.33d);
        if (atom.Template == "A:ProxyAtomic_ForbiddenGrimoire") return 2_700;
        if (atom.Template is "A:ProxyAtomic_Buffer" or "A:ruleRetainHand"
            or "A:rulePlayedSkillsGainSly" or "A:ruleUnblockedAttackPoison"
            or "A:ruleWeakEnemiesTakeMoreAttackDamage") return 2_200;
        if (atom.Template == "A:rule" && spec.Variant == "retain_block_between_turns") return 3_400;
        if (atom.Template == "A:rule" && spec.Variant == "first_card_block_doubled_each_turn") return 2_200;
        if (atom.Template == "A:ProxyAtomic_SwordSage") return 2_150;
        if (atom.Template is "CL:ProxyAtomic_Anointed" or "CL:ProxyAtomic_Alchemize") return 1_500;
        if (atom.Template == "CL:ProxyAtomic_Catastrophe") return 1_600;
        if (atom.Template == "I:ProxyAtomic_Transfigure") return 1_500;
        if (atom.Template == "I:Transform") return 710;
        // Jackpot is the clean native anchor: after its 25 Damage, the three random 0-cost cards occupy roughly
        // 3,960 value of its three-Energy Rare envelope, or 1,320 each. The former 300 price let a generic
        // "for each card played this combat" prefix (20 expected cards) buy a new 0-cost card on every count and
        // still scrape under the aggressive-mode ceiling.
        if (atom.Template == "CL:AddRandomZeroCostCardsToHand") return first * 1_320;
        if (atom.Template == "R:CostDownWhenDrawn") return 700;
        if (atom.Scope == OperationScope.AbilityTrigger)
            return (int)Math.Round(700 + 350 * Math.Min(3d, RelativeTriggerFrequency(atom)));
        if (atom.Scope == OperationScope.AbilityRule) return 1_350;
        if (atom.Template.Contains(":Proxy", StringComparison.Ordinal)) return 1_250;

        // Dualcast is a one-Energy Basic whose entire payoff is two activations of the same rightmost Orb. Treat
        // one activation as roughly three ordinary damage points: below Lightning's raw 8-damage Evoke because
        // the effect requires and consumes a pre-existing Orb, while still pricing Frost/Plasma utility. Shatter
        // normally reaches about three occupied slots, so “all Orbs twice” uses six such activations.
        if (atom.Template == "D:EvokeRightmostOrb") return first * 300;
        // Activating every occupied Orb has broader value than repeatedly activating only the rightmost Orb.
        // Use three occupied slots and four damage-equivalent points per activation; Shatter remains comfortably
        // inside the native-card residual band after its Exhaust payment.
        if (atom.Template == "D:EvokeAllTwice") return first * 3 * 400;

        // A generated Shiv is a delayed four-damage card that also consumes Hand space and a card play. Native
        // Blade Dance and Cloak and Dagger put its practical value slightly above four immediate damage, but well
        // below treating it as a free full card draw. Enchantments deliberately add no extra budget here.
        if (atom.Template is "N:CreateShiv" or "N:CreateInkShiv"
            && spec.Flags.Contains("shiv_reference"))
            return (int)Math.Round(first * 495d);

        if (CardEffectRules.IsEnemyDamage(atom))
        {
            return (int)Math.Round(AdaptedMultiHitDamage(first, hits) * DamageValueMultiplier(atom) * 100d);
        }
        // Strike 6 and Defend 5 establish the cleanest starting-card exchange rate: one point of Block is worth
        // about 1.2 points of damage.  The old 0.85 multiplier inverted that relationship and made Block-heavy
        // cards look weaker than their native equivalents to whole-card floors and upgrade valuation.
        if (spec.Flags.Contains("block_reference")) return first * 120;
        if (spec.Flags.Contains("draw_reference")) return EstimatedDrawValue(first);
        if (atom.Template == "NCR:GainEnergy") return first * NecrobinderEnergyValuePerPoint;
        if (CardEffectRules.IsEnergyGainOperation(atom)) return first * OrdinaryEnergyValuePerPoint;
        // Wraith Form is the clean native joint anchor. At three Energy its two Intangible stacks must consume
        // essentially an Ancient card's complete upper-band allowance after paying for the repeated Dexterity
        // loss. Pricing Intangible at only 42 Damage-equivalent left standalone Intangible powers far too cheap.
        // Keep this paired with NegativeEffectTuning's 11.5-Damage-equivalent Dexterity-loss payment: with the
        // native 2 Intangible and 2.4 expected turn-start losses, Wraith Form evaluates to about 8,160.
        if (spec.Flags.Contains("intangible_reference")) return first * 7_500;
        if (atom.Template == "D:GainOrbSlots") return first * OrbSlotValuePerSlot;
        if (CardEffectRules.IsEnemyStrengthReduction(atom))
        {
            var allEnemies = spec.Flags.Contains("all_enemies_reference");
            if (spec.Flags.Contains("this_turn_reference"))
                return first * (allEnemies ? 300 : 170);
            return first * (allEnemies ? 2_000 : 1_500);
        }
        if (CardEffectRules.IsPositivePermanentStatGain(atom))
            // Permanent combat stats are not ordinary one-shot numbers. Brand demonstrates that even one
            // reusable Strength needs a substantial payment, so cheap pairings must reserve most of the card.
            return first * (atom.Template == "D:GainFocus" ? 1_400 : 1_200);
        if (spec.Flags.Contains("positive_focus_reference")) return first * 650;
        // Vigor is consumed by the next Attack instead of persisting through combat. Terraforming and Prep Time
        // place it close to 1.8 Damage-equivalent per point; treating it like permanent Strength overprices both.
        if (atom.Template is "R:GainVigor" or "CL:GainVigor") return first * VigorValuePerPoint;
        if (spec.Flags.Contains("positive_strength_dexterity_reference")) return first * 525;
        // Native anchors agree closely on roughly 3.2 damage-equivalent value per stack: Stone Armor is a
        // one-Energy Uncommon Power for 4 Plating, Eternal Armor is a three-Energy Rare for 9, and Neutron Aegis
        // pays one Energy plus five Stars for 8. The former 1.9 rate substantially underpriced this decaying but
        // persistent Block source and let it coexist with too much unrelated output.
        if (spec.Flags.Contains("plating_reference")) return first * 435;
        // Weak/Vulnerable stacks principally extend duration rather than multiplying their per-turn effect.
        // A single smooth curve preserves the one-stack anchors while preventing 3-5 turn applications from
        // receiving linear full-combat value; it also avoids per-amount breakpoints or native-card exceptions.
        if (spec.Flags.Contains("vulnerable_reference"))
            return DiminishingDurationStatusValue(first, VulnerableValuePerTurn);
        if (spec.Flags.Contains("weak_reference"))
            return DiminishingDurationStatusValue(first, WeakValuePerTurn);
        if (spec.Flags.Contains("poison_reference"))
        {
            // Deadly Poison (1 Energy, 5 Poison) is the clean native anchor: one Poison is worth roughly two
            // immediate damage points after accounting for its delayed decay. All-enemy and repeated applications
            // must retain their multiplicity here or whole-card floors systematically underprice them.
            var area = spec.Flags.Contains("all_enemies_reference") ? 1.55d : 1d;
            return (int)Math.Round(first * hits * area * 200d);
        }
        // Caltrops (1 Energy, 3 Thorns) and Abrasive (3 Energy, 4 Thorns plus Dexterity) show that Thorns is a
        // persistent combat stat, not an ordinary one-point scalar.
        if (atom.Template == "N:Thorns") return first * 350;
        if (spec.Flags.Contains("doom_reference"))
        {
            // Doom is a delayed threshold payoff, so one stack remains worth less than one point of immediate
            // Damage. Its targeting geometry is nevertheless identical to Damage: random targeting is less
            // valuable per printed stack, while applying the same stack count to every enemy is substantially
            // more valuable. Keeping that multiplier here and in Doom's numeric sampler prevents all-enemy Doom
            // from receiving both the full single-target amount and a falsely cheap whole-card valuation.
            return (int)Math.Round(first * 80d * DamageValueMultiplier(atom));
        }
        // Healing and maximum HP persist beyond the current combat and are restricted to Powers/Exhaust cards.
        // Not Yet (2 Energy Rare, Exhaust, Heal 10) anchors healing near five Damage points per HP after the
        // resource/downside curve. Feed (1 Energy Rare, 10 Damage, conditional Max HP 3, Exhaust) similarly places
        // one Max HP near nine Damage once its Fatal cadence is applied. These run-persistent rewards must not coexist
        // with a full ordinary payoff merely because their printed integers look small.
        if (spec.Flags.Contains("heal_reference")) return first * 500;
        if (spec.Flags.Contains("max_hp_reference")) return first * 900;
        // Native Wrought in War prints equal Damage and Forge values, which is the most direct exchange-rate
        // sample. Forge used to be priced at 2.3 damage per point, allowing tiny Forge-only capstones through.
        if (spec.Flags.Contains("forge_reference")) return first * 100;
        if (spec.Flags.Contains("star_gain_reference")) return first * 260;
        // Without a resolved Orb slot, use Lightning as the neutral reference: one Channel is roughly five
        // delayed damage. Whole-card balancing uses the GeneratorOperation overload below to retain the rolled
        // Orb type and its exact relative value.
        if (spec.Flags.Contains("orb_channel_reference")) return first * 500;
        if (spec.Flags.Contains("add_one_card_to_hand_reference")) return 380;
        if (IsStaticExtraDamageHits(atom)) return ExpectedExtraDamageHits(atom) * 1_000;
        if (atom.Scope == OperationScope.Modifier) return explicitValues.Length == 0 ? 700 : first * 240;
        return explicitValues.Length == 0 ? 650 : first * 100;
    }

    private static bool IsFreeRandomAttackToHand(ComponentAtom atom) =>
        atom.Template == "I_CREATE_RANDOM_ATTACK"
        || atom.Template == "I:Create"
            && OperationRuntimeSpecCompiler.GetOrCompile(atom) is
                { Opcode: "create_card", Variant: "random_attack_zero_cost_this_turn" };

    private static int RandomColorlessCardValue => (int)Math.Round(
        RandomGeneratedCardValue * RandomColorlessPoolValueMultiplier, MidpointRounding.AwayFromZero);

    internal static int PlayerSelectedExhaustValue(int count, string sourceZone, bool upTo)
    {
        count = Math.Max(1, count);
        if (!upTo)
            // Mandatory selected Exhaust is restricted to one target by validation/generation. Ignore any legacy
            // excess here so malformed snapshots cannot buy extra positive budget before being rejected.
            return sourceZone == "draw" ? 850 : 350;
        // The first optional slot carries the right to decline. Every further ceiling slot is useful, but has a
        // much lower and stable marginal value because additional profitable targets are increasingly uncertain.
        return 450 + (count - 1) * 250;
    }

    /// <summary>
    /// Value per printed point of direct damage. Random targeting is a drawback, so it is worth less than chosen
    /// single-target damage and therefore receives a larger printed number for the same budget. Repeated random
    /// hits recover a little value from Strength/on-hit scaling. Area damage is worth substantially more per
    /// printed point, with an additional premium for repeated area hits.
    /// </summary>
    internal static double DamageValueMultiplier(ComponentAtom atom)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(atom);
        return DamageValueMultiplier(atom.Template, spec);
    }

    private static double DamageValueMultiplier(GeneratorOperation operation) =>
        DamageValueMultiplier(operation.Template, OperationRuntimeSpecCompiler.GetOrCompile(operation));

    /// <summary>One printed Damage point resolved once, before intrinsic/linked hit-count multiplication.</summary>
    internal static double SingleResolutionDamagePointValue(GeneratorOperation operation) =>
        DamageValueMultiplier(operation) * 100d;

    private static double DamageValueMultiplier(string template, OperationRuntimeSpec spec)
    {
        var hits = Math.Clamp(spec.Values.FirstOrDefault(value => value.Id is "hits" or "repeat_count")
            ?.BaseValue ?? 1, 1, 8);
        if (template.StartsWith("N:RandomD", StringComparison.Ordinal)
            || spec.Flags.Contains("random_enemy_reference"))
            return hits > 1 ? 0.95d : 0.90d;
        if (template.StartsWith("N:AllD", StringComparison.Ordinal)
            || template == "NCR:OstyAllDamage"
            || spec.Flags.Contains("all_enemies_reference"))
            return hits > 1 ? 1.75d : 1.55d;
        return 1d;
    }

    /// <summary>
    /// Consecutive hits gain one point of adaptation value for every hit after the first. This represents
    /// Strength/on-hit compatibility without pretending that each hit is a completely independent damage line:
    /// 3 damage 3 times is therefore worth 3*3+3-1 = 11 before its targeting multiplier.
    /// </summary>
    internal static int AdaptedMultiHitDamage(int damagePerHit, int hits)
    {
        damagePerHit = Math.Max(0, damagePerHit);
        hits = Math.Max(1, hits);
        return damagePerHit * hits + hits - 1;
    }

    internal static int EstimatedEffectValue(GeneratorOperation operation)
    {
        if (CardEffectRules.IsDirectOrbChannel(operation))
        {
            var count = Math.Max(1, OperationRuntimeSpecCompiler.StaticLiteralValue(operation, "amount", 1));
            return count * OrbSlotCatalog.ChannelValue(operation.OrbOutputId, operation.Template);
        }

        return EstimatedEffectValue(new ComponentAtom(operation.Template, operation.Scope,
            operation.ChineseText, operation.RequiresSingleTarget, CardReferenceRequirement.None)
            { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(operation) });
    }

    internal static int PoisonExtraTriggerValue(int amount) => Math.Max(1, amount) * 1_350;

    /// <summary>
    /// The fourth and later drawn cards are increasingly likely to collide with hand size, overdraw or cards that
    /// cannot be used this turn. Their marginal value therefore decays, even though generation separately makes
    /// these large numbers rarer so the lower valuation cannot increase their occurrence.
    /// </summary>
    private static int EstimatedDrawValue(int count)
    {
        count = Math.Max(1, count);
        // Draw is slightly more valuable than the former 4.5-damage exchange rate. This keeps Draw 2 a modest
        // one-cost template. Four-or-more Draw keeps its existing general high-count premium relative to the
        // balanced upper envelope, so widening every rarity ceiling does not accidentally normalize Draw 4 filler.
        if (count <= 3) return count * 460;
        var value = 3 * 460d;
        var marginal = 300d;
        for (var index = 3; index < count; index++)
        {
            value += marginal;
            marginal *= 0.72d;
        }
        return (int)Math.Round(value * ComponentAssemblyGenerator.BalancedUpperBoundMultiplier);
    }

    private static int DiminishingDurationStatusValue(int turns, int oneTurnValue) =>
        (int)Math.Round(Math.Sqrt(Math.Max(1, turns)) * oneTurnValue, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Relative opportunities per turn for persistent triggers, or success probability for one-shot conditions.
    /// These figures are inferred from normal v111 deck cadence rather than treated as exact simulation results.
    /// </summary>
    internal static double RelativeTriggerFrequency(GeneratorOperation trigger) =>
        RelativeTriggerFrequency(trigger.Template, OperationRuntimeSpecCompiler.GetOrCompile(trigger));

    internal static double RelativeTriggerFrequency(ComponentAtom trigger) =>
        RelativeTriggerFrequency(trigger.Template, OperationRuntimeSpecCompiler.GetOrCompile(trigger));

    private static double RelativeTriggerFrequency(string template, OperationRuntimeSpec spec) =>
        TryCalibratedTriggerFrequency(template, spec, out var frequency) ? frequency : 0.75d;

    private static bool TryCalibratedTriggerFrequency(string template, OperationRuntimeSpec spec,
        out double frequency)
    {
        var threshold = Math.Max(1, spec.Values.FirstOrDefault(value =>
            value.Id is "threshold" or "duration")?.BaseValue
            ?? spec.Values.FirstOrDefault(value => value.Explicit)?.BaseValue ?? 1);
        if (TryTemplateTriggerFrequency(template, threshold, out frequency)) return true;
        var kind = spec.Trigger?.Kind ?? spec.Condition?.Kind;
        frequency = kind switch
        {
            "strike_card_drawn" => 0.65d,
            "vulnerable_applied" => 0.5d,
            "owner_hp_lost_during_turn" => 0.55d,
            "nth_attack_played_this_turn" => 0.3d,
            "turn_end_if_self_in_exhaust" => 0.35d,
            "self_exhausted" => 0.7d,
            "card_exhausted_this_turn" => 0.55d,
            "owner_lost_hp_this_turn" => 0.55d,
            "next_attack" => 0.9d,
            "card_played" => 3.2d,
            "attack_played" => 1.55d,
            "skill_played" => 1.45d,
            "attack_damaged_enemy" or "attack_dealt_damage" => 1.8d,
            "card_drawn" or "card_drawn_during_turn" => 4.8d,
            "block_gained" => 1.3d,
            "for_each_exhausted_card" or "for_each_exhausted_non_attack" => 0.7d,
            "card_exhausted" => 1.3d,
            "card_generated" => 0.65d,
            "doom_applied" => 0.55d,
            "orb_channeled" => 0.9d,
            "lightning_orb_evoked" => 0.55d,
            "osty_attacks_target" => 0.8d,
            "creature_died" => 0.3d,
            "attack_received" => 2.5d,
            "fatal" => FatalTriggerFrequency,
            "hand_empty" => 0.16d,
            "target_has_poison" => 0.55d,
            "target_has_vulnerable" => 0.62d,
            "exhaust_pile_minimum" => 0.3d,
            "osty_attacked_this_turn" => 0.55d,
            "doom_applied_this_turn" => 0.45d,
            "turn_start" => 1d,
            _ when spec.Flags.Contains("any_card_played_reference") => 3.2d,
            _ when spec.Flags.Contains("attack_damage_reference") => 1.8d,
            _ when spec.Flags.Contains("attack_received_reference") => 2.5d,
            _ when spec.Flags.Contains("target_vulnerable_reference") => 0.62d,
            _ when spec.Flags.Contains("turn_start_reference") => 1d,
            _ when spec.Flags.Contains("this_turn_reference") && spec.Condition is not null => 0.45d,
            _ => double.NaN
        };
        return !double.IsNaN(frequency);
    }

    internal static bool TryStructuredTriggerFrequencyForAudit(GeneratorOperation operation,
        out double frequency) => TryCalibratedTriggerFrequency(operation.Template,
        OperationRuntimeSpecCompiler.GetOrCompile(operation), out frequency);

    private static double CardsPlayedThresholdFrequency(int threshold) => threshold switch
    {
        <= 1 => 3.2d,
        2 => 1.35d,
        3 => 0.80d,
        4 => 0.50d,
        5 => 0.32d,
        _ => Math.Max(0.12d, 0.35d * 5d / threshold)
    };

    private static bool TryTemplateTriggerFrequency(string template, int threshold, out double frequency)
    {
        frequency = template switch
        {
            "A:whenEnergySpent" => 3.1d / threshold,
            "A:whenOneStarSpent" => 2d / threshold,
            "A:whenEnergyCostAtLeast" or "NCR:WheneverHighCostCardPlayed" => threshold switch
            {
                <= 1 => 2.2d,
                2 => 0.75d,
                _ => 0.3d
            },
            "A:turnStart" or "D:turnStart" or "A:turnEnd" => 1d,
            "A:firstCardPlayedEachTurn" => 1d,
            "NCR:FirstAttackPlayedEachTurn" => 1d,
            "A:firstZeroCostAttackPlayedEachTurn" => 0.55d,
            "A:whenSkillPlayed" => 1.45d,
            "CL:WheneverAttackPlayed" => 1.55d,
            "A:whenAttackDealsDamage" => 1.8d,
            "A:whenCardExhausted" => 1.3d,
            "A:whenCardGenerated" => 1.1d,
            "A:whenCardDrawnDuringTurn" => 4.8d,
            "A:whenDebuffApplied" => 1.5d,
            "A:whenDoomApplied" => 1.1d,
            "A:whenEtherealDrawn" or "A:whenEtherealPlayed" => 0.7d,
            "A:whenFirstStatusDrawn" => 0.4d,
            "A:whenLightningEvoked" => 0.55d,
            "A:whenOrbChanneled" => 1.1d,
            "A:whenOstyLosesHp" => 0.65d,
            "A:whenPowerPlayed" => 0.75d,
            "A:whenSoulPlayed" => 2.2d,
            "A:whenStarsChanged" => 2.2d,
            "A:whenStatusGenerated" => 0.45d,
            "D:ForEachOrb" => 3d,
            "D:ForEachEnemy" => 2d,
            "D:ForEachUniqueOrb" => 2.5d,
            // Helix Drill excludes this card's payment. Use two other Energy as the ordinary-turn baseline:
            // thresholds 1/2 therefore resolve about 2/1 times and share the generic payoff scaler.
            "D:ForEachEnergySpentThisTurn" => 2d / threshold,
            "D:ForEachExhaustedStatus" => ExhaustedStatusTriggerFrequency,
            "D:WheneverStatusGenerated" => 0.45d,
            "D:NextTurnsStart" => Math.Clamp(threshold, 1, 4),
            // Unlike a persistent draw Power, this listener is installed by the source card during the turn.
            // Opening-hand draws have already happened, so use the same post-arming opportunity baseline as
            // “after you play this card, whenever you play another card” rather than the 4.8 full-turn draw rate.
            "C:untilTurnEndCardDrawn" => 3.2d,
            // Fiend Fire, Second Wind and Stoke resolve once for every card consumed by their immediately
            // preceding all-hand Exhaust. This is a live count, not the 0.7/turn cadence of a generic Exhaust
            // event trigger; normal hands contribute roughly three to four eligible cards.
            "C:forEach" => 6d,
            "C:forEachDiscarded" => 1.1d,
            "C:playableIfDrawPileEmpty" => 0.08d,
            "CL:IfHandEmpty" => 0.16d,
            "CL:IfNoAttacksInHand" => 0.3d,
            "C:ifTargetPoisoned" => 0.55d,
            "C:ifLastDrawnSkill" => 0.42d,
            "D:IfHasFrost" => 0.62d,
            "D:IfEnemyIntendsAttack" => 0.62d,
            "D:IfCardsPlayedBelow" => 0.7d,
            "D:IfFatal" or "C:ifFatal" or "CL:IfFatal" or "R:IfFatal" => FatalTriggerFrequency,
            "R:IfEnergyXAtLeast" => 0.45d,
            "R:AtTurnStartIfInExhaust" => 0.35d,
            "R:NextTurn" or "NCR:NextTurn" or "CL:AtNextTurnStart" => 0.45d,
            "NCR:IfOstyAttackedThisTurn" => 0.55d,
            "NCR:IfDoomAppliedThisTurn" => 0.45d,
            "NCR:IfOstyAlive" => 0.85d,
            "NCR:IfFirstPlayThisTurn" => 0.82d,
            "C:grantNextAttacksThisTurn" => Math.Clamp(threshold, 1, 3),
            "C:grantNextAttack" => 1d,
            "NCR:ForEachEtherealPlayedCombat" => 2d,
            "NCR:ForEachCardDrawnThisTurn" => 4.8d,
            "NCR:ForEachDoomThreshold" => 2d,
            "NCR:ForEachOstyAttackThisTurn" => 1.2d,
            "NCR:ForEachExhaustedSoul" => 2.25d,
            "NCR:ForEachOstyAttackCard" => 6.5d,
            "NCR:WheneverCreatureDies" => 0.3d,
            "NCR:WheneverCardPlayedThisTurn" => 3.2d,
            "NCR:WheneverOstyAttacksTargetThisTurn" => 0.8d,
            "R:ForEachPriorAttackHitOnTarget" => 2d,
            "R:ForEachStarCostCard" => 5d,
            "R:ForEachSkillPlayedThisTurn" => 2d,
            "R:ForEachStarGainedThisTurn" => 1.5d,
            "R:ForEachGeneratedCardCombat" => 3d,
            "R:WheneverDrawn" => 1d,
            "R:AtTurnEndWhenTopOfDraw" => 0.2d,
            "R:IfCardsPlayedAtLeastThisTurn" => 0.45d,
            "CL:ForEachCardPlayedCombat" => 20d,
            "CL:ForEachDrawPileCard" => 10d,
            "CL:AfterTurns" => 0.25d,
            "CL:EveryCardsDrawn" => 4.8d / threshold,
            "CL:EveryCardsPlayedThisTurn" => CardsPlayedThresholdFrequency(threshold),
            "CL:FirstAttackOrSkillEachTurn" => 1d,
            "CL:WheneverDrawPileShuffled" => DrawPileShuffleTriggerFrequency,
            _ => double.NaN
        };
        return !double.IsNaN(frequency);
    }

    /// <summary>
    /// Every trigger/count prefix in the component catalogs must have an explicit cadence rule.  Text rules are
    /// retained for the few deliberately shared templates (A:when/C:if), but an unknown trigger may no longer
    /// silently look balanced merely because the historical 0.75 fallback happened to be plausible.
    /// </summary>
    internal static bool TryLegacyCalibratedTriggerFrequencyForAudit(string template, string text,
        out double frequency)
    {
        var threshold = Math.Max(1, Number.Match(text) is { Success: true } match ? int.Parse(match.Value) : 1);
        frequency = template switch
        {
            // A Regent normally spends about 3 Energy and 1.8-2.2 Stars in a developed turn.
            "A:whenEnergySpent" => 3.1d / threshold,
            "A:whenOneStarSpent" => 2d / threshold,
            "A:whenEnergyCostAtLeast" or "NCR:WheneverHighCostCardPlayed" => threshold switch
            {
                <= 1 => 2.2d,
                2 => 0.75d,
                _ => 0.3d
            },
            "A:turnStart" or "D:turnStart" or "A:turnEnd" => 1d,
            "A:firstCardPlayedEachTurn" => 1d,
            "NCR:FirstAttackPlayedEachTurn" => 1d,
            "A:firstZeroCostAttackPlayedEachTurn" => 0.55d,
            "A:whenSkillPlayed" => 1.45d,
            "CL:WheneverAttackPlayed" => 1.55d,
            // Attack-damage triggers can fire once per hit rather than merely once per card, so multi-hit
            // attacks push their normal cadence slightly above the generic Attack-play trigger.
            "A:whenAttackDealsDamage" => 1.8d,
            "A:whenCardExhausted" => 1.3d,
            "A:whenCardGenerated" => 1.1d,
            "A:whenCardDrawnDuringTurn" => 4.8d,
            "A:whenDebuffApplied" => 1.5d,
            "A:whenDoomApplied" => 1.1d,
            "A:whenEtherealDrawn" or "A:whenEtherealPlayed" => 0.7d,
            "A:whenFirstStatusDrawn" => 0.4d,
            "A:whenLightningEvoked" => 0.55d,
            "A:whenOrbChanneled" => 1.1d,
            "A:whenOstyLosesHp" => 0.65d,
            "A:whenPowerPlayed" => 0.75d,
            "A:whenSoulPlayed" => 2.2d,
            "A:whenStarsChanged" => 2.2d,
            "A:whenStatusGenerated" => 0.45d,
            // Compile Driver-style clauses multiply their payoff by the number of distinct live Orb types.
            // Two to three distinct types is a deliberately conservative developed-turn estimate; the upper tail
            // remains available through decks that maintain all four ordinary types.
            "D:ForEachOrb" => 3d,
            "D:ForEachEnemy" => 2d,
            "D:ForEachUniqueOrb" => 2.5d,
            "D:ForEachEnergySpentThisTurn" => 2d / threshold,
            "D:ForEachExhaustedStatus" => ExhaustedStatusTriggerFrequency,
            "D:WheneverStatusGenerated" => 0.45d,
            "D:NextTurnsStart" => Math.Clamp(threshold, 1, 4),
            "C:untilTurnEndCardDrawn" => 3.2d,
            "C:forEach" => 6d,
            "C:forEachDiscarded" => 1.1d,
            "C:playableIfDrawPileEmpty" => 0.08d,
            "CL:IfHandEmpty" => 0.16d,
            "CL:IfNoAttacksInHand" => 0.3d,
            "C:ifTargetPoisoned" => 0.55d,
            "C:ifLastDrawnSkill" => 0.42d,
            "D:IfHasFrost" => 0.62d,
            "D:IfEnemyIntendsAttack" => 0.62d,
            "D:IfCardsPlayedBelow" => 0.7d,
            "D:IfFatal" or "C:ifFatal" or "CL:IfFatal" or "R:IfFatal" => FatalTriggerFrequency,
            "R:IfEnergyXAtLeast" => 0.45d,
            "R:AtTurnStartIfInExhaust" => 0.35d,
            // Waiting a full turn is a meaningful one-shot liability. It should print a visibly larger payoff
            // than the same immediate line instead of receiving the old default 8% premium.
            "R:NextTurn" or "NCR:NextTurn" or "CL:AtNextTurnStart" => 0.45d,
            "NCR:IfOstyAttackedThisTurn" => 0.55d,
            "NCR:IfDoomAppliedThisTurn" => 0.45d,
            "NCR:IfOstyAlive" => 0.85d,
            // Almost every first hand-play succeeds, but replays/copies later in the turn may fail this gate.
            // Treat it as a sub-one-shot cadence rather than the generic difficult-condition 0.45 estimate.
            "NCR:IfFirstPlayThisTurn" => 0.82d,
            "C:grantNextAttacksThisTurn" => Math.Clamp(Number.Match(text) is { Success: true } nextAttacks
                ? int.Parse(nextAttacks.Value) : 1, 1, 3),
            "C:grantNextAttack" => 1d,
            // Count-based dependency prefixes use their expected live count, not a generic condition chance.
            "NCR:ForEachEtherealPlayedCombat" => 2d,
            "NCR:ForEachCardDrawnThisTurn" => 4.8d,
            "NCR:ForEachDoomThreshold" => 2d,
            "NCR:ForEachOstyAttackThisTurn" => 1.2d,
            "NCR:ForEachExhaustedSoul" => 2.25d,
            "NCR:ForEachOstyAttackCard" => 6.5d,
            "NCR:WheneverCreatureDies" => 0.3d,
            "NCR:WheneverCardPlayedThisTurn" => 3.2d,
            "NCR:WheneverOstyAttacksTargetThisTurn" => 0.8d,
            "R:ForEachPriorAttackHitOnTarget" => 2d,
            "R:ForEachStarCostCard" => 5d,
            "R:ForEachSkillPlayedThisTurn" => 2d,
            "R:ForEachStarGainedThisTurn" => 1.5d,
            "R:ForEachGeneratedCardCombat" => 3d,
            "R:WheneverDrawn" => 1d,
            "R:AtTurnEndWhenTopOfDraw" => 0.2d,
            "R:IfCardsPlayedAtLeastThisTurn" => 0.45d,
            "CL:ForEachCardPlayedCombat" => 20d,
            "CL:ForEachDrawPileCard" => 10d,
            "CL:AfterTurns" => 0.25d,
            "CL:EveryCardsDrawn" => 4.8d / threshold,
            "CL:EveryCardsPlayedThisTurn" => CardsPlayedThresholdFrequency(threshold),
            "CL:FirstAttackOrSkillEachTurn" => 1d,
            "CL:WheneverDrawPileShuffled" => DrawPileShuffleTriggerFrequency,
            _ when text.Contains("抽到名字中有“打击”", StringComparison.Ordinal) => 0.65d,
            _ when text.Contains("施加易伤", StringComparison.Ordinal) => 0.5d,
            _ when text.Contains("在回合内失去生命", StringComparison.Ordinal) => 0.55d,
            _ when text.Contains("打出第3张攻击牌", StringComparison.Ordinal) => 0.3d,
            _ when template == "C:after" && text.Contains("回合结束", StringComparison.Ordinal) => 0.35d,
            _ when template == "C:after" && text.Contains("被消耗", StringComparison.Ordinal) => 0.7d,
            _ when text.Contains("曾消耗过牌", StringComparison.Ordinal) => 0.55d,
            _ when text.Contains("本回合失去过生命", StringComparison.Ordinal) => 0.55d,
            _ when text.Contains("当你打出下一张攻击牌时", StringComparison.Ordinal) => 0.9d,
            _ when IsAnyCardPlayedTriggerText(text) => 3.2d,
            _ when text.Contains("每当你打出一张攻击牌", StringComparison.Ordinal) => 1.55d,
            _ when text.Contains("每当你打出一张技能牌", StringComparison.Ordinal) => 1.45d,
            _ when IsAttackDamageTriggerText(text) => 1.8d,
            _ when text.Contains("每当你抽到一张牌", StringComparison.Ordinal)
                || text.Contains("每抽一张牌", StringComparison.Ordinal) => 4.8d,
            _ when text.Contains("每当你获得格挡", StringComparison.Ordinal) => 1.3d,
            _ when text.Contains("每消耗一张牌", StringComparison.Ordinal) => 1.3d,
            _ when text.Contains("每当有一张牌被消耗", StringComparison.Ordinal) => 1.3d,
            _ when text.Contains("每当你生成一张牌", StringComparison.Ordinal) => 0.65d,
            _ when text.Contains("每当你给予易伤", StringComparison.Ordinal) => 0.5d,
            _ when text.Contains("每当你给予灾厄", StringComparison.Ordinal) => 0.55d,
            _ when text.Contains("每当你生成一个充能球", StringComparison.Ordinal) => 0.9d,
            _ when text.Contains("每当你激发", StringComparison.Ordinal) => 0.55d,
            _ when text.Contains("每当奥斯提攻击", StringComparison.Ordinal) => 0.8d,
            _ when text.Contains("每当一名生物死亡", StringComparison.Ordinal) => 0.3d,
            // Flame Barrier-style retaliation is exposed to every enemy hit. Multi-enemy turns and multi-hit
            // attacks make 2.5 resolutions a better ordinary-turn estimate than a one-shot success probability.
            _ when text.Contains("受到一次攻击", StringComparison.Ordinal) => 2.5d,
            _ when text.StartsWith("斩杀时", StringComparison.Ordinal) => FatalTriggerFrequency,
            _ when text.Contains("手牌为空", StringComparison.Ordinal) => 0.16d,
            _ when text.Contains("拥有中毒", StringComparison.Ordinal) => 0.55d,
            _ when text.Contains("拥有易伤", StringComparison.Ordinal) => 0.62d,
            _ when text.Contains("消耗牌堆中至少", StringComparison.Ordinal) => 0.3d,
            _ when text.Contains("奥斯提在本回合攻击过", StringComparison.Ordinal) => 0.55d,
            _ when text.Contains("本回合给予过灾厄", StringComparison.Ordinal) => 0.45d,
            _ when text.Contains("回合开始时", StringComparison.Ordinal) => 1d,
            _ when text.Contains("本回合", StringComparison.Ordinal) && text.StartsWith("如果", StringComparison.Ordinal) => 0.45d,
            _ => double.NaN
        };
        return !double.IsNaN(frequency);
    }

    private static bool IsAttackDamageTriggerText(string text) =>
        text.Contains("造成未被格挡的伤害时", StringComparison.Ordinal)
        || text.Contains("攻击造成未被格挡的伤害", StringComparison.Ordinal)
        || text.Contains("攻击对一名敌人造成伤害时", StringComparison.Ordinal)
        || text.Contains("攻击造成伤害时", StringComparison.Ordinal);

    private static bool IsAnyCardPlayedTriggerText(string text) =>
        text.Contains("每当你打出一张牌", StringComparison.Ordinal)
        || text.Contains("每打出一张牌", StringComparison.Ordinal)
        || text.Contains("每当你使用一张牌", StringComparison.Ordinal)
        || text.Contains("每使用一张牌", StringComparison.Ordinal);

    internal static bool IsPersistentAbilityTrigger(GeneratorOperation trigger) =>
        trigger.Scope == OperationScope.AbilityTrigger
        && trigger.Template != "CL:AfterTurns"
        && !OperationRuntimeSpecCompiler.GetOrCompile(trigger).Flags.Contains("this_turn_reference");

    /// <summary>Expected resolutions over the supplied active lifetime; one-shot gates stay probabilities.</summary>
    internal static double ExpectedTriggerResolutions(GeneratorOperation trigger, double persistentTurns = 1d)
    {
        var frequency = RelativeTriggerFrequency(trigger);
        if (!HasRepeatedOrMultiplicativePayoff(trigger)) return Math.Clamp(frequency, 0d, 1d);
        return Math.Max(0d, frequency) * (IsPersistentAbilityTrigger(trigger) ? persistentTurns : 1d);
    }

    internal static int PayoffScalePercent(IReadOnlyList<GeneratorOperation> previous)
    {
        var trigger = LinkedTrigger(previous);
        if (trigger is null) return 100;
        // Mind Blast is a multiplier by the entire live draw pile. Its printed number is a per-card coefficient,
        // not an ordinary one-shot damage amount, so it receives only a small fraction of a normal damage line.
        if (trigger.Template == "CL:ForEachDrawPileCard") return 10;
        if (trigger.Template == "CL:ForEachCardPlayedCombat") return 5;
        if (trigger.Template == "C:grantNextAttacksThisTurn")
            return CardEffectRules.NextAttackGrantCount(trigger) switch { 1 => 100, 2 => 60, _ => 42 };
        if (trigger.Template == "C:grantNextAttack") return 100;
        if (trigger.Template is "A:whenEnergyCostAtLeast" or "NCR:WheneverHighCostCardPlayed")
        {
            var threshold = Math.Clamp(OperationRuntimeSpecCompiler.StaticLiteralValue(
                trigger, "threshold", 2), 1, 3);
            return threshold switch { 1 => 36, 2 => 60, _ => 125 };
        }
        var frequency = RelativeTriggerFrequency(trigger);
        if (HasRepeatedOrMultiplicativePayoff(trigger))
        {
            // A payoff that can happen several times each turn, or is multiplied by a commonly nontrivial live
            // count, must be much smaller. This covers persistent Power triggers as well as one-turn effects such
            // as “after playing this, whenever you play a card” and “for each unique Orb”.
            var perTurnScale = frequency switch
            {
                >= 2.5d => 25,
                >= 1.5d => 34,
                >= 1d => 52,
                >= 0.7d => 60,
                >= 0.4d => 85,
                _ => 105
            };
            // A Power trigger usually remains active for several turns. Its printed amount is a per-trigger payoff,
            // so reserve value for that lifetime before the continuous effective-cost curve is applied. This is a
            // budget multiplier rather than a final-value cap and therefore follows all mode-wide value bonuses.
            return IsPersistentAbilityTrigger(trigger)
                ? Math.Max(1, (int)Math.Round(perTurnScale * 0.68d))
                : perTurnScale;
        }
        // One-shot gates do not deserve a full inverse-probability multiplier: otherwise rare conditions create
        // pathological numbers. They receive a controlled premium instead.
        return frequency switch
        {
            <= 0.15d => 220,
            <= 0.3d => 155,
            <= 0.5d => 138,
            <= 0.7d => 120,
            _ => 108
        };
    }

    internal static bool HasRepeatedOrMultiplicativePayoff(GeneratorOperation trigger)
    {
        if (trigger.Scope == OperationScope.AbilityTrigger && trigger.Template != "CL:AfterTurns") return true;
        if (trigger.Template is "D:ForEachUniqueOrb" or "NCR:WheneverCardPlayedThisTurn"
            or "C:grantNextAttacksThisTurn" or "C:untilTurnEndCardDrawn" or "D:NextTurnsStart"
            or "R:AtTurnStartIfInExhaust")
            return true;
        return OperationRuntimeSpecCompiler.GetOrCompile(trigger).Flags
            .Contains("repeated_or_multiplicative");
    }

    /// <summary>
    /// High-frequency means a repeatable trigger expected to resolve more than once per ordinary turn. A plain
    /// turn-start trigger is intentionally excluded so native once-per-turn Power structures remain available.
    /// </summary>
    internal static bool IsHighFrequencyTrigger(GeneratorOperation trigger) =>
        HasRepeatedOrMultiplicativePayoff(trigger) && RelativeTriggerFrequency(trigger) > 1d;

    internal static GeneratorOperation? HighFrequencyLinkedTrigger(IReadOnlyList<GeneratorOperation> previous)
    {
        var trigger = LinkedTrigger(previous);
        return trigger is not null && IsHighFrequencyTrigger(trigger) ? trigger : null;
    }

    private static int ReciprocalPercent(double valueMultiplier) =>
        (int)Math.Round(100d / Math.Max(0.01d, valueMultiplier), MidpointRounding.AwayFromZero);

    internal static int ScaleRewardCenter(ComponentAtom atom, int center, int benefitLines,
        GeneratedRarity rarity, IReadOnlyList<GeneratorOperation> previous, Random random,
        bool balancedValues = true)
    {
        if (!IsScalableReward(atom)) return center;
        // Keep the whole distribution a little above the native reference curve. Multi-effect cards still split
        // their budget, but no longer flatten every line so aggressively that interesting combinations feel
        // uniformly mediocre.
        var lineShare = benefitLines switch { <= 1 => 114, 2 => 92, 3 => 80, _ => 72 };
        lineShare += rarity switch
        {
            // Pull Rare slightly back toward the shared curve and give Ancient a clearer capstone step.
            GeneratedRarity.Rare => 0,
            GeneratedRarity.Ancient => 20,
            _ => 0
        };
        var triggerScale = PayoffScalePercent(previous);
        // Drawing from a repeatable trigger is materially stronger than drawing the same number of cards once:
        // it supplies recurring card advantage and can feed the engine that caused the trigger.  The whole-card
        // estimator below prices that compounding value explicitly; reduce the printed center by the inverse
        // factor here so ordinary low-rarity Powers do not turn a modest native payoff (for example Thunder's
        // 8 damage per Lightning Evoke) into Draw 2 per Evoke merely because both looked similar as one-shot lines.
        var triggeredDrawScale = OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("draw_reference")
            && LinkedTrigger(previous) is { } drawTrigger
            && HasRepeatedOrMultiplicativePayoff(drawTrigger)
                ? ReciprocalPercent(IsPersistentAbilityTrigger(drawTrigger)
                    ? PersistentTriggeredDrawValueMultiplier
                    : TemporaryTriggeredDrawValueMultiplier)
                : 100;
        var delayedScale = LinkedTrigger(previous) is null && CardEffectRules.IsDelayedEffect(atom) ? 140 : 100;
        // Strength comes from the shared sampler, never from a curated combo list. Keep ordinary rewards centered
        // near their native budget while retaining a small rarity-sensitive high tail. Common/Uncommon cards use
        // the tightest curve; Rare may still high-roll, and Ancient's printed multiplier is lower because its
        // line-share and single-effect capstone bonus already supply a separate rarity lift.
        var roll = random.Next(1000);
        var variancePercent = RewardVariancePercent(atom, rarity, roll);
        if (!balancedValues)
            variancePercent = MapToAggressiveBudgetDistribution(atom, rarity, variancePercent);
        return Math.Max(1, (int)Math.Round(
            (double)center * lineShare * triggerScale * triggeredDrawScale * variancePercent * delayedScale
            / 10_000_000_000d));
    }

    private static int RewardVariancePercent(ComponentAtom atom, GeneratedRarity rarity, int roll)
    {
        // A one-line 1-Energy Basic damage card now resolves to the intended compact 5-7 band. This is a shared
        // reward multiplier, so Block and every other scalable family receive the same lower-variance treatment.
        if (rarity == GeneratedRarity.Basic)
            return roll < 200 ? 75 : roll < 700 ? 90 : 103;

        // Energy has a separate narrow safety curve because a high roll of 3+ Energy is qualitatively different
        // from a few extra points of damage, Block, Doom or Poison.
        if (CardEffectRules.IsEnergyGainOperation(atom))
            return roll < 20 ? 125 : roll < 90 ? 110 : 100;

        return rarity switch
        {
            GeneratedRarity.Common => roll switch
            {
                < 30 => 100,
                < 100 => 99,
                < 280 => 98,
                < 800 => 97,
                < 950 => 96,
                _ => 95
            },
            GeneratedRarity.Uncommon => roll switch
            {
                < 30 => 97,
                < 100 => 96,
                < 280 => 95,
                < 800 => 94,
                < 950 => 93,
                _ => 92
            },
            GeneratedRarity.Rare => roll switch
            {
                < 30 => 110,
                < 100 => 105,
                < 280 => 102,
                < 800 => 100,
                < 950 => 98,
                _ => 95
            },
            GeneratedRarity.Ancient => roll switch
            {
                < 30 => 103,
                < 100 => 100,
                < 280 => 98,
                < 800 => 95,
                < 950 => 92,
                _ => 90
            },
            _ => 100
        };
    }

    /// <summary>
    /// Balanced mode is the canonical reference distribution for future numeric tuning and tests. Aggressive mode
    /// changes only the sampled scalable-reward budget: it preserves the balanced lower endpoint and linearly maps
    /// every quantile onto a range whose upper endpoint is 50% higher. Do not tune effect centers against this
    /// remapped mode, and do not apply it to thresholds, durations, marker values, or fixed safety caps.
    /// </summary>
    private static int MapToAggressiveBudgetDistribution(ComponentAtom atom, GeneratedRarity rarity,
        int balancedPercent)
    {
        var (minimum, maximum) = RewardVarianceBounds(atom, rarity);
        if (maximum <= minimum) return balancedPercent;
        var aggressiveMaximum = (int)Math.Round(maximum * 1.50d, MidpointRounding.AwayFromZero);
        var position = (balancedPercent - minimum) / (double)(maximum - minimum);
        return (int)Math.Round(minimum + position * (aggressiveMaximum - minimum),
            MidpointRounding.AwayFromZero);
    }

    private static (int Minimum, int Maximum) RewardVarianceBounds(ComponentAtom atom, GeneratedRarity rarity)
    {
        if (rarity == GeneratedRarity.Basic) return (75, 103);
        if (CardEffectRules.IsEnergyGainOperation(atom)) return (100, 125);
        return rarity switch
        {
            GeneratedRarity.Common => (95, 100),
            GeneratedRarity.Uncommon => (92, 97),
            GeneratedRarity.Rare => (95, 110),
            GeneratedRarity.Ancient => (90, 103),
            _ => (100, 100)
        };
    }

    internal static bool IsCondition(ComponentAtom atom) =>
        atom.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger
        || CardEffectRules.IsDependencyPrefix(atom);

    internal static bool NeedsNativeReference(ComponentAtom atom)
    {
        if (atom.Scope is OperationScope.AbilityTrigger or OperationScope.AbilityRule or OperationScope.Modifier)
            return true;
        if (atom.Template.Contains(":Proxy", StringComparison.Ordinal)) return true;
        return !OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("has_numeric_literal");
    }

    internal static bool IsScalableReward(ComponentAtom atom)
    {
        if (OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags
            .Contains(ComponentSemanticFlags.ScalableReward)) return true;
        if (atom.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger or OperationScope.AbilityRule)
            return false;
        var operation = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
            new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget,
            RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom));
        if (!CardEffectRules.IsBeneficialEffect(operation)) return false;
        return CardEffectRules.IsEnemyDamage(atom)
            || atom.Template is "N:B" or "N:Draw" or "N:E" or "N:NextTurnEnergy"
                or "D:GainEnergy" or "D:NextTurnEnergy" or "NCR:GainEnergy" or "NCR:NextTurnEnergy"
                or "R:GainEnergy" or "D:GainTemporaryFocus" or "T:Apply"
                or "NCR:ApplyDoom" or "NCR:ApplyDoomAll"
                or "NCR:Summon" or "NCR:CreateSoulInDraw"
                or "NCR:OstyDamage" or "NCR:OstyAllDamage" or "I:DrawAndBlockIfSkill"
                or "R:Forge"
            || OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("scalable_reward_wording");
    }

    internal static GeneratorOperation? LinkedTrigger(IReadOnlyList<GeneratorOperation> previous)
    {
        if (previous.LastOrDefault() is not { } last) return null;
        if (last.Scope is OperationScope.AbilityTrigger or OperationScope.ConditionalTrigger
            || CardEffectRules.IsDependencyPrefix(last))
            return last;
        if (!last.Parameters.TryGetValue("triggerIndex", out var index) || index < 0 || index >= previous.Count)
            return null;
        return previous[index];
    }

    /// <summary>
    /// Replay and repeated free plays copy an entire card rather than one printed number, so pricing them as
    /// ordinary “amount × 100” effects dramatically understates their real value. These estimates deliberately
    /// sit around a full strong card line per additional play; whole-card valuation separately multiplies a
    /// linked payoff by its trigger cadence.
    /// </summary>
    private static bool TryEstimateReplayValue(ComponentAtom atom, int first, int hits, out int value)
    {
        value = atom.Template switch
        {
            // Decisions, Decisions plays the selected Skill this many times for free, including its first play.
            // Decisions, Decisions pays three Energy-equivalent Stars, Exhausts, and uses most of the card on
            // this action. One selected free Skill is worth materially more than an ordinary damage line.
            "R:PlaySelectedSkillMultipleTimes" => hits * 1_400,
            // Playing an unknown Attack from hand is worth roughly one and a half ordinary Energy per card: the
            // card itself and its printed cost are both supplied, with randomness discounting target/control.
            "I:AutoPlayRandomAttackFromHand" => Math.Clamp(first, 1, 5) * 1_500,
            "I:ReplayNextSkills" => Math.Clamp(first, 1, 4) * 1_100,
            "I:ReplayAttack" => Math.Clamp(hits, 1, 4) * 1_100,
            // Echo Form's per-turn payoff is multiplied by its first-card trigger in card-level valuation.
            "D:ReplayEventCard" => 1_280,
            "CL:ProxyAtomic_HiddenGem" => Math.Clamp(first, 1, 4) * 1_100,
            "I:ProxyAtomic_SignalBoost" => 1_500,
            "A:ProxyAtomic_SwordSage" => 2_150,
            // Transfigure bundles a powerful permanent Replay grant with a +1-cost tradeoff.
            "I:ProxyAtomic_Transfigure" => 1_500,
            _ when OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("replay_reference") =>
                Math.Max(1_100, first * 1_100),
            _ when OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("extra_play_reference") =>
                Math.Max(1_100, hits * 1_100),
            _ => 0
        };
        return value > 0;
    }

    internal static bool IsReplayOrRepeatedPlayEffect(ComponentAtom atom) =>
        atom.Template is "R:PlaySelectedSkillMultipleTimes" or "I:ReplayNextSkills" or "I:ReplayAttack"
            or "D:ReplayEventCard" or "CL:ProxyAtomic_HiddenGem" or "I:ProxyAtomic_SignalBoost"
            or "A:ProxyAtomic_SwordSage" or "I:ProxyAtomic_Transfigure"
        || OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Any(flag =>
            flag is "replay_reference" or "extra_play_reference");

    private static int TriggerSelectionWeight(ComponentAtom atom)
    {
        if (!IsCondition(atom)) return 100;
        var frequency = RelativeTriggerFrequency(atom);
        if (atom.Scope == OperationScope.AbilityTrigger)
            return frequency switch { >= 2d => 45, >= 1d => 62, >= 0.65d => 78, _ => 88 };
        return frequency switch { <= 0.15d => 18, <= 0.3d => 35, <= 0.5d => 58, <= 0.7d => 78, _ => 92 };
    }

    internal static int ExpectedLineValue(GeneratedRarity rarity, double cost)
    {
        var rarityBase = 800 + (rarity switch
        {
            GeneratedRarity.Basic => -100,
            GeneratedRarity.Common => 0,
            GeneratedRarity.Uncommon => 120,
            GeneratedRarity.Rare => 280,
            GeneratedRarity.Ancient => 450,
            _ => 0
        });
        // X has no fixed payment to place on the continuous curve. Keep its legacy neutral reference while every
        // fixed Energy/Star combination, including half-Energy Star costs, uses the shared nonlinear function.
        return cost < 0
            ? 650 + (rarityBase - 800)
            : Math.Max(1, (int)Math.Round(rarityBase * ResourceEconomyModel.BudgetStrength(cost)));
    }

    /// <summary>
    /// Measures the positive part of a card against the ordinary reward budget for its rarity and resource cost.
    /// This deliberately ignores Exhaust itself: compensated printed numbers remain in <paramref name="operations"/>,
    /// so a well-compensated Exhaust card scores high and should rarely be upgraded into a reusable card, while an
    /// under-compensated card scores low. The same trigger-frequency conversion used by the top-rarity floor keeps
    /// persistent and conditional payoffs comparable to immediate effects.
    /// </summary>
    internal static double RelativeCardRewardValue(IReadOnlyList<GeneratorOperation> operations,
        GeneratedRarity rarity, double effectiveCost)
    {
        var rewards = PositiveRewardOperations(operations);
        if (rewards.Length == 0) return 0d;

        var actual = EstimatedPositiveCardValue(operations);
        var required = ComponentAssemblyGenerator.CalibratedWholeCardCenter(rarity, effectiveCost);
        return actual / Math.Max(1d, required);
    }

    /// <summary>
    /// Estimates the positive value of a complete operation list in the same currency used by card floors. Unlike
    /// summing individual atoms, this prices extra hits against their host damage and linked payoffs against their
    /// trigger frequency. The upgrade generator uses the difference between base and upgraded lists, preventing
    /// an extra-hit +1 from being treated like an ordinary printed-number +1.
    /// </summary>
    internal static double EstimatedPositiveCardValue(IReadOnlyList<GeneratorOperation> operations) =>
        PositiveRewardOperations(operations)
            .Sum(item => EstimatedCardLevelRewardValue(item.operation, item.index, operations));

    internal static double EstimatedPositiveCardValue(IReadOnlyList<GeneratorOperation> operations,
        bool hasPrintedResourceCost, GeneratedCardType cardType, IReadOnlyCollection<CardTag>? tags) =>
        PositiveRewardOperations(operations, hasPrintedResourceCost, cardType, tags)
            .Sum(item => ContextualCardLevelRewardValue(item.operation, item.index, operations,
                hasPrintedResourceCost, cardType, tags))
        + EstimatedPositiveKeywordValue(tags);

    /// <summary>
    /// Exposes the exact per-operation contribution used by the whole-card generator valuation. This is audit-only
    /// plumbing: it deliberately reuses the production filters and contextual calculation rather than maintaining
    /// a second approximation of triggers, modifiers, card-copy roles or linked numeric fields.
    /// </summary>
    internal static double EstimatedContextualOperationValueForAudit(GeneratorOperation operation,
        int operationIndex, IReadOnlyList<GeneratorOperation> operations, bool hasPrintedResourceCost,
        GeneratedCardType cardType, IReadOnlyCollection<CardTag>? tags)
    {
        if (operation.Template is "N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK"
            || !CardEffectRules.IsBeneficialEffect(operation)
            || operation.Template != "C:untilTurnEnd"
            && IsCondition(new ComponentAtom(operation.Template, operation.Scope, string.Empty,
                operation.RequiresSingleTarget, CardReferenceRequirement.None)))
            return 0d;
        if (CardEffectRules.IsZeroCostCopyThisCardToDiscard(operation)) return 0d;
        if (CardEffectRules.CopyThisCardBudgetRole(operation, hasPrintedResourceCost, cardType, tags)
            is CopyThisCardBudgetRole.PaidReusableDownside or CopyThisCardBudgetRole.ExhaustOffset)
            return 0d;
        return ContextualCardLevelRewardValue(operation, operationIndex, operations,
            hasPrintedResourceCost, cardType, tags);
    }

    internal static double LinkedResolutionMultiplierForAudit(GeneratorOperation operation,
        int operationIndex, IReadOnlyList<GeneratorOperation> operations) =>
        LinkedResolutionMultiplier(operation, operationIndex, operations);

    /// <summary>
    /// Resolves ordinary Energy-X/Star-X value slots at a concrete payment for card-floor checks. Ordinary X cards
    /// do not have a fixed effective-cost coordinate, but their payoff at X=1 must still be good enough to consume
    /// one resource and one draw. Special-X conversion happens later and remains governed by its fixed shell.
    /// </summary>
    internal static double EstimatedPositiveCardValueAtOrdinaryX(IReadOnlyList<GeneratorOperation> operations,
        int resolvedX, bool hasPrintedResourceCost, GeneratedCardType cardType,
        IReadOnlyCollection<CardTag>? tags)
    {
        resolvedX = Math.Max(0, resolvedX);
        var materialized = operations.Select(operation =>
        {
            var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
            if (!spec.Values.Any(value => value.Source is "energy_x" or "star_x")) return operation;
            var values = spec.Values.Select(value => value.Source is "energy_x" or "star_x"
                ? value with
                {
                    BaseValue = Math.Max(0, resolvedX + value.Offset),
                    Source = "fixed",
                    Offset = 0
                }
                : value).ToArray();
            return operation with { RuntimeSpec = spec with { Values = values } };
        }).ToArray();
        return EstimatedPositiveCardValue(materialized, hasPrintedResourceCost, cardType, tags);
    }

    /// <summary>
    /// Fixed card-face keyword value. Retain is calibrated from v111's eleven Retain-only upgrades plus Misery's
    /// Damage+2/Retain split and costs four Damage points. Innate is the smaller two-Damage convenience requested
    /// for both authored cards and upgrades. Do not inspect live enchantments here; only keywords authored into the
    /// generated card template consume its initial budget.
    /// </summary>
    internal static double EstimatedPositiveKeywordValue(IReadOnlyCollection<CardTag>? tags) =>
        (tags?.Contains(CardTag.Retain) == true ? 400d : 0d)
        + (tags?.Contains(CardTag.Innate) == true ? 200d : 0d);

    /// <summary>
    /// Counts independently useful reward fields rather than raw operation lines. Two numeric variants of the
    /// same Damage operation still deal damage twice, but they share one whole-card budget field; otherwise a rare
    /// duplicate roll silently receives a second full-card allowance.
    /// </summary>
    internal static int PositiveRewardFieldCount(IReadOnlyList<GeneratorOperation> operations)
    {
        var rewards = PositiveRewardOperations(operations).Select(item => item.operation).ToArray();
        return AuditedRewardFieldCount(rewards);
    }

    internal static int PositiveRewardFieldCount(IReadOnlyList<GeneratorOperation> operations,
        bool hasPrintedResourceCost, GeneratedCardType cardType, IReadOnlyCollection<CardTag>? tags)
    {
        var rewards = PositiveRewardOperations(operations, hasPrintedResourceCost, cardType, tags)
            .Select(item => item.operation).ToArray();
        return AuditedRewardFieldCount(rewards);
    }

    private static int AuditedRewardFieldCount(IReadOnlyList<GeneratorOperation> rewards)
    {
        var structured = rewards.Select(WholeCardRewardFieldKey).Distinct(StringComparer.Ordinal).Count();
        if (!OperationRuntimeSpecCompiler.EnableLegacyEquivalenceAssertions) return structured;
        var legacy = rewards.Select(operation =>
                CardEffectRules.IsDamageBudgetEffect(operation) || CardEffectRules.IsOrbEvokeReward(operation)
                    ? "Damage"
                    : $"{NumericTextSchema.Family(operation.Template)}|{NumericTextSchema.Fields(operation.ChineseText)}")
            .Distinct(StringComparer.Ordinal).Count();
        if (legacy != structured)
            throw new InvalidOperationException($"Whole-card reward field schema drift: legacy={legacy}, "
                + $"structured={structured}, rewards=" + string.Join(" || ", rewards.Select(operation =>
                    $"{operation.Template}:{operation.ChineseText}:d={operation.DerivativeId}:"
                    + $"e={operation.DerivativeEnchantmentId}:os={operation.OrbSourceId}:oo={operation.OrbOutputId}")));
        return structured;
    }

    /// <summary>
    /// Classifies independently useful reward families for diagnostics. Direct,
    /// random, retaliatory and triggered Damage all belong to the same Damage field; field count never expands the
    /// ordinary whole-card allowance.
    /// </summary>
    private static string WholeCardRewardFieldKey(GeneratorOperation operation)
    {
        // Orb Evoke is already a direct combat output (Damage, Block or Energy) rather than a setup effect like
        // Channel. Pairing it with printed Damage must split one primary-output allowance, as native Dualcast and
        // Ball Lightning-style cards do, instead of receiving an automatic sqrt(2) whole-card expansion.
        if (CardEffectRules.IsDamageBudgetEffect(operation) || CardEffectRules.IsOrbEvokeReward(operation))
            return "Damage";
        return CardEffectRules.FieldKey(operation);
    }

    /// <summary>
    /// Total damage-package value, including static extra-hit modifiers whose real payoff is inherited from the
    /// card's direct damage. Whole-card synergy pricing must use this rather than the printed host line alone.
    /// </summary>
    internal static double EstimatedDamagePackageValue(IReadOnlyList<GeneratorOperation> operations)
    {
        var damageHosts = operations.Where(CardEffectRules.IsEnemyDamage).ToArray();
        var hostDamage = damageHosts.Sum(operation => (double)EstimatedEffectValue(operation));
        if (hostDamage <= 0d) return 0d;
        var extraHits = operations.Select((operation, index) => (operation, index))
            .Where(item => IsStaticExtraDamageHits(item.operation))
            .Sum(item => ExpectedContextualExtraDamageHits(item.operation, item.index, operations));
        var dynamicTotalHits = operations.Select((operation, index) => (operation, index))
            .Sum(item => ExpectedDynamicHitTotal(item.operation, item.index, operations));
        var dynamicExtraHits = dynamicTotalHits > 0d ? Math.Max(0d, dynamicTotalHits - 1d) : 0d;
        return hostDamage + (extraHits + dynamicExtraHits) * damageHosts.Sum(AddedHitValue);
    }

    internal static double MixedDamageBlockScale(double damageValue, double blockValue)
    {
        if (damageValue <= 0d || blockValue <= 0d) return 1d;
        var surcharge = Math.Min(damageValue, blockValue) * 0.20d;
        return Math.Clamp((damageValue + blockValue - surcharge) / (damageValue + blockValue), 0.85d, 1d);
    }

    private static (GeneratorOperation operation, int index)[] PositiveRewardOperations(
        IReadOnlyList<GeneratorOperation> operations) =>
        operations.Select((operation, index) => (operation, index))
            .Where(item => item.operation.Template is not
                ("N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK")
                && CardEffectRules.IsBeneficialEffect(item.operation)
                && (item.operation.Template == "C:untilTurnEnd"
                    || CardEffectRules.IsSelfManagedStateEffect(item.operation)
                    || !IsCondition(new ComponentAtom(item.operation.Template, item.operation.Scope,
                        item.operation.ChineseText, item.operation.RequiresSingleTarget,
                        CardReferenceRequirement.None))))
            .ToArray();

    private static (GeneratorOperation operation, int index)[] PositiveRewardOperations(
        IReadOnlyList<GeneratorOperation> operations, bool hasPrintedResourceCost,
        GeneratedCardType cardType, IReadOnlyCollection<CardTag>? tags) =>
        PositiveRewardOperations(operations)
            // A zero-cost self-copy changes the whole-card cost curve instead of consuming an independent reward
            // field. ComponentAssemblyGenerator averages the paid and zero-cost envelopes for the remaining
            // payload, which models both copies without counting this line twice.
            .Where(item => !CardEffectRules.IsZeroCostCopyThisCardToDiscard(item.operation))
            .Where(item => CardEffectRules.CopyThisCardBudgetRole(item.operation, hasPrintedResourceCost,
                    cardType, tags)
                is not (CopyThisCardBudgetRole.PaidReusableDownside or CopyThisCardBudgetRole.ExhaustOffset))
            .ToArray();

    private static double ContextualCardLevelRewardValue(GeneratorOperation operation, int operationIndex,
        IReadOnlyList<GeneratorOperation> operations, bool hasPrintedResourceCost,
        GeneratedCardType cardType, IReadOnlyCollection<CardTag>? tags)
    {
        return CardEffectRules.CopyThisCardBudgetRole(operation, hasPrintedResourceCost, cardType, tags) switch
        {
            // Anger is the clean reference: after its ordinary 6 Damage, creating another reusable 0-cost Anger
            // is worth approximately another 6 Damage points of card economy.
            CopyThisCardBudgetRole.FreeReusableBenefit => 600d,
            // A Power copy is a durable repeatable engine. Price it as a large upper-rarity effect; selection
            // weighting separately keeps it rare instead of relying on an impossible hard rarity lock.
            CopyThisCardBudgetRole.PowerBenefit => 2_000d,
            CopyThisCardBudgetRole.PaidReusableDownside or CopyThisCardBudgetRole.ExhaustOffset => 0d,
            _ => EstimatedCardLevelRewardValue(operation, operationIndex, operations)
        };
    }

    /// <summary>
    /// Applies a whole-card reward floor to Rare and Ancient cards. Every positive operation contributes to the
    /// same approximate currency, including utility/rule effects and payoffs behind conditions; trigger frequency
    /// converts a linked payoff back from its printed per-trigger amount to an estimated card-level contribution.
    /// Exact native assemblies bypass this check in ComponentAssemblyGenerator, preserving the non-zero
    /// reconstruction path for every original card.
    /// </summary>
    internal static bool HasAdequateTopRarityCardValue(IReadOnlyList<GeneratorOperation> operations,
        GeneratedRarity rarity, double effectiveCost, IReadOnlyCollection<CardTag>? tags = null)
    {
        return !TryMeasureTopRarityCardValue(operations, rarity, effectiveCost, out var actual, out var required,
                tags)
            || actual >= required;
    }

    internal static bool TryMeasureTopRarityCardValue(IReadOnlyList<GeneratorOperation> operations,
        GeneratedRarity rarity, double effectiveCost, out double actual, out double required,
        IReadOnlyCollection<CardTag>? tags = null)
    {
        actual = 0d;
        required = 0d;
        // X-cost cards have no fixed price against which a static floor can be calibrated. Zero-cost cards do:
        // unlike the previous implementation, they must clear their own lower but nonzero top-rarity threshold.
        if (rarity is not (GeneratedRarity.Rare or GeneratedRarity.Ancient) || double.IsNaN(effectiveCost))
            return false;
        var semantic = operations.Select((operation, index) => (operation, index))
            .Where(item => item.operation.Template is not
                ("N_SELECT_HAND_CARD" or "N_SELECT_HAND_ATTACK")).ToArray();
        if (semantic.Length == 0)
        {
            required = TopRarityBaseFloor(rarity, effectiveCost);
            return true;
        }

        var rewards = semantic
            .Where(item => CardEffectRules.IsBeneficialEffect(item.operation)
                && !IsCondition(new ComponentAtom(item.operation.Template, item.operation.Scope,
                    item.operation.ChineseText, item.operation.RequiresSingleTarget,
                    CardReferenceRequirement.None)))
            .ToArray();

        actual = rewards.Sum(item => EstimatedCardLevelRewardValue(item.operation, item.index, operations))
            + EstimatedPositiveKeywordValue(tags);
        // Draw 2 is worth 900 in this currency. Ancient uses a distinctly higher floor than Rare; its narrower
        // numeric variance therefore removes outliers without allowing low-roll capstones through.
        required = TopRarityBaseFloor(rarity, effectiveCost);
        return true;
    }

    private static double TopRarityBaseFloor(GeneratedRarity rarity, double effectiveCost)
    {
        // A zero-cost top-rarity card still consumes a draw and a rare reward slot. The generic cost curve's old
        // 702-point Rare floor admitted ordinary-card results such as 8 damage or 9 Block, so calibrate free cards
        // separately: Draw 2 / 9 damage is the Rare boundary, with Ancient retaining a distinct capstone gap.
        if (effectiveCost <= 0d)
            return rarity == GeneratedRarity.Ancient ? 1_440d : 900d;
        return ExpectedLineValue(rarity, effectiveCost)
            * (rarity == GeneratedRarity.Ancient ? 1.40d : 0.90d);
    }

    private static double EstimatedCardLevelRewardValue(GeneratorOperation operation, int operationIndex,
        IReadOnlyList<GeneratorOperation> operations)
    {
        var atom = new ComponentAtom(operation.Template, operation.Scope, operation.ChineseText,
            operation.RequiresSingleTarget, CardReferenceRequirement.None)
            { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(operation) };
        var value = (double)EstimatedEffectValue(operation);
        if (operation.Template == "NCR:GainEnergy"
            && operations.Any(candidate => candidate.Template == "NCR:IncreaseAllCardCostsThisTurn"))
            // Borrowed Time's four Energy is not four unrestricted Energy: making every follow-up card cost one
            // more consumes most of the nominal gain. Price the pair by its native net utility, while Wisp and
            // recombinations without the tax retain the full per-Energy value.
            value *= 0.36d;
        var hostDamage = operations.Where(CardEffectRules.IsEnemyDamage)
            .Sum(damage => (double)EstimatedEffectValue(new ComponentAtom(damage.Template, damage.Scope,
                damage.ChineseText, damage.RequiresSingleTarget, CardReferenceRequirement.None)
                { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(damage) }));
        // These effects inherit the amount dealt by a preceding damage line. Pricing them as fixed text made a
        // damage upgrade look much smaller than its real result because the linked Doom/Block/splash also rises.
        if (operation.Template == "NCR:ApplyDoomEqualDamage") return hostDamage * 1.35d;
        if (operation.Template == "CL:GainBlockEqualDamage") return hostDamage * 1.20d;
        if (operation.Template == "CL:DamageOtherEnemiesEqual") return hostDamage;
        if (operation.Template == "CL:IncreaseRollingDamage")
            return RollingDamageGrowthValue(operation, operationIndex, operations);
        if (CardEffectRules.IsCurrentBlockDamageModifier(operation))
            // Body Slam replaces its zero-valued hidden Damage anchor. The expected state is fixed, but its
            // targeting, trigger cadence and every compatible hit-count modifier belong to the actual host.
            return ExpectedCurrentBlock * 100d * DependentHostValueMultiplier(
                DependentNumericFamily.Damage, operation, operationIndex, operations,
                CardEffectRules.CurrentBlockDamageAnchorIndex(operations, operationIndex));
        if (operation.Template is "NCR:OstyCurrentHpBonusDamage" or "NCR:OstyMaxHpBonusDamage")
        {
            // Osty's HP is the additive coefficient; the modifier is copied onto every hit of the host Damage.
            // Keep a conservative ordinary combat-state anchor while inheriting the real host geometry.
            var expectedHp = operation.Template == "NCR:OstyCurrentHpBonusDamage"
                ? ExpectedOstyCurrentHp : ExpectedOstyMaxHp;
            return expectedHp * 100d * DependentHostValueMultiplier(
                DependentNumericFamily.Damage, operation, operationIndex, operations);
        }
        if (operation.Template == "NCR:DoomPerDoomThreshold")
        {
            var bonus = Math.Max(0, OperationRuntimeSpecCompiler.StaticLiteralValue(operation, "bonus",
                OperationRuntimeSpecCompiler.PrimaryStaticLiteralValue(operation, 0)));
            // No Escape applies the printed Doom once for every live threshold; this is an Apply-Doom payoff,
            // not a generic Modifier scalar.
            return bonus * 80d * DamageValueMultiplier(operation)
                * LinkedResolutionMultiplier(operation, operationIndex, operations);
        }
        if (operation.Template == "R:DoubleEnergyX")
            return DoubleEnergyXModifierValue(operation, operationIndex, operations);
        if (operation.Template == "R:DoubleEitherXAtThreshold")
            return GlobalXDoubleModifierValue(operation, operations);
        if (operation.Template == "M:RepeatAreaOnKill")
            // Echoing Slash repeats its actual all-enemy Damage packet, including targeting and on-hit adaptation.
            // Roughly 0.45 kills from the first wave preserves the native card's former ~700 rider without giving
            // an unrelated fixed value to a modifier attached to a 2-damage or 30-damage host.
            return operations.Where(CardEffectRules.IsEnemyDamage).Sum(AddedHitValue) * 0.45d;
        if (TryDependentNumericIncrease(operation, out _, out _))
        {
            // A point added to "this card's Damage/Block" is not a self-contained point. It is copied onto every
            // compatible numeric host owned by the card. Price the original operation against one ordinary host,
            // then multiply by the summed marginal value of the actual hosts. This covers Rampage, Claw, The
            // Scythe, draw-growth and Perfect-Strike-style additive modifiers without template-specific patches.
            var inheritedMultiplier = DependentNumericValueMultiplier(operation, operationIndex, operations);
            if (IsCountedAdditiveDamageModifier(operation))
            {
                var amount = CountedModifierAmount(operation);
                var expectedCount = ExpectedCountedModifierCount(operation, operationIndex, operations);
                return amount * 100d * expectedCount * inheritedMultiplier;
            }
            if (IsStrengthScaledBlockModifier(operation))
            {
                var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
                var interval = Math.Max(1, OperationRuntimeSpecCompiler.StaticLiteralValue(
                    operation, "strength_interval", 1));
                var blockPerInterval = Math.Max(0, OperationRuntimeSpecCompiler.StaticLiteralValue(
                    operation, "block_per_interval", 0));
                // Expect a Fight is mostly ordinary 15 Block in an uncommitted deck. An average 1.1 Strength
                // keeps the native three-Energy card inside the shared envelope while still making recombinations inherit every actual
                // Block host and trigger cadence instead of receiving a free fixed rider.
                return blockPerInterval / (double)interval * 1.10d * 120d * inheritedMultiplier;
            }
            var ownRepeat = LinkedResolutionMultiplier(operation, operationIndex, operations);
            return value * inheritedMultiplier * ownRepeat;
        }
        if (TryDynamicHitTotalModifierValue(operation, operationIndex, operations, out var dynamicHitValue))
            return dynamicHitValue;
        if (IsStaticExtraDamageHits(atom))
        {
            var extraHits = ExpectedContextualExtraDamageHits(operation, operationIndex, operations);
            return extraHits * operations.Where(CardEffectRules.IsEnemyDamage).Sum(AddedHitValue);
        }
        var linkedResolutions = LinkedResolutionMultiplier(operation, operationIndex, operations);
        var resolutionValue = value * linkedResolutions;
        if (CardEffectRules.IsEnemyDamage(operation)
            && HasAdjacentMultiplicativeDependency(operation, operationIndex, operations)
            && linkedResolutions > 1d)
        {
            // A count prefix repeats a Damage action rather than multiplying one packet. Only Damage receives the
            // extra multi-hit adaptation value; Block, Poison, Doom and other stackable amounts remain plain totals.
            resolutionValue += (linkedResolutions - 1d) * SingleResolutionDamagePointValue(operation);
        }
        if (operation.Parameters.TryGetValue("triggerIndex", out var ownerIndex)
            && ownerIndex >= 0 && ownerIndex < operationIndex
            && operations[ownerIndex].Template is "A:turnStart" or "D:turnStart")
        {
            // Demon Form's delayed, successively accumulated permanent stat is worth about 23% of receiving the
            // same stat immediately on every nominal resolution. This preserves the high one-shot stat price while
            // preventing a recurring stat engine from being charged three times at full immediate value.
            if (CardEffectRules.IsPositivePermanentStatGain(atom)) resolutionValue *= 0.32d;
            // Aggression repeatedly retrieves and upgrades one random discarded Attack. Later resolutions have
            // fewer legal/high-value targets, so the package has strongly diminishing returns.
            if (operation.Template is "N:Move" or "I:UpgradeThatCard") resolutionValue *= 0.32d;
        }
        else if (operation.Parameters.TryGetValue("triggerIndex", out ownerIndex)
                 && ownerIndex >= 0 && ownerIndex < operationIndex
                 && operations[ownerIndex].Template == "A:whenCardGenerated"
                 && CardEffectRules.IsPositivePermanentStatGain(atom))
        {
            // Arsenal's later Strength stacks enter progressively later and improve fewer remaining attacks.
            // Charging every expected generated card as a fresh immediate permanent stat triples the native card.
            resolutionValue *= 0.33d;
        }
        if (OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("draw_reference")
            && operation.Parameters.TryGetValue("triggerIndex", out var drawTriggerIndex)
            && drawTriggerIndex >= 0 && drawTriggerIndex < operationIndex
            && HasRepeatedOrMultiplicativePayoff(operations[drawTriggerIndex]))
        {
            // A recurring draw is not merely several independent copies of an immediate draw line.  Reserve
            // additional budget for sustained hand advantage and trigger-engine feedback.  This remains a soft
            // value multiplier: aggressive mode and rarity variance can still produce exceptional combinations.
            resolutionValue *= IsPersistentAbilityTrigger(operations[drawTriggerIndex])
                ? PersistentTriggeredDrawValueMultiplier
                : TemporaryTriggeredDrawValueMultiplier;
        }
        if (CardEffectRules.IsDirectOrbChannel(operation))
        {
            var channelIndices = operations.Select((candidate, index) => (candidate, index))
                .Where(item => CardEffectRules.IsDirectOrbChannel(item.candidate)).ToArray();
            if (channelIndices.Length > 0 && channelIndices[0].index == operationIndex)
            {
                // Channeling several different Orb types on one card is materially more flexible than repeating
                // one type: Rainbow supplies damage, Block and scaling setup at once. Price that generic diversity
                // on the first channel operation so operation traces still sum exactly to the whole-card value.
                var distinctTypes = channelIndices.Select(item =>
                        item.candidate.OrbOutputId ?? item.candidate.Template)
                    .Distinct(StringComparer.Ordinal).Count();
                resolutionValue += Math.Max(0, distinctTypes - 1) * 675d;
            }
        }
        return resolutionValue;
    }

    private static double RollingDamageGrowthValue(GeneratorOperation operation, int operationIndex,
        IReadOnlyList<GeneratorOperation> operations)
    {
        var owner = operation.Parameters.GetValueOrDefault("triggerIndex", -1);
        var hostEntry = operations.Select((candidate, index) => (candidate, index)).FirstOrDefault(item =>
            item.index != operationIndex && CardEffectRules.IsPrintedDamageReward(item.candidate)
            && item.candidate.Parameters.GetValueOrDefault("triggerIndex", -1) == owner);
        if (hostEntry.candidate is null) return EstimatedEffectValue(operation);
        var resolutions = LinkedResolutionMultiplier(hostEntry.candidate, hostEntry.index, operations);
        // Rolling growth compounds with both how often it is gained and how often the enlarged hit resolves later.
        // Infer the quadratic coefficient from native Rolling Boulder rather than assigning N^2 a full ordinary-hit
        // price: Rare 3-cost center 6460 - base (5 all-enemy damage * 3 turns = 2325) leaves 4135 for growth;
        // 4135 / (5 * 155 * 3^2) = 0.5928315412. Higher-frequency hosts still rise quadratically.
        var futureApplications = Math.Max(0d,
            resolutions * resolutions * RollingGrowthQuadraticCoefficient);
        var growth = Math.Max(1, OperationRuntimeSpecCompiler.PrimaryStaticLiteralValue(operation, 1));
        return growth * futureApplications * DamageValuePerPrintedPoint(hostEntry.candidate);
    }

    /// <summary>
    /// Finisher, Flechettes and Barrage replace the host Damage line's total hit count with a live count. Their
    /// modifiers cannot have fixed standalone prices: every expected extra hit inherits the card's actual generated
    /// Damage, targeting multiplier and multi-hit adaptation. Native play anchors them at two prior Attacks, three
    /// Skills in hand and three live Orbs respectively. When Ultimate Chaos puts several such modifiers on one card,
    /// runtime adds the live counts, so charge the shared first hit only once.
    /// </summary>
    private static bool TryDynamicHitTotalModifierValue(GeneratorOperation operation, int operationIndex,
        IReadOnlyList<GeneratorOperation> operations, out double value)
    {
        value = 0d;
        if (ExpectedDynamicHitTotal(operation, operationIndex, operations) <= 0d) return false;
        var modifierIndices = operations.Select((candidate, index) => (candidate, index))
            .Where(item => ExpectedDynamicHitTotal(item.candidate, item.index, operations) > 0d)
            .Select(item => item.index).ToArray();
        if (modifierIndices.Length == 0 || operationIndex != modifierIndices[0]) return true;
        var expectedTotalHits = modifierIndices.Sum(index =>
            ExpectedDynamicHitTotal(operations[index], index, operations));
        var inheritedHitValue = operations.Where(CardEffectRules.IsEnemyDamage).Sum(AddedHitValue);
        value = Math.Max(0d, expectedTotalHits - 1d) * inheritedHitValue;
        return true;
    }

    private static double ExpectedDynamicHitTotal(GeneratorOperation operation, int operationIndex,
        IReadOnlyList<GeneratorOperation> operations)
    {
        if (!CardEffectRules.IsDynamicTotalHitModifier(operation)) return 0d;
        return operation.Template switch
        {
            "M:RepeatPerAttackThisTurn" => 2d,
            "M:RepeatPerSkillInHand" => 3d,
            "D:RepeatPerOrb" => LinkedCountOrDefault(operation, operationIndex, operations, 3d),
            "NCR:RepeatPerVoidPlayedCombat" => LinkedCountOrDefault(operation, operationIndex, operations, 2d),
            "R:RepeatPerSkillPlayedThisTurn" => LinkedCountOrDefault(operation, operationIndex, operations, 2d),
            "R:RepeatPerStarGainedThisTurn" => LinkedCountOrDefault(operation, operationIndex, operations, 1.5d),
            _ => 0d
        };
    }

    private static double LinkedCountOrDefault(GeneratorOperation operation, int operationIndex,
        IReadOnlyList<GeneratorOperation> operations, double fallback)
    {
        var linked = LinkedResolutionMultiplier(operation, operationIndex, operations);
        return Math.Abs(linked - 1d) < 0.0001d ? fallback : linked;
    }

    private static double AddedHitValue(GeneratorOperation operation)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        var damagePerHit = spec.Values.FirstOrDefault(value => value.Explicit && value.Source == "fixed")
            ?.BaseValue ?? 1;
        return (damagePerHit + 1d) * DamageValueMultiplier(operation) * 100d;
    }

    private static double DamageValuePerPrintedPoint(GeneratorOperation operation)
    {
        var amount = Math.Max(1, OperationRuntimeSpecCompiler.PrimaryStaticLiteralValue(operation, 1));
        return EstimatedEffectValue(operation) / (double)amount;
    }

    private enum DependentNumericFamily { Damage, Block, RollingDamage }

    private static double DoubleEnergyXModifierValue(GeneratorOperation operation, int operationIndex,
        IReadOnlyList<GeneratorOperation> operations)
    {
        var threshold = 4;
        if (operationIndex > 0 && operations[operationIndex - 1].Template == "R:IfEnergyXAtLeast")
            threshold = Math.Max(1, OperationRuntimeSpecCompiler.StaticLiteralValue(
                operations[operationIndex - 1], "threshold", threshold));
        var hosts = operations.Where(candidate => OperationRuntimeSpecCompiler.GetOrCompile(candidate).Variant
            == "selected_energy_x_threshold").ToArray();
        if (hosts.Length == 0) return 0d;
        // At the threshold, doubling X adds exactly threshold more hits. Paying four Energy is deliberately rare;
        // infer a 15% ordinary success rate instead of giving the rider an unrelated fixed rule value.
        return hosts.Sum(AddedHitValue) * threshold * EnergyXThresholdSuccessChance;
    }

    /// <summary>
    /// The generalized Heavenly Drill rider doubles the underlying X before offsets for every X-valued operation
    /// on the card. Price it by evaluating that actual host set at the native threshold and at twice the threshold,
    /// then multiply only the marginal gain by the native 15% high-payment frequency. This reproduces the old
    /// Heavenly Drill residual (about 540 value for 8 Damage X times) while correctly charging multi-X and mixed
    /// Draw/Block/Channel cards instead of assigning the rider one unrelated fixed value.
    /// </summary>
    private static double GlobalXDoubleModifierValue(GeneratorOperation modifier,
        IReadOnlyList<GeneratorOperation> operations)
    {
        var threshold = Math.Max(1, OperationRuntimeSpecCompiler.StaticLiteralValue(
            modifier, "threshold", 4));
        var xHostIndices = operations.Select((operation, index) => (operation, index))
            .Where(item => item.operation.Template != "R:DoubleEitherXAtThreshold"
                && OperationRuntimeSpecCompiler.GetOrCompile(item.operation).Values.Any(value =>
                    value.Source is "energy_x" or "star_x" or "special_x"))
            .Select(item => item.index).ToArray();
        if (xHostIndices.Length == 0) return 0d;

        double ValueAt(int resolvedX)
        {
            var materialized = operations.Select(operation =>
            {
                var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
                var values = spec.Values.Select(value => value.Source is "energy_x" or "star_x" or "special_x"
                    ? value with
                    {
                        BaseValue = Math.Max(0, resolvedX + value.Offset),
                        Source = "fixed",
                        Offset = 0
                    }
                    : value).ToArray();
                return operation with { RuntimeSpec = spec with { Values = values } };
            }).ToArray();
            return xHostIndices.Sum(index => EstimatedCardLevelRewardValue(
                materialized[index], index, materialized));
        }

        return Math.Max(0d, ValueAt(threshold * 2) - ValueAt(threshold))
            * EnergyXThresholdSuccessChance;
    }

    /// <summary>
    /// Identifies positive numeric fields whose printed amount is added to another field instead of resolving by
    /// itself. The returned slot is the amount to normalize after all hosts have been assembled.
    /// </summary>
    internal static bool TryDependentNumericIncrease(GeneratorOperation operation, out string valueSlot,
        out string family)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        var numericSlots = OperationRuntimeSpecCompiler.ExplicitFixedValueSlots(operation);
        valueSlot = spec is { Opcode: "modify_block", Variant: "strength_scaled" }
            ? numericSlots.FirstOrDefault(slot => slot.Id == "block_per_interval")?.Id ?? string.Empty
            : numericSlots.FirstOrDefault()?.Id ?? string.Empty;
        var dependentFamily = operation.Template switch
        {
            "D:IncreaseThisCardBlockRun" => DependentNumericFamily.Block,
            "CL:IncreaseRollingDamage" => DependentNumericFamily.RollingDamage,
            "I:IncreaseDamageThisCombat" or "D:IncreaseAllClaws"
                or "NCR:IncreaseThisCardDamageRun" or "R:DamageUpWhenDrawn" => DependentNumericFamily.Damage,
            _ when spec is { Opcode: "modify_block", Variant: "strength_scaled" } =>
                DependentNumericFamily.Block,
            _ when IsAdditiveDamageModifier(operation, spec) => DependentNumericFamily.Damage,
            _ => (DependentNumericFamily?)null
        };
        family = dependentFamily?.ToString() ?? string.Empty;
        return dependentFamily is not null && valueSlot.Length > 0;
    }

    private static bool IsAdditiveDamageModifier(GeneratorOperation operation, OperationRuntimeSpec spec)
    {
        if (operation.Scope != OperationScope.Modifier) return false;
        if (spec.Opcode == "modify_damage")
            return spec.Variant is "vulnerable_scaled" or "strike_count_scaled" or "exhaust_pile_scaled";
        return operation.Template is "M:DamagePerExhaustCard" or "M:DamagePerDiscardThisTurn"
            or "M:DamagePerCardDrawnCombat" or "NCR:DamagePerCardDrawnThisTurn"
            or "NCR:DamagePerExhaustedSoul" or "NCR:DamagePerOstyAttackCard"
            or "R:BonusPerStarCostCardInHand" or "R:BonusPerGeneratedCardThisCombat"
            or "CL:BonusPerUniqueDebuff";
    }

    private static bool IsStrengthScaledBlockModifier(GeneratorOperation operation) =>
        OperationRuntimeSpecCompiler.GetOrCompile(operation) is
            { Opcode: "modify_block", Variant: "strength_scaled" };

    private static bool IsCountedAdditiveDamageModifier(GeneratorOperation operation)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        if (spec.Opcode == "modify_damage")
            return spec.Variant is "vulnerable_scaled" or "strike_count_scaled" or "exhaust_pile_scaled";
        return operation.Template is "M:DamagePerExhaustCard" or "M:DamagePerDiscardThisTurn"
            or "M:DamagePerCardDrawnCombat" or "NCR:DamagePerCardDrawnThisTurn"
            or "NCR:DamagePerExhaustedSoul" or "NCR:DamagePerOstyAttackCard"
            or "R:BonusPerStarCostCardInHand" or "R:BonusPerGeneratedCardThisCombat"
            or "CL:BonusPerUniqueDebuff";
    }

    private static int CountedModifierAmount(GeneratorOperation operation)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        var slot = spec.Variant switch
        {
            "vulnerable_scaled" => "damage_per_vulnerable",
            "strike_count_scaled" => "damage_per_strike",
            _ => "amount"
        };
        return Math.Max(0, OperationRuntimeSpecCompiler.StaticLiteralValue(operation, slot,
            OperationRuntimeSpecCompiler.PrimaryStaticLiteralValue(operation, 0)));
    }

    private static double ExpectedCountedModifierCount(GeneratorOperation operation, int operationIndex,
        IReadOnlyList<GeneratorOperation> operations)
    {
        // Prefix-owned modifiers already expose their count through LinkedResolutionMultiplier. Standalone
        // compact wording stores both the count condition and its coefficient in one operation and needs an
        // explicit ordinary-state estimate.
        var linked = LinkedResolutionMultiplier(operation, operationIndex, operations);
        if (operation.Parameters.ContainsKey("triggerIndex")
            || operationIndex > 0 && CardEffectRules.IsDependencyPrefix(operations[operationIndex - 1]))
            return linked;
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        return operation.Template switch
        {
            // Murder is a three-Energy Rare intended as a late-combat finisher. Its native 1 base Damage plus
            // one per card drawn lands on the 2,500-value native cohort at roughly 24 cards drawn; preserve that
            // inferred live count while still multiplying by the actual generated coefficient and host geometry.
            "M:DamagePerCardDrawnCombat" => 33d,
            "NCR:DamagePerOstyAttackCard" => 6.5d,
            "NCR:DamagePerExhaustedSoul" => 2.25d,
            "M:DamagePerDiscardThisTurn" => 1.1d,
            "M:DamagePerExhaustCard" => 3d,
            "CL:BonusPerUniqueDebuff" => 2d,
            "M:base" when spec.Variant == "strike_count_scaled" => 5d,
            "M:base" when spec.Variant == "vulnerable_scaled" => 3d,
            "M:base" when spec.Variant == "exhaust_pile_scaled" => 3d,
            _ => linked
        };
    }

    /// <summary>
    /// Sum of the marginal value of every numeric host affected by <paramref name="operation"/>, normalized to
    /// one ordinary single-target, single-hit Damage line (or one ordinary Block line for Block growth).
    /// Intrinsic hits, static extra hits, area/random targeting and linked trigger cadence all remain visible.
    /// </summary>
    internal static double DependentNumericValueMultiplier(GeneratorOperation operation, int operationIndex,
        IReadOnlyList<GeneratorOperation> operations)
    {
        if (!TryDependentNumericIncrease(operation, out _, out var familyName)) return 1d;
        return DependentHostValueMultiplier(Enum.Parse<DependentNumericFamily>(familyName), operation,
            operationIndex, operations);
    }

    internal static double DamageModifierHostValueMultiplier(GeneratorOperation operation, int operationIndex,
        IReadOnlyList<GeneratorOperation> operations) => DependentHostValueMultiplier(
            DependentNumericFamily.Damage, operation, operationIndex, operations);

    private static double DependentHostValueMultiplier(DependentNumericFamily family,
        GeneratorOperation operation, int operationIndex, IReadOnlyList<GeneratorOperation> operations,
        int onlyHostIndex = -1)
    {
        if (operation.Parameters.TryGetValue("triggerIndex", out var ownerIndex)
            && ownerIndex >= 0 && ownerIndex < operationIndex
            && CardEffectRules.IsNextAttackGrantTrigger(operations[ownerIndex]))
            // This modifier belongs to a future Attack selected at runtime. Its host fields are not the Damage
            // lines on the granting card, so retain the neutral one-single-hit estimate.
            return 1d;
        IEnumerable<(GeneratorOperation Operation, int Index)> hosts = operations
            .Select((candidate, index) => (candidate, index)).Where(item => item.index != operationIndex)
            .Select(item => (item.candidate, item.index));
        if (onlyHostIndex >= 0) hosts = hosts.Where(item => item.Index == onlyHostIndex);

        double referenceValue;
        if (family == DependentNumericFamily.Block)
        {
            hosts = hosts.Where(item => CardEffectRules.PrintedBlockValueSlot(item.Operation) is not null);
            referenceValue = 120d;
        }
        else
        {
            hosts = hosts.Where(item => CardEffectRules.IsPrintedDamageReward(item.Operation));
            if (family == DependentNumericFamily.RollingDamage)
            {
                var triggerIndex = operation.Parameters.GetValueOrDefault("triggerIndex", -1);
                // New catalogs reuse the ordinary area-Damage component; the legacy RollingAllDamage ID is
                // retained only so pre-refactor snapshots still deserialize and keep their original value.
                hosts = hosts.Where(item => item.Operation.Template is "N:AllD" or "CL:RollingAllDamage"
                    && item.Operation.Parameters.GetValueOrDefault("triggerIndex", -1) == triggerIndex);
            }
            referenceValue = 100d;
        }

        var staticExtraHits = family == DependentNumericFamily.Damage
            ? operations.Select((candidate, index) => (candidate, index))
                .Where(item => IsStaticExtraDamageHits(item.candidate))
                .Sum(item => ExpectedContextualExtraDamageHits(item.candidate, item.index, operations))
            : 0d;
        var dynamicTotalHits = family == DependentNumericFamily.Damage
            ? operations.Select((candidate, index) => (candidate, index))
                .Sum(item => ExpectedDynamicHitTotal(item.candidate, item.index, operations))
            : 0d;
        var inheritedPerPoint = 0d;
        foreach (var (host, hostIndex) in hosts)
        {
            double perPoint;
            if (family == DependentNumericFamily.Block)
                perPoint = 120d;
            else
            {
                var intrinsicHits = Math.Max(1, OperationRuntimeSpecCompiler.GetOrCompile(host).Values
                    .FirstOrDefault(valueSlot => valueSlot.Id is "hits" or "repeat_count")?.BaseValue ?? 1);
                // Additive Damage is applied before the card's hit loop. Each coefficient point therefore appears
                // on every intrinsic/dynamic/static hit. Adaptation's +1 per extra hit belongs to the hit modifier,
                // not to a coefficient increase, so the marginal value is exactly hit count * target geometry.
                var resolvedHits = (dynamicTotalHits > 0d ? dynamicTotalHits : intrinsicHits) + staticExtraHits;
                perPoint = Math.Max(0d, resolvedHits) * DamageValueMultiplier(host) * 100d;
            }
            inheritedPerPoint += perPoint * LinkedResolutionMultiplier(host, hostIndex, operations);
        }
        return inheritedPerPoint <= 0d ? 1d : Math.Max(0.10d, inheritedPerPoint / referenceValue);
    }

    private static double LinkedResolutionMultiplier(GeneratorOperation operation, int operationIndex,
        IReadOnlyList<GeneratorOperation> operations)
    {
        GeneratorOperation? trigger = null;
        var triggerIndex = -1;
        if (operation.Parameters.TryGetValue("triggerIndex", out var ownerIndex)
            && ownerIndex >= 0 && ownerIndex < operationIndex)
        {
            triggerIndex = ownerIndex;
            trigger = operations[ownerIndex];
        }
        else if (operationIndex > 0
                 && CardEffectRules.IsDependencyPrefix(operations[operationIndex - 1])
                 && CardEffectRules.IsLegalDependencyPayoff(operations[operationIndex - 1], operation))
            // Dependency prefixes are intentionally adjacent rather than trigger-owned in snapshots and catalogs.
            // Generation already scales their printed payoff through LinkedTrigger/PayoffScalePercent; whole-card
            // valuation must restore the same expected live count for count-prefixed effects.
        {
            triggerIndex = operationIndex - 1;
            trigger = operations[triggerIndex];
        }
        if (trigger is null) return 1d;

        if (trigger.Template == "CL:IfNoAttacksInHand"
            && OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("draw_reference"))
            // Impatience is the clean native package: its controllable zero-cost gate makes Draw 2 worth roughly
            // one ordinary zero-cost Uncommon card despite not being available in every hand.
            return 1.04d;

        var frequency = RelativeTriggerFrequency(trigger);
        double resolutions;
        if (CardEffectRules.IsDependencyPrefix(trigger)
            || trigger.Template is "C:forEach" or "C:forEachDiscarded")
            // These prefixes multiply by a live count (draw-pile size, cards played this combat, Star-cost cards,
            // cards exhausted by the preceding action, and so on). The old generic repeated-trigger cap of 3.5
            // silently contradicted the generation scaler and truncated explicit 5/8/10-count native effects.
            resolutions = DependencyResolutionCount(trigger, triggerIndex, operations, frequency);
        else if (IsPersistentAbilityTrigger(trigger))
        {
            // Powers normally receive several opportunities after they are played. Frequent triggers have small
            // printed payoffs, so multiplying by their cadence restores a comparable whole-card estimate.
            var minimumResolutions = frequency < 0.5d ? 0.5d : 1.25d;
            resolutions = Math.Clamp(ExpectedTriggerResolutions(trigger, 3d), minimumResolutions,
                PersistentResolutionCap(trigger));
        }
        else if (HasRepeatedOrMultiplicativePayoff(trigger))
            resolutions = Math.Clamp(ExpectedTriggerResolutions(trigger), 0.5d, 3.5d);
        else
            // One-shot difficult conditions already receive a larger printed payoff from PayoffScalePercent.
            resolutions = Math.Clamp(frequency, 0.05d, 1d);

        // A multiplier can itself be owned by a repeated trigger: Coolant is turn-start -> per unique Orb ->
        // Block. The payoff resolves both once per Orb and once per active turn. The former audit counted only the
        // immediate owner and silently lost the outer cadence. Owners always precede their children, so recursive
        // expansion is acyclic and exposes every multiplier exactly once.
        var parentResolutions = trigger.Parameters.TryGetValue("triggerIndex", out var parentIndex)
            && parentIndex >= 0 && parentIndex < triggerIndex
            ? LinkedResolutionMultiplier(trigger, triggerIndex, operations)
            : 1d;
        var adjacentDependencyResolutions = operationIndex > 0
            && operationIndex - 1 != triggerIndex
            && CardEffectRules.IsDependencyPrefix(operations[operationIndex - 1])
            && CardEffectRules.IsLegalDependencyPayoff(operations[operationIndex - 1], operation)
            ? DependencyResolutionCount(operations[operationIndex - 1], operationIndex - 1, operations,
                RelativeTriggerFrequency(operations[operationIndex - 1]))
            : 1d;
        return resolutions * parentResolutions * adjacentDependencyResolutions
            * NestedConditionMultiplier(triggerIndex, operationIndex, operation, operations);
    }

    private static double PersistentResolutionCap(GeneratorOperation trigger) => trigger.Template switch
    {
        // These native Powers resolve far more than 4.5 times during an ordinary three-turn active lifetime.
        "A:whenCardPlayed" or "A:whenCardDrawnDuringTurn" => 10d,
        "A:whenAttackDealsDamage" => 6d,
        // End-of-turn Powers are normally drawn after combat begins and average fewer than three live ticks.
        "A:turnEnd" => 2.5d,
        // Debuff applications are frequent, but this Power is normally drawn after combat has already begun.
        "A:whenDebuffApplied" => 4d,
        _ => 4.5d
    };

    private static double DependencyResolutionCount(GeneratorOperation trigger, int triggerIndex,
        IReadOnlyList<GeneratorOperation> operations, double fallback)
    {
        if (trigger.Template != "C:forEachDiscarded" || triggerIndex <= 0) return Math.Max(0d, fallback);
        var payment = operations[triggerIndex - 1];
        if (payment.Template == "N:DiscardAll") return 4d;
        if (payment.Template == "N:Discard")
            return Math.Max(1d, OperationRuntimeSpecCompiler.StaticLiteralValue(payment, "count", 1));
        return Math.Max(0d, fallback);
    }

    private static double NestedConditionMultiplier(int triggerIndex, int operationIndex,
        GeneratorOperation operation, IReadOnlyList<GeneratorOperation> operations)
    {
        if (triggerIndex < 0 || operationIndex <= triggerIndex + 1) return 1d;
        var multiplier = 1d;
        for (var index = triggerIndex + 1; index < operationIndex; index++)
        {
            var condition = operations[index];
            if (condition.Scope != OperationScope.ConditionalTrigger
                || !CardEffectRules.IsLegalDependencyPayoff(condition, operation)) continue;
            multiplier *= Math.Clamp(RelativeTriggerFrequency(condition), 0.05d, 1d);
        }
        return multiplier;
    }

    internal static int ValueFitWeight(int value, int target)
    {
        if (value <= 0 || value <= target * 1.15d) return 100;
        if (value <= target * 1.5d) return 68;
        if (value <= target * 2d) return 32;
        if (value <= target * 3d) return 11;
        return 3;
    }

    private static bool IsStaticExtraDamageHits(ComponentAtom atom) =>
        atom.Scope == OperationScope.Modifier
        && (atom.Template is "M:repeat" or "D:RepeatDamage" or "R:RepeatDamage"
            || OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("static_extra_damage_hits"));

    private static bool IsStaticExtraDamageHits(GeneratorOperation operation) =>
        operation.Scope == OperationScope.Modifier
        && (operation.Template is "M:repeat" or "D:RepeatDamage" or "R:RepeatDamage"
            || OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("static_extra_damage_hits"));

    internal static int ExpectedExtraDamageHits(ComponentAtom atom)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(atom);
        var printedExtraHits = Math.Max(1, spec.Values.FirstOrDefault(value =>
            value.Id is "extra_hits" or "repeat_count" or "hits_per_hp_loss_event")?.BaseValue ?? 1);
        return spec is { Opcode: "modify_hits", Variant: "hp_loss_scaled" }
            ? printedExtraHits * 3
            : printedExtraHits;
    }

    internal static int ExpectedExtraDamageHits(GeneratorOperation operation) =>
        ExpectedExtraDamageHits(new ComponentAtom(operation.Template, operation.Scope, operation.ChineseText,
            operation.RequiresSingleTarget, CardReferenceRequirement.None)
            { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(operation) });

    private static double ExpectedContextualExtraDamageHits(GeneratorOperation operation, int operationIndex,
        IReadOnlyList<GeneratorOperation> operations)
    {
        // Rattle explicitly adds one hit for every earlier Osty Attack; unlike dynamic-total modifiers it keeps the
        // host's original hit. Its native prefix is calibrated at 1.2 prior attacks this turn.
        if (operation.Template == "NCR:RepeatPerOstyAttackThisTurn")
            return LinkedCountOrDefault(operation, operationIndex, operations, 1.2d);
        return ExpectedExtraDamageHits(operation)
            * (HasAdjacentMultiplicativeDependency(operation, operationIndex, operations)
                ? LinkedResolutionMultiplier(operation, operationIndex, operations)
                : 1d);
    }

    private static bool HasAdjacentMultiplicativeDependency(GeneratorOperation operation, int operationIndex,
        IReadOnlyList<GeneratorOperation> operations) =>
        operationIndex > 0
        && CardEffectRules.IsMultiplicativeDependencyPrefix(operations[operationIndex - 1])
        && CardEffectRules.IsLegalDependencyPayoff(operations[operationIndex - 1], operation);

    private static bool HasContextualModifierValuation(GeneratorOperation operation)
    {
        if (CardEffectRules.IsDependencyPrefix(operation) || CardEffectRules.IsNegativeEffect(operation)) return true;
        if (operation.Template is "CL:IncreaseRollingDamage" or "M:RepeatAreaOnKill"
            or "NCR:OstyCurrentHpBonusDamage" or "NCR:OstyMaxHpBonusDamage"
            or "NCR:DoomPerDoomThreshold" or "R:DoubleEnergyX"
            or "R:DoubleEitherXAtThreshold" or "M:TriggeredAttackDamagePercent") return true;
        if (CardEffectRules.IsCurrentBlockDamageModifier(operation)
            || CardEffectRules.IsDynamicTotalHitModifier(operation)
            || IsStaticExtraDamageHits(operation)
            || TryDependentNumericIncrease(operation, out _, out _)) return true;
        // Cost-down changes the card's resource coordinate, not a Damage/Block/status host. Its occurrence count is
        // still linked to the adjacent Void prefix, while the card-cost curve is priced outside this effect model.
        return operation.Template == "NCR:CostDownPerVoidPlayed";
    }

    internal static void Validate()
    {
        var fixedModifierFallbacks = Enum.GetValues<GeneratedCharacter>()
            .SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms)
            .Where(atom => atom.Scope == OperationScope.Modifier && !IsCondition(atom))
            .Select(atom => new GeneratorOperation(atom.Template, atom.Scope, string.Empty,
                new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget,
                RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom)))
            .Where(operation => CardEffectRules.IsBeneficialEffect(operation))
            .Where(operation => !HasContextualModifierValuation(operation))
            .Select(operation => operation.Template).Distinct(StringComparer.Ordinal)
            .OrderBy(template => template, StringComparer.Ordinal).ToArray();
        if (fixedModifierFallbacks.Length > 0)
            throw new InvalidOperationException("以下宿主 modifier 仍在使用通用固定价值："
                                                + string.Join(", ", fixedModifierFallbacks));

        var uncalibrated = Enum.GetValues<GeneratedCharacter>()
            .SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms)
            .DistinctBy(atom => (atom.Template, atom.Scope, atom.ChineseText))
            .Select(atom => new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
                new Dictionary<string, int>()))
            .Where(operation => CardEffectRules.TriggerNeedsLinkedEffect(operation)
                                || CardEffectRules.IsDependencyPrefix(operation))
            .Where(operation => !TryCalibratedTriggerFrequency(operation.Template,
                OperationRuntimeSpecCompiler.GetOrCompile(operation), out _))
            .Select(operation => $"{operation.Template}: {operation.ChineseText}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (uncalibrated.Length > 0)
            throw new InvalidOperationException("以下触发/计数前件缺少显式触发频率：\n"
                                                + string.Join("\n", uncalibrated));

        var doomAtoms = CharacterComponentCatalogs.Get(GeneratedCharacter.Necrobinder).Atoms
            .Where(atom => atom.Template is "NCR:ApplyDoom" or "NCR:ApplyDoomAll")
            .ToArray();
        var selectedDoom = doomAtoms.First(atom =>
            !OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("random_enemy_reference")
            && !OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("all_enemies_reference"));
        var randomDoom = doomAtoms.First(atom =>
            OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("random_enemy_reference"));
        var allDoom = doomAtoms.First(atom =>
            OperationRuntimeSpecCompiler.GetOrCompile(atom).Flags.Contains("all_enemies_reference"));
        static double DoomValuePerStack(ComponentAtom atom)
        {
            var amount = OperationRuntimeSpecCompiler.ExplicitFixedValueSlots(atom)[0].BaseValue;
            return EstimatedEffectValue(atom) / (double)amount;
        }
        if (Math.Abs(DoomValuePerStack(selectedDoom) - 80d) > 0.01d
            || Math.Abs(DoomValuePerStack(randomDoom) - 72d) > 0.01d
            || Math.Abs(DoomValuePerStack(allDoom) - 124d) > 0.01d)
            throw new InvalidOperationException("灾厄没有复用伤害的随机/单体/全体目标价值倍率。 ");

        var orbit = new ComponentAtom("A:whenEnergySpent", OperationScope.AbilityTrigger,
            "你每花费4点能量。", false, CardReferenceRequirement.None);
        var orbitWeight = TriggerOccurrenceWeight([orbit]);
        if (orbitWeight is <= 0 or >= 100)
            throw new InvalidOperationException("环绕轨道触发器没有正确应用触发频率先验。");

        var starTrigger = new GeneratorOperation("A:whenOneStarSpent", OperationScope.AbilityTrigger,
            "每花费2颗蓝星时。", new Dictionary<string, int>());
        var starCostCardAtom = CharacterComponentCatalogs.Get(GeneratedCharacter.Regent).Atoms
            .First(atom => atom.Template == "R:ForEachStarCostCard");
        var starCostCardTrigger = new GeneratorOperation(starCostCardAtom.Template, starCostCardAtom.Scope,
            string.Empty, new Dictionary<string, int>(),
            RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(starCostCardAtom));
        var orbitTrigger = new GeneratorOperation("A:whenEnergySpent", OperationScope.AbilityTrigger,
            "你每花费4点能量。", new Dictionary<string, int>());
        if (RelativeTriggerFrequency(starTrigger) <= RelativeTriggerFrequency(orbitTrigger)
            || PayoffScalePercent([starTrigger]) >= PayoffScalePercent([orbitTrigger])
            || RelativeTriggerFrequency(starCostCardTrigger) != 5d)
            throw new InvalidOperationException("高频蓝星触发器没有获得更低的单次收益预算。");

        var priorHitAtom = CharacterComponentCatalogs.Get(GeneratedCharacter.Regent).Atoms
            .First(atom => atom.Template == "R:ForEachPriorAttackHitOnTarget");
        var priorHitTrigger = new GeneratorOperation(priorHitAtom.Template, priorHitAtom.Scope,
            string.Empty, new Dictionary<string, int>(),
            RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(priorHitAtom));
        if (RelativeTriggerFrequency(priorHitTrigger) != 2d)
            throw new InvalidOperationException("此前命中目标计数器没有按2次进行预算折算。");

        static GeneratorOperation CatalogOperation(ComponentAtom atom) => new(atom.Template, atom.Scope,
            string.Empty, new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget,
            RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom), LocalizedText: atom.LocalizedText);
        var regressionAtoms = Enum.GetValues<GeneratedCharacter>()
            .SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms).ToArray();
        GeneratorOperation WithAmount(string template, string slot, int amount)
        {
            var operation = CatalogOperation(regressionAtoms.First(atom => atom.Template == template
                && OperationRuntimeSpecCompiler.GetOrCompile(atom).Values.Any(value => value.Id == slot)));
            return OperationRuntimeSpecCompiler.TryReplaceFixedValue(operation, slot, amount, out var adjusted)
                ? adjusted : throw new InvalidOperationException($"Cannot set {template}.{slot} for regression audit.");
        }
        var twoDamage = WithAmount("T:D", "damage", 2);
        var twoBlock = WithAmount("N:B", "block", 2);
        var twoPoison = WithAmount("T:Poison", "amount", 2);
        var repeatedDamage = new[] { starCostCardTrigger, twoDamage };
        var repeatedBlock = new[] { starCostCardTrigger, twoBlock };
        var repeatedPoison = new[] { starCostCardTrigger, twoPoison };
        if (EstimatedCardLevelRewardValue(twoDamage, 1, repeatedDamage) != 1_400d
            || EstimatedCardLevelRewardValue(twoBlock, 1, repeatedBlock)
                != EstimatedEffectValue(twoBlock) * 5d
            || EstimatedCardLevelRewardValue(twoPoison, 1, repeatedPoison)
                != EstimatedEffectValue(twoPoison) * 5d)
            throw new InvalidOperationException("计数前件没有把伤害解析为多段，或错误地给格挡/中毒加入了多段溢价。");

        var fiveDamage = WithAmount("T:D", "damage", 5);
        var oneExtraHit = CatalogOperation(regressionAtoms.First(atom => atom.Template == "M:repeat"
            && OperationRuntimeSpecCompiler.FixedValue(atom, "extra_hits") == 1));
        var repeatedModifier = new[] { fiveDamage, starCostCardTrigger, oneExtraHit };
        if (!CardEffectRules.IsLegalDependencyPayoff(starCostCardTrigger, oneExtraHit)
            || EstimatedCardLevelRewardValue(oneExtraHit, 2, repeatedModifier) != 3_000d)
            throw new InvalidOperationException("计数前件无法重复兼容的伤害 modifier，或多段适配价值计算错误。");

        var shuffleTrigger = new GeneratorOperation("CL:WheneverDrawPileShuffled",
            OperationScope.AbilityTrigger, "每当你的抽牌堆洗牌时。", new Dictionary<string, int>());
        if (RelativeTriggerFrequency(shuffleTrigger) != DrawPileShuffleTriggerFrequency
            || PayoffScalePercent([shuffleTrigger]) != 71
            || Math.Abs(ExpectedTriggerResolutions(shuffleTrigger, 3d) - 0.75d) > 0.0001d)
            throw new InvalidOperationException("洗牌触发器没有按原版《计策》的四回合一次节奏计价。 ");

        var debuffAppliedTrigger = new GeneratorOperation("A:whenDebuffApplied", OperationScope.AbilityTrigger,
            "每当你给予一个敌人负面状态时。", new Dictionary<string, int>());
        if (RelativeTriggerFrequency(debuffAppliedTrigger) != 1.5d
            || PayoffScalePercent([debuffAppliedTrigger]) != 23
            || !IsHighFrequencyTrigger(debuffAppliedTrigger)
            || ExpectedTriggerResolutions(debuffAppliedTrigger, 3d) != 4.5d)
            throw new InvalidOperationException("给予敌人负面状态触发器没有按每回合1.5次进行高频预算折算。");

        var uniqueOrbMultiplier = new GeneratorOperation("D:ForEachUniqueOrb", OperationScope.Modifier,
            "每有一种不同的充能球，", new Dictionary<string, int>());
        var cardsPlayedThisTurn = new GeneratorOperation("C:untilTurnEnd", OperationScope.ConditionalTrigger,
            "本回合每当你打出一张牌时。", new Dictionary<string, int>());
        var persistentCardPlayed = new GeneratorOperation("A:whenCardPlayed", OperationScope.AbilityTrigger,
            "每当你打出一张牌时。", new Dictionary<string, int>());
        var temporaryCardPlayed = new GeneratorOperation("NCR:WheneverCardPlayedThisTurn",
            OperationScope.AbilityTrigger, "本回合每当你打出一张牌时。", new Dictionary<string, int>());
        var temporaryCardDrawn = new GeneratorOperation("C:untilTurnEndCardDrawn",
            OperationScope.ConditionalTrigger, "本回合每当你抽到一张牌时。", new Dictionary<string, int>());
        var legacyUsesCard = new GeneratorOperation("legacy:wheneverCardUsed", OperationScope.ConditionalTrigger,
            "每使用一张牌时。", new Dictionary<string, int>());
        if (PayoffScalePercent([uniqueOrbMultiplier]) != 25
            || PayoffScalePercent([cardsPlayedThisTurn]) != 25
            || RelativeTriggerFrequency(persistentCardPlayed) != 3.2d
            || PayoffScalePercent([persistentCardPlayed]) != 17
            || RelativeTriggerFrequency(temporaryCardPlayed) != 3.2d
            || PayoffScalePercent([temporaryCardPlayed]) != 25
            || RelativeTriggerFrequency(temporaryCardDrawn) != 3.2d
            || PayoffScalePercent([temporaryCardDrawn]) != 25
            || RelativeTriggerFrequency(legacyUsesCard) != 3.2d
            || PayoffScalePercent([legacyUsesCard]) != 25)
            throw new InvalidOperationException("即时高频/倍乘前件没有获得正确的低单次收益预算。");
        var unblockedDamageTrigger = new GeneratorOperation("A:whenAttackDealsDamage",
            OperationScope.AbilityTrigger, "造成未被格挡的伤害时。", new Dictionary<string, int>());
        var legacyUnblockedDamageTrigger = new GeneratorOperation("legacy:unblockedDamage",
            OperationScope.ConditionalTrigger, "每有一次攻击造成未被格挡的伤害时。", new Dictionary<string, int>());
        var retaliationTrigger = new GeneratorOperation("C:untilTurnEnd", OperationScope.ConditionalTrigger,
            "本回合每当你受到一次攻击时。", new Dictionary<string, int>());
        if (RelativeTriggerFrequency(unblockedDamageTrigger) != 1.8d
            || PayoffScalePercent([unblockedDamageTrigger]) != 23
            || RelativeTriggerFrequency(legacyUnblockedDamageTrigger) != 1.8d
            || PayoffScalePercent([legacyUnblockedDamageTrigger]) != 34
            || RelativeTriggerFrequency(retaliationTrigger) != 2.5d
            || PayoffScalePercent([retaliationTrigger]) != 25)
            throw new InvalidOperationException("受击/造成未被格挡伤害的触发器没有使用指定的高频预算。");
        var temporaryFocus = new ComponentAtom("D:GainTemporaryFocus", OperationScope.NonTargeted,
            "获得1点集中。", false, CardReferenceRequirement.None);
        if (!IsScalableReward(temporaryFocus))
            throw new InvalidOperationException("不同充能球种类数后的临时集中未接入收益缩放模型。");

        var drawPileTrigger = new GeneratorOperation("CL:ForEachDrawPileCard", OperationScope.Modifier,
            "抽牌堆中每有一张牌，", new Dictionary<string, int>());
        if (PayoffScalePercent([drawPileTrigger]) != 10)
            throw new InvalidOperationException("按抽牌堆牌数造成伤害的每牌系数没有受到大幅预算压缩。");

        var dualcast = new ComponentAtom("D:EvokeRightmostOrb", OperationScope.NonTargeted,
            "激发最右侧的充能球2次。", false, CardReferenceRequirement.None);
        var shatterEvoke = new ComponentAtom("D:EvokeAllTwice", OperationScope.NonTargeted,
            "激发所有充能球2次。", false, CardReferenceRequirement.None);
        if (EstimatedEffectValue(dualcast) != 600 || EstimatedEffectValue(shatterEvoke) != 2_400)
            throw new InvalidOperationException("充能球激发没有按 Dualcast/Shatter 原版模板计价。 ");

        var lightningEvoke = new GeneratorOperation("A:whenLightningEvoked", OperationScope.AbilityTrigger,
            "每当你激发闪电充能球时。", new Dictionary<string, int>());
        var drawTwoOperation = new GeneratorOperation("N:Draw", OperationScope.NonTargeted,
            "抽2张牌。", new Dictionary<string, int> { ["triggerIndex"] = 0 });
        var drawTwoAtom = new ComponentAtom("N:Draw", OperationScope.NonTargeted,
            "抽2张牌。", false, CardReferenceRequirement.None);
        var recurringDrawValue = EstimatedCardLevelRewardValue(drawTwoOperation, 1,
            [lightningEvoke, drawTwoOperation]);
        var recurringDrawCenter = ScaleRewardCenter(drawTwoAtom, 2, 1, GeneratedRarity.Common,
            [lightningEvoke], new Random(2701));
        var immediateDrawCenter = ScaleRewardCenter(drawTwoAtom, 2, 1, GeneratedRarity.Common,
            [], new Random(2701));
        if (Math.Abs(recurringDrawValue - 1_791.24d) > 0.001d
            || recurringDrawCenter >= immediateDrawCenter)
            throw new InvalidOperationException("重复触发抽牌没有计入持续卡差溢价，或没有回压卡面抽牌数值。 ");

        var nextTwoTurns = new GeneratorOperation("D:NextTurnsStart", OperationScope.ConditionalTrigger,
            "在接下来的2个回合开始时。", new Dictionary<string, int>());
        var everyTenDraws = new GeneratorOperation("CL:EveryCardsDrawn", OperationScope.AbilityTrigger,
            "你每抽10张牌。", new Dictionary<string, int>());
        var everyFiveCards = new GeneratorOperation("CL:EveryCardsPlayedThisTurn", OperationScope.AbilityTrigger,
            "每当你在一回合内打出5张牌时。", new Dictionary<string, int>());
        var afterThreeTurns = new GeneratorOperation("CL:AfterTurns", OperationScope.AbilityTrigger,
            "在3回合结束后。", new Dictionary<string, int>());
        if (RelativeTriggerFrequency(nextTwoTurns) != 2d
            || Math.Abs(RelativeTriggerFrequency(everyTenDraws) - 0.48d) > 0.0001d
            || Math.Abs(RelativeTriggerFrequency(everyFiveCards) - 0.32d) > 0.0001d
            || HasRepeatedOrMultiplicativePayoff(afterThreeTurns)
            || IsPersistentAbilityTrigger(afterThreeTurns))
            throw new InvalidOperationException("次数/阈值型触发器没有使用其真实触发频率。 ");

        if (Math.Abs(ExpectedTriggerResolutions(persistentCardPlayed, 2.4d) - 7.68d) > 0.0001d
            || Math.Abs(ExpectedTriggerResolutions(temporaryCardPlayed, 2.4d) - 3.2d) > 0.0001d)
            throw new InvalidOperationException("跨回合与仅本回合触发器没有共享正确的持续时间模型。 ");

        var damage = new ComponentAtom("T:D", OperationScope.SingleEnemyOnly,
            "造成6点伤害。", true, CardReferenceRequirement.None);
        var block = new ComponentAtom("N:B", OperationScope.NonTargeted,
            "获得5点格挡。", false, CardReferenceRequirement.None);
        var draw = new ComponentAtom("N:Draw", OperationScope.NonTargeted,
            "抽1张牌。", false, CardReferenceRequirement.None);
        var energy = new ComponentAtom("N:E", OperationScope.NonTargeted,
            "获得1点能量。", false, CardReferenceRequirement.None);
        var permanentStrength = new ComponentAtom("N:Self", OperationScope.NonTargeted,
            "获得1点力量。", false, CardReferenceRequirement.None);
        var permanentFocus = new ComponentAtom("D:GainFocus", OperationScope.NonTargeted,
            "获得1点集中。", false, CardReferenceRequirement.None);
        var orbSlot = new ComponentAtom("D:GainOrbSlots", OperationScope.NonTargeted,
            "获得1个充能球栏位。", false, CardReferenceRequirement.None);
        var twoLightning = new GeneratorOperation("D:ChannelLightning", OperationScope.NonTargeted,
            "生成2个闪电充能球。", new Dictionary<string, int>(), OrbOutputId: "lightning");
        var playSkillFiveTimes = new ComponentAtom("R:PlaySelectedSkillMultipleTimes", OperationScope.NonTargeted,
            "选择手牌中的一张技能牌，将其打出5次。", false, CardReferenceRequirement.HandCard);
        var replayTwoSkills = new ComponentAtom("I:ReplayNextSkills", OperationScope.Independent,
            "在本回合，你打出的下2张技能牌会被额外打出一次。", false, CardReferenceRequirement.None);
        var hiddenGem = new ComponentAtom("CL:ProxyAtomic_HiddenGem", OperationScope.Independent,
            "你抽牌堆中的一张没有重放的随机牌获得2层重放。", false, CardReferenceRequirement.None);
        var forge = new ComponentAtom("R:Forge", OperationScope.NonTargeted,
            "铸造7。", false, CardReferenceRequirement.None);
        var extraHits = new ComponentAtom("M:repeat", OperationScope.Modifier,
            "这张牌额外造成2次伤害。", false, CardReferenceRequirement.None);
        var lostHpExtraHits = new ComponentAtom("M:repeat", OperationScope.Modifier,
            "本场战斗中你每失去过一次生命，这张牌额外造成1次伤害。", false,
            CardReferenceRequirement.None);
        var doubleVulnerable = new ComponentAtom("T:Apply", OperationScope.SingleEnemyOnly,
            "将该敌人身上的易伤层数翻倍。", true, CardReferenceRequirement.None);
        var vulnerableStrength = new ComponentAtom("N:StrengthPerTargetVulnerable", OperationScope.NonTargeted,
            "目标敌人身上每有一层易伤，就获得1点力量。", true, CardReferenceRequirement.None);
        var discardCopy = new ComponentAtom("N:Create", OperationScope.NonTargeted,
            "将此牌的一张复制加入弃牌堆。", false, CardReferenceRequirement.None);
        var zeroCostDiscardCopy = new ComponentAtom("D:CreateZeroCostCopyInDiscard", OperationScope.NonTargeted,
            "将这张牌的0费复制品加入弃牌堆。", false, CardReferenceRequirement.None);
        var randomCurrentCharacterCard = new ComponentAtom("N:Create", OperationScope.NonTargeted,
            "将一张当前角色的随机牌加入手牌。", false, CardReferenceRequirement.None);
        var playTwoRandomHandAttacks = new ComponentAtom("I:AutoPlayRandomAttackFromHand",
            OperationScope.Independent, "随机打出手牌中的2张攻击牌，攻击随机敌人。", false,
            CardReferenceRequirement.HandCard);
        var exhaustAllHand = new ComponentAtom("N:Exhaust", OperationScope.NonTargeted,
            "消耗所有手牌。", false, CardReferenceRequirement.None);
        var enemyStrength = new ComponentAtom("T:Apply", OperationScope.SingleEnemyOnly,
            "使该敌人获得1点力量。", true, CardReferenceRequirement.None);
        var havoc = new ComponentAtom("I:PlayTopCardAndExhaust", OperationScope.Independent,
            "打出抽牌堆顶部的牌并消耗该牌。", false, CardReferenceRequirement.None);
        var plating = new ComponentAtom("N:Self", OperationScope.NonTargeted,
            "获得4层覆甲。", false, CardReferenceRequirement.None);
        var temporaryEnemyStrength = new ComponentAtom("T:Apply", OperationScope.SingleEnemyOnly,
            "使该敌人本回合失去8点力量。", true, CardReferenceRequirement.None);
        var temporaryAllEnemyStrength = new ComponentAtom("N:AllTempStrengthLoss", OperationScope.NonTargeted,
            "使所有敌人本回合失去6点力量。", false, CardReferenceRequirement.None);
        var permanentEnemyStrength = new ComponentAtom("NCR:TargetLoseStrength", OperationScope.SingleEnemyOnly,
            "该敌人失去1点力量。", true, CardReferenceRequirement.None);
        var permanentAllEnemyStrength = new ComponentAtom("R:EnemiesLoseStrength", OperationScope.NonTargeted,
            "所有敌人失去1点力量。", false, CardReferenceRequirement.None);
        var permanentRunDamage = new ComponentAtom("NCR:IncreaseThisCardDamageRun", OperationScope.Independent,
            "这张牌在本局游戏中的伤害永久性增加5点。", false, CardReferenceRequirement.None);
        var permanentRunBlock = new ComponentAtom("D:IncreaseThisCardBlockRun", OperationScope.Independent,
            "这张牌在本局游戏中的格挡值永久增加3点。", false, CardReferenceRequirement.None);
        var playExhaustedShivs = new ComponentAtom("I:PlayExhaustedShivsAtTarget", OperationScope.Independent,
            "将消耗牌堆中的所有小刀对该敌人打出。", true, CardReferenceRequirement.None);
        var vigor = new ComponentAtom("R:GainVigor", OperationScope.NonTargeted,
            "获得2点活力。", false, CardReferenceRequirement.None);
        var temporaryStrength = new ComponentAtom("R:GainStrengthThisTurn", OperationScope.NonTargeted,
            "本回合获得2点力量。", false, CardReferenceRequirement.None);
        var repeatedAreaDamage = new ComponentAtom("N:AllD", OperationScope.NonTargeted,
            "对所有敌人造成4点伤害2次。", false, CardReferenceRequirement.None);
        var randomDamage = new ComponentAtom("N:RandomD", OperationScope.NonTargeted,
            "随机对敌人造成6点伤害。", false, CardReferenceRequirement.None);
        var repeatedRandomDamage = randomDamage with { ChineseText = "随机对敌人造成4点伤害2次。" };
        var repeatedTargetDamage = new ComponentAtom("T:D", OperationScope.SingleEnemyOnly,
            "造成3点伤害3次。", true, CardReferenceRequirement.None);
        var twoShivs = new ComponentAtom("N:CreateShiv", OperationScope.NonTargeted,
            "将2张小刀加入手牌。", false, CardReferenceRequirement.None);
        var freeRandomAttack = new ComponentAtom("I:Create", OperationScope.Independent,
            "将一张随机攻击牌加入手牌。其本回合费用为0。", false, CardReferenceRequirement.None);
        var freeHand = new ComponentAtom("I:FreeHandThisTurn", OperationScope.Independent,
            "你手牌中的所有牌在本回合免费打出。", false, CardReferenceRequirement.None);
        var goldAxe = new ComponentAtom("CL:DamageEqualCardsPlayedCombat", OperationScope.SingleEnemyOnly,
            "造成本场战斗中所打出牌数的伤害。", true, CardReferenceRequirement.None);
        var discardAll = new ComponentAtom("N:DiscardAll", OperationScope.NonTargeted,
            "丢弃所有手牌。", false, CardReferenceRequirement.None);
        var shivsRetain = new ComponentAtom("A:ruleShivsRetain", OperationScope.AbilityRule,
            "小刀获得保留。", false, CardReferenceRequirement.None);
        var shivBonusDamage = new ComponentAtom("A:ruleShivBonusDamage", OperationScope.AbilityRule,
            "小刀额外造成4点伤害。", false, CardReferenceRequirement.None);
        var triggerPoison = new ComponentAtom("I:TriggerPoisonNow", OperationScope.Independent,
            "立即触发所有敌人的中毒。", false, CardReferenceRequirement.None);
        var poisonExtraTrigger = new ComponentAtom("A:rulePoisonExtraTriggers", OperationScope.AbilityRule,
            "中毒会额外触发1次。", false, CardReferenceRequirement.None);
        var royaltiesGold = new ComponentAtom("A:ProxyAtomic_Royalties", OperationScope.AbilityRule,
            "在战斗结束时，获得30金币。", false, CardReferenceRequirement.None);
        var fatalGold = new ComponentAtom("CL:GainGold", OperationScope.NonTargeted,
            "获得20金币。", false, CardReferenceRequirement.None);
        if (EstimatedEffectValue(damage) != 600 || EstimatedEffectValue(block) != 600
            || EstimatedEffectValue(draw) != 460 || EstimatedEffectValue(energy) != 650
            || EstimatedEffectValue(permanentStrength) != 1_200
            || EstimatedEffectValue(permanentFocus) != 1_400
            || EstimatedEffectValue(orbSlot) != 650
            || EstimatedEffectValue(twoLightning) != 1_000
            || EstimatedEffectValue(playSkillFiveTimes) != 7_000
            || EstimatedEffectValue(replayTwoSkills) != 2_200
            || EstimatedEffectValue(hiddenGem) != 2_200
            || EstimatedEffectValue(forge) != 700
            || EstimatedEffectValue(extraHits) != 2_000
            || ExpectedExtraDamageHits(lostHpExtraHits) != 3
            || EstimatedEffectValue(lostHpExtraHits) != 3_000
            || EstimatedEffectValue(doubleVulnerable) != 600
            || EstimatedEffectValue(vulnerableStrength) != 2_400
            || EstimatedEffectValue(discardCopy) != 1_100
            || EstimatedEffectValue(zeroCostDiscardCopy) != 1_500
            || EstimatedEffectValue(randomCurrentCharacterCard) != 450
            || EstimatedEffectValue(playTwoRandomHandAttacks) != 3_000
            || EstimatedEffectValue(exhaustAllHand) != 1_500
            || EstimatedEffectValue(enemyStrength) != 900
            || EstimatedEffectValue(havoc) != 900
            || EstimatedEffectValue(plating) != 1_740
            || EstimatedEffectValue(temporaryEnemyStrength) != 1_360
            || EstimatedEffectValue(temporaryAllEnemyStrength) != 1_800
            || EstimatedEffectValue(permanentEnemyStrength) != 1_500
            || EstimatedEffectValue(permanentAllEnemyStrength) != 2_000
            || EstimatedEffectValue(permanentRunDamage) != 3_000
            || EstimatedEffectValue(permanentRunBlock) != 2_160
            || EstimatedEffectValue(playExhaustedShivs) != 2_400
            || EstimatedEffectValue(shivsRetain) != 300
            || EstimatedEffectValue(shivBonusDamage) != 1_800
            || EstimatedEffectValue(vigor) != 360
            || EstimatedEffectValue(repeatedAreaDamage) != 1_575
            || EstimatedEffectValue(randomDamage) != 540
            || EstimatedEffectValue(repeatedRandomDamage) != 855
            || EstimatedEffectValue(repeatedTargetDamage) != 1_100
            || EstimatedEffectValue(twoShivs) != 990
            || EstimatedEffectValue(freeRandomAttack) != 1_100
            || EstimatedEffectValue(freeHand) != FreeHandThisTurnValue
            || EstimatedEffectValue(goldAxe) != GoldAxeDynamicDamageValue
            || EstimatedEffectValue(poisonExtraTrigger) != 1_350
            || PoisonExtraTriggerValue(2) != 2_700
            || EstimatedEffectValue(royaltiesGold) != 1_500
            || EstimatedEffectValue(fatalGold) != 1_000
            || !ComponentAssemblyGenerator.IsExplicitRareOperation(royaltiesGold)
            || !ComponentAssemblyGenerator.IsExplicitRareOperation(fatalGold)
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                royaltiesGold, GeneratedRarity.Common) != 12
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                royaltiesGold, GeneratedRarity.Rare) != 45
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                fatalGold, GeneratedRarity.Common) != 12
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                fatalGold, GeneratedRarity.Rare) != 45
            || !CardEffectRules.IsRandomCardGeneration(freeRandomAttack)
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                doubleVulnerable, GeneratedRarity.Common) != 22
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                doubleVulnerable, GeneratedRarity.Rare) != 70
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                randomCurrentCharacterCard, GeneratedRarity.Common) != 22
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                randomCurrentCharacterCard, GeneratedRarity.Rare) != 70
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                plating, GeneratedRarity.Common) != 12
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                plating, GeneratedRarity.Rare) != 45
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                permanentRunDamage, GeneratedRarity.Common) != 22
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                permanentRunBlock, GeneratedRarity.Rare) != 70
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                playExhaustedShivs, GeneratedRarity.Common) != 22
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                playExhaustedShivs, GeneratedRarity.Rare) != 70
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                freeHand, GeneratedRarity.Common) != 22
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                discardAll, GeneratedRarity.Uncommon) != 28
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                shivsRetain, GeneratedRarity.Uncommon) != 28
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                triggerPoison, GeneratedRarity.Ancient) != 70
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                poisonExtraTrigger, GeneratedRarity.Common) != 22
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                poisonExtraTrigger, GeneratedRarity.Rare) != 70
            || !ComponentAssemblyGenerator.IsExplicitRareOperation(hiddenGem)
            || ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(
                hiddenGem, GeneratedRarity.Common) != 22
            || EffectSelectionTuning.TriggeredReplayWeight([playSkillFiveTimes], [persistentCardPlayed]) != 4
            || EffectSelectionTuning.TriggeredReplayWeight([playSkillFiveTimes], []) != 100)
            throw new InvalidOperationException($"基础效果价值换算表发生了意外变化："
                + $"plating={EstimatedEffectValue(plating)}, tempStrength={EstimatedEffectValue(temporaryEnemyStrength)}, "
                + $"tempAllStrength={EstimatedEffectValue(temporaryAllEnemyStrength)}, "
                + $"runDamage={EstimatedEffectValue(permanentRunDamage)}, runBlock={EstimatedEffectValue(permanentRunBlock)}, "
                + $"exhaustedShivs={EstimatedEffectValue(playExhaustedShivs)}, "
                + $"random={EstimatedEffectValue(randomDamage)}, repeatedRandom={EstimatedEffectValue(repeatedRandomDamage)}, "
                + $"twoShivs={EstimatedEffectValue(twoShivs)}, "
                + $"platingCommon={ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(plating, GeneratedRarity.Common)}, "
                + $"platingRare={ComponentAssemblyGenerator.ExplicitRareOperationRarityWeight(plating, GeneratedRarity.Rare)}。");
        GeneratorOperation Operation(ComponentAtom atom) => new(atom.Template, atom.Scope, atom.ChineseText,
            new Dictionary<string, int>(), RequiresSingleTarget: atom.RequiresSingleTarget);
        var cheapFreeHand = new List<GeneratorOperation> { Operation(freeHand) };
        var cheapFreeHandBounds = ComponentAssemblyGenerator.WholeCardBudgetBounds(
            GeneratedRarity.Rare, 1d, 1, balancedValues: true,
            character: GeneratedCharacter.Silent);
        var bulletTimeNoDraw = new GeneratorOperation("I:PreventDrawThisTurn", OperationScope.Independent,
            "你在本回合内不能再抽牌。", new Dictionary<string, int>());
        var bulletTimeAnchor = ComponentAssemblyGenerator.CalibratedWholeCardCenter(
            GeneratedRarity.Rare, 3d) * NegativeEffectTuning.BaseMultiplier(bulletTimeNoDraw);
        if (cheapFreeHandBounds.Maximum >= FreeHandThisTurnValue
            || ComponentAssemblyGenerator.ApplyWholeCardBudgetEnvelope(cheapFreeHand,
                GeneratedRarity.Rare, 1d, GeneratedCardType.Skill, [], true,
                balancedValues: true, character: GeneratedCharacter.Silent, ultimateChaos: false)
            || Math.Abs(FreeHandThisTurnValue - bulletTimeAnchor) > 25d)
            throw new InvalidOperationException("手牌全部免费没有按三费稀有《子弹时间》及其禁抽代价定价，"
                                                + "或一费单效果版本绕过了平衡上限。");
        var summonX = new GeneratorOperation("NCR:SummonX", OperationScope.NonTargeted, "召唤X。",
            new Dictionary<string, int>(), RuntimeSpec: new OperationRuntimeSpec(
                OperationRuntimeSpec.CurrentSchemaVersion, "template_self_action", "ncr_summonx", "self",
                "none", "none", "any", ["summon_reference"],
                [new RuntimeValueSlot("amount", 0, "energy_x")], null, null));
        var summonXAtOne = EstimatedPositiveCardValueAtOrdinaryX([summonX], 1, true,
            GeneratedCardType.Skill, []);
        var summonOne = new GeneratorOperation("NCR:Summon", OperationScope.NonTargeted,
            "召唤1。", new Dictionary<string, int>());
        var soulOne = new GeneratorOperation("NCR:CreateSoulInDraw", OperationScope.NonTargeted,
            "将1张灵魂加入抽牌堆。", new Dictionary<string, int>());
        var soulX = new GeneratorOperation("NCR:CreateSoulInDrawX", OperationScope.NonTargeted,
            "将X张灵魂加入抽牌堆。", new Dictionary<string, int>(), RuntimeSpec: new OperationRuntimeSpec(
                OperationRuntimeSpec.CurrentSchemaVersion, "template_self_action", "ncr_create_soul_draw_x",
                "self", "none", "draw", "any", ["soul_reference"],
                [new RuntimeValueSlot("amount", 0, "energy_x")], null, null));
        if (EstimatedEffectValue(new GeneratorOperation("NCR:Summon", OperationScope.NonTargeted,
                "召唤8。", new Dictionary<string, int>())) != 1_912
            || Math.Abs(summonXAtOne - SummonValuePerPoint) > 0.001d
            || EstimatedEffectValue(summonX) != EstimatedEffectValue(summonOne)
            || EstimatedEffectValue(soulX) != EstimatedEffectValue(soulOne)
            || ComponentAssemblyGenerator.HasAdequateOrdinaryXCardValue([summonX],
                GeneratedCardType.Skill, [], true))
            throw new InvalidOperationException("X费召唤/灵魂没有与对应非X组件共享单位价值，或召唤仍可绕过最低效率检查。 ");

        GeneratorOperation[] NativeOperations(GeneratedCharacter character, string cardId)
        {
            var recipe = CharacterComponentCatalogs.Get(character).Recipes.Single(candidate =>
                candidate.Id == cardId);
            return recipe.Atoms.Select((atom, index) => new GeneratorOperation(atom.Template, atom.Scope,
                string.Empty, recipe.TriggerOwners[index] < 0
                    ? new Dictionary<string, int>()
                    : new Dictionary<string, int> { ["triggerIndex"] = recipe.TriggerOwners[index] },
                RequiresSingleTarget: atom.RequiresSingleTarget,
                RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom))).ToArray();
        }

        double NativeValue(GeneratedCharacter character, string cardId) =>
            EstimatedPositiveCardValue(NativeOperations(character, cardId));

        var dirgeOperations = NativeOperations(GeneratedCharacter.Necrobinder, "Dirge");
        var dirgeAtOne = EstimatedPositiveCardValueAtOrdinaryX(dirgeOperations, 1, true,
            GeneratedCardType.Skill, [CardTag.Exhaust]);
        var foregoneConclusion = NativeOperations(GeneratedCharacter.Regent, "ForegoneConclusion").Single();
        var foregoneConclusionUpgraded = foregoneConclusion with
        {
            RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(foregoneConclusion) with
            {
                Values = OperationRuntimeSpecCompiler.GetOrCompile(foregoneConclusion).Values.Select(value =>
                    value.Id == "amount" ? value with { BaseValue = 3 } : value).ToArray()
            }
        };
        var nativeValueAnchors = new (GeneratedCharacter Character, string CardId, double Expected)[]
        {
            (GeneratedCharacter.Silent, "Mirage", 1_800d),
            (GeneratedCharacter.Colorless, "JackOfAllTrades", 855d),
            (GeneratedCharacter.Necrobinder, "TimesUp", 2_050d),
            (GeneratedCharacter.Colorless, "Prolong", 1_400d),
            (GeneratedCharacter.Colorless, "GoldAxe", GoldAxeDynamicDamageValue),
            (GeneratedCharacter.Ironclad, "BodySlam", 1_100d),
            (GeneratedCharacter.Silent, "Shadowmeld", 1_400d),
            (GeneratedCharacter.Silent, "Nightmare", 5_100d),
            (GeneratedCharacter.Necrobinder, "DevourLife", 1_075.5d),
            (GeneratedCharacter.Ironclad, "FiendFire", 4_200d),
            (GeneratedCharacter.Regent, "SpectrumShift", 2_565d),
            (GeneratedCharacter.Regent, "BundleOfJoy", 2_565d),
            (GeneratedCharacter.Defect, "CreativeAi", 3_360d),
            (GeneratedCharacter.Ironclad, "Stoke", 2_700d),
            (GeneratedCharacter.Colorless, "RollingBoulder", 6_460d),
            (GeneratedCharacter.Silent, "Murder", 3_400d),
            (GeneratedCharacter.Necrobinder, "ReaperForm", 3_375d),
            (GeneratedCharacter.Silent, "BulletTime", FreeHandThisTurnValue),
            (GeneratedCharacter.Necrobinder, "SentryMode", 2_550d),
            (GeneratedCharacter.Ironclad, "PerfectedStrike", 1_600d),
            (GeneratedCharacter.Ironclad, "Bully", 1_000d),
            (GeneratedCharacter.Ironclad, "AshenStrike", 1_500d),
            (GeneratedCharacter.Silent, "MementoMori", 1_340d),
            (GeneratedCharacter.Necrobinder, "PullFromBelow", 1_100d),
            (GeneratedCharacter.Regent, "LunarBlast", 900d),
            (GeneratedCharacter.Regent, "Radiate", 775d),
            (GeneratedCharacter.Necrobinder, "Rattle", 1_660d),
            (GeneratedCharacter.Necrobinder, "DeathMarch", 2_020d),
            (GeneratedCharacter.Ironclad, "ExpectAFight", 2_460d),
            (GeneratedCharacter.Colorless, "Rend", 2_000d),
            (GeneratedCharacter.Silent, "EchoingSlash", 2_317.25d),
            (GeneratedCharacter.Regent, "CrescentSpear", 1_300d),
            (GeneratedCharacter.Defect, "Barrage", 1_700d),
            (GeneratedCharacter.Regent, "HeavenlyDrill", 1_340d),
            (GeneratedCharacter.Necrobinder, "Unleash", 600d),
            (GeneratedCharacter.Necrobinder, "Protector", 1_300d),
            (GeneratedCharacter.Necrobinder, "NoEscape", 1_600d),
            (GeneratedCharacter.Silent, "PreciseCut", 1_300d),
            (GeneratedCharacter.Colorless, "Stratagem", 750d),
            (GeneratedCharacter.Regent, "ForegoneConclusion", 1_250d)
        };
        var nativeAnchorFailures = nativeValueAnchors.Select(anchor =>
                (anchor, Actual: NativeValue(anchor.Character, anchor.CardId)))
            .Where(item => Math.Abs(item.Actual - item.anchor.Expected) > 0.001d)
            .Select(item => $"{item.anchor.Character}/{item.anchor.CardId}:"
                + $" expected={item.anchor.Expected:0.###}, actual={item.Actual:0.###}").ToList();
        var cascadeAtOne = EstimatedPositiveCardValueAtOrdinaryX(
            NativeOperations(GeneratedCharacter.Ironclad, "Cascade"), 1, true,
            GeneratedCardType.Skill, []);
        if (Math.Abs(cascadeAtOne - 1_100d) > 0.001d)
            nativeAnchorFailures.Add($"Ironclad/Cascade@X1: expected=1100, actual={cascadeAtOne:0.###}");
        if (Math.Abs(dirgeAtOne - 1_049d) > 0.001d)
            nativeAnchorFailures.Add($"Necrobinder/Dirge@X1: expected=1049, actual={dirgeAtOne:0.###}");
        var foregoneConclusionUpgradeValue = EstimatedPositiveCardValue([foregoneConclusionUpgraded]);
        if (Math.Abs(foregoneConclusionUpgradeValue - 1_875d) > 0.001d)
            nativeAnchorFailures.Add("Regent/ForegoneConclusion+3: expected=1875, actual="
                                     + $"{foregoneConclusionUpgradeValue:0.###}");
        if (nativeAnchorFailures.Count > 0)
            throw new InvalidOperationException("原版离群组件没有按结构化卡面数值或动态效果锚点计价："
                                                + string.Join("; ", nativeAnchorFailures));
        var rareSummonAtom = CharacterComponentCatalogs.Get(GeneratedCharacter.Necrobinder).Recipes
            .Single(candidate => candidate.Id == "Afterlife").Atoms.Single();
        var rareSummonEight = new GeneratorOperation(rareSummonAtom.Template, rareSummonAtom.Scope,
            "召唤6。", new Dictionary<string, int>(),
            RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(rareSummonAtom));
        if (!OperationRuntimeSpecCompiler.TryReplaceFixedValue(rareSummonEight, "amount", 8,
                out rareSummonEight))
            throw new InvalidOperationException("召唤数值探针无法写入结构化amount槽。 ");
        var rareSummonProbe = new List<GeneratorOperation> { rareSummonEight };
        var rareSummonEnvelopeApplied = ComponentAssemblyGenerator.ApplyWholeCardBudgetEnvelope(rareSummonProbe,
                GeneratedRarity.Rare, 1d, GeneratedCardType.Skill, [], true, true,
                GeneratedCharacter.Necrobinder, false);
        var rareSummonAmount = OperationRuntimeSpecCompiler.StaticLiteralValue(rareSummonProbe[0], "amount", 0);
        if (!rareSummonEnvelopeApplied || rareSummonAmount != 8)
            throw new InvalidOperationException($"一费稀有纯召唤8没有落入重新校准后的整卡预算区间："
                + $"applied={rareSummonEnvelopeApplied}, amount={rareSummonAmount}, "
                + $"value={EstimatedPositiveCardValue(rareSummonProbe)}。 ");
        var damageGrowth = new GeneratorOperation("I:IncreaseDamageThisCombat", OperationScope.Independent,
            "在本场战斗中，此卡的基础伤害增加4点。", new Dictionary<string, int>());
        var targetDamageSix = Operation(damage);
        var targetDamageSeven = targetDamageSix;
        var oneHostGrowth = new[] { targetDamageSix, damageGrowth };
        var twoHostGrowth = new[] { targetDamageSix, targetDamageSeven, damageGrowth };
        var oneHostGrowthValue = EstimatedPositiveCardValue(oneHostGrowth)
            - EstimatedPositiveCardValue([targetDamageSix]);
        var twoHostGrowthValue = EstimatedPositiveCardValue(twoHostGrowth)
            - EstimatedPositiveCardValue([targetDamageSix, targetDamageSeven]);
        if (Math.Abs(DependentNumericValueMultiplier(damageGrowth, 1, oneHostGrowth) - 1d) > 0.001d
            || Math.Abs(DependentNumericValueMultiplier(damageGrowth, 2, twoHostGrowth) - 2d) > 0.001d
            || Math.Abs(twoHostGrowthValue - oneHostGrowthValue * 2d) > 0.001d)
            throw new InvalidOperationException("本卡伤害成长没有按所有受影响伤害字段的每点价值相加计价。");

        var normalizedTwoHostGrowth = twoHostGrowth.ToList();
        ComponentAssemblyGenerator.NormalizeDependentNumericIncreases(normalizedTwoHostGrowth);
        if (OperationRuntimeSpecCompiler.StaticLiteralValue(normalizedTwoHostGrowth[2], "amount", 0) != 2)
            throw new InvalidOperationException("双单体伤害字段没有把本卡+4成长归一化为+2。");

        var frequentDamageTrigger = new GeneratorOperation("A:whenCardPlayed", OperationScope.AbilityTrigger,
            "每当你打出一张牌时。", new Dictionary<string, int>());
        var frequentDamage = targetDamageSix with
        {
            Parameters = new Dictionary<string, int> { ["triggerIndex"] = 0 }
        };
        var frequentGrowth = new[] { frequentDamageTrigger, frequentDamage, damageGrowth };
        if (DependentNumericValueMultiplier(damageGrowth, 2, frequentGrowth) <= 3d)
            throw new InvalidOperationException("高频伤害字段没有大幅提高本卡伤害成长的边际价值。");

        var perfectStrikeGrowth = new GeneratorOperation("M:base", OperationScope.Modifier,
            "本场战斗中每有一张名称含“打击”的牌，这张牌额外造成2点伤害。",
            new Dictionary<string, int>());
        var perfectStrikePair = new[] { targetDamageSix, targetDamageSeven, perfectStrikeGrowth };
        if (Math.Abs(DependentNumericValueMultiplier(perfectStrikeGrowth, 2, perfectStrikePair) - 2d) > 0.001d)
            throw new InvalidOperationException("完美打击式数值加算没有继承所有伤害字段的价值。");

        var blockFive = Operation(block);
        var blockSix = Operation(block);
        var blockGrowth = new GeneratorOperation("D:IncreaseThisCardBlockRun", OperationScope.Independent,
            "本局游戏中，此牌的基础格挡增加4点。", new Dictionary<string, int>());
        var twoHostBlockGrowth = new[] { blockFive, blockSix, blockGrowth };
        if (Math.Abs(DependentNumericValueMultiplier(blockGrowth, 2, twoHostBlockGrowth) - 2d) > 0.001d)
            throw new InvalidOperationException("本卡格挡成长没有按所有受影响格挡字段的每点价值相加计价。");
        var shivOutlierProbe = new[]
        {
            new GeneratorOperation("N:RandomD", OperationScope.NonTargeted,
                "随机对敌人造成3点伤害5次。", new Dictionary<string, int>()),
            Operation(twoShivs)
        };
        var shivOutlierMaximum = ComponentAssemblyGenerator.WholeCardBudgetBounds(
            GeneratedRarity.Common, 1d, PositiveRewardFieldCount(shivOutlierProbe),
            character: GeneratedCharacter.Silent).Maximum;
        if (EstimatedPositiveCardValue(shivOutlierProbe) <= shivOutlierMaximum)
            throw new InvalidOperationException("两张小刀仍被低估，未能让1费普通3×5随机伤害组合越过整卡预算上限。");
        var repeatedDamageBase = new[] { Operation(damage), Operation(extraHits) };
        if (EstimatedDamagePackageValue(repeatedDamageBase) != 2_000d)
            throw new InvalidOperationException("额外伤害次数没有计入完整伤害包价值。");
        var finisherModifier = new GeneratorOperation("M:RepeatPerAttackThisTurn", OperationScope.Modifier,
            "本回合每打出过一张攻击牌，就造成一次伤害。", new Dictionary<string, int>());
        var flechettesModifier = new GeneratorOperation("M:RepeatPerSkillInHand", OperationScope.Modifier,
            "手牌中每有一张技能牌，就造成一次伤害。", new Dictionary<string, int>());
        var barrageModifier = new GeneratorOperation("D:RepeatPerOrb", OperationScope.Modifier,
            "每有一个充能球，这张牌就造成一次伤害。", new Dictionary<string, int>());
        GeneratorOperation DamageProbe(int amount)
        {
            if (!OperationRuntimeSpecCompiler.TryReplacePrimaryExplicitFixedValue(targetDamageSix, amount,
                    out var updated))
                throw new InvalidOperationException("无法构造动态段数价值测试的伤害 operation。");
            return updated;
        }
        var damageTwelve = DamageProbe(12);
        var damageFive = DamageProbe(5);
        var damageTenProbe = DamageProbe(10);
        var finisherSix = new[] { targetDamageSix, finisherModifier };
        var finisherTwelve = new[] { damageTwelve, finisherModifier };
        var flechettesFive = new[] { damageFive, flechettesModifier };
        var flechettesTen = new[] { damageTenProbe, flechettesModifier };
        var barrageFive = new[] { damageFive, barrageModifier };
        if (Math.Abs(EstimatedPositiveCardValue(finisherSix) - 1_300d) > 0.001d
            || Math.Abs(EstimatedPositiveCardValue(finisherTwelve) - 2_500d) > 0.001d
            || Math.Abs(EstimatedDamagePackageValue(finisherTwelve) - 2_500d) > 0.001d
            || Math.Abs(EstimatedPositiveCardValue(flechettesFive) - 1_700d) > 0.001d
            || Math.Abs(EstimatedPositiveCardValue(flechettesTen) - 3_200d) > 0.001d
            || Math.Abs(EstimatedDamagePackageValue(flechettesTen) - 3_200d) > 0.001d
            || Math.Abs(EstimatedPositiveCardValue(barrageFive) - 1_700d) > 0.001d
            || Math.Abs(EstimatedDamagePackageValue(barrageFive) - 1_700d) > 0.001d)
            throw new InvalidOperationException("终结技/飞镖/弹幕齐射的动态段数没有继承当前每段伤害价值。");
        if (MixedDamageBlockScale(2_600d, 1_320d) is not (>= 0.92d and <= 0.94d))
            throw new InvalidOperationException("攻防混合卡没有支付足够的灵活性预算。 ");
        if (EstimatedPositiveCardValue([Operation(damage), Operation(lostHpExtraHits)]) != 2_700d)
            throw new InvalidOperationException("按三次失去生命估算的额外伤害次数价值发生了意外变化。");
        var repeatedDamageHitUpgrade = new[]
        {
            repeatedDamageBase[0],
            repeatedDamageBase[1] with { ChineseText = "这张牌额外造成3次伤害。" }
        };
        var repeatedDamageNumberUpgrade = new[]
        {
            repeatedDamageBase[0] with { ChineseText = "造成7点伤害。" },
            repeatedDamageBase[1]
        };
        if (EstimatedPositiveCardValue(repeatedDamageHitUpgrade) - EstimatedPositiveCardValue(repeatedDamageBase)
            <= EstimatedPositiveCardValue(repeatedDamageNumberUpgrade) - EstimatedPositiveCardValue(repeatedDamageBase))
            throw new InvalidOperationException("额外伤害次数升级没有按宿主伤害计算整卡边际价值。");
        var equalDoom = new GeneratorOperation("NCR:ApplyDoomEqualDamage", OperationScope.NonTargeted,
            "给予等量于所造成伤害的灾厄。", new Dictionary<string, int>());
        var equalDoomBase = new[] { repeatedDamageBase[0], equalDoom };
        var equalDoomUpgrade = new[] { repeatedDamageNumberUpgrade[0], equalDoom };
        if (EstimatedPositiveCardValue(equalDoomUpgrade) - EstimatedPositiveCardValue(equalDoomBase) <= 200d)
            throw new InvalidOperationException("等量伤害衍生收益没有随宿主伤害升级进入边际价值。");
        var drawTwo = draw with { ChineseText = "抽2张牌。" };
        var blockEleven = block with { ChineseText = "获得11点格挡。" };
        var damageEight = damage with { ChineseText = "造成8点伤害。" };
        var damageNine = damage with { ChineseText = "造成9点伤害。" };
        var damageTen = damage with { ChineseText = "造成10点伤害。" };
        var damageFifteen = damage with { ChineseText = "造成15点伤害。" };
        var damageSixteen = damage with { ChineseText = "造成16点伤害。" };
        var damageNineteen = damage with { ChineseText = "造成19点伤害。" };
        var damageTwenty = damage with { ChineseText = "造成20点伤害。" };
        var damageSeventeen = damage with { ChineseText = "造成17点伤害。" };
        var damageEighteen = damage with { ChineseText = "造成18点伤害。" };
        var turnStart = new GeneratorOperation("A:turnStart", OperationScope.AbilityTrigger,
            "在你的回合开始时。", new Dictionary<string, int>());
        var turnStartBlockTwo = new GeneratorOperation("N:B", OperationScope.NonTargeted,
            "获得2点格挡。", new Dictionary<string, int> { ["triggerIndex"] = 0 });
        var turnStartBlockThree = turnStartBlockTwo with { ChineseText = "获得3点格挡。" };
        var nextAttack = new GeneratorOperation("C:untilTurnEnd", OperationScope.ConditionalTrigger,
            "当你打出下一张攻击牌时。", new Dictionary<string, int>());
        var nextAttackBlockFive = new GeneratorOperation("N:B", OperationScope.NonTargeted,
            "获得5点格挡。", new Dictionary<string, int> { ["triggerIndex"] = 0 });
        if (HasAdequateTopRarityCardValue([Operation(draw)], GeneratedRarity.Rare, 1)
            || HasAdequateTopRarityCardValue([Operation(drawTwo)], GeneratedRarity.Rare, 1)
            || HasAdequateTopRarityCardValue([Operation(damageEight)], GeneratedRarity.Rare, 1)
            || !HasAdequateTopRarityCardValue([Operation(damageTen)], GeneratedRarity.Rare, 1)
            || !HasAdequateTopRarityCardValue([Operation(draw), Operation(block)], GeneratedRarity.Rare, 1)
            || !HasAdequateTopRarityCardValue([Operation(draw), Operation(blockEleven)], GeneratedRarity.Rare, 1)
            || HasAdequateTopRarityCardValue([turnStart, turnStartBlockTwo], GeneratedRarity.Rare, 1)
            || !HasAdequateTopRarityCardValue([turnStart, turnStartBlockThree], GeneratedRarity.Rare, 1)
            || HasAdequateTopRarityCardValue([Operation(damageSeventeen)], GeneratedRarity.Ancient, 1)
            || !HasAdequateTopRarityCardValue([Operation(damageEighteen)], GeneratedRarity.Ancient, 1)
            || HasAdequateTopRarityCardValue([Operation(damageEight)], GeneratedRarity.Rare, 0)
            || !HasAdequateTopRarityCardValue([Operation(damageNine)], GeneratedRarity.Rare, 0)
            || !HasAdequateTopRarityCardValue([Operation(drawTwo)], GeneratedRarity.Rare, 0)
            || HasAdequateTopRarityCardValue([nextAttack, nextAttackBlockFive], GeneratedRarity.Rare, 0)
            || !TryMeasureTopRarityCardValue([nextAttack, nextAttackBlockFive], GeneratedRarity.Rare, 0,
                out var nextAttackValue, out var zeroCostRareFloor)
            || Math.Abs(nextAttackValue - 540d) > 0.01d
            || Math.Abs(zeroCostRareFloor - 900d) > 0.01d
            || !HasAdequateTopRarityCardValue([Operation(draw)], GeneratedRarity.Common, 1))
            throw new InvalidOperationException("高稀有度整卡最低价值线发生了意外变化。");

        // Pin the intended global shape: ordinary cards stay close to a native 10-point reference, with a small
        // high tail and a similarly small low tail rather than the former extremely wide 75%-170% curve.
        var distributionRandom = new Random(20260825);
        var samples = Enumerable.Range(0, 100_000)
            .Select(_ => ScaleRewardCenter(damage, 20, 1, GeneratedRarity.Common,
                Array.Empty<GeneratorOperation>(), distributionRandom)).ToArray();
        var average = samples.Average();
        var highRollRate = samples.Count(value => value >= 23) / (double)samples.Length;
        var centerRate = samples.Count(value => value == 22) / (double)samples.Length;
        if (average is < 22.08d or > 22.12d || highRollRate is < 0.09d or > 0.11d
            || centerRate is < 0.89d or > 0.91d)
            throw new InvalidOperationException(
                $"通用收益分布不再满足紧凑均值与受控尾部要求：average={average:0.00}, high={highRollRate:P1}, center={centerRate:P1}。");

        // Aggressive mode must remap the distribution itself, not multiply the finished card value. Pin both
        // endpoints and an interior quantile so later balancing cannot accidentally collapse this into a flat
        // post-processing multiplier.
        if (MapToAggressiveBudgetDistribution(damage, GeneratedRarity.Common, 95) != 95
            || MapToAggressiveBudgetDistribution(damage, GeneratedRarity.Common, 100) != 150
            || MapToAggressiveBudgetDistribution(damage, GeneratedRarity.Common, 97) != 117
            || MapToAggressiveBudgetDistribution(damage, GeneratedRarity.Ancient, 90) != 90
            || MapToAggressiveBudgetDistribution(damage, GeneratedRarity.Ancient, 103) != 155)
            throw new InvalidOperationException("数值激进模式没有按预算分布上下界进行线性映射。");
    }
}
