#if !UNITY_6000_3_OR_NEWER
#define FULUMA_COMPATIBILITY_PATH
#endif

#if FULUMA_COMPATIBILITY_PATH
using Saesentsessis.Fuluma.Profiling;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// URP 17 (Unity 6.0-6.2) marks the Compatibility Mode members obsolete; URP 14 (2022.3) has no other path.
#pragma warning disable 618, 672

namespace Saesentsessis.Fuluma.Rendering.URP
{
	/// <summary>
	/// The pass's Compatibility path: URP 14 (Unity 2022.3), and URP 17 with Compatibility Mode on (Unity 6.0-6.2). URP
	/// calls <see cref="Execute"/> instead of recording a render graph; the draws go into a command buffer executed at
	/// once.
	/// </summary>
	/// <remarks>
	/// Scene depth always comes from URP's <c>_CameraDepthTexture</c> (<see cref="NeedsCameraDepthTexture"/>): the
	/// Compatibility path has no depth input attachment, and doesn't read the depth attachment as a texture.
	/// </remarks>
	internal partial class FulumaSpotLightRenderPass
	{
		/// <summary>
		/// Pass data of the full-resolution draws (not pooled: the draws execute right away).
		/// </summary>
		private DrawPassData _compatibilityDrawData;

		/// <summary>
		/// Draws this camera's lights.
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

			if (TryPrepareCamera(ref cameraData, prepared, out _) == false)
				return;

			LastRecordedLowResolutionPasses = false;
			LastRecordedLightBufferBeforeOpaques = early;

			var cmd = CommandBufferPool.Get();
			DrawFullResolution(cmd, camera, DrawSet.All);
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
