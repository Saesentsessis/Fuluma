#if UNITY_6000_5_OR_NEWER && !FULUMA_LEGACY_LIFECYCLE
#define FULUMA_LIFECYCLE_ATTRIBUTES
#endif
using System;
using System.Collections.Generic;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Data;
using Saesentsessis.Fuluma.Profiling;
using Saesentsessis.Fuluma.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif
using UnityEngine;
using UnityEngine.Rendering;

namespace Saesentsessis.Fuluma.Rendering.Buffers
{
	/// <summary>
	/// GPU copy of one <see cref="LightStore"/>'s spot lights, shared by every pass that draws that store.
	/// </summary>
	/// <remarks>
	/// <list type="bullet">
	/// <item><b>Static pool</b>: one transform buffer and one light buffer, updated with <c>SetData</c> over the
	/// dirty ranges the store recorded since the last upload. Fully re-uploaded when the store's epoch changes or a
	/// buffer grows.</item>
	/// <item><b>Dynamic pool</b>: light data the same way. Transforms are re-read every frame (the store's sync job), then
	/// a copy job writes them into the next buffer of a <see cref="GraphicsBufferRing"/>, locked for the copy.</item>
	/// </list>
	/// Mirrors are created on the first <see cref="Acquire"/> and destroyed on the last <see cref="Release"/>. While any
	/// exists, every mirror uploads at <c>RenderPipelineManager.beginContextRendering</c> and unlocks its ring buffer at
	/// <c>endContextRendering</c> (or earlier, from a pass, via <see cref="CompleteUploads"/>).
	/// Being the store's only GPU consumer is what lets it consume the store's dirty ranges.
	/// <para>
	/// Statics: the mirror registry survives play-mode switches on purpose (the passes that own the mirrors live in
	/// renderer assets and keep them; a store's epoch change makes the next upload a full one). A code reload frees every
	/// mirror's GPU buffers first (<c>[OnCodeUnloading]</c> on 6000.5+, <c>AssemblyReloadEvents.beforeAssemblyReload</c>
	/// before), so none is left to the finalizer; a pass releasing its mirror afterwards does nothing.
	/// </para>
	/// </remarks>
#if UNITY_6000_5_OR_NEWER
	// Mirrors are owned by passes that outlive play-mode switches; released explicitly on code reload.
	[NoAutoStaticsCleanup]
#endif
	internal sealed partial class SpotLightGpuMirror : IDisposable
	{
		/// <summary>
		/// Fewest buffers a ring of per-frame data (dynamic transforms, dynamic clip boxes) rotates through: one buffer,
		/// rewritten every frame, which the GPU may still be reading from the frame before.
		/// </summary>
		internal const int MinimumRingLength = 1;

		/// <summary>
		/// Most buffers a ring rotates through: three cover the usual one to two frames the GPU runs behind the CPU (see
		/// <see cref="GraphicsBufferRing"/>).
		/// </summary>
		internal const int MaximumRingLength = 3;

		/// <summary>
		/// Ring length when none is given.
		/// </summary>
		internal const int DefaultRingLength = MaximumRingLength;

		/// <summary>
		/// Fewest lights the buffers are created for; the capacity is then rounded up to a power of two.
		/// </summary>
		internal const int MinimumCapacity = 64;

		/// <summary>
		/// GPU memory per light of capacity outside the ring: static transforms, light data and clip box references, dynamic
		/// light data and clip box references.
		/// </summary>
		internal const int GpuBytesPerLight =
			PackedTransformData.Size + PackedSpotLightData.Size + PackedClipBoxReferences.Size // static pool
			+ PackedSpotLightData.Size + PackedClipBoxReferences.Size; // dynamic pool

		/// <summary>
		/// GPU memory per light of capacity in each buffer of the ring: a dynamic transform.
		/// </summary>
		internal const int GpuBytesPerLightPerRingBuffer = PackedTransformData.Size;

		private static readonly Dictionary<LightStore, SpotLightGpuMirror> Mirrors = new(ReferenceEqualityComparer<LightStore>.Instance);

		/// <summary>
		/// Between <c>beginContextRendering</c> and <c>endContextRendering</c>: mirrors released then are kept (and
		/// disposed at the end of the frame unless acquired again), see <see cref="Release"/>.
		/// </summary>
		private static bool _renderingFrame;

