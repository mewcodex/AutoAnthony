using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AutoAnthony;
using ChaosCardGenerator;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using GameTag = MegaCrit.Sts2.Core.Entities.Cards.CardTag;

static void Check(bool result, string message)
{
    if (!result) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
}
static ChaosCardModel Model(GeneratedCard definition)
{
    var card = (ChaosCardModel)RuntimeHelpers.GetUninitializedObject(typeof(ChaosDefectCard000));
    AccessTools.Field(typeof(AbstractModel), "<IsMutable>k__BackingField").SetValue(card, true);
    AccessTools.Field(typeof(AbstractModel), "<Id>k__BackingField").SetValue(card, ModelDb.GetId<ChaosDefectCard000>());
    AccessTools.Field(typeof(ChaosCardModel), "_freeformDefinition").SetValue(card, definition);
    return card;
}
GeneratorOperation Op(string template)
{
    var atom = Enum.GetValues<GeneratedCharacter>().SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms)
        .First(atom => atom.Template == template);
    return new(template, atom.Scope, atom.ChineseText, new Dictionary<string, int>(),
        RequiresSingleTarget: atom.RequiresSingleTarget, RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(atom),
        LocalizedText: atom.LocalizedText);
}
var baseline = args.Contains("--baseline");
var extensions = args.Contains("--extensions");
// The native deduplicator's static Summon locale IDs require a running localization manager. None of this
// probe's tips are Summon tips; skip that engine-only initialization and keep its real deduplication algorithm.
new Harmony("autoanthony.tests.generated-hotpaths").Patch(typeof(IHoverTip).TypeInitializer!,
    prefix: new HarmonyMethod(typeof(ProbeHooks), nameof(ProbeHooks.Skip)));
if (extensions)
{
    ComponentPresentationApi.Register("perf_hover", "", new LiveTips());
    ComponentKeywordApi.Register(new("perf_keyword"));
    ComponentKeywordRuntimeApi.RegisterPackage("perf_keywords", [new("perf_keyword", new LiveKeyword())]);
}
var definition = new GeneratedCard(1, GeneratedCardType.Attack, TargetMode.SingleEnemy, GeneratedRarity.Common,
    "", [CardTag.Strike], [Op("T:D"), Op("N:B"), Op("NCR:OstyDamage")]);
var card = Model(definition);
var staleShootDefinition = definition with { Tags = [CardTag.Shoot, CardTag.Strike], Operations = [Op("T:D")] };
var restoredStaleShoot = CardTinkeringApi.DeserializeCard(CardTinkeringApi.SerializeCard(staleShootDefinition));
Check(restoredStaleShoot.Tags.Contains(CardTag.Shoot), "fixture preserves an old snapshot's stray Shoot tag");
var staleShootModel = Model(restoredStaleShoot);
Check(!staleShootModel.Tags.Contains(GameTag.Shoot) && staleShootModel.Tags.Contains(GameTag.Strike),
    "runtime filters stray Shoot without changing saved effects or other tags");
var actualShootModel = Model(definition with { Tags = [], Operations = [Op("F:ShootArrow"), Op("T:D")] });
Check(actualShootModel.Tags.Contains(GameTag.Shoot), "runtime derives missing Shoot from the real operation");
var shootMatches = new[] { staleShootModel, actualShootModel }.Where(candidate => candidate.Tags.Contains(GameTag.Shoot)).ToArray();
Check(shootMatches.Length == 1 && ReferenceEquals(shootMatches[0], actualShootModel),
    "Shoot retrieval predicate excludes the legacy false positive");
