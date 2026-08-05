using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public enum DentShape
{
    /// <summary>No flat face - a hemispherical tip. Equivalent to Cylinder with inner radius 0.</summary>
    Capsule = 0,
    /// <summary>Round flat face with a rounded rim, like the end of a cylinder.</summary>
    Cylinder = 1,
    /// <summary>Square flat face with a rounded rim, like the end of a cuboid.</summary>
    Square = 2,
    /// <summary>
    /// A hard flat surface the object RESTS ON, rather than a punch pressed into it.
    /// Everything below it conforms to it, and the displaced volume splays outward just
    /// above the contact instead of piling up behind. Uses Outer Radius as its half size;
    /// Inner Radius is ignored.
    /// </summary>
    Plane = 3
}

/// <summary>
/// A passive marker. Holds no logic of its own: DentManager reads every enabled
/// DentSource each frame and stamps them into the dent map.
///
/// Behaves like a SOLID OBJECT pressed into the surface. Depth comes from how far
/// the stamp is pushed into the mesh, not from an authored intensity value - so
/// position the source where you want the deepest point of the dent to be.
///
/// +Z (transform.forward) is the press direction, and the contact surface touches
/// the object's origin. Displacement is always along +Z, never sideways.
///
/// 'depth' controls how much penetration is flattened: vertices deeper than it all
/// get the same push, so they move rigidly and keep their relative positions.
/// </summary>
public class DentSource : MonoBehaviour
{
    public DentShape shape = DentShape.Cylinder;

    [Tooltip("Half-width of the flat contact face.\n" +
             "Beyond this the punch curves back in a rounded rim out to Outer Radius.\n" +
             "Ignored by Capsule (no flat face) and Plane (flat right to its edge).")]
    public float innerRadius = 0.05f;

    [Tooltip("Half-width of the whole punch, including the rounded rim.\n" +
             "For Capsule this is the tip radius. For Plane it is the half size of the " +
             "surface - make it comfortably larger than the object resting on it.")]
    public float outerRadius = 0.2f;

    [Tooltip("How much penetration gets flattened.\n" +
             "Vertices deeper than this all receive the SAME push, so they translate " +
             "rigidly and keep their relative spacing.\n\n" +
             "Set it comfortably larger than the deepest the stamp will sit in the mesh, " +
             "or the press cannot reach its own contact surface and the dent comes out " +
             "soft and half-formed.\n\n" +
             "Preserving the relief of separate mesh parts is handled by Island Rigidity " +
             "on DentManager, not by this.")]
    public float depth = 0.5f;

    [Tooltip("Scale retained along the press axis.\n" +
             "0 = fully conformed to the stamp (risks coplanar Z-fighting on overlapping shells).\n" +
             "1 = no flattening at all.\n" +
             "Like scaling soft-selected verts toward zero on one axis in Blender.")]
    [Range(0f, 1f)] public float flattenScale = 0.15f;

    [Tooltip("Master multiplier, for fading a stamp in and out. " +
             "Dent depth is driven by geometry, not by this.")]
    [Range(0f, 1f)] public float strength = 1f;

    [Tooltip("How much material spreads sideways around the contact, as a fraction of how " +
             "far it was pushed in. 0 disables it.\n\n" +
             "Nothing spreads at the very centre, it peaks around the inner radius, and " +
             "tapers to nothing at the outer radius - like skin bulging around a press.")]
    [Range(0f, 2f)] public float spreadAmount = 0.25f;

    [Tooltip("PUNCH shapes: material piling up behind the contact and rising back out of " +
             "the press. Positive moves along -Z, negative along +Z.\n\n" +
             "PLANE: how far the squashed volume splays sideways. Scales automatically with " +
             "how deep the object is pressed into the surface.\n\n" +
             "0 disables it.")]
    [Range(-5f, 5f)] public float rimBulge = 0.35f;

