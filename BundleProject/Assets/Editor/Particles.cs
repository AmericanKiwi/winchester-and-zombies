using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>Muzzle smoke and impact dust, authored as serialized particle systems (the game build can't set them up in code).</summary>
public static class Particles
{
    public static Texture2D SoftTexture(string dir)
    {
        string path = dir + "/T_Soft.png";
        if (!File.Exists(path))
        {
            var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                float a = Mathf.Clamp01(1f - new Vector2(x - 31.5f, y - 31.5f).magnitude / 32f);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a * (3f - 2f * a)));
            }
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.alphaIsTransparency = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    /// <summary>
    /// Blood splatter decal texture: a dense central pool, satellite drops and a few streaks thrown in one
    /// direction, dark red with a soft wet edge. Deterministic per seed so builds match.
    /// </summary>
    public static Texture2D BloodTexture(string dir, int seed)
    {
        string path = $"{dir}/T_Blood{seed}.png";
        if (!File.Exists(path))
        {
            const int S = 256;
            var rng = new System.Random(seed * 7919);
            float R() => (float)rng.NextDouble();
            var field = new float[S, S];
            void Blob(float cx, float cy, float r, float strength)
            {
                int x0 = Mathf.Max(0, (int)(cx - r - 2)), x1 = Mathf.Min(S - 1, (int)(cx + r + 2));
                int y0 = Mathf.Max(0, (int)(cy - r - 2)), y1 = Mathf.Min(S - 1, (int)(cy + r + 2));
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / r;
                    if (d < 1f) field[x, y] = Mathf.Max(field[x, y], strength * (1f - d * d));
                }
            }

            float c = S * 0.5f;
            // Central pool: overlapping lobes so the outline is irregular.
            for (int i = 0; i < 14; i++)
            {
                float a = R() * 6.2831853f, d = R() * S * 0.12f;
                Blob(c + Mathf.Cos(a) * d, c + Mathf.Sin(a) * d, S * (0.07f + R() * 0.09f), 1f);
            }
            // Streaks flung mostly one way, tapering into droplets.
            float throwDir = R() * 6.2831853f;
            for (int s = 0; s < 6; s++)
            {
                float a = throwDir + (R() - 0.5f) * 1.4f, len = S * (0.18f + R() * 0.25f), w = S * (0.012f + R() * 0.02f);
                for (float t = 0f; t < 1f; t += 0.02f)
                    Blob(c + Mathf.Cos(a) * len * t, c + Mathf.Sin(a) * len * t, w * (1f - t * 0.7f), 0.9f);
                Blob(c + Mathf.Cos(a) * len * 1.04f, c + Mathf.Sin(a) * len * 1.04f, w * 0.9f, 0.9f);
            }
            // Satellite drops.
            for (int i = 0; i < 40; i++)
            {
                float a = R() * 6.2831853f, d = S * (0.15f + R() * 0.32f);
                Blob(c + Mathf.Cos(a) * d, c + Mathf.Sin(a) * d, S * (0.004f + R() * 0.014f), 0.85f);
            }

            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float v = field[x, y];
                float alpha = Mathf.Clamp01((v - 0.04f) * 16f);           // crisp wet edge
                float depth = Mathf.Clamp01(v * 1.3f);                      // thicker centre is darker
                var col = Color.Lerp(new Color(0.42f, 0.03f, 0.02f), new Color(0.18f, 0.01f, 0.01f), depth);
                col.a = alpha * 0.95f;
                tex.SetPixel(x, y, col);
            }
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.alphaIsTransparency = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    /// <summary>Short burst of blood droplets that fall and fade.</summary>
    public static string BloodSprayPrefab(string dir, Material mat)
    {
        var ps = New("WZ_BloodSpray", null, mat);
        try
        {
            var main = ps.main;
            main.duration = 0.15f;
            main.loop = false;
            main.playOnAwake = true;
            main.stopAction = ParticleSystemStopAction.Destroy;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.4f, 0.9f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f, 4.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.09f);
            main.gravityModifier = 1.4f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            Burst(ps, 24);
            Cone(ps, 40f, 0.04f);
            Grow(ps, 0.6f);
            string path = dir + "/WZ_BloodSpray.prefab";
            PrefabUtility.SaveAsPrefabAsset(ps.gameObject, path);
            return path;
        }
        finally { Object.DestroyImmediate(ps.gameObject); }
    }

    public static void MuzzleSmoke(Transform muzzle, Material mat)
    {
        var ps = New("MuzzleSmoke", muzzle, mat);
        var main = ps.main;
        main.duration = 0.3f;
        main.loop = false;
        main.playOnAwake = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.3f, 1f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.12f);
        main.startColor = new Color(0.85f, 0.85f, 0.85f, 0.35f);
        main.gravityModifier = -0.03f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        Burst(ps, 8);
        Cone(ps, 20f, 0.01f);
        Grow(ps, 4f);
        Fade(ps);
    }

    public static string ImpactPrefab(string dir, Material mat)
    {
        var ps = New("WZ_Impact", null, mat);
        try
        {
            var main = ps.main;
            main.duration = 0.2f;
            main.loop = false;
            main.playOnAwake = true;
            main.stopAction = ParticleSystemStopAction.Destroy;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1f, 3f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.08f);
            main.startColor = new Color(0.55f, 0.5f, 0.42f, 0.8f);
            main.gravityModifier = 1.2f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            Burst(ps, 14);
            Cone(ps, 35f, 0.02f);
            Grow(ps, 2f);
            Fade(ps);
            string path = dir + "/WZ_Impact.prefab";
            PrefabUtility.SaveAsPrefabAsset(ps.gameObject, path);
            return path;
        }
        finally { Object.DestroyImmediate(ps.gameObject); }
    }

    private static ParticleSystem New(string name, Transform parent, Material mat)
    {
        var go = new GameObject(name);
        if (parent != null) go.transform.SetParent(parent, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return ps;
    }

    private static void Burst(ParticleSystem ps, short count)
    {
        var e = ps.emission;
        e.rateOverTime = 0f;
        e.SetBursts(new[] { new ParticleSystem.Burst(0f, count) });
    }

    private static void Cone(ParticleSystem ps, float angle, float radius)
    {
        var s = ps.shape;
        s.shapeType = ParticleSystemShapeType.Cone;
        s.angle = angle;
        s.radius = radius;
    }

    private static void Grow(ParticleSystem ps, float to)
    {
        var s = ps.sizeOverLifetime;
        s.enabled = true;
        s.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, to));
    }

    private static void Fade(ParticleSystem ps)
    {
        var c = ps.colorOverLifetime;
        c.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
        c.color = g;
    }
}
