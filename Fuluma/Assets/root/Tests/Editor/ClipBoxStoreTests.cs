using System;
using System.Collections.Generic;
using NUnit.Framework;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Data;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Saesentsessis.Fuluma.Tests.Editor
{
	public class ClipBoxStoreTests
	{
		private const float Tolerance = 1e-4f;

		// ---------- Helpers ----------

		private static Placement PlacementAt(float3 position)
			=> new() { Position = position, Rotation = Quaternion.identity };

		/// <summary>
		/// Every corner of the box (given by <paramref name="cornerToWorld"/> from a corner in {-1, 1}³) maps to that
		/// corner, the center maps to 0, and points just inside / outside along each axis are classified correctly.
		/// </summary>
		private static void AssertCornersOnUnitCube(PackedClipBoxData packed, Func<float3, float3> cornerToWorld)
		{
			for (var x = -1; x <= 1; x += 2)
			for (var y = -1; y <= 1; y += 2)
			for (var z = -1; z <= 1; z += 2)
			{
				var corner = new float3(x, y, z);
				var mapped = packed.ToBox(cornerToWorld(corner));
				Assert.That(math.distance(mapped, corner), Is.LessThan(Tolerance), $"Corner {corner} mapped to {mapped}.");
			}

			Assert.That(math.length(packed.ToBox(cornerToWorld(float3.zero))), Is.LessThan(Tolerance));
			Assert.IsTrue(packed.Contains(cornerToWorld(new float3(0.99f, -0.99f, 0.5f))));
			Assert.IsFalse(packed.Contains(cornerToWorld(new float3(1.01f, 0f, 0f))));
			Assert.IsFalse(packed.Contains(cornerToWorld(new float3(0f, -1.01f, 0f))));
			Assert.IsFalse(packed.Contains(cornerToWorld(new float3(0f, 0f, 1.01f))));
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

		// ---------- Allocation ----------

		[Test]
		public void StoreWithoutBoxes_AllocatesClipBoxMemoryWithTheLights()
		{
			Assert.IsFalse(_store.GetClipBoxes().IsCreated, "Nothing before the first add.");

			var light = _store.AddSpotLight(PlacementAt(float3.zero), SpotLightData.Default);
			_store.AddSpotLight(Track(new GameObject()).transform, SpotLightData.Default);

			Assert.AreEqual(0, _store.ClipBoxCount);
			Assert.IsTrue(_store.GetClipBoxes().IsCreated);
			Assert.AreEqual(1, _store.GetSpotLightClipBoxReferences(LightTransformMode.Static).Length);
			Assert.AreEqual(1, _store.GetSpotLightClipBoxReferences(LightTransformMode.Dynamic).Length);
			Assert.IsTrue(_store.SetClipBoxes(light, ReadOnlySpan<ClipBoxReference>.Empty));
			Assert.AreEqual(PackedClipBoxReferences.Empty, _store.GetSpotLightClipBoxReferences(LightTransformMode.Static)[0]);
		}

		[Test]
		public void References_AreIndexAlignedWithTheLights()
		{
			var first = _store.AddSpotLight(PlacementAt(float3.zero), SpotLightData.Default);
			var second = _store.AddSpotLight(PlacementAt(new float3(1f, 0f, 0f)), SpotLightData.Default);
			var box = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);

			Assert.IsTrue(_store.SetClipBoxes(second, new[] { ClipBoxReference.Keep(box) }));

			var references = _store.GetSpotLightClipBoxReferences(LightTransformMode.Static);
			Assert.AreEqual(2, references.Length);
			Assert.AreEqual(PackedClipBoxReferences.Empty, references[0], "Lights without boxes have empty references.");
			Assert.AreEqual(1, references[1].UsedCount);
			Assert.AreEqual(0, _store.GetSpotLightClipBoxReferences(LightTransformMode.Dynamic).Length);

			// Lights added afterwards get empty references too.
			var third = _store.AddSpotLight(PlacementAt(float3.zero), SpotLightData.Default);
			Assert.AreEqual(3, _store.GetSpotLightClipBoxReferences(LightTransformMode.Static).Length);
			Assert.IsTrue(_store.TryGetClipBoxes(third, new ClipBoxReference[4], out var count));
			Assert.AreEqual(0, count);
			Assert.IsTrue(_store.TryGetClipBoxes(first, new ClipBoxReference[4], out count));
			Assert.AreEqual(0, count);
		}

		// ---------- Packing ----------

		[Test]
		public void StaticBox_MapsItsCornersOntoTheUnitCube()
		{
			var placement = new Placement
			{
				Position = new Vector3(1f, 2f, 3f),
				Rotation = Quaternion.Euler(10f, 30f, -20f)
			};
			var shape = new ClipBoxData { Center = new Vector3(0.5f, -1f, 2f), Size = new Vector3(2f, 4f, 6f) };

			var handle = _store.AddClipBox(placement, shape);
			var packed = _store.GetClipBoxes()[handle.Slot.Index];

			var toWorld = float4x4.TRS(placement.Position, placement.Rotation, new float3(1f));
			AssertCornersOnUnitCube(packed, corner => math.transform(toWorld, (float3)shape.Center + corner * (float3)shape.Size * 0.5f));
		}

		[Test]
		public void DynamicBox_FollowsItsTransform_ScaleAndShearIncluded()
		{
			// A rotated child under a non-uniformly scaled parent: a sheared box.
			var parent = Track(new GameObject("Parent")).transform;
			parent.SetPositionAndRotation(new Vector3(-2f, 1f, 4f), Quaternion.Euler(0f, 25f, 0f));
			parent.localScale = new Vector3(2f, 0.5f, 3f);
			var child = Track(new GameObject("Child")).transform;
			child.SetParent(parent, false);
			child.localRotation = Quaternion.Euler(35f, 0f, 15f);
			child.localPosition = new Vector3(0.3f, 1f, -0.2f);

			var shape = new ClipBoxData { Center = new Vector3(0f, 0.25f, 0f), Size = new Vector3(1f, 2f, 0.5f) };
			var handle = _store.AddClipBox(child, shape);

			AssertCornersOnUnitCube(_store.GetClipBoxes()[handle.Slot.Index],
				corner => (float3)child.TransformPoint(shape.Center + Vector3.Scale(corner, shape.Size) * 0.5f));

			// Moved: the sync re-packs it.
			child.localPosition += new Vector3(1f, -2f, 0.5f);
			parent.Rotate(0f, 40f, 10f);
			_store.ScheduleDynamicClipBoxSync().Complete();

			AssertCornersOnUnitCube(_store.GetClipBoxes()[handle.Slot.Index],
				corner => (float3)child.TransformPoint(shape.Center + Vector3.Scale(corner, shape.Size) * 0.5f));
		}

		[Test]
		public void DegenerateBox_ContainsNothing()
		{
			var flat = _store.AddClipBox(PlacementAt(float3.zero), new ClipBoxData { Size = new Vector3(1f, 0f, 1f) });
			Assert.IsFalse(_store.GetClipBoxes()[flat.Slot.Index].Contains(float3.zero));

			var transform = Track(new GameObject()).transform;
			transform.localScale = new Vector3(1f, 1f, 0f);
			var squashed = _store.AddClipBox(transform, ClipBoxData.Default);
			Assert.IsFalse(_store.GetClipBoxes()[squashed.Slot.Index].Contains(float3.zero));
		}

		[Test]
		public void SetShapeAndTransform_RepackTheBox()
		{
			var handle = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);
			Assert.IsTrue(_store.GetClipBoxes()[handle.Slot.Index].Contains(new float3(0.4f, 0f, 0f)));

			Assert.IsTrue(_store.SetClipBoxShape(handle, new ClipBoxData { Size = new Vector3(0.5f, 1f, 1f) }));
			Assert.IsFalse(_store.GetClipBoxes()[handle.Slot.Index].Contains(new float3(0.4f, 0f, 0f)));

			Assert.IsTrue(_store.SetClipBoxPlacement(handle, PlacementAt(new float3(10f, 0f, 0f))));
			Assert.IsTrue(_store.GetClipBoxes()[handle.Slot.Index].Contains(new float3(10.2f, 0f, 0f)));
			Assert.IsFalse(_store.GetClipBoxes()[handle.Slot.Index].Contains(float3.zero));

			var dynamic = _store.AddClipBox(Track(new GameObject()).transform, ClipBoxData.Default);
			Assert.IsFalse(_store.SetClipBoxPlacement(dynamic, PlacementAt(float3.zero)), "A dynamic box follows its transform.");
			Assert.IsTrue(_store.SetClipBoxShape(dynamic, new ClipBoxData { Size = new Vector3(4f, 4f, 4f) }));
			Assert.IsTrue(_store.GetClipBoxes()[dynamic.Slot.Index].Contains(new float3(1.9f, 0f, 0f)));
		}

		// ---------- Handles ----------

		[Test]
		public void Handles_DieOnRemoval_AndIndicesAreReused()
		{
			Assert.IsFalse(_store.Contains(ClipBoxHandle.Null));
			Assert.IsTrue(ClipBoxHandle.Null.IsNull);

			var a = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);
			var b = _store.AddClipBox(Track(new GameObject()).transform, ClipBoxData.Default);
			Assert.IsTrue(_store.Contains(a));
			Assert.IsTrue(_store.Contains(b));
			Assert.AreEqual(2, _store.ClipBoxCount);
			Assert.AreNotEqual(a.Slot.Index, b.Slot.Index);

			Assert.IsTrue(_store.Remove(a));
			Assert.IsFalse(_store.Contains(a));
			Assert.IsFalse(_store.Remove(a));
			Assert.IsTrue(_store.Contains(b), "Removing one box leaves the others' indices alone.");
			Assert.IsFalse(_store.GetClipBoxes()[a.Slot.Index].Contains(float3.zero), "A removed box becomes an empty hole.");

			var c = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);
			Assert.AreEqual(a.Slot.Index, c.Slot.Index, "The hole is reused.");
			Assert.IsFalse(_store.Contains(a), "The old handle stays dead.");
			Assert.IsTrue(_store.Contains(c));

			// The wrong mode, or another store's handle, never validates.
			Assert.IsFalse(_store.Contains(new ClipBoxHandle(c.Slot, c.Epoch, LightTransformMode.Dynamic)));
			var other = Track(ScriptableObject.CreateInstance<LightStore>());
			Assert.IsFalse(other.Contains(c));
		}

		[Test]
		public void DynamicBoxRemoval_KeepsTheOtherDynamicBoxesInStep()
		{
			var transforms = new Transform[3];
			var handles = new ClipBoxHandle[3];
			for (var i = 0; i < 3; i++)
			{
				transforms[i] = Track(new GameObject()).transform;
				transforms[i].position = new Vector3(i * 10f, 0f, 0f);
				handles[i] = _store.AddClipBox(transforms[i], ClipBoxData.Default);
			}

			Assert.IsTrue(_store.Remove(handles[0]));

			// The last box was swapped into the removed one's dense place; its transform must have moved with it.
			transforms[2].position = new Vector3(100f, 0f, 0f);
			_store.ScheduleDynamicClipBoxSync().Complete();

			Assert.IsTrue(_store.GetClipBoxes()[handles[2].Slot.Index].Contains(new float3(100f, 0f, 0f)));
			Assert.IsTrue(_store.GetClipBoxes()[handles[1].Slot.Index].Contains(new float3(10f, 0f, 0f)));
		}

		[Test]
		public void StoreRelease_KillsBoxHandles()
		{
			var box = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);
			_store.Clear();
			Assert.IsFalse(_store.Contains(box));
			Assert.AreEqual(0, _store.ClipBoxCount);

			box = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);
			Object.DestroyImmediate(_store);
			_store = Track(ScriptableObject.CreateInstance<LightStore>());
			Assert.IsFalse(_store.Contains(box));
		}

		[Test]
		public void BoxCount_IsLimitedTo15BitIndices()
		{
			for (var i = 0; i < LightStore.MaxClipBoxes; i++)
			{
				var handle = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);
				Assert.LessOrEqual(handle.Slot.Index, PackedClipBoxReferences.MaxBoxIndex);
			}

			Assert.AreEqual(32767, _store.ClipBoxCount);
			Assert.Throws<InvalidOperationException>(() => _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default));
		}

		// ---------- Light references ----------

		[Test]
		public void LightReferences_RoundTrip_WithRoles()
		{
			var light = _store.AddSpotLight(Track(new GameObject()).transform, SpotLightData.Default);
			var keep = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);
			var subtract = _store.AddClipBox(Track(new GameObject()).transform, ClipBoxData.Default);

			var set = new[] { ClipBoxReference.Keep(keep), ClipBoxReference.Subtract(subtract) };
			Assert.IsTrue(_store.SetClipBoxes(light, set));

			var read = new ClipBoxReference[LightStore.MaxClipBoxesPerLight];
			Assert.IsTrue(_store.TryGetClipBoxes(light, read, out var count));
			Assert.AreEqual(2, count);
			Assert.AreEqual(set[0], read[0]);
			Assert.AreEqual(set[1], read[1]);

			// GPU form: index in the low 15 bits, subtract in the top bit, unused slots 0xFFFF.
			var packed = _store.GetSpotLightClipBoxReferences(LightTransformMode.Dynamic)[0];
			Assert.AreEqual((ushort)keep.Slot.Index, packed[0]);
			Assert.AreEqual((ushort)(subtract.Slot.Index | 0x8000), packed[1]);
			Assert.AreEqual(PackedClipBoxReferences.None, packed[2]);
			Assert.AreEqual(PackedClipBoxReferences.None, packed[3]);

			// An empty span removes them.
			Assert.IsTrue(_store.SetClipBoxes(light, ReadOnlySpan<ClipBoxReference>.Empty));
			Assert.IsTrue(_store.TryGetClipBoxes(light, read, out count));
			Assert.AreEqual(0, count);
		}

		[Test]
		public void LightReferences_RejectDeadHandles_AndTooManyBoxes()
		{
			var light = _store.AddSpotLight(PlacementAt(float3.zero), SpotLightData.Default);
			var boxes = new ClipBoxHandle[5];
			for (var i = 0; i < boxes.Length; i++)
				boxes[i] = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);

			Assert.IsTrue(_store.SetClipBoxes(light, new[] { ClipBoxReference.Keep(boxes[0]) }));

			var five = new ClipBoxReference[5];
			for (var i = 0; i < five.Length; i++)
				five[i] = ClipBoxReference.Keep(boxes[i]);
			Assert.Throws<ArgumentException>(() => _store.SetClipBoxes(light, five));

			_store.Remove(boxes[4]);
			Assert.IsFalse(_store.SetClipBoxes(light, new[] { ClipBoxReference.Keep(boxes[1]), ClipBoxReference.Keep(boxes[4]) }));
			Assert.IsTrue(_store.TryGetClipBoxes(light, new ClipBoxReference[4], out var count));
			Assert.AreEqual(1, count, "A rejected call changes nothing.");

			Assert.IsFalse(_store.SetClipBoxes(LightHandle.Null, new[] { ClipBoxReference.Keep(boxes[0]) }));
			Assert.IsFalse(_store.TryGetClipBoxes(LightHandle.Null, new ClipBoxReference[4], out _));
		}

		[Test]
		public void RemovingABox_DropsItFromEveryLight_AndKeepsTheOtherReferencesFirst()
		{
			var a = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);
			var b = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);
			var c = _store.AddClipBox(Track(new GameObject()).transform, ClipBoxData.Default);

			var staticLight = _store.AddSpotLight(PlacementAt(float3.zero), SpotLightData.Default);
			var dynamicLight = _store.AddSpotLight(Track(new GameObject()).transform, SpotLightData.Default);
			_store.SetClipBoxes(staticLight, new[] { ClipBoxReference.Keep(a), ClipBoxReference.Subtract(b), ClipBoxReference.Keep(c) });
			_store.SetClipBoxes(dynamicLight, new[] { ClipBoxReference.Subtract(b) });

			_store.ConsumeSpotLightClipBoxDirtyRange(LightTransformMode.Static);
			_store.ConsumeSpotLightClipBoxDirtyRange(LightTransformMode.Dynamic);

			Assert.IsTrue(_store.Remove(b));

			var read = new ClipBoxReference[4];
			_store.TryGetClipBoxes(staticLight, read, out var count);
			Assert.AreEqual(2, count);
			Assert.AreEqual(ClipBoxReference.Keep(a), read[0]);
			Assert.AreEqual(ClipBoxReference.Keep(c), read[1]);

			_store.TryGetClipBoxes(dynamicLight, read, out count);
			Assert.AreEqual(0, count);

			Assert.AreEqual(new BufferRange(0, 1), _store.ConsumeSpotLightClipBoxDirtyRange(LightTransformMode.Static));
			Assert.AreEqual(new BufferRange(0, 1), _store.ConsumeSpotLightClipBoxDirtyRange(LightTransformMode.Dynamic));

			// A new box reusing b's index doesn't reach the lights.
			var reused = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);
			Assert.AreEqual(b.Slot.Index, reused.Slot.Index);
			Assert.IsFalse(_store.GetSpotLightClipBoxReferences(LightTransformMode.Static)[0].References(reused.Slot.Index));
		}

		[Test]
		public void RemovingBoxes_DefersTheReferenceCleanup_UntilUploadOrReuse()
		{
			var boxes = new ClipBoxHandle[4];
			for (var i = 0; i < boxes.Length; i++)
				boxes[i] = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);

			var light = _store.AddSpotLight(PlacementAt(float3.zero), SpotLightData.Default);
			_store.SetClipBoxes(light, new[]
			{
				ClipBoxReference.Keep(boxes[0]), ClipBoxReference.Subtract(boxes[1]),
				ClipBoxReference.Keep(boxes[2]), ClipBoxReference.Subtract(boxes[3])
			});

			// Removals only queue.
			_store.Remove(boxes[0]);
			_store.Remove(boxes[2]);
			Assert.AreEqual(2, _store.PendingClipBoxRemovals);

			// Reading skips the removed boxes without flushing.
			var read = new ClipBoxReference[4];
			Assert.IsTrue(_store.TryGetClipBoxes(light, read, out var count));
			Assert.AreEqual(2, count);
			Assert.AreEqual(ClipBoxReference.Subtract(boxes[1]), read[0]);
			Assert.AreEqual(ClipBoxReference.Subtract(boxes[3]), read[1]);
			Assert.AreEqual(2, _store.PendingClipBoxRemovals);

			// The GPU read flushes all of them in one pass.
			var references = _store.GetSpotLightClipBoxReferences(LightTransformMode.Static);
			Assert.AreEqual(0, _store.PendingClipBoxRemovals);
			Assert.AreEqual(2, references[0].UsedCount);

			// So does adding a box (it may reuse a removed index).
			_store.Remove(boxes[1]);
			Assert.AreEqual(1, _store.PendingClipBoxRemovals);
			var reused = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);
			Assert.AreEqual(0, _store.PendingClipBoxRemovals);
			Assert.AreEqual(boxes[1].Slot.Index, reused.Slot.Index);
			Assert.IsFalse(_store.GetSpotLightClipBoxReferences(LightTransformMode.Static)[0].References(reused.Slot.Index));
		}

		[Test]
		public void RemovingBoxes_QueuesNothing_WhenTheStoreHasNoLights()
		{
			var box = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);
			_store.Remove(box);
			Assert.AreEqual(0, _store.PendingClipBoxRemovals);
		}

		[Test]
		public void RemovingALight_KeepsReferencesAlignedWithTheLights()
		{
			var boxes = new ClipBoxHandle[3];
			var lights = new LightHandle[3];
			for (var i = 0; i < 3; i++)
			{
				boxes[i] = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);
				lights[i] = _store.AddSpotLight(PlacementAt(new float3(i, 0f, 0f)), SpotLightData.Default);
				_store.SetClipBoxes(lights[i], new[] { ClipBoxReference.Keep(boxes[i]) });
			}

			Assert.IsTrue(_store.Remove(lights[0]));

			var read = new ClipBoxReference[4];
			for (var i = 1; i < 3; i++)
			{
				Assert.IsTrue(_store.TryGetClipBoxes(lights[i], read, out var count));
				Assert.AreEqual(1, count);
				Assert.AreEqual(boxes[i], read[0].Handle, $"Light {i} lost its own box after the swap-back.");
			}

			Assert.AreEqual(2, _store.GetSpotLightClipBoxReferences(LightTransformMode.Static).Length);
		}

		[Test]
		public void ClearClipBoxes_EmptiesEveryLightsReferences()
		{
			var light = _store.AddSpotLight(PlacementAt(float3.zero), SpotLightData.Default);
			var box = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);
			_store.SetClipBoxes(light, new[] { ClipBoxReference.Keep(box) });

			_store.ClearClipBoxes();

			Assert.IsFalse(_store.Contains(box));
			Assert.AreEqual(0, _store.ClipBoxCount);
			Assert.AreEqual(PackedClipBoxReferences.Empty, _store.GetSpotLightClipBoxReferences(LightTransformMode.Static)[0]);
			Assert.IsTrue(_store.Contains(light), "Lights stay.");
		}

		// ---------- Dirty ranges ----------

		[Test]
		public void BoxChanges_AndDynamicSync_MarkBoxIndices()
		{
			var a = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);
			var b = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);
			Assert.AreEqual(new BufferRange(0, 2), _store.ConsumeClipBoxDirtyRange());
			Assert.IsTrue(_store.ConsumeClipBoxDirtyRange().IsEmpty);

			_store.SetClipBoxShape(b, ClipBoxData.Default);
			Assert.AreEqual(new BufferRange(b.Slot.Index, 1), _store.ConsumeClipBoxDirtyRange());

			var dynamic = _store.AddClipBox(Track(new GameObject()).transform, ClipBoxData.Default);
			_store.ConsumeClipBoxDirtyRange();
			_store.ScheduleDynamicClipBoxSync().Complete();
			Assert.AreEqual(new BufferRange(dynamic.Slot.Index, 1), _store.ConsumeClipBoxDirtyRange(),
				"Dynamic boxes are re-packed, and marked, every sync.");

			_store.Remove(a);
			Assert.AreEqual(new BufferRange(a.Slot.Index, 1), _store.ConsumeClipBoxDirtyRange());
		}

		[Test]
		public void QueuedRemoval_FlushesWhileTheDynamicBoxSyncRuns()
		{
			// The mirror schedules the dynamic box sync before it reads the references, which flushes queued removals.
			var light = _store.AddSpotLight(PlacementAt(float3.zero), SpotLightData.Default);
			var removed = _store.AddClipBox(PlacementAt(float3.zero), ClipBoxData.Default);
			_store.AddClipBox(Track(new GameObject()).transform, ClipBoxData.Default);
			Assert.IsTrue(_store.SetClipBoxes(light, new[] { ClipBoxReference.Keep(removed) }));
			Assert.IsTrue(_store.Remove(removed));

			var sync = _store.ScheduleDynamicClipBoxSync();
			try
			{
				Assert.DoesNotThrow(() => _store.GetSpotLightClipBoxReferences(LightTransformMode.Static));
				Assert.AreEqual(0, _store.GetSpotLightClipBoxReferences(LightTransformMode.Static)[0].UsedCount);
			}
			finally
			{
				sync.Complete();
			}
		}

		// ---------- PackedClipBoxReferences ----------

		[Test]
		public void PackedReferences_EncodeSlotsAndRoles()
		{
			var references = PackedClipBoxReferences.Empty;
			Assert.AreEqual(0, references.UsedCount);

			references[0] = PackedClipBoxReferences.Encode(5, ClipBoxRole.Keep);
			references[1] = PackedClipBoxReferences.Encode(PackedClipBoxReferences.MaxBoxIndex, ClipBoxRole.Subtract);
			references[2] = PackedClipBoxReferences.Encode(9, ClipBoxRole.Subtract);
			Assert.AreEqual(3, references.UsedCount);

			Assert.IsTrue(PackedClipBoxReferences.TryDecode(references[1], out var index, out var role));
			Assert.AreEqual(PackedClipBoxReferences.MaxBoxIndex, index);
			Assert.AreEqual(ClipBoxRole.Subtract, role);
			Assert.IsFalse(PackedClipBoxReferences.TryDecode(references[3], out _, out _));
			Assert.Throws<ArgumentOutOfRangeException>(() => PackedClipBoxReferences.Encode(PackedClipBoxReferences.MaxBoxIndex + 1, ClipBoxRole.Keep));

			Assert.IsTrue(references.RemoveBox(PackedClipBoxReferences.MaxBoxIndex));
			Assert.AreEqual(2, references.UsedCount);
			Assert.AreEqual(PackedClipBoxReferences.Encode(9, ClipBoxRole.Subtract), references[1], "Later references move down.");
			Assert.IsFalse(references.RemoveBox(42));
		}

		private T Track<T>(T obj) where T : Object
		{
			_created.Add(obj);
			return obj;
		}
	}
}
