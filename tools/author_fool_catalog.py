"""Materialize the manually reviewed v114 Fool decomposition (never infer component boundaries)."""
from __future__ import annotations
import json, re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT.parent / "proj/114/MegaCrit.Sts2.Core.Models.Cards"
LOCALES = ROOT.parent / "export/114/localization"
DEFINITIONS: dict[str, dict] = {}


def define(key, zh, en, *, scope="NonTargeted", opcode="fool_action", variant=None,
           slots=(), target="self", flags=(), condition=None, trigger=None, text_slots=(),
           source_zone="none", destination_zone="none", card_filter="any"):
    DEFINITIONS[key] = dict(template="F:" + key, chineseTemplate=zh, englishTemplate=en, scope=scope,
        opcode=opcode, variant=variant or key.lower(), target=target, flags=list(flags),
        values=[dict(id=s[0], baseValue=s[1], source=s[2] if len(s)>2 else "fixed",
                     upgradable=s[3] if len(s)>3 else True, explicit=s[4] if len(s)>4 else True,
                     offset=0) for s in slots], condition=condition, trigger=trigger,
        textSlots=[dict(id=s[0],chineseValue=s[1],englishValue=s[2]) for s in text_slots],
        sourceZone=source_zone,destinationZone=destination_zone,cardFilter=card_filter)


def trigger(key, zh, en, kind, *, persistent=True):
    define(key, zh, en, scope="AbilityTrigger" if persistent else "ConditionalTrigger", opcode="trigger",
           variant="event", trigger=dict(kind=kind, lifetime="combat" if persistent else "immediate"),
           flags=("repeated_or_multiplicative",))


def condition(key, zh, en, kind, *, enemy=False):
    define(key, zh, en, scope="ConditionalTrigger", opcode="condition", variant=kind,
           target="selected_enemy" if enemy else "self",
           condition=dict(kind=kind, subject="target" if enemy else "self"),
           flags=("requires_selected_enemy",) if enemy else ())


define("Aim", "获得[[amount]]点瞄准。", "Gain [[amount]] Aim.", opcode="apply_power", variant="aim", slots=(("amount",5),))
define("Stretch", "获得[[amount]]点拉伸。", "Gain [[amount]] Stretch.", opcode="apply_power", variant="stretch", slots=(("amount",7),))
define("TemporaryThorns", "在本回合获得[[amount]]点荆棘。", "Gain [[amount]] Thorns this turn.", opcode="apply_power", variant="thorns_this_turn", slots=(("amount",3),))
define("ShootArrow", "发射一支箭矢，", "Shoot an Arrow to", scope="Modifier", opcode="modify_damage",
       variant="shoot_arrow", flags=("requires_damage_anchor","grants_shoot_tag","sole_damage_sentence"))
define("StretchFromDamage", "获得等同于所造成伤害量的拉伸。", "Gain Stretch equal to the damage dealt.", flags=("requires_last_damage",))
condition("IfArrow", "如果你拥有箭矢，", "If you have an Arrow,", "arrow_available")
condition("IfImpaled", "如果目标敌人处于扎穿状态，", "If the target enemy is Impaled,", "target_impaled", enemy=True)
condition("IfOwnImpaled", "如果目标敌人被你的箭矢扎穿，", "If the target enemy is Impaled by your Arrow,", "target_own_arrow", enemy=True)
condition("IfShotSucceeded", "如果这张牌发射了箭矢，", "If this card shot an Arrow,", "this_card_shot_arrow")
for key, zh, en, kind in [
    ("IfWahKi","如果你拥有哇旋律或叽旋律，","If you have the Wah or Ki Melody,","melody_wah_or_ki"),
    ("IfWahCho","如果你拥有哇旋律或啾旋律，","If you have the Wah or Cho Melody,","melody_wah_or_cho"),
    ("IfChoKi","如果你拥有啾旋律或叽旋律，","If you have the Cho or Ki Melody,","melody_cho_or_ki"),
    ("IfKi","如果你拥有叽旋律，","If you have the Ki Melody,","melody_ki")]:
    condition(key, zh, en, kind)
