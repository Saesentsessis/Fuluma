namespace Saesentsessis.Fuluma.Rendering.URP
{
	/// <summary>
	/// How a light's glow fades with the distance from its lamp.
	/// </summary>
	public enum DistanceFalloff
	{
		/// <summary>
		/// Inverse square scaled by the light's range: a light looks the same at any size (doubling the range and every
		/// distance gives the same picture), so intensity is relative to the light's size.
		/// </summary>
		RangeScaled = 0,

		/// <summary>
		/// As URP's own lights: inverse square of the distance in world units, faded out toward the range with URP's
		/// window. The intensity means what a Unity Light's intensity means.
		/// </summary>
		Unity = 1
	}
}
