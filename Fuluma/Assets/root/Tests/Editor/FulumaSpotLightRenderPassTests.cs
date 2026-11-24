using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Data;
using Saesentsessis.Fuluma.Rendering.Buffers;
using Saesentsessis.Fuluma.Rendering.Culling;
using Saesentsessis.Fuluma.Rendering.Proxy;
using Saesentsessis.Fuluma.Rendering.URP;
using Saesentsessis.Fuluma.Tests.Editor.Utils;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using static Saesentsessis.Fuluma.Tests.Editor.Utils.TestImages;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Tests.Editor
{
	/// <summary>
	/// Spot light shader and render pass (proxy meshes: <see cref="ProxyMeshTests"/>). Needs a graphics device with compute and vertex-stage structured
	/// buffers (run without -nographics).
	/// </summary>
#if UNITY_6000_5_OR_NEWER
	// Immutable: the gallery's camera table.
	[NoAutoStaticsCleanup]
#endif
	public class FulumaSpotLightRenderPassTests
	{
		private const string ShaderName = "Hidden/Fuluma/Analytical/SpotLight";

		private const int TimingSceneSize = 128;

		// ---------- Helpers ----------

		private const string ImageDirectoryVariable = "FULUMA_TEST_IMAGE_DIR";
		private const string BenchmarkVariable = "FULUMA_BENCHMARK";

		// ---------- Gallery (for eyes; only runs when FULUMA_TEST_IMAGE_DIR is set) ----------

		private static readonly (string name, Vector3 camera, Vector3 target, float fov, int light)[] GalleryViews =
		{
			("side", new Vector3(12f, 0f, 4f), new Vector3(0f, 0f, 4f), 60f, 0),
			("above-oblique", new Vector3(8f, 6f, -2f), new Vector3(0f, 0f, 4f), 60f, 0),
			("into-cap", new Vector3(0f, 0f, 14f), Vector3.zero, 60f, 0),
			("behind-apex", new Vector3(0f, 0f, -6f), new Vector3(0f, 0f, 4f), 60f, 0),
			("inside-forward", new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, 8f), 60f, 0),
			("inside-back-at-apex", new Vector3(0f, 0f, 6f), Vector3.zero, 60f, 0),
			("inside-sideways", new Vector3(0f, 0f, 4f), new Vector3(8f, 0f, 4f), 60f, 0),
			("grazing-surface", new Vector3(1.2f, 0f, -0.8f), new Vector3(5.2f, 0f, 6.1f), 60f, 0),
			("far", new Vector3(40f, 12f, 4f), new Vector3(0f, 0f, 4f), 20f, 0),
			("onto-floor", new Vector3(9f, 3f, -3f), new Vector3(0f, 1f, 4f), 60f, 1),
			("wide-150", new Vector3(12f, 0f, 4f), new Vector3(0f, 0f, 4f), 60f, 2),
			("narrow-8", new Vector3(10f, 0f, 4f), new Vector3(0f, 0f, 4f), 60f, 3)
		};

		private static RenderScene CreateTimingScene(float scale, LightBufferUpsample upsample, LightBufferLights lights,
			bool withWall = true)
		{
			var scene = new RenderScene(size: TimingSceneSize);
			scene.AddLight(new Vector3(-3f, 0f, 6f), Quaternion.LookRotation(Vector3.right));
			scene.AddLight(new Vector3(1f, -2.5f, 7f), Quaternion.LookRotation(new Vector3(0.2f, 1f, 0.1f)));
			if (withWall)
				scene.AddOccluder(new Vector3(-1.6f, 0f, 3f), new Vector3(3f, 8f, 0.2f));
			scene.UseLightBufferScale(scale);
			scene.UseLightBufferUpsample(upsample);
			scene.UseLightBufferLights(lights, 0.05f);
			return scene;
		}

		/// <summary>
		/// Renders twice with <paramref name="timing"/> (LargeOnly needs a culled frame first) and checks where the buffer
		/// was drawn.
		/// </summary>
		private static LightContribution RenderWithTiming(RenderScene scene, LightBufferTiming timing, bool expectBeforeOpaques)
		{
			// The pass is read after rendering: changing the renderer's settings makes URP rebuild the renderer, which
			// recreates the feature's pass.
			scene.UseLightBufferTiming(timing);
			scene.RenderLightContribution();
			var beforeOpaques = false;
			var picture = scene.RenderLightContribution(
				() => beforeOpaques = scene.Feature.SpotLightRenderPass.LastRecordedLightBufferBeforeOpaques);
			Assert.AreEqual(expectBeforeOpaques, beforeOpaques, $"{timing}: where the light buffer was drawn.");
			Assert.AreEqual(0, picture.BadPixelCount, "NaN, infinite or negative pixels.");
			return picture;
		}

		private static void AssertSamePicture(LightContribution reference, LightContribution test, string label)
		{
			double energy = 0, difference = 0;
			for (var y = 0; y < TimingSceneSize; y++)
			for (var x = 0; x < TimingSceneSize; x++)
			{
				var a = Luminance(reference.Pixel(x, y));
				energy += a;
				difference += math.abs(a - Luminance(test.Pixel(x, y)));
			}

			var meanError = (float)(difference / energy);
			TestContext.WriteLine($"{label}: mean absolute difference {meanError:P2} of the mean glow");
			Assert.Greater(energy, 1.0, "The test scene should glow.");
			Assert.Less(meanError, 0.01f, $"{label}: the picture differs from the reference.");
		}

		private static (Matrix4x4 placement, ClipBoxData box, ClipBoxRole role)[] ClipBoxSetup(string setup)
		{
			// The beam: apex (0, -2.5, 6), axis up, range 8; the camera at the origin looks along +z.
			static (Matrix4x4, ClipBoxData, ClipBoxRole) Box(Vector3 center, Vector3 size, ClipBoxRole role, float yaw = 0f)
				=> (Matrix4x4.TRS(center, Quaternion.Euler(0f, yaw, 10f), Vector3.one), new ClipBoxData { Size = size }, role);

			return setup switch
			{
				// A room ending at y ~ 0.5: no glow above its ceiling.
				"KeepLowerPart" => new[] { Box(new Vector3(0f, -1f, 6f), new Vector3(12f, 3f, 12f), ClipBoxRole.Keep) },
				// A slab cut out of the middle of the beam.
				"SubtractMiddle" => new[] { Box(new Vector3(0f, 1f, 6f), new Vector3(12f, 1.2f, 12f), ClipBoxRole.Subtract) },
				// Two keep boxes forming an L in the view plane.
				"LShapedRoom" => new[]
				{
					Box(new Vector3(1.5f, -1.5f, 6f), new Vector3(5f, 3f, 8f), ClipBoxRole.Keep),
					Box(new Vector3(-1.2f, 1f, 6f), new Vector3(2f, 8f, 8f), ClipBoxRole.Keep)
				},
				// A room with a pillar-like cut-out (rotated, so it splits view rays).
				"KeepMinusCutOut" => new[]
				{
					Box(new Vector3(0f, 0f, 6f), new Vector3(10f, 6f, 10f), ClipBoxRole.Keep),
					Box(new Vector3(0.3f, 0.5f, 6f), new Vector3(0.6f, 2f, 0.6f), ClipBoxRole.Subtract, yaw: 35f)
				},
				// The same, listed subtract first (the variant sorts the roles out).
				"CutOutThenKeep" => new[]
				{
					Box(new Vector3(0.3f, 0.5f, 6f), new Vector3(0.6f, 2f, 0.6f), ClipBoxRole.Subtract, yaw: 35f),
					Box(new Vector3(0f, 0f, 6f), new Vector3(10f, 6f, 10f), ClipBoxRole.Keep)
				},
				// A room with two pillars cut out (three boxes: the general variant).
				"RoomTwoPillars" => new[]
				{
					Box(new Vector3(0f, 0f, 6f), new Vector3(10f, 6f, 10f), ClipBoxRole.Keep),
					Box(new Vector3(-0.6f, 0.5f, 6f), new Vector3(0.5f, 2f, 0.5f), ClipBoxRole.Subtract, yaw: 35f),
					Box(new Vector3(0.7f, -0.3f, 5.5f), new Vector3(0.6f, 1.5f, 0.6f), ClipBoxRole.Subtract, yaw: -20f)
				},
				// Two subtract slabs across the beam: three pieces from two subtracts.
				"TwoCutOuts" => new[]
				{
					Box(new Vector3(0f, -0.5f, 6f), new Vector3(12f, 0.5f, 12f), ClipBoxRole.Subtract),
					Box(new Vector3(0f, 1.2f, 6f), new Vector3(12f, 0.6f, 12f), ClipBoxRole.Subtract)
				},
				// Two keep slabs with a gap between them along the view: two pieces from two keeps.
				"TwoRoomsApart" => new[]
				{
					Box(new Vector3(0f, 0f, 5.3f), new Vector3(12f, 12f, 0.8f), ClipBoxRole.Keep),
					Box(new Vector3(0f, 0f, 6.7f), new Vector3(12f, 12f, 0.8f), ClipBoxRole.Keep)
				},
				// A cut-out that no ray through the room reaches: the room alone.
				"CutOutOutsideRoom" => new[]
				{
					Box(new Vector3(0f, -1f, 6f), new Vector3(12f, 3f, 12f), ClipBoxRole.Keep),
					Box(new Vector3(0f, 4f, 6f), new Vector3(3f, 1f, 3f), ClipBoxRole.Subtract)
				},
				// Thin slabs across the view direction: rays through the beam cross three cuts (four pieces).
				"FourPiecesExact" => new[]
				{
					Box(new Vector3(0f, 0f, 6f), new Vector3(12f, 12f, 12f), ClipBoxRole.Keep),
					Box(new Vector3(0f, 0f, 5.2f), new Vector3(12f, 12f, 0.3f), ClipBoxRole.Subtract),
					Box(new Vector3(0f, 0f, 6f), new Vector3(12f, 12f, 0.3f), ClipBoxRole.Subtract),
					Box(new Vector3(0f, 0f, 6.8f), new Vector3(12f, 12f, 0.3f), ClipBoxRole.Subtract)
				},
				_ => throw new System.ArgumentException(setup)
			};
		}

		/// <summary>
		/// The light's region as the shader defines it: inside any keep box (or anywhere without keep boxes) and outside
		/// every subtract box, with the boxes packed exactly as the store packs them.
		/// </summary>
		private static bool InClipRegion((Matrix4x4 placement, ClipBoxData box, ClipBoxRole role)[] boxes, Vector3 point)
		{
			var hasKeep = false;
			var inKeep = false;

			foreach (var (placement, box, role) in boxes)
			{
				var inside = box.Pack(placement).Contains(point);
				if (role == ClipBoxRole.Keep)
				{
					hasKeep = true;
					inKeep |= inside;
				}
				else if (inside)
				{
					return false;
				}
			}

			return hasKeep == false || inKeep;
		}

		private static void RenderGallerySheet(string path, List<string> badViews)
		{
			const int tile = 192;
			const int columns = 4;
			var sheet = new ContactSheet(tile, columns, rows: (GalleryViews.Length + columns - 1) / columns);

			for (var v = 0; v < GalleryViews.Length; v++)
			{
				var (name, cameraPosition, target, fov, light) = GalleryViews[v];

				using var scene = new RenderScene(size: tile);
				scene.SetCamera(cameraPosition, target, fov);

				AddGalleryLight(scene, light);

				var glow = scene.RenderLightContribution();

				TestContext.WriteLine($"{v:00} {name}: max luminance {glow.MaxLuminance:F3}, bad pixels {glow.BadPixelCount}");
				if (glow.BadPixelCount > 0)
					badViews.Add(name);

				sheet.Place(v % columns, v / columns, glow.Tonemapped());
			}

			sheet.Save(path);
		}

		/// <param name="light">Light setup of a <see cref="GalleryViews"/> entry.</param>
		private static void AddGalleryLight(RenderScene scene, int light)
		{
			var data = RenderScene.DefaultTestLight();
			var forward = Quaternion.LookRotation(Vector3.forward);

			switch (light)
			{
				case 1: // pointing down onto a floor
					scene.AddLight(new Vector3(0f, 4f, 4f), Quaternion.LookRotation(Vector3.down), data);
					scene.AddOccluder(new Vector3(0f, -0.1f, 4f), new Vector3(30f, 0.2f, 30f));
					break;
				case 2: // wide
					data.InnerAngleDegrees = 120f;
					data.OuterAngleDegrees = 150f;
					scene.AddLight(Vector3.zero, forward, data);
					break;
				case 3: // narrow
					data.InnerAngleDegrees = 4f;
					data.OuterAngleDegrees = 8f;
					scene.AddLight(Vector3.zero, forward, data);
					break;
				default:
					scene.AddLight(Vector3.zero, forward, data);
					break;
			}
		}

		/// <summary>
		/// Glow <paramref name="test"/> adds where <paramref name="reference"/> has none (bleed), and its mean absolute
		/// difference from it, both as fractions of the reference's total glow.
		/// </summary>
		private static (float bleed, float meanError) CompareWithReference(LightContribution reference, LightContribution test, int size)
		{
			double total = 0, bleed = 0, difference = 0;

			for (var y = 0; y < size; y++)
			for (var x = 0; x < size; x++)
			{
				var expected = Luminance(reference.Pixel(x, y));
				var actual = Luminance(test.Pixel(x, y));

				total += expected;
				difference += math.abs(actual - expected);
				if (expected < 1e-4f)
					bleed += actual;
			}

			return ((float)(bleed / total), (float)(difference / total));
		}

		// ---------- LargeOnly light buffer ----------

		/// <summary>
		/// Near and wide: far above the default large-light screen size (0.15).
		/// </summary>
		private static void AddLargeTestLight(RenderScene scene)
			=> scene.AddLight(new Vector3(-3f, 0f, 6f), Quaternion.LookRotation(Vector3.right));

		/// <summary>
		/// Far and short: about 0.08 of the screen height, below the default large-light screen size. Brighter, so its
		/// share of the glow is large enough to check.
		/// </summary>
		private static void AddSmallTestLight(RenderScene scene)
		{
			var data = RenderScene.DefaultTestLight();
			data.Range = 2f;
			data.Intensity *= 20f;
			scene.AddLight(new Vector3(3f, 1f, 22f), Quaternion.LookRotation(new Vector3(-0.3f, 1f, 0.2f)), data);
		}

		private static void AssertMatches(LightContribution reference, LightContribution test, int size, float tolerance, string what)
		{
			var (_, meanError) = CompareWithReference(reference, test, size);
			TestContext.WriteLine($"{what}: mean absolute difference {meanError:P2} of the reference glow");
			Assert.AreEqual(0, test.BadPixelCount, $"{what}: NaN, infinite or negative pixels.");
			Assert.Less(meanError, tolerance, what);
		}

		// Renders one light and compares sampled pixels with the brute-force integral. Logs an "ACCURACY" line.
		/// <param name="orthographicSize">Above 0: an orthographic camera of that size (fieldOfView is ignored).</param>
		/// <param name="clipBoxes">Adds clip boxes to the scene and returns the light's references to them.</param>
		/// <param name="inRegion">The light's region for the brute-force integral (null: everywhere).</param>
		private static void CompareWithBruteForce(float lx, float ly, float lz, float dx, float dy, float dz, float fieldOfView,
			float tolerance, SpotLightProxyArrayDescriptor descriptor = null, System.Action<FulumaSpotLightRenderPass> afterLitRender = null,
			float orthographicSize = 0f, System.Func<RenderScene, ClipBoxReference[]> clipBoxes = null,
			System.Func<Vector3, bool> inRegion = null, int clipBoxPieceLimit = FulumaSpotLightRenderPass.MinClipBoxPieces,
			bool forceGeneralClipVariant = false, float innerAngle = 30f, float outerAngle = 60f, bool wideConePrecision = false,
			LightModel model = default, float featureIntensity = 1f)
		{
			const int size = 48;
			using var scene = new RenderScene(size: size);
			scene.UseWideConePrecision(wideConePrecision);
			scene.UseScattering(model.Phase, model.Anisotropy, model.Falloff, model.Glare);
			scene.Feature.LightIntensity = featureIntensity;

			if (orthographicSize > 0f)
				scene.SetOrthographicCamera(Vector3.zero, Vector3.forward, orthographicSize);
			else
				scene.SetCamera(Vector3.zero, Vector3.forward, fieldOfView);

			if (descriptor != null)
				scene.UseProxyDescriptor(descriptor);

			var lightPosition = new Vector3(lx, ly, lz);
			var lightDirection = new Vector3(dx, dy, dz).normalized;
			var data = RenderScene.DefaultTestLight();
			data.InnerAngleDegrees = innerAngle;
			data.OuterAngleDegrees = outerAngle;

			if (clipBoxes != null)
			{
				scene.UseClipBoxes(true, clipBoxPieceLimit);
				scene.AddLight(lightPosition, Quaternion.LookRotation(lightDirection), data, clipBoxes(scene));
			}
			else
			{
				scene.AddLight(lightPosition, Quaternion.LookRotation(lightDirection), data);
			}

			scene.Feature.ForceGeneralClipVariant = forceGeneralClipVariant;
			var glow = scene.RenderLightContribution(() =>
			{
				var pass = scene.Feature.SpotLightRenderPass;
				Assert.AreEqual(forceGeneralClipVariant, pass.ForceGeneralClipVariant, "The pass was rebuilt before drawing.");
				afterLitRender?.Invoke(pass);
			});
			var intensity = scene.Feature.LightIntensity;

			var compared = 0;
			var worst = 0f;
			var errorSum = 0f;
			for (var y = 2; y < size; y += 5)
			for (var x = 2; x < size; x += 5)
			{
				var ray = scene.PixelRay(x, y);
				var expected = BruteForceInverseSquareModel(ray, lightPosition, lightDirection, data, inRegion, model) * intensity;
				var rendered = glow.Pixel(x, y).g; // white light: every channel carries the same value

				// Too faint to compare relatively, but must stay faint (catches glow a clip box should have cut away).
				if (expected < 0.02f)
				{
					Assert.Less(rendered, 0.03f, $"Pixel ({x}, {y}): rendered {rendered:F4} where the brute force has {expected:F4}.");
					continue;
				}

				var error = math.abs(rendered / expected - 1f);
				if (error > worst)
					TestContext.WriteLine($"  new worst at ({x}, {y}): rendered {rendered:F5}, brute force {expected:F5}, ray {ray.direction}");
				worst = math.max(worst, error);
				errorSum += error;
				compared++;

				Assert.IsFalse(float.IsNaN(rendered) || float.IsInfinity(rendered), $"Pixel ({x}, {y}) is not finite.");
				Assert.Less(math.abs(rendered - expected), math.max(tolerance * expected, 0.0015f),
					$"Pixel ({x}, {y}): rendered {rendered:F4}, brute force {expected:F4}.");
			}

			TestContext.WriteLine($"Compared {compared} pixels, worst relative error {worst:P2}.");
			TestContext.WriteLine($"ACCURACY light ({lx}, {ly}, {lz}) fov {fieldOfView}: {compared} pixels, " +
			                      $"worst {worst:P1}, mean {(compared > 0 ? errorSum / compared : 0f):P1}");
			Assert.Greater(compared, 5, "Too few glowing pixels to compare.");
		}

		/// <summary>
		/// Numerical integral of the inverse-square model along a ray, as the shader defines it: window
		/// saturate(1 - d^2/r^2)^2 / (d^2 + e^2) with e = 2% of the range, normalized by range / (4 pi), times the angular
		/// falloff saturate((cos - cosOuter) * angleScale)^2, inside the range sphere. <paramref name="model"/> picks the
		/// variants: URP's window (1 - (d^2/r^2)^2)^2 without the range scale and 1 / (4 pi), a point lamp (e = 0), and
		/// Henyey-Greenstein's phase relative to isotropic scattering.
		/// </summary>
		/// <param name="inRegion">Only points it accepts count (a clipped light's region); null: all.</param>
		private static float BruteForceInverseSquareModel(Ray ray, Vector3 apex, Vector3 axis, SpotLightData data,
			System.Func<Vector3, bool> inRegion = null, LightModel model = default)
		{
			var packed = data.Pack();
			var range = packed.Range;
			var cosOuter = packed.CosOuter;
			var angleScale = (float)packed.AngleScale;
			var epsilon = model.Glare == LampGlare.None ? 0.02f * range : 0f;

			const int steps = 40000;
			const float maxDistance = 30f;
			var step = maxDistance / steps;
			var total = 0.0;

			for (var i = 0; i < steps; i++)
			{
				var t = (i + 0.5f) * step;
				var point = ray.origin + ray.direction * t;
				var toPoint = point - apex;
				var d2 = toPoint.sqrMagnitude;
				if (d2 > range * range || Vector3.Dot(toPoint, axis) <= 0f)
					continue;

				if (inRegion != null && inRegion(point) == false)
					continue;

				var distance = math.sqrt(d2);
				var cosAngle = Vector3.Dot(toPoint, axis) / distance;
				var falloff = math.saturate((cosAngle - cosOuter) * angleScale);
				if (falloff <= 0f)
					continue;

				var angular = falloff * falloff;
				var rangeFraction = d2 / (range * range);
				var window = model.Falloff == DistanceFalloff.Unity
					? math.saturate(1f - rangeFraction * rangeFraction)
					: math.saturate(1f - rangeFraction);

				// Light leaves the lamp toward the point and scatters back along the ray.
				var phase = model.PhaseRelativeToIsotropic(Vector3.Dot(toPoint, -ray.direction) / distance);
				total += (double)window * window / (d2 + epsilon * epsilon) * angular * phase * step;
			}

			return (float)(model.Falloff == DistanceFalloff.Unity ? total : total * range * 0.25 / math.PI);
		}

		/// <summary>
		/// The feature's light model settings for a brute-force comparison; default: today's model (isotropic,
		/// range-scaled, no glare).
		/// </summary>
		private readonly struct LightModel
		{
			public readonly ScatteringPhase Phase;
			public readonly float Anisotropy;
			public readonly DistanceFalloff Falloff;
			public readonly LampGlare Glare;

			public LightModel(ScatteringPhase phase, float anisotropy, DistanceFalloff falloff, LampGlare glare)
			{
				Phase = phase;
				Anisotropy = anisotropy;
				Falloff = falloff;
				Glare = glare;
			}

			/// <summary>
			/// 4 pi times the phase function at the scattering angle's cosine <paramref name="cosScatter"/>.
			/// </summary>
			public float PhaseRelativeToIsotropic(float cosScatter)
			{
				if (Phase != ScatteringPhase.HenyeyGreenstein)
					return 1f;

				var g = Anisotropy;
				return (1f - g * g) / math.pow(1f + g * g - 2f * g * cosScatter, 1.5f);
			}
		}

		/// <summary>
		/// Directory for saved pictures; ignores the test when <c>FULUMA_TEST_IMAGE_DIR</c> isn't set.
		/// </summary>
		private static string ImageDirectoryOrIgnore(string what)
		{
			var directory = System.Environment.GetEnvironmentVariable(ImageDirectoryVariable);
			if (string.IsNullOrEmpty(directory))
				Assert.Ignore($"Set {ImageDirectoryVariable} to render {what}.");

			return directory;
		}

		private static void IgnoreUnlessBenchmarking()
		{
			if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(BenchmarkVariable)))
				Assert.Ignore($"Set {BenchmarkVariable}=1 to run the benchmark.");
		}

		/// <summary>
		/// Milliseconds per frame the lights add (with minus without), once per run, sorted.
		/// </summary>
		private static List<double> MeasureLightCost(RenderScene scene, int frames = 40, int runs = 5)
		{
			var samples = new List<double>(runs);

			for (var run = 0; run < runs; run++)
			{
				var empty = scene.TimeFrames(frames, withLights: false);
				var lit = scene.TimeFrames(frames, withLights: true);
				samples.Add(lit - empty);
			}

			samples.Sort();
			return samples;
		}

		private static int CountProxyMeshes()
			=> Resources.FindObjectsOfTypeAll<Mesh>().Count(m => m.name == SpotLightProxyMeshBuilder.MeshName);

		private NativeLeakDetectionMode _previousLeakMode;
		private LightStore _store;
		private Shader _shader;

		[OneTimeSetUp]
		public void OneTimeSetUp()
		{
			_previousLeakMode = NativeLeakDetection.Mode;
			NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;
		}

		[OneTimeTearDown]
		public void OneTimeTearDown() => NativeLeakDetection.Mode = _previousLeakMode;

		[SetUp]
		public void SetUp()
		{
			GpuRequirement.IgnoreUnlessFakeLightsDraw();

			_store = ScriptableObject.CreateInstance<LightStore>();
			_shader = Shader.Find(ShaderName);
			Assert.IsNotNull(_shader, $"Shader '{ShaderName}' not found.");
		}

		[TearDown]
		public void TearDown()
		{
			if (_store != null)
				Object.DestroyImmediate(_store);
		}

		// ---------- Shader ----------

		[Test]
		public void Shader_AllVariantsCompile_WithoutErrors()
		{
			var shader = _shader;
			var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

			try
			{
				string[] depthSources = { null, "_DEPTH_AS_INPUT_ATTACHMENT", "_DEPTH_AS_INPUT_ATTACHMENT_MSAA" };

				ShaderUtil.ClearShaderMessages(shader);

				foreach (var depth in depthSources)
				foreach (var wideCones in new[] { false, true })
				{
					var keywords = new List<string>();
					if (depth != null)
						keywords.Add(depth);
					if (wideCones)
						keywords.Add("_FULUMA_WIDE_CONES");

					material.shaderKeywords = keywords.ToArray();

					for (var pass = 0; pass < material.passCount; pass++)
						ShaderUtil.CompilePass(material, pass, forceSync: true);
				}

				var errors = ShaderUtil.GetShaderMessages(shader)
					.Where(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
					.Select(m => $"{m.file}({m.line}): {m.message} {m.messageDetails}")
					.ToArray();

				Assert.IsEmpty(errors, string.Join("\n", errors));
				Assert.IsFalse(ShaderUtil.ShaderHasError(shader));
			}
			finally
			{
				Object.DestroyImmediate(material);
			}
		}

		// ---------- Pass lifetime ----------

		[Test]
		public void Feature_DrawsWithEveryLodOfItsDescriptor()
		{
			// Four LODs, one more than the built-in set, so the descriptor visibly wins over the fallback.
			var descriptor = ScriptableObject.CreateInstance<SpotLightProxyArrayDescriptor>();
			descriptor.SetLods(new[]
			{
				new SpotLightProxyDescriptor(0.3f, 16, 12, 9, 7, 4),
				new SpotLightProxyDescriptor(0.15f, 12, 9, 7, 4),
				new SpotLightProxyDescriptor(0.05f, 8, 4),
				new SpotLightProxyDescriptor(0f, 3)
			});

			try
			{
				using var scene = new RenderScene();
				scene.UseProxyDescriptor(descriptor);
				var pass = scene.Feature.SpotLightRenderPass;

				Assert.AreEqual(descriptor.LodCount, pass.LodCount);
				Assert.AreEqual(pass.LodCount, pass.ProxyMesh.subMeshCount);
			}
			finally
			{
				Object.DestroyImmediate(descriptor);
			}
		}

		[Test]
		public void Pass_WithoutShader_LogsAnError_AndDrawsNothing()
		{
			LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("need a spot light shader"));

			var pass = new FulumaSpotLightRenderPass(_store, null);
			Assert.IsFalse(pass.IsCreated);
			pass.Dispose();
		}

		[Test]
		public void Pass_WithInvalidDescriptor_LogsAnError_AndUsesTheBuiltInLods()
		{
			var descriptor = ScriptableObject.CreateInstance<SpotLightProxyArrayDescriptor>();
			descriptor.SetLods(new[] { new SpotLightProxyDescriptor(0f, 16, 2) }); // a band below 3 vertices
			LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("proxy descriptor is invalid"));

			var pass = new FulumaSpotLightRenderPass(_store, _shader, proxyDescriptor: descriptor);
			try
			{
				Assert.IsTrue(pass.IsCreated);
				Assert.AreEqual(3, pass.LodCount, "The built-in set has three LODs.");
			}
			finally
			{
				pass.Dispose();
				Object.DestroyImmediate(descriptor);
			}
		}

		[Test]
		public void Feature_Create_BringsInvalidSettingsBackIntoRange()
		{
			var feature = ScriptableObject.CreateInstance<FulumaRenderFeature>();
			try
			{
				var serialized = new SerializedObject(feature);
				serialized.FindProperty("lightIntensity").floatValue = float.NaN;
				serialized.FindProperty("nearPlaneFadeDistance").floatValue = -1f;
				serialized.FindProperty("dynamicBufferCount").intValue = 9;
				serialized.FindProperty("maxSpotLights").intValue = -5;
				serialized.FindProperty("clipBoxPieceLimit").intValue = 1;
				serialized.FindProperty("lightBufferScale").floatValue = float.PositiveInfinity;
				serialized.FindProperty("largeLightScreenSize").floatValue = 7f;
				serialized.FindProperty("lightBufferLights").intValue = 5;
				serialized.FindProperty("lightBufferTiming").intValue = -1;
				serialized.FindProperty("fidelityFeatures").intValue = 1 << 30 | (int)FidelityFeatures.ClipBoxes;
				serialized.ApplyModifiedPropertiesWithoutUndo();

				feature.Create();

				Assert.AreEqual(1f, feature.LightIntensity);
				Assert.AreEqual(0.001f, feature.NearPlaneFadeDistance);
				Assert.AreEqual(SpotLightGpuMirror.MaximumRingLength, feature.DynamicBufferCount);
				Assert.AreEqual(1, feature.MaxSpotLights);
				Assert.AreEqual(FulumaSpotLightRenderPass.MinClipBoxPieces, feature.ClipBoxPieceLimit);
				Assert.AreEqual(LightBufferScaling.Full, feature.LightBufferScale);
				Assert.AreEqual(1f, feature.LargeLightScreenSize);
				Assert.AreEqual(LightBufferLights.All, feature.LightBufferLights);
				Assert.AreEqual(LightBufferTiming.Auto, feature.LightBufferTiming);
				Assert.AreEqual(FidelityFeatures.ClipBoxes, feature.FidelityFeatures);
			}
			finally
			{
				feature.Dispose();
				Object.DestroyImmediate(feature);
			}
		}

		[Test]
		public void Pass_CreateAndDispose_LeavesNothingBehind()
		{
			var meshes = CountProxyMeshes();
			var materials = CountMaterials();
			var mirrors = SpotLightGpuMirror.MirrorCount;

			for (var i = 0; i < 20; i++)
			{
				var pass = new FulumaSpotLightRenderPass(_store, _shader);
				Assert.AreEqual(mirrors + 1, SpotLightGpuMirror.MirrorCount);
				Assert.IsNotNull(pass.ProxyMesh);
				pass.Dispose();
			}

			Assert.AreEqual(meshes, CountProxyMeshes());
			Assert.AreEqual(materials, CountMaterials());
			Assert.AreEqual(mirrors, SpotLightGpuMirror.MirrorCount);
		}

		[Test]
		public void Pass_WithoutLodsAndDownsampled_BuildsOneLod_AndLeavesNothingBehind()
		{
			var lightBufferShader = Shader.Find(DownsampledLightBuffer.ShaderName);
			Assert.IsNotNull(lightBufferShader, $"Shader '{DownsampledLightBuffer.ShaderName}' not found.");

			int CountLightBufferMaterials() => Resources.FindObjectsOfTypeAll<Material>().Count(m => m.shader == lightBufferShader);
			var meshes = CountProxyMeshes();
			var materials = CountMaterials() + CountLightBufferMaterials();

			var pass = new FulumaSpotLightRenderPass(_store, _shader, useLods: false, lightBufferScale: 0.125f,
				upsample: LightBufferUpsample.Bilinear);
			Assert.AreEqual(LightBufferUpsample.Bilinear, pass.Upsample);
			Assert.AreEqual(1, pass.LodCount);
			Assert.AreEqual(1, pass.ProxyMesh.subMeshCount);
			Assert.AreEqual(0.125f, pass.LightBufferScale);
			Assert.IsFalse(pass.UsesDepthInputAttachment, "The downsampled buffer reads its own low-resolution depth.");
			pass.Dispose();

			Assert.AreEqual(meshes, CountProxyMeshes());
			Assert.AreEqual(materials, CountMaterials() + CountLightBufferMaterials());
		}

		[Test]
		public void Pass_Downsampled_RequestsUrpDepthCopyOnlyWithMsaa()
		{
			// Single-sampled, the light buffer reads the camera's depth attachment itself: no _CameraDepthTexture copy
			// (on a Mali-G52 that copy was a whole-screen pass plus ~6 MB of extra reads per frame).
			var pass = new FulumaSpotLightRenderPass(_store, _shader, useLods: false, lightBufferScale: 0.25f,
				upsample: LightBufferUpsample.DepthAware);
			try
			{
				pass.ConfigureDepthSource(1);
				Assert.AreEqual(ScriptableRenderPassInput.None, pass.input & ScriptableRenderPassInput.Depth);
				Assert.IsTrue(pass.requiresIntermediateTexture);

				// MSAA: the buffer's passes read single samples, so they need URP's resolved copy.
				pass.ConfigureDepthSource(4);
				Assert.AreEqual(ScriptableRenderPassInput.Depth, pass.input & ScriptableRenderPassInput.Depth);
			}
			finally
			{
				pass.Dispose();
			}
		}

		[TestCase(0.25f, LightBufferUpsample.DepthAware, LightBufferLights.All)]
		[TestCase(0.25f, LightBufferUpsample.Bilinear, LightBufferLights.All)]
		[TestCase(0.5f, LightBufferUpsample.DepthAware, LightBufferLights.LargeOnly)]
		public void Render_LightBufferBeforeOpaques_MatchesTheBufferAfterOpaques(float scale, LightBufferUpsample upsample,
			LightBufferLights lights)
		{
			// The same buffer, made from the depth prepass instead of the opaques' depth: the same picture.
			using var scene = CreateTimingScene(scale, upsample, lights);
			var after = RenderWithTiming(scene, LightBufferTiming.AfterOpaques, false);
			var before = RenderWithTiming(scene, LightBufferTiming.BeforeOpaques, true);
			AssertSamePicture(after, before, $"BEFORE OPAQUES {scale} {upsample} {lights}");
		}

		[TestCase(true, CopyDepthMode.ForcePrepass, DepthPrimingMode.Disabled, TestName = "Render_LightBufferTimingAuto_UsesTheForcedPrepass")]
		[TestCase(false, CopyDepthMode.AfterTransparents, DepthPrimingMode.Forced, TestName = "Render_LightBufferTimingAuto_UsesThePrimedDepth")]
		[TestCase(true, CopyDepthMode.ForcePrepass, DepthPrimingMode.Forced, TestName = "Render_LightBufferTimingAuto_UsesThePrimedDepth_WithForcedPrepass")]
		public void Render_LightBufferTimingAuto_UsesAPrepassTheRendererMakesAnyway(bool depthTexture, CopyDepthMode copyDepthMode,
			DepthPrimingMode depthPrimingMode)
		{
			using var scene = CreateTimingScene(0.25f, LightBufferUpsample.DepthAware, LightBufferLights.All);
			var after = RenderWithTiming(scene, LightBufferTiming.AfterOpaques, false);

			scene.UseDepthPrepassSettings(depthTexture, copyDepthMode, depthPrimingMode);

			// Deferred renderers never get the buffer before the opaques: Auto draws after them there (by design).
			var auto = RenderWithTiming(scene, LightBufferTiming.Auto, expectBeforeOpaques: scene.RendersDeferred == false);
			AssertSamePicture(after, auto, $"AUTO {copyDepthMode} {depthPrimingMode}");
		}

		[TestCase(false, CopyDepthMode.AfterTransparents)]
		[TestCase(true, CopyDepthMode.AfterTransparents)]
		[TestCase(true, CopyDepthMode.AfterOpaques)]
		public void Render_LightBufferTimingAuto_DrawsAfterOpaques_WithoutAPrepass(bool depthTexture, CopyDepthMode copyDepthMode)
		{
			using var scene = CreateTimingScene(0.25f, LightBufferUpsample.DepthAware, LightBufferLights.All);
			scene.UseDepthPrepassSettings(depthTexture, copyDepthMode, DepthPrimingMode.Disabled);
			RenderWithTiming(scene, LightBufferTiming.Auto, false);
		}

		[Test]
		public void Render_RightAfterTheRendererIsRebuilt_StillDrawsTheLights()
		{
			// Changing renderer settings makes URP rebuild the renderer mid-frame, which recreates the feature's pass. The
			// new pass has to keep the store's GPU mirror (and this frame's upload): a fresh one drew nothing for a frame.
			using var scene = CreateTimingScene(1f, LightBufferUpsample.DepthAware, LightBufferLights.All);
			var reference = scene.RenderLightContribution();
			var passBefore = scene.Feature.SpotLightRenderPass;

			scene.UseDepthPrepassSettings(false, CopyDepthMode.AfterOpaques, DepthPrimingMode.Disabled);
			var firstFrame = scene.RenderLightContribution();

			Assert.AreNotSame(passBefore, scene.Feature.SpotLightRenderPass, "The renderer should have been rebuilt.");
			AssertSamePicture(reference, firstFrame, "REBUILD");
		}

		[TestCase(1f)]
		[TestCase(0.25f)]
		[TestCase(0.5f, LightBufferLights.LargeOnly)]
		public void Render_WithoutSceneDepth_IgnoresOccluders(float scale, LightBufferLights lights = LightBufferLights.All)
		{
			// The same lights with and without a wall in front of them: with scene depth the wall hides glow, without it the
			// picture is the same as without the wall.
			LightContribution Render(bool useSceneDepth, bool withWall)
			{
				using var scene = CreateTimingScene(scale, LightBufferUpsample.DepthAware, lights, withWall);
				scene.UseSceneDepth(useSceneDepth);
				scene.RenderLightContribution();
				var picture = scene.RenderLightContribution();
				Assert.AreEqual(0, picture.BadPixelCount, "NaN, infinite or negative pixels.");
				return picture;
			}

			var noWall = Render(false, false);
			AssertSamePicture(noWall, Render(false, true), $"NO SCENE DEPTH {scale} {lights}");

			var occluded = Render(true, true);
			double total = 0, hidden = 0;
			for (var y = 0; y < TimingSceneSize; y++)
			for (var x = 0; x < TimingSceneSize; x++)
			{
				var free = Luminance(noWall.Pixel(x, y));
				total += free;
				hidden += math.max(0f, free - Luminance(occluded.Pixel(x, y)));
			}

			Assert.Greater(hidden / total, 0.05, "With scene depth the wall should hide part of the glow.");
		}

		[Test]
		public void Pass_WithoutSceneDepth_AsksUrpForNoDepth_AndUpscalesBilinearly()
		{
			var pass = new FulumaSpotLightRenderPass(_store, _shader, useLods: false, lightBufferScale: 0.25f,
				upsample: LightBufferUpsample.DepthAware, useSceneDepth: false);
			try
			{
				Assert.IsFalse(pass.UsesSceneDepth);
				Assert.IsFalse(pass.UsesDepthInputAttachment);
				Assert.AreEqual(LightBufferUpsample.Bilinear, pass.Upsample, "Depth-aware upscaling needs scene depth.");

				pass.ConfigureDepthSource(4);
				Assert.AreEqual(ScriptableRenderPassInput.None, pass.input & ScriptableRenderPassInput.Depth);

				pass.LightBufferTiming = LightBufferTiming.BeforeOpaques;
				Assert.IsNull(pass.LightBufferBeforeOpaquesPass, "Without depth there is no prepass to draw from.");
			}
			finally
			{
				pass.Dispose();
			}
		}

		[Test]
		public void LightBufferTiming_NeedsABuffer_AndOnlyBeforeOpaquesRequestsThePrepass()
		{
			var full = new FulumaSpotLightRenderPass(_store, _shader, useLods: false);
			var buffered = new FulumaSpotLightRenderPass(_store, _shader, useLods: false, lightBufferScale: 0.25f);
			try
			{
				full.LightBufferTiming = LightBufferTiming.BeforeOpaques;
				Assert.IsNull(full.LightBufferBeforeOpaquesPass, "Without a buffer there is nothing to draw early.");

				buffered.LightBufferTiming = LightBufferTiming.AfterOpaques;
				Assert.IsNull(buffered.LightBufferBeforeOpaquesPass);

				// Auto only uses a prepass URP renders anyway: it asks for nothing.
				buffered.LightBufferTiming = LightBufferTiming.Auto;
				var early = buffered.LightBufferBeforeOpaquesPass;
				Assert.IsNotNull(early);
				Assert.Less((int)early.renderPassEvent, (int)RenderPassEvent.AfterRenderingOpaques);
				Assert.AreEqual(ScriptableRenderPassInput.None, early.input);

				// Asking for the depth texture before the opaques is what makes URP render a depth prepass.
				buffered.LightBufferTiming = LightBufferTiming.BeforeOpaques;
				Assert.AreSame(early, buffered.LightBufferBeforeOpaquesPass);
				Assert.AreEqual(ScriptableRenderPassInput.Depth, early.input & ScriptableRenderPassInput.Depth);
			}
			finally
			{
				full.Dispose();
				buffered.Dispose();
			}
		}

		// ---------- Clip boxes ----------

		/// <summary>
		/// A beam rising in front of the camera (seen from the side), limited by boxes; compared with the brute-force
		/// integral over the same region.
		/// </summary>
		[TestCase("KeepLowerPart")]
		[TestCase("SubtractMiddle")]
		[TestCase("LShapedRoom")]
		[TestCase("KeepMinusCutOut")]
		[TestCase("CutOutThenKeep")]
		[TestCase("CutOutOutsideRoom")]
		[TestCase("TwoRoomsApart")]
		[TestCase("TwoCutOuts")]
		[TestCase("RoomTwoPillars")]
		[TestCase("FourPiecesExact", 5)]
		public void Render_ClipBoxes_MatchBruteForceIntegral(string setup, int pieceLimit = FulumaSpotLightRenderPass.MinClipBoxPieces)
		{
			var boxes = ClipBoxSetup(setup);

			CompareWithBruteForce(0f, -2.5f, 6f, 0f, 1f, 0f, 60f, tolerance: 0.08f,
				clipBoxes: scene =>
				{
					var references = new ClipBoxReference[boxes.Length];
					for (var i = 0; i < boxes.Length; i++)
						references[i] = new ClipBoxReference(scene.AddClipBox(boxes[i].placement, boxes[i].box), boxes[i].role);
					return references;
				},
				inRegion: point => InClipRegion(boxes, point),
				clipBoxPieceLimit: pieceLimit);
		}

		/// <summary>
		/// Clipped lights seen by an orthographic camera: the one- and two-box variants take the box-space ray origin from
		/// the vertex stage's interpolated term there (the rays are parallel and start on the camera plane).
		/// </summary>
		[TestCase("KeepLowerPart")]
		[TestCase("SubtractMiddle")]
		[TestCase("KeepMinusCutOut")]
		[TestCase("LShapedRoom")]
		[TestCase("TwoRoomsApart")]
		[TestCase("TwoCutOuts")]
		[TestCase("RoomTwoPillars")]
		public void Render_ClipBoxesOrthographic_MatchBruteForceIntegral(string setup)
		{
			var boxes = ClipBoxSetup(setup);

			CompareWithBruteForce(0f, -2.5f, 6f, 0f, 1f, 0f, 60f, tolerance: 0.08f, orthographicSize: 4f,
				clipBoxes: scene =>
				{
					var references = new ClipBoxReference[boxes.Length];
					for (var i = 0; i < boxes.Length; i++)
						references[i] = new ClipBoxReference(scene.AddClipBox(boxes[i].placement, boxes[i].box), boxes[i].role);
					return references;
				},
				inRegion: point => InClipRegion(boxes, point));
		}

		/// <summary>
		/// The general clip box variant (the fallback when culling didn't sort lights by clip class) on lights with one box
		/// or none.
		/// </summary>
		[TestCase("KeepLowerPart")]
		[TestCase("SubtractMiddle")]
		[TestCase("KeepMinusCutOut")]
		[TestCase("TwoCutOuts")]
		[TestCase("None")]
		public void Render_GeneralClipVariant_MatchesBruteForceIntegral(string setup)
		{
			var boxes = setup == "None" ? System.Array.Empty<(Matrix4x4, ClipBoxData, ClipBoxRole)>() : ClipBoxSetup(setup);

			CompareWithBruteForce(0f, -2.5f, 6f, 0f, 1f, 0f, 60f, tolerance: 0.08f,
				clipBoxes: scene =>
				{
					// Without boxes the store needs one anyway, or the clip box variants aren't used at all.
					if (boxes.Length == 0)
						scene.AddClipBox(Matrix4x4.TRS(new Vector3(50f, 0f, 0f), Quaternion.identity, Vector3.one), ClipBoxData.Default);

					var references = new ClipBoxReference[boxes.Length];
					for (var i = 0; i < boxes.Length; i++)
						references[i] = new ClipBoxReference(scene.AddClipBox(boxes[i].Item1, boxes[i].Item2), boxes[i].Item3);
					return references;
				},
				inRegion: boxes.Length == 0 ? null : point => InClipRegion(boxes, point),
				afterLitRender: pass => Assert.IsTrue(pass.LastRecordedClipBoxes),
				forceGeneralClipVariant: true);
		}

		/// <summary>
		/// Lights of every clip class in one scene, in both pools: drawn per class (each with its own variant), they look
		/// the same as when all of them go through the general variant.
		/// </summary>
		[Test]
		public void Render_ClipClasses_DrawLikeTheGeneralVariant()
		{
			LightContribution Render(bool forceGeneral)
			{
				using var scene = new RenderScene(size: 48);
				scene.SetCamera(Vector3.zero, Vector3.forward, 70f);
				scene.UseClipBoxes(true, FulumaSpotLightRenderPass.MaxClipBoxPieces);

				var room = scene.AddClipBox(Matrix4x4.TRS(new Vector3(0f, -1f, 6f), Quaternion.Euler(0f, 20f, 10f), Vector3.one),
					new ClipBoxData { Size = new Vector3(6f, 3f, 6f) });
				var pillar = scene.AddClipBox(Matrix4x4.TRS(new Vector3(0.4f, 0f, 6f), Quaternion.Euler(0f, 35f, 0f), Vector3.one),
					new ClipBoxData { Size = new Vector3(0.6f, 6f, 0.6f) });
				var shelf = scene.AddClipBox(Matrix4x4.TRS(new Vector3(-0.5f, 0.8f, 6.5f), Quaternion.Euler(0f, 0f, 15f), Vector3.one),
					new ClipBoxData { Size = new Vector3(3f, 0.4f, 3f) });

				var data = RenderScene.DefaultTestLight();
				data.InnerAngleDegrees = 30f;
				data.OuterAngleDegrees = 60f;
				var up = Quaternion.LookRotation(Vector3.up);
				scene.AddLight(new Vector3(-2f, -2.5f, 7f), up, data);
				scene.AddLight(new Vector3(0f, -2.5f, 6f), up, data, ClipBoxReference.Keep(room));
				scene.AddLight(new Vector3(1.5f, -2.5f, 5f), up, data, ClipBoxReference.Subtract(pillar));
				scene.AddLight(new Vector3(-0.5f, -2.5f, 8f), up, data, ClipBoxReference.Keep(room), ClipBoxReference.Subtract(pillar));
				scene.AddLight(new Vector3(0.8f, -2.5f, 7f), up, data, ClipBoxReference.Subtract(pillar), ClipBoxReference.Keep(room));
				scene.AddLight(new Vector3(-1.2f, -2.5f, 5.5f), up, data, ClipBoxReference.Keep(room), ClipBoxReference.Keep(pillar));
				scene.AddLight(new Vector3(1.0f, -2.5f, 8.5f), up, data, ClipBoxReference.Subtract(pillar), ClipBoxReference.Subtract(shelf));
				scene.AddLight(new Vector3(-1.5f, -2.5f, 7.5f), up, data, ClipBoxReference.Keep(room), ClipBoxReference.Subtract(pillar),
					ClipBoxReference.Subtract(shelf));

				scene.Feature.ForceGeneralClipVariant = forceGeneral;
				var glow = scene.RenderLightContribution(() =>
				{
					var pass = scene.Feature.SpotLightRenderPass;
					Assert.AreEqual(forceGeneral, pass.ForceGeneralClipVariant, "The pass was rebuilt before drawing.");
					Assert.IsTrue(pass.LastRecordedClipBoxes);
					Assert.AreEqual(forceGeneral == false, pass.Culler.PartitionedByClipClass);

					// Per class: the eight lights split 1 none / 2 one box / 2 keep minus subtract / 1 two keeps / 1 two
					// subtracts / 1 general (three boxes).
					var classes = new int[SpotLightClipClassJob.ClassCount];
					var total = 0;
					var culler = pass.Culler;

					void Add(RangeInt range, SpotLightClipClassCounts counts)
					{
						total += range.length;
						for (var c = 0; c < SpotLightClipClassCounts.StoredCount; c++)
							classes[c] += counts[c];
						classes[SpotLightClipClassJob.General] += range.length - counts.Stored;
					}

					Add(culler.InsideDraw(LightTransformMode.Static, out var inside), inside);
					for (var lod = 0; lod < culler.LodCount; lod++)
						Add(culler.OutsideDraw(LightTransformMode.Static, lod, out var outside), outside);

					Assert.AreEqual(8, total);
					Assert.AreEqual(forceGeneral ? new[] { 0, 0, 0, 0, 0, 8 } : new[] { 1, 2, 2, 1, 1, 1 }, classes);
				});
				Assert.AreEqual(0, glow.BadPixelCount);
				return glow;
			}

			var perClass = Render(false);
			var general = Render(true);

			var worst = 0f;
			for (var y = 0; y < 48; y++)
			for (var x = 0; x < 48; x++)
			{
				var a = perClass.Pixel(x, y);
				var b = general.Pixel(x, y);
				worst = math.max(worst, math.max(math.abs(a.r - b.r), math.max(math.abs(a.g - b.g), math.abs(a.b - b.b))));
			}

			TestContext.WriteLine($"CLASSES max {general.MaxLuminance:F3}, worst difference {worst:F5}");
			Assert.Greater(general.MaxLuminance, 0.05f, "The lights should glow.");
			Assert.Less(worst, 0.002f * math.max(1f, general.MaxLuminance));
		}

		[Test]
		public void Render_PieceLimit_MergesTheExtraPieces()
		{
			// Four pieces along the rays through the beam: exact with a limit of 5, brighter with 3 (the last gaps glow).
			var boxes = ClipBoxSetup("FourPiecesExact");

			float Total(int limit)
			{
				using var scene = new RenderScene(size: 48);
				scene.SetCamera(Vector3.zero, Vector3.forward, 60f);
				scene.UseClipBoxes(true, limit);
				var references = boxes.Select(b => new ClipBoxReference(scene.AddClipBox(b.placement, b.box), b.role)).ToArray();
				var data = RenderScene.DefaultTestLight();
				data.InnerAngleDegrees = 30f;
				data.OuterAngleDegrees = 60f;
				scene.AddLight(new Vector3(0f, -2.5f, 6f), Quaternion.LookRotation(Vector3.up), data, references);
				var glow = scene.RenderLightContribution();
				Assert.AreEqual(0, glow.BadPixelCount);

				var total = 0f;
				for (var y = 0; y < 48; y++)
				for (var x = 0; x < 48; x++)
					total += glow.Pixel(x, y).g;
				return total;
			}

			var exact = Total(5);
			var merged = Total(3);
			TestContext.WriteLine($"PIECES total glow: limit 5 {exact:F2}, limit 3 {merged:F2}");
			Assert.Greater(merged, exact * 1.02f, "Merging pieces should fill gaps.");
		}

		/// <summary>
		/// Lights in the dynamic pool (their own materials, their pool's buffers bound per camera) draw exactly like the
		/// same lights in the static pool, with and without clip boxes, in both drawing paths.
		/// </summary>
		[TestCase(false, 1f)]
		[TestCase(true, 1f)]
		[TestCase(true, 0.5f)]
		public void Render_DynamicLights_MatchStaticOnes(bool clipBoxes, float lightBufferScale)
		{
			var boxes = ClipBoxSetup("RoomTwoPillars");

			LightContribution Render(bool dynamic)
			{
				using var scene = new RenderScene(size: 48);
				scene.SetCamera(Vector3.zero, Vector3.forward, 60f);
				scene.UseLightBufferScale(lightBufferScale);
				scene.UseDynamicLights(dynamic);

				var references = System.Array.Empty<ClipBoxReference>();
				if (clipBoxes)
				{
					scene.UseClipBoxes(true);
					references = boxes.Select(b => new ClipBoxReference(scene.AddClipBox(b.placement, b.box), b.role)).ToArray();
				}

				var data = RenderScene.DefaultTestLight();
				data.InnerAngleDegrees = 30f;
				data.OuterAngleDegrees = 60f;
				scene.AddLight(new Vector3(0f, -2.5f, 6f), Quaternion.LookRotation(Vector3.up), data, references);
				scene.AddLight(new Vector3(1.5f, -2f, 8f), Quaternion.LookRotation(new Vector3(-0.3f, 1f, 0.1f)), data, references);

				var glow = scene.RenderLightContribution(() =>
				{
					var mirror = scene.Feature.SpotLightRenderPass.Mirror;
					Assert.AreEqual(dynamic ? 2 : 0, mirror.DynamicCount);
					Assert.AreEqual(dynamic ? 0 : 2, mirror.StaticCount);
				});
				Assert.AreEqual(0, glow.BadPixelCount);
				return glow;
			}

			var placed = Render(false);
			var following = Render(true);

			var worst = 0f;
			for (var y = 0; y < 48; y++)
			for (var x = 0; x < 48; x++)
				worst = math.max(worst, math.abs(placed.Pixel(x, y).g - following.Pixel(x, y).g));

			TestContext.WriteLine($"DYNAMIC LIGHTS boxes {clipBoxes} scale {lightBufferScale}: max {placed.MaxLuminance:F3}, worst difference {worst:F5}");
			Assert.Greater(placed.MaxLuminance, 0.05f);
			Assert.Less(worst, 0.002f * math.max(1f, placed.MaxLuminance));
		}

		/// <summary>
		/// Boxes that follow transforms (uploaded through the per-frame ring) clip exactly like the same boxes placed
		/// statically.
		/// </summary>
		[TestCase("KeepMinusCutOut")]
		[TestCase("RoomTwoPillars")]
		public void Render_DynamicClipBoxes_MatchStaticOnes(string setup)
		{
			var boxes = ClipBoxSetup(setup);

			LightContribution Render(bool dynamic)
			{
				using var scene = new RenderScene(size: 48);
				scene.SetCamera(Vector3.zero, Vector3.forward, 60f);
				scene.UseClipBoxes(true);

				var references = new ClipBoxReference[boxes.Length];
				for (var i = 0; i < boxes.Length; i++)
				{
					var handle = dynamic
						? scene.AddDynamicClipBox(boxes[i].placement, boxes[i].box)
						: scene.AddClipBox(boxes[i].placement, boxes[i].box);
					references[i] = new ClipBoxReference(handle, boxes[i].role);
				}

				var data = RenderScene.DefaultTestLight();
				data.InnerAngleDegrees = 30f;
				data.OuterAngleDegrees = 60f;
				scene.AddLight(new Vector3(0f, -2.5f, 6f), Quaternion.LookRotation(Vector3.up), data, references);

				var glow = scene.RenderLightContribution(() =>
					Assert.AreEqual(dynamic, scene.Feature.SpotLightRenderPass.Mirror.ClipBoxesFromRing));
				Assert.AreEqual(0, glow.BadPixelCount);
				return glow;
			}

			var placed = Render(false);
			var following = Render(true);

			var worst = 0f;
			for (var y = 0; y < 48; y++)
			for (var x = 0; x < 48; x++)
				worst = math.max(worst, math.abs(placed.Pixel(x, y).g - following.Pixel(x, y).g));

			TestContext.WriteLine($"DYNAMIC BOXES max {placed.MaxLuminance:F3}, worst difference {worst:F5}");
			Assert.Greater(placed.MaxLuminance, 0.05f);
			Assert.Less(worst, 0.002f * math.max(1f, placed.MaxLuminance));
		}

		[Test]
		public void ClipBoxes_Off_OrUnused_UseThePlainVariant()
		{
			using var scene = new RenderScene(size: 32);
			scene.SetCamera(Vector3.zero, Vector3.forward, 60f);
			var data = RenderScene.DefaultTestLight();
			scene.AddLight(new Vector3(0f, -2.5f, 6f), Quaternion.LookRotation(Vector3.up), data);

			// Enabled, but the store has no boxes.
			scene.UseClipBoxes(true);
			var plain = scene.RenderLightContribution(() =>
			{
				var pass = scene.Feature.SpotLightRenderPass;
				Assert.IsFalse(pass.LastRecordedClipBoxes, "No boxes: the plain variant.");
				Assert.IsNotNull(pass.Mirror.ClipBoxes, "The box buffers exist with the mirror.");
			});

			// Boxes exist, but the feature doesn't use them.
			var box = scene.AddClipBox(Matrix4x4.TRS(new Vector3(0f, -2.5f, 6f), Quaternion.identity, Vector3.one),
				new ClipBoxData { Size = new Vector3(10f, 2f, 10f) });
			scene.UseClipBoxes(false);
			scene.AddLight(new Vector3(0f, -2.5f, 60f), Quaternion.LookRotation(Vector3.up), data, ClipBoxReference.Keep(box));
			var off = scene.RenderLightContribution(() =>
			{
				var pass = scene.Feature.SpotLightRenderPass;
				Assert.IsFalse(pass.LastRecordedClipBoxes, "Feature off: the plain variant.");
			});
			Assert.AreEqual(plain.MaxLuminance, off.MaxLuminance, 1e-5f, "Off: boxes change nothing.");

			// On: buffers and the clipping variant.
			scene.UseClipBoxes(true);
			scene.RenderLightContribution(() =>
			{
				var pass = scene.Feature.SpotLightRenderPass;
				Assert.IsTrue(pass.LastRecordedClipBoxes);
			});
		}

		[Test]
		public void LightBufferScale_ClampsAndRoundsSizesUp()
		{
			Assert.AreEqual(960, DownsampledLightBuffer.ScaledSize(1920, 0.5f));
			Assert.AreEqual(576, DownsampledLightBuffer.ScaledSize(1920, 0.3f), "1920 x 0.3f is 576.00002 in float.");
			Assert.AreEqual(20, DownsampledLightBuffer.ScaledSize(1920, 0.01f));
			Assert.AreEqual(1, DownsampledLightBuffer.ScaledSize(16, 0.01f));

			Assert.AreEqual(LightBufferScaling.Min, LightBufferScaling.Clamp(0f));
			Assert.AreEqual(LightBufferScaling.Full, LightBufferScaling.Clamp(2f));
			Assert.AreEqual(LightBufferScaling.Full, LightBufferScaling.Clamp(float.NaN));
			Assert.IsFalse(LightBufferScaling.IsDownsampled(1f));
			Assert.IsTrue(LightBufferScaling.IsDownsampled(0.999f));
		}

		[Test]
		public void Feature_ChangingScaleBetweenDownsampledValues_KeepsThePass()
		{
			using var scene = new RenderScene();
			scene.UseLightBufferScale(0.5f);
			var pass = scene.Feature.SpotLightRenderPass;
			Assert.AreEqual(0.5f, pass.LightBufferScale);

			scene.UseLightBufferScale(0.3f);
			Assert.AreSame(pass, scene.Feature.SpotLightRenderPass, "Moving between downsampled scales shouldn't recreate the pass.");
			Assert.AreEqual(0.3f, pass.LightBufferScale);

			scene.UseLightBufferScale(1f);
			Assert.AreNotSame(pass, scene.Feature.SpotLightRenderPass, "Going to full resolution changes the depth source.");
			Assert.AreEqual(1f, scene.Feature.SpotLightRenderPass.LightBufferScale);
			Assert.IsNull(pass.Material, "The old pass should be disposed.");
		}

		[Test]
		public void LodData_DisposeIsSafeTwiceAndOnDefault()
		{
			var data = SpotLightProxyArrayData.CreateDefault(Allocator.Persistent);
			Assert.IsTrue(data.IsCreated);

			data.Dispose();
			Assert.IsFalse(data.IsCreated);
			Assert.DoesNotThrow(() => data.Dispose());
			Assert.DoesNotThrow(() => default(SpotLightProxyArrayData).Dispose());

			var descriptor = ScriptableObject.CreateInstance<SpotLightProxyArrayDescriptor>();
			var fromDescriptor = descriptor.CreateData(Allocator.Persistent);
			Assert.AreEqual(descriptor.LodCount, fromDescriptor.Length);
			fromDescriptor.Dispose();
			Object.DestroyImmediate(descriptor);
		}

		// ---------- End to end: the project's URP renderer (with FulumaRenderFeature) renders into a texture ----------

		[Test]
		public void Render_LightAcrossView_AddsGlowOnlyInsideTheCone()
		{
			using var scene = new RenderScene();

			// Apex left of the view, pointing right: the view axis crosses the cone around z = 6.
			scene.AddLight(new Vector3(-3f, 0f, 6f), Quaternion.LookRotation(Vector3.right));

			var glow = scene.RenderLightContribution();

			Assert.Greater(Luminance(glow.Center), 1e-3f, "No glow where the view ray crosses the cone.");
			Assert.Less(Luminance(glow.LeftEdge), 1e-4f, "Glow on a ray that misses the cone.");
		}

		[Test]
		public void Render_OpaqueOccluder_BlocksTheGlowBehindIt()
		{
			using var scene = new RenderScene();

			scene.AddLight(new Vector3(-3f, 0f, 6f), Quaternion.LookRotation(Vector3.right));
			scene.AddOccluder(new Vector3(0f, 0f, 3f), new Vector3(4f, 4f, 0.2f));

			var glow = scene.RenderLightContribution();

			Assert.Less(Luminance(glow.Center), 1e-4f, "Glow leaked through opaque geometry in front of the cone.");
		}

		[Test]
		public void Render_CameraOutsideTheFeatureLayers_DrawsNothing_AndOffscreenLightsAreCulled()
		{
			using var scene = new RenderScene();

			scene.AddLight(new Vector3(-3f, 0f, 6f), Quaternion.LookRotation(Vector3.right));
			scene.AddLight(new Vector3(0f, 0f, -20f), Quaternion.LookRotation(Vector3.back)); // behind the camera

			var glow = scene.RenderLightContribution();
			Assert.Greater(glow.MaxLuminance, 1e-3f, "The light in view should glow.");
			Assert.AreEqual(1, scene.Feature.SpotLightRenderPass.Culler.Visible, "Only the light in view should survive culling.");
			Assert.IsTrue(scene.Feature.SpotLightRenderPass.LastCullingStartedEarly,
				"Culling should start in OnCameraPreCull, before URP's culling and the graph recording.");

			var layers = scene.Feature.CameraLayers;
			try
			{
				scene.Feature.CameraLayers = 0;
				glow = scene.RenderLightContribution();
				Assert.Less(glow.MaxLuminance, 1e-4f, "A camera whose culling mask misses the feature's layers drew lights.");
			}
			finally
			{
				scene.Feature.CameraLayers = layers;
			}
		}

		// Camera far outside the light, so the depth-tested front-face pass draws it: the wall must hide it completely.
		[Test]
		public void Render_DistantLightBehindWall_IsHidden()
		{
			using var scene = new RenderScene();

			scene.AddLight(new Vector3(-3f, 0f, 20f), Quaternion.LookRotation(Vector3.right));
			var visible = scene.RenderLightContribution();
			Assert.Greater(Luminance(visible.Center), 1e-3f, "The distant light should glow without the wall.");

			scene.AddOccluder(new Vector3(0f, 0f, 10f), new Vector3(30f, 30f, 0.2f));
			var hidden = scene.RenderLightContribution();
			Assert.Less(hidden.MaxLuminance, 1e-4f, "Glow leaked through the wall.");
		}

		[Test]
		public void Render_CameraInsideTheCone_StillGlows()
		{
			using var scene = new RenderScene();

			// Camera at the origin sits inside: 2 units along the axis of a cone that starts behind it.
			scene.AddLight(new Vector3(0f, 0f, -2f), Quaternion.LookRotation(Vector3.forward));

			var glow = scene.RenderLightContribution();

			Assert.Greater(Luminance(glow.Center), 1e-3f, "No glow with the camera inside the volume.");
		}

		// Regression: the first shader version took the angular falloff at the point closest to the axis, which for a
		// camera on the axis is the camera itself, clamped onto the outer surface: angular 0, a black view.
		[Test]
		public void Render_CameraBehindApex_LookingAlongAxis_Glows()
		{
			using var scene = new RenderScene();

			scene.SetCamera(new Vector3(0f, 0f, -6f), new Vector3(0f, 0f, 4f));
			scene.AddLight(Vector3.zero, Quaternion.LookRotation(Vector3.forward));

			var glow = scene.RenderLightContribution();

			Assert.Greater(Luminance(glow.Center), 1e-3f, "No glow looking down the axis from behind the apex.");
		}

		[Test]
		public void Render_ProducesFiniteGlow()
		{
			using var scene = new RenderScene(size: 256);

			scene.AddLight(new Vector3(-3f, 0f, 6f), Quaternion.LookRotation(Vector3.right));
			scene.AddOccluder(new Vector3(1.5f, -1.5f, 5f), new Vector3(1f, 1f, 1f));

			var glow = scene.RenderLightContribution();
			var center = Luminance(glow.Center);

			TestContext.WriteLine($"Center luminance {center:F4}, max {glow.MaxLuminance:F4}, " +
			                      $"depth input attachment: {scene.Feature.SpotLightRenderPass.UsesDepthInputAttachment}, " +
			                      $"device: {SystemInfo.graphicsDeviceType}");

			// Set FULUMA_TEST_IMAGE_DIR to keep a picture (Reinhard-tonemapped glow only).
			var imageDirectory = System.Environment.GetEnvironmentVariable(ImageDirectoryVariable);
			if (string.IsNullOrEmpty(imageDirectory) == false)
				glow.SavePng(System.IO.Path.Combine(imageDirectory, "SpotLight.png"));

			Assert.Greater(center, 1e-3f);
			Assert.IsFalse(float.IsNaN(glow.MaxLuminance) || float.IsInfinity(glow.MaxLuminance), $"Non-finite glow: {glow.MaxLuminance}");
		}

		[Test]
		public void Render_WithMsaa_StillGlows_AndRespectsOccluders()
		{
			using var scene = new RenderScene(msaaSamples: 4);

			scene.AddLight(new Vector3(-3f, 0f, 6f), Quaternion.LookRotation(Vector3.right));
			var open = scene.RenderLightContribution();
			Assert.Greater(Luminance(open.Center), 1e-3f, "No glow with MSAA on.");

			scene.AddOccluder(new Vector3(0f, 0f, 3f), new Vector3(4f, 4f, 0.2f));
			var blocked = scene.RenderLightContribution();
			Assert.Less(Luminance(blocked.Center), 1e-4f, "Glow leaked through an occluder with MSAA on.");
		}

		[Test]
		public void Render_Gallery_SavesViewsFromManyAngles()
		{
			var directory = ImageDirectoryOrIgnore("the gallery");
			var badViews = new List<string>();

			RenderGallerySheet(System.IO.Path.Combine(directory, "gallery.png"), badViews);

			Assert.IsEmpty(badViews, "Views with NaN, infinite or negative pixels: " + string.Join(", ", badViews));
		}

		// The light model's variants side by side: one row per model, one column per view (side, facing the lamp from inside
		// the beam, facing it straight on from outside the range, a lamp shining down onto a floor). Same intensity everywhere,
		// so the rows also compare brightness.
		[Test]
		public void Render_LightModelSheet_SavesEveryModel()
		{
			var directory = ImageDirectoryOrIgnore("the light model sheet");
			const int tile = 192;

			var models = new (string name, LightModel model)[]
			{
				("isotropic", new LightModel(ScatteringPhase.Isotropic, 0.7f, DistanceFalloff.RangeScaled, LampGlare.None)),
				("Henyey-Greenstein 0.7", new LightModel(ScatteringPhase.HenyeyGreenstein, 0.7f, DistanceFalloff.RangeScaled, LampGlare.None)),
				("isotropic, point lamp", new LightModel(ScatteringPhase.Isotropic, 0.7f, DistanceFalloff.RangeScaled, LampGlare.Point)),
				("Henyey-Greenstein 0.7, point lamp", new LightModel(ScatteringPhase.HenyeyGreenstein, 0.7f, DistanceFalloff.RangeScaled, LampGlare.Point)),
				("isotropic, Unity falloff", new LightModel(ScatteringPhase.Isotropic, 0.7f, DistanceFalloff.Unity, LampGlare.None)),
				("Henyey-Greenstein 0.7, Unity falloff, point lamp", new LightModel(ScatteringPhase.HenyeyGreenstein, 0.7f, DistanceFalloff.Unity, LampGlare.Point))
			};

			var facingOffAxis = Quaternion.LookRotation(new Vector3(math.sin(math.radians(12f)), 0f, -math.cos(math.radians(12f))));
			var views = new (Vector3 camera, Vector3 target, Vector3 light, Quaternion rotation, bool floor)[]
			{
				(Vector3.zero, Vector3.forward, new Vector3(-3f, 1f, 6f), Quaternion.LookRotation(new Vector3(1f, -0.3f, 0f)), false),
				(Vector3.zero, Vector3.forward, new Vector3(0f, 0f, 6f), facingOffAxis, false),
				(Vector3.zero, Vector3.forward, new Vector3(0f, 0f, 11f), Quaternion.LookRotation(Vector3.back), false),
				(new Vector3(0f, 2f, 0f), new Vector3(0f, 0f, 6f), new Vector3(0f, 4f, 6f), Quaternion.LookRotation(Vector3.down), true)
			};

			var sheet = new ContactSheet(tile, views.Length, models.Length);
			var badViews = new List<string>();

			for (var row = 0; row < models.Length; row++)
			for (var column = 0; column < views.Length; column++)
			{
				var (name, model) = models[row];
				var view = views[column];

				using var scene = new RenderScene(size: tile);
				scene.UseScattering(model.Phase, model.Anisotropy, model.Falloff, model.Glare);
				scene.SetCamera(view.camera, view.target);
				scene.AddLight(view.light, view.rotation, RenderScene.DefaultTestLight());
				if (view.floor)
					scene.AddOccluder(new Vector3(0f, -0.1f, 6f), new Vector3(30f, 0.2f, 30f));

				var glow = scene.RenderLightContribution();
				TestContext.WriteLine($"{name}, view {column}: max luminance {glow.MaxLuminance:F3}, bad pixels {glow.BadPixelCount}");
				if (glow.BadPixelCount > 0)
					badViews.Add($"{name}, view {column}");

				sheet.Place(column, row, glow.Tonemapped());
			}

			sheet.Save(System.IO.Path.Combine(directory, "light-models.png"));
			Assert.IsEmpty(badViews, "Views with NaN, infinite or negative pixels: " + string.Join(", ", badViews));
		}

		// The quadrature must match a brute-force numerical integral of the same model, pixel by pixel. This checks the HLSL
		// end to end (segments, depth clipping, normalization), not just that it renders.
		[TestCase(-3f, 0f, 6f, 1f, 0f, 0f)]          // side view across the beam
		[TestCase(0f, 0.8f, 2.9f, 0f, -0.42f, 0.9f)] // along the beam, 25 degrees off axis
		[TestCase(0f, 0f, -2f, 0f, 0f, 1f)]          // camera inside the cone
		[TestCase(-3f, 0f, 20f, 1f, 0f, 0f, 25f)]    // camera far outside, zoomed in: drawn by the depth-tested front-face pass
		public void Render_MatchesBruteForceIntegral(float lx, float ly, float lz, float dx, float dy, float dz, float fieldOfView = 60f)
		{
			// 3-point Gauss per segment: measured worst 2.5-7% per ray in Python, so allow 8% (or 0.0015 absolute).
			CompareWithBruteForce(lx, ly, lz, dx, dy, dz, fieldOfView, tolerance: 0.08f);
		}

		// Wide lights with the cancellation-free cone intersection (feature "Wide Cone Precision"): exact near the axis
		// too, up to the angle clamp. Views down the axis are where the default intersection loses its hits in float32.
		[TestCase(0f, 0f, 11f, 0f, 0f, -1f, 170f)]          // facing the light from outside its range, down the axis
		[TestCase(0f, 0f, 11f, 0f, 0f, -1f, 179.8f)]
		[TestCase(0f, 0f, 4f, 0f, 0f, 1f, 170f)]            // behind the apex, looking along the axis
		[TestCase(0f, 0f, 4f, 0f, 0f, 1f, 179.8f)]
		[TestCase(0.5f, -2f, 5f, 0.2f, 1f, -0.3f, 179.8f)]  // oblique
		[TestCase(0f, -3f, 6f, 0f, 1f, 0f, 179.8f)]         // side view across the flattened beam
		[TestCase(0f, 0f, -1f, 0f, 0f, 1f, 179.8f)]         // camera inside the volume, near the apex
		public void Render_WideConePrecision_MatchesBruteForceIntegral(float lx, float ly, float lz, float dx, float dy,
			float dz, float outerAngle)
		{
			CompareWithBruteForce(lx, ly, lz, dx, dy, dz, 60f, tolerance: 0.08f, innerAngle: outerAngle - 20f,
				outerAngle: outerAngle, wideConePrecision: true);
		}

		// The proxy only decides which pixels run the shader, so drawing through the coarsest LOD (a 3-sided pyramid with a
		// 3-face cap, inflated to enclose the volume) must give the same image. Lights the camera is outside of use it.
		[TestCase(-3f, 0f, 6f, 1f, 0f, 0f)]
		[TestCase(-3f, 0f, 20f, 1f, 0f, 0f, 25f)]
		[TestCase(2f, -3f, 12f, -0.3f, 1f, 0.2f, 40f)]
		public void Render_CoarsestLod_MatchesBruteForceIntegral(float lx, float ly, float lz, float dx, float dy, float dz,
			float fieldOfView = 60f)
		{
			var descriptor = ScriptableObject.CreateInstance<SpotLightProxyArrayDescriptor>();
			descriptor.SetLods(new[]
			{
				new SpotLightProxyDescriptor(float.MaxValue, 16, 12, 9, 7, 4), // never reached: only for the inside pass
				new SpotLightProxyDescriptor(0f, 3)
			});

			try
			{
				var drawnWithLod1 = 0;
				CompareWithBruteForce(lx, ly, lz, dx, dy, dz, fieldOfView, tolerance: 0.08f, descriptor,
					pass =>
					{
						Assert.AreEqual(2, pass.LodCount, "The test descriptor wasn't applied.");
						drawnWithLod1 = pass.Culler.OutsideDraw(LightTransformMode.Static, 1).length;
					});

				Assert.AreEqual(1, drawnWithLod1, "The light should have been drawn with the coarse LOD.");
			}
			finally
			{
				Object.DestroyImmediate(descriptor);
			}
		}

		// The orthographic path takes the scene distance straight from the (linear) depth, without a screen position.
		[TestCase(-3f, 0f, 6f, 1f, 0f, 0f, 4f)]
		[TestCase(0f, -2f, 3f, 0.2f, 1f, 0.6f, 6f)]
		public void Render_Orthographic_MatchesBruteForceIntegral(float lx, float ly, float lz, float dx, float dy, float dz,
			float orthographicSize)
		{
			CompareWithBruteForce(lx, ly, lz, dx, dy, dz, 60f, tolerance: 0.08f, orthographicSize: orthographicSize);
		}

		// The light model's variants (feature "Scattering"): phase functions, URP's falloff and the point lamp, each against
		// the brute-force integral of the same model. Views from the side, from behind the lamp along the beam, and facing the
		// lamp (where forward scattering and the glare peak). Dim views get a brighter feature, so enough pixels compare.
		[TestCase(ScatteringPhase.HenyeyGreenstein, 0.7f, DistanceFalloff.RangeScaled, LampGlare.None, -3f, 0f, 6f, 1f, 0f, 0f, 4f)]
		[TestCase(ScatteringPhase.HenyeyGreenstein, 0.7f, DistanceFalloff.RangeScaled, LampGlare.None, 0f, 0.8f, 2.9f, 0f, -0.42f, 0.9f, 4f)]
		[TestCase(ScatteringPhase.HenyeyGreenstein, 0.7f, DistanceFalloff.RangeScaled, LampGlare.None, 0.6f, 0.3f, 7f, -0.15f, -0.05f, -1f)]
		[TestCase(ScatteringPhase.Isotropic, 0.7f, DistanceFalloff.Unity, LampGlare.None, -3f, 0f, 6f, 1f, 0f, 0f)]
		[TestCase(ScatteringPhase.Isotropic, 0.7f, DistanceFalloff.Unity, LampGlare.None, 0f, 0.8f, 2.9f, 0f, -0.42f, 0.9f)]
		[TestCase(ScatteringPhase.Isotropic, 0.7f, DistanceFalloff.RangeScaled, LampGlare.Point, -3f, 0f, 6f, 1f, 0f, 0f)]
		[TestCase(ScatteringPhase.HenyeyGreenstein, 0.7f, DistanceFalloff.Unity, LampGlare.Point, 0.6f, 0.3f, 7f, -0.15f, -0.05f, -1f)]
		public void Render_LightModelVariant_MatchesBruteForceIntegral(ScatteringPhase phase, float anisotropy,
			DistanceFalloff falloff, LampGlare glare, float lx, float ly, float lz, float dx, float dy, float dz,
			float featureIntensity = 1f)
		{
			CompareWithBruteForce(lx, ly, lz, dx, dy, dz, 60f, tolerance: 0.08f,
				model: new LightModel(phase, anisotropy, falloff, glare), featureIntensity: featureIntensity);
		}

		// ---------- Downsampled light buffer ----------

		/// <summary>
		/// The glow is smooth, so shading it at a fraction of the resolution and upscaling it should keep its overall
		/// brightness, and differ per pixel only by the upscale's blur. Open scene (no occluders), compared with a
		/// full-resolution render at 128x128. Measured (phase 1, bilinear): energy 98.3 / 97.7 / 95.3 / 89.8 %, mean
		/// absolute difference 5.4 / 11.2 / 23.5 / 49.2 % of the mean glow for 2x / 4x / 8x / 16x (scales 0.5 / 0.25 /
		/// 0.125 / 0.0625). Fractional ratios (0.75, 0.3) check that any scale works.
		/// </summary>
		[TestCase(0.75f, LightBufferUpsample.Bilinear, 0.03f, 0.08f)]
		[TestCase(0.5f, LightBufferUpsample.Bilinear, 0.03f, 0.08f)]
		[TestCase(0.3f, LightBufferUpsample.Bilinear, 0.05f, 0.15f)]
		[TestCase(0.25f, LightBufferUpsample.Bilinear, 0.05f, 0.15f)]
		[TestCase(0.125f, LightBufferUpsample.Bilinear, 0.08f, 0.30f)]
		[TestCase(0.0625f, LightBufferUpsample.Bilinear, 0.15f, 0.60f)]
		[TestCase(0.75f, LightBufferUpsample.DepthAware, 0.03f, 0.08f)]
		[TestCase(0.5f, LightBufferUpsample.DepthAware, 0.03f, 0.08f)]
		[TestCase(0.3f, LightBufferUpsample.DepthAware, 0.05f, 0.15f)]
		[TestCase(0.25f, LightBufferUpsample.DepthAware, 0.05f, 0.15f)]
		[TestCase(0.125f, LightBufferUpsample.DepthAware, 0.08f, 0.30f)]
		[TestCase(0.0625f, LightBufferUpsample.DepthAware, 0.15f, 0.60f)]
		public void Render_Downsampled_MatchesFullResolution(float scale, LightBufferUpsample upsample,
			float energyTolerance, float meanErrorTolerance)
		{
			const int size = 128;
			using var scene = new RenderScene(size: size);
			scene.AddLight(new Vector3(-3f, 0f, 6f), Quaternion.LookRotation(Vector3.right));
			scene.AddLight(new Vector3(2f, 1.5f, 9f), Quaternion.LookRotation(new Vector3(-0.3f, -1f, 0.4f)));

			var full = scene.RenderLightContribution();

			scene.UseLightBufferScale(scale);
			scene.UseLightBufferUpsample(upsample);
			var drawnWith = 0f;
			var low = scene.RenderLightContribution(() => drawnWith = scene.Feature.SpotLightRenderPass.LightBufferScale);

			var imageDirectory = System.Environment.GetEnvironmentVariable(ImageDirectoryVariable);
			if (string.IsNullOrEmpty(imageDirectory) == false)
			{
				full.SavePng(System.IO.Path.Combine(imageDirectory, "downsample_full.png"));
				low.SavePng(System.IO.Path.Combine(imageDirectory, $"downsample_{scale:0.####}_{upsample}.png"));
			}

			Assert.AreEqual(scale, drawnWith, "The pass should draw into the downsampled buffer.");
			Assert.AreEqual(0, low.BadPixelCount, "NaN, infinite or negative pixels.");

			double fullEnergy = 0, lowEnergy = 0, difference = 0;
			for (var y = 0; y < size; y++)
			for (var x = 0; x < size; x++)
			{
				var a = Luminance(full.Pixel(x, y));
				var b = Luminance(low.Pixel(x, y));
				fullEnergy += a;
				lowEnergy += b;
				difference += math.abs(a - b);
			}

			var energyError = math.abs((float)(lowEnergy / fullEnergy) - 1f);
			var meanError = (float)(difference / fullEnergy);
			TestContext.WriteLine($"DOWNSAMPLE {scale} {upsample}: energy {lowEnergy / fullEnergy:P1} of full resolution, " +
			                      $"mean absolute difference {meanError:P1} of the mean glow");

			Assert.Greater(fullEnergy, 1.0, "The test scene should glow.");
			Assert.Less(energyError, energyTolerance, "Total glow differs from full resolution.");
			Assert.Less(meanError, meanErrorTolerance, "Glow shape differs from full resolution.");
		}

		/// <summary>
		/// A wall in front of part of the beam: where the full-resolution image shows the wall (no glow), a downsampled
		/// buffer bleeds glow over it near the edge. The depth-aware upsample should cut that bleed well below bilinear's.
		/// </summary>
		[Test]
		public void Render_Downsampled_DepthAwareUpsample_BleedsLessOverEdges([Values(0.5f, 0.3f, 0.25f, 0.125f)] float scale)
		{
			const int size = 128;
			using var scene = new RenderScene(size: size);
			scene.AddLight(new Vector3(-3f, 0f, 6f), Quaternion.LookRotation(Vector3.right));
			scene.AddLight(new Vector3(1f, -2.5f, 7f), Quaternion.LookRotation(new Vector3(0.2f, 1f, 0.1f)));

			// Covers the left half of the view in front of both beams.
			scene.AddOccluder(new Vector3(-1.6f, 0f, 3f), new Vector3(3f, 8f, 0.2f));

			var full = scene.RenderLightContribution();

			scene.UseLightBufferScale(scale);
			scene.UseLightBufferUpsample(LightBufferUpsample.Bilinear);
			var bilinear = scene.RenderLightContribution();

			scene.UseLightBufferUpsample(LightBufferUpsample.DepthAware);
			var depthAware = scene.RenderLightContribution();

			var (bilinearBleed, bilinearError) = CompareWithReference(full, bilinear, size);
			var (depthAwareBleed, depthAwareError) = CompareWithReference(full, depthAware, size);

			TestContext.WriteLine($"EDGES {scale}: bleed over the wall (fraction of the total glow) bilinear {bilinearBleed:P2}, " +
			                      $"depth-aware {depthAwareBleed:P2}; mean absolute difference bilinear {bilinearError:P1}, depth-aware {depthAwareError:P1}");

			var imageDirectory = System.Environment.GetEnvironmentVariable(ImageDirectoryVariable);
			if (string.IsNullOrEmpty(imageDirectory) == false)
			{
				full.SavePng(System.IO.Path.Combine(imageDirectory, "edges_full.png"));
				bilinear.SavePng(System.IO.Path.Combine(imageDirectory, $"edges_{scale:0.####}_bilinear.png"));
				depthAware.SavePng(System.IO.Path.Combine(imageDirectory, $"edges_{scale:0.####}_depthaware.png"));
			}

			Assert.Greater(bilinearBleed, 0.001f, "The scene should make a bilinear upscale bleed over the wall.");
			Assert.Less(depthAwareBleed, bilinearBleed * 0.5f, "The depth-aware upsample should bleed far less over the wall.");
			Assert.LessOrEqual(depthAwareError, bilinearError, "The depth-aware upsample should be at least as close to full resolution.");
		}

		/// <summary>
		/// The downsampled depth keeps each block's farthest surface (or nearest, on half the texels with the depth-aware
		/// upsample), so a wall covering whole blocks still hides the light (only blocks straddling its edges can leak).
		/// </summary>
		[Test]
		public void Render_Downsampled_OccluderStillHidesTheLight([Values(0.5f, 0.3f, 0.25f, 0.125f, 0.0625f)] float scale)
		{
			using var scene = new RenderScene(size: 128);
			scene.UseLightBufferScale(scale);

			scene.AddLight(new Vector3(-3f, 0f, 6f), Quaternion.LookRotation(Vector3.right));
			scene.AddOccluder(new Vector3(0f, 0f, 3f), new Vector3(4f, 4f, 0.2f));

			var glow = scene.RenderLightContribution();

			Assert.Less(Luminance(glow.Center), 1e-4f, "Glow leaked through the wall in the middle of it.");
		}

		[Test]
		public void Render_Downsampled_WithMsaa_StillGlows()
		{
			using var scene = new RenderScene(msaaSamples: 4, size: 128);
			scene.UseLightBufferScale(0.25f);
			scene.AddLight(new Vector3(-3f, 0f, 6f), Quaternion.LookRotation(Vector3.right));

			var glow = scene.RenderLightContribution();

			Assert.Greater(Luminance(glow.Center), 1e-3f);
			Assert.AreEqual(0, glow.BadPixelCount);
		}

		// ---------- Near-plane fade ----------

		[Test]
		public void NearPlaneFade_TogglesTheKeywordOnBothMaterials()
		{
			using var scene = new RenderScene();
			scene.UseLightBufferScale(0.5f);
			var pass = scene.Feature.SpotLightRenderPass;
			var keyword = new UnityEngine.Rendering.LocalKeyword(pass.Material.shader, "_FULUMA_NEAR_FADE");

			scene.UseNearPlaneFade(true, 0.25f);
			Assert.IsTrue(pass.Material.IsKeywordEnabled(keyword));
			Assert.IsTrue(pass.LowResolutionMaterial.IsKeywordEnabled(keyword));
			Assert.AreEqual(0.25f, pass.Material.GetFloat("_FulumaNearFadeDistance"));

			scene.UseNearPlaneFade(false, 0.25f);
			Assert.IsFalse(pass.Material.IsKeywordEnabled(keyword));
			Assert.IsFalse(pass.LowResolutionMaterial.IsKeywordEnabled(keyword));
		}

		/// <summary>
		/// The fade only touches proxy fragments within its distance of the near plane: lights away from the camera
		/// look exactly the same with it on.
		/// </summary>
		[Test]
		public void NearPlaneFade_LeavesLightsAwayFromTheCameraUnchanged()
		{
			using var scene = new RenderScene(size: 128);
			scene.AddLight(new Vector3(-3f, 0f, 6f), Quaternion.LookRotation(Vector3.right));
			scene.AddLight(new Vector3(2f, 1.5f, 9f), Quaternion.LookRotation(new Vector3(-0.3f, -1f, 0.4f)));

			var off = scene.RenderLightContribution();
			scene.UseNearPlaneFade(true, 0.5f);
			var on = scene.RenderLightContribution();

			var (_, difference) = CompareWithReference(off, on, 128);
			Assert.Less(difference, 1e-4f, "Lights away from the near plane shouldn't change.");
		}

		/// <summary>
		/// A short light whose apex is at the camera: its proxy (inside pass) lies within the fade distance of the near
		/// plane, so the fade dims it, smoothly and without invalid pixels.
		/// </summary>
		[Test]
		public void NearPlaneFade_DimsALightAtTheCamera()
		{
			using var scene = new RenderScene(size: 128);
			var data = RenderScene.DefaultTestLight();
			data.Range = 0.5f;
			data.Intensity = 20f;
			scene.AddLight(new Vector3(0f, 0f, 0.05f), Quaternion.LookRotation(Vector3.forward), data);

			var off = scene.RenderLightContribution();
			scene.UseNearPlaneFade(true, 0.5f);
			var on = scene.RenderLightContribution();

			double offEnergy = 0, onEnergy = 0;
			for (var y = 0; y < 128; y++)
			for (var x = 0; x < 128; x++)
			{
				offEnergy += Luminance(off.Pixel(x, y));
				onEnergy += Luminance(on.Pixel(x, y));
			}

			TestContext.WriteLine($"NEAR FADE: glow with the fade {onEnergy / offEnergy:P1} of without");
			Assert.Greater(offEnergy, 1.0, "The test light should glow.");
			Assert.AreEqual(0, on.BadPixelCount);
			Assert.Less(onEnergy, offEnergy * 0.95, "The fade should dim a light right at the near plane.");
			Assert.Greater(onEnergy, 0.0, "The fade shouldn't remove the light entirely.");
		}

		/// <summary>
		/// With LargeOnly, small lights draw at full resolution and large ones through the downsampled buffer: the image is
		/// the small light rendered at full resolution plus the large light rendered downsampled.
		/// </summary>
		[Test]
		public void Render_LargeLightsOnly_IsSmallLightsAtFullResolutionPlusLargeLightsDownsampled()
		{
			const int size = 128;
			const float scale = 0.25f;

			LightContribution smallFull, largeDownsampled, split;
			var lowResolutionRecorded = false;

			using (var scene = new RenderScene(size: size))
			{
				AddSmallTestLight(scene);
				smallFull = scene.RenderLightContribution();
			}

			using (var scene = new RenderScene(size: size))
			{
				scene.UseLightBufferScale(scale);
				AddLargeTestLight(scene);
				largeDownsampled = scene.RenderLightContribution();
			}

			using (var scene = new RenderScene(size: size))
			{
				scene.UseLightBufferScale(scale);
				scene.UseLightBufferLights(LightBufferLights.LargeOnly);
				AddSmallTestLight(scene);
				AddLargeTestLight(scene);
				split = scene.RenderLightContribution(() =>
					lowResolutionRecorded = scene.Feature.SpotLightRenderPass.LastRecordedLowResolutionPasses);
			}

			double expectedTotal = 0, difference = 0, smallTotal = 0;
			for (var y = 0; y < size; y++)
			for (var x = 0; x < size; x++)
			{
				var small = Luminance(smallFull.Pixel(x, y));
				var expected = small + Luminance(largeDownsampled.Pixel(x, y));
				expectedTotal += expected;
				smallTotal += small;
				difference += math.abs(Luminance(split.Pixel(x, y)) - expected);
			}

			var error = (float)(difference / expectedTotal);
			TestContext.WriteLine($"LARGE ONLY: small light {smallTotal / expectedTotal:P1} of the glow, " +
			                      $"difference from small at full res + large downsampled {error:P3}");

			Assert.IsTrue(lowResolutionRecorded, "A camera seen for the first time should get the downsampled buffer's passes.");
			Assert.AreEqual(0, split.BadPixelCount, "NaN, infinite or negative pixels.");
			Assert.Greater(smallTotal, 0.01 * expectedTotal, "The small light should glow visibly.");
			Assert.Less(error, 0.005f, "The split render should equal its two halves.");
		}

		/// <summary>
		/// Culling finishes after the graph is recorded, so whether the downsampled buffer's passes are recorded depends on
		/// the camera's previous culling. Without large lights there, they're skipped and the full-resolution pass draws
		/// everything: a large light appearing in that frame is drawn at full resolution, never lost; the frame after, it
		/// goes through the buffer.
		/// </summary>
		[Test]
		public void Render_LargeLightsOnly_SkipsTheBufferAfterAFrameWithoutLargeLights()
		{
			const int size = 128;

			LightContribution smallReference, bothReference;

			using (var scene = new RenderScene(size: size))
			{
				AddSmallTestLight(scene);
				smallReference = scene.RenderLightContribution();
				AddLargeTestLight(scene);
				bothReference = scene.RenderLightContribution();
			}

			using var split = new RenderScene(size: size);
			split.UseLightBufferScale(0.25f);
			split.UseLightBufferLights(LightBufferLights.LargeOnly);
			AddSmallTestLight(split);

			var recorded = false;
			LightContribution Render() => split.RenderLightContribution(() =>
				recorded = split.Feature.SpotLightRenderPass.LastRecordedLowResolutionPasses);

			var first = Render();
			Assert.IsTrue(recorded, "First frame of a camera: the buffer's passes are recorded.");
			AssertMatches(smallReference, first, size, 0.005f, "First frame (empty buffer)");

			var second = Render();
			Assert.IsFalse(recorded, "No large lights in the last frame: no buffer passes.");
			AssertMatches(smallReference, second, size, 0.005f, "Without buffer passes");

			AddLargeTestLight(split);
			var third = Render();
			Assert.IsFalse(recorded, "The last frame still had no large lights.");
			AssertMatches(bothReference, third, size, 0.005f, "A large light appearing without buffer passes (full resolution)");

			var fourth = Render();
			Assert.IsTrue(recorded, "The last frame had a large light: the buffer's passes are back.");
			AssertMatches(bothReference, fourth, size, 0.15f, "The large light through the buffer");
		}

		// Looking along the beam from behind the light, view direction pitched 25 degrees from the light axis
		// (reported artifact setup). Only runs when FULUMA_TEST_IMAGE_DIR is set.
		[Test]
		public void Render_ShallowAngleViews_SavesSheet()
		{
			var directory = ImageDirectoryOrIgnore("the shallow-angle views");

			const int tile = 384;
			float[] distances = { 1f, 3f, 6f };
			(float inner, float outer)[] angles = { (30f, 60f), (20f, 30f) };

			var sheet = new ContactSheet(tile, columns: distances.Length, rows: angles.Length);

			var viewDirection = new Vector3(0f, -math.sin(math.radians(25f)), math.cos(math.radians(25f)));
			var toApex = new Vector3(0f, -math.sin(math.radians(15f)), math.cos(math.radians(15f)));

			for (var row = 0; row < angles.Length; row++)
			for (var column = 0; column < distances.Length; column++)
			{
				using var scene = new RenderScene(size: tile);

				// The apex sits 10 degrees above the view center.
				var cameraPosition = -toApex * distances[column];
				scene.SetCamera(cameraPosition, cameraPosition + viewDirection);

				var data = RenderScene.DefaultTestLight();
				data.InnerAngleDegrees = angles[row].inner;
				data.OuterAngleDegrees = angles[row].outer;
				data.Color = new Color32(255, 170, 60, 255);
				scene.AddLight(Vector3.zero, Quaternion.LookRotation(Vector3.forward), data);

				var glow = scene.RenderLightContribution();
				TestContext.WriteLine($"Distance {distances[column]} angles {angles[row]}: max {glow.MaxLuminance:F3}, bad {glow.BadPixelCount}");

				sheet.Place(column, row, glow.Tonemapped());
			}

			sheet.Save(System.IO.Path.Combine(directory, "shallow.png"));
		}

		// ---------- Benchmark (only runs when FULUMA_BENCHMARK is set) ----------

		/// <summary>
		/// GPU cost of many large overlapping lights: 64 lights fill a 1024x1024 HDR target from inside and outside their
		/// cones. Reports the median per-frame time with lights minus without, over several runs. For comparing shader
		/// changes on one machine, not an absolute budget. Downsampled, the time includes the depth downsample and the
		/// composite (neither runs without lights).
		/// </summary>
		[Test]
		public void Benchmark_ManyOverlappingLights([Values(1f, 0.75f, 0.5f, 0.3f, 0.25f, 0.125f, 0.0625f, 0.03f, 0.01f)] float scale,
			[Values(LightBufferUpsample.DepthAware, LightBufferUpsample.Bilinear)] LightBufferUpsample upsample)
		{
			IgnoreUnlessBenchmarking();
			var downsampled = LightBufferScaling.IsDownsampled(scale);
			if (downsampled == false && upsample != LightBufferUpsample.DepthAware)
				Assert.Ignore("Full resolution has no upsample.");

			using var scene = new RenderScene(size: 1024);
			scene.SetCamera(new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, 6f), 70f);
			if (downsampled)
			{
				scene.UseLightBufferScale(scale);
				scene.UseLightBufferUpsample(upsample);
			}

			var data = RenderScene.DefaultTestLight();
			data.InnerAngleDegrees = 30f;
			data.OuterAngleDegrees = 60f;

			var random = new System.Random(1234);
			for (var i = 0; i < 64; i++)
			{
				var position = new Vector3((float)random.NextDouble() * 8f - 4f, (float)random.NextDouble() * 6f - 3f, (float)random.NextDouble() * 6f - 2f);
				var direction = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() + 0.2f);
				scene.AddLight(position, Quaternion.LookRotation(direction), data);
			}

			var samples = MeasureLightCost(scene);
			var median = samples[samples.Count / 2];
			TestContext.WriteLine($"BENCHMARK 64 lights @1024x1024, light buffer scale {scale} {upsample}, on {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}): " +
			                      $"median {median:F3} ms/frame for the lights, runs [{string.Join(", ", samples.Select(x => x.ToString("F3")))}]");
		}

		/// <summary>
		/// GPU cost of the wide-cone intersection variant: the 64 overlapping lights of
		/// <see cref="Benchmark_ManyOverlappingLights"/> at full resolution, with the variant off and on (alternating runs,
		/// so drift affects both), and with every light widened to 170 degrees.
		/// </summary>
		[Test]
		public void Benchmark_WideConePrecision([Values(60f, 170f)] float outerAngle)
		{
			IgnoreUnlessBenchmarking();

			using var scene = new RenderScene(size: 1024);
			scene.SetCamera(new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, 6f), 70f);

			var data = RenderScene.DefaultTestLight();
			data.OuterAngleDegrees = outerAngle;
			data.InnerAngleDegrees = outerAngle * 0.5f;

			var random = new System.Random(1234);
			for (var i = 0; i < 64; i++)
			{
				var position = new Vector3((float)random.NextDouble() * 8f - 4f, (float)random.NextDouble() * 6f - 3f, (float)random.NextDouble() * 6f - 2f);
				var direction = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() + 0.2f);
				scene.AddLight(position, Quaternion.LookRotation(direction), data);
			}

			var off = new List<double>();
			var on = new List<double>();
			for (var round = 0; round < 3; round++)
			{
				scene.UseWideConePrecision(false);
				off.AddRange(MeasureLightCost(scene, runs: 3));
				scene.UseWideConePrecision(true);
				on.AddRange(MeasureLightCost(scene, runs: 3));
			}

			off.Sort();
			on.Sort();
			var medianOff = off[off.Count / 2];
			var medianOn = on[on.Count / 2];
			TestContext.WriteLine($"BENCHMARK wide cone precision, 64 lights {outerAngle} degrees @1024x1024 on {SystemInfo.graphicsDeviceName}: " +
			                      $"off {medianOff:F3} ms, on {medianOn:F3} ms ({(medianOn / medianOff - 1) * 100:+0.0;-0.0}%)");
		}

		/// <summary>
		/// 64 lights 10-16 units away (the camera is outside every light volume) behind a wall that hides most of them.
		/// Measures how much work is spent on light pixels that end up occluded.
		/// </summary>
		[Test]
		public void Benchmark_LightsBehindWall()
		{
			IgnoreUnlessBenchmarking();

			using var scene = new RenderScene(size: 1024);
			scene.SetCamera(new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, 6f), 70f);
			scene.AddOccluder(new Vector3(-1.5f, 0f, 6f), new Vector3(14f, 12f, 0.2f));

			var data = RenderScene.DefaultTestLight();
			data.Range = 6f;
			data.InnerAngleDegrees = 30f;
			data.OuterAngleDegrees = 60f;

			var random = new System.Random(4321);
			for (var i = 0; i < 64; i++)
			{
				var position = new Vector3((float)random.NextDouble() * 12f - 6f, (float)random.NextDouble() * 8f - 4f, (float)random.NextDouble() * 6f + 10f);
				var direction = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 1.2f);
				scene.AddLight(position, Quaternion.LookRotation(direction), data);
			}

			var samples = MeasureLightCost(scene);
			var median = samples[samples.Count / 2];
			TestContext.WriteLine($"BENCHMARK behind wall, 64 lights @1024x1024 on {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}): " +
			                      $"median {median:F3} ms/frame for the lights, runs [{string.Join(", ", samples.Select(x => x.ToString("F3")))}]");
		}

		private int CountMaterials()
			=> Resources.FindObjectsOfTypeAll<Material>().Count(m => m.shader == _shader);
	}
}
