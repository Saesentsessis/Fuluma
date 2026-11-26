#ifndef FULUMA_SPOT_LIGHT_FORWARD_PASS_INCLUDED
#define FULUMA_SPOT_LIGHT_FORWARD_PASS_INCLUDED

#include "FulumaSpotLightScattering.hlsl"

struct Attributes
{
    float3 positionOS : POSITION;   // proxy space, see SpotLightProxyMeshBuilder.cs
    uint instanceID : SV_InstanceID;
};

// The light is decoded once per vertex and handed to the fragment stage as flat varyings, instead of every pixel reloading
// it from the structured buffers and decoding it again (2 buffer loads, 2 sincos for the packed direction, half and
// RGBA8 unpacking, the color-space conversion). Everything that depends only on the light and the camera position is
// computed here too: with a perspective camera every pixel's ray starts at the camera, so those terms are the same for
// all of a light's pixels.
struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 viewVectorWS : TEXCOORD0;                                // world position minus camera position
    nointerpolation float4 lightToCameraRange : TEXCOORD1;          // xyz: camera position minus light position, w: range
    nointerpolation float4 lightDirectionCosOuter : TEXCOORD2;      // xyz: axis, w: cos(outer half-angle)
    nointerpolation float4 lightRadianceAngleScale : TEXCOORD3;     // xyz: radiance, w: angleScale
    nointerpolation float3 lightCosInnerCameraInside : TEXCOORD4;   // x: cos(inner half-angle); y, z: the camera is
                                                                    // inside the outer / inner capped cone (1 or 0)
#if defined(_FULUMA_CLIP_BOX_SINGLE) || defined(_FULUMA_CLIP_BOXES)
    nointerpolation uint2 clipBoxReferences : TEXCOORD5;            // the light's packed clip box references
#endif
#if defined(FULUMA_CLIP_BOX_VARYINGS)
    // Per box, its ray in box space (FulumaClipBoxVaryings): a flat and an interpolated term. Pair variants: box 0 is
    // the keep box (keep + subtract), box 1 the other.
    nointerpolation float3 clipBox0Flat : TEXCOORD6;
    float3 clipBox0Ray : TEXCOORD7;
#endif
#if defined(FULUMA_CLIP_BOX_PAIR_VARYINGS)
    nointerpolation float3 clipBox1Flat : TEXCOORD8;
    float3 clipBox1Ray : TEXCOORD9;
#endif
#if defined(FULUMA_CLIP_BOX_PACKED_VARYINGS)
    // The four slots' terms (FulumaClipBoxVaryings), packed: 3 flat and 3 interpolated rows instead of 4 + 4
    // (FulumaPackClipBoxTerms).
    nointerpolation float4 clipBoxFlatA : TEXCOORD6;
    nointerpolation float4 clipBoxFlatB : TEXCOORD7;
    nointerpolation float4 clipBoxFlatC : TEXCOORD8;
    float4 clipBoxRayA : TEXCOORD9;
    float4 clipBoxRayB : TEXCOORD10;
    float4 clipBoxRayC : TEXCOORD11;
#endif
};

