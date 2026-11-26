# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.0] - 2026-11-26

### Added

- **Unity 2022.3 support** (URP 14), from 2022.3.20f1. Every feature works there, the light buffer and its timings
  included; verified with the full test suite on 2022.3.62f3 and on Android Vulkan (Mali-G52, Mali-G77).
- **URP Compatibility Mode support**, wherever URP still has it: always on Unity 2022.3, through the Graphics settings'
  checkbox on Unity 6.0 to 6.2, and on Unity 6.3 with Unity's `URP_COMPATIBILITY_MODE` scripting define. Every
  feature works in both modes: full resolution, the downsampled light buffer (All and Large Only, both upsamplers) and
  Before Opaques / Auto timing (Auto follows URP's Compatibility Mode prepass rules). Unity 6.4 and newer have only
  RenderGraph. The pass reads the mode when it is created; switching it recreates URP's pipeline and with it the
  feature's pass. Without depth input attachments there, full-resolution draws read URP's depth texture (URP adds a
  depth copy); a downsampled light buffer skips that copy where the camera certainly renders into an intermediate
  depth texture (HDR, the default). On the measured Mali phones both modes cost within a few percent of each other in
  the same Unity version.
- **Unity 6.0 to 6.2 support** on RenderGraph (Deferred+ needs 6.1).
- A **Light Buffer Scene Depth** keyword group in shader variant stripping: the light buffer's Compatibility Mode
  variant (`_FULUMA_CAMERA_DEPTH_TEXTURE`), kept only when the build's project uses Compatibility Mode.

### Changed

- **Minimum Unity version: 2022.3.20f1** (was 6000.3); the URP dependency is now `com.unity.render-pipelines.universal`
  14.0.10 or newer. Unity 6 projects keep their own URP 17. Unity 2023.1 and 2023.2 are expected to work but are
  untested.
- **The light buffer skips its passes when a camera sees no lights.** Its depth downsample, clear and composite are
  whole-screen work. In Compatibility Mode the pass waits for the camera's culling and decides from the current frame:
  a camera looking away from every light measured 54-55% less GPU time on Mali phones. With RenderGraph, culling ends
  after the frame is recorded, so the previous frame decides; with every light in the buffer, only where the depth
  input attachment makes the full-resolution stand-in free (Unity 6.6 and newer).
- In Compatibility Mode, **Large Only** decides from the current frame's culling whether large lights go through the
  buffer (RenderGraph uses the previous frame's, as before).
- Cameras rendering into a depth-only texture no longer run the lights' culling or ask URP for its depth texture.
- **Light Intensity 0 skips the render feature** for every camera: no culling, no passes, no depth copy request.
- **Lights that emit nothing** (intensity 0, color alpha 0, or a black color) are skipped by culling at no extra cost
  per light.

## [0.1.0] - 2026-11-24

### Added

- Initial preview release of Fuluma: analytical (fake) volumetric spot lights for URP, mobile first. Point lights, GPU
  culling and OpenGL ES 3.x support are planned for later versions.
- **Fake volumetric spot lights**, drawn by `FulumaRenderFeature` in every URP rendering path (Forward, Forward+,
  Deferred, Deferred+), RenderGraph only.
  - Analytical in-scattering: inverse-square falloff with a smooth range cut-off, an inner/outer cone penumbra, and
    the glow integrated along the view ray inside the cone and up to the scene depth (per-segment Gauss-Legendre
    quadrature).
  - Static lights (placed from code) and dynamic lights (following a `Transform` every frame, synced and copied
    by jobs off the main thread).
- **Components**:
  - `FakeSpotLight` — a standalone fake spot light.
  - `LinkedFakeLight` — mirrors a Unity spot `Light` (color, color temperature, intensity, range, angles), every
    frame or in the Editor only.
  - `FakeLightClipBox` — an oriented box that limits where lights glow.
  - GameObject > Light > Volumetric menu items for all three.
- **`LightStore`** — a `ScriptableObject` holding lights and clip boxes in native memory, with a public API for
  adding, moving, changing and removing them from code (including a batched, Burst-jobified `SetSpotLightPlacements`).
  `LightStore.Default` is used when no store is assigned.
- **Clip boxes** — up to four per light, each Keep (the light exists only inside) or Subtract (removed inside),
  exact per pixel. Dedicated shader variants for the common cases (one box, keep + subtract, two keeps, two
  subtracts) and a general one for three or four boxes.
- **CPU culling** per camera (Burst, multithreaded): frustum culling with an exact cone test, proxy mesh LODs by
  screen size, and a split between lights the camera is inside of and the rest (which keep early-Z).
- **Downsampled light buffer**: lights shaded at a fraction of the camera resolution (0.01–1) and upscaled
  bilinearly or depth-aware; optionally only large lights; optionally shaded before the opaques from URP's depth
  prepass, which keeps URP's render passes merged on tile-based GPUs.
- **Fidelity features**, each trading quality against cost: scene depth, clip boxes, near-plane fade, wide cone
  precision (exact glow near the axis of cones wider than about 160°) and proxy LODs.
- **Render feature settings**: pass event (default After Rendering Skybox, so the sky doesn't cover glow in front of
  it), override store, camera layers, global light intensity, maximum lights per camera, initial GPU buffer capacities
  with memory readouts, and the number of buffers per-frame data rotates through.
- **Scattering settings** on the render feature, each option its own shader variant (the defaults keep the isotropic,
  range-scaled model): the phase function (Isotropic, or Henyey-Greenstein with a global anisotropy), the distance
  falloff (Range Scaled, or URP's own light attenuation, where intensity means what a Unity Light's does) and the lamp
  glare (None, or a point lamp whose glow grows without bound toward it).
- **Shader variant stripping** on Project Settings > Fuluma (`ProjectSettings/FulumaSettings.asset`): Automatic keeps
  the variants the build's Fuluma render features use (plus listed shader variant collections, and optionally inactive
  features), Custom keeps the ticked values per keyword group; excluded combinations apply in both modes. The page
  previews how many variants a build keeps, and each build logs a summary per shader and writes a JSON report to
  `Logs/ShaderStripping/`. The stripping itself comes from the ShaderWorks package
  (`com.saesentsessis.unity-shaderworks`).
- **Editor tooling**: Scene view gizmos and handles for lights (range, inner and outer angles) and clip boxes, a
  combined cone angle slider, an inspector status for every light and box (registered where, and which renderers
  draw its store), and warnings for clip box slots that have no effect.
- User manual in `Documentation~`: a page per component and asset, and a technical deep dive.
- Profiler markers and samplers for the upload, culling and draw passes.
- EditMode test suite, including comparisons of the rendered glow against a brute-force integral.
- Requires Unity 6000.3 or newer; dependencies on `com.unity.render-pipelines.universal` 17.3.0,
  `com.unity.burst` 1.8.25, `com.unity.collections` 2.6.2, `com.unity.mathematics` 1.3.3,
  `com.saesentsessis.unity-collections-specialized` 0.3.0, `com.saesentsessis.unity-meshgen` 0.2.0 and
  `com.saesentsessis.unity-shaderworks` 0.1.1. The GPU must
  support compute shaders and structured buffers in vertex shaders: verified on Direct3D 11 and 12 and on Vulkan
  (Mali-G52, Mali-G77); OpenGL ES 3.x isn't supported yet (Mali's drivers have no vertex-stage structured buffers).

[0.2.0]: https://github.com/Saesentsessis/Fuluma/releases/tag/0.2.0
[0.1.0]: https://github.com/Saesentsessis/Fuluma/releases/tag/0.1.0
