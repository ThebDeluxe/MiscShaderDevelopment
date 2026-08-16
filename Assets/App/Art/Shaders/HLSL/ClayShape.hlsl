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
//   Radial    : keep each vertex's own direction and move it onto the surface. Faces stay
//               evenly covered on square shapes, but on a flat shape the whole top cap
//               points roughly along the axis, where the surface is only the half-height,
//               so it collapses into a small central disc.
//   Parametric: place vertices by latitude and longitude instead. Flat shapes stay spread,
//               but on square shapes vertices bunch at the corners - even angle spacing is
//               not even surface spacing - and the faces smear.

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

// Where the surface sits along a direction, for the radial placement.
float ClaySurfaceDistance(float3 dir, float3 halfExtents, float crossExp, float profileExp)
{
    // Implicit form of the same superellipsoid, with the exponents inverted: the
    // parametric e maps to an implicit 2/e.
    float m = 2.0 / max(crossExp, 1e-3);
    float n = 2.0 / max(profileExp, 1e-3);

    float3 d = abs(dir / max(halfExtents, 1e-4));

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
/// Spread       0 keeps each vertex's own direction, 1 places them by angle. Low suits
///              square shapes, high suits flat ones - see the note above.
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

    // How deep the vertex sits, as a fraction of the base radius, so interior vertices
    // stay interior instead of collapsing onto the shell.
    float depth = radius / baseRadius;

    float latTerm = ClaySignedPow(cosLat, profileExp);

    float3 parametric;
    parametric.x = extents.x * latTerm * ClaySignedPow(lonDir.x, crossExp);
    parametric.y = extents.y * latTerm * ClaySignedPow(lonDir.y, crossExp);
    parametric.z = extents.z * ClaySignedPow(sinLat, profileExp);

    // The other placement: straight out along the vertex's own direction.
    float3 radial = dir * ClaySurfaceDistance(dir, extents, crossExp, profileExp);

    float3 shaped = lerp(radial, parametric, saturate(Spread)) * depth;

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
