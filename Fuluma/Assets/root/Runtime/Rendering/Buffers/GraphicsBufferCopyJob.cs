using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Saesentsessis.Fuluma.Rendering.Buffers
{
	/// <summary>
	/// One block copy into a locked graphics buffer, off the main thread.
	/// </summary>
	/// <remarks>
	/// <b>The destination is a region Unity handed out from <c>LockBufferForWrite</c>.</b> It stays
	/// valid until <c>UnlockBufferAfterWrite</c>, which is why the writer must not unlock before this
	/// job's handle completes. Getting that order wrong hands the driver a buffer that a worker
	/// thread is still filling.
	/// </remarks>
	[BurstCompile(DisableSafetyChecks = true, FloatMode = FloatMode.Fast)]
	internal unsafe struct GraphicsBufferCopyJob : IJob
	{
		[NativeDisableUnsafePtrRestriction] public void* SourcePtr;
		[NativeDisableUnsafePtrRestriction] public void* DestinationPtr;

		public long ByteCount;

		public void Execute()
		{
			UnsafeUtility.MemCpy(DestinationPtr, SourcePtr, ByteCount);
		}
	}
}