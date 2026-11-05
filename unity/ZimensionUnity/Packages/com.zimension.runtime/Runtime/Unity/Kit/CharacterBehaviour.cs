using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;

namespace Zimension.Kit
{
    /// <summary>
    /// A non-player character's behaviours (Zimension's character behaviour): Idle, Follow (stays
    /// within 2 m of the player), Attack (chases within 120 m, strikes within 1 m, damage after the
    /// impact delay), Shoot (stops at 8 m and fires its weapon, scaled by difficulty) and Patrol
    /// (walks its points, optionally looping and waiting). One behaviour runs at a time: behaviours
    /// without an activate event start active; <see cref="Activate"/> / <see cref="Deactivate"/>
    /// switch them (the generated world logic calls these on the behaviour's events).
    ///
    /// Movement uses the character's CharacterController along NavMesh paths when a NavMesh exists
    /// (see <see cref="RuntimeNavMesh"/>), otherwise straight at the target. Animator states:
    /// <see cref="idleState"/>, <see cref="walkState"/>, <see cref="runState"/>,
    /// <see cref="attackState"/>, <see cref="deathState"/> (cross-faded by name).
    /// </summary>
    [RequireComponent(typeof(CharacterController)), AddComponentMenu("Zimension/Character Behaviour")]
    public sealed class CharacterBehaviour : MonoBehaviour
    {
        public enum Kind { Idle, Follow, Attack, Shoot, Patrol }
        public enum Difficulty { Easy, Medium, Hard }

        [Serializable]
        public sealed class Behaviour
        {
            public Kind kind = Kind.Idle;
            [Tooltip("Zimension activate event (empty: active from the start).")] public string activateEvent = "";
            public string deactivateEvent = "";
            [Header("Attack / Shoot / Follow")]
            public bool runWhileChasing = true;
            public float attackImpactDelay = 0.5f;
            public float attackDamage = 10;
            public Weapon weapon;
            public Difficulty difficulty = Difficulty.Medium;
            public float aimReactionDelay = 0.22f;
            [Header("Patrol")]
            public List<Vector3> patrolPoints = new List<Vector3>();
            public bool loop = true;
            public bool useRun;
            public float waitAtPoint;
        }

        public List<Behaviour> behaviours = new List<Behaviour>();
        [Header("Movement")]
        public float walkSpeed = 1.8f;
        public float runSpeed = 4.5f;
        public float chaseDistance = 120, attackRange = 1, shootRange = 8, followDistance = 2;
        [Tooltip("Yaw (degrees) that turns the model's own forward to +Z.")] public float modelYawOffset;
        [Header("Animation")]
        public Animator animator;
        public string idleState = "Idle", walkState = "Walk", runState = "Run", attackState = "Right_Hook", deathState = "Dying";
        [Tooltip("Seconds the character pauses when hit (its hit reaction).")] public float hitReaction = 0.2f;
        [Header("Events")]
        public UnityEvent onAttackHit = new UnityEvent();

        CharacterController m_Controller;
        Health m_Health;
        int m_Active = -1, m_Patrol;
        float m_Yaw, m_SwingTime, m_Reaction, m_Wait, m_NextShot, m_Repath, m_VerticalSpeed;
        bool m_Swinging, m_Impact, m_PatrolDone;
        string m_State;
        Vector3 m_Start; Quaternion m_StartRotation, m_Tilt = Quaternion.identity;
        readonly List<Vector3> m_Path = new List<Vector3>();
        readonly Dictionary<string, bool> m_Has = new Dictionary<string, bool>();

        public Behaviour ActiveBehaviour => m_Active >= 0 && m_Active < behaviours.Count ? behaviours[m_Active] : null;
        public bool Dead => m_Health && m_Health.Dead;

        void Awake()
        {
            m_Controller = GetComponent<CharacterController>();
            m_Health = GetComponent<Health>();
            if (!animator) animator = GetComponentInChildren<Animator>();
            m_Start = transform.position; m_StartRotation = transform.rotation;
            // The object's yaw minus the model offset is the way the character faces; the rest of its rotation is kept.
            float yaw = transform.eulerAngles.y;
            m_Tilt = Quaternion.Inverse(Quaternion.Euler(0, yaw, 0)) * transform.rotation;
            m_Yaw = yaw - modelYawOffset;
            if (m_Health)
            {
                m_Health.onHit.AddListener(() => { m_Reaction = hitReaction; m_State = null; });
                m_Health.onDie.AddListener(() => { m_Swinging = false; Play(deathState, 0.5f, restart: true); });
                m_Health.onRespawn.AddListener(ResetBehaviour);
            }
            ResetBehaviour();
        }

        void ResetBehaviour()
        {
            m_Active = -1;
            for (int i = 0; i < behaviours.Count; i++) if (string.IsNullOrEmpty(behaviours[i].activateEvent)) m_Active = i;
            m_Swinging = false; m_Reaction = 0; m_Patrol = 0; m_PatrolDone = false; m_State = null;
            Play(idleState, 0);
        }

