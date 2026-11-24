using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Data;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Saesentsessis.Fuluma.Rendering.Components
{
	/// <summary>
	/// A fake volumetric light that mirrors the Unity <see cref="Light"/> on the same GameObject: its type, color (with
	/// color temperature), intensity (times <see cref="IntensityMultiplier"/>), range, spot angles and enabled state.
	/// </summary>
	/// <remarks>
	/// <list type="bullet">
	/// <item>Spot lights become fake spot lights. Point, directional and area lights have no fake light. This is silent:
	/// no warning is logged, and <see cref="HasFakeLight"/> is false.</item>
	/// <item>The fake light exists only while the <see cref="Light"/> component is enabled.</item>
	/// <item>Values are copied as <see cref="SyncMode"/> says (every frame, or only in the Editor and on
	/// <see cref="Refresh"/>), by one shared loop (<c>LinkedFakeLightSystem</c>), right before the GPU upload. Only
	/// changes are pushed to the store.</item>
	/// <item>The transform works like every <see cref="FakeLightComponent"/>: static (moves on
	/// <see cref="FakeLightComponent.ApplyTransform"/>) or, with <see cref="FakeLightComponent.SyncTransform"/>, dynamic
	/// (follows the Light's Transform every frame).</item>
	/// </list>
	/// </remarks>
	[ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(Light)), AddComponentMenu("Light/Volumetric/Fake Light (Linked)")]
	public class LinkedFakeLight : FakeLightComponent
	{
		/// <summary>
		/// The fake light type a Unity light type maps to, or null when there is none (yet).
		/// </summary>
		private static FakeLightType? TypeOf(LightType type) => type == LightType.Spot ? FakeLightType.Spot : null;

		/// <summary>
		/// The Light's color as authored (gamma space, like <see cref="SpotLightData.Color"/>), tinted by its color
		/// temperature when the project and the light use one (the tint applies in linear space, as Unity's lighting
		/// does). Alpha is 1: the intensity carries the brightness.
		/// </summary>
		private static Color32 LightColor(Light light)
		{
			var color = light.color;

			if (GraphicsSettings.lightsUseColorTemperature && light.useColorTemperature)
				color = (color.linear * Mathf.CorrelatedColorTemperatureToRGB(light.colorTemperature)).gamma;

			color.a = 1f;
			return color;
		}

		[Tooltip("Every Frame: the Light's values are checked every frame and changes pushed. Editor Only: checked every frame in the Editor outside play mode; at runtime only on enable and when Refresh() is called (no per-frame cost).")]
		[SerializeField] private LinkedLightSyncMode syncMode = LinkedLightSyncMode.EveryFrame;

		[Tooltip("The fake light's intensity is the Light's intensity times this: a beam's brightness isn't tied to how brightly the Light lights surfaces.")]
		[SerializeField, Min(0f)] private float intensityMultiplier = 1f;

		private Light _light;

		/// <summary>
		/// The values last pushed to the store, to push only changes.
		/// </summary>
		private SpotLightData _pushedSpotLight;

		/// <summary>
		/// When the Light's values are copied.
		/// </summary>
		public LinkedLightSyncMode SyncMode
		{
			get => syncMode;
			set => syncMode = value;
		}

		/// <summary>
		/// The fake light's intensity per unit of the Light's intensity. Setting it pushes the change.
		/// </summary>
		public float IntensityMultiplier
		{
			get => intensityMultiplier;
			set
			{
				intensityMultiplier = math.max(0f, value);
				Refresh();
			}
		}

		/// <summary>
		/// The Unity Light this component mirrors.
		/// </summary>
		public Light Light => _light != null ? _light : _light = GetComponent<Light>();

		/// <summary>
		/// Whether the Light currently has a fake equivalent in a store (enabled and of a supported type).
		/// </summary>
		public bool HasFakeLight => TryGetRegistration(out _, out _);

		/// <summary>
		/// True while the Unity Light is enabled and of a type with a fake equivalent (currently spot only).
		/// </summary>
		protected override bool WantsRegistration
		{
			get
			{
				var light = Light;
				return light != null && light.enabled && TypeOf(light.type) is FakeLightType.Spot;
			}
		}

		/// <summary>
		/// Copies the Light's current values now: adds or removes the fake light when the Light was switched on or off
		/// or changed type, and pushes changed values. Called every frame in <see cref="LinkedLightSyncMode.EveryFrame"/>
		/// mode; call it after changing the Light from code in <see cref="LinkedLightSyncMode.EditorOnly"/> mode.
		/// </summary>
		public void Refresh()
		{
			if (isActiveAndEnabled == false)
				return;

			// Added, removed or re-registered: a new registration already read every value.
			if (UpdateRegistration())
				return;

			if (TryGetRegistration(out var store, out var handle) == false)
				return;

			var spotLight = ReadSpotLight();
			if (spotLight.Equals(_pushedSpotLight))
				return;

			_pushedSpotLight = spotLight;
			store.SetSpotLight(handle, spotLight);
		}

		/// <summary>
		/// Registers the light (if the Unity Light is enabled and a spot light) and starts polling the Light.
		/// </summary>
		protected override void OnEnable()
		{
			_light = GetComponent<Light>();
			base.OnEnable();
			LinkedFakeLightSystem.Add(this);
		}

		/// <summary>
		/// Stops polling the Light and removes the fake light from its store.
		/// </summary>
		protected override void OnDisable()
		{
			LinkedFakeLightSystem.Remove(this);
			base.OnDisable();
		}

		/// <summary>
		/// False once the Unity Light's type no longer maps to the registered fake light type.
		/// </summary>
		protected override bool MatchesRegistration(in LightHandle handle) => handle.Type == TypeOf(Light.type);

		/// <inheritdoc/>
		protected override LightHandle Register(LightStore store, LightTransformMode mode)
		{
			_pushedSpotLight = ReadSpotLight();

			return mode == LightTransformMode.Dynamic
				? store.AddSpotLight(transform, _pushedSpotLight)
				: store.AddSpotLight(Placement.From(transform), _pushedSpotLight);
		}

		/// <inheritdoc/>
		protected override void UpdateData(LightStore store, in LightHandle handle)
		{
			_pushedSpotLight = ReadSpotLight();
			store.SetSpotLight(handle, _pushedSpotLight);
		}

		/// <inheritdoc/>
		protected override void UpdateTransform(LightStore store, in LightHandle handle)
		{
			store.SetSpotLightPlacement(handle, Placement.From(transform));
		}

		/// <summary>
		/// The Light's values as fake spot light data. Unity's spot angles are full cone angles, like
		/// <see cref="SpotLightData"/>'s (the outer one is clamped to 160 degrees when packed).
		/// </summary>
		private SpotLightData ReadSpotLight()
		{
			var light = Light;

			return new SpotLightData
			{
				Color = LightColor(light),
				Intensity = light.intensity * intensityMultiplier,
				Range = light.range,
				InnerAngleDegrees = light.innerSpotAngle,
				OuterAngleDegrees = light.spotAngle
			};
		}
	}
}
