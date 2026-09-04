using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace AutoAnthony.Multiplayer;

/// <summary>
/// Starts deterministic pool generation while every peer is still in the character-select lobby. The host and
/// clients generate concurrently; the normal begin-run packet is sent only after all peers acknowledge a matching
/// gameplay fingerprint.
/// </summary>
internal struct ChaosPoolPreparationMessage : INetMessage, IPacketSerializable
{
    internal string PreparationId;
    internal string Seed;
    internal int CharacterMask;
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