trigger("ForEachMelody", "你每拥有一种旋律，", "For each Melody you have,", "melody_types", persistent=False)
define("ForEachEnergyX", "重复[[count]]次，", "Repeat [[count]] times,", scope="ConditionalTrigger", opcode="trigger",
       variant="event", slots=(("count",0,"energy_x"),), flags=("repeated_or_multiplicative",),
       trigger=dict(kind="energy_x_count",lifetime="immediate"))
trigger("ForEachSadnessExhausted", "每消耗一张悲伤，", "For each Sadness Exhausted,", "sadness_exhausted_by_this_card", persistent=False)
for key, zh, en, kind in [
    ("OnSadnessGenerated","每当你生成一张悲伤时，","Whenever you create a Sadness,","sadness_generated"),
    ("OnMelodyGained","每当你获得一种旋律时，","Whenever you gain a Melody,","melody_gained"),
    ("OnWahGained","每当你获得哇旋律时，","Whenever you gain the Wah Melody,","wah_melody_gained"),
    ("OnChoGained","每当你获得啾旋律时，","Whenever you gain the Cho Melody,","cho_melody_gained"),
    ("OnKiGained","每当你获得叽旋律时，","Whenever you gain the Ki Melody,","ki_melody_gained"),
    ("OnAllMelodies","每当你集齐三种旋律时，","Whenever you gain all three Melodies,","all_melodies_gained"),
    ("OnArrowImpaled","每当你的箭矢扎穿一名敌人时，","Whenever your Arrow Impales an enemy,","arrow_impaled"),
    ("OnFirstArrowDislodged","每回合第一次让你的箭矢松脱时，","The first time your Arrow is Dislodged each turn,","first_arrow_dislodged_each_turn")]:
    trigger(key, zh, en, kind)