        /// <summary>Start behaviour <paramref name="index"/> (its activate event).</summary>
        public void Activate(int index)
        {
            if (Dead || index < 0 || index >= behaviours.Count) return;
            m_Active = index; m_Swinging = false; m_Patrol = 0; m_PatrolDone = false; m_Path.Clear();
        }

        /// <summary>Stop behaviour <paramref name="index"/> if it is running (its deactivate event): back to idle.</summary>
        public void Deactivate(int index)
        {
            if (m_Active != index) return;
            m_Active = -1; m_Swinging = false;
            Play(idleState, 0.5f);
        }

        bool Has(string state)
        {
            if (string.IsNullOrEmpty(state) || !animator || !animator.runtimeAnimatorController) return false;
            if (!m_Has.TryGetValue(state, out var has)) m_Has[state] = has = animator.HasState(0, Animator.StringToHash(state));
            return has;
        }

        void Play(string state, float fade, bool restart = false)
        {
            if (!Has(state) || (state == m_State && !restart)) return;
            m_State = state;
            animator.CrossFadeInFixedTime(state, fade, 0, 0);
        }

        float ClipLength(string state)
        {
            if (!animator || !animator.runtimeAnimatorController) return 0.6f;
            foreach (var clip in animator.runtimeAnimatorController.animationClips) if (clip && clip.name.StartsWith(state, StringComparison.Ordinal)) return clip.length;
            return 0.6f;
        }

        void Update()
        {
            if (Dead) return;
            float dt = Time.deltaTime;
            if (m_Reaction > 0) { m_Reaction -= dt; Gravity(dt); return; }
            var player = PlayerController.Current;
            var behaviour = ActiveBehaviour;
            if (behaviour == null || behaviour.kind == Kind.Idle) { Play(idleState, 0.5f); Gravity(dt); return; }
            bool alive = player && !player.Dead;
            var target = alive ? player.transform.position : transform.position;
            var delta = target - transform.position; float horizontal = new Vector2(delta.x, delta.z).magnitude;
            switch (behaviour.kind)
            {
                case Kind.Follow:
                    if (!alive || horizontal > chaseDistance || horizontal <= followDistance) Stand(dt, alive ? delta : Vector3.zero);
                    else MoveTo(target, behaviour.runWhileChasing ? runSpeed : walkSpeed, behaviour.runWhileChasing ? runState : walkState, dt);
                    break;
                case Kind.Attack:
                    if (!alive || horizontal > chaseDistance) { m_Swinging = false; Stand(dt, Vector3.zero); break; }
                    if (m_Swinging || delta.magnitude <= attackRange) Swing(behaviour, player, delta, dt);
                    else MoveTo(target, behaviour.runWhileChasing ? runSpeed : walkSpeed, behaviour.runWhileChasing ? runState : walkState, dt);
                    break;
                case Kind.Shoot:
                    if (!alive || horizontal > chaseDistance) { Stand(dt, Vector3.zero); break; }
                    if (horizontal > shootRange) MoveTo(target, behaviour.runWhileChasing ? runSpeed : walkSpeed, behaviour.runWhileChasing ? runState : walkState, dt);
                    else { Stand(dt, delta); Shoot(behaviour, player, dt); }
                    break;
                case Kind.Patrol: Patrol(behaviour, dt); break;
            }
        }

        void Gravity(float dt)
        {
            if (!m_Controller.enabled) return;
            m_VerticalSpeed = m_Controller.isGrounded ? -2 : Mathf.Max(-30, m_VerticalSpeed - 9.81f * dt);
            m_Controller.Move(new Vector3(0, m_VerticalSpeed * dt, 0));
        }

        void Face(Vector3 direction, float dt)
        {
            direction.y = 0;
            if (direction.sqrMagnitude < 1e-6f) return;
            float desired = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            m_Yaw = Mathf.LerpAngle(m_Yaw, desired, 1 - Mathf.Exp(-10 * dt));
            transform.rotation = Quaternion.Euler(0, m_Yaw + modelYawOffset, 0) * m_Tilt;
        }

        void Stand(float dt, Vector3 look)
        {
            Play(idleState, 0.5f);
            Face(look, dt);
            Gravity(dt);
        }

        Vector3 NextCorner(Vector3 target)
        {
            if (!RuntimeNavMesh.Ready && NavMesh.CalculateTriangulation().vertices.Length == 0) return target;
            if ((m_Repath -= Time.deltaTime) <= 0 || m_Path.Count == 0)
            {
                m_Repath = 0.5f; m_Path.Clear();
                var path = new NavMeshPath();
                if (NavMesh.SamplePosition(transform.position, out var from, 2, NavMesh.AllAreas) && NavMesh.SamplePosition(target, out var to, 3, NavMesh.AllAreas)
                    && NavMesh.CalculatePath(from.position, to.position, NavMesh.AllAreas, path) && path.corners.Length > 1)
                    for (int i = 1; i < path.corners.Length; i++) m_Path.Add(path.corners[i]);
            }
            while (m_Path.Count > 1 && Vector2.Distance(new Vector2(m_Path[0].x, m_Path[0].z), new Vector2(transform.position.x, transform.position.z)) < 0.4f) m_Path.RemoveAt(0);
            return m_Path.Count > 0 ? m_Path[0] : target;
        }

