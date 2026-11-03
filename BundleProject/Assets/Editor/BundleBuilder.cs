using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Builds the "winchesterzombies" AssetBundle for the Nomad Drive mod in the game's exact Unity version.
/// Run from the menu (Mod > Build Bundle) or batch mode: -executeMethod BundleBuilder.Build
///
/// The bundle holds meshes, textures, animation and prefabs only. No custom scripts are referenced (the
/// plugin adds its components at runtime) and materials are placeholders carrying textures/colours,
/// which the plugin converts to the game's HDRP/Lit shader on load.
/// </summary>
public static class BundleBuilder
{
    private const string Src = "Assets/Source";
    private const string Gen = "Assets/Generated";
    private const string BundleName = "winchesterzombies";
    private const string OutDir = "../out/bundle";

    private const string ZombieFbx = Src + "/Zombie/Zombie_Rigged.fbx";
    private const string RifleFbx = Src + "/Rifle/Winchester1873_Detailed.fbx";

    // Rifle clip paths are relative to the Recoil object holding the Animator.
    private const string Art = "Winchester1873";
    private const string Lever = Art + "/LeverPivot";
    private const string Bolt = Art + "/BoltRoot";
    private const string Trigger = Art + "/TriggerPivot";
    private const string Gate = Art + "/LoadingGatePivot";
    private const string Round = Art + "/ReloadRound";
    private static readonly Vector3 ReloadRot = new Vector3(-10f, 10f, 25f);
    private static readonly Vector3 ReloadPos = new Vector3(-0.25f, 0.06f, 0.06f);

