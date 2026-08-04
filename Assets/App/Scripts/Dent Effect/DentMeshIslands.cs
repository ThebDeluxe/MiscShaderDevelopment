using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Connected-component analysis for meshes, shared by DentVertexUVGenerator and
/// DentColliderRig so there is only one copy of the connectivity logic.
/// </summary>
public static class DentMeshIslands
{
    /// <summary>
    /// Returns an island index per vertex.
    ///
    /// Coincident vertices are welded first: Unity splits vertices at UV seams and hard
    /// edges, so raw index connectivity would report far too many islands.
    /// </summary>
    public static int[] Build(Mesh mesh, Vector3[] verts, float weldEpsilon, out int islandCount)
    {
        int vertexCount = verts.Length;

        int[] weld = new int[vertexCount];
        var lookup = new Dictionary<Vector3Int, int>(vertexCount);
        float inv = 1f / Mathf.Max(weldEpsilon, 1e-6f);

        for (int i = 0; i < vertexCount; i++)
        {
            var key = new Vector3Int(
                Mathf.RoundToInt(verts[i].x * inv),
                Mathf.RoundToInt(verts[i].y * inv),
                Mathf.RoundToInt(verts[i].z * inv));

            if (lookup.TryGetValue(key, out int rep)) weld[i] = rep;
            else { lookup[key] = i; weld[i] = i; }
        }

        int[] parent = new int[vertexCount];
        for (int i = 0; i < vertexCount; i++) parent[i] = i;

        int[] tris = mesh.triangles;
        for (int t = 0; t < tris.Length; t += 3)
        {
            int a = weld[tris[t]];
            int b = weld[tris[t + 1]];
            int c = weld[tris[t + 2]];
            Union(parent, a, b);
            Union(parent, b, c);
        }

        var idOfRoot = new Dictionary<int, int>();
        int[] islandOf = new int[vertexCount];

        for (int i = 0; i < vertexCount; i++)
        {
            int root = Find(parent, weld[i]);
            if (!idOfRoot.TryGetValue(root, out int id))
            {
                id = idOfRoot.Count;
                idOfRoot[root] = id;
            }
            islandOf[i] = id;
        }

        islandCount = idOfRoot.Count;
        return islandOf;
    }

    /// <summary>Vertex indices belonging to each island.</summary>
    public static List<int>[] GroupByIsland(int[] islandOf, int islandCount)
    {
        var members = new List<int>[islandCount];
        for (int i = 0; i < islandCount; i++) members[i] = new List<int>();
        for (int i = 0; i < islandOf.Length; i++) members[islandOf[i]].Add(i);
        return members;
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
}
