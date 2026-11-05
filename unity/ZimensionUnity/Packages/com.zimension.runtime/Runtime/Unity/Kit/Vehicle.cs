using UnityEngine;
using UnityEngine.Events;
using Zimension.Unity;

namespace Zimension.Kit
{
    /// <summary>
    /// A drivable vehicle (Zimension's vehicle settings) with arcade handling: W/S accelerate and
    /// brake/reverse, A/D steer, Space handbrake, Shift boost, F (interact) enters and exits. It
    /// follows the ground under it and stops at obstacles; wheels spin and the steering wheels turn.
    /// The camera follows from behind at <see cref="cameraDistance"/> / <see cref="cameraHeight"/>.
    /// </summary>
    [AddComponentMenu("Zimension/Vehicle")]
    public sealed class Vehicle : PlayerTarget
    {
        public enum EnterMode { Hide, Seat }
        public enum ForwardAxis { PositiveZ, NegativeZ, PositiveX, NegativeX }

        [Header("Prompts")]
        public string enterLabel = "Enter Vehicle";
        public string exitLabel = "Exit Vehicle";
        public EnterMode enterMode = EnterMode.Hide;
        [Tooltip("Where the driver sits (Seat mode) and where the camera looks.")] public Transform seat;

        [Header("Handling")]
        public ForwardAxis forwardAxis = ForwardAxis.PositiveZ;
        public float maxSpeed = 14;
        public float reverseSpeed = 5;
        public float maxAcceleration = 18;
        public float brakeAcceleration = 24;
        [Tooltip("Steering response, per second.")] public float steerSpeed = 2.4f;
        public float steerReturnSpeed = 3.2f;
        [Tooltip("Radians.")] public float maxSteerAngle = 0.55f;
        public float wheelBase = 2.6f;
        [Header("Boost")]
        public bool boostEnabled;
        public float boostMultiplier = 1.8f;
        public float boostDuration = 3;
        public float boostRechargeTime = 4;

        [Header("Wheels")]
        public float wheelRadius = 0.35f;
        public Transform[] wheels = new Transform[0];
        public Transform[] steeringWheels = new Transform[0];
        [Tooltip("Wheel spin axis in the wheel's own space.")] public Vector3 wheelSpinAxis = Vector3.right;

        [Header("Camera")]
        public float cameraDistance = 8;
        public float cameraHeight = 4;

        [Header("Sound")]
        public AudioSource engineSound;

        [Header("Events")]
        public UnityEvent onEnter = new UnityEvent();
        public UnityEvent onExit = new UnityEvent();

        public PlayerController Driver { get; private set; }
        public float Speed => m_Speed;
        public float Boost => m_Boost;

        float m_Speed, m_Steer, m_Boost = 1, m_BoostLeft, m_Recharge;
        float m_Yaw;
        Quaternion[] m_SteerBase = new Quaternion[0];
        Quaternion m_ModelOffset = Quaternion.identity;
        float m_CamYaw, m_CamPitch = 12;
        int m_EnteredFrame = -1;

        public override string Prompt => Driver ? exitLabel : enterLabel;
        public override bool Available => base.Available && !Driver;

        void Awake()
        {
            m_SteerBase = new Quaternion[steeringWheels.Length];
            for (int i = 0; i < steeringWheels.Length; i++) if (steeringWheels[i]) m_SteerBase[i] = steeringWheels[i].localRotation;
            m_ModelOffset = Quaternion.Inverse(Quaternion.LookRotation(LocalForward, Vector3.up));
            m_BoostLeft = boostDuration;
        }

        Vector3 LocalForward
        {
            get
            {
                switch (forwardAxis)
                {
                    case ForwardAxis.NegativeZ: return Vector3.back;
                    case ForwardAxis.PositiveX: return Vector3.right;
                    case ForwardAxis.NegativeX: return Vector3.left;
                    default: return Vector3.forward;
                }
            }
        }

        public Vector3 Forward => transform.TransformDirection(LocalForward).normalized;

        public override void Use(PlayerController player) => Enter(player);

        public void Enter(PlayerController player)
        {
            if (Driver || !player) return;
            Driver = player;
            m_EnteredFrame = Time.frameCount; // the F press that entered must not also exit
            var f = Forward; f.y = 0;
            m_Yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            m_CamYaw = m_Yaw;
            player.Mount(this);
            if (engineSound) { engineSound.loop = true; engineSound.Play(); }
            onEnter.Invoke();
        }

        public void Exit()
        {
            if (!Driver) return;
            var player = Driver;
            Driver = null;
            m_Speed = 0;
            if (engineSound) engineSound.Stop();
            var right = Vector3.Cross(Vector3.up, Forward).normalized;
            player.Dismount(transform.position + right * 2f);
            onExit.Invoke();
        }

        /// <summary>Zimension's stop_vehicle.</summary>
        public void Stop() => m_Speed = 0;
        public void RefillBoost(float seconds) => m_BoostLeft = Mathf.Min(boostDuration, m_BoostLeft + seconds);

