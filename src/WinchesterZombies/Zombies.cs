using System.Collections.Generic;
using System.Linq;
using Mirror;
using NomadDrive.Features.Attachables;
using NomadDrive.Features.EvilRoads;
using NomadDrive.Features.FloatingOrigin;
using NomadDrive.Features.Player;
using NomadDrive.Features.WorldGeneration.ObjectSpawning;
using NomadDrive.Features.WorldGeneration.POISpawning;
using UnityEngine;

namespace WinchesterZombies
{
    public enum ZombieEvent : byte { Attack = 1, Hit, Die }

    /// <summary>
    /// One zombie. On the host it runs the AI (chase the nearest standing player, claw on the attack clip's
    /// impact frame, die to rifle hits or a fast vehicle). On other clients it follows host snapshots.
    /// </summary>
    public class ZombieAgent : MonoBehaviour
    {
        private static readonly int SpeedId = Animator.StringToHash("Speed");
        private static readonly int WalkAnimSpeedId = Animator.StringToHash("WalkAnimSpeed");
        private static readonly int AttackId = Animator.StringToHash("Attack");
        private static readonly int HitId = Animator.StringToHash("Hit");
        private static readonly int DieId = Animator.StringToHash("Die");
        private static readonly int IsDeadId = Animator.StringToHash("IsDead");

        private const float DetectRange = 45f, LoseRange = 70f, AttackRange = 1.4f, AttackReach = 1.9f;
        private const float AttackCooldown = 1.6f, StaggerCooldown = 0.6f, TurnSpeed = 200f;

        public ushort Id;
        public bool IsServer;
        public bool Dead { get; private set; }
        public float Health;
        public Animator Animator;

        // Host AI
        private Player _target;
        private float _nextAttack, _nextStagger, _busyUntil;
        private Vector3 _lastPos, _knock;
        private float _speed;
        private float _nextWakeCheck;

        /// <summary>Host: near enough to a player to think and animate. Far-off zombies sleep until someone comes close.</summary>
        public bool IsAwake { get; private set; } = true;

        // Remote clients
        private Vector3 _netPos;
        private float _netYaw, _netSpeed;
        internal float LastSeen;

        internal void Init(ushort id, bool server)
        {
            Id = id;
            IsServer = server;
            Health = Plugin.ZombieHealth.Value;
            Animator = GetComponentInChildren<Animator>();
            // The host needs the attack clip's event even when nobody is looking.
            Animator.cullingMode = server ? AnimatorCullingMode.AlwaysAnimate : AnimatorCullingMode.CullUpdateTransforms;
            Animator.gameObject.AddComponent<ZombieAnimEvents>().Agent = this;
            _lastPos = transform.position;
            _netPos = transform.position;
            _netYaw = transform.eulerAngles.y;
            LastSeen = Time.time;
        }

        private void Update()
        {
            if (Dead) return;
            if (IsServer) ServerUpdate();
            else ClientUpdate();
        }

        // ------------------------------------------------------------------ host

        private void ServerUpdate()
        {
            float dt = Time.deltaTime;
            if (Time.time >= _nextWakeCheck)
            {
                _nextWakeCheck = Time.time + 0.5f + Random.value * 0.1f;   // spread out so they don't all check on one frame
                SetAwake(ZombieDirector.NearestAnyPlayer(transform.position) < Plugin.WakeRange.Value);
            }
            if (!IsAwake) return;

            if (_knock.sqrMagnitude > 0.01f)
            {
                // Shoved by a slow vehicle: slides out of its way, slowing quickly.
                Vector3 to = transform.position + _knock * dt;
                if (ZombieDirector.GroundY(to, out float gy)) to.y = gy;
                transform.position = to;
                _knock *= Mathf.Exp(-3f * dt);
            }
            AcquireTarget();
            bool busy = Time.time < _busyUntil;

            if (_target != null && !busy)
            {
                Vector3 to = _target.transform.position - transform.position;
                to.y = 0f;
                float dist = to.magnitude;
                Face(dist > AttackRange ? Steer(to / Mathf.Max(dist, 0.001f)) : to, dt);
                if (dist > AttackRange)
                {
                    Vector3 next = transform.position + transform.forward * (Plugin.ZombieSpeed.Value * dt);
                    // A big step up is a wall or a ledge (or a roof seen from below), not ground to walk onto.
                    bool wall = false;
                    if (ZombieDirector.GroundY(next, out float y)) { wall = y > transform.position.y + 0.6f; next.y = y; }
                    if (!wall && !WalksIntoVehicle(next)) transform.position = next;
                }
                else if (Time.time >= _nextAttack && Vector3.Angle(transform.forward, to) < 30f)
                {
                    _nextAttack = Time.time + AttackCooldown;
                    _busyUntil = Time.time + 1.1f;
                    ZombieDirector.Broadcast(Id, ZombieEvent.Attack);
                }
            }

            Vector3 delta = transform.position - _lastPos;
            delta.y = 0f;
            _lastPos = transform.position;
            _speed = Mathf.Lerp(_speed, dt > 0f ? delta.magnitude / dt : 0f, 1f - Mathf.Exp(-8f * dt));
            SetMoveAnim(_speed);
        }

