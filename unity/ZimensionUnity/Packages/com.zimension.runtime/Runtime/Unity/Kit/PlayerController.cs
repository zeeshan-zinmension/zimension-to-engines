using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Zimension.Unity;

namespace Zimension.Kit
{
    /// <summary>
    /// The player: a CharacterController (1.8 m, 0.3 m radius) with WASD + mouse look, Shift sprint,
    /// Space jump, a third- or first-person camera, and an avatar whose Animator gets the
    /// parameters <c>Speed</c> (m/s) and <c>Grounded</c>. F uses the nearest
    /// <see cref="PlayerTarget"/> in reach (interactions, pickups, weapons, vehicles) and shows its
    /// prompt on the <see cref="GameHud"/>. Also owns the player's health, ammo reserve, held
    /// weapon (G drops it) and the vehicle being driven.
    /// </summary>
    [RequireComponent(typeof(CharacterController)), AddComponentMenu("Zimension/Player Controller")]
    public sealed class PlayerController : MonoBehaviour
    {
        public static PlayerController Current { get; private set; }

        public const float Height = 1.8f, Radius = 0.3f, EyeHeight = 1.6f;

        [Header("Movement")]
        public float walkSpeed = 2.8f;
        public float runSpeed = 6.8f;
        public float jumpVelocity = 6f;
        public float gravityScale = 2f;
        public bool jumpEnabled = true, sprintEnabled = true, autoSprint;

        [Header("Camera")]
        public Camera playerCamera;
        public bool thirdPerson = true;
        public float cameraDistance = 3f;
        public float cameraHeight = 1.8f;
        public float mouseSensitivity = 2.2f;
        [Tooltip("Camera offset to the right while holding a gun (over the shoulder).")] public float aimShoulderOffset = 0.55f;

        [Header("Avatar")]
        [Tooltip("The visible character (turns toward travel). Its Animator receives Speed and Grounded.")] public Transform avatar;
        public Animator animator;
        [Tooltip("Right-hand bone; found by name on start when empty.")] public Transform rightHand;

        [Header("Health")]
        public float maxHealth = 100;
        public float health = 100;
        public UnityEvent onDamaged = new UnityEvent();
        public UnityEvent onDied = new UnityEvent();

        [Header("State")]
        public bool frozen;

        CharacterController m_Controller;
        float m_Yaw, m_Pitch, m_Recoil;
        Vector3 m_Velocity;
        bool m_Grounded, m_Dead;
        Vector3 m_SpawnPosition; float m_SpawnYaw;
        Transform m_Platform; Vector3 m_PlatformPoint; Quaternion m_PlatformRotation;
        readonly List<PlayerTarget> m_InRange = new List<PlayerTarget>();
        PlayerTarget m_Selected;
        Transform m_Grip;
        float m_BaseFov;
        readonly Dictionary<string, int> m_Ammo = new Dictionary<string, int>();
        readonly List<string> m_Inventory = new List<string>();
        static readonly int SpeedParam = Animator.StringToHash("Speed"), GroundedParam = Animator.StringToHash("Grounded");
        bool m_HasSpeed, m_HasGrounded;

        public Camera PlayerCamera => playerCamera;
        public bool Grounded => m_Grounded;
        public bool Dead => m_Dead;
        public bool CursorLocked => Cursor.lockState == CursorLockMode.Locked;
        public Weapon CurrentWeapon { get; private set; }
        public Vehicle CurrentVehicle { get; private set; }
        public IReadOnlyList<string> Inventory => m_Inventory;
        public bool InputBlocked => frozen || m_Dead || (GameManager.Current && GameManager.Current.InputBlocked);