		/// <summary>
		/// Reused by <see cref="OnEndContextRendering"/> to collect mirrors left unreferenced during the frame.
		/// </summary>
		private static readonly List<SpotLightGpuMirror> Unreferenced = new();

		internal static int MirrorCount => Mirrors.Count;

		/// <summary>
		/// Returns the mirror of <paramref name="store"/>, creating it if needed. Pair with <see cref="Release"/>.
		/// </summary>
		/// <param name="initialCapacity">Light capacity hint, used only when the mirror is created; buffers grow as needed.</param>
		/// <param name="initialClipBoxCapacity">Clip box capacity hint, likewise.</param>
		/// <param name="ringLength">Buffers per ring of per-frame data (<see cref="MinimumRingLength"/> to
		/// <see cref="MaximumRingLength"/>), likewise used only when the mirror is created.</param>
		public static SpotLightGpuMirror Acquire(LightStore store, int initialCapacity, int initialClipBoxCapacity,
			int ringLength = DefaultRingLength)
		{
			if (store == null)
				throw new ArgumentNullException(nameof(store));

			if (Mirrors.TryGetValue(store, out var mirror) == false)
			{
				mirror = new SpotLightGpuMirror(store, initialCapacity, initialClipBoxCapacity, ringLength);
				Mirrors.Add(store, mirror);

				if (Mirrors.Count == 1)
					Subscribe();
			}

			mirror._refCount++;
			return mirror;
		}

		/// <summary>
		/// Frees every mirror's GPU buffers and empties the registry, whoever still holds a reference (code reload only).
		/// </summary>
		internal static void ReleaseAll()
		{
			if (Mirrors.Count == 0)
				return;

			foreach (var mirror in Mirrors.Values)
			{
				mirror._refCount = 0;
				mirror.Dispose();
			}

			Mirrors.Clear();
			Unsubscribe();
			_renderingFrame = false;
		}

		/// <summary>
		/// Lets the next upload write the dynamic rings again in the current frame, for tests: the Editor test runner renders
		/// many times within one <see cref="Time.frameCount"/>, and each render waits for the GPU (readback) anyway.
		/// </summary>
		internal static void ForgetFrameGuardsForTests()
		{
			foreach (var mirror in Mirrors.Values)
			{
				mirror.CompleteUploads();
				mirror._lastTransformFrame = -1;
				mirror._lastClipBoxFrame = -1;
			}
		}

		private static void Remove(SpotLightGpuMirror mirror)
		{
			Mirrors.Remove(mirror._store);
			mirror.Dispose();

			if (Mirrors.Count == 0)
				Unsubscribe();
		}

#if FULUMA_LIFECYCLE_ATTRIBUTES
		[OnCodeUnloading]
		private static void ReleaseAllOnCodeUnload() => ReleaseAll();
#elif UNITY_EDITOR
		[UnityEditor.InitializeOnLoadMethod]
		private static void SubscribeToCodeReload()
		{
			UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= ReleaseAll;
			UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ReleaseAll;
		}
#endif

		/// <returns>True if the buffer was recreated, so its whole content must be uploaded.</returns>
		private static bool EnsureCapacity(ref GraphicsBuffer buffer, int count, int stride)
		{
			if (count <= buffer.count)
				return false;

			var capacity = Grow(count, buffer.count);
			buffer.Release();
			buffer = CreateBuffer(capacity, stride);
			return true;
		}

		private static int Grow(int required, int current)
			=> required <= current ? current : math.ceilpow2(required);

		private static GraphicsBuffer CreateBuffer(int capacity, int stride)
			=> new(GraphicsBuffer.Target.Structured, GraphicsBuffer.UsageFlags.None, capacity, stride);

		private static void ReleaseBuffer(ref GraphicsBuffer buffer)
		{
			buffer?.Release();
			buffer = null;
		}

		private static void Subscribe()
		{
			RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
			RenderPipelineManager.endContextRendering += OnEndContextRendering;
		}

		private static void Unsubscribe()
		{
			RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
			RenderPipelineManager.endContextRendering -= OnEndContextRendering;
		}

