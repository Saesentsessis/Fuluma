#ifndef FULUMA_SPOT_LIGHT_SCATTERING_INCLUDED
#define FULUMA_SPOT_LIGHT_SCATTERING_INCLUDED

#include "../ShaderLibrary/Intersections.hlsl"

// In-scattering of a fake spot light along a view ray: the integral of distanceFalloff(d) * angularFalloff along the part
// of the ray inside the light's volume, clipped at the scene depth.
//
// The ray is split at the inner cone into three segments (outer entry -> inner entry -> inner exit -> outer exit; a
// penumbra-only ray is one segment), each integrated with a 3-point Gauss-Legendre rule in the airlight angle (see below).
//
// Distance falloff: inverse square with a small source size e, times a range window, normalized so it matches a smooth
// falloff at a quarter-range offset (hotter core):
//   f(d) = (1 - d^2/r^2)^2 / (d^2 + e^2) * r / (4 pi), e = FULUMA_SOURCE_RADIUS * r
// _FULUMA_FALLOFF_UNITY: URP's own attenuation instead, in world units, without the range scale and without isotropic
// scattering's 1 / (4 pi), so intensity means what a Unity Light's does:
//   f(d) = (1 - (d^2/r^2)^2)^2 / (d^2 + e^2)
// _FULUMA_POINT_LAMP: a point lamp, e = 0 (h is only kept above FULUMA_POINT_LAMP_MIN_H * r so 1 / h stays finite): the glow
// of rays passing the lamp grows without bound.
// Angular falloff: saturate((cos - cosOuter) * angleScale)^2. The inner cone comes from the packed data:
// angleScale = 1 / (cosInner - cosOuter), so cosInner = cosOuter + 1 / angleScale.
// Phase function: 1 / (4 pi) above is isotropic scattering. _FULUMA_PHASE_HG multiplies each sample by Henyey-Greenstein's
// phase relative to isotropic, FulumaSpotPhase. Its angle needs no extra state: light leaves the lamp toward the sample
// (direction p / d) and scatters back along the ray, so cos = dot(p / d, -ray) = -s / d.
//
// Removed alternatives (other sampling schemes, a reference raymarch, smooth-window and linear falloffs) are archived in
// Assets/Archive/SpotLightSampling, with what was learned from each.

// Source size as a fraction of the range: keeps 1 / d^2 finite at the apex.
#define FULUMA_SOURCE_RADIUS 0.02

// _FULUMA_POINT_LAMP: the closest a ray's line is taken to pass by the lamp, as a fraction of the range.
#define FULUMA_POINT_LAMP_MIN_H 1.0e-4

#if defined(_FULUMA_PHASE_HG)
// Henyey-Greenstein relative to isotropic scattering (4 pi p), from the cosine of the scattering angle: with _FulumaPhase =
// (1 - g^2, 1 + g^2, 2g), a / (b - m c)^1.5 (FulumaSpotLightRenderPass.PhaseConstants).
float FulumaSpotPhase(float cosScatter)
{
    float r = rsqrt(_FulumaPhase.y - _FulumaPhase.z * cosScatter);
    return _FulumaPhase.x * r * r * r;
}
#endif

// ---------------------------------------------------------------------------------------------------------------------
// Ray relative to the light

// Everything the samples need is scalar: a point at s = t - tClosest along the ray is the closest point plus s times
// the (unit) ray direction, which is perpendicular to it, so
//   distance^2 = h^2 + s^2,   height along the axis = axialClosest + s * axialSlope.
struct FulumaSpotRay
{
    float tClosest;         // t of the ray point closest to the light origin
    float h2;               // squared distance of the ray line from the light origin
    float axialClosest;     // height of that closest point along the light axis
    float axialSlope;       // dot(ray direction, light axis): height gained per unit of t
};

// rayToLight: ray origin minus light origin; coneRay: the same ray's dot products (FulumaMakeConeRayRelative).
FulumaSpotRay FulumaMakeSpotRay(float3 rayToLight, float3 rayDirection, FulumaConeRay coneRay)
{
    FulumaSpotRay ray;
    ray.tClosest = -coneRay.rayDirectionRayToCone;

    // From the closest point itself rather than |o|^2 - tc^2: no cancellation of two large squares when the camera is
    // far from the light.
    float3 closest = rayToLight + rayDirection * ray.tClosest;
    ray.h2 = dot(closest, closest);

    ray.axialClosest = coneRay.rayToConeAxialDot + ray.tClosest * coneRay.directionDot;
    ray.axialSlope = coneRay.directionDot;
    return ray;
}

