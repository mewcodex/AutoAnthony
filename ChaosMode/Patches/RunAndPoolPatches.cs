using Godot;
using HarmonyLib;
using ChaosCardGenerator;
using AutoAnthony.Multiplayer;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.ValueProps;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace AutoAnthony.Patches;

[HarmonyPatch(typeof(StartRunLobby), "BeginRunForAllPlayers")]
internal static class MultiplayerGenerationModePatch
{
    private sealed class HostPreparation(
        string preparationId,
        string seed,
        List<ModifierModel> modifiers,
        ChaosPoolSnapshotModifier marker,
        IEnumerable<ulong> expectedPeers)
    {
        internal string PreparationId { get; } = preparationId;
        internal string Seed { get; } = seed;
        internal List<ModifierModel> Modifiers { get; } = modifiers;
        internal ChaosPoolSnapshotModifier Marker { get; } = marker;
        internal HashSet<ulong> ExpectedPeers { get; } = expectedPeers.ToHashSet();
        internal Dictionary<ulong, string> PeerFingerprints { get; } = new();
        internal string HostFingerprint { get; set; } = string.Empty;
        internal ChaosGenerationProgressOverlay? Progress { get; set; }
        internal bool HostComplete { get; set; }
        internal bool Starting { get; set; }
        internal bool Cancelled { get; set; }
    }

    private sealed class ClientPreparation(string preparationId)
    {
        internal string PreparationId { get; } = preparationId;
        internal string Fingerprint { get; set; } = string.Empty;
        internal ChaosGenerationProgressOverlay? Progress { get; set; }
        internal bool Complete { get; set; }
        internal bool Cancelled { get; set; }
    }

    private sealed class LobbyMessageHandlers(StartRunLobby lobby)
    {
        internal MessageHandlerDelegate<ChaosPoolPreparationMessage> Preparation { get; } =
            (message, senderId) => ReceivePreparation(lobby, message, senderId);

        internal MessageHandlerDelegate<ChaosPoolPreparationAckMessage> Acknowledgement { get; } =
            (message, senderId) => ReceiveAcknowledgement(lobby, message, senderId);

        internal MessageHandlerDelegate<ChaosPoolPreparationCancelMessage> Cancellation { get; } =
            (message, senderId) => ReceiveCancellation(lobby, message, senderId);
    }

    private static readonly System.Reflection.MethodInfo BeginRunMethod =
        AccessTools.Method(typeof(StartRunLobby), "BeginRunForAllPlayers",
            [typeof(string), typeof(List<ModifierModel>)]);
    private static readonly ConditionalWeakTable<StartRunLobby, HostPreparation> HostPreparations = new();
    private static readonly ConditionalWeakTable<StartRunLobby, ClientPreparation> ClientPreparations = new();
    private static readonly ConditionalWeakTable<StartRunLobby, LobbyMessageHandlers> RegisteredHandlers = new();
    private static readonly object PreparationGate = new();
    private static int _callingOriginal;

    private static bool Prefix(StartRunLobby __instance, string seed, List<ModifierModel> modifiers)
    {
        if (!__instance.NetService.Type.IsMultiplayer() || Volatile.Read(ref _callingOriginal) > 0) return true;
        RegisterLobbyHandlers(__instance);

        // Disabled hosts only need to synchronize the mode switch and can begin immediately.
        if (!ChaosModSettings.Enabled)
        {
            AddGenerationMarker(modifiers, poolSnapshot: null, generationFingerprint: null, enabled: false,
                ultimateChaos: ChaosModSettings.UltimateChaos,
                replaceStartingCards: ChaosModSettings.ReplaceStartingCards,
                numericBalanceOptimization: ChaosModSettings.NumericBalanceOptimization,
                numericRandomMode: ChaosModSettings.NumericRandomMode,
                preserveOriginalCards: ChaosModSettings.PreserveOriginalCards,
                randomCardArt: ChaosModSettings.RandomCardArt);
            return true;
        }

        HostPreparation? matchingPreparation = null;
        var releaseMatchingPreparation = false;
        lock (PreparationGate)
        {
            if (HostPreparations.TryGetValue(__instance, out var existing))
            {
                if (existing.Seed == seed && ReferenceEquals(existing.Modifiers, modifiers))
                {
                    // The acknowledgement handler normally releases the run directly. This path also covers a
                    // roster change causing vanilla to re-check readiness after every remaining peer completed.
                    matchingPreparation = existing;
                    releaseMatchingPreparation = CanStart(existing);
                }
                else
                {
                    existing.Cancelled = true;
                    existing.Progress?.Dispose();
                    HostPreparations.Remove(__instance);
                }
            }
        }
        if (matchingPreparation is not null)
        {
            if (releaseMatchingPreparation)
                TryReleasePreparedRun(__instance, matchingPreparation.PreparationId);
            return false;
        }

        var provisionalCharacters = ProvisionalCharacters(__instance);
        if (provisionalCharacters.Length == 0
            && __instance.Players.All(player => player.character is not RandomCharacter))
        {
            // Preserve the existing behavior for lobbies made entirely of unsupported modded characters.
            AddGenerationMarker(modifiers, poolSnapshot: null, generationFingerprint: null, enabled: true,
                ChaosModSettings.UltimateChaos, ChaosModSettings.ReplaceStartingCards,
                ChaosModSettings.NumericBalanceOptimization, ChaosModSettings.NumericRandomMode,
                ChaosModSettings.PreserveOriginalCards, ChaosModSettings.RandomCardArt);
            return true;
        }
        if (provisionalCharacters.Length == 0) provisionalCharacters = [GeneratedCharacter.Ironclad];

        var preparationId = Guid.NewGuid().ToString("N");
        AddGenerationMarker(modifiers, poolSnapshot: null, generationFingerprint: null, enabled: true,
            ChaosModSettings.UltimateChaos, ChaosModSettings.ReplaceStartingCards,
            ChaosModSettings.NumericBalanceOptimization, ChaosModSettings.NumericRandomMode,
            ChaosModSettings.PreserveOriginalCards, ChaosModSettings.RandomCardArt);
        var marker = modifiers.OfType<ChaosPoolSnapshotModifier>()
            .Last(snapshot => snapshot.MultiplayerGenerationModeSpecified);
        var state = new HostPreparation(preparationId, seed, modifiers, marker,
            __instance.Players.Where(player => player.id != __instance.NetService.NetId)
                .Select(player => player.id));
        lock (PreparationGate)
        {
            HostPreparations.Remove(__instance);
            HostPreparations.Add(__instance, state);
        }

        var request = new ChaosPoolPreparationMessage
        {
            PreparationId = preparationId,
            Seed = seed,
            CharacterMask = EncodeCharacters(provisionalCharacters),
            UltimateChaos = ChaosModSettings.UltimateChaos,
            ReplaceStartingCards = ChaosModSettings.ReplaceStartingCards,
            NumericBalanceOptimization = ChaosModSettings.NumericBalanceOptimization,
            NumericRandomMode = ChaosModSettings.NumericRandomMode,
            PreserveOriginalCards = ChaosModSettings.PreserveOriginalCards,
            RandomCardArt = ChaosModSettings.RandomCardArt
        };
        __instance.NetService.SendMessage(request);
        Log.Info($"[AutoAnthony] Started concurrent multiplayer pool preparation {preparationId}: "
                 + $"peers={state.ExpectedPeers.Count}, seed={seed}.");
        TaskHelper.RunSafely(PrepareHostAsync(__instance, state, provisionalCharacters, request));
        return false;
    }

    private static async Task PrepareHostAsync(StartRunLobby lobby, HostPreparation state,
        GeneratedCharacter[] provisionalCharacters, ChaosPoolPreparationMessage request)
    {
        ChaosGenerationProgressOverlay? generatedProgress = null;
        try
        {
            generatedProgress = await ChaosRunDefinitions.ActivateAsync(provisionalCharacters, request.Seed,
                request.UltimateChaos, request.ReplaceStartingCards, request.NumericBalanceOptimization,
                request.NumericRandomMode, request.PreserveOriginalCards, request.RandomCardArt);
            var generationFingerprint = ChaosPoolSnapshot.MultiplayerGameplayFingerprint(
                ChaosRunDefinitions.GetAllCards());
            lock (PreparationGate)
            {
                if (!HostPreparations.TryGetValue(lobby, out var current)
                    || !ReferenceEquals(current, state) || state.Cancelled)
                {
                    generatedProgress?.Dispose();
                    generatedProgress = null;
                    ChaosRunDefinitions.DeactivateRun();
                    return;
                }
                state.HostFingerprint = generationFingerprint;
                state.HostComplete = true;
                state.Marker.MultiplayerGenerationFingerprint = generationFingerprint;
                state.Progress = generatedProgress;
                generatedProgress = null;
            }
            state.Progress?.ShowWaitingForPlayers();
            Log.Info($"[AutoAnthony] Host completed concurrent multiplayer pool preparation "
                     + $"{state.PreparationId}: fingerprint={generationFingerprint}.");
            TryReleasePreparedRun(lobby, state.PreparationId);
        }
        catch (Exception exception)
        {
            Log.Error($"[AutoAnthony] Host multiplayer pool preparation failed: {exception}");
            CancelHostPreparation(lobby, state.PreparationId,
                "host pool generation failed", broadcast: true);
        }
        finally
        {
            generatedProgress?.Dispose();
        }
    }

    private static void ReceivePreparation(StartRunLobby lobby, ChaosPoolPreparationMessage message,
        ulong senderId)
    {
        if (lobby.NetService.Type != NetGameType.Client) return;
        ClientPreparation state;
        lock (PreparationGate)
        {
            if (ClientPreparations.TryGetValue(lobby, out var existing))
            {
                if (existing.PreparationId == message.PreparationId)
                {
                    if (existing.Complete)
                        SendAcknowledgement(lobby, existing.PreparationId, success: true,
                            existing.Fingerprint, string.Empty);
                    return;
                }
                existing.Cancelled = true;
                existing.Progress?.Dispose();
                ClientPreparations.Remove(lobby);
            }
            state = new ClientPreparation(message.PreparationId);
            ClientPreparations.Add(lobby, state);
        }

        Log.Info($"[AutoAnthony] Client received concurrent multiplayer pool preparation "
                 + $"{message.PreparationId} from {senderId}.");
        TaskHelper.RunSafely(PrepareClientAsync(lobby, state, message));
    }

    private static async Task PrepareClientAsync(StartRunLobby lobby, ClientPreparation state,
        ChaosPoolPreparationMessage request)
    {
        ChaosGenerationProgressOverlay? generatedProgress = null;
        try
        {
            var characters = DecodeCharacters(request.CharacterMask);
            generatedProgress = await ChaosRunDefinitions.ActivateAsync(characters, request.Seed,
                request.UltimateChaos, request.ReplaceStartingCards, request.NumericBalanceOptimization,
                request.NumericRandomMode, request.PreserveOriginalCards, request.RandomCardArt);
            var fingerprint = ChaosPoolSnapshot.MultiplayerGameplayFingerprint(
                ChaosRunDefinitions.GetAllCards());
            lock (PreparationGate)
            {
                if (!ClientPreparations.TryGetValue(lobby, out var current)
                    || !ReferenceEquals(current, state) || state.Cancelled)
                {
                    generatedProgress?.Dispose();
                    generatedProgress = null;
                    ChaosRunDefinitions.DeactivateRun();
                    return;
                }
                state.Fingerprint = fingerprint;
                state.Complete = true;
                state.Progress = generatedProgress;
                generatedProgress = null;
            }
            state.Progress?.ShowWaitingForPlayers();
            Log.Info($"[AutoAnthony] Client completed concurrent multiplayer pool preparation "
                     + $"{state.PreparationId}: fingerprint={fingerprint}.");
            SendAcknowledgement(lobby, state.PreparationId, success: true, fingerprint, string.Empty);
        }
        catch (Exception exception)
        {
            Log.Error($"[AutoAnthony] Client multiplayer pool preparation failed: {exception}");
            var failure = $"{exception.GetType().Name}: {exception.Message}";
            if (failure.Length > 512) failure = failure[..512];
            SendAcknowledgement(lobby, state.PreparationId, success: false, string.Empty, failure);
            CancelClientPreparation(lobby, state.PreparationId, restoreLobby: true);
        }
        finally
        {
            generatedProgress?.Dispose();
        }
    }

    private static void SendAcknowledgement(StartRunLobby lobby, string preparationId, bool success,
        string fingerprint, string failure)
    {
        lobby.NetService.SendMessage(new ChaosPoolPreparationAckMessage
        {
            PreparationId = preparationId,
            Success = success,
            GameplayFingerprint = fingerprint,
            Failure = failure
        });
    }

    private static void ReceiveAcknowledgement(StartRunLobby lobby, ChaosPoolPreparationAckMessage message,
        ulong senderId)
    {
        if (lobby.NetService.Type != NetGameType.Host) return;
        var cancel = false;
        lock (PreparationGate)
        {
            if (!HostPreparations.TryGetValue(lobby, out var state)
                || state.PreparationId != message.PreparationId
                || !state.ExpectedPeers.Contains(senderId)) return;
            if (!message.Success)
            {
                Log.Error($"[AutoAnthony] Peer {senderId} failed multiplayer pool preparation "
                          + $"{message.PreparationId}: {message.Failure}");
                cancel = true;
            }
            else
            {
                state.PeerFingerprints[senderId] = message.GameplayFingerprint;
                Log.Info($"[AutoAnthony] Peer {senderId} acknowledged multiplayer pool preparation "
                         + $"{message.PreparationId} ({state.PeerFingerprints.Count}/{state.ExpectedPeers.Count}).");
            }
        }
        if (cancel)
            CancelHostPreparation(lobby, message.PreparationId, $"peer {senderId} generation failed",
                broadcast: true);
        else
            TryReleasePreparedRun(lobby, message.PreparationId);
    }

    private static void ReceiveCancellation(StartRunLobby lobby, ChaosPoolPreparationCancelMessage message,
        ulong senderId)
    {
        if (lobby.NetService.Type != NetGameType.Client) return;
        Log.Warn($"[AutoAnthony] Host {senderId} cancelled multiplayer pool preparation "
                 + $"{message.PreparationId}.");
        CancelClientPreparation(lobby, message.PreparationId, restoreLobby: true);
    }

    private static void TryReleasePreparedRun(StartRunLobby lobby, string preparationId)
    {
        HostPreparation? state;
        string? mismatch = null;
        lock (PreparationGate)
        {
            if (!HostPreparations.TryGetValue(lobby, out state)
                || state.PreparationId != preparationId || state.Starting || state.Cancelled
                || !CanStart(state)) return;
            mismatch = state.ExpectedPeers.Select(peer => (Peer: peer,
                    Fingerprint: state.PeerFingerprints.GetValueOrDefault(peer)))
                .Where(entry => !string.Equals(entry.Fingerprint, state.HostFingerprint,
                    StringComparison.Ordinal))
                .Select(entry => $"{entry.Peer}={entry.Fingerprint}")
                .FirstOrDefault();
        }

        if (mismatch is not null)
        {
            CancelHostPreparation(lobby, preparationId,
                $"generated pool fingerprint mismatch ({mismatch}, host={state!.HostFingerprint})",
                broadcast: true);
            return;
        }
        if (!lobby.IsAboutToBeginGame())
        {
            CancelHostPreparation(lobby, preparationId, "lobby readiness changed", broadcast: true);
            return;
        }

        lock (PreparationGate)
        {
            if (!HostPreparations.TryGetValue(lobby, out var current)
                || !ReferenceEquals(current, state) || state.Starting || state.Cancelled
                || !CanStart(state)) return;
            state.Starting = true;
            state.Marker.MultiplayerGenerationFingerprint = state.HostFingerprint;
        }

        Log.Info($"[AutoAnthony] Every peer completed matching pool preparation {preparationId}; "
                 + "broadcasting the vanilla begin-run message now.");
        InvokeOriginal(lobby, state!.Seed, state.Modifiers);
    }

    private static bool CanStart(HostPreparation state) =>
        state.HostComplete && state.ExpectedPeers.All(state.PeerFingerprints.ContainsKey);

    private static void CancelHostPreparation(StartRunLobby lobby, string preparationId, string reason,
        bool broadcast)
    {
        HostPreparation? state;
        lock (PreparationGate)
        {
            if (!HostPreparations.TryGetValue(lobby, out state)
                || state.PreparationId != preparationId || state.Starting) return;
            state.Cancelled = true;
            HostPreparations.Remove(lobby);
        }
        state.Progress?.Dispose();
        if (state.HostComplete) ChaosRunDefinitions.DeactivateRun();
        if (broadcast && lobby.NetService.IsConnected)
            lobby.NetService.SendMessage(new ChaosPoolPreparationCancelMessage
                { PreparationId = preparationId });
        Log.Warn($"[AutoAnthony] Cancelled multiplayer pool preparation {preparationId}: {reason}.");
        RestoreCancelledLobby(lobby);
    }

    private static void CancelClientPreparation(StartRunLobby lobby, string preparationId, bool restoreLobby)
    {
        ClientPreparation? state;
        lock (PreparationGate)
        {
            if (!ClientPreparations.TryGetValue(lobby, out state)
                || state.PreparationId != preparationId) return;
            state.Cancelled = true;
            ClientPreparations.Remove(lobby);
        }
        state.Progress?.Dispose();
        if (state.Complete) ChaosRunDefinitions.DeactivateRun();
        if (restoreLobby) RestoreCancelledLobby(lobby);
    }

    private static void InvokeOriginal(StartRunLobby lobby, string seed, List<ModifierModel> modifiers)
    {
        Interlocked.Increment(ref _callingOriginal);
        try
        {
            BeginRunMethod.Invoke(lobby, [seed, modifiers]);
        }
        finally
        {
            Interlocked.Decrement(ref _callingOriginal);
        }
    }

    internal static ChaosGenerationProgressOverlay? TakePreparedProgress(StartRunLobby lobby)
    {
        lock (PreparationGate)
        {
            if (HostPreparations.TryGetValue(lobby, out var host))
            {
                HostPreparations.Remove(lobby);
                var progress = host.Progress;
                host.Progress = null;
                return progress;
            }
            if (!ClientPreparations.TryGetValue(lobby, out var client)) return null;
            ClientPreparations.Remove(lobby);
            var clientProgress = client.Progress;
            client.Progress = null;
            return clientProgress;
        }
    }

    internal static void RegisterLobbyHandlers(StartRunLobby lobby)
    {
        lock (PreparationGate)
        {
            if (RegisteredHandlers.TryGetValue(lobby, out _)) return;
            var handlers = new LobbyMessageHandlers(lobby);
            lobby.NetService.RegisterMessageHandler(handlers.Preparation);
            lobby.NetService.RegisterMessageHandler(handlers.Acknowledgement);
            lobby.NetService.RegisterMessageHandler(handlers.Cancellation);
            RegisteredHandlers.Add(lobby, handlers);
        }
    }

    internal static void CleanUpLobby(StartRunLobby lobby)
    {
        lock (PreparationGate)
        {
            if (RegisteredHandlers.TryGetValue(lobby, out var handlers))
            {
                lobby.NetService.UnregisterMessageHandler(handlers.Preparation);
                lobby.NetService.UnregisterMessageHandler(handlers.Acknowledgement);
                lobby.NetService.UnregisterMessageHandler(handlers.Cancellation);
                RegisteredHandlers.Remove(lobby);
            }
            if (HostPreparations.TryGetValue(lobby, out var host))
            {
                host.Cancelled = true;
                host.Progress?.Dispose();
                HostPreparations.Remove(lobby);
            }
            if (ClientPreparations.TryGetValue(lobby, out var client))
            {
                client.Cancelled = true;
                client.Progress?.Dispose();
                ClientPreparations.Remove(lobby);
            }
        }
    }

    internal static void OnRemoteReadyChanged(StartRunLobby lobby, bool ready, ulong senderId)
    {
        if (ready || lobby.NetService.Type != NetGameType.Host) return;
        string? preparationId;
        lock (PreparationGate)
            preparationId = HostPreparations.TryGetValue(lobby, out var state) && !state.Starting
                ? state.PreparationId
                : null;
        if (preparationId is not null)
            CancelHostPreparation(lobby, preparationId, $"peer {senderId} became unready", broadcast: true);
    }

    internal static void OnRemoteDisconnected(StartRunLobby lobby, ulong playerId)
    {
        lock (PreparationGate)
        {
            if (!HostPreparations.TryGetValue(lobby, out var state) || state.Starting) return;
            state.ExpectedPeers.Remove(playerId);
            state.PeerFingerprints.Remove(playerId);
        }
    }

    internal static void OnPlayerJoined(StartRunLobby lobby)
    {
        if (lobby.NetService.Type != NetGameType.Host) return;
        string? preparationId;
        lock (PreparationGate)
            preparationId = HostPreparations.TryGetValue(lobby, out var state) && !state.Starting
                ? state.PreparationId
                : null;
        if (preparationId is not null)
            CancelHostPreparation(lobby, preparationId, "lobby roster changed", broadcast: true);
    }