    [Tooltip("PUNCH shapes: how far past the outer radius the bulge reaches, as a multiple " +
             "of it.\n\n" +
             "PLANE: how far ABOVE the surface the splay reaches, as a multiple of the " +
             "press depth.")]
    [Range(1f, 4f)] public float bulgeReach = 1.8f;

    [Tooltip("Punch shapes only. Which way the bulge moves.\n" +
             "0 = straight back out of the press, along -Z. Reliable everywhere.\n" +
             "1 = along the vertex normal, which follows the surface but points the wrong " +
             "way on parts of a curved shape.\n\n" +
             "Plane always splays outward and ignores this.")]
    [Range(0f, 1f)] public float bulgeNormalBias = 0f;

    [Tooltip("What drives the bulge strength.\n\n" +
             "Bulge scales with how far this stamp is pressed into the mesh. This caps that " +
             "driver, so a single long protrusion dipping deep cannot inflate the bulge " +
             "across the whole object.\n\n" +
             "0 = no clamp.")]
    public float bulgeClamp = 0.25f;

    [Tooltip("Plane only. Offset of the rectangle's centre from this transform, along the " +
             "source's own +X and +Y.\n\n" +
             "Lets the source sit under the character - which is what the splay radiates " +
             "from - while the surface itself is clamped to a collider face that may be " +
             "nowhere near centred on it.")]
    public Vector2 planeOffset = Vector2.zero;

    [Tooltip("How gently the press fades out at the edge of a Plane, as a fraction of its " +
             "extent. Lets the mesh bend over a ledge instead of shearing off at a hard line.")]
    [Range(0.01f, 0.9f)] public float planeEdgeSoftness = 0.2f;

    [Tooltip("Multiplier on DentManager's decay rate for dents this stamp creates.\n" +
             "1 = the manager's rate. Above 1 fades faster, below 1 lingers.\n" +
             "0 makes this stamp's dents permanent regardless of the manager.")]
    [Range(0f, 5f)] public float decayMultiplier = 1f;

    /// <summary>Inner radius, guaranteed below outer (smoothstep inverts otherwise).
    /// Capsule has no flat face, so it is zero. Plane repurposes this slot as its second
    /// half extent, so it passes through untouched.</summary>
    public float SafeInnerRadius =>
        shape == DentShape.Capsule ? 0f
        : shape == DentShape.Plane ? Mathf.Max(innerRadius, 0.0001f)
        : Mathf.Min(innerRadius, outerRadius - 0.0001f);

    public float SafeOuterRadius => Mathf.Max(outerRadius, 0.0001f);

    /// <summary>Largest lateral reach from the source, used for range checks.</summary>
    public float LateralReach => shape == DentShape.Plane
        ? Mathf.Max(Mathf.Abs(planeOffset.x) + SafeInnerRadius,
                    Mathf.Abs(planeOffset.y) + SafeOuterRadius)
        : SafeOuterRadius;

    /// <summary>Sideways spread inside the contact. Meaningless for a Plane, whose
    /// displaced volume is handled by the splay instead.</summary>
    public float EffectiveSpread => shape == DentShape.Plane ? 0f : spreadAmount;

    /// <summary>
    /// Deepest penetration this source achieved last frame, written by DentManager.
    /// Runtime only - it exists so the Plane gizmo can show the real splay height rather
    /// than a guess.
    /// </summary>
    [System.NonSerialized] public float lastPressDepth;

    void OnEnable() => DentManager.Register(this);
    void OnDisable() => DentManager.Unregister(this);

    void OnValidate()
    {
        innerRadius = Mathf.Max(0f, innerRadius);

        // A Plane's two radii are independent rectangle extents, not a fillet, so the
        // usual inner-below-outer rule does not apply.
        if (shape != DentShape.Plane)
            outerRadius = Mathf.Max(innerRadius + 0.0001f, outerRadius);
        else
            outerRadius = Mathf.Max(0.0001f, outerRadius);

        depth       = Mathf.Max(0.0001f, depth);
        bulgeClamp  = Mathf.Max(0f, bulgeClamp);
    }