        // Obstacle avoidance: feel ahead toward the target; if that's blocked, take the nearest open bearing either
        // side and keep it for a moment, so a zombie walks round a rock or a building corner instead of into it.
        private Vector3 _steer;
        private float _nextSteer, _detourUntil;
        private int _detourSide = 1;
        private static readonly RaycastHit[] FeelHits = new RaycastHit[8];
        private static readonly float[] DetourAngles = { 35f, 70f, 105f, 140f };

        private Vector3 Steer(Vector3 desired)
        {
            if (Time.time < _nextSteer && _steer.sqrMagnitude > 0f) return _steer;
            _nextSteer = Time.time + 0.2f;

            if (Time.time < _detourUntil && Clear(_steer) && Vector3.Angle(_steer, desired) < 120f) return _steer;
            if (Clear(desired)) return _steer = desired;
            foreach (float a in DetourAngles)
            {
                for (int s = 0; s < 2; s++)
                {
                    int side = s == 0 ? _detourSide : -_detourSide;
                    Vector3 d = Quaternion.Euler(0f, a * side, 0f) * desired;
                    if (!Clear(d)) continue;
                    _detourSide = side;
                    _detourUntil = Time.time + 1f;
                    return _steer = d;
                }
            }
            return _steer = desired;   // boxed in: keep pushing toward the target
        }

        /// <summary>Nothing solid within a metre and a bit along <paramref name="dir"/> at body height, and no wall-high step.</summary>
        private bool Clear(Vector3 dir)
        {
            Vector3 p = transform.position;
            int n = Physics.SphereCastNonAlloc(p + Vector3.up * 0.9f, 0.3f, dir, FeelHits, 1.3f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var h = FeelHits[i];
                if (h.distance <= 0f) continue;   // already overlapping at the start: don't let it freeze the zombie
                var rb = h.rigidbody;
                if (rb != null && !rb.isKinematic && rb.mass < 50f) continue;   // loose props get walked through
                if (h.collider.GetComponentInParent<Player>() != null || h.collider.GetComponentInParent<ZombieAgent>() != null) continue;
                return false;
            }
            return !ZombieDirector.GroundY(p + dir * 1f, out float y) || y <= p.y + 0.6f;
        }

        /// <summary>Host: nudge apart from a neighbour that's too close (called by the director).</summary>
        internal void Separate(Vector3 push)
        {
            Vector3 to = transform.position + push;
            if (ZombieDirector.GroundY(to, out float y))
            {
                if (y > transform.position.y + 0.6f) return;
                to.y = y;
            }
            transform.position = to;
        }

        private void SetAwake(bool awake)
        {
            if (awake == IsAwake) return;
            IsAwake = awake;
            // Asleep, the Animator stops entirely; awake, it runs even off-screen for the attack clip's event.
            Animator.cullingMode = awake ? AnimatorCullingMode.AlwaysAnimate : AnimatorCullingMode.CullCompletely;
            if (awake) return;
            _target = null;
            _speed = 0f;
            _knock = Vector3.zero;
            SetMoveAnim(0f);
        }

        private void AcquireTarget()
        {
            if (_target != null)
            {
                bool lost = _target.IsDowned || (_target.transform.position - transform.position).sqrMagnitude > LoseRange * LoseRange;
                if (!lost) return;
                _target = null;
            }
            float best = DetectRange * DetectRange;
            foreach (var p in ZombieDirector.Players)
            {
                if (p == null || p.IsDowned) continue;
                float d = (p.transform.position - transform.position).sqrMagnitude;
                if (d < best) { best = d; _target = p; }
            }
        }

        private void Face(Vector3 dir, float dt)
        {
            if (dir.sqrMagnitude < 0.0001f) return;
            var want = Quaternion.LookRotation(dir.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, want, TurnSpeed * dt);
        }

