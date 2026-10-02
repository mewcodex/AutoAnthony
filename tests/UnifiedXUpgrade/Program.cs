using ChaosCardGenerator;
using System.Text.Json;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
var prototypes = CardTinkeringApi.GetComponentPrototypes().Select(p => p.Operation).ToArray();
GeneratorOperation WithAmount(string template, int value)
{
    var operation = prototypes.First(o => o.Template == template);
    var slot = OperationRuntimeSpecCompiler.UpgradeValueSlot(operation)!;
    Check(OperationRuntimeSpecCompiler.TryReplaceFixedValue(operation, slot, value, out var replaced), template);
    return replaced;
}
var operations = new[] { WithAmount("T:D", 2), WithAmount("N:B", 2), WithAmount("N:E", 2) };
var card = new GeneratedCard(2, GeneratedCardType.Attack, TargetMode.SingleEnemy, GeneratedRarity.Rare,
    CardDescriptionRenderer.Render(operations), [], operations, Character:GeneratedCharacter.Ironclad);
card = SpecialXCardConverter.Convert(card, new Random(1), SpecialXGenerationMode.Forced);
Check(card.Cost == -1 && card.Operations.All(OperationRuntimeSpecCompiler.ValueUsesX), "Convert every matching reward, including Energy");
var upgrade = new CardUpgradeEffect(CardUpgradeKind.IncreaseAllX, Delta: 2);
var loaded = JsonSerializer.Deserialize<CardUpgradeEffect>(JsonSerializer.Serialize(upgrade))!;
Check(CardUpgradeGenerator.ExpandEffects(card.Operations, [loaded]).Count() == 3, "Persist one unified choice");
var after = CardUpgradeGenerator.ApplyEffectsToOperations(card.Operations, [loaded]);
Check(after.All(o => OperationRuntimeSpecCompiler.GetOrCompile(o).Values.Any(v => v.Source == "special_x" && v.Offset == 2)), "Upgrade all positive X slots");
var chinese = CardDescriptionRenderer.Render(after);
Check(chinese.Contains("X+2{energyPrefix:energyIcons(1)}") && !chinese.Contains("energyIcons(X"), "Symbolic energy must use numeric X+N notation");
Check(EnglishCardDescriptionRenderer.Render(after).Contains("X+2"), "English X+N projection");
var negative = card.Operations[1] with { Template = "D:LoseFocus" };
Check(!CardUpgradeGenerator.ExpandEffects([negative], [upgrade]).Any(), "Exclude negative values");
var probe = card with { Operations = card.Operations.Take(2).ToArray() };
var plans = Enumerable.Range(0, 300).Select(seed => CardUpgradeGenerator.Generate(probe, new Random(seed))).ToArray();
var unified = plans.Count(p => p.Effects.Any(e => e.Kind == CardUpgradeKind.IncreaseAllX));
Check(unified >= 210, "Prefer unified X upgrade when affordable");
Check(plans.Any(p => p.Effects.Any(e => e.Kind == CardUpgradeKind.IncreaseAllX && e.Delta > 1)), "Allow offsets greater than one");
Check(plans.All(p => p.Effects.All(e => e.Kind != CardUpgradeKind.IncreaseNumber)), "No isolated X upgrade choices");
Check(plans.All(p => p.UpgradedCost == -1), "Never upgrade the card payment X");
var ordinaryX = prototypes.First(o => o.Template == "T:DX");
var ordinaryAfter = CardUpgradeGenerator.ApplyEffectsToOperations([ordinaryX], [upgrade]).Single();
var beforeSpec = OperationRuntimeSpecCompiler.GetOrCompile(ordinaryX);
var afterSpec = OperationRuntimeSpecCompiler.GetOrCompile(ordinaryAfter);
Check(afterSpec.Values.Where(v => v.Source == "fixed").SequenceEqual(beforeSpec.Values.Where(v => v.Source == "fixed")), "Do not change ordinary fixed damage");
Check(afterSpec.Values.Any(v => v.Source == "energy_x" && v.Offset == 2), "Ordinary X hit count upgrades too");
Check(EnglishCardDescriptionRenderer.Render([ordinaryAfter]).Contains("X+2"), "Ordinary X English projection");
Check(OperationRuntimeSpecCompiler.LegacyXOffset("X+3") == 3, "Legacy X+N offset");
Console.WriteLine($"Unified-X regression passed ({unified}/300 affordable probes).");
foreach (var (template, chineseBase) in new[] {
    ("N:RandomD", "随机对敌人造成2点伤害2次。"),
    ("N:AllD", "对所有敌人造成2点伤害2次。"),
    ("T:D", "造成2点伤害2次。") })
{
    var op = new GeneratorOperation(template, template == "T:D" ? OperationScope.SingleEnemyOnly : OperationScope.NonTargeted,
        chineseBase, new Dictionary<string,int>(), RequiresSingleTarget: template == "T:D");
    var host = card with { Cost=2, Operations=[op], Upgrade=null };
    var both = SpecialXCardConverter.Convert(host, new Random(3), SpecialXGenerationMode.Forced);
    foreach (var delta in new[] {1,2,3,4})
    {
        var result = CardUpgradeGenerator.ApplyEffectsToOperations(both.Operations,
            [new(CardUpgradeKind.IncreaseAllX, Delta:delta)]);
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(result[0]);
        Check(spec.Values.Count(v=>v.Source=="special_x" && v.Offset==delta)==2, "Both damage and hits must increase");
        var english = EnglishCardDescriptionRenderer.Render(result);
        Check(english.Split("X+"+delta).Length==3, "Both English X slots must render independently: "+english);
        var rebuilt = OperationRuntimeSpecCompiler.CompileLegacy(result[0] with {RuntimeSpec=null, LocalizedText=null});
        Check(rebuilt.Values.Count(v=>v.Source=="special_x" && v.Offset==delta)==2, "Legacy rebuild retains both X slots and offsets");
    }
}
Console.WriteLine("Dual-X single/random/all-enemy damage regression passed.");
var starCard = SpecialXCardConverter.Convert(card with {
    Cost=0, StarCost=2, HasStarCostX=false, Character=GeneratedCharacter.Regent,
    Operations=operations, Upgrade=null }, new Random(1), SpecialXGenerationMode.Forced);