trigger("WhenThisDislodgesArrow", "如果这张牌让你的箭矢松脱，", "If this card Dislodges your Arrow,", "this_card_dislodges_arrow", persistent=False)
trigger("WhileDiscardArrowShot", "每当你发射一支箭矢时，如果这张牌在你的弃牌堆中，", "Whenever you Shoot an Arrow, if this card is in your discard pile,", "arrow_shot_if_self_in_discard", persistent=False)
define("CreateSadness", "将[[amount]]张悲伤加入你的手牌。", "Add [[amount]] Sadness to your Hand.", slots=(("amount",1),), flags=("api_negative","status_reference","count_unit_reference"))
define("CreateJoy", "将[[amount]]张喜悦加入你的手牌。", "Add [[amount]] Joy to your Hand.", slots=(("amount",1),), flags=("api_beneficial","count_unit_reference"))
define("TransformSadnessToJoy", "将你手牌中的随机[[amount]]张悲伤变化为喜悦。", "Transform [[amount]] random Sadness in your Hand into Joy.", slots=(("amount",1),), flags=("requires_hand_cards","api_beneficial","count_unit_reference"))
define("ExhaustSadness", "消耗你手牌中的所有悲伤。", "Exhaust all Sadness in your Hand.", flags=("api_beneficial","requires_hand_cards"))
define("RefundStretch", "重新获得被这张牌的格挡效果消耗的拉伸。", "Regain the Stretch consumed by this card's Block effects.", scope="Modifier", flags=("requires_block_anchor",))
define("AimMultiplier", "瞄准对这张牌有[[amount]]倍效果。", "Aim has [[amount]] times its effect on this card.", scope="Modifier", slots=(("amount",2),), flags=("requires_arrow_damage",))
define("DoubleAgainstImpaled", "对处于扎穿状态的敌人造成双倍伤害。", "Deal double damage to Impaled enemies.", scope="Modifier", flags=("requires_damage_anchor",))
define("ExtraHitsPerMelody", "你每拥有一种旋律，这张牌就额外造成[[hits]]次伤害。", "Deal damage [[hits]] additional times for each Melody you have.", scope="Modifier", slots=(("hits",1),), flags=("requires_damage_anchor",))
define("DamagePerSadnessCreated", "本场战斗中，你每生成过一张悲伤，这张牌就额外造成[[amount]]点伤害。", "Deals [[amount]] additional damage for each Sadness you have created this combat.", scope="Modifier", slots=(("amount",6),), flags=("requires_damage_anchor",))
define("DamagePerMelodyCard", "你每有一张名字中含有“哇”、“啾”或“叽”的牌，这张牌就额外造成[[amount]]点伤害。", "Deals [[amount]] additional damage for ALL your cards containing Wah, Cho, or Ki.", scope="Modifier", slots=(("amount",4),), flags=("requires_damage_anchor",))
define("CostPerMelody", "你每拥有一种旋律，这张牌的耗能减少[[amount]][E]。", "Costs [[amount]][E] less for each Melody you have.", scope="Independent", slots=(("amount",2),), flags=("self_cost_reduction",))
define("CostIfWahKi", "如果你拥有哇旋律或叽旋律，这张牌的耗能减少[[amount]][E]。", "Costs [[amount]][E] less if you have the Wah or Ki Melody.", scope="Independent", slots=(("amount",2),), flags=("self_cost_reduction",))
define("TransformThisToSadness", "将这张牌变化为悲伤并返回你的手牌。", "Transform this card into Sadness and return it to your Hand.", scope="Independent", flags=("api_negative","api_self_card_movement","single_use_transformation"))
define("LookChooseDiscard", "查看你抽牌堆顶的[[amount]]张牌，选择1张放入你的手牌，丢弃其余牌。", "Look at the top [[amount]] cards of your Draw Pile. Choose 1 to add to your Hand and discard the rest.", slots=(("amount",3),), flags=("requires_player_choice","count_unit_reference"))
define("RandomShootFree", "将[[amount]]张随机发射牌加入你的手牌。该牌在本回合内可以免费打出。", "Add [[amount]] random Shoot cards to your Hand. They are free to play this turn.", slots=(("amount",1),), flags=("count_unit_reference",))
define("DiscardShootToHand", "将你弃牌堆中的[[amount]]张发射牌放入你的手牌。", "Put [[amount]] Shoot cards from your discard pile into your Hand.", slots=(("amount",1),), flags=("requires_player_choice","count_unit_reference"))
define("DrawShootToHand", "将你抽牌堆中的[[amount]]张发射牌放入你的手牌。", "Put [[amount]] Shoot cards from your Draw Pile into your Hand.", slots=(("amount",1),), flags=("requires_player_choice","count_unit_reference"))
define("DiscardToDrawTop", "选择你弃牌堆中的[[amount]]张牌，放到你的抽牌堆顶。", "Put [[amount]] cards from your discard pile on top of your Draw Pile.", slots=(("amount",1),), flags=("requires_player_choice","count_unit_reference"))
define("Dislodge", "使你扎穿目标敌人的所有箭矢松脱。", "Dislodge all your Arrows from the target enemy.", scope="SingleEnemyOnly", target="selected_enemy", flags=("requires_single_target",))
define("OnlyCardsThisTurn", "你在本回合只能再打出[[amount]]张牌。", "You can only play [[amount]] more cards this turn.", opcode="apply_power", variant="encore", slots=(("amount",1),), flags=("api_negative",))
define("ArrowSlots", "额外获得[[amount]]支箭矢。", "Gain [[amount]] additional Arrows.", slots=(("amount",1),), flags=("api_power_foundation",))
define("MelodyReplay", "本回合你打出的下[[amount]]张“哇”、“啾”或“叽”牌会被额外打出一次。", "The next [[amount]] Wah, Cho, or Ki cards you play this turn are played an additional time.", opcode="apply_power", variant="ki_verse", slots=(("amount",1),))
define("Quietus", "本回合，你的下一次射击造成[[amount]]倍伤害，但箭矢会被摧毁。", "Your next Shot this turn deals [[amount]] times its damage, but the Arrow is destroyed.", opcode="apply_power", variant="quietus", slots=(("amount",2),))
define("Honing", "箭矢造成[[amount]]%额外伤害。", "Arrows deal [[amount]]% more damage.", opcode="apply_power", variant="honing", slots=(("amount",50),), flags=("api_power_foundation","percentage_value"))
define("Serrated", "扎穿的敌人从你的攻击中受到的伤害增加[[amount]]%。", "Impaled enemies take [[amount]]% more damage from your Attacks.", opcode="apply_power", variant="serrated_arrows", slots=(("amount",25),), flags=("api_power_foundation","percentage_value"))
define("ShotVulnerable", "这名敌人在本回合从射击中额外受到[[amount]]%伤害。", "The target enemy takes [[amount]]% more damage from Shots this turn.", scope="SingleEnemyOnly", target="selected_enemy", opcode="apply_power", variant="dislike_you", slots=(("amount",50),), flags=("requires_single_target","percentage_value"))
define("PlayMelodyCards", "打出你手牌中的所有[[melodies]]牌。", "Play all [[melodies]] cards in your Hand.",
       slots=(("melody_mask",5,"fixed",False,False),), flags=("requires_hand_cards","melody_subset_slot"),
       text_slots=(("melodies","“哇”和“叽”","Wah and Ki"),), source_zone="hand",card_filter="melody_5")
