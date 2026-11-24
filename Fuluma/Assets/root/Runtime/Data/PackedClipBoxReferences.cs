using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Saesentsessis.Fuluma.Data
{
	/// <summary>
	/// GPU form of a light's clip box references: up to <see cref="Count"/> 16-bit references into its store's box array.
	/// Each reference is the box index (15 bits) plus its <see cref="ClipBoxRole"/> in the top bit; <see cref="None"/>
	/// marks an unused slot. Slot <c>i</c> is the 16 bits starting at bit <c>16·(i &amp; 1)</c> of
	/// <see cref="Slots01"/> (i &lt; 2) or <see cref="Slots23"/>. Used slots always come first.
	/// </summary>
	[StructLayout(LayoutKind.Sequential, Size = Size)]
	internal struct PackedClipBoxReferences : IEquatable<PackedClipBoxReferences>
	{
		/// <summary>
		/// Size of the struct in bytes, on the CPU and the GPU.
		/// </summary>
		public const int Size = 8;

		/// <summary>
		/// References per light.
		/// </summary>
		public const int Count = 4;

		/// <summary>
		/// An unused slot. Box index 0x7FFF is never handed out, so this can't collide with a real reference.
		/// </summary>
		public const ushort None = 0xFFFF;

		/// <summary>
		/// Highest box index a reference can hold (indices are 0 to this, inclusive).
		/// </summary>
		public const int MaxBoxIndex = 0x7FFE;

		/// <summary>
		/// Role bit of a reference: set for <see cref="ClipBoxRole.Subtract"/>.
		/// </summary>
		internal const ushort SubtractBit = 0x8000;
		private const int IndexMask = 0x7FFF;

		/// <summary>
		/// No references: the light is not clipped.
		/// </summary>
		public static PackedClipBoxReferences Empty => new() { Slots01 = uint.MaxValue, Slots23 = uint.MaxValue };

		/// <summary>
		/// Encodes one reference: the box index plus the role bit.
		/// </summary>
		/// <exception cref="ArgumentOutOfRangeException"><paramref name="boxIndex"/> is negative or above
		/// <see cref="MaxBoxIndex"/>.</exception>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ushort Encode(int boxIndex, ClipBoxRole role)
		{
			if ((uint)boxIndex > MaxBoxIndex)
				throw new ArgumentOutOfRangeException(nameof(boxIndex), boxIndex, $"Box indices go up to {MaxBoxIndex}.");

			return (ushort)(boxIndex | (role == ClipBoxRole.Subtract ? SubtractBit : 0));
		}

		/// <summary>
		/// Splits an encoded reference into its box index and role.
		/// </summary>
		/// <returns>False for <see cref="None"/>.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool TryDecode(ushort reference, out int boxIndex, out ClipBoxRole role)
		{
			boxIndex = reference & IndexMask;
			role = (reference & SubtractBit) != 0 ? ClipBoxRole.Subtract : ClipBoxRole.Keep;
			return reference != None;
		}

		[Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS"), Conditional("UNITY_EDITOR")]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static void CheckSlot(int slot)
		{
			// Constant message: Burst-compiled jobs read references too.
			if ((uint)slot >= Count)
				throw new ArgumentOutOfRangeException(nameof(slot), "Clip box reference slots go from 0 to 3.");
		}

		/// <summary>
		/// Slot 0 in the low 16 bits, slot 1 in the high 16 bits.
		/// </summary>
		public uint Slots01;    // 4 B

		/// <summary>
		/// Slot 2 in the low 16 bits, slot 3 in the high 16 bits.
		/// </summary>
		public uint Slots23;    // 4 B

		/// <summary>
		/// The encoded reference in <paramref name="slot"/> (0 to <see cref="Count"/> - 1); <see cref="None"/> if
		/// unused.
		/// </summary>
		/// <remarks>The slot is range-checked only in the Editor and with collections checks enabled.</remarks>
		public ushort this[int slot]
		{
			readonly get
			{
				CheckSlot(slot);
				var pair = slot < 2 ? Slots01 : Slots23;
				return (ushort)(pair >> ((slot & 1) * 16));
			}
			set
			{
				CheckSlot(slot);
				var shift = (slot & 1) * 16;
				var mask = ~(0xFFFFu << shift);
				if (slot < 2)
					Slots01 = (Slots01 & mask) | ((uint)value << shift);
				else
					Slots23 = (Slots23 & mask) | ((uint)value << shift);
			}
		}

		/// <summary>
		/// Number of used slots (references are always packed from slot 0).
		/// </summary>
		public readonly int UsedCount
		{
			get
			{
				var count = 0;
				while (count < Count && this[count] != None)
					count++;

				return count;
			}
		}

		/// <summary>
		/// Whether any slot references <paramref name="boxIndex"/>, in either role.
		/// </summary>
		public readonly bool References(int boxIndex)
		{
			for (var slot = 0; slot < Count; slot++)
			{
				var reference = this[slot];
				if (reference != None && (reference & IndexMask) == boxIndex)
					return true;
			}

			return false;
		}

		/// <summary>
		/// Drops every reference to <paramref name="boxIndex"/> and moves the rest down, so used slots stay first.
		/// </summary>
		/// <returns>Whether anything was removed.</returns>
		public bool RemoveBox(int boxIndex)
		{
			var kept = Empty;
			var keptCount = 0;

			for (var slot = 0; slot < Count; slot++)
			{
				var reference = this[slot];
				if (reference != None && (reference & IndexMask) != boxIndex)
					kept[keptCount++] = reference;
			}

			var changed = kept.Slots01 != Slots01 || kept.Slots23 != Slots23;
			this = kept;
			return changed;
		}

		/// <inheritdoc/>
		public readonly bool Equals(PackedClipBoxReferences other) => Slots01 == other.Slots01 && Slots23 == other.Slots23;

		/// <inheritdoc/>
		public readonly override bool Equals(object obj) => obj is PackedClipBoxReferences other && Equals(other);

		/// <inheritdoc/>
		public readonly override int GetHashCode() => HashCode.Combine(Slots01, Slots23);
	}
}