        /// <summary>The 0.9 x 1.8 m box Zimension tests ranges against (the vehicle's bounds while driving).</summary>
        public Bounds RangeBox
        {
            get
            {
                if (CurrentVehicle)
                {
                    Bounds b = new Bounds(CurrentVehicle.transform.position, Vector3.one); bool any = false;
                    foreach (var r in CurrentVehicle.GetComponentsInChildren<Renderer>()) { if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
                    return b;
                }
                var p = transform.position;
                return new Bounds(p + Vector3.up * Height / 2, new Vector3(0.9f, Height, 0.9f));
            }
        }

        void Awake()
        {
            Current = this;
            m_Controller = GetComponent<CharacterController>();
            m_Controller.height = Height; m_Controller.radius = Radius; m_Controller.center = new Vector3(0, Height / 2, 0);
            m_Controller.stepOffset = 0.3f; m_Controller.slopeLimit = 50; m_Controller.skinWidth = 0.02f; m_Controller.minMoveDistance = 0;
            if (!playerCamera) playerCamera = Camera.main;
            if (playerCamera) m_BaseFov = playerCamera.fieldOfView;
            if (!animator && avatar) animator = avatar.GetComponentInChildren<Animator>();
            if (animator)
            {
                animator.applyRootMotion = false;
                foreach (var p in animator.parameters)
                {
                    if (p.nameHash == SpeedParam) m_HasSpeed = true;
                    if (p.nameHash == GroundedParam) m_HasGrounded = true;
                }
            }
            if (!rightHand && avatar) rightHand = FindHand(avatar);
            m_Yaw = transform.eulerAngles.y;
            m_SpawnPosition = transform.position; m_SpawnYaw = m_Yaw;
        }

        void OnDestroy() { if (Current == this) Current = null; }

        static Transform FindHand(Transform root)
        {
            var animator = root.GetComponentInChildren<Animator>();
            if (animator && animator.isHuman) { var h = animator.GetBoneTransform(HumanBodyBones.RightHand); if (h) return h; }
            Transform best = null; int score = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var n = t.name.ToLowerInvariant().Replace("_", "").Replace(" ", "").Replace(".", "");
                int s = n.EndsWith("righthand") ? 3 : n.EndsWith("handr") || n.EndsWith("rhand") ? 2 : n.Contains("righthand") && !n.Contains("thumb") && !n.Contains("index") && !n.Contains("middle") && !n.Contains("ring") && !n.Contains("pinky") ? 1 : 0;
                if (s > score) { score = s; best = t; }
            }
            return best;
        }

        // ---- Spawn and movement ------------------------------------------------------------------

        public void SetSpawn(Vector3 position, float yaw) { m_SpawnPosition = position; m_SpawnYaw = yaw; }
        public void Respawn() { m_Dead = false; health = maxHealth; Teleport(m_SpawnPosition, m_SpawnYaw); }

        public void Teleport(Vector3 position, float yaw)
        {
            m_Controller.enabled = false;
            if (Physics.SphereCast(position + Vector3.up * 0.5f, Radius * 0.9f, Vector3.down, out var hit, 3f, ~0, QueryTriggerInteraction.Ignore)) position.y = hit.point.y + 0.01f;
            transform.SetPositionAndRotation(position, Quaternion.Euler(0, yaw, 0));
            m_Yaw = yaw; m_Pitch = 0; m_Velocity = Vector3.zero; m_Platform = null;
            m_Controller.enabled = true;
        }

        /// <summary>Upward speed that rises the given height (launch_player_up_*).</summary>
        public void Launch(float metres)
        {
            m_Velocity.y = Mathf.Sqrt(2 * Mathf.Abs(Physics.gravity.y) * gravityScale * Mathf.Max(0, metres));
            m_Grounded = false;
        }

        public void AddRecoil(float degrees) => m_Recoil += degrees;

        void Update()
        {
            if (CurrentVehicle) { HandleCursor(); return; } // the vehicle drives and calls FollowVehicle
            float dt = Time.deltaTime;
            HandleCursor();
            CarryWithPlatform();
            bool input = !InputBlocked;
            Vector2 move = Vector2.zero; bool sprint = false, jump = false;
            if (input)
            {
                if (CursorLocked)
                {
                    m_Yaw += ZimensionInput.MouseX * mouseSensitivity;
                    m_Pitch = Mathf.Clamp(m_Pitch - ZimensionInput.MouseY * mouseSensitivity, -80, 80);
                }
                if (thirdPerson) cameraDistance = Mathf.Clamp(cameraDistance - ZimensionInput.Scroll * 0.5f, 0.5f, 30);
                move = ZimensionInput.Move;
                sprint = sprintEnabled && (autoSprint ^ (ZimensionInput.Key(KeyCode.LeftShift) || ZimensionInput.Key(KeyCode.RightShift)));
                jump = jumpEnabled && ZimensionInput.KeyDown(KeyCode.Space);
                if (ZimensionInput.KeyDown(KeyCode.F)) UseSelected();
                if (ZimensionInput.KeyDown(KeyCode.G) && CurrentWeapon) DropWeapon();
            }
            var yaw = Quaternion.Euler(0, m_Yaw, 0);
            var wish = yaw * new Vector3(move.x, 0, move.y);
            if (wish.sqrMagnitude > 1) wish.Normalize();
            var horizontal = wish * (sprint ? runSpeed : walkSpeed);
            float gravity = Mathf.Abs(Physics.gravity.y) * gravityScale;
            if (m_Grounded && m_Velocity.y <= 0) m_Velocity.y = -2f;
            if (m_Grounded && jump) { m_Velocity.y = jumpVelocity; m_Grounded = false; }
            m_Velocity.y -= gravity * dt;
            m_Velocity.x = horizontal.x; m_Velocity.z = horizontal.z;
            var flags = m_Controller.Move(m_Velocity * dt);
            m_Grounded = (flags & CollisionFlags.Below) != 0;
            if ((flags & CollisionFlags.Above) != 0 && m_Velocity.y > 0) m_Velocity.y = 0;
            RememberPlatform();
            // Facing: travel direction on foot; the camera's yaw while holding a gun or in first person.
            bool aimFacing = !thirdPerson || (CurrentWeapon && CurrentWeapon.type == Weapon.WeaponType.Gun);
            var facing = avatar ? avatar : transform;
            if (aimFacing) facing.rotation = Quaternion.Slerp(facing.rotation, yaw, 1 - Mathf.Exp(-20 * dt));
            else if (wish.sqrMagnitude > 0.01f) facing.rotation = Quaternion.Slerp(facing.rotation, Quaternion.LookRotation(wish.normalized, Vector3.up), 1 - Mathf.Exp(-12 * dt));
            if (animator)
            {
                if (m_HasSpeed) animator.SetFloat(SpeedParam, horizontal.magnitude, 0.08f, dt);
                if (m_HasGrounded) animator.SetBool(GroundedParam, m_Grounded);
            }
            UpdateTargets();
            if (transform.position.y < -200) Respawn();
        }