        /// <summary>Attack clip impact frame (host): the claw connects if the target is still close and in front.</summary>
        internal void ServerAttackHit()
        {
            if (!IsServer || Dead || _target == null || _target.IsDowned) return;
            Vector3 to = _target.transform.position - transform.position;
            to.y = 0f;
            if (to.magnitude > AttackReach || Vector3.Angle(transform.forward, to) > 70f) return;
            Plugin.Log.LogInfo($"Zombie {Id} clawed a player for {Plugin.ZombieDamage.Value}.");
            ZombieDirector.HitPlayer(_target, Plugin.ZombieDamage.Value);
        }

        internal void ServerTakeDamage(float amount, bool headshot = false)
        {
            if (Dead) return;
            SetAwake(true);
            if (headshot && Plugin.HeadshotKills.Value)
            {
                Plugin.Log.LogInfo($"Zombie {Id} killed by a headshot.");
                ServerDie();
                return;
            }
            Health -= amount;
            if (Health <= 0f)
            {
                Plugin.Log.LogInfo($"Zombie {Id} killed.");
                ServerDie();
                return;
            }
            Plugin.Log.LogInfo($"Zombie {Id} hit for {amount}, {Health} HP left.");
            if (Time.time >= _nextStagger)
            {
                _nextStagger = Time.time + StaggerCooldown;
                _busyUntil = Time.time + 0.5f;
                ZombieDirector.Broadcast(Id, ZombieEvent.Hit);
            }
        }

        /// <summary>Death from rifle fire: the body falls forward, leaving a pool where the torso lands.</summary>
        private void ServerDie()
        {
            ZombieDirector.Broadcast(Id, ZombieEvent.Die);
            Blood.ServerGroundDelayed(transform.position + transform.forward * 1.0f, 1.5f, 1.0f);
        }

        private static readonly Collider[] Overlaps = new Collider[24];
        private const float MphToMs = 0.44704f;

        /// <summary>
        /// Host: a vehicle struck this zombie (reported by whoever simulates the vehicle, see VehicleHits).
        /// At the kill speed or faster it dies and the part that struck it (bumper, tyre, ...) loses condition;
        /// slower, it is shoved out of the way and staggered.
        /// </summary>
        internal void ServerVehicleHit(Rigidbody vehicle, Vector3 contact, Vector3 travel, float mph)
        {
            if (Dead) return;
            Vector3 p = transform.position;
            if (mph >= Plugin.VehicleKillMph.Value)
            {
                Plugin.Log.LogInfo($"Zombie {Id} hit by a vehicle at {mph:0} mph.");
                ZombieDirector.DamageVehiclePart(vehicle, null, contact, mph);
                ZombieDirector.Broadcast(Id, ZombieEvent.Die);

                // Splatter where the body met the vehicle, a spray carried forward, and a pool where it lands.
                Vector3 chest = p + Vector3.up;
                Vector3 outward = chest - contact;
                if (outward.sqrMagnitude < 0.0001f) outward = -travel;
                Blood.ServerVehicle(vehicle, contact, outward.normalized, 0.8f + mph * 0.015f);
                Blood.ServerVehicleSplatter(vehicle, chest, travel, mph);
                Blood.ServerSpray(chest, travel + Vector3.up * 0.3f, 1.3f);
                Blood.ServerGroundDelayed(p + travel * 1.5f, 1.6f, 0.6f);
                return;
            }

            Plugin.Log.LogInfo($"Zombie {Id} shoved by a vehicle at {mph:0} mph.");
            ServerTakeDamage(Plugin.ZombieHealth.Value * 0.3f * mph / Mathf.Max(1f, Plugin.VehicleKillMph.Value));
            if (Dead) return;
            // Thrown ahead and off to whichever side of the vehicle it was on, clear of its path.
            Vector3 side = p - vehicle.worldCenterOfMass;
            side -= Vector3.Dot(side, travel) * travel;
            side.y = 0f;
            side = side.sqrMagnitude > 0.0001f ? side.normalized : Vector3.Cross(Vector3.up, travel);
            _knock = travel * Mathf.Max(2f, mph * MphToMs * 1.8f) + side * 3f;
            _busyUntil = Time.time + 0.7f;
        }

        /// <summary>Host: true if a step to <paramref name="next"/> would walk into a vehicle (one it isn't already inside).</summary>
        private bool WalksIntoVehicle(Vector3 next) => TouchesVehicle(next) && !TouchesVehicle(transform.position);

