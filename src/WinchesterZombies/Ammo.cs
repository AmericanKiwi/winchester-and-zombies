using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EvilCore.EvilSave;
using EvilCore.UI.Scripts;
using Mirror;
using NomadDrive.Features.Interaction;
using NomadDrive.Features.ObjectPlacement;
using NomadDrive.Features.SaveSystem;
using NomadDrive.Features.Tools;
using NomadDrive.Features.WorldGeneration.ObjectSpawning;
using UnityEngine;
using UnityEngine.AddressableAssets;
using Player = NomadDrive.Features.Player.Player;

namespace WinchesterZombies
{
    /// <summary>
    /// A box of .44-40 rounds. Pick it up and carry it like any small item; with the rifle in hand, look at a
    /// box and hold E to fill your pocket (the reverse of putting a battery in the grinder). Empty boxes vanish.
    /// </summary>
    public class AmmoBoxItem : HeldItem, INetworkSaveable
    {
        private enum BoxState { Idle, Fill, Equipped }

        private InteractionStateMachine<BoxState> _boxStates;
        private bool _hovered;
        private float _nextRefresh;

        protected override void InitializeStateMachine()
        {
            _boxStates = new InteractionStateMachine<BoxState>(this);
            BaseStateMachine = _boxStates;
            ConfigureStates();
            _boxStates.Initialize(DetermineBoxState());
        }

        protected override void ConfigureStates()
        {
            _boxStates
                .RegisterState(BoxState.Idle, c => WithEquipInteraction(c).WithCrosshair(CrosshairType.StartGrab).WithNameLabelVisibility(true))
                .RegisterState(BoxState.Fill, c => c.WithHoldInteraction(InteractionKey.Primary, "Fill pocket", RequestFill, 0.5f)
                    .WithCrosshair(CrosshairType.Interact).WithNameLabelVisibility(true).WithInteractionLabelVisibility(true))
                .RegisterState(BoxState.Equipped, c => c.WithCrosshair(CrosshairType.Default));
        }

        private BoxState DetermineBoxState()
        {
            if (IsEquipped) return BoxState.Equipped;
            bool rifleInHand = playerService?.EquipmentManager?.EquippedEntity is RifleItem;
            bool room = AmmoClient.Pocket >= 0 && AmmoClient.Pocket < AmmoClient.PocketCapacity;
            return rifleInHand && room && AmmoClient.BoxRounds(netId) > 0 ? BoxState.Fill : BoxState.Idle;
        }

        public override void UpdateState() => _boxStates?.TransitionTo(DetermineBoxState());

        public override Sprite InventoryIconOverride => Assets.AmmoIcon;

        protected override void OnHovered()
        {
            base.OnHovered();
            _hovered = true;
            UpdateState();
        }

        protected override void OnUnhovered()
        {
            base.OnUnhovered();
            _hovered = false;
        }

        private void Update()
        {
            // What E does depends on what's in your hand and your pocket, so keep it current while you look at it.
            if (_hovered && Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + 0.2f;
                UpdateState();
            }
            if (NetworkServer.active && Time.time >= _nextSupportCheck)
            {
                _nextSupportCheck = Time.time + 1f + UnityEngine.Random.value * 0.2f;
                ServerCheckSupport();
            }
        }

        private float _nextSupportCheck;
        private static readonly RaycastHit[] Below = new RaycastHit[8];

