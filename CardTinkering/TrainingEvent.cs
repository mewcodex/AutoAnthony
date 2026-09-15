using Godot;
using System.Text;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace AutoAnthonyCardTinkering;

public sealed class CardTinkeringEvent : EventModel
{
    private const int DeckSyncChunkSize = 4;
    internal const string PortraitPath = "res://images/events/tinker_time.png";
    internal const string LockIconPath = "res://images/packed/common_ui/locked_model.png";
    private TaskCompletionSource<string?>? _localCommitResult;

    public override IEnumerable<LocString> GameInfoOptions => [];

    protected override IReadOnlyList<EventOption> GenerateInitialOptions() =>
    [
        new EventOption(this, OpenWorkbench, InitialOptionKey("OPEN_WORKBENCH"),
            disableOnChosen: false).ThatWontSaveToChoiceHistory(),
        // The overlay invokes this option through EventSynchronizer. Keeping the commit inside an event-option task
        // makes the base game broadcast the owning player and await all peers before the room-exit checksum.
        new EventOption(this, SynchronizeCommittedChanges, InitialOptionKey("APPLY_CHANGES"),
            disableOnChosen: false).ThatWontSaveToChoiceHistory()
    ];

    protected override void SetInitialEventState(bool isPreFinished)
    {
        if (Owner is { RunState: RunState suppressedRun } && ChimeraCompatibility.IsSuppressed(suppressedRun))
            Complete();
        else if (isPreFinished || Owner is { RunState: RunState run } owner
            && TinkeringStateStore.GetPlayerRun(run, owner).CompletedTrainingActs
                .Contains(run.CurrentActIndex))
            Complete();
        else base.SetInitialEventState(false);
    }

    public override IEnumerable<string> GetAssetPaths(IRunState runState) =>
        ["res://scenes/events/default_event_layout.tscn", PortraitPath, LockIconPath,
            "res://images/packed/sprite_fonts/ironclad_energy_icon.png",
            "res://images/packed/sprite_fonts/silent_energy_icon.png",
            "res://images/packed/sprite_fonts/defect_energy_icon.png",
            "res://images/packed/sprite_fonts/necrobinder_energy_icon.png",
            "res://images/packed/sprite_fonts/regent_energy_icon.png",
            "res://images/packed/sprite_fonts/colorless_energy_icon.png",
            "res://images/packed/sprite_fonts/star_icon.png",
            ..NPreviewCardHolder.AssetPaths];

    public override Task AfterEventStarted()
    {
        Log.Info($"[CardTinkering] Training event started (finished={IsFinished}, node={Node is not null}).");
        if (Owner is { RunState: RunState run } && ChimeraCompatibility.IsSuppressed(run))
        {
            if (!IsFinished) Complete();
            MarkRoomPreFinishedIfReady();
            return Task.CompletedTask;
        }
        if (IsFinished)
        {
            MarkRoomPreFinishedIfReady();
            return Task.CompletedTask;
        }
        TrainingEventUi.Attach(this);
        return Task.CompletedTask;
    }

    private Task OpenWorkbench()
    {
        Log.Info("[CardTinkering] Open-workbench fallback option selected.");
        if (Owner is { RunState: RunState run } && ChimeraCompatibility.IsSuppressed(run))
        {
            if (!IsFinished) Complete();
            MarkRoomPreFinishedIfReady();
            return Task.CompletedTask;
        }
        if (Owner is { } owner && LocalContext.IsMe(owner)) TrainingEventUi.Attach(this);
        return Task.CompletedTask;
    }

    internal Task<string?> SubmitCommittedChanges()
    {
        if (Owner is { RunState: RunState run } && ChimeraCompatibility.IsSuppressed(run))
            return Task.FromResult<string?>("Card Tinkering is disabled in a Chimera run.");
        if (Owner is null || !LocalContext.IsMe(Owner))
            return Task.FromResult<string?>("Only the local owner can submit Training Room changes.");
        if (_localCommitResult is { Task.IsCompleted: false }) return _localCommitResult.Task;
        _localCommitResult = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { RunManager.Instance.EventSynchronizer.ChooseLocalOption(1); }
        catch (Exception exception) { _localCommitResult.TrySetResult(exception.Message); }
        return _localCommitResult.Task;
    }

