using Saesentsessis.Shaders.Stripping;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Editor.Build
{
#if UNITY_6000_5_OR_NEWER
	// Immutable: keyword schemas built once.
	[NoAutoStaticsCleanup]
#endif
	/// <summary>
	/// The keyword groups of Fuluma's shaders, as variant stripping sees them. Ids are stored in the project's settings:
	/// keep them, and list old ones as former ids when renaming. Keywords must match the shaders' <c>multi_compile_local</c>
	/// lines (a schema keyword the shader doesn't declare makes stripping keep everything, with a warning).
	/// </summary>
	internal static class FulumaShaderKeywords
	{
		public const string SpotLightShaderName = "Hidden/Fuluma/Analytical/SpotLight";
		public const string LightBufferShaderName = "Hidden/Fuluma/LightBuffer";

		public const int DepthTexture = 0;
		public const int DepthInputAttachment = 1;
		public const int DepthInputAttachmentMsaa = 2;
		public const int DepthLowResolution = 3;
		public const int DepthNone = 4;

		public static readonly KeywordGroup DepthSource = new("depthSource", "Scene Depth Source", new[]
		{
			KeywordValue.None("Depth texture"),
			KeywordValue.Of("_DEPTH_AS_INPUT_ATTACHMENT", "Depth input attachment"),
			KeywordValue.Of("_DEPTH_AS_INPUT_ATTACHMENT_MSAA", "Depth input attachment (MSAA)"),
			KeywordValue.Of("_DEPTH_LOW_RES", "Downsampled depth (light buffer)"),
			KeywordValue.Of("_DEPTH_NONE", "No scene depth")
		});

		public static readonly KeywordGroup NearPlaneFade = OnOff("nearPlaneFade", "Near Plane Fade", "_FULUMA_NEAR_FADE");

		public static readonly KeywordGroup ClipClass = new("clipClass", "Clip Boxes", new[]
		{
			KeywordValue.None("No clip boxes"),
			KeywordValue.Of("_FULUMA_CLIP_BOX_SINGLE", "One box"),
			KeywordValue.Of("_FULUMA_CLIP_BOX_PAIR", "Keep minus subtract"),
			KeywordValue.Of("_FULUMA_CLIP_BOX_UNION", "Two keeps"),
			KeywordValue.Of("_FULUMA_CLIP_BOX_EXCLUDE", "Two subtracts"),
			KeywordValue.Of("_FULUMA_CLIP_BOXES", "General (3-4 boxes)")
		});

		public static readonly KeywordGroup WideConePrecision = OnOff("wideConePrecision", "Wide Cone Precision", "_FULUMA_WIDE_CONES");

		public static readonly KeywordGroup ScatteringPhase = new("scatteringPhase", "Scattering Phase", new[]
		{
			KeywordValue.None("Isotropic"),
			KeywordValue.Of("_FULUMA_PHASE_HG", "Henyey-Greenstein")
		});

		public static readonly KeywordGroup DistanceFalloff = new("distanceFalloff", "Distance Falloff", new[]
		{
			KeywordValue.None("Range Scaled"),
			KeywordValue.Of("_FULUMA_FALLOFF_UNITY", "Unity")
		});

		public static readonly KeywordGroup LampGlare = new("lampGlare", "Lamp Glare", new[]
		{
			KeywordValue.None("None"),
			KeywordValue.Of("_FULUMA_POINT_LAMP", "Point")
		});

		public static readonly KeywordGroup Upsample = new("upsample", "Light Buffer Upsample", new[]
		{
			KeywordValue.None("Bilinear"),
			KeywordValue.Of("_FULUMA_CHECKERBOARD", "Depth Aware")
		});

		public static readonly ShaderKeywordSchema SpotLight = new("spotLight", SpotLightShaderName, "Spot Light Shader", new[]
		{
			DepthSource, NearPlaneFade, ClipClass, WideConePrecision, ScatteringPhase, DistanceFalloff, LampGlare
		});

		public static readonly ShaderKeywordSchema LightBuffer = new("lightBuffer", LightBufferShaderName, "Light Buffer Shader", new[]
		{
			Upsample
		});

		private static KeywordGroup OnOff(string id, string displayName, string keyword)
			=> new(id, displayName, new[] { KeywordValue.None("Off"), KeywordValue.Of(keyword, "On") });
	}
}
