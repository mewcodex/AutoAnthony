using ChaosCardGenerator;
using System.Reflection;

if (args.Contains("--fixed-99-restrictions"))
{
    typeof(RandomCardGenerator).Assembly.GetType("ChaosCardGenerator.LongDurationStatusVariant", true)!
        .GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
    var total = 0;
    var variants = 0;
    foreach (var character in new[] { GeneratedCharacter.Ironclad, GeneratedCharacter.Silent })
    for (var seed = 0; seed < 8; seed++)
    {
        var generator = new RandomCardGenerator(character, 99143 + seed * 104729, balancedValues: true);
        foreach (var rarity in Enum.GetValues<GeneratedRarity>())
        for (var i = 0; i < (rarity switch { GeneratedRarity.Basic => 10, GeneratedRarity.Common => 20,
                 GeneratedRarity.Uncommon => 35, GeneratedRarity.Rare => 25, _ => 2 }); i++)
        {
            var card = generator.Generate(rarity);
            total++;
            if (!card.Operations.Any(operation => OperationRuntimeSpecCompiler.GetOrCompile(operation)
                    .Flags.Contains("fixed_99_enemy_status"))) continue;
            variants++;
            if (card.Type != GeneratedCardType.Power && !card.Tags.Contains(CardTag.Exhaust))
                throw new InvalidOperationException("99-stack non-Power generated without Exhaust.");
            if (card.Upgrade is { } upgrade && GeneratedCardTagPolicy.RemovedKeywords(upgrade).Contains(CardTag.Exhaust))
                throw new InvalidOperationException("99-stack upgrade removed Exhaust.");
            foreach (var operation in card.Operations.Where(operation =>
                         OperationRuntimeSpecCompiler.GetOrCompile(operation).Flags.Contains("fixed_99_enemy_status")))
            {
                var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
                if (!CardEffectRules.IsRestrictedEffect(operation)
                    || !spec.Flags.Contains(ComponentSemanticFlags.Restricted))
                    throw new InvalidOperationException("99-stack restriction metadata missing.");
                // Verify older serialized variants without the newly emitted general restriction flag.
                var legacy = operation with { RuntimeSpec = spec with
                    { Flags = spec.Flags.Where(flag => flag != ComponentSemanticFlags.Restricted).ToArray() } };
                if (!CardEffectRules.IsRestrictedEffect(legacy))
                    throw new InvalidOperationException("Legacy 99-stack snapshot bypassed restriction.");
            }
        }
    }
    if (variants == 0) throw new InvalidOperationException("No 99-stack variants exercised by fixed seeds.");
    Console.WriteLine($"99-stack restriction regressions passed: {total} balanced cards, {variants} variants; Exhaust/Power, upgrade and legacy metadata checks passed.");
    return;
}

if (args.Contains("--exhaust-frequency"))
{
    var total = 0; var exhaust = 0; var basic = 0; var tags = 0;
    for (var seed = 0; seed < 20; seed++)
    {
        var generator = new RandomCardGenerator(GeneratedCharacter.Ironclad, 67134 + seed * 104729,
            balancedValues: true);
        foreach (var rarity in Enum.GetValues<GeneratedRarity>())
        for (var i = 0; i < (rarity switch { GeneratedRarity.Basic => 10, GeneratedRarity.Common => 20,
            GeneratedRarity.Uncommon => 35, GeneratedRarity.Rare => 25, _ => 2 }); i++)
        {
            var card = generator.Generate(rarity);
            var active = card.Operations.Any(op => {
                var spec = OperationRuntimeSpecCompiler.GetOrCompile(op);
                return spec.Opcode == "exhaust_card" || spec.Variant is "i_playtopcardandexhaust"
                    or "i_exhaustrandomattack" or "d_exhaustallstatuses";
            });
            total++;
            if (active) { exhaust++; if (rarity == GeneratedRarity.Basic) basic++; }
            if (card.Tags.Contains(CardTag.Exhaust)) tags++;
        }
    }
    Console.WriteLine($"20 balanced Ironclad pools: cards={total}, activeExhaust={exhaust}, basicActiveExhaust={basic}, ExhaustKeyword={tags}");
    return;
}