		private static void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
		{
			// Components that copy their data from elsewhere (LinkedFakeLight) push changes now, before the upload.
			GpuUploadEvents.RaiseBeforeUpload();

			_renderingFrame = true;

			foreach (var mirror in Mirrors.Values)
				mirror.Upload();
		}

		private static void OnEndContextRendering(ScriptableRenderContext context, List<Camera> cameras)
		{
			_renderingFrame = false;

			foreach (var mirror in Mirrors.Values)
			{
				mirror.CompleteUploads();
				if (mirror._refCount == 0)
					Unreferenced.Add(mirror);
			}

			foreach (var mirror in Unreferenced)
				Remove(mirror);

			Unreferenced.Clear();
		}

		private readonly LightStore _store;

		/// <summary>
		/// Buffers in each ring of this mirror (dynamic transforms, dynamic clip boxes).
		/// </summary>
		private readonly int _ringLength;

		private int _refCount;

		private GraphicsBuffer _staticTransforms;
		private GraphicsBuffer _staticLights;
		private GraphicsBuffer _dynamicLights;
		private GraphicsBufferRing _dynamicTransforms;
		private int _dynamicTransformCapacity;

		private uint _uploadedEpoch;
		private int _lastTransformFrame = -1;
		private bool _transformsLocked;

		private SpotLightGpuMirror(LightStore store, int initialCapacity, int initialClipBoxCapacity, int ringLength)
		{
			_store = store;
			_ringLength = math.clamp(ringLength, MinimumRingLength, MaximumRingLength);

			var capacity = Grow(math.max(initialCapacity, MinimumCapacity), 0);

			_staticTransforms = CreateBuffer(capacity, PackedTransformData.Size);
			_staticLights = CreateBuffer(capacity, PackedSpotLightData.Size);
			_dynamicLights = CreateBuffer(capacity, PackedSpotLightData.Size);
			_dynamicTransforms = CreateRing(capacity, PackedTransformData.Size);
			_dynamicTransformCapacity = capacity;
			CreateClipBoxBuffers(capacity, initialClipBoxCapacity);
		}

		public LightStore Store => _store;

		public GraphicsBuffer StaticTransforms => _staticTransforms;
		public GraphicsBuffer StaticLights => _staticLights;
		public GraphicsBuffer DynamicLights => _dynamicLights;

		/// <summary>
		/// The ring buffer written this frame. Bind it after <see cref="Upload"/>.
		/// </summary>
		public GraphicsBuffer DynamicTransforms => _dynamicTransforms.Current;

		/// <summary>
		/// Number of valid static lights in the buffers as of the last upload.
		/// </summary>
		public int StaticCount { get; private set; }

		/// <summary>
		/// Number of valid dynamic lights in the buffers as of the last upload.
		/// </summary>
		public int DynamicCount { get; private set; }

		/// <summary>
		/// Drops one reference; the last one frees the GPU buffers. During a frame they are freed at its end instead, unless
		/// acquired again by then: URP rebuilds a renderer in the middle of a frame (disposing the feature's pass, then
		/// creating a new one), after this frame's upload, and a fresh mirror would have drawn no lights for that frame.
		/// </summary>
		public void Release()
		{
			if (_refCount <= 0 || --_refCount > 0)
				return;

			if (_renderingFrame == false)
				Remove(this);
		}

		/// <summary>
		/// Brings the GPU buffers up to date with the store. Static data and dynamic light data are uploaded as dirty
		/// ranges; dynamic transforms are synced and copied once per frame, by jobs that run until
		/// <see cref="CompleteUploads"/>. Clip boxes and their references follow the same way.
		/// </summary>
		public void Upload()
		{
			using var profilerScope = FulumaProfiling.MirrorUpload.Auto();
			var store = _store;

			if (store == null || store.IsAllocated == false)
			{
				// Released (play-mode switch) or destroyed: draw nothing; the next allocation has a new epoch.
				StaticCount = 0;
				DynamicCount = 0;
				_uploadedEpoch = 0;
				ClipBoxesActive = false;
				return;
			}

			var fullUpload = store.Epoch != _uploadedEpoch;
			_uploadedEpoch = store.Epoch;

			UploadStatic(store, fullUpload);
			UploadDynamicLights(store, fullUpload);
			UploadDynamicTransforms(store);
			UploadClipBoxes(store, fullUpload);

			JobHandle.ScheduleBatchedJobs();
		}

