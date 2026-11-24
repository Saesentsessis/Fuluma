using System;
using Saesentsessis.Fuluma.Core;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Saesentsessis.Fuluma.Rendering.Proxy
{
	/// <summary>
	/// The proxy mesh LODs fake spot lights are drawn with: per LOD the vertex rings of its cap and the screen size from
	/// which it is used. Assigned to the Fuluma renderer feature; without one, a built-in set of three LODs is used.
	/// </summary>
	[CreateAssetMenu(fileName = "New Spot Light Proxy Array", menuName = "Saesentsessis/Fuluma/Spot Light Proxy Array Descriptor")]
	public class SpotLightProxyArrayDescriptor : ScriptableObject
	{
		/// <summary>
		/// Most LODs culling can sort lights into.
		/// </summary>
		public const int MaxLodCount = 8;

		/// <summary>
		/// Finest first. Culling picks the first LOD whose screen size the light reaches; at most
		/// <see cref="MaxLodCount"/> are used.
		/// </summary>
		[Tooltip("Proxy LODs, finest first, with non-increasing screen sizes. A light uses the first LOD whose screen size it reaches; the last LOD takes every smaller light. At most 8 LODs are used.")]
		[SerializeField] private SpotLightProxyDescriptor[] lods =
		{
			new(0.3f, 16, 12, 9, 7, 4),
			new(0.15f, 12, 9, 7, 4),
			new(0.06f, 10, 7, 4),
			new(0.02f, 6, 4),
			new(0f, 3)
		};

		/// <summary>
		/// Number of LODs in the asset (culling uses at most <see cref="MaxLodCount"/>).
		/// </summary>
		public int LodCount => lods.Length;

		/// <summary>
		/// The LODs, finest first.
		/// </summary>
		public ReadOnlySpan<SpotLightProxyDescriptor> Lods => lods;

		/// <summary>
		/// Copies the LODs into native memory. The caller owns the result and must dispose it.
		/// </summary>
		/// <param name="allocator">Allocator of the result's arrays.</param>
		/// <returns>Every LOD's band resolutions, range and screen size.</returns>
		internal SpotLightProxyArrayData CreateData(Allocator allocator)
		{
			var data = new SpotLightProxyArrayData(
				new NativeArray<int>(CountBands(), allocator, NativeArrayOptions.UninitializedMemory),
				new NativeArray<BufferRange>(lods.Length, allocator, NativeArrayOptions.UninitializedMemory),
				new NativeArray<float>(lods.Length, allocator, NativeArrayOptions.UninitializedMemory));

			try
			{
				CopyLods(data);
				return data;
			}
			catch
			{
				data.Dispose();
				throw;
			}
		}

		/// <summary>
		/// Replaces the LODs (tests).
		/// </summary>
		internal void SetLods(SpotLightProxyDescriptor[] value) => lods = value;

		private int CountBands()
		{
			var count = 0;

			for (var i = 0; i < lods.Length; i++)
				count += lods[i].BandResolutions.Length;

			return count;
		}

		/// <summary>
		/// Writes every LOD's band resolutions back to back, its range within them and its screen size.
		/// </summary>
		private unsafe void CopyLods(in SpotLightProxyArrayData data)
		{
			var resolutions = (int*)data.Resolutions.GetUnsafePtr();
			var lodRanges = data.LodRanges; // views of the same memory
			var screenSizes = data.ScreenSizes;
			var offset = 0;

			for (var i = 0; i < lods.Length; i++)
			{
				var source = lods[i].BandResolutions;

				lodRanges[i] = new BufferRange(offset, source.Length);
				screenSizes[i] = lods[i].ScreenSize;

				fixed (int* sourcePtr = source)
					UnsafeUtility.MemCpy(resolutions + offset, sourcePtr, (long)source.Length * sizeof(int));

				offset += source.Length;
			}
		}
	}
}
