using Unity.Mathematics;

namespace Saesentsessis.Fuluma.Utils
{
	/// <summary>
	/// Light directions as polar angles, and their 16:16 packing for the GPU.
	/// </summary>
	internal static partial class QuaternionUtils
	{
		/// <summary>
		/// Polar angles of the rotation's +z axis: azimuth <c>atan2(x, z)</c> in [-π, π] (x) and elevation
		/// <c>asin(y)</c> in [-π/2, π/2] (y), in radians. Roll is lost.
		/// </summary>
		public static float2 ToPolarRotation(in quaternion q)
		{
			var x = 2.0f * (q.value.x * q.value.z + q.value.w * q.value.y);
			var y = 2.0f * (q.value.y * q.value.z - q.value.w * q.value.x);
			var z = 1.0f - 2.0f * (math.square(q.value.x) + math.square(q.value.y));

			var azimuth = math.atan2(x, z);
			var elevation = math.asin(math.clamp(y, -1f, 1f));
			
			return new float2(azimuth, elevation);
		}

		/// <summary>
		/// Builds the rotation (without roll) whose +z axis has azimuth (x) and elevation (y), in radians: the inverse of
		/// <see cref="ToPolarRotation"/> for elevations inside (-π/2, π/2).
		/// </summary>
		/// <remarks>
		/// Yaw by the azimuth around +y, then pitch by minus the elevation around +x (a positive elevation tilts +z up
		/// toward +y).
		/// </remarks>
		public static quaternion ToQuaternion(in float2 polarRotation)
		{
			var halfAngles = polarRotation * 0.5f;

			math.sincos(halfAngles, out var sines, out var cosines);

			return new quaternion(-cosines.x * sines.y,
				sines.x * cosines.y,
				sines.x * sines.y,
				cosines.x * cosines.y);
		}

		/// <summary>
		/// Packs polar angles (azimuth in x, elevation in y, radians, as from <see cref="ToPolarRotation"/>) into one
		/// 32-bit integer: azimuth in the high 16 bits, elevation in the low 16 bits, each mapped to unorm16. The
		/// shader decodes it in <c>FulumaDecodePolarDirection</c>.
		/// </summary>
		public static uint PackPolar(in float2 polarRotation)
		{
			// Normalize from radians to [-1.0, 1.0].
			var azimuthNormalized = polarRotation.x / math.PI;
			var elevationNormalized = polarRotation.y / math.PIHALF;

			// Remap from [-1.0, 1.0] to [0.0, 1.0].
			var azimuth01 = azimuthNormalized * 0.5f + 0.5f;
			var elevation01 = elevationNormalized * 0.5f + 0.5f;
			
			return unchecked((uint)(azimuth01 * ushort.MaxValue) << 16 | (uint)(elevation01 * ushort.MaxValue));
		}
		
		/// <summary>
		/// Inverse of <see cref="PackPolar"/>: azimuth (x) and elevation (y) in radians, quantized to 16 bits each.
		/// </summary>
		public static float2 UnpackPolar(uint packed)
		{
			// Convert to [0.0, 1.0] float space.
			var az01 = (packed >> 16) / (float)ushort.MaxValue;
			var el01 = (packed & 0xFFFF) / (float)ushort.MaxValue;

			// Remap from [0.0, 1.0] to [-1.0, 1.0] and restore radians.
			var azimuth = (az01 * 2.0f - 1.0f) * math.PI;
			var elevation = (el01 * 2.0f - 1.0f) * (math.PI * 0.5f);

			return new float2(azimuth, elevation);
		}
	}
}