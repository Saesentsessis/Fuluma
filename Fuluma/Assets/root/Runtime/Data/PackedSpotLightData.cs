using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace Saesentsessis.Fuluma.Data
{
	/// <summary>
	/// GPU layout of one fake spot light's parameters. Mirrors <c>PackedSpotLightData</c> in
	/// <c>Shaders/Analytical/SpotLight/FulumaSpotLightInput.hlsl</c>: keep both in sync.
	/// </summary>
	/// <remarks>
	/// Fields are ordered so there is no padding: 4 + 4 + 4 + 2 + 2 = 16 B.
	/// Radiance = rgb * <see cref="Intensity"/> * (a / 255).
	/// </remarks>
	[StructLayout(LayoutKind.Sequential, Size = Size)]
	internal struct PackedSpotLightData : IEquatable<PackedSpotLightData>
	{
		/// <summary>
		/// Size of the struct in bytes, on the CPU and the GPU.
		/// </summary>
		public const int Size = 16;

		/// <summary>Field-by-field equality.</summary>
		public static bool operator ==(PackedSpotLightData left, PackedSpotLightData right)
		{
			return left.Equals(right);
		}

		/// <summary>Field-by-field inequality.</summary>
		public static bool operator !=(PackedSpotLightData left, PackedSpotLightData right)
		{
			return !left.Equals(right);
		}

		/// <summary>
		/// RGBA8 in raw <see cref="UnityEngine.Color32"/> byte order: R in the lowest byte (0xAABBGGRR). A is an
		/// intensity multiplier.
		/// </summary>
		public uint PackedColor;    //  4 B  RGBA8, raw Color32 byte order: R in the lowest byte (0xAABBGGRR). A is an intensity multiplier.

		/// <summary>
		/// Range in world units (<see cref="SpotLightData.Range"/>).
		/// </summary>
		public float Range;         //  4 B

		/// <summary>
		/// Cosine of the outer half-angle; geometry always uses the outer angle.
		/// </summary>
		public float CosOuter;      //  4 B  cosine of the outer half-angle; geometry always uses the outer angle

		/// <summary>
		/// <c>1 / max(1e-4, cosInner - cosOuter)</c>: scales the angular falloff
		/// <c>saturate((cos - cosOuter) * AngleScale)²</c>.
		/// </summary>
		public half AngleScale;     //  2 B  1 / max(1e-4, cosInner - cosOuter)

		/// <summary>
		/// HDR brightness.
		/// </summary>
		public half Intensity;      //  2 B  HDR brightness

		/// <inheritdoc/>
		public bool Equals(PackedSpotLightData other)
		{
			return PackedColor == other.PackedColor
			       && Range.Equals(other.Range)
			       && CosOuter.Equals(other.CosOuter)
			       && AngleScale.value == other.AngleScale.value
			       && Intensity.value == other.Intensity.value;
		}

		/// <inheritdoc/>
		public override bool Equals(object obj)
		{
			return obj is PackedSpotLightData other && Equals(other);
		}

		/// <inheritdoc/>
		public override int GetHashCode()
		{
			return HashCode.Combine(PackedColor, Range, CosOuter, AngleScale.value, Intensity.value);
		}
	}
}
