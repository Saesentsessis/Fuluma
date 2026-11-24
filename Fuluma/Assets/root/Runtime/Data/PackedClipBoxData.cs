using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace Saesentsessis.Fuluma.Data
{
	/// <summary>
	/// GPU form of a clip box: the world-to-box affine transform, mapping the box onto the cube [-1, 1]³. A ray
	/// <c>o + t·d</c> keeps its parameter <c>t</c> through it, so the slab test in box space gives the ray's interval in
	/// world distances directly. Any affine placement works, sheared ones included.
	/// </summary>
	[StructLayout(LayoutKind.Sequential, Size = Size)]
	internal struct PackedClipBoxData
	{
		/// <summary>
		/// Size of the struct in bytes, on the CPU and the GPU.
		/// </summary>
		public const int Size = 48;

		/// <summary>
		/// A box that contains no point (every point maps to x = 2): what a degenerate box (zero size or scale) packs to.
		/// </summary>
		public static PackedClipBoxData Empty => new() { Row0 = new float4(0f, 0f, 0f, 2f) };

		/// <summary>
		/// Box-space x, y, z of a world point <c>p</c>: <c>dot(RowN.xyz, p) + RowN.w</c>. This row gives x.
		/// </summary>
		public float4 Row0;     // 16 B

		/// <summary>
		/// Second row of the world-to-box transform: gives box-space y (see <see cref="Row0"/>).
		/// </summary>
		public float4 Row1;     // 16 B

		/// <summary>
		/// Third row of the world-to-box transform: gives box-space z (see <see cref="Row0"/>).
		/// </summary>
		public float4 Row2;     // 16 B

		/// <summary>
		/// Box-space position of <paramref name="world"/>; inside the box when every component is within [-1, 1].
		/// </summary>
		public readonly float3 ToBox(float3 world)
			=> new(math.dot(Row0.xyz, world) + Row0.w, math.dot(Row1.xyz, world) + Row1.w, math.dot(Row2.xyz, world) + Row2.w);

		/// <summary>
		/// Whether <paramref name="world"/> lies inside the box or on its surface.
		/// </summary>
		public readonly bool Contains(float3 world)
			=> math.all(math.abs(ToBox(world)) <= 1f);
	}
}
