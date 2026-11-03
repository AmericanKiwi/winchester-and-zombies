using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace WinchesterZombies
{
    // Custom Mirror messages. The mod isn't run through Mirror's weaver, so each message registers its own
    // reader/writer below. Message ids are hashes of the type names, identical for everyone on the same version.

    public struct HelloMsg : NetworkMessage { public string Version; }
    public struct WelcomeMsg : NetworkMessage { public string Version; }
    public struct RifleFireMsg : NetworkMessage { public uint NetId; public Vector3 Origin; public Vector3 Direction; }
    public struct RifleReloadMsg : NetworkMessage { public uint NetId; }
    public struct RifleActionMsg : NetworkMessage { public uint NetId; public byte Action; }
    public struct RifleStateMsg : NetworkMessage { public uint NetId; public byte Tube; public byte Chamber; public short Reserve; }
    public struct ImpactMsg : NetworkMessage { public Vector3 Point; public Vector3 Normal; }
    public struct ZombieSnapshotMsg : NetworkMessage { public ZombieNetState[] Zombies; }
    public struct ZombieEventMsg : NetworkMessage { public ushort Id; public byte Kind; }
    public struct PlayerHitMsg : NetworkMessage { public float Damage; }
    public struct BloodMsg : NetworkMessage { public byte Kind; public uint NetId; public Vector3 Point; public Vector3 Normal; public float Size; }

    public struct PocketMsg : NetworkMessage { public short Rounds; public short Capacity; }
    public struct AmmoTakeMsg : NetworkMessage { public uint NetId; }
    public struct BoxStateMsg : NetworkMessage { public uint NetId; public short Rounds; }
    public struct TyreHitMsg : NetworkMessage { public uint NetId; public Vector3 Point; }
    public struct CarHitMsg : NetworkMessage { public ushort Id; public uint NetId; public Vector3 Point; public Vector3 Travel; public float Mph; }

    public struct ZombieNetState { public ushort Id; public Vector3 Position; public float Yaw; public float Speed; }

    internal static class Net
    {
        private static bool _serverRegistered;
        private static bool _clientRegistered;

        /// <summary>Connections that sent a matching Hello; only they receive mod traffic.</summary>
        private static readonly HashSet<int> ModdedConnections = new HashSet<int>();

        public static bool HostHasMod { get; private set; }

        static Net()
        {
            Writer<HelloMsg>.write = (w, m) => w.WriteString(m.Version);
            Reader<HelloMsg>.read = r => new HelloMsg { Version = r.ReadString() };
            Writer<WelcomeMsg>.write = (w, m) => w.WriteString(m.Version);
            Reader<WelcomeMsg>.read = r => new WelcomeMsg { Version = r.ReadString() };
            Writer<RifleFireMsg>.write = (w, m) => { w.WriteUInt(m.NetId); w.WriteVector3(m.Origin); w.WriteVector3(m.Direction); };
            Reader<RifleFireMsg>.read = r => new RifleFireMsg { NetId = r.ReadUInt(), Origin = r.ReadVector3(), Direction = r.ReadVector3() };
            Writer<RifleReloadMsg>.write = (w, m) => w.WriteUInt(m.NetId);
            Reader<RifleReloadMsg>.read = r => new RifleReloadMsg { NetId = r.ReadUInt() };
            Writer<RifleActionMsg>.write = (w, m) => { w.WriteUInt(m.NetId); w.WriteByte(m.Action); };
            Reader<RifleActionMsg>.read = r => new RifleActionMsg { NetId = r.ReadUInt(), Action = r.ReadByte() };
            Writer<RifleStateMsg>.write = (w, m) => { w.WriteUInt(m.NetId); w.WriteByte(m.Tube); w.WriteByte(m.Chamber); w.WriteShort(m.Reserve); };
            Reader<RifleStateMsg>.read = r => new RifleStateMsg { NetId = r.ReadUInt(), Tube = r.ReadByte(), Chamber = r.ReadByte(), Reserve = r.ReadShort() };
            Writer<ImpactMsg>.write = (w, m) => { w.WriteVector3(m.Point); w.WriteVector3(m.Normal); };
            Reader<ImpactMsg>.read = r => new ImpactMsg { Point = r.ReadVector3(), Normal = r.ReadVector3() };
            Writer<ZombieEventMsg>.write = (w, m) => { w.WriteUShort(m.Id); w.WriteByte(m.Kind); };
            Reader<ZombieEventMsg>.read = r => new ZombieEventMsg { Id = r.ReadUShort(), Kind = r.ReadByte() };
            Writer<PlayerHitMsg>.write = (w, m) => w.WriteFloat(m.Damage);
            Reader<PlayerHitMsg>.read = r => new PlayerHitMsg { Damage = r.ReadFloat() };
            Writer<BloodMsg>.write = (w, m) => { w.WriteByte(m.Kind); w.WriteUInt(m.NetId); w.WriteVector3(m.Point); w.WriteVector3(m.Normal); w.WriteFloat(m.Size); };
            Reader<BloodMsg>.read = r => new BloodMsg { Kind = r.ReadByte(), NetId = r.ReadUInt(), Point = r.ReadVector3(), Normal = r.ReadVector3(), Size = r.ReadFloat() };
            Writer<PocketMsg>.write = (w, m) => { w.WriteShort(m.Rounds); w.WriteShort(m.Capacity); };
            Reader<PocketMsg>.read = r => new PocketMsg { Rounds = r.ReadShort(), Capacity = r.ReadShort() };
            Writer<AmmoTakeMsg>.write = (w, m) => w.WriteUInt(m.NetId);
            Reader<AmmoTakeMsg>.read = r => new AmmoTakeMsg { NetId = r.ReadUInt() };
            Writer<BoxStateMsg>.write = (w, m) => { w.WriteUInt(m.NetId); w.WriteShort(m.Rounds); };
            Reader<BoxStateMsg>.read = r => new BoxStateMsg { NetId = r.ReadUInt(), Rounds = r.ReadShort() };
            Writer<TyreHitMsg>.write = (w, m) => { w.WriteUInt(m.NetId); w.WriteVector3(m.Point); };
            Reader<TyreHitMsg>.read = r => new TyreHitMsg { NetId = r.ReadUInt(), Point = r.ReadVector3() };
            Writer<CarHitMsg>.write = (w, m) => { w.WriteUShort(m.Id); w.WriteUInt(m.NetId); w.WriteVector3(m.Point); w.WriteVector3(m.Travel); w.WriteFloat(m.Mph); };
            Reader<CarHitMsg>.read = r => new CarHitMsg { Id = r.ReadUShort(), NetId = r.ReadUInt(), Point = r.ReadVector3(), Travel = r.ReadVector3(), Mph = r.ReadFloat() };
            Writer<ZombieSnapshotMsg>.write = (w, m) =>
            {
                var list = m.Zombies ?? Array.Empty<ZombieNetState>();
                w.WriteUShort((ushort)list.Length);
                foreach (var z in list)
                {
                    w.WriteUShort(z.Id);
                    w.WriteVector3(z.Position);
                    w.WriteFloat(z.Yaw);
                    w.WriteFloat(z.Speed);
                }
            };
            Reader<ZombieSnapshotMsg>.read = r =>
            {
                var list = new ZombieNetState[r.ReadUShort()];
                for (int i = 0; i < list.Length; i++)
                    list[i] = new ZombieNetState { Id = r.ReadUShort(), Position = r.ReadVector3(), Yaw = r.ReadFloat(), Speed = r.ReadFloat() };
                return new ZombieSnapshotMsg { Zombies = list };
            };
        }

        /// <summary>Registers handlers whenever the server/client (re)starts; Mirror clears them on shutdown.</summary>
        public static void Tick()
        {
            if (NetworkServer.active && !_serverRegistered)
            {
                _serverRegistered = true;
                ModdedConnections.Clear();
                NetworkServer.RegisterHandler<HelloMsg>(OnServerHello, false);
                NetworkServer.RegisterHandler<RifleFireMsg>(RifleServer.OnFire);
                NetworkServer.RegisterHandler<RifleReloadMsg>(RifleServer.OnReload);
                NetworkServer.RegisterHandler<AmmoTakeMsg>(AmmoServer.OnTake);
                NetworkServer.RegisterHandler<CarHitMsg>(VehicleHits.OnServerHit);
                NetworkServer.RegisterHandler<TyreHitMsg>(VehicleHits.OnServerTyreHit);
                Plugin.Log.LogInfo("Server handlers registered.");
            }
            else if (!NetworkServer.active && _serverRegistered)
            {
                _serverRegistered = false;
                ModdedConnections.Clear();
                ZombieDirector.Clear();
                RifleServer.Clear();
                AmmoServer.Clear();
                ModSave.Clear();
            }

            if (NetworkClient.active && !_clientRegistered && NetworkClient.isConnected)
            {
                _clientRegistered = true;
                HostHasMod = NetworkServer.active;
                NetworkClient.RegisterHandler<WelcomeMsg>(OnClientWelcome, false);
                NetworkClient.RegisterHandler<RifleActionMsg>(RifleClient.OnAction);
                NetworkClient.RegisterHandler<RifleStateMsg>(RifleClient.OnState);
                NetworkClient.RegisterHandler<ImpactMsg>(Effects.OnImpact);
                NetworkClient.RegisterHandler<ZombieSnapshotMsg>(ZombieWorld.OnSnapshot);
                NetworkClient.RegisterHandler<ZombieEventMsg>(ZombieWorld.OnEvent);
                NetworkClient.RegisterHandler<PlayerHitMsg>(m => LocalPlayer.Damage(m.Damage));
                NetworkClient.RegisterHandler<BloodMsg>(Blood.OnMsg);
                NetworkClient.RegisterHandler<PocketMsg>(AmmoClient.OnPocket);
                NetworkClient.RegisterHandler<BoxStateMsg>(AmmoClient.OnBoxState);
                if (!NetworkServer.active) NetworkClient.Send(new HelloMsg { Version = Plugin.Version });
            }
            else if ((!NetworkClient.active || !NetworkClient.isConnected) && _clientRegistered)
            {
                _clientRegistered = false;
                HostHasMod = false;
                AmmoClient.Clear();
                ZombieWorld.Clear();
                VehicleHits.Clear();
                Blood.Clear();
            }
        }

        private static void OnServerHello(NetworkConnectionToClient conn, HelloMsg msg)
        {
            if (msg.Version != Plugin.Version)
            {
                Plugin.Log.LogWarning($"Client {conn.connectionId} has mod version {msg.Version}, host has {Plugin.Version}.");
                conn.Send(new WelcomeMsg { Version = Plugin.Version });
                return;
            }
            ModdedConnections.Add(conn.connectionId);
            conn.Send(new WelcomeMsg { Version = Plugin.Version });
            RifleServer.SendAllStates(conn);
            AmmoServer.SendAllStates(conn);
            Plugin.Log.LogInfo($"Client {conn.connectionId} joined with the mod.");
        }

        private static void OnClientWelcome(WelcomeMsg msg)
        {
            HostHasMod = true;
            if (msg.Version != Plugin.Version)
                Notice.Show($"Winchester & Zombies: host has version {msg.Version}, you have {Plugin.Version}. Install the same version.");
        }

        /// <summary>Sends to every remote client that has the mod (the host's own client is handled directly).</summary>
        public static void SendToModded<T>(T msg) where T : struct, NetworkMessage
        {
            foreach (var conn in NetworkServer.connections.Values)
            {
                if (conn == null || conn == NetworkServer.localConnection) continue;
                if (ModdedConnections.Contains(conn.connectionId)) conn.Send(msg);
            }
        }

        public static bool IsModded(NetworkConnectionToClient conn) => conn != null && ModdedConnections.Contains(conn.connectionId);

        public static void SendTo<T>(NetworkConnectionToClient conn, T msg) where T : struct, NetworkMessage
        {
            if (conn == null) return;
            if (conn == NetworkServer.localConnection || ModdedConnections.Contains(conn.connectionId)) conn.Send(msg);
        }
    }
}
