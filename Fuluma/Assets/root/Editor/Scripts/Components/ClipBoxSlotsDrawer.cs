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
	// Immutable: property paths, labels and messages.
	[NoAutoStaticsCleanup]
#endif
	/// <summary>
	/// Draws <see cref="ClipBoxSlots"/> as four rows (box and role) and explains, under a slot, why the light ignores its
	/// box: in another light store, disabled, or already used by an earlier slot.
	/// </summary>
	/// <remarks>
	/// Cheap per repaint: the checks read the component's <see cref="FakeLightComponent.ClipBoxes"/> (a struct copy, no
	/// allocation), messages and labels are cached, and help boxes have a fixed height (no text measuring). With several
	/// objects selected the checks are skipped, since they would describe only the first one.
	/// </remarks>
	[CustomPropertyDrawer(typeof(ClipBoxSlots))]
	internal sealed class ClipBoxSlotsDrawer : PropertyDrawer
	{
		private const float BoxWidthFraction = 0.62f;

		private const string OtherStoreMessage = "This box is in another light store, so the light ignores it. Give both the same Override Store.";
		private const string DisabledMessage = "This box is disabled (or its GameObject is inactive), so the light ignores it until it is enabled.";

		private static readonly string[] BoxPaths = { "box0", "box1", "box2", "box3" };
		private static readonly string[] RolePaths = { "role0", "role1", "role2", "role3" };

		private static readonly GUIContent[] SlotLabels =
		{
			new("Slot 1", "First clip box of the light and its role. Leave Box empty to leave the slot unused."),
			new("Slot 2", "Second clip box of the light and its role. Leave Box empty to leave the slot unused."),
			new("Slot 3", "Third clip box of the light and its role. Leave Box empty to leave the slot unused."),
			new("Slot 4", "Fourth clip box of the light and its role. Leave Box empty to leave the slot unused.")
		};

		private static readonly string[] DuplicateMessages =
		{
			"Slot 1 already uses this box; it counts once.",
			"Slot 2 already uses this box; it counts once.",
			"Slot 3 already uses this box; it counts once."
		};

		private static float HelpBoxHeight => EditorGUIUtility.singleLineHeight * 2f;

		private static void DrawSlot(Rect row, SerializedProperty property, int index)
		{
			var fields = EditorGUI.PrefixLabel(row, SlotLabels[index]);

			// PrefixLabel returns an already indented rect: draw the fields without indenting them again.
			var indent = EditorGUI.indentLevel;
			EditorGUI.indentLevel = 0;

			var boxWidth = math.floor(fields.width * BoxWidthFraction);
			var boxRect = new Rect(fields.x, fields.y, boxWidth - 2f, fields.height);
			var roleRect = new Rect(fields.x + boxWidth, fields.y, fields.width - boxWidth, fields.height);

			EditorGUI.PropertyField(boxRect, property.FindPropertyRelative(BoxPaths[index]), GUIContent.none);
			EditorGUI.PropertyField(roleRect, property.FindPropertyRelative(RolePaths[index]), GUIContent.none);

			EditorGUI.indentLevel = indent;
		}

		/// <summary>
		/// Why slot <paramref name="index"/>'s box doesn't take effect, the most important reason first.
		/// <paramref name="firstUse"/>: for a duplicate, the earlier slot holding the same box.
		/// </summary>
		private static SlotIssue IssueOf(FakeLightComponent light, in ClipBoxSlots slots, int index, out int firstUse)
		{
			firstUse = -1;
			var box = slots[index].Box;
			if (box == null)
				return SlotIssue.None;

			// Both null means both use LightStore.Default.
			if (box.OverrideStore != light.OverrideStore)
				return SlotIssue.OtherStore;

			if (box.isActiveAndEnabled == false)
				return SlotIssue.Disabled;

			for (var i = 0; i < index; i++)
			{
				if (slots[i].Box == box)
				{
					firstUse = i;
					return SlotIssue.Duplicate;
				}
			}

			return SlotIssue.None;
		}

		private static string MessageOf(SlotIssue issue, int firstUse) => issue switch
		{
			SlotIssue.OtherStore => OtherStoreMessage,
			SlotIssue.Disabled => DisabledMessage,
			_ => DuplicateMessages[firstUse]
		};

		/// <summary>
		/// The single light component being edited; false for several targets or a field outside a light component.
		/// </summary>
		private static bool TryGetLight(SerializedProperty property, out FakeLightComponent light)
		{
			var serializedObject = property.serializedObject;
			light = serializedObject.isEditingMultipleObjects ? null : serializedObject.targetObject as FakeLightComponent;
			return light != null;
		}

		public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
		{
			var line = EditorGUIUtility.singleLineHeight;
			var spacing = EditorGUIUtility.standardVerticalSpacing;

			if (property.isExpanded == false)
				return line;

			var height = line + ClipBoxSlots.Count * (line + spacing);

			if (TryGetLight(property, out var light))
			{
				var slots = light.ClipBoxes;
				for (var i = 0; i < ClipBoxSlots.Count; i++)
				{
					if (IssueOf(light, slots, i, out _) != SlotIssue.None)
						height += HelpBoxHeight + spacing;
				}
			}

			return height;
		}

		public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
		{
			var line = EditorGUIUtility.singleLineHeight;
			var spacing = EditorGUIUtility.standardVerticalSpacing;

			var row = new Rect(position.x, position.y, position.width, line);
			property.isExpanded = EditorGUI.Foldout(row, property.isExpanded, label, true);
			if (property.isExpanded == false)
				return;

			var hasLight = TryGetLight(property, out var light);
			var slots = hasLight ? light.ClipBoxes : default;

			using (new EditorGUI.IndentLevelScope())
			{
				for (var i = 0; i < ClipBoxSlots.Count; i++)
				{
					row.y += line + spacing;
					DrawSlot(row, property, i);

					if (hasLight == false)
						continue;

					var issue = IssueOf(light, slots, i, out var firstUse);
					if (issue == SlotIssue.None)
						continue;

					row.y += line + spacing;
					var help = EditorGUI.IndentedRect(new Rect(row.x, row.y, row.width, HelpBoxHeight));
					EditorGUI.HelpBox(help, MessageOf(issue, firstUse), issue == SlotIssue.Duplicate ? MessageType.Info : MessageType.Warning);
					row.y += HelpBoxHeight - line;
				}
			}
		}

		private enum SlotIssue : byte
		{
			None,
			OtherStore,
			Disabled,
			Duplicate
		}
	}
}
