using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using AutoAnthony;
using ChaosCardGenerator;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;

static void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    Console.WriteLine($"PASS {label}");
}

var assembly = typeof(ChaosRunDefinitions).Assembly;
var patch = assembly.GetType("AutoAnthony.Patches.ChaosDefaultTransformationPoolPatch", true)!;
var target = AccessTools.Method(typeof(CardFactory), nameof(CardFactory.GetDefaultTransformationOptions));
var original = PatchProcessor.GetOriginalInstructions(target);
var before = original.Select(i => (i.opcode, i.operand)).ToArray();
var transpiler = AccessTools.Method(patch, "Transpiler");
var rewritten = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, [original])!).ToArray();
var changed = Enumerable.Range(0, rewritten.Length)
    .Where(i => before[i] != (rewritten[i].opcode, rewritten[i].operand)).ToArray();
Check(rewritten.Length == before.Length && changed.Length == 1,
    "Actual v111 IL: only one pool lookup changes; native filters remain intact");
Check(rewritten[changed[0]].opcode == OpCodes.Call
      && Equals(rewritten[changed[0]].operand, AccessTools.Method(patch, "GetActivePoolCards")),
    "Replacement uses a static method with the original pool receiver");
var originalLookup = (MethodInfo)before[changed[0]].operand;
var replacementLookup = (MethodInfo)rewritten[changed[0]].operand;
Check(replacementLookup.GetParameters().Select(p => p.ParameterType).SequenceEqual(
        new[] { originalLookup.DeclaringType }.Concat(originalLookup.GetParameters().Select(p => p.ParameterType)))
      && originalLookup.ReturnType == replacementLookup.ReturnType, "IL stack contract matches");

// Inject isolated canonical pool identities without starting Godot or changing a user's run/settings.
var models = (Dictionary<ModelId, AbstractModel>)AccessTools.Field(typeof(ModelDb), "_contentById").GetValue(null)!;
var routing = assembly.GetType("AutoAnthony.Patches.CharacterPoolPatchRouting", true)!;
var activePoolFor = AccessTools.Method(routing, "ActivePoolFor");
var active = AccessTools.Field(typeof(ChaosRunDefinitions), "_runActive");
var pairs = new[]
{
    (typeof(IroncladCardPool), typeof(ChaosIroncladCardPool)),
    (typeof(SilentCardPool), typeof(ChaosSilentCardPool)),
    (typeof(DefectCardPool), typeof(ChaosDefectCardPool)),
    (typeof(NecrobinderCardPool), typeof(ChaosNecrobinderCardPool)),
    (typeof(RegentCardPool), typeof(ChaosRegentCardPool))
};
foreach (var (nativeType, generatedType) in pairs)
{
    var native = (CardPoolModel)RuntimeHelpers.GetUninitializedObject(nativeType);
    var generated = (CardPoolModel)RuntimeHelpers.GetUninitializedObject(generatedType);
    models[ModelDb.GetId(generatedType)] = generated;
    active.SetValue(null, false);
    Check(ReferenceEquals(activePoolFor.Invoke(null, [native]), native), $"{nativeType.Name}: mod inactive stays vanilla");
    active.SetValue(null, true);
    Check(ReferenceEquals(activePoolFor.Invoke(null, [native]), generated), $"{nativeType.Name}: active run maps to its own color");
    Check(ReferenceEquals(activePoolFor.Invoke(null, [generated]), generated), $"{generatedType.Name}: already normalized stays unchanged");
}
foreach (var type in new[] { typeof(ColorlessCardPool), typeof(CurseCardPool), typeof(TokenCardPool), typeof(EventCardPool), typeof(MockCardPool) })
{
    var pool = (CardPoolModel)RuntimeHelpers.GetUninitializedObject(type);
    Check(ReferenceEquals(activePoolFor.Invoke(null, [pool]), pool), $"{type.Name}: native/shared/external boundary preserved");
}

var harmony = new Harmony("autoanthony.test.transformation-pools");
new PatchClassProcessor(harmony, patch).Patch();
new PatchClassProcessor(harmony, assembly.GetType("AutoAnthony.Patches.ColorlessPoolContentsPatch", true)!).Patch();
Check(Harmony.GetPatchInfo(target)!.Owners.Contains(harmony.Id), "Default transformation patch installs on the actual game method");
var contents = (System.Collections.IDictionary)AccessTools.Field(routing, "PoolContentsCache").GetValue(null)!;
var getCards = AccessTools.Method(patch, "GetActivePoolCards");
var preserve = AccessTools.Field(typeof(ChaosRunDefinitions), "_activePreserveOriginalCards");
var replaceStarters = AccessTools.Field(typeof(ChaosRunDefinitions), "_activeReplaceStartingCards");
// These two distinct model identities stand in for an already generated run snapshot and a vanilla card.
// Cache injection avoids invoking the generator or loading Godot; the actual pool getter and native
// GetUnlockedCards are exercised below, including both run policy flags and all five pool mappings.
var generatedCandidate = (CardModel)RuntimeHelpers.GetUninitializedObject(typeof(StrikeIronclad));
var nativeCandidate = (CardModel)RuntimeHelpers.GetUninitializedObject(typeof(Bash));
foreach (var (nativeType, generatedType) in pairs)
foreach (var keepOriginal in new[] { false, true })
foreach (var replaceBasic in new[] { false, true })
{
    var character = Enum.Parse<GeneratedCharacter>(nativeType.Name.Replace("CardPool", ""));
    CardModel[] expected = keepOriginal ? [generatedCandidate, nativeCandidate] : [generatedCandidate];
    contents[(character, replaceBasic, keepOriginal)] = expected;
    preserve.SetValue(null, keepOriginal);
    replaceStarters.SetValue(null, replaceBasic);
    var source = (CardPoolModel)RuntimeHelpers.GetUninitializedObject(nativeType);
    var actual = ((IEnumerable<CardModel>)getCards.Invoke(null, [source, null, CardMultiplayerConstraint.None])!).ToArray();
    Check(actual.SequenceEqual(expected), $"{character}: transformation follows PreserveOriginal={keepOriginal}, ReplaceStarting={replaceBasic}");
}
harmony.UnpatchAll(harmony.Id);
active.SetValue(null, false);
Console.WriteLine("Transformation pool routing regressions passed; no game process or save was opened.");
