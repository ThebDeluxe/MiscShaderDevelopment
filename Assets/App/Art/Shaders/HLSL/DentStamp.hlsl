#ifndef DENTSTAMP_INCLUDED
#define DENTSTAMP_INCLUDED

float4 _DentData[32];
float _DentIntensity[32];
int _DentCount;

void CalculateDentMask_float(float3 WorldPos, out float Mask)
{
    float m = 0;
    for (int i = 0; i < _DentCount; i++)
    {
        float3 dentPos = _DentData[i].xyz;
        float radius = _DentData[i].w;
        float dist = distance(WorldPos, dentPos);
        float falloff = saturate(1.0 - dist / radius);
        falloff = falloff * falloff * (3.0 - 2.0 * falloff);
        m = max(m, falloff * _DentIntensity[i]);
    }
    Mask = m;
}

#endif // DENTSTAMP_INCLUDED