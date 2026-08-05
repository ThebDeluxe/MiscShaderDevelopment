using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// A basic rolling-ball character controller for Unity's new Input System, split
/// across two referenced objects:
///
///   - Position Body : a Rigidbody that ONLY moves position (walking & jumping).
///                     Its rotation is frozen so physics never spins it.
///   - Rolling Object : a separate Transform that receives the visual rolling
///                       rotation. Make this a child of the Position Body so it
///                       follows the position automatically.
///
/// Controls:
///   - WASD / Left Stick : move in world axial directions
///   - Space (hold)      : charge a jump; the longer the hold, the bigger the jump
///
/// Rolling matches travel exactly: angularSpeed = linearSpeed / radius.
/// Set Ball Radius to match the visible ball's radius.
/// </summary>
public class BallCharacterController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Rigidbody that only moves position (walking & jumping). Rotation is frozen.")]
    [SerializeField] private Rigidbody positionBody;
    [Tooltip("Object that visually rolls. Best as a child of the Position Body so it follows position.")]
    [SerializeField] private Transform rollingObject;
    [Tooltip("Objects that follow the body's position (including ground height) but ignore the upward jump motion. Each keeps its starting height offset.")]
    [SerializeField] private List<GameObject> horizontalFollowers = new List<GameObject>();

    [Header("Ball")]
    [Tooltip("Radius of the ball, in metres. Used to convert movement speed into the correct rolling rotation.")]
    [SerializeField] private float ballRadius = 0.5f;

    [Header("Colliders")]
    [Tooltip("Physical collision radius. Deliberately SMALLER than the visible mesh, so the " +
             "character can visually sink into surfaces by the difference - which is what " +
             "gives the dent effect something to flatten.")]
    [SerializeField] private float innerRadius = 0.4f;

    [Tooltip("Approximate radius of the visible mesh. Used as a trigger for clicking and " +
             "other interaction, and as the detection sphere for contact-driven dents.")]
    [SerializeField] private float outerRadius = 0.7f;

    [Tooltip("Build and maintain the two sphere colliders on the position body. Turn off if " +
             "you would rather author them by hand.")]
    [SerializeField] private bool manageColliders = true;

    private SphereCollider innerCollider;
    private SphereCollider outerCollider;

    [Header("Movement")]
    [Tooltip("Camera that movement is relative to. W goes away from it. Leave empty to " +
             "find one automatically, or to fall back to world axes if there is none.")]
    [SerializeField] private ThirdPersonCamera steeringCamera;

    [Tooltip("Maximum move speed in metres per second.")]
    [SerializeField] private float moveSpeed = 6f;
    [Tooltip("How quickly the ball accelerates toward the target velocity.")]
    [SerializeField] private float acceleration = 40f;

    [Header("Jump")]
    [Tooltip("Peak height of the shortest (tap) jump, in metres.")]
    [SerializeField] private float minJumpHeight = 1f;

    [Tooltip("Peak height of a fully-charged jump, in metres.")]
    [SerializeField] private float maxJumpHeight = 3.5f;

    [Tooltip("Time in the air for the shortest jump, in seconds.")]
    [SerializeField] private float minJumpDuration = 0.45f;

    [Tooltip("Time in the air for a fully-charged jump, in seconds.")]
    [SerializeField] private float maxJumpDuration = 0.85f;

    [Tooltip("Seconds of holding Space needed to reach the maximum jump.")]
    [SerializeField] private float maxChargeTime = 1f;

    [Header("Ground Check")]
    [Tooltip("Which layers count as ground.")]
    [SerializeField] private LayerMask groundMask = ~0;
    [Tooltip("Extra distance below the ball used when testing for ground.")]
    [SerializeField] private float groundCheckPadding = 0.1f;

    // Input System actions (created in code so no .inputactions asset is required).
    private InputAction moveAction;
    private InputAction jumpAction;

    private Vector2 moveInput;
    private bool isGrounded;

    // Jump charging state.
    private bool isCharging;
    private float chargeTime;

    // Parabolic jump state. The arc is authored, not simulated, so gravity is switched off
    // for its duration and the vertical velocity is driven from the curve instead.
    private bool isJumping;
    private float jumpTimer;
    private float jumpDuration;
    private float jumpPeak;

    // Follower tracking: the body's last grounded height (so jumps are ignored)
    // and each follower's starting height offset from that ground level.
    private float lastGroundedY;
    private float[] followerYOffsets;

    private void Awake()
    {
        if (positionBody == null)
            Debug.LogError("BallCharacterController: Position Body is not assigned.", this);
        else
        {
            positionBody.freezeRotation = true; // this body only moves position

            // Physics steps at a fixed rate while rendering does not, so without
            // interpolation the transform only moves on physics ticks and the renderer
            // keeps sampling stale positions. That reads as per-frame jitter, and is
            // glaring once a camera is locked to the body.
            if (positionBody.interpolation == RigidbodyInterpolation.None)
                positionBody.interpolation = RigidbodyInterpolation.Interpolate;
        }

        if (rollingObject == null)
            Debug.LogError("BallCharacterController: Rolling Object is not assigned.", this);

        if (manageColliders) SetUpColliders();

        if (steeringCamera == null) steeringCamera = FindFirstObjectByType<ThirdPersonCamera>();

        // WASD + arrow keys + left stick -> a 2D vector.
        moveAction = new InputAction("Move", InputActionType.Value, expectedControlType: "Vector2");
        moveAction.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w")
            .With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");
        moveAction.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/upArrow")
            .With("Down", "<Keyboard>/downArrow")
            .With("Left", "<Keyboard>/leftArrow")
            .With("Right", "<Keyboard>/rightArrow");
        moveAction.AddBinding("<Gamepad>/leftStick");

        // Space (or gamepad south button) -> jump.
        jumpAction = new InputAction("Jump", InputActionType.Button);
        jumpAction.AddBinding("<Keyboard>/space");
        jumpAction.AddBinding("<Gamepad>/buttonSouth");

        jumpAction.started += OnJumpStarted;   // pressed down -> begin charging
        jumpAction.canceled += OnJumpReleased;  // released -> launch
    }

    /// <summary>
    /// Builds both spheres on the POSITION BODY, never on the rolling mesh.
    ///
    /// A collider on the rolling object would spin with it. A sphere is rotation-invariant
    /// so collision would still work, but anything reading the collider's transform - like
    /// contact-driven dent sources, whose press axis and square extent come from it - would
    /// see that rotation and smear.
    /// </summary>
    private void SetUpColliders()
    {
        if (positionBody == null) return;

        Transform bodyTransform = positionBody.transform;

        innerCollider = FindOrCreate(bodyTransform, "Collider (Inner)");
        innerCollider.radius = innerRadius;
        innerCollider.isTrigger = false;

        outerCollider = FindOrCreate(bodyTransform, "Collider (Outer)");
        outerCollider.radius = outerRadius;
        outerCollider.isTrigger = true;   // interaction and detection only, never blocks

        if (outerRadius < innerRadius)
            Debug.LogWarning("BallCharacterController: Outer Radius is smaller than Inner " +
                             "Radius, so the character cannot sink into anything.", this);
    }

    private static SphereCollider FindOrCreate(Transform parent, string childName)
    {
        Transform existing = parent.Find(childName);

        if (existing == null)
        {
            var go = new GameObject(childName);
            go.transform.SetParent(parent, false);
            existing = go.transform;
        }

        // Deliberately NOT using ?? here. Unity overloads == to report missing and destroyed
        // objects as null, but ?? and ?. use the runtime's real null check and bypass that
        // overload, so they hand back a fake-null component that throws on first use.
        if (!existing.TryGetComponent(out SphereCollider collider))
            collider = existing.gameObject.AddComponent<SphereCollider>();

        // Children with no Rigidbody of their own belong to the nearest Rigidbody ancestor,
        // which is what keeps both spheres attached to the position body.
        return collider;
    }

    private void OnValidate()
    {
        innerRadius = Mathf.Max(0.01f, innerRadius);
        outerRadius = Mathf.Max(innerRadius, outerRadius);

        if (innerCollider != null) innerCollider.radius = innerRadius;
        if (outerCollider != null) outerCollider.radius = outerRadius;
    }

    private void Start()
    {
        // Capture the ground reference height and each follower's offset from it,
        // so followers keep their relative placement while tracking the ground.
        lastGroundedY = positionBody != null ? positionBody.position.y : 0f;

        followerYOffsets = new float[horizontalFollowers.Count];
        for (int i = 0; i < horizontalFollowers.Count; i++)
        {
            GameObject follower = horizontalFollowers[i];
            followerYOffsets[i] = follower != null
                ? follower.transform.position.y - lastGroundedY
                : 0f;
        }
    }

    private void OnEnable()
    {
        // Guarded: if Awake failed these were never created, and the resulting
        // NullReferenceException would bury the real error.
        moveAction?.Enable();
        jumpAction?.Enable();
    }

    private void OnDisable()
    {
        moveAction?.Disable();
        jumpAction?.Disable();

        // Gravity is switched off for the duration of a jump arc. Being disabled mid-jump
        // would otherwise leave the body floating forever.
        if (isJumping) EndJump();
    }

    private void OnDestroy()
    {
        if (jumpAction != null)
        {
            jumpAction.started -= OnJumpStarted;
            jumpAction.canceled -= OnJumpReleased;
            jumpAction.Dispose();
        }

        moveAction?.Dispose();
    }

    private void Update()
    {
        moveInput = moveAction.ReadValue<Vector2>();

        // Build up the jump charge while Space is held (capped at maxChargeTime).
        if (isCharging)
            chargeTime = Mathf.Min(chargeTime + Time.deltaTime, maxChargeTime);
    }

    private void FixedUpdate()
    {
        if (positionBody == null) return;

        CheckGrounded();

        // Remember the body's height whenever it's on the ground, so followers can
        // track terrain height without being carried upward by a jump.
        if (isGrounded)
            lastGroundedY = positionBody.position.y;

        Move();
        UpdateJumpArc();
        Roll();
    }

    private void LateUpdate()
    {
        if (positionBody == null) return;

        // Followers track the body's horizontal position and its grounded height,
        // so they stay on top of terrain, but the jump (upward Y) is ignored.
        Vector3 bodyPos = positionBody.position;
        for (int i = 0; i < horizontalFollowers.Count; i++)
        {
            GameObject follower = horizontalFollowers[i];
            if (follower == null) continue;

            float yOffset = (followerYOffsets != null && i < followerYOffsets.Length)
                ? followerYOffsets[i]
                : 0f;

            follower.transform.position = new Vector3(bodyPos.x, lastGroundedY + yOffset, bodyPos.z);
        }
    }

    private void Move()
    {
        // Steer relative to the camera, so W always means "away from the viewer".
        Vector3 forward = Vector3.forward;
        Vector3 right = Vector3.right;

        if (steeringCamera != null)
        {
            forward = steeringCamera.PlanarForward;
            right = steeringCamera.PlanarRight;
        }

        Vector3 desiredDir = right * moveInput.x + forward * moveInput.y;
        if (desiredDir.sqrMagnitude > 1f) desiredDir.Normalize();

        Vector3 targetVelocity = desiredDir * moveSpeed;

        // Only steer the horizontal velocity; leave vertical (gravity/jump) alone.
        Vector3 velocity = positionBody.linearVelocity;
        Vector3 horizontal = new Vector3(velocity.x, 0f, velocity.z);
        horizontal = Vector3.MoveTowards(horizontal, targetVelocity, acceleration * Time.fixedDeltaTime);

        positionBody.linearVelocity = new Vector3(horizontal.x, velocity.y, horizontal.z);
    }

    /// <summary>
    /// Drives the vertical velocity from an authored parabola rather than letting gravity
    /// produce the arc, so jump height and airtime are exactly what was asked for and do
    /// not shift with the project's gravity setting or the body's mass.
    ///
    /// Velocity is set rather than position: the trajectory is still authored, but the
    /// solver keeps resolving collisions, so the character cannot punch through a ceiling.
    ///
    ///     y(t)  = peak * 4t(1 - t)          for t in 0..1
    ///     y'(t) = peak * 4(1 - 2t) / duration
    /// </summary>
    private void UpdateJumpArc()
    {
        if (!isJumping) return;

        jumpTimer += Time.fixedDeltaTime;
        float t = jumpTimer / jumpDuration;

        if (t >= 1f)
        {
            EndJump();
            return;
        }

        // Landing early on higher ground should stop the arc rather than let it keep
        // driving downward. Only checked past the apex, since we start on the ground.
        if (t > 0.5f && isGrounded)
        {
            EndJump();
            return;
        }

        float verticalSpeed = jumpPeak * 4f * (1f - 2f * t) / jumpDuration;

        Vector3 velocity = positionBody.linearVelocity;
        positionBody.linearVelocity = new Vector3(velocity.x, verticalSpeed, velocity.z);
    }

    private void EndJump()
    {
        isJumping = false;
        positionBody.useGravity = true;
    }

    private void Roll()
    {
        if (rollingObject == null || ballRadius <= 0.0001f) return;

        // Roll the visual object to match the body's horizontal travel:
        //   angularSpeed (rad/s) = linearSpeed / radius
        //   rotation axis        = up x moveDirection
        Vector3 horizontal = new Vector3(positionBody.linearVelocity.x, 0f, positionBody.linearVelocity.z);
        float speed = horizontal.magnitude;
        if (speed <= 0.001f) return;

        Vector3 axis = Vector3.Cross(Vector3.up, horizontal.normalized);
        float degrees = (speed / ballRadius) * Mathf.Rad2Deg * Time.fixedDeltaTime;
        rollingObject.Rotate(axis * degrees, Space.World);
    }

    private void CheckGrounded()
    {
        // Cast a small sphere just below the ball to detect ground. Uses the PHYSICAL
        // radius, not the visual one, so it agrees with what actually collides.
        float castDistance = groundCheckPadding + 0.05f;
        isGrounded = Physics.SphereCast(
            positionBody.position,
            innerRadius * 0.9f,
            Vector3.down,
            out _,
            (innerRadius - innerRadius * 0.9f) + castDistance,
            groundMask,
            QueryTriggerInteraction.Ignore);
    }

    private void OnJumpStarted(InputAction.CallbackContext ctx)
    {
        // Only start charging if we're on the ground and not already airborne.
        if (isGrounded && !isJumping)
        {
            isCharging = true;
            chargeTime = 0f;
        }
    }

    private void OnJumpReleased(InputAction.CallbackContext ctx)
    {
        if (!isCharging || positionBody == null) return;
        isCharging = false;

        // Scale both height and airtime by how long Space was held.
        float t = Mathf.Clamp01(chargeTime / maxChargeTime);
        jumpPeak = Mathf.Lerp(minJumpHeight, maxJumpHeight, t);
        jumpDuration = Mathf.Lerp(minJumpDuration, maxJumpDuration, t);

        jumpTimer = 0f;
        isJumping = true;

        // The arc is authored, so gravity must not also act on the body while it runs.
        positionBody.useGravity = false;
    }
}