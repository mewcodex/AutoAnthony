using Godot;
using HarmonyLib;
using AutoAnthony;
using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Encounters;
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
                .First(atom => atom.Template == template);
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(atom);
        if (amount is not null)
        {
            var first = spec.Values.First(value => value.Explicit);
            spec = spec with { Values = spec.Values.Select(value => value.Id == first.Id ? value with { BaseValue = amount.Value } : value).ToArray() };
        }
        return new(template, atom.Scope, atom.ChineseText,
            owner is null ? [] : new Dictionary<string, int> { ["triggerIndex"] = owner.Value },
            RequiresSingleTarget: atom.RequiresSingleTarget, RuntimeSpec: spec, LocalizedText: atom.LocalizedText);
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
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<AxebotsNormal>().ToMutable(), showTransition: false)
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
            var target = _owner.Creature.CombatState.HittableEnemies.First();
            var arrow = await Card([Op("F:ShootArrow"), Op("T:D", 6)], type: GeneratedCardType.Attack);
            await PowerCmd.Apply<AimPower>(Context, _owner.Creature, 4, _owner.Creature, null);
            var arrowsBefore = ArrowCmd.GetAvailableArrowCount(_owner);
            var hp = target.CurrentHp;
            await Play(arrow);
            Check(hp - target.CurrentHp == 10, "Arrow damage includes native Aim");
            Check(ArrowCmd.GetAvailableArrowCount(_owner) == arrowsBefore - 1 && ArrowCmd.CreatureIsImpaledByArrow(target),
                "one Arrow spent and target impaled");
            await PowerCmd.Remove<AimPower>(_owner.Creature);

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

            await Play(await Card([Op("A:turnEnd"), Op("F:IfKi", owner: 0), Op("F:NextCardFree", 1, 1)],
                type: GeneratedCardType.Power));
            Check(_owner.Creature.GetPower<FreeCardPower>() is null, "nested Tune does not fire when the Power is played");
            foreach (var power in _owner.Creature.Powers.OfType<ChaosCompositePower>().ToArray())
                await power.AfterAutoPostPlayPhaseEntered(Context, _owner);
            Check(_owner.Creature.GetPower<FreeCardPower>()?.Amount == 1, "nested Tune checks Ki at turn end");

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