        void MoveTo(Vector3 target, float speed, string state, float dt)
        {
            var corner = NextCorner(target);
            var direction = corner - transform.position; direction.y = 0;
            if (direction.sqrMagnitude > 1e-4f) direction.Normalize();
            Face(direction, dt);
            Play(Has(state) ? state : walkState, 0.5f);
            m_VerticalSpeed = m_Controller.isGrounded ? -2 : Mathf.Max(-30, m_VerticalSpeed - 9.81f * dt);
            m_Controller.Move((direction * speed + Vector3.up * m_VerticalSpeed) * dt);
        }

        void Swing(Behaviour b, PlayerController player, Vector3 delta, float dt)
        {
            Face(delta, dt);
            if (!m_Swinging) { m_Swinging = true; m_SwingTime = 0; m_Impact = false; Play(attackState, 0.2f, restart: true); }
            float duration = Mathf.Max(0.1f, ClipLength(attackState));
            m_SwingTime += dt;
            if (!m_Impact && m_SwingTime >= Mathf.Min(b.attackImpactDelay, duration))
            {
                m_Impact = true;
                var from = transform.position + Vector3.up; var to = player.transform.position + Vector3.up;
                bool clear = !Physics.Linecast(from, to, out var hit, ~0, QueryTriggerInteraction.Ignore) || hit.collider.transform.IsChildOf(player.transform) || hit.collider.transform.IsChildOf(transform);
                if ((player.transform.position - transform.position).magnitude <= attackRange + 0.3f && clear) { player.Damage(b.attackDamage); onAttackHit.Invoke(); }
            }
            Gravity(dt);
            if (m_SwingTime >= duration) { m_Swinging = false; m_State = null; }
        }

        void Shoot(Behaviour b, PlayerController player, float dt)
        {
            var w = b.weapon;
            float damageScale = b.difficulty == Difficulty.Easy ? 0.8f : b.difficulty == Difficulty.Hard ? 1.3f : 1;
            float cooldownScale = b.difficulty == Difficulty.Easy ? 1.35f : b.difficulty == Difficulty.Hard ? 0.75f : 1;
            float accuracyScale = b.difficulty == Difficulty.Easy ? 0.75f : b.difficulty == Difficulty.Hard ? 1.18f : 1;
            if (Time.time < m_NextShot) return;
            float rate = w ? Mathf.Max(0.2f, w.fireRate) : 2;
            m_NextShot = Time.time + Mathf.Max(b.aimReactionDelay, 1f / rate * cooldownScale * 3);
            float accuracy = Mathf.Clamp01((w ? w.accuracy : 70) / 100f * accuracyScale);
            var origin = transform.position + Vector3.up * 1.4f;
            var aim = player.transform.position + Vector3.up * 1.2f - origin;
            float spread = (1 - accuracy) * 8;
            var direction = Quaternion.Euler(UnityEngine.Random.Range(-spread, spread), UnityEngine.Random.Range(-spread, spread), 0) * aim.normalized;
            if (Physics.Raycast(origin, direction, out var hit, w ? w.range : 100, ~0, QueryTriggerInteraction.Ignore) && hit.collider.transform.IsChildOf(player.transform))
                player.Damage((w ? w.damage : 10) * damageScale);
        }

        void Patrol(Behaviour b, float dt)
        {
            if (b.patrolPoints.Count == 0 || m_PatrolDone) { Stand(dt, Vector3.zero); return; }
            var point = b.patrolPoints[Mathf.Clamp(m_Patrol, 0, b.patrolPoints.Count - 1)];
            var delta = point - transform.position;
            if (new Vector2(delta.x, delta.z).magnitude < 0.35f)
            {
                if (m_Wait <= 0) m_Wait = b.waitAtPoint;
                m_Wait -= dt;
                Stand(dt, Vector3.zero);
                if (m_Wait > 0) return;
                m_Wait = 0; m_Path.Clear();
                m_Patrol++;
                if (m_Patrol >= b.patrolPoints.Count) { if (b.loop) m_Patrol = 0; else { m_PatrolDone = true; m_Patrol = b.patrolPoints.Count - 1; } }
                return;
            }
            MoveTo(point, b.useRun ? runSpeed : walkSpeed, b.useRun ? runState : walkState, dt);
        }

        void OnDrawGizmosSelected()
        {
            foreach (var b in behaviours)
            {
                if (b.kind != Kind.Patrol) continue;
                Gizmos.color = Color.cyan;
                for (int i = 0; i < b.patrolPoints.Count; i++)
                {
                    Gizmos.DrawSphere(b.patrolPoints[i], 0.15f);
                    if (i + 1 < b.patrolPoints.Count) Gizmos.DrawLine(b.patrolPoints[i], b.patrolPoints[i + 1]);
                }
            }
        }
    }
}
