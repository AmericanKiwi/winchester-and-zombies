using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Mirror;
using UnityEngine;

namespace WinchesterZombies
{
    /// <summary>
    /// Winchester 1873 + zombies for Nomad Drive. Everyone in a session needs the same version: the rifle is
    /// a networked item and all zombie/rifle events travel as custom Mirror messages (see Net).
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.americankiwi.nomaddrive.winchesterzombies";
        public const string Name = "Winchester & Zombies";
        public const string Version = "1.5.0";

        internal static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }

        // Rifle
        internal static ConfigEntry<float> RifleDamage;
        internal static ConfigEntry<int> RifleCapacity;
        internal static ConfigEntry<int> PocketCapacity;
        internal static ConfigEntry<bool> InfiniteReserve;
        /// <summary>Rounds in the magazine tube when full (the rest of the capacity is the chambered round).</summary>
        internal static int TubeCapacity => Mathf.Max(1, RifleCapacity.Value - 1);
        // Ammo boxes and where rifles/boxes appear
        internal static ConfigEntry<int> BoxRounds;
        internal static ConfigEntry<int> StartRifles;
        internal static ConfigEntry<int> StartAmmoBoxes;
        internal static ConfigEntry<float> LootRifleChance;
        internal static ConfigEntry<float> LootAmmoChance;
        internal static ConfigEntry<KeyCode> SpawnAmmoKey;
        internal static ConfigEntry<string> HipOffsetText;
        internal static ConfigEntry<string> AimOffsetText;
        internal static Vector3 HipOffset = new Vector3(0.17f, -0.17f, 0.28f);
        internal static Vector3 AimOffset = new Vector3(0f, -0.046f, 0.24f);
        internal static ConfigEntry<string> RightHandGripText;
        internal static ConfigEntry<string> LeftHandPositionText;
        internal static ConfigEntry<string> LeftHandRotationText;
        internal static Vector3 RightHandGrip = new Vector3(-0.10f, -0.035f, 90f);
        internal static Vector3 LeftHandPosition = new Vector3(-0.01f, -0.07f, 0.25f);
        internal static Vector3 LeftHandRotation = new Vector3(90f, 90f, 0f);

        private static void ReadOffsets()
        {
            if (RifleFactory.TryParse(HipOffsetText.Value, out var hip)) HipOffset = hip;
            if (RifleFactory.TryParse(AimOffsetText.Value, out var aim)) AimOffset = aim;
            if (RifleFactory.TryParse(RightHandGripText.Value, out var rh)) RightHandGrip = rh;
            if (RifleFactory.TryParse(LeftHandPositionText.Value, out var lp)) LeftHandPosition = lp;
            if (RifleFactory.TryParse(LeftHandRotationText.Value, out var lr)) LeftHandRotation = lr;
        }
        // Zombies
        internal static ConfigEntry<bool> ZombiesEnabled;
        internal static ConfigEntry<int> MaxZombies;
        internal static ConfigEntry<float> ZombieHealth;
        internal static ConfigEntry<float> ZombieDamage;
        internal static ConfigEntry<float> ZombieSpeed;
        internal static ConfigEntry<float> SpawnMinDistance;
        internal static ConfigEntry<float> SpawnMaxDistance;
        internal static ConfigEntry<string> BigLocationGroup;
        internal static ConfigEntry<string> SmallLocationGroup;
        internal static ConfigEntry<float> BigLocationArea;
        internal static ConfigEntry<float> WakeRange;
        internal static ConfigEntry<string> RoadGroup;
        internal static ConfigEntry<float> RoadSpacing;
        internal static ConfigEntry<float> RoadGroupChance;
        internal static ConfigEntry<float> RoadOnRoadShare;
        internal static ConfigEntry<bool> HeadshotKills;
        internal static ConfigEntry<bool> ShowBlood;
        internal static ConfigEntry<KeyCode> DebugBloodKey;
        internal static ConfigEntry<string> VehicleBloodColor;
        internal static ConfigEntry<float> BloodGroundSeconds;
        internal static ConfigEntry<float> BloodVehicleSeconds;
        internal static ConfigEntry<float> VehicleKillMph;
        internal static ConfigEntry<float> VehicleDamage;
        internal static ConfigEntry<float> ImpactStrength;
        internal static ConfigEntry<float> BodyBumpStrength;
        internal static ConfigEntry<string> BodyTyreWear;
        // Keys
        internal static ConfigEntry<KeyCode> ReloadKey;
        internal static ConfigEntry<KeyCode> SpawnRifleKey;
        internal static ConfigEntry<KeyCode> SpawnZombieKey;
        internal static ConfigEntry<KeyCode> ToggleZombiesKey;
        internal static ConfigEntry<KeyCode> ReloadConfigKey;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            AdoptOldConfig();

            RifleDamage = Config.Bind("Rifle", "Damage", 50f, "Damage per hit. Zombies have Zombies.Health HP.");
            RifleCapacity = Config.Bind("Rifle", "Capacity", 7, "Rounds the rifle holds, counting the one in the chamber (host).");
            PocketCapacity = Config.Bind("Rifle", "PocketCapacity", 7, "Spare rounds a player can carry; T loads the rifle from these (host).");
            InfiniteReserve = Config.Bind("Rifle", "InfinitePocket", false, "Pocket rounds never run out (host).");

            BoxRounds = Config.Bind("Ammo", "BoxRounds", 20, "Rounds in a new ammo box. Rifle in hand, look at a box and hold E to fill your pocket; empty boxes disappear (host).");
            StartRifles = Config.Bind("Spawns", "StartRifles", 0, "Rifles placed at the start of a new world. 0 = one per player slot in the session (host).");
            StartAmmoBoxes = Config.Bind("Spawns", "StartAmmoBoxes", 0, "Ammo boxes placed at the start of a new world. 0 = one per player slot (host).");
            LootRifleChance = Config.Bind("Spawns", "LootRifleChance", 0.067f, "Chance a loot location has a rifle, rolled once per location (0-1, host).");
            LootAmmoChance = Config.Bind("Spawns", "LootAmmoChance", 0.25f, "Chance a loot location has an ammo box, rolled once per location (0-1, host).");

            ZombiesEnabled = Config.Bind("Zombies", "Enabled", true, "Spawn zombies around players (host setting).");
            MaxZombies = Config.Bind("Zombies", "MaxAlive", 150, "Cap on zombies alive at once (host). Locations are populated once and never refill. Zombies far from everyone sleep, so most of these cost little.");
            ZombieHealth = Config.Bind("Zombies", "Health", 100f, "Zombie hit points (host).");
            ZombieDamage = Config.Bind("Zombies", "Damage", 12f, "Damage per claw hit (host).");
            ZombieSpeed = Config.Bind("Zombies", "WalkSpeed", 1.1f, "Shambling speed in m/s (host).");
            SpawnMinDistance = Config.Bind("Zombies", "SpawnMinDistance", 60f, "Closest to a player a group can appear (host).");
            SpawnMaxDistance = Config.Bind("Zombies", "SpawnMaxDistance", 150f, "Farthest from a player a group is placed (host).");
            BigLocationGroup = Config.Bind("Zombies", "BigLocationGroupSize", "20-40", "Zombies placed once at each big location (buildings; min-max, host).");
            SmallLocationGroup = Config.Bind("Zombies", "SmallLocationGroupSize", "5-10", "Zombies placed once at each small location (min-max, host).");
            BigLocationArea = Config.Bind("Zombies", "BigLocationArea", 400f, "A location whose footprint is at least this many square metres counts as big, as do the game's Major locations (host).");
            WakeRange = Config.Bind("Zombies", "WakeRange", 90f, "Zombies farther than this from every player sleep: no AI or animation until someone comes closer (host).");
            RoadGroup = Config.Bind("Zombies", "RoadGroupSize", "2-5", "Zombies in a roadside group (min-max, host).");
            RoadSpacing = Config.Bind("Zombies", "RoadSpacing", 100f, "Road is split into stretches this long (m); each gets at most one group, once (host).");
            RoadGroupChance = Config.Bind("Zombies", "RoadGroupChance", 0.7f, "Chance a road stretch has a group (0-1, host).");
            RoadOnRoadShare = Config.Bind("Zombies", "RoadGroupInRoad", 0.75f, "Share of a road group standing in the road itself; the rest are on the shoulders (0-1, host).");
            HeadshotKills = Config.Bind("Zombies", "HeadshotKills", true, "A rifle hit to the head kills outright (host).");
            ShowBlood = Config.Bind("Blood", "Enabled", true, "Show blood spray and stains on this machine.");
            VehicleBloodColor = Config.Bind("Blood", "VehicleColor", "#C70303", "Colour painted onto vehicles (hex). The car-paint shader shifts it: darker turns brown, lighter turns pink.");
            DebugBloodKey = Config.Bind("Keys", "DebugBlood", KeyCode.None, "Testing: stain whatever you're looking at (only on your screen).");
            BloodGroundSeconds = Config.Bind("Blood", "GroundStainSeconds", 300f, "How long blood on the ground lasts before fading.");
            BloodVehicleSeconds = Config.Bind("Blood", "VehicleStainSeconds", 900f, "How long blood on a vehicle lasts before fading.");
            VehicleKillMph = Config.Bind("Vehicles", "KillSpeedMph", 10f, "Hitting a zombie faster than this kills it (host).");
            VehicleDamage = Config.Bind("Vehicles", "PartDamage", 4f, "Condition lost by the part that hits a zombie, plus 0.2 per mph over the kill speed (parts have 100, host).");
            ImpactStrength = Config.Bind("Vehicles", "ImpactJolt", 2f, "How hard hitting a zombie jolts the vehicle (1 = a real 75 kg body, 0 = none; the driver's setting).");
            BodyBumpStrength = Config.Bind("Vehicles", "BodyBump", 1f, "How hard a tyre bumps when it rolls over a body (0 = none; the driver's setting).");
            BodyTyreWear = Config.Bind("Vehicles", "BodyTyreWear", "1-2", "Percent of a tyre's condition lost each time it rolls over a body (min-max, host).");

            // R is the game's placement-mode key and F8 its bug reporter, so the defaults stay clear of them.
            ReloadKey = Config.Bind("Keys", "Reload", KeyCode.T, "Load rounds into the rifle while holding it.");
            SpawnRifleKey = Config.Bind("Keys", "SpawnRifle", KeyCode.PageUp, "Host: spawn a Winchester in front of you.");
            SpawnZombieKey = Config.Bind("Keys", "SpawnZombie", KeyCode.Home, "Host: spawn a zombie 20 m ahead.");
            ToggleZombiesKey = Config.Bind("Keys", "ToggleZombies", KeyCode.End, "Host: pause/resume populating new roads and loot locations.");
            SpawnAmmoKey = Config.Bind("Keys", "SpawnAmmoBox", KeyCode.Delete, "Host: spawn an ammo box in front of you.");

            HipOffsetText = Config.Bind("Rifle", "HipPosition", "0.17,-0.17,0.28", "Where the receiver sits when carried, from your eye (right, up, forward in metres).");
            AimOffsetText = Config.Bind("Rifle", "AimPosition", "0,-0.046,0.24", "Where the receiver sits when aiming (right mouse), from your eye. Up = -0.046 puts the sights on the crosshair.");
            RightHandGripText = Config.Bind("Hands", "RightHandGrip", "-0.10,-0.035,90", "Right hand on the wrist: distance forward of the receiver, height, and twist round the bore (degrees), in metres along the rifle.");
            LeftHandPositionText = Config.Bind("Hands", "LeftHandPosition", "-0.01,-0.07,0.25", "Left hand on the forend: right, up, forward of the receiver (metres).");
            LeftHandRotationText = Config.Bind("Hands", "LeftHandRotation", "90,90,0", "Left hand angle on the forend (degrees, x/y/z along the rifle).");
            ReadOffsets();
            ReloadConfigKey = Config.Bind("Keys", "ReloadConfig", KeyCode.PageDown, "Re-read this config file (carry pose, keys, damage) without restarting.");

            ModSave.Patch(Guid);

            string bundlePath = Path.Combine(Path.GetDirectoryName(Info.Location), "winchesterzombies");
            StartCoroutine(Assets.Load(bundlePath));
            gameObject.AddComponent<Hud>();
            Log.LogInfo($"{Name} {Version} loaded.");
        }

        /// <summary>
        /// The plugin id changed in 1.4.1, which renamed the settings file. If this is the first run under the new
        /// name, take over the settings from the previous file (any com.*.nomaddrive.winchesterzombies.cfg) and remove it.
        /// </summary>
        private void AdoptOldConfig()
        {
            try
            {
                string mine = Config.ConfigFilePath;
                if (File.Exists(mine)) return;
                foreach (string old in Directory.GetFiles(Path.GetDirectoryName(mine), "com.*.nomaddrive.winchesterzombies.cfg"))
                {
                    if (string.Equals(old, mine, System.StringComparison.OrdinalIgnoreCase)) continue;
                    File.Move(old, mine);
                    Config.Reload();
                    Log.LogInfo("Kept your settings from the previous settings file.");
                    return;
                }
            }
            catch (System.Exception e) { Log.LogWarning("Couldn't carry over the old settings file: " + e.Message); }
        }

        private void Update()
        {
            if (!Assets.Ready) return;
            // The rifle prefab is cloned from the game's shovel, so it is built once the game is running.
            RifleFactory.EnsureBuilt();
            AmmoBoxFactory.EnsureBuilt();
            Net.Tick();
            ZombieDirector.Tick();
            VehicleHits.Tick();
            ModSave.Tick();
            LootPlacer.Tick();

            if (DebugBloodKey.Value != KeyCode.None && Keys.Down(DebugBloodKey.Value)) Blood.DebugAtCrosshair();

            if (Keys.Down(ReloadConfigKey.Value))
            {
                Config.Reload();
                ReadOffsets();
                Notice.Show("Winchester & Zombies config reloaded.");
            }

            if (!NetworkServer.active) return;
            if (Keys.Down(SpawnRifleKey.Value)) RifleFactory.ServerSpawnInFrontOfLocalPlayer();
            if (Keys.Down(SpawnAmmoKey.Value)) AmmoBoxFactory.ServerSpawnInFrontOfLocalPlayer();
            if (Keys.Down(SpawnZombieKey.Value)) ZombieDirector.ServerSpawnAhead(20f);
            if (Keys.Down(ToggleZombiesKey.Value))
            {
                ZombieDirector.SpawningPaused = !ZombieDirector.SpawningPaused;
                Log.LogInfo(ZombieDirector.SpawningPaused ? "Zombie spawning paused." : "Zombie spawning resumed.");
            }
        }
    }
}
