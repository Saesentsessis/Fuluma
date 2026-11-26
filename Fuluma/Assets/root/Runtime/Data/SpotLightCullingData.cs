using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;

namespace Saesentsessis.Fuluma.Data
{
	/// <summary>
	/// What CPU culling needs of one spot light, unpacked (the GPU copy's direction is packed into 16:16 polar angles).
	/// </summary>
	/// <remarks>
	/// Kept by each pool next to its packed data, index-aligned, with a bounding sphere in a separate list (see
	/// <c>SpotLightPool</c>) so the first culling pass streams 16 B per light.
	/// <para>
	/// A light that emits nothing (zero intensity, zero alpha or a black color) keeps a negative range here
	/// (<see cref="CullingRange"/>). Its bounding sphere then has radius <see cref="DarkRadius"/>, which every frustum
	/// plane test rejects, so culling skips it without a test of its own; moving the light keeps that, reshaping it
	/// recomputes it. Both markers are finite: the jobs compile with fast math, which assumes no infinities.
	/// </para>
	/// </remarks>
	[StructLayout(LayoutKind.Sequential, Size = Size)]
	internal struct SpotLightCullingData
	{
		/// <summary>
		/// Size of the struct in bytes.
		/// </summary>
		public const int Size = 32;

		/// <summary>
		/// Bounding sphere radius of a light that emits nothing: farther below zero than any plane distance, so every
		/// frustum plane test (<c>distance &lt; -radius</c>) rejects it; its square still fits a float.
		/// </summary>
		public const float DarkRadius = -1e18f;

		/// <summary>
		/// Range kept for a light that emits nothing (any negative range marks it).
		/// </summary>
		private const float DarkRange = -1f;

		/// <summary>
		/// Light axis of a rotation: its forward (+z), as the GPU decodes it.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float3 Direction(quaternion rotation) => math.mul(rotation, new float3(0f, 0f, 1f));

		/// <inheritdoc cref="Direction(quaternion)"/>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float3 Direction(Quaternion rotation) => Direction((quaternion)rotation);

		/// <summary>
		/// Whether <paramref name="light"/> adds any light: radiance is rgb × intensity × alpha.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool Emits(in PackedSpotLightData light)
			=> (float)light.Intensity > 0f && (light.PackedColor & 0xFF000000u) != 0u && (light.PackedColor & 0x00FFFFFFu) != 0u;

		/// <summary>
		/// The range culling keeps for <paramref name="light"/>: its own, or a negative one when it emits nothing (see
		/// remarks).
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float CullingRange(in PackedSpotLightData light) => Emits(light) ? light.Range : DarkRange;

		/// <summary>
		/// Apex (xyz) and range (w).
		/// </summary>
		public float4 PositionRange;

		/// <summary>
		/// Unit cone axis (xyz) and cosine of the outer half-angle (w).
		/// </summary>
		public float4 DirectionCosOuter;

		/// <param name="position">Apex in world space.</param>
		/// <param name="direction">Unit cone axis.</param>
		/// <param name="range">Range of the light.</param>
		/// <param name="cosOuter">Cosine of the outer half-angle.</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public SpotLightCullingData(float3 position, float3 direction, float range, float cosOuter)
		{
			PositionRange = new float4(position, range);
			DirectionCosOuter = new float4(direction, cosOuter);
		}

		/// <summary>
		/// Culling data of <paramref name="light"/> placed at <paramref name="placement"/>.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal SpotLightCullingData(in Placement placement, in PackedSpotLightData light)
			: this(placement.Position, Direction(placement.Rotation), CullingRange(light), light.CosOuter)
		{
		}

		/// <summary>
		/// Whether this light emits nothing, so culling skips it (see remarks).
		/// </summary>
		public readonly bool IsDark => PositionRange.w < 0f;

		/// <summary>
		/// Smallest sphere around the light's volume: a cone of the outer half-angle capped by the range sphere.
		/// </summary>
		/// <returns>Center (xyz) and radius (w); radius <see cref="DarkRadius"/> for a light that emits nothing
		/// (<see cref="IsDark"/>).</returns>
		/// <remarks>
		/// From 45 degrees up, the sphere through the cap's rim circle also holds the apex: center at the rim's height,
		/// radius the rim radius. Narrower cones need the sphere through apex and rim: center at r / (2 cos), radius the
		/// same. Both contain the whole cap (its points are no farther than the rim).
		/// </remarks>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public readonly float4 BoundingSphere()
		{
			var position = PositionRange.xyz;
			var range = PositionRange.w;
			var direction = DirectionCosOuter.xyz;
			var cosOuter = DirectionCosOuter.w;

			if (IsDark)
				return new float4(position, DarkRadius);

			if (cosOuter <= math.SQRT2 * 0.5f)
			{
				var sinOuter = math.sqrt(math.saturate(1f - cosOuter * cosOuter));
				return new float4(position + direction * (range * cosOuter), range * sinOuter);
			}

			var radius = range / (2f * cosOuter);
			return new float4(position + direction * radius, radius);
		}

		/// <summary>
		/// Moves the light: a new apex and axis, the same range and cone.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal void Place(float3 position, quaternion rotation)
		{
			PositionRange.xyz = position;
			DirectionCosOuter.xyz = Direction(rotation);
		}

		/// <summary>
		/// Reshapes the light: the range and cone of <paramref name="light"/> (a negative range if it emits nothing), the
		/// same apex and axis.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal void Reshape(in PackedSpotLightData light)
		{
			PositionRange.w = CullingRange(light);
			DirectionCosOuter.w = light.CosOuter;
		}
	}
}
