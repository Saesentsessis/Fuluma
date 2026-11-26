using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using System;
#else
using System.Collections.Generic;
#endif

namespace Saesentsessis.Fuluma.Editor.Core
{
	/// <summary>
	/// The renderers of a URP pipeline asset: URP 17's public list, or on URP 14, which has none, the asset's serialized
	/// list (read through <see cref="UnityEditor.SerializedObject"/>; no reflection). Entries can be null.
	/// </summary>
	internal static class UrpRendererDataList
	{
#if UNITY_6000_0_OR_NEWER
		public static ReadOnlySpan<ScriptableRendererData> Of(UniversalRenderPipelineAsset asset) => asset.rendererDataList;
#else
		public static IEnumerable<ScriptableRendererData> Of(UniversalRenderPipelineAsset asset)
		{
			using var serialized = new UnityEditor.SerializedObject(asset);
			var list = serialized.FindProperty("m_RendererDataList");
			if (list == null || list.isArray == false)
				yield break;

			for (var i = 0; i < list.arraySize; i++)
				yield return list.GetArrayElementAtIndex(i).objectReferenceValue as ScriptableRendererData;
		}
#endif
	}
}
