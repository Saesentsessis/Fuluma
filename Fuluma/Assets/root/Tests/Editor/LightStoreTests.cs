using System.Collections.Generic;
using NUnit.Framework;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Core.Lifecycle;
using Saesentsessis.Fuluma.Data;
using Saesentsessis.Fuluma.Rendering.Components;
using Unity.Mathematics;
using UnityEngine;

namespace Saesentsessis.Fuluma.Tests.Editor
{
	public class LightStoreTests
	{
		private const float Tolerance = 1e-4f;

		private static Placement StaticAt(float x)
		{
			return new Placement { Position = new Vector3(x, 0f, 0f), Rotation = Quaternion.identity };
		}

		private static SpotLightData SpotData(float radius)
		{
			return new SpotLightData
			{
				Color = new Color32(255, 255, 255, 255),
				Range = radius,
				InnerAngleDegrees = 20f,
				OuterAngleDegrees = 30f
			};
		}

		private static void AssertPosition(float3 expected, float3 actual)
		{
			Assert.AreEqual(expected.x, actual.x, Tolerance);
			Assert.AreEqual(expected.y, actual.y, Tolerance);
			Assert.AreEqual(expected.z, actual.z, Tolerance);
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
			// Destroying a store runs OnDisable, which releases its native memory.
			for (var i = _created.Count - 1; i >= 0; i--)
			{
				if (_created[i] != null)
					Object.DestroyImmediate(_created[i]);
			}

			_created.Clear();
		}

		// ---------- Handles ----------

		[Test]
		public void DefaultHandle_IsNull_AndNotValid()
		{
			Assert.IsTrue(default(LightHandle).IsNull);
			Assert.IsTrue(LightHandle.Null.IsNull);
			Assert.IsFalse(_store.Contains(LightHandle.Null));
			Assert.IsFalse(_store.Remove(LightHandle.Null));
		}

		[Test]
		public void NewStore_IsNotAllocated_UntilFirstAdd()
		{
			Assert.IsFalse(_store.IsAllocated);
			Assert.AreEqual(0, _store.SpotLightCount);

			_store.AddSpotLight(StaticAt(0f), SpotData(1f));

			Assert.IsTrue(_store.IsAllocated);
			Assert.AreEqual(1, _store.SpotLightCount);
		}

		[Test]
		public void HandleFromOneStore_IsRejectedByAnother()
		{
			var other = Track(ScriptableObject.CreateInstance<LightStore>());

			var handle = _store.AddSpotLight(StaticAt(0f), SpotData(1f));
			other.AddSpotLight(StaticAt(0f), SpotData(1f));

			Assert.IsTrue(_store.Contains(handle));
			Assert.IsFalse(other.Contains(handle), "Same slot and version in another store must not validate.");
			Assert.IsFalse(other.Remove(handle));
			Assert.AreEqual(1, other.SpotLightCount);
		}

		[Test]
		public void ClearSpotLights_KeepsEpoch_AndRejectsStaleHandles()
		{
			var handle = _store.AddSpotLight(StaticAt(0f), SpotData(1f));
			var epochBefore = _store.Epoch;

			_store.ClearSpotLights();

			Assert.IsFalse(_store.Contains(handle));
			Assert.AreEqual(epochBefore, _store.Epoch, "Clear keeps memory, so the epoch stays.");

			var fresh = _store.AddSpotLight(StaticAt(0f), SpotData(1f));
			Assert.IsTrue(_store.Contains(fresh));
			Assert.IsFalse(_store.Contains(handle), "A stale handle must not validate against a reused slot.");
		}

		// ---------- Static pool ----------

		[Test]
		public void AddStatic_StoresPackedTransformAndData()
		{
			var handle = _store.AddSpotLight(StaticAt(3f), SpotData(7f));

			Assert.AreEqual(LightTransformMode.Static, handle.Mode);
			Assert.AreEqual(FakeLightType.Spot, handle.Type);
			Assert.AreEqual(1, _store.StaticSpotLightCount);
			Assert.AreEqual(0, _store.DynamicSpotLightCount);

			AssertPosition(new float3(3f, 0f, 0f), _store.GetSpotLightTransforms(LightTransformMode.Static)[0].Position);
			Assert.AreEqual(7f, _store.GetSpotLights(LightTransformMode.Static)[0].Range, Tolerance);
		}

