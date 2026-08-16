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
// Every shape here is one SUPERQUADRIC, which is the family that runs continuously from
// sphere through capsule and cylinder to box:
//
//     (|u/a|^m + |v/b|^m)^(n/m) + |w/c|^n = 1
//
//   a, b, c : half extents across, across, and along the axis
//   m       : cross-section exponent. 2 is a circle, high is a square
//   n       : profile exponent. 2 rounds the ends, high flattens them into faces
//
// Because it is implicit, the distance from the centre to the surface along any direction
// is closed form - so a vertex can simply be moved onto the surface that matches its own
// direction, keeping its depth. That single expression gives every shape below, with real
// rounded edges rather than a scaled sphere pretending to be one.
//
// A taper on top scales the cross-section along the axis, which turns a cylinder into a
// cone and a box into a pyramid.

// Exponent for a roundness in 0..1. 1 is fully round, 0 is as square as is stable.
float ClayRoundnessToExponent(float roundness)
{
    return lerp(8.0, 2.0, saturate(roundness));
}

/// Distance from the centre to the superquadric surface, along a unit direction.
float ClaySurfaceDistance(float3 dir, float3 halfExtents, float crossExp, float profileExp)
{
    // dir.xy across the axis, dir.z along it.
    float3 d = abs(dir / max(halfExtents, 1e-4));

    float cross = pow(max(d.x, 1e-6), crossExp) + pow(max(d.y, 1e-6), crossExp);
    cross = pow(cross, profileExp / crossExp);

    float total = cross + pow(max(d.z, 1e-6), profileExp);

    return 1.0 / max(pow(total, 1.0 / profileExp), 1e-5);
}

/// <summary>
/// PositionOS   vertex, object space
/// AxisOS       shape's long axis, object space, captured when the shape changed
/// PivotOS      point the shape is built around
/// Size         half extents: x and y across the axis, z along it, relative to BaseRadius
/// Params       x = cross roundness, y = profile roundness, z = taper, w = base radius
/// Amount       0 = untouched, 1 = fully the target shape
/// </summary>
void ApplyClayShape_float(float3 PositionOS, float3 AxisOS, float3 PivotOS,
                          float3 Size, float4 Params, float Amount,
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

    // Where the target surface sits in this direction, and how deep this vertex is as a
    // fraction of the base - so interior vertices stay interior instead of collapsing onto
    // the shell.
    float surface = ClaySurfaceDistance(dir, Size * baseRadius, crossExp, profileExp);
    float depth = radius / baseRadius;

    float3 shaped = dir * (surface * depth);

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
