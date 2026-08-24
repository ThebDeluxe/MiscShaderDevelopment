#ifndef CLAYHEIGHTFIELD_INCLUDED
#define CLAYHEIGHTFIELD_INCLUDED

// Terrain as a local height map, rather than as a scattering of plane contacts.
//
// WHY A SEPARATE PATH FOR TERRAIN
// Terrain is genuinely a heightfield - one surface height per point on the ground - so
// sampling it as one is exact rather than an approximation. Describing the same ground with
// a handful of plane stamps is what makes rolling over it look messy: each plane is a flat
// answer to a curved question, they disagree where they meet, and which ones exist changes
// as the character moves, so the seams travel.
//
// A grid fixed relative to the character removes all of that. The samples are always in the
// same places, so nothing jumps between frames, and the surface is continuous by
// construction because it is one field rather than several stamps competing.
//
// The limit is inherent: a height map is a function of x and z, so it cannot represent a
// wall, an overhang or a ceiling. Those keep the ordinary contact path - this only replaces
// the ground beneath.

TEXTURE2D(_HeightField);
SAMPLER(sampler_HeightField);

// xy = world XZ of the grid's minimum corner, zw = world size of the grid
float4 _HeightFieldArea;

// x = lowest sampled height, y = height range, z = press strength, w = 1 when in use
float4 _HeightFieldParams;

// How far the push follows the surface normal rather than straight up, 0..1
float _HeightFieldNormalPress;

// xyz = character centre, w = deepest press allowed
float4 _HeightFieldCentre;

// x = bulge amount, y = how far above the surface it reaches
// z = decay multiplier, w = scale retained along the press (a flatten scale)
float4 _HeightFieldBulge;

/// Surface height at a world position, in world units. Bilinear, so the surface is smooth
/// between samples rather than stepped.
float ClaySampleHeight(float3 worldPos)
{
    float2 uv = (worldPos.xz - _HeightFieldArea.xy) / max(_HeightFieldArea.zw, 1e-4);

    // Outside the grid there is nothing to press against.
    if (any(saturate(uv) != uv)) return -1e9;

    float encoded = SAMPLE_TEXTURE2D_LOD(_HeightField, sampler_HeightField, uv, 0).r;
    return _HeightFieldParams.x + encoded * _HeightFieldParams.y;
}

/// <summary>
/// Pushes a vertex up out of the ground, and returns the surface normal it was pressed
/// against.
///
/// The normal comes from the height GRADIENT, so a slope presses along its own facing
/// rather than straight up - which is what a plane stamp would have to be told, and what it
/// gets wrong when the ground curves between one stamp and the next.
/// </summary>
float3 ClayHeightFieldPush(float3 worldPos, out float3 surfaceNormal, out float depth)
{
    surfaceNormal = float3(0, 1, 0);
    depth = 0.0;

    if (_HeightFieldParams.w < 0.5) return float3(0, 0, 0);

    float height = ClaySampleHeight(worldPos);
    if (height < -1e8) return float3(0, 0, 0);

    depth = height - worldPos.y;

    // Central differences across one grid cell, which is the finest detail the field holds.
    float2 step = _HeightFieldArea.zw * 0.02;

    float hx = ClaySampleHeight(worldPos + float3(step.x, 0, 0))
             - ClaySampleHeight(worldPos - float3(step.x, 0, 0));
    float hz = ClaySampleHeight(worldPos + float3(0, 0, step.y))
             - ClaySampleHeight(worldPos - float3(0, 0, step.y));

    surfaceNormal = normalize(float3(-hx, 2.0 * step.x, -hz));

    // --- above the surface: displaced material piling up ---
    if (depth <= 0.0)
    {
        float above = -depth;
        float reach = _HeightFieldBulge.y;

        if (_HeightFieldBulge.x == 0.0 || reach <= 1e-5 || above > reach)
        {
            depth = 0.0;
            return float3(0, 0, 0);
        }

        // Outward from the character's own axis, which is where the displaced volume has to
        // go - the ground it was pressed out of is directly below.
        float3 radial = worldPos - _HeightFieldCentre.xyz;
        radial.y = 0.0;

        float len = length(radial);
        if (len < 1e-4) { depth = 0.0; return float3(0, 0, 0); }

        // Zero at the surface, rising and falling again. A bulge at full strength right
        // where the press ends would tear the two apart along the contact line.
        float peak = reach * 0.35;
        float band = smoothstep(0.0, peak, above) * (1.0 - smoothstep(peak, reach, above));

        depth = 0.0;
        return (radial / len) * (_HeightFieldBulge.x * band);
    }

    // --- below the surface: pressed back out ---
    //
    // Capped, because a heightfield has no natural limit the way a stamp's depth does. Left
    // uncapped, a vertex well below the ground is pushed the whole way, so a rolling
    // character presses every vertex that passes through contact to its full extent and the
    // accumulated history reads as the whole shape flattening.
    //
    // Eased into the limit rather than clipped at it. A hard min gives every vertex past the
    // cap exactly the same depth, so the dent gets a flat floor and a sharp rim where the
    // clipping begins - it reads as a punched hole rather than a press.
    float limit = _HeightFieldCentre.w;
    depth = limit * (1.0 - exp(-depth / max(limit, 1e-4)));

    // The same scale the stamps keep along their press axis, so terrain does not dent harder
    // than a plane does for the same contact.
    depth *= (1.0 - saturate(_HeightFieldBulge.w));

    // Along the normal, by the PERPENDICULAR distance to the surface - which is the vertical
    // depth times the normal's own y, since that is the cosine of the slope. Pushing along
    // the normal by the vertical depth instead would overshoot on a slope and drag the mesh
    // down it, which is what makes a normal press look like it slides.
    //
    // Straight up is kept as an option because it is what a heightfield literally describes:
    // ground holding a vertex at its own height, with no sideways component at all.
    float perpendicular = depth * max(surfaceNormal.y, 1e-3);

    float3 alongNormal = surfaceNormal * perpendicular;
    float3 alongUp = float3(0, depth, 0);

    return lerp(alongUp, alongNormal, saturate(_HeightFieldNormalPress))
           * saturate(_HeightFieldParams.z);
}

#endif // CLAYHEIGHTFIELD_INCLUDED