    private async Task SynchronizeCommittedChanges()
    {
        string? failure = null;
        try
        {
            var owner = Owner ?? throw new InvalidOperationException("The Training Room has no owning player.");
            var run = owner.RunState as RunState
                      ?? throw new InvalidOperationException("The Training Room owner is not attached to a run.");
            if (ChimeraCompatibility.IsSuppressed(run))
            {
                Complete();
                MarkRoomPreFinishedIfReady();
                return;
            }
            if (RunManager.Instance.NetService.Type.IsMultiplayer())
            {
                var synchronizer = RunManager.Instance.PlayerChoiceSynchronizer;
                var isLocalOwner = LocalContext.IsMe(owner);
                var countChoiceId = synchronizer.ReserveChoiceId(owner);
                var localDeck = isLocalOwner ? owner.Deck.Cards.ToArray() : null;
                var cardCount = localDeck?.Length ?? -1;
                if (isLocalOwner)
                    synchronizer.SyncLocalChoice(owner, countChoiceId,
                        PlayerChoiceResult.FromIndex(cardCount));
                else
                    cardCount = (await synchronizer.WaitForRemoteChoice(owner, countChoiceId)).AsIndex();
                if (cardCount is < 1 or > 1000)
                    throw new InvalidDataException($"Received an invalid Training Room deck size: {cardCount}.");

                var replacementCards = isLocalOwner ? null : new List<CardModel>(cardCount);
                for (var offset = 0; offset < cardCount; offset += DeckSyncChunkSize)
                {
                    var cardChoiceId = synchronizer.ReserveChoiceId(owner);
                    if (isLocalOwner)
                    {
                        var chunk = localDeck!.Skip(offset).Take(DeckSyncChunkSize).ToArray();
                        synchronizer.SyncLocalChoice(owner, cardChoiceId,
                            PlayerChoiceResult.FromMutableCards(chunk));
                    }
                    else
                    {
                        var chunk = (await synchronizer.WaitForRemoteChoice(owner, cardChoiceId))
                            .AsMutableCards().ToArray();
                        var expected = Math.Min(DeckSyncChunkSize, cardCount - offset);
                        if (chunk.Length != expected)
                            throw new InvalidDataException(
                                $"Received {chunk.Length} Training Room cards in a {expected}-card chunk.");
                        replacementCards!.AddRange(chunk);
                    }
                }

                var runChoiceId = synchronizer.ReserveChoiceId(owner);
                PlayerChoiceResult runChoice;
                if (isLocalOwner)
                {
                    runChoice = PlayerChoiceResult.FromIndexes(PackString(TinkeringStateStore.EncodePlayerRun(
                        TinkeringStateStore.GetPlayerRun(run, owner))));
                    synchronizer.SyncLocalChoice(owner, runChoiceId, runChoice);
                }
                else
                {
                    runChoice = await synchronizer.WaitForRemoteChoice(owner, runChoiceId);
                    ReplaceRemoteDeck(run, owner, replacementCards!);
                }

                var playerState = TinkeringStateStore.DecodePlayerRun(owner.NetId,
                    UnpackString(runChoice.AsIndexes()));
                TinkeringStateStore.SetPlayerRun(run, owner, playerState);
            }

            Complete();
            MarkRoomPreFinishedIfReady();
            // SaveManager already logs and absorbs storage failures. Await it here so the host's save cannot race
            // ahead of the synchronized deck/run-state application, but do not turn a completed network commit into
            // a retryable UI error merely because persistent storage is unavailable.
            await SaveManager.Instance.SaveRun(run.CurrentRoom);
            Log.Info($"[CardTinkering] Synchronized Training Room commit for player {owner.NetId} "+
                     $"({owner.Deck.Cards.Count} cards, multiplayer={RunManager.Instance.NetService.Type.IsMultiplayer()}).");
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            Log.Error($"[CardTinkering] Failed to synchronize Training Room changes: {exception}");
        }
        finally
        {
            if (Owner is { } owner && LocalContext.IsMe(owner))
                _localCommitResult?.TrySetResult(failure);
        }
    }

    private static void ReplaceRemoteDeck(RunState run, MegaCrit.Sts2.Core.Entities.Players.Player owner,
        IReadOnlyList<CardModel> replacementCards)
    {
        if (replacementCards.Count is < 1 or > 1000)
            throw new InvalidDataException($"Received an invalid Training Room deck size: {replacementCards.Count}.");
        foreach (var card in owner.Deck.Cards.ToArray())
        {
            card.RemoveFromCurrentPile();
            run.RemoveCard(card);
        }
        foreach (var card in replacementCards)
        {
            if (card.Pile is not null) card.RemoveFromCurrentPile();
            run.AddCard(card, owner);
            owner.Deck.AddInternal(card);
        }
    }

