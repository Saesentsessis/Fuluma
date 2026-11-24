using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Editor.Tools
{
	/// <summary>
	/// Compiles every variant of the spot light shader for several graphics APIs and writes the compiled blobs to disk,
	/// for disassembly and offline analysis (fxc/dxc, spirv-dis/spirv-cross, Mali Offline Compiler, ...).
	/// See docs/dev/ShaderInspection.md.
	/// </summary>
	/// <remarks>
	/// Menu: Tools > Fuluma > Dump Spot Light Shader. Batch mode:
	/// <c>Unity.exe -batchmode -quit -projectPath &lt;project&gt; -executeMethod Saesentsessis.Fuluma.Editor.Tools.SpotLightShaderDump.DumpFromCommandLine -fulumaShaderDumpDir &lt;dir&gt;</c>
	/// </remarks>
#if UNITY_6000_5_OR_NEWER
	// Immutable: tables of what to dump.
	[NoAutoStaticsCleanup]
#endif
	public static class SpotLightShaderDump
	{
		// Each shader goes to its own subfolder, so variants can be dumped side by side for comparison (add entries here).
		private static readonly (string name, string folder)[] Shaders =
		{
			("Hidden/Fuluma/Analytical/SpotLight", "SpotLight")
		};
		private const string DefaultDirectory = "Temp/FulumaShaderDump";
		private const string DirectoryArgument = "-fulumaShaderDumpDir";

		private static readonly string[][] DepthVariants =
		{
			Array.Empty<string>(),
			new[] { "_DEPTH_AS_INPUT_ATTACHMENT" },
			new[] { "_DEPTH_AS_INPUT_ATTACHMENT_MSAA" },
			new[] { "_DEPTH_LOW_RES" },
			new[] { "_FULUMA_CLIP_BOX_SINGLE" },
			new[] { "_FULUMA_CLIP_BOX_PAIR" },
			new[] { "_FULUMA_CLIP_BOX_UNION" },
			new[] { "_FULUMA_CLIP_BOX_EXCLUDE" },
			new[] { "_FULUMA_CLIP_BOXES" },
			new[] { "_DEPTH_LOW_RES", "_FULUMA_CLIP_BOX_SINGLE" },
			new[] { "_DEPTH_LOW_RES", "_FULUMA_CLIP_BOXES" },
			new[] { "_FULUMA_PHASE_HG" },
			new[] { "_FULUMA_FALLOFF_UNITY" },
			new[] { "_FULUMA_POINT_LAMP" },
			new[] { "_FULUMA_PHASE_HG", "_FULUMA_FALLOFF_UNITY", "_FULUMA_POINT_LAMP" }
		};

		private static readonly (ShaderCompilerPlatform platform, BuildTarget target, string extension)[] Targets =
			new (ShaderCompilerPlatform platform, BuildTarget target, string extension)?[]
				{
					(ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64, "dxbc"),
					// D3D12 has its own compiler platform only in newer Editors (not in 6000.3); looked up by name.
					Enum.TryParse<ShaderCompilerPlatform>("D3D12", out var d3d12)
						? (d3d12, BuildTarget.StandaloneWindows64, "d3d12")
						: null,
					(ShaderCompilerPlatform.Vulkan, BuildTarget.Android, "vulkan"),
					(ShaderCompilerPlatform.GLES3x, BuildTarget.Android, "gles3"),
					(ShaderCompilerPlatform.Metal, BuildTarget.iOS, "metal")
				}
				.Where(target => target.HasValue)
				.Select(target => target.Value)
				.ToArray();

		[MenuItem("Tools/Fuluma/Dump Spot Light Shader")]
		private static void DumpFromMenu()
		{
			var directory = Dump(DefaultDirectory);
			EditorUtility.RevealInFinder(directory);
		}

		/// <summary>
		/// Entry point for <c>-executeMethod</c>; reads the output folder from <c>-fulumaShaderDumpDir</c>.
		/// </summary>
		public static void DumpFromCommandLine()
		{
			var args = Environment.GetCommandLineArgs();
			var index = Array.IndexOf(args, DirectoryArgument);
			var directory = index >= 0 && index + 1 < args.Length ? args[index + 1] : DefaultDirectory;
			Dump(directory);
		}

		/// <summary>
		/// Dumps every listed shader into <paramref name="directory"/>, writing a <c>report.txt</c> per shader.
		/// </summary>
		/// <param name="directory">Output folder, relative to the project folder or absolute.</param>
		/// <returns>The full path of the folder written to (one subfolder per shader).</returns>
		/// <exception cref="InvalidOperationException">A listed shader is not found.</exception>
		public static string Dump(string directory)
		{
			directory = Path.GetFullPath(directory);

			foreach (var (name, folder) in Shaders)
				DumpShader(name, Path.Combine(directory, folder));

			return directory;
		}

		private static void DumpShader(string shaderName, string directory)
		{
			var shader = Shader.Find(shaderName);
			if (shader == null)
				throw new InvalidOperationException($"Shader '{shaderName}' not found.");

			Directory.CreateDirectory(directory);

			var subshader = ShaderUtil.GetShaderData(shader).GetSubshader(0);
			var report = new StringBuilder();
			report.AppendLine($"Shader: {shaderName}, Unity {Application.unityVersion}, {subshader.PassCount} passes");

			for (var passIndex = 0; passIndex < subshader.PassCount; passIndex++)
			{
				var pass = subshader.GetPass(passIndex);
				DumpPass(pass, $"{passIndex}{pass.Name}", directory, report);
			}

			File.WriteAllText(Path.Combine(directory, "report.txt"), report.ToString());
			Debug.Log($"[Fuluma] Shader dump written to {directory}\n{report}");
		}

		/// <summary>
		/// Every target and keyword variant (depth sources, clip box classes) of one pass, plus the preprocessed HLSL
		/// of its default D3D variant. File names start with the pass index and name, e.g.
		/// <c>0FulumaSpotLightInside_D3D_default_Fragment.dxbc</c>.
		/// </summary>
		private static void DumpPass(ShaderData.Pass pass, string passName, string directory, StringBuilder report)
		{
			foreach (var (platform, target, extension) in Targets)
			foreach (var keywords in DepthVariants)
			foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
			{
				var variantName = keywords.Length == 0 ? "default" : string.Join("+", keywords).Trim('_');
				var fileName = $"{passName}_{platform}_{variantName}_{stage}.{extension}";

				VariantCompileInfoSafe(pass, stage, keywords, platform, target, out var success, out var bytes, out var messages);

				var line = $"{fileName}: {(success ? $"{bytes.Length} bytes" : "FAILED")}";
				if (messages.Length > 0)
					line += " | " + string.Join(" | ", messages);

				report.AppendLine(line);

				if (success && bytes.Length > 0)
					File.WriteAllBytes(Path.Combine(directory, fileName), bytes);
			}

			// Preprocessed HLSL of the default D3D variant: what Unity hands to the compiler, for fxc/dxc or reading.
			foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
			{
				var preprocessed = pass.PreprocessVariant(stage, Array.Empty<string>(), ShaderCompilerPlatform.D3D,
					BuildTarget.StandaloneWindows64, stripLineDirectives: true);

				if (preprocessed.Success)
					File.WriteAllText(Path.Combine(directory, $"{passName}_D3D11_default_{stage}.preprocessed.hlsl"), preprocessed.PreprocessedCode);
				else
					report.AppendLine($"Preprocess {passName} {stage} failed");
			}
		}

		private static void VariantCompileInfoSafe(ShaderData.Pass pass, ShaderType stage, string[] keywords,
			ShaderCompilerPlatform platform, BuildTarget target, out bool success, out byte[] bytes, out string[] messages)
		{
			try
			{
				// forExternalTool: plain bytecode for third-party tools instead of Unity's runtime container.
				var info = pass.CompileVariant(stage, keywords, platform, target, forExternalTool: true);
				success = info.Success;
				bytes = info.ShaderData ?? Array.Empty<byte>();
				messages = info.Messages?.Select(m => $"{m.severity}: {m.message} ({Path.GetFileName(m.file)}:{m.line})").ToArray()
				           ?? Array.Empty<string>();
			}
			catch (Exception e)
			{
				success = false;
				bytes = Array.Empty<byte>();
				messages = new[] { e.GetType().Name + ": " + e.Message };
			}
		}
	}
}
