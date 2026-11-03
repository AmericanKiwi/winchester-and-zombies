using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using EvilCore.EvilSave;
using HarmonyLib;
using Mirror;
using NomadDrive.Features.FloatingOrigin;
using NomadDrive.Features.SaveSystem;
using NomadDrive.Features.WorldGeneration;
using UnityEngine;

namespace WinchesterZombies
{
    /// <summary>
    /// Puts mod objects in the game's own world save. Each rifle and ammo box is a PersistentObject whose
    /// "addressable" id is one of ours (wz:...), so the game saves its position, hotbar slot and our state blob
    /// like any item. The game respawns saved objects by loading that id as an addressable; a patch on that
    /// step spawns ours instead. Without the mod those records fail to load and the game skips them.
    /// </summary>
    internal static class ModSave
    {
        public const string StateId = "wz:state";
        private static float _readyAt = -1f;
        private static float _nextWorldCheck;
        private static WorldGenerator _world;

        public static void Patch(string harmonyId)
        {
            var harmony = new Harmony(harmonyId);
            AmmoPlacement.Patch(harmony);
            var target = AccessTools.Method(typeof(GameSaveService), "SpawnFromRecordAsync");
            if (target == null)
            {
                Plugin.Log.LogError("GameSaveService.SpawnFromRecordAsync not found; rifles and ammo won't load from saves.");
                return;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(ModSave), nameof(SpawnFromRecordPrefix)));
        }

        private static bool SpawnFromRecordPrefix(ObjectRecord record, ref UniTask<PersistentObject> __result)
        {
            if (record == null || record.AddressableGuid == null || !record.AddressableGuid.StartsWith("wz:", StringComparison.Ordinal))
                return true;
            __result = SpawnModRecord(record);
            return false;
        }

        private static async UniTask<PersistentObject> SpawnModRecord(ObjectRecord record)
        {
            // Saves load while the game starts a world; the prefabs are built from game items just before.
            float giveUpAt = Time.realtimeSinceStartup + 60f;
            while ((RifleFactory.Prefab == null || !AmmoBoxFactory.Built) && Time.realtimeSinceStartup < giveUpAt)
                await UniTask.Yield();

            Vector3 pos = FloatingOriginManager.ToRenderWorld(record.Position);
            GameObject go = null;
            string id = record.AddressableGuid;
            if (id == RifleFactory.SaveId)
                go = RifleFactory.ServerSpawn(pos, record.Rotation, 0, Chamber.Empty, record.Guid)?.gameObject;
            else if (id == AmmoBoxFactory.SaveIds[0] || id == AmmoBoxFactory.SaveIds[1])
                go = AmmoBoxFactory.ServerSpawn(id == AmmoBoxFactory.SaveIds[0] ? 0 : 1, pos, record.Rotation, Plugin.BoxRounds.Value, record.Guid)?.gameObject;
            else if (id == StateId)
                go = WorldState.Create(record.Guid).gameObject;

            if (go == null) Plugin.Log.LogWarning($"Couldn't restore saved '{id}'.");
            return go != null ? go.GetComponent<PersistentObject>() : null;
        }

        public static void MakePersistent(GameObject go, string saveId, string saveGuid) =>
            PersistentObject.ServerEnsure(go, saveId, saveGuid);

        /// <summary>Same as the game does for restored items: keep them under the floating-origin world root.</summary>
        public static void ParentToWorld(GameObject go)
        {
            var origin = FloatingOriginManager.Instance;
            if (origin != null && origin.WorldContentRoot != null && go.GetComponentInChildren<IFloatingOriginShiftable>(true) == null)
                go.transform.SetParent(origin.WorldContentRoot, true);
        }

        /// <summary>
        /// Host: a world without our state (new, or saved before the mod) gets one once loading has settled.
        /// That's when the start-area rifles and boxes are placed (see LootPlacer).
        /// </summary>
        public static void Tick()
        {
            if (!NetworkServer.active || WorldState.Instance != null) { _readyAt = -1f; return; }
            // The game restores a save once the world is fully generated (and moves the host to where they saved).
            if (Time.time >= _nextWorldCheck)
            {
                _nextWorldCheck = Time.time + 1f;
                _world = UnityEngine.Object.FindAnyObjectByType<WorldGenerator>();
            }
            bool settled = NetworkClient.localPlayer != null && _world != null && _world.IsWorldFullyReady && !GameSaveService.IsRestoringWorld;
            if (!settled) { _readyAt = -1f; return; }
            if (_readyAt < 0f) _readyAt = Time.time + 3f;
            if (Time.time < _readyAt) return;
            WorldState.Create(null);
            Plugin.Log.LogInfo("Winchester & Zombies world state created.");
        }

        public static void Clear()
        {
            if (WorldState.Instance != null) UnityEngine.Object.Destroy(WorldState.Instance.gameObject);
            _readyAt = -1f;
            _world = null;
        }
    }

    /// <summary>
    /// Per-world mod state, saved as a hidden object in the world save: whether the start-area rifles and boxes
    /// were placed, which loot locations have been rolled for rifles/ammo, and every player's pocket.
    /// </summary>
    internal class WorldState : MonoBehaviour, INetworkSaveable
    {
        public static WorldState Instance { get; private set; }

        public bool StartPlaced;
        public readonly HashSet<string> RolledSites = new HashSet<string>();

        /// <summary>
        /// False while a save is still being restored into this state (objects respawn first, then their saved
        /// data is applied), so nothing is placed twice.
        /// </summary>
        public bool Ready => (_ready || Time.time > _createdAt + 60f) && !GameSaveService.IsRestoringWorld;
        private bool _ready;
        private float _createdAt;

        private void Awake() => _createdAt = Time.time;

        public static WorldState Create(string saveGuid)
        {
            if (Instance != null) Destroy(Instance.gameObject);
            var go = new GameObject("WZ_WorldState");
            Instance = go.AddComponent<WorldState>();
            Instance._ready = saveGuid == null;   // a restored state is ready once its saved data is applied
            PersistentObject.ServerEnsure(go, ModSave.StateId, saveGuid);
            return Instance;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public string ContributorKey => "wz.state";

        public void CaptureState(EvilWriter writer, ISaveContext ctx)
        {
            writer.Write((byte)1);
            writer.Write(StartPlaced);
            writer.Write(RolledSites.Count);
            foreach (var s in RolledSites) writer.Write(s);
            AmmoServer.WritePockets(writer);
        }

        public void RestoreSelfState(EvilReader reader, ISaveContext ctx)
        {
            reader.ReadByte();
            StartPlaced = reader.ReadBool();
            int n = reader.ReadInt();
            for (int i = 0; i < n; i++) RolledSites.Add(reader.ReadString());
            AmmoServer.ReadPockets(reader);
            _ready = true;
        }

        public void RestoreLinks(EvilReader reader, ISaveContext ctx) { }
    }
}