        private static bool TouchesVehicle(Vector3 p)
        {
            int n = Physics.OverlapCapsuleNonAlloc(p + Vector3.up * 0.4f, p + Vector3.up * 1.4f, 0.3f, Overlaps, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var rb = Overlaps[i].attachedRigidbody;
                if (rb != null && rb.mass >= 200f && rb.GetComponentInParent<ZombieAgent>() == null) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ remote clients

        internal void ApplySnapshot(ZombieNetState s)
        {
            _netPos = FloatingOriginManager.ToRenderWorld(s.Position);
            _netYaw = s.Yaw;
            _netSpeed = s.Speed;
            LastSeen = Time.time;
        }

        private void ClientUpdate()
        {
            float k = 1f - Mathf.Exp(-10f * Time.deltaTime);
            if ((transform.position - _netPos).sqrMagnitude > 25f) transform.position = _netPos;   // far off: snap
            else transform.position = Vector3.Lerp(transform.position, _netPos, k);
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, _netYaw, 0f), k);
            SetMoveAnim(_netSpeed);
        }

        internal ZombieNetState NetState() => new ZombieNetState
        {
            Id = Id,
            Position = FloatingOriginManager.ToTrueWorld(transform.position),
            Yaw = transform.eulerAngles.y,
            Speed = _speed,
        };

        // ------------------------------------------------------------------ shared

        private void SetMoveAnim(float speed)
        {
            Animator.SetFloat(SpeedId, speed);
            Animator.SetFloat(WalkAnimSpeedId, Mathf.Clamp(speed / 0.6f, 0.6f, 2.5f));
        }

        internal void PlayEvent(ZombieEvent e)
        {
            switch (e)
            {
                case ZombieEvent.Attack:
                    Animator.SetTrigger(AttackId);
                    break;
                case ZombieEvent.Hit:
                    Animator.ResetTrigger(AttackId);
                    Animator.SetTrigger(HitId);
                    break;
                case ZombieEvent.Die:
                    Dead = true;
                    Animator.ResetTrigger(AttackId);
                    Animator.ResetTrigger(HitId);
                    Animator.SetBool(IsDeadId, true);
                    Animator.SetTrigger(DieId);
                    Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;
                    Destroy(gameObject, 25f);
                    break;
            }
        }

        private void OnDestroy() => ZombieWorld.Forget(this);
    }

    /// <summary>Marks a zombie's head hitbox.</summary>
    public class ZombieHead : MonoBehaviour { }

    /// <summary>Receives the zombie clips' animation events on the Animator's object.</summary>
    public class ZombieAnimEvents : MonoBehaviour
    {
        public ZombieAgent Agent;
        private void OnAttackHit() { if (Agent != null) Agent.ServerAttackHit(); }
    }

    /// <summary>Every zombie this machine shows, keyed by id (the host's AI zombies included).</summary>
    internal static class ZombieWorld
    {
        public static readonly Dictionary<ushort, ZombieAgent> All = new Dictionary<ushort, ZombieAgent>();

        public static ZombieAgent Create(ushort id, Vector3 renderPos, float yaw, bool server)
        {
            var go = Object.Instantiate(Assets.Zombie, renderPos, Quaternion.Euler(0f, yaw, 0f));
            go.name = "WZ_Zombie_" + id;
            Assets.ApplyHdrp(go);
            var root = FloatingOriginManager.Instance != null ? FloatingOriginManager.Instance.WorldContentRoot : null;
            if (root != null) go.transform.SetParent(root, true);

            var capsule = go.AddComponent<CapsuleCollider>();
            capsule.isTrigger = true;   // doesn't stop vehicles dead; shots test it explicitly
            capsule.center = new Vector3(0f, 0.9f, 0f);
            capsule.height = 1.8f;
            capsule.radius = 0.35f;
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;

            var agent = go.AddComponent<ZombieAgent>();
            agent.Init(id, server);

            // Head hitbox on the skeleton's head bone, so it follows the shamble and the lunge.
            var head = agent.Animator.GetBoneTransform(HumanBodyBones.Head);
            if (head != null)
            {
                var hb = new GameObject("HeadHitbox");
                hb.transform.SetParent(head, false);
                hb.transform.localPosition = new Vector3(0f, 0.08f, 0f);
                var sphere = hb.AddComponent<SphereCollider>();
                sphere.isTrigger = true;
                sphere.radius = 0.13f;
                hb.AddComponent<ZombieHead>();
            }
            All[id] = agent;
            return agent;
        }

