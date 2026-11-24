using System;
using System.Runtime.InteropServices;

namespace Saesentsessis.Fuluma.Core
{
	/// <summary>
	/// A contiguous slice of some larger collection: where it starts and how long it is.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	internal readonly struct BufferRange : IEquatable<BufferRange>
	{
		/// <summary>
		/// An empty range at index 0: "nothing to do".
		/// </summary>
		public static BufferRange Invalid => new(0, 0);

		/// <summary>Whether both ranges have the same index and length.</summary>
		public static bool operator ==(BufferRange left, BufferRange right)
		{
			return left.Equals(right);
		}

		/// <summary>Whether the ranges differ in index or length.</summary>
		public static bool operator !=(BufferRange left, BufferRange right)
		{
			return !left.Equals(right);
		}

		/// <summary>
		/// First element of the range.
		/// </summary>
		public readonly int Index;

		/// <summary>
		/// Number of elements in the range.
		/// </summary>
		public readonly int Length;

		/// <param name="index">First element of the range.</param>
		/// <param name="length">Number of elements in the range.</param>
		public BufferRange(int index, int length)
		{
			Index = index;
			Length = length;
		}

		/// <summary>
		/// One past the last element, so a loop reads <c>for (i = Index; i &lt; End; i++)</c>.
		/// </summary>
		public int End => Index + Length;

		/// <summary>
		/// Whether the range covers no elements.
		/// </summary>
		public bool IsEmpty => Length <= 0;

		/// <inheritdoc/>
		public bool Equals(BufferRange other)
		{
			return Index == other.Index && Length == other.Length;
		}

		/// <inheritdoc/>
		public override bool Equals(object obj)
		{
			return obj is BufferRange other && Equals(other);
		}

		/// <inheritdoc/>
		public override int GetHashCode()
		{
			return HashCode.Combine(Index, Length);
		}
	}
}