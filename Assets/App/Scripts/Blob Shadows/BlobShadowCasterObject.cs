using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

// Blob shadow caster for regular GameObjects (not in a SubScene).
// For baked/ECS units, use BlobShadowCasterAuthoring instead.
public class BlobShadowCasterObject : MonoBehaviour
{
    public static readonly List<BlobShadowCasterObject> All = new List<BlobShadowCasterObject>();

    [Tooltip("Where the feet are, relative to this object's pivot.")]
    public Vector3 GroundOffset = Vector3.zero;
    [Tooltip("How tall the shadow caster is, in metres.")]
    public float Height = 1.8f;
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

    public BlobCasterData GetCasterData(float heightFade)
    {
        Vector3 g = transform.TransformPoint(GroundOffset);
        float fade = heightFade > 0f ? math.saturate(1f - g.y / heightFade) : 1f;
        return new BlobCasterData
        {
            GroundAndRadius = new float4(g.x, g.y, g.z, Radius),
            HeightStrength  = new float4(Height, Strength * fade, 0f, 0f)
        };
    }

    void OnDrawGizmosSelected() =>
        BlobShadowGizmos.Draw(transform.TransformPoint(GroundOffset), Height, Radius);

    // Keeps the list clean when "Enter Play Mode Options" skips domain reload.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => All.Clear();
}