		[Test]
		public void RemoveStatic_SwapsBack_AndKeepsTransformsAlignedWithData()
		{
			// Light i sits at x = i and has range = i, so alignment is checkable from the arrays alone.
			var handles = new LightHandle[4];
			for (var i = 0; i < handles.Length; i++)
				handles[i] = _store.AddSpotLight(StaticAt(i), SpotData(i));

			Assert.IsTrue(_store.Remove(handles[1]));

			Assert.AreEqual(3, _store.StaticSpotLightCount);
			Assert.IsFalse(_store.Contains(handles[1]));
			Assert.IsTrue(_store.Contains(handles[0]));
			Assert.IsTrue(_store.Contains(handles[2]));
			Assert.IsTrue(_store.Contains(handles[3]));
			AssertStaticAligned();

			// Removing twice fails, and the count does not change.
			Assert.IsFalse(_store.Remove(handles[1]));
			Assert.AreEqual(3, _store.StaticSpotLightCount);
		}

		[Test]
		public void RemoveStatic_Last_DoesNotDisturbOthers()
		{
			var a = _store.AddSpotLight(StaticAt(0f), SpotData(0f));
			var b = _store.AddSpotLight(StaticAt(1f), SpotData(1f));

			Assert.IsTrue(_store.Remove(b));

			Assert.IsTrue(_store.Contains(a));
			Assert.AreEqual(1, _store.StaticSpotLightCount);
			AssertStaticAligned();
		}

		[Test]
		public void HandlesSurviveManySwapBacks()
		{
			var live = new List<(LightHandle handle, float key)>();

			for (var i = 0; i < 200; i++)
				live.Add((_store.AddSpotLight(StaticAt(i), SpotData(i)), i));

			// Remove every third light (by insertion order), forcing many swap-backs.
			for (var i = live.Count - 1; i >= 0; i -= 3)
			{
				Assert.IsTrue(_store.Remove(live[i].handle));
				live.RemoveAt(i);
			}

			Assert.AreEqual(live.Count, _store.StaticSpotLightCount);
			AssertStaticAligned();

			// Every surviving handle still addresses its own light: give it a new, unique range
			// and check that exactly the entry at its position picked it up.
			foreach (var (handle, key) in live)
				Assert.IsTrue(_store.SetSpotLight(handle, SpotData(key + 1000f)));

			var lights = _store.GetSpotLights(LightTransformMode.Static);
			var transforms = _store.GetSpotLightTransforms(LightTransformMode.Static);
			for (var i = 0; i < lights.Length; i++)
				Assert.AreEqual(transforms[i].Position.x + 1000f, lights[i].Range, Tolerance);
		}

		[Test]
		public void SetStaticTransform_UpdatesOnlyThatLight()
		{
			var a = _store.AddSpotLight(StaticAt(0f), SpotData(0f));
			_store.AddSpotLight(StaticAt(1f), SpotData(1f));
			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out _, out _);

			Assert.IsTrue(_store.SetSpotLightPlacement(a, StaticAt(5f)));