var metadataType = typeof(ChaosCardModel).Assembly.GetType("AutoAnthony.ChaosCardPresentationMetadata", true)!;
var staleMetadata = AccessTools.Method(metadataType, "For").Invoke(null, [restoredStaleShoot])!;
var externalTags = (IEnumerable<GameTag>)AccessTools.Property(metadataType, "ExternalTags").GetValue(staleMetadata)!;
Check(!externalTags.Contains(GameTag.Shoot), "external profile projection filters the same stale Shoot tag");
var samples = new List<(string Name, double BytesPerCall, double MicrosecondsPerCall)>();
long consumed = 0;
void Measure(string name, Action action, int count = 100_000)
{
    for (var i = 0; i < 2_000; i++) action();
    GC.Collect();
    var allocated = GC.GetAllocatedBytesForCurrentThread();
    var timer = Stopwatch.StartNew();
    for (var i = 0; i < count; i++) action();
    timer.Stop();
    var bytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)count;
    var micros = timer.Elapsed.TotalMicroseconds / count;
    samples.Add((name, bytes, micros));
    Console.WriteLine($"MEASURE {name}: {bytes:F1} B/call; {micros:F3} us/call");
}
Measure("tags", () => { foreach (var tag in card.Tags) consumed += (int)tag; });
Measure("gains_block", () => { if (card.GainsBlock) consumed++; });
Check(card.Tags.Contains(GameTag.Strike) && card.Tags.Contains(GameTag.OstyAttack) && card.GainsBlock,
    "derived Osty/Strike tags and block flag are preserved");
AccessTools.Field(typeof(ChaosCardModel), "_freeformDefinition").SetValue(card,
    definition with { Tags = [], Operations = [Op("T:D")] });
Check(!card.Tags.Any() && !card.GainsBlock, "definition replacement invalidates static metadata");
AccessTools.Field(typeof(ChaosCardModel), "_freeformDefinition").SetValue(card, definition);

var cache = typeof(ChaosCardModel).Assembly.GetType("AutoAnthony.ChaosRuntimeDescriptionCache", true)!;
var install = AccessTools.Method(cache, "InstallIfChanged")
    .CreateDelegate<Action<LocTable, string, string>>();
var table = new LocTable("aa-hotpath", new Dictionary<string, string>());
install(table, "description", "unchanged");
Measure("install_unchanged", () => install(table, "description", "unchanged"));
install(table, "description", "upgraded");
Check(table.GetRawText("description") == "upgraded", "changed template is still installed");
table.MergeWith(new Dictionary<string, string> { ["description"] = "external replacement" });
install(table, "description", "upgraded");
Check(table.GetRawText("description") == "upgraded", "an external localization overwrite cannot poison the installation cache");
var replacementTable = new LocTable("aa-hotpath", new Dictionary<string, string>());
install(replacementTable, "description", "unchanged");
Check(replacementTable.GetRawText("description") == "unchanged", "fresh language table is initialized");

var art = new ChaosCardDefinition(0, definition, "res://original.png", "", "", "", "",
    PortraitSourceId: "CARD.PERF", PortraitVariantId: "$original");
var visualKey = AccessTools.Method(typeof(ChaosCardModel).Assembly.GetType("AutoAnthony.ChaosPortraitCompatibility", true)!,
    "VisualKey").CreateDelegate<Func<ChaosCardDefinition, string>>();
Measure("portrait_identity", () => consumed += visualKey(art).Length);
Check(visualKey(art) != visualKey(art with { PortraitVariantId = "$redirected", PortraitVariantPath = "res://other.png" }),
    "new portrait assignment has a different identity");
if (!baseline)
    Check(ReferenceEquals(visualKey(art), visualKey(art with { Card = definition with { Cost = 2 } })),
        "equivalent editor art projections reuse the same identity key");

var tips = AccessTools.PropertyGetter(typeof(ChaosCardModel), "ExtraHoverTips")
    .CreateDelegate<Func<ChaosCardModel, IEnumerable<IHoverTip>>>();
var plainCard = Model(definition with { Tags = [], Operations = [Op("T:D")] });
Measure("empty_extra_tips", () => { foreach (var tip in tips(plainCard)) consumed += tip.Id.Length; }, 20_000);
ModelDb.Inject(typeof(Dazed));
var derivativeCard = Model(definition with { Tags = [], Operations = [Op("D:CreateDazedInDiscard")] });
Measure("derivative_extra_tips", () => { foreach (var tip in tips(derivativeCard)) consumed += tip.Id.Length; }, 10_000);
var first = tips(derivativeCard).OfType<CardHoverTip>().Single().Card;
var cacheFailures = new List<string>();
void CacheCheck(bool result, string message)
{
    Console.WriteLine((result ? "PASS " : "FAIL ") + message);
    if (!result) cacheFailures.Add(message);
}
first.RemoveKeyword(MegaCrit.Sts2.Core.Entities.Cards.CardKeyword.Ethereal);
var reread = tips(derivativeCard).OfType<CardHoverTip>().Single().Card;
CacheCheck(!ReferenceEquals(first, reread)
    && reread.Keywords.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardKeyword.Ethereal),
    "mutating a returned derivative preview cannot contaminate the next query");
