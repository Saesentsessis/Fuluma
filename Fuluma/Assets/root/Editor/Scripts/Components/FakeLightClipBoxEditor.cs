using Saesentsessis.Fuluma.Data;
using Saesentsessis.Fuluma.Editor.Drawing;
using Saesentsessis.Fuluma.Rendering.Components;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Editor.Components
{
#if UNITY_6000_5_OR_NEWER
	// Immutable: colors.
	[NoAutoStaticsCleanup]
#endif
	/// <summary>
	/// <see cref="FakeLightClipBox"/> in the Scene view: a wire box gizmo, and a box handle (center and size, in the
	/// box's own space with its rotation and scale) for the selected box. Edits go through <see cref="FakeLightClipBox.Shape"/>,
	/// so the store sees them at once, with Undo. The Inspector starts with a <see cref="RegistrationStatus"/> box.
	/// </summary>
	[CustomEditor(typeof(FakeLightClipBox)), CanEditMultipleObjects]
	internal sealed class FakeLightClipBoxEditor : UnityEditor.Editor
	{
		private static readonly Color BoxColor = new(1f, 0.75f, 0.2f, 1f);
		private static readonly Color UnselectedColor = new(1f, 0.75f, 0.2f, 0.35f);

		[DrawGizmo(GizmoType.NotInSelectionHierarchy | GizmoType.Pickable)]
		private static void DrawUnselected(FakeLightClipBox box, GizmoType type)
			=> FulumaShapes.ClipBoxGizmo(box.transform.localToWorldMatrix, box.Shape, UnselectedColor);

		// A selected box is drawn by its handle (which brings its own wireframe); this covers boxes under a selected parent.
		[DrawGizmo(GizmoType.InSelectionHierarchy | GizmoType.Pickable)]
		private static void DrawInSelection(FakeLightClipBox box, GizmoType type)
		{
			if (Selection.Contains(box.gameObject) == false)
				FulumaShapes.ClipBoxGizmo(box.transform.localToWorldMatrix, box.Shape, BoxColor);
		}

		private BoxBoundsHandle _handle;

		private readonly RegistrationStatus _status = new();

		/// <inheritdoc/>
		public override void OnInspectorGUI()
		{
			if (serializedObject.isEditingMultipleObjects == false)
				DrawStatus((FakeLightClipBox)target);

			base.OnInspectorGUI();
		}

		private void OnEnable() => _handle = new BoxBoundsHandle { wireframeColor = BoxColor, handleColor = BoxColor };

		private void DrawStatus(FakeLightClipBox box)
		{
			var registered = box.TryGetRegistration(out var store, out var handle);
			var reason = registered ? default
				: EditorUtility.IsPersistent(box) ? RegistrationStatus.PrefabAsset
				: box.isActiveAndEnabled == false ? RegistrationStatus.Disabled
				: RegistrationStatus.Unknown;

			_status.Draw(RegistrationStatus.Subject.ClipBox, box.OverrideStore, registered, store, handle.Mode, reason);
		}

		private void OnSceneGUI()
		{
			var box = (FakeLightClipBox)target;
			var shape = box.Shape;

			using (new Handles.DrawingScope(box.transform.localToWorldMatrix))
			{
				_handle.center = shape.Center;
				_handle.size = shape.Size;

				EditorGUI.BeginChangeCheck();
				_handle.DrawHandle();
				if (EditorGUI.EndChangeCheck() == false)
					return;

				Undo.RecordObject(box, "Edit Fake Light Clip Box");
				box.Shape = new ClipBoxData { Center = _handle.center, Size = _handle.size };
				PrefabUtility.RecordPrefabInstancePropertyModifications(box);
			}
		}
	}
}
