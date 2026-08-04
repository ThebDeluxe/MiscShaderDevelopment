using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds an approximate collider out of overlapping spheres, and moves them with the
/// dent deformation so physics roughly follows what you see.
///
/// APPROACH
/// Rather than voxelising the interior (which needs a reliable inside/outside test and
/// breaks on open or non-manifold shells), this k-means clusters the SURFACE vertices of
/// each mesh island. Sphere count scales with island size, so a small stud gets one
/// sphere, a tube gets a chain, and a torso gets a cluster - which falls out of the
/// clustering for free rather than needing special cases.
///
/// The trade is accuracy: spheres sit centred on the surface rather than on the medial
/// axis, so the shell reads slightly fat. For collision against a soft character that is
/// usually invisible.
///
/// DYNAMIC PART
/// Each sphere remembers the vertices that produced it, so deforming it is just skinning:
/// re-evaluate those few vertices and recompute centre and spread. No re-packing.
///
/// Runs after DentManager so it reads the island push values built this frame.
/// </summary>
[DefaultExecutionOrder(100)]
[RequireComponent(typeof(MeshFilter))]
public class DentColliderRig : MonoBehaviour
{
    [Serializable]
    public class SphereBinding
    {
        public int islandId;
        public Vector3 restCentre;    // local space, already inset below the surface
        public float restRadius;
        public float restSpread;      // mean distance from restCentre to stored members
        public Vector3 centreOffset;  // restCentre minus the mean of the stored members
        public int[] members;         // vertex indices driving this sphere
    }

    [Header("Source")]
    [Tooltip("Supplies the deformation. Leave empty to search this GameObject and its parents.")]
    public DentManager dentManager;

    [Header("Bake Settings")]
    [Tooltip("Roughly how big each sphere should be, in local space. Smaller means more " +
             "spheres and a tighter fit, at the cost of contact count.")]
    public float targetSphereRadius = 0.08f;

    [Tooltip("Multiplier on the derived sphere count. Raise for a denser, more detailed " +
             "packing; lower for a coarser one.")]
    [Range(0.1f, 4f)] public float density = 1f;

    [Tooltip("Hard cap per island, so one huge island cannot generate hundreds of colliders.")]
    public int maxSpheresPerIsland = 24;

    [Tooltip("How far to sink each sphere below the surface, as a fraction of the cluster's " +
             "mean spread. Clusters sit ON the surface, so without this the spheres bulge " +
             "outward. Around 0.5 usually keeps them inside.")]
    [Range(0f, 1.5f)] public float surfaceInset = 0.6f;

    [Tooltip("Radius is taken at this percentile of the distances from the sphere centre to " +
             "its cluster vertices. Low values fit inside the surface; 1 would touch the " +
             "furthest vertex and poke out. Overlapping neighbours fill the gaps.")]
    [Range(0f, 1f)] public float radiusPercentile = 0.3f;

    [Tooltip("Final multiplier on the fitted radius. Slightly above 1 over-covers, slightly " +
             "below tucks the spheres further in.")]
    public float radiusScale = 1f;

    [Tooltip("How many vertices each sphere stores for runtime skinning. More is smoother " +
             "but costs more per frame.")]
    [Range(1, 32)] public int maxMembersPerSphere = 8;

    [Tooltip("Vertices closer than this are welded when working out island connectivity. " +
             "Should match the value on DentVertexUVGenerator.")]
    public float weldEpsilon = 0.0001f;

    [Header("Runtime")]
    [Tooltip("Create real SphereColliders on Start. Turn off to preview the packing only.")]
    public bool createColliders = true;

    [Tooltip("Follow the deformation each frame. Turn off if dents are shallow relative to " +
             "sphere size, where static colliders are indistinguishable in play.")]
    public bool updateWithDeformation = true;

    [Tooltip("Physics layer for the generated collider objects.")]
    public int colliderLayer = 0;