// ---------------------------------------------------------------------------------------------------------------------
// Gauss-Legendre quadrature of the full model (distance falloff x angular falloff) on each
// segment, in the airlight angle phi rather than in t.
//
// With t - tc = H tan(phi), H^2 = h^2 + e^2, the measure becomes dt = D / H dphi (D = s^2 + H^2), so
//   integral f(d) angular dt = (1 / H) integral f(d) D angular dphi.
// For the inverse-square falloff f D is exactly the range window (smooth, bounded).
// The segments already end at every kink of the integrand (inner and outer cone, cap), so a 3-point rule per segment is
// close to exact. Measured in float32 against brute force (Python, 145 rays incl. side, along-the-beam, inside and
// behind-apex views): worst 2.5-7% per ray for 60/30, 30/20 and 8/4 degree cones; in render tests within ~1-2% per pixel.
// No large terms are subtracted, so narrow cones are as accurate as wide ones.
//
// Why not a closed form: the exact antiderivatives exist (all terms are s^n D^(m/2)), and in float64 they matched brute
// force to 1e-4. In float32 the expansion of (cos - cosOuter)^2 into three large, nearly cancelling terms broke down:
// up to 64% error at the edges of a 60 degree cone and over 1000x for narrow cones (k^2 up to 3e5).

//
// Per sample the integrand is f(d) D angular. For this falloff D = s^2 + h^2 + e^2 = d^2 + e^2 cancels f's denominator,
// leaving window(d)^2 * angular * r / (4 pi): no division per sample.
//
// The nodes are placed without per-node trigonometry. With u = tan(phi) = (t - tc) / H at the segment ends (no trig):
//   half width   = atan2(u1 - u0, 1 + u0 u1) / 2           (tangent of a difference; correct up to pi)
//   center node  = tan((phi0 + phi1) / 2) = (sin0 + sin1) / (cos0 + cos1), cos = 1 / sqrt(1 + u^2), sin = u cos
//   outer nodes  = tan(center +- delta) = (tc +- td) / (1 -+ tc td), td = tan(sqrt(3/5) * half width)
// So one atan2 and one tan per segment instead of two atan and three tan. Same nodes, same result.
//
// A 1-point rule (one sample per segment) was tried as a cheap shader: ~18% faster, but up to 90% off with seams along
// the silhouettes. Archived in Assets/Archive/SpotLightSampling.

// A trig-free node placement (Gauss-Legendre in u with psi = 2 atan(q u), q = tan(h/2)) was A/B tested on 2026-11-18:
// ~2% faster on the RTX 3060, slightly less accurate, because its roots and divisions load the same slow XU pipe as the
// trig it removes. Archived in Assets/Archive/SpotLightSampling with the numbers.

// Per-light constants of the samples.
struct FulumaSpotSampleConstants
{
    float h;                // padded distance of the ray line from the light origin: sqrt(h^2 + e^2)
    float inverseRange2;
    float angleScale;
    float falloffBias;      // cosOuter * angleScale: the angular falloff is saturate(cos * angleScale - falloffBias)
};

// s: the sample's offset from the closest point along the ray. Scalar throughout (see FulumaSpotRay).
float FulumaSpotSampleValue(FulumaSpotRay ray, float s, FulumaSpotSampleConstants constants, bool insideInnerCone)
{
    float d2 = ray.h2 + s * s;

#if defined(_FULUMA_FALLOFF_UNITY)
    float range2 = d2 * constants.inverseRange2;
    float window = saturate(1.0 - range2 * range2);
#else
    float window = saturate(1.0 - d2 * constants.inverseRange2);
#endif
    float value = window * window;

    // insideInnerCone is a literal at every call: without a phase function the inner samples never compute it.
    float inverseD = rsqrt(max(d2, 1.0e-12));

    if (!insideInnerCone)
    {
        float cosAngle = (ray.axialClosest + s * ray.axialSlope) * inverseD;
        float falloff = saturate(cosAngle * constants.angleScale - constants.falloffBias);
        value *= falloff * falloff;
    }

#if defined(_FULUMA_PHASE_HG)
    value *= FulumaSpotPhase(-s * inverseD);
#endif

    return value;
}

