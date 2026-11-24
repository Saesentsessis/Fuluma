namespace Saesentsessis.Fuluma.Data
{
	/// <summary>
	/// What a clip box referenced by a fake light does to the light's volume.
	/// </summary>
	/// <remarks>
	/// A light's region is the union of its <see cref="Keep"/> boxes (or unlimited when it has none) minus the union of
	/// its <see cref="Subtract"/> boxes. Neither role casts shadows: a subtract box only removes the glow inside itself.
	/// </remarks>
	public enum ClipBoxRole : byte
	{
		/// <summary>
		/// The light exists only inside its keep boxes (for example the room it is in).
		/// </summary>
		Keep = 0,

		/// <summary>
		/// The light is removed inside this box (for example a neighbouring room, or the notch of an L-shaped room).
		/// </summary>
		Subtract = 1
	}
}
