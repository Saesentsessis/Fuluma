using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Saesentsessis.Fuluma.Rendering.URP
{
	/// <summary>
	/// Whether URP's settings make it render a full depth prepass for a camera, and where its depth lands, for
	/// <see cref="LightBufferTiming.Auto"/>. URP decides this inside <c>UniversalRenderer.RecordRenderGraph</c> and keeps
	/// it in locals; this re-evaluates the parts of that rule (URP 17.7: <c>RequirePrepassForTextures</c>,
	/// <c>IsDepthPrimingEnabledRenderGraph</c>, <c>AddRequirementsOfInternalFeatures</c>) that public settings decide:
	/// <list type="bullet">
	/// <item>the camera needs the depth texture and the renderer's Depth Texture Mode is "Force Prepass";</item>
	/// <item>depth priming (Forced, or Auto off mobile; no MSAA, base camera).</item>
	/// </list>
	/// Prepasses other features cause (passes needing normals, or depth before the opaques) aren't visible from public API,
	/// so they aren't detected (<see cref="LightBufferTiming.BeforeOpaques"/> is the setting for those projects). Cases this
	/// can't judge (deferred rendering, the Scene view) answer "no prepass": the buffer is then drawn after the opaques,
	/// which is always correct.
	/// </summary>
	internal static class UrpDepthPrepass
	{
		/// <summary>
		/// True when the settings make URP render a full depth prepass for this camera; <paramref name="depth"/> is then
		/// where the prepass depth is after <c>AfterRenderingPrePasses</c>: the camera's depth attachment with depth
		/// priming, else <c>_CameraDepthTexture</c>.
		/// </summary>
		/// <param name="passNeedsDepthTexture">Whether the caller's own pass asks URP for the depth texture (after the
		/// opaques; it decides whether URP may copy depth, which depth priming needs).</param>
		/// <param name="inDepthAttachment">Whether <paramref name="depth"/> is the camera's depth attachment (primed).</param>
		public static bool TryGetPrepassDepth(UniversalRendererData data, UniversalCameraData cameraData,
			UniversalResourceData resources, bool passNeedsDepthTexture, out TextureHandle depth, out bool inDepthAttachment)
		{
			depth = TextureHandle.nullHandle;
			inDepthAttachment = false;

			if (data == null || cameraData.renderer is not UniversalRenderer || cameraData.isSceneViewCamera)
				return false;

			// Deferred: the prepass may be partial and renders into the attachment, GBuffer rules apply. Not handled.
			if (data.renderingMode is RenderingMode.Deferred or RenderingMode.DeferredPlus)
				return false;

			// URP skips the prepass when no prepass layer is visible to the camera.
			if ((data.prepassLayerMask & cameraData.camera.cullingMask) == 0)
				return false;

			var needsDepth = cameraData.requiresDepthTexture || passNeedsDepthTexture;
			var canCopyDepth = CanCopyDepth(cameraData);
			var forcedPrepass = cameraData.requiresDepthTexture && data.copyDepthMode == CopyDepthMode.ForcePrepass;
			var prepassForTexture = forcedPrepass || (needsDepth && canCopyDepth == false);
			var priming = UsesDepthPriming(data, cameraData, needsDepth, canCopyDepth);

			if (prepassForTexture == false && priming == false)
				return false;

			inDepthAttachment = priming;
			depth = priming ? resources.activeDepthTexture : resources.cameraDepthTexture;
			return depth.IsValid();
		}

		private static bool UsesDepthPriming(UniversalRendererData data, UniversalCameraData cameraData, bool needsDepth,
			bool canCopyDepth)
		{
#if UNITY_ANDROID || UNITY_IOS || UNITY_TVOS || UNITY_EMBEDDED_LINUX
			const bool recommended = false;
#else
			const bool recommended = true;
#endif
			var requested = (recommended && data.depthPrimingMode == DepthPrimingMode.Auto)
			                || data.depthPrimingMode == DepthPrimingMode.Forced;
			if (requested == false)
				return false;

			if (needsDepth && canCopyDepth == false)
				return false;

			var webGl = Application.platform == RuntimePlatform.WebGLPlayer
			            || SystemInfo.graphicsDeviceType == GraphicsDeviceType.WebGPU;
			var firstToWriteDepth = cameraData.renderType == CameraRenderType.Base || cameraData.clearDepth;
			var offscreenDepth = cameraData.targetTexture != null && cameraData.targetTexture.format == RenderTextureFormat.Depth;

			return webGl == false && firstToWriteDepth && offscreenDepth == false
			       && cameraData.cameraTargetDescriptor.msaaSamples == 1;
		}

		private static bool CanCopyDepth(UniversalCameraData cameraData)
		{
			var msaa = cameraData.cameraTargetDescriptor.msaaSamples > 1;
			var supportsDepthCopy = msaa == false
			                        && (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.Depth)
			                            || SystemInfo.copyTextureSupport != CopyTextureSupport.None);
			var msaaDepthResolve = msaa && SystemInfo.supportsMultisampledTextures != 0;

			if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3 && msaaDepthResolve)
				return false;

			return supportsDepthCopy || msaaDepthResolve;
		}
	}
}