if (args.Contains("--exhaust-upgrade-rules"))
{
    var upgrade = new OperationRuntimeSpec(1,"upgrade_card","referenced","referenced_card","none","none","any",[],[]);
    foreach (var kind in new[] { "self_exhausted", "card_exhausted", "for_each_exhausted_card",
        "for_each_exhausted_non_attack", "for_each_exhausted_status" })
    {
        var trigger = new OperationRuntimeSpec(1,"trigger","","self","none","none","any",[],[],
            Trigger: new(kind,"combat"));
        if (CardEffectRules.IsValidTriggerPayoff(trigger, upgrade)) throw new Exception(kind);
    }
    var exhaustSpec = new OperationRuntimeSpec(1,"exhaust_card","selected","selected_card","hand","none","any",[],[]);
    var operations = new[] {
        new GeneratorOperation("N:Exhaust",OperationScope.NonTargeted,"",new Dictionary<string,int>(),RuntimeSpec:exhaustSpec),
        new GeneratorOperation("N:UpgradeReferencedCard",OperationScope.NonTargeted,"",new Dictionary<string,int>(),RuntimeSpec:upgrade) };
    if (CardEffectRules.HasValidTriggerPayoffs(operations)
        || CardEffectRules.HasValidTriggerPayoffs(operations.Reverse().ToArray())) throw new Exception("Order bypass");
    if (!CardEffectRules.HasValidTriggerPayoffs([operations[1]])) throw new Exception("Standalone upgrade rejected");
    Console.WriteLine("Exhaust/reference-upgrade compatibility passed.");
    return;
}

if (args.Contains("--repair-detail"))
{
    var character = Enum.Parse<GeneratedCharacter>(args.Last());
    var generator = new RandomCardGenerator(character, 29817 + 104729, balancedValues: true);
    for (var i = 0; i < 10; i++)
    {
        var card = generator.Generate(GeneratedRarity.Basic);
        Console.WriteLine($"{i}: {card.Type} {card.Cost}/{card.StarCost}: {card.ChineseDescription}");
    }
    AssemblySurvivalAudit.Enabled = true;
    var matching = typeof(RandomCardGenerator).GetMethod("GenerateMatching", BindingFlags.Instance | BindingFlags.NonPublic)!;
    try
    {
        for (var i = 0; i < 4; i++)
        {
            var card = (GeneratedCard)matching.Invoke(generator,
                [GeneratedRarity.Basic, (Func<GeneratedCard,bool>)StartingPoolConstraintResolver.CountsAsDefense])!;
            Console.WriteLine($"REPAIR: {card.ChineseDescription}");
        }
    }
    catch (Exception error) { Console.WriteLine(error); }
    Console.WriteLine(AssemblySurvivalAudit.Report());
    return;
}

if (args.Contains("--ancient-unique"))
{
    var exclusive = new[] { "N:Intangible", "A:ProxyAtomic_ForbiddenGrimoire", "NCR:OstyMaxHpBonusDamage" };
    foreach (var character in Enum.GetValues<GeneratedCharacter>())
    foreach (var ultimate in new[] { false, true })
    {
        var catalog = CharacterComponentCatalogs.Get(character, ultimate);
        foreach (var atom in catalog.Atoms.Where(atom => exclusive.Contains(atom.Template)))
            if (ComponentPolicy.Multiplicity(atom) != ComponentMultiplicity.UniquePerPool)
                throw new Exception($"Missing Ancient uniqueness: {character}/{atom.Template}");
        var occurrences = new Dictionary<string, int>();
        for (var seed = 0; seed < 5; seed++)
        {
            var generator = new RandomCardGenerator(character, 71133 + seed * 104729, ultimate,
                balancedValues: true);
            var cards = new List<GeneratedCard>();
            foreach (var rarity in Enum.GetValues<GeneratedRarity>())
            {
                var count = character == GeneratedCharacter.Colorless
                    ? rarity == GeneratedRarity.Uncommon ? 31 : rarity == GeneratedRarity.Rare ? 21 : 0
                    : rarity switch { GeneratedRarity.Basic => 10, GeneratedRarity.Common => 20,
                        GeneratedRarity.Uncommon => 35, GeneratedRarity.Rare => 25, _ => 2 };
                for (var i = 0; i < count; i++) cards.Add(generator.Generate(rarity));
            }
            if (!ComponentPolicy.TryAuditPool(cards, out var failure)) throw new Exception(failure);
            foreach (var template in exclusive)
            {
                var count = cards.SelectMany(card => card.Operations).Count(op => op.Template == template);
                if (count > 1) throw new Exception($"Repeated {template}: {count}");
                occurrences[template] = occurrences.GetValueOrDefault(template) + count;
            }
        }
        Console.WriteLine($"{character}/ultimate={ultimate}: 5 pools passed; "
            + string.Join(", ", occurrences.Select(pair => $"{pair.Key}={pair.Value}")));
    }
    return;
}

