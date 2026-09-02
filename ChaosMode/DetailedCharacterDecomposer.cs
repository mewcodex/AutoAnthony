using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace AutoAnthony;

internal sealed record DetailedDecomposition(
    IReadOnlyList<ComponentAtom> Atoms,
    IReadOnlyList<int> TriggerOwners);

internal static class DetailedCharacterDecomposer
{
    internal static bool TryDecompose(GeneratedCharacter character, CardModel card, string chinese, string english,
        out DetailedDecomposition decomposition)
    {
        decomposition = null!;
        return character switch
        {
            GeneratedCharacter.Defect => DefectDecomposer.TryDecompose(card, chinese, english, out decomposition),
            GeneratedCharacter.Necrobinder => NecrobinderDecomposer.TryDecompose(card, chinese, english, out decomposition),
            GeneratedCharacter.Regent => RegentDecomposer.TryDecompose(card, chinese, english, out decomposition),
            _ => false
        };
    }

    internal static ComponentAtom Atom(string template, OperationScope scope, string chinese, string english,
        bool requiresTarget = false, CardReferenceRequirement cardReference = CardReferenceRequirement.None)
    {
        ExternalOperationTextRegistry.Register(template, FinishEnglish(english));
        return new ComponentAtom(template, scope, FinishChinese(chinese), requiresTarget, cardReference);
    }

    internal static ComponentAtom Damage(int amount) =>
        new("T:D", OperationScope.SingleEnemyOnly, $"造成{amount}点伤害。", true, CardReferenceRequirement.None);

    internal static ComponentAtom AllDamage(int amount, int hits = 1) =>
        new("N:AllD", OperationScope.NonTargeted,
            hits == 1 ? $"对所有敌人造成{amount}点伤害。" : $"对所有敌人造成{amount}点伤害{hits}次。",
            false, CardReferenceRequirement.None);

    internal static ComponentAtom Block(int amount) =>
        new("N:B", OperationScope.NonTargeted, $"获得{amount}点格挡。", false, CardReferenceRequirement.None);

    internal static ComponentAtom Draw(int amount) =>
        new("N:Draw", OperationScope.NonTargeted, $"抽{amount}张牌。", false, CardReferenceRequirement.None);

    internal static ComponentAtom Apply(string status, int amount) =>
        new("T:Apply", OperationScope.SingleEnemyOnly, $"给予{amount}层{status}。", true, CardReferenceRequirement.None);

    internal static int Value(CardModel card, string key, int fallback = 1) =>
        card.DynamicVars.TryGetValue(key, out var value) ? value.IntValue : fallback;

    private static string FinishChinese(string text) => text.Trim().EndsWith('。') ? text.Trim() : text.Trim() + "。";
    private static string FinishEnglish(string text) => text.Trim().EndsWith('.') ? text.Trim() : text.Trim() + ".";
}

internal static class RegentDecomposer
{
    // These cards contain one indivisible rule.  The proxy executes only that rule; multi-sentence cards below
    // are always decomposed into reusable operations.
    private static readonly HashSet<string> AtomicCards = new(StringComparer.Ordinal)
    {
        "Alignment", "Arsenal", "Begone", "BlackHole", "BundleOfJoy", "Charge",
        "ForegoneConclusion", "Furnace", "Genesis", "Guards", "MonarchsGaze", "NeutronAegis",
        "PaleBlueDot", "Parry", "PillarOfCreation", "Quasar", "RoyalGamble", "Royalties", "SpectrumShift",
        "Stardust", "SwordSage", "Terraforming", "TheSealedThrone", "TheSmith", "Tyranny", "Venerate"
    };

