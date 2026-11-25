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
		/// <see cref="LightBufferTiming"/> before the opaques (called by <see cref="FulumaLightBufferEarlyPass"/>). Not
		/// implemented on this path yet: the main pass draws everything after the opaques.
		/// </summary>
		internal void ExecuteLightBufferBeforeOpaques(ScriptableRenderContext context, ref RenderingData renderingData)
		{
			_preparedCamera = null;
			_earlyCamera = null;
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
			DrawLowResolution(cmd, ref cameraData, drawSet, drawSampler);
			CompositeLightBuffer(cmd, ref cameraData);
		}

		/// <summary>
		/// Low-resolution depth from the camera's depth (none when the lights don't use scene depth), then the lights of
		/// <paramref name="drawSet"/> into the light buffer.
		/// </summary>
		private void DrawLowResolution(CommandBuffer cmd, ref CameraData cameraData, DrawSet drawSet,
			ProfilingSampler drawSampler)
		{
			var descriptor = cameraData.cameraTargetDescriptor;
			_lightBuffer.AllocateTargets(descriptor, _useSceneDepth);

			if (_useSceneDepth)
				_lightBuffer.ExecuteDownsample(cmd, descriptor.width, descriptor.height, cameraData.renderer.cameraDepthTargetHandle);

			_lightBuffer.BeginLightDraw(cmd, _useSceneDepth);

			var data = _compatibilityDrawData ??= new DrawPassData();
			FillPassData(data, lowResolution: true, cameraData.camera, drawSet);

			using (new ProfilingScope(cmd, drawSampler))
				ExecuteDraws(new CommandBufferDrawCommands(cmd), data, _useSceneDepth ? _lightBuffer.LowResDepth : null);
		}

		/// <summary>
		/// The light buffer onto the camera color, then the camera's targets bound again: URP 14 skips binding targets it
		/// believes are bound, so the passes after this one would otherwise draw into the light buffer's.
		/// </summary>
		private void CompositeLightBuffer(CommandBuffer cmd, ref CameraData cameraData)
		{
			var renderer = cameraData.renderer;
			var cameraColor = renderer.cameraColorTargetHandle;
			_lightBuffer.ExecuteComposite(cmd, cameraColor);
			CoreUtils.SetRenderTarget(cmd, cameraColor, renderer.cameraDepthTargetHandle);
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
