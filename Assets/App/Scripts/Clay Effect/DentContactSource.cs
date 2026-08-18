using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

/// <summary>
/// Turns nearby surfaces into Plane-shaped dent sources, so the character flattens against
/// whatever it is actually resting on instead of against a hand-placed stamp.
///
/// WHY RAYCASTS RATHER THAN COLLISION CONTACTS
/// A sphere resting on a convex edge produces ONE contact, whose normal runs from the edge
/// to the sphere centre - the bisector of the two faces - so the character flattens against
/// an averaged angle instead of folding into the edge. Rays hit triangles, so each face
/// reports its own true normal and becomes its own plane, and the shader's constraint
/// accumulation produces the fold. Contacts also only exist once the PHYSICAL collider
/// touches, which is smaller than the mesh, so the dent would snap on rather than ease in.
///
/// Physics.ComputePenetration looks like the obvious tool here and is a trap: it cannot
/// handle non-convex MeshColliders, which is most level geometry, and silently returns false.
///
/// HOW THE EXTENT IS FOUND
/// A flattening plane that runs past a ledge keeps pressing where there is no longer any
/// floor, so the overhanging part of the mesh never droops. Working the face out
/// analytically only succeeds for a BoxCollider - on a concave mesh "the face" is a strip
/// of triangles with no single extent. Instead the edge is MEASURED: step outward from the
/// contact and probe whether the same surface is still underneath. That works on any
/// collider, at the cost of some extra raycasts.
/// </summary>
[DefaultExecutionOrder(-20)]
public class DentContactSource : MonoBehaviour
{
    [Header("Detection")]
    [Tooltip("Radius of the VISIBLE mesh. Surfaces within this distance start flattening " +
             "the character, by however far the mesh overlaps them.\n\n" +
             "Used as-is only while the character is a plain sphere - with a Shape Morph " +
             "driving it, the reach is asked of that instead, per direction.")]
    public float visualRadius = 0.7f;

    [Tooltip("Shape driver, so detection follows the deformed silhouette rather than a " +
             "sphere. Found in children if empty; without one the character is assumed round.")]
    public ClayShapeMorph shapeMorph;

    [Tooltip("The character's actual colliders. When set, the reach in each direction is " +
             "measured against these rather than reconstructed from the shape maths.\n\n" +
             "Real geometry cannot disagree with what is physically there, and it covers " +
             "composites and mid-morph blends without any special handling.\n\n" +
             "Leave empty on a BLOB: it is found from parents, and once absorbed a blob sits " +
             "under the character, so it would otherwise adopt the character's reach as its " +
             "own.")]
    public ClayShapeColliders shapeColliders;

    [Tooltip("Local offset of the character's centre from this transform.")]
    public Vector3 centreOffset = Vector3.zero;

    [Tooltip("Which layers can produce dents.")]
    public LayerMask surfaceMask = ~0;

    [Tooltip("How many directions are sampled around the character. More catches small " +
             "features and thin edges; each one is a raycast.")]
    [Range(6, 64)] public int sampleDirections = 24;

    [Header("Curvature")]
    [Tooltip("How far apart the samples are when estimating a convex mesh's curvature, as a " +
             "fraction of the visual radius.\n\n" +
             "A convex hull is faceted, so samples closer together than a facet report it as " +
             "flat and samples across an edge report a spike. Spreading them over several " +
             "facets averages that out.")]
    [Range(0.05f, 1f)] public float curvatureSampleSpread = 0.3f;

    [Tooltip("Largest surface radius considered. Anything flatter than this is treated as " +
             "this radius, which reads as flat.\n\n" +
             "Kept modest: this is also what a failed curvature estimate falls back to, and " +
             "a very large value there produces a contact curving so gently that it presses " +
             "like an infinite plane - and draws a gizmo sphere the size of the level.")]
    public float maxSurfaceRadius = 8f;

    [Header("Clustering")]
    [Tooltip("Surfaces whose normals are closer together than this angle are treated as one " +
             "and the deepest wins. Lower values keep the faces of an edge separate so the " +
             "mesh folds into it.")]
    [Range(1f, 90f)] public float mergeAngle = 25f;

    [Tooltip("Hard cap on simultaneous dent sources. DentManager supports 16 total, and " +
             "every one is evaluated per vertex.")]
    [Range(1, 8)] public int maxSources = 3;

    [Tooltip("Overlaps below this are ignored, so grazing surfaces do not flicker sources " +
             "in and out.")]
    public float minSink = 0.005f;

    [Tooltip("Deepest overlap that will ever be reported, as a fraction of the reach in that " +
             "direction.\n\n" +
             "An object only sinks by design as far as the gap between its mesh and its " +
             "collider - about a third of its radius. A contact reporting more than that " +
             "means something upstream is wrong, and the result is a mesh pressed completely " +
             "flat against a surface it is not really that deep into. Capping it stops a bad " +
             "reading becoming maximum squish.")]
    [Range(0.05f, 1f)] public float maxSinkFraction = 0.45f;

    [Tooltip("Manager these contacts belong to. Sources spawned here are marked as its " +
             "property, so a blob's floor contact cannot press a plane into a nearby " +
             "character. Found on this object or its parents if left empty.")]
    public DentManager owner;

    [Tooltip("How merged siblings interface with each other.\n\n" +
             "OFF treats a sibling as the rounded blob it is, so the pair press curved " +
             "dents into each other.\n\n" +
             "ON gives them a flat disc at the radical plane between their centres, which " +
             "is what two clay balls squashed hard together would share.")]
    public bool flatSiblingInterfaces = false;

    [Header("Edge Clamping")]
    [Tooltip("Measure how far each surface actually extends, so the flattening stops at a " +
             "ledge instead of pressing out over the drop.")]
    public bool clampToEdges = true;

    [Tooltip("Binary search steps used to find each edge. Each one is a raycast, per " +
             "direction, per surface - four directions, so this is the main cost here.")]
    [Range(1, 8)] public int edgeProbeSteps = 4;

    [Tooltip("How far above and below the surface an edge probe looks. Also decides how big " +
             "a step counts as a different surface rather than the same one continuing.")]
    public float edgeProbeTolerance = 0.06f;

    [Header("Stability")]
    [Tooltip("Seconds for a surface's position, angle and extent to catch up when they move.")]
    public float surfaceSmoothing = 0.06f;

    [Tooltip("Seconds for a newly found surface to reach full strength.")]
    public float fadeInTime = 0.08f;

    [Tooltip("Seconds a surface keeps fading after the rays stop finding it. Hysteresis " +
             "that stops a face flickering as you nudge around a corner.")]
    public float fadeOutTime = 0.25f;

    [Header("Stamp Settings")]
    [Tooltip("Upper cap on the surface half size, as a multiple of the visual radius. The " +
             "measured edges usually bring it in tighter than this.")]
    public float planeSizeScale = 1.2f;

