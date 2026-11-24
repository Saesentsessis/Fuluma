using Saesentsessis.Fuluma.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Jobs;

namespace Saesentsessis.Fuluma.Core.Pools
{
	/// <summary>
	/// Reads every tracked <see cref="UnityEngine.Transform"/> and writes its packed form into the dense transform list,
	/// and its position and axis into the culling lists (with a new bounding sphere).
	/// </summary>
	/// <remarks>
	/// Runs over all dynamic lights every frame, moved or not: checking what moved costs more than copying.
	/// The <c>TransformAccessArray</c> mirrors the dense order of the pool, so <c>index</c> is the dense index.
	/// </remarks>
	[BurstCompile]
	internal struct PackDynamicTransformsJob : IJobParallelForTransform
	{
		[WriteOnly] public NativeArray<PackedTransformData> Output;
		[WriteOnly] public NativeArray<float4> CullingSpheres;

		/// <summary>
		/// Range and cone angle are kept; position and axis are replaced.
		/// </summary>
		public NativeArray<SpotLightCullingData> CullingData;

		public void Execute(int index, TransformAccess transform)
		{
			if (transform.isValid == false)
				return;

			transform.GetPositionAndRotation(out var position, out var rotation);

			Output[index] = new Placement
			{
				Position = position,
				Rotation = rotation
			}.Pack();

			var culling = CullingData[index];
			culling.Place(position, rotation);
			CullingData[index] = culling;
			CullingSpheres[index] = culling.BoundingSphere();
		}
	}
}
