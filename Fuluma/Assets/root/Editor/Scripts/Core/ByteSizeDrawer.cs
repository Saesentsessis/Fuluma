using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Utils;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Saesentsessis.Fuluma.Editor.Core
{
	/// <summary>
	/// Draws an integer field with a grey line under it: the CPU and/or GPU memory its value allocates
	/// (<see cref="ByteSizeAttribute"/>), and the allocated count when the minimum or power-of-two rounding changes it.
	/// </summary>
	[CustomPropertyDrawer(typeof(ByteSizeAttribute))]
	internal class ByteSizeDrawer : PropertyDrawer
	{
		private const float LabelHeight = 14f;

		/// <summary>
		/// GPU bytes per item, including the per-buffer part times the sibling buffer count field (1 if it isn't found).
		/// </summary>
		private static long GpuBytesPerItem(ByteSizeAttribute size, SerializedProperty property)
		{
			if (size.BufferCountField == null)
				return size.GpuBytesPerItem;

			var path = property.propertyPath;
			var dot = path.LastIndexOf('.');
			var siblingPath = dot < 0 ? size.BufferCountField : string.Concat(path.Substring(0, dot + 1), size.BufferCountField);
			var buffers = property.serializedObject.FindProperty(siblingPath);
			var count = buffers is { propertyType: SerializedPropertyType.Integer } ? math.max(buffers.intValue, 0) : 1;

			return size.GpuBytesPerItem + (long)size.GpuBytesPerItemPerBuffer * count;
		}

		private static string SizeLabel(ByteSizeAttribute size, int value, long gpuBytesPerItem)
		{
			var count = size.AllocatedCount(value);

			var text = string.Empty;
			if (size.CpuBytesPerItem > 0)
				text = "CPU " + FormatUtils.FormatByteSize(count * size.CpuBytesPerItem);

			if (gpuBytesPerItem > 0)
				text += (text.Length > 0 ? ", GPU " : "GPU ") + FormatUtils.FormatByteSize(count * gpuBytesPerItem);

			if (count != value)
				text += $" (allocates {count})";

			return text;
		}

		/// <inheritdoc/>
		public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
		{
			if (property.propertyType != SerializedPropertyType.Integer)
				return EditorGUI.GetPropertyHeight(property, label);

			return LabelHeight + EditorGUI.GetPropertyHeight(property, label);
		}

		/// <inheritdoc/>
		public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
		{
			if (property.propertyType != SerializedPropertyType.Integer)
			{
				EditorGUI.PropertyField(position, property, label);
				return;
			}

			var propertyRect = new Rect(position.x, position.y, position.width, position.height - LabelHeight);
			var labelRect = new Rect(position.x, position.y + propertyRect.height, position.width, LabelHeight);

			EditorGUI.PropertyField(propertyRect, property, label);
			var size = (ByteSizeAttribute)attribute;
			EditorGUI.LabelField(labelRect, SizeLabel(size, property.intValue, GpuBytesPerItem(size, property)),
				EditorStyles.centeredGreyMiniLabel);
		}
	}
}
