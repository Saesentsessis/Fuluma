#if UNITY_6000_0_OR_NEWER
#define FULUMA_RENDER_GRAPH
#endif
#if !UNITY_6000_3_OR_NEWER || (URP_COMPATIBILITY_MODE && !UNITY_6000_4_OR_NEWER)
#define FULUMA_COMPATIBILITY_PATH
#endif

using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if FULUMA_RENDER_GRAPH
using UnityEngine.Rendering.RenderGraphModule;
#endif

#if FULUMA_COMPATIBILITY_PATH
// URP 17 (Unity 6.0-6.3) marks the Compatibility Mode members obsolete; URP 14 (2022.3) has no other path.
#pragma warning disable 618, 672
#endif

namespace Saesentsessis.Fuluma.Rendering.URP
{
	/// <summary>
	/// <see cref="FulumaSpotLightRenderPass.LightBufferTiming"/>: records the downsampled light buffer (depth downsample and
	/// low-resolution light draw) right after URP's prepasses, from the depth prepass. With
	/// <see cref="LightBufferTiming.BeforeOpaques"/> it asks for the depth texture this early, which makes URP render that
	/// prepass; with <see cref="LightBufferTiming.Auto"/> it asks for nothing and only uses a prepass URP renders anyway.
	/// The owner pass then only composites the buffer after the opaques, inside URP's opaque native render pass instead of
	/// splitting it (on tile-based GPUs the split stores and reloads the camera's color and depth).
	/// </summary>
	internal sealed class FulumaLightBufferEarlyPass : ScriptableRenderPass
	{
		private readonly FulumaSpotLightRenderPass _owner;

		public FulumaLightBufferEarlyPass(FulumaSpotLightRenderPass owner)
		{
			_owner = owner;
			renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
		}

		/// <summary>
		/// Asks URP for the depth texture before the opaques (which makes it render a depth prepass), or for nothing.
		/// </summary>
		public void RequestDepthPrepass(bool request)
			=> ConfigureInput(request ? ScriptableRenderPassInput.Depth : ScriptableRenderPassInput.None);

#if FULUMA_RENDER_GRAPH
		public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
			=> _owner.RecordLightBufferBeforeOpaques(renderGraph, frameData);
#endif

#if FULUMA_COMPATIBILITY_PATH
		public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
			=> _owner.ExecuteLightBufferBeforeOpaques(context, ref renderingData);
#endif
	}
}