    private static GeneratedCharacter[] ProvisionalCharacters(StartRunLobby lobby) =>
        lobby.Players.Select(player => ChaosCharacterMapping.From(player.character))
            .Where(character => character.HasValue).Select(character => character!.Value)
            .Distinct().OrderBy(character => character).ToArray();

    private static int EncodeCharacters(IEnumerable<GeneratedCharacter> characters) =>
        characters.Aggregate(0, (mask, character) => mask | 1 << (int)character);

    private static GeneratedCharacter[] DecodeCharacters(int mask) =>
        Enum.GetValues<GeneratedCharacter>()
            .Where(character => character != GeneratedCharacter.Colorless && (mask & 1 << (int)character) != 0)
            .ToArray();

    internal static void AuditPreparationProtocol()
    {
        var request = new ChaosPoolPreparationMessage
        {
            PreparationId = "PREPARATION_AUDIT",
            Seed = "SEED_AUDIT",
            CharacterMask = EncodeCharacters([GeneratedCharacter.Ironclad, GeneratedCharacter.Regent]),
            UltimateChaos = true,
            ReplaceStartingCards = false,
            NumericBalanceOptimization = true,
            NumericRandomMode = true,
            PreserveOriginalCards = true,
            RandomCardArt = true
        };
        var writer = new MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter { WarnOnGrow = false };
        request.Serialize(writer);
        var reader = new MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketReader();
        reader.Reset(writer.Buffer);
        var restored = new ChaosPoolPreparationMessage();
        restored.Deserialize(reader);
        if (writer.BytePosition > 256
            || restored.PreparationId != request.PreparationId
            || restored.Seed != request.Seed
            || restored.CharacterMask != request.CharacterMask
            || !restored.UltimateChaos
            || restored.ReplaceStartingCards
            || !restored.NumericBalanceOptimization
            || !restored.NumericRandomMode
            || !restored.PreserveOriginalCards
            || !restored.RandomCardArt
            || !DecodeCharacters(restored.CharacterMask)
                .SequenceEqual([GeneratedCharacter.Ironclad, GeneratedCharacter.Regent]))
            throw new InvalidOperationException(
                $"Multiplayer pool-preparation message failed round-trip validation ({writer.BytePosition} bytes).");
    }

    private static void RestoreCancelledLobby(StartRunLobby lobby)
    {
        // Character select disables Embark, Back and all character buttons before SetReady returns. Vanilla
        // expects BeginRunForAllPlayers to synchronously commit the run, but our host generation intentionally
        // yields frames. If a peer unreadies or disconnects during that window, merely returning here leaves the
        // host permanently ready with every useful control disabled. Reuse the screen's normal Unready path so
        // the lobby and its UI return to one coherent state.
        try
        {
            if (lobby.LobbyListener is NCharacterSelectScreen screen)
            {
                AccessTools.Method(typeof(NCharacterSelectScreen), "OnUnreadyPressed")?.Invoke(screen, [null]);
            }
            else if (lobby.LocalPlayer.isReady)
            {
                lobby.SetReady(ready: false);
            }
            Log.Warn("[AutoAnthony] Restored the multiplayer lobby after card-pool preparation was cancelled.");
        }
        catch (Exception exception)
        {
            Log.Error($"[AutoAnthony] Could not restore the multiplayer lobby after generation was cancelled: {exception}");
        }
    }

    private static void AddGenerationMarker(List<ModifierModel> modifiers, string? poolSnapshot,
        string? generationFingerprint, bool enabled,
        bool ultimateChaos, bool replaceStartingCards, bool numericBalanceOptimization, bool numericRandomMode,
        bool preserveOriginalCards, bool randomCardArt)
    {

        // BeginRunForAllPlayers is host-only. The marker is serialized by the vanilla lobby packet and reaches
        // StartNewMultiplayerRun on every peer before any generated pool is built.
        modifiers.RemoveAll(modifier => modifier is ChaosPoolSnapshotModifier snapshot
                                          && snapshot.MultiplayerGenerationModeSpecified);
        var marker = (ChaosPoolSnapshotModifier)ModelDb.Modifier<ChaosPoolSnapshotModifier>().ToMutable();
        marker.MultiplayerGenerationModeSpecified = true;
        marker.MultiplayerModEnabled = enabled;
        marker.MultiplayerUltimateChaos = ultimateChaos;
        marker.MultiplayerReplaceStartingCardsSpecified = true;
        marker.MultiplayerReplaceStartingCards = replaceStartingCards;
        marker.MultiplayerNumericBalanceOptimizationSpecified = true;
        marker.MultiplayerNumericBalanceOptimization = numericBalanceOptimization;
        marker.MultiplayerNumericRandomMode = numericRandomMode;
        marker.MultiplayerPreserveOriginalCards = preserveOriginalCards;
        marker.MultiplayerRandomCardArtSpecified = true;
        marker.MultiplayerRandomCardArt = randomCardArt;
        marker.MultiplayerGenerationFingerprint = generationFingerprint ?? string.Empty;
        marker.PoolSnapshot = poolSnapshot ?? string.Empty;
        modifiers.Add(marker);
        Log.Info($"[AutoAnthony] Host selected multiplayer generation mode: Enabled={marker.MultiplayerModEnabled}, UltimateChaos={marker.MultiplayerUltimateChaos}, ReplaceStartingCards={marker.MultiplayerReplaceStartingCards}, NumericBalanceOptimization={marker.MultiplayerNumericBalanceOptimization}, NumericRandom={marker.MultiplayerNumericRandomMode}, PreserveOriginal={marker.MultiplayerPreserveOriginalCards}, RandomCardArt={marker.MultiplayerRandomCardArt}, DeterministicFingerprint={!string.IsNullOrEmpty(marker.MultiplayerGenerationFingerprint)}, LegacyAuthoritativeSnapshot={!string.IsNullOrEmpty(marker.PoolSnapshot)}.");
    }
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.AddLocalHostPlayer))]
internal static class MultiplayerPreparationHostLobbyRegistrationPatch
{
    private static void Postfix(StartRunLobby __instance) =>
        MultiplayerGenerationModePatch.RegisterLobbyHandlers(__instance);
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.InitializeFromMessage))]
internal static class MultiplayerPreparationClientLobbyRegistrationPatch
{
    private static void Postfix(StartRunLobby __instance) =>
        MultiplayerGenerationModePatch.RegisterLobbyHandlers(__instance);
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.CleanUp))]
internal static class MultiplayerPreparationLobbyCleanupPatch
{
    private static void Prefix(StartRunLobby __instance) =>
        MultiplayerGenerationModePatch.CleanUpLobby(__instance);
}

[HarmonyPatch(typeof(StartRunLobby), "HandlePlayerReadyMessage")]
internal static class MultiplayerPreparationReadyChangePatch
{
    private static void Postfix(StartRunLobby __instance, LobbyPlayerSetReadyMessage message, ulong senderId) =>
        MultiplayerGenerationModePatch.OnRemoteReadyChanged(__instance, message.ready, senderId);
}

[HarmonyPatch(typeof(StartRunLobby), "OnDisconnectedFromClientAsHost")]
internal static class MultiplayerPreparationDisconnectPatch
{
    private static void Prefix(StartRunLobby __instance, ulong playerId) =>
        MultiplayerGenerationModePatch.OnRemoteDisconnected(__instance, playerId);
}

[HarmonyPatch(typeof(StartRunLobby), "HandlePlayerJoinedMessage")]
internal static class MultiplayerPreparationJoinPatch
{
    private static void Postfix(StartRunLobby __instance) =>
        MultiplayerGenerationModePatch.OnPlayerJoined(__instance);
}

/// <summary>
/// StartRunLobby transports the host snapshot through its modifier list, but the standard character-select
/// listener deliberately discards all modifiers before it calls NGame.StartNewMultiplayerRun. Capture only our
/// transient carrier immediately before the listener sees the list, then reattach it at NGame. This also keeps
/// vanilla's standard-run "modifiers list is not empty" error from firing on every AutoAnthony multiplayer run.
/// </summary>
[HarmonyPatch(typeof(StartRunLobby), "BeginRunLocally")]
internal static class MultiplayerGenerationMarkerTransportPatch
{
    private sealed class PendingMarker(ChaosPoolSnapshotModifier marker)
    {
        internal ChaosPoolSnapshotModifier Marker { get; } = marker;
    }

    private static readonly ConditionalWeakTable<StartRunLobby, PendingMarker> Pending = new();
    private static readonly object Gate = new();

    private static void Prefix(StartRunLobby __instance, ref List<ModifierModel> modifiers)
    {
        var marker = modifiers.OfType<ChaosPoolSnapshotModifier>()
            .LastOrDefault(snapshot => snapshot.MultiplayerGenerationModeSpecified);
        if (marker is null) return;

        lock (Gate)
        {
            Pending.Remove(__instance);
            Pending.Add(__instance, new PendingMarker(marker));
        }
        modifiers = modifiers.Where(modifier => modifier != marker).ToList();
        Log.Info($"[AutoAnthony] Captured the multiplayer generation carrier before the lobby listener; authoritativeSnapshot={!string.IsNullOrEmpty(marker.PoolSnapshot)}.");
    }

    internal static bool TryTake(StartRunLobby lobby, out ChaosPoolSnapshotModifier? marker)
    {
        lock (Gate)
        {
            if (!Pending.TryGetValue(lobby, out var pending))
            {
                marker = null;
                return false;
            }
            Pending.Remove(lobby);
            marker = pending.Marker;
            return true;
        }
    }
}

[HarmonyPatch(typeof(NMainMenu), "AbandonRun")]
internal static class ChaosAbandonRunCleanupPatch
{
    private static void Prefix()
    {
        Log.Info("[AutoAnthony] Abandon run cleanup started; preserving the active pool until run history serialization completes.");
    }

    private static Exception? Finalizer(Exception? __exception)
    {
        // RunManager.ToSave must still see the active generated pools while the abandoned run is written to
        // history. Clear them only after the original method has finished, including its cloud-save work, so
        // main-menu UI and later profile operations cannot observe a stale run-scoped cache.
        ChaosRunDefinitions.DeactivateRun();
        Log.Info(__exception is null
            ? "[AutoAnthony] Abandon run cleanup completed; released generated pool state."
            : $"[AutoAnthony] Abandon run cleanup released generated pool state after an error: {__exception}");
        return __exception;
    }
}

[HarmonyPatch(typeof(ModelDb), nameof(ModelDb.InitIds))]
[HarmonyAfter("ActLikeIt2")]
internal static class ChaosModelDbReadyPatch
{
    private static void Postfix()
    {
        // Library preview initialization must never inherit a user-selected mode. The exhaustive development audit
        // is opt-in and also stays deterministic; Ultimate Chaos is still applied normally for an actual run.
        using var generationMode = ChaosModSettings.OverrideGenerationMode(false);
        try
        {
            var fullAudit = string.Equals(
                System.Environment.GetEnvironmentVariable("AUTOANTHONY_STARTUP_AUDIT"), "1",
                StringComparison.Ordinal);
            DumpExternalNameCatalogIfRequested();
            LogDistributionAuditIfRequested();
            RunExternalStressAuditIfRequested();
            EnsureLibraryCardsInModelDb();
            ChaosRunDefinitions.ActivateLibraryPreview();
            OptionalActSelectionFrameworkCompatibility.LogStatus();
            if (fullAudit)
            {
                HistorySnapshotOptimizer.AuditIsolation();
                ChaosRunDefinitions.AuditPortraitSourceCompatibility();
                AuditAncientFuel();
                AuditAncientRelicCardRouting();
                AuditLibraryCards();
                AuditPowerDescriptionProjection();
                AuditCombatUpgradeOperations();
                AuditGeneratedCardAfflictions();
                const string numericRandomMultiplayerSeed = "AUTOANTHONY_NUMERIC_RANDOM_MULTIPLAYER_AUDIT";
                ChaosRunDefinitions.ActivateForStartupAudit(
                    [GeneratedCharacter.Ironclad, GeneratedCharacter.Regent],
                    numericRandomMultiplayerSeed, randomizeNumericValues: true);
                ChaosPoolSnapshot.AuditAuthoritativeMultiplayerRoundTrip(
                    [GeneratedCharacter.Ironclad, GeneratedCharacter.Regent], numericRandomMultiplayerSeed,
                    ChaosRunDefinitions.GetAllCards());
                MultiplayerGenerationModePatch.AuditPreparationProtocol();
                Log.Info("[AutoAnthony] Numeric-random authoritative multiplayer snapshot audit passed.");
                ChaosRunDefinitions.ActivateForStartupAudit(
                    [GeneratedCharacter.Ironclad, GeneratedCharacter.Silent, GeneratedCharacter.Defect,
                        GeneratedCharacter.Necrobinder, GeneratedCharacter.Regent],
                    "AUTOANTHONY_FULL_STARTUP_AUDIT");
                AuditRunReplacement(auditPoolQuotas: true);
                AuditExternalCatalogs();
                ChaosDerivativeResolver.AuditEnchantmentCompatibility();
                AuditEnergyIconTemplates();
                ChaosPoolSnapshot.AuditRoundTrip([GeneratedCharacter.Ironclad, GeneratedCharacter.Silent],
                    "IRONCLAD_CHAOS_LIBRARY_PREVIEW", ChaosRunDefinitions.GetAllCards());
                AuditExecutionRouting();
                Audit(GeneratedCharacter.Ironclad);
                Audit(GeneratedCharacter.Silent);
                Audit(GeneratedCharacter.Defect);
                Audit(GeneratedCharacter.Necrobinder);
                Audit(GeneratedCharacter.Regent);
                Audit(GeneratedCharacter.Colorless);
                Log.Info("[AutoAnthony] Full startup audit passed for all character and Colorless pools.");
            }
            else
            {
                Log.Info("[AutoAnthony] Production startup initialization completed. "
                         + "Set AUTOANTHONY_STARTUP_AUDIT=1 for the development audit.");
            }
        }
        catch (Exception exception)
        {
            // ModelDb.InitIds is part of boot. Letting a diagnostic exception escape here black-screens the
            // game before the settings UI exists, so fail open and retain a useful log for diagnosis.
            Log.Error($"[AutoAnthony] Startup initialization failed without blocking game startup: {exception}");
        }
        finally
        {
            ChaosRunDefinitions.DeactivateRun();
        }
    }

    private static void AuditAncientFuel()
    {
        var fuel = ModelDb.Card<Fuel>();
        var ancientFuel = ModelDb.Card<AncientFuel>();
        var failures = new List<string>();
        if (ancientFuel.Type != CardType.Skill) failures.Add($"type={ancientFuel.Type}");
        if (ancientFuel.Rarity != CardRarity.Token) failures.Add($"rarity={ancientFuel.Rarity}");
        if (ancientFuel.TargetType != TargetType.Self) failures.Add($"target={ancientFuel.TargetType}");
        if (!ancientFuel.ShouldShowInCardLibrary) failures.Add("library=false");
        if (ancientFuel.Pool is not TokenCardPool) failures.Add($"pool={ancientFuel.Pool.GetType().Name}");
        if (fuel.Pool is not TokenCardPool) failures.Add($"fuelPool={fuel.Pool.GetType().Name}");
        if (!ModelDb.AllCards.Contains(ancientFuel)) failures.Add("missingFromModelDbAllCards");
        if (!ancientFuel.Pool.AllCards.Contains(ancientFuel)) failures.Add("missingFromTokenPool");
        if (!ancientFuel.Keywords.Contains(CardKeyword.Exhaust)) failures.Add("missingExhaust");
        if (ancientFuel.DynamicVars.Energy.IntValue != 1) failures.Add($"energy={ancientFuel.DynamicVars.Energy.IntValue}");
        if (ancientFuel.DynamicVars.Cards.IntValue != 1) failures.Add($"cards={ancientFuel.DynamicVars.Cards.IntValue}");
        if (ancientFuel.PortraitPath != fuel.PortraitPath) failures.Add("portraitMismatch");
        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"Ancient Fuel canonical model audit failed: {string.Join(", ", failures)}.");
        var upgraded = ancientFuel.ToMutable();
        CardCmd.Upgrade(upgraded);
        if (upgraded.DynamicVars.Energy.IntValue != 1 || upgraded.DynamicVars.Cards.IntValue != 2)
            throw new InvalidOperationException("Ancient Fuel upgrade audit failed.");

