using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Absorbs nearby clay blobs into the player, and keeps the resulting assembly coherent.
///
/// Three jobs, none of which need new deformation machinery:
///
///  - Ownership. Reparenting a blob under the rolling object makes its colliders part of
///    the player's Rigidbody, so they stop colliding with each other and fold into the
///    combined mass automatically.
///
///  - Pivot. A two-blob assembly does not rotate about the original character's centre, so
///    the rolling pivot and the Rigidbody's centre of mass are both moved to the combined
///    centre. Children are restored to their world positions afterwards, otherwise moving
///    the pivot would drag everything with it.
///
///  - Interfaces. Two clay balls pressed together share a FLAT disc, not a spherical dent.
///    Each blob is told about its siblings so their contact sources emit a Plane at the
///    radical plane between the pair rather than the curved surface they would otherwise
///    see.
/// </summary>
[DefaultExecutionOrder(-40)]
public class BlobMerger : MonoBehaviour
{
    [Header("Assembly")]
    [Tooltip("Rigidbody the assembly belongs to. Falls back to this object's.")]
    public Rigidbody body;

    [Tooltip("Object blobs are parented to, so they roll with the assembly.")]
    public Transform rollingObject;

    [Tooltip("The player's own deformation driver, so it learns about its new siblings.")]
    public DentContactSource ownContactSource;

    [Tooltip("Controller told about the assembly's effective rolling radius.")]
    public ClayCharacterController controller;

    [Tooltip("Deformation driver. Absorbed blobs are registered with it so the whole " +
             "assembly squashes together, about one shared pivot.")]
    public SquashStretch squash;

    [Header("Pickup")]
    [Tooltip("Layers searched for blobs.")]
    public LayerMask blobMask = ~0;

    [Tooltip("Extra slack on the touch test, so a blob is absorbed on contact rather than " +
             "needing to overlap.")]
    public float pickupPadding = 0.05f;

    [Tooltip("Where a blob has to reach before it is absorbed, between the physical " +
             "collider and the visible surface.\n\n" +
             "0 = the inner collider. Blobs sit close to what the assembly actually rolls " +
             "on, which keeps fast movement smooth, but they have to visibly overlap first.\n\n" +
             "1 = the visible surface. Picks up on contact as it looks, but attached blobs " +
             "stick out past the rolling surface and make it bumpy.\n\n" +
             "0.5 is a reasonable middle.")]
    [Range(0f, 1f)] public float pickupRadiusBlend = 0.5f;

    [Tooltip("Physical radius, taken from the controller's inner collider when one is found.")]
    public float innerRadius = 0.4f;

    [Header("Throwing")]
    [Tooltip("Camera used for aiming. Leave empty to use the main camera.")]
    public Camera aimCamera;

    [Tooltip("Aim at the mouse cursor rather than the screen centre. Only useful if the " +
             "cursor is unlocked - the third person camera locks it by default.")]
    public bool aimAtCursor = false;

    [Tooltip("How far to look for something to aim at before just firing down the ray.")]
    public float maxAimDistance = 100f;

    [Tooltip("Layers the aim ray can land on.")]
    public LayerMask aimMask = ~0;

    [Tooltip("Launch speed, in metres per second.")]
    public float throwSpeed = 14f;

    [Tooltip("Extra upward speed added to the launch, for a bit of arc.")]
    public float throwArc = 2f;

    [Tooltip("Seconds spent turning the assembly so the chosen blob faces the target " +
             "before it is released. 0 fires immediately.")]
    public float spinDuration = 0.12f;

    [Tooltip("Seconds a thrown blob cannot be re-absorbed, so it actually gets away.")]
    public float pickupLockSeconds = 0.7f;

    [Header("Debug")]
    public bool drawGizmos = true;

    readonly List<ClayBlob> merged = new List<ClayBlob>();
    readonly Collider[] overlaps = new Collider[16];
    readonly List<DentContactSource> siblingBuffer = new List<DentContactSource>();

    InputAction throwAction;
    bool throwing;
    readonly RaycastHit[] aimHits = new RaycastHit[16];

    /// <summary>Blobs currently part of the assembly.</summary>
    public int MergedCount => merged.Count;

    /// <summary>Radius of a sphere with the assembly's combined volume. Drives rolling.</summary>
    public float EffectiveRadius { get; private set; }

