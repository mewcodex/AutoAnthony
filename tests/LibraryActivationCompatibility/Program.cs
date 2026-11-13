using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoAnthony;
using ChaosCardGenerator;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

// Real installed patch methods; no library/game initializer, native UI, user settings, or saves are run.
var workshop = args[0];
var gameLib = args[1];
var mode = args[2];
var reverse = args.Contains("--aa-first");
var gameVersion = JsonDocument.Parse(File.ReadAllText(Path.Combine(gameLib, "..", "release_info.json")))
    .RootElement.GetProperty("version").GetString()!.TrimStart('v');
var paths = new[] { gameLib, Path.Combine(workshop, "3737335127", "BaseLib"),
    Path.Combine(workshop, "3747602295", "shared"),
    Path.Combine(workshop, "3747602295", "compat", gameVersion) };
AssemblyLoadContext.Default.Resolving += (_, name) => paths.Select(path => Path.Combine(path, name.Name + ".dll"))
    .Where(File.Exists).Select(AssemblyLoadContext.Default.LoadFromAssemblyPath).FirstOrDefault();
var aa = typeof(ChaosCardModel).Assembly;
var native = typeof(CardModel).Assembly;
var h = new Harmony("autoanthony.tests.library-activation");
h.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Logging.Logger), "GetIsRunningFromGodotEditor"),
    prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.False)));
h.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Logging.ConsoleLogPrinter), "Print"),
    prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.Skip)));
var settings = aa.GetType("AutoAnthony.ChaosModSettings", true)!;
h.Patch(settings.TypeInitializer!, prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.Skip)));
AccessTools.Field(settings, "_loaded").SetValue(null, true);
foreach (var name in new[] { "_enabled", "_addGeneratedCards", "_replaceStartingCards", "_numericBalanceOptimization" })
    AccessTools.Field(settings, name).SetValue(null, true);
var models = (IDictionary)AccessTools.Field(typeof(ModelDb), "_contentById").GetValue(null)!;
AbstractModel Canonical(Type type)
{
    var id = ModelDb.GetId(type);
    if (models[id] is AbstractModel present) return present;
    var value = type.Assembly == native && type.Namespace == "MegaCrit.Sts2.Core.Models.Cards"
        && type.GetConstructor(Type.EmptyTypes) is not null
        ? (AbstractModel)Activator.CreateInstance(type)!
        : (AbstractModel)RuntimeHelpers.GetUninitializedObject(type);
    AccessTools.Field(typeof(AbstractModel), "<Id>k__BackingField").SetValue(value, id);
    if (value is CardModel)
    {
        AccessTools.Field(typeof(CardModel), "_canonicalInstance").SetValue(value, value);
        if (type.Assembly != native)
        {
            AccessTools.Field(typeof(CardModel), "_keywordsAddedFromOutside").SetValue(value,
                new HashSet<MegaCrit.Sts2.Core.Entities.Cards.CardKeyword>());
            AccessTools.Field(typeof(CardModel), "_keywordsRemovedFromOutside").SetValue(value,
                new HashSet<MegaCrit.Sts2.Core.Entities.Cards.CardKeyword>());
            AccessTools.Field(typeof(CardModel), "_temporaryStarCosts").SetValue(value,
                new List<MegaCrit.Sts2.Core.Entities.Cards.TemporaryCardCost>());
        }
    }
    models[id] = value;
    return value;
}
foreach (var type in native.GetTypes().Concat(aa.GetTypes()).Where(type => !type.IsAbstract && !type.ContainsGenericParameters
    && (type.IsSubclassOf(typeof(CardModel)) || type.IsSubclassOf(typeof(CardPoolModel)) || type.IsSubclassOf(typeof(CharacterModel)))))
    Canonical(type);
var defs = (IDictionary)AccessTools.Field(typeof(ChaosRunDefinitions), "Definitions").GetValue(null)!;
foreach (var character in Enum.GetValues<GeneratedCharacter>())
{
    var count = ChaosRunDefinitions.CountFor(character);
    var basic = ChaosRunDefinitions.BasicCountFor(character);
    var cards = Enumerable.Range(0, count).Select(slot => new ChaosCardDefinition(slot,
        new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other,
            slot < basic ? GeneratedRarity.Basic : slot >= count - 2 && character != GeneratedCharacter.Colorless
                ? GeneratedRarity.Ancient : GeneratedRarity.Common,
            "", [], [], Character: character), "", "", "", "", "")).ToArray();
    defs[character] = cards;
    foreach (var (field, value) in new[] { ("DefinitionUltimateModes", false), ("DefinitionBalancedValueModes", true),
                 ("DefinitionNumericRandomModes", false), ("DefinitionReplaceStartingCardsModes", true),
                 ("DefinitionPreserveOriginalCardsModes", false) })
        ((IDictionary)AccessTools.Field(typeof(ChaosRunDefinitions), field).GetValue(null)!)[character] = value;
}
void Check(bool result, string message)
{
    if (!result) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}
