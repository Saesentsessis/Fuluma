---
name: fuluma-dev-render-debug
description: Diagnoses Fuluma fake light rendering problems while developing the Fuluma package in this repository (Unity URP fake volumetric spot lights, FulumaRenderFeature, LightStore, clip boxes, light buffer, shader variants). Use it whenever a fake light is invisible, missing, wrongly lit, clipped wrongly, different in a player or on a phone, a render test fails or produces a wrong image, a "[Fuluma]" error or warning appears, or a measurement looks off, in the dev project, its tests, profiling players or captures, even if the request only says "the lights look wrong" or "test X fails".
---

# Debugging Fuluma rendering (repository development)

This repository *is* Fuluma: you can read and run everything, which a package user can't. Use that to turn a
suspicion into evidence (a log line, a rendered image, a failing or passing test) before proposing a fix.

## Start with the user-facing decision tree

Read `plugins/fuluma/skills/fuluma-render-debug/SKILL.md` (and its `references/unity-yaml.md`) first and walk it: it
is the shipped diagnosis path (component → store → feature on the active renderer → camera filter → GPU/shader, then
clip boxes, look, builds, performance) with the exact log messages and YAML fields. This skill adds what only the
repository offers. If you find a cause the shipped skill doesn't cover, say so: it probably belongs in that file too.

**Diagnose, then ask before changing anything.** The dev project's scenes, URP assets and project settings are
edited by the user for their own experiments (MSAA, pass event, scattering settings, stripping mode change often):
never "fix" them silently, and don't revert their edits. Commit only when asked.

## Where things are