var resolver = typeof(ChaosCardModel).Assembly.GetType("AutoAnthony.ChaosDerivativeResolver", true)!;
var isDetached = AccessTools.Method(resolver, "IsDetachedPreview").CreateDelegate<Func<CardModel, bool>>();
CacheCheck(isDetached(reread), "materialized derivative previews retain detached-preview behavior");
var nativeLocale = AccessTools.Field(typeof(LocManager), "<Instance>k__BackingField");
var originalLocale = nativeLocale.GetValue(null);
var locale = (LocManager)RuntimeHelpers.GetUninitializedObject(typeof(LocManager));
AccessTools.Field(typeof(LocManager), "<Language>k__BackingField").SetValue(locale, "eng");
var localeTables = AccessTools.Field(typeof(LocManager), "_tables");
var tipCache = AccessTools.Field(typeof(ChaosCardModel), "BuiltInHoverTips").GetValue(null)!;
object CachedPrototypes()
{
    var lookup = new object?[] { derivativeCard, null };
    tipCache.GetType().GetMethod("TryGetValue")!.Invoke(tipCache, lookup);
    return AccessTools.Field(lookup[1]!.GetType(), "Tips").GetValue(lookup[1])!;
}
try
{
    nativeLocale.SetValue(null, locale);
    localeTables.SetValue(locale, new Dictionary<string, LocTable>());
    _ = tips(derivativeCard).ToArray();
    var initial = CachedPrototypes();
    _ = tips(derivativeCard).ToArray();
    CacheCheck(ReferenceEquals(initial, CachedPrototypes()), "unchanged locale tables reuse private prototypes");
    localeTables.SetValue(locale, new Dictionary<string, LocTable>());
    _ = tips(derivativeCard).ToArray();
    CacheCheck(!ReferenceEquals(initial, CachedPrototypes()),
        "reloading tables for the same language invalidates cached tooltips");
    var retiredTables = ProbeHooks.PrimeTemporaryTables(locale, derivativeCard, tips);
    localeTables.SetValue(locale, new Dictionary<string, LocTable>());
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    CacheCheck(!retiredTables.IsAlive, "stale tooltip entries do not retain old localization tables");
}
finally { nativeLocale.SetValue(null, originalLocale); }
if (cacheFailures.Count > 0) throw new InvalidOperationException(string.Join("; ", cacheFailures));
new Harmony("autoanthony.tests.live-energy-tip").Patch(
    AccessTools.Method(typeof(HoverTipFactory), nameof(HoverTipFactory.ForEnergy), [typeof(CardModel)]),
    prefix: new HarmonyMethod(typeof(ProbeHooks), nameof(ProbeHooks.Energy)));
var energyCard = Model(definition with { Tags = [], Operations = [Op("N:E")] });
ProbeHooks.EnergyContext = "first-character";
Check(tips(energyCard).Single().Id == "energy:first-character", "energy hint uses the current character context");
ProbeHooks.EnergyContext = "second-character";
Check(tips(energyCard).Single().Id == "energy:second-character", "cached definition does not retain an old energy icon");

ModelDb.Inject(typeof(GiantRock));
ModelDb.Inject(typeof(MegaCrit.Sts2.Core.Models.Enchantments.Instinct));
var createPreview = AccessTools.Method(resolver, "CreatePreview")
    .CreateDelegate<Func<GeneratorOperation, bool, CardModel>>();
var clonePreview = AccessTools.Method(resolver, "CloneDetachedPreview")
    .CreateDelegate<Func<CardModel, CardModel>>();
