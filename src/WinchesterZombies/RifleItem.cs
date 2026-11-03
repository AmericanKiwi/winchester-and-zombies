using EvilCore.EvilSave;
using NomadDrive.Features.Inputs;
using NomadDrive.Features.SaveSystem;
using NomadDrive.Features.Interaction;
using NomadDrive.Features.Player.IK;
using NomadDrive.Features.Tools;
using RootMotion.FinalIK;
using UnityEngine;

namespace WinchesterZombies
{
    /// <summary>
    /// The rifle as a game held item. Pickup, hotbar, carrying, dropping and position sync come from the
    /// game's HeldItem base (inherited from the cloned shovel). Firing and reloading go to the host as
    /// mod messages; ammo lives on the host (<see cref="RifleServer"/>).
    ///
    /// While you hold it, the rifle is posed from the camera (like the game's Polaroid camera), so it follows
    /// your view: carried at the hip, raised to the sights while the aim button is held.
    /// </summary>
    public class RifleItem : DirectHeldItem, IHandPoseProvider, INetworkSaveable
    {
        // Saved with the world (the game's save calls these on the host): rounds in the tube and chamber.
        public string ContributorKey => "wz.rifle";

        public void CaptureState(EvilWriter writer, ISaveContext ctx)
        {
            RifleServer.GetLoad(netId, out int tube, out Chamber chamber);
            writer.Write((byte)1);
            writer.Write(tube);
            writer.Write((byte)chamber);
        }

        public void RestoreSelfState(EvilReader reader, ISaveContext ctx)
        {
            reader.ReadByte();
            int tube = reader.ReadInt();
            var chamber = (Chamber)reader.ReadByte();
            RifleServer.SetLoad(netId, tube, chamber);
        }

        public void RestoreLinks(EvilReader reader, ISaveContext ctx) { }

        public RifleVisual Visual;   // serialized so each spawned copy keeps its own visual

        // Hand targets (see RifleFactory.SetUpHands); positions are in the rifle model's space.
        public Transform RightHand;
        public Vector3 RightGripOffset;
        public Quaternion RightGripRotation = Quaternion.identity;
        public InteractionObject SupportGrip;
        public Transform LeftHand;

        private PlayerIKBridge _supportBridge;

        // The visual lies along the item's +Y (the shovel's long axis); this turns that to camera-forward.
        private static readonly Quaternion ItemToView = Quaternion.Euler(90f, 0f, 0f);

        private bool _aimHeld;
        private float _aim;

        public override bool CanBeThrown => false;
        public override bool UsesAltUseInput => true;
        public bool SuppressesHandClipping => true;

        private bool HeldByMe =>
            isOwned && IsEquipped && !IsStowed && playerService?.EquipmentManager?.EquippedEntity == this;

        internal bool HeldInMyHands => HeldByMe;
        internal float Aim => _aim;

        public override Sprite InventoryIconOverride => Assets.RifleIcon;

        public override void OnUseButtonDown()
        {
            base.OnUseButtonDown();
            // A free cursor means a menu is open (the game still passes clicks on its buttons through).
            if (HeldByMe && Cursor.lockState == CursorLockMode.Locked) RifleClient.RequestFire(this);
        }

        public override void OnAltUseButton(bool held)
        {
            base.OnAltUseButton(held);
            _aimHeld = held;
        }

        private void Update()
        {
            PlaceHands();
            UpdateSupportGrip();
            if (!HeldByMe)
            {
                _aimHeld = false;
                _aim = 0f;
                return;
            }
            if (Keys.Down(Plugin.ReloadKey.Value)) RifleClient.RequestReload(this);
            bool aiming = AimInput();
            _aim = Mathf.MoveTowards(_aim, aiming ? 1f : 0f, Time.deltaTime * 6f);
        }

        /// <summary>Right hand round the wrist behind the lever, left hand under the forend.</summary>
        private void PlaceHands()
        {
            if (Visual == null) return;
            var vis = Visual.transform;
            if (RightHand != null)
            {
                Vector3 g = Plugin.RightHandGrip;   // (forward, up, twist about the bore in degrees)
                Quaternion twist = Quaternion.AngleAxis(g.z, Vector3.forward);
                RightHand.position = vis.TransformPoint(new Vector3(0f, g.y, g.x) + twist * RightGripOffset);
                RightHand.rotation = vis.rotation * twist * RightGripRotation;
            }
            if (LeftHand != null)
            {
                LeftHand.position = vis.TransformPoint(Plugin.LeftHandPosition);
                LeftHand.rotation = vis.rotation * Quaternion.Euler(Plugin.LeftHandRotation);
            }
        }

