using System;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Data;
using Saesentsessis.Fuluma.Profiling;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Saesentsessis.Fuluma.Rendering.Culling
{
	/// <summary>
	/// Per-camera CPU culling of one store's spot lights, and the GPU buffer of visible light indices the draws read.
	/// </summary>
	/// <remarks>
	/// Index buffer layout, per pool (static, then dynamic): one region per bucket, each sized for the whole pool. Bucket
	/// 0 holds the lights the camera may be inside of (inside pass, LOD0), bucket <c>1 + lod</c> the other lights drawn
	/// with that LOD (outside pass). When the camera splits large lights off (for the downsampled light buffer), large
	/// outside lights are in bucket <c>1 + LodCount + lod</c> instead. Only the filled parts are uploaded. The upload is a
	/// plain <c>SetData</c> while the camera's graph executes: URP submits every camera before recording the next, so a
	/// later camera's upload can't reach an earlier camera's draws.
	/// </remarks>
	internal sealed class SpotLightCuller : IDisposable
	{
		private const int PoolCount = 2;
		private const int SlotsPerPool = SpotLightCullingJob.MaxBucketCount;
		private const int SlotCount = PoolCount * SlotsPerPool;

		private static int Slot(int pool, int bucket) => pool * SlotsPerPool + bucket;

		private static int ClampToCreated(int count, NativeArray<float4> spheres)
			=> math.min(count, spheres.IsCreated ? spheres.Length : 0);

		private NativeArray<uint> _indices;
		private NativeArray<int> _counters;

		// Per slot (pool * SlotsPerPool + bucket): region start in the index buffer, and visible lights kept.
		private readonly int[] _regions = new int[SlotCount];
		private readonly int[] _counts = new int[SlotCount];

		// Clip class counts (SpotLightClipClassJob) per slot, as found and as kept; and per pool whether it had references
		// to order by (without, every light of it has no boxes).
		private NativeArray<int> _regionStarts;
		private NativeArray<SpotLightClipClassCounts> _foundClasses;
		private readonly SpotLightClipClassCounts[] _classes = new SpotLightClipClassCounts[SlotCount];
		private readonly bool[] _poolHasReferences = new bool[PoolCount];

		private GraphicsBuffer _buffer;
		private JobHandle _handle;

		public SpotLightCuller()
		{
			_counters = new NativeArray<int>(SlotCount, Allocator.Persistent);
			_regionStarts = new NativeArray<int>(SlotCount, Allocator.Persistent);
			_foundClasses = new NativeArray<SpotLightClipClassCounts>(SlotCount, Allocator.Persistent);
		}

		/// <summary>
		/// Visible light indices; bind before drawing (valid after <see cref="Complete"/>).
		/// </summary>
		public GraphicsBuffer Buffer => _buffer;

		/// <summary>
		/// Visible lights the last <see cref="Complete"/> dropped because of its maximum.
		/// </summary>
		public int Dropped { get; private set; }

		/// <summary>
		/// Visible lights kept by the last <see cref="Complete"/>, over every pool and bucket.
		/// </summary>
		public int Visible { get; private set; }

		/// <summary>
		/// LOD count of the last scheduled camera.
		/// </summary>
		public int LodCount { get; private set; } = 1;

		/// <summary>
		/// Whether the last scheduled camera split large outside lights into buckets of their own
		/// (<see cref="LargeDraw"/>).
		/// </summary>
		public bool SplitsLargeLights { get; private set; }

		/// <summary>
		/// Lights kept by the last <see cref="Complete"/> that the downsampled buffer draws when large lights are split
		/// off: every inside-pass light and every large outside light, over both pools.
		/// </summary>
		public int LowResolutionLights { get; private set; }

		/// <summary>
		/// Whether the last <see cref="Schedule"/> ordered each region by clip class (<see cref="SpotLightClipClassJob"/>;
		/// see the draw ranges' <c>clipClasses</c>).
		/// </summary>
		public bool PartitionedByClipClass { get; private set; }

		/// <summary>
		/// Whether the last <see cref="Schedule"/> used <see cref="SpotLightCullingFastJob"/> (one LOD) rather than
		/// <see cref="SpotLightCullingJob"/>.
		/// </summary>
		internal bool LastUsedFastJob { get; private set; }

		/// <summary>
		/// Use <see cref="SpotLightCullingJob"/> even with one LOD (tests compare it with the fast job).
		/// </summary>
		internal bool ForceLodJob { get; set; }

		private int BucketCount => 1 + LodCount * (SplitsLargeLights ? 2 : 1);

		/// <summary>
		/// Schedules culling of the first <paramref name="staticCount"/> static and <paramref name="dynamicCount"/> dynamic
		/// lights of <paramref name="store"/> (what its GPU mirror holds). Pair with <see cref="Complete"/>.
		/// </summary>
		/// <param name="partitionByClipClass">Also order each region by clip class (<see cref="PartitionedByClipClass"/>).</param>
		public JobHandle Schedule(LightStore store, in SpotLightCullingCamera camera, int staticCount, int dynamicCount,
			bool partitionByClipClass = false)
		{
			_handle.Complete();

			// Before the culling jobs: getting the references may first drop removed boxes from them, which waits for
			// every reader of the store.
			var staticReferences = partitionByClipClass ? store.GetSpotLightClipBoxReferences(LightTransformMode.Static) : default;
			var dynamicReferences = partitionByClipClass ? store.GetSpotLightClipBoxReferences(LightTransformMode.Dynamic) : default;

			store.GetSpotLightCullingInput(LightTransformMode.Static, out var staticSpheres, out var staticLights, out _);
			store.GetSpotLightCullingInput(LightTransformMode.Dynamic, out var dynamicSpheres, out var dynamicLights, out var dynamicDependency);

			staticCount = ClampToCreated(staticCount, staticSpheres);
			dynamicCount = ClampToCreated(dynamicCount, dynamicSpheres);

			LodCount = camera.LodCount;
			SplitsLargeLights = camera.LargeBucketOffset != 0;
			LastUsedFastJob = LodCount == 1 && ForceLodJob == false;
			LayOutRegions(staticCount, dynamicCount);
			ResetCounters();

			var handle = default(JobHandle);

			PartitionedByClipClass = partitionByClipClass;
			_poolHasReferences[(int)LightTransformMode.Static] = staticReferences.IsCreated;
			_poolHasReferences[(int)LightTransformMode.Dynamic] = dynamicReferences.IsCreated;

			if (staticCount > 0)
			{
				handle = ScheduleJob(staticSpheres, staticLights, staticCount, camera, LightTransformMode.Static, default);
				handle = SchedulePartition(staticReferences, LightTransformMode.Static, handle);
			}

			if (dynamicCount > 0)
			{
				var dynamicHandle = ScheduleJob(dynamicSpheres, dynamicLights, dynamicCount, camera, LightTransformMode.Dynamic,
					dynamicDependency);
				handle = JobHandle.CombineDependencies(handle,
					SchedulePartition(dynamicReferences, LightTransformMode.Dynamic, dynamicHandle));
			}

			_handle = handle;
			store.AddSpotLightReader(handle);
			JobHandle.ScheduleBatchedJobs();
			return handle;
		}

		/// <summary>
		/// Waits for culling and lays out the draws, keeping at most <paramref name="maxLights"/> visible lights (in slot
		/// order: static inside, static LOD0, LOD1, ..., large LOD0, LOD1, ..., then the same for dynamic lights).
		/// </summary>
		public void Complete(int maxLights)
		{
			_handle.Complete();
			_handle = default;

			var budget = math.max(0, maxLights);
			var visible = 0;
			var kept = 0;
			var lowResolution = 0;

			for (var pool = 0; pool < PoolCount; pool++)
			for (var bucket = 0; bucket < SlotsPerPool; bucket++)
			{
				var slot = Slot(pool, bucket);
				var found = bucket < BucketCount ? _counters[slot] : 0;

				visible += found;
				_counts[slot] = math.min(found, budget);
				budget -= _counts[slot];
				kept += _counts[slot];
				_classes[slot] = KeptClasses(pool, slot);

				if (bucket == SpotLightCullingJob.InsideBucket || IsLargeBucket(bucket))
					lowResolution += _counts[slot];
			}

			Visible = kept;
			Dropped = visible - kept;
			LowResolutionLights = SplitsLargeLights ? lowResolution : 0;
		}

		/// <summary>
		/// Uploads the visible indices of every region.
		/// </summary>
		public void Upload()
		{
			var required = UsedIndexCount();
			if (required == 0)
				return;

			EnsureBufferCapacity(required);

			for (var slot = 0; slot < SlotCount; slot++)
				if (_counts[slot] > 0)
					_buffer.SetData(_indices, _regions[slot], _regions[slot], _counts[slot]);
		}

		/// <summary>
		/// Lights of <paramref name="pool"/> the camera may be inside of: inside pass, LOD0.
		/// </summary>
		public RangeInt InsideDraw(LightTransformMode pool) => Draw(pool, SpotLightCullingJob.InsideBucket);

		/// <summary>
		/// The other visible lights of <paramref name="pool"/> drawn with <paramref name="lod"/>: outside pass. With
		/// <see cref="SplitsLargeLights"/>, only the small ones (the large ones are in <see cref="LargeDraw"/>).
		/// </summary>
		public RangeInt OutsideDraw(LightTransformMode pool, int lod) => Draw(pool, 1 + lod);

		/// <summary>
		/// Large outside lights of <paramref name="pool"/> drawn with <paramref name="lod"/> (outside pass); empty unless
		/// <see cref="SplitsLargeLights"/>.
		/// </summary>
		public RangeInt LargeDraw(LightTransformMode pool, int lod)
			=> SplitsLargeLights && (uint)lod < (uint)LodCount ? Draw(pool, 1 + LodCount + lod) : default;

		/// <summary>
		/// <see cref="InsideDraw(LightTransformMode)"/> and its clip classes (<see cref="SpotLightClipClassJob"/>): how many
		/// of its lights fall into each class, in the order it lists them (<see cref="SpotLightClipClassCounts"/>); the rest
		/// are general. All 0 unless <see cref="PartitionedByClipClass"/>.
		/// </summary>
		public RangeInt InsideDraw(LightTransformMode pool, out SpotLightClipClassCounts clipClasses)
			=> Draw(pool, SpotLightCullingJob.InsideBucket, out clipClasses);

		/// <summary>
		/// <see cref="OutsideDraw(LightTransformMode, int)"/> and its clip classes (as
		/// <see cref="InsideDraw(LightTransformMode, out SpotLightClipClassCounts)"/>).
		/// </summary>
		public RangeInt OutsideDraw(LightTransformMode pool, int lod, out SpotLightClipClassCounts clipClasses) => Draw(pool, 1 + lod, out clipClasses);

		/// <summary>
		/// <see cref="LargeDraw(LightTransformMode, int)"/> and its clip classes (as
		/// <see cref="InsideDraw(LightTransformMode, out SpotLightClipClassCounts)"/>).
		/// </summary>
		public RangeInt LargeDraw(LightTransformMode pool, int lod, out SpotLightClipClassCounts clipClasses)
		{
			clipClasses = default;
			return SplitsLargeLights && (uint)lod < (uint)LodCount ? Draw(pool, 1 + LodCount + lod, out clipClasses) : default;
		}

		public void Dispose()
		{
			_handle.Complete();
			_handle = default;

			if (_indices.IsCreated)
				_indices.Dispose();

			if (_counters.IsCreated)
				_counters.Dispose();

			if (_regionStarts.IsCreated)
				_regionStarts.Dispose();

			if (_foundClasses.IsCreated)
				_foundClasses.Dispose();

			_buffer?.Release();
			_buffer = null;
		}

		/// <summary>
		/// Culled indices of one region after <see cref="Complete"/> (for tests).
		/// </summary>
		internal NativeArray<uint> GetIndices(RangeInt drawRange) => _indices.GetSubArray(drawRange.start, drawRange.length);

		/// <summary>
		/// A slot's lights per clip class, within what <see cref="Complete"/> kept (the beginning of the region). Zero when
		/// not partitioned: every light then counts as general.
		/// </summary>
		private SpotLightClipClassCounts KeptClasses(int pool, int slot)
		{
			if (PartitionedByClipClass == false)
				return default;

			var kept = _counts[slot];
			if (_poolHasReferences[pool] == false)
				return new SpotLightClipClassCounts { NoBoxes = kept };

			return _foundClasses[slot].Trimmed(kept);
		}

		private RangeInt Draw(LightTransformMode pool, int bucket)
		{
			if ((uint)bucket >= (uint)BucketCount)
				return default;

			var slot = Slot((int)pool, bucket);
			return new RangeInt(_regions[slot], _counts[slot]);
		}

		private RangeInt Draw(LightTransformMode pool, int bucket, out SpotLightClipClassCounts clipClasses)
		{
			clipClasses = (uint)bucket < (uint)BucketCount ? _classes[Slot((int)pool, bucket)] : default;
			return Draw(pool, bucket);
		}

		private bool IsLargeBucket(int bucket) => SplitsLargeLights && bucket > LodCount && bucket < BucketCount;

		/// <summary>
		/// Gives each bucket of each pool room for the whole pool: static buckets first, then dynamic ones.
		/// </summary>
		private void LayOutRegions(int staticCount, int dynamicCount)
		{
			var offset = 0;

			for (var pool = 0; pool < PoolCount; pool++)
			{
				var size = pool == (int)LightTransformMode.Static ? staticCount : dynamicCount;

				for (var bucket = 0; bucket < SlotsPerPool; bucket++)
				{
					_regions[Slot(pool, bucket)] = offset;
					_regionStarts[Slot(pool, bucket)] = offset;
					offset += bucket < BucketCount ? size : 0;
				}
			}

			EnsureIndexCapacity(offset);
		}

		private void ResetCounters()
		{
			for (var slot = 0; slot < SlotCount; slot++)
			{
				_counters[slot] = 0;
				_foundClasses[slot] = default;
			}
		}

		/// <summary>
		/// Orders one pool's regions by clip class once its culling job is done. Nothing to do without references (no
		/// light of the pool has boxes).
		/// </summary>
		private JobHandle SchedulePartition(NativeArray<PackedClipBoxReferences> references, LightTransformMode pool,
			JobHandle culling)
		{
			if (PartitionedByClipClass == false || references.IsCreated == false)
				return culling;

			return new SpotLightClipClassJob
			{
				References = references,
				Indices = _indices,
				Counters = _counters,
				RegionStarts = _regionStarts,
				ClassCounts = _foundClasses,
				SlotStart = Slot((int)pool, 0)
			}.Schedule(BucketCount, 1, culling);
		}

		/// <summary>
		/// End of the last region with visible lights: the part of the index buffer the draws read.
		/// </summary>
		private int UsedIndexCount()
		{
			var used = 0;
			for (var slot = 0; slot < SlotCount; slot++)
				if (_counts[slot] > 0)
					used = math.max(used, _regions[slot] + _counts[slot]);

			return used;
		}

		private void EnsureBufferCapacity(int required)
		{
			if (_buffer != null && _buffer.count >= required)
				return;

			_buffer?.Release();
			_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, math.ceilpow2(math.max(required, 64)), sizeof(uint));
		}

		private JobHandle ScheduleJob(NativeArray<float4> spheres, NativeArray<SpotLightCullingData> lights, int count,
			in SpotLightCullingCamera camera, LightTransformMode pool, JobHandle dependency)
		{
			if (LastUsedFastJob)
			{
				return new SpotLightCullingFastJob
				{
					Spheres = spheres,
					Lights = lights,
					Camera = camera,
					Indices = _indices,
					Counters = _counters,
					RegionStart = _regions[Slot((int)pool, 0)],
					RegionSize = count,
					CounterStart = Slot((int)pool, 0),
					Marker = FulumaProfiling.CullingJob
				}.ScheduleBatch(count, SpotLightCullingFastJob.BatchSize, dependency);
			}

			return new SpotLightCullingJob
			{
				Spheres = spheres,
				Lights = lights,
				Camera = camera,
				Indices = _indices,
				Counters = _counters,
				RegionStart = _regions[Slot((int)pool, 0)],
				RegionSize = count,
				CounterStart = Slot((int)pool, 0),
				Marker = FulumaProfiling.CullingJob
			}.ScheduleBatch(count, SpotLightCullingJob.BatchSize, dependency);
		}

		private void EnsureIndexCapacity(int required)
		{
			if (_indices.IsCreated && _indices.Length >= required)
				return;

			if (_indices.IsCreated)
				_indices.Dispose();

			_indices = new NativeArray<uint>(math.ceilpow2(math.max(required, 256)), Allocator.Persistent,
				NativeArrayOptions.UninitializedMemory);
		}
	}
}
