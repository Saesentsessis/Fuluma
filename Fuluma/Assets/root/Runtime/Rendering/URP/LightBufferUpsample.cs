namespace Saesentsessis.Fuluma.Rendering.URP
{
	/// <summary>
	/// How a downsampled light buffer (<see cref="FulumaRenderFeature.LightBufferScale"/> below 1) is upscaled onto the camera.
	/// </summary>
	public enum LightBufferUpsample
	{
		/// <summary>
		/// Weights the 4 nearest buffer texels by bilinear weight and how close their depth is to each camera pixel's,
		/// with a checkerboard (nearest/farthest) depth downsample: no glow bleeding across depth edges. Per camera pixel,
		/// a depth read and a gather of the 4 buffer depths, then one filtered read where those depths agree, else 4 loads.
		/// </summary>
		DepthAware = 0,

		/// <summary>
		/// Plain bilinear filtering of the buffer, farthest depth per block: cheapest (1 filtered read per camera pixel),
		/// but glow bleeds over nearer geometry along depth edges.
		/// </summary>
		Bilinear = 1
	}
}
