#ifndef FULUMA_SPOT_LIGHT_SCATTERING_INCLUDED
#define FULUMA_SPOT_LIGHT_SCATTERING_INCLUDED

#include "../ShaderLibrary/Intersections.hlsl"

// In-scattering of a fake spot light along a view ray, from three segments split at the inner cone:
//
//   outer entry --A-- inner entry --B-- inner exit --C-- outer exit
//
// A and C cross the penumbra (angular falloff 0..1), B lies inside the inner cone (angular falloff exactly 1).
// A ray that misses the inner cone is split at its smallest angle to the axis into two penumbra segments. Every segment is clipped at the scene depth, and its
// value is evaluated at the midpoint of what remains:
//   _FULUMA_SAMPLING_POINT     value = length * distanceFalloff(midpoint) * angular(midpoint)
//   _FULUMA_SAMPLING_INTEGRAL  value = closed-form integral of distanceFalloff over the segment * angular(midpoint)
//   _FULUMA_SAMPLING_SHELLS    no segments: integral over the outer shape + integral over the inner shape (see below)
//   _FULUMA_SAMPLING_QUADRATURE 3-point Gauss-Legendre per segment in the airlight angle (see below)
//
// The inner cone comes from the packed data: angleScale = 1 / (cosInner - cosOuter), so
// cosInner = cosOuter + 1 / angleScale.
//
// Distance falloffs (_FULUMA_FALLOFF_*), each with the normalization used by both sampling modes, so a ray straight
// through the light origin across the whole sphere gives about 1 (smooth window, linear); inverse square has a hotter
// core and matches the others at a quarter-range offset:
//   SMOOTH_WINDOW  f(d) = (1 - d^2/r^2)^2       * 15 / (16 r)
//   INVERSE_SQUARE f(d) = 1 / (d^2 + e^2)        * r / (4 pi) * window(d), e = 2% of the range
//   LINEAR         f(d) = (1 - d / r)            * 1 / r

// ---------------------------------------------------------------------------------------------------------------------
// Point falloffs, f(d) at one point (d2 = squared distance to the light origin)

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

// ---------------------------------------------------------------------------------------------------------------------
// Closed-form integrals of the same falloffs along a ray segment.
// s0, s1: segment ends measured from the ray point closest to the light; h2: squared distance of the ray line from the
// light. A ray point at s lies at d^2 = s^2 + h2. Inside the volume |s| <= range, so these stay well conditioned.

// (1 - d^2/r^2)^2 with K = 1 - h2/r^2: F(s) = K^2 s - (2/3) K s^3 / r^2 + (1/5) s^5 / r^4.
float FulumaFalloffIntegralSmoothWindow(float s0, float s1, float h2, float range)
{
    float inverseRange2 = 1.0 / (range * range);
    float k = 1.0 - h2 * inverseRange2;

    float3 s0Powers = float3(s0, s0 * s0 * s0, s0 * s0 * s0 * s0 * s0);
    float3 s1Powers = float3(s1, s1 * s1 * s1, s1 * s1 * s1 * s1 * s1);
    float3 coefficients = float3(k * k, -2.0 / 3.0 * k * inverseRange2, 0.2 * inverseRange2 * inverseRange2);

    return dot(s1Powers - s0Powers, coefficients) * (15.0 / 16.0) / range;
}

// 1 / (d^2 + e^2): (atan(s1 / H) - atan(s0 / H)) / H with H^2 = h2 + e^2. The range window is applied once, at the
// point of the segment nearest to the light.
float FulumaFalloffIntegralInverseSquare(float s0, float s1, float h2, float range)
{
    float epsilon = 0.02 * range;
    float h = sqrt(h2 + epsilon * epsilon);

    float integral = (atan(s1 / h) - atan(s0 / h)) / h;

    float sNearest = clamp(0.0, s0, s1);
    float window = saturate(1.0 - (sNearest * sNearest + h2) / (range * range));

    return integral * window * window * range * (0.25 / PI);
}

float FulumaAsinh(float x)
{
    // Symmetric form; ln(|x| + sqrt(x^2 + 1)) avoids the cancellation of ln(x + sqrt(x^2 + 1)) for negative x.
    return sign(x) * log(abs(x) + sqrt(x * x + 1.0));
}