    [Tooltip("How gently the press fades out at the edge of the surface, as a fraction of " +
             "its extent. Lets the mesh bend over a ledge rather than shear off at a line.")]
    [Range(0.01f, 0.9f)] public float planeEdgeSoftness = 0.25f;

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

    [Header("Idle")]
    [Tooltip("Skip the probe entirely when nothing is within reach.\n\n" +
             "The broad overlap test is one cheap query; the probe behind it is dozens of " +
             "raycasts plus a surface lookup per direction. Most objects in a scene are " +
             "touching nothing at any given moment, so checking first and stopping is the " +
             "largest saving available here.")]
    public bool skipWhenNothingNear = true;

    [Tooltip("Seconds of probing after the last surface leaves range, so tracked surfaces " +
             "finish fading out instead of vanishing.")]
    public float idleGraceTime = 0.5f;

    [Header("Debug")]
    public bool drawGizmos = true;

    /// <summary>Skips the frame's work. Set by DentLOD to throttle at distance.</summary>
    [System.NonSerialized] public bool paused;

    /// <summary>How far the visible mesh overlaps a surface, and which way to push out.</summary>
    public readonly struct SurfaceOverlap
    {
        public readonly Vector3 Point;
        public readonly Vector3 Normal;   // from the surface toward this object
        public readonly float Depth;
        public readonly float Weight;     // 0..1 fade, so forces ease in and out with it

        public SurfaceOverlap(Vector3 point, Vector3 normal, float depth, float weight)
        {
            Point = point;
            Normal = normal;
            Depth = depth;
            Weight = weight;
        }
    }

    /// <summary>
    /// Surfaces the visible mesh is currently overlapping.
    ///
    /// Already gathered for the dent effect, so anything wanting soft collision can push
    /// against these rather than paying for its own queries.
    /// </summary>
    public int GetSurfaceOverlaps(SurfaceOverlap[] buffer)
    {
        int count = 0;

        for (int i = 0; i < tracked.Count && count < buffer.Length; i++)
        {
            // Siblings share our Rigidbody, so pushing away from one is pushing away from
            // ourselves - a self-force that just shoves the whole assembly sideways.
            if (tracked[i].isSibling) continue;

            buffer[count++] = new SurfaceOverlap(tracked[i].point, tracked[i].pressAxis,
                                                 tracked[i].sink, tracked[i].weight);
        }

        return count;
    }

    [Tooltip("Draw every edge probe: green where the surface was found to continue, red " +
             "where it was not. If red dots never appear past a ledge, the probe is the " +
             "problem; if they do but the plane still presses out there, the problem is " +
             "downstream of the measurement.")]
    public bool drawEdgeProbes = false;

    [Tooltip("Log the measured extents once a second.")]
    public bool logExtents = false;

    readonly List<(Vector3 pos, bool solid)> probeLog = new List<(Vector3, bool)>(64);
    float nextLogTime;

    const int MaxOverlaps = 16;
    readonly Collider[] overlaps = new Collider[MaxOverlaps];

    readonly List<Contact> contacts = new List<Contact>(16);
    readonly List<Tracked> tracked = new List<Tracked>(8);
    readonly List<DentSource> pool = new List<DentSource>();

    // Blobs merged into the same assembly. Two clay balls pressed together share a FLAT
    // disc, not a spherical dent, so these are handled explicitly rather than being picked
    // up as curved surfaces by the overlap pass.
    readonly List<DentContactSource> siblings = new List<DentContactSource>();
    readonly HashSet<Collider> siblingColliders = new HashSet<Collider>();

    /// <summary>
    /// Tells this source which other blobs share its assembly.
    ///
    /// Siblings are always handled here rather than by the ordinary overlap pass, for two
    /// reasons. Merging parents a blob under the character, so IsChildOf excludes it from
    /// the character's probe entirely. And the physical colliders are deliberately smaller
    /// than the visible meshes - the character's is 0.4 against a 0.7 mesh - so a blob
    /// resting on the character's visible surface is nowhere near its collider and would
    /// register no contact at all. Working from the visual radii fixes both.
    /// </summary>
    public void SetSiblings(List<DentContactSource> all, DentContactSource self)
    {
        siblings.Clear();
        siblingColliders.Clear();

        for (int i = 0; i < all.Count; i++)
        {
            if (all[i] == null || all[i] == self) continue;

            siblings.Add(all[i]);
            all[i].GetComponentsInChildren(true, colliderBuffer);

            for (int c = 0; c < colliderBuffer.Count; c++)
                siblingColliders.Add(colliderBuffer[c]);
        }
    }

    static readonly List<Collider> colliderBuffer = new List<Collider>(8);

    Vector3[] directions;
    int builtDirectionCount;
    Rigidbody ownBody;
    float lastNearTime = -999f;
    bool shapeCollidersResolved;

    // Reach per sample direction, refreshed once per probe rather than per ray. Each lookup
    // walks every collider piece, so asking inside the ray loop meant repeating that work
    // for directions whose answer had not changed.
    float[] reachCache;

    static readonly ProfilerMarker MarkerProbe = new ProfilerMarker("DentContact.Probe");
    static readonly ProfilerMarker MarkerTrack = new ProfilerMarker("DentContact.Track");
    static readonly ProfilerMarker MarkerApply = new ProfilerMarker("DentContact.Apply");

    struct Contact
    {
        public DentShape shape;    // Plane for flat faces, Capsule for curved ones
        public Vector3 point;      // on the real surface
        public Vector3 pressAxis;  // from the surface INTO the character
        public Vector3 right;      // in-plane basis the extents are measured along
        public float sink;         // how far the visible mesh overlaps the surface
        public float radius;       // curved surfaces: radius of the surface it sits on
        public float bulgeRadius;  // curved surfaces: radius of the contact patch
        public float curvature;    // 1/R, 0 for a flat face

        // True when this is another blob in the same assembly rather than the world.
        public bool isSibling;

        // Which collider produced this, kept for diagnosis only. A contact pointing at
        // nothing visible is close to untraceable without knowing what generated it.
        public Collider origin;

        // Running curvature estimate, gathered as samples of the same surface merge in.
        public float curvatureSum;
        public int curvatureSamples;

        // Signed extents relative to 'point'. Plane only - a curved surface tapers off on
        // its own, so it needs no rectangle and no edge probing.
        public float minX, maxX, minY, maxY;
        public bool hasExtents;
    }

    /// <summary>
    /// A surface that persists between frames.
    ///
    /// Detection is a sampling process, so a face near the edge of ray coverage drops out
    /// and returns constantly as the character moves. Rebuilding the set each frame means
    /// its plane is destroyed and re-created, which makes control snap between surfaces.
    /// Keeping identity lets the plane move smoothly and fade rather than pop.
    /// </summary>
    class Tracked
    {
        public DentShape shape;
        public Vector3 point;
        public Vector3 pressAxis;
        public Vector3 right;
        public float sink;
        public float radius;
        public float bulgeRadius;
        public float curvature;
        public float weight;       // 0..1, fades in and out
        public bool isSibling;

