namespace Saesentsessis.Fuluma.Rendering.URP
{
	/// <summary>
	/// How bright a light gets at its lamp.
	/// </summary>
	public enum LampGlare
	{
		/// <summary>
		/// The lamp has a small size (2% of the range), which keeps the glow next to it finite.
		/// </summary>
		None = 0,

		/// <summary>
		/// A point lamp: the glow keeps growing toward it, so rays passing close to the lamp glare.
		/// </summary>
		Point = 1
	}
}
