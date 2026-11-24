# Backlog

Known issues and ideas not scheduled for a release yet. Newest first.

## Issues

### OpenGL ES 3.x: light data through constant buffers (after 0.1.0)

- **Found:** 2026-11-23, on both Mali phones (G52, G77; OpenGL ES 3.2): the drivers report no structured buffers for
  vertex shaders (`SystemInfo.maxComputeBufferInputsVertex` 0), so the pass logs "could not be initialized" and draws
  nothing. README Platform Support says Not supported (planned). The user's decision: constant buffers, after 0.1.0.
- **Confirmed at the driver (2026-11-23, 15C, OpenGL ES 3.2 r49p1):** with the check removed, every spot light variant
  fails to link: "GLSL link error: The number of vertex shader storage blocks (5) is greater than the maximum number
  allowed (0)" (3 for variants without clip boxes), 71 failures in a run; the player stalled (2 frames in 12 s) and
  showed a blank screen. Unity reported `supportsComputeShaders` true, `maxComputeBufferInputsVertex` 0 (compute 24),
  shader level 50: the values are the driver's, so the check is right.
- **Today:** only the vertex stage reads buffers. Per instance: `_FulumaVisibleSpotLights` → light index →
  `_FulumaSpotTransforms`, `_FulumaSpotLights` (16 B each), `_FulumaSpotClipBoxReferences` (8 B); clip box variants
  read up to 4 `_FulumaClipBoxes` (48 B each). The fragment stage reads varyings only.
- **Design sketch:** after culling, a Burst job gathers each visible light into a 48 B record (three `uint4`: transform,
  data, box references) in draw order, into a frame ring of constant buffers. Each draw binds a 16 KB window (GLES
  3.0's guaranteed block size: 341 records) with `SetConstantBuffer(…, offset, size)`; regions split into chunks of
  341; window starts aligned to `SystemInfo.constantBufferOffsetAlignment`, the buffer padded so every window is fully
  backed. Clip boxes in a store-wide constant buffer (341 boxes per store on this path; or boxes copied per record:
  240 B, 68 lights per draw).
- **Work:** gather job + chunked draw loop (pass, culler regions, clip-class counts); shader declarations under a
  local keyword (e.g. `_FULUMA_CONSTANT_BUFFERS`) in `FulumaSpotLightInput.hlsl`, `FulumaSpotLightVertex`,
  `FulumaClipBoxVaryings` (`#pragma target 4.5` only exists for structured buffers); mirror without structured
  buffers and dynamic ring on this path (box constant buffer only); path choice instead of the hard error; keyword in
  `FulumaShaderKeywords`; stripping per graphics API (the framework can't decide per API yet); light buffer depth
  downsample uses `GATHER_RED` (GLES 3.1; GLES 3.0 / WebGL 2 need the per-texel fallback); tests: force the path on
  D3D12 and run the render suite + brute-force comparisons on both paths, plus chunking past 341, alignment, box
  limit; phones: render + feature matrix on GLES; docs (Platform Support, box limit, CHANGELOG).
- **Alternative or addition (2026-11-24): data textures.** The mirror's structured buffers become RGBA32UI textures
  (rows of up to 4096 texels; transforms, light data, box references, boxes) read with `texelFetch` in the vertex
  stage (guaranteed in GLES 3.0: 16 vertex texture units); the per-camera visible-light index list becomes a small
  R32UI texture. Keeps today's structure: global mirror, index indirection, the same draws and draw loop, no
  341-record chunks or box limit. Changes: mirror uploads (Unity has no partial `Texture2D` upload: dirty rows through
  a small staging texture and `CopyTexture`, or whole-texture `SetPixelData` + `Apply` for small stores), a texture
  ring for dynamic lights and boxes, fetch functions behind the same keyword. Risk to measure first: upload cost per
  frame (index texture per camera, dynamic rings). Could also be the only non-SSBO path, or sit next to the constant
  buffers. Leaning: less restructuring than the gather + chunked draws.
- **OpenGL ES 3.0 devices (2026-11-24, Galaxy S4 SGH-M919, Adreno 320, Android 10, ARMv7, serial `RV1DB5DTJ8V`):** no
  storage buffers in any stage, no `textureGather` (light buffer depth downsample and depth-aware composite need the
  per-texel fallback), 32-bit only (the profiling build needs ARMv7 next to ARM64). It has `GL_EXT_color_buffer_float`
  and `_half_float`, so the light buffer's B10G11R11 / RGBA16F / R32F targets render; no depth framebuffer fetch (scene
  depth through URP's depth texture copy). Targeting GLES 3.0 also covers WebGL 2. Expect it to be slow (2013 GPU,
  1080p): useful as the GLES 3.0 floor device.
- **To decide with the user:** keyword (testable on desktop) vs `SHADER_API_GLES3` (no variants, phone-only tests);
  GLES 3.1 minimum vs 3.0 / WebGL 2; constant buffers vs data textures (or both); store-wide box buffer vs per-record
  boxes. Size: like the clip-class work.

### Reflection probe bakes ignore the probe's Culling Mask

- **Reported:** 2026-11-23, by the user.
- **Symptom:** baking a Reflection Probe captures the fake lights whatever the probe's **Cubemap Capture Settings >
  Culling Mask** is set to. `FulumaRenderFeature` does not respect the mask.
- **Likely cause (from the code, not verified):** the camera filter (`FulumaRenderFeature.cs`, the
  `camera.cullingMask & cameraLayers` test) only skips a camera whose mask shares no layer with **Camera Layers**. With
  Camera Layers on Everything (the default), any probe mask with at least one layer passes, so the lights are baked.
  Fake lights live on no layer of their own, so there is nothing for the probe's mask to exclude.
- **To decide with the user:** how a probe's mask should select fake lights (per feature through Camera Layers, per
  light or store layer, or a separate setting for reflection cameras), and whether baked and realtime probes behave
  the same.
