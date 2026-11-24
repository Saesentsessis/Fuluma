#ifndef FULUMA_ANALYTICAL_INTERSECTIONS_INCLUDED
#define FULUMA_ANALYTICAL_INTERSECTIONS_INCLUDED

// Ray intersections for analytical (fake) volumetric lights.
// All inputs are in the same space (world space in practice); ray and axis directions must be normalized.

#define FULUMA_INTERSECTION_NO_HIT 1.0e30

// Extends the [tNear, tFar] interval with hit t, if t lies in front of the ray origin and its height along the
// cone axis is in [minHeight, maxHeight].
// height = dot(rayOrigin + t * rayDirection - coneOrigin, coneDirection), expanded as axialBase + t * axialSlope.
void FulumaAccumulateConeHit(float t, float axialBase, float axialSlope, float minHeight, float maxHeight,
    inout float tNear, inout float tFar)
{
    float height = axialBase + t * axialSlope;

    // Small tolerance so hits exactly on the cone/cap seam aren't lost to rounding.
    float tolerance = 1.0e-4 * max(maxHeight, 1.0);
    bool valid = t >= 0.0 && height >= minHeight - tolerance && height <= maxHeight + tolerance;

    tNear = valid ? min(tNear, t) : tNear;
    tFar = valid ? max(tFar, t) : tFar;
}

// Ray terms shared by every cone and cap test against the same apex and axis.
struct FulumaConeRay
{
    float directionDot;             // dot(rayDirection, coneDirection)
    float rayToConeAxialDot;        // dot(rayOrigin - coneOrigin, coneDirection)
    float rayDirectionRayToCone;    // dot(rayDirection, rayOrigin - coneOrigin)
    float rayToConeLengthSquared;   // |rayOrigin - coneOrigin|^2
#if defined(_FULUMA_WIDE_CONES)
    // Moment m = (rayOrigin - coneOrigin) x rayDirection: |m| is the ray line's distance from the apex.
    float momentLengthSquared;      // |m|^2
    float momentAxialDot;           // dot(m, coneDirection)
#endif
};

// rayToCone: ray origin minus cone apex.
FulumaConeRay FulumaMakeConeRayRelative(float3 rayToCone, float3 coneDirection, float3 rayDirection)
{
    FulumaConeRay ray;
    ray.directionDot = dot(rayDirection, coneDirection);
    ray.rayToConeAxialDot = dot(rayToCone, coneDirection);
    ray.rayDirectionRayToCone = dot(rayDirection, rayToCone);
    ray.rayToConeLengthSquared = dot(rayToCone, rayToCone);
#if defined(_FULUMA_WIDE_CONES)
    float3 moment = cross(rayToCone, rayDirection);
    ray.momentLengthSquared = dot(moment, moment);
    ray.momentAxialDot = dot(moment, coneDirection);
#endif
    return ray;
}

FulumaConeRay FulumaMakeConeRay(float3 coneOrigin, float3 coneDirection, float3 rayOrigin, float3 rayDirection)
{
    return FulumaMakeConeRayRelative(rayOrigin - coneOrigin, coneDirection, rayDirection);
}

