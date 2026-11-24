using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Data;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Saesentsessis.Fuluma.Rendering.Buffers
{
	// Clip boxes: the store's box array and each pool's per-light box references. Their buffers exist for the mirror's
	// whole life, whether or not any pass draws with clip boxes; they are kept up to date like the light buffers.
	internal sealed partial class SpotLightGpuMirror
	{
		/// <summary>
		/// Fewest boxes the box buffers are created for; the capacity is then rounded up to a power of two.
		/// </summary>
		internal const int MinimumClipBoxCapacity = 16;

		/// <summary>
		/// GPU memory per box of capacity outside the ring: the box buffer used while the store has static boxes only.
		/// </summary>
		internal const int GpuBytesPerClipBox = PackedClipBoxData.Size;

		/// <summary>
		/// GPU memory per box of capacity in each buffer of the ring used while the store has dynamic boxes.
		/// </summary>
		internal const int GpuBytesPerClipBoxPerRingBuffer = PackedClipBoxData.Size;

		/// <summary>
		/// One pool's references, as many as the pool has lights. Reading them from the store first drops references to
		/// removed boxes (deferred until now).
		/// </summary>
		private static void UploadClipBoxReferences(LightStore store, LightTransformMode mode, ref GraphicsBuffer buffer,
			bool fullUpload)
		{
			var references = store.GetSpotLightClipBoxReferences(mode);
			var range = store.ConsumeSpotLightClipBoxDirtyRange(mode);
			var count = references.Length;

			if (EnsureCapacity(ref buffer, count, PackedClipBoxReferences.Size) || fullUpload)
				range = new BufferRange(0, count);

			if (range.IsEmpty == false)
				buffer.SetData(references, range.Index, range.Index, range.Length);
		}

		/// <summary>
		/// Boxes by box index while the store has static boxes only, updated over the dirty range.
		/// </summary>
		private GraphicsBuffer _clipBoxes;

		/// <summary>
		/// While the store has dynamic boxes: every box, rewritten each frame through a locked buffer by a job (re-pack the
		/// dynamic boxes, copy them all), never on the main thread.
		/// </summary>
		private GraphicsBufferRing _clipBoxRing;
		private int _clipBoxRingCapacity;
		private int _lastClipBoxFrame = -1;
		private bool _clipBoxesFromRing;
		private bool _clipBoxesLocked;

		private GraphicsBuffer _staticClipBoxReferences;
		private GraphicsBuffer _dynamicClipBoxReferences;

		/// <summary>
		/// Whether the store has boxes as of the last upload, so draws need the clip box variants and bindings.
		/// </summary>
		public bool ClipBoxesActive { get; private set; }

		/// <summary>
		/// Boxes by box index (<see cref="PackedClipBoxData"/>).
		/// </summary>
		public GraphicsBuffer ClipBoxes => _clipBoxesFromRing ? _clipBoxRing.Current : _clipBoxes;

		/// <summary>
		/// The static pool's per-light references, index-aligned with <see cref="StaticLights"/>.
		/// </summary>
		public GraphicsBuffer StaticClipBoxReferences => _staticClipBoxReferences;

		/// <summary>
		/// The dynamic pool's per-light references, index-aligned with <see cref="DynamicLights"/>.
		/// </summary>
		public GraphicsBuffer DynamicClipBoxReferences => _dynamicClipBoxReferences;

		/// <summary>
		/// Whether the boxes come from the per-frame ring (the store has dynamic boxes), for tests.
		/// </summary>
		internal bool ClipBoxesFromRing => _clipBoxesFromRing;

		private void CreateClipBoxBuffers(int lightCapacity, int clipBoxCapacity)
		{
			_clipBoxRingCapacity = Grow(math.max(clipBoxCapacity, MinimumClipBoxCapacity), 0);
			_clipBoxes = CreateBuffer(_clipBoxRingCapacity, PackedClipBoxData.Size);
			_clipBoxRing = CreateRing(_clipBoxRingCapacity, PackedClipBoxData.Size);
			_staticClipBoxReferences = CreateBuffer(lightCapacity, PackedClipBoxReferences.Size);
			_dynamicClipBoxReferences = CreateBuffer(lightCapacity, PackedClipBoxReferences.Size);
		}

		private void UploadClipBoxes(LightStore store, bool fullUpload)
		{
			UploadBoxes(store, fullUpload);
			UploadClipBoxReferences(store, LightTransformMode.Static, ref _staticClipBoxReferences, fullUpload);
			UploadClipBoxReferences(store, LightTransformMode.Dynamic, ref _dynamicClipBoxReferences, fullUpload);
			ClipBoxesActive = store.ClipBoxCount > 0;
		}

		private void UploadBoxes(LightStore store, bool fullUpload)
		{
			if (store.HasDynamicClipBoxes)
			{
				UploadBoxesThroughRing(store);
				return;
			}

			// Static boxes only: changed ranges with SetData. Coming back from the ring, everything.
			var full = fullUpload || _clipBoxesFromRing;
			_clipBoxesFromRing = false;

			var count = store.ClipBoxIndexCount;
			var range = store.ConsumeClipBoxDirtyRange();

			if (EnsureCapacity(ref _clipBoxes, count, PackedClipBoxData.Size) || full)
				range = new BufferRange(0, count);

			if (range.IsEmpty == false)
				_clipBoxes.SetData(store.GetClipBoxes(), range.Index, range.Index, range.Length);
		}

		/// <summary>
		/// Dynamic boxes follow their transforms, so every frame a job re-packs them and copies every box into the next ring
		/// buffer, locked until <see cref="CompleteClipBoxUploads"/> (the first light pass, or the end of the frame). At most
		/// once per frame: several render contexts in one frame share it (writing again could wrap onto a buffer the GPU
		/// still reads).
		/// </summary>
		private unsafe void UploadBoxesThroughRing(LightStore store)
		{
			// The ring rewrites every box each frame: changed ranges don't matter.
			store.ConsumeClipBoxDirtyRange();
			_clipBoxesFromRing = true;

			if (Time.frameCount == _lastClipBoxFrame)
				return;

			var count = store.ClipBoxIndexCount;
			if (count > _clipBoxRingCapacity)
			{
				CompleteClipBoxUploads();
				_clipBoxRing.Dispose();
				_clipBoxRingCapacity = Grow(count, _clipBoxRingCapacity);
				_clipBoxRing = CreateRing(_clipBoxRingCapacity, PackedClipBoxData.Size);
			}

			var destination = _clipBoxRing.BeginWriteNext<PackedClipBoxData>(count);
			_clipBoxRing.EndWrite(store.ScheduleClipBoxCopy(destination.GetUnsafePtr()));

			_clipBoxesLocked = true;
			_lastClipBoxFrame = Time.frameCount;
		}

		/// <summary>
		/// Waits for the box copy and unlocks its ring buffer. Must run before the GPU reads it.
		/// </summary>
		private void CompleteClipBoxUploads()
		{
			if (_clipBoxesLocked == false)
				return;

			_clipBoxRing.CompleteAll<PackedClipBoxData>();
			_clipBoxesLocked = false;
		}

		private void DisposeClipBoxBuffers()
		{
			ClipBoxesActive = false;
			CompleteClipBoxUploads();
			_clipBoxRing.Dispose();
			_clipBoxRingCapacity = 0;
			_lastClipBoxFrame = -1;
			_clipBoxesFromRing = false;
			ReleaseBuffer(ref _clipBoxes);
			ReleaseBuffer(ref _staticClipBoxReferences);
			ReleaseBuffer(ref _dynamicClipBoxReferences);
		}
	}
}
