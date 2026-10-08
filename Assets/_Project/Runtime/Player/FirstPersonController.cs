using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace HorrorHouse
{
    /// <summary>
    /// A grounded first-person controller tuned for a horror game rather than a shooter.
    ///
    /// The speeds here are real human speeds, not game speeds: a normal indoor walk is about
    /// 1.3 m/s, and most games run at two or three times that. Moving at a believable pace is
    /// what makes a house feel like a place rather than a level - it takes a moment to cross a
    /// room, so the room has a size you can feel.
    ///
    /// Head motion is deliberately restrained. Heavy camera bob reads as seasickness, not
    /// presence; what sells presence is a small amount of motion that stays in step with the
    /// footfalls you hear.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class FirstPersonController : MonoBehaviour
    {
        [Header("Camera")]
        public Transform cameraPivot;
        [Range(0.02f, 1f)] public float lookSensitivity = 0.12f;
        [Tooltip("How far you can look up and down, in degrees.")]
        [Range(60f, 89f)] public float maxPitch = 85f;
        public bool invertY;

        [Header("Movement (metres per second)")]
        [Tooltip("A normal indoor walking pace. Real humans walk about 1.3 m/s.")]
        public float walkSpeed = 1.35f;
        [Tooltip("Hurried, not athletic. This is a frightened person, not a sprinter.")]
        public float runSpeed = 3.1f;
        public float crouchSpeed = 0.85f;
        [Tooltip("How quickly you reach full speed. Low values feel like weight.")]
        public float acceleration = 9f;

        [Header("Stance (metres)")]
        public float standHeight = 1.80f;
        public float crouchHeight = 1.15f;
        [Tooltip("Eye height below the top of the capsule.")]
        public float eyeOffset = 0.12f;
        public float stanceChangeSpeed = 7f;

        [Header("Head motion")]
        [Tooltip("Vertical travel of the head at a walk, in metres. Keep this small.")]
        [Range(0f, 0.08f)] public float bobAmount = 0.022f;
        [Tooltip("Sideways sway, in metres. Smaller than the vertical bob.")]
        [Range(0f, 0.06f)] public float swayAmount = 0.013f;
        [Tooltip("Footfalls per second at a walk.")]
        public float bobFrequency = 1.8f;

        [Header("Physics")]
        public float gravity = -9.81f;
        [Tooltip("Downward force kept on the controller while grounded so it hugs stairs.")]
        public float groundStick = -2f;

        CharacterController cc;
        Vector3 velocity;          // horizontal movement, world space
        float verticalVelocity;
        float pitch;               // camera up/down, degrees
        float bobTimer;
        float currentHeight;
        Vector3 cameraRestPosition;

        public bool IsCrouching { get; private set; }
        public bool IsMoving => velocity.sqrMagnitude > 0.02f;
        /// <summary>Distance walked in metres. Stage 9's footstep audio will read this.</summary>
        public float DistanceTravelled { get; private set; }

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            currentHeight = standHeight;
            cc.height = standHeight;
            cc.center = new Vector3(0f, standHeight * 0.5f, 0f);

            if (cameraPivot == null && Camera.main != null) cameraPivot = Camera.main.transform;
            cameraRestPosition = new Vector3(0f, standHeight - eyeOffset, 0f);
            if (cameraPivot != null) cameraPivot.localPosition = cameraRestPosition;
        }

        void OnEnable() => SetCursorLocked(true);
        void OnDisable() => SetCursorLocked(false);

        void Update()
        {
            ReadCursor();
            if (Cursor.lockState != CursorLockMode.Locked) return;

            ReadLook();
            ReadStance();
            ReadMove();
            ApplyHeadMotion();
        }

        // ------------------------------------------------------------ input

        /// <summary>
        /// Escape releases the mouse so you can get back to the Editor; clicking in the Game
        /// view takes control again. Without this, play mode traps the cursor.
        /// </summary>
        void ReadCursor()
        {
            if (EscapePressed()) SetCursorLocked(false);
            else if (Cursor.lockState != CursorLockMode.Locked && FirePressed()) SetCursorLocked(true);
        }

        void ReadLook()
        {
            Vector2 look = LookDelta() * lookSensitivity;
            transform.Rotate(Vector3.up, look.x, Space.Self);

            pitch += invertY ? look.y : -look.y;
            pitch = Mathf.Clamp(pitch, -maxPitch, maxPitch);
            if (cameraPivot != null)
                cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }

        void ReadStance()
        {
            bool wantsCrouch = CrouchHeld();

            // Refuse to stand up under something solid, or the capsule pushes through the ceiling.
            if (IsCrouching && !wantsCrouch && !HasHeadroom()) wantsCrouch = true;
            IsCrouching = wantsCrouch;

            float target = IsCrouching ? crouchHeight : standHeight;
            currentHeight = Mathf.MoveTowards(currentHeight, target, stanceChangeSpeed * Time.deltaTime);
            cc.height = currentHeight;
            cc.center = new Vector3(0f, currentHeight * 0.5f, 0f);
            cameraRestPosition = new Vector3(0f, currentHeight - eyeOffset, 0f);
        }

        bool HasHeadroom()
        {
            var origin = transform.position + Vector3.up * (crouchHeight - cc.radius);
            float distance = standHeight - crouchHeight + 0.05f;
            return !Physics.SphereCast(origin, cc.radius * 0.95f, Vector3.up,
                                       out _, distance, ~0, QueryTriggerInteraction.Ignore);
        }

        void ReadMove()
        {
            Vector2 input = MoveInput();
            var wish = transform.right * input.x + transform.forward * input.y;
            if (wish.sqrMagnitude > 1f) wish.Normalize();

            float speed = IsCrouching ? crouchSpeed : (RunHeld() ? runSpeed : walkSpeed);
            velocity = Vector3.MoveTowards(velocity, wish * speed, acceleration * Time.deltaTime);

            if (cc.isGrounded && verticalVelocity < 0f) verticalVelocity = groundStick;
            else verticalVelocity += gravity * Time.deltaTime;

            var motion = velocity + Vector3.up * verticalVelocity;
            cc.Move(motion * Time.deltaTime);

            DistanceTravelled += velocity.magnitude * Time.deltaTime;
        }

        void ApplyHeadMotion()
        {
            if (cameraPivot == null) return;

            float speed = velocity.magnitude;
            if (cc.isGrounded && speed > 0.1f)
            {
                // Step rate rises with speed, so running feels hurried rather than just faster.
                bobTimer += Time.deltaTime * bobFrequency * Mathf.PI * 2f * (speed / walkSpeed);
            }
            else
            {
                // Settle the head rather than snapping it straight when you stop.
                bobTimer = Mathf.MoveTowards(bobTimer % (Mathf.PI * 2f), 0f, Time.deltaTime * 6f);
            }

            float amplitude = Mathf.Clamp01(speed / walkSpeed);
            // Vertical dips twice per stride, lateral sways once - that is how walking works.
            float y = Mathf.Sin(bobTimer * 2f) * bobAmount * amplitude;
            float x = Mathf.Sin(bobTimer) * swayAmount * amplitude;

            cameraPivot.localPosition = Vector3.Lerp(
                cameraPivot.localPosition, cameraRestPosition + new Vector3(x, y, 0f),
                1f - Mathf.Exp(-18f * Time.deltaTime));
        }

        // ------------------------------------------------------------ input backends

        static void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

#if ENABLE_INPUT_SYSTEM
        Vector2 MoveInput()
        {
            var k = Keyboard.current;
            if (k == null) return Vector2.zero;
            float x = (k.dKey.isPressed ? 1f : 0f) - (k.aKey.isPressed ? 1f : 0f);
            float y = (k.wKey.isPressed ? 1f : 0f) - (k.sKey.isPressed ? 1f : 0f);
            return new Vector2(x, y);
        }

        Vector2 LookDelta() => Mouse.current?.delta.ReadValue() ?? Vector2.zero;
        bool RunHeld() => Keyboard.current?.leftShiftKey.isPressed ?? false;
        bool CrouchHeld() => Keyboard.current?.leftCtrlKey.isPressed ?? false;
        bool EscapePressed() => Keyboard.current?.escapeKey.wasPressedThisFrame ?? false;
        bool FirePressed() => Mouse.current?.leftButton.wasPressedThisFrame ?? false;
#else
        Vector2 MoveInput() => new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
        Vector2 LookDelta() => new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 10f;
        bool RunHeld() => Input.GetKey(KeyCode.LeftShift);
        bool CrouchHeld() => Input.GetKey(KeyCode.LeftControl);
        bool EscapePressed() => Input.GetKeyDown(KeyCode.Escape);
        bool FirePressed() => Input.GetMouseButtonDown(0);
#endif
    }
}
