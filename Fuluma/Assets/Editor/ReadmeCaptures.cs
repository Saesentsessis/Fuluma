using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Saesentsessis.Fuluma.Data;
using Saesentsessis.Fuluma.Rendering.Components;
using Saesentsessis.Fuluma.Rendering.URP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// Renders the README's pictures from a JSON description of setups (scene, fake lights, clip boxes) and shots (camera,
/// clip boxes on or off). Camera renders, so no gizmos; box outlines are optional meshes. The PNGs go to the config's
/// output folder, composed into the final images by <c>docs/dev/Scripts/compose_readme_images.py</c>. Nothing is saved
/// to the scenes. Command line (drop <c>-nographics</c>):
/// <c>Unity.exe -batchmode -projectPath &lt;project&gt; -executeMethod ReadmeCaptures.CaptureFromCommandLine
/// -readmeCaptureConfig &lt;json&gt;</c>.
/// </summary>
public static class ReadmeCaptures
{
	private const string ConfigArgument = "-readmeCaptureConfig";
	private const string DefaultConfigPath = "Builds/ReadmeCaptures/captures.json";

	/// <summary>
	/// Batch-mode entry point; exits with 1 on failure.
	/// </summary>
	public static void CaptureFromCommandLine()
	{
		try
		{
			Capture(ConfigPathFromCommandLine());
			EditorApplication.Exit(0);
		}
		catch (Exception exception)
		{
			Debug.LogException(exception);
			EditorApplication.Exit(1);
		}
	}

	[MenuItem("Tools/Fuluma/README Captures")]
	private static void CaptureFromMenu()
	{
		if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
			Capture(DefaultConfigPath);
	}

	private static string ConfigPathFromCommandLine()
	{
		var arguments = Environment.GetCommandLineArgs();
		var index = Array.IndexOf(arguments, ConfigArgument);
		return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : DefaultConfigPath;
	}

	private static void Capture(string configPath)
	{
		var config = JsonUtility.FromJson<CaptureConfig>(File.ReadAllText(configPath));
		Directory.CreateDirectory(config.outputFolder);

		var feature = FindFeature();
		var saved = FeatureSettings.From(feature);
		var asyncCompilation = ShaderUtil.allowAsyncCompilation;
		ShaderUtil.allowAsyncCompilation = false;
		try
		{
			foreach (var setup in config.setups)
			{
				(setup.overrideFeature ? setup.feature : config.feature).ApplyTo(feature);
				RenderSetup(config, setup);
			}
		}
		finally
		{
			ShaderUtil.allowAsyncCompilation = asyncCompilation;
			saved.ApplyTo(feature);
		}
	}

	private static FulumaRenderFeature FindFeature()
	{
		var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
		if (pipeline == null)
			throw new InvalidOperationException("The active render pipeline isn't URP.");

		foreach (var rendererData in pipeline.rendererDataList)
			if (rendererData != null && rendererData.TryGetRendererFeature(out FulumaRenderFeature feature))
				return feature;
		throw new InvalidOperationException("No renderer of the active URP asset has a Fuluma Render Feature.");
	}

	private static void RenderSetup(CaptureConfig config, CaptureSetup setup)
	{
		var scene = string.IsNullOrEmpty(setup.scene)
			? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single)
			: EditorSceneManager.OpenScene(setup.scene, OpenSceneMode.Single);

		if (setup.disableSceneFakeLights)
			DisableSceneFakeLights();
		HideObjects(setup.hideObjectsNamed);
		if (setup.survey)
			LogSurvey(scene);

		var boxes = setup.boxes.Select(CreateBox).ToArray();
		var outlines = setup.boxes.Select((box, i) => box.outline ? CreateOutline(box, setup.outlineWidth, i) : null)
			.Where(outline => outline != null).ToArray();
		var lights = setup.lights.Select(CreateLight).ToArray();
		var camera = CreateCamera(setup);