void PatchClass(string name) => new PatchClassProcessor(h, aa.GetType("AutoAnthony.Patches." + name, true)!).Patch();
void AaPatches()
{
    if (args.Contains("--patch-all"))
    {
        var failures = new List<string>();
        foreach (var type in aa.GetTypes().Where(type => type.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0))
        {
            try { new PatchClassProcessor(h, type).Patch(); }
            catch (Exception exception)
            {
                failures.Add(type.FullName!);
                Console.WriteLine("PATCH_FAILED " + type.FullName + ": " + exception);
            }
        }
        Check(failures.Count == 0, "all runtime Harmony patch classes install against game " + gameVersion);
        return;
    }
    foreach (var name in new[] { "SeedBeforeSingleplayerPatch", "SeedBeforeMultiplayerPatch", "SeedBeforeLoadPatch",
                 "SaveGeneratedPoolPatch", "CanonicalizedMultiplayerSnapshotPatch", "ColorlessPoolContentsPatch" })
        PatchClass(name);
    foreach (var character in new[] { "Ironclad", "Silent", "Defect", "Necrobinder", "Regent" })
    {
        PatchClass(character + "PoolPatch");
        PatchClass(character + "StartingDeckPatch");
    }
}
Assembly? baselib = null, ritsu = null;
void Direct(Assembly assembly, string typeName, MethodBase target, string? prefix = null, string? postfix = null, string? finalizer = null)
{
    var type = assembly.GetType(typeName, true)!;
    HarmonyMethod? Method(string? name) => name is null ? null : new(AccessTools.Method(type, name));
    h.Patch(target, Method(prefix), Method(postfix), finalizer: Method(finalizer));
}
void Libraries()
{
    if (mode is "base" or "both")
    {
        baselib = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(paths[1], "BaseLib.dll"));
        new PatchClassProcessor(h, baselib.GetType("BaseLib.Patches.Content.ModelDbSharedCardPoolsPatch", true)!).Patch();
        new PatchClassProcessor(h, baselib.GetType("BaseLib.Patches.Saves.ExtendedSavePatches+CanonicalizeExtendedRunData", true)!).Patch();
    }
    if (mode is "ritsu" or "both")
    {
        ritsu = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(paths[3], "STS2-RitsuLib.Runtime.dll"));
        Direct(ritsu, "STS2RitsuLib.Content.Patches.StartupPoolCachePatch",
            AccessTools.PropertyGetter(typeof(CardPoolModel), nameof(CardPoolModel.AllCards)), "Prefix", finalizer: "Finalizer");
        foreach (var character in new[] { typeof(Ironclad), typeof(Silent), typeof(Defect), typeof(Necrobinder), typeof(Regent) })
            Direct(ritsu, "STS2RitsuLib.Content.DynamicCharacterStarterContentPatcher",
                AccessTools.PropertyGetter(character, nameof(CharacterModel.StartingDeck)), postfix: "StartingDeckPostfix");
        Direct(ritsu, "STS2RitsuLib.RunData.Patches.RunSavedDataPrepareSingleplayerNewRunPayloadPatch",
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.NGame), "StartNewSingleplayerRun"), "Prefix");
        Direct(ritsu, "STS2RitsuLib.RunData.Patches.RunSavedDataCanonicalizeSavePatch",
            AccessTools.Method(typeof(RunManager), "CanonicalizeSave"), postfix: "Postfix");
    }
}
if (reverse) { AaPatches(); Libraries(); } else { Libraries(); AaPatches(); }
if (args.Contains("--patch-all")) return;
AccessTools.Method(typeof(ChaosRunDefinitions), "CaptureOriginalColorlessCards").Invoke(null, null);
if (args.Contains("--generate"))
{
    AccessTools.Method(typeof(ChaosRunDefinitions), "CaptureOriginalColorlessCards").Invoke(null, null);
    AccessTools.Field(typeof(ChaosRunDefinitions), "_activeReplaceStartingCards").SetValue(null, true);
    AccessTools.Field(typeof(ChaosRunDefinitions), "_activeNumericBalanceOptimization").SetValue(null, true);
    if (ritsu is not null)
        AccessTools.Property(ritsu.GetType("STS2RitsuLib.Content.ModContentRegistry", true)!, "IsFrozen").SetValue(null, true);
    var actual = new List<ChaosCardDefinition>();
    foreach (var character in Enum.GetValues<GeneratedCharacter>())
    {
        var generated = (IReadOnlyList<ChaosCardDefinition>)AccessTools.Method(typeof(ChaosRunDefinitions), "GenerateDefinitions")
            .Invoke(null, [character, "LIBRARY_COMPATIBILITY_20261030", false, true, false, true, null])!;
        Check(generated.Count == ChaosRunDefinitions.CountFor(character), $"{character} actually generates a complete balanced pool");
        actual.AddRange(generated);
        defs[character] = generated;
    }
    Console.WriteLine("GENERATION_DIGEST " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(actual)))));
    // v0.113 adds one Colorless Rare at the end. Reproduce the previous complete snapshot shape and verify
    // selective recovery preserves every old card rather than silently replacing its saved gameplay definition.
    var currentPools = Enum.GetValues<GeneratedCharacter>().ToDictionary(character => character,
        character => (IReadOnlyList<ChaosCardDefinition>)defs[character]!);
    var previousPools = currentPools.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ChaosCardDefinition>)
        (pair.Key == GeneratedCharacter.Colorless ? pair.Value.Take(52).ToArray() : pair.Value.ToArray()));
    GeneratorOperation Legacy(string template, OperationScope scope, string chinese, string english)
    {
        var operation = new GeneratorOperation(template, scope, chinese, new Dictionary<string, int>());
        var spec = OperationRuntimeSpecCompiler.CompileLegacy(operation);
        if (!OperationLocalizedText.TryCompile(chinese, english, spec, out var localized))
            throw new InvalidOperationException("Could not prepare a legacy snapshot fixture: " + template);
        return operation with { RuntimeSpec = spec, LocalizedText = localized };
    }
    var legacySilent = previousPools[GeneratedCharacter.Silent].ToArray();
    var commonSlots = legacySilent.Where(card => card.Card.Rarity == GeneratedRarity.Common).Take(2).ToArray();
    var legacyOperations = new[]
    {
        new[] { Legacy("I:DrawWithRetain", OperationScope.Independent,
            "抽2张牌。这些牌在本回合获得保留。", "Draw 2 cards. Retain them this turn.") },
        new[] { Legacy("T:D", OperationScope.SingleEnemyOnly, "造成9点伤害。", "Deal 9 damage."),
            Legacy("M:DamagePerDiscardThisTurn", OperationScope.Modifier,
                "本回合每丢弃过一张牌，这张牌额外造成4点伤害。",
                "Deal 4 additional damage for each card discarded this turn.") }
    };
    for (var index = 0; index < commonSlots.Length; index++)
    {
        var legacySource = commonSlots[index];
        var operations = legacyOperations[index];
        legacySilent[legacySource.Slot] = legacySource with
        {
            Card = legacySource.Card with
            {
                Cost = 1, StarCost = -1, HasStarCostX = false, Upgrade = null, Tags = [],
                Type = index == 0 ? GeneratedCardType.Skill : GeneratedCardType.Attack,
                Target = index == 0 ? TargetMode.Other : TargetMode.SingleEnemy,
                Operations = operations,
                ChineseDescription = CardDescriptionRenderer.Render(operations),
                EnglishDescription = EnglishCardDescriptionRenderer.Render(operations)
            },
            RuntimeSpecs = operations.Select(OperationRuntimeSpecCompiler.GetOrCompile).ToArray(),
            UpgradeValueSlots = []
        };
    }
    previousPools[GeneratedCharacter.Silent] = legacySilent;
    Canonical(typeof(ChaosPoolSnapshotModifier));
    var saved = ChaosPoolSnapshot.ToSerializableModifier(GeneratedCharacter.Ironclad,
        "LIBRARY_COMPATIBILITY_20261030", previousPools);
    var payload = saved.Props!.strings!.Single(property => property.name == "PoolSnapshot").value;
    var recovered = ChaosPoolSnapshot.RestoreAll(payload, GeneratedCharacter.Ironclad,
        "LIBRARY_COMPATIBILITY_20261030", currentPools, out var recovery, logFailures: false);
    Check(recovery.RestoredCards == actual.Count - 1 && recovery.RegeneratedCards == 1,
        "previous 52-card Colorless snapshot recovers exactly the one new Rare slot");
    Check(previousPools.All(pair => pair.Value.All(card =>
            GeneratedCardEffectIdentity.Signature(card.Card)
                == GeneratedCardEffectIdentity.Signature(recovered[pair.Key][card.Slot].Card)
            && card.Card.Cost == recovered[pair.Key][card.Slot].Card.Cost
            && card.Card.Name?.Chinese == recovered[pair.Key][card.Slot].Card.Name?.Chinese
            && card.Card.Name?.English == recovered[pair.Key][card.Slot].Card.Name?.English)),
        "added Colorless slot preserves prior saved effects, costs and names, including both retired source components");
}
var colorless = ModelDb.CardPool<ColorlessCardPool>();
var nativeColorlessCount = ((CardModel[])AccessTools.Method(typeof(ColorlessCardPool), "GenerateAllCards")
    .Invoke(colorless, null)!).Length;
