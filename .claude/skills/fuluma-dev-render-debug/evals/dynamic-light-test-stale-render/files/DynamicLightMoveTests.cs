using NUnit.Framework;
using Saesentsessis.Fuluma.Rendering.Components;
using Saesentsessis.Fuluma.Tests.Editor.Utils;
using UnityEngine;

namespace Saesentsessis.Fuluma.Tests.Editor
{
	public sealed class DynamicLightMoveTests
	{
		[Test]
		public void DynamicLight_MovesBetweenRenders()
		{
			GpuRequirement.IgnoreUnlessFakeLightsDraw();
			using var scene = new RenderScene();

			var cameraObject = new GameObject("Test Camera");
			var camera = cameraObject.AddComponent<Camera>();
			var target = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGBHalf);
			camera.targetTexture = target;
			camera.transform.SetPositionAndRotation(new Vector3(0f, 0f, -4f), Quaternion.identity);

			var lightObject = new GameObject("Dynamic Light");
			lightObject.transform.SetPositionAndRotation(new Vector3(-1f, 0f, 3f), Quaternion.LookRotation(Vector3.back));
			var light = lightObject.AddComponent<FakeSpotLight>();
			light.OverrideStore = scene.Feature.Store;
			light.SyncTransform = true;
			light.Data = RenderScene.DefaultTestLight();

			camera.Render();
			var before = Read(target);

			lightObject.transform.position = new Vector3(1f, 0f, 3f);
			camera.Render();
			var after = Read(target);

			Object.DestroyImmediate(lightObject);
			Object.DestroyImmediate(cameraObject);
			target.Release();

			var largest = 0f;
			for (var i = 0; i < before.Length; i++)
				largest = Mathf.Max(largest, Mathf.Abs(before[i].r - after[i].r));
			Assert.That(largest, Is.GreaterThan(0.05f), "The glow should move with the light.");
		}

		private static Color[] Read(RenderTexture target)
		{
			var previous = RenderTexture.active;
			RenderTexture.active = target;
			var texture = new Texture2D(target.width, target.height, TextureFormat.RGBAHalf, false);
			texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
			RenderTexture.active = previous;
			var pixels = texture.GetPixels();
			Object.DestroyImmediate(texture);
			return pixels;
		}
	}
}
