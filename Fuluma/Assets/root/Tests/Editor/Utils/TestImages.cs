using System.IO;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;

namespace Saesentsessis.Fuluma.Tests.Editor.Utils
{
	/// <summary>
	/// Helpers for rendered test images.
	/// </summary>
	internal static class TestImages
	{
		public static float Luminance(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

		/// <summary>
		/// Reinhard tonemap to gamma-space LDR, for pictures.
		/// </summary>
		public static Color[] Tonemap(Color[] pixels) => pixels.Select(c =>
		{
			var r = math.max(c.r, 0f);
			var g = math.max(c.g, 0f);
			var b = math.max(c.b, 0f);
			return new Color(r / (1f + r), g / (1f + g), b / (1f + b), 1f).gamma;
		}).ToArray();

		public static void WritePng(string path, Color[] pixels, int width, int height)
		{
			var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
			texture.SetPixels(pixels);
			texture.Apply();

			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllBytes(path, texture.EncodeToPNG());
			Object.DestroyImmediate(texture);
		}
	}
}