        void LateUpdate()
        {
            if (CurrentVehicle) return;
            m_Recoil = Mathf.MoveTowards(m_Recoil, 0, 20 * Time.deltaTime);
            UpdateCamera();
        }

        void UpdateCamera()
        {
            if (!playerCamera) return;
            var rotation = Quaternion.Euler(m_Pitch - m_Recoil, m_Yaw, 0);
            bool aiming = CurrentWeapon && CurrentWeapon.Aiming;
            float fov = aiming ? m_BaseFov / Mathf.Max(1, CurrentWeapon.adsZoom) : m_BaseFov;
            playerCamera.fieldOfView = Mathf.Lerp(playerCamera.fieldOfView, fov, 1 - Mathf.Exp(-12 * Time.deltaTime));
            if (!thirdPerson)
            {
                playerCamera.transform.SetPositionAndRotation(transform.position + Vector3.up * EyeHeight, rotation);
                return;
            }
            var pivot = transform.position + Vector3.up * cameraHeight;
            if (CurrentWeapon && CurrentWeapon.type == Weapon.WeaponType.Gun) pivot += rotation * Vector3.right * aimShoulderOffset;
            float distance = aiming ? Mathf.Min(cameraDistance, 1.6f) : cameraDistance;
            var desired = pivot - rotation * Vector3.forward * distance;
            var dir = desired - pivot; float len = dir.magnitude;
            if (len > 0.01f && Physics.SphereCast(pivot, 0.2f, dir / len, out var hit, len, ~0, QueryTriggerInteraction.Ignore) && !hit.collider.transform.IsChildOf(transform))
                desired = pivot + dir / len * Mathf.Max(0.2f, hit.distance - 0.05f);
            playerCamera.transform.SetPositionAndRotation(desired, rotation);
        }

        void HandleCursor()
        {
            if (GameManager.Current && GameManager.Current.InputBlocked) return;
            if (ZimensionInput.KeyDown(KeyCode.Escape)) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; GameManager.Current?.Pause(); }
            else if (ZimensionInput.MouseDown(0) && !CursorLocked) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
        }

        void RememberPlatform()
        {
            m_Platform = null;
            if (!m_Grounded) return;
            if (Physics.SphereCast(transform.position + Vector3.up * (Radius + 0.05f), Radius * 0.9f, Vector3.down, out var hit, 0.3f, ~0, QueryTriggerInteraction.Ignore)
                && hit.collider.GetComponentInParent<WorldItemId>())
            {
                m_Platform = hit.collider.transform;
                m_PlatformPoint = m_Platform.InverseTransformPoint(transform.position);
                m_PlatformRotation = m_Platform.rotation;
            }
        }

        void CarryWithPlatform()
        {
            if (!m_Platform) return;
            var delta = m_Platform.TransformPoint(m_PlatformPoint) - transform.position;
            if (delta.sqrMagnitude > 1e-8f && delta.sqrMagnitude < 4f) m_Controller.Move(delta);
            float yawDelta = (m_Platform.rotation * Quaternion.Inverse(m_PlatformRotation)).eulerAngles.y;
            if (Mathf.Abs(Mathf.DeltaAngle(0, yawDelta)) > 0.01f) m_Yaw += yawDelta;
        }

        // ---- Targets (interactions, pickups, weapons, vehicles) ----------------------------------

        void UpdateTargets()
        {
            var box = RangeBox;
            foreach (var t in PlayerTarget.All)
            {
                bool inside = t && t.Available && t.WorldBounds.Intersects(box);
                bool listed = m_InRange.Contains(t);
                if (inside && !listed) m_InRange.Add(t); // entry order: the newest in reach wins
                else if (!inside && listed) m_InRange.Remove(t);
            }
            m_InRange.RemoveAll(t => !t || !t.Available);
            PlayerTarget selected = null;
            foreach (var t in m_InRange) if (!string.IsNullOrEmpty(t.Prompt)) selected = t;
            m_Selected = selected;
            var hud = GameHud.Current;
            if (hud) hud.SetPrompt(selected ? selected.Prompt : "", selected && selected.IsPickup);
        }

