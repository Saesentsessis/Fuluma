using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

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
#if UNITY_6000_5_OR_NEWER
	// The device's capabilities, queried once: they don't change while the process runs.
	[NoAutoStaticsCleanup]
#endif
	internal static class UrpDepthPrepass
	{
		/// <summary>
		/// What URP's rules ask of the device (<see cref="QueryDevice"/>); <see cref="DeviceFlags.None"/> until queried.
		/// </summary>
		private static DeviceFlags s_deviceFlags;

#if !UNITY_6000_3_OR_NEWER || (URP_COMPATIBILITY_MODE && !UNITY_6000_4_OR_NEWER)
		/// <summary>
		/// Compatibility Mode (URP 14, and URP 17's <c>UniversalRenderer.Setup</c>): where the depth a full depth prepass
		/// rendered is when Fuluma's early pass runs (<c>AfterRenderingPrePasses</c>), re-evaluated from public settings:
		/// <list type="bullet">
		/// <item>the depth texture is needed (by the camera or any pass, Fuluma's included) and URP can't copy depth, or
		/// the renderer's Depth Texture Mode is "Force Prepass": the prepass renders into <c>_CameraDepthTexture</c>;</item>
		/// <item>depth priming (Forced, or Auto off mobile; forward, first camera to write depth, not a reflection or
		/// depth-only camera; URP 17: no MSAA): the prepass renders into the camera's depth attachment, and URP copies it
		/// into <c>_CameraDepthTexture</c> only after Fuluma's pass, at the same event.</item>
		/// </list>
		/// Prepasses this can't see (the Scene view, gizmos, other features' normals) are answered "none": the buffer is then
		/// drawn after the opaques, which is always correct. Unlike <see cref="Renders"/>, the answer can be
		/// <see cref="CompatibilityDepth.Unusable"/>: a prepass whose depth Fuluma can't read in time.
		/// </summary>
		/// <param name="passNeedsDepthTexture">Whether Fuluma's own passes ask URP for the depth texture.</param>
		public static CompatibilityDepth FindCompatibilityDepth(UniversalRendererData data, in CameraState camera,
			bool passNeedsDepthTexture)
		{
			if (data == null || camera.UniversalRenderer == false || camera.IsSceneView)
				return CompatibilityDepth.None;

			// Deferred: the prepass only exists for normals there. Not handled.
			if (data.renderingMode == RenderingMode.Deferred || IsDeferredPlus(data))
				return CompatibilityDepth.None;

			// URP 14 and 17.0 build the prepass from the opaque layers; later versions may use the prepass layers. Either
			// missing the camera answers "none".
			if ((data.opaqueLayerMask & camera.CullingMask) == 0 || PrepassLayersMiss(data, camera))
				return CompatibilityDepth.None;

			var canCopyDepth = CanCopyDepth(camera.MsaaSamples);
			if (UsesDepthPrimingCompatibility(data, camera, canCopyDepth))
			{
				// URP 14 primes MSAA cameras too: their attachment can't be sampled, and the depth texture isn't copied yet.
				return camera.MsaaSamples > 1 ? CompatibilityDepth.Unusable : CompatibilityDepth.Attachment;
			}

			var needsDepth = camera.RequiresDepthTexture || passNeedsDepthTexture;
			var prepass = needsDepth && (canCopyDepth == false || data.copyDepthMode == CopyDepthMode.ForcePrepass);
			return prepass ? CompatibilityDepth.Texture : CompatibilityDepth.None;
		}
#endif


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

			var firstToWriteDepth = camera.RenderType == CameraRenderType.Base || camera.ClearDepth;
			var offscreenDepth = camera.TargetTexture != null && camera.TargetTexture.format == RenderTextureFormat.Depth;

			return IsWebGl() == false && firstToWriteDepth && offscreenDepth == false && camera.MsaaSamples == 1;
		}

#if !UNITY_6000_3_OR_NEWER || (URP_COMPATIBILITY_MODE && !UNITY_6000_4_OR_NEWER)
		private static bool UsesDepthPrimingCompatibility(UniversalRendererData data, in CameraState camera, bool canCopyDepth)
		{
#if UNITY_ANDROID || UNITY_IOS || UNITY_TVOS || UNITY_EMBEDDED_LINUX
			const bool recommended = false;
#else
			const bool recommended = true;
#endif
			var requested = (recommended && data.depthPrimingMode == DepthPrimingMode.Auto)
			                || data.depthPrimingMode == DepthPrimingMode.Forced;
			if (requested == false || canCopyDepth == false)
				return false;

			var firstToWriteDepth = camera.RenderType == CameraRenderType.Base || camera.ClearDepth;
			var offscreenDepth = camera.TargetTexture != null && camera.TargetTexture.format == RenderTextureFormat.Depth;
#if UNITY_6000_0_OR_NEWER
			// URP 17 doesn't prime MSAA cameras.
			if (camera.MsaaSamples > 1)
				return false;
#endif
			return IsWebGl() == false && firstToWriteDepth && offscreenDepth == false && camera.IsReflection == false;
		}

		private static bool IsDeferredPlus(UniversalRendererData data)
