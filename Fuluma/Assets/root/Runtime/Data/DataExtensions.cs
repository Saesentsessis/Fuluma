using System.Runtime.CompilerServices;
using Saesentsessis.Fuluma.Utils;
using Unity.Mathematics;

namespace Saesentsessis.Fuluma.Data
{
	/// <summary>
	/// Packs authoring data (<see cref="Placement"/>, <see cref="SpotLightData"/>, <see cref="ClipBoxData"/>)
	/// into its GPU form.
	/// </summary>
	internal static class DataExtensions
	{
		/// <summary>
		/// Smallest half-extent a clip box packs with: below it the box is degenerate and packs to
		/// <see cref="PackedClipBoxData.Empty"/>. Its cube is also the smallest determinant the box-to-world matrix may
		/// have, which catches a placement scaled to (nearly) zero.
		/// </summary>
		public const float MinClipBoxHalfExtent = 1.0e-5f;

		/// <summary>
		/// Packs a position and the rotation's +z axis as polar angles (see
		/// <see cref="PackedTransformData.PackedDirection"/>). Roll is dropped.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static PackedTransformData Pack(this Placement transform)
		{
			return new PackedTransformData
			{
				Position = transform.Position,
				PackedDirection = QuaternionUtils.PackPolar(QuaternionUtils.ToPolarRotation(transform.Rotation))
			};
		}

		/// <summary>
		/// Packs a box placed by a static placement (position and rotation, no scale).
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static PackedClipBoxData Pack(this ClipBoxData box, in Placement placement)
			=> box.Pack(float4x4.TRS(placement.Position, placement.Rotation, new float3(1f)));

		/// <summary>
		/// Packs a box in the space of <paramref name="localToWorld"/> (any affine matrix: a <c>Transform</c>'s full
		/// matrix, scale and shear included): the inverse of box-to-world, which maps the box onto [-1, 1]³.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static PackedClipBoxData Pack(this ClipBoxData box, float4x4 localToWorld)
		{
			var halfExtents = math.abs((float3)box.Size) * 0.5f;
			if (math.any(halfExtents < MinClipBoxHalfExtent))
				return PackedClipBoxData.Empty;

			var boxToWorld = math.mul(localToWorld, float4x4.TRS(box.Center, quaternion.identity, halfExtents));

			// Zero (or near zero) scale in the placement: no volume, and no usable inverse.
			var determinant = math.determinant(new float3x3(boxToWorld.c0.xyz, boxToWorld.c1.xyz, boxToWorld.c2.xyz));
			if (math.abs(determinant) < MinClipBoxHalfExtent * MinClipBoxHalfExtent * MinClipBoxHalfExtent)
				return PackedClipBoxData.Empty;

			// General inverse: the box's half-extents and the placement's scale make it non-rigid.
			var worldToBox = math.inverse(boxToWorld);

			return new PackedClipBoxData
			{
				Row0 = new float4(worldToBox.c0.x, worldToBox.c1.x, worldToBox.c2.x, worldToBox.c3.x),
				Row1 = new float4(worldToBox.c0.y, worldToBox.c1.y, worldToBox.c2.y, worldToBox.c3.y),
				Row2 = new float4(worldToBox.c0.z, worldToBox.c1.z, worldToBox.c2.z, worldToBox.c3.z)
			};
		}

		/// <summary>
		/// Packs a spot light's parameters. The angles are clamped first (see
		/// <see cref="SpotLightData.OuterAngleDegrees"/> and <see cref="SpotLightData.InnerAngleDegrees"/>); the
		/// intensity is rounded to a half.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static PackedSpotLightData Pack(this SpotLightData data)
		{
			var halfAngles = ClampHalfAngles(data.InnerAngleDegrees, data.OuterAngleDegrees);
			var cosines = math.cos(math.radians(halfAngles));

			return new PackedSpotLightData
			{
				PackedColor = Utils.ColorUtils.ToPackedRGBA(data.Color),
				Range = data.Range,
				CosOuter = cosines.y,
				AngleScale = (half)(1f / math.max(1.0e-4f, cosines.x - cosines.y)),
				Intensity = (half)data.Intensity
			};
		}

		/// <summary>
		/// Packs two spot lights into <paramref name="outputPtr"/>[0] and [1].
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static unsafe void Pack2(in SpotLightData data0, in SpotLightData data1, PackedSpotLightData* outputPtr)
		{
			*outputPtr++ = data0.Pack();
			*outputPtr = data1.Pack();
		}

		/// <summary>
		/// Full cone angles (degrees) to half-angles (degrees), outer clamped to <see cref="SpotLightData.MaxOuterHalfAngleDegrees"/>
		/// and inner to [0, outer].
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static float2 ClampHalfAngles(float innerDegrees, float outerDegrees)
		{
			var outer = math.clamp(outerDegrees * 0.5f, 0f, SpotLightData.MaxOuterHalfAngleDegrees);
			var inner = math.clamp(innerDegrees * 0.5f, 0f, outer);
			return new float2(inner, outer);
		}
	}
}