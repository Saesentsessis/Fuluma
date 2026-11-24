using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Data;
using Saesentsessis.Fuluma.Tests.Editor.Utils;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = System.Random;

namespace Saesentsessis.Fuluma.Tests.Editor
{
	/// <summary>
	/// <see cref="LightStore.SetSpotLightPlacements"/>: the batched move of static spot lights.
	/// </summary>
	public class StaticTransformBatchTests
	{
		/// <summary>
		/// Each 16-bit polar angle within one step.
		/// </summary>
		private static void AssertPackedDirectionClose(uint expected, uint actual, int light)
		{
			var low = Math.Abs((int)(expected & 0xFFFF) - (int)(actual & 0xFFFF));
			var high = Math.Abs((int)(expected >> 16) - (int)(actual >> 16));
			Assert.LessOrEqual(Math.Max(low, high), 1, $"Packed direction of light {light}: {expected:X8} vs {actual:X8}.");
		}

		private static void AssertClose(float4 expected, float4 actual, string message)
			=> Assert.Less(math.cmax(math.abs(expected - actual)), 1e-4f, $"{message} {expected} vs {actual}");

		private static double Measure(int iterations, Action action)
		{
			action(); // warm-up: Burst compile, JIT

			var samples = new List<double>(iterations);
			var stopwatch = new Stopwatch();

			for (var i = 0; i < iterations; i++)
			{
				stopwatch.Restart();
				action();
				stopwatch.Stop();
				samples.Add(stopwatch.Elapsed.TotalMilliseconds);
			}

			samples.Sort();
			return samples[iterations / 2];
		}

		private static Placement RandomPlacement(Random random) => new()
		{
			Position = new Vector3((float)random.NextDouble() * 100f, (float)random.NextDouble() * 20f, (float)random.NextDouble() * 100f),
			Rotation = Quaternion.Euler((float)random.NextDouble() * 360f, (float)random.NextDouble() * 360f, (float)random.NextDouble() * 360f)
		};

		private static int[] Shuffled(int count, Random random)
		{
			var order = new int[count];
			for (var i = 0; i < count; i++)
				order[i] = i;

			for (var i = count - 1; i > 0; i--)
			{
				var j = random.Next(i + 1);
				(order[i], order[j]) = (order[j], order[i]);
			}

			return order;
		}

		private static T[] ToArray<T>(NativeArray<T> array) where T : struct => array.ToArray();

		private readonly List<Object> _created = new();
		private readonly List<IDisposable> _disposables = new();
		private LightStore _store;

		[SetUp]
		public void SetUp() => _store = Track(ScriptableObject.CreateInstance<LightStore>());

		[TearDown]
		public void TearDown()
		{
			foreach (var disposable in _disposables)
				disposable.Dispose();

			for (var i = _created.Count - 1; i >= 0; i--)
				if (_created[i] != null)
					Object.DestroyImmediate(_created[i]);

			_disposables.Clear();
			_created.Clear();
		}

		[Test]
		public void Batch_MatchesOneCallPerLight()
		{
			const int count = 1000; // several batches, the last one partial
			var reference = Track(ScriptableObject.CreateInstance<LightStore>());

			var handles = Native<LightHandle>(count);
			var referenceHandles = new LightHandle[count];
			var random = new Random(31);

			for (var i = 0; i < count; i++)
			{
				var light = TestData.Spot(1f + i % 7);
				var placement = RandomPlacement(random);
				handles[i] = _store.AddSpotLight(placement, light);
				referenceHandles[i] = reference.AddSpotLight(placement, light);
			}

			// Move in a shuffled order, so batches touch scattered dense indices.
			var order = Shuffled(count, random);
			var batchHandles = Native<LightHandle>(count);
			var placements = Native<Placement>(count);

			for (var i = 0; i < count; i++)
			{
				var placement = RandomPlacement(random);
				batchHandles[i] = handles[order[i]];
				placements[i] = placement;
				reference.SetSpotLightPlacement(referenceHandles[order[i]], placement);
			}

			Assert.AreEqual(count, _store.SetSpotLightPlacements(batchHandles, placements));

			// Burst (fast float math) and managed code may round the trigonometry differently.
			var expected = ToArray(reference.GetSpotLightTransforms(LightTransformMode.Static));
			var actual = ToArray(_store.GetSpotLightTransforms(LightTransformMode.Static));
			Assert.AreEqual(expected.Length, actual.Length);
			for (var i = 0; i < expected.Length; i++)
			{
				Assert.AreEqual((Vector3)expected[i].Position, (Vector3)actual[i].Position, $"Position of light {i}.");
				AssertPackedDirectionClose(expected[i].PackedDirection, actual[i].PackedDirection, i);
			}

			reference.GetSpotLightCullingInput(LightTransformMode.Static, out var referenceSpheres, out var referenceCulling, out _);
			_store.GetSpotLightCullingInput(LightTransformMode.Static, out var spheres, out var culling, out _);
			for (var i = 0; i < count; i++)
			{
				AssertClose(referenceSpheres[i], spheres[i], $"Bounding sphere of light {i}.");
				AssertClose(referenceCulling[i].PositionRange, culling[i].PositionRange, $"Culling apex of light {i}.");
				AssertClose(referenceCulling[i].DirectionCosOuter, culling[i].DirectionCosOuter, $"Culling axis of light {i}.");
			}
		}

