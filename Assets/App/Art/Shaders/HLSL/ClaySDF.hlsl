#ifndef CLAYSDF_INCLUDED
#define CLAYSDF_INCLUDED

// Signed distance fields for dent contacts.
//
// WHY THIS RATHER THAN A CONTACT PER FACE
// A box touched on an edge currently produces two independent plane presses that meet at a
// seam, and rolling across it shows the seam moving. An SDF describes the whole solid at
// once: distance is negative inside, zero on the surface, positive outside, and the surface
// NORMAL is simply the gradient - so an edge is one continuous surface with a correct
// normal, rather than two flat answers disagreeing about which one applies.
//
// Several solids combine with min(), which is exact. Replacing that with a SMOOTH minimum
// blends them over a small radius instead, which is what makes overlapping contacts read as
// one piece of clay being pressed rather than several stamps competing.

/// Signed distance to a box, in the box's own space. Negative inside.
float ClaySdBox(float3 p, float3 halfExtents)
{
    float3 q = abs(p) - halfExtents;
    return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
}

/// Rounds a solid's edges by pushing its surface outward - the cheapest way to soften a
/// hard corner, since it costs one subtraction.
float ClaySdRound(float d, float radius)
{
    return d - radius;
}

/// <summary>
/// Smooth minimum. k is the blend radius: at 0 this is a plain min and solids meet at a
/// crease, and larger values fuse them over that distance.
///
/// The polynomial form, which is cheap and has no discontinuity in its derivative - that
/// matters here because the gradient IS the surface normal, so a kink in it would show up
/// as a visible crease in the deformation.
/// </summary>
float ClaySmoothMin(float a, float b, float k)
{
    if (k <= 1e-5) return min(a, b);

    float h = saturate(0.5 + 0.5 * (b - a) / k);
    return lerp(b, a, h) - k * h * (1.0 - h);
}

#endif // CLAYSDF_INCLUDED
