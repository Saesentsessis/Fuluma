using System.Collections.Generic;
using Saesentsessis.Shaders.Stripping;
using UnityEditor;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Editor.Build
{
#if UNITY_6000_5_OR_NEWER
	// Immutable: search keywords.
	[NoAutoStaticsCleanup]
#endif
	/// <summary>
	/// The Project Settings > Fuluma page.
	/// </summary>
	internal sealed class FulumaSettingsProvider : SettingsProvider
	{
		private const string PagePath = "Project/Fuluma";

		private static readonly HashSet<string> SearchKeywords = new()
		{
			"Fuluma", "Shader", "Variant", "Stripping", "Keyword", "Build"
		};

		[SettingsProvider]
		private static SettingsProvider Create() => new FulumaSettingsProvider();

		private readonly ShaderStrippingSettingsGui _strippingGui = new();
		private readonly FulumaShaderStrippingSource _source = new();

		private FulumaSettingsProvider() : base(PagePath, SettingsScope.Project, SearchKeywords)
		{
		}

		public override void OnActivate(string searchContext, UnityEngine.UIElements.VisualElement rootElement)
		{
			Undo.undoRedoPerformed += OnUndoRedo;
			_strippingGui.MarkPreviewStale();
		}

		public override void OnDeactivate() => Undo.undoRedoPerformed -= OnUndoRedo;

		public override void OnGUI(string searchContext)
		{
			var settings = FulumaProjectSettings.instance;
			EditorGUIUtility.labelWidth = 220f;

			if (_strippingGui.Draw(settings, settings.Stripping, _source))
				settings.SaveSettings();
		}

		private void OnUndoRedo()
		{
			FulumaProjectSettings.instance.SaveSettings();
			_strippingGui.MarkPreviewStale();
			Repaint();
		}
	}
}