define("RandomTarot", "将[[amount]]张随机塔罗牌加入你的手牌。", "Add [[amount]] random Tarot cards to your Hand.", slots=(("amount",1),), flags=("count_unit_reference",))
define("PlayCheapest", "打出你手牌中耗能最低的[[amount]]张牌。", "Play the [[amount]] lowest-cost cards in your Hand.", slots=(("amount",1),), flags=("requires_hand_cards","count_unit_reference"))
define("NextCardFree", "你接下来打出的[[amount]]张牌耗能为0[E]。", "The next [[amount]] cards you play cost 0[E].", opcode="apply_power", variant="free_card", slots=(("amount",1),))

define("UpgradeRandomHand", "升级手牌中的[[amount]]张随机牌。", "Upgrade [[amount]] random cards in your Hand.",
       slots=(("amount",1),), flags=("count_unit_reference",))
define("TransformHandToTheShot", "将手牌中的[[amount]]张牌变化为[[shot]]。", "Transform [[amount]] cards in your Hand into [[shot]].",
       slots=(("amount",1),), flags=("requires_player_choice","count_unit_reference"), text_slots=(("shot","全力射击","The Shot"),))
define("TransformSadnessToCopy", "将手牌中的[[amount]]张随机悲伤变化为这张牌的复制品。", "Transform [[amount]] random Sadness in your Hand into copies of this card.",
       slots=(("amount",1),), flags=("count_unit_reference",))
define("DislodgeAll", "使目标敌人身上所有属于你的箭矢松脱。", "Dislodge ALL of your Arrows from the target enemy.",
       scope="SingleEnemyOnly", target="selected_enemy", flags=("requires_selected_enemy",))
define("SplashDamage", "对所有其他敌人造成等量于上次造成伤害的伤害。", "Deal damage equal to the last damage dealt to all other enemies.", flags=("requires_last_damage","api_enemy_damage"))
define("ExhaustShoot", "消耗本场战斗中所有尚未消耗的发射牌。", "Exhaust all of your non-Exhausted Shoot cards this combat.", flags=("api_negative",))
define("CreateTheShot", "将一张[[shot]]加入手牌。其基础伤害为刚才消耗的牌数量的[[amount]]倍。", "Add [[shot]] to your Hand. Its base damage is [[amount]] times the number of cards just Exhausted.", slots=(("amount",10),), flags=("requires_exhaust_result",), text_slots=(("shot","全力射击","The Shot"),))
define("UntilNonMinionDies", "在本回合中，下次有非爪牙敌人死亡时，", "The next time a non-Minion enemy dies this turn,",
       scope="ConditionalTrigger", opcode="trigger",variant="event",
       trigger=dict(kind="next_non_minion_enemy_death_this_turn",lifetime="this_turn_once"),
       flags=("this_turn_reference","one_shot","enemy_death_reference"))
