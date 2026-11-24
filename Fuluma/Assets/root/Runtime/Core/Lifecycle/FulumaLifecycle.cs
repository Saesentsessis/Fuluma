#if UNITY_6000_5_OR_NEWER && !FULUMA_LEGACY_LIFECYCLE
#define FULUMA_LIFECYCLE_ATTRIBUTES
#endif
using System.Collections.Generic;
#if UNITY_EDITOR && FULUMA_LIFECYCLE_ATTRIBUTES
using UnityEditor.Scripting.LifecycleManagement;
#endif
using UnityEngine;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Core.Lifecycle
{
	/// <summary>
	/// Keeps fake lights alive across Editor play-mode switches, with or without a domain reload.
	/// </summary>
	/// <remarks>
	/// <b>Leaving a mode</b> (edit → play or play → edit): every <see cref="LightStore"/> is released, so no
	/// native memory or handle crosses the switch.
	/// <para>
	/// <b>Entering a mode</b>: every enabled <see cref="ILightStoreClient"/> registers again.
	/// </para>
	/// Unity 6000.7 also cycles <c>OnDisable</c>/<c>OnEnable</c> on loaded objects during the switch even when
	/// scene reload is disabled, so clients usually come back on their own; the restore pass guarantees it and is
	/// a no-op otherwise. Measured callback order is documented in <c>docs/dev/Lifecycle.md</c>.
	/// <list type="table">
	/// <item><term>Unity 6000.5+</term><description>Leave: <c>[OnExitingEditMode]</c>, <c>[OnExitingPlayMode]</c>.
	/// Enter: <c>[OnEnteringEditMode]</c> (fires after the edit scene is restored) and
	/// <c>RuntimeInitializeOnLoadMethod(AfterSceneLoad)</c> (there is no "entered play mode" attribute, and
	/// <c>[OnEnteringPlayMode]</c> fires while scene objects are still disabled).</description></item>
	/// <item><term>Fallback</term><description><c>EditorApplication.playModeStateChanged</c>: <c>Exiting*</c> to leave,
	/// <c>Entered*</c> to enter. Define <c>FULUMA_LEGACY_LIFECYCLE</c> to force this path on 6000.5+.</description></item>
	/// </list>
	/// Player builds never switch modes, so nothing here runs outside the Editor except the client list itself.
	/// </remarks>
#if UNITY_6000_5_OR_NEWER
	// The client registry must survive play-mode switches (its whole job).
	[NoAutoStaticsCleanup]
#endif
	internal static partial class FulumaLifecycle
	{
#if FULUMA_LIFECYCLE_ATTRIBUTES
		internal const bool UsesLifecycleAttributes = true;
#else
		internal const bool UsesLifecycleAttributes = false;
#endif

		// Must survive play-mode switches without a domain reload: enabled components stay in it.
		private static readonly List<ILightStoreClient> Clients = new();

		internal static int ClientCount => Clients.Count;

		internal static void AddClient(ILightStoreClient client)
		{
			if (Clients.Contains(client) == false)
				Clients.Add(client);
		}

		internal static void RemoveClient(ILightStoreClient client)
		{
			var index = Clients.IndexOf(client);
			if (index < 0)
				return;

			// Order does not matter, so swap-back keeps removal O(1) after the lookup.
			Clients[index] = Clients[^1];
			Clients.RemoveAt(Clients.Count - 1);
		}

		/// <summary>
		/// Called when a mode is being left. Releases every store.
		/// </summary>
		internal static void OnLeavingMode()
		{
			LightStore.ReleaseAll();
		}

		/// <summary>
		/// Called once the new mode is entered. Every enabled client registers again if its handle died.
		/// </summary>
		internal static void OnEnteredMode()
		{
			// Index loop: restoring never adds or removes clients, but it must not allocate an enumerator either.
			for (var i = 0; i < Clients.Count; i++)
				Clients[i].RestoreRegistration();
		}

#if UNITY_EDITOR
#if FULUMA_LIFECYCLE_ATTRIBUTES
		[OnExitingEditMode]
		private static void OnExitingEditMode() => OnLeavingMode();

		[OnExitingPlayMode]
		private static void OnExitingPlayMode() => OnLeavingMode();

		[OnEnteringEditMode]
		private static void OnEnteringEditMode() => OnEnteredMode();

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
		private static void OnPlayModeSceneLoaded() => OnEnteredMode();
#else
		[UnityEditor.InitializeOnLoadMethod]
		private static void SubscribeToPlayModeChanges()
		{
			UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
			UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
		}

		private static void OnPlayModeStateChanged(UnityEditor.PlayModeStateChange state)
		{
			switch (state)
			{
				case UnityEditor.PlayModeStateChange.ExitingEditMode:
				case UnityEditor.PlayModeStateChange.ExitingPlayMode:
					OnLeavingMode();
					break;

				case UnityEditor.PlayModeStateChange.EnteredEditMode:
				case UnityEditor.PlayModeStateChange.EnteredPlayMode:
					OnEnteredMode();
					break;
			}
		}
#endif // FULUMA_LIFECYCLE_ATTRIBUTES
#endif // UNITY_EDITOR
	}
}