    [Header("Preview")]
    public bool drawGizmos = true;
    public Color gizmoColour = new Color(0.2f, 0.9f, 1f, 0.35f);

    [SerializeField, HideInInspector] List<SphereBinding> bindings = new List<SphereBinding>();
    [SerializeField, HideInInspector] int bakedVertexCount;

    Vector3[] restPositions;
    Vector3[] accumulated;          // per stored member, object space, mirrors the texture's history
    int[] memberFlatIndex;          // start offset of each binding's members within 'accumulated'
    SphereCollider[] colliders;
    Transform colliderRoot;

    public int SphereCount => bindings != null ? bindings.Count : 0;

    // ====================================================================
    // Bake
    // ====================================================================

    [ContextMenu("Bake Collider Spheres")]
    public void Bake()
    {
        var mesh = GetComponent<MeshFilter>().sharedMesh;
        if (mesh == null)
        {
            Debug.LogError($"{name}: no sharedMesh to bake from.", this);
            return;
        }

        Vector3[] verts = mesh.vertices;
        Vector3[] normals = mesh.normals;
        if (normals == null || normals.Length != verts.Length)
        {
            Debug.LogWarning($"{name}: mesh has no usable normals, spheres cannot be inset " +
                             "below the surface and will sit proud of it.", this);
            normals = null;
        }

        int[] islandOf = DentMeshIslands.Build(mesh, verts, weldEpsilon, out int islandCount);
        var islandMembers = DentMeshIslands.GroupByIsland(islandOf, islandCount);

        bindings = new List<SphereBinding>();
        bakedVertexCount = verts.Length;

        for (int island = 0; island < islandCount; island++)
        {
            var list = islandMembers[island];
            if (list.Count == 0) continue;

            // Island extent drives sphere count, so a long tube gets a chain and a
            // compact blob gets a cluster, without either being a special case.
            Vector3 centroid = Vector3.zero;
            for (int k = 0; k < list.Count; k++) centroid += verts[list[k]];
            centroid /= list.Count;

            float extent = 0f;
            for (int k = 0; k < list.Count; k++)
                extent = Mathf.Max(extent, Vector3.Distance(verts[list[k]], centroid));

            int derived = Mathf.RoundToInt(density * (extent * 2f) / Mathf.Max(targetSphereRadius * 2f, 1e-4f));
            int clusterCount = Mathf.Clamp(derived, 1, Mathf.Min(maxSpheresPerIsland, list.Count));

            BakeIsland(verts, normals, list, island, clusterCount);
        }

        Debug.Log($"{name}: baked {bindings.Count} collider spheres across {islandCount} island(s).", this);
    }

