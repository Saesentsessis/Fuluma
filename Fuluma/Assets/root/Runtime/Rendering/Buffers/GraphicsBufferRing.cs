using System;
using Saesentsessis.Fuluma.Core;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Saesentsessis.Fuluma.Rendering.Buffers
{
	/// <summary>
	/// A rotation of identical graphics buffers, one written per frame.
	/// </summary>
	/// <remarks>
	/// <b>Why not just write the same buffer every frame.</b> The GPU is typically one to two frames
	/// behind the CPU. Writing the buffer the GPU is currently reading forces the driver either to
	/// stall the CPU until the read finishes or to shadow-copy behind our back; both are the cost we
	/// were trying to avoid by locking in the first place. With three buffers the one being written
	/// has not been touched by the GPU for two frames, so neither happens.
	/// <para>
	/// Three is the default because it covers the usual one-to-two frame depth. Two is enough when
	/// the pipeline is shallow and saves a buffer's worth of VRAM; more than three only helps if
	/// something is queueing frames much deeper than that, and hides the fact that it is.
	/// </para>
	/// <para>
	/// The buffers themselves are allocated once, at construction. Nothing here allocates per frame —
	/// not managed memory, not native, not a graphics resource.
	/// </para>
	/// </remarks>
	internal struct GraphicsBufferRing : IDisposable
	{
		private GraphicsBufferWriter[] _writers;
		private int _index;

		public GraphicsBufferRing(
			int length,
			GraphicsBuffer.Target target,
			GraphicsBuffer.UsageFlags usage,
			int count,
			int stride)
		{
			_writers = new GraphicsBufferWriter[length < 1 ? 1 : length];
			_index = 0;

			for (var i = 0; i < _writers.Length; i++)
				_writers[i] = new GraphicsBufferWriter(new GraphicsBuffer(target, usage, count, stride));
		}

		/// <summary>
		/// Number of buffers in the rotation.
		/// </summary>
		public readonly int Length => _writers?.Length ?? 0;

		/// <summary>
		/// The buffer most recently written, which is the one to bind for drawing.
		/// </summary>
		public readonly GraphicsBuffer Current => _writers[_index].Buffer;

		public readonly bool IsCreated => _writers != null;

		/// <summary>
		/// Advances to the next buffer and locks it.
		/// </summary>
		/// <remarks>
		/// Writes begin from the first element of a buffer.
		/// </remarks>
		/// <returns>The region to fill. Pass the filling job to <see cref="EndWrite"/>.</returns>
		public NativeArray<T> BeginWriteNext<T>(int elements) where T : unmanaged
		{
			_index = GetNextIndex();

			return _writers[_index].BeginWrite<T>(new BufferRange(0, elements));
		}

		/// <summary>
		/// Advances to the next buffer and locks it.
		/// </summary>
		/// <returns>The region to fill. Pass the filling job to <see cref="EndWrite"/>.</returns>
		public NativeArray<T> BeginWriteNext<T>(BufferRange range) where T : unmanaged
		{
			_index = GetNextIndex();

			return _writers[_index].BeginWrite<T>(range);
		}

		/// <summary>
		/// Records the job filling the region from <c>BeginWriteNext</c>; the buffer is unlocked once it completes.
		/// </summary>
		public void EndWrite(JobHandle handle) => _writers[_index].EndWrite(handle);

		/// <summary>
		/// Advances to the next buffer and schedules a copy of <paramref name="src"/> into it, starting at
		/// <paramref name="dstIndex"/>.
		/// </summary>
		/// <returns>The copy job's handle.</returns>
		public JobHandle ScheduleWrite<T>(NativeArray<T> src, int dstIndex,
			JobHandle dependency = default) where T : unmanaged
		{
			_index = GetNextIndex();

			return _writers[_index].ScheduleWrite(src, dstIndex, dependency);
		}

		/// <summary>
		/// Advances to the next buffer and schedules a copy of <paramref name="srcRange"/> of <paramref name="src"/> into
		/// it, starting at <paramref name="dstIndex"/>.
		/// </summary>
		/// <returns>The copy job's handle.</returns>
		public JobHandle ScheduleWrite<T>(NativeArray<T> src, BufferRange srcRange, int dstIndex,
			JobHandle dependency = default) where T : unmanaged
		{
			_index = GetNextIndex();

			return _writers[_index].ScheduleWrite(src, srcRange, dstIndex, dependency);
		}

		/// <summary>
		/// Completes and unlocks every buffer that has a write outstanding.
		/// </summary>
		public void CompleteAll<T>() where T : unmanaged
		{
			for (var i = 0; i < _writers.Length; i++)
				_writers[i].Complete<T>();
		}

		public void Dispose()
		{
			if (_writers == null)
				return;

			for (var i = 0; i < _writers.Length; i++)
				_writers[i].Dispose();

			_writers = null;
			_index = 0;
		}

		private int GetNextIndex() => _index + 1 < _writers.Length ? _index + 1 : 0;
	}
}