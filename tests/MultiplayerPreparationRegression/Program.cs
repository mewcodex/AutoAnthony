using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using AutoAnthony;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;

var assembly=typeof(ChaosCardModel).Assembly;
var protocol=assembly.GetType("AutoAnthony.Patches.MultiplayerGenerationModePatch",true)!;
var clientStateType=protocol.GetNestedType("ClientPreparation",BindingFlags.NonPublic)!;
var hostStateType=protocol.GetNestedType("HostPreparation",BindingFlags.NonPublic)!;
var preparationType=assembly.GetType("AutoAnthony.Multiplayer.ChaosPoolPreparationMessage",true)!;
var ackType=assembly.GetType("AutoAnthony.Multiplayer.ChaosPoolPreparationAckMessage",true)!;
var chunkType=assembly.GetType("AutoAnthony.Multiplayer.ChaosPoolSnapshotChunkMessage",true)!;
void Check(bool condition,string label) { if(!condition) throw new Exception(label); }
object Call(string name,params object[] args)=>AccessTools.Method(protocol,name).Invoke(null,args)!;
void Set(object value,string name,object field) {
    var property=AccessTools.Property(value.GetType(),name);
    if(property is not null) property.SetValue(value,field);
    else AccessTools.Field(value.GetType(),name).SetValue(value,field);
}
object Get(object value,string name)=>AccessTools.Property(value.GetType(),name).GetValue(value)!;
object Message(Type type,params (string,object)[] fields) {
    var message=Activator.CreateInstance(type)!;
    foreach(var (name,value) in fields) Set(message,name,value);
    return message;
}
object Table(string name)=>AccessTools.Field(protocol,name).GetValue(null)!;
void TableAdd(string name,StartRunLobby lobby,object state)=>Table(name).GetType().GetMethod("Add")!.Invoke(Table(name),[lobby,state]);
var harmony=new Harmony("autoanthony.tests.multiplayer-preparation");
harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Logging.Logger),"GetIsRunningFromGodotEditor"),
    prefix:new HarmonyMethod(typeof(TestHooks),nameof(TestHooks.NotEditor)));
harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Logging.ConsoleLogPrinter),"Print"),
    prefix:new HarmonyMethod(typeof(TestHooks),nameof(TestHooks.NoGameMutation)));
var registration=assembly.GetType("AutoAnthony.Patches.MultiplayerPreparationLobbyConstructionPatch",true)!;
var ctor=AccessTools.Constructor(typeof(StartRunLobby),[typeof(GameMode),typeof(INetGameService),typeof(IStartRunLobbyListener),typeof(int)]);
harmony.Patch(ctor,postfix:new HarmonyMethod(AccessTools.Method(registration,"Postfix")));
harmony.Patch(AccessTools.Method(protocol,"IsClientHost"),prefix:new HarmonyMethod(typeof(TestHooks),nameof(TestHooks.KnownHost)));
harmony.Patch(AccessTools.Method(assembly.GetType("AutoAnthony.ChaosGenerationProgressOverlay"),"Create"),
    prefix:new HarmonyMethod(typeof(TestHooks),nameof(TestHooks.NoOverlay)));
harmony.Patch(AccessTools.Method(protocol,"RestoreClientSnapshotAsync"),
    prefix:new HarmonyMethod(typeof(TestHooks),nameof(TestHooks.Restore)));
harmony.Patch(AccessTools.Method(typeof(StartRunLobby),"BeginRunForAllPlayers"),
    prefix:new HarmonyMethod(typeof(TestHooks),nameof(TestHooks.Begin)));
harmony.Patch(AccessTools.Method(typeof(ChaosRunDefinitions),"DeactivateRun"),
    prefix:new HarmonyMethod(typeof(TestHooks),nameof(TestHooks.NoGameMutation)));
var services=new Dictionary<ulong,NetProxy>();
StartRunLobby Client(ulong id) {
    var service=DispatchProxy.Create<INetGameService,NetProxy>();
    var proxy=(NetProxy)(object)service;
    proxy.Id=id; proxy.Kind=NetGameType.Client; services[id]=proxy;
    var lobby=new StartRunLobby(default,service,new Listener(),4);
    Check(proxy.Handlers.ContainsKey(preparationType)&&proxy.Handlers.ContainsKey(chunkType),"Handlers missing at construction");
    var count=proxy.Handlers.Count;
    Call("RegisterLobbyHandlers",lobby);
    Check(proxy.Handlers.Count==count&&proxy.Handlers[preparationType].Count==1,"Duplicate registration");
    return lobby;
}
var clients=new[]{Client(2),Client(3)};
var hostService=DispatchProxy.Create<INetGameService,NetProxy>();
var hostProxy=(NetProxy)(object)hostService; hostProxy.Id=1;hostProxy.Kind=NetGameType.Host;
var host=(StartRunLobby)RuntimeHelpers.GetUninitializedObject(typeof(StartRunLobby));
AccessTools.Field(typeof(StartRunLobby),"<NetService>k__BackingField").SetValue(host,hostService);
AccessTools.Field(typeof(StartRunLobby),"<LobbyListener>k__BackingField").SetValue(host,new Listener());
AccessTools.Field(typeof(StartRunLobby),"<Players>k__BackingField").SetValue(host,
    new List<StartRunLobbyPlayer>{new(){id=1,isReady=true},new(){id=2,isReady=true},new(){id=3,isReady=true}});
