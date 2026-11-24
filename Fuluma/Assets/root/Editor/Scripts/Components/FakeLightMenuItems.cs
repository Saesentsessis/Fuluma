using Saesentsessis.Fuluma.Rendering.Components;
using UnityEditor;
using UnityEngine;

namespace Saesentsessis.Fuluma.Editor.Components
{
	/// <summary>
	/// GameObject &gt; Light &gt; Volumetric: new GameObjects with the fake light components, mirroring their
	/// Add Component menu paths.
	/// </summary>
	internal static class FakeLightMenuItems
	{
		private const string Root = "GameObject/Light/Volumetric/";
		private const int Priority = 20;

		[MenuItem(Root + "Fake Spot Light", false, Priority)]
		private static void CreateFakeSpotLight(MenuCommand command)
			=> Create(command, "Fake Spot Light", typeof(FakeSpotLight));

		/// <summary>
		/// A Unity spot <see cref="Light"/> with a <see cref="LinkedFakeLight"/> that mirrors it.
		/// </summary>
		[MenuItem(Root + "Fake Light (Linked)", false, Priority + 1)]
		private static void CreateLinkedFakeLight(MenuCommand command)
		{
			var go = Create(command, "Fake Light (Linked)", typeof(Light), typeof(LinkedFakeLight));
			go.GetComponent<Light>().type = LightType.Spot;
		}

		[MenuItem(Root + "Fake Light Clip Box", false, Priority + 2)]
		private static void CreateFakeLightClipBox(MenuCommand command)
			=> Create(command, "Fake Light Clip Box", typeof(FakeLightClipBox));

		/// <summary>
		/// Like Unity's own create menu items: under the context object when there is one (aligned to it), else at the
		/// Scene view's pivot; undoable and selected.
		/// </summary>
		private static GameObject Create(MenuCommand command, string name, params System.Type[] components)
		{
			// ObjectFactory registers the creation with Undo and applies the component presets.
			var go = ObjectFactory.CreateGameObject(name, components);

			if (command.context is GameObject parent)
				GameObjectUtility.SetParentAndAlign(go, parent);
			else if (SceneView.lastActiveSceneView != null)
				go.transform.position = SceneView.lastActiveSceneView.pivot;

			Selection.activeGameObject = go;
			return go;
		}
	}
}
