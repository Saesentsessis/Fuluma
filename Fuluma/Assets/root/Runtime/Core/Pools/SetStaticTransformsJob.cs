using Saesentsessis.Fuluma.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.Specialized;
using Unity.Jobs;
using Unity.Mathematics;

namespace Saesentsessis.Fuluma.Core.Pools
{
	/// <summary>
	/// Moves many static spot lights at once: resolves each handle to its dense index, packs the transform and
	/// refreshes the culling data, as <see cref="SpotLightPool.SetTransform"/> does for one light.
	/// </summary>
	/// <remarks>
	/// Each batch writes its lowest and highest moved dense index and its moved count to <see cref="BatchResults"/>
	/// (<c>int3(min, max, count)</c>); the caller reduces them into the dirty range. Handles that are dead, from another
	/// epoch or not static spot lights are skipped. A light listed twice gets one of its placements (no defined order).
	/// </remarks>
	[BurstCompile(FloatMode = FloatMode.Fast)]
	internal struct SetStaticTransformsJob : IJobParallelForBatch
	{
		public const int BatchSize = 256;

		[ReadOnly] public NativeArray<LightHandle> Handles;
		[ReadOnly] public NativeArray<Placement> Placements;

		/// <summary>
		/// The static pool's allocator, only read (handle → dense index).
		/// </summary>
		[ReadOnly] public UnsafeSlotAllocator Allocator;

		public uint Epoch;

		[NativeDisableParallelForRestriction] public NativeArray<PackedTransformData> Transforms;
		[NativeDisableParallelForRestriction] public NativeArray<SpotLightCullingData> CullingData;
		[NativeDisableParallelForRestriction] public NativeArray<float4> CullingSpheres;

		/// <summary>
		/// One entry per batch: <c>int3(min dense index, max dense index, moved count)</c>.
		/// </summary>
		[NativeDisableParallelForRestriction] public NativeArray<int3> BatchResults;

		public void Execute(int start, int count)
		{
			var lowest = int.MaxValue;
			var highest = -1;
			var moved = 0;

			for (var i = start; i < start + count; i++)
			{
				if (TryGetDenseIndex(Handles[i], out var dense) == false)
					continue;

				var placement = Placements[i];
				Transforms[dense] = placement.Pack();

				var culling = CullingData[dense];
				culling.Place(placement.Position, placement.Rotation);
				CullingData[dense] = culling;
				CullingSpheres[dense] = culling.BoundingSphere();

				lowest = math.min(lowest, dense);
				highest = math.max(highest, dense);
				moved++;
			}

			BatchResults[start / BatchSize] = new int3(lowest, highest, moved);
		}

		private bool TryGetDenseIndex(in LightHandle handle, out int dense)
		{
			dense = -1;
			return handle.Epoch == Epoch && handle.Type == FakeLightType.Spot && handle.Mode == LightTransformMode.Static
			       && SpotLightPool.TryGetDenseIndex(Allocator, handle.Slot, out dense);
		}
	}
}
