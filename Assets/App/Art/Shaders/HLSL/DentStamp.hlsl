#ifndef DENTSTAMP_INCLUDED
#define DENTSTAMP_INCLUDED

// Shared dent evaluation used by the stamp pass.
//
// Data layout (filled by DentManager.BuildDentArrays):
//   _DentPos[i]    : xyz = world position of the source,  w = shape id (0 = sphere, 1 = flat)
//   _DentAxis[i]   : xyz = world push direction (source's +Z), w = axial reach (flat only)
//   _DentParams[i] : x = inner radius, y = outer radius, z = intensity, w = unused
//
// Everything is evaluated in WORLD space (that's where the sources live), but the
// resulting displacement is converted to OBJECT space before being stored, so old
// dents don't swim around when the character rotates.

#define DENT_MAX 32

float4 _DentPos[DENT_MAX];
float4 _DentAxis[DENT_MAX];
float4 _DentParams[DENT_MAX];
int    _DentCount;

// --------------------------------------------------------------------
// Shape falloffs. Each returns 0..1 for "how much is this point affected".
// Keep these self-contained: they're the bits you'd port to C# if the
// collider proxy ever needs to match the visual deformation.
// --------------------------------------------------------------------

// Round, soft-edged. Full strength inside 'inner', fading to nothing at 'outer'.
float DentFalloff_Sphere(float3 toPoint, float inner, float outer)
{
    float d = length(toPoint);
    return 1.0 - smoothstep(inner, outer, d);
}

// Flat-bottomed, round-edged: like pressing the end of a cylinder into clay.
// 'radial' controls the round profile, 'axial' limits how deep along the axis it reaches.
float DentFalloff_Flat(float3 toPoint, float3 axis, float inner, float outer, float reach)
{
    float  axial   = dot(toPoint, axis);        // distance along the axis
    float3 radialV = toPoint - axial * axis;    // component across the axis
    float  radial  = length(radialV);

    float radialFalloff = 1.0 - smoothstep(inner, outer, radial);
    float axialFalloff  = 1.0 - smoothstep(0.0, max(reach, 1e-4), abs(axial));

    return radialFalloff * axialFalloff;
}

// --------------------------------------------------------------------
// Returns the strongest displacement affecting this surface point,
// as an OBJECT space vector. Magnitude is implicit in the vector length.
// --------------------------------------------------------------------
void CalculateDentVector_float(float3 WorldPos, out float3 Displacement)
{
    float3 bestAxisWS = float3(0, 0, 0);
    float  bestMag    = 0.0;

    for (int i = 0; i < _DentCount; i++)
    {
        float3 toPoint = WorldPos - _DentPos[i].xyz;
        float3 axis    = _DentAxis[i].xyz;

        float inner = _DentParams[i].x;
        float outer = _DentParams[i].y;

        float falloff = (_DentPos[i].w < 0.5)
            ? DentFalloff_Sphere(toPoint, inner, outer)
            : DentFalloff_Flat(toPoint, axis, inner, outer, _DentAxis[i].w);

        float mag = falloff * _DentParams[i].z;

        // Strongest source wins, rather than summing. Overlapping dents of the
        // same depth then read as one dent, not a doubly-deep one.
        if (mag > bestMag)
        {
            bestMag    = mag;
            bestAxisWS = axis;
        }
    }

    // One matrix multiply, outside the loop.
    Displacement = TransformWorldToObjectDir(bestAxisWS, false) * bestMag;
}

#endif // DENTSTAMP_INCLUDED
