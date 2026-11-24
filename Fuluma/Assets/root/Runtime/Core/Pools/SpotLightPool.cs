using System;
using Saesentsessis.Fuluma.Data;
using Unity.Collections;
using Unity.Collections.Specialized;
using Unity.Mathematics;

namespace Saesentsessis.Fuluma.Core.Pools
{
	/// <summary>
	/// Dense storage for one transform mode of fake spot lights.
	/// </summary>
	/// <remarks>
	/// Every list shares the dense index: element <c>i</c> of <see cref="Transforms"/>, <see cref="Lights"/>,
	/// <see cref="CullingSpheres"/>, <see cref="CullingData"/> and <see cref="ClipBoxes"/> always belongs to the same
	/// light, so the GPU needs no indirection. Removal is swap-back in the allocator and in every list, which keeps them
	/// aligned. Anything else that mirrors the dense order (a <c>TransformAccessArray</c>) must apply the same swap-back
	/// with the index returned from <see cref="Remove"/>.
	/// <para>
	/// The two culling lists are CPU-only. They hold the light unpacked (world direction instead of 16:16 polar angles)
	/// plus its bounding sphere, kept up to date by every change, so culling reads them as they are.
	/// </para>
	/// </remarks>
	internal struct SpotLightPool : IDisposable
	{
		/// <summary>
		/// Dense index of a live slot of <paramref name="allocator"/>; shared by the pool and the jobs that resolve
		/// handles on their own.
		/// </summary>
		public static unsafe bool TryGetDenseIndex(in UnsafeSlotAllocator allocator, in StableIndexHandle slot,
			out int denseIndex)
		{
			if (allocator.IsCreated == false || allocator.IsValid(slot.Index, slot.Version) == false)
			{
				denseIndex = -1;
				return false;
			}

			denseIndex = allocator.IndicesPtr[slot.Index];
			return true;
		}

		public UnsafeSlotAllocator Allocator;
		public NativeList<PackedTransformData> Transforms;
		public NativeList<PackedSpotLightData> Lights;

		/// <summary>
		/// Bounding sphere of each light's volume: center (xyz), radius (w).
		/// </summary>
		public NativeList<float4> CullingSpheres;

		public NativeList<SpotLightCullingData> CullingData;

		/// <summary>
		/// Each light's clip box references (<see cref="PackedClipBoxReferences.Empty"/> for a light without boxes).
		/// </summary>
		public NativeList<PackedClipBoxReferences> ClipBoxes;

		/// <summary>
		/// Indices whose transform changed since the GPU mirror last consumed them.
		/// </summary>
		public DirtyRange TransformsDirty;

		/// <summary>
		/// Indices whose light data changed since the GPU mirror last consumed them.
		/// </summary>
		public DirtyRange LightsDirty;

		/// <summary>
		/// Indices whose clip box references changed since the GPU mirror last consumed them.
		/// </summary>
		public DirtyRange ClipBoxesDirty;

		public SpotLightPool(int initialCapacity, AllocatorManager.AllocatorHandle allocator)
		{
			Allocator = new UnsafeSlotAllocator(initialCapacity, allocator);
			Transforms = new NativeList<PackedTransformData>(initialCapacity, allocator);
			Lights = new NativeList<PackedSpotLightData>(initialCapacity, allocator);
			CullingSpheres = new NativeList<float4>(initialCapacity, allocator);
			CullingData = new NativeList<SpotLightCullingData>(initialCapacity, allocator);
			ClipBoxes = new NativeList<PackedClipBoxReferences>(initialCapacity, allocator);
			TransformsDirty = default;
			LightsDirty = default;
			ClipBoxesDirty = default;
		}

		public readonly bool IsCreated => Allocator.IsCreated;

		public readonly int Length => Allocator.IsCreated ? Allocator.Length : 0;

		public readonly int Capacity => Allocator.IsCreated ? Allocator.Capacity : 0;