        // Signed extents in the plane's own basis, measured from the contact point.
        // Signed rather than a half size so an off-centre face keeps its asymmetry: the
        // plane can stop at a ledge on one side while reaching across on the other.
        public float minX, maxX, minY, maxY;
        public bool measured;

        public bool seenThisFrame;
        public DentSource source;
        public Collider origin;
    }

    void LateUpdate()
    {
        if (paused) return;

        if (owner == null) owner = GetComponentInParent<DentManager>();
        if (ownBody == null) ownBody = GetComponentInParent<Rigidbody>();
        if (shapeMorph == null) shapeMorph = GetComponentInChildren<ClayShapeMorph>();

        // Only ever this object's OWN shape colliders. Searching parents finds the
        // character's set once a blob is absorbed into it - the blob is parented under the
        // rolling object - and a small blob would then claim the character's entire reach,
        // pressing everything around it violently out of shape.
        if (!shapeCollidersResolved)
        {
            shapeCollidersResolved = true;

            if (shapeColliders == null && GetComponentInParent<ClayBlob>() == null)
                shapeColliders = GetComponentInParent<ClayShapeColliders>();
        }

        EnsureDirections();

        // One cheap query decides whether the expensive one runs at all.
        if (!AnythingNear())
        {
            using (MarkerTrack.Auto()) TrackSurfaces();
            using (MarkerApply.Auto()) ApplyToSources();

            LogExtentsIfDue();
            return;
        }

        using (MarkerProbe.Auto()) Probe();
        using (MarkerTrack.Auto()) TrackSurfaces();
        using (MarkerApply.Auto()) ApplyToSources();

        LogExtentsIfDue();
    }

    /// <summary>
    /// Whether any surface is close enough to be worth probing for.
    ///
    /// A single overlap test against the furthest the shape reaches. The probe behind it is
    /// dozens of raycasts plus a per-direction surface lookup that walks every collider
    /// piece, so for an object touching nothing - which is most of them, most of the time -
    /// this turns the whole per-frame cost into one query.
    ///
    /// Tracking and application still run, so surfaces already found fade out properly
    /// rather than disappearing the moment the last one leaves range.
    /// </summary>
    bool AnythingNear()
    {
        if (!skipWhenNothingNear) return true;

        Vector3 centre = transform.TransformPoint(centreOffset);

        int count = Physics.OverlapSphereNonAlloc(centre, MaxReach, overlaps,
                                                  surfaceMask, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < count; i++)
        {
            if (overlaps[i] == null) continue;
            if (ownBody != null && overlaps[i].attachedRigidbody == ownBody) continue;

            lastNearTime = Time.time;
            return true;
        }

        // Keep going briefly, so anything already tracked finishes fading.
        return Time.time - lastNearTime <= idleGraceTime;
    }

    void LogExtentsIfDue()
    {
        if (!logExtents || Time.time < nextLogTime) return;

        nextLogTime = Time.time + 1f;

        for (int i = 0; i < tracked.Count; i++)
        {
            Tracked s = tracked[i];
            Debug.Log($"{name} surface {i}: axis {s.pressAxis}, " +
                      $"X [{s.minX:0.000}, {s.maxX:0.000}]  Y [{s.minY:0.000}, {s.maxY:0.000}], " +
                      $"sink {s.sink:0.000}, weight {s.weight:0.00}", this);
        }
    }

    /// <summary>
    /// Evenly spread directions using a Fibonacci sphere. An even spread matters more than
    /// the exact count: clumped samples would over-report whichever face they favour.
    /// </summary>
    void EnsureDirections()
    {
        if (directions != null && builtDirectionCount == sampleDirections) return;

        builtDirectionCount = sampleDirections;
        directions = new Vector3[sampleDirections];

        float goldenAngle = Mathf.PI * (3f - Mathf.Sqrt(5f));

        for (int i = 0; i < sampleDirections; i++)
        {
            float y = 1f - (i / (float)(sampleDirections - 1)) * 2f;
            float radius = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
            float theta = goldenAngle * i;

            directions[i] = new Vector3(Mathf.Cos(theta) * radius, y, Mathf.Sin(theta) * radius);
        }
    }

    void Probe()
    {
        contacts.Clear();
        probeLog.Clear();

        Vector3 centre = transform.TransformPoint(centreOffset);
        float mergeDot = Mathf.Cos(mergeAngle * Mathf.Deg2Rad);

        int count = Physics.OverlapSphereNonAlloc(centre, MaxReach, overlaps,
                                                  surfaceMask, QueryTriggerInteraction.Ignore);

        bool needsRaycastPass = false;

        for (int i = 0; i < count; i++)
        {
            Collider col = overlaps[i];
            if (col == null) continue;

            // Anything on our own Rigidbody is part of us, not the world. Hierarchy alone
            // is not enough to tell: merging parents a blob under the character, so from
            // the character's side the blob is a child, but from the blob's side the
            // character's colliders are not - they would sail straight through this test
            // and be treated as ordinary geometry to push away from.
            if (ownBody != null && col.attachedRigidbody == ownBody) continue;
            if (col.transform.IsChildOf(transform)) continue;

            // Merged siblings get their interface from ProbeSibling instead, which knows to
            // tag it as one of ours.
            if (siblingColliders.Contains(col)) continue;

            if (col is BoxCollider box) { probingCollider = col; ProbeBox(box, centre, mergeDot); }
            else if (col is SphereCollider sphere) { probingCollider = col; ProbeSphere(sphere, centre, mergeDot); }
            else if (col is CapsuleCollider capsule) { probingCollider = col; ProbeCapsule(capsule, centre, mergeDot); }
            else if (col is MeshCollider mesh && mesh.convex) { probingCollider = col; ProbeConvexMesh(mesh, centre, mergeDot); }
            else needsRaycastPass = true;
        }

        probingCollider = null;

        // Rays are only needed for geometry that cannot be solved directly.
        if (needsRaycastPass) ProbeByRays(centre, mergeDot);

        for (int i = 0; i < siblings.Count; i++) ProbeSibling(siblings[i], centre, mergeDot);

        FinaliseSampledCurvature();

        contacts.Sort((a, b) => b.sink.CompareTo(a.sink));
    }

