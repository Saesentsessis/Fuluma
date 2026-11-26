using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Saesentsessis.Fuluma.Rendering.Proxy;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Saesentsessis.Fuluma.Tests.Editor
{
	/// <summary>
	/// Spot light proxy meshes built from <see cref="SpotLightProxyArrayDescriptor"/> LODs, checked on the CPU through
	/// the C# copy of the vertex shader's mapping. Needs no graphics device.
	/// </summary>
	public class ProxyMeshTests
	{
		/// <summary>
		/// Relative tolerance between two fits of the same LOD: MeshGen generates the vertices with Burst, and the Editor
		/// runs a call as Mono code until Burst has compiled it, so two builds can differ by a rounding step.
		/// </summary>
		private const float FitTolerance = 1e-5f;

		private static void AssertSameFit(float expected, float actual, string message)
			=> Assert.AreEqual(expected, actual, expected * FitTolerance, message);

		// ---------- Helpers ----------

		/// <summary>
		/// The descriptor class's default LOD set (5 LODs, down to a 3-segment rim), as native data. Dispose it.
		/// </summary>
		private static SpotLightProxyArrayData CreateDescriptorData()
		{
			var descriptor = ScriptableObject.CreateInstance<SpotLightProxyArrayDescriptor>();
			var data = descriptor.CreateData(Allocator.Persistent);
			Object.DestroyImmediate(descriptor);
			return data;
		}

		private static IEnumerable<Vector3> SampleTrueVolumeSurface(float range, float halfAngleDegrees)
		{
			var halfAngle = math.radians(halfAngleDegrees);
			var rimHeight = range * math.cos(halfAngle);

			for (var a = 0; a < 72; a++)
			{
				var azimuth = a * (2f * math.PI / 72);
				var direction = new Vector2(math.cos(azimuth), math.sin(azimuth));

				// Cone side
				for (var h = 1; h <= 8; h++)
				{
					var height = rimHeight * h / 8f;
					var radius = height * math.tan(halfAngle);
					yield return new Vector3(direction.x * radius, direction.y * radius, height);
				}

				// Spherical cap
				for (var p = 0; p <= 8; p++)
				{
					var polar = halfAngle * p / 8f;
					yield return range * new Vector3(direction.x * math.sin(polar), direction.y * math.sin(polar), math.cos(polar));
				}
			}
		}

		private NativeLeakDetectionMode _previousLeakMode;

		[OneTimeSetUp]
		public void OneTimeSetUp()
		{
			_previousLeakMode = NativeLeakDetection.Mode;
			NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;
		}

		[OneTimeTearDown]
		public void OneTimeTearDown() => NativeLeakDetection.Mode = _previousLeakMode;

		[Test]
		public void ProxyMesh_HasExpectedTopology([Values(0, 1, 2, 3, 4)] int lod)
		{
			var data = CreateDescriptorData();
			Mesh mesh = null;

			try
			{
				var bands = data.GetBandResolutions(lod).ToArray();
				mesh = SpotLightProxyMeshBuilder.Create(data, lod, out _);

				// Apex, every band (band 0 is the rim the cone side shares), tip.
				var vertexCount = 1 + bands.Sum() + 1;

				// Cone fan + tip fan + one bridge per pair of bands (as many triangles as both bands have vertices).
				var triangleCount = bands[0] + bands[^1];
				for (var band = 1; band < bands.Length; band++)
					triangleCount += bands[band - 1] + bands[band];

				Assert.AreEqual(vertexCount, mesh.vertexCount);
				Assert.AreEqual(triangleCount * 3, (int)mesh.GetIndexCount(0));
				Assert.AreEqual(SpotLightProxyMeshBuilder.MeshName, mesh.name);
			}
			finally
			{
				if (mesh != null)
					Object.DestroyImmediate(mesh);

				data.Dispose();
			}
		}

		[Test]
		public void ProxyLods_OneSubmeshPerLod_MatchingTheSingleLodMeshes()
		{
			var descriptor = ScriptableObject.CreateInstance<SpotLightProxyArrayDescriptor>();
			var data = descriptor.CreateData(Allocator.Persistent);
			var lods = SpotLightProxyMeshBuilder.CreateLods(descriptor);
			var builtIn = SpotLightProxyMeshBuilder.CreateLods(null);

			try
			{
				Assert.AreEqual(descriptor.LodCount, lods.Count);
				Assert.AreEqual(lods.Count, lods.Mesh.subMeshCount);

				for (var lod = 0; lod < lods.Count; lod++)
				{
					var single = SpotLightProxyMeshBuilder.Create(data, lod, out var shape);
					var submesh = lods.Mesh.GetSubMesh(lod);

					Assert.AreEqual(single.vertexCount, submesh.vertexCount, $"LOD{lod} vertices");
					Assert.AreEqual(single.GetIndexCount(0), (uint)submesh.indexCount, $"LOD{lod} indices");
					Assert.AreEqual(shape.CosSegment, lods.Shapes[lod].CosSegment);
					AssertSameFit(shape.CapAnglePerWidened, lods.Shapes[lod].CapAnglePerWidened, $"LOD{lod} cap angle");
					Assert.AreEqual(descriptor.Lods[lod].ScreenSize, lods.ScreenSizes[lod]);
					if (lod > 0)
						Assert.LessOrEqual(lods.ScreenSizes[lod], lods.ScreenSizes[lod - 1]);

					TestContext.WriteLine($"LOD{lod}: {submesh.vertexCount} vertices, {submesh.indexCount / 3} triangles, " +
					                      $"screen size {lods.ScreenSizes[lod]}, cos segment {shape.CosSegment:F4}, " +
					                      $"cap angle per widened {shape.CapAnglePerWidened:F4}, max inflation {shape.MaxInflation:F4}");
					Object.DestroyImmediate(single);
				}

				// The class defaults and the built-in set share LOD0.
				Assert.AreEqual(builtIn.Mesh.GetSubMesh(0).vertexCount, lods.Mesh.GetSubMesh(0).vertexCount);
				AssertSameFit(builtIn.Shapes[0].CapAnglePerWidened, lods.Shapes[0].CapAnglePerWidened, "LOD0 cap angle");
			}
			finally
			{
				Object.DestroyImmediate(lods.Mesh);
				Object.DestroyImmediate(builtIn.Mesh);
				data.Dispose();
				Object.DestroyImmediate(descriptor);
			}
		}

		[Test]
		public void ProxyMesh_WindsOutward_AndEnclosesTheTrueVolume(
			[Values(0, 1, 2, 3, 4)] int lod,
			[Values(2f, 10f, 30f, 60f, 90f, 120f, 160f)] float outerAngleDegrees)
		{
			const float range = 3f;
			var cosOuter = math.cos(math.radians(outerAngleDegrees) * 0.5f);
			var data = CreateDescriptorData();
			Mesh mesh;
			(float widened, float inflation) mapping;

			try
			{
				mesh = SpotLightProxyMeshBuilder.Create(data, lod, out var shape);
				shape.Evaluate(cosOuter, out var widened, out var inflation);
				TestContext.WriteLine($"LOD{lod}, outer {outerAngleDegrees} degrees: widened half-angle {math.degrees(widened):F2} degrees, inflation {inflation:F4}");
				mapping = (widened, inflation);
			}
			finally
			{
				data.Dispose();
			}

			try
			{
				// Proxy space to light space (C# copy of the vertex shader).
				var vertices = mesh.vertices
					.Select(v => (Vector3)SpotLightProxyShape.MapVertex(v, range, mapping.widened, mapping.inflation))
					.ToArray();
				var indices = mesh.GetIndices(0);
				var interior = new Vector3(0f, 0f, 0.5f * range * cosOuter);

				var planes = new List<(Vector3 normal, Vector3 point)>();
				for (var i = 0; i < indices.Length; i += 3)
				{
					var a = vertices[indices[i]];
					var b = vertices[indices[i + 1]];
					var c = vertices[indices[i + 2]];
					var normal = Vector3.Cross(b - a, c - a).normalized;

					Assert.Greater(Vector3.Dot(normal, a - interior), 0f, $"Triangle {i / 3} winds inward.");
					planes.Add((normal, a));
				}

				foreach (var point in SampleTrueVolumeSurface(range, outerAngleDegrees * 0.5f))
				foreach (var (normal, onPlane) in planes)
					Assert.LessOrEqual(Vector3.Dot(normal, point - onPlane), 1e-4f * range,
						$"True volume point {point} lies outside the proxy.");
			}
			finally
			{
				Object.DestroyImmediate(mesh);
			}
		}
	}
}
