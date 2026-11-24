namespace Saesentsessis.Fuluma.Core
{
	/// <summary>
	/// Where the transform of a fake light comes from.
	/// </summary>
	public enum LightTransformMode : byte
	{
		/// <summary>
		/// Transform is set from code and changes only when explicitly updated.
		/// </summary>
		Static = 0,

		/// <summary>
		/// Transform is read from a <see cref="UnityEngine.Transform"/> component every frame.
		/// </summary>
		Dynamic = 1
	}
}