        const int sampleSize = 10_000;
        var hits = Enumerable.Range(0, sampleSize)
            .Count(index => ChaosRunDefinitions.ShouldUseAncientFuel($"ANCIENT_FUEL_AUDIT_{index}"));
        if (hits is < 850 or > 1_150)
            throw new InvalidOperationException($"Ancient Fuel 10% seed distribution audit failed: {hits}/{sampleSize}.");
    }

    private static void AuditGeneratedCardAfflictions()
    {
        var player = Player.CreateForNewRun<Ironclad>(UnlockState.all, ulong.MaxValue - 147);
        player.ResetCombatState();
        var combatState = new CombatState();
        combatState.AddPlayer(player);

        foreach (var character in Enum.GetValues<GeneratedCharacter>())
        {
            foreach (var canonical in Enumerable.Range(0, ChaosRunDefinitions.CountFor(character))
                         .Select(slot => ChaosCardRegistry.Canonical(character, slot)))
            {
                var card = (ChaosCardModel)combatState.CreateCard(canonical, player);
                player.PlayerCombatState!.DrawPile.AddInternal(card, silent: true);
                AuditAffliction<Hexed>(card, 2);
                AuditAffliction<Bound>(card, 3);
                player.PlayerCombatState.DrawPile.RemoveInternal(card, silent: true);
            }
        }

        var recoveryProbe = (ChaosCardModel)combatState.CreateCard(
            ChaosCardRegistry.Canonical(GeneratedCharacter.Ironclad, 0), player);
        player.PlayerCombatState!.DrawPile.AddInternal(recoveryProbe, silent: true);
        ChaosAfflictionCompatibility.Audit(recoveryProbe);
        player.PlayerCombatState.DrawPile.RemoveInternal(recoveryProbe, silent: true);

        Log.Info("[AutoAnthony] Generated-card Hexed/Bound affliction lifecycle audit passed.");
    }

    private static void AuditAffliction<T>(ChaosCardModel card, int amount) where T : AfflictionModel
    {
        var affliction = ModelDb.Affliction<T>().ToMutable();
        card.AfflictInternal(affliction, amount);
        if (card.Affliction is not T || card.Affliction.Amount != amount)
            throw new InvalidOperationException($"{card.Id} failed to apply {typeof(T).Name}.");
        _ = card.GetDescriptionForPile(PileType.Draw);
        _ = card.HoverTips.ToArray();
        var clone = card.CreateClone();
        if (clone.Affliction is not T || clone.Affliction.Amount != amount)
            throw new InvalidOperationException($"{card.Id} failed to clone {typeof(T).Name}.");
        // Afflictions are combat-only models and the base game deliberately does not put them in SerializableCard;
        // multiplayer references the already-subscribed combat card instead. Clone coverage is the relevant model
        // compatibility check here.
        card.ClearAfflictionInternal();
        if (card.Affliction is not null)
            throw new InvalidOperationException($"{card.Id} failed to clear {typeof(T).Name}.");
    }

    private static void AuditAncientRelicCardRouting()
    {
        // The Ancient-event page reads canonical relic descriptions before it creates player-owned option copies.
        // Keep this in the production startup audit so an Owner access from a description patch cannot regress into
        // a CanonicalModelException that only appears when the player reaches Orobas/Darv.
        foreach (var relic in new RelicModel[] { ModelDb.Relic<ArchaicTooth>(), ModelDb.Relic<DustyTome>() })
        {
            var description = relic.DynamicDescription.GetFormattedText();
            var eventDescription = relic.DynamicEventDescription.GetFormattedText();
            if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(eventDescription))
                throw new InvalidOperationException($"{relic.Id} canonical description audit failed.");
        }

        foreach (var character in new[]
                 {
                     GeneratedCharacter.Ironclad, GeneratedCharacter.Silent, GeneratedCharacter.Defect,
                     GeneratedCharacter.Necrobinder, GeneratedCharacter.Regent
                 })
        {
            var firstAncientSlot = ChaosRunDefinitions.CountFor(character) - ChaosRunDefinitions.AncientCount;
            var firstCard = ChaosCardRegistry.Canonical(character, firstAncientSlot);
            var secondCard = ChaosCardRegistry.Canonical(character, firstAncientSlot + 1);
            var first = firstCard.Id;
            var second = secondCard.Id;
            if (!ChaosAncientRelics.IsStoredGeneratedCard(first)
                || !ChaosAncientRelics.IsStoredGeneratedCard(second)
                || first == second)
                throw new InvalidOperationException($"Generated Ancient relic routing audit failed for {character}.");

            var originalToothCard = ChaosAncientRelics.OriginalAncientCanonical(character, 0);
            var originalTomeCard = ChaosAncientRelics.OriginalAncientCanonical(character, 1);
            var transcendenceIds = ArchaicTooth.TranscendenceCards.Select(card => card.Id).ToHashSet();
            if (originalToothCard.Rarity != CardRarity.Ancient
                || originalTomeCard.Rarity != CardRarity.Ancient
                || originalToothCard.Id == originalTomeCard.Id
                || !transcendenceIds.Contains(originalToothCard.Id)
                || transcendenceIds.Contains(originalTomeCard.Id))
                throw new InvalidOperationException($"Vanilla Ancient relic routing audit failed for {character}.");

            // Orobas asks Archaic Tooth to prepare its referenced card while generating the third relic option.
            // Materialize the final option-tip sequence here to ensure that path stays independent of generated
            // card secondary hover-tip construction.
            var archaicTooth = (ArchaicTooth)ModelDb.Relic<ArchaicTooth>().ToMutable();
            ChaosAncientRelics.BindArchaicToothCard(archaicTooth, firstCard);
            var toothTips = archaicTooth.HoverTipsExcludingRelic.ToArray();
            if (archaicTooth.StarterCard?.Id != first || archaicTooth.AncientCard?.Id != first
                || toothTips.Length != 1)
                throw new InvalidOperationException($"Archaic Tooth generated-card preview audit failed for {character}.");

            // Dusty Tome resolves the referenced card and builds its upgraded preview before the relic receives an
            // owner. Exercise that exact setup path for every character so a malformed generated Ancient cannot
            // silently break Darv's option construction.
            var dustyTome = (DustyTome)ModelDb.Relic<DustyTome>().ToMutable();
            ChaosAncientRelics.BindDustyTomeCard(dustyTome, secondCard);
            var dustyTips = dustyTome.HoverTipsExcludingRelic.ToArray();
            if (dustyTome.AncientCard != second || dustyTips.Length != 1)
                throw new InvalidOperationException($"Dusty Tome generated-card preview audit failed for {character}.");

            var upgradedReward = secondCard.ToMutable();
            CardCmd.Upgrade(upgradedReward);
            if (!upgradedReward.IsUpgraded)
                throw new InvalidOperationException($"Dusty Tome generated-card upgrade audit failed for {character}.");
        }
        if (ChaosAncientRelics.IsStoredGeneratedCard(ModelDb.Card<MeteorShower>().Id))
            throw new InvalidOperationException("Vanilla Meteor Shower was mistaken for a stored generated Ancient card.");
    }

    private static void AuditPowerDescriptionProjection()
    {
        static GeneratorOperation Structured(GeneratorOperation operation) => operation with
        {
            RuntimeSpec = OperationRuntimeSpecCompiler.CompileLegacy(operation)
        };

        var descriptionProbe = new GeneratorOperation[]
        {
            Structured(new("N:B", OperationScope.NonTargeted, "获得8点格挡。",
                new Dictionary<string, int> { ["block"] = 8 })),
            Structured(new("A:turnStart", OperationScope.AbilityTrigger, "在你的回合开始时，",
                new Dictionary<string, int>())),
            Structured(new("N:RandomD", OperationScope.NonTargeted, "对一名随机敌人造成X+1点伤害。",
                new Dictionary<string, int> { ["damage"] = 1, ["triggerIndex"] = 1 }))
        };
        var filteredProbe = ChaosCompositePower.DescriptionOperations(descriptionProbe);
        if (filteredProbe.Count != 2 || filteredProbe.Any(operation => operation.Template == "N:B")
            || filteredProbe[1].Parameters.GetValueOrDefault("triggerIndex", -1) != 0)
            throw new InvalidOperationException("AutoAnthony Power operation filtering audit failed.");
        if (ChaosCompositePower.ResolvePrintedX("X点伤害；X+1次。", 3) != "3点伤害；4次。")
            throw new InvalidOperationException("AutoAnthony resolved Power X-value audit failed.");
        if (ChaosOperationVariables.ReplaceInitialValue(descriptionProbe[0], 13).ChineseText
                != "获得13点格挡。")
            throw new InvalidOperationException("AutoAnthony captured Power value replacement audit failed.");
        var drawAndBlock = Structured(new GeneratorOperation("I:DrawAndBlockIfSkill", OperationScope.NonTargeted,
            "抽2张牌。如果抽到的是技能牌，获得7点格挡。", new Dictionary<string, int>()));
        if (ChaosOperationVariables.ReplaceInitialValue(drawAndBlock, 11).ChineseText
                != "抽2张牌。如果抽到的是技能牌，获得11点格挡。")
            throw new InvalidOperationException("AutoAnthony secondary captured Power value replacement audit failed.");
        var enchantedShiv = Structured(new GeneratorOperation("N:CreateInkShiv", OperationScope.NonTargeted,
            "将2张墨影小刀加入手牌。", new Dictionary<string, int>(), DerivativeId: "shiv",
            DerivativeEnchantmentId: "inky"));
        var styledChinese = ChaosTextFormatter.Format(ChaosDerivativeTextStyle.Apply(
            enchantedShiv.ChineseText, [enchantedShiv], chinese: true), chinese: true);
        var styledEnglish = ChaosTextFormatter.Format(ChaosDerivativeTextStyle.Apply(
            "Add 2 Inky Shivs to your hand.", [enchantedShiv], chinese: false), chinese: false);
        if (!styledChinese.Contains("[purple]墨影[/purple][gold]小刀[/gold]", StringComparison.Ordinal)
            || !styledEnglish.Contains("[purple]Inky[/purple] [gold]Shivs[/gold]", StringComparison.Ordinal)
            || styledChinese.Contains("[gold][gold]", StringComparison.Ordinal)
            || styledEnglish.Contains("[gold][gold]", StringComparison.Ordinal))
            throw new InvalidOperationException("AutoAnthony derivative/enchantment description colors are invalid.");
        var currentTurnAttack = Structured(new GeneratorOperation("C:untilTurnEnd", OperationScope.ConditionalTrigger,
            "本回合每当你打出一张攻击牌时，", new Dictionary<string, int>()));
        var currentTurnDefense = Structured(new GeneratorOperation("C:untilTurnEnd", OperationScope.ConditionalTrigger,
            "本回合每当你受到一次攻击时，", new Dictionary<string, int>()));
        var combatLongTrigger = Structured(new GeneratorOperation("A:turnStart", OperationScope.AbilityTrigger,
            "在你的回合开始时，", new Dictionary<string, int>()));
        if (ChaosCompositePower.TurnLimitedTriggerExpired(currentTurnAttack, false, false)
            || !ChaosCompositePower.TurnLimitedTriggerExpired(currentTurnAttack, true, false)
            || ChaosCompositePower.TurnLimitedTriggerExpired(currentTurnDefense, true, false)
            || !ChaosCompositePower.TurnLimitedTriggerExpired(currentTurnDefense, false, true)
            || ChaosCompositePower.TurnLimitedTriggerExpired(combatLongTrigger, true, true))
            throw new InvalidOperationException("AutoAnthony mixed-duration Power expiration audit failed.");
        var damageVar = new ChaosDamageVar("PowerDamageAudit", 5, ValueProp.Move);
        var blockVar = new ChaosBlockVar("PowerBlockAudit", 5, ValueProp.Move);
        var unpoweredDamage = new ChaosDamageVar("UnpoweredDamageAudit", 5, ValueProp.Unpowered);
        var proxyDamage = descriptionProbe[2] with { Template = "N:ProxyDamage_Audit" };
        if (!ChaosCardModel.UsesFinalCombatPreviewInPower(descriptionProbe[2], damageVar)
            || !ChaosCardModel.UsesFinalCombatPreviewInPower(descriptionProbe[0], blockVar)
            || ChaosCardModel.UsesFinalCombatPreviewInPower(descriptionProbe[2], unpoweredDamage)
            || ChaosCardModel.UsesFinalCombatPreviewInPower(proxyDamage, damageVar))
            throw new InvalidOperationException("AutoAnthony Power Strength/Dexterity capture routing audit failed.");
        var choiceProbe = new GeneratorOperation[]
        {
            Structured(new("NCR:WheneverCardPlayedThisTurn", OperationScope.AbilityTrigger,
                "本回合每当你打出一张牌时，", new Dictionary<string, int>())),
            Structured(new("N_SELECT_HAND_CARD", OperationScope.NonTargeted, "选择手牌中的一张牌。",
                new Dictionary<string, int> { ["slotIndex"] = 1 })),
            Structured(new("R:PlaySelectedSkillMultipleTimes", OperationScope.NonTargeted,
                "选择手牌中的一张技能牌，将其打出5次。",
                new Dictionary<string, int> { ["triggerIndex"] = 0 }, "card1"))
        };
        if (ChaosOperationExecutor.CardSelectorForSlot(choiceProbe, "card1")?.Template != "N_SELECT_HAND_CARD"
            || ChaosOperationExecutor.CardSelectorForSlot(choiceProbe, "eventCard") is not null)
            throw new InvalidOperationException("AutoAnthony triggered card-choice slot routing audit failed.");
    }

    private static void EnsureLibraryCardsInModelDb()
    {
        var ancientFuel = ModelDb.Card<AncientFuel>();
        var cards = ModelDb.AllCards.ToList();

        // ModelDb.AllCards is cached from the currently visible character pools. Those pools are deliberately
        // swapped only while a chaos run is active, so a cache made on the title screen otherwise contains no
        // generated cards and every in-run library tab becomes empty. Keep a stable union instead: vanilla cards
        // remain available outside runs, while all six generated families are available to the in-run filters.
        foreach (var character in ChaosRunDefinitions.SupportedPools)
        foreach (var type in ChaosCardRegistry.TypesFor(character))
            cards.Add(ModelDb.GetById<CardModel>(ModelDb.GetId(type)));
        cards.Add(ancientFuel);

        AccessTools.Field(typeof(ModelDb), "_allCards").SetValue(null, cards.Distinct().ToArray());
    }

    private static void AuditLibraryCards()
    {
        var allCards = ModelDb.AllCards.ToHashSet();
        var generated = ChaosRunDefinitions.SupportedPools
            .SelectMany(character => ChaosCardRegistry.TypesFor(character)
                .Select(type => (Character: character,
                    Card: ModelDb.GetById<CardModel>(ModelDb.GetId(type)))))
            .ToArray();
        var missing = generated
            .Where(entry => !allCards.Contains(entry.Card))
            .Select(entry => entry.Card.Id.ToString())
            .ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                $"Generated card-library registration audit failed: {string.Join(", ", missing.Take(8))}");

        foreach (var (character, card) in generated)
        {
            var validPool = character switch
            {
                GeneratedCharacter.Ironclad => card.Pool is ChaosIroncladCardPool,
                GeneratedCharacter.Silent => card.Pool is ChaosSilentCardPool,
                GeneratedCharacter.Defect => card.Pool is ChaosDefectCardPool,
                GeneratedCharacter.Necrobinder => card.Pool is ChaosNecrobinderCardPool,
                GeneratedCharacter.Regent => card.Pool is ChaosRegentCardPool,
                GeneratedCharacter.Colorless => card.Pool is ColorlessCardPool,
                _ => false
            };
            if (!validPool)
                throw new InvalidOperationException(
                    $"Generated card-library pool audit failed: {card.Id} resolved to {card.Pool.GetType().Name}.");
        }

        var generatedAncients = generated.Select(entry => entry.Card)
            .Where(card => ChaosCardLibraryRules.IsAncientTabCard(card, replaceCharacterAncients: true)).ToArray();
        var vanillaAncients = new CardPoolModel[]
            {
                ModelDb.CardPool<IroncladCardPool>(), ModelDb.CardPool<SilentCardPool>(),
                ModelDb.CardPool<DefectCardPool>(), ModelDb.CardPool<NecrobinderCardPool>(),
                ModelDb.CardPool<RegentCardPool>()
            }
            .SelectMany(pool => pool.AllCards)
            .Where(card => card.Rarity == CardRarity.Ancient).ToArray();
        var eventAncients = ModelDb.CardPool<EventCardPool>().AllCards
            .Where(card => card.Rarity == CardRarity.Ancient).ToArray();
        if (generatedAncients.Length != ChaosRunDefinitions.AncientCount * 5
            || vanillaAncients.Length != ChaosRunDefinitions.AncientCount * 5
            || vanillaAncients.Any(card => ChaosCardLibraryRules.IsAncientTabCard(card, replaceCharacterAncients: true))
            || generatedAncients.Any(card => ChaosCardLibraryRules.IsAncientTabCard(card, replaceCharacterAncients: false))
            || vanillaAncients.Any(card => !ChaosCardLibraryRules.IsAncientTabCard(card, replaceCharacterAncients: false))
            || eventAncients.Length == 0
            || eventAncients.Any(card => !ChaosCardLibraryRules.IsAncientTabCard(card, replaceCharacterAncients: true))
            || eventAncients.Any(card => !ChaosCardLibraryRules.IsAncientTabCard(card, replaceCharacterAncients: false))
            || !ChaosCardLibraryRules.IsAncientTabCard(ModelDb.Card<Apparition>(), replaceCharacterAncients: true))
            throw new InvalidOperationException("Generated Ancient card-library filter audit failed.");
    }

    private static void AuditEnergyIconTemplates()
    {
        static GeneratorOperation Structured(GeneratorOperation operation) => operation with
        {
            RuntimeSpec = OperationRuntimeSpecCompiler.CompileLegacy(operation)
        };

        var templates = new[]
        {
            "N:E", "N:NextTurnEnergy", "D:GainEnergy", "D:NextTurnEnergy",
            "NCR:GainEnergy", "NCR:NextTurnEnergy", "R:GainEnergy"
        };
        foreach (var template in templates)
        {
            var operation = Structured(new GeneratorOperation(template, OperationScope.NonTargeted,
                template.Contains("NextTurn", StringComparison.Ordinal)
                    ? "在下个回合获得2点能量。"
                    : "获得2点能量。",
                new Dictionary<string, int>()));
            var chinese = ChaosOperationVariables.InsertToken(operation, 0, operation.ChineseText, chinese: true);
            var englishSource = template.Contains("NextTurn", StringComparison.Ordinal)
                ? "Next turn, gain 2 Energy."
                : "Gain 2 Energy.";
            var english = ChaosOperationVariables.InsertToken(operation, 0, englishSource, chinese: false);
            if (ChaosOperationVariables.Name(operation, 0) != "Energy0"
                || !chinese.Contains("{Energy0:energyIcons()}", StringComparison.Ordinal)
                || !english.Contains("{Energy0:energyIcons()}", StringComparison.Ordinal))
                throw new InvalidOperationException($"AutoAnthony energy icon formatter audit failed for {template}.");
        }

        var spent = Structured(new GeneratorOperation("A:whenEnergySpent", OperationScope.AbilityTrigger,
            "你每花费4点能量。", new Dictionary<string, int>()));
        var spentChinese = ChaosOperationVariables.InsertToken(spent, 0, spent.ChineseText, chinese: true);
        var spentEnglish = ChaosOperationVariables.InsertToken(spent, 0, "Every 4 Energy you spend.", chinese: false);
        if (!spentChinese.Contains("{energyPrefix:energyIcons(4)}", StringComparison.Ordinal)
            || spentChinese.Contains("点能量", StringComparison.Ordinal)
            || !spentEnglish.Contains("{energyPrefix:energyIcons(4)}", StringComparison.Ordinal)
            || spentEnglish.Contains(" Energy", StringComparison.Ordinal))
            throw new InvalidOperationException("Orbit Energy-spent trigger icon formatter audit failed.");

        var helixSpent = Structured(new GeneratorOperation("D:ForEachEnergySpentThisTurn",
            OperationScope.ConditionalTrigger, "在本回合中，此牌以外每使用了1点能量，",
            new Dictionary<string, int>()));
        var helixChinese = ChaosOperationVariables.InsertToken(helixSpent, 0,
            helixSpent.ChineseText, chinese: true);
        var helixEnglish = ChaosOperationVariables.InsertToken(helixSpent, 0,
            "For every 1 Energy spent this turn except on this card,", chinese: false);
        if (!helixChinese.Contains("{energyPrefix:energyIcons(1)}", StringComparison.Ordinal)
            || helixChinese.Contains("点能量", StringComparison.Ordinal)
            || !helixEnglish.Contains("{energyPrefix:energyIcons(1)}", StringComparison.Ordinal)
            || helixEnglish.Contains(" Energy", StringComparison.Ordinal))
            throw new InvalidOperationException("Helix Drill Energy-spent formatter audit failed.");
    }

    private static void AuditExecutionRouting()
    {
        static GeneratorOperation Structured(GeneratorOperation probe) => probe with
        {
            RuntimeSpec = OperationRuntimeSpecCompiler.CompileLegacy(probe)
        };

        static GeneratorOperation StructuredProbe(string template, OperationScope scope, string chinese,
            IReadOnlyDictionary<string, int>? parameters = null)
        {
            var probe = new GeneratorOperation(template, scope, chinese,
                parameters ?? new Dictionary<string, int>());
            return Structured(probe);
        }

        AuditRuntimeSpecs();
        var fatalUnblockedDamage = Structured(new GeneratorOperation("CL:DieOnUnblockedAttack",
            OperationScope.AbilityRule, "受到未被格挡的攻击伤害时，立即死亡。", new Dictionary<string, int>()));
        var immediateAbilityRule = Structured(new GeneratorOperation("A:ProxyAtomic_Buffer",
            OperationScope.AbilityRule, "阻止下一次生命损伤。", new Dictionary<string, int>()));
        var retainWholeHandRule = Structured(new GeneratorOperation("A:ruleRetainHand",
            OperationScope.AbilityRule, "在你的回合结束时，不再丢弃你的手牌。", new Dictionary<string, int>()));
        if (!ChaosOperationExecutor.RequiresCompositePower(fatalUnblockedDamage)
            || !ChaosOperationExecutor.RequiresCompositePower(retainWholeHandRule)
            || ChaosOperationExecutor.RequiresCompositePower(immediateAbilityRule))
            throw new InvalidOperationException(
                "The Gambit persistent-rule Power arming audit failed.");
        if (!ChaosOperationExecutor.TriggeredDamageUsesPoweredAttack(sourceIsInCombatPile: true)
            || ChaosOperationExecutor.TriggeredDamageUsesPoweredAttack(sourceIsInCombatPile: false))
            throw new InvalidOperationException(
                "Card-lifecycle triggered damage no longer distinguishes live cards from captured Power proxies.");
        var numericProxyTemplates = Enum.GetValues<GeneratedCharacter>()
            .SelectMany(character => CharacterComponentCatalogs.Get(character).Recipes)
            .SelectMany(recipe => recipe.Atoms)
            .Where(atom => atom.Template.Contains(":Proxy", StringComparison.Ordinal)
                && OperationRuntimeSpecCompiler.ExplicitFixedValueSlots(atom).Count > 0)
            .Select(atom => atom.Template)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var uninterpretedNumericProxies = numericProxyTemplates
            .Where(template => !ChaosOperationExecutor.InterpretsGeneratedProxyValues(template)).ToArray();
        if (uninterpretedNumericProxies.Length > 0)
            throw new InvalidOperationException("Numeric original-card proxies still use native fixed values: "
                                                + string.Join(", ", uninterpretedNumericProxies));
        var orbit = RequireSingle(CharacterComponentCatalogs.Get(GeneratedCharacter.Regent).Recipes,
            recipe => recipe.Id == "Orbit", "Regent/Orbit recipe");
        if (orbit.Atoms.Count != 2 || orbit.Atoms[0].Template != "A:whenEnergySpent"
            || orbit.Atoms[1].Template != "R:GainEnergy" || orbit.TriggerOwners[1] != 0
            || CardEffectRules.TriggerSupportsChoiceContext(new GeneratorOperation(orbit.Atoms[0].Template,
                orbit.Atoms[0].Scope, orbit.Atoms[0].ChineseText, new Dictionary<string, int>(),
                RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(orbit.Atoms[0]))))
            throw new InvalidOperationException("Orbit component decomposition/routing audit failed.");

        var dominate = RequireSingle(CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad).Recipes,
            recipe => recipe.Id == "Dominate", "Ironclad/Dominate recipe");
        var strength = RequireSingle(dominate.Atoms,
            atom => atom.ChineseText.Contains("易伤", StringComparison.Ordinal)
                    && atom.ChineseText.Contains("力量", StringComparison.Ordinal),
            "Ironclad/Dominate vulnerable-Strength atom");
        if (strength.Template != "N:StrengthPerTargetVulnerable" || !strength.RequiresSingleTarget
            || !ChaosOperationExecutor.IsTargetVulnerableStrength(
                new GeneratorOperation(strength.Template, strength.Scope, strength.ChineseText,
                    new Dictionary<string, int>(), RequiresSingleTarget: true,
                    RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(strength))))
            throw new InvalidOperationException("Dominate target-scaled Strength routing audit failed.");

        var ashenStrike = RequireSingle(CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad).Recipes,
            recipe => recipe.Id == "AshenStrike", "Ironclad/AshenStrike recipe");
        var exhaustModifier = RequireSingle(ashenStrike.Atoms,
            atom => atom.Scope == OperationScope.Modifier, "Ironclad/AshenStrike modifier atom");
        var legacyStyledModifier = Structured(new GeneratorOperation("M:base", OperationScope.Modifier,
            "你的消耗牌堆中每有1张牌，伤害增加3点。", new Dictionary<string, int>()));
        if (exhaustModifier.Template != "M:DamagePerExhaustCard"
            || !ChaosOperationExecutor.IsExhaustPileDamageModifier(
                new GeneratorOperation(exhaustModifier.Template, exhaustModifier.Scope, exhaustModifier.ChineseText,
                    new Dictionary<string, int>(),
                    RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(exhaustModifier)))
            || !ChaosOperationExecutor.IsExhaustPileDamageModifier(legacyStyledModifier)
            || !ChaosOperationVariables.TryGetInitialValue(legacyStyledModifier, out var modifierAmount)
            || modifierAmount != 3)
            throw new InvalidOperationException("Exhaust-pile damage modifier routing audit failed.");

        var currentCharacterCard = Structured(new GeneratorOperation("N:CreateCurrentCharacterCardInHand",
            OperationScope.NonTargeted, "将一张当前角色的随机牌加入手牌。", new Dictionary<string, int>()));
        var upgradedCurrentCharacterCard = currentCharacterCard with
        {
            ChineseText = CardUpgradeGenerator.UpgradeRandomGenerationChinese(currentCharacterCard.ChineseText)
        };
        var legacyCurrentCharacterCard = currentCharacterCard with { Template = "N:Create" };
        if (!ChaosOperationExecutor.IsCurrentCharacterRandomCardGeneration(currentCharacterCard)
            || !ChaosOperationExecutor.IsCurrentCharacterRandomCardGeneration(upgradedCurrentCharacterCard)
            || !ChaosOperationExecutor.IsCurrentCharacterRandomCardGeneration(legacyCurrentCharacterCard))
            throw new InvalidOperationException("Current-character random-card generation routing audit failed.");

        var autoplay = Structured(new GeneratorOperation("I:PlayTopCardAndExhaust", OperationScope.Independent,
            "打出抽牌堆顶部的牌并将其消耗。", new Dictionary<string, int> { ["triggerIndex"] = 0 }));
        if (!ChaosCompositePower.TurnStartOperationNeedsChoiceContext(autoplay))
            throw new InvalidOperationException("Turn-start autoplay choice-context audit failed.");
        if (ChaosOperationExecutor.RandomDrawAutoplayLimit(2) != 2
            || ChaosOperationExecutor.RandomDrawAutoplayLimit(0) != 1)
            throw new InvalidOperationException("Random draw-pile Attack autoplay ignores its printed amount.");
        if (ChaosOperationExecutor.RandomHandAutoplayLimit(2) != 2
            || ChaosOperationExecutor.RandomHandAutoplayLimit(0) != 1)
            throw new InvalidOperationException("Random hand Attack autoplay ignores its printed amount.");
        if (ChaosOperationExecutor.FillHandTargetCount(returnsThisToHand: false) != CardPile.MaxCardsInHand
            || ChaosOperationExecutor.FillHandTargetCount(returnsThisToHand: true) != CardPile.MaxCardsInHand - 1)
            throw new InvalidOperationException("Fill-hand effects do not reserve a slot for a card that returns itself to hand.");
        if (ChaosOperationExecutor.ExecutableOrbRepeatCount(3) != 3
            || ChaosOperationExecutor.ExecutableOrbRepeatCount(0) != 0
            || ChaosOperationExecutor.ExecutableOrbRepeatCount(-1) != 0)
            throw new InvalidOperationException("X-scaled orb operations do not treat zero payment as a no-op.");
        if (!ChaosOperationExecutor.ExhaustedCardMatchesTrigger("for_each_exhausted_status", CardType.Status)
            || ChaosOperationExecutor.ExhaustedCardMatchesTrigger("for_each_exhausted_status", CardType.Skill)
            || ChaosOperationExecutor.ExhaustedCardMatchesTrigger("for_each_exhausted_non_attack", CardType.Attack)
            || !ChaosOperationExecutor.ExhaustedCardMatchesTrigger("for_each_exhausted_non_attack", CardType.Status)
            || !ChaosOperationExecutor.ExhaustedCardMatchesTrigger("for_each_exhausted_card", CardType.Attack))
            throw new InvalidOperationException("Exhaust-count triggers no longer filter their actual exhausted card types.");
        var dazedDiscard = Structured(new GeneratorOperation("D:CreateDazedInDiscard", OperationScope.NonTargeted,
            "将一张晕眩加入弃牌堆。", new Dictionary<string, int>(), DerivativeId: "dazed"));
        var woundDiscard = Structured(new GeneratorOperation("D:CreateTwoWoundsInDiscard", OperationScope.NonTargeted,
            "将2张伤口加入弃牌堆。", new Dictionary<string, int>(), DerivativeId: "wound"));
        var slimeDiscard = Structured(new GeneratorOperation("D:CreateSlimeInDiscard", OperationScope.NonTargeted,
            "将一张黏液加入弃牌堆。", new Dictionary<string, int>(), DerivativeId: "slimed"));
        var burnDiscard = Structured(new GeneratorOperation("D:CreateBurnInDiscard", OperationScope.NonTargeted,
            "将一张灼伤加入弃牌堆。", new Dictionary<string, int>(), DerivativeId: "burn"));
        var debrisHand = Structured(new GeneratorOperation("R:AddDebrisToHand", OperationScope.NonTargeted,
            "将一张碎屑加入手牌。", new Dictionary<string, int>(), DerivativeId: "debris"));
        var randomColorless = Structured(new GeneratorOperation("R:AddRandomColorlessToHand",
            OperationScope.NonTargeted, "将一张随机无色牌加入手牌。", new Dictionary<string, int>()));
        var starXCreation = new GeneratorOperation("R:AddDebrisToHand", OperationScope.NonTargeted,
            "将X张碎屑加入手牌。", new Dictionary<string, int>(), DerivativeId: "debris",
            RuntimeSpec: new OperationRuntimeSpec(OperationRuntimeSpec.CurrentSchemaVersion,
                "create_card", "derivative", "self", "none", "hand", "any",
                ["count_unit_reference"],
                [new RuntimeValueSlot("amount", 0, "star_x", 0, true, true)]));
        var transformTwo = Structured(new GeneratorOperation("CL:TransformSelectedHandCards",
            OperationScope.NonTargeted, "变化手牌中的2张牌。", new Dictionary<string, int>()));
        var repeatSelectedSkill = Structured(new GeneratorOperation("R:PlaySelectedSkillMultipleTimes",
            OperationScope.NonTargeted, "选择一张技能牌，将其打出3次。", new Dictionary<string, int>()));
        if (ChaosOperationExecutor.ExecutableDerivativeDiscardCount(dazedDiscard, 0) != 1
            || ChaosOperationExecutor.ExecutableDerivativeDiscardCount(woundDiscard, 0) != 2
            || ChaosOperationExecutor.ExecutableDerivativeDiscardCount(slimeDiscard, 0) != 1
            || ChaosOperationExecutor.ExecutableDerivativeDiscardCount(burnDiscard, 0) != 1
            || ChaosOperationExecutor.ExecutableOperationCount(debrisHand, 0) != 1
            || ChaosOperationExecutor.ExecutableOperationCount(randomColorless, 0) != 1
            || ChaosOperationExecutor.ExecutableOperationCount(starXCreation, 0) != 0
            || ChaosOperationExecutor.SelectionCountForEffect(transformTwo, 2) != 2
            || ChaosOperationExecutor.SelectionCountForEffect(repeatSelectedSkill, 3) != 1
            || ChaosOperationExecutor.ExecutableGeneratedCardCount(-1) != 0
            || ChaosOperationExecutor.GeneratedCardChoiceCandidateCount(0) != 0
            || ChaosOperationExecutor.GeneratedCardChoiceCandidateCount(4) != 4
            || ChaosOperationExecutor.GeneratedCardChoiceCandidateCount(9) != 4
            || ChaosOperationExecutor.HasGeneratedCardChoiceCandidates(0, 0)
            || ChaosOperationExecutor.HasGeneratedCardChoiceCandidates(2, 0)
            || !ChaosOperationExecutor.HasGeneratedCardChoiceCandidates(2, 2))
            throw new InvalidOperationException(
                "Generated-card counts or empty generated-choice guards failed their runtime audit.");
        var firstCardTrigger = StructuredProbe("A:firstCardPlayedEachTurn", OperationScope.AbilityTrigger,
            "每回合中，当你打出第一张牌时，");
        var replayPayoff = StructuredProbe("D:ReplayEventCard", OperationScope.NonTargeted,
            "重放该牌。", new Dictionary<string, int> { ["triggerIndex"] = 0 });
        var orbPayoff = StructuredProbe("D:ChannelRandom", OperationScope.NonTargeted,
            "生成3个随机充能球。", new Dictionary<string, int> { ["triggerIndex"] = 0 });
        if (!ChaosCompositePower.HasTriggerWithLinkedEffect([firstCardTrigger, replayPayoff],
                "first_card_played_each_turn", "D:ReplayEventCard")
            || ChaosCompositePower.HasTriggerWithLinkedEffect([firstCardTrigger, orbPayoff],
                "first_card_played_each_turn", "D:ReplayEventCard"))
            throw new InvalidOperationException("First-card trigger incorrectly inherits Echo Form replay without its linked payoff.");
        if (!ChaosCompositePower.IsFirstCardPlayThisTurn(1)
            || ChaosCompositePower.IsFirstCardPlayThisTurn(0)
            || ChaosCompositePower.IsFirstCardPlayThisTurn(2))
            throw new InvalidOperationException("Composable first-card triggers no longer dispatch exactly once each turn.");
        if (!ChaosCompositePower.ShouldFireOwnerTurnHpLoss(true, -1m, true)
            || ChaosCompositePower.ShouldFireOwnerTurnHpLoss(true, -1m, false)
            || ChaosCompositePower.ShouldFireOwnerTurnHpLoss(false, -1m, true)
            || ChaosCompositePower.ShouldFireOwnerTurnHpLoss(true, 1m, true))
            throw new InvalidOperationException("Owner HP-loss-during-turn triggers escaped their owner-turn boundary.");
        var restrictedHealing = StructuredProbe("N:Heal", OperationScope.NonTargeted,
            "回复5点生命。", new Dictionary<string, int>());
        if (ChaosOperationExecutor.CanBeRandomlyGeneratedInCombat([restrictedHealing])
            || !ChaosOperationExecutor.CanBeRandomlyGeneratedInCombat([orbPayoff]))
            throw new InvalidOperationException(
                "Random combat-card generation no longer excludes generated run-persistent rewards.");
        var activeTriggerProbe = new HashSet<int>();
        if (!ChaosCompositePower.TryEnterTrigger(activeTriggerProbe, 7, 0)
            || ChaosCompositePower.TryEnterTrigger(activeTriggerProbe, 7, 1)
            || !ChaosCompositePower.TryEnterTrigger(activeTriggerProbe, 8, 63)
            || ChaosCompositePower.TryEnterTrigger(activeTriggerProbe, 9, 64))
            throw new InvalidOperationException("Composite-Power trigger recursion guards no longer suppress re-entry or bound cross-trigger cycles.");
        var triggeredSelectedExhaust = Structured(new GeneratorOperation("N:Exhaust", OperationScope.NonTargeted,
            "消耗手牌中的2张牌。", new Dictionary<string, int> { ["triggerIndex"] = 0 }));
        var turnStartTrigger = Structured(new GeneratorOperation("A:turnStart", OperationScope.AbilityTrigger,
            "在你的回合开始时，", new Dictionary<string, int>()));
        if (!CardEffectRules.OperationNeedsChoiceContext(triggeredSelectedExhaust)
            || !CardEffectRules.TriggerSupportsChoiceContext(turnStartTrigger))
            throw new InvalidOperationException(
                "Triggered selected exhaust no longer routes through a real player-choice context.");
        var fixedValueLifecycleTemplates = new[]
        {
            "D:CostDownWhenStatusGenerated", "D:EvokeAllTwice", "D:CreateTwoWoundsInDiscard",
            "NCR:CostDownPerVoidPlayed", "NCR:CostDownWhenCreatureDies"
        };
        if (fixedValueLifecycleTemplates.Any(template => Enum.GetValues<GeneratedCharacter>()
                .SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms)
                .Where(atom => atom.Template == template)
                .Any(atom => OperationRuntimeSpecCompiler.ExplicitFixedValueSlots(atom).Count == 0)))
            throw new InvalidOperationException("Generated-value lifecycle routing audit lost an explicit numeric slot.");
        var plainX = OperationRuntimeSpecCompiler.CompileLegacy(new GeneratorOperation("I:ProxyAtomic_MultiCast",
            OperationScope.Independent, "激发你最右侧的充能球X次。", new Dictionary<string, int>()));
        var plusOneX = OperationRuntimeSpecCompiler.CompileLegacy(new GeneratorOperation("I:ProxyAtomic_MultiCast",
            OperationScope.Independent, "激发你最右侧的充能球X+1次。", new Dictionary<string, int>()));
        var plainXValue = RequireSingle(plainX.Values, _ => true, "MultiCast X value slot");
        var plusOneXValue = RequireSingle(plusOneX.Values, _ => true, "MultiCast X+1 value slot");
        if (plainXValue.Source != "energy_x" || plainXValue.Offset != 0
            || plusOneXValue.Source != "energy_x" || plusOneXValue.Offset != 1)
            throw new InvalidOperationException("Direct MultiCast structured execution does not preserve paid X and X+1 upgrades.");
        if (ChaosOperationExecutor.HandExhaustSelectionCount(2, 5) != 2
            || ChaosOperationExecutor.HandExhaustSelectionCount(3, 2) != 2
            || ChaosOperationExecutor.HandExhaustSelectionCount(0, 5) != 1
            || ChaosOperationExecutor.HandExhaustSelectionCount(2, 0) != 0)
            throw new InvalidOperationException("Hand-exhaust selection ignores its printed amount or available hand size.");
        if (ChaosOperationExecutor.ClampedSelectionBounds(2, 2, 5) != (2, 2)
            || ChaosOperationExecutor.ClampedSelectionBounds(3, 3, 2) != (2, 2)
            || ChaosOperationExecutor.ClampedSelectionBounds(0, 3, 2) != (0, 2))
            throw new InvalidOperationException("Hand-card selectors do not preserve printed exact/up-to counts.");
        var completedSelection = ChaosOperationExecutor.CompleteMandatorySelection(
            new[] { 1, 2, 3 }, new[] { 2 }, 2, 2);
        var optionalSelection = ChaosOperationExecutor.CompleteMandatorySelection(
            new[] { 1, 2, 3 }, Array.Empty<int>(), 0, 3);
        if (!completedSelection.SequenceEqual(new[] { 2, 1 }) || optionalSelection.Count != 0)
            throw new InvalidOperationException("Mandatory multi-card selection can still under-resolve its printed count.");
        if (!ChaosOperationExecutor.RecoveryRequiresCreation(matchingCount: 0, recoverableCount: 0)
            || ChaosOperationExecutor.RecoveryRequiresCreation(matchingCount: 1, recoverableCount: 0)
            || ChaosOperationExecutor.RecoveryRequiresCreation(matchingCount: 1, recoverableCount: 1))
            throw new InvalidOperationException("Slotted derivative recovery does not create a missing first copy safely.");
        var ghostSeedMarkerProbe = new SerializableCard();
        GhostSeedEtherealPersistence.WriteMarker(ghostSeedMarkerProbe);
        if (!GhostSeedEtherealPersistence.HasMarker(ghostSeedMarkerProbe))
            throw new InvalidOperationException("Ghost Seed Ethereal marker does not survive card serialization.");
        var selfStrengthLoss = OperationRuntimeSpecCompiler.CompileLegacy(new GeneratorOperation(
            "NCR:LoseStrength", OperationScope.NonTargeted, "失去2点力量。", new Dictionary<string, int>()));
        if (ChaosOperationExecutor.StructuredSelfPowerRoute(selfStrengthLoss) != "strength_loss")
            throw new InvalidOperationException(
                "Self Strength loss can be misrouted to an absent enemy target from a card-drawn trigger.");
        var temporarySelfStrength = new OperationRuntimeSpec(OperationRuntimeSpec.CurrentSchemaVersion,
            "apply_power", "strength_this_turn", "self", "none", "none", "none",
            Array.Empty<string>(), Array.Empty<RuntimeValueSlot>());
        if (ChaosOperationExecutor.StructuredSelfPowerRoute(temporarySelfStrength) != "strength_this_turn")
            throw new InvalidOperationException("Temporary self Strength can be misrouted as permanent Strength.");
        var unroutedSelfPowers = Enum.GetValues<GeneratedCharacter>()
            .SelectMany(character => CharacterComponentCatalogs.Get(character).Atoms)
            .Select(OperationRuntimeSpecCompiler.GetOrCompile)
            .Where(spec => spec is { Opcode: "apply_power", Target: "self" })
            .Where(spec => ChaosOperationExecutor.StructuredSelfPowerRoute(spec) is null)
            .Select(spec => spec.Variant)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(variant => variant, StringComparer.Ordinal)
            .ToArray();
        if (unroutedSelfPowers.Length > 0)
            throw new InvalidOperationException(
                $"Structured self Power routes are missing: {string.Join(", ", unroutedSelfPowers)}.");
        var firstAttackTrigger = StructuredProbe("A:when", OperationScope.AbilityTrigger,
            "每回合中，当你打出第1张攻击牌时。");
        if (!ChaosCompositePower.IsNthAttackPlayedThisTurnTrigger(firstAttackTrigger)
            || ChaosCompositePower.NthAttackPlayedThisTurnThreshold(firstAttackTrigger) != 1)
            throw new InvalidOperationException("Nth-Attack trigger ignores its generated threshold.");

        var colorless = CharacterComponentCatalogs.Get(GeneratedCharacter.Colorless);
        var goldAxe = RequireSingle(colorless.Recipes, recipe => recipe.Id == "GoldAxe",
            "Colorless/GoldAxe recipe");
        if (goldAxe.Atoms.Count != 1
            || goldAxe.Atoms[0].Template != "CL:DamageEqualCardsPlayedCombat"
            || OperationRuntimeSpecCompiler.GetOrCompile(goldAxe.Atoms[0]) is not
                { Opcode: "deal_damage", Variant: "cards_played_combat", Target: "selected_enemy", Values.Count: 0 })
            throw new InvalidOperationException("Gold Axe dynamic single-hit damage routing audit failed.");
        var mindBlast = RequireSingle(colorless.Recipes, recipe => recipe.Id == "MindBlast",
            "Colorless/MindBlast recipe");
        if (mindBlast.Atoms.Count != 2
            || !ChaosOperationExecutor.IsExternallyScaledDamageDependency(
                new GeneratorOperation(mindBlast.Atoms[0].Template, mindBlast.Atoms[0].Scope,
                    string.Empty, new Dictionary<string, int>(),
                    RuntimeSpec: OperationRuntimeSpecCompiler.GetOrCompile(mindBlast.Atoms[0])))
            || mindBlast.Atoms[1].Template != "T:D"
            || ChaosCardModel.ExternalDamageBonusKey(2) != -3
            || ChaosCardModel.ExternalDamageBonusIndex(-3) != 2)
            throw new InvalidOperationException("Mind Blast damage multiplier/powered-flat routing audit failed.");

        var beatDownRecipe = RequireSingle(colorless.Recipes, recipe => recipe.Id == "BeatDown",
            "Colorless/BeatDown recipe");
        var beatDown = RequireSingle(beatDownRecipe.Atoms, _ => true, "Colorless/BeatDown atom");
        if (!ChaosOperationExecutor.IsDirectDiscardAttackAutoplay(
                new GeneratorOperation(beatDown.Template, beatDown.Scope, beatDown.ChineseText,
                    new Dictionary<string, int>())))
            throw new InvalidOperationException("Beat Down discard-pile autoplay routing audit failed.");

        var corruption = RequireSingle(CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad).Recipes,
            recipe => recipe.Id == "Corruption", "Ironclad/Corruption recipe");
        var skillPlayed = RequireSingle(corruption.Atoms,
            atom => atom.ChineseText.Contains("打出一张技能牌", StringComparison.Ordinal),
            "Ironclad/Corruption skill-played trigger atom");
        if (skillPlayed.Template != "A:whenSkillPlayed" || skillPlayed.Scope != OperationScope.AbilityTrigger)
            throw new InvalidOperationException("Skill-played Power trigger routing audit failed.");

        var falseDefense = new GeneratedCard(0, GeneratedCardType.Skill, TargetMode.Other,
            GeneratedRarity.Basic, "你在接下来的2回合内无法再从卡牌中获得格挡。",
            Array.Empty<ChaosCardGenerator.CardTag>(),
            [new GeneratorOperation("CL:NoBlockFromCards", OperationScope.Independent,
                "你在接下来的2回合内无法再从卡牌中获得格挡。", new Dictionary<string, int>())]);
        var trueDefense = falseDefense with
        {
            ChineseDescription = "获得5点格挡。",
            Operations = [new GeneratorOperation("N:B", OperationScope.NonTargeted,
                "获得5点格挡。", new Dictionary<string, int>())]
        };
        var weakDefense = trueDefense with
        {
            Operations = [new GeneratorOperation("N:B", OperationScope.NonTargeted,
                "获得3点格挡。", new Dictionary<string, int>())]
        };
        var weakDamage = trueDefense with
        {
            Type = GeneratedCardType.Attack,
            Operations = [new GeneratorOperation("T:D", OperationScope.SingleEnemyOnly,
                "造成3点伤害。", new Dictionary<string, int>(), RequiresSingleTarget: true)]
        };
        var multiHitDamage = weakDamage with
        {
            Operations = [new GeneratorOperation("N:RandomD", OperationScope.NonTargeted,
                "随机对敌人造成2点伤害2次。", new Dictionary<string, int>())]
        };
        if (ChaosRunDefinitions.CountsAsStartingDefense(falseDefense)
            || ChaosRunDefinitions.CountsAsStartingDefense(weakDefense)
            || !ChaosRunDefinitions.CountsAsStartingDefense(trueDefense)
            || ChaosRunDefinitions.CountsAsStartingDamage(weakDamage)
            || !ChaosRunDefinitions.CountsAsStartingDamage(multiHitDamage))
            throw new InvalidOperationException(
                "Starting-deck combat coverage must require at least four total Damage/Block and ignore negative Block wording.");
    }

    private static T RequireSingle<T>(IEnumerable<T> source, Func<T, bool> predicate, string label)
    {
        var matches = source.Where(predicate).Take(3).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException(
                $"Execution routing audit expected exactly one {label}, found "
                + (matches.Length == 3 ? "at least 3" : matches.Length.ToString()) + ".");
    }

    private static void AuditRuntimeSpecs()
    {
        CatalogRuntimeSpecRegistry.ValidateCoverage();
        OperationRuntimeSpecCompiler.ValidateStructuredLocalizationIndependence();
    }

    private static void AuditCombatUpgradeOperations()
    {
        var catalogs = new[]
        {
            CharacterComponentCatalogs.Get(GeneratedCharacter.Ironclad),
            CharacterComponentCatalogs.Get(GeneratedCharacter.Silent),
            CharacterComponentCatalogs.Get(GeneratedCharacter.Defect),
            CharacterComponentCatalogs.Get(GeneratedCharacter.Necrobinder),
            CharacterComponentCatalogs.Get(GeneratedCharacter.Regent),
            CharacterComponentCatalogs.Get(GeneratedCharacter.Colorless)
        };
        var upgradeAtoms = catalogs.SelectMany(catalog => catalog.Recipes)
            .SelectMany(recipe => recipe.Atoms)
            .Where(atom => atom.ChineseText.Contains("升级", StringComparison.Ordinal))
            .ToArray();
        var templates = upgradeAtoms.Select(atom => atom.Template).ToHashSet(StringComparer.Ordinal);
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "I:Upgrade", "I:UpgradeThatCard", "NCR:UpgradeRandomDiscardCards"
        };
        if (upgradeAtoms.Length != 3 || !templates.SetEquals(expected))
            throw new InvalidOperationException("Combat card-upgrade component catalog changed without a routing audit: "
                                                + string.Join(", ", upgradeAtoms.Select(atom => atom.Template)));

        var handUpgrade = new GeneratorOperation("I:Upgrade", OperationScope.Independent,
            "升级手牌中的一张牌。", new Dictionary<string, int>());
        var movedUpgrade = new GeneratorOperation("I:UpgradeThatCard", OperationScope.Independent,
            "升级那张攻击牌。", new Dictionary<string, int>());
        var discardUpgrade = new GeneratorOperation("NCR:UpgradeRandomDiscardCards", OperationScope.NonTargeted,
            "随机升级弃牌堆中的2张牌。", new Dictionary<string, int>());
        if (!upgradeAtoms.All(atom => CardEffectRules.IsExistingCombatCardUpgrade(new GeneratorOperation(
                atom.Template, atom.Scope, atom.ChineseText, new Dictionary<string, int>())))
            || !CardEffectRules.OperationNeedsChoiceContext(handUpgrade)
            || CardEffectRules.OperationNeedsChoiceContext(movedUpgrade)
            || CardEffectRules.OperationNeedsChoiceContext(discardUpgrade))
            throw new InvalidOperationException("Combat card-upgrade choice-context routing audit failed.");
    }

    private static void AuditRunReplacement(bool auditPoolQuotas)
    {
        if (SeedBeforeLoadPatch.RequiresGeneratedPoolActivation(null, containsGeneratedCards: false)
            || SeedBeforeLoadPatch.RequiresGeneratedPoolActivation("   ", containsGeneratedCards: false)
            || !SeedBeforeLoadPatch.RequiresGeneratedPoolActivation(null, containsGeneratedCards: true)
            || !SeedBeforeLoadPatch.RequiresGeneratedPoolActivation("AA1:test", containsGeneratedCards: false))
            throw new InvalidOperationException("Vanilla/legacy generated-save activation audit failed.");

        var cases = new (GeneratedCharacter Generated, CharacterModel Character, Type PoolType, int Basics)[]
        {
            (GeneratedCharacter.Ironclad, ModelDb.Character<Ironclad>(), typeof(ChaosIroncladCardPool), ChaosRunDefinitions.BasicCount),
            (GeneratedCharacter.Silent, ModelDb.Character<Silent>(), typeof(ChaosSilentCardPool), ChaosRunDefinitions.SilentBasicCount),
            (GeneratedCharacter.Defect, ModelDb.Character<Defect>(), typeof(ChaosDefectCardPool), ChaosRunDefinitions.BasicCount),
            (GeneratedCharacter.Necrobinder, ModelDb.Character<Necrobinder>(), typeof(ChaosNecrobinderCardPool), ChaosRunDefinitions.BasicCount),
            (GeneratedCharacter.Regent, ModelDb.Character<Regent>(), typeof(ChaosRegentCardPool), ChaosRunDefinitions.BasicCount)
        };
        foreach (var entry in cases)
        {
            ChaosRunDefinitions.SelectActiveCharactersForStartupAudit([entry.Generated]);
            var deck = entry.Character.StartingDeck.ToArray();
            if (entry.Character.CardPool.GetType() != entry.PoolType
                || deck.Length != entry.Basics
                || deck.Any(card => card is not ChaosCardModel))
                throw new InvalidOperationException($"{entry.Generated} run-pool/starting-deck replacement audit failed.");
            if (auditPoolQuotas)
            {
                var generatedPool = ChaosRunDefinitions.GetCards(entry.Generated)
                    .Select(definition => definition.Card).ToArray();
                var ordinaryX = generatedPool.Count(SpecialXCardConverter.IsOrdinaryX);
                var specialX = generatedPool.Count(SpecialXCardConverter.IsSpecial);
                if (ordinaryX > 2 || specialX is < 1 or > 2 || ordinaryX + specialX < 2)
                    throw new InvalidOperationException(
                        $"{entry.Generated} X-cost quota audit failed: ordinary={ordinaryX}, special={specialX}.");
            }
        }
        ChaosRunDefinitions.SelectActiveCharactersForStartupAudit([GeneratedCharacter.Ironclad],
            replaceStartingCards: false);
        var originalStartingDeck = ModelDb.Character<Ironclad>().StartingDeck.ToArray();
        var mixedPool = ModelDb.Character<Ironclad>().CardPool.AllCards.ToArray();
        if (originalStartingDeck.Length == 0 || originalStartingDeck.Any(card => card is ChaosCardModel)
            || mixedPool.Where(card => card.Rarity == CardRarity.Basic).Any(card => card is ChaosCardModel)
            || mixedPool.Where(card => card.Rarity != CardRarity.Basic).Any(card => card is not ChaosCardModel))
            throw new InvalidOperationException("Disabled starting-card replacement did not preserve the original Basic cards and starting deck.");
        if (ChaosBasicCardAncientRelics.IsActive)
            throw new InvalidOperationException("Basic Strike/Defend relic overrides stayed active with the original starting deck.");

        ChaosRunDefinitions.SelectActiveCharactersForStartupAudit([GeneratedCharacter.Ironclad],
            replaceStartingCards: true, preserveOriginalCards: true);
        var preservedPool = ModelDb.Character<Ironclad>().CardPool.AllCards.ToArray();
        var expectedIroncladMultiplayer = ChaosRunDefinitions
            .OriginalCardsForPreservedPool(GeneratedCharacter.Ironclad)
            .Count(card => card.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly);
        var expectedColorlessMultiplayer = ChaosRunDefinitions
            .OriginalCardsForPreservedPool(GeneratedCharacter.Colorless)
            .Count(card => card.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly);
        var preservedColorlessPool = ModelDb.CardPool<ColorlessCardPool>().AllCards.ToArray();
        if (preservedPool.Any(card => card is ChaosCardModel && card.Rarity == CardRarity.Ancient)
            || preservedPool.Count(card => card is not ChaosCardModel && card.Rarity == CardRarity.Ancient)
                != ChaosRunDefinitions.AncientCount
            || preservedPool.Where(card => card.Rarity == CardRarity.Basic).Any(card => card is not ChaosCardModel)
            || expectedIroncladMultiplayer == 0
            || preservedPool.Count(card => card.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly)
                != expectedIroncladMultiplayer
            || expectedColorlessMultiplayer == 0
            || preservedColorlessPool.Count(card =>
                    card.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly)
                != expectedColorlessMultiplayer
            || !ChaosBasicCardAncientRelics.IsActive
            || ChaosRunDefinitions.GenerationCountFor(GeneratedCharacter.Ironclad)
                != ChaosRunDefinitions.CountFor(GeneratedCharacter.Ironclad) - ChaosRunDefinitions.AncientCount)
            throw new InvalidOperationException(
                "Preserved-original pool did not retain the complete vanilla pool, including multiplayer cards, "
                + "while replacing generated Ancients and retaining generated Basics.");

        ChaosRunDefinitions.SelectActiveCharactersForStartupAudit(
            [GeneratedCharacter.Ironclad, GeneratedCharacter.Silent]);
        var ironcladDeck = ModelDb.Character<Ironclad>().StartingDeck.ToArray();
        var silentDeck = ModelDb.Character<Silent>().StartingDeck.ToArray();
        var inactiveDeck = ModelDb.Character<Defect>().StartingDeck.ToArray();
        if (ironcladDeck.Any(card => card is not ChaosCardModel)
            || silentDeck.Any(card => card is not ChaosCardModel)
            || inactiveDeck.Any(card => card is ChaosCardModel)
            || !ChaosRunDefinitions.ActiveCharacters.SequenceEqual(
                new[] { GeneratedCharacter.Ironclad, GeneratedCharacter.Silent }.OrderBy(character => character)))
            throw new InvalidOperationException("Multiplayer active-character/starting-deck replacement audit failed.");
        var canonicalizedResumeSave = new SerializableRun { Modifiers = [] };
        SaveGeneratedPoolPatch.AttachSnapshot(canonicalizedResumeSave);
        var firstResumePayload = ChaosPoolSnapshot.ReadFrom(canonicalizedResumeSave);
        SaveGeneratedPoolPatch.AttachSnapshot(canonicalizedResumeSave);
        if (string.IsNullOrWhiteSpace(firstResumePayload)
            || !string.Equals(firstResumePayload, ChaosPoolSnapshot.ReadFrom(canonicalizedResumeSave),
                StringComparison.Ordinal)
            || canonicalizedResumeSave.Modifiers.Count(modifier =>
                modifier.Id == ModelDb.Modifier<ChaosPoolSnapshotModifier>().Id) != 1)
            throw new InvalidOperationException("Canonicalized multiplayer save snapshot reattachment audit failed.");
        foreach (var entry in cases)
            if (entry.Character.CardPool.GetType() != entry.PoolType)
                throw new InvalidOperationException($"Non-current {entry.Generated} pool was not replaced during a chaos run.");
        var colorless = ModelDb.CardPool<ColorlessCardPool>().AllCards.ToArray();
        if (colorless.Length != ChaosRunDefinitions.ColorlessCount
            || colorless.Any(card => card is not ChaosColorlessCardModel)
            || colorless.Any(card => card.GetType().Name == "Fasten"))
            throw new InvalidOperationException("Colorless run-pool replacement audit failed.");
        var basicCardRelics = new RelicModel[]
        {
            ModelDb.Relic<NeowsTalisman>().ToMutable(),
            ModelDb.Relic<LeafyPoultice>().ToMutable(),
            ModelDb.Relic<LargeCapsule>().ToMutable(),
            ModelDb.Relic<GhostSeed>().ToMutable(),
            ModelDb.Relic<PandorasBox>().ToMutable(),
            ModelDb.Relic<NutritiousSoup>().ToMutable(),
            ModelDb.Relic<PaelsClaw>().ToMutable()
        };
        foreach (var relic in basicCardRelics)
        {
            var description = relic.DynamicDescription.GetFormattedText();
            var eventDescription = relic.DynamicEventDescription.GetFormattedText();
            if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(eventDescription)
                || description.Contains('{') || eventDescription.Contains('{'))
                throw new InvalidOperationException($"{relic.Id} chaos relic-description audit failed.");
        }
        var tooltipRelics = basicCardRelics
            .Where(relic => relic is GhostSeed or PandorasBox or NutritiousSoup or PaelsClaw);
        if (tooltipRelics.Any(relic => !relic.HoverTipsExcludingRelic.Any()))
            throw new InvalidOperationException("Restored generated-card relic hover-tip audit failed.");
        var restoredRelics = new RelicModel[]
        {
            ModelDb.Relic<GhostSeed>(),
            ModelDb.Relic<PandorasBox>(),
            ModelDb.Relic<PaelsClaw>(),
            ModelDb.Relic<NutritiousSoup>()
        };
        if (restoredRelics.Any(relic => !relic.IsAllowed(NullRunState.Instance))
            || !ModelDb.Relic<StrikeDummy>().IsAllowed(NullRunState.Instance))
            throw new InvalidOperationException("Restored generated-card relic availability audit failed.");
        if (!MassiveScrollChaosAvailability.ShouldSuppress(runActive: true, preserveOriginalCards: false)
            || MassiveScrollChaosAvailability.ShouldSuppress(runActive: true, preserveOriginalCards: true)
            || MassiveScrollChaosAvailability.ShouldSuppress(runActive: false, preserveOriginalCards: false))
            throw new InvalidOperationException("Massive Scroll generated-pool availability audit failed.");
        if (!ModelDb.AncientEvent<Pael>().AllPossibleOptions.Any(option => option.Relic is PaelsClaw)
            || !ModelDb.AncientEvent<Tezcatara>().AllPossibleOptions.Any(option => option.Relic is NutritiousSoup))
            throw new InvalidOperationException("Restored generated-card Ancient relic-option audit failed.");
    }

    private static void AuditExternalCatalogs()
    {
        var characters = new[] { GeneratedCharacter.Defect, GeneratedCharacter.Necrobinder, GeneratedCharacter.Regent, GeneratedCharacter.Colorless };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var character in characters)
        {
            var catalog = CharacterComponentCatalogs.Get(character);
            CardNameGenerator.ValidateCatalog(catalog);
            var assembler = new ComponentAssemblyGenerator(new Random(20260820), character);
            var unreachable = catalog.Recipes.Where(recipe => !assembler.CanAssemble(recipe)).Select(recipe => recipe.Id).ToArray();
            if (unreachable.Length > 0)
                throw new InvalidOperationException($"{character} original-card assembly audit failed: {string.Join(", ", unreachable)}");
            var wholeCardProxies = catalog.AtomKeys.Where(key => key.Contains(":Proxy", StringComparison.Ordinal)
                && !key.Contains("ProxyAtomic_", StringComparison.Ordinal)
                && !key.Contains("ProxyDamage_Atomic_", StringComparison.Ordinal)).ToArray();
            if (wholeCardProxies.Length > 0)
                throw new InvalidOperationException($"{character} still contains non-atomic whole-card proxies: {string.Join(", ", wholeCardProxies)}");
            foreach (var key in catalog.AtomKeys.Where(key => key.Contains(":Proxy", StringComparison.Ordinal)))
                if (!seen.Add(key))
                    throw new InvalidOperationException($"Character-specific operation leaked across catalogs: {key}");
        }

        var regent = CharacterComponentCatalogs.Get(GeneratedCharacter.Regent).Recipes;
        var noStars = regent.Count(recipe => recipe.StarCost < 0 && !recipe.HasStarCostX);
        var starX = regent.Count(recipe => recipe.HasStarCostX);
        var fixedStars = regent.Count(recipe => recipe.StarCost > 0);
        if (noStars != 64 || starX != 1 || fixedStars != 21)
            throw new InvalidOperationException($"Regent star-cost distribution audit failed: none={noStars}, X={starX}, fixed={fixedStars}.");
    }

    private static void RunExternalStressAuditIfRequested()
    {
        if (!string.Equals(System.Environment.GetEnvironmentVariable("AUTOANTHONY_STRESS_AUDIT"), "1", StringComparison.Ordinal)) return;
        var characters = new[] { GeneratedCharacter.Defect, GeneratedCharacter.Necrobinder, GeneratedCharacter.Regent, GeneratedCharacter.Colorless };
        foreach (var character in characters)
        {
            RandomCardGenerator? generator = null;
            for (var index = 0; index < 10_000; index++)
            {
                if (index % 90 == 0)
                    generator = new RandomCardGenerator(character, 20260820 + (int)character * 997 + index);
                var card = generator!.Generate();
                CardTemplateValidator.Validate(card);
                if (card.Character != character)
                    throw new InvalidOperationException($"{character} stress audit generated a {card.Character} card.");
                var forbiddenPrefixes = character switch
                {
                    GeneratedCharacter.Defect => new[] { "NCR:", "R:" },
                    GeneratedCharacter.Necrobinder => new[] { "D:", "R:" },
                    GeneratedCharacter.Regent => new[] { "D:", "NCR:" },
                    GeneratedCharacter.Colorless => new[] { "D:", "NCR:", "R:" },
                    _ => Array.Empty<string>()
                };
                if (card.Operations.Any(operation => forbiddenPrefixes.Any(prefix =>
                        operation.Template.StartsWith(prefix, StringComparison.Ordinal))))
                    throw new InvalidOperationException($"{character} stress audit found a leaked character operation.");
            }
            Log.Info($"[AutoAnthony] {character} 10,000-card generation stress audit passed.");
        }
    }

    private static void DumpExternalNameCatalogIfRequested()
    {
        if (!string.Equals(System.Environment.GetEnvironmentVariable("AUTOANTHONY_DUMP_NAMES"), "1", StringComparison.Ordinal)) return;
        foreach (var character in new[] { GeneratedCharacter.Defect, GeneratedCharacter.Necrobinder, GeneratedCharacter.Regent, GeneratedCharacter.Colorless })
            foreach (var recipe in CharacterComponentCatalogs.Get(character).Recipes)
                Log.Info($"[AutoAnthonyName] {character}|{recipe.Id}|{recipe.ChineseTitle}|{recipe.EnglishTitle}");
    }

    private static void LogDistributionAuditIfRequested()
    {
        if (!string.Equals(System.Environment.GetEnvironmentVariable("AUTOANTHONY_DISTRIBUTION_AUDIT"), "1", StringComparison.Ordinal)) return;
        var characters = new[]
        {
            GeneratedCharacter.Silent, GeneratedCharacter.Defect,
            GeneratedCharacter.Necrobinder, GeneratedCharacter.Regent
            , GeneratedCharacter.Colorless
        };
        foreach (var character in characters)
        {
            var catalog = CharacterComponentCatalogs.Get(character);
            var rarities = character == GeneratedCharacter.Colorless
                ? new[] { GeneratedRarity.Uncommon, GeneratedRarity.Rare }
                : Enum.GetValues<GeneratedRarity>();
            foreach (var rarity in rarities)
            {
                RandomCardGenerator? generator = null;
                var cards = Enumerable.Range(0, 500).Select(index =>
                {
                    if (index % 90 == 0)
                        generator = new RandomCardGenerator(character,
                            20260820 + (int)character * 997 + (int)rarity * 10_000 + index);
                    return generator!.Generate(rarity);
                }).ToArray();
                var fixedCosts = cards.Where(card => card.Cost >= 0).Select(card => card.Cost).ToArray();
                var effects = cards.Select(card => card.Operations.Count(operation =>
                    !operation.Template.StartsWith("N_SELECT_", StringComparison.Ordinal))).ToArray();
                var source = catalog.Recipes.Where(recipe => recipe.OriginalRarity == rarity).ToArray();
                Log.Info($"[AutoAnthonyDistribution] {character}/{rarity}: "
                    + $"cost={fixedCosts.Average():0.00}, zero={cards.Count(card => card.Cost == 0) / 5d:0.0}%, "
                    + $"effects={effects.Average():0.00}; sourceCost={source.Where(recipe => recipe.Cost >= 0).Average(recipe => recipe.Cost):0.00}, "
                    + $"sourceEffects={source.Average(recipe => recipe.Atoms.Count):0.00}");
            }
        }
    }

    private static void Audit(GeneratedCharacter character)
    {
        ChaosStatPreview.Audit();
        var definitions = ChaosRunDefinitions.GetCards(character);
        DerivativePoolConstraintResolver.Audit(definitions.Select(definition => definition.Card).ToArray());
        if (character != GeneratedCharacter.Colorless)
        {
            var startingCards = definitions.Take(10).Select(definition => definition.Card).ToArray();
            var damageCoverage = startingCards.Count(ChaosRunDefinitions.CountsAsStartingDamage);
            var defenseCoverage = startingCards.Count(ChaosRunDefinitions.CountsAsStartingDefense);
            var highResourceCards = startingCards.Count(StartingPoolConstraintResolver.IsHighResourceCard);
            if (damageCoverage < 4 || defenseCoverage < 4)
                throw new InvalidOperationException($"{character} starting coverage audit failed: damage={damageCoverage}, defense={defenseCoverage}.");
            if (highResourceCards > StartingPoolConstraintResolver.MaximumHighResourceCards)
                throw new InvalidOperationException($"{character} starting resource audit failed: highResource={highResourceCards}.");
        }
        var generatedChineseNames = definitions.Select(definition => definition.Card.Name!.Chinese).ToArray();
        var generatedEnglishNames = definitions.Select(definition => definition.Card.Name!.English).ToArray();
        if (generatedChineseNames.Distinct(StringComparer.Ordinal).Count() != generatedChineseNames.Length
            || generatedEnglishNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != generatedEnglishNames.Length)
            throw new InvalidOperationException($"{character} generated duplicate card names within one pool.");
        var catalog = CharacterComponentCatalogs.Get(character);
        var originalChineseNames = catalog.Recipes.Select(recipe => recipe.ChineseTitle).ToHashSet(StringComparer.Ordinal);
        var originalEnglishNames = catalog.Recipes.Where(recipe => !string.IsNullOrWhiteSpace(recipe.EnglishTitle))
            .Select(recipe => recipe.EnglishTitle).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (generatedChineseNames.Any(originalChineseNames.Contains) || generatedEnglishNames.Any(originalEnglishNames.Contains))
            throw new InvalidOperationException($"{character} generated a card name identical to an original card.");

        var cards = Enumerable.Range(0, ChaosRunDefinitions.CountFor(character)).Select(slot => ChaosCardRegistry.Canonical(character, slot)).ToArray();
        var counts = cards.GroupBy(card => card.Rarity).ToDictionary(group => group.Key, group => group.Count());
        var valid = character == GeneratedCharacter.Colorless
            ? cards.Length == ChaosRunDefinitions.ColorlessCount
                && counts.GetValueOrDefault(CardRarity.Uncommon) == ChaosRunDefinitions.ColorlessUncommonCount
                && counts.GetValueOrDefault(CardRarity.Rare) == ChaosRunDefinitions.ColorlessRareCount
            : cards.Length == ChaosRunDefinitions.CountFor(character)
            && counts.GetValueOrDefault(MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Basic) == ChaosRunDefinitions.BasicCountFor(character)
            && counts.GetValueOrDefault(MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Common) == 20
            && counts.GetValueOrDefault(MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Uncommon) == 35
            && counts.GetValueOrDefault(MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Rare) == 25
            && counts.GetValueOrDefault(MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Ancient) == 2;
        if (!valid) throw new InvalidOperationException($"AutoAnthony {character} card slot rarity audit failed.");
        // This checks cross-color priority, per-pool uniqueness, and the two-way same-color Ancient assignment
        // once for the complete pool instead of rebuilding the source-art index for every individual card.
        ChaosRunDefinitions.AuditPortraitSelections(character, definitions);
        foreach (var canonical in cards)
        {
            var mutable = (ChaosCardModel)canonical.ToMutable();
            CardTemplateValidator.Validate(mutable.Generated,
                allowRandomizedNumericValues: ChaosRunDefinitions.ActiveNumericRandomMode);
            if (mutable.Generated.Operations.Any(operation => operation.Template.Any(character => character > 127)))
                throw new InvalidOperationException($"AutoAnthony operation ID is not ASCII-only for {canonical.Id}.");
            // Validate the actual SmartFormat template without calling GetDescriptionForPile: at this point custom
            // cards have not yet been attached to their pool, so EnergyIconHelper cannot be used safely.
            var chinese = LocManager.Instance.Language is "zhs" or "zht";
            var styledChinese = ChaosRuntimeDescriptionRenderer.Render(mutable, chinese: true);
            CardTextStyle.ValidateRenderedChinese(styledChinese);
            var styledEnglish = ChaosRuntimeDescriptionRenderer.Render(mutable, chinese: false);
            EnglishCardDescriptionRenderer.ValidateRenderedEnglish(styledEnglish);
            foreach (var derivativeUpgrade in mutable.Generated.Upgrade?.Effects.Where(effect =>
                         effect.Kind == CardUpgradeKind.UpgradeDerivative && effect.OperationIndex is not null)
                     ?? Enumerable.Empty<CardUpgradeEffect>())
            {
                var operation = mutable.Generated.Operations[derivativeUpgrade.OperationIndex!.Value];
                if (!DerivativeSlotCatalog.SupportsUpgrade(operation.Template, operation.DerivativeId))
                    continue;
                var derivativeName = DerivativeSlotCatalog.ChineseCardName(operation);
                if (!styledChinese.Contains("{IfUpgraded:show:", StringComparison.Ordinal)
                    || !styledChinese.Contains(derivativeName + "+", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"AutoAnthony derivative upgrade preview audit failed for {canonical.Id}: {styledChinese}");
            }
            if (mutable.Generated.Operations.Any(operation => operation.Template.StartsWith("N_SELECT_", StringComparison.Ordinal))
                && (styledChinese.Split('\n').Any(line => line is "选择你手牌中的一张牌。" or "选择你手牌中的一张攻击牌。")
                    || styledEnglish.Split('\n').Any(line => line is "Choose a card in your Hand." or "Choose an Attack in your Hand.")))
                throw new InvalidOperationException($"AutoAnthony internal card-slot selector leaked into card text for {canonical.Id}.");
            if (System.Text.RegularExpressions.Regex.IsMatch(styledChinese, @"(?m)^虚无(?:。|$)")
                || styledChinese.Contains("你的格挡不会在回合开始时移除", StringComparison.Ordinal)
                || styledChinese.Contains("获得等同于目标易伤层数的力量", StringComparison.Ordinal)
                || styledChinese.Contains("当此牌被消耗时", StringComparison.Ordinal))
                throw new InvalidOperationException($"AutoAnthony Chinese text style audit failed for {canonical.Id}: {styledChinese}");
            var descriptionTemplate = ChaosRuntimeDescriptionCache.GetFormatted(mutable, chinese);
            if (!ReferenceEquals(descriptionTemplate, ChaosRuntimeDescriptionCache.GetFormatted(mutable, chinese)))
                throw new InvalidOperationException($"AutoAnthony description-template cache missed for {canonical.Id}.");
            if (mutable.Generated.Operations.Any(CardEffectRules.IsEnergyGainOperation)
                && !descriptionTemplate.Contains(":energyIcons()}", StringComparison.Ordinal))
                throw new InvalidOperationException($"AutoAnthony energy icon template audit failed for {canonical.Id}.");
            for (var operationIndex = 0; operationIndex < mutable.Generated.Operations.Count; operationIndex++)
            {
                var operation = mutable.Generated.Operations[operationIndex];
                if (!CardEffectRules.IsEnemyDamage(operation)
                    || operation.Template.Contains(":Proxy", StringComparison.Ordinal)
                       && operation.Template != "T:ProxyDamage_Atomic_Poke"
                    || !ChaosOperationVariables.TryGetInitialValue(operation, out _))
                    continue;
                var variableName = ChaosOperationVariables.Name(operation, operationIndex);
                var expectsOstyDealer = operation.Template is "NCR:OstyDamage" or "NCR:OstyAllDamage"
                    or "T:ProxyDamage_Atomic_Poke";
                if (!mutable.DynamicVars.TryGetValue(variableName, out var variable)
                    || expectsOstyDealer && variable is not OstyDamageVar
                    || !expectsOstyDealer && variable is not DamageVar
                    || !descriptionTemplate.Contains("{" + variableName + ":", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"AutoAnthony damage variable binding audit failed for {canonical.Id}/{operation.Template}: "
                        + $"slot={variableName}; variable={variable?.GetType().Name ?? "missing"}; "
                        + $"template={descriptionTemplate.Replace('\n', '|')}.");
            }
            ChaosRuntimeDescriptionCache.InstallIfChanged(
                LocManager.Instance.GetTable("cards"), canonical.Id.Entry + ".description", descriptionTemplate);
            var formatProbe = new LocString("cards", canonical.Id.Entry + ".description");
            mutable.DynamicVars.AddTo(formatProbe);
            // The normal CardModel formatting path always supplies this selector. Keep it true here so startup
            // audits parse and resolve the combat-only statistical-preview branch as well as the ordinary text.
            formatProbe.Add("InCombat", true);
            formatProbe.Add(new IfUpgradedVar(UpgradeDisplay.Normal));
            var energyPrefix = character.ToString().ToLowerInvariant();
            formatProbe.Add("energyPrefix", energyPrefix);
            formatProbe.Add("singleStarIcon", "[img]res://images/packed/sprite_fonts/star_icon.png[/img]");
            foreach (var energyVar in mutable.DynamicVars.Values.OfType<EnergyVar>())
                energyVar.ColorPrefix = energyPrefix;
            var formattedProbe = formatProbe.GetFormattedText();
            if (System.Text.RegularExpressions.Regex.IsMatch(formattedProbe,
                    @"\{(?:Calculated(?:Block|Damage|Hits|Cards|Focus|Channels|Strength)|Damage|Block|Energy|HpLoss|Amount)\d+"))
                throw new InvalidOperationException($"AutoAnthony unresolved dynamic variable for {canonical.Id}: {formattedProbe}");
            // Materialize every generated hover tip while ModelDb and localization are live. This catches invalid
            // derivative/orb/power mappings at startup instead of waiting for the player to hover that exact card.
            _ = mutable.HoverTips.ToArray();
            mutable.ExtraDamage = 7;
            mutable.ExtraBlock = 6;
            var restored = (ChaosCardModel)CardModel.FromSerializable(mutable.ToSerializable());
            if (restored.ExtraDamage != 7 || restored.ExtraBlock != 6)
                throw new InvalidOperationException($"AutoAnthony card save round-trip audit failed for {canonical.Id}.");
        }
        if (character == GeneratedCharacter.Necrobinder)
        {
            var deckVersion = (ChaosCardModel)cards[0].ToMutable();
            var combatVersion = (ChaosCardModel)cards[0].ToMutable();
            combatVersion.DeckVersion = deckVersion;
            ChaosOperationExecutor.IncreaseCardDamageForRun(combatVersion, 8);
            if (combatVersion.ExtraDamage != 8 || deckVersion.ExtraDamage != 8)
                throw new InvalidOperationException("Run-persistent card damage did not propagate to the deck version.");
        }
        if (character == GeneratedCharacter.Defect)
        {
            var deckVersion = (ChaosCardModel)cards[0].ToMutable();
            var combatVersion = (ChaosCardModel)cards[0].ToMutable();
            combatVersion.DeckVersion = deckVersion;
            ChaosOperationExecutor.IncreaseCardBlockForRun(combatVersion, 5);
            if (combatVersion.ExtraBlock != 5 || deckVersion.ExtraBlock != 5)
                throw new InvalidOperationException("Run-persistent card Block did not propagate to the deck version.");
        }
        var powerSource = cards.Cast<ChaosCardModel>().FirstOrDefault(candidate =>
            ChaosCompositePower.DescriptionOperations(candidate.Generated.Operations).Count > 0);
        if (powerSource is null) return;
        var previewPower = (ChaosCompositePower)ModelDb.Power<ChaosCompositePower>().ToMutable();
        previewPower.Configure(character, powerSource.Definition.Slot, false, false,
            resolvedEnergyXValue: 3, resolvedStarXValue: 3);
        var formattedPowerDescription = previewPower.Description.GetFormattedText();
        if (string.IsNullOrWhiteSpace(formattedPowerDescription)
            || formattedPowerDescription.Contains("{description}", StringComparison.Ordinal))
            throw new InvalidOperationException("AutoAnthony power description formatting audit failed.");
        if (!ResourceLoader.Exists(previewPower.Definition.PowerIconPath)
            || !ResourceLoader.Exists(previewPower.Definition.PowerBigIconPath))
            throw new InvalidOperationException("AutoAnthony power icon audit failed.");
    }
}

[HarmonyPatch(typeof(NGame), nameof(NGame.StartNewSingleplayerRun))]
internal static class SeedBeforeSingleplayerPatch
{
    private static int _callingOriginal;

    private static bool Prefix(
        NGame __instance,
        CharacterModel character,
        bool shouldSave,
        IReadOnlyList<ActModel> acts,
        IReadOnlyList<ModifierModel> modifiers,
        string seed,
        GameMode gameMode,
        int ascensionLevel,
        DateTimeOffset? dailyTime,
        ref Task<RunState> __result)
    {
        if (Volatile.Read(ref _callingOriginal) > 0) return true;
        if (!ChaosModSettings.Enabled)
        {
            SurpriseCardKnowledge.BeginNewRun();
            ChaosRunDefinitions.DeactivateRun();
            return true;
        }
        var generatedCharacter = ChaosCharacterMapping.From(character);
        if (!generatedCharacter.HasValue)
        {
            SurpriseCardKnowledge.BeginNewRun();
            ChaosRunDefinitions.DeactivateRun();
            return true;
        }

        __result = StartAfterGeneration(__instance, generatedCharacter.Value, character, shouldSave, acts,
            modifiers, seed, gameMode, ascensionLevel, dailyTime);
        return false;
    }

    private static async Task<RunState> StartAfterGeneration(
        NGame game,
        GeneratedCharacter generatedCharacter,
        CharacterModel character,
        bool shouldSave,
        IReadOnlyList<ActModel> acts,
        IReadOnlyList<ModifierModel> modifiers,
        string seed,
        GameMode gameMode,
        int ascensionLevel,
        DateTimeOffset? dailyTime)
    {
        SurpriseCardKnowledge.BeginNewRun();
        ChaosGenerationProgressOverlay? generationProgress = null;
        try
        {
            generationProgress = await ChaosRunDefinitions.ActivateAsync(generatedCharacter, seed);
        }
        catch (Exception exception)
        {
            // Run generation is optional mod work performed before the vanilla async start. Never fault that outer
            // task: NCharacterSelect has already entered its transition state and does not recover from an exception,
            // which presents as a permanent black screen. ActivateAsync has restored the prior runtime state; make
            // this one run vanilla and retain the full exception in the log.
            ChaosRunDefinitions.DeactivateRun();
            Log.Error($"[AutoAnthony] Card-pool generation failed; starting this run with original card pools instead: {exception}");
        }
        using var progress = generationProgress;
        if (ChaosRunDefinitions.IsRunActive)
        {
            ChaosCardDiscovery.ResetGeneratedCardsForNewRun();
            ChaosPoolSnapshot.PrimeRunPayload(ChaosRunDefinitions.ActiveCharacters,
                ChaosRunDefinitions.ActiveSeed, ChaosRunDefinitions.GetAllCards());
            Log.Info($"[AutoAnthony] Activated singleplayer generated-card pools: "
                     + $"character={generatedCharacter}, seed={ChaosRunDefinitions.ActiveSeed}, "
                     + $"UltimateChaos={ChaosRunDefinitions.ActiveUltimateChaos}, "
                     + $"ReplaceStartingCards={ChaosRunDefinitions.ActiveReplaceStartingCards}, "
                     + $"NumericBalanceOptimization={ChaosRunDefinitions.ActiveNumericBalanceOptimization}, "
                     + $"NumericRandom={ChaosRunDefinitions.ActiveNumericRandomMode}, "
                     + $"PreserveOriginal={ChaosRunDefinitions.ActivePreserveOriginalCards}.");
        }

        Task<RunState> originalTask;
        Interlocked.Increment(ref _callingOriginal);
        try
        {
            originalTask = game.StartNewSingleplayerRun(character, shouldSave, acts, modifiers, seed, gameMode,
                ascensionLevel, dailyTime);
        }
        finally
        {
            Interlocked.Decrement(ref _callingOriginal);
        }
        var runState = await originalTask;
        SurpriseCardKnowledge.RecordOwnedCards(runState);
        return runState;
    }
}

internal static class ChaosCharacterMapping
{
    internal static GeneratedCharacter? From(CharacterModel character) => character switch
    {
        Ironclad => GeneratedCharacter.Ironclad,
        Silent => GeneratedCharacter.Silent,
        Defect => GeneratedCharacter.Defect,
        Necrobinder => GeneratedCharacter.Necrobinder,
        Regent => GeneratedCharacter.Regent,
        _ => null
    };

    internal static GeneratedCharacter[] From(SerializableRun save) => save.Players
        .Select(player => player.CharacterId == ModelDb.Character<Ironclad>().Id ? GeneratedCharacter.Ironclad
            : player.CharacterId == ModelDb.Character<Silent>().Id ? GeneratedCharacter.Silent
            : player.CharacterId == ModelDb.Character<Defect>().Id ? GeneratedCharacter.Defect
            : player.CharacterId == ModelDb.Character<Necrobinder>().Id ? GeneratedCharacter.Necrobinder
            : player.CharacterId == ModelDb.Character<Regent>().Id ? GeneratedCharacter.Regent
            : (GeneratedCharacter?)null)
        .Where(character => character.HasValue).Select(character => character!.Value)
        .Distinct().OrderBy(character => character).ToArray();

    internal static GeneratedCharacter[] From(RunHistory history) => history.Players
        .Select(player => player.Character == ModelDb.Character<Ironclad>().Id ? GeneratedCharacter.Ironclad
            : player.Character == ModelDb.Character<Silent>().Id ? GeneratedCharacter.Silent
            : player.Character == ModelDb.Character<Defect>().Id ? GeneratedCharacter.Defect
            : player.Character == ModelDb.Character<Necrobinder>().Id ? GeneratedCharacter.Necrobinder
            : player.Character == ModelDb.Character<Regent>().Id ? GeneratedCharacter.Regent
            : (GeneratedCharacter?)null)
        .Where(character => character.HasValue).Select(character => character!.Value)
        .Distinct().OrderBy(character => character).ToArray();
}

[HarmonyPatch(typeof(NGame), nameof(NGame.StartNewMultiplayerRun))]
internal static class SeedBeforeMultiplayerPatch
{
    private static int _callingOriginal;

    private static bool Prefix(
        NGame __instance,
        StartRunLobby lobby,
        bool shouldSave,
        IReadOnlyList<ActModel> acts,
        IReadOnlyList<ModifierModel> modifiers,
        string seed,
        int ascensionLevel,
        DateTimeOffset? dailyTime,
        ref Task<RunState> __result)
    {
        if (Volatile.Read(ref _callingOriginal) > 0) return true;
        IReadOnlyList<ModifierModel> effectiveModifiers = modifiers;
        if (!modifiers.OfType<ChaosPoolSnapshotModifier>()
                .Any(snapshot => snapshot.MultiplayerGenerationModeSpecified)
            && MultiplayerGenerationMarkerTransportPatch.TryTake(lobby, out var transportedMarker)
            && transportedMarker is not null)
        {
            effectiveModifiers = modifiers.Append(transportedMarker).ToArray();
            Log.Info("[AutoAnthony] Reattached the host multiplayer generation carrier at NGame run creation.");
        }
        __result = StartAfterGeneration(__instance, lobby, shouldSave, acts, effectiveModifiers, seed, ascensionLevel,
            dailyTime);
        return false;
    }

    private static async Task<RunState> StartAfterGeneration(
        NGame game,
        StartRunLobby lobby,
        bool shouldSave,
        IReadOnlyList<ActModel> acts,
        IReadOnlyList<ModifierModel> modifiers,
        string seed,
        int ascensionLevel,
        DateTimeOffset? dailyTime)
    {
        SurpriseCardKnowledge.BeginNewRun();
        var generationMarker = modifiers.OfType<ChaosPoolSnapshotModifier>()
            .LastOrDefault(snapshot => snapshot.MultiplayerGenerationModeSpecified);
        var enabled = generationMarker?.MultiplayerModEnabled ?? ChaosModSettings.Enabled;
        var ultimateChaos = generationMarker?.MultiplayerUltimateChaos
                            ?? ChaosModSettings.EffectiveUltimateChaos;
        var replaceStartingCards = generationMarker?.MultiplayerReplaceStartingCardsSpecified == true
            ? generationMarker.MultiplayerReplaceStartingCards
            : generationMarker is null ? ChaosModSettings.ReplaceStartingCards : true;
        var numericBalanceOptimization = generationMarker?.MultiplayerNumericBalanceOptimizationSpecified == true
            ? generationMarker.MultiplayerNumericBalanceOptimization
            : generationMarker is null ? ChaosModSettings.EffectiveNumericBalanceOptimization : true;
        var numericRandomMode = generationMarker?.MultiplayerNumericRandomMode
                                ?? ChaosModSettings.EffectiveNumericRandomMode;
        var preserveOriginalCards = generationMarker?.MultiplayerPreserveOriginalCards
                                    ?? ChaosModSettings.PreserveOriginalCards;
        var randomCardArt = generationMarker?.MultiplayerRandomCardArtSpecified == true
            ? generationMarker.MultiplayerRandomCardArt
            : ChaosModSettings.RandomCardArt;
        if (generationMarker is not null && enabled != ChaosModSettings.Enabled)
            Log.Info($"[AutoAnthony] Using host multiplayer enabled setting instead of the local setting: Enabled={enabled}.");
        if (generationMarker is not null && ultimateChaos != ChaosModSettings.UltimateChaos)
            Log.Info($"[AutoAnthony] Using host multiplayer generation mode instead of the local setting: UltimateChaos={ultimateChaos}.");
        if (generationMarker?.MultiplayerReplaceStartingCardsSpecified == true
            && replaceStartingCards != ChaosModSettings.ReplaceStartingCards)
            Log.Info($"[AutoAnthony] Using host multiplayer starting-card setting instead of the local setting: ReplaceStartingCards={replaceStartingCards}.");
        if (generationMarker?.MultiplayerNumericBalanceOptimizationSpecified == true
            && numericBalanceOptimization != ChaosModSettings.NumericBalanceOptimization)
            Log.Info($"[AutoAnthony] Using host multiplayer numeric setting instead of the local setting: NumericBalanceOptimization={numericBalanceOptimization}.");
        var characters = lobby.Players.Select(player => ChaosCharacterMapping.From(player.character))
            .Where(character => character.HasValue).Select(character => character!.Value).ToArray();
        ChaosGenerationProgressOverlay? generationProgress = null;
        if (enabled && characters.Length > 0 && !string.IsNullOrEmpty(generationMarker?.PoolSnapshot))
        {
            var hostPayload = generationMarker.PoolSnapshot;
            var fingerprint = ChaosPoolSnapshot.MultiplayerFingerprint(hostPayload);
            var reboundPayload = ChaosPoolSnapshot.RebindMultiplayerActiveCharacters(hostPayload, characters, seed);
            var report = ChaosRunDefinitions.ActivateFromSave(characters, seed, reboundPayload);
            if (report.RegeneratedCards != 0)
                throw new InvalidDataException(
                    $"The authoritative multiplayer pool snapshot required {report.RegeneratedCards} regenerated cards.");
            generationProgress = MultiplayerGenerationModePatch.TakePreparedProgress(lobby);
            Log.Info($"[AutoAnthony] Restored host-authoritative multiplayer pools: fingerprint={fingerprint}, cards={report.RestoredCards}.");
        }
        else if (enabled && characters.Length > 0)
        {
            generationProgress = await ChaosRunDefinitions.ActivateAsync(characters, seed, ultimateChaos,
                replaceStartingCards, numericBalanceOptimization, numericRandomMode, preserveOriginalCards,
                randomCardArt);
            // The host generated these exact pools before broadcasting the start message, so ActivateAsync can
            // legitimately reuse them without returning a new overlay. Preserve the original host overlay until
            // vanilla finishes entering the first room.
            generationProgress ??= MultiplayerGenerationModePatch.TakePreparedProgress(lobby);
            if (!string.IsNullOrWhiteSpace(generationMarker?.MultiplayerGenerationFingerprint))
            {
                var localFingerprint = ChaosPoolSnapshot.MultiplayerGameplayFingerprint(
                    ChaosRunDefinitions.GetAllCards());
                if (!string.Equals(localFingerprint, generationMarker.MultiplayerGenerationFingerprint,
                        StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Generated multiplayer card pools differ from the host (host={generationMarker.MultiplayerGenerationFingerprint}, local={localFingerprint}). Ensure every player uses the same AutoAnthony version and gameplay component mods.");
                Log.Info($"[AutoAnthony] Verified deterministic multiplayer pools: fingerprint={localFingerprint}.");
            }
            else
                Log.Warn("[AutoAnthony] The host did not provide a generated-pool fingerprint; using deterministic peer generation for compatibility with an older version.");
        }
        using var progress = generationProgress;
        if (enabled && ChaosRunDefinitions.IsRunActive)
        {
            ChaosCardDiscovery.ResetGeneratedCardsForNewRun();
            ChaosPoolSnapshot.PrimeRunPayload(ChaosRunDefinitions.ActiveCharacters,
                ChaosRunDefinitions.ActiveSeed, ChaosRunDefinitions.GetAllCards());
            Log.Info($"[AutoAnthony] Activated multiplayer generated-card pools for [{string.Join(", ", ChaosRunDefinitions.ActiveCharacters)}] with seed {ChaosRunDefinitions.ActiveSeed}.");
        }
        else
        {
            ChaosRunDefinitions.DeactivateRun();
            Log.Info("[AutoAnthony] Multiplayer card-pool replacement is disabled for this run.");
        }

        Task<RunState> originalTask;
        Interlocked.Increment(ref _callingOriginal);
        try
        {
            var runModifiers = modifiers.Where(modifier => modifier is not ChaosPoolSnapshotModifier snapshot
                                                            || !snapshot.MultiplayerGenerationModeSpecified)
                .ToArray();
            originalTask = game.StartNewMultiplayerRun(lobby, shouldSave, acts, runModifiers, seed, ascensionLevel,
                dailyTime);
        }
        finally
        {
            Interlocked.Decrement(ref _callingOriginal);
        }
        var runState = await originalTask;
        SurpriseCardKnowledge.RecordOwnedCards(runState);
        return runState;
    }
}

[HarmonyPatch(typeof(ColorfulPhilosophers), "GenerateInitialOptions")]
internal static class ColorfulPhilosophersChaosPoolPatch
{
    private static readonly System.Reflection.MethodInfo OfferRewardsMethod =
        AccessTools.Method(typeof(ColorfulPhilosophers), "OfferRewards", [typeof(CardPoolModel)]);

    private static bool Prefix(ColorfulPhilosophers __instance, ref IReadOnlyList<EventOption> __result)
    {
        if (!ChaosRunDefinitions.IsRunActive || __instance.Owner is not { } owner) return true;

        // Character.CardPool is replaced with its generated counterpart during a run. The vanilla event compares
        // those generated pools against a hard-coded list of vanilla pool instances and consequently finds no
        // matches. Rebuild the same ordered choices entirely in generated-pool space.
        var unlocked = owner.UnlockState.CharacterCardPools.ToHashSet();
        var ownPool = owner.Character.CardPool;
        var options = OrderedChaosPools()
            .Where(pool => pool != ownPool && unlocked.Contains(pool))
            .Select(pool => new EventOption(__instance,
                () => OfferRewards(__instance, pool),
                "COLORFUL_PHILOSOPHERS.pages.INITIAL.options." + pool.EnergyColorName.ToUpperInvariant()))
            .ToList();
        while (options.Count > Math.Min(3, options.Count))
            options.RemoveAt(__instance.Rng.NextInt(options.Count));
        __result = options;
        return false;
    }

    private static IEnumerable<CardPoolModel> OrderedChaosPools()
    {
        yield return ModelDb.CardPool<ChaosNecrobinderCardPool>();
        yield return ModelDb.CardPool<ChaosIroncladCardPool>();
        yield return ModelDb.CardPool<ChaosRegentCardPool>();
        yield return ModelDb.CardPool<ChaosSilentCardPool>();
        yield return ModelDb.CardPool<ChaosDefectCardPool>();
        foreach (var pool in ExternalComponentCharacterApi.GetActiveCardPools())
            yield return pool;
    }

    private static Task OfferRewards(ColorfulPhilosophers instance, CardPoolModel pool) =>
        (Task)(OfferRewardsMethod.Invoke(instance, [pool])
            ?? throw new InvalidOperationException("Colorful Philosophers reward task was null."));
}

[HarmonyPatch(typeof(TheFutureOfPotions), "get_PotionToCardType")]
internal static class FutureOfPotionsChaosRewardCoveragePatch
{
    private static void Postfix(TheFutureOfPotions __instance,
        Dictionary<PotionModel, CardType> __result)
    {
        if (!ChaosRunDefinitions.IsRunActive || __instance.Owner is not { } owner) return;
        var poolCards = owner.Character.CardPool.GetUnlockedCards(owner.UnlockState,
            owner.RunState.CardMultiplayerConstraint).ToArray();
        foreach (var potion in __result.Keys.ToArray())
        {
            var rarity = potion.Rarity switch
            {
                MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Rare
                    or MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Event => CardRarity.Rare,
                MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Uncommon => CardRarity.Uncommon,
                _ => CardRarity.Common
            };
            if (poolCards.Any(card => card.Rarity == rarity && card.Type == __result[potion])) continue;
            var allowedTypes = potion.Rarity is MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Common
                    or MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Token
                ? new[] { CardType.Attack, CardType.Skill }
                : new[] { CardType.Attack, CardType.Skill, CardType.Power };
            var fallback = allowedTypes.FirstOrDefault(type =>
                poolCards.Any(card => card.Rarity == rarity && card.Type == type));
            if (!poolCards.Any(card => card.Rarity == rarity && card.Type == fallback))
            {
                Log.Error($"[AutoAnthony] The Future of Potions found no {rarity} card in the active generated pool.");
                continue;
            }
            Log.Warn($"[AutoAnthony] The Future of Potions replaced unavailable {rarity} {__result[potion]} "
                     + $"with {fallback} for the active generated pool.");
            __result[potion] = fallback;
        }
    }
}

[HarmonyPatch(typeof(RunState), nameof(RunState.FromSerializable))]
internal static class SeedBeforeLoadPatch
{
    private static void Prefix(SerializableRun save)
    {
        PrepareSavedRun(save, "RunState.FromSerializable");
    }

    internal static void PrepareSavedRun(SerializableRun save, string source)
    {
        // RunState.FromSerializable can be called repeatedly with the same SerializableRun (combat checkpoint,
        // return-to-menu, then Continue). Loading must not consume the marker from that shared object.
        var snapshot = ChaosPoolSnapshot.ReadFrom(save);
        SurpriseCardKnowledge.RestoreFrom(save, ChaosPoolSnapshot.ReadSurpriseKnowledge(save));
        var characters = ChaosCharacterMapping.From(save);
        if (characters.Length == 0)
        {
            ChaosRunDefinitions.DeactivateRun();
            return;
        }

        var containsGeneratedCards = save.Players.SelectMany(player => player.Deck)
            .Any(card => card.Id is { } id && ChaosCardRegistry.IsGeneratedCardId(id));
        if (!RequiresGeneratedPoolActivation(snapshot, containsGeneratedCards))
        {
            ChaosRunDefinitions.DeactivateRun();
            Log.Info($"[AutoAnthony] Treated {source} as a vanilla run because it contains neither an "
                     + "AutoAnthony pool snapshot nor generated card IDs.");
            return;
        }

        var runtimeSeed = save.SerializableRng.Seed ?? string.Empty;
        var seed = ChaosPoolSnapshot.ResolveGeneratedPoolSeed(snapshot, runtimeSeed);
        // SaveRun internally materializes a temporary RunState from the SerializableRun it has just created.
        // That object carries the exact immutable snapshot already active in this process; decoding and validating
        // all 514 definitions again added about two seconds to every combat victory autosave.
        if (ChaosRunDefinitions.IsRunActive
            && ChaosRunDefinitions.ActiveSeed == seed
            && ChaosRunDefinitions.ActiveCharacters.SequenceEqual(characters)
            && ChaosPoolSnapshot.IsCachedRunPayload(characters, seed,
                ChaosRunDefinitions.GetAllCards(), snapshot))
            return;

        var report = ChaosRunDefinitions.ActivateFromSave(characters,
            seed, snapshot);
        ChaosPoolSnapshot.PrimeRunPayload(ChaosRunDefinitions.ActiveCharacters,
            ChaosRunDefinitions.ActiveSeed, ChaosRunDefinitions.GetAllCards());
        Log.Info($"[AutoAnthony] Restored generated-card pools for [{string.Join(", ", characters)}]: "
            + $"source={source}, "
            + (string.Equals(runtimeSeed, seed, StringComparison.Ordinal)
                ? string.Empty
                : $"runtimeSeed={runtimeSeed}, poolSeed={seed}, ")
            + $"savedVersion={report.SavedVersion}, currentVersion={ChaosPoolSnapshot.ModVersion}, "
            + $"kept={report.RestoredCards}, regenerated={report.RegeneratedCards}"
            + (report.Failure is null ? string.Empty : $", note={report.Failure}"));
    }

    internal static bool RequiresGeneratedPoolActivation(string? snapshot, bool containsGeneratedCards) =>
        !string.IsNullOrWhiteSpace(snapshot) || containsGeneratedCards;

    private static void Postfix(RunState __result)
    {
        // The marker only transports the compact pool snapshot. Keep it in the SerializableRun for any later
        // reload of that object, but remove its mutable model from ordinary runtime modifier hooks and UI.
        var modifiers = __result.Modifiers.Where(modifier => modifier is not ChaosPoolSnapshotModifier).ToArray();
        if (modifiers.Length != __result.Modifiers.Count)
            AccessTools.Property(typeof(RunState), nameof(RunState.Modifiers)).SetValue(__result, modifiers);
    }
}

/// <summary>
/// A resumed multiplayer run exists as a LoadRunLobby before RunState.FromSerializable is called. Restore the
/// host's immutable card-pool snapshot as soon as that lobby is constructed so deck previews, card tooltips and
/// any mod hooks raised while players ready up cannot resolve generated card ids through a stale local preview
/// pool. Both LoadRunLobby constructors are patched; the client constructor chains the common constructor, and
/// the snapshot cache makes the second call a cheap identity check.
/// </summary>
[HarmonyPatch]
internal static class MultiplayerLoadLobbySnapshotPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
        typeof(LoadRunLobby).GetConstructors();

    private static void Postfix(LoadRunLobby __instance) =>
        SeedBeforeLoadPatch.PrepareSavedRun(__instance.Run, "multiplayer load lobby");
}

/// <summary>
/// Live reconnect packets contain the authoritative SerializableRun followed by the combat checkpoint. Activate
/// the saved pools before the response reaches any reconstruction code; otherwise CardState ids in the checkpoint
/// can briefly resolve against whatever preview/run pool happened to be active on the reconnecting client.
/// </summary>
[HarmonyPatch(typeof(ClientRejoinResponseMessage), nameof(ClientRejoinResponseMessage.Deserialize))]
internal static class MultiplayerRejoinSnapshotPatch
{
    private static void Postfix(ref ClientRejoinResponseMessage __instance) =>
        SeedBeforeLoadPatch.PrepareSavedRun(__instance.serializableRun, "multiplayer live rejoin packet");
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.ToSave))]
internal static class SaveGeneratedPoolPatch
{
    private static void Postfix(ref SerializableRun __result)
    {
        AttachSnapshot(__result);
    }