        /// <summary>
        /// The left hand is the game's "support grip" (as the Polaroid camera uses in photo mode): set it on the
        /// holder's IK while the rifle is in hand, for your own player and for other players you see.
        /// </summary>
        private void UpdateSupportGrip()
        {
            // While you aim, the rifle is so close to your eye that the left forearm would fill the view, so your
            // own left hand lets go (other players still see both hands).
            bool aimingMine = HeldByMe && _aim > 0.3f;
            PlayerIKBridge bridge = SupportGrip != null && IsEquipped && !IsStowed && !aimingMine ? HolderBridge() : null;
            if (bridge == _supportBridge) return;
            if (_supportBridge != null) _supportBridge.SetSupportGrip(null);
            _supportBridge = bridge;
            if (bridge != null) bridge.SetSupportGrip(SupportGrip);
        }

        private PlayerIKBridge HolderBridge()
        {
            if (IsHeldByLocalPlayer)
                return playerService?.LocalPlayer != null ? playerService.LocalPlayer.GetComponentInChildren<PlayerIKBridge>() : null;
            if (NetworkedTransform == null || networkManager == null) return null;
            var parent = NetworkedTransform.SyncedParentData;
            if (!parent.HasParent || !networkManager.TryGetNetworkObjectById(parent.ParentNetworkedTransformNetId, out var holder))
                return null;
            return holder.GetComponentInChildren<PlayerIKBridge>();
        }

        protected override void OnDisable()
        {
            if (_supportBridge != null) _supportBridge.SetSupportGrip(null);
            _supportBridge = null;
            base.OnDisable();
        }

        /// <summary>
        /// The game's alt-use action isn't bound to right mouse in play, so OnAltUseButton never fires for it.
        /// Read the button directly, but leave it to whatever you're looking at if that wants the press.
        /// </summary>
        private bool AimInput()
        {
            var alt = AltUseInputType;
            bool pressed = EquippingInputs.IsUseObjectButton(alt) ||
                (Rewired.ReInput.isReady && Rewired.ReInput.controllers?.Mouse != null && Rewired.ReInput.controllers.Mouse.GetButton(1));
            if (!pressed) return _aimHeld;
            bool claimed = playerService != null && playerService.TryGetInteractionManager(out var im) &&
                HeldUsePressGate.IsKeyClaimedByTarget(alt, im);
            return _aimHeld || !claimed;
        }

        /// <summary>Where the item root goes, in camera space, to put the rifle's receiver at <paramref name="view"/>.</summary>
        private Vector3 ItemOffsetFor(Vector3 view) =>
            view - ItemToView * (Visual != null ? Visual.transform.localPosition : Vector3.zero);

        public bool TryGetHandPose(Transform space, Vector3 basePosition, Quaternion baseRotation,
            out Vector3 localPosition, out Quaternion localRotation, out float weight)
        {
            localPosition = basePosition;
            localRotation = baseRotation;
            weight = 0f;
            if (!isOwned || space == null) return false;   // other players' rifles use the shared carry pose
            Transform cam = playerService?.CameraTransform;
            if (cam == null) return false;

            float a = Mathf.SmoothStep(0f, 1f, _aim);
            Vector3 view = Vector3.Lerp(Plugin.HipOffset, Plugin.AimOffset, a);
            Quaternion camLocal = Quaternion.Inverse(space.rotation) * cam.rotation;
            localPosition = space.InverseTransformPoint(cam.position) + camLocal * ItemOffsetFor(view);
            localRotation = camLocal * ItemToView;
            weight = 1f;

            // Everyone's carry pose (what other players see) is the same hip pose with a level camera.
            Quaternion levelLocal = Quaternion.Inverse(space.rotation) * Quaternion.Euler(0f, cam.eulerAngles.y, 0f);
            RifleFactory.SetSharedCarryPose(
                space.InverseTransformPoint(cam.position) + levelLocal * ItemOffsetFor(Plugin.HipOffset),
                (levelLocal * ItemToView).eulerAngles);
            return true;
        }

        public override bool Weaved() => true;
    }
}
