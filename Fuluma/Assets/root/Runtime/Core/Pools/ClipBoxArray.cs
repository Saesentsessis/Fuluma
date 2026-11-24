using System;
using Saesentsessis.Fuluma.Data;
using Unity.Collections;
using Unity.Collections.Specialized;
using Unity.Mathematics;

namespace Saesentsessis.Fuluma.Core.Pools
{
	/// <summary>
	/// A store's clip boxes, static and dynamic together, indexed by box index: the stable slot ID the allocator hands
	/// out, which is also what lights reference. Unlike the light pools nothing is compacted, so an index never changes
	/// while its box lives; removed boxes leave holes that later boxes reuse.
	/// </summary>
	/// <remarks>
	/// The per-index lists (<see cref="Boxes"/>, <see cref="Shapes"/>, <see cref="Placements"/>,
	/// <see cref="DynamicIndices"/>) grow to cover the highest index handed out. Dynamic boxes are also listed densely in
	/// <see cref="DynamicBoxes"/>, which a <c>TransformAccessArray</c> in the store mirrors with the same swap-back.
	/// </remarks>
	internal struct ClipBoxArray : IDisposable
	{
		/// <summary>
		/// Most boxes per store: a light's reference holds a 15-bit index, and index 0x7FFF is reserved for "none"
		/// (with this many live boxes at most, the allocator never hands it out).
		/// </summary>
		public const int MaxBoxes = PackedClipBoxReferences.MaxBoxIndex + 1;

		public UnsafeSlotAllocator Allocator;

		/// <summary>
		/// GPU form of every box, by box index. Holes hold <see cref="PackedClipBoxData.Empty"/>.
		/// </summary>
		public NativeList<PackedClipBoxData> Boxes;

		/// <summary>
		/// Authoring shape of every box, by box index (the dynamic sync packs from it).
		/// </summary>
		public NativeList<ClipBoxData> Shapes;

		/// <summary>
		/// Local-to-world placement of every static box, by box index (unused for dynamic boxes).
		/// </summary>
		public NativeList<float4x4> Placements;

		/// <summary>
		/// Dense index in <see cref="DynamicBoxes"/> by box index; -1 for static boxes.
		/// </summary>
		public NativeList<int> DynamicIndices;

		/// <summary>
		/// Box index of every dynamic box, in the dense order of the store's <c>TransformAccessArray</c>.
		/// </summary>
		public NativeList<int> DynamicBoxes;

		/// <summary>
		/// Box indices whose packed box changed since the GPU mirror last consumed them.
		/// </summary>
		public DirtyRange BoxesDirty;

		public ClipBoxArray(int initialCapacity, AllocatorManager.AllocatorHandle allocator)
		{
			Allocator = new UnsafeSlotAllocator(initialCapacity, allocator);
			Boxes = new NativeList<PackedClipBoxData>(initialCapacity, allocator);
			Shapes = new NativeList<ClipBoxData>(initialCapacity, allocator);
			Placements = new NativeList<float4x4>(initialCapacity, allocator);
			DynamicIndices = new NativeList<int>(initialCapacity, allocator);
			DynamicBoxes = new NativeList<int>(allocator);
			BoxesDirty = default;
		}

		public readonly bool IsCreated => Allocator.IsCreated;

		/// <summary>
		/// Live boxes.
		/// </summary>
		public readonly int Count => Allocator.IsCreated ? Allocator.Length : 0;

		/// <summary>
		/// Length of the per-index lists: every live box index is below it. Read from <see cref="DynamicIndices"/>, which
		/// no job touches, so it stays readable while the dynamic box sync writes <see cref="Boxes"/>.
		/// </summary>
		public readonly int IndexCount => DynamicIndices.IsCreated ? DynamicIndices.Length : 0;

		/// <summary>
		/// Adds a box.
		/// </summary>
		/// <param name="placement">Static placement; ignored (and not kept) for a dynamic box.</param>
		/// <param name="packed">The box's GPU form now (for a dynamic box, from its transform at this moment).</param>
		/// <returns>The box index.</returns>
		/// <exception cref="InvalidOperationException">The store already has <see cref="MaxBoxes"/> boxes.</exception>
		public int Add(in ClipBoxData shape, in float4x4 placement, in PackedClipBoxData packed, bool dynamic,
			out StableIndexHandle slot)
		{
			if (Count >= MaxBoxes)
				throw new InvalidOperationException($"A light store holds at most {MaxBoxes} clip boxes.");

			var index = Allocator.Allocate(out var version);
			var dynamicIndex = dynamic ? DynamicBoxes.Length : -1;

			if (index == Boxes.Length)
			{
				// A new index (no hole to reuse): append, instead of growing the lists and then overwriting.
				Boxes.Add(packed);
				Shapes.Add(shape);
				Placements.Add(placement);
				DynamicIndices.Add(dynamicIndex);
			}
			else
			{
				EnsureIndex(index);
				Boxes[index] = packed;
				Shapes[index] = shape;
				Placements[index] = placement;
				DynamicIndices[index] = dynamicIndex;
			}

			if (dynamic)
				DynamicBoxes.Add(index);

			BoxesDirty.Mark(index);
			slot = new StableIndexHandle(index, version);
			return index;
		}

