using Saesentsessis.Fuluma.Data;
using Unity.Mathematics;
using UnityEngine;

namespace Saesentsessis.Fuluma.Rendering.Proxy
{
	/// <summary>
	/// How the vertex shader shapes one proxy LOD around a light, passed as <c>_FulumaSpotProxyShape</c>.
	/// </summary>
	internal readonly struct SpotLightProxyShape
	{
		/// <summary>
		/// C# copy of <c>FulumaSpotProxyVertex</c>: proxy space to light space (light at the origin, axis +z).
		/// </summary>
		public static float3 MapVertex(float3 proxy, float range, float widenedHalfAngle, float inflation)
		{
			if (proxy.z < 0f)
				return float3.zero;

			math.sincos((1f - proxy.z) * widenedHalfAngle, out var sinPolar, out var cosPolar);
			return range * inflation * new float3(proxy.xy * sinPolar, cosPolar);
		}

		/// <summary>
		/// cos(pi / n) for the n rim vertices: the cone side is widened by it so its flat faces enclose the round cone.
		/// </summary>
		public readonly float CosSegment;

		/// <summary>
		/// Largest angular size of a cap face (half the angle its plane cuts from the unit sphere), per radian of the
		/// widened half-angle. The cap is inflated by <c>1 / cos(CapAnglePerWidened * widenedHalfAngle)</c> so its flat faces
		/// enclose the round cap.
		/// </summary>
		public readonly float CapAnglePerWidened;

		public SpotLightProxyShape(float cosSegment, float capAnglePerWidened)
		{
			CosSegment = cosSegment;
			CapAnglePerWidened = capAnglePerWidened;
		}

		/// <summary>
		/// Cap inflation at the widest supported cone; inflation only grows with the angle, so it bounds every light.
		/// </summary>
		public float MaxInflation
		{
			get
			{
				Evaluate(math.cos(math.radians(SpotLightData.MaxOuterHalfAngleDegrees)), out _, out var inflation);
				return inflation;
			}
		}

		public Vector4 ToVector() => new(CosSegment, CapAnglePerWidened, 0f, 0f);

		/// <summary>
		/// C# copy of <c>FulumaSpotProxyShape</c> in <c>FulumaSpotLightForwardPass.hlsl</c>.
		/// </summary>
		public void Evaluate(float cosOuter, out float widenedHalfAngle, out float inflation)
		{
			var sinOuter = math.sqrt(math.saturate(1f - cosOuter * cosOuter));
			widenedHalfAngle = math.atan2(sinOuter, cosOuter * CosSegment);
			inflation = 1f / math.cos(CapAnglePerWidened * widenedHalfAngle);
		}
	}
}
