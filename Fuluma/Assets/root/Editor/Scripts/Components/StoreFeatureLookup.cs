using System.Collections.Generic;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Rendering.URP;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Editor.Components
{
#if UNITY_6000_5_OR_NEWER
	// Scratch lists, cleared on every use.
	[NoAutoStaticsCleanup]
#endif
	/// <summary>
	/// Finds the <see cref="FulumaRenderFeature"/>s that draw a light store, across the render pipeline of every quality
	/// level (a level without its own pipeline asset uses the default one).
	/// </summary>
	/// <remarks>
	/// Reads URP's public renderer list (URP 14 has none, so there the pipeline asset's serialized list) and each
	/// renderer's feature list; no reflection. Stores are compared by the serialized override store (null =
	/// <see cref="LightStore.Default"/>), so the lookup never creates the default store.
	/// </remarks>
	internal static class StoreFeatureLookup
	{
		/// <summary>
		/// Pipeline assets already scanned during one lookup, with whether they draw the store.
		/// </summary>
		private static readonly List<(RenderPipelineAsset Asset, bool Draws)> ScannedAssets = new();

		/// <summary>
		/// Fills <paramref name="result"/> for the store a component names by <paramref name="overrideStore"/> (null: the
		/// default store).
		/// </summary>
		public static void Find(LightStore overrideStore, Result result)
		{
			result.Clear();
			ScannedAssets.Clear();

			var levelCount = QualitySettings.count;
			result.LevelCount = levelCount;
			string[] levelNames = null;

			for (var level = 0; level < levelCount; level++)
			{
				var asset = QualitySettings.GetRenderPipelineAssetAt(level);
				if (asset == null)
					asset = GraphicsSettings.defaultRenderPipeline;

				if (Draws(asset, overrideStore, result))
					continue;

				levelNames ??= QualitySettings.names;
				result.MissingLevels.Add(levelNames[level]);
			}

			ScannedAssets.Clear();
		}

		/// <summary>
		/// Whether any renderer of <paramref name="asset"/> has an active feature drawing the store; collects the renderer
		/// names on the first visit of each asset.
		/// </summary>
		private static bool Draws(RenderPipelineAsset asset, LightStore overrideStore, Result result)
		{
			foreach (var scanned in ScannedAssets)
			{
				if (scanned.Asset == asset)
					return scanned.Draws;
			}

			var draws = false;

			if (asset is UniversalRenderPipelineAsset universal)
			{
				result.AnyUniversalPipeline = true;

				foreach (var renderer in RendererDataList(universal))
				{
					if (renderer != null)
						draws |= ScanRenderer(renderer, overrideStore, result);
				}
			}

			ScannedAssets.Add((asset, draws));
			return draws;
		}

#if UNITY_6000_0_OR_NEWER
		private static System.ReadOnlySpan<ScriptableRendererData> RendererDataList(UniversalRenderPipelineAsset asset)
			=> asset.rendererDataList;
#else
		/// <summary>
		/// URP 14 keeps the renderer list in a serialized field only.
		/// </summary>
		private static IEnumerable<ScriptableRendererData> RendererDataList(UniversalRenderPipelineAsset asset)
		{
			using var serialized = new UnityEditor.SerializedObject(asset);
			var list = serialized.FindProperty("m_RendererDataList");
			if (list == null || list.isArray == false)
				yield break;

			for (var i = 0; i < list.arraySize; i++)
				yield return list.GetArrayElementAtIndex(i).objectReferenceValue as ScriptableRendererData;
		}
#endif

		private static bool ScanRenderer(ScriptableRendererData renderer, LightStore overrideStore, Result result)
		{
			var draws = false;

			foreach (var feature in renderer.rendererFeatures)
			{
				if (feature is not FulumaRenderFeature fuluma || fuluma.OverrideStore != overrideStore)
					continue;

				if (fuluma.isActive == false)
				{
					AddOnce(result.DisabledRenderers, renderer.name);
					continue;
				}

				draws = true;
				AddOnce(result.DrawingRenderers, renderer.name);

				if (fuluma.UseClipBoxes == false)
					AddOnce(result.NoClipBoxRenderers, renderer.name);
			}

			return draws;
		}

		private static void AddOnce(List<string> list, string name)
		{
			if (list.Contains(name) == false)
				list.Add(name);
		}

		/// <summary>
		/// What the lookup found for one store. Lists hold renderer and quality level names, without duplicates.
		/// </summary>
		internal sealed class Result
		{
			/// <summary>Renderers with an active feature drawing the store.</summary>
			public readonly List<string> DrawingRenderers = new();

			/// <summary>Renderers whose feature for the store is disabled.</summary>
			public readonly List<string> DisabledRenderers = new();

			/// <summary>Renderers drawing the store with the feature's Clip Boxes fidelity feature off.</summary>
			public readonly List<string> NoClipBoxRenderers = new();

			/// <summary>Quality levels whose pipeline draws the store on no renderer.</summary>
			public readonly List<string> MissingLevels = new();

			/// <summary>Whether any quality level renders with URP.</summary>
			public bool AnyUniversalPipeline;

			/// <summary>Number of quality levels.</summary>
			public int LevelCount;

			internal void Clear()
			{
				DrawingRenderers.Clear();
				DisabledRenderers.Clear();
				NoClipBoxRenderers.Clear();
				MissingLevels.Clear();
				AnyUniversalPipeline = false;
				LevelCount = 0;
			}
		}
	}
}