if (args.Contains("--review-rules"))
{
    ReviewRules();
    return;
}
static void ReviewRules()
{
    OperationRuntimeSpec Spec(string opcode = "trigger", string target = "self", string? trigger = null, string? condition = null) =>
        new(1, opcode, "", target, "none", "none", "any", [], [],
            condition is null ? null : new(condition), trigger is null ? null : new(trigger,"combat"));
    if (CardEffectRules.IsValidTriggerPayoff(Spec(trigger:"self_exhausted"),Spec("upgrade_card","referenced_card"))
        || CardEffectRules.IsValidTriggerPayoff(Spec(trigger:"next_attack"),Spec("modify_damage","selected_enemy"))
        || CardEffectRules.IsValidTriggerPayoff(Spec(trigger:"turn_start"),Spec(condition:"cards_played_this_turn_below"))
        || CardEffectRules.IsValidTriggerPayoff(Spec(trigger:"turn_start_if_self_in_exhaust"),Spec(condition:"owner_lost_hp_this_turn"))
        || !CardEffectRules.IsValidTriggerPayoff(Spec(trigger:"self_exhausted"),Spec("gain_block"))
        || !CardEffectRules.IsValidTriggerPayoff(Spec(trigger:"turn_start"),Spec(condition:"hand_empty")))
        throw new Exception("Trigger compatibility regression.");
    var power = new GeneratedCard(3,GeneratedCardType.Power,TargetMode.Other,GeneratedRarity.Rare,"",[],[]);
    var candidateType = typeof(CardUpgradeGenerator).GetNestedType("Candidate",BindingFlags.NonPublic)!;
    var candidate = Activator.CreateInstance(candidateType,new object[]{new CardUpgradeEffect(CardUpgradeKind.ReduceCost)})!;
    var selected = Array.CreateInstance(candidateType,0);
    var fits = typeof(CardUpgradeGenerator).GetMethod("FitsUpgradeCeiling",BindingFlags.NonPublic|BindingFlags.Static)!;
    if(!(bool)fits.Invoke(null,[power,selected,candidate,0d])!) throw new Exception("Expensive Power cost upgrade still capped.");
    Console.WriteLine("Trigger rules and expensive Power cost upgrade passed.");
    return;
}

if (args.Contains("--review-values"))
{
    ReviewValues();
    return;
}
static void ReviewValues()
{
    foreach (var text in new[] { "召唤4X。" })
    {
        var op = new GeneratorOperation("NCR:SummonX", OperationScope.NonTargeted, text, new Dictionary<string,int>(), LocalizedText:OperationLocalizedText.ScaledSummonX);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(OperationRuntimeSpecCompiler.GetOrCompile(op)));
        foreach(var delta in new[]{1,2,3})
        {
            var updated = CardUpgradeGenerator.ApplyEffectsToOperations([op], [new(CardUpgradeKind.IncreaseAllX, Delta: delta)]);
            Console.WriteLine(CardDescriptionRenderer.Render(updated));
            if(OperationRuntimeSpecCompiler.GetOrCompile(updated[0]).Values.Single(v=>v.Id=="hits").Offset != delta) throw new Exception("X offset mismatch");
        }
    }
    var catalog = CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad);
    var exhaust = catalog.Atoms.First(a => a.Template == "N:Exhaust" && OperationRuntimeSpecCompiler.GetOrCompile(a) is { Variant: "selected", SourceZone: "hand" });
    var counter = catalog.Atoms.First(a => OperationRuntimeSpecCompiler.GetOrCompile(a).Trigger?.Kind == "for_each_exhausted_card");
    GeneratorOperation ToOp(ComponentAtom a) => new(a.Template,a.Scope,a.ChineseText,new Dictionary<string,int>(),RuntimeSpec:OperationRuntimeSpecCompiler.GetOrCompile(a));
    var operations = new[] { ToOp(exhaust), ToOp(counter), new GeneratorOperation("T:D",OperationScope.SingleEnemyOnly,"造成1点伤害。",new Dictionary<string,int>{{"triggerIndex",1}}, RequiresSingleTarget:true) };
    operations = operations.Select(op=>op with {RuntimeSpec=OperationRuntimeSpecCompiler.GetOrCompile(op)}).ToArray();
    if(CardEffectRules.HasValidTriggerPayoffs(operations)) throw new Exception("Tiny single-exhaust payload remains legal.");
    foreach (var rarity in new[] { GeneratedRarity.Basic, GeneratedRarity.Common, GeneratedRarity.Uncommon })
    {
        Console.WriteLine($"{rarity}: " + System.Text.Json.JsonSerializer.Serialize(CardTinkeringApi.Analyze(new GeneratedCard(0,GeneratedCardType.Attack,TargetMode.SingleEnemy,rarity,"",[],operations))));
        var bounds = typeof(ComponentAssemblyGenerator).GetMethod("WholeCardBudgetBounds",BindingFlags.Static|BindingFlags.NonPublic)!;
        Console.WriteLine($"bounds: {bounds.Invoke(null,[rarity,0d,2,100,1d,true,GeneratedCharacter.Ironclad,0d])}");
    }
    return;
}

