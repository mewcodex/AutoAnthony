using ChaosCardGenerator;

internal static class SelfCostReductionRegression
{
    internal static void Run()
    {
        var catalogs = Enum.GetValues<GeneratedCharacter>().Select(CharacterComponentCatalogs.Get).ToArray();
        GeneratorOperation Op(string template, int amount = 1, int? parent = null)
        {
            var atom = catalogs.SelectMany(catalog => catalog.Atoms).First(atom => atom.Template == template);
            var op = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
                parent is { } owner ? new Dictionary<string, int> { ["triggerIndex"] = owner } : [],
                RequiresSingleTarget: atom.RequiresSingleTarget,
                RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom), LocalizedText: atom.LocalizedText);
            return OperationRuntimeSpecCompiler.TryReplaceFixedValue(op, "amount", amount, out var updated)
                ? updated : op;
        }
        static void Check(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException(detail);
        }
        static GeneratedCard Card(int cost, params GeneratorOperation[] operations) => new(
            cost, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Uncommon, "", [], operations,
            Character: GeneratedCharacter.Fool);
        static double Value(int cost, params GeneratorOperation[] operations) =>
            CardTinkeringApi.Analyze(Card(cost, operations)).Budget.PositiveValue;

        foreach (var template in new[] { "F:CostPerMelody", "F:CostIfWahKi", "I:ReduceThisCardCostCombat",
                     "D:CostDownWhenStatusGenerated", "NCR:CostDownPerVoidPlayed", "NCR:CostDownWhenCreatureDies",
                     "R:CostDownWhenDrawn", "C:whileInCombat", "C:whileInCombatSkillCostReduction" })
        {
            var tooLarge = Op(template, 3);
            Check(!CardEffectRules.HasValidNumericSelfCostReductionAmounts(2, [tooLarge]), template + " missing amount cap");
            var mutable = new List<GeneratorOperation> { tooLarge };
            Check(CardEffectRules.ClampNumericSelfCostReductionAmounts(2, mutable)
                && OperationRuntimeSpecCompiler.StaticLiteralValue(mutable[0], "amount") == 2,
                template + " did not clamp to printed Energy");
            Check(Value(2, mutable[0]) <= 1400.0001d, template + " exceeded total saving cap");
            Check(Value(0, mutable[0]) == 0d && Value(-1, mutable[0]) == 0d,
                template + " borrowed value from a free/X shell");
        }
        var repeat = Op("F:ForEachMelody");
        var reduceTwo = Op("I:ReduceThisCardCostCombat", 2, 0);
        Check(Math.Abs(Value(2, repeat, reduceTwo) - 1400d) < .001d,
            "Repeated reductions must price at most two Energy on a two-Energy card");
        var analysis = CardTinkeringApi.Analyze(Card(2, repeat, reduceTwo,
            Op("F:CostPerMelody", 2)));
        Check(Math.Abs(analysis.Budget.PositiveValue - 1400d) < .001d
            && Math.Abs(analysis.Components.Sum(component => component.ContextualValue) - 1400d) < .001d,
            "Multiple reductions and component traces did not share one cap");
        Check(Math.Abs(Value(3, Op("F:CostIfWahKi", 2)) - 840d) < .001d,
            "A conditional reduction lost its below-cap frequency discount");
        Check(Math.Abs(Value(2, Op("D:SetThisCardCostZero")) - 1400d) < .001d,
            "Set-zero failed to use the same whole-card saving model");
        var costAttack = CardTinkeringApi.Analyze(Card(2, Op("C:whileInCombat", 2)));
        Check(!costAttack.Components[0].IsTrigger && costAttack.Components[0].ContextualValue == 1400d,
            "An integrated reduction rule was mistaken for a zero-value control-flow prefix");
        foreach (var ultimate in new[] { false, true })
        {
            var generator = new RandomCardGenerator(GeneratedCharacter.Fool, 113_163,
                unlockComponentRoles: ultimate, balancedValues: true, randomizeNumericValues: true);
            foreach (var rarity in Enum.GetValues<GeneratedRarity>())
            for (var sample = 0; sample < 12; sample++)
                CardTemplateValidator.Validate(generator.Generate(rarity), allowRandomizedNumericValues: true);
        }
        Console.WriteLine("PASS: all 9 numeric self-cost families, repeats, shared caps, free/X shells, API traces.");
        Console.WriteLine("PASS: randomized numeric values and upgrades across normal/Ultimate Fool pools.");
    }
}