// A segment end in the airlight angle: u = tan(phi) = (t - tClosest) / H, and cos(phi) = 1 / sqrt(1 + u^2). Ends shared
// by two segments are computed once.
struct FulumaSpotSegmentEnd
{
    float u;
    float cosPhi;
};

FulumaSpotSegmentEnd FulumaMakeSpotSegmentEnd(FulumaSpotRay ray, float t, float inverseH)
{
    FulumaSpotSegmentEnd end;
    end.u = (t - ray.tClosest) * inverseH;
    end.cosPhi = rsqrt(1.0 + end.u * end.u);
    return end;
}

// The segment's integral times H (the caller applies 1 / H once for all segments).
float FulumaSpotQuadratureSegment(FulumaSpotRay ray, FulumaSpotSegmentEnd start, FulumaSpotSegmentEnd end,
    FulumaSpotSampleConstants constants, bool insideInnerCone)
{
    // One exit point (no early return): keeps FXC from flagging "potentially uninitialized" variables after unrolling.
    float result = 0.0;

    // Empty when the scene depth cut it away (both ends clipped to the same t).
    UNITY_BRANCH
    if (end.u > start.u)
    {
        float u0 = start.u;
        float u1 = end.u;

        // Gauss-Legendre in phi: nodes at the center and +-sqrt(3/5) of the half width.
        float halfWidth = 0.5 * atan2(u1 - u0, 1.0 + u0 * u1);
        float tanCenter = (u0 * start.cosPhi + u1 * end.cosPhi) / (start.cosPhi + end.cosPhi);
        float tanDelta = tan(0.7745966692 * halfWidth); // sqrt(3/5)
        const float3 weights = float3(5.0 / 9.0, 8.0 / 9.0, 5.0 / 9.0);

        // Sample offsets s = H tan(phi) of the three nodes.
        float3 offsets = constants.h * float3(
            (tanCenter - tanDelta) / (1.0 + tanCenter * tanDelta),
            tanCenter,
            (tanCenter + tanDelta) / (1.0 - tanCenter * tanDelta));

        float sum = 0.0;

        UNITY_UNROLL
        for (int i = 0; i < 3; i++)
            sum += weights[i] * FulumaSpotSampleValue(ray, offsets[i], constants, insideInnerCone);

        result = sum * halfWidth;
    }

    return result;
}

// Per-light and per-ray constants, shared by every segment and sample. inverseH: 1 / sqrt(h^2 + e^2).
FulumaSpotSampleConstants FulumaMakeSpotSampleConstants(FulumaSpotRay ray, FulumaSpotLight light, out float inverseH)
{
#if defined(_FULUMA_POINT_LAMP)
    float minimumH = FULUMA_POINT_LAMP_MIN_H * light.range;
    float paddedH2 = max(ray.h2, minimumH * minimumH);
#else
    float epsilon = FULUMA_SOURCE_RADIUS * light.range;
    float paddedH2 = ray.h2 + epsilon * epsilon;
#endif
    inverseH = rsqrt(paddedH2);

    FulumaSpotSampleConstants constants;
    constants.h = paddedH2 * inverseH;
    constants.inverseRange2 = rcp(light.range * light.range);
    constants.angleScale = light.angleScale;
    constants.falloffBias = light.cosOuter * light.angleScale;
    return constants;
}

// The quadrature's sum times this is the glow: 1 / H from the change of variable, then the falloff's scale: range / (4 pi)
// (isotropic scattering's 1 / (4 pi); a phase function's shape is in the samples), or 1 for Unity's attenuation.
float FulumaSpotScale(FulumaSpotLight light, float inverseH)
{
#if defined(_FULUMA_FALLOFF_UNITY)
    return inverseH;
#else
    return inverseH * light.range * (0.25 / PI);
#endif
}

