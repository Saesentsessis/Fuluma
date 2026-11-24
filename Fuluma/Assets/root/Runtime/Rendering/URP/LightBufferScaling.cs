using Unity.Mathematics;
using UnityEngine;

namespace Saesentsessis.Fuluma.Rendering.URP
{
	/// <summary>
	/// Limits of the resolution scale fake lights are shaded at (<see cref="FulumaRenderFeature.LightBufferScale"/>):
	/// a fraction of the camera's width and height. Below <see cref="Full"/> the lights draw into a smaller buffer (with a
	/// matching downsampled scene depth), which is then upscaled onto the camera color.
	/// </summary>
	public static class LightBufferScaling
	{
		/// <summary>
		/// Full resolution: the lights draw straight onto the camera, with no light buffer.
		/// </summary>
		public const float Full = 1f;

		/// <summary>
		/// Smallest scale (1% per axis).
		/// </summary>
		public const float Min = 0.01f;

		/// <summary>
		/// Default screen size (bounding-sphere diameter over screen height) from which a light counts as large for
		/// <see cref="LightBufferLights.LargeOnly"/>.
		/// </summary>
		public const float DefaultLargeLightScreenSize = 0.15f;

		/// <summary>
		/// Smallest screen size <see cref="LightBufferLights.LargeOnly"/> accepts.
		/// </summary>
		public const float MinLargeLightScreenSize = 0.01f;

		/// <summary>
		/// Clamps <paramref name="scale"/> to [<see cref="Min"/>, <see cref="Full"/>]; NaN becomes full resolution.
		/// </summary>
		internal static float Clamp(float scale) => float.IsNaN(scale) ? Full : math.clamp(scale, Min, Full);

		/// <summary>
		/// Whether <paramref name="scale"/> draws into a downsampled buffer.
		/// </summary>
		internal static bool IsDownsampled(float scale) => Clamp(scale) < Full;
	}
}