var connectingField=AccessTools.Field(typeof(StartRunLobby),"_connectingPlayers");
connectingField.SetValue(host,Activator.CreateInstance(connectingField.FieldType));
Call("RegisterLobbyHandlers",host);
var markerType=assembly.GetType("AutoAnthony.ChaosPoolSnapshotModifier",true)!;
var marker=(ModifierModel)RuntimeHelpers.GetUninitializedObject(markerType);
AccessTools.Field(typeof(AbstractModel),"<IsMutable>k__BackingField").SetValue(marker,true);
var id="AA2:REGRESSION";
var request=Message(preparationType,("PreparationId",id),("Seed","SEED"),("CharacterMask",1),("AddGeneratedCards",true));
var state=Activator.CreateInstance(hostStateType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,
    null,[id,"SEED",new List<ModifierModel>(),marker,new ulong[]{2,3}],null)!;
Set(state,"Request",request);
var payload=new string('A',48_123);
Set(state,"AuthoritativePayload",payload);Set(state,"HostFingerprint","GAMEPLAY");
TableAdd("HostPreparations",host,state);
// Drop the original bootstrap entirely. The snapshot transfer itself must re-bootstrap each missing client.
Call("SendAuthoritativeSnapshot",host,state,payload,"GAMEPLAY");
foreach(var peer in new ulong[]{2,3}) {
    var packets=hostProxy.Sent.Where(packet=>packet.Peer==peer).ToArray();
    Check(packets[0].Message.GetType()==preparationType,"Chunks sent before bootstrap");
    Check(packets.Count(packet=>packet.Message.GetType()==chunkType)==3,"Wrong chunk count");
    services[peer].Deliver(packets[0].Message,1);
    foreach(var ack in services[peer].Sent) hostProxy.Deliver(ack.Message,peer);
    Check(((IDictionary)Get(state,"PeerFingerprints")).Count==0,"Bootstrap receipt incorrectly completed generation");
    foreach(var packet in packets.Skip(1).Where((packet,index)=>index!=1)) services[peer].Deliver(packet.Message,1);
}
Check(TestHooks.Restored.Count==0,"Incomplete snapshot installed");
hostProxy.Sent.Clear();
Call("SendAuthoritativeSnapshot",host,state,payload,"GAMEPLAY");
foreach(var peer in new ulong[]{2,3}) {
    foreach(var packet in hostProxy.Sent.Where(packet=>packet.Peer==peer)) services[peer].Deliver(packet.Message,1);
    Check(TestHooks.Restored.ContainsKey(peer)&&TestHooks.Restored[peer]==payload,"Retry did not restore the exact snapshot");
    Check(TestHooks.RestoreCounts[peer]==1,"Duplicate chunks installed twice");
    // A repeated bootstrap after installation resends the completed fingerprint without rebuilding the pool.
    services[peer].Sent.Clear();services[peer].Deliver((INetMessage)request,1);
    foreach(var ack in services[peer].Sent) hostProxy.Deliver(ack.Message,peer);
}
Check(!(bool)Call("CanStart",state),"Started before host completion");
Set(state,"HostComplete",true);
Check((bool)Call("CanStart",state),"Matching installed snapshots did not complete readiness");
Call("TryReleasePreparedRun",host,id);
Check(TestHooks.BeginCount==1,"Vanilla start not released exactly once");
Call("TryReleasePreparedRun",host,id);Check(TestHooks.BeginCount==1,"Duplicate release");
var oldClient=Client(4);
var oldRequest=Message(preparationType,("PreparationId","LEGACY_NONCE"),("Seed","SEED"),("CharacterMask",1));
services[4].Deliver((INetMessage)oldRequest,1);
Check(services[4].Sent.Count==0,"New client sent a bootstrap-only ack to an older host");
Check(!(bool)Call("PreparationTimedOut",true,89.9d)&&(bool)Call("PreparationTimedOut",true,90d)
    &&(bool)Call("PreparationTimedOut",false,0d),"Incorrect bounded wait/disconnection policy");
