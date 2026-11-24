using Saesentsessis.Fuluma.Data;
using Saesentsessis.Fuluma.Editor.Drawing;
using Saesentsessis.Fuluma.Rendering.Components;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Editor.Components
{
#if UNITY_6000_5_OR_NEWER
	// Immutable: colors and directions.
	[NoAutoStaticsCleanup]
#endif
	/// <summary>
	/// <see cref="FakeSpotLight"/> in the Scene view: its volume as a gizmo, and handles for the range and the outer and
	/// inner cone angles of the selected light (like Unity's own Light). Edits go through <see cref="FakeSpotLight.Data"/>,
	/// so the store sees them at once, with Undo. The Inspector is <see cref="FakeLightComponentEditor"/>'s (status box,
	/// then the fields).
	/// </summary>
	[CustomEditor(typeof(FakeSpotLight)), CanEditMultipleObjects]
	internal sealed class FakeSpotLightEditor : FakeLightComponentEditor
	{
		private const float HandleSize = 0.04f;

		// Unity's light gizmo color.
		private static readonly Color LightColor = new(254f / 255f, 253f / 255f, 136f / 255f, 1f);
		private static readonly Color UnselectedColor = new(LightColor.r, LightColor.g, LightColor.b, 0.35f);
		private static readonly Color InnerColor = new(LightColor.r, LightColor.g, LightColor.b, 0.5f);

		/// <summary>
		/// Directions around the axis the angle handles sit at, in light space.
		/// </summary>
		private static readonly Vector3[] HandleDirections = { Vector3.up, Vector3.down, Vector3.right, Vector3.left };

		[DrawGizmo(GizmoType.NotInSelectionHierarchy | GizmoType.Pickable)]
		private static void DrawUnselected(FakeSpotLight light, GizmoType type)
			=> FulumaShapes.SpotLightGizmo(LightToWorld(light.transform), light.Data, UnselectedColor, InnerColor, drawInner: false);

		[DrawGizmo(GizmoType.InSelectionHierarchy | GizmoType.Pickable)]
		private static void DrawSelected(FakeSpotLight light, GizmoType type)
			=> FulumaShapes.SpotLightGizmo(LightToWorld(light.transform), light.Data, LightColor, InnerColor, drawInner: true);

		/// <summary>
		/// A dot on the axis at the range; dragged along the axis.
		/// </summary>
		private static float RangeHandle(float range)
		{
			var tip = new Vector3(0f, 0f, range);
			var size = HandleUtility.GetHandleSize(tip) * HandleSize;
			var moved = Handles.Slider(tip, Vector3.forward, size, Handles.DotHandleCap, 0f);
			return math.max(0f, moved.z);
		}

		/// <summary>
		/// Four dots where a cone of <paramref name="halfAngle"/> (degrees) meets the cap sphere; each is dragged along the
		/// sphere's tangent and the new angle read back from where it ends up. Returns the half-angle in degrees.
		/// </summary>
		private static float AngleHandles(float range, float halfAngle)
		{
			var radians = math.radians(halfAngle);
			var cos = math.cos(radians);
			var sin = math.sin(radians);
			var result = halfAngle;

			foreach (var direction in HandleDirections)
			{
				var point = new Vector3(0f, 0f, cos * range) + direction * (sin * range);
				var tangent = new Vector3(0f, 0f, -sin) + direction * cos;
				var size = HandleUtility.GetHandleSize(point) * HandleSize;

				EditorGUI.BeginChangeCheck();
				var moved = Handles.Slider(point, tangent, size, Handles.DotHandleCap, 0f);
				if (EditorGUI.EndChangeCheck())
				{
					var angle = math.degrees(math.atan2(Vector3.Dot(moved, direction), moved.z));
					result = math.clamp(angle, 0f, SpotLightData.MaxOuterHalfAngleDegrees);
				}
			}

			return result;
		}

		private static Matrix4x4 LightToWorld(Transform transform)
		{
			transform.GetPositionAndRotation(out var position, out var rotation);
			return Matrix4x4.TRS(position, rotation, Vector3.one);
		}

		private void OnSceneGUI()
		{
			var light = (FakeSpotLight)target;
			var data = light.Data;
			light.transform.GetPositionAndRotation(out var apex, out var rotation);

			var range = math.max(0f, data.Range);
			var outerHalf = math.clamp(data.OuterAngleDegrees * 0.5f, 0f, SpotLightData.MaxOuterHalfAngleDegrees);
			var innerHalf = math.clamp(data.InnerAngleDegrees * 0.5f, 0f, outerHalf);

			using (new Handles.DrawingScope(LightColor, Matrix4x4.TRS(apex, rotation, Vector3.one)))
			{
				EditorGUI.BeginChangeCheck();

				var newRange = RangeHandle(range);
				var newOuter = AngleHandles(range, outerHalf);
				var newInner = AngleHandles(range, innerHalf);

				if (EditorGUI.EndChangeCheck() == false)
					return;

				Undo.RecordObject(light, "Edit Fake Spot Light");

				// Angles are stored as full angles; the inner one never exceeds the outer one.
				data.Range = newRange;
				data.OuterAngleDegrees = newOuter * 2f;
				data.InnerAngleDegrees = math.min(newInner, newOuter) * 2f;
				light.Data = data;

				PrefabUtility.RecordPrefabInstancePropertyModifications(light);
			}
		}
	}
}