    internal static void AttachSnapshot(SerializableRun save)
    {
        var characters = ChaosRunDefinitions.ActiveCharacters;
        if (characters.Count == 0) return;
        // Defensive replacement keeps repeated ToSave calls and combat replays from accumulating markers.
        _ = ChaosPoolSnapshot.ExtractFrom(save);
        save.Modifiers.Add(ChaosPoolSnapshot.ToCachedSerializableModifier(characters,
            ChaosRunDefinitions.ActiveSeed, ChaosRunDefinitions.GetAllCards(),
            SurpriseCardKnowledge.ToSavePayload()));
    }
}

/// <summary>
/// The live SerializableRun carries all six generated pools so continuing and multiplayer reconnects remain
/// authoritative. Run history is immutable and only renders the players' final decks, so replace that full marker
/// at the dedicated history-save boundary with a sparse marker containing exactly those referenced definitions.
/// </summary>
[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveRunHistory))]
internal static class CompactGeneratedPoolHistoryPatch
{
    [ThreadStatic] private static bool _refreshOptimizationSignature;

    private static void Prefix(RunHistory history)
    {
        _refreshOptimizationSignature = false;
        try
        {
            var snapshotId = ModelDb.Modifier<ChaosPoolSnapshotModifier>().Id;
            if (!history.Modifiers.Any(modifier => modifier.Id == snapshotId))
            {
                _refreshOptimizationSignature = true;
                return;
            }

            var activeCharacters = ChaosCharacterMapping.From(history);
            if (activeCharacters.Length == 0 || !ChaosRunDefinitions.IsRunActive
                                             || ChaosRunDefinitions.ActiveSeed != history.Seed) return;

            var finalDeckIds = history.Players
                .SelectMany(player => player.Deck)
                .Select(card => card.Id)
                .Where(id => id is not null)
                .Select(id => id!)
                .Distinct()
                .ToArray();
            var compact = ChaosPoolSnapshot.ToHistorySerializableModifier(activeCharacters, history.Seed,
                ChaosRunDefinitions.GetAllCards(), finalDeckIds, out var savedCardCount);

            history.Modifiers.RemoveAll(modifier => modifier.Id == snapshotId);
            if (compact is not null) history.Modifiers.Add(compact);
            _refreshOptimizationSignature = true;
            Log.Info($"[AutoAnthony] Compacted run-history snapshot to {savedCardCount} generated final-deck card definitions "
                     + $"across {history.Players.Count} player(s).");
        }
        catch (Exception exception)
        {
            // History creation must never prevent a run from ending. The original full snapshot remains attached
            // because replacement happens only after the compact payload has been built successfully.
            Log.Warn($"[AutoAnthony] Could not compact the run-history snapshot; preserving the full snapshot: {exception}");
        }
    }