    [MenuItem("Mod/Build Bundle")]
    public static void Build()
    {
        Directory.CreateDirectory(Gen);
        ConfigureTextures();
        ConfigureZombieImporter();
        ConfigureRifleImporter();

        var mats = BuildMaterials();
        var soft = Particles.SoftTexture(Gen);
        var smokeMat = Mat("M_Smoke", Color.white, 0f, 0f, soft, null);
        var rounds = Cartridges.Build(Gen, mats["Brass"], mats["BrassFired"], mats["Lead"], mats["Copper"]);
        var zombie = BuildZombiePrefab(mats["Zombie"]);
        var rifle = BuildRiflePrefab(mats["Rifle"], rounds.live, smokeMat);
        var impact = Particles.ImpactPrefab(Gen, smokeMat);
        var bloodSprayMat = Mat("M_BloodSpray", Color.white, 0f, 0f, soft, null);
        var spray = Particles.BloodSprayPrefab(Gen, bloodSprayMat);
        var bloodTextures = new[] { 1, 2, 3 }.Select(i => AssetDatabase.GetAssetPath(Particles.BloodTexture(Gen, i)));
        var ammoBoxes = AmmoBoxes.Build(Gen, (name, color, smoothness, tex) => Mat(name, color, 0f, smoothness, tex, null));
        // Hotbar icons: the rifle laid diagonally seen from its right side, the box from above and in front.
        var rifleIcon = Icons.Render(rifle, Gen + "/I_Rifle.png", Quaternion.Euler(-38f, 0f, 0f), Vector3.left);
        var ammoIcon = Icons.Render(ammoBoxes[0], Gen + "/I_AmmoBox.png", Quaternion.Euler(0f, 25f, 0f), new Vector3(0f, -0.8f, 1f), 1.15f);

        // The game strips Unity's particle setup API from its build, so effects are authored here as data;
        // the plugin only plays them.
        var assets = new List<string> { zombie, rifle, rounds.live, rounds.spent, impact, AssetDatabase.GetAssetPath(soft), spray };
        assets.AddRange(bloodTextures);
        assets.AddRange(ammoBoxes);
        assets.Add(rifleIcon);
        assets.Add(ammoIcon);
        foreach (var path in assets) AssetImporter.GetAtPath(path).assetBundleName = BundleName;
        AssetDatabase.SaveAssets();

        Directory.CreateDirectory(OutDir);
        BuildPipeline.BuildAssetBundles(OutDir, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        Debug.Log("[Bundle] Built " + Path.GetFullPath(Path.Combine(OutDir, BundleName)));
    }

    // ------------------------------------------------------------------ import settings

    private static void ConfigureTextures()
    {
        foreach (var path in new[] { Src + "/Zombie/Zombie_Normal.png", Src + "/Rifle/Rifle_Normal.png" })
        {
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            if (ti.textureType == TextureImporterType.NormalMap) continue;
            ti.textureType = TextureImporterType.NormalMap;
            ti.SaveAndReimport();
        }
    }

    private static void ConfigureZombieImporter()
    {
        var importer = (ModelImporter)AssetImporter.GetAtPath(ZombieFbx);
        importer.globalScale = 1f;
        importer.useFileScale = true;
        importer.bakeAxisConversion = true;
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.importAnimation = true;
        importer.materialImportMode = ModelImporterMaterialImportMode.None;
        importer.SaveAndReimport();

        var clips = importer.defaultClipAnimations;
        foreach (var clip in clips)
        {
            clip.name = clip.takeName.Contains("|") ? clip.takeName.Substring(clip.takeName.IndexOf('|') + 1) : clip.takeName;
            bool loop = clip.name == "Zombie_Idle" || clip.name == "Zombie_Walk";
            clip.loopTime = loop;
            clip.loopPose = loop;
            clip.lockRootRotation = clip.lockRootHeightY = clip.lockRootPositionXZ = true;
            clip.keepOriginalOrientation = clip.keepOriginalPositionY = clip.keepOriginalPositionXZ = true;
            // Attack keyed 1..40 in Blender, claws land on frame 19.
            if (clip.name == "Zombie_Attack")
                clip.events = new[] { new AnimationEvent { functionName = "OnAttackHit", time = 18f / 39f } };
        }
        importer.clipAnimations = clips;
        importer.SaveAndReimport();
    }

    private static void ConfigureRifleImporter()
    {
        var importer = (ModelImporter)AssetImporter.GetAtPath(RifleFbx);
        importer.globalScale = 1f;
        importer.useFileScale = true;
        importer.bakeAxisConversion = true;
        importer.importAnimation = false;
        importer.animationType = ModelImporterAnimationType.None;
        importer.materialImportMode = ModelImporterMaterialImportMode.None;
        importer.SaveAndReimport();
    }

    // ------------------------------------------------------------------ materials (placeholders for HDRP/Lit)

    private static Dictionary<string, Material> BuildMaterials()
    {
        Texture2D Tex(string p) => AssetDatabase.LoadAssetAtPath<Texture2D>(p);
        return new Dictionary<string, Material>
        {
            ["Zombie"] = Mat("M_Zombie", Color.white, 0f, 0.25f, Tex(Src + "/Zombie/Zombie_Color.png"), Tex(Src + "/Zombie/Zombie_Normal.png")),
            ["Rifle"] = Mat("M_Rifle", Color.white, 0.45f, 0.45f, Tex(Src + "/Rifle/Rifle_Color.png"), Tex(Src + "/Rifle/Rifle_Normal.png")),
            ["Brass"] = Mat("M_CartridgeBrass", new Color(0.80f, 0.62f, 0.30f), 1f, 0.72f, null, null),
            ["BrassFired"] = Mat("M_CartridgeBrassFired", new Color(0.62f, 0.46f, 0.22f), 1f, 0.45f, null, null),
            ["Lead"] = Mat("M_Lead", new Color(0.42f, 0.42f, 0.44f), 0.35f, 0.28f, null, null),
            ["Copper"] = Mat("M_Copper", new Color(0.49f, 0.25f, 0.13f), 0.65f, 0.3f, null, null),
        };
    }

    /// <summary>Standard-shader material; the plugin reads _Color/_MainTex/_BumpMap/_Metallic/_Glossiness.</summary>
    private static Material Mat(string name, Color color, float metallic, float smoothness, Texture2D albedo, Texture2D normal)
    {
        string path = $"{Gen}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.SetColor("_Color", color);
        mat.SetFloat("_Metallic", metallic);
        mat.SetFloat("_Glossiness", smoothness);
        mat.SetTexture("_MainTex", albedo);
        mat.SetTexture("_BumpMap", normal);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // ------------------------------------------------------------------ zombie

    private static string BuildZombiePrefab(Material mat)
    {
        var controller = BuildZombieController();
        var root = new GameObject("WZ_Zombie");
        try
        {
            var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ZombieFbx), root.transform);
            model.name = "Model";
            // The imported rig faces -Z; turn it so the zombie walks and claws along the root's +Z.
            model.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            foreach (var r in model.GetComponentsInChildren<Renderer>()) r.sharedMaterial = mat;
            var animator = model.GetComponent<Animator>();
            if (animator == null) animator = model.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.avatar = AssetDatabase.LoadAllAssetsAtPath(ZombieFbx).OfType<Avatar>().First();
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            string path = Gen + "/WZ_Zombie.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            return path;
        }
        finally { Object.DestroyImmediate(root); }
    }

