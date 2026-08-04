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
/// The feel comes from a spring, not from velocity directly. The target is driven by
/// vertical SPEED, so rising and falling both stretch. Landing drops the target to zero
/// and the under-damped spring overshoots past it, which reads as a squash and an elastic
/// settle without any of that being scripted.
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
    [Tooltip("Stretch produced per unit of vertical speed. Higher means the object reacts " +
             "to slower movement.")]
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

    [Tooltip("How quickly the bounce settles. Critical damping is 2 * sqrt(Stiffness) - at " +
             "Stiffness 120 that is about 22, so anything well below that will visibly " +
             "oscillate. Lower means a longer wobble.")]
    public float damping = 7f;

    [Header("Pivot")]
    [Tooltip("Point the scaling happens around, in local space. For a character standing on " +
             "the ground, put this at the feet so squashing does not sink them through the " +
             "floor.")]
    public Vector3 pivotLocal = Vector3.zero;

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

    float lastHeight;
    float springValue;      // signed: positive stretches vertically, negative squashes
    float springVelocity;

    /// <summary>Signed deformation. Negative means squashed.</summary>
    public float CurrentAmount => springValue;

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

        if (!material.HasProperty(AxisID) || !material.HasProperty(AmountID))
        {
            Debug.LogError($"{name}: material '{material.name}' is missing '{AxisProperty}' or " +
                           $"'{AmountProperty}'. Add them as Per Material properties in the " +
                           "shader graph and feed them to the squash Custom Function node.", this);
            enabled = false;
            return;
        }

        lastHeight = motionSource.position.y;
    }

    void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        float height = motionSource.position.y;
        float verticalSpeed = (height - lastHeight) / dt;
        lastHeight = height;

        // SPEED, not signed velocity: rising and falling should both stretch. The squash
        // comes from the spring overshooting when the motion stops, not from the sign.
        float target = 0f;
        if (Mathf.Abs(verticalSpeed) > speedDeadzone)
            target = Mathf.Clamp(Mathf.Abs(verticalSpeed) * velocityToStretch, 0f, maxStretch);

        // Under-damped spring. The overshoot past zero on landing is what turns a sudden
        // stop into a squash and an elastic settle, for free.
        float accel = (target - springValue) * stiffness - springVelocity * damping;
        springVelocity += accel * dt;
        springValue += springVelocity * dt;

        springValue = Mathf.Clamp(springValue, -maxStretch, maxStretch);

        Push();
    }

    void Push()
    {
        // The shader works in object space, so world up has to be converted. Doing it every
        // frame means a tumbling object still squashes vertically rather than with its mesh.
        Vector3 axisOS = transform.InverseTransformDirection(Vector3.up);

        material.SetVector(AxisID, axisOS);
        material.SetFloat(AmountID, springValue);
        material.SetVector(PivotID, pivotLocal);
    }

    void OnDisable()
    {
        // Leave the mesh at rest rather than frozen mid-stretch.
        if (material != null && material.HasProperty(AmountID))
            material.SetFloat(AmountID, 0f);

        springValue = 0f;
        springVelocity = 0f;
    }

    void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        Vector3 pivotWS = transform.TransformPoint(pivotLocal);

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