    private static void Postfix()
    {
        if (_refreshOptimizationSignature)
            HistorySnapshotOptimizer.RefreshCompletedProfileSignatureAfterHistorySave();
        _refreshOptimizationSignature = false;
    }
}

/// <summary>
/// Multiplayer resume canonicalizes the host's save by materializing a RunState and serializing it again. The
/// runtime RunState intentionally excludes ChaosPoolSnapshotModifier, so vanilla's rebuilt SerializableRun would
/// otherwise silently lose the authoritative six-pool snapshot before the load lobby sends it to other players.
/// Reattach the immutable marker to the canonicalized result just as RunManager.ToSave does.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.CanonicalizeSave))]
internal static class CanonicalizedMultiplayerSnapshotPatch
{
    private static void Postfix(ref SerializableRun __result) =>
        SaveGeneratedPoolPatch.AttachSnapshot(__result);
}

[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.Add),
    [typeof(IEnumerable<CardModel>), typeof(CardPile), typeof(CardPilePosition), typeof(AbstractModel),
        typeof(bool), typeof(bool)])]
internal static class SurpriseModeTrackSeenCardsPatch
{
    private static void Postfix(ref Task<IReadOnlyList<CardPileAddResult>> __result)
    {
        __result = RecordAfterAdd(__result);
    }

