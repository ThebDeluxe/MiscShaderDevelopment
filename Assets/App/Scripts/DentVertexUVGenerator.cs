using System.Collections.Generic;
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

    public int TextureSize { get; private set; }
    public int VertexCount { get; private set; }
    public bool IsGenerated { get; private set; }
    public Mesh PointMesh { get; private set; }

    /// <summary>Number of connected components found.</summary>
    public int IslandCount { get; private set; }
    /// <summary>Centroid of each island, in LOCAL space.</summary>
    public Vector3[] IslandCentroids { get; private set; }
    /// <summary>Distance from each island's centroid to its furthest vertex, in LOCAL space.</summary>
    public float[] IslandRadii { get; private set; }

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

        // Work on an instance so the mesh asset on disk is never modified.
        instancedMesh = Instantiate(meshFilter.sharedMesh);
        instancedMesh.name = meshFilter.sharedMesh.name + " (Dent Instance)";

        Vector3[] verts = instancedMesh.vertices;
        VertexCount = verts.Length;
        TextureSize = Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(VertexCount)));

        int[] islandOf = BuildIslands(instancedMesh, verts);

        var uv = new List<Vector4>(VertexCount);
        for (int i = 0; i < VertexCount; i++)
        {
            int x = i % TextureSize;
            int y = i / TextureSize;
            // +0.5 puts the coordinate at the exact texel centre.
            uv.Add(new Vector4((x + 0.5f) / TextureSize,
                               (y + 0.5f) / TextureSize,
                               islandOf[i],
                               0f));
        }

        // SetUVs' channel index maps directly onto TEXCOORD n.
        instancedMesh.SetUVs(targetUVChannel, uv);
        meshFilter.mesh = instancedMesh;

        BuildPointMesh(verts, uv);

        IsGenerated = true;
        Debug.Log($"{name}: dent data generated. {VertexCount} verts -> {TextureSize}x{TextureSize}, " +
                  $"{IslandCount} island(s), on TEXCOORD{targetUVChannel}.", this);
    }

    // --------------------------------------------------------------------
    // Connectivity
    // --------------------------------------------------------------------

    int[] BuildIslands(Mesh mesh, Vector3[] verts)
    {
        // Weld coincident vertices first. Unity splits verts at UV seams and hard
        // edges, so raw index connectivity would report far too many islands.
        int[] weld = new int[VertexCount];
        var lookup = new Dictionary<Vector3Int, int>(VertexCount);
        float inv = 1f / Mathf.Max(weldEpsilon, 1e-6f);

        for (int i = 0; i < VertexCount; i++)
        {
            var key = new Vector3Int(
                Mathf.RoundToInt(verts[i].x * inv),
                Mathf.RoundToInt(verts[i].y * inv),
                Mathf.RoundToInt(verts[i].z * inv));

            if (lookup.TryGetValue(key, out int rep)) weld[i] = rep;
            else { lookup[key] = i; weld[i] = i; }
        }

        // Union-find over triangles.
        int[] parent = new int[VertexCount];
        for (int i = 0; i < VertexCount; i++) parent[i] = i;

        int[] tris = mesh.triangles;
        for (int t = 0; t < tris.Length; t += 3)
        {
            int a = weld[tris[t]];
            int b = weld[tris[t + 1]];
            int c = weld[tris[t + 2]];
            Union(parent, a, b);
            Union(parent, b, c);
        }

        // Compact roots into 0..n-1 island ids.
        var idOfRoot = new Dictionary<int, int>();
        int[] islandOf = new int[VertexCount];

        for (int i = 0; i < VertexCount; i++)
        {
            int root = Find(parent, weld[i]);
            if (!idOfRoot.TryGetValue(root, out int id))
            {
                id = idOfRoot.Count;
                idOfRoot[root] = id;
            }
            islandOf[i] = id;
        }

        IslandCount = idOfRoot.Count;

        // Centroid and radius per island, in local space.
        var sums = new Vector3[IslandCount];
        var counts = new int[IslandCount];
        for (int i = 0; i < VertexCount; i++)
        {
            sums[islandOf[i]] += verts[i];
            counts[islandOf[i]]++;
        }

        IslandCentroids = new Vector3[IslandCount];
        IslandRadii = new float[IslandCount];
        for (int i = 0; i < IslandCount; i++)
            IslandCentroids[i] = counts[i] > 0 ? sums[i] / counts[i] : Vector3.zero;

        for (int i = 0; i < VertexCount; i++)
        {
            int id = islandOf[i];
            float d = Vector3.Distance(verts[i], IslandCentroids[id]);
            if (d > IslandRadii[id]) IslandRadii[id] = d;
        }

        return islandOf;
    }

    static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];   // path halving
            i = parent[i];
        }
        return i;
    }

    static void Union(int[] parent, int a, int b)
    {
        a = Find(parent, a);
        b = Find(parent, b);
        if (a != b) parent[b] = a;
    }

    // --------------------------------------------------------------------

    void BuildPointMesh(Vector3[] verts, List<Vector4> uv)
    {
        PointMesh = new Mesh
        {
            name = instancedMesh.name + " (Dent Points)",
            indexFormat = VertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
        };

        PointMesh.SetVertices(verts);
        PointMesh.SetUVs(targetUVChannel, uv);

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
