using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Data;
using Saesentsessis.Fuluma.Rendering.Culling;
using Saesentsessis.Fuluma.Rendering.Proxy;
using Saesentsessis.Fuluma.Rendering.URP;
using Saesentsessis.Fuluma.Tests.Editor.Utils;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = System.Random;

namespace Saesentsessis.Fuluma.Tests.Editor
{
	/// <summary>
	/// CPU culling of spot lights (<see cref="SpotLightCuller"/>): never drops a visible light, sorts lights into the two
	/// shader passes, and its cost for 65536 lights. No graphics device needed.
	/// </summary>
	public class SpotLightCullingTests
	{
		private const int MaxLights = FulumaSpotLightRenderPass.DefaultMaxVisibleLights;

		/// <summary>
		/// The LOD a screen size maps to (the number of switch sizes it's below, the last LOD's excluded), or -1 within
		/// 2% of a switch size.
		/// </summary>
		private static int ExpectedLod(float screenSize, float[] screenSizes)
		{
			var lod = 0;

			for (var i = 0; i < screenSizes.Length - 1; i++)
			{
				if (math.abs(screenSize / screenSizes[i] - 1f) < 0.02f)
					return -1;

				if (screenSize < screenSizes[i])
					lod++;
			}

			return lod;
		}

		/// <summary>
		/// Wall-clock milliseconds of each run, sorted, after <paramref name="warmUp"/> untimed runs (Burst compile,
		/// worker threads, caches).
		/// </summary>
		private static List<double> TimeMilliseconds(System.Action action, int iterations = 200, int warmUp = 10)
		{
			for (var i = 0; i < warmUp; i++)
				action();

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
			return samples;
		}

		private static T WithoutJobWorkers<T>(System.Func<T> action)
		{
			var workers = JobsUtility.JobWorkerCount;
			JobsUtility.JobWorkerCount = 0;

			try
			{
				return action();
			}
			finally
			{
				JobsUtility.JobWorkerCount = workers;
			}
		}

		private static double Median(List<double> sorted) => sorted[sorted.Count / 2];

		private static string Percentiles(List<double> sorted)
			=> $"median {Median(sorted):F3} ms, p10 {sorted[sorted.Count / 10]:F3}, p90 {sorted[sorted.Count * 9 / 10]:F3}";

		private static Vector3 RandomDirection(Random random)
		{
			Vector3 v;
			do
			{
				v = new Vector3((float)(random.NextDouble() * 2.0 - 1.0), (float)(random.NextDouble() * 2.0 - 1.0), (float)(random.NextDouble() * 2.0 - 1.0));
			} while (v.sqrMagnitude < 0.01f || v.sqrMagnitude > 1f);

			return v.normalized;
		}

		/// <summary>
		/// Points of the true volume: apex, cone side, cap and interior (cone of the clamped outer half-angle, range sphere).
		/// </summary>
		private static IEnumerable<Vector3> SampleVolume(Vector3 position, Quaternion rotation, SpotLightData data)
		{
			var halfAngle = math.radians(math.min(data.OuterAngleDegrees * 0.5f, SpotLightData.MaxOuterHalfAngleDegrees));

			yield return position;

			for (var a = 0; a < 24; a++)
			{
				var azimuth = a * (2f * math.PI / 24);

				for (var p = 1; p <= 6; p++)
				{
					var polar = halfAngle * p / 6f;
					var direction = rotation * new Vector3(math.cos(azimuth) * math.sin(polar), math.sin(azimuth) * math.sin(polar), math.cos(polar));

					for (var t = 1; t <= 6; t++)
						yield return position + direction * (data.Range * t / 6f);
				}
			}

			yield return position + rotation * Vector3.forward * data.Range;
		}

		private static bool InClip(Matrix4x4 clip, Vector3 point)
		{
			var c = clip * new Vector4(point.x, point.y, point.z, 1f);
			return c.w > 0f && math.abs(c.x) <= c.w && math.abs(c.y) <= c.w && math.abs(c.z) <= c.w;
		}

		private LightStore _store;
		private SpotLightCuller _culler;
		private GameObject _cameraObject;
		private Camera _camera;
		private float[] _lodScreenSizes;
		private SpotLightProxyShape[] _lodShapes;

