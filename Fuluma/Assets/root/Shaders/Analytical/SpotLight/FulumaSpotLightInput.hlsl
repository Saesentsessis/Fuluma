#ifndef FULUMA_SPOT_LIGHT_INPUT_INCLUDED
#define FULUMA_SPOT_LIGHT_INPUT_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
// SRGBToLinear: the light color is converted in linear-space projects.
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

// Largest MSAA sample count the nearest-sample depth loop handles.
#define FULUMA_MAX_MSAA_SAMPLES 8

// ---------------------------------------------------------------------------------------------------------------------
// GPU data. Layouts must match Runtime/Data/PackedTransformData.cs and Runtime/Data/PackedSpotLightData.cs (16 B each).

struct FulumaPackedTransformData
{
    float3 position;
    uint packedDirection;   // polar rotation: azimuth (high 16 bits) and elevation (low 16 bits), unorm16 each
};

struct FulumaPackedSpotLightData
{
    uint packedColor;       // RGBA8, R in the lowest byte; A is an intensity multiplier
    float range;
    float cosOuter;         // cosine of the outer half-angle
    uint packedHalves;      // low 16 bits: half angleScale, high 16 bits: half intensity
};

// One pair per light pool (static or dynamic), bound on that pool's materials once per camera.
StructuredBuffer<FulumaPackedTransformData> _FulumaSpotTransforms;
StructuredBuffer<FulumaPackedSpotLightData> _FulumaSpotLights;

// Visible lights of this draw, from CPU culling (SpotLightCuller): dense indices into the two buffers above. Each draw
// reads its own list, starting at _FulumaVisibleOffset.
StructuredBuffer<uint> _FulumaVisibleSpotLights;
uint _FulumaVisibleOffset;

// MSAA sample count of the depth input attachment (only read by the _DEPTH_AS_INPUT_ATTACHMENT_MSAA variant).
int _FulumaMsaaSampleCount;

// Shape of the proxy LOD this draw uses, set per draw by the pass (SpotLightProxyShape.cs). x: cos(pi / rim segments),
// y: largest cap face angle per radian of the widened half-angle.
float4 _FulumaSpotProxyShape;

CBUFFER_START(UnityPerMaterial)
    float _Intensity;
    float _FulumaNearFadeDistance;  // _FULUMA_NEAR_FADE: eye-depth distance past the near plane over which the glow fades in
    float _FulumaClipPieceLimit;    // _FULUMA_CLIP_BOXES: most pieces of a ray integrated (3 to 5), the rest merge
    float4 _FulumaPhase;            // _FULUMA_PHASE_HG: (a, b, m) of the phase function, see FulumaSpotPhase
CBUFFER_END

// Clip box variants, one per clip class (FulumaSpotLightRenderPass draws each class with its own): _FULUMA_CLIP_BOX_SINGLE
// for lights with one box, _FULUMA_CLIP_BOX_PAIR for one keep and one subtract box, _FULUMA_CLIP_BOX_UNION for two keep
// boxes, _FULUMA_CLIP_BOX_EXCLUDE for two subtract boxes, _FULUMA_CLIP_BOXES for the rest (three or four boxes; or any
// lights, when culling didn't sort them).
#if defined(_FULUMA_CLIP_BOX_SINGLE) || defined(_FULUMA_CLIP_BOX_PAIR) || defined(_FULUMA_CLIP_BOX_UNION) \
    || defined(_FULUMA_CLIP_BOX_EXCLUDE) || defined(_FULUMA_CLIP_BOXES)
#define FULUMA_CLIPPING
#endif

// Every clip variant transforms its boxes in the vertex stage (FulumaSpotLightForwardPass.hlsl): the pixel gets each
// box's ray in box space through varyings and does no buffer loads or matrix math. The general variant (up to four
// boxes) packs its four boxes' terms into float4s (FULUMA_CLIP_BOX_PACKED_VARYINGS) to stay well inside the 15 fragment
// input rows GLES 3.0 guarantees.
#if defined(_FULUMA_CLIP_BOX_SINGLE) || defined(_FULUMA_CLIP_BOX_PAIR) || defined(_FULUMA_CLIP_BOX_UNION) \
    || defined(_FULUMA_CLIP_BOX_EXCLUDE)
#define FULUMA_CLIP_BOX_VARYINGS
#endif
#if defined(_FULUMA_CLIP_BOX_PAIR) || defined(_FULUMA_CLIP_BOX_UNION) || defined(_FULUMA_CLIP_BOX_EXCLUDE)
#define FULUMA_CLIP_BOX_PAIR_VARYINGS
#endif
#if defined(_FULUMA_CLIP_BOXES)
#define FULUMA_CLIP_BOX_PACKED_VARYINGS
#endif

#if defined(FULUMA_CLIPPING)
#include "../ShaderLibrary/ClipBoxes.hlsl"

