using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

// Blob shadow caster for regular GameObjects (not in a SubScene).
// For baked/ECS units, use BlobShadowCasterAuthoring instead.
public class BlobShadowCasterObject : MonoBehaviour
{
    public static readonly List<BlobShadowCasterObject> All = new List<BlobShadowCasterObject>();

    public float Radius = 0.5f;
    [Range(0f, 1f)] public float Strength = 0.6f;

    int _index = -1;

    void OnEnable()
    {
        _index = All.Count;
        All.Add(this);
    }

    void OnDisable()
    {
        // Swap-remove so disabling is O(1) even with many casters.
        int last = All.Count - 1;
        if (_index >= 0 && _index <= last)
        {
            All[_index] = All[last];
            All[_index]._index = _index;
            All.RemoveAt(last);
        }
        _index = -1;
    }

    public float4 GetCasterData(float heightFade)
    {
        Vector3 p = transform.position;
        float fade = heightFade > 0f ? math.saturate(1f - p.y / heightFade) : 1f;
        return new float4(p.x, p.z, Radius, Strength * fade);
    }

    // Keeps the list clean when "Enter Play Mode Options" skips domain reload.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => All.Clear();
}
