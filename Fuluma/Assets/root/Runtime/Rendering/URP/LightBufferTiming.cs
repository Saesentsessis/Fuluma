namespace Saesentsessis.Fuluma.Rendering.URP
{
	/// <summary>
	/// When a downsampled light buffer is shaded, relative to URP's opaque pass.
	/// </summary>
	public enum LightBufferTiming
	{
		/// <summary>
		/// Before the opaques when the renderer's settings make URP render a full depth prepass for the camera anyway
		/// (Depth Texture Mode "Force Prepass" with a depth texture, or depth priming), else after them. Never adds a
		/// prepass. Prepasses that other features cause (SSAO, depth normals) can't be detected: use
		/// <see cref="BeforeOpaques"/> for those. Deferred rendering and the Scene view always draw after the opaques.
		/// </summary>
		Auto,

		/// <summary>
		/// After the opaques, from their depth. On tile-based GPUs this splits URP's opaque render pass (the camera's
		/// color and depth are stored and reloaded around the buffer).
		/// </summary>
		AfterOpaques,

		/// <summary>
		/// Before the opaques, from a depth prepass, which this requests (URP draws every opaque object twice). Only the
		/// composite runs after the opaques, inside URP's opaque render pass.
		/// </summary>
		BeforeOpaques
	}
}
