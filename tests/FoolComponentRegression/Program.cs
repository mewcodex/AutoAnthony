using ChaosCardGenerator;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static GeneratorOperation Operation(ComponentAtom atom, int? owner = null) => new(
    atom.Template, atom.Scope, atom.ChineseText,
    owner is { } index ? new Dictionary<string, int> { ["triggerIndex"] = index } : [],
    RequiresSingleTarget: atom.RequiresSingleTarget,
    RuntimeSpec: atom.RuntimeSpec, LocalizedText: atom.LocalizedText);

static void MustReject(GeneratedCard card)
{
    try { CardTemplateValidator.Validate(card); }
    catch (InvalidOperationException) { return; }
    throw new InvalidOperationException("A non-Exhaust Skill with a permanent reward was accepted.");
}

var catalogs = Enum.GetValues<GeneratedCharacter>().Select(CharacterComponentCatalogs.Get).ToArray();
var fool = catalogs.Single(catalog => catalog.Character == GeneratedCharacter.Fool);
// Run before generation can populate process-local numeric text variants, just like a cold Continue action.
foreach (var template in new[] { "I:ProxyAtomic_Begone", "I:ProxyAtomic_Seance" })
foreach (var amount in new[] { 1, 2, 10 })
foreach (var derivative in DerivativeSlotCatalog.All.Where(derivative => derivative.IsPlayable))
{
    var atom = catalogs.SelectMany(catalog => catalog.Atoms).First(atom => atom.Template == template);
    var spec = OperationRuntimeSpecCompiler.WithDerivativeReference(
        OperationRuntimeSpecCompiler.AddExplicitFixedValue(OperationRuntimeSpecCompiler.GetOrCompile(atom),
            "amount", amount), derivative.Id);
    var op = Operation(atom) with { RuntimeSpec = spec, DerivativeId = derivative.Id, LocalizedText = null };
    Require(ComponentLocalizationApi.TryGet(atom.SemanticId, out var registered), "Missing source localization.");
    var counted = DerivativeSlotCatalog.RestoreSourceCountLocalization(op, registered, spec);
    counted.Validate(spec);
    var bound = DerivativeSlotCatalog.BindSourceLocalizedText(op, counted, counted.RenderEnglish(spec)!, spec);
    var restored = op with { LocalizedText = bound };
    Require(bound.RenderChinese(spec).Contains(amount.ToString())
        && EnglishCardDescriptionRenderer.OperationText(restored).Contains(amount.ToString()),
        "Cold implicit-count producer lost its numeric slot or English description.");
}
Console.WriteLine("PASS: cold source-localization recovery for both implicit-count transforms and all playable derivatives.");
if (args.Length > 0 && args[0] == "--audit-ancients")
{
    CompositionDiversityAudit.Run(args.Length > 1 ? int.Parse(args[1]) : 32);
    return;
}
if (args.Length > 0 && args[0] == "--audit-melodies")
{
    MelodyDistributionAudit.Run(fool, args.Length > 1 ? int.Parse(args[1]) : 32);
    return;
}
Require(fool.Recipes.Count == 84, "Fool single-player recipe count drifted.");
var foolStarters = fool.Recipes.Where(recipe => recipe.OriginalRarity == GeneratedRarity.Basic).ToArray();
Require(foolStarters.Sum(CharacterComponentCatalogs.NativeOccurrenceWeight) == 10
    && foolStarters.Where(recipe => recipe.Atoms.Any(FoolComponentRules.IsShootModifier))
        .Sum(CharacterComponentCatalogs.NativeOccurrenceWeight) == 1,
    "Fool Shoot baseline must be one Arrow card in ten actual starting cards, not one in three unique recipes.");
SelfCostReductionRegression.Run();
CompositionDiversityAudit.Verify();
Require(fool.Recipes.Count(recipe => recipe.Tags.Any(tag => tag is CardTag.Wah or CardTag.Cho or CardTag.Ki)) == 19,
    "Native melody tags must be read independently of source property order.");
foreach (var melodyTag in new[] { CardTag.Wah, CardTag.Cho, CardTag.Ki })
    Require(fool.Recipes.Count(recipe => recipe.Tags.Contains(melodyTag)) == 8, "Native melody coverage drifted: " + melodyTag);
foreach (var native in NativeCardDecompositionApi.Cards.Where(card => card.Pool == "Fool"))
    Require(NativeCardDecompositionApi.TryCreateDefinition(native.CatalogId, out _, out var unsupported),
        $"Fool native upgrade could not reconstruct: {native.ClassName}: {string.Join(',', unsupported)}.");
