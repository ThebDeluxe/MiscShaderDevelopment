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

    [Tooltip("How many points per island DentManager tests when working out that island's\n" +
             "rigid push. More is more accurate on awkward shapes, but costs a little CPU\n" +
             "each frame. A single centroid is not enough: on a toroid it lands in the hole.")]
    [Range(1, 128)] public int samplesPerIsland = 32;

    public int TextureSize { get; private set; }
    public int VertexCount { get; private set; }
    public bool IsGenerated { get; private set; }
    public Mesh PointMesh { get; private set; }

    /// <summary>Number of connected components found.</summary>
    public int IslandCount { get; private set; }
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
