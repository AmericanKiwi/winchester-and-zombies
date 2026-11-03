using UnityEditor;
using UnityEngine;

/// <summary>Hotbar icons, rendered from the mod's own prefabs on a transparent background.</summary>
public static class Icons
{
    private const int Size = 256;

    /// <summary>Renders <paramref name="prefabPath"/> turned by <paramref name="turn"/>, seen from <paramref name="viewDir"/>; returns the sprite's path.</summary>
    public static string Render(string prefabPath, string outPath, Quaternion turn, Vector3 viewDir, float margin = 1.08f)
    {
        var rig = new GameObject("IconRig");
        var rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
        try
        {
            rig.transform.position = new Vector3(0f, -1000f, 0f);
            const int layer = 30;
            var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), rig.transform);
            model.transform.localRotation = turn;
            foreach (var t in model.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
            foreach (var ps in model.GetComponentsInChildren<ParticleSystem>(true)) ps.gameObject.SetActive(false);

            Bounds? b = null;
            foreach (var r in model.GetComponentsInChildren<Renderer>())
            {
                if (!r.gameObject.activeInHierarchy || r is ParticleSystemRenderer) continue;
                if (b == null) b = r.bounds; else { var bb = b.Value; bb.Encapsulate(r.bounds); b = bb; }
            }
            var bounds = b ?? new Bounds(rig.transform.position, Vector3.one);

            var light = new GameObject("Key").AddComponent<Light>();
            light.transform.SetParent(rig.transform, false);
            light.type = LightType.Directional;
            light.intensity = 1.25f;
            light.cullingMask = 1 << layer;
            light.transform.rotation = Quaternion.LookRotation(new Vector3(-0.4f, -0.7f, 0.6f));
            var fill = new GameObject("Fill").AddComponent<Light>();
            fill.transform.SetParent(rig.transform, false);
            fill.type = LightType.Directional;
            fill.intensity = 0.55f;
            fill.cullingMask = 1 << layer;
            fill.transform.rotation = Quaternion.LookRotation(new Vector3(0.6f, 0.2f, -0.5f));
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.45f);

            var cam = new GameObject("IconCam").AddComponent<Camera>();
            cam.transform.SetParent(rig.transform, false);
            Vector3 dir = viewDir.normalized;
            cam.transform.position = bounds.center - dir * 5f;
            cam.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
            cam.orthographic = true;
            // Fit the bounds' corners as the camera sees them.
            float half = 0f;
            for (int i = 0; i < 8; i++)
            {
                var corner = bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var local = cam.transform.InverseTransformPoint(corner);
                half = Mathf.Max(half, Mathf.Abs(local.x), Mathf.Abs(local.y));
            }
            cam.orthographicSize = half * margin;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 20f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.cullingMask = 1 << layer;
            cam.targetTexture = rt;
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            System.IO.File.WriteAllBytes(outPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(outPath);
            var ti = (TextureImporter)AssetImporter.GetAtPath(outPath);
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = false;
            ti.SaveAndReimport();
            return outPath;
        }
        finally
        {
            Object.DestroyImmediate(rig);
            rt.Release();
            Object.DestroyImmediate(rt);
        }
    }
}
