using System;
using Saesentsessis.Fuluma.Rendering.Proxy;
using Unity.Mathematics;
using UnityEngine;

namespace Saesentsessis.Fuluma.Rendering.Culling
{
	/// <summary>
	/// One camera as spot light culling sees it: its frustum, what decides which shader pass draws a visible light, and
	/// the screen sizes that pick its proxy LOD.
	/// </summary>
	internal struct SpotLightCullingCamera
	{
		/// <summary>
		/// Most LODs <see cref="LodThresholdsA"/> and <see cref="LodThresholdsB"/> can tell apart (7 thresholds).
		/// </summary>
		public const int MaxLodCount = SpotLightProxyArrayDescriptor.MaxLodCount;

		/// <summary>
		/// Builds the culling view of a camera from its matrices and the LOD settings.
		/// </summary>
		/// <param name="view">World to camera.</param>
		/// <param name="projection">Camera projection in Unity's GL convention (<c>Camera.projectionMatrix</c>).</param>
		/// <param name="position">Camera position in world space.</param>
		/// <param name="orthographic">Whether the projection is orthographic.</param>
		/// <param name="nearClipPlane">Near plane distance, for <see cref="NearPadding"/>.</param>
		/// <param name="lodScreenSizes">Per LOD, finest first, non-increasing: the smallest screen size (bounding-sphere
		/// diameter over screen height) drawn with it. One entry: a single LOD.</param>
		/// <param name="lodShapes">Per LOD: the proxy shape the inside/outside test checks the camera against.</param>
		/// <param name="largeLightScreenSize">Above 0: outside-pass lights at least this large on screen (bounding-sphere
		/// diameter over screen height) go to buckets of their own (see <see cref="LargeBucketOffset"/>).</param>
		/// <param name="nearFadeDistance">The near-plane fade distance while the fade is on, else 0. The fade only exists in
		/// the inside pass, so a light switches to it while its proxy is still that far past the near plane (any visible
		/// fragment closer would already be faded): the fade then starts at 1 instead of popping in when the camera reaches
		/// the proxy.</param>
		/// <exception cref="ArgumentException">No LOD, or not one shape per screen size.</exception>
		public static SpotLightCullingCamera Create(Matrix4x4 view, Matrix4x4 projection, Vector3 position, bool orthographic,
			float nearClipPlane, ReadOnlySpan<float> lodScreenSizes, ReadOnlySpan<SpotLightProxyShape> lodShapes,
			float largeLightScreenSize = 0f, float nearFadeDistance = 0f)
		{
			if (lodScreenSizes.Length == 0 || lodShapes.Length != lodScreenSizes.Length)
				throw new ArgumentException("Every LOD needs a screen size and a shape.");

			var clip = projection * view;
			float4 Row(int i) => clip.GetRow(i);

			// Gribb-Hartmann: -w <= x, y, z <= w in clip space.
			var left = Normalize(Row(3) + Row(0));
			var right = Normalize(Row(3) - Row(0));
			var bottom = Normalize(Row(3) + Row(1));
			var top = Normalize(Row(3) - Row(1));

			var near = Normalize(Row(3) + Row(2));
			var far = Normalize(Row(3) - Row(2));

			var tanHalfX = 1f / math.abs(projection.m00);
			var tanHalfY = 1f / math.abs(projection.m11);

			var camera = new SpotLightCullingCamera
			{
				SideX = new float4(left.x, right.x, bottom.x, top.x),
				SideY = new float4(left.y, right.y, bottom.y, top.y),
				SideZ = new float4(left.z, right.z, bottom.z, top.z),
				SideW = new float4(left.w, right.w, bottom.w, top.w),
				DepthX = new float4(near.x, far.x, near.x, far.x),
				DepthY = new float4(near.y, far.y, near.y, far.y),
				DepthZ = new float4(near.z, far.z, near.z, far.z),
				DepthW = new float4(near.w, far.w, near.w, far.w),
				Position = position,
				Orthographic = orthographic,
				// A point at eye depth d inside the frustum is at most d * sqrt(1 + tanX^2 + tanY^2) from the camera.
				NearPadding = 1.05f * (nearClipPlane + nearFadeDistance) * math.sqrt(1f + tanHalfX * tanHalfX + tanHalfY * tanHalfY),
				NearPlane = near - new float4(0f, 0f, 0f, nearFadeDistance)
			};

			// Perspective: m11 = 1 / tan(fovY / 2). Orthographic: m11 = 1 / orthographicSize. Either way the half screen
			// height at the light is tanHalfY * d (perspective) or tanHalfY (orthographic).
			camera.SetLodThresholds(lodScreenSizes, tanHalfY);
			camera.SetLodShapes(lodShapes);
			camera.SetLargeThreshold(largeLightScreenSize, tanHalfY);
			return camera;
		}

