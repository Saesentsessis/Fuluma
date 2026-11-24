using System;
using Saesentsessis.Fuluma.Profiling;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Rendering.URP
{
	/// <summary>
	/// The downsampled light buffer around <see cref="FulumaSpotLightRenderPass"/>: per camera, a low-resolution depth
	/// made from the camera depth, the cleared light buffer the lights draw into, and the pass that adds it back onto the
	/// camera color (<see cref="LightBufferUpsample"/>).
	/// </summary>
	/// <remarks>
	/// Sizes are <c>ceil(camera size × scale)</c>, so a low-resolution pixel covers <c>1 / scale</c> camera pixels per
	/// axis (not a whole number in general: the downsample reads every camera pixel it touches, at most 16 per axis,
	/// spread evenly, 2x2 texels per gather). Each low-resolution pixel keeps one depth of the camera pixels it covers,
	/// written twice: as raw depth in a depth buffer (the outside pass's early-Z) and as linear eye depth in an R32F copy
	/// (the light shader's distance clipping and the depth-aware composite, neither of which then converts it; a depth
	/// attachment can't also be sampled in the same pass).
	/// <list type="bullet">
	/// <item><b>Bilinear</b>: the farthest depth of each block. No light behind any of its pixels is lost, but the glow
	/// reaches past nearer geometry inside a block, and bilinear filtering spreads it over depth edges.</item>
	/// <item><b>Depth-aware</b>: farthest and nearest alternating like a checkerboard, so every 2x2 neighbourhood holds
	/// texels shaded up to the near and up to the far surface. The composite weights the 4 texels around each camera pixel
	/// by bilinear weight and depth similarity (joint bilateral upsampling), and takes the nearest in depth when none
	/// is close.</item>
	/// </list>
	/// </remarks>
#if UNITY_6000_5_OR_NEWER
	// Immutable: shader property IDs and constants.
	[NoAutoStaticsCleanup]
#endif
	internal sealed class DownsampledLightBuffer : IDisposable
	{
		public const string ShaderName = "Hidden/Fuluma/LightBuffer";

		// Shader passes of Hidden/Fuluma/LightBuffer.
		private const int DownsampleDepthPass = 0;
		private const int BilinearCompositePass = 1;
		private const int DepthAwareCompositePass = 2;

		private const string CheckerboardKeywordName = "_FULUMA_CHECKERBOARD";

		private static readonly int DownsampleSizesId = Shader.PropertyToID("_FulumaDownsampleSizes");
		private static readonly int LowResSizeId = Shader.PropertyToID("_FulumaLowResSize");
		private static readonly int LowResDepthId = Shader.PropertyToID("_FulumaLowResDepth");
		private static readonly int CameraDepthId = Shader.PropertyToID("_FulumaCameraDepth");
		private static readonly Vector4 FullScaleBias = new(1f, 1f, 0f, 0f);

		/// <summary>
		/// <c>ceil(size × scale)</c>, at least 1. The small slack keeps float rounding from adding a pixel when the product is
		/// a whole number (1920 × 0.3).
		/// </summary>
		internal static int ScaledSize(int size, float scale) => math.max(1, (int)math.ceil(size * scale - 1e-3f));

		private static void Blit(BlitPassData data, RasterGraphContext context)
		{
			// Textures resolve only while the graph executes; the material is this buffer's own.
			if (data.LowResDepth.IsValid())
			{
				RTHandle lowResDepth = data.LowResDepth;
				data.Material.SetTexture(LowResDepthId, lowResDepth);
			}

			if (data.CameraDepth.IsValid())
			{
				RTHandle cameraDepth = data.CameraDepth;
				data.Material.SetTexture(CameraDepthId, cameraDepth);
			}

			Blitter.BlitTexture(context.cmd, data.Source, FullScaleBias, data.Material, data.Pass);
		}

		/// <summary>
		/// A low-resolution texture that matches the camera color wherever the lights and the composite rely on it:
		/// dimension and slices (a texture array with single-pass XR), XR usage and dynamic resolution. Everything else
		/// differs on purpose (size, format, one sample, no mips), so it starts from a fresh descriptor and copies those
		/// fields, instead of copying the camera's and resetting the rest (which would also inherit fields added later).
		/// </summary>
		private static TextureDesc LowResDesc(in TextureDesc camera, int width, int height, string name) => new(width, height)
		{
			dimension = camera.dimension,
			slices = camera.slices,
			vrUsage = camera.vrUsage,
			useDynamicScale = camera.useDynamicScale,
			useDynamicScaleExplicit = camera.useDynamicScaleExplicit,
			name = name,
			wrapMode = TextureWrapMode.Clamp
		};

		private static TextureDesc DepthDesc(int width, int height, in TextureDesc camera)
		{
			var desc = LowResDesc(camera, width, height, "_FulumaLowResDepthBuffer");
			desc.depthBufferBits = DepthBits.Depth32;
			desc.filterMode = FilterMode.Point;
			return desc;
		}

		/// <summary>
		/// Packed 11:11:10 float when it can be blended into (half the bandwidth; no alpha needed), else RGBA half.
		/// </summary>
		private static GraphicsFormat PickLightFormat()
			=> SystemInfo.IsFormatSupported(GraphicsFormat.B10G11R11_UFloatPack32, GraphicsFormatUsage.Blend)
				? GraphicsFormat.B10G11R11_UFloatPack32
				: GraphicsFormat.R16G16B16A16_SFloat;

		/// <summary>
		/// Eye depth in 32-bit float; 16-bit is a last resort (~0.1% relative steps).
		/// </summary>
		private static GraphicsFormat PickRawDepthFormat()
			=> SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, GraphicsFormatUsage.Render)
				? GraphicsFormat.R32_SFloat
				: GraphicsFormat.R16_SFloat;

		private readonly GraphicsFormat _lightFormat;
		private readonly GraphicsFormat _rawDepthFormat;
		private Material _material;

		private float _scale;

		/// <param name="scale">Fraction of the camera's width and height, below 1 (see <see cref="LightBufferScaling"/>).</param>
		public DownsampledLightBuffer(Shader shader, float scale, LightBufferUpsample upsample)
		{
			if (shader == null)
				throw new ArgumentNullException(nameof(shader));

			Scale = scale;
			Upsample = upsample;
			_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
			_material.SetKeyword(new LocalKeyword(shader, CheckerboardKeywordName), upsample == LightBufferUpsample.DepthAware);
			_lightFormat = PickLightFormat();
			_rawDepthFormat = PickRawDepthFormat();
		}

		/// <summary>
		/// Fraction of the camera's width and height the buffer has, clamped to [<see cref="LightBufferScaling.Min"/>, 1].
		/// Takes effect from the next recorded camera.
		/// </summary>
		public float Scale
		{
			get => _scale;
			set => _scale = LightBufferScaling.Clamp(value);
		}

		public LightBufferUpsample Upsample { get; }

		/// <summary>
		/// Allocates this camera's targets and records the depth downsample from <paramref name="cameraDepth"/>
		/// (single-sampled: the camera's depth attachment, URP's resolved <c>_CameraDepthTexture</c> with MSAA, or the
		/// depth prepass's depth).
		/// </summary>
		/// <param name="cameraColor">The camera's color target: the targets copy its dimension and slices (2D, or a
		/// texture array with single-pass XR), so they match how the shaders declare them.</param>
		public Targets RecordDownsample(RenderGraph renderGraph, TextureHandle cameraDepth, in TextureDesc cameraColor,
			int cameraWidth, int cameraHeight)
		{
			var width = ScaledSize(cameraWidth, _scale);
			var height = ScaledSize(cameraHeight, _scale);

			var targets = new Targets(
				renderGraph.CreateTexture(LightDesc(width, height, cameraColor)),
				renderGraph.CreateTexture(DepthDesc(width, height, cameraColor)),
				renderGraph.CreateTexture(RawDepthDesc(width, height, cameraColor)),
				new Vector2Int(width, height));

			// URP records and executes one camera before recording the next, so this holds for this camera's pass.
			_material.SetVector(DownsampleSizesId, new Vector4(cameraWidth, cameraHeight, width, height));

			using var builder = renderGraph.AddRasterRenderPass(FulumaProfiling.DownsampleDepthName, out BlitPassData data,
				FulumaProfiling.DownsampleDepthSampler);

			builder.UseTexture(cameraDepth);
			builder.SetRenderAttachment(targets.RawDepth, 0, AccessFlags.Write);
			builder.SetRenderAttachmentDepth(targets.Depth, AccessFlags.Write);
			builder.AllowGlobalStateModification(false);

			// Pass data is pooled: every field is set, or a composite's old handles would reach Blit.
			data.Source = cameraDepth;
			data.LowResDepth = TextureHandle.nullHandle;
			data.CameraDepth = TextureHandle.nullHandle;
			data.Material = _material;
			data.Pass = DownsampleDepthPass;
			builder.SetRenderFunc(static (BlitPassData pData, RasterGraphContext context) => Blit(pData, context));

			return targets;
		}

		/// <summary>
		/// The light buffer alone, for lights that don't read scene depth (no depth downsample; <see cref="Targets.Depth"/>
		/// and <see cref="Targets.RawDepth"/> are invalid). Composite it bilinearly.
		/// </summary>
		public Targets CreateLightTarget(RenderGraph renderGraph, in TextureDesc cameraColor, int cameraWidth, int cameraHeight)
		{
			var width = ScaledSize(cameraWidth, _scale);
			var height = ScaledSize(cameraHeight, _scale);
			return new Targets(renderGraph.CreateTexture(LightDesc(width, height, cameraColor)), TextureHandle.nullHandle,
				TextureHandle.nullHandle, new Vector2Int(width, height));
		}

		/// <summary>
		/// Records the pass that adds the light buffer of <paramref name="targets"/> onto <paramref name="cameraColor"/>,
		/// upscaled as <see cref="Upsample"/> says (depth-aware reads <paramref name="cameraDepth"/>, the same depth the
		/// downsample read).
		/// </summary>
		/// <param name="cameraDepth">Full-resolution depth the depth-aware composite samples.</param>
		/// <param name="depthAttachment">Kept attached read-only when valid (the camera's depth attachment), so RenderGraph
		/// can merge the composite with the camera's neighbouring passes instead of storing and reloading the camera color
		/// around it. Never the texture the depth-aware composite samples. Reading the depth as an input attachment
		/// instead (which would allow the merge for it too) measured slower on Mali-G52 and G77 (2026-11-18,
		/// <c>docs/dev/PixelShaderIdeas.md</c>).</param>
		public void RecordComposite(RenderGraph renderGraph, in Targets targets, TextureHandle cameraColor, TextureHandle cameraDepth,
			TextureHandle depthAttachment)
		{
			var depthAware = Upsample == LightBufferUpsample.DepthAware;

			using var builder = renderGraph.AddRasterRenderPass(FulumaProfiling.CompositeName, out BlitPassData data,
				FulumaProfiling.CompositeSampler);

			builder.UseTexture(targets.Light);
			if (depthAware)
			{
				builder.UseTexture(targets.RawDepth);
				builder.UseTexture(cameraDepth);
				_material.SetVector(LowResSizeId, new Vector4(targets.Size.x, targets.Size.y, 0f, 0f));
			}

			if (depthAttachment.IsValid())
				builder.SetRenderAttachmentDepth(depthAttachment, AccessFlags.Read);

			builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
			builder.AllowGlobalStateModification(false);

			data.Source = targets.Light;
			data.LowResDepth = depthAware ? targets.RawDepth : TextureHandle.nullHandle;
			data.CameraDepth = depthAware ? cameraDepth : TextureHandle.nullHandle;
			data.Material = _material;
			data.Pass = depthAware ? DepthAwareCompositePass : BilinearCompositePass;
			builder.SetRenderFunc(static (BlitPassData pData, RasterGraphContext context) => Blit(pData, context));
		}

		public void Dispose()
		{
			if (_material == null)
				return;

			CoreUtils.Destroy(_material);
			_material = null;
		}

		private TextureDesc LightDesc(int width, int height, in TextureDesc camera)
		{
			var desc = LowResDesc(camera, width, height, "_FulumaLightBuffer");
			desc.format = _lightFormat;
			desc.filterMode = FilterMode.Bilinear;
			desc.clearBuffer = true;
			desc.clearColor = Color.clear;
			return desc;
		}

		private TextureDesc RawDepthDesc(int width, int height, in TextureDesc camera)
		{
			var desc = LowResDesc(camera, width, height, "_FulumaLowResDepth");
			desc.format = _rawDepthFormat;
			desc.filterMode = FilterMode.Point;
			return desc;
		}

		/// <summary>
		/// Textures of one camera's downsampled light buffer.
		/// </summary>
		public readonly struct Targets
		{
			/// <summary>
			/// Cleared to black; the lights add into it.
			/// </summary>
			public readonly TextureHandle Light;

			/// <summary>
			/// Low-resolution depth buffer, for the outside pass's depth test.
			/// </summary>
			public readonly TextureHandle Depth;

			/// <summary>
			/// The same depth as linear eye depth in an R32F texture, read by the light shader and the depth-aware
			/// composite (<c>_FulumaLowResDepth</c>).
			/// </summary>
			public readonly TextureHandle RawDepth;

			/// <summary>
			/// Size of the low-resolution textures in pixels.
			/// </summary>
			public readonly Vector2Int Size;

			public Targets(TextureHandle light, TextureHandle depth, TextureHandle rawDepth, Vector2Int size)
			{
				Light = light;
				Depth = depth;
				RawDepth = rawDepth;
				Size = size;
			}
		}

		private class BlitPassData
		{
			public TextureHandle Source;

			/// <summary>
			/// Depth-aware composite only (else invalid); the camera depth only when read as a texture.
			/// </summary>
			public TextureHandle LowResDepth, CameraDepth;

			public Material Material;
			public int Pass;
		}
	}
}
