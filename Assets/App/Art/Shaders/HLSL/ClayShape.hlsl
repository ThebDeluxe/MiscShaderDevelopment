#ifndef CLAYSHAPE_INCLUDED
#define CLAYSHAPE_INCLUDED

// Whole-body shape change as a SPATIAL FIELD rather than per-vertex targets.
//
//     newPos = f(oldPos)
//
// Every vertex passing through the same point in space moves the same way, so separate
// islands - eyes, a hat, arms - are carried along by the deformation and keep their
// arrangement, instead of each vertex teleporting to an authored position the way a blend
// shape or a baked delta map does. It also makes the deformation's FRAME a parameter, so
// one field produces its shape along any axis.
//
// HOW A SHAPE IS DEFINED
// Every shape here is one SUPERELLIPSOID, the family running continuously from sphere
// through capsule and cylinder to box. Rather than measuring where its surface lies and
// pushing vertices onto it, the shape is built PARAMETRICALLY from each vertex's own
// angles:
//
//     x = a * cos(lat)^e1 * cos(lon)^e2
//     y = b * cos(lat)^e1 * sin(lon)^e2
//     z = c * sin(lat)^e1
//
//   a, b, c : half extents across, across, and along the axis
//   e1      : profile exponent. 1 rounds the ends, near 0 flattens them into faces
//   e2      : cross-section exponent. 1 is a circle, near 0 is a square
//
// That distinction matters. Pushing vertices along their own direction seems natural but
// bunches badly on flat shapes: on a pancake every vertex of the sphere's top cap points
// roughly upward, and the surface that way is only the half-height, so the whole cap is
// squeezed into a small central disc while only the equator reaches the rim. Mapping
// ANGLES keeps vertices spread as they were.
//
// A taper on top scales the cross-section along the axis, which turns a cylinder into a
// cone and a box into a pyramid.

// Neither placement is right for every shape, so they are blended per shape - see Spread.
//
//   Warp      : scale each vertex's own POSITION by the extents, squared off by a factor
//               that depends only on its direction. Every point along a ray moves by the
//               same amount, so anything off the body keeps its position relative to what
//               is beneath it - a hat stays on the head. Faces stay evenly covered on
//               square shapes, but on a very flat shape the vertices that were near the
//               poles end up crowded.
//   Parametric: place vertices by latitude and longitude instead. Flat shapes stay evenly
//               spread, but vertices are RE-PLACED rather than moved, so detail loses its
//               relative position - and on square shapes they bunch at the corners.

// Signed power, since the parametrisation needs to keep the sign of each angle term.
float ClaySignedPow(float v, float e)
{
    return sign(v) * pow(max(abs(v), 1e-6), e);
}

// Roundness in 0..1 to a superellipsoid exponent. 1 is fully round, low is square.
float ClayRoundnessToExponent(float roundness)
{
    return lerp(0.15, 1.0, saturate(roundness));
}

// Where the surface sits along a direction in NORMALISED space, for the radial placement.
//
// Normalised meaning the extents have already been divided out, so the target is the unit
// superellipsoid. That matters: projecting along a vertex's raw direction collapses
// anything pointing down a short axis onto that short surface, so a plank pinches toward
// its centre plane. A cube hides it only because its extents are equal.
float ClaySurfaceDistance(float3 dir, float crossExp, float profileExp)
{
    // Implicit form of the same superellipsoid, with the exponents inverted: the
    // parametric e maps to an implicit 2/e.
    float m = 2.0 / max(crossExp, 1e-3);
    float n = 2.0 / max(profileExp, 1e-3);

    float3 d = abs(dir);

    float across = pow(max(d.x, 1e-6), m) + pow(max(d.y, 1e-6), m);
    across = pow(across, n / m);

    float total = across + pow(max(d.z, 1e-6), n);

    return 1.0 / max(pow(total, 1.0 / n), 1e-5);
}