    /// <summary>
    /// Turns the curvature gathered while merging ray samples into a usable radius.
    ///
    /// Nothing analytic is possible on a concave mesh, but the rays that found the surface
    /// already sampled it in several places - and those samples were being thrown away when
    /// they merged. How fast the normal turns between two of them, over the distance between
    /// them, is the curvature. Free, since the rays were cast anyway.
    ///
    /// Only applied to sampled contacts: a box face is exactly flat and should stay that way.
    /// </summary>
    void FinaliseSampledCurvature()
    {
        float minCurvature = 1f / Mathf.Max(maxSurfaceRadius, 0.01f);

        for (int i = 0; i < contacts.Count; i++)
        {
            Contact c = contacts[i];

            if (c.hasExtents || c.shape != DentShape.Plane) continue;
            if (c.curvatureSamples == 0) continue;

            float curvature = c.curvatureSum / c.curvatureSamples;

            // Anything flatter than the cap reads as flat, which also filters out the noise
            // a handful of ray samples inevitably carries.
            c.curvature = Mathf.Abs(curvature) > minCurvature ? curvature : 0f;

            contacts[i] = c;
        }
    }

    /// <summary>
    /// Sphere against box, solved rather than sampled.
    ///
    /// Every face the sphere overlaps becomes its own plane, with its true normal and its
    /// exact rectangle - so a step gives you its top face AND its riser, a corner gives you
    /// both walls, and a ledge's extent is known without probing for it. Rays cast from the
    /// character's centre cannot see a riser below a ledge at all, and the edge probes only
    /// ever look downward, so between them that case was invisible.
    ///
    /// Costs a few dot products per box, against dozens of raycasts.
    /// </summary>
    void ProbeBox(BoxCollider box, Vector3 centre, float mergeDot)
    {
        Transform bt = box.transform;
        Vector3 scale = bt.lossyScale;

        Vector3 half = new Vector3(Mathf.Abs(box.size.x * scale.x),
                                   Mathf.Abs(box.size.y * scale.y),
                                   Mathf.Abs(box.size.z * scale.z)) * 0.5f;

        // Sphere centre in the box's own frame, relative to its centre.
        Vector3 p = bt.InverseTransformPoint(centre) - box.center;
        p = new Vector3(p.x * scale.x, p.y * scale.y, p.z * scale.z);

        Vector3[] axes = { bt.right, bt.up, bt.forward };

        // A face is only a contact if the sphere's centre lies OUTSIDE its plane. Picking
        // the nearest face per axis regardless is what put a plane inside the geometry:
        // rolling up beside a short step sits level with its centre, so the top face gets
        // chosen with the centre buried under it, and the character is pressed upward from
        // inside the box.
        bool centreOutside = Mathf.Abs(p.x) > half.x
                          || Mathf.Abs(p.y) > half.y
                          || Mathf.Abs(p.z) > half.z;

        // Fully swallowed by the box - no face is "outside", so fall back to the nearest one.
        int nearestAxis = 0;
        if (!centreOutside)
        {
            float nearest = float.MinValue;
            for (int a = 0; a < 3; a++)
            {
                float d = Mathf.Abs(p[a]) - half[a];
                if (d > nearest) { nearest = d; nearestAxis = a; }
            }
        }

        for (int axis = 0; axis < 3; axis++)
        {
            int j = (axis + 1) % 3;
            int k = (axis + 2) % 3;

            float sign = p[axis] >= 0f ? 1f : -1f;

            // Distance from the sphere centre out to this face plane. Negative means the
            // centre is inside the box past it.
            float distance = sign * p[axis] - half[axis];

            if (centreOutside)
            {
                if (distance <= 0f) continue;   // centre is not outside this face
            }
            else if (axis != nearestAxis)
            {
                continue;
            }

            float sink = visualRadius - distance;
            if (sink < minSink) continue;

            // Must actually be over the face, not off its side.
            if (Mathf.Abs(p[j]) > half[j] + visualRadius) continue;
            if (Mathf.Abs(p[k]) > half[k] + visualRadius) continue;

            Vector3 normal = axes[axis] * sign;

            // Build the basis exactly as Quaternion.LookRotation will, which defines
            // right = cross(up, forward). Deriving the extents in any other frame flips
            // their asymmetry and hangs the rectangle off the wrong edge.
            Vector3 up = axes[j];
            Vector3 right = Vector3.Cross(up, normal);

            // Which way round the box's own axis ended up pointing in that basis.
            float rightSign = Vector3.Dot(right, axes[k]) >= 0f ? 1f : -1f;

            float alongRight = p[k] * rightSign;
            float alongUp = p[j];

            // The contact plane sits at the face, directly beneath the character.
            Vector3 point = centre - normal * distance;

            // The face rectangle, relative to that point. Exact, no probing.
            var contact = new Contact
            {
                shape = DentShape.Plane,
                point = point,
                pressAxis = normal,
                right = right,
                sink = sink,
                minX = -half[k] - alongRight,
                maxX = half[k] - alongRight,
                minY = -half[j] - alongUp,
                maxY = half[j] - alongUp,
                hasExtents = true
            };

            MergeOrAdd(contact, mergeDot);
        }
    }

    /// <summary>
    /// Sphere against sphere, solved exactly.
    ///
    /// The Capsule stamp's contact surface is -R + sqrt(R^2 - lat^2) - a hemisphere with its
    /// tip at the origin, curving away behind it. Put that origin on the collider's surface
    /// facing the character and the stamp IS the sphere, with no approximation and no
    /// sampling. A curved surface also tapers to nothing on its own, so unlike a flat face
    /// it needs no rectangle and no edge probing.
    /// </summary>
    void ProbeSphere(SphereCollider sphere, Vector3 centre, float mergeDot)
    {
        Transform st = sphere.transform;
        Vector3 scale = st.lossyScale;

        // Unity scales a sphere collider by the largest axis.
        float radius = sphere.radius * Mathf.Max(Mathf.Abs(scale.x),
                                                 Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));