#if defined(FULUMA_CLIPPING)
// One box's view ray in box space, as two terms that reach every pixel exactly. The map to box space is affine and t is
// the same in both spaces.
// - Perspective: every ray starts at the camera (flat: the camera in box space); its direction is the normalized view
//   vector, and the box-space image of the view vector interpolates linearly (interpolated). The pixel rescales it.
// - Orthographic: the direction is the camera's forward axis (flat, in box space); the origin, on the camera plane, is
//   an affine function of the vertex position (interpolated).
// No reference (0xFFFF): both terms 0, which the slab test reads as an unlimited box.
void FulumaClipBoxVaryings(uint reference, float3 cameraPosition, float3 viewVector, out float3 flatTerm,
    out float3 interpolatedTerm)
{
    flatTerm = 0.0;
    interpolatedTerm = 0.0;

    UNITY_BRANCH
    if (reference == FULUMA_CLIP_BOX_NONE)
        return;

    FulumaPackedClipBox box = _FulumaClipBoxes[reference & FULUMA_CLIP_BOX_INDEX_MASK];
    float3x3 toBox = float3x3(box.row0.xyz, box.row1.xyz, box.row2.xyz);
    float3 offset = float3(box.row0.w, box.row1.w, box.row2.w);

    if (IsPerspectiveProjection())
    {
        flatTerm = mul(toBox, cameraPosition) + offset;
        interpolatedTerm = mul(toBox, viewVector);
    }
    else
    {
        float3 forward = GetViewForwardDir();
        float3 origin = cameraPosition + viewVector - forward * dot(viewVector, forward);
        flatTerm = mul(toBox, forward);
        interpolatedTerm = mul(toBox, origin) + offset;

        // A camera looking along a box axis gives exact zeros here, and the slab test divides by the direction:
        // Direct3D defines x / 0 as +-inf, OpenGL leaves it undefined (clip boxes then cut the wrong region). A tiny
        // direction gives the same slabs with finite numbers.
        flatTerm = abs(flatTerm) < FULUMA_CLIP_MIN_DIRECTION ? FULUMA_CLIP_MIN_DIRECTION : flatTerm;
    }
}

// The box's interval along this pixel's ray from its two terms (FulumaClipBoxVaryings). inverseViewLength: 1 / |view
// vector| (perspective only).
float2 FulumaClipBoxIntervalFromVaryings(float3 flatTerm, float3 interpolatedTerm, float inverseViewLength)
{
    bool perspective = IsPerspectiveProjection();
    float3 origin = perspective ? flatTerm : interpolatedTerm;
    float3 direction = perspective ? interpolatedTerm * inverseViewLength : flatTerm;
    return FulumaClipBoxSlab(origin, direction);
}

// Four float3 into three float4 and back (the general variant's packed varyings).
void FulumaPackClipBoxTerms(float3 term0, float3 term1, float3 term2, float3 term3, out float4 a, out float4 b, out float4 c)
{
    a = float4(term0, term1.x);
    b = float4(term1.yz, term2.xy);
    c = float4(term2.z, term3);
}

void FulumaUnpackClipBoxTerms(float4 a, float4 b, float4 c, out float3 term0, out float3 term1, out float3 term2,
    out float3 term3)
{
    term0 = a.xyz;
    term1 = float3(a.w, b.xy);
    term2 = float3(b.zw, c.x);
    term3 = c.yzw;
}
#endif

FulumaSpotLight FulumaUnpackSpotLightVaryings(Varyings input)
{
    FulumaSpotLight light;
    light.position = GetCameraPositionWS() - input.lightToCameraRange.xyz;
    light.range = input.lightToCameraRange.w;
    light.direction = input.lightDirectionCosOuter.xyz;
    light.cosOuter = input.lightDirectionCosOuter.w;
    light.radiance = input.lightRadianceAngleScale.xyz;
    light.angleScale = input.lightRadianceAngleScale.w;
    return light;
}

// Maps a proxy-space vertex onto a polyhedron that encloses the light's capped cone.
// The flat faces would cut inside the curved surfaces, so two corrections apply:
//  - The cone side is widened: with N segments the face midpoints sit at cos(pi / N) of the vertex radius, so the rim
//    goes to the angle whose tangent is tan(outer) / cos(pi / N).
//  - Cap vertices are pushed out by 1 / cos(c * widened half-angle), c being the LOD's largest cap face angle per
//    radian of widened half-angle (_FulumaSpotProxyShape.y, fitted on the CPU), so every cap face stays outside the
//    range sphere.
void FulumaSpotProxyShape(float cosOuter, out float widenedHalfAngle, out float inflation)
{
    // Widen the cone so the rim's flat faces enclose the round cone, and inflate the cap so its flat faces enclose the
    // round cap (fitted per LOD on the CPU, see SpotLightProxyShape).
    float sinOuter = sqrt(saturate(1.0 - cosOuter * cosOuter));
    widenedHalfAngle = atan2(sinOuter, cosOuter * _FulumaSpotProxyShape.x);
    inflation = 1.0 / cos(_FulumaSpotProxyShape.y * widenedHalfAngle);
}

