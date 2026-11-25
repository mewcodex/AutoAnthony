using Godot;
using HarmonyLib;
using AutoAnthony;
using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.FoolArrows;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using GTag = ChaosCardGenerator.CardTag;

[ModInitializer(nameof(Init))]
public static class CombatSmoke
{
    private static bool _started;
    private static Player _owner = null!;
    private static readonly ThrowingPlayerChoiceContext Context = new();
    private static readonly IComponentCatalog Catalog = CharacterComponentCatalogs.Get(GeneratedCharacter.Fool);
    public static void Init()
    {
        if (!OS.GetUserDataDir().Contains("AutoAnthonyV113Smoke-20261112"))
            throw new InvalidOperationException("An isolated test profile is required.");
        new Harmony("autoanthony.fool.combat-regression").Patch(
            AccessTools.Method(typeof(NMainMenu), nameof(NMainMenu._Ready)),
            postfix: new HarmonyMethod(typeof(CombatSmoke), nameof(Ready)));
        new Harmony("autoanthony.fool.combat-regression.ftue").Patch(
            AccessTools.Method(typeof(SaveManager), nameof(SaveManager.SeenFtue)),
            prefix: new HarmonyMethod(typeof(CombatSmoke), nameof(SkipTutorial)));
    }
    private static bool SkipTutorial(ref bool __result) { __result = true; return false; }
    private static void Ready(NMainMenu __instance)
    {
        if (_started) return;
        _started = true;
        _ = Run(__instance.GetTree());
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        GD.Print("AA_FOOL_COMBAT_PASS " + message);
    }
    private static GeneratorOperation Op(string template, int? amount = null, int? owner = null)
    {
        var atom = Catalog.Atoms.FirstOrDefault(atom => atom.Template == template)
            ?? Enum.GetValues<GeneratedCharacter>().SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms)
                .FirstOrDefault(atom => atom.Template == template);
        GeneratorOperation operation;
        if (atom is not null)
            operation = new(template, atom.Scope, atom.ChineseText, new Dictionary<string, int>(),
                RequiresSingleTarget: atom.RequiresSingleTarget, RuntimeSpec: atom.RuntimeSpec, LocalizedText: atom.LocalizedText);
        else
        {
            var (text, scope) = template switch
            {
                "F:ExhaustShoot" => ("消耗本场战斗中所有尚未消耗的发射牌。", OperationScope.NonTargeted),
                "F:CreateTheShot" => ("将一张全力射击加入手牌。其基础伤害为刚才消耗的牌数量的10倍。", OperationScope.NonTargeted),
                "F:NextCardFree" => ("你接下来打出的1张牌耗能为0[E]。", OperationScope.NonTargeted),
                "F:IfKi" => ("如果你拥有叽旋律，", OperationScope.ConditionalTrigger),
                _ => throw new InvalidOperationException("Missing test component: " + template)
            };
            var localized = (OperationLocalizedText?)AccessTools.Method(typeof(ChaosCardModel).Assembly
                .GetType("ChaosCardGenerator.FoolComponentDefinitions", true), "Localized").Invoke(null, [template]);
            operation = new(template, scope, text, new Dictionary<string, int>(), LocalizedText: localized);
        }
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(operation);
        if (amount is not null)
        {
            var first = spec.Values.First(value => value.Explicit);
            spec = spec with { Values = spec.Values.Select(value => value.Id == first.Id ? value with { BaseValue = amount.Value } : value).ToArray() };
        }
        return operation with { RuntimeSpec = spec,
            Parameters = owner is null ? [] : new Dictionary<string, int> { ["triggerIndex"] = owner.Value } };
    }
    private static async Task<ChaosCardModel> Card(GeneratorOperation[] operations, GTag[]? tags = null,
        GeneratedCardType type = GeneratedCardType.Skill, int cost = 1)
    {
        var definition = new GeneratedCard(cost, type, TargetMode.Other, GeneratedRarity.Rare,
            CardDescriptionRenderer.Render(operations), tags ?? [], operations,
            new("回归测试", "Regression Test", []), EnglishDescription: EnglishCardDescriptionRenderer.Render(operations),
            Character: GeneratedCharacter.Fool);
        var card = AutoAnthonyFreeformCardApi.CreateForCombat(_owner, definition);
        await CardPileCmd.AddGeneratedCardToCombat(card, PileType.Hand, _owner);
        return card;
    }
    private static async Task Play(CardModel card) => await CardCmd.AutoPlay(Context, card,
        _owner.Creature.CombatState!.HittableEnemies.First(), skipCardPileVisuals: false).WaitAsync(TimeSpan.FromSeconds(20));

    private static async Task CheckFatal()
    {
            var fatal = (ChaosCardModel)await Card([Op("T:D", 12), Op("C:ifFatal"),
                Op("N:AllD", 23, 1), Op("CL:IncreaseRollingDamage", 33, 1)], type: GeneratedCardType.Attack);
            var target = _owner.Creature.CombatState!.HittableEnemies.First();
            var other = _owner.Creature.CombatState.HittableEnemies.Last();
            Check(target != other, "Fatal fixture has a surviving enemy");
            var hp = other.CurrentHp;
            await Play(fatal);
            Check(other.CurrentHp == hp && (int)AccessTools.Method(typeof(ChaosCardModel), "OperationDamageBonus").Invoke(fatal, [2])! == 0,
                "Fatal area damage and growth do not run without a kill");
            await CardPileCmd.Add(fatal, PileType.Hand);
            target.SetCurrentHpInternal(1);
            await Play(fatal);
            Check(hp - other.CurrentHp == 23, "Fatal executes area damage after selected enemy dies");
            Check((int)AccessTools.Method(typeof(ChaosCardModel), "OperationDamageBonus").Invoke(fatal, [2])! == 33, "Fatal grows only its area damage after resolving");
    }
    private static async Task CheckShootGeneration()
    {
        var hand = PileType.Hand.GetPile(_owner);
        var pool = _owner.Character.CardPool.GetUnlockedCards(_owner.UnlockState,
            _owner.RunState.CardMultiplayerConstraint).ToArray();
        foreach (var candidate in pool.Where(candidate => candidate.Tags.Contains(
                     MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shoot)))
        {
            Check(candidate is not ChaosCardModel generated || generated.Generated.Operations.Any(FoolComponentRules.IsShootModifier),
                "Shoot candidate has a real Shoot operation: " + candidate.Id);
            var clone = _owner.Creature.CombatState!.CreateCard(candidate, _owner);
            Check(clone.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shoot)
                && (clone is not ChaosCardModel created || created.Generated.Operations.Any(FoolComponentRules.IsShootModifier)),
                "Shoot candidate retains its effect when created: " + candidate.Id);
            await CardPileCmd.Add(clone, PileType.Discard);
            await CardPileCmd.RemoveFromCombat(clone, skipVisuals: true);
        }
        for (var native = 0; native < 2; native++)
        {
            CardModel source = native == 0 ? await Card([Op("F:RandomShootFree", 2)])
                : _owner.Creature.CombatState!.CreateCard(ModelDb.Card<BlotOutTheSky>(), _owner);
            if (source.Pile is null) await CardPileCmd.AddGeneratedCardToCombat(source, PileType.Hand, _owner);
            for (var sample = 0; sample < 12; sample++)
            {
                if (source.Pile != hand) await CardPileCmd.Add(source, PileType.Hand);
                var before = hand.Cards.ToHashSet();
                await Play(source);
                var added = hand.Cards.Where(candidate => !before.Contains(candidate)).ToArray();
                Check(added.Length == (native == 0 ? 2 : 1), "Shoot generator produced requested count");
                foreach (var created in added)
                {
                    Check(created.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.Shoot)
                        && (created is not ChaosCardModel chaos || chaos.Generated.Operations.Any(FoolComponentRules.IsShootModifier)),
                        "Generated Shoot card really shoots: " + created.Id);
                    var description = created.GetDescriptionForPile(PileType.Hand);
                    Check(description.Contains("发射", StringComparison.Ordinal)
                        || description.Contains("Shoot", StringComparison.OrdinalIgnoreCase),
                        "Generated Shoot card describes its shot: " + created.Id + " " + description);
                }
                await CardPileCmd.RemoveFromCombat(added, skipVisuals: true);
            }
        }
    }
    private static async Task CheckV114Effects()
    {
        var selector = new MegaCrit.Sts2.Core.TestSupport.TestCardSelector();
        using var selection = RunManager.Instance.CardSelectorStack.UseSelector(_owner, selector);
        var hand = PileType.Hand.GetPile(_owner);
        await CardPileCmd.RemoveFromCombat(hand.Cards.ToArray(), skipVisuals: true);
        foreach (var power in _owner.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        CombatManager.Instance.History.Clear();
        var transform = await Card([Op("F:TransformHandToTheShot", 2)]);
        var first = await Card([Op("N:B", 4)]);
        var second = await Card([Op("N:B", 5)]);
        selector.PrepareToSelect(new CardModel[] { first, second });
        await Play(transform);
        Check(hand.Cards.Count == 2 && hand.Cards.All(candidate => candidate is TheShot
            && candidate.DynamicVars.Damage.BaseValue == 20), "v114 Florescence transforms the requested hand cards");
        await CardPileCmd.RemoveFromCombat(hand.Cards.ToArray(), skipVisuals: true);
        transform = await Card([Op("F:TransformHandToTheShot", 1)]);
        transform.ApplyFreeformDefinition(transform.Generated with { Upgrade = new CardUpgradePlan(1,
            [new(CardUpgradeKind.UpgradeReferencedCards, 0)], "", [], "") });
        CardCmd.Upgrade(transform);
        first = await Card([Op("N:B", 4)]);
        selector.PrepareToSelect(new CardModel[] { first });
        await Play(transform);
        Check(hand.Cards.Single() is TheShot upgraded && upgraded.IsUpgraded
            && upgraded.DynamicVars.Damage.BaseValue == 25, "v114 Florescence upgrades the transformed Shot and its preview reference");
        await CardPileCmd.RemoveFromCombat(hand.Cards.ToArray(), skipVisuals: true);
        var growth = await Card([Op("N:B", 8), Op("F:TransformSadnessToCopy", 1)]);
        await CardPileCmd.AddGeneratedCardToCombat(_owner.Creature.CombatState!.CreateCard<Sadness>(_owner), PileType.Hand, _owner);
        await Play(growth);
        Check(hand.Cards.Single() is ChaosCardModel copy && !ReferenceEquals(copy, growth)
            && copy.Generated.Operations.Any(operation => operation.Template == "F:TransformSadnessToCopy"),
            "v114 Growth transforms Sadness into a real copy of its source");
        await CardPileCmd.RemoveFromCombat(hand.Cards.ToArray(), skipVisuals: true);
        var upgradeTarget = _owner.Creature.CombatState!.CreateCard<DefendFool>(_owner);
        await CardPileCmd.AddGeneratedCardToCombat(upgradeTarget, PileType.Hand, _owner);
        await Play(await Card([Op("F:OnKiGained"), Op("F:UpgradeRandomHand", 1, 0)], type: GeneratedCardType.Power));
        var ki = _owner.Creature.CombatState!.CreateCard<KiHop>(_owner);
        await CardPileCmd.AddGeneratedCardToCombat(ki, PileType.Hand, _owner);
        await Play(ki);
        Check(upgradeTarget.IsUpgraded, "v114 Tune upgrades a hand card on the first Ki Melody");
        await Play(await Card([Op("F:NextCardFree", 2)]));
        var freeTarget = await Card([Op("N:B", 4)], cost: 2);
        Check(freeTarget.EnergyCost.GetWithModifiers(CostModifiers.All) == 0,
            "legacy FreeCard still reduces the next card's cost under v114");
        await Play(freeTarget);
        Check(_owner.Creature.GetPower<ChaosNextCardsFreePower>()?.Amount == 1,
            "legacy FreeCard consumes exactly one stack per card");
        foreach (var power in _owner.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
    }
    private static async Task CheckArrowConditions(CardModel shot, Creature target)
    {
        var conditionalBlock = await Card([Op("F:IfArrow"), Op("N:B", 3, 0)]);
        var block = _owner.Creature.Block;
        Check(_owner.PlayerCombatState!.Arrows.Count == 1 && !ArrowCmd.PlayerHasAnyArrows(_owner),
            "an Away Arrow slot is not an available Arrow");
        await Play(conditionalBlock);
        Check(_owner.Creature.Block == block, "If Arrow is false while the only Arrow is impaled");

        await Play(await Card([Op("F:IfOwnImpaled"), Op("F:Dislodge", owner: 0), Op("N:B", 3, 0)]));
        Check(_owner.Creature.Block - block == 3 && ArrowCmd.GetAvailableArrowCount(_owner) == 1,
            "own-Impaled condition keeps both payoffs after Dislodge removes the condition");
        block = _owner.Creature.Block;
        await Play(conditionalBlock);
        Check(_owner.Creature.Block - block == 3, "If Arrow becomes true after Dislodge retrieves it");

        await Play(shot);
        target.GetPowerInstances<ImpaledPower>().Single(power => power.Applier == _owner.Creature).HitCounter = 1;
        block = _owner.Creature.Block;
        await Play(await Card([Op("T:D", 1), Op("F:IfImpaled"), Op("N:B", 3, 1)], type: GeneratedCardType.Attack));
        Check(_owner.Creature.Block - block == 3 && !ArrowCmd.CreatureIsImpaledByArrow(target)
                && ArrowCmd.GetAvailableArrowCount(_owner) == 1,
            "Ki Hop-style condition remembers Impaled before the hit that retrieves the last Arrow");

        // Leave the fixture in its original one-Arrow/Away state for the following return-card checks.
        await Play(shot);
        var slot = _owner.PlayerCombatState.Arrows.Single();
        slot.Status = ArrowStatus.Away;
        try
        {
            block = _owner.Creature.Block;
            await Play(conditionalBlock);
            Check(_owner.Creature.Block == block, "an unavailable Arrow does not satisfy If Arrow");
        }
        finally { slot.Status = ArrowStatus.Away; }
    }
    private static async Task Run(SceneTree tree)
    {
        try
        {
            await tree.ToSignal(tree.CreateTimer(1), SceneTreeTimer.SignalName.Timeout);
            var settings = typeof(ChaosCardModel).Assembly.GetType("AutoAnthony.ChaosModSettings", true)!;
            foreach (var field in new[] { "_enabled", "_addGeneratedCards", "_replaceStartingCards", "_numericBalanceOptimization" })
                AccessTools.Field(settings, field).SetValue(null, true);
            foreach (var field in new[] { "_ultimateChaos", "_numericRandomMode", "_preserveOriginalCards", "_biweeklyBalanceAdjustments" })
                AccessTools.Field(settings, field).SetValue(null, false);
            var ultimateRegent = System.Environment.GetEnvironmentVariable("AA_FOOL_TEST_ULT_REGENT") == "1";
            if (ultimateRegent) AccessTools.Field(settings, "_ultimateChaos").SetValue(null, true);
            _owner = Player.CreateForNewRun(ultimateRegent ? ModelDb.Character<Regent>() : ModelDb.Character<Fool>(), UnlockState.all, 1);
            var acts = ActModel.GetRandomList(new Rng(1, "combat"), UnlockState.all, false).ToArray();
            await NGame.Instance!.StartNewSingleplayerRun(_owner, true, acts, [], "AA_FOOL_COMBAT", GameMode.Standard, false);
            NModalContainer.Instance?.Clear();
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<CultistsNormal>().ToMutable(), showTransition: false)
                .WaitAsync(TimeSpan.FromSeconds(30));
            for (var i = 0; i < 100 && !CombatManager.Instance.IsInProgress; i++)
                await tree.ToSignal(tree.CreateTimer(.1), SceneTreeTimer.SignalName.Timeout);
            Check(CombatManager.Instance.IsInProgress, "entered a real combat");
            Check(_owner.PlayerCombatState!.Arrows.Count == 1, "one initial Arrow slot is available across character modes");
            await CardPileCmd.RemoveFromCombat(_owner.PlayerCombatState!.AllCards.ToArray(), skipVisuals: true);
            foreach (var enemy in _owner.Creature.CombatState!.HittableEnemies)
            {
                enemy.SetMaxHpInternal(10000); enemy.SetCurrentHpInternal(10000);
                foreach (var power in enemy.Powers.ToArray()) await PowerCmd.Remove(power);
            }
            foreach (var power in _owner.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
            if (System.Environment.GetEnvironmentVariable("AA_V114_ONLY") == "1")
            {
                await CheckV114Effects();
                GD.Print("AA_V114_COMBAT_COMPLETE");
                tree.Quit(); return;
            }
            if (System.Environment.GetEnvironmentVariable("AA_SHOOT_GENERATION_ONLY") == "1")
            {
                await CheckShootGeneration();
                GD.Print("AA_SHOOT_GENERATION_COMPLETE");
                tree.Quit(); return;
            }
            if (System.Environment.GetEnvironmentVariable("AA_FATAL_ONLY") == "1")
            {
                await CheckFatal();
                GD.Print("AA_FATAL_COMPLETE");
                tree.Quit(); return;
            }
            var target = _owner.Creature.CombatState!.HittableEnemies.First();
            var arrow = await Card([Op("F:ShootArrow"), Op("T:D", 6)], type: GeneratedCardType.Attack);
            await PowerCmd.Apply<AimPower>(Context, _owner.Creature, 4, _owner.Creature, null);
            var arrowsBefore = ArrowCmd.GetAvailableArrowCount(_owner);
            var hp = target.CurrentHp;
            await Play(arrow);
            Check(hp - target.CurrentHp == 10, "Arrow damage includes native Aim");
            Check(ArrowCmd.GetAvailableArrowCount(_owner) == arrowsBefore - 1 && ArrowCmd.CreatureIsImpaledByArrow(target),
                "one Arrow spent and target impaled");
            await PowerCmd.Remove<AimPower>(_owner.Creature);
            await CheckArrowConditions(arrow, target);

            var returning = await Card([Op("N:B", 4), Op("R:ReturnThisToHand")]);
            await Play(returning);
            Check(returning.Pile?.Type == PileType.Hand, "unconditional return resolves after play");
            var conditional = await Card([Op("F:IfArrow"), Op("R:ReturnThisToHand", owner: 0)]);
            await Play(conditional);
            Check(conditional.Pile?.Type == PileType.Discard, "false condition does not request return");
            ArrowCmd.GainArrowSlot(_owner);
            await Play(conditional);
            Check(conditional.Pile?.Type == PileType.Hand, "true condition executes its return payoff");
            var exhausted = await Card([Op("N:B", 4), Op("R:ReturnThisToHand")], [GTag.Exhaust]);
            await Play(exhausted);
            Check(exhausted.Pile?.Type == PileType.Hand, "Exhaust event completes before requested return");
            var count = PileType.Hand.GetPile(_owner).Cards.OfType<Sadness>().Count();
            await Play(await Card([Op("T:D", 10), Op("F:TransformThisToSadness")], type: GeneratedCardType.Attack));
            Check(PileType.Hand.GetPile(_owner).Cards.OfType<Sadness>().Count() == count + 1,
                "Mourning replacement returns one Sadness to hand");

            var joy = PileType.Hand.GetPile(_owner).Cards.OfType<Joy>().Count();
            await Play(await Card([Op("F:CreateJoy", 2)]));
            Check(PileType.Hand.GetPile(_owner).Cards.OfType<Joy>().Count() == joy + 2, "creates multiple Joy cards");
            await Play(await Card([Op("F:CreateSadness", 2)]));
            var sadness = PileType.Hand.GetPile(_owner).Cards.OfType<Sadness>().Count();
            var block = _owner.Creature.Block;
            await Play(await Card([Op("F:ExhaustSadness"), Op("F:ForEachSadnessExhausted"), Op("N:B", 2, 1)]));
            Check(_owner.Creature.Block - block == sadness * 2, "Sadness repeat uses the actual preceding Exhaust count");

            await Play(await Card([Op("F:CreateSadness", 2)]));
            sadness = PileType.Hand.GetPile(_owner).Cards.OfType<Sadness>().Count();
            await Play(await Card([Op("F:ExhaustSadness"), Op("F:CreateTheShot", 10)]));
            var shot = PileType.Hand.GetPile(_owner).Cards.OfType<TheShot>().Last();
            hp = target.CurrentHp;
            await Play(shot);
            Check(hp - target.CurrentHp == sadness * 10, "The Shot derives damage from its own Exhaust source");

            await Play(await Card([Op("F:OnMelodyGained"), Op("N:B", 2, 0)], type: GeneratedCardType.Power));
            block = _owner.Creature.Block;
            await Play(await Card([Op("F:Stretch", 1)], [GTag.Wah, GTag.Cho]));
            Check(_owner.Creature.Block - block == 4, "each newly gained Melody fires once");
            block = _owner.Creature.Block;
            await Play(await Card([Op("F:Stretch", 1)], [GTag.Wah]));
            Check(_owner.Creature.Block == block, "repeating a Melody does not regain it");
            await Play(await Card([Op("F:Stretch", 1)], [GTag.Ki]));
            await PowerCmd.Remove<StretchPower>(_owner.Creature);
            block = _owner.Creature.Block;
            await Play(await Card([Op("F:ForEachMelody"), Op("N:B", 2, 0)]));
            Check(_owner.Creature.Block - block == 6,
                "three Melodies repeat Block without a squared count");

            await PowerCmd.Remove<StretchPower>(_owner.Creature);
            await PowerCmd.Apply<StretchPower>(Context, _owner.Creature, 5, _owner.Creature, null);
            block = _owner.Creature.Block;
            await Play(await Card([Op("N:B", 4), Op("F:RefundStretch")]));
            Check(_owner.Creature.Block - block == 9 && _owner.Creature.GetPower<StretchPower>()?.Amount == 5,
                "Cho Ki refunds the native Stretch consumed by its Block");

            var adroitOnly = await Card([Op("F:Aim", 1)]);
            Check(!adroitOnly.GainsBlock, "Adroit fixture has no intrinsic Block effect");
            CardCmd.Enchant<Adroit>(adroitOnly, 3);
            block = _owner.Creature.Block;
            await Play(adroitOnly);
            Check(_owner.Creature.Block - block == 8 && _owner.Creature.GetPower<StretchPower>() is null,
                "v113.1 Adroit Block on a generated non-Block card consumes and gains Stretch");

            await PowerCmd.Apply<StretchPower>(Context, _owner.Creature, 5, _owner.Creature, null);
            var noBlock = await Card([Op("F:Aim", 1)]);
            var stretch = _owner.Creature.GetPower<StretchPower>()!;
            Check(stretch.ModifyBlockAdditive(_owner.Creature, 0,
                    MegaCrit.Sts2.Core.ValueProps.ValueProp.Move, noBlock, null) == 0,
                "v113.1 non-Block cards do not preview phantom Stretch Block");
            block = _owner.Creature.Block;
            await Play(noBlock);
            Check(_owner.Creature.Block == block && stretch.Amount == 5,
                "non-Block card preserves Stretch for the next Block effect");
            await PowerCmd.Remove<StretchPower>(_owner.Creature);

            await Play(await Card([Op("A:turnEnd"), Op("F:IfKi", owner: 0), Op("F:NextCardFree", 1, 1)],
                type: GeneratedCardType.Power));
            Check(_owner.Creature.GetPower<ChaosNextCardsFreePower>() is null, "nested Tune does not fire when the Power is played");
            foreach (var power in _owner.Creature.Powers.OfType<ChaosCompositePower>().ToArray())
                await power.AfterAutoPostPlayPhaseEntered(Context, _owner);
            Check(_owner.Creature.GetPower<ChaosNextCardsFreePower>()?.Amount == 1, "nested Tune checks Ki at turn end");

            var growth = await Card([Op("F:Aim", 5), Op("F:UntilNonMinionDies"), Op("F:IncreaseAimRun", 2, 1)], [GTag.Exhaust]);
            await Play(growth);
            foreach (var power in _owner.Creature.Powers.OfType<ChaosCompositePower>().ToArray())
            {
                await power.AfterDeath(Context, target, null, false, 0);
                await power.AfterDeath(Context, target, null, false, 0);
            }
            Check(growth.ExtraAim == 2, "permanent Aim reward fires only once for its eligible death gate");
            await PowerCmd.Remove<AimPower>(_owner.Creature);
            ArrowCmd.GainArrowSlot(_owner);
            _owner.PlayerCombatState!.LoseEnergy(_owner.PlayerCombatState.Energy);
            hp = target.CurrentHp;
            await Play(await Card([Op("F:ShootArrow"), Op("T:D_EnergyX", 2)], type: GeneratedCardType.Attack, cost: -1));
            Check(hp == target.CurrentHp, "X=0 Arrow attack completes without phantom damage");
            ArrowCmd.GainArrowSlot(_owner);
            _owner.PlayerCombatState.GainEnergy(3);
            hp = target.CurrentHp;
            await Play(await Card([Op("F:ShootArrow"), Op("T:D_EnergyX", 2)], type: GeneratedCardType.Attack, cost: -1));
            Check(hp - target.CurrentHp == 6, "X=3 Arrow attack resolves three hits");
            await Play(await Card([Op("F:ForEachEnergyX"), Op("F:Aim", 2, 0)], cost: -1));
            Check(_owner.Creature.GetPower<AimPower>()?.Amount == 6, "X=3 repeat applies the payoff three times");
            var handBefore = PileType.Hand.GetPile(_owner).Cards.Count;
            await Play(await Card([Op("F:RandomShootFree", 2)]));
            Check(PileType.Hand.GetPile(_owner).Cards.Count == handBefore + 2, "creates two effective-pool Shoot cards");
            handBefore = PileType.Hand.GetPile(_owner).Cards.Count;
            await Play(await Card([Op("F:RandomTarot", 2)]));
            Check(PileType.Hand.GetPile(_owner).Cards.Count == handBefore + 2, "creates two native Tarot cards");
            await CheckV114Effects();
            await CheckFatal();
            GD.Print("AA_FOOL_COMBAT_COMPLETE");
            await NGame.Instance.ReturnToMainMenu();
            tree.Quit();
        }
        catch (Exception exception)
        {
            GD.PrintErr("AA_FOOL_COMBAT_FAIL " + exception);
            tree.Quit(1);
        }
    }
}
