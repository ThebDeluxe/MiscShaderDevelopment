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

    [Header("Movement")]
    [Tooltip("Maximum move speed in metres per second.")]
    [SerializeField] private float moveSpeed = 6f;
    [Tooltip("How quickly the ball accelerates toward the target velocity.")]
    [SerializeField] private float acceleration = 40f;

    [Header("Jump")]
    [Tooltip("Upward velocity applied for the shortest (tap) jump.")]
    [SerializeField] private float minJumpSpeed = 4f;
    [Tooltip("Upward velocity applied for a fully-charged jump.")]
    [SerializeField] private float maxJumpSpeed = 12f;
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

    // Follower tracking: the body's last grounded height (so jumps are ignored)
    // and each follower's starting height offset from that ground level.
    private float lastGroundedY;
    private float[] followerYOffsets;

    private void Awake()
    {
        if (positionBody == null)
            Debug.LogError("BallCharacterController: Position Body is not assigned.", this);
        else
            positionBody.freezeRotation = true; // this body only moves position

        if (rollingObject == null)
            Debug.LogError("BallCharacterController: Rolling Object is not assigned.", this);

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
        moveAction.Enable();
        jumpAction.Enable();
    }

    private void OnDisable()
    {
        moveAction.Disable();
        jumpAction.Disable();
    }

    private void OnDestroy()
    {
        jumpAction.started -= OnJumpStarted;
        jumpAction.canceled -= OnJumpReleased;
        moveAction.Dispose();
        jumpAction.Dispose();
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
        // WASD maps to world X/Z (axial) movement.
        Vector3 desiredDir = new Vector3(moveInput.x, 0f, moveInput.y);
        if (desiredDir.sqrMagnitude > 1f) desiredDir.Normalize();

        Vector3 targetVelocity = desiredDir * moveSpeed;

        // Only steer the horizontal velocity; leave vertical (gravity/jump) alone.
        Vector3 velocity = positionBody.linearVelocity;
        Vector3 horizontal = new Vector3(velocity.x, 0f, velocity.z);
        horizontal = Vector3.MoveTowards(horizontal, targetVelocity, acceleration * Time.fixedDeltaTime);

        positionBody.linearVelocity = new Vector3(horizontal.x, velocity.y, horizontal.z);
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
        // Cast a small sphere just below the ball to detect ground.
        float castDistance = groundCheckPadding + 0.05f;
        isGrounded = Physics.SphereCast(
            positionBody.position,
            ballRadius * 0.9f,
            Vector3.down,
            out _,
            (ballRadius - ballRadius * 0.9f) + castDistance,
            groundMask,
            QueryTriggerInteraction.Ignore);
    }

    private void OnJumpStarted(InputAction.CallbackContext ctx)
    {
        // Only start charging if we're on the ground.
        if (isGrounded)
        {
            isCharging = true;
            chargeTime = 0f;
        }
    }

    private void OnJumpReleased(InputAction.CallbackContext ctx)
    {
        if (!isCharging || positionBody == null) return;
        isCharging = false;

        // Scale jump strength by how long Space was held.
        float t = Mathf.Clamp01(chargeTime / maxChargeTime);
        float jumpSpeed = Mathf.Lerp(minJumpSpeed, maxJumpSpeed, t);

        // Replace vertical velocity so the jump height is consistent.
        Vector3 velocity = positionBody.linearVelocity;
        velocity.y = jumpSpeed;
        positionBody.linearVelocity = velocity;
    }
}