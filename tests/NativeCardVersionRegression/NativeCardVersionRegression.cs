using AutoAnthony;
using ChaosCardGenerator;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models.Events;

// Test-only engine plugin. Never load it in the real game/user profile; see README for isolation requirements.
[ModInitializer(nameof(Init))]
public static class NativeCardVersionRegression
{
    private static bool _started;
    private static CardModel[]? _potionPoolFixture;

    public static void Init()
    {
        if (!OS.GetUserDataDir().Contains("AutoAnthonyV113Smoke-20261112", StringComparison.Ordinal))
            throw new InvalidOperationException("Native-card regression requires the isolated test profile.");
        new Harmony("autoanthony.native-version-regression").Patch(
            AccessTools.Method(typeof(NMainMenu), nameof(NMainMenu._Ready)),
            postfix: new HarmonyMethod(typeof(NativeCardVersionRegression), nameof(MainMenuReady)));
        new Harmony("autoanthony.potion-event-fixture").Patch(
            AccessTools.Method(typeof(CardPoolModel), nameof(CardPoolModel.GetUnlockedCards)),
            postfix: new HarmonyMethod(typeof(NativeCardVersionRegression), nameof(PotionPoolFixture)));
    }

    private static async void MainMenuReady(NMainMenu __instance)
    {
        if (_started) return;
        _started = true;
        var tree = __instance.GetTree();
        await tree.ToSignal(tree.CreateTimer(1), SceneTreeTimer.SignalName.Timeout);
        try
        {
            var settings = typeof(ChaosCardModel).Assembly.GetType("AutoAnthony.ChaosModSettings", true)!;
            AccessTools.Field(settings, "_enabled").SetValue(null, true);
            AccessTools.Field(settings, "_decomposeOriginalCards").SetValue(null, true);
            var owner = Player.CreateForNewRun(ModelDb.Character<Ironclad>(), UnlockState.all, 1);
            var canonical = ModelDb.All.OfType<CardModel>().Where(card => card.GetType().Assembly == typeof(CardModel).Assembly)
                .ToDictionary(card => card.GetType().Name, StringComparer.Ordinal);
            var failures = new List<string>();
            var checkedCards = 0;
            var rendered = 0;
            foreach (var language in new[] { "eng", "zhs" })
            {
                LocManager.Instance.SetLanguage(language);
                foreach (var source in NativeCardDecompositionApi.Cards)
                {
                    if (!canonical.TryGetValue(source.ClassName, out var prototype))
                    {
                        failures.Add("Missing native class: " + source.ClassName);
                        continue;
                    }
                    var card = prototype.ToMutable();
                    card.Owner = owner;
                    if (source.TemplateBindings is { } bindings)
                    {
                        foreach (var (binding, propertyName) in new[]
                                 { ("CardType", "TinkerTimeType"), ("Rider", "TinkerTimeRider") })
                        {
                            var property = AccessTools.Property(card.GetType(), propertyName);
                            property.SetValue(card, Enum.Parse(property.PropertyType, bindings[binding]));
                        }
                    }
                    if (!AutoAnthonyNativeCardApi.TryResolve(card, out var decomposition))
                    {
                        failures.Add("Unresolved native card: " + source.CatalogId);
                        continue;
                    }
                    var upgraded = false;
                    do
                    {
                        try
                        {
                            var expectedCost = upgraded ? decomposition.Upgrade.Result?.EnergyCost : decomposition.Base.EnergyCost;
                            if (card.EnergyCost.CostsX != decomposition.Base.EnergyCostX
                                || !card.EnergyCost.CostsX && expectedCost is not null
                                && card.EnergyCost.GetWithModifiers(CostModifiers.None) != expectedCost)
                                throw new InvalidOperationException("Native Energy cost differs from catalog");
                            var expectedStars = upgraded ? decomposition.Upgrade.Result?.StarCost : decomposition.Base.StarCost;
                            if (card.HasStarCostX != decomposition.Base.StarCostX
                                || expectedStars >= 0 && card.BaseStarCost != expectedStars)
                                throw new InvalidOperationException("Native Star cost differs from catalog");
                            foreach (var variable in decomposition.Base.Variables)
                            {
                                var expected = upgraded
                                    ? decomposition.Upgrade.Result?.Variables.GetValueOrDefault(variable.Id)
                                    : variable.BaseValue;
                                if (!card.DynamicVars.TryGetValue(variable.Id, out var live))
                                    throw new InvalidOperationException("Missing DynamicVar " + variable.Id);
                                if (expected is not null && live.BaseValue != expected)
                                    throw new InvalidOperationException($"{variable.Id}={live.BaseValue}, expected {expected}");
                            }
                            foreach (var component in decomposition.Components)
                            foreach (var argument in component.Arguments.Values)
                            foreach (var binding in argument.NativeBindings ?? [])
                            {
                                if (!card.DynamicVars.ContainsKey(binding.Variable))
                                    throw new InvalidOperationException("Missing bound variable " + binding.Variable);
                                if (argument.ValueSource == "fixed" && argument.NativeBindings?.Count == 1
                                    && card.DynamicVars[binding.Variable].BaseValue * binding.ValueTransform.Scale
                                        + binding.ValueTransform.Offset
                                        != argument.BaseValue + (upgraded ? binding.UpgradeDelta * binding.ValueTransform.Scale : 0))
                                    throw new InvalidOperationException("Native component argument differs from " + binding.Variable);
                            }
                            var expectedKeywords = upgraded ? decomposition.Upgrade.Result?.Keywords : decomposition.Base.Keywords;
                            if (source.Pool == "Fool" && !card.Tags.Select(tag => tag.ToString()).ToHashSet()
                                    .SetEquals(decomposition.Base.Tags))
                                throw new InvalidOperationException("Native Fool melody/Shoot tags differ from catalog");
                            if (expectedKeywords is not null && !card.Keywords.Select(keyword => keyword.ToString())
                                    .ToHashSet().SetEquals(expectedKeywords))
                                throw new InvalidOperationException("Native keyword set differs from catalog");
                            var description = card.GetDescriptionForPile(PileType.Deck);
                            if (description.Contains(":diff()", StringComparison.Ordinal)
                                || description.Contains(":energyIcons()", StringComparison.Ordinal)
                                || description.Contains("[[", StringComparison.Ordinal))
                                throw new InvalidOperationException("Unresolved description: " + description);
                            rendered++;
                        }
                        catch (Exception exception)
                        {
                            failures.Add($"{source.CatalogId}, {language}, upgraded={upgraded}: {exception.Message}");
                        }
                        if (upgraded || !card.IsUpgradable) break;
                        card.UpgradeInternal();
                        upgraded = true;
                    } while (true);
                    checkedCards++;
                }
            }
            CheckGeneratedLifecycle(owner);
            CheckShootTags(owner);
            CheckReworkedSourceSnapshot();
            await CheckV113ShivRetain(owner);
            CheckFoolDescriptions(owner);
            CheckLibraryVisibility();
            CheckNativeStartingCopies();
            CheckPortraitBlur(owner);
            await CheckLibraryScreen(__instance);
            CheckFutureOfPotions();
            foreach (var failure in failures) GD.PrintErr("AA_NATIVE_FAIL " + failure);
            GD.Print($"AA_NATIVE_COMPLETE cards={checkedCards}, descriptions={rendered}, failures={failures.Count}");
            tree.Quit(failures.Count == 0 ? 0 : 1);
        }
        catch (Exception exception)
        {
            GD.PrintErr("AA_NATIVE_FATAL " + exception);
            tree.Quit(1);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        GD.Print("AA_V113_PASS " + message);
    }

    private static void PotionPoolFixture(CardPoolModel __instance, ref IEnumerable<CardModel> __result)
    {
        if (_potionPoolFixture is not null && __instance is ChaosIroncladCardPool)
            __result = _potionPoolFixture;
    }

    private static void CheckFutureOfPotions()
    {
        var settings = typeof(ChaosCardModel).Assembly.GetType("AutoAnthony.ChaosModSettings", true)!;
        AccessTools.Field(settings, "_numericBalanceOptimization").SetValue(null, true);
        AccessTools.Field(settings, "_ultimateChaos").SetValue(null, false);
        ChaosRunDefinitions.Activate(GeneratedCharacter.Ironclad, "AA_POTIONS_03166");
        var potionPrototypes = new[] { PotionRarity.Common, PotionRarity.Uncommon, PotionRarity.Rare }
            .Select(rarity => ModelDb.All.OfType<PotionModel>().First(potion => potion.Rarity == rarity)).ToArray();
        var sourceCards = ChaosRunDefinitions.GetAllCards().SelectMany(pair => pair.Value
            .Select(definition => ChaosCardRegistry.Canonical(pair.Key, definition.Slot))).ToArray();
        var checkedRewards = 0;
        foreach (var character in new CharacterModel[] { ModelDb.Character<Ironclad>(), ModelDb.Character<Silent>(),
                     ModelDb.Character<Defect>(), ModelDb.Character<Necrobinder>(), ModelDb.Character<Regent>(),
                     ModelDb.Character<Fool>() })
        {
            var owner = Player.CreateForNewRun(character, UnlockState.all, 1);
            _ = RunState.CreateForNewRun([owner], ActModel.GetRandomList(new Rng(1, "potions"),
                UnlockState.all, false).Select(act => act.ToMutable()).ToArray(), [], GameMode.Standard, false, 0, "AA_POTIONS_REWARD");
            var potions = potionPrototypes.Select(prototype => prototype.ToMutable()).ToArray();
            foreach (var potion in potions) owner.AddPotionInternal(potion);
            var instance = (TheFutureOfPotions)ModelDb.Event<TheFutureOfPotions>().ToMutable();
            AccessTools.Property(typeof(EventModel), "Owner").SetValue(instance, owner);
            var types = potions.ToDictionary(potion => potion, _ => CardType.Attack);
            AccessTools.Field(typeof(TheFutureOfPotions), "_cardTypes").SetValue(instance, types);
            var getter = AccessTools.Property(typeof(TheFutureOfPotions), "PotionToCardType");
            foreach (var potion in potions)
            foreach (var requested in new[] { CardType.Attack, CardType.Skill, CardType.Power })
            {
                types[potion] = requested;
                _ = getter.GetValue(instance);
                var rarity = potion.Rarity switch { PotionRarity.Common => CardRarity.Common,
                    PotionRarity.Uncommon => CardRarity.Uncommon, _ => CardRarity.Rare };
                CheckPotionReward(owner, rarity, types[potion]);
                if (potion.Rarity == PotionRarity.Common && types[potion] == CardType.Power)
                    throw new InvalidOperationException("Common potion selected Power.");
                checkedRewards++;
            }

            if (character is not Ironclad) continue;
            try
            {
                // Reproduce the original two-candidate failure using actual generated canonical cards.
                var attacks = sourceCards.Where(card => card.Rarity == CardRarity.Rare
                    && card.Type == CardType.Attack).Take(2).ToArray();
                var skills = sourceCards.Where(card => card.Rarity == CardRarity.Rare
                    && card.Type == CardType.Skill).Take(3).ToArray();
                if (attacks.Length != 2 || skills.Length != 3) throw new InvalidOperationException("Sparse fixture unavailable.");
                _potionPoolFixture = owner.Character.CardPool.GetUnlockedCards(owner.UnlockState,
                        owner.RunState.CardMultiplayerConstraint).Where(card => card.Rarity != CardRarity.Rare)
                    .Concat(attacks).Concat(skills).ToArray();
                var insufficient = PotionOptions(owner, CardRarity.Rare, CardType.Attack);
                var failed = false;
                try { _ = CardFactory.CreateForReward(owner, 3, insufficient).ToArray(); }
                catch (InvalidOperationException error) when (error.Message.Contains("couldn't generate a valid card"))
                { failed = true; }
                Check(failed, "native three-choice reward reproduces the two-candidate failure without global fallback");
                var rarePotion = potions.Single(potion => potion.Rarity == PotionRarity.Rare);
                types[rarePotion] = CardType.Attack;
                _ = getter.GetValue(instance);
                Check(types[rarePotion] == CardType.Skill, "potion event changes insufficient Rare Attack to a three-card Rare Skill type");
                CheckPotionReward(owner, CardRarity.Rare, types[rarePotion]);
                var options = (IReadOnlyList<MegaCrit.Sts2.Core.Events.EventOption>)
                    AccessTools.Method(typeof(TheFutureOfPotions), "GenerateInitialOptions").Invoke(instance, [])!;
                Check(options.Any(option => option.Description.GetFormattedText().Contains("Skill", StringComparison.OrdinalIgnoreCase)),
                    "potion event text uses the same corrected type as reward generation");
            }
            finally { _potionPoolFixture = null; }
        }
        Check(checkedRewards == 54, "54 real reward populations across all six built-in character pools");
        ChaosRunDefinitions.DeactivateRun();
    }

    private static CardCreationOptions PotionOptions(Player owner, CardRarity rarity, CardType type) =>
        CardCreationOptions.ForNonCombatWithUniformOdds([owner.Character.CardPool],
                card => card.Rarity == rarity && card.Type == type)
            .WithFlags(CardCreationFlags.NoRarityModification | CardCreationFlags.NoCardPoolModifications);

    private static void CheckPotionReward(Player owner, CardRarity rarity, CardType type)
    {
        var reward = new CardReward(PotionOptions(owner, rarity, type), 3, owner);
        var generated = 0;
        reward.AfterGenerated += () => { generated++; foreach (var card in reward.Cards) CardCmd.Upgrade(card); };
        reward.Populate();
        var cards = reward.Cards.ToArray();
        if (cards.Length != 3 || cards.Select(card => card.Id).Distinct().Count() != 3
            || cards.Any(card => card.Rarity != rarity || card.Type != type || !card.IsUpgraded)
            || generated != 1)
            throw new InvalidOperationException("Potion reward differs from advertised rarity/type/upgrade/choice count.");
        reward.Populate();
        if (generated != 1) throw new InvalidOperationException("Populated potion reward generated twice.");
    }

    private static void CheckReworkedSourceSnapshot()
    {
        var legacy = new GeneratorOperation("I:DrawWithRetain", OperationScope.Independent,
            "抽2张牌。这些牌在本回合获得保留。", new Dictionary<string, int>(),
            LocalizationId: "silent/expertise/0");
        var spec = OperationRuntimeSpecCompiler.CompileLegacy(legacy);
        legacy = legacy with { RuntimeSpec = spec };
        var card = new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Uncommon,
            legacy.ChineseText, [], [legacy], Character: GeneratedCharacter.Silent);
        var definition = new ChaosCardDefinition(0, card, "", "", "", "", "", [spec], []);
        var restored = (ChaosCardDefinition)AccessTools.Method(typeof(ChaosPoolSnapshot), "HydrateRuntimeSpecs")
            .Invoke(null, [definition, true])!;
        var op = restored.Card.Operations[0];
        Check(op.RuntimeSpec!.StableSignature() == spec.StableSignature()
            && EnglishCardDescriptionRenderer.OperationText(op).Contains("Retain", StringComparison.Ordinal)
            && op.LocalizedText!.RenderChinese(spec).Contains("保留", StringComparison.Ordinal),
            "a reused native localization ID preserves the retired Draw+Retain operation and prose");
    }

