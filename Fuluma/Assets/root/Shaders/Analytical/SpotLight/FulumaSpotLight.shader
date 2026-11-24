// Analytical (fake) volumetric spot light, drawn by FulumaSpotLightRenderPass: per light pool, one instanced draw per
// pass (inside / outside, see below) and proxy LOD, one instance per light, over the proxy mesh from
// SpotLightProxyMeshBuilder.cs (one submesh per LOD; inside-pass lights always use LOD0).
//
// Layout follows URP's Lit shader:
//   FulumaSpotLightInput.hlsl        GPU data, buffers, material constants, scene depth access
//   FulumaSpotLightScattering.hlsl   in-scattering: inverse-square falloff, Gauss-Legendre quadrature per segment
//   FulumaSpotLightForwardPass.hlsl  vertex/fragment programs
//   ../ShaderLibrary/Intersections.hlsl  ray vs. sphere-capped cone (shared by analytical lights)
//   ../ShaderLibrary/ClipBoxes.hlsl      clip box intervals along the ray (clip box variants only)
Shader "Hidden/Fuluma/Analytical/SpotLight"
{
    Properties
    {
        _Intensity("Intensity", Float) = 1.0
        _FulumaNearFadeDistance("Near Fade Distance", Float) = 0.5
        // Depth test of the outside pass: LessEqual, or Always when the feature doesn't use scene depth (RenderGraph can
        // merge the pass into URP's opaque render pass, whose depth then stays bound).
        [Enum(UnityEngine.Rendering.CompareFunction)] _FulumaOutsideZTest("Outside Pass Depth Test", Float) = 4
        _FulumaClipPieceLimit("Clip Box Piece Limit", Float) = 3
        _FulumaPhase("Phase Constants", Vector) = (1, 1, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }

        // Every light is drawn by one of two passes. CPU culling (SpotLightCullingJob) picks it per light and camera, and
        // the render pass draws each pass over its own list of visible lights.

        // Pass 0: the camera may be inside or touching the light's proxy. Back faces only, no depth test: exactly one
        // fragment per covered pixel even with the camera inside the volume. Occlusion comes from the scene depth in
        // the fragment program.
        Pass
        {
            Name "FulumaSpotLightInside"
            Tags { "LightMode" = "FulumaSpotLight" }

            Blend One One
            ZWrite Off
            ZTest Always
            Cull Front

            HLSLPROGRAM
            // Structured buffers in the vertex stage.
            #pragma target 4.5

            #pragma vertex FulumaSpotLightVertex
            #pragma fragment FulumaSpotLightFragment

            // Scene depth source, set per camera by the pass: depth input attachment (single- or multisampled),
            // _CameraDepthTexture where depth can't be read as an input attachment, or the pass's low-resolution depth
            // when the lights draw into a downsampled buffer, or none (_DEPTH_NONE: the feature's "Use Scene Depth" off).
            // Local on purpose: URP's global keywords of the same name don't leak in.
            #pragma multi_compile_local _ _DEPTH_AS_INPUT_ATTACHMENT _DEPTH_AS_INPUT_ATTACHMENT_MSAA _DEPTH_LOW_RES _DEPTH_NONE

            // Fades the glow where this pass's proxy fragments approach the near plane, which cuts them off with a hard
            // edge when the camera is near the light (feature toggle; only this pass, since only its lights can be
            // clipped by the near plane).
            #pragma multi_compile_local _ _FULUMA_NEAR_FADE

            // Lights limited by clip boxes (feature setting; only while the store uses boxes). Local, like the others.
            #pragma multi_compile_local _ _FULUMA_CLIP_BOX_SINGLE _FULUMA_CLIP_BOX_PAIR _FULUMA_CLIP_BOX_UNION _FULUMA_CLIP_BOX_EXCLUDE _FULUMA_CLIP_BOXES

            // Cancellation-free cone intersections for wide lights (feature toggle "Wide Cone Precision"): a few more
            // instructions per pixel, exact near the axis of cones wider than ~160 degrees.
            #pragma multi_compile_local _ _FULUMA_WIDE_CONES

            // Light model (feature settings "Scattering"): phase function, distance falloff, lamp glare.
            #pragma multi_compile_local _ _FULUMA_PHASE_HG
            #pragma multi_compile_local _ _FULUMA_FALLOFF_UNITY
            #pragma multi_compile_local _ _FULUMA_POINT_LAMP

            #include "FulumaSpotLightInput.hlsl"
            #include "FulumaSpotLightForwardPass.hlsl"
            ENDHLSL
        }

        // Pass 1: the camera is clearly outside the proxy. Front faces with a depth test against the scene: still one
        // fragment per covered pixel, and pixels whose proxy surface is behind geometry are rejected before the
        // fragment program runs (the volume lies behind its front faces, so nothing visible is lost).
        Pass
        {
            Name "FulumaSpotLightOutside"
            Tags { "LightMode" = "FulumaSpotLight" }

            Blend One One
            ZWrite Off
            ZTest [_FulumaOutsideZTest]
            Cull Back

            HLSLPROGRAM
            // Structured buffers in the vertex stage.
            #pragma target 4.5

            #pragma vertex FulumaSpotLightVertex
            #pragma fragment FulumaSpotLightFragment

            // Scene depth source, set per camera by the pass: depth input attachment (single- or multisampled),
            // _CameraDepthTexture where depth can't be read as an input attachment, or the pass's low-resolution depth
            // when the lights draw into a downsampled buffer, or none (_DEPTH_NONE: the feature's "Use Scene Depth" off).
            // Local on purpose: URP's global keywords of the same name don't leak in.
            #pragma multi_compile_local _ _DEPTH_AS_INPUT_ATTACHMENT _DEPTH_AS_INPUT_ATTACHMENT_MSAA _DEPTH_LOW_RES _DEPTH_NONE

            // Lights limited by clip boxes (feature setting; only while the store uses boxes). Local, like the others.
            #pragma multi_compile_local _ _FULUMA_CLIP_BOX_SINGLE _FULUMA_CLIP_BOX_PAIR _FULUMA_CLIP_BOX_UNION _FULUMA_CLIP_BOX_EXCLUDE _FULUMA_CLIP_BOXES

            // Cancellation-free cone intersections for wide lights (feature toggle "Wide Cone Precision"): a few more
            // instructions per pixel, exact near the axis of cones wider than ~160 degrees.
            #pragma multi_compile_local _ _FULUMA_WIDE_CONES

            // Light model (feature settings "Scattering"): phase function, distance falloff, lamp glare.
            #pragma multi_compile_local _ _FULUMA_PHASE_HG
            #pragma multi_compile_local _ _FULUMA_FALLOFF_UNITY
            #pragma multi_compile_local _ _FULUMA_POINT_LAMP

            #include "FulumaSpotLightInput.hlsl"
            #include "FulumaSpotLightForwardPass.hlsl"
            ENDHLSL
        }
    }

    Fallback Off
}
