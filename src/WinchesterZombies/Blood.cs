using System.Collections;
using System.Collections.Generic;
using Mirror;
using NomadDrive.Features.FloatingOrigin;
using PaintCore;
using PaintIn3D;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace WinchesterZombies
{
    public enum BloodKind : byte { Spray = 1, Ground, Vehicle }

    /// <summary>
    /// Blood: a spray burst, splatter decals on the ground, and splatter stuck to the vehicle part that hit a
    /// zombie. The host decides where; every client draws the same stains (HDRP decal projectors, the same
    /// way the game draws footprints). Stains fade out after a while and the oldest are recycled.
    /// </summary>
    internal static class Blood
    {
        private const int MaxGround = 60, MaxVehicle = 30;

        private static Material[] _decals;
        private static readonly Queue<GameObject> GroundStains = new Queue<GameObject>();
        private static readonly Queue<GameObject> VehicleStains = new Queue<GameObject>();

        // ------------------------------------------------------------------ host

        public static void ServerSpray(Vector3 renderPoint, Vector3 direction, float size) =>
            Send(new BloodMsg { Kind = (byte)BloodKind.Spray, Point = FloatingOriginManager.ToTrueWorld(renderPoint), Normal = direction, Size = size });

        public static void ServerGround(Vector3 renderPoint, float size) =>
            Send(new BloodMsg { Kind = (byte)BloodKind.Ground, Point = FloatingOriginManager.ToTrueWorld(renderPoint), Normal = Vector3.up, Size = size });

        public static void ServerGroundDelayed(Vector3 renderPoint, float size, float delay) =>
            Plugin.Instance.StartCoroutine(Later(delay, () => ServerGround(renderPoint, size)));

        /// <summary>Stain on a vehicle, stored in the vehicle's own space so it rides along.</summary>
        public static void ServerVehicle(Rigidbody vehicle, Vector3 renderPoint, Vector3 outward, float size)
        {
            var identity = vehicle.GetComponentInParent<NetworkIdentity>();
            if (identity == null) return;
            var t = identity.transform;
            Send(new BloodMsg
            {
                Kind = (byte)BloodKind.Vehicle,
                NetId = identity.netId,
                Point = t.InverseTransformPoint(renderPoint),
                Normal = t.InverseTransformDirection(outward),
                Size = size,
            });
        }

        private static readonly RaycastHit[] PanelHits = new RaycastHit[8];

        /// <summary>
        /// A body hitting a vehicle: several splats across the panels that met it, found by casting from the
        /// body back at the vehicle (the front face, and down onto the bonnet/roof edge), more the faster it was.
        /// </summary>
        public static void ServerVehicleSplatter(Rigidbody vehicle, Vector3 chest, Vector3 travel, float mph)
        {
            int want = Mathf.Clamp(3 + (int)(mph / 6f), 3, 10);
            float size = 0.7f + mph * 0.015f;
            Vector3 side = Vector3.Cross(Vector3.up, travel).normalized;
            int painted = 0;
            for (int i = 0; i < want * 3 && painted < want; i++)
            {
                Vector3 origin, dir;
                if (i % 3 != 2)
                {
                    // Into the face that struck the body, spread across its width and height.
                    origin = chest + travel * 1.5f + side * Random.Range(-0.9f, 0.9f) + Vector3.up * Random.Range(-0.6f, 0.6f);
                    dir = -travel;
                }
                else
                {
                    // Down onto the top edge just behind the impact (thrown up over the front).
                    origin = chest - travel * Random.Range(0f, 1.2f) + side * Random.Range(-0.8f, 0.8f) + Vector3.up * 2f;
                    dir = Vector3.down;
                }
                if (!PanelHit(vehicle, origin, dir, 4f, out var hit)) continue;
                ServerVehicle(vehicle, hit.point, hit.normal, size * Random.Range(0.6f, 1.2f));
                painted++;
            }
        }

        private static bool PanelHit(Rigidbody vehicle, Vector3 origin, Vector3 dir, float length, out RaycastHit best)
        {
            best = default;
            int n = Physics.RaycastNonAlloc(origin, dir, PanelHits, length, ~0, QueryTriggerInteraction.Ignore);
            float nearest = 1e30f;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                if (PanelHits[i].rigidbody != vehicle || PanelHits[i].distance >= nearest) continue;
                nearest = PanelHits[i].distance;
                best = PanelHits[i];
                found = true;
            }
            return found;
        }

        private static IEnumerator Later(float delay, System.Action action)
        {
            yield return new WaitForSeconds(delay);
            action();
        }

        private static void Send(BloodMsg msg)
        {
            if (NetworkClient.active) OnMsg(msg);   // host's own view
            Net.SendToModded(msg);
        }

        // ------------------------------------------------------------------ every client

        public static void OnMsg(BloodMsg msg)
        {
            if (!Plugin.ShowBlood.Value) return;
            switch ((BloodKind)msg.Kind)
            {
                case BloodKind.Spray:
                    Spray(FloatingOriginManager.ToRenderWorld(msg.Point), msg.Normal, msg.Size);
                    break;
                case BloodKind.Ground:
                    var pos = FloatingOriginManager.ToRenderWorld(msg.Point);
                    var root = FloatingOriginManager.Instance != null ? FloatingOriginManager.Instance.WorldContentRoot : null;
                    Keep(GroundStains, MaxGround, Stain(root, pos, Vector3.up, msg.Size, Plugin.BloodGroundSeconds.Value));
                    break;
                case BloodKind.Vehicle:
                    if (!NetworkClient.spawned.TryGetValue(msg.NetId, out var identity) || identity == null) return;
                    var t = identity.transform;
                    Keep(VehicleStains, MaxVehicle, VehicleDecal(t, t.TransformPoint(msg.Point), t.TransformDirection(msg.Normal), msg.Size, Plugin.BloodVehicleSeconds.Value));
                    break;
            }
        }

        /// <summary>Debug: stain whatever the camera is looking at (vehicle decal or ground splatter), locally.</summary>
        public static void DebugAtCrosshair()
        {
            var cam = Camera.main;
            if (cam == null || !Physics.Raycast(cam.transform.position, cam.transform.forward, out var hit, 15f, ~0, QueryTriggerInteraction.Ignore)) return;
            var identity = hit.rigidbody != null ? hit.rigidbody.GetComponentInParent<NetworkIdentity>() : null;
            if (identity != null)
                Keep(VehicleStains, MaxVehicle, VehicleDecal(identity.transform, hit.point, hit.normal, 0.8f, Plugin.BloodVehicleSeconds.Value));
            else
                Keep(GroundStains, MaxGround, Stain(null, hit.point, hit.normal, 1f, Plugin.BloodGroundSeconds.Value));
            Plugin.Log.LogInfo($"Debug blood on {hit.collider.name} ({(identity != null ? "vehicle decal" : "ground")}).");
        }

        // Restoration paint groups on the game's vehicle panels (RestorableVehicleSurface / PaintSprayTool).
        private const int PaintColorGroup = 200, PaintMaskGroup = 204;
        private static CwPaintDecal _colorBrush, _maskBrush;

        /// <summary>
        /// Blood on a vehicle, painted into the panel the way the game's spray can paints: the splatter goes into
        /// the paint-colour layer and its shape into the paint mask, so it follows the bodywork exactly and stays
        /// put. Every client paints the same hit locally (as the spray can's relay does). Being real paint, it
        /// stays until the panel is repainted, and is saved with the vehicle.
        /// </summary>
        private static GameObject VehicleDecal(Transform vehicle, Vector3 pos, Vector3 outward, float size, float lifetime)
        {
            if (Assets.BloodTextures.Count == 0) return null;
            EnsureBrushes();
            outward = outward.sqrMagnitude > 0.0001f ? outward.normalized : Vector3.up;
            var tex = Assets.BloodTextures[Random.Range(0, Assets.BloodTextures.Count)];
            float angle = Random.Range(0f, 360f);
            float radius = 0.5f * size * Random.Range(0.85f, 1.15f);
            // The brush projects along the rotation's forward, from just outside the panel into it.
            var rot = Quaternion.LookRotation(-outward);
            Vector3 from = pos + outward * 0.05f;
            int seed = Random.Range(0, 1 << 30);   // int.MaxValue is stripped from the game's build

            foreach (var brush in new[] { _colorBrush, _maskBrush })
            {
                brush.Shape = tex;
                brush.Angle = angle;
                brush.Radius = radius;
                brush.HandleHitPoint(false, 0, 1f, seed, from, rot);
            }
            return null;   // nothing to keep or fade: the blood lives in the paint
        }

        private static void EnsureBrushes()
        {
            if (_colorBrush != null) return;
            var root = new GameObject("WZ_BloodPainter");
            Object.DontDestroyOnLoad(root);

            _colorBrush = new GameObject("Color").AddComponent<CwPaintDecal>();
            _colorBrush.transform.SetParent(root.transform, false);
            _colorBrush.Group = new CwGroup(PaintColorGroup);
            _colorBrush.BlendMode = CwBlendMode.AlphaBlend(new Vector4(1f, 1f, 1f, 1f));
            // Flat blood red through the splatter shape. (Tinting with the dark texture on top came out near-black,
            // which the glossy restoration paint then shows as sky reflection.)
            _colorBrush.Texture = Texture2D.whiteTexture;   // the brush needs a texture to paint colour at all
            _colorBrush.ShapeChannel = CwChannel.Alpha;
            // The restoration paint shader adds a glossy clear-coat that lifts dark colours toward grey, so this is a
            // pure, fairly bright red that reads as fresh blood on the panel.
            _colorBrush.Color = ColorUtility.TryParseHtmlString(Plugin.VehicleBloodColor.Value, out var c) ? c : new Color(0.78f, 0.01f, 0.01f);
            _colorBrush.Opacity = 1f;
            _colorBrush.Hardness = 1f;
            _colorBrush.Scale = Vector3.one;

            _maskBrush = new GameObject("Mask").AddComponent<CwPaintDecal>();
            _maskBrush.transform.SetParent(root.transform, false);
            _maskBrush.Group = new CwGroup(PaintMaskGroup);
            _maskBrush.BlendMode = CwBlendMode.Additive(new Vector4(1f, 0f, 0f, 0f));
            _maskBrush.ShapeChannel = CwChannel.Alpha;
            _maskBrush.Color = Color.white;
            _maskBrush.Opacity = 1f;
            _maskBrush.Hardness = 1f;
            _maskBrush.Scale = Vector3.one;
        }

        private static void Spray(Vector3 pos, Vector3 dir, float size)
        {
            if (Assets.BloodSpray == null) return;
            var go = Object.Instantiate(Assets.BloodSpray, pos, Quaternion.LookRotation(dir.sqrMagnitude > 0.001f ? dir : Vector3.up));
            go.transform.localScale = Vector3.one * Mathf.Max(0.5f, size);
            Assets.ApplyHdrp(go);
            Object.Destroy(go, 3f);
        }

        /// <summary>
        /// A thin splatter laid on the surface, facing <paramref name="outward"/>, randomly turned. (This game's
        /// desert terrain doesn't accept HDRP decals, so it's a cut-out quad rather than a projector.)
        /// </summary>
        private static GameObject Stain(Transform parent, Vector3 pos, Vector3 outward, float size, float lifetime)
        {
            var mat = StainMaterial();
            if (mat == null) return null;
            Plugin.Log.LogDebug($"Blood stain at {pos} on {(parent != null ? parent.name : "the ground")}, size {size:0.0}.");
            outward = outward.sqrMagnitude > 0.0001f ? outward.normalized : Vector3.up;
            if (parent == null || parent.GetComponentInParent<Rigidbody>() == null) SnapToGround(ref pos, ref outward);

            var go = new GameObject("WZ_Blood");
            if (parent != null) go.transform.SetParent(parent, true);
            go.transform.SetPositionAndRotation(pos + outward * 0.025f,
                Quaternion.LookRotation(-outward) * Quaternion.Euler(0f, 0f, Random.Range(0f, 360f)));
            go.transform.localScale = Vector3.one * size * Random.Range(0.85f, 1.15f) / (go.transform.parent != null ? go.transform.parent.lossyScale.x : 1f);
            go.AddComponent<MeshFilter>().sharedMesh = Quad();
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.AddComponent<BloodFade>().Lifetime = lifetime;
            return go;
        }

        private static readonly RaycastHit[] GroundHits = new RaycastHit[8];

        /// <summary>Settles a ground stain onto the actual surface (and its slope) under the point.</summary>
        private static void SnapToGround(ref Vector3 pos, ref Vector3 normal)
        {
            int n = Physics.RaycastNonAlloc(pos + Vector3.up * 1.5f, Vector3.down, GroundHits, 4f, ~0, QueryTriggerInteraction.Ignore);
            float best = 1e30f;
            for (int i = 0; i < n; i++)
            {
                var h = GroundHits[i];
                if (h.rigidbody != null || h.collider.GetComponentInParent<ZombieAgent>() != null) continue;
                if (h.collider.GetComponentInParent<NomadDrive.Features.Player.Player>() != null) continue;
                if (h.distance < best) { best = h.distance; pos = h.point; normal = h.normal; }
            }
        }

        private static Mesh _quad;

        /// <summary>Unit quad facing -Z (so a stain faces away from the surface it's looking into).</summary>
        private static Mesh Quad()
        {
            if (_quad != null) return _quad;
            _quad = new Mesh { name = "WZ Blood Quad" };
            _quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
            _quad.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            _quad.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            _quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            _quad.RecalculateBounds();
            _quad.RecalculateTangents();
            return _quad;
        }

        private static void Keep(Queue<GameObject> queue, int max, GameObject stain)
        {
            if (stain == null) return;
            queue.Enqueue(stain);
            while (queue.Count > max)
            {
                var old = queue.Dequeue();
                if (old != null) Object.Destroy(old);
            }
        }

        /// <summary>HDRP/Lit, alpha cut-out to the splatter shape, glossy like wet blood.</summary>
        private static Material StainMaterial()
        {
            if (_decals == null)
            {
                var lit = Shader.Find("HDRP/Lit");
                if (lit == null || Assets.BloodTextures.Count == 0) return null;
                _decals = new Material[Assets.BloodTextures.Count];
                for (int i = 0; i < _decals.Length; i++)
                {
                    var m = new Material(lit) { name = "WZ Blood " + i };
                    m.SetTexture("_BaseColorMap", Assets.BloodTextures[i]);
                    m.SetColor("_BaseColor", Color.white);
                    m.SetFloat("_AlphaCutoffEnable", 1f);
                    m.SetFloat("_AlphaCutoff", 0.5f);
                    m.SetFloat("_Metallic", 0f);
                    m.SetFloat("_Smoothness", 0.82f);
                    HDMaterial.ValidateMaterial(m);
                    _decals[i] = m;
                }
            }
            return _decals[Random.Range(0, _decals.Length)];
        }

        public static void Clear()
        {
            foreach (var q in new[] { GroundStains, VehicleStains })
            {
                while (q.Count > 0) { var g = q.Dequeue(); if (g != null) Object.Destroy(g); }
            }
        }
    }

    /// <summary>Holds a stain for its lifetime, then fades it out over a few seconds.</summary>
    public class BloodFade : MonoBehaviour
    {
        public float Lifetime = 300f;
        private const float FadeTime = 8f;
        private static readonly int CutoffId = Shader.PropertyToID("_AlphaCutoff");
        private Renderer _renderer;
        private DecalProjector _projector;
        private MaterialPropertyBlock _block;
        private float _age;

        private void Awake()
        {
            _renderer = GetComponent<Renderer>();
            _projector = GetComponent<DecalProjector>();
        }

        private void Update()
        {
            _age += Time.deltaTime;
            float left = Lifetime - _age;
            if (left <= 0f) { Destroy(gameObject); return; }
            if (left < FadeTime && _projector != null) _projector.fadeFactor = left / FadeTime;
            else if (left < FadeTime && _renderer != null)
            {
                // Raising the cut-out threshold eats the thin edges first, like the stain drying away.
                _block ??= new MaterialPropertyBlock();
                _block.SetFloat(CutoffId, Mathf.Lerp(0.5f, 1f, 1f - left / FadeTime));
                _renderer.SetPropertyBlock(_block);
            }
        }
    }
}
