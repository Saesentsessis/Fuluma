using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Data;
using Saesentsessis.Fuluma.Rendering.Buffers;
using Saesentsessis.Fuluma.Rendering.Proxy;
using Saesentsessis.Fuluma.Rendering.URP;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Saesentsessis.Fuluma.Tests.Editor.Utils
{
	/// <summary>
	/// A camera rendering through the project's URP asset (whose renderer contains FulumaRenderFeature, drawing
	/// the feature's store: LightStore.Default or its override store) into a small HDR texture.
	/// </summary>
	internal sealed class RenderScene : IDisposable
	{
		public static SpotLightData DefaultTestLight()
		{
			var data = SpotLightData.Default;
			data.Range = 8f;
			data.InnerAngleDegrees = 40f;
			data.OuterAngleDegrees = 60f;
			return data;
		}

		private static RenderTexture CreateTarget(int size)
		{
			var target = new RenderTexture(size, size, GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormat.D32_SFloat_S8_UInt)
			{
				hideFlags = HideFlags.HideAndDontSave
			};

			target.Create();
			return target;
		}

		private static FulumaRenderFeature FindFeature(UniversalRenderPipelineAsset asset, out UniversalRendererData universalData)
		{
			universalData = null;

			// rendererDataList is internal API surface; read the default renderer's features through serialization.
			var serialized = new SerializedObject(asset);
			var list = serialized.FindProperty("m_RendererDataList");
			if (list == null || list.arraySize == 0)
				return null;

			var rendererData = list.GetArrayElementAtIndex(0).objectReferenceValue as ScriptableRendererData;
			universalData = rendererData as UniversalRendererData;
			return rendererData == null ? null : rendererData.rendererFeatures.OfType<FulumaRenderFeature>().FirstOrDefault(f => f.isActive);
		}

		private readonly int _size;

		private readonly List<Object> _objects = new();
		private readonly List<(Vector3 position, Quaternion rotation, SpotLightData data)> _lightPlacements = new();
		private readonly List<LightHandle> _lights = new();
		private readonly List<ClipBoxReference[]> _lightBoxes = new();

		/// <summary>
		/// Whether the lights go into the dynamic pool (each following a GameObject's transform) instead of the static one.
		/// </summary>
		private bool _dynamicLights;
		private readonly List<GameObject> _lightObjects = new();
		private readonly List<ClipBoxHandle> _boxes = new();
		private bool _previousUseClipBoxes;
		private int _previousClipBoxPieceLimit;
		private readonly Camera _camera;
		private readonly RenderTexture _target;
		private readonly UniversalRenderPipelineAsset _asset;
		private readonly int _previousMsaa;

		private bool _descriptorReplaced;
		private bool _scaleReplaced;
		private float _previousScale;
		private bool _nearFadeReplaced;
		private bool _previousNearFade;
		private float _previousNearFadeDistance;
		private bool _wideConesReplaced;
		private bool _previousWideCones;
		private bool _lightsReplaced;
		private LightBufferLights _previousLights;
		private float _previousLargeScreenSize;
		private bool _upsampleReplaced;
		private float _previousIntensity;
		private ScatteringPhase _previousPhase;
		private float _previousAnisotropy;
		private DistanceFalloff _previousFalloff;
		private LampGlare _previousGlare;
		private LightBufferUpsample _previousUpsample;
		private SpotLightProxyArrayDescriptor _previousDescriptor;
		private LightBufferTiming _previousTiming;
		private bool _previousUseSceneDepth;
		private UniversalRendererData _rendererData;
		private bool _depthSettingsReplaced;
		private bool _previousDepthTexture;
		private CopyDepthMode _previousCopyDepthMode;
		private DepthPrimingMode _previousDepthPrimingMode;

		public RenderScene(int msaaSamples = 1, int size = 64)
		{
			_size = size;

			// Every render scene draws fake lights: without a capable graphics device the test can't run.
			GpuRequirement.IgnoreUnlessFakeLightsDraw();

			_asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
			if (_asset == null)
				Assert.Ignore("The project's render pipeline isn't URP.");

			Feature = FindFeature(_asset, out _rendererData);
			if (Feature == null)
				Assert.Ignore("The active URP renderer has no FulumaRenderFeature.");

			_previousMsaa = _asset.msaaSampleCount;
			_asset.msaaSampleCount = msaaSamples;

			_target = CreateTarget(_size);
			_camera = CreateCamera(_target);

			// Tests start from full resolution and unit intensity whatever the project's renderer asset says (restored on
			// Dispose).
			if (Feature.LightBufferScale != LightBufferScaling.Full)
				UseLightBufferScale(LightBufferScaling.Full);

			if (Feature.LightBufferLights != LightBufferLights.All)
				UseLightBufferLights(LightBufferLights.All);

			// The brute-force references have no near-plane fade.
			UseNearPlaneFade(false, Feature.NearPlaneFadeDistance);

			// The default cone intersection, whatever the renderer asset says (tests opt in to the wide-cone variant).
			UseWideConePrecision(false);

			_previousIntensity = Feature.LightIntensity;
			Feature.LightIntensity = 1f;

			// Today's light model unless a test picks another (the brute-force references default to it).
			_previousPhase = Feature.ScatteringPhase;
			_previousAnisotropy = Feature.ScatteringAnisotropy;
			_previousFalloff = Feature.DistanceFalloff;
			_previousGlare = Feature.LampGlare;
			UseScattering(ScatteringPhase.Isotropic, _previousAnisotropy, DistanceFalloff.RangeScaled, LampGlare.None);

			// After the opaques: what the references were made with, whatever the renderer's prepass settings.
			_previousUseClipBoxes = Feature.UseClipBoxes;
			_previousClipBoxPieceLimit = Feature.ClipBoxPieceLimit;
			Feature.UseClipBoxes = true;
			Feature.ClipBoxPieceLimit = FulumaSpotLightRenderPass.MinClipBoxPieces;

			_previousTiming = Feature.LightBufferTiming;
			Feature.LightBufferTiming = LightBufferTiming.AfterOpaques;

			_previousUseSceneDepth = Feature.UseSceneDepth;
			Feature.UseSceneDepth = true;
		}

		public FulumaRenderFeature Feature { get; }

		/// <summary>
		/// Whether the project's URP renderer uses a deferred path (Deferred or Deferred+).
		/// </summary>
		public bool RendersDeferred => _rendererData != null
#if UNITY_6000_1_OR_NEWER
		                               && _rendererData.renderingMode is RenderingMode.Deferred or RenderingMode.DeferredPlus;
#else
		                               // Deferred+ exists from Unity 6.1.
		                               && _rendererData.renderingMode == RenderingMode.Deferred;
#endif

		/// <summary>
		/// Whether the lights read the scene's depth, until <see cref="Dispose"/> (recreates the feature's pass).
		/// </summary>
		public void UseSceneDepth(bool enabled) => Feature.UseSceneDepth = enabled;

		/// <summary>
		/// Shades a downsampled light buffer at this point of the frame until <see cref="Dispose"/>.
		/// </summary>
		public void UseLightBufferTiming(LightBufferTiming timing) => Feature.LightBufferTiming = timing;

		/// <summary>
		/// Sets the URP asset's depth texture and the renderer's depth texture mode and depth priming until
		/// <see cref="Dispose"/> (what decides whether URP renders a depth prepass).
		/// </summary>
		public void UseDepthPrepassSettings(bool depthTexture, CopyDepthMode copyDepthMode, DepthPrimingMode depthPrimingMode)
		{
			if (_rendererData == null)
				Assert.Ignore("The renderer isn't a UniversalRendererData.");

			if (_depthSettingsReplaced == false)
			{
				_previousDepthTexture = _asset.supportsCameraDepthTexture;
				_previousCopyDepthMode = _rendererData.copyDepthMode;
				_previousDepthPrimingMode = _rendererData.depthPrimingMode;
				_depthSettingsReplaced = true;
			}

			_asset.supportsCameraDepthTexture = depthTexture;
			_rendererData.copyDepthMode = copyDepthMode;
			_rendererData.depthPrimingMode = depthPrimingMode;
		}

		/// <summary>
		/// Lights only exist in the store during <see cref="RenderLightContribution"/>.
		/// </summary>
		public void AddLight(Vector3 position, Quaternion rotation) => AddLight(position, rotation, DefaultTestLight());

		public void AddLight(Vector3 position, Quaternion rotation, SpotLightData data)
		{
			_lightPlacements.Add((position, rotation, data));
			_lightBoxes.Add(null);
		}

		/// <summary>
		/// A light limited by clip boxes (added with <see cref="AddClipBox"/>).
		/// </summary>
		public void AddLight(Vector3 position, Quaternion rotation, SpotLightData data, params ClipBoxReference[] boxes)
		{
			_lightPlacements.Add((position, rotation, data));
			_lightBoxes.Add(boxes);
		}

		/// <summary>
		/// A static clip box in the renderer's store until <see cref="Dispose"/>.
		/// </summary>
		public ClipBoxHandle AddClipBox(Matrix4x4 placement, ClipBoxData box)
		{
			var handle = Feature.Store.AddClipBox(placement, box);
			_boxes.Add(handle);
			return handle;
		}

		/// <summary>
		/// A box that follows a new GameObject's transform (re-packed every frame), placed by <paramref name="placement"/>.
		/// </summary>
		public ClipBoxHandle AddDynamicClipBox(Matrix4x4 placement, ClipBoxData box)
		{
			var go = new GameObject("Fuluma Test Clip Box") { hideFlags = HideFlags.HideAndDontSave };
			_objects.Add(go);
			go.transform.SetPositionAndRotation(placement.GetPosition(), placement.rotation);
			go.transform.localScale = placement.lossyScale;

			var handle = Feature.Store.AddClipBox(go.transform, box);
			_boxes.Add(handle);
			return handle;
		}

		/// <summary>
		/// Clip box settings until <see cref="Dispose"/>.
		/// </summary>
		public void UseClipBoxes(bool enabled, int pieceLimit = FulumaSpotLightRenderPass.MinClipBoxPieces)
		{
			Feature.UseClipBoxes = enabled;
			Feature.ClipBoxPieceLimit = pieceLimit;
		}

		/// <summary>
		/// World-space view ray through the center of pixel (x, y), bottom-left origin like the readback.
		/// </summary>
		/// <remarks>Starts at the camera, like the shader's ray. ViewportPointToRay starts on the near plane.</remarks>
		public Ray PixelRay(int x, int y)
		{
			var nearPlaneRay = _camera.ViewportPointToRay(new Vector3((x + 0.5f) / _size, (y + 0.5f) / _size, 0f));

			// Orthographic: parallel rays starting on the camera plane, like the shader's.
			return _camera.orthographic
				? new Ray(nearPlaneRay.origin - nearPlaneRay.direction * _camera.nearClipPlane, nearPlaneRay.direction)
				: new Ray(_camera.transform.position, nearPlaneRay.direction);
		}

		public void SetCamera(Vector3 position, Vector3 target, float fieldOfView = 60f)
		{
			_camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(target - position));
			_camera.fieldOfView = fieldOfView;
			_camera.orthographic = false;
		}

		public void SetOrthographicCamera(Vector3 position, Vector3 target, float orthographicSize)
		{
			_camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(target - position));
			_camera.orthographic = true;
			_camera.orthographicSize = orthographicSize;
		}

		/// <summary>
		/// Near-plane fade settings until <see cref="Dispose"/> (off for every test unless set).
		/// </summary>
		public void UseNearPlaneFade(bool enabled, float distance)
		{
			if (_nearFadeReplaced == false)
			{
				_previousNearFade = Feature.UseNearPlaneFade;
				_previousNearFadeDistance = Feature.NearPlaneFadeDistance;
			}

			_nearFadeReplaced = true;
			Feature.UseNearPlaneFade = enabled;
			Feature.NearPlaneFadeDistance = distance;
		}

		/// <summary>
		/// The light model's variants until <see cref="Dispose"/> (isotropic, range-scaled, no glare for every test unless
		/// set).
		/// </summary>
		public void UseScattering(ScatteringPhase phase, float anisotropy, DistanceFalloff falloff, LampGlare glare)
		{
			Feature.ScatteringPhase = phase;
			Feature.ScatteringAnisotropy = anisotropy;
			Feature.DistanceFalloff = falloff;
			Feature.LampGlare = glare;
		}

		/// <summary>
		/// The cancellation-free (wide-cone) intersection variant until <see cref="Dispose"/> (off for every test unless set).
		/// </summary>
		public void UseWideConePrecision(bool enabled)
		{
			if (_wideConesReplaced == false)
				_previousWideCones = Feature.UseWideConePrecision;

			_wideConesReplaced = true;
			Feature.UseWideConePrecision = enabled;
		}

		/// <summary>
		/// Shades these lights in a downsampled light buffer until <see cref="Dispose"/> (recreates the feature's pass).
		/// </summary>
		public void UseLightBufferLights(LightBufferLights lights, float largeScreenSize = LightBufferScaling.DefaultLargeLightScreenSize)
		{
			if (_lightsReplaced == false)
			{
				_previousLights = Feature.LightBufferLights;
				_previousLargeScreenSize = Feature.LargeLightScreenSize;
			}

			_lightsReplaced = true;
			Feature.LightBufferLights = lights;
			Feature.LargeLightScreenSize = largeScreenSize;
		}

		/// <summary>
		/// Upscales a downsampled light buffer this way until <see cref="Dispose"/> (recreates the feature's pass).
		/// </summary>
		public void UseLightBufferUpsample(LightBufferUpsample upsample)
		{
			if (_upsampleReplaced == false)
				_previousUpsample = Feature.LightBufferUpsample;

			_upsampleReplaced = true;
			Feature.LightBufferUpsample = upsample;
		}

		/// <summary>
		/// Shades the lights at this fraction of the resolution until <see cref="Dispose"/> (recreates the feature's pass
		/// when crossing full resolution).
		/// </summary>
		public void UseLightBufferScale(float scale)
		{
			if (_scaleReplaced == false)
				_previousScale = Feature.LightBufferScale;

			_scaleReplaced = true;
			Feature.LightBufferScale = scale;
		}

		public void AddOccluder(Vector3 position, Vector3 scale)
		{
			var cube = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
			cube.transform.position = position;
			cube.transform.localScale = scale;
		}

		/// <summary>
		/// Draws with <paramref name="descriptor"/>'s proxy LODs until <see cref="Dispose"/> (recreates the feature's pass).
		/// </summary>
		public void UseProxyDescriptor(SpotLightProxyArrayDescriptor descriptor)
		{
			var previous = SetFeatureDescriptor(descriptor);
			if (_descriptorReplaced == false)
				_previousDescriptor = previous;

			_descriptorReplaced = true;
		}

		/// <summary>
		/// Renders with and without the lights; the difference is the fake lights' contribution alone.
		/// </summary>
		/// <param name="afterLitRender">Runs right after the render with the lights (the pass's culling state is that
		/// render's then).</param>
		public LightContribution RenderLightContribution(Action afterLitRender = null)
		{
			AddLightsToStore();
			var withLights = Render();
			afterLitRender?.Invoke();
			RemoveLightsFromStore();
			var withoutLights = Render();

			var difference = new Color[withLights.Length];
			for (var i = 0; i < difference.Length; i++)
				difference[i] = withLights[i] - withoutLights[i];

			return new LightContribution(difference, _size);
		}

		/// <summary>
		/// Milliseconds per frame for <paramref name="frames"/> back-to-back renders with the current lights, including
		/// the wait for the GPU (a 1-pixel readback at the end). Lights are added for the measurement and removed after.
		/// </summary>
		public double TimeFrames(int frames, bool withLights)
		{
			if (withLights)
				AddLightsToStore();

			try
			{
				_camera.Render(); // warm-up: uploads, pipeline state, shader variants
				SyncGpu();

				var stopwatch = Stopwatch.StartNew();
				for (var i = 0; i < frames; i++)
					_camera.Render();
				SyncGpu();
				stopwatch.Stop();

				return stopwatch.Elapsed.TotalMilliseconds / frames;
			}
			finally
			{
				RemoveLightsFromStore();
			}
		}

		public void Dispose()
		{
			RemoveLightsFromStore();

			for (var i = _objects.Count - 1; i >= 0; i--)
				if (_objects[i] != null)
					Object.DestroyImmediate(_objects[i]);

			if (_target != null)
			{
				_target.Release();
				Object.DestroyImmediate(_target);
			}

			_asset.msaaSampleCount = _previousMsaa;

			if (_descriptorReplaced)
				SetFeatureDescriptor(_previousDescriptor);

			if (_scaleReplaced)
				Feature.LightBufferScale = _previousScale;

			if (_upsampleReplaced)
				Feature.LightBufferUpsample = _previousUpsample;

			if (_nearFadeReplaced)
			{
				Feature.UseNearPlaneFade = _previousNearFade;
				Feature.NearPlaneFadeDistance = _previousNearFadeDistance;
			}

			if (_wideConesReplaced)
				Feature.UseWideConePrecision = _previousWideCones;

			if (_lightsReplaced)
			{
				Feature.LightBufferLights = _previousLights;
				Feature.LargeLightScreenSize = _previousLargeScreenSize;
			}

			Feature.LightIntensity = _previousIntensity;
			UseScattering(_previousPhase, _previousAnisotropy, _previousFalloff, _previousGlare);
			Feature.LightBufferTiming = _previousTiming;
			Feature.UseClipBoxes = _previousUseClipBoxes;
			Feature.ClipBoxPieceLimit = _previousClipBoxPieceLimit;
			Feature.ForceGeneralClipVariant = false;

			foreach (var box in _boxes)
				Feature.Store.Remove(box);

			_boxes.Clear();
			Feature.UseSceneDepth = _previousUseSceneDepth;

			if (_depthSettingsReplaced)
			{
				_asset.supportsCameraDepthTexture = _previousDepthTexture;
				_rendererData.copyDepthMode = _previousCopyDepthMode;
				_rendererData.depthPrimingMode = _previousDepthPrimingMode;
			}
		}

		/// <summary>
		/// Puts the scene's lights into the dynamic pool (each following its own GameObject) instead of the static one.
		/// </summary>
		public void UseDynamicLights(bool dynamic) => _dynamicLights = dynamic;

		/// <returns>The descriptor it replaced.</returns>
		private SpotLightProxyArrayDescriptor SetFeatureDescriptor(SpotLightProxyArrayDescriptor descriptor)
		{
			var previous = Feature.SpotLightDescriptor;
			Feature.SpotLightDescriptor = descriptor;
			return previous;
		}

		private Camera CreateCamera(RenderTexture target)
		{
			var cameraObject = Track(new GameObject("Fuluma Test Camera"));

			var camera = cameraObject.AddComponent<Camera>();
			camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
			camera.fieldOfView = 60f;
			camera.nearClipPlane = 0.1f;
			camera.farClipPlane = 100f;
			camera.clearFlags = CameraClearFlags.SolidColor;
			camera.backgroundColor = Color.black;
			camera.allowHDR = true;
			camera.targetTexture = target;

			var cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
			cameraData.renderPostProcessing = false;
			cameraData.antialiasing = AntialiasingMode.None;

			return camera;
		}

		private void AddLightsToStore()
		{
			for (var i = 0; i < _lightPlacements.Count; i++)
			{
				var (position, rotation, data) = _lightPlacements[i];
				LightHandle handle;
				if (_dynamicLights)
				{
					var go = new GameObject("Fuluma Test Light") { hideFlags = HideFlags.HideAndDontSave };
					go.transform.SetPositionAndRotation(position, rotation);
					_lightObjects.Add(go);
					handle = Feature.Store.AddSpotLight(go.transform, data);
				}
				else
				{
					handle = Feature.Store.AddSpotLight(new Placement { Position = position, Rotation = rotation }, data);
				}
				_lights.Add(handle);

				if (_lightBoxes[i] != null)
					Assert.IsTrue(Feature.Store.SetClipBoxes(handle, _lightBoxes[i]), "Clip boxes of a test light.");
			}
		}

		private void RemoveLightsFromStore()
		{
			foreach (var handle in _lights)
				Feature.Store.Remove(handle);

			_lights.Clear();

			foreach (var go in _lightObjects)
				Object.DestroyImmediate(go);

			_lightObjects.Clear();
		}

		/// <summary>
		/// Waits for the GPU with a 1-pixel readback.
		/// </summary>
		private void SyncGpu() => ReadPixels(1);

		private Color[] Render()
		{
			// Each render here is a frame as far as the lights are concerned (the test runner stays in one Time.frameCount).
			SpotLightGpuMirror.ForgetFrameGuardsForTests();
			_camera.Render();
			return ReadPixels(_size);
		}

		/// <summary>
		/// The bottom-left <paramref name="size"/> × <paramref name="size"/> pixels of the target.
		/// </summary>
		private Color[] ReadPixels(int size)
		{
			var previous = RenderTexture.active;
			RenderTexture.active = _target;

			var readback = new Texture2D(size, size, TextureFormat.RGBAHalf, false, linear: true);
			readback.ReadPixels(new Rect(0, 0, size, size), 0, 0);
			readback.Apply();

			RenderTexture.active = previous;

			var pixels = readback.GetPixels();
			Object.DestroyImmediate(readback);
			return pixels;
		}

		private T Track<T>(T obj) where T : Object
		{
			_objects.Add(obj);
			return obj;
		}
	}
}
