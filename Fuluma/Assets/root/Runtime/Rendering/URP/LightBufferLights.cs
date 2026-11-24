using UnityEngine;

namespace Saesentsessis.Fuluma.Rendering.URP
{
	/// <summary>
	/// Which lights a downsampled light buffer (<see cref="FulumaRenderFeature.LightBufferScale"/> below 1) shades.
	/// </summary>
	public enum LightBufferLights
	{
		/// <summary>
		/// Every light draws into the downsampled buffer.
		/// </summary>
		All = 0,

		/// <summary>
		/// Lights at least <see cref="FulumaRenderFeature.LargeLightScreenSize"/> on screen, and every light the camera is
		/// inside of, draw into the downsampled buffer; smaller lights draw at full resolution. One more raster pass than
		/// <see cref="All"/>. The full-resolution lights read depth as an input attachment where supported (else URP's
		/// depth texture); the buffer reads the camera's depth attachment, or URP's depth texture with MSAA.
		/// </summary>
		[InspectorName("Large Only")]
		LargeOnly = 1
	}
}
