using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Prepares a mesh for the dent system. Two jobs:
///
/// 1. INDEX MAPPING - a synthetic UV set where every vertex owns exactly one unique
///    texel: vertex i -> texel (i % size, i / size). This deliberately does not touch
///    UV0, so it works regardless of what the authored unwrap looks like.
///
/// 2. ISLAND IDS - flood fills the mesh into connected components and stores each
///    vertex's island index alongside the mapping. This lets the stamp pass move a
///    whole disconnected part rigidly instead of crushing its relief, which is what
///    keeps decorations sitting proud of the body they rest on.
///
/// Layout in the target UV channel (a float4):
///     xy = texel centre for this vertex
///     z  = island index
///     w  = unused
///
/// Also builds a companion mesh with MeshTopology.Points, used only by the stamp
/// pass. Points are essential: with triangle topology the rasteriser would fill the
/// (meaningless) area between three arbitrarily indexed vertices.
/// </summary>
[RequireComponent(typeof(MeshFilter))]
public class DentVertexUVGenerator : MonoBehaviour
{
    [Tooltip("TEXCOORD channel to write the index mapping and island ids into.\n" +
             "2 = TEXCOORD2 = Shader Graph 'UV2'.\n" +
             "Must match the channel the stamp shader and Clay_Shader read.")]
    [Range(1, 7)] public int targetUVChannel = 2;

    [Tooltip("Vertices closer than this are treated as the same point when working out\n" +
             "connectivity. Needed because Unity splits vertices at UV seams and hard\n" +
             "edges, which would otherwise look like separate islands.")]
    public float weldEpsilon = 0.0001f;

    [Tooltip("How many points per island DentManager tests when working out that island's\n" +
             "rigid push. More is more accurate on awkward shapes, but costs a little CPU\n" +
             "each frame. A single centroid is not enough: on a toroid it lands in the hole.")]
    [Range(1, 128)] public int samplesPerIsland = 32;

    public int TextureWidth { get; private set; }
    public int TextureHeight { get; private set; }
    public int VertexCount { get; private set; }
    public bool IsGenerated { get; private set; }
    public Mesh PointMesh { get; private set; }

    /// <summary>Number of connected components found.</summary>
    public int IslandCount { get; private set; }

    /// <summary>
    /// Mesh LOD levels on the source mesh. 1 when the mesh has none, or when the running
    /// Unity version predates the feature.
    /// </summary>
    public int MeshLodCount { get; private set; } = 1;

    /// <summary>Levels surviving on the instance this component actually renders.</summary>
    public int InstanceMeshLodCount { get; private set; } = 1;

    /// <summary>Centroid of each island, in LOCAL space. Used only as a size reference.</summary>
    public Vector3[] IslandCentroids { get; private set; }
    /// <summary>Distance from each island's centroid to its furthest vertex, in LOCAL space.</summary>
    public float[] IslandRadii { get; private set; }
    /// <summary>Sample points on each island, in LOCAL space, used to evaluate its rigid push.</summary>
    public Vector3[][] IslandSamples { get; private set; }

    Mesh instancedMesh;

    void Awake()
    {
        Generate();
    }

    public void Generate(bool force = false)
    {
        if (IsGenerated && !force) return;

        var meshFilter = GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null)
        {
            Debug.LogWarning($"{name}: DentVertexUVGenerator found no mesh to work with.", this);
            return;
        }

        // In the editor Unity keeps a CPU copy of mesh data regardless of this flag, so a
        // non-readable mesh only fails in a BUILD - where vertices comes back empty, the
        // instanced mesh ends up with no geometry, and the character silently vanishes
        // while physics carries on as normal.
        if (!meshFilter.sharedMesh.isReadable)
        {
            Debug.LogError($"{name}: mesh '{meshFilter.sharedMesh.name}' is not readable. " +
                           "Enable Read/Write in the model's import settings - this system " +
                           "reads vertices and triangles at runtime. Without it the mesh will " +
                           "render correctly in the editor and disappear in a build.", this);
            return;
        }

        // Work on an instance so the mesh asset on disk is never modified.
        instancedMesh = Instantiate(meshFilter.sharedMesh);
        instancedMesh.name = meshFilter.sharedMesh.name + " (Dent Instance)";

        // Mesh LOD levels all share ONE vertex buffer - each level is just a range of the
        // index buffer - so a per-vertex mapping is LOD invariant: vertex 500 is the same
        // point at every level and reads the same texel. Nothing here needs to change for
        // it. What is worth checking is that instantiating has not dropped the LOD ranges,
        // which would silently cost the renderer its LODs.
        MeshLodCount = ReadLodCount(meshFilter.sharedMesh);
        InstanceMeshLodCount = ReadLodCount(instancedMesh);

        Vector3[] verts = instancedMesh.vertices;
        VertexCount = verts.Length;