#if UNITY_6000_1_OR_NEWER
			=> data.renderingMode == RenderingMode.DeferredPlus;
#else
			=> false;
#endif

		private static bool PrepassLayersMiss(UniversalRendererData data, in CameraState camera)
#if UNITY_6000_2_OR_NEWER
			=> (data.prepassLayerMask & camera.CullingMask) == 0;
#else
			=> false;
#endif
#endif

		/// <summary>
		/// Whether URP can copy the camera's depth (URP's <c>CanCopyDepth</c>): single-sampled, the depth format or texture
		/// copies; with MSAA, a depth resolve (not on OpenGL ES 3).
		/// </summary>
		private static bool CanCopyDepth(int msaaSamples)
		{
			if (s_deviceFlags == DeviceFlags.None)
				s_deviceFlags = QueryDevice();

			return (s_deviceFlags & (msaaSamples > 1 ? DeviceFlags.CopiesMsaaDepth : DeviceFlags.CopiesDepth)) != 0;
		}

		/// <summary>
		/// Whether this device runs WebGL or WebGPU, where URP never primes depth.
		/// </summary>
		private static bool IsWebGl()
		{
			if (s_deviceFlags == DeviceFlags.None)
				s_deviceFlags = QueryDevice();

			return (s_deviceFlags & DeviceFlags.WebGl) != 0;
		}

		private static DeviceFlags QueryDevice()
		{
			var flags = DeviceFlags.Queried;
			if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.Depth) || SystemInfo.copyTextureSupport != CopyTextureSupport.None)
				flags |= DeviceFlags.CopiesDepth;

			if (SystemInfo.supportsMultisampledTextures != 0 && SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES3)
				flags |= DeviceFlags.CopiesMsaaDepth;

			var webGl = Application.platform == RuntimePlatform.WebGLPlayer;
#if UNITY_6000_0_OR_NEWER
			webGl |= SystemInfo.graphicsDeviceType == GraphicsDeviceType.WebGPU;
#endif
			if (webGl)
				flags |= DeviceFlags.WebGl;

			return flags;
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
				        | (clearDepth ? Flags.ClearDepth : Flags.None)
				        | (camera.cameraType == CameraType.Reflection ? Flags.Reflection : Flags.None);
				MsaaSamples = (byte)math.clamp(msaaSamples, 1, byte.MaxValue);
			}

			/// <summary>
			/// The camera renders with URP's Universal Renderer (not the 2D one).
			/// </summary>
			public bool UniversalRenderer => (State & Flags.UniversalRenderer) != 0;

			public bool IsSceneView => (State & Flags.SceneView) != 0;

			public bool RequiresDepthTexture => (State & Flags.RequiresDepthTexture) != 0;

			public bool ClearDepth => (State & Flags.ClearDepth) != 0;

			public bool IsReflection => (State & Flags.Reflection) != 0;

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
				ClearDepth = 1 << 3,
				Reflection = 1 << 4
			}
		}

		[System.Flags]
		private enum DeviceFlags : byte
		{
			None = 0,
			Queried = 1 << 0,
			CopiesDepth = 1 << 1,
			CopiesMsaaDepth = 1 << 2,
			WebGl = 1 << 3
		}

#if !UNITY_6000_3_OR_NEWER || (URP_COMPATIBILITY_MODE && !UNITY_6000_4_OR_NEWER)
		/// <summary>
		/// Where Fuluma's early pass finds a depth prepass's depth in Compatibility Mode
		/// (<see cref="FindCompatibilityDepth"/>).
		/// </summary>
		public enum CompatibilityDepth : byte
		{
			/// <summary>
			/// URP renders no full depth prepass the settings show.
			/// </summary>
			None,

			/// <summary>
			/// In <c>_CameraDepthTexture</c>.
			/// </summary>
			Texture,

			/// <summary>
			/// In the camera's depth attachment (depth priming).
			/// </summary>
			Attachment,

			/// <summary>
			/// A prepass runs, but its depth can't be read in time (depth priming with MSAA on URP 14).
			/// </summary>
			Unusable
		}
#endif
	}
}
