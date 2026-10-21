using ChaosCardGenerator;
var samples = args.Length > 0 ? int.Parse(args[0]) : 3000;
foreach (var rarity in new[]{GeneratedRarity.Common, GeneratedRarity.Uncommon, GeneratedRarity.Rare}) {
 var gen = new ComponentAssemblyGenerator(new Random(20261004+(int)rarity), GeneratedCharacter.Defect, balancedValues:true);
 var scrape=new List<int>(); var reboot=new List<int>(); var growth=0;
 for(int i=0;i<samples;i++) {
  var card=gen.Generate(rarity);
  for(int j=0;j<card.Operations.Count;j++) {
   var op=card.Operations[j];
   int Number(GeneratorOperation o)=>OperationRuntimeSpecCompiler.GetOrCompile(o).Values.FirstOrDefault(v=>v.Id=="draw" || v.Id=="cards" || v.Id=="amount")?.BaseValue ?? 0;
   if(op.Template=="D:DrawAndDiscardNonZero")scrape.Add(Number(op));
   if(op.Template=="D:ShuffleAllUnexhaustedIntoDraw" && j+1<card.Operations.Count) {
    reboot.Add(Number(card.Operations[j+1]));
    if(args.Contains("--examples") && reboot.Count<=12) Console.WriteLine($"Reboot: {card}");
   }
   if(op.Template=="D:IncreaseThisCardBlockRun")growth++;
  }
 }
 string Summary(List<int> values)=>values.Count==0?"n=0":$"n={values.Count} avg={values.Average():F3} hist={string.Join(',',values.GroupBy(x=>x).OrderBy(x=>x.Key).Select(g=>$"{g.Key}:{g.Count()}"))}";
 Console.WriteLine($"{rarity} cards={samples} Scrape {Summary(scrape)} Reboot {Summary(reboot)} Growth={growth}");
}
