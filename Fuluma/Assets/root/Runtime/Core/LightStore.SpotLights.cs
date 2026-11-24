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
	// Spot lights: adding, moving, changing and removing them.
	public partial class LightStore
	{
		/// <summary>
		/// Number of fake spot lights registered for rendering, in both pools.
		/// </summary>
		public int SpotLightCount => _staticSpotLights.Length + _dynamicSpotLights.Length;

		/// <summary>
		/// Number of static fake spot lights (transform set from code).
		/// </summary>
		public int StaticSpotLightCount => _staticSpotLights.Length;

		/// <summary>
		/// Number of dynamic fake spot lights (transform read from a <see cref="Transform"/> every frame).
		/// </summary>
		public int DynamicSpotLightCount => _dynamicSpotLights.Length;

		/// <summary>
		/// Lights the static pool holds before it grows. 0 until the store allocates.
		/// </summary>
		internal int StaticSpotLightCapacity => _staticSpotLights.Capacity;

		/// <summary>
		/// Lights the dynamic pool holds before it grows. 0 until the store allocates.
		/// </summary>
		internal int DynamicSpotLightCapacity => _dynamicSpotLights.Capacity;

		/// <summary>
		/// Adds a static fake spot light. Its placement changes only through <see cref="SetSpotLightPlacement"/>.
		/// </summary>
		/// <param name="placement">Initial world-space placement.</param>
		/// <param name="light">Raw fake spot light data.</param>
		/// <returns>Handle to update/remove the added light.</returns>
		public LightHandle AddSpotLight(in Placement placement, in SpotLightData light)
		{
			EnsureAllocated();
			CompleteSpotLightJobs(LightTransformMode.Static);

			_staticSpotLights.Add(placement, light, out var slot);
			return new LightHandle(slot, _epoch, FakeLightType.Spot, LightTransformMode.Static);
		}

		/// <summary>
		/// Adds a dynamic fake spot light that follows <paramref name="transform"/> every frame.
		/// </summary>
		/// <remarks>Main thread only.</remarks>
		/// <param name="transform">Transform to follow. The store keeps a reference until the light is removed.</param>
		/// <param name="light">Raw fake spot light data.</param>
		/// <returns>Handle to update/remove the added light.</returns>
		/// <exception cref="ArgumentNullException"><paramref name="transform"/> is null.</exception>
		public LightHandle AddSpotLight(Transform transform, in SpotLightData light)
		{
			if (transform == null)
				throw new ArgumentNullException(nameof(transform));

			EnsureAllocated();
			CompleteSpotLightJobs(LightTransformMode.Dynamic);

			_dynamicSpotLights.Add(Placement.From(transform), light, out var slot);
			_dynamicSpotLightTransforms.Add(transform);
			return new LightHandle(slot, _epoch, FakeLightType.Spot, LightTransformMode.Dynamic);
		}

		/// <summary>
		/// Replaces the color, intensity, range and angles of a spot light in either pool.
		/// </summary>
		/// <returns>False if the handle is dead.</returns>
		public bool SetSpotLight(in LightHandle handle, in SpotLightData light)
		{
			if (TryGetSpotLightIndex(handle, out var denseIndex) == false)
				return false;

			// The dynamic sync job reads and writes the culling list too.
			CompleteSpotLightJobs(handle.Mode);
			PoolOf(handle.Mode).SetLight(denseIndex, light);
			return true;
		}

		/// <summary>
		/// Moves a static spot light.
		/// </summary>
		/// <returns>False if the handle is dead or refers to a dynamic spot light, whose placement is owned by its
		/// <see cref="Transform"/>.</returns>
		public bool SetSpotLightPlacement(in LightHandle handle, in Placement placement)
		{
			if (handle.Mode != LightTransformMode.Static || TryGetSpotLightIndex(handle, out var denseIndex) == false)
				return false;

			CompleteSpotLightJobs(LightTransformMode.Static);
			_staticSpotLights.SetTransform(denseIndex, placement);
			return true;
		}

		/// <summary>
		/// Moves many static spot lights at once: <c>handles[i]</c> to <c>placements[i]</c>. Same result as calling
		/// <see cref="SetSpotLightPlacement"/> for each, done by a Burst job over the worker threads and waited for here.
		/// </summary>
		/// <remarks>
		/// Handles that are dead, from another store or refer to dynamic spot lights are skipped. A light listed twice
		/// gets one of its placements, in no defined order.
		/// </remarks>
		/// <returns>How many lights were moved.</returns>
		/// <exception cref="ArgumentException">The arrays differ in length.</exception>
		public int SetSpotLightPlacements(NativeArray<LightHandle> handles, NativeArray<Placement> placements)
		{
			if (handles.Length != placements.Length)
				throw new ArgumentException($"{handles.Length} handles but {placements.Length} placements.", nameof(placements));

			if (_allocated == false || handles.Length == 0 || _staticSpotLights.Length == 0)
				return 0;

			CompleteSpotLightJobs(LightTransformMode.Static);

			var batchCount = (handles.Length + SetStaticTransformsJob.BatchSize - 1) / SetStaticTransformsJob.BatchSize;
			using var batchResults = new NativeArray<int3>(batchCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

			new SetStaticTransformsJob
			{
				Handles = handles,
				Placements = placements,
				Allocator = _staticSpotLights.Allocator,
				Epoch = _epoch,
				Transforms = _staticSpotLights.Transforms.AsArray(),
				CullingData = _staticSpotLights.CullingData.AsArray(),
				CullingSpheres = _staticSpotLights.CullingSpheres.AsArray(),
				BatchResults = batchResults
			}.ScheduleBatch(handles.Length, SetStaticTransformsJob.BatchSize).Complete();

			return MarkMovedStaticLights(batchResults);
		}

		/// <summary>
		/// Removes every spot light from both pools, invalidating their handles.
		/// </summary>
		public void ClearSpotLights()
		{
			if (_allocated == false)
				return;

			CompleteSpotLightJobs(LightTransformMode.Dynamic);

			_staticSpotLights.Clear();
			_dynamicSpotLights.Clear();
			ClearTransforms(_dynamicSpotLightTransforms);
		}

		/// <summary>
		/// Removes a spot light from whichever pool it lives in (see <see cref="Remove(in LightHandle)"/>).
		/// </summary>
		private bool RemoveSpotLight(in LightHandle handle)
		{
			if (TryGetSpotLightIndex(handle, out _) == false)
				return false;

			CompleteSpotLightJobs(handle.Mode);
			PoolOf(handle.Mode).Remove(handle.Slot, out var removedDense);

			if (handle.Mode == LightTransformMode.Dynamic)
				_dynamicSpotLightTransforms.RemoveAtSwapBack(removedDense);

			return true;
		}

		/// <summary>
		/// The dense index of a live spot light of this store; false for a dead handle or one of another store or type.
		/// </summary>
		private bool TryGetSpotLightIndex(in LightHandle handle, out int denseIndex)
		{
			denseIndex = -1;
			return _allocated && handle.Type == FakeLightType.Spot && handle.Epoch == _epoch
			       && PoolOf(handle.Mode).TryGetDenseIndex(handle.Slot, out denseIndex);
		}

		/// <summary>
		/// Folds the per-batch results of <see cref="SetStaticTransformsJob"/> into the transform dirty range.
		/// </summary>
		/// <returns>How many lights were moved.</returns>
		private int MarkMovedStaticLights(NativeArray<int3> batchResults)
		{
			var moved = 0;

			foreach (var batch in batchResults)
			{
				// x: lowest moved dense index, y: highest, z: moved count.
				if (batch.z == 0)
					continue;

				_staticSpotLights.TransformsDirty.Mark(batch.x);
				_staticSpotLights.TransformsDirty.Mark(batch.y);
				moved += batch.z;
			}

			return moved;
		}

		private void AllocateSpotLights()
		{
			_staticSpotLights = new SpotLightPool(InitialSpotLightCapacity, StoreAllocator);
			_dynamicSpotLights = new SpotLightPool(InitialSpotLightCapacity, StoreAllocator);
			_dynamicSpotLightTransforms = new TransformAccessArray(InitialSpotLightCapacity);
		}

		private void ReleaseSpotLights()
		{
			CompleteSpotLightJobs(LightTransformMode.Dynamic);

			_staticSpotLights.Dispose();
			_dynamicSpotLights.Dispose();
			DisposeTransforms(ref _dynamicSpotLightTransforms);
		}

		// ToDo batched add, e.g.:
		// public void AddSpotLights(NativeArray<Placement> transforms, NativeArray<SpotLightData> lights, NativeArray<LightHandle> output)
		// Pack in pairs/quads via DataExtensions.Pack2 and append to the static pool in one go.
	}
}