        void UseSelected()
        {
            if (!m_Selected || !m_Selected.Available) return;
            var target = m_Selected;
            target.Use(this);
            m_InRange.Remove(target);
        }

        // ---- Health ------------------------------------------------------------------------------

        public void Damage(float amount)
        {
            if (m_Dead || amount <= 0) return;
            health = Mathf.Max(0, health - amount);
            onDamaged.Invoke();
            if (health <= 0) { m_Dead = true; onDied.Invoke(); GameManager.Current?.PlayerDied(); }
        }

        public void Heal(float amount) { if (!m_Dead) health = Mathf.Min(maxHealth, health + Mathf.Max(0, amount)); }

        // ---- Inventory and ammo ------------------------------------------------------------------

        public void AddToInventory(string item) => m_Inventory.Add(item);
        public int Ammo(string weaponClass) => m_Ammo.TryGetValue(weaponClass ?? "", out var n) ? n : 0;
        public void AddAmmo(string weaponClass, int rounds) => m_Ammo[weaponClass ?? ""] = Ammo(weaponClass) + Mathf.Max(0, rounds);
        public int TakeAmmo(string weaponClass, int wanted)
        {
            int have = Ammo(weaponClass), got = Mathf.Min(have, Mathf.Max(0, wanted));
            m_Ammo[weaponClass ?? ""] = have - got;
            return got;
        }

        // ---- Weapons -----------------------------------------------------------------------------

        /// <summary>
        /// The grip frame on the right hand that Zimension holds weapons in (the hand turned 90 degrees
        /// about X and -90 about Z, then offset 12 cm forward), mirrored into Unity space.
        /// </summary>
        Transform Grip()
        {
            if (m_Grip) return m_Grip;
            var hand = rightHand ? rightHand : avatar ? avatar : transform;
            m_Grip = new GameObject("Weapon Grip").transform;
            m_Grip.SetParent(hand, false);
            // three: rotateX(90) then rotateZ(-90), then translateZ(-0.12), X(-0.04), Y(0.07) in that frame.
            var q = Quaternion.Euler(90, 0, 0) * Quaternion.Euler(0, 0, -90);
            q = new Quaternion(q.x, -q.y, -q.z, q.w);
            m_Grip.localRotation = q;
            m_Grip.localPosition = q * new Vector3(0.04f, 0.07f, -0.12f);
            return m_Grip;
        }

        public void Equip(Weapon weapon)
        {
            if (!weapon || weapon == CurrentWeapon) return;
            if (CurrentWeapon) DropWeapon();
            CurrentWeapon = weapon;
            weapon.AttachTo(this, Grip());
        }

        public void DropWeapon()
        {
            var weapon = CurrentWeapon;
            if (!weapon) return;
            CurrentWeapon = null;
            weapon.Detach();
            if (playerCamera) playerCamera.fieldOfView = m_BaseFov;
        }

        // ---- Vehicles ----------------------------------------------------------------------------

        public void Mount(Vehicle vehicle)
        {
            CurrentVehicle = vehicle;
            m_Controller.enabled = false;
            if (vehicle.enterMode == Vehicle.EnterMode.Hide) SetAvatarVisible(false);
            if (vehicle.seat) transform.SetParent(vehicle.seat, true);
            if (GameHud.Current) GameHud.Current.SetPrompt(vehicle.exitLabel, false);
        }

        public void FollowVehicle(Vehicle vehicle)
        {
            if (!vehicle.seat) transform.position = vehicle.transform.position;
            else transform.SetPositionAndRotation(vehicle.seat.position, vehicle.seat.rotation);
            float mx = 0, my = 0;
            if (CursorLocked && !InputBlocked) { mx = ZimensionInput.MouseX * mouseSensitivity; my = ZimensionInput.MouseY * mouseSensitivity; }
            vehicle.UpdateCamera(playerCamera, mx, my);
        }

        public void Dismount(Vector3 exitPosition)
        {
            var vehicle = CurrentVehicle;
            CurrentVehicle = null;
            transform.SetParent(null, true);
            SetAvatarVisible(true);
            if (vehicle) { var f = vehicle.Forward; m_Yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg; }
            Teleport(exitPosition, m_Yaw);
            if (GameHud.Current) GameHud.Current.SetPrompt("", false);
        }

        void SetAvatarVisible(bool visible)
        {
            var root = avatar ? avatar : transform;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true)) r.enabled = visible;
        }
    }
}
