using ChaosCardGenerator;
using System.Reflection;

var balance = typeof(ComponentAssemblyGenerator).Assembly.GetType("ChaosCardGenerator.EffectBalanceModel")!;
var centerMethod = balance.GetMethod("TryBudgetDrivenNumericCenter", BindingFlags.Static | BindingFlags.NonPublic)!;
ComponentAtom Atom(string template) => Enum.GetValues<GeneratedCharacter>()
    .SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms).First(atom=>atom.Template==template);
GeneratorOperation Operation(string template) {
    var atom=Atom(template);
    return new GeneratorOperation(template, atom.Scope, atom.ChineseText, new Dictionary<string,int>(),
        RuntimeSpec:OperationRuntimeSpecCompiler.GetOrCompile(atom), LocalizedText:atom.LocalizedText);
}
int Center(string template, IReadOnlyList<GeneratorOperation> previous, double budget) {
    object[] arguments=[Atom(template),previous,budget,0];
    if (!(bool)centerMethod.Invoke(null,arguments)!) throw new Exception($"Missing numeric center: {template}");
    return (int)arguments[3];
}
var host=Operation("T:D");
foreach(var (prefix,payoff) in new[] {
    ("R:ForEachGeneratedCardCombat","R:BonusPerGeneratedCardThisCombat"),
    ("R:ForEachStarCostCard","R:BonusPerStarCostCardInHand"),
    ("NCR:ForEachOstyAttackCard","NCR:DamagePerOstyAttackCard"),
    ("NCR:ForEachExhaustedSoul","NCR:DamagePerExhaustedSoul"),
    ("NCR:ForEachCardDrawnThisTurn","NCR:DamagePerCardDrawnThisTurn") }) {
    var center=Center(payoff,[host,Operation(prefix)],1900d);
    if(center!=19 || Center(payoff,[host,host,Operation(prefix)],1900d)!=center)
        throw new Exception($"{payoff}: cadence/host normalization duplicated, center={center}");
}
if(Center("M:DamagePerCardDrawnCombat",[host],1900d)!=1)
    throw new Exception("Native high-count Murder coefficient must retain intrinsic cadence pricing");
foreach(var template in new[]{"R:PlaySelectedSkillMultipleTimes","D:AutoPlayRandomAttackFromDraw",
    "I:AutoPlayRandomAttackFromHand"}) {
    if(Center(template,[],5500d)<2 || Center(template,[],5500d)<=Center(template,[],750d))
        throw new Exception($"{template}: higher budget does not increase play counts");
}
Console.WriteLine("Numeric center regressions passed: no double cadence/host division; intrinsic high counts remain priced; autoplay counts scale with budget.");
var thresholdAtom=CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad).Atoms.First(atom =>
    OperationRuntimeSpecCompiler.GetOrCompile(atom) is {Opcode:"modify_block", Variant:"strength_scaled"});
object[] thresholdArguments=[thresholdAtom,new[]{host},1900d,0];
if((bool)centerMethod.Invoke(null,thresholdArguments)!)
    throw new Exception("A reward coefficient was substituted into a threshold slot");
GeneratorOperation Amount(GeneratorOperation operation, int value) {
    if(OperationRuntimeSpecCompiler.PrimaryStaticLiteralValue(operation,-1)==value) return operation;
    if(!OperationRuntimeSpecCompiler.TryReplacePrimaryExplicitFixedValue(operation,value,out var result))
        throw new Exception($"Missing fixture value: {operation.Template}");
    return result;
}
var effectValue=balance.GetMethod("EstimatedEffectValue",BindingFlags.NonPublic|BindingFlags.Static,
    [typeof(GeneratorOperation)])!;
foreach(var template in new[]{"D:AutoPlayRandomAttackFromDraw","I:AutoPlayRandomAttackFromHand"}) {
    var one=(int)effectValue.Invoke(null,[Amount(Operation(template),1)])!;
    for(var count=2;count<=10;count++)
        if((int)effectValue.Invoke(null,[Amount(Operation(template),count)])! != one*count)
            throw new Exception($"{template}: quantity {count} is not fully priced");
}
var scaleMethod=typeof(ComponentAssemblyGenerator).GetMethod("ScaleWholeCardNumericRewardsCore",
    BindingFlags.NonPublic|BindingFlags.Static)!;
