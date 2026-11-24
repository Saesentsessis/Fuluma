// Fullscreen passes around a downsampled fake light buffer, drawn by FulumaSpotLightRenderPass when its light buffer scale
// is below 1 (Blitter's fullscreen triangle; _BlitTexture is the pass's source). Any scale works: every pass maps pixels
// through the actual size ratio, which needn't be a whole number.
//   0  FulumaDownsampleDepth        camera depth -> low-resolution depth: a depth buffer (raw depth, for early-Z) and an
//                                   R32F copy holding linear eye depth (what the light shader and the depth-aware composite
//                                   use, so neither converts it). Farthest depth of each block, or, with
//                                   _FULUMA_CHECKERBOARD, farthest and nearest alternating like a checkerboard (for the
//                                   depth-aware composite).
//   1  FulumaComposite              low-resolution light buffer -> camera color, bilinear, additive
//   2  FulumaCompositeDepthAware    the same, weighting the 4 nearest low-resolution texels by bilinear weight x depth
//                                   similarity to the camera pixel (joint bilateral upsampling), nearest depth if none fits
// Gathers (2x2 texels per fetch) keep both depth passes cheap on mobile GPUs, where the per-texel loads dominated
// (measured on a Mali-G52, see docs/dev/PixelShaderIdeas.md); they need shader model 4.5, like the light shader.
Shader "Hidden/Fuluma/LightBuffer"
{
    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    // Eye depth of a raw depth: perspective through _ZBufferParams; orthographic depth is linear between the clip planes.
    float FulumaEyeDepth(float rawDepth)
    {
        UNITY_BRANCH
        if (unity_OrthoParams.w > 0.5)
        {
            float depth01 = rawDepth;
        #if UNITY_REVERSED_Z
            depth01 = 1.0 - depth01;
        #endif
            return lerp(_ProjectionParams.y, _ProjectionParams.z, depth01);
        }

        return LinearEyeDepth(rawDepth, _ZBufferParams);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "FulumaDownsampleDepth"

            ZTest Always
            ZWrite On
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment DownsampleDepthFragment
            #pragma multi_compile_local _ _FULUMA_CHECKERBOARD

            // The source is a depth texture: keep full precision.
            #define USE_FULL_PRECISION_BLIT_TEXTURE
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // xy: source (full-resolution) size in pixels, zw: target (low-resolution) size in pixels.
            float4 _FulumaDownsampleSizes;

            // Most source pixels read per axis. Below a 1/16 scale a target pixel covers more than this, and the reads
            // spread evenly over its block (16x16 = 256 texels, in 64 gathers; at a 1% scale a pixel would otherwise
            // read ~10000, in very few threads). Details thinner than the spacing can then be missed.
            #define FULUMA_MAX_DEPTH_SAMPLES 16

            struct DownsampleOutput
            {
                float eyeDepth : SV_Target0;
                float depth : SV_Depth;
            };

            // Start of the i-th of `count` 2-texel pairs spread from `first` to `last` (both pair texels inside).
            int FulumaPairStart(int i, int count, int first, int last)
            {
                return count > 1 ? first + int(i * float(last - 1 - first) / float(count - 1) + 0.5) : first;
            }

            DownsampleOutput DownsampleDepthFragment(Varyings input)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Source pixels this target pixel touches (source / target sizes needn't divide: blocks of a fractional
                // ratio overlap their neighbours by a pixel).
                int2 target = int2(floor(input.positionCS.xy));
                float2 ratio = _FulumaDownsampleSizes.xy / _FulumaDownsampleSizes.zw;
                int2 sourceSize = int2(_FulumaDownsampleSizes.xy);
                int2 first = int2(floor(target * ratio));
                int2 last = min(int2(ceil((target + 1.0) * ratio)), sourceSize) - 1;

                // Raw depth: larger is nearer with reversed Z.
                float nearMin = 1.0, nearMax = 0.0;

                UNITY_BRANCH
                if (all(last > first))
                {
                    // Blocks at least 2 wide (every scale up to 0.5): 2x2 texels per gather. Pairs stay inside the block
                    // (the last one may overlap its neighbour pair, which min / max don't mind).
                    int2 pairs = min((last - first + 2) / 2, FULUMA_MAX_DEPTH_SAMPLES / 2);
                    float2 texelSize = 1.0 / _FulumaDownsampleSizes.xy;

                    UNITY_LOOP
                    for (int j = 0; j < pairs.y; j++)
                    {
                        int y = FulumaPairStart(j, pairs.y, first.y, last.y);

                        UNITY_LOOP
                        for (int i = 0; i < pairs.x; i++)
                        {
                            int x = FulumaPairStart(i, pairs.x, first.x, last.x);

                            // The corner shared by texels (x, y) and (x + 1, y + 1).
                            float4 rawDepths = GATHER_RED_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, (float2(x, y) + 1.0) * texelSize);
                            nearMin = min(nearMin, min(min(rawDepths.x, rawDepths.y), min(rawDepths.z, rawDepths.w)));
                            nearMax = max(nearMax, max(max(rawDepths.x, rawDepths.y), max(rawDepths.z, rawDepths.w)));
                        }
                    }
                }
                else
                {
                    // Blocks 1 texel wide on an axis (scales above 0.5): every texel, at most 2x2.
                    UNITY_LOOP
                    for (int y = first.y; y <= last.y; y++)
                    {
                        UNITY_LOOP
                        for (int x = first.x; x <= last.x; x++)
                        {
                            float rawDepth = LOAD_TEXTURE2D_X(_BlitTexture, int2(x, y)).r;
                            nearMin = min(nearMin, rawDepth);
                            nearMax = max(nearMax, rawDepth);
                        }
                    }
                }

            #if UNITY_REVERSED_Z
                float farthest = nearMin, nearest = nearMax;
            #else
                float farthest = nearMax, nearest = nearMin;
            #endif

            #if defined(_FULUMA_CHECKERBOARD)
                // Every 2x2 neighbourhood then has texels shaded up to the near and up to the far surface of a block.
                float rawDepthOut = ((target.x + target.y) & 1) == 0 ? farthest : nearest;
            #else
                float rawDepthOut = farthest;
            #endif

                DownsampleOutput output;
                output.eyeDepth = FulumaEyeDepth(rawDepthOut);
                output.depth = rawDepthOut;
                return output;
            }
            ENDHLSL
        }

        Pass
        {
            Name "FulumaComposite"

            ZTest Always
            ZWrite Off
            Cull Off
            Blend One One

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment CompositeFragment

            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            half4 CompositeFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                real3 light = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, input.texcoord, 0).rgb;
                return half4(light, 0.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "FulumaCompositeDepthAware"

            ZTest Always
            ZWrite Off
            Cull Off
            Blend One One

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment DepthAwareCompositeFragment

            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // Linear eye depth per low-resolution texel (written by the downsample).
            TEXTURE2D_X_FLOAT(_FulumaLowResDepth);
            TEXTURE2D_X_FLOAT(_FulumaCameraDepth);

            // xy: low-resolution size in pixels, zw: unused.
            float4 _FulumaLowResSize;

            // Weight falloff with the relative depth difference: 2^-(difference * sharpness), so 1/32 (3%) halves a texel's
            // weight and 25% makes it ~0.4%.
            #define FULUMA_DEPTH_SHARPNESS 32.0

            // Largest relative depth difference of the four texels at which plain bilinear filtering is used (a depth weight
            // of 2^-0.5 = 0.71 or more for every texel).
            #define FULUMA_FLAT_DIFFERENCE (1.0 / 64.0)

            // Below this total weight no texel is at the pixel's depth: take the nearest in depth.
            #define FULUMA_MIN_WEIGHT 1e-3

            half4 DepthAwareCompositeFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float depth = FulumaEyeDepth(LOAD_TEXTURE2D_X(_FulumaCameraDepth, uint2(input.positionCS.xy)).r);

                // The 2x2 low-resolution texels around this pixel, and their bilinear weights.
                float2 texel = input.texcoord * _FulumaLowResSize.xy - 0.5;
                float2 corner = floor(texel);
                float2 f = texel - corner;
                int2 base = int2(corner);
                int2 maxCoord = int2(_FulumaLowResSize.xy) - 1;

                // Gather order: x (0, 1), y (1, 1), z (1, 0), w (0, 0) in texel offsets from `base`; the clamp sampler
                // clamps at the borders like the loads below.
                float4 texelDepths = GATHER_RED_TEXTURE2D_X(_FulumaLowResDepth, sampler_PointClamp,
                    (corner + 1.0) / _FulumaLowResSize.xy);

                // Depths and their difference in full precision; the weights and colours after that in `real` (half on
                // mobile): relative differences, weights in [0, 1] and light values all fit it.
                real4 difference = real4(abs(texelDepths - depth) / max(depth, 1e-4));

                // All four texels on this pixel's surface (the common case away from depth edges): their depth weights
                // would all be close to 1, so one hardware-filtered read gives (nearly) the same result for far less
                // work. Only pixels at depth edges take the weighted path below.
                UNITY_BRANCH
                if (max(max(difference.x, difference.y), max(difference.z, difference.w)) < FULUMA_FLAT_DIFFERENCE)
                    return half4(SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, input.texcoord, 0).rgb, 0.0);

                real2 g = real2(f);
                real4 bilinear = real4((1.0 - g.x) * g.y, g.x * g.y, g.x * (1.0 - g.y), (1.0 - g.x) * (1.0 - g.y));
                real4 weight = bilinear * exp2(-difference * FULUMA_DEPTH_SHARPNESS);

                real3 light01 = real3(LOAD_TEXTURE2D_X(_BlitTexture, clamp(base + int2(0, 1), 0, maxCoord)).rgb);
                real3 light11 = real3(LOAD_TEXTURE2D_X(_BlitTexture, clamp(base + int2(1, 1), 0, maxCoord)).rgb);
                real3 light10 = real3(LOAD_TEXTURE2D_X(_BlitTexture, clamp(base + int2(1, 0), 0, maxCoord)).rgb);
                real3 light00 = real3(LOAD_TEXTURE2D_X(_BlitTexture, clamp(base, 0, maxCoord)).rgb);

                real weightSum = dot(weight, 1.0);
                if (weightSum > FULUMA_MIN_WEIGHT)
                    return half4((weight.x * light01 + weight.y * light11 + weight.z * light10 + weight.w * light00) / weightSum, 0.0);

                // No texel at this depth: the nearest in depth (ties in the order (0, 0), (1, 0), (0, 1), (1, 1)).
                real nearest = min(min(difference.x, difference.y), min(difference.z, difference.w));
                real3 light = difference.w == nearest ? light00
                    : difference.z == nearest ? light10
                    : difference.x == nearest ? light01
                    : light11;
                return half4(light, 0.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
