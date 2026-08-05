#ifndef DENTSTAMP_INCLUDED
#define DENTSTAMP_INCLUDED

// Shared dent evaluation used by the stamp pass.
//
// The stamp behaves like a SOLID OBJECT pressed into the surface, not a field of
// influence. Depth comes from geometry - how far a vertex sits inside the stamp
// volume - rather than from an authored intensity value.
//
// Convention: the stamp's +Z (axis) is the press direction, and its contact surface
// touches the source's origin. Vertices on the approach side of that surface (behind
// it, up to 'depth') are inside the stamp and get scaled forward onto it.
//
// All four shapes share ONE press function and ONE contact surface function. They
// differ only in cross-section (round vs square) and whether they have a flat face:
//   Cylinder / Square : flat out to 'inner', then a rounded rim out to 'outer'
//   Capsule           : the same punch with inner = 0, i.e. a hemisphere
//   Plane             : flat right to its edge, no rim
//
// ISLAND RIGIDITY
// Per-vertex pressing crushes the relief of separate shells: a decoration sitting
// proud of a body gets scaled toward the same contact plane, so it sinks into it.
// To avoid that, DentManager evaluates the press once per mesh island (on the CPU)
// and uploads the result. The shader blends between the per-vertex press and that
// single rigid push.
//
// Data layout (filled by DentManager):
//   _DentPos[i]    : xyz = world position of the contact point, w = shape id
//   _DentAxis[i]   : xyz = world press axis (+Z),               w = depth
//   _DentRight[i]  : xyz = world right (+X, orients Square),    w = flatten scale
//   _DentParams[i] : x = inner radius, y = outer radius, z = strength, w = spread amount
//                    For PLANE: x = half extent along +X, y = half extent along +Y,
//                    w = edge softness. A plane has no rim fillet, so the inner radius
//                    slot carries its second extent instead.
//   _DentBulge[i]  : x = rim bulge amount, y = bulge reach, z = normal bias,
//                    w = bulge driver (deepest penetration, already clamped by the CPU)
//   _DentDecay[i]  : x = decay multiplier for dents this stamp creates
//                    For PLANE: y, z = offset of the rectangle's centre from the source,
//                    measured along the source's own +X and +Y. The source itself stays
//                    under the character - it is what the splay radiates from and what
//                    range checks measure - so the rectangle has to be placed separately.
//   _IslandPush[j] : xyz = OBJECT space rigid push for island j, w = rigidity 0..1
//
// Shape ids: 0 = Capsule, 1 = Cylinder, 2 = Square, 3 = Plane.
//
// PLANE is not a punch. It is a hard surface the object RESTS ON, so displaced material
// cannot pile up behind it - there is no behind. Everything below the plane conforms to
// it, and the volume has nowhere to go but sideways, splaying outward just above the
// contact. That splay is driven by the bulge driver, which the CPU measures across the
// whole mesh, because no single vertex can know how squashed the object is.
//
// BULGE DRIVER
// Bulge strength scales with how far the stamp is pressed in. That is measured on the
// CPU as the deepest penetration anywhere on the mesh, then CLAMPED per source - without
// the clamp a single long protrusion dipping deep would inflate the bulge everywhere.
//
// PERPENDICULAR SPREAD
// Material pushed out of the way has to go somewhere. Two separate effects:
//
//  - Spread  : inside the contact, vertices slide sideways away from the axis.
//              Peaks around the inner radius, driven by how much was pushed in.
//
//  - Rim bulge : material displaced by a PUNCH piles up around the contact. The band is
//              centred on the contact plane and straddles it. It moves along -Z by
//              default; the vertex normal is tempting but wrong on its own, since on the
//              side of a ball it points sideways and below the equator it points
//              downward. Blend some in with _DentBulge[i].z to follow the surface.
//
//  - Plane splay : a PLANE has no behind, so its displaced volume goes sideways instead.
//
// DECAY
// The map's ALPHA channel carries the decay multiplier of whichever stamp wrote that
// texel, because the vector alone says nothing about where it came from. Decay is
// otherwise proportional - prev * decay^t - which means a deep dent and a shallow one
// shrink by the same fraction, so the deep one stays visible far longer. _DecayDepthBias
// adds a magnitude-dependent term so deeper dents fade faster and the two even out.

#define DENT_MAX   16
#define ISLAND_MAX 32

#define DENT_SHAPE_CAPSULE  0
#define DENT_SHAPE_CYLINDER 1
#define DENT_SHAPE_SQUARE   2
#define DENT_SHAPE_PLANE    3

float4 _DentPos[DENT_MAX];
float4 _DentAxis[DENT_MAX];
float4 _DentRight[DENT_MAX];
float4 _DentParams[DENT_MAX];
float4 _DentBulge[DENT_MAX];
float4 _DentDecay[DENT_MAX];
int    _DentCount;

float4 _IslandPush[ISLAND_MAX];
int    _IslandCount;

