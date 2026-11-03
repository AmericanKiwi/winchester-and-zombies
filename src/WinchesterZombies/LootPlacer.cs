using System;
using System.Collections.Generic;
using System.Reflection;
using Mirror;
using NomadDrive.Features.FloatingOrigin;
using NomadDrive.Features.Interaction;
using NomadDrive.Features.WorldGeneration;
using NomadDrive.Features.WorldGeneration.ObjectSpawning;
using UnityEngine;
using Random = UnityEngine.Random;

namespace WinchesterZombies
{
    /// <summary>
    /// Host: puts rifles and ammo boxes in the world. A new world (or one saved before the mod) gets one rifle and
    /// one box per player slot at the start; each loot location is rolled once, when a player first gets near,
    /// for a rifle and an ammo box placed among the game's own loot there. Both are remembered in the save.
    /// </summary>
    internal static class LootPlacer
    {
        private const float SiteRange = 150f;      // roll a location once a player is this close
        private const float LootReadyRange = 40f;  // ...or this close, if the game placed no loot points there

        private static float _next;
        private static LootSpawnManager[] _sites = new LootSpawnManager[0];
        private static float _sitesAt;
        private static readonly List<Vector3> Players = new List<Vector3>();

        public static void Tick()
        {
            if (!NetworkServer.active) return;
            AmmoServer.Tick();
            var state = WorldState.Instance;
            if (state == null || !state.Ready || RifleFactory.Prefab == null || !AmmoBoxFactory.Built) return;
            if (Time.time < _next) return;
            _next = Time.time + 2f;

            if (!state.StartPlaced && PlaceStart()) state.StartPlaced = true;
            RollSites(state);
        }

        // ---------------------------------------------------------------- start area

        private static bool PlaceStart()
        {
            var host = NetworkClient.localPlayer != null ? NetworkClient.localPlayer.transform : null;
            if (host == null) return false;
            int slots = PlayerSlots();
            int rifles = Plugin.StartRifles.Value > 0 ? Plugin.StartRifles.Value : slots;
            int boxes = Plugin.StartAmmoBoxes.Value > 0 ? Plugin.StartAmmoBoxes.Value : slots;

            // Face open ground: the first direction (from where the host is looking, then round) whose patch
            // 2.5-3.5 m out is level ground, not a vehicle or a building.
            Vector3 look = Vector3.ProjectOnPlane(host.forward, Vector3.up).normalized;
            if (look.sqrMagnitude < 0.01f) look = Vector3.forward;
            Vector3 fwd = look;
            foreach (float turn in new[] { 0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f })
            {
                Vector3 dir = Quaternion.Euler(0f, turn, 0f) * look;
                if (OpenGround(host.position, host.position + dir * 2.5f) && OpenGround(host.position, host.position + dir * 3.5f)) { fwd = dir; break; }
            }
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            float yaw = Quaternion.LookRotation(fwd).eulerAngles.y;

            // A row of rifles 2.5 m ahead, lying side by side, and a row of boxes just beyond them.
            for (int i = 0; i < rifles; i++)
            {
                Vector3 p = Ground(host.position + fwd * 2.5f + right * ((i - (rifles - 1) / 2f) * 0.45f));
                RifleFactory.ServerSpawn(p + Vector3.up * 0.05f, RifleFactory.LyingFlat(yaw + 90f), Plugin.TubeCapacity, Chamber.Live);
            }
            for (int i = 0; i < boxes; i++)
            {
                Vector3 p = Ground(host.position + fwd * 3.3f + right * ((i - (boxes - 1) / 2f) * 0.25f));
                AmmoBoxFactory.ServerSpawn(i % 2, p + Vector3.up * 0.02f, Quaternion.Euler(0f, yaw, 0f), Plugin.BoxRounds.Value);
            }
            Plugin.Log.LogInfo($"Placed {rifles} rifle(s) and {boxes} ammo box(es) at the start ({slots} player slot(s)), " +
                $"{Vector3.SignedAngle(look, fwd, Vector3.up):F0} degrees from where the host faces.");
            return true;
        }

        /// <summary>Ground at <paramref name="p"/> is static (no vehicle or loose object) and near the host's foot level.</summary>
        private static bool OpenGround(Vector3 feet, Vector3 p)
        {
            var hits = Physics.RaycastAll(p + Vector3.up * 3f, Vector3.down, 6f, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                if (h.collider.GetComponentInParent<NomadDrive.Features.Player.Player>() != null) continue;
                return h.collider.attachedRigidbody == null && Mathf.Abs(h.point.y - feet.y) < 0.6f;
            }
            return false;
        }

