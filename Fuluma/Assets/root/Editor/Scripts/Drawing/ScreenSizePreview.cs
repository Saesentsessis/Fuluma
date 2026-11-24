using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Editor.Drawing
{
#if UNITY_6000_5_OR_NEWER
	// Editor-only overlay state; Hide() unsubscribes, and a stale subscription only draws nothing.
	[NoAutoStaticsCleanup]
#endif
	/// <summary>
	/// Scene view overlay showing a range of screen sizes as a flat ring around the view's center: a light whose bounding
	/// sphere looks as large as the outer edge sits at the top of the range, one as large as the inner edge at its bottom.
	/// </summary>
	/// <remarks>
	/// Self-contained on purpose: <see cref="Show"/> and <see cref="Hide"/> are its only entry points (called by the
	/// proxy LOD editor), so removing the preview means deleting this file and those calls.
	/// </remarks>
	internal static class ScreenSizePreview
	{
		/// <summary>
		/// Segments of the ring.
		/// </summary>
		private const int Segments = 96;

		private static readonly Color Fill = new(1f, 0.85f, 0.35f, 0.12f);
		private static readonly Color Outline = new(1f, 0.85f, 0.35f, 0.8f);

		/// <summary>
		/// Unit circle, <see cref="Segments"/> + 1 points (the last repeats the first).
		/// </summary>
		private static readonly Vector2[] UnitCircle = CreateUnitCircle();

		private static readonly int SrcBlendId = Shader.PropertyToID("_SrcBlend");
		private static readonly int DstBlendId = Shader.PropertyToID("_DstBlend");
		private static readonly int CullId = Shader.PropertyToID("_Cull");
		private static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");
		private static readonly int ZTestId = Shader.PropertyToID("_ZTest");

		/// <summary>
		/// Unlit vertex-color material for the ring, created on first use.
		/// </summary>
		private static Material _ringMaterial;

		private static bool _subscribed;
		private static float _top;
		private static float _bottom;

		/// <summary>
		/// Shows the range from <paramref name="top"/> down to <paramref name="bottom"/> (fractions of the screen height:
		/// bounding-sphere diameter over screen height).
		/// </summary>
		internal static void Show(float top, float bottom)
		{
			var changed = _subscribed == false || top != _top || bottom != _bottom;
			_top = top;
			_bottom = bottom;

			if (_subscribed == false)
			{
				SceneView.duringSceneGui += Draw;
				_subscribed = true;
			}

			if (changed)
				SceneView.RepaintAll();
		}

		/// <summary>
		/// Removes the overlay and frees its material.
		/// </summary>
		internal static void Hide()
		{
			if (_subscribed == false)
				return;

			SceneView.duringSceneGui -= Draw;
			_subscribed = false;
			SceneView.RepaintAll();

			if (_ringMaterial != null)
			{
				Object.DestroyImmediate(_ringMaterial);
				_ringMaterial = null;
			}
		}

		private static Vector2[] CreateUnitCircle()
		{
			var points = new Vector2[Segments + 1];
			for (var i = 0; i <= Segments; i++)
			{
				math.sincos(2f * math.PI * i / Segments, out var sin, out var cos);
				points[i] = new Vector2(cos, sin);
			}

			return points;
		}

		/// <summary>
		/// The ring's unlit, alpha-blended vertex-color material, created on first use; null if the Editor's colored shader
		/// is missing.
		/// </summary>
		private static Material RingMaterial()
		{
			if (_ringMaterial != null)
				return _ringMaterial;

			var shader = Shader.Find("Hidden/Internal-Colored");
			if (shader == null)
				return null;

			_ringMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
			_ringMaterial.SetInt(SrcBlendId, (int)BlendMode.SrcAlpha);
			_ringMaterial.SetInt(DstBlendId, (int)BlendMode.OneMinusSrcAlpha);
			_ringMaterial.SetInt(CullId, (int)CullMode.Off);
			_ringMaterial.SetInt(ZWriteId, 0);
			_ringMaterial.SetInt(ZTestId, (int)CompareFunction.Always);
			return _ringMaterial;
		}

		private static void Draw(SceneView view)
		{
			if (Event.current.type != EventType.Repaint || view.camera == null)
				return;

			var pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
			var width = view.camera.pixelWidth / pixelsPerPoint;
			var height = view.camera.pixelHeight / pixelsPerPoint;
			var center = new Vector3(width * 0.5f, height * 0.5f, 0f);

			// Screen size is the sphere's diameter over the screen height.
			var outer = 0.5f * height * math.saturate(_top);
			var inner = 0.5f * height * math.saturate(_bottom);

			Handles.BeginGUI();
			DrawRing(center, inner, outer);

			var color = Handles.color;
			Handles.color = Outline;
			Handles.DrawWireDisc(center, Vector3.forward, outer);

			if (inner > 0f)
				Handles.DrawWireDisc(center, Vector3.forward, inner);

			Handles.color = color;
			Handles.EndGUI();
		}

		/// <summary>
		/// Fills the band between the two radii as one triangle strip around the circle (inner and outer points in turn),
		/// in GUI coordinates.
		/// </summary>
		private static void DrawRing(Vector3 center, float inner, float outer)
		{
			if (outer <= inner)
				return;

			var material = RingMaterial();
			if (material == null || material.SetPass(0) == false)
				return;

			GL.Begin(GL.TRIANGLE_STRIP);
			GL.Color(Fill);

			foreach (var point in UnitCircle)
			{
				GL.Vertex3(center.x + point.x * inner, center.y + point.y * inner, 0f);
				GL.Vertex3(center.x + point.x * outer, center.y + point.y * outer, 0f);
			}

			GL.End();
		}
	}
}
