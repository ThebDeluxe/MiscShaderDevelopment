using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds the character's collider out of primitives that approximate whatever shape it has
/// morphed into.
///
/// WHY A COMPOSITE RATHER THAN A CONVEX MESH
/// Unity has no cylinder or cone collider, so anything but a sphere, capsule or box needs
/// approximating. A convex MeshCollider would be one collider and is not as slow as its
/// reputation - the cost is the cook, which happens once - but it has sharp hull corners,
/// and this character ROLLS. Sharp corners catch. A composite of rounded primitives
/// behaves far better in motion, and sphere and capsule tests are the cheapest PhysX has.
///
/// The cost that does matter is contact count rather than collider count: several
/// overlapping primitives resting on a surface each generate contacts for the solver. The
/// counts here are deliberately low, and the inertia tensor is pinned so rebuilding a shape
/// does not make Unity recompute mass distribution from every piece.
/// </summary>
[DefaultExecutionOrder(-64)]
public class ClayShapeColliders : MonoBehaviour
{
    [Header("Setup")]
    [Tooltip("Body the colliders belong to. Falls back to this object's.")]
    public Rigidbody body;

    [Tooltip("Supplies the shape being morphed to. Found in children if empty.")]
    public ClayShapeMorph morph;

    [Tooltip("Controller whose own sphere is handed over. Without this its collider stays " +
             "active alongside these, and being larger it wins every contact.")]
    public ClayCharacterController controller;

    [Tooltip("Physics material applied to every piece.")]
    public PhysicsMaterial material;

    [Header("Sizing")]
    [Tooltip("How large the PHYSICAL collider is compared to the visible shape.\n\n" +
             "Below 1 by design: the gap is what the mesh sinks by, and that sink is what " +
             "the dent effect flattens. One value covers every shape.")]
    [Range(0.2f, 1f)] public float innerScale = 0.6f;

    [Header("Composite Detail")]
    [Tooltip("Spheres around the rim of a cylinder or cone. More is a rounder silhouette " +
             "and more contacts for the solver to chew through.")]
    [Range(4, 16)] public int rimSegments = 8;

    [Tooltip("Rings of spheres up a cone, spreading its taper over several steps.")]
    [Range(1, 5)] public int coneRings = 3;

    [Header("Mass")]
    [Tooltip("Pin the inertia tensor rather than letting Unity derive it from the pieces.\n\n" +
             "Worth doing: a compound recomputes mass distribution whenever its colliders " +
             "change, and a composite that swaps on every morph would pay that each time. " +
             "Pinning also keeps handling consistent between shapes.")]
    public bool pinInertia = true;

    [Tooltip("Inertia tensor to pin, as a fraction of a solid sphere's.")]
    public float inertiaScale = 1f;

    [Header("Debug")]
    public bool drawGizmos = true;

    readonly List<Collider> pieces = new List<Collider>();

    Transform holder;
    ClayShape applied = ClayShape.Sphere;
    bool built;

    void Start()
    {
        if (body == null) body = GetComponent<Rigidbody>();
        if (morph == null) morph = GetComponentInChildren<ClayShapeMorph>();
        if (controller == null) controller = GetComponent<ClayCharacterController>();

        if (body == null || morph == null)
        {
            Debug.LogError($"{name}: ClayShapeColliders needs a Rigidbody and a ClayShapeMorph.", this);
            enabled = false;
            return;
        }

        Transform existing = body.transform.Find("Shape Collider");

        if (existing == null)
        {
            var go = new GameObject("Shape Collider");
            go.transform.SetParent(body.transform, false);
            existing = go.transform;
        }

        holder = existing;
        built = true;

        // The controller builds its own sphere in Awake and leaves it enabled. Two colliders
        // on one body both collide, and the larger wins - so its sphere has to be handed
        // over or these never get a contact.
        if (controller != null && controller.InnerCollider != null)
            controller.InnerCollider.enabled = false;

        Rebuild(ClayShape.Sphere);
    }

    void Update()
    {
        if (!built || morph.CurrentShape == applied) return;
        Rebuild(morph.CurrentShape);
    }

