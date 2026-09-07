using System.Text.Json;
using System.Text.Encodings.Web;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using ChaosCardGenerator;

var count = 1;
var seed = (int?)null;
var selfTest = false;
var semanticEquivalenceAudit = false;
var dumpAtoms = false;
var dumpRuntimeTemplates = false;
var distributionAudit = false;
var componentOccurrenceAudit = false;
var nativeBudgetCenterAudit = false;
var upgradeAudit = false;
var ultimateChaos = false;
var forceSpecialX = false;
var suppressDerivativeReferences = false;
var aggressiveValues = false;
var numericRandomMode = false;
var catalogOwnershipAudit = false;
var nativeDecompositionAudit = false;
string? writeRefactorBaseline = null;
string? compareRefactorBaseline = null;
string? textDependencySourceRoot = null;
string? textDependencyOutput = null;
string? runtimeSpecCoverageOutput = null;
string? numericSlotAuditOutput = null;
string? runtimeSpecNumericCoverageOutput = null;
string? catalogRuntimeSpecsOutput = null;
string? structuredCatalogOutput = null;
string? nativeCardValuationOutput = null;
var baselineVersion = "unspecified";
var auditSamples = 2000;
int? poolAuditSeed = null;
var poolAuditAttempts = 1;
string? runSeed = null;
GeneratedRarity? fixedRarity = null;
var character = GeneratedCharacter.Ironclad;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--count" when i + 1 < args.Length:
            count = int.Parse(args[++i]);
            break;
        case "--seed" when i + 1 < args.Length:
            seed = int.Parse(args[++i]);
            break;
        case "--self-test":
            selfTest = true;
            break;
        case "--native-decomposition-audit":
            nativeDecompositionAudit = true;
            break;
        case "--semantic-equivalence-audit":
            semanticEquivalenceAudit = true;
            break;
        case "--dump-atoms":
            dumpAtoms = true;
            break;
        case "--dump-runtime-templates":
            dumpRuntimeTemplates = true;
            break;
        case "--distribution-audit":
            distributionAudit = true;
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedAuditSamples))
            {
                auditSamples = parsedAuditSamples;
                i++;
            }
            break;
        case "--component-occurrence-audit":
            componentOccurrenceAudit = true;
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedPoolSamples))
            {
                auditSamples = parsedPoolSamples;
                i++;
            }
            break;
        case "--native-budget-center-audit":
            nativeBudgetCenterAudit = true;
            break;
        case "--native-card-valuation-audit" when i + 1 < args.Length:
            nativeCardValuationOutput = args[++i];
            break;
        case "--upgrade-audit":
            upgradeAudit = true;
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedUpgradeAuditSamples))
            {
                auditSamples = parsedUpgradeAuditSamples;
                i++;
            }
            break;
        case "--pool-audit-seed" when i + 1 < args.Length:
            poolAuditSeed = int.Parse(args[++i]);
            break;
        case "--pool-audit-attempts" when i + 1 < args.Length:
            poolAuditAttempts = int.Parse(args[++i]);
            break;
        case "--ultimate-chaos":
            ultimateChaos = true;
            break;
        case "--special-x":
            forceSpecialX = true;
            break;
        case "--suppress-derivative-references":
            suppressDerivativeReferences = true;
            break;
        case "--aggressive-values":
            aggressiveValues = true;
            break;
        case "--numeric-random":
            numericRandomMode = true;
            break;
        case "--catalog-ownership-audit":
            catalogOwnershipAudit = true;
            break;
        case "--write-refactor-baseline" when i + 1 < args.Length:
            writeRefactorBaseline = args[++i];
            break;
        case "--compare-refactor-baseline" when i + 1 < args.Length:
            compareRefactorBaseline = args[++i];
            break;
        case "--baseline-version" when i + 1 < args.Length:
            baselineVersion = args[++i];
            break;
        case "--text-dependency-audit" when i + 2 < args.Length:
            textDependencySourceRoot = args[++i];
            textDependencyOutput = args[++i];
            break;
        case "--runtime-spec-coverage" when i + 1 < args.Length:
            runtimeSpecCoverageOutput = args[++i];
            break;
        case "--numeric-slot-audit" when i + 1 < args.Length:
            numericSlotAuditOutput = args[++i];
            break;
        case "--runtime-spec-numeric-coverage" when i + 1 < args.Length:
            runtimeSpecNumericCoverageOutput = args[++i];
            break;
        case "--write-catalog-runtime-specs" when i + 1 < args.Length:
            catalogRuntimeSpecsOutput = args[++i];
            break;
        case "--write-structured-catalog" when i + 1 < args.Length:
            structuredCatalogOutput = args[++i];
            break;
        case "--run-seed" when i + 1 < args.Length:
            runSeed = args[++i];
            break;
        case "--rarity" when i + 1 < args.Length:
            fixedRarity = Enum.Parse<GeneratedRarity>(args[++i], ignoreCase: true);
            break;
        case "--character" when i + 1 < args.Length:
            character = Enum.Parse<GeneratedCharacter>(args[++i], ignoreCase: true);
            break;
        default:
            throw new ArgumentException($"未知参数：{args[i]}");
    }
}

