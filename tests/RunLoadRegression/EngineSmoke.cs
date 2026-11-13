using Godot;
using HarmonyLib;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using AutoAnthony;
using ChaosCardGenerator;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

[ModInitializer(nameof(Init))]
public static class EngineSmoke
{
    private static bool _started;
    private static int _generatedPools;
    public static void Init()
    {
        GD.Print("AA_SMOKE_USER_DIR " + OS.GetUserDataDir());
        if (!OS.GetUserDataDir().Contains("AutoAnthonyV113Smoke-20261112"))
            throw new InvalidOperationException("Isolated test profile was not selected.");
        new Harmony("autoanthony.v113.engine-smoke").Patch(
            AccessTools.Method(typeof(NMainMenu), nameof(NMainMenu._Ready)),
            postfix: new HarmonyMethod(typeof(EngineSmoke), nameof(MainMenuReady)));
        new Harmony("autoanthony.v113.engine-smoke.generations").Patch(
            AccessTools.Method(typeof(ChaosRunDefinitions), "GenerateDefinitions"),
            prefix: new HarmonyMethod(typeof(EngineSmoke), nameof(PoolGenerating)));
    }
    private static void PoolGenerating() => _generatedPools++;
    private static void MainMenuReady(NMainMenu __instance)
    {
        if (_started) return;
        _started = true;
        _ = Run(__instance.GetTree());
    }
    private static async Task Run(SceneTree tree)
    {
        try
        {
            await tree.ToSignal(tree.CreateTimer(1), SceneTreeTimer.SignalName.Timeout);
            var expectedFile = System.IO.Path.Combine(OS.GetUserDataDir(), "aa-0160-load-expected.json");
            if (System.Environment.GetEnvironmentVariable("AA_SMOKE_LOAD_ONLY") == "1")
            {
                var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    System.IO.File.ReadAllText(expectedFile))!;
                var generationsBeforeContinue = _generatedPools;
                await Continue();
                if (_generatedPools != generationsBeforeContinue)
                    throw new InvalidOperationException("Continuing a complete same-version snapshot regenerated pools.");
                CheckLoaded(expected["pool"], expected["deck"], "fresh process");
                await NGame.Instance!.ReturnToMainMenu();
                GD.Print("AA_SMOKE_FRESH_LOAD_PASS");
                tree.Quit();
                return;
            }
            var settings = typeof(ChaosCardModel).Assembly.GetType("AutoAnthony.ChaosModSettings", true)!;
            foreach (var field in new[] { "_enabled", "_addGeneratedCards", "_replaceStartingCards", "_numericBalanceOptimization" })
                AccessTools.Field(settings, field).SetValue(null, true);
            AccessTools.Field(settings, "_showCardInternalIds").SetValue(null, true);
            foreach (var field in new[] { "_ultimateChaos", "_numericRandomMode", "_preserveOriginalCards", "_biweeklyBalanceAdjustments" })
                AccessTools.Field(settings, field)?.SetValue(null, false);
            var cases = new (CharacterModel Character, bool Ultimate)[] {
                (ModelDb.Character<Ironclad>(), false), (ModelDb.Character<Silent>(), false),
                (ModelDb.Character<Defect>(), false), (ModelDb.Character<Necrobinder>(), false),
                (ModelDb.Character<Regent>(), false), (ModelDb.Character<Fool>(), false),
                (ModelDb.Character<Regent>(), true), (ModelDb.Character<Fool>(), true) };
            foreach (var (character, ultimate) in cases)
            {
                if (System.Environment.GetEnvironmentVariable("AA_SMOKE_FOOL_ONLY") == "1" && character is not Fool) continue;
                if (System.Environment.GetEnvironmentVariable("AA_SMOKE_ULTIMATE_ONLY") == "1" && !ultimate) continue;
                AccessTools.Field(settings, "_ultimateChaos").SetValue(null, ultimate);
                var player = Player.CreateForNewRun(character, UnlockState.all, 1);
                var acts = ActModel.GetRandomList(new Rng(1, "smoke"), UnlockState.all, false).ToArray();
                var run = await NGame.Instance!.StartNewSingleplayerRun(player, true, acts, [],
                    "AA_V113_SMOKE_" + character.Id.Entry, GameMode.Standard, false);
                CheckPreparedPools();
                if (player.Deck.Cards.OfType<ChaosCardModel>().Count() != ChaosRunDefinitions.BasicCountFor(
                    player.Deck.Cards.OfType<ChaosCardModel>().First().Generated.Character))
                    throw new InvalidOperationException("Incomplete starter deck for " + character.Id);
                foreach (var card in player.Deck.Cards.OfType<ChaosCardModel>())
                {
                    var description = card.GetDescriptionForPile(PileType.Deck);
                    if (description.Contains(":diff()") || description.Contains(":energyIcons()"))
                        throw new InvalidOperationException("Unresolved description for " + card.Id);
                    var tips = card.HoverTips.ToArray();
                    if (!tips.Any(tip => tip.Id.StartsWith(
                            "LocString with Title=main_menu_ui.AUTO_ANTHONY_CARD_INTERNAL_ID_HOVER_TITLE")))
                        throw new InvalidOperationException("Missing internal-ID tooltip for " + card.Id);
                    if (ultimate && !tips.Any(tip => tip.Id.Contains("_MODE_HOVER_TITLE")))
                        throw new InvalidOperationException("Missing generation-mode tooltip for " + card.Id);
                }
                GD.Print("AA_SMOKE_RUN_PASS " + character.Id + " deck=" + player.Deck.Cards.Count);
                var upgraded = player.Deck.Cards.OfType<ChaosCardModel>().First(card => card.IsUpgradable);
                upgraded.UpgradeInternal();
                upgraded.FinalizeUpgradeInternal();
                if (character is Fool) upgraded.ExtraAim = 3;
                await SaveManager.Instance.SaveRun(null);
                var pool = PoolFingerprint();
                var deck = DeckFingerprint(player);
                System.IO.File.WriteAllText(expectedFile, JsonSerializer.Serialize(
                    new Dictionary<string, string> { ["pool"] = pool, ["deck"] = deck }));
                await NGame.Instance.ReturnToMainMenu();
                await Continue();
                CheckLoaded(pool, deck, character.Id.ToString());
                await NGame.Instance.ReturnToMainMenu();
            }
            GD.Print("AA_SMOKE_PASS all six characters started, saved, resumed and returned to menu");
            tree.Quit();
        }
        catch (Exception exception)
        {
            GD.PrintErr("AA_SMOKE_FAIL " + exception);
            tree.Quit(1);
        }
    }

    private static void CheckPreparedPools()
    {
        var pools = ChaosRunDefinitions.GetAllCards();
        if (pools.Count != 7 || pools.Values.Sum(pool => pool.Count) != 606)
            throw new InvalidOperationException("Incomplete generated pool set.");
        foreach (var (character, definitions) in pools)
        {
            var cards = definitions.Select(definition => definition.Card).ToArray();
            foreach (var card in cards) CardTemplateValidator.Validate(card);
            if (cards.Select(GeneratedCardEffectIdentity.Signature).Distinct(StringComparer.Ordinal).Count() != cards.Length
                || cards.Select(GeneratedCardEffectIdentity.TemplateSignature).Distinct(StringComparer.Ordinal).Count() != cards.Length)
                throw new InvalidOperationException("Duplicate effects/templates in " + character);
            OstyPoolConstraintResolver.Audit(cards);
            SlyPoolConstraintResolver.Audit(cards);
            DerivativePoolConstraintResolver.Audit(cards);
            if (character == GeneratedCharacter.Colorless) continue;
            var starters = cards.Where(card => card.Rarity == GeneratedRarity.Basic).ToArray();
            if (starters.Count(StartingPoolConstraintResolver.CountsAsDamage) < 4
                || starters.Count(StartingPoolConstraintResolver.CountsAsDefense) < 4
                || starters.Count(StartingPoolConstraintResolver.IsHighResourceCard) > 2)
                throw new InvalidOperationException("Invalid starter combat coverage/costs for " + character
                    + $": damage={starters.Count(StartingPoolConstraintResolver.CountsAsDamage)}, "
                    + $"defense={starters.Count(StartingPoolConstraintResolver.CountsAsDefense)}, "
                    + $"highResource={starters.Count(StartingPoolConstraintResolver.IsHighResourceCard)}, "
                    + $"size={starters.Length}");
            OstyPoolConstraintResolver.Audit(starters);
            SlyPoolConstraintResolver.Audit(starters);
            DerivativePoolConstraintResolver.Audit(starters, restrictProducersToCommonAndUncommon: false);
            if ((character == GeneratedCharacter.Regent || cards.Any(card => card.UnifiedChaos))
                && starters.Count(StartingPoolConstraintResolver.ConsumesStars)
                    > starters.Count(StartingPoolConstraintResolver.ProducesStars))
                throw new InvalidOperationException("Unsupported starter Star costs for " + character);
        }
        GD.Print("AA_SMOKE_POOL_PASS seven pools / 606 cards, upgrades, uniqueness, starter coverage and support");
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string PoolFingerprint() => Digest(string.Join("\n", ChaosRunDefinitions.GetAllCards()
        .OrderBy(pair => pair.Key).SelectMany(pair => pair.Value.OrderBy(card => card.Slot)
            .Select(card => pair.Key + ":" + JsonSerializer.Serialize(card)))));

    private static string DeckFingerprint(Player owner) => Digest(string.Join("\n", owner.Deck.Cards.Select(card =>
        card.Id + ":" + card.CurrentUpgradeLevel + ":" + card.EnergyCost.GetWithModifiers(CostModifiers.None)
        + ":" + card.BaseStarCost + ":" + string.Join(",", card.Keywords.Order())
        + ":" + card.GetDescriptionForPile(PileType.Deck)
        + ":" + (card is ChaosCardModel chaos
            ? CardTinkeringApi.SerializeCard(chaos.Generated) + ":extraAim=" + chaos.ExtraAim : "native"))));

    private static async Task Continue()
    {
        var game = NGame.Instance ?? throw new InvalidOperationException("Missing game before Continue");
        var menu = game.MainMenu ?? throw new InvalidOperationException("Missing main menu before Continue");
        await (Task)AccessTools.Method(typeof(NMainMenu), "OnContinueButtonPressedAsync").Invoke(menu, [])!;
        if (RunManager.Instance.DebugOnlyGetState() is null || game.MainMenu is not null)
            throw new InvalidOperationException("Native Continue did not enter the saved run");
    }

    private static void CheckLoaded(string expectedPool, string expectedDeck, string label)
    {
        if (!ChaosRunDefinitions.IsRunActive || PoolFingerprint() != expectedPool)
            throw new InvalidOperationException(label + ": generated pool changed during load");
        var owner = RunManager.Instance.DebugOnlyGetState()!.Players.Single();
        if (DeckFingerprint(owner) != expectedDeck)
            throw new InvalidOperationException(label + ": deck/upgrades/costs/descriptions changed during load");
        foreach (var card in owner.Deck.Cards)
            if (card.GetDescriptionForPile(PileType.Deck).Contains(":diff()"))
                throw new InvalidOperationException(label + ": unbound dynamic variable after load");
        GD.Print("AA_SMOKE_LOAD_PASS " + label + " pool=" + expectedPool[..16] + " deck=" + expectedDeck[..16]);
    }
}
