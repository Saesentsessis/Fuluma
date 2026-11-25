#if !UNITY_6000_3_OR_NEWER || (URP_COMPATIBILITY_MODE && !UNITY_6000_4_OR_NEWER)
#define FULUMA_COMPATIBILITY_PATH
#endif

#if FULUMA_COMPATIBILITY_PATH
using Saesentsessis.Fuluma.Profiling;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// URP 17 (Unity 6.0-6.3) marks the Compatibility Mode members obsolete; URP 14 (2022.3) has no other path.
#pragma warning disable 618, 672

namespace Saesentsessis.Fuluma.Rendering.URP
{
	/// <summary>
	/// The pass's Compatibility path: URP 14 (Unity 2022.3), and URP 17 with Compatibility Mode on (Unity 6.0-6.2, and 6.3
	/// with Unity's <c>URP_COMPATIBILITY_MODE</c> define). URP calls <see cref="Execute"/> instead of recording a render
	/// graph; the draws go into a command buffer executed at once. URP executes each camera before the next, so material
	/// state set while a camera executes holds for its draws.
	/// </summary>
	/// <remarks>
	/// Scene depth always comes from URP's <c>_CameraDepthTexture</c> (<see cref="NeedsCameraDepthTexture"/>): the
	/// Compatibility path has no depth input attachment, and doesn't read the depth attachment as a texture. Every draw
	/// uses the camera's own projection, and blits copy texel rows as they are, so the light buffer keeps the camera
	/// target's orientation (render texture or backbuffer).
	/// </remarks>
	internal partial class FulumaSpotLightRenderPass
	{
		/// <summary>
		/// A camera rendering into a depth-only target: URP 14 renders it without a fresh <c>_CameraDepthTexture</c>, and
		/// there is no color to draw into.
		/// </summary>
		private static bool RendersDepthOnly(in CameraData cameraData)
			=> cameraData.targetTexture != null && cameraData.targetTexture.format == RenderTextureFormat.Depth;

		/// <summary>
		/// Pass data of this path's draws (not pooled: each draw executes before the next is filled).
		/// </summary>
		private DrawPassData _compatibilityDrawData;

		/// <summary>
		/// Draws this camera's lights: at full resolution, through the downsampled light buffer, or both (Large Only);
		/// only the composite when the early pass already drew the buffer.
		/// </summary>
		public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
		{
			ref var cameraData = ref renderingData.cameraData;
			var camera = cameraData.camera;

			// What the early pass (LightBufferTiming) already did for this camera, if anything.
			var prepared = _preparedCamera == camera;
			var early = prepared && _earlyCamera == camera;
			_preparedCamera = null;
			_earlyCamera = null;

			if (RendersDepthOnly(cameraData) || TryPrepareCamera(ref cameraData, prepared, out _) == false)
				return;

			LastRecordedLowResolutionPasses = false;
			LastRecordedLightBufferBeforeOpaques = early;

			var cmd = CommandBufferPool.Get();
			if (_lightBuffer == null)
				DrawFullResolution(cmd, camera, DrawSet.All);
			else if (early)
			{
				// The buffer is drawn already; small lights (LargeOnly) at full resolution, then the composite.
				if (SplitsLargeLights)
				{
					DrawFullResolution(cmd, camera, DrawSet.Small);
					LastRecordedLowResolutionPasses = true;
				}

				CompositeLightBuffer(cmd, ref cameraData);
			}
			else if (SplitsLargeLights == false)
				DrawThroughLightBuffer(cmd, ref cameraData, DrawSet.All, FulumaProfiling.DrawSampler);
			else
			{
				// LargeOnly: small lights at full resolution; large and camera-inside lights through the downsampled
				// buffer, unless the last culling of this camera found none (then the full-resolution draw has them all).
				var lowResolution = LowResolutionLightsLastFrame(camera);
				LastRecordedLowResolutionPasses = lowResolution;

				DrawFullResolution(cmd, camera, lowResolution ? DrawSet.Small : DrawSet.All);
				if (lowResolution)
					DrawThroughLightBuffer(cmd, ref cameraData, DrawSet.Large, FulumaProfiling.DrawLowResolutionSampler);
			}

			context.ExecuteCommandBuffer(cmd);
			CommandBufferPool.Release(cmd);
		}