    void BakeIsland(Vector3[] verts, Vector3[] normals, List<int> islandVerts, int islandId, int clusterCount)
    {
        // Seed centres from an evenly strided subset, so they start spread over the whole
        // island rather than clumped wherever the vertex order happens to begin.
        var centres = new Vector3[clusterCount];
        for (int c = 0; c < clusterCount; c++)
        {
            int idx = Mathf.Clamp(Mathf.FloorToInt((float)c / clusterCount * islandVerts.Count),
                                  0, islandVerts.Count - 1);
            centres[c] = verts[islandVerts[idx]];
        }

        int[] assignment = new int[islandVerts.Count];

        const int iterations = 12;
        for (int iter = 0; iter < iterations; iter++)
        {
            for (int v = 0; v < islandVerts.Count; v++)
            {
                Vector3 p = verts[islandVerts[v]];
                int best = 0;
                float bestSq = float.MaxValue;
                for (int c = 0; c < clusterCount; c++)
                {
                    float sq = (p - centres[c]).sqrMagnitude;
                    if (sq < bestSq) { bestSq = sq; best = c; }
                }
                assignment[v] = best;
            }

            var sums = new Vector3[clusterCount];
            var counts = new int[clusterCount];
            for (int v = 0; v < islandVerts.Count; v++)
            {
                sums[assignment[v]] += verts[islandVerts[v]];
                counts[assignment[v]]++;
            }
            for (int c = 0; c < clusterCount; c++)
                if (counts[c] > 0) centres[c] = sums[c] / counts[c];
        }

        var buckets = new List<int>[clusterCount];
        for (int c = 0; c < clusterCount; c++) buckets[c] = new List<int>();
        for (int v = 0; v < islandVerts.Count; v++) buckets[assignment[v]].Add(islandVerts[v]);

        for (int c = 0; c < clusterCount; c++)
        {
            var bucket = buckets[c];
            if (bucket.Count == 0) continue;   // k-means can orphan a cluster

            // Fit against the WHOLE cluster, not just the members we keep for skinning,
            // so the sphere reflects the real surface patch.
            Vector3 surfaceCentre = Vector3.zero;
            Vector3 meanNormal = Vector3.zero;
            for (int k = 0; k < bucket.Count; k++)
            {
                surfaceCentre += verts[bucket[k]];
                if (normals != null) meanNormal += normals[bucket[k]];
            }
            surfaceCentre /= bucket.Count;

            float meanSpread = 0f;
            for (int k = 0; k < bucket.Count; k++)
                meanSpread += Vector3.Distance(verts[bucket[k]], surfaceCentre);
            meanSpread /= bucket.Count;

            // Sink the centre below the surface. Cluster centres land ON the shell, so
            // without this every sphere bulges outward by roughly its own radius.
            Vector3 centre = surfaceCentre;
            if (normals != null && meanNormal.sqrMagnitude > 1e-8f)
                centre -= meanNormal.normalized * (meanSpread * surfaceInset);

            // Radius at a low percentile of the distances, so the sphere fits inside the
            // surface rather than reaching the furthest stray vertex.
            var distances = new List<float>(bucket.Count);
            for (int k = 0; k < bucket.Count; k++)
                distances.Add(Vector3.Distance(verts[bucket[k]], centre));
            distances.Sort();

            int pIndex = Mathf.Clamp(Mathf.FloorToInt(radiusPercentile * (distances.Count - 1)),
                                     0, distances.Count - 1);
            float radius = Mathf.Max(distances[pIndex] * radiusScale, 1e-4f);

            // Store a strided subset for runtime skinning.
            int take = Mathf.Min(maxMembersPerSphere, bucket.Count);
            var members = new int[take];
            for (int m = 0; m < take; m++)
            {
                int idx = Mathf.Clamp(Mathf.FloorToInt((float)m / take * bucket.Count),
                                      0, bucket.Count - 1);
                members[m] = bucket[idx];
            }

            // The runtime tracks the mean of the STORED members, so remember how far the
            // fitted centre sits from that mean and re-apply it each frame. Without this
            // the sphere would jump to the surface on the first update.
            Vector3 storedMean = Vector3.zero;
            for (int m = 0; m < take; m++) storedMean += verts[members[m]];
            storedMean /= take;

            float spread = 0f;
            for (int m = 0; m < take; m++) spread += Vector3.Distance(verts[members[m]], centre);
            spread /= take;

            bindings.Add(new SphereBinding
            {
                islandId = islandId,
                restCentre = centre,
                restRadius = radius,
                restSpread = Mathf.Max(spread, 1e-5f),
                centreOffset = centre - storedMean,
                members = members
            });
        }
    }

    [ContextMenu("Clear Bake")]
    public void ClearBake()
    {
        bindings = new List<SphereBinding>();
        bakedVertexCount = 0;
    }

    // ====================================================================
    // Runtime
    // ====================================================================