        public static void OnSnapshot(ZombieSnapshotMsg msg)
        {
            if (NetworkServer.active || msg.Zombies == null) return;   // the host runs the real ones
            foreach (var s in msg.Zombies)
            {
                if (!All.TryGetValue(s.Id, out var z) || z == null)
                    z = Create(s.Id, FloatingOriginManager.ToRenderWorld(s.Position), s.Yaw, server: false);
                if (!z.Dead) z.ApplySnapshot(s);
            }
            // Living zombies the host stopped sending were despawned.
            foreach (var z in All.Values.ToList())
                if (z != null && !z.Dead && Time.time - z.LastSeen > 1.5f) Object.Destroy(z.gameObject);
        }

        public static void OnEvent(ZombieEventMsg msg)
        {
            if (All.TryGetValue(msg.Id, out var z) && z != null) z.PlayEvent((ZombieEvent)msg.Kind);
        }

        public static void Forget(ZombieAgent z)
        {
            if (All.TryGetValue(z.Id, out var cur) && cur == z) All.Remove(z.Id);
        }

        public static void Clear()
        {
            foreach (var z in All.Values.ToList()) if (z != null) Object.Destroy(z.gameObject);
            All.Clear();
        }
    }

    /// <summary>Host only: spawning, despawning, snapshots and damage routing.</summary>
    internal static class ZombieDirector
    {
        public static bool SpawningPaused;
        public static readonly List<Player> Players = new List<Player>();

        private static ushort _nextId = 1;
        private static float _nextSnapshot, _nextSpawn, _nextPlayerScan;
        private static readonly RaycastHit[] Hits = new RaycastHit[16];

        public static void Tick()
        {
            if (!NetworkServer.active || Assets.Zombie == null) return;

            if (Time.time >= _nextPlayerScan)
            {
                _nextPlayerScan = Time.time + 0.5f;
                Players.Clear();
                Players.AddRange(Object.FindObjectsByType<Player>(FindObjectsSortMode.None));
            }

            if (Time.time >= _nextSpawn)
            {
                _nextSpawn = Time.time + 2f;
                if (Plugin.ZombiesEnabled.Value && !SpawningPaused)
                {
                    PopulateLootSites();
                    PopulateRoads();
                }
                DespawnFar();
            }
            SpawnQueued();
            SeparateCrowd();

            if (Time.time >= _nextSnapshot)
            {
                // Sleeping zombies don't move, so they go out every fifth snapshot (often enough that clients keep them).
                _nextSnapshot = Time.time + 0.1f;
                bool all = ++_snapshotCount % 5 == 0;
                var alive = ZombieWorld.All.Values.Where(z => z != null && !z.Dead && z.IsServer && (all || z.IsAwake)).Select(z => z.NetState()).ToArray();
                Net.SendToModded(new ZombieSnapshotMsg { Zombies = alive });
            }
        }

        private static int _snapshotCount;
        private static readonly List<ZombieAgent> Crowd = new List<ZombieAgent>();
        private const float PersonalSpace = 0.75f;

        /// <summary>
        /// Zombies are triggers (so cars don't stop dead), which means nothing keeps them apart: each frame,
        /// awake ones closer than arm's length are eased apart, so a crowd fans out instead of merging into one.
        /// </summary>
        private static void SeparateCrowd()
        {
            Crowd.Clear();
            CrowdPos.Clear();
            Pushes.Clear();
            foreach (var z in ZombieWorld.All.Values)
            {
                if (z == null || !z.IsServer || !z.IsAwake || z.Dead) continue;
                Crowd.Add(z);
                CrowdPos.Add(z.transform.position);
                Pushes.Add(Vector3.zero);
            }
            float rate = Mathf.Min(1f, 8f * Time.deltaTime);
            for (int i = 0; i < Crowd.Count; i++)
            {
                for (int j = i + 1; j < Crowd.Count; j++)
                {
                    Vector3 d = CrowdPos[i] - CrowdPos[j];
                    d.y = 0f;
                    float sq = d.sqrMagnitude;
                    if (sq >= PersonalSpace * PersonalSpace) continue;
                    float dist = Mathf.Sqrt(sq);
                    Vector3 dir = dist > 0.001f ? d / dist : Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward;
                    Vector3 push = dir * ((PersonalSpace - dist) * 0.5f * rate);
                    Pushes[i] += push;
                    Pushes[j] -= push;
                }
            }
            for (int i = 0; i < Crowd.Count; i++)
                if (Pushes[i].sqrMagnitude > 1e-6f) Crowd[i].Separate(Vector3.ClampMagnitude(Pushes[i], 0.2f));
        }

        private static readonly List<Vector3> CrowdPos = new List<Vector3>(), Pushes = new List<Vector3>();