        /// <summary>The session's player capacity, from the lobby settings the world was hosted with.</summary>
        private static int PlayerSlots()
        {
            try
            {
                var gen = UnityEngine.Object.FindAnyObjectByType<WorldGenerator>();
                var lobby = gen != null ? typeof(WorldGenerator).GetField("_lobbyManager", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(gen) : null;
                var max = lobby?.GetType().GetProperty("PendingMaxPlayers")?.GetValue(lobby);
                if (max is uint n && n > 0) return Mathf.Clamp((int)n, 1, 16);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't read the lobby size: " + e.Message); }
            return Mathf.Max(1, NetworkServer.connections.Count);
        }

        // ---------------------------------------------------------------- loot locations

        private static void RollSites(WorldState state)
        {
            if (Time.time >= _sitesAt)
            {
                _sites = UnityEngine.Object.FindObjectsByType<LootSpawnManager>(FindObjectsSortMode.None);
                _sitesAt = Time.time + 10f;
            }
            Players.Clear();
            foreach (var conn in NetworkServer.connections.Values)
                if (conn?.identity != null) Players.Add(conn.identity.transform.position);
            if (Players.Count == 0) return;

            foreach (var site in _sites)
            {
                if (site == null) continue;
                Vector3 p = site.transform.position;
                float near = 1e9f;   // float.MaxValue is stripped from the game's build
                foreach (var pl in Players) near = Mathf.Min(near, Vector3.Distance(pl, p));
                if (near > SiteRange) continue;
                var points = LootPoints(site.transform);
                if (points.Count == 0 && near > LootReadyRange) continue;   // the game hasn't placed its loot yet

                if (!state.RolledSites.Add(SiteKey(p))) continue;
                if (Random.value < Plugin.LootRifleChance.Value)
                {
                    Vector3 at = NearPoint(points, p);
                    RifleFactory.ServerSpawn(at + Vector3.up * 0.05f, RifleFactory.LyingFlat(Random.Range(0f, 360f)), Random.Range(0, 4), Chamber.Empty);
                    Plugin.Log.LogInfo($"Rifle placed at a loot location ({FloatingOriginManager.ToTrueWorld(at)}).");
                }
                if (Random.value < Plugin.LootAmmoChance.Value)
                {
                    Vector3 at = NearPoint(points, p);
                    AmmoBoxFactory.ServerSpawn(Random.Range(0, 2), at + Vector3.up * 0.02f, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f), Plugin.BoxRounds.Value);
                    Plugin.Log.LogInfo($"Ammo box placed at a loot location ({FloatingOriginManager.ToTrueWorld(at)}).");
                }
            }
        }

        /// <summary>The spots where the game put this location's loot.</summary>
        private static List<Transform> LootPoints(Transform site)
        {
            var list = new List<Transform>();
            foreach (Transform child in site)
                if (child.name.StartsWith("LootSpawnPoint_", StringComparison.Ordinal)) list.Add(child);
            return list;
        }

        /// <summary>Beside one of the location's loot spots, on whatever surface it's on.</summary>
        private static Vector3 NearPoint(List<Transform> points, Vector3 fallback)
        {
            Vector3 p = points.Count > 0 ? points[Random.Range(0, points.Count)].position : fallback;
            var off = Random.insideUnitCircle * 0.3f;
            return Surface(p + new Vector3(off.x, 0f, off.y), 0.5f, 2f);
        }

        private static string SiteKey(Vector3 renderPos)
        {
            Vector3 t = FloatingOriginManager.ToTrueWorld(renderPos);
            return $"{Mathf.RoundToInt(t.x / 2f)}:{Mathf.RoundToInt(t.y / 2f)}:{Mathf.RoundToInt(t.z / 2f)}";
        }

        // ---------------------------------------------------------------- surfaces

        private static Vector3 Ground(Vector3 p) => Surface(p, 3f, 10f);

        /// <summary>The first solid surface below <paramref name="p"/> (searching from <paramref name="above"/> over it), ignoring players and items.</summary>
        private static Vector3 Surface(Vector3 p, float above, float depth)
        {
            var hits = Physics.RaycastAll(p + Vector3.up * above, Vector3.down, above + depth, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                if (h.collider.GetComponentInParent<NomadDrive.Features.Player.Player>() != null) continue;
                if (h.collider.GetComponentInParent<HeldItem>() != null) continue;
                return h.point;
            }
            return p;
        }
    }
}