    void Rebuild(ClayShape shape)
    {
        applied = shape;
        Clear();

        float baseRadius = morph.baseRadius;

        if (shape == ClayShape.Sphere)
        {
            holder.localRotation = Quaternion.identity;
            AddSphere(Vector3.zero, baseRadius * innerScale);
            Finish();
            return;
        }

        var d = morph.GetDefinition(shape);
        if (d == null) { AddSphere(Vector3.zero, baseRadius * innerScale); Finish(); return; }

        // The shape's axis is captured in the RENDERER's object space; the pieces live under
        // the body, so the holder is aimed along it and everything below is built in its
        // local frame with +Z as the axis.
        Vector3 axisWS = morph.CurrentAxisWorld;
        holder.rotation = Quaternion.LookRotation(axisWS, SafeUp(axisWS));

        float across = d.width * baseRadius * innerScale;
        float along = d.length * baseRadius * innerScale;

        bool square = d.crossRoundness < 0.5f;
        bool rounded = d.endRoundness > 0.6f;
        bool elongated = along > across * 1.4f;

        if (d.taper > 0.3f) BuildTapered(across, along, square);
        else if (square) BuildBoxCage(across, along);
        else if (rounded && elongated) BuildCapsule(across, along);
        else BuildDisc(across, along);

        Finish();
    }

    /// <summary>Long and round: one capsule is already the right shape.</summary>
    void BuildCapsule(float across, float along)
    {
        var capsule = New<CapsuleCollider>("Capsule");
        capsule.direction = 2;                       // holder local Z, which is the axis
        capsule.radius = across;
        capsule.height = Mathf.Max(along * 2f, across * 2f);
    }

    /// <summary>
    /// Cylinder or pancake: a ring of capsules around the rim with a box filling the middle.
    ///
    /// The capsules give the edge a rounded contact so it rolls over things instead of
    /// catching, which is the whole reason not to use a hull here.
    /// </summary>
    void BuildDisc(float across, float along)
    {
        float rimRadius = Mathf.Min(along, across * 0.5f);
        float ringRadius = Mathf.Max(across - rimRadius, 0.01f);

        for (int i = 0; i < rimSegments; i++)
        {
            float angle = (Mathf.PI * 2f / rimSegments) * i;
            var position = new Vector3(Mathf.Cos(angle) * ringRadius,
                                       Mathf.Sin(angle) * ringRadius, 0f);

            var capsule = New<CapsuleCollider>($"Rim {i}");
            capsule.direction = 2;
            capsule.radius = rimRadius;
            capsule.height = Mathf.Max(along * 2f, rimRadius * 2f);
            capsule.transform.localPosition = position;
        }

        // Fills the faces so things do not fall between the rim pieces.
        float inner = ringRadius * Mathf.Sqrt(2f) * 0.5f;
        var box = New<BoxCollider>("Face");
        box.size = new Vector3(inner * 2f, inner * 2f, Mathf.Max(along * 2f - 0.01f, 0.01f));
    }

    /// <summary>
    /// Box or prism: capsules along the twelve edges, with a box filling the faces.
    ///
    /// Rounded edges matter more than exactness here - a sharp hull corner digs in and stops
    /// the roll dead, where a capsule edge rides over.
    /// </summary>
    void BuildBoxCage(float across, float along)
    {
        float edge = Mathf.Min(across, along) * 0.25f;

        float x = Mathf.Max(across - edge, 0.01f);
        float y = x;
        float z = Mathf.Max(along - edge, 0.01f);

        // Four edges along each axis, at the corners of the other two.
        AddEdge(new Vector3(0, y, z), 0, x, edge);
        AddEdge(new Vector3(0, -y, z), 0, x, edge);
        AddEdge(new Vector3(0, y, -z), 0, x, edge);
        AddEdge(new Vector3(0, -y, -z), 0, x, edge);

        AddEdge(new Vector3(x, 0, z), 1, y, edge);
        AddEdge(new Vector3(-x, 0, z), 1, y, edge);
        AddEdge(new Vector3(x, 0, -z), 1, y, edge);
        AddEdge(new Vector3(-x, 0, -z), 1, y, edge);

        AddEdge(new Vector3(x, y, 0), 2, z, edge);
        AddEdge(new Vector3(-x, y, 0), 2, z, edge);
        AddEdge(new Vector3(x, -y, 0), 2, z, edge);
        AddEdge(new Vector3(-x, -y, 0), 2, z, edge);

        var box = New<BoxCollider>("Faces");
        box.size = new Vector3(across * 2f, across * 2f, along * 2f);
    }

