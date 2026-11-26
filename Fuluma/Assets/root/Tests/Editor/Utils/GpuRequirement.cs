using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Saesentsessis.Fuluma.Tests.Editor.Utils
{
	/// <summary>
	/// Skips tests that need a graphics device when there is none: <c>-nographics</c>, or a machine without a GPU. Batch
	/// mode with a GPU still runs them. Render tests are also skipped on Mesa's llvmpipe software rasterizer (see
	/// <see cref="IsLlvmpipe"/>).
	/// </summary>
	internal static class GpuRequirement
	{
		/// <summary>
		/// Whether there is a graphics device at all (buffers, readbacks, rendering).
		/// </summary>
		public static bool HasGraphicsDevice => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

		/// <summary>
		/// Whether the device can draw Fuluma's lights: compute support and structured buffers in the vertex stage.
		/// </summary>
		public static bool CanDrawFakeLights =>
			HasGraphicsDevice && SystemInfo.supportsComputeShaders && SystemInfo.maxComputeBufferInputsVertex >= 1;

		/// <summary>
		/// Whether the device is Mesa's llvmpipe software rasterizer (OpenGL, or Vulkan as lavapipe): what CI's GPU-less
		/// Linux runners render with. Mesa 23.2.1's (CI's Ubuntu 22.04 image) draws the glow up to ~20% off the exact
		/// integral at some pixels, where every real GPU tested and Mesa 26.3's llvmpipe match it (2026-11-26; cause not
		/// investigated: the integration's trig, the direction decode and the image orientation were ruled out).
		/// </summary>
		public static bool IsLlvmpipe => SystemInfo.graphicsDeviceName.IndexOf("llvmpipe", System.StringComparison.OrdinalIgnoreCase) >= 0;

		/// <summary>
		/// Ignores the current test without a graphics device.
		/// </summary>
		public static void IgnoreWithoutGraphicsDevice()
		{
			if (HasGraphicsDevice == false)
				Assert.Ignore("Needs a graphics device (run without -nographics).");
		}

		/// <summary>
		/// Ignores the current test unless the device can draw Fuluma's lights.
		/// </summary>
		public static void IgnoreUnlessFakeLightsDraw()
		{
			IgnoreWithoutGraphicsDevice();

			if (CanDrawFakeLights == false)
				Assert.Ignore("Needs a graphics device with compute and vertex-stage structured buffers.");

			if (IsLlvmpipe)
				Assert.Ignore("Render tests don't run on Mesa's llvmpipe software rasterizer: Mesa 23.2.1's (CI) draws the " +
				              "glow up to ~20% off the exact integral where real GPUs and Mesa 26.3 match it.");
		}
	}
}
