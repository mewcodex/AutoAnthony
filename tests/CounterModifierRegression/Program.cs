using System.Reflection;
using System.Runtime.CompilerServices;
using AutoAnthony;
using ChaosCardGenerator;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

static void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
}

var assembly = typeof(ChaosCardModel).Assembly;
var executor = assembly.GetType("AutoAnthony.ChaosOperationExecutor", true)!;
var damageAndHits = AccessTools.Method(executor, "DamageAndHits");
var counter = AccessTools.Method(executor, "DynamicRepeatCount");
var executionState = Activator.CreateInstance(assembly.GetType("AutoAnthony.ChaosExecutionState", true)!)!;
var owner = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
var otherOwner = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
AccessTools.Field(typeof(Player), "_runPiles").SetValue(owner, Array.Empty<CardPile>());
AccessTools.Field(typeof(Player), "<Creature>k__BackingField").SetValue(owner,
    RuntimeHelpers.GetUninitializedObject(typeof(Creature)));
var card = (ChaosCardModel)RuntimeHelpers.GetUninitializedObject(typeof(ChaosRegentCard000));
AccessTools.Field(typeof(AbstractModel), "<IsMutable>k__BackingField").SetValue(card, true);
AccessTools.Field(typeof(CardModel), "_owner").SetValue(card, owner);
AccessTools.Field(typeof(CardModel), "_dynamicVars").SetValue(card, new DynamicVarSet([]));
var history = (List<CombatHistoryEntry>)AccessTools.Field(typeof(CombatHistory), "_entries")
    .GetValue(CombatManager.Instance.History)!;

GeneratorOperation Operation(string template)
{
    var atom = Enum.GetValues<GeneratedCharacter>().SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms)
        .First(atom => atom.Template == template);
    return new GeneratorOperation(template, atom.Scope, atom.ChineseText, new Dictionary<string, int>(),
        RequiresSingleTarget: atom.RequiresSingleTarget, RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom),
        LocalizedText: atom.LocalizedText);
}
void Definition(IReadOnlyList<GeneratorOperation> operations)
{
    var modifierIndex = operations.Count - 1;
    var upgrade = new CardUpgradePlan(1, [new(CardUpgradeKind.IncreaseNumber, modifierIndex, 1,
        ValueSlotId: OperationRuntimeSpecCompiler.UpgradeValueSlot(operations[modifierIndex]))], "", [], "");
    var definition = new GeneratedCard(1, GeneratedCardType.Attack, TargetMode.SingleEnemy,
        GeneratedRarity.Basic, "", [], operations, Character: GeneratedCharacter.Regent, Upgrade: upgrade);
    AccessTools.Field(typeof(ChaosCardModel), "_freeformDefinition").SetValue(card, definition);
}
void GeneratedEntries(int ownCount)
{
    history.Clear();
    foreach (var creator in Enumerable.Repeat(owner, ownCount).Concat(Enumerable.Repeat(otherOwner, 3)))
    {
        var entry = (CardGeneratedEntry)RuntimeHelpers.GetUninitializedObject(typeof(CardGeneratedEntry));
        AccessTools.Field(typeof(CardGeneratedEntry), "<Creator>k__BackingField").SetValue(entry, creator);
        history.Add(entry);
    }
}

var generatedBonus = Operation("R:BonusPerGeneratedCardThisCombat");
Check(OperationRuntimeSpecCompiler.TryReplacePrimaryExplicitFixedValue(generatedBonus, 1, out generatedBonus),
    "Numeric bonus fixture");
Definition([Operation("T:D"), Operation("R:ForEachGeneratedCardCombat"), generatedBonus]);
foreach (var upgraded in new[] { false, true })
{
    AccessTools.Field(typeof(CardModel), "_currentUpgradeLevel").SetValue(card, upgraded ? 1 : 0);
    for (var count = 0; count <= 10; count++)
    {
        GeneratedEntries(count);
        for (var read = 0; read < 3; read++)
        {
            var actual = ((decimal Damage, int Hits))damageAndHits.Invoke(null,
                [card, 5m, executionState, 1, 0])!;
            var expected = 5m + count * (upgraded ? 2 : 1);
            Check(actual == (expected, 1), $"Generated={count}, upgraded={upgraded}: expected {expected}x1, got {actual}");
            Check(card.ExtraDamage == 0, "Preview/play must not persist a recomputed count bonus");
        }
    }
}
Console.WriteLine("Generated-card damage: base 5 + count*(1/2), counts 0..10; foreign-player history excluded; repeated reads do not accumulate.");

AccessTools.Field(typeof(CardModel), "_currentUpgradeLevel").SetValue(card, 0);
Definition([Operation("T:D"), generatedBonus]);
for (var count = 0; count <= 10; count++)
{
    GeneratedEntries(count);
    var actual = ((decimal Damage, int Hits))damageAndHits.Invoke(null,
        [card, 5m, executionState, 1, 0])!;
    Check(actual == (5m + count, 1), $"Legacy standalone generated-card bonus: count={count}, got {actual}");
}
Console.WriteLine("Legacy standalone generated-card bonus also retains linear scaling.");

foreach (var (prefix, payoff) in new[] {
    ("D:ForEachOrb", "D:RepeatPerOrb"),
    ("NCR:ForEachEtherealPlayedCombat", "NCR:RepeatPerVoidPlayedCombat"),
    ("NCR:ForEachCardDrawnThisTurn", "NCR:DamagePerCardDrawnThisTurn"),
    ("NCR:ForEachOstyAttackThisTurn", "NCR:RepeatPerOstyAttackThisTurn"),
    ("R:ForEachSkillPlayedThisTurn", "R:RepeatPerSkillPlayedThisTurn"),
    ("R:ForEachStarGainedThisTurn", "R:RepeatPerStarGainedThisTurn"),
    ("R:ForEachGeneratedCardCombat", "R:BonusPerGeneratedCardThisCombat") })
{
    Definition([Operation("T:D"), Operation(prefix), Operation(payoff)]);
    for (var count = 0; count <= 10; count++)
        Check((int)counter.Invoke(null, [card, 2, count, count])! == count, $"{prefix}: counter squared");
    Console.WriteLine($"PASS {prefix} -> {payoff}: linked count consumed once");
}
history.Clear();
Console.WriteLine("Counter modifier regressions passed without starting the game or touching saves.");
