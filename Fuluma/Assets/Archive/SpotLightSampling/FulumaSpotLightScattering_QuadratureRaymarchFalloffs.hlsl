#ifndef FULUMA_SPOT_LIGHT_SCATTERING_INCLUDED
#define FULUMA_SPOT_LIGHT_SCATTERING_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"
#include "../ShaderLibrary/Intersections.hlsl"

// In-scattering of a fake spot light along a view ray: the integral of distanceFalloff(d) * angularFalloff along the part
// of the ray inside the light's volume, clipped at the scene depth.
//
//   _FULUMA_SAMPLING_QUADRATURE  the fake light: the ray is split at the inner cone into three segments
//                                (outer entry -> inner entry -> inner exit -> outer exit), each integrated with a 3-point
//                                Gauss-Legendre rule in the airlight angle (see below). Fixed cost.
//   _FULUMA_SAMPLING_RAYMARCH    the reference: plain raymarch of the same integrand with a fixed world-space step
//                                (_FulumaRaymarchStepSize), jittered per pixel. Cost grows with the path length.
//
// Other sampling modes (point sample, integrated distance, shell sum) are archived in Assets/Archive/SpotLightSampling.
//
// The inner cone comes from the packed data: angleScale = 1 / (cosInner - cosOuter), so cosInner = cosOuter + 1 / angleScale.
//
// Distance falloffs (_FULUMA_FALLOFF_*), normalized so a ray straight through the light origin across the whole sphere
// gives about 1 (smooth window, linear); inverse square has a hotter core and matches the others at a quarter-range offset:
//   SMOOTH_WINDOW  f(d) = (1 - d^2/r^2)^2       * 15 / (16 r)
//   INVERSE_SQUARE f(d) = 1 / (d^2 + e^2)        * r / (4 pi) * window(d), e = 2% of the range
//   LINEAR         f(d) = (1 - d / r)            * 1 / r

// Upper bound on raymarch steps per light and pixel; the step grows past _FulumaRaymarchStepSize to respect it.
#define FULUMA_RAYMARCH_MAX_STEPS 1024

// ---------------------------------------------------------------------------------------------------------------------
// Falloffs at one point

// Distance falloff, d2 = squared distance to the light origin.
float FulumaFalloffPoint(float d2, float range)
{
    float inverseRange2 = 1.0 / (range * range);
    float window = saturate(1.0 - d2 * inverseRange2);

#if defined(_FULUMA_FALLOFF_INVERSE_SQUARE)
    float epsilon = 0.02 * range;
    return window * window / (d2 + epsilon * epsilon) * range * (0.25 / PI);
#elif defined(_FULUMA_FALLOFF_LINEAR)
    return saturate(1.0 - sqrt(d2) / range) / range;
#else // _FULUMA_FALLOFF_SMOOTH_WINDOW
    return window * window * (15.0 / 16.0) / range;
#endif
}

// Inner/outer cone falloff at a point (given relative to the light origin): 0 at the outer edge and outside the cone
// (including behind the apex), 1 inside the inner cone.
float FulumaSpotAngularFalloff(float3 toPoint, float3 lightDirection, float cosOuter, float angleScale)
{
    float cosAngle = dot(toPoint, lightDirection) * rsqrt(max(dot(toPoint, toPoint), 1.0e-12));
    float falloff = saturate((cosAngle - cosOuter) * angleScale);
    return falloff * falloff;
}

// ---------------------------------------------------------------------------------------------------------------------
// Ray relative to the light

struct FulumaSpotRay
{
    float3 origin;          // ray origin relative to the light origin
    float3 direction;
    float tClosest;         // t of the ray point closest to the light origin
    float h2;               // squared distance of the ray line from the light origin
};

FulumaSpotRay FulumaMakeSpotRay(float3 rayOrigin, float3 rayDirection, float3 lightPosition)
{
    FulumaSpotRay ray;
    ray.origin = rayOrigin - lightPosition;
    ray.direction = rayDirection;
    ray.tClosest = -dot(rayDirection, ray.origin);

    // |o x v|^2 rather than |o|^2 - tc^2: no cancellation when the camera is far from the light.
    float3 perpendicular = cross(ray.origin, rayDirection);
    ray.h2 = dot(perpendicular, perpendicular);
    return ray;
}

// ---------------------------------------------------------------------------------------------------------------------
// _FULUMA_SAMPLING_QUADRATURE: Gauss-Legendre quadrature of the full model (distance falloff x angular falloff) on each
// segment, in the airlight angle phi rather than in t.
//
// With t - tc = H tan(phi), H^2 = h^2 + e^2, the measure becomes dt = D / H dphi (D = s^2 + H^2), so
//   integral f(d) angular dt = (1 / H) integral f(d) D angular dphi.
// For inverse square f D is exactly the range window (smooth, bounded), and for the other falloffs it is smooth too.
// The segments already end at every kink of the integrand (inner and outer cone, cap), so a 3-point rule per segment is
// close to exact. Measured in float32 against brute force (Python, 145 rays incl. side, along-the-beam, inside and
// behind-apex views): worst 2.5-7% per ray for 60/30, 30/20 and 8/4 degree cones; in render tests within ~1-2% per pixel.
// No large terms are subtracted, so narrow cones are as accurate as wide ones.
//
// Why not a closed form: the exact antiderivatives exist (all terms are s^n D^(m/2)), and in float64 they matched brute
// force to 1e-4. In float32 the expansion of (cos - cosOuter)^2 into three large, nearly cancelling terms broke down:
// up to 64% error at the edges of a 60 degree cone and over 1000x for narrow cones (k^2 up to 3e5).

