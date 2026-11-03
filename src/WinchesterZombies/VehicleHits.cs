using System.Collections.Generic;
using Mirror;
using NomadDrive.Features.FloatingOrigin;
using NomadDrive.Features.Vehicle.Networking;
using NomadDrive.Features.Vehicle.WheelSystem;
using NWH.WheelController3D;
using UnityEngine;

namespace WinchesterZombies
{
    /// <summary>
    /// Vehicles against zombies, worked out on the machine that simulates each vehicle (its driver, or the host
    /// when nobody drives; everyone else sees a kinematic copy). Striking a zombie jolts the vehicle and is
    /// reported to the host, which decides kill or shove. Tyres rolling over a body bump the vehicle up.
    /// </summary>
    internal static class VehicleHits
    {
        private const float MphToMs = 0.44704f;
        private const float BodyMass = 75f;
        private const float BodyHalfThickness = 0.2f;

        private static readonly List<NetworkedNWHVehicle> Vehicles = new List<NetworkedNWHVehicle>();
        private static readonly Dictionary<NetworkedNWHVehicle, WheelController[]> Wheels = new Dictionary<NetworkedNWHVehicle, WheelController[]>();
        private static readonly Dictionary<ushort, float> HitUntil = new Dictionary<ushort, float>();
        private static readonly HashSet<long> OverBody = new HashSet<long>();   // (wheel, body) pairs a tyre is on now
        private static readonly Collider[] Overlaps = new Collider[24];
        private static float _nextScan;

        public static void Tick()
        {
            if (!NetworkClient.active || ZombieWorld.All.Count == 0) return;
            if (Time.time >= _nextScan)
            {
                _nextScan = Time.time + 1f;
                Vehicles.Clear();
                Vehicles.AddRange(Object.FindObjectsByType<NetworkedNWHVehicle>(FindObjectsSortMode.None));
                Wheels.Clear();
            }

            foreach (var v in Vehicles)
            {
                if (v == null || !v.IsPhysicsAuthority) continue;
                var rb = v.Rigidbody;
                if (rb == null || rb.isKinematic) continue;
                Vector3 centre = v.BoundsCenterWorld;
                float reach = v.BoundingRadius + 1.5f;
                foreach (var z in ZombieWorld.All.Values)
                {
                    if (z == null || (z.transform.position - centre).sqrMagnitude > reach * reach) continue;
                    if (z.Dead) CheckTyres(v, rb, z);
                    else CheckStrike(v, rb, z);
                }
            }
        }

        // ------------------------------------------------------------------ striking a standing zombie

        private static void CheckStrike(NetworkedNWHVehicle v, Rigidbody rb, ZombieAgent z)
        {
            if (HitUntil.TryGetValue(z.Id, out float until) && Time.time < until) return;
            Vector3 p = z.transform.position;
            int n = Physics.OverlapCapsuleNonAlloc(p + Vector3.up * 0.3f, p + Vector3.up * 1.5f, 0.45f, Overlaps, ~0, QueryTriggerInteraction.Ignore);
            Collider struck = null;
            for (int i = 0; i < n && struck == null; i++)
                if (Overlaps[i].attachedRigidbody == rb) struck = Overlaps[i];
            if (struck == null) return;

            Vector3 chest = p + Vector3.up;
            // ClosestPoint needs a convex shape; for concave meshes use the bounds (ClosestPointOnBounds is stripped).
            Vector3 contact = struck is MeshCollider mc && !mc.convex ? struck.bounds.ClosestPoint(chest) : struck.ClosestPoint(chest);
            Vector3 vel = rb.GetPointVelocity(contact);
            vel.y = 0f;
            float speed = vel.magnitude;
            if (speed < 0.5f) return;   // parked or creeping: the zombie just stands against it
            float mph = speed / MphToMs;
            bool kill = mph >= Plugin.VehicleKillMph.Value;
            HitUntil[z.Id] = Time.time + (kill ? 5f : 0.6f);

            Vector3 travel = vel / speed;
            Jolt(rb, contact, travel, speed, kill);
            Sounds.PlayAt(Sounds.Thud, contact, kill ? 1f : 0.5f);

            if (NetworkServer.active) z.ServerVehicleHit(rb, contact, travel, mph);
            else NetworkClient.Send(new CarHitMsg { Id = z.Id, NetId = v.netId, Point = FloatingOriginManager.ToTrueWorld(contact), Travel = travel, Mph = mph });
        }

