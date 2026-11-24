using System.Collections.Generic;
using NUnit.Framework;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Core.Lifecycle;
using Saesentsessis.Fuluma.Data;
using Saesentsessis.Fuluma.Rendering.Buffers;
using Saesentsessis.Fuluma.Tests.Editor.Utils;
using UnityEngine;
using UnityEngine.Rendering;

namespace Saesentsessis.Fuluma.Tests.Editor
{
	/// <summary>
	/// Reads GPU buffers back, so these need a real graphics device: run without <c>-nographics</c>.
	/// </summary>
	public class SpotLightGpuMirrorTests
	{
		private const float Tolerance = 1e-4f;

		private static T[] Read<T>(GraphicsBuffer buffer, int count) where T : struct
		{
			var result = new T[count];
			buffer.GetData(result, 0, 0, count);
			return result;
		}

		private LightStore _store;
		private SpotLightGpuMirror _mirror;
		private readonly List<Object> _created = new();

		[SetUp]
		public void SetUp()
		{
			GpuRequirement.IgnoreWithoutGraphicsDevice();

			_store = Track(ScriptableObject.CreateInstance<LightStore>());
			_mirror = SpotLightGpuMirror.Acquire(_store, 4, 4);
		}

		[TearDown]
		public void TearDown()
		{
			_mirror?.Release();
			_mirror = null;

			for (var i = _created.Count - 1; i >= 0; i--)
				if (_created[i] != null)
					Object.DestroyImmediate(_created[i]);

			_created.Clear();
		}

		[Test]
		public void Acquire_SameStore_SharesOneMirror_ReleasedByLastUser()
		{
			var before = SpotLightGpuMirror.MirrorCount;
			var second = SpotLightGpuMirror.Acquire(_store, 4, 4);

			Assert.AreSame(_mirror, second);
			Assert.AreEqual(before, SpotLightGpuMirror.MirrorCount);

			second.Release();
			Assert.AreEqual(before, SpotLightGpuMirror.MirrorCount, "One user is left.");

			_mirror.Release();
			_mirror = null;
			Assert.AreEqual(before - 1, SpotLightGpuMirror.MirrorCount);
		}

		[Test]
		public void Upload_Static_MatchesStore()
		{
			for (var i = 0; i < 5; i++)
				_store.AddSpotLight(TestData.StaticAt(i), TestData.Spot(i + 1f));

			_mirror.Upload();

			Assert.AreEqual(5, _mirror.StaticCount);
			var transforms = Read<PackedTransformData>(_mirror.StaticTransforms, 5);
			var lights = Read<PackedSpotLightData>(_mirror.StaticLights, 5);

			for (var i = 0; i < 5; i++)
			{
				Assert.AreEqual(i, transforms[i].Position.x, Tolerance);
				Assert.AreEqual(i + 1f, lights[i].Range, Tolerance);
			}
		}

		[Test]
		public void Upload_Static_SendsOnlyTheDirtyRange()
		{
			var handles = new LightHandle[4];
			for (var i = 0; i < handles.Length; i++)
				handles[i] = _store.AddSpotLight(TestData.StaticAt(i), TestData.Spot(i + 1f));

			_mirror.Upload();

			// Plant a marker on the GPU that a full upload would overwrite.
			var marker = new[] { new PackedSpotLightData { Range = -99f } };
			_mirror.StaticLights.SetData(marker, 0, 0, 1);

			_store.SetSpotLight(handles[2], TestData.Spot(42f));
			_mirror.Upload();

			var lights = Read<PackedSpotLightData>(_mirror.StaticLights, 4);
			Assert.AreEqual(-99f, lights[0].Range, Tolerance, "Slot 0 was not dirty and must not be re-uploaded.");
			Assert.AreEqual(42f, lights[2].Range, Tolerance);
		}