		/// <summary>
		/// Appends a light, without clip boxes.
		/// </summary>
		/// <returns>Dense index of the new light.</returns>
		public int Add(in Placement placement, in SpotLightData light, out StableIndexHandle slot)
		{
			// Allocate returns the sparse ID; the new light always takes the next dense index.
			var denseIndex = Allocator.Length;
			var id = Allocator.Allocate(out var version);

			PackingKernels.PlaceNewSpotLight(placement, light, out var packed, out var transform, out var culling, out var sphere);
			Transforms.Add(transform);
			Lights.Add(packed);
			CullingData.Add(culling);
			CullingSpheres.Add(sphere);
			ClipBoxes.Add(PackedClipBoxReferences.Empty);

			TransformsDirty.Mark(denseIndex);
			LightsDirty.Mark(denseIndex);
			ClipBoxesDirty.Mark(denseIndex);

			slot = new StableIndexHandle(id, version);
			return denseIndex;
		}

		public readonly bool TryGetDenseIndex(in StableIndexHandle slot, out int denseIndex)
			=> TryGetDenseIndex(Allocator, slot, out denseIndex);

		/// <summary>
		/// Removes a light by swapping the last one into its place.
		/// </summary>
		/// <param name="removedDense">Dense index that was vacated and refilled by the last light.</param>
		public bool Remove(in StableIndexHandle slot, out int removedDense)
		{
			removedDense = -1;

			if (Allocator.IsCreated == false || Allocator.Release(slot.Index, slot.Version, out removedDense, out _) == false)
				return false;

			Transforms.RemoveAtSwapBack(removedDense);
			Lights.RemoveAtSwapBack(removedDense);
			CullingSpheres.RemoveAtSwapBack(removedDense);
			CullingData.RemoveAtSwapBack(removedDense);
			ClipBoxes.RemoveAtSwapBack(removedDense);

			// The last light moved into the vacated slot; removing the last light itself changes only the count.
			if (removedDense < Lights.Length)
			{
				TransformsDirty.Mark(removedDense);
				LightsDirty.Mark(removedDense);
				ClipBoxesDirty.Mark(removedDense);
			}

			return true;
		}

		public void SetTransform(int denseIndex, in Placement placement)
		{
			var culling = CullingData[denseIndex];
			PackingKernels.PlaceSpotLight(placement, ref culling, out var transform, out var sphere);

			Transforms[denseIndex] = transform;
			TransformsDirty.Mark(denseIndex);
			CullingData[denseIndex] = culling;
			CullingSpheres[denseIndex] = sphere;
		}

		public void SetLight(int denseIndex, in SpotLightData light)
		{
			var culling = CullingData[denseIndex];
			PackingKernels.ReshapeSpotLight(light, ref culling, out var packed, out var sphere);

			Lights[denseIndex] = packed;
			LightsDirty.Mark(denseIndex);
			CullingData[denseIndex] = culling;
			CullingSpheres[denseIndex] = sphere;
		}

		public void SetClipBoxes(int denseIndex, in PackedClipBoxReferences references)
		{
			ClipBoxes[denseIndex] = references;
			ClipBoxesDirty.Mark(denseIndex);
		}

		/// <summary>
		/// Takes every light's clip boxes away (all boxes of the store are gone).
		/// </summary>
		public void ClearClipBoxes()
		{
			if (ClipBoxes.Length == 0)
				return;

			for (var i = 0; i < ClipBoxes.Length; i++)
				ClipBoxes[i] = PackedClipBoxReferences.Empty;

			ClipBoxesDirty.Mark(0);
			ClipBoxesDirty.Mark(ClipBoxes.Length - 1);
		}

		/// <summary>
		/// Removes every light. Previously issued slots fail validation afterwards.
		/// </summary>
		public void Clear()
		{
			if (Allocator.IsCreated == false)
				return;

			Allocator.Clear();
			Transforms.Clear();
			Lights.Clear();
			CullingSpheres.Clear();
			CullingData.Clear();
			ClipBoxes.Clear();

			// Nothing left to upload; the count alone tells the mirror.
			TransformsDirty.Reset();
			LightsDirty.Reset();
			ClipBoxesDirty.Reset();
		}

		public void Dispose()
		{
			// Every list is created with the allocator.
			if (Allocator.IsCreated == false)
				return;

			Allocator.Dispose();
			Transforms.Dispose();
			Lights.Dispose();
			CullingSpheres.Dispose();
			CullingData.Dispose();
			ClipBoxes.Dispose();
			this = default;
		}
	}
}
