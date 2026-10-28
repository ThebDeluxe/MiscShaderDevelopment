#ifndef HARD_SHADOW_INCLUDED
#define HARD_SHADOW_INCLUDED

void MainLightHardShadow_float(float3 WorldPos, out float Shadow, out float3 LightDir, out float3 LightColor)
{
#ifdef SHADERGRAPH_PREVIEW
    Shadow = 1;
    LightDir = normalize(float3(0.5, 0.5, 0));
    LightColor = 1;
#else
    float4 shadowCoord = TransformWorldToShadowCoord(WorldPos);
    Shadow = MainLightRealtimeShadow(shadowCoord); // single bilinear compare tap with Hard Shadows
    Light mainLight = GetMainLight();
    LightDir = mainLight.direction;
    LightColor = mainLight.color;
#endif
}

#endif