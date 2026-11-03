using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace WinchesterZombies
{
    /// <summary>
    /// Prefabs from the bundle. The bundle's materials are Standard-shader placeholders; on load they are
    /// rebuilt on the game's HDRP/Lit so they render like the game's own props.
    /// </summary>
    internal static class Assets
    {
        public static GameObject Rifle;
        public static GameObject Zombie;
        public static GameObject Cartridge;
        public static GameObject SpentCase;
        public static GameObject Impact;
        public static GameObject BloodSpray;
        public static readonly GameObject[] AmmoBoxes = new GameObject[2];   // green, kraft
        public static Sprite RifleIcon, AmmoIcon;                           // hotbar icons
        public static readonly List<Texture2D> BloodTextures = new List<Texture2D>();
        public static Texture2D SoftTexture;
        public static Material SmokeMaterial;
        public static Material BloodSprayMaterial;
        public static bool Ready { get; private set; }

        private static readonly Dictionary<Material, Material> Converted = new Dictionary<Material, Material>();

        /// <summary>The game's build only keeps Unity's async bundle API, so loading is a coroutine.</summary>
        public static IEnumerator Load(string path)
        {
            var open = AssetBundle.LoadFromFileAsync(path);
            yield return open;
            var bundle = open.assetBundle;
            if (bundle == null)
            {
                Plugin.Log.LogError($"Asset bundle not found or unreadable at {path}; the mod is disabled.");
                yield break;
            }

            IEnumerator Get<T>(string name, System.Action<T> set) where T : Object
            {
                var req = bundle.LoadAssetAsync("Assets/Generated/" + name, typeof(T));
                yield return req;
                set(req.asset as T);
            }
            yield return Get<GameObject>("WZ_Winchester.prefab", a => Rifle = a);
            yield return Get<GameObject>("WZ_Zombie.prefab", a => Zombie = a);
            yield return Get<GameObject>("WZ_Cartridge.prefab", a => Cartridge = a);
            yield return Get<GameObject>("WZ_SpentCase.prefab", a => SpentCase = a);
            yield return Get<GameObject>("WZ_Impact.prefab", a => Impact = a);
            yield return Get<Texture2D>("T_Soft.png", a => SoftTexture = a);
            yield return Get<GameObject>("WZ_BloodSpray.prefab", a => BloodSpray = a);
            for (int i = 1; i <= 3; i++)
                yield return Get<Texture2D>($"T_Blood{i}.png", a => { if (a != null) BloodTextures.Add(a); });
            yield return Get<GameObject>("WZ_AmmoBox_Green.prefab", a => AmmoBoxes[0] = a);
            yield return Get<GameObject>("WZ_AmmoBox_Kraft.prefab", a => AmmoBoxes[1] = a);
            yield return Get<Sprite>("I_Rifle.png", a => RifleIcon = a);
            yield return Get<Sprite>("I_AmmoBox.png", a => AmmoIcon = a);

            Ready = Rifle != null && Zombie != null && SpentCase != null && AmmoBoxes[0] != null && AmmoBoxes[1] != null;
            if (Ready) Plugin.Log.LogInfo("Assets loaded.");
            else Plugin.Log.LogError("Some assets are missing from the bundle.");
        }

        /// <summary>Swaps placeholder materials on an instance for HDRP/Lit copies (cached per source material).</summary>
        public static void ApplyHdrp(GameObject root)
        {
            var lit = Shader.Find("HDRP/Lit");
            if (lit == null)
            {
                Plugin.Log.LogWarning("HDRP/Lit shader not found; models may render pink.");
                return;
            }
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer)
                {
                    bool blood = r.sharedMaterial != null && r.sharedMaterial.name.Contains("Blood");
                    r.sharedMaterial = blood ? BloodSprayMat() : Smoke();
                    continue;
                }
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null) mats[i] = Convert(mats[i], lit);
                r.sharedMaterials = mats;
            }
        }

        private static Material Convert(Material src, Shader lit)
        {
            if (src.shader == lit) return src;
            if (Converted.TryGetValue(src, out var done)) return done;

            var m = new Material(lit) { name = src.name + " (HDRP)" };
            m.SetColor("_BaseColor", src.HasProperty("_Color") ? src.GetColor("_Color") : Color.white);
            if (src.HasProperty("_MainTex") && src.GetTexture("_MainTex") != null)
                m.SetTexture("_BaseColorMap", src.GetTexture("_MainTex"));
            if (src.HasProperty("_BumpMap") && src.GetTexture("_BumpMap") != null)
            {
                m.SetTexture("_NormalMap", src.GetTexture("_BumpMap"));
                m.SetFloat("_NormalScale", 1f);
            }
            m.SetFloat("_Metallic", src.HasProperty("_Metallic") ? src.GetFloat("_Metallic") : 0f);
            m.SetFloat("_Smoothness", src.HasProperty("_Glossiness") ? src.GetFloat("_Glossiness") : 0.5f);
            HDMaterial.ValidateMaterial(m);   // sets HDRP keywords/passes for the textures we assigned
            Converted[src] = m;
            return m;
        }

        /// <summary>Dark red droplets for the blood spray (HDRP/Unlit ignores particle colour, so it's on the material).</summary>
        public static Material BloodSprayMat()
        {
            if (BloodSprayMaterial != null) return BloodSprayMaterial;
            var unlit = Shader.Find("HDRP/Unlit");
            if (unlit == null) return null;
            BloodSprayMaterial = new Material(unlit) { name = "WZ Blood Spray" };
            BloodSprayMaterial.SetColor("_UnlitColor", new Color(0.32f, 0.02f, 0.02f, 0.9f));
            if (SoftTexture != null) BloodSprayMaterial.SetTexture("_UnlitColorMap", SoftTexture);
            BloodSprayMaterial.SetFloat("_SurfaceType", 1f);
            BloodSprayMaterial.SetFloat("_BlendMode", 0f);
            HDMaterial.ValidateMaterial(BloodSprayMaterial);
            return BloodSprayMaterial;
        }

        /// <summary>Soft transparent material for muzzle smoke and dust puffs.</summary>
        public static Material Smoke()
        {
            if (SmokeMaterial != null) return SmokeMaterial;
            var unlit = Shader.Find("HDRP/Unlit");
            if (unlit == null) return null;
            SmokeMaterial = new Material(unlit) { name = "WZ Smoke" };
            // HDRP/Unlit ignores particle vertex colour, so the faint grey and transparency live on the material.
            SmokeMaterial.SetColor("_UnlitColor", new Color(0.72f, 0.72f, 0.72f, 0.16f));
            if (SoftTexture != null) SmokeMaterial.SetTexture("_UnlitColorMap", SoftTexture);
            SmokeMaterial.SetFloat("_SurfaceType", 1f);   // transparent
            SmokeMaterial.SetFloat("_BlendMode", 0f);     // alpha
            HDMaterial.ValidateMaterial(SmokeMaterial);
            return SmokeMaterial;
        }
    }

    /// <summary>Raw keyboard keys through Rewired (the game's input system), with Unity's legacy input as a fallback.</summary>
    internal static class Keys
    {
        public static bool Down(KeyCode key)
        {
            if (Rewired.ReInput.isReady && Rewired.ReInput.controllers?.Keyboard != null)
                return Rewired.ReInput.controllers.Keyboard.GetKeyDown(key);
            try { return Input.GetKeyDown(key); }
            catch (System.InvalidOperationException) { return false; }
        }
    }
}
