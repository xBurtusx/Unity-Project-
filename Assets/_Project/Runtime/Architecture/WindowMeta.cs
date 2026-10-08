using UnityEngine;

namespace HorrorHouse
{
    /// <summary>
    /// Identity carried by every generated window. Useful well beyond Stage 2: horror events
    /// need to ask "which windows can the player see the street from" and "which room is this
    /// window in", and curtain state is driven per window.
    /// </summary>
    public class WindowMeta : MonoBehaviour
    {
        [Header("Identity")]
        public string windowId;
        public string room;

        [Header("Behaviour")]
        public bool operable;
        public bool locked = true;

        [Header("Curtains")]
        [Tooltip("0 = fully open, 1 = fully drawn. Changing this between visits is a core unease device.")]
        [Range(0f, 1f)] public float curtainClosed = 0f;
        public bool hasCurtains = true;
    }
}