define("IncreaseAimRun", "这张牌的瞄准永久增加[[amount]]点。", "Permanently increase this card's Aim by [[amount]].",
       slots=(("amount",2),), flags=("requires_aim_anchor","permanent_growth","api_restricted"))


def f(key, *values, owner=-1):
    d = DEFINITIONS[key]
    return (d["template"], values, owner)


def g(template, zh, en, scope="NonTargeted", owner=-1):
    return (template, zh, en, scope, owner)


def damage(n, hits=1, owner=-1):
    return g("T:D_EnergyX" if hits == "X" else "T:D", f"造成{n}点伤害" + ("。" if hits == 1 else f"{hits}次。"),
             f"Deal {n} damage" + ("." if hits == 1 else f" {hits} times."), "SingleEnemyOnly", owner)


def shoot(n, hits=1): return [f("ShootArrow"),damage(n,hits)]


def block(n, owner=-1): return g("N:B", f"获得{n}点格挡。", f"Gain {n} Block.", owner=owner)
def draw(n, owner=-1): return g("N:Draw", f"抽{n}张牌。", f"Draw {n} card" + ("." if n == 1 else "s."), owner=owner)
def energy(n, owner=-1): return g("N:E", f"获得{n}点能量。", f"Gain {n} Energy.", owner=owner)
def start(): return g("A:turnStart", "在你的回合开始时，", "At the start of your turn,", "AbilityTrigger")
def end(): return g("A:turnEnd", "在你的回合结束时，", "At the end of your turn,", "AbilityTrigger")
def nextturn(): return g("C:NextTurnStart", "在你的下个回合开始时，", "At the start of your next turn,", "ConditionalTrigger")
def vulnerable(n,owner=-1): return g("T:Apply",f"给予{n}层易伤。",f"Apply {n} Vulnerable.","SingleEnemyOnly",owner)
def weak(n,owner=-1): return g("T:Apply",f"给予{n}层虚弱。",f"Apply {n} Weak.","SingleEnemyOnly",owner)
def all_damage(n,owner=-1): return g("N:AllD",f"对所有敌人造成{n}点伤害。",f"Deal {n} damage to ALL enemies.",owner=owner)
def return_hand(owner=-1): return g("R:ReturnThisToHand","将这张牌放回你的手牌。","Return this card to your Hand.",owner=owner)