if (args.Contains("--review-frequency"))
{
    var character = Enum.Parse<GeneratedCharacter>(args.Last());
    var catalog = CharacterComponentCatalogs.Get(character);
    var generated = new List<GeneratedCard>();
    for (var seed = 0; seed < 30; seed++)
    {
        var generator = new RandomCardGenerator(character, 17823 + seed * 104729, balancedValues:true);
        foreach (var rarity in Enum.GetValues<GeneratedRarity>())
        {
            var count = rarity switch { GeneratedRarity.Basic => character == GeneratedCharacter.Colorless ? 0 : 10, GeneratedRarity.Common => character == GeneratedCharacter.Colorless ? 0 : 20, GeneratedRarity.Uncommon => character == GeneratedCharacter.Colorless ? 31 : 35, GeneratedRarity.Rare => character == GeneratedCharacter.Colorless ? 21 : 25, _ => character == GeneratedCharacter.Colorless ? 0 : 2 };
            for (var i=0;i<count;i++) generated.Add(generator.Generate(rarity));
        }
    }
    foreach (var rarity in Enum.GetValues<GeneratedRarity>())
    foreach (var type in Enum.GetValues<GeneratedCardType>())
    {
        var cards = generated.Where(c=>c.Rarity==rarity && c.Type==type && c.Cost>=0).ToArray();
        var native = catalog.Recipes.Where(c=>c.OriginalRarity==rarity && c.Type==type && c.Cost>=0).ToArray();
        if(cards.Length>0) Console.WriteLine($"{character}/{rarity}/{type}: n={cards.Length}, mean={cards.Average(c=>c.Cost):F2}, native={(native.Length>0 ? native.Average(c=>c.Cost):double.NaN):F2}, histogram={string.Join(',', Enumerable.Range(0,5).Select(cost=>cards.Count(c=>c.Cost==cost)))}");
        var nonSly = cards.Where(c=>!c.Tags.Contains(CardTag.Sly)).ToArray();
        if(type == GeneratedCardType.Attack && nonSly.Length>0) Console.WriteLine($"NONSLY {character}/{rarity}: {nonSly.Average(c=>c.Cost):F2} ({nonSly.Length})");
    }
    var rareCheck = typeof(ComponentAssemblyGenerator).GetMethod("IsExplicitRareOperation",BindingFlags.NonPublic|BindingFlags.Static)!;
    bool ExhaustRelated(GeneratorOperation op)
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(op);
        return spec.Opcode == "exhaust_card" || spec.SourceZone == "exhaust"
            || spec.Trigger?.Kind?.Contains("exhaust",StringComparison.Ordinal)==true
            || spec.Condition?.Kind == "exhaust_pile_minimum" || spec.Variant.Contains("exhaust",StringComparison.Ordinal);
    }
    Console.WriteLine($"EXHAUST Basic={generated.Count(c=>c.Rarity==GeneratedRarity.Basic && c.Operations.Any(ExhaustRelated))}/{generated.Count(c=>c.Rarity==GeneratedRarity.Basic)}, lifecycle={generated.Count(c=>c.Operations.Any(op=>OperationRuntimeSpecCompiler.GetOrCompile(op).Trigger?.Kind?.Contains("self_in_exhaust",StringComparison.Ordinal)==true))}/{generated.Count}");
    foreach(var group in catalog.Atoms.Where(a=>(bool)rareCheck.Invoke(null,[a])!).GroupBy(a=>a.SchemaKey))
    {
        var source = catalog.Recipes.Count(r=>r.Atoms.Any(a=>a.SchemaKey==group.Key));
        var count = generated.Count(c=>c.Operations.Any(op=>OperationRuntimeSpecCompiler.StructuralFieldKey(op)==group.Key));
        var ratio = count/(double)generated.Count/(source/(double)catalog.Recipes.Count);
        Console.WriteLine($"RARE {group.First().Template} {group.First().ChineseText} native={source}, generated={count}, ratio={ratio:F2}");
    }
    return;
}

