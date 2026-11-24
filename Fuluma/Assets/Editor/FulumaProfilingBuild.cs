using System;
using System.IO;
using System.Reflection;
using Saesentsessis.Shaders.Stripping;
using Saesentsessis.Fuluma.Tests;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Prepares the benchmark scene for GPU profiling (Nsight, RenderDoc) and builds a Windows development player of it.
/// Menu: Tools > Fuluma > Profiling. Command line:
/// <c>Unity.exe -batchmode -quit -projectPath &lt;project&gt; -executeMethod FulumaProfilingBuild.BuildFromCommandLine</c>
/// (the player goes to <c>Builds/Profiling/Fuluma.exe</c>; exit code 1 on failure).
/// </summary>
public static class FulumaProfilingBuild
{
	private const string ScenePath = "Assets/Scenes/Benchmark.unity";
	private const string PlayerPath = "Builds/Profiling/Fuluma.exe";
	private const string AndroidPlayerPath = "Builds/Profiling/Fuluma.apk";
	private const string AndroidApplicationId = "com.saesentsessis.fuluma.profiling";
	private const string AndroidGlesPlayerPath = "Builds/Profiling/Fuluma_gles.apk";
	private const string FulumaSettingsTypeName = "Saesentsessis.Fuluma.Editor.Build.FulumaProjectSettings, Saesentsessis.Fuluma.Editor";

	/// <summary>
	/// Adds a <see cref="FlyCamera"/> to the scene's main camera if it has none, and saves the scene.
	/// </summary>
	[MenuItem("Tools/Fuluma/Profiling/Add Fly Camera To Benchmark Scene")]
	public static void PrepareScene()
	{
		var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

		var camera = FindMainCamera();
		if (camera == null)
			throw new InvalidOperationException($"No camera in {ScenePath}.");

		if (camera.GetComponent<FlyCamera>() == null)
		{
			Undo.AddComponent<FlyCamera>(camera.gameObject);
			EditorSceneManager.MarkSceneDirty(scene);
			EditorSceneManager.SaveScene(scene);
			Debug.Log($"[Fuluma] Added FlyCamera to '{camera.name}' in {ScenePath}.");
		}

		AssignOccluderMaterial(scene);
	}

	private const string OccluderMaterialPath = "Assets/Profiling/Occluder.mat";
	private const string OccluderShaderName = "Universal Render Pipeline/Simple Lit";

	/// <summary>
	/// Gives the stress test's occluder boxes a material asset (URP Simple Lit, created if missing): the scene then
	/// references its shader, so builds include it. Primitives' default material isn't in a build, and the boxes drew
	/// with the error shader.
	/// </summary>
	private static void AssignOccluderMaterial(UnityEngine.SceneManagement.Scene scene)
	{
		var material = AssetDatabase.LoadAssetAtPath<Material>(OccluderMaterialPath);
		if (material == null)
		{
			var shader = Shader.Find(OccluderShaderName);
			if (shader == null)
				throw new InvalidOperationException($"Shader '{OccluderShaderName}' not found.");

			material = new Material(shader) { name = "Occluder", color = new Color(0.45f, 0.45f, 0.48f) };
			AssetDatabase.CreateAsset(material, OccluderMaterialPath);
			AssetDatabase.SaveAssets();
		}

		var changed = false;
		foreach (var stressTest in UnityEngine.Object.FindObjectsByType<SpotLightStressTest>(FindObjectsInactive.Include))
		{
			var serialized = new SerializedObject(stressTest);
			var property = serialized.FindProperty("occluderMaterial");
			if (property == null || property.objectReferenceValue == material)
				continue;

			property.objectReferenceValue = material;
			serialized.ApplyModifiedPropertiesWithoutUndo();
			changed = true;
		}

		if (changed)
		{
			EditorSceneManager.MarkSceneDirty(scene);
			EditorSceneManager.SaveScene(scene);
			Debug.Log($"[Fuluma] Assigned {OccluderMaterialPath} to the stress test in {ScenePath}.");
		}
	}

	/// <summary>
	/// Development build (the stress test's profiler-marker timings need one), 64-bit Windows, the benchmark scene only.
	/// </summary>
	[MenuItem("Tools/Fuluma/Profiling/Build Windows Player")]
	public static bool BuildPlayer()
		=> Build(PlayerPath, BuildTarget.StandaloneWindows64, BuildTargetGroup.Standalone);

