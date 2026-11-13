using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using AutoAnthony;
using ChaosCardGenerator;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}

var assembly = typeof(ChaosCardModel).Assembly;
var binding = assembly.GetType("AutoAnthony.Patches.ChaosAncientRelics", true)!;
object? Call(string name, params object?[] args) => AccessTools.Method(binding, name).Invoke(null, args);
void Set(object value, Type owner, string field, object? contents) => AccessTools.Field(owner, field).SetValue(value, contents);
var models = (Dictionary<ModelId, AbstractModel>)AccessTools.Field(typeof(ModelDb), "_contentById").GetValue(null)!;
var definitions = (IDictionary)AccessTools.Field(typeof(ChaosRunDefinitions), "Definitions").GetValue(null)!;
AccessTools.Field(typeof(ChaosRunDefinitions), "_originalColorlessCards").SetValue(null, Array.Empty<CardModel>());
AccessTools.Field(typeof(ModelIdSerializationCache), "_initialized").SetValue(null, true);
void CacheProperties(Type type) => ModelIdSerializationCache.CacheSavedPropertiesForTypeDebug(type);
AccessTools.Property(typeof(ModelIdSerializationCache), nameof(ModelIdSerializationCache.PropertyIdBitSize)).SetValue(null, 16);
void RegisterWireId(ModelId id)
{
    foreach (var (mapName, listName, value, bitsName) in new[] {
        ("_categoryNameToNetIdMap", "_netIdToCategoryNameMap", id.Category, nameof(ModelIdSerializationCache.CategoryIdBitSize)),
        ("_entryNameToNetIdMap", "_netIdToEntryNameMap", id.Entry, nameof(ModelIdSerializationCache.EntryIdBitSize)) })
    {
        var map = (Dictionary<string, int>)AccessTools.Field(typeof(ModelIdSerializationCache), mapName).GetValue(null)!;
        var names = (List<string>)AccessTools.Field(typeof(ModelIdSerializationCache), listName).GetValue(null)!;
        if (!map.ContainsKey(value)) { map[value] = names.Count; names.Add(value); }
        AccessTools.Property(typeof(ModelIdSerializationCache), bitsName).SetValue(null, 16);
    }
}
var harmony = new Harmony("autoanthony.tests.ancient-relic-binding");
// Keep engine assets/localization outside this isolated test; all binding/clone/save/upgrade code is real.
harmony.Patch(typeof(RelicModel).TypeInitializer!,
    prefix: new HarmonyMethod(typeof(TestHooks), nameof(TestHooks.SkipEngineNames)));
harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Logging.Logger), "GetIsRunningFromGodotEditor"),
    prefix: new HarmonyMethod(typeof(TestHooks), nameof(TestHooks.NotEditor)));
harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Logging.ConsoleLogPrinter), "Print"),
    prefix: new HarmonyMethod(typeof(TestHooks), nameof(TestHooks.SkipEngineNames)));
harmony.Patch(AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Title)),
    prefix: new HarmonyMethod(typeof(TestHooks), nameof(TestHooks.Title)));
harmony.Patch(AccessTools.PropertyGetter(typeof(ChaosCardModel), nameof(ChaosCardModel.PortraitPath)),
    prefix: new HarmonyMethod(typeof(TestHooks), nameof(TestHooks.Portrait)));
foreach (var patch in new[] { "ArchaicToothSentinelHoverTipPatch", "ArchaicToothBoundCardClonePatch", "DustyTomeAncientCardBindingPatch",
             "DustyTomeBoundCardSavePatch", "DustyTomeBoundCardLoadPatch" })
    new PatchClassProcessor(harmony, assembly.GetType("AutoAnthony.Patches." + patch, true)!).Patch();

T Relic<T>() where T : RelicModel
{
    var relic = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    CacheProperties(typeof(T));
    Set(relic, typeof(AbstractModel), "<Id>k__BackingField", ModelDb.GetId(typeof(T)));
    Set(relic, typeof(AbstractModel), "<IsMutable>k__BackingField", true);
    Set(relic, typeof(RelicModel), "_canonicalInstance", relic);
    Set(relic, typeof(RelicModel), "_dynamicVars", new DynamicVarSet([
        new StringVar("AncientCard"), new StringVar("StarterCard")]));
    AccessTools.Field(typeof(T), "_extraHoverTips").SetValue(relic,
        typeof(T) == typeof(ArchaicTooth) ? new List<IHoverTip>() : Array.Empty<IHoverTip>());
    return relic;
}