float3 FulumaSpotProxyVertex(float3 proxyPosition, float range, float widenedHalfAngle, float inflation)
{
    if (proxyPosition.z < 0.0)
        return float3(0.0, 0.0, 0.0); // apex

    float polar = (1.0 - proxyPosition.z) * widenedHalfAngle;
    float sinPolar, cosPolar;
    sincos(polar, sinPolar, cosPolar);

    return range * inflation * float3(proxyPosition.xy * sinPolar, cosPolar);
}

Varyings FulumaSpotLightVertex(Attributes input)
{
    // The instance is the n-th visible light of this draw's list; which pass draws it was decided by CPU culling
    // (SpotLightCullingMath.CameraNearProxy).
    uint lightIndex = _FulumaVisibleSpotLights[_FulumaVisibleOffset + input.instanceID];
    FulumaSpotLight light = LoadFulumaSpotLight(lightIndex);

    float widenedHalfAngle, inflation;
    FulumaSpotProxyShape(light.cosOuter, widenedHalfAngle, inflation);

    float3 local = FulumaSpotProxyVertex(input.positionOS, light.range, widenedHalfAngle, inflation);

    // Right-handed basis around the light axis, so the mesh winding survives the transform.
    float3 forward = light.direction;
    float3 up = abs(forward.y) < 0.999 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
    float3 tangent = normalize(cross(up, forward));
    float3 bitangent = cross(forward, tangent);

    float3 positionWS = light.position + tangent * local.x + bitangent * local.y + forward * local.z;
    float3 cameraPosition = GetCameraPositionWS();
    float3 lightToCamera = cameraPosition - light.position;
    float cosInner = FulumaSpotCosInner(light);

    // Whether the camera is inside each capped cone: what a perspective ray's origin test gives for every pixel.
    FulumaConeRay cameraRay = FulumaMakeConeRayRelative(lightToCamera, light.direction, light.direction);

    Varyings output;
    output.positionCS = TransformWorldToHClip(positionWS);
    output.viewVectorWS = positionWS - cameraPosition;

    output.lightToCameraRange = float4(lightToCamera, light.range);
    output.lightDirectionCosOuter = float4(light.direction, light.cosOuter);
    output.lightRadianceAngleScale = float4(light.radiance, light.angleScale);
    output.lightCosInnerCameraInside = float3(cosInner,
        FulumaIsInsideCappedCone(cameraRay, light.range, light.cosOuter) ? 1.0 : 0.0,
        FulumaIsInsideCappedCone(cameraRay, light.range, cosInner) ? 1.0 : 0.0);

#if defined(FULUMA_CLIPPING)
    uint2 clipBoxReferences = _FulumaSpotClipBoxReferences[lightIndex];
#endif
#if defined(_FULUMA_CLIP_BOX_SINGLE) || defined(_FULUMA_CLIP_BOXES)
    output.clipBoxReferences = clipBoxReferences;
#endif
#if defined(FULUMA_CLIP_BOX_PACKED_VARYINGS)
    {
        float3 flat0, flat1, flat2, flat3, ray0, ray1, ray2, ray3;
        FulumaClipBoxVaryings(clipBoxReferences.x & 0xFFFFu, cameraPosition, output.viewVectorWS, flat0, ray0);
        FulumaClipBoxVaryings(clipBoxReferences.x >> 16, cameraPosition, output.viewVectorWS, flat1, ray1);
        FulumaClipBoxVaryings(clipBoxReferences.y & 0xFFFFu, cameraPosition, output.viewVectorWS, flat2, ray2);
        FulumaClipBoxVaryings(clipBoxReferences.y >> 16, cameraPosition, output.viewVectorWS, flat3, ray3);
        FulumaPackClipBoxTerms(flat0, flat1, flat2, flat3, output.clipBoxFlatA, output.clipBoxFlatB, output.clipBoxFlatC);
        FulumaPackClipBoxTerms(ray0, ray1, ray2, ray3, output.clipBoxRayA, output.clipBoxRayB, output.clipBoxRayC);
    }
#endif
#if defined(FULUMA_CLIP_BOX_VARYINGS)
    uint firstBox = clipBoxReferences.x & 0xFFFFu;
#if defined(FULUMA_CLIP_BOX_PAIR_VARYINGS)
    uint secondBox = clipBoxReferences.x >> 16;
#if defined(_FULUMA_CLIP_BOX_PAIR)
    // The keep box first.
    if ((firstBox & FULUMA_CLIP_BOX_SUBTRACT) != 0)
    {
        uint keepBox = secondBox;
        secondBox = firstBox;
        firstBox = keepBox;
    }
#endif
    FulumaClipBoxVaryings(secondBox, cameraPosition, output.viewVectorWS, output.clipBox1Flat, output.clipBox1Ray);
#endif
    FulumaClipBoxVaryings(firstBox, cameraPosition, output.viewVectorWS, output.clipBox0Flat, output.clipBox0Ray);
#endif

    return output;
}

