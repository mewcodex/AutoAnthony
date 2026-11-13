using System.Runtime.CompilerServices;
using AutoAnthony;
using ChaosCardGenerator;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}
static void Field(object target, Type owner, string name, object? value) =>
    AccessTools.Field(owner, name).SetValue(target, value);
var prototypes = Enum.GetValues<GeneratedCharacter>()
    .SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms).ToArray();
GeneratorOperation Op(string template, int? value = null, int trigger = -1)
{
    var atom = prototypes.First(atom => atom.Template == template);
    var operation = new GeneratorOperation(template, atom.Scope, atom.ChineseText,
        trigger < 0 ? new Dictionary<string, int>() : new Dictionary<string, int> { ["triggerIndex"] = trigger },
        RequiresSingleTarget: atom.RequiresSingleTarget, RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom),
        LocalizedText: atom.LocalizedText);
    if (value is { } number)
    {
        var slot = OperationRuntimeSpecCompiler.UpgradeValueSlot(operation)!;
        if (OperationRuntimeSpecCompiler.StaticLiteralValue(operation, slot) != number)
        {
            Check(OperationRuntimeSpecCompiler.TryReplaceFixedValue(operation, slot, number, out var changed),
                $"bind {template}/{slot}={number}");
            operation = changed;
        }
    }
    return operation;
}
static GeneratedCard Card(int cost, IReadOnlyList<GeneratorOperation> operations, int upgradedCost,
    CardUpgradeEffect effect, GeneratedCardType type = GeneratedCardType.Power) => new(cost, type,
        TargetMode.Other, GeneratedRarity.Rare, CardDescriptionRenderer.Render(operations), [], operations,
        Upgrade: new CardUpgradePlan(upgradedCost, [effect], "", [], ""),
        Character: GeneratedCharacter.Defect);
static ChaosCardModel Model(GeneratedCard definition)
{
    var model = (ChaosCardModel)RuntimeHelpers.GetUninitializedObject(typeof(ChaosDefectCard000));
    Field(model, typeof(AbstractModel), "<IsMutable>k__BackingField", true);
    Field(model, typeof(AbstractModel), "<Id>k__BackingField", ModelDb.GetId<ChaosDefectCard000>());
    Field(model, typeof(CardModel), "_canonicalInstance", model);
    Field(model, typeof(CardModel), "_temporaryStarCosts", new List<TemporaryCardCost>());
    Field(model, typeof(CardModel), "_keywordsAddedFromOutside", new HashSet<CardKeyword>());
    Field(model, typeof(CardModel), "_keywordsRemovedFromOutside", new HashSet<CardKeyword>());
    Field(model, typeof(ChaosCardModel), "_freeformDefinition", definition);
    return model;
}
var runtime = typeof(ChaosCardModel).Assembly.GetType("AutoAnthony.ChaosOperationExecutor", true)!;
var effectiveSpec = AccessTools.Method(runtime, "EffectiveRuntimeSpec");
static void Invalid(GeneratedCard definition)
{
    try { CardTemplateValidator.Validate(definition); }
    catch (InvalidOperationException) { return; }
    throw new InvalidOperationException("Expected invalid upgrade plan");
}

var block = Op("N:B", 8, 0);
var endOrbBlock = new[] { Op("A:turnEnd"), Op("D:ForEachUniqueOrb", trigger: 0), block };
var blockUpgrade = new CardUpgradeEffect(CardUpgradeKind.IncreaseNumber, 2, 1, ValueSlotId: "block");
var badCost = Card(3, endOrbBlock, 4, blockUpgrade);
Invalid(badCost);
var model = Model(badCost);
Check(model.EnergyCost.GetWithModifiers(CostModifiers.None) == 3, "original cost is 3");
model.UpgradeInternal();
Check(model.EnergyCost.GetWithModifiers(CostModifiers.None) == 3
      && model.DynamicVars["Block2"].BaseValue == 9, "stale cost-increase plan cannot make a 3-cost card cost 4");
var zeroBlock = Card(0, [Op("N:B", 5)], -1,
    new CardUpgradeEffect(CardUpgradeKind.ReduceCost, Delta: -1), GeneratedCardType.Skill);
Invalid(zeroBlock);
model = Model(zeroBlock);
model.UpgradeInternal();
Check(!model.EnergyCost.CostsX && model.EnergyCost.GetWithModifiers(CostModifiers.None) == 0,
    "zero-cost upgrade cannot become -1 or an X payment");