		[SetUp]
		public void SetUp()
		{
			_store = ScriptableObject.CreateInstance<LightStore>();
			_culler = new SpotLightCuller();

			_cameraObject = new GameObject("Fuluma Culling Test Camera") { hideFlags = HideFlags.HideAndDontSave };
			_camera = _cameraObject.AddComponent<Camera>();
			_camera.enabled = false;
			_camera.aspect = 16f / 9f;
			_camera.fieldOfView = 60f;
			_camera.nearClipPlane = 0.3f;
			_camera.farClipPlane = 150f;

			// The built-in LOD set (3 LODs: screen sizes 0.25, 0.08, 0).
			var lods = SpotLightProxyMeshBuilder.CreateLods(null);
			_lodScreenSizes = lods.ScreenSizes;
			_lodShapes = lods.Shapes;
			Object.DestroyImmediate(lods.Mesh);
		}

		[TearDown]
		public void TearDown()
		{
			_culler.Dispose();
			Object.DestroyImmediate(_store);
			Object.DestroyImmediate(_cameraObject);
		}

		[Test]
		public void Culling_NeverDropsAVisibleLight_AndCullsMostHiddenOnes([Values(false, true)] bool orthographic)
		{
			_camera.orthographic = orthographic;
			_camera.orthographicSize = 20f;
			_camera.transform.SetPositionAndRotation(new Vector3(3f, 2f, -5f), Quaternion.Euler(10f, 25f, 0f));

			var random = new Random(777);
			var lights = new List<(Vector3 position, Quaternion rotation, SpotLightData data)>();

			for (var i = 0; i < 3000; i++)
			{
				var data = SpotLightData.Default;
				data.Range = (float)(0.5 + random.NextDouble() * 12.0);
				data.OuterAngleDegrees = (float)(2.0 + random.NextDouble() * 158.0);
				data.InnerAngleDegrees = data.OuterAngleDegrees * 0.5f;

				var position = new Vector3((float)(random.NextDouble() * 160.0 - 80.0), (float)(random.NextDouble() * 60.0 - 30.0),
					(float)(random.NextDouble() * 160.0 - 80.0));
				var rotation = Quaternion.LookRotation(RandomDirection(random));

				lights.Add((position, rotation, data));
				_store.AddSpotLight(new Placement { Position = position, Rotation = rotation }, data);
			}

			Cull();

			var visible = new HashSet<uint>(StaticInside().Concat(StaticOutside()));
			var clip = _camera.projectionMatrix * _camera.worldToCameraMatrix;
			var sampledVisible = 0;
			var keptWithoutVisibleSample = 0;

			for (var i = 0; i < lights.Count; i++)
			{
				var (position, rotation, data) = lights[i];
				var anyInside = SampleVolume(position, rotation, data).Any(p => InClip(clip, p));

				if (anyInside)
				{
					sampledVisible++;
					Assert.IsTrue(visible.Contains((uint)i), $"Light {i} at {position} has a visible part but was culled.");
				}
				else if (visible.Contains((uint)i))
				{
					keptWithoutVisibleSample++;
				}
			}

			TestContext.WriteLine($"{lights.Count} lights: {sampledVisible} with a visible sample, culling kept {visible.Count} " +
			                      $"({keptWithoutVisibleSample} without a visible sample).");

			Assert.Greater(sampledVisible, 50, "The test scene should have visible lights.");
			Assert.Less(keptWithoutVisibleSample, sampledVisible / 4 + 10, "Culling keeps too many hidden lights.");
		}

		[Test]
		public void Culling_SortsLightsIntoInsideAndOutsidePasses()
		{
			_camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

			var data = SpotLightData.Default;
			data.Range = 5f;
			data.OuterAngleDegrees = 60f;

			// 0: the camera sits at the apex. 1: in the cone near the cap. 2: far ahead. 3: beside the cone, within range.
			_store.AddSpotLight(new Placement { Position = Vector3.zero, Rotation = Quaternion.LookRotation(Vector3.forward) }, data);
			_store.AddSpotLight(new Placement { Position = new Vector3(0f, 0f, -4f), Rotation = Quaternion.LookRotation(Vector3.forward) }, data);
			_store.AddSpotLight(new Placement { Position = new Vector3(0f, 0f, 40f), Rotation = Quaternion.LookRotation(Vector3.forward) }, data);
			_store.AddSpotLight(new Placement { Position = new Vector3(-3f, 0f, 4f), Rotation = Quaternion.LookRotation(Vector3.left) }, data);

			Cull();

			var inside = StaticInside();
			var outside = StaticOutside();

			CollectionAssert.AreEquivalent(new uint[] { 0, 1 }, inside);
			CollectionAssert.AreEquivalent(new uint[] { 2, 3 }, outside);
		}