		private static float Lane(float4 low, float4 high, int index)
			=> math.select(high[index & 3], low[index & 3], index < 4);

		/// <summary>
		/// Diameter over height is r / halfHeight, so a light is smaller than screen size s when r &lt; s * halfHeight.
		/// The last LOD has no threshold (it takes every smaller light), nor do unused lanes (-1: never below).
		/// </summary>
		private static float Threshold(ReadOnlySpan<float> screenSizes, int lod, int lodCount, float halfHeightScale)
		{
			if (lod >= lodCount - 1)
				return -1f;

			var scaled = screenSizes[lod] * halfHeightScale;
			return scaled * scaled;
		}

		private static float4 Normalize(float4 plane) => plane / math.length(plane.xyz);

		// Frustum planes, inward normals (n . x + w >= 0 inside), normalized, in SoA form so one float4 operation tests a
		// point against four planes: left, right, bottom and top; then near, far, near, far (repeated to fill the lanes).
		public float4 SideX, SideY, SideZ, SideW;
		public float4 DepthX, DepthY, DepthZ, DepthW;
		public float3 Position;
		public bool Orthographic;

		/// <summary>
		/// Distance from the camera to the farthest corner of its near plane, plus 5%; with the near-plane fade, of the
		/// plane where the fade ends (near plane + fade distance) instead.
		/// </summary>
		public float NearPadding;

		/// <summary>
		/// Near plane (inward normal xyz, w), moved forward by the near-plane fade distance, for the orthographic
		/// inside/outside test.
		/// </summary>
		public float4 NearPlane;

		/// <summary>
		/// Number of proxy LODs (1 to <see cref="MaxLodCount"/>).
		/// </summary>
		public int LodCount;

		/// <summary>
		/// Switch sizes of LODs 0..3 and 4..6, as <c>(screenSize * tanHalfFovY)^2</c> (perspective) or
		/// <c>(screenSize * orthographicSize)^2</c>; unused lanes are -1. A light of bounding radius r at distance d is
		/// smaller than LOD i's screen size when <c>r^2 &lt; threshold * d^2</c> (orthographic: <c>d = 1</c>), so its LOD is
		/// the number of thresholds it falls below.
		/// </summary>
		public float4 LodThresholdsA, LodThresholdsB;

		/// <summary>
		/// Screen size from which an outside-pass light counts as large (drawn into the downsampled light buffer), scaled
		/// like <see cref="LodThresholdsA"/>: large when <c>r^2 &gt;= LargeThreshold * d^2</c>. Only read when
		/// <see cref="LargeBucketOffset"/> isn't 0.
		/// </summary>
		public float LargeThreshold;

		/// <summary>
		/// <see cref="LodCount"/> when large lights get buckets of their own (bucket <c>1 + LodCount + lod</c>), else 0:
		/// every outside light then stays in bucket <c>1 + lod</c>.
		/// </summary>
		public int LargeBucketOffset;

