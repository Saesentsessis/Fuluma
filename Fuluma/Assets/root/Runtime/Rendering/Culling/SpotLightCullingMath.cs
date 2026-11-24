using System.Runtime.CompilerServices;
using System.Threading;
using Saesentsessis.Fuluma.Data;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Saesentsessis.Fuluma.Rendering.Culling
{
	/// <summary>
	/// Per-light tests shared by <see cref="SpotLightCullingJob"/> (with LODs) and <see cref="SpotLightCullingFastJob"/>
	/// (LOD0 only): frustum visibility, LOD by screen size, and which shader pass draws a light.
	/// </summary>
	internal static class SpotLightCullingMath
	{
		/// <summary>
		/// Frustum test of the light's volume, its LOD by screen size, then which shader pass draws it (the inside pass
		/// draws with LOD0 whatever <paramref name="lod"/> says). <paramref name="large"/>: at least
		/// <see cref="SpotLightCullingCamera.LargeThreshold"/> on screen (only meaningful when the camera splits large
		/// lights off).
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static SpotLightVisibility Classify(float4 sphere, in SpotLightCullingData light, in SpotLightCullingCamera camera,
			out int lod, out bool large)
		{
			lod = 0;
			large = false;

			if (InFrustum(sphere, light, camera) == false)
				return SpotLightVisibility.Culled;

			// Only visible lights need a LOD.
			lod = SelectLod(sphere, camera, out large);
			return CameraNearProxy(light, camera, lod) ? SpotLightVisibility.Inside : SpotLightVisibility.Outside;
		}

		/// <summary>
		/// <see cref="Classify"/> with LOD0 for every light: no LOD selection, and the inside test's LOD lookups fold to
		/// constants.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static SpotLightVisibility ClassifyLod0(float4 sphere, in SpotLightCullingData light, in SpotLightCullingCamera camera)
		{
			if (InFrustum(sphere, light, camera) == false)
				return SpotLightVisibility.Culled;

			return CameraNearProxy(light, camera, 0) ? SpotLightVisibility.Inside : SpotLightVisibility.Outside;
		}

		/// <summary>
		/// Whether any part of the light's volume may be inside the frustum.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool InFrustum(float4 sphere, in SpotLightCullingData light, in SpotLightCullingCamera camera)
		{
			// Bounding sphere against the six planes (four side lanes, near/far/near/far lanes).
			var side = PlaneDistances(camera.SideX, camera.SideY, camera.SideZ, camera.SideW, sphere.xyz);
			var depth = PlaneDistances(camera.DepthX, camera.DepthY, camera.DepthZ, camera.DepthW, sphere.xyz);
			var closest = math.min(side, depth);

			if (math.any(closest < -sphere.w))
				return false;

			// The sphere straddles a plane: test the exact volume. Planes the sphere is inside of can't cull it (the volume
			// is inside the sphere), so all six are tested at once.
			return math.any(closest < sphere.w) == false || OutsideAnyPlane(light, camera) == false;
		}

		/// <summary>
		/// Proxy LOD by screen size: the number of LOD thresholds the bounding sphere falls below (see
		/// <see cref="SpotLightCullingCamera.LodThresholdsA"/>). Branch-free; distance is to the sphere's center.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int SelectLod(float4 sphere, in SpotLightCullingCamera camera) => SelectLod(sphere, camera, out _);

		/// <summary>
		/// <see cref="SelectLod(float4, in SpotLightCullingCamera)"/>, and whether the light is large on screen
		/// (<see cref="SpotLightCullingCamera.LargeThreshold"/>) from the same distance and radius.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int SelectLod(float4 sphere, in SpotLightCullingCamera camera, out bool large)
		{
			ScreenSizeTerms(sphere, camera, out var radiusSq, out var distanceSq);

			large = radiusSq >= camera.LargeThreshold * distanceSq;

			var belowA = radiusSq < camera.LodThresholdsA * distanceSq;
			var belowB = radiusSq < camera.LodThresholdsB * distanceSq;
			return math.csum(math.select(int4.zero, 1, belowA)) + math.csum(math.select(int4.zero, 1, belowB));
		}

		/// <summary>
		/// Whether the light is at least <see cref="SpotLightCullingCamera.LargeThreshold"/> on screen.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool IsLarge(float4 sphere, in SpotLightCullingCamera camera)
		{
			ScreenSizeTerms(sphere, camera, out var radiusSq, out var distanceSq);
			return radiusSq >= camera.LargeThreshold * distanceSq;
		}

		/// <summary>
		/// Whether the whole volume (a cone of the outer half-angle capped by the range sphere) is on the outer side of
		/// one of the camera planes.
		/// </summary>
		/// <remarks>
		/// The volume's farthest reach along a plane normal n, from the apex: the full range when n lies inside the cone,
		/// otherwise range * cos(angle(n, axis) - outer half-angle), and never below the apex itself (0).
		/// </remarks>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool OutsideAnyPlane(in SpotLightCullingData light, in SpotLightCullingCamera camera)
		{
			var position = light.PositionRange.xyz;
			var range = light.PositionRange.w;
			var direction = light.DirectionCosOuter.xyz;
			var cosOuter = light.DirectionCosOuter.w;
			var sinOuter = math.sqrt(math.saturate(1f - cosOuter * cosOuter));

			var side = OutsidePlanes(camera.SideX, camera.SideY, camera.SideZ, camera.SideW, position, range, direction, cosOuter, sinOuter);
			var depth = OutsidePlanes(camera.DepthX, camera.DepthY, camera.DepthZ, camera.DepthW, position, range, direction, cosOuter, sinOuter);
			return math.any(side | depth);
		}

		/// <summary>
		/// C# port of the old vertex-shader test: whether the camera, padded by its near-plane corners, may touch the
		/// proxy of LOD <paramref name="lod"/>. Then front faces can be missing or clipped by the near plane, and the light
		/// needs pass 0 (back faces, no depth test). That pass is right wherever the camera is, so it may draw with LOD0.
		/// Conservative: the proxy radius uses the LOD's largest inflation, and borderline lights get pass 0.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool CameraNearProxy(in SpotLightCullingData light, in SpotLightCullingCamera camera, int lod)
		{
			var position = light.PositionRange.xyz;
			var range = light.PositionRange.w;
			var direction = light.DirectionCosOuter.xyz;
			var cosOuter = light.DirectionCosOuter.w;
			var proxyRadius = range * camera.MaxInflation(lod);

			// Orthographic: parallel rays, so front faces cover every pixel unless the near plane clips the proxy, which
			// lies within its radius of the apex.
			if (camera.Orthographic)
				return math.dot(camera.NearPlane.xyz, position) + camera.NearPlane.w < proxyRadius;

			// Cheap rejection with the LOD's widest-cone inflation: most lights end here.
			var toCamera = camera.Position - position;
			var distanceSq = math.lengthsq(toCamera);
			var reach = proxyRadius + camera.NearPadding;
			if (distanceSq > reach * reach)
				return false;

			// Widened proxy cone, as FulumaSpotProxyShape computes it: tan(widened) = tan(outer) / cos(pi / n).
			var sinOuter = math.sqrt(math.saturate(1f - cosOuter * cosOuter));
			var tanWidened = sinOuter / (cosOuter * camera.CosSegment(lod));
			var cosWidened = math.rsqrt(1f + tanWidened * tanWidened);
			var sinWidened = tanWidened * cosWidened;

			// The inflation the shader gives this light (coarse LODs inflate wide cones far more than narrow ones).
			var inflation = 1f / math.cos(camera.CapAnglePerWidened(lod) * math.atan(tanWidened));
			reach = range * inflation + camera.NearPadding;
			if (distanceSq > reach * reach)
				return false;

			// Apex pulled back so the cone grows by the padding.
			var fromPulledApex = toCamera + direction * (camera.NearPadding / math.max(sinWidened, 1e-3f));
			return math.dot(fromPulledApex, direction) >= math.length(fromPulledApex) * cosWidened;
		}

		/// <summary>
		/// Copies one batch's <paramref name="count"/> indices to the end of a region, reserving the room with one atomic
		/// add on the region's counter (so batches land in no particular order).
		/// </summary>
		public static unsafe void Append(NativeArray<uint> indices, NativeArray<int> counters, uint* source, int count, int region,
			int counter)
		{
			if (count == 0)
				return;

			ref var slot = ref UnsafeUtility.ArrayElementAsRef<int>(counters.GetUnsafePtr(), counter);
			var offset = Interlocked.Add(ref slot, count) - count;
			UnsafeUtility.MemCpy((uint*)indices.GetUnsafePtr() + region + offset, source, (long)count * sizeof(uint));
		}

		/// <summary>
		/// What every screen-size test compares: the bounding sphere's squared radius against a threshold times the
		/// squared distance to its center (orthographic: distance 1).
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static void ScreenSizeTerms(float4 sphere, in SpotLightCullingCamera camera, out float radiusSq, out float distanceSq)
		{
			var offset = sphere.xyz - camera.Position;
			distanceSq = math.select(math.dot(offset, offset), 1f, camera.Orthographic);
			radiusSq = sphere.w * sphere.w;
		}

		/// <summary>
		/// Signed distances of a point to four planes in SoA form.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static float4 PlaneDistances(float4 x, float4 y, float4 z, float4 w, float3 point)
			=> x * point.x + y * point.y + z * point.z + w;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static bool4 OutsidePlanes(float4 x, float4 y, float4 z, float4 w, float3 position, float range,
			float3 direction, float cosOuter, float sinOuter)
		{
			var apex = PlaneDistances(x, y, z, w, position);
			var cosAxis = x * direction.x + y * direction.y + z * direction.z;
			var sinAxis = math.sqrt(math.saturate(1f - cosAxis * cosAxis));

			var reach = math.select(math.max(0f, cosAxis * cosOuter + sinAxis * sinOuter), 1f, cosAxis >= cosOuter);
			return apex + range * reach < 0f;
		}
	}
}
