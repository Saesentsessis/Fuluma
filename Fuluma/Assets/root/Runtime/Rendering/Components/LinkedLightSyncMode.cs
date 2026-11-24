namespace Saesentsessis.Fuluma.Rendering.Components
{
	/// <summary>
	/// When a <see cref="LinkedFakeLight"/> copies its Unity <c>Light</c>'s values.
	/// </summary>
	public enum LinkedLightSyncMode
	{
		/// <summary>
		/// Checked every frame, right before the light data is uploaded; only changes are pushed. Costs a few native
		/// property reads per light and frame.
		/// </summary>
		EveryFrame = 0,

		/// <summary>
		/// Checked every frame in the Editor outside play mode; in play mode (and in players) only on enable and when
		/// <see cref="LinkedFakeLight.Refresh"/> is called. No per-frame cost at runtime.
		/// </summary>
		EditorOnly = 1
	}
}