        /// <summary>Distance from a point to the nearest player, standing or downed.</summary>
        public static float NearestAnyPlayer(Vector3 pos)
        {
            float best = 1e30f;
            foreach (var p in Players) if (p != null) best = Mathf.Min(best, (p.transform.position - pos).sqrMagnitude);
            return Mathf.Sqrt(best);
        }

        private static int AliveCount => ZombieWorld.All.Values.Count(z => z != null && !z.Dead);

        // ------------------------------------------------------------------ populating (once per place)

        // Places already populated this session, keyed by a coarse true-world grid cell. They never refill.
        private static readonly HashSet<long> Visited = new HashSet<long>();
        private static float _nextSiteScan;

        private static long CellKey(Vector3 renderPos, float cell, int kind)
        {
            Vector3 t = FloatingOriginManager.ToTrueWorld(renderPos);
            long x = (long)Mathf.Floor(t.x / cell), z = (long)Mathf.Floor(t.z / cell);
            return (x * 73856093L) ^ (z * 19349663L) ^ kind;
        }

        private static List<Player> Standing() => Players.Where(p => p != null && !p.IsDowned).ToList();

        private static float NearestPlayerDistance(Vector3 pos, List<Player> players)
        {
            float best = 1e30f;   // float.PositiveInfinity and LINQ's Min(selector) are stripped from the game's build
            foreach (var p in players) best = Mathf.Min(best, Vector3.Distance(p.transform.position, pos));
            return best;
        }

        /// <summary>A place zombies gather: a location the game generated, sized from its footprint.</summary>
        private struct Site
        {
            public Transform Root;
            public Vector3 Offset;   // footprint centre, in the root's frame
            public float Radius;
            public bool Big;
            public string Name;
        }

        private static readonly List<Site> Sites = new List<Site>();

        /// <summary>
        /// Locations the game generated: big ones (Major locations, or any whose footprint is at least
        /// Zombies.BigLocationArea) and small ones. Loot spots outside any location count as small.
        /// </summary>
        private static void ScanSites()
        {
            Sites.Clear();
            foreach (var poi in Object.FindObjectsByType<PoiDefinition>(FindObjectsSortMode.None))
            {
                var fp = poi.BakedFootprint;
                float area = fp.IsValid ? fp.size.x * fp.size.z : 0f;
                Sites.Add(new Site
                {
                    Root = poi.transform,
                    Offset = fp.IsValid ? fp.centerOffset : Vector3.zero,
                    Radius = fp.IsValid ? Mathf.Clamp(Mathf.Max(fp.size.x, fp.size.z) * 0.5f, 3f, 40f) : 8f,
                    Big = poi.Category == POICategory.Major || area >= Plugin.BigLocationArea.Value,
                    Name = poi.DisplayName,
                });
            }
            foreach (var loot in Object.FindObjectsByType<LootSpawnManager>(FindObjectsSortMode.None))
                if (loot.GetComponentInParent<PoiDefinition>() == null)
                    Sites.Add(new Site { Root = loot.transform, Radius = 6f, Name = loot.name });
        }

        /// <summary>Each location gets its zombies the first time a player comes within range.</summary>
        private static void PopulateLootSites()
        {
            if (Time.time >= _nextSiteScan)
            {
                _nextSiteScan = Time.time + 10f;
                ScanSites();
            }
            var standing = Standing();
            foreach (var site in Sites)
            {
                if (site.Root == null) continue;
                Vector3 c = site.Root.TransformPoint(new Vector3(site.Offset.x, 0f, site.Offset.z));
                float d = NearestPlayerDistance(c, standing);
                if (d > Plugin.SpawnMaxDistance.Value) continue;
                if (!Visited.Add(CellKey(c, 25f, 1))) continue;
                // A location the player is already standing in (e.g. where they woke up) stays empty.
                if (d < site.Radius + 15f) continue;
                Plugin.Log.LogInfo($"Location '{site.Name}' ({(site.Big ? "big" : "small")}, {site.Radius * 2f:0} m across).");
                SpawnGroup(c, 2f, site.Radius + 12f, site.Big ? Plugin.BigLocationGroup.Value : Plugin.SmallLocationGroup.Value,
                    onlyOffRoad: false, groundY: site.Root.position.y);
            }
        }

        private const int RoadProbeAngles = 48;