        AddCurvedContact(st.TransformPoint(sphere.center), radius, centre, mergeDot);
    }

    /// <summary>
    /// Capsule against sphere. The closest point on the capsule's axis acts as the centre of
    /// a sphere of its radius, which is exact around the barrel. Along the axis a capsule is
    /// straight where this treats it as curved, so long capsules read slightly rounder than
    /// they are - usually invisible at contact scale.
    /// </summary>
    void ProbeCapsule(CapsuleCollider capsule, Vector3 centre, float mergeDot)
    {
        Transform ct = capsule.transform;
        Vector3 scale = ct.lossyScale;

        int dir = capsule.direction;   // 0 = X, 1 = Y, 2 = Z

        // Radius scales with the two axes across the capsule, height with the one along it.
        float radiusScale = dir == 0 ? Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z))
                          : dir == 1 ? Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z))
                                     : Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y));

        float radius = capsule.radius * radiusScale;
        float height = capsule.height * Mathf.Abs(scale[dir]);

        Vector3 axis = dir == 0 ? ct.right : dir == 1 ? ct.up : ct.forward;
        Vector3 mid = ct.TransformPoint(capsule.center);

        // The segment between the two cap centres.
        float halfSpan = Mathf.Max(height * 0.5f - radius, 0f);
        Vector3 a = mid - axis * halfSpan;
        Vector3 b = mid + axis * halfSpan;

        AddCurvedContact(ClosestPointOnSegment(a, b, centre), radius, centre, mergeDot);
    }

    void AddCurvedContact(Vector3 surfaceCentre, float radius, Vector3 centre, float mergeDot,
                          bool isSibling = false)
    {
        Vector3 toCharacter = centre - surfaceCentre;
        float distance = toCharacter.magnitude;
        if (distance < 1e-4f) return;   // concentric, no meaningful direction

        Vector3 normal = toCharacter / distance;

        // Gap between the two surfaces; negative once they overlap.
        float sink = visualRadius - (distance - radius);
        if (sink < minSink) return;

        MergeOrAdd(new Contact
        {
            // Capsule is a punch: its bulge piles up around the contact, which reads well
            // for something pressing into clay. The geometry would suit a curved Plane
            // equally, but that carries resting semantics and a softer, flatter look.
            shape = DentShape.Capsule,
            point = surfaceCentre + normal * radius,
            pressAxis = normal,
            radius = radius,
            isSibling = isSibling,
            sink = sink,

            // Radius of the contact patch, from the effective radius of two touching
            // spheres. The bulge ring needs to sit at the size of the patch, not the size
            // of the surface - on a large sphere those differ by an order of magnitude.
            bulgeRadius = Mathf.Sqrt(Mathf.Max(2f * (radius * visualRadius) /
                                               (radius + visualRadius) * sink, 0f)),

            hasExtents = false
        }, mergeDot);
    }

    static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 point)
    {
        Vector3 ab = b - a;
        float lengthSq = ab.sqrMagnitude;
        if (lengthSq < 1e-8f) return a;

        float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / lengthSq);
        return a + ab * t;
    }

    /// <summary>
    /// Convex mesh, reduced to the sphere that best matches it at the contact.
    ///
    /// Collider.ClosestPoint gives the contact point and normal directly - no rays, and it
    /// handles arbitrary convex shapes. What it does not give is how sharply the surface
    /// curves, so that is measured: query a few points offset sideways and see how fast the
    /// normal rotates against how far the contact moved. That ratio is the curvature, and
    /// its reciprocal is the radius the existing curved-contact path already expects.
    ///
    /// Only works on CONVEX meshes. ClosestPoint returns the input position unchanged for a
    /// concave one, which is why those still fall back to ray sampling.
    /// </summary>
    void ProbeConvexMesh(Collider col, Vector3 centre, float mergeDot)
    {
        Vector3 p0 = col.ClosestPoint(centre);

        Vector3 delta = centre - p0;
        float distance = delta.magnitude;

        // Zero means the centre is inside the collider, or ClosestPoint is unsupported here.
        if (distance < 1e-4f) return;
        if (distance > visualRadius) return;

        Vector3 normal = delta / distance;

        Quaternion basis = LookAlong(normal);
        Vector3 tangentA = basis * Vector3.right;
        Vector3 tangentB = basis * Vector3.up;

        float spread = visualRadius * curvatureSampleSpread;

        float total = 0f;
        int samples = 0;

        AccumulateCurvature(col, p0, normal, centre + tangentA * spread, ref total, ref samples);
        AccumulateCurvature(col, p0, normal, centre - tangentA * spread, ref total, ref samples);
        AccumulateCurvature(col, p0, normal, centre + tangentB * spread, ref total, ref samples);
        AccumulateCurvature(col, p0, normal, centre - tangentB * spread, ref total, ref samples);

        float curvature = samples > 0 ? total / samples : 0f;
        float radius = curvature > 1f / maxSurfaceRadius
            ? 1f / curvature
            : maxSurfaceRadius;

        // The sphere that touches at p0 with this normal has its centre one radius back.
        AddCurvedContact(p0 - normal * radius, radius, centre, mergeDot);
    }

    /// <summary>
    /// Curvature between the contact and one offset sample: how much the normal turned,
    /// divided by how far along the surface it turned over.
    /// </summary>
    static void AccumulateCurvature(Collider col, Vector3 p0, Vector3 n0, Vector3 queryPoint,
                                    ref float total, ref int samples)
    {
        Vector3 p1 = col.ClosestPoint(queryPoint);

        Vector3 delta = queryPoint - p1;
        float distance = delta.magnitude;
        if (distance < 1e-4f) return;

        float arc = Vector3.Distance(p1, p0);
        if (arc < 1e-4f) return;

        float angle = Vector3.Angle(n0, delta / distance) * Mathf.Deg2Rad;

        total += angle / arc;
        samples++;
    }

    /// <summary>
    /// How far the visible surface reaches in a world direction.
    ///
    /// Measured against the REAL colliders where they exist, scaled out to the visible
    /// silhouette. That gap is deliberate - it is the sink the dent effect flattens - so the
    /// collider distance times the visual ratio is where the mesh's surface actually is.
    ///
    /// Doing it this way rather than recomputing the shape means detection cannot drift out
    /// of agreement with what the player can see and touch, which is exactly what a private
    /// copy of the shape maths kept producing.
    /// </summary>
    float ReachAlong(Vector3 worldDirection, Vector3 centre)
    {
        if (shapeColliders != null && shapeColliders.Pieces.Count > 0)
        {
            // Cast against the pieces, so this is where the surface actually is along this
            // ray. Measuring with ClosestPoint from far away instead converges on the
            // furthest point in the direction, which on a flat shape is a corner several
            // times beyond the face being tested.
            return shapeColliders.SurfaceDistanceAlong(worldDirection)
                   * shapeColliders.VisualOverCollider;
        }

        return shapeMorph != null ? shapeMorph.SurfaceDistanceWorld(worldDirection) : visualRadius;
    }

    /// <summary>Furthest the surface reaches in any direction, for the broad overlap test.</summary>
    float MaxReach =>
        shapeColliders != null && shapeColliders.Pieces.Count > 0
            ? shapeColliders.MaxReach * shapeColliders.VisualOverCollider
            : shapeMorph != null ? shapeMorph.MaxRadius
            : visualRadius;

    /// <summary>
    /// How far this object's surface reaches toward a world point.
    ///
    /// Used by siblings, so a blob interfacing with a morphed character measures against
    /// where that character's surface actually is rather than a sphere it stopped being.
    /// </summary>
    public float ReachToward(Vector3 worldPoint)
    {
        Vector3 centre = transform.TransformPoint(centreOffset);
        Vector3 direction = worldPoint - centre;

        return direction.sqrMagnitude > 1e-6f ? ReachAlong(direction.normalized, centre)
                                              : visualRadius;
    }

    void ProbeByRays(Vector3 centre, float mergeDot)
    {
        // Reach is resolved once per direction up front. Each lookup walks every collider
        // piece, and a cone can be nearly thirty of them, so asking inside the loop repeated
        // that work for every ray.
        if (reachCache == null || reachCache.Length != directions.Length)
            reachCache = new float[directions.Length];

        for (int i = 0; i < directions.Length; i++)
            reachCache[i] = ReachAlong(directions[i], centre);

        for (int i = 0; i < directions.Length; i++)
        {
            float reach = reachCache[i];

            if (!Physics.Raycast(centre, directions[i], out RaycastHit hit, reach,
                                 surfaceMask, QueryTriggerInteraction.Ignore))
                continue;

            // Same reasoning as the overlap pass: our own assembly is not the world.
            if (ownBody != null && hit.collider.attachedRigidbody == ownBody) continue;

            // Boxes, rounded primitives and convex meshes were already handled directly.
            if (hit.collider is BoxCollider || hit.collider is SphereCollider
                || hit.collider is CapsuleCollider) continue;
            if (hit.collider is MeshCollider hitMesh && hitMesh.convex) continue;

            float sink = reach - hit.distance;
            if (sink < minSink) continue;

            // hit.normal comes off the triangle and points back toward the ray origin, which
            // is exactly the Plane stamp's press axis: from the surface into the character.
            MergeOrAdd(new Contact
            {
                shape = DentShape.Plane,
                point = hit.point,
                pressAxis = hit.normal,
                sink = sink,
                hasExtents = false
            }, mergeDot);
        }
    }

    /// <summary>
    /// Interface with a blob merged into the same assembly.
    ///
    /// Both are spheres of known visual radius, so this is solved rather than sampled.
    /// Curved by default - each presses a rounded dent into the other, as one lump of clay
    /// resting against another does.
    ///
    /// With flat interfaces on, they instead share their RADICAL PLANE: flat, perpendicular
    /// to the line joining their centres, positioned where the two surfaces intersect.
    ///
    ///     x = (d^2 + ra^2 - rb^2) / 2d      distance from our centre to the plane
    ///     r = sqrt(ra^2 - x^2)              radius of the contact disc
    /// </summary>
    void ProbeSibling(DentContactSource other, Vector3 centre, float mergeDot)
    {
        Vector3 otherCentre = other.transform.TransformPoint(other.centreOffset);

        Vector3 delta = centre - otherCentre;
        float d = delta.magnitude;
        if (d < 1e-4f) return;

        Vector3 axis = delta / d;   // from the sibling's surface into us

        // The sibling's reach TOWARD US, not a fixed radius. A morphed character is not a
        // sphere, so assuming one leaves a blob interfacing with a surface that is no longer
        // where the character is.
        float rb = other.ReachToward(centre);
        float ra = ReachAlong(-axis, centre);

        if (d >= ra + rb) return;   // not touching yet

        // The real surface and its NORMAL where the sibling faces us, when it can tell us.
        // Without that, the axis is the line between centres - which is only the surface
        // normal on a sphere. Resting on a pancake's top face near the rim, that line points
        // out toward the character's middle rather than down into the face being touched.
        Vector3 surfacePoint = Vector3.zero;
        Vector3 surfaceNormal = Vector3.up;
        bool haveSurface = false;

        if (other.shapeColliders != null)
            haveSurface = other.shapeColliders.SurfaceToward(centre, out surfacePoint, out surfaceNormal);

        if (haveSurface)
        {
            axis = surfaceNormal.normalized;

            // Depth measured along that normal rather than between centres, so the two agree.
            float gap = Vector3.Dot(centre - surfacePoint, axis);
            float sinkAlong = ReachAlong(-axis, centre) - gap;

            if (sinkAlong < minSink) return;

            if (!flatSiblingInterfaces)
            {
                AddCurvedContact(surfacePoint - axis * rb, rb, centre, mergeDot, true);
                return;
            }
        }

        if (!flatSiblingInterfaces)
        {
            // Treat the sibling as the sphere it is. AddCurvedContact does the rest, so
            // this shares every downstream behaviour with a real SphereCollider contact.
            AddCurvedContact(otherCentre, rb, centre, mergeDot, true);
            return;
        }

        float x = (d * d + ra * ra - rb * rb) / (2f * d);

        // A much larger sibling can put the plane behind our centre; the press is still
        // valid, it just swallows more of us.
        float sink = Mathf.Min(ra - x, ra);
        if (sink < minSink) return;

        float discSq = ra * ra - x * x;
        if (discSq <= 0f) return;

        float disc = Mathf.Sqrt(discSq);

        Quaternion basis = LookAlong(axis);

        MergeOrAdd(new Contact
        {
            shape = DentShape.Plane,
            point = centre - axis * x,
            pressAxis = axis,
            right = basis * Vector3.right,
            sink = sink,
            curvature = 0f,          // flat: the interface is a plane, not a curve
            isSibling = true,
            hasExtents = true,
            minX = -disc, maxX = disc,
            minY = -disc, maxY = disc
        }, mergeDot);
    }

    /// <summary>Which collider the probe currently running belongs to, recorded for diagnosis.</summary>
    Collider probingCollider;

    void MergeOrAdd(Contact c, float mergeDot)
    {
        c.origin = probingCollider;

        // A contact deeper than the geometry allows means something upstream is wrong - a
        // reach measured against the wrong shape, or a probe begun inside a collider. Capping
        // it keeps that from turning into a fully flattened mesh.
        float allowed = ReachToward(c.point) * maxSinkFraction;
        if (c.sink > allowed) c.sink = allowed;
        for (int i = 0; i < contacts.Count; i++)
        {
            Contact existing = contacts[i];
            if (Vector3.Dot(existing.pressAxis, c.pressAxis) < mergeDot) continue;

            // Two samples of the same surface. How far the normal turned between them,
            // over how far apart they are, is the curvature - so merging is where the
            // shape of a curved surface can be read off for free.
            float arc = Vector3.Distance(existing.point, c.point);
            if (arc > 1e-4f)
            {
                float angle = Vector3.Angle(existing.pressAxis, c.pressAxis) * Mathf.Deg2Rad;

                // Behind the tangent plane means the surface is curving away: convex,
                // which is the positive direction for the stamp.
                float axial = Vector3.Dot(c.point - existing.point, existing.pressAxis);
                float sign = axial <= 0f ? 1f : -1f;

                existing.curvatureSum += sign * angle / arc;
                existing.curvatureSamples++;
            }

            if (c.sink > existing.sink)
            {
                // Deeper sample wins the geometry, but the estimate built up so far has to
                // survive the swap.
                float sum = existing.curvatureSum;
                int samples = existing.curvatureSamples;

                existing = c;
                existing.curvatureSum = sum;
                existing.curvatureSamples = samples;
            }

            contacts[i] = existing;
            return;
        }

        contacts.Add(c);
    }

    /// <summary>
    /// Matches this frame's raw contacts onto surfaces already being tracked, so a face
    /// keeps the same plane rather than getting a fresh one every time the sampling shifts.
    /// </summary>
    void TrackSurfaces()
    {
        float dt = Mathf.Max(Time.deltaTime, 1e-5f);
        float matchDot = Mathf.Cos(mergeAngle * Mathf.Deg2Rad);
        float cap = visualRadius * planeSizeScale;

        for (int i = 0; i < tracked.Count; i++) tracked[i].seenThisFrame = false;

        for (int c = 0; c < contacts.Count; c++)
        {
            Contact contact = contacts[c];
            Tracked match = null;
            float bestDot = matchDot;

            for (int t = 0; t < tracked.Count; t++)
            {
                if (tracked[t].seenThisFrame) continue;

                float dot = Vector3.Dot(tracked[t].pressAxis, contact.pressAxis);
                if (dot > bestDot) { bestDot = dot; match = tracked[t]; }
            }

            if (match == null)
            {
                // Existing surfaces keep their slots: churning them for a newcomer is
                // exactly the popping this is meant to prevent.
                if (tracked.Count >= maxSources) continue;

                match = new Tracked
                {
                    shape = contact.shape,
                    point = contact.point,
                    pressAxis = contact.pressAxis,
                    right = contact.right,
                    radius = contact.radius,
                    bulgeRadius = contact.bulgeRadius,
                    curvature = contact.curvature,
                    isSibling = contact.isSibling,
                    origin = contact.origin,
                    sink = contact.sink,
                    weight = 0f
                };
                tracked.Add(match);
            }
            else
            {
                float k = surfaceSmoothing > 0f ? 1f - Mathf.Exp(-dt / surfaceSmoothing) : 1f;

                match.shape = contact.shape;
                match.point = Vector3.Lerp(match.point, contact.point, k);
                match.pressAxis = Vector3.Slerp(match.pressAxis, contact.pressAxis, k).normalized;
                match.sink = Mathf.Lerp(match.sink, contact.sink, k);
                match.radius = Mathf.Lerp(match.radius, contact.radius, k);
                match.bulgeRadius = Mathf.Lerp(match.bulgeRadius, contact.bulgeRadius, k);
                match.curvature = Mathf.Lerp(match.curvature, contact.curvature, k);
                match.isSibling = contact.isSibling;
                match.origin = contact.origin;

                if (contact.hasExtents)
                    match.right = Vector3.Slerp(match.right, contact.right, k).normalized;
            }

            match.seenThisFrame = true;

            // A curved surface tapers off on its own, so it needs no rectangle.
            if (contact.shape == DentShape.Plane)
                UpdateExtents(match, contact, cap, dt);
        }

        for (int t = tracked.Count - 1; t >= 0; t--)
        {
            Tracked s = tracked[t];

            float rate = s.seenThisFrame
                ? (fadeInTime > 0f ? dt / fadeInTime : 1f)
                : -(fadeOutTime > 0f ? dt / fadeOutTime : 1f);

            s.weight = Mathf.Clamp01(s.weight + rate);

            if (!s.seenThisFrame && s.weight <= 0f)
            {
                if (s.source != null) s.source.gameObject.SetActive(false);
                tracked.RemoveAt(t);
            }
        }
    }

    /// <summary>
    /// Brings a surface's extents up to date, either from the exact figures a box provided
    /// or by measuring them.
    /// </summary>
    void UpdateExtents(Tracked s, Contact contact, float cap, float dt)
    {
        float minX, maxX, minY, maxY;

        if (contact.hasExtents)
        {
            // A box knows its own face, so there is nothing to search for.
            minX = Mathf.Max(contact.minX, -cap);
            maxX = Mathf.Min(contact.maxX, cap);
            minY = Mathf.Max(contact.minY, -cap);
            maxY = Mathf.Min(contact.maxY, cap);
        }
        else if (clampToEdges)
        {
            // Nothing analytic is possible on a concave mesh, so walk outward and find
            // where the surface stops being underneath.
            if (s.right.sqrMagnitude < 0.5f)
                s.right = LookAlong(s.pressAxis) * Vector3.right;

            Vector3 up = Vector3.Cross(s.pressAxis, s.right).normalized;

            maxX = ProbeEdge(s, s.right, cap);
            minX = -ProbeEdge(s, -s.right, cap);
            maxY = ProbeEdge(s, up, cap);
            minY = -ProbeEdge(s, -up, cap);
        }
        else
        {
            minX = -cap; maxX = cap;
            minY = -cap; maxY = cap;
        }

        if (!s.measured)
        {
            s.minX = minX; s.maxX = maxX;
            s.minY = minY; s.maxY = maxY;
            s.measured = true;
            return;
        }

        // Smoothed, because a probed edge lands on slightly different answers frame to
        // frame and an unsmoothed edge visibly shimmers.
        float k = surfaceSmoothing > 0f ? 1f - Mathf.Exp(-dt / surfaceSmoothing) : 1f;

        s.minX = Mathf.Lerp(s.minX, minX, k);
        s.maxX = Mathf.Lerp(s.maxX, maxX, k);
        s.minY = Mathf.Lerp(s.minY, minY, k);
        s.maxY = Mathf.Lerp(s.maxY, maxY, k);
    }

    /// <summary>How far the surface continues in one in-plane direction, up to 'cap'.</summary>
    float ProbeEdge(Tracked s, Vector3 direction, float cap)
    {
        if (!SurfaceContinues(s, direction, cap)) 
        {
            float low = 0f;
            float high = cap;

            for (int i = 0; i < edgeProbeSteps; i++)
            {
                float mid = (low + high) * 0.5f;
                if (SurfaceContinues(s, direction, mid)) low = mid; else high = mid;
            }

            return low;
        }

        // Still solid at the cap, so the edge is further out than we care about.
        return cap;
    }

    /// <summary>
    /// Is the same surface still underneath, this far out?
    ///
    /// The probe starts above the plane and casts back down onto it, so a drop past a ledge
    /// simply misses, and a step up or down hits at the wrong height and is rejected.
    /// </summary>
    bool SurfaceContinues(Tracked s, Vector3 direction, float distance)
    {
        Vector3 origin = s.point + direction * distance + s.pressAxis * edgeProbeTolerance;

        bool solid = Physics.Raycast(origin, -s.pressAxis, out RaycastHit hit,
                                     edgeProbeTolerance * 2f, surfaceMask,
                                     QueryTriggerInteraction.Ignore)
                     // A different facing means a different surface, not this one continuing.
                     && Vector3.Dot(hit.normal, s.pressAxis) > 0.9f;

        if (drawEdgeProbes) probeLog.Add((origin, solid));

        return solid;
    }

    void ApplyToSources()
    {
        EnsurePool(tracked.Count);

        Vector3 centre = transform.TransformPoint(centreOffset);

        for (int i = 0; i < pool.Count; i++)
        {
            var src = pool[i];
            bool used = i < tracked.Count;

            if (!used)
            {
                // Disabling unregisters it from DentManager, so it costs nothing.
                if (src.gameObject.activeSelf) src.gameObject.SetActive(false);
                continue;
            }

            Tracked s = tracked[i];
            s.source = src;

            if (!src.gameObject.activeSelf) src.gameObject.SetActive(true);

            if (s.shape == DentShape.Capsule)
            {
                // The stamp's own curve matches the surface, so it sits right on the
                // contact point and needs no rectangle, offset or basis.
                src.transform.SetPositionAndRotation(s.point, LookAlong(s.pressAxis));

                src.shape = DentShape.Capsule;
                src.outerRadius = Mathf.Max(s.radius, 0.0001f);
                src.bulgeRadius = s.bulgeRadius;

                // Still a surface being rested ON, so the bulge splays outward. Punch
                // direction would push it along -Z, straight into the collider.
                src.bulgeOutward = 1f;

                ApplyCommonSettings(src, s.sink, s.weight);
                continue;
            }

            // The source sits directly under the character, projected onto the contact
            // plane. That matters twice over: the splay radiates outward from the source,
            // and DentManager's range check measures from it. The rectangle is placed
            // separately via an offset, so it can still stop at the measured edges.
            Vector3 sourcePos = centre - s.pressAxis * Vector3.Dot(centre - s.point, s.pressAxis);

            // Keep the plane's basis aligned to whatever the extents were measured in,
            // otherwise an exact box rectangle would be applied along the wrong axes.
            Vector3 basisRight = s.right.sqrMagnitude > 0.5f
                ? s.right
                : LookAlong(s.pressAxis) * Vector3.right;

            Vector3 basisUp = Vector3.Cross(s.pressAxis, basisRight).normalized;
            src.transform.SetPositionAndRotation(sourcePos,
                                                 Quaternion.LookRotation(s.pressAxis, basisUp));

            // Extents were measured about the contact point, so shift them onto the source.
            Vector3 shift = s.point - sourcePos;
            float offX = Vector3.Dot(shift, src.transform.right);
            float offY = Vector3.Dot(shift, src.transform.up);

            float minX = s.minX + offX, maxX = s.maxX + offX;
            float minY = s.minY + offY, maxY = s.maxY + offY;

            src.planeOffset = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);

            ApplySettings(src, s.sink, s.weight,
                          Mathf.Max((maxX - minX) * 0.5f, 0.0001f),
                          Mathf.Max((maxY - minY) * 0.5f, 0.0001f),
                          s.curvature);
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

            // Marked as ours, so no other manager picks it up and presses our contacts
            // into their mesh at a position that means nothing there.
            src.exclusiveOwner = owner;

            go.SetActive(false);
            pool.Add(src);
        }
    }

    void ApplySettings(DentSource src, float sink, float weight, float halfX, float halfY,
                       float curvature)
    {
        src.shape = DentShape.Plane;

        src.innerRadius = halfX;
        src.outerRadius = halfY;
        src.planeEdgeSoftness = planeEdgeSoftness;
        src.planeCurvature = curvature;
        src.bulgeRadius = 0f;   // planes size their splay from the surface itself

        ApplyCommonSettings(src, sink, weight);
    }

    void ApplyCommonSettings(DentSource src, float sink, float weight)
    {
        // Just past the actual sink, so the press can always reach its contact surface
        // without the source claiming a huge radius in DentManager's bounds filter.
        src.depth = Mathf.Max(sink * 1.5f, 0.01f);

        src.flattenScale = flattenScale;

        // Strength carries the fade, so a surface eases in and out instead of switching on.
        src.strength = strength * weight;

        src.rimBulge = rimBulge;
        src.bulgeReach = bulgeReach;
        src.bulgeClamp = bulgeClamp;
        src.decayMultiplier = decayMultiplier;
    }

    /// <summary>Rotation whose +Z points along 'forward', with a stable up vector.</summary>
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

        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.4f);
        Gizmos.DrawWireSphere(centre, visualRadius);

        for (int i = 0; i < tracked.Count; i++)
        {
            Tracked s = tracked[i];

            Gizmos.color = new Color(1f, 1f, 0f, Mathf.Max(s.weight, 0.15f));
            Gizmos.DrawSphere(s.point, 0.03f);
            Gizmos.DrawLine(s.point, s.point + s.pressAxis * 0.3f);

#if UNITY_EDITOR
            // What produced this contact. A dent pointing at nothing visible is otherwise
            // guesswork - this names the collider responsible, and draws a line back to it.
            string label = s.isSibling ? "sibling"
                         : s.origin != null ? $"{s.origin.name} ({s.origin.GetType().Name})"
                         : "ray";

            UnityEditor.Handles.color = Color.yellow;
            UnityEditor.Handles.Label(s.point + s.pressAxis * 0.35f, label);

            if (s.origin != null)
            {
                Gizmos.color = new Color(1f, 0.3f, 0f, 0.6f);
                Gizmos.DrawLine(s.point, s.origin.bounds.center);
            }
#endif

            if (s.shape == DentShape.Capsule || s.curvature != 0f)
            {
                // The surface this stamp is matching. Capped for display, since a nearly
                // flat contact has a radius of many metres and would otherwise draw a sphere
                // swallowing the whole scene.
                float shown = Mathf.Min(s.radius, MaxReach * 3f);
                if (shown > 0.01f) Gizmos.DrawWireSphere(s.point - s.pressAxis * shown, shown);

                continue;
            }

            // The measured rectangle, so it is obvious where the surface is believed to end.
            Vector3 right = s.right.sqrMagnitude > 0.5f
                ? s.right
                : LookAlong(s.pressAxis) * Vector3.right;
            Vector3 up = Vector3.Cross(s.pressAxis, right).normalized;

            Vector3 a = s.point + right * s.maxX + up * s.maxY;
            Vector3 b = s.point + right * s.maxX + up * s.minY;
            Vector3 c = s.point + right * s.minX + up * s.minY;
            Vector3 d = s.point + right * s.minX + up * s.maxY;

            Gizmos.DrawLine(a, b);
            Gizmos.DrawLine(b, c);
            Gizmos.DrawLine(c, d);
            Gizmos.DrawLine(d, a);
        }

        if (!drawEdgeProbes) return;

        for (int i = 0; i < probeLog.Count; i++)
        {
            Gizmos.color = probeLog[i].solid ? Color.green : Color.red;
            Gizmos.DrawSphere(probeLog[i].pos, 0.02f);
        }
    }
}
