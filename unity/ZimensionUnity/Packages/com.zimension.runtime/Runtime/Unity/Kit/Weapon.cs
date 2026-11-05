using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using Zimension.Unity;

namespace Zimension.Kit
{
    /// <summary>
    /// A gun or melee weapon the player picks up with the interact key and holds in the right hand.
    /// Left mouse fires (held for automatic guns), R reloads, right mouse aims, G drops. Shots are
    /// hitscan rays from the screen centre with the weapon's accuracy spread; anything with a
    /// <see cref="Health"/> takes <see cref="damage"/>. Ammo: <see cref="capacity"/> rounds per
    /// magazine, the reserve is the player's ammo for this weapon class.
    /// </summary>
    [AddComponentMenu("Zimension/Weapon")]
    public sealed class Weapon : PlayerTarget
    {
        public enum WeaponType { Gun, Melee }

        [Header("Weapon")]
        public string weaponName = "Weapon";
        public WeaponType type = WeaponType.Gun;
        [Tooltip("rifle, pistol, shotgun ... (reserve ammo is shared per class)")] public string weaponClass = "rifle";
        public float damage = 35;
        public float range = 100;
        [Tooltip("Shots per second.")] public float fireRate = 8;
        public int capacity = 30;
        public bool automatic;
        [Range(0, 100)] public float accuracy = 90;
        public float reloadTime = 1.5f;
        [Tooltip("Degrees of camera kick per shot.")] public float recoil = 3;
        public bool recoilAffectsCamera = true;
        [Tooltip("Field-of-view zoom while aiming.")] public float adsZoom = 1.3f;
        public bool infiniteAmmo;
        [Tooltip("Reserve rounds granted on the first pickup.")] public int initialAmmo = 30;
        public Color bulletColor = Color.red;
        public AudioClip fireSound, reloadSound;

        [Header("Holding")]
        [Tooltip("Pose in the hand, relative to the hand grip.")] public Vector3 holdPosition;
        public Vector3 holdEuler;
        [Tooltip("Where shots and the tracer start (child of the weapon).")] public Transform muzzle;
        [Tooltip("Off (Zimension): the weapon keeps its own scale under the hand. On: it keeps its world size.")] public bool keepWorldSize;

        [Header("Events")]
        public UnityEvent onPickup = new UnityEvent();
        public UnityEvent onDrop = new UnityEvent();
        public UnityEvent onFire = new UnityEvent();
        public UnityEvent onReload = new UnityEvent();

        public int Rounds { get; private set; }
        public bool Reloading { get; private set; }
        public bool Held => m_Holder;
        public PlayerController Holder => m_Holder;
        public bool Aiming { get; private set; }

        PlayerController m_Holder;
        Transform m_Parent;
        Vector3 m_LyingPosition, m_LyingScale, m_LyingLocalScale;
        Quaternion m_LyingRotation;
        float m_NextShot;
        bool m_GrantedAmmo;
        LineRenderer m_Tracer;
        AudioSource m_Audio;

        public override string Prompt => Held ? "" : weaponName;
        public override bool IsPickup => true;
        public override bool Available => base.Available && !Held;

        void Awake() => Rounds = capacity;

        public override void Use(PlayerController player) => player.Equip(this);

        /// <summary>Called by the player when it takes this weapon into its hand.</summary>
        public void AttachTo(PlayerController player, Transform hand)
        {
            m_Holder = player;
            m_Parent = transform.parent;
            m_LyingPosition = transform.localPosition; m_LyingRotation = transform.localRotation; m_LyingScale = transform.lossyScale; m_LyingLocalScale = transform.localScale;
            foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;
            foreach (var rb in GetComponentsInChildren<Rigidbody>()) rb.isKinematic = true;
            transform.SetParent(hand, true);
            transform.localPosition = holdPosition;
            transform.localRotation = Quaternion.Euler(holdEuler);
            var parentScale = hand.lossyScale;
            if (!keepWorldSize) transform.localScale = m_LyingLocalScale;
            else transform.localScale = new Vector3(m_LyingScale.x / Mathf.Max(1e-5f, parentScale.x), m_LyingScale.y / Mathf.Max(1e-5f, parentScale.y), m_LyingScale.z / Mathf.Max(1e-5f, parentScale.z));
            if (!m_GrantedAmmo && !infiniteAmmo) { player.AddAmmo(weaponClass, initialAmmo); m_GrantedAmmo = true; }
            onPickup.Invoke();
        }

        /// <summary>Back into the world in front of the player.</summary>
        public void Detach()
        {
            if (!m_Holder) return;
            var player = m_Holder;
            m_Holder = null;
            Aiming = false;
            StopAllCoroutines(); Reloading = false;
            transform.SetParent(m_Parent, true);
            transform.position = player.transform.position + player.transform.forward * 1.2f + Vector3.up * 0.5f;
            transform.localRotation = m_LyingRotation;
            transform.localScale = m_LyingLocalScale;
            if (Physics.Raycast(transform.position, Vector3.down, out var hit, 5f, ~0, QueryTriggerInteraction.Ignore)) transform.position = hit.point + Vector3.up * 0.05f;
            foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = true;
            onDrop.Invoke();
        }

