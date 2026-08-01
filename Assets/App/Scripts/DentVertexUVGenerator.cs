using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Generates a synthetic "index mapping" UV set where every vertex owns exactly one
/// unique texel: vertex i -> texel (i % size, i / size).
///
/// This deliberately does NOT touch UV0 (the authored unwrap). It exists purely so the
/// dent system has a guaranteed one-to-one vertex/texel mapping, independent of whatever
/// the real unwrap looks like.
///
/// Also builds a companion mesh with MeshTopology.Points, used only by DentManager's
/// stamp pass. Points are essential: with triangle topology the rasteriser would fill
/// the (meaningless) area between three arbitrarily-indexed vertices, smearing across
/// most of the texture.
/// </summary>
[RequireComponent(typeof(MeshFilter))]
public class DentVertexUVGenerator : MonoBehaviour
{
    [Tooltip("TEXCOORD channel to write the index mapping into.\n" +
             "2 = TEXCOORD2 = Mesh.uv3 = Shader Graph 'UV2'.\n" +
             "Must match the channel the stamp shader and Clay_Shader read.")]
    [Range(1, 3)] public int targetUVChannel = 2;

    public int TextureSize { get; private set; }
    public int VertexCount { get; private set; }
    public bool IsGenerated { get; private set; }
    public Mesh PointMesh { get; private set; }

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
            Debug.LogWarning($"{name}: DentVertexUVGenerator found no mesh to generate UVs for.", this);
            return;
        }

        // Work on an instance so the mesh asset on disk is never modified.
        instancedMesh = Instantiate(meshFilter.sharedMesh);
        instancedMesh.name = meshFilter.sharedMesh.name + " (Dent UV Instance)";

        VertexCount = instancedMesh.vertexCount;
        TextureSize = Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(VertexCount)));

        Vector2[] indexUV = new Vector2[VertexCount];
        for (int i = 0; i < VertexCount; i++)
        {
            int x = i % TextureSize;
            int y = i / TextureSize;
            // +0.5 puts the coordinate at the exact texel centre.
            indexUV[i] = new Vector2((x + 0.5f) / TextureSize, (y + 0.5f) / TextureSize);
        }

        ApplyUV(instancedMesh, indexUV);
        meshFilter.mesh = instancedMesh;

        BuildPointMesh(instancedMesh, indexUV);

        IsGenerated = true;
        Debug.Log($"{name}: dent UV generated. {VertexCount} verts -> {TextureSize}x{TextureSize} " +
                  $"({TextureSize * TextureSize} texels available) on TEXCOORD{targetUVChannel}.", this);
    }

    void ApplyUV(Mesh mesh, Vector2[] uv)
    {
        // Unity naming: Mesh.uv = TEXCOORD0, uv2 = TEXCOORD1, uv3 = TEXCOORD2, uv4 = TEXCOORD3.
        switch (targetUVChannel)
        {
            case 1: mesh.uv2 = uv; break;
            case 2: mesh.uv3 = uv; break;
            case 3: mesh.uv4 = uv; break;
        }
    }

    void BuildPointMesh(Mesh source, Vector2[] indexUV)
    {
        PointMesh = new Mesh
        {
            name = source.name + " (Dent Points)",
            indexFormat = VertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
        };

        PointMesh.SetVertices(source.vertices);
        ApplyUV(PointMesh, indexUV);

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