    internal static bool TryDecompose(CardModel card, string chinese, string english, out DetailedDecomposition decomposition)
    {
        var id = card.GetType().Name;
        if (AtomicCards.Contains(id))
        {
            var scope = card.Type == CardType.Power ? OperationScope.AbilityRule
                : card.TargetType == TargetType.AnyEnemy ? OperationScope.SingleEnemyOnly
                : OperationScope.Independent;
            var prefix = card.Type == CardType.Attack
                ? card.TargetType == TargetType.AnyEnemy ? "T:ProxyDamage_Atomic_" : "N:ProxyDamage_Atomic_"
                : scope == OperationScope.AbilityRule ? "A:ProxyAtomic_" : "I:ProxyAtomic_";
            if (card.EnergyCost.CostsX) prefix += "EnergyX_";
            // The star-X marker is part of the schema, not display text.  It prevents Stardust's operation
            // from ever being assembled onto a card without a star-X cost.
            if (card.HasStarCostX) prefix += "StarX_";
            var atom = DetailedCharacterDecomposer.Atom(prefix + id, scope, chinese, english,
                card.TargetType == TargetType.AnyEnemy);
            decomposition = new([atom], [-1]);
            return true;
        }

        var damage = DetailedCharacterDecomposer.Value(card, "Damage",
            DetailedCharacterDecomposer.Value(card, "CalculationBase"));
        var block = DetailedCharacterDecomposer.Value(card, "Block");
        var cards = DetailedCharacterDecomposer.Value(card, "Cards");
        var stars = DetailedCharacterDecomposer.Value(card, "Stars");
        var forge = DetailedCharacterDecomposer.Value(card, "Forge");
        var atoms = new List<ComponentAtom>();
        var owners = new List<int>();
        void Add(ComponentAtom atom, int owner = -1) { atoms.Add(atom); owners.Add(owner); }
        ComponentAtom Unique(string suffix, OperationScope scope, string zh, string en, bool target = false,
            CardReferenceRequirement reference = CardReferenceRequirement.None) =>
            DetailedCharacterDecomposer.Atom($"R:{suffix}", scope, zh, en, target, reference);
        void GainStars(int value, int owner = -1) => Add(Unique("GainStars", OperationScope.NonTargeted,
            $"获得{value}颗蓝星", $"Gain {value} Stars"), owner);
        void Forge(int value, int owner = -1) => Add(Unique("Forge", OperationScope.NonTargeted,
            $"铸造{value}", $"Forge {value}"), owner);

        switch (id)
        {
            case "AstralPulse":
                Add(DetailedCharacterDecomposer.AllDamage(damage, 2));
                break;
            case "BeatIntoShape":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("ForEachPriorAttackHitOnTarget", OperationScope.Modifier,
                    "本回合此前每对该敌人造成过一次攻击伤害，",
                    "For each time you dealt Attack damage to this enemy earlier this turn,", true));
                Forge(DetailedCharacterDecomposer.Value(card, "CalculationExtra", 5));
                break;
            case "BigBang":
                Add(DetailedCharacterDecomposer.Draw(cards));
                Add(Unique("GainEnergy", OperationScope.NonTargeted,
                    $"获得{DetailedCharacterDecomposer.Value(card, "Energy")}点能量", $"Gain {DetailedCharacterDecomposer.Value(card, "Energy")} Energy"));
                GainStars(stars);
                Forge(forge);
                break;
            case "Bombardment":
                Add(DetailedCharacterDecomposer.Damage(damage));
                var bombardmentTrigger = atoms.Count;
                Add(Unique("AtTurnStartIfInExhaust", OperationScope.ConditionalTrigger,
                    "在你的回合开始时，若此牌在你的消耗牌堆中", "At the start of your turn, if this card is in your exhaust pile"));
                Add(Unique("PlayThisCard", OperationScope.Independent, "打出此牌", "Play this card"), bombardmentTrigger);
                break;
            case "Bulwark":
                Add(DetailedCharacterDecomposer.Block(block));
                Forge(forge);
                break;
            case "CelestialMight":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("RepeatDamage", OperationScope.Modifier,
                    $"这张牌额外造成{DetailedCharacterDecomposer.Value(card, "Repeat", 2) - 1}次伤害",
                    $"This card deals damage {DetailedCharacterDecomposer.Value(card, "Repeat", 2) - 1} additional times"));
                break;
            case "ChildOfTheStars":
                var starSpentTrigger = atoms.Count;
                Add(DetailedCharacterDecomposer.Atom("A:whenOneStarSpent", OperationScope.AbilityTrigger,
                    "每花费1颗蓝星时", "Whenever you spend 1 Star"));
                Add(DetailedCharacterDecomposer.Block(DetailedCharacterDecomposer.Value(card, "BlockForStars")),
                    starSpentTrigger);
                break;
            case "CloakOfStars": Add(DetailedCharacterDecomposer.Block(block)); break;
            case "CollisionCourse":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("AddDebrisToHand", OperationScope.NonTargeted, "将一张碎屑加入手牌", "Add a Debris to your hand"));
                break;
            case "Comet":
            case "FallingStar":
            case "GammaBlast":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(DetailedCharacterDecomposer.Apply("虚弱", DetailedCharacterDecomposer.Value(card, "WeakPower")));
                Add(DetailedCharacterDecomposer.Apply("易伤", DetailedCharacterDecomposer.Value(card, "VulnerablePower")));
                break;
            case "Conqueror":
                Forge(forge);
                Add(Unique("KingsSwordDoubleDamageThisTurn", OperationScope.SingleEnemyOnly,
                    "本回合君王之剑对该敌人造成双倍伤害", "King's Sword deals double damage to this enemy this turn", true));
                break;
            case "Convergence":
                Add(Unique("RetainHandThisTurn", OperationScope.NonTargeted, "本回合保留你的手牌", "Retain your hand this turn"));
                var convergenceTrigger = atoms.Count;
                Add(Unique("NextTurn", OperationScope.ConditionalTrigger, "下个回合开始时", "At the start of your next turn"));
                Add(Unique("GainEnergy", OperationScope.NonTargeted,
                    $"获得{DetailedCharacterDecomposer.Value(card, "Energy")}点能量", $"Gain {DetailedCharacterDecomposer.Value(card, "Energy")} Energy"), convergenceTrigger);
                GainStars(stars, convergenceTrigger);
                break;
            case "CosmicIndifference":
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("MoveDiscardCardToDrawTop", OperationScope.NonTargeted,
                    "将弃牌堆中的一张牌放到抽牌堆顶", "Put a card from your discard pile on top of your draw pile"));
                break;
            case "CrashLanding":
                Add(DetailedCharacterDecomposer.AllDamage(damage));
                Add(Unique("FillHandWithDebris", OperationScope.NonTargeted,
                    "将碎屑加入手牌，直到手牌已满", "Add Debris to your hand until it is full"));
                break;
            case "CrescentSpear":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("BonusPerStarCostCardInHand", OperationScope.Modifier,
                    $"你的所有牌中每有一张有蓝星耗费的牌，这张牌就额外造成{DetailedCharacterDecomposer.Value(card, "CalculationExtra")}点伤害",
                    $"Deals {DetailedCharacterDecomposer.Value(card, "CalculationExtra")} additional damage for each of your cards with a Star cost"));
                break;
            case "CrushUnder":
                Add(DetailedCharacterDecomposer.AllDamage(damage));
                Add(Unique("EnemiesLoseStrengthThisTurn", OperationScope.NonTargeted,
                    $"本回合所有敌人失去{DetailedCharacterDecomposer.Value(card, "StrengthPower")}点力量",
                    $"All enemies lose {DetailedCharacterDecomposer.Value(card, "StrengthPower")} Strength this turn"));
                break;
            case "DecisionsDecisions":
                Add(DetailedCharacterDecomposer.Draw(cards));
                Add(Unique("PlaySelectedSkillMultipleTimes", OperationScope.NonTargeted,
                    $"选择手牌中的一张技能牌，将其打出{DetailedCharacterDecomposer.Value(card, "Repeat", 2)}次",
                    $"Choose a Skill in your hand. Play it {DetailedCharacterDecomposer.Value(card, "Repeat", 2)} times", false, CardReferenceRequirement.HandCard));
                break;
            case "DefendRegent": Add(DetailedCharacterDecomposer.Block(block)); break;
            case "Devastate": Add(DetailedCharacterDecomposer.Damage(damage)); break;
            case "DyingStar":
                Add(DetailedCharacterDecomposer.AllDamage(damage));
                Add(Unique("EnemiesLoseStrengthThisTurn", OperationScope.NonTargeted,
                    $"本回合所有敌人失去{DetailedCharacterDecomposer.Value(card, "StrengthPower")}点力量",
                    $"All enemies lose {DetailedCharacterDecomposer.Value(card, "StrengthPower")} Strength this turn"));
                break;
            case "GatherLight":
                Add(DetailedCharacterDecomposer.Block(block));
                GainStars(stars);
                break;
            case "Glimmer":
                Add(DetailedCharacterDecomposer.Draw(cards));
                Add(Unique("PutSelectedHandCardsOnDraw", OperationScope.NonTargeted,
                    "将手牌中的牌放到抽牌堆顶", "Put cards from your hand on top of your draw pile", false, CardReferenceRequirement.HandCard));
                break;
            case "Glitterstream":
                Add(DetailedCharacterDecomposer.Block(block));
                var glitterTrigger = atoms.Count;
                Add(Unique("NextTurn", OperationScope.ConditionalTrigger, "下个回合开始时", "At the start of your next turn"));
                Add(DetailedCharacterDecomposer.Block(block), glitterTrigger);
                break;
            case "Glow":
                GainStars(stars);
                Add(DetailedCharacterDecomposer.Draw(cards));
                var glowTrigger = atoms.Count;
                Add(Unique("NextTurn", OperationScope.ConditionalTrigger, "下个回合开始时", "At the start of your next turn"));
                Add(DetailedCharacterDecomposer.Draw(cards), glowTrigger);
                break;
            case "GuidingStar":
                Add(DetailedCharacterDecomposer.Damage(damage));
                var guideTrigger = atoms.Count;
                Add(Unique("NextTurn", OperationScope.ConditionalTrigger, "下个回合开始时", "At the start of your next turn"));
                Add(DetailedCharacterDecomposer.Draw(cards), guideTrigger);
                break;
            case "HeavenlyDrill":
                Add(DetailedCharacterDecomposer.Atom("T:D_EnergyX", OperationScope.SingleEnemyOnly,
                    $"造成{damage}点伤害X次。", $"Deal {damage} damage X times.", true));
                Add(Unique("DoubleEitherXAtThreshold", OperationScope.Modifier,
                    $"如果X至少为{DetailedCharacterDecomposer.Value(card, "Energy", 4)}，则X翻倍。",
                    $"If X is at least {DetailedCharacterDecomposer.Value(card, "Energy", 4)}, double X."));
                break;
            case "Hegemony":
                Add(DetailedCharacterDecomposer.Damage(damage));
                var hegemonyTrigger = atoms.Count;
                Add(Unique("NextTurn", OperationScope.ConditionalTrigger, "下个回合开始时", "At the start of your next turn"));
                Add(Unique("GainEnergy", OperationScope.NonTargeted,
                    $"获得{DetailedCharacterDecomposer.Value(card, "Energy")}点能量", $"Gain {DetailedCharacterDecomposer.Value(card, "Energy")} Energy"), hegemonyTrigger);
                break;
            case "HeirloomHammer":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("CopySelectedColorlessCard", OperationScope.NonTargeted,
                    "选择手牌中的一张无色牌，将它的一张复制加入手牌", "Choose a Colorless card in your hand. Add a copy of it to your hand", false, CardReferenceRequirement.HandCard));
                break;
            case "HiddenCache":
                GainStars(stars);
                var cacheTrigger = atoms.Count;
                Add(Unique("NextTurn", OperationScope.ConditionalTrigger, "下个回合开始时", "At the start of your next turn"));
                GainStars(stars, cacheTrigger);
                break;
            case "IAmInvincible":
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("PlayAtTurnEndIfTopOfDraw", OperationScope.Independent,
                    "回合结束时，若此牌位于抽牌堆顶，则打出此牌", "At the end of your turn, if this is the top card of your draw pile, play it"));
                break;
            case "KinglyKick":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("CostDownWhenDrawn", OperationScope.Independent,
                    "每当抽到此牌时，本场战斗此牌耗能减少1", "Whenever this card is drawn, it costs 1 less this combat"));
                break;
            case "KinglyPunch":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("DamageUpWhenDrawn", OperationScope.Modifier,
                    $"每当抽到此牌时，本场战斗此牌基础伤害增加{DetailedCharacterDecomposer.Value(card, "CalculationExtra")}",
                    $"Whenever this card is drawn, increase its base damage by {DetailedCharacterDecomposer.Value(card, "CalculationExtra")} this combat"));
                break;
            case "KnockoutBlow":
                Add(DetailedCharacterDecomposer.Damage(damage));
                var fatal = atoms.Count;
                Add(Unique("IfFatal", OperationScope.ConditionalTrigger, "斩杀时", "If this kills", true));
                GainStars(stars, fatal);
                break;
            case "KnowThyPlace":
                Add(DetailedCharacterDecomposer.Apply("虚弱", DetailedCharacterDecomposer.Value(card, "WeakPower")));
                Add(DetailedCharacterDecomposer.Apply("易伤", DetailedCharacterDecomposer.Value(card, "VulnerablePower")));
                break;
            case "LunarBlast":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("RepeatPerSkillPlayedThisTurn", OperationScope.Modifier,
                    "本回合每打出过一张技能牌，这张牌就造成一次伤害", "Deal damage once for each Skill played this turn"));
                break;
            case "MakeItSo":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("ReturnAfterSkillsPlayed", OperationScope.Independent,
                    $"每打出{DetailedCharacterDecomposer.Value(card, "Cards", 3)}张技能牌，将此牌从弃牌堆放入手牌",
                    $"Whenever you play {DetailedCharacterDecomposer.Value(card, "Cards", 3)} Skills, return this from your discard pile to your hand"));
                break;
            case "ManifestAuthority":
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("AddRandomColorlessToHand", OperationScope.NonTargeted,
                    "将一张随机无色牌加入手牌", "Add a random Colorless card to your hand"));
                break;
            case "MeteorShower":
                Add(DetailedCharacterDecomposer.AllDamage(damage));
                Add(Unique("ApplyWeakAll", OperationScope.NonTargeted,
                    $"给予所有敌人{DetailedCharacterDecomposer.Value(card, "WeakPower")}层虚弱",
                    $"Apply {DetailedCharacterDecomposer.Value(card, "WeakPower")} Weak to all enemies"));
                Add(Unique("ApplyVulnerableAll", OperationScope.NonTargeted,
                    $"给予所有敌人{DetailedCharacterDecomposer.Value(card, "VulnerablePower")}层易伤",
                    $"Apply {DetailedCharacterDecomposer.Value(card, "VulnerablePower")} Vulnerable to all enemies"));
                break;
            case "Monologue":
                var monologueTrigger = atoms.Count;
                Add(DetailedCharacterDecomposer.Atom("C:untilTurnEnd", OperationScope.ConditionalTrigger,
                    "本回合每当你打出一张牌时", "Whenever you play a card this turn"));
                Add(Unique("GainStrengthThisTurn", OperationScope.NonTargeted,
                    $"本回合获得{DetailedCharacterDecomposer.Value(card, "Power")}点力量",
                    $"Gain {DetailedCharacterDecomposer.Value(card, "Power")} Strength this turn"), monologueTrigger);
                break;
            case "Orbit":
                // Orbit is a reusable persistent trigger plus an ordinary payoff.  Keeping the threshold and
                // Energy gain separate lets the shared balance model price the frequent trigger correctly.
                var orbitTrigger = atoms.Count;
                Add(DetailedCharacterDecomposer.Atom("A:whenEnergySpent", OperationScope.AbilityTrigger,
                    "你每花费4点能量", "Every 4 Energy you spend"));
                Add(Unique("GainEnergy", OperationScope.NonTargeted,
                    $"获得{DetailedCharacterDecomposer.Value(card, "Energy")}点能量",
                    $"Gain {DetailedCharacterDecomposer.Value(card, "Energy")} Energy"), orbitTrigger);
                break;
            case "ParticleWall":
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("ReturnThisToHand", OperationScope.Independent, "将此牌放回手牌", "Return this card to your hand"));
                break;
            case "Patter":
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("GainVigor", OperationScope.NonTargeted,
                    $"获得{DetailedCharacterDecomposer.Value(card, "VigorPower")}点活力",
                    $"Gain {DetailedCharacterDecomposer.Value(card, "VigorPower")} Vigor"));
                break;
            case "PhotonCut":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(DetailedCharacterDecomposer.Draw(cards));
                Add(Unique("PutSelectedHandCardOnDraw", OperationScope.NonTargeted,
                    "将手牌中的一张牌放到抽牌堆顶", "Put a card from your hand on top of your draw pile", false, CardReferenceRequirement.HandCard));
                break;
            case "Prophesize": Add(DetailedCharacterDecomposer.Draw(cards)); break;
            case "Radiate":
                Add(DetailedCharacterDecomposer.AllDamage(damage));
                Add(Unique("RepeatPerStarGainedThisTurn", OperationScope.Modifier,
                    "本回合每获得过一颗蓝星，这张牌就造成一次伤害", "Deal damage once for each Star gained this turn"));
                break;
            case "RefineBlade":
                Forge(forge);
                var refineTrigger = atoms.Count;
                Add(Unique("NextTurn", OperationScope.ConditionalTrigger, "下个回合开始时", "At the start of your next turn"));
                Add(Unique("GainEnergy", OperationScope.NonTargeted,
                    $"获得{DetailedCharacterDecomposer.Value(card, "Energy")}点能量", $"Gain {DetailedCharacterDecomposer.Value(card, "Energy")} Energy"), refineTrigger);
                break;
            case "Reflect":
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("ReflectBlockedDamageThisTurn", OperationScope.NonTargeted,
                    "本回合将被格挡的攻击伤害反弹给攻击者", "This turn, reflect blocked Attack damage to the attacker"));
                break;
            case "Resonance":
                Add(Unique("GainStrength", OperationScope.NonTargeted,
                    $"获得{DetailedCharacterDecomposer.Value(card, "StrengthPower")}点力量",
                    $"Gain {DetailedCharacterDecomposer.Value(card, "StrengthPower")} Strength"));
                Add(Unique("EnemiesLoseStrength", OperationScope.NonTargeted,
                    $"所有敌人失去{DetailedCharacterDecomposer.Value(card, "StrengthPower")}点力量",
                    $"All enemies lose {DetailedCharacterDecomposer.Value(card, "StrengthPower")} Strength"));
                break;
            case "SeekingEdge":
                Add(Unique("KingsSwordHitsAllEnemies", OperationScope.AbilityRule,
                    "君王之剑现在会对所有敌人造成伤害", "King's Sword now deals damage to all enemies"));
                Forge(forge);
                break;
            case "SevenStars":
                Add(DetailedCharacterDecomposer.AllDamage(damage, DetailedCharacterDecomposer.Value(card, "Repeat", 7)));
                break;
            case "ShiningStrike":
                Add(DetailedCharacterDecomposer.Damage(damage));
                GainStars(stars);
                Add(Unique("PutThisOnDraw", OperationScope.Independent,
                    "将此牌放到抽牌堆顶", "Put this card on top of your draw pile"));
                break;
            case "SolarStrike":
                Add(DetailedCharacterDecomposer.Damage(damage));
                GainStars(stars);
                break;
            case "SpoilsOfBattle":
                Forge(forge);
                Add(DetailedCharacterDecomposer.Draw(cards));
                break;
            case "StrikeRegent": Add(DetailedCharacterDecomposer.Damage(damage)); break;
            case "SummonForth":
                Add(Unique("PutKingsSwordInHand", OperationScope.NonTargeted,
                    "将君王之剑放入手牌", "Put King's Sword into your hand"));
                Forge(forge);
                break;
            case "Supermassive":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("BonusPerGeneratedCardThisCombat", OperationScope.Modifier,
                    $"本场战斗每生成过一张牌，这张牌就额外造成{DetailedCharacterDecomposer.Value(card, "CalculationExtra")}点伤害",
                    $"Deals {DetailedCharacterDecomposer.Value(card, "CalculationExtra")} additional damage for each card generated this combat"));
                break;
            case "VoidForm":
                // End-turn is a reusable one-shot payment; the recurring free-card rule is the durable Power.
                // Keep their printed order here. Runtime defers EndTurn until every on-play operation (including
                // applying VoidFormPower) has resolved, matching the native card without keeping an atomic proxy.
                Add(Unique("EndTurn", OperationScope.Independent,
                    "结束你的回合", "End your turn"));
                Add(DetailedCharacterDecomposer.Atom("A:VoidFormFirstCardsFree", OperationScope.AbilityRule,
                    $"你可以免费打出每回合的前{DetailedCharacterDecomposer.Value(card, "Cards", 2)}张牌",
                    $"The first {DetailedCharacterDecomposer.Value(card, "Cards", 2)} cards you play each turn are free to play"));
                break;
            case "WroughtInWar":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Forge(forge);
                break;
            default:
                decomposition = null!;
                return false;
        }

        decomposition = new(atoms, owners);
        return true;
    }
}

