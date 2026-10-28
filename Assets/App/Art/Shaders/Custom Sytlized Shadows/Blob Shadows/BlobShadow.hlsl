#ifndef BLOB_SHADOW_INCLUDED
#define BLOB_SHADOW_INCLUDED

// Set globally by BlobShadowSystem. Do NOT also add these as Shader Graph properties.
TEXTURE2D(_BlobShadowTex);
SAMPLER(sampler_BlobShadowTex);
float4 _BlobShadowParams; // xy = origin (xz), z = 1 / worldSize

// Returns shadow amount 0..1 (0 = no shadow).
float SampleBlobShadow(float3 positionWS)
{
    float2 uv = (positionWS.xz - _BlobShadowParams.xy) * _BlobShadowParams.z;
    if (any(uv < 0.0) || any(uv > 1.0)) return 0.0;
    return SAMPLE_TEXTURE2D_LOD(_BlobShadowTex, sampler_BlobShadowTex, uv, 0).r;
}

// Shader Graph Custom Function (File mode, name "BlobShadow").
// Output is a multiplier: 1 = fully lit, lower = shadowed.
void BlobShadow_float(float3 PositionWS, float Intensity, out float Shadow)
{
    Shadow = 1.0 - SampleBlobShadow(PositionWS) * Intensity;
}

void BlobShadow_half(float3 PositionWS, half Intensity, out half Shadow)
{
    Shadow = 1.0 - SampleBlobShadow(PositionWS) * Intensity;
}

#endif
