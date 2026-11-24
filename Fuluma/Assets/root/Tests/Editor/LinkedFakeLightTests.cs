using System.Collections.Generic;
using NUnit.Framework;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Data;
using Saesentsessis.Fuluma.Rendering.Components;
using UnityEngine;

namespace Saesentsessis.Fuluma.Tests.Editor
{
	/// <summary>
	/// <see cref="LinkedFakeLight"/>: a fake light mirroring the Unity Light on its GameObject. Edit mode, so
	/// <see cref="LinkedFakeLightSystem.PollAll"/> stands in for the per-frame poll before the GPU upload (and both sync
	/// modes poll).
	/// </summary>
	public class LinkedFakeLightTests
	{
		/// <summary>
		/// Packed the way the store packs (Burst), so the comparison is exact.
		/// </summary>
		private static PackedSpotLightData Packed(in SpotLightData light)
		{
			PackingKernels.PackSpotLight(light, out var packed);
			return packed;
		}

		private static SpotLightData Expected(Light light, float multiplier)
		{
			var color = light.color;
			color.a = 1f;

			return new SpotLightData
			{
				Color = color,
				Intensity = light.intensity * multiplier,
				Range = light.range,
				InnerAngleDegrees = light.innerSpotAngle,
				OuterAngleDegrees = light.spotAngle
			};
		}

		private readonly List<Object> _created = new();
		private LightStore _store;

		[SetUp]
		public void SetUp()
		{
			_store = Track(ScriptableObject.CreateInstance<LightStore>());
		}

		[TearDown]
		public void TearDown()
		{
			for (var i = _created.Count - 1; i >= 0; i--)
				if (_created[i] != null)
					Object.DestroyImmediate(_created[i]);

			_created.Clear();
		}

		[Test]
		public void Registers_WithTheLightsValues_TimesTheMultiplier()
		{
			var (light, linked) = CreateLinkedSpotLight();
			linked.IntensityMultiplier = 2f;
			light.gameObject.SetActive(true);

			Assert.AreEqual(1, _store.StaticSpotLightCount);
			Assert.IsTrue(linked.HasFakeLight);
			Assert.AreEqual(Packed(Expected(light, 2f)), _store.GetSpotLights(LightTransformMode.Static)[0]);
		}

		[Test]
		public void EveryFrame_PushesChangedValues_OnTheNextPoll()
		{
			var (light, linked) = CreateLinkedSpotLight();
			light.gameObject.SetActive(true);

			light.range = 11f;
			light.spotAngle = 70f;
			light.innerSpotAngle = 20f;
			light.color = new Color(0.2f, 0.4f, 0.9f);
			light.intensity = 5f;
			Assert.AreEqual(7f, _store.GetSpotLights(LightTransformMode.Static)[0].Range, "Nothing is pushed before the poll.");

			LinkedFakeLightSystem.PollAll();

			Assert.AreEqual(Packed(Expected(light, 1f)), _store.GetSpotLights(LightTransformMode.Static)[0]);
			Assert.AreEqual(1, _store.StaticSpotLightCount);
		}

		[Test]
		public void UnchangedValues_AreNotPushedAgain()
		{
			var (light, _) = CreateLinkedSpotLight();
			light.gameObject.SetActive(true);

			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out _, out _);
			LinkedFakeLightSystem.PollAll();
			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out _, out var lights);
			Assert.AreEqual(0, lights.Length, "An unchanged light shouldn't be marked for upload.");

