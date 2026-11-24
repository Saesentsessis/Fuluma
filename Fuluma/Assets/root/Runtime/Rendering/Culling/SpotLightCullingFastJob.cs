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
	/// <see cref="SpotLightCullingJob"/> for a camera with a single proxy LOD (LODs turned off, or a one-LOD descriptor):
	/// no LOD selection, and lists counted in registers instead of the bucket scatter. Writes bucket 0 (inside) and
	/// bucket 1 (outside, LOD0) with the same region layout, and bucket 2 (large outside lights) when the camera splits
	/// them off.
	/// </summary>
	// CompileSynchronously: the editor otherwise runs the managed version until Burst is ready (skews tests and timings).
	[BurstCompile(FloatMode = FloatMode.Fast, CompileSynchronously = true)]
	internal unsafe struct SpotLightCullingFastJob : IJobParallelForBatch
	{
		public const int BatchSize = SpotLightCullingJob.BatchSize;

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
		/// Start of this pool's inside region in <see cref="Indices"/>; its outside region follows at
		/// <c>RegionStart + RegionSize</c>, and the large-light region at <c>RegionStart + 2 * RegionSize</c>.
		/// </summary>
		public int RegionStart;

		/// <summary>
		/// Size of each region: the pool's light count.
		/// </summary>
		public int RegionSize;

		/// <summary>
		/// This pool's inside counter in <see cref="Counters"/>; the outside one is <c>CounterStart + 1</c>, the large one
		/// <c>CounterStart + 2</c>.
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

			var inside = stackalloc uint[BatchSize];
			var outside = stackalloc uint[BatchSize];
			var large = stackalloc uint[BatchSize];
			var insideCount = 0;
			var outsideCount = 0;
			var largeCount = 0;

			var spheres = (float4*)Spheres.GetUnsafeReadOnlyPtr();
			var lights = (SpotLightCullingData*)Lights.GetUnsafeReadOnlyPtr();
			var splitLarge = Camera.LargeBucketOffset != 0;

			for (var i = start; i < start + count; i++)
			{
				var visibility = SpotLightCullingMath.ClassifyLod0(spheres[i], lights[i], Camera);
				var isOutside = visibility == SpotLightVisibility.Outside;

				// The same for every light of the camera, so the branch is predictable.
				var isLarge = splitLarge && SpotLightCullingMath.IsLarge(spheres[i], Camera);

				// Every slot is written; only the matching count moves on.
				inside[insideCount] = (uint)i;
				outside[outsideCount] = (uint)i;
				large[largeCount] = (uint)i;
				insideCount += visibility == SpotLightVisibility.Inside ? 1 : 0;
				outsideCount += isOutside && isLarge == false ? 1 : 0;
				largeCount += isOutside && isLarge ? 1 : 0;
			}

			SpotLightCullingMath.Append(Indices, Counters, inside, insideCount, RegionStart, CounterStart);
			SpotLightCullingMath.Append(Indices, Counters, outside, outsideCount, RegionStart + RegionSize, CounterStart + 1);
			SpotLightCullingMath.Append(Indices, Counters, large, largeCount, RegionStart + 2 * RegionSize, CounterStart + 2);
		}
	}
}
