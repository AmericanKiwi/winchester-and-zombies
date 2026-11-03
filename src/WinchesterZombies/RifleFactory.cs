using System;
using System.Linq;
using System.Reflection;
using Mirror;
using NomadDrive.Features.Digging;
using NomadDrive.Features.Interaction;
using NomadDrive.Features.Interaction.IK;
using NomadDrive.Features.WorldGeneration.ObjectSpawning;
using RootMotion.FinalIK;
using HeldItemConfig = NomadDrive.Features.Interaction.HeldItemConfig;
using PolaroidCamera = NomadDrive.Features.PolaroidCamera.PolaroidCamera;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace WinchesterZombies
{
    /// <summary>
    /// Builds the rifle's networked prefab at runtime from the game's shovel: the shovel already handles
    /// pickup, hotbar, two-handed carry, dropping and network sync. Its renderers are hidden, the Winchester
    /// model is added, and the Shovel behaviour is swapped for <see cref="RifleItem"/>. Every client builds the
    /// same prefab and registers it under <see cref="AssetId"/>, so host spawns appear for everyone.
    /// </summary>
    internal static class RifleFactory
    {
        public const uint AssetId = 0x5A0E1873;   // fixed so all players agree

        public static GameObject Prefab { get; private set; }
        private static bool _loading;
        private static bool _failed;

        public static void EnsureBuilt()
        {
            if (Prefab != null)
            {
                if (NetworkClient.active && !NetworkClient.prefabs.ContainsKey(AssetId))
                    NetworkClient.RegisterPrefab(Prefab, AssetId);
                return;
            }
            if (_loading || _failed) return;

            var registry = Resources.FindObjectsOfTypeAll<LootRegistry>().FirstOrDefault();
            if (registry == null) return;   // game not far enough into loading yet
            string shovelAddress = FindEntry(registry, "shovel");
            if (shovelAddress == null)
            {
                _failed = true;
                Plugin.Log.LogError("Shovel not found in the loot registry; the rifle can't be built.");
                return;
            }
            // The Polaroid camera's free-hand grip gives the rifle a left hand on the forend (optional).
            string cameraAddress = FindEntry(registry, "polaroid");

            _loading = true;
            Addressables.LoadAssetAsync<GameObject>(shovelAddress).Completed += handle =>
            {
                if (handle.Result == null)
                {
                    _loading = false;
                    _failed = true;
                    Plugin.Log.LogError($"Couldn't load '{shovelAddress}'.");
                    return;
                }
                if (cameraAddress == null) { Finish(handle.Result, null); return; }
                Addressables.LoadAssetAsync<GameObject>(cameraAddress).Completed += camHandle =>
                    Finish(handle.Result, camHandle.Result);
            };
        }

        /// <summary>Addressable address of the first loot entry whose address or name contains <paramref name="word"/>.</summary>
        private static string FindEntry(LootRegistry registry, string word) =>
            registry.Entries.FirstOrDefault(x =>
                (x.address ?? "").IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (x.displayName ?? "").IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)?.address;

        private static void Finish(GameObject shovelPrefab, GameObject cameraPrefab)
        {
            _loading = false;
            try { Build(shovelPrefab, cameraPrefab); }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Log.LogError("Building the rifle prefab failed: " + e);
            }
        }

        private static void Build(GameObject shovelPrefab, GameObject cameraPrefab)
        {
            // Inactive holder so the clone's Awake/OnEnable don't run until it is spawned for real.
            var holder = new GameObject("WZ_PrefabHolder");
            holder.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(holder);

            var clone = UnityEngine.Object.Instantiate(shovelPrefab, holder.transform);
            clone.name = "WZ_Winchester1873";

            // Work out where the shovel sits in its own space so the rifle can overlay it.
            Bounds shovelBounds = LocalBounds(clone);
            foreach (var r in clone.GetComponentsInChildren<Renderer>(true)) r.enabled = false;

            // Swap the behaviour, keeping all inherited HeldItem/Interactable settings.
            var shovel = clone.GetComponent<Shovel>();
            var rifle = clone.AddComponent<RifleItem>();
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(shovel), rifle);
            Retarget(clone, shovel, rifle);
            UnityEngine.Object.DestroyImmediate(shovel);

            rifle.interactableName = "Winchester 1873";
            if (rifle.heldItemConfig != null)
            {
                var cfg = UnityEngine.Object.Instantiate(rifle.heldItemConfig);
                cfg.name = "WZ_RifleHeldItemConfig";
                rifle.heldItemConfig = cfg;
                _holdConfig = cfg;
            }

            // Winchester model, long axis laid along the shovel's long axis and centred on it.
            var visual = UnityEngine.Object.Instantiate(Assets.Rifle, clone.transform);
            visual.name = "WinchesterVisual";
            Assets.ApplyHdrp(visual);
            var axis = LongestAxis(shovelBounds.size);
            visual.transform.localRotation = Quaternion.FromToRotation(Vector3.forward, axis);
            visual.transform.localPosition = shovelBounds.center - visual.transform.localRotation * new Vector3(0f, -0.04f, 0.1f);
            rifle.Visual = visual.AddComponent<RifleVisual>();
            rifle.Visual.Init(visual.transform);

            SetUpHands(clone, rifle, visual.transform, cameraPrefab);

            // A network asset id baked into the shovel would block registering ours.
            typeof(NetworkIdentity).GetField("_assetId", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(clone.GetComponent<NetworkIdentity>(), 0u);

            NetworkClient.RegisterPrefab(clone, AssetId);
            Prefab = clone;
            Plugin.Log.LogInfo($"Rifle prefab built from '{shovelPrefab.name}' (long axis {axis}).");
        }

        public const string SaveId = "wz:rifle";

        /// <summary>Host: spawn a loaded rifle 1.5 m in front of the camera.</summary>
        public static void ServerSpawnInFrontOfLocalPlayer()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var pos = cam.transform.position + cam.transform.forward * 1.5f;
            if (ServerSpawn(pos, LyingFlat(cam.transform.eulerAngles.y), Plugin.TubeCapacity, Chamber.Live) != null)
                Notice.Show("Winchester 1873 spawned.");
        }

        /// <summary>The rifle's resting rotation on the ground, turned to <paramref name="yaw"/>.</summary>
        public static Quaternion LyingFlat(float yaw) => Quaternion.Euler(0f, yaw, 90f);

        /// <summary>
        /// Host: spawns a rifle with the given load and makes it part of the world save. <paramref name="saveGuid"/>
        /// is set when the save system is restoring this rifle.
        /// </summary>
        public static RifleItem ServerSpawn(Vector3 renderPos, Quaternion rot, int tube, Chamber chamber, string saveGuid = null)
        {
            if (Prefab == null || !NetworkServer.active) return null;
            var go = UnityEngine.Object.Instantiate(Prefab, renderPos, rot);
            go.SetActive(true);
            ModSave.MakePersistent(go, SaveId, saveGuid);
            NetworkServer.Spawn(go, NetworkServer.localConnection);
            ModSave.ParentToWorld(go);
            var rifle = go.GetComponent<RifleItem>();
            RifleServer.SetLoad(rifle.netId, tube, chamber);
            return rifle;
        }

        private static Bounds LocalBounds(GameObject root)
        {
            var inv = root.transform.worldToLocalMatrix;
            Bounds? b = null;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var m = inv * mf.transform.localToWorldMatrix;
                var mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = m.MultiplyPoint3x4(corner);
                    if (b == null) b = new Bounds(p, Vector3.zero);
                    else { var bb = b.Value; bb.Encapsulate(p); b = bb; }
                }
            }
            return b ?? new Bounds(Vector3.zero, Vector3.one);
        }

        /// <summary>
        /// The shovel's only hand target is a right hand halfway up its handle, which on the rifle put the right
        /// hand out by the barrel. Keep that grip (its fingers wrap a handle along the same axis as the rifle's
        /// wrist) but let <see cref="RifleItem"/> place it on the wrist, and add the Polaroid camera's free-hand
        /// grip as a left hand under the forend. Both are placed in the rifle model's space every frame.
        /// </summary>
        private static void SetUpHands(GameObject clone, RifleItem rifle, Transform visual, GameObject cameraPrefab)
        {
            var setup = clone.GetComponent<HeldItemIKSetup>();
            var rightIO = setup != null ? setup.RightHandInteraction : null;
            var right = rightIO != null ? rightIO.GetComponentsInChildren<InteractionTarget>(true)
                .FirstOrDefault(t => t.effectorType == FullBodyBipedEffector.RightHand) : null;
            if (right != null)
            {
                // The grip relative to the shovel handle's axis (the handle colliders sit at z = -0.05 on the
                // shovel's long axis), expressed in the rifle model's space.
                Vector3 axisInVisual = visual.InverseTransformPoint(clone.transform.TransformPoint(
                    new Vector3(0f, clone.transform.InverseTransformPoint(right.transform.position).y, -0.05f)));
                rifle.RightHand = right.transform;
                rifle.RightGripOffset = visual.InverseTransformPoint(right.transform.position) - axisInVisual;
                rifle.RightGripRotation = Quaternion.Inverse(visual.rotation) * right.transform.rotation;
            }
            else Plugin.Log.LogWarning("The shovel has no right-hand grip; the rifle keeps the default hold.");

            var cam = cameraPrefab != null ? cameraPrefab.GetComponentInChildren<PolaroidCamera>(true) : null;
            var supportField = typeof(PolaroidCamera).GetField("supportHandGrip", BindingFlags.Instance | BindingFlags.NonPublic);
            var support = cam != null ? supportField?.GetValue(cam) as InteractionObject : null;
            if (support == null)
            {
                Plugin.Log.LogWarning("Polaroid camera's support grip not found; the rifle is held one-handed.");
                return;
            }
            var grip = UnityEngine.Object.Instantiate(support.gameObject, clone.transform);
            grip.name = "WZ_ForendGrip";
            rifle.SupportGrip = grip.GetComponent<InteractionObject>();
            rifle.LeftHand = grip.GetComponentsInChildren<InteractionTarget>(true)
                .FirstOrDefault(t => t.effectorType == FullBodyBipedEffector.LeftHand)?.transform;
            if (rifle.LeftHand == null) Plugin.Log.LogWarning("Support grip has no left-hand target.");
        }

        private static HeldItemConfig _holdConfig;

        /// <summary>
        /// The pose other players' rifles are drawn with (the game's shared carry config). Each client fills it
        /// from its own player's hip pose, which matches everyone's since all players share one body rig.
        /// </summary>
        public static void SetSharedCarryPose(Vector3 position, Vector3 euler)
        {
            if (_holdConfig == null) return;
            _holdConfig.positionOnHand = position;
            _holdConfig.rotationOnHand = euler;
        }

        internal static bool TryParse(string text, out Vector3 v)
        {
            v = Vector3.zero;
            var parts = (text ?? "").Split(',');
            if (parts.Length != 3) return false;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, ci, out v.x)) return false;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, ci, out v.y)) return false;
            return float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float, ci, out v.z);
        }

        private static Vector3 LongestAxis(Vector3 size)
        {
            if (size.x >= size.y && size.x >= size.z) return Vector3.right;
            if (size.y >= size.z) return Vector3.up;
            return Vector3.forward;
        }

        /// <summary>Points any field on the prefab that referenced the old Shovel at the new RifleItem.</summary>
        private static void Retarget(GameObject root, Component from, Component to)
        {
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || mb == from || mb == to) continue;
                for (var t = mb.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                {
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (!typeof(Component).IsAssignableFrom(f.FieldType)) continue;
                        if (!ReferenceEquals(f.GetValue(mb), from)) continue;
                        if (f.FieldType.IsInstanceOfType(to)) f.SetValue(mb, to);
                    }
                }
            }
        }
    }
}
