#ifndef SQUASHSTRETCH_INCLUDED
#define SQUASHSTRETCH_INCLUDED

// Volume-preserving squash and stretch along an arbitrary axis.
//
// Deliberately NOT part of the dent system. Dents are persistent per-vertex state stored
// in a texture, accumulated and decayed. This is a uniform whole-object transform derived
// from current velocity - no history, no accumulation. Pushing it through the dent map
// would bake a body-wide transform into per-vertex storage every frame, fight the dents
// through their "strongest wins" rule, and decay something that should not decay.
//
// Scaling by s along the axis and 1/sqrt(s) across it keeps volume constant:
//     s * (1/sqrt(s))^2 = 1
// which is what stops the character visibly gaining or losing mass as it moves.
//
// Amount is signed: positive stretches along the axis (moving), negative squashes along
// it and bulges outward (landing, or a sudden stop once the spring overshoots).

void ApplySquashStretch_float(float3 PositionOS, float3 AxisOS, float Amount, float3 PivotOS,
                              out float3 Out)
{
    float3 axis = AxisOS;
    float axisLen = length(axis);

    // No meaningful direction means no deformation, rather than a divide by zero.
    if (axisLen < 1e-5)
    {
        Out = PositionOS;
        return;
    }

    axis /= axisLen;

    // Floor the scale so a large negative amount cannot invert the mesh.
    float along = max(1.0 + Amount, 0.05);
    float across = rsqrt(along);

    float3 p = PositionOS - PivotOS;

    float  d      = dot(p, axis);
    float3 alongV = axis * d;
    float3 crossV = p - alongV;

    Out = PivotOS + alongV * along + crossV * across;
}

#endif // SQUASHSTRETCH_INCLUDED
