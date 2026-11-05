using UnityEngine;
using UnityEngine.SceneManagement;

namespace Zimension.Kit
{
    /// <summary>
    /// The game's flow: the start screen, pause (Esc), death, winning, restarting, scene changes
    /// with fades, and Zimension's built-in events (reset_player, player_dead, game_over_win,
    /// launch_player_up_*, health changes, freezing, fades). Generated world logic calls these.
    /// </summary>
    [AddComponentMenu("Zimension/Game Manager")]
    public sealed class GameManager : MonoBehaviour
    {
        public static GameManager Current { get; private set; }

        public PlayerController player;
        public GameHud hud;
        [Tooltip("The world logic: its OnStart runs when play begins.")] public ZimensionLogic logic;

        bool m_Started, m_Paused;

        public bool InputBlocked => m_Paused || (hud && hud.BlocksInput);
        public bool Started => m_Started;

        void Awake()
        {
            Current = this;
            if (!player) player = FindAnyObjectByType<PlayerController>();
            if (!hud) hud = FindAnyObjectByType<GameHud>();
            if (!logic) logic = FindAnyObjectByType<ZimensionLogic>();
        }

        void Start()
        {
            if (!hud || hud.Screen_ != GameHud.ScreenKind.Start) Begin();
        }

        void OnDestroy() { if (Current == this) Current = null; }

        /// <summary>Play begins (the start screen's button): the world's on_start.</summary>
        public void Begin()
        {
            if (m_Started) return;
            m_Started = true;
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
            if (logic) logic.BeginPlay();
        }

        public void Pause()
        {
            if (!m_Started || (hud && hud.Screen_ != GameHud.ScreenKind.None)) return;
            m_Paused = true; Time.timeScale = 0;
            hud?.ShowScreen(GameHud.ScreenKind.Pause);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }

        public void Resume()
        {
            m_Paused = false; Time.timeScale = 1;
            hud?.ShowScreen(GameHud.ScreenKind.None);
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        }

        /// <summary>reset_world / die_start_over: reload the scene from the start.</summary>
        public void ResetWorld()
        {
            Time.timeScale = 1;
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex >= 0 ? SceneManager.GetActiveScene().name : SceneManager.GetActiveScene().path);
        }

        public void ResetPlayer() => player?.Respawn();

        public void PlayerDied()
        {
            hud?.ShowScreen(GameHud.ScreenKind.Dead);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }

        public void RespawnPlayer()
        {
            hud?.ShowScreen(GameHud.ScreenKind.None);
            player?.Respawn();
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        }

        public void Win()
        {
            hud?.ShowScreen(GameHud.ScreenKind.Win);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }

        public void LoadScene(string sceneName, float fadeSeconds = 0.35f)
        {
            if (logic) logic.CarryVariables();
            if (hud && fadeSeconds > 0) { hud.FadeOut(fadeSeconds); StartCoroutine(LoadAfter(sceneName, fadeSeconds)); }
            else SceneManager.LoadScene(sceneName);
        }

        System.Collections.IEnumerator LoadAfter(string sceneName, float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            SceneManager.LoadScene(sceneName);
        }

        /// <summary>Zimension's built-in event names.</summary>
        public void Builtin(string name)
        {
            var p = player;
            switch (name)
            {
                case "reset_player": ResetPlayer(); break;
                case "reset_world": case "die_start_over": ResetWorld(); break;
                case "player_dead": if (p) p.Damage(p.health + 1); else PlayerDied(); break;
                case "game_over_win": Win(); break;
                case "launch_player_up_5m": p?.Launch(5); break;
                case "launch_player_up_10m": p?.Launch(10); break;
                case "hit_damge_10": case "reduce_health_10": p?.Damage(10); break;
                case "reduce_health_50": p?.Damage(50); break;
                case "increase_health_20": p?.Heal(20); break;
                case "full_health": p?.Heal(p.maxHealth); break;
                case "increase_boost_20": if (p && p.CurrentVehicle) p.CurrentVehicle.RefillBoost(p.CurrentVehicle.boostDuration * 0.2f); break;
                case "full_boost": if (p && p.CurrentVehicle) p.CurrentVehicle.RefillBoost(p.CurrentVehicle.boostDuration); break;
                case "stop_vehicle": if (p && p.CurrentVehicle) p.CurrentVehicle.Stop(); break;
                case "scene_fade_in": hud?.FadeIn(0.6f); break;
                case "scene_fade_out": hud?.FadeOut(0.6f); break;
                case "freeze_player_movement": if (p) p.frozen = true; break;
                case "unfreeze_player_movement": if (p) p.frozen = false; break;
                case "clear_delay_events": if (logic) logic.ClearDelays(); break;
                case "save_game": PlayerPrefs.Save(); break;
                default: Debug.LogWarning($"[Zimension] Built-in event '{name}' has no Unity equivalent yet."); break;
            }
        }
    }
}
