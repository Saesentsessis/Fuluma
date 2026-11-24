using Unity.Collections;

namespace Saesentsessis.Fuluma.Rendering.Proxy
{
	/// <summary>
	/// One proxy LOD of a <see cref="SpotLightProxyArrayData"/>: a read-only view of its band resolutions.
	/// </summary>
	internal struct SpotLightProxyData
	{
		/// <summary>
		/// Wraps a view of one LOD's band resolutions.
		/// </summary>
		public SpotLightProxyData(NativeArray<int>.ReadOnly bandResolutions)
		{
			BandResolutions = bandResolutions;
		}

		/// <summary>
		/// Vertices per cap ring, from the rim toward the cap tip. Valid until the owning
		/// <see cref="SpotLightProxyArrayData"/> is disposed.
		/// </summary>
		public NativeArray<int>.ReadOnly BandResolutions { get; }
	}
}