foreach (var character in new[] { GeneratedCharacter.Ironclad, GeneratedCharacter.Silent,
             GeneratedCharacter.Defect, GeneratedCharacter.Necrobinder, GeneratedCharacter.Regent, GeneratedCharacter.Fool })
{
    var count = ChaosRunDefinitions.CountFor(character);
    var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
    var playerCharacter = (CharacterModel)RuntimeHelpers.GetUninitializedObject(typeof(CardModel).Assembly
        .GetType("MegaCrit.Sts2.Core.Models.Characters." + character, true)!);
    Set(player, typeof(Player), "<Character>k__BackingField", playerCharacter);
    Set(player, typeof(Player), "_runPiles", Array.Empty<CardPile>());
    var run = (RunState)RuntimeHelpers.GetUninitializedObject(typeof(RunState));
    Set(run, typeof(RunState), "_allCards", new List<CardModel>());
    Set(player, typeof(Player), "_runState", run);
    var initial = new GeneratedCard(1, GeneratedCardType.Skill, TargetMode.Other, GeneratedRarity.Ancient,
        "", [], [], Character: character,
        Name: new GeneratedCardName("选项甲", "Shown Ancient", []),
        Upgrade: new CardUpgradePlan(0, [], "", [], ""));
    var pool = Enumerable.Range(0, count).Select(slot => new ChaosCardDefinition(slot, initial,
        "res://ancient-test.png", "", "", "", "")).ToArray();
    definitions[character] = pool;
    ((IDictionary)AccessTools.Field(typeof(ChaosRunDefinitions), "DefinitionBalancedValueModes").GetValue(null)!)[character] = true;
    for (var index = 0; index < 2; index++)
    {
        var prefix = character == GeneratedCharacter.Ironclad ? "ChaosCard" : $"Chaos{character}Card";
        var type = assembly.GetType($"AutoAnthony.{prefix}{count - 2 + index:000}", true)!;
        CacheProperties(type);
        var canonical = (ChaosCardModel)RuntimeHelpers.GetUninitializedObject(type);
        Set(canonical, typeof(AbstractModel), "<Id>k__BackingField", ModelDb.GetId(type));
        Set(canonical, typeof(CardModel), "_canonicalInstance", canonical);
        Set(canonical, typeof(CardModel), "_dynamicVars", new DynamicVarSet([]));
        Set(canonical, typeof(CardModel), "_keywords", new HashSet<CardKeyword>());
        Set(canonical, typeof(CardModel), "_keywordsAddedFromOutside", new HashSet<CardKeyword>());
        Set(canonical, typeof(CardModel), "_keywordsRemovedFromOutside", new HashSet<CardKeyword>());
        Set(canonical, typeof(CardModel), "_temporaryStarCosts", new List<TemporaryCardCost>());
        models[canonical.Id] = canonical;
        RegisterWireId(canonical.Id);
        var tome = Relic<DustyTome>();
        var tooth = Relic<ArchaicTooth>();
        RelicModel option = index == 0 ? tooth : tome;
        var bind = index == 0 ? "BindArchaicToothCard" : "BindDustyTomeCard";
        Call(bind, option, canonical);
        var tipsField = AccessTools.Field(option.GetType(), "_extraHoverTips");
        CardModel Preview(RelicModel relic) => ((IEnumerable<IHoverTip>)tipsField.GetValue(relic)!)
            .OfType<CardHoverTip>().Single().Card;
        var preview = (ChaosCardModel)Preview(option);
        Check(((StringVar)option.DynamicVars["AncientCard"]).StringValue == preview.Title,
            $"{character}/{index}: name and preview agree, including upgrade suffix");
        Check(preview.IsUpgraded == (index == 1), $"{character}/{index}: Tome upgraded, Tooth unupgraded");

        var normalArguments = new object?[] { option, player, index, null };
        Check((bool)AccessTools.Method(binding, "TryCreateBoundAncientCard").Invoke(null, normalArguments)!
            && normalArguments[3] is ChaosCardModel { HasFreeformDefinition: false },
            $"{character}/{index}: unchanged reward stays linked to shared pool");

        pool[count - 2 + index] = pool[count - 2 + index] with
        { Card = initial with { Name = new GeneratedCardName("卡池乙", "Rebound Pool", []), Cost = 3 } };
        var arguments = new object?[] { option, null };
        Check((bool)AccessTools.Method(binding, "TryGetBoundAncientCard").Invoke(null, arguments)!,
            $"{character}/{index}: binding exists after pool changes");
        var captured = (ChaosCardModel)Call("RestoreAncientCard", arguments[1])!;
        Check(captured.Generated.Name!.English == "Shown Ancient" && captured.Generated.Cost == 1,
            $"{character}/{index}: reward keeps shown identity and effects, not rebound slot");
        if (index == 1) captured.UpgradeInternal();
        Check(captured.EnergyCost.Canonical == preview.EnergyCost.Canonical,
            $"{character}/{index}: preview/reward use the same upgraded cost");
        var awardArguments = new object?[] { option, player, index, null };
        Check((bool)AccessTools.Method(binding, "TryCreateBoundAncientCard").Invoke(null, awardArguments)!
            && awardArguments[3] is ChaosCardModel { HasFreeformDefinition: true } award
            && award.Owner == player && run.ContainsCard(award) && award.Generated.Name!.English == "Shown Ancient",
            $"{character}/{index}: real receipt helper registers the captured card to its actual owner");
        if (index == 1)
        {
            var wrongArguments = new object?[] { option, player, 0, null };
            Check(!(bool)AccessTools.Method(binding, "TryCreateBoundAncientCard").Invoke(null, wrongArguments)!,
                $"{character}: Tome cannot accidentally award Tooth's Ancient slot");
        }

        var clone = (RelicModel)option.ClonePreservingMutability();
        Check(Preview(clone).Title == preview.Title, $"{character}/{index}: cloned relic keeps its visible preview");
        var cloneArguments = new object?[] { clone, null };
        Check((bool)AccessTools.Method(binding, "TryGetBoundAncientCard").Invoke(null, cloneArguments)!,
            $"{character}/{index}: relic clone recovers captured binding");
        var restored = (ChaosCardModel)Call("RestoreAncientCard", cloneArguments[1])!;
        Check(restored.Generated.Name!.English == "Shown Ancient", $"{character}/{index}: clone reward stays frozen");

        var save = option.ToSerializable();
        if (index == 1)
        {
            Check(save.Props!.strings!.Count(p => p.name == nameof(ChaosCardModel.FreeformDefinitionPayload)) == 1,
                $"{character}: Tome snapshot is serialized once");
            var loaded = Relic<DustyTome>();
            loaded.AncientCard = tome.AncientCard;
            Call("RestoreBoundDustyTomeCard", save, loaded);
            Check(Preview(loaded).Title == preview.Title, $"{character}: Tome load restores shown upgraded name");
            var writer = new PacketWriter { WarnOnGrow = false };
            save.Props.Serialize(writer);
            var reader = new PacketReader();
            reader.Reset(writer.Buffer.Take(writer.BytePosition).ToArray());
            var networkProps = new SavedProperties();
            networkProps.Deserialize(reader);
            var remote = Relic<DustyTome>();
            networkProps.Fill(remote);
            Call("RestoreBoundDustyTomeCard", new SerializableRelic { Id = save.Id, Props = networkProps }, remote);
            Check(Preview(remote).Title == preview.Title,
                $"{character}: native packet round trip preserves the captured reward");
            var legacy = Relic<DustyTome>();
            legacy.AncientCard = tome.AncientCard;
            Call("RestoreBoundDustyTomeCard", new SerializableRelic { Id = save.Id }, legacy);
            Check(Preview(legacy).Title == "Rebound Pool+", $"{character}: old ModelId-only save remains loadable");
            var invalid = new SerializableRelic { Id = save.Id, Props = new SavedProperties { strings =
                [new(nameof(ChaosCardModel.FreeformDefinitionPayload), "invalid legacy capture")] } };
            Call("RestoreBoundDustyTomeCard", invalid, legacy);
            Check(Preview(legacy).Title == "Rebound Pool+", $"{character}: invalid old capture cannot block run loading");
        }
        else
        {
            var loaded = Relic<ArchaicTooth>();
            save.Props!.Fill(loaded);
            Check(Preview(loaded).Title == preview.Title, $"{character}: Tooth native save restores shown name");
        }
    }
}
Console.WriteLine("Ancient relic binding regressions passed without launching the game or changing saves.");

static class TestHooks
{
    public static bool SkipEngineNames() => false;
    public static bool NotEditor(ref bool __result) { __result = false; return false; }
    public static bool Title(CardModel __instance, ref string __result)
    {
        if (__instance is not ChaosCardModel card) return true;
        __result = card.Generated.Name!.English + (card.IsUpgraded ? "+" : "");
        return false;
    }
    public static bool Portrait(ref string __result) { __result = "res://ancient-test.png"; return false; }
}