		[Test]
		public void Upload_Static_GrowsBuffers_AndKeepsAllData()
		{
			var initialCapacity = _mirror.StaticLights.count;
			var count = initialCapacity + 5;

			for (var i = 0; i < count; i++)
				_store.AddSpotLight(TestData.StaticAt(i), TestData.Spot(i));

			_mirror.Upload();

			Assert.GreaterOrEqual(_mirror.StaticLights.count, count);
			var lights = Read<PackedSpotLightData>(_mirror.StaticLights, count);
			for (var i = 0; i < count; i++)
				Assert.AreEqual(i, lights[i].Range, Tolerance);
		}

		[Test]
		public void Upload_Dynamic_CopiesCurrentTransforms_AndLightData()
		{
			var t = Track(new GameObject()).transform;
			_store.AddSpotLight(t, TestData.Spot(3f));
			t.position = new Vector3(7f, 8f, 9f);

			_mirror.Upload();
			_mirror.CompleteUploads();

			Assert.AreEqual(1, _mirror.DynamicCount);
			var transforms = Read<PackedTransformData>(_mirror.DynamicTransforms, 1);
			Assert.AreEqual(7f, transforms[0].Position.x, Tolerance);
			Assert.AreEqual(8f, transforms[0].Position.y, Tolerance);
			Assert.AreEqual(9f, transforms[0].Position.z, Tolerance);
			Assert.AreEqual(3f, Read<PackedSpotLightData>(_mirror.DynamicLights, 1)[0].Range, Tolerance);
		}

		[Test]
		public void Upload_Dynamic_WithOneRingBuffer_RewritesTheSameBufferEveryFrame()
		{
			var store = Track(ScriptableObject.CreateInstance<LightStore>());
			var mirror = SpotLightGpuMirror.Acquire(store, 4, 4, ringLength: 1);

			try
			{
				var t = Track(new GameObject()).transform;
				store.AddSpotLight(t, TestData.Spot(1f));

				mirror.Upload();
				mirror.CompleteUploads();
				var buffer = mirror.DynamicTransforms;

				t.position = new Vector3(5f, 0f, 0f);
				SpotLightGpuMirror.ForgetFrameGuardsForTests(); // the next frame
				mirror.Upload();
				mirror.CompleteUploads();

				Assert.AreSame(buffer, mirror.DynamicTransforms, "One buffer: no rotation.");
				Assert.AreEqual(5f, Read<PackedTransformData>(mirror.DynamicTransforms, 1)[0].Position.x, Tolerance);
			}
			finally
			{
				mirror.Release();
			}
		}

		[Test]
		public void Upload_Dynamic_TransformsWrittenOncePerFrame()
		{
			var t = Track(new GameObject()).transform;
			_store.AddSpotLight(t, TestData.Spot(1f));

			_mirror.Upload();
			_mirror.CompleteUploads();
			var buffer = _mirror.DynamicTransforms;

			t.position = new Vector3(5f, 0f, 0f);
			_mirror.Upload(); // same frame: the ring must not advance
			_mirror.CompleteUploads();

			Assert.AreSame(buffer, _mirror.DynamicTransforms);
			Assert.AreEqual(0f, Read<PackedTransformData>(buffer, 1)[0].Position.x, Tolerance);
		}

		[Test]
		public void Upload_AfterStoreRelease_DrawsNothing_ThenFullyReuploads()
		{
			_store.AddSpotLight(TestData.StaticAt(1f), TestData.Spot(1f));
			_mirror.Upload();

			FulumaLifecycle.OnLeavingMode();
			_mirror.Upload();
			Assert.AreEqual(0, _mirror.StaticCount);

			_store.AddSpotLight(TestData.StaticAt(2f), TestData.Spot(2f));
			_mirror.Upload();
			Assert.AreEqual(1, _mirror.StaticCount);
			Assert.AreEqual(2f, Read<PackedSpotLightData>(_mirror.StaticLights, 1)[0].Range, Tolerance);
		}