		foreach (var shot in setup.shots)
		{
			for (var i = 0; i < lights.Length; i++)
				lights[i].ClipBoxes = shot.clipBoxes ? SlotsOf(setup.lights[i], boxes) : ClipBoxSlots.None;
			foreach (var outline in outlines)
				outline.SetActive(shot.outlines);
			RenderShot(config, camera, shot);
		}
	}

	private static void DisableSceneFakeLights()
	{
		foreach (var light in UnityEngine.Object.FindObjectsByType<FakeLightComponent>())
			light.enabled = false;
		foreach (var box in UnityEngine.Object.FindObjectsByType<FakeLightClipBox>())
			box.enabled = false;
	}

	private static void HideObjects(string[] namePrefixes)
	{
		if (namePrefixes.Length == 0)
			return;

		foreach (var transform in UnityEngine.Object.FindObjectsByType<Transform>())
			if (namePrefixes.Any(prefix => transform.name.StartsWith(prefix, StringComparison.Ordinal)))
			{
				transform.gameObject.SetActive(false);
				Debug.Log($"[ReadmeCaptures] hid {transform.name}");
			}
	}

	private static void LogSurvey(Scene scene)
	{
		var renderers = UnityEngine.Object.FindObjectsByType<Renderer>();
		if (renderers.Length == 0)
			return;

		var bounds = renderers[0].bounds;
		foreach (var renderer in renderers)
			bounds.Encapsulate(renderer.bounds);
		Debug.Log($"[ReadmeCaptures] {scene.name}: {renderers.Length} renderers, bounds {bounds.min} to {bounds.max}");

		foreach (var renderer in renderers.OrderByDescending(r => r.bounds.size.sqrMagnitude).Take(40))
			Debug.Log($"[ReadmeCaptures]   {renderer.name}: {renderer.bounds.min} to {renderer.bounds.max}");
		foreach (var camera in UnityEngine.Object.FindObjectsByType<Camera>())
			Debug.Log($"[ReadmeCaptures]   camera {camera.name}: {camera.transform.position} {camera.transform.eulerAngles}");
	}

	private static FakeLightClipBox CreateBox(BoxSpec spec, int index)
	{
		var gameObject = new GameObject($"Capture Box {index}");
		gameObject.transform.SetPositionAndRotation(spec.position, Quaternion.Euler(spec.euler));
		var box = gameObject.AddComponent<FakeLightClipBox>();
		box.Shape = new ClipBoxData { Center = Vector3.zero, Size = spec.size };
		return box;
	}

	private static FakeSpotLight CreateLight(LightSpec spec, int index)
	{
		var gameObject = new GameObject($"Capture Light {index}");
		gameObject.transform.SetPositionAndRotation(spec.position,
			Quaternion.LookRotation(spec.target - spec.position, Math.Abs((spec.target - spec.position).normalized.y) > 0.99f ? Vector3.forward : Vector3.up));
		var light = gameObject.AddComponent<FakeSpotLight>();
		light.Data = new SpotLightData
		{
			Color = spec.color,
			Intensity = spec.intensity,
			Range = spec.range,
			InnerAngleDegrees = spec.inner,
			OuterAngleDegrees = spec.outer
		};
		return light;
	}

	private static ClipBoxSlots SlotsOf(LightSpec spec, FakeLightClipBox[] boxes)
	{
		var slots = ClipBoxSlots.None;
		for (var i = 0; i < spec.boxes.Length && i < ClipBoxSlots.Count; i++)
		{
			var role = i < spec.subtract.Length && spec.subtract[i] ? ClipBoxRole.Subtract : ClipBoxRole.Keep;
			slots[i] = new ClipBoxSlot(boxes[spec.boxes[i]], role);
		}

		return slots;
	}

	private static Camera CreateCamera(CaptureSetup setup)
	{
		var sceneCamera = setup.useSceneCamera ? Camera.main : null;
		if (sceneCamera != null)
			return sceneCamera;

		var camera = new GameObject("Capture Camera").AddComponent<Camera>();
		camera.clearFlags = CameraClearFlags.SolidColor;
		camera.backgroundColor = setup.background;
		camera.nearClipPlane = 0.05f;
		camera.farClipPlane = 500f;
		return camera;
	}

	private static void RenderShot(CaptureConfig config, Camera camera, Shot shot)
	{
		camera.transform.position = shot.position;
		camera.transform.LookAt(shot.target);
		camera.fieldOfView = shot.fov;

		var target = new RenderTexture(config.width, config.height, 24, RenderTextureFormat.ARGB32,
			RenderTextureReadWrite.sRGB);
		var previousTarget = camera.targetTexture;
		var previousActive = RenderTexture.active;
		var readback = new Texture2D(config.width, config.height, TextureFormat.RGBA32, false, false);
		try
		{
			camera.targetTexture = target;
			// The first render uploads the store and fills the culling history some passes are recorded from.
			for (var i = 0; i < config.renderCount; i++)
				camera.Render();

			RenderTexture.active = target;
			readback.ReadPixels(new Rect(0, 0, config.width, config.height), 0, 0);
			var path = Path.Combine(config.outputFolder, shot.output + ".png");
			File.WriteAllBytes(path, readback.EncodeToPNG());
			Debug.Log($"[ReadmeCaptures] wrote {path}");
		}
		finally
		{
			camera.targetTexture = previousTarget;
			RenderTexture.active = previousActive;
			UnityEngine.Object.DestroyImmediate(readback);
			target.Release();
			UnityEngine.Object.DestroyImmediate(target);
		}
	}

	private static GameObject CreateOutline(BoxSpec spec, float width, int index)
	{
		var gameObject = new GameObject($"Capture Box Outline {index}");
		gameObject.transform.SetPositionAndRotation(spec.position, Quaternion.Euler(spec.euler));
		gameObject.AddComponent<MeshFilter>().sharedMesh = CreateOutlineMesh(spec.size, width);

		var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
		material.SetColor("_BaseColor", spec.outlineColor);
		gameObject.AddComponent<MeshRenderer>().sharedMaterial = material;
		return gameObject;
	}

	private static Mesh CreateOutlineMesh(Vector3 size, float width)
	{
		var vertices = new List<Vector3>();
		var indices = new List<int>();
		var half = size * 0.5f;
		for (var axis = 0; axis < 3; axis++)
		for (var corner = 0; corner < 4; corner++)
		{
			var center = Vector3.zero;
			var extents = Vector3.one * (width * 0.5f);
			var u = (axis + 1) % 3;
			var v = (axis + 2) % 3;
			center[u] = (corner & 1) == 0 ? -half[u] : half[u];
			center[v] = (corner & 2) == 0 ? -half[v] : half[v];
			extents[axis] = half[axis] + width * 0.5f;
			AddCuboid(vertices, indices, center, extents);
		}

		var mesh = new Mesh();
		mesh.SetVertices(vertices);
		mesh.SetTriangles(indices, 0);
		mesh.RecalculateBounds();
		return mesh;
	}

	private static void AddCuboid(List<Vector3> vertices, List<int> indices, Vector3 center, Vector3 extents)
	{
		var first = vertices.Count;
		for (var i = 0; i < 8; i++)
			vertices.Add(center + Vector3.Scale(extents,
				new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));

		// Two triangles per face, wound outward.
		int[] faces = { 0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5 };
		foreach (var index in faces)
			indices.Add(first + index);
	}

	[Serializable]
	private class CaptureConfig
	{
		public int width = 3840;
		public int height = 2160;
		public int renderCount = 3;
		public string outputFolder = "Builds/ReadmeCaptures";
		/// <summary>Feature settings for the captures (the feature's defaults); the asset's are restored afterwards.</summary>
		public FeatureSettings feature = new();
		public CaptureSetup[] setups = Array.Empty<CaptureSetup>();
	}

	[Serializable]
	private class FeatureSettings
	{
		/// <summary>After the skybox, so the sky doesn't overwrite glow drawn over it.</summary>
		public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
		public float lightIntensity = 1f;
		public ScatteringPhase scatteringPhase = ScatteringPhase.Isotropic;
		public float scatteringAnisotropy = 0.7f;
		public DistanceFalloff distanceFalloff = DistanceFalloff.RangeScaled;
		public LampGlare lampGlare = LampGlare.None;
		public float lightBufferScale = 0.5f;

		public static FeatureSettings From(FulumaRenderFeature feature) => new()
		{
			renderPassEvent = feature.RenderPassEvent,
			lightIntensity = feature.LightIntensity,
			scatteringPhase = feature.ScatteringPhase,
			scatteringAnisotropy = feature.ScatteringAnisotropy,
			distanceFalloff = feature.DistanceFalloff,
			lampGlare = feature.LampGlare,
			lightBufferScale = feature.LightBufferScale
		};

		public void ApplyTo(FulumaRenderFeature feature)
		{
			feature.RenderPassEvent = renderPassEvent;
			feature.LightIntensity = lightIntensity;
			feature.ScatteringPhase = scatteringPhase;
			feature.ScatteringAnisotropy = scatteringAnisotropy;
			feature.DistanceFalloff = distanceFalloff;
			feature.LampGlare = lampGlare;
			feature.LightBufferScale = lightBufferScale;
		}
	}

	[Serializable]
	private class CaptureSetup
	{
		public string name;
		/// <summary>Scene asset path; empty for a new empty scene.</summary>
		public string scene;
		public bool disableSceneFakeLights = true;
		public bool useSceneCamera;
		/// <summary>Scene objects whose name starts with one of these are deactivated.</summary>
		public string[] hideObjectsNamed = Array.Empty<string>();
		public bool survey;
		/// <summary>Use <see cref="feature"/> instead of the config's feature settings for this setup.</summary>
		public bool overrideFeature;
		public FeatureSettings feature = new();
		public Color background = Color.black;
		public float outlineWidth = 0.03f;
		public LightSpec[] lights = Array.Empty<LightSpec>();
		public BoxSpec[] boxes = Array.Empty<BoxSpec>();
		public Shot[] shots = Array.Empty<Shot>();
	}

	[Serializable]
	private class LightSpec
	{
		public Vector3 position;
		public Vector3 target;
		public Color color = Color.white;
		public float intensity = 1f;
		public float range = 5f;
		public float inner = 20f;
		public float outer = 30f;
		/// <summary>Indices into the setup's boxes, one per clip box slot.</summary>
		public int[] boxes = Array.Empty<int>();
		/// <summary>Per slot: true for Subtract, false (or missing) for Keep.</summary>
		public bool[] subtract = Array.Empty<bool>();
	}

	[Serializable]
	private class BoxSpec
	{
		public Vector3 position;
		public Vector3 euler;
		public Vector3 size = Vector3.one;
		public bool outline;
		public Color outlineColor = new(0.8f, 0.65f, 0.3f, 1f);
	}

	[Serializable]
	private class Shot
	{
		public string output;
		public Vector3 position;
		public Vector3 target;
		public float fov = 60f;
		public bool clipBoxes = true;
		public bool outlines = true;
	}
}
