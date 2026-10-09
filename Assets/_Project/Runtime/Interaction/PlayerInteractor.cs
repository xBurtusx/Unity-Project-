using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace HorrorHouse
{
    /// <summary>
    /// Casts a short ray from the camera, finds whatever the player is looking at, and shows a
    /// prompt for it.
    ///
    /// Two deliberate choices:
    ///
    /// REACH IS SHORT. 2.2 metres, roughly arm's length plus a step. A long reach lets the player
    /// operate the house from across the room, which quietly destroys the sense of occupying a
    /// body - you want them to have to walk up to the switch in the dark.
    ///
    /// THE RAY HAS WIDTH. A zero-width ray makes small targets like a light switch infuriating to
    /// hit, so this falls back to a short sphere cast. Fighting the cursor is not tension.
    /// </summary>
    public class PlayerInteractor : MonoBehaviour
    {
        [Header("Reach")]
        [Tooltip("Metres. Arm's length plus a step - deliberately short.")]
        public float reach = 2.2f;
        [Tooltip("Forgiveness radius for small targets such as switches.")]
        public float aimAssistRadius = 0.07f;
        public LayerMask blocking = ~0;

        [Header("References")]
        public Camera eye;

        IInteractable current;
        Canvas canvas;
        Text promptLabel;
        Image reticle, ring;
        float ringAmount;

        void Awake()
        {
            if (eye == null) eye = GetComponentInChildren<Camera>();
            BuildUI();
        }

        void Update()
        {
            current = FindTarget();

            bool show = current != null && current.CanInteract;
            if (promptLabel != null)
            {
                promptLabel.enabled = show;
                if (show) promptLabel.text = $"[ E ]   {current.Prompt}";
            }
            if (reticle != null)
                reticle.color = show ? new Color(1f, 1f, 1f, 0.95f) : new Color(1f, 1f, 1f, 0.55f);
            if (ring != null)
            {
                // The ring opens up around the dot when something can be used, and closes again
                // when it cannot - the cursor itself says "interactable" before any text does.
                ringAmount = Mathf.MoveTowards(ringAmount, show ? 1f : 0f, Time.unscaledDeltaTime * 9f);
                ring.color = new Color(1f, 1f, 1f, 0.9f * ringAmount);
                ring.rectTransform.localScale = Vector3.one * Mathf.Lerp(0.55f, 1f, ringAmount);
            }

            if (show && InteractPressed()) current.Interact(gameObject);
        }

        readonly List<Collider> ownColliders = new List<Collider>();

        IInteractable FindTarget()
        {
            if (eye == null) return null;

            // The camera sits INSIDE the player's own capsule, and Unity reports a collider you
            // start inside as a hit at zero distance. Without this the very first thing every
            // cast finds is the player, which has no IInteractable - so nothing in the house
            // could ever be used. Collected once, not per frame.
            if (ownColliders.Count == 0)
                GetComponentsInChildren(true, ownColliders);

            var ray = new Ray(eye.transform.position, eye.transform.forward);

            // Precise first, so that when two things overlap you get the one you are pointing at.
            var found = FirstInteractable(
                Physics.RaycastAll(ray, reach, blocking, QueryTriggerInteraction.Collide));
            if (found != null) return found;

            // Then forgiving, for small targets such as a switch plate.
            return FirstInteractable(
                Physics.SphereCastAll(ray, aimAssistRadius, reach, blocking,
                                      QueryTriggerInteraction.Collide));
        }

        IInteractable FirstInteractable(RaycastHit[] hits)
        {
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (var h in hits)
            {
                if (h.collider == null || ownColliders.Contains(h.collider)) continue;

                var found = h.collider.GetComponentInParent<IInteractable>();
                if (found != null) return found;

                // Something solid and not usable is in the way - a wall between you and the
                // thing beyond it. Stop here rather than reaching through it.
                return null;
            }
            return null;
        }

        // ------------------------------------------------------------ UI

        void BuildUI()
        {
            var go = new GameObject("InteractionUI") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);

            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;          // under the VHS overlay, which sits at 32000
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            // A small dot rather than crosshairs: this is a person looking, not a weapon aiming.
            var dot = new GameObject("Reticle") { hideFlags = HideFlags.DontSave };
            dot.transform.SetParent(go.transform, false);
            reticle = dot.AddComponent<Image>();
            reticle.sprite = DiscSprite(32, 0f);
            reticle.color = new Color(1f, 1f, 1f, 0.55f);
            reticle.raycastTarget = false;
            var dotRt = dot.GetComponent<RectTransform>();
            dotRt.sizeDelta = new Vector2(7f, 7f);
            dotRt.anchoredPosition = Vector2.zero;

            var ringGo = new GameObject("InteractRing") { hideFlags = HideFlags.DontSave };
            ringGo.transform.SetParent(go.transform, false);
            ring = ringGo.AddComponent<Image>();
            ring.sprite = DiscSprite(96, 0.82f);
            ring.color = new Color(1f, 1f, 1f, 0f);
            ring.raycastTarget = false;
            var ringRt = ringGo.GetComponent<RectTransform>();
            ringRt.sizeDelta = new Vector2(34f, 34f);
            ringRt.anchoredPosition = Vector2.zero;

            var label = new GameObject("Prompt") { hideFlags = HideFlags.DontSave };
            label.transform.SetParent(go.transform, false);
            promptLabel = label.AddComponent<Text>();
            promptLabel.font = BuiltinFont();
            promptLabel.fontSize = 22;
            promptLabel.alignment = TextAnchor.MiddleCenter;
            promptLabel.color = new Color(0.94f, 0.94f, 0.92f, 0.92f);
            promptLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
            promptLabel.enabled = false;

            var rt = label.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(700f, 40f);
            // Below centre, so the prompt never sits on top of what you are looking at.
            rt.anchoredPosition = new Vector2(0f, -90f);

            var shadow = label.AddComponent<Outline>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.8f);
            shadow.effectDistance = new Vector2(1.2f, -1.2f);
        }

        /// <summary>Soft-edged disc, or a ring when <paramref name="hole"/> is the inner radius fraction.</summary>
        static Sprite DiscSprite(int size, float hole)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r)) / r;
                float outer = Mathf.Clamp01((1f - d) * r * 0.9f);
                float a = outer;
                if (hole > 0f) a *= Mathf.Clamp01((d - hole) * r * 0.9f + 0.5f) * (hole > 0f ? 1f : 0f);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        static Font BuiltinFont()
        {
            // Unity renamed the built-in font; try the current name first, then the old one.
            var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return f;
        }

        // ------------------------------------------------------------ input

#if ENABLE_INPUT_SYSTEM
        static bool InteractPressed() => Keyboard.current?.eKey.wasPressedThisFrame ?? false;
#else
        static bool InteractPressed() => Input.GetKeyDown(KeyCode.E);
#endif
    }
}
