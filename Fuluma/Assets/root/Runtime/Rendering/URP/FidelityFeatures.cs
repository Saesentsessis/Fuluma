using System;

namespace Saesentsessis.Fuluma.Rendering.URP
{
	/// <summary>
	/// Optional parts of how <see cref="FulumaRenderFeature"/> draws its lights; each trades quality against cost.
	/// </summary>
	[Flags]
	public enum FidelityFeatures
	{
		/// <summary>
		/// Every feature off.
		/// </summary>
		None = 0,

		/// <summary>
		/// Read the scene's depth so geometry hides the glow behind it and the glow stops at surfaces. Off: the lights
		/// ignore all geometry (their glow shows through walls, floors and characters), in exchange for no depth reads in
		/// the light shader, no depth texture, prepass or copy requested from URP, a light buffer without its depth
		/// downsample (always composited bilinearly; Light Buffer Timing has no effect) and, on tile-based GPUs, no split
		/// of URP's render pass. Off can also be slower: lights hidden behind geometry are shaded too, instead of being
		/// skipped. Changing it recreates the feature's pass.
		/// </summary>
		SceneDepth = 1 << 0,

		/// <summary>
		/// Limit lights by their clip boxes (Keep boxes such as rooms, Subtract boxes such as neighboring rooms). Costs
		/// nothing, and no GPU memory, while the store has no boxes. Off: boxes are ignored. Applies immediately.
		/// </summary>
		ClipBoxes = 1 << 1,

		/// <summary>
		/// Fade a light's glow out where its volume meets the camera's near clipping plane, which otherwise cuts it with a
		/// hard edge when the camera is close to the light. A few instructions on lights the camera is near or inside.
		/// Applies immediately.
		/// </summary>
		NearPlaneFade = 1 << 2,

		/// <summary>
		/// Pick a proxy mesh LOD per light by its size on screen. Off: every light uses LOD0, and culling skips LOD
		/// selection. Changing it recreates the feature's pass.
		/// </summary>
		SpotLightLods = 1 << 16,

		/// <summary>
		/// Cancellation-free cone intersections, exact near the axis of lights with an outer angle past about 160 degrees
		/// (the default intersection leaves holes or speckles there, worse the farther the camera is). A few more
		/// instructions per pixel on every light. Applies immediately.
		/// </summary>
		WideConePrecision = 1 << 17
	}
}