if (args.Contains("--starter-coverage"))
{
    foreach (var character in Enum.GetValues<GeneratedCharacter>().Where(c => c != GeneratedCharacter.Colorless
        && (args.Length == 1 || c.ToString() == args.Last())))
    {
        var completed = 0;
        for (var seed = 0; seed < 20; seed++)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var repaired = false;
            var failure = string.Empty;
            for (var attempt = 0; attempt < 4 && !repaired; attempt++)
            {
                try
                {
                var generator = new RandomCardGenerator(character, 29817 + seed * 104729 + attempt,
                    suppressDerivativeReferences: attempt == 3, balancedValues: true);
                var cards = Enumerable.Range(0, 10).Select(_ => generator.Generate(GeneratedRarity.Basic)).ToArray();
                // Match the game's initial combat-coverage phase before local support/resource repairs.
                var matching = typeof(RandomCardGenerator).GetMethod("GenerateMatching", BindingFlags.Instance | BindingFlags.NonPublic)!;
                void Cover(Func<GeneratedCard, bool> required, Func<GeneratedCard, bool> protect)
                {
                    while (cards.Count(required) < 4)
                    {
                        var replacement = (GeneratedCard)matching.Invoke(generator, [GeneratedRarity.Basic, required])!;
                        var slot = Array.FindIndex(cards, card => !required(card)
                            && (!protect(card) || protect(replacement) || cards.Count(protect) > 4));
                        if (slot < 0) throw new InvalidOperationException("No coverage repair slot.");
                        cards[slot] = replacement;
                    }
                }
                Cover(StartingPoolConstraintResolver.CountsAsDamage, StartingPoolConstraintResolver.CountsAsDefense);
                Cover(StartingPoolConstraintResolver.CountsAsDefense, StartingPoolConstraintResolver.CountsAsDamage);
                repaired = StartingPoolConstraintResolver.TryRepair(cards, generator, new Random(seed), 4, 4, 128, out failure);
                if (repaired && (cards.Count(StartingPoolConstraintResolver.CountsAsDamage) < 4
                    || cards.Count(StartingPoolConstraintResolver.CountsAsDefense) < 4))
                    throw new InvalidOperationException($"{character}/{seed}: insufficient combat coverage");
                }
                catch (Exception error) when (error is InvalidOperationException or TargetInvocationException)
                {
                    failure = error.InnerException?.Message ?? error.Message;
                    Console.Error.WriteLine($"Rejected {character}/{seed} candidate {attempt + 1}: {failure}");
                }
            }
            if (!repaired) throw new InvalidOperationException($"{character}/{seed}: {failure}");
            completed++;
            Console.WriteLine($"{character}/{seed}: passed in {started.ElapsedMilliseconds} ms.");
        }
        Console.WriteLine($"{character}: {completed}/20 starting decks passed coverage and support repair.");
    }
    return;
}

if (args.Contains("--basic-frequency"))
{
    var rareCheck = typeof(ComponentAssemblyGenerator).GetMethod("IsExplicitRareOperation", BindingFlags.NonPublic | BindingFlags.Static)!;
    foreach (var character in Enum.GetValues<GeneratedCharacter>().Where(c => c != GeneratedCharacter.Colorless))
    {
        var rare = 0; var negative = 0; var keywords = 0; var total = 0;
        var negatives = new Dictionary<string, int>();
        for (var seed = 0; seed < (int.TryParse(args.Last(), out var count) ? count : 100); seed++)
        {
            var generator = new RandomCardGenerator(character, 81723 + seed * 104729, balancedValues: true);
            for (var slot = 0; slot < 10; slot++)
            {
                var card = generator.Generate(GeneratedRarity.Basic);
                bool IsRare(GeneratorOperation op) => (bool)rareCheck.Invoke(null, [new ComponentAtom(op.Template, op.Scope, op.ChineseText, op.RequiresSingleTarget, CardReferenceRequirement.None) { RuntimeSpec = op.RuntimeSpec }])!;
                if (card.Operations.Any(IsRare)) rare++;
                if (card.Operations.Any(CardEffectRules.IsNegativeEffect)) negative++;
                foreach (var op in card.Operations.Where(CardEffectRules.IsNegativeEffect))
                    negatives[op.Template] = negatives.GetValueOrDefault(op.Template) + 1;
                if (card.Tags.Any(t => t is CardTag.Exhaust or CardTag.Ethereal)) keywords++;
                total++;
            }
        }
        Console.WriteLine($"{character}: cards={total}, rare={rare}, negative={negative}, exhaustOrEthereal={keywords}");
        Console.WriteLine(string.Join(", ", negatives.OrderByDescending(p => p.Value).Select(p => $"{p.Key}={p.Value}")));
    }
    return;
}