/// <summary>
/// PositionOS   vertex, object space
/// AxisOS       shape's long axis, object space, captured when the shape changed
/// PivotOS      point the shape is built around
/// Size         half extents: x and y across the axis, z along it, relative to BaseRadius
/// Params       x = cross roundness, y = profile roundness, z = taper, w = base radius
/// Spread       0 warps positions, keeping detail where it sat. 1 places vertices by angle,
///              which spreads flat shapes evenly but moves detail off its body.
/// Amount       0 = untouched, 1 = fully the target shape
/// </summary>
void ApplyClayShape_float(float3 PositionOS, float3 AxisOS, float3 PivotOS,
                          float3 Size, float4 Params, float Spread, float Amount,
                          out float3 Out)
{
    float blend = saturate(Amount);

    float axisLen = length(AxisOS);
    if (blend < 1e-4 || axisLen < 1e-5)
    {
        Out = PositionOS;
        return;
    }

    float3 w = AxisOS / axisLen;

    // Any stable pair perpendicular to the axis. The shape is symmetric across them, so
    // which pair does not matter - only that it does not collapse when the axis is up.
    float3 helper = abs(w.y) > 0.99 ? float3(1, 0, 0) : float3(0, 1, 0);
    float3 u = normalize(cross(helper, w));
    float3 v = cross(w, u);

    float3 p = PositionOS - PivotOS;

    // Into the shape's own frame.
    float3 local = float3(dot(p, u), dot(p, v), dot(p, w));

    float radius = length(local);
    if (radius < 1e-5)
    {
        Out = PositionOS;
        return;
    }

    float3 dir = local / radius;

    float baseRadius = max(Params.w, 1e-4);
    float crossExp = ClayRoundnessToExponent(Params.x);
    float profileExp = ClayRoundnessToExponent(Params.y);

    // The vertex's own angles: latitude from the axis, longitude around it. Working from
    // these rather than from the raw direction is what keeps vertices spread out.
    float sinLat = clamp(dir.z, -1.0, 1.0);
    float cosLat = sqrt(max(1.0 - sinLat * sinLat, 0.0));

    float2 lonDir = cosLat > 1e-5 ? dir.xy / cosLat : float2(1, 0);

    float3 extents = Size * baseRadius;

    // --- placement one: WARP ---
    // Scale the vertex's own position by the extents, then square it off by a factor that
    // depends only on its direction. Because every point along a ray is scaled by the same
    // amount, anything sitting off the body - a hat, an ear - keeps its position relative
    // to what is beneath it.
    //
    // This is the difference between warping the shape and projecting onto it. Projection
    // sends a vertex to wherever the surface lies in its direction, so a hat pointing at a
    // plank's flat face lands in the middle of that face, having lost where it was. The
    // squaring factor is 1 at round exponents, leaving a plain anisotropic scale.
    float3 unit = local / baseRadius;
    float3 warped = unit * extents * ClaySurfaceDistance(dir, crossExp, profileExp);

    // --- placement two: PARAMETRIC ---
    // Placed by latitude and longitude instead, which keeps vertices evenly spread on flat
    // shapes but re-places them, so relative arrangement is not preserved.
    float depth = radius / baseRadius;
    float latTerm = ClaySignedPow(cosLat, profileExp);

    float3 parametric;
    parametric.x = extents.x * latTerm * ClaySignedPow(lonDir.x, crossExp);
    parametric.y = extents.y * latTerm * ClaySignedPow(lonDir.y, crossExp);
    parametric.z = extents.z * ClaySignedPow(sinLat, profileExp);

    parametric *= depth;

    float3 shaped = lerp(warped, parametric, saturate(Spread));

    // Taper narrows the cross-section toward the far end of the axis, which turns a
    // cylinder into a cone and a box into a pyramid.
    float taper = saturate(Params.z);
    if (taper > 1e-4)
    {
        float halfHeight = max(Size.z * baseRadius, 1e-4);
        float alongAxis = saturate((shaped.z + halfHeight) / (2.0 * halfHeight));

        shaped.xy *= lerp(1.0, max(1.0 - taper, 0.0), alongAxis);
    }

    // Back out of the shape's frame.
    float3 result = u * shaped.x + v * shaped.y + w * shaped.z;

    Out = lerp(p, result, blend) + PivotOS;
}

#endif // CLAYSHAPE_INCLUDED