// Hits on the cone surface (apex to rim height): a t^2 + b t + c = 0.
void FulumaAccumulateConeSurfaceHits(FulumaConeRay ray, float cosTheta, float rimHeight, inout float tNear, inout float tFar)
{
    float cosSquared = cosTheta * cosTheta;

    float a = ray.directionDot * ray.directionDot - cosSquared;
    float b = 2.0 * (ray.directionDot * ray.rayToConeAxialDot - ray.rayDirectionRayToCone * cosSquared);
    float c = ray.rayToConeAxialDot * ray.rayToConeAxialDot - ray.rayToConeLengthSquared * cosSquared;

    if (abs(a) < 1.0e-6)
    {
        // Ray parallel to the cone surface: the quadratic collapses to a linear equation.
        if (abs(b) > 1.0e-6)
            FulumaAccumulateConeHit(-c / b, ray.rayToConeAxialDot, ray.directionDot, 0.0, rimHeight, tNear, tFar);
    }
    else
    {
#if defined(_FULUMA_WIDE_CONES)
        // Cancellation-free form of b^2 - 4ac (rayDirection is unit length):
        //   b^2 - 4ac = 4 cos^2 (|m|^2 sin^2 - dot(m, coneDirection)^2), m = rayToCone x rayDirection.
        // b^2 and 4ac both grow with the squared distance to the apex while their difference shrinks with cos^2: for
        // half-angles past ~80 degrees, rays near the axis lost or misplaced their hits in float32 (speckled holes).
        float discriminant = 4.0 * cosSquared
            * (ray.momentLengthSquared * (1.0 - cosSquared) - ray.momentAxialDot * ray.momentAxialDot);
#else
        float discriminant = b * b - 4.0 * a * c;
#endif
        if (discriminant >= 0.0)
        {
            float discriminantSqrt = sqrt(discriminant);
            float inverse2A = 0.5 / a;

            FulumaAccumulateConeHit((-b - discriminantSqrt) * inverse2A, ray.rayToConeAxialDot, ray.directionDot, 0.0, rimHeight, tNear, tFar);
            FulumaAccumulateConeHit((-b + discriminantSqrt) * inverse2A, ray.rayToConeAxialDot, ray.directionDot, 0.0, rimHeight, tNear, tFar);
        }
    }
}

// Origin inside the capped cone: within range and within the cone angle.
bool FulumaIsInsideCappedCone(FulumaConeRay ray, float range, float cosTheta)
{
    return ray.rayToConeLengthSquared <= range * range
        && ray.rayToConeAxialDot >= 0.0
        && ray.rayToConeAxialDot * ray.rayToConeAxialDot >= ray.rayToConeLengthSquared * cosTheta * cosTheta;
}

// Ray vs. a cone capped by a sphere: the volume of a spot light.
// The cone has its apex at coneOrigin, opens along coneDirection with half-angle acos(cosTheta), and is cut by
// the sphere of radius range around the apex (so the cap is a spherical cap, not a flat disc).
// Valid for 0 < half-angle < 90 degrees (the volume is convex there).
//
// Port of ConeIntersectionTester.RaySphereCappedCone (Tests/ConeIntersectionTester.cs), with one addition:
// when the ray starts inside the volume, tNear is 0 (the C# version reports only the exit point then).
//
// Returns true if the ray hits the volume in front of its origin; [tNear, tFar] is the inside segment along the ray.
bool RaySphereCappedCone(float3 coneOrigin, float3 coneDirection, float range, float cosTheta,
    float3 rayOrigin, float3 rayDirection, out float tNear, out float tFar)
{
    FulumaConeRay ray = FulumaMakeConeRay(coneOrigin, coneDirection, rayOrigin, rayDirection);
    float rimHeight = range * cosTheta;

    tNear = FULUMA_INTERSECTION_NO_HIT;
    tFar = -FULUMA_INTERSECTION_NO_HIT;

    FulumaAccumulateConeSurfaceHits(ray, cosTheta, rimHeight, tNear, tFar);

    // Spherical cap: hits on the range sphere above the rim.
    float discriminant = ray.rayDirectionRayToCone * ray.rayDirectionRayToCone - (ray.rayToConeLengthSquared - range * range);
    if (discriminant >= 0.0)
    {
        float discriminantSqrt = sqrt(discriminant);

        FulumaAccumulateConeHit(-ray.rayDirectionRayToCone - discriminantSqrt, ray.rayToConeAxialDot, ray.directionDot, rimHeight, range, tNear, tFar);
        FulumaAccumulateConeHit(-ray.rayDirectionRayToCone + discriminantSqrt, ray.rayToConeAxialDot, ray.directionDot, rimHeight, range, tNear, tFar);
    }

    if (FulumaIsInsideCappedCone(ray, range, cosTheta))
        tNear = 0.0;

    return tFar >= tNear && tNear < FULUMA_INTERSECTION_NO_HIT;
}

