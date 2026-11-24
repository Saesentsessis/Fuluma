namespace Saesentsessis.Fuluma.Rendering.Culling
{
	/// <summary>
	/// What <see cref="SpotLightCullingJob"/> decides for one light.
	/// </summary>
	internal enum SpotLightVisibility : byte
	{
		/// <summary>
		/// No part of the light's volume is in the frustum: not drawn.
		/// </summary>
		Culled,

		/// <summary>
		/// The camera may touch the proxy: shader pass 0 (back faces, no depth test).
		/// </summary>
		Inside,

		/// <summary>
		/// The camera is clearly outside the proxy: shader pass 1 (front faces, depth-tested).
		/// </summary>
		Outside
	}
}
