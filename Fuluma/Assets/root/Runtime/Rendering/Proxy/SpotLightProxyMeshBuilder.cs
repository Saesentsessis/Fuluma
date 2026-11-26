using System;
using Saesentsessis.Fuluma.Data;
using Saesentsessis.MeshGeneration;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Saesentsessis.Fuluma.Rendering.Proxy
{
	/// <summary>
	/// Builds spot light proxy meshes from a <see cref="SpotLightProxyArrayDescriptor"/> with the mesh generator: per LOD,
	/// a hollow cone (the side) and a hollow half-sphere (the cap) that share the rim ring.
	/// </summary>
	/// <remarks>
	/// Vertices are rewritten into <b>proxy space</b>, which the vertex shader (<c>FulumaSpotLightForwardPass.hlsl</c>)
	/// turns into a cone of the light's angle and range:
	/// <list type="bullet">
	/// <item>Apex: <c>(0, 0, -1)</c> (negative z is the marker). It maps to the light origin.</item>
	/// <item>Every other vertex: <c>(cos a, sin a, u)</c>. <c>a</c> is the angle around the axis, and <c>u</c> runs from 0
	/// (rim, where the cone side meets the cap) to 1 (cap tip on the axis), uniformly per band. It maps to polar angle
	/// <c>(1 - u) * widenedHalfAngle</c> at distance <c>range * inflation</c>.</item>
	/// </list>
	/// One LOD has <c>1 + sum(bands) + 1</c> vertices (apex, every band, tip): the cone's ring is the cap's band 0, not a
	/// copy of it. The generator places both rings with the same resolution from the same starting angle, so the cone's
	/// own indices (apex 0, ring 1 to n) are already proxy indices, and the cap's are generated with a base vertex of 1.
	/// The generator's winding is kept: <c>cross(b - a, c - a)</c> points outward (checked by
	/// <c>ProxyMesh_WindsOutward_AndEnclosesTheTrueVolume</c>), as the shader's passes expect (front faces outside, back
	/// faces inside).
	/// </remarks>
	internal static class SpotLightProxyMeshBuilder
	{
		public const string MeshName = "Fuluma Spot Light Proxy";

		/// <summary>
		/// LOD data only lives while the mesh is built. Persistent (not Temp) on purpose: a missed dispose shows up as
		/// "Leak Detected" instead of being hidden.
		/// </summary>
		private const Allocator ProxyDataAllocator = Allocator.Persistent;

		/// <summary>
		/// Step of the outer half-angles <see cref="FitShape"/> probes, up to the clamp.
		/// </summary>
		private const float FitStepDegrees = 0.5f;

		/// <summary>
		/// Margin on the fitted inflation, for the fit's sampling and float rounding in the shader.
		/// </summary>
		private const float FitMargin = 1.01f;

		/// <summary>
		/// Builds the first <paramref name="maxLodCount"/> LODs of <paramref name="descriptor"/> (or of the built-in set
		/// when it's null), at most <see cref="SpotLightProxyArrayDescriptor.MaxLodCount"/>, into one mesh.
		/// </summary>
		public static SpotLightProxyLods CreateLods(SpotLightProxyArrayDescriptor descriptor,
			int maxLodCount = SpotLightProxyArrayDescriptor.MaxLodCount)
		{
			var data = descriptor != null
				? descriptor.CreateData(ProxyDataAllocator)
				: SpotLightProxyArrayData.CreateDefault(ProxyDataAllocator);

			try
			{
				return CreateLods(data, maxLodCount);
			}
			finally
			{
				data.Dispose();
			}
		}

		/// <summary>
		/// Builds the first <paramref name="maxLodCount"/> LODs of <paramref name="data"/> (at least one, at most
		/// <see cref="SpotLightProxyArrayDescriptor.MaxLodCount"/>) into one mesh. Screen sizes below 0 become 0, and
		/// each is lowered to the previous one's if larger, so they never increase.
		/// </summary>
		/// <exception cref="ArgumentException"><paramref name="data"/> has no LODs, a LOD has no bands or a band has fewer
		/// than 3 vertices, or the LODs need more vertices than 16-bit indices allow.</exception>
		public static SpotLightProxyLods CreateLods(in SpotLightProxyArrayData data,
			int maxLodCount = SpotLightProxyArrayDescriptor.MaxLodCount)
		{
			if (data.Length == 0)
				throw new ArgumentException("The proxy array has no LODs.", nameof(data));

			var count = math.clamp(math.min(data.Length, maxLodCount), 1, SpotLightProxyArrayDescriptor.MaxLodCount);
			var lods = new BuiltLod[count];
			var screenSizes = new float[count];

			for (var lod = 0; lod < count; lod++)
			{
				lods[lod] = BuildLod(data, lod);

				// Non-increasing, so culling can count the thresholds a light falls below.
				screenSizes[lod] = math.max(0f, data.GetScreenSize(lod));
				if (lod > 0)
					screenSizes[lod] = math.min(screenSizes[lod], screenSizes[lod - 1]);
			}

			var shapes = new SpotLightProxyShape[count];
			for (var lod = 0; lod < count; lod++)
				shapes[lod] = lods[lod].Shape;

			return new SpotLightProxyLods(CreateMesh(lods), shapes, screenSizes);
		}

		/// <summary>
		/// Builds one LOD alone, as a single-submesh mesh.
		/// </summary>
		/// <param name="shape">The fitted shape of that LOD.</param>
		public static Mesh Create(in SpotLightProxyArrayData data, int lod, out SpotLightProxyShape shape)
		{
			var built = BuildLod(data, lod);
			shape = built.Shape;
			return CreateMesh(new[] { built });
		}

		private static BuiltLod BuildLod(in SpotLightProxyArrayData data, int lod)
		{
			if ((uint)lod >= (uint)data.Length)
				throw new ArgumentOutOfRangeException(nameof(lod), lod, $"The proxy array has {data.Length} LODs.");

			// A view of memory owned by `data`: the half-sphere descriptor built on it must not be disposed (its Dispose
			// would dispose the view). Creating it validates the bands (at least one, each with 3 or more vertices).
			var bands = data.GetBandResolutions(lod);
			var cap = MeshGenerator.CreateHollowHalfSphereMeshDescriptor(bands);

			// Only the cone's triangles are kept: its vertices are the apex and the rim ring, which the cap writes again.
			var cone = MeshGenerator.CreateHollowConeMeshDescriptor(bands[0], math.PIHALF);

			// Proxy layout: apex, then the cap's vertices (its band 0 doubles as the cone's ring).
			var generated = new NativeArray<float3>(1 + cap.VertexCount, Allocator.Temp);
			var generatedIndices = new NativeArray<int>(cone.IndexCount + cap.IndexCount, Allocator.Temp);

			try
			{
				MeshGenerator.GenerateHollowConeMeshData(in cone, generated.Slice(0, cone.VertexCount),
					generatedIndices.Slice(0, cone.IndexCount));
				MeshGenerator.GenerateHollowHalfSphereMeshData(in cap, generated.Slice(1),
					generatedIndices.Slice(cone.IndexCount), baseVertexIndex: 1);

				var vertices = ToProxySpace(generated, bands);
				var indices = generatedIndices.ToArray();
				var shape = FitShape(vertices, indices, bands[0], capIndexStart: cone.IndexCount);
				return new BuiltLod(vertices, indices, shape);
			}
			finally
			{
				generated.Dispose();
				generatedIndices.Dispose();
			}
		}

		/// <summary>
		/// The generated vertices (apex, then the cap's) in proxy space: the apex marker, one ring per band from the rim
		/// (band 0, u = 0) toward the tip, then the tip.
		/// </summary>
		private static Vector3[] ToProxySpace(NativeArray<float3> generated, NativeArray<int> bands)
		{
			var vertices = new Vector3[generated.Length];
			vertices[0] = new Vector3(0f, 0f, -1f);

			var vertex = 1;
			for (var band = 0; band < bands.Length; band++)
			{
				var u = band / (float)bands.Length;
				for (var i = 0; i < bands[band]; i++, vertex++)
					vertices[vertex] = Ring(generated[vertex], u);
			}

			vertices[vertex] = new Vector3(0f, 0f, 1f);
			return vertices;
		}

		/// <summary>
		/// A ring vertex in proxy space: its direction around the axis, and <paramref name="u"/>.
		/// </summary>
		private static Vector3 Ring(float3 generated, float u)
		{
			var direction = math.normalizesafe(generated.xy, new float2(1f, 0f));
			return new Vector3(direction.x, direction.y, u);
		}

		/// <summary>
		/// One mesh holding every LOD's vertices back to back, with one submesh per LOD (local indices, base vertex).
		/// </summary>
		private static Mesh CreateMesh(BuiltLod[] lods)
		{
			var vertexCount = 0;
			foreach (var lod in lods)
				vertexCount += lod.Vertices.Length;

			if (vertexCount > ushort.MaxValue + 1)
				throw new ArgumentException($"The proxy LODs need {vertexCount} vertices; 16-bit indices allow {ushort.MaxValue + 1}.");

			var vertices = new Vector3[vertexCount];
			var offset = 0;
			foreach (var lod in lods)
			{
				lod.Vertices.CopyTo(vertices, offset);
				offset += lod.Vertices.Length;
			}

			var mesh = new Mesh
			{
				name = MeshName,
				hideFlags = HideFlags.HideAndDontSave,
				subMeshCount = lods.Length
			};

			mesh.SetVertices(vertices);

			offset = 0;
			for (var i = 0; i < lods.Length; i++)
			{
				mesh.SetIndices(lods[i].Indices, MeshTopology.Triangles, i, calculateBounds: false, baseVertex: offset);
				offset += lods[i].Vertices.Length;
			}

			// Instances are placed by the vertex shader; the mesh bounds are never used for culling.
			mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
			mesh.UploadMeshData(markNoLongerReadable: false);

			return mesh;
		}

		/// <summary>
		/// Fits the cap inflation: for outer half-angles up to the clamp, the plane of every cap face must stay at least
		/// the light's range from the light origin. Stores the worst face angle per radian of widened half-angle.
		/// </summary>
		/// <remarks>
		/// In double precision: the smallest probes make faces ~0.009 wide next to z = 1 with face angles of ~0.0025 rad,
		/// where float32 keeps only a few digits of the edge vectors and of the angle (MeshGen's Mono and Burst vertices,
		/// one rounding step apart, gave fitted shapes 0.6% apart). The vertices themselves stay float32.
		/// </remarks>
		private static SpotLightProxyShape FitShape(Vector3[] vertices, int[] indices, int rimSegments, int capIndexStart)
		{
			var cosSegment = math.cos(math.PI / rimSegments);
			var capAnglePerWidened = 0.0;

			for (var degrees = FitStepDegrees; degrees <= SpotLightData.MaxOuterHalfAngleDegrees; degrees += FitStepDegrees)
			{
				// SpotLightProxyShape.Evaluate's widened half-angle, in double.
				math.sincos(math.radians((double)degrees), out var sinOuter, out var cosOuter);
				var widened = math.atan2(sinOuter, cosOuter * cosSegment);

				var faceAngle = LargestCapFaceAngle(vertices, indices, capIndexStart, widened);
				capAnglePerWidened = math.max(capAnglePerWidened, faceAngle / widened);
			}

			return new SpotLightProxyShape(cosSegment, (float)capAnglePerWidened * FitMargin);
		}

		/// <summary>
		/// Largest angular size of a cap face (half the angle its plane cuts from the unit sphere around the light),
		/// with the proxy widened to <paramref name="widenedHalfAngle"/> and not inflated.
		/// </summary>
		private static double LargestCapFaceAngle(Vector3[] vertices, int[] indices, int capIndexStart, double widenedHalfAngle)
		{
			var largest = 0.0;

			for (var i = capIndexStart; i < indices.Length; i += 3)
			{
				var a = MapCapVertex(vertices[indices[i]], widenedHalfAngle);
				var b = MapCapVertex(vertices[indices[i + 1]], widenedHalfAngle);
				var c = MapCapVertex(vertices[indices[i + 2]], widenedHalfAngle);

				var normal = math.cross(b - a, c - a);
				var length = math.length(normal);
				if (length < 1e-18)
					continue; // degenerate face (all vertices on the tip)

				// atan2 of sine and cosine: acos of a cosine this close to 1 loses most of the angle's digits.
				var unitNormal = normal / length;
				var planeDistance = math.abs(math.dot(unitNormal, a));
				largest = math.max(largest, math.atan2(math.length(math.cross(unitNormal, a)), planeDistance));
			}

			return largest;
		}

		/// <summary>
		/// <see cref="SpotLightProxyShape.MapVertex"/> for a cap vertex (z >= 0) in double, with range and inflation 1: on the
		/// unit sphere around the light.
		/// </summary>
		private static double3 MapCapVertex(Vector3 proxy, double widenedHalfAngle)
		{
			math.sincos((1.0 - proxy.z) * widenedHalfAngle, out var sinPolar, out var cosPolar);
			return new double3(proxy.x * sinPolar, proxy.y * sinPolar, cosPolar);
		}

		/// <summary>
		/// One LOD in proxy space, with indices local to its own vertices.
		/// </summary>
		private readonly struct BuiltLod
		{
			public readonly Vector3[] Vertices;
			public readonly int[] Indices;
			public readonly SpotLightProxyShape Shape;

			public BuiltLod(Vector3[] vertices, int[] indices, SpotLightProxyShape shape)
			{
				Vertices = vertices;
				Indices = indices;
				Shape = shape;
			}
		}
	}
}