		/// <summary>
		/// <see cref="LightBufferTiming"/> before the opaques: draws the light buffer's downsample and low-resolution lights
		/// from URP's depth prepass, right after the prepasses (called by <see cref="FulumaLightBufferEarlyPass"/>). The main
		/// pass then composites it. Without a prepass whose depth can be read now, nothing is drawn here and the main pass
		/// does everything after the opaques.
		/// </summary>
		internal void ExecuteLightBufferBeforeOpaques(ScriptableRenderContext context, ref RenderingData renderingData)
		{
			ref var cameraData = ref renderingData.cameraData;
			var camera = cameraData.camera;

			_preparedCamera = null;
			_earlyCamera = null;

			if (_lightBuffer == null || RendersDepthOnly(cameraData) || TryPrepareCamera(ref cameraData, false, out _) == false)
				return;

			_preparedCamera = camera;

			if (TryGetPrepassDepth(ref cameraData, out var prepassDepth, out var isAttachment) == false)
				return;

			if (SplitsLargeLights && LowResolutionLightsLastFrame(camera) == false)
				return;

			var cmd = CommandBufferPool.Get();
			DrawLowResolution(cmd, ref cameraData, prepassDepth,
				SplitsLargeLights ? DrawSet.Large : DrawSet.All,
				SplitsLargeLights ? FulumaProfiling.DrawLowResolutionSampler : FulumaProfiling.DrawSampler);
			BindCameraTargets(cmd, ref cameraData);
			context.ExecuteCommandBuffer(cmd);
			CommandBufferPool.Release(cmd);

			_earlyDepthIsAttachment = isAttachment;
			_earlyCamera = camera;
		}

		/// <summary>
		/// Where URP's depth prepass put this camera's depth when this runs (<see cref="UrpDepthPrepass.FindCompatibilityDepth"/>):
		/// the camera's depth attachment with depth priming (<paramref name="depth"/>), else <c>_CameraDepthTexture</c>
		/// (<paramref name="depth"/> null: the light buffer's shader reads it). With
		/// <see cref="URP.LightBufferTiming.BeforeOpaques"/> the early pass's own request makes the prepass.
		/// </summary>
		private bool TryGetPrepassDepth(ref CameraData cameraData, out RTHandle depth, out bool isAttachment)
		{
			var forced = _lightBufferTiming == LightBufferTiming.BeforeOpaques;
			var passNeedsDepthTexture = forced || (input & ScriptableRenderPassInput.Depth) != 0;
			var found = UrpDepthPrepass.FindCompatibilityDepth(RendererData, UrpDepthPrepass.CameraState.From(cameraData),
				passNeedsDepthTexture);

			if (found == UrpDepthPrepass.CompatibilityDepth.None && forced)
				found = UrpDepthPrepass.CompatibilityDepth.Texture;

			isAttachment = found == UrpDepthPrepass.CompatibilityDepth.Attachment;
			depth = isAttachment ? cameraData.renderer.cameraDepthTargetHandle : null;
			return found is UrpDepthPrepass.CompatibilityDepth.Texture or UrpDepthPrepass.CompatibilityDepth.Attachment;
		}

		/// <summary>
		/// Once per camera and frame, by whichever pass executes first: consumes the culling started for it (or starts
		/// it), sets the depth keywords and binds the mirror's buffers.
		/// </summary>
		/// <param name="alreadyPrepared">The early pass prepared this camera: only returns its MSAA sample count.</param>
		/// <returns>False when nothing is drawn for this camera.</returns>
		private bool TryPrepareCamera(ref CameraData cameraData, bool alreadyPrepared, out int msaaSamples)
		{
			msaaSamples = _preparedMsaaSamples;
			if (alreadyPrepared)
				return true;

			if (TryBeginPreparingCamera(out var culledCamera) == false)
				return false;

			msaaSamples = math.max(1, cameraData.cameraTargetDescriptor.msaaSamples);
			FinishPreparingCamera(msaaSamples, cameraData.camera, culledCamera, cameraData.GetViewMatrix(),
				cameraData.GetProjectionMatrix(), cameraData.worldSpaceCameraPos);
			return true;
		}

