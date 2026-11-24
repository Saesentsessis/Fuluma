using System;
using Saesentsessis.Fuluma.Data;
using UnityEngine;

namespace Saesentsessis.Fuluma.Rendering.Components
{
	/// <summary>
	/// One clip box a light component uses, and how.
	/// </summary>
	[Serializable]
	public struct ClipBoxSlot : IEquatable<ClipBoxSlot>
	{
		[Tooltip("The box. It must be enabled and in the same store as the light to take effect.")]
		public FakeLightClipBox Box;

		[Tooltip("Keep: the light exists only inside its keep boxes. Subtract: the light is removed inside this box (no shadow behind it).")]
		public ClipBoxRole Role;

		/// <summary>
		/// A slot using <paramref name="box"/> in the given role.
		/// </summary>
		/// <param name="box">The box; null leaves the slot unused.</param>
		/// <param name="role">Whether the box keeps or removes the light inside it.</param>
		public ClipBoxSlot(FakeLightClipBox box, ClipBoxRole role)
		{
			Box = box;
			Role = role;
		}

		/// <summary>
		/// Whether both slots use the same box in the same role.
		/// </summary>
		public bool Equals(ClipBoxSlot other) => Box == other.Box && Role == other.Role;

		/// <inheritdoc/>
		public override bool Equals(object obj) => obj is ClipBoxSlot other && Equals(other);

		/// <inheritdoc/>
		public override int GetHashCode() => HashCode.Combine(Box, (byte)Role);
	}
}