    void OnDrawGizmos()
    {
        Vector3 p = transform.position;
        Vector3 fwd = transform.forward;
        Vector3 back = -fwd * depth;   // stamp body extends behind the contact surface

        Color innerCol = new Color(1f, 0.35f, 0f, 0.9f);
        Color outerCol = new Color(1f, 0.7f, 0.2f, 0.5f);

        switch (shape)
        {
            case DentShape.Capsule:
            {
                // Rounded tip at the origin, so the sphere centre sits one radius back.
                Vector3 capCentre = p - fwd * outerRadius;
                Gizmos.color = outerCol;
                Gizmos.DrawWireSphere(capCentre, outerRadius);

                if (depth > outerRadius)
                {
                    Vector3 tail = p + back;
#if UNITY_EDITOR
                    Handles.color = outerCol;
                    Handles.DrawWireDisc(tail, fwd, outerRadius);
#endif
                    Vector3 r = transform.right * outerRadius;
                    Vector3 u = transform.up * outerRadius;
                    Gizmos.DrawLine(capCentre + r, tail + r);
                    Gizmos.DrawLine(capCentre - r, tail - r);
                    Gizmos.DrawLine(capCentre + u, tail + u);
                    Gizmos.DrawLine(capCentre - u, tail - u);
                }
                break;
            }

            case DentShape.Cylinder:
#if UNITY_EDITOR
                Handles.color = innerCol;
                Handles.DrawWireDisc(p, fwd, innerRadius);
                Handles.color = outerCol;
                Handles.DrawWireDisc(p, fwd, outerRadius);
                Handles.DrawWireDisc(p + back, fwd, outerRadius);

                Vector3 cr = transform.right * outerRadius;
                Vector3 cu = transform.up * outerRadius;
                Handles.DrawLine(p + cr, p + back + cr);
                Handles.DrawLine(p - cr, p + back - cr);
                Handles.DrawLine(p + cu, p + back + cu);
                Handles.DrawLine(p - cu, p + back - cu);
#endif
                break;

            case DentShape.Square:
                Gizmos.color = innerCol;
                DrawSquare(p, innerRadius);
                Gizmos.color = outerCol;
                DrawSquare(p, outerRadius);
                DrawSquare(p + back, outerRadius);
                DrawSquareRails(p, back, outerRadius);
                break;

            case DentShape.Plane:
                // Rectangle with independent extents, offset to match the collider face.
                Gizmos.color = outerCol;
                DrawRect(p + transform.right * planeOffset.x + transform.up * planeOffset.y,
                         innerRadius, outerRadius);
                break;
        }

        // Press direction. This drives both the depth and the displacement direction.
        Gizmos.color = Color.cyan;
        Vector3 tip = p + fwd * (outerRadius * 1.5f);
        Gizmos.DrawLine(p, tip);
        Gizmos.DrawLine(tip, tip - fwd * (outerRadius * 0.3f) + transform.right * (outerRadius * 0.15f));
        Gizmos.DrawLine(tip, tip - fwd * (outerRadius * 0.3f) - transform.right * (outerRadius * 0.15f));

        // Only draw what the current mode actually uses.
        if (shape == DentShape.Plane)
        {
            DrawPlaneSplay(p, fwd);
        }
        else
        {
            DrawSpreadArrows(p, fwd);
            DrawBulgeRing(p, fwd);
        }
    }

