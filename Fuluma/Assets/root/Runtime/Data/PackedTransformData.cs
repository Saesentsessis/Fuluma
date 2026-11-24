using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace Saesentsessis.Fuluma.Data
{
	/// <summary>
	/// GPU layout of one fake light's placement: world position and light axis. Mirrors
	/// <c>FulumaPackedTransformData</c> in <c>Shaders/Analytical/SpotLight/FulumaSpotLightInput.hlsl</c>: keep both in
	/// sync.
	/// </summary>
	[StructLayout(LayoutKind.Sequential, Size = Size)]
	internal struct PackedTransformData
	{
		/// <summary>
		/// Size of the struct in bytes, on the CPU and the GPU.
		/// </summary>
		public const int Size = 16;

		/// <summary>
		/// World position (the apex of a spot light).
		/// </summary>
		public float3 Position;			// 12 B

		/// <summary>
		/// Light axis (the rotation's +z) as polar angles packed by <see cref="Utils.QuaternionUtils.PackPolar"/>:
		/// azimuth in the high 16 bits, elevation in the low 16 bits, each unorm16. Roll is not stored.
		/// </summary>
		public uint PackedDirection;	//  4 B		polar 16:16 bits (azimuth high, elevation low)
	}
}