float FulumaSpotQuadratureSegment(FulumaSpotRay ray, float a, float b, float tScene, FulumaSpotLight light,
    bool insideInnerCone)
{
    b = min(b, tScene);
    if (b <= a)
        return 0.0;

    // Same source size as the inverse-square falloff, so the airlight substitution is exact for it.
    float epsilon = 0.02 * light.range;
    float h2 = ray.h2 + epsilon * epsilon;
    float h = sqrt(h2);

    float phi0 = atan((a - ray.tClosest) / h);
    float phi1 = atan((b - ray.tClosest) / h);
    float halfWidth = 0.5 * (phi1 - phi0);
    float center = 0.5 * (phi1 + phi0);

    const float3 nodes = float3(-0.7745966692, 0.0, 0.7745966692);  // sqrt(3/5)
    const float3 weights = float3(5.0 / 9.0, 8.0 / 9.0, 5.0 / 9.0);

    float sum = 0.0;

    UNITY_UNROLL
    for (int i = 0; i < 3; i++)
    {
        float s = h * tan(center + halfWidth * nodes[i]);
        float3 toPoint = ray.origin + ray.direction * (ray.tClosest + s);

        float angular = insideInnerCone
            ? 1.0
            : FulumaSpotAngularFalloff(toPoint, light.direction, light.cosOuter, light.angleScale);

        float jacobian = s * s + h2; // D: dt = D / h dphi
        sum += weights[i] * FulumaFalloffPoint(dot(toPoint, toPoint), light.range) * jacobian * angular;
    }

    return sum * halfWidth / h;
}

float FulumaSpotQuadrature(FulumaSpotRay ray, float tScene, FulumaSpotLight light,
    float outerNear, float outerFar, float innerNear, float innerFar, bool innerHit)
{
    // A penumbra-only ray is one segment: its angular falloff peaks smoothly inside, no kink to split at.
    if (!innerHit)
        return FulumaSpotQuadratureSegment(ray, outerNear, outerFar, tScene, light, false);

    return FulumaSpotQuadratureSegment(ray, outerNear, innerNear, tScene, light, false)
         + FulumaSpotQuadratureSegment(ray, innerNear, innerFar, tScene, light, true)
         + FulumaSpotQuadratureSegment(ray, innerFar, outerFar, tScene, light, false);
}

// ---------------------------------------------------------------------------------------------------------------------
// _FULUMA_SAMPLING_RAYMARCH: the reference. Marches the outer volume with a fixed world-space step and sums the same
// integrand (distance falloff x angular falloff) at every step, so it converges to exactly what the quadrature
// approximates as the step shrinks. The start is jittered per pixel (interleaved gradient noise): large steps show as
// fine noise instead of banding. Same depth clipping as the fake light.

float FulumaSpotRaymarch(FulumaSpotRay ray, float tScene, FulumaSpotLight light, float outerNear, float outerFar,
    float2 pixelCoord)
{
    float start = outerNear;
    float end = min(outerFar, tScene);
    if (end <= start)
        return 0.0;

    float pathLength = end - start;
    float stepSize = max(_FulumaRaymarchStepSize, pathLength / FULUMA_RAYMARCH_MAX_STEPS);
    int stepCount = (int)ceil(pathLength / stepSize);
    float jitter = InterleavedGradientNoise(pixelCoord, 0);

    float sum = 0.0;

    UNITY_LOOP
    for (int i = 0; i < stepCount; i++)
    {
        // Stratified: one sample per step, at a per-pixel offset inside it. Samples past the end are skipped, which
        // keeps the last partial step unbiased on average.
        float t = start + (i + jitter) * stepSize;
        if (t >= end)
            break;

        float3 toPoint = ray.origin + ray.direction * t;
        float angular = FulumaSpotAngularFalloff(toPoint, light.direction, light.cosOuter, light.angleScale);
        sum += FulumaFalloffPoint(dot(toPoint, toPoint), light.range) * angular;
    }

    return sum * stepSize;
}

// ---------------------------------------------------------------------------------------------------------------------

// Whole-ray glow of one light. tScene: distance along the ray to the first opaque surface.
float FulumaSpotLightScattering(float3 rayOrigin, float3 rayDirection, float tScene, FulumaSpotLight light,
    float outerNear, float outerFar, float innerNear, float innerFar, bool innerHit, float2 pixelCoord)
{
    FulumaSpotRay ray = FulumaMakeSpotRay(rayOrigin, rayDirection, light.position);

#if defined(_FULUMA_SAMPLING_RAYMARCH)
    return FulumaSpotRaymarch(ray, tScene, light, outerNear, outerFar, pixelCoord);
#else // _FULUMA_SAMPLING_QUADRATURE
    return FulumaSpotQuadrature(ray, tScene, light, outerNear, outerFar, innerNear, innerFar, innerHit);
#endif
}

// cosInner from the packed data (see top of file). Clamped below 1: an inner angle of 0 has no inner cone.
float FulumaSpotCosInner(FulumaSpotLight light)
{
    return min(light.cosOuter + 1.0 / max(light.angleScale, 1.0e-4), 0.999999);
}

#endif // FULUMA_SPOT_LIGHT_SCATTERING_INCLUDED