if (args.Contains("--funnel"))
{
    AssemblySurvivalAudit.Enabled = true;
    var character = Enum.Parse<GeneratedCharacter>(args.Last());
    _ = ComponentOccurrenceAudit.Run(character, 10);
    Console.WriteLine(AssemblySurvivalAudit.Report());
    return;
}

var check = typeof(ComponentAssemblyGenerator).GetMethod("IsCompatible", BindingFlags.NonPublic | BindingFlags.Instance)!;
foreach (var character in Enum.GetValues<GeneratedCharacter>())
{
    if (args.Contains("--names"))
    {
        var generatorNames = new RandomCardGenerator(character, 20260822 + (int)character * 997, true);
        var ids = CharacterComponentCatalogs.Get(character).Recipes.Select(r=>r.Id).ToHashSet();
        for (var i=0;i<16;i++)
        {
            var card = generatorNames.Generate();
            if(card.Name?.SourceCardIds.Any(id=>!ids.Contains(id)) != false)
                Console.WriteLine($"{character} {card.Name}: {string.Join(',',card.Name?.SourceCardIds ?? [])}");
        }
        continue;
    }
    if (args.Contains("--roundtrip"))
    {
        var card = new RandomCardGenerator(character, 8431 + (int)character)
            .Generate(character == GeneratedCharacter.Colorless ? GeneratedRarity.Uncommon : GeneratedRarity.Common);
        Console.WriteLine($"{character}: {string.Join(';', CardTinkeringApi.Validate(card, card.Operations).Errors)}");
        foreach (var operation in card.Operations)
            Console.WriteLine($"{operation.Template} slot={operation.CardTargetSlot} {operation.ChineseText}");
        continue;
    }
    var generator = new ComponentAssemblyGenerator(new Random(1), character, balancedValues: true);
    foreach (var recipe in CharacterComponentCatalogs.Get(character).Recipes)
    {
        var previous = new List<GeneratorOperation>();
        for (var i = 0; i < recipe.Atoms.Count; i++)
        {
            var atom = recipe.Atoms[i];
            var compatible = (bool)check.Invoke(generator,
                [recipe.Type, recipe.Target, recipe.Cost, recipe.HasStarCostX, atom, previous])!;
            if (!compatible) Console.WriteLine($"{character}/{recipe.Id}/{i}: {atom.Template} rejected after {string.Join(',',previous.Select(p=>p.Template))}");
            var parameters = new Dictionary<string,int>();
            if (i < recipe.TriggerOwners.Count && recipe.TriggerOwners[i] >= 0)
                parameters["triggerIndex"] = recipe.TriggerOwners[i];
            previous.Add(new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText, parameters,
                RequiresSingleTarget: atom.RequiresSingleTarget, RuntimeSpec: atom.RuntimeSpec));
        }
        if (args.Contains("--values") && recipe.Id is "EchoForm" or "Barrage" or "DeathMarch" or "CrescentSpear")
        {
            var native = new GeneratedCard(recipe.Cost, recipe.Type, recipe.Target, recipe.OriginalRarity,
                string.Empty, recipe.Tags, previous, Character:character);
            Console.WriteLine($"{character}/{recipe.Id}: {CardTinkeringApi.Analyze(native).Budget}");
        }
        if (args.Contains("--upgrade-regression") && recipe.Id == "BodySlam")
        {
            var native = new GeneratedCard(recipe.Cost, recipe.Type, recipe.Target, recipe.OriginalRarity,
                string.Empty, recipe.Tags, previous, Character: character);
            for (var seed = 0; seed < 100; seed++)
            {
                var upgrade = CardUpgradeGenerator.Generate(native, new Random(seed));
                if (!upgrade.Effects.Any(effect => effect.Kind == CardUpgradeKind.ReduceCost))
                    throw new InvalidOperationException($"Sole legal Body Slam upgrade lost at seed {seed}.");
            }
            Console.WriteLine("Body Slam sole-upgrade regression: 100/100 passed.");
        }
    }
}