if (dumpAtoms)
{
    var catalog = CharacterComponentCatalogs.Get(character);
    foreach (var atom in catalog.Atoms.OrderBy(atom => atom.Template).ThenBy(atom => atom.ChineseText))
        Console.WriteLine($"{atom.Template}\t{atom.Scope}\t{atom.ChineseText}");
    return;
}

if (dumpRuntimeTemplates)
{
    var catalog = CharacterComponentCatalogs.Get(character);
    foreach (var atom in catalog.Atoms
                 .DistinctBy(atom => (atom.Template, atom.Scope,
                     OperationRuntimeSpecCompiler.GetOrCompile(atom).Opcode,
                     OperationRuntimeSpecCompiler.GetOrCompile(atom).Variant))
                 .OrderBy(atom => atom.Template, StringComparer.Ordinal)
                 .ThenBy(atom => atom.Scope))
    {
        var spec = OperationRuntimeSpecCompiler.GetOrCompile(atom);
        Console.WriteLine(string.Join('\t', atom.Template, atom.Scope, spec.Opcode, spec.Variant));
    }
    return;
}

if (writeRefactorBaseline is not null)
{
    var result = RefactorBaselineAudit.Write(writeRefactorBaseline, baselineVersion);
    Console.WriteLine($"baseline={result.Path}; uncompressedBytes={result.UncompressedBytes}; sha256={result.Sha256}");
    return;
}

if (compareRefactorBaseline is not null)
{
    var comparison = RefactorBaselineAudit.Compare(compareRefactorBaseline, baselineVersion);
    Console.WriteLine($"matches={comparison.Matches}; sha256={comparison.Current.Sha256}; {comparison.Detail}");
    if (!comparison.Matches) Environment.ExitCode = 1;
    return;
}

if (textDependencySourceRoot is not null && textDependencyOutput is not null)
{
    var result = RefactorBaselineAudit.WriteTextDependencyInventory(textDependencySourceRoot, textDependencyOutput);
    Console.WriteLine($"textDependencies={result.Entries}; output={result.Path}");
    return;
}