// v113 distinguishes the card's own upgrade keywords from external changes. Replaying OnUpgrade after
// deserialization must keep that provenance, or the native downgrade path retains upgrade-only Innate.
var keywordDefinition = Card(2, [Op("N:B", 8)], 2,
    new(CardUpgradeKind.GrantInnate), GeneratedCardType.Skill) with { Tags = [ChaosCardGenerator.CardTag.Exhaust] };
model = Model(keywordDefinition);
model.UpgradeInternal();
AccessTools.Method(typeof(ChaosCardModel), "AfterDeserialized").Invoke(model, []);
var outsideAdded = (HashSet<CardKeyword>)AccessTools.Field(typeof(CardModel), "_keywordsAddedFromOutside").GetValue(model)!;
Check(model.Keywords.Contains(CardKeyword.Innate) && !outsideAdded.Contains(CardKeyword.Innate),
    "cache reconstruction does not reclassify upgrade-only Innate as an external keyword");
model.AddKeyword(CardKeyword.Ethereal);
model.RemoveKeyword(CardKeyword.Exhaust);
AccessTools.Method(typeof(ChaosCardModel), "AfterDeserialized").Invoke(model, []);
Check(model.Keywords.Contains(CardKeyword.Ethereal) && !model.Keywords.Contains(CardKeyword.Exhaust),
    "cache reconstruction preserves effective external keyword additions and removals");

var startDraw = new[] { Op("A:turnStart"), Op("N:Draw", 2, 0) };
foreach (var kind in new[] { CardUpgradeKind.ReduceThreshold, CardUpgradeKind.ReduceNegativeNumber })
{
    var badDraw = Card(1, startDraw, 1, new(kind, 1, -1, ValueSlotId: "draw"));
    Invalid(badDraw);
    model = Model(badDraw);
    model.UpgradeInternal();
    var spec = (OperationRuntimeSpec)effectiveSpec.Invoke(null, [model, 1])!;
    Check(model.DynamicVars["Amount1"].BaseValue == 2 && spec.Values.First(v => v.Id == "draw").BaseValue == 2,
        $"{kind} cannot reduce Machine Learning's draw in text, DynamicVars or execution");
}
var negativeDraw = Card(1, startDraw, 1, new(CardUpgradeKind.IncreaseNumber, 1, -1, ValueSlotId: "draw"));
Invalid(negativeDraw);
model = Model(negativeDraw);
model.UpgradeInternal();
Check(model.DynamicVars["Amount1"].BaseValue == 2, "a negative reward upgrade is ignored at the shared boundary");

int AppliedValue(GeneratorOperation operation, CardUpgradeKind kind, int delta, string? slot = null)
{
    slot ??= OperationRuntimeSpecCompiler.UpgradeValueSlot(operation)!;
    var upgraded = CardUpgradeGenerator.ApplyEffectsToOperations([operation], [new(kind, 0, delta, ValueSlotId: slot)]);
    return OperationRuntimeSpecCompiler.StaticLiteralValue(upgraded[0], slot);
}
var selfDamage = Op("N:HP-", 2);
Check(AppliedValue(selfDamage, CardUpgradeKind.ReduceSelfDamage, -4) == 1,
    "an oversized stale self-damage reduction leaves one HP payment");
Check(AppliedValue(selfDamage, CardUpgradeKind.ReduceSelfDamage, 1) == 2
      && AppliedValue(selfDamage, CardUpgradeKind.IncreaseNumber, 1) == 2,
    "neither a reversed reduction nor a reward upgrade can increase self-damage");
Invalid(Card(1, [selfDamage, Op("N:B", 8)], 1,
    new(CardUpgradeKind.ReduceSelfDamage, 0, -2)));
foreach (var template in new[] { "D:LoseFocus", "D:LoseTemporaryFocus", "N:LoseDex", "D:LoseOrbSlots" })
{
    var payment = Op(template, 2);
    Check(AppliedValue(payment, CardUpgradeKind.ReduceNegativeNumber, 1) == 2
          && AppliedValue(payment, CardUpgradeKind.ReduceNegativeNumber, -4) == 1,
        $"{template} cannot worsen or lose its last payment through an invalid reduction plan");
}
var energyThreshold = Op("A:whenEnergySpent", 2);
Check(AppliedValue(energyThreshold, CardUpgradeKind.ReduceThreshold, 1) == 2
      && AppliedValue(energyThreshold, CardUpgradeKind.IncreaseNumber, 1) == 2
      && AppliedValue(energyThreshold, CardUpgradeKind.ReduceThreshold, -4) == 1,
    "threshold upgrades make a gate easier without lowering it to zero");
