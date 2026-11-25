using System.Collections.Generic;
using Saesentsessis.Fuluma.Editor.Core;
using Saesentsessis.Fuluma.Rendering.URP;
using Saesentsessis.Shaders.Stripping;
using UnityEditor;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Editor.Build
{
#if UNITY_6000_5_OR_NEWER
	// Immutable: the schema list.
	[NoAutoStaticsCleanup]
#endif
	/// <summary>
	/// Fuluma's shaders for variant stripping: in Automatic mode, each <see cref="FulumaRenderFeature"/> on a renderer of the
	/// build's URP assets keeps the variants it can draw with.
	/// </summary>
	/// <remarks>
	/// A feature keeps both depth paths (full resolution and the downsampled light buffer) whenever it reads scene depth,
	/// so changing its Light Buffer Scale from a script works in builds. Everything else follows its settings as they are
	/// at build time; settings that scripts change at runtime need Custom mode or a shader variant collection.
	/// </remarks>
	internal sealed class FulumaShaderStrippingSource : IShaderStrippingSource
	{
		private static readonly ShaderKeywordSchema[] SchemaList = { FulumaShaderKeywords.SpotLight, FulumaShaderKeywords.LightBuffer };

		/// <summary>
		/// The spot light variants <paramref name="feature"/> can draw with.
		/// </summary>
		internal static VariantBits SpotLightPattern(FulumaRenderFeature feature)
		{
			var schema = FulumaShaderKeywords.SpotLight;
			var pattern = schema.AllValues;

			pattern = schema.WithValues(pattern, FulumaShaderKeywords.DepthSource, DepthSources(feature));
			pattern = schema.WithValue(pattern, FulumaShaderKeywords.NearPlaneFade, feature.UseNearPlaneFade ? 1 : 0);
			if (feature.UseClipBoxes == false)
				pattern = schema.WithValue(pattern, FulumaShaderKeywords.ClipClass, 0);

			pattern = schema.WithValue(pattern, FulumaShaderKeywords.WideConePrecision, feature.UseWideConePrecision ? 1 : 0);
			pattern = schema.WithValue(pattern, FulumaShaderKeywords.ScatteringPhase,
				feature.ScatteringPhase == ScatteringPhase.HenyeyGreenstein ? 1 : 0);
			pattern = schema.WithValue(pattern, FulumaShaderKeywords.DistanceFalloff, feature.DistanceFalloff == DistanceFalloff.Unity ? 1 : 0);
			pattern = schema.WithValue(pattern, FulumaShaderKeywords.LampGlare, feature.LampGlare == LampGlare.Point ? 1 : 0);
			return pattern;
		}

		/// <summary>
		/// The light buffer variant <paramref name="feature"/> composites with (kept even at full resolution, for a scale
		/// lowered at runtime). Without scene depth the buffer is always composited bilinearly.
		/// </summary>
		internal static VariantBits LightBufferPattern(FulumaRenderFeature feature)
		{
			var schema = FulumaShaderKeywords.LightBuffer;
			var depthAware = feature.UseSceneDepth && feature.LightBufferUpsample == LightBufferUpsample.DepthAware;
			var pattern = schema.WithValue(schema.AllValues, FulumaShaderKeywords.Upsample, depthAware ? 1 : 0);

			// Only the Compatibility path samples URP's depth texture (with MSAA, or without an intermediate depth), and
			// only with scene depth. Whether it runs is the project's Compatibility Mode setting when the build starts.
			if (feature.UseSceneDepth == false || FulumaSpotLightRenderPass.CompatibilityModeActive == false)
				pattern = schema.WithValue(pattern, FulumaShaderKeywords.LightBufferDepth, 0);

			return pattern;
		}

		/// <summary>
		/// Without scene depth: none. With it: the depth texture and the downsampled depth, plus both input attachment
		/// variants where Unity supports them (6000.6+; whether a device does is only known at runtime, and the MSAA one
		/// depends on the camera's target, which builds can't see).
		/// </summary>
		private static ulong DepthSources(FulumaRenderFeature feature)
		{
			if (feature.UseSceneDepth == false)
				return 1UL << FulumaShaderKeywords.DepthNone;

			var sources = (1UL << FulumaShaderKeywords.DepthTexture) | (1UL << FulumaShaderKeywords.DepthLowResolution);
#if UNITY_6000_6_OR_NEWER
			sources |= (1UL << FulumaShaderKeywords.DepthInputAttachment) | (1UL << FulumaShaderKeywords.DepthInputAttachmentMsaa);
#endif
			return sources;
		}

		public string Id => "com.saesentsessis.fuluma";

		public string DisplayName => "Fuluma";

		public IReadOnlyList<ShaderKeywordSchema> Schemas => SchemaList;

		public ShaderStrippingSettings Settings => FulumaProjectSettings.instance.Stripping;

		public void CollectAutomaticPatterns(StrippingContext context, ShaderKeywordSchema schema, List<VariantBits> patterns,
			List<string> sourceNames)
		{
			var spotLight = schema == FulumaShaderKeywords.SpotLight;
			if (spotLight == false && schema != FulumaShaderKeywords.LightBuffer)
				return;

			foreach (var asset in context.PipelineAssets)
			{
				if (asset is not UniversalRenderPipelineAsset universal)
					continue;

				foreach (var renderer in UrpRendererDataList.Of(universal))
				{
					if (renderer == null)
						continue;

					foreach (var feature in renderer.rendererFeatures)
					{
						if (feature is not FulumaRenderFeature fuluma || (fuluma.isActive == false && context.CountInactiveSources == false))
							continue;

						var pattern = spotLight ? SpotLightPattern(fuluma) : LightBufferPattern(fuluma);
						if (patterns.Contains(pattern) == false)
							patterns.Add(pattern);

						var name = $"{AssetDatabase.GetAssetPath(renderer)} > {fuluma.name}";
						if (sourceNames.Contains(name) == false)
							sourceNames.Add(name);
					}
				}
			}
		}
	}
}
