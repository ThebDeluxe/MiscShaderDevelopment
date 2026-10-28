#ifndef UNIT_SHADOW_INCLUDED
#define UNIT_SHADOW_INCLUDED

#ifndef SHADERGRAPH_PREVIEW
TEXTURE2D(_UnitShadowMap);
float4x4 _UnitShadowView;
float4 _UnitShadowParams; // x: 1/coverage, y: near, z: 1/(far-near), w: resolution
float _UnitShadowBias;    // normalised depth units

float3 UnitShadowCoord(float3 worldPos)
{
    float3 v = mul(_UnitShadowView, float4(worldPos, 1.0)).xyz;
    float2 uv = v.xy * _UnitShadowParams.x + 0.5;
    float depth = (-v.z - _UnitShadowParams.y) * _UnitShadowParams.z;
    return float3(uv, depth);
}

float UnitShadowTexel(int2 p)
{
    int res = (int)_UnitShadowParams.w;
    p = clamp(p, 0, res - 1);
    return LOAD_TEXTURE2D(_UnitShadowMap, p).r;
}

// 2x2 bilinear-weighted comparison, same idea as hardware PCF
float UnitShadowBilinear(float2 uv, float depth)
{
    float2 st = uv * _UnitShadowParams.w - 0.5;
    int2 p = (int2)floor(st);
    float2 f = frac(st);
    float4 occ = float4(
        UnitShadowTexel(p),
        UnitShadowTexel(p + int2(1, 0)),
        UnitShadowTexel(p + int2(0, 1)),
        UnitShadowTexel(p + int2(1, 1)));
    float4 lit = step((depth - _UnitShadowBias).xxxx, occ);
    return lerp(lerp(lit.x, lit.y, f.x), lerp(lit.z, lit.w, f.x), f.y);
}
#endif

// Receiver: 0 = shadowed, 1 = lit. Radius in shadow-map texels (0 = 2x2 bilinear only).
void UnitShadow_float(float3 WorldPos, float Radius, out float Shadow)
{
#ifdef SHADERGRAPH_PREVIEW
    Shadow = 1;
#else
    float3 c = UnitShadowCoord(WorldPos);
    Shadow = 1;
    if (all(c.xy > 0) && all(c.xy < 1) && c.z < 1)
    {
        float texel = 1.0 / _UnitShadowParams.w;
        float spread = max(Radius, 0.0) * texel * 0.5; // taps span +/- Radius texels
        float sum = 0;
        float wsum = 0;
        [unroll]
        for (int y = -2; y <= 2; y++)
        [unroll]
            for (int x = -2; x <= 2; x++)
            {
                float w = (3 - abs(x)) * (3 - abs(y)); // tent weights 1,2,3,2,1
                sum += w * UnitShadowBilinear(c.xy + float2(x, y) * spread, c.z);
                wsum += w;
            }
        Shadow = sum / wsum;
    }
#endif
}
// Proxy: writes distance from the light, 0 = near, 1 = far
void UnitShadowDepth_float(float3 WorldPos, out float Depth)
{
#ifdef SHADERGRAPH_PREVIEW
    Depth = 0;
#else
    Depth = saturate(UnitShadowCoord(WorldPos).z);
#endif
}

#endif