		public readonly bool IsValid(in StableIndexHandle slot)
			=> Allocator.IsCreated && Allocator.IsValid(slot.Index, slot.Version);

		/// <summary>
		/// Whether a box lives at <paramref name="index"/> (false for a hole or an index past the allocator's range).
		/// </summary>
		public readonly unsafe bool IsAlive(int index)
			=> (uint)index < (uint)Allocator.Capacity && Allocator.IndicesPtr[index] < Allocator.Length;

		public readonly bool IsDynamic(int index) => DynamicIndices[index] >= 0;

		/// <summary>
		/// Handle of the live box at <paramref name="index"/>.
		/// </summary>
		public readonly unsafe StableIndexHandle SlotOf(int index)
			=> new(index, Allocator.MetaPtr[Allocator.IndicesPtr[index]].Version);

		/// <summary>
		/// Removes a box; its index becomes a hole that a later box reuses.
		/// </summary>
		/// <param name="removedDynamicDense">The dense dynamic index that was vacated and refilled by the last dynamic
		/// box (apply the same swap-back to the <c>TransformAccessArray</c>); -1 for a static box.</param>
		public bool Remove(in StableIndexHandle slot, out int removedDynamicDense)
		{
			removedDynamicDense = -1;

			if (IsValid(slot) == false || Allocator.Release(slot.Index, slot.Version, out _, out _) == false)
				return false;

			var index = slot.Index;
			var dense = DynamicIndices[index];

			if (dense >= 0)
			{
				DynamicBoxes.RemoveAtSwapBack(dense);
				if (dense < DynamicBoxes.Length)
					DynamicIndices[DynamicBoxes[dense]] = dense;

				removedDynamicDense = dense;
			}

			DynamicIndices[index] = -1;
			Boxes[index] = PackedClipBoxData.Empty;
			BoxesDirty.Mark(index);
			return true;
		}

		/// <summary>
		/// Replaces a box's GPU form (and shape, and for a static box its placement).
		/// </summary>
		public void Set(int index, in ClipBoxData shape, in float4x4 placement, in PackedClipBoxData packed)
		{
			Boxes[index] = packed;
			Shapes[index] = shape;
			Placements[index] = placement;
			BoxesDirty.Mark(index);
		}

		/// <summary>
		/// Removes every box. Previously issued slots fail validation afterwards.
		/// </summary>
		public void Clear()
		{
			if (Allocator.IsCreated == false)
				return;

			Allocator.Clear();
			Boxes.Clear();
			Shapes.Clear();
			Placements.Clear();
			DynamicIndices.Clear();
			DynamicBoxes.Clear();
			BoxesDirty.Reset();
		}

		public void Dispose()
		{
			if (Allocator.IsCreated)
				Allocator.Dispose();

			if (Boxes.IsCreated)
				Boxes.Dispose();

			if (Shapes.IsCreated)
				Shapes.Dispose();

			if (Placements.IsCreated)
				Placements.Dispose();

			if (DynamicIndices.IsCreated)
				DynamicIndices.Dispose();

			if (DynamicBoxes.IsCreated)
				DynamicBoxes.Dispose();

			this = default;
		}

		/// <summary>
		/// Grows the per-index lists to cover <paramref name="index"/>, filling new entries as holes.
		/// </summary>
		private void EnsureIndex(int index)
		{
			var oldLength = Boxes.Length;
			if (index < oldLength)
				return;

			var newLength = index + 1;
			Boxes.Resize(newLength, NativeArrayOptions.UninitializedMemory);
			Shapes.Resize(newLength, NativeArrayOptions.ClearMemory);
			Placements.Resize(newLength, NativeArrayOptions.ClearMemory);
			DynamicIndices.Resize(newLength, NativeArrayOptions.UninitializedMemory);

			for (var i = oldLength; i < newLength; i++)
			{
				Boxes[i] = PackedClipBoxData.Empty;
				DynamicIndices[i] = -1;
			}
		}
	}
}
