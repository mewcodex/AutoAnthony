using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;

namespace AutoAnthony.Patches;

/// <summary>
/// Vanilla combat checksums serialize only a power's model id and Amount. ChaosCompositePower keeps trigger
/// counters and one-shot flags outside Amount, so two peers could previously disagree without the checksum noticing.
/// Append a small, versioned trailer to NetFullCombatState. The base game already requires exact gameplay-mod
/// versions in multiplayer, while the magic probe keeps older replay/state payloads readable.
/// </summary>
internal static class ChaosMultiplayerPowerState
{
    private const uint Magic = 0x41415053; // "AAPS"
    private const int Schema = 1;
    private const int MaxEntries = 256;
    private const int MaxStateValues = 512;

    private sealed record Entry(int CreatureIndex, int PowerIndex, int[] Values);
    private sealed class Extension(IReadOnlyList<Entry> entries)
    {
        internal IReadOnlyList<Entry> Entries { get; } = entries;
    }

    private static readonly ConditionalWeakTable<NetFullCombatState, Extension> Extensions = new();

    internal static void Capture(NetFullCombatState state, IRunState runState)
    {
        // Vanilla also computes combat checksums after every action in singleplayer. This extension only exists to
        // compare peers, so avoid per-action lists, state arrays and ConditionalWeakTable entries outside multiplayer.
        if (!RunManager.Instance.NetService.Type.IsMultiplayer())
        {
            Extensions.Remove(state);
            return;
        }

        var entries = new List<Entry>();
        var creatures = runState.Players.Count == 0
            ? []
            : runState.Players[0].Creature.CombatState?.Creatures ?? [];
        for (var creatureIndex = 0; creatureIndex < creatures.Count; creatureIndex++)
        {
            var powers = creatures[creatureIndex].Powers;
            for (var powerIndex = 0; powerIndex < powers.Count; powerIndex++)
                if (powers[powerIndex] is ChaosCompositePower chaosPower)
                    entries.Add(new Entry(creatureIndex, powerIndex, chaosPower.CaptureMultiplayerState()));
        }
        Set(state, entries);
    }

    internal static void Copy(NetFullCombatState source, NetFullCombatState destination)
    {
        if (Extensions.TryGetValue(source, out var extension)) Set(destination, extension.Entries);
    }

    internal static void Serialize(NetFullCombatState state, PacketWriter writer)
    {
        // No extension means a singleplayer or legacy state. Keeping the trailer optional also avoids adding bytes
        // to every local replay checksum while multiplayer states always receive one in Capture/Deserialize.
        if (!Extensions.TryGetValue(state, out var extension)) return;
        var entries = extension.Entries;
        writer.WriteUInt(Magic);
        writer.WriteInt(Schema);
        writer.WriteInt(entries.Count);
        foreach (var entry in entries)
        {
            writer.WriteInt(entry.CreatureIndex);
            writer.WriteInt(entry.PowerIndex);
            writer.WriteInt(entry.Values.Length);
            foreach (var value in entry.Values) writer.WriteInt(value);
        }
    }

    internal static void Deserialize(NetFullCombatState state, PacketReader reader)
    {
        // NetFullCombatState is embedded inside several messages, so absence of our trailer must not consume the
        // following message field. Probe the raw bit stream without moving PacketReader.BitPosition.
        if (!TryPeekUInt32(reader, out var magic) || magic != Magic) return;
        reader.ReadUInt();
        if (reader.ReadInt() != Schema) throw new InvalidDataException("Unsupported AutoAnthony combat-state schema.");
        var count = reader.ReadInt();
        if (count is < 0 or > MaxEntries) throw new InvalidDataException("Invalid AutoAnthony power-state count.");
        var entries = new List<Entry>(count);
        for (var index = 0; index < count; index++)
        {
            var creatureIndex = reader.ReadInt();
            var powerIndex = reader.ReadInt();
            var valueCount = reader.ReadInt();
            if (creatureIndex < 0 || powerIndex < 0 || valueCount is < 0 or > MaxStateValues)
                throw new InvalidDataException("Invalid AutoAnthony power-state entry.");
            var values = new int[valueCount];
            for (var valueIndex = 0; valueIndex < valueCount; valueIndex++) values[valueIndex] = reader.ReadInt();
            entries.Add(new Entry(creatureIndex, powerIndex, values));
        }
        Set(state, entries);
    }

    private static void Set(NetFullCombatState state, IReadOnlyList<Entry> entries)
    {
        Extensions.Remove(state);
        Extensions.Add(state, new Extension(entries));
    }

    private static bool TryPeekUInt32(PacketReader reader, out uint value)
    {
        value = 0;
        if (reader.Buffer is null || reader.BitPosition < 0
                                  || reader.BitPosition + 32 > reader.Buffer.Length * 8) return false;
        for (var bitIndex = 0; bitIndex < 32; bitIndex++)
        {
            var absolute = reader.BitPosition + bitIndex;
            var bit = (reader.Buffer[absolute / 8] >> (absolute % 8)) & 1;
            value |= (uint)bit << bitIndex;
        }
        return true;
    }
}

[HarmonyPatch(typeof(NetFullCombatState), nameof(NetFullCombatState.FromRun))]
internal static class ChaosFullCombatStateCapturePatch
{
    private static void Postfix(IRunState runState, NetFullCombatState __result) =>
        ChaosMultiplayerPowerState.Capture(__result, runState);
}

[HarmonyPatch(typeof(NetFullCombatState), nameof(NetFullCombatState.Serialize))]
internal static class ChaosFullCombatStateSerializePatch
{
    private static void Postfix(NetFullCombatState __instance, PacketWriter writer) =>
        ChaosMultiplayerPowerState.Serialize(__instance, writer);
}

[HarmonyPatch(typeof(NetFullCombatState), nameof(NetFullCombatState.Deserialize))]
internal static class ChaosFullCombatStateDeserializePatch
{
    private static void Postfix(NetFullCombatState __instance, PacketReader reader) =>
        ChaosMultiplayerPowerState.Deserialize(__instance, reader);
}

[HarmonyPatch(typeof(NetFullCombatState), nameof(NetFullCombatState.Anonymized))]
internal static class ChaosFullCombatStateAnonymizedPatch
{
    private static void Postfix(NetFullCombatState __instance, NetFullCombatState __result) =>
        ChaosMultiplayerPowerState.Copy(__instance, __result);
}
