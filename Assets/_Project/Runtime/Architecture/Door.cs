using System.Collections.Generic;
using UnityEngine;

namespace HorrorHouse
{
    /// <summary>
    /// Makes a generated door open.
    ///
    /// Sits on the PIVOT, which Stage 1 placed at the hinge edge. Opening is therefore just a
    /// rotation of this transform - the leaf, its handle and its collider are children and come
    /// along for free. Nothing here ever rotates the leaf itself; a door that turns about its
    /// middle is the single most recognisable tell of a fake building.
    ///
    /// Not every door swings. A barn door slides along the wall and a garage door rolls upward,
    /// so motion is chosen from the door's kind rather than assumed.
    /// </summary>
    public class Door : MonoBehaviour, IInteractable
    {
        public enum Motion { Swing, Slide, Roll }

        [Header("Identity")]
        public string doorId;
        [Tooltip("Both leaves of a double door share an id, so they open together.")]
        public string soundSet;

        [Header("Motion")]
        public Motion motion = Motion.Swing;
        [Tooltip("Degrees when fully open. The sign encodes which way it swings.")]
        public float openAngle = 95f;
        [Tooltip("Metres travelled by a sliding or rolling door.")]
        public float openDistance = 0.9f;
        [Tooltip("Seconds to open. Heavier doors should be slower - it reads as weight.")]
        public float openTime = 0.8f;
        public float closeTime = 0.9f;

        [Header("State")]
        public bool isOpen;
        public bool locked;
        [Tooltip("Story flag that must be set before this door will open. Blank = always usable.")]
        public string requiredFlag;
        [Tooltip("Degrees it moves before stopping dead when locked - the handle gives, the door does not.")]
        public float lockedJiggle = 2.5f;

        [Header("Audio")]
        public AudioClip openClip, closeClip, lockedClip;
        [Range(0f, 1f)] public float volume = 0.7f;

        float t;                 // 0 = closed, 1 = open
        float target;
        Vector3 closedLocalPos;
        Quaternion closedLocalRot;
        AudioSource audioSource;
        float jiggleUntil;

        static readonly Dictionary<string, List<Door>> byId = new Dictionary<string, List<Door>>();

        // ------------------------------------------------------------ IInteractable

        public string Prompt
        {
            get
            {
                if (IsBlocked) return "Locked";
                return isOpen ? "Close" : "Open";
            }
        }

        public bool CanInteract => true;

        public void Interact(GameObject interactor)
        {
            // Both leaves of a double door answer to one press.
            if (!string.IsNullOrEmpty(doorId) && byId.TryGetValue(doorId, out var group))
                foreach (var d in group) d.Use();
            else
                Use();
        }

        // ------------------------------------------------------------ lifecycle

        bool IsBlocked => locked || !string.IsNullOrEmpty(requiredFlag);

        bool poseCaptured;

        /// <summary>
        /// Records where "closed" is. Everything else is measured from here, so if this never
        /// runs the door has no idea where it started and will not move correctly. Guarded so it
        /// can be called from anywhere - including the editor, where Awake does not run - without
        /// ever recording an already-open door as its closed position.
        /// </summary>
        public void CapturePose()
        {
            if (poseCaptured) return;
            closedLocalPos = transform.localPosition;
            closedLocalRot = transform.localRotation;
            poseCaptured = true;
        }

        void Awake()
        {
            CapturePose();

            audioSource = GetComponent<AudioSource>();
            if (audioSource == null)
            {
                audioSource = gameObject.AddComponent<AudioSource>();
                audioSource.playOnAwake = false;
                // Fully positional: a door closing upstairs must sound like it is upstairs.
                audioSource.spatialBlend = 1f;
                audioSource.minDistance = 1f;
                audioSource.maxDistance = 22f;
                audioSource.rolloffMode = AudioRolloffMode.Linear;
            }
        }

