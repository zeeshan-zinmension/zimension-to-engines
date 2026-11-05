using UnityEngine;
using UnityEngine.Events;

namespace Zimension.Kit
{
    /// <summary>
    /// A collectable: picked up on touch (<see cref="autoPickup"/>) or with the interact key. The
    /// object's sounds stop, it hides, and then <see cref="onCollected"/> runs (Zimension's collect
    /// counter and On Collect event are wired there).
    /// </summary>
    [AddComponentMenu("Zimension/Collectable")]
    public sealed class Collectable : PlayerTarget
    {
        public bool autoPickup = true;
        [Tooltip("Shown in the pickup prompt.")]
        public string displayName = "Item";
        [Tooltip("Collected into the player's inventory instead of just counted.")]
        public bool inventory;
        public AudioClip collectSound;
        public UnityEvent onCollected = new UnityEvent();

        public override string Prompt => autoPickup ? "" : displayName;
        public override bool IsPickup => true;
        public override bool Available => base.Available && !autoPickup;

        public override void Use(PlayerController player) => Collect();

        void Update()
        {
            if (!autoPickup) return;
            var player = PlayerController.Current;
            if (player && player.isActiveAndEnabled && WorldBounds.Intersects(player.RangeBox)) Collect();
        }

        public void Collect()
        {
            if (!gameObject.activeInHierarchy) return;
            if (collectSound) AudioSource.PlayClipAtPoint(collectSound, transform.position);
            foreach (var s in GetComponentsInChildren<AudioSource>()) s.Stop();
            if (inventory && PlayerController.Current) PlayerController.Current.AddToInventory(displayName);
            gameObject.SetActive(false);
            onCollected.Invoke();
        }
    }
}