var prototype = createPreview(Op("N:CreateShiv") with { DerivativeId = "rock", DerivativeEnchantmentId = "instinct" }, false);
var enchantedPreview = clonePreview(prototype);
Check(isDetached(enchantedPreview) && !ReferenceEquals(prototype.Enchantment, enchantedPreview.Enchantment)
    && ReferenceEquals(enchantedPreview.Enchantment!.Card, enchantedPreview),
    "preview cloning isolates enchantments and preserves their new card owner");
new Harmony("autoanthony.tests.detached-preview").Patch(
    AccessTools.Method(typeof(CardModel), nameof(CardModel.UpdateDynamicVarPreview)),
    postfix: new HarmonyMethod(AccessTools.Method(
        typeof(ChaosCardModel).Assembly.GetType("AutoAnthony.ChaosDetachedDerivativePreviewPatch", true)!, "Postfix")));
enchantedPreview.DynamicVars.ClearPreview();
enchantedPreview.UpdateDynamicVarPreview(MegaCrit.Sts2.Core.Entities.Cards.CardPreviewMode.None,
    null, enchantedPreview.DynamicVars);
Check(enchantedPreview.DynamicVars.Damage.PreviewValue == prototype.DynamicVars.Damage.BaseValue * 2,
    "Instinct Giant Rock still displays doubled damage after cloning and clearing its preview");
var upgradedPreview = clonePreview(createPreview(Op("N:CreateShiv") with { DerivativeId = "rock" }, true));
Check(upgradedPreview.IsUpgraded && upgradedPreview.DynamicVars.Damage.BaseValue == 24,
    "upgraded derivative previews retain their upgraded values");
ModelDb.Inject(typeof(Fuel));
ModelDb.Inject(typeof(AncientFuel));
var fuelState = AccessTools.Field(typeof(ChaosRunDefinitions), "_ancientFuelActive");
var originalFuel = fuelState.GetValue(null);
try
{
    var fuelCard = Model(definition with { Tags = [], Operations = [Op("N:CreateShiv") with { DerivativeId = "fuel" }] });
    fuelState.SetValue(null, false);
    Check(tips(fuelCard).OfType<CardHoverTip>().Single().Card is Fuel, "ordinary runs preview ordinary Fuel");
    fuelState.SetValue(null, true);
    Check(tips(fuelCard).OfType<CardHoverTip>().Single().Card is AncientFuel, "run-level Fuel changes invalidate previews");
}
finally { fuelState.SetValue(null, originalFuel); }
var otherCard = Model(derivativeCard.Generated);
var second = tips(otherCard).OfType<CardHoverTip>().Single().Card;
Check(first is Dazed && second is Dazed && !ReferenceEquals(first, second),
    "different owner cards never share a mutable derivative preview");
AccessTools.Field(typeof(CardModel), "_currentUpgradeLevel").SetValue(derivativeCard, 1);
Check(!ReferenceEquals(first, tips(derivativeCard).OfType<CardHoverTip>().Single().Card),
    "changing upgrade state rebuilds the derivative preview");
AccessTools.Field(typeof(ChaosCardModel), "_freeformDefinition").SetValue(derivativeCard, plainCard.Generated);
Check(!tips(derivativeCard).OfType<CardHoverTip>().Any(), "edited components replace old cached derivative references");
if (extensions)
{
    var dynamicSpec = new OperationRuntimeSpec(1, "perf_hover", "", "self", "", "", "", [], []);
    var dynamicOperation = new GeneratorOperation("external:perf_hover", OperationScope.NonTargeted,
        "", new Dictionary<string, int>(), RuntimeSpec: dynamicSpec);
    var dynamicCard = Model(definition with { Tags = [], Operations = [dynamicOperation] });
    dynamicCard.ExtraDamage = 3;
    Check(tips(dynamicCard).Single().Id == "live:3", "extension tooltip reads live card state");
    dynamicCard.ExtraDamage = 7;
    Check(tips(dynamicCard).Single().Id == "live:7", "extension tooltip callbacks are never frozen by built-in caching");
    var keywordCard = Model(plainCard.Generated with { CustomKeywords = ["perf_keyword"] });
    keywordCard.ExtraDamage = 3;
    Check(keywordCard.Tags.Contains(GameTag.Strike) && tips(keywordCard).Single().Id == "keyword:3",
        "custom keyword tags and tips read current instance state");
    keywordCard.ExtraDamage = 7;
    Check(!keywordCard.Tags.Any() && tips(keywordCard).Single().Id == "keyword:7",
        "custom keyword projections remain live after cached builtin queries");
    AccessTools.Field(typeof(ChaosCardModel), "_freeformDefinition").SetValue(keywordCard, keywordCard.Generated with
    {
        Upgrade = new(1, [], "", [], "", RemovedCustomKeywords: ["perf_keyword"])
    });
    AccessTools.Field(typeof(CardModel), "_currentUpgradeLevel").SetValue(keywordCard, 1);
    Check(!tips(keywordCard).Any(), "upgrading away a custom keyword removes its live tips");
}
try
{
    ComponentPresentationApi.Register("late_route", "", new LiveTips());
    throw new Exception("Registration unexpectedly remained open");
}
catch (InvalidOperationException) { Console.WriteLine("PASS presentation registration still freezes at first query"); }