		[Test]
		public void Culling_CapsVisibleLights_AndReportsDropped()
		{
			_camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

			for (var i = 0; i < 20; i++)
				_store.AddSpotLight(new Placement { Position = new Vector3(0f, 0f, 10f + i), Rotation = Quaternion.identity }, SpotLightData.Default);

			Cull(maxLights: 8);

			Assert.AreEqual(8, _culler.Visible);
			Assert.AreEqual(12, _culler.Dropped);
		}

		[Test]
		public void Culling_FollowsMovedAndRemovedLights()
		{
			_camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

			var a = _store.AddSpotLight(new Placement { Position = new Vector3(0f, 0f, 10f), Rotation = Quaternion.identity }, SpotLightData.Default);
			var b = _store.AddSpotLight(new Placement { Position = new Vector3(0f, 0f, -50f), Rotation = Quaternion.identity }, SpotLightData.Default);

			Cull();
			Assert.AreEqual(1, _culler.Visible);

			// Move b in front of the camera, then remove a (b swaps into its dense slot).
			_store.SetSpotLightPlacement(b, new Placement { Position = new Vector3(1f, 0f, 12f), Rotation = Quaternion.identity });
			Cull();
			Assert.AreEqual(2, _culler.Visible);

			_store.Remove(a);
			Cull();
			CollectionAssert.AreEquivalent(new uint[] { 0 }, StaticOutside());
		}

		[Test]
		public void Culling_PicksLodsByScreenSize([Values(false, true)] bool orthographic)
		{
			_camera.orthographic = orthographic;
			_camera.orthographicSize = 20f;
			_camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

			var tanHalfY = orthographic ? _camera.orthographicSize : math.tan(math.radians(0.5f * _camera.fieldOfView));
			var expected = new List<int>();
			var used = new HashSet<int>();

			// Pointing sideways, ahead of the camera: always the outside pass.
			foreach (var radius in new[] { 0.5f, 2f, 6f })
			foreach (var distance in new[] { 8f, 16f, 32f, 64f, 128f })
			{
				var data = SpotLightData.Default;
				data.Range = radius;
				data.OuterAngleDegrees = 60f;

				var position = new Vector3(0f, 0f, distance);
				var rotation = Quaternion.LookRotation(Vector3.right);
				var sphere = new SpotLightCullingData(position, rotation * Vector3.forward, radius, math.cos(math.radians(30f))).BoundingSphere();

				var sphereDistance = orthographic ? 1f : math.length(sphere.xyz);
				var screenSize = sphere.w / (sphereDistance * tanHalfY);
				var lod = ExpectedLod(screenSize, _lodScreenSizes);
				if (lod < 0)
					continue; // within 2% of a switch size: either LOD is fine

				_store.AddSpotLight(new Placement { Position = position, Rotation = rotation }, data);
				expected.Add(lod);
				used.Add(lod);
			}

			Cull();

			for (var i = 0; i < expected.Count; i++)
				CollectionAssert.Contains(StaticOutside(expected[i]), (uint)i, $"Light {i} should use LOD{expected[i]}.");

			Assert.IsEmpty(StaticInside());
			Assert.Greater(used.Count, 1, "The scene should cover more than one LOD.");
			TestContext.WriteLine($"Lights per LOD: {OutsidePerLod()}");
		}

