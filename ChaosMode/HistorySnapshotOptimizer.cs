using System.Security.Cryptography;
using System.Text;
using ChaosCardGenerator;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace AutoAnthony;

internal sealed record HistoryOptimizationReport(
    int TotalFiles,
    int AutoAnthonyFiles,
    int OptimizedFiles,
    int AlreadyCompactFiles,
    int FailedFiles,
    long BytesBefore,
    long BytesAfter);

/// <summary>
/// Explicit, user-triggered migration for histories created before sparse final-deck snapshots. It intentionally
/// writes only the local copy: routing hundreds of old files through RunHistorySaveManager.SaveHistory would invoke
/// the vanilla 100-file cloud pruning policy once per file. The game's normal directory sync can reconcile the
/// compact local files later without deleting unrelated local history during this operation.
/// </summary>
internal static class HistorySnapshotOptimizer
{
    private const int YieldEveryFiles = 4;
    private static int _running;
    private static readonly AccessTools.FieldRef<SaveManager, ISaveStore> SaveStore =
        AccessTools.FieldRefAccess<SaveManager, ISaveStore>("_saveStore");

    internal static bool IsRunning => Volatile.Read(ref _running) != 0;

    internal static bool ShouldShowForCurrentProfile()
    {
        if (!SaveManager.Instance.IsProfileInitialized) return false;
        var profileId = SaveManager.Instance.CurrentProfileId;
        // This is a one-time migration for a profile, not a live history-file monitor. New histories already use
        // sparse snapshots, while cloud reconciliation can legitimately change file timestamps after migration.
        // Basing visibility on the mutable directory signature therefore made an already-completed action reappear.
        return !ChaosModSettings.HasHistoryOptimizationRecord(profileId);
    }

    internal static async Task<HistoryOptimizationReport> OptimizeCurrentProfileAsync(
        Action<int, int>? reportProgress = null)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("History optimization is already running.");

