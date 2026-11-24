using Saesentsessis.Fuluma.Data;
using Unity.Burst;
using Unity.Collections;
using UnityEngine.Jobs;

namespace Saesentsessis.Fuluma.Core.Pools
{
	/// <summary>
	/// Re-packs every dynamic clip box from its <see cref="UnityEngine.Transform"/>'s full local-to-world matrix (scale
	/// and shear included). <c>index</c> is the dense dynamic index; the box index it writes comes from
	/// <see cref="DynamicBoxes"/>.
	/// </summary>
	/// <remarks>
	/// Runs over all dynamic boxes every frame, moved or not, like the dynamic lights: checking what moved costs more
	/// than packing.
	/// </remarks>
	[BurstCompile]
	internal struct PackDynamicClipBoxesJob : IJobParallelForTransform
	{
		[ReadOnly] public NativeArray<int> DynamicBoxes;
		[ReadOnly] public NativeArray<ClipBoxData> Shapes;

		/// <summary>
		/// By box index; each dense index writes a different box, so parallel writes never collide.
		/// </summary>
		[NativeDisableParallelForRestriction, WriteOnly] public NativeArray<PackedClipBoxData> Boxes;

		public void Execute(int index, TransformAccess transform)
		{
			if (transform.isValid == false)
				return;

			var box = DynamicBoxes[index];
			Boxes[box] = Shapes[box].Pack(transform.localToWorldMatrix);
		}
	}
}