    /// <summary>
    /// Plane splay: a square volume rising along +Z off the surface. Height is the press
    /// depth times the reach multiplier, so out of play mode it can only be estimated.
    /// </summary>
    void DrawPlaneSplay(Vector3 p, Vector3 fwd)
    {
        if (Mathf.Abs(rimBulge) <= 0.001f) return;

        bool measured = lastPressDepth > 1e-5f;
        float pressDepth = measured ? lastPressDepth : outerRadius * 0.1f;
        float height = pressDepth * Mathf.Max(bulgeReach, 1f);

        Gizmos.color = new Color(0.4f, 1f, 0.5f, 0.8f);
        DrawSquare(p + fwd * height, outerRadius);
        DrawSquareRails(p, fwd * height, outerRadius);

#if UNITY_EDITOR
        Handles.color = new Color(0.4f, 1f, 0.5f, 1f);
        Handles.Label(p + fwd * height + transform.right * outerRadius,
                      measured
                          ? $"splay {rimBulge:0.00}, height {height:0.000}"
                          : $"splay {rimBulge:0.00}, height x{bulgeReach:0.0} of press depth (estimated)");
#endif
    }

    /// <summary>Where the elephant-foot bulge fades out.</summary>
    void DrawBulgeRing(Vector3 p, Vector3 fwd)
    {
        if (Mathf.Abs(rimBulge) <= 0.001f) return;

#if UNITY_EDITOR
        Handles.color = new Color(1f, 0.85f, 0.3f, 0.6f);
        Handles.DrawWireDisc(p, fwd, outerRadius * bulgeReach);
        Handles.color = new Color(1f, 0.85f, 0.3f, 1f);
        Handles.Label(p - transform.up * (outerRadius * bulgeReach + 0.02f),
                      $"rim bulge {rimBulge:0.00}");
#endif
    }

    /// <summary>
    /// Arrows radiating outward from the inner radius, sized by spreadAmount, so the
    /// sideways component is visible rather than something you have to infer.
    /// </summary>
    void DrawSpreadArrows(Vector3 p, Vector3 fwd)
    {
        if (spreadAmount <= 0.001f) return;

        const int arrowCount = 8;
        float ringRadius = Mathf.Max(innerRadius, outerRadius * 0.15f);
        float length = spreadAmount * outerRadius * 0.6f;
        float head = length * 0.3f;

        Gizmos.color = new Color(0.4f, 1f, 0.5f, 0.9f);

        for (int i = 0; i < arrowCount; i++)
        {
            float angle = (360f / arrowCount) * i;
            Vector3 dir = Quaternion.AngleAxis(angle, fwd) * transform.right;

            Vector3 start = p + dir * ringRadius;
            Vector3 end = start + dir * length;

            Gizmos.DrawLine(start, end);
            Gizmos.DrawLine(end, end - dir * head + fwd * head * 0.5f);
            Gizmos.DrawLine(end, end - dir * head - fwd * head * 0.5f);
        }

#if UNITY_EDITOR
        Handles.color = new Color(0.4f, 1f, 0.5f, 1f);
        Vector3 labelDir = transform.right;
        Handles.Label(p + labelDir * (ringRadius + length * 1.2f),
                      $"spread {spreadAmount:0.00}");
#endif
    }

    void DrawSquare(Vector3 centre, float halfWidth) => DrawRect(centre, halfWidth, halfWidth);

    void DrawRect(Vector3 centre, float halfX, float halfY)
    {
        Vector3 r = transform.right * halfX;
        Vector3 u = transform.up * halfY;

        Vector3 a = centre + r + u;
        Vector3 b = centre + r - u;
        Vector3 c = centre - r - u;
        Vector3 d = centre - r + u;

        Gizmos.DrawLine(a, b);
        Gizmos.DrawLine(b, c);
        Gizmos.DrawLine(c, d);
        Gizmos.DrawLine(d, a);
    }

    void DrawSquareRails(Vector3 face, Vector3 back, float halfWidth)
    {
        Vector3 r = transform.right * halfWidth;
        Vector3 u = transform.up * halfWidth;

        Gizmos.DrawLine(face + r + u, face + back + r + u);
        Gizmos.DrawLine(face + r - u, face + back + r - u);
        Gizmos.DrawLine(face - r - u, face + back - r - u);
        Gizmos.DrawLine(face - r + u, face + back - r + u);
    }
}