        /// <summary>
        /// Host: a box left resting on nothing (a release the game couldn't land, or one set on a vehicle's empty
        /// fitting spot) is let go so it settles on whatever is underneath. Only near players, where it can be seen.
        /// </summary>
        private void ServerCheckSupport()
        {
            if (IsEquipped || IsStowed || ZombieDirector.NearestAnyPlayer(transform.position) > 30f) return;
            var rb = GetComponent<Rigidbody>();
            if (rb == null || !rb.isKinematic) return;   // still moving: physics will land it
            Collider body = null;
            foreach (var c in GetComponentsInChildren<Collider>())
                if (!c.isTrigger && c.enabled) { body = c; break; }
            if (body == null) return;
            Bounds b = body.bounds;
            Vector3 from = new Vector3(b.center.x, b.min.y + 0.02f, b.center.z);
            int n = Physics.RaycastNonAlloc(from, Vector3.down, Below, 0.06f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (!Below[i].collider.transform.IsChildOf(transform)) return;
            Plugin.Log.LogInfo($"Ammo box {netId} was resting on nothing; letting it settle.");
            ServerReleaseFromLostSupport(null);
        }

        private void RequestFill()
        {
            if (!Net.HostHasMod) return;
            NetworkClient.Send(new AmmoTakeMsg { NetId = netId });
        }

        internal void ShowCount(int rounds) => interactableName = $".44-40 cartridges ({rounds})";

        // Saved with the world: rounds left in the box.
        public string ContributorKey => "wz.ammo";

        public void CaptureState(EvilWriter writer, ISaveContext ctx)
        {
            writer.Write((byte)1);
            writer.Write(AmmoServer.BoxRounds(netId));
        }

        public void RestoreSelfState(EvilReader reader, ISaveContext ctx)
        {
            reader.ReadByte();
            AmmoServer.SetBox(this, reader.ReadInt());
        }

        public void RestoreLinks(EvilReader reader, ISaveContext ctx) { }

        public override bool Weaved() => true;
    }

    /// <summary>
    /// Builds the networked ammo box prefabs from the game's grinder battery (a small pocketable item), one per
    /// box colour, the same way the rifle is built from the shovel.
    /// </summary>
    internal static class AmmoBoxFactory
    {
        public static readonly uint[] AssetIds = { 0x5A0EA440, 0x5A0EA441 };   // green, kraft
        public static readonly string[] SaveIds = { "wz:ammo-green", "wz:ammo-kraft" };
        public static readonly GameObject[] Prefabs = new GameObject[2];
        public static bool Built => Prefabs[0] != null && Prefabs[1] != null;

        private static bool _loading, _failed;
        private static Queue<string> _candidates;

        // Box size (from the bundle prefab): 0.116 x 0.039 x 0.066 m, resting on y = 0.
        private static readonly Vector3 BoxCentre = new Vector3(0f, 0.0195f, 0f);
        private static readonly Vector3 BoxSize = new Vector3(0.116f, 0.039f, 0.066f);

