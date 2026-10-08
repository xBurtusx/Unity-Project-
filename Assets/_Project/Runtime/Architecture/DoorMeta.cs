using UnityEngine;

namespace HorrorHouse
{
    /// <summary>
    /// Identity carried by every generated door pivot. Stage 7's interaction system reads this
    /// rather than guessing from object names, and Stage 9's audio picks its sound set from it.
    /// Sits on the PIVOT, which is positioned at the hinge - rotating this transform swings the
    /// door correctly. Never rotate the leaf itself.
    /// </summary>
    public class DoorMeta : MonoBehaviour
    {
        [Header("Identity")]
        public string doorId;
        public string roomA;
        public string roomB;

        [Header("Behaviour")]
        [Tooltip("panel, entry, french, glazed, bifold, barn, fire, overhead")]
        public string kind;
        [Tooltip("in_left, in_right, out_left, out_right, double, slide, bifold, roll")]
        public string swing;
        [Tooltip("Degrees this door reaches when fully open. Sign encodes the swing direction.")]
        public float openAngle = 95f;

        [Header("Audio")]
        [Tooltip("Which sound set to use: bedroom_door, bathroom_door, front_door, garage_door, ...")]
        public string soundSet;

        [Header("State")]
        public bool startsOpen;
        public bool locked;
        [Tooltip("Story flag that must be set before this door can be opened. Blank = always usable.")]
        public string requiredFlag;
    }
}
