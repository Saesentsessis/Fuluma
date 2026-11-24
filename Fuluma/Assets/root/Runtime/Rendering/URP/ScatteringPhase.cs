namespace Saesentsessis.Fuluma.Rendering.URP
{
	/// <summary>
	/// How the haze around the lights scatters light toward the camera (the phase function): equally in every direction,
	/// or mostly onward, so beams look faint from the side and bright when looking toward their lamp.
	/// </summary>
	public enum ScatteringPhase
	{
		/// <summary>
		/// Equally in every direction: a beam looks equally bright from every side. The cheapest.
		/// </summary>
		Isotropic = 0,

		/// <summary>
		/// Henyey-Greenstein, the usual phase function of haze and fog, shaped by
		/// <see cref="FulumaRenderFeature.ScatteringAnisotropy"/>.
		/// </summary>
		HenyeyGreenstein = 1
	}
}
