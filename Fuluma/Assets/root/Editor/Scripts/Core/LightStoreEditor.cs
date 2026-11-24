using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Data;
using Saesentsessis.Fuluma.Utils;
using Unity.Collections.Specialized;
using UnityEditor;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Editor.Core
{
#if UNITY_6000_5_OR_NEWER
	// Immutable: a size computed once.
	[NoAutoStaticsCleanup]
#endif
	[CustomEditor(typeof(LightStore))]
	internal class LightStoreEditor : UnityEditor.Editor
	{
		// Per slot: packed transform + packed light data + allocator metadata.
		private static readonly int SpotLightSlotSize =
			PackedTransformData.Size + PackedSpotLightData.Size + UnsafeSlotAllocator.PackedElementSize;

		private static readonly int ClipBoxSlotSize = PackedClipBoxData.Size;

		/// <inheritdoc/>
		public override bool RequiresConstantRepaint() => true;

		/// <inheritdoc/>
		public override void OnInspectorGUI()
		{
			DrawDefaultInspector();

			var store = (LightStore)target;

			if (store.IsAllocated == false)
			{
				EditorGUILayout.HelpBox("Store is not initialized.", MessageType.Info);
				return;
			}

			var staticSpotCapacity = store.StaticSpotLightCapacity;
			var dynamicSpotCapacity = store.DynamicSpotLightCapacity;

			var clipBoxCapacity = store.ClipBoxIndexCount;
			
			var totalByteSize = (long)(staticSpotCapacity + dynamicSpotCapacity) * SpotLightSlotSize 
			                    + (long)clipBoxCapacity * ClipBoxSlotSize;

			EditorGUI.BeginDisabledGroup(true);

			EditorGUILayout.LabelField("Epoch:", store.Epoch.ToString(), EditorStyles.boldLabel);
			EditorGUILayout.LabelField("Static spot lights (count/capacity):",
				string.Concat(store.StaticSpotLightCount, "/", staticSpotCapacity), EditorStyles.boldLabel);
			EditorGUILayout.LabelField("Dynamic spot lights (count/capacity):",
				string.Concat(store.DynamicSpotLightCount, "/", dynamicSpotCapacity), EditorStyles.boldLabel);
			EditorGUILayout.LabelField("Clip Box (count/capacity):", string.Concat(store.ClipBoxCount, "/", clipBoxCapacity), EditorStyles.boldLabel);
			EditorGUILayout.LabelField("CPU buffers memory usage:", FormatUtils.FormatByteSize(totalByteSize), EditorStyles.boldLabel);

			EditorGUI.EndDisabledGroup();
		}
	}
}
