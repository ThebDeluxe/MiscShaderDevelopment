using Unity.Mathematics;
using UnityEngine;

// Per-caster data sent to the GPU. Layout must match "Caster" in BlobShadowSplat.shader.
public struct BlobCasterData
{
    public float4 GroundAndRadius; // xyz = ground position (world), w = radius
    public float4 HeightStrength;  // x = shadow caster height, y = strength, zw unused
}

// Turns the directional light's angle into a ground offset per metre of height.
// Shadow length = height * tan(angle from vertical) = height * horizontal / vertical.
public static class BlobShadowLight
{
    // Caps shadow length when the light is close to the horizon (4 = 4 m of shadow per 1 m of height).
    public const float MaxLengthPerHeight = 4f;

    public static Light FindSun()
    {
        if (RenderSettings.sun != null) return RenderSettings.sun;
        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            if (light.type == LightType.Directional) return light;
        return null;
    }

    public static float2 ComputeOffsetPerHeight(Vector3 lightForward)
    {
        float down = math.max(-lightForward.y, 1e-4f);
        float2 offset = new float2(lightForward.x, lightForward.z) / down;
        float length = math.length(offset);
        if (length > MaxLengthPerHeight) offset *= MaxLengthPerHeight / length;
        return offset;
    }

    public static float2 CurrentOffsetPerHeight()
    {
        var sun = FindSun();
        return sun != null ? ComputeOffsetPerHeight(sun.transform.forward) : float2.zero;
    }
}

public static class BlobShadowGizmos
{
    const float Dot = 0.06f;

    // Green = ground position, cyan = shadow caster height, orange = shadow length, yellow = light ray.
    public static void Draw(Vector3 ground, float height, float radius)
    {
        float2 o = BlobShadowLight.CurrentOffsetPerHeight();
        Vector3 top  = ground + Vector3.up * height;
        float topY   = ground.y + height;
        Vector3 feetOnGround = new Vector3(ground.x + o.x * ground.y, 0f, ground.z + o.y * ground.y);
        Vector3 tipOnGround  = new Vector3(ground.x + o.x * topY,     0f, ground.z + o.y * topY);

        Gizmos.color = Color.green;
        Gizmos.DrawSphere(ground, Dot);

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(ground, top);
        Gizmos.DrawSphere(top, Dot);

        Gizmos.color = new Color(1f, 0.5f, 0f);
        Gizmos.DrawLine(feetOnGround, tipOnGround);
        Gizmos.DrawSphere(tipOnGround, Dot);

        Gizmos.color = new Color(1f, 1f, 0f, 0.4f);
        Gizmos.DrawLine(top, tipOnGround);

#if UNITY_EDITOR
        UnityEditor.Handles.color = new Color(1f, 0.5f, 0f);
        UnityEditor.Handles.DrawWireDisc(feetOnGround, Vector3.up, radius);
        UnityEditor.Handles.DrawWireDisc(tipOnGround,  Vector3.up, radius);
#endif
    }
}
