using NomadDrive.Features.FloatingOrigin;
using UnityEngine;

namespace WinchesterZombies
{
    internal static class Effects
    {
        /// <summary>Visual-only brass thrown up out of the receiver (each client simulates its own).</summary>
        public static void EjectBrass(GameObject prefab, Transform port)
        {
            if (prefab == null || port == null) return;
            var go = Object.Instantiate(prefab, port.position, port.rotation * Quaternion.Euler(-60f, 0f, 0f));
            Assets.ApplyHdrp(go);
            var col = go.AddComponent<CapsuleCollider>();
            col.direction = 2;
            col.radius = 0.0067f;
            col.height = 0.034f;
            col.center = new Vector3(0f, 0f, 0.017f);
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.015f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            foreach (var own in port.root.GetComponentsInChildren<Collider>()) Physics.IgnoreCollision(col, own);
            Vector3 dir = port.up - port.forward * 0.3f + port.right * 0.08f + Random.insideUnitSphere * 0.1f;
            rb.linearVelocity = dir.normalized * 2.6f;
            rb.angularVelocity = port.right * Random.Range(-30f, -18f) + Random.insideUnitSphere * 4f;
            var root = FloatingOriginManager.Instance != null ? FloatingOriginManager.Instance.WorldContentRoot : null;
            if (root != null) go.transform.SetParent(root, true);
            Object.Destroy(go, 15f);
        }

        /// <summary>Dust puff where a shot landed (the prefab plays on spawn and removes itself).</summary>
        public static void OnImpact(ImpactMsg msg)
        {
            if (Assets.Impact == null) return;
            var go = Object.Instantiate(Assets.Impact, FloatingOriginManager.ToRenderWorld(msg.Point), Quaternion.LookRotation(msg.Normal));
            Assets.ApplyHdrp(go);
            Object.Destroy(go, 3f);
        }
    }

    /// <summary>Placeholder sounds generated at runtime (same synth as the Unity asset project).</summary>
    internal static class Sounds
    {
        private const int Rate = 44100;
        private const float TwoPi = 6.2831853f;   // Mathf.PI is stripped from the game's build
        private static AudioClip _shot, _dry, _open, _close, _insert, _thud;

        public static AudioClip Shot => _shot != null ? _shot : _shot = MakeShot();
        public static AudioClip DryFire => _dry != null ? _dry : _dry = Mech("WZ_Dry", 0.12f, 1, (0f, 2600f, 1f));
        public static AudioClip LeverOpen => _open != null ? _open : _open = Mech("WZ_Open", 0.25f, 2, (0f, 2200f, 0.8f), (0.17f, 1700f, 1f));
        public static AudioClip LeverClose => _close != null ? _close : _close = Mech("WZ_Close", 0.2f, 3, (0.02f, 1900f, 0.8f), (0.07f, 2800f, 1f));
        public static AudioClip Insert => _insert != null ? _insert : _insert = Mech("WZ_Insert", 0.15f, 4, (0f, 1400f, 0.6f), (0.05f, 2100f, 0.9f));
        public static AudioClip Thud => _thud != null ? _thud : _thud = MakeThud();

        /// <summary>Body against metal: a low thump with a dull, quickly damped crunch on top.</summary>
        private static AudioClip MakeThud()
        {
            var rng = new System.Random(44);
            var d = new float[(int)(0.45f * Rate)];
            float lp = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float t = (float)i / Rate, n = (float)(rng.NextDouble() * 2.0 - 1.0);
                lp += (n - lp) * 0.06f;
                float pitch = 55f + 40f * Mathf.Exp(-t * 30f);
                d[i] = Mathf.Sin(TwoPi * pitch * t) * Mathf.Exp(-t * 14f) + lp * 4f * Mathf.Exp(-t * 35f);
            }
            return Finish("WZ_Thud", d);
        }

        /// <summary>One-shot 3D sound at a point (a throwaway source that removes itself).</summary>
        public static void PlayAt(AudioClip clip, Vector3 point, float volume)
        {
            if (clip == null) return;
            var go = new GameObject("WZ_Sound");
            go.transform.position = point;
            var src = go.AddComponent<AudioSource>();
            src.spatialBlend = 1f;
            src.minDistance = 3f;
            src.maxDistance = 80f;
            src.pitch = Random.Range(0.9f, 1.1f);
            src.PlayOneShot(clip, volume);
            Object.Destroy(go, clip.length + 0.2f);
        }

        private static AudioClip MakeShot()
        {
            var rng = new System.Random(1873);
            var d = new float[(int)(0.9f * Rate)];
            float lp = 0f, lp2 = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float t = (float)i / Rate, n = (float)(rng.NextDouble() * 2.0 - 1.0);
                lp += (n - lp) * 0.18f;
                lp2 += (lp - lp2) * 0.12f;
                d[i] = n * Mathf.Exp(-t * 90f) * 0.5f + lp * Mathf.Exp(-t * 12f) * 1.4f + lp2 * 3f * Mathf.Exp(-t * 5f) * 0.9f;
            }
            return Finish("WZ_Shot", d);
        }

        private static AudioClip Mech(string name, float seconds, int seed, params (float at, float freq, float amp)[] clicks)
        {
            var rng = new System.Random(seed);
            var d = new float[(int)(seconds * Rate)];
            foreach (var (at, freq, amp) in clicks)
            {
                int start = (int)(at * Rate);
                for (int i = start; i < d.Length; i++)
                {
                    float t = (float)(i - start) / Rate, n = (float)(rng.NextDouble() * 2.0 - 1.0);
                    d[i] += amp * (n * Mathf.Exp(-t * 400f) * 0.7f + Mathf.Sin(TwoPi * freq * t) * Mathf.Exp(-t * 90f) * 0.35f);
                }
            }
            return Finish(name, d);
        }

        private static AudioClip Finish(string name, float[] d)
        {
            float peak = 0.0001f;
            foreach (float s in d) peak = Mathf.Max(peak, Mathf.Abs(s));
            for (int i = 0; i < d.Length; i++) d[i] = d[i] / peak * 0.9f;
            var clip = AudioClip.Create(name, d.Length, 1, Rate, false);
            clip.SetData(d, 0);
            return clip;
        }
    }
}