        public static void EnsureBuilt()
        {
            if (Built)
            {
                if (NetworkClient.active)
                    for (int i = 0; i < 2; i++)
                        if (!NetworkClient.prefabs.ContainsKey(AssetIds[i])) NetworkClient.RegisterPrefab(Prefabs[i], AssetIds[i]);
                return;
            }
            if (_loading || _failed) return;
            var registry = Resources.FindObjectsOfTypeAll<LootRegistry>().FirstOrDefault();
            if (registry == null) return;
            if (_candidates == null)
                _candidates = new Queue<string>(registry.Entries
                    .Where(e => (e.address ?? "").IndexOf("battery", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                (e.displayName ?? "").IndexOf("battery", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(e => e.address).Distinct());
            if (_candidates.Count == 0)
            {
                _failed = true;
                Plugin.Log.LogError("No grinder battery in the loot registry; ammo boxes can't be built.");
                return;
            }
            // Try each battery entry until one is the grinder's ConsumableBattery (not a car battery).
            string address = _candidates.Dequeue();
            _loading = true;
            Addressables.LoadAssetAsync<GameObject>(address).Completed += handle =>
            {
                _loading = false;
                var prefab = handle.Result;
                if (prefab == null || prefab.GetComponent<ConsumableBattery>() == null) return;
                try { for (int i = 0; i < 2; i++) Prefabs[i] = Build(prefab, i); }
                catch (Exception e)
                {
                    _failed = true;
                    Plugin.Log.LogError("Building the ammo box failed: " + e);
                    return;
                }
                Plugin.Log.LogInfo($"Ammo boxes built from '{prefab.name}'.");
            };
        }

        private static GameObject Build(GameObject batteryPrefab, int variant)
        {
            var holder = new GameObject("WZ_AmmoHolder");
            holder.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(holder);

            var clone = UnityEngine.Object.Instantiate(batteryPrefab, holder.transform);
            clone.name = variant == 0 ? "WZ_AmmoBox_Green" : "WZ_AmmoBox_Kraft";
            foreach (var r in clone.GetComponentsInChildren<Renderer>(true)) r.enabled = false;

            // Swap the battery behaviour for the ammo box, keeping the inherited pickup/carry settings.
            var battery = clone.GetComponent<ConsumableBattery>();
            var box = clone.AddComponent<AmmoBoxItem>();
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(battery), box);
            Retarget(clone, battery, box);
            UnityEngine.Object.DestroyImmediate(battery);
            box.ShowCount(Plugin.BoxRounds.Value);

            // The battery's body collider is a capsule; make it a box the carton's size. It stays on the same
            // object, which carries the InteractableRouter the game uses to tell what you're looking at. The
            // snapping-plane trigger (for setting things on top) is left as it is.
            foreach (var c in clone.GetComponentsInChildren<Collider>(true))
            {
                if (c.GetComponent<SnappingPlane>() != null) continue;
                var owner = c.transform;
                bool trigger = c.isTrigger;
                // The battery's collider child is turned (it stands the capsule up, at an odd yaw); square it with
                // the box, or the box collider comes out twisted against the carton and its corners poke through.
                if (owner != clone.transform)
                {
                    owner.localPosition = Vector3.zero;
                    owner.localRotation = Quaternion.identity;
                }
                UnityEngine.Object.DestroyImmediate(c);
                var bc = owner.gameObject.AddComponent<BoxCollider>();
                bc.isTrigger = trigger;
                bc.center = owner.InverseTransformPoint(clone.transform.TransformPoint(BoxCentre));
                Vector3 size = owner.InverseTransformVector(clone.transform.TransformVector(BoxSize));
                bc.size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
            }

            FitPlacement(clone, box);

            // The battery's network asset id came along with the clone; clear it so ours can be registered.
            typeof(NetworkIdentity).GetField("_assetId", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(clone.GetComponent<NetworkIdentity>(), 0u);

            var visual = UnityEngine.Object.Instantiate(Assets.AmmoBoxes[variant], clone.transform);
            visual.name = "AmmoBoxVisual";
            Assets.ApplyHdrp(visual);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;

            NetworkClient.RegisterPrefab(clone, AssetIds[variant]);
            return clone;
        }

        /// <summary>
        /// Placement and physics settings came from the battery, which stands on end. The box lies flat: no
        /// placement offset or start rotation, continuous collision so a throw can't sink it into a floor, and
        /// the battery's set-things-on-top surface moved to the box lid so boxes stack.
        /// </summary>
        private static void FitPlacement(GameObject clone, AmmoBoxItem box)
        {
            if (box.heldItemConfig != null)
            {
                var cfg = UnityEngine.Object.Instantiate(box.heldItemConfig);
                cfg.positionOffsetForPlacement = Vector3.zero;
                cfg.enterPlacingModeStartRotation = Vector3.zero;
                box.heldItemConfig = cfg;
            }

            foreach (var rb in clone.GetComponentsInChildren<Rigidbody>(true))
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // Stacking compares the footprint recorded on each item (the one below and the one going on top),
            // and the battery's is a standing cell. Record the carton's, so a box fits a box.
            var loot = clone.GetComponent<LootItemDefinition>();
            if (loot != null)
                typeof(LootItemDefinition).GetField("footprint", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(loot,
                    new LootFootprint { isBaked = true, size = BoxSize, centerOffset = BoxCentre });

            // The surface sits on the lid; its trigger is a slab rising 3 cm above it, for the placement cast to find.
            Vector3 lidTop = clone.transform.TransformPoint(new Vector3(0f, BoxSize.y, 0f));
            Vector3 slabCentre = clone.transform.TransformPoint(new Vector3(0f, BoxSize.y + 0.015f, 0f));
            Vector3 lidSize = clone.transform.TransformVector(new Vector3(BoxSize.x, 0.03f, BoxSize.z));
            foreach (var plane in clone.GetComponentsInChildren<SnappingPlane>(true))
            {
                // The surface's own position is where stacked items are set down (the battery's top, 9 cm up):
                // bring it to the lid, unscaled, with its trigger the lid's size.
                if (plane.transform != clone.transform)
                {
                    plane.transform.SetPositionAndRotation(lidTop, clone.transform.rotation);
                    plane.transform.localScale = Vector3.one;
                }
                foreach (var c in plane.GetComponents<BoxCollider>())
                {
                    c.center = plane.transform.InverseTransformPoint(slabCentre);
                    Vector3 s = plane.transform.InverseTransformVector(lidSize);
                    c.size = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
                }
            }
        }

        /// <summary>Host: spawn a full box in front of the camera.</summary>
        public static void ServerSpawnInFrontOfLocalPlayer()
        {
            var cam = Camera.main;
            if (cam == null) return;
            // Items spawn at rest (kinematic), so set it on the ground rather than leave it hanging in the air.
            var pos = cam.transform.position + cam.transform.forward * 1.2f;
            foreach (var h in Physics.RaycastAll(pos + Vector3.up * 0.5f, Vector3.down, 5f, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            {
                if (h.collider.GetComponentInParent<Player>() != null) continue;
                pos = h.point;
                break;
            }
            if (ServerSpawn(UnityEngine.Random.Range(0, 2), pos, Quaternion.Euler(0f, cam.transform.eulerAngles.y, 0f), Plugin.BoxRounds.Value) != null)
                Notice.Show("Ammo box spawned.");
        }

        public static AmmoBoxItem ServerSpawn(int variant, Vector3 renderPos, Quaternion rot, int rounds, string saveGuid = null)
        {
            if (!Built || !NetworkServer.active) return null;
            var go = UnityEngine.Object.Instantiate(Prefabs[variant], renderPos, rot);
            go.SetActive(true);
            ModSave.MakePersistent(go, SaveIds[variant], saveGuid);
            NetworkServer.Spawn(go, NetworkServer.localConnection);
            ModSave.ParentToWorld(go);
            var box = go.GetComponent<AmmoBoxItem>();
            AmmoServer.SetBox(box, rounds);
            return box;
        }

        private static void Retarget(GameObject root, Component from, Component to)
        {
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || mb == from || mb == to) continue;
                for (var t = mb.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (typeof(Component).IsAssignableFrom(f.FieldType) && ReferenceEquals(f.GetValue(mb), from) && f.FieldType.IsInstanceOfType(to))
                            f.SetValue(mb, to);
            }
        }
    }

    /// <summary>
    /// The game lets a placed item hang on a wall (the battery's wall rule: back to the wall), which for a
    /// small carton just looks like it's floating. Ammo boxes only go down on floor-like surfaces: aiming at a
    /// wall or a ceiling shows the can't-place ghost instead.
    /// </summary>
    internal static class AmmoPlacement
    {
        public static void Patch(HarmonyLib.Harmony harmony)
        {
            var target = HarmonyLib.AccessTools.Method(typeof(DropCastHelper), nameof(DropCastHelper.ProcessCastResult));
            if (target == null)
            {
                Plugin.Log.LogWarning("DropCastHelper.ProcessCastResult not found; ammo boxes can still be hung on walls.");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyLib.HarmonyMethod(typeof(AmmoPlacement), nameof(FloorOnly)));

            // Vehicles (the van's inside walls) carry their own snap surfaces, which are checked here instead.
            var plane = HarmonyLib.AccessTools.Method(typeof(SnappingPlane), nameof(SnappingPlane.EvaluatePlacement),
                new[] { typeof(GameObject), typeof(PlacementSurfaceClass?) });
            if (plane != null) harmony.Patch(plane, postfix: new HarmonyLib.HarmonyMethod(typeof(AmmoPlacement), nameof(FloorPlanesOnly)));
            else Plugin.Log.LogWarning("SnappingPlane.EvaluatePlacement not found; ammo boxes can still be hung on vehicle walls.");

            var snap = HarmonyLib.AccessTools.Method(typeof(ObjectPlacementManager), "TryGetValidSnappingPlane",
                new[] { typeof(RaycastHit), typeof(SnappingPlane).MakeByRefType(), typeof(SnapFitResult).MakeByRefType() });
            if (snap != null) harmony.Patch(snap, postfix: new HarmonyLib.HarmonyMethod(typeof(AmmoPlacement), nameof(BackedSurfacesOnly)));
            else Plugin.Log.LogWarning("ObjectPlacementManager.TryGetValidSnappingPlane not found; ammo boxes can still sit on empty fitting spots.");
        }

        private static void FloorPlanesOnly(SnappingPlane __instance, GameObject targetObject, PlacementSurfaceClass? surfaceClass, ref SnapFitResult __result)
        {
            if (__result != SnapFitResult.Accepted || targetObject == null || targetObject.GetComponent<AmmoBoxItem>() == null) return;
            var cls = surfaceClass ?? PlacementFaceResolver.Classify(__instance.transform.up, Vector3.up);
            if (cls != PlacementSurfaceClass.Floor) __result = SnapFitResult.NotAllowed;
        }

        /// <summary>
        /// Vehicles carry snap surfaces for fittings that may not be installed (a shelf, a bed), hanging in the
        /// air. Where you aim at one, a box only takes it if something solid is right under that spot: a floor,
        /// a seat, another box.
        /// </summary>
        private static void BackedSurfacesOnly(ObjectPlacementManager __instance, RaycastHit hitInfo, ref SnappingPlane snappingPlane, ref SnapFitResult fit, ref bool __result)
        {
            if (!__result) return;
            var guest = __instance.CurrentPlacementObject;
            if (guest == null || guest.GetComponent<AmmoBoxItem>() == null || BackedAt(hitInfo.point, guest)) return;
            __result = false;
            snappingPlane = null;
            fit = SnapFitResult.NotAllowed;
        }

        private static bool BackedAt(Vector3 point, GameObject guest)
        {
            int n = Physics.RaycastNonAlloc(point + Vector3.up * 0.03f, Vector3.down, SupportHits, 0.12f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (!SupportHits[i].collider.transform.IsChildOf(guest.transform)) return true;
            return false;
        }

        private static readonly RaycastHit[] SupportHits = new RaycastHit[8];

        private static void FloorOnly(HeldItem item, ref DropResult __result)
        {
            if (!__result.Success || !(item is AmmoBoxItem)) return;
            if (PlacementFaceResolver.Classify(__result.HitInfo.normal, Vector3.up) != PlacementSurfaceClass.Floor
                || !BackedAt(__result.HitInfo.point, item.gameObject))
                __result = DropResult.Failed;
        }
    }

    /// <summary>
    /// Host side of ammo: rounds left in each box, and each player's pocket (spare rounds the T reload uses).
    /// Pockets are keyed by the player's account id so they follow the player across saves.
    /// </summary>
    internal static class AmmoServer
    {
        private static readonly Dictionary<uint, int> Boxes = new Dictionary<uint, int>();
        private static readonly Dictionary<string, int> Pockets = new Dictionary<string, int>();
        private static readonly HashSet<int> PocketSent = new HashSet<int>();

        public static void Clear()
        {
            Boxes.Clear();
            Pockets.Clear();
            PocketSent.Clear();
        }

        // ---------------------------------------------------------------- boxes

        public static int BoxRounds(uint netId) => Boxes.TryGetValue(netId, out var n) ? n : Plugin.BoxRounds.Value;

        public static void SetBox(AmmoBoxItem box, int rounds)
        {
            if (box == null) return;
            Boxes[box.netId] = Mathf.Max(0, rounds);
            BroadcastBox(box.netId);
        }

        private static void BroadcastBox(uint netId)
        {
            var msg = new BoxStateMsg { NetId = netId, Rounds = (short)Mathf.Clamp(BoxRounds(netId), 0, 32767) };   // short.MaxValue is stripped from the game's build
            if (NetworkClient.active) AmmoClient.OnBoxState(msg);
            Net.SendToModded(msg);
        }

        /// <summary>A player held E on a box with the rifle in hand: move rounds from the box to their pocket.</summary>
        public static void OnTake(NetworkConnectionToClient conn, AmmoTakeMsg msg)
        {
            if (!NetworkServer.spawned.TryGetValue(msg.NetId, out var identity)) return;
            var box = identity.GetComponent<AmmoBoxItem>();
            if (box == null || conn?.identity == null) return;
            if ((conn.identity.transform.position - box.transform.position).sqrMagnitude > 5f * 5f) return;

            string key = PocketKey(conn);
            int space = Plugin.PocketCapacity.Value - Pocket(key);
            int moved = Mathf.Min(space, BoxRounds(msg.NetId));
            if (moved <= 0) return;
            Pockets[key] = Pocket(key) + moved;
            Boxes[msg.NetId] = BoxRounds(msg.NetId) - moved;
            SendPocket(conn, key);
            BroadcastBox(msg.NetId);
            if (Boxes[msg.NetId] <= 0)
            {
                Boxes.Remove(msg.NetId);
                NetworkServer.Destroy(box.gameObject);
            }
        }

        // ---------------------------------------------------------------- pockets

        public static string PocketKey(NetworkConnectionToClient conn)
        {
            var player = conn?.identity != null ? conn.identity.GetComponent<Player>() : null;
            string id = player != null ? player.EosProductUserId : null;
            return string.IsNullOrEmpty(id) ? "conn:" + (conn?.connectionId ?? -1) : id;
        }

        public static int Pocket(string key) => Pockets.TryGetValue(key, out var n) ? n : 0;

        public static bool TakeFromPocket(string key, NetworkConnectionToClient conn)
        {
            if (Pocket(key) <= 0) return false;
            Pockets[key] = Pocket(key) - 1;
            SendPocket(conn, key);
            return true;
        }

        private static void SendPocket(NetworkConnectionToClient conn, string key)
        {
            var msg = new PocketMsg
            {
                Rounds = (short)(Plugin.InfiniteReserve.Value ? -1 : Pocket(key)),
                Capacity = (short)Plugin.PocketCapacity.Value,
            };
            if (conn == NetworkServer.localConnection) AmmoClient.OnPocket(msg);
            else Net.SendTo(conn, msg);
        }

        /// <summary>Tells each player their pocket once they're in the world (and the host its own).</summary>
        public static void Tick()
        {
            foreach (var conn in NetworkServer.connections.Values)
            {
                if (conn?.identity == null || PocketSent.Contains(conn.connectionId)) continue;
                if (conn != NetworkServer.localConnection && !Net.IsModded(conn)) continue;
                PocketSent.Add(conn.connectionId);
                SendPocket(conn, PocketKey(conn));
            }
        }

        public static void SendAllStates(NetworkConnectionToClient conn)
        {
            foreach (var kv in Boxes) Net.SendTo(conn, new BoxStateMsg { NetId = kv.Key, Rounds = (short)kv.Value });
            PocketSent.Remove(conn.connectionId);   // re-sent by Tick once their player exists
        }

        // ---------------------------------------------------------------- save (world state blob)

        public static void WritePockets(EvilWriter w)
        {
            w.Write(Pockets.Count);
            foreach (var kv in Pockets) { w.Write(kv.Key); w.Write(kv.Value); }
        }

        public static void ReadPockets(EvilReader r)
        {
            int n = r.ReadInt();
            for (int i = 0; i < n; i++) Pockets[r.ReadString()] = r.ReadInt();
            PocketSent.Clear();
        }
    }

    /// <summary>Client side: this player's pocket and the round count of every box (for labels and the E prompt).</summary>
    internal static class AmmoClient
    {
        public static int Pocket { get; private set; } = 0;
        public static int PocketCapacity { get; private set; } = 7;
        private static readonly Dictionary<uint, int> Boxes = new Dictionary<uint, int>();

        public static void Clear()
        {
            Pocket = 0;
            Boxes.Clear();
        }

        public static int BoxRounds(uint netId) => Boxes.TryGetValue(netId, out var n) ? n : 0;

        public static void OnPocket(PocketMsg msg)
        {
            Pocket = msg.Rounds;
            PocketCapacity = msg.Capacity;
        }

        public static void OnBoxState(BoxStateMsg msg)
        {
            Boxes[msg.NetId] = msg.Rounds;
            if (NetworkClient.spawned.TryGetValue(msg.NetId, out var id) && id.TryGetComponent<AmmoBoxItem>(out var box))
                box.ShowCount(msg.Rounds);
        }
    }
}