	/// <summary>
	/// Development APK for on-device measurement: IL2CPP, ARM64, the benchmark scene only, at
	/// <c>Builds/Profiling/Fuluma.apk</c> (install with <c>adb install -r</c>). The landscape-only orientation and
	/// frame timing stats are project settings.
	/// </summary>
	[MenuItem("Tools/Fuluma/Profiling/Build Android Player")]
	public static bool BuildAndroidPlayer()
	{
		var android = NamedBuildTarget.Android;
		PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
		PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
		if (PlayerSettings.GetApplicationIdentifier(android) is var id && (string.IsNullOrEmpty(id) || id.StartsWith("com.DefaultCompany")))
			PlayerSettings.SetApplicationIdentifier(android, AndroidApplicationId);

		return Build(AndroidPlayerPath, BuildTarget.Android, BuildTargetGroup.Android);
	}

	/// <summary>
	/// Like <see cref="BuildAndroidPlayer"/> with OpenGL ES 3 as the only graphics API, at
	/// <c>Builds/Profiling/Fuluma_gles.apk</c>. Same application id, so installing one replaces the other: HyperOS asks
	/// on the phone before a USB install of a new package, not before an update. The project's graphics APIs are
	/// restored afterwards.
	/// </summary>
	[MenuItem("Tools/Fuluma/Profiling/Build Android Player (OpenGL ES 3)")]
	public static bool BuildAndroidGlesPlayer()
	{
		var android = NamedBuildTarget.Android;
		var useDefaultApis = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android);
		var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
		try
		{
			PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
			PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
			PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
			PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
			if (PlayerSettings.GetApplicationIdentifier(android) is var id && (string.IsNullOrEmpty(id) || id.StartsWith("com.DefaultCompany")))
				PlayerSettings.SetApplicationIdentifier(android, AndroidApplicationId);
			return Build(AndroidGlesPlayerPath, BuildTarget.Android, BuildTargetGroup.Android);
		}
		finally
		{
			PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, apis);
			PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, useDefaultApis);
		}
	}

	private static bool Build(string path, BuildTarget target, BuildTargetGroup group)
	{
		PrepareScene();

		var options = new BuildPlayerOptions
		{
			scenes = new[] { ScenePath },
			locationPathName = path,
			target = target,
			targetGroup = group,
			options = BuildOptions.Development
		};

		// The stress test switches variant settings at run time: keep every Fuluma shader variant (Custom stripping with
		// nothing denied) for this build only. The settings are internal to the package's Editor assembly; this
		// development tool reaches them by reflection and never saves them.
		var stripping = GetFulumaStrippingSettings();
		var mode = stripping.Mode;
		BuildReport report;
		try
		{
			stripping.Mode = StrippingMode.Custom;
			report = BuildPipeline.BuildPlayer(options);
		}
		finally
		{
			stripping.Mode = mode;
		}

		var summary = report.summary;

		if (summary.result != BuildResult.Succeeded)
		{
			Debug.LogError($"[Fuluma] Profiling build {summary.result}: {summary.totalErrors} errors.");
			return false;
		}

		Debug.Log($"[Fuluma] Profiling build at {Path.GetFullPath(path)} " +
		          $"({summary.totalSize / (1024f * 1024f):F1} MB, {summary.totalTime.TotalSeconds:F0} s).");
		return true;
	}

	public static void BuildAndroidFromCommandLine() => RunFromCommandLine(BuildAndroidPlayer);

	public static void BuildAndroidGlesFromCommandLine() => RunFromCommandLine(BuildAndroidGlesPlayer);

	public static void BuildFromCommandLine() => RunFromCommandLine(BuildPlayer);

	private static void RunFromCommandLine(Func<bool> build)
	{
		var succeeded = false;
		try
		{
			succeeded = build();
		}
		catch (Exception exception)
		{
			Debug.LogException(exception);
		}

		EditorApplication.Exit(succeeded ? 0 : 1);
	}

	private static ShaderStrippingSettings GetFulumaStrippingSettings()
	{
		var type = Type.GetType(FulumaSettingsTypeName, true);
		var settings = type.BaseType!.GetProperty("instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
		var stripping = type.GetProperty("Stripping", BindingFlags.Public | BindingFlags.Instance)!.GetValue(settings);
		return (ShaderStrippingSettings)stripping;
	}

	private static Camera FindMainCamera()
	{
		var cameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include);
		foreach (var camera in cameras)
			if (camera.CompareTag("MainCamera"))
				return camera;

		return cameras.Length > 0 ? cameras[0] : null;
	}
}
