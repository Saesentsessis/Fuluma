using System;
using System.Runtime.InteropServices;
using Unity.Collections.Specialized;

namespace Saesentsessis.Fuluma.Core
{
	/// <summary>
	/// Stable reference to a fake light registered in a <see cref="LightStore"/>.
	/// </summary>
	/// <remarks>
	/// A handle addresses a slot inside one pool of the store. The pool is selected by
	/// <see cref="Type"/> and <see cref="Mode"/>, so callers never have to track which pool a light lives in.
	/// A handle dies when its light is removed, when the store is cleared, or when the store is released
	/// (the latter is detected through <see cref="Epoch"/>).
	/// </remarks>
	[StructLayout(LayoutKind.Sequential)]
	public readonly struct LightHandle : IEquatable<LightHandle>
	{
		/// <summary>
		/// Handle that refers to nothing. Equal to <c>default</c>: real handles always carry an epoch of 1 or more.
		/// </summary>
		public static LightHandle Null => default;

		/// <summary>Whether both handles refer to the same slot, epoch, type and mode.</summary>
		public static bool operator ==(LightHandle left, LightHandle right) => left.Equals(right);

		/// <summary>Whether the handles differ in slot, epoch, type or mode.</summary>
		public static bool operator !=(LightHandle left, LightHandle right) => !left.Equals(right);

		/// <summary>
		/// Slot inside the pool selected by <see cref="Type"/> and <see cref="Mode"/>.
		/// </summary>
		internal readonly StableIndexHandle Slot;

		/// <summary>
		/// <see cref="LightStore.Epoch"/> at the moment the light was added.
		/// </summary>
		internal readonly uint Epoch;

		/// <summary>
		/// Light type, which selects the store's pools.
		/// </summary>
		public readonly FakeLightType Type;

		/// <summary>
		/// Static or dynamic pool of that type.
		/// </summary>
		public readonly LightTransformMode Mode;

		/// <summary>
		/// Creates a handle. Handles are issued by the store's <c>Add…</c> methods; one built by hand is valid only if
		/// it matches a live light.
		/// </summary>
		internal LightHandle(StableIndexHandle slot, uint epoch, FakeLightType type, LightTransformMode mode)
		{
			Slot = slot;
			Epoch = epoch;
			Type = type;
			Mode = mode;
		}

		/// <summary>
		/// Whether the handle was never assigned. Use <see cref="LightStore.Contains(in LightHandle)"/> to check whether it is still alive.
		/// </summary>
		public bool IsNull => Epoch == 0;

		/// <inheritdoc/>
		public bool Equals(LightHandle other)
		{
			return Slot.Equals(other.Slot) && Epoch == other.Epoch && Type == other.Type && Mode == other.Mode;
		}

		/// <inheritdoc/>
		public override bool Equals(object obj)
		{
			return obj is LightHandle other && Equals(other);
		}

		/// <inheritdoc/>
		public override int GetHashCode()
		{
			return HashCode.Combine(Slot, Epoch, (byte)Type, (byte)Mode);
		}
	}
}