		/// <summary>
		/// Per LOD (0..3, 4..7): <c>SpotLightProxyShape.CosSegment</c>, the cone the proxy is widened to.
		/// </summary>
		public float4 LodCosSegmentA, LodCosSegmentB;

		/// <summary>
		/// Per LOD (0..3, 4..7): <c>SpotLightProxyShape.MaxInflation</c>, a conservative proxy radius per range.
		/// </summary>
		public float4 LodMaxInflationA, LodMaxInflationB;

		/// <summary>
		/// Per LOD (0..3, 4..7): <c>SpotLightProxyShape.CapAnglePerWidened</c>, for a light's exact inflation.
		/// </summary>
		public float4 LodCapAngleA, LodCapAngleB;

		/// <summary>
		/// Widened-cone cosine of LOD <paramref name="lod"/>.
		/// </summary>
		public readonly float CosSegment(int lod) => Lane(LodCosSegmentA, LodCosSegmentB, lod);

		/// <summary>
		/// Largest inflation of LOD <paramref name="lod"/>.
		/// </summary>
		public readonly float MaxInflation(int lod) => Lane(LodMaxInflationA, LodMaxInflationB, lod);

		/// <summary>
		/// Cap face angle per radian of widened half-angle of LOD <paramref name="lod"/>.
		/// </summary>
		public readonly float CapAnglePerWidened(int lod) => Lane(LodCapAngleA, LodCapAngleB, lod);

		private void SetLargeThreshold(float screenSize, float halfHeightScale)
		{
			var split = screenSize > 0f;
			var scaled = screenSize * halfHeightScale;

			LargeThreshold = split ? scaled * scaled : 0f;
			LargeBucketOffset = split ? LodCount : 0;
		}

		private void SetLodShapes(ReadOnlySpan<SpotLightProxyShape> shapes)
		{
			// Lanes past the last LOD are never read; they repeat the last LOD.
			Span<float> cosSegments = stackalloc float[MaxLodCount];
			Span<float> maxInflations = stackalloc float[MaxLodCount];
			Span<float> capAngles = stackalloc float[MaxLodCount];

			for (var lod = 0; lod < MaxLodCount; lod++)
			{
				var shape = shapes[math.min(lod, shapes.Length - 1)];
				cosSegments[lod] = shape.CosSegment;
				maxInflations[lod] = shape.MaxInflation;
				capAngles[lod] = shape.CapAnglePerWidened;
			}

			LodCosSegmentA = new float4(cosSegments[0], cosSegments[1], cosSegments[2], cosSegments[3]);
			LodCosSegmentB = new float4(cosSegments[4], cosSegments[5], cosSegments[6], cosSegments[7]);
			LodMaxInflationA = new float4(maxInflations[0], maxInflations[1], maxInflations[2], maxInflations[3]);
			LodMaxInflationB = new float4(maxInflations[4], maxInflations[5], maxInflations[6], maxInflations[7]);
			LodCapAngleA = new float4(capAngles[0], capAngles[1], capAngles[2], capAngles[3]);
			LodCapAngleB = new float4(capAngles[4], capAngles[5], capAngles[6], capAngles[7]);
		}

		private void SetLodThresholds(ReadOnlySpan<float> screenSizes, float halfHeightScale)
		{
			LodCount = math.clamp(screenSizes.Length, 1, MaxLodCount);

			LodThresholdsA = new float4(Threshold(screenSizes, 0, LodCount, halfHeightScale), Threshold(screenSizes, 1, LodCount, halfHeightScale),
				Threshold(screenSizes, 2, LodCount, halfHeightScale), Threshold(screenSizes, 3, LodCount, halfHeightScale));
			LodThresholdsB = new float4(Threshold(screenSizes, 4, LodCount, halfHeightScale), Threshold(screenSizes, 5, LodCount, halfHeightScale),
				Threshold(screenSizes, 6, LodCount, halfHeightScale), -1f);
		}
	}
}
