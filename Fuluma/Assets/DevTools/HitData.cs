using Unity.Mathematics;

namespace Saesentsessis.Fuluma.Tests
{
	/// <summary>
	/// One ray hit of <see cref="ConeIntersectionTester"/>; <c>default</c> means no hit.
	/// </summary>
	public struct HitData
	{
		public float3 Position;
		public float DistanceFromOrigin;

		public bool IsValid => DistanceFromOrigin != 0;
	}
}