        /// <summary>
        /// The body takes some of the vehicle's momentum at the point of contact: a thump that slows it,
        /// lifts that corner and twists it a little when the hit is off-centre.
        /// </summary>
        private static void Jolt(Rigidbody rb, Vector3 contact, Vector3 travel, float speed, bool kill)
        {
            float dv = speed * BodyMass / (rb.mass + BodyMass) * (kill ? 0.7f : 1f);
            dv = Mathf.Min(dv * Plugin.ImpactStrength.Value, 3f);
            Vector3 impulse = (-travel + Vector3.up * (kill ? 0.35f : 0.1f)) * (dv * rb.mass);
            rb.AddForceAtPosition(impulse, contact, ForceMode.Impulse);
        }

        // ------------------------------------------------------------------ tyres over a body

        private static void CheckTyres(NetworkedNWHVehicle v, Rigidbody rb, ZombieAgent z)
        {
            var a = z.Animator;
            if (a == null) return;
            Transform head = a.GetBoneTransform(HumanBodyBones.Head), hips = a.GetBoneTransform(HumanBodyBones.Hips);
            Transform lf = a.GetBoneTransform(HumanBodyBones.LeftFoot), rf = a.GetBoneTransform(HumanBodyBones.RightFoot);
            if (head == null || hips == null || lf == null || rf == null) return;
            // The body may still be falling when the wheels reach it, so its outline is taken flat on the ground.
            Vector3 h = head.position, c = hips.position, f = (lf.position + rf.position) * 0.5f, root = z.transform.position;

            foreach (var w in WheelsOf(v))
            {
                if (w == null || !w.isActiveAndEnabled || !w.IsGrounded) continue;
                Vector3 ground = w.HitPoint;
                float d = Mathf.Min(Mathf.Min(FlatDistance(ground, h, c), FlatDistance(ground, c, f)), FlatDistance(ground, root, c));
                bool over = d < BodyHalfThickness + w.Width * 0.5f && Mathf.Abs(ground.y - root.y) < 1f;
                long key = ((long)w.GetInstanceID() << 16) ^ z.Id;
                if (!over) { OverBody.Remove(key); continue; }
                if (!OverBody.Add(key)) continue;
                Bump(rb, w);
            }
        }

        /// <summary>The tyre rides up over the body: an upward kick at that wheel, more the faster it rolls on.</summary>
        private static void Bump(Rigidbody rb, WheelController w)
        {
            Vector3 vel = rb.GetPointVelocity(w.WheelPosition);
            vel.y = 0f;
            float speed = vel.magnitude;
            float lift = Mathf.Clamp(0.5f + speed * 0.06f, 0.5f, 1.6f) * Plugin.BodyBumpStrength.Value;
            float cornerMass = rb.mass * 0.25f;
            Vector3 impulse = rb.transform.up * (lift * cornerMass);
            if (speed > 0.1f) impulse -= vel / speed * (0.15f * cornerMass);   // a little drag as it climbs
            rb.AddForceAtPosition(impulse, w.WheelPosition, ForceMode.Impulse);
            Sounds.PlayAt(Sounds.Thud, w.HitPoint, 0.35f);

            var v = rb.GetComponent<NetworkedNWHVehicle>();
            if (NetworkServer.active) ServerWearTyre(rb.transform, w.WheelPosition);
            else if (v != null) NetworkClient.Send(new TyreHitMsg { NetId = v.netId, Point = FloatingOriginManager.ToTrueWorld(w.WheelPosition) });
        }

