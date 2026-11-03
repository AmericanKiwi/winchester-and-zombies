using UnityEngine;

namespace WinchesterZombies
{
    /// <summary>
    /// Plays the rifle's animation and effects on every client: lever/trigger/reload clips via the Animator,
    /// plus the parts the clips don't own (hammer, recoil kick, muzzle flash and smoke, ejected brass, sound).
    /// </summary>
    public class RifleVisual : MonoBehaviour
    {
        [SerializeField] private Animator _animator;
        [SerializeField] private Transform _recoil;
        [SerializeField] private Transform _hammer;
        [SerializeField] private Transform _muzzle;
        [SerializeField] private Transform _ejection;
        [SerializeField] private Light _flash;
        [SerializeField] private ParticleSystem _smoke;
        [SerializeField] private AudioSource _audio;

        private const float HammerCocked = -30f;
        private float _hammerAngle = HammerCocked, _hammerTarget = HammerCocked, _hammerSpeed = 2500f;
        private float _kick;
        private float _flashOffAt;
        private Quaternion _hammerBase = Quaternion.identity;
        private Vector3 _recoilBase;

        /// <summary>Called once on the prefab; the references survive cloning.</summary>
        public void Init(Transform visualRoot)
        {
            _recoil = visualRoot.Find("Recoil");
            _animator = _recoil.GetComponent<Animator>();
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _recoil.gameObject.AddComponent<RifleAnimEvents>();
            var art = _recoil.Find("Winchester1873");
            _hammer = art.Find("HammerPivot");
            _muzzle = art.Find("Attachments/Muzzle");
            _ejection = art.Find("Attachments/Ejection");

            var lightGo = new GameObject("MuzzleFlash");
            lightGo.transform.SetParent(_muzzle, false);
            _flash = lightGo.AddComponent<Light>();
            _flash.type = LightType.Point;
            _flash.color = new Color(1f, 0.72f, 0.4f);
            _flash.range = 8f;
            _flash.intensity = 60000f;   // HDRP physical units (lumens)
            _flash.enabled = false;

            _smoke = _muzzle.Find("MuzzleSmoke")?.GetComponent<ParticleSystem>();
            _audio = gameObject.AddComponent<AudioSource>();
            _audio.spatialBlend = 1f;
            _audio.minDistance = 2f;
            _audio.maxDistance = 120f;
            _audio.playOnAwake = false;
        }

        private void Awake()
        {
            if (_hammer != null) _hammerBase = _hammer.localRotation;
            if (_recoil != null) _recoilBase = _recoil.localPosition;
        }

        public void Play(RifleAction action)
        {
            switch (action)
            {
                case RifleAction.Fire:
                    _animator.Play("Fire", 0, 0f);
                    SetHammer(0f, 2500f);
                    _kick = 1f;
                    _flash.enabled = true;
                    _flashOffAt = Time.time + 0.05f;
                    if (_smoke != null) _smoke.Play(true);
                    PlaySound(Sounds.Shot, 1f);
                    break;
                case RifleAction.DryFire:
                    _animator.Play("Fire", 0, 0f);
                    SetHammer(0f, 2500f);
                    PlaySound(Sounds.DryFire, 0.8f);
                    break;
                case RifleAction.Cycle:
                    _animator.Play("Cycle", 0, 0f);
                    SetHammer(HammerCocked, 200f);
                    PlaySound(Sounds.LeverOpen, 0.8f);
                    break;
                case RifleAction.ReloadStart: _animator.Play("ReloadStart", 0, 0f); break;
                case RifleAction.ReloadInsert: _animator.Play("ReloadInsert", 0, 0f); break;
                case RifleAction.ReloadEnd: _animator.Play("ReloadEnd", 0, 0f); break;
            }
        }

        private void SetHammer(float angle, float speed)
        {
            _hammerTarget = angle;
            _hammerSpeed = speed;
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (_hammer != null)
            {
                _hammerAngle = Mathf.MoveTowards(_hammerAngle, _hammerTarget, _hammerSpeed * dt);
                _hammer.localRotation = _hammerBase * Quaternion.Euler(_hammerAngle, 0f, 0f);
            }
            if (_recoil != null)
            {
                _kick = Mathf.Lerp(_kick, 0f, 1f - Mathf.Exp(-14f * dt));
                _recoil.localPosition = _recoilBase + Vector3.back * (0.05f * _kick);
                _recoil.localRotation = Quaternion.Euler(-7f * _kick, 0f, 0f);
            }
            if (_flash != null && _flash.enabled && Time.time >= _flashOffAt) _flash.enabled = false;
        }

        // Animation events (forwarded by RifleAnimEvents on the Animator's object): visual/audio only.
        internal void OnEject()
        {
            Effects.EjectBrass(Assets.SpentCase, _ejection);
        }

        internal void OnChamber() => PlaySound(Sounds.LeverClose, 0.8f);
        internal void OnRoundInserted() => PlaySound(Sounds.Insert, 0.7f);

        private void PlaySound(AudioClip clip, float volume)
        {
            if (_audio == null || clip == null) return;
            _audio.pitch = Random.Range(0.95f, 1.05f);
            _audio.PlayOneShot(clip, volume);
        }
    }

    /// <summary>Receives the rifle clips' animation events on the Animator's object.</summary>
    public class RifleAnimEvents : MonoBehaviour
    {
        private RifleVisual _visual;
        private void Awake() => _visual = GetComponentInParent<RifleVisual>();
        private void OnEject() { if (_visual != null) _visual.OnEject(); }
        private void OnChamber() { if (_visual != null) _visual.OnChamber(); }
        private void OnCycleEnd() { }
        private void OnReloadReady() { }
        private void OnRoundInserted() { if (_visual != null) _visual.OnRoundInserted(); }
        private void OnInsertEnd() { }
        private void OnReloadEnd() { }
    }
}
