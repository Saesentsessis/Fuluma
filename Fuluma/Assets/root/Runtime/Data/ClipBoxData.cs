using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Saesentsessis.Fuluma.Data
{
	/// <summary>
	/// Authoring shape of one clip box, in the space of its placement (a static placement, or a <see cref="Transform"/>
	/// whose full matrix, scale included, applies). Packed into <see cref="PackedClipBoxData"/> for the GPU.
	/// </summary>
	[Serializable, StructLayout(LayoutKind.Sequential)]
	public struct ClipBoxData
	{
		/// <summary>
		/// A unit box centred on its placement.
		/// </summary>
		public static ClipBoxData Default => new() { Center = Vector3.zero, Size = Vector3.one };

		/// <summary>
		/// Center of the box, in the space of its placement.
		/// </summary>
		[Tooltip("Center of the box, in the space of its placement.")]
		public Vector3 Center;

		/// <summary>
		/// Width, height and depth of the box, in the space of its placement. A box with any half-extent below
		/// <see cref="DataExtensions.MinClipBoxHalfExtent"/> is degenerate and contains no point.
		/// </summary>
		[Tooltip("Width, height and depth of the box, in the space of its placement. The sign is ignored; a box with a "
		         + "zero extent contains nothing.")]
		public Vector3 Size;
	}
}
