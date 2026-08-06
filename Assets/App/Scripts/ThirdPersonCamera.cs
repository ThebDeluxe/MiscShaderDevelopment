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

    [Header("Zoom")]
    [Tooltip("Metres of zoom per notch of scroll wheel.")]
    [SerializeField] private float zoomSpeed = 0.5f;

    [SerializeField] private float minDistance = 1.5f;
    [SerializeField] private float maxDistance = 20f;

    [Tooltip("Seconds for the zoom to settle. 0 snaps.")]
    [SerializeField] private float zoomSmoothing = 0.08f;

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

    [Tooltip("Colliders on this Rigidbody are never treated as obstructions. Left empty, it " +
             "is found from the target - which matters once the player picks things up, " +
             "since absorbed objects sit around the pivot and would otherwise sweep through " +
             "the camera's line and yank it in as they roll.")]
    [SerializeField] private Rigidbody ignoreBody;

    [Tooltip("Never come closer to the target than this, however blocked the view is. " +
             "Stops the camera diving into the character or through the floor.")]
    [SerializeField] private float minObstructionDistance = 1.2f;

    [Tooltip("Extra gap kept between the camera and whatever it hit, so the near clip plane " +
             "does not slice into the surface.")]
    [SerializeField] private float collisionPadding = 0.2f;

    [Tooltip("Seconds to ease back out once the view clears. Pulling IN is always instant, " +
             "since a slow pull-in would let the camera sit inside geometry.")]
    [SerializeField] private float returnSmoothing = 0.25f;

    [Header("Cursor")]
    [SerializeField] private bool lockCursor = true;

    private InputAction lookAction;
    private InputAction zoomAction;

    private float yaw;
    private float pitch;
    private float wantedDistance;
    private float zoomVelocity;
    private Vector3 smoothedPivot;
    private Vector3 followVelocity;
    private float currentDistance;
    private float distanceVelocity;
    private readonly RaycastHit[] obstructionHits = new RaycastHit[16];

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

    /// <summary>
    /// Camera right flattened onto the ground plane.
    /// </summary>
    public Vector3 PlanarRight => Vector3.Cross(Vector3.up, PlanarForward);

    private void Awake()
    {
        yaw = startYaw;
        pitch = startPitch;
        wantedDistance = distance;
        currentDistance = distance;

        lookAction = new InputAction("Look", InputActionType.Value, expectedControlType: "Vector2");
        lookAction.AddBinding("<Mouse>/delta");
        lookAction.AddBinding("<Gamepad>/rightStick");

        zoomAction = new InputAction("Zoom", InputActionType.Value, expectedControlType: "Vector2");
        zoomAction.AddBinding("<Mouse>/scroll");

        if (target != null)
            smoothedPivot = target.position + targetOffset;

        if (ignoreBody == null && target != null)
            ignoreBody = target.GetComponentInParent<Rigidbody>();
    }

    private void OnEnable()
    {
        lookAction?.Enable();
        zoomAction?.Enable();
        ApplyCursorState();
    }

    private void OnDisable()
    {
        lookAction?.Disable();
        zoomAction?.Disable();
    }

    private void OnDestroy()
    {
        lookAction?.Dispose();
        zoomAction?.Dispose();
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
        ReadZoom();

        Vector3 pivot = target.position + targetOffset;

        // Smoothing the pivot rather than the camera position means the orbit stays exact
        // while the follow lags, which reads as weight rather than as sloppy aim.
        smoothedPivot = followSmoothing > 0f
            ? Vector3.SmoothDamp(smoothedPivot, pivot, ref followVelocity, followSmoothing)
            : pivot;

        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 direction = rotation * Vector3.back;   // from pivot toward the camera

        float targetDistance = avoidGeometry
            ? DistanceToObstruction(smoothedPivot, direction)
            : distance;

        // Pull in instantly, ease back out. A smoothed pull-in would leave the camera
        // inside geometry for several frames, which is exactly when the near clip plane
        // starts slicing through triangles.
        currentDistance = targetDistance < currentDistance
            ? targetDistance
            : Mathf.SmoothDamp(currentDistance, targetDistance, ref distanceVelocity, returnSmoothing);

        transform.SetPositionAndRotation(smoothedPivot + direction * currentDistance, rotation);
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

    /// <summary>
    /// How far the camera can sit from the pivot before something gets in the way.
    ///
    /// A SphereCast that starts already overlapping a collider reports a hit at distance
    /// zero with a zero normal. Taken at face value that slams the camera onto the pivot,
    /// and the next frame - no longer overlapping - it snaps back out. That flip-flop is
    /// the jitter you get when the camera grazes the ground, so a zero-distance hit is
    /// treated as "fully blocked" rather than "zero distance away".
    /// </summary>
    /// <summary>
    /// Scroll wheel zoom. A wheel notch reports 120 rather than 1, so only the sign is
    /// used - scaling by the raw value would fling the camera across the level per click.
    /// </summary>
    private void ReadZoom()
    {
        float scroll = zoomAction.ReadValue<Vector2>().y;

        if (Mathf.Abs(scroll) > 0.01f)
            wantedDistance = Mathf.Clamp(wantedDistance - Mathf.Sign(scroll) * zoomSpeed,
                                         minDistance, maxDistance);

        distance = zoomSmoothing > 0f
            ? Mathf.SmoothDamp(distance, wantedDistance, ref zoomVelocity, zoomSmoothing)
            : wantedDistance;
    }

    /// <summary>
    /// How far the camera can sit from the pivot before something gets in the way.
    ///
    /// A SphereCast that starts already overlapping a collider reports a hit at distance
    /// zero with a zero normal. Taken at face value that slams the camera onto the pivot,
    /// and the next frame - no longer overlapping - it snaps back out. That flip-flop is
    /// the jitter you get when the camera grazes the ground, so a zero-distance hit is
    /// treated as "fully blocked" rather than "zero distance away".
    /// </summary>
    private float DistanceToObstruction(Vector3 pivot, Vector3 direction)
    {
        int count = Physics.SphereCastNonAlloc(pivot, cameraRadius, direction, obstructionHits,
                                               distance, obstructionMask,
                                               QueryTriggerInteraction.Ignore);

        float nearest = distance;

        for (int i = 0; i < count; i++)
        {
            Collider hit = obstructionHits[i].collider;
            if (hit == null) continue;

            // Anything riding on the target is part of the thing we are looking AT, not
            // something in the way of it.
            if (ignoreBody != null && hit.attachedRigidbody == ignoreBody) continue;

            // A sweep that starts already overlapping reports distance zero with a zero
            // normal. Taken at face value that slams the camera onto the pivot, and the
            // next frame - no longer overlapping - it snaps back out. That flip-flop is the
            // jitter you get grazing the ground, so treat it as fully blocked instead.
            float d = obstructionHits[i].distance <= 1e-4f
                ? minObstructionDistance
                : Mathf.Max(obstructionHits[i].distance - collisionPadding, minObstructionDistance);

            if (d < nearest) nearest = d;
        }

        return Mathf.Clamp(nearest, minObstructionDistance, distance);
    }

    private void OnValidate()
    {
        distance = Mathf.Max(0.1f, distance);
        minDistance = Mathf.Max(0.1f, minDistance);
        maxDistance = Mathf.Max(minDistance + 0.1f, maxDistance);
        minObstructionDistance = Mathf.Clamp(minObstructionDistance, 0.05f, maxDistance);
        maxPitch = Mathf.Max(minPitch + 1f, maxPitch);
    }
}