		[Test]
		public void Culling_Orthographic_OnlyLightsNearTheNearPlaneUseTheInsidePass()
		{
			_camera.orthographic = true;
			_camera.orthographicSize = 20f;
			_camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

			var data = SpotLightData.Default;
			data.Range = 5f;
			data.OuterAngleDegrees = 60f;

			// 0: apex behind the camera, cone reaching through the near plane. 1: far ahead, pointing sideways.
			_store.AddSpotLight(new Placement { Position = new Vector3(0f, 0f, -2f), Rotation = Quaternion.LookRotation(Vector3.forward) }, data);
			_store.AddSpotLight(new Placement { Position = new Vector3(0f, 0f, 60f), Rotation = Quaternion.LookRotation(Vector3.right) }, data);

			Cull();

			CollectionAssert.AreEquivalent(new uint[] { 0 }, StaticInside());
			CollectionAssert.AreEquivalent(new uint[] { 1 }, StaticOutside());
		}

		[Test]
		public void Culling_SingleLod_DrawsEveryOutsideLightWithIt()
		{
			_camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

			for (var i = 0; i < 10; i++)
				_store.AddSpotLight(new Placement { Position = new Vector3(0f, 0f, 10f + 12f * i), Rotation = Quaternion.LookRotation(Vector3.right) }, SpotLightData.Default);

			_culler.Schedule(_store, SingleLodCullingCamera(), _store.StaticSpotLightCount, 0);
			_culler.Complete(MaxLights);

			Assert.AreEqual(1, _culler.LodCount);
			Assert.AreEqual(10, StaticOutside(0).Length);
			Assert.AreEqual(0, _culler.OutsideDraw(LightTransformMode.Static, 1).length, "There is no LOD1.");
		}

		/// <summary>
		/// With clip classes, every draw region lists its lights without boxes first, then with one box, then keep +
		/// subtract (either order), then two keeps, then two subtracts, then the rest, and the counts it reports match (also
		/// when the maximum cuts regions short).
		/// </summary>
		[TestCase(true, MaxLights)]
		[TestCase(false, MaxLights)]
		[TestCase(true, 25)]
		public void Culling_ClipClasses_OrderEveryRegionByBoxCount(bool lods, int maxLights)
		{
			_camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

			var room = _store.AddClipBox(new Placement { Position = new Vector3(0f, 0f, 20f), Rotation = Quaternion.identity }, ClipBoxData.Default);
			var hole = _store.AddClipBox(new Placement { Position = new Vector3(0f, 0f, 20f), Rotation = Quaternion.identity }, ClipBoxData.Default);
			var hall = _store.AddClipBox(new Placement { Position = new Vector3(0f, 0f, 30f), Rotation = Quaternion.identity }, ClipBoxData.Default);

			// Per configuration: its references and the class they belong to.
			var configurations = new (ClipBoxReference[] boxes, int clipClass)[]
			{
				(new ClipBoxReference[0], 0),
				(new[] { ClipBoxReference.Keep(room) }, 1),
				(new[] { ClipBoxReference.Subtract(hole) }, 1),
				(new[] { ClipBoxReference.Keep(room), ClipBoxReference.Subtract(hole) }, 2),
				(new[] { ClipBoxReference.Subtract(hole), ClipBoxReference.Keep(room) }, 2),
				(new[] { ClipBoxReference.Keep(room), ClipBoxReference.Keep(hall) }, 3),
				(new[] { ClipBoxReference.Subtract(hole), ClipBoxReference.Subtract(hall) }, 4),
				(new[] { ClipBoxReference.Keep(room), ClipBoxReference.Subtract(hole), ClipBoxReference.Subtract(hall) }, 5),
				(new[] { ClipBoxReference.Keep(room), ClipBoxReference.Keep(hall), ClipBoxReference.Subtract(hole), ClipBoxReference.Subtract(hall) }, 5)
			};

			// Lights from right at the camera (inside pass) to far away (coarse LODs), classes interleaved.
			var classes = new int[90];
			for (var i = 0; i < classes.Length; i++)
			{
				var light = _store.AddSpotLight(new Placement
				{
					Position = new Vector3((i % 5 - 2) * 0.7f, 0f, 0.5f + i * 1.3f),
					Rotation = Quaternion.LookRotation(Vector3.right)
				}, SpotLightData.Default);

				var configuration = configurations[i % configurations.Length];
				classes[i] = configuration.clipClass;
				Assert.IsTrue(_store.SetClipBoxes(light, configuration.boxes));
			}

			var camera = lods ? CullingCamera(0.3f) : SingleLodCullingCamera(0.3f);
			_culler.Schedule(_store, camera, _store.StaticSpotLightCount, 0, partitionByClipClass: true);
			_culler.Complete(maxLights);
			Assert.IsTrue(_culler.PartitionedByClipClass);

			var seen = new int[SpotLightClipClassJob.ClassCount];
			void Check(RangeInt range, SpotLightClipClassCounts clipClasses, string name)
			{
				Assert.LessOrEqual(clipClasses.Stored, range.length, name);
				var indices = _culler.GetIndices(range);
				for (var i = 0; i < indices.Length; i++)
				{
					// The class whose run holds position i (past every stored run: general).
					var expected = 0;
					for (var run = clipClasses[0]; expected < SpotLightClipClassJob.General && i >= run; run += clipClasses[expected])
						expected++;
					Assert.AreEqual(expected, classes[indices[i]], $"{name}: light {indices[i]} at {i} of {indices.Length} ({clipClasses}).");
					seen[expected]++;
				}
			}

			Check(_culler.InsideDraw(LightTransformMode.Static, out var insideClasses), insideClasses, "inside");
			for (var lod = 0; lod < _culler.LodCount; lod++)
			{
				Check(_culler.OutsideDraw(LightTransformMode.Static, lod, out var outsideClasses), outsideClasses, $"lod{lod}");
				Check(_culler.LargeDraw(LightTransformMode.Static, lod, out var largeClasses), largeClasses, $"large{lod}");
			}

			TestContext.WriteLine($"CLASSES visible {_culler.Visible} (dropped {_culler.Dropped}): {string.Join(" / ", seen)}");
			Assert.AreEqual(_culler.Visible, seen.Sum());
			// Every class shows up when nothing is cut (a cut keeps each region's beginning: the first classes).
			for (var c = 0; c < (maxLights < MaxLights ? 1 : seen.Length); c++)
				Assert.Greater(seen[c], 0, $"class {c}");
			if (maxLights < MaxLights)
				Assert.Greater(_culler.Dropped, 0, "The maximum should cut some regions short.");

			// Without the partition: no classes (every light counts as having several boxes).
			_culler.Schedule(_store, camera, _store.StaticSpotLightCount, 0);
			_culler.Complete(maxLights);
			Assert.IsFalse(_culler.PartitionedByClipClass);
			_culler.OutsideDraw(LightTransformMode.Static, 0, out var none);
			Assert.AreEqual(0, none.Stored);
		}