var wrongSlot = energyThreshold with { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(energyThreshold)
    with { Values = [.. OperationRuntimeSpecCompiler.GetOrCompile(energyThreshold).Values,
        new RuntimeValueSlot("draw", 2)] } };
Check(AppliedValue(wrongSlot, CardUpgradeKind.ReduceThreshold, -1, "draw") == 2,
    "a threshold upgrade cannot reduce a reward slot on the same operation");
var externalThreshold = energyThreshold with { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(energyThreshold)
    with { Trigger = new RuntimeTriggerSpec("external_test_threshold", "combat", "threshold") } };
Check(AppliedValue(externalThreshold, CardUpgradeKind.ReduceThreshold, -1) == 1,
    "external trigger metadata retains support for explicit threshold reductions");
var nextAttacks = Op("C:grantNextAttacksThisTurn", 2);
Check(AppliedValue(nextAttacks, CardUpgradeKind.ReduceThreshold, -1) == 2
      && AppliedValue(nextAttacks, CardUpgradeKind.IncreaseNumber, 1) == 2,
    "the number of next Attacks affected is not a reducible payment or an ordinary numeric upgrade");

var cappedDraw = Card(1, [Op("A:turnStart"), Op("N:Draw", 12, 0)], 1,
    new(CardUpgradeKind.IncreaseNumber, 1, 1, ValueSlotId: "draw"));
var projected = CardUpgradeGenerator.ApplyEffectsToOperations(cappedDraw.Operations, cappedDraw.Upgrade!.Effects);
Check(OperationRuntimeSpecCompiler.StaticLiteralValue(projected[1], "draw") == 12,
    "a new count cap cannot turn an existing Draw 12 upgrade into Draw 10");
model = Model(cappedDraw);
model.UpgradeInternal();
var cappedSpec = (OperationRuntimeSpec)effectiveSpec.Invoke(null, [model, 1])!;
Check(model.DynamicVars["Amount1"].BaseValue == 12 && cappedSpec.Values.First(v => v.Id == "draw").BaseValue == 12,
    "capped draw preview and live execution agree");
var curseRewards = new List<GeneratorOperation> { Op("N:Draw", 8) };
AccessTools.Method(typeof(ComponentAssemblyGenerator), "ApplyCurseStatusEasterEggCompensation")
    .Invoke(null, [curseRewards, 1.75d]);
Check(OperationRuntimeSpecCompiler.StaticLiteralValue(curseRewards[0], "draw") == 10,
    "curse compensation respects universal count caps before upgrade planning can encounter oversized rewards");
var normalDraw = Card(1, startDraw, 0, new(CardUpgradeKind.IncreaseNumber, 1, 1, ValueSlotId: "draw"));
model = Model(normalDraw);
model.UpgradeInternal();
Check(model.DynamicVars["Amount1"].BaseValue == 3 && model.EnergyCost.GetWithModifiers(CostModifiers.None) == 0,
    "ordinary positive draw and cost-reduction upgrades remain effective");
var clone = (ChaosCardModel)model.ClonePreservingMutability();
Check(clone.DynamicVars["Amount1"].BaseValue == 3 && clone.EnergyCost.GetWithModifiers(CostModifiers.None) == 0,
    "cloning an upgraded card does not apply the plan twice");
AccessTools.Method(typeof(ChaosCardModel), "AfterDeserialized").Invoke(clone, []);
Check(clone.DynamicVars["Amount1"].BaseValue == 3 && clone.EnergyCost.GetWithModifiers(CostModifiers.None) == 0,
    "cache restoration applies the upgrade exactly once");

var death = Op("NCR:KillOsty");
var summon = Op("NCR:Summon", 5);
var ostyAttack = Op("NCR:OstyAllDamage", 8);
Check(!CardEffectRules.HasValidOstyLifecycleOrdering([death, ostyAttack]), "reject death followed by Osty attack");
Check(!CardEffectRules.HasValidOstyLifecycleOrdering([summon, death])
      && !CardEffectRules.HasValidOstyLifecycleOrdering([death, summon]), "reject Summon/death in either order in one resolution");
Check(CardEffectRules.HasValidOstyLifecycleOrdering([ostyAttack, death]), "allow Osty attack before death");
Check(!CardEffectRules.HasValidOstyLifecycleOrdering([Op("NCR:IfOstyAlive"),
        Op("NCR:KillOsty", trigger: 0), Op("NCR:OstyAllDamage", trigger: 0)]),
    "conditions cannot hide a death-before-attack conflict");
