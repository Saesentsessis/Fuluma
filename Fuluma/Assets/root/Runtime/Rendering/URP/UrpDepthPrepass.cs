using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
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
		/// True when the settings make URP render a full depth prepass for this camera. Its depth is then, after
		/// <c>AfterRenderingPrePasses</c>, in the camera's depth attachment with depth priming
		/// (<paramref name="inDepthAttachment"/>), else in <c>_CameraDepthTexture</c>.
		/// </summary>
		/// <param name="passNeedsDepthTexture">Whether the caller's own pass asks URP for the depth texture (after the
		/// opaques; it decides whether URP may copy depth, which depth priming needs).</param>
		/// <param name="inDepthAttachment">Whether the prepass depth is the camera's depth attachment (primed).</param>
		public static bool Renders(UniversalRendererData data, in CameraState camera, bool passNeedsDepthTexture,
			out bool inDepthAttachment)
		{
			inDepthAttachment = false;

			if (data == null || camera.UniversalRenderer == false || camera.IsSceneView)
				return false;

			// Deferred: the prepass may be partial and renders into the attachment, GBuffer rules apply. Not handled.
			// Deferred+ exists from Unity 6.1.
#if UNITY_6000_1_OR_NEWER
			if (data.renderingMode is RenderingMode.Deferred or RenderingMode.DeferredPlus)
				return false;
#else
			if (data.renderingMode == RenderingMode.Deferred)
				return false;
#endif

			// URP skips the prepass when no prepass layer is visible to the camera. Before Unity 6.2 (no prepass layer
			// mask) the prepass draws the opaque layers.
#if UNITY_6000_2_OR_NEWER
			if ((data.prepassLayerMask & camera.CullingMask) == 0)
				return false;
#else
			if ((data.opaqueLayerMask & camera.CullingMask) == 0)
				return false;
#endif

			var needsDepth = camera.RequiresDepthTexture || passNeedsDepthTexture;
			var canCopyDepth = CanCopyDepth(camera.MsaaSamples);
			var forcedPrepass = camera.RequiresDepthTexture && data.copyDepthMode == CopyDepthMode.ForcePrepass;
			var prepassForTexture = forcedPrepass || (needsDepth && canCopyDepth == false);
			var priming = UsesDepthPriming(data, camera, needsDepth, canCopyDepth);

			inDepthAttachment = priming;
			return prepassForTexture || priming;
		}

		private static bool UsesDepthPriming(UniversalRendererData data, in CameraState camera, bool needsDepth,
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

			var webGl = Application.platform == RuntimePlatform.WebGLPlayer;
#if UNITY_6000_0_OR_NEWER
			webGl |= SystemInfo.graphicsDeviceType == GraphicsDeviceType.WebGPU;
#endif
			var firstToWriteDepth = camera.RenderType == CameraRenderType.Base || camera.ClearDepth;
			var offscreenDepth = camera.TargetTexture != null && camera.TargetTexture.format == RenderTextureFormat.Depth;

			return webGl == false && firstToWriteDepth && offscreenDepth == false && camera.MsaaSamples == 1;
		}

		private static bool CanCopyDepth(int msaaSamples)
		{
			var msaa = msaaSamples > 1;
			var supportsDepthCopy = msaa == false
			                        && (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.Depth)
			                            || SystemInfo.copyTextureSupport != CopyTextureSupport.None);
			var msaaDepthResolve = msaa && SystemInfo.supportsMultisampledTextures != 0;

			if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3 && msaaDepthResolve)
				return false;

			return supportsDepthCopy || msaaDepthResolve;
		}

		/// <summary>
		/// What the rule reads from a camera, taken from whichever camera data the render path has. Fields are ordered
		/// by size, largest first.
		/// </summary>
		[StructLayout(LayoutKind.Sequential)]
		public readonly struct CameraState
		{
#if UNITY_6000_0_OR_NEWER
			public static CameraState From(UniversalCameraData cameraData)
				=> new(cameraData.renderer, cameraData.isSceneViewCamera, cameraData.camera, cameraData.requiresDepthTexture,
					cameraData.renderType, cameraData.clearDepth, cameraData.targetTexture,
					cameraData.cameraTargetDescriptor.msaaSamples);
#endif

#if !UNITY_6000_3_OR_NEWER || (URP_COMPATIBILITY_MODE && !UNITY_6000_4_OR_NEWER)
			public static CameraState From(in CameraData cameraData)
				=> new(cameraData.renderer, cameraData.isSceneViewCamera, cameraData.camera, cameraData.requiresDepthTexture,
					cameraData.renderType, cameraData.clearDepth, cameraData.targetTexture,
					cameraData.cameraTargetDescriptor.msaaSamples);
#endif

			public readonly RenderTexture TargetTexture;
			public readonly int CullingMask;
			public readonly CameraRenderType RenderType;
			public readonly Flags State;

			/// <summary>
			/// The camera target's MSAA sample count (1 to 8).
			/// </summary>
			public readonly byte MsaaSamples;

			private CameraState(ScriptableRenderer renderer, bool isSceneView, Camera camera, bool requiresDepthTexture,
				CameraRenderType renderType, bool clearDepth, RenderTexture targetTexture, int msaaSamples)
			{
				TargetTexture = targetTexture;
				CullingMask = camera.cullingMask;
				RenderType = renderType;
				// Qualified: inside this struct, UniversalRenderer names the property.
				State = (renderer is UnityEngine.Rendering.Universal.UniversalRenderer ? Flags.UniversalRenderer : Flags.None)
				        | (isSceneView ? Flags.SceneView : Flags.None)
				        | (requiresDepthTexture ? Flags.RequiresDepthTexture : Flags.None)
				        | (clearDepth ? Flags.ClearDepth : Flags.None);
				MsaaSamples = (byte)math.clamp(msaaSamples, 1, byte.MaxValue);
			}

			/// <summary>
			/// The camera renders with URP's Universal Renderer (not the 2D one).
			/// </summary>
			public bool UniversalRenderer => (State & Flags.UniversalRenderer) != 0;

			public bool IsSceneView => (State & Flags.SceneView) != 0;

			public bool RequiresDepthTexture => (State & Flags.RequiresDepthTexture) != 0;

			public bool ClearDepth => (State & Flags.ClearDepth) != 0;

			/// <summary>
			/// The camera's yes/no properties, packed into one byte.
			/// </summary>
			[System.Flags]
			public enum Flags : byte
			{
				None = 0,
				UniversalRenderer = 1 << 0,
				SceneView = 1 << 1,
				RequiresDepthTexture = 1 << 2,
				ClearDepth = 1 << 3
			}
		}
	}
}
