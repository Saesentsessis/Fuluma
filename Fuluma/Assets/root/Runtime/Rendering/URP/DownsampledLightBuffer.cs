using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
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
	/// This file holds what doesn't depend on how URP runs the passes; <c>DownsampledLightBuffer.RenderGraph.cs</c>
	/// records them into URP's render graph (Unity 6).
	/// </remarks>
#if UNITY_6000_5_OR_NEWER
	// Immutable: shader property IDs and constants.
	[NoAutoStaticsCleanup]
#endif
	internal sealed partial class DownsampledLightBuffer : IDisposable
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

		// Unity 2022.3 names the format usages FormatUsage; Unity 6 GraphicsFormatUsage.
#if UNITY_6000_0_OR_NEWER
		private const GraphicsFormatUsage BlendUsage = GraphicsFormatUsage.Blend;
		private const GraphicsFormatUsage RenderUsage = GraphicsFormatUsage.Render;
#else
		private const FormatUsage BlendUsage = FormatUsage.Blend;
		private const FormatUsage RenderUsage = FormatUsage.Render;
#endif

		/// <summary>
		/// <c>ceil(size × scale)</c>, at least 1. The small slack keeps float rounding from adding a pixel when the product is
		/// a whole number (1920 × 0.3).
		/// </summary>
		internal static int ScaledSize(int size, float scale) => math.max(1, (int)math.ceil(size * scale - 1e-3f));

		/// <summary>
		/// Packed 11:11:10 float when it can be blended into (half the bandwidth; no alpha needed), else RGBA half.
		/// </summary>
		private static GraphicsFormat PickLightFormat()
			=> SystemInfo.IsFormatSupported(GraphicsFormat.B10G11R11_UFloatPack32, BlendUsage)
				? GraphicsFormat.B10G11R11_UFloatPack32
				: GraphicsFormat.R16G16B16A16_SFloat;

		/// <summary>
		/// Eye depth in 32-bit float; 16-bit is a last resort (~0.1% relative steps).
		/// </summary>
		private static GraphicsFormat PickRawDepthFormat()
			=> SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, RenderUsage)
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
			OnCreated();
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
		/// Shader pass of the composite <see cref="Upsample"/> picks.
		/// </summary>
		private int CompositePass => Upsample == LightBufferUpsample.DepthAware ? DepthAwareCompositePass : BilinearCompositePass;

		public void Dispose()
		{
			if (_material == null)
				return;

			OnDisposing();
			CoreUtils.Destroy(_material);
			_material = null;
		}

		/// <summary>
		/// Lets a render path create what it needs beside the material.
		/// </summary>
		partial void OnCreated();

		/// <summary>
		/// Lets a render path release what <see cref="OnCreated"/> or its passes created.
		/// </summary>
		partial void OnDisposing();

		/// <summary>
		/// The downsample's camera and low-resolution sizes. URP records and executes one camera before recording the next,
		/// so this holds for this camera's pass.
		/// </summary>
		private static void SetDownsampleSizes(Material material, int cameraWidth, int cameraHeight, int width, int height)
			=> material.SetVector(DownsampleSizesId, new Vector4(cameraWidth, cameraHeight, width, height));

		/// <summary>
		/// The depth-aware composite's low-resolution size.
		/// </summary>
		private static void SetCompositeSize(Material material, Vector2Int size)
			=> material.SetVector(LowResSizeId, new Vector4(size.x, size.y, 0f, 0f));

		/// <summary>
		/// The textures a blit reads besides its source, set only when given (the material is this buffer's own).
		/// </summary>
		private void BindBlitTextures(Texture lowResDepth, Texture cameraDepth)
		{
			if (lowResDepth != null)
				_material.SetTexture(LowResDepthId, lowResDepth);

			if (cameraDepth != null)
				_material.SetTexture(CameraDepthId, cameraDepth);
		}
	}
}