// View ray through this pixel, relative to the light (rayToLight: ray origin minus light position), and whether its
// origin is inside the outer / inner capped cone.
// Perspective: from the camera, so the origin terms are per light and come from the vertex shader.
// Orthographic: parallel rays starting on the camera plane, so the origin (and its inside tests) vary per pixel.
void FulumaGetViewRay(Varyings input, FulumaSpotLight light, float cosInner,
    out float3 rayToLight, out float3 rayDirection, out bool insideOuter, out bool insideInner)
{
    UNITY_BRANCH
    if (IsPerspectiveProjection())
    {
        rayToLight = input.lightToCameraRange.xyz;
        rayDirection = normalize(input.viewVectorWS);
        insideOuter = input.lightCosInnerCameraInside.y > 0.5;
        insideInner = input.lightCosInnerCameraInside.z > 0.5;
    }
    else
    {
        rayDirection = GetViewForwardDir();
        rayToLight = input.lightToCameraRange.xyz + input.viewVectorWS
            - rayDirection * dot(input.viewVectorWS, rayDirection);

        FulumaConeRay originRay = FulumaMakeConeRayRelative(rayToLight, light.direction, rayDirection);
        insideOuter = FulumaIsInsideCappedCone(originRay, light.range, light.cosOuter);
        insideInner = FulumaIsInsideCappedCone(originRay, light.range, cosInner);
    }
}

// Distance along the ray to the first opaque surface.
float FulumaSceneDistance(float2 positionCS, float3 rayDirection)
{
#if defined(_DEPTH_NONE)
    // The feature's "Use Scene Depth" is off: nothing occludes the lights.
    return FLT_MAX;
#elif defined(_DEPTH_LOW_RES)
    // The downsampled buffer's depth is already linear eye depth (FulumaLightBuffer.shader's downsample): along the
    // camera's forward axis for perspective, and the distance from the camera plane for orthographic.
    float eyeDepth = LoadFulumaSceneDepth(positionCS);
    return IsPerspectiveProjection() ? eyeDepth / dot(rayDirection, GetViewForwardDir()) : eyeDepth;
#else
    float rawDepth = LoadFulumaSceneDepth(positionCS);
    float tScene;

    UNITY_BRANCH
    if (IsPerspectiveProjection())
    {
        // Eye depth is measured along the camera's forward axis; the ray reaches it after depth / cos(angle).
        // Much cheaper than reconstructing the world position (4x4 matrix multiply and divide).
        tScene = LinearEyeDepth(rawDepth, _ZBufferParams) / dot(rayDirection, GetViewForwardDir());
    }
    else
    {
        // Orthographic depth is linear between the clip planes, and the ray starts on the camera plane, so the
        // distance along it is the eye depth. No screen position needed (the target may be a downsampled buffer).
        float depth01 = rawDepth;
#if UNITY_REVERSED_Z
        depth01 = 1.0 - depth01;
#endif
        tScene = lerp(_ProjectionParams.y, _ProjectionParams.z, depth01);
    }

    return tScene;
#endif
}

