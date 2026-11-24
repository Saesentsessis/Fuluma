using Saesentsessis.Fuluma.Data;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Editor.Data
{
#if UNITY_6000_5_OR_NEWER
	// Immutable: labels and layout constants.
	[NoAutoStaticsCleanup]
#endif
	/// <summary>
	/// Draws <see cref="SpotLightData"/>: color with a line explaining what its alpha does, intensity, range, and both cone
	/// angles as one min/max slider (inner = min, outer = max) with a number field at each end, limited to the
	/// largest outer angle the shader supports.
	/// </summary>
	/// <remarks>
	/// Cheap per repaint: fixed height, cached labels, and the alpha line is formatted again only when the alpha or the
	/// intensity changes.
	/// </remarks>
	[CustomPropertyDrawer(typeof(SpotLightData))]
	internal sealed class SpotLightDataDrawer : PropertyDrawer
	{
		/// <summary>
		/// Largest full outer angle the packing keeps (twice <see cref="SpotLightData.MaxOuterHalfAngleDegrees"/>).
		/// </summary>
		private const float MaxFullAngle = SpotLightData.MaxOuterHalfAngleDegrees * 2f;

		private const float AngleFieldWidth = 52f;
		private const float AngleFieldGap = 4f;

		/// <summary>
		/// Rows: header, color, alpha note, intensity, range, cone angles.
		/// </summary>
		private const int RowCount = 6;

		private static readonly GUIContent AnglesLabel = new("Cone Angles",
			"Full cone angles in degrees. Left: inner angle, where the angular falloff starts (full brightness inside it). "
			+ "Right: outer angle, the edge of the volume. The outer angle is limited to 179.8 degrees.");

		private static readonly GUIContent MixedAlphaNote = new("Alpha multiplies the intensity (0 = off, 1 = full).");

		/// <summary>
		/// Inner and outer angle as one min/max slider between two number fields. The inner angle never exceeds the outer
		/// one: raising it past the outer angle stops at the outer angle, lowering the outer angle below it pulls it along.
		/// Only the angles that were edited are written, so editing several lights keeps their other angle.
		/// </summary>
		private static void DrawAngles(Rect row, SerializedProperty inner, SerializedProperty outer)
		{
			EditorGUI.BeginProperty(row, AnglesLabel, outer);
			EditorGUI.BeginProperty(row, AnglesLabel, inner);

			var fields = EditorGUI.PrefixLabel(row, AnglesLabel);

			// PrefixLabel returns an already indented rect: draw the controls without indenting them again.
			var indent = EditorGUI.indentLevel;
			EditorGUI.indentLevel = 0;

			var innerRect = new Rect(fields.x, fields.y, AngleFieldWidth, fields.height);
			var outerRect = new Rect(fields.xMax - AngleFieldWidth, fields.y, AngleFieldWidth, fields.height);
			var sliderRect = new Rect(innerRect.xMax + AngleFieldGap, fields.y,
				math.max(0f, outerRect.x - innerRect.xMax - 2f * AngleFieldGap), fields.height);

			var innerValue = math.clamp(inner.floatValue, 0f, MaxFullAngle);
			var outerValue = math.clamp(outer.floatValue, 0f, MaxFullAngle);

			EditorGUI.showMixedValue = inner.hasMultipleDifferentValues;
			EditorGUI.BeginChangeCheck();
			var newInner = EditorGUI.FloatField(innerRect, innerValue);
			var innerTyped = EditorGUI.EndChangeCheck();

			EditorGUI.showMixedValue = outer.hasMultipleDifferentValues;
			EditorGUI.BeginChangeCheck();
			var newOuter = EditorGUI.FloatField(outerRect, outerValue);
			var outerTyped = EditorGUI.EndChangeCheck();

			EditorGUI.showMixedValue = inner.hasMultipleDifferentValues || outer.hasMultipleDifferentValues;
			var slidInner = innerValue;
			var slidOuter = outerValue;
			EditorGUI.BeginChangeCheck();
			EditorGUI.MinMaxSlider(sliderRect, ref slidInner, ref slidOuter, 0f, MaxFullAngle);
			var slid = EditorGUI.EndChangeCheck();
			EditorGUI.showMixedValue = false;

			if (slid)
				ApplyAngles(inner, outer, slidInner, slidOuter, slidInner.Equals(innerValue) == false, slidOuter.Equals(outerValue) == false);
			else if (innerTyped)
				ApplyAngles(inner, outer, math.min(math.clamp(newInner, 0f, MaxFullAngle), outerValue), outerValue, true, false);
			else if (outerTyped)
				ApplyAngles(inner, outer, innerValue, math.clamp(newOuter, 0f, MaxFullAngle), false, true);

			EditorGUI.indentLevel = indent;

			EditorGUI.EndProperty();
			EditorGUI.EndProperty();
		}

		private static void ApplyAngles(SerializedProperty inner, SerializedProperty outer, float innerValue, float outerValue,
			bool innerChanged, bool outerChanged)
		{
			if (outerChanged)
			{
				outer.floatValue = outerValue;

				// A smaller outer angle pulls the inner angle along (on every selected light it would exceed).
				if (innerChanged == false && inner.hasMultipleDifferentValues == false && inner.floatValue > outerValue)
					innerChanged = true;
				innerValue = math.min(innerValue, outerValue);
			}

			if (innerChanged)
				inner.floatValue = innerValue;
		}

		private readonly GUIContent _alphaNote = new();
		private byte _noteAlpha;
		private float _noteIntensity = float.NaN;

		public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
		{
			return RowCount * EditorGUIUtility.singleLineHeight + (RowCount - 1) * EditorGUIUtility.standardVerticalSpacing;
		}

		public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
		{
			var step = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
			var row = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);

			EditorGUI.LabelField(row, label);

			var color = property.FindPropertyRelative(nameof(SpotLightData.Color));
			var intensity = property.FindPropertyRelative(nameof(SpotLightData.Intensity));

			using (new EditorGUI.IndentLevelScope())
			{
				row.y += step;
				EditorGUI.PropertyField(row, color);

				row.y += step;
				DrawAlphaNote(row, color, intensity);

				row.y += step;
				EditorGUI.PropertyField(row, intensity);

				row.y += step;
				EditorGUI.PropertyField(row, property.FindPropertyRelative(nameof(SpotLightData.Range)));

				row.y += step;
				DrawAngles(row,
					property.FindPropertyRelative(nameof(SpotLightData.InnerAngleDegrees)),
					property.FindPropertyRelative(nameof(SpotLightData.OuterAngleDegrees)));
			}
		}

		/// <summary>
		/// A mini label in the field column: what the alpha does and the resulting brightness.
		/// </summary>
		private void DrawAlphaNote(Rect row, SerializedProperty color, SerializedProperty intensity)
		{
			var note = new Rect(row.x + EditorGUIUtility.labelWidth, row.y, row.width - EditorGUIUtility.labelWidth, row.height);

			if (color.hasMultipleDifferentValues || intensity.hasMultipleDifferentValues)
			{
				EditorGUI.LabelField(note, MixedAlphaNote, EditorStyles.miniLabel);
				return;
			}

			var alpha = ((Color32)color.colorValue).a;
			var value = intensity.floatValue;

			if (alpha != _noteAlpha || value.Equals(_noteIntensity) == false)
			{
				_noteAlpha = alpha;
				_noteIntensity = value;

				var multiplier = alpha / 255f;
				_alphaNote.text = $"Alpha multiplies the intensity: {value:0.###} × {multiplier:0.###} = {value * multiplier:0.###}";
				_alphaNote.tooltip = "The color's alpha is an extra 0..1 brightness multiplier (stored in 8 bits with the color), "
				                     + "e.g. for fading a light without touching its intensity.";
			}

			EditorGUI.LabelField(note, _alphaNote, EditorStyles.miniLabel);
		}
	}
}
