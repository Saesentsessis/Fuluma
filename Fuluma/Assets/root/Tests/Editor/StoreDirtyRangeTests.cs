using System.Collections.Generic;
using NUnit.Framework;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Data;
using Saesentsessis.Fuluma.Tests.Editor.Utils;
using UnityEngine;

namespace Saesentsessis.Fuluma.Tests.Editor
{
	public class StoreDirtyRangeTests
	{
		private LightStore _store;
		private readonly List<Object> _created = new();

		[SetUp]
		public void SetUp() => _store = Track(ScriptableObject.CreateInstance<LightStore>());

		[TearDown]
		public void TearDown()
		{
			for (var i = _created.Count - 1; i >= 0; i--)
				if (_created[i] != null)
					Object.DestroyImmediate(_created[i]);

			_created.Clear();
		}

		[Test]
		public void Add_MarksAppendedRange_AndConsumeResets()
		{
			for (var i = 0; i < 3; i++)
				_store.AddSpotLight(TestData.StaticAt(i), TestData.Spot(i));

			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out var transforms, out var lights);
			Assert.AreEqual(new BufferRange(0, 3), transforms);
			Assert.AreEqual(new BufferRange(0, 3), lights);

			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out transforms, out lights);
			Assert.IsTrue(transforms.IsEmpty);
			Assert.IsTrue(lights.IsEmpty);
		}

		[Test]
		public void SetData_And_SetTransform_MarkOnlyTheirOwnBuffer()
		{
			var handles = AddStatic(3);

			_store.SetSpotLight(handles[1], TestData.Spot(10f));
			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out var transforms, out var lights);
			Assert.IsTrue(transforms.IsEmpty);
			Assert.AreEqual(new BufferRange(1, 1), lights);

			_store.SetSpotLightPlacement(handles[2], TestData.StaticAt(10f));
			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out transforms, out lights);
			Assert.AreEqual(new BufferRange(2, 1), transforms);
			Assert.IsTrue(lights.IsEmpty);
		}

		[Test]
		public void SwapBackRemoval_MarksTheRefilledSlot_RemovingLastMarksNothing()
		{
			var handles = AddStatic(3);

			_store.Remove(handles[0]);
			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out var transforms, out var lights);
			Assert.AreEqual(new BufferRange(0, 1), transforms);
			Assert.AreEqual(new BufferRange(0, 1), lights);

			// handles[1] is the last dense entry now (handles[2] moved to slot 0).
			_store.Remove(handles[1]);
			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out transforms, out lights);
			Assert.IsTrue(transforms.IsEmpty);
			Assert.IsTrue(lights.IsEmpty);
			Assert.AreEqual(1, _store.StaticSpotLightCount);
		}

		[Test]
		public void FarApartChanges_AreCoveredByOneInterval()
		{
			var handles = AddStatic(10);

			_store.SetSpotLight(handles[2], TestData.Spot(1f));
			_store.SetSpotLight(handles[7], TestData.Spot(1f));

			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out _, out var lights);
			Assert.AreEqual(new BufferRange(2, 6), lights);
		}

		[Test]
		public void DirtyRangesBeyondCount_AreClipped()
		{
			var handles = AddStatic(4);
			_store.SetSpotLight(handles[3], TestData.Spot(1f));
			_store.Remove(handles[3]);

			// Slot 3 was dirty but no longer exists; nothing within the 3 remaining lights changed.
			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out _, out var lights);
			Assert.IsTrue(lights.IsEmpty, $"Expected an empty range, got {lights.Index}+{lights.Length}.");

			// A dirty slot below the new count survives clipping.
			_store.SetSpotLight(handles[1], TestData.Spot(1f));
			_store.SetSpotLight(handles[2], TestData.Spot(1f));
			_store.Remove(handles[2]);
			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out _, out lights);
			Assert.AreEqual(new BufferRange(1, 1), lights);
		}

		[Test]
		public void Pools_TrackDirtyRangesSeparately()
		{
			AddStatic(2);
			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out _, out _);

			_store.AddSpotLight(Track(new GameObject()).transform, TestData.Spot(1f));

			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out _, out var staticLights);
			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Dynamic, out _, out var dynamicLights);
			Assert.IsTrue(staticLights.IsEmpty);
			Assert.AreEqual(new BufferRange(0, 1), dynamicLights);
		}

		private LightHandle[] AddStatic(int count)
		{
			var handles = new LightHandle[count];
			for (var i = 0; i < count; i++)
				handles[i] = _store.AddSpotLight(TestData.StaticAt(i), TestData.Spot(i));

			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out _, out _);
			return handles;
		}

		private T Track<T>(T obj) where T : Object
		{
			_created.Add(obj);
			return obj;
		}
	}
}