    private static List<int> PackString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var packed = new List<int>(1 + (bytes.Length + 3) / 4) { bytes.Length };
        for (var index = 0; index < bytes.Length; index += 4)
        {
            var word = 0;
            for (var offset = 0; offset < 4 && index + offset < bytes.Length; offset++)
                word |= bytes[index + offset] << (offset * 8);
            packed.Add(word);
        }
        return packed;
    }

    private static string UnpackString(IReadOnlyList<int> packed)
    {
        if (packed.Count == 0 || packed[0] is < 0 or > 8_000_000)
            throw new InvalidDataException("Received an invalid Training Room run-state payload length.");
        var length = packed[0];
        if (packed.Count != 1 + (length + 3) / 4)
            throw new InvalidDataException("Received a truncated Training Room run-state payload.");
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
            bytes[index] = (byte)(packed[1 + index / 4] >> ((index % 4) * 8));
        return Encoding.UTF8.GetString(bytes);
    }

    private static void MarkRoomPreFinishedIfReady()
    {
        var manager = RunManager.Instance;
        if (manager.DebugOnlyGetState()?.CurrentRoom is not EventRoom room) return;
        if (manager.EventSynchronizer.Events.All(model => model.IsFinished)) room.MarkPreFinished();
    }

    internal void Complete() => SetEventFinished(
        new LocString("events", Id.Entry + ".pages.DONE.description"));
}

internal static class TrainingEventUi
{
    private sealed class RetryRequest
    {
        internal NEventRoom? Room;
        internal RunState? Run;
    }

    private static readonly ConditionalWeakTable<CardTinkeringEvent, RetryRequest> PendingRetries = new();

    internal static void Attach(CardTinkeringEvent training)
    {
        if (training.Owner?.RunState is RunState suppressedRun
            && ChimeraCompatibility.IsSuppressed(suppressedRun)) return;
        if (training.IsFinished) return;
        if (training.Owner is null || !LocalContext.IsMe(training.Owner)) return;
        Node? parent = training.Node;
        while (parent is not null && parent is not NEventRoom) parent = parent.GetParent();
        if (parent is not NEventRoom room)
        {
            QueueRetry(training, null, training.Owner.RunState as RunState);
            return;
        }
        if (NRun.Instance?.GlobalUi.GetChildren().Any(child => child is TrainingRoomOverlay) == true) return;
        var run = RunManager.Instance.DebugOnlyGetState();
        if (run is null)
        {
            QueueRetry(training, room, null);
            return;
        }
        Attach(room, training, run);
    }

    internal static void Attach(NEventRoom room, CardTinkeringEvent training, RunState run)
    {
        if (ChimeraCompatibility.IsSuppressed(run)) return;
        if (TryAttach(room, training, run)) return;
        QueueRetry(training, room, run);
    }

    private static bool TryAttach(NEventRoom room, CardTinkeringEvent training, RunState run)
    {
        if (ChimeraCompatibility.IsSuppressed(run)) return true;
        var globalUi = NRun.Instance?.GlobalUi;
        if (training.IsFinished || training.Owner is not { } owner || !LocalContext.IsMe(owner)) return true;
        if (globalUi is null || !GodotObject.IsInstanceValid(room)) return false;
        if (globalUi.GetChildren().Any(child => child is TrainingRoomOverlay)) return true;
        try
        {
            var overlay = new TrainingRoomOverlay(
                new TrainingSession(run, owner, run.CurrentActIndex), training);
            globalUi.AddChild(overlay);
            if (GodotObject.IsInstanceValid(globalUi.TargetManager))
                globalUi.MoveChild(overlay, globalUi.TargetManager.GetIndex());
            room.TreeExiting += () =>
            {
                if (GodotObject.IsInstanceValid(overlay) && !overlay.IsQueuedForDeletion()) overlay.QueueFree();
            };
            overlay.Activate();
            PendingRetries.Remove(training);
            Log.Info($"[CardTinkering] Attached editor UI for player {owner.NetId} below the native target layer "
                     + $"in Act {run.CurrentActIndex + 1}.");
            return true;
        }
        catch (Exception exception)
        {
            Log.Warn($"[CardTinkering] Editor UI attachment is not ready yet; it will retry: {exception.Message}");
            return false;
        }
    }

    private static void QueueRetry(CardTinkeringEvent training, NEventRoom? room, RunState? run)
    {
        if (ChimeraCompatibility.IsSuppressed(run ?? training.Owner?.RunState as RunState)) return;
        if (training.IsFinished || training.Owner is null || !LocalContext.IsMe(training.Owner)) return;
        if (PendingRetries.TryGetValue(training, out var existing))
        {
            existing.Room ??= room;
            existing.Run ??= run;
            return;
        }
        var request = new RetryRequest { Room = room, Run = run };
        PendingRetries.Add(training, request);
        TaskHelper.RunSafely(RetryAttach(training, request));
    }

