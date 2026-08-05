using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Turns real collider contacts into Plane-shaped dent sources, so the character flattens
/// against whatever it is actually resting on instead of against a hand-placed stamp.
///
/// WHY COLLISION CALLBACKS RATHER THAN A GEOMETRY QUERY
/// Physics.ComputePenetration looks like the obvious tool, but PhysX cannot compute
/// penetration against non-convex MeshColliders - which is most level geometry - and it
/// silently returns false. OnCollisionStay hands us points and normals that the solver has
/// already produced, works on concave meshes, and costs nothing extra.
///
/// HOW THE SINK WORKS
/// The physical collider is deliberately smaller than the visible mesh. Contacts land on
/// the real surface, so the mesh overlaps it by (visual radius - physical radius) - and
/// that overlap is exactly what the Plane stamp flattens. Deepen the dent by shrinking the
/// physical collider, not by tuning the stamp.
///
/// Put this on the object holding the RIGIDBODY. Unity delivers collision messages there,
/// not to the child objects carrying the colliders.
/// </summary>
[DefaultExecutionOrder(-20)]
public class DentContactSource : MonoBehaviour
{
    [Header("Detection")]
    [Tooltip("Radius of the VISIBLE mesh. Contacts closer to the centre than this are " +
             "treated as the mesh sinking into the surface by the difference.")]
    public float visualRadius = 0.7f;

    [Tooltip("Local offset of the character's centre from this transform.")]
    public Vector3 centreOffset = Vector3.zero;

    [Tooltip("Which layers can produce dents.")]
    public LayerMask surfaceMask = ~0;

    [Header("Clustering")]
    [Tooltip("Contacts whose normals are closer together than this angle are merged, and " +
             "the deepest one wins. Stops a single flat surface producing a dozen sources.")]
    [Range(1f, 90f)] public float mergeAngle = 25f;

    [Tooltip("Hard cap on simultaneous dent sources from this component. DentManager only " +
             "supports 16 sources total, and every one is evaluated per vertex.")]
    [Range(1, 8)] public int maxSources = 3;

    [Tooltip("Sink amounts below this are ignored, so grazing touches do not flicker " +
             "sources in and out.")]
    public float minSink = 0.005f;

    [Header("Stamp Settings")]
    [Tooltip("Half size of the contact surface, as a multiple of the visual radius. Only " +
             "needs to comfortably cover the character.")]
    public float planeSizeScale = 3f;

    [Tooltip("Scale retained along the press axis. 0 is fully conformed.")]
    [Range(0f, 1f)] public float flattenScale = 0.15f;

    [Range(0f, 1f)] public float strength = 1f;

    [Tooltip("How far the squashed volume splays sideways.")]
    [Range(-5f, 5f)] public float rimBulge = 0.75f;

    [Tooltip("How far above the surface the splay reaches, as a multiple of press depth.")]
    [Range(1f, 4f)] public float bulgeReach = 1.15f;

    [Tooltip("Caps how deep a press can drive the splay. 0 = no clamp.")]
    public float bulgeClamp = 0.3f;

    [Tooltip("Multiplier on DentManager's decay rate for dents these contacts create.")]
    [Range(0f, 5f)] public float decayMultiplier = 2f;

    [Header("Debug")]
    public bool drawGizmos = true;

    readonly List<Contact> contacts = new List<Contact>(16);
    readonly List<DentSource> pool = new List<DentSource>();

    struct Contact
    {
        public Vector3 point;      // on the real surface
        public Vector3 pressAxis;  // from the surface INTO the character
        public float sink;         // how far the visible mesh overlaps the surface
    }

    void FixedUpdate()
    {
        // Cleared before the solver runs; OnCollisionStay refills it during this step.
        contacts.Clear();
    }

    void OnCollisionEnter(Collision collision) => Harvest(collision);
    void OnCollisionStay(Collision collision) => Harvest(collision);