Check(CardEffectRules.HasValidOstyLifecycleOrdering([Op("A:turnStart"), Op("NCR:Summon", trigger: 0), death]),
    "future Summon is distinct from an immediate death");
Check(!CardEffectRules.HasValidOstyLifecycleOrdering([Op("A:turnStart"),
        Op("NCR:Summon", trigger: 0), Op("NCR:KillOsty", trigger: 0)]),
    "reject Summon/death in a shared future trigger");
var sacrifice = CharacterComponentCatalogs.Get(GeneratedCharacter.Necrobinder).Recipes.Single(recipe => recipe.Id == "Sacrifice");
Check(CardEffectRules.HasValidOstyLifecycleOrdering(sacrifice.Atoms.Select((atom, index) =>
        new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
            sacrifice.TriggerOwners[index] < 0 ? new Dictionary<string, int>()
                : new Dictionary<string, int> { ["triggerIndex"] = sacrifice.TriggerOwners[index] },
            RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom))).ToArray()),
    "native Sacrifice's post-death Max HP block remains legal");
var focusLoss = Op("D:LoseFocus", 3);
var reducedLoss = Card(1, [Op("A:turnStart"), focusLoss with
    { Parameters = new Dictionary<string, int> { ["triggerIndex"] = 0 } }], 1,
    new(CardUpgradeKind.IncreaseNumber, 1, -1,
        ValueSlotId: OperationRuntimeSpecCompiler.UpgradeValueSlot(focusLoss)));
model = Model(reducedLoss);
model.UpgradeInternal();
Check(model.DynamicVars["Amount1"].BaseValue == 2,
    "signed native upgrades reducing a negative payment remain effective");
foreach (var seed in Enumerable.Range(0, 300))
{
    var definition = normalDraw with { Upgrade = null };
    var plan = CardUpgradeGenerator.Generate(definition, new Random(seed));
    Check(plan.UpgradedCost >= 0 && plan.UpgradedCost <= definition.Cost
          && CardUpgradeGenerator.ApplyEffectsToOperations(definition.Operations, plan.Effects)
              .Where(op => op.Template == "N:Draw").All(op =>
                  OperationRuntimeSpecCompiler.StaticLiteralValue(op, "draw") >= 2),
        $"Machine Learning upgrade seed {seed}");
}
var soulOperations = new[] { Op("NCR:Summon", 3), Op("NCR:CreateSoulInDraw", 1) };
var soulUpgrade = new CardUpgradeEffect(CardUpgradeKind.IncreaseNumber, 0, 1, ValueSlotId: "amount");
var soulCard = Card(1, soulOperations, 0, soulUpgrade, GeneratedCardType.Skill);
model = Model(soulCard);
_ = model.DynamicVars;
model.UpgradeInternal();
Check(model.EnergyCost.GetWithModifiers(CostModifiers.None) == 0 && model.DynamicVars["Amount0"].BaseValue == 4
      && model.DynamicVars["Amount1"].BaseValue == 1, "reducing cost and upgrading Summon cannot consume a Soul count");
var costAtSoulIndex = Card(1, soulOperations, 0,
    new(CardUpgradeKind.ReduceCost, 1, -1, ValueSlotId: "amount"), GeneratedCardType.Skill);
model = Model(costAtSoulIndex);
model.UpgradeInternal();
Check(model.DynamicVars["Amount1"].BaseValue == 1, "a stale cost choice bound to a producer cannot reduce its amount");
var badStarCost = soulCard with { StarCost = 0, Upgrade = soulCard.Upgrade! with { UpgradedStarCost = -1 } };
model = Model(badStarCost);
model.UpgradeInternal();
Check(model.BaseStarCost == 0, "a stale Star-cost upgrade cannot hide its cost by changing zero to minus one");
var exhausted = Op("C:ifCardExhaustedThisTurn");
Check(CardEffectRules.HasAttackClassifyingDamage([exhausted, Op("T:D", 2, 0)]),
    "immediate conditional damage classifies as Attack");
Check(!CardEffectRules.HasAttackClassifyingDamage([Op("C:NextTurnStart"), Op("T:D", 2, 0)]),
    "future turn-start damage does not classify its host as Attack");
