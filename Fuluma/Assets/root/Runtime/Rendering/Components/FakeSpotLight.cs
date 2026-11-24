using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Data;
using UnityEngine;

namespace Saesentsessis.Fuluma.Rendering.Components
{
	/// <summary>
	/// A fake volumetric spot light: a glowing cone along this <see cref="Transform"/>'s forward (+Z) axis, drawn by the
	/// Fuluma renderer feature. It needs no Unity <see cref="Light"/>; to mirror an existing one, use
	/// <see cref="LinkedFakeLight"/> instead.
	/// </summary>
	/// <remarks>
	/// Static by default (see <see cref="FakeLightComponent.SyncTransform"/>): call
	/// <see cref="FakeLightComponent.ApplyTransform"/> after moving it, and <see cref="FakeLightComponent.ApplyData"/>
	/// (or set <see cref="Data"/>) after changing its parameters from code.
	/// </remarks>
	[ExecuteAlways, DisallowMultipleComponent, AddComponentMenu("Light/Volumetric/Fake Spot Light")]
	public class FakeSpotLight : FakeLightComponent
	{
		[Tooltip("Color, intensity, range and cone angles of the light.")]
		[SerializeField] private SpotLightData data = SpotLightData.Default;

		/// <summary>
		/// Light parameters. Setting the property pushes them to the store immediately.
		/// </summary>
		public SpotLightData Data
		{
			get => data;
			set
			{
				data = value;
				ApplyData();
			}
		}

		/// <inheritdoc/>
		protected override LightHandle Register(LightStore store, LightTransformMode mode)
		{
			return mode == LightTransformMode.Dynamic
				? store.AddSpotLight(transform, data)
				: store.AddSpotLight(Placement.From(transform), data);
		}

		/// <inheritdoc/>
		protected override void UpdateData(LightStore store, in LightHandle handle)
		{
			store.SetSpotLight(handle, data);
		}

		/// <inheritdoc/>
		protected override void UpdateTransform(LightStore store, in LightHandle handle)
		{
			store.SetSpotLightPlacement(handle, Placement.From(transform));
		}
	}
}