		[Test]
		public void Culling_FastJob_MatchesTheLodJobWithOneLod([Values(false, true)] bool orthographic)
		{
			_camera.orthographic = orthographic;
			_camera.orthographicSize = 20f;
			_camera.transform.SetPositionAndRotation(new Vector3(3f, 2f, -5f), Quaternion.Euler(10f, 25f, 0f));

			var random = new Random(4040);
			for (var i = 0; i < 3000; i++)
			{
				var data = SpotLightData.Default;
				data.Range = (float)(0.5 + random.NextDouble() * 12.0);
				data.OuterAngleDegrees = (float)(2.0 + random.NextDouble() * 158.0);

				var position = new Vector3((float)(random.NextDouble() * 60.0 - 30.0), (float)(random.NextDouble() * 20.0 - 10.0),
					(float)(random.NextDouble() * 60.0 - 30.0));
				_store.AddSpotLight(new Placement { Position = position, Rotation = Quaternion.LookRotation(RandomDirection(random)) }, data);
			}

			var camera = SingleLodCullingCamera();

			_culler.ForceLodJob = true;
			_culler.Schedule(_store, camera, _store.StaticSpotLightCount, 0);
			_culler.Complete(MaxLights);
			Assert.IsFalse(_culler.LastUsedFastJob);
			var lodInside = StaticInside().OrderBy(i => i).ToArray();
			var lodOutside = StaticOutside(0).OrderBy(i => i).ToArray();

			_culler.ForceLodJob = false;
			_culler.Schedule(_store, camera, _store.StaticSpotLightCount, 0);
			_culler.Complete(MaxLights);
			Assert.IsTrue(_culler.LastUsedFastJob);

			CollectionAssert.AreEqual(lodInside, StaticInside().OrderBy(i => i).ToArray(), "Inside lights differ.");
			CollectionAssert.AreEqual(lodOutside, StaticOutside(0).OrderBy(i => i).ToArray(), "Outside lights differ.");
			Assert.Greater(lodInside.Length + lodOutside.Length, 100, "The scene should have visible lights.");
		}

