using System;
using System.Diagnostics;
using Saesentsessis.Fuluma.Data;
using Saesentsessis.MeshGeneration;
using Saesentsessis.MeshGeneration.Shapes;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Tests
{
#if UNITY_6000_5_OR_NEWER
	// Immutable: a profiler marker.
	[NoAutoStaticsCleanup]
#endif
	[ExecuteInEditMode]
	public class ConeIntersectionTester : MonoBehaviour
	{
		public static readonly ProfilerMarker GenerateSphereMarker = new ProfilerMarker(ProfilerCategory.Scripts, "Fuluma.GenerateSphere");

		[SerializeField] private Placement lightStaticTransform;
		[SerializeField] private SpotLightData lightData;
		[SerializeField] private float3 rayOrigin;
		[SerializeField] private Quaternion rayDirection;
		[SerializeField] private float rayLength = 10f;
		[SerializeField] private float hitRadius = 0.1f;

		[Header("Conversion")]
		[SerializeField] private float halfAngleDegrees = 12.25f;
		[SerializeField] private float cosTheta;
		
		[Header("Mesh Gen")]
		[SerializeField] private bool useBands = true;
		[SerializeField] private int baseResolution = 8;
		[SerializeField] private int[] bands;
		[SerializeField] private MeshFilter meshFilter;
		[SerializeField] private MeshFilter sphereMeshFilter;

		[Header("Tests")]
		[SerializeField] private int warmupIterationCount = 50;
		[SerializeField] private int iterationCount = 10000;
		[SerializeField] private int testResolution = 1024;
		[SerializeField] private int[] testBands;

		private Mesh _coneMesh;
		private Mesh _halfSphereMesh;
		private HollowConeMeshDescriptor _hollowConeDescriptor;
		private HollowHalfSphereMeshDescriptor _hollowHalfSphereDescriptor;
		
		private bool _wasHit;
		private HitData _hit0;
		private HitData _hit1;

		private void Start()
		{
			UpdateConeMesh();
			UpdateSphereMesh();
		}

		[ContextMenu(nameof(ExecutePositionalError))]
		private unsafe void ExecutePositionalError()
		{
			var verts = new NativeArray<float3>(256, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
			var diff = new NativeArray<float3>(128, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

			try
			{
				var vertsPtr = (float3*)verts.GetUnsafePtr();

				MeshGeneratorUtils.PopulateCircleVertices(vertsPtr, 1f, 0f, 0f, 128);
				MeshGeneratorUtils.PopulateCircleVerticesExact(vertsPtr, 1f, 0f, 0f, 128);

				var totalError = float3.zero;

				for (var i = 0; i < 128; i++)
				{
					var error = diff[i] = verts[128 + i] - verts[i];

					totalError += error;
				}

				UnityEngine.Debug.Log($"TotalError: {totalError}, error at index 127: {diff[^1]}");
			}
			finally
			{
				verts.Dispose();
				diff.Dispose();
			}
		}
		
		[ContextMenu(nameof(ExecuteMeshGen))]
		private void ExecuteMeshGen()
		{
			var coneDescriptor = MeshGenerator.CreateHollowConeMeshDescriptor(testResolution, 45f);
			var sphereDescriptor = useBands
				? MeshGenerator.CreateHollowHalfSphereMeshDescriptor(new NativeArray<int>(testBands, Allocator.Temp))
				: MeshGenerator.CreateHollowHalfSphereMeshDescriptor(testResolution, Allocator.Temp);
			
			var timeSpansCone = new NativeArray<long>(iterationCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
			var timeSpansSphere = new NativeArray<long>(iterationCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
			var coneVerticesArray = new NativeArray<float3>(coneDescriptor.Resolution + 1, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
			var coneIndicesArray = new NativeArray<ushort>(coneDescriptor.Resolution * 3, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
			var sphereVerticesArray = new NativeArray<float3>(sphereDescriptor.VertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
			var sphereIndicesArray = new NativeArray<ushort>(sphereDescriptor.IndexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
			
			try
			{
				for (var i = 0; i < warmupIterationCount; i++)
				{
					MeshGenerator.GenerateHollowConeMeshData(in coneDescriptor, coneVerticesArray, coneIndicesArray);
					MeshGenerator.GenerateHollowHalfSphereMeshData(in sphereDescriptor, sphereVerticesArray, sphereIndicesArray);
				}
				
				for (var i = 0; i < iterationCount; i++)
				{
					var timestamp = Stopwatch.GetTimestamp();

					MeshGenerator.GenerateHollowConeMeshData(in coneDescriptor, coneVerticesArray, coneIndicesArray);

					timeSpansCone[i] = Stopwatch.GetTimestamp() - timestamp;
					
					timestamp = Stopwatch.GetTimestamp();

					MeshGenerator.GenerateHollowHalfSphereMeshData(in sphereDescriptor, sphereVerticesArray, sphereIndicesArray);

					timeSpansSphere[i] = Stopwatch.GetTimestamp() - timestamp;
				}
			}
			finally
			{
				sphereDescriptor.Dispose();
				coneVerticesArray.Dispose();
				coneIndicesArray.Dispose();
				sphereVerticesArray.Dispose();
				sphereIndicesArray.Dispose();
			}

			try
			{
				var accumulatorCone = 0L;
				var accumulatorSphere = 0L;

				for (var i = iterationCount - 1; i >= 0; i--)
					accumulatorCone += timeSpansCone[i];
				
				for (var i = iterationCount - 1; i >= 0; i--)
					accumulatorSphere += timeSpansSphere[i];
				
				var averageCone = accumulatorCone / iterationCount;
				var averageSphere = accumulatorSphere / iterationCount;
				
				UnityEngine.Debug.Log($"{iterationCount} Cone run: {new TimeSpan(accumulatorCone)}, average: {new TimeSpan(averageCone)}.");
				UnityEngine.Debug.Log($"{iterationCount} Sphere run: {new TimeSpan(accumulatorSphere)}, average: {new TimeSpan(averageSphere)}.");
			}
			finally
			{
				timeSpansCone.Dispose();
				timeSpansSphere.Dispose();
			}
		}

		[ContextMenu(nameof(ExecuteFullCycleMeshGen))]
		private void ExecuteFullCycleMeshGen()
		{
			var timeSpansCone = new NativeArray<long>(iterationCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
			var timeSpansSphere = new NativeArray<long>(iterationCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
			
			for (var i = 0; i < warmupIterationCount; i++)
			{
				var coneDescriptor = MeshGenerator.CreateHollowConeMeshDescriptor(testResolution, math.radians(45f));
				using var sphereDescriptor = MeshGenerator.CreateHollowHalfSphereMeshDescriptor(new NativeArray<int>(testBands, Allocator.Temp));

				var coneMesh = MeshGenerator.CreateHollowConeMesh(coneDescriptor);
				var sphereMesh = MeshGenerator.CreateHollowHalfSphereMesh(sphereDescriptor);
				
				CoreUtils.Destroy(coneMesh);
				CoreUtils.Destroy(sphereMesh);
			}

			for (var i = 0; i < iterationCount; i++)
			{
				var timestamp = Stopwatch.GetTimestamp();

				var coneDescriptor = MeshGenerator.CreateHollowConeMeshDescriptor(testResolution, math.radians(45f));
				var coneMesh = MeshGenerator.CreateHollowConeMesh(coneDescriptor);

				timeSpansCone[i] = Stopwatch.GetTimestamp() - timestamp;
				
				CoreUtils.Destroy(coneMesh);
				
				timestamp = Stopwatch.GetTimestamp();

				using var sphereDescriptor = MeshGenerator.CreateHollowHalfSphereMeshDescriptor(new NativeArray<int>(testBands, Allocator.Temp));
				
				var sphereMesh = MeshGenerator.CreateHollowHalfSphereMesh(sphereDescriptor);

				timeSpansSphere[i] = Stopwatch.GetTimestamp() - timestamp;
				
				CoreUtils.Destroy(sphereMesh);
			}

			try
			{
				var accumulatorCone = 0L;
				var accumulatorSphere = 0L;

				for (var i = iterationCount - 1; i >= 0; i--)
					accumulatorCone += timeSpansCone[i];

				for (var i = iterationCount - 1; i >= 0; i--)
					accumulatorSphere += timeSpansSphere[i];

				var averageCone = accumulatorCone / iterationCount;
				var averageSphere = accumulatorSphere / iterationCount;

				UnityEngine.Debug.Log($"{iterationCount} Cone run: {new TimeSpan(accumulatorCone)}, average: {new TimeSpan(averageCone)}.");
				UnityEngine.Debug.Log($"{iterationCount} Sphere run: {new TimeSpan(accumulatorSphere)}, average: {new TimeSpan(averageSphere)}.");
			}
			finally
			{
				timeSpansCone.Dispose();
				timeSpansSphere.Dispose();
			}
		}
		
		private void OnDestroy()
		{
			CoreUtils.Destroy(_coneMesh);
			_coneMesh = null;
			
			CoreUtils.Destroy(_halfSphereMesh);
			_halfSphereMesh = null;
			
			_hollowHalfSphereDescriptor.Dispose();
			_hollowHalfSphereDescriptor = default;

			if (meshFilter != null)
				meshFilter.sharedMesh = null;

			if (sphereMeshFilter != null)
				sphereMeshFilter.sharedMesh = null;
		}

		[ContextMenu(nameof(UpdateSphereMesh))]
		private void UpdateSphereMesh()
		{
			using (GenerateSphereMarker.Auto())
			{
				var descriptor = useBands
					? MeshGenerator.CreateHollowHalfSphereMeshDescriptor(new NativeArray<int>(bands,
						Allocator.Persistent))
					: MeshGenerator.CreateHollowHalfSphereMeshDescriptor(baseResolution, Allocator.Domain);

				if (descriptor == _hollowHalfSphereDescriptor)
				{
					descriptor.Dispose();
					return;
				}

				_halfSphereMesh = MeshGenerator.CreateHollowHalfSphereMesh(descriptor);
				_hollowHalfSphereDescriptor = descriptor;

				var halfAngleRadians = math.radians(halfAngleDegrees);

				math.sincos(halfAngleRadians, out var sin, out var cos);

				sphereMeshFilter.sharedMesh = _halfSphereMesh;
				sphereMeshFilter.transform.localPosition = new Vector3(0f, 0f, cos);
				sphereMeshFilter.transform.localScale = new Vector3(sin, sin, 1f - cos);
			}
		}

		private void UpdateConeMesh()
		{
			var halfAngleRadians = math.radians(halfAngleDegrees);

			var descriptor = MeshGenerator.CreateHollowConeMeshDescriptor(baseResolution, halfAngleRadians);

			if (descriptor == _hollowConeDescriptor)
				return;

			_coneMesh = MeshGenerator.CreateHollowConeMesh(descriptor);
			_hollowConeDescriptor = descriptor;

			meshFilter.sharedMesh = _coneMesh;
			meshFilter.transform.localScale = new Vector3(1f, 1f, math.cos(halfAngleRadians));
		}

		private void Update()
		{
			cosTheta = math.cos(math.radians(halfAngleDegrees));
			
			_wasHit = RaySphereCappedCone(lightStaticTransform.Position, math.forward(lightStaticTransform.Rotation), 
				lightData.Range, cosTheta, rayOrigin, math.forward(rayDirection),
				out _hit0, out _hit1);
		}

		public static bool RaySphereCappedCone(float3 coneOrigin, float3 coneDirection, float range, float cosTheta,
			float3 rayOrigin, float3 rayDirection, out HitData hit0, out HitData hit1)
		{
			hit0 = default;
			hit1 = default;
			
			var cosSquared = math.square(cosTheta);
			var rimHeight = range * cosTheta;
			
			// Determinant = b^2 - 4ac
			// a = (rayDirection * coneDirection)^2 - cosSquared
			// b = 2 * ( (rayDirection * coneDirection) * ( (rayOrigin - coneOrigin) * coneDirection ) - rayDirection * (rayOrigin - coneOrigin) * cosSquared
			// c = ( (rayOrigin - coneOrigin) * coneDirection )^2 - (rayOrigin - coneOrigin) * (rayOrigin - coneOrigin) * cosSquared

			var rayToCone = rayOrigin - coneOrigin;
			var directionDot = math.dot(rayDirection, coneDirection);
			var rayToConeAxialDot = math.dot(rayToCone, coneDirection);
			var rayDirectionRayToCone = math.dot(rayDirection, rayToCone);
			var rayToConeLengthSquared = math.dot(rayToCone, rayToCone);

			var tNear = float.PositiveInfinity;
			var tFar = float.NegativeInfinity;
			
			// Cone surface
			{
				var a = math.square(directionDot) - cosSquared;
				var b = 2f * (directionDot * rayToConeAxialDot - rayDirectionRayToCone * cosSquared);
				var c = math.square(rayToConeAxialDot) - rayToConeLengthSquared * cosSquared;

				if (math.abs(a) < 1e-6f)
				{
					// Ray parallel to the cone surface: the quadratic collapses to linear.
					if (math.abs(b) > 1e-6f)
						Accumulate(-c / b, rayToConeAxialDot, directionDot, 0f, rimHeight, ref tNear, ref tFar);
				}
				else
				{
					var discriminant = math.square(b) - 4f * a * c;
					if (discriminant >= 0f)
					{
						var discriminantSqrt = math.sqrt(discriminant);
						var inverse2A = 0.5f / a;

						Accumulate((-b - discriminantSqrt) * inverse2A, rayToConeAxialDot, directionDot, 0f, rimHeight, ref tNear, ref tFar);
						Accumulate((-b + discriminantSqrt) * inverse2A, rayToConeAxialDot, directionDot, 0f, rimHeight, ref tNear, ref tFar);
					}
				}
			}

			// Spherical cap
			{
				var discriminant = math.square(rayDirectionRayToCone) - (rayToConeLengthSquared - math.square(range));

				if (discriminant >= 0f)
				{
					var discriminantSqrt = math.sqrt(discriminant);

					Accumulate(-rayDirectionRayToCone - discriminantSqrt, rayToConeAxialDot, directionDot, rimHeight, range, ref tNear, ref tFar);
					Accumulate(-rayDirectionRayToCone + discriminantSqrt, rayToConeAxialDot, directionDot, rimHeight, range, ref tNear, ref tFar);
				}
			}
			
			if (float.IsPositiveInfinity(tNear))
				return false;

			hit0 = new HitData
			{
				Position = rayOrigin + rayDirection * tNear,
				DistanceFromOrigin = tNear
			};

			hit1 = new HitData
			{
				Position = rayOrigin + rayDirection * tFar,
				DistanceFromOrigin = tFar
			};
			
			return true;
		}
		
		private static void Accumulate(float t, float axialBase, float axialSlope,
			float minHeight, float maxHeight, ref float tNear, ref float tFar)
		{
			if (t < 0f)
				return;

			// h = dot(P - coneOrigin, coneDirection), expanded so no vector math is needed.
			var height = axialBase + t * axialSlope;
			if (height < minHeight || height > maxHeight)
				return;

			tNear = math.min(tNear, t);
			tFar  = math.max(tFar, t);
		}

		private void OnDrawGizmosSelected()
		{
			var c = Gizmos.color;
			
			// Where the ray points
			Gizmos.color = Color.cornflowerBlue;
			Gizmos.DrawLine(rayOrigin, rayOrigin + math.forward(rayDirection) * rayLength);

			// Where the cone points
			Gizmos.color = Color.white;
			Gizmos.DrawLine(lightStaticTransform.Position, (float3)lightStaticTransform.Position + math.forward(lightStaticTransform.Rotation) * lightData.Range);

			if (_wasHit)
			{
				Gizmos.color = Color.greenYellow;
				Gizmos.DrawWireSphere(_hit0.Position, hitRadius);

				Gizmos.color = Color.yellowNice;
				Gizmos.DrawWireSphere(_hit1.Position, hitRadius);
			}

			Gizmos.color = c;

			if (_coneMesh == null)
				return;

			var bounds = _coneMesh.bounds;
			
			Gizmos.DrawWireCube(bounds.center, bounds.size);
		}
	}
}