    void Start()
    {
        if (bindings == null || bindings.Count == 0)
        {
            Debug.LogWarning($"{name}: no baked spheres. Use the component context menu -> " +
                             "Bake Collider Spheres.", this);
            enabled = false;
            return;
        }

        if (dentManager == null) dentManager = GetComponentInParent<DentManager>();

        var mesh = GetComponent<MeshFilter>().sharedMesh;
        restPositions = mesh.vertices;

        if (restPositions.Length != bakedVertexCount)
        {
            Debug.LogError($"{name}: mesh has {restPositions.Length} verts but the bake was made " +
                           $"against {bakedVertexCount}. Re-bake.", this);
            enabled = false;
            return;
        }

        BuildFlatMemberIndex();

        if (createColliders) CreateColliders();
    }

    void BuildFlatMemberIndex()
    {
        memberFlatIndex = new int[bindings.Count + 1];
        int running = 0;
        for (int b = 0; b < bindings.Count; b++)
        {
            memberFlatIndex[b] = running;
            running += bindings[b].members.Length;
        }
        memberFlatIndex[bindings.Count] = running;

        accumulated = new Vector3[running];
    }

    void CreateColliders()
    {
        var rootGo = new GameObject("Dent Colliders");
        colliderRoot = rootGo.transform;
        colliderRoot.SetParent(transform, false);

        colliders = new SphereCollider[bindings.Count];
        for (int b = 0; b < bindings.Count; b++)
        {
            var go = new GameObject($"Sphere {b} (island {bindings[b].islandId})");
            go.layer = colliderLayer;
            go.transform.SetParent(colliderRoot, false);
            go.transform.localPosition = bindings[b].restCentre;

            var col = go.AddComponent<SphereCollider>();
            col.radius = bindings[b].restRadius;
            colliders[b] = col;
        }
    }

    void LateUpdate()
    {
        if (!updateWithDeformation || dentManager == null || colliders == null) return;

        float decay = dentManager.CurrentDecay;

        for (int b = 0; b < bindings.Count; b++)
        {
            var bind = bindings[b];
            int flat = memberFlatIndex[b];
            int count = bind.members.Length;

            Vector3 memberMean = Vector3.zero;

            for (int m = 0; m < count; m++)
            {
                Vector3 rest = restPositions[bind.members[m]];
                Vector3 current = dentManager.EvaluateDisplacementOS(rest, bind.islandId);

                // Mirror the shader's "strongest wins, decay the rest" rule so the
                // colliders keep the same history the dent texture does.
                Vector3 decayed = accumulated[flat + m] * decay;
                Vector3 result = current.sqrMagnitude > decayed.sqrMagnitude ? current : decayed;
                accumulated[flat + m] = result;

                memberMean += rest + result;
            }

            memberMean /= count;

            // Re-apply the bake-time inset so the sphere stays buried rather than
            // snapping to the surface.
            Vector3 centre = memberMean + bind.centreOffset;

            float spread = 0f;
            for (int m = 0; m < count; m++)
            {
                Vector3 deformed = restPositions[bind.members[m]] + accumulated[flat + m];
                spread += Vector3.Distance(deformed, centre);
            }
            spread /= count;

            var col = colliders[b];
            col.transform.localPosition = centre;
            col.radius = bind.restRadius * (spread / bind.restSpread);
        }
    }

    // ====================================================================

    void OnDrawGizmosSelected()
    {
        if (!drawGizmos || bindings == null) return;

        Gizmos.color = gizmoColour;
        Gizmos.matrix = transform.localToWorldMatrix;

        // In play mode the live colliders are authoritative; otherwise preview the bake.
        if (Application.isPlaying && colliders != null)
        {
            for (int b = 0; b < colliders.Length; b++)
                if (colliders[b] != null)
                    Gizmos.DrawWireSphere(colliders[b].transform.localPosition, colliders[b].radius);
        }
        else
        {
            for (int b = 0; b < bindings.Count; b++)
                Gizmos.DrawWireSphere(bindings[b].restCentre, bindings[b].restRadius);
        }

        Gizmos.matrix = Matrix4x4.identity;
    }
}