internal static class NecrobinderDecomposer
{
    private static readonly HashSet<string> AtomicCards = new(StringComparer.Ordinal)
    {
        "Afterlife", "Bodyguard", "Calcify", "CallOfTheVoid", "Countdown", "DanseMacabre", "Dredge", "Eidolon",
        "Demesne", "DevourLife", "EnfeeblingTouch", "Eradicate", "ForbiddenGrimoire", "Haunt", "Oblivion", "Pagestorm",
        "Poke", "Reanimate", "ReaperForm", "Seance", "SentryMode", "Shroud", "SleightOfFlesh", "Transfigure", "Wisp"
    };

    internal static bool TryDecompose(CardModel card, string chinese, string english, out DetailedDecomposition decomposition)
    {
        var id = card.GetType().Name;
        if (AtomicCards.Contains(id))
        {
            var scope = card.Type == CardType.Power ? OperationScope.AbilityRule
                : card.TargetType == TargetType.AnyEnemy ? OperationScope.SingleEnemyOnly
                : OperationScope.Independent;
            var prefix = card.Type == CardType.Attack
                ? card.TargetType == TargetType.AnyEnemy ? "T:ProxyDamage_Atomic_" : "N:ProxyDamage_Atomic_"
                : scope == OperationScope.AbilityRule ? "A:ProxyAtomic_" : "I:ProxyAtomic_";
            if (card.EnergyCost.CostsX) prefix += "EnergyX_";
            var atom = DetailedCharacterDecomposer.Atom(prefix + id, scope, chinese, english,
                card.TargetType == TargetType.AnyEnemy);
            decomposition = new([atom], [-1]);
            return true;
        }

        var damage = DetailedCharacterDecomposer.Value(card, "Damage");
        var ostyDamage = DetailedCharacterDecomposer.Value(card, "OstyDamage");
        var block = DetailedCharacterDecomposer.Value(card, "Block");
        var cards = DetailedCharacterDecomposer.Value(card, "Cards");
        var atoms = new List<ComponentAtom>();
        var owners = new List<int>();
        void Add(ComponentAtom atom, int owner = -1) { atoms.Add(atom); owners.Add(owner); }
        ComponentAtom Unique(string suffix, OperationScope scope, string zh, string en, bool target = false,
            CardReferenceRequirement reference = CardReferenceRequirement.None) =>
            DetailedCharacterDecomposer.Atom($"NCR:{suffix}", scope, zh, en, target, reference);

        switch (id)
        {
            case "BansheesCry":
                Add(DetailedCharacterDecomposer.AllDamage(damage));
                Add(Unique("CostDownPerVoidPlayed", OperationScope.Modifier,
                    "本场战斗中每打出过一张虚无牌，此牌的耗能就减少1",
                    "Costs 1 less for each Ethereal card played this combat"));
                break;
            case "BlightStrike":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("ApplyDoomEqualDamage", OperationScope.SingleEnemyOnly,
                    "给予等同于所造成伤害的灾厄", "Apply Doom equal to unblocked damage dealt", true));
                break;
            case "BoneShards":
                var ostyAllDamage = DetailedCharacterDecomposer.Value(card, "OstyDamage");
                var ostyAlive = atoms.Count;
                Add(Unique("IfOstyAlive", OperationScope.ConditionalTrigger,
                    "若奥斯提存活", "If Osty is alive"));
                Add(Unique("OstyAllDamage", OperationScope.NonTargeted,
                    $"奥斯提对所有敌人造成{ostyAllDamage}点伤害",
                    $"Osty deals {ostyAllDamage} damage to ALL enemies"), ostyAlive);
                Add(DetailedCharacterDecomposer.Block(block), ostyAlive);
                Add(Unique("KillOsty", OperationScope.NonTargeted, "奥斯提死去", "Osty dies"));
                break;
            case "BorrowedTime":
                var borrowedEnergy = DetailedCharacterDecomposer.Value(card, "Energy");
                Add(Unique("GainEnergy", OperationScope.NonTargeted,
                    $"获得{borrowedEnergy}点能量", $"Gain {borrowedEnergy} Energy"));
                Add(Unique("IncreaseAllCardCostsThisTurn", OperationScope.NonTargeted,
                    "所有牌在本回合耗能增加1", "Cards cost 1 more this turn"));
                break;
            case "CaptureSpirit":
                Add(Unique("TargetHpLoss", OperationScope.SingleEnemyOnly,
                    $"敌人失去{damage}点生命", $"The enemy loses {damage} HP", true));
                Add(Unique("CreateSoulInDraw", OperationScope.NonTargeted,
                    $"将{cards}张灵魂加入抽牌堆", $"Add {cards} Souls to your draw pile"));
                break;
            case "Cleanse":
                var cleanseSummon = DetailedCharacterDecomposer.Value(card, "Summon");
                Add(Unique("Summon", OperationScope.NonTargeted,
                    $"召唤{cleanseSummon}", $"Summon {cleanseSummon}"));
                Add(Unique("ExhaustSelectedDrawCard", OperationScope.NonTargeted,
                    "从抽牌堆中选择一张牌消耗", "Choose a card in your draw pile to Exhaust"));
                break;
            case "DeathMarch":
                Add(DetailedCharacterDecomposer.Damage(damage));
                var drawScale = DetailedCharacterDecomposer.Value(card, "ExtraDamage");
                Add(Unique("DamagePerCardDrawnThisTurn", OperationScope.Modifier,
                    $"本回合每抽一张牌，这张牌额外造成{drawScale}点伤害",
                    $"Deal {drawScale} additional damage for each card drawn this turn"));
                break;
            case "Deathbringer":
                var doomAll = DetailedCharacterDecomposer.Value(card, "DoomPower");
                var weakAll = DetailedCharacterDecomposer.Value(card, "WeakPower");
                Add(Unique("ApplyDoomAll", OperationScope.NonTargeted,
                    $"给予所有敌人{doomAll}层灾厄", $"Apply {doomAll} Doom to ALL enemies"));
                Add(Unique("ApplyWeakAll", OperationScope.NonTargeted,
                    $"给予所有敌人{weakAll}层虚弱", $"Apply {weakAll} Weak to ALL enemies"));
                break;
            case "DeathsDoor":
                Add(DetailedCharacterDecomposer.Block(block));
                var doomGiven = atoms.Count;
                Add(Unique("IfDoomAppliedThisTurn", OperationScope.ConditionalTrigger,
                    "若本回合曾给予灾厄", "If you applied Doom this turn"));
                Add(DetailedCharacterDecomposer.Block(block), doomGiven);
                break;
            case "Debilitate":
                Add(DetailedCharacterDecomposer.Damage(damage));
                var debilitateTurns = DetailedCharacterDecomposer.Value(card, "DebilitatePower");
                Add(Unique("DoubleVulnerableWeak", OperationScope.SingleEnemyOnly,
                    $"在接下来的{debilitateTurns}回合内，该敌人的易伤与虚弱效果翻倍",
                    $"For the next {debilitateTurns} turns, Vulnerable and Weak on that enemy are doubled", true));
                break;
            case "DefendNecrobinder": Add(DetailedCharacterDecomposer.Block(block)); break;
            case "Defile": Add(DetailedCharacterDecomposer.Damage(damage)); break;
            case "Defy":
                Add(DetailedCharacterDecomposer.Block(block));
                Add(DetailedCharacterDecomposer.Apply("虚弱", DetailedCharacterDecomposer.Value(card, "WeakPower")));
                break;
            case "Delay":
                Add(DetailedCharacterDecomposer.Block(block));
                var delayEnergy = DetailedCharacterDecomposer.Value(card, "Energy");
                Add(Unique("NextTurnEnergy", OperationScope.NonTargeted,
                    $"在下个回合获得{delayEnergy}点能量", $"Next turn, gain {delayEnergy} Energy"));
                break;
            case "Dirge":
                Add(Unique("SummonX", OperationScope.NonTargeted, "召唤X", "Summon X"));
                Add(Unique("CreateSoulInDrawX", OperationScope.NonTargeted,
                    "将X张灵魂加入抽牌堆", "Add X Souls to your draw pile"));
                break;
            case "DrainPower":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("UpgradeRandomDiscardCards", OperationScope.NonTargeted,
                    $"随机升级弃牌堆中的{cards}张牌", $"Upgrade {cards} random cards in your discard pile"));
                break;
            case "EndOfDays":
                var endDoom = DetailedCharacterDecomposer.Value(card, "DoomPower");
                Add(Unique("ApplyDoomAll", OperationScope.NonTargeted,
                    $"给予所有敌人{endDoom}层灾厄", $"Apply {endDoom} Doom to ALL enemies"));
                Add(Unique("KillEnemiesAtDoomThreshold", OperationScope.NonTargeted,
                    "杀死灾厄不低于当前生命值的敌人",
                    "Kill enemies whose Doom is at least their current HP"));
                break;
            case "Fear":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(DetailedCharacterDecomposer.Apply("易伤", DetailedCharacterDecomposer.Value(card, "VulnerablePower")));
                break;
            case "Fetch":
                Add(Unique("OstyDamage", OperationScope.SingleEnemyOnly,
                    $"奥斯提造成{ostyDamage}点伤害", $"Osty deals {ostyDamage} damage", true));
                var firstFetch = atoms.Count;
                Add(Unique("IfFirstPlayThisTurn", OperationScope.ConditionalTrigger,
                    "若这是本回合第一次打出此牌", "If this is the first time this card was played this turn"));
                Add(DetailedCharacterDecomposer.Draw(cards), firstFetch);
                break;
            case "Flatten":
                Add(Unique("OstyDamage", OperationScope.SingleEnemyOnly,
                    $"奥斯提造成{ostyDamage}点伤害", $"Osty deals {ostyDamage} damage", true));
                var ostyAttacked = atoms.Count;
                Add(Unique("IfOstyAttackedThisTurn", OperationScope.ConditionalTrigger,
                    "如果奥斯提在本回合攻击过", "If Osty attacked this turn"));
                Add(Unique("SetCostZeroIfOstyAttacked", OperationScope.Independent,
                    "这张牌的耗能变为0", "this card costs 0"), ostyAttacked);
                break;
            case "Friendship":
                var selfStrengthLoss = DetailedCharacterDecomposer.Value(card, "StrengthPower");
                Add(Unique("ApplyPower_FriendshipPower", OperationScope.AbilityRule,
                    "每回合开始时获得1点能量", "At the start of your turn, gain 1 Energy"));
                Add(Unique("LoseStrength", OperationScope.NonTargeted,
                    $"失去{selfStrengthLoss}点力量", $"Lose {selfStrengthLoss} Strength"));
                break;
            case "GraveWarden":
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("CreateSoulInDraw", OperationScope.NonTargeted,
                    "将1张灵魂加入抽牌堆", "Add 1 Soul to your draw pile"));
                break;
            case "Graveblast":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("MoveDiscardCardToHand", OperationScope.NonTargeted,
                    "将弃牌堆中的一张牌放入手牌", "Put a card from your discard pile into your hand"));
                break;
            case "Hang":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("DoubleHangDamage", OperationScope.SingleEnemyOnly,
                    "让所有吊杀牌对该敌人造成的伤害翻倍",
                    "Hang cards deal double damage to that enemy", true));
                break;
            case "HighFive":
                Add(Unique("OstyAllDamage", OperationScope.NonTargeted,
                    $"奥斯提对所有敌人造成{ostyDamage}点伤害",
                    $"Osty deals {ostyDamage} damage to ALL enemies"));
                var highFiveVuln = DetailedCharacterDecomposer.Value(card, "VulnerablePower");
                Add(Unique("ApplyVulnerableAll", OperationScope.NonTargeted,
                    $"给予所有敌人{highFiveVuln}层易伤", $"Apply {highFiveVuln} Vulnerable to ALL enemies"));
                break;
            case "Invoke":
                var invokeSummon = DetailedCharacterDecomposer.Value(card, "Summon");
                var invokeTrigger = atoms.Count;
                Add(Unique("NextTurn", OperationScope.ConditionalTrigger, "在下个回合", "Next turn"));
                Add(Unique("Summon", OperationScope.NonTargeted,
                    $"召唤{invokeSummon}", $"Summon {invokeSummon}"), invokeTrigger);
                var invokeEnergy = DetailedCharacterDecomposer.Value(card, "Energy");
                Add(Unique("GainEnergy", OperationScope.NonTargeted,
                    $"获得{invokeEnergy}点能量", $"Gain {invokeEnergy} Energy"), invokeTrigger);
                break;
            case "Lethality":
                var firstAttack = atoms.Count;
                Add(Unique("FirstAttackPlayedEachTurn", OperationScope.AbilityTrigger,
                    "每回合第一次打出攻击牌时",
                    "Whenever you play the first Attack each turn"));
                Add(DetailedCharacterDecomposer.Atom("M:TriggeredAttackDamagePercent", OperationScope.Modifier,
                    "该攻击牌造成的伤害增加50%", "That Attack deals 50% more damage"), firstAttack);
                break;
            case "Melancholy":
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("CostDownWhenCreatureDies", OperationScope.Independent,
                    "每当有生物死亡时，此牌耗能减少1",
                    "Whenever a creature dies, this card costs 1 less"));
                break;
            case "Misery":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("CopyTargetDebuffsToOthers", OperationScope.SingleEnemyOnly,
                    "给予其他敌人该敌人身上的所有负面效果",
                    "Apply all debuffs on that enemy to all other enemies", true));
                break;
            case "NecroMastery":
                Add(Unique("ApplyPower_NecroMasteryPower", OperationScope.AbilityRule,
                    "每当奥斯提失去生命值时，所有敌人失去等量生命值",
                    "Whenever Osty loses HP, ALL enemies lose that much HP"));
                var masterySummon = DetailedCharacterDecomposer.Value(card, "Summon");
                Add(Unique("Summon", OperationScope.NonTargeted,
                    $"召唤{masterySummon}", $"Summon {masterySummon}"));
                break;
            case "NegativePulse":
                Add(DetailedCharacterDecomposer.Block(block));
                var pulseDoom = DetailedCharacterDecomposer.Value(card, "DoomPower");
                Add(Unique("ApplyDoomAll", OperationScope.NonTargeted,
                    $"给予所有敌人{pulseDoom}层灾厄", $"Apply {pulseDoom} Doom to ALL enemies"));
                break;
            case "Neurosurge":
                var neuroEnergy = DetailedCharacterDecomposer.Value(card, "Energy");
                Add(Unique("ApplyPower_NeurosurgePower", OperationScope.AbilityRule,
                    "每回合开始时给予自身灾厄", "At the start of your turn, apply Doom to yourself"));
                Add(Unique("GainEnergy", OperationScope.NonTargeted,
                    $"获得{neuroEnergy}点能量", $"Gain {neuroEnergy} Energy"));
                Add(DetailedCharacterDecomposer.Draw(cards));
                break;
            case "NoEscape":
                var baseDoom = DetailedCharacterDecomposer.Value(card, "CalculationBase");
                Add(Unique("ApplyDoom", OperationScope.SingleEnemyOnly,
                    $"给予{baseDoom}层灾厄", $"Apply {baseDoom} Doom", true));
                var threshold = DetailedCharacterDecomposer.Value(card, "DoomThreshold");
                var extraDoom = DetailedCharacterDecomposer.Value(card, "CalculationExtra");
                Add(Unique("DoomPerDoomThreshold", OperationScope.Modifier,
                    $"目标每有{threshold}层灾厄，额外给予{extraDoom}层灾厄",
                    $"Apply {extraDoom} additional Doom for each {threshold} Doom on the target", true));
                break;
            case "Parse":
                Add(DetailedCharacterDecomposer.Draw(cards));
                break;
            case "Reap":
                Add(DetailedCharacterDecomposer.Damage(damage));
                break;
            case "Sow":
                Add(DetailedCharacterDecomposer.AllDamage(damage));
                break;
            case "Protector":
                Add(Unique("OstyDamage", OperationScope.SingleEnemyOnly,
                    $"奥斯提造成{ostyDamage}点伤害", $"Osty deals {ostyDamage} damage", true));
                Add(Unique("OstyMaxHpBonusDamage", OperationScope.Modifier,
                    "额外造成等同于奥斯提最大生命值的伤害",
                    "Deal additional damage equal to Osty's Max HP"));
                break;
            case "PullAggro":
                var pullSummon = DetailedCharacterDecomposer.Value(card, "Summon");
                Add(Unique("Summon", OperationScope.NonTargeted,
                    $"召唤{pullSummon}", $"Summon {pullSummon}"));
                Add(DetailedCharacterDecomposer.Block(block));
                break;
            case "PullFromBelow":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("RepeatPerVoidPlayedCombat", OperationScope.Modifier,
                    "本场战斗中每打出过一张虚无牌，此牌就造成一次伤害",
                    "Deal damage once for each Ethereal card played this combat"));
                break;
            case "Putrefy":
                Add(DetailedCharacterDecomposer.Apply("虚弱", DetailedCharacterDecomposer.Value(card, "WeakPower")));
                Add(DetailedCharacterDecomposer.Apply("易伤", DetailedCharacterDecomposer.Value(card, "VulnerablePower")));
                break;
            case "Rattle":
                Add(Unique("OstyDamage", OperationScope.SingleEnemyOnly,
                    $"奥斯提造成{ostyDamage}点伤害", $"Osty deals {ostyDamage} damage", true));
                Add(Unique("RepeatPerOstyAttackThisTurn", OperationScope.Modifier,
                    "本回合每使用过一次奥斯提攻击牌，这张牌就额外造成一次伤害",
                    "Deal damage one additional time for each Osty Attack played this turn"));
                break;
            case "Reave":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("CreateSoulInDraw", OperationScope.NonTargeted,
                    "将1张灵魂加入抽牌堆", "Add 1 Soul to your draw pile"));
                break;
            case "RightHandHand":
                Add(Unique("OstyDamage", OperationScope.SingleEnemyOnly,
                    $"奥斯提造成{ostyDamage}点伤害", $"Osty deals {ostyDamage} damage", true));
                var highCost = DetailedCharacterDecomposer.Value(card, "Energy");
                Add(Unique("ReturnFromDiscardOnHighCostPlay", OperationScope.Independent,
                    $"每当打出耗能至少为{highCost}的牌时，将此牌从弃牌堆放回手牌",
                    $"Whenever you play a card that costs at least {highCost}, return this from your discard pile to your hand"));
                break;
            case "Sacrifice":
                var sacrificeTrigger = atoms.Count;
                Add(Unique("IfOstyAlive", OperationScope.ConditionalTrigger, "若奥斯提存活", "If Osty is alive"));
                Add(Unique("KillOsty", OperationScope.NonTargeted, "奥斯提死去", "Osty dies"), sacrificeTrigger);
                Add(Unique("BlockTripleOstyMaxHp", OperationScope.NonTargeted,
                    "获得等同于奥斯提最大生命值3倍的格挡",
                    "Gain Block equal to 3 times Osty's Max HP"), sacrificeTrigger);
                break;
            case "Scourge":
                var scourgeDoom = DetailedCharacterDecomposer.Value(card, "DoomPower");
                Add(Unique("ApplyDoom", OperationScope.SingleEnemyOnly,
                    $"给予{scourgeDoom}层灾厄", $"Apply {scourgeDoom} Doom", true));
                Add(DetailedCharacterDecomposer.Draw(cards));
                break;
            case "SculptingStrike":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("AddVoidToSelectedHandCard", OperationScope.NonTargeted,
                    "为一张手牌添加虚无", "Add Ethereal to a card in your hand", false, CardReferenceRequirement.HandCard));
                break;
            case "Severance":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("CreateSoulInDraw", OperationScope.NonTargeted,
                    "将1张灵魂加入抽牌堆", "Add 1 Soul to your draw pile"));
                Add(Unique("CreateSoulInHand", OperationScope.NonTargeted,
                    "将1张灵魂加入手牌", "Add 1 Soul to your hand"));
                Add(Unique("CreateSoulInDiscard", OperationScope.NonTargeted,
                    "将1张灵魂加入弃牌堆", "Add 1 Soul to your discard pile"));
                break;
            case "SharedFate":
                var playerLoss = DetailedCharacterDecomposer.Value(card, "PlayerStrengthLoss");
                var enemyLoss = DetailedCharacterDecomposer.Value(card, "EnemyStrengthLoss");
                Add(Unique("LoseStrength", OperationScope.NonTargeted,
                    $"失去{playerLoss}点力量", $"Lose {playerLoss} Strength"));
                Add(Unique("TargetLoseStrength", OperationScope.SingleEnemyOnly,
                    $"敌人失去{enemyLoss}点力量", $"The enemy loses {enemyLoss} Strength", true));
                break;
            case "SicEm":
                Add(Unique("OstyDamage", OperationScope.SingleEnemyOnly,
                    $"奥斯提造成{ostyDamage}点伤害", $"Osty deals {ostyDamage} damage", true));
                Add(Unique("ApplyPower_SicEmPower", OperationScope.SingleEnemyOnly,
                    "本回合中，奥斯提每次命中这名敌人时，召唤3",
                    "Whenever Osty hits this enemy this turn, Summon 3", true));
                break;
            case "Snap":
                Add(Unique("OstyDamage", OperationScope.SingleEnemyOnly,
                    $"奥斯提造成{ostyDamage}点伤害", $"Osty deals {ostyDamage} damage", true));
                Add(Unique("AddRetainToSelectedHandCard", OperationScope.NonTargeted,
                    "给一张手牌添加保留", "Give a card in your hand Retain", false, CardReferenceRequirement.HandCard));
                break;
            case "SoulStorm":
                Add(DetailedCharacterDecomposer.Damage(damage));
                var soulDamage = DetailedCharacterDecomposer.Value(card, "ExtraDamage");
                Add(Unique("DamagePerExhaustedSoul", OperationScope.Modifier,
                    $"消耗牌堆每有一张灵魂，这张牌额外造成{soulDamage}点伤害",
                    $"Deal {soulDamage} additional damage for each Soul in your Exhaust pile"));
                break;
            case "SpiritOfAsh":
                Add(Unique("ApplyPower_SpiritOfAshPower", OperationScope.AbilityRule, chinese, english));
                break;
            case "Spur":
                var spurSummon = DetailedCharacterDecomposer.Value(card, "Summon");
                var ostyHeal = DetailedCharacterDecomposer.Value(card, "Heal");
                Add(Unique("Summon", OperationScope.NonTargeted,
                    $"召唤{spurSummon}", $"Summon {spurSummon}"));
                Add(Unique("HealOsty", OperationScope.NonTargeted,
                    $"奥斯提回复{ostyHeal}点生命", $"Osty heals {ostyHeal} HP"));
                break;
            case "Squeeze":
                Add(Unique("OstyDamage", OperationScope.SingleEnemyOnly,
                    $"奥斯提造成{ostyDamage}点伤害", $"Osty deals {ostyDamage} damage", true));
                var ostyCardScale = DetailedCharacterDecomposer.Value(card, "ExtraDamage");
                Add(Unique("DamagePerOstyAttackCard", OperationScope.Modifier,
                    $"每有一张奥斯提攻击牌，这张牌额外造成{ostyCardScale}点伤害",
                    $"Deal {ostyCardScale} additional damage for each Osty Attack card"));
                break;
            case "StrikeNecrobinder": Add(DetailedCharacterDecomposer.Damage(damage)); break;
            case "TheScythe":
                Add(DetailedCharacterDecomposer.Damage(damage));
                var scytheIncrease = DetailedCharacterDecomposer.Value(card, "Increase");
                Add(Unique("IncreaseThisCardDamageRun", OperationScope.Independent,
                    $"本局游戏中，此牌的基础伤害增加{scytheIncrease}点",
                    $"Increase this card's base damage by {scytheIncrease} for this run"));
                break;
            case "TimesUp":
                Add(Unique("DoomScaledDamage", OperationScope.SingleEnemyOnly,
                    "造成等同于目标灾厄层数的伤害", "Deal damage equal to the target's Doom", true));
                break;
            case "Undeath":
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("CreateCopyInDiscard", OperationScope.NonTargeted,
                    "将此牌的一张复制加入弃牌堆", "Add a copy of this card to your discard pile"));
                break;
            case "Unleash":
                Add(Unique("OstyDamage", OperationScope.SingleEnemyOnly,
                    $"奥斯提造成{ostyDamage}点伤害", $"Osty deals {ostyDamage} damage", true));
                Add(Unique("OstyCurrentHpBonusDamage", OperationScope.Modifier,
                    "额外造成等同于奥斯提当前生命值的伤害",
                    "Deal additional damage equal to Osty's current HP"));
                break;
            case "Veilpiercer":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("NextVoidCostsZero", OperationScope.NonTargeted,
                    "你打出的下一张虚无牌耗能变为0", "The next Ethereal card you play costs 0"));
                break;
            default:
                decomposition = null!;
                return false;
        }
        decomposition = new(atoms, owners);
        return true;
    }
}