        void Update()
        {
            if (!m_Holder || m_Holder.InputBlocked) return;
            Aiming = type == WeaponType.Gun && ZimensionInput.Mouse(1);
            bool trigger = automatic ? ZimensionInput.Mouse(0) : ZimensionInput.MouseDown(0);
            if (trigger && m_Holder.CursorLocked) TryFire();
            if (ZimensionInput.KeyDown(KeyCode.R)) Reload();
        }

        public void TryFire()
        {
            if (Reloading || Time.time < m_NextShot) return;
            if (type == WeaponType.Gun && !infiniteAmmo && Rounds <= 0) { if (!Reload()) GameHud.Current?.ShowMessage("No ammo", 2); return; }
            m_NextShot = Time.time + 1f / Mathf.Max(0.1f, fireRate);
            if (type == WeaponType.Melee) { Swing(); return; }
            if (!infiniteAmmo) Rounds--;
            Shoot();
            if (fireSound) Play(fireSound);
            if (recoilAffectsCamera && m_Holder) m_Holder.AddRecoil(Aiming ? recoil * 0.5f : recoil);
            onFire.Invoke();
        }

        void Shoot()
        {
            var cam = m_Holder ? m_Holder.PlayerCamera : Camera.main;
            if (!cam) return;
            // accuracy 100 = dead centre; lower spreads up to ~6 degrees.
            float spread = (1 - Mathf.Clamp01(accuracy / 100f)) * 6f * (Aiming ? 0.4f : 1f);
            var direction = Quaternion.Euler(Random.Range(-spread, spread), Random.Range(-spread, spread), 0) * cam.transform.forward;
            var origin = cam.transform.position;
            var end = origin + direction * range;
            if (Physics.Raycast(origin, direction, out var hit, range, ~0, QueryTriggerInteraction.Ignore))
            {
                if (!hit.collider.transform.IsChildOf(m_Holder.transform))
                {
                    end = hit.point;
                    hit.collider.GetComponentInParent<Health>()?.Damage(damage);
                    if (hit.rigidbody && !hit.rigidbody.isKinematic) hit.rigidbody.AddForceAtPosition(direction * damage * 0.2f, hit.point, ForceMode.Impulse);
                }
            }
            StartCoroutine(Tracer(muzzle ? muzzle.position : transform.position, end));
        }

        void Swing()
        {
            var origin = m_Holder.transform.position + Vector3.up * 1.1f + m_Holder.transform.forward * 0.8f;
            foreach (var c in Physics.OverlapSphere(origin, Mathf.Max(0.8f, Mathf.Min(range, 2.5f)), ~0, QueryTriggerInteraction.Ignore))
            {
                if (c.transform.IsChildOf(m_Holder.transform)) continue;
                c.GetComponentInParent<Health>()?.Damage(damage);
            }
            onFire.Invoke();
        }

        IEnumerator Tracer(Vector3 from, Vector3 to)
        {
            if (!m_Tracer)
            {
                var go = new GameObject("Tracer");
                go.transform.SetParent(transform, false);
                m_Tracer = go.AddComponent<LineRenderer>();
                m_Tracer.positionCount = 2;
                m_Tracer.widthMultiplier = 0.02f;
                var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
                m_Tracer.material = new Material(shader);
                m_Tracer.material.color = bulletColor;
                m_Tracer.startColor = m_Tracer.endColor = bulletColor;
            }
            m_Tracer.SetPosition(0, from); m_Tracer.SetPosition(1, to);
            m_Tracer.enabled = true;
            yield return new WaitForSeconds(0.05f);
            m_Tracer.enabled = false;
        }

        /// <summary>Refill the magazine from the holder's reserve; false when there is nothing to load.</summary>
        public bool Reload()
        {
            if (type != WeaponType.Gun || Reloading || Rounds >= capacity) return false;
            int reserve = infiniteAmmo || !m_Holder ? int.MaxValue : m_Holder.Ammo(weaponClass);
            if (reserve <= 0) return false;
            StartCoroutine(Reloading_());
            return true;
        }

        IEnumerator Reloading_()
        {
            Reloading = true;
            onReload.Invoke();
            GameHud.Current?.ShowMessage("Reloading...", reloadTime);
            if (reloadSound) Play(reloadSound);
            yield return new WaitForSeconds(reloadTime);
            int need = capacity - Rounds;
            int got = infiniteAmmo || !m_Holder ? need : m_Holder.TakeAmmo(weaponClass, need);
            Rounds += got;
            Reloading = false;
        }

        void Play(AudioClip clip)
        {
            if (!m_Audio) { m_Audio = gameObject.AddComponent<AudioSource>(); m_Audio.spatialBlend = 0.6f; m_Audio.playOnAwake = false; }
            m_Audio.PlayOneShot(clip);
        }
    }
}