        /// <summary>
        /// Each stretch of road near a player may get one group, decided once. The ground round each player is
        /// swept in a fan of probes at a few distances, so road ahead is found even at driving speed (cells
        /// already decided aren't probed again).
        /// </summary>
        private static void PopulateRoads()
        {
            var standing = Standing();
            float min = Plugin.SpawnMinDistance.Value, max = Mathf.Max(min + 1f, Plugin.SpawnMaxDistance.Value);
            float spacing = Mathf.Max(20f, Plugin.RoadSpacing.Value);
            float turn = Random.Range(0f, 360f / RoadProbeAngles);   // a different fan each time, so nothing slips between
            foreach (var player in standing)
            {
                for (int r = 0; r < 3; r++)
                {
                    float radius = Mathf.Lerp(min, max, r / 2f);
                    for (int a = 0; a < RoadProbeAngles; a++)
                    {
                        Vector3 probe = player.transform.position + Quaternion.Euler(0f, turn + a * 360f / RoadProbeAngles, 0f) * Vector3.forward * radius;
                        long cell = CellKey(probe, spacing, 2);
                        if (Visited.Contains(cell) || !OnRoad(probe, out Vector3 roadPoint)) continue;
                        Visited.Add(cell);
                        if (NearestPlayerDistance(roadPoint, standing) < min * 0.8f) continue;
                        if (Random.value > Plugin.RoadGroupChance.Value) continue;
                        SpawnRoadGroup(roadPoint);
                    }
                }
            }
        }

        /// <summary>A group standing in the road (most of them) and on its shoulders, for drivers to hit or dodge.</summary>
        private static void SpawnRoadGroup(Vector3 roadPoint)
        {
            ParseRange(Plugin.RoadGroup.Value, out int lo, out int hi);
            int count = Mathf.Min(Random.Range(lo, hi + 1), Plugin.MaxZombies.Value - AliveCount - SpawnQueue.Count);
            if (count <= 0) return;
            Plugin.Log.LogInfo($"Placing {count} zombie(s) on the road at {FloatingOriginManager.ToTrueWorld(roadPoint)}.");
            for (int i = 0; i < count; i++)
            {
                bool inRoad = Random.value < Plugin.RoadOnRoadShare.Value;
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    Vector2 off = Random.insideUnitCircle * 14f;
                    Vector3 pos = roadPoint + new Vector3(off.x, 0f, off.y);
                    bool onRoad = OnRoad(pos, out Vector3 at);
                    if (inRoad != onRoad && attempt < 10) continue;   // the last tries take whatever ground there is
                    if (!onRoad && !GroundY(pos, out at.y)) continue;
                    if (!onRoad) at = new Vector3(pos.x, at.y, pos.z);
                    Spawn(at, Random.Range(0f, 360f));
                    break;
                }
            }
        }

