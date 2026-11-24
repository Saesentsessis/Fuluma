using Saesentsessis.Fuluma.Core.Pools;
using Saesentsessis.Fuluma.Data;
using Saesentsessis.Fuluma.Rendering.Buffers;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace Saesentsessis.Fuluma.Core
{
	// Jobs: the per-frame dynamic transform syncs of lights and boxes, the copies of their results into locked GPU
	// buffers, and the culling jobs that read the pools.
	public partial class LightStore
	{
		/// <summary>
		/// Copies <paramref name="byteCount"/> bytes from <paramref name="source"/> to <paramref name="destination"/> on a
		/// worker once <paramref name="dependency"/> is done.
		/// </summary>
		private static unsafe JobHandle ScheduleCopy(void* source, void* destination, long byteCount, JobHandle dependency)
		{
			return new GraphicsBufferCopyJob
			{
				SourcePtr = source,
				DestinationPtr = destination,
				ByteCount = byteCount
			}.Schedule(dependency);
		}

		/// <summary>
		/// Schedules the per-frame copy of every dynamic spot light's <see cref="Transform"/> into the dynamic pool's
		/// transform and culling lists.
		/// </summary>
		/// <remarks>
		/// The store completes this job itself before any change to the dynamic pool. Readers must depend on the returned
		/// handle or call <see cref="CompleteSpotLightJobs"/> with <see cref="LightTransformMode.Dynamic"/>.
		/// </remarks>
		/// <param name="dependency">Job the sync waits for.</param>
		/// <returns>The sync job, or <paramref name="dependency"/> when there are no dynamic spot lights.</returns>
		internal JobHandle ScheduleDynamicSpotLightSync(JobHandle dependency = default)
		{
			if (_dynamicSpotLights.Length == 0)
				return dependency;

			_dynamicSpotLightSync = new PackDynamicTransformsJob
			{
				Output = _dynamicSpotLights.Transforms.AsArray(),
				CullingSpheres = _dynamicSpotLights.CullingSpheres.AsArray(),
				CullingData = _dynamicSpotLights.CullingData.AsArray()
			}.ScheduleReadOnly(_dynamicSpotLightTransforms, DynamicTransformBatchSize,
				JobHandle.CombineDependencies(dependency, _dynamicSpotLightSync, _spotLightReaders));

			return _dynamicSpotLightSync;
		}

		/// <summary>
		/// Syncs dynamic transforms from their <see cref="Transform"/>s and copies the result to
		/// <paramref name="destination"/> (typically a locked GPU buffer), all off the main thread.
		/// </summary>
		/// <remarks>
		/// The copy reads the store's memory through a raw pointer, which the job safety system can't see. The store
		/// therefore tracks the returned job like its own sync job: any structural change to the dynamic pool, and
		/// releasing the store, waits for it first.
		/// </remarks>
		/// <param name="destination">At least <see cref="DynamicSpotLightCount"/> elements; must stay valid until the
		/// returned job completes.</param>
		internal unsafe JobHandle ScheduleDynamicSpotLightCopy(void* destination, JobHandle dependency = default)
		{
			var count = DynamicSpotLightCount;
			if (count == 0)
				return dependency;

			// Last frame's sync and copy should already be done; completing makes taking the pointer legal.
			CompleteSpotLightJobs(LightTransformMode.Dynamic);

			var source = NativeListUnsafeUtility.GetUnsafeReadOnlyPtr(_dynamicSpotLights.Transforms);
			_dynamicSpotLightSync = ScheduleCopy(source, destination, (long)PackedTransformData.Size * count,
				ScheduleDynamicSpotLightSync(dependency));

			return _dynamicSpotLightSync;
		}

		/// <summary>
		/// Waits for the jobs that touch one pool: the culling jobs reading it, and for the dynamic pool also its transform
		/// sync (and any copy of its output).
		/// </summary>
		internal void CompleteSpotLightJobs(LightTransformMode mode)
		{
			CompleteSpotLightReaders();

			if (mode == LightTransformMode.Static)
				return;

			_dynamicSpotLightSync.Complete();
			_dynamicSpotLightSync = default;
		}

		/// <summary>
		/// Waits for every job reading the pools (see <see cref="AddSpotLightReader"/>).
		/// </summary>
		internal void CompleteSpotLightReaders()
		{
			_spotLightReaders.Complete();
			_spotLightReaders = default;
		}

		/// <summary>
		/// Registers a job that reads the pools' culling lists or clip box references, so changes to the pools wait for it.
		/// </summary>
		internal void AddSpotLightReader(JobHandle reader)
			=> _spotLightReaders = JobHandle.CombineDependencies(_spotLightReaders, reader);

		/// <summary>
		/// Schedules the per-frame re-packing of every dynamic clip box from its <see cref="Transform"/>.
		/// </summary>
		/// <remarks>
		/// The store completes this job itself before any change to the boxes. Readers of the box array must depend on
		/// the returned handle or call <see cref="CompleteClipBoxJobs"/>.
		/// </remarks>
		/// <param name="dependency">Job the sync waits for.</param>
		/// <returns>The sync job, or <paramref name="dependency"/> when there are no dynamic boxes.</returns>
		internal JobHandle ScheduleDynamicClipBoxSync(JobHandle dependency = default)
		{
			if (HasDynamicClipBoxes == false)
				return dependency;

			MarkDynamicClipBoxesDirty();

			_dynamicClipBoxSync = new PackDynamicClipBoxesJob
			{
				DynamicBoxes = _clipBoxes.DynamicBoxes.AsArray(),
				Shapes = _clipBoxes.Shapes.AsArray(),
				Boxes = _clipBoxes.Boxes.AsArray()
			}.ScheduleReadOnly(_dynamicClipBoxTransforms, DynamicClipBoxBatchSize,
				JobHandle.CombineDependencies(dependency, _dynamicClipBoxSync));

			return _dynamicClipBoxSync;
		}

		/// <summary>
		/// Re-packs the dynamic boxes and copies every box (static ones too, holes included: <see cref="ClipBoxIndexCount"/>
		/// of them) to <paramref name="destination"/>, typically a locked GPU buffer, all off the main thread. The store
		/// completes the copy itself before any box changes (<see cref="CompleteClipBoxJobs"/>).
		/// </summary>
		internal unsafe JobHandle ScheduleClipBoxCopy(void* destination, JobHandle dependency = default)
		{
			var count = ClipBoxIndexCount;
			if (count == 0)
				return dependency;

			// Last frame's packing and copy should already be done; completing makes taking the pointer legal.
			CompleteClipBoxJobs();

			var source = NativeListUnsafeUtility.GetUnsafeReadOnlyPtr(_clipBoxes.Boxes);
			_dynamicClipBoxSync = ScheduleCopy(source, destination, (long)PackedClipBoxData.Size * count,
				ScheduleDynamicClipBoxSync(dependency));

			return _dynamicClipBoxSync;
		}

		/// <summary>
		/// Waits for the job scheduled by <see cref="ScheduleDynamicClipBoxSync"/> (and for any copy of the boxes
		/// scheduled after it).
		/// </summary>
		internal void CompleteClipBoxJobs()
		{
			_dynamicClipBoxSync.Complete();
			_dynamicClipBoxSync = default;
		}

		/// <summary>
		/// Dynamic boxes are re-packed every frame: marks the index range they span.
		/// </summary>
		private void MarkDynamicClipBoxesDirty()
		{
			var dynamicBoxes = _clipBoxes.DynamicBoxes;
			var low = int.MaxValue;
			var high = int.MinValue;

			for (var i = dynamicBoxes.Length - 1; i >= 0; i--)
			{
				low = math.min(low, dynamicBoxes[i]);
				high = math.max(high, dynamicBoxes[i]);
			}

			_clipBoxes.BoxesDirty.Mark(low);
			_clipBoxes.BoxesDirty.Mark(high);
		}
	}
}
