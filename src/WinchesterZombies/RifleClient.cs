using System.Collections.Generic;
using Mirror;
using NomadDrive.Features.FloatingOrigin;
using UnityEngine;

namespace WinchesterZombies
{
    /// <summary>Client side: sends fire/reload requests and plays what the host reports on every rifle.</summary>
    internal static class RifleClient
    {
        public static readonly Dictionary<uint, RifleStateMsg> States = new Dictionary<uint, RifleStateMsg>();

        public static void RequestFire(RifleItem rifle)
        {
            if (!Net.HostHasMod) { Notice.Show("The host doesn't have Winchester & Zombies installed."); return; }
            var cam = Camera.main;
            if (cam == null) return;
            NetworkClient.Send(new RifleFireMsg
            {
                NetId = rifle.netId,
                Origin = FloatingOriginManager.ToTrueWorld(cam.transform.position),
                Direction = cam.transform.forward,
            });
        }

        public static void RequestReload(RifleItem rifle)
        {
            if (!Net.HostHasMod) return;
            if (AmmoClient.Pocket == 0)
            {
                Notice.Show("No rounds in your pocket. Rifle in hand, look at an ammo box and hold E.");
                return;
            }
            NetworkClient.Send(new RifleReloadMsg { NetId = rifle.netId });
        }

        public static void OnAction(RifleActionMsg msg)
        {
            var rifle = Find(msg.NetId);
            if (rifle != null && rifle.Visual != null) rifle.Visual.Play((RifleAction)msg.Action);
        }

        public static void OnState(RifleStateMsg msg) => States[msg.NetId] = msg;

        private static RifleItem Find(uint netId) =>
            NetworkClient.spawned.TryGetValue(netId, out var identity) ? identity.GetComponent<RifleItem>() : null;
    }
}
