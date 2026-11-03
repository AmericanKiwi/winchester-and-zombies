using System;
using System.Collections;
using System.Collections.Generic;
using Mirror;
using NomadDrive.Features.FloatingOrigin;
using UnityEngine;

namespace WinchesterZombies
{
    public enum RifleAction : byte { Fire = 1, DryFire, Cycle, ReloadStart, ReloadInsert, ReloadEnd }
    public enum Chamber : byte { Empty, Live, Spent }

    /// <summary>
    /// Host-side rifle state and timing. Clients ask to fire or reload; the host checks ammo, resolves the shot,
    /// steps through the lever/reload timeline (matching the clip lengths) and tells everyone what to play.
    /// </summary>
    internal static class RifleServer
    {
        // Clip timings (seconds), matching the bundle's Rifle_* clips.
        private const float TriggerTime = 0.32f, CycleChamber = 0.45f, CycleLength = 0.62f;
        private const float ReloadStartLength = 0.3f, InsertAt = 0.37f, InsertLength = 0.55f, ReloadEndLength = 0.3f;

        private sealed class State
        {
            public int Tube;
            public Chamber Chamber;
            public bool Busy;
            public bool Reloading;
            public bool CancelReload;
        }

        private static readonly Dictionary<uint, State> States = new Dictionary<uint, State>();

        public static void Clear() => States.Clear();

        /// <summary>Sets a rifle's load (a new spawn, or a saved rifle being restored).</summary>
        public static void SetLoad(uint netId, int tube, Chamber chamber)
        {
            States[netId] = new State { Tube = Mathf.Clamp(tube, 0, Plugin.TubeCapacity), Chamber = chamber };
            SendState(netId);
        }

        /// <summary>What the save stores for a rifle.</summary>
        public static void GetLoad(uint netId, out int tube, out Chamber chamber)
        {
            var s = Get(netId);
            tube = s?.Tube ?? 0;
            chamber = s?.Chamber ?? Chamber.Empty;
            if (chamber == Chamber.Spent) chamber = Chamber.Empty;
        }

        private static State Get(uint netId)
        {
            if (!States.TryGetValue(netId, out var s) && NetworkServer.spawned.TryGetValue(netId, out var id) && id.GetComponent<RifleItem>() != null)
            {
                s = new State { Tube = Plugin.TubeCapacity, Chamber = Chamber.Live };
                States[netId] = s;
            }
            return s;
        }

        private static bool Owns(NetworkConnectionToClient conn, uint netId, out RifleItem rifle)
        {
            rifle = null;
            if (!NetworkServer.spawned.TryGetValue(netId, out var identity)) return false;
            rifle = identity.GetComponent<RifleItem>();
            if (rifle == null) return false;
            // The holder has authority over the item; the host's own connection always passes.
            return conn == NetworkServer.localConnection || identity.connectionToClient == conn;
        }

        public static void OnFire(NetworkConnectionToClient conn, RifleFireMsg msg)
        {
            if (!Owns(conn, msg.NetId, out var rifle)) return;
            var s = Get(msg.NetId);
            if (s == null) return;
            if (s.Reloading) { s.CancelReload = true; return; }
            if (s.Busy) return;

            if (s.Chamber == Chamber.Live)
            {
                s.Chamber = Chamber.Spent;
                Broadcast(msg.NetId, RifleAction.Fire);
                ResolveShot(rifle, FloatingOriginManager.ToRenderWorld(msg.Origin), msg.Direction.normalized);
                Plugin.Instance.StartCoroutine(Cycle(msg.NetId, s, TriggerTime));
            }
            else if (s.Tube > 0)
            {
                Plugin.Instance.StartCoroutine(Cycle(msg.NetId, s, 0f));
            }
            else
            {
                Broadcast(msg.NetId, RifleAction.DryFire);
            }
            SendState(msg.NetId);
        }

        public static void OnReload(NetworkConnectionToClient conn, RifleReloadMsg msg)
        {
            if (!Owns(conn, msg.NetId, out _)) return;
            var s = Get(msg.NetId);
            if (s == null || s.Busy || s.Tube >= Plugin.TubeCapacity) return;
            string pocket = AmmoServer.PocketKey(conn);
            if (!Plugin.InfiniteReserve.Value && AmmoServer.Pocket(pocket) <= 0) return;
            Plugin.Instance.StartCoroutine(Reload(msg.NetId, s, conn, pocket));
        }