// 1 - d/r with d = sqrt(s^2 + h2): F(s) = s - (s d + h2 asinh(s / h)) / (2 r).
float FulumaFalloffIntegralLinear(float s0, float s1, float h2, float range)
{
    float h = max(sqrt(h2), 1.0e-5 * range);

    float d0 = sqrt(s0 * s0 + h2);
    float d1 = sqrt(s1 * s1 + h2);

    float f0 = s0 - (s0 * d0 + h2 * FulumaAsinh(s0 / h)) / (2.0 * range);
    float f1 = s1 - (s1 * d1 + h2 * FulumaAsinh(s1 / h)) / (2.0 * range);

    return (f1 - f0) / range;
}

float FulumaFalloffIntegral(float s0, float s1, float h2, float range)
{
#if defined(_FULUMA_FALLOFF_INVERSE_SQUARE)
    return FulumaFalloffIntegralInverseSquare(s0, s1, h2, range);
#elif defined(_FULUMA_FALLOFF_LINEAR)
    return FulumaFalloffIntegralLinear(s0, s1, h2, range);
#else // _FULUMA_FALLOFF_SMOOTH_WINDOW
    return FulumaFalloffIntegralSmoothWindow(s0, s1, h2, range);
#endif
}

// ---------------------------------------------------------------------------------------------------------------------
// Segments

// Inner/outer cone falloff at a point (given relative to the light origin): 0 at the outer edge, 1 inside the inner cone.
float FulumaSpotAngularFalloff(float3 toPoint, float3 lightDirection, float cosOuter, float angleScale)
{
    float cosAngle = dot(toPoint, lightDirection) * rsqrt(max(dot(toPoint, toPoint), 1.0e-12));
    float falloff = saturate((cosAngle - cosOuter) * angleScale);
    return falloff * falloff;
}

// Ray terms relative to the light, shared by all segments of one ray.
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

// Where a penumbra segment takes its angular sample.
//
// Midpoint in t, except for the integrated inverse-square falloff: there almost all of a segment's distance integral
// sits close to the light, while the angle to the axis changes strongly along the same stretch, so a midpoint
// sample can be far from where the light actually comes from (measured: 2-5x too bright on penumbra rays passing
// near the apex, and a visible seam along the inner cone's silhouette where segments switch). With the airlight
// substitution t - tc = H tan(phi), the inverse-square integrand dt / (d^2 + e^2) becomes dphi / H: uniform in
// phi. The midpoint in phi is therefore where the segment's light is centered (measured: within ~1.0-1.3x, no seam).
float FulumaSpotAngularSampleT(FulumaSpotRay ray, float a, float b, float range)
{
#if defined(_FULUMA_SAMPLING_INTEGRAL) && defined(_FULUMA_FALLOFF_INVERSE_SQUARE)
    float epsilon = 0.02 * range;
    float h = sqrt(ray.h2 + epsilon * epsilon);
    float phi0 = atan((a - ray.tClosest) / h);
    float phi1 = atan((b - ray.tClosest) / h);
    return ray.tClosest + h * tan(0.5 * (phi0 + phi1));
#else
    return 0.5 * (a + b);
#endif
}

// Value of segment [a, b], clipped at tScene. insideInnerCone skips the angular term (it is exactly 1 there).
float FulumaSpotSegment(FulumaSpotRay ray, float a, float b, float tScene, FulumaSpotLight light, bool insideInnerCone)
{
    b = min(b, tScene);
    if (b <= a)
        return 0.0;

    float3 midpoint = ray.origin + ray.direction * (0.5 * (a + b));

    float angular = 1.0;
    if (!insideInnerCone)
    {
        float3 angularPoint = ray.origin + ray.direction * FulumaSpotAngularSampleT(ray, a, b, light.range);
        angular = FulumaSpotAngularFalloff(angularPoint, light.direction, light.cosOuter, light.angleScale);
    }

#if defined(_FULUMA_SAMPLING_INTEGRAL)
    float distanceTerm = FulumaFalloffIntegral(a - ray.tClosest, b - ray.tClosest, ray.h2, light.range);
#else // _FULUMA_SAMPLING_POINT
    float distanceTerm = (b - a) * FulumaFalloffPoint(dot(midpoint, midpoint), light.range);
#endif

    return max(distanceTerm, 0.0) * angular;
}

