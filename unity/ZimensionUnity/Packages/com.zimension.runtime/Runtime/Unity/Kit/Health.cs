using System.Collections;
using UnityEngine;
using UnityEngine.Events;

namespace Zimension.Kit
{
    /// <summary>
    /// Hit points for an object (Zimension's damage settings). Weapons call <see cref="Damage"/>;
    /// <see cref="onHit"/> runs on every hit and <see cref="onDie"/> once when it reaches zero. A
    /// dead object can disappear after a delay and respawn where it started (or at a respawn point).
    /// </summary>
    [AddComponentMenu("Zimension/Health")]
    public sealed class Health : MonoBehaviour
    {
        public float maxHealth = 100;
        public float health = 100;
        [Header("Health bar")]
        public bool showHealthBar;
        public Color healthBarColor = new Color(1, 0, 0);
        public float barHeightOffset = 0.3f;
        [Header("Death")]
        public bool disappearOnDeath;
        public float disappearDelay = 2;
        public bool respawn;
        public float respawnDelay = 5;
        [Tooltip("0 = unlimited.")] public int respawnLimit;
        public Transform[] respawnPoints = new Transform[0];
        public bool randomRespawnPoint = true;
        [Header("Events")]
        public UnityEvent onHit = new UnityEvent();
        public UnityEvent onDie = new UnityEvent();
        public UnityEvent onRespawn = new UnityEvent();

        bool m_Dead;
        int m_Respawns;
        Vector3 m_StartPosition;
        Quaternion m_StartRotation;
        float m_LastHit = -10;

        public bool Dead => m_Dead;
        public float Fraction => maxHealth > 0 ? Mathf.Clamp01(health / maxHealth) : 0;

        void Awake() { m_StartPosition = transform.position; m_StartRotation = transform.rotation; }

        public void Damage(float amount)
        {
            if (m_Dead || amount <= 0) return;
            health = Mathf.Max(0, health - amount);
            m_LastHit = Time.time;
            onHit.Invoke();
            if (health <= 0) Die();
        }

        public void Heal(float amount)
        {
            if (m_Dead || amount <= 0) return;
            health = Mathf.Min(maxHealth, health + amount);
        }

        public void Kill() { if (!m_Dead) { health = 0; Die(); } }

        void Die()
        {
            m_Dead = true;
            onDie.Invoke();
            if (disappearOnDeath || respawn) StartCoroutine(AfterDeath());
        }

        IEnumerator AfterDeath()
        {
            if (disappearOnDeath)
            {
                yield return new WaitForSeconds(Mathf.Max(0, disappearDelay));
                SetVisible(false);
            }
            if (!respawn || (respawnLimit > 0 && m_Respawns >= respawnLimit)) yield break;
            yield return new WaitForSeconds(Mathf.Max(0, respawnDelay));
            m_Respawns++;
            var points = respawnPoints;
            if (points != null && points.Length > 0)
            {
                var p = points[randomRespawnPoint ? Random.Range(0, points.Length) : (m_Respawns - 1) % points.Length];
                if (p) transform.SetPositionAndRotation(p.position, p.rotation);
            }
            else transform.SetPositionAndRotation(m_StartPosition, m_StartRotation);
            health = maxHealth;
            m_Dead = false;
            SetVisible(true);
            onRespawn.Invoke();
        }

        // Hidden but still active, so the respawn coroutine keeps running.
        void SetVisible(bool visible)
        {
            foreach (var r in GetComponentsInChildren<Renderer>(true)) r.enabled = visible;
            foreach (var c in GetComponentsInChildren<Collider>(true)) c.enabled = visible;
        }

        void OnGUI()
        {
            if (!showHealthBar || m_Dead) return;
            var cam = Camera.main;
            if (!cam) return;
            var top = transform.position;
            foreach (var r in GetComponentsInChildren<Renderer>()) top.y = Mathf.Max(top.y, r.bounds.max.y);
            var p = cam.WorldToScreenPoint(top + Vector3.up * barHeightOffset);
            if (p.z <= 0 || p.z > 60) return;
            float w = 60, h = 7, x = p.x - w / 2, y = Screen.height - p.y;
            GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, new Color(0, 0, 0, 0.6f), 0, 3);
            GUI.DrawTexture(new Rect(x + 1, y + 1, (w - 2) * Fraction, h - 2), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, healthBarColor, 0, 2);
        }
    }
}