		/// <summary>
		/// LargeOnly light buffer: with a large-light screen size, outside lights at least that large on screen move from
		/// their LOD bucket to the matching large bucket; nothing else changes (inside lights, LODs, small lights), and no
		/// light is lost or duplicated.
		/// </summary>
		[Test]
		public void Culling_LargeLightSplit_MovesOnlyLargeOutsideLights([Values(false, true)] bool orthographic,
			[Values(true, false)] bool lods)
		{
			const float largeScreenSize = 0.15f;

			_camera.orthographic = orthographic;
			_camera.orthographicSize = 20f;
			_camera.transform.SetPositionAndRotation(new Vector3(3f, 2f, -5f), Quaternion.Euler(10f, 25f, 0f));
			AddRandomLights(5151);

			_culler.Schedule(_store, lods ? CullingCamera() : SingleLodCullingCamera(), _store.StaticSpotLightCount, 0);
			_culler.Complete(MaxLights);
			Assert.IsFalse(_culler.SplitsLargeLights);
			Assert.AreEqual(0, _culler.LowResolutionLights);
			var before = StaticBuckets();

			_culler.Schedule(_store, lods ? CullingCamera(largeScreenSize) : SingleLodCullingCamera(largeScreenSize), _store.StaticSpotLightCount, 0);
			_culler.Complete(MaxLights);
			Assert.IsTrue(_culler.SplitsLargeLights);
			Assert.AreEqual(lods == false, _culler.LastUsedFastJob);
			var after = StaticBuckets();

			CollectionAssert.AreEquivalent(before.Keys, after.Keys, "The split should keep exactly the same visible lights.");

			_store.GetSpotLightCullingInput(LightTransformMode.Static, out var spheres, out _, out _);
			var tanHalfY = orthographic ? _camera.orthographicSize : math.tan(math.radians(0.5f * _camera.fieldOfView));
			int large = 0, small = 0, inside = 0;

			foreach (var (index, bucket) in before)
			{
				if (bucket == "inside")
				{
					inside++;
					Assert.AreEqual("inside", after[index], $"Inside light {index} should stay inside.");
					continue;
				}

				var sphere = spheres[(int)index];
				var distance = orthographic ? 1f : math.distance(sphere.xyz, (float3)_camera.transform.position);
				var screenSize = sphere.w / (distance * tanHalfY);
				var lod = bucket.Substring("lod".Length);

				if (math.abs(screenSize / largeScreenSize - 1f) < 0.02f)
				{
					// Within 2% of the switch size: either bucket is fine, but the LOD must not change.
					Assert.That(after[index], Is.EqualTo("lod" + lod).Or.EqualTo("large" + lod), $"Light {index}");
					continue;
				}

				var isLarge = screenSize >= largeScreenSize;
				large += isLarge ? 1 : 0;
				small += isLarge ? 0 : 1;
				Assert.AreEqual((isLarge ? "large" : "lod") + lod, after[index], $"Light {index}, screen size {screenSize:F3}");
			}

			TestContext.WriteLine($"Split {(lods ? "LOD job" : "fast job")}{(orthographic ? ", orthographic" : "")}: inside {inside}, " +
			                      $"large {large}, small {small}, low resolution {_culler.LowResolutionLights}");

			Assert.AreEqual(after.Values.Count(b => b == "inside" || b.StartsWith("large")), _culler.LowResolutionLights);
			Assert.Greater(large, 10, "The scene should have large lights.");
			Assert.Greater(small, 10, "The scene should have small lights.");
		}