        /// <param name="groundY">If set, spots whose top surface is well above this (roofs, rocks) are skipped.</param>
        private static void SpawnGroup(Vector3 centre, float minR, float maxR, string sizeRange, bool onlyOffRoad, float? groundY = null)
        {
            ParseRange(sizeRange, out int lo, out int hi);
            int count = Random.Range(lo, hi + 1);
            int room = Plugin.MaxZombies.Value - AliveCount - SpawnQueue.Count;
            if (count > room) count = Mathf.Max(0, room);
            Plugin.Log.LogInfo($"Placing {count} zombie(s) {(onlyOffRoad ? "beside the road" : "at a location")} at {FloatingOriginManager.ToTrueWorld(centre)}.");
            for (int i = 0; i < count; i++)
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    Vector2 off = Random.insideUnitCircle.normalized * Random.Range(minR, maxR);
                    Vector3 pos = centre + new Vector3(off.x, 0f, off.y);
                    if (onlyOffRoad && OnRoad(pos, out _)) continue;
                    if (!GroundY(pos, out float y)) continue;
                    if (groundY.HasValue && y > groundY.Value + 3f) continue;
                    pos.y = y;
                    Spawn(pos, Random.Range(0f, 360f));
                    break;
                }
            }
        }

        private static void ParseRange(string text, out int lo, out int hi)
        {
            lo = 1;
            hi = 1;
            var parts = (text ?? "").Split('-');
            if (parts.Length >= 1 && int.TryParse(parts[0].Trim(), out int a)) lo = hi = Mathf.Max(0, a);
            if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out int b)) hi = Mathf.Max(lo, b);
        }

        /// <summary>True if the ground under the point is a road surface (roads carry their own mesh collider).</summary>
        private static bool OnRoad(Vector3 pos, out Vector3 point)
        {
            point = pos;
            int n = Physics.RaycastNonAlloc(pos + Vector3.up * 30f, Vector3.down, Hits, 80f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                if (Hits[i].collider.GetComponentInParent<EvilRoad>() == null) continue;
                point = Hits[i].point;
                return true;
            }
            return false;
        }

        /// <summary>Host: the vehicle part that struck a zombie loses condition (more the faster it was going).</summary>
        public static void DamageVehiclePart(Rigidbody vehicle, Collider struck, Vector3 at, float mph)
        {
            var part = struck != null ? struck.GetComponentInParent<ConditionComponent>() : null;
            if (part == null || !part.transform.IsChildOf(vehicle.transform))
            {
                // The chassis itself has no condition: take the nearest part (bumper, tyre, hood...).
                part = null;
                float best = 9f;   // within 3 m
                foreach (var c in vehicle.GetComponentsInChildren<ConditionComponent>())
                {
                    float d = (c.transform.position - at).sqrMagnitude;
                    if (d < best) { best = d; part = c; }
                }
            }
            if (part == null) return;
            float damage = Plugin.VehicleDamage.Value + 0.2f * Mathf.Max(0f, mph - Plugin.VehicleKillMph.Value);
            part.ServerAddCondition(-damage);
            Plugin.Log.LogInfo($"{part.DisplayName} took {damage:0.#} damage ({part.Condition:0}/{part.MaxCondition:0}).");
        }

        public static void ServerSpawnAhead(float distance)
        {
            var cam = Camera.main;
            if (cam == null || Assets.Zombie == null) return;
            Vector3 fwd = cam.transform.forward;
            fwd.y = 0f;
            Vector3 pos = cam.transform.position + fwd.normalized * distance;
            if (GroundY(pos, out float y)) pos.y = y;
            Spawn(pos, Quaternion.LookRotation(-fwd).eulerAngles.y);
            Notice.Show("Zombie spawned.");
        }

        // Groups appear a couple of zombies per frame, so a 40-strong location doesn't hitch the game.
        private static readonly Queue<KeyValuePair<Vector3, float>> SpawnQueue = new Queue<KeyValuePair<Vector3, float>>();
        private const int SpawnsPerFrame = 2;

        private static void Spawn(Vector3 pos, float yaw) => SpawnQueue.Enqueue(new KeyValuePair<Vector3, float>(pos, yaw));

        private static void SpawnQueued()
        {
            for (int i = 0; i < SpawnsPerFrame && SpawnQueue.Count > 0; i++)
            {
                var s = SpawnQueue.Dequeue();
                ushort id = _nextId++;
                if (_nextId == 0) _nextId = 1;
                ZombieWorld.Create(id, s.Key, s.Value, server: true);
            }
        }

        private static void DespawnFar()
        {
            // Left far behind: removed for good (their place stays visited, so nothing refills).
            const float far = 350f;
            foreach (var z in ZombieWorld.All.Values.ToList())
            {
                if (z == null || !z.IsServer || z.Dead) continue;
                bool near = Players.Any(p => p != null && (p.transform.position - z.transform.position).sqrMagnitude < far * far);
                if (!near) Object.Destroy(z.gameObject);
            }
        }

        public static void ServerDamage(ZombieAgent zombie, float amount, bool headshot) => zombie.ServerTakeDamage(amount, headshot);

        public static void Broadcast(ushort id, ZombieEvent e)
        {
            var msg = new ZombieEventMsg { Id = id, Kind = (byte)e };
            ZombieWorld.OnEvent(msg);   // host's own zombie
            Net.SendToModded(msg);
        }

        public static void HitPlayer(Player player, float damage)
        {
            if (player.isLocalPlayer) LocalPlayer.Damage(damage);
            else Net.SendTo(player.connectionToClient, new PlayerHitMsg { Damage = damage });
        }

        /// <summary>Ground height under a point, ignoring players, zombies and moving bodies (vehicles, props).</summary>
        public static bool GroundY(Vector3 pos, out float y)
        {
            y = pos.y;
            int n = Physics.RaycastNonAlloc(pos + Vector3.up * 30f, Vector3.down, Hits, 80f, ~0, QueryTriggerInteraction.Ignore);
            float best = 1e30f;   // float.MaxValue is stripped from the game's build
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                var h = Hits[i];
                if (h.rigidbody != null && !h.rigidbody.isKinematic) continue;
                if (h.collider.GetComponentInParent<Player>() != null || h.collider.GetComponentInParent<ZombieAgent>() != null) continue;
                if (h.distance < best) { best = h.distance; y = h.point.y; found = true; }
            }
            return found;
        }

        public static void Clear()
        {
            foreach (var z in ZombieWorld.All.Values.ToList()) if (z != null && z.IsServer) Object.Destroy(z.gameObject);
            Players.Clear();
            Visited.Clear();
            SpawnQueue.Clear();
            Sites.Clear();
            _nextId = 1;
        }
    }
}
