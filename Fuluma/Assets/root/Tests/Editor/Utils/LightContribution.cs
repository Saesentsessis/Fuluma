using System.Linq;
using UnityEngine;

namespace Saesentsessis.Fuluma.Tests.Editor.Utils
{
	/// <summary>
	/// The fake lights' contribution: a render with the lights minus the same render without them.
	/// </summary>
	internal sealed class LightContribution
	{
		private readonly Color[] _pixels;
		private readonly int _size;

		public LightContribution(Color[] pixels, int size)
		{
			_pixels = pixels;
			_size = size;
		}

		public Color Center => _pixels[_size / 2 * _size + _size / 2];

		public Color LeftEdge => _pixels[_size / 2 * _size + 1];

		public int Size => _size;

		public float MaxLuminance => _pixels.Max(TestImages.Luminance);

		/// <summary>
		/// NaN, infinite or clearly negative pixels: additive glow can only ever brighten.
		/// </summary>
		public int BadPixelCount => _pixels.Count(c =>
			float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsNaN(c.b)
			|| float.IsInfinity(c.r) || float.IsInfinity(c.g) || float.IsInfinity(c.b)
			|| TestImages.Luminance(c) < -1e-3f);

		public Color Pixel(int x, int y) => _pixels[y * _size + x];

		/// <summary>
		/// Reinhard tonemap to gamma-space LDR, for pictures.
		/// </summary>
		public Color[] Tonemapped() => TestImages.Tonemap(_pixels);

		public void SavePng(string path) => TestImages.WritePng(path, Tonemapped(), _size, _size);
	}
}
