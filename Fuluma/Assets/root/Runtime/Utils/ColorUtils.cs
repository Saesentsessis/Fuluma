using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Saesentsessis.Fuluma.Utils
{
	/// <summary>
	/// Color packing shared with the shaders.
	/// </summary>
	internal static class ColorUtils
	{
		/// <summary>
		/// Packs a color as RGBA8 in raw <see cref="Color32"/> byte order: R in the lowest byte, A in the highest
		/// (0xAABBGGRR as a little-endian uint). HLSL decodes it with <c>(c >> 0/8/16/24) &amp; 0xFF</c>.
		/// </summary>
		public static uint ToPackedRGBA(Color32 color)
		{
			return UnsafeUtility.As<Color32, uint>(ref color);
		}

		/// <summary>
		/// Inverse of <see cref="ToPackedRGBA"/>.
		/// </summary>
		public static Color32 FromPackedRGBA(uint packed)
		{
			return UnsafeUtility.As<uint, Color32>(ref packed);
		}
	}
}