			light.range = 9f;
			LinkedFakeLightSystem.PollAll();
			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out _, out lights);
			Assert.AreEqual(1, lights.Length, "A changed light should be marked for upload.");
		}

		[Test]
		public void DisablingTheLight_RemovesTheFakeLight_AndEnablingBringsItBack()
		{
			var (light, linked) = CreateLinkedSpotLight();
			light.gameObject.SetActive(true);

			light.enabled = false;
			LinkedFakeLightSystem.PollAll();
			Assert.AreEqual(0, _store.SpotLightCount);
			Assert.IsFalse(linked.HasFakeLight);

			light.enabled = true;
			LinkedFakeLightSystem.PollAll();
			Assert.AreEqual(1, _store.StaticSpotLightCount);
			Assert.AreEqual(Packed(Expected(light, 1f)), _store.GetSpotLights(LightTransformMode.Static)[0]);
		}

		[Test]
		public void UnsupportedTypes_HaveNoFakeLight_UntilBackToSpot()
		{
			var (light, linked) = CreateLinkedSpotLight();
			light.gameObject.SetActive(true);

			// No fake point lights yet; directional and area lights have no fake equivalent.
			foreach (var type in new[] { LightType.Point, LightType.Directional })
			{
				light.type = type;
				LinkedFakeLightSystem.PollAll();
				Assert.AreEqual(0, _store.SpotLightCount, $"{type} light");
				Assert.IsFalse(linked.HasFakeLight, $"{type} light");
			}

			light.type = LightType.Spot;
			LinkedFakeLightSystem.PollAll();
			Assert.AreEqual(1, _store.StaticSpotLightCount);
		}

		[Test]
		public void DisabledLight_IsNotRegistered_OnEnable()
		{
			var (light, linked) = CreateLinkedSpotLight();
			light.enabled = false;
			light.gameObject.SetActive(true);

			Assert.AreEqual(0, _store.SpotLightCount);
			Assert.IsFalse(linked.HasFakeLight);
		}

		[Test]
		public void SyncTransform_UsesTheDynamicPool_AndDisablingTheComponentRemovesTheLight()
		{
			var (light, linked) = CreateLinkedSpotLight();
			linked.SyncTransform = true;
			light.gameObject.SetActive(true);

			Assert.AreEqual(1, _store.DynamicSpotLightCount);
			Assert.AreEqual(Packed(Expected(light, 1f)), _store.GetSpotLights(LightTransformMode.Dynamic)[0]);

			linked.enabled = false;
			Assert.AreEqual(0, _store.SpotLightCount);
			Assert.AreEqual(0, LinkedFakeLightSystem.Count, "A disabled component leaves the polling loop.");
		}

		[Test]
		public void StaticLight_MovesOnlyOnApplyTransform()
		{
			var (light, linked) = CreateLinkedSpotLight();
			light.gameObject.SetActive(true);

			light.transform.position = new Vector3(3f, 0f, 0f);
			LinkedFakeLightSystem.PollAll();
			Assert.AreEqual(0f, _store.GetSpotLightTransforms(LightTransformMode.Static)[0].Position.x, 1e-4f, "Static lights don't follow moves by themselves.");

			linked.ApplyTransform();
			Assert.AreEqual(3f, _store.GetSpotLightTransforms(LightTransformMode.Static)[0].Position.x, 1e-4f);
		}

		[Test]
		public void EditorOnly_StillPollsOutsidePlayMode_AndRefreshPushesImmediately()
		{
			var (light, linked) = CreateLinkedSpotLight();
			linked.SyncMode = LinkedLightSyncMode.EditorOnly;
			light.gameObject.SetActive(true);

			light.range = 4f;
			LinkedFakeLightSystem.PollAll();
			Assert.AreEqual(4f, _store.GetSpotLights(LightTransformMode.Static)[0].Range, "Outside play mode, Editor Only polls too.");

			light.range = 6f;
			linked.Refresh();
			Assert.AreEqual(6f, _store.GetSpotLights(LightTransformMode.Static)[0].Range);
		}

		/// <summary>
		/// The real hook: rendering raises the GPU mirror's before-upload event, which polls linked lights, so a changed
		/// Light reaches the store the feature draws within that frame. Needs the project's URP asset (skipped otherwise).
		/// </summary>
		[Test]
		public void Rendering_PollsLinkedLights_BeforeTheUpload()
		{
			using var scene = new Utils.RenderScene();
			var store = scene.Feature.Store;
			var (light, linked) = CreateLinkedSpotLight();
			linked.OverrideStore = store;

			try
			{
				light.gameObject.SetActive(true);
				var index = store.StaticSpotLightCount - 1;
				Assert.AreEqual(7f, store.GetSpotLights(LightTransformMode.Static)[index].Range);

				light.range = 12.5f;
				scene.TimeFrames(1, withLights: false);

				Assert.AreEqual(12.5f, store.GetSpotLights(LightTransformMode.Static)[index].Range, "Rendering a frame should have polled the light.");
			}
			finally
			{
				// The feature's store is a project asset: leave it as it was.
				linked.enabled = false;
			}
		}

		// ---------- Helpers ----------

		private T Track<T>(T obj) where T : Object
		{
			_created.Add(obj);
			return obj;
		}

		/// <summary>
		/// An inactive GameObject with a spot Light (range 7, 50 / 30 degrees, warm color, intensity 3, no color
		/// temperature) and a LinkedFakeLight on the test store; activate it to register.
		/// </summary>
		private (Light light, LinkedFakeLight linked) CreateLinkedSpotLight()
		{
			var go = Track(new GameObject("Fuluma Test LinkedFakeLight"));
			go.SetActive(false);

			var light = go.AddComponent<Light>();
			light.type = LightType.Spot;
			light.range = 7f;
			light.spotAngle = 50f;
			light.innerSpotAngle = 30f;
			light.color = new Color(1f, 0.8f, 0.6f);
			light.intensity = 3f;
			light.useColorTemperature = false;

			var linked = go.AddComponent<LinkedFakeLight>();
			linked.OverrideStore = _store;
			return (light, linked);
		}
	}
}
