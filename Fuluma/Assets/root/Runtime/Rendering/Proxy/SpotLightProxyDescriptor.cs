using System;
using UnityEngine;

namespace Saesentsessis.Fuluma.Rendering.Proxy
{
	/// <summary>
	/// One proxy LOD: its cap bands (band 0 is the rim, shared with the cone side) and when it's used.
	/// </summary>
	[Serializable]
	public class SpotLightProxyDescriptor
	{
		[Tooltip("Vertices per ring, from the rim (also the cone side's resolution) toward the cap tip. At least 3 each.")]
		[SerializeField, Range(3, ushort.MaxValue)] private int[] bandResolutions;

		[Tooltip("Smallest screen size (light's bounding-sphere diameter over the screen height) drawn with this LOD; smaller lights use the next one. Ignored on the last LOD.")]
		[SerializeField, Min(0f)] private float screenSize;

		/// <summary>
		/// A LOD used from <paramref name="screenSize"/> on, with the given cap bands.
		/// </summary>
		/// <param name="screenSize">See <see cref="ScreenSize"/>.</param>
		/// <param name="bandResolutions">See <see cref="BandResolutions"/>.</param>
		public SpotLightProxyDescriptor(float screenSize, params int[] bandResolutions)
		{
			this.bandResolutions = bandResolutions;
			this.screenSize = screenSize;
		}

		/// <summary>
		/// Smallest screen size (bounding-sphere diameter over screen height) drawn with this LOD.
		/// </summary>
		public float ScreenSize => screenSize;

		/// <summary>
		/// Vertices per ring, from the rim (also the cone side's resolution) toward the cap tip.
		/// </summary>
		public ReadOnlySpan<int> BandResolutions => bandResolutions;
	}
}
