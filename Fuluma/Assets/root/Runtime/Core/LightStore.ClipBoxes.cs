using System;
using Saesentsessis.Fuluma.Core.Pools;
using Saesentsessis.Fuluma.Data;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace Saesentsessis.Fuluma.Core
{
	// Clip boxes: oriented boxes that limit where fake lights glow. A light references up to MaxClipBoxesPerLight boxes
	// of its store, each as Keep (the light exists only inside its keep boxes) or Subtract (removed inside the box).
	public partial class LightStore
	{
		/// <summary>
		/// Removes the references to <paramref name="removed"/> boxes from every light of <paramref name="pool"/>.
		/// </summary>
		private static void RemoveClipBoxReferences(ref SpotLightPool pool, NativeBitArray removed)
		{
			if (pool.Length == 0)
				return;

			using var changed = new NativeReference<int2>(Allocator.TempJob);
			new RemoveClipBoxReferencesJob
			{
				References = pool.ClipBoxes.AsArray(),
				Removed = removed,
				ChangedRange = changed
			}.Run();

			var range = changed.Value;
			if (range.x > range.y)
				return;

			pool.ClipBoxesDirty.Mark(range.x);
			pool.ClipBoxesDirty.Mark(range.y);
		}

		/// <summary>
		/// Live clip boxes, static and dynamic.
		/// </summary>
		public int ClipBoxCount => _clipBoxes.Count;

		/// <summary>
		/// Adds a clip box at a fixed placement. It moves only through <c>SetClipBoxPlacement</c>.
		/// </summary>
		/// <param name="placement">World position and rotation of the box's space.</param>
		/// <param name="shape">Center and size in that space.</param>
		/// <exception cref="InvalidOperationException">The store already holds <see cref="MaxClipBoxes"/> boxes.</exception>
		public ClipBoxHandle AddClipBox(in Placement placement, in ClipBoxData shape)
		{
			PrepareClipBoxAdd();

			PackingKernels.PlaceClipBox(shape, placement, out var matrix, out var packed);
			_clipBoxes.Add(shape, matrix, packed, false, out var slot);
			return new ClipBoxHandle(slot, _epoch, LightTransformMode.Static);
		}

		/// <summary>
		/// Adds a clip box at a fixed placement given as a matrix (any affine one: a <see cref="Transform"/>'s
		/// <c>localToWorldMatrix</c>, scale and shear included). It moves only through <c>SetClipBoxPlacement</c>.
		/// </summary>
		/// <param name="placement">Local-to-world matrix of the box's space.</param>
		/// <param name="shape">Center and size in that space.</param>
		/// <exception cref="InvalidOperationException">The store already holds <see cref="MaxClipBoxes"/> boxes.</exception>
		public ClipBoxHandle AddClipBox(float4x4 placement, in ClipBoxData shape)
		{
			PrepareClipBoxAdd();

			PackingKernels.PackClipBox(shape, placement, out var packed);
			_clipBoxes.Add(shape, placement, packed, false, out var slot);
			return new ClipBoxHandle(slot, _epoch, LightTransformMode.Static);
		}

		/// <summary>
		/// Adds a clip box that follows <paramref name="transform"/> (its full matrix, scale included) every frame.
		/// </summary>
		/// <remarks>Main thread only.</remarks>
		/// <param name="transform">Transform whose space the box lives in. The store keeps a reference until the box is
		/// removed.</param>
		/// <param name="shape">Center and size in the transform's space.</param>
		/// <exception cref="ArgumentNullException"><paramref name="transform"/> is null.</exception>
		/// <exception cref="InvalidOperationException">The store already holds <see cref="MaxClipBoxes"/> boxes.</exception>
		public ClipBoxHandle AddClipBox(Transform transform, in ClipBoxData shape)
		{
			if (transform == null)
				throw new ArgumentNullException(nameof(transform));

			PrepareClipBoxAdd();

			float4x4 localToWorld = transform.localToWorldMatrix;
			PackingKernels.PackClipBox(shape, localToWorld, out var packed);
			_clipBoxes.Add(shape, float4x4.identity, packed, true, out var slot);
			_dynamicClipBoxTransforms.Add(transform);
			return new ClipBoxHandle(slot, _epoch, LightTransformMode.Dynamic);
		}

		/// <summary>
		/// Whether the handle refers to a clip box that is still in this store.
		/// </summary>
		public bool Contains(in ClipBoxHandle handle)
		{
			return _allocated && handle.Epoch == _epoch && _clipBoxes.IsValid(handle.Slot)
			       && _clipBoxes.IsDynamic(handle.Slot.Index) == (handle.Mode == LightTransformMode.Dynamic);
		}

		/// <summary>
		/// Removes a clip box. Lights stop using it at once; their stored references to it are dropped later, in one pass
		/// for all removed boxes (before the next GPU upload of references or the next added box).
		/// </summary>
		/// <returns>False if the handle was already dead.</returns>
		public bool Remove(in ClipBoxHandle handle)
		{
			if (Contains(handle) == false)
				return false;

			CompleteClipBoxJobs();
			_clipBoxes.Remove(handle.Slot, out var removedDynamicDense);

			if (removedDynamicDense >= 0)
				_dynamicClipBoxTransforms.RemoveAtSwapBack(removedDynamicDense);

			// Without lights nothing references it; a light added later starts with no boxes.
			if (SpotLightCount > 0)
				_removedClipBoxes.Add(handle.Slot.Index);

			return true;
		}

		/// <summary>
		/// Replaces the center and size of a box of either kind.
		/// </summary>
		/// <returns>False if the handle is dead.</returns>
		public bool SetClipBoxShape(in ClipBoxHandle handle, in ClipBoxData shape)
		{
			if (Contains(handle) == false)
				return false;

			CompleteClipBoxJobs();

			var index = handle.Slot.Index;
			var placement = _clipBoxes.Placements[index];
			var packFrom = handle.Mode == LightTransformMode.Static
				? placement
				: (float4x4)_dynamicClipBoxTransforms[_clipBoxes.DynamicIndices[index]].localToWorldMatrix;
			PackingKernels.PackClipBox(shape, packFrom, out var packed);

			_clipBoxes.Set(index, shape, placement, packed);
			return true;
		}

		/// <summary>
		/// Moves a static clip box.
		/// </summary>
		/// <returns>False if the handle is dead or refers to a dynamic box, whose placement is owned by its
		/// <see cref="Transform"/>.</returns>
		public bool SetClipBoxPlacement(in ClipBoxHandle handle, in Placement placement)
		{
			if (handle.Mode != LightTransformMode.Static || Contains(handle) == false)
				return false;

			// The dynamic sync writes other entries of the same array.
			CompleteClipBoxJobs();

			var index = handle.Slot.Index;
			var shape = _clipBoxes.Shapes[index];
			PackingKernels.PlaceClipBox(shape, placement, out var matrix, out var packed);
			_clipBoxes.Set(index, shape, matrix, packed);
			return true;
		}

		/// <summary>
		/// Moves a static clip box to a placement given as a matrix (scale and shear included).
		/// </summary>
		/// <returns>False if the handle is dead or refers to a dynamic box, whose placement is owned by its
		/// <see cref="Transform"/>.</returns>
		public bool SetClipBoxPlacement(in ClipBoxHandle handle, float4x4 placement)
		{
			if (handle.Mode != LightTransformMode.Static || Contains(handle) == false)
				return false;

			// The dynamic sync writes other entries of the same array.
			CompleteClipBoxJobs();

			var index = handle.Slot.Index;
			var shape = _clipBoxes.Shapes[index];
			PackingKernels.PackClipBox(shape, placement, out var packed);
			_clipBoxes.Set(index, shape, placement, packed);
			return true;
		}

		/// <summary>
		/// Removes every clip box, and every light's references, invalidating all <see cref="ClipBoxHandle"/>s.
		/// </summary>
		public void ClearClipBoxes()
		{
			if (_allocated == false)
				return;

			CompleteClipBoxJobs();

			_clipBoxes.Clear();
			_removedClipBoxes.Clear();
			ClearTransforms(_dynamicClipBoxTransforms);

			// Culling reads the references (clip class partition).
			CompleteSpotLightReaders();
			_staticSpotLights.ClearClipBoxes();
			_dynamicSpotLights.ClearClipBoxes();
		}

		/// <summary>
		/// Allocates the store and gets the boxes ready for a new one, which may reuse a removed box's index.
		/// </summary>
		private void PrepareClipBoxAdd()
		{
			EnsureAllocated();
			CompleteClipBoxJobs();
			FlushRemovedClipBoxReferences();
		}

		/// <summary>
		/// <see cref="SetClipBoxes(in LightHandle, ReadOnlySpan{ClipBoxReference})"/> for a spot light.
		/// </summary>
		private bool SetSpotLightClipBoxes(in LightHandle light, ReadOnlySpan<ClipBoxReference> boxes)
		{
			if (boxes.Length > MaxClipBoxesPerLight)
				throw new ArgumentException($"A light uses at most {MaxClipBoxesPerLight} clip boxes, got {boxes.Length}.", nameof(boxes));

			if (TryGetSpotLightIndex(light, out var denseIndex) == false)
				return false;

			var packed = PackedClipBoxReferences.Empty;
			for (var i = 0; i < boxes.Length; i++)
			{
				if (Contains(boxes[i].Handle) == false)
					return false;

				packed[i] = PackedClipBoxReferences.Encode(boxes[i].Handle.Slot.Index, boxes[i].Role);
			}

			// Culling reads the references (clip class partition).
			CompleteSpotLightReaders();
			PoolOf(light.Mode).SetClipBoxes(denseIndex, packed);
			return true;
		}

		/// <summary>
		/// <see cref="TryGetClipBoxes"/> for a spot light.
		/// </summary>
		private bool TryGetSpotLightClipBoxes(in LightHandle light, Span<ClipBoxReference> output, out int count)
		{
			count = 0;

			if (TryGetSpotLightIndex(light, out var denseIndex) == false)
				return false;

			var packed = PoolOf(light.Mode).ClipBoxes[denseIndex];
			for (var slot = 0; slot < PackedClipBoxReferences.Count && count < output.Length; slot++)
			{
				if (PackedClipBoxReferences.TryDecode(packed[slot], out var index, out var role) == false)
					break;

				// A removed box not yet dropped from the lights (its index can't be reused before that).
				if (_clipBoxes.IsAlive(index) == false)
					continue;

				var mode = _clipBoxes.IsDynamic(index) ? LightTransformMode.Dynamic : LightTransformMode.Static;
				output[count++] = new ClipBoxReference(new ClipBoxHandle(_clipBoxes.SlotOf(index), _epoch, mode), role);
			}

			return true;
		}

		/// <summary>
		/// Drops every light's references to the boxes removed since the last flush, in one pass per pool, so a box
		/// reusing one of their indices can't clip those lights.
		/// </summary>
		private void FlushRemovedClipBoxReferences()
		{
			if (_allocated == false || _removedClipBoxes.Length == 0)
				return;

			using var removed = new NativeBitArray(math.max(_clipBoxes.IndexCount, 1), Allocator.TempJob);
			foreach (var index in _removedClipBoxes)
				removed.Set(index, true);

			_removedClipBoxes.Clear();

			CompleteSpotLightReaders();
			RemoveClipBoxReferences(ref _staticSpotLights, removed);
			RemoveClipBoxReferences(ref _dynamicSpotLights, removed);
		}

		private void AllocateClipBoxes()
		{
			_clipBoxes = new ClipBoxArray(InitialClipBoxCapacity, StoreAllocator);
			_dynamicClipBoxTransforms = new TransformAccessArray(InitialClipBoxCapacity);
			_removedClipBoxes = new NativeList<int>(StoreAllocator);
		}

		private void ReleaseClipBoxes()
		{
			CompleteClipBoxJobs();

			_clipBoxes.Dispose();
			_removedClipBoxes.Dispose();
			_removedClipBoxes = default;
			DisposeTransforms(ref _dynamicClipBoxTransforms);
		}
	}
}