		/// <summary>
		/// Waits for the dynamic transform and clip box copies and unlocks their ring buffers. Must run before the GPU
		/// reads them.
		/// </summary>
		public void CompleteUploads()
		{
			if (_transformsLocked == false && _clipBoxesLocked == false)
				return;

			using var profilerScope = FulumaProfiling.MirrorComplete.Auto();

			if (_transformsLocked)
			{
				_dynamicTransforms.CompleteAll<PackedTransformData>();
				_transformsLocked = false;
			}

			CompleteClipBoxUploads();
		}

		public void Dispose()
		{
			CompleteUploads();

			_dynamicTransforms.Dispose();
			ReleaseBuffer(ref _staticTransforms);
			ReleaseBuffer(ref _staticLights);
			ReleaseBuffer(ref _dynamicLights);
			DisposeClipBoxBuffers();

			StaticCount = 0;
			DynamicCount = 0;
		}

		private void UploadStatic(LightStore store, bool fullUpload)
		{
			var count = store.StaticSpotLightCount;
			store.ConsumeSpotLightDirtyRanges(LightTransformMode.Static, out var transformRange, out var lightRange);

			var fullTransforms = EnsureCapacity(ref _staticTransforms, count, PackedTransformData.Size) || fullUpload;
			var fullLights = EnsureCapacity(ref _staticLights, count, PackedSpotLightData.Size) || fullUpload;

			if (fullTransforms)
				transformRange = new BufferRange(0, count);

			if (fullLights)
				lightRange = new BufferRange(0, count);

			if (transformRange.IsEmpty == false)
				_staticTransforms.SetData(store.GetSpotLightTransforms(LightTransformMode.Static), transformRange.Index, transformRange.Index, transformRange.Length);

			if (lightRange.IsEmpty == false)
				_staticLights.SetData(store.GetSpotLights(LightTransformMode.Static), lightRange.Index, lightRange.Index, lightRange.Length);

			StaticCount = count;
		}

		private void UploadDynamicLights(LightStore store, bool fullUpload)
		{
			var count = store.DynamicSpotLightCount;
			store.ConsumeSpotLightDirtyRanges(LightTransformMode.Dynamic, out _, out var lightRange);

			if (EnsureCapacity(ref _dynamicLights, count, PackedSpotLightData.Size) || fullUpload)
				lightRange = new BufferRange(0, count);

			if (lightRange.IsEmpty == false)
				_dynamicLights.SetData(store.GetSpotLights(LightTransformMode.Dynamic), lightRange.Index, lightRange.Index, lightRange.Length);
		}

		private unsafe void UploadDynamicTransforms(LightStore store)
		{
			var count = store.DynamicSpotLightCount;

			// Several render contexts can run in one frame (Scene view, Game view, previews). Writing the ring more than
			// once per frame would let it wrap onto a buffer the GPU may still read.
			if (count == 0 || Time.frameCount == _lastTransformFrame)
			{
				DynamicCount = count == 0 ? 0 : DynamicCount;
				return;
			}

			if (count > _dynamicTransformCapacity)
			{
				CompleteUploads();
				_dynamicTransforms.Dispose();
				_dynamicTransformCapacity = Grow(count, _dynamicTransformCapacity);
				_dynamicTransforms = CreateRing(_dynamicTransformCapacity, PackedTransformData.Size);
			}

			var destination = _dynamicTransforms.BeginWriteNext<PackedTransformData>(count);
			var copy = store.ScheduleDynamicSpotLightCopy(destination.GetUnsafePtr());
			_dynamicTransforms.EndWrite(copy);
			JobHandle.ScheduleBatchedJobs();

			_transformsLocked = true;
			_lastTransformFrame = Time.frameCount;
			DynamicCount = count;
		}

		private GraphicsBufferRing CreateRing(int capacity, int stride)
			=> new(_ringLength, GraphicsBuffer.Target.Structured, GraphicsBuffer.UsageFlags.LockBufferForWrite, capacity, stride);
	}
}
