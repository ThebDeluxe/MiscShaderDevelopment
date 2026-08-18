using UnityEngine;

/// <summary>
/// Morphs a clay character into a given shape when it touches this object.
///
/// Works either as a trigger volume or as solid geometry - both are handled, so a plain
/// MeshCollider on level geometry is enough.
///
/// SETUP NOTES
/// The CHARACTER needs a Rigidbody, which it has. This object does not.
///
/// A non-convex MeshCollider cannot be a trigger: Unity requires Convex to be ticked for
/// isTrigger to do anything, and without it OnTriggerEnter never fires at all. So either
/// tick Convex and use it as a trigger, or leave it solid and let the collision path below
/// handle it.
/// </summary>
[RequireComponent(typeof(Collider))]
public class ClayMorphTrigger : MonoBehaviour
{
    [Header("Shape")]
    [Tooltip("What the character becomes on contact. Sphere returns it to its normal form.")]
    public ClayShape shape = ClayShape.Pancake;

    [Tooltip("Which way the shape's long axis points.\n\n" +
             "Shape Default uses whatever the shape itself specifies - up for a pancake, the " +
             "direction of travel for a noodle. The others override that per trigger.")]
    public AxisSource axisSource = AxisSource.ShapeDefault;

    [Tooltip("Used when Axis Source is This Object, as a direction in this object's space.")]
    public Vector3 customAxis = Vector3.up;

    [Header("Behaviour")]
    [Tooltip("Morph again every time the character re-enters. Off means it fires once.")]
    public bool repeatable = true;

    [Tooltip("Seconds before this trigger can fire again, so a character resting in the " +
             "volume does not re-trigger every frame.")]
    public float cooldown = 0.5f;

    [Tooltip("Only characters on these layers are affected.")]
    public LayerMask affects = ~0;

    [Header("Debug")]
    public bool drawGizmos = true;

    [Tooltip("Log when something touches this but carries no ClayShapeMorph, which is the " +
             "usual reason a trigger appears to do nothing.")]
    public bool logMisses = false;

    public enum AxisSource
    {
        /// <summary>Whatever the shape itself specifies.</summary>
        ShapeDefault = 0,
        /// <summary>Straight up, whatever the shape would have chosen.</summary>
        WorldUp = 1,
        /// <summary>The direction given below, in this object's space.</summary>
        ThisObject = 2,
        /// <summary>From this object toward the character - a shape formed by the impact.</summary>
        AwayFromTrigger = 3
    }

    bool fired;
    float nextAllowedTime;

    void Reset()
    {
        // Left as authored: a solid mesh collider works through the collision path, and
        // forcing isTrigger on a non-convex mesh would silently stop it reporting anything.
    }

    void OnTriggerEnter(Collider other) => TryMorph(other);
    void OnTriggerStay(Collider other) => TryMorph(other);

    // Solid geometry reports collisions rather than triggers, and a non-convex MeshCollider
    // can only ever be solid - so both paths are needed for this to work on any collider.
    void OnCollisionEnter(Collision collision) => TryMorph(collision.collider);
    void OnCollisionStay(Collision collision) => TryMorph(collision.collider);

    void TryMorph(Collider other)
    {
        if (other == null) return;
        if (Time.time < nextAllowedTime) return;
        if (!repeatable && fired) return;
        if ((affects.value & (1 << other.gameObject.layer)) == 0) return;

        // The morph lives on the renderer, which is usually several levels below whatever
        // collider was touched - hence searching the whole hierarchy from the body down.
        var morph = FindMorph(other);
        if (morph == null)
        {
            if (logMisses)
                Debug.Log($"{name}: '{other.name}' touched this but has no ClayShapeMorph " +
                          "anywhere on its Rigidbody.", this);
            return;
        }

        if (morph.CurrentShape == shape) return;

        morph.SetShape(shape, ResolveAxis(morph));

        fired = true;
        nextAllowedTime = Time.time + Mathf.Max(cooldown, 0f);
    }

    static ClayShapeMorph FindMorph(Collider other)
    {
        // Up first: the collider is usually a child of the body, and the morph a sibling
        // branch, so the shared root is what connects them.
        Transform root = other.attachedRigidbody != null
            ? other.attachedRigidbody.transform
            : other.transform.root;

        return root.GetComponentInChildren<ClayShapeMorph>();
    }

    Vector3 ResolveAxis(ClayShapeMorph morph)
    {
        switch (axisSource)
        {
            case AxisSource.WorldUp:
                return Vector3.up;

            case AxisSource.ThisObject:
                return transform.TransformDirection(customAxis);

            case AxisSource.AwayFromTrigger:
            {
                Vector3 toCharacter = morph.PivotWorld - transform.position;
                return toCharacter.sqrMagnitude > 1e-4f ? toCharacter.normalized : Vector3.up;
            }

            // Zero means "use the shape's own axis setting", which SetShape reads.
            default:
                return Vector3.zero;
        }
    }

    void OnDrawGizmos()
    {
        if (!drawGizmos) return;

        var collider = GetComponent<Collider>();
        if (collider == null) return;

        Gizmos.color = fired && !repeatable
            ? new Color(0.5f, 0.5f, 0.5f, 0.25f)
            : new Color(1f, 0.6f, 0.2f, 0.35f);

        Bounds b = collider.bounds;
        Gizmos.DrawWireCube(b.center, b.size);

#if UNITY_EDITOR
        UnityEditor.Handles.color = Color.white;
        UnityEditor.Handles.Label(b.center + Vector3.up * (b.extents.y + 0.1f), shape.ToString());
#endif
    }
}
