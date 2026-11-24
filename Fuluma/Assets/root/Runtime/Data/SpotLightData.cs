using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Saesentsessis.Fuluma.Data
{
	/// <summary>
	/// Authoring parameters of one fake spot light. Packed into <see cref="PackedSpotLightData"/> for the GPU.
	/// </summary>
	[Serializable, StructLayout(LayoutKind.Sequential)]
	public struct SpotLightData : IEquatable<SpotLightData>
	{
		/// <summary>
		/// Largest supported outer half-angle, in degrees (a full outer angle of 179.8). Past 90 degrees the volume is no
		/// longer convex, and the enclosing proxy widens its cone by atan(tan(angle) / cos(pi / N)), which diverges near
		/// 90 degrees.
		/// </summary>
		public const float MaxOuterHalfAngleDegrees = 89.9f;

		/// <summary>
		/// Sensible starting values (C# structs can't have field initializers).
		/// </summary>
		public static SpotLightData Default => new()
		{
			Color = new Color32(255, 255, 255, 255),
			Intensity = 1f,
			Range = 5f,
			InnerAngleDegrees = 20f,
			OuterAngleDegrees = 30f
		};

		/// <summary>
		/// Light color. Alpha is an extra 0..1 intensity multiplier.
		/// </summary>
		[Tooltip("Light color. Alpha is an extra 0..1 intensity multiplier.")]
		public Color32 Color;

		/// <summary>
		/// HDR brightness, stored as a half on the GPU.
		/// </summary>
		[Tooltip("HDR brightness (stored as a half on the GPU).")]
		[Min(0f)] public float Intensity;

		/// <summary>
		/// Range: distance from the apex at which the light fades to zero and the volume ends.
		/// </summary>
		[Tooltip("Range: distance from the apex at which the light fades to zero and the volume ends.")]
		[Min(0f)] public float Range;

		/// <summary>
		/// Full cone angle (degrees) where the angular falloff starts. Its half-angle is clamped to
		/// [0, outer half-angle].
		/// </summary>
		[Tooltip("Full cone angle (degrees) where the angular falloff starts. Clamped to at most the outer angle.")]
		public float InnerAngleDegrees;

		/// <summary>
		/// Full cone angle (degrees) of the visible volume. Its half-angle is clamped to
		/// [0, <see cref="MaxOuterHalfAngleDegrees"/>].
		/// </summary>
		// The number must match MaxOuterHalfAngleDegrees (attribute arguments can't format a float).
		[Tooltip("Full cone angle (degrees) of the visible volume. The half-angle is clamped to at most 89.9 degrees "
		         + "(a full angle of 179.8).")]
		public float OuterAngleDegrees;

		public bool Equals(SpotLightData other)
		{
			return Color.Equals(other.Color)
			       && Intensity.Equals(other.Intensity)
			       && Range.Equals(other.Range)
			       && InnerAngleDegrees.Equals(other.InnerAngleDegrees)
			       && OuterAngleDegrees.Equals(other.OuterAngleDegrees);
		}

		public override bool Equals(object obj)
		{
			return obj is SpotLightData other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(Color, Intensity, Range, InnerAngleDegrees, OuterAngleDegrees);
		}

		public static bool operator ==(SpotLightData left, SpotLightData right)
		{
			return left.Equals(right);
		}

		public static bool operator !=(SpotLightData left, SpotLightData right)
		{
			return !left.Equals(right);
		}
	}
}
