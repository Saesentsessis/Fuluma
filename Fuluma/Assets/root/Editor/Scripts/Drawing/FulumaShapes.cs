using Saesentsessis.Fuluma.Data;
using Unity.Mathematics;
using UnityEngine;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Editor.Drawing
{
#if UNITY_6000_5_OR_NEWER
	// Immutable tables and scratch buffers rewritten on every draw.
	[NoAutoStaticsCleanup]
#endif
	/// <summary>
	/// Scene view shapes of the fake light components, drawn as gizmos. Built for a low per-draw cost: every shape is
	/// written in its own local space into a reused buffer (the transform goes to <see cref="Gizmos.matrix"/>, so no
	/// per-vertex transform on the CPU) and drawn with one <see cref="Gizmos.DrawLineList"/> call.
	/// </summary>
	internal static class FulumaShapes
	{
		/// <summary>
		/// Segments of a cone's rim circle.
		/// </summary>
		private const int RimSegments = 32;

		/// <summary>
		/// Segments of each arc across a spot light's cap.
		/// </summary>
		private const int ArcSegments = 16;

		/// <summary>
		/// Rim circle, 4 lines from the apex to the rim, and (outer cone only) 2 arcs across the cap.
		/// </summary>
		private const int ConeLinePoints = 2 * RimSegments + 8;

		private const int CapLinePoints = 4 * ArcSegments;

		/// <summary>
		/// cos and sin of i * 2 pi / <see cref="RimSegments"/>, for i = 0 .. <see cref="RimSegments"/>.
		/// </summary>
		private static readonly Vector2[] UnitCircle = CreateUnitCircle();

		private static readonly Vector3[] OuterLines = new Vector3[ConeLinePoints + CapLinePoints];
		private static readonly Vector3[] InnerLines = new Vector3[ConeLinePoints];

		/// <summary>
		/// A spot light's volume: the outer cone with its spherical cap, and the inner cone when it differs, in light space
		/// (apex at the origin, +z along the axis) under <paramref name="lightToWorld"/>.
		/// </summary>
		/// <param name="data">The light's parameters; the angles are clamped like the GPU data is
		/// (<see cref="SpotLightData.MaxOuterHalfAngleDegrees"/>, inner at most outer).</param>
		public static void SpotLightGizmo(in Matrix4x4 lightToWorld, in SpotLightData data, Color outerColor, Color innerColor,
			bool drawInner)
		{
			var range = math.max(0f, data.Range);
			var outerHalf = math.radians(math.clamp(data.OuterAngleDegrees * 0.5f, 0f, SpotLightData.MaxOuterHalfAngleDegrees));
			var innerHalf = math.radians(math.clamp(data.InnerAngleDegrees * 0.5f, 0f, math.degrees(outerHalf)));

			var previousMatrix = Gizmos.matrix;
			var previousColor = Gizmos.color;
			Gizmos.matrix = lightToWorld;

			FillCone(OuterLines, range, outerHalf);
			FillCapArcs(OuterLines, ConeLinePoints, range, outerHalf);
			Gizmos.color = outerColor;
			Gizmos.DrawLineList(OuterLines);

			if (drawInner && innerHalf > 1e-4f && outerHalf - innerHalf > 1e-4f)
			{
				FillCone(InnerLines, range, innerHalf);
				Gizmos.color = innerColor;
				Gizmos.DrawLineList(InnerLines);
			}

			Gizmos.matrix = previousMatrix;
			Gizmos.color = previousColor;
		}

		/// <summary>
		/// A clip box: <paramref name="box"/>'s center and size in the space of <paramref name="boxToWorld"/> (its
		/// Transform's full matrix, scale included).
		/// </summary>
		public static void ClipBoxGizmo(in Matrix4x4 boxToWorld, in ClipBoxData box, Color color)
		{
			var previousMatrix = Gizmos.matrix;
			var previousColor = Gizmos.color;

			Gizmos.matrix = boxToWorld;
			Gizmos.color = color;
			Gizmos.DrawWireCube(box.Center, box.Size);

			Gizmos.matrix = previousMatrix;
			Gizmos.color = previousColor;
		}

		/// <summary>
		/// The rim circle (where the cone meets the cap sphere) and 4 lines from the apex to it, from index 0.
		/// </summary>
		private static void FillCone(Vector3[] lines, float range, float halfAngle)
		{
			var radius = range * math.sin(halfAngle);
			var height = range * math.cos(halfAngle);

			var index = 0;
			for (var i = 0; i < RimSegments; i++)
			{
				var a = UnitCircle[i];
				var b = UnitCircle[i + 1];
				lines[index++] = new Vector3(a.x * radius, a.y * radius, height);
				lines[index++] = new Vector3(b.x * radius, b.y * radius, height);
			}

			lines[index++] = Vector3.zero;
			lines[index++] = new Vector3(radius, 0f, height);
			lines[index++] = Vector3.zero;
			lines[index++] = new Vector3(-radius, 0f, height);
			lines[index++] = Vector3.zero;
			lines[index++] = new Vector3(0f, radius, height);
			lines[index++] = Vector3.zero;
			lines[index] = new Vector3(0f, -radius, height);
		}

		/// <summary>
		/// Two arcs across the spherical cap, in the light's xz and yz planes, from rim to rim through the tip, from
		/// <paramref name="start"/>. The polar angle steps by a rotation instead of one sin/cos per point.
		/// </summary>
		private static void FillCapArcs(Vector3[] lines, int start, float range, float halfAngle)
		{
			var step = 2f * halfAngle / ArcSegments;
			var stepCos = math.cos(step);
			var stepSin = math.sin(step);

			// (cos, sin) of the polar angle, from -halfAngle.
			var cos = math.cos(halfAngle);
			var sin = -math.sin(halfAngle);

			var index = start;
			for (var i = 0; i < ArcSegments; i++)
			{
				var nextCos = cos * stepCos - sin * stepSin;
				var nextSin = sin * stepCos + cos * stepSin;

				lines[index++] = new Vector3(sin * range, 0f, cos * range);
				lines[index++] = new Vector3(nextSin * range, 0f, nextCos * range);
				lines[index++] = new Vector3(0f, sin * range, cos * range);
				lines[index++] = new Vector3(0f, nextSin * range, nextCos * range);

				cos = nextCos;
				sin = nextSin;
			}
		}

		private static Vector2[] CreateUnitCircle()
		{
			var circle = new Vector2[RimSegments + 1];
			for (var i = 0; i <= RimSegments; i++)
			{
				var angle = i * (2f * math.PI / RimSegments);
				circle[i] = new Vector2(math.cos(angle), math.sin(angle));
			}

			return circle;
		}
	}
}
