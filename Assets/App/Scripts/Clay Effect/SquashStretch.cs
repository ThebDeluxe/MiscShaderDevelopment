using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives volume-preserving squash and stretch from vertical motion.
///
/// Deliberately separate from the dent system. Dents are persistent per-vertex state in a
/// texture; this is a uniform whole-object transform with no history, so it belongs in the
/// vertex shader as a plain affine scale rather than in the dent map.
///
/// The axis is fixed to world up. Horizontal motion is ignored entirely, which keeps the
/// effect readable - a character that leans into every sideways step reads as wobbly
/// rather than weighty.
///
/// The spring can be driven two ways:
///   SetHold    - a sustained target, for things like crouching into a jump charge.
///   AddImpulse - a kick to the spring's velocity, for instantaneous events like landing
///                or launching. Impacts read better as impulses than as targets, because
///                the overshoot and settle come out of the spring rather than needing to
///                be animated.
///
/// Leave Auto From Motion off when something else (a character controller) is driving it,
/// so the two do not fight over the same spring.
/// </summary>
[DefaultExecutionOrder(-50)]
public class SquashStretch : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("Renderer whose material receives the squash. Leave empty to use this object's.")]
    public Renderer targetRenderer;

    [Tooltip("Transform whose motion drives the effect. Leave empty to use this object's.")]
    public Transform motionSource;

    [Header("Response")]
    [Tooltip("Derive the spring target from vertical speed. Turn OFF when a controller is " +
             "driving this through SetHold and AddImpulse.")]
    public bool autoFromMotion = false;

    [Tooltip("Stretch produced per unit of vertical speed. Auto mode only.")]
    public float velocityToStretch = 0.15f;

    [Tooltip("Hard cap on stretch and squash, so a fast fall or a teleport cannot tear the " +
             "mesh apart.")]
    [Range(0f, 2f)] public float maxStretch = 0.5f;

    [Tooltip("Vertical speeds below this are ignored, so idle jitter does not wobble the " +
             "character.")]
    public float speedDeadzone = 0.05f;

    [Header("Spring")]
    [Tooltip("How hard the spring pulls toward the target. Higher is snappier and overshoots " +
             "more sharply on landing.")]
    public float stiffness = 120f;

    [Tooltip("How quickly the bounce settles by default. Critical damping is " +
             "2 * sqrt(Stiffness) - anything well below that will visibly oscillate. " +
             "AddImpulse can override this per event.")]
    public float damping = 7f;

    [Header("Pivot")]
    [Tooltip("Squash around the BOTTOM of this collider, so the character flattens down onto " +
             "the ground instead of sinking through it. BallCharacterController assigns its " +
             "inner collider here automatically.")]
    public Collider pivotCollider;

    [Tooltip("Fallback pivot in local space, used only when no Pivot Collider is set.")]
    public Vector3 pivotLocal = Vector3.zero;

    // Extra colliders folded into the pivot. An assembly has to flatten onto its own lowest
    // point, not the original character's - otherwise a blob hanging below squashes toward
    // the character instead of onto the ground.
    readonly List<Collider> pivotColliders = new List<Collider>();

    /// <summary>Includes a collider when working out where the assembly rests.</summary>
    public void AddPivotCollider(Collider collider)
    {
        if (collider != null && !pivotColliders.Contains(collider)) pivotColliders.Add(collider);
    }

    public void RemovePivotCollider(Collider collider) => pivotColliders.Remove(collider);

    [Header("Debug")]
    public bool drawGizmos = true;

    // Shader properties on the character material. These must exist as Per Material scope
    // properties in the graph, and be fed to the squash Custom Function node.
    const string AxisProperty   = "_SquashAxis";
    const string AmountProperty = "_SquashAmount";
    const string PivotProperty  = "_SquashPivot";

    static readonly int AxisID   = Shader.PropertyToID(AxisProperty);
    static readonly int AmountID = Shader.PropertyToID(AmountProperty);
    static readonly int PivotID  = Shader.PropertyToID(PivotProperty);

    Material material;
    Transform rendererTransform;

    // Extra renderers driven by the same spring - absorbed blobs, for instance. Each has
    // its own material and object space, but they all share ONE world pivot so the whole
    // assembly flattens to the same height rather than each part squashing about itself.
    readonly List<Renderer> extraRenderers = new List<Renderer>();
    readonly List<Material> extraMaterials = new List<Material>();

    /// <summary>Adds a renderer to be deformed by the same spring.</summary>
    public void AddRenderer(Renderer renderer)
    {
        if (renderer == null || extraRenderers.Contains(renderer)) return;

        Material instance = renderer.material;
        if (!instance.HasProperty(AxisID) || !instance.HasProperty(AmountID))
        {
            Debug.LogWarning($"{name}: '{renderer.name}' has no squash properties, skipping.", this);
            return;
        }

        extraRenderers.Add(renderer);
        extraMaterials.Add(instance);
    }

    /// <summary>Stops deforming a renderer, leaving it at rest.</summary>
    public void RemoveRenderer(Renderer renderer)
    {
        int index = extraRenderers.IndexOf(renderer);
        if (index < 0) return;

        if (extraMaterials[index] != null) extraMaterials[index].SetFloat(AmountID, 0f);

        extraRenderers.RemoveAt(index);
        extraMaterials.RemoveAt(index);
    }

    float lastHeight;
    float springValue;      // signed: positive stretches vertically, negative squashes
    float springVelocity;
    float holdTarget;       // sustained target set by SetHold
    float activeDamping;    // may be overridden per impulse, restored once settled

    /// <summary>Signed deformation. Negative means squashed.</summary>
    public float CurrentAmount => springValue;

    /// <summary>
    /// Sets a sustained target the spring eases toward. Use for held poses - crouching
    /// into a jump, for instance. Negative squashes, positive stretches.
    /// </summary>
    public void SetHold(float amount)
    {
        holdTarget = Mathf.Clamp(amount, -maxStretch, maxStretch);
    }

    /// <summary>
    /// Kicks the spring's velocity, for instantaneous events. The overshoot and settle then
    /// fall out of the spring rather than needing to be animated.
    ///
    /// wobbleDuration, if positive, sets how long the wobble should last: the spring's
    /// envelope decays as exp(-damping * t / 2), so reaching about 5% takes roughly
    /// 6 / damping seconds. Bigger impacts can therefore ring for longer as well as harder.
    /// </summary>
    public void AddImpulse(float velocityChange, float wobbleDuration = 0f)
    {
        springVelocity += velocityChange;

        activeDamping = wobbleDuration > 0.01f
            ? Mathf.Max(6f / wobbleDuration, 0.5f)
            : damping;
    }

    /// <summary>Drops everything back to rest immediately.</summary>
    public void ResetSpring()
    {
        springValue = 0f;
        springVelocity = 0f;
        holdTarget = 0f;
        activeDamping = damping;
    }

    void Start()
    {
        if (targetRenderer == null) targetRenderer = GetComponent<Renderer>();
        if (motionSource == null) motionSource = transform;

        if (targetRenderer == null)
        {
            Debug.LogError($"{name}: no Renderer to drive.", this);
            enabled = false;
            return;
        }

        // Same instance DentManager uses - the first access creates it, later ones reuse it.
        material = targetRenderer.material;

        // The shader runs in the RENDERER's object space, which on a rolling character is a
        // different transform from this one. Converting against the wrong basis is what
        // makes the squash appear to spin with the mesh.
        rendererTransform = targetRenderer.transform;

        if (!material.HasProperty(AxisID) || !material.HasProperty(AmountID))
        {
            Debug.LogError($"{name}: material '{material.name}' is missing '{AxisProperty}' or " +
                           $"'{AmountProperty}'. Add them as Per Material properties in the " +
                           "shader graph and feed them to the squash Custom Function node.", this);
            enabled = false;
            return;
        }

        lastHeight = motionSource.position.y;
        activeDamping = damping;
    }

    void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        float height = motionSource.position.y;
        float verticalSpeed = (height - lastHeight) / dt;
        lastHeight = height;

        float target = holdTarget;

        if (autoFromMotion)
        {
            // SPEED, not signed velocity: rising and falling should both stretch. The squash
            // comes from the spring overshooting when the motion stops, not from the sign.
            if (Mathf.Abs(verticalSpeed) > speedDeadzone)
                target += Mathf.Clamp(Mathf.Abs(verticalSpeed) * velocityToStretch, 0f, maxStretch);
        }

        // Under-damped spring. The overshoot past the target is what turns a sudden stop
        // into a squash and an elastic settle, for free.
        float accel = (target - springValue) * stiffness - springVelocity * activeDamping;
        springVelocity += accel * dt;
        springValue += springVelocity * dt;

        springValue = Mathf.Clamp(springValue, -maxStretch, maxStretch);

        // Once it has rung out, go back to the default damping so the next impulse starts
        // from a known state rather than inheriting the last one's timing.
        if (Mathf.Abs(springValue - target) < 0.001f && Mathf.Abs(springVelocity) < 0.01f)
            activeDamping = damping;

        Push();
    }

    void Push()
    {
        Vector3 pivotWS = PivotWorld();

        Apply(material, rendererTransform, pivotWS);

        for (int i = 0; i < extraRenderers.Count; i++)
        {
            if (extraRenderers[i] == null) continue;
            Apply(extraMaterials[i], extraRenderers[i].transform, pivotWS);
        }
    }

    /// <summary>
    /// Pushes the deformation onto one renderer.
    ///
    /// The shader works in that renderer's OWN object space, so world up and the shared
    /// pivot both have to be converted per renderer. Because the shader then transforms the
    /// result by the same object matrix, scaling along this axis is exactly scaling along
    /// world up - and every part flattens to the same world height.
    /// </summary>
    void Apply(Material target, Transform space, Vector3 pivotWS)
    {
        if (target == null || space == null) return;

        target.SetVector(AxisID, space.InverseTransformDirection(Vector3.up));
        target.SetFloat(AmountID, springValue);
        target.SetVector(PivotID, space.InverseTransformPoint(pivotWS));
    }

    /// <summary>
    /// Where the scaling happens around, in world space.
    ///
    /// The BOTTOM of the combined colliders, at their horizontal centre. Squashing about a
    /// centre sinks everything into the floor by half the squash; squashing about the
    /// contact point keeps the assembly planted and spreads it outward instead.
    ///
    /// Every collider in the assembly counts, so a blob hanging below the character
    /// flattens onto the ground rather than toward the character above it. The horizontal
    /// centre matters too: the perpendicular widening is measured from this point.
    /// </summary>
    Vector3 PivotWorld()
    {
        Bounds combined = default;
        bool any = false;

        if (pivotCollider != null)
        {
            combined = pivotCollider.bounds;
            any = true;
        }

        for (int i = 0; i < pivotColliders.Count; i++)
        {
            if (pivotColliders[i] == null) continue;

            if (!any) { combined = pivotColliders[i].bounds; any = true; }
            else combined.Encapsulate(pivotColliders[i].bounds);
        }

        if (!any) return transform.TransformPoint(pivotLocal);

        return new Vector3(combined.center.x, combined.min.y, combined.center.z);
    }

    void OnDisable()
    {
        // Leave the mesh at rest rather than frozen mid-stretch.
        if (material != null && material.HasProperty(AmountID))
            material.SetFloat(AmountID, 0f);

        for (int i = 0; i < extraMaterials.Count; i++)
            if (extraMaterials[i] != null) extraMaterials[i].SetFloat(AmountID, 0f);

        ResetSpring();
    }

    void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        Vector3 pivotWS = PivotWorld();

        Gizmos.color = new Color(1f, 0.4f, 0.8f, 0.9f);
        Gizmos.DrawWireSphere(pivotWS, 0.05f);

        if (Mathf.Abs(springValue) > 1e-4f)
        {
            // Green when stretching, red when squashing.
            Gizmos.color = springValue >= 0f ? Color.green : Color.red;
            Gizmos.DrawLine(pivotWS - Vector3.up * springValue, pivotWS + Vector3.up * springValue);
        }
    }
}