    void Awake()
    {
        if (body == null) body = GetComponent<Rigidbody>();
        if (ownContactSource == null) ownContactSource = GetComponentInChildren<DentContactSource>();
        if (controller == null) controller = GetComponent<ClayCharacterController>();
        if (squash == null) squash = GetComponentInChildren<SquashStretch>();

        EffectiveRadius = ownContactSource != null ? ownContactSource.visualRadius : 0.5f;

        // Pick up the controller's real collision radius, so pickup lines up with what the
        // assembly actually rolls on rather than with the visible surface. Read in Start,
        // because the controller builds its colliders in Awake.
        if (controller != null && controller.InnerCollider != null)
            innerRadius = controller.InnerCollider.radius;

        throwAction = new InputAction("Throw", InputActionType.Button);
        throwAction.AddBinding("<Mouse>/leftButton");
        throwAction.AddBinding("<Gamepad>/rightTrigger");
        throwAction.performed += _ => TryThrow();
    }

    void OnEnable() => throwAction?.Enable();
    void OnDisable() => throwAction?.Disable();
    void OnDestroy() => throwAction?.Dispose();

    void FixedUpdate()
    {
        if (ownContactSource == null || rollingObject == null) return;

        LookForBlobs();

        // Recomputed every step, not just on pickup: the assembly rolls, so a blob that was
        // underneath swings out to the side and back again constantly. A value captured at
        // merge time is wrong within a fraction of a second.
        RefreshGroundProbe();
    }

    void LookForBlobs()
    {
        Vector3 centre = AssemblyCentre();
        float reach = EffectiveRadius + LargestBlobRadius() + pickupPadding;

        int count = Physics.OverlapSphereNonAlloc(centre, reach, overlaps, blobMask,
                                                  QueryTriggerInteraction.Ignore);

        for (int i = 0; i < count; i++)
        {
            if (overlaps[i] == null) continue;

            var blob = overlaps[i].GetComponentInParent<ClayBlob>();
            if (blob == null || !blob.CanBePickedUp) continue;

            // Touching is measured surface to surface, against whichever part of the
            // assembly is nearest rather than its centre.
            if (!IsTouching(blob)) continue;

            Absorb(blob);
        }
    }

    /// <summary>Radius the assembly presents for pickup, between physical and visible.</summary>
    float OwnPickupRadius =>
        Mathf.Lerp(innerRadius,
                   ownContactSource != null ? ownContactSource.visualRadius : 0.5f,
                   pickupRadiusBlend);

    bool IsTouching(ClayBlob blob)
    {
        // Blobs already absorbed still present their visible size - they are what the new
        // blob physically runs into.
        float nearest = Vector3.Distance(blob.transform.position, OwnCentre())
                        - blob.visualRadius - OwnPickupRadius;

        for (int i = 0; i < merged.Count; i++)
        {
            float gap = Vector3.Distance(blob.transform.position, merged[i].transform.position)
                        - blob.visualRadius - merged[i].visualRadius;

            if (gap < nearest) nearest = gap;
        }

        return nearest <= pickupPadding;
    }

    void Absorb(ClayBlob blob)
    {
        blob.AttachTo(rollingObject);
        merged.Add(blob);

        RegisterForSquash(blob);

        RecentrePivot();
        RefreshSiblings();
        RefreshRollingRadius();
        RefreshGroundProbe();
    }

    /// <summary>
    /// Throws one of the absorbed blobs at whatever is being aimed at.
    ///
    /// The blob already nearest the launch direction is chosen, so it leads the throw
    /// rather than having to pass through the rest of the lump - its collider only leaves
    /// the shared Rigidbody at the moment of release, so it would collide with everything
    /// on the way out.
    /// </summary>
    public bool TryThrow()
    {
        if (throwing || merged.Count == 0 || rollingObject == null) return false;

        StartCoroutine(ThrowRoutine());
        return true;
    }

    IEnumerator ThrowRoutine()
    {
        throwing = true;

        Vector3 aimPoint = ResolveAimPoint();

        Vector3 launchDir = Vector3.ProjectOnPlane(aimPoint - AssemblyCentre(), Vector3.up);

        // Last resort if the aim collapses - the camera's own facing is always a sensible
        // direction, and beats normalising something near zero.
        if (launchDir.sqrMagnitude < 1e-4f)
        {
            Camera cam = aimCamera != null ? aimCamera : Camera.main;
            launchDir = cam != null
                ? Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up)
                : rollingObject.forward;
        }

        if (launchDir.sqrMagnitude < 1e-6f) launchDir = Vector3.forward;
        launchDir.Normalize();

        // Choosing the blob already facing the throw replaces turning the assembly to suit,
        // and keeps the launch clear of the rest of the lump either way.
        ClayBlob blob = PickBlobFacing(launchDir);

        if (spinDuration > 0f) yield return new WaitForSeconds(spinDuration);

