using Saesentsessis.Fuluma.Data;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Saesentsessis.Fuluma.Core
{
	// What the GPU mirror and the culler read: the pools' arrays and dirty ranges, the boxes and the references.
	public partial class LightStore
	{
		/// <summary>
		/// Whether the store has boxes that follow a <see cref="UnityEngine.Transform"/> (re-packed every frame).
		/// </summary>
		internal bool HasDynamicClipBoxes => _allocated && _clipBoxes.DynamicBoxes.Length > 0;

		/// <summary>
		/// Box indices in use, holes included: the length of <see cref="GetClipBoxes"/>.
		/// </summary>
		internal int ClipBoxIndexCount => _clipBoxes.IndexCount;

		/// <summary>
		/// Removed boxes still waiting to be dropped from the lights' references (for tests).
		/// </summary>
		internal int PendingClipBoxRemovals => _allocated ? _removedClipBoxes.Length : 0;

		/// <summary>
		/// Packed boxes by box index (holes included). Complete <see cref="CompleteClipBoxJobs"/> first.
		/// </summary>
		internal NativeArray<PackedClipBoxData> GetClipBoxes() => _allocated ? _clipBoxes.Boxes.AsArray() : default;

		/// <summary>
		/// One pool's packed transforms, by dense index. For the dynamic pool, written by the transform sync.
		/// </summary>
		internal NativeArray<PackedTransformData> GetSpotLightTransforms(LightTransformMode mode)
			=> _allocated ? PoolOf(mode).Transforms.AsArray() : default;

		/// <summary>
		/// One pool's packed light data, by dense index, index-aligned with <see cref="GetSpotLightTransforms"/>.
		/// </summary>
		internal NativeArray<PackedSpotLightData> GetSpotLights(LightTransformMode mode)
			=> _allocated ? PoolOf(mode).Lights.AsArray() : default;

		/// <summary>
		/// Hands the changed index ranges of one pool to the GPU mirror and resets them. The store has a single GPU
		/// mirror, so consuming here is safe. Dynamic transforms are re-uploaded every frame, so their range is consumed
		/// but meaningless.
		/// </summary>
		internal void ConsumeSpotLightDirtyRanges(LightTransformMode mode, out BufferRange transforms, out BufferRange lights)
		{
			ref var pool = ref PoolOf(mode);
			transforms = pool.TransformsDirty.Consume(pool.Length);
			lights = pool.LightsDirty.Consume(pool.Length);
		}

		/// <summary>
		/// Culling input of one pool: bounding spheres and unpacked lights, index-aligned with the GPU buffers. For the
		/// dynamic pool, read them only after <paramref name="dependency"/> (this frame's transform sync).
		/// Register the reading job with <see cref="AddSpotLightReader"/>.
		/// </summary>
		internal void GetSpotLightCullingInput(LightTransformMode mode, out NativeArray<float4> spheres,
			out NativeArray<SpotLightCullingData> lights, out JobHandle dependency)
		{
			ref var pool = ref PoolOf(mode);
			spheres = _allocated ? pool.CullingSpheres.AsArray() : default;
			lights = _allocated ? pool.CullingData.AsArray() : default;
			dependency = mode == LightTransformMode.Static ? default : _dynamicSpotLightSync;
		}

		/// <summary>
		/// One pool's clip box references, index-aligned with its lights. References to removed boxes are dropped first.
		/// </summary>
		internal NativeArray<PackedClipBoxReferences> GetSpotLightClipBoxReferences(LightTransformMode mode)
		{
			FlushRemovedClipBoxReferences();
			return _allocated ? PoolOf(mode).ClipBoxes.AsArray() : default;
		}

		/// <summary>
		/// Hands one pool's changed reference range to the GPU mirror and resets it. References to removed boxes are
		/// dropped first.
		/// </summary>
		internal BufferRange ConsumeSpotLightClipBoxDirtyRange(LightTransformMode mode)
		{
			FlushRemovedClipBoxReferences();

			ref var pool = ref PoolOf(mode);
			return pool.ClipBoxesDirty.Consume(pool.Length);
		}

		/// <summary>
		/// Hands the changed box index range to the GPU mirror and resets it.
		/// </summary>
		internal BufferRange ConsumeClipBoxDirtyRange() => _clipBoxes.BoxesDirty.Consume(_clipBoxes.IndexCount);
	}
}