			AssertPosition(new float3(5f, 0f, 0f), _store.GetSpotLightTransforms(LightTransformMode.Static)[0].Position);
			AssertPosition(new float3(1f, 0f, 0f), _store.GetSpotLightTransforms(LightTransformMode.Static)[1].Position);
			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out var transforms, out _);
			Assert.AreEqual(new BufferRange(0, 1), transforms);
		}

		[Test]
		public void StaticChanges_LeaveTheDynamicPoolClean()
		{
			_store.AddSpotLight(CreateTransform(Vector3.zero), SpotData(0f));
			_store.AddSpotLight(StaticAt(0f), SpotData(0f));
			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out _, out _);
			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Dynamic, out _, out _);

			var h = _store.AddSpotLight(StaticAt(1f), SpotData(1f));
			_store.SetSpotLight(h, SpotData(2f));

			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Dynamic, out var dynamicTransforms, out var dynamicLights);
			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out var staticTransforms, out var staticLights);
			Assert.IsTrue(dynamicTransforms.IsEmpty && dynamicLights.IsEmpty);
			Assert.AreEqual(new BufferRange(1, 1), staticTransforms);
			Assert.AreEqual(new BufferRange(1, 1), staticLights);
		}

		// ---------- Dynamic pool ----------

		[Test]
		public void AddDynamic_CapturesInitialTransform()
		{
			var t = CreateTransform(new Vector3(1f, 2f, 3f));

			var handle = _store.AddSpotLight(t, SpotData(4f));

			Assert.AreEqual(LightTransformMode.Dynamic, handle.Mode);
			Assert.AreEqual(1, _store.DynamicSpotLightCount);
			Assert.AreEqual(0, _store.StaticSpotLightCount);
			AssertPosition(new float3(1f, 2f, 3f), _store.GetSpotLightTransforms(LightTransformMode.Dynamic)[0].Position);
			Assert.AreEqual(4f, _store.GetSpotLights(LightTransformMode.Dynamic)[0].Range, Tolerance);
		}

		[Test]
		public void AddDynamic_NullTransform_Throws()
		{
			Assert.Throws<System.ArgumentNullException>(() => _store.AddSpotLight((Transform)null, SpotData(1f)));
		}

		[Test]
		public void SetTransform_OnDynamicLight_IsRejected()
		{
			var t = CreateTransform(Vector3.zero);
			var handle = _store.AddSpotLight(t, SpotData(1f));

			Assert.IsFalse(_store.SetSpotLightPlacement(handle, StaticAt(9f)));
			AssertPosition(float3.zero, _store.GetSpotLightTransforms(LightTransformMode.Dynamic)[0].Position);
		}

		[Test]
		public void DynamicSync_FollowsTransforms_AfterSwapBackRemoval()
		{
			var transforms = new Transform[4];
			var handles = new LightHandle[4];

			for (var i = 0; i < transforms.Length; i++)
			{
				transforms[i] = CreateTransform(new Vector3(i, 0f, 0f));
				handles[i] = _store.AddSpotLight(transforms[i], SpotData(i));
			}

			Assert.IsTrue(_store.Remove(handles[0]));

			// Move every surviving light to x = 10 * range, so the sync result is checkable against its data.
			for (var i = 1; i < transforms.Length; i++)
				transforms[i].position = new Vector3(10f * i, 0f, 0f);

			_store.ScheduleDynamicSpotLightSync();
			_store.CompleteSpotLightJobs(LightTransformMode.Dynamic);

			var lights = _store.GetSpotLights(LightTransformMode.Dynamic);
			var packed = _store.GetSpotLightTransforms(LightTransformMode.Dynamic);

			Assert.AreEqual(3, lights.Length);
			for (var i = 0; i < lights.Length; i++)
				Assert.AreEqual(10f * lights[i].Range, packed[i].Position.x, Tolerance, $"Dense index {i} is out of sync with its Transform.");
		}

		[Test]
		public void DynamicSync_PacksRotation()
		{
			var t = CreateTransform(Vector3.zero);
			t.rotation = Quaternion.LookRotation(Vector3.up + Vector3.right);
			_store.AddSpotLight(t, SpotData(1f));

			t.rotation = Quaternion.LookRotation(Vector3.left);
			_store.ScheduleDynamicSpotLightSync();
			_store.CompleteSpotLightJobs(LightTransformMode.Dynamic);

			var expected = new Placement { Position = Vector3.zero, Rotation = t.rotation }.Pack();
			Assert.AreEqual(expected.PackedDirection, _store.GetSpotLightTransforms(LightTransformMode.Dynamic)[0].PackedDirection);
		}

		[Test]
		public void RemoveDynamic_WhileSyncIsPending_CompletesSyncFirst()
		{
			var a = _store.AddSpotLight(CreateTransform(Vector3.zero), SpotData(0f));
			_store.AddSpotLight(CreateTransform(Vector3.one), SpotData(1f));

			_store.ScheduleDynamicSpotLightSync();

			// Would throw a job-safety exception if the store did not complete the pending sync.
			Assert.IsTrue(_store.Remove(a));
			Assert.AreEqual(1, _store.DynamicSpotLightCount);
		}

		[Test]
		public void ClearSpotLights_EmptiesBothPools_AndInvalidatesHandles()
		{
			var s = _store.AddSpotLight(StaticAt(0f), SpotData(0f));
			var d = _store.AddSpotLight(CreateTransform(Vector3.zero), SpotData(0f));

			_store.ClearSpotLights();

			Assert.AreEqual(0, _store.SpotLightCount);
			Assert.IsFalse(_store.Contains(s));
			Assert.IsFalse(_store.Contains(d));

			// The dynamic pool still works after a clear (TransformAccessArray emptied in step).
			var t = CreateTransform(new Vector3(2f, 0f, 0f));
			_store.AddSpotLight(t, SpotData(1f));
			_store.ScheduleDynamicSpotLightSync();
			_store.CompleteSpotLightJobs(LightTransformMode.Dynamic);
			AssertPosition(new float3(2f, 0f, 0f), _store.GetSpotLightTransforms(LightTransformMode.Dynamic)[0].Position);
		}

		// ---------- FakeSpotLight component ----------

		[Test]
		public void FakeSpotLight_RegistersOnEnable_AndRemovesOnDisable()
		{
			var light = CreateFakeSpotLight(syncTransform: false);

			light.gameObject.SetActive(true);
			Assert.AreEqual(1, _store.StaticSpotLightCount);

			light.enabled = false;
			Assert.AreEqual(0, _store.SpotLightCount);

			light.enabled = true;
			Assert.AreEqual(1, _store.StaticSpotLightCount);
		}

		[Test]
		public void FakeSpotLight_TogglingSyncTransform_MovesLightBetweenPools()
		{
			var light = CreateFakeSpotLight(syncTransform: false);
			light.gameObject.SetActive(true);

			light.SyncTransform = true;
			Assert.AreEqual(0, _store.StaticSpotLightCount);
			Assert.AreEqual(1, _store.DynamicSpotLightCount);

			light.SyncTransform = false;
			Assert.AreEqual(1, _store.StaticSpotLightCount);
			Assert.AreEqual(0, _store.DynamicSpotLightCount);
		}

		// ---------- Play-mode switch (FulumaLifecycle) ----------

		[Test]
		public void Lifecycle_LeavingMode_ReleasesStores_AndEnteringMode_RestoresEnabledLights()
		{
			var staticLight = CreateFakeSpotLight(syncTransform: false);
			var dynamicLight = CreateFakeSpotLight(syncTransform: true);
			staticLight.gameObject.SetActive(true);
			dynamicLight.gameObject.SetActive(true);

			FulumaLifecycle.OnLeavingMode();

			Assert.IsFalse(_store.IsAllocated, "Leaving a mode must release every store.");
			Assert.AreEqual(0, _store.SpotLightCount);

			FulumaLifecycle.OnEnteredMode();

			Assert.IsTrue(_store.IsAllocated);
			Assert.AreEqual(1, _store.StaticSpotLightCount, "Static light must come back into the static pool.");
			Assert.AreEqual(1, _store.DynamicSpotLightCount, "Dynamic light must come back into the dynamic pool.");

			// Restored lights are fully functional: removal through the component works.
			staticLight.enabled = false;
			dynamicLight.enabled = false;
			Assert.AreEqual(0, _store.SpotLightCount);
		}

		[Test]
		public void Lifecycle_EnteringMode_WithLiveHandles_IsNoOp()
		{
			var light = CreateFakeSpotLight(syncTransform: false);
			light.gameObject.SetActive(true);
			var epoch = _store.Epoch;

			FulumaLifecycle.OnEnteredMode();
			FulumaLifecycle.OnEnteredMode();

			Assert.AreEqual(1, _store.SpotLightCount, "Restoring a live registration must not add a duplicate.");
			Assert.AreEqual(epoch, _store.Epoch);
		}

		[Test]
		public void Lifecycle_DisabledOrDestroyedLights_AreNotRestored()
		{
			var disabled = CreateFakeSpotLight(syncTransform: false);
			var destroyed = CreateFakeSpotLight(syncTransform: true);
			disabled.gameObject.SetActive(true);
			destroyed.gameObject.SetActive(true);
			var clientsBefore = FulumaLifecycle.ClientCount;

			disabled.enabled = false;
			Object.DestroyImmediate(destroyed.gameObject);
			Assert.AreEqual(clientsBefore - 2, FulumaLifecycle.ClientCount, "Disabled and destroyed lights must leave the client list.");

			FulumaLifecycle.OnLeavingMode();
			FulumaLifecycle.OnEnteredMode();

			Assert.AreEqual(0, _store.SpotLightCount);
		}

		[Test]
		public void Lifecycle_LightOnDefaultStore_ComesBack()
		{
			var go = Track(new GameObject("Fuluma Test Default Light"));
			go.AddComponent<FakeSpotLight>();
			var before = LightStore.Default.StaticSpotLightCount;

			FulumaLifecycle.OnLeavingMode();
			Assert.AreEqual(0, LightStore.Default.SpotLightCount);

			FulumaLifecycle.OnEnteredMode();
			Assert.AreEqual(before, LightStore.Default.StaticSpotLightCount);
		}

		[Test]
		public void FakeSpotLight_StaticLight_MovesOnlyOnApplyTransform()
		{
			var light = CreateFakeSpotLight(syncTransform: false);
			light.gameObject.SetActive(true);

			light.transform.position = new Vector3(4f, 0f, 0f);
			AssertPosition(float3.zero, _store.GetSpotLightTransforms(LightTransformMode.Static)[0].Position);

			light.ApplyTransform();
			AssertPosition(new float3(4f, 0f, 0f), _store.GetSpotLightTransforms(LightTransformMode.Static)[0].Position);
		}

		[Test]
		public void FakeSpotLight_ChangingOverrideStore_MovesLightBetweenStores()
		{
			var other = Track(ScriptableObject.CreateInstance<LightStore>());
			var light = CreateFakeSpotLight(syncTransform: true);
			light.gameObject.SetActive(true);

			light.OverrideStore = other;

			Assert.AreEqual(0, _store.SpotLightCount);
			Assert.AreEqual(1, other.DynamicSpotLightCount);
		}

		[Test]
		public void FakeSpotLight_DataSetter_PushesToStore()
		{
			var light = CreateFakeSpotLight(syncTransform: true);
			light.gameObject.SetActive(true);

			light.Data = SpotData(12f);

			Assert.AreEqual(12f, _store.GetSpotLights(LightTransformMode.Dynamic)[0].Range, Tolerance);
		}

		// ---------- Helpers ----------

		private T Track<T>(T obj) where T : Object
		{
			_created.Add(obj);
			return obj;
		}

		private Transform CreateTransform(Vector3 position)
		{
			var go = Track(new GameObject("Fuluma Test Light"));
			go.transform.position = position;
			return go.transform;
		}

		/// <summary>
		/// Creates an inactive light pointing at the test store; activate it to register.
		/// </summary>
		private FakeSpotLight CreateFakeSpotLight(bool syncTransform)
		{
			var go = Track(new GameObject("Fuluma Test FakeSpotLight"));
			go.SetActive(false);

			var light = go.AddComponent<FakeSpotLight>();
			light.OverrideStore = _store;
			light.SyncTransform = syncTransform;
			light.Data = SpotData(1f);

			return light;
		}

		/// <summary>
		/// Every static light was created with position.x == range; swap-back must keep that true.
		/// </summary>
		private void AssertStaticAligned()
		{
			var lights = _store.GetSpotLights(LightTransformMode.Static);
			var transforms = _store.GetSpotLightTransforms(LightTransformMode.Static);

			Assert.AreEqual(lights.Length, transforms.Length);
			for (var i = 0; i < lights.Length; i++)
				Assert.AreEqual(transforms[i].Position.x, lights[i].Range, Tolerance, $"Dense index {i}: transform and data belong to different lights.");
		}
	}
}
