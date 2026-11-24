#if UNITY_6000_5_OR_NEWER && !FULUMA_LEGACY_LIFECYCLE
#define FULUMA_LIFECYCLE_ATTRIBUTES
#endif
using System;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Profiling
{
	/// <summary>
	/// Profiler markers of the spot light pipeline, readable with <see cref="ProfilerRecorder"/> by name (category
	/// <see cref="ProfilerCategory.Render"/>). Samples only exist where the profiler is compiled in: the Editor and
	/// development players.
	/// </summary>
	/// <remarks>
	/// The markers and samplers are immutable and live for the whole domain. The last-camera statistics are reset when
	/// play mode is entered or left (attributes on 6000.5+, <c>RuntimeInitializeOnLoadMethod</c> and
	/// <c>playModeStateChanged</c> before), so a session never shows the previous one's numbers.
	/// </remarks>
#if UNITY_6000_5_OR_NEWER
	// Markers and samplers are immutable; the statistics are reset explicitly on play-mode switches.
	[NoAutoStaticsCleanup]
#endif
	public static partial class FulumaProfiling
	{
		/// <summary>
		/// Main thread, once per frame: the GPU mirror's upload of changed static and dynamic data, and scheduling the
		/// dynamic transform sync and copy.
		/// </summary>
		public const string MirrorUploadName = "Fuluma.SpotLights.MirrorUpload";

		/// <summary>
		/// Main thread: waiting for the dynamic transform sync and copy, and unlocking the buffer.
		/// </summary>
		public const string MirrorCompleteName = "Fuluma.SpotLights.MirrorComplete";

		/// <summary>
		/// Main thread, per camera: setting up and scheduling culling.
		/// </summary>
		public const string CullingScheduleName = "Fuluma.SpotLights.CullingSchedule";

		/// <summary>
		/// Main thread, per camera: syncing the per-pool, per-clip-class materials and binding their buffers.
		/// </summary>
		public const string MaterialBindingName = "Fuluma.SpotLights.MaterialBinding";

		/// <summary>
		/// Worker threads: the culling job, summed over every batch and thread.
		/// </summary>
		public const string CullingJobName = "Fuluma.SpotLights.CullingJob";

		/// <summary>
		/// Main thread, per camera: waiting for the culling jobs when the draw executes.
		/// </summary>
		public const string CullingWaitName = "Fuluma.SpotLights.CullingWait";

		/// <summary>
		/// Main thread, per camera: uploading the visible light indices.
		/// </summary>
		public const string CullingUploadName = "Fuluma.SpotLights.CullingUpload";

		/// <summary>
		/// The draw pass. Has GPU timing where <c>SystemInfo.supportsGpuRecorder</c>: set
		/// <see cref="ProfilingSampler.enableRecording"/> and read <see cref="ProfilingSampler.gpuElapsedTime"/> and
		/// <see cref="ProfilingSampler.cpuElapsedTime"/>.
		/// </summary>
		public const string DrawName = "Fuluma Draw SpotLightPass (Instanced)";

		/// <summary>
		/// With <c>LightBufferLights.LargeOnly</c>: the draw pass of the large and camera-inside lights, into the
		/// downsampled buffer (sampler, GPU timing like <see cref="DrawName"/>). <see cref="DrawName"/> then covers the
		/// full-resolution lights only.
		/// </summary>
		public const string DrawLowResolutionName = "Fuluma Draw SpotLightPass Low Resolution (Instanced)";

		/// <summary>
		/// Downsampled light buffer: the camera depth reduced to the buffer's size (sampler, GPU timing like
		/// <see cref="DrawName"/>).
		/// </summary>
		public const string DownsampleDepthName = "Fuluma Downsample Depth";

		/// <summary>
		/// Downsampled light buffer: upscaling it onto the camera color (sampler, GPU timing like <see cref="DrawName"/>).
		/// </summary>
		public const string CompositeName = "Fuluma Composite Light Buffer";

		internal static readonly ProfilerMarker MirrorUpload = new(ProfilerCategory.Render, MirrorUploadName);
		internal static readonly ProfilerMarker MirrorComplete = new(ProfilerCategory.Render, MirrorCompleteName);
		internal static readonly ProfilerMarker CullingSchedule = new(ProfilerCategory.Render, CullingScheduleName);
		internal static readonly ProfilerMarker MaterialBinding = new(ProfilerCategory.Render, MaterialBindingName);
		internal static readonly ProfilerMarker CullingJob = new(ProfilerCategory.Render, CullingJobName);
		internal static readonly ProfilerMarker CullingWait = new(ProfilerCategory.Render, CullingWaitName);
		internal static readonly ProfilerMarker CullingUpload = new(ProfilerCategory.Render, CullingUploadName);

		/// <summary>
		/// Written by the pass: lights per proxy LOD of the last culled camera (see <see cref="LastSpotLightsPerLod"/>).
		/// </summary>
		internal static readonly int[] SpotLightsPerLod = new int[8];

		/// <summary>
		/// Sampler of the draw pass (see <see cref="DrawName"/>).
		/// </summary>
		public static ProfilingSampler DrawSampler { get; } = new(DrawName);

		/// <summary>
		/// Sampler of the low-resolution draw pass (see <see cref="DrawLowResolutionName"/>).
		/// </summary>
		public static ProfilingSampler DrawLowResolutionSampler { get; } = new(DrawLowResolutionName);

		/// <summary>
		/// Sampler of the depth downsample pass (see <see cref="DownsampleDepthName"/>).
		/// </summary>
		public static ProfilingSampler DownsampleDepthSampler { get; } = new(DownsampleDepthName);

		/// <summary>
		/// Sampler of the composite pass (see <see cref="CompositeName"/>).
		/// </summary>
		public static ProfilingSampler CompositeSampler { get; } = new(CompositeName);

		/// <summary>
		/// Lights the last culled camera draws, after the cap.
		/// </summary>
		public static int LastVisibleSpotLights { get; internal set; }

		/// <summary>
		/// Proxy triangles the last culled camera draws for its lights (every LOD and pass).
		/// </summary>
		public static long LastSpotLightTriangles { get; internal set; }

		/// <summary>
		/// Lights the last culled camera draws with each proxy LOD (index = LOD), inside-pass lights counted as LOD0.
		/// Entries past the LOD count are 0.
		/// </summary>
		public static ReadOnlySpan<int> LastSpotLightsPerLod => SpotLightsPerLod;

		/// <summary>
		/// Clears the last-camera statistics (the markers and samplers stay).
		/// </summary>
		internal static void ResetStatistics()
		{
			LastVisibleSpotLights = 0;
			LastSpotLightTriangles = 0;
			Array.Clear(SpotLightsPerLod, 0, SpotLightsPerLod.Length);
		}

#if FULUMA_LIFECYCLE_ATTRIBUTES
		[OnEnteringPlayMode]
		private static void OnEnteringPlayMode() => ResetStatistics();

		[OnExitingPlayMode]
		private static void OnExitingPlayMode() => ResetStatistics();
#else
		// Runs on entering play mode (and once at player startup, where the statistics are empty anyway).
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		private static void OnEnteringPlayMode() => ResetStatistics();

#if UNITY_EDITOR
		[UnityEditor.InitializeOnLoadMethod]
		private static void SubscribeToPlayModeChanges()
		{
			UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
			UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
		}

		private static void OnPlayModeStateChanged(UnityEditor.PlayModeStateChange state)
		{
			if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode)
				ResetStatistics();
		}
#endif // UNITY_EDITOR
#endif // FULUMA_LIFECYCLE_ATTRIBUTES
	}
}
