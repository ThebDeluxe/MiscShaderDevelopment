using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Orbiting third-person camera driven by mouse or right stick.
///
/// Runs in LateUpdate so it reads the character's final position for the frame, after the
/// controller and any physics interpolation have settled. Doing this in Update would show
/// the camera a stale position and produce visible jitter.
///
/// Exposes the flattened forward and right vectors the character uses to steer, so movement
/// stays relative to where the player is looking.
/// </summary>
[DefaultExecutionOrder(200)]
public class ThirdPersonCamera : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("What to orbit. Usually the character's position body.")]
    [SerializeField] private Transform target;

    [Tooltip("Offset from the target to look at, so the camera frames the body rather than " +
             "its pivot at the feet.")]
    [SerializeField] private Vector3 targetOffset = new Vector3(0f, 0.5f, 0f);

    [Header("Orbit")]
    [SerializeField] private float distance = 6f;
    [SerializeField] private float mouseSensitivity = 0.15f;
    [SerializeField] private float gamepadSensitivity = 180f;

    [Tooltip("How far the camera can look down and up, in degrees.")]
    [SerializeField] private float minPitch = -20f;
    [SerializeField] private float maxPitch = 70f;

    [Tooltip("Starting yaw and pitch, in degrees.")]
    [SerializeField] private float startYaw = 0f;
    [SerializeField] private float startPitch = 25f;

    [Header("Smoothing")]
    [Tooltip("Seconds for the camera to catch up to the target. 0 is rigid.")]
    [SerializeField] private float followSmoothing = 0.06f;

    [Header("Collision")]
    [Tooltip("Pull the camera in when geometry blocks the view.")]
    [SerializeField] private bool avoidGeometry = true;
    [SerializeField] private LayerMask obstructionMask = ~0;
    [SerializeField] private float cameraRadius = 0.25f;

    [Header("Cursor")]
    [SerializeField] private bool lockCursor = true;

    private InputAction lookAction;

    private float yaw;
    private float pitch;
    private Vector3 smoothedPivot;
    private Vector3 followVelocity;

    /// <summary>Camera forward flattened onto the ground plane. Use this to steer movement.</summary>
    public Vector3 PlanarForward
    {
        get
        {
            Vector3 flat = Vector3.ProjectOnPlane(transform.forward, Vector3.up);

            // Looking straight down or up leaves nothing to flatten, so fall back to the
            // camera's up vector, which still encodes the yaw.
            if (flat.sqrMagnitude < 1e-4f)
                flat = Vector3.ProjectOnPlane(transform.up, Vector3.up);

            return flat.normalized;
        }
    }

    /// <summary>Camera right flattened onto the ground plane.</summary>
    public Vector3 PlanarRight => Vector3.Cross(Vector3.up, PlanarForward);

    private void Awake()
    {
        yaw = startYaw;
        pitch = startPitch;

        lookAction = new InputAction("Look", InputActionType.Value, expectedControlType: "Vector2");
        lookAction.AddBinding("<Mouse>/delta");
        lookAction.AddBinding("<Gamepad>/rightStick");

        if (target != null)
            smoothedPivot = target.position + targetOffset;
    }

    private void OnEnable()
    {
        lookAction?.Enable();
        ApplyCursorState();
    }

    private void OnDisable()
    {
        lookAction?.Disable();
    }

    private void OnDestroy()
    {
        lookAction?.Dispose();
    }

    private void ApplyCursorState()
    {
        if (!lockCursor) return;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void LateUpdate()
    {
        if (target == null) return;

        ReadLook();

        Vector3 pivot = target.position + targetOffset;

        // Smoothing the pivot rather than the camera position means the orbit stays exact
        // while the follow lags, which reads as weight rather than as sloppy aim.
        smoothedPivot = followSmoothing > 0f
            ? Vector3.SmoothDamp(smoothedPivot, pivot, ref followVelocity, followSmoothing)
            : pivot;

        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 wanted = smoothedPivot - rotation * Vector3.forward * distance;

        if (avoidGeometry)
            wanted = PullInIfBlocked(smoothedPivot, wanted);

        transform.SetPositionAndRotation(wanted, rotation);
    }

    private void ReadLook()
    {
        Vector2 look = lookAction.ReadValue<Vector2>();

        // Mouse delta already arrives per-frame, so scaling it by deltaTime would make
        // sensitivity depend on framerate. Stick input is a held axis and does need it.
        bool fromGamepad = Gamepad.current != null
                           && Gamepad.current.rightStick.ReadValue().sqrMagnitude > 0.0001f;

        float scale = fromGamepad ? gamepadSensitivity * Time.deltaTime : mouseSensitivity;

        yaw += look.x * scale;
        pitch -= look.y * scale;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
    }

    private Vector3 PullInIfBlocked(Vector3 pivot, Vector3 wanted)
    {
        Vector3 direction = wanted - pivot;
        float length = direction.magnitude;
        if (length < 1e-4f) return wanted;

        direction /= length;

        if (Physics.SphereCast(pivot, cameraRadius, direction, out RaycastHit hit, length,
                               obstructionMask, QueryTriggerInteraction.Ignore))
            return pivot + direction * hit.distance;

        return wanted;
    }

    private void OnValidate()
    {
        distance = Mathf.Max(0.1f, distance);
        maxPitch = Mathf.Max(minPitch + 1f, maxPitch);
    }
}
