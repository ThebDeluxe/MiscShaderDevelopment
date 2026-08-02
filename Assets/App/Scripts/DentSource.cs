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
    Square = 2
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
             "Ignored by Capsule, which is this same punch with no flat face at all.")]
    public float innerRadius = 0.05f;

    [Tooltip("Half-width of the whole punch, including the rounded rim.\n" +
             "For Capsule this is the tip radius.")]
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

    /// <summary>Inner radius, guaranteed below outer (smoothstep inverts otherwise).</summary>
    public float SafeInnerRadius => Mathf.Min(innerRadius, outerRadius - 0.0001f);
    public float SafeOuterRadius => Mathf.Max(outerRadius, 0.0001f);

    void OnEnable() => DentManager.Register(this);
    void OnDisable() => DentManager.Unregister(this);

    void OnValidate()
    {
        innerRadius = Mathf.Max(0f, innerRadius);
        outerRadius = Mathf.Max(innerRadius + 0.0001f, outerRadius);
        depth       = Mathf.Max(0.0001f, depth);
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
        }

        // Press direction. This drives both the depth and the displacement direction.
        Gizmos.color = Color.cyan;
        Vector3 tip = p + fwd * (outerRadius * 1.5f);
        Gizmos.DrawLine(p, tip);
        Gizmos.DrawLine(tip, tip - fwd * (outerRadius * 0.3f) + transform.right * (outerRadius * 0.15f));
        Gizmos.DrawLine(tip, tip - fwd * (outerRadius * 0.3f) - transform.right * (outerRadius * 0.15f));
    }

    void DrawSquare(Vector3 centre, float halfWidth)
    {
        Vector3 r = transform.right * halfWidth;
        Vector3 u = transform.up * halfWidth;

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
