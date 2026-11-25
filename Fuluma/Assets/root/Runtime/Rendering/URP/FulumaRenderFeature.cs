using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Rendering.Buffers;
using Saesentsessis.Fuluma.Rendering.Proxy;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Saesentsessis.Fuluma.Rendering.URP
{
	/// <summary>
	/// URP renderer feature that draws the fake volumetric spot lights of one <see cref="LightStore"/>: per camera it
	/// culls them on the CPU and draws them additively with an analytical shader, at full resolution or through a
	/// downsampled light buffer.
	/// </summary>
	/// <remarks>
	/// Add it to a Universal Renderer (RenderGraph only, no Compatibility Mode). Some property setters recreate the
	/// feature's pass; each property says which.
	/// </remarks>
	public class FulumaRenderFeature : ScriptableRendererFeature
	{
		/// <summary>
		/// The features a new render feature starts with: everything except <see cref="FidelityFeatures.WideConePrecision"/>.
		/// </summary>
		public const FidelityFeatures DefaultFidelityFeatures = FidelityFeatures.SceneDepth | FidelityFeatures.ClipBoxes
		                                                         | FidelityFeatures.NearPlaneFade | FidelityFeatures.SpotLightLods;

		/// <summary>
		/// Every fidelity feature; serialized bits outside it are dropped.
		/// </summary>
		internal const FidelityFeatures AllFidelityFeatures = FidelityFeatures.SceneDepth | FidelityFeatures.ClipBoxes
		                                                    | FidelityFeatures.NearPlaneFade | FidelityFeatures.SpotLightLods
		                                                    | FidelityFeatures.WideConePrecision;

		private const string DefaultSpotLightShaderName = "Hidden/Fuluma/Analytical/SpotLight";
		private const float DefaultLightIntensity = 1f;
		private const float DefaultNearPlaneFadeDistance = 0.5f;
		private const float MinNearPlaneFadeDistance = 0.001f;
		private const float DefaultScatteringAnisotropy = 0.7f;
		private const float MaxScatteringAnisotropy = 0.95f;

		/// <summary>
		/// <paramref name="value"/> clamped to [min, max], or <paramref name="fallback"/> when it isn't finite.
		/// </summary>
		private static float ClampFinite(float value, float min, float max, float fallback)
			=> math.isfinite(value) ? math.clamp(value, min, max) : fallback;

		[Tooltip("When in URP's frame the lights are drawn (default: after the skybox, so the sky doesn't cover the glow). With Light Buffer Timing, part of a downsampled light buffer is recorded after URP's prepasses instead.")]
		[SerializeField] private RenderPassEvent @event = RenderPassEvent.AfterRenderingSkybox;

		[Header("Lights")]
		[Tooltip("Store whose fake lights this feature draws. Empty: the default store, where fake light components register unless they name a store of their own. Lights in any other store need a feature of their own that uses that store.")]
		[SerializeField] private LightStore overrideStore;

		[Tooltip("Cameras draw this feature's fake lights only if their culling mask includes one of these layers, as if the lights lived on them. Preview cameras never draw them.")]
		[SerializeField] private LayerMask cameraLayers = ~0;

		[Tooltip("Global multiplier on the brightness of every fake light this feature draws, whatever its type.")]
		[SerializeField, Min(0f)] private float lightIntensity = DefaultLightIntensity;

		[Header("Scattering")]
		[Tooltip("How the haze scatters light toward the camera. Isotropic: equally in every direction, a beam looks the same from every side. Henyey-Greenstein: mostly onward (as real haze does), so beams look faint from the side and bright when looking toward their lamp.")]
		[SerializeField] private ScatteringPhase scatteringPhase = ScatteringPhase.Isotropic;

		[Tooltip("Henyey-Greenstein only: how strongly the haze scatters onward (the mean cosine of the scattering angle). 0 scatters equally in every direction; toward 1 nearly all light keeps its direction (beams only show when looking toward their lamp). Above 0.8 the glow toward the lamp gets less accurate.")]
		[SerializeField, Range(0f, MaxScatteringAnisotropy)] private float scatteringAnisotropy = DefaultScatteringAnisotropy;

		[Tooltip("How the glow fades with the distance from the lamp. Range Scaled: inverse square scaled by the light's range, so a light looks the same at any size. Unity: as URP's lights, inverse square in world units with URP's fade toward the range; the intensity means what a Unity Light's does (an isotropic beam glows with the Light's attenuation summed along the view ray).")]
		[SerializeField] private DistanceFalloff distanceFalloff = DistanceFalloff.RangeScaled;

		[Tooltip("How bright a light gets at its lamp. None: the lamp has a small size (2% of the range), keeping the glow next to it finite. Point: a point lamp, glaring where rays pass close to it.")]
		[SerializeField] private LampGlare lampGlare = LampGlare.None;

		[Header("Fidelity")]
		[Tooltip("Optional parts of the rendering, each trading quality against cost. Scene Depth: geometry hides the glow and stops it at surfaces (off: glow shows through everything, no depth reads). Clip Boxes: lights are limited by their FakeLightClipBoxes (free while the store has none). Near Plane Fade: hides the hard edge the near plane cuts through a light the camera is at. Wide Cone Precision: exact glow near the axis of lights wider than about 160 degrees (a few more instructions on every light). Spot Light LODs: proxy mesh LOD per light by screen size (off: always LOD0).")]
		[SerializeField] private FidelityFeatures fidelityFeatures = DefaultFidelityFeatures;

		[Tooltip("Distance past the near clipping plane (along the view direction) over which the glow fades in.")]
		[SerializeField, Min(MinNearPlaneFadeDistance)] private float nearPlaneFadeDistance = DefaultNearPlaneFadeDistance;

		[Header("Memory")]
		[Tooltip("GPU buffers that per-frame data (dynamic lights' transforms, dynamic clip boxes) rotates through, used when the store's GPU buffers are first created. Each frame writes the next one, so the GPU can still read the frames before. 3 covers a GPU running one to two frames behind; 2 saves memory where the GPU runs at most one frame behind; 1 rewrites the buffer the GPU may still be reading (a stall, or moving lights and boxes a frame off), fine without dynamic lights or boxes.")]
		[SerializeField, Range(SpotLightGpuMirror.MinimumRingLength, SpotLightGpuMirror.MaximumRingLength)]
		private int dynamicBufferCount = SpotLightGpuMirror.DefaultRingLength;

		[Header("Spot Lights")]
		[Tooltip("Proxy mesh LODs for spot lights, picked per light by its size on screen. The built-in set is used when left empty.")]
		[SerializeField] private SpotLightProxyArrayDescriptor spotLightDescriptor;

		[Tooltip("Most fake spot lights drawn per camera, static and dynamic together. Visible lights past it are skipped, with a warning.")]
		[SerializeField, Min(1)] private int maxSpotLights = FulumaSpotLightRenderPass.DefaultMaxVisibleLights;

		[Tooltip("Initial capacity, in spot lights, of the store's GPU buffers (used when they are first created). They grow when needed.")]
		[SerializeField, Min(1), ByteSize(0, SpotLightGpuMirror.GpuBytesPerLight, SpotLightGpuMirror.MinimumCapacity, roundUpToPowerOfTwo: true,
			 gpuBytesPerItemPerBuffer: SpotLightGpuMirror.GpuBytesPerLightPerRingBuffer, bufferCountField: nameof(dynamicBufferCount))]
		private int spotLightBufferSize = 256;

		[Tooltip("Hidden/Fuluma/Analytical/SpotLight by default (filled in by the Editor when empty, so builds include it).")]
		[SerializeField] private Shader spotLightShader;

		[Header("Clip Boxes")]
		[Tooltip("Most pieces a clipped light's ray is integrated in. Each piece costs one more integration on the pixels that have it; past the limit, further pieces merge into the last one (the gaps between them glow). 3 covers rooms with one cut-out; 5 is exact for any 4 boxes.")]
		[SerializeField, Range(FulumaSpotLightRenderPass.MinClipBoxPieces, FulumaSpotLightRenderPass.MaxClipBoxPieces)]
		private int clipBoxPieceLimit = FulumaSpotLightRenderPass.MinClipBoxPieces;

		[Tooltip("Initial capacity, in clip boxes, of the store's GPU box buffers (used when they are first created). They grow when needed.")]
		[SerializeField, Min(1), ByteSize(0, SpotLightGpuMirror.GpuBytesPerClipBox, SpotLightGpuMirror.MinimumClipBoxCapacity, roundUpToPowerOfTwo: true,
			 gpuBytesPerItemPerBuffer: SpotLightGpuMirror.GpuBytesPerClipBoxPerRingBuffer, bufferCountField: nameof(dynamicBufferCount))]
		private int clipBoxBufferSize = SpotLightGpuMirror.MinimumClipBoxCapacity;

		[Header("Light Buffer")]
		[Tooltip("Resolution the lights are shaded at, as a fraction of the camera's width and height (0.5 = a quarter of the pixels). Below 1 they draw into a smaller buffer that is then upscaled onto the camera; the buffer reads the camera's depth attachment directly, and URP's depth texture only with MSAA. 1 draws straight onto the camera.")]
		[SerializeField, Range(LightBufferScaling.Min, LightBufferScaling.Full)] private float lightBufferScale = 0.5f;

		[Tooltip("Which lights a downsampled light buffer shades. All: every light. Large Only: lights at least 'Large Light Screen Size' on screen, and lights the camera is inside of; smaller lights draw at full resolution (one more pass; consider All on tile-based mobile GPUs).")]
		[SerializeField] private LightBufferLights lightBufferLights = LightBufferLights.All;

		[Tooltip("Large Only: a light's bounding-sphere diameter as a fraction of the screen height from which it is shaded in the downsampled buffer.")]
		[SerializeField, Range(LightBufferScaling.MinLargeLightScreenSize, 1f)] private float largeLightScreenSize = LightBufferScaling.DefaultLargeLightScreenSize;

		[Tooltip("How a downsampled light buffer is upscaled. Depth-aware keeps glow from bleeding over nearer geometry (3 texture reads per pixel, 6 at depth edges); bilinear is cheapest (1).")]
		[SerializeField] private LightBufferUpsample lightBufferUpsample = LightBufferUpsample.DepthAware;

		[Tooltip("Downsampled light buffer only: when it is shaded. Before the opaques (from URP's depth prepass) only the composite runs after them, so on tile-based mobile GPUs URP's opaque and transparent passes stay one render pass instead of storing and reloading the camera's color and depth around the buffer (2-7% of the frame on Mali). Auto: before the opaques when the renderer's settings make URP render a depth prepass anyway (Depth Texture Mode 'Force Prepass' with a depth texture, depth priming), else after; never adds a prepass. Auto can't see prepasses other features cause (SSAO, depth normals): with those, choose Before Opaques. Without any prepass, Before Opaques makes URP add one, which draws every opaque object twice and costs about what it saves.")]
		[SerializeField] private LightBufferTiming lightBufferTiming = LightBufferTiming.Auto;

		[Tooltip("Hidden/Fuluma/LightBuffer by default (filled in by the Editor when empty, so builds include it). Only used with a downsampled light buffer.")]
		[SerializeField] private Shader lightBufferShader;

		// GPU culling isn't implemented yet (PreferGPU falls back to the CPU): kept serialized, hidden until it is.
		[SerializeField, HideInInspector] private Shader cullingShader;

		[SerializeField, HideInInspector] private CullingBackend cullingBackend = CullingBackend.PreferGPU;

		private FulumaSpotLightRenderPass _spotLightRenderPass;

		private bool _forceGeneralClipVariant;

		/// <summary>
		/// True while <see cref="OnValidate"/> runs <see cref="Create"/>, where the replaced pass can't be destroyed at once.
		/// </summary>
		[System.NonSerialized] private bool _validating;

		/// <summary>
		/// Whether <see cref="AddRenderPasses"/> already looked for this feature's renderer data again (once per pass: on
		/// URP 14 the lookup scans every loaded object and allocates).
		/// </summary>
		[System.NonSerialized] private bool _rendererDataRetried;

		/// <summary>
		/// The optional rendering features in use. Setting it applies only what changed: <see cref="FidelityFeatures.SceneDepth"/>
		/// and <see cref="FidelityFeatures.SpotLightLods"/> recreate the pass, the others apply immediately.
		/// </summary>
		public FidelityFeatures FidelityFeatures
		{
			get => fidelityFeatures;
			set
			{
				var changed = fidelityFeatures ^ value;
				if (changed == FidelityFeatures.None)
					return;

				fidelityFeatures = value;

				// Both shape the pass; a new pass applies every other feature as well.
				if ((changed & (FidelityFeatures.SceneDepth | FidelityFeatures.SpotLightLods)) != 0)
				{
					Create();
					return;
				}

				if ((changed & FidelityFeatures.ClipBoxes) != 0)
					_spotLightRenderPass?.SetClipBoxes(IsUsing(FidelityFeatures.ClipBoxes), clipBoxPieceLimit);

				if ((changed & FidelityFeatures.NearPlaneFade) != 0)
					_spotLightRenderPass?.SetNearPlaneFade(IsUsing(FidelityFeatures.NearPlaneFade), nearPlaneFadeDistance);

				if ((changed & FidelityFeatures.WideConePrecision) != 0)
					_spotLightRenderPass?.SetWideConePrecision(IsUsing(FidelityFeatures.WideConePrecision));
			}
		}

		/// <summary>
		/// Whether lights are limited by their clip boxes (<see cref="FidelityFeatures.ClipBoxes"/>). Applies immediately.
		/// </summary>
		public bool UseClipBoxes
		{
			get => IsUsing(FidelityFeatures.ClipBoxes);
			set => SetFidelityFeature(FidelityFeatures.ClipBoxes, value);
		}

		/// <summary>
		/// Most pieces a clipped light's ray is integrated in (3 to 5). Applies immediately.
		/// </summary>
		public int ClipBoxPieceLimit
		{
			get => clipBoxPieceLimit;
			set
			{
				clipBoxPieceLimit = math.clamp(value, FulumaSpotLightRenderPass.MinClipBoxPieces, FulumaSpotLightRenderPass.MaxClipBoxPieces);
				_spotLightRenderPass?.SetClipBoxes(IsUsing(FidelityFeatures.ClipBoxes), clipBoxPieceLimit);
			}
		}

		/// <summary>
		/// Whether the glow fades out where the near clipping plane cuts a light's volume
		/// (<see cref="FidelityFeatures.NearPlaneFade"/>). Applies immediately.
		/// </summary>
		public bool UseNearPlaneFade
		{
			get => IsUsing(FidelityFeatures.NearPlaneFade);
			set => SetFidelityFeature(FidelityFeatures.NearPlaneFade, value);
		}

		/// <summary>
		/// Distance past the near clipping plane over which the glow fades in. Applies immediately.
		/// </summary>
		public float NearPlaneFadeDistance
		{
			get => nearPlaneFadeDistance;
			set
			{
				nearPlaneFadeDistance = math.max(0.001f, value);
				_spotLightRenderPass?.SetNearPlaneFade(IsUsing(FidelityFeatures.NearPlaneFade), nearPlaneFadeDistance);
			}
		}

		/// <summary>
		/// Whether the lights use the cancellation-free cone intersection (<see cref="FidelityFeatures.WideConePrecision"/>).
		/// Applies immediately.
		/// </summary>
		public bool UseWideConePrecision
		{
			get => IsUsing(FidelityFeatures.WideConePrecision);
			set => SetFidelityFeature(FidelityFeatures.WideConePrecision, value);
		}

		/// <summary>
		/// Global multiplier on the brightness of every fake light this feature draws, whatever its type; at least 0.
		/// Applies immediately.
		/// </summary>
		public float LightIntensity
		{
			get => lightIntensity;
			set
			{
				lightIntensity = math.max(0f, value);
				_spotLightRenderPass?.SetIntensity(lightIntensity);
			}
		}

		/// <summary>
		/// How the haze scatters light toward the camera (a shader variant per value). Applies immediately.
		/// </summary>
		public ScatteringPhase ScatteringPhase
		{
			get => scatteringPhase;
			set
			{
				scatteringPhase = value;
				ApplyScattering();
			}
		}

		/// <summary>
		/// How strongly the haze scatters onward with <see cref="URP.ScatteringPhase.HenyeyGreenstein"/>: the mean cosine
		/// of the scattering angle, 0 (every direction alike) to 0.95. Above 0.8 the glow toward the lamp gets less
		/// accurate (the worst view ray is off by up to about a third at 0.9). Applies immediately.
		/// </summary>
		public float ScatteringAnisotropy
		{
			get => scatteringAnisotropy;
			set
			{
				scatteringAnisotropy = ClampFinite(value, 0f, MaxScatteringAnisotropy, DefaultScatteringAnisotropy);
				ApplyScattering();
			}
		}

		/// <summary>
		/// How the glow fades with the distance from the lamp (a shader variant per value). Applies immediately.
		/// </summary>
		public DistanceFalloff DistanceFalloff
		{
			get => distanceFalloff;
			set
			{
				distanceFalloff = value;
				ApplyScattering();
			}
		}

		/// <summary>
		/// How bright a light gets at its lamp (a shader variant per value). Applies immediately.
		/// </summary>
		public LampGlare LampGlare
		{
			get => lampGlare;
			set
			{
				lampGlare = value;
				ApplyScattering();
			}
		}

		/// <summary>
		/// Shader the spot lights are drawn with; null means the default (<c>Hidden/Fuluma/Analytical/SpotLight</c>).
		/// Changing it recreates the pass.
		/// </summary>
		public Shader SpotLightShader
		{
			get => spotLightShader;
			set
			{
				if (value == spotLightShader)
					return;

				spotLightShader = value;
				Create();
			}
		}

		/// <summary>
		/// Whether each light gets a proxy LOD by its screen size (<see cref="FidelityFeatures.SpotLightLods"/>); off draws
		/// every light with LOD0 (and culls faster). Changing it recreates the pass.
		/// </summary>
		public bool UseSpotLightLods
		{
			get => IsUsing(FidelityFeatures.SpotLightLods);
			set => SetFidelityFeature(FidelityFeatures.SpotLightLods, value);
		}

		/// <summary>
		/// Resolution fake lights are shaded at, as a fraction of the camera's width and height, clamped to
		/// [<see cref="LightBufferScaling.Min"/>, 1]. 1 draws straight onto the camera. Changing it between two
		/// downsampled scales takes effect from the next camera; crossing full resolution recreates the pass.
		/// </summary>
		public float LightBufferScale
		{
			get => lightBufferScale;
			set
			{
				value = LightBufferScaling.Clamp(value);
				if (value == lightBufferScale)
					return;

				var wasDownsampled = LightBufferScaling.IsDownsampled(lightBufferScale);
				lightBufferScale = value;

				if (wasDownsampled && LightBufferScaling.IsDownsampled(value) && _spotLightRenderPass != null)
					_spotLightRenderPass.LightBufferScale = value;
				else
					Create();
			}
		}

		/// <summary>
		/// Whether the lights read the scene's depth (<see cref="FidelityFeatures.SceneDepth"/>; see
		/// <see cref="FulumaSpotLightRenderPass.UsesSceneDepth"/>). Changing it recreates the pass.
		/// </summary>
		public bool UseSceneDepth
		{
			get => IsUsing(FidelityFeatures.SceneDepth);
			set => SetFidelityFeature(FidelityFeatures.SceneDepth, value);
		}

		/// <summary>
		/// When a downsampled light buffer is shaded, relative to URP's opaques (see
		/// <see cref="FulumaSpotLightRenderPass.LightBufferTiming"/>). Applies immediately.
		/// </summary>
		public LightBufferTiming LightBufferTiming
		{
			get => lightBufferTiming;
			set
			{
				lightBufferTiming = value;
				if (_spotLightRenderPass != null)
					_spotLightRenderPass.LightBufferTiming = value;
			}
		}

		/// <summary>
		/// How a downsampled light buffer is upscaled. Changing it recreates the pass.
		/// </summary>
		public LightBufferUpsample LightBufferUpsample
		{
			get => lightBufferUpsample;
			set
			{
				if (value == lightBufferUpsample)
					return;

				lightBufferUpsample = value;
				Create();
			}
		}

		/// <summary>
		/// Which lights a downsampled light buffer shades. Changing it recreates the pass.
		/// </summary>
		public LightBufferLights LightBufferLights
		{
			get => lightBufferLights;
			set
			{
				if (value == lightBufferLights)
					return;

				lightBufferLights = value;
				Create();
			}
		}

		/// <summary>
		/// With <see cref="Rendering.URP.LightBufferLights.LargeOnly"/>: the screen size (bounding-sphere diameter over
		/// screen height) from which a light is shaded in the downsampled buffer. Changing it recreates the pass.
		/// </summary>
		public float LargeLightScreenSize
		{
			get => largeLightScreenSize;
			set
			{
				value = math.clamp(value, LightBufferScaling.MinLargeLightScreenSize, 1f);
				if (value == largeLightScreenSize)
					return;

				largeLightScreenSize = value;
				Create();
			}
		}

		/// <summary>
		/// Proxy LODs of the spot lights; null means the built-in set. Changing it recreates the pass.
		/// </summary>
		public SpotLightProxyArrayDescriptor SpotLightDescriptor
		{
			get => spotLightDescriptor;
			set
			{
				if (value == spotLightDescriptor)
					return;

				spotLightDescriptor = value;
				Create();
			}
		}

		/// <summary>
		/// Cameras draw this feature only if their culling mask overlaps these layers.
		/// </summary>
		public LayerMask CameraLayers
		{
			get => cameraLayers;
			set => cameraLayers = value;
		}

		/// <summary>
		/// Most fake spot lights drawn per camera, over both pools.
		/// </summary>
		public int MaxSpotLights
		{
			get => maxSpotLights;
			set
			{
				maxSpotLights = math.max(1, value);

				if (_spotLightRenderPass != null)
					_spotLightRenderPass.MaxVisibleLights = maxSpotLights;
			}
		}

		/// <summary>
		/// When in URP's frame the lights are drawn. Applies from the next frame.
		/// </summary>
		public RenderPassEvent RenderPassEvent
		{
			get => @event;
			set
			{
				@event = value;

				if (_spotLightRenderPass != null)
					_spotLightRenderPass.renderPassEvent = value;
			}
		}

		/// <summary>
		/// The store this feature draws; null means <see cref="LightStore.Default"/>. Unlike <see cref="Store"/>, reading
		/// it never creates the default store. Changing it recreates the pass.
		/// </summary>
		public LightStore OverrideStore
		{
			get => overrideStore;
			set
			{
				if (value == overrideStore)
					return;

				overrideStore = value;
				Create();
			}
		}

		/// <summary>
		/// Initial capacity, in spot lights, of the store's GPU buffers, used when they are first created (they grow when
		/// needed). Set in the Inspector.
		/// </summary>
		public int SpotLightBufferSize => spotLightBufferSize;

		/// <summary>
		/// Initial capacity, in clip boxes, of the store's GPU box buffers, used when they are first created (they grow
		/// when needed). Set in the Inspector.
		/// </summary>
		public int ClipBoxBufferSize => clipBoxBufferSize;

		/// <summary>
		/// GPU buffers per-frame data (dynamic lights' transforms, dynamic clip boxes) rotates through, used when the
		/// store's GPU buffers are first created. Set in the Inspector.
		/// </summary>
		public int DynamicBufferCount => dynamicBufferCount;

		/// <summary>
		/// Shader of the downsampled light buffer; null means the default (<c>Hidden/Fuluma/LightBuffer</c>). Changing it
		/// recreates the pass.
		/// </summary>
		public Shader LightBufferShader
		{
			get => lightBufferShader;
			set
			{
				if (value == lightBufferShader)
					return;

				lightBufferShader = value;
				Create();
			}
		}

		/// <summary>
		/// The store this feature draws: the override store, or <see cref="LightStore.Default"/> when none is set.
		/// </summary>
		public LightStore Store => overrideStore == null ? LightStore.Default : overrideStore;

		/// <summary>
		/// Where the lights are culled. Only CPU culling exists so far, so every value culls on the CPU. Changing it
		/// recreates the pass.
		/// </summary>
		internal CullingBackend Culling
		{
			get => cullingBackend;
			set
			{
				if (value == cullingBackend)
					return;
				
				cullingBackend = value;
				Create();
			}
		}

		/// <summary>
		/// Draws every clipped light with the general clip box variant (<see cref="FulumaSpotLightRenderPass.ForceGeneralClipVariant"/>),
		/// for tests; kept across pass rebuilds.
		/// </summary>
		internal bool ForceGeneralClipVariant
		{
			get => _forceGeneralClipVariant;
			set
			{
				_forceGeneralClipVariant = value;
				if (_spotLightRenderPass != null)
					_spotLightRenderPass.ForceGeneralClipVariant = value;
			}
		}

		internal FulumaSpotLightRenderPass SpotLightRenderPass => _spotLightRenderPass;

		/// <summary>
		/// (Re)creates the spot light pass from the current settings. URP calls it when the renderer is built and when
		/// the feature changes in the inspector; in the Editor it also fills empty shader fields with the default shaders.
		/// No pass is created when the spot light shader can't be found.
		/// </summary>
		public override void Create()
		{
#if UNITY_EDITOR
			AssignDefaultShaders();
#endif
			SanitizeSettings();

			// The old pass is disposed only after the new one exists: both hold the store's GPU mirror, which then carries
			// over instead of being freed and fully re-uploaded. (URP's own renderer rebuilds dispose the feature first;
			// the mirror keeps itself alive through such a frame, see SpotLightGpuMirror.Release.)
			var previous = _spotLightRenderPass;
			_spotLightRenderPass = null;

			try
			{
				CreatePass();
			}
			finally
			{
				DisposeReplacedPass(previous);
			}
		}

		/// <summary>
		/// Starts culling the lights for a camera that draws them, so the culling jobs overlap URP's own culling and the
		/// render graph recording.
		/// </summary>
		public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
		{
			// Start culling before URP's own culling and the graph recording, so it overlaps them.
			ref readonly var camera = ref cameraData.camera;
			
			if (_spotLightRenderPass == null || DrawsFor(camera) == false)
				return;
			
			_spotLightRenderPass.BeginCulling(camera, cameraData.GetViewMatrix(), cameraData.GetProjectionMatrix(),
				cameraData.worldSpaceCameraPos);
		}

		/// <summary>
		/// Enqueues the spot light pass (and, with <see cref="LightBufferTiming"/>, its early light buffer pass) for
		/// cameras whose culling mask overlaps <see cref="CameraLayers"/>; preview cameras are skipped.
		/// </summary>
		public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
		{
			if (_spotLightRenderPass == null || _spotLightRenderPass.IsCreated == false
			    || DrawsFor(renderingData.cameraData.camera) == false)
				return;

			// Before URP sets up its own passes: whether this camera needs URP's depth copy depends on its MSAA.
			_spotLightRenderPass.ConfigureDepthSource(renderingData.cameraData.cameraTargetDescriptor.msaaSamples);
			renderer.EnqueuePass(_spotLightRenderPass);

			if (_spotLightRenderPass.LightBufferBeforeOpaquesPass != null)
			{
				// Auto reads the renderer's settings; find them if this feature was created before its renderer was listed.
				if (_spotLightRenderPass.RendererData == null && _rendererDataRetried == false)
				{
					_rendererDataRetried = true;
					_spotLightRenderPass.RendererData = FindRendererData();
				}
				renderer.EnqueuePass(_spotLightRenderPass.LightBufferBeforeOpaquesPass);
			}
		}

		/// <summary>
		/// Disposes the spot light pass, which releases its materials, proxy mesh and its hold on the store's GPU buffers.
		/// </summary>
		protected override void Dispose(bool disposing)
		{
			base.Dispose(disposing);

			if (_spotLightRenderPass != null)
			{
				_spotLightRenderPass.Dispose();
				_spotLightRenderPass = null;
			}
		}

		/// <summary>
		/// Whether <paramref name="feature"/> is on: a bit test (<see cref="System.Enum.HasFlag"/> would box).
		/// </summary>
		private bool IsUsing(FidelityFeatures feature) => (fidelityFeatures & feature) != 0;

		private void SetFidelityFeature(FidelityFeatures feature, bool enabled)
			=> FidelityFeatures = enabled ? fidelityFeatures | feature : fidelityFeatures & ~feature;

		private void ApplyScattering()
			=> _spotLightRenderPass?.SetScattering(scatteringPhase, scatteringAnisotropy, distanceFalloff, lampGlare);

		private void CreatePass()
		{
			// A missing shader still gets a pass, which logs an error and draws nothing.
			var shader = spotLightShader != null
				? spotLightShader
				: Shader.Find(DefaultSpotLightShaderName);

			_spotLightRenderPass = new FulumaSpotLightRenderPass(Store, shader, spotLightBufferSize, spotLightDescriptor,
				IsUsing(FidelityFeatures.SpotLightLods), lightBufferScale, lightBufferShader, lightBufferUpsample, lightBufferLights,
				largeLightScreenSize, IsUsing(FidelityFeatures.SceneDepth), clipBoxBufferSize, dynamicBufferCount)
			{
				renderPassEvent = @event
			};

			_spotLightRenderPass.SetIntensity(lightIntensity);
			_spotLightRenderPass.SetNearPlaneFade(IsUsing(FidelityFeatures.NearPlaneFade), nearPlaneFadeDistance);
			_spotLightRenderPass.SetWideConePrecision(IsUsing(FidelityFeatures.WideConePrecision));
			ApplyScattering();
			_spotLightRenderPass.SetClipBoxes(IsUsing(FidelityFeatures.ClipBoxes), clipBoxPieceLimit);
			_spotLightRenderPass.ForceGeneralClipVariant = _forceGeneralClipVariant;
			_spotLightRenderPass.MaxVisibleLights = maxSpotLights;
			_spotLightRenderPass.LightBufferTiming = lightBufferTiming;
			_spotLightRenderPass.RendererData = FindRendererData();
			_rendererDataRetried = false;
		}

#if UNITY_EDITOR
		/// <summary>
		/// A player only contains shaders something references, and <c>Shader.Find</c> can't find a stripped one (a
		/// downsampled light buffer then silently fell back to full resolution in builds). So empty shader fields get the
		/// default shaders in the Editor, which the renderer asset then references. <c>Create</c> runs on load and on every
		/// inspector change.
		/// </summary>
		private void AssignDefaultShaders()
		{
			var changed = false;

			if (spotLightShader == null)
			{
				spotLightShader = Shader.Find(DefaultSpotLightShaderName);
				changed |= spotLightShader != null;
			}

			if (lightBufferShader == null)
			{
				lightBufferShader = Shader.Find(DownsampledLightBuffer.ShaderName);
				changed |= lightBufferShader != null;
			}

			if (changed)
				UnityEditor.EditorUtility.SetDirty(this);
		}
#endif
		
		/// <summary>
		/// Disposes a pass <see cref="Create"/> replaced. Inside <see cref="OnValidate"/> (every Inspector edit) the pass's
		/// proxy mesh and materials can't be destroyed, so there the disposal waits for the next Editor update.
		/// </summary>
		private void DisposeReplacedPass(FulumaSpotLightRenderPass pass)
		{
			if (pass == null)
				return;

#if UNITY_EDITOR
			if (_validating)
			{
				UnityEditor.EditorApplication.delayCall += pass.Dispose;
				return;
			}
#endif
			pass.Dispose();
		}

		/// <summary>
		/// Brings serialized settings back into their valid ranges, for values the Inspector's attributes don't prevent
		/// (edited files, scripts, older versions): non-finite numbers return to their defaults, unknown enum values and
		/// fidelity bits are dropped.
		/// </summary>
		private void SanitizeSettings()
		{
			@event = (RenderPassEvent)math.clamp((int)@event, (int)RenderPassEvent.BeforeRendering, (int)RenderPassEvent.AfterRendering);
			lightIntensity = ClampFinite(lightIntensity, 0f, float.MaxValue, DefaultLightIntensity);
			scatteringAnisotropy = ClampFinite(scatteringAnisotropy, 0f, MaxScatteringAnisotropy, DefaultScatteringAnisotropy);
			fidelityFeatures &= AllFidelityFeatures;

			if ((uint)scatteringPhase > (uint)ScatteringPhase.HenyeyGreenstein)
				scatteringPhase = ScatteringPhase.Isotropic;

			if ((uint)distanceFalloff > (uint)DistanceFalloff.Unity)
				distanceFalloff = DistanceFalloff.RangeScaled;

			if ((uint)lampGlare > (uint)LampGlare.Point)
				lampGlare = LampGlare.None;
			nearPlaneFadeDistance = ClampFinite(nearPlaneFadeDistance, MinNearPlaneFadeDistance, float.MaxValue, DefaultNearPlaneFadeDistance);
			dynamicBufferCount = math.clamp(dynamicBufferCount, SpotLightGpuMirror.MinimumRingLength, SpotLightGpuMirror.MaximumRingLength);
			maxSpotLights = math.max(1, maxSpotLights);
			spotLightBufferSize = math.max(1, spotLightBufferSize);
			clipBoxPieceLimit = math.clamp(clipBoxPieceLimit, FulumaSpotLightRenderPass.MinClipBoxPieces, FulumaSpotLightRenderPass.MaxClipBoxPieces);
			clipBoxBufferSize = math.max(1, clipBoxBufferSize);
			lightBufferScale = LightBufferScaling.Clamp(lightBufferScale);
			largeLightScreenSize = ClampFinite(largeLightScreenSize, LightBufferScaling.MinLargeLightScreenSize, 1f,
				LightBufferScaling.DefaultLargeLightScreenSize);

			if ((uint)lightBufferLights > (uint)LightBufferLights.LargeOnly)
				lightBufferLights = LightBufferLights.All;

			if ((uint)lightBufferUpsample > (uint)LightBufferUpsample.Bilinear)
				lightBufferUpsample = LightBufferUpsample.DepthAware;

			if ((uint)lightBufferTiming > (uint)LightBufferTiming.BeforeOpaques)
				lightBufferTiming = LightBufferTiming.Auto;
		}

		/// <summary>
		/// The URP renderer data this feature belongs to, among every configured pipeline asset (null if none lists it).
		/// </summary>
		private UniversalRendererData FindRendererData()
		{
#if UNITY_6000_0_OR_NEWER
			foreach (var pipeline in GraphicsSettings.allConfiguredRenderPipelines)
			{
				if (pipeline is not UniversalRenderPipelineAsset asset)
					continue;

				foreach (var rendererData in asset.rendererDataList)
					if (rendererData is UniversalRendererData universal && universal.rendererFeatures.Contains(this))
						return universal;
			}
#else
			// URP 14 has no public renderer list. A feature is a sub-asset of its renderer data, which is loaded with it.
			foreach (var universal in Resources.FindObjectsOfTypeAll<UniversalRendererData>())
				if (universal.rendererFeatures.Contains(this))
					return universal;
#endif

			return null;
		}

		private bool DrawsFor(Camera camera)
			=> camera != null && camera.cameraType != CameraType.Preview && (camera.cullingMask & cameraLayers) != 0;

#if UNITY_EDITOR
		/// <summary>
		/// Replaces <see cref="ScriptableRendererFeature"/>'s own <c>OnValidate</c> (same condition: a URP pipeline exists),
		/// marking that <see cref="Create"/> runs from it.
		/// </summary>
		private void OnValidate()
		{
			if (RenderPipelineManager.currentPipeline is not UniversalRenderPipeline)
				return;

			_validating = true;
			try
			{
				Create();
			}
			finally
			{
				_validating = false;
			}
		}
#endif
	}
}