foreach (var frozen in new[] { false, true })
{
    if (ritsu is not null)
        AccessTools.Property(ritsu.GetType("STS2RitsuLib.Content.ModContentRegistry", true)!, "IsFrozen").SetValue(null, frozen);
    for (var i = 0; i < 3; i++) Check(colorless.AllCards.Count() == nativeColorlessCount,
        $"Colorless remains {nativeColorlessCount}, registry frozen={frozen}, query={i}");
}
AccessTools.Field(typeof(ChaosRunDefinitions), "_activeReplaceStartingCards").SetValue(null, true);
AccessTools.Field(typeof(ChaosRunDefinitions), "_activeNumericBalanceOptimization").SetValue(null, true);
AccessTools.Method(typeof(ChaosRunDefinitions), "SetActiveCharacters").Invoke(null,
    [Enum.GetValues<GeneratedCharacter>().Where(c => c != GeneratedCharacter.Colorless)]);
foreach (var character in new[] { GeneratedCharacter.Ironclad, GeneratedCharacter.Silent, GeneratedCharacter.Defect,
             GeneratedCharacter.Necrobinder, GeneratedCharacter.Regent })
{
    var model = (CharacterModel)Canonical(native.GetType("MegaCrit.Sts2.Core.Models.Characters." + character, true)!);
    Check(model.CardPool.GetType().Assembly == aa, $"{character} selects generated character pool");
    Check(model.StartingDeck.Count() == ChaosRunDefinitions.BasicCountFor(character)
        && model.StartingDeck.All(c => c is ChaosCardModel), $"{character} keeps complete generated starting deck");
    Check(model.CardPool.AllCards.All(c => c is ChaosCardModel)
        && model.CardPool.AllCards.Count() == ChaosRunDefinitions.CountFor(character), $"{character} keeps generated reward pool");
}
// v113 creates Players before NGame prepares pools. Reproduce that ordering without native UI/save managers.
var refreshStarter = AccessTools.Method(aa.GetType("AutoAnthony.Patches.PreparedStartingDeck", true)!, "Refresh");
foreach (var character in Enum.GetValues<GeneratedCharacter>().Where(c => c != GeneratedCharacter.Colorless))
{
    var model = (CharacterModel)Canonical(native.GetType("MegaCrit.Sts2.Core.Models.Characters." + character, true)!);
    var preparedPlayer = (MegaCrit.Sts2.Core.Entities.Players.Player)RuntimeHelpers.GetUninitializedObject(
        typeof(MegaCrit.Sts2.Core.Entities.Players.Player));
    AccessTools.Field(preparedPlayer.GetType(), "<Character>k__BackingField").SetValue(preparedPlayer, model);
    var deck = new MegaCrit.Sts2.Core.Entities.Cards.CardPile(MegaCrit.Sts2.Core.Entities.Cards.PileType.Deck);
    AccessTools.Field(preparedPlayer.GetType(), "<Deck>k__BackingField").SetValue(preparedPlayer, deck);
    AccessTools.Field(typeof(ChaosRunDefinitions), "_activeReplaceStartingCards").SetValue(null, false);
    foreach (var starter in model.StartingDeck) deck.AddInternal(starter.ToMutable(), silent: true);
    var originalDeck = deck.Cards.ToArray();
    refreshStarter.Invoke(null, [preparedPlayer]);
    Check(deck.Cards.SequenceEqual(originalDeck), $"{character}: disabled replacement preserves prepared native starters");
    var extra = ((CardModel)Canonical(typeof(MegaCrit.Sts2.Core.Models.Cards.Burn))).ToMutable();
    deck.AddInternal(extra, silent: true);
    AccessTools.Field(typeof(ChaosRunDefinitions), "_activeReplaceStartingCards").SetValue(null, true);
    refreshStarter.Invoke(null, [preparedPlayer]);
    Check(deck.Cards.Contains(extra) && deck.Cards.OfType<ChaosCardModel>().Count()
          == ChaosRunDefinitions.BasicCountFor(character),
        $"{character}: prepared inventory receives the complete generated starter deck and retains extra cards");
    var rebound = deck.Cards.ToArray();
    refreshStarter.Invoke(null, [preparedPlayer]);
    Check(deck.Cards.SequenceEqual(rebound), $"{character}: prepared inventory refresh is idempotent");
}
var unsupportedType = native.GetType("MegaCrit.Sts2.Core.Models.Characters.Fool");
if (unsupportedType is not null)
{
    var unsupported = (CharacterModel)Canonical(unsupportedType);
    Check(unsupported.CardPool.GetType().Assembly == native
          && unsupported.StartingDeck.All(card => card is not ChaosCardModel),
        "the new unsupported native character retains its original pool and starting deck");
}
// Check actual save extension callbacks do not remove the native modifier collection.
var original = new SerializableRun();
var rewritten = new SerializableRun();
var marker = new MegaCrit.Sts2.Core.Saves.Runs.SerializableModifier();
original.Modifiers.Add(marker);
rewritten.Modifiers.Add(marker);
if (baselib is not null)
    AccessTools.Method(baselib.GetType("BaseLib.Patches.Saves.ExtendedSavePatches+CanonicalizeExtendedRunData", true),
        "CanonicalizeExtendedData").Invoke(null, [original, rewritten]);
