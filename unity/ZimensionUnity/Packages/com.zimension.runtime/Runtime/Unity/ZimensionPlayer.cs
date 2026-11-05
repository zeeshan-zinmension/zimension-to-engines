using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Zimension.Core;

namespace Zimension.Unity
{
    /// <summary>
    /// The playable avatar: a CharacterController capsule (1.8 m tall, 0.3 m radius, 0.3 m step,
    /// 50 degree slope limit, as the native player), WASD + mouse look, Shift sprint, Space jump,
    /// a third- or first-person camera and the avatar model's Idle/Walk/Run/Jump clips.
    ///
    /// Saved settings (world.avatar / playerSettings): walkSpeed and sprintSpeed multiply 2.8 and
    /// 6.8 m/s, jump velocity is 6 * sqrt(jumpHeight), cameraType FPP/TPP, cameraDistance,
    /// cameraHeight, disbaleJump, disableSprint, autoSprint. The controller is the single motion
    /// owner of the player; moving platforms carry it through the delta of the item it stands on.
    /// Unarmed third-person movement faces the travel direction.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class ZimensionPlayer : MonoBehaviour
    {
        public const float Height = 1.8f, Radius = 0.3f, StepHeight = 0.3f, EyeHeight = 1.6f;
        public const float GravityScale = 2f;

        [Header("Movement")]
        public float walkSpeed = 2.8f;
        public float runSpeed = 6.8f;
        public float jumpVelocity = 6f;
        public bool jumpEnabled = true, sprintEnabled = true, autoSprint;
        [Header("Camera")]
        public bool thirdPerson = true;
        public float cameraDistance = 3f;
        public float cameraHeight = 1.8f;
        public float mouseSensitivity = 2.2f;
        [Header("State")]
        public bool frozen;
        public float health = 100, maxHealth = 100;

        CharacterController m_Controller;
        Camera m_Camera;
        Transform m_Model, m_ModelPivot;
        float m_Yaw, m_Pitch;
        Vector3 m_Velocity;
        bool m_Grounded;
        Transform m_Platform;
        Vector3 m_PlatformLocalPoint;
        Quaternion m_PlatformRotation;
        Vector3 m_SpawnPosition;
        float m_SpawnYaw;
        readonly HashSet<Collider> m_Touching = new HashSet<Collider>();
        readonly HashSet<Collider> m_TouchingThisFrame = new HashSet<Collider>();
        // Avatar locomotion.
        PlayableGraph m_Graph;
        AnimationMixerPlayable m_Mixer;
        readonly AnimationClipPlayable[] m_Clips = new AnimationClipPlayable[4];
        readonly bool[] m_HasClip = new bool[4];
        readonly float[] m_Weights = new float[4];
        float m_LocomotionTime, m_JumpTime;

        public Camera PlayerCamera => m_Camera;
        public Vector3 Feet => transform.position;
        public bool Grounded => m_Grounded;
        public bool CursorLocked => Cursor.lockState == CursorLockMode.Locked;

        public void Configure(JsonNode world, ZimensionProject project, Camera camera)
        {
            m_Controller = GetComponent<CharacterController>();
            m_Controller.height = Height;
            m_Controller.radius = Radius;
            m_Controller.center = new Vector3(0, Height / 2, 0);
            m_Controller.stepOffset = StepHeight;
            m_Controller.slopeLimit = 50;
            m_Controller.skinWidth = 0.02f;
            m_Controller.minMoveDistance = 0;

            var avatar = world.Obj("avatar");
            walkSpeed = Mathf.Clamp(2.8f * avatar.Float("walkSpeed", 1), 0, 50);
            runSpeed = Mathf.Clamp(6.8f * avatar.Float("sprintSpeed", 1), 0, 80);
            jumpVelocity = 6 * Mathf.Sqrt(Mathf.Clamp(avatar.Float("jumpHeight", 1), 0, 25));
            jumpEnabled = !avatar.Flag("disbaleJump", false);
            sprintEnabled = !avatar.Flag("disableSprint", false);
            autoSprint = avatar.Flag("autoSprint", false);
            thirdPerson = avatar.Str("cameraType") != "FPP";
            cameraDistance = Mathf.Clamp(avatar.Float("cameraDistance", 3), 0.2f, 30);
            cameraHeight = Mathf.Clamp(avatar.Float("cameraHeight", 1.8f), 0.2f, 10);

            m_Camera = camera;
            if (project && project.avatarPrefab && thirdPerson) SpawnAvatarModel(project);
        }

        void SpawnAvatarModel(ZimensionProject project)
        {
            // A pivot owns facing (turned toward travel); the model under it keeps its import pose plus
            // the heading correction and scale, so animation never fights the controller.
            var pivot = new GameObject("Avatar").transform;
            pivot.SetParent(transform, false);
            var model = Instantiate(project.avatarPrefab, pivot);
            model.name = project.avatarPrefab.name;
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            foreach (var c in model.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            // Like Avatar.normalizeRetargetedAvatarHeading: the model's forward from its anatomical
            // left/right (upper arms), turned to face +Z.
            if (AvatarMeasure.Forward(model.transform, out var forward))
                model.transform.localRotation = Quaternion.FromToRotation(forward, Vector3.forward);
            // characterHeight from the real rest-pose mesh (baked skinned vertices), feet on the capsule bottom.
            if (AvatarMeasure.VerticalExtent(model.transform, pivot, out var low, out var high) && high - low > 0.01f)
            {
                float scale = Mathf.Clamp(project.avatarHeight, 0.2f, 10f) / (high - low);
                model.transform.localScale = model.transform.localScale * scale;
                model.transform.localPosition = new Vector3(0, -low * scale, 0);
            }
            m_ModelPivot = pivot;
            m_Model = pivot;
            var clips = new[] { project.idleClip, project.walkClip, project.runClip, project.jumpClip };
            bool any = false;
            foreach (var c in clips) any |= c;
            if (!any)
            {
                Debug.LogWarning("[Zimension] The player character has no Idle/Walk/Run/Jump animations, so it does not animate. " +
                                 "The import report (ZimensionProject > Import report) says why; re-import after exporting the world again from the Zimension editor.");
                return;
            }
            for (int i = 0; i < 4; i++)
                if (clips[i] && clips[i].legacy) { Debug.LogWarning($"[Zimension] Player clip '{clips[i].name}' is a Legacy clip and cannot play; re-import (Zimension > Import from Zimension) to convert it."); clips[i] = null; }
            Debug.Log($"[Zimension] Player animations: Idle={Name(clips[0])}, Walk={Name(clips[1])}, Run={Name(clips[2])}, Jump={Name(clips[3])}");
            var animator = model.GetComponent<Animator>();
            if (!animator) animator = model.AddComponent<Animator>();
            animator.applyRootMotion = false;
            m_Graph = PlayableGraph.Create("Zimension Player");
            m_Graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            m_Mixer = AnimationMixerPlayable.Create(m_Graph, 4);
            for (int i = 0; i < 4; i++)
            {
                if (!clips[i]) continue;
                m_Clips[i] = AnimationClipPlayable.Create(m_Graph, clips[i]);
                m_Clips[i].SetApplyFootIK(false);
                m_Clips[i].SetSpeed(0);
                m_Graph.Connect(m_Clips[i], 0, m_Mixer, i);
                m_HasClip[i] = true;
            }
            AnimationPlayableOutput.Create(m_Graph, "Locomotion", animator).SetSourcePlayable(m_Mixer);
            m_Graph.Play();
        }

        static string Name(AnimationClip c) => c ? c.name : "(none)";

        /// <summary>Remember the spawn pose for reset_player / respawn.</summary>
        public void SetSpawn(Vector3 position, float yaw)
        {
            m_SpawnPosition = position;
            m_SpawnYaw = yaw;
        }

        public void Respawn() => Teleport(m_SpawnPosition, m_SpawnYaw);

        /// <summary>Teleport with valid floor placement: the feet settle on the surface below (elevated interiors stay grounded).</summary>
        public void Teleport(Vector3 position, float yaw)
        {
            if (!m_Controller) m_Controller = GetComponent<CharacterController>();
            m_Controller.enabled = false;
            var start = position + Vector3.up * 0.5f;
            if (Physics.SphereCast(start, Radius * 0.9f, Vector3.down, out var hit, 3f, ~0, QueryTriggerInteraction.Ignore))
                position.y = hit.point.y + 0.01f;
            transform.position = position;
            m_Yaw = yaw; m_Pitch = 0;
            transform.rotation = Quaternion.Euler(0, m_Yaw, 0);
            m_Velocity = Vector3.zero;
            m_Platform = null;
            m_Controller.enabled = true;
            UpdateCamera(true);
        }

        public void Launch(float metres)
        {
            // launchPlayerUp: enough upward speed to rise that many metres.
            m_Velocity.y = Mathf.Sqrt(2 * Mathf.Abs(Physics.gravity.y) * GravityScale * Mathf.Max(0, metres));
            m_Grounded = false;
        }

        /// <summary>Whether the capsule touched this collider during the last movement (solid contact events).</summary>
        public bool Touches(Collider c) => m_Touching.Contains(c);

        public IEnumerable<Collider> Touching => m_Touching;

        void OnControllerColliderHit(ControllerColliderHit hit)
        {
            if (hit.collider) m_TouchingThisFrame.Add(hit.collider);
        }

        public void Tick(float dt, bool inputEnabled)
        {
            if (!m_Controller) return;
            HandleCursor(inputEnabled);
            CarryWithPlatform();
            Vector2 move = Vector2.zero;
            bool sprint = false, jump = false;
            if (inputEnabled && !frozen)
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
            }
            var yawRotation = Quaternion.Euler(0, m_Yaw, 0);
            var wish = yawRotation * new Vector3(move.x, 0, move.y);
            float speed = sprint ? runSpeed : walkSpeed;
            var horizontal = wish * speed;
            float gravity = Mathf.Abs(Physics.gravity.y) * GravityScale;
            if (m_Grounded && m_Velocity.y <= 0) m_Velocity.y = -2f; // keep contact on slopes and steps
            if (m_Grounded && jump) { m_Velocity.y = jumpVelocity; m_Grounded = false; m_JumpTime = 0; }
            m_Velocity.y -= gravity * dt;
            m_Velocity.x = horizontal.x; m_Velocity.z = horizontal.z;
            m_TouchingThisFrame.Clear();
            var flags = m_Controller.Move(m_Velocity * dt);
            m_Grounded = (flags & CollisionFlags.Below) != 0;
            if ((flags & CollisionFlags.Above) != 0 && m_Velocity.y > 0) m_Velocity.y = 0;
            m_Touching.Clear();
            foreach (var c in m_TouchingThisFrame) m_Touching.Add(c);
            AddResting();
            RememberPlatform();
            // Unarmed third-person movement faces the travel direction; first person faces the view.
            if (thirdPerson && m_Model)
            {
                if (wish.sqrMagnitude > 0.01f)
                    m_Model.rotation = Quaternion.Slerp(m_Model.rotation, Quaternion.LookRotation(wish.normalized, Vector3.up), 1 - Mathf.Exp(-12 * dt));
            }
            else transform.rotation = yawRotation;
            Animate(dt, move.sqrMagnitude > 0.01f, sprint);
            UpdateCamera(false);
            // Fell out of the world: back to the spawn point.
            if (transform.position.y < -200) Respawn();
        }

        void AddResting()
        {
            // Standing still never calls OnControllerColliderHit for side surfaces; overlap the capsule (with skin) as well.
            var p0 = transform.position + Vector3.up * Radius;
            var p1 = transform.position + Vector3.up * (Height - Radius);
            var hits = Physics.OverlapCapsule(p0, p1, Radius + m_Controller.skinWidth + 0.02f, ~0, QueryTriggerInteraction.Ignore);
            foreach (var c in hits) if (c && c != m_Controller) m_Touching.Add(c);
        }

        void RememberPlatform()
        {
            m_Platform = null;
            if (!m_Grounded) return;
            if (Physics.SphereCast(transform.position + Vector3.up * (Radius + 0.05f), Radius * 0.9f, Vector3.down, out var hit, 0.3f, ~0, QueryTriggerInteraction.Ignore))
            {
                var item = hit.collider.GetComponentInParent<ZimensionItem>();
                if (!item) return;
                m_Platform = hit.collider.transform;
                m_PlatformLocalPoint = m_Platform.InverseTransformPoint(transform.position);
                m_PlatformRotation = m_Platform.rotation;
            }
        }

        void CarryWithPlatform()
        {
            if (!m_Platform) return;
            var target = m_Platform.TransformPoint(m_PlatformLocalPoint);
            var delta = target - transform.position;
            float yawDelta = (m_Platform.rotation * Quaternion.Inverse(m_PlatformRotation)).eulerAngles.y;
            if (delta.sqrMagnitude > 1e-8f && delta.sqrMagnitude < 4f) m_Controller.Move(delta);
            if (Mathf.Abs(Mathf.DeltaAngle(0, yawDelta)) > 0.01f) m_Yaw += yawDelta;
        }

        void Animate(float dt, bool moving, bool sprint)
        {
            if (!m_Graph.IsValid()) return;
            // Airborne for more than a moment (not just stepping off a kerb) plays the jump clip.
            bool airborne = !m_Grounded && (m_JumpTime < 0.6f || m_Velocity.y < -3f);
            int state = airborne && m_HasClip[3] ? 3 : moving ? (sprint && m_HasClip[2] ? 2 : m_HasClip[1] ? 1 : 0) : 0;
            if (!m_HasClip[state]) state = 0;
            m_LocomotionTime += dt;
            m_JumpTime += dt;
            for (int i = 0; i < 4; i++)
            {
                if (!m_HasClip[i]) continue;
                m_Weights[i] = Mathf.MoveTowards(m_Weights[i], i == state ? 1 : 0, dt / 0.2f);
                m_Mixer.SetInputWeight(i, m_Weights[i]);
                var clip = m_Clips[i].GetAnimationClip();
                float length = clip ? Mathf.Max(0.01f, clip.length) : 1;
                m_Clips[i].SetTime(i == 3 ? Mathf.Min(m_JumpTime, length) : Mathf.Repeat(m_LocomotionTime, length));
            }
            if (state != 3 && m_Weights[3] <= 0) { /* jump clip restarts on the next jump */ }
            m_Graph.Evaluate(0);
        }

        public void RestartJumpClip() => m_LocomotionTime = 0;

        void HandleCursor(bool inputEnabled)
        {
            if (!inputEnabled) return;
            if (ZimensionInput.KeyDown(KeyCode.Escape)) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            else if (ZimensionInput.MouseDown(0) && !CursorLocked) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
        }

        public void UnlockCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        void UpdateCamera(bool snap)
        {
            if (!m_Camera) return;
            var rotation = Quaternion.Euler(m_Pitch, m_Yaw, 0);
            if (!thirdPerson)
            {
                m_Camera.transform.position = transform.position + Vector3.up * EyeHeight;
                m_Camera.transform.rotation = rotation;
                return;
            }
            var pivot = transform.position + Vector3.up * cameraHeight;
            var desired = pivot - rotation * Vector3.forward * cameraDistance;
            // Keep the camera out of walls.
            var direction = desired - pivot;
            float distance = direction.magnitude;
            if (distance > 0.01f && Physics.SphereCast(pivot, 0.2f, direction / distance, out var hit, distance, ~0, QueryTriggerInteraction.Ignore)
                && !hit.collider.transform.IsChildOf(transform))
                desired = pivot + direction / distance * Mathf.Max(0.2f, hit.distance - 0.05f);
            m_Camera.transform.position = desired;
            m_Camera.transform.rotation = rotation;
        }

        public float Yaw => m_Yaw;

        void OnDestroy()
        {
            if (m_Graph.IsValid()) m_Graph.Destroy();
        }
    }
}