        // Rectangular, not square. The mapping is i % width, i / width, so it never needed
        // a square - and forcing one means rounding the side up to a power of two, which
        // can leave over half the texels unused. A power-of-two width keeps rows aligned
        // while the height takes only as many rows as there are vertices to store.
        TextureWidth = Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(VertexCount)));
        TextureHeight = Mathf.Max(1, Mathf.CeilToInt(VertexCount / (float)TextureWidth));

        int[] islandOf = BuildIslands(instancedMesh, verts);
        var uv = new List<Vector4>(VertexCount);
        for (int i = 0; i < VertexCount; i++)
        {
            int x = i % TextureWidth;
            int y = i / TextureWidth;
            // +0.5 puts the coordinate at the exact texel centre.
            uv.Add(new Vector4((x + 0.5f) / TextureWidth,
                               (y + 0.5f) / TextureHeight,
                               islandOf[i],
                               0f));
        }

        // SetUVs' channel index maps directly onto TEXCOORD n.
        instancedMesh.SetUVs(targetUVChannel, uv);
        meshFilter.mesh = instancedMesh;

        BuildPointMesh(verts, instancedMesh.normals, uv);

        IsGenerated = true;

        int texels = TextureWidth * TextureHeight;
        Debug.Log($"{name}: dent data generated. {VertexCount} verts -> {TextureWidth}x{TextureHeight} " +
                  $"({texels} texels, {(VertexCount / (float)texels):P0} used), " +
                  $"{IslandCount} island(s), on TEXCOORD{targetUVChannel}. " +
                  $"Mesh LOD levels: {MeshLodCount}.", this);

        if (InstanceMeshLodCount < MeshLodCount)
        {
            Debug.LogWarning($"{name}: the source mesh had {MeshLodCount} Mesh LOD levels but the " +
                             $"instance kept {InstanceMeshLodCount} - the renderer has lost its " +
                             "LODs. The dent mapping itself is unaffected, since all levels share " +
                             "one vertex buffer, but the LOD saving is gone.", this);
        }
    }

    // --------------------------------------------------------------------
    // Mesh LOD
    //
    // Queried by reflection so this compiles on Unity versions predating the feature, and
    // reports a single level when it is unavailable or unused.
    // --------------------------------------------------------------------

    static PropertyInfo lodCountProperty;
    static bool lodCountResolved;

    public static int ReadLodCount(Mesh mesh)
    {
        if (mesh == null) return 1;

        if (!lodCountResolved)
        {
            lodCountResolved = true;
            lodCountProperty = typeof(Mesh).GetProperty("lodCount",
                                                        BindingFlags.Public | BindingFlags.Instance);
        }

        if (lodCountProperty == null) return 1;

        try { return Mathf.Max(1, (int)lodCountProperty.GetValue(mesh)); }
        catch { return 1; }
    }

    // --------------------------------------------------------------------
    // Connectivity
    // --------------------------------------------------------------------

    int[] BuildIslands(Mesh mesh, Vector3[] verts)
    {
        int[] islandOf = DentMeshIslands.Build(mesh, verts, weldEpsilon, out int count);
        IslandCount = count;

        var members = DentMeshIslands.GroupByIsland(islandOf, IslandCount);

        IslandCentroids = new Vector3[IslandCount];
        IslandRadii = new float[IslandCount];
        IslandSamples = new Vector3[IslandCount][];

        int wanted = Mathf.Max(1, samplesPerIsland);

        for (int i = 0; i < IslandCount; i++)
        {
            var list = members[i];
            if (list.Count == 0)
            {
                IslandSamples[i] = new Vector3[0];
                continue;
            }

            Vector3 sum = Vector3.zero;
            for (int k = 0; k < list.Count; k++) sum += verts[list[k]];
            IslandCentroids[i] = sum / list.Count;

            float maxDist = 0f;
            for (int k = 0; k < list.Count; k++)
            {
                float d = Vector3.Distance(verts[list[k]], IslandCentroids[i]);
                if (d > maxDist) maxDist = d;
            }
            IslandRadii[i] = maxDist;

            // Evenly strided subset, so samples are spread over the whole island rather
            // than clustered wherever the vertex order happens to start.
            int take = Mathf.Min(wanted, list.Count);
            var samples = new Vector3[take];
            for (int k = 0; k < take; k++)
            {
                int index = Mathf.FloorToInt((float)k / take * list.Count);
                samples[k] = verts[list[Mathf.Clamp(index, 0, list.Count - 1)]];
            }
            IslandSamples[i] = samples;
        }

        return islandOf;
    }

    // --------------------------------------------------------------------

    void BuildPointMesh(Vector3[] verts, Vector3[] normals, List<Vector4> uv)
    {
        PointMesh = new Mesh
        {
            name = instancedMesh.name + " (Dent Points)",
            indexFormat = VertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
        };

        PointMesh.SetVertices(verts);
        PointMesh.SetUVs(targetUVChannel, uv);

        // The rim bulge in the stamp shader follows the surface, so it needs normals.
        if (normals != null && normals.Length == VertexCount)
            PointMesh.SetNormals(normals);
        else
            Debug.LogWarning($"{name}: mesh has no usable normals, so the dent rim bulge " +
                             "will have no direction to follow.", this);

        int[] pointIndices = new int[VertexCount];
        for (int i = 0; i < VertexCount; i++) pointIndices[i] = i;
        PointMesh.SetIndices(pointIndices, MeshTopology.Points, 0, false);

        // The stamp pass writes to texel space, so screen-space bounds are irrelevant,
        // but Unity still culls on them. Make them effectively infinite.
        PointMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
    }

    void OnDestroy()
    {
        if (PointMesh != null) DestroyImmediate(PointMesh);
        if (instancedMesh != null) DestroyImmediate(instancedMesh);
    }
}
