using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Core.Lifecycle;
using Saesentsessis.Fuluma.Data;
using Saesentsessis.Fuluma.Rendering.Components;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Saesentsessis.Fuluma.Tests.Editor
{
	/// <summary>
	/// <see cref="FakeLightClipBox"/> and the clip box slots of <see cref="FakeLightComponent"/>.
	/// </summary>
	public class FakeLightClipBoxTests
	{
		private static ClipBoxReference Reference(FakeLightClipBox box, ClipBoxRole role)
		{
			Assert.IsTrue(box.TryGetRegistration(out _, out var handle), $"'{box.name}' should be registered.");
			return new ClipBoxReference(handle, role);
		}

		private static LightHandle LightHandleOf(FakeLightComponent light)
		{
			Assert.IsTrue(light.TryGetRegistration(out _, out var handle), $"'{light.name}' should be registered.");
			return handle;
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

		// ---------- Box component ----------

		[Test]
		public void StaticBox_UsesTheTransformsScale_AndMovesOnApplyTransform()
		{
			var box = CreateBox(new Vector3(5f, 0f, 0f), new ClipBoxData { Size = Vector3.one });
			box.transform.localScale = new Vector3(4f, 1f, 1f);
			box.ApplyTransform();

			Assert.IsTrue(box.TryGetRegistration(out var store, out var handle));
			Assert.AreSame(_store, store);
			Assert.AreEqual(LightTransformMode.Static, handle.Mode);
			Assert.IsTrue(Packed(handle).Contains(new float3(6.9f, 0f, 0f)), "Scale stretches the box.");

			box.transform.position = new Vector3(-5f, 0f, 0f);
			Assert.IsTrue(Packed(handle).Contains(new float3(5f, 0f, 0f)), "A static box waits for ApplyTransform.");
			box.ApplyTransform();
			Assert.IsTrue(Packed(handle).Contains(new float3(-5f, 0f, 0f)));
			Assert.IsFalse(Packed(handle).Contains(new float3(5f, 0f, 0f)));
		}

		[Test]
		public void DynamicBox_FollowsItsTransform_AndShapeChangesApply()
		{
			var box = CreateBox(Vector3.zero, ClipBoxData.Default, syncTransform: true);
			Assert.IsTrue(box.TryGetRegistration(out _, out var handle));
			Assert.AreEqual(LightTransformMode.Dynamic, handle.Mode);

			box.transform.position = new Vector3(0f, 10f, 0f);
			_store.ScheduleDynamicClipBoxSync().Complete();
			Assert.IsTrue(Packed(handle).Contains(new float3(0f, 10f, 0f)));

			box.Shape = new ClipBoxData { Size = new Vector3(1f, 1f, 8f) };
			Assert.IsTrue(Packed(handle).Contains(new float3(0f, 10f, 3.9f)));
		}

		// ---------- Lights using boxes ----------

		[Test]
		public void Light_UsesItsBoxes_WhicheverIsEnabledFirst()
		{
			// The box comes after the light.
			var room = CreateBox(Vector3.zero, ClipBoxData.Default, active: false);
			var neighbour = CreateBox(Vector3.zero, ClipBoxData.Default);
			var light = CreateSpotLight(new ClipBoxSlot(room, ClipBoxRole.Keep), new ClipBoxSlot(neighbour, ClipBoxRole.Subtract));

			AssertLightBoxes(light, Reference(neighbour, ClipBoxRole.Subtract));

			room.gameObject.SetActive(true);
			AssertLightBoxes(light, Reference(room, ClipBoxRole.Keep), Reference(neighbour, ClipBoxRole.Subtract));
		}

		[Test]
		public void DisablingABox_StopsItsUse_UntilItIsEnabledAgain()
		{
			var room = CreateBox(Vector3.zero, ClipBoxData.Default);
			var light = CreateSpotLight(new ClipBoxSlot(room, ClipBoxRole.Keep));
			AssertLightBoxes(light, Reference(room, ClipBoxRole.Keep));

			room.enabled = false;
			AssertLightBoxes(light);

			room.enabled = true;
			AssertLightBoxes(light, Reference(room, ClipBoxRole.Keep));

			// A box changing mode gets a new handle; the light follows.
			room.SyncTransform = true;
			AssertLightBoxes(light, Reference(room, ClipBoxRole.Keep));
		}

		[Test]
		public void LightChangingPool_KeepsItsBoxes()
		{
			var room = CreateBox(Vector3.zero, ClipBoxData.Default);
			var light = CreateSpotLight(new ClipBoxSlot(room, ClipBoxRole.Keep));

			light.SyncTransform = true;
			AssertLightBoxes(light, Reference(room, ClipBoxRole.Keep));
			Assert.AreEqual(1, _store.DynamicSpotLightCount);
		}

		[Test]
		public void SettingClipBoxes_ReplacesTheBoxes_AndSkipsEmptySlots()
		{
			var a = CreateBox(Vector3.zero, ClipBoxData.Default);
			var b = CreateBox(Vector3.zero, ClipBoxData.Default);
			var c = CreateBox(Vector3.zero, ClipBoxData.Default);
			var light = CreateSpotLight(new ClipBoxSlot(a, ClipBoxRole.Keep));

			// Used slots may sit anywhere among the four.
			var slots = ClipBoxSlots.None;
			slots[1] = new ClipBoxSlot(b, ClipBoxRole.Subtract);
			slots[3] = new ClipBoxSlot(c, ClipBoxRole.Keep);
			light.ClipBoxes = slots;
			AssertLightBoxes(light, Reference(b, ClipBoxRole.Subtract), Reference(c, ClipBoxRole.Keep));
			Assert.AreEqual(2, light.ClipBoxes.UsedCount);

			// The old box no longer drives the light.
			a.enabled = false;
			a.enabled = true;
			AssertLightBoxes(light, Reference(b, ClipBoxRole.Subtract), Reference(c, ClipBoxRole.Keep));

			light.ClipBoxes = ClipBoxSlots.None;
			AssertLightBoxes(light);
			Assert.Throws<ArgumentOutOfRangeException>(() => _ = slots[ClipBoxSlots.Count]);
		}

		[Test]
		public void BoxFromAnotherStore_IsIgnored()
		{
			var otherStore = Track(ScriptableObject.CreateInstance<LightStore>());
			var foreign = CreateBox(Vector3.zero, ClipBoxData.Default, store: otherStore);

			LogAssert.Expect(LogType.Warning, new Regex("another light store"));
			var light = CreateSpotLight(new ClipBoxSlot(foreign, ClipBoxRole.Keep));

			AssertLightBoxes(light);
		}

		[Test]
		public void LinkedFakeLight_UsesBoxesToo()
		{
			var room = CreateBox(Vector3.zero, ClipBoxData.Default);

			var go = Track(new GameObject("Linked"));
			go.SetActive(false);
			var unityLight = go.AddComponent<Light>();
			unityLight.type = LightType.Spot;
			var linked = go.AddComponent<LinkedFakeLight>();
			linked.OverrideStore = _store;
			linked.ClipBoxes = new ClipBoxSlots(new ClipBoxSlot(room, ClipBoxRole.Keep));
			go.SetActive(true);

			AssertLightBoxes(linked, Reference(room, ClipBoxRole.Keep));
		}

		[Test]
		public void PlayModeSwitch_RestoresBoxesAndReferences()
		{
			var room = CreateBox(Vector3.zero, ClipBoxData.Default);
			var light = CreateSpotLight(new ClipBoxSlot(room, ClipBoxRole.Keep));

			// What a play-mode switch does to the stores and the components.
			FulumaLifecycle.OnLeavingMode();
			Assert.AreEqual(0, _store.ClipBoxCount);
			FulumaLifecycle.OnEnteredMode();

			Assert.AreEqual(1, _store.ClipBoxCount);
			AssertLightBoxes(light, Reference(room, ClipBoxRole.Keep));
		}

		// ---------- Helpers ----------

		private FakeLightClipBox CreateBox(Vector3 position, ClipBoxData shape, bool syncTransform = false, bool active = true,
			LightStore store = null)
		{
			var go = Track(new GameObject("Box"));
			go.SetActive(false);
			go.transform.position = position;
			var box = go.AddComponent<FakeLightClipBox>();
			box.OverrideStore = store != null ? store : _store;
			box.SyncTransform = syncTransform;
			box.Shape = shape;
			go.SetActive(active);
			return box;
		}

		private FakeSpotLight CreateSpotLight(params ClipBoxSlot[] boxes)
		{
			var go = Track(new GameObject("Spot"));
			go.SetActive(false);
			var light = go.AddComponent<FakeSpotLight>();
			light.OverrideStore = _store;

			var slots = ClipBoxSlots.None;
			for (var i = 0; i < boxes.Length; i++)
				slots[i] = boxes[i];

			light.ClipBoxes = slots;
			go.SetActive(true);
			return light;
		}

		private PackedClipBoxData Packed(ClipBoxHandle handle) => _store.GetClipBoxes()[handle.Slot.Index];

		private void AssertLightBoxes(FakeLightComponent light, params ClipBoxReference[] expected)
		{
			var handle = LightHandleOf(light);
			var read = new ClipBoxReference[LightStore.MaxClipBoxesPerLight];
			Assert.IsTrue(_store.TryGetClipBoxes(handle, read, out var count), "The light should be registered.");
			Assert.AreEqual(expected.Length, count, "Number of boxes the light uses.");
			for (var i = 0; i < count; i++)
				Assert.AreEqual(expected[i], read[i], $"Box {i}.");
		}

		private T Track<T>(T obj) where T : Object
		{
			_created.Add(obj);
			return obj;
		}
	}
}