# Every boundary below was reviewed against the native OnPlay/hooks. Native numeric and keyword upgrades are
# independently reconstructed from explicit variable bindings by build_native_reference_catalog.py.
RECIPES = {
 "AllIsWell":[f("OnSadnessGenerated"),block(4,0)],
 "Anguish":[damage(6),f("DamagePerSadnessCreated",6)],
 "Archery":[start(),f("Aim",6,owner=0)],
 "ArrowStab":[damage(13),f("Aim",6)],
 "BlossomShot":[*shoot(11),f("IfShotSucceeded"),f("StretchFromDamage",owner=2)],
 "BlotOutTheSky":[f("RandomShootFree",1)],
 "Bounce":[f("Stretch",8),nextturn(),return_hand(1)],
 "BowSmack":[damage(8),f("IfImpaled"),energy(1,1)],
 "Brace":[f("ForEachEnergyX","X"),f("Aim",8,owner=0)],
 "BrightenUp":[f("CreateSadness",2),f("CreateJoy",2)],
 "ChoBarbs":[block(8),f("TemporaryThorns",2)],
 "ChoChorus":[block(14),f("PlayMelodyCards",5)],
 "ChoFirm":[block(5),f("IfArrow"),block(5,1)],
 "ChoKi":[block(8),f("RefundStretch")],
 "ChoLimbs":[block(5),block(5),f("CostIfWahKi",2)],
 "ChoReady":[block(7),f("Aim",3)],
 "Coda":[damage(40),f("CostPerMelody",2)],
 "DeepStretch":[f("Stretch",7),draw(1)],
 "DefendFool":[block(5)],
 "DislikeYou":[damage(8),f("ShotVulnerable",50)],
 "DivineShot":[*shoot(40)],
 "Ecstasy":[start(),f("TransformSadnessToJoy",1,owner=0)],
 "Encore":[energy(1),draw(2),f("OnlyCardsThisTurn",1)],
 "Ennui":[start(),f("CreateSadness",1,owner=0),draw(2,0)],
 "EpicRetrieval":[damage(15),f("WhenThisDislodgesArrow"),energy(3,1)],
 "ExplosiveArrows":[f("OnArrowImpaled"),all_damage(8,0)],
 "Florescence":[f("TransformHandToTheShot",1)],
 "Growth":[block(8),f("TransformSadnessToCopy",1)],
 "HarmonicShield":[block(4),f("ForEachMelody"),block(4,1)],
 "HeavyShot":[*shoot(16),f("AimMultiplier",3)],
 "HiddenRoots":[start(),f("Stretch",3,owner=0)],
 "HobblingShot":[*shoot(13),f("IfShotSucceeded"),weak(1,2),vulnerable(1,2)],
 "Honing":[f("Honing",100)],
 "Jest":[block(15),f("CreateSadness",1)],
 "KiChant":[f("LookChooseDiscard",3)],
 "KiHop":[damage(7),f("IfImpaled"),draw(2,1)],
 "KiRest":[nextturn(),energy(4,0),f("IfWahCho"),g("N:RetainHandThisTurn","本回合保留你的手牌。","Retain your Hand this turn.",owner=2)],
 "KiVerse":[f("MelodyReplay",1)],
 "KiWhip":[damage(5),nextturn(),energy(2,1)],
 "LockedIn":[f("DrawShootToHand",1)],
 "FoolishShot":[*shoot(15)],
 "LostLute":[f("OnMelodyGained"),g("CL:PlayTopDrawCard","打出你抽牌堆顶的牌。","Play the top card of your Draw Pile.",owner=0)],
 "Lullaby":[f("OnAllMelodies"),block(12,0)],
 "MelodicShot":[*shoot(12),f("ExtraHitsPerMelody",1)],
 "Motif":[f("OnMelodyGained"),draw(1,0)],
 "MourningBlow":[damage(10),f("TransformThisToSadness")],
 "MythicForm":[start(),f("RandomTarot",1,owner=0)],
 "Nail":[f("IfImpaled"),damage(20,owner=0),weak(2,0),vulnerable(2,0)],
 "Nock":[f("Aim",5)],
 "Optimism":[f("ExhaustSadness"),f("ForEachSadnessExhausted"),draw(1,1)],
 "PunchOfJoy":[damage(14),f("TransformSadnessToJoy",1)],
 "QuickShot":[*shoot(10)],
 "Quietus":[f("Quietus",2)],
 "Quiver":[f("ArrowSlots",1)],
 "RainShot":[*shoot(12),f("IfShotSucceeded"),f("SplashDamage",owner=2)],
 "Recall":[f("DislodgeAll")],
 "RepeatShot":[*shoot(13,"X")],
 "RhythmStrike":[damage(4),f("ExtraHitsPerMelody",1)],
 "Rummage":[draw(3),f("CreateSadness",1)],
 "SerratedArrows":[f("Serrated",50)],
 "ShowOff":[f("Aim",5),f("UntilNonMinionDies"),f("IncreaseAimRun",2,owner=1)],
 "Simpleton":[end(),f("PlayCheapest",1,owner=0)],
 "SkyShot":[*shoot(30)],
 "SongPath":[f("OnChoGained"),block(4,0)],
 "SorrowfulStrike":[damage(13),f("CreateSadness",1)],
 "SteadyCadence":[damage(10),f("ForEachMelody"),draw(1,1)],
 "StrikeFool":[damage(6)],
 "Timber":[damage(20),g("C:ifFatal","斩杀时，","On Fatal,","ConditionalTrigger"),all_damage(30,1)],
 "TrickShot":[*shoot(12),f("IfShotSucceeded"),draw(2,2)],
 "Tune":[f("OnKiGained"),f("UpgradeRandomHand",1,owner=0)],
 "TwinShot":[*shoot(7,2)],
 "Uproot":[f("IfOwnImpaled"),f("Dislodge",owner=0),vulnerable(3,0)],
 "ViolentChord":[f("OnWahGained"),all_damage(7,0)],
 "Vitriol":[weak(2),f("CreateSadness",2)],
 "WahBlast":[all_damage(14),f("IfChoKi"),g("M:repeat","这张牌额外造成1次伤害。","This card deals damage 1 additional time.","Modifier",1)],
 "WahCho":[block(3),damage(3)],
 "WahChoKi":[block(8),damage(8),draw(1)],
 "WahHammer":[damage(9),f("DoubleAgainstImpaled")],
 "WahKi":[damage(3,2),nextturn(),energy(1,1),draw(1,1)],
 "WahSymphony":[all_damage(6),f("DamagePerMelodyCard",4)],
 "WahSweep":[all_damage(8)],
 "WahTouch":[damage(6),vulnerable(1)],
 "WhistlingArrows":[block(3),f("WhileDiscardArrowShot"),return_hand(1)],
 "Wrest":[f("IfOwnImpaled"),f("Dislodge",owner=0),draw(2,0)],
 "Yah":[f("OnFirstArrowDislodged"),energy(1,0)],
}