// t of the ray point with the smallest angle to the light axis (largest angular falloff), clamped to [tMin, tMax].
// cos(angle) along the ray is (e + b t) / sqrt(c + 2 d t + t^2), with e = dot(o, axis), b = dot(v, axis),
// d = dot(v, o), c = |o|^2 (o relative to the light). Setting its derivative to 0 leaves a linear equation:
// t = (e d - b c) / (b d - e).
float FulumaSpotPeakAngleT(FulumaSpotRay ray, float3 lightDirection, float tMin, float tMax)
{
    float e = dot(ray.origin, lightDirection);
    float b = dot(ray.direction, lightDirection);
    float d = dot(ray.direction, ray.origin);
    float c = dot(ray.origin, ray.origin);

    float denominator = b * d - e;
    float t = abs(denominator) > 1.0e-6 ? (e * d - b * c) / denominator : 0.5 * (tMin + tMax);

    return clamp(t, tMin, tMax);
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
// behind-apex views): worst 2.5-7% per ray for 60/30, 30/20 and 8/4 degree cones. No large terms are subtracted, so
// narrow cones are as accurate as wide ones.
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

// _FULUMA_SAMPLING_SHELLS: no angular sampling at all. Each shape (outer and inner capped cone) contributes the
// closed-form distance-falloff integral over the part of the ray inside it, clipped at the scene depth. Inside the
// inner cone a ray is counted twice, in the penumbra once, which gives a two-level angular profile.
// FULUMA_SHELL_WEIGHT scales the sum so the inner cone keeps the brightness of the other modes (angular = 1 there).
#define FULUMA_SHELL_WEIGHT 0.5

float FulumaSpotShell(FulumaSpotRay ray, float a, float b, float tScene, float range)
{
    b = min(b, tScene);
    if (b <= a)
        return 0.0;

    return max(FulumaFalloffIntegral(a - ray.tClosest, b - ray.tClosest, ray.h2, range), 0.0);
}

// Whole-ray glow of one light. tScene: distance along the ray to the first opaque surface.
float FulumaSpotLightScattering(float3 rayOrigin, float3 rayDirection, float tScene, FulumaSpotLight light,
    float outerNear, float outerFar, float innerNear, float innerFar, bool innerHit)
{
    FulumaSpotRay ray = FulumaMakeSpotRay(rayOrigin, rayDirection, light.position);

#if defined(_FULUMA_SAMPLING_SHELLS)
    float shells = FulumaSpotShell(ray, outerNear, outerFar, tScene, light.range);
    if (innerHit)
        shells += FulumaSpotShell(ray, innerNear, innerFar, tScene, light.range);

    return shells * FULUMA_SHELL_WEIGHT;
#elif defined(_FULUMA_SAMPLING_QUADRATURE)
    return FulumaSpotQuadrature(ray, tScene, light, outerNear, outerFar, innerNear, innerFar, innerHit);
#else
    if (!innerHit)
    {
        // Penumbra only: split where the ray comes closest (in angle) to the axis. Each half then runs from the outer
        // edge toward the angular peak, like segments A and C; as the ray approaches the inner cone the split point
        // becomes the point where it touches the inner cone, so the result joins the three-segment case without a seam.
        float peak = FulumaSpotPeakAngleT(ray, light.direction, outerNear, outerFar);

        return FulumaSpotSegment(ray, outerNear, peak, tScene, light, false)
             + FulumaSpotSegment(ray, peak, outerFar, tScene, light, false);
    }

    return FulumaSpotSegment(ray, outerNear, innerNear, tScene, light, false)
         + FulumaSpotSegment(ray, innerNear, innerFar, tScene, light, true)
         + FulumaSpotSegment(ray, innerFar, outerFar, tScene, light, false);
#endif
}

// cosInner from the packed data (see top of file). Clamped below 1: an inner angle of 0 has no inner cone.
float FulumaSpotCosInner(FulumaSpotLight light)
{
    return min(light.cosOuter + 1.0 / max(light.angleScale, 1.0e-4), 0.999999);
}

#endif // FULUMA_SPOT_LIGHT_SCATTERING_INCLUDED
