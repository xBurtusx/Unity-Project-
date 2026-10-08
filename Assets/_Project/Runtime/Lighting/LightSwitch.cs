using UnityEngine;

namespace HorrorHouse
{
    /// <summary>
    /// A wall switch. Toggles a light group, rocks its plate, and clicks.
    ///
    /// Kept separate from the interaction system on purpose: the player calls <see cref="Use"/>
    /// through <see cref="IInteractable"/>, but horror events call the very same method to flip a
    /// switch with nobody standing there. Neither path knows about the other, so a light turning
    /// itself on is indistinguishable - to the rest of the game, and to the player - from one the
    /// player turned on.
    /// </summary>
    public class LightSwitch : MonoBehaviour, IInteractable
    {
        [Header("Identity")]
        public string switchId;
        public string room;

        [Tooltip("The light group this switch controls - a group name from lights.json.")]
        public string controls;

        [Header("Behaviour")]
        public bool enabledForPlayer = true;

        [Tooltip("Clear this when the power is cut. The switch still moves and still clicks, but " +
                 "nothing happens - far more unsettling than a switch that refuses to move.")]
        public bool powered = true;

        [Header("Plate")]
        public Transform rocker;
        public float rockerAngle = 11f;

        [Header("Audio")]
        public AudioClip clickOn;
        public AudioClip clickOff;
        [Range(0f, 1f)] public float clickVolume = 0.55f;

        AudioSource audioSource;

        // ------------------------------------------------------------ IInteractable

        public string Prompt => AnyLightOn() ? "Turn off the light" : "Turn on the light";

        public bool CanInteract => enabledForPlayer;

        public void Interact(GameObject interactor) => Use();

        // ------------------------------------------------------------ behaviour

        void Awake()
        {
            audioSource = GetComponent<AudioSource>();
            if (audioSource == null)
            {
                audioSource = gameObject.AddComponent<AudioSource>();
                audioSource.playOnAwake = false;
                // Fully 3D, so a switch thrown elsewhere in the house is heard from over there.
                audioSource.spatialBlend = 1f;
                audioSource.minDistance = 0.6f;
                audioSource.maxDistance = 14f;
                audioSource.rolloffMode = AudioRolloffMode.Linear;
            }
        }

        void Start() => UpdateRocker();

        public void Use()
        {
            bool wasOn = AnyLightOn();

            if (powered) LightFixture.ToggleGroup(controls);

            UpdateRocker();

            var clip = wasOn ? clickOff : clickOn;
            if (clip != null) audioSource.PlayOneShot(clip, clickVolume);
        }

        bool AnyLightOn()
        {
            foreach (var f in LightFixture.All)
                if (f.group == controls && f.isOn) return true;
            return false;
        }

        void UpdateRocker()
        {
            if (rocker == null) return;
            rocker.localRotation = Quaternion.Euler(AnyLightOn() ? -rockerAngle : rockerAngle, 0f, 0f);
        }
    }
}