    void AddEdge(Vector3 position, int direction, float halfLength, float radius)
    {
        var capsule = New<CapsuleCollider>("Edge");
        capsule.direction = direction;
        capsule.radius = radius;
        capsule.height = halfLength * 2f + radius * 2f;
        capsule.transform.localPosition = position;
    }

    /// <summary>
    /// Cone or pyramid: rings of spheres shrinking up the axis.
    ///
    /// Rough by design - a few rings read as a taper in motion, and spheres are the cheapest
    /// test there is, so this stays affordable even at a decent ring count.
    /// </summary>
    void BuildTapered(float across, float along, bool square)
    {
        for (int ring = 0; ring < coneRings; ring++)
        {
            float t = coneRings > 1 ? ring / (float)(coneRings - 1) : 0f;

            float z = Mathf.Lerp(-along, along, t);
            float radius = Mathf.Lerp(across, across * 0.15f, t);

            float sphereRadius = Mathf.Max(radius * 0.45f, 0.02f);
            float ringRadius = Mathf.Max(radius - sphereRadius, 0f);

            if (ringRadius < 0.01f)
            {
                AddSphere(new Vector3(0, 0, z), sphereRadius);
                continue;
            }

            int count = Mathf.Max(square ? 4 : rimSegments, 3);

            for (int i = 0; i < count; i++)
            {
                float angle = (Mathf.PI * 2f / count) * i;
                AddSphere(new Vector3(Mathf.Cos(angle) * ringRadius,
                                      Mathf.Sin(angle) * ringRadius, z), sphereRadius);
            }

            AddSphere(new Vector3(0, 0, z), Mathf.Max(ringRadius, sphereRadius));
        }
    }

    void AddSphere(Vector3 localPosition, float radius)
    {
        var sphere = New<SphereCollider>("Sphere");
        sphere.radius = radius;
        sphere.transform.localPosition = localPosition;
    }

    T New<T>(string name) where T : Collider
    {
        var go = new GameObject(name);
        go.transform.SetParent(holder, false);
        go.layer = holder.gameObject.layer;

        var collider = go.AddComponent<T>();
        if (material != null) collider.sharedMaterial = material;

        pieces.Add(collider);
        return collider;
    }

    void Clear()
    {
        for (int i = 0; i < pieces.Count; i++)
        {
            if (pieces[i] == null) continue;

            if (Application.isPlaying) Destroy(pieces[i].gameObject);
            else DestroyImmediate(pieces[i].gameObject);
        }

        pieces.Clear();
    }

    /// <summary>
    /// Pins the inertia tensor after a rebuild.
    ///
    /// A compound body recomputes its mass distribution whenever its colliders change, so a
    /// composite that swaps on every morph would pay for that each time. Pinning also keeps
    /// handling consistent: a box would otherwise resist rolling differently from a sphere
    /// purely because its pieces are arranged differently.
    /// </summary>
    void Finish()
    {
        if (!pinInertia || body == null) return;

        float radius = morph.baseRadius * innerScale;

        // A solid sphere: 2/5 m r^2 on every axis.
        float inertia = 0.4f * body.mass * radius * radius * Mathf.Max(inertiaScale, 0.01f);

        body.centerOfMass = Vector3.zero;
        body.inertiaTensor = Vector3.one * Mathf.Max(inertia, 1e-4f);
        body.inertiaTensorRotation = Quaternion.identity;
    }

    static Vector3 SafeUp(Vector3 forward)
    {
        return Mathf.Abs(Vector3.Dot(forward.normalized, Vector3.up)) > 0.99f
            ? Vector3.forward
            : Vector3.up;
    }

    void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        Gizmos.color = new Color(0.4f, 0.9f, 1f, 0.8f);

        for (int i = 0; i < pieces.Count; i++)
        {
            if (pieces[i] == null) continue;

            Gizmos.matrix = pieces[i].transform.localToWorldMatrix;

            if (pieces[i] is SphereCollider s) Gizmos.DrawWireSphere(Vector3.zero, s.radius);
            else if (pieces[i] is BoxCollider b) Gizmos.DrawWireCube(Vector3.zero, b.size);
            else if (pieces[i] is CapsuleCollider c) Gizmos.DrawWireSphere(Vector3.zero, c.radius);
        }

        Gizmos.matrix = Matrix4x4.identity;
    }
}
