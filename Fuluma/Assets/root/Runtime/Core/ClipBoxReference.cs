using System;
using System.Runtime.InteropServices;
using Saesentsessis.Fuluma.Data;

namespace Saesentsessis.Fuluma.Core
{
	/// <summary>
	/// One clip box a fake light uses, and how (see <see cref="ClipBoxRole"/>).
	/// </summary>
	[StructLayout(LayoutKind.Explicit, Size = 16)]
	public readonly struct ClipBoxReference : IEquatable<ClipBoxReference>
	{
		/// <summary>
		/// A reference with <see cref="ClipBoxRole.Keep"/>: the light exists only inside its keep boxes.
		/// </summary>
		public static ClipBoxReference Keep(ClipBoxHandle box) => new(box, ClipBoxRole.Keep);

		/// <summary>
		/// A reference with <see cref="ClipBoxRole.Subtract"/>: the light is removed inside the box.
		/// </summary>
		public static ClipBoxReference Subtract(ClipBoxHandle box) => new(box, ClipBoxRole.Subtract);

		/// <summary>
		/// The box, in the same store as the light.
		/// </summary>
		[FieldOffset(0)]
		public readonly ClipBoxHandle Handle;

		/// <summary>
		/// Whether the light is kept inside the box or removed inside it.
		/// </summary>
		[FieldOffset(13)]
		public readonly ClipBoxRole Role;

		/// <param name="handle">The box, in the same store as the light.</param>
		/// <param name="role">What the box does to the light.</param>
		public ClipBoxReference(ClipBoxHandle handle, ClipBoxRole role)
		{
			Handle = handle;
			Role = role;
		}

		/// <inheritdoc/>
		public bool Equals(ClipBoxReference other) => Handle.Equals(other.Handle) && Role == other.Role;

		/// <inheritdoc/>
		public override bool Equals(object obj) => obj is ClipBoxReference other && Equals(other);

		/// <inheritdoc/>
		public override int GetHashCode() => HashCode.Combine(Handle, (byte)Role);
	}
}