        void OnEnable()
        {
            if (string.IsNullOrEmpty(doorId)) return;
            if (!byId.TryGetValue(doorId, out var list)) byId[doorId] = list = new List<Door>();
            list.Add(this);

            t = target = isOpen ? 1f : 0f;
            ApplyPose();
        }

        void OnDisable()
        {
            if (!string.IsNullOrEmpty(doorId) && byId.TryGetValue(doorId, out var list))
                list.Remove(this);
        }

        void Update()
        {
            if (Time.time < jiggleUntil)
            {
                // Locked: the door gives a little, then stops. Shows it is a door, not scenery.
                float j = Mathf.Sin((jiggleUntil - Time.time) * 38f) * 0.14f;
                transform.localRotation = closedLocalRot * Quaternion.Euler(0f, lockedJiggle * j, 0f);
                return;
            }

            if (Mathf.Approximately(t, target)) return;

            float duration = Mathf.Max(0.05f, target > t ? openTime : closeTime);
            t = Mathf.MoveTowards(t, target, Time.deltaTime / duration);
            ApplyPose();
        }

        // ------------------------------------------------------------ behaviour

        /// <summary>
        /// Toggles this leaf. Called by the player through <see cref="Interact"/>, and by horror
        /// events directly - a door that opens itself must be indistinguishable from one opened
        /// by hand, so both go through here.
        /// </summary>
        public void Use()
        {
            CapturePose();
            if (IsBlocked)
            {
                jiggleUntil = Time.time + 0.35f;
                if (lockedClip != null) audioSource.PlayOneShot(lockedClip, volume);
                return;
            }

            isOpen = !isOpen;
            target = isOpen ? 1f : 0f;

            var clip = isOpen ? openClip : closeClip;
            if (clip != null) audioSource.PlayOneShot(clip, volume);
        }

        /// <summary>Sets the door without any sound, for scene setup and story state.</summary>
        public void SetOpenImmediate(bool open)
        {
            isOpen = open;
            t = target = open ? 1f : 0f;
            ApplyPose();
        }

        /// <summary>Opens or closes over time, as if someone did it. Used by events.</summary>
        public void SetOpen(bool open, bool silent = false)
        {
            if (isOpen == open) return;
            isOpen = open;
            target = open ? 1f : 0f;
            if (silent) return;
            var clip = open ? openClip : closeClip;
            if (clip != null) audioSource.PlayOneShot(clip, volume);
        }

        void ApplyPose()
        {
            CapturePose();
            // Ease out, so the door slows as it reaches its stop instead of snapping.
            float e = 1f - (1f - t) * (1f - t);

            switch (motion)
            {
                case Motion.Swing:
                    transform.localRotation = closedLocalRot * Quaternion.Euler(0f, openAngle * e, 0f);
                    break;

                case Motion.Slide:
                    // Slides along the wall - the direction the leaf already extends from the hinge.
                    transform.localPosition = closedLocalPos + SlideDirection() * (openDistance * e);
                    break;

                case Motion.Roll:
                    transform.localPosition = closedLocalPos + Vector3.up * (openDistance * e);
                    break;
            }
        }

        /// <summary>
        /// The leaf hangs off the pivot along the wall, so the vector from pivot to leaf is the
        /// wall direction - exactly the way a sliding door has to travel.
        /// </summary>
        Vector3 SlideDirection()
        {
            if (transform.childCount == 0) return Vector3.right;
            var offset = transform.GetChild(0).localPosition;
            offset.y = 0f;
            return offset.sqrMagnitude < 0.0001f ? Vector3.right : offset.normalized;
        }

        public static Door Find(string id)
        {
            return byId.TryGetValue(id, out var list) && list.Count > 0 ? list[0] : null;
        }

        public static void SetAll(string id, bool open, bool silent = false)
        {
            if (byId.TryGetValue(id, out var list))
                foreach (var d in list) d.SetOpen(open, silent);
        }
    }
}