    private static AnimationClip ZClip(string name) =>
        AssetDatabase.LoadAllAssetsAtPath(ZombieFbx).OfType<AnimationClip>().First(c => c.name == name);

    private static AnimatorController BuildZombieController()
    {
        string path = Gen + "/Zombie.controller";
        AssetDatabase.DeleteAsset(path);
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
        ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("WalkAnimSpeed", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("Attack", AnimatorControllerParameterType.Trigger);
        ctrl.AddParameter("Hit", AnimatorControllerParameterType.Trigger);
        ctrl.AddParameter("Die", AnimatorControllerParameterType.Trigger);
        ctrl.AddParameter("IsDead", AnimatorControllerParameterType.Bool);
        ctrl.parameters = ctrl.parameters.Select(p => { if (p.name == "WalkAnimSpeed") p.defaultFloat = 1f; return p; }).ToArray();

        var sm = ctrl.layers[0].stateMachine;
        var idle = sm.AddState("Idle"); idle.motion = ZClip("Zombie_Idle");
        var walk = sm.AddState("Walk"); walk.motion = ZClip("Zombie_Walk");
        walk.speedParameterActive = true; walk.speedParameter = "WalkAnimSpeed";
        var attack = sm.AddState("Attack"); attack.motion = ZClip("Zombie_Attack");
        var hit = sm.AddState("Hit"); hit.motion = ZClip("Zombie_Hit");
        var death = sm.AddState("Death"); death.motion = ZClip("Zombie_Death");
        sm.defaultState = idle;

        var t = idle.AddTransition(walk); t.hasExitTime = false; t.duration = 0.25f; t.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
        t = walk.AddTransition(idle); t.hasExitTime = false; t.duration = 0.3f; t.AddCondition(AnimatorConditionMode.Less, 0.05f, "Speed");
        t = sm.AddAnyStateTransition(attack); t.hasExitTime = false; t.duration = 0.1f; t.canTransitionToSelf = false;
        t.AddCondition(AnimatorConditionMode.If, 0f, "Attack"); t.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsDead");
        t = attack.AddTransition(idle); t.hasExitTime = true; t.exitTime = 0.9f; t.duration = 0.2f;
        t = sm.AddAnyStateTransition(hit); t.hasExitTime = false; t.duration = 0.05f; t.canTransitionToSelf = true;
        t.AddCondition(AnimatorConditionMode.If, 0f, "Hit"); t.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsDead");
        t = hit.AddTransition(idle); t.hasExitTime = true; t.exitTime = 0.85f; t.duration = 0.15f;
        t = sm.AddAnyStateTransition(death); t.hasExitTime = false; t.duration = 0.1f; t.canTransitionToSelf = false;
        t.AddCondition(AnimatorConditionMode.If, 0f, "Die");
        AssetDatabase.SaveAssets();
        return ctrl;
    }

    // ------------------------------------------------------------------ rifle

    private static string BuildRiflePrefab(Material mat, string cartridgePath, Material smokeMat)
    {
        var controller = BuildRifleController();
        var root = new GameObject("WZ_Winchester");
        try
        {
            var recoil = new GameObject("Recoil");
            recoil.transform.SetParent(root.transform, false);
            var animator = recoil.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            var art = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(RifleFbx), recoil.transform);
            PrefabUtility.UnpackPrefabInstance(art, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            art.name = Art;
            foreach (var r in art.GetComponentsInChildren<Renderer>()) r.sharedMaterial = mat;

            var round = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(cartridgePath), art.transform);
            PrefabUtility.UnpackPrefabInstance(round, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            round.name = "ReloadRound";
            round.transform.localPosition = art.transform.Find("Attachments/Loading").localPosition;
            round.SetActive(false);

            Particles.MuzzleSmoke(art.transform.Find("Attachments/Muzzle"), smokeMat);

            string path = Gen + "/WZ_Winchester.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            return path;
        }
        finally { Object.DestroyImmediate(root); }
    }

    private static AnimatorController BuildRifleController()
    {
        var art = AssetDatabase.LoadAssetAtPath<GameObject>(RifleFbx).transform;
        Vector3 boltBase = art.Find("BoltRoot").localPosition;
        Vector3 load = art.Find("Attachments/Loading").localPosition;

        var idle = new ClipBuilder("Rifle_Idle").Euler(Lever, (0f, Vector3.zero), (1f, Vector3.zero)).Save(true);
        var fire = new ClipBuilder("Rifle_Fire")
            .Euler(Trigger, (0f, X(0)), (0.03f, X(14)), (0.12f, X(14)), (0.22f, X(0)), (0.25f, X(0))).Save(false);
        var cycle = new ClipBuilder("Rifle_Cycle")
            .Euler(Lever, (0f, X(0)), (0.2f, X(-62)), (0.28f, X(-62)), (0.48f, X(0)), (0.62f, X(0)))
            .Position(Bolt, boltBase, (0f, Vector3.zero), (0.03f, Vector3.zero), (0.2f, Z(-0.042f)), (0.28f, Z(-0.042f)), (0.46f, Vector3.zero), (0.62f, Vector3.zero))
            .Euler(Art, (0f, Vector3.zero), (0.2f, new Vector3(-3f, 0f, 2f)), (0.3f, new Vector3(-3f, 0f, 2f)), (0.48f, X(1)), (0.62f, Vector3.zero))
            .Position(Art, Vector3.zero, (0f, Vector3.zero), (0.2f, new Vector3(0f, -0.006f, 0f)), (0.3f, new Vector3(0f, -0.006f, 0f)), (0.5f, Vector3.zero), (0.62f, Vector3.zero))
            .Event(0.21f, "OnEject").Event(0.45f, "OnChamber").Event(0.6f, "OnCycleEnd").Save(false);
        var reloadStart = new ClipBuilder("Rifle_ReloadStart")
            .Euler(Art, (0f, Vector3.zero), (0.3f, ReloadRot)).Position(Art, Vector3.zero, (0f, Vector3.zero), (0.3f, ReloadPos))
            .Event(0.29f, "OnReloadReady").Save(false);
        var reloadInsert = new ClipBuilder("Rifle_ReloadInsert")
            .Euler(Art, (0f, ReloadRot), (0.24f, ReloadRot), (0.3f, ReloadRot + X(2)), (0.42f, ReloadRot), (0.55f, ReloadRot))
            .Position(Art, Vector3.zero, (0f, ReloadPos), (0.24f, ReloadPos), (0.3f, ReloadPos + Z(0.006f)), (0.42f, ReloadPos), (0.55f, ReloadPos))
            .Active(Round, (0f, true), (0.38f, false), (0.55f, false))
            .Position(Round, load, (0f, new Vector3(0.013f, -0.007f, -0.0835f)), (0.12f, new Vector3(0.0036f, 0f, -0.0575f)),
                (0.28f, new Vector3(-0.01f, -0.001f, -0.0235f)), (0.38f, new Vector3(-0.02f, -0.002f, 0.0065f)), (0.55f, new Vector3(-0.02f, -0.002f, 0.0065f)))
            .Euler(Round, (0f, Y(-15)), (0.12f, Y(-15)), (0.28f, Y(-10)), (0.38f, Y(-5)), (0.55f, Y(-5)))
            .Euler(Gate, (0f, Vector3.zero), (0.13f, Vector3.zero), (0.2f, Y(35)), (0.34f, Y(35)), (0.42f, Vector3.zero), (0.55f, Vector3.zero))
            .Event(0.37f, "OnRoundInserted").Event(0.53f, "OnInsertEnd").Save(false);
        var reloadEnd = new ClipBuilder("Rifle_ReloadEnd")
            .Euler(Art, (0f, ReloadRot), (0.3f, Vector3.zero)).Position(Art, Vector3.zero, (0f, ReloadPos), (0.3f, Vector3.zero))
            .Event(0.29f, "OnReloadEnd").Save(false);

        string path = Gen + "/Winchester.controller";
        AssetDatabase.DeleteAsset(path);
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
        var sm = ctrl.layers[0].stateMachine;
        AnimatorState Add(string n, Motion m) { var s = sm.AddState(n); s.motion = m; return s; }
        var idleS = Add("Idle", idle);
        var fireS = Add("Fire", fire);
        var cycleS = Add("Cycle", cycle);
        Add("ReloadStart", reloadStart);
        Add("ReloadInsert", reloadInsert);
        var endS = Add("ReloadEnd", reloadEnd);
        sm.defaultState = idleS;
        foreach (var from in new[] { fireS, cycleS, endS })
        {
            var t = from.AddTransition(idleS);
            t.hasExitTime = true; t.exitTime = 1f; t.duration = 0f;
        }
        AssetDatabase.SaveAssets();
        return ctrl;
    }

    private static Vector3 X(float v) => new Vector3(v, 0f, 0f);
    private static Vector3 Y(float v) => new Vector3(0f, v, 0f);
    private static Vector3 Z(float v) => new Vector3(0f, 0f, v);

    private sealed class ClipBuilder
    {
        private readonly AnimationClip _clip;
        private readonly List<AnimationEvent> _events = new List<AnimationEvent>();

        public ClipBuilder(string name) => _clip = new AnimationClip { name = name, frameRate = 60f };

        public ClipBuilder Euler(string path, params (float t, Vector3 v)[] keys)
        {
            Curve(path, typeof(Transform), "localEulerAnglesRaw.x", keys.Select(k => (k.t, k.v.x)));
            Curve(path, typeof(Transform), "localEulerAnglesRaw.y", keys.Select(k => (k.t, k.v.y)));
            Curve(path, typeof(Transform), "localEulerAnglesRaw.z", keys.Select(k => (k.t, k.v.z)));
            return this;
        }

        public ClipBuilder Position(string path, Vector3 basePos, params (float t, Vector3 offset)[] keys)
        {
            Curve(path, typeof(Transform), "m_LocalPosition.x", keys.Select(k => (k.t, basePos.x + k.offset.x)));
            Curve(path, typeof(Transform), "m_LocalPosition.y", keys.Select(k => (k.t, basePos.y + k.offset.y)));
            Curve(path, typeof(Transform), "m_LocalPosition.z", keys.Select(k => (k.t, basePos.z + k.offset.z)));
            return this;
        }

        public ClipBuilder Active(string path, params (float t, bool on)[] keys)
        {
            Curve(path, typeof(GameObject), "m_IsActive", keys.Select(k => (k.t, k.on ? 1f : 0f)), stepped: true);
            return this;
        }

        public ClipBuilder Event(float time, string fn)
        {
            _events.Add(new AnimationEvent { time = time, functionName = fn });
            return this;
        }

        public AnimationClip Save(bool loop)
        {
            AnimationUtility.SetAnimationEvents(_clip, _events.ToArray());
            var s = AnimationUtility.GetAnimationClipSettings(_clip);
            s.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(_clip, s);
            string path = $"{Gen}/{_clip.name}.anim";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(_clip, path);
            return _clip;
        }

        private void Curve(string path, System.Type type, string prop, IEnumerable<(float t, float v)> keys, bool stepped = false)
        {
            var curve = new AnimationCurve(keys.Select(k => new Keyframe(k.t, k.v)).ToArray());
            var mode = stepped ? AnimationUtility.TangentMode.Constant : AnimationUtility.TangentMode.ClampedAuto;
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, mode);
                AnimationUtility.SetKeyRightTangentMode(curve, i, mode);
            }
            AnimationUtility.SetEditorCurve(_clip, EditorCurveBinding.FloatCurve(path, type, prop), curve);
        }
    }
}