    private static async Task<IReadOnlyList<CardPileAddResult>> RecordAfterAdd(
        Task<IReadOnlyList<CardPileAddResult>> task)
    {
        var results = await task;
        SurpriseCardKnowledge.RecordSuccessfulAdds(results);
        if (ChaosModSettings.SurpriseModePro)
        {
            // The pile node may have rendered once before the successful add was recorded. Refresh cards already
            // visible on the combat table so their cost appears immediately; the Pro description remains concealed
            // by the NCard.UpdateVisuals postfix.
            foreach (var result in results)
            {
                if (!result.success || !ChaosCardRegistry.IsGeneratedCardId(result.cardAdded.Id)) continue;
                var node = NCard.FindOnTable(result.cardAdded);
                node?.UpdateVisuals(result.cardAdded.Pile?.Type ?? result.targetPile, CardPreviewMode.Normal);
            }
        }
        return results;
    }
}

internal static class ChaosRunHistoryContext
{
    [ThreadStatic] internal static int Depth;
    internal static HashSet<ModelId>? RestoredCardIds;
    internal static ChaosRunDefinitions.RuntimePoolState? PreviousPoolState;
}

[HarmonyPatch(typeof(NRunHistory), "DisplayRun")]
internal static class GeneratedCardHistorySnapshotPatch
{
    private static void Prefix(RunHistory history)
    {
        var stopwatch = Stopwatch.StartNew();
        ChaosRunHistoryContext.PreviousPoolState ??= ChaosRunDefinitions.CaptureRuntimeState();
        ChaosRunHistoryContext.RestoredCardIds = null;
        var snapshot = ChaosPoolSnapshot.ReadFrom(history);
        var characters = ChaosCharacterMapping.From(history);
        if (string.IsNullOrWhiteSpace(snapshot) || characters.Length == 0) return;

        try
        {
            var report = ChaosRunDefinitions.ActivateHistoryFromSave(characters, history.Seed, snapshot);
            ChaosRunHistoryContext.RestoredCardIds = report.RestoredSlots
                .Select(entry => ModelDb.GetId(ChaosCardRegistry.TypesFor(entry.Character)[entry.Slot]))
                .ToHashSet();
            stopwatch.Stop();
            Log.Info($"[AutoAnthony.Perf] Loaded run-history card snapshot without regeneration in "
                     + $"{stopwatch.ElapsedMilliseconds} ms: savedVersion={report.SavedVersion}, "
                     + $"restored={report.RestoredCards}, rejected={report.RejectedCards}.");
        }
        catch (Exception exception)
        {
            Log.Warn($"[AutoAnthony] Could not load the generated-card snapshot for run history; generated cards will use the missing-card placeholder: {exception}");
        }
    }
}