// --------------------------------------------------------------------
// Lateral distance from the press axis, per cross-section.
// --------------------------------------------------------------------

float DentLateral_Round(float3 toPoint, float3 axis)
{
    float  axial   = dot(toPoint, axis);
    float3 radialV = toPoint - axial * axis;
    return length(radialV);
}

// Chebyshev distance in the stamp's own right/up basis, so inner/outer read as
// half-widths of the square rather than radii.
float DentLateral_Square(float3 toPoint, float3 axis, float3 right)
{
    float3 up = cross(axis, right);
    float lx = abs(dot(toPoint, right));
    float ly = abs(dot(toPoint, up));
    return max(lx, ly);
}

// --------------------------------------------------------------------
// Contact surface of the punch, as an axial offset at lateral distance 'lat'.
//
// Flat out to 'inner', then a quarter-round fillet of radius (outer - inner) that
// recedes BACKWARDS, reaching -(outer - inner) at 'outer'. Past that there is no
// contact, signalled by a large negative value so the penetration clamps to zero.
//
// The rim is geometry, not a multiplier on the push. That matters: tapering the push
// instead would leave each vertex at a scaled copy of its ORIGINAL position, so the
// dent would inherit the body's own curvature rather than the stamp's shape - which
// looks soft and mushy whenever 'inner' is small.
//
// With inner = 0 this degenerates to a hemisphere, which is exactly the Capsule.
// --------------------------------------------------------------------
float DentSurfaceAxial(float lat, float inner, float outer)
{
    if (lat >= outer) return -1e9;   // outside the punch entirely
    if (lat <= inner) return 0.0;    // flat contact face

    float r = max(outer - inner, 1e-5);
    float d = lat - inner;
    return -(r - sqrt(max(r * r - d * d, 0.0)));
}

// --------------------------------------------------------------------
// The press.
//
//     target axial = surfaceAxial + (axial - surfaceAxial) * flatten
// so the push distance is  penetration * (1 - flatten).
// flatten = 0 is fully conformed, 1 is no change.
//
// 'depth' caps the penetration: vertices deeper than it all receive the SAME push,
// so they translate rigidly and keep their relative spacing. Set it comfortably
// larger than the deepest the stamp will sit, or the press cannot reach its own
// contact surface and the dent comes out soft and half-formed.
// --------------------------------------------------------------------
float DentPress_Axial(float axial, float surfaceAxial, float depth, float flatten)
{
    float penetration = clamp(surfaceAxial - axial, 0.0, depth);
    return penetration * (1.0 - flatten);
}