var seeded = new StringBuilder();
foreach (var character in Enum.GetValues<GeneratedCharacter>())
foreach (var ultimate in new[] { false, true })
{
    var generator = new RandomCardGenerator(character, seed: 20261028 + (int)character,
        unlockComponentRoles: ultimate, balancedValues: true);
    foreach (var rarity in character == GeneratedCharacter.Colorless
                 ? new[] { GeneratedRarity.Uncommon, GeneratedRarity.Rare }
                 : new[] { GeneratedRarity.Common, GeneratedRarity.Uncommon, GeneratedRarity.Rare })
        for (var index = 0; index < 5; index++) seeded.Append(CardTinkeringApi.SerializeCard(generator.Generate(rarity)));
}
Console.WriteLine("SEED_DIGEST " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seeded.ToString()))));
var output = samples.Select(sample => new { sample.Name, sample.BytesPerCall, sample.MicrosecondsPerCall }).ToArray();
if (args.FirstOrDefault(argument => argument.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) is { } outputPath)
    File.WriteAllText(outputPath, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
if (!baseline)
{
    Check(samples.Single(sample => sample.Name == "install_unchanged").BytesPerCall < 8,
        "unchanged template refresh has no per-call collection allocation");
    Check(samples.Single(sample => sample.Name == "derivative_extra_tips").BytesPerCall < (extensions ? 3072 : 2048),
        "isolated derivative tooltips still allocate less than the original uncached path");
}
GC.KeepAlive(consumed);
Console.WriteLine("Generated-card hot-path regression passed. No game or user files were modified.");

static class ProbeHooks
{
    public static bool Skip() => false;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static WeakReference PrimeTemporaryTables(LocManager locale, ChaosCardModel card,
        Func<ChaosCardModel, IEnumerable<IHoverTip>> tips)
    {
        var tables = new Dictionary<string, LocTable>();
        AccessTools.Field(typeof(LocManager), "_tables").SetValue(locale, tables);
        _ = tips(card).ToArray();
        return new WeakReference(tables);
    }
    public static string EnergyContext = "";
    public static bool Energy(ref IHoverTip __result)
    {
        __result = new ProbeTip("energy:" + EnergyContext);
        return false;
    }
}

sealed class LiveTips : IComponentHoverTipProvider
{
    public IEnumerable<IHoverTip> BuildHoverTips(ComponentPresentationContext context) =>
        [new ProbeTip("live:" + context.Card.ExtraDamage)];
}

sealed class LiveKeyword : IComponentKeywordRuntimeAdapter
{
    public IEnumerable<GameTag> SemanticTags(ChaosCardModel card) => card.ExtraDamage == 3 ? [GameTag.Strike] : [];
    public IEnumerable<IHoverTip> BuildHoverTips(ChaosCardModel card) => [new ProbeTip("keyword:" + card.ExtraDamage)];
}

sealed record ProbeTip(string Id) : IHoverTip
{
    public bool IsSmart => false;
    public bool IsDebuff => false;
    public bool IsInstanced => false;
    public AbstractModel? CanonicalModel => null;
}
