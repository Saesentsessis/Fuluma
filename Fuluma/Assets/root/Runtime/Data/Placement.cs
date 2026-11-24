using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;

namespace Saesentsessis.Fuluma.Data
{
	/// <summary>
	/// World placement of a fake light or clip box: position and rotation, no scale. Packed into
	/// <see cref="PackedTransformData"/> for the GPU.
	/// </summary>
	[Serializable, StructLayout(LayoutKind.Sequential)]
	public struct Placement
	{
		/// <summary>
		/// The current world position and rotation of <paramref name="transform"/>.
		/// </summary>
		internal static Placement From(Transform transform)
		{
			transform.GetPositionAndRotation(out var position, out var rotation);
			return new Placement { Position = position, Rotation = rotation };
		}

		/// <summary>
		/// World position (the apex of a spot light).
		/// </summary>
		[Tooltip("World position (the apex of a spot light).")]
		public Vector3 Position;

		// Pads Position to 16 bytes.
		private float _padding;

		/// <summary>
		/// World rotation. A spot light shines along its +z axis; its roll around that axis is ignored.
		/// </summary>
		[Tooltip("World rotation. A spot light shines along its +z axis; roll around that axis is ignored.")]
		public Quaternion Rotation;

		/// <summary>
		/// Local-to-world matrix of the placement (unit scale).
		/// </summary>
		internal readonly float4x4 ToMatrix() => float4x4.TRS(Position, Rotation, new float3(1f));
	}
}