		[Test]
		public void RemovingDynamicLight_WhileCopyIsPending_IsSafe()
		{
			var a = _store.AddSpotLight(Track(new GameObject()).transform, TestData.Spot(1f));
			_store.AddSpotLight(Track(new GameObject()).transform, TestData.Spot(2f));

			_mirror.Upload(); // copy job in flight, ring buffer locked

			Assert.IsTrue(_store.Remove(a), "The store must wait for the copy before compacting.");
			_mirror.CompleteUploads();
		}

		// ---------- Clip boxes ----------

		[Test]
		public void Upload_DynamicClipBoxes_ComeFromTheRing_WithEveryBox()
		{
			var shape = new ClipBoxData { Size = new Vector3(2f, 4f, 6f) };
			_store.AddClipBox(new Placement { Position = new Vector3(1f, 0f, 0f), Rotation = Quaternion.identity }, shape);
			var t = Track(new GameObject()).transform;
			_store.AddClipBox(t, shape);
			t.position = new Vector3(0f, 5f, 0f); // after registration: the per-frame packing picks it up

			_mirror.Upload();
			Assert.IsTrue(_mirror.ClipBoxesFromRing, "A store with dynamic boxes uploads them through the ring.");
			_mirror.CompleteUploads();

			var boxes = Read<PackedClipBoxData>(_mirror.ClipBoxes, 2);
			Assert.IsTrue(boxes[0].Contains(new Unity.Mathematics.float3(1f, 1.5f, 2.5f)), "The static box is copied too.");
			Assert.IsFalse(boxes[0].Contains(new Unity.Mathematics.float3(1f, 2.5f, 0f)));
			Assert.IsTrue(boxes[1].Contains(new Unity.Mathematics.float3(0f, 6.5f, 0f)), "The dynamic box follows its transform.");
			Assert.IsFalse(boxes[1].Contains(new Unity.Mathematics.float3(0f, 0f, 0f)));

			var buffer = _mirror.ClipBoxes;
			_mirror.Upload(); // same frame: the ring must not advance
			_mirror.CompleteUploads();
			Assert.AreSame(buffer, _mirror.ClipBoxes);
		}

		[Test]
		public void Upload_LastDynamicClipBoxRemoved_GoesBackToChangedRangeUploads()
		{
			var shape = new ClipBoxData { Size = new Vector3(2f, 2f, 2f) };
			_store.AddClipBox(new Placement { Position = new Vector3(3f, 0f, 0f), Rotation = Quaternion.identity }, shape);
			var dynamicBox = _store.AddClipBox(Track(new GameObject()).transform, shape);

			_mirror.Upload();
			_mirror.CompleteUploads();
			Assert.IsTrue(_mirror.ClipBoxesFromRing);

			Assert.IsTrue(_store.Remove(dynamicBox));
			_mirror.Upload();
			_mirror.CompleteUploads();

			Assert.IsFalse(_mirror.ClipBoxesFromRing, "Static boxes only: back to SetData.");
			Assert.IsTrue(Read<PackedClipBoxData>(_mirror.ClipBoxes, 1)[0].Contains(new Unity.Mathematics.float3(3.5f, 0f, 0f)));
		}

		[Test]
		public void ChangingClipBoxes_WhileCopyIsPending_IsSafe()
		{
			var shape = new ClipBoxData { Size = Vector3.one };
			var a = _store.AddClipBox(Track(new GameObject()).transform, shape);
			_store.AddClipBox(Track(new GameObject()).transform, shape);

			_mirror.Upload(); // packing and copy in flight, ring buffer locked

			Assert.IsTrue(_store.SetClipBoxShape(a, new ClipBoxData { Size = Vector3.one * 2f }), "The store must wait for the copy.");
			Assert.IsTrue(_store.Remove(a));
			_store.AddClipBox(new Placement { Rotation = Quaternion.identity }, shape);
			_mirror.CompleteUploads();
		}

		private T Track<T>(T obj) where T : Object
		{
			_created.Add(obj);
			return obj;
		}
	}
}
