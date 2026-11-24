using Saesentsessis.Shaders.Stripping;
using UnityEditor;
using UnityEngine;

namespace Saesentsessis.Fuluma.Editor.Build
{
	/// <summary>
	/// Fuluma's per-project Editor settings, in <c>ProjectSettings/FulumaSettings.asset</c> (versioned with the project,
	/// never part of a build). Edited on the Project Settings > Fuluma page.
	/// </summary>
	[FilePath("ProjectSettings/FulumaSettings.asset", FilePathAttribute.Location.ProjectFolder)]
	internal sealed class FulumaProjectSettings : ScriptableSingleton<FulumaProjectSettings>
	{
		[SerializeField] private ShaderStrippingSettings stripping = new();

		/// <summary>
		/// Variant stripping of Fuluma's shaders in player builds.
		/// </summary>
		public ShaderStrippingSettings Stripping => stripping ??= new ShaderStrippingSettings();

		/// <summary>
		/// Writes the settings to their file.
		/// </summary>
		public void SaveSettings() => Save(true);

		private void OnEnable()
		{
			// Editable, so Undo can record it.
			hideFlags &= ~HideFlags.NotEditable;
		}
	}
}
