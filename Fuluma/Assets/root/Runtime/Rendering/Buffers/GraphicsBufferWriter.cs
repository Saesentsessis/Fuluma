using System;
using Saesentsessis.Fuluma.Core;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace Saesentsessis.Fuluma.Rendering.Buffers
{
	/// <summary>
	/// One graphics buffer plus the write that is still in flight into it.
	/// </summary>
	/// <remarks>
	/// <c>LockBufferForWrite</c> hands back a mapped region and <c>UnlockBufferAfterWrite</c> gives
	/// it to the driver. Between those two calls the region belongs to the user, so a job can fill it,
	/// but only if the unlock waits for that job. Pairing the handle with the buffer is what makes that
	/// impossible to get wrong from the outside: there is no way to unlock without going through
	/// <see cref="Complete"/>.
	/// </remarks>
	internal struct GraphicsBufferWriter : IDisposable
	{
		private GraphicsBuffer _buffer;
		private JobHandle _pending;
		private BufferRange _lockedRange;
		private bool _locked;

		public GraphicsBufferWriter(GraphicsBuffer buffer)
		{
			_buffer = buffer;
			_pending = default;
			_lockedRange = BufferRange.Invalid;
			_locked = false;
		}

		private GraphicsBufferWriter(GraphicsBuffer buffer, JobHandle handle, BufferRange range, bool locked)
		{
			_buffer = buffer;
			_pending = handle;
			_lockedRange = range;
			_locked = locked;
		}

		public readonly bool IsCreated => _buffer != null;
		
		public readonly GraphicsBuffer Buffer => _buffer;

		/// <summary>
		/// Locks <paramref name="range"/> of the buffer and returns it as the region to write into.
		/// </summary>
		/// <remarks>
		/// Completes any previous write first. That is a stall if it happens, and it only happens if
		/// the same buffer is written twice in a frame — which is what <see cref="GraphicsBufferRing"/> exists to
		/// avoid.
		/// </remarks>
		public NativeArray<T> BeginWrite<T>(BufferRange range) where T : unmanaged
		{
			Complete<T>();

			_lockedRange = range;
			_locked = true;

			return _buffer.LockBufferForWrite<T>(range.Index, range.Length);
		}

		/// <summary>
		/// Records the job that fills the locked region. It must not be completed yet.
		/// </summary>
		public void EndWrite(JobHandle handle)
		{
			_pending = handle;
		}

		/// <summary>
		/// Locks <c>src.Length</c> elements of the buffer from <paramref name="dstIndex"/> and schedules a job that
		/// copies <paramref name="src"/> into them.
		/// </summary>
		/// <remarks>
		/// Completes any previous write first. That is a stall if it happens, and it only happens if
		/// the same buffer is written twice in a frame — which is what <see cref="GraphicsBufferRing"/> exists to
		/// avoid.
		/// </remarks>
		/// <param name="src">CPU-side source data.</param>
		/// <param name="dstIndex">First element of the graphics buffer to write (the start of the locked region).</param>
		/// <param name="dependency">A job the copy must wait for.</param>
		/// <returns>The copy job's handle.</returns>
		public unsafe JobHandle ScheduleWrite<T>(NativeArray<T> src, int dstIndex, JobHandle dependency)
			where T : unmanaged
		{
			Complete<T>();
			
			_lockedRange = new BufferRange(dstIndex, src.Length);
			_locked = true;
			
			var dst = _buffer.LockBufferForWrite<T>(_lockedRange.Index, _lockedRange.Length);

			var writeJob = new GraphicsBufferCopyJob
			{
				SourcePtr = src.GetUnsafeReadOnlyPtr(),
				DestinationPtr = dst.GetUnsafePtr(),
				ByteCount = (long)sizeof(T) * src.Length
			};
			
			return _pending = writeJob.Schedule(dependency);
		}

		/// <summary>
		/// Locks <c>srcRange.Length</c> elements of the buffer from <paramref name="dstIndex"/> and schedules a job that
		/// copies <paramref name="srcRange"/> of <paramref name="src"/> into them.
		/// </summary>
		/// <remarks>
		/// Completes any previous write first. That is a stall if it happens, and it only happens if
		/// the same buffer is written twice in a frame — which is what <see cref="GraphicsBufferRing"/> exists to
		/// avoid.
		/// </remarks>
		/// <param name="src">CPU-side source data.</param>
		/// <param name="srcRange">The elements of <paramref name="src"/> to copy.</param>
		/// <param name="dstIndex">First element of the graphics buffer to write (the start of the locked region).</param>
		/// <param name="dependency">A job the copy must wait for.</param>
		/// <returns>The copy job's handle.</returns>
		public unsafe JobHandle ScheduleWrite<T>(NativeArray<T> src, BufferRange srcRange, int dstIndex, JobHandle dependency = default)
			where T : unmanaged
		{
			Complete<T>();

			_lockedRange = new BufferRange(dstIndex, srcRange.Length);
			_locked = true;

			var dst = _buffer.LockBufferForWrite<T>(_lockedRange.Index, _lockedRange.Length);

			var writeJob = new GraphicsBufferCopyJob
			{
				SourcePtr = (T*)src.GetUnsafeReadOnlyPtr() + srcRange.Index,
				DestinationPtr = dst.GetUnsafePtr(),
				ByteCount = (long)sizeof(T) * srcRange.Length
			};

			return _pending = writeJob.Schedule(dependency);
		}

		/// <summary>
		/// Waits for the pending write and releases the region to the driver.
		/// </summary>
		public void Complete<T>() where T : unmanaged
		{
			if (_locked == false)
				return;

			_pending.Complete();
			_pending = default;

			_buffer.UnlockBufferAfterWrite<T>(_lockedRange.Length);

			_locked = false;
			_lockedRange = BufferRange.Invalid;
		}

		public void Dispose()
		{
			if (_buffer == null)
				return;

			// Unlocking needs an element type and there is none to hand at disposal. Completing the
			// job is the part that matters — the buffer release below reclaims the mapping either way.
			_pending.Complete();
			_pending = default;
			_locked = false;

			_buffer.Release();
			_buffer = null;
		}
	}
}