using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// A rolling-ball character controller for Unity's new Input System, split across two
/// referenced objects:
///
///   - Position Body  : the Rigidbody. Physics owns both its position and its rotation, so
///                      torque genuinely rolls it and attached colliders are swept rather
///                      than teleported.
///   - Rolling Object : a child holding the visuals. It inherits the body's rotation, so
///                      nothing here needs to drive it.
///
/// Controls:
///   - WASD / Left Stick : move relative to the camera
///   - Space (hold)      : charge a jump; the longer the hold, the bigger the jump
///
/// Rolling is driven by torque against friction, so the assembly's real mass and inertia
/// decide how it accelerates - a lump grown from absorbed blobs feels heavier for free.
/// </summary>
public class ClayCharacterController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The Rigidbody. Physics owns its rotation, so torque rolls it directly.")]
    [SerializeField] private Rigidbody positionBody;
    [Tooltip("Child holding the visuals. Inherits the body's rotation automatically.")]
    [SerializeField] private Transform rollingObject;

    [Header("Colliders")]
    [Tooltip("Physical collision radius, and the radius the ball actually rolls on.\n\n" +
             "Deliberately SMALLER than the visible mesh, so the character can sink into " +
             "surfaces by the difference - which is what gives the dent effect something to " +
             "flatten. The gap also decides how fast it appears to spin: contact at this " +
             "radius means friction forces v = omega * r, so a small value spins visibly " +
             "faster than the visible size suggests.")]
    [SerializeField] private float innerRadius = 0.4f;

    [Tooltip("Radius of the visible mesh. Used as an interaction trigger and as the " +
             "detection sphere for contact-driven dents.")]
    [SerializeField] private float outerRadius = 0.7f;

    [Tooltip("Build and maintain the two sphere colliders on the position body. Turn off if " +
             "you would rather author them by hand.")]
    [SerializeField] private bool manageColliders = true;

    [Tooltip("Physics material for the inner collider.\n\n" +
             "Physics rolling turns torque into travel THROUGH FRICTION, so this is not " +
             "optional there - on a slippery material the assembly just spins on the spot. " +
             "The colliders are built at runtime, which is why this is set here rather than " +
             "on a collider in the scene.")]
    [SerializeField] private PhysicsMaterial innerMaterial;

    [Tooltip("Physics material for the outer trigger. Rarely needed, since triggers do not " +
             "resolve contacts.")]
    [SerializeField] private PhysicsMaterial outerMaterial;

    private SphereCollider innerCollider;
    private SphereCollider outerCollider;

    // Radius used to convert movement into rolling. Starts at the ball's own radius and is
    // replaced by BlobMerger once the assembly grows, since a two-blob lump has no single
    // radius of its own.
    private float rollingRadius;

    /// <summary>Effective radius the assembly rolls at. Set by BlobMerger when blobs merge.</summary>
    public float RollingRadius
    {
        get => rollingRadius;
        set => rollingRadius = Mathf.Max(0.01f, value);
    }

    /// <summary>
    /// How much further down the ground check has to reach, because absorbed blobs hang
    /// below the body. Set by BlobMerger.
    /// </summary>
    public float GroundProbeExtension { get; set; }

    private readonly RaycastHit[] groundHits = new RaycastHit[8];

    [Header("Movement")]
    [Tooltip("How hard the assembly is driven toward its target roll speed.\n\n" +
             "Applied as real torque, so a heavier assembly genuinely accelerates more " +
             "slowly. Note this is usually NOT the limiting value - see Traction Limit.")]
    [SerializeField] private float rollTorque = 60f;

    [Tooltip("How hard it brakes when there is no input. 0 coasts until friction stops it.")]
    [SerializeField] private float brakeTorque = 60f;

    [Tooltip("Below this spin, with no input, the assembly is snapped to a stop.\n\n" +
             "Braking torque is proportional to how fast it is spinning, so it decays " +
             "toward zero without ever arriving. What is left is far too slow to read as " +
             "movement but perfectly visible as a slow turn on the spot.")]
    [SerializeField] private float restAngularThreshold = 0.35f;

    [Tooltip("Matching threshold for drift, in metres per second.")]
    [SerializeField] private float restLinearThreshold = 0.15f;

    [Tooltip("THE responsiveness dial. Grip available for rolling, as a multiple of real " +
             "friction.\n\n" +
             "Torque is capped at what the contact patch could transmit, so this - not Roll " +
             "Torque - is almost always what limits acceleration. A ball driven purely by " +
             "friction cannot accelerate faster than mu * g, about 9.8 m/s at friction 1, " +
             "which is why realistic values feel heavy.\n\n" +
             "1 is physically honest. Raise it for arcade grip: 4 to 6 feels light and " +
             "responsive without the ball visibly slipping.")]
    [SerializeField] private float tractionLimit = 5f;

    [Tooltip("Acceleration available while airborne, where there is no friction to roll " +
             "against and torque would do nothing.")]
    [SerializeField] private float airControl = 12f;

    [Tooltip("Fastest horizontal speed air control alone may reach, as a multiple of Move " +
             "Speed.\n\n" +
             "Without a cap every airborne moment adds free acceleration, so repeatedly " +
             "jumping builds speed indefinitely. Above the cap steering still works, it " +
             "just cannot add any more speed.")]
    [SerializeField] private float airSpeedCap = 1.05f;

    [Tooltip("Camera that movement is relative to. W goes away from it. Leave empty to " +
             "find one automatically, or to fall back to world axes if there is none.")]
    [SerializeField] private ThirdPersonCamera steeringCamera;

    [Tooltip("Maximum move speed in metres per second.")]
    [SerializeField] private float moveSpeed = 6f;

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

    [Header("Squash & Stretch")]
    [Tooltip("Deformation driven by this controller. Leave empty to search children.")]
    [SerializeField] private SquashStretch squash;

    [Tooltip("How flat the character gets at full jump charge, as anticipation.")]
    [SerializeField] private float chargeSquash = 0.3f;

    [Tooltip("Stretch impulse on launch, scaled by how charged the jump was.")]
    [SerializeField] private float launchImpulse = 6f;

    [Tooltip("Squash impulse on landing, per metre per second of impact speed.")]
    [SerializeField] private float impactImpulse = 0.5f;

    [Tooltip("Impact speed treated as the hardest possible landing, for scaling wobble.")]
    [SerializeField] private float referenceImpactSpeed = 12f;

    [Tooltip("Wobble length for the gentlest impact or weakest jump, in seconds.")]
    [SerializeField] private float minWobbleDuration = 0.25f;

    [Tooltip("Wobble length for the hardest impact or strongest jump, in seconds.")]
    [SerializeField] private float maxWobbleDuration = 1.1f;

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

    // Landing detection. The velocity is sampled at the END of each physics step, because
    // by the time the solver reports us grounded it has already cancelled the fall.
    private bool wasGrounded;
    private float previousVerticalSpeed;

    private void Awake()
    {
        if (positionBody == null)
            Debug.LogError("ClayCharacterController: Position Body is not assigned.", this);
        else
        {
            // Physics owns rotation, so torque genuinely rolls the body and attached
            // colliders are swept rather than teleported.
            positionBody.freezeRotation = false;

            // Physics steps at a fixed rate while rendering does not, so without
            // interpolation the transform only moves on physics ticks and the renderer
            // keeps sampling stale positions. That reads as per-frame jitter, and is
            // glaring once a camera is locked to the body.
            if (positionBody.interpolation == RigidbodyInterpolation.None)
                positionBody.interpolation = RigidbodyInterpolation.Interpolate;
        }

        if (rollingObject == null)
            Debug.LogError("ClayCharacterController: Rolling Object is not assigned.", this);

        if (manageColliders) SetUpColliders();

        // The radius that touches the ground, not the visible one. Friction ties travel to
        // spin through the contact radius, so the target spin has to be derived from it.
        rollingRadius = innerRadius;

        if (steeringCamera == null) steeringCamera = FindFirstObjectByType<ThirdPersonCamera>();
        if (squash == null) squash = GetComponentInChildren<SquashStretch>();

        // The inner collider is created at runtime, so it cannot be assigned in the
        // inspector. Its bottom is the ground contact, which is where the squash should
        // pivot - otherwise flattening sinks the character halfway through the floor.
        if (squash != null && innerCollider != null && squash.pivotCollider == null)
            squash.pivotCollider = innerCollider;

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
        if (innerMaterial != null) innerCollider.sharedMaterial = innerMaterial;

        outerCollider = FindOrCreate(bodyTransform, "Collider (Outer)");
        outerCollider.radius = outerRadius;
        outerCollider.isTrigger = true;   // interaction and detection only, never blocks
        if (outerMaterial != null) outerCollider.sharedMaterial = outerMaterial;

        if (innerMaterial == null)
        {
            Debug.LogWarning("ClayCharacterController: no Inner Material is assigned. Torque " +
                             "only becomes travel through friction, so with Unity's default " +
                             "material the assembly may spin without moving.", this);
        }

        if (outerRadius < innerRadius)
            Debug.LogWarning("ClayCharacterController: Outer Radius is smaller than Inner " +
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

    /// <summary>The physical collider the assembly rolls on. Null until Awake has run.</summary>
    public SphereCollider InnerCollider => innerCollider;

    /// <summary>The interaction trigger around the visible surface. Null until Awake has run.</summary>
    public SphereCollider OuterCollider => outerCollider;

    private void OnValidate()
    {
        innerRadius = Mathf.Max(0.01f, innerRadius);
        outerRadius = Mathf.Max(innerRadius, outerRadius);

        if (innerCollider != null) innerCollider.radius = innerRadius;
        if (outerCollider != null) outerCollider.radius = outerRadius;
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
        {
            chargeTime = Mathf.Min(chargeTime + Time.deltaTime, maxChargeTime);

            // Anticipation: sink toward flat as the charge builds. Volume preservation in
            // the shader spreads the character outward as it flattens, so this reads as a
            // crouch rather than a shrink.
            if (squash != null)
                squash.SetHold(-chargeSquash * Mathf.Clamp01(chargeTime / maxChargeTime));
        }
    }

    private void FixedUpdate()
    {
        if (positionBody == null) return;

        CheckGrounded();
        DetectLanding();

        Move();
        UpdateJumpArc();

        // Sampled last so it holds the speed we were travelling at during this step, before
        // a collision next step wipes it.
        previousVerticalSpeed = positionBody.linearVelocity.y;
        wasGrounded = isGrounded;
    }

    /// <summary>
    /// Fires a squash impulse scaled by how hard the character hit.
    ///
    /// The impact speed comes from the PREVIOUS step: once the solver has resolved the
    /// contact, vertical velocity is already back to roughly zero, so reading it after the
    /// fact reports every landing as gentle.
    /// </summary>
    private void DetectLanding()
    {
        if (squash == null) return;
        if (isGrounded == wasGrounded) return;
        if (!isGrounded) return;

        float impactSpeed = -previousVerticalSpeed;
        if (impactSpeed <= 0.1f) return;

        float severity = Mathf.Clamp01(impactSpeed / Mathf.Max(referenceImpactSpeed, 0.01f));

        // Harder impacts hit deeper AND ring for longer, rather than only being bigger.
        squash.SetHold(0f);
        squash.AddImpulse(-impactImpulse * impactSpeed,
                          Mathf.Lerp(minWobbleDuration, maxWobbleDuration, severity));
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

        MovePhysical(desiredDir);
    }

    /// <summary>
    /// Rolls the body with torque and lets friction turn that into travel.
    ///
    /// Driven toward a TARGET ANGULAR VELOCITY rather than pushed with constant torque:
    /// the target is whatever spin would carry the assembly at moveSpeed, so it accelerates
    /// hard when far from that and eases off as it arrives. No separate speed cap is
    /// needed, and it cannot over-drive into a wheelspin.
    ///
    /// Torque is applied in Force mode, so the assembly's real inertia decides how quickly
    /// it responds. That is what makes a growing lump feel heavier without any of it being
    /// scripted - and it is why Acceleration mode, which ignores inertia entirely, felt
    /// both sluggish and unchanging as blobs were absorbed.
    /// </summary>
    private void MovePhysical(Vector3 desiredDir)
    {
        bool steering = desiredDir.sqrMagnitude > 1e-4f;

        if (!isGrounded)
        {
            ApplyAirControl(desiredDir);
            return;
        }

        // The radius that actually touches the ground. Friction ties travel to spin through
        // THIS, not the visible size - so using anything else here silently caps the top
        // speed at the ratio between the two.
        float radius = Mathf.Max(rollingRadius, 0.01f);

        // The spin that would carry the assembly at moveSpeed: omega = v / r, about the
        // axis across the direction of travel.
        Vector3 targetAngular = steering
            ? Vector3.Cross(Vector3.up, desiredDir) * (moveSpeed / radius)
            : Vector3.zero;

        if (!steering && TryComeToRest()) return;

        Vector3 error = targetAngular - positionBody.angularVelocity;

        float strength = steering ? rollTorque : brakeTorque;
        if (strength <= 0f) return;

        Vector3 torque = error * strength;

        // Cap at what friction can actually transmit. Beyond mu * m * g * r the contact
        // patch slips, which reads as spinning on the spot rather than accelerating.
        float maxTorque = tractionLimit * positionBody.mass
                          * Physics.gravity.magnitude * radius;

        if (torque.magnitude > maxTorque) torque = torque.normalized * maxTorque;

        positionBody.AddTorque(torque, ForceMode.Force);
    }

    /// <summary>
    /// Steers while airborne, where there is no friction to roll against.
    ///
    /// Capped, because otherwise every airborne moment is free acceleration with none of
    /// the traction limit that holds ground speed in check - so repeatedly jumping builds
    /// speed without limit. Past the cap the push is projected onto the turn, which keeps
    /// air steering responsive without letting it add speed.
    /// </summary>
    private void ApplyAirControl(Vector3 desiredDir)
    {
        if (desiredDir.sqrMagnitude < 1e-4f) return;

        Vector3 velocity = positionBody.linearVelocity;
        Vector3 horizontal = new Vector3(velocity.x, 0f, velocity.z);

        float cap = moveSpeed * Mathf.Max(airSpeedCap, 0.1f);
        Vector3 push = desiredDir;

        if (horizontal.magnitude >= cap)
        {
            // Strip the component that would speed us up, leaving only the part that turns.
            Vector3 along = Vector3.Project(push, horizontal.normalized);
            if (Vector3.Dot(along, horizontal) > 0f) push -= along;

            if (push.sqrMagnitude < 1e-6f) return;
        }

        positionBody.AddForce(push * airControl, ForceMode.Acceleration);
    }

    /// <summary>
    /// Snaps the assembly to a full stop once it is nearly there.
    ///
    /// A proportional brake weakens as it succeeds, so it approaches zero asymptotically
    /// and leaves a slow residual turn that never resolves. Below a threshold it is
    /// cheaper and cleaner to just stop, and lets the body sleep rather than being woken
    /// by torque every step.
    /// </summary>
    private bool TryComeToRest()
    {
        Vector3 velocity = positionBody.linearVelocity;
        Vector3 horizontal = new Vector3(velocity.x, 0f, velocity.z);

        if (positionBody.angularVelocity.magnitude > restAngularThreshold) return false;
        if (horizontal.magnitude > restLinearThreshold) return false;

        positionBody.angularVelocity = Vector3.zero;

        // Vertical is left alone, so gravity and any jump arc still apply.
        positionBody.linearVelocity = new Vector3(0f, velocity.y, 0f);

        return true;
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

    private void CheckGrounded()
    {
        // Cast a small sphere just below the ball to detect ground. Uses the PHYSICAL
        // radius, not the visual one, so it agrees with what actually collides.
        float radius = innerRadius * 0.9f;

        // Absorbed blobs hang below the body and their colliders belong to the same
        // Rigidbody, so a SphereCast started here begins overlapping them - and Unity does
        // not report colliders a sweep starts inside. Without extending the reach and
        // skipping our own assembly, standing on a blob reads as airborne and jumping
        // becomes impossible.
        float distance = (innerRadius - radius) + groundCheckPadding + 0.05f
                         + Mathf.Max(0f, GroundProbeExtension);

        int count = Physics.SphereCastNonAlloc(positionBody.position, radius, Vector3.down,
                                               groundHits, distance, groundMask,
                                               QueryTriggerInteraction.Ignore);

        isGrounded = false;

        for (int i = 0; i < count; i++)
        {
            // Parts of our own assembly are not ground.
            if (groundHits[i].collider == null) continue;
            if (groundHits[i].collider.attachedRigidbody == positionBody) continue;

            isGrounded = true;
            break;
        }
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

        // Release the anticipation crouch and fire a stretch scaled by jump power, so a
        // charged launch stretches harder and wobbles for longer than a tap.
        if (squash != null)
        {
            squash.SetHold(0f);
            squash.AddImpulse(launchImpulse * t,
                              Mathf.Lerp(minWobbleDuration, maxWobbleDuration, t));
        }

        // The arc is authored, so gravity must not also act on the body while it runs.
        positionBody.useGravity = false;
    }
}