var refresh = AccessTools.Method(typeof(ChaosCardModel), "RefreshDefinitionCacheIfChanged");
model = Model(soulCard with { Cost = -1, StarCost = 3 });
Check(model.EnergyCost.CostsX && model.BaseStarCost == 3, "prime cached X and Star costs without building DynamicVars");
Field(model, typeof(ChaosCardModel), "_freeformDefinition", soulCard with { StarCost = 1 });
Check((bool)refresh.Invoke(model, null)! && !model.EnergyCost.CostsX
      && model.EnergyCost.GetWithModifiers(CostModifiers.None) == 1 && model.BaseStarCost == 1,
    "cost-only caches refresh even when no dynamic variable table was ever created");
model = Model(Card(1, [Op("T:D", 3), Op("N:B", 3)], 1,
    new(CardUpgradeKind.IncreaseNumber, 0, 1, ValueSlotId: "damage")));
_ = model.DynamicVars;
model.EnergyCost.SetCustomBaseCost(-1);
Field(model, typeof(ChaosCardModel), "_freeformDefinition", soulCard);
Check((bool)refresh.Invoke(model, null)! && !model.EnergyCost.CostsX
      && model.EnergyCost.GetWithModifiers(CostModifiers.None) == 1
      && model.DynamicVars.ContainsKey("Amount0") && model.DynamicVars.ContainsKey("Amount1")
      && !model.DynamicVars.ContainsKey("Damage0"), "definition replacement refreshes stale variable names and cost caches before formatting");
model.UpgradeInternal();
Check(model.DynamicVars["Amount0"].BaseValue == 4 && model.DynamicVars["Amount1"].BaseValue == 1,
    "refreshed definitions still apply each upgrade exactly once");
var alternate = soulCard with { Operations = [Op("NCR:Summon", 5), Op("NCR:CreateSoulInDraw", 2)] };
Field(model, typeof(ChaosCardModel), "_freeformDefinition", alternate);
Check((bool)refresh.Invoke(model, null)! && model.DynamicVars["Amount0"].BaseValue == 6
      && model.DynamicVars["Amount1"].BaseValue == 2 && !(bool)refresh.Invoke(model, null)!,
    "an already-upgraded rebound card refreshes once without double-applying numeric upgrades");
var locale = (MegaCrit.Sts2.Core.Localization.LocManager)RuntimeHelpers.GetUninitializedObject(
    typeof(MegaCrit.Sts2.Core.Localization.LocManager));
AccessTools.Field(typeof(MegaCrit.Sts2.Core.Localization.LocManager), "<Instance>k__BackingField").SetValue(null, locale);
Field(locale, typeof(MegaCrit.Sts2.Core.Localization.LocManager), "<CultureInfo>k__BackingField",
    System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
var textTable = new MegaCrit.Sts2.Core.Localization.LocTable("cards", new Dictionary<string, string>());
Field(locale, typeof(MegaCrit.Sts2.Core.Localization.LocManager), "_tables",
    new Dictionary<string, MegaCrit.Sts2.Core.Localization.LocTable> { ["cards"] = textTable });
AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocManager), "LoadLocFormatters").Invoke(locale, null);
model = Model(Card(1, [Op("T:D", 3), Op("N:B", 3)], 1,
    new(CardUpgradeKind.IncreaseNumber, 0, 1, ValueSlotId: "damage")));
_ = model.DynamicVars;
var formattedDefinition = Card(1, [Op("T:Apply", 2), Op("N:E", 1), Op("N:B", 4), Op("T:D", 6)], 0,
    new(CardUpgradeKind.ReduceCost, Delta: -1), GeneratedCardType.Attack);
Field(model, typeof(ChaosCardModel), "_freeformDefinition", formattedDefinition);
refresh.Invoke(model, null);
foreach (var raw in new[] { "使该敌人获得{Amount0:diff()}点力量。",
             "获得{Energy1:energyIcons()}。造成{Damage3:diff()}点伤害3次。" })
{
    textTable.MergeWith(new Dictionary<string, string> { ["probe"] = raw });
    var text = new MegaCrit.Sts2.Core.Localization.LocString("cards", "probe");
    model.DynamicVars.AddTo(text);
    ((MegaCrit.Sts2.Core.Localization.DynamicVars.EnergyVar)model.DynamicVars["Energy1"]).ColorPrefix = "ironclad";
    var formatted = text.GetFormattedText();
    Check(!formatted.Contains('{') && !formatted.Contains('}'), "native formatter resolves the reported template: " + formatted);
}
Console.WriteLine("Upgrade lifecycle regression passed. No game or user saves were modified.");
