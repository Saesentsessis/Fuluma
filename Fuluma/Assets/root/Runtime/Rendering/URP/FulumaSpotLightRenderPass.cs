using System;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Profiling;
using Saesentsessis.Fuluma.Rendering.Buffers;
using Saesentsessis.Fuluma.Rendering.Culling;
using Saesentsessis.Fuluma.Rendering.Proxy;
using Saesentsessis.Fuluma.Utils;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Rendering.URP
{
	// The shader has one model: inverse-square falloff, Gauss-Legendre quadrature in the airlight angle. Removed
	// alternatives (other sampling schemes, a reference raymarch, other falloffs) are archived in
	// Assets/Archive/SpotLightSampling.

	/// <summary>
	/// Draws the fake spot lights of one <see cref="LightStore"/>.
	/// </summary>
	/// <remarks>
	/// Light data lives in the store; its GPU copy is the store's shared GPU mirror (<c>SpotLightGpuMirror</c>), which
	/// uploads once per frame on its own. Per camera, this pass culls the lights on the CPU (<c>SpotLightCuller</c>,
	/// started from <c>OnCameraPreCull</c> and waited for when the graph executes),
	/// binds the mirror's buffers and the visible-index lists, and issues one instanced draw per pool (static, dynamic),
	/// clip class, shader pass and proxy LOD: the inside pass always with LOD0, the outside pass with the LOD culling
	/// picked from each light's screen size. The LODs of the <see cref="SpotLightProxyArrayDescriptor"/> are submeshes of
	/// one mesh.
	/// <para>
	/// Full-resolution draws read scene depth as a read-only depth input attachment where the platform supports it (no
	/// copy on tile GPUs; emulated as a texture elsewhere by RenderGraph), with the nearest of all samples on MSAA targets.
	/// Otherwise the pass requests URP's depth texture and reads <c>_CameraDepthTexture</c>.
	/// </para>
	/// <para>
	/// With a light buffer scale below 1 the lights draw into a downsampled light buffer: all of them, or with
	/// <see cref="LightBufferLights.LargeOnly"/> only the large and camera-inside ones (culling splits them off), the
	/// others drawing at full resolution first. The buffer's depth is made from the camera's depth attachment, or from
	/// URP's depth texture with MSAA.
	/// </para>
	/// <para>
	/// This file holds what doesn't depend on how URP runs the pass: settings, materials, culling and the draw loop.
	/// <c>FulumaSpotLightRenderPass.RenderGraph.cs</c> records it into URP's render graph (Unity 6).
	/// </para>
	/// </remarks>
#if UNITY_6000_5_OR_NEWER
	// Immutable: shader property IDs.
	[NoAutoStaticsCleanup]
#endif
	internal partial class FulumaSpotLightRenderPass : ScriptableRenderPass, IDisposable
	{
		/// <summary>
		/// Default of <see cref="MaxVisibleLights"/>.
		/// </summary>
		public const int DefaultMaxVisibleLights = 65536;

		/// <summary>
		/// Smallest piece limit <see cref="SetClipBoxes"/> accepts: pieces of a clipped light's ray integrated separately
		/// (3 covers a room with one cut-out).
		/// </summary>
		public const int MinClipBoxPieces = 3;

		/// <summary>
		/// Largest piece limit <see cref="SetClipBoxes"/> accepts: four box references leave a ray at most five pieces,
		/// so this limit is exact for any boxes.
		/// </summary>
		public const int MaxClipBoxPieces = 5;

		private const string RenderPassCouldNotBeInitializedMessage =
			"FulumaSpotLightRenderPass could not be initialized: it needs compute shaders and structured buffers (SSBOs) in vertex shaders.";

		private const string MissingDepthTextureMessage =
			"Fake spot lights need URP's depth texture here (no depth input attachment on this platform, or MSAA with a light buffer); enable 'Depth Texture' in the URP asset.";

		private const string MissingSpotLightShaderMessage =
			"The fake spot lights need a spot light shader (Hidden/Fuluma/Analytical/SpotLight); no lights are drawn.";

		private const string InvalidProxyDescriptorMessage =
			"The spot light proxy descriptor is invalid ({0}); using the built-in proxy LODs.";

		private const string MissingLightBufferShaderMessage =
			"The downsampled light buffer needs the shader '" + DownsampledLightBuffer.ShaderName + "'; drawing at full resolution.";

		private const string DroppedLightsMessage =
			"More fake spot lights are visible than the render feature's maximum; the rest are skipped. Raise the maximum if this is expected.";

		// Shader passes of Hidden/Fuluma/Analytical/SpotLight.
		private const int InsidePass = 0;
		private const int OutsidePass = 1;

		private const string DepthInputKeywordName = "_DEPTH_AS_INPUT_ATTACHMENT";
		private const string DepthInputMsaaKeywordName = "_DEPTH_AS_INPUT_ATTACHMENT_MSAA";
		private const string DepthLowResKeywordName = "_DEPTH_LOW_RES";
		private const string DepthNoneKeywordName = "_DEPTH_NONE";
		private const string ClipBoxesKeywordName = "_FULUMA_CLIP_BOXES";
		private const string ClipBoxSingleKeywordName = "_FULUMA_CLIP_BOX_SINGLE";
		private const string ClipBoxPairKeywordName = "_FULUMA_CLIP_BOX_PAIR";
		private const string ClipBoxUnionKeywordName = "_FULUMA_CLIP_BOX_UNION";
		private const string ClipBoxExcludeKeywordName = "_FULUMA_CLIP_BOX_EXCLUDE";
		private const string NearFadeKeywordName = "_FULUMA_NEAR_FADE";
		private const string WideConesKeywordName = "_FULUMA_WIDE_CONES";
		private const string PhaseHenyeyGreensteinKeywordName = "_FULUMA_PHASE_HG";
		private const string FalloffUnityKeywordName = "_FULUMA_FALLOFF_UNITY";
		private const string PointLampKeywordName = "_FULUMA_POINT_LAMP";

		/// <summary>
		/// Clip classes drawn with their own material (<see cref="SpotLightClipClassJob"/>): no boxes, one box, keep minus
		/// subtract, two keeps, two subtracts, general.
		/// </summary>
		private const int ClipClassCount = SpotLightClipClassJob.ClassCount;

		private static readonly int ClipBoxesId = Shader.PropertyToID("_FulumaClipBoxes");
		private static readonly int ClipBoxReferencesId = Shader.PropertyToID("_FulumaSpotClipBoxReferences");
		private static readonly int ClipPieceLimitId = Shader.PropertyToID("_FulumaClipPieceLimit");
		private static readonly int OutsideZTestId = Shader.PropertyToID("_FulumaOutsideZTest");

		private static readonly int TransformsId = Shader.PropertyToID("_FulumaSpotTransforms");
		private static readonly int LightsId = Shader.PropertyToID("_FulumaSpotLights");
		private static readonly int MsaaSampleCountId = Shader.PropertyToID("_FulumaMsaaSampleCount");
		private static readonly int IntensityId = Shader.PropertyToID("_Intensity");
		private static readonly int NearFadeDistanceId = Shader.PropertyToID("_FulumaNearFadeDistance");
		private static readonly int PhaseId = Shader.PropertyToID("_FulumaPhase");
		private static readonly int ProxyShapeId = Shader.PropertyToID("_FulumaSpotProxyShape");
		private static readonly int VisibleLightsId = Shader.PropertyToID("_FulumaVisibleSpotLights");
		private static readonly int VisibleOffsetId = Shader.PropertyToID("_FulumaVisibleOffset");
		private static readonly int LowResDepthId = Shader.PropertyToID("_FulumaLowResDepth");

		/// <summary>
		/// Whether full-resolution draws can read the camera's depth attachment as an input attachment. The API exists
		/// from Unity 6000.6; older versions always read URP's depth texture instead.
		/// </summary>
		private static bool DepthInputAttachmentSupported
#if UNITY_6000_6_OR_NEWER
			=> SystemInfo.supportsDepthAttachmentAsInputAttachment;
#else
			=> false;
#endif

		/// <summary>
		/// Whether URP runs passes through <c>Execute</c> (Compatibility Mode) instead of the render graph: always on
		/// Unity 2022.3 (URP 14), the Graphics settings' checkbox on 6.0-6.2, never from 6.3.
		/// </summary>
		internal static bool CompatibilityModeActive
#if UNITY_6000_3_OR_NEWER
			=> false;
#elif UNITY_6000_0_OR_NEWER
			=> GraphicsSettings.TryGetRenderPipelineSettings<RenderGraphSettings>(out var settings)
			   && settings.enableRenderCompatibilityMode;
#else
			=> true;
#endif

		/// <summary>
		/// <c>_FulumaPhase</c>: (a, b, m) of Henyey-Greenstein's phase function relative to isotropic scattering, which the
		/// shader evaluates as a / (b - m c)^1.5, c being the cosine of the scattering angle: a = 1 - g^2, b = 1 + g^2,
		/// m = 2g.
		/// </summary>
		private static Vector4 PhaseConstants(float anisotropy)
		{
			var g = math.clamp(anisotropy, 0f, 0.99f);
			return new Vector4(1f - g * g, 1f + g * g, 2f * g, 0f);
		}

		/// <summary>
		/// One light pass's draws, when it executes, through whichever command buffer URP gives it.
		/// </summary>
		/// <param name="lowResDepth">Downsampled buffer only: the low-resolution depth the shader reads; else null.</param>
		private static void ExecuteDraws<TCommands>(TCommands cmd, DrawPassData data, Texture lowResDepth)
			where TCommands : struct, IDrawCommands
		{
			// The dynamic transform buffer is locked until its copy job finishes; unlock before the GPU reads it.
			data.Mirror.CompleteUploads();

			var culler = data.Pass.FinishCulling(data.Camera);
			if (culler == null)
				return;

			// Per pool, each draw reads its own list of visible lights (offset into the index buffer). Culling decided per
			// light: camera near or inside its proxy: InsidePass, back faces without depth test, LOD0; otherwise OutsidePass,
			// depth-tested front faces, LOD by screen size. The property block is copied when a draw is recorded, so the
			// values set here hold for this pass's draws.
			var properties = data.Properties;
			properties.SetBuffer(VisibleLightsId, culler.Buffer);
			if (lowResDepth != null)
				properties.SetTexture(LowResDepthId, lowResDepth);

			DrawPool(cmd, data, culler, LightTransformMode.Static, properties);
			DrawPool(cmd, data, culler, LightTransformMode.Dynamic, properties);
		}

		/// <summary>
		/// The draws of <see cref="DrawPassData.DrawSet"/>: all lights; the small outside ones (full resolution next to
		/// the downsampled buffer); or the inside and large outside ones (the downsampled buffer). Without a large-light
		/// split, <see cref="SpotLightCuller.LargeDraw"/> is empty and the small outside draws hold every outside light.
		/// </summary>
		private static void DrawPool<TCommands>(TCommands cmd, DrawPassData data, SpotLightCuller culler, LightTransformMode pool,
			MaterialPropertyBlock properties) where TCommands : struct, IDrawCommands
		{
			var small = data.DrawSet != DrawSet.Large;
			var large = data.DrawSet != DrawSet.Small;
			var lodCount = math.min(culler.LodCount, data.LodShapes.Length);

			// Material by material (clip class), then region by region: blending is additive, so the order is free, and
			// each material is set once per shader pass instead of once per region.
			var materials = pool == LightTransformMode.Static ? data.StaticMaterials : data.DynamicMaterials;
			var classCount = data.ClipClasses ? ClipClassCount : 1;
			for (var clipClass = 0; clipClass < classCount; clipClass++)
			{
				var material = materials[clipClass];

				if (large)
					Draw(cmd, data, material, clipClass, InsidePass, 0, properties, culler.InsideDraw(pool, out var clipClasses), clipClasses);

				for (var lod = 0; lod < lodCount; lod++)
				{
					if (small)
						Draw(cmd, data, material, clipClass, OutsidePass, lod, properties, culler.OutsideDraw(pool, lod, out var clipClasses), clipClasses);

					if (large)
						Draw(cmd, data, material, clipClass, OutsidePass, lod, properties, culler.LargeDraw(pool, lod, out var clipClasses), clipClasses);
				}
			}
		}

		/// <summary>
		/// One clip class's lights of one region. Each class draws with its own variant: lights only pay for the clipping
		/// they use, and the general variant's registers don't slow the others. Without clip boxes the whole region is
		/// class 0. A region lists its classes in order (<paramref name="clipClasses"/>; the rest of the region is general).
		/// </summary>
		private static void Draw<TCommands>(TCommands cmd, DrawPassData data, Material material, int clipClass, int shaderPass,
			int lod, MaterialPropertyBlock properties, RangeInt lightsRange, SpotLightClipClassCounts clipClasses)
			where TCommands : struct, IDrawCommands
		{
			if (lightsRange.length == 0 || material == null)
				return;

			int start, count;
			if (data.ClipClasses == false)
			{
				start = lightsRange.start;
				count = lightsRange.length;
			}
			else
			{
				var before = 0;
				for (var c = 0; c < clipClass; c++)
					before += clipClasses[c];

				start = lightsRange.start + before;
				count = clipClass < SpotLightClipClassJob.General ? clipClasses[clipClass] : lightsRange.length - before;
			}

			if (count <= 0)
				return;

			properties.SetVector(ProxyShapeId, data.LodShapes[lod]);
			DrawLights(cmd, data, material, shaderPass, lod, properties, start, count);
		}

		private static void DrawLights<TCommands>(TCommands cmd, DrawPassData data, Material material, int shaderPass,
			int lod, MaterialPropertyBlock properties, int start, int count) where TCommands : struct, IDrawCommands
		{
			if (count <= 0)
				return;

			// The property block is copied when the draw is recorded.
			properties.SetInteger(VisibleOffsetId, start);
			cmd.DrawMeshInstancedProcedural(data.Mesh, lod, material, shaderPass, count, properties);
		}

		private readonly bool _depthAsInputAttachment;

		private SpotLightGpuMirror _mirror;

		/// <summary>
		/// Full-resolution draws (depth keywords set per camera).
		/// </summary>
		private Material _material;

		/// <summary>
		/// Draws into the downsampled buffer (<c>_DEPTH_LOW_RES</c>); null at full resolution. A material of its own because
		/// with <see cref="LightBufferLights.LargeOnly"/> both kinds of draw happen in one frame.
		/// </summary>
		private Material _lowResolutionMaterial;

		/// <summary>
		/// What changes per draw (the visible-light offset, the LOD's proxy shape) plus what only exists while the pass
		/// executes (the visible-index buffer, which can be recreated at upload, and the low-resolution depth). Unity copies a
		/// draw's whole block into every recorded draw and binds it again, so everything per camera or per pool lives on the
		/// materials instead (<see cref="BindMaterials"/>): that halved the per-draw CPU cost on Mali phones.
		/// </summary>
		private MaterialPropertyBlock _drawProperties;
		private MaterialPropertyBlock _lowResolutionDrawProperties;

		private LocalKeyword _depthInputKeyword;
		private LocalKeyword _depthInputMsaaKeyword;
		private LocalKeyword _depthLowResKeyword;
		private LocalKeyword _depthNoneKeyword;
		private LocalKeyword _clipBoxesKeyword;
		private LocalKeyword _clipBoxSingleKeyword;
		private LocalKeyword _clipBoxPairKeyword;
		private LocalKeyword _clipBoxUnionKeyword;
		private LocalKeyword _clipBoxExcludeKeyword;

		/// <summary>
		/// The materials each pool draws with, by clip class (<see cref="SpotLightClipClassJob"/>): [0] no boxes, [1] one box
		/// (<c>_FULUMA_CLIP_BOX_SINGLE</c>), [2] keep minus subtract (<c>_FULUMA_CLIP_BOX_PAIR</c>), [3] two keeps
		/// (<c>_FULUMA_CLIP_BOX_UNION</c>), [4] two subtracts (<c>_FULUMA_CLIP_BOX_EXCLUDE</c>), [5] the rest
		/// (<c>_FULUMA_CLIP_BOXES</c>). Each pool needs its own set because the pool's buffers are bound on the materials.
		/// The static set's [0] is <see cref="_material"/> / <see cref="_lowResolutionMaterial"/> itself (the settings
		/// holders); every other entry is a copy, created on first use (clip classes only while clip boxes are active, the
		/// dynamic set only once the dynamic pool has lights) and synced from the holder per camera.
		/// </summary>
		private readonly Material[] _staticMaterials = new Material[SpotLightClipClassJob.ClassCount];
		private readonly Material[] _staticLowResolutionMaterials = new Material[SpotLightClipClassJob.ClassCount];
		private readonly Material[] _dynamicMaterials = new Material[SpotLightClipClassJob.ClassCount];
		private readonly Material[] _dynamicLowResolutionMaterials = new Material[SpotLightClipClassJob.ClassCount];

		/// <summary>
		/// Bumped whenever a holder (<see cref="_material"/>, <see cref="_lowResolutionMaterial"/>) changes: a copy is
		/// re-synced from its holder only when its synced version is older (copying properties every camera cost ~0.08 ms
		/// per camera on a Mali-G52 phone).
		/// </summary>
		private int _holderVersion = 1;
		private readonly int[] _staticSyncedVersions = new int[SpotLightClipClassJob.ClassCount];
		private readonly int[] _staticLowResolutionSyncedVersions = new int[SpotLightClipClassJob.ClassCount];
		private readonly int[] _dynamicSyncedVersions = new int[SpotLightClipClassJob.ClassCount];
		private readonly int[] _dynamicLowResolutionSyncedVersions = new int[SpotLightClipClassJob.ClassCount];

		/// <summary>
		/// Whether this pass draws lights limited by their clip boxes (the Clip Boxes fidelity feature).
		/// </summary>
		private bool _clipBoxesEnabled;

		/// <summary>
		/// Whether the clip box variant and buffers are used for the camera being recorded.
		/// </summary>
		private bool _clipBoxesActive;
		private LocalKeyword _nearFadeKeyword;

		/// <summary>
		/// The near-plane fade distance while the fade is on, else 0: culling moves lights to the inside pass (the one that
		/// fades) that much earlier (see <see cref="SpotLightCullingCamera.Create"/>).
		/// </summary>
		private float _nearFadeCullingDistance;
		private LocalKeyword _wideConesKeyword;
		private LocalKeyword _phaseHenyeyGreensteinKeyword;
		private LocalKeyword _falloffUnityKeyword;
		private LocalKeyword _pointLampKeyword;

		/// <summary>
		/// Null at full resolution.
		/// </summary>
		private DownsampledLightBuffer _lightBuffer;

		/// <summary>
		/// Every proxy LOD: one mesh with a submesh per LOD, finest first.
		/// </summary>
		private Mesh _proxyMesh;

		/// <summary>
		/// <c>_FulumaSpotProxyShape</c> of each LOD.
		/// </summary>
		private Vector4[] _lodShapes;

		/// <summary>
		/// Triangles of each LOD's submesh.
		/// </summary>
		private int[] _lodTriangles;

		// Per LOD, for culling: switch sizes, and the shape the inside/outside test checks the camera against.
		private float[] _lodScreenSizes;
		private SpotLightProxyShape[] _lodProxyShapes;

		private SpotLightCuller _culler;

		/// <summary>
		/// Camera whose culling <see cref="BeginCulling"/> started and the next recording consumes.
		/// </summary>
		private Camera _cullingCamera;

		/// <summary>
		/// The current camera's culling was completed and uploaded by the first of its light passes to execute.
		/// </summary>
		private bool _cullingFinished;

		private readonly LightBufferLights _lightBufferLights;
		private readonly bool _useSceneDepth;
		private readonly float _largeLightScreenSize;

		/// <summary>
		/// Per camera, with <see cref="LightBufferLights.LargeOnly"/>: whether its last culling found lights for the
		/// downsampled buffer. Culling finishes after the graph is recorded, so this decides whether the next recording
		/// adds the buffer's passes; when it doesn't, the full-resolution pass draws every light.
		/// </summary>
		private readonly System.Collections.Generic.Dictionary<Camera, bool> _lowResolutionLightsLastFrame =
			new(ReferenceEqualityComparer<Camera>.Instance);

		private bool _warnedMissingDepth;
		private bool _warnedDroppedLights;

		/// <summary>
		/// <see cref="LightBufferTiming"/>'s early pass (null with <see cref="URP.LightBufferTiming.AfterOpaques"/> or no
		/// buffer).
		/// </summary>
		private FulumaLightBufferEarlyPass _earlyPass;
		private LightBufferTiming _lightBufferTiming = LightBufferTiming.AfterOpaques;

		/// <summary>
		/// The camera whose recording the early pass already prepared this frame (culling, keywords, buffers), and its
		/// MSAA sample count; the main pass then skips that. Cleared by every recording, so it never outlives a camera.
		/// </summary>
		private Camera _preparedCamera;
		private int _preparedMsaaSamples;

		/// <summary>
		/// The camera whose light buffer the early pass recorded this frame, and the buffer's targets for the composite.
		/// </summary>
		private Camera _earlyCamera;

		/// <summary>
		/// Whether the depth the early pass downsampled is the camera's (primed) depth attachment rather than the prepass
		/// depth texture.
		/// </summary>
		private bool _earlyDepthIsAttachment;

		/// <summary>
		/// Creates the pass's materials, proxy mesh, culler and (below full resolution) light buffer, and acquires the
		/// store's GPU mirror. On a device without compute shaders or vertex-stage structured buffers it logs an error
		/// and the pass draws nothing.
		/// </summary>
		/// <param name="store">The store whose spot lights this pass draws.</param>
		/// <param name="shader">The spot light shader (<c>Hidden/Fuluma/Analytical/SpotLight</c>).</param>
		/// <param name="initialCapacity">Capacity, in lights, of the store's GPU buffers if this pass creates them; they
		/// grow when needed.</param>
		/// <param name="proxyDescriptor">Proxy LODs; null uses the built-in set.</param>
		/// <param name="useLods">False: only the descriptor's LOD0 is built and drawn, and culling takes its fast path.</param>
		/// <param name="lightBufferScale">Resolution the lights are shaded at, as a fraction of the camera's width and
		/// height (see <see cref="LightBufferScaling"/>); 1 draws straight onto the camera.</param>
		/// <param name="lightBufferShader"><c>Hidden/Fuluma/LightBuffer</c> (found by name when null); only used when
		/// <paramref name="lightBufferScale"/> is below 1.</param>
		/// <param name="upsample">How a downsampled buffer is upscaled.</param>
		/// <param name="lightBufferLights">Which lights a downsampled buffer shades.</param>
		/// <param name="largeLightScreenSize">With <see cref="LightBufferLights.LargeOnly"/>: the screen size
		/// (bounding-sphere diameter over screen height) from which a light is drawn into the downsampled buffer.</param>
		/// <param name="useSceneDepth">False: the lights ignore the scene's geometry (no depth reads, no depth texture or
		/// prepass, a downsampled buffer without depth composited bilinearly). See <see cref="UsesSceneDepth"/>.</param>
		/// <param name="initialClipBoxCapacity">Capacity, in clip boxes, of the store's GPU box buffers if this pass creates
		/// them; they grow when needed.</param>
		/// <param name="dynamicBufferCount">Buffers the store's per-frame GPU data (dynamic transforms and clip boxes)
		/// rotates through, 1 to 3, if this pass creates them. Fewer buffers save memory; with fewer than the frames the GPU
		/// runs behind, the CPU may overwrite data the GPU still reads (a stall, or moving lights drawn a frame off).</param>
		/// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="shader"/> is
		/// null.</exception>
		public FulumaSpotLightRenderPass(LightStore store, Shader shader, int initialCapacity = 256,
			SpotLightProxyArrayDescriptor proxyDescriptor = null, bool useLods = true,
			float lightBufferScale = LightBufferScaling.Full, Shader lightBufferShader = null,
			LightBufferUpsample upsample = LightBufferUpsample.DepthAware,
			LightBufferLights lightBufferLights = LightBufferLights.All,
			float largeLightScreenSize = LightBufferScaling.DefaultLargeLightScreenSize, bool useSceneDepth = true,
			int initialClipBoxCapacity = SpotLightGpuMirror.MinimumClipBoxCapacity,
			int dynamicBufferCount = SpotLightGpuMirror.DefaultRingLength)
		{
			_lightBufferLights = lightBufferLights;
			_useSceneDepth = useSceneDepth;
			_largeLightScreenSize = math.max(LightBufferScaling.MinLargeLightScreenSize, largeLightScreenSize);

			if (store == null)
				throw new ArgumentNullException(nameof(store));

			// Without a shader the pass exists but draws nothing (see IsCreated).
			if (shader == null)
			{
				Debug.LogError(StringUtils.PrependFulumaPrefix(MissingSpotLightShaderMessage));
				return;
			}

			if (SystemInfo.supportsComputeShaders == false || SystemInfo.maxComputeBufferInputsVertex < 1)
			{
				Debug.LogError(StringUtils.PrependFulumaPrefix(RenderPassCouldNotBeInitializedMessage));
				return;
			}

			CreateMaterialAndProxy(shader, proxyDescriptor, useLods);
			// Depth-aware upscaling compares depths, which needs the scene depth.
			CreateLightBuffer(lightBufferScale, lightBufferShader, useSceneDepth ? upsample : LightBufferUpsample.Bilinear);

			_culler = new SpotLightCuller();
			_drawProperties = new MaterialPropertyBlock();
			_lowResolutionDrawProperties = new MaterialPropertyBlock();

			// The downsampled buffer reads its own low-resolution depth, made from the camera's depth attachment (URP's
			// depth texture with MSAA). Full-resolution draws (no buffer, or the small lights of LargeOnly) read the depth
			// input attachment where supported.
			_depthAsInputAttachment = _useSceneDepth && (_lightBuffer == null || SplitsLargeLights)
			                          && DepthInputAttachmentSupported;
			ConfigureDepthSource(1);

			_mirror = SpotLightGpuMirror.Acquire(store, initialCapacity, initialClipBoxCapacity, dynamicBufferCount);
		}

		/// <summary>
		/// Whether the lights read the scene's depth, so geometry hides the glow behind it. Off: no depth reads and no
		/// depth texture, prepass or copy requested from URP, and a downsampled buffer has no depth (bilinear composite,
		/// <see cref="URP.LightBufferTiming"/> has no effect); glow shows through all geometry.
		/// </summary>
		public bool UsesSceneDepth => _useSceneDepth;

		/// <summary>
		/// Store this pass draws.
		/// </summary>
		public LightStore Store => _mirror?.Store;

		/// <summary>
		/// Whether full-resolution draws read scene depth as an input attachment (true) or from
		/// <c>_CameraDepthTexture</c> (false). Draws into the downsampled buffer read its own low-resolution depth either
		/// way. Always false before Unity 6000.6, which has no depth input attachment API.
		/// </summary>
		public bool UsesDepthInputAttachment => _depthAsInputAttachment;

		/// <summary>
		/// Resolution the lights are shaded at, as a fraction of the camera's width and height (1: no light buffer).
		/// </summary>
		/// <remarks>Set only moves between downsampled scales (below 1, from the next camera); whether there is a light
		/// buffer at all is fixed at construction (it changes the depth source), so setting it on a full-resolution pass
		/// does nothing.</remarks>
		public float LightBufferScale
		{
			get => _lightBuffer?.Scale ?? LightBufferScaling.Full;
			internal set
			{
				if (_lightBuffer != null && LightBufferScaling.IsDownsampled(value))
					_lightBuffer.Scale = value;
			}
		}

		/// <summary>
		/// How the downsampled buffer is upscaled (meaningless at full resolution).
		/// </summary>
		public LightBufferUpsample Upsample => _lightBuffer?.Upsample ?? LightBufferUpsample.DepthAware;

		/// <summary>
		/// Which lights the downsampled buffer shades (meaningless at full resolution).
		/// </summary>
		public LightBufferLights LightBufferLights => _lightBufferLights;

		/// <summary>
		/// With <see cref="LightBufferLights.LargeOnly"/>: the screen size from which a light is drawn into the
		/// downsampled buffer.
		/// </summary>
		public float LargeLightScreenSize => _largeLightScreenSize;

		/// <summary>
		/// Most lights drawn per camera, over both pools. Visible lights past it are skipped (with a warning).
		/// </summary>
		public int MaxVisibleLights { get; set; } = DefaultMaxVisibleLights;

		/// <summary>
		/// With a downsampled light buffer: when it is shaded relative to URP's opaques (see
		/// <see cref="URP.LightBufferTiming"/> and <see cref="LightBufferBeforeOpaquesPass"/>). Before them, only the
		/// composite runs after the opaques, so URP's opaque and transparent passes stay one native render pass on
		/// tile-based GPUs. Enqueue <see cref="LightBufferBeforeOpaquesPass"/> along with this pass. No effect without a
		/// buffer or scene depth.
		/// </summary>
		public LightBufferTiming LightBufferTiming
		{
			get => _lightBufferTiming;
			set
			{
				_lightBufferTiming = value;

				if (value == LightBufferTiming.AfterOpaques || _lightBuffer == null || _useSceneDepth == false)
				{
					_earlyPass = null;
					return;
				}

				_earlyPass ??= new FulumaLightBufferEarlyPass(this);
				_earlyPass.RequestDepthPrepass(value == LightBufferTiming.BeforeOpaques);
			}
		}

		/// <summary>
		/// The pass that records the light buffer before the opaques, or null (<see cref="URP.LightBufferTiming.AfterOpaques"/>,
		/// no buffer, or no scene depth).
		/// </summary>
		public ScriptableRenderPass LightBufferBeforeOpaquesPass => _earlyPass;

		/// <summary>
		/// Whether this pass limits lights by their clip boxes (see <see cref="SetClipBoxes"/>).
		/// </summary>
		public bool ClipBoxesEnabled => _clipBoxesEnabled;

		/// <summary>
		/// Whether large and camera-inside lights go to the downsampled buffer while the rest draw at full resolution.
		/// </summary>
		internal bool SplitsLargeLights => _lightBuffer != null && _lightBufferLights == LightBufferLights.LargeOnly;

		/// <summary>
		/// The renderer data this pass draws for (set by the feature). <see cref="URP.LightBufferTiming.Auto"/> reads its
		/// settings to tell whether URP renders a depth prepass; without it, Auto draws after the opaques.
		/// </summary>
		internal UniversalRendererData RendererData { get; set; }

		/// <summary>
		/// With <see cref="SplitsLargeLights"/>: whether the last recorded camera got the downsampled buffer's passes (for
		/// tests).
		/// </summary>
		internal bool LastRecordedLowResolutionPasses { get; private set; }

		/// <summary>
		/// Whether the last recorded camera's light buffer was drawn before the opaques (<see cref="LightBufferTiming"/>; for
		/// tests).
		/// </summary>
		internal bool LastRecordedLightBufferBeforeOpaques { get; private set; }

		internal Material Material => _material;

		internal Material LowResolutionMaterial => _lowResolutionMaterial;

		internal Mesh ProxyMesh => _proxyMesh;

		/// <summary>
		/// Proxy LODs drawn (submeshes of <see cref="ProxyMesh"/>).
		/// </summary>
		internal int LodCount => _lodShapes?.Length ?? 0;

		internal SpotLightCuller Culler => _culler;

		/// <summary>
		/// Whether the last recorded camera's culling was started by <see cref="BeginCulling"/> (for tests).
		/// </summary>
		internal bool LastCullingStartedEarly { get; private set; }

		/// <summary>
		/// The GPU copy of the store this pass draws (for tests).
		/// </summary>
		internal SpotLightGpuMirror Mirror => _mirror;

		/// <summary>
		/// Whether the last recorded camera drew with the clip box variant (for tests).
		/// </summary>
		internal bool LastRecordedClipBoxes => _clipBoxesActive;

		/// <summary>
		/// Draw every clipped light's class with the general clip box variant, as when culling didn't order the lights by
		/// clip class (for tests).
		/// </summary>
		internal bool ForceGeneralClipVariant { get; set; }

		/// <summary>
		/// Whether the pass could be built (a shader, and a GPU that supports it); a pass that couldn't draws nothing.
		/// </summary>
		internal bool IsCreated => _material != null && _mirror != null;

		/// <summary>
		/// Fades the inside pass's glow to 0 where its proxy meets the near plane, over <paramref name="distance"/> (eye
		/// depth past the near plane), hiding the hard edge the near plane cuts when the camera is near a light. Off: the
		/// shader variant without the extra instructions.
		/// </summary>
		public void SetNearPlaneFade(bool enabled, float distance)
		{
			distance = math.max(1e-4f, distance);
			_nearFadeCullingDistance = enabled ? distance : 0f;

			foreach (var material in new[] { _material, _lowResolutionMaterial })
			{
				if (material == null)
					continue;

				material.SetKeyword(_nearFadeKeyword, enabled);
				material.SetFloat(NearFadeDistanceId, distance);
			}

			_holderVersion++;
		}

		/// <summary>
		/// Switches the lights to the cancellation-free cone intersection (shader variant <c>_FULUMA_WIDE_CONES</c>), exact
		/// near the axis of very wide cones, for a few more instructions per pixel. Off: the default variant.
		/// </summary>
		public void SetWideConePrecision(bool enabled)
		{
			foreach (var material in new[] { _material, _lowResolutionMaterial })
			{
				if (material != null)
					material.SetKeyword(_wideConesKeyword, enabled);
			}

			_holderVersion++;
		}

		/// <summary>
		/// The light model's optional parts, each a shader variant: the phase function (with its anisotropy, a material
		/// constant), the distance falloff and the lamp glare. The defaults (isotropic, range-scaled, no glare) are the
		/// variant without any of them.
		/// </summary>
		public void SetScattering(ScatteringPhase phase, float anisotropy, DistanceFalloff falloff, LampGlare glare)
		{
			var phaseConstants = PhaseConstants(anisotropy);

			foreach (var material in new[] { _material, _lowResolutionMaterial })
			{
				if (material == null)
					continue;

				material.SetKeyword(_phaseHenyeyGreensteinKeyword, phase == ScatteringPhase.HenyeyGreenstein);
				material.SetVector(PhaseId, phaseConstants);
				material.SetKeyword(_falloffUnityKeyword, falloff == DistanceFalloff.Unity);
				material.SetKeyword(_pointLampKeyword, glare == LampGlare.Point);
			}

			_holderVersion++;
		}

		/// <summary>
		/// Limits lights by their clip boxes (<see cref="Saesentsessis.Fuluma.Rendering.Components.FakeLightClipBox"/>).
		/// Off, or while the store has no boxes: the shader variant without them (the store's box buffers exist either
		/// way). A clipped ray is integrated in at most <paramref name="pieceLimit"/> pieces (<see cref="MinClipBoxPieces"/>
		/// to <see cref="MaxClipBoxPieces"/>); further pieces merge into the last one (their gaps glow). Applies immediately.
		/// </summary>
		public void SetClipBoxes(bool enabled, int pieceLimit)
		{
			_clipBoxesEnabled = enabled;

			pieceLimit = math.clamp(pieceLimit, MinClipBoxPieces, MaxClipBoxPieces);
			foreach (var material in new[] { _material, _lowResolutionMaterial })
			{
				if (material != null)
					material.SetFloat(ClipPieceLimitId, pieceLimit);
			}

			_holderVersion++;
		}

		/// <summary>
		/// Global multiplier on every light's brightness (the materials' <c>_Intensity</c>). Applies immediately.
		/// </summary>
		public void SetIntensity(float intensity)
		{
			if (_material != null)
				_material.SetFloat(IntensityId, intensity);

			if (_lowResolutionMaterial != null)
				_lowResolutionMaterial.SetFloat(IntensityId, intensity);

			_holderVersion++;
		}

		/// <summary>
		/// Starts this camera's culling early, so it runs on the worker threads while the main thread does URP's own
		/// culling, shadow culling and render graph recording. <see cref="FulumaRenderFeature"/> calls it from
		/// <c>OnCameraPreCull</c>; without it, recording schedules the culling itself. The draws wait for it when the
		/// graph executes.
		/// </summary>
		public void BeginCulling(Camera camera, Matrix4x4 view, Matrix4x4 projection, Vector3 position)
		{
			if (IsCreated == false || camera == null)
				return;

			ScheduleCulling(camera, view, projection, position);
		}

		/// <summary>
		/// Releases the culler, the light buffer, the materials and the proxy mesh, and this pass's hold on the store's
		/// GPU mirror (freed when no pass uses it any more).
		/// </summary>
		public void Dispose()
		{
			_culler?.Dispose();
			_culler = null;

			_clipBoxesEnabled = false;
			_mirror?.Release();
			_mirror = null;

			_lightBuffer?.Dispose();
			_lightBuffer = null;

			_drawProperties?.Clear();
			_drawProperties = null;

			_lowResolutionDrawProperties?.Clear();
			_lowResolutionDrawProperties = null;

			if (_proxyMesh != null)
			{
				CoreUtils.Destroy(_proxyMesh);
				_proxyMesh = null;
			}

			if (_material != null)
			{
				CoreUtils.Destroy(_material);
				_material = null;
			}

			if (_lowResolutionMaterial != null)
			{
				CoreUtils.Destroy(_lowResolutionMaterial);
				_lowResolutionMaterial = null;
			}

			// [0] of the static sets is the holder destroyed above; every other entry is a copy.
			for (var i = 0; i < SpotLightClipClassJob.ClassCount; i++)
			{
				if (i > 0)
				{
					CoreUtils.Destroy(_staticMaterials[i]);
					CoreUtils.Destroy(_staticLowResolutionMaterials[i]);
				}

				CoreUtils.Destroy(_dynamicMaterials[i]);
				CoreUtils.Destroy(_dynamicLowResolutionMaterials[i]);
				_staticMaterials[i] = null;
				_staticLowResolutionMaterials[i] = null;
				_dynamicMaterials[i] = null;
				_dynamicLowResolutionMaterials[i] = null;
			}

			_lowResolutionLightsLastFrame.Clear();
		}

		/// <summary>
		/// Per camera, before URP sets up its passes (the feature's <c>AddRenderPasses</c>). Depth as input attachment:
		/// read-only depth from tile memory. The downsampled buffer reads the depth attachment as a texture (see
		/// <c>LightBufferDepthSource</c>). Both need an intermediate target, since the backbuffer's depth can't be
		/// read; the composite also blends into an intermediate color target. URP's depth texture only where
		/// <see cref="NeedsCameraDepthTexture"/>.
		/// </summary>
		/// <param name="msaaSamples">The camera target's MSAA sample count.</param>
		internal void ConfigureDepthSource(int msaaSamples)
		{
			ConfigureInput(NeedsCameraDepthTexture(msaaSamples) ? ScriptableRenderPassInput.Depth : ScriptableRenderPassInput.None);
#if UNITY_6000_0_OR_NEWER
			requiresIntermediateTexture = _depthAsInputAttachment || _lightBuffer != null;
#endif
		}

		/// <summary>
		/// Whether this camera's draws need URP's <c>_CameraDepthTexture</c>: full-resolution draws without depth input
		/// attachments, the light buffer with MSAA (its passes read single samples), and every draw in Compatibility Mode.
		/// </summary>
		private bool NeedsCameraDepthTexture(int msaaSamples)
			=> _useSceneDepth
			   && (CompatibilityModeActive
			       || (_depthAsInputAttachment == false && (_lightBuffer == null || SplitsLargeLights))
			       || (_lightBuffer != null && msaaSamples > 1));

		/// <summary>
		/// Starts preparing a camera, by whichever pass records it first: takes the camera whose culling
		/// <see cref="BeginCulling"/> started (consumed whatever happens next, so an early return can't leave it to a
		/// later recording).
		/// </summary>
		/// <returns>False when there is nothing to draw.</returns>
		private bool TryBeginPreparingCamera(out Camera culledCamera)
		{
			culledCamera = _cullingCamera;
			_cullingCamera = null;
			return IsCreated && _mirror.StaticCount + _mirror.DynamicCount > 0;
		}

		/// <summary>
		/// Finishes preparing a camera once its MSAA sample count is known: depth keywords, the clip box variant, its
		/// culling (scheduled now if <see cref="BeginCulling"/> didn't run for it) and the materials' buffers.
		/// </summary>
		private void FinishPreparingCamera(int msaaSamples, Camera camera, Camera culledCamera, Matrix4x4 view,
			Matrix4x4 projection, Vector3 position)
		{
			SetDepthKeywords(msaaSamples);
			_clipBoxesActive = _clipBoxesEnabled && _mirror.ClipBoxesActive;
			EnsureCullingStarted(camera, culledCamera, view, projection, position);
			BindMaterials(msaaSamples);
			_preparedMsaaSamples = msaaSamples;
		}

		/// <summary>
		/// False, warned once, when this camera's draws need URP's <c>_CameraDepthTexture</c> and URP has none.
		/// </summary>
		private bool HasNeededDepthTexture(int msaaSamples, bool cameraDepthTextureExists)
		{
			if (NeedsCameraDepthTexture(msaaSamples) == false || cameraDepthTextureExists)
				return true;

			if (_warnedMissingDepth == false)
			{
				Debug.LogWarning(StringUtils.PrependFulumaPrefix(MissingDepthTextureMessage));
				_warnedMissingDepth = true;
			}

			return false;
		}

		private void FillPassData(DrawPassData data, bool lowResolution, Camera camera, DrawSet drawSet)
		{
			data.Mirror = _mirror;
			data.Mesh = _proxyMesh;
			data.LodShapes = _lodShapes;
			data.StaticMaterials = lowResolution ? _staticLowResolutionMaterials : _staticMaterials;
			data.DynamicMaterials = lowResolution ? _dynamicLowResolutionMaterials : _dynamicMaterials;
			data.ClipClasses = _clipBoxesActive;
			data.Properties = lowResolution ? _lowResolutionDrawProperties : _drawProperties;
			data.Pass = this;
			data.Camera = camera;
			data.DrawSet = drawSet;
		}

		/// <summary>
		/// With <see cref="SplitsLargeLights"/>: whether this camera's last culling had lights for the downsampled buffer
		/// (a camera not seen yet counts as yes). Bounded: forgets every camera once it has seen many.
		/// </summary>
		private bool LowResolutionLightsLastFrame(Camera camera)
		{
			if (_lowResolutionLightsLastFrame.Count > 32)
				_lowResolutionLightsLastFrame.Clear();

			return _lowResolutionLightsLastFrame.TryGetValue(camera, out var any) == false || any;
		}

		private void CreateMaterialAndProxy(Shader shader, SpotLightProxyArrayDescriptor proxyDescriptor, bool useLods)
		{
			var lodCount = useLods ? SpotLightProxyArrayDescriptor.MaxLodCount : 1;
			SpotLightProxyLods lods;
			try
			{
				lods = SpotLightProxyMeshBuilder.CreateLods(proxyDescriptor, lodCount);
			}
			catch (ArgumentException exception) when (proxyDescriptor != null)
			{
				// No LODs, a band below 3 vertices, or more vertices than 16-bit indices allow.
				Debug.LogError(StringUtils.PrependFulumaPrefix(string.Format(InvalidProxyDescriptorMessage, exception.Message)),
					proxyDescriptor);
				lods = SpotLightProxyMeshBuilder.CreateLods(null, lodCount);
			}
			_proxyMesh = lods.Mesh;
			_lodScreenSizes = lods.ScreenSizes;
			_lodShapes = new Vector4[lods.Count];
			_lodTriangles = new int[lods.Count];
			for (var lod = 0; lod < lods.Count; lod++)
			{
				_lodShapes[lod] = lods.Shapes[lod].ToVector();
				_lodTriangles[lod] = (int)(_proxyMesh.GetIndexCount(lod) / 3);
			}

			_lodProxyShapes = lods.Shapes;

			_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

			_depthInputKeyword = new LocalKeyword(shader, DepthInputKeywordName);
			_depthInputMsaaKeyword = new LocalKeyword(shader, DepthInputMsaaKeywordName);
			_depthLowResKeyword = new LocalKeyword(shader, DepthLowResKeywordName);
			_depthNoneKeyword = new LocalKeyword(shader, DepthNoneKeywordName);
			_clipBoxesKeyword = new LocalKeyword(shader, ClipBoxesKeywordName);
			_clipBoxSingleKeyword = new LocalKeyword(shader, ClipBoxSingleKeywordName);
			_clipBoxPairKeyword = new LocalKeyword(shader, ClipBoxPairKeywordName);
			_clipBoxUnionKeyword = new LocalKeyword(shader, ClipBoxUnionKeywordName);
			_clipBoxExcludeKeyword = new LocalKeyword(shader, ClipBoxExcludeKeywordName);
			_material.SetKeyword(_depthNoneKeyword, _useSceneDepth == false);
			_material.SetFloat(OutsideZTestId, (float)(_useSceneDepth ? CompareFunction.LessEqual : CompareFunction.Always));
			_nearFadeKeyword = new LocalKeyword(shader, NearFadeKeywordName);
			_wideConesKeyword = new LocalKeyword(shader, WideConesKeywordName);
			_phaseHenyeyGreensteinKeyword = new LocalKeyword(shader, PhaseHenyeyGreensteinKeywordName);
			_falloffUnityKeyword = new LocalKeyword(shader, FalloffUnityKeywordName);
			_pointLampKeyword = new LocalKeyword(shader, PointLampKeywordName);
		}

		private void CreateLightBuffer(float scale, Shader lightBufferShader, LightBufferUpsample upsample)
		{
			if (LightBufferScaling.IsDownsampled(scale) == false)
				return;

			var shader = lightBufferShader != null ? lightBufferShader : Shader.Find(DownsampledLightBuffer.ShaderName);
			if (shader == null)
			{
				Debug.LogError(StringUtils.PrependFulumaPrefix(MissingLightBufferShaderMessage));
				return;
			}

			_lightBuffer = new DownsampledLightBuffer(shader, scale, upsample);

			_lowResolutionMaterial = new Material(_material.shader) { hideFlags = HideFlags.HideAndDontSave };
			_lowResolutionMaterial.SetKeyword(_depthLowResKeyword, _useSceneDepth);
			_lowResolutionMaterial.SetKeyword(_depthNoneKeyword, _useSceneDepth == false);
			_lowResolutionMaterial.SetFloat(OutsideZTestId, _material.GetFloat(OutsideZTestId));
		}

		private void ScheduleCulling(Camera camera, Matrix4x4 view, Matrix4x4 projection, Vector3 position)
		{
			using var profilerScope = FulumaProfiling.CullingSchedule.Auto();

			var cullingCamera = SpotLightCullingCamera.Create(view, projection, position, camera.orthographic,
				camera.nearClipPlane, _lodScreenSizes, _lodProxyShapes, SplitsLargeLights ? _largeLightScreenSize : 0f,
				_nearFadeCullingDistance);

			var partitionByClipClass = _clipBoxesEnabled && _mirror.ClipBoxesActive && ForceGeneralClipVariant == false;
			_culler.Schedule(_mirror.Store, cullingCamera, _mirror.StaticCount, _mirror.DynamicCount, partitionByClipClass);
			_cullingCamera = camera;
			_cullingFinished = false;
			
			JobHandle.ScheduleBatchedJobs();
		}

		/// <summary>
		/// CPU culling for this camera (which lights are in the frustum, and which shader pass draws each). Normally
		/// already running since <c>OnCameraPreCull</c>; the render function waits for it, as late as possible.
		/// </summary>
		private void EnsureCullingStarted(Camera camera, Camera culledCamera, Matrix4x4 view, Matrix4x4 projection,
			Vector3 position)
		{
			LastCullingStartedEarly = culledCamera == camera;

			if (LastCullingStartedEarly == false)
				ScheduleCulling(camera, view, projection, position);
		}

		/// <summary>
		/// URP records and executes one camera before recording the next, so the material keywords set here hold for
		/// this camera's full-resolution draws (the downsampled buffer's material always has <c>_DEPTH_LOW_RES</c>). Keeps
		/// the pass free of global state changes (mergeable into the native render pass).
		/// </summary>
		private void SetDepthKeywords(int msaaSamples)
		{
			var single = _depthAsInputAttachment && msaaSamples <= 1;
			var multi = _depthAsInputAttachment && msaaSamples > 1;
			if (_material.IsKeywordEnabled(_depthInputKeyword) == single && _material.IsKeywordEnabled(_depthInputMsaaKeyword) == multi)
				return;

			_material.SetKeyword(_depthInputKeyword, single);
			_material.SetKeyword(_depthInputMsaaKeyword, multi);
			_holderVersion++;
		}

		/// <summary>
		/// Per camera, while it is recorded: every material this camera may draw with takes the holder's current state
		/// (keywords, settings), its clip class keyword, and its pool's buffers (the mirror uploaded them at
		/// <c>beginContextRendering</c> and may have recreated them), the clip boxes and the camera's MSAA sample count.
		/// Command buffers read material state when they run, and URP records and executes one camera before recording the
		/// next, so these hold for this camera's draws.
		/// </summary>
		private void BindMaterials(int msaaSamples)
		{
			using var profilerScope = FulumaProfiling.MaterialBinding.Auto();

			var classCount = _clipBoxesActive ? ClipClassCount : 1;
			if (_mirror.StaticCount > 0)
			{
				BindPool(_material, _staticMaterials, _staticSyncedVersions, classCount, LightTransformMode.Static, msaaSamples);
				if (_lowResolutionMaterial != null)
					BindPool(_lowResolutionMaterial, _staticLowResolutionMaterials, _staticLowResolutionSyncedVersions, classCount,
						LightTransformMode.Static, 1);
			}

			if (_mirror.DynamicCount > 0)
			{
				BindPool(_material, _dynamicMaterials, _dynamicSyncedVersions, classCount, LightTransformMode.Dynamic, msaaSamples);
				if (_lowResolutionMaterial != null)
					BindPool(_lowResolutionMaterial, _dynamicLowResolutionMaterials, _dynamicLowResolutionSyncedVersions, classCount,
						LightTransformMode.Dynamic, 1);
			}
		}

		private void BindPool(Material holder, Material[] materials, int[] syncedVersions, int classCount, LightTransformMode pool,
			int msaaSamples)
		{
			var dynamic = pool == LightTransformMode.Dynamic;
			var transforms = dynamic ? _mirror.DynamicTransforms : _mirror.StaticTransforms;
			var lights = dynamic ? _mirror.DynamicLights : _mirror.StaticLights;

			for (var clipClass = 0; clipClass < classCount; clipClass++)
			{
				var material = MaterialFor(holder, materials, syncedVersions, clipClass, dynamic);

				material.SetBuffer(TransformsId, transforms);
				material.SetBuffer(LightsId, lights);
				material.SetInteger(MsaaSampleCountId, msaaSamples);

				if (_clipBoxesActive == false)
					continue;

				material.SetBuffer(ClipBoxesId, _mirror.ClipBoxes);
				material.SetBuffer(ClipBoxReferencesId, dynamic ? _mirror.DynamicClipBoxReferences : _mirror.StaticClipBoxReferences);
			}
		}

		/// <summary>
		/// A pool's material for a clip class, created on first use and synced from the holder when the holder changed
		/// since (<see cref="_holderVersion"/>): the static set's plain material is the holder itself; every other one copies
		/// the holder's properties and keywords (the camera's depth keywords included) and adds its clip class keyword.
		/// </summary>
		private Material MaterialFor(Material holder, Material[] materials, int[] syncedVersions, int clipClass, bool dynamic)
		{
			if (clipClass == SpotLightClipClassJob.NoBoxes && dynamic == false)
				return materials[0] = holder;

			var material = materials[clipClass];
			if (material != null && syncedVersions[clipClass] == _holderVersion)
				return material;

			material = materials[clipClass] ??= new Material(holder.shader) { hideFlags = HideFlags.HideAndDontSave };
			syncedVersions[clipClass] = _holderVersion;
			material.CopyPropertiesFromMaterial(holder);
			material.SetKeyword(_clipBoxSingleKeyword, clipClass == SpotLightClipClassJob.OneBox);
			material.SetKeyword(_clipBoxPairKeyword, clipClass == SpotLightClipClassJob.KeepMinusSubtract);
			material.SetKeyword(_clipBoxUnionKeyword, clipClass == SpotLightClipClassJob.TwoKeeps);
			material.SetKeyword(_clipBoxExcludeKeyword, clipClass == SpotLightClipClassJob.TwoSubtracts);
			material.SetKeyword(_clipBoxesKeyword, clipClass == SpotLightClipClassJob.General);
			return material;
		}

		/// <summary>
		/// Waits for this camera's culling and uploads the visible indices, once: the first of the camera's light passes
		/// to execute does it. Called while the graph executes: URP submits each camera before recording the next, so the
		/// upload can't reach an earlier camera's draws. Null when nothing is visible.
		/// </summary>
		private SpotLightCuller FinishCulling(Camera camera)
		{
			if (_culler == null)
				return null;

			if (_cullingFinished == false)
			{
				_cullingFinished = true;

				using (FulumaProfiling.CullingWait.Auto())
					_culler.Complete(MaxVisibleLights);

				WarnOnceIfLightsDropped();
				RecordDrawStats();

				if (SplitsLargeLights && camera != null)
					_lowResolutionLightsLastFrame[camera] = _culler.LowResolutionLights > 0;

				if (_culler.Visible > 0)
				{
					using (FulumaProfiling.CullingUpload.Auto())
						_culler.Upload();
				}
			}

			return _culler.Visible == 0 ? null : _culler;
		}

		/// <summary>
		/// Lights and proxy triangles per LOD, for <see cref="FulumaProfiling"/>.
		/// </summary>
		private void RecordDrawStats()
		{
			var perLod = FulumaProfiling.SpotLightsPerLod;
			System.Array.Clear(perLod, 0, perLod.Length);

			var triangles = 0L;
			for (var lod = 0; lod < math.min(_culler.LodCount, _lodTriangles.Length); lod++)
			{
				var lights = _culler.OutsideDraw(LightTransformMode.Static, lod).length + _culler.OutsideDraw(LightTransformMode.Dynamic, lod).length
				             + _culler.LargeDraw(LightTransformMode.Static, lod).length + _culler.LargeDraw(LightTransformMode.Dynamic, lod).length;
				if (lod == 0)
					lights += _culler.InsideDraw(LightTransformMode.Static).length + _culler.InsideDraw(LightTransformMode.Dynamic).length;

				perLod[lod] = lights;
				triangles += (long)lights * _lodTriangles[lod];
			}

			FulumaProfiling.LastVisibleSpotLights = _culler.Visible;
			FulumaProfiling.LastSpotLightTriangles = triangles;
		}

		private void WarnOnceIfLightsDropped()
		{
			if (_culler.Dropped == 0 || _warnedDroppedLights)
				return;

			Debug.LogWarning(StringUtils.PrependFulumaPrefix(DroppedLightsMessage));
			_warnedDroppedLights = true;
		}

		/// <summary>
		/// Which visible lights a light pass draws.
		/// </summary>
		private enum DrawSet
		{
			/// <summary>
			/// Every visible light.
			/// </summary>
			All,

			/// <summary>
			/// The small outside lights (full resolution, next to the downsampled buffer).
			/// </summary>
			Small,

			/// <summary>
			/// The inside and large outside lights (the downsampled buffer).
			/// </summary>
			Large
		}

		/// <summary>
		/// The command buffer a light pass draws through: RenderGraph's raster command buffer, or URP's plain one in
		/// Compatibility Mode. The draw loop is generic over it (a struct), so neither path boxes or allocates.
		/// </summary>
		private interface IDrawCommands
		{
			void DrawMeshInstancedProcedural(Mesh mesh, int submesh, Material material, int shaderPass, int count,
				MaterialPropertyBlock properties);
		}

		/// <summary>
		/// What one light pass draws, filled when it is recorded (<see cref="FillPassData"/>).
		/// </summary>
		private class DrawPassData
		{
			public SpotLightGpuMirror Mirror;
			public FulumaSpotLightRenderPass Pass;
			public Camera Camera;
			public DrawSet DrawSet;
			public Mesh Mesh;
			public Vector4[] LodShapes;

			/// <summary>
			/// Each pool's materials by clip class (bound per camera, <see cref="BindMaterials"/>); only [0] is used while
			/// <see cref="ClipClasses"/> is false.
			/// </summary>
			public Material[] StaticMaterials;
			public Material[] DynamicMaterials;
			public bool ClipClasses;

			/// <summary>
			/// The per-draw block (visible-index buffer, offset, proxy shape; low-resolution depth for the buffer's pass).
			/// </summary>
			public MaterialPropertyBlock Properties;
		}
	}
}
