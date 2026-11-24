using Saesentsessis.Fuluma.Data;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;

namespace Saesentsessis.Fuluma.Rendering.Culling
{
	/// <summary>
	/// Culls one pool against a camera and appends the dense index of every visible light to one index region per
	/// bucket: bucket 0 for lights the camera may be inside of (inside pass, always LOD0), bucket <c>1 + lod</c> for the
	/// rest (outside pass, LOD from the light's screen size). When the camera splits large lights off
	/// (<see cref="SpotLightCullingCamera.LargeBucketOffset"/>), large outside lights go to bucket
	/// <c>1 + LodCount + lod</c> instead.
	/// </summary>
	/// <remarks>
	/// Used when the camera has several LODs; with one, <see cref="SpotLightCullingFastJob"/> does the same without the
	/// LOD selection and with register-counted lists (inside, outside, large). Each batch collects its indices locally and
	/// reserves room in a region with one atomic add, so the regions fill in no particular order (fine: lights blend
	/// additively). Every region of the pool has room for the whole pool.
	/// <para>
	/// Shaped after its generated assembly (x64 SSE2/SSE4/AVX2 and ARMv8, see <c>docs/dev/CullingAssembly.md</c>):
	/// <list type="bullet">
	/// <item><c>SkipLocalsInit</c>: the stack lists were otherwise zeroed on every batch (9 KB of stores then; 17 KB
	/// since the large-light buckets).</item>
	/// <item>Planes in SoA <c>float4</c>s, near and far included, so each test is a few packed FMAs rather than scalar
	/// dot products with horizontal adds.</item>
	/// <item>The exact volume test runs on all six planes at once, without per-plane branches.</item>
	/// <item>Indices are appended without branches.</item>
	/// </list>
	/// </para>
	/// </remarks>
	// CompileSynchronously: the editor otherwise runs the managed version until Burst is ready (skews tests and timings).
	[BurstCompile(FloatMode = FloatMode.Fast, CompileSynchronously = true)]
	internal unsafe struct SpotLightCullingJob : IJobParallelForBatch
	{
		public const int BatchSize = 256;

		/// <summary>
		/// Inside bucket + two outside buckets per LOD (small and large lights).
		/// </summary>
		public const int MaxBucketCount = 1 + 2 * SpotLightCullingCamera.MaxLodCount;

		public const int InsideBucket = 0;

		[ReadOnly] public NativeArray<float4> Spheres;
		[ReadOnly] public NativeArray<SpotLightCullingData> Lights;

		public SpotLightCullingCamera Camera;

		/// <summary>
		/// Shared by both pools' jobs; each writes only within its own regions.
		/// </summary>
		[NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction]
		public NativeArray<uint> Indices;

		[NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction]
		public NativeArray<int> Counters;

		/// <summary>
		/// Start of this pool's bucket-0 region in <see cref="Indices"/>; bucket b starts at
		/// <c>RegionStart + b * RegionSize</c>.
		/// </summary>
		public int RegionStart;

		/// <summary>
		/// Size of each region: the pool's light count.
		/// </summary>
		public int RegionSize;

		/// <summary>
		/// This pool's bucket-0 counter in <see cref="Counters"/>; bucket b's is <c>CounterStart + b</c>.
		/// </summary>
		public int CounterStart;

		/// <summary>
		/// Times every batch. A field: Burst can't create a marker in a static constructor.
		/// </summary>
		public ProfilerMarker Marker;

		[SkipLocalsInit]
		public void Execute(int start, int count)
		{
			using var profilerScope = Marker.Auto();

			// One list of BatchSize per bucket, and their counts (only these few are cleared).
			var lists = stackalloc uint[MaxBucketCount * BatchSize];
			var counts = stackalloc int[MaxBucketCount];
			var bucketCount = 1 + Camera.LodCount + Camera.LargeBucketOffset;
			for (var b = 0; b < bucketCount; b++)
				counts[b] = 0;

			var spheres = (float4*)Spheres.GetUnsafeReadOnlyPtr();
			var lights = (SpotLightCullingData*)Lights.GetUnsafeReadOnlyPtr();
			var largeOffset = Camera.LargeBucketOffset;

			for (var i = start; i < start + count; i++)
			{
				var visibility = SpotLightCullingMath.Classify(spheres[i], lights[i], Camera, out var lod, out var large);

				// Culled lights are written to bucket 0 too, but its count doesn't move on. The large offset is 0 unless
				// the camera splits large lights off.
				var outsideBucket = 1 + lod + math.select(0, largeOffset, large);
				var bucket = visibility == SpotLightVisibility.Outside ? outsideBucket : InsideBucket;
				lists[bucket * BatchSize + counts[bucket]] = (uint)i;
				counts[bucket] += visibility == SpotLightVisibility.Culled ? 0 : 1;
			}

			for (var b = 0; b < bucketCount; b++)
				SpotLightCullingMath.Append(Indices, Counters, lists + b * BatchSize, counts[b], RegionStart + b * RegionSize, CounterStart + b);
		}
	}
}
