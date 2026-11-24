using System;
using UnityEngine;

namespace Saesentsessis.Fuluma.Core
{
	/// <summary>
	/// Shows, under an integer capacity field, the memory that capacity allocates on the CPU and/or the GPU.
	/// </summary>
	/// <remarks>
	/// The allocated count is <c>max(value, MinimumCount)</c>, rounded up to a power of two when
	/// <see cref="RoundUpToPowerOfTwo"/> is set, mirroring how the owning buffers size themselves. A side whose bytes per
	/// item is 0 isn't shown. GPU data kept in several buffers (a ring) adds <see cref="GpuBytesPerItemPerBuffer"/> times
	/// the integer in the sibling field <see cref="BufferCountField"/>.
	/// </remarks>
	[AttributeUsage(AttributeTargets.Field)]
	public class ByteSizeAttribute : PropertyAttribute
	{
		/// <summary>
		/// CPU bytes per item of capacity (0: no CPU allocation).
		/// </summary>
		public readonly int CpuBytesPerItem;

		/// <summary>
		/// GPU bytes per item of capacity (0: no GPU allocation).
		/// </summary>
		public readonly int GpuBytesPerItem;

		/// <summary>
		/// Fewest items allocated whatever the field says.
		/// </summary>
		public readonly int MinimumCount;

		/// <summary>
		/// Whether the allocated count is rounded up to a power of two.
		/// </summary>
		public readonly bool RoundUpToPowerOfTwo;

		/// <summary>
		/// GPU bytes per item of capacity in each of <see cref="BufferCountField"/> buffers (0: none).
		/// </summary>
		public readonly int GpuBytesPerItemPerBuffer;

		/// <summary>
		/// Name of the sibling integer field holding the buffer count; null when <see cref="GpuBytesPerItemPerBuffer"/> is 0.
		/// </summary>
		public readonly string BufferCountField;

		/// <param name="cpuBytesPerItem">CPU bytes per item of capacity; 0 (or less) hides the CPU side.</param>
		/// <param name="gpuBytesPerItem">GPU bytes per item of capacity; 0 (or less) hides the GPU side.</param>
		/// <param name="minimumCount">Fewest items allocated whatever the field says.</param>
		/// <param name="roundUpToPowerOfTwo">Whether the allocated count is rounded up to a power of two.</param>
		/// <param name="gpuBytesPerItemPerBuffer">GPU bytes per item in each buffer counted by
		/// <paramref name="bufferCountField"/>.</param>
		/// <param name="bufferCountField">Sibling integer field with the number of those buffers.</param>
		public ByteSizeAttribute(int cpuBytesPerItem, int gpuBytesPerItem, int minimumCount = 0, bool roundUpToPowerOfTwo = false,
			int gpuBytesPerItemPerBuffer = 0, string bufferCountField = null)
		{
			GpuBytesPerItemPerBuffer = bufferCountField == null ? 0 : Math.Max(0, gpuBytesPerItemPerBuffer);
			BufferCountField = GpuBytesPerItemPerBuffer > 0 ? bufferCountField : null;
			CpuBytesPerItem = Math.Max(0, cpuBytesPerItem);
			GpuBytesPerItem = Math.Max(0, gpuBytesPerItem);
			MinimumCount = Math.Max(0, minimumCount);
			RoundUpToPowerOfTwo = roundUpToPowerOfTwo;
			order = short.MinValue;
		}

		/// <summary>
		/// The number of items a field value of <paramref name="value"/> allocates.
		/// </summary>
		public long AllocatedCount(int value)
		{
			long count = Math.Max(Math.Max(value, 0), MinimumCount);
			if (RoundUpToPowerOfTwo == false || count <= 1)
				return count;

			long power = 1;
			while (power < count)
				power <<= 1;

			return power;
		}
	}
}