half4 FulumaSpotLightFragment(Varyings input) : SV_Target
{
    FulumaSpotLight light = FulumaUnpackSpotLightVaryings(input);
    float cosInner = input.lightCosInnerCameraInside.x;

    float3 rayToLight, rayDirection;
    bool insideOuter, insideInner;
    FulumaGetViewRay(input, light, cosInner, rayToLight, rayDirection, insideOuter, insideInner);

    // Outer volume first: most proxy pixels of a light miss it, and they leave before the depth fetch and before the
    // inner cone is solved.
    FulumaConeRay coneRay = FulumaMakeConeRayRelative(rayToLight, light.direction, rayDirection);
    FulumaCapSphereHits cap = FulumaIntersectCapSphere(coneRay, light.range);

    float outerNear, outerFar;
    if (!FulumaRayCappedConeOfPair(coneRay, cap, light.range, light.cosOuter, insideOuter, outerNear, outerFar))
        return half4(0.0, 0.0, 0.0, 0.0);

    // Stop at the first opaque surface along the ray; fully hidden volumes leave before the inner cone too.
    float tScene = FulumaSceneDistance(input.positionCS.xy, rayDirection);
    if (tScene <= outerNear)
        return half4(0.0, 0.0, 0.0, 0.0);

#if defined(FULUMA_CLIP_BOX_VARYINGS)
    // One or two boxes, from the vertex stage (no loads, no matrix math): rays that miss the region leave before the
    // inner cone.
    float clippedFar = min(outerFar, tScene);
    float inverseViewLength = rsqrt(dot(input.viewVectorWS, input.viewVectorWS));
    float2 box0 = FulumaClipBoxIntervalFromVaryings(input.clipBox0Flat, input.clipBox0Ray, inverseViewLength);
    float2 firstPiece, secondPiece;
    bool inRegion;

#if defined(_FULUMA_CLIP_BOX_SINGLE)
    // The box's interval (keep) or the ray around it (subtract).
    uint reference = input.clipBoxReferences.x & 0xFFFFu;
    bool subtract = reference != FULUMA_CLIP_BOX_NONE && (reference & FULUMA_CLIP_BOX_SUBTRACT) != 0;
    inRegion = FulumaClipSingleBoxPieces(box0, subtract, outerNear, clippedFar, firstPiece, secondPiece);
#elif defined(_FULUMA_CLIP_BOX_PAIR)
    // The room (box 0) minus the cut-out (box 1). Where a whole warp misses the room, the cut-out's slab is skipped.
    if (FulumaClipRoomMissed(box0, outerNear, clippedFar))
        return half4(0.0, 0.0, 0.0, 0.0);

    float2 box1 = FulumaClipBoxIntervalFromVaryings(input.clipBox1Flat, input.clipBox1Ray, inverseViewLength);
    inRegion = FulumaClipKeepMinusSubtractPieces(box0, box1, outerNear, clippedFar, firstPiece, secondPiece);
#elif defined(_FULUMA_CLIP_BOX_UNION)
    // Two keeps: the union of two rooms.
    float2 box1 = FulumaClipBoxIntervalFromVaryings(input.clipBox1Flat, input.clipBox1Ray, inverseViewLength);
    inRegion = FulumaClipKeepUnionPieces(box0, box1, outerNear, clippedFar, firstPiece, secondPiece);
#else
    // Two subtracts: the ray minus both boxes, up to three pieces.
    float2 box1 = FulumaClipBoxIntervalFromVaryings(input.clipBox1Flat, input.clipBox1Ray, inverseViewLength);
    float2 thirdPiece;
    inRegion = FulumaClipExcludeTwoPieces(box0, box1, outerNear, clippedFar, firstPiece, secondPiece, thirdPiece);
#endif

    if (inRegion == false)
        return half4(0.0, 0.0, 0.0, 0.0);
#elif defined(_FULUMA_CLIP_BOXES)
    // Several boxes (or any lights, when culling didn't sort them by clip class): the volume along the ray limited to
    // the light's region, walked breakpoint by breakpoint. Rays that miss the region leave before the inner cone.
    float clippedFar = min(outerFar, tScene);
    float inverseViewLength = rsqrt(dot(input.viewVectorWS, input.viewVectorWS));
    float3 flat0, flat1, flat2, flat3, ray0, ray1, ray2, ray3;
    FulumaUnpackClipBoxTerms(input.clipBoxFlatA, input.clipBoxFlatB, input.clipBoxFlatC, flat0, flat1, flat2, flat3);
    FulumaUnpackClipBoxTerms(input.clipBoxRayA, input.clipBoxRayB, input.clipBoxRayC, ray0, ray1, ray2, ray3);
    float2 slot0 = FulumaClipBoxIntervalFromVaryings(flat0, ray0, inverseViewLength);
    float2 slot1 = FulumaClipBoxIntervalFromVaryings(flat1, ray1, inverseViewLength);
    float2 slot2 = FulumaClipBoxIntervalFromVaryings(flat2, ray2, inverseViewLength);
    float2 slot3 = FulumaClipBoxIntervalFromVaryings(flat3, ray3, inverseViewLength);
    FulumaClipIntervals clip = FulumaClipIntervalsFromSlots(input.clipBoxReferences,
        float4(slot0.x, slot1.x, slot2.x, slot3.x), float4(slot0.y, slot1.y, slot2.y, slot3.y), outerNear, clippedFar);

    if (FulumaClipAnyInside(clip) == false)
        return half4(0.0, 0.0, 0.0, 0.0);
#endif

    float innerNear, innerFar;
    bool innerHit = FulumaRayCappedConeOfPair(coneRay, cap, light.range, cosInner, insideInner, innerNear, innerFar);
    innerHit = FulumaNestInnerSegment(innerHit, outerNear, outerFar, innerNear, innerFar);

#if defined(_FULUMA_CLIP_BOX_EXCLUDE)
    float scattering = FulumaSpotLightScatteringThreePieces(rayToLight, rayDirection, coneRay, light, firstPiece,
        secondPiece, thirdPiece, innerNear, innerFar, innerHit);
#elif defined(FULUMA_CLIP_BOX_VARYINGS)
    float scattering = FulumaSpotLightScatteringTwoPieces(rayToLight, rayDirection, coneRay, light, firstPiece,
        secondPiece, innerNear, innerFar, innerHit);
#elif defined(_FULUMA_CLIP_BOXES)
    float scattering = FulumaSpotLightScatteringClipped(rayToLight, rayDirection, coneRay, light, clip, outerNear,
        clippedFar, innerNear, innerFar, innerHit, (uint)_FulumaClipPieceLimit);
#else
    float scattering = FulumaSpotLightScattering(rayToLight, rayDirection, coneRay, tScene, light,
        outerNear, outerFar, innerNear, innerFar, innerHit);
#endif

#if defined(_FULUMA_NEAR_FADE)
    // The proxy fragment's eye depth (the view vector along the camera's forward axis, perspective and orthographic
    // alike): 0 where the near plane cuts the proxy, 1 from the fade distance on, so the cut leaves no hard edge.
    float proxyDepth = dot(input.viewVectorWS, GetViewForwardDir());
    scattering *= saturate((proxyDepth - _ProjectionParams.y) / _FulumaNearFadeDistance);
#endif

    return half4(light.radiance * scattering, 0.0);
}

#endif // FULUMA_SPOT_LIGHT_FORWARD_PASS_INCLUDED