        if (blob != null) Release(blob, launchDir * throwSpeed + Vector3.up * throwArc);

        throwing = false;
    }

    /// <summary>
    /// Picks whichever absorbed blob already sits nearest the launch direction.
    ///
    /// Cheaper than turning the assembly to suit, and it does not fight the solver: under
    /// physics rolling the body owns its own rotation, so forcing it mid-throw would be
    /// overwritten the moment the step runs.
    /// </summary>
    ClayBlob PickBlobFacing(Vector3 launchDir)
    {
        Vector3 pivot = AssemblyCentre();

        ClayBlob best = merged[0];
        float bestDot = float.MinValue;

        for (int i = 0; i < merged.Count; i++)
        {
            Vector3 offset = Vector3.ProjectOnPlane(merged[i].transform.position - pivot, Vector3.up);
            if (offset.sqrMagnitude < 1e-6f) continue;

            float dot = Vector3.Dot(offset.normalized, launchDir);
            if (dot > bestDot) { bestDot = dot; best = merged[i]; }
        }

        return best;
    }

    /// <summary>
    /// Where the throw is aimed.
    ///
    /// The camera sits behind the character, so a ray through screen centre passes straight
    /// through the player - and a jump lifts them into the middle of the screen, where they
    /// swallow the ray entirely. The resulting aim point lands centimetres from the
    /// assembly, and normalising that near-zero vector produces a direction that looks
    /// random. So the assembly is skipped, and anything suspiciously close is ignored too.
    /// </summary>
    Vector3 ResolveAimPoint()
    {
        Camera cam = aimCamera != null ? aimCamera : Camera.main;
        if (cam == null) return AssemblyCentre() + rollingObject.forward * maxAimDistance;

        // The third person camera locks the cursor, so screen centre is the sensible
        // default - a locked cursor's position is meaningless.
        Vector2 screenPoint = aimAtCursor && Mouse.current != null
            ? Mouse.current.position.ReadValue()
            : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

        Ray ray = cam.ScreenPointToRay(screenPoint);

        Vector3 centre = AssemblyCentre();

        // Anything nearer than this is too close to give a stable direction.
        float minRange = EffectiveRadius + 1f;

        int count = Physics.RaycastNonAlloc(ray, aimHits, maxAimDistance, aimMask,
                                            QueryTriggerInteraction.Ignore);

        float nearest = float.MaxValue;
        Vector3 aimPoint = ray.GetPoint(maxAimDistance);

        for (int i = 0; i < count; i++)
        {
            Collider hit = aimHits[i].collider;
            if (hit == null) continue;

            // Our own assembly is not a target.
            if (body != null && hit.attachedRigidbody == body) continue;

            if (Vector3.Distance(aimHits[i].point, centre) < minRange) continue;

            if (aimHits[i].distance < nearest)
            {
                nearest = aimHits[i].distance;
                aimPoint = aimHits[i].point;
            }
        }

        return aimPoint;
    }

    /// <summary>Cuts a blob loose, undoing everything Absorb set up.</summary>
    void Release(ClayBlob blob, Vector3 velocity)
    {
        merged.Remove(blob);
        UnregisterFromSquash(blob);

        Rigidbody thrown = blob.Detach(pickupLockSeconds);
        thrown.linearVelocity = velocity;

        // A little spin, so it does not sail out looking frozen.
        thrown.angularVelocity = Random.insideUnitSphere * 6f;

        RecentrePivot();
        RefreshSiblings();
        RefreshRollingRadius();
        RefreshGroundProbe();
    }

    void UnregisterFromSquash(ClayBlob blob)
    {
        if (squash == null) return;

        var renderers = blob.GetComponentsInChildren<MeshRenderer>(true);
        for (int i = 0; i < renderers.Length; i++) squash.RemoveRenderer(renderers[i]);

        var colliders = blob.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++) squash.RemovePivotCollider(colliders[i]);
    }

    /// <summary>
    /// Hands the blob's renderers to the squash driver, so the whole assembly deforms
    /// together about one shared world pivot rather than each part squashing about itself.
    /// </summary>
    void RegisterForSquash(ClayBlob blob)
    {
        if (squash == null) return;

        var renderers = blob.GetComponentsInChildren<MeshRenderer>(false);
        for (int i = 0; i < renderers.Length; i++)
            squash.AddRenderer(renderers[i]);

        // Fold the blob into the squash pivot, so the assembly flattens onto ITS lowest
        // point rather than the character's.
        var colliders = blob.GetComponentsInChildren<Collider>(false);
        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i].isTrigger) continue;
            squash.AddPivotCollider(colliders[i]);
        }
    }

    /// <summary>
    /// How far the assembly hangs below the body, so the controller's ground check can reach
    /// past an absorbed blob.
    ///
    /// A blob resting on the ground IS the assembly touching the ground, but its collider
    /// belongs to the same Rigidbody and so is skipped by the check. Extending the reach to
    /// the assembly's lowest point lets the cast find the real ground underneath it.
    /// </summary>
    void RefreshGroundProbe()
    {
        if (controller == null) return;

        float bodyY = body != null ? body.position.y : transform.position.y;
        float lowest = 0f;

        for (int i = 0; i < merged.Count; i++)
        {
            var colliders = merged[i].GetComponentsInChildren<Collider>(false);

            for (int c = 0; c < colliders.Length; c++)
            {
                if (colliders[c].isTrigger) continue;

                float drop = bodyY - colliders[c].bounds.min.y;
                if (drop > lowest) lowest = drop;
            }
        }

        controller.GroundProbeExtension = lowest;
    }

    /// <summary>
    /// Moves the rolling pivot and the Rigidbody's centre of mass onto the assembly's
    /// combined centre, so it turns about its real balance point rather than the original
    /// character's origin.
    /// </summary>
    void RecentrePivot()
    {
        Vector3 comWorld = AssemblyCentre();

        // Children have to be pinned in world space across the move, or shifting the pivot
        // would carry the whole assembly with it.
        int childCount = rollingObject.childCount;
        var children = new Transform[childCount];
        var worldPoses = new (Vector3 pos, Quaternion rot)[childCount];

        for (int i = 0; i < childCount; i++)
        {
            children[i] = rollingObject.GetChild(i);
            worldPoses[i] = (children[i].position, children[i].rotation);
        }

        rollingObject.position = comWorld;

        for (int i = 0; i < childCount; i++)
            children[i].SetPositionAndRotation(worldPoses[i].pos, worldPoses[i].rot);

        if (body != null)
            body.centerOfMass = body.transform.InverseTransformPoint(comWorld);
    }

    void RefreshSiblings()
    {
        siblingBuffer.Clear();

        if (ownContactSource != null) siblingBuffer.Add(ownContactSource);

        for (int i = 0; i < merged.Count; i++)
            if (merged[i].contactSource != null) siblingBuffer.Add(merged[i].contactSource);

        // Everyone gets everyone else, so each pair produces a flat interface from both
        // sides rather than one blob denting the other spherically.
        for (int i = 0; i < siblingBuffer.Count; i++)
            siblingBuffer[i].SetSiblings(siblingBuffer, siblingBuffer[i]);
    }

    /// <summary>
    /// Effective radius of the assembly, from combined volume rather than bounds - a wide
    /// assembly should roll as though it were the size its mass suggests, not its extent.
    /// </summary>
    void RefreshRollingRadius()
    {
        float volume = Cube(ownContactSource != null ? ownContactSource.visualRadius : 0.5f);

        for (int i = 0; i < merged.Count; i++)
            volume += Cube(merged[i].visualRadius);

        EffectiveRadius = Mathf.Pow(volume, 1f / 3f);

        if (controller != null) controller.RollingRadius = EffectiveRadius;
    }

    static float Cube(float v) => v * v * v;

    Vector3 OwnCentre() =>
        ownContactSource != null
            ? ownContactSource.transform.TransformPoint(ownContactSource.centreOffset)
            : transform.position;

    Vector3 AssemblyCentre()
    {
        // Volume weighted, so a big blob pulls the pivot more than a small one.
        float ownWeight = Cube(ownContactSource != null ? ownContactSource.visualRadius : 0.5f);

        Vector3 sum = OwnCentre() * ownWeight;
        float total = ownWeight;

        for (int i = 0; i < merged.Count; i++)
        {
            float weight = Cube(merged[i].visualRadius);
            sum += merged[i].transform.position * weight;
            total += weight;
        }

        return total > 0f ? sum / total : transform.position;
    }

    float LargestBlobRadius()
    {
        float largest = 0.5f;
        for (int i = 0; i < merged.Count; i++)
            if (merged[i].visualRadius > largest) largest = merged[i].visualRadius;

        return largest;
    }

    void OnDrawGizmosSelected()
    {
        if (!drawGizmos || !Application.isPlaying) return;

        Gizmos.color = new Color(1f, 0.5f, 0.9f, 0.9f);
        Gizmos.DrawWireSphere(AssemblyCentre(), 0.08f);

        Gizmos.color = new Color(1f, 0.5f, 0.9f, 0.3f);
        Gizmos.DrawWireSphere(AssemblyCentre(), EffectiveRadius);
    }
}