internal static class DefectDecomposer
{
    private static readonly HashSet<string> AtomicCards = new(StringComparer.Ordinal)
    {
        // These cards each apply one indivisible rule/effect. No separately resolving damage, Block, draw,
        // generation, or status clause is hidden inside one of these atoms.
        "Buffer", "Capacitor", "Chaos", "Chill", "CreativeAi", "Defragment", "DoubleEnergy", "Dualcast",
        "EchoForm", "Feral", "Fusion", "Hotfix", "Iteration", "Loop", "MachineLearning", "MultiCast",
        "Quadcast", "SignalBoost", "Smokestack", "Spinner", "Storm", "Subroutine", "Supercritical", "Tempest",
        "Thunder", "TrashToTreasure", "Voltaic", "WhiteNoise", "Zap"
    };

    internal static bool TryDecompose(CardModel card, string chinese, string english, out DetailedDecomposition decomposition)
    {
        var id = card.GetType().Name;
        if (AtomicCards.Contains(id))
        {
            var scope = card.Type == CardType.Power ? OperationScope.AbilityRule
                : card.TargetType == TargetType.AnyEnemy ? OperationScope.SingleEnemyOnly
                : OperationScope.Independent;
            var prefix = scope == OperationScope.AbilityRule ? "A:ProxyAtomic_" : "I:ProxyAtomic_";
            var atom = DetailedCharacterDecomposer.Atom(prefix + id, scope, chinese, english,
                card.TargetType == TargetType.AnyEnemy);
            decomposition = new([atom], [-1]);
            return true;
        }

        var damage = DetailedCharacterDecomposer.Value(card, "Damage");
        var block = DetailedCharacterDecomposer.Value(card, "Block");
        var cards = DetailedCharacterDecomposer.Value(card, "Cards");
        var atoms = new List<ComponentAtom>();
        var owners = new List<int>();
        void Add(ComponentAtom atom, int owner = -1) { atoms.Add(atom); owners.Add(owner); }
        ComponentAtom Unique(string suffix, OperationScope scope, string zh, string en, bool target = false,
            CardReferenceRequirement reference = CardReferenceRequirement.None) =>
            DetailedCharacterDecomposer.Atom($"D:{suffix}", scope, zh, en, target, reference);

        switch (id)
        {
            case "AdaptiveStrike":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("CreateZeroCostCopyInDiscard", OperationScope.NonTargeted,
                    "将此牌的一张0费复制品加入弃牌堆", "Add a 0-cost copy of this card to your discard pile"));
                break;
            case "AllForOne":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("ReturnZeroCostDiscardToHand", OperationScope.NonTargeted,
                    "将弃牌堆中所有0费牌放入手牌", "Put all 0-cost cards from your discard pile into your hand"));
                break;
            case "BallLightning":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("ChannelLightning", OperationScope.NonTargeted, "生成1个闪电充能球", "Channel 1 Lightning"));
                break;
            case "Barrage":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("RepeatPerOrb", OperationScope.Modifier,
                    "每有一个充能球，这张牌就造成一次伤害", "Deal damage once for each Orb"));
                break;
            case "BeamCell":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(DetailedCharacterDecomposer.Apply("易伤", DetailedCharacterDecomposer.Value(card, "VulnerablePower")));
                break;
            case "BiasedCognition":
                var focus = DetailedCharacterDecomposer.Value(card, "FocusPower");
                var focusLoss = DetailedCharacterDecomposer.Value(card, "BiasedCognitionPower");
                Add(Unique("ApplyPower_BiasedCognitionPower", OperationScope.AbilityRule,
                    $"每回合开始时，失去{focusLoss}点集中", $"At the start of your turn, lose {focusLoss} Focus"));
                Add(Unique("GainFocus", OperationScope.NonTargeted, $"获得{focus}点集中", $"Gain {focus} Focus"));
                break;
            case "BoostAway":
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("CreateDazedInDiscard", OperationScope.NonTargeted,
                    "将一张晕眩加入弃牌堆", "Add a Dazed to your discard pile"));
                break;
            case "BootSequence":
                Add(DetailedCharacterDecomposer.Block(block));
                break;
            case "BulkUp":
                var orbSlots = DetailedCharacterDecomposer.Value(card, "OrbSlots");
                var strength = DetailedCharacterDecomposer.Value(card, "StrengthPower");
                var dexterity = DetailedCharacterDecomposer.Value(card, "DexterityPower");
                Add(Unique("GainStrength", OperationScope.NonTargeted,
                    $"获得{strength}点力量", $"Gain {strength} Strength"));
                Add(Unique("GainDexterity", OperationScope.NonTargeted,
                    $"获得{dexterity}点敏捷", $"Gain {dexterity} Dexterity"));
                Add(Unique("LoseOrbSlots", OperationScope.NonTargeted,
                    $"失去{orbSlots}个充能球栏位", $"Lose {orbSlots} Orb slots"));
                break;
            case "ChargeBattery":
                var nextEnergy = DetailedCharacterDecomposer.Value(card, "Energy");
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("NextTurnEnergy", OperationScope.NonTargeted,
                    $"在下个回合获得{nextEnergy}点能量", $"Next turn, gain {nextEnergy} Energy"));
                break;
            case "Claw":
                var clawIncrease = DetailedCharacterDecomposer.Value(card, "Increase");
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("IncreaseAllClaws", OperationScope.Independent,
                    $"本场战斗中，所有拥有此效果的牌基础伤害增加{clawIncrease}点",
                    $"Increase the base damage of all cards with this effect by {clawIncrease} this combat"));
                break;
            case "ColdSnap":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("ChannelFrost", OperationScope.NonTargeted, "生成1个冰霜充能球", "Channel 1 Frost"));
                break;
            case "Compact":
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("TransformStatusesToFuel", OperationScope.NonTargeted,
                    "将手牌中的所有状态牌变化为燃料", "Transform all Status cards in your hand into Fuel"));
                break;
            case "CompileDriver":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("DrawPerUniqueOrb", OperationScope.NonTargeted,
                    "每有一种不同的充能球，抽1张牌", "Draw 1 card for each unique Orb"));
                break;
            case "ConsumingShadow":
                var darkCount = DetailedCharacterDecomposer.Value(card, "Repeat");
                Add(Unique("ApplyPower_ConsumingShadowPower", OperationScope.AbilityRule,
                    "在本回合结束时，激发最左侧的充能球", "At the end of this turn, Evoke your leftmost Orb"));
                Add(Unique("ChannelDark", OperationScope.NonTargeted,
                    $"生成{darkCount}个黑暗充能球", $"Channel {darkCount} Dark"));
                break;
            case "Coolant":
                Add(Unique("ApplyPower_CoolantPower", OperationScope.AbilityRule, chinese, english));
                break;
            case "Coolheaded":
                Add(Unique("ChannelFrost", OperationScope.NonTargeted, "生成1个冰霜充能球", "Channel 1 Frost"));
                Add(DetailedCharacterDecomposer.Draw(cards));
                break;
            case "Darkness":
                Add(Unique("ChannelDark", OperationScope.NonTargeted, "生成1个黑暗充能球", "Channel 1 Dark"));
                Add(Unique("TriggerDarkPassives", OperationScope.NonTargeted,
                    "触发所有黑暗充能球的被动", "Trigger the Passive of all Dark Orbs"));
                break;
            case "DefendDefect":
                Add(DetailedCharacterDecomposer.Block(block));
                break;
            case "FightThrough":
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("CreateTwoWoundsInDiscard", OperationScope.NonTargeted,
                    "将2张伤口加入弃牌堆", "Add 2 Wounds to your discard pile"));
                break;
            case "FlakCannon":
                Add(Unique("ExhaustAllStatuses", OperationScope.NonTargeted,
                    "消耗所有状态牌", "Exhaust all Status cards"));
                var flakTrigger = atoms.Count;
                Add(Unique("ForEachExhaustedStatus", OperationScope.ConditionalTrigger,
                    "每消耗一张状态牌", "For each Status Exhausted"));
                Add(new ComponentAtom("N:RandomD", OperationScope.NonTargeted,
                    $"随机对敌人造成{damage}点伤害。", false, CardReferenceRequirement.None), flakTrigger);
                break;
            case "FocusedStrike":
                var temporaryFocus = DetailedCharacterDecomposer.Value(card, "FocusPower");
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("GainTemporaryFocus", OperationScope.NonTargeted,
                    $"本回合获得{temporaryFocus}点集中", $"Gain {temporaryFocus} Focus this turn"));
                break;
            case "Ftl":
                Add(DetailedCharacterDecomposer.Damage(damage));
                var playMax = DetailedCharacterDecomposer.Value(card, "PlayMax");
                var ftlTrigger = atoms.Count;
                Add(Unique("IfCardsPlayedBelow", OperationScope.ConditionalTrigger,
                    $"若本回合打出的牌少于{playMax}张", $"If you have played fewer than {playMax} cards this turn"));
                Add(DetailedCharacterDecomposer.Draw(1), ftlTrigger);
                break;
            case "GeneticAlgorithm":
                var blockIncrease = DetailedCharacterDecomposer.Value(card, "Increase");
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("IncreaseThisCardBlockRun", OperationScope.Independent,
                    $"本局游戏中，此牌的基础格挡增加{blockIncrease}点",
                    $"Increase this card's base Block by {blockIncrease} for this run"));
                break;
            case "Glacier":
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("ChannelFrost", OperationScope.NonTargeted, "生成2个冰霜充能球", "Channel 2 Frost"));
                break;
            case "Glasswork":
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("ChannelGlass", OperationScope.NonTargeted, "生成1个玻璃充能球", "Channel 1 Glass"));
                break;
            case "GoForTheEyes":
                Add(DetailedCharacterDecomposer.Damage(damage));
                var intentTrigger = atoms.Count;
                Add(Unique("IfEnemyIntendsAttack", OperationScope.ConditionalTrigger,
                    "若该敌人的意图是攻击", "If the enemy intends to attack", true));
                Add(DetailedCharacterDecomposer.Apply("虚弱", DetailedCharacterDecomposer.Value(card, "WeakPower")), intentTrigger);
                break;
            case "GunkUp":
                var gunkHits = DetailedCharacterDecomposer.Value(card, "Repeat");
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("RepeatDamage", OperationScope.Modifier,
                    $"这张牌额外造成{gunkHits - 1}次伤害", $"This card deals damage {gunkHits - 1} additional times"));
                Add(Unique("CreateSlimeInDiscard", OperationScope.NonTargeted,
                    "将一张黏液加入弃牌堆", "Add a Slimed to your discard pile"));
                break;
            case "Hailstorm":
                Add(Unique("ApplyPower_HailstormPower", OperationScope.AbilityRule, chinese, english));
                break;
            case "HelixDrill":
                var helixTrigger = atoms.Count;
                Add(Unique("ForEachEnergySpentThisTurn", OperationScope.ConditionalTrigger,
                    "在本回合中，此牌以外每使用了1点能量，",
                    "For every 1 Energy spent this turn except on this card,"));
                Add(DetailedCharacterDecomposer.Damage(5), helixTrigger);
                break;
            case "Hologram":
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("MoveDiscardCardToHand", OperationScope.NonTargeted,
                    "将弃牌堆中的一张牌放入手牌", "Put a card from your discard pile into your hand"));
                break;
            case "Hyperbeam":
                var focusPenalty = DetailedCharacterDecomposer.Value(card, "FocusPower");
                Add(DetailedCharacterDecomposer.AllDamage(damage));
                Add(Unique("LoseTemporaryFocus", OperationScope.NonTargeted,
                    $"本回合失去{focusPenalty}点集中", $"Lose {focusPenalty} Focus this turn"));
                break;
            case "IceLance":
                var frostCount = DetailedCharacterDecomposer.Value(card, "Repeat");
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("ChannelFrost", OperationScope.NonTargeted,
                    $"生成{frostCount}个冰霜充能球", $"Channel {frostCount} Frost"));
                break;
            case "Leap":
                Add(DetailedCharacterDecomposer.Block(block));
                break;
            case "LightningRod":
                var lightningTurns = DetailedCharacterDecomposer.Value(card, "LightningRodPower");
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("ApplyPower_LightningRodPower", OperationScope.Independent,
                    $"在接下来的{lightningTurns}个回合开始时，生成1个闪电充能球",
                    $"At the start of each of your next {lightningTurns} turns, Channel 1 Lightning"));
                break;
            case "MeteorStrike":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("ChannelPlasma", OperationScope.NonTargeted, "生成3个等离子充能球", "Channel 3 Plasma"));
                break;
            case "Modded":
                var gainedSlots = DetailedCharacterDecomposer.Value(card, "Repeat");
                Add(Unique("GainOrbSlots", OperationScope.NonTargeted,
                    $"获得{gainedSlots}个充能球栏位", $"Gain {gainedSlots} Orb slots"));
                Add(DetailedCharacterDecomposer.Draw(cards));
                Add(Unique("IncreaseThisCardCost", OperationScope.Independent,
                    "这张牌的耗能增加1", "Increase this card's cost by 1"));
                break;
            case "MomentumStrike":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("SetThisCardCostZero", OperationScope.Independent,
                    "这张牌的耗能降为0", "Set this card's cost to 0"));
                break;
            case "Null":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(DetailedCharacterDecomposer.Apply("虚弱", DetailedCharacterDecomposer.Value(card, "WeakPower")));
                Add(Unique("ChannelDark", OperationScope.NonTargeted, "生成1个黑暗充能球", "Channel 1 Dark"));
                break;
            case "Overclock":
                Add(DetailedCharacterDecomposer.Draw(cards));
                Add(Unique("CreateBurnInDiscard", OperationScope.NonTargeted,
                    "将一张灼伤加入弃牌堆", "Add a Burn to your discard pile"));
                break;
            case "Rainbow":
                Add(Unique("ChannelLightning", OperationScope.NonTargeted, "生成1个闪电充能球", "Channel 1 Lightning"));
                Add(Unique("ChannelFrost", OperationScope.NonTargeted, "生成1个冰霜充能球", "Channel 1 Frost"));
                Add(Unique("ChannelDark", OperationScope.NonTargeted, "生成1个黑暗充能球", "Channel 1 Dark"));
                break;
            case "Reboot":
                Add(Unique("ShuffleAllUnexhaustedIntoDraw", OperationScope.NonTargeted,
                    "将所有未消耗的牌洗回抽牌堆", "Shuffle all unexhausted cards into your draw pile"));
                Add(DetailedCharacterDecomposer.Draw(cards));
                break;
            case "Refract":
                var refractOrbs = DetailedCharacterDecomposer.Value(card, "Repeat");
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("RepeatDamage", OperationScope.Modifier,
                    "这张牌额外造成1次伤害", "This card deals damage 1 additional time"));
                Add(Unique("ChannelGlass", OperationScope.NonTargeted,
                    $"生成{refractOrbs}个玻璃充能球", $"Channel {refractOrbs} Glass"));
                break;
            case "RocketPunch":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(DetailedCharacterDecomposer.Draw(cards));
                Add(Unique("CostDownWhenStatusGenerated", OperationScope.Independent,
                    "每当你生成状态牌时，此牌在下一次打出前耗能减少1",
                    "Whenever you generate a Status, this card costs 1 less until played"));
                break;
            case "Scavenge":
                Add(Unique("ExhaustSelectedHandCard", OperationScope.NonTargeted,
                    "消耗手牌中的一张牌", "Exhaust a card in your hand", false, CardReferenceRequirement.HandCard));
                var scavengeEnergy = DetailedCharacterDecomposer.Value(card, "Energy");
                Add(Unique("NextTurnEnergy", OperationScope.NonTargeted,
                    $"在下个回合获得{scavengeEnergy}点能量", $"Next turn, gain {scavengeEnergy} Energy"));
                break;
            case "Scrape":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("DrawAndDiscardNonZero", OperationScope.NonTargeted,
                    $"抽{cards}张牌。丢弃其中耗能不为0的牌",
                    $"Draw {cards} cards. Discard those that do not cost 0"));
                break;
            case "ShadowShield":
                Add(DetailedCharacterDecomposer.Block(block));
                Add(Unique("ChannelDark", OperationScope.NonTargeted, "生成1个黑暗充能球", "Channel 1 Dark"));
                break;
            case "Shatter":
                Add(DetailedCharacterDecomposer.AllDamage(damage));
                Add(Unique("EvokeAllTwice", OperationScope.NonTargeted,
                    "激发所有充能球2次", "Evoke all Orbs twice"));
                break;
            case "Skim":
                Add(DetailedCharacterDecomposer.Draw(cards));
                break;
            case "StrikeDefect":
                Add(DetailedCharacterDecomposer.Damage(damage));
                break;
            case "Sunder":
                Add(DetailedCharacterDecomposer.Damage(damage));
                var fatalTrigger = atoms.Count;
                Add(Unique("IfFatal", OperationScope.ConditionalTrigger, "斩杀时", "If this kills", true));
                var sunderEnergy = DetailedCharacterDecomposer.Value(card, "Energy");
                Add(Unique("GainEnergy", OperationScope.NonTargeted,
                    $"获得{sunderEnergy}点能量", $"Gain {sunderEnergy} Energy"), fatalTrigger);
                break;
            case "SweepingBeam":
                Add(DetailedCharacterDecomposer.AllDamage(damage));
                Add(DetailedCharacterDecomposer.Draw(cards));
                break;
            case "Synchronize":
                var focusPerOrb = DetailedCharacterDecomposer.Value(card, "CalculationExtra");
                Add(Unique("GainTemporaryFocusPerUniqueOrb", OperationScope.NonTargeted,
                    $"本回合每有一种不同的充能球，获得{focusPerOrb}点集中",
                    $"Gain {focusPerOrb} Focus this turn for each unique Orb"));
                break;
            case "Synthesis":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("NextPowerCostsZero", OperationScope.NonTargeted,
                    "下一张能力牌耗能变为0", "Your next Power costs 0"));
                break;
            case "TeslaCoil":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("TriggerLightningPassivesAtTarget", OperationScope.SingleEnemyOnly,
                    "对该敌人触发所有闪电充能球的被动", "Trigger the Passive of all Lightning Orbs on that enemy", true));
                break;
            case "Turbo":
                var turboEnergy = DetailedCharacterDecomposer.Value(card, "Energy");
                Add(Unique("GainEnergy", OperationScope.NonTargeted,
                    $"获得{turboEnergy}点能量", $"Gain {turboEnergy} Energy"));
                Add(Unique("CreateVoidInDiscard", OperationScope.NonTargeted,
                    "将一张虚空加入弃牌堆", "Add a Void to your discard pile"));
                break;
            case "Uproar":
                Add(DetailedCharacterDecomposer.Damage(damage));
                Add(Unique("RepeatDamage", OperationScope.Modifier,
                    "这张牌额外造成1次伤害", "This card deals damage 1 additional time"));
                Add(Unique("AutoPlayRandomAttackFromDraw", OperationScope.NonTargeted,
                    "随机打出抽牌堆中的1张攻击牌", "Play a random Attack from your draw pile"));
                break;
            default:
                decomposition = null!;
                return false;
        }

        decomposition = new(atoms, owners);
        return true;
    }
}