		[Test]
		public void Batch_SkipsDeadDynamicAndForeignHandles()
		{
			var other = Track(ScriptableObject.CreateInstance<LightStore>());
			var follow = Track(new GameObject("Fuluma Batch Test Light"));

			var alive = _store.AddSpotLight(TestData.StaticAt(1f), TestData.Spot(5f));
			var removed = _store.AddSpotLight(TestData.StaticAt(2f), TestData.Spot(5f));
			var dynamic = _store.AddSpotLight(follow.transform, TestData.Spot(5f));
			var foreign = other.AddSpotLight(TestData.StaticAt(3f), TestData.Spot(5f));
			_store.Remove(removed);

			var before = ToArray(_store.GetSpotLightTransforms(LightTransformMode.Static));
			var otherBefore = ToArray(other.GetSpotLightTransforms(LightTransformMode.Static));

			var handles = Native(alive, removed, dynamic, foreign, LightHandle.Null);
			var placements = Native<Placement>(handles.Length);
			for (var i = 0; i < placements.Length; i++)
				placements[i] = TestData.StaticAt(100f + i);

			Assert.AreEqual(1, _store.SetSpotLightPlacements(handles, placements));

			var after = ToArray(_store.GetSpotLightTransforms(LightTransformMode.Static));
			Assert.AreEqual(before.Length, after.Length);
			Assert.AreEqual(TestData.StaticAt(100f).Pack(), after[0], "The live light should have moved.");
			CollectionAssert.AreEqual(otherBefore, ToArray(other.GetSpotLightTransforms(LightTransformMode.Static)), "Another store's light moved.");
		}

		[Test]
		public void Batch_MarksOnlyTheMovedRangeDirty()
		{
			var handles = new LightHandle[10];
			for (var i = 0; i < handles.Length; i++)
				handles[i] = _store.AddSpotLight(TestData.StaticAt(i), TestData.Spot(5f));

			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out _, out _);

			_store.SetSpotLightPlacements(Native(handles[7], handles[3]), Native(TestData.StaticAt(70f), TestData.StaticAt(30f)));

			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out var transforms, out var lights);
			Assert.AreEqual(new BufferRange(3, 5), transforms);
			Assert.IsTrue(lights.IsEmpty, "Light data didn't change.");
		}

		[Test]
		public void Batch_NothingToMove_ChangesNothing()
		{
			_store.AddSpotLight(TestData.StaticAt(0f), TestData.Spot(5f));
			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out _, out _);

			Assert.AreEqual(0, _store.SetSpotLightPlacements(Native(LightHandle.Null), Native(TestData.StaticAt(1f))));
			Assert.AreEqual(0, _store.SetSpotLightPlacements(Native<LightHandle>(0), Native<Placement>(0)));

			_store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out var transforms, out _);
			Assert.IsTrue(transforms.IsEmpty);
		}

		[Test]
		public void Batch_LengthMismatch_Throws()
		{
			var handle = _store.AddSpotLight(TestData.StaticAt(0f), TestData.Spot(5f));

			Assert.Throws<ArgumentException>(() =>
				_store.SetSpotLightPlacements(Native(handle), Native<Placement>(2)));
		}

		/// <summary>
		/// Main-thread time to move 65536 static lights: one batched call against one call per light.
		/// </summary>
		[Test]
		public void Benchmark_Move65536StaticLights()
		{
			if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FULUMA_BENCHMARK")))
				Assert.Ignore("Set FULUMA_BENCHMARK=1 to run the benchmark.");

			const int count = 65536;
			const int iterations = 30;

			var random = new Random(7);
			var handles = Native<LightHandle>(count);
			var placements = Native<Placement>(count);

			for (var i = 0; i < count; i++)
			{
				handles[i] = _store.AddSpotLight(RandomPlacement(random), TestData.Spot(5f));
				placements[i] = RandomPlacement(random);
			}

			var batched = Measure(iterations, () => _store.SetSpotLightPlacements(handles, placements));
			var single = Measure(iterations, () =>
			{
				for (var i = 0; i < count; i++)
					_store.SetSpotLightPlacement(handles[i], placements[i]);
			});

			TestContext.WriteLine($"BENCHMARK moving {count} static lights: batched median {batched:F3} ms, " +
			                      $"one call per light median {single:F3} ms ({single / batched:F0}x)");
		}

		private NativeArray<T> Native<T>(int length) where T : struct
		{
			var array = new NativeArray<T>(length, Allocator.Persistent);
			_disposables.Add(array);
			return array;
		}

		private NativeArray<T> Native<T>(params T[] values) where T : struct
		{
			var array = new NativeArray<T>(values, Allocator.Persistent);
			_disposables.Add(array);
			return array;
		}

		private T Track<T>(T obj) where T : Object
		{
			_created.Add(obj);
			return obj;
		}
	}
}