    private static async Task RetryAttach(CardTinkeringEvent training, RetryRequest request)
    {
        // Multiplayer room scenes can become visible several frames later on one peer. Retry for ten seconds rather
        // than leaving that player with only the underlying event buttons and an event the party cannot complete.
        for (var attempt = 0; attempt < 600; attempt++)
        {
            if (ChimeraCompatibility.IsSuppressed(request.Run ?? training.Owner?.RunState as RunState)) break;
            if (training.IsFinished || training.Owner is null || !LocalContext.IsMe(training.Owner)) break;
            request.Run ??= training.Owner.RunState as RunState
                            ?? RunManager.Instance.DebugOnlyGetState();
            if (request.Room is null || !GodotObject.IsInstanceValid(request.Room))
            {
                Node? parent = training.Node;
                while (parent is not null && parent is not NEventRoom) parent = parent.GetParent();
                request.Room = parent as NEventRoom;
            }
            if (request.Room is not null && request.Run is not null
                && TryAttach(request.Room, training, request.Run)) return;

            if (Engine.GetMainLoop() is SceneTree tree)
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            else
                await Task.Delay(16);
        }
        PendingRetries.Remove(training);
        if (!training.IsFinished)
            Log.Error($"[CardTinkering] Could not attach the Training Room UI for local player "
                      + $"{training.Owner?.NetId} after 600 frames.");
    }

    internal static void AttachAnytime(RunState run)
    {
        var globalUi = NRun.Instance?.GlobalUi;
        if (!CardTinkeringFeatureGate.CanEditAnytime(run) || globalUi is null
            || globalUi.GetChildren().Any(child => child is TrainingRoomOverlay)) return;
        try
        {
            var owner = LocalContext.GetMe(run);
            if (owner is null) return;
            var overlay = new TrainingRoomOverlay(
                new TrainingSession(run, owner, run.CurrentActIndex,
                    completesTrainingAct: false));
            globalUi.AddChild(overlay);
            if (GodotObject.IsInstanceValid(globalUi.TargetManager))
                globalUi.MoveChild(overlay, globalUi.TargetManager.GetIndex());
            overlay.Activate();
            Log.Info("[CardTinkering] Opened the card workbench from the out-of-combat deck screen.");
        }
        catch (Exception exception)
        {
            Log.Error($"[CardTinkering] Failed to attach the anytime editor UI: {exception}");
        }
    }

#if FREEFORM_API
    internal static void AttachFreeform(RunState run)
    {
        var globalUi = NRun.Instance?.GlobalUi;
        if (!CardTinkeringFeatureGate.CanFreeformEdit(run) || globalUi is null
            || globalUi.GetChildren().Any(child => child is FreeformEditorOverlay)) return;
        try
        {
            var overlay = new FreeformEditorOverlay(run);
            globalUi.AddChild(overlay);
            if (GodotObject.IsInstanceValid(globalUi.TargetManager))
                globalUi.MoveChild(overlay, globalUi.TargetManager.GetIndex());
            overlay.Activate();
            Log.Info("[CardTinkering] Opened the freeform card editor.");
        }
        catch (Exception exception)
        {
            Log.Error($"[CardTinkering] Failed to attach the freeform editor UI: {exception}");
        }
    }
#endif
}

internal static class TrainingEventLocalization
{
    internal static void Install()
    {
        var manager = LocManager.Instance;
        var chinese = manager.Language is "zhs" or "zht";
        var id = ModelDb.Event<CardTinkeringEvent>().Id.Entry;
        manager.GetTable("events").MergeWith(new Dictionary<string, string>
        {
            [$"{id}.title"] = chinese ? "训练室" : "Training Room",
            [$"{id}.pages.INITIAL.description"] = chinese
                ? "这里可以重新排列自动安东尼学卡牌的效果组件。"
                : "Rearrange the effect components of Auto-Anthonyology cards here.",
            [$"{id}.pages.INITIAL.options.OPEN_WORKBENCH.title"] = chinese
                ? "打开卡牌工匠台"
                : "Open the card workbench",
            [$"{id}.pages.INITIAL.options.OPEN_WORKBENCH.description"] = chinese
                ? "进入组件编辑界面。完成编辑并保存后可以离开；不合法卡牌会要求确认。"
                : "Open the component editor. Save when finished to leave; invalid cards require confirmation.",
            [$"{id}.pages.INITIAL.options.APPLY_CHANGES.title"] = chinese ? "应用调整" : "Apply changes",
            [$"{id}.pages.INITIAL.options.APPLY_CHANGES.description"] = chinese
                ? "应用当前调整并结束训练。"
                : "Apply the current changes and finish training.",
            [$"{id}.pages.DONE.description"] = chinese
                ? "调整已经保存。你可以继续攀登了。"
                : "Your changes are saved. You may continue your ascent."
        });
    }
}
