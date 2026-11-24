using System.Collections.Generic;
using NUnit.Framework;
using Saesentsessis.Fuluma.Editor.Build;
using Saesentsessis.Fuluma.Rendering.URP;
using Saesentsessis.Shaders.Stripping;
using UnityEditor;
using UnityEngine;

namespace Saesentsessis.Fuluma.Tests.Editor
{
	/// <summary>
	/// Fuluma's keyword schemas against its shaders, and the variants each render feature keeps.
	/// </summary>
	public class FulumaShaderStrippingTests
	{
		private static IEnumerable<ShaderKeywordSchema> Schemas()
		{
			yield return FulumaShaderKeywords.SpotLight;
			yield return FulumaShaderKeywords.LightBuffer;
		}

		// A keyword added to a shader but not to its schema would be invisible to stripping (variants differing only in it
		// share a fate); one in the schema but not the shader makes stripping keep everything. Both directions must match.
		[TestCaseSource(nameof(Schemas))]
		public void Schema_MatchesTheShadersKeywords(ShaderKeywordSchema schema)
		{
			var shader = Shader.Find(schema.ShaderName);
			Assert.IsNotNull(shader, schema.ShaderName);

			var schemaKeywords = new HashSet<string>();
			foreach (var group in schema.Groups)
			foreach (var value in group.Values)
			{
				if (value.Keyword != null)
					schemaKeywords.Add(value.Keyword);
			}

			// Local keywords only: the space also lists global ones Unity adds (XR's STEREO_* keywords).
			var shaderKeywords = new HashSet<string>();
			foreach (var keyword in shader.keywordSpace.keywords)
			{
				if (keyword.isOverridable == false)
					shaderKeywords.Add(keyword.name);
			}

			CollectionAssert.AreEquivalent(shaderKeywords, schemaKeywords);
		}

		// Custom mode keeping everything counts every variant of every pass: the near fade group only exists in the inside
		// pass (960 + 480 for the spot light shader, not 2 x 960), the checkerboard only in the downsample pass (2 + 1 + 1).
		// Keeping one value per group keeps exactly one variant per pass.
		[TestCase("spotLight", 1440, 2)]
		[TestCase("lightBuffer", 4, 3)]
		public void Preview_CountsEveryPassWithItsOwnGroups(string schemaId, long variants, long oneValueEach)
		{
			var schema = schemaId == "spotLight" ? FulumaShaderKeywords.SpotLight : FulumaShaderKeywords.LightBuffer;
			var source = new FulumaShaderStrippingSource();
			var settings = new ShaderStrippingSettings { Mode = StrippingMode.Custom };

			var all = StrippingPreview.Create(source, settings, StrippingMode.Custom, schema, EditorUserBuildSettings.activeBuildTarget);
			Assert.IsNull(all.Error);
			Assert.IsNull(all.KeepEverythingReason);
			Assert.AreEqual(variants, all.Counts.Variants);
			Assert.AreEqual(variants, all.Counts.Kept);

			for (var g = 0; g < schema.Groups.Count; g++)
			for (var v = 1; v < schema.Groups[g].Count; v++)
				settings.SetDenied(schema, g, v, true);

			var one = StrippingPreview.Create(source, settings, StrippingMode.Custom, schema, EditorUserBuildSettings.activeBuildTarget);
			Assert.AreEqual(oneValueEach, one.Counts.Kept);
		}

		[Test]
		public void Feature_KeepsTheVariantsItCanDrawWith()
		{
			var feature = ScriptableObject.CreateInstance<FulumaRenderFeature>();
			try
			{
				var schema = FulumaShaderKeywords.SpotLight;
				var pattern = FulumaShaderStrippingSource.SpotLightPattern(feature);

				var expectedDepth = (1UL << FulumaShaderKeywords.DepthTexture) | (1UL << FulumaShaderKeywords.DepthLowResolution);
#if UNITY_6000_6_OR_NEWER
				expectedDepth |= (1UL << FulumaShaderKeywords.DepthInputAttachment) | (1UL << FulumaShaderKeywords.DepthInputAttachmentMsaa);
#endif
				Assert.AreEqual(expectedDepth, schema.GetValues(pattern, schema.IndexOf(FulumaShaderKeywords.DepthSource)),
					"Scene depth: both depth paths, so the light buffer scale can change at runtime.");
				Assert.AreEqual(0b10UL, schema.GetValues(pattern, schema.IndexOf(FulumaShaderKeywords.NearPlaneFade)));
				Assert.AreEqual(FulumaShaderKeywords.ClipClass.AllValues, schema.GetValues(pattern, schema.IndexOf(FulumaShaderKeywords.ClipClass)));
				Assert.AreEqual(0b01UL, schema.GetValues(pattern, schema.IndexOf(FulumaShaderKeywords.WideConePrecision)));
				Assert.AreEqual(0b01UL, schema.GetValues(pattern, schema.IndexOf(FulumaShaderKeywords.ScatteringPhase)));
				Assert.AreEqual(0b01UL, schema.GetValues(pattern, schema.IndexOf(FulumaShaderKeywords.DistanceFalloff)));
				Assert.AreEqual(0b01UL, schema.GetValues(pattern, schema.IndexOf(FulumaShaderKeywords.LampGlare)));

				var lightBuffer = FulumaShaderKeywords.LightBuffer;
				Assert.AreEqual(0b10UL, lightBuffer.GetValues(FulumaShaderStrippingSource.LightBufferPattern(feature), 0), "Depth aware.");

				feature.UseSceneDepth = false;
				feature.UseClipBoxes = false;
				feature.ScatteringPhase = ScatteringPhase.HenyeyGreenstein;
				feature.DistanceFalloff = DistanceFalloff.Unity;
				feature.LampGlare = LampGlare.Point;
				pattern = FulumaShaderStrippingSource.SpotLightPattern(feature);

				Assert.AreEqual(1UL << FulumaShaderKeywords.DepthNone, schema.GetValues(pattern, schema.IndexOf(FulumaShaderKeywords.DepthSource)));
				Assert.AreEqual(0b01UL, schema.GetValues(pattern, schema.IndexOf(FulumaShaderKeywords.ClipClass)));
				Assert.AreEqual(0b10UL, schema.GetValues(pattern, schema.IndexOf(FulumaShaderKeywords.ScatteringPhase)));
				Assert.AreEqual(0b10UL, schema.GetValues(pattern, schema.IndexOf(FulumaShaderKeywords.DistanceFalloff)));
				Assert.AreEqual(0b10UL, schema.GetValues(pattern, schema.IndexOf(FulumaShaderKeywords.LampGlare)));
				Assert.AreEqual(0b01UL, lightBuffer.GetValues(FulumaShaderStrippingSource.LightBufferPattern(feature), 0),
					"Without scene depth the buffer composites bilinearly.");
			}
			finally
			{
				Object.DestroyImmediate(feature);
			}
		}
	}
}