        private static IEnumerator Cycle(uint netId, State s, float delay)
        {
            s.Busy = true;
            if (delay > 0f) yield return new WaitForSeconds(delay);
            Broadcast(netId, RifleAction.Cycle);
            yield return new WaitForSeconds(CycleChamber);
            s.Chamber = Chamber.Empty;   // case thrown out at the top of the stroke
            if (s.Tube > 0) { s.Tube--; s.Chamber = Chamber.Live; }
            SendState(netId);
            yield return new WaitForSeconds(CycleLength - CycleChamber);
            s.Busy = false;
        }

        /// <summary>Loads rounds one at a time from the reloading player's pocket.</summary>
        private static IEnumerator Reload(uint netId, State s, NetworkConnectionToClient conn, string pocket)
        {
            s.Busy = true;
            s.Reloading = true;
            s.CancelReload = false;
            Broadcast(netId, RifleAction.ReloadStart);
            yield return new WaitForSeconds(ReloadStartLength);
            while (true)
            {
                Broadcast(netId, RifleAction.ReloadInsert);
                yield return new WaitForSeconds(InsertAt);
                if (Plugin.InfiniteReserve.Value || AmmoServer.TakeFromPocket(pocket, conn)) s.Tube++;
                SendState(netId);
                yield return new WaitForSeconds(InsertLength - InsertAt);
                bool outOfRounds = !Plugin.InfiniteReserve.Value && AmmoServer.Pocket(pocket) <= 0;
                if (s.CancelReload || s.Tube >= Plugin.TubeCapacity || outOfRounds) break;
            }
            Broadcast(netId, RifleAction.ReloadEnd);
            yield return new WaitForSeconds(ReloadEndLength);
            s.Reloading = false;
            s.Busy = false;
            if (s.Chamber != Chamber.Live && s.Tube > 0) Plugin.Instance.StartCoroutine(Cycle(netId, s, 0.05f));
        }

        private static void ResolveShot(RifleItem rifle, Vector3 origin, Vector3 dir)
        {
            var hits = Physics.RaycastAll(origin, dir, 250f, ~0, QueryTriggerInteraction.Collide);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            Transform shooter = rifle.transform.root;
            foreach (var hit in hits)
            {
                if (hit.transform.IsChildOf(shooter) || hit.transform.IsChildOf(rifle.transform)) continue;
                var zombie = hit.collider.GetComponentInParent<ZombieAgent>();
                if (hit.collider.isTrigger && zombie == null) continue;
                if (hit.collider.GetComponentInParent<NomadDrive.Features.Player.Player>() != null) continue;   // no friendly fire

                if (zombie != null)
                {
                    // The ray enters the body capsule first; it's a headshot if it also crosses this zombie's head.
                    bool headshot = false;
                    foreach (var h in hits)
                        if (h.collider.GetComponent<ZombieHead>() != null && h.collider.GetComponentInParent<ZombieAgent>() == zombie)
                            headshot = true;
                    ZombieDirector.ServerDamage(zombie, Plugin.RifleDamage.Value, headshot);
                    Blood.ServerSpray(hit.point, dir, headshot ? 1.3f : 0.8f);
                    if (UnityEngine.Random.value < 0.6f)
                        Blood.ServerGround(zombie.transform.position + dir * UnityEngine.Random.Range(0.4f, 1.2f), 0.55f);
                }
                else if (hit.rigidbody != null && !hit.rigidbody.isKinematic)
                    hit.rigidbody.AddForceAtPosition(dir * 8f, hit.point, ForceMode.Impulse);

                var impact = new ImpactMsg { Point = FloatingOriginManager.ToTrueWorld(hit.point), Normal = hit.normal };
                if (NetworkClient.active) Effects.OnImpact(impact);
                Net.SendToModded(impact);
                return;
            }
        }

        private static void Broadcast(uint netId, RifleAction action)
        {
            var msg = new RifleActionMsg { NetId = netId, Action = (byte)action };
            if (NetworkClient.active) RifleClient.OnAction(msg);   // host's own view
            Net.SendToModded(msg);
        }

        private static RifleStateMsg StateMsg(uint netId, State s) => new RifleStateMsg
        {
            NetId = netId,
            Tube = (byte)Mathf.Clamp(s.Tube, 0, 255),
            Chamber = (byte)s.Chamber,
            Reserve = (short)Plugin.TubeCapacity,   // tube size, so clients show the host's capacity
        };

        private static void SendState(uint netId)
        {
            if (!States.TryGetValue(netId, out var s)) return;
            var msg = StateMsg(netId, s);
            if (NetworkClient.active) RifleClient.OnState(msg);
            Net.SendToModded(msg);
        }

        public static void SendAllStates(NetworkConnectionToClient conn)
        {
            foreach (var kv in States) Net.SendTo(conn, StateMsg(kv.Key, kv.Value));
        }
    }
}
