using UnityEditor;
using UnityEngine;

/// <summary>
/// The .44-40 ammo boxes from the 3D Assets project (Models/Props/AmmoBoxes1873), rebuilt here from the same
/// measurements and colours. Their labels were 3D Text, which HDRP can't draw, so each label (paper plus
/// printing) is rendered once into a texture and put on a flat card on the lid.
/// </summary>
public static class AmmoBoxes
{
    // Sizes and offsets copied from AmmoBox1873_Green.prefab (metres; the box sits on y = 0).
    private static readonly Vector3 CartonPos = new Vector3(0f, 0.018f, 0f), CartonSize = new Vector3(0.115f, 0.036f, 0.065f);
    private static readonly Vector3 LidPos = new Vector3(0f, 0.037f, 0f), LidSize = new Vector3(0.116f, 0.003f, 0.066f);
    private static readonly Vector3 SeamPos = new Vector3(0f, 0.034f, 0f), SeamSize = new Vector3(0.1155f, 0.0008f, 0.0655f);
    private const float LabelY = 0.0388f, LabelW = 0.098f, LabelD = 0.051f;

    private static readonly Color Green = new Color(0.12f, 0.23f, 0.16f);
    private static readonly Color Kraft = new Color(0.48f, 0.31f, 0.16f);
    private static readonly Color Seam = new Color(0.22f, 0.14f, 0.075f);
    private static readonly Color Paper = new Color(0.86f, 0.78f, 0.58f);
    private static readonly Color Ink = new Color32(31, 24, 14, 255);

    /// <summary>Builds WZ_AmmoBox_Green and WZ_AmmoBox_Kraft; returns their asset paths.</summary>
    public static string[] Build(string gen, System.Func<string, Color, float, Texture2D, Material> mat)
    {
        var label = BakeLabel(gen);
        var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        var card = LabelCard(gen);
        var labelMat = mat("M_AmmoLabel", Color.white, 0.12f, label);
        var seamMat = mat("M_AmmoSeam", Seam, 0.08f, null);
        return new[]
        {
            Box(gen, "WZ_AmmoBox_Green", mat("M_AmmoGreen", Green, 0.08f, null), seamMat, labelMat, cube, card),
            Box(gen, "WZ_AmmoBox_Kraft", mat("M_AmmoKraft", Kraft, 0.08f, null), seamMat, labelMat, cube, card),
        };
    }

    private static string Box(string gen, string name, Material body, Material seam, Material label, Mesh cube, Mesh card)
    {
        var root = new GameObject(name);
        try
        {
            void Part(string n, Mesh mesh, Material m, Vector3 pos, Vector3 scale)
            {
                var go = new GameObject(n);
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = pos;
                go.transform.localScale = scale;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = m;
            }
            Part("Carton", cube, body, CartonPos, CartonSize);
            Part("Lid", cube, body, LidPos, LidSize);
            Part("LidSeam", cube, seam, SeamPos, SeamSize);
            Part("Label", card, label, new Vector3(0f, LabelY, 0f), Vector3.one);
            string path = $"{gen}/{name}.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            return path;
        }
        finally { Object.DestroyImmediate(root); }
    }

    /// <summary>A flat card the size of the paper label, facing up, with the label texture across it.</summary>
    private static Mesh LabelCard(string gen)
    {
        string path = gen + "/AmmoLabelCard.asset";
        AssetDatabase.DeleteAsset(path);
        float x = LabelW / 2f, z = LabelD / 2f;
        var mesh = new Mesh
        {
            name = "AmmoLabelCard",
            vertices = new[] { new Vector3(-x, 0f, -z), new Vector3(-x, 0f, z), new Vector3(x, 0f, z), new Vector3(x, 0f, -z) },
            uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) },
            normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up },
            triangles = new[] { 0, 1, 2, 0, 2, 3 },
        };
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }

    /// <summary>
    /// Renders the three lines of print on the paper colour, the way the original prefab lays them out
    /// (Arial, centred, at the same sizes and spacing), looking straight down at the lid.
    /// </summary>
    private static Texture2D BakeLabel(string gen)
    {
        const int W = 1024, H = 533;   // label aspect 0.098 : 0.051
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var rig = new GameObject("LabelBake");
        var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
        try
        {
            rig.transform.position = new Vector3(0f, -500f, 0f);
            const int layer = 31;
            void Line(string text, float size, float z)
            {
                var go = new GameObject(text) { layer = layer };
                go.transform.SetParent(rig.transform, false);
                go.transform.localPosition = new Vector3(0f, 0f, z);
                go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);   // lies flat, reads from above
                var tm = go.AddComponent<TextMesh>();
                tm.font = font;
                tm.text = text;
                tm.fontSize = 64;
                tm.characterSize = size;
                tm.anchor = TextAnchor.MiddleCenter;
                tm.alignment = TextAlignment.Center;
                tm.color = Ink;
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterial = new Material(font.material) { color = Ink };
            }
            Line("MODEL 1873", 0.0013f, 0.012f);
            Line(".44-40", 0.002f, 0f);
            Line("RIFLE CARTRIDGES", 0.00085f, -0.013f);

            var camGo = new GameObject("LabelCam");
            camGo.transform.SetParent(rig.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 0.5f, 0f);
            camGo.transform.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = LabelD / 2f;
            cam.aspect = (float)W / H;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 1f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Paper;
            cam.cullingMask = 1 << layer;
            cam.targetTexture = rt;
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, true);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            string path = gen + "/T_AmmoLabel.png";
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.sRGBTexture = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
        finally
        {
            Object.DestroyImmediate(rig);
            rt.Release();
            Object.DestroyImmediate(rt);
        }
    }
}