Table("HostPreparations").GetType().GetMethod("Remove")!.Invoke(Table("HostPreparations"),[host]);
var cancelled=Activator.CreateInstance(hostStateType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,
    null,["AA2:CANCEL","SEED",new List<ModifierModel>(),marker,new ulong[]{2,3}],null)!;
Set(cancelled,"HostComplete",true);
TableAdd("HostPreparations",host,cancelled);
host.Players[0]=new StartRunLobbyPlayer{id=1,isReady=false};
Call("CancelHostPreparation",host,"AA2:CANCEL","pool synchronization timed out",true);
object[] lookup=[host,null!];
Check(!(bool)Table("HostPreparations").GetType().GetMethod("TryGetValue")!.Invoke(Table("HostPreparations"),lookup)!
    &&(bool)Get(cancelled,"Cancelled"),"Timed-out transaction did not release its state");
Check(hostProxy.Sent.Any(packet=>packet.Message.GetType().Name=="ChaosPoolPreparationCancelMessage"),
    "Cancellation not transmitted to peers");
foreach(var lobby in clients.Append(oldClient)) Call("CleanUpLobby",lobby);
Check(!services[2].Handlers.ContainsKey(preparationType),"Lobby handlers leaked after cleanup");
Console.WriteLine("PASS: early/idempotent registration; lost bootstrap; missing/duplicate chunks; two-client exact snapshot transfer; receipt vs completion; release once; legacy ack compatibility; timeout/disconnect policy; cancellation release; cleanup. No game/saves modified.");

public class NetProxy : DispatchProxy {
    public ulong Id; public NetGameType Kind;
    public Dictionary<Type,List<Delegate>> Handlers=[];
    public List<(ulong Peer,INetMessage Message)> Sent=[];
    protected override object? Invoke(MethodInfo? method,object?[]? args) {
        args??=[];
        switch(method!.Name) {
            case "get_NetId":return Id;case "get_Type":return Kind;case "get_IsConnected":return true;
            case "get_IsGameLoading":return false;
            case "RegisterMessageHandler": {
                var type=method.GetGenericArguments()[0];
                if(!Handlers.TryGetValue(type,out var list)) Handlers[type]=list=[];
                list.Add((Delegate)args[0]!);return null;
            }
            case "UnregisterMessageHandler": {
                var type=method.GetGenericArguments()[0];
                if(Handlers.TryGetValue(type,out var list)) {list.Remove((Delegate)args[0]!);if(list.Count==0)Handlers.Remove(type);}
                return null;
            }
            case "SendMessage": {
                var source=(INetMessage)args[0]!;
                var writer=new PacketWriter{WarnOnGrow=false};source.Serialize(writer);
                var reader=new PacketReader();reader.Reset(writer.Buffer.Take(writer.BytePosition).ToArray());
                var copy=(INetMessage)Activator.CreateInstance(source.GetType())!;copy.Deserialize(reader);
                Sent.Add((args.Length>1?(ulong)args[1]!:1ul,copy));return null;
            }
        }
        return method.ReturnType==typeof(void)?null:method.ReturnType.IsValueType?Activator.CreateInstance(method.ReturnType):null;
    }
    public void Deliver(INetMessage message,ulong sender) {
        if(Handlers.TryGetValue(message.GetType(),out var handlers))foreach(var handler in handlers.ToArray())handler.DynamicInvoke(message,sender);
    }
}
public static class TestHooks {
    public static Dictionary<ulong,string> Restored=[];
    public static Dictionary<ulong,int> RestoreCounts=[];
    public static int BeginCount;
    public static bool KnownHost(ulong senderId,ref bool __result) {__result=senderId==1;return false;}
    public static bool NotEditor(ref bool __result) {__result=false;return false;}
    public static bool NoOverlay(ref object __result) {__result=null!;return false;}
    public static bool NoGameMutation()=>false;
    public static bool Begin() {BeginCount++;return false;}
    public static bool Restore(StartRunLobby lobby,object state,string payload,ref Task __result) {
        Restored[lobby.NetService.NetId]=payload;
        RestoreCounts[lobby.NetService.NetId]=RestoreCounts.GetValueOrDefault(lobby.NetService.NetId)+1;
        AccessTools.Property(state.GetType(),"Complete").SetValue(state,true);
        __result=Task.CompletedTask;return false;
    }
}
public class Listener : IStartRunLobbyListener {
    public void PlayerConnected(StartRunLobbyPlayer player){} public void PlayerChanged(StartRunLobbyPlayer player,bool random){}
    public void AscensionChanged(){} public void SeedChanged(){} public void ModifiersChanged(){} public void MaxAscensionChanged(){}
    public void RemotePlayerDisconnected(StartRunLobbyPlayer player){}
    public void BeginRun(string seed,List<ActModel> acts,IReadOnlyList<ModifierModel> modifiers){}
    public void LocalPlayerDisconnected(NetErrorInfo info){}
}