		[Test]
		public void Culling_LargeLightSplit_FastJobMatchesTheLodJobWithOneLod([Values(false, true)] bool orthographic)
		{
			_camera.orthographic = orthographic;
			_camera.orthographicSize = 20f;
			_camera.transform.SetPositionAndRotation(new Vector3(3f, 2f, -5f), Quaternion.Euler(10f, 25f, 0f));
			AddRandomLights(6262);

			var camera = SingleLodCullingCamera(0.15f);

			_culler.ForceLodJob = true;
			_culler.Schedule(_store, camera, _store.StaticSpotLightCount, 0);
			_culler.Complete(MaxLights);
			Assert.IsFalse(_culler.LastUsedFastJob);
			var lodJob = StaticBuckets();
			var lodJobLowResolution = _culler.LowResolutionLights;

			_culler.ForceLodJob = false;
			_culler.Schedule(_store, camera, _store.StaticSpotLightCount, 0);
			_culler.Complete(MaxLights);
			Assert.IsTrue(_culler.LastUsedFastJob);

			CollectionAssert.AreEquivalent(lodJob, StaticBuckets(), "The jobs should sort every light into the same bucket.");
			Assert.AreEqual(lodJobLowResolution, _culler.LowResolutionLights);
			Assert.Greater(lodJob.Values.Count(b => b == "large0"), 10, "The scene should have large lights.");
		}

		/// <summary>
		/// CPU time of culling 65536 static lights for one camera (schedule + complete, Burst, all worker threads). Two
		/// scenes: lights all around the camera (about a sixth visible) and all in front of it; with the built-in LOD set
		/// (<see cref="SpotLightCullingJob"/>) and with LOD0 only (<see cref="SpotLightCullingFastJob"/>).
		/// </summary>
		[Test]
		public void Benchmark_Culling65536Lights([Values(false, true)] bool allInFront, [Values(true, false)] bool lods,
			[Values(false, true)] bool splitLargeLights)
		{
			if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("FULUMA_BENCHMARK")))
				Assert.Ignore("Set FULUMA_BENCHMARK=1 to run the benchmark.");

			const int count = 65536;
			var scene = (allInFront ? "all in front" : "all around") + (lods ? ", LODs" : ", no LODs (fast job)")
			            + (splitLargeLights ? ", large lights split off" : "");

			_camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
			AddBenchmarkLights(count, allInFront);

			var largeScreenSize = splitLargeLights ? LightBufferScaling.DefaultLargeLightScreenSize : 0f;
			var cullingCamera = lods ? CullingCamera(largeScreenSize) : SingleLodCullingCamera(largeScreenSize);
			void CullAll() => CullOnce(cullingCamera, count);

			var samples = TimeMilliseconds(CullAll);
			TestContext.WriteLine($"BENCHMARK culling {count} lights ({scene}), {SystemInfo.processorType}, " +
			                      $"{JobsUtility.JobWorkerCount} workers: {Percentiles(samples)}, visible {_culler.Visible} " +
			                      $"(inside {StaticInside().Length}, outside per LOD {OutsidePerLod()}, low resolution {_culler.LowResolutionLights})");

			// Same work on one thread (no workers: jobs run on the main thread): the job's own cost, without scheduling
			// and work-stealing noise. What a single mobile core would spend, scaled by its speed.
			samples = WithoutJobWorkers(() => TimeMilliseconds(CullAll));
			TestContext.WriteLine($"BENCHMARK culling {count} lights ({scene}), single thread: {Percentiles(samples)} " +
			                      $"({Median(samples) * 1e6 / count:F1} ns per light)");

			// Main-thread cost of uploading the visible indices (SetData), when there is a graphics device.
			if (GpuRequirement.HasGraphicsDevice == false)
				return;