if (runtimeSpecCoverageOutput is not null)
{
    var output = Path.GetFullPath(runtimeSpecCoverageOutput);
    if (File.Exists(output)) throw new IOException($"Refusing to overwrite RuntimeSpec coverage audit: {output}");
    Directory.CreateDirectory(Path.GetDirectoryName(output)
        ?? throw new InvalidOperationException($"Coverage path has no parent directory: {output}"));
    File.WriteAllText(output, OperationRuntimeSpecCompiler.CoverageReport(),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    Console.WriteLine($"runtimeSpecCoverage={output}");
    return;
}

if (numericSlotAuditOutput is not null)
{
    var output = Path.GetFullPath(numericSlotAuditOutput);
    if (File.Exists(output)) throw new IOException($"Refusing to overwrite numeric-slot audit: {output}");
    Directory.CreateDirectory(Path.GetDirectoryName(output)
        ?? throw new InvalidOperationException($"Numeric-slot path has no parent directory: {output}"));
    File.WriteAllText(output, NumericSlotMigrationAudit.Report(),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    Console.WriteLine($"numericSlotAudit={output}");
    return;
}

if (runtimeSpecNumericCoverageOutput is not null)
{
    var output = Path.GetFullPath(runtimeSpecNumericCoverageOutput);
    if (File.Exists(output)) throw new IOException($"Refusing to overwrite numeric RuntimeSpec coverage: {output}");
    Directory.CreateDirectory(Path.GetDirectoryName(output)
        ?? throw new InvalidOperationException($"Numeric RuntimeSpec path has no parent directory: {output}"));
    File.WriteAllText(output, OperationRuntimeSpecCompiler.NumericCoverageReport(),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    Console.WriteLine($"runtimeSpecNumericCoverage={output}");
    return;
}

if (catalogRuntimeSpecsOutput is not null)
{
    var output = Path.GetFullPath(catalogRuntimeSpecsOutput);
    if (File.Exists(output)) throw new IOException($"Refusing to overwrite catalog RuntimeSpec registry: {output}");
    Directory.CreateDirectory(Path.GetDirectoryName(output)
        ?? throw new InvalidOperationException($"Catalog RuntimeSpec path has no parent directory: {output}"));
    var entries = Enum.GetValues<GeneratedCharacter>()
        .SelectMany(catalog => ReviewedAuthoringCatalogSource.GetForRuntimeSpecExport(catalog).Recipes.SelectMany(recipe =>
            recipe.Atoms.Select((atom, index) => new CatalogRuntimeSpecExport(
                CatalogRuntimeSpecRegistry.SemanticId(catalog, recipe.Id, index),
                OperationRuntimeSpecCompiler.CompileRequired(new GeneratorOperation(atom.Template, atom.Scope,
                    atom.ChineseText, new Dictionary<string, int>(),
                    RequiresSingleTarget: atom.RequiresSingleTarget))))))
        .OrderBy(entry => entry.Id, StringComparer.Ordinal)
        .ToArray();
    File.WriteAllText(output, JsonSerializer.Serialize(entries, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    }), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    Console.WriteLine($"catalogRuntimeSpecs={entries.Length}; output={output}");
    return;
}

if (structuredCatalogOutput is not null)
{
    var output = Path.GetFullPath(structuredCatalogOutput);
    if (File.Exists(output)) throw new IOException($"Refusing to overwrite structured catalog: {output}");
    Directory.CreateDirectory(Path.GetDirectoryName(output)
        ?? throw new InvalidOperationException($"Structured catalog path has no parent directory: {output}"));
    var entries = StructuredComponentCatalogRegistry.ExportAuthoringSource();
    File.WriteAllText(output, JsonSerializer.Serialize(entries,
        StructuredComponentCatalogRegistry.JsonOptions(indented: true)),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    Console.WriteLine($"structuredCatalogRecipes={entries.Count}; operations={entries.Sum(entry => entry.Atoms.Count)}; output={output}");
    return;
}

if (nativeBudgetCenterAudit)
{
    Console.Write(GeneratorDistributionAudit.RunNativeBudgetCenterAudit());
    return;
}

if (nativeCardValuationOutput is not null)
{
    var result = NativeCardValuationAudit.Write(nativeCardValuationOutput);
    Console.WriteLine($"nativeValuation={result.OutputDirectory}; cards={result.Cards}; "
        + $"componentOccurrences={result.ComponentOccurrences}; componentTemplates={result.ComponentTemplates}; "
        + $"packageTemplates={result.PackageTemplates}");
    return;
}

if (distributionAudit)
{
    Console.Write(GeneratorDistributionAudit.Run(character, auditSamples, ultimateChaos: ultimateChaos,
        balancedValues: !aggressiveValues));
    return;
}

if (componentOccurrenceAudit)
{
    Console.Write(ComponentOccurrenceAudit.Run(character, auditSamples, ultimateChaos: ultimateChaos,
        balancedValues: !aggressiveValues));
    return;
}

if (upgradeAudit)
{
    Console.Write(GeneratorUpgradeAudit.Run(character, auditSamples));
    return;
}

if (selfTest)
{
    GeneratorSelfTest.Run();
    NativeCardValuationAudit.ValidateCoverage();
    NativeReferenceCatalog.Validate();
    Console.WriteLine("RandomCardGenerator self-test passed.");
    return;
}

if (nativeDecompositionAudit)
{
    NativeReferenceCatalog.Validate();
    var executable = NativeCardDecompositionApi.Cards
        .Where(card => card.ExecutionSupport == NativeCardExecutionSupport.ExecutableRecipe).ToArray();
    var exactUpgrades = executable.Count(card =>
        NativeCardDecompositionApi.TryCreateDefinition(card.CatalogId, out _, out _));
    var unsupported = executable.Select(card =>
        (card.CatalogId, Success: NativeCardDecompositionApi.TryCreateDefinition(
            card.CatalogId, out _, out var actions), Actions: actions))
        .Where(item => !item.Success).ToArray();
    Console.WriteLine($"Native-card decomposition audit passed: 567 records; 481 executable bases; "
                      + $"{exactUpgrades} exact generated upgrades; 86 structured references.");
    foreach (var item in unsupported)
        Console.WriteLine($"upgrade-adapter-pending={item.CatalogId}:{string.Join(',', item.Actions)}");
    return;
}

if (semanticEquivalenceAudit)
{
    OperationRuntimeSpecCompiler.ValidateSemanticProjectionEquivalence();
    Console.WriteLine("RuntimeSpec semantic-equivalence audit passed.");
    return;
}

if (catalogOwnershipAudit)
{
    Console.WriteLine(CatalogOwnershipAudit.Run());
    return;
}

if (poolAuditSeed is { } exactPoolSeed)
{
    var poolRandom = new Random(exactPoolSeed);
    var poolRarities = character == GeneratedCharacter.Colorless
        ? Enumerable.Repeat(GeneratedRarity.Uncommon, 31)
            .Concat(Enumerable.Repeat(GeneratedRarity.Rare, 21)).ToArray()
        : Enumerable.Repeat(GeneratedRarity.Basic, character == GeneratedCharacter.Silent ? 12 : 10)
            .Concat(Enumerable.Repeat(GeneratedRarity.Common, 20))
            .Concat(Enumerable.Repeat(GeneratedRarity.Uncommon, 35))
            .Concat(Enumerable.Repeat(GeneratedRarity.Rare, 25))
            .Concat(Enumerable.Repeat(GeneratedRarity.Ancient, 2)).ToArray();
    var stopwatch = Stopwatch.StartNew();
    var gcPauseStart = GC.GetTotalPauseDuration();
    var gcCountsStart = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
    var resolved = false;
    var failure = string.Empty;
    var attemptsUsed = 0;
    long cardGenerationMs = 0;
    long generatorConstructionMs = 0;
    long startingRepairMs = 0;
    long initialStartingRepairMs = 0;
    long finalStartingRepairMs = 0;
    long supportRepairMs = 0;
    long slowestCardMs = 0;
    var slowestCardIndex = -1;
    long mostAllocatedCardBytes = 0;
    var mostAllocatedCardIndex = -1;
    long generationLoopWallMs = 0;
    var allocatedBytesStart = GC.GetTotalAllocatedBytes(precise: false);
    GeneratedCard[] poolCards = [];
    for (var attempt = 0; attempt < Math.Max(1, poolAuditAttempts) && !resolved; attempt++)
    {
        attemptsUsed = attempt + 1;
        var generatorStarted = Stopwatch.GetTimestamp();
        var poolGenerator = new RandomCardGenerator(character, poolRandom.Next(), ultimateChaos,
            suppressDerivativeReferences: suppressDerivativeReferences, balancedValues: !aggressiveValues,
            randomizeNumericValues: numericRandomMode);
        generatorConstructionMs += (long)Stopwatch.GetElapsedTime(generatorStarted).TotalMilliseconds;
        poolCards = new GeneratedCard[poolRarities.Length];
        var generationLoopStarted = Stopwatch.GetTimestamp();
        for (var index = 0; index < poolRarities.Length; index++)
        {
            var cardStarted = Stopwatch.GetTimestamp();
            var cardAllocatedStart = GC.GetAllocatedBytesForCurrentThread();
            poolCards[index] = poolGenerator.Generate(poolRarities[index]);
            var cardAllocated = GC.GetAllocatedBytesForCurrentThread() - cardAllocatedStart;
            var cardElapsed = (long)Stopwatch.GetElapsedTime(cardStarted).TotalMilliseconds;
            cardGenerationMs += cardElapsed;
            if (cardElapsed > slowestCardMs)
            {
                slowestCardMs = cardElapsed;
                slowestCardIndex = index;
            }
            if (cardAllocated > mostAllocatedCardBytes)
            {
                mostAllocatedCardBytes = cardAllocated;
                mostAllocatedCardIndex = index;
            }
            if (index == 9 && character != GeneratedCharacter.Colorless)
            {
                var startingCards = poolCards.Take(10).ToArray();
                var repairStarted = Stopwatch.GetTimestamp();
                if (!StartingPoolConstraintResolver.TryRepair(startingCards, poolGenerator, poolRandom,
                        minimumDamage: 4, minimumDefense: 4, replacementAttemptLimit: 20_000, out failure))
                    break;
                var repairElapsedMs = (long)Stopwatch.GetElapsedTime(repairStarted).TotalMilliseconds;
                startingRepairMs += repairElapsedMs;
                initialStartingRepairMs += repairElapsedMs;
                Array.Copy(startingCards, poolCards, startingCards.Length);
            }
        }
        generationLoopWallMs += (long)Stopwatch.GetElapsedTime(generationLoopStarted).TotalMilliseconds;
        if (poolCards.Any(card => card is null))
            continue;
        var supportStarted = Stopwatch.GetTimestamp();
        resolved = OstyPoolConstraintResolver.TryResolve(poolCards, poolRarities, poolGenerator, poolRandom,
                replacementAttemptLimit: 20_000, out failure)
            && SlyPoolConstraintResolver.TryResolve(poolCards, poolRarities, poolGenerator, poolRandom,
                replacementAttemptLimit: 20_000, out failure)
            && DerivativePoolConstraintResolver.TryRepairAndResolve(poolCards, poolRarities, poolGenerator,
                poolRandom, replacementAttemptLimit: 20_000, out failure)
             && GeneratedCardEffectIdentity.TryAudit(poolCards, out failure);
        supportRepairMs += (long)Stopwatch.GetElapsedTime(supportStarted).TotalMilliseconds;
        if (resolved && character != GeneratedCharacter.Colorless)
        {
            var startingCards = poolCards.Take(10).ToArray();
            var repairStarted = Stopwatch.GetTimestamp();
            resolved = StartingPoolConstraintResolver.TryRepair(startingCards, poolGenerator, poolRandom,
                minimumDamage: 4, minimumDefense: 4, replacementAttemptLimit: 20_000, out failure);
            var repairElapsedMs = (long)Stopwatch.GetElapsedTime(repairStarted).TotalMilliseconds;
            startingRepairMs += repairElapsedMs;
            finalStartingRepairMs += repairElapsedMs;
            if (resolved) Array.Copy(startingCards, poolCards, startingCards.Length);
        }
    }
    stopwatch.Stop();
    var gcPauseMs = (long)(GC.GetTotalPauseDuration() - gcPauseStart).TotalMilliseconds;
    var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedBytesStart;
    var gcCounts = Enumerable.Range(0, GC.MaxGeneration + 1)
        .Select(generation => GC.CollectionCount(generation) - gcCountsStart[generation]);
    var completedPoolCards = poolCards.OfType<GeneratedCard>().ToArray();
    Console.WriteLine($"character={character}; ultimate={ultimateChaos}; cards={poolRarities.Length}; "
        + $"resolved={resolved}; attempts={attemptsUsed}; elapsedMs={stopwatch.ElapsedMilliseconds}; "
        + $"sly={completedPoolCards.Count(SlyPoolConstraintResolver.HasSly)}; "
        + $"discard={completedPoolCards.Count(SlyPoolConstraintResolver.HasDiscardEffect)}; "
        + $"startingDamage={completedPoolCards.Take(10).Count(StartingPoolConstraintResolver.CountsAsDamage)}; "
        + $"startingDefense={completedPoolCards.Take(10).Count(StartingPoolConstraintResolver.CountsAsDefense)}; "
        + $"startingHighResource={completedPoolCards.Take(10).Count(StartingPoolConstraintResolver.IsHighResourceCard)}; "
        + $"generatorConstructionMs={generatorConstructionMs}; cardGenerationMs={cardGenerationMs}; "
        + $"generationLoopWallMs={generationLoopWallMs}; "
        + $"startingRepairMs={startingRepairMs}({initialStartingRepairMs}+{finalStartingRepairMs}); "
        + $"supportRepairMs={supportRepairMs}; slowestCard={slowestCardIndex + 1}/{slowestCardMs}ms; "
        + $"mostAllocatedCard={mostAllocatedCardIndex + 1}/{mostAllocatedCardBytes / 1024d / 1024d:F1}MiB; "
        + $"gcPauseMs={gcPauseMs}; gcCollections={string.Join('/', gcCounts)}; "
        + $"allocatedMiB={allocatedBytes / 1024d / 1024d:F1}; "
        + $"failure={failure}");
    return;
}

var generator = new RandomCardGenerator(character, seed, ultimateChaos,
    suppressDerivativeReferences: suppressDerivativeReferences, balancedValues: !aggressiveValues,
    randomizeNumericValues: numericRandomMode);
if (runSeed is not null)
{
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes("AutoAnthony/v111/nonnegative-v10/" + runSeed));
    var runRandom = new Random(BitConverter.ToInt32(hash, 0) & int.MaxValue);
    generator = new RandomCardGenerator(character, runRandom.Next(), ultimateChaos,
        suppressDerivativeReferences: suppressDerivativeReferences, balancedValues: !aggressiveValues,
        randomizeNumericValues: numericRandomMode);
}
var cards = runSeed is null
    ? fixedRarity is { } rarity
        ? Enumerable.Range(0, count).Select(_ => forceSpecialX
            ? generator.GenerateSpecialX(rarity)
            : generator.Generate(rarity)).ToArray()
        : Enumerable.Range(0, count).Select(_ => generator.Generate()).ToArray()
    : Enumerable.Repeat(GeneratedRarity.Basic, character == GeneratedCharacter.Silent ? 12 : 10)
        .Concat(Enumerable.Repeat(GeneratedRarity.Common, 20))
        .Concat(Enumerable.Repeat(GeneratedRarity.Uncommon, 35))
        .Concat(Enumerable.Repeat(GeneratedRarity.Rare, 25))
        .Concat(Enumerable.Repeat(GeneratedRarity.Ancient, 2))
        .Take(count)
        .Select(generator.Generate)
        .ToArray();
Console.WriteLine(JsonSerializer.Serialize(cards, new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
}));

internal sealed record CatalogRuntimeSpecExport(string Id, OperationRuntimeSpec Spec)
;
