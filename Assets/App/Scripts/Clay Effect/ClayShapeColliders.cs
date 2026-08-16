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
    [Tooltip("Multiplies every shape's own Collider Scale, for adjusting all of them at once " +
             "without losing their relative sizing. Leave at 1 and tune per shape on the " +
             "ClayShapeMorph.")]
    [Range(0.2f, 2f)] public float sizeMultiplier = 1f;

    [Header("Composite Detail")]
    [Tooltip("Spheres around the rim of a cylinder or cone. More is a rounder silhouette " +
             "and more contacts for the solver to chew through.")]
    [Range(4, 16)] public int rimSegments = 8;

    [Tooltip("Boxes rotated evenly to fill a circular face. Three gives a twelve-sided fit; " +
             "one is a plain inscribed square.\n\n" +
             "Boxes rather than spheres because a sphere bulges through a flat face - it is " +
             "a poor fit for a disc, so it would take many badly placed ones to do what a " +
             "few rotated boxes do exactly.")]
    [Range(1, 5)] public int discFillBoxes = 3;

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
    Vector3 alignedAxis = Vector3.zero;
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
        if (!built) return;

        if (morph.CurrentShape != applied)
        {
            Rebuild(morph.CurrentShape);
            return;
        }

        // The renderer is a child of the body and has no rotation of its own, so once the
        // holder is aimed it stays correct as the body rolls. Only a change to the AXIS
        // itself needs picking up - live tuning re-pushes the shape, and a Travel-axis shape
        // resolves a new direction each time. Comparing the object-space axis is a field
        // read, so this costs nothing per frame.
        if (morph.CurrentAxisObject != alignedAxis) AlignHolder();
    }

    /// <summary>
    /// Points the holder along the shape's axis, expressed relative to the BODY.
    ///
    /// Working in body space rather than world means the pieces ride the body's rotation
    /// naturally, and only the genuine offset between renderer and body is applied - a
    /// world-space aim taken once would bake that offset in as a permanent error.
    /// </summary>
    void AlignHolder()
    {
        alignedAxis = morph.CurrentAxisObject;

        if (applied == ClayShape.Sphere) return;   // a sphere has no orientation to match

        Vector3 axisWS = morph.CurrentAxisWorld;
        if (axisWS.sqrMagnitude < 1e-6f) return;

        Vector3 axisLocal = body.transform.InverseTransformDirection(axisWS);
        if (axisLocal.sqrMagnitude < 1e-6f) return;

        holder.localRotation = Quaternion.LookRotation(axisLocal.normalized, SafeUp(axisLocal));
    }

    void Rebuild(ClayShape shape)
    {
        applied = shape;
        Clear();

        float baseRadius = morph.baseRadius;

        // Per shape, since a flat pancake and a chunky box want different amounts of sink -
        // and a shape built from a composite usually wants a little more clearance than one
        // a single primitive fits exactly.
        var d = morph.GetDefinition(shape);
        float scale = (d != null ? d.colliderScale : 0.6f) * Mathf.Max(sizeMultiplier, 0.01f);

        if (shape == ClayShape.Sphere || d == null)
        {
            holder.localRotation = Quaternion.identity;
            AddSphere(Vector3.zero, baseRadius * scale);
            Finish(scale);
            return;
        }

        // The pieces below are built with +Z as the axis; the holder is aimed so that lines
        // up with the shape. Done relative to the body rather than in world space, so it
        // stays correct as the body rolls - see AlignHolder.
        AlignHolder();

        float across = d.width * baseRadius * scale;
        float thick = d.SafeThickness * baseRadius * scale;
        float along = d.length * baseRadius * scale;

        bool square = d.crossRoundness < 0.5f;
        bool rounded = d.endRoundness > 0.6f;
        bool elongated = along > Mathf.Max(across, thick) * 1.4f;

        if (d.taper > 0.3f) BuildTapered(across, along, square);
        else if (square) BuildBoxCage(across, thick, along);
        else if (rounded && elongated) BuildCapsule(Mathf.Min(across, thick), along);
        else BuildDisc(across, along);

        Finish(scale);
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

        // Fills the face. Rotated boxes rather than spheres: a sphere bulges through a flat
        // face, so it fits a disc badly, where each box is exactly flat and a few rotated
        // evenly inscribe the circle closely.
        FillDisc(ringRadius, Mathf.Max(along - rimRadius, 0.01f), Vector3.zero);
    }

    /// <summary>
    /// Inscribes a circle with rotated boxes.
    ///
    /// Each is sized so its corners land exactly on the circle - half extents of
    /// (R cos t, R sin t) at t = pi / 2N - so the union stays inside the silhouette however
    /// many are used. One is an inscribed square; three cover it closely.
    /// </summary>
    void FillDisc(float radius, float halfDepth, Vector3 centre)
    {
        int count = Mathf.Max(discFillBoxes, 1);
        float t = Mathf.PI / (2f * count);

        float halfX = radius * Mathf.Cos(t);
        float halfY = radius * Mathf.Sin(t);

        for (int i = 0; i < count; i++)
        {
            var box = New<BoxCollider>($"Face {i}");
            box.size = new Vector3(halfX * 2f, halfY * 2f, Mathf.Max(halfDepth * 2f, 0.01f));
            box.transform.localPosition = centre;
            box.transform.localRotation = Quaternion.Euler(0f, 0f, i * 180f / count);
        }
    }

    /// <summary>
    /// Box or prism: capsules along the twelve edges, with a box filling the faces.
    ///
    /// Rounded edges matter more than exactness here - a sharp hull corner digs in and stops
    /// the roll dead, where a capsule edge rides over.
    /// </summary>
    void BuildBoxCage(float across, float thick, float along)
    {
        float edge = Mathf.Min(Mathf.Min(across, thick), along) * 0.25f;

        float x = Mathf.Max(across - edge, 0.01f);
        float y = Mathf.Max(thick - edge, 0.01f);
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
        box.size = new Vector3(across * 2f, thick * 2f, along * 2f);
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
    /// Cone or pyramid: a flat base, then rings of spheres shrinking up the axis.
    ///
    /// The base is filled rather than ringed, so it rests flat instead of rocking on its
    /// rim - which is what a ring of spheres alone gives you.
    /// </summary>
    void BuildTapered(float across, float along, bool square)
    {
        // A proper flat bottom, using the same rotated-box fill as a disc face.
        float baseThickness = Mathf.Max(along * 0.18f, 0.02f);
        FillDisc(across, baseThickness, new Vector3(0f, 0f, -along + baseThickness));

        // Rings above it carry the taper. Started past the base so they do not simply
        // duplicate it.
        for (int ring = 1; ring < coneRings; ring++)
        {
            float t = ring / (float)Mathf.Max(coneRings - 1, 1);

            float z = Mathf.Lerp(-along, along, t);
            float radius = Mathf.Lerp(across, across * 0.12f, t);

            float sphereRadius = Mathf.Max(radius * 0.5f, 0.02f);
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

            // Fills the middle of the ring, so nothing falls through the gap.
            AddSphere(new Vector3(0, 0, z), sphereRadius);
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
    void Finish(float scale)
    {
        if (!pinInertia || body == null) return;

        float radius = morph.baseRadius * scale;

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