var playPackage=new List<GeneratorOperation>{Amount(host,25),Amount(Operation("R:PlaySelectedSkillMultipleTimes"),2)};
if(!(bool)scaleMethod.Invoke(null,[playPackage,0.96d,false,GeneratedCharacter.Regent,false])!
    || OperationRuntimeSpecCompiler.PrimaryStaticLiteralValue(playPackage[1],0)!=2)
    throw new Exception("A mild correction collapsed two free plays to one");
var bonusPackage=new List<GeneratorOperation>{Amount(host,10),Operation("R:ForEachGeneratedCardCombat"),
    Amount(Operation("R:BonusPerGeneratedCardThisCombat"),2)};
if(!(bool)scaleMethod.Invoke(null,[bonusPackage,0.96d,false,GeneratedCharacter.Regent,false])!
    || OperationRuntimeSpecCompiler.PrimaryStaticLiteralValue(bonusPackage[2],0)!=2
    || OperationRuntimeSpecCompiler.PrimaryStaticLiteralValue(bonusPackage[0],0)!=9)
    throw new Exception("Stalled rounding did not choose the single closest-valued step");
Console.WriteLine("Discrete fitting regressions passed: two free plays survive mild correction; stalled fit adjusts one cheapest step rather than every coefficient.");
if(args.Contains("--regression")) return;
var samples = args.Length > 0 ? int.Parse(args[0]) : 400;
var watched = new HashSet<string>(StringComparer.Ordinal) {
    "R:DamageUpWhenDrawn", "R:BonusPerGeneratedCardThisCombat", "M:DamagePerCardDrawnCombat",
    "NCR:DamagePerCardDrawnThisTurn", "NCR:DamagePerOstyAttackCard", "NCR:DamagePerExhaustedSoul",
    "R:BonusPerStarCostCardInHand", "R:PlaySelectedSkillMultipleTimes", "D:AutoPlayRandomAttackFromDraw",
    "I:AutoPlayRandomAttackFromHand", "I:PlayTopXCards", "D:ReplayEventCard"
};
var rows = new Dictionary<(GeneratedCharacter Character, GeneratedRarity Rarity, string Template), List<int>>();
foreach (var character in args.Contains("--focus")
    ? new[] {GeneratedCharacter.Regent,GeneratedCharacter.Defect}
    : new[] {GeneratedCharacter.Ironclad, GeneratedCharacter.Silent, GeneratedCharacter.Regent,
        GeneratedCharacter.Necrobinder, GeneratedCharacter.Defect})
foreach (var rarity in args.Contains("--focus")
    ? new[] {GeneratedRarity.Uncommon,GeneratedRarity.Rare}
    : new[] {GeneratedRarity.Basic, GeneratedRarity.Common, GeneratedRarity.Uncommon, GeneratedRarity.Rare})
{
    var generator = new ComponentAssemblyGenerator(new Random(20261012 + 31*(int)character + (int)rarity), character,
        balancedValues:true);
    for (var i = 0; i < samples; i++)
    {
        var card = generator.Generate(rarity);
        foreach (var op in card.Operations.Where(op => watched.Contains(op.Template)))
        {
            var slot = OperationRuntimeSpecCompiler.ExplicitFixedValueSlots(op).FirstOrDefault();
            if (slot is null) continue;
            var key = (character, rarity, op.Template);
            if (!rows.TryGetValue(key, out var values)) rows[key] = values = [];
            values.Add(slot.BaseValue + slot.Offset);
        }
    }
    Console.WriteLine($"Completed {character}/{rarity}: {samples} balanced cards");
}
foreach (var row in rows.OrderBy(row => row.Key.Character).ThenBy(row => row.Key.Rarity).ThenBy(row => row.Key.Template))
    Console.WriteLine($"{row.Key}: n={row.Value.Count} mean={row.Value.Average():F2} hist=" +
        string.Join(',', row.Value.GroupBy(x=>x).OrderBy(g=>g.Key).Select(g=>$"{g.Key}:{g.Count()}")));