        try
        {
            var saveManager = SaveManager.Instance;
            if (!saveManager.IsProfileInitialized)
                throw new InvalidOperationException("The current save profile is not initialized.");
            var profileId = saveManager.CurrentProfileId;
            var historyDirectory = HistoryDirectory();
            var names = saveManager.GetAllRunHistoryNames()
                .Where(name => name.EndsWith(".run", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            var autoAnthony = 0;
            var optimized = 0;
            var alreadyCompact = 0;
            var failed = 0;
            long bytesBefore = 0;
            long bytesAfter = 0;
            reportProgress?.Invoke(0, names.Length);

            for (var index = 0; index < names.Length; index++)
            {
                var name = names[index];
                try
                {
                    // Do not deserialize, migrate, or rewrite vanilla/other-mod histories. The three-part marker
                    // check is specific to AutoAnthony's modifier id, property name and payload encoding.
                    if (!IsAutoAnthonyHistoryFile(historyDirectory, name)) continue;
                    var read = saveManager.LoadRunHistory(name);
                    if (!read.Success || read.SaveData is not { } history)
                    {
                        failed++;
                        continue;
                    }

                    var payload = ChaosPoolSnapshot.ReadFrom(history);
                    if (string.IsNullOrWhiteSpace(payload)) continue;
                    autoAnthony++;
                    var characters = CharacterMappings(history);
                    if (characters.Length == 0)
                    {
                        failed++;
                        continue;
                    }

                    var finalDeckIds = history.Players
                        .SelectMany(player => player.Deck)
                        .Select(card => card.Id)
                        .Where(id => id is not null)
                        .Select(id => id!)
                        .Distinct()
                        .ToArray();
                    if (!ChaosPoolSnapshot.TryCompactHistorySnapshot(payload, characters, history.Seed,
                            finalDeckIds, out var compact, out _, out _, out var compactFailure))
                    {
                        if (compactFailure is null) alreadyCompact++;
                        else
                        {
                            failed++;
                            Log.Warn($"[AutoAnthony] Could not inspect old run history {name}: {compactFailure}");
                        }
                        continue;
                    }

                    var snapshotId = ModelDb.Modifier<ChaosPoolSnapshotModifier>().Id;
                    history.Modifiers.RemoveAll(modifier => modifier.Id == snapshotId);
                    if (compact is not null) history.Modifiers.Add(compact);
                    history.SchemaVersion = saveManager.GetLatestSchemaVersion<RunHistory>();
                    var json = SaveManager.ToJson(history);
                    var (before, after) = await ReplaceHistoryAtomicallyAndSyncCloud(
                        historyDirectory, profileId, name, json);
                    bytesBefore += before;
                    bytesAfter += after;
                    optimized++;
                }
                catch (Exception exception)
                {
                    failed++;
                    Log.Warn($"[AutoAnthony] Failed to optimize old run history {name}; original preserved: {exception}");
                }
                finally
                {
                    reportProgress?.Invoke(index + 1, names.Length);
                }

                if ((index + 1) % YieldEveryFiles == 0) await NextProcessFrame();
            }

            if (failed == 0)
                ChaosModSettings.MarkHistoryOptimizationCurrent(profileId, ComputeCurrentSignature());
            var report = new HistoryOptimizationReport(names.Length, autoAnthony, optimized, alreadyCompact,
                failed, bytesBefore, bytesAfter);
            Log.Info($"[AutoAnthony] Old run-history optimization finished: total={report.TotalFiles}, "
                     + $"aa={report.AutoAnthonyFiles}, optimized={report.OptimizedFiles}, "
                     + $"alreadyCompact={report.AlreadyCompactFiles}, failed={report.FailedFiles}, "
                     + $"bytes={report.BytesBefore}->{report.BytesAfter}.");
            return report;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>Keep a completed profile hidden when v0.2.44+ appends another already-sparse history.</summary>
    internal static void RefreshCompletedProfileSignatureAfterHistorySave()
    {
        if (!SaveManager.Instance.IsProfileInitialized) return;
        var profileId = SaveManager.Instance.CurrentProfileId;
        if (!ChaosModSettings.HasHistoryOptimizationRecord(profileId)) return;
        ChaosModSettings.MarkHistoryOptimizationCurrent(profileId, ComputeCurrentSignature());
    }

    private static GeneratedCharacter[] CharacterMappings(RunHistory history) => history.Players
        .Select(player => player.Character == ModelDb.Character<MegaCrit.Sts2.Core.Models.Characters.Ironclad>().Id
            ? GeneratedCharacter.Ironclad
            : player.Character == ModelDb.Character<MegaCrit.Sts2.Core.Models.Characters.Silent>().Id
                ? GeneratedCharacter.Silent
                : player.Character == ModelDb.Character<MegaCrit.Sts2.Core.Models.Characters.Defect>().Id
                    ? GeneratedCharacter.Defect
                    : player.Character == ModelDb.Character<MegaCrit.Sts2.Core.Models.Characters.Necrobinder>().Id
                        ? GeneratedCharacter.Necrobinder
                        : player.Character == ModelDb.Character<MegaCrit.Sts2.Core.Models.Characters.Regent>().Id
                            ? GeneratedCharacter.Regent
                            : (GeneratedCharacter?)null)
        .Where(character => character.HasValue)
        .Select(character => character!.Value)
        .Distinct()
        .OrderBy(character => character)
        .ToArray();

    private static async Task<(long Before, long After)> ReplaceHistoryAtomicallyAndSyncCloud(
        string historyDirectory, int profileId, string fileName, string json)
    {
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
            throw new InvalidDataException("History file name contains a path component.");
        var root = Path.GetFullPath(historyDirectory);
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("History file resolved outside the current profile.");

        var before = new FileInfo(path).Length;
        var temporary = path + ".autoanthony-compact.tmp";
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        var after = new FileInfo(path).Length;
        if (SaveStore(SaveManager.Instance) is CloudSaveStore cloud)
        {
            var relativePath = Path.Combine(RunHistorySaveManager.GetHistoryPath(profileId), fileName)
                .Replace(Path.DirectorySeparatorChar, '/');
            try
            {
                var persisted = cloud.IsFilePersisted(relativePath);
                await cloud.OverwriteCloudWithLocal(relativePath, forgetImmediately: !persisted);
            }
            catch (Exception exception)
            {
                // The local atomic replacement is already valid. Cloud calls are best-effort throughout the base
                // game too; retain the compact local copy and let a later normal sync retry reconciliation.
                Log.Warn($"[AutoAnthony] Optimized local history {fileName}, but cloud synchronization failed: {exception.Message}");
            }
        }
        return (before, after);
    }

    private static bool IsAutoAnthonyHistoryFile(string historyDirectory, string fileName)
    {
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)) return false;
        var root = Path.GetFullPath(historyDirectory);
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        return ContainsAutoAnthonySnapshotMarker(File.ReadAllText(path));
    }

    private static bool ContainsAutoAnthonySnapshotMarker(string text) =>
        text.Contains(ModelDb.Modifier<ChaosPoolSnapshotModifier>().Id.Entry, StringComparison.Ordinal)
        && text.Contains("\"PoolSnapshot\"", StringComparison.Ordinal)
        && text.Contains("\"AA1:", StringComparison.Ordinal);

    internal static void AuditIsolation()
    {
        var ownId = ModelDb.Modifier<ChaosPoolSnapshotModifier>().Id.Entry;
        if (!ContainsAutoAnthonySnapshotMarker(
                $"{{\"id\":\"{ownId}\",\"name\":\"PoolSnapshot\",\"value\":\"AA1:test\"}}")
            || ContainsAutoAnthonySnapshotMarker(
                "{\"id\":\"OTHER_MOD\",\"name\":\"PoolSnapshot\",\"value\":\"AA1:test\"}")
            || ContainsAutoAnthonySnapshotMarker(
                $"{{\"id\":\"{ownId}\",\"name\":\"Other\",\"value\":\"AA1:test\"}}"))
            throw new InvalidOperationException("Run-history optimization ownership filter audit failed.");
    }

    private static string HistoryDirectory() => ProjectSettings.GlobalizePath(
        SaveManager.Instance.GetProfileScopedPath(Path.Combine("saves", "history")));

    private static string ComputeCurrentSignature()
    {
        try
        {
            var directory = HistoryDirectory();
            if (!Directory.Exists(directory)) return "EMPTY";
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.run", SearchOption.TopDirectoryOnly)
                         .OrderBy(file => file.Name, StringComparer.Ordinal))
            {
                var metadata = Encoding.UTF8.GetBytes($"{file.Name}\0{file.Length}\0{file.LastWriteTimeUtc.Ticks}\n");
                hash.AppendData(metadata);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception exception)
        {
            Log.Warn($"[AutoAnthony] Could not fingerprint run history for optimization visibility: {exception.Message}");
            return string.Empty;
        }
    }

    private static async Task NextProcessFrame()
    {
        if (Engine.GetMainLoop() is SceneTree tree)
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        else
            await Task.Yield();
    }
}
