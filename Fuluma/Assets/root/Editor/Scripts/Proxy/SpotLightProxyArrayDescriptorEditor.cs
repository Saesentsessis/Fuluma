using System;
using Saesentsessis.Fuluma.Editor.Drawing;
using Saesentsessis.Fuluma.Rendering.Proxy;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Editor.Proxy
{
#if UNITY_6000_5_OR_NEWER
	// Immutable: labels and layout constants.
	[NoAutoStaticsCleanup]
#endif
	/// <summary>
	/// Inspector of a <see cref="SpotLightProxyArrayDescriptor"/>: a LOD bar like a LODGroup's above the LOD list, whose
	/// boundaries drag the LODs' screen sizes and whose context menu inserts and deletes LODs. The selected LOD's range is
	/// previewed in the Scene view (<see cref="ScreenSizePreview"/>).
	/// </summary>
	[CustomEditor(typeof(SpotLightProxyArrayDescriptor))]
	internal sealed class SpotLightProxyArrayDescriptorEditor : UnityEditor.Editor
	{
		private const float BarHeight = 40f;
		private const string LodsPath = "lods";
		private const string ScreenSizePath = "screenSize";

		private static readonly GUIContent BarLabel = new("LODs by screen size",
			"Drag a boundary to change where a LOD ends; right-click a LOD to insert or delete. Screen size is a light's "
			+ "bounding-sphere diameter over the screen height. The last LOD takes every smaller light.");

		private SerializedProperty _lods;

		/// <summary>
		/// Each LOD's <c>screenSize</c> property, looked up once per array change (lookups allocate).
		/// </summary>
		private SerializedProperty[] _screenSizeProperties = Array.Empty<SerializedProperty>();

		private float[] _screenSizes = Array.Empty<float>();
		private int _cachedCount = -1;
		private int _selected;

		/// <inheritdoc/>
		public override void OnInspectorGUI()
		{
			serializedObject.Update();

			var count = ReadScreenSizes();
			EditorGUILayout.LabelField(BarLabel, EditorStyles.boldLabel);
			var barRect = GUILayoutUtility.GetRect(0f, BarHeight, GUILayout.ExpandWidth(true));
			var sizes = _screenSizes.AsSpan(0, count);

			if (FulumaGui.LodBar(barRect, sizes, ref _selected, SpotLightProxyArrayDescriptor.MaxLodCount,
				    out var command, out var commandIndex))
			{
				WriteScreenSizes(sizes);
			}

			ApplyCommand(command, commandIndex, sizes);

			EditorGUILayout.Space();
			EditorGUILayout.PropertyField(_lods, true);

			serializedObject.ApplyModifiedProperties();

			UpdatePreview(sizes);
		}

		private void OnEnable() => _lods = serializedObject.FindProperty(LodsPath);

		private void OnDisable() => ScreenSizePreview.Hide();

		/// <summary>
		/// Reads every LOD's screen size into <see cref="_screenSizes"/>, refreshing the property cache when the array
		/// changed (an edit, a command, Undo).
		/// </summary>
		private int ReadScreenSizes()
		{
			var count = _lods.arraySize;
			if (count != _cachedCount)
				CacheScreenSizeProperties(count);

			for (var i = 0; i < count; i++)
				_screenSizes[i] = _screenSizeProperties[i].floatValue;

			return count;
		}

		private void CacheScreenSizeProperties(int count)
		{
			if (_screenSizeProperties.Length < count)
			{
				var capacity = math.max(count, SpotLightProxyArrayDescriptor.MaxLodCount);
				_screenSizeProperties = new SerializedProperty[capacity];
				_screenSizes = new float[capacity];
			}

			for (var i = 0; i < count; i++)
				_screenSizeProperties[i] = _lods.GetArrayElementAtIndex(i).FindPropertyRelative(ScreenSizePath);

			_cachedCount = count;
		}

		private void WriteScreenSizes(ReadOnlySpan<float> sizes)
		{
			for (var i = 0; i < sizes.Length; i++)
			{
				var property = _screenSizeProperties[i];
				if (property.floatValue != sizes[i])
					property.floatValue = sizes[i];
			}
		}

		/// <summary>
		/// Insert copies the LOD it goes before (bands included) and takes the upper half of its range on the bar's scale;
		/// Delete removes the LOD, keeping at least one.
		/// </summary>
		private void ApplyCommand(LodBarCommand command, int index, ReadOnlySpan<float> sizes)
		{
			var count = sizes.Length;
			if (index < 0 || index >= count)
				return;

			switch (command)
			{
				case LodBarCommand.InsertBefore when count < SpotLightProxyArrayDescriptor.MaxLodCount:
				{
					var top = FulumaGui.LodRangeTop(sizes, index);
					var bottom = FulumaGui.LodRangeBottom(sizes, index);
					var middle = FulumaGui.BarToScreenSize(0.5f * (FulumaGui.ScreenSizeToBar(top) + FulumaGui.ScreenSizeToBar(bottom)));

					// Duplicates element `index` at `index`; the copy becomes the new, finer LOD.
					_lods.InsertArrayElementAtIndex(index);
					CacheScreenSizeProperties(_lods.arraySize);
					_screenSizeProperties[index].floatValue = middle;
					_selected = index;
					break;
				}

				case LodBarCommand.Delete when count > 1:
					_lods.DeleteArrayElementAtIndex(index);
					CacheScreenSizeProperties(_lods.arraySize);
					_selected = math.min(_selected, count - 2);
					break;
			}
		}

		/// <summary>
		/// Shows the selected LOD's range in the Scene view, from the sizes read this event (a command applied in it shows
		/// from the next one).
		/// </summary>
		private void UpdatePreview(ReadOnlySpan<float> sizes)
		{
			if (sizes.Length == 0)
			{
				ScreenSizePreview.Hide();
				return;
			}

			var lod = math.clamp(_selected, 0, sizes.Length - 1);
			ScreenSizePreview.Show(FulumaGui.LodRangeTop(sizes, lod), FulumaGui.LodRangeBottom(sizes, lod));
		}
	}
}