        void Update()
        {
            if (!Driver) return;
            float dt = Time.deltaTime;
            bool input = !Driver.InputBlocked;
            if (input && Time.frameCount != m_EnteredFrame && ZimensionInput.KeyDown(KeyCode.F)) { Exit(); return; }
            var move = input ? ZimensionInput.Move : Vector2.zero;
            bool handbrake = input && ZimensionInput.Key(KeyCode.Space);
            bool boosting = input && boostEnabled && m_BoostLeft > 0 && (ZimensionInput.Key(KeyCode.LeftShift) || ZimensionInput.Key(KeyCode.RightShift)) && move.y > 0;
            if (boosting) { m_BoostLeft -= dt; m_Recharge = 0; }
            else if (boostEnabled && m_BoostLeft < boostDuration && (m_Recharge += dt) > 0.5f) m_BoostLeft = Mathf.Min(boostDuration, m_BoostLeft + dt * boostDuration / Mathf.Max(0.1f, boostRechargeTime));
            m_Boost = boosting ? boostMultiplier : 1;

            float top = maxSpeed * m_Boost;
            if (move.y > 0.05f) m_Speed = m_Speed < 0 ? Mathf.MoveTowards(m_Speed, 0, brakeAcceleration * dt) : Mathf.MoveTowards(m_Speed, top, maxAcceleration * m_Boost * dt);
            else if (move.y < -0.05f) m_Speed = m_Speed > 0 ? Mathf.MoveTowards(m_Speed, 0, brakeAcceleration * dt) : Mathf.MoveTowards(m_Speed, -reverseSpeed, maxAcceleration * dt);
            else m_Speed = Mathf.MoveTowards(m_Speed, 0, maxAcceleration * 0.35f * dt);
            if (handbrake) m_Speed = Mathf.MoveTowards(m_Speed, 0, brakeAcceleration * 1.5f * dt);

            float steerTarget = move.x * maxSteerAngle;
            m_Steer = Mathf.MoveTowards(m_Steer, steerTarget, (Mathf.Abs(steerTarget) > Mathf.Abs(m_Steer) ? steerSpeed : steerReturnSpeed) * dt);
            // Bicycle model: turn rate from speed and steering angle.
            m_Yaw += m_Speed / Mathf.Max(0.5f, wheelBase) * Mathf.Tan(m_Steer) * Mathf.Rad2Deg * dt;

            var heading = Quaternion.Euler(0, m_Yaw, 0);
            var step = heading * Vector3.forward * (m_Speed * dt);
            var position = transform.position;
            // Stop at obstacles (ignore our own colliders and the driver).
            if (step.sqrMagnitude > 1e-6f)
            {
                var hits = Physics.RaycastAll(position + Vector3.up * 0.6f, step.normalized, step.magnitude + 1.5f, ~0, QueryTriggerInteraction.Ignore);
                foreach (var h in hits)
                {
                    if (h.collider.transform.IsChildOf(transform) || h.collider.transform.IsChildOf(Driver.transform)) continue;
                    if (h.distance < 1.5f + step.magnitude * 0.5f) { step = Vector3.zero; m_Speed = 0; break; }
                }
            }
            position += step;
            // Follow the ground.
            var groundHits = Physics.RaycastAll(position + Vector3.up * 2f, Vector3.down, 6f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.NegativeInfinity;
            foreach (var h in groundHits)
            {
                if (h.collider.transform.IsChildOf(transform) || h.collider.transform.IsChildOf(Driver.transform)) continue;
                if (h.point.y > best) best = h.point.y;
            }
            if (!float.IsNegativeInfinity(best)) position.y = Mathf.Lerp(position.y, best, 1 - Mathf.Exp(-15 * dt));
            transform.SetPositionAndRotation(position, heading * m_ModelOffset * Quaternion.Euler(0, 0, 0));

            // Wheels.
            float spin = m_Speed * dt / Mathf.Max(0.05f, wheelRadius) * Mathf.Rad2Deg;
            foreach (var w in wheels) if (w) w.Rotate(wheelSpinAxis, spin, Space.Self);
            for (int i = 0; i < steeringWheels.Length; i++)
                if (steeringWheels[i]) steeringWheels[i].localRotation = Quaternion.AngleAxis(m_Steer * Mathf.Rad2Deg, Vector3.up) * m_SteerBase[i];
            if (engineSound) engineSound.pitch = 0.8f + Mathf.Abs(m_Speed) / Mathf.Max(1, maxSpeed) * 0.8f;
            Driver.FollowVehicle(this);
        }

        /// <summary>Third-person chase camera; the mouse orbits it.</summary>
        public void UpdateCamera(Camera cam, float mouseX, float mouseY)
        {
            if (!cam) return;
            m_CamYaw += mouseX;
            m_CamPitch = Mathf.Clamp(m_CamPitch - mouseY, -5, 60);
            if (Mathf.Abs(mouseX) < 0.01f && Mathf.Abs(m_Speed) > 1) m_CamYaw = Mathf.LerpAngle(m_CamYaw, m_Yaw, 1 - Mathf.Exp(-2.5f * Time.deltaTime));
            var target = (seat ? seat.position : transform.position) + Vector3.up * 1.2f;
            var rotation = Quaternion.Euler(m_CamPitch, m_CamYaw, 0);
            cam.transform.position = target - rotation * Vector3.forward * cameraDistance + Vector3.up * (cameraHeight - 1.2f) * 0.5f;
            cam.transform.LookAt(target);
        }
    }
}