    void Harvest(Collision collision)
    {
        if ((surfaceMask.value & (1 << collision.gameObject.layer)) == 0) return;

        Vector3 centre = transform.TransformPoint(centreOffset);
        float mergeDot = Mathf.Cos(mergeAngle * Mathf.Deg2Rad);

        int count = collision.contactCount;
        for (int i = 0; i < count; i++)
        {
            // GetContact avoids the array allocation that collision.contacts performs.
            ContactPoint cp = collision.GetContact(i);

            Vector3 toCentre = centre - cp.point;
            float distance = toCentre.magnitude;
            if (distance < 1e-5f) continue;

            // How far the VISIBLE mesh reaches past the contact surface.
            float sink = visualRadius - distance;
            if (sink < minSink) continue;

            // Force the axis to point from the surface into the character, so the Plane's
            // +Z convention holds no matter which way round the contact was reported.
            Vector3 axis = cp.normal;
            if (Vector3.Dot(axis, toCentre) < 0f) axis = -axis;

            MergeOrAdd(new Contact { point = cp.point, pressAxis = axis, sink = sink }, mergeDot);
        }
    }

    void MergeOrAdd(Contact c, float mergeDot)
    {
        for (int i = 0; i < contacts.Count; i++)
        {
            if (Vector3.Dot(contacts[i].pressAxis, c.pressAxis) < mergeDot) continue;

            // Same surface as far as the dent is concerned - keep whichever is deeper.
            if (c.sink > contacts[i].sink) contacts[i] = c;
            return;
        }

        contacts.Add(c);
    }

    void LateUpdate()
    {
        // Deepest contacts matter most, so they survive the cap.
        contacts.Sort((a, b) => b.sink.CompareTo(a.sink));
        if (contacts.Count > maxSources) contacts.RemoveRange(maxSources, contacts.Count - maxSources);

        EnsurePool(contacts.Count);

        for (int i = 0; i < pool.Count; i++)
        {
            var src = pool[i];
            bool used = i < contacts.Count;

            if (!used)
            {
                // Disabling unregisters it from DentManager, so it costs nothing.
                if (src.gameObject.activeSelf) src.gameObject.SetActive(false);
                continue;
            }

            Contact c = contacts[i];

            if (!src.gameObject.activeSelf) src.gameObject.SetActive(true);

            src.transform.position = c.point;
            src.transform.rotation = LookAlong(c.pressAxis);

            ApplySettings(src, c.sink);
        }
    }

    void EnsurePool(int needed)
    {
        while (pool.Count < needed)
        {
            var go = new GameObject($"Contact Dent {pool.Count}");
            go.transform.SetParent(transform, true);

            var src = go.AddComponent<DentSource>();
            src.shape = DentShape.Plane;

            go.SetActive(false);
            pool.Add(src);
        }
    }

    /// <summary>
    /// Pushes the look onto a spawned source. Size and depth are derived rather than
    /// authored: the surface only needs to cover the character, and how deep it presses is
    /// decided by the geometry, not by a number.
    /// </summary>
    void ApplySettings(DentSource src, float sink)
    {
        src.shape = DentShape.Plane;
        src.outerRadius = visualRadius * planeSizeScale;

        // Just past the actual sink, so the press can always reach its contact surface
        // without the source claiming a huge radius in DentManager's bounds filter.
        src.depth = Mathf.Max(sink * 1.5f, 0.01f);

        src.flattenScale = flattenScale;
        src.strength = strength;
        src.rimBulge = rimBulge;
        src.bulgeReach = bulgeReach;
        src.bulgeClamp = bulgeClamp;
        src.decayMultiplier = decayMultiplier;
    }

    /// <summary>Rotation whose +Z points along 'forward', with any stable up vector.</summary>
    static Quaternion LookAlong(Vector3 forward)
    {
        Vector3 up = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.99f
            ? Vector3.forward
            : Vector3.up;

        return Quaternion.LookRotation(forward, up);
    }

    void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        Vector3 centre = transform.TransformPoint(centreOffset);

        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.5f);
        Gizmos.DrawWireSphere(centre, visualRadius);

        Gizmos.color = Color.yellow;
        for (int i = 0; i < contacts.Count; i++)
        {
            Gizmos.DrawSphere(contacts[i].point, 0.03f);
            Gizmos.DrawLine(contacts[i].point, contacts[i].point + contacts[i].pressAxis * 0.3f);
        }
    }
}
