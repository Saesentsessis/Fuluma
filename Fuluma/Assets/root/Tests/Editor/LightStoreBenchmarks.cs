using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
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
	/// Main-thread cost of single <see cref="LightStore"/> calls: adding, changing and removing static and dynamic spot
	/// lights and clip boxes, one call per item. A baseline for batched APIs; runs only with <c>FULUMA_BENCHMARK</c> set.
	/// </summary>
	/// <remarks>
	/// Each operation runs <see cref="Count"/> calls per round. The mean comes from timing whole rounds with
	/// <see cref="Stopwatch.GetTimestamp"/> (median and best of <see cref="Rounds"/>); median, p99 and maximum come from a
	/// second pass that times every call on its own, so they include one timestamp read and are limited to the timer's
	/// resolution. No culling or upload jobs are running, so the calls never wait for readers.
	/// </remarks>
	public class LightStoreBenchmarks
	{
		private const string BenchmarkVariable = "FULUMA_BENCHMARK";
		private const int Count = 4096;
		private const int Rounds = 9;

		private static string CollectionsChecks
		{
			get
			{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
				return "on";
#else
				return "off";
#endif
			}
		}

		private static string Format(double nanoseconds) => double.IsNaN(nanoseconds) ? "-" : nanoseconds.ToString("F0");

		private static double TicksToNanoseconds(long ticks) => ticks * 1e9 / Stopwatch.Frequency;

		private static Placement RandomPlacement(Random random) => new()
		{
			Position = new Vector3((float)random.NextDouble() * 100f, (float)random.NextDouble() * 20f, (float)random.NextDouble() * 100f),
			Rotation = Quaternion.Euler((float)random.NextDouble() * 360f, (float)random.NextDouble() * 360f, (float)random.NextDouble() * 360f)
		};

		private readonly List<Object> _created = new();
		private readonly List<Result> _results = new();
		private Transform[] _transforms;
		private LightStore _store;
		private LightHandle[] _lights;
		private ClipBoxHandle[] _boxes;
		private Placement[] _placements;
		private int[] _order;

		[SetUp]
		public void SetUp()
		{
			if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(BenchmarkVariable)))
				Assert.Ignore($"Set {BenchmarkVariable}=1 to run the benchmark.");

			var random = new Random(11);
			_placements = new Placement[Count];
			_transforms = new Transform[Count];
			_order = new int[Count];

			for (var i = 0; i < Count; i++)
			{
				_placements[i] = RandomPlacement(random);
				var go = Track(new GameObject($"Benchmark Transform {i}"));
				go.hideFlags = HideFlags.HideAndDontSave;
				go.transform.SetPositionAndRotation(_placements[i].Position, _placements[i].Rotation);
				_transforms[i] = go.transform;
				_order[i] = i;
			}

			for (var i = Count - 1; i > 0; i--)
			{
				var j = random.Next(i + 1);
				(_order[i], _order[j]) = (_order[j], _order[i]);
			}

			_lights = new LightHandle[Count];
			_boxes = new ClipBoxHandle[Count];
			_results.Clear();
		}

		[TearDown]
		public void TearDown()
		{
			for (var i = _created.Count - 1; i >= 0; i--)
			{
				if (_created[i] != null)
					Object.DestroyImmediate(_created[i]);
			}

			_created.Clear();
			_store = null;
		}

		/// <summary>
		/// Every single-call operation on spot lights and clip boxes, printed as one table.
		/// </summary>
		[Test]
		public void Benchmark_StoreOperations()
		{
			var light = TestData.Spot(5f);
			var changed = TestData.Spot(7f);
			var shape = ClipBoxData.Default;
			var bigger = new ClipBoxData { Center = Vector3.zero, Size = new Vector3(2f, 3f, 4f) };
			var references = new ClipBoxReference[4];

			// ---------- Static spot lights ----------
			Run("AddSpotLight (static, empty store)", FreshStore,
				i => _lights[i] = _store.AddSpotLight(_placements[i], light));
			Run("SetSpotLight (static)", () => StoreWithLights(dynamic: false),
				i => _store.SetSpotLight(_lights[i], changed));
			Run("SetSpotLightPlacement", () => StoreWithLights(dynamic: false),
				i => _store.SetSpotLightPlacement(_lights[i], _placements[Count - 1 - i]));
			RunBatchedPlacements();
			Run("Remove (static spot light, random order)", () => StoreWithLights(dynamic: false),
				i => _store.Remove(_lights[_order[i]]));

			// ---------- Dynamic spot lights ----------
			Run("AddSpotLight (dynamic, empty store)", FreshStore,
				i => _lights[i] = _store.AddSpotLight(_transforms[i], light));
			Run("SetSpotLight (dynamic)", () => StoreWithLights(dynamic: true),
				i => _store.SetSpotLight(_lights[i], changed));
			Run("Remove (dynamic spot light, random order)", () => StoreWithLights(dynamic: true),
				i => _store.Remove(_lights[_order[i]]));

			// ---------- Clip boxes ----------
			Run("AddClipBox (static, empty store)", FreshStore,
				i => _boxes[i] = _store.AddClipBox(_placements[i], shape));
			Run("AddClipBox (dynamic, empty store)", FreshStore,
				i => _boxes[i] = _store.AddClipBox(_transforms[i], shape));
			Run("SetClipBoxShape", () => StoreWithBoxes(lights: false),
				i => _store.SetClipBoxShape(_boxes[i], bigger));
			Run("SetClipBoxPlacement", () => StoreWithBoxes(lights: false),
				i => _store.SetClipBoxPlacement(_boxes[i], _placements[Count - 1 - i]));
			Run("SetClipBoxes (1 keep box per light)", () => StoreWithBoxes(lights: true),
				i =>
				{
					references[0] = ClipBoxReference.Keep(_boxes[i]);
					_store.SetClipBoxes(_lights[i], new ReadOnlySpan<ClipBoxReference>(references, 0, 1));
				});
			Run("SetClipBoxes (4 boxes per light)", () => StoreWithBoxes(lights: true),
				i =>
				{
					references[0] = ClipBoxReference.Keep(_boxes[i]);
					references[1] = ClipBoxReference.Subtract(_boxes[(i + 1) % Count]);
					references[2] = ClipBoxReference.Subtract(_boxes[(i + 2) % Count]);
					references[3] = ClipBoxReference.Subtract(_boxes[(i + 3) % Count]);
					_store.SetClipBoxes(_lights[i], references);
				});
			Run("Remove (clip box, no lights)", () => StoreWithBoxes(lights: false),
				i => _store.Remove(_boxes[_order[i]]));
			Run("Remove (clip box, referenced by a light: queued)", StoreWithReferencedBoxes,
				i => _store.Remove(_boxes[_order[i]]));
			RunReferenceFlush();

			TestContext.WriteLine(Table());
		}

		/// <summary>
		/// The steps the single calls are made of, timed alone, to attribute their cost.
		/// </summary>
		[Test]
		public void Benchmark_StoreOperationSteps()
		{
			var light = TestData.Spot(5f);
			var shape = ClipBoxData.Default;
			var packedLight = light.Pack();
			var matrix = _placements[0].ToMatrix();
			var culling = new SpotLightCullingData(_placements[0], packedLight);
			var dirty = new DirtyRange();
			var list = new NativeList<PackedSpotLightData>(Count, Allocator.Persistent);
			list.Resize(Count, NativeArrayOptions.ClearMemory);
			var sink = 0f;

			try
			{
				StoreWithBoxes(lights: true);
				var dynamicLight = _store.AddSpotLight(_transforms[0], light);

				Run("SpotLightData.Pack", null, i => sink += light.Pack().Range);
				Run("Placement.Pack (position + polar direction)", null, i => sink += _placements[i].Pack().Position.x);
				Run("Placement.ToMatrix (TRS)", null, i => sink += _placements[i].ToMatrix().c3.x);
				Run("ClipBoxData.Pack(Placement)", null, i => sink += shape.Pack(_placements[i]).Row0.w);
				Run("ClipBoxData.Pack(float4x4)", null, i => sink += shape.Pack(matrix).Row0.w);
				Run("PackingKernels.PackSpotLight (Burst)", null, i =>
				{
					PackingKernels.PackSpotLight(light, out var packed);
					sink += packed.Range;
				});
				Run("PackingKernels.PlaceSpotLight (Burst)", null, i =>
				{
					PackingKernels.PlaceSpotLight(_placements[i], ref culling, out var transform, out var sphere);
					sink += sphere.w;
				});
				Run("PackingKernels.PlaceClipBox (Burst)", null, i =>
				{
					PackingKernels.PlaceClipBox(shape, _placements[i], out var placed, out var packedBox);
					sink += packedBox.Row0.w;
				});
				Run("SpotLightCullingData.Place", null, i => culling.Place(_placements[i].Position, _placements[i].Rotation));
				Run("SpotLightCullingData.Reshape", null, i => culling.Reshape(packedLight));
				Run("Contains(LightHandle)", null, i => sink += _store.Contains(_lights[i]) ? 1f : 0f);
				Run("Contains(ClipBoxHandle)", null, i => sink += _store.Contains(_boxes[i]) ? 1f : 0f);
				Run("CompleteSpotLightJobs(Static)", null, i => _store.CompleteSpotLightJobs(LightTransformMode.Static));
				Run("CompleteSpotLightJobs(Dynamic)", null, i => _store.CompleteSpotLightJobs(LightTransformMode.Dynamic));
				Run("CompleteClipBoxJobs", null, i => _store.CompleteClipBoxJobs());
				Run("default(JobHandle).Complete", null, i => default(Unity.Jobs.JobHandle).Complete());
				Run("NativeList<16 B> indexer write", null, i => list[i] = packedLight);
				Run("NativeList<16 B> indexer read", null, i => sink += list[i].Range);
				Run("DirtyRange.Mark", null, i => dirty.Mark(i));
				Run("Transform.GetPositionAndRotation", null, i => sink += Placement.From(_transforms[i]).Position.x);
				Run("Transform.localToWorldMatrix", null, i => sink += _transforms[i].localToWorldMatrix.m03);
				Run("SetSpotLight (dynamic, same light)", null, i => _store.SetSpotLight(dynamicLight, light));

				var access = new UnityEngine.Jobs.TransformAccessArray(Count);
				try
				{
					Run("TransformAccessArray.Add", () => access.SetTransforms(Array.Empty<Transform>()),
						i => access.Add(_transforms[i]));
					Run("TransformAccessArray.RemoveAtSwapBack (random order)", () => access.SetTransforms(_transforms),
						i => access.RemoveAtSwapBack(math.min(_order[i], access.length - 1)));
					Run("TransformAccessArray.RemoveAtSwapBack (last)", () => access.SetTransforms(_transforms),
						i => access.RemoveAtSwapBack(access.length - 1));
					Run("TransformAccessArray[i] = last, then remove last", () => access.SetTransforms(_transforms),
						i =>
						{
							var last = access.length - 1;
							var index = math.min(_order[i], last);
							if (index != last)
								access[index] = access[last];
							access.RemoveAtSwapBack(last);
						});
					RunOnce($"TransformAccessArray.SetTransforms ({Count}, per transform)",
						() => access.SetTransforms(_transforms));
				}
				finally
				{
					access.Dispose();
				}

				TestContext.WriteLine(Table() + $"(sink {sink})");
			}
			finally
			{
				list.Dispose();
			}
		}

		private void Run(string name, Action prepare, Action<int> call)
		{
			prepare ??= () => { };

			// Warm-up: JIT and first-use costs outside the measured rounds.
			prepare();
			for (var i = 0; i < Count; i++)
				call(i);

			var roundMeans = new double[Rounds];
			for (var round = 0; round < Rounds; round++)
			{
				prepare();
				var start = Stopwatch.GetTimestamp();
				for (var i = 0; i < Count; i++)
					call(i);
				roundMeans[round] = TicksToNanoseconds(Stopwatch.GetTimestamp() - start) / Count;
			}

			var perCall = new long[Rounds * Count];
			for (var round = 0; round < Rounds; round++)
			{
				prepare();
				for (var i = 0; i < Count; i++)
				{
					var start = Stopwatch.GetTimestamp();
					call(i);
					perCall[round * Count + i] = Stopwatch.GetTimestamp() - start;
				}
			}

			Array.Sort(roundMeans);
			Array.Sort(perCall);
			_results.Add(new Result(name, roundMeans[Rounds / 2], roundMeans[0],
				TicksToNanoseconds(perCall[perCall.Length / 2]),
				TicksToNanoseconds(perCall[(int)(perCall.Length * 0.99)]),
				TicksToNanoseconds(perCall[perCall.Length - 1])));
		}

		/// <summary>
		/// One call that handles all <see cref="Count"/> items, reported per item.
		/// </summary>
		private void RunOnce(string name, Action call)
		{
			call();
			var rounds = new double[Rounds];
			for (var round = 0; round < Rounds; round++)
			{
				var start = Stopwatch.GetTimestamp();
				call();
				rounds[round] = TicksToNanoseconds(Stopwatch.GetTimestamp() - start);
			}

			Array.Sort(rounds);
			_results.Add(new Result(name, rounds[Rounds / 2] / Count, rounds[0] / Count, double.NaN, double.NaN, double.NaN));
		}

		/// <summary>
		/// <see cref="LightStore.SetSpotLightPlacements"/> for all lights in one call, per light.
		/// </summary>
		private void RunBatchedPlacements()
		{
			var handles = new NativeArray<LightHandle>(Count, Allocator.Persistent);
			var placements = new NativeArray<Placement>(Count, Allocator.Persistent);

			try
			{
				StoreWithLights(dynamic: false);
				for (var i = 0; i < Count; i++)
				{
					handles[i] = _lights[i];
					placements[i] = _placements[Count - 1 - i];
				}

				_store.SetSpotLightPlacements(handles, placements);
				var rounds = new double[Rounds];
				for (var round = 0; round < Rounds; round++)
				{
					var start = Stopwatch.GetTimestamp();
					_store.SetSpotLightPlacements(handles, placements);
					rounds[round] = TicksToNanoseconds(Stopwatch.GetTimestamp() - start);
				}

				Array.Sort(rounds);
				_results.Add(new Result($"SetSpotLightPlacements (batched, {Count} lights)", rounds[Rounds / 2] / Count,
					rounds[0] / Count, double.NaN, double.NaN, double.NaN));
			}
			finally
			{
				handles.Dispose();
				placements.Dispose();
			}
		}

		/// <summary>
		/// The one pass that drops every queued clip box from the lights' references, after removing all boxes, per box.
		/// </summary>
		private void RunReferenceFlush()
		{
			var rounds = new double[Rounds];
			for (var round = -1; round < Rounds; round++)
			{
				StoreWithReferencedBoxes();
				for (var i = 0; i < Count; i++)
					_store.Remove(_boxes[i]);

				var start = Stopwatch.GetTimestamp();
				_store.GetSpotLightClipBoxReferences(LightTransformMode.Static);
				if (round >= 0)
					rounds[round] = TicksToNanoseconds(Stopwatch.GetTimestamp() - start);
			}

			Array.Sort(rounds);
			_results.Add(new Result($"Flush of {Count} removed boxes from {Count} lights (per box)", rounds[Rounds / 2] / Count,
				rounds[0] / Count, double.NaN, double.NaN, double.NaN));
		}

		private void FreshStore()
		{
			if (_store != null)
				Object.DestroyImmediate(_store);

			_store = Track(ScriptableObject.CreateInstance<LightStore>());
		}

		private void StoreWithLights(bool dynamic)
		{
			FreshStore();
			var light = TestData.Spot(5f);
			for (var i = 0; i < Count; i++)
				_lights[i] = dynamic ? _store.AddSpotLight(_transforms[i], light) : _store.AddSpotLight(_placements[i], light);
		}

		private void StoreWithBoxes(bool lights)
		{
			if (lights)
				StoreWithLights(dynamic: false);
			else
				FreshStore();

			for (var i = 0; i < Count; i++)
				_boxes[i] = _store.AddClipBox(_placements[i], ClipBoxData.Default);
		}

		private void StoreWithReferencedBoxes()
		{
			StoreWithBoxes(lights: true);
			Span<ClipBoxReference> reference = stackalloc ClipBoxReference[1];
			for (var i = 0; i < Count; i++)
			{
				reference[0] = ClipBoxReference.Keep(_boxes[i]);
				_store.SetClipBoxes(_lights[i], reference);
			}
		}

		private string Table()
		{
			var text = new StringBuilder();
			text.AppendLine($"BENCHMARK LightStore single calls: {Count} per round, {Rounds} rounds, " +
			                $"timer resolution {TicksToNanoseconds(1):F0} ns, collections checks {CollectionsChecks}");
			text.AppendLine($"{"Operation",-58} {"mean ns",9} {"best ns",9} {"p50 ns",9} {"p99 ns",9} {"max ns",10}");
			foreach (var result in _results)
			{
				text.AppendLine($"{result.Name,-58} {result.MeanNs,9:F0} {result.BestNs,9:F0} {Format(result.MedianNs),9} " +
				                $"{Format(result.P99Ns),9} {Format(result.MaxNs),10}");
			}

			return text.ToString();
		}

		private T Track<T>(T obj) where T : Object
		{
			_created.Add(obj);
			return obj;
		}

		private readonly struct Result
		{
			public readonly string Name;
			public readonly double MeanNs;
			public readonly double BestNs;
			public readonly double MedianNs;
			public readonly double P99Ns;
			public readonly double MaxNs;

			public Result(string name, double meanNs, double bestNs, double medianNs, double p99Ns, double maxNs)
			{
				Name = name;
				MeanNs = meanNs;
				BestNs = bestNs;
				MedianNs = medianNs;
				P99Ns = p99Ns;
				MaxNs = maxNs;
			}
		}
	}
}