if (ritsu is not null)
    AccessTools.Method(ritsu.GetType("STS2RitsuLib.RunData.Patches.RunSavedDataCanonicalizeSavePatch", true),
        "Postfix").Invoke(null, [original, rewritten]);
Check(original.Modifiers.Count == 1 && rewritten.Modifiers.Count == 1
    && ReferenceEquals(rewritten.Modifiers[0], marker), "library canonicalize callbacks retain native modifier carrier");
if (ritsu is not null)
{
    var preparedPlayer = (MegaCrit.Sts2.Core.Entities.Players.Player)RuntimeHelpers.GetUninitializedObject(
        typeof(MegaCrit.Sts2.Core.Entities.Players.Player));
    AccessTools.Field(preparedPlayer.GetType(), "<Character>k__BackingField").SetValue(preparedPlayer, Canonical(typeof(Ironclad)));
    AccessTools.Method(ritsu.GetType("STS2RitsuLib.RunData.Patches.RunSavedDataPrepareSingleplayerNewRunPayloadPatch", true), "Prefix")
        .Invoke(null, [preparedPlayer, "LIBRARY_COMPATIBILITY", Array.Empty<ModifierModel>()]);
    Check(ChaosRunDefinitions.IsRunActive, "Ritsu singleplayer preparation does not clear generated-pool activation");
    var documentType = ritsu.GetType("STS2RitsuLib.RunData.RunSavedDataDocument", true)!;
    var document = AccessTools.Method(documentType, "FromJson").Invoke(null,
        ["{\"_ritsulib\":{\"version\":1,\"run_saved_data\":{\"probe\":{\"slot\":{\"test\":true}}}}}"]);
    var json = Encoding.UTF8.GetBytes("{\"modifiers\":[{\"id\":\"MODIFIER.CHAOS_POOL_SNAPSHOT_MODIFIER\",\"props\":{\"strings\":[{\"name\":\"PoolSnapshot\",\"value\":\"AA1:test-payload\"}]}}]}");
    var updated = (byte[])AccessTools.Method(documentType, "InjectIntoUtf8Json").Invoke(null, [json, document])!;
    using var parsed = JsonDocument.Parse(updated);
    Check(parsed.RootElement.GetProperty("modifiers")[0].GetProperty("props").GetProperty("strings")[0]
        .GetProperty("value").GetString() == "AA1:test-payload", "Ritsu JSON extension preserves the complete native snapshot carrier");
}
Console.WriteLine($"COMPLETE mode={mode}, aaFirst={reverse}; actual installed library patches, isolated model database; no full engine/lobby test");

public static class Hooks
{
    public static bool Skip() => false;
    public static bool False(ref bool __result) { __result = false; return false; }
}