float FulumaSpotQuadrature(FulumaSpotRay ray, float tScene, FulumaSpotLight light,
    float outerNear, float outerFar, float innerNear, float innerFar, bool innerHit)
{
    float inverseH;
    FulumaSpotSampleConstants constants = FulumaMakeSpotSampleConstants(ray, light, inverseH);

    // Segment ends clipped at the scene depth (callers guarantee tScene > outerNear).
    FulumaSpotSegmentEnd first = FulumaMakeSpotSegmentEnd(ray, outerNear, inverseH);
    FulumaSpotSegmentEnd last = FulumaMakeSpotSegmentEnd(ray, min(outerFar, tScene), inverseH);

    float value;

    // A penumbra-only ray is one segment: its angular falloff peaks smoothly inside, no kink to split at.
    UNITY_BRANCH
    if (!innerHit)
    {
        value = FulumaSpotQuadratureSegment(ray, first, last, constants, false);
    }
    else
    {
        FulumaSpotSegmentEnd innerFirst = FulumaMakeSpotSegmentEnd(ray, min(innerNear, tScene), inverseH);
        FulumaSpotSegmentEnd innerLast = FulumaMakeSpotSegmentEnd(ray, min(innerFar, tScene), inverseH);

        value = FulumaSpotQuadratureSegment(ray, first, innerFirst, constants, false)
              + FulumaSpotQuadratureSegment(ray, innerFirst, innerLast, constants, true)
              + FulumaSpotQuadratureSegment(ray, innerLast, last, constants, false);
    }

    return value * FulumaSpotScale(light, inverseH);
}

// ---------------------------------------------------------------------------------------------------------------------

// Whole-ray glow of one light. rayToLight: ray origin minus light origin; coneRay: the ray's dot products against the
// light (FulumaMakeConeRayRelative); tScene: distance along the ray to the first opaque surface.
float FulumaSpotLightScattering(float3 rayToLight, float3 rayDirection, FulumaConeRay coneRay, float tScene,
    FulumaSpotLight light, float outerNear, float outerFar, float innerNear, float innerFar, bool innerHit)
{
    FulumaSpotRay ray = FulumaMakeSpotRay(rayToLight, rayDirection, coneRay);
    return FulumaSpotQuadrature(ray, tScene, light, outerNear, outerFar, innerNear, innerFar, innerHit);
}

// One piece [near, far] of the ray inside the light's region (clip boxes), split where it crosses the inner cone's
// [innerNear, innerFar] (clamped to the piece). The caller scales the sum like FulumaSpotQuadrature does.
float FulumaSpotQuadraturePiece(FulumaSpotRay ray, FulumaSpotSampleConstants constants, float inverseH,
    float near, float far, float innerNear, float innerFar, bool innerHit)
{
    FulumaSpotSegmentEnd first = FulumaMakeSpotSegmentEnd(ray, near, inverseH);
    FulumaSpotSegmentEnd last = FulumaMakeSpotSegmentEnd(ray, far, inverseH);

    float value;

    UNITY_BRANCH
    if (!innerHit)
    {
        value = FulumaSpotQuadratureSegment(ray, first, last, constants, false);
    }
    else
    {
        FulumaSpotSegmentEnd innerFirst = FulumaMakeSpotSegmentEnd(ray, clamp(innerNear, near, far), inverseH);
        FulumaSpotSegmentEnd innerLast = FulumaMakeSpotSegmentEnd(ray, clamp(innerFar, near, far), inverseH);

        value = FulumaSpotQuadratureSegment(ray, first, innerFirst, constants, false)
              + FulumaSpotQuadratureSegment(ray, innerFirst, innerLast, constants, true)
              + FulumaSpotQuadratureSegment(ray, innerLast, last, constants, false);
    }

    return value;
}

