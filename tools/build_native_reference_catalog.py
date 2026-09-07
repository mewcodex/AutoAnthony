#!/usr/bin/env python3
"""Build the read-only v111 native card/component reference catalogs.

This authoring tool deliberately reads the reviewed generator catalog plus the
decompiled v111 card sources.  Its output is documentation/audit data only and
must never be used as a generator component source.
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import re
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_GAME_SOURCE = ROOT.parent / "proj" / "111" / "MegaCrit.Sts2.Core.Models.Cards"
DEFAULT_LOCALIZATION = ROOT.parent / "export" / "111" / "localization"
DATA = ROOT / "ChaosCardGenerator" / "Data"


def screaming_snake(value: str) -> str:
    value = re.sub(r"([A-Z]+)([A-Z][a-z])", r"\1_\2", value)
    return re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", value).upper()


def extract_balanced(text: str, start: int, opening: str = "{", closing: str = "}") -> tuple[str, int]:
    if start < 0 or start >= len(text) or text[start] != opening:
        raise ValueError(f"expected {opening!r} at {start}")
    depth = 0
    in_string = False
    escaped = False
    for index in range(start, len(text)):
        char = text[index]
        if in_string:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                in_string = False
            continue
        if char == '"':
            in_string = True
        elif char == opening:
            depth += 1
        elif char == closing:
            depth -= 1
            if depth == 0:
                return text[start + 1:index], index + 1
    raise ValueError(f"unterminated {opening}{closing} block")


def split_args(value: str) -> list[str]:
    result: list[str] = []
    depth = 0
    in_string = False
    escaped = False
    start = 0
    pairs = {"(": ")", "[": "]", "{": "}", "<": ">"}
    opens = set(pairs)
    closes = set(pairs.values())
    for index, char in enumerate(value):
        if in_string:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                in_string = False
            continue
        if char == '"':
            in_string = True
        elif char in opens:
            depth += 1
        elif char in closes:
            depth -= 1
        elif char == "," and depth == 0:
            result.append(value[start:index].strip())
            start = index + 1
    result.append(value[start:].strip())
    return result


def number(value: str) -> int | float | None:
    match = re.fullmatch(r"\s*(-?\d+(?:\.\d+)?)m?\s*", value)
    if not match:
        return None
    parsed = float(match.group(1))
    return int(parsed) if parsed.is_integer() else parsed


def expression(text: str, marker: str) -> str | None:
    start = text.find(marker)
    if start < 0:
        return None
    arrow = text.find("=>", start)
    brace = text.find("{", start)
    if arrow >= 0 and (brace < 0 or arrow < brace):
        index = arrow + 2
        depth = 0
        in_string = False
        while index < len(text):
            char = text[index]
            if char == '"':
                in_string = not in_string
            elif not in_string:
                if char in "([{<":
                    depth += 1
                elif char in ")]}>" and depth:
                    depth -= 1
                elif char == ";" and depth == 0:
                    return text[arrow + 2:index].strip()
            index += 1
    if brace >= 0:
        body, _ = extract_balanced(text, brace)
        return body
    return None


VAR_DEFAULT_NAMES = {
    "BlockVar": "Block",
    "CalculatedBlockVar": "CalculatedBlock",
    "CalculatedDamageVar": "CalculatedDamage",
    "CalculationBaseVar": "CalculationBase",
    "CalculationExtraVar": "CalculationExtra",
    "CardsVar": "Cards",
    "DamageVar": "Damage",
    "EnergyVar": "Energy",
    "ExtraDamageVar": "ExtraDamage",
    "ForgeVar": "Forge",
    "GoldVar": "Gold",
    "HealVar": "Heal",
    "HpLossVar": "HpLoss",
    "MaxHpVar": "MaxHp",
    "OstyDamageVar": "OstyDamage",
    "RepeatVar": "Repeat",
    "StarsVar": "Stars",
    "SummonVar": "Summon",
}

PROPERTY_VAR_NAMES = {
    "Block": "Block", "CalculatedBlock": "CalculatedBlock", "CalculatedDamage": "CalculatedDamage",
    "CalculationBase": "CalculationBase", "CalculationExtra": "CalculationExtra", "Cards": "Cards",
    "Damage": "Damage", "Dexterity": "DexterityPower", "Doom": "DoomPower", "Energy": "Energy",
    "ExtraDamage": "ExtraDamage", "Forge": "Forge", "Gold": "Gold", "Heal": "Heal",
    "HpLoss": "HpLoss", "MaxHp": "MaxHp", "OstyDamage": "OstyDamage", "Poison": "PoisonPower",
    "Repeat": "Repeat", "Stars": "Stars", "Strength": "StrengthPower", "Summon": "Summon",
    "Vulnerable": "VulnerablePower", "Weak": "WeakPower",
}


def parse_dynamic_vars(text: str) -> list[dict[str, Any]]:
    block = expression(text, "CanonicalVars")
    if not block:
        return []
    result: list[dict[str, Any]] = []
    pattern = re.compile(r"new\s+(?P<type>PowerVar<[^>]+>|\w+Var)\s*\(")
    for match in pattern.finditer(block):
        args_body, _ = extract_balanced(block, match.end() - 1, "(", ")")
        args = split_args(args_body)
        type_name = match.group("type")
        bare_type = type_name.split("<", 1)[0]
        explicit_name = args[0][1:-1] if args and re.fullmatch(r'"[^"\\]*"', args[0]) else None
        if explicit_name is not None:
            value_arg = args[1] if len(args) > 1 else ""
        else:
            value_arg = args[0] if args else ""
        if type_name.startswith("PowerVar<"):
            generic = type_name[len("PowerVar<"):-1]
            name = explicit_name or generic
            kind = "Power"
        else:
            name = explicit_name or VAR_DEFAULT_NAMES.get(bare_type)
            kind = bare_type.removesuffix("Var")
        if not name:
            # Calculated variables without a literal base value still need identity.
            continue
        parsed = number(value_arg)
        result.append({
            "Id": name,
            "Kind": kind,
            "BaseValue": parsed,
            "BaseExpression": None if parsed is not None else value_arg.strip(),
            "Calculated": bare_type.startswith("Calculated"),
        })
    # Decompiler output can repeat a constructor inside a calculated lambda. Identity is canonical.
    return list({item["Id"]: item for item in result}.values())


def parse_enum_property(text: str, marker: str, prefix: str) -> list[str]:
    block = expression(text, marker)
    return [] if not block else sorted(set(re.findall(re.escape(prefix) + r"(\w+)", block)))


def method_body(text: str, name: str) -> str | None:
    match = re.search(rf"\b{name}\s*\(\s*\)\s*", text)
    if not match:
        return None
    brace = text.find("{", match.end())
    return None if brace < 0 else extract_balanced(text, brace)[0]


IMPLICIT_UPGRADE_RULES: dict[str, list[dict[str, Any]]] = {
    "Armaments": [{"Kind": "ReplaceSelection", "From": {"Zone": "Hand", "Count": 1},
                    "To": {"Zone": "Hand", "Count": "All"}, "Action": "Upgrade"}],
    "Begone": [{"Kind": "UpgradeProducedCard", "Card": "MinionStrike"}],
    "Cascade": [{"Kind": "ChangeXOffset", "Component": "AutoPlayDrawTop", "Delta": 1}],
    "Charge": [{"Kind": "UpgradeProducedCard", "Card": "MinionDiveBomb"}],
    "Compact": [{"Kind": "UpgradeProducedCard", "Card": "Fuel"}],
    "Darkness": [{"Kind": "ChangeRepeatCount", "Component": "TriggerAllDarkOrbPassives", "Delta": 1}],
    "Dirge": [{"Kind": "UpgradeProducedCard", "Card": "Soul"}],
    "Enlightenment": [{"Kind": "ChangeDuration", "Component": "SetHandCostToOne",
                         "From": "Turn", "To": "Combat"}],
    "Guards": [{"Kind": "UpgradeProducedCard", "Card": "MinionSacrifice"}],
    "HiddenDaggers": [{"Kind": "UpgradeProducedCard", "Card": "Shiv"}],
    "Jackpot": [{"Kind": "UpgradeProducedCards", "Filter": {"EnergyCost": 0, "EnergyCostX": False}}],
    "KnifeTrap": [{"Kind": "UpgradeReferencedCardsBeforePlay", "Filter": {"Tag": "Shiv",
                                                                              "Zone": "Exhaust"}}],
    "Largesse": [{"Kind": "UpgradeProducedCard", "Pool": "Colorless"}],
    "Malaise": [{"Kind": "ChangeXOffset", "Component": "StrengthLossAndWeak", "Delta": 1}],
    "ManifestAuthority": [{"Kind": "UpgradeProducedCard", "Pool": "Colorless"}],
    "MultiCast": [{"Kind": "ChangeXOffset", "Component": "EvokeRightmostOrb", "Delta": 1}],
    "PrimalForce": [{"Kind": "UpgradeProducedCard", "Card": "GiantRock"}],
    "Quasar": [{"Kind": "UpgradeProducedChoices", "Pool": "Colorless"}],
    "Reave": [{"Kind": "UpgradeProducedCard", "Card": "Soul"}],
    "Spinner": [{"Kind": "AddComponent", "Component": "ChannelGlassOrb", "Timing": "OnPlay"}],
    "Splash": [{"Kind": "UpgradeProducedChoices", "Filter": {"Type": "Attack",
                                                                   "Pool": "OtherCharacter"}}],
    "Stoke": [{"Kind": "UpgradeProducedCards", "Pool": "CurrentCharacter"}],
    "StormOfSteel": [{"Kind": "UpgradeProducedCard", "Card": "Shiv"}],
    "Tempest": [{"Kind": "ChangeXOffset", "Component": "ChannelLightning", "Delta": 1}],
    "TeslaCoil": [{"Kind": "ChangeRepeatCount", "Component": "TriggerAllLightningOrbPassives", "Delta": 1}],
    "TrueGrit": [{"Kind": "ReplaceSelection", "From": {"Zone": "Hand", "Mode": "Random", "Count": 1},
                  "To": {"Zone": "Hand", "Mode": "PlayerChoice", "Count": 1}, "Action": "Exhaust"}],
}


def parse_upgrade(class_name: str, text: str, variables: list[dict[str, Any]], base_cost: int,
                  base_star_cost: int, base_keywords: list[str]) -> dict[str, Any]:
    max_match = re.search(r"MaxUpgradeLevel\s*=>\s*(\d+)", text)
    max_level = int(max_match.group(1)) if max_match else 1
    body = method_body(text, "OnUpgrade")
    actions: list[dict[str, Any]] = []
    if body:
        for match in re.finditer(r"DynamicVars\s*\[\s*\"([^\"]+)\"\s*\]\.UpgradeValueBy\((-?[\d.]+)m?\)", body):
            actions.append({"Kind": "ChangeVariable", "Variable": match.group(1), "Delta": number(match.group(2))})
        for match in re.finditer(r"DynamicVars\.(\w+)\.UpgradeValueBy\((-?[\d.]+)m?\)", body):
            actions.append({"Kind": "ChangeVariable", "Variable": PROPERTY_VAR_NAMES.get(match.group(1), match.group(1)),
                            "Delta": number(match.group(2))})
        for match in re.finditer(r"EnergyCost\.UpgradeBy\((-?[\d.]+)\)", body):
            actions.append({"Kind": "ChangeEnergyCost", "Delta": number(match.group(1))})
        for match in re.finditer(r"UpgradeStarCostBy\((-?[\d.]+)\)", body):
            actions.append({"Kind": "ChangeStarCost", "Delta": number(match.group(1))})
        for match in re.finditer(r"AddKeyword\(CardKeyword\.(\w+)\)", body):
            actions.append({"Kind": "AddKeyword", "Keyword": match.group(1)})
        for match in re.finditer(r"RemoveKeyword\(CardKeyword\.(\w+)\)", body):
            actions.append({"Kind": "RemoveKeyword", "Keyword": match.group(1)})

        # Preserve any uncommon mutation as an explicit source-level action instead of silently dropping it.
        scrubbed = body
        scrub_patterns = [
            r"(?:base\.)?DynamicVars\s*\[\s*\"[^\"]+\"\s*\]\.UpgradeValueBy\(-?[\d.]+m?\)\s*;",
            r"(?:base\.)?DynamicVars\.\w+\.UpgradeValueBy\(-?[\d.]+m?\)\s*;",
            r"(?:base\.)?EnergyCost\.UpgradeBy\(-?[\d.]+\)\s*;",
            r"UpgradeStarCostBy\(-?[\d.]+\)\s*;",
            r"AddKeyword\(CardKeyword\.\w+\)\s*;",
            r"RemoveKeyword\(CardKeyword\.\w+\)\s*;",
        ]
        for pattern in scrub_patterns:
            scrubbed = re.sub(pattern, "", scrubbed)
        scrubbed = re.sub(r"\s+", " ", scrubbed).strip()
        if scrubbed:
            actions.append({"Kind": "NativeUpgradeRule", "Rule": scrubbed})

    actions.extend(copy.deepcopy(IMPLICIT_UPGRADE_RULES.get(class_name, [])))

    upgraded_variables = {item["Id"]: item["BaseValue"] for item in variables}
    upgraded_cost = base_cost
    upgraded_star = base_star_cost
    upgraded_keywords = set(base_keywords)
    for action in actions:
        if action["Kind"] == "ChangeVariable" and action["Variable"] in upgraded_variables \
                and upgraded_variables[action["Variable"]] is not None:
            upgraded_variables[action["Variable"]] += action["Delta"]
        elif action["Kind"] == "ChangeEnergyCost":
            upgraded_cost += action["Delta"]
        elif action["Kind"] == "ChangeStarCost":
            upgraded_star += action["Delta"]
        elif action["Kind"] == "AddKeyword":
            upgraded_keywords.add(action["Keyword"])
        elif action["Kind"] == "RemoveKeyword":
            upgraded_keywords.discard(action["Keyword"])
    return {
        "MaxLevel": max_level,
        "Actions": actions,
        "Result": {
            "EnergyCost": upgraded_cost,
            "StarCost": upgraded_star,
            "Variables": upgraded_variables,
            "Keywords": sorted(upgraded_keywords),
        } if max_level > 0 else None,
    }


def parse_constructor(text: str, class_name: str) -> tuple[int, str, str, str, bool]:
    match = re.search(rf"public\s+{re.escape(class_name)}\s*\(\s*\)\s*:\s*base\s*\(", text)
    if not match:
        raise ValueError(f"missing constructor for {class_name}")
    body, _ = extract_balanced(text, match.end() - 1, "(", ")")
    args = split_args(body)
    if len(args) < 4:
        raise ValueError(f"invalid CardModel constructor for {class_name}: {body}")
    cost = number(args[0])
    if not isinstance(cost, int):
        raise ValueError(f"non-integral card cost for {class_name}: {args[0]}")
    return (cost, args[1].split(".")[-1], args[2].split(".")[-1], args[3].split(".")[-1],
            not any("shouldShowInCardLibrary: false" in arg for arg in args[4:]))


def parse_card_sources(source: Path) -> dict[str, dict[str, Any]]:
    pool_root = source.parent / "MegaCrit.Sts2.Core.Models.CardPools"
    pool_for: dict[str, str] = {}
    for pool_file in pool_root.glob("*CardPool.cs"):
        if pool_file.stem == "MockCardPool":
            continue
        text = pool_file.read_text(encoding="utf-8-sig")
        for class_name in re.findall(r"ModelDb\.Card<(\w+)>\(\)", text):
            if class_name in pool_for:
                raise ValueError(f"card {class_name} occurs in multiple native pools")
            pool_for[class_name] = pool_file.stem.removesuffix("CardPool")

    result: dict[str, dict[str, Any]] = {}
    for path in sorted(source.glob("*.cs")):
        class_name = path.stem
        if class_name.startswith("Mock"):
            continue
        text = path.read_text(encoding="utf-8-sig")
        if re.search(r"MultiplayerConstraint\s*=>\s*CardMultiplayerConstraint\.MultiplayerOnly", text):
            continue
        cost, card_type, rarity, target, library = parse_constructor(text, class_name)
        star = re.search(r"CanonicalStarCost\s*=>\s*(-?\d+)", text)
        star_cost = int(star.group(1)) if star else -1
        has_energy_x = bool(re.search(r"HasEnergyCostX\s*=>\s*true", text))
        has_star_x = bool(re.search(r"HasStarCostX\s*=>\s*true", text))
        if "IsUpgraded" in text and class_name not in IMPLICIT_UPGRADE_RULES:
            raise ValueError(f"unstructured IsUpgraded behavior in {class_name}")
        variables = parse_dynamic_vars(text)
        keywords = parse_enum_property(text, "CanonicalKeywords", "CardKeyword.")
        tags = parse_enum_property(text, "CanonicalTags", "CardTag.")
        result[class_name] = {
            "ClassName": class_name,
            "NativeId": screaming_snake(class_name),
            "Pool": pool_for.get(class_name, "Unpooled"),
            "EnergyCost": cost,
            "EnergyCostX": has_energy_x,
            "StarCost": star_cost,
            "StarCostX": has_star_x,
            "Type": card_type,
            "Rarity": rarity,
            "Target": target,
            "ShouldShowInLibrary": library,
            "Keywords": keywords,
            "Tags": tags,
            "Variables": variables,
            "Upgrade": parse_upgrade(class_name, text, variables, cost, star_cost, keywords),
        }
    missing_pool = sorted(name for name, card in result.items() if card["Pool"] == "Unpooled")
    if missing_pool:
        raise ValueError(f"native cards missing pool membership: {missing_pool}")
    return result


def loc_entry(table: dict[str, str], native_id: str, suffix: str) -> str:
    return table.get(f"{native_id}.{suffix}", "")


def arg_var(name: str) -> dict[str, str]:
    return {"Source": "NativeVariable", "Id": name}


def arg_literal(value: Any) -> dict[str, Any]:
    return {"Source": "Literal", "Value": value}


def ref(component: str, /, trigger: int = -1, **arguments: Any) -> dict[str, Any]:
    return {
        "ComponentId": component,
        "TriggerOwner": trigger,
        "Arguments": {key: (value if isinstance(value, dict) and "Source" in value else arg_literal(value))
                      for key, value in arguments.items()},
    }


def special_components(card: str) -> list[dict[str, Any]]:
    v = arg_var
    empty: set[str] = {
        "AscendersBane", "Clumsy", "CurseOfTheBell", "Dazed", "Debris", "Folly", "Greed", "Injury",
        "PoorSleep", "Soot", "SporeMind", "Wound", "Writhe",
    }
    if card in empty:
        return []
    mapping: dict[str, list[dict[str, Any]]] = {
        "Abundance": [ref("choice.generated_cards", candidates=3, choose=1, cardType="Power", upgraded=True,
                          destination="Hand", freeDuration="Turn")],
        "Apotheosis": [ref("upgrade.all_cards", zone="AllCombatPiles")],
        "Apparition": [ref("gain.power", power="Intangible", amount=v("IntangiblePower"))],
        "BadLuck": [ref("trigger.turn_end_if_in_hand"), ref("lose.hp", trigger=0, amount=v("HpLoss"))],
        "Beckon": [ref("trigger.turn_end_if_in_hand"), ref("lose.hp", trigger=0, amount=v("HpLoss"))],
        "BrightestFlame": [ref("gain.energy", amount=v("Energy")), ref("draw.cards", amount=v("Cards")),
                           ref("lose.max_hp", amount=v("MaxHp"))],
        "Burn": [ref("trigger.turn_end_if_in_hand"), ref("take.unpowered_damage", trigger=0, amount=v("Damage"))],
        "ByrdonisEgg": [ref("quest.add_rest_site_option", option="Hatch")],
        "ByrdSwoop": [ref("deal.damage", amount=v("Damage"), target="SelectedEnemy", hits=1)],
        "Caltrops": [ref("trigger.when_attacked"), ref("deal.damage_to_attacker", trigger=0,
                                                         amount=v("ThornsPower"))],
        "Clash": [ref("play_condition.hand_all_type", cardType="Attack"),
                  ref("deal.damage", amount=v("Damage"), target="SelectedEnemy", hits=1)],
        "Debt": [ref("trigger.turn_end_if_in_hand"), ref("lose.gold", trigger=0, amount=v("Gold"))],
        "Decay": [ref("trigger.turn_end_if_in_hand"), ref("take.unpowered_damage", trigger=0, amount=v("Damage"))],
        "DeprecatedCard": [ref("draw.cards", amount=1), ref("remove.deck_version")],
        "Disintegration": [ref("choice_only.apply_power", power="Disintegration", amount=v("DisintegrationPower"))],
        "Distraction": [ref("create.random_card", cardType="Skill", destination="Hand", count=1,
                            freeDuration="Turn")],
        "Doubt": [ref("trigger.turn_end_if_in_hand"), ref("gain.power", trigger=0, power="Weak",
                                                            amount=v("WeakPower"))],
        "Dowsing": [ref("quest.count_rooms", roomType="Unknown", amount=v("Rooms")),
                    ref("quest.transform_on_complete", trigger=0, card="Abundance")],
        "DualWield": [ref("select.hand_card", cardTypes=["Attack", "Power"], count=1),
                      ref("create.copy_of_selected", trigger=0, destination="Hand", count=v("Cards"))],
        "Enlightenment": [ref("set.hand_cost", amount=1, duration="TurnOrCombatOnUpgrade")],
        "Enthralled": [ref("play_restriction.must_play_first")],
        "Entrench": [ref("multiply.current_block", multiplier=2)],
        "Exterminate": [ref("deal.damage", amount=v("Damage"), target="AllEnemies", hits=v("Repeat"))],
        "Fasten": [ref("gain.power", power="Fasten", amount=v("ExtraBlock"))],
        "FeedingFrenzy": [ref("gain.power", power="Strength", amount=v("StrengthPower"), duration="Turn")],
        "FranticEscape": [ref("sandpit.move_farther"), ref("sandpit.increase_counter", amount=1),
                          ref("change.this_card_cost", amount=1, duration="Combat")],
        "Fuel": [ref("gain.energy", amount=v("Energy"))],
        "GiantRock": [ref("deal.damage", amount=v("Damage"), target="SelectedEnemy", hits=1)],
        "Guilty": [ref("trigger.after_combat"), ref("count.down", trigger=0, amount=v("Combats")),
                   ref("remove.from_deck_when_zero", trigger=1)],
        "HelloWorld": [ref("trigger.turn_start"), ref("create.random_card", trigger=0, rarity="Common",
                                                                 destination="Hand", count=1)],
        "Infection": [ref("trigger.turn_end_if_in_hand"), ref("take.unpowered_damage", trigger=0,
                                                                amount=v("Damage"))],
        "LanternKey": [ref("quest.force_event_next_act", act=3, event="WarHistorianRepy")],
        "Luminesce": [ref("gain.energy", amount=v("Energy"))],
        "Maul": [ref("deal.damage", amount=v("Damage"), target="SelectedEnemy", hits=2),
                 ref("increase.named_card_damage", card="Maul", amount=v("Increase"), duration="Combat")],
        "Metamorphosis": [ref("create.random_card", cardType="Attack", destination="Draw", count=v("Cards"),
                              freeDuration="Combat")],
        "MindRot": [ref("choice_only.apply_power", power="MindRot", amount=v("MindRotPower"))],
        "MinionDiveBomb": [ref("deal.damage", amount=v("Damage"), target="SelectedEnemy", hits=1)],
        "MinionSacrifice": [ref("gain.block", amount=v("Block"))],
        "MinionStrike": [ref("deal.damage", amount=v("Damage"), target="SelectedEnemy", hits=1),
                         ref("draw.cards", amount=v("Cards"))],
        "NeowsFury": [ref("deal.damage", amount=v("Damage"), target="SelectedEnemy", hits=1),
                      ref("select.discard_to_hand", maximum=v("Cards"), optional=True)],
        "Normality": [ref("play_restriction.max_cards_per_turn", amount=v("CalculationBase"))],
        "Outmaneuver": [ref("gain.energy", amount=v("Energy"), timing="NextTurn")],
        "Peck": [ref("deal.damage", amount=v("Damage"), target="SelectedEnemy", hits=v("Repeat"))],
        "Rebound": [ref("deal.damage", amount=v("Damage"), target="SelectedEnemy", hits=1),
                    ref("next_played_card.move", destination="DrawTop", duration="Turn")],
        "Regret": [ref("trigger.turn_end_if_in_hand"), ref("lose.hp_per_card_in_hand", trigger=0, amountPerCard=1)],
        "Relax": [ref("gain.block", amount=v("Block")), ref("trigger.next_turn_start"),
                  ref("draw.cards", trigger=1, amount=v("Cards")), ref("gain.energy", trigger=1, amount=v("Energy"))],
        "RipAndTear": [ref("deal.damage", amount=v("Damage"), target="RandomEnemy", hits=2)],
        "Shame": [ref("trigger.turn_end_if_in_hand"), ref("gain.power", trigger=0, power="Frail",
                                                             amount=v("Frail"))],
        "Shiv": [ref("deal.damage", amount=v("Damage"), target="DynamicSelectedOrAllEnemies", hits=1)],
        "Slimed": [ref("draw.cards", amount=v("Cards"))],
        "Sloth": [ref("choice_only.apply_power", power="Sloth", amount=v("SlothPower"))],
        "Soul": [ref("draw.cards", amount=v("Cards"))],
        "SovereignBlade": [ref("deal.damage", amount=v("Damage"), target="DynamicSelectedOrAllEnemies",
                                 hits=v("Repeat")), ref("gain.block_if_enabled", amount=v("CalculatedBlock"))],
        "SpoilsMap": [ref("quest.replace_next_act_map", map="SpoilsActMap"),
                      ref("quest.mark_treasure", gold=v("Gold")), ref("quest.gain_gold_and_remove", gold=v("Gold"))],
        "Squash": [ref("deal.damage", amount=v("Damage"), target="SelectedEnemy", hits=1),
                   ref("apply.power", power="Vulnerable", amount=v("VulnerablePower"), target="SelectedEnemy")],
        "Stack": [ref("gain.block_scaled", base=v("CalculationBase"), scale="DiscardPileCount", multiplier=1)],
        "SweepingGaze": [ref("osty.deal_damage", amount=v("OstyDamage"), target="RandomEnemy")],
        "ToricToughness": [ref("gain.block", amount=v("Block")), ref("trigger.next_turn_starts",
                                                                             duration=v("Turns")),
                           ref("gain.block", trigger=1, amount=v("Block"))],
        "Toxic": [ref("trigger.turn_end_if_in_hand"), ref("take.unpowered_damage", trigger=0, amount=v("Damage"))],
        "Void": [ref("trigger.when_drawn_this_card"), ref("lose.energy", trigger=0, amount=v("Energy"))],
        "WasteAway": [ref("choice_only.apply_power", power="WasteAway", amount=v("WasteAwayPower"))],
        "Whistle": [ref("deal.damage", amount=v("Damage"), target="SelectedEnemy", hits=1),
                    ref("apply.power", power="Stun", amount=1, target="SelectedEnemy")],
        "Wish": [ref("select.draw_to_hand", count=1)],
        "Wither": [ref("trigger.turn_end_if_in_hand"), ref("take.unpowered_damage", trigger=0, amount=v("Damage"))],
    }
    if card not in mapping:
        raise KeyError(f"no reference-only component recipe for {card}")
    return mapping[card]


MAD_SCIENCE = {
    "AttackSapping": ("Attack", "AnyEnemy", [
        ref("deal.damage", amount=arg_var("Damage"), target="SelectedEnemy", hits=1),
        ref("apply.power", power="Weak", amount=arg_var("SappingWeak"), target="SelectedEnemy"),
        ref("apply.power", power="Vulnerable", amount=arg_var("SappingVulnerable"), target="SelectedEnemy")]),
    "AttackViolence": ("Attack", "AnyEnemy", [
        ref("deal.damage", amount=arg_var("Damage"), target="SelectedEnemy", hits=arg_var("ViolenceHits"))]),
    "AttackChoking": ("Attack", "AnyEnemy", [
        ref("deal.damage", amount=arg_var("Damage"), target="SelectedEnemy", hits=1),
        ref("trigger.after_card_played", duration="Turn", excludesSource=False),
        ref("lose.enemy_hp", trigger=1, amount=arg_var("ChokingDamage"), target="SelectedEnemy")]),
    "SkillEnergized": ("Skill", "Self", [ref("gain.block", amount=arg_var("Block")),
                                             ref("gain.energy", amount=arg_var("EnergizedEnergy"))]),
    "SkillWisdom": ("Skill", "Self", [ref("gain.block", amount=arg_var("Block")),
                                          ref("draw.cards", amount=arg_var("WisdomCards"))]),
    "SkillChaos": ("Skill", "Self", [ref("gain.block", amount=arg_var("Block")),
                                         ref("create.random_card", count=1, destination="Hand",
                                             freeDuration="Turn")]),
    "PowerExpertise": ("Power", "Self", [
        ref("gain.power", power="Strength", amount=arg_var("ExpertiseStrength")),
        ref("gain.power", power="Dexterity", amount=arg_var("ExpertiseDexterity"))]),
    "PowerCurious": ("Power", "Self", [ref("card_type.cost_reduction", cardType="Power",
                                               amount=arg_var("CuriousReduction"), duration="Combat")]),
    "PowerImprovement": ("Power", "Self", [ref("trigger.combat_end"),
                                                ref("upgrade.random_deck_card", trigger=0, count=1)]),
}


def runtime_contract(spec: dict[str, Any]) -> dict[str, Any]:
    """Return the non-instance part of a runtime spec.

    Numeric base values belong to a card occurrence.  Everything else (including
    filters, zones, duration, and value-source semantics) identifies the unit
    operation itself and therefore participates in the stable component ID.
    """
    contract = copy.deepcopy(spec)
    contract["Values"] = [{key: value for key, value in item.items() if key != "BaseValue"}
                          for item in contract.get("Values", [])]
    return contract


def native_component_id(atom: dict[str, Any], spec: dict[str, Any]) -> str:
    parts = ["native", spec["Opcode"], spec["Variant"], spec["Target"], atom["Scope"]]
    prefix = ".".join(re.sub(r"[^a-z0-9]+", "_", str(part).lower()).strip("_") for part in parts)
    signature = {
        "Scope": atom["Scope"],
        "RequiresSingleTarget": atom["RequiresSingleTarget"],
        "CardReference": atom["CardReference"],
        "RuntimeContract": runtime_contract(spec),
    }
    digest = hashlib.sha256(json.dumps(signature, sort_keys=True, separators=(",", ":"),
                                       ensure_ascii=True).encode("ascii")).hexdigest()[:10]
    return f"{prefix}.{digest}"


# Equal native base values are common (damage/block, Strength/Dexterity, draw/energy, and so on),
# so value equality alone is not a safe binding rule.  This small reviewed table resolves only those
# ambiguous occurrences.  Unambiguous bindings continue to be derived from the native source below.
# Each tuple is (native variable, scale, offset), where componentValue = nativeValue * scale + offset.
NATIVE_BINDING_OVERRIDES: dict[tuple[str, int, str], tuple[tuple[str, float, float], ...]] = {
    ("HandOfGreed", 0, "damage"): (("Damage", 1, 0),),
    ("HandOfGreed", 2, "amount"): (("Gold", 1, 0),),
    ("Prowess", 0, "amount"): (("StrengthPower", 1, 0),),
    ("Prowess", 1, "amount"): (("DexterityPower", 1, 0),),
    ("Restlessness", 1, "draw"): (("Cards", 1, 0),),
    ("Restlessness", 2, "energy"): (("Energy", 1, 0),),
    ("RollingBoulder", 1, "damage"): (("RollingBoulderPower", 1, 0),),
    ("Shockwave", 0, "amount"): (("Power", 1, 0),),
    ("Shockwave", 1, "amount"): (("Power", 1, 0),),
    ("BulkUp", 0, "amount"): (("StrengthPower", 1, 0),),
    ("BulkUp", 1, "amount"): (("DexterityPower", 1, 0),),
    ("Coolheaded", 1, "draw"): (("Cards", 1, 0),),
    ("Modded", 1, "draw"): (("Cards", 1, 0),),
    ("Null", 1, "amount"): (("WeakPower", 1, 0),),
    ("RocketPunch", 1, "draw"): (("Cards", 1, 0),),
    ("Brand", 2, "amount"): (("StrengthPower", 1, 0),),
    ("Dominate", 0, "amount"): (("VulnerablePower", 1, 0),),
    ("DrumOfBattle", 2, "energy"): (("Energy", 1, 0),),
    ("EvilEye", 0, "block"): (("Block", 1, 0),),
    ("EvilEye", 2, "block"): (("Block", 1, 0),),
    ("IronWave", 0, "block"): (("Block", 1, 0),),
    ("IronWave", 1, "damage"): (("Damage", 1, 0),),
    ("SwordBoomerang", 0, "hits"): (("Repeat", 1, 0),),
    ("Uppercut", 1, "amount"): (("Power", 1, 0),),
    ("Uppercut", 2, "amount"): (("Power", 1, 0),),
    ("Spite", 2, "extra_hits"): (("Repeat", 1, -1),),
    ("BoneShards", 1, "damage"): (("OstyDamage", 1, 0),),
    ("BoneShards", 2, "block"): (("Block", 1, 0),),
    ("CaptureSpirit", 0, "amount"): (("Damage", 1, 0),),
    ("CaptureSpirit", 1, "amount"): (("Cards", 1, 0),),
    ("DeathsDoor", 0, "block"): (("Block", 1, 0),),
    ("DeathsDoor", 2, "block"): (("Block", 1, 0),),
    ("Invoke", 1, "amount"): (("Summon", 1, 0),),
    ("Invoke", 2, "energy"): (("Energy", 1, 0),),
    ("Putrefy", 0, "amount"): (("Power", 1, 0),),
    ("Putrefy", 1, "amount"): (("Power", 1, 0),),
    ("SharedFate", 1, "amount"): (("EnemyStrengthLoss", 1, 0),),
    ("BeatIntoShape", 0, "damage"): (("Damage", 1, 0),),
    ("BeatIntoShape", 1, "amount"): (("CalculationBase", 1, 0),),
    ("BeatIntoShape", 3, "amount"): (("CalculationExtra", 1, 0),),
    ("Convergence", 3, "stars"): (("Stars", 1, 0),),
    ("CrushUnder", 1, "amount"): (("StrengthLoss", 1, 0),),
    ("DecisionsDecisions", 0, "draw"): (("Cards", 1, 0),),
    ("DyingStar", 0, "damage"): (("Damage", 1, 0),),
    ("DyingStar", 1, "amount"): (("StrengthLoss", 1, 0),),
    ("Glow", 0, "stars"): (("Stars", 1, 0),),
    ("Resonance", 0, "amount"): (("StrengthPower", 1, 0),),
    ("Resonance", 1, "amount"): (("StrengthPower", 1, 0),),
    ("WroughtInWar", 0, "damage"): (("Damage", 1, 0),),
    ("WroughtInWar", 1, "amount"): (("Forge", 1, 0),),
    ("CelestialMight", 1, "extra_hits"): (("Repeat", 1, -1),),
    ("BouncingFlask", 0, "hits"): (("Repeat", 1, 0),),
    ("Dash", 0, "block"): (("Block", 1, 0),),
    ("Dash", 1, "damage"): (("Damage", 1, 0),),
    ("DodgeAndRoll", 0, "block"): (("Block", 1, 0),),
    ("DodgeAndRoll", 2, "block"): (("Block", 1, 0),),
    ("Prepared", 0, "draw"): (("Cards", 1, 0),),
}


def bind_native_upgrade_variables(class_name: str, source: dict[str, Any],
                                  components: list[dict[str, Any]]) -> None:
    variables = {item["Id"]: item for item in source["Variables"]}
    deltas: dict[str, int | float] = {}
    for action in source["Upgrade"]["Actions"]:
        if action["Kind"] == "ChangeVariable":
            variable = action["Variable"]
            deltas[variable] = deltas.get(variable, 0) + action["Delta"]

    # A single upgraded native variable is still not sufficient evidence when several component fields happen to
    # share its base value. Brand, for example, upgrades Strength 1 -> 2 but also contains fixed self-damage 1 and
    # an Exhaust-one selector. Binding all three because they equal 1 corrupts both reconstruction and runtime
    # upgrades. Such cards must use a reviewed override for every intentionally bound occurrence.
    value_occurrences: dict[int | float | None, int] = {}
    for component in components:
        for argument in component["Arguments"].values():
            if argument["Source"] == "RuntimeValue" and argument["Upgradable"]:
                base_value = argument["BaseValue"]
                value_occurrences[base_value] = value_occurrences.get(base_value, 0) + 1

    for component_index, component in enumerate(components):
        for argument_name, argument in component["Arguments"].items():
            if argument["Source"] != "RuntimeValue" or not argument["Upgradable"]:
                continue
            override = NATIVE_BINDING_OVERRIDES.get((class_name, component_index, argument_name))
            if override is None:
                candidates = [variable for variable, delta in deltas.items()
                              if variables[variable]["BaseValue"] == argument["BaseValue"]]
                if len(candidates) != 1 or value_occurrences.get(argument["BaseValue"], 0) != 1:
                    continue
                override = ((candidates[0], 1, 0),)

            bindings = []
            for variable, scale, offset in override:
                if variable not in variables or variable not in deltas:
                    raise ValueError(f"invalid native binding {class_name}/{component_index}/"
                                     f"{argument_name} -> {variable}")
                native_base = variables[variable]["BaseValue"]
                if argument["ValueSource"] == "fixed" and native_base is not None \
                        and native_base * scale + offset != argument["BaseValue"]:
                    raise ValueError(f"native binding value mismatch {class_name}/{component_index}/"
                                     f"{argument_name}: {native_base} * {scale} + {offset} != "
                                     f"{argument['BaseValue']}")
                bindings.append({
                    "Variable": variable,
                    "BaseValue": native_base,
                    "UpgradeDelta": deltas[variable],
                    "ValueTransform": {"Scale": scale, "Offset": offset},
                })
            argument["NativeBindings"] = bindings


def current_components(class_name: str, source: dict[str, Any], recipe: dict[str, Any],
                       specs: dict[str, dict[str, Any]]) -> list[dict[str, Any]]:
    result = []
    for atom in recipe["Atoms"]:
        spec = copy.deepcopy(specs[atom["SemanticId"]])
        result.append({
            "ComponentId": native_component_id(atom, spec),
            "SemanticId": atom["SemanticId"],
            "TriggerOwner": atom["TriggerOwner"],
            "CardReference": atom["CardReference"],
            "RequiresSingleTarget": atom["RequiresSingleTarget"],
            "Arguments": {value["Id"]: {
                "Source": "RuntimeValue",
                "ValueSource": value["Source"],
                "BaseValue": value["BaseValue"],
                "Offset": value["Offset"],
                "Upgradable": value["Upgradable"],
            } for value in spec["Values"]},
            "RuntimeSpec": spec,
            "Text": {"zhHans": atom["ChineseText"], "en": atom["EnglishText"]},
        })
    bind_native_upgrade_variables(class_name, source, result)
    return result


def component_display_name(component_id: str) -> tuple[str, str]:
    words = component_id.replace("native.", "").replace("_", " ").replace(".", " / ")
    en = " ".join(word.capitalize() if word != "/" else word for word in words.split())
    common_zh = {
        "deal.damage": "造成伤害", "gain.block": "获得格挡", "draw.cards": "抽牌",
        "gain.energy": "获得能量", "lose.energy": "失去能量", "gain.power": "获得状态",
        "apply.power": "给予状态", "create.random_card": "生成随机牌", "trigger.turn_start": "回合开始时",
        "trigger.turn_end_if_in_hand": "回合结束时若在手牌中", "trigger.when_drawn_this_card": "抽到此牌时",
        "take.unpowered_damage": "受到不受能力影响的伤害", "lose.hp": "失去生命",
    }
    return common_zh.get(component_id, en), en


def build(args: argparse.Namespace) -> None:
    sources = parse_card_sources(args.game_source)
    recipes = json.loads((DATA / "catalog_recipes.json").read_text(encoding="utf-8"))
    runtime_entries = json.loads((DATA / "catalog_runtime_specs.json").read_text(encoding="utf-8"))
    runtime_specs = {entry["Id"]: entry["Spec"] for entry in runtime_entries}
    zhs = json.loads((args.localization / "zhs" / "cards.json").read_text(encoding="utf-8-sig"))
    eng = json.loads((args.localization / "eng" / "cards.json").read_text(encoding="utf-8-sig"))
    recipe_by_class = {recipe["Id"]: recipe for recipe in recipes}
    orphan_recipes = sorted(set(recipe_by_class) - set(sources))
    if orphan_recipes:
        raise ValueError(f"generator recipes no longer map to non-multiplayer native cards: {orphan_recipes}")
    referenced_semantics = {atom["SemanticId"] for recipe in recipes for atom in recipe["Atoms"]}
    missing_specs = sorted(referenced_semantics - set(runtime_specs))
    if missing_specs:
        raise ValueError(f"generator recipes reference missing runtime specs: {missing_specs}")

    cards: list[dict[str, Any]] = []
    all_components: dict[str, dict[str, Any]] = {}
    for class_name, source in sorted(sources.items()):
        if class_name == "MadScience":
            continue
        native_id = source["NativeId"]
        base = {
            "EnergyCost": source["EnergyCost"], "EnergyCostX": source["EnergyCostX"],
            "StarCost": source["StarCost"], "StarCostX": source["StarCostX"],
            "Type": source["Type"], "Target": source["Target"], "Rarity": source["Rarity"],
            "Keywords": source["Keywords"], "Tags": source["Tags"], "Variables": source["Variables"],
        }
        if class_name in recipe_by_class:
            components = current_components(class_name, source, recipe_by_class[class_name], runtime_specs)
            source_kind = "GeneratorCatalog"
        else:
            components = special_components(class_name)
            source_kind = "ReferenceExtension"
        card = {
            "CatalogId": f"native/{source['Pool'].lower()}/{native_id.lower()}",
            "NativeId": native_id,
            "ClassName": class_name,
            "Pool": source["Pool"],
            "SourceKind": source_kind,
            "ReferenceOnly": True,
            "GenerationEligible": False,
            "ShouldShowInLibrary": source["ShouldShowInLibrary"],
            "Title": {"zhHans": loc_entry(zhs, native_id, "title"), "en": loc_entry(eng, native_id, "title")},
            "DescriptionTemplate": {"zhHans": loc_entry(zhs, native_id, "description"),
                                    "en": loc_entry(eng, native_id, "description")},
            "Base": base,
            "Upgrade": source["Upgrade"],
            "Components": components,
        }
        cards.append(card)

    mad_source = sources["MadScience"]
    for variant, (card_type, target, components) in MAD_SCIENCE.items():
        native_id = f"MAD_SCIENCE_{screaming_snake(variant)}"
        base = {
            "EnergyCost": 1, "EnergyCostX": False, "StarCost": -1, "StarCostX": False,
            "Type": card_type, "Target": target, "Rarity": "Event", "Keywords": [], "Tags": [],
            "Variables": mad_source["Variables"],
        }
        upgrade = copy.deepcopy(mad_source["Upgrade"])
        upgrade["Result"]["Variables"] = {item["Id"]: item["BaseValue"] for item in mad_source["Variables"]}
        cards.append({
            "CatalogId": f"native/event/{native_id.lower()}", "NativeId": "MAD_SCIENCE",
            "ClassName": "MadScience", "Variant": variant, "Pool": "Event",
            "SourceKind": "ReferenceExtension", "ReferenceOnly": True, "GenerationEligible": False,
            "TemplateBindings": {"CardType": card_type,
                                 "Rider": variant.removeprefix(card_type)},
            "ShouldShowInLibrary": False,
            "Title": {"zhHans": loc_entry(zhs, "MAD_SCIENCE", "title"),
                      "en": loc_entry(eng, "MAD_SCIENCE", "title")},
            "DescriptionTemplate": {"zhHans": loc_entry(zhs, "MAD_SCIENCE", "description"),
                                    "en": loc_entry(eng, "MAD_SCIENCE", "description")},
            "Base": base, "Upgrade": upgrade, "Components": components,
        })

    # Build a deduplicated, parameterized component dictionary from all occurrences.
    for card in cards:
        for component in card["Components"]:
            component_id = component["ComponentId"]
            arguments = component.get("Arguments", {})
            zh, en = component_display_name(component_id)
            definition = all_components.setdefault(component_id, {
                "Id": component_id,
                "Name": {"zhHans": zh, "en": en},
                "ReferenceOnly": True,
                "GenerationEligible": False,
                "RuntimeContract": (runtime_contract(component["RuntimeSpec"])
                                    if "RuntimeSpec" in component
                                    else {"Kind": component_id}),
                "ExampleText": component.get("Text", {"zhHans": "", "en": ""}),
                "Parameters": {},
            })
            expected_contract = (runtime_contract(component["RuntimeSpec"])
                                 if "RuntimeSpec" in component
                                 else {"Kind": component_id})
            if definition["RuntimeContract"] != expected_contract:
                raise ValueError(f"component ID collision with different contracts: {component_id}")
            for name, value in arguments.items():
                source_kind = value.get("Source", "Literal") if isinstance(value, dict) else "Literal"
                parameter = definition["Parameters"].setdefault(name,
                    {"Name": name, "AcceptedSources": [], "SupportsNativeBindings": False})
                if isinstance(value, dict) and value.get("NativeBindings"):
                    parameter["SupportsNativeBindings"] = True
                accepted = parameter["AcceptedSources"]
                if source_kind not in accepted:
                    accepted.append(source_kind)

    component_output = []
    for definition in sorted(all_components.values(), key=lambda item: item["Id"]):
        definition["Parameters"] = [definition["Parameters"][key]
                                    for key in sorted(definition["Parameters"])]
        component_output.append(definition)

    keyword_names = {
        "None": ("无", "None"), "Exhaust": ("消耗", "Exhaust"), "Ethereal": ("虚无", "Ethereal"),
        "Innate": ("固有", "Innate"), "Unplayable": ("不可被打出", "Unplayable"),
        "Retain": ("保留", "Retain"), "Sly": ("奇巧", "Sly"), "Eternal": ("永恒", "Eternal"),
    }
    keywords = [{
        "Id": key, "Name": {"zhHans": value[0], "en": value[1]}, "ReferenceOnly": True,
        "GenerationEligible": False,
    } for key, value in keyword_names.items() if key != "None"]

    payload = {
        "SchemaVersion": 2,
        "GameVersion": "v111",
        "Purpose": "Complete non-multiplayer native-card reconstruction reference",
        "ReferenceOnly": True,
        "GenerationEligible": False,
        "Excluded": ["Cards whose MultiplayerConstraint is MultiplayerOnly", "Test mock cards"],
        "IncludedSpecialCategories": ["Status", "Curse", "Quest", "Event", "Token", "Deprecated"],
        "Cards": sorted(cards, key=lambda item: item["CatalogId"]),
    }
    component_payload = {
        "SchemaVersion": 2, "GameVersion": "v111", "ReferenceOnly": True,
        "GenerationEligible": False, "Components": component_output, "Keywords": keywords,
    }
    args.cards_output.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    args.components_output.write_text(json.dumps(component_payload, ensure_ascii=False, indent=2) + "\n",
                                      encoding="utf-8")
    print(f"wrote {len(cards)} cards, {len(component_output)} components, {len(keywords)} keywords")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--game-source", type=Path, default=DEFAULT_GAME_SOURCE)
    parser.add_argument("--localization", type=Path, default=DEFAULT_LOCALIZATION)
    parser.add_argument("--cards-output", type=Path, default=DATA / "native_reference_cards.json")
    parser.add_argument("--components-output", type=Path, default=DATA / "native_reference_components.json")
    build(parser.parse_args())


if __name__ == "__main__":
    main()