			samples = TimeMilliseconds(_culler.Upload, warmUp: 1);
			TestContext.WriteLine($"BENCHMARK upload of {_culler.Visible} visible indices ({SystemInfo.graphicsDeviceType}): " +
			                      $"{Percentiles(samples)}");
		}

		/// <summary>
		/// Static lights of random size and direction, all around the camera or all in front of it.
		/// </summary>
		private void AddBenchmarkLights(int count, bool allInFront)
		{
			var random = new Random(4242);

			for (var i = 0; i < count; i++)
			{
				var data = SpotLightData.Default;
				data.Range = (float)(1.0 + random.NextDouble() * 4.0);
				data.OuterAngleDegrees = (float)(20.0 + random.NextDouble() * 100.0);

				var position = allInFront
					? new Vector3((float)(random.NextDouble() * 40.0 - 20.0), (float)(random.NextDouble() * 20.0 - 10.0), (float)(10.0 + random.NextDouble() * 60.0))
					: new Vector3((float)(random.NextDouble() * 200.0 - 100.0), (float)(random.NextDouble() * 40.0 - 20.0), (float)(random.NextDouble() * 200.0 - 100.0));

				_store.AddSpotLight(new Placement { Position = position, Rotation = Quaternion.LookRotation(RandomDirection(random)) }, data);
			}
		}

		// ---------- Helpers ----------

		private void Cull(int maxLights = MaxLights)
		{
			_culler.Schedule(_store, CullingCamera(), _store.StaticSpotLightCount, _store.DynamicSpotLightCount);
			_culler.Complete(maxLights);
		}

		private void CullOnce(in SpotLightCullingCamera camera, int count)
		{
			_culler.Schedule(_store, camera, count, 0);
			_culler.Complete(MaxLights);
		}

		/// <param name="largeLightScreenSize">Above 0: split large outside lights off (LargeOnly light buffer).</param>
		private SpotLightCullingCamera CullingCamera(float largeLightScreenSize = 0f)
			=> SpotLightCullingCamera.Create(_camera.worldToCameraMatrix, _camera.projectionMatrix, _camera.transform.position,
				_camera.orthographic, _camera.nearClipPlane, _lodScreenSizes, _lodShapes, largeLightScreenSize);

		/// <summary>
		/// The camera with only the first LOD.
		/// </summary>
		private SpotLightCullingCamera SingleLodCullingCamera(float largeLightScreenSize = 0f)
			=> SpotLightCullingCamera.Create(_camera.worldToCameraMatrix, _camera.projectionMatrix, _camera.transform.position,
				_camera.orthographic, _camera.nearClipPlane, _lodScreenSizes.AsSpan(0, 1), _lodShapes.AsSpan(0, 1), largeLightScreenSize);

		private uint[] StaticLarge(int lod) => _culler.GetIndices(_culler.LargeDraw(LightTransformMode.Static, lod)).ToArray();

		/// <summary>
		/// Every static light's bucket after the last culling: "inside", "lod{n}" or "large{n}".
		/// </summary>
		private Dictionary<uint, string> StaticBuckets()
		{
			var buckets = new Dictionary<uint, string>();

			void Put(IEnumerable<uint> indices, string bucket)
			{
				foreach (var index in indices)
				{
					Assert.IsFalse(buckets.ContainsKey(index), $"Light {index} is in two buckets ({buckets.GetValueOrDefault(index)}, {bucket}).");
					buckets[index] = bucket;
				}
			}

			Put(StaticInside(), "inside");
			for (var lod = 0; lod < _culler.LodCount; lod++)
			{
				Put(StaticOutside(lod), $"lod{lod}");
				Put(StaticLarge(lod), $"large{lod}");
			}

			return buckets;
		}

		/// <summary>
		/// 3000 static lights of random size, angle and direction around a tilted camera.
		/// </summary>
		private void AddRandomLights(int seed)
		{
			var random = new Random(seed);
			for (var i = 0; i < 3000; i++)
			{
				var data = SpotLightData.Default;
				data.Range = (float)(0.5 + random.NextDouble() * 12.0);
				data.OuterAngleDegrees = (float)(2.0 + random.NextDouble() * 158.0);

				var position = new Vector3((float)(random.NextDouble() * 60.0 - 30.0), (float)(random.NextDouble() * 20.0 - 10.0),
					(float)(random.NextDouble() * 60.0 - 30.0));
				_store.AddSpotLight(new Placement { Position = position, Rotation = Quaternion.LookRotation(RandomDirection(random)) }, data);
			}
		}

		private uint[] StaticInside() => _culler.GetIndices(_culler.InsideDraw(LightTransformMode.Static)).ToArray();

		private uint[] StaticOutside(int lod) => _culler.GetIndices(_culler.OutsideDraw(LightTransformMode.Static, lod)).ToArray();

		/// <summary>
		/// Outside-pass lights of every LOD.
		/// </summary>
		private uint[] StaticOutside()
			=> Enumerable.Range(0, _culler.LodCount).SelectMany(StaticOutside).ToArray();

		private string OutsidePerLod()
			=> string.Join("/", Enumerable.Range(0, _culler.LodCount).Select(lod => _culler.OutsideDraw(LightTransformMode.Static, lod).length));
	}
}
