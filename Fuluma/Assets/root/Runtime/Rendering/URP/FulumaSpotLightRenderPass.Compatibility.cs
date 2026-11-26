#if !UNITY_6000_3_OR_NEWER || (URP_COMPATIBILITY_MODE && !UNITY_6000_4_OR_NEWER)
#define FULUMA_COMPATIBILITY_PATH
#endif

#if FULUMA_COMPATIBILITY_PATH
using Saesentsessis.Fuluma.Profiling;
using Saesentsessis.Fuluma.Utils;
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
	/// Full-resolution draws read scene depth from URP's <c>_CameraDepthTexture</c> (<see cref="NeedsCameraDepthTexture"/>;
	/// no depth input attachment on this path); the light buffer reads the camera's depth attachment where it can be
	/// sampled, else URP's depth texture (<see cref="DownsampledLightBuffer"/>). The light buffer is drawn with the camera's own projection; for a
	/// camera rendering straight to the backbuffer (unflipped) the composite samples it upside down
	/// (<see cref="CompositeLightBuffer"/>).
	/// </remarks>
	internal partial class FulumaSpotLightRenderPass
	{
		/// <summary>
		/// Pass data of this path's draws (not pooled: each draw executes before the next is filled).
		/// </summary>
		private DrawPassData _compatibilityDrawData;

		/// <summary>
		/// Draws this camera's lights: at full resolution, through the downsampled light buffer, or both (Large Only);
		/// only the composite when the early pass already drew the buffer.
		/// </summary>
		/// <remarks>
		/// Unlike a render graph recording, <c>Execute</c> fills the command buffer right before it runs, so it waits for
		/// this camera's culling first and records only what it found: nothing without visible lights, and the light
		/// buffer's passes (downsample, clear, composite: whole-screen work) only when lights go through it.
		/// </remarks>
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

			// The early pass only draws the buffer when there are lights for it, so it always needs its composite.
			var culler = FinishCulling(camera);
			if (culler == null && early == false)
				return;

			// Large Only: small lights at full resolution, large and camera-inside lights through the buffer.
			var bufferLights = _lightBuffer != null && (early || SplitsLargeLights == false || culler.LowResolutionLights > 0);
			if (bufferLights && early == false && CanSampleSceneDepth(ref cameraData) == false)
				return;

			var cmd = CommandBufferPool.Get();
			if (_lightBuffer == null || SplitsLargeLights)
				DrawFullResolution(cmd, camera, bufferLights ? DrawSet.Small : DrawSet.All);

			if (early)
				CompositeLightBuffer(cmd, ref cameraData);
			else if (bufferLights)
			{
				DrawThroughLightBuffer(cmd, ref cameraData, SplitsLargeLights ? DrawSet.Large : DrawSet.All,
					SplitsLargeLights ? FulumaProfiling.DrawLowResolutionSampler : FulumaProfiling.DrawSampler);
			}

			LastRecordedLowResolutionPasses = bufferLights;
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

			if (_lightBuffer == null || TryPrepareCamera(ref cameraData, false, out _) == false)
				return;

			_preparedCamera = camera;

			if (TryGetPrepassDepth(ref cameraData, out var prepassDepth, out var isAttachment) == false)
				return;

			// Only with lights for the buffer (see Execute); otherwise the main pass draws whatever is visible.
			var culler = FinishCulling(camera);
			if (culler == null || (SplitsLargeLights && culler.LowResolutionLights == 0))
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
		/// Whether URP certainly renders this camera into an intermediate depth texture, which the light buffer samples
		/// itself (single-sampled) instead of URP's depth copy. In URP's Compatibility Mode setup (URP 14 and 17) a base
		/// camera with an intermediate color target always gets an intermediate depth too; only conditions that force the
		/// intermediate color are trusted: this pass's <c>requiresIntermediateTexture</c> (URP 17, with the light buffer), or
		/// on URP 14 HDR or the opaque texture. Scene view cameras (their filtering mode can draw straight to the view) and
		/// cameras with a target texture are left to URP's depth copy.
		/// </summary>
		private bool RendersIntoIntermediateDepth(ref CameraData cameraData)
		{
			if (cameraData.renderType != CameraRenderType.Base || cameraData.isSceneViewCamera || cameraData.targetTexture != null)
				return false;

#if UNITY_6000_0_OR_NEWER
			return _lightBuffer != null;
#else
			return cameraData.isHdrEnabled || cameraData.requiresOpaqueTexture;
#endif
		}

		/// <summary>
		/// Whether the light buffer can read this camera's scene depth: always when it doesn't use any or URP makes its
		/// depth texture for this camera, else only from a depth attachment it can sample. When
		/// <see cref="ConfigureDepthSource"/> wrongly expected one, every later camera asks URP for the depth texture and
		/// this camera's buffer is skipped, once.
		/// </summary>
		private bool CanSampleSceneDepth(ref CameraData cameraData)
		{
			if (_useSceneDepth == false || _depthTextureRequested
			    || DownsampledLightBuffer.IsSampleable(cameraData.renderer.cameraDepthTargetHandle))
				return true;

			_alwaysRequestDepthTexture = true;
			Debug.LogWarning(StringUtils.PrependFulumaPrefix(UnexpectedBackbufferDepthMessage));
			return false;
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
			var flip = SystemInfo.graphicsUVStartsAtTop && cameraData.IsCameraProjectionMatrixFlipped() == false;
			_lightBuffer.ExecuteComposite(cmd, cameraData.renderer.cameraColorTargetHandle, flip);
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