// The store's clip boxes, by box index, and this draw's pool's per-light references (index-aligned with
// _FulumaSpotLights; 0xFFFF slots for a light without boxes).
StructuredBuffer<FulumaPackedClipBox> _FulumaClipBoxes;
StructuredBuffer<uint2> _FulumaSpotClipBoxReferences;
#endif

// ---------------------------------------------------------------------------------------------------------------------
// Decoded light

struct FulumaSpotLight
{
    float3 position;
    float3 direction;   // cone axis, unit length
    float range;
    float cosOuter;
    float angleScale;   // 1 / (cosInner - cosOuter)
    float3 radiance;    // linear color * intensity * alpha * material intensity
};

// Inverse of QuaternionUtils.PackPolar / ToPolarRotation (Runtime/Utils/QuaternionUtils.cs):
// azimuth = atan2(x, z) in [-pi, pi], elevation = asin(y) in [-pi/2, pi/2].
float3 FulumaDecodePolarDirection(uint packed)
{
    float azimuth = ((packed >> 16) / 65535.0 * 2.0 - 1.0) * PI;
    float elevation = ((packed & 0xFFFF) / 65535.0 * 2.0 - 1.0) * HALF_PI;

    float sinAzimuth, cosAzimuth, sinElevation, cosElevation;
    sincos(azimuth, sinAzimuth, cosAzimuth);
    sincos(elevation, sinElevation, cosElevation);

    return float3(cosElevation * sinAzimuth, sinElevation, cosElevation * cosAzimuth);
}

float4 FulumaDecodeRGBA8(uint packed)
{
    return float4(packed & 0xFF, (packed >> 8) & 0xFF, (packed >> 16) & 0xFF, packed >> 24) / 255.0;
}

FulumaSpotLight LoadFulumaSpotLight(uint index)
{
    FulumaPackedTransformData transform = _FulumaSpotTransforms[index];
    FulumaPackedSpotLightData data = _FulumaSpotLights[index];

    float4 color = FulumaDecodeRGBA8(data.packedColor);
    float intensity = f16tof32(data.packedHalves >> 16);

    // Color32 is authored in gamma space, like Unity's light colors.
#if !defined(UNITY_COLORSPACE_GAMMA)
    color.rgb = SRGBToLinear(color.rgb);
#endif

    FulumaSpotLight light;
    light.position = transform.position;
    light.direction = FulumaDecodePolarDirection(transform.packedDirection);
    light.range = data.range;
    light.cosOuter = data.cosOuter;
    light.angleScale = f16tof32(data.packedHalves & 0xFFFF);
    light.radiance = color.rgb * (intensity * color.a * _Intensity);
    return light;
}

// ---------------------------------------------------------------------------------------------------------------------
// Scene depth: input attachment when the platform supports reading depth that way, else URP's _CameraDepthTexture.
// Drawn into a downsampled light buffer: the low-resolution depth the pass made (farthest depth of each block, or
// farthest and nearest alternating for the depth-aware composite), at this low-resolution pixel, as linear eye depth
// (not raw depth).

#if defined(_DEPTH_LOW_RES)
TEXTURE2D_X_FLOAT(_FulumaLowResDepth);
#endif

float LoadFulumaSceneDepth(float2 fragCoord)
{
#if defined(_DEPTH_LOW_RES)
    return LOAD_TEXTURE2D_X(_FulumaLowResDepth, uint2(fragCoord)).r;
#elif defined(_DEPTH_AS_INPUT_ATTACHMENT) && UNITY_VERSION >= 60060000
    // URP declares FetchSceneDepth from Unity 6000.6 (UNITY_VERSION is 60060000 there: 6000.3.0 is 60030000, measured).
    // Fuluma never enables the input attachment variants before that (no API to bind it), so older versions
    // compile them as the depth-texture path below.
    return FetchSceneDepth(fragCoord);
#elif defined(_DEPTH_AS_INPUT_ATTACHMENT_MSAA) && UNITY_VERSION >= 60060000
    // Nearest of all samples: the glow never reaches past geometry covering any part of the pixel.
    float nearest = FetchSceneDepth(fragCoord, 0);

    UNITY_UNROLL
    for (int i = 1; i < FULUMA_MAX_MSAA_SAMPLES; i++)
    {
        if (i >= _FulumaMsaaSampleCount)
            break;

        float sampleDepth = FetchSceneDepth(fragCoord, i);
    #if UNITY_REVERSED_Z
        nearest = max(nearest, sampleDepth);
    #else
        nearest = min(nearest, sampleDepth);
    #endif
    }

    return nearest;
#else
    return LoadSceneDepth(uint2(fragCoord));
#endif
}

#endif // FULUMA_SPOT_LIGHT_INPUT_INCLUDED
