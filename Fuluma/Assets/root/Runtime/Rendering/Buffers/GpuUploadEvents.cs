using System;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Rendering.Buffers
{
	/// <summary>
	/// Hooks around the per-frame upload of light data to the GPU (<see cref="SpotLightGpuMirror"/>), in play mode and in
	/// the Editor alike.
	/// </summary>
#if UNITY_6000_5_OR_NEWER
	// Its subscriber (LinkedFakeLightSystem) tracks its own subscription and must keep it across switches.
	[NoAutoStaticsCleanup]
#endif
	internal static class GpuUploadEvents
	{
		/// <summary>
		/// Raised at <c>RenderPipelineManager.beginContextRendering</c>, right before the mirrors upload: the last moment
		/// to change light data that this frame should draw. Raised once per render context, so several times per Editor
		/// frame (Game and Scene views), and only while some pass draws a store.
		/// </summary>
		public static event Action BeforeUpload;

		internal static void RaiseBeforeUpload() => BeforeUpload?.Invoke();
	}
}
