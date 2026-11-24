using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Saesentsessis.Fuluma.Tests.Editor.Utils
{
	/// <summary>
	/// Skips tests that need a graphics device when there is none: <c>-nographics</c> (CI), or a machine without a GPU.
	/// Batch mode with a GPU still runs them.
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
		}
	}
}