        /// <summary>Host: the tyre nearest the wheel that went over a body loses Vehicles.BodyTyreWear (a share of its condition).</summary>
        private static void ServerWearTyre(Transform vehicle, Vector3 wheelPos)
        {
            Tire nearest = null;
            float best = 1.5f * 1.5f;
            foreach (var tire in vehicle.GetComponentsInChildren<Tire>())
            {
                float d = (tire.transform.position - wheelPos).sqrMagnitude;
                if (d < best) { best = d; nearest = tire; }
            }
            var condition = nearest != null ? nearest.ConditionComponent : null;
            if (condition == null) return;
            ParseWear(out float lo, out float hi);
            float wear = condition.MaxCondition * Random.Range(lo, hi) / 100f;
            condition.ServerAddCondition(-wear);
            Plugin.Log.LogInfo($"Tyre {nearest.name} wore {wear:0.#} going over a body ({condition.Condition:0}/{condition.MaxCondition:0}).");
        }

        private static void ParseWear(out float lo, out float hi)
        {
            lo = 1f;
            hi = 2f;
            var parts = (Plugin.BodyTyreWear.Value ?? "").Split('-');
            if (parts.Length >= 1 && float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float a)) lo = hi = Mathf.Max(0f, a);
            if (parts.Length >= 2 && float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b)) hi = Mathf.Max(lo, b);
        }

        /// <summary>Host: a client driving a vehicle reports a tyre going over a body.</summary>
        public static void OnServerTyreHit(NetworkConnectionToClient conn, TyreHitMsg msg)
        {
            if (!Net.IsModded(conn)) return;
            if (!NetworkServer.spawned.TryGetValue(msg.NetId, out var identity) || identity == null) return;
            var v = identity.GetComponentInChildren<NetworkedNWHVehicle>();
            if (v == null) return;
            Vector3 point = FloatingOriginManager.ToRenderWorld(msg.Point);
            if ((v.transform.position - point).sqrMagnitude > 10f * 10f) return;   // not a wheel of this vehicle
            ServerWearTyre(v.transform, point);
        }

        private static WheelController[] WheelsOf(NetworkedNWHVehicle v)
        {
            if (!Wheels.TryGetValue(v, out var list))
                Wheels[v] = list = v.GetComponentsInChildren<WheelController>(true);
            return list;
        }

        /// <summary>Horizontal distance from a point to the segment a-b.</summary>
        private static float FlatDistance(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector2 P = new Vector2(p.x, p.z), A = new Vector2(a.x, a.z), B = new Vector2(b.x, b.z);
            Vector2 ab = B - A;
            float t = ab.sqrMagnitude > 0.0001f ? Mathf.Clamp01(Vector2.Dot(P - A, ab) / ab.sqrMagnitude) : 0f;
            return Vector2.Distance(P, A + ab * t);
        }

        // ------------------------------------------------------------------ host

        /// <summary>Host: a client driving a vehicle reports that it struck a zombie.</summary>
        public static void OnServerHit(NetworkConnectionToClient conn, CarHitMsg msg)
        {
            if (!Net.IsModded(conn)) return;
            if (!ZombieWorld.All.TryGetValue(msg.Id, out var z) || z == null || z.Dead || !z.IsServer) return;
            if (!NetworkServer.spawned.TryGetValue(msg.NetId, out var identity) || identity == null) return;
            var v = identity.GetComponentInChildren<NetworkedNWHVehicle>();
            if (v == null || v.Rigidbody == null) return;
            Vector3 point = FloatingOriginManager.ToRenderWorld(msg.Point);
            if ((z.transform.position - point).sqrMagnitude > 25f) return;   // the host's zombie is nowhere near that
            Vector3 travel = msg.Travel.sqrMagnitude > 0.0001f ? msg.Travel.normalized : Vector3.forward;
            z.ServerVehicleHit(v.Rigidbody, point, travel, Mathf.Clamp(msg.Mph, 0f, 150f));
        }

        public static void Clear()
        {
            Vehicles.Clear();
            Wheels.Clear();
            HitUntil.Clear();
            OverBody.Clear();
        }
    }
}
