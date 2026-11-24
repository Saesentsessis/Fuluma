namespace Saesentsessis.Fuluma.Rendering.URP
{
	/// <summary>
	/// Where <see cref="FulumaRenderFeature"/> culls fake lights.
	/// </summary>
	internal enum CullingBackend
	{
		/// <summary>
		/// On the GPU where supported, otherwise on the CPU. Only CPU culling exists so far.
		/// </summary>
		PreferGPU,

		CPU
	}
}
