#if !UNITY_6000_3_OR_NEWER || (URP_COMPATIBILITY_MODE && !UNITY_6000_4_OR_NEWER)
#define FULUMA_COMPATIBILITY_PATH
#endif

#if FULUMA_COMPATIBILITY_PATH
using Saesentsessis.Fuluma.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// URP 17 (Unity 6.0-6.3) marks the Compatibility Mode members obsolete; URP 14 (2022.3) has no other path.
#pragma warning disable 618, 672

namespace Saesentsessis.Fuluma.Rendering.URP
{
	/// <summary>
	/// The light buffer's Compatibility path: persistent targets (one set, reallocated when the camera size changes) and
	/// command-buffer blits, run inside the spot light pass's <c>Execute</c>.
	/// </summary>
	/// <remarks>
	/// Scene depth is the camera's depth attachment where it can be sampled (single-sampled, an intermediate target), as
	/// on the RenderGraph path; otherwise the shader reads URP's <c>_CameraDepthTexture</c> itself
	/// (<c>_FULUMA_CAMERA_DEPTH_TEXTURE</c>): C# can't bind URP's depth texture on this path (referring to it by name
	/// binds nothing, measured on 2022.3). Textures are bound with <c>SetGlobalTexture</c>, never on the material: a switch
	/// between Compatibility Mode and RenderGraph recreates URP's pipeline and with it this buffer, so the RenderGraph
	/// path's material textures never reach this path.
	/// </remarks>
	internal sealed partial class DownsampledLightBuffer
	{
		private const string CameraDepthTextureKeywordName = "_FULUMA_CAMERA_DEPTH_TEXTURE";

		/// <summary>
		/// Samples the light buffer upside down: what a camera rendering straight to the backbuffer needs (see
		/// <see cref="ExecuteComposite"/>).
		/// </summary>
		private static readonly Vector4 FlippedScaleBias = new(1f, -1f, 0f, 1f);

		/// <summary>
		/// Whether <paramref name="depth"/> can be sampled as this camera's scene depth: a render texture (not the
		/// backbuffer's), single-sampled.
		/// </summary>
		private static bool IsSampleable(RTHandle depth) => depth != null && depth.rt != null && depth.rt.antiAliasing <= 1;

		/// <summary>
		/// A low-resolution target matching the camera target's dimension, slices, XR usage and dynamic resolution, single
		/// sampled, without mips or depth.
		/// </summary>
		private static RenderTextureDescriptor LowResDescriptor(in RenderTextureDescriptor camera, int width, int height)
			=> new(width, height)
			{
				dimension = camera.dimension,
				volumeDepth = camera.volumeDepth,
				vrUsage = camera.vrUsage,
				useDynamicScale = camera.useDynamicScale,
				msaaSamples = 1,
				useMipMap = false,
				autoGenerateMips = false,
				depthBufferBits = 0
			};

		private static void Reallocate(ref RTHandle handle, in RenderTextureDescriptor descriptor, FilterMode filterMode,
			string name)
		{
#if UNITY_6000_0_OR_NEWER
			RenderingUtils.ReAllocateHandleIfNeeded(ref handle, descriptor, filterMode, TextureWrapMode.Clamp, name: name);
#else
			RenderingUtils.ReAllocateIfNeeded(ref handle, descriptor, filterMode, TextureWrapMode.Clamp, name: name);
#endif
		}

		private RTHandle _lightTarget;
		private RTHandle _depthTarget;
		private RTHandle _rawDepthTarget;
		private Vector2Int _targetSize;
		private LocalKeyword _cameraDepthTextureKeyword;

		/// <summary>
		/// The current camera's depth attachment when the passes read it, else null (they read URP's depth texture).
		/// </summary>
		private RTHandle _sceneDepth;

		/// <summary>
		/// The low-resolution linear eye depth the light shader reads (<c>_FulumaLowResDepth</c>); null before
		/// <see cref="AllocateTargets"/> with depth.
		/// </summary>
		public Texture LowResDepth => _rawDepthTarget;