		/// <summary>
		/// The lights of <paramref name="drawSet"/> onto the camera's targets, which URP binds for a pass that configures
		/// none: its color, and its depth for the outside pass's depth test.
		/// </summary>
		private void DrawFullResolution(CommandBuffer cmd, Camera camera, DrawSet drawSet)
		{
			var data = _compatibilityDrawData ??= new DrawPassData();
			FillPassData(data, lowResolution: false, camera, drawSet);

			using (new ProfilingScope(cmd, FulumaProfiling.DrawSampler))
				ExecuteDraws(new CommandBufferDrawCommands(cmd), data, null);
		}

		/// <summary>
		/// Low-resolution depth (with scene depth), the lights of <paramref name="drawSet"/> into the light buffer, then the
		/// buffer onto the camera color.
		/// </summary>
		private void DrawThroughLightBuffer(CommandBuffer cmd, ref CameraData cameraData, DrawSet drawSet,
			ProfilingSampler drawSampler)
		{
			DrawLowResolution(cmd, ref cameraData, cameraData.renderer.cameraDepthTargetHandle, drawSet, drawSampler);
			CompositeLightBuffer(cmd, ref cameraData);
		}

		/// <summary>
		/// Low-resolution depth from <paramref name="sceneDepth"/> (none when the lights don't use scene depth), then the
		/// lights of <paramref name="drawSet"/> into the light buffer.
		/// </summary>
		/// <param name="sceneDepth">The camera's depth attachment, used where it can be sampled; else (or null) URP's
		/// <c>_CameraDepthTexture</c>.</param>
		private void DrawLowResolution(CommandBuffer cmd, ref CameraData cameraData, RTHandle sceneDepth, DrawSet drawSet,
			ProfilingSampler drawSampler)
		{
			var descriptor = cameraData.cameraTargetDescriptor;
			_lightBuffer.AllocateTargets(descriptor, _useSceneDepth);

			if (_useSceneDepth)
				_lightBuffer.ExecuteDownsample(cmd, descriptor.width, descriptor.height, sceneDepth);

			_lightBuffer.BeginLightDraw(cmd, _useSceneDepth);

			var data = _compatibilityDrawData ??= new DrawPassData();
			FillPassData(data, lowResolution: true, cameraData.camera, drawSet);

			using (new ProfilingScope(cmd, drawSampler))
				ExecuteDraws(new CommandBufferDrawCommands(cmd), data, _useSceneDepth ? _lightBuffer.LowResDepth : null);
		}

		/// <summary>
		/// The light buffer onto the camera color, then the camera's targets bound again.
		/// </summary>
		private void CompositeLightBuffer(CommandBuffer cmd, ref CameraData cameraData)
		{
			_lightBuffer.ExecuteComposite(cmd, cameraData.renderer.cameraColorTargetHandle);
			BindCameraTargets(cmd, ref cameraData);
		}

		/// <summary>
		/// URP 14 skips binding targets it believes are bound, so after switching to the light buffer's targets the passes
		/// after this one would otherwise draw into them.
		/// </summary>
		private static void BindCameraTargets(CommandBuffer cmd, ref CameraData cameraData)
		{
			var renderer = cameraData.renderer;
			CoreUtils.SetRenderTarget(cmd, renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);
		}

		private readonly struct CommandBufferDrawCommands : IDrawCommands
		{
			private readonly CommandBuffer _cmd;

			public CommandBufferDrawCommands(CommandBuffer cmd) => _cmd = cmd;

			public void DrawMeshInstancedProcedural(Mesh mesh, int submesh, Material material, int shaderPass, int count,
				MaterialPropertyBlock properties)
				=> _cmd.DrawMeshInstancedProcedural(mesh, submesh, material, shaderPass, count, properties);
		}
	}
}
#endif
