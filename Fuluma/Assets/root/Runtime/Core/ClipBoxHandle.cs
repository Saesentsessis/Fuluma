using System;
using System.Runtime.InteropServices;
using Unity.Collections.Specialized;

namespace Saesentsessis.Fuluma.Core
{
	/// <summary>
	/// Stable reference to a clip box registered in a <see cref="LightStore"/>.
	/// </summary>
	/// <remarks>
	/// <see cref="Slot"/>'s index is also the box's index in the store's box array, which is what lights reference on the
	/// GPU, so it never changes while the box lives. A handle dies when its box is removed, when the store's boxes are
	/// cleared, or when the store is released (detected through <see cref="Epoch"/>).
	/// </remarks>
	[StructLayout(LayoutKind.Sequential)]
	public readonly struct ClipBoxHandle : IEquatable<ClipBoxHandle>
	{
		/// <summary>
		/// Handle that refers to nothing. Equal to <c>default</c>: real handles always carry an epoch of 1 or more.
		/// </summary>
		public static ClipBoxHandle Null => default;

		/// <summary>Whether both handles refer to the same slot, epoch and mode.</summary>
		public static bool operator ==(ClipBoxHandle left, ClipBoxHandle right) => left.Equals(right);

		/// <summary>Whether the handles differ in slot, epoch or mode.</summary>
		public static bool operator !=(ClipBoxHandle left, ClipBoxHandle right) => !left.Equals(right);

		/// <summary>
		/// Slot in the store's box array; <see cref="StableIndexHandle.Index"/> is the box index lights reference.
		/// </summary>
		internal readonly StableIndexHandle Slot;

		/// <summary>
		/// <see cref="LightStore.Epoch"/> at the moment the box was added.
		/// </summary>
		internal readonly uint Epoch;

		/// <summary>
		/// Whether the box has a fixed placement or follows a <see cref="UnityEngine.Transform"/>.
		/// </summary>
		public readonly LightTransformMode Mode;

		/// <summary>
		/// Creates a handle. Handles are issued by the store's <c>AddClipBox</c> overloads; one built by hand is valid
		/// only if it matches a live box.
		/// </summary>
		internal ClipBoxHandle(StableIndexHandle slot, uint epoch, LightTransformMode mode)
		{
			Slot = slot;
			Epoch = epoch;
			Mode = mode;
		}

		/// <summary>
		/// Whether the handle was never assigned. Use <see cref="LightStore.Contains"/> to check whether
		/// it is still alive.
		/// </summary>
		public bool IsNull => Epoch == 0;

		/// <inheritdoc/>
		public bool Equals(ClipBoxHandle other) => Slot.Equals(other.Slot) && Epoch == other.Epoch && Mode == other.Mode;

		/// <inheritdoc/>
		public override bool Equals(object obj) => obj is ClipBoxHandle other && Equals(other);

		/// <inheritdoc/>
		public override int GetHashCode() => HashCode.Combine(Slot, Epoch, (byte)Mode);
	}
}
