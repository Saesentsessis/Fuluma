using System;
using Saesentsessis.Fuluma.Core;
using Unity.Collections;

namespace Saesentsessis.Fuluma.Rendering.Proxy
{
	/// <summary>
	/// Band resolutions and switch sizes of every proxy LOD, flattened into native memory for mesh generation.
	/// </summary>
	/// <remarks>
	/// The caller owns it and must <see cref="Dispose"/> it. Instances only live while the proxy mesh is built.
	/// There is deliberately no shared static instance: a struct copy of a static disposed anywhere would leave
	/// every other copy pointing at freed memory, and a static allocation is never freed before a domain reload.
	/// </remarks>
	internal struct SpotLightProxyArrayData : IDisposable
	{
		/// <summary>
		/// Creates the built-in LOD set used when no <see cref="SpotLightProxyArrayDescriptor"/> is assigned.
		/// </summary>
		/// <param name="allocator">Allocator of the result's arrays; the caller disposes the result.</param>
		public static SpotLightProxyArrayData CreateDefault(Allocator allocator)
		{
			ReadOnlySpan<int> lod0 = stackalloc int[] { 16, 12, 9, 7, 4 };
			ReadOnlySpan<int> lod1 = stackalloc int[] { 12, 9, 7, 4 };
			ReadOnlySpan<int> lod2 = stackalloc int[] { 10, 7, 4 };

			var resolutions = new NativeArray<int>(lod0.Length + lod1.Length + lod2.Length, allocator, NativeArrayOptions.UninitializedMemory);
			var lodRanges = new NativeArray<BufferRange>(3, allocator, NativeArrayOptions.UninitializedMemory);
			var screenSizes = new NativeArray<float>(3, allocator, NativeArrayOptions.UninitializedMemory);

			var offset = 0;
			Append(lod0, 0, 0.25f);
			Append(lod1, 1, 0.08f);
			Append(lod2, 2, 0f);

			return new SpotLightProxyArrayData(resolutions, lodRanges, screenSizes);

			void Append(ReadOnlySpan<int> bands, int lod, float screenSize)
			{
				for (var i = 0; i < bands.Length; i++)
					resolutions[offset + i] = bands[i];

				lodRanges[lod] = new BufferRange(offset, bands.Length);
				screenSizes[lod] = screenSize;
				offset += bands.Length;
			}
		}

		private NativeArray<int> _resolutions;
		private NativeArray<BufferRange> _lodRanges;
		private NativeArray<float> _screenSizes;

		/// <summary>
		/// Takes ownership of the three arrays: <see cref="Dispose"/> frees them.
		/// </summary>
		/// <param name="resolutions">Every LOD's band resolutions, back to back.</param>
		/// <param name="lodRanges">Each LOD's range in <paramref name="resolutions"/>.</param>
		/// <param name="screenSizes">Each LOD's switch size (see <see cref="GetScreenSize"/>).</param>
		/// <exception cref="ArgumentException"><paramref name="lodRanges"/> and <paramref name="screenSizes"/> differ in
		/// length.</exception>
		public SpotLightProxyArrayData(NativeArray<int> resolutions, NativeArray<BufferRange> lodRanges, NativeArray<float> screenSizes)
		{
			if (lodRanges.Length != screenSizes.Length)
				throw new ArgumentException($"{lodRanges.Length} LOD ranges but {screenSizes.Length} screen sizes.", nameof(screenSizes));

			_resolutions = resolutions;
			_lodRanges = lodRanges;
			_screenSizes = screenSizes;
		}

		/// <summary>
		/// Whether any of the arrays is allocated (false on <c>default</c> and after <see cref="Dispose"/>).
		/// </summary>
		public readonly bool IsCreated => _resolutions.IsCreated || _lodRanges.IsCreated || _screenSizes.IsCreated;

		/// <summary>
		/// Number of LODs.
		/// </summary>
		public readonly int Length => _lodRanges.Length;

		/// <summary>
		/// Same as <see cref="Get"/>.
		/// </summary>
		public SpotLightProxyData this[int index] => Get(index);

		internal readonly NativeArray<int> Resolutions => _resolutions;

		internal readonly NativeArray<BufferRange> LodRanges => _lodRanges;

		internal readonly NativeArray<float> ScreenSizes => _screenSizes;

		/// <summary>
		/// Smallest screen size (bounding-sphere diameter over screen height) a light is drawn with LOD
		/// <paramref name="index"/> at; smaller lights use the next LOD. The last LOD takes every smaller light.
		/// </summary>
		public readonly float GetScreenSize(int index) => _screenSizes[index];

		/// <summary>
		/// LOD <paramref name="index"/> (0 = finest), as a read-only view valid until <see cref="Dispose"/>.
		/// </summary>
		public readonly SpotLightProxyData Get(int index)
		{
			var range = _lodRanges[index];

			return new SpotLightProxyData(_resolutions.GetSubArray(range.Index, range.Length).AsReadOnly());
		}

		/// <summary>
		/// Frees every array. Safe to call more than once and on <c>default</c>.
		/// </summary>
		public void Dispose()
		{
			if (_resolutions.IsCreated)
				_resolutions.Dispose();

			if (_lodRanges.IsCreated)
				_lodRanges.Dispose();

			if (_screenSizes.IsCreated)
				_screenSizes.Dispose();

			this = default;
		}

		/// <summary>
		/// Band resolutions of one LOD as a sub-array of the owned memory (valid until <see cref="Dispose"/>).
		/// </summary>
		internal readonly NativeArray<int> GetBandResolutions(int index)
		{
			var range = _lodRanges[index];
			return _resolutions.GetSubArray(range.Index, range.Length);
		}
	}
}
