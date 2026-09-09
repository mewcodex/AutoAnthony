using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace AutoAnthony.Multiplayer;

/// <summary>
/// Starts host-authoritative pool generation while every peer is still in the character-select lobby. The normal
/// begin-run packet is sent only after every client has installed and acknowledged the host's exact structured
/// snapshot.
/// </summary>
internal struct ChaosPoolPreparationMessage : INetMessage, IPacketSerializable
{
    internal string PreparationId;
    internal string Seed;
    internal int CharacterMask;
    internal bool AddGeneratedCards;
    internal bool UltimateChaos;
    internal bool ReplaceStartingCards;
    internal bool NumericBalanceOptimization;
    internal bool NumericRandomMode;
    internal bool PreserveOriginalCards;
    internal bool RandomCardArt;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(PreparationId);
        writer.WriteString(Seed);
        writer.WriteInt(CharacterMask);
        writer.WriteBool(AddGeneratedCards);
        writer.WriteBool(UltimateChaos);
        writer.WriteBool(ReplaceStartingCards);
        writer.WriteBool(NumericBalanceOptimization);
        writer.WriteBool(NumericRandomMode);
        writer.WriteBool(PreserveOriginalCards);
        writer.WriteBool(RandomCardArt);
    }

    public void Deserialize(PacketReader reader)
    {
        PreparationId = reader.ReadString();
        Seed = reader.ReadString();
        CharacterMask = reader.ReadInt();
        AddGeneratedCards = reader.ReadBool();
        UltimateChaos = reader.ReadBool();
        ReplaceStartingCards = reader.ReadBool();
        NumericBalanceOptimization = reader.ReadBool();
        NumericRandomMode = reader.ReadBool();
        PreserveOriginalCards = reader.ReadBool();
        RandomCardArt = reader.ReadBool();
    }
}

internal struct ChaosPoolPreparationAckMessage : INetMessage, IPacketSerializable
{
    internal string PreparationId;
    internal bool Success;
    internal string GameplayFingerprint;
    internal string Failure;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(PreparationId);
        writer.WriteBool(Success);
        writer.WriteString(GameplayFingerprint);
        writer.WriteString(Failure);
    }

    public void Deserialize(PacketReader reader)
    {
        PreparationId = reader.ReadString();
        Success = reader.ReadBool();
        GameplayFingerprint = reader.ReadString();
        Failure = reader.ReadString();
    }
}

/// <summary>
/// Carries one bounded part of the host-authored generated pool snapshot. New multiplayer runs intentionally do
/// not regenerate the same 514 cards independently on every peer: platform- or component-registration ordering
/// must not be allowed to change gameplay, and clients should not pay the full generation cost a second time.
/// </summary>
internal struct ChaosPoolSnapshotChunkMessage : INetMessage, IPacketSerializable
{
    internal string PreparationId;
    internal string PayloadFingerprint;
    internal string GameplayFingerprint;
    internal int PayloadLength;
    internal int ChunkIndex;
    internal int ChunkCount;
    internal string Chunk;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(PreparationId);
        writer.WriteString(PayloadFingerprint);
        writer.WriteString(GameplayFingerprint);
        writer.WriteInt(PayloadLength);
        writer.WriteInt(ChunkIndex);
        writer.WriteInt(ChunkCount);
        writer.WriteString(Chunk);
    }

    public void Deserialize(PacketReader reader)
    {
        PreparationId = reader.ReadString();
        PayloadFingerprint = reader.ReadString();
        GameplayFingerprint = reader.ReadString();
        PayloadLength = reader.ReadInt();
        ChunkIndex = reader.ReadInt();
        ChunkCount = reader.ReadInt();
        Chunk = reader.ReadString();
    }
}

internal struct ChaosPoolPreparationCancelMessage : INetMessage, IPacketSerializable
{
    internal string PreparationId;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer) => writer.WriteString(PreparationId);

    public void Deserialize(PacketReader reader) => PreparationId = reader.ReadString();
}