| What | Where |
|---|---|
| Package source (ships) | `Fuluma/Assets/root/` (Runtime, Editor, Shaders, Tests, `Documentation~`, README) |
| Dev project | `Fuluma/` (Unity 6000.7 beta; URP asset `Assets/New Universal Render Pipeline Asset*.asset`, every quality level uses it) |
| Scenes | `Fuluma/Assets/Scenes/` (`Benchmark.unity` = stress test; demos may be untracked) |
| Dev tools | `Fuluma/Assets/Profiling/` (stress test, `MobileGpuStats`, `FlyCamera`), `Fuluma/Assets/Editor/` (profiling builds, `ReadmeCaptures`, `SpotLightShaderDump`), `Fuluma/Assets/DevTools/` |
| Research and decisions | `docs/dev/` (`PixelShaderIdeas.md` measurements, `ShaderInspection.md`, `VariantStripping.md`, `Lifecycle.md`, `Backlog.md` known issues) |
| Local notes | `.claude/CLAUDE.md` when it exists (git-ignored; the most detailed map, but don't rely on it in other clones) |

Code entry points for the usual suspects:

- Support check and pass creation: `FulumaSpotLightRenderPass.Create` (`Runtime/Rendering/URP/`), its error message
  constants at the top of that file.
- Camera filter: `FulumaRenderFeature` (`camera.cameraType != CameraType.Preview && (camera.cullingMask & cameraLayers) != 0`)
  and `AddRenderPasses`.
- Store registration and lifecycle: `FakeLightComponent`, `LinkedFakeLight(System)`, `Core/Lifecycle/FulumaLifecycle`
  (stores release on every play mode switch and code reload).
- Upload: `Rendering/Buffers/SpotLightGpuMirror*` (dirty ranges, rings written once per `Time.frameCount`).
- Culling: `Rendering/Culling/SpotLightCuller`, `SpotLightCullingJob` (buckets, LODs, inside/outside pass).
- Editor diagnosis the user sees: `Editor/Scripts/Components/RegistrationStatus.cs`, `StoreFeatureLookup.cs`,
  `ClipBoxSlotsDrawer.cs`.
- Variants: `Editor/Scripts/Build/FulumaShaderKeywords.cs` (schema; must match the shader's keywords),
  `FulumaShaderStrippingSource.cs` (Automatic rules).

## Getting evidence

**Batch-mode Unity** (only while the Editor is closed: `Fuluma/Temp/UnityLockfile` must not exist; ask the user to
close it otherwise). Editor: `C:/Program Files/Unity/Hub/Editor/6000.7.0b4/Editor/Unity.exe`.

- Compile check: `-batchmode -quit -nographics -projectPath <Fuluma> -logFile <log>`; errors are `error CS` lines.
- Tests: `-batchmode -projectPath <Fuluma> -runTests -testPlatform EditMode -assemblyNames
  "Saesentsessis.Fuluma.Editor.Tests" -testResults <xml> -logFile <log>`.
  Keep the GPU: no `-nographics` (render tests skip without a device). Read the NUnit XML, not the exit code. Add
  `-testFilter <regex>` for one test, `-force-d3d11` for Direct3D 11.
- **Reproduce a rendering bug as a test** with `Tests/Editor/Utils/RenderScene` (renders through the project's URP
  asset into a readback; pins full resolution, `LightBufferLights.All` and intensity 1, restores afterwards) and
  compare against `LightContribution` (brute-force integral). Lights for tests go into `Feature.Store`, never
  `LightStore.Default`. Tests that change renderer settings must re-read `Feature.SpotLightRenderPass` afterwards
  (the pass is recreated).
- **See a scene without the Editor UI:** `ReadmeCaptures` (`Fuluma/Assets/Editor/ReadmeCaptures.cs`) renders camera
  shots of any scene or an empty black scene with fake lights and clip boxes from a JSON config, pins the feature
  settings and restores them: `-executeMethod ReadmeCaptures.CaptureFromCommandLine -readmeCaptureConfig <json>`
  (no `-nographics`). Camera renders contain no gizmos. Look at the PNGs yourself before concluding.

**Players and phones.**

- Profiling builds (`FulumaProfilingBuild`, menu Tools > Fuluma > Profiling, or `-executeMethod
  FulumaProfilingBuild.BuildFromCommandLine` / `BuildAndroidFromCommandLine` / `BuildAndroidGlesFromCommandLine`)
  keep every Fuluma variant, so stripping can't explain a difference there; a user build can (check
  `Logs/ShaderStripping/*.json` and the build log's `[Shader Stripping]` lines).
- The stress test (`SpotLightStressTest`) prints its overlay to the log with `-fulumaLogStats`: lights drawn, per LOD,
  culling and draw timings, light buffer size in pixels. Command-line switches set feature settings (the first
  occurrence of a switch wins); the list is in the class's `*Argument` constants.
- Android: Unity's adb (`.../Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe`),
  `Fuluma/Builds/Profiling/android_run.sh <name> <seconds> <switches>` runs one launch and keeps the logcat and a
  screenshot under `Builds/Profiling/Captures/Android/<model>/`. Read the screenshot. Don't do anything on a phone an
  end user wouldn't (no root, no developer-setting tweaks beyond USB debugging).
- Desktop GPU detail: Nsight Graphics traces via `Fuluma/Builds/Profiling/gpu_trace.ps1` / `compare_light_buffer.ps1`
  (run long commands through a script file in the user's Terminal panel).

**Shaders.** `SpotLightShaderDump` + `fxc` + `docs/dev/Scripts/dxbc_cost.py` for instruction cost per variant
(`docs/dev/ShaderInspection.md`). A GLES failure shows as driver link errors in logcat ("GLSL link error: The number of
vertex shader storage blocks (N) is greater than the maximum number allowed (0)"): that's the platform limit, not a
bug (`docs/dev/Backlog.md`).

## Traps seen in this repository

- **The dev URP asset is the user's playground.** MSAA, the pass event, scattering and stripping settings change
  between sessions. A "regression" may be a setting: read the renderer and URP asset before suspecting code. For
  measurements, pin settings on the command line (`-fulumaMsaa 1` etc.).
- **MSAA multiplies the light buffer's cost** on mobile (URP's depth copy); compare like with like.
- **Event After Rendering Opaques** lets the skybox cover glow in front of the sky (default is After Rendering Skybox
  since 2026-11-23; older renderer assets keep 300).
- **Frame guards:** dynamic rings are written once per `Time.frameCount`; batch-mode renders don't advance it, so tests
  call `SpotLightGpuMirror.ForgetFrameGuardsForTests()` between renders (a missing call shows stale dynamic lights).
- **A mirror released mid-frame stays until `endContextRendering`** (URP rebuilds renderers mid-frame); a fresh one
  draws nothing that frame.
- **`Library/BurstCache` under the wrong folder** comes from running Unity or `bcl` from inside `Assets/`; launch from
  the project folder.
- **A new or renamed shader keyword** must be added to the stripping schema
  (`Editor/Scripts/Build/FulumaShaderKeywords.cs`, a group per `multi_compile` line) and, when a feature setting
  drives it, to the Automatic rules in `FulumaShaderStrippingSource.cs`; otherwise `Schema_MatchesTheShadersKeywords`
  fails and builds strip without knowing the keyword. Design and rules: `docs/dev/VariantStripping.md`.
- **Costs differ by GPU family.** The phone tables in `docs/dev/PixelShaderIdeas.md` cover Mali (G51, G52, G77) and
  Adreno 618. The clip box variants move box terms into varyings (the general variant packs four boxes into 3 flat +
  3 interpolated `float4`); on the Adreno 618 three boxes cost +175% against +39–70% on Mali. Check a variant's static
  cost with `SpotLightShaderDump` (desktop DXBC) and, for mobile, a vendor offline compiler when one is installed
  (Mali's can't be downloaded here); vary one thing at a time with the stress test's switches.
- **6000.7 hides missing asmdef references** that 6000.3 (the minimum) rejects: verify asmdef changes on 6000.3 from a
  short scratch path (long paths break Unity's package import on Windows).

## Report

Use the shipped skill's report shape (Cause / Evidence / Fix / Other findings), plus, when you reproduced it, how
(test name, capture config, log path) so the user can rerun it. Then ask before editing code or assets. When the fix
is in package code, say which test covers it or which one you'd add.
