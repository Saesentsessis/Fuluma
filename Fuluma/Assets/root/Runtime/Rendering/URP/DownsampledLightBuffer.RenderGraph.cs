#if UNITY_6000_0_OR_NEWER
#define FULUMA_RENDER_GRAPH
#endif

#if FULUMA_RENDER_GRAPH
using Saesentsessis.Fuluma.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace Saesentsessis.Fuluma.Rendering.URP
{
	/// <summary>
	/// The light buffer's RenderGraph path (Unity 6): per-frame textures and raster passes.
	/// </summary>
	internal sealed partial class DownsampledLightBuffer
	{
		private static void Blit(BlitPassData data, RasterGraphContext context)
		{
			// Textures resolve only while the graph executes.
			Texture lowResDepth = null, cameraDepth = null;
			if (data.LowResDepth.IsValid())
			{
				RTHandle handle = data.LowResDepth;
				lowResDepth = handle;
			}

			if (data.CameraDepth.IsValid())
			{
				RTHandle handle = data.CameraDepth;
				cameraDepth = handle;
			}

			data.Buffer.BindBlitTextures(lowResDepth, cameraDepth);
			Blitter.BlitTexture(context.cmd, data.Source, FullScaleBias, data.Buffer._material, data.Pass);
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

			SetDownsampleSizes(_material, cameraWidth, cameraHeight, width, height);

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
			data.Buffer = this;
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
				SetCompositeSize(_material, targets.Size);
			}

			if (depthAttachment.IsValid())
				builder.SetRenderAttachmentDepth(depthAttachment, AccessFlags.Read);

			builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
			builder.AllowGlobalStateModification(false);

			data.Source = targets.Light;
			data.LowResDepth = depthAware ? targets.RawDepth : TextureHandle.nullHandle;
			data.CameraDepth = depthAware ? cameraDepth : TextureHandle.nullHandle;
			data.Buffer = this;
			data.Pass = CompositePass;
			builder.SetRenderFunc(static (BlitPassData pData, RasterGraphContext context) => Blit(pData, context));
		}

		private TextureDesc LightDesc(int width, int height, in TextureDesc camera)
		{
			var desc = LowResDesc(camera, width, height, "_FulumaLightBuffer");
			desc.colorFormat = _lightFormat;
			desc.filterMode = FilterMode.Bilinear;
			desc.clearBuffer = true;
			desc.clearColor = Color.clear;
			return desc;
		}

		private TextureDesc RawDepthDesc(int width, int height, in TextureDesc camera)
		{
			var desc = LowResDesc(camera, width, height, "_FulumaLowResDepth");
			desc.colorFormat = _rawDepthFormat;
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

			public DownsampledLightBuffer Buffer;
			public int Pass;
		}
	}
}
#endif