var showOff = fool.Recipes.Single(recipe => recipe.Id == "ShowOff");
Require(showOff.Atoms.Select(atom => atom.Template).SequenceEqual(
    ["F:Aim", "F:UntilNonMinionDies", "F:IncreaseAimRun"]), "Permanent Aim growth was not split.");
Require(showOff.TriggerOwners.SequenceEqual([-1, -1, 1]), "Growth lost its one-shot owner.");
Require(showOff.Tags.Contains(CardTag.Exhaust), "Native Show Off must retain Exhaust.");

var oneShot = Operation(showOff.Atoms[1]);
var growth = Operation(showOff.Atoms[2], 0);
Require(OperationRuntimeSpecCompiler.GetOrCompile(oneShot).Trigger?.Lifetime == "this_turn_once",
    "The permanent-growth gate must expire after this turn or its first success.");
Require(CardEffectRules.IsRestrictedEffect(showOff.Atoms[2]) && CardEffectRules.IsRestrictedEffect(growth),
    "Permanent Aim growth did not enter the common restricted-effect policy.");
Require(CardEffectRules.IsPersistentPowerFoundation(growth), "Restricted growth cannot establish a Power.");

var hunt = catalogs.Single(catalog => catalog.Character == GeneratedCharacter.Silent)
    .Recipes.Single(recipe => recipe.Id == "TheHunt");
Require(CardEffectRules.IsRestrictedEffect(hunt.Atoms.Single(atom => atom.Template == "I:AddCardReward")),
    "The Hunt reward lost its restriction.");
var repeatAtom = catalogs.Single(catalog => catalog.Character == GeneratedCharacter.Ironclad)
    .Atoms.First(atom => atom.Template == "A:whenSkillPlayed");
var repeat = Operation(repeatAtom);
var selfCopy = Operation(catalogs.Single(catalog => catalog.Character == GeneratedCharacter.Ironclad)
    .Recipes.Single(recipe => recipe.Id == "Anger").Atoms.Single(atom => atom.Template == "N:Create"));
var restrictedCount = 0;
foreach (var atom in catalogs.SelectMany(catalog => catalog.Atoms).Where(CardEffectRules.IsRestrictedEffect))
{
    var reward = Operation(atom, 0);
    Require(CardEffectRules.HasNoRepeatedTriggeredRestrictedEffects([oneShot, reward]),
        $"One-shot permanent reward was rejected: {atom.Template}.");
    Require(!CardEffectRules.HasNoRepeatedTriggeredRestrictedEffects([repeat, reward]),
        $"Recurring permanent reward was accepted: {atom.Template}.");
    var nestedGate = oneShot with { Parameters = new Dictionary<string, int> { ["triggerIndex"] = 0 } };
    var nestedReward = reward with { Parameters = new Dictionary<string, int> { ["triggerIndex"] = 1 } };
    Require(!CardEffectRules.HasNoRepeatedTriggeredRestrictedEffects([repeat, nestedGate, nestedReward]),
        $"Nested one-shot gate bypassed the recurring-reward restriction: {atom.Template}.");
    Require(!CardEffectRules.HasNoRestrictedSelfCopyAssembly([
        Operation(atom), selfCopy]),
        $"Permanent reward allowed a self-copy loop: {atom.Template}.");
    MustReject(new(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare, "", [],
        [Operation(atom)], Character: GeneratedCharacter.Fool));
    restrictedCount++;
}
Require(restrictedCount >= 8, $"Restricted-effect coverage unexpectedly collapsed: {restrictedCount}.");
var arrow = Operation(fool.Atoms.Single(atom => atom.Template == "F:ShootArrow"));
var damage = Operation(fool.Recipes.Single(recipe => recipe.Id == "StrikeFool").Atoms[0]);
var refund = Operation(fool.Atoms.First(atom => atom.Template == "F:RefundStretch"));
Require(CardTextStyle.Chinese(refund, "重新获得这张牌花费的拉伸。")
    == "重新获得被这张牌的格挡效果消耗的拉伸。", "Saved refund wording was not normalized.");