		/// <summary>
		/// Sizes this camera's targets (<c>ceil(camera size × scale)</c>), reallocating them only when that changed. Call
		/// before recording any command that uses them for this camera.
		/// </summary>
		/// <param name="withDepth">Also the depth targets (the lights read scene depth).</param>
		public void AllocateTargets(in RenderTextureDescriptor camera, bool withDepth)
		{
			var width = ScaledSize(camera.width, _scale);
			var height = ScaledSize(camera.height, _scale);
			_targetSize = new Vector2Int(width, height);

			var light = LowResDescriptor(camera, width, height);
			light.graphicsFormat = _lightFormat;
			Reallocate(ref _lightTarget, light, FilterMode.Bilinear, "_FulumaLightBuffer");

			if (withDepth == false)
				return;

			var rawDepth = LowResDescriptor(camera, width, height);
			rawDepth.graphicsFormat = _rawDepthFormat;
			Reallocate(ref _rawDepthTarget, rawDepth, FilterMode.Point, "_FulumaLowResDepth");

			var depth = LowResDescriptor(camera, width, height);
			depth.graphicsFormat = GraphicsFormat.None;
			depth.depthBufferBits = 32;
			Reallocate(ref _depthTarget, depth, FilterMode.Point, "_FulumaLowResDepthBuffer");
		}

		/// <summary>
		/// The camera's depth into the low-resolution depth buffer and its linear eye depth copy: from
		/// <paramref name="cameraDepth"/> (the camera's depth attachment) where it can be sampled, else from URP's
		/// <c>_CameraDepthTexture</c>; the depth-aware composite then reads the same. Every texel is written, so neither
		/// target loads its old contents.
		/// </summary>
		public void ExecuteDownsample(CommandBuffer cmd, int cameraWidth, int cameraHeight, RTHandle cameraDepth)
		{
			using var profilerScope = new ProfilingScope(cmd, FulumaProfiling.DownsampleDepthSampler);

			// URP executes one camera before the next, so the keyword holds for this camera's blits.
			_sceneDepth = IsSampleable(cameraDepth) ? cameraDepth : null;
			_material.SetKeyword(_cameraDepthTextureKeyword, _sceneDepth == null);

			SetDownsampleSizes(_material, cameraWidth, cameraHeight, _targetSize.x, _targetSize.y);
			CoreUtils.SetRenderTarget(cmd, _rawDepthTarget, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
				_depthTarget, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, ClearFlag.None, Color.clear);

			if (_sceneDepth != null)
				Blitter.BlitTexture(cmd, _sceneDepth, FullScaleBias, _material, DownsampleDepthPass);
			else
				Blitter.BlitTexture(cmd, FullScaleBias, _material, DownsampleDepthPass);
		}

		/// <summary>
		/// Binds the light buffer, cleared to black, for the lights; with depth, the downsampled depth too (loaded, for the
		/// outside pass's depth test; not stored, nothing reads it afterwards).
		/// </summary>
		public void BeginLightDraw(CommandBuffer cmd, bool withDepth)
		{
			if (withDepth)
			{
				CoreUtils.SetRenderTarget(cmd, _lightTarget, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
					_depthTarget, RenderBufferLoadAction.Load, RenderBufferStoreAction.DontCare, ClearFlag.Color, Color.clear);
				return;
			}

			CoreUtils.SetRenderTarget(cmd, _lightTarget, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
				ClearFlag.Color, Color.clear);
		}

		/// <summary>
		/// Adds the light buffer onto <paramref name="cameraColor"/>, upscaled as <see cref="Upsample"/> says (depth-aware
		/// reads the same scene depth the downsample read). Leaves <paramref name="cameraColor"/> bound without depth.
		/// </summary>
		/// <param name="flip">The camera renders straight to the backbuffer (unflipped projection) on an API whose render
		/// textures start at the top: the buffer, drawn with that projection, is then sampled upside down, as URP's own final
		/// blit does (measured on Vulkan, Mali-G52: without it the buffer composited upside down).</param>
		public void ExecuteComposite(CommandBuffer cmd, RTHandle cameraColor, bool flip)
		{
			using var profilerScope = new ProfilingScope(cmd, FulumaProfiling.CompositeSampler);

			if (Upsample == LightBufferUpsample.DepthAware)
			{
				SetCompositeSize(_material, _targetSize);
				cmd.SetGlobalTexture(LowResDepthId, _rawDepthTarget);
				if (_sceneDepth != null)
					cmd.SetGlobalTexture(CameraDepthId, _sceneDepth);
			}

			// The composite blends onto the camera color, so it loads it.
			CoreUtils.SetRenderTarget(cmd, cameraColor, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
				ClearFlag.None, Color.clear);
			Blitter.BlitTexture(cmd, _lightTarget, flip ? FlippedScaleBias : FullScaleBias, _material, CompositePass);
		}

		partial void OnCreated() => _cameraDepthTextureKeyword = new LocalKeyword(_material.shader, CameraDepthTextureKeywordName);

		partial void OnDisposing()
		{
			_lightTarget?.Release();
			_depthTarget?.Release();
			_rawDepthTarget?.Release();
			_lightTarget = null;
			_depthTarget = null;
			_rawDepthTarget = null;
			_sceneDepth = null;
		}
	}
}
#endif