[HarmonyPatch(typeof(NRunHistory), "OnSubmenuHidden")]
internal static class GeneratedCardHistorySnapshotCleanupPatch
{
    private static void Postfix()
    {
        if (ChaosRunHistoryContext.PreviousPoolState is { } previous)
            ChaosRunDefinitions.RestoreRuntimeState(previous);
        ChaosRunHistoryContext.PreviousPoolState = null;
        ChaosRunHistoryContext.RestoredCardIds = null;
    }
}

[HarmonyPatch(typeof(NDeckHistory), nameof(NDeckHistory.LoadDeck))]
internal static class GeneratedCardHistoryScopePatch
{
    private static void Prefix() => ChaosRunHistoryContext.Depth++;

    private static Exception? Finalizer(Exception? __exception)
    {
        ChaosRunHistoryContext.Depth = Math.Max(0, ChaosRunHistoryContext.Depth - 1);
        return __exception;
    }
}

[HarmonyPatch(typeof(SaveUtil), nameof(SaveUtil.CardOrDeprecated))]
internal static class GeneratedCardHistoryPlaceholderPatch
{
    private static bool Prefix(ModelId id, ref CardModel __result)
    {
        if (ChaosRunHistoryContext.Depth <= 0 || !ChaosCardRegistry.IsGeneratedCardId(id)) return true;
        if (ChaosRunHistoryContext.RestoredCardIds?.Contains(id) == true) return true;
        __result = ModelDb.Card<DeprecatedCard>();
        return false;
    }
}