def write():
    zh=json.loads((LOCALES/'zhs/cards.json').read_text(encoding='utf-8-sig'))
    en=json.loads((LOCALES/'eng/cards.json').read_text(encoding='utf-8-sig'))
    pool=(SOURCE.parent/'MegaCrit.Sts2.Core.Models.CardPools/FoolCardPool.cs').read_text(encoding='utf-8-sig')
    native={c for c in re.findall(r'ModelDb.Card<(\w+)>',pool) if 'MultiplayerOnly' not in (SOURCE/(c+'.cs')).read_text(encoding='utf-8-sig')}
    assert native==RECIPES.keys(), (native-RECIPES.keys(),RECIPES.keys()-native)
    lines=['# Fool v114 reviewed unit operations', '',
           'Manually reviewed component boundaries. Melody/Shoot tags are semantic tags, not keyword effects.', '',
           '| 原卡 | 费用/蓝星/类型/目标/稀有度 | unit operations | Keywords | 英文名 |',
           '|---|---|---|---|---|']
    for c,ops in sorted(RECIPES.items()):
        s=(SOURCE/(c+'.cs')).read_text(encoding='utf-8-sig')
        key=re.sub(r'([a-z0-9])([A-Z])',r'\1_\2',c).upper()
        cost,kind,rarity,target=re.search(r': base\((-?\d+), CardType\.(\w+), CardRarity\.(\w+), TargetType\.(\w+)\)',s).groups()
        if 'HasEnergyCostX => true' in s: cost='X'
        # Property order is not semantic: Wah Touch and several Shots declare tags after CanonicalVars.
        tag_property=re.search(r'\b(?:CanonicalTags|Tags)\s*=>\s*(.*?);',s,re.S)
        tagarea=tag_property.group(1) if tag_property else ''
        tags=list(dict.fromkeys(re.findall(r'CardTag\.(\w+)',tagarea)))
        # Read only canonical keyword declarations, not OnUpgrade mutations.
        kwstart=s.find('CanonicalKeywords'); kwend=s.find('\n\tprotected ',kwstart+1)
        if kwend<0: kwend=s.find('\n\tpublic '+c+'()',kwstart+1)
        if kwstart>=0: tags+=re.findall(r'CardKeyword\.(\w+)',s[kwstart:kwend])
        if c=='Lullaby': tags=[]
        encoded=[]
        for op in ops:
            if len(op)==3:
                template,values,owner=op; d=DEFINITIONS[template[2:]]; text=d['chineseTemplate']; english=d['englishTemplate']
                assert len(values)==len(d['values']), (c,template,values)
                for slot,value in zip(d['values'],values):
                    text=text.replace('[['+slot['id']+']]',str(value)); english=english.replace('[['+slot['id']+']]',str(value))
                for slot in d['textSlots']:
                    text=text.replace('[['+slot['id']+']]',slot['chineseValue']);english=english.replace('[['+slot['id']+']]',slot['englishValue'])
                scope=d['scope']
            else: template,text,english,scope,owner=op
            required=scope=='SingleEnemyOnly'
            encoded.append('⟦'+'¦'.join([template,scope,str(required).lower(),'None',str(owner),text,english])+'⟧')
        title=zh[key+'.title']; etitle=en[key+'.title']
        lines.append(f"| {title} `{c}` | {cost}/-/{kind}/{'SingleEnemy' if target=='AnyEnemy' else 'Other'}/{rarity} | {'；'.join(encoded)} | {','.join(dict.fromkeys(tags))} | {etitle} |")
    (ROOT/'fool_unit_operations.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    (ROOT/'ChaosCardGenerator/Data/fool_components.json').write_text(json.dumps(list(DEFINITIONS.values()),ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    review = ["# 愚者组件拆解审阅清单（v0.114.0）", "",
        "85 张非多人角色牌采用逐张离线拆解，复用既有伤害、格挡、抽牌、能量、状态与回合触发器。",
        "下表列出 65 个愚者契约；数值均为命名槽位，并非把每个数值作为一个组件。完整原卡组合见 [拆解表](../fool_unit_operations.md)。", "",
        "## 组合规则", "",
        "- 射击是赋予 Shoot 标签的伤害 modifier。随机牌只允许射击加一条对单伤害句，关键字不计入句数；原卡精确重建保留原卡的额外效果。",
        "- 旋律是显式 Wah/Cho/Ki 标签，名称与标签共同保存；打出手牌中的旋律牌支持三种旋律的七个非空子集。",
        "- 炫技拆成瞄准、限本回合的下一次非爪牙死亡触发、永久瞄准成长。永久奖励沿用消耗／能力要求，禁止重复触发祖先和本牌复制循环。",
        "- 新版怒放选择手牌变化为全力射击；旧版全力射击的消耗计数公式仍用于现有存档。",
        "- 悲伤变化在出牌结束后执行，按一次性使用加悲伤代价计价，不额外叠加消耗补偿。",
        "- 产生喜悦／悲伤及变化产物使用共用衍生物／状态槽位；旋律掩码不属于可升级的数值。",
        "- 需要伤害、格挡、瞄准或消耗结果的后续效果必须具有相应主效果；嵌套条件在事件真正触发时求值。", "",
        "## 契约", "", "| 标识 | 类别 | 中文模板 | 数值槽 | 原卡来源 |", "|---|---|---|---|---|"]
    for key, definition in DEFINITIONS.items():
        source = []
        for card, operations in sorted(RECIPES.items()):
            if any(operation[0] == definition['template'] for operation in operations):
                native_key = re.sub(r'([a-z0-9])([A-Z])',r'\1_\2',card).upper()
                source.append(zh[native_key+'.title'])
        slots = ', '.join(slot['id'] + ('（结构选择）' if not slot['explicit'] else '') for slot in definition['values']) or '无'
        review.append('| `' + definition['template'] + '` | ' + definition['scope'] + ' | '
            + definition['chineseTemplate'] + ' | ' + slots + ' | ' + '、'.join(source) + ' |')
    review += ["", "## 实现位置", "",
        "组件契约与原卡组合：`tools/author_fool_catalog.py`、`Data/fool_components.json`、`fool_unit_operations.md`。",
        "生成合法性：`FoolComponentRules.cs`；价格：`FoolComponentDefinitions.cs`、`EffectBalanceModel.cs`、`PercentageValueTuning.cs`。",
        "战斗执行与事件：`ChaosFoolExecutor.cs`、`ChaosFoolPowerHooks.cs`、`ChaosFoolCardHooks.cs`。",
        "名字与旋律标签：`ManualFoolNameParts.cs`、`FoolMelodyNames.cs`；不依据本地化牌名解析战斗行为。", ""]
    (ROOT/'docs/fool-component-review.zh-CN.md').write_text('\n'.join(review),encoding='utf-8')
    print(f'Manual Fool catalog: {len(RECIPES)} recipes; {len(DEFINITIONS)} new component definitions.')


if __name__=='__main__': write()
