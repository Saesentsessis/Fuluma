using Saesentsessis.Fuluma.Rendering.Components;
using UnityEditor;
using UnityEngine;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Editor.Components
{
#if UNITY_6000_5_OR_NEWER
	// Immutable: status reasons.
	[NoAutoStaticsCleanup]
#endif
	/// <summary>
	/// Inspector of every <see cref="FakeLightComponent"/>: a <see cref="RegistrationStatus"/> box on top (whether and
	/// where the light is registered, which renderers draw its store), then the default fields. Editors of specific
	/// light types derive from it. With several objects selected the status is left out.
	/// </summary>
	[CustomEditor(typeof(FakeLightComponent), true), CanEditMultipleObjects]
	internal class FakeLightComponentEditor : UnityEditor.Editor
	{
		private static readonly RegistrationStatus.Reason NoLight = new(
			"Not registered: there is no Light on this GameObject.", MessageType.Warning);

		private static readonly RegistrationStatus.Reason LightOff = new(
			"Not registered: the linked Light is disabled; the fake light exists only while it is on.", MessageType.Info);

		private static readonly RegistrationStatus.Reason UnsupportedType = new(
			"Not registered: only Spot lights have a fake equivalent so far.", MessageType.Info);

		private static RegistrationStatus.Reason ReasonOf(FakeLightComponent light)
		{
			if (EditorUtility.IsPersistent(light))
				return RegistrationStatus.PrefabAsset;

			if (light.isActiveAndEnabled == false)
				return RegistrationStatus.Disabled;

			if (light is not LinkedFakeLight linked)
				return RegistrationStatus.Unknown;

			var unityLight = linked.Light;
			if (unityLight == null)
				return NoLight;

			if (unityLight.enabled == false)
				return LightOff;

			return unityLight.type != LightType.Spot ? UnsupportedType : RegistrationStatus.Unknown;
		}

		private readonly RegistrationStatus _status = new();

		/// <inheritdoc/>
		public override void OnInspectorGUI()
		{
			if (serializedObject.isEditingMultipleObjects == false)
				DrawStatus((FakeLightComponent)target);

			base.OnInspectorGUI();
		}

		private void DrawStatus(FakeLightComponent light)
		{
			var registered = light.TryGetRegistration(out var store, out var handle);
			var reason = registered ? default : ReasonOf(light);

			_status.Draw(RegistrationStatus.Subject.Light, light.OverrideStore, registered, store, handle.Mode, reason);
		}
	}
}