internal static class CharacterPoolPatchRouting
{
    internal static bool ReplacePool<TPool>(ref CardPoolModel result) where TPool : CardPoolModel
    {
        if (!ChaosRunDefinitions.IsRunActive) return true;
        result = ModelDb.CardPool<TPool>();
        return false;
    }

    internal static bool ReplaceStartingDeck(GeneratedCharacter character,
        ref IEnumerable<CardModel> result)
    {
        if (!ChaosRunDefinitions.IsCharacterRunActive(character)
            || !ChaosRunDefinitions.ActiveReplaceStartingCards)
            return true;
        result = Enumerable.Range(0, ChaosRunDefinitions.BasicCountFor(character))
            .Select(slot => ChaosCardRegistry.Canonical(character, slot)).ToArray();
        return false;
    }

    /// <summary>
    /// Multiplayer rewards may deserialize CardCreationOptions that captured a vanilla character-pool model
    /// before the authoritative chaos snapshot was activated. Character.CardPool is patched correctly afterward,
    /// but the already-captured object remains vanilla and CardFactory consequently offers original cards. Rebind
    /// every stale character pool at the final GetPossibleCards boundary; this also covers rerolls and rewards
    /// reconstructed from SerializableReward without changing Colorless, Curse, Token, or event-only pools.
    /// </summary>
    internal static bool NormalizeCapturedPools(CardCreationOptions options)
    {
        if (!ChaosRunDefinitions.IsRunActive) return false;
        var changed = false;
        var normalized = options.CardPools.Select(pool =>
        {
            CardPoolModel replacement = pool switch
            {
                IroncladCardPool => ModelDb.CardPool<ChaosIroncladCardPool>(),
                SilentCardPool => ModelDb.CardPool<ChaosSilentCardPool>(),
                DefectCardPool => ModelDb.CardPool<ChaosDefectCardPool>(),
                NecrobinderCardPool => ModelDb.CardPool<ChaosNecrobinderCardPool>(),
                RegentCardPool => ModelDb.CardPool<ChaosRegentCardPool>(),
                _ => pool
            };
            changed |= !ReferenceEquals(pool, replacement);
            return replacement;
        }).ToArray();
        if (changed) options.WithCardPools(normalized);
        return changed;
    }
}

[HarmonyPatch(typeof(CardCreationOptions), nameof(CardCreationOptions.GetPossibleCards))]
internal static class CapturedRewardPoolPatch
{
    private static void Prefix(CardCreationOptions __instance)
    {
        if (CharacterPoolPatchRouting.NormalizeCapturedPools(__instance) && ChaosDiagnostics.VerboseRuntime)
            Log.Info("[AutoAnthony] Rebound stale character card pool(s) in CardCreationOptions to the active chaos pools.");
    }
}

[HarmonyPatch(typeof(Ironclad), nameof(Ironclad.CardPool), MethodType.Getter)]
internal static class IroncladPoolPatch
{
    private static bool Prefix(ref CardPoolModel __result) =>
        CharacterPoolPatchRouting.ReplacePool<ChaosIroncladCardPool>(ref __result);
}

[HarmonyPatch(typeof(Ironclad), nameof(Ironclad.StartingDeck), MethodType.Getter)]
internal static class IroncladStartingDeckPatch
{
    private static bool Prefix(ref IEnumerable<CardModel> __result) =>
        CharacterPoolPatchRouting.ReplaceStartingDeck(GeneratedCharacter.Ironclad, ref __result);
}

[HarmonyPatch(typeof(Silent), nameof(Silent.CardPool), MethodType.Getter)]
internal static class SilentPoolPatch
{
    private static bool Prefix(ref CardPoolModel __result) =>
        CharacterPoolPatchRouting.ReplacePool<ChaosSilentCardPool>(ref __result);
}

[HarmonyPatch(typeof(Silent), nameof(Silent.StartingDeck), MethodType.Getter)]
internal static class SilentStartingDeckPatch
{
    private static bool Prefix(ref IEnumerable<CardModel> __result) =>
        CharacterPoolPatchRouting.ReplaceStartingDeck(GeneratedCharacter.Silent, ref __result);
}

[HarmonyPatch(typeof(Defect), nameof(Defect.CardPool), MethodType.Getter)]
internal static class DefectPoolPatch
{
    private static bool Prefix(ref CardPoolModel __result) =>
        CharacterPoolPatchRouting.ReplacePool<ChaosDefectCardPool>(ref __result);
}

[HarmonyPatch(typeof(Defect), nameof(Defect.StartingDeck), MethodType.Getter)]
internal static class DefectStartingDeckPatch
{
    private static bool Prefix(ref IEnumerable<CardModel> __result) =>
        CharacterPoolPatchRouting.ReplaceStartingDeck(GeneratedCharacter.Defect, ref __result);
}

[HarmonyPatch(typeof(Necrobinder), nameof(Necrobinder.CardPool), MethodType.Getter)]
internal static class NecrobinderPoolPatch
{
    private static bool Prefix(ref CardPoolModel __result) =>
        CharacterPoolPatchRouting.ReplacePool<ChaosNecrobinderCardPool>(ref __result);
}

[HarmonyPatch(typeof(Necrobinder), nameof(Necrobinder.StartingDeck), MethodType.Getter)]
internal static class NecrobinderStartingDeckPatch
{
    private static bool Prefix(ref IEnumerable<CardModel> __result) =>
        CharacterPoolPatchRouting.ReplaceStartingDeck(GeneratedCharacter.Necrobinder, ref __result);
}

[HarmonyPatch(typeof(Regent), nameof(Regent.CardPool), MethodType.Getter)]
internal static class RegentPoolPatch
{
    private static bool Prefix(ref CardPoolModel __result) =>
        CharacterPoolPatchRouting.ReplacePool<ChaosRegentCardPool>(ref __result);
}

[HarmonyPatch(typeof(Regent), nameof(Regent.StartingDeck), MethodType.Getter)]
internal static class RegentStartingDeckPatch
{
    private static bool Prefix(ref IEnumerable<CardModel> __result) =>
        CharacterPoolPatchRouting.ReplaceStartingDeck(GeneratedCharacter.Regent, ref __result);
}

[HarmonyPatch(typeof(CardPoolModel), nameof(CardPoolModel.AllCards), MethodType.Getter)]
internal static class ColorlessPoolContentsPatch
{
    private static bool Prefix(CardPoolModel __instance, ref IEnumerable<CardModel> __result)
    {
        if (!ChaosRunDefinitions.IsRunActive) return true;
        if (__instance is ColorlessCardPool)
        {
            var generatedColorless = ChaosCardRegistry.ColorlessTypes
                .Select(type => ModelDb.GetById<CardModel>(ModelDb.GetId(type)));
            __result = ChaosRunDefinitions.ActivePreserveOriginalCards
                ? generatedColorless.Concat(
                        ChaosRunDefinitions.OriginalCardsForPreservedPool(GeneratedCharacter.Colorless))
                    .ToArray()
                : generatedColorless.ToArray();
            return false;
        }

        var character = __instance switch
        {
            ChaosIroncladCardPool => GeneratedCharacter.Ironclad,
            ChaosSilentCardPool => GeneratedCharacter.Silent,
            ChaosDefectCardPool => GeneratedCharacter.Defect,
            ChaosNecrobinderCardPool => GeneratedCharacter.Necrobinder,
            ChaosRegentCardPool => GeneratedCharacter.Regent,
            _ => (GeneratedCharacter?)null
        };
        if (character is null) return true;

        var firstGeneratedSlot = ChaosRunDefinitions.ActiveReplaceStartingCards
            ? 0
            : ChaosRunDefinitions.BasicCountFor(character.Value);
        var generated = ChaosCardRegistry.TypesFor(character.Value).Skip(firstGeneratedSlot)
            .Select(type => ModelDb.GetById<CardModel>(ModelDb.GetId(type)))
            // Preserving the original non-Basic pool is a replacement policy for Ancients, not an additive one:
            // character rewards and Ancient relics use the two vanilla Ancients, while generated Ancient slots
            // remain unavailable implementation placeholders for the fixed registry/snapshot schema.
            .Where(card => !ChaosRunDefinitions.ActivePreserveOriginalCards
                || card.Rarity != CardRarity.Ancient);
        var originalBasic = ChaosRunDefinitions.ActiveReplaceStartingCards
            ? Enumerable.Empty<CardModel>()
            : OriginalBasicCards(character.Value);
        var originalNonBasic = ChaosRunDefinitions.ActivePreserveOriginalCards
            ? OriginalNonBasicCards(character.Value)
            : Enumerable.Empty<CardModel>();
        __result = originalBasic.Concat(generated).Concat(originalNonBasic).ToArray();
        return false;
    }

    private static IEnumerable<CardModel> OriginalBasicCards(GeneratedCharacter character) => (character switch
        {
            GeneratedCharacter.Ironclad => ModelDb.CardPool<IroncladCardPool>().AllCards,
            GeneratedCharacter.Silent => ModelDb.CardPool<SilentCardPool>().AllCards,
            GeneratedCharacter.Defect => ModelDb.CardPool<DefectCardPool>().AllCards,
            GeneratedCharacter.Necrobinder => ModelDb.CardPool<NecrobinderCardPool>().AllCards,
            GeneratedCharacter.Regent => ModelDb.CardPool<RegentCardPool>().AllCards,
            _ => Array.Empty<CardModel>()
        }).Where(card => card.Rarity == CardRarity.Basic
                         && card.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly);

    private static IEnumerable<CardModel> OriginalNonBasicCards(GeneratedCharacter character) =>
        ChaosRunDefinitions.OriginalCardsForPreservedPool(character)
            .Where(card => card.Rarity != CardRarity.Basic);
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.Title), MethodType.Getter)]
internal static class ChaosCardTitlePatch
{
    private static bool Prefix(CardModel __instance, ref string __result)
    {
        if (__instance is not ChaosCardModel chaosCard) return true;
        __result = chaosCard.DynamicTitle;
        return false;
    }
}

[HarmonyPatch(typeof(NCardLibrary), nameof(NCardLibrary._Ready))]
internal static class ChaosCardLibraryPoolPatch
{
    private static readonly System.Reflection.FieldInfo PoolFiltersField = AccessTools.Field(typeof(NCardLibrary), "_poolFilters");
    private static readonly System.Reflection.FieldInfo IroncladFilterField = AccessTools.Field(typeof(NCardLibrary), "_ironcladFilter");
    private static readonly System.Reflection.FieldInfo SilentFilterField = AccessTools.Field(typeof(NCardLibrary), "_silentFilter");
    private static readonly System.Reflection.FieldInfo DefectFilterField = AccessTools.Field(typeof(NCardLibrary), "_defectFilter");
    private static readonly System.Reflection.FieldInfo NecrobinderFilterField = AccessTools.Field(typeof(NCardLibrary), "_necrobinderFilter");
    private static readonly System.Reflection.FieldInfo RegentFilterField = AccessTools.Field(typeof(NCardLibrary), "_regentFilter");
    private static readonly System.Reflection.FieldInfo ColorlessFilterField = AccessTools.Field(typeof(NCardLibrary), "_colorlessFilter");
    private static readonly System.Reflection.FieldInfo AncientsFilterField = AccessTools.Field(typeof(NCardLibrary), "_ancientsFilter");

    private static void Postfix(NCardLibrary __instance)
    {
        var filters = (Dictionary<NCardPoolFilter, Func<CardModel, bool>>)PoolFiltersField.GetValue(__instance)!;
        var ironcladFilter = (NCardPoolFilter)IroncladFilterField.GetValue(__instance)!;
        var silentFilter = (NCardPoolFilter)SilentFilterField.GetValue(__instance)!;
        var defectFilter = (NCardPoolFilter)DefectFilterField.GetValue(__instance)!;
        var necrobinderFilter = (NCardPoolFilter)NecrobinderFilterField.GetValue(__instance)!;
        var regentFilter = (NCardPoolFilter)RegentFilterField.GetValue(__instance)!;
        var colorlessFilter = (NCardPoolFilter)ColorlessFilterField.GetValue(__instance)!;
        var ancientsFilter = (NCardPoolFilter)AncientsFilterField.GetValue(__instance)!;
        filters[ironcladFilter] = card => MatchesCharacterPool(card, typeof(ChaosIroncladCardPool), typeof(IroncladCardPool));
        filters[silentFilter] = card => MatchesCharacterPool(card, typeof(ChaosSilentCardPool), typeof(SilentCardPool));
        filters[defectFilter] = card => MatchesCharacterPool(card, typeof(ChaosDefectCardPool), typeof(DefectCardPool));
        filters[necrobinderFilter] = card => MatchesCharacterPool(card, typeof(ChaosNecrobinderCardPool), typeof(NecrobinderCardPool));
        filters[regentFilter] = card => MatchesCharacterPool(card, typeof(ChaosRegentCardPool), typeof(RegentCardPool));
        filters[colorlessFilter] = card => ChaosRunDefinitions.IsRunActive
            ? card is ChaosColorlessCardModel
              || ChaosRunDefinitions.ActivePreserveOriginalCards && card.Pool is ColorlessCardPool
            : card.Pool is ColorlessCardPool;
        // Hide only the ten original Ancients from the five replaced character pools. Event-pool Ancients such
        // as Apparition are not replaced by this mod and must remain visible beside the generated set.
        var useGeneratedCharacterAncients = ChaosRunDefinitions.IsRunActive
            ? !ChaosRunDefinitions.ActivePreserveOriginalCards
            : ChaosModSettings.Enabled && !ChaosModSettings.PreserveOriginalCards;
        filters[ancientsFilter] = card =>
            ChaosCardLibraryRules.IsAncientTabCard(card, useGeneratedCharacterAncients);
    }

    private static bool MatchesCharacterPool(CardModel card, Type generatedPool, Type originalPool)
    {
        if (!ChaosRunDefinitions.IsRunActive) return originalPool.IsInstanceOfType(card.Pool);
        if (generatedPool.IsInstanceOfType(card.Pool))
            return (!ChaosRunDefinitions.ActivePreserveOriginalCards || card.Rarity != CardRarity.Ancient)
                && (ChaosRunDefinitions.ActiveReplaceStartingCards || card.Rarity != CardRarity.Basic);
        if (!originalPool.IsInstanceOfType(card.Pool)) return false;
        return card.Rarity == CardRarity.Basic
            ? !ChaosRunDefinitions.ActiveReplaceStartingCards
            : ChaosRunDefinitions.ActivePreserveOriginalCards;
    }
}

[HarmonyPatch(typeof(NCardLibraryGrid), nameof(NCardLibraryGrid.RefreshVisibility))]
internal static class ChaosCardLibraryVisibilityPatch
{
    private static readonly System.Reflection.FieldInfo UnlockedCardsField =
        AccessTools.Field(typeof(NCardLibraryGrid), "_unlockedCards");

    private static void Postfix(NCardLibraryGrid __instance)
    {
        var unlocked = (HashSet<CardModel>)UnlockedCardsField.GetValue(__instance)!;

        // Generated slots do not belong to the vanilla epoch progression, and Ancient Fuel is an always-unlocked
        // easter-egg token. Do not mark them discovered: the vanilla NotSeen state and discovery history still apply.
        foreach (var card in ModelDb.AllCards.Where(ChaosCardLibraryRules.IsAlwaysUnlocked))
            unlocked.Add(card);
    }
}

[HarmonyPatch(typeof(NCard), nameof(NCard.UpdateVisuals))]
internal static class ChaosUnseenLibraryCardCostPatch
{
    private static readonly string[] CostParts =
    [
        "%EnergyIcon", "%EnergyLabel", "%UnplayableEnergyIcon",
        "%StarIcon", "%StarLabel", "%UnplayableStarIcon"
    ];

    private sealed class CostConcealmentMarker;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NCard, CostConcealmentMarker>
        CostConcealedCards = new();

    // NCard instances are pooled. An unseen library card can therefore share its node later with a visible card in
    // combat, a reward, or the inspect screen. Restore only nodes hidden by this patch before vanilla recalculates
    // them; otherwise the stale hidden label appears as an empty cost orb on the next card using the node.
    private static void Prefix(NCard __instance) => RestoreParts(__instance);

    private static void Postfix(NCard __instance)
    {
        if (__instance.Model is not ChaosCardModel || __instance.Visibility != ModelVisibility.NotSeen
            || !IsInCardLibrary(__instance))
            return;

        // Vanilla's NotSeen visual replaces the real cost with a crossed-out 1-Energy icon. Generated slots do
        // not have a meaningful hidden cost shared between runs, so leave the entire Energy/Star area blank.
        foreach (var path in CostParts)
            if (__instance.GetNodeOrNull<CanvasItem>(path) is { } part)
                part.Visible = false;
        CostConcealedCards.GetValue(__instance, static _ => new CostConcealmentMarker());
    }

    internal static void RestoreParts(NCard card)
    {
        if (!CostConcealedCards.TryGetValue(card, out _) || !card.IsNodeReady()) return;
        foreach (var path in CostParts)
            if (card.GetNodeOrNull<CanvasItem>(path) is { } part)
                part.Visible = true;
        CostConcealedCards.Remove(card);
    }

    private static bool IsInCardLibrary(Node node)
    {
        for (Node? current = node; current is not null; current = current.GetParent())
            if (current is NCardLibraryGrid)
                return true;
        return false;
    }
}

internal static class ChaosCardLibraryRules
{
    internal static bool IsAlwaysUnlocked(CardModel card) => card is ChaosCardModel or AncientFuel;

    internal static bool IsAncientTabCard(CardModel card, bool replaceCharacterAncients)
    {
        if (card.Rarity != CardRarity.Ancient) return false;
        if (card is ChaosCardModel) return replaceCharacterAncients;
        var vanillaCharacterAncient = card.Pool is IroncladCardPool or SilentCardPool or DefectCardPool
            or NecrobinderCardPool or RegentCardPool;
        return !vanillaCharacterAncient || !replaceCharacterAncients;
    }
}
