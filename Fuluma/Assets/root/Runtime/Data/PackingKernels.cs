using Unity.Burst;
using Unity.Mathematics;

namespace Saesentsessis.Fuluma.Data
{
	/// <summary>
	/// Burst-compiled entry points for the packing the store does on the main thread when one light or box is added or
	/// changed. The math is <see cref="DataExtensions"/>' (the same the store's jobs run); only the compiler differs: as
	/// plain C# (Mono, IL2CPP) the <c>Unity.Mathematics</c> code costs 0.2 to 2 µs per call, through Burst tens of ns.
	/// </summary>
	/// <remarks>
	/// Direct calls from managed code (structs by <c>in</c>/<c>ref</c>/<c>out</c>). Without Burst (disabled in the Editor,
	/// or a platform it doesn't support) the same methods run as C#, with the same results.
	/// </remarks>
	[BurstCompile]
	internal static class PackingKernels
	{
		/// <summary>
		/// <see cref="DataExtensions.Pack(SpotLightData)"/>.
		/// </summary>
		[BurstCompile(CompileSynchronously = true)]
		public static void PackSpotLight(in SpotLightData light, out PackedSpotLightData packed)
			=> packed = light.Pack();

		/// <summary>
		/// Everything a new spot light needs: packed light, packed transform, culling data and bounding sphere.
		/// </summary>
		[BurstCompile(CompileSynchronously = true)]
		public static void PlaceNewSpotLight(in Placement placement, in SpotLightData light, out PackedSpotLightData packed,
			out PackedTransformData transform, out SpotLightCullingData culling, out float4 sphere)
		{
			packed = light.Pack();
			transform = placement.Pack();
			culling = new SpotLightCullingData(placement, packed);
			sphere = culling.BoundingSphere();
		}

		/// <summary>
		/// Moves a spot light: its packed transform, and its culling data placed anew with the bounding sphere.
		/// </summary>
		[BurstCompile(CompileSynchronously = true)]
		public static void PlaceSpotLight(in Placement placement, ref SpotLightCullingData culling,
			out PackedTransformData transform, out float4 sphere)
		{
			transform = placement.Pack();
			culling.Place(placement.Position, placement.Rotation);
			sphere = culling.BoundingSphere();
		}

		/// <summary>
		/// Gives a spot light new parameters: packed light, and its culling data reshaped with the bounding sphere.
		/// </summary>
		[BurstCompile(CompileSynchronously = true)]
		public static void ReshapeSpotLight(in SpotLightData light, ref SpotLightCullingData culling,
			out PackedSpotLightData packed, out float4 sphere)
		{
			packed = light.Pack();
			culling.Reshape(packed);
			sphere = culling.BoundingSphere();
		}

		/// <summary>
		/// A box at a static placement: the placement's matrix (what the store keeps) and the packed box.
		/// </summary>
		[BurstCompile(CompileSynchronously = true)]
		public static void PlaceClipBox(in ClipBoxData box, in Placement placement, out float4x4 matrix,
			out PackedClipBoxData packed)
		{
			matrix = placement.ToMatrix();
			packed = box.Pack(matrix);
		}

		/// <summary>
		/// <see cref="DataExtensions.Pack(ClipBoxData, float4x4)"/>.
		/// </summary>
		[BurstCompile(CompileSynchronously = true)]
		public static void PackClipBox(in ClipBoxData box, in float4x4 placement, out PackedClipBoxData packed)
			=> packed = box.Pack(placement);
	}
}
