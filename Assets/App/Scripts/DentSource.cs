using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public enum DentShape
{
    /// <summary>Round, soft-edged. Like pressing a ball into clay.</summary>
    Sphere = 0,
    /// <summary>Flat-bottomed with a round edge. Like pressing the end of a cylinder in.</summary>
    Flat = 1
}

/// <summary>
/// A passive marker. Holds no logic of its own: DentManager reads every enabled
/// DentSource each frame and stamps them into the dent map.
///
/// The object's +Z (transform.forward) is the push direction, and for the Flat
/// shape it is also the press axis. Point it INTO the surface you want dented.
/// </summary>
public class DentSource : MonoBehaviour
{
    public DentShape shape = DentShape.Sphere;

    [Tooltip("Full strength inside this radius.")]
    public float innerRadius = 0.05f;

    [Tooltip("Falls off to nothing at this radius. Must be larger than inner radius.")]
    public float outerRadius = 0.2f;

    [Tooltip("Flat only: how far the dent reaches forward from the flat face, along +Z.")]
    public float axialReach = 0.15f;

    [Tooltip("How far vertices are pushed, before Max Dent Depth on the material.")]
    public float intensity = 1f;

    /// <summary>Inner radius, guaranteed to be below outer (smoothstep misbehaves otherwise).</summary>
    public float SafeInnerRadius => Mathf.Min(innerRadius, outerRadius - 0.0001f);
    public float SafeOuterRadius => Mathf.Max(outerRadius, 0.0001f);

    void OnEnable() => DentManager.Register(this);
    void OnDisable() => DentManager.Unregister(this);

    void OnValidate()
    {
        innerRadius = Mathf.Max(0f, innerRadius);
        outerRadius = Mathf.Max(innerRadius + 0.0001f, outerRadius);
        axialReach  = Mathf.Max(0.0001f, axialReach);
    }

    void OnDrawGizmos()
    {
        Vector3 p = transform.position;
        Vector3 fwd = transform.forward;

        Color inner = new Color(1f, 0.35f, 0f, 0.9f);
        Color outer = new Color(1f, 0.7f, 0.2f, 0.5f);

        if (shape == DentShape.Sphere)
        {
            Gizmos.color = inner;
            Gizmos.DrawWireSphere(p, innerRadius);
            Gizmos.color = outer;
            Gizmos.DrawWireSphere(p, outerRadius);
        }
        else
        {
#if UNITY_EDITOR
            // The flat face sits at the origin; the dent presses forward to +Z * axialReach.
            Handles.color = inner;
            Handles.DrawWireDisc(p, fwd, innerRadius);
            Handles.DrawWireDisc(p + fwd * axialReach, fwd, innerRadius);
            Handles.color = outer;
            Handles.DrawWireDisc(p, fwd, outerRadius);
            Handles.DrawWireDisc(p + fwd * axialReach, fwd, outerRadius);

            // Side rails so the reach in front of the face is readable.
            Vector3 right = transform.right * outerRadius;
            Vector3 up = transform.up * outerRadius;
            Handles.DrawLine(p + right, p + fwd * axialReach + right);
            Handles.DrawLine(p - right, p + fwd * axialReach - right);
            Handles.DrawLine(p + up, p + fwd * axialReach + up);
            Handles.DrawLine(p - up, p + fwd * axialReach - up);
#endif
        }

        // Push direction. This is the bit that actually drives the deformation now.
        Gizmos.color = Color.cyan;
        Vector3 tip = p + fwd * (outerRadius * 1.5f);
        Gizmos.DrawLine(p, tip);
        Gizmos.DrawLine(tip, tip - fwd * (outerRadius * 0.3f) + transform.right * (outerRadius * 0.15f));
        Gizmos.DrawLine(tip, tip - fwd * (outerRadius * 0.3f) - transform.right * (outerRadius * 0.15f));

#if UNITY_EDITOR
        Handles.color = Color.white;
        Handles.Label(p + transform.right * outerRadius,
                      $"{shape}\nintensity {intensity:0.00}");
#endif
    }
}