// Strongest press at a world position, as a WORLD space vector.
// WorldNormal is the vertex's own surface normal, which the rim bulge can follow.
// DecayMul comes from whichever stamp won, so the result carries its own fade rate.
float3 EvaluateDentWorld(float3 WorldPos, float3 WorldNormal, out float DecayMul)
{
    // Press is accumulated, bulge is not - see the combination notes further down.
    float3 pressAccum      = float3(0, 0, 0);
    float3 bestExtras      = float3(0, 0, 0);
    float  bestExtrasMagSq = 0.0;
    float  deepestPress    = 0.0;
    DecayMul = 1.0;

    for (int i = 0; i < _DentCount; i++)
    {
        float3 toPoint = WorldPos - _DentPos[i].xyz;
        float3 axis    = _DentAxis[i].xyz;
        float3 right   = _DentRight[i].xyz;

        float shapeId  = _DentPos[i].w;
        float depth    = _DentAxis[i].w;
        float flatten  = _DentRight[i].w;
        float inner    = _DentParams[i].x;
        float outer    = _DentParams[i].y;
        float strength = _DentParams[i].z;
        float spread   = _DentParams[i].w;
        float rimAmt   = _DentBulge[i].x;
        float rimReach = max(_DentBulge[i].y, 1.0);
        float rimBias  = saturate(_DentBulge[i].z);
        float driver   = _DentBulge[i].w;   // already clamped on the CPU

        bool isPlane = (shapeId > 2.5);

        float axial = dot(toPoint, axis);

        // Component across the axis: gives both the round cross-section distance and
        // the outward direction the spread pushes along.
        float3 radialV = toPoint - axial * axis;
        float  radial  = length(radialV);
        float3 outward = (radial > 1e-5) ? (radialV / radial) : float3(0, 0, 0);

        // Square and Plane use a Chebyshev cross-section; the other two are round.
        float lat = (shapeId > 1.5)
            ? DentLateral_Square(toPoint, axis, right)
            : radial;

        // Capsule has no flat face; Plane is flat right to its edge. Both mean no inner radius.
        float innerEff = (shapeId < 0.5 || isPlane) ? 0.0 : inner;

        // A Plane is a rectangle with independent half sizes, so it can be clamped to the
        // actual face of the collider that produced it. Without that the surface runs past
        // a ledge and keeps flattening the part of the mesh hanging over the drop.
        float surfaceAxial;
        float planeEdge = 1.0;

        if (isPlane)
        {
            float3 planeUp = cross(axis, right);

            // The rectangle is placed relative to the source rather than centred on it, so
            // the source can stay under the character while the surface is clamped to the
            // collider face that produced it.
            float2 planeOffset = float2(_DentDecay[i].y, _DentDecay[i].z);

            float lx = abs(dot(toPoint, right) - planeOffset.x);
            float ly = abs(dot(toPoint, planeUp) - planeOffset.y);

            float halfX = inner;   // no rim fillet on a plane, so this slot is the 2nd extent
            float halfY = outer;

            surfaceAxial = (lx <= halfX && ly <= halfY) ? 0.0 : -1e9;

            // Soften the last fraction of each edge, so the mesh bends over the lip instead
            // of shearing off along a hard line.
            float soft = max(saturate(_DentParams[i].w), 0.001);
            float sx = 1.0 - smoothstep(halfX * (1.0 - soft), halfX, lx);
            float sy = 1.0 - smoothstep(halfY * (1.0 - soft), halfY, ly);
            planeEdge = sx * sy;
        }
        else
        {
            surfaceAxial = DentSurfaceAxial(lat, innerEff, outer);
        }

        float push = DentPress_Axial(axial, surfaceAxial, depth, flatten) * planeEdge;

        // --- sideways spread, inside the contact ---
        // Rises from the centre, peaks around the inner radius, gone by the outer radius.
        // The lower bound on the ramp width stops a zero inner radius (Capsule) producing
        // a discontinuity at the axis.
        float peak    = max(innerEff, outer * 0.15);
        float rampIn  = smoothstep(0.0, peak, lat);
        float rampOut = 1.0 - smoothstep(peak, outer, lat);
        float bulge   = push * spread * rampIn * rampOut;

        // --- bulge ---
        float  rim;
        float3 rimDir;

        if (isPlane)
        {
            // Resting on a hard surface: the squashed volume splays OUTWARD, just above
            // the plane, and never crosses it.
            float height = max(driver * rimReach, 1e-5);
            float above  = (axial > 0.0) ? (1.0 - smoothstep(0.0, height, axial)) : 0.0;

            rim    = driver * rimAmt * above;
            rimDir = outward;
        }
        else
        {
            // A punch: displaced material piles up around the contact. The band is centred
            // ON the contact plane rather than sitting behind it, so the pile straddles the
            // surface instead of forming only on the approach side.
            float axialProf = 1.0 - smoothstep(0.0, max(driver, 1e-5), abs(axial));
            float rimIn     = smoothstep(innerEff, outer, lat);
            float rimOut    = 1.0 - smoothstep(outer, outer * rimReach, lat);

            rim    = driver * rimAmt * rimIn * rimOut * axialProf * (1.0 - flatten);
            rimDir = normalize(lerp(-axis, WorldNormal, rimBias) + 1e-6);
        }

        // --- combine ---
        // The PRESS is a constraint: "do not be inside this stamp". In a corner two of them
        // must BOTH be satisfied, so taking whichever is strongest would push a vertex out
        // of the wall while leaving it under the floor, and no fold appears.
        //
        // Plain summing is wrong too - two parallel stamps would stack into a double-deep
        // dent. Adding only the part not already covered along this axis gives both: a
        // parallel stamp contributes nothing extra, while a perpendicular one contributes
        // in full, which is exactly a right-angle fold.
        float wanted  = push * strength;
        float already = dot(pressAccum, axis);
        float extra   = max(wanted - already, 0.0);

        pressAccum += axis * extra;

        // The BULGE is not a constraint, just displaced material, so strongest still wins
        // there rather than piling up once per surface.
        float3 extras = (outward * bulge + rimDir * rim) * strength;
        float extrasMagSq = dot(extras, extras);
        if (extrasMagSq > bestExtrasMagSq)
        {
            bestExtrasMagSq = extrasMagSq;
            bestExtras = extras;
        }

        // Fade rate comes from whichever stamp actually pressed this vertex hardest.
        if (extra > deepestPress)
        {
            deepestPress = extra;
            DecayMul = max(_DentDecay[i].x, 0.0);
        }
    }

    return pressAccum + bestExtras;
}

// --------------------------------------------------------------------
// Final displacement for a vertex, in OBJECT space.
// IslandId comes from the z component of the index UV.
// --------------------------------------------------------------------
void CalculateDentVector_float(float3 WorldPos, float3 WorldNormal, float IslandId,
                               out float3 Displacement, out float DecayMul)
{
    float3 perVertex = TransformWorldToObjectDir(
        EvaluateDentWorld(WorldPos, WorldNormal, DecayMul), false);

    int id = clamp((int)round(IslandId), 0, ISLAND_MAX - 1);
    float4 island = (id < _IslandCount) ? _IslandPush[id] : float4(0, 0, 0, 0);

    // island.xyz is already object space; island.w is this island's rigidity.
    Displacement = lerp(perVertex, island.xyz, saturate(island.w));
}

#endif // DENTSTAMP_INCLUDED
