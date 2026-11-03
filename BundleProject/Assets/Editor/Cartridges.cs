using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// .44-40 WCF live round and fired case as turned meshes from real profile dimensions (same as the
/// CartridgeBuilder in the 3D Assets project). Origin at the case head, +Z toward the nose, metres.
/// </summary>
public static class Cartridges
{
    private const int Sides = 24;
    private const float SmoothAngle = 35f;

    private const float RimR = 0.00667f, HeadR = 0.00598f, ShoulderR = 0.00580f, NeckR = 0.00563f, BulletR = 0.00542f;
    private const float RimT = 0.00165f, ShoulderZ = 0.0262f, NeckZ = 0.0278f, CaseLen = 0.0331f, Overall = 0.0404f;
    private const float PrimerR = 0.0024f;

    public static (string live, string spent) Build(string dir, Material brass, Material fired, Material lead, Material copper)
    {
        var outside = new List<Vector2>
        {
            new Vector2(0f, 0f), new Vector2(RimR - 0.0004f, 0f), new Vector2(RimR, 0.0004f), new Vector2(RimR, RimT),
            new Vector2(HeadR, RimT + 0.0003f), new Vector2(ShoulderR, ShoulderZ), new Vector2(NeckR, NeckZ), new Vector2(NeckR, CaseLen),
        };
        var live = new List<Vector2>(outside) { new Vector2(BulletR, CaseLen) };
        var spent = new List<Vector2>(outside)
        {
            new Vector2(NeckR - 0.0004f, CaseLen), new Vector2(NeckR - 0.0004f, 0.004f), new Vector2(0f, 0.004f),
        };
        var bullet = new List<Vector2>
        {
            new Vector2(BulletR - 0.0002f, CaseLen), new Vector2(BulletR - 0.0002f, CaseLen + 0.0006f), new Vector2(BulletR, CaseLen + 0.0009f),
            new Vector2(BulletR * 0.97f, 0.0350f), new Vector2(BulletR * 0.88f, 0.0367f), new Vector2(BulletR * 0.74f, 0.0382f),
            new Vector2(BulletR * 0.55f, 0.0395f), new Vector2(BulletR * 0.42f, Overall), new Vector2(0f, Overall),
        };
        var primer = new List<Vector2> { new Vector2(0f, -0.00005f), new Vector2(PrimerR - 0.0002f, -0.00005f), new Vector2(PrimerR, 0f) };
        var struck = new List<Vector2>
        {
            new Vector2(0f, 0.00035f), new Vector2(0.0007f, 0.00025f), new Vector2(0.0009f, -0.00005f),
            new Vector2(PrimerR - 0.0002f, -0.00005f), new Vector2(PrimerR, 0f),
        };

        var caseLive = Save(dir, Lathe("Case_Live", live));
        var caseSpent = Save(dir, Lathe("Case_Spent", spent));
        var bulletMesh = Save(dir, Lathe("Bullet", bullet));
        var primerMesh = Save(dir, Lathe("Primer", primer));
        var struckMesh = Save(dir, Lathe("Primer_Struck", struck));

        string livePath = Prefab(dir + "/WZ_Cartridge.prefab", ("Case", caseLive, brass), ("Bullet", bulletMesh, lead), ("Primer", primerMesh, copper));
        string spentPath = Prefab(dir + "/WZ_SpentCase.prefab", ("Case", caseSpent, fired), ("Primer", struckMesh, copper));
        return (livePath, spentPath);
    }

    private static Mesh Lathe(string name, List<Vector2> profile)
    {
        int segs = profile.Count - 1;
        var segNormal = new Vector2[segs];
        for (int i = 0; i < segs; i++)
        {
            Vector2 d = profile[i + 1] - profile[i];
            segNormal[i] = new Vector2(d.y, -d.x).normalized;
        }
        Vector2 Joint(int s, bool start)
        {
            int o = start ? s - 1 : s + 1;
            if (o < 0 || o >= segs || Vector2.Angle(segNormal[s], segNormal[o]) > SmoothAngle) return segNormal[s];
            return (segNormal[s] + segNormal[o]).normalized;
        }

        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        float length = Mathf.Max(0.0001f, profile[profile.Count - 1].y - profile[0].y);
        for (int s = 0; s < segs; s++)
        {
            Vector2 p0 = profile[s], p1 = profile[s + 1];
            if ((p1 - p0).sqrMagnitude < 1e-12f) continue;
            Vector2 n0 = Joint(s, true), n1 = Joint(s, false);
            int b = verts.Count;
            for (int k = 0; k <= Sides; k++)
            {
                float a = k * Mathf.PI * 2f / Sides, c = Mathf.Cos(a), sn = Mathf.Sin(a);
                verts.Add(new Vector3(p0.x * c, p0.x * sn, p0.y)); norms.Add(new Vector3(n0.x * c, n0.x * sn, n0.y).normalized); uvs.Add(new Vector2((float)k / Sides, p0.y / length));
                verts.Add(new Vector3(p1.x * c, p1.x * sn, p1.y)); norms.Add(new Vector3(n1.x * c, n1.x * sn, n1.y).normalized); uvs.Add(new Vector2((float)k / Sides, p1.y / length));
            }
            for (int k = 0; k < Sides; k++)
            {
                int a0 = b + k * 2, a1 = a0 + 1, b0 = a0 + 2, b1 = a0 + 3;
                Tri(tris, verts, norms, a0, a1, b0);
                Tri(tris, verts, norms, b0, a1, b1);
            }
        }
        var mesh = new Mesh { name = name };
        mesh.SetVertices(verts); mesh.SetNormals(norms); mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds(); mesh.RecalculateTangents();
        return mesh;
    }

    private static void Tri(List<int> tris, List<Vector3> v, List<Vector3> n, int a, int b, int c)
    {
        Vector3 face = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
        if (face.sqrMagnitude < 1e-16f) return;
        if (Vector3.Dot(face, n[a] + n[b] + n[c]) < 0f) (b, c) = (c, b);
        tris.Add(a); tris.Add(b); tris.Add(c);
    }

    private static Mesh Save(string dir, Mesh mesh)
    {
        string path = $"{dir}/{mesh.name}.asset";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }

    private static string Prefab(string path, params (string name, Mesh mesh, Material mat)[] pieces)
    {
        var root = new GameObject(System.IO.Path.GetFileNameWithoutExtension(path));
        try
        {
            foreach (var (name, mesh, mat) in pieces)
            {
                var go = new GameObject(name);
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            }
            PrefabUtility.SaveAsPrefabAsset(root, path);
            return path;
        }
        finally { Object.DestroyImmediate(root); }
    }
}
