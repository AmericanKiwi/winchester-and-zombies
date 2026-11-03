using Mirror;
using NomadDrive.Features.Player;
using UnityEngine;

namespace WinchesterZombies
{
    internal static class LocalPlayer
    {
        public static Player Get()
        {
            var id = NetworkClient.localPlayer;
            return id != null ? id.GetComponent<Player>() : null;
        }

        /// <summary>Damage to this machine's player, the same way the game's own hazards do it.</summary>
        public static void Damage(float amount)
        {
            var player = Get();
            var stats = player != null ? player.GetComponent<PlayerStatsManager>() : null;
            if (stats != null) stats.ApplyDirectDamage(amount);
        }
    }

    /// <summary>Short on-screen messages and the ammo readout while holding the rifle.</summary>
    internal class Hud : MonoBehaviour
    {
        private static string _text;
        private static float _until;
        private GUIStyle _style;

        public static void Show(string text, float seconds = 4f)
        {
            _text = text;
            _until = Time.unscaledTime + seconds;
            Plugin.Log.LogInfo(text);
        }

        private void OnGUI()
        {
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.MiddleCenter };
                _style.normal.textColor = Color.white;
            }

            if (_text != null && Time.unscaledTime < _until)
                GUI.Label(new Rect(0, Screen.height * 0.18f, Screen.width, 30), _text, _style);

            var held = HeldRifle();
            if (held != null && RifleClient.States.TryGetValue(held.netId, out var s))
            {
                int loaded = s.Tube + (s.Chamber == (byte)Chamber.Live ? 1 : 0);
                string pocket = AmmoClient.Pocket < 0 ? "inf" : $"{AmmoClient.Pocket}/{AmmoClient.PocketCapacity}";
                GUI.Label(new Rect(0, Screen.height - 90, Screen.width - 40, 30),
                    $".44-40   {loaded} / {s.Reserve + 1}   pocket {pocket}   [{Plugin.ReloadKey.Value}] reload",
                    new GUIStyle(_style) { alignment = TextAnchor.MiddleRight });
            }
        }

        private static RifleItem HeldRifle()
        {
            foreach (var r in FindObjectsByType<RifleItem>(FindObjectsSortMode.None))
                if (r.HeldInMyHands) return r;
            return null;
        }
    }

    internal static class Notice
    {
        public static void Show(string text) => Hud.Show(text);
    }
}