#if defined(FULUMA_CLIP_BOXES_INCLUDED)
// Glow of one light along a ray limited to its clip boxes: walks the ray's [near, far] breakpoint by breakpoint and
// integrates each piece of the region as it closes. At most pieceLimit pieces: once pieceLimit - 1 are closed, the last
// one stays open to the last inside point, merging the remaining pieces (their gaps glow).
float FulumaSpotLightScatteringClipped(float3 rayToLight, float3 rayDirection, FulumaConeRay coneRay,
    FulumaSpotLight light, FulumaClipIntervals clip, float near, float far, float innerNear, float innerFar,
    bool innerHit, uint pieceLimit)
{
    FulumaSpotRay ray = FulumaMakeSpotRay(rayToLight, rayDirection, coneRay);

    float inverseH;
    FulumaSpotSampleConstants constants = FulumaMakeSpotSampleConstants(ray, light, inverseH);

    float value = 0.0;
    float t = near;
    bool open = false;
    float pieceStart = near;
    float lastInside = near;
    uint closed = 0;

    // At most 9 steps: the 4 slots' starts and ends, and far. One copy of the quadrature: a piece is integrated in the step that
    // closes it (it turns outside, or the ray ends).
    UNITY_LOOP
    for (uint step = 0; step < 2 * FULUMA_CLIP_BOX_SLOTS + 1 && t < far; step++)
    {
        float next = FulumaClipNextBreakpoint(clip, t, far);
        bool inside = FulumaClipInside(clip, 0.5 * (t + next));

        if (inside)
        {
            pieceStart = open ? pieceStart : t;
            open = true;
            lastInside = next;
        }

        // Outside with room for more pieces: close (lastInside is t then). Past the limit the piece stays open over the
        // gap, merging the rest; the end of the ray closes it.
        bool final = next >= far;
        if (open && ((inside == false && closed + 1 < pieceLimit) || final))
        {
            value += FulumaSpotQuadraturePiece(ray, constants, inverseH, pieceStart, lastInside, innerNear, innerFar, innerHit);
            closed++;
            open = false;
        }

        t = next;
    }

    return value * FulumaSpotScale(light, inverseH);
}

// Glow of one light along at most two pieces of the ray (the one- and two-box variants; an empty piece has
// end <= start). One copy of the quadrature, run once per non-empty piece.
float FulumaSpotLightScatteringTwoPieces(float3 rayToLight, float3 rayDirection, FulumaConeRay coneRay,
    FulumaSpotLight light, float2 firstPiece, float2 secondPiece, float innerNear, float innerFar, bool innerHit)
{
    FulumaSpotRay ray = FulumaMakeSpotRay(rayToLight, rayDirection, coneRay);

    float inverseH;
    FulumaSpotSampleConstants constants = FulumaMakeSpotSampleConstants(ray, light, inverseH);

    float value = 0.0;
    float2 piece = firstPiece;

    UNITY_LOOP
    for (uint i = 0; i < 2; i++)
    {
        if (piece.y > piece.x)
            value += FulumaSpotQuadraturePiece(ray, constants, inverseH, piece.x, piece.y, innerNear, innerFar, innerHit);

        piece = secondPiece;
    }

    return value * FulumaSpotScale(light, inverseH);
}

// Glow of one light along at most three pieces of the ray (the two-subtract variant). One copy of the quadrature, run
// once per non-empty piece.
float FulumaSpotLightScatteringThreePieces(float3 rayToLight, float3 rayDirection, FulumaConeRay coneRay,
    FulumaSpotLight light, float2 firstPiece, float2 secondPiece, float2 thirdPiece, float innerNear, float innerFar,
    bool innerHit)
{
    FulumaSpotRay ray = FulumaMakeSpotRay(rayToLight, rayDirection, coneRay);

    float inverseH;
    FulumaSpotSampleConstants constants = FulumaMakeSpotSampleConstants(ray, light, inverseH);

    float value = 0.0;
    float2 piece = firstPiece;

    UNITY_LOOP
    for (uint i = 0; i < 3; i++)
    {
        if (piece.y > piece.x)
            value += FulumaSpotQuadraturePiece(ray, constants, inverseH, piece.x, piece.y, innerNear, innerFar, innerHit);

        piece = i == 0 ? secondPiece : thirdPiece;
    }

    return value * FulumaSpotScale(light, inverseH);
}
#endif

// cosInner from the packed data (see top of file). Clamped below 1: an inner angle of 0 has no inner cone.
float FulumaSpotCosInner(FulumaSpotLight light)
{
    return min(light.cosOuter + 1.0 / max(light.angleScale, 1.0e-4), 0.999999);
}

#endif // FULUMA_SPOT_LIGHT_SCATTERING_INCLUDED
