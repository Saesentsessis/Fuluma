#if UNITY_6000_0_OR_NEWER
#define FULUMA_RENDER_GRAPH
#endif

#if FULUMA_RENDER_GRAPH
using Saesentsessis.Fuluma.Profiling;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Saesentsessis.Fuluma.Rendering.URP
{
	/// <summary>
	/// The pass's RenderGraph path (Unity 6): records the light draws, the light buffer's passes and the early pass's
	/// work into URP's render graph.
	/// </summary>
	internal partial class FulumaSpotLightRenderPass
	{
		/// <summary>
		/// Full-resolution depth the light buffer's downsample and depth-aware composite read: the camera's depth attachment
		/// itself when it is single-sampled (URP then needn't copy it into <c>_CameraDepthTexture</c>, a whole-screen pass
		/// plus a depth store and reload on tile-based GPUs), else URP's resolved copy.
		/// </summary>
		private static TextureHandle LightBufferDepthSource(UniversalResourceData resources, int msaaSamples)
			=> msaaSamples <= 1 ? resources.activeDepthTexture : resources.cameraDepthTexture;

		private static void ExecutePass(RasterPassData data, RasterGraphContext context)
		{
			// Textures resolve only while the graph executes.
			Texture lowResDepth = null;
			if (data.LowResDepth.IsValid())
			{
				RTHandle handle = data.LowResDepth;
				lowResDepth = handle;
			}

			ExecuteDraws(new RasterDrawCommands(context.cmd), data, lowResDepth);
		}

		private DownsampledLightBuffer.Targets _earlyTargets;

		/// <summary>
		/// The depth the early pass downsampled (the prepass texture, or the primed depth attachment), which the
		/// depth-aware composite reads too.
		/// </summary>
		private TextureHandle _earlyDepth;

		/// <summary>
		/// Records this camera's light draws: at full resolution, through the downsampled light buffer, or both (Large
		/// Only); only the composite when the early pass already drew the buffer. Records nothing when the store is
		/// empty or there is no scene depth to read.
		/// </summary>
		public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
		{
			var resources = frameData.Get<UniversalResourceData>();
			var cameraData = frameData.Get<UniversalCameraData>();
			var camera = cameraData.camera;

			// What the early pass (LightBufferTiming) already did for this camera, if anything.
			var prepared = _preparedCamera == camera;
			var early = prepared && _earlyCamera == camera;
			_preparedCamera = null;
			_earlyCamera = null;

			if (TryPrepareCamera(renderGraph, resources, cameraData, prepared, out var msaaSamples) == false)
				return;

			LastRecordedLowResolutionPasses = false;
			LastRecordedLightBufferBeforeOpaques = early;

			if (_lightBuffer == null)
			{
				RecordFullResolutionPass(renderGraph, resources, msaaSamples, camera, DrawSet.All);
				return;
			}

			if (early)
			{
				// The buffer is drawn already; small lights (LargeOnly) at full resolution, then the composite. It reads the
				// prepass depth texture, so the camera's depth stays attached (read-only) and the composite merges into
				// URP's opaque native render pass.
				if (SplitsLargeLights)
				{
					RecordFullResolutionPass(renderGraph, resources, msaaSamples, camera, DrawSet.Small);
					LastRecordedLowResolutionPasses = true;
				}

				// The depth-aware composite samples the downsampled depth's source; the camera's depth attachment stays
				// attached unless that is it (primed depth).
				var keepDepthAttached = _earlyDepthIsAttachment == false || _lightBuffer.Upsample == LightBufferUpsample.Bilinear;
				_lightBuffer.RecordComposite(renderGraph, _earlyTargets, resources.activeColorTexture, _earlyDepth,
					keepDepthAttached ? resources.activeDepthTexture : TextureHandle.nullHandle);
				return;
			}

			if (SplitsLargeLights == false)
			{
				RecordLowResolutionPasses(renderGraph, resources, cameraData, msaaSamples, DrawSet.All, FulumaProfiling.DrawName,
					FulumaProfiling.DrawSampler);
				return;
			}

			// LargeOnly: small lights at full resolution; large and camera-inside lights through the downsampled buffer,
			// unless the last culling of this camera found none (then the full-resolution pass draws everything).
			var lowResolution = (_useSceneDepth == false || LightBufferDepthSource(resources, msaaSamples).IsValid())
			                    && LowResolutionLightsLastFrame(camera);
			LastRecordedLowResolutionPasses = lowResolution;

			RecordFullResolutionPass(renderGraph, resources, msaaSamples, camera, lowResolution ? DrawSet.Small : DrawSet.All);

			if (lowResolution)
				RecordLowResolutionPasses(renderGraph, resources, cameraData, msaaSamples, DrawSet.Large, FulumaProfiling.DrawLowResolutionName,
					FulumaProfiling.DrawLowResolutionSampler);
		}

		/// <summary>
		/// <see cref="LightBufferTiming"/> before the opaques: records the light buffer's downsample and low-resolution draw
		/// from URP's depth prepass, right after the prepasses (called by <see cref="FulumaLightBufferEarlyPass"/>). The main
		/// pass then composites it. Without a prepass (Auto, and URP renders none) nothing is recorded here and the main pass
		/// does everything after the opaques.
		/// </summary>
		internal void RecordLightBufferBeforeOpaques(RenderGraph renderGraph, ContextContainer frameData)
		{
			var resources = frameData.Get<UniversalResourceData>();
			var cameraData = frameData.Get<UniversalCameraData>();
			var camera = cameraData.camera;

			_preparedCamera = null;
			_earlyCamera = null;

			if (_lightBuffer == null || TryPrepareCamera(renderGraph, resources, cameraData, false, out _) == false)
				return;

			_preparedCamera = camera;

			if (TryGetPrepassDepth(resources, cameraData, out var prepassDepth, out var isAttachment) == false)
				return;

			if (SplitsLargeLights && LowResolutionLightsLastFrame(camera) == false)
				return;

			_earlyTargets = RecordLowResolutionDraw(renderGraph, resources, cameraData, prepassDepth,
				SplitsLargeLights ? DrawSet.Large : DrawSet.All,
				SplitsLargeLights ? FulumaProfiling.DrawLowResolutionName : FulumaProfiling.DrawName,
				SplitsLargeLights ? FulumaProfiling.DrawLowResolutionSampler : FulumaProfiling.DrawSampler);
			_earlyDepth = prepassDepth;
			_earlyDepthIsAttachment = isAttachment;
			_earlyCamera = camera;
		}

		/// <summary>
		/// Where URP's depth prepass put this camera's depth, if it renders one: with Auto, a prepass the renderer's settings
		/// cause anyway (<see cref="UrpDepthPrepass"/>; the depth attachment with depth priming, else
		/// <c>_CameraDepthTexture</c>), or,
		/// with <see cref="URP.LightBufferTiming.BeforeOpaques"/>, the one the early pass requested (in
		/// <c>_CameraDepthTexture</c>, which URP fills right after the prepass even with depth priming).
		/// </summary>
		private bool TryGetPrepassDepth(UniversalResourceData resources, UniversalCameraData cameraData, out TextureHandle depth,
			out bool isAttachment)
		{
			// Before Opaques asks URP for the depth texture itself (the early pass's input), so URP may then also need to
			// copy depth, which depth priming depends on.
			var forced = _lightBufferTiming == LightBufferTiming.BeforeOpaques;
			var passNeedsDepthTexture = forced || (input & ScriptableRenderPassInput.Depth) != 0;
			if (UrpDepthPrepass.Renders(RendererData, UrpDepthPrepass.CameraState.From(cameraData), passNeedsDepthTexture,
				    out isAttachment))
			{
				depth = isAttachment ? resources.activeDepthTexture : resources.cameraDepthTexture;
				if (depth.IsValid())
					return true;
			}

			isAttachment = false;
			depth = forced ? resources.cameraDepthTexture : TextureHandle.nullHandle;
			return depth.IsValid();
		}

		/// <summary>
		/// Once per camera and frame, by whichever pass records first: consumes the culling started for it (or starts
		/// it), sets the depth keywords and binds the mirror's buffers.
		/// </summary>
		/// <param name="alreadyPrepared">The early pass prepared this camera: only returns its MSAA sample count.</param>
		/// <returns>False when nothing is drawn for this camera.</returns>
		private bool TryPrepareCamera(RenderGraph renderGraph, UniversalResourceData resources, UniversalCameraData cameraData,
			bool alreadyPrepared, out int msaaSamples)
		{
			msaaSamples = _preparedMsaaSamples;
			if (alreadyPrepared)
				return true;

			if (TryBeginPreparingCamera(out var culledCamera) == false)
				return false;

			if (resources.activeColorTexture.IsValid() == false || resources.activeDepthTexture.IsValid() == false)
				return false;

			// Without scene depth the pass may draw straight onto an imported camera target, whose descriptor RenderGraph
			// can't give; the count then only decides whether the composite keeps the depth attached.
			msaaSamples = _useSceneDepth
				? math.max(1, (int)renderGraph.GetTextureDesc(resources.activeDepthTexture).msaaSamples)
				: math.max(1, cameraData.cameraTargetDescriptor.msaaSamples);

			if (HasNeededDepthTexture(msaaSamples, resources.cameraDepthTexture.IsValid()) == false)
				return false;

			FinishPreparingCamera(msaaSamples, cameraData.camera, culledCamera, cameraData.GetViewMatrix(),
				cameraData.GetProjectionMatrix(), cameraData.worldSpaceCameraPos);
			return true;
		}

		private void RecordFullResolutionPass(RenderGraph renderGraph, UniversalResourceData resources, int msaaSamples,
			Camera camera, DrawSet drawSet)
		{
			using var builder = renderGraph.AddRasterRenderPass(FulumaProfiling.DrawName, out RasterPassData data,
				FulumaProfiling.DrawSampler);

			builder.SetRenderAttachment(resources.activeColorTexture, 0);
			BindSceneDepth(builder, resources);
			builder.AllowGlobalStateModification(false);

			FillPassData(data, lowResolution: false, camera, drawSet);
			data.LowResDepth = TextureHandle.nullHandle;
			builder.SetRenderFunc(static (RasterPassData pData, RasterGraphContext context) => ExecutePass(pData, context));
		}

		/// <summary>
		/// Low-resolution depth, the lights of <paramref name="drawSet"/> into the low-resolution buffer, then the buffer
		/// onto the camera color.
		/// </summary>
		private void RecordLowResolutionPasses(RenderGraph renderGraph, UniversalResourceData resources,
			UniversalCameraData cameraData, int msaaSamples, DrawSet drawSet, string drawName, ProfilingSampler drawSampler)
		{
			var sceneDepth = _useSceneDepth ? LightBufferDepthSource(resources, msaaSamples) : TextureHandle.nullHandle;
			var targets = RecordLowResolutionDraw(renderGraph, resources, cameraData, sceneDepth, drawSet, drawName, drawSampler);

			// The bilinear composite keeps the camera's depth attached (read-only, unused) so it merges into the following
			// passes; the depth-aware one samples it as a texture, so it can't.
			var keepDepthAttached = _lightBuffer.Upsample == LightBufferUpsample.Bilinear && msaaSamples <= 1;
			_lightBuffer.RecordComposite(renderGraph, targets, resources.activeColorTexture, sceneDepth,
				keepDepthAttached ? resources.activeDepthTexture : TextureHandle.nullHandle);
		}

		/// <summary>
		/// Low-resolution depth from <paramref name="sceneDepth"/> (none when the lights don't use scene depth), then the
		/// lights of <paramref name="drawSet"/> into the low-resolution buffer.
		/// </summary>
		private DownsampledLightBuffer.Targets RecordLowResolutionDraw(RenderGraph renderGraph, UniversalResourceData resources,
			UniversalCameraData cameraData, TextureHandle sceneDepth, DrawSet drawSet, string drawName, ProfilingSampler drawSampler)
		{
			// The buffer is sized from the texture it is composited onto. URP's camera color has an explicit size; a color
			// target sized by scale (another feature's) reports none, so the camera's target size stands in.
			var cameraColor = renderGraph.GetTextureDesc(resources.activeColorTexture);
			int width, height;
			if (cameraColor.sizeMode == TextureSizeMode.Explicit)
			{
				width = cameraColor.width;
				height = cameraColor.height;
			}
			else
			{
				width = cameraData.cameraTargetDescriptor.width;
				height = cameraData.cameraTargetDescriptor.height;
			}

			var targets = _useSceneDepth
				? _lightBuffer.RecordDownsample(renderGraph, sceneDepth, cameraColor, width, height)
				: _lightBuffer.CreateLightTarget(renderGraph, cameraColor, width, height);

			using var builder = renderGraph.AddRasterRenderPass(drawName, out RasterPassData data, drawSampler);
			builder.SetRenderAttachment(targets.Light, 0);
			if (_useSceneDepth)
			{
				builder.SetRenderAttachmentDepth(targets.Depth, AccessFlags.Read);
				builder.UseTexture(targets.RawDepth);
			}

			builder.AllowGlobalStateModification(false);

			FillPassData(data, lowResolution: true, cameraData.camera, drawSet);
			data.LowResDepth = targets.RawDepth;
			builder.SetRenderFunc(static (RasterPassData pData, RasterGraphContext context) => ExecutePass(pData, context));
			return targets;
		}

		/// <summary>
		/// The outside pass (1) depth-tests its front faces against the scene, so the GPU rejects occluded pixels before
		/// the fragment program runs. That needs the scene depth bound read-only as the depth attachment.
		/// </summary>
		private void BindSceneDepth(IRasterRenderGraphBuilder builder, UniversalResourceData resources)
		{
			// No scene depth: no depth attachment either, so the outside pass's depth test can't hide anything.
			if (_useSceneDepth == false)
				return;

#if UNITY_6000_6_OR_NEWER
			if (_depthAsInputAttachment)
			{
				// RenderGraph binds a depth input attachment as the read-only depth attachment too (the backends require
				// it), and rejects an explicit SetRenderAttachmentDepth of the same texture.
				builder.SetExtendedFeatureFlags(ExtendedFeatureFlags.DepthAttachmentAsInputAttachment);
				builder.SetInputAttachment(resources.activeDepthTexture, 0);
				return;
			}
#endif

			// The fragment program reads the _CameraDepthTexture copy; the attachment itself is only depth-tested.
			builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
			builder.UseTexture(resources.cameraDepthTexture);
		}

		private readonly struct RasterDrawCommands : IDrawCommands
		{
			private readonly RasterCommandBuffer _cmd;

			public RasterDrawCommands(RasterCommandBuffer cmd) => _cmd = cmd;

			public void DrawMeshInstancedProcedural(Mesh mesh, int submesh, Material material, int shaderPass, int count,
				MaterialPropertyBlock properties)
				=> _cmd.DrawMeshInstancedProcedural(mesh, submesh, material, shaderPass, count, properties);
		}

		/// <summary>
		/// RenderGraph pools pass data: every field is set when a pass is recorded.
		/// </summary>
		private class RasterPassData : DrawPassData
		{
			/// <summary>
			/// Downsampled buffer only: the low-resolution depth the shader reads.
			/// </summary>
			public TextureHandle LowResDepth;
		}
	}
}
#endif