Check(starCard.Cost==0 && starCard.HasStarCostX, "Preserve fixed energy cost on Star-X conversion");
var starAfter = CardUpgradeGenerator.ApplyEffectsToOperations(starCard.Operations, [upgrade]);
Check(starAfter.All(o=>OperationRuntimeSpecCompiler.GetOrCompile(o).Values.Any(v=>v.Source=="special_x" && v.Offset==2)), "Star-X uses the same unified upgrade");
var damageSlot = beforeSpec.Values.First(v=>v.Id=="damage");
var mixedSpec = beforeSpec with { Values = [damageSlot with {Source="energy_x", BaseValue=0, Offset=1}, new("hits",0,"star_x",3)] };
var mixed = ordinaryX with { RuntimeSpec=mixedSpec,
    LocalizedText=new("造成[[damage]]点伤害[[hits]]次。", "Deal [[damage]] damage [[hits]] times.") };
var mixedAfter = CardUpgradeGenerator.ApplyEffectsToOperations([mixed],[upgrade]).Single();
var mixedValues = OperationRuntimeSpecCompiler.GetOrCompile(mixedAfter).Values;
Check(mixedValues[0].Offset==3 && mixedValues[1].Offset==5, "Add the same delta without flattening existing offsets or resource sources");
Console.WriteLine("Energy-X/Star-X and distinct existing offsets passed.");
var auditGain = typeof(CardUpgradeGenerator).GetMethod("EstimatedUpgradeGain",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
double Gain(GeneratedCard host, int delta) => (double)auditGain.Invoke(null,
    [host with { Upgrade=new CardUpgradePlan(host.Cost,
        [new(CardUpgradeKind.IncreaseAllX, Delta:delta)], "", [], "") }])!;
var singleDamage = card with { Operations=[card.Operations[0]] };
var singleEnergy = card with { Operations=[card.Operations[2]] };
var doubleX = card with { Operations=[mixed with {RuntimeSpec=mixedSpec with {
    Values=mixedSpec.Values.Select(v=>v with {Offset=0}).ToArray()}}] };
double previous=0;
for (var delta=1; delta<=4; delta++)
{
    var damageGain=Gain(singleDamage,delta);
    var energyGain=Gain(singleEnergy,delta);
    var compoundGain=Gain(doubleX,delta);
    Check(Math.Abs(damageGain-100*delta)<0.01,"Single-hit X damage upgrade must scale with n");
    Check(Math.Abs(energyGain-650*delta)<0.01,"X energy upgrade must scale with n");
    // At X=3: (3+n)^2 - 3^2 damage, plus n additional multi-hit adaptation points.
    Check(Math.Abs(compoundGain-100*(7*delta+delta*delta))<0.01,"Dual-X upgrade must include the cross term and multi-hit value at X=3");
    Check(compoundGain>previous,"Compound marginal gain must increase");
    previous=compoundGain;
    Console.WriteLine($"X+{delta} upgrade value: damage={damageGain}, energy={energyGain}, dualDamageAndHits={compoundGain}");
}