// ---------------------------------------------------------------------------------------------------------------------
// Nested sphere-capped cones sharing apex, axis and range (a spot light's outer and inner angle).
// The cap sphere is solved once and each cone adds only its own quadratic. Split in steps so a caller can test the
// outer volume (and its own early-outs, such as scene depth) before paying for the inner cone.

// Roots of the ray against the range sphere around the apex (the cap of every cone of the pair).
struct FulumaCapSphereHits
{
    float t0;
    float t1;
    bool hit;
};

FulumaCapSphereHits FulumaIntersectCapSphere(FulumaConeRay ray, float range)
{
    float discriminant = ray.rayDirectionRayToCone * ray.rayDirectionRayToCone - (ray.rayToConeLengthSquared - range * range);
    float discriminantSqrt = sqrt(max(discriminant, 0.0));

    FulumaCapSphereHits hits;
    hits.hit = discriminant >= 0.0;
    hits.t0 = -ray.rayDirectionRayToCone - discriminantSqrt;
    hits.t1 = -ray.rayDirectionRayToCone + discriminantSqrt;
    return hits;
}

// One cone of the pair, capped by the shared sphere. originInside: the ray origin is inside this capped cone
// (FulumaIsInsideCappedCone), passed in because it's often known per light (camera origin) rather than per ray.
// Returns whether it's hit; [tNear, tFar] is the inside segment along the ray.
bool FulumaRayCappedConeOfPair(FulumaConeRay ray, FulumaCapSphereHits cap, float range, float cosTheta, bool originInside,
    out float tNear, out float tFar)
{
    float rimHeight = range * cosTheta;

    tNear = FULUMA_INTERSECTION_NO_HIT;
    tFar = -FULUMA_INTERSECTION_NO_HIT;

    FulumaAccumulateConeSurfaceHits(ray, cosTheta, rimHeight, tNear, tFar);

    // The cap: hits on the range sphere above this cone's rim.
    if (cap.hit)
    {
        FulumaAccumulateConeHit(cap.t0, ray.rayToConeAxialDot, ray.directionDot, rimHeight, range, tNear, tFar);
        FulumaAccumulateConeHit(cap.t1, ray.rayToConeAxialDot, ray.directionDot, rimHeight, range, tNear, tFar);
    }

    if (originInside)
        tNear = 0.0;

    return tFar >= tNear && tNear < FULUMA_INTERSECTION_NO_HIT;
}

// Keeps [innerNear, innerFar] inside [outerNear, outerFar] despite rounding at the seams. Returns whether the inner
// segment is still non-empty.
bool FulumaNestInnerSegment(bool innerHit, float outerNear, float outerFar, inout float innerNear, inout float innerFar)
{
    innerNear = max(innerNear, outerNear);
    innerFar = min(innerFar, outerFar);
    return innerHit && innerFar > innerNear;
}

// Both cones of a spot light in one call. The inner cone lies inside the outer one, so [innerNear, innerFar] is inside
// [outerNear, outerFar] whenever both hit. Requires cosInner >= cosOuter. Returns whether the outer volume is hit;
// innerHit says whether the inner one is.
bool RaySphereCappedConePair(float3 coneOrigin, float3 coneDirection, float range, float cosOuter, float cosInner,
    float3 rayOrigin, float3 rayDirection,
    out float outerNear, out float outerFar, out float innerNear, out float innerFar, out bool innerHit)
{
    FulumaConeRay ray = FulumaMakeConeRay(coneOrigin, coneDirection, rayOrigin, rayDirection);
    FulumaCapSphereHits cap = FulumaIntersectCapSphere(ray, range);

    bool outerHit = FulumaRayCappedConeOfPair(ray, cap, range, cosOuter, FulumaIsInsideCappedCone(ray, range, cosOuter),
        outerNear, outerFar);
    innerHit = FulumaRayCappedConeOfPair(ray, cap, range, cosInner, FulumaIsInsideCappedCone(ray, range, cosInner),
        innerNear, innerFar);
    innerHit = FulumaNestInnerSegment(innerHit, outerNear, outerFar, innerNear, innerFar);

    return outerHit;
}

#endif // FULUMA_ANALYTICAL_INTERSECTIONS_INCLUDED