var aim = Operation(showOff.Atoms[0]);
Require(FoolComponentRules.HasValidShootAssembly([arrow, damage]), "Arrow + damage was rejected.");
Require(!FoolComponentRules.HasValidShootAssembly([arrow, damage, aim]), "An extra effect bypassed Shoot exclusivity.");
Require(!FoolComponentRules.HasValidShootAssembly([arrow]), "An orphan Shoot modifier was accepted.");
Require(CardDescriptionRenderer.Render([arrow, damage]) == "发射一支箭矢，造成6点伤害。",
    "Shoot modifier was not rendered inline in Chinese.");
Require(EnglishCardDescriptionRenderer.Render([arrow, damage]) == "Shoot an Arrow to deal 6 damage.",
    "Shoot modifier was not rendered inline in English.");
var melody = fool.Atoms.Single(atom => atom.Template == "F:PlayMelodyCards");
for (var mask = 1; mask <= 7; mask++)
{
    var selected = FoolMelodySubset.WithMask(melody, mask);
    Require(selected.RuntimeSpec!.CardFilter == $"melody_{mask}"
        && selected.RuntimeSpec.Values.Single().BaseValue == mask
        && !selected.RuntimeSpec.Values.Single().Explicit && !selected.RuntimeSpec.Values.Single().Upgradable,
        "A melody selector became a printed/upgradeable number.");
    selected.LocalizedText!.Validate(selected.RuntimeSpec);
}
var generatedCount = 0;
var tune = fool.Recipes.Single(recipe => recipe.Id == "Tune");
var tuneOperations = tune.Atoms.Select((atom, index) => Operation(atom,
    tune.TriggerOwners[index] >= 0 ? tune.TriggerOwners[index] : null)).ToArray();
Require(tune.TriggerOwners.SequenceEqual([-1, 0, 1]), "Tune lost its nested event/condition ownership.");
Require(CardDescriptionRenderer.Render(tuneOperations).Count(character => character == '。') == 1,
    "A nested conditional clause was split by a sentence boundary.");
var florescence = fool.Recipes.Single(recipe => recipe.Id == "Florescence");
var creation = Operation(florescence.Atoms.Last());
Require(!FoolComponentRules.HasValidExhaustDependencies([creation], false), "Orphan The Shot creation was allowed.");
var shootExhaust = Operation(florescence.Atoms.First());
Require(FoolComponentRules.HasValidExhaustDependencies([shootExhaust, creation], false), "Native The Shot dependency was rejected.");
Require(!FoolComponentRules.HasValidExhaustDependencies([shootExhaust, creation with {
    Parameters = new Dictionary<string, int> { ["triggerIndex"] = 0 } }], false),
    "The Shot incorrectly borrowed an Exhaust result from another resolution.");
var mourning = fool.Recipes.Single(recipe => recipe.Id == "MourningBlow");
MustReject(new(1, GeneratedCardType.Attack, TargetMode.SingleEnemy, GeneratedRarity.Rare, "", [CardTag.Exhaust],
    mourning.Atoms.Select(atom => Operation(atom)).ToArray(), Character: GeneratedCharacter.Fool));
foreach (var character in Enum.GetValues<GeneratedCharacter>())
{
    var generator = new RandomCardGenerator(character, 113_061, balancedValues: true);
    foreach (var rarity in character == GeneratedCharacter.Colorless
        ? new[] { GeneratedRarity.Uncommon, GeneratedRarity.Rare }
        : Enum.GetValues<GeneratedRarity>())
    for (var i = 0; i < 6; i++)
    {
        var generated = generator.Generate(rarity);
        if (generated.Name?.MelodyTags is { Count: > 0 } tags)
        {
            var words = generated.Name.Chinese.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var prefix = tags.Select(tag => tag switch { CardTag.Wah => "哇", CardTag.Cho => "啾", _ => "叽" }).ToArray();
            Require(words.Take(prefix.Length).SequenceEqual(prefix) && words.Length <= prefix.Length + 1,
                "A melody title contains extra body fragments: " + generated.Name.Chinese);
            Require(words.Length == prefix.Length || words[^1].Length <= 2,
                "A melody title retained two reviewed Chinese fragments: " + generated.Name.Chinese);
        }
        CardTemplateValidator.Validate(generated);
        Require(FoolComponentRules.HasValidShootAssembly(generated.Operations), "An illegal generated Shoot card escaped validation.");
        generatedCount++;
    }
}
Console.WriteLine($"PASS: 84 Fool recipes; split permanent Aim growth; {restrictedCount} restricted source atoms across 7 pools.");
Console.WriteLine($"PASS: arrow exclusivity/rendering, all 7 melody subsets, {generatedCount} generated cards across 7 pools.");
