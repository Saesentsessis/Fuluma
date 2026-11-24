#if UNITY_6000_5_OR_NEWER && !FULUMA_LEGACY_LIFECYCLE
#define FULUMA_LIFECYCLE_ATTRIBUTES
#endif
using System.Collections.Generic;
using Saesentsessis.Fuluma.Rendering.Buffers;
using UnityEngine;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Rendering.Components
{
	/// <summary>
	/// One loop over every enabled <see cref="LinkedFakeLight"/>, right before the light data is uploaded
	/// (<see cref="GpuUploadEvents.BeforeUpload"/>), instead of an <c>Update</c> message per component.
	/// </summary>
	/// <remarks>
	/// Subscribed only while some component is enabled. In play mode it polls at most once per frame (the event fires
	/// once per render context, so several times per Editor frame); in edit mode every time, which is cheap there.
	/// <para>
	/// Statics across play-mode switches without a domain reload: the light list keeps itself right (Unity disables and
	/// re-enables every component on a switch, see <c>docs/dev/Lifecycle.md</c>), so it isn't cleared. Entering
	/// play mode drops destroyed entries and resets the once-per-frame guard, since <c>Time.frameCount</c> starts over
	/// (attribute on 6000.5+, <c>RuntimeInitializeOnLoadMethod</c> before).
	/// </para>
	/// </remarks>
#if UNITY_6000_5_OR_NEWER
	// The light list follows OnEnable/OnDisable across switches; the frame guard is reset explicitly on entering play mode.
	[NoAutoStaticsCleanup]
#endif
	internal static partial class LinkedFakeLightSystem
	{
		private static readonly List<LinkedFakeLight> Lights = new();
		private static int _lastPolledFrame = -1;
		private static bool _subscribed;

		public static int Count => Lights.Count;

		public static void Add(LinkedFakeLight light)
		{
			if (Lights.Contains(light))
				return;

			Lights.Add(light);

			if (_subscribed == false)
			{
				GpuUploadEvents.BeforeUpload += OnBeforeUpload;
				_subscribed = true;
			}
		}

		public static void Remove(LinkedFakeLight light)
		{
			var index = Lights.IndexOf(light);
			
			if (index < 0)
				return;

			// Order doesn't matter: swap with the last one.
			Lights[index] = Lights[^1];
			Lights.RemoveAt(Lights.Count - 1);

			if (Lights.Count == 0 && _subscribed)
			{
				GpuUploadEvents.BeforeUpload -= OnBeforeUpload;
				_subscribed = false;
			}
		}

		/// <summary>
		/// Polls every light whose sync mode asks for it now (tests call it directly).
		/// </summary>
		internal static void PollAll()
		{
			var isPlaying = Application.isPlaying;
			
			// Iterates backwards: a light that turns out destroyed removes itself.
			for (var i = Lights.Count - 1; i >= 0; i--)
			{
				var light = Lights[i];
				
				if (light == null)
				{
					Lights.RemoveAt(i);
					continue;
				}

				// Every Frame: always. Editor Only: only outside play mode (at runtime it waits for Refresh()).
				if (light.SyncMode == LinkedLightSyncMode.EveryFrame || isPlaying == false)
					light.Refresh();
			}
		}

		/// <summary>
		/// Forgets the last polled frame and drops destroyed entries (unsubscribing when none is left).
		/// </summary>
		internal static void ResetForNewSession()
		{
			_lastPolledFrame = -1;

			for (var i = Lights.Count - 1; i >= 0; i--)
				if (Lights[i] == null)
					Lights.RemoveAt(i);

			if (Lights.Count == 0 && _subscribed)
			{
				GpuUploadEvents.BeforeUpload -= OnBeforeUpload;
				_subscribed = false;
			}
		}

#if FULUMA_LIFECYCLE_ATTRIBUTES
		[OnEnteringPlayMode]
		private static void OnEnteringPlayMode() => ResetForNewSession();
#else
		// Runs on entering play mode (and once at player startup, where nothing is stale).
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		private static void OnEnteringPlayMode() => ResetForNewSession();
#endif

		private static void OnBeforeUpload()
		{
			if (Application.isPlaying)
			{
				if (Time.frameCount == _lastPolledFrame)
					return;

				_lastPolledFrame = Time.frameCount;
			}

			PollAll();
		}
	}
}