    private static async Task CheckV113ShivRetain(Player owner)
    {
        var atom = CharacterComponentCatalogs.Get(GeneratedCharacter.Silent).Atoms
            .First(atom => atom.Template == "A:ruleShivsRetain");
        var operation = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText,
            new Dictionary<string, int>(), RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom),
            LocalizedText: atom.LocalizedText);
        var definition = new GeneratedCard(1, GeneratedCardType.Power, TargetMode.Other, GeneratedRarity.Uncommon,
            "", [], [operation], Character: GeneratedCharacter.Silent);
        var power = (ChaosCompositePower)ModelDb.Power<ChaosCompositePower>().ToMutable();
        AccessTools.Property(typeof(MegaCrit.Sts2.Core.Models.PowerModel), "Owner").SetValue(power, owner.Creature);
        AccessTools.Method(typeof(ChaosCompositePower), "ConfigureTinkered").Invoke(power,
            [GeneratedCharacter.Silent, 0, false, true, 0, 0, 0, null, null, "",
                CardTinkeringApi.SerializeCard(definition), -1]);
        var shiv = ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.Shiv>().ToMutable();
        shiv.Owner = owner;
        await power.AfterCardEnteredCombat(shiv);
        var keywords = new HashSet<CardKeyword>();
        Check(power.TryModifyKeywordsInCombat(shiv, keywords) && keywords.Contains(CardKeyword.Retain)
            && !shiv.Keywords.Contains(CardKeyword.Retain), "Shiv Retain is a live Power contribution, not a permanent card mutation");
        Check(!power.TryModifyKeywordsInCombat(shiv, keywords), "Shiv Retain preserves an existing contribution");
        await power.AfterRemoved(owner.Creature);
        Check(!shiv.Keywords.Contains(CardKeyword.Retain), "removing the Power leaves no permanent Retain on the Shiv");
    }

    private static void CheckFoolDescriptions(Player owner)
    {
        var catalog = CharacterComponentCatalogs.Get(GeneratedCharacter.Fool);
        GeneratorOperation Op(string template, int? parent = null)
        {
            var atom = catalog.Atoms.FirstOrDefault(atom => atom.Template == template);
            if (atom is null)
            {
                var text = template == "F:IfKi" ? "如果你拥有叽旋律，" : "你接下来打出的1张牌耗能为0[E]。";
                if (template is not ("F:IfKi" or "F:NextCardFree"))
                    throw new InvalidOperationException("Missing test component: " + template);
                var localized = (OperationLocalizedText?)AccessTools.Method(typeof(ChaosCardModel).Assembly
                    .GetType("ChaosCardGenerator.FoolComponentDefinitions", true), "Localized").Invoke(null, [template]);
                var legacy = new GeneratorOperation(template,
                    template == "F:IfKi" ? OperationScope.ConditionalTrigger : OperationScope.NonTargeted,
                    text, parent is null ? new Dictionary<string, int>() : new Dictionary<string, int> { ["triggerIndex"] = parent.Value },
                    LocalizedText: localized);
                return legacy with { RuntimeSpec = OperationRuntimeSpecCompiler.GetOrCompile(legacy) };
            }
            return new(template, atom.Scope, atom.ChineseText,
                parent is null ? new Dictionary<string, int>() : new Dictionary<string, int> { ["triggerIndex"] = parent.Value },
                RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom), LocalizedText: atom.LocalizedText);
        }
        ChaosCardModel Card(params GeneratorOperation[] operations)
        {
            var model = (ChaosCardModel)ChaosCardRegistry.Canonical(GeneratedCharacter.Fool, 0).ToMutable();
            model.Owner = owner;
            model.ApplyFreeformDefinition(new(3, GeneratedCardType.Attack, TargetMode.SingleEnemy, GeneratedRarity.Rare,
                "", [], operations, Character: GeneratedCharacter.Fool));
            return model;
        }
        foreach (var language in new[] { "zhs", "eng" })
        {
            LocManager.Instance.SetLanguage(language);
            var chinese = language == "zhs";
            var arrow = Card(Op("T:D"), Op("F:ShootArrow")).GetDescriptionForPile(PileType.Deck);
            Check(arrow.IndexOf(chinese ? "发射一支" : "Shoot an", StringComparison.Ordinal)
                    < arrow.IndexOf(chinese ? "造成" : "deal", StringComparison.Ordinal)
                && !arrow.Contains(chinese ? "。\n发射" : ".\nShoot", StringComparison.Ordinal),
                "generated Arrow stays in the damage sentence: " + language);
            foreach (var template in new[] { "F:CostPerMelody", "F:CostIfWahKi", "F:NextCardFree" })
            {
                var cost = Card(Op(template)).GetDescriptionForPile(PileType.Deck);
                var expectedGlyphs = chinese && template != "F:NextCardFree" ? 2 : 1;
                Check(!cost.Contains("[E]", StringComparison.Ordinal)
                    && System.Text.RegularExpressions.Regex.Matches(cost, "energy_icon\\.png").Count == expectedGlyphs,
                    "cost amount / final-cost glyphs without shorthand residue: " + template + "/" + language);
            }
            var nested = Card(Op("A:turnEnd"), Op("F:IfKi", 0), Op("F:NextCardFree", 1))
                .GetDescriptionForPile(PileType.Deck);
            Check(!nested.Contains(chinese ? "结束时。" : "turn.", StringComparison.Ordinal)
                && (!chinese || nested.Count(character => character == '。') == 1),
                "nested condition has one complete runtime sentence: " + language);
        }
    }

    private static void CheckShootTags(Player owner)
    {
        var catalog = CharacterComponentCatalogs.Get(GeneratedCharacter.Fool);
        var damageAtom = catalog.Recipes.Single(recipe => recipe.Id == "StrikeFool").Atoms[0];
        var arrowAtom = catalog.Atoms.Single(atom => atom.Template == "F:ShootArrow");
        GeneratorOperation Op(ComponentAtom atom) => new(atom.Template, atom.Scope, atom.ChineseText,
            new Dictionary<string, int>(), RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom), LocalizedText: atom.LocalizedText);
        var legacyDefinition = new GeneratedCard(1, GeneratedCardType.Attack, TargetMode.SingleEnemy,
            GeneratedRarity.Common, "", [ChaosCardGenerator.CardTag.Shoot], [Op(damageAtom)], Character: GeneratedCharacter.Fool);
        var legacy = (ChaosCardModel)ChaosCardRegistry.Canonical(GeneratedCharacter.Fool, 0).ToMutable();
        legacy.Owner = owner;
        legacy.FreeformDefinitionPayload = CardTinkeringApi.SerializeCard(legacyDefinition);
        AccessTools.Method(typeof(ChaosCardModel), "AfterDeserialized").Invoke(legacy, []);
        Check(!legacy.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shoot), "loaded generated card rejects Shoot inherited without a Shoot effect");
        var actual = (ChaosCardModel)ChaosCardRegistry.Canonical(GeneratedCharacter.Fool, 1).ToMutable();
        actual.Owner = owner;
        actual.ApplyFreeformDefinition(legacyDefinition with { Tags = [], Operations = [Op(arrowAtom), Op(damageAtom)] });
        var native = ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.QuickShot>().ToMutable();
        native.Owner = owner;
        Check(actual.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shoot) && native.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shoot),
            "real generated/native Shoot cards retain their retrieval tag");
        Check(new CardModel[] { legacy, actual, native }.Count(candidate => candidate.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shoot)) == 2,
            "Shoot search includes exactly the real generated and native Shoot cards");
        actual.ApplyFreeformDefinition(legacyDefinition);
        Check(!actual.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shoot), "removing Shoot effects invalidates the derived tag projection");
    }

    private static void CheckLibraryVisibility()
    {
        var grid = new MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary.NCardLibraryGrid();
        try
        {
            for (var repeat = 0; repeat < 2; repeat++)
            {
                grid.RefreshVisibility();
                var unlocked = (HashSet<ModelId>)AccessTools.Field(grid.GetType(), "_unlockedCards").GetValue(grid)!;
                var seen = (HashSet<ModelId>)AccessTools.Field(grid.GetType(), "_seenCards").GetValue(grid)!;
                var generated = ModelDb.AllCards.OfType<ChaosCardModel>().ToArray();
                Check(generated.Length > 600 && generated.All(card => unlocked.Contains(card.Id)),
                    "v113 library unlock refresh uses ModelIds for every generated slot");
                Check(unlocked.Contains(ModelDb.Card<AncientFuel>().Id), "Ancient Fuel remains unlocked");
                Check(seen.SetEquals(MegaCrit.Sts2.Core.Saves.SaveManager.Instance.Progress.DiscoveredCards),
                    "library unlock refresh preserves discovery history");
            }
        }
        finally { grid.Free(); }
    }

    private static void CheckNativeStartingCopies()
    {
        foreach (var (character, native) in new (GeneratedCharacter, CharacterModel)[]
        {
            (GeneratedCharacter.Ironclad, ModelDb.Character<Ironclad>()),
            (GeneratedCharacter.Silent, ModelDb.Character<Silent>()),
            (GeneratedCharacter.Defect, ModelDb.Character<Defect>()),
            (GeneratedCharacter.Necrobinder, ModelDb.Character<Necrobinder>()),
            (GeneratedCharacter.Regent, ModelDb.Character<Regent>()),
            (GeneratedCharacter.Fool, ModelDb.Character<Fool>())
        })
        {
            var catalog = CharacterComponentCatalogs.Get(character);
            foreach (var group in native.StartingDeck.GroupBy(card => card.GetType().Name))
            {
                var recipe = catalog.Recipes.Single(recipe => recipe.Id == group.Key);
                Check(CharacterComponentCatalogs.NativeOccurrenceWeight(recipe) == group.Count(),
                    "native starter occurrence weight " + character + "/" + group.Key);
            }
        }
    }

    private static async Task CheckLibraryScreen(NMainMenu menu)
    {
        var tree = menu.GetTree();
        var library = menu.SubmenuStack.GetSubmenuType<MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary.NCardLibrary>();
        for (var repeat = 0; repeat < 2; repeat++)
        {
            library.Initialize(null!);
            menu.SubmenuStack.Push(library);
            await tree.ToSignal(tree.CreateTimer(.3), SceneTreeTimer.SignalName.Timeout);
            var grid = (MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary.NCardLibraryGrid)
                AccessTools.Field(library.GetType(), "_grid").GetValue(library)!;
            Check(grid.VisibleCards.Any(), "card encyclopedia opens and renders, visit " + repeat);
            if (repeat == 0) CheckColorlessFilter(library);
            menu.SubmenuStack.Pop();
            await tree.ToSignal(tree.CreateTimer(.3), SceneTreeTimer.SignalName.Timeout);
        }
    }

    private static void CheckColorlessFilter(MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary.NCardLibrary library)
    {
        var settings = typeof(ChaosCardModel).Assembly.GetType("AutoAnthony.ChaosModSettings", true)!;
        var enabledField = AccessTools.Field(settings, "_enabled");
        var activeField = AccessTools.Field(typeof(ChaosRunDefinitions), "_runActive");
        var preserveField = AccessTools.Field(typeof(ChaosRunDefinitions), "_activePreserveOriginalCards");
        var enabled = enabledField.GetValue(null);
        var active = activeField.GetValue(null);
        var preserve = preserveField.GetValue(null);
        var filters = (Dictionary<MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary.NCardPoolFilter, Func<CardModel, bool>>)
            AccessTools.Field(library.GetType(), "_poolFilters").GetValue(library)!;
        var colorless = (MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary.NCardPoolFilter)
            AccessTools.Field(library.GetType(), "_colorlessFilter").GetValue(library)!;
        var accepts = filters[colorless];
        var generated = ChaosCardRegistry.Canonical(GeneratedCharacter.Colorless, 0);
        var original = ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.Finesse>();
        try
        {
            enabledField.SetValue(null, false);
            activeField.SetValue(null, false);
            preserveField.SetValue(null, true);
            Check(accepts(original) && !accepts(generated),
                "disabled vanilla run excludes registered generated Colorless cards, even with stale preserve setting");
            var savedInstance = generated.ToMutable();
            savedInstance.Owner = Player.CreateForNewRun(ModelDb.Character<Ironclad>(), UnlockState.all, 1);
            Check(savedInstance.GetDescriptionForPile(PileType.Deck).Length > 0,
                "hidden generated Colorless cards remain readable for saved/history instances");
            enabledField.SetValue(null, true);
            activeField.SetValue(null, true);
            preserveField.SetValue(null, false);
            Check(!accepts(original) && accepts(generated), "generated run shows only generated Colorless cards");
            preserveField.SetValue(null, true);
            Check(accepts(original) && accepts(generated), "preserved run shows both Colorless sets");
            activeField.SetValue(null, false);
            Check(accepts(original) && !accepts(generated), "leaving generated run restores native-only Colorless filter");
        }
        finally
        {
            enabledField.SetValue(null, enabled);
            activeField.SetValue(null, active);
            preserveField.SetValue(null, preserve);
        }
    }

    private static void CheckGeneratedLifecycle(Player owner)
    {
        var atom = CharacterComponentCatalogs.Get(GeneratedCharacter.Defect).Atoms.First(atom => atom.Template == "N:B");
        var operation = new GeneratorOperation(atom.Template, atom.Scope, atom.ChineseText, new Dictionary<string, int>(),
            RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom), LocalizedText: atom.LocalizedText);
        Check(OperationRuntimeSpecCompiler.TryReplaceFixedValue(operation, "block", 17, out operation),
            "bind per-instance Block to 17");
        var definition = new GeneratedCard(2, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Rare,
            CardDescriptionRenderer.Render([operation]), [ChaosCardGenerator.CardTag.Exhaust], [operation],
            Upgrade: new CardUpgradePlan(2, [new(CardUpgradeKind.GrantInnate)], "", [], ""),
            Character: GeneratedCharacter.Defect);
        var card = (ChaosCardModel)ChaosCardRegistry.Canonical(GeneratedCharacter.Defect, 0).ToMutable();
        card.Owner = owner;
        card.ApplyFreeformDefinition(definition);
        card.UpgradeInternal();
        card.AddKeyword(CardKeyword.Ethereal);
        card.RemoveKeyword(CardKeyword.Exhaust);
        AccessTools.Method(typeof(ChaosCardModel), "AfterDeserialized").Invoke(card, []);
        var outside = (HashSet<CardKeyword>)AccessTools.Field(typeof(CardModel), "_keywordsAddedFromOutside").GetValue(card)!;
        Check(card.Keywords.Contains(CardKeyword.Innate) && !outside.Contains(CardKeyword.Innate),
            "cache refresh keeps upgrade-only Innate separate from external keywords");
        Check(card.Keywords.Contains(CardKeyword.Ethereal) && !card.Keywords.Contains(CardKeyword.Exhaust),
            "cache refresh preserves external keyword additions and removals");
        card.DowngradeInternal();
        Check(!card.Keywords.Contains(CardKeyword.Innate) && card.Keywords.Contains(CardKeyword.Ethereal)
            && !card.Keywords.Contains(CardKeyword.Exhaust), "native downgrade removes Innate and preserves external changes");
        Check(card.EnergyCost.GetWithModifiers(CostModifiers.None) == 2 && card.DynamicVars["Block0"].BaseValue == 17,
            "native downgrade uses the per-instance definition instead of its canonical slot");
        card.UpgradeInternal();
        Check(card.Keywords.Contains(CardKeyword.Innate) && card.DynamicVars["Block0"].BaseValue == 17,
            "re-upgrading a downgraded instance applies its own plan once");
    }

    private static Texture2D? _testPortrait;
    private static bool SubstitutePortrait(ref Texture2D texture, ref bool __result)
    {
        if (_testPortrait is null) return true;
        texture = _testPortrait;
        __result = true;
        return false;
    }

    private static void CheckPortraitBlur(Player owner)
    {
        var card = (ChaosCardModel)ChaosCardRegistry.Canonical(GeneratedCharacter.Defect, 0).ToMutable();
        card.Owner = owner;
        // Build the portrait controls explicitly: headless/TestMode intentionally makes NCard.Create return null.
        // The actual native texture setter and AA compatibility postfix still run on real Godot controls.
        var node = new NCard();
        AccessTools.Field(typeof(NCard), "_model").SetValue(node, card);
        AccessTools.Field(typeof(NCard), "_visibility").SetValue(node, ModelVisibility.NotSeen);
        foreach (var name in new[] { "_portrait", "_ancientPortrait" })
        {
            var portrait = new TextureRect
            {
                Material = ResourceLoader.Load<ShaderMaterial>("res://scenes/cards/card_portrait_blur_material.tres")
            };
            node.AddChild(portrait);
            AccessTools.Field(typeof(NCard), name).SetValue(node, portrait);
        }
        var patch = typeof(ChaosCardModel).Assembly.GetType("AutoAnthony.ChaosDirectPortraitCompatibilityPatch", true)!;
        var adapter = typeof(ChaosCardModel).Assembly.GetType("AutoAnthony.ChaosPortraitCompatibility", true)!;
        var harmony = new Harmony("autoanthony.test-portrait-override");
        var resolver = AccessTools.Method(adapter, "TryResolveDirectTexture");
        harmony.Patch(resolver, prefix: new HarmonyMethod(typeof(NativeCardVersionRegression), nameof(SubstitutePortrait)));
        try
        {
            using var image = Image.CreateEmpty(128, 128, false, Image.Format.Rgba8);
            _testPortrait = new AtlasTexture
            {
                Atlas = ImageTexture.CreateFromImage(image), Region = new Rect2(16, 32, 64, 32)
            };
            AccessTools.Method(patch, "Postfix").Invoke(null, [node]);
            var expected = ImageHelper.GetAtlasUvRegion(_testPortrait);
            foreach (var name in new[] { "_portrait", "_ancientPortrait" })
            {
                var portrait = (TextureRect)AccessTools.Field(typeof(NCard), name).GetValue(node)!;
                Check(ReferenceEquals(portrait.Texture, _testPortrait)
                    && portrait.GetInstanceShaderParameter("uv_region").AsVector4().IsEqualApprox(expected),
                    "hidden portrait uses final art-mod atlas UVs: " + name);
            }
            // No-change refresh must keep the same atlas without rebuilding textures.
            AccessTools.Method(patch, "Postfix").Invoke(null, [node]);
        }
        finally
        {
            _testPortrait = null;
            harmony.UnpatchAll(harmony.Id);
            node.Free();
        